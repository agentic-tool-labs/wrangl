using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>resume --hold [--team T] [--yes]</c>: the only way out of a hold, run by the top Orchestrator on the user's order. From a quit
/// it resumes from the pinned quit checkpoint; without <c>--yes</c> it only shows what it would do.
/// </summary>
internal static class ResumeHold
{
    internal static int Run(RosterContext c)
    {
        if (c.Opts.IsTrue("checkpoint") || c.Opts.IsTrue("all") || c.Opts.Has("relaunch") || c.Opts.IsTrue("relaunch-missing") || c.Opts.Positionals.Count > 0)
        {
            throw HoldStore.Refuse(c, "bad_flags", "resume --hold cannot be combined with --checkpoint, --all, --relaunch, --relaunch-missing or a path");
        }

        var proof = ResumeVerb.Prove(c);
        if (proof.Refusal is { } refusal)
        {
            return refusal;
        }

        var team = c.TeamFile;
        var roster = c.Services.Roster;
        var record = roster.ReadTeamRecord(c.Dir, team);
        var takeover = false;
        if (!record.IsNull && record.Member("parent") is { IsMap: true })
        {
            RefuseLane(c, team, proof.Invoker!);
        }
        else if (record.IsNull)
        {
            throw HoldStore.Refuse(c, "resume_not_top", "only the top Orchestrator resumes, on the user's order");
        }
        else if (!roster.IsTeamOwnedBy(record, proof.Invoker!))
        {
            try
            {
                ResumeVerb.RefuseTakeover(c, record, proof.Invoker!);
            }
            catch (RosterFailure error)
            {
                throw HoldStore.Refuse(c, "resume_not_top", error.Message);
            }

            takeover = true;
        }

        var effective = roster.HoldOf(c.Dir, team) ?? throw HoldStore.Refuse(c, "not_held", $"team {team ?? "default"} is not on hold");
        var own = HoldStore.ReadOwn(c, team);
        if (own is null)
        {
            throw HoldStore.Refuse(c, "not_held", $"team {team ?? "default"} has an unreadable hold file at {effective.Path}; fix or remove it by hand");
        }

        if (takeover && c.Opts.IsTrue("yes") && ResumeVerb.Transfer(c, static () => []).Refusal is { } refused)
        {
            return refused;
        }

        if (effective.Kind == "stop")
        {
            return ResumeFromStop(c, team, own);
        }

        var (output, exit) = ResumeFromQuit(c, team, own);
        c.Out(output);
        return exit;
    }

    /// <summary>A lane resumes on its own only as the repair of a kept hold: no ancestor is held, and the caller owns its root team.</summary>
    private static void RefuseLane(RosterContext c, string? lane, Ah.Abstractions.TeamInvoker invoker)
    {
        var roster = c.Services.Roster;
        string? held = null;
        var root = lane;
        for (var depth = 0; depth < 8 && MemberPlanning.Text(roster.ReadTeamRecord(c.Dir, root).Member("parent")?.Member("team")) is { } parent; depth++)
        {
            root = parent;
            held = roster.HoldOf(c.Dir, parent) is not null ? parent : held;
        }

        if (held is not null)
        {
            throw HoldStore.Refuse(c, "resume_not_top", $"team {lane} is held through {held}; resume it with `ah roster resume --hold --team {held}`, on the user's order");
        }

        var owner = roster.ReadTeamRecord(c.Dir, root);
        if (root == lane || owner.IsNull || !roster.IsTeamOwnedBy(owner, invoker))
        {
            throw HoldStore.Refuse(c, "resume_not_top", "only the top Orchestrator resumes, on the user's order");
        }
    }

