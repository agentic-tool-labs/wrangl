using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>
/// A view source with one live orchestrator (pid 101) and one team whose members, status rows, activity records, box entries and
/// links the test sets; what the model documents (detail, graph, actions and the read documents) are built from.
/// </summary>
internal sealed class ModelSource : IViewSource, IDetailReads
{
    internal const double Now = 1_800_000_000_000d;
    internal const string Dir = "/m/repo/.claude/ah2";
    internal const string File = "/m/repo/.claude/ah2/teams/t.json";

    /// <summary>The member rows of the status document, by member name.</summary>
    internal Dictionary<string, JsonObject> Status { get; } = [];

    internal Dictionary<string, ActivityHistory> Activity { get; } = [];

    /// <summary>Box entries by member name, oldest first.</summary>
    internal Dictionary<string, List<JsonObject>> Boxes { get; } = [];

    internal List<JsonObject> LinkRows { get; } = [];

    internal List<JsonObject> Dispatches { get; } = [];

    internal Dictionary<string, JsonObject> TeamRows { get; } = [];

    internal ModelSource Member(string name, string role = "implementor", string activity = "working", bool live = true, string? session = null)
    {
        TeamRows[name] = new JsonObject { ["name"] = name, ["role"] = role, ["model"] = "sonnet" };
        Status[name] = new JsonObject
        {
            ["name"] = name,
            ["role"] = role,
            ["label"] = name,
            ["kind"] = "claude",
            ["route"] = "peer",
            ["session_id"] = session ?? "sess-" + name,
            ["live"] = live,
            ["activity"] = activity,
            ["activity_at"] = "2026-06-02T10:00:00.000Z",
            ["blocked_by"] = JsonValue.Null,
            ["stream"] = JsonValue.Null,
            ["last_tool"] = "Read",
            ["last_tool_at"] = "2026-06-02T10:00:10.000Z",
            ["context_pct"] = 46,
            ["context_used"] = 92000,
        };
        return this;
    }

    /// <summary>The team's name; null makes it the default team, which has no links.</summary>
    internal string? TeamName { get; set; }

    /// <summary>The <c>pid_start</c> the team file records for the orchestrator, when set.</summary>
    internal string? PidStart { get; set; }

    /// <summary>The peers of the one stream <c>tui</c>, when set.</summary>
    internal List<string>? StreamPeers { get; set; }

    /// <summary>The path of the work tree the stream facts of <c>tui</c> carry, when set.</summary>
    internal string? StreamWorkTree { get; set; }

    /// <summary>The stream <c>tui</c> is recorded as done rather than open.</summary>
    internal bool StreamDone { get; set; }

    /// <summary>Adds a second stream <c>ops</c> beside <c>tui</c>.</summary>
    internal bool SecondStream { get; set; }

    /// <summary>What the read model has published, when set.</summary>
    internal Dictionary<string, JsonObject>? Published { get; set; }

    public IReadOnlyDictionary<string, JsonObject>? PublishedTeamSummaries() => Published;

    public IStreamRules Streams => new ModelStreams(this);

    private sealed class ModelStreams(ModelSource source) : IStreamRules
    {
        public IReadOnlyList<KeyValuePair<string, string>> GlyphStates { get; } = [];

        public JsonObject StreamsOf(JsonValue? team)
        {
            var streams = new JsonObject();
            if (source.StreamPeers is not null)
            {
                streams["tui"] = new JsonObject { ["state"] = source.StreamDone ? "done" : "open" };
                if (source.SecondStream)
                {
                    streams["ops"] = new JsonObject { ["state"] = "open" };
                }
            }

            return streams;
        }

        public JsonArray StreamMembers(JsonValue? team, string name) => new();

        public string StreamGlyph(string? state, JsonArray statuses, IReadOnlyList<string> owedTo) => "ok";

        public string StreamGlyphState(string? state, JsonArray statuses, IReadOnlyList<string> owedTo, bool statusKnown = true) => state == "done" ? "done" : "ok";

        public JsonArray StreamMemberStatuses(JsonArray members, JsonArray? agents, JsonValue? self = null) => new();

