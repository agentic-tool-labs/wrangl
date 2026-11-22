using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.DaemonClient;

/// <summary>How a heal run came to start; recorded in the journal row.</summary>
internal enum HealTrigger
{
    Manual,
    DirectWrite,
    Failsafe,
}

/// <summary>One probe of <c>/v1/health</c>: whether a complete reply came inside the deadline, and how long it took.</summary>
internal sealed record HealProbe(bool Replied, long LatencyMs)
{
    internal static HealProbe Miss(long latencyMs) => new(false, latencyMs);
}

/// <summary>The identity of the ahd under judgment: the pid and start recorded in <c>ahd.json</c>, and the process table's start time for that pid when it gives one.</summary>
internal sealed record HealTarget(int Pid, string StartedAt, DateTimeOffset StartedUtc, string? ProcStart = null);

/// <summary>What a heal run concluded.</summary>
internal sealed record HealOutcome(string Kind, string Detail, JsonObject? Row);

/// <summary>Everything the heal touches that is not logic, so a run can be driven on a virtual clock against a fake daemon.</summary>
internal interface IHealEnvironment
{
    DateTimeOffset Now { get; }

    /// <summary>Time since the run began on a clock that only moves forward. Every interval, window and deadline is measured on it: a wall clock that steps cannot shorten one.</summary>
    TimeSpan Elapsed { get; }

    Task DelayAsync(TimeSpan span);

    /// <summary>The ahd named by <c>ahd.json</c>, or null when none is recorded.</summary>
    HealTarget? ReadTarget();

    /// <summary>Whether <paramref name="target"/> is still alive and still this state home's daemon with the same recorded start.</summary>
    bool IdentityHolds(HealTarget target);

    bool Alive(int pid);

    /// <summary>The state home heal was run for.</summary>
    string Home { get; }

    /// <summary>Whether a live daemon holds <c>run/ahd.life.lock</c>; a lock that cannot be tried counts as free.</summary>
    bool LifeHeld();

    /// <summary>
    /// Whether the process heal signalled is certainly gone: its pid is dead, and nothing else. A start time that reads differently
    /// is not proof (where it comes from <c>ps</c> it moves when the wall clock steps), nor is a removed or changed <c>ahd.json</c>
    /// (a daemon drops it while it shuts down and may then never exit), nor a process table that does not answer. A pid reused
    /// during the wait is therefore neither gone nor the same process: the run ends not-killed and starts nothing.
    /// </summary>
    bool Gone(HealTarget target);

    /// <summary>
    /// Whether the pid is certainly still the process heal sent TERM to: alive with the start time it had when the run began. The
    /// record is not asked, since a daemon that handled TERM may have removed it. Where the process table gave no start time at the
    /// beginning this is <see cref="IdentityHolds"/>. Doubt (a process table that does not answer) is false: no KILL on doubt.
    /// </summary>
    bool SameProcess(HealTarget target);

    Task<HealProbe> ProbeAsync(TimeSpan deadline);

    void Signal(int pid, bool kill);

    /// <summary>Copies the last 10 MB of the daemon log; returns the copy's path, or empty when there was no log.</summary>
    string CopyLog();

    /// <summary>Starts the daemon through the one normal start path and returns its pid, or null when it did not start.</summary>
    Task<int?> StartAsync();

    /// <summary>The restart history; empty when there is no file yet, and null when the file is there and cannot be read.</summary>
    HealState? ReadState();

    void WriteState(HealState state);

    void Journal(JsonObject row);

    void Progress(string line);

    /// <summary><c>run/ahd.life.json</c>, written by the daemon that won <c>run/ahd.life.lock</c>; null when absent or not of that shape.</summary>
    LifeRecord? ReadLife();

    /// <summary>The process table's UTC start time of <paramref name="pid"/>; null when it gives none.</summary>
    string? StartOf(int pid);

    /// <summary>Whether <paramref name="pid"/>'s command line is this state home's daemon; a <c>ps</c> that cannot answer is false.</summary>
    bool IsOurDaemon(int pid);

    /// <summary>Whether the user ran this heal, by the same test <c>--force</c> uses; an asker that cannot be told is not the user.</summary>
    bool AskerIsUser();

    int OwnPid { get; }

    /// <summary>The <c>ahd.log</c> line written before TERM goes to a daemon found by the life lock alone.</summary>
    void LogLifeKill(HealTarget target, string reason);
}