    internal static (JsonObject Output, int Exit) ResumeFromQuit(RosterContext c, string? team, JsonObject own)
    {
        var yes = c.Opts.IsTrue("yes");
        var wouldSeal = QuitVerb.WindingDown(c, team);
        if (yes && wouldSeal.Count > 0)
        {
            QuitVerb.SealForResume(c, team);
            own = HoldStore.ReadOwn(c, team) ?? throw HoldStore.Refuse(c, "hold_changed", $"the hold of team {team ?? "default"} was released while it was sealed; nothing is relaunched");
        }

        var checkpoint = HoldStore.Field(own, "checkpoint");
        var plan = HoldStore.Field(own, "plan");
        if (!yes)
        {
            // a quit killed before its checkpoint has none yet; --yes takes it
            var planned = checkpoint is null ? new JsonObject { ["status"] = "planned", ["members"] = new JsonArray() } : ResumeCheckpoint.ResumePinned(c, checkpoint, yes: false, rebrief: true).Output;
            planned["verb"] = "resume";
            planned["from"] = "quit";
            planned["hold"] = HoldStore.PathOf(c, team);
            planned["plan"] = plan is null ? JsonValue.Null : plan;
            planned["checkpoint"] = checkpoint is null ? JsonValue.Null : checkpoint;
            planned["would_seal"] = TeamNames(wouldSeal);
            planned["lanes"] = PlanLanes(c, team, own);
            planned["other_held_teams"] = OtherHeldTeams(c, team);
            return (planned, 0);
        }

        if (checkpoint is null)
        {
            throw HoldStore.Refuse(c, "not_held", "the hold names no checkpoint record to resume from");
        }

        var covered = c.Services.Roster.HoldCovered(c.Dir, team);
        var held = new EffectiveHold(team, null, HoldStore.PathOf(c, team), "quit", "held", HoldStore.Field(own, "since"), own, null);
        var read = ReadAll(c, team, own, covered);
        var drained = QuitVerb.DrainAll(c, covered, held);
        JsonArray archived = [], kept = [], recovered = [];
        var redelivered = 0;
        var (code, output, mailFailed) = ResumeCheckpoint.ResumePinned(c, checkpoint, yes: true, rebrief: true, () =>
        {
            (archived, kept) = ArchiveAll(c, team, covered, read);
            redelivered = Redeliver(c, covered, read, held);
            recovered = RecoverAll(c, covered);
        });
        if (code is { } refusal)
        {
            return (output, refusal);
        }

        TellResumed(c, output);
        var bare = BareWaiting(output);
        var lanes = new JsonArray();
        var exits = new List<int> { ResumeCheckpoint.ExitCode(output, mailFailed) };
        foreach (var lane in QuitVerb.ParentsFirst(c, team).Skip(1).Where(t => !c.Services.Roster.ReadTeamRecord(c.Dir, t).IsNull && c.Services.Roster.HoldOf(c.Dir, t) is null))
        {
            var pinned = read.TryGetValue(lane!, out var doc) ? HoldStore.Field(doc, "checkpoint") : null;
            var (laneOutput, laneExit) = OnLane(c, lane, () => pinned is null ? ResumeCheckpoint.ResumeFromRows(c, yes: true) : ResumeCheckpoint.ResumePinned(c, pinned, yes: true, rebrief: true));
            lanes.Add(laneOutput);
            exits.Add(laneExit);
            bare.AddRange(BareWaiting(laneOutput));
        }

        output["verb"] = "resume";
        output["from"] = "quit";
        output["plan"] = plan is null ? JsonValue.Null : plan;
        output["hold_archived"] = archived;
        output["lanes_kept"] = kept;
        output["lanes"] = lanes;
        output["recovered"] = recovered;
        output["bare_waiting"] = bare;
        output["drained"] = drained.Totals;
        output["redelivered"] = redelivered;
        output["other_held_teams"] = OtherHeldTeams(c, team);
        if (bare.Count > 0 && !MemberLaunchDoctor.RecoveryOn(c))
        {
            output["next"] = "ah roster recover enable";
            exits.Add(3);
        }

        return (output, exits.Contains(1) ? 1 : exits.Contains(3) ? 3 : 0);
    }

    /// <summary>Resumes one lane with the verb's team on it. A lane that cannot be resumed is reported; the others still come back.</summary>
    private static (JsonObject Output, int Exit) OnLane(RosterContext c, string? lane, Func<(int? Refusal, JsonObject Output, bool MailFailed)> resume) =>
        CheckpointVerb.WithTeam(c, lane, () =>
        {
            try
            {
                var (_, output, mailFailed) = resume();
                TellResumed(c, output);
                return (output, ResumeCheckpoint.ExitCode(output, mailFailed));
            }
            catch (RosterFailure error)
            {
                return (new JsonObject { ["team"] = lane!, ["error"] = error.Message }, 1);
            }
        });