        public JsonObject StreamFacts(JsonValue record, JsonArray members, JsonArray statuses, JsonArray exchanges, JsonArray? agents)
        {
            var peers = new JsonArray();
            foreach (var p in source.StreamPeers ?? [])
            {
                peers.Add(new JsonObject { ["name"] = p, ["role"] = "implementor", ["status"] = "live", ["in_stream_tab"] = true });
            }

            var facts = new JsonObject { ["branch"] = "ah/tui", ["peers"] = peers, ["waiting_on"] = new JsonArray() };
            if (source.StreamWorkTree is not null)
            {
                facts["worktree"] = new JsonObject { ["path"] = source.StreamWorkTree, ["exists"] = true };
            }

            return facts;
        }

        public JsonArray OpenTeamExchanges(string dir, string? team, bool allTeams, bool created = false) => new();
    }

    public IReadOnlyList<string> RegisteredHierarchyDirs() => [.. WorkTreeDirs, Dir];

    /// <summary>Hierarchy dirs of work trees that list the same team files.</summary>
    internal List<string> WorkTreeDirs { get; } = [];

    /// <summary>The dirs a status document was computed for, one row per computation.</summary>
    internal List<string> StatusReads { get; } = [];

    /// <summary>Runs inside each status computation.</summary>
    internal Action? DuringStatus { get; set; }

    public bool DirExists(string path) => !MissingDirs.Contains(path);

    public string RealPath(string path) => path;

    /// <summary>Further named teams beside <see cref="TeamName"/>, with no members; each has its own team file and box directories.</summary>
    internal List<string> ExtraTeams { get; set; } = [];

    public IReadOnlyList<string> TeamNames(string dir) => [.. TeamName is null ? [] : (string[])[TeamName], .. ExtraTeams];

    /// <summary>The stream records of the team's document, when a test has lanes.</summary>
    internal JsonObject? LaneStreams { get; set; }

    /// <summary>The orchestrator block of each lane child team, by team name; such a team is read but not listed.</summary>
    internal Dictionary<string, JsonObject> LaneChildren { get; } = [];

    public JsonObject? ReadTeam(string dir, string? name)
    {
        if (TeamName is not null && name is null)
        {
            return null;
        }

        if (name is not null && LaneChildren.TryGetValue(name, out var held))
        {
            return new JsonObject { ["orchestrator"] = held, ["members"] = new JsonArray() };
        }

        var members = new JsonArray();
        foreach (var row in TeamRows.Values.Where(r => MemberTeam.GetValueOrDefault(r.Str("name")!) == (name is not null && ExtraTeams.Contains(name) ? name : null)).ToArray())
        {
            members.Add(row);
        }

        var orchestrator = new JsonObject { ["pid"] = 101, ["session_id"] = "sess-orch" };
        if (PidStart is not null)
        {
            orchestrator["pid_start"] = PidStart;
        }

        var doc = new JsonObject { ["orchestrator"] = orchestrator, ["members"] = members };
        if (LaneStreams is not null)
        {
            doc["streams"] = LaneStreams;
        }

        return doc;
    }

    public string TeamPath(string dir, string? name) => name is not null && ExtraTeams.Contains(name) ? $"/m/repo/.claude/ah2/teams/{name}.json" : File;

    /// <summary>The name of the directory the first team's boxes live in, which is also the team's key in view paths.</summary>
    internal string BoxesName { get; set; } = "boxes";

    public string BoxDirFor(string teamFile, string member) =>
        teamFile == File ? "/" + BoxesName + "/" + member : "/boxes-" + Path.GetFileNameWithoutExtension(teamFile) + "/" + member;

    private string BoxKey(string boxDir) => boxDir.StartsWith("/" + BoxesName + "/", StringComparison.Ordinal) ? boxDir[("/" + BoxesName + "/").Length..] : boxDir["/boxes-".Length..];

    public bool PidAlive(long pid) => pid == 101;

    public bool OrchestratorAlive(long pid, string? recordedStart) => pid == 101;

    /// <summary>How the process table stands on the recorded <c>pid_start</c>.</summary>
    internal Ah.Core.Time.StartMatch StartVerdict { get; set; } = Ah.Core.Time.StartMatch.Unknown;

    public Ah.Core.Time.StartMatch ProcessStartMatch(long pid, string recordedStart) => StartVerdict;