/// <summary>What <c>run/ahd.life.json</c> names: the lock winner's pid, its process-table start, and the state home it serves.</summary>
internal sealed record LifeRecord(int Pid, string Start, string Home);

/// <summary>The restart history the backoff and the cap are judged from, and what the last run concluded (the doctor row reads it).</summary>
internal sealed record HealState(IReadOnlyList<DateTimeOffset> Restarts, DateTimeOffset? LastCappedRow, HealLast? Last = null)
{
    internal static HealState Empty { get; } = new([], null);
}

/// <summary>The last run that was more than "not hung": its kind (<c>restarted</c>, <c>capped</c>, <c>failed-unkillable</c>, <c>failed-not-killed</c>, <c>failed-restart</c>), when, and its one-line detail.</summary>
internal sealed record HealLast(string Kind, DateTimeOffset At, string Detail, string LogCopy);

/// <summary>
/// The verdict that an ahd is hung, and the restart that follows only when it is (handoff spec ahd-heal, crash-resume §14.11). The
/// verdict is positive and measured here: no reply at all for a whole window (H1). A reply, however slow, is an answer and ends the
/// run without a signal. The stuck-writer verdict (H2) is not built: ahd's write transactions do not begin through one seam.
/// </summary>
internal sealed class AhdHeal(IHealEnvironment env, HealTiming timing)
{
    internal async Task<HealOutcome> RunAsync(HealTrigger trigger, bool force)
    {
        var target = env.ReadTarget();
        Func<string?> lost;
        Func<string, HealOutcome> refuse;
        var life = false;
        if (target is null || !env.Alive(target.Pid))
        {
            life = true;
            if (!env.LifeHeld())
            {
                return NotHung("exited", trigger, 0);
            }

            // No record names the holder, so run/ahd.life.json nominates it and the live process must prove it (spec
            // heal-recordless-holder): only a heal the user ran, and only a holder old enough to be past its first start.
            if (trigger != HealTrigger.Manual || !env.AskerIsUser())
            {
                return LifeRefused("not-user", trigger);
            }

            var record = env.ReadLife();
            var holder = record is null ? null : new HealTarget(record.Pid, record.Start, ProcessStart.TryParseUtc(record.Start, out var at) ? at : DateTimeOffset.MaxValue, record.Start);
            if (LifeWhy(record) is { } why)
            {
                return LifeRefused(why, trigger);
            }

            if (env.Now - holder!.StartedUtc < timing.RecordlessGrace)
            {
                return LifeRefused("young", trigger);
            }

            target = holder;
            lost = () => env.LifeHeld() ? LifeWhy(env.ReadLife(), target) : "exited";
            refuse = why => why == "exited" ? NotHung("exited", trigger, 0) : LifeRefused(why, trigger);
        }
        else
        {
            var recorded = target;
            lost = () => env.IdentityHolds(recorded) ? null : env.Alive(recorded.Pid) ? "identity-changed" : "exited";
            refuse = reason => NotHung(reason, trigger, 0);
        }

        var verdict = await JudgeAsync(target, trigger, life, lost, refuse).ConfigureAwait(false);
        if (verdict.Outcome is not null)
        {
            return verdict.Outcome;
        }

        var now = env.Now;
        var read = env.ReadState();
        if (read is null && !force)
        {
            // An unreadable history could hide any number of restarts: it counts as capped, and the file is left for the user to
            // see. Only --force, the user's, starts a fresh one.
            var unreadable = new JsonObject { ["kind"] = "ahd-heal-capped", ["reason"] = "state-unreadable", ["trigger"] = Name(trigger) };
            env.Journal(unreadable);
            return new HealOutcome("capped", "ahd is hung but run/heal-state.json cannot be read, so the restart cap cannot be judged; no signal was sent", unreadable);
        }

        var state = Prune(read ?? HealState.Empty, now);
        if (!force && CapRefusal(state, now) is { } refused)
        {
            return refused;
        }

        if (await FinalAsync(target, trigger, life, lost, refuse).ConfigureAwait(false) is { } last)
        {
            return last;
        }

        var log = env.CopyLog();

        env.WriteState(state with { Restarts = [.. state.Restarts, now] });
        env.Progress("sending TERM to ahd pid " + target.Pid);
        if (lost() is { } gone)
        {
            env.WriteState(state);
            return refuse(gone);
        }

        if (life)
        {
            env.LogLifeKill(target, Name(trigger));
        }

        env.Signal(target.Pid, kill: false);
        var signal = "TERM";
        if (!await WaitGoneAsync(target, timing.TermWait).ConfigureAwait(false))
        {
            // The hang is proven and TERM was sent to the proven process, so the record no longer decides (a daemon that handled
            // TERM may have dropped it): KILL goes to the pid only while it is certainly still that process. On doubt nothing is
            // sent and nothing is started on top of a process that may still hold the database.
            env.Progress("ahd ignored TERM");
            if (!env.SameProcess(target))
            {
                var notKilled = new JsonObject { ["kind"] = "ahd-heal-failed", ["stage"] = "not-killed", ["pid"] = target.Pid, ["trigger"] = Name(trigger) };
                env.Journal(Tag(notKilled, life, target, signal));
                return Remember(new HealOutcome("failed-not-killed", "ahd pid " + target.Pid + " did not exit on TERM and could not be confirmed as the same process, so KILL was not sent and no new ahd was started", notKilled), log);
            }

            // nothing sits between that check and the signal: no output, no file, no wait
            env.Signal(target.Pid, kill: true);
            signal = "KILL";
            if (!await WaitGoneAsync(target, timing.KillWait).ConfigureAwait(false))
            {
                var failed = new JsonObject { ["kind"] = "ahd-heal-failed", ["stage"] = "unkillable", ["pid"] = target.Pid, ["trigger"] = Name(trigger) };
                env.Journal(Tag(failed, life, target, signal));
                return Remember(new HealOutcome("failed-unkillable", "ahd pid " + target.Pid + " is still alive after KILL", failed), log);
            }
        }

        var started = await env.StartAsync().ConfigureAwait(false);
        var answered = started is { } p && p != target.Pid && await AnswersWithinAsync(timing.StartVerify).ConfigureAwait(false);
        if (!answered)
        {
            var failed = new JsonObject { ["kind"] = "ahd-heal-failed", ["stage"] = "restart", ["pid"] = target.Pid, ["trigger"] = Name(trigger) };
            env.Journal(Tag(failed, life, target, signal));
            return Remember(new HealOutcome("failed-restart", "the restarted ahd did not answer", failed), log);
        }

        var row = new JsonObject
        {
            ["kind"] = "ahd-hung-restart",
            ["verdict"] = verdict.Verdict,
            ["pid"] = target.Pid,
            ["pid_start"] = target.StartedAt,
            ["window_s"] = (long)verdict.Window.TotalSeconds,
            ["probes"] = verdict.Probes,
            ["misses"] = verdict.Misses,
            ["signal"] = signal,
            ["log_copy"] = log,
            ["new_pid"] = started!.Value,
            ["trigger"] = Name(trigger),
        };
        env.Journal(Tag(row, life, target, signal));
        return Remember(new HealOutcome("restarted", "ahd pid " + target.Pid + " replaced by " + started.Value + " (" + verdict.Verdict + ", " + signal + ")", row), log);
    }