    private static JsonArray PlanLanes(RosterContext c, string? team, JsonObject own)
    {
        var lanes = new JsonArray();
        var covered = c.Services.Roster.HoldCovered(c.Dir, team);
        var keptBack = new HashSet<string?>();
        foreach (var lane in QuitVerb.ParentsFirst(c, team).Skip(1))
        {
            var doc = HoldStore.ReadOwn(c, lane);
            var parent = MemberPlanning.Text(c.Services.Roster.ReadTeamRecord(c.Dir, lane).Member("parent")?.Member("team"));
            if (keptBack.Contains(parent) || (doc is not null && !Releases(doc, own, covered)))
            {
                keptBack.Add(lane);
                continue;
            }

            var pinned = doc is null ? null : HoldStore.Field(doc, "checkpoint");
            lanes.Add(OnLane(c, lane, () => pinned is null ? ResumeCheckpoint.ResumeFromRows(c, yes: false) : ResumeCheckpoint.ResumePinned(c, pinned, yes: false, rebrief: true)).Output);
        }

        return lanes;
    }

    internal static JsonArray TeamNames(IEnumerable<string?> teams)
    {
        var names = new JsonArray();
        names.AddRange(teams.Select(static t => (JsonValue)(t ?? "_default")));
        return names;
    }

    internal static JsonArray BareWaiting(JsonObject output)
    {
        var bare = new JsonArray();
        if (output.Get("members") is { IsArray: true } rows)
        {
            bare.AddRange(rows.AsArray().Where(static m => MemberPlanning.Text(m.Member("status")) == "bare").Select(static m => m.Member("name")!.Value));
        }

        return bare;
    }

    /// <summary>Re-stamps each covered team no longer held: its rows follow the sessions a restart left in their panes. The top's owner proof covers the lanes.</summary>
    private static JsonArray RecoverAll(RosterContext c, IReadOnlyList<string?> covered)
    {
        var recovered = new JsonArray();
        foreach (var each in covered.Where(t => !c.Services.Roster.ReadTeamRecord(c.Dir, t).IsNull && c.Services.Roster.HoldOf(c.Dir, t) is null))
        {
            var (restamped, down) = CheckpointVerb.WithTeam(c, each, () => TeamRecover.Restamp(c, c.Services.Roster.ReadTeamRecord(c.Dir, each)));
            recovered.Add(new JsonObject { ["team"] = each ?? "_default", ["restamped"] = TeamNames(restamped), ["down"] = TeamNames(down) });
        }

        return recovered;
    }

