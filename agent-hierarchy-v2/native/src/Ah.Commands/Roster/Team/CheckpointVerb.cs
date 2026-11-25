using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Commands;

/// <summary>
/// <c>checkpoint [--team T | --all] [--note text]</c> brings a team to a state a restart can survive: it saves a snapshot, asks
/// every member that is not idle and clean to commit and stop (a request with slug <c>checkpoint</c>, sent as mail), and writes a
/// record of who was asked. It never waits. <c>checkpoint --status</c> reads the newest record and says, from the answers filed
/// since and the work trees as they are now, whether a restart is safe: exit 0 when it is, 3 when it is not.
/// </summary>
internal static class CheckpointVerb
{
    internal const string Slug = "checkpoint";

    private const string IdleClean = "idle-clean";

    private const string Asked = "asked";

    private const string NotRunning = "not-running";

    private static readonly string[] Flags = ["cwd", "team", "all", "note", "status"];

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster checkpoint is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"checkpoint: unrecognized flag --{key} (use --team or --all, --note, --status)");
            }
        }

        if (c.Opts.IsTrue("all") && c.Opts.Has("team"))
        {
            throw new RosterFailure("checkpoint: --all and --team cannot be combined");
        }

        var status = c.Opts.IsTrue("status");
        if (status && c.Opts.Has("note"))
        {
            throw new RosterFailure("checkpoint: --note is only valid when taking a checkpoint, not with --status");
        }

        if (c.Opts.Has("note") && c.Opts.Text("note") is null)
        {
            throw new RosterFailure("checkpoint: --note needs the note text");
        }

        if (!status && c.Opts.Positionals.Count > 0)
        {
            throw new RosterFailure("checkpoint: only --status takes a record path");
        }

        if (status && c.Opts.Positionals.Count > 1)
        {
            throw new RosterFailure("checkpoint --status: at most one record path");
        }

        var teams = Teams(c);
        if (status)
        {
            return RunStatus(c, teams);
        }

        var outcomes = new JsonArray();
        var failed = false;
        foreach (var team in teams)
        {
            try
            {
                var outcome = WithTeam(c, team, () => Take(c, team, c.Opts.Text("note") ?? string.Empty, decide: true));
                failed |= outcome.Get("mail_failed") is { IsTruthy: true };
                outcomes.Add(outcome);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not RosterFailure)
            {
                // the teams already checkpointed (--all) are still reported, then the failing team stops the verb
                if (outcomes.Count > 0)
                {
                    c.Out(new JsonObject { ["teams"] = outcomes });
                }

                throw new RosterFailure($"checkpoint: team {TeamLabel(team)}: {error.Message}");
            }
        }

        c.Out(new JsonObject { ["teams"] = outcomes });
        return failed ? 1 : 0;
    }

    private static string TeamLabel(string? team) => team is null ? "(the default team)" : $"\"{team}\"";

    internal static T WithTeam<T>(RosterContext c, string? team, Func<T> body)
    {
        var before = c.TeamFile;
        c.TeamFile = team;
        try
        {
            return body();
        }
        finally
        {
            c.TeamFile = before;
        }
    }

    /// <summary>The teams to act on: the named one (the default team without <c>--team</c>), or with <c>--all</c> each team the proved caller owns.</summary>
    private static List<string?> Teams(RosterContext c)
    {
        if (!c.Opts.IsTrue("all"))
        {
            return [c.TeamFile];
        }

        var owned = OwnedTeams(c);
        if (owned.Count == 0)
        {
            throw new RosterFailure("checkpoint --all: this session owns no team");
        }

        return owned;
    }

    /// <summary>Each team the proved caller owns, the default team included.</summary>
    internal static List<string?> OwnedTeams(RosterContext c)
    {
        var roster = c.Services.Roster;
        var invoker = c.Invoker();
        var owned = new List<string?>();
        var candidates = new List<string?> { null };
        candidates.AddRange(roster.ListTeamNames(c.Dir));
        foreach (var team in candidates)
        {
            var record = roster.ReadTeamRecord(c.Dir, team);
            if (!record.IsNull && (c.UserActs || roster.IsTeamOwnedBy(record, invoker)))
            {
                owned.Add(team);
            }
        }

        return owned;
    }

    // ---------------------------------------------------------------- taking a checkpoint

    internal sealed record Seen(
        JsonValue Row, string Name, string Role, string? Session, bool Live, string? Tree, string? Branch, string? Head, bool Dirty, string? Failure, List<string> Open, bool Busy, string? OpenError = null)
    {
        internal bool IdleClean => Live && !Busy && Open.Count == 0 && OpenError is null && Tree is not null && Failure is null && !Dirty && Branch is { Length: > 0 } and not "HEAD";
    }

    /// <summary>
    /// Takes the checkpoint of <paramref name="team"/>. With <paramref name="quit"/> the request is the quit variant and each brief is
    /// sent hold-exempt, straight to the mailbox files, so the held team still receives it. With <paramref name="stop"/> (the path of a
    /// stop's evidence file) no request is created and no mail is sent: the record only names the evidence.
    /// </summary>
    // decide: a checkpoint or quit refuses when the message store cannot say what is open, and nothing is written; the emergency stop
    // leaves it false and records what it can. recordOnly: the members as already read; no request and no mail, as with a stop.
    internal static JsonObject Take(RosterContext c, string? team, string note, bool quit = false, string? stop = null, bool decide = false, List<Seen>? recordOnly = null)
    {
        var dir = c.Dir;
        var now = c.Services.Clock.UtcNow;
        var teamName = team ?? "_default";
        var seen = recordOnly ?? ReadMembers(c, team, decide, recordWhatItCan: stop is not null);

        // after the reads, so that a checkpoint the store refuses leaves no snapshot behind
        var snapshot = SnapshotWriter.Write(c, dir, team);

        var requests = new Dictionary<string, (string Id, string Path)>(StringComparer.Ordinal);
        foreach (var member in seen.Where(m => stop is null && recordOnly is null && m.Live && !m.IdleClean))
        {
            requests[member.Name] = CreateRequest(c, dir, team, member, quit);
        }

        var previous = c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = dir, ["team"] = teamName });
        var supersedes = new JsonArray();
        if (previous.Member("found") is { IsTruthy: true } && previous.Member("record")?.Member("members") is { IsArray: true } before)
        {
            foreach (var m in before.AsArray())
            {
                if (m.Member("request") is { IsText: true } id)
                {
                    supersedes.Add(id);
                }
            }
        }

        var proved = c.Proved();
        var orchestratorTree = WorkTreeAssignment.TreeOf(c, c.Cwd)?.Root;
        var orchestratorState = orchestratorTree is null ? null : ReleaseGate.TreeState(c, orchestratorTree);
        var orchestratorBranch = orchestratorTree is null ? null : BranchOf(c, orchestratorTree);
        var memberRecords = MemberRecords(seen, requests);
        var pool = c.ProgressPool;
        var rollup = pool + "/progress/" + c.ProgressTeam + "/rollup.md";
        var record = new JsonObject
        {
            ["ts"] = Timestamps.ToIsoString(now),
            ["team"] = teamName,
            ["by"] = new JsonObject { ["pid"] = proved.Pid, ["session"] = proved.SessionId is null ? JsonValue.Null : proved.SessionId },
            ["note"] = note,
            ["snapshot"] = snapshot.Get("snapshot") ?? JsonValue.Null,
            ["anchor"] = stop is null ? AnchorPath(c, dir, team) : AnchorPathIfReadable(c, dir, team),
            // per-dir-files-to-db S11d: the rollup is a row; its key is the path its file had
            ["rollup"] = c.Services.Hierarchy.StateCalls.Progress("get", new JsonObject { ["dir"] = pool, ["key"] = rollup }).Member("found") is { IsTruthy: true } ? rollup : JsonValue.Null,
            ["members"] = memberRecords,
            ["orchestrator"] = new JsonObject
            {
                ["session"] = proved.SessionId is null ? JsonValue.Null : proved.SessionId,
                ["pid"] = proved.Pid,
                ["pid_start"] = proved.PidStart is null ? JsonValue.Null : proved.PidStart,
                ["worktree"] = orchestratorTree is null ? JsonValue.Null : orchestratorTree,
                ["branch"] = orchestratorBranch is null ? JsonValue.Null : orchestratorBranch,
                ["head"] = orchestratorState?.Head is { } head ? head : JsonValue.Null,
            },
            ["supersedes"] = supersedes,
        };
        if (stop is not null)
        {
            record["stop"] = stop;
        }

        var written = c.Services.Hierarchy.StateCalls.Checkpoint(
            "write",
            new JsonObject { ["dir"] = dir, ["team"] = teamName, ["ts"] = StampOf(now), ["record"] = record });
        var path = MemberPlanning.JsConvertText(written.Member("path"));
        var resume = c.Services.Hierarchy.StateCalls.Progress(
            "rollup-resume-point",
            new JsonObject { ["dir"] = pool, ["team"] = c.ProgressTeam, ["path"] = path });

        var relay = new JsonArray();
        var mailFailed = false;
        var report = new JsonArray();
        foreach (var m in seen)
        {
            var entry = new JsonObject
            {
                ["name"] = m.Name,
                ["classification"] = !m.Live ? NotRunning : m.IdleClean ? IdleClean : Asked,
            };
            if (requests.TryGetValue(m.Name, out var asked))
            {
                entry["request"] = asked.Id;
                entry["request_path"] = asked.Path;
                try
                {
                    using var exempt = quit ? Ah.Mesh.MeshModule.BeginHoldExempt() : null;
                    var sent = c.Services.SendBrief(c.Cwd, m.Name, WorkTreeAssignment.TeamOf(c), asked.Path)
                        ?? throw new InvalidOperationException("the mail layer is not composed");
                    entry["entry"] = sent.EntryId is null ? JsonValue.Null : sent.EntryId;
                    entry["delivery"] = sent.DeliveryLine is null ? JsonValue.Null : sent.DeliveryLine;
                    if (sent.DeliveryLine is { } line && line.StartsWith("delivery: relay", StringComparison.Ordinal))
                    {
                        relay.Add(line);
                    }
                }
                catch (InvalidOperationException error)
                {
                    mailFailed = true;
                    entry["mail_error"] = error.Message;
                }
            }

            report.Add(entry);
        }

        var result = new JsonObject
        {
            ["team"] = teamName,
            ["record"] = path,
            ["snapshot"] = snapshot.Get("snapshot") ?? JsonValue.Null,
            ["rollup"] = resume.Member("appended") is { IsTruthy: true } ? resume.Member("rollup") ?? JsonValue.Null : JsonValue.Null,
            ["members"] = report,
            ["relay"] = relay,
            ["safe_to_restart"] = false,
            ["next"] = "ah roster checkpoint --status" + (team is null ? string.Empty : " --team " + team),
        };
        if (mailFailed)
        {
            result["mail_failed"] = true;
        }

        return result;
    }

    private static List<Seen> ReadMembers(RosterContext c, string? team, bool decide, bool recordWhatItCan)
    {
        var dir = c.Dir;
        var teamRecord = c.Services.Roster.ReadTeamRecord(dir, team);
        if (teamRecord.IsNull)
        {
            throw new RosterFailure($"there is no team {TeamLabel(team)} to checkpoint");
        }

        var cache = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        var rows = teamRecord.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [];
        try
        {
            using (decide ? c.Services.Hierarchy.Core.DecisionReads() : null)
            {
                var attributed = c.Services.Hierarchy.Roster.AttributedRoster(dir);
                return rows.Where(r => MemberPlanning.Text(r.Member("name")) is { Length: > 0 }).Select(r => See(c, dir, r, attributed, cache, recordWhatItCan)).ToList();
            }
        }
        catch (StoreUnavailableException error)
        {
            throw new RosterFailure($"the checkpoint was refused, nothing was written: {error.Message}");
        }
    }

    private static JsonArray MemberRecords(List<Seen> seen, Dictionary<string, (string Id, string Path)> requests)
    {
        var records = new JsonArray();
        foreach (var m in seen)
        {
            records.Add(new JsonObject
            {
                ["name"] = m.Name,
                ["role"] = m.Role,
                ["session"] = m.Session is null ? JsonValue.Null : m.Session,
                ["branch"] = m.Branch is null ? JsonValue.Null : m.Branch,
                ["worktree"] = m.Tree is null ? JsonValue.Null : m.Tree,
                ["head"] = m.Head is null ? JsonValue.Null : m.Head,
                ["classification"] = !m.Live ? NotRunning : m.IdleClean ? IdleClean : Asked,
                ["request"] = requests.TryGetValue(m.Name, out var asked) ? asked.Id : JsonValue.Null,
                ["open_requests"] = Strings(m.Open),
            });
        }

        return records;
    }

    /// <summary>What a record-only checkpoint would hold, read but not yet written: <see cref="Preview"/> is what a plan and the verify read.</summary>
    internal sealed record RecordOnly(List<Seen> Seen, JsonValue Preview);

    /// <summary>
    /// Whether the team's newest record, <paramref name="newest"/> (null when it has none), is behind a wind-down: an <c>active</c> chain-down row
    /// of the team is later than the record, or the team has such a row and no record. A held team's record is the hold's own, so it never is.
    /// </summary>
    internal static bool BehindWindDown(RosterContext c, string? team, JsonValue? newest)
    {
        if (c.Services.Roster.ReadTeamRecord(c.Dir, team).IsNull || c.Services.Roster.HoldOf(c.Dir, team) is not null)
        {
            return false;
        }

        DateTimeOffset? woundDown;
        try
        {
            using (c.Services.Hierarchy.Core.DecisionReads())
            {
                var rows = c.Services.Hierarchy.Core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "chain-down" }));
                woundDown = Ah.Roster.RosterModule.LastChainDownAt(rows, WithTeam(c, team, () => WorkTreeAssignment.TeamOf(c)));
            }
        }
        catch (StoreUnavailableException error)
        {
            throw new RosterFailure($"the checkpoint was refused, nothing was written: {error.Message}");
        }

        if (woundDown is null)
        {
            return false;
        }

        return newest is not { } record
            || (record.Member("ts") is { IsText: true } ts
                && DateTimeOffset.TryParse(ts.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var taken)
                && woundDown > taken);
    }

    /// <summary>The record-only checkpoint a resume of a team behind a wind-down takes, read now and written once ownership is settled; null when the team is not behind.</summary>
    internal static RecordOnly? ReadRecordOnly(RosterContext c, string? team, JsonValue? newest)
    {
        if (!BehindWindDown(c, team, newest))
        {
            return null;
        }

        var seen = ReadMembers(c, team, decide: true, recordWhatItCan: false);
        JsonValue anchor;
        try
        {
            using (c.Services.Hierarchy.Core.DecisionReads())
            {
                anchor = AnchorPath(c, c.Dir, team);
            }
        }
        catch (StoreUnavailableException error)
        {
            throw new RosterFailure($"the checkpoint was refused, nothing was written: {error.Message}");
        }

        return new RecordOnly(seen, JsonValue.FromObject(new JsonObject
        {
            ["team"] = team ?? "_default",
            ["anchor"] = anchor,
            ["members"] = MemberRecords(seen, new Dictionary<string, (string Id, string Path)>(StringComparer.Ordinal)),
        }));
    }

    internal static Seen See(
        RosterContext c, string dir, JsonValue row, JsonArray attributed, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> cache, bool recordWhatItCan = false)
    {
        var name = MemberPlanning.Text(row.Member("name"))!;
        var role = MemberPlanning.Text(row.Member("role")) ?? string.Empty;
        var live = ReleaseGate.IsLive(c, dir, row);
        var tree = ReleaseGate.WorkTree(c, row, name, true, attributed);
        var state = tree is null ? null : ReleaseGate.TreeState(c, tree);
        List<string> open = [];
        string? openError = null;
        try
        {
            open = tree is null && !live ? [] : OpenRequests(c, ReleaseGate.ExchangesFor(c, dir, tree, cache), name);
        }
        catch (StoreUnavailableException error) when (recordWhatItCan)
        {
            openError = error.Message; // an emergency stop still finishes, and says it could not read this rather than that nothing is open
        }

        var session = row.Member("session_id") is { IsText: true, IsTruthy: true } sid ? sid.AsString() : null;
        return new Seen(
            row, name, role, session, live, tree, tree is null || state?.Failure is not null ? null : BranchOf(c, tree), state?.Head,
            state?.Dirty ?? false, tree is null ? "no work tree" : state?.Failure, open, live && ReleaseGate.BusyNow(c, dir, row, name, attributed), openError);
    }

    internal static string? BranchOf(RosterContext c, string tree) =>
        ReleaseGate.Git(c, tree, "rev-parse", "--abbrev-ref", "HEAD") is { Ok: true } read ? read.Stdout.Trim() : null;

    /// <summary>The ids of the exchanges to or from <paramref name="name"/> that have no response; checkpoint requests are not among them.</summary>
    internal static List<string> OpenRequests(RosterContext c, List<JsonObject> exchanges, string name)
    {
        var open = new List<string>();
        foreach (var exchange in exchanges)
        {
            if (exchange.Get("response") is { IsMap: true }
                || exchange.Get("slug") is { IsText: true } slug && slug.AsString() == Slug
                || exchange.Get("request") is not { IsMap: true } request || request.Member("path") is not { IsText: true } path
                || exchange.Get("id") is not { IsText: true } id)
            {
                continue;
            }

            // ReadMsgFile answers null for any read failure; a request that still exists but cannot be read stays open (fail closed).
            var read = c.Services.Hierarchy.Core.ReadMsgFile(path.AsString());
            var fm = read.Member("fm");
            if (read.IsNull ? c.Services.Hierarchy.Core.MsgExists(path.AsString())
                : MemberPlanning.Text(fm?.Member("to_name")) == name || MemberPlanning.Text(fm?.Member("from_name")) == name)
            {
                open.Add(id.AsString());
            }
        }

        return open;
    }

    private static (string Id, string Path) CreateRequest(RosterContext c, string dir, string? team, Seen member, bool quit)
    {
        var options = new JsonObject
        {
            ["type"] = "request",
            ["to"] = member.Role,
            ["from"] = "orchestrator",
            ["slug"] = Slug,
            ["title"] = quit ? "Save work and stop (team quit)" : "Save work before restart",
            ["cwd"] = c.Cwd,
            ["toName"] = member.Name,
            ["fromName"] = JsonValue.Null,
            ["team"] = team is null ? JsonValue.Null : team,
            ["markerName"] = JsonValue.Null,
            ["paneName"] = JsonValue.Null,
            ["reqPath"] = JsonValue.Null,
            ["parent"] = JsonValue.Null,
            ["reason"] = JsonValue.Null,
            ["eta"] = JsonValue.Null,
        };
        var form = member.Tree is null ? ReleaseGate.NoTreeForm : ReleaseGate.IsReadOnlyClass(c, member.Role) ? ReleaseGate.ReadOnlyForm : null;
        if (quit)
        {
            options["checkpointAsk"] = "quit" + (form is null ? string.Empty : "-" + form);
        }
        else if (form is not null)
        {
            options["checkpointAsk"] = form;
        }

        var made = c.Services.Hierarchy.Messages.CreateMessage(dir, options);
        return (MemberPlanning.JsConvertText(made.Member("id")), MemberPlanning.JsConvertText(made.Member("path")));
    }

    private static JsonValue AnchorPathIfReadable(RosterContext c, string dir, string? team)
    {
        try
        {
            return AnchorPath(c, dir, team);
        }
        catch (StoreUnavailableException)
        {
            return JsonValue.Null;
        }
    }

    private static JsonValue AnchorPath(RosterContext c, string dir, string? team)
    {
        var anchor = c.Services.Hierarchy.Decisions.OpenRunAnchor(dir, team is null ? JsonValue.Null : team);
        if (anchor.Get("id") is not { IsText: true } id)
        {
            return JsonValue.Null;
        }

        foreach (var exchange in c.Services.Hierarchy.Messages.ListExchangesAllTeams(dir))
        {
            if (exchange.Member("id") is { IsText: true } have && have.AsString() == id.AsString()
                && exchange.Member("request")?.Member("path") is { IsText: true } path)
            {
                return path;
            }
        }

        return JsonValue.Null;
    }

    internal static string StampOf(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        array.AddRange(values.Select(v => (JsonValue)v));
        return array;
    }

    // ---------------------------------------------------------------- the status

    private sealed record Judged(string Name, string Status, string Detail, bool Safe)
    {
        internal JsonObject ToJson() => new() { ["name"] = Name, ["status"] = Status, ["detail"] = Detail, ["safe"] = Safe };
    }

    private static int RunStatus(RosterContext c, List<string?> teams)
    {
        var named = c.Opts.Positionals.Count == 1 ? c.Opts.Positionals[0] : null;
        if (named is not null && teams.Count != 1)
        {
            throw new RosterFailure("checkpoint --status: a record path names one team's record, so it cannot be combined with --all");
        }

        var results = new JsonArray();
        var safe = true;
        foreach (var team in teams)
        {
            var one = WithTeam(c, team, () => StatusOf(c, team, named));
            safe &= one.Get("safe_to_restart") is { IsTruthy: true };
            results.Add(one);
        }

        c.Out(new JsonObject { ["teams"] = results, ["safe_to_restart"] = safe });
        return safe ? 0 : 3;
    }

    /// <summary>The record whose key is <paramref name="key"/> (the absolute path its file had), or null (per-dir-files-to-db S11a-ckpt: <c>checkpoint.get</c>).</summary>
    internal static JsonValue? RecordAt(RosterContext c, string key) =>
        c.Services.Hierarchy.StateCalls.Checkpoint("get", new JsonObject { ["dir"] = c.Dir, ["key"] = key }) is var got && got.Member("found") is { IsTruthy: true }
            ? got.Member("record")
            : null;

    private static JsonObject StatusOf(RosterContext c, string? team, string? recordPath)
    {
        var dir = c.Dir;
        var teamName = team ?? "_default";
        string path;
        JsonValue record;
        if (recordPath is not null)
        {
            if (!Path.IsPathRooted(recordPath) || RecordAt(c, recordPath) is not { } found)
            {
                throw new RosterFailure($"checkpoint --status: no such record {recordPath} (give its absolute path)");
            }

            path = recordPath;
            record = found;
        }
        else
        {
            var read = c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = dir, ["team"] = teamName });
            if (read.Member("found") is not { IsTruthy: true })
            {
                return new JsonObject
                {
                    ["team"] = teamName,
                    ["record"] = JsonValue.Null,
                    ["safe_to_restart"] = false,
                    ["blockers"] = Strings([$"no checkpoint record for team {teamName}"]),
                };
            }

            path = MemberPlanning.JsConvertText(read.Member("path"));
            record = read.Member("record") ?? JsonValue.Null;
        }

        var teamRecord = c.Services.Roster.ReadTeamRecord(dir, team);
        var live = teamRecord.IsNull ? [] : (teamRecord.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : []);
        var attributed = c.Services.Hierarchy.Roster.AttributedRoster(dir);
        var cache = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        var members = new List<Judged>();
        if (record.Member("members") is { IsArray: true } recorded)
        {
            foreach (var entry in recorded.AsArray())
            {
                var name = MemberPlanning.Text(entry.Member("name")) ?? string.Empty;
                var row = live.Cast<JsonValue?>().FirstOrDefault(r => MemberPlanning.Text(r!.Value.Member("name")) == name);
                members.Add(Judge(c, dir, entry, name, row, attributed, cache));
            }
        }

        var orchestrator = JudgeOrchestrator(c, record.Member("orchestrator"));
        var blockers = members.Append(orchestrator).Where(j => !j.Safe).Select(j => $"{j.Name}: {j.Status}{(j.Detail.Length > 0 ? " (" + j.Detail + ")" : string.Empty)}").ToList();
        var memberJson = new JsonArray();
        memberJson.AddRange(members.Select(j => (JsonValue)j.ToJson()));
        return new JsonObject
        {
            ["team"] = teamName,
            ["record"] = path,
            ["ts"] = record.Member("ts") ?? JsonValue.Null,
            ["members"] = memberJson,
            ["orchestrator"] = orchestrator.ToJson(),
            ["safe_to_restart"] = blockers.Count == 0,
            ["blockers"] = Strings(blockers),
        };
    }

    private static Judged JudgeOrchestrator(RosterContext c, JsonValue? entry)
    {
        const string Name = "orchestrator";
        if (entry?.Member("worktree") is not { IsText: true } tree)
        {
            return new Judged(Name, "unknown", "the record names no work tree", false);
        }

        var state = ReleaseGate.TreeState(c, tree.AsString());
        if (state.Failure is { } failure)
        {
            return new Judged(Name, "unknown", failure, false);
        }

        return state.Dirty ? new Judged(Name, "dirty", "its work tree has uncommitted changes", false) : new Judged(Name, "clean", string.Empty, true);
    }

    /// <summary>How many commits of the member's tree are beyond the run's branch and the release ref; null when that cannot be read.</summary>
    internal static int? CommitsAheadOfRun(RosterContext c, string? memberTree, string? runTree)
    {
        if (memberTree is null || runTree is null)
        {
            return null;
        }

        var head = ReleaseGate.Git(c, runTree, "rev-parse", "HEAD");
        if (!head.Ok || head.Stdout.Trim() is not { Length: > 0 } sha)
        {
            return null;
        }

        var release = BranchHygiene.Release(c, runTree);
        string[] shipped = release is { Active: true, Ref: { } golden } ? [golden] : [];

        // A ship lands on the remote's ah/release-* branches too (ReleaseWatch.Tips); ah.releaseRef, when set, is the only release.
        string[] branches = BranchHygiene.Configured(c, runTree) is null ? ["--glob=refs/remotes/origin/ah/release-*"] : [];
        // b372: the whole-tree pathspec drops a commit whose tree equals its parent's, so an empty wip(resume) commit is not unreleased work
        var ahead = ReleaseGate.Git(c, memberTree, ["rev-list", "--count", "HEAD", "--not", sha, .. shipped, .. branches, "--", ":/"]);
        return ahead.Ok && int.TryParse(ahead.Stdout.Trim(), out var count) ? count : null;
    }

    private static Judged Judge(
        RosterContext c, string dir, JsonValue entry, string name, JsonValue? row, JsonArray attributed, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> cache)
    {
        var classification = MemberPlanning.Text(entry.Member("classification")) ?? string.Empty;
        var tree = MemberPlanning.Text(entry.Member("worktree"));
        var recordedHead = MemberPlanning.Text(entry.Member("head"));
        var live = row is { } r && ReleaseGate.IsLive(c, dir, r);
        var state = tree is null ? null : ReleaseGate.TreeState(c, tree);
        var busy = live && ReleaseGate.BusyNow(c, dir, row!.Value, name, attributed);

        if (!live || classification == NotRunning)
        {
            return Left(name, classification, entry, tree, state, ReadResponse(c, dir, entry, tree, cache));
        }

        if (classification == IdleClean)
        {
            if (state is null || state.Failure is { } why)
            {
                return new Judged(name, "stale", state?.Failure ?? "no work tree", false);
            }

            if (state.Dirty || busy)
            {
                return new Judged(name, "stale", state.Dirty ? "dirty" : "busy", false);
            }

            return recordedHead is not null && state.Head != recordedHead ? new Judged(name, "stale", "sha", false) : new Judged(name, IdleClean, string.Empty, true);
        }

        var answer = ReadResponse(c, dir, entry, tree, cache);
        if (answer.Response is null)
        {
            return busy ? new Judged(name, Asked, "no answer yet; the member is still working", false) : new Judged(name, "unanswered", "no answer yet and the member is idle", false);
        }

        if (answer.Answer is null)
        {
            return new Judged(name, "unanswered", "the response has no valid Checkpoint: line; expected under ## [1] status: " + ReleaseGate.ExpectedLine(c, ReleaseGate.CheckpointPrefix, tree, MemberPlanning.Text(entry.Member("role")) ?? string.Empty), false);
        }

        if (!answer.Answer.Ready)
        {
            return new Judged(name, "not-ready", answer.Answer.Reason, false);
        }

        if (busy)
        {
            return new Judged(name, "stale", "busy", false);
        }

        if (answer.Answer.Form == ReleaseGate.NoTreeForm)
        {
            return tree is null ? new Judged(name, "ready", "no-tree", true) : new Judged(name, "unanswered", ReleaseGate.HaveTreeText(c, tree), false);
        }

        if (answer.Answer.Form == ReleaseGate.ReadOnlyForm && !ReleaseGate.IsReadOnlyClass(c, MemberPlanning.Text(entry.Member("role")) ?? string.Empty))
        {
            return new Judged(name, "unanswered", ReleaseGate.CanCommitText, false);
        }

        if (state is null || state.Failure is { } failure)
        {
            return new Judged(name, "stale", state?.Failure ?? "no work tree", false);
        }

        if (answer.Answer.Form == ReleaseGate.ReadOnlyForm)
        {
            var head = state.Head!;
            return state.Dirty ? new Judged(name, "stale", "dirty", false) : new Judged(name, "ready", $"read-only {BranchOf(c, tree!) ?? "unknown"} {head[..Math.Min(7, head.Length)]}", true);
        }

        if (!state.Head!.StartsWith(answer.Answer.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return new Judged(name, "stale", "sha", false);
        }

        return state.Dirty ? new Judged(name, "stale", "dirty", false) : new Judged(name, "ready", answer.Answer.Sha[..Math.Min(7, answer.Answer.Sha.Length)], true);
    }

    /// <summary>A member that is no longer live is safe only if it was idle and clean, or answered ready, before it left, and its work tree is clean now.</summary>
    private static Judged Left(string name, string classification, JsonValue entry, string? tree, ReleaseGate.TreeReading? state, (string? Response, ReleaseGate.Answer? Answer) answer)
    {
        var wasReady = classification is IdleClean or NotRunning || answer.Answer is { Ready: true };
        if (!wasReady)
        {
            return new Judged(name, "gone", "it left without answering ready", false);
        }

        if (tree is null && answer.Answer?.Form == ReleaseGate.NoTreeForm)
        {
            return new Judged(name, "gone", string.Empty, true);
        }

        if (tree is null || state is null || state.Failure is not null)
        {
            return new Judged(name, "gone", state?.Failure ?? "no work tree to check", false);
        }

        return state.Dirty ? new Judged(name, "gone", "its work tree has uncommitted changes", false) : new Judged(name, "gone", string.Empty, true);
    }

    /// <summary>The response filed for the member's checkpoint request, and the answer line in it when it has a valid one.</summary>
    internal static (string? Response, ReleaseGate.Answer? Answer) ReadResponse(
        RosterContext c, string dir, JsonValue entry, string? tree, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> cache)
    {
        if (MemberPlanning.Text(entry.Member("request")) is not { Length: > 0 } id)
        {
            return (null, null);
        }

        foreach (var exchange in ReleaseGate.ExchangesFor(c, dir, tree, cache))
        {
            if (exchange.Get("id") is { IsText: true } have && have.AsString() == id
                && exchange.Get("response") is { IsMap: true } response && response.Member("path") is { IsText: true } path)
            {
                return (path.AsString(), ReleaseGate.ReadAnswer(c, path.AsString()));
            }
        }

        return (null, null);
    }
}