    /// <summary>Keeps what this run concluded in the state file, where the doctor row reads it. The restart list was written before the signal.</summary>
    private HealOutcome Remember(HealOutcome outcome, string log)
    {
        env.WriteState((env.ReadState() ?? HealState.Empty) with { Last = new HealLast(outcome.Kind, env.Now, outcome.Detail, log) });
        return outcome;
    }

    private sealed record Judged(HealOutcome? Outcome, string Verdict, TimeSpan Window, int Probes, int Misses);

    private async Task<Judged> JudgeAsync(HealTarget target, HealTrigger trigger, bool life, Func<string?> lost, Func<string, HealOutcome> refuse)
    {
        var probes = 0;
        var misses = 0;
        TimeSpan? firstMiss = null;
        var slowest = 0L;
        var graceWaited = false;
        while (true)
        {
            if (lost() is { } gone)
            {
                return Done(refuse(gone));
            }

            // One wait, to the end of the grace. A start recorded in the future (the clock stepped back since) gives no uptime to
            // trust: the whole grace is waited then, so the daemon has run at least that long before any probe counts. This is the
            // one place the wall clock is read for a duration, and it can only add waiting: the floor below never reads it.
            var uptime = env.Now - target.StartedUtc;
            if (uptime < timing.Grace && !graceWaited)
            {
                graceWaited = true;
                await env.DelayAsync(uptime < TimeSpan.Zero ? timing.Grace : timing.Grace - uptime).ConfigureAwait(false);
                continue;
            }

            var started = env.Elapsed;
            var probe = await env.ProbeAsync(timing.ProbeDeadline).ConfigureAwait(false);
            probes++;
            if (probe.Replied)
            {
                slowest = Math.Max(slowest, probe.LatencyMs);
                env.Progress($"probe {probes}: reply in {probe.LatencyMs} ms");
                return Done(life ? LifeRefused("answering", trigger, slowest) : NotHung("replied", trigger, slowest));
            }

            misses++;
            firstMiss ??= started;
            env.Progress($"probe {probes}: no reply within {(long)timing.ProbeDeadline.TotalSeconds} s ({misses} in a row)");
            var span = env.Elapsed - firstMiss.Value;
            // The floor does not rest on any recorded time: whatever the record or the process table say of the daemon's uptime, this
            // run has itself watched it for the whole grace and window, on its own monotonic clock, before the verdict can stand.
            if (misses >= timing.MissesNeeded && span >= timing.Window && env.Elapsed >= timing.Grace + timing.Window)
            {
                return new Judged(null, "H1", span, probes, misses);
            }

            var wait = started + timing.ProbeEvery - env.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await env.DelayAsync(wait).ConfigureAwait(false);
            }
        }
    }

    private static Judged Done(HealOutcome outcome) => new(outcome, string.Empty, TimeSpan.Zero, 0, 0);

    /// <summary>The last look before a signal: the same identity and one more probe. Null means the verdict still holds.</summary>
    private async Task<HealOutcome?> FinalAsync(HealTarget target, HealTrigger trigger, bool life, Func<string?> lost, Func<string, HealOutcome> refuse)
    {
        if (lost() is { } gone)
        {
            return refuse(gone);
        }

        var probe = await env.ProbeAsync(timing.ProbeDeadline).ConfigureAwait(false);
        if (!probe.Replied)
        {
            if (life)
            {
                return lost() is { } after ? refuse(after) : null;
            }

            return env.IdentityHolds(target) ? null : NotHung("identity-changed", trigger, 0);
        }

        return life ? LifeRefused("answering", trigger, probe.LatencyMs) : NotHung("replied", trigger, probe.LatencyMs);
    }

    /// <summary>
    /// Rules 3 to 5 of the recordless identification: the sidecar names this home and a plausible pid, that pid is alive with the
    /// recorded process-table start, and its command line is this home's daemon. During a run the sidecar must still name the same
    /// holder. Null when all hold; otherwise the rule that failed, for the journal's <c>why</c>.
    /// </summary>
    private string? LifeWhy(LifeRecord? record, HealTarget? holder = null)
    {
        if (record is null || record.Pid <= 1 || record.Pid == env.OwnPid || record.Start.Length == 0)
        {
            return "no-sidecar";
        }

        if (!string.Equals(record.Home, env.Home, StringComparison.Ordinal))
        {
            return "home-mismatch";
        }

        if ((holder is not null && (record.Pid != holder.Pid || record.Start != holder.StartedAt))
            || !env.Alive(record.Pid)
            || ProcessStart.Compare(env.StartOf(record.Pid), static () => null, record.Start) != StartMatch.Same)
        {
            return "start-mismatch";
        }

        return env.IsOurDaemon(record.Pid) ? null : "not-our-daemon";
    }

    /// <summary>A recordless holder heal did not signal: journalled as <c>life-lock-held</c> with the rule that stopped it, and the by-hand hint printed.</summary>
    private HealOutcome LifeRefused(string why, HealTrigger trigger, long slowest = 0)
    {
        return NotHung("life-lock-held", trigger, slowest, $"no record; an ahd for {env.Home} still holds run/ahd.life.lock (shutting down or stuck); heal did not signal it ({why}); end it by hand: {AhdClient.LifeHint(env.Home)}", why);
    }

    /// <summary>The recordless path's rows carry how the target was identified, its process-table start and the last signal sent.</summary>
    private static JsonObject Tag(JsonObject row, bool life, HealTarget target, string signal)
    {
        if (life)
        {
            row["identity"] = "life-lock";
            row["start"] = target.StartedAt;
            row["signal"] = signal;
        }

        return row;
    }

    private async Task<bool> WaitGoneAsync(HealTarget target, TimeSpan wait)
    {
        var until = env.Elapsed + wait;
        while (!env.Gone(target))
        {
            if (env.Elapsed >= until)
            {
                return false;
            }

            await env.DelayAsync(timing.Poll).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> AnswersWithinAsync(TimeSpan wait)
    {
        var until = env.Elapsed + wait;
        while (true)
        {
            if ((await env.ProbeAsync(timing.ProbeDeadline).ConfigureAwait(false)).Replied)
            {
                return true;
            }

            if (env.Elapsed >= until)
            {
                return false;
            }

            await env.DelayAsync(timing.Poll).ConfigureAwait(false);
        }
    }

    private HealState Prune(HealState state, DateTimeOffset now) =>
        state with { Restarts = [.. state.Restarts.Where(t => now - t < timing.StateKeep)] };

    /// <summary>The refusal when the cap or the backoff forbids another restart now, journalled as <see cref="Capped"/> does; null when one is allowed.</summary>
    private HealOutcome? CapRefusal(HealState state, DateTimeOffset now)
    {
        var n = state.Restarts.Count;
        var next = n == 0 ? now : state.Restarts[^1] + (timing.BackoffBase * Math.Pow(2, n - 1));
        return n >= timing.MaxRestarts || now < next ? Capped(state, next, now) : null;
    }

    private HealOutcome Capped(HealState state, DateTimeOffset next, DateTimeOffset now)
    {
        var row = new JsonObject { ["kind"] = "ahd-heal-capped", ["restarts_24h"] = state.Restarts.Count, ["next_allowed_at"] = next.ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
        if (state.LastCappedRow is not { } last || now - last >= timing.CappedRowEvery)
        {
            env.Journal(row);
            state = state with { LastCappedRow = now };
        }

        var detail = $"ahd is hung but {state.Restarts.Count} restarts happened in 24 h or the backoff has not passed; next allowed {row["next_allowed_at"].AsString()}";
        env.WriteState(state with { Last = new HealLast("capped", now, detail, string.Empty) });
        return new HealOutcome("capped", detail, row);
    }

    private HealOutcome NotHung(string reason, HealTrigger trigger, long slowest, string? detail = null, string? why = null)
    {
        var row = new JsonObject { ["kind"] = "ahd-heal-not-hung", ["reason"] = reason, ["slowest_reply_ms"] = slowest, ["trigger"] = Name(trigger) };
        if (why is not null)
        {
            row["why"] = why;
        }

        env.Journal(row);
        // an ahd that answers is no longer the one an earlier run failed on or was capped for: the doctor row stops saying so
        if (reason == "replied" && env.ReadState() is { Last.Kind: not "restarted" } state)
        {
            env.WriteState(state with { Last = null });
        }

        return new HealOutcome("not-hung", detail ?? reason, row);
    }

    private static string Name(HealTrigger trigger) => trigger switch
    {
        HealTrigger.DirectWrite => "direct-write",
        HealTrigger.Failsafe => "failsafe",
        _ => "manual",
    };
}

/// <summary>The times the verdict and the restart run on; tests shrink them, production uses <see cref="Production"/>.</summary>
internal sealed record HealTiming(
    TimeSpan Grace,
    TimeSpan ProbeDeadline,
    TimeSpan ProbeEvery,
    TimeSpan Window,
    int MissesNeeded,
    TimeSpan TermWait,
    TimeSpan KillWait,
    TimeSpan StartVerify,
    TimeSpan Poll,
    TimeSpan BackoffBase,
    int MaxRestarts,
    TimeSpan StateKeep,
    TimeSpan CappedRowEvery)
{
    /// <summary>How old, from its process-table start, a daemon found by the life lock alone must be before heal judges it: a healthy one may still be migrating before its first <c>ahd.json</c>.</summary>
    internal TimeSpan RecordlessGrace { get; init; } = TimeSpan.FromMinutes(5);

    internal static HealTiming Production { get; } = new(
        Grace: TimeSpan.FromSeconds(30),
        ProbeDeadline: TimeSpan.FromSeconds(10),
        ProbeEvery: TimeSpan.FromSeconds(15),
        Window: TimeSpan.FromSeconds(60),
        MissesNeeded: 4,
        TermWait: TimeSpan.FromSeconds(10),
        KillWait: TimeSpan.FromSeconds(5),
        StartVerify: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(250),
        BackoffBase: TimeSpan.FromMinutes(10),
        MaxRestarts: 3,
        StateKeep: TimeSpan.FromHours(24),
        CappedRowEvery: TimeSpan.FromHours(1));
}
