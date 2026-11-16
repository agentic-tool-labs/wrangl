using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// Every input <see cref="HierLib.ComputeStatus"/> reads, by the question it asks (ah-msgs-fast-2 §3.8). The status logic runs once over a
/// source: <see cref="HierLib.FileStatusSource"/> asks the stores, which is how status entries are rebuilt, and the entry source answers from
/// persisted entries, which is how status is assembled. A source never decides a now-dependent rule; those stay in the status logic.
/// </summary>
internal interface IStatusSource
{
    /// <summary>The team names of status dir <paramref name="dir"/>, in listing order (<c>ListTeamNames</c>).</summary>
    IReadOnlyList<string> TeamNames(string dir);

    /// <summary>The team record <c>ReadTeamRecord(dir, team)</c> gives.</summary>
    JsonValue TeamRecord(string dir, string? team);

    /// <summary>Whether the team's Orchestrator is proven gone (<c>TeamLifeOf</c> is Orphaned); probes the process now.</summary>
    bool Orphaned(JsonValue team);

    /// <summary>The attributed peer roster of <paramref name="dir"/> (<c>AttributedRoster</c>).</summary>
    JsonArray Roster(string dir);

    /// <summary>The session-pid status rows of <paramref name="dir"/> (<c>SessionStatusRows</c>): the newest of each session, oldest first.</summary>
    IReadOnlyList<JsonValue> SessionRows(string dir);

    /// <summary>The exchanges the status lists for <paramref name="dir"/>: its live listing across every pool of every team.</summary>
    JsonArray Exchanges(string dir);

    /// <summary>The gate rows of <paramref name="dir"/> (<c>ReadGates</c>).</summary>
    JsonArray Gates(string dir);

    /// <summary>The request front matter of <paramref name="exchange"/>, an empty map when it cannot be read.</summary>
    JsonValue RequestFm(JsonValue exchange);

    /// <summary>The title of <paramref name="exchange"/> and where it came from (<c>ExchangeTitle</c>).</summary>
    (string? Title, string? Source) Title(JsonValue exchange);

    /// <summary>The modification time of the response file at <paramref name="path"/>, or null.</summary>
    double? ResponseMtime(string path);

    /// <summary>The status line the response at <paramref name="path"/> reports (<c>ExtractReport(...).Status</c> of its text), or null.</summary>
    string? ResponseStatus(string path);

    /// <summary>When each request was dispatched (<c>DispatchOrigin</c>).</summary>
    Dictionary<string, JsonValue> Origins(JsonArray requests);

    /// <summary>The activity record <c>ReadActivity(dir, file)</c> gives.</summary>
    JsonValue Activity(string dir, string file);

    /// <summary>The member's registration liveness, null when it has none; probes the process now.</summary>
    MemberRegistrationLiveness? Registration(string name, string team);

    /// <summary>The exchanges a run of <paramref name="team"/> counts rounds over (<c>ListRunExchanges</c> of every pool of every team).</summary>
    JsonArray RunExchanges(string dir, string? team, long sinceMs);

    /// <summary>The decisions of run <paramref name="anchorId"/> (<c>ReadDecisions</c>).</summary>
    JsonObject Decisions(string dir, string anchorId);

    /// <summary>The team tag <c>OpenExchanges</c> filters each exchange by: the stored team header, else the request's <c>team</c>.</summary>
    IReadOnlyList<JsonValue> TeamTags(IReadOnlyList<JsonValue> exchanges);

    /// <summary>
    /// The backlog health status dir <paramref name="dir"/> shows at <paramref name="nowMs"/>, or null. <paramref name="flip"/> is told
    /// each later instant at which the answer changes with the clock alone, and <paramref name="nowMs"/> itself when one cannot be named.
    /// </summary>
    BacklogHealth? Backlog(string dir, double nowMs, Action<double>? flip = null);

    /// <summary>Whether the hierarchy is enabled for <paramref name="cwd"/>: the config's <c>enabled</c> (was <c>_config.ResolveConfig(cwd)</c> in the compute).</summary>
    bool Enabled(string? cwd);

    /// <summary>Whether status dir <paramref name="dir"/> exists (was <c>Exists(dir)</c> in the compute).</summary>
    bool HasDir(string dir);

    /// <summary>The branch checked out in <paramref name="workTree"/>, or null (was <c>HierarchyModule.BranchOfWorkTree(workTree, ReadTextOrNull)</c> in the compute).</summary>
    string? Branch(string workTree);

