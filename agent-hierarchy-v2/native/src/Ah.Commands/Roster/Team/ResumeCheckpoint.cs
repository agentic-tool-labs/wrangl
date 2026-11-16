using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>resume --checkpoint [record | --all]</c>: after a restart, checks each member of a checkpoint record against its registration
/// and the team's own view, re-briefs the members that had open requests, and confirms this session is the team's Orchestrator.
/// A member is <c>live</c> (registered under the recorded session, running), <c>pending</c> (its registration names a process that
/// is gone, or it has none, but the team still lists its pane as running: a restore can still land there), <c>missing</c> (no live
/// registration and the team does not list it as running), or <c>replaced</c> (running under another session). Only a
/// <c>missing</c> member is ever relaunched, and only when asked to be; a pending one is never relaunched by this build.
/// </summary>
internal static class ResumeCheckpoint
{
    private const string Live = "live";

    private const string Missing = "missing";

    private const string Pending = "pending";

    private const string Relaunched = "relaunched";

    private const int SettleSeconds = 20;

    private const string ChainDownNote = "taken for a chain-down resume";

    private sealed class Member(string name, JsonValue entry, string status, string why)
    {
        internal string Name { get; } = name;

        internal JsonValue Entry { get; } = entry;

        internal string Status { get; set; } = status;

        private string _why = Reasoned(status, why);

        /// <summary>Why the member is in its status; never empty, so a refusal always says something.</summary>
        internal string Why
        {
            get => _why;
            set => _why = Reasoned(Status, value);
        }

        private static string Reasoned(string status, string? why) => string.IsNullOrWhiteSpace(why) ? $"{status}: no reason was recorded" : why;

        internal double? AgeSeconds { get; set; }

        /// <summary>The pane the team records for the member, with its transport and id; set for a member that is not live.</summary>
        internal (PaneReading Reading, string? Transport, string? Id)? Pane { get; set; }

        internal string? Relaunch { get; set; }

        /// <summary>The launch's own row for the member (its <c>session</c>, <c>pane</c>, <c>old_pane</c>, <c>blocked</c>); null until launched.</summary>
        internal JsonObject? Launched { get; set; }

        /// <summary>The state of the member's pane read just before its launch; null until read.</summary>
        internal PaneKnowledge? PaneNow { get; set; }

        /// <summary>What a person is told about a bare member that is not held: why a relaunch here is refused and what gets it re-adopted.</summary>
        internal string? BareSentence { get; set; }

        /// <summary>Which field of the checkpoint entry disagrees with the team file's row, or null when the entry is fresh.</summary>
        internal string? Stale { get; set; }

        internal string? Rebrief { get; set; }

        internal List<string> Sent { get; } = [];

        internal List<string> Open { get; } = Opens(entry);

        private static List<string> Opens(JsonValue entry) =>
            entry.Member("open_requests") is { IsArray: true } list ? list.AsArray().Where(v => v.IsText).Select(v => v.AsString()).ToList() : [];
    }

    private sealed record Outcome(int? Refusal, JsonObject Output, bool MailFailed);

    private sealed record Briefing(string Mode, List<string> Refs, string? Why);

    internal static int Run(RosterContext c)
    {
        if (c.Opts.Positionals.Count > 1)
        {
            throw new RosterFailure("resume --checkpoint: at most one record path");
        }

        if (c.Opts.Has("relaunch") && c.Opts.Text("relaunch") is null)
        {
            throw new RosterFailure("resume --checkpoint: --relaunch needs the member's name");
        }

        if (c.Opts.IsTrue("all"))
        {
            if (c.Opts.Positionals.Count > 0 || c.Opts.Has("team") || c.Opts.Has("relaunch"))
            {
                throw new RosterFailure("resume --checkpoint --all: it cannot be combined with a record path, --team or --relaunch");
            }

            return RunAll(c);
        }

        var (record, recordPath, recordOnly) = Load(c);
        var one = Resume(c, record, recordPath, transfer: true, recordOnly: recordOnly);
        if (one.Refusal is { } refusal)
        {
            return refusal;
        }

        c.Out(one.Output);
        PrintMemory(c, c.ProgressTeam);
        return ExitCode(one.Output, one.MailFailed);
    }

    /// <summary>1 when a re-brief could not be mailed; else 3, a partial result, when a member ended relaunch-failed; else 0.</summary>
    internal static int ExitCode(JsonObject output, bool mailFailed) => mailFailed ? 1 : RelaunchFailed(output) ? 3 : 0;

    private static bool RelaunchFailed(JsonObject output) =>
        output.Get("members") is { IsArray: true } rows && rows.AsArray().Any(static m => m.Member("status") is { IsText: true } status && status.AsString() == "relaunch-failed");

    /// <summary>Writes the team's AH MEMORY block to stderr when its ledger has something to recall; stdout stays one JSON value.</summary>
    private static void PrintMemory(RosterContext c, string team)
    {
        if (c.Services.Hierarchy.Progress.RecallDigest(c.ProgressPool, team, includeLanes: true) is { } digest)
        {
            c.Stderr.Write(digest + "\n");
        }
    }