    public Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)> RegistrationIndex() => id => (4242, "/m/repo/.claude/worktrees/rc3-tui", RegisteredCwd);

    public IReadOnlyList<KeyValuePair<string, long>> LiveSessions(string dir) => [];

    public bool IsEnabled(string dir) => true;

    public string TeamState(JsonObject doc) => "live";

    /// <summary>The extra team a member belongs to; a member not listed is in <see cref="TeamName"/>'s team.</summary>
    internal Dictionary<string, string> MemberTeam { get; } = [];

    private JsonArray TeamsOf(JsonArray members, JsonArray dispatches)
    {
        JsonArray MembersOf(string? team)
        {
            var list = new JsonArray();
            foreach (var m in members.Where(m => MemberTeam.GetValueOrDefault(m.AsObject().Str("name")!) == team))
            {
                list.Add(m);
            }

            return list;
        }

        var teams = new JsonArray { new JsonObject { ["team"] = TeamName is null ? JsonValue.Null : TeamName, ["members"] = MembersOf(null), ["dispatches"] = dispatches } };
        foreach (var extra in ExtraTeams)
        {
            teams.Add(new JsonObject { ["team"] = extra, ["members"] = MembersOf(extra), ["dispatches"] = new JsonArray() });
        }

        return teams;
    }

    public JsonObject ComputeStatus(string dir, double nowMs)
    {
        lock (StatusReads)
        {
            StatusReads.Add(dir);
        }

        DuringStatus?.Invoke();
        var members = new JsonArray();
        foreach (var row in Status.Values)
        {
            members.Add(row);
        }

        var dispatches = new JsonArray();
        foreach (var d in Dispatches)
        {
            dispatches.Add(d);
        }

        return new JsonObject
        {
            ["enabled"] = true,
            ["member_sessions"] = new JsonArray(),
            ["teams"] = TeamsOf(members, dispatches),
        };
    }

    public string Clean(string text, int cap) => text;

    /// <summary>The unacknowledged entries of each box by its directory.</summary>
    internal Dictionary<string, int> UnackedByBox { get; } = [];

    public (int Unacked, int Overdue) BoxCounts(string boxDir, double nowMs) => (UnackedByBox.GetValueOrDefault(boxDir), 0);

    internal List<JsonObject> JoinRows { get; } = [];

    public IReadOnlyList<JsonObject> PendingJoins(string dir, string? team, double nowMs) => JoinRows;

    public JsonObject JoinDecisionCommands(string id) => new();

    public IReadOnlyList<JsonObject> Links(string dir, string team, double nowMs) => LinkRows;

    public double LinkLapseMs(JsonObject link) => 0;

    public string? MeshPolicy(JsonObject teamDoc) => null;

    public JsonObject Placement(JsonObject memberRow, string? transport) => new();

    public ActivityHistory? ActivityOf(string dir, string sessionId) => Activity.GetValueOrDefault(sessionId);

    /// <summary>The change stamp of each box by member name; a test moves one to say that box changed.</summary>
    internal Dictionary<string, string> BoxStamps { get; } = [];

    /// <summary>The text of request files by path; a file not listed cannot be read.</summary>
    internal Dictionary<string, string> Texts { get; } = [];

    /// <summary>Request file paths that lie outside the message store.</summary>
    internal HashSet<string> OutsideStore { get; } = [];

    public bool IsMessageFile(string path) => !OutsideStore.Contains(path);

    public FileText? RequestText(string path, int maxBytes)
    {
        if (!Texts.TryGetValue(path, out var text))
        {
            return null;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new FileText(System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, maxBytes)), bytes.Length > maxBytes);
    }

    /// <summary>How many times one entry was asked for with its request file read.</summary>
    internal int EntryReads { get; private set; }

    public JsonObject? BoxEntry(string boxDir, string id, bool index = false)
    {
        if (!index)
        {
            EntryReads++;
        }

        return Boxes.TryGetValue(BoxKey(boxDir), out var rows) ? rows.FirstOrDefault(r => r.Str("id") == id) : null;
    }

    /// <summary>The subagent runs the in-flight records hold.</summary>
    internal List<SubagentRun> Runs { get; } = [];

    internal int RunReads { get; private set; }

    public IReadOnlyList<SubagentRun> SubagentRuns(double nowMs)
    {
        RunReads++;
        return Runs;
    }

    /// <summary>The stamp of every file asked about (the team file); a test moves it to say the team file was rewritten.</summary>
    internal string FileStampText { get; set; } = string.Empty;

    public string FileStamp(string path) => FileStampText;

    public string BoxStamp(string boxDir) => BoxStamps.GetValueOrDefault(BoxKey(boxDir), string.Empty);

    /// <summary>How many times the entries were asked for with request files read, and without.</summary>
    internal int FullBoxReads { get; private set; }

    internal int IndexBoxReads { get; private set; }

    public IReadOnlyList<JsonObject> BoxEntries(string boxDir, int limit, bool index = false)
    {
        if (index)
        {
            IndexBoxReads++;
        }
        else
        {
            FullBoxReads++;
        }

        if (!Boxes.TryGetValue(BoxKey(boxDir), out var rows))
        {
            return [];
        }

        var taken = rows.TakeLast(limit);
        return index
            ? [.. taken.Select(static r => new JsonObject { ["id"] = r["id"], ["kind"] = r["kind"], ["from"] = r["from"], ["to"] = r["to"], ["created"] = r["created"], ["acked"] = r["acked"], ["ref"] = r.TryGetValue("ref", out var p) ? p : JsonValue.Null })]
            : [.. taken];
    }

    // ---- the reads of the orchestrator detail

    internal HashSet<string> MissingDirs { get; } = [];

    internal string? RegisteredCwd { get; set; }

    /// <summary>The peer-record rows; a row's <c>type</c> picks the readers that see it.</summary>
    internal List<JsonObject> Peers { get; } = [];

    internal bool PeersThrow { get; set; }

    public IReadOnlyList<JsonObject> PeerRows(IReadOnlySet<string> types) =>
        PeersThrow ? throw new IOException("peer records") : [.. Peers.Where(p => p.Str("type") is { } t && types.Contains(t))];

    internal List<DispatchCandidate> Candidates { get; } = [];

    public IReadOnlyList<DispatchCandidate> OpenDispatches(string sessionId, double nowMs) => sessionId == "sess-orch" ? Candidates : [];

    internal GitRunner? GitReader { get; set; }

    internal StatuslineTap? TapReader { get; set; }

    /// <summary>The text of small files by path (HEAD files and pointers).</summary>
    internal Dictionary<string, string> SmallTexts { get; } = [];

    internal List<string> TreePaths { get; } = [];

    internal bool TreesThrow { get; set; }

    internal JsonObject? Run { get; set; }

    internal List<string> UnavailableKeys { get; } = [];

    /// <summary>The progress records the plan document reads.</summary>
    internal IViewProgress? ProgressReader { get; set; }

    IViewProgress? IDetailReads.Progress => ProgressReader;

    GitRunner? IDetailReads.Git => GitReader;

    StatuslineTap? IDetailReads.Tap => TapReader;

    string? IDetailReads.ReadText(string path) => SmallTexts.GetValueOrDefault(path);

    IReadOnlyList<string> IDetailReads.Trees(string dir, IReadOnlyList<string> teams) =>
        TreesThrow ? throw new IOException("trees") : TreePaths;

    JsonObject? IDetailReads.PipelineRun(string dir, string? team) => Run;

    internal List<Ah.Hierarchy.ParkedQuestion> Parked { get; } = [];

    IReadOnlyList<Ah.Hierarchy.ParkedQuestion> IDetailReads.ParkedQuestions(string dir) => Parked;

    IReadOnlyList<string> IDetailReads.Unavailable => UnavailableKeys;

    internal JsonObject Build(string path) => ViewBuilder.Build(this, path, Now).Body;

    /// <summary>The view path of the only orchestrator, its team and a member of it.</summary>
    internal (string Orch, string Team, string Member) Paths(string member = "impl")
    {
        var orch = Build("/")["children"].AsArray()[0].Member("path")!.Value.AsString();
        var team = Build(orch)["children"].AsArray()[0].Member("path")!.Value.AsString();
        return (orch, team, team + "/m/" + member);
    }
}
