using Ah.Core.Json;

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
}

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
        if (target is null || !env.Alive(target.Pid))
        {
            // Report only: every identity heal signals by is keyed on ahd.json, so an unrecorded daemon is the user's to end.
            return env.LifeHeld()
                ? NotHung("life-lock-held", trigger, 0, $"no record; an ahd for {env.Home} still holds run/ahd.life.lock (shutting down or stuck); heal cannot signal an unrecorded daemon; end it by hand: {AhdClient.LifeHint(env.Home)}")
                : NotHung("exited", trigger, 0);
        }

        var verdict = await JudgeAsync(target, trigger).ConfigureAwait(false);
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

        if (await FinalAsync(target, trigger).ConfigureAwait(false) is { } last)
        {
            return last;
        }

        var log = env.CopyLog();

        env.WriteState(state with { Restarts = [.. state.Restarts, now] });
        env.Progress("sending TERM to ahd pid " + target.Pid);
        if (!env.IdentityHolds(target))
        {
            env.WriteState(state);
            return NotHung(env.Alive(target.Pid) ? "identity-changed" : "exited", trigger, 0);
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
                env.Journal(notKilled);
                return Remember(new HealOutcome("failed-not-killed", "ahd pid " + target.Pid + " did not exit on TERM and could not be confirmed as the same process, so KILL was not sent and no new ahd was started", notKilled), log);
            }

            // nothing sits between that check and the signal: no output, no file, no wait
            env.Signal(target.Pid, kill: true);
            signal = "KILL";
            if (!await WaitGoneAsync(target, timing.KillWait).ConfigureAwait(false))
            {
                var failed = new JsonObject { ["kind"] = "ahd-heal-failed", ["stage"] = "unkillable", ["pid"] = target.Pid, ["trigger"] = Name(trigger) };
                env.Journal(failed);
                return Remember(new HealOutcome("failed-unkillable", "ahd pid " + target.Pid + " is still alive after KILL", failed), log);
            }
        }

        var started = await env.StartAsync().ConfigureAwait(false);
        var answered = started is { } p && p != target.Pid && await AnswersWithinAsync(timing.StartVerify).ConfigureAwait(false);
        if (!answered)
        {
            var failed = new JsonObject { ["kind"] = "ahd-heal-failed", ["stage"] = "restart", ["pid"] = target.Pid, ["trigger"] = Name(trigger) };
            env.Journal(failed);
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
        env.Journal(row);
        return Remember(new HealOutcome("restarted", "ahd pid " + target.Pid + " replaced by " + started.Value + " (" + verdict.Verdict + ", " + signal + ")", row), log);
    }

    /// <summary>Keeps what this run concluded in the state file, where the doctor row reads it. The restart list was written before the signal.</summary>
    private HealOutcome Remember(HealOutcome outcome, string log)
    {
        env.WriteState((env.ReadState() ?? HealState.Empty) with { Last = new HealLast(outcome.Kind, env.Now, outcome.Detail, log) });
        return outcome;
    }

    private sealed record Judged(HealOutcome? Outcome, string Verdict, TimeSpan Window, int Probes, int Misses);

    private async Task<Judged> JudgeAsync(HealTarget target, HealTrigger trigger)
    {
        var probes = 0;
        var misses = 0;
        TimeSpan? firstMiss = null;
        var slowest = 0L;
        var graceWaited = false;
        while (true)
        {
            if (!env.IdentityHolds(target))
            {
                return Done(NotHung(env.Alive(target.Pid) ? "identity-changed" : "exited", trigger, slowest));
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
                return Done(NotHung("replied", trigger, slowest));
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
    private async Task<HealOutcome?> FinalAsync(HealTarget target, HealTrigger trigger)
    {
        if (!env.IdentityHolds(target))
        {
            return NotHung(env.Alive(target.Pid) ? "identity-changed" : "exited", trigger, 0);
        }

        var probe = await env.ProbeAsync(timing.ProbeDeadline).ConfigureAwait(false);
        if (!probe.Replied)
        {
            return env.IdentityHolds(target) ? null : NotHung("identity-changed", trigger, 0);
        }

        return NotHung("replied", trigger, probe.LatencyMs);
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

    private HealOutcome NotHung(string reason, HealTrigger trigger, long slowest, string? detail = null)
    {
        var row = new JsonObject { ["kind"] = "ahd-heal-not-hung", ["reason"] = reason, ["slowest_reply_ms"] = slowest, ["trigger"] = Name(trigger) };
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