    /// <summary>Every team the proved caller owns that has a checkpoint record, each on its own; ownership is never taken here.</summary>
    private static int RunAll(RosterContext c)
    {
        var proof = ResumeVerb.Prove(c);
        if (proof.Refusal is { } refusal)
        {
            return refusal;
        }

        var roster = c.Services.Roster;
        var candidates = new List<string?> { null };
        candidates.AddRange(roster.ListTeamNames(c.Dir));
        var reports = new JsonArray();
        var reportedTeams = new List<string?>();
        var mailFailed = false;
        var relaunchFailed = false;
        var before = c.TeamFile;
        try
        {
            foreach (var team in candidates)
            {
                var teamRecord = roster.ReadTeamRecord(c.Dir, team);
                if (teamRecord.IsNull || !roster.IsTeamOwnedBy(teamRecord, proof.Invoker!))
                {
                    continue;
                }

                c.TeamFile = team;
                var read = c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = c.Dir, ["team"] = team ?? "_default" });
                var found = read.Member("found") is { IsTruthy: true };
                try
                {
                    c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, team);
                    var recordOnly = CheckpointVerb.ReadRecordOnly(c, team, found ? read.Member("record") : null);
                    if (!found && recordOnly is null)
                    {
                        continue;
                    }

                    var done = recordOnly is { } taken
                        ? Resume(c, taken.Preview, string.Empty, transfer: false, recordOnly: taken)
                        : Resume(c, read.Member("record") ?? JsonValue.Null, MemberPlanning.JsConvertText(read.Member("path")), transfer: false);
                    mailFailed |= done.MailFailed;
                    relaunchFailed |= RelaunchFailed(done.Output);
                    reports.Add(done.Output);
                    reportedTeams.Add(team);
                }
                catch (RosterFailure error)
                {
                    reports.Add(new JsonObject { ["team"] = team ?? "_default", ["error"] = error.Message });
                }
            }
        }
        finally
        {
            c.TeamFile = before;
        }

        if (reports.Count == 0)
        {
            throw new RosterFailure("resume --checkpoint --all: no owned team has a checkpoint; name one with --team");
        }

        c.Out(new JsonObject { ["teams"] = reports });
        foreach (var team in reportedTeams)
        {
            PrintMemory(c, Ah.Hierarchy.HierarchyModule.ProgressTeamOf(team));
        }

        return mailFailed ? 1 : relaunchFailed ? 3 : 0;
    }

    /// <summary>
    /// The record to resume from: the one named (an absolute path), else the newest of the team, else, when the team is behind a wind-down,
    /// the record-only checkpoint to take. Sets the team the verb works on.
    /// </summary>
    private static (JsonValue Record, string Path, CheckpointVerb.RecordOnly? RecordOnly) Load(RosterContext c)
    {
        var services = c.Services;
        var named = c.Opts.Positionals.Count == 1 ? c.Opts.Positionals[0] : null;
        JsonValue record;
        string path;
        if (named is not null)
        {
            if (!Path.IsPathRooted(named) || CheckpointVerb.RecordAt(c, named) is not { } found)
            {
                throw new RosterFailure($"resume --checkpoint: no such record {named} (give its absolute path)");
            }

            path = named;
            record = found;
            var recorded = MemberPlanning.Text(record.Member("team")) ?? "_default";
            if (c.Opts.Text("team") is { } wanted && wanted != recorded)
            {
                throw new RosterFailure($"resume --checkpoint: --team {wanted} does not match the record's team {recorded}");
            }

            c.TeamFile = recorded == "_default" ? null : recorded;
        }
        else
        {
            c.RepoBasename = services.Config.TeamPrefix(c.Cwd, c.TeamFile);
            var read = services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = c.Dir, ["team"] = c.TeamFile ?? "_default" });
            var found = read.Member("found") is { IsTruthy: true };
            if (CheckpointVerb.ReadRecordOnly(c, c.TeamFile, found ? read.Member("record") : null) is { } recordOnly)
            {
                return (recordOnly.Preview, string.Empty, recordOnly);
            }

            if (!found)
            {
                var label = c.TeamFile is null ? "(the default team)" : $"\"{c.TeamFile}\"";
                throw new RosterFailure($"resume --checkpoint: no checkpoint for {label} — take one with `checkpoint`");
            }

            path = MemberPlanning.JsConvertText(read.Member("path"));
            record = read.Member("record") ?? JsonValue.Null;
        }

        c.RepoBasename = services.Config.TeamPrefix(c.Cwd, c.TeamFile);
        return (record, path, null);
    }

    /// <summary>
    /// The resume of one team from the record at <paramref name="recordPath"/> exactly, as the hold verbs run it: every missing member is
    /// relaunched, and <paramref name="rebrief"/> says whether each live member is sent its own checkpoint response as its resume plan.
    /// </summary>
    internal static (int? Refusal, JsonObject Output, bool MailFailed) ResumePinned(RosterContext c, string recordPath, bool yes, bool rebrief, Action? release = null)
    {
        var record = CheckpointVerb.RecordAt(c, recordPath) ?? throw new RosterFailure($"resume --checkpoint: no such record {recordPath} (give its absolute path)");
        c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, c.TeamFile);
        var done = Resume(c, record, recordPath, transfer: false, rebrief, yes, relaunchMissing: true, release);
        return (done.Refusal, done.Output, done.MailFailed);
    }

    /// <summary>The resume of one team with no checkpoint: the team file's rows are judged, every missing member is relaunched from them, and no brief is sent.</summary>
    internal static (int? Refusal, JsonObject Output, bool MailFailed) ResumeFromRows(RosterContext c, bool yes)
    {
        c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, c.TeamFile);
        var record = new JsonObject { ["team"] = c.TeamFile ?? "_default", ["members"] = new JsonArray() };
        var done = Resume(c, JsonValue.FromObject(record), string.Empty, transfer: false, rebrief: false, yes, relaunchMissing: true, fromRows: true);
        return (done.Refusal, done.Output, done.MailFailed);
    }

    /// <summary>Plans (without <c>--yes</c>) or does the resume of one team from <paramref name="record"/>; with <paramref name="transfer"/> the caller also becomes the team's Orchestrator.</summary>
    private static Outcome Resume(RosterContext c, JsonValue record, string recordPath, bool transfer, bool rebrief = true, bool? yes = null, bool? relaunchMissing = null, Action? release = null, bool fromRows = false, CheckpointVerb.RecordOnly? recordOnly = null)
    {
        var dir = c.Dir;
        var relaunchName = c.Opts.Text("relaunch");
        var manual = relaunchMissing is null || fromRows;
        List<Member> Verified() => Verify(c, dir, record, fromRows ? static _ => true : manual && (relaunchName is not null || c.Opts.IsTrue("relaunch-missing")) ? name => relaunchName is null || name == relaunchName : null);
        var members = Verified();
        if (relaunchMissing == true)
        {
            members = Settle(c, yes ?? c.Opts.IsTrue("yes"), members, Verified);
        }

        if (relaunchName is not null)
        {
            var asked = members.FirstOrDefault(m => m.Name == relaunchName)
                ?? throw new RosterFailure($"resume --checkpoint: --relaunch {relaunchName}: team {c.TeamFile ?? "_default"} has no member {relaunchName}");
            if (asked.BareSentence is { } sentence)
            {
                throw new RosterFailure($"resume --checkpoint: --relaunch {relaunchName}: {relaunchName} runs without ah's launch and is not relaunched here; {sentence}");
            }

            if (asked.Status is Live or "replaced")
            {
                throw new RosterFailure($"resume --checkpoint: --relaunch {relaunchName}: {relaunchName} is running; nothing to relaunch");
            }
        }

        // A named member may be pending (an explicit decision that it died for good); --relaunch-missing takes missing members only.
        var wanted = members.Where(m => (m.Status == Missing && (relaunchMissing ?? c.Opts.IsTrue("relaunch-missing") || m.Name == relaunchName)) || (m.Status == Pending && m.Name == relaunchName)).ToList();
        foreach (var named in members.Where(m => m.Name == relaunchName && m.Status is not (Missing or Pending)))
        {
            named.Relaunch = $"skipped: the member is {named.Status}, and only a missing or pending member is relaunched";
        }

        var cache = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        foreach (var m in members.Where(m => m.Open.Count > 0))
        {
            m.Rebrief = relaunchName is not null && m.Name != relaunchName ? $"skipped: --relaunch {relaunchName} touches only that member"
                : m.Status != Live ? $"skipped: the member is {m.Status}" : Describe(Plan(c, dir, m, cache));
        }

        var output = new JsonObject { ["status"] = "planned" };
        if (!(yes ?? c.Opts.IsTrue("yes")))
        {
            foreach (var m in wanted)
            {
                m.Relaunch = "would relaunch";
            }

            Fill(output, recordPath, record, members);
            if (recordOnly is not null)
            {
                output["record_only"] = "would take";
            }

            return new Outcome(null, output, false);
        }

        var snapshot = wanted.Count == 0 ? (JsonValue?)null : manual ? LiveSnapshot(c, record, members) : ReadSnapshot(c, record);
        List<JsonObject> Launch() => LaunchMissing(c, wanted, snapshot);
        ResumeVerb.Handover? handover = null;
        if (transfer)
        {
            handover = ResumeVerb.Transfer(c, Launch);
            if (handover.Refusal is { } refusal)
            {
                return new Outcome(refusal, output, false);
            }
        }
        else
        {
            // The hold goes only now: a record or snapshot that cannot be used has refused above and the team stays held.
            release?.Invoke();
            _ = Launch();
        }

        // Ownership refused: a record-only run writes nothing, so it sends nothing from a record that does not exist.
        var noRecord = recordOnly is not null && handover?.Refused is not null;
        if (recordOnly is not null && !noRecord)
        {
            var taken = CheckpointVerb.Take(c, c.TeamFile, ChainDownNote, decide: true, recordOnly: recordOnly.Seen);
            recordPath = MemberPlanning.JsConvertText(taken.Get("record"));
            record = CheckpointVerb.RecordAt(c, recordPath) ?? throw new RosterFailure($"resume --checkpoint: no such record {recordPath} (give its absolute path)");
            output["record_only"] = "taken";
        }

        var mailFailed = rebrief && !noRecord && ReBrief(c, dir, members, cache, relaunchName);
        output["status"] = "resumed";
        output["orchestrator"] = handover is null ? "owned" : handover.Refused is not null ? "held" : handover.AlreadyBound ? "bound" : "transferred";
        if (handover is { Session: null })
        {
            output["session"] = "unproven";
        }

        if (handover?.Refused is { } held)
        {
            output["ownership_refused"] = ResumeVerb.OwnershipRefusal(held);
        }

        Fill(output, recordPath, record, members);
        if (mailFailed)
        {
            output["mail_failed"] = true;
        }

        return new Outcome(null, output, mailFailed);
    }

    private static bool InReadablePane(Member m) => m.Status == Pending && m.Pane is { Reading.State: PaneKnowledge.Present };

    /// <summary>
    /// For a hold or <c>--all</c> resume: a pending member whose pane is there gets one wait per command, since a registration arrives in
    /// 1-9 s, then everything is read again. One still pending with no harness in its pane is left in a bare shell, so it counts as missing.
    /// </summary>
    private static List<Member> Settle(RosterContext c, bool yes, List<Member> members, Func<List<Member>> verify)
    {
        var panes = c.Services.Launch?.PaneState;
        if (yes && !c.Settled && members.Any(InReadablePane))
        {
            c.Settled = true;
            c.Services.Sleep(TimeSpan.FromSeconds(SettleSeconds));
            RefreshPanes(c.Services);
            members = verify();
        }

        foreach (var m in members.Where(InReadablePane))
        {
            if (m.Pane!.Value.Reading.RootPid is { } root && panes?.HarnessRunning(root) == false)
            {
                m.Status = Missing;
                m.Why = "its pane holds a shell with no harness, and it has not registered again";
            }
        }

        return members;
    }

    /// <summary>
    /// Relaunches the members asked for, once the Orchestrator is proved. A member whose registration is live again by now was restored
    /// meanwhile and is refused; the others go through the same launch path as a snapshot resume.
    /// </summary>
    private static List<JsonObject> LaunchMissing(RosterContext c, List<Member> wanted, JsonValue? snapshot)
    {
        var address = WorkTreeAssignment.TeamOf(c);
        var go = new List<Member>();
        if (wanted.Count > 0)
        {
            RefreshPanes(c.Services);
        }

        // the snapshot's session is the one source for both the stale refusal and the launch
        foreach (var entry in (snapshot?.Member("members") is { IsArray: true } listed ? listed.AsArray() : []).Where(e => wanted.Any(m => m.Name == MemberPlanning.Text(e.Member("name")))))
        {
            if (NewestSession(c, MemberPlanning.Text(entry.Member("name"))!) is { } newest)
            {
                entry.AsObject()["session_id"] = newest;
            }
        }

        foreach (var m in wanted)
        {
            if (c.Services.Hierarchy.Core.Liveness?.ForMember(m.Name, address) is { } restored && restored.HoldsClaim(c.Services.Clock.UtcNow))
            {
                Refuse(m, $"restored meanwhile: it registered again as session {restored.SessionId}");
            }
            else if (m.Stale is not null && SnapshotSession(snapshot, m.Name) is null)
            {
                Refuse(m, $"checkpoint entry is stale ({m.Stale}) and the team file records no session for it; nothing was launched");
            }
            else if (PaneObstacle(c, m) is { } obstacle)
            {
                Refuse(m, obstacle);
            }
            else
            {
                go.Add(m);
            }
        }

        var results = go.Count == 0
            ? []
            : ResumeVerb.ResumeMembers(
                c,
                snapshot!.Value,
                name => go.Any(m => m.Name == name),
                null,
                name => (snapshot.Value.Member("members") is { IsArray: true } all ? all.AsArray() : []).FirstOrDefault(e => MemberPlanning.Text(e.Member("name")) == name) is { IsMap: true } e
                    ? MemberPlanning.Text(e.Member("session_id"))
                    : null,
                // a pane with no harness in it holds a dead terminal (Claude's exit cleared its agent): it is replaced, never typed into
                name => go.FirstOrDefault(m => m.Name == name) is { PaneNow: PaneKnowledge.Present, Pane: { Transport: "herdr" } present } ? present.Id : null);
        foreach (var m in go)
        {
            var row = results.FirstOrDefault(r => r.Get("name") is { IsText: true } n && n.AsString() == m.Name);
            var launched = row?.Get("status") is { IsText: true } s ? s.AsString() : "failed";
            m.Relaunch = launched;
            m.Launched = row;
            if (m.PaneNow == PaneKnowledge.Absent && row is not null && launched is "resumed" or "fresh")
            {
                row["old_pane"] = "absent";
            }

            if (launched == "blocked")
            {
                m.Status = "relaunch-blocked";
                m.Why = row?.Get("why") is { IsText: true } blockedWhy ? blockedWhy.AsString() : "blocked";
            }
            else if (launched is "resumed" or "fresh")
            {
                m.Status = Relaunched;
                m.Why = $"launched from its work tree ({launched})";
            }
            else
            {
                m.Status = launched == "alive" ? Live : "relaunch-failed";
                m.Why = row?.Get("why") is { IsText: true } why ? why.AsString() : "it is not in the snapshot";
            }
        }

        return results;
    }

    private static string? SnapshotSession(JsonValue? snapshot, string name) =>
        (snapshot?.Member("members") is { IsArray: true } all ? all.AsArray() : []).FirstOrDefault(e => MemberPlanning.Text(e.Member("name")) == name) is { IsMap: true } e
            ? MemberPlanning.Text(e.Member("session_id"))
            : null;

    /// <summary>The newest session known for a member: its matched bare mark's, else the newest registration's; null when neither names one.</summary>
    private static string? NewestSession(RosterContext c, string name)
    {
        var row = c.Services.Roster.TeamMemberByName(c.Dir, name, c.TeamFile);
        if (row.IsMap && BareMark.From(row.Member("bare")) is { State: BareMark.Matched, SessionId: { Length: > 0 } marked })
        {
            return marked;
        }

        return c.Services.Launch?.Snapshots.SessionIdOf(c.Dir, c.TeamFile, new JsonObject { ["name"] = name }) is { IsText: true } registered && registered.AsString().Length > 0
            ? registered.AsString()
            : null;
    }

    private static void Refuse(Member m, string why)
    {
        m.Status = "relaunch-refused";
        m.Why = why;
        m.Relaunch = "refused";
    }

    /// <summary>
    /// Why the pane of a member about to be relaunched is not safe to launch into, or null when it is: its state cannot be had, or a
    /// harness runs in it (or the process table that would say could not be read, which counts as running).
    /// </summary>
    private static string? PaneObstacle(RosterContext c, Member m)
    {
        var panes = c.Services.Launch?.PaneStateFor(IRosterLaunch.FlavourOf(c.Services, c.Dir, c.TeamFile, MemberPlanning.Text(m.Entry.Member("session")), m.Name));
        if (m.Pane is not { } recorded)
        {
            return NoRecordedPane;
        }

        if (recorded.Transport is null || recorded.Id is null)
        {
            return AbsenceObstacle(c, m.Name, MemberPlanning.Text(m.Entry.Member("session")));
        }

        var reading = panes?.Pane(recorded.Transport, recorded.Id) ?? new PaneReading(PaneKnowledge.Unknown, null, "this build has no pane reader");
        m.PaneNow = reading.State;
        switch (reading.State)
        {
            case PaneKnowledge.Absent:
                return null;
            case PaneKnowledge.Unknown:
                return "pane state unknown: " + (string.IsNullOrWhiteSpace(reading.Reason) ? "the reading gave no reason" : reading.Reason);
        }

        // A pane that was gone at the plan and is there now is a restore wave: its harness may start a moment after its shell, so a
        // walk that finds none proves nothing.
        if (m.Pane.Value.Reading.State == PaneKnowledge.Absent)
        {
            return "restore in progress: its pane was gone at the plan and is there now";
        }

        if (reading.RootPid is not { } root)
        {
            return "pane state unknown: the listing carries no pid for the pane";
        }

        return panes!.HarnessRunning(root) switch
        {
            false => null,
            true => "a harness is running in its pane",
            null => "a harness is running in its pane (the process table could not be read)",
        };
    }

    /// <summary>
    /// The team as its file records it now: a member is relaunched from its live row, never from what a checkpoint once recorded. A
    /// fresh checkpoint entry only fills in the work tree the team file lacks.
    /// </summary>
    private static JsonValue LiveSnapshot(RosterContext c, JsonValue record, List<Member> members)
    {
        var doc = c.Services.Launch!.Snapshots.BuildSnapshot(c.Dir, c.TeamFile, c.Services.Clock.UtcNow)
            ?? throw new RosterFailure($"resume --checkpoint: team {c.TeamFile ?? "_default"} has no members to relaunch");
        foreach (var live in (doc.Get("members") is { IsArray: true } list ? list.AsArray() : []).Where(m => m.IsMap && m.Member("work_root") is not { IsText: true }))
        {
            var name = MemberPlanning.Text(live.Member("name"));
            if (members.FirstOrDefault(m => m.Name == name) is { Stale: null }
                && RecordedMember(c, record, name!) is { } recorded
                && recorded.Member("work_root") is { IsText: true } root)
            {
                live.AsObject()["work_root"] = root;
            }
        }

        return JsonValue.FromObject(doc);
    }

    /// <summary>The record's snapshot entry for a member, or null when the record names no readable snapshot or the member is not in it.</summary>
    private static JsonValue? RecordedMember(RosterContext c, JsonValue record, string name)
    {
        try
        {
            return ReadSnapshot(c, record).Member("members") is { IsArray: true } all
                ? all.AsArray().Where(m => MemberPlanning.Text(m.Member("name")) == name).Cast<JsonValue?>().FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is RosterFailure or IOException or JsonParseException)
        {
            return null;
        }
    }

    private static JsonValue ReadSnapshot(RosterContext c, JsonValue record)
    {
        if (MemberPlanning.Text(record.Member("snapshot")) is not { Length: > 0 } snapshotPath)
        {
            throw new RosterFailure("resume --checkpoint: the record names no snapshot, so no member can be relaunched");
        }

        var doc = c.Services.Roster.ReadSnapshot(snapshotPath);
        return doc.IsMap ? doc : throw new RosterFailure($"resume --checkpoint: {snapshotPath} is not a team snapshot");
    }

    private const string NoRecordedPane = "no recorded pane; absence cannot be proven";

    /// <summary>
    /// With no recorded pane, absence is proved instead: the registry can be read (it lists this very session, since it reads as empty when
    /// it cannot be read), no live session is the member, and herdr's readable agent list holds no agent that is. Returns the check that failed.
    /// </summary>
    private static string? AbsenceObstacle(RosterContext c, string name, string? session)
    {
        var why = NoRecordedPane + ": ";
        var live = c.Services.OwnerProof?.LiveSessions() ?? [];
        if (!live.Any(s => s.Pid == c.Proved().Pid))
        {
            return why + "the session registry cannot be read";
        }

        var row = c.Services.Roster.TeamMemberByName(c.Dir, name, c.TeamFile);
        var rowSession = row.IsMap ? MemberPlanning.Text(row.Member("session_id")) : null;
        bool IsMember(string? id) => id is { Length: > 0 } && (id == session || id == rowSession);
        var address = $"{name}@{WorkTreeAssignment.TeamOf(c)}";
        if (live.Any(s => IsMember(s.SessionId) || s.Addresses.Contains(address)))
        {
            return why + "a live session is this member";
        }

        var transport = PaneTransport(c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile));
        if (transport != "herdr")
        {
            return why + $"only herdr's agent list can prove it, and this team's transport is {transport ?? "none"}";
        }

        if (c.Services.Launch?.PaneStateFor(IRosterLaunch.FlavourOf(c.Services, c.Dir, c.TeamFile, session ?? rowSession, name))?.Agents() is not { } agents)
        {
            return why + "the herdr agent list cannot be read";
        }

        return agents.Any(a => MemberPlanning.Text(a.Member("name")) == name || IsMember(MemberPlanning.Text(a.Member("session_id"))))
            ? why + "herdr lists an agent that is this member"
            : null;
    }

    private static string? PaneTransport(JsonValue team) => team.IsNull ? null : MemberPlanning.Text(team.Member("transport"));

    private static string? PaneId(JsonValue? row) => row is { } r ? MemberPlanning.Text(r.Member("transport_id")) : null;

    /// <summary>The state of the pane the team file records for the member: unknown when the team records none (absence cannot be proven) or this build has no pane reader.</summary>
    private static PaneReading PaneOf(IRosterCliServices services, JsonValue team, JsonValue? row)
    {
        if (PaneTransport(team) is not { } transport || PaneId(row) is not { } id)
        {
            return new PaneReading(PaneKnowledge.Unknown, null);
        }

        var session = row is { } r ? MemberPlanning.Text(r.Member("session_id")) : null;
        var flavour = Ah.Core.Mux.HerdrFlavour.Resolve(
            string.IsNullOrEmpty(session) ? null : services.Mailbox?.ReadRegistration(session), team, services.Launch?.Herdr.Flavour, row);
        return services.Launch?.PaneStateFor(flavour)?.Pane(transport, id) ?? new PaneReading(PaneKnowledge.Unknown, null);
    }

    /// <summary>Drops the pane listings of every herdr flavour read so far, so the next question lists again.</summary>
    private static void RefreshPanes(IRosterCliServices services)
    {
        services.Launch?.PaneStateFor(Ah.Core.Mux.HerdrFlavour.Tui)?.Refresh();
        services.Launch?.PaneStateFor(Ah.Core.Mux.HerdrFlavour.Core)?.Refresh();
    }

    /// <summary>The process of a member whose recovery record says its team was held when it was restored, or null when there is no such record.</summary>
    private static (int Pid, string? PidStart, DateTimeOffset Since)? HeldRecord(IRosterCliServices services, string dir, string? team, string member)
    {
        if (team is null)
        {
            return null;
        }

        try
        {
            if (services.RecoverRecords?.Read(Ah.Watch.WatchModule.RecoverDirKey(dir), team, member, false) is not { } text
                || JsonText.Parse(text) is not { IsMap: true } record
                || record.Member("state") is not { IsText: true } state || state.AsString() != Ah.Watch.WatchModule.RecoveryHeldState
                || record.Member("pid") is not { Kind: JsonKind.Number } pid)
            {
                return null;
            }

            var since = record.Member("detected_at") is { IsText: true } at
                && DateTimeOffset.TryParse(at.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            return ((int)pid.AsNumber(), record.Member("pid_start") is { IsText: true } start ? start.AsString() : null, since);
        }
        catch (Exception e) when (e is JsonParseException or StoreUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// The field in which a checkpoint entry disagrees with the member's live row (its session, else the expected root its snapshot
    /// recorded), or null when they agree or either side does not say. A stale entry is never acted on.
    /// </summary>
    private static string? StaleField(RosterContext c, JsonValue record, string name, string? entrySession, JsonValue row)
    {
        if (entrySession is not null && NewestSession(c, name) is { } current && current != entrySession)
        {
            return $"session {entrySession}, the team file has {current}";
        }

        if (MemberPlanning.Text(row.Member("expected_root")) is not { Length: > 0 } rowRoot)
        {
            return null;
        }

        var recordedRoot = RecordedMember(c, record, name) is { } recorded ? MemberPlanning.Text(recorded.Member("expected_root")) : null;
        return recordedRoot is { Length: > 0 } && recordedRoot != rowRoot ? $"expected_root {recordedRoot}, the team file has {rowRoot}" : null;
    }

    /// <summary>Each member of the record against its registration (the one liveness predicate) and the state of its pane.</summary>
    private static List<Member> Verify(RosterContext c, string dir, JsonValue record, Func<string, bool>? alsoFromTeamFile = null)
    {
        var services = c.Services;
        var address = WorkTreeAssignment.TeamOf(c);
        var team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        var rows = team.IsNull ? [] : team.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [];
        var members = new List<Member>();
        var entries = record.Member("members") is { IsArray: true } recorded ? recorded.AsArray().ToList() : [];
        var manual = alsoFromTeamFile is not null;
        if (manual)
        {
            // a member spawned after the checkpoint has a row but no entry; the row alone is enough to judge it
            var known = entries.Select(e => MemberPlanning.Text(e.Member("name"))).ToHashSet(StringComparer.Ordinal);
            entries.AddRange(rows
                .Select(r => MemberPlanning.Text(r.Member("name")))
                .Where(n => !string.IsNullOrEmpty(n) && !known.Contains(n) && alsoFromTeamFile!(n!))
                .Select(n => JsonValue.FromObject(new JsonObject { ["name"] = n! })));
        }

        if (entries.Count == 0)
        {
            return members;
        }

        var now = services.Clock.UtcNow;
        foreach (var entry in entries)
        {
            var name = MemberPlanning.Text(entry.Member("name")) ?? string.Empty;
            var session = MemberPlanning.Text(entry.Member("session"));
            JsonValue? row = rows.Cast<JsonValue?>().FirstOrDefault(r => MemberPlanning.Text(r!.Value.Member("name")) == name);
            var registration = services.Hierarchy.Core.Liveness?.ForMember(name, address);
            if (BareMark.From(row?.Member("bare")) is { State: BareMark.Matched, Pid: { } barePid } bare
                && services.Hierarchy.Core.Liveness is { } identity
                && Ah.Core.Time.LivenessRules.HoldsClaim(identity.JudgePeerRow(Ah.Core.Time.PeerRowKind.Up, barePid, bare.PidStart, bare.Since, null, null).Verdict, bare.Since, now))
            {
                var sentence = WatchModule.IsBareOrchestrator(MemberPlanning.Text(row?.Member("role")), name, null)
                    ? WatchModule.BareOrchestratorSentence
                    : WatchModule.BareSentence(MemberLaunchDoctor.RecoveryOn(c), c.TeamFile ?? "@default", name);
                members.Add(new Member(name, entry, "bare", $"it runs without ah's launch, so it is not adopted or re-briefed: {sentence}") { BareSentence = sentence });
                continue;
            }

            if (HeldRecord(services, dir, c.TeamFile, name) is { } heldRecord
                && services.Hierarchy.Core.Liveness is { } heldIdentity
                && Ah.Core.Time.LivenessRules.HoldsClaim(heldIdentity.JudgePeerRow(Ah.Core.Time.PeerRowKind.Up, heldRecord.Pid, heldRecord.PidStart, heldRecord.Since, null, null).Verdict, heldRecord.Since, now))
            {
                members.Add(new Member(name, entry, "bare", "it runs without ah's launch and its team was held when it was restored: the daemon re-adopts it once the hold is gone, so it is neither relaunched nor re-briefed here"));
                continue;
            }

            if (registration is { } held && held.HoldsClaim(now))
            {
                var replaced = session is not null && registration.SessionId != session;
                members.Add(new Member(
                    name,
                    entry,
                    replaced ? "replaced" : Live,
                    replaced ? $"running as session {registration.SessionId}; the checkpoint recorded {session}" : registration.Why));
                continue;
            }

            // Not live: the pane decides. A pane that is there can still be restored into, one that cannot be listed might be, and only a
            // pane that is gone cannot.
            var reading = PaneOf(services, team, row);
            var noPane = PaneTransport(team) is null || PaneId(row) is null;
            var absence = noPane ? AbsenceObstacle(c, name, session) : null;
            var member = reading.State == PaneKnowledge.Absent || (noPane && absence is null)
                ? new Member(name, entry, Missing, noPane ? "no recorded pane, and its absence is proved" : registration is null ? "no registration, and its pane is gone" : $"{registration.Why}, and its pane is gone")
                : new Member(
                    name,
                    entry,
                    Pending,
                    reading.State == PaneKnowledge.Unknown
                        ? (noPane ? absence!
                            : string.IsNullOrWhiteSpace(reading.Reason) ? "its pane could not be listed, so no action is safe" : $"its pane state is unknown ({reading.Reason}), so no action is safe")
                        : registration?.Why ?? "its pane is there, but it has not registered again");
            member.Pane = (reading, PaneTransport(team), PaneId(row));
            if (manual && row is { } live)
            {
                member.Stale = StaleField(c, record, name, session, live);
            }

            if (registration?.At is { } at && member.Status == Pending)
            {
                member.AgeSeconds = Math.Max(0, Math.Round((now - at).TotalSeconds));
            }

            members.Add(member);
        }

        return members;
    }

    // ---------------------------------------------------------------- the re-brief

    /// <summary>
    /// What a member with open requests is sent: its checkpoint response when it answered; else, because that request says "commit and
    /// stop", one brief per open request from the record, oldest first.
    /// </summary>
    private static Briefing Plan(RosterContext c, string dir, Member m, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> cache)
    {
        var tree = MemberPlanning.Text(m.Entry.Member("worktree"));
        if (CheckpointVerb.ReadResponse(c, dir, m.Entry, tree, cache).Response is { } response)
        {
            return new Briefing("response", [response], null);
        }

        var refs = new List<string>();
        var exchanges = ReleaseGate.ExchangesFor(c, dir, tree, cache);
        foreach (var id in m.Open.Order(StringComparer.Ordinal))
        {
            var found = exchanges.FirstOrDefault(e => e.Get("id") is { IsText: true } have && have.AsString() == id);
            if (found?.Get("request") is { IsMap: true } request && request.Member("path") is { IsText: true } path)
            {
                refs.Add(path.AsString());
            }
        }

        return refs.Count == 0
            ? new Briefing("none", [], $"skipped: no open request file was found for {string.Join(", ", m.Open)}")
            : new Briefing("unanswered-fallback", refs, null);
    }

    private static string Describe(Briefing plan) => plan.Mode switch
    {
        "response" => "would send",
        "unanswered-fallback" => "unanswered-fallback",
        _ => plan.Why!,
    };

    /// <summary>Sends each live or relaunched member that had open requests its briefs. True when a send was refused.</summary>
    private static bool ReBrief(RosterContext c, string dir, List<Member> members, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> cache, string? only)
    {
        var failed = false;
        foreach (var m in members.Where(m => m.Open.Count > 0))
        {
            if (only is not null && m.Name != only)
            {
                m.Rebrief = $"skipped: --relaunch {only} touches only that member";
                continue;
            }

            if (m.Status is not (Live or Relaunched))
            {
                m.Rebrief = $"skipped: the member is {m.Status}";
                continue;
            }

            var plan = Plan(c, dir, m, cache);
            if (plan.Mode == "none")
            {
                m.Rebrief = plan.Why;
                continue;
            }

            try
            {
                foreach (var file in plan.Refs)
                {
                    var sent = c.Services.SendBrief(c.Cwd, m.Name, WorkTreeAssignment.TeamOf(c), file)
                        ?? throw new InvalidOperationException("the mail layer is not composed");
                    if (sent.EntryId is not null)
                    {
                        m.Sent.Add(sent.EntryId);
                    }
                }

                m.Rebrief = plan.Mode == "response" ? "sent" : plan.Mode;
            }
            catch (InvalidOperationException error)
            {
                failed = true;
                m.Rebrief = $"mail_error: {error.Message}";
            }
        }

        return failed;
    }

    /// <summary>The daemon's re-brief of one member it relaunched, from the team's newest checkpoint. Null when sent or there is nothing to send, else why not.</summary>
    internal static string? ReBriefMember(RosterContext c, string name)
    {
        var read = c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = c.Dir, ["team"] = c.TeamFile ?? "_default" });
        if (read.Member("found") is not { IsTruthy: true }
            || read.Member("record")?.Member("members") is not { IsArray: true } entries
            || entries.AsArray().FirstOrDefault(e => MemberPlanning.Text(e.Member("name")) == name) is not { IsMap: true } entry)
        {
            return null;
        }

        var member = new Member(name, entry, Live, "relaunched by the daemon");
        _ = ReBrief(c, c.Dir, [member], new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal), name);
        return member.Rebrief is { } said && said.StartsWith("mail_error:", StringComparison.Ordinal) ? said : null;
    }

    private static void Fill(JsonObject output, string recordPath, JsonValue record, List<Member> members)
    {
        var noRecord = recordPath.Length == 0;
        var teamName = MemberPlanning.Text(record.Member("team")) ?? "_default";
        output["checkpoint"] = noRecord ? JsonValue.Null : recordPath;
        output["team"] = teamName;
        output["anchor"] = record.Member("anchor") ?? JsonValue.Null;
        output["resume_point"] = noRecord ? JsonValue.Null : recordPath;
        var rows = new JsonArray();
        var open = new JsonArray();
        foreach (var m in members)
        {
            var row = new JsonObject { ["name"] = m.Name, ["status"] = m.Status, ["why"] = m.Why };
            if (m.AgeSeconds is { } age)
            {
                row["age_seconds"] = age;
            }

            if (m.Relaunch is not null)
            {
                row["relaunch"] = m.Relaunch;
            }

            if (m.Rebrief is not null)
            {
                row["rebrief"] = m.Rebrief;
            }

            foreach (var key in new[] { "session", "pane", "old_pane", "blocked", "by_hand" })
            {
                if (m.Launched?.Get(key) is { IsNull: false } launchedValue)
                {
                    row[key] = launchedValue;
                }
            }

            if (m.Sent.Count > 0)
            {
                var entries = new JsonArray();
                entries.AddRange(m.Sent.Select(id => (JsonValue)id));
                row["entries"] = entries;
            }

            if (m.Status == "relaunch-failed")
            {
                row["next"] = noRecord
                    ? $"ah roster spawn-one {MemberPlanning.Text(m.Entry.Member("role")) ?? "<role>"} --member {m.Name} --team {(teamName == "_default" ? "@default" : teamName)}"
                    : $"ah roster resume --checkpoint {recordPath} --relaunch {m.Name} --yes";
            }

            if (m.Status == "relaunch-failed" && m.Open.Count > 0)
            {
                var orphaned = new JsonArray();
                orphaned.AddRange(m.Open.Select(id => (JsonValue)id));
                row["orphaned"] = orphaned;
            }

            rows.Add(row);
            foreach (var id in m.Open)
            {
                open.Add(new JsonObject { ["id"] = id, ["owner"] = m.Name });
            }
        }

        output["members"] = rows;
        output["open_requests"] = open;
        if (members.Where(m => m.Stale is not null).ToList() is { Count: > 0 } stale)
        {
            var notes = new JsonArray();
            notes.AddRange(stale.Select(m => (JsonValue)$"checkpoint entry for {m.Name} is stale ({m.Stale}); used the team file"));
            output["stale_entries"] = notes;
        }
    }
}