    /// <summary>Tells each member up again with no brief in this run that the hold is lifted, so an earlier "IS QUIT" line no longer applies. An alert only: it starts no work.</summary>
    internal static void TellResumed(RosterContext c, JsonObject output)
    {
        var team = c.TeamFile ?? "_default";
        var names = output.Get("members") is { IsArray: true } rows
            ? rows.AsArray()
                .Where(static m => MemberPlanning.Text(m.Member("status")) is "live" or "relaunched" && MemberPlanning.Text(m.Member("rebrief")) is not ("sent" or "unanswered-fallback"))
                .Select(static m => MemberPlanning.Text(m.Member("name"))!)
                .ToList()
            : [];
        if (names.Count == 0)
        {
            return;
        }

        try
        {
            var id = $"team-resumed:{team}:{c.Services.Clock.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            c.Services.EventMail?.SendEvent(
                c.Cwd,
                c.Dir,
                id,
                "team-resumed",
                $"team {team} is resumed: the hold is lifted, and any earlier \"IS QUIT\" line no longer applies. This starts no work.",
                new JsonObject { ["team"] = team },
                [.. names.Select(n => $"{n}@{WorkTreeAssignment.TeamOf(c)}")]);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // an alert only: the team is already resumed
        }
    }

    /// <summary>Queues again what the quit drained, in every covered team that is no longer held; a lane keeping a hold of its own keeps its mail back. Each team's rows are stamped by its own hold when it had one.</summary>
    private static int Redeliver(RosterContext c, IReadOnlyList<string?> covered, Dictionary<string, JsonObject> read, EffectiveHold held)
    {
        if (c.Services.Mailbox is not { } mailbox)
        {
            return 0;
        }

        var roster = c.Services.Roster;
        var count = 0;
        foreach (var each in covered.Where(t => !roster.ReadTeamRecord(c.Dir, t).IsNull && roster.HoldOf(c.Dir, t) is null))
        {
            var hold = read.TryGetValue(each ?? string.Empty, out var doc)
                ? new EffectiveHold(each, null, HoldStore.PathOf(c, each), HoldStore.Field(doc, "kind") ?? "quit", "held", HoldStore.Field(doc, "since"), doc, null)
                : held;
            count += (int)mailbox.RedeliverDrained(roster.TeamPath(c.Dir, each), each ?? "_default", hold).Get("entries")!.Value.AsNumber();
        }

        return count;
    }

    /// <summary>Every other top team on hold here, with the command that resumes it: nothing resumes them together, and one left out stays held.</summary>
    private static JsonArray OtherHeldTeams(RosterContext c, string? team)
    {
        var roster = c.Services.Roster;
        var others = new JsonArray();
        foreach (var other in new string?[] { null }.Concat(roster.ListTeamNames(c.Dir)).Where(t => t != team))
        {
            var record = roster.ReadTeamRecord(c.Dir, other);
            if (record.IsNull || record.Member("parent") is { IsMap: true } || roster.HoldOf(c.Dir, other) is not { } hold)
            {
                continue;
            }

            var resume = $"ah roster resume --hold --team {HoldStore.TeamArg(other)}";
            others.Add(new JsonObject
            {
                ["team"] = other ?? "_default",
                ["kind"] = hold.Kind,
                ["next"] = roster.TeamIsOrphaned(record) ? $"ah roster adopt --orchestrator-pid <your session's pid> --team {HoldStore.TeamArg(other)}, then {resume}" : resume,
            });
        }

        return others;
    }

    /// <summary>The own hold of every covered team that has one, read before anything is drained: what a later archive is compared against.</summary>
    internal static Dictionary<string, JsonObject> ReadAll(RosterContext c, string? team, JsonObject own, IReadOnlyList<string?> covered)
    {
        var read = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var each in covered)
        {
            if (HoldStore.ReadOwn(c, each) is { } doc)
            {
                read[each ?? string.Empty] = doc;
            }
        }

        if (!read.TryGetValue(team ?? string.Empty, out var top) || !HoldStore.Same(top, own))
        {
            throw HoldStore.Refuse(c, "hold_changed", $"the hold of team {team ?? "default"} changed before the resume started (a stop landed or it was resumed elsewhere); nothing is relaunched and that hold is left as it is");
        }

        return read;
    }

    /// <summary>
    /// Archives the hold of every covered team that had its own file when it was read, the top team last, so a crash leaves it held.
    /// A hold that was replaced or released since (a stop that landed during the drain) is left as it is and the resume refuses.
    /// </summary>
    internal static (JsonArray Archived, JsonArray Kept) ArchiveAll(RosterContext c, string? team, IReadOnlyList<string?> covered, IReadOnlyDictionary<string, JsonObject> read)
    {
        var archived = new JsonArray();
        var kept = new JsonArray();
        var top = read[team ?? string.Empty];
        foreach (var each in covered.Where(t => t != team).Append(team))
        {
            if (read.TryGetValue(each ?? string.Empty, out var before))
            {
                if (each != team && !Releases(before, top, covered))
                {
                    kept.Add($"lane {each ?? "default"} keeps its own {HoldStore.Field(before, "kind") ?? "hold"}; resume it separately");
                    continue;
                }

                archived.Add(HoldStore.Release(c, each, before)
                    ?? throw HoldStore.Refuse(c, "hold_changed", $"the hold of team {each ?? "default"} changed while the resume ran (a stop landed or it was resumed elsewhere); nothing is relaunched and that hold is left as it is"));
            }
        }

        return (archived, kept);
    }

    /// <summary>A lane's hold goes with the top's when it is the same kind and cascaded from a covered team; a lane's own stop, or a hold from outside, stays.</summary>
    private static bool Releases(JsonObject lane, JsonObject top, IReadOnlyList<string?> covered) =>
        HoldStore.Field(lane, "kind") == HoldStore.Field(top, "kind")
        && HoldStore.Field(lane, "cascade_from") is { } from && covered.Any(t => (t ?? "_default") == from);

    private static int ResumeFromStop(RosterContext c, string? team, JsonObject own)
    {
        var evidencePath = HoldStore.Field(own, "evidence") ?? throw HoldStore.Refuse(c, "not_held", "the stop hold names no evidence file to assess");
        if (!c.Opts.IsTrue("yes"))
        {
            return Assess(c, team, own, evidencePath);
        }

        var assessment = HoldStore.Field(own, "assessment");
        if (assessment is null || !c.Services.Files.Exists(assessment))
        {
            throw HoldStore.Refuse(c, "assess_first", $"run `ah roster resume --hold --team {HoldStore.TeamArg(team)}` without --yes first, read the assessment, then resume");
        }

        var checkpoint = HoldStore.Field(own, "checkpoint") ?? throw HoldStore.Refuse(c, "not_held", "the hold names no checkpoint record to resume from");
        var covered = c.Services.Roster.HoldCovered(c.Dir, team);
        var held = new EffectiveHold(team, null, HoldStore.PathOf(c, team), "stop", "stopped", HoldStore.Field(own, "since"), own, null);
        var read = ReadAll(c, team, own, covered);
        var drained = QuitVerb.DrainAll(c, covered, held);
        var open = OpenRequests(c, evidencePath);
        JsonArray archived = [], kept = [];
        var (code, output, mailFailed) = ResumeCheckpoint.ResumePinned(c, checkpoint, yes: true, rebrief: false, () => (archived, kept) = ArchiveAll(c, team, covered, read));
        if (code is { } refusal)
        {
            return refusal;
        }

        output["verb"] = "resume";
        output["from"] = "stop";
        output["assessment"] = assessment;
        output["open_requests"] = open;
        output["hold_archived"] = archived;
        output["lanes_kept"] = kept;
        output["drained"] = drained.Totals;
        output["other_held_teams"] = OtherHeldTeams(c, team);
        c.Out(output);
        return ResumeCheckpoint.ExitCode(output, mailFailed);
    }

    private static JsonObject ReadEvidence(RosterContext c, string path)
    {
        try
        {
            return JsonText.Parse(c.Services.Files.ReadText(path)) is { IsMap: true } map ? map.AsObject() : throw new RosterFailure($"resume: the stop evidence {path} is not a JSON object");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            throw new RosterFailure($"resume: the stop evidence {path} cannot be read: {e.Message}");
        }
    }

    /// <summary>The ids of the requests that were open at the stop, per member, for the Orchestrator to re-issue by hand.</summary>
    private static JsonArray OpenRequests(RosterContext c, string evidencePath)
    {
        var open = new JsonArray();
        if (ReadEvidence(c, evidencePath).Get("members") is { IsArray: true } members)
        {
            foreach (var member in members.AsArray().Where(m => m.Member("open_requests") is { IsArray: true } list && list.AsArray().Count > 0))
            {
                open.Add(new JsonObject { ["member"] = member.Member("name") ?? JsonValue.Null, ["requests"] = member.Member("open_requests")!.Value });
            }
        }

        return open;
    }

    private sealed record Reassessed(string Name, string? Team, JsonObject Now, bool Gone, bool HeadChanged, bool Dirty, int Stopped, int Unverified, int Running, List<string> Notes);

    private static bool Alive(RosterContext c, JsonValue process)
    {
        // StopVerb.StillRunning records each process's start; a record from before that has none and is judged on the pid alone
        var table = c.Services.Callers?.ProcessTable;
        return process.Member("pid") is { IsNumber: true } pid && table is not null
            && c.Services.Hierarchy.Core.PidRunning(pid, process.Member("pid_start"), Ah.Core.Time.LivenessPolicy.RunningOrUnknown);
    }

    private static int Assess(RosterContext c, string? team, JsonObject own, string evidencePath)
    {
        var evidence = ReadEvidence(c, evidencePath);
        var roster = c.Services.Roster;
        var attributed = c.Services.Hierarchy.Roster.AttributedRoster(c.Dir);
        var cache = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        var stopped = evidence.Get("jobs_stopped") is { IsArray: true } js ? js.AsArray() : new JsonArray();
        var unverified = evidence.Get("jobs_unverified") is { IsArray: true } ju ? ju.AsArray() : new JsonArray();
        var running = evidence.Get("still_running") is { IsArray: true } sr ? sr.AsArray() : new JsonArray();
        var signalled = evidence.Get("signalled") is { IsArray: true } sg ? sg.AsArray() : new JsonArray();
        var survivors = evidence.Get("job_survivors") is { IsArray: true } sv ? sv.AsArray() : new JsonArray();
        var rows = new List<Reassessed>();
        foreach (var then in evidence.Get("members") is { IsArray: true } list ? list.AsArray() : new JsonArray())
        {
            var name = MemberPlanning.Text(then.Member("name")) ?? string.Empty;
            var memberTeam = MemberPlanning.Text(then.Member("team"));
            bool Mine(JsonValue o) => MemberPlanning.Text(o.Member("member")) == name && MemberPlanning.Text(o.Member("team")) == memberTeam;
            var record = roster.ReadTeamRecord(c.Dir, memberTeam);
            var row = record.IsNull || record.Member("members") is not { IsArray: true } members
                ? JsonValue.Null
                : members.AsArray().FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == name);
            var notes = new List<string>();
            if (row.IsNull)
            {
                rows.Add(new Reassessed(name, memberTeam, new JsonObject(), true, false, false, 0, 0, 0, ["the member is no longer in the team record"]));
                continue;
            }

            var now = StopVerb.MemberEvidence(c, memberTeam, CheckpointVerb.See(c, c.Dir, row, attributed, cache));
            var headChanged = MemberPlanning.Text(then.Member("head")) != MemberPlanning.Text(now.Get("head") ?? JsonValue.Null);
            var porcelain = MemberPlanning.Text(now.Get("porcelain") ?? JsonValue.Null) ?? string.Empty;
            var dirty = porcelain.Split('\n').Any(l => l.Length > 0 && !l.StartsWith("##", StringComparison.Ordinal));
            var ops = now.Get("git_ops") is { IsArray: true } o ? o.AsArray().Count : 0;
            if (ops > 0)
            {
                notes.Add("a git operation is in progress");
            }

            if (dirty)
            {
                notes.Add("the work tree is dirty");
            }

            if (headChanged)
            {
                notes.Add("HEAD moved since the stop");
            }

            if (signalled.Any(s => Mine(s) && s.Member("ok") is { IsBool: true } ok && !ok.AsBool()))
            {
                notes.Add("the stop signal failed");
            }

            if (survivors.Count > 0)
            {
                notes.Add("a job process survived the stop");
            }

            var stillAlive = running.Where(r => Mine(r) && Alive(c, r)).ToList();
            if (stillAlive.Count > 0)
            {
                notes.Add($"{stillAlive.Count} process(es) of the member's harness are still running");
            }

            rows.Add(new Reassessed(name, memberTeam, now, false, headChanged, dirty, stopped.Count(Mine), unverified.Count(Mine), running.Count(Mine), notes));
        }

        var since = HoldStore.Field(own, "since");
        var path = HoldStore.Named(c, team, HoldStore.StampOfSince(since), "assessment.md");
        HoldStore.WriteFile(c, path, AssessmentText(c, own, evidencePath, since, rows, evidence));
        own["assessment"] = path;
        if (!HoldStore.Rewrite(c, team, own, own))
        {
            throw HoldStore.Refuse(c, "hold_changed", $"the hold of team {team ?? "default"} changed while the assessment ran (it was resumed or replaced); nothing is written");
        }

        var members2 = new JsonArray();
        foreach (var r in rows)
        {
            members2.Add(new JsonObject
            {
                ["name"] = r.Name,
                ["head_changed"] = r.HeadChanged,
                ["dirty"] = r.Dirty,
                ["git_ops"] = r.Now.Get("git_ops") ?? new JsonArray(),
                ["jobs_stopped"] = r.Stopped,
                ["jobs_unverified"] = r.Unverified,
                ["still_running"] = r.Running,
            });
        }

        c.Out(new JsonObject { ["verb"] = "resume", ["from"] = "stop", ["status"] = "assessed", ["assessment"] = path, ["members"] = members2, ["other_held_teams"] = OtherHeldTeams(c, team) });
        return 0;
    }

    private static string AssessmentText(RosterContext c, JsonObject own, string evidencePath, string? since, List<Reassessed> rows, JsonObject evidence)
    {
        var text = new System.Text.StringBuilder();
        text.Append("# Stop assessment\n\n");
        text.Append($"- Reason: {HoldStore.Field(own, "reason") ?? "none given"}\n- Stopped at: {since ?? "unknown"}\n- Evidence: {evidencePath}\n\n");
        text.Append("| member | signal via | head at stop | head now | changed | dirty | git ops | jobs stopped | jobs unverified | still running |\n|---|---|---|---|---|---|---|---|---|---|\n");
        var signalled = evidence.Get("signalled") is { IsArray: true } sg ? sg.AsArray() : new JsonArray();
        foreach (var r in rows)
        {
            var via = signalled.FirstOrDefault(s => MemberPlanning.Text(s.Member("member")) == r.Name) is { IsMap: true } hit ? MemberPlanning.Text(hit.Member("via")) ?? "?" : "-";
            var then = (evidence.Get("members") is { IsArray: true } l ? l.AsArray() : new JsonArray()).FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == r.Name);
            var headThen = MemberPlanning.Text(then.Member("head")) ?? "-";
            var headNow = MemberPlanning.Text(r.Now.Get("head") ?? JsonValue.Null) ?? "-";
            var ops = r.Now.Get("git_ops") is { IsArray: true } o ? o.AsArray().Count : 0;
            text.Append($"| {r.Name} | {via} | {headThen} | {headNow} | {(r.HeadChanged ? "yes" : "no")} | {(r.Dirty ? "yes" : "no")} | {ops} | {r.Stopped} | {r.Unverified} | {r.Running} |\n");
        }

        text.Append("\n## In flight at the stop\n");
        foreach (var r in rows)
        {
            text.Append($"\n### {r.Name}\n");
            bool Mine(JsonValue o) => MemberPlanning.Text(o.Member("member")) == r.Name && MemberPlanning.Text(o.Member("team")) == r.Team;
            var then = (evidence.Get("members") is { IsArray: true } l ? l.AsArray() : new JsonArray()).FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == r.Name && MemberPlanning.Text(m.Member("team")) == r.Team);
            var open = then.IsNull || then.Member("open_requests") is not { IsArray: true } requests ? [] : requests.AsArray().Select(x => MemberPlanning.JsConvertText(x)).ToList();
            text.Append($"- Open requests: {(open.Count == 0 ? "none" : string.Join(", ", open))}\n");
            foreach (var j in (evidence.Get("jobs_stopped") is { IsArray: true } a ? a.AsArray() : new JsonArray()).Where(Mine))
            {
                text.Append($"- Job stopped by ah: {MemberPlanning.Text(j.Member("command")) ?? "?"} (pid {MemberPlanning.JsConvertText(j.Member("pid"))}, {MemberPlanning.Text(j.Member("signal")) ?? "?"})\n");
            }

            foreach (var j in (evidence.Get("jobs_unverified") is { IsArray: true } b ? b.AsArray() : new JsonArray()).Where(Mine))
            {
                text.Append($"- Job ah could not verify, left to the member: {MemberPlanning.Text(j.Member("label")) ?? MemberPlanning.Text(j.Member("job_id")) ?? "?"}\n");
            }

            foreach (var p in (evidence.Get("still_running") is { IsArray: true } d ? d.AsArray() : new JsonArray()).Where(Mine))
            {
                text.Append($"- Process left running at the stop: {MemberPlanning.Text(p.Member("command")) ?? "?"} (pid {MemberPlanning.JsConvertText(p.Member("pid"))}), now {(Alive(c, p) ? "alive" : "gone")}\n");
            }
        }

        text.Append("\n## May be damaged or incomplete\n");
        var any = false;
        foreach (var r in rows.Where(r => r.Notes.Count > 0))
        {
            any = true;
            text.Append($"- {r.Name}: {string.Join("; ", r.Notes)}\n");
        }

        if (!any)
        {
            text.Append("- Nothing found.\n");
        }

        return text.ToString();
    }
}