    /// <summary>The checkout <paramref name="cwd"/> is in, or null (was <c>HierarchyModule.CheckoutOfCwd(cwd, stat)</c> in the compute).</summary>
    string? Checkout(string cwd);

    /// <summary>
    /// Whether the peer member <paramref name="name"/> of <paramref name="team"/> is live, and its session id (was <c>Attributed(roster, name,
    /// team, null, Registration)</c> in the compute): decided by its registration, else by its roster row, the process table and the clock.
    /// </summary>
    (bool Live, string? SessionId) MemberLife(JsonArray roster, JsonValue name, string? team);
}

internal sealed partial class HierLib
{
    /// <summary>The status inputs as the stores hold them now: the reads <see cref="ComputeStatus"/> has always made. One instance per status build (it caches request front matter by id).</summary>
    internal sealed class FileStatusSource(HierLib lib) : IStatusSource
    {
        private readonly Dictionary<string, JsonValue> _fm = new(StringComparer.Ordinal);

        public IReadOnlyList<string> TeamNames(string dir) => lib._roster.ListTeamNames(dir);

        public JsonValue TeamRecord(string dir, string? team) => lib._roster.ReadTeamRecord(dir, team);

        public bool Orphaned(JsonValue team) => lib._roster.TeamLifeOf(team) == Ah.Roster.TeamLife.Orphaned;

        public JsonArray Roster(string dir) => lib.AttributedRoster(dir);

        public IReadOnlyList<JsonValue> SessionRows(string dir) => [.. lib.SessionStatusRows(dir).Select(static r => (JsonValue)r)];

        public JsonArray Exchanges(string dir) => lib.ListLiveExchangesAllTeams(dir);

        public JsonArray Gates(string dir) => lib.ReadGates(dir);

        public JsonValue RequestFm(JsonValue exchange)
        {
            var id = Member(exchange, "id") is { IsText: true } t ? t.AsString() : JsConvert.ToJsString(Member(exchange, "id"));
            if (!_fm.TryGetValue(id, out var fm))
            {
                var parsed = lib._core.ReadMsgFile(Member(Member(exchange, "request"), "path").AsString());
                fm = parsed.Member("fm") is { IsMap: true } m ? m : new JsonObject();
                _fm[id] = fm;
            }

            return fm;
        }

        public (string? Title, string? Source) Title(JsonValue exchange) => lib.ExchangeTitle(exchange);

        public double? ResponseMtime(string path) => lib.MtimeMs(path);

        public string? ResponseStatus(string path) =>
            lib._core.ReadMsgFile(path).Member("text") is { IsText: true } text ? ProgressFormat.ExtractReport(text.AsString()).Status : null;

        public Dictionary<string, JsonValue> Origins(JsonArray requests) => lib.DispatchOrigin(requests);

        public JsonValue Activity(string dir, string file) => lib.ReadActivity(dir, file);

        public MemberRegistrationLiveness? Registration(string name, string team) => lib._core.Liveness?.ForMember(name, team);

        public JsonArray RunExchanges(string dir, string? team, long sinceMs) =>
            lib._core.ListRunExchanges(lib.PoolDirs(dir, lib._roster.ListTeamNames(dir), null), team, sinceMs);

        public JsonObject Decisions(string dir, string anchorId) => lib.ReadDecisions(lib.DecisionLogPath(dir, anchorId));

        public IReadOnlyList<JsonValue> TeamTags(IReadOnlyList<JsonValue> exchanges) => lib.StoredTeamTags([.. exchanges]);

        public BacklogHealth? Backlog(string dir, double nowMs, Action<double>? flip = null)
        {
            flip?.Invoke(nowMs);  // read from the stores at their own clock: no instant can be named
            return lib.BacklogHealthOf(dir);
        }

        public bool Enabled(string? cwd) => lib._config.ConfigEnabled(cwd);

        public bool HasDir(string dir) => lib.Exists(dir);

        public string? Branch(string workTree) => HierarchyModule.BranchOfWorkTree(workTree, lib.ReadTextOrNull);

        public string? Checkout(string cwd) => HierarchyModule.CheckoutOfCwd(cwd, p => lib._files.Stat(p) is not null);

        public (bool Live, string? SessionId) MemberLife(JsonArray roster, JsonValue name, string? team) => lib.MemberLifeOf(roster, name, team, Registration);
    }
}
