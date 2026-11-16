using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Launch;
using Ah.Roster;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>stop --now [--team T | --all] [--confirm] [--grace SECONDS] [--reason TEXT]</c>: the emergency stop at the user's order. The team
/// goes on hold, every member is told to stop all its work, and the background jobs a member registered with a verified pid are
/// stopped. No member session is killed. Without <c>--confirm</c> it only prints what it would do. Nothing here needs the daemon.
/// </summary>
internal static class StopVerb
{
    private const int DefaultGrace = 20;

    private const int MaxGrace = 120;

    private const int MaxReason = 160;

    private const int SigTerm = 15;

    private const int SigKill = 9;

    private static readonly string[] Flags = ["cwd", "team", "all", "now", "confirm", "grace", "reason", "orchestrator-pid"];

    private static readonly string[] GitOps = ["rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD"];

    private sealed record Target(string? Team, string Name, JsonValue Row, string Via);

    private sealed record Job(string? Team, string Owner, string Id, string Label, int Pid, List<int> Kill, JobRecordFile Found, JsonObject Record)
    {
        internal bool Background => Record.Get("background") is { IsTruthy: true };
    }

    private sealed record JobScan(List<Job> Stop, JsonArray Unverified, JsonArray Excluded);

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"stop: unrecognized flag --{key} (use --now, --team, --all, --confirm, --grace, --reason)");
            }
        }

        if (c.Opts.Text("now") is { } word)
        {
            throw HoldStore.Refuse(c, "bad_flags", $"--now takes no value, got {word}");
        }

        if (!c.Opts.IsTrue("now"))
        {
            throw HoldStore.Refuse(c, "stop_needs_now", "stop needs --now: it is an emergency stop and is never a default");
        }

        if (c.Opts.IsTrue("all") && c.Opts.Has("team"))
        {
            throw HoldStore.Refuse(c, "bad_flags", "--all and --team exclude each other");
        }

        var grace = Grace(c);
        var reason = Reason(c);
        var roster = c.Services.Roster;
        var tops = Tops(c);
        var coveredBy = tops.ToDictionary(t => t ?? string.Empty, t => roster.HoldCovered(c.Dir, t), StringComparer.Ordinal);
        var covered = tops.SelectMany(t => coveredBy[t ?? string.Empty]).Distinct().ToList();
        var panes = PaneMembers.Panes(c, "stop");
        var table = c.Services.Callers?.ProcessTable ?? throw new RosterFailure("stop: this composition has no process table");
        var targets = Targets(c, covered);
        var protect = Protected(c, table, covered);
        var scan = ScanJobs(c, covered, table, protect);

        if (tops.All(t => HoldStore.ReadOwn(c, t) is { } h && HoldStore.Field(h, "kind") == "stop"))
        {
            return PrintAlready(c, tops);
        }

        if (!c.Opts.IsTrue("confirm"))
        {
            c.Out(Planned(tops, covered, targets, scan));
            return 0;
        }

        var now = c.Services.Clock.UtcNow;
        var since = Timestamps.ToIsoString(now);
        var stamp = RosterModule.HoldStamp(now);
        var holds = WriteHolds(c, tops, coveredBy, since, reason);
        var drained = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var top in tops)
        {
            var held = new EffectiveHold(top, null, HoldStore.PathOf(c, top), "stop", "stopped", since, holds[top ?? string.Empty], null);
            drained[top ?? string.Empty] = QuitVerb.DrainAll(c, coveredBy[top ?? string.Empty], held).Totals;
        }

        var signalled = new List<JsonObject>();
        foreach (var target in targets)
        {
            signalled.Add(Signal(c, panes, target, stamp, reason));
        }

        if (grace > 0)
        {
            panes.Sleeper.Sleep(TimeSpan.FromSeconds(grace));
        }

        protect = Protected(c, table, covered);
        scan = ScanJobs(c, covered, table, protect);
        var (stopped, survivors) = StopJobs(c, panes, table, scan.Stop);
        var running = StillRunning(c, table, targets);

        var results = new List<JsonObject>();
        var failed = survivors.Count > 0 || signalled.Any(s => s.Get("ok") is { IsBool: true } ok && !ok.AsBool());
        foreach (var top in tops)
        {
            var teams = coveredBy[top ?? string.Empty];
            var evidence = WriteEvidence(c, top, teams, stamp, since, reason, signalled, stopped, scan, survivors, running, drained[top ?? string.Empty]);
            var checkpoints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var team in teams)
            {
                var taken = CheckpointVerb.WithTeam(c, team, () => CheckpointVerb.Take(c, team, "emergency stop", stop: evidence));
                checkpoints[team ?? string.Empty] = MemberPlanning.JsConvertText(taken.Get("record"));
            }

            foreach (var team in teams)
            {
                var doc = holds[team ?? string.Empty];
                doc["evidence"] = evidence;
                doc["checkpoint"] = checkpoints[team ?? string.Empty];
                _ = HoldStore.Change(c, team, _ => doc);
            }

            results.Add(Result(c, top, teams, since, evidence, checkpoints[top ?? string.Empty], signalled, stopped, scan, survivors, running, drained[top ?? string.Empty]));
        }

        c.Out(results.Count == 1 ? results[0] : new JsonObject { ["verb"] = "stop", ["stops"] = Arr(results.Select(r => (JsonValue)r)) });
        return failed ? 4 : 0;
    }

    private static int Grace(RosterContext c)
    {
        if (!c.Opts.Has("grace"))
        {
            return DefaultGrace;
        }

        var text = c.Opts.Text("grace");
        if (text is null || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > MaxGrace)
        {
            throw HoldStore.Refuse(c, "bad_grace", $"--grace must be a whole number of seconds from 0 to {MaxGrace}");
        }

        return seconds;
    }

    /// <summary>The reason as one line of at most <see cref="MaxReason"/> characters, or null when none was given.</summary>
    private static string? Reason(RosterContext c)
    {
        var text = c.Opts.Text("reason");
        if (text is null)
        {
            return null;
        }

        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0)
        {
            return null;
        }

        return line.Length > MaxReason ? line[..MaxReason] : line;
    }

    /// <summary>The teams to stop: the named one, or with <c>--all</c> each team the caller owns that has no parent.</summary>
    private static List<string?> Tops(RosterContext c)
    {
        var roster = c.Services.Roster;
        if (!c.Opts.IsTrue("all"))
        {
            if (roster.ReadTeamRecord(c.Dir, c.TeamFile).IsNull)
            {
                throw new RosterFailure($"stop: there is no team {(c.TeamFile is null ? "(the default team)" : $"\"{c.TeamFile}\"")} to stop");
            }

            return [c.TeamFile];
        }

        var invoker = c.Invoker();
        var owned = new List<string?>();
        var candidates = new List<string?> { null };
        candidates.AddRange(roster.ListTeamNames(c.Dir));
        foreach (var team in candidates)
        {
            var record = roster.ReadTeamRecord(c.Dir, team);
            if (!record.IsNull && record.Member("parent") is not { IsMap: true } && (c.UserActs || roster.IsTeamOwnedBy(record, invoker)))
            {
                owned.Add(team);
            }
        }

        return owned.Count > 0 ? owned : throw new RosterFailure("stop --all: this session owns no team");
    }

    // ---------------------------------------------------------------- holds

    private static Dictionary<string, JsonObject> WriteHolds(RosterContext c, List<string?> tops, Dictionary<string, IReadOnlyList<string?>> coveredBy, string since, string? reason)
    {
        var written = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var top in tops)
        {
            foreach (var team in new[] { top }.Concat(coveredBy[top ?? string.Empty].Where(t => t != top)))
            {
                var doc = HoldStore.Change(c, team, previous => new JsonObject
                {
                    ["v"] = 1,
                    ["team"] = team is null ? JsonValue.Null : team,
                    ["kind"] = "stop",
                    ["phase"] = "stopped",
                    ["since"] = since,
                    ["sealed_at"] = since,
                    ["by"] = HoldStore.By(c, team),
                    ["note"] = JsonValue.Null,
                    ["reason"] = reason is null ? JsonValue.Null : reason,
                    ["checkpoint"] = JsonValue.Null,
                    ["plan"] = JsonValue.Null,
                    ["evidence"] = JsonValue.Null,
                    ["assessment"] = JsonValue.Null,
                    ["cascade_from"] = team == top ? JsonValue.Null : top is null ? "_default" : top,
                    ["previous"] = previous is null ? JsonValue.Null : previous,
                })!;
                written[team ?? string.Empty] = doc;
            }
        }

        return written;
    }

    private static int PrintAlready(RosterContext c, List<string?> tops)
    {
        var holds = new JsonArray();
        foreach (var top in tops)
        {
            var own = HoldStore.ReadOwn(c, top)!;
            holds.Add(new JsonObject
            {
                ["team"] = top is null ? JsonValue.Null : top,
                ["hold"] = HoldStore.PathOf(c, top),
                ["since"] = HoldStore.Field(own, "since") is { } s ? s : JsonValue.Null,
                ["evidence"] = HoldStore.Field(own, "evidence") is { } e ? e : JsonValue.Null,
                ["checkpoint"] = HoldStore.Field(own, "checkpoint") is { } k ? k : JsonValue.Null,
            });
        }

        c.Out(new JsonObject { ["verb"] = "stop", ["state"] = "stop", ["phase"] = "stopped", ["already"] = true, ["holds"] = holds });
        return 0;
    }

    // ---------------------------------------------------------------- signalling members

    private static List<Target> Targets(RosterContext c, List<string?> covered)
    {
        var panes = PaneMembers.Panes(c, "stop");
        var found = new List<Target>();
        foreach (var team in covered)
        {
            var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
            if (record.IsNull || record.Member("members") is not { IsArray: true } rows)
            {
                continue;
            }

            var transport = record.Member("transport") is { IsText: true } t ? t.AsString() : null;
            foreach (var row in rows.AsArray())
            {
                if (MemberPlanning.Text(row.Member("name")) is not { Length: > 0 } name || !ReleaseGate.IsLive(c, c.Dir, row))
                {
                    continue;
                }

                var codex = MemberPlanning.JsConvertText(c.Services.Config.ResolveKind(row)) == "codex";
                var pasteable = panes.Paste.PlacementOf(row, transport).Get("pasteable") is { IsTruthy: true };
                found.Add(new Target(team, name, row, !codex && pasteable ? "pane" : "mail"));
            }
        }

        return found;
    }

    private static string StopText(string? team, string? reason) =>
        $"EMERGENCY STOP for team {team ?? "default"} at the user's order. Reason: {reason ?? "none"}. Stop now; do not finish the current step. " +
        "Stop every background task, Monitor and subagent you started (TaskStop each one). Stop every process and script you launched (builds, test servers, watchers, scripts). " +
        "Do NOT commit, push, stash, reset, clean or edit anything; leave your work tree exactly as it is. Send no mail. Then hold: do nothing until a resume brief arrives.";

    private static JsonObject Signal(RosterContext c, IRosterPanes panes, Target target, string stamp, string? reason)
    {
        var text = StopText(target.Team, reason);
        var entry = new JsonObject { ["team"] = target.Team is null ? JsonValue.Null : target.Team, ["member"] = target.Name };
        string? paneError = null;
        if (target.Via == "pane")
        {
            paneError = ViaPane(c, panes, target, text);
            if (paneError is null)
            {
                entry["via"] = "pane";
                entry["ok"] = true;
                return entry;
            }
        }

        var mailed = ViaMail(c, target, stamp, text);
        entry["via"] = target.Via == "pane" ? "mail" : "mail-pending";
        entry["ok"] = mailed is null;
        if (paneError is not null and not PaneMailed)
        {
            entry["pane_error"] = paneError;
        }

        if (mailed is not null)
        {
            entry["error"] = mailed;
        }

        return entry;
    }

    /// <summary>Esc to interrupt the turn, a second to let the harness settle, then the stop text as a prompt; null when it was typed and confirmed, else why not.</summary>
    private static string? ViaPane(RosterContext c, IRosterPanes panes, Target target, string text)
    {
        var record = c.Services.Roster.ReadTeamRecord(c.Dir, target.Team);
        var transport = record.Member("transport") is { IsText: true } t ? t.AsString() : null;
        var place = panes.Paste.PlacementOf(target.Row, transport);
        var pane = place.Get("pane") is { IsText: true } p && p.AsString().Length > 0 ? p.AsString() : null;
        if (pane is null)
        {
            return "the member has no pane to press a key in";
        }

        try
        {
            if (place.Get("transport") is { IsText: true } kind && kind.AsString() == "tmux")
            {
                var sent = panes.Mux.Run("tmux", ["send-keys", "-t", pane, "Escape"], 15000);
                if (!sent.Ok)
                {
                    return "tmux send-keys failed";
                }
            }
            else
            {
                AnswerVerb.PressKeys(c, target.Name, ["esc"]);
            }
        }
        catch (RosterFailure e)
        {
            return e.Message;
        }

        var clock = c.Services.Clock;
        var escAt = clock.UtcNow;
        var deadline = escAt + TimeSpan.FromSeconds(SettleSeconds);
        JsonObject pasted;
        var tries = 0;
        while (true)
        {
            var left = deadline - clock.UtcNow;
            panes.Sleeper.Sleep(left < TimeSpan.FromSeconds(1) ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : TimeSpan.FromSeconds(1));
            tries++;
            var lockWait = deadline - clock.UtcNow;
            var options = new PasteOptions(
                target.Row, target.Team ?? "_default", transport, text, Source: "stop", MaxChars: 800,
                LockWait: lockWait > TimeSpan.Zero ? lockWait : TimeSpan.Zero);
            pasted = panes.Paste.Paste(options);
            var settling = pasted.Get("outcome") is { IsText: true } o && o.AsString() == "refused"
                && pasted.Get("reason") is { IsText: true } r && r.AsString() is "busy" or "indeterminate";
            if (!settling || clock.UtcNow >= deadline)
            {
                break;
            }
        }

        var waited = (int)Math.Round((clock.UtcNow - escAt).TotalSeconds);

        if (pasted.Get("outcome") is { IsText: true } outcome && outcome.AsString() == "confirmed")
        {
            return null;
        }

        // A pane herdr gives no status is not typed into: nothing was pasted, and the mail that follows carries the stop.
        if (pasted.Get("outcome") is { IsText: true } mailedOutcome && mailedOutcome.AsString() == "mailed")
        {
            return PaneMailed;
        }

        if (pasted.Get("reason") is not { IsTruthy: true } why)
        {
            return "the paste was not confirmed";
        }

        var reasonText = MemberPlanning.JsConvertText(why);
        if (pasted.Get("outcome") is { IsText: true } refused && refused.AsString() == "refused")
        {
            if (pasted.Get("why") is { IsTruthy: true } detail)
            {
                reasonText += ": " + MemberPlanning.JsConvertText(detail);
            }

            if (tries > 1)
            {
                reasonText += $" (after {waited} s)";
            }
        }

        return reasonText;
    }

    private const int SettleSeconds = 8;

    private const string PaneMailed = "mailed";

    /// <summary>One hold-exempt brief carrying the stop text, straight to the mailbox files; null when it was written, else why not.</summary>
    private static string? ViaMail(RosterContext c, Target target, string stamp, string text)
    {
        try
        {
            var made = c.Services.Hierarchy.Messages.CreateMessage(c.Dir, new JsonObject
            {
                ["type"] = "request",
                ["to"] = MemberPlanning.JsConvertText(target.Row.Member("role")),
                ["from"] = "orchestrator",
                ["slug"] = "emergency-stop",
                ["title"] = "EMERGENCY STOP: hold",
                ["cwd"] = c.Cwd,
                ["toName"] = target.Name,
                ["fromName"] = JsonValue.Null,
                ["team"] = target.Team is null ? JsonValue.Null : target.Team,
                ["markerName"] = JsonValue.Null,
                ["paneName"] = JsonValue.Null,
                ["reqPath"] = JsonValue.Null,
                ["parent"] = JsonValue.Null,
                ["reason"] = JsonValue.Null,
                ["eta"] = JsonValue.Null,
                ["body"] = text,
            });
            using var exempt = Ah.Mesh.MeshModule.BeginHoldExempt();
            _ = CheckpointVerb.WithTeam(c, target.Team, () => c.Services.SendBrief(c.Cwd, target.Name, WorkTreeAssignment.TeamOf(c), MemberPlanning.JsConvertText(made.Member("path")))
                ?? throw new InvalidOperationException("the mail layer is not composed"));
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or Ah.Core.Json.StoreUnavailableException)
        {
            // a message store that cannot answer the brief's checks leaves this target unsignalled; the stop goes on to the rest
            return e.Message;
        }
    }

    // ---------------------------------------------------------------- background jobs

    /// <summary>Every pid a stop must never signal: this process and the caller with their ancestors, and every live registration with its ancestors.</summary>
    private static HashSet<int> Protected(RosterContext c, IProcessTable table, List<string?> covered)
    {
        var keep = new HashSet<int>();
        void Add(int pid)
        {
            if (pid <= 0)
            {
                return;
            }

            keep.Add(pid);
            foreach (var up in table.Ancestors(pid) ?? [])
            {
                keep.Add(up);
            }
        }

        Add(Environment.ProcessId);
        Add(c.Proved().Pid);
        foreach (var pid in RegistrationPids(c).Values.Concat(RecordedPids(c, covered)))
        {
            Add(pid);
        }

        return keep;
    }

    /// <summary>The orchestrator and member pids the covered teams' records name, which are protected even when no live registration holds them.</summary>
    private static List<int> RecordedPids(RosterContext c, List<string?> covered)
    {
        var found = new List<int>();
        foreach (var team in covered)
        {
            var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
            if (record.IsNull)
            {
                continue;
            }

            if (record.Member("orchestrator")?.Member("pid") is { IsNumber: true } orchestrator)
            {
                found.Add((int)orchestrator.AsNumber());
            }

            if (record.Member("members") is { IsArray: true } rows)
            {
                found.AddRange(rows.AsArray().Select(r => r.Member("pid")).OfType<JsonValue>().Where(p => p.IsNumber).Select(p => (int)p.AsNumber()));
            }
        }

        return found;
    }

    /// <summary>The pid of every live registration, by address.</summary>
    private static Dictionary<string, int> RegistrationPids(RosterContext c)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        if (c.Services.Mailbox is not { } mailbox)
        {
            return found;
        }

        foreach (var registration in mailbox.LiveRegistrations())
        {
            if (registration.Get("pid") is { IsNumber: true } pid && registration.Get("address") is { IsText: true } address)
            {
                found[address.AsString() + "|" + (registration.Get("team_file") is { IsText: true } tf ? tf.AsString() : string.Empty)] = (int)pid.AsNumber();
            }
        }

        return found;
    }

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;

    private static JobScan ScanJobs(RosterContext c, List<string?> covered, IProcessTable table, HashSet<int> protect)
    {
        var files = c.Services.Files;
        var registered = RegistrationPids(c).Values.Concat(RecordedPids(c, covered)).ToHashSet();
        var scan = new JobScan([], [], []);
        foreach (var team in covered)
        {
            var dir = WatchModule.JobsDir(c.MainDir ?? c.Dir, team);
            foreach (var found in WatchModule.JobRecordFiles(c.Services.Hierarchy.Status, dir))
            {
                var record = found.Record;
                if (record.Get("ended_at") is { IsText: true })
                {
                    continue;
                }

                var id = Text(record, "id") ?? found.Id;
                var owner = Text(record, "owner") ?? string.Empty;
                var label = Text(record, "label") ?? string.Empty;
                var info = new JsonObject
                {
                    ["team"] = team is null ? JsonValue.Null : team,
                    ["member"] = owner,
                    ["job_id"] = id,
                    ["label"] = label,
                };
                if (record.Get("pid") is not { IsNumber: true } pidValue || Text(record, "pid_start") is not { Length: > 0 } start)
                {
                    scan.Unverified.Add(info);
                    continue;
                }

                var pid = (int)pidValue.AsNumber();
                if (ProcessIdentity.Reused(table, pid, start))
                {
                    continue;
                }

                if (!ProcessIdentity.Same(table, pid, start))
                {
                    scan.Unverified.Add(info);
                    continue;
                }

                var kill = new List<int> { pid };
                kill.AddRange((table.Descendants(pid) ?? []).Select(n => n.Pid));
                if (protect.Contains(pid) || kill.Any(registered.Contains))
                {
                    info["pid"] = pid;
                    scan.Excluded.Add(info);
                    continue;
                }

                scan.Stop.Add(new Job(
                    team, owner, id, label, pid, kill.Where(p => LaunchModule.IsSignalablePid(p) && !protect.Contains(p)).Distinct().ToList(),
                    found, record));
            }
        }

        return scan;
    }

    /// <summary>
    /// A dismissed member's open background jobs with a verified pid: stopped as an emergency stop stops them, and the record of each
    /// one whose processes are gone deleted, so no job-overdue alarm outlives the member. Empty when there were none.
    /// </summary>
    internal static (JsonArray Stopped, JsonArray Unverified) StopMemberJobs(RosterContext c, string? team, string owner, List<string> warnings)
    {
        if (c.Services.Callers?.ProcessTable is not { } table)
        {
            return ([], []);
        }

        List<string?> covered = [team];
        List<Job> mine;
        JsonArray unverified;
        List<JsonObject> stopped;
        HashSet<int> alive;
        try
        {
            var scan = ScanJobs(c, covered, table, Protected(c, table, covered));
            unverified = Array(scan.Unverified.Select(j => j.AsObject()).Where(j => Text(j, "member") == owner));
            mine = scan.Stop.Where(j => j.Owner == owner && j.Background).ToList();
            if (mine.Count == 0)
            {
                return ([], unverified);
            }

            (stopped, var survivors) = StopJobs(c, PaneMembers.Panes(c, "dismiss"), table, mine);
            alive = survivors.Select(s => (int)s.Get("pid")!.Value.AsNumber()).ToHashSet();
        }
        catch (Exception e) when (e is RosterFailure or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            warnings.Add($"dismiss: {owner}'s background jobs were not stopped: {SafeText.Render(e.Message)}");
            return ([], []);
        }

        // An ended record leaves the overdue alarm at once; the 24 h job prune deletes it.
        var dir = c.MainDir ?? c.Dir;
        var now = Timestamps.ToIsoString(c.Services.Clock.UtcNow);
        for (var i = 0; i < mine.Count; i++)
        {
            var ended = false;
            if (!mine[i].Kill.Any(alive.Contains))
            {
                try
                {
                    c.Services.Hierarchy.StateCalls.PutJob(dir, mine[i].Found.Team, mine[i].Found.Id, WatchModule.JobPatch(mine[i].Record, WatchModule.JobEnded(mine[i].Record, now, "dismissed")), ifOpen: true);
                    ended = true;
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Ah.Core.Json.StoreUnavailableException)
                {
                    warnings.Add($"dismiss: job {mine[i].Id} of {owner} was stopped but its record was not ended: {SafeText.Render(e.Message)}");
                }
            }

            stopped[i]["row_ended"] = ended;
        }

        return (Array(stopped), unverified);
    }

    /// <summary>SIGTERM every pid of every job, two seconds, SIGKILL what is left, one more second; the survivors are what still lives.</summary>
    private static (List<JsonObject> Stopped, List<JsonObject> Survivors) StopJobs(RosterContext c, IRosterPanes panes, IProcessTable table, List<Job> jobs)
    {
        var started = new Dictionary<int, string?>();
        var commands = new Dictionary<int, string?>();
        var sent = new HashSet<int>();
        foreach (var pid in jobs.SelectMany(j => j.Kill).Distinct())
        {
            started[pid] = table.StartTime(pid);
            commands[pid] = table.Command(pid);
        }

        bool Alive(int pid) => ProcessStart.IsSame(ProcessStart.Compare(table, pid, started[pid]));
        var callers = c.Services.Callers!;
        foreach (var pid in started.Keys.Where(Alive))
        {
            _ = callers.SignalProcess(pid, SigTerm);
            sent.Add(pid);
        }

        panes.Sleeper.Sleep(TimeSpan.FromSeconds(2));
        var killed = new HashSet<int>();
        foreach (var pid in started.Keys.Where(Alive))
        {
            _ = callers.SignalProcess(pid, SigKill);
            killed.Add(pid);
        }

        panes.Sleeper.Sleep(TimeSpan.FromSeconds(1));
        var stopped = jobs.Select(j => new JsonObject
        {
            ["team"] = j.Team is null ? JsonValue.Null : j.Team,
            ["member"] = j.Owner,
            ["job_id"] = j.Id,
            ["pid"] = j.Pid,
            ["command"] = commands.GetValueOrDefault(j.Pid) is { } command ? command : j.Label,
            ["signal"] = j.Kill.Any(killed.Contains) ? "KILL" : j.Kill.Any(sent.Contains) ? "TERM" : "gone",
        }).ToList();
        var survivors = started.Keys.Where(Alive).Select(p => new JsonObject { ["pid"] = p, ["command"] = commands[p] is { } cmd ? cmd : JsonValue.Null }).ToList();
        return (stopped, survivors);
    }

    /// <summary>Read only: every process below each stopped member's registration, which the member did not stop itself.</summary>
    private static List<JsonObject> StillRunning(RosterContext c, IProcessTable table, List<Target> targets)
    {
        var running = new List<JsonObject>();
        var registrations = RegistrationPids(c);
        foreach (var target in targets)
        {
            var teamPath = c.Services.Roster.TeamPath(c.Dir, target.Team);
            foreach (var (key, pid) in registrations)
            {
                var parts = key.Split('|');
                if (!parts[0].StartsWith(target.Name + "@", StringComparison.Ordinal) || parts[1] != teamPath)
                {
                    continue;
                }

                foreach (var node in table.Descendants(pid) ?? [])
                {
                    running.Add(new JsonObject
                    {
                        ["team"] = target.Team is null ? JsonValue.Null : target.Team,
                        ["member"] = target.Name,
                        ["pid"] = node.Pid,
                        ["pid_start"] = table.StartTime(node.Pid) is { } start ? start : JsonValue.Null,
                        ["command"] = node.Command is { } command ? command : JsonValue.Null,
                    });
                }
            }
        }

        return running;
    }

    // ---------------------------------------------------------------- evidence and output

    private static string WriteEvidence(
        RosterContext c, string? top, IReadOnlyList<string?> teams, string stamp, string since, string? reason, List<JsonObject> signalled, List<JsonObject> stopped,
        JobScan scan, List<JsonObject> survivors, List<JsonObject> running, JsonObject drained)
    {
        var names = teams.Select(t => t ?? "_default").ToHashSet(StringComparer.Ordinal);
        bool Mine(JsonObject o) => o.Get("team") is { IsText: true } t ? names.Contains(t.AsString()) : names.Contains("_default");
        var attributed = c.Services.Hierarchy.Roster.AttributedRoster(c.Dir);
        var cache = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        var members = new JsonArray();
        foreach (var team in teams)
        {
            var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
            if (record.IsNull || record.Member("members") is not { IsArray: true } rows)
            {
                continue;
            }

            foreach (var row in rows.AsArray().Where(r => MemberPlanning.Text(r.Member("name")) is { Length: > 0 }))
            {
                members.Add(MemberEvidence(c, team, CheckpointVerb.See(c, c.Dir, row, attributed, cache, recordWhatItCan: true)));
            }
        }

        var evidence = new JsonObject
        {
            ["ts"] = since,
            ["reason"] = reason is null ? JsonValue.Null : reason,
            ["by"] = HoldStore.By(c, top),
            ["teams"] = Arr(teams.Select(t => (JsonValue)(t ?? "_default"))),
            ["signalled"] = Array(signalled.Where(Mine)),
            ["jobs_stopped"] = Array(stopped.Where(Mine)),
            ["jobs_unverified"] = Array(scan.Unverified.Select(j => j.AsObject()).Where(Mine)),
            ["jobs_excluded"] = Array(scan.Excluded.Select(j => j.AsObject()).Where(Mine)),
            ["job_survivors"] = Array(survivors),
            ["still_running"] = Array(running.Where(Mine)),
            ["members"] = members,
            ["drained"] = drained,
        };
        var path = HoldStore.Named(c, top, stamp, "stop-evidence.json");
        HoldStore.WriteFile(c, path, JsonText.Stringify(evidence, 2) + "\n");
        return path;
    }

    private static JsonArray Array(IEnumerable<JsonObject> items) => Arr(items.Select(i => (JsonValue)i));

    private static JsonArray Arr(IEnumerable<JsonValue> items)
    {
        var array = new JsonArray();
        array.AddRange(items);
        return array;
    }

    internal static JsonObject MemberEvidence(RosterContext c, string? team, CheckpointVerb.Seen seen)
    {
        var porcelain = JsonValue.Null;
        var ops = new JsonArray();
        if (seen.Tree is { } tree)
        {
            if (ReleaseGate.Git(c, tree, "status", "--porcelain=v1", "-b") is { Ok: true } status)
            {
                porcelain = status.Stdout;
            }

            if (ReleaseGate.Git(c, tree, "rev-parse", "--absolute-git-dir") is { Ok: true } gitDir)
            {
                var dir = gitDir.Stdout.Trim();
                foreach (var op in GitOps.Where(o => c.Services.Files.Exists(NodePath.Join(dir, o))))
                {
                    ops.Add(op);
                }
            }
        }

        return new JsonObject
        {
            ["team"] = team is null ? JsonValue.Null : team,
            ["name"] = seen.Name,
            ["worktree"] = seen.Tree is null ? JsonValue.Null : seen.Tree,
            ["branch"] = seen.Branch is null ? JsonValue.Null : seen.Branch,
            ["head"] = seen.Head is null ? JsonValue.Null : seen.Head,
            ["porcelain"] = porcelain,
            ["git_ops"] = ops,
            ["open_requests"] = seen.OpenError is null ? Arr(seen.Open.Select(o => (JsonValue)o)) : JsonValue.Null,
            ["open_requests_error"] = seen.OpenError is null ? JsonValue.Null : seen.OpenError,
        };
    }

    private static JsonObject Planned(List<string?> tops, List<string?> covered, List<Target> targets, JobScan scan) => new()
    {
        ["verb"] = "stop",
        ["status"] = "planned",
        ["teams"] = Arr(covered.Select(t => (JsonValue)(t ?? "_default"))),
        ["would_signal"] = Array(targets.Select(t => new JsonObject { ["team"] = t.Team is null ? JsonValue.Null : t.Team, ["member"] = t.Name, ["via"] = t.Via })),
        ["would_stop_jobs"] = Array(scan.Stop.Select(j => new JsonObject
        {
            ["team"] = j.Team is null ? JsonValue.Null : j.Team,
            ["member"] = j.Owner,
            ["job_id"] = j.Id,
            ["pid"] = j.Pid,
            ["label"] = j.Label,
            ["processes"] = j.Kill.Count,
        })),
        ["jobs_unverified"] = scan.Unverified,
        ["jobs_excluded"] = scan.Excluded,
        ["next"] = "Nothing was changed. Run the same command with --confirm to stop " + (tops.Count == 1 ? "the team." : "these teams."),
    };

    private static JsonObject Result(
        RosterContext c, string? top, IReadOnlyList<string?> teams, string since, string evidence, string checkpoint, List<JsonObject> signalled, List<JsonObject> stopped,
        JobScan scan, List<JsonObject> survivors, List<JsonObject> running, JsonObject drained)
    {
        var names = teams.Select(t => t ?? "_default").ToHashSet(StringComparer.Ordinal);
        bool Mine(JsonObject o) => o.Get("team") is { IsText: true } t ? names.Contains(t.AsString()) : names.Contains("_default");
        var jobPids = stopped.Concat(survivors).Select(o => o.Get("pid")).OfType<JsonValue>().Where(p => p.IsNumber).Select(p => p.AsNumber()).ToHashSet();
        bool NotAJob(JsonObject o) => o.Get("pid") is not { IsNumber: true } p || !jobPids.Contains(p.AsNumber());
        return new JsonObject
        {
            ["verb"] = "stop",
            ["team"] = top is null ? JsonValue.Null : top,
            ["teams"] = Arr(teams.Select(t => (JsonValue)(t ?? "_default"))),
            ["state"] = "stop",
            ["phase"] = "stopped",
            ["since"] = since,
            ["hold"] = HoldStore.PathOf(c, top),
            ["evidence"] = evidence,
            ["checkpoint"] = checkpoint,
            ["signalled"] = Array(signalled.Where(Mine)),
            ["jobs_stopped"] = Array(stopped.Where(Mine)),
            ["jobs_unverified"] = Array(scan.Unverified.Select(j => j.AsObject()).Where(Mine)),
            ["jobs_excluded"] = Array(scan.Excluded.Select(j => j.AsObject()).Where(Mine)),
            ["job_survivors"] = Array(survivors),
            ["session_processes"] = Array(running.Where(Mine).Where(NotAJob)),
            ["session_processes_note"] = "the members' own session processes; STOP keeps sessions alive, so these are expected and are not survivors",
            ["drained"] = drained,
            ["next"] = $"Tell the user. Resume only on the user's order: ah roster resume --hold --team {HoldStore.TeamArg(top)} (it assesses first)",
        };
    }
}
