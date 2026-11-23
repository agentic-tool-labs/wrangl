using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>
/// The view models the daemon serves for <c>GET /v1/view</c>: presentation-free JSON for a client that looks at the whole
/// hierarchy from outside. Everything that came from a file or an agent passes through the one sanitizer before it enters a
/// view. The builder only reads, through <see cref="IViewSource"/>, and writes nothing. Key order is the oracle's.
/// </summary>
internal static partial class ViewBuilder
{
    internal const int ViewSchema = 1;

    private const int NameCap = 64;
    private const int TextCap = 200;
    private const int LinksCap = 50;

    /// <summary>What the views say about herdr's status when the caller has none.</summary>
    internal const string HerdrUnreachable = "herdr not reachable from the daemon";

    internal sealed record BuiltView(int Status, JsonObject Body);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z")]
    private static partial Regex MemberName();

    // ------------------------------------------------------------------ small helpers

    private static string Sha10(string p) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(p)))[..10];

    /// <summary><c>encodeURIComponent(s.toWellFormed())</c>.</summary>
    internal static string Seg(string s)
    {
        var sb = new StringBuilder();
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in s.EnumerateRunes())
        {
            var r = rune;
            if (r.IsAscii && (char.IsAsciiLetterOrDigit((char)r.Value) || "-_.!~*'()".Contains((char)r.Value, StringComparison.Ordinal)))
            {
                _ = sb.Append((char)r.Value);
                continue;
            }

            var n = r.EncodeToUtf8(bytes);
            for (var i = 0; i < n; i++)
            {
                _ = sb.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary><c>String(v)</c> for a JSON value as a template literal prints it.</summary>
    internal static string JsString(JsonValue v) => v.Kind switch
    {
        JsonKind.Null => "null",
        JsonKind.Bool => v.AsBool() ? "true" : "false",
        JsonKind.Number => JsonText.Stringify(v, 0),
        JsonKind.Text => v.AsString(),
        JsonKind.Array => string.Join(',', v.AsArray().Select(static x => x.Kind is JsonKind.Null ? string.Empty : JsString(x))),
        _ => "[object Object]",
    };

    private static bool Truthy(JsonValue v) => v.Kind switch
    {
        JsonKind.Null => false,
        JsonKind.Bool => v.AsBool(),
        JsonKind.Number => v.AsNumber() != 0 && !double.IsNaN(v.AsNumber()),
        JsonKind.Text => v.AsString().Length > 0,
        _ => true,
    };

    private static JsonValue Get(JsonObject o, string key) => o.TryGetValue(key, out var v) ? v : JsonValue.Null;

    private static bool Has(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind != JsonKind.Null;

    private static JsonValue Orn(JsonValue v) => v.Kind == JsonKind.Null ? JsonValue.Null : v;

    private static JsonValue IsoOf(double ms) => double.IsNaN(ms) || double.IsInfinity(ms)
        ? JsonValue.Null
        : Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));

    private static JsonValue AgeSec(string? iso, double nowMs)
    {
        if (iso is null || !Timestamps.TryParseMs(iso, out var t))
        {
            return JsonValue.Null;
        }

        return Math.Max(0, Math.Floor(((nowMs - t) / 1000) + 0.5));
    }

    private static int CompareOrdinal(string a, string b) => string.CompareOrdinal(a, b);

    // ------------------------------------------------------------------ the builder's working state

    private sealed class Orch
    {
        internal required string Oid { get; init; }

        internal required string Dir { get; init; }

        internal long? Pid { get; init; }

        internal bool Live { get; init; }

        internal required List<TeamEntry> Teams { get; init; }

        /// <summary>A live session of an ah2-enabled directory that has no Team in it yet: listed from the session-pid record, not a team file.</summary>
        internal bool NoTeam { get; init; }
    }

    private sealed class TeamEntry
    {
        internal string? Name { get; init; }

        internal required string Key { get; init; }

        internal required string Tkey { get; init; }

        internal required string File { get; init; }

        internal required string RealFile { get; init; }

        /// <summary>The ordinal-first hierarchy directory that resolves to this team file.</summary>
        internal required string Dir { get; set; }

        internal required JsonObject Doc { get; init; }
    }

    private sealed class Facts
    {
        internal required List<JsonObject> Members { get; init; }

        internal int Skipped { get; init; }

        internal required List<JsonObject> Dispatches { get; init; }

        internal required List<JsonObject> Open { get; init; }

        internal required Dictionary<string, string> LastReport { get; init; }

        internal required Dictionary<string, (int Unacked, int Overdue)> Boxes { get; init; }

        internal required JsonObject Summary { get; init; }

        internal required Work Work { get; init; }

        internal double Truncated { get; init; }
    }

    private sealed class StreamRow
    {
        internal required string Name { get; init; }

        internal required JsonObject Rec { get; init; }

        internal required string GlyphState { get; init; }

        internal required JsonObject FactsOf { get; init; }
    }

    private sealed class Work
    {
        internal required List<StreamRow> Streams { get; init; }

        internal required List<JsonObject> Links { get; init; }

        internal int LinksTruncated { get; init; }

        internal required IReadOnlyList<JsonObject> Joins { get; init; }

        internal required Dictionary<string, JsonValue> CreatedOf { get; init; }

        internal string? Reason { get; init; }

        internal int Stuck { get; init; }
    }

    private static JsonObject Zero() => new()
    {
        ["teams"] = 0,
        ["members"] = 0,
        ["live"] = 0,
        ["working"] = 0,
        ["blocked"] = 0,
        ["owed"] = 0,
        ["ctx_high"] = 0,
        ["overdue"] = 0,
        ["joins_pending"] = 0,
        ["streams_stuck"] = 0,
    };

    private static JsonObject AddSummary(JsonObject a, JsonObject b)
    {
        foreach (var key in a.Keys.ToList())
        {
            a[key] = a[key].AsNumber() + (b.TryGetValue(key, out var v) && v.Kind == JsonKind.Number ? v.AsNumber() : 0);
        }

        return a;
    }

    // ------------------------------------------------------------------ discovery

    private static List<Orch> Discover(IViewSource src, IReadOnlyList<string> extraDirs, Func<string, JsonObject> statusOf)
    {
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        void Add(string? d)
        {
            if (!string.IsNullOrEmpty(d) && src.DirExists(d))
            {
                var real = src.RealPath(d);
                if (!dirs.ContainsKey(real))
                {
                    order.Add(real);
                }

                dirs[real] = d;
            }
        }

        foreach (var d in src.RegisteredHierarchyDirs())
        {
            Add(d);
        }

        foreach (var d in extraDirs)
        {
            Add(d);
        }

        var teams = new List<TeamEntry>();
        var byFile = new Dictionary<string, TeamEntry>(StringComparer.Ordinal);
        var foundIn = new Dictionary<string, List<(string RealDir, string Dir)>>(StringComparer.Ordinal);
        foreach (var realDir in order)
        {
            var dir = dirs[realDir];
            foreach (var name in new string?[] { null }.Concat(src.TeamNames(dir)))
            {
                var file = src.TeamPath(dir, name);
                var realFile = src.RealPath(file);
                if (byFile.ContainsKey(realFile))
                {
                    foundIn[realFile].Add((realDir, dir));
                    continue;
                }

                var doc = src.ReadTeam(dir, name);
                if (doc is null)
                {
                    continue;
                }

                foundIn[realFile] = [(realDir, dir)];

                var team = new TeamEntry
                {
                    Name = name,
                    Key = name ?? "(default)",
                    Tkey = Path.GetFileName(Path.GetDirectoryName(src.BoxDirFor(file, "x")))!,
                    File = file,
                    RealFile = realFile,
                    Dir = dir,
                    Doc = doc,
                };
                byFile[realFile] = team;
                teams.Add(team);
            }
        }

        // A team is read through the directory its team file lives in, when that directory was seen; else the ordinal-first one.
        foreach (var team in teams)
        {
            var found = foundIn[team.RealFile];
            var own = Path.GetDirectoryName(Path.GetDirectoryName(team.RealFile));
            team.Dir = found.FindIndex(f => f.RealDir == own) is >= 0 and var at
                ? found[at].Dir
                : found.Select(static f => f.Dir).Min(StringComparer.Ordinal)!;
        }

        // One row per orchestrator, which is a pid with the start it recorded; a team with no recorded pid is its own row, and a team
        // with no recorded start (an older record) is the group of its pid that has none.
        var groups = new List<(long? Pid, string? Start, List<TeamEntry> Teams)>();
        foreach (var team in teams)
        {
            long? pid = team.Doc.Obj("orchestrator") is { } o && o.Int("pid") is > 0 and var p ? p : null;
            var start = pid is null ? null : team.Doc.Obj("orchestrator")?.Str("pid_start") is { Length: > 0 } recorded ? recorded : null;
            var group = pid is null ? -1 : groups.FindIndex(g => g.Pid == pid && g.Start == start);
            if (group < 0)
            {
                groups.Add((pid, start, [team]));
            }
            else
            {
                groups[group].Teams.Add(team);
            }
        }

        // a member's session is shown under its team, whichever directory its own session-pid row landed in
        var live = order.Select(r => dirs[r]).Distinct(StringComparer.Ordinal).ToDictionary(static d => d, src.LiveSessions, StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        // Each team is read through one dir, so the status of that dir names its members; a status of every other dir that lists the team repeats it,
        // and with dozens of work trees listing one team that repetition was nine tenths of a build.
        foreach (var teamDir in teams.Select(static t => t.Dir).Distinct(StringComparer.Ordinal))
        {
            if (statusOf(teamDir).Get("member_sessions") is { IsArray: true } memberSessions)
            {
                foreach (var m in memberSessions.AsArray())
                {
                    if (m.IsText)
                    {
                        _ = members.Add(m.AsString());
                    }
                }
            }
        }

        // a pid whose live session has no team: it takes the bare id over a team group at that pid that is not live
        var teamless = new HashSet<long>();
        foreach (var realDir in order)
        {
            var dir = dirs[realDir];
            if (live[dir].Count > 0 && src.IsEnabled(dir))
            {
                foreach (var (sessionId, sessionPid) in live[dir])
                {
                    if (!members.Contains(sessionId))
                    {
                        _ = teamless.Add(sessionPid);
                    }
                }
            }
        }

        var alive = groups.Select(g => g.Pid is not null && src.OrchestratorAlive(g.Pid.Value, g.Start)).ToList();
        var output = new List<Orch>();
        for (var i = 0; i < groups.Count; i++)
        {
            var (pid, start, grouped) = groups[i];
            var ordered = grouped.OrderBy(static t => t.Key, StringComparer.Ordinal).ToList();
            string oid;
            if (pid is null)
            {
                oid = $"o.none.{Sha10(ordered[0].RealFile)}";
            }
            else
            {
                // The bare id goes to the live identity at the pid: the group whose start matches the live process, else the
                // legacy group while the pid is alive, else the only group of the pid. A legacy group has no start to hash.
                var siblings = Enumerable.Range(0, groups.Count).Where(j => groups[j].Pid == pid).ToList();
                var primary = siblings.FirstOrDefault(j => alive[j] && groups[j].Start is not null, -1) is >= 0 and var matching ? matching
                    : siblings.FirstOrDefault(j => alive[j] && groups[j].Start is null, -1) is >= 0 and var legacy ? legacy
                    : siblings.Count == 1 && !teamless.Contains(pid.Value) ? siblings[0]
                    : -1;
                var bare = $"o.{pid.Value.ToString(CultureInfo.InvariantCulture)}";
                oid = primary == i ? bare : start is null ? $"{bare}.legacy" : $"{bare}.{Sha10(start)[..8]}";
            }

            output.Add(new Orch
            {
                Oid = oid,
                Dir = ordered[0].Dir,
                Pid = pid,
                Live = alive[i],
                Teams = ordered,
            });
        }

        // a live session of an enabled directory that no live team row accounts for: one row per session pid, wherever it was recorded
        var taken = output.Where(static o => o.Live && o.Pid is not null).Select(static o => o.Pid!.Value).ToHashSet();

        foreach (var realDir in order)
        {
            var dir = dirs[realDir];
            var sessions = live[dir];
            if (sessions.Count == 0 || !src.IsEnabled(dir))
            {
                continue;
            }

            foreach (var (sessionId, pid) in sessions)
            {
                if (members.Contains(sessionId) || !taken.Add(pid))
                {
                    continue;
                }

                output.Add(new Orch
                {
                    Oid = $"o.{pid.ToString(CultureInfo.InvariantCulture)}",
                    Dir = dir,
                    Pid = pid,
                    Live = true,
                    Teams = [],
                    NoTeam = true,
                });
            }
        }

        return [.. output.OrderBy(static o => o.Dir, StringComparer.Ordinal).ThenBy(static o => o.Pid ?? 0).ThenBy(static o => o.Oid, StringComparer.Ordinal)];
    }

    // ------------------------------------------------------------------ per-team data

    private static string? Text(IViewSource src, JsonValue v, int cap = TextCap)
    {
        var raw = v.Kind == JsonKind.Text ? v.AsString() : v.Kind == JsonKind.Null ? string.Empty : JsString(v);
        var cleaned = src.Clean(raw, cap);
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    private static JsonValue TextOrNull(IViewSource src, JsonValue v, int cap = TextCap) =>
        Text(src, v, cap) is { } t ? t : JsonValue.Null;

    private static JsonValue TextOrNull(IViewSource src, string? v, int cap = TextCap) =>
        v is null ? JsonValue.Null : TextOrNull(src, JsonValue.FromString(v), cap);

    /// <summary>The streams of a team and what they are derived from: the stream names, herdr's reading, its agents, the team's exchanges and the stream rows.</summary>
    private static (List<string> Names, HerdrReading Reading, JsonArray? Agents, JsonArray Exchanges, List<StreamRow> Rows) ReadStreams(
        IViewSource src, TeamEntry team, Func<HerdrReading> herdr)
    {
        var doc = team.Doc;
        var herdrTeam = doc.Str("transport") == "herdr";
        using var streamsTimer = ViewTiming.Section("streams");
        var streams = src.Streams.StreamsOf(JsonValue.FromObject(doc));
        var names = streams.Keys.Where(n => n.Length > 0 && streams[n].Kind == JsonKind.Map).ToList();
        var reading = herdrTeam && names.Count > 0 ? herdr() : new HerdrReading(null, null);
        var agents = herdrTeam ? reading.Agents : null;
        var statusKnown = !herdrTeam || agents is not null;
        var exchanges = names.Count > 0 ? src.OpenExchanges(team.Dir, team.Name, created: true) : new JsonArray();
        var owedTo = exchanges.Select(static e => e.Kind == JsonKind.Map && e.AsObject().Str("to_name") is { } n ? n : string.Empty).ToList();
        var streamRows = new List<StreamRow>();
        foreach (var name in names)
        {
            var rec = streams[name].AsObject();
            var members = src.Streams.StreamMembers(JsonValue.FromObject(doc), name);
            var statuses = src.Streams.StreamMemberStatuses(members, agents);
            var glyph = src.Streams.StreamGlyphState(rec.Str("state"), statuses, owedTo, statusKnown);
            streamRows.Add(new StreamRow
            {
                Name = name,
                Rec = rec,
                GlyphState = glyph,
                FactsOf = src.Streams.StreamFacts(JsonValue.FromObject(rec), members, statuses, exchanges, agents),
            });
        }

        return (names, reading, agents, exchanges, streamRows);
    }

    private static Work OpenWork(IViewSource src, Orch orch, TeamEntry team, Func<HerdrReading> herdr, double nowMs)
    {
        var doc = team.Doc;
        var herdrTeam = doc.Str("transport") == "herdr";
        var (names, reading, agents, exchanges, streamRows) = ReadStreams(src, team, herdr);
        var links = new List<JsonObject>();
        if (team.Name is not null)
        {
            using var linksTimer = ViewTiming.Section("links");
            try
            {
                links.AddRange(src.Links(team.Dir, team.Name, nowMs));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or JsonParseException)
            {
                // an unreadable links file reads as no links
            }
        }

        static bool IsActive(JsonObject l) => l.Str("state") == "active";
        var linkRows = links
            .Where(static l => l.Str("id") is not null)
            .OrderBy(static l => l, Comparer<JsonObject>.Create((a, b) =>
            {
                if (IsActive(a) == IsActive(b))
                {
                    var ga = JsString(Get(a, "granted_at") is { Kind: not JsonKind.Null } x ? x : JsonValue.FromString("undefined"));
                    var gb = JsString(Get(b, "granted_at") is { Kind: not JsonKind.Null } y ? y : JsonValue.FromString("undefined"));
                    return string.CompareOrdinal(gb, ga) < 0 ? -1 : string.CompareOrdinal(gb, ga) > 0 ? 1 : 0;
                }

                return IsActive(a) ? -1 : 1;
            }))
            .ToList();
        var created = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var e in exchanges)
        {
            if (e.Kind == JsonKind.Map && e.AsObject().Str("id") is { } id)
            {
                created[id] = Get(e.AsObject(), "created");
            }
        }

        return new Work
        {
            Streams = streamRows,
            Links = linkRows.Take(LinksCap).ToList(),
            LinksTruncated = Math.Max(0, linkRows.Count - LinksCap),
            Joins = src.PendingJoins(team.Dir, team.Name, nowMs),
            CreatedOf = created,
            Reason = herdrTeam && agents is null && names.Count > 0 ? Text(src, reading.Reason is null ? JsonValue.Null : JsonValue.FromString(reading.Reason)) ?? HerdrUnreachable : null,
            Stuck = streamRows.Count(static r => r.GlyphState == "blocked"),
        };
    }

    private static JsonValue RemainingMin(IViewSource src, JsonObject link, double nowMs)
    {
        if (link.Str("state") != "active" || link.Obj("limits") is not { } limits || !limits.TryGetValue("minutes", out var m) || m.Kind != JsonKind.Number)
        {
            return JsonValue.Null;
        }

        var lapse = src.LinkLapseMs(link);
        return double.IsFinite(lapse) ? Math.Max(0, Math.Ceiling((lapse - nowMs) / 60000)) : JsonValue.Null;
    }

    private static string StateAt(JsonObject d, double ms)
    {
        JsonObject? current = null;
        if (d.TryGetValue("states", out var statesValue) && statesValue.Kind == JsonKind.Array)
        {
            var states = WatchJson.Objects(statesValue.AsArray());
            current = states.Count > 0 ? states[0] : null;
            foreach (var s in states)
            {
                if (Liveness.TimeOrNaN(s, "at") <= ms)
                {
                    current = s;
                }
            }
        }

        return current is not null && current.Str("state") is { } st ? st : current is not null ? JsString(Get(current, "state")) : "working";
    }

    /// <summary>
    /// Whether a member is blocked, for the summary's <c>blocked</c> count and the Graph alike: something blocks it, or its activity is blocked or
    /// failed. A member waiting on its own job (<c>blocked-on-job</c>) is not: nobody has to act for it.
    /// </summary>
    internal static bool IsBlocked(JsonObject m) => Truthy(Get(m, "blocked_by")) || m.Str("activity") is "blocked" or "failed";

    /// <summary>
    /// A member as the web reads it: <paramref name="m"/> with its <c>live</c>, <c>activity</c> and <c>activity_at</c> from the published
    /// <c>member_states</c> entry. A member that entry does not hold keeps what it has, and so does a liveness of <c>unknown</c>: that is no verdict,
    /// and the entry can lag a new member's registration that the status row has already read.
    /// </summary>
    internal static void ApplyPublishedState(JsonObject m, JsonObject? states, string dir, string team)
    {
        if (states is null || m.Str("name") is not { } name || states.Obj(Model.TreeIds.Member(dir, team, name)) is not { } state)
        {
            return;
        }

        var word = state.Str("state");
        if (state.Str("liveness") is "live" or "proven_dead")
        {
            m["live"] = state.Str("liveness") == "live";
        }

        m["activity"] = word switch
        {
            "gone" or null => "unknown",
            "blocked" when m.Str("activity") == "failed" => "failed",
            _ => word,
        };
        if (word != "blocked")
        {
            m["blocked_by"] = JsonValue.Null;
        }

        if (state.Str("state_at") is { } at)
        {
            m["activity_at"] = at;
        }

        if (state.Int("context_pct") is { } contextPct)
        {
            m["context_pct"] = contextPct;
        }
    }

    /// <summary>The team's stuck streams; none for a team its published summary says has ended, as that summary's own stuck count does.</summary>
    internal static int StreamsStuck(int stuck, JsonObject? teamSummary) => teamSummary?.Str("state") == "ended" ? 0 : stuck;

    private static Facts TeamFacts(IViewSource src, Orch orch, TeamEntry team, Dictionary<string, JsonObject> statusByDir, IReadOnlyDictionary<string, JsonObject>? published, double nowMs, Func<HerdrReading> herdr)
    {
        var status = statusByDir[team.Dir];
        var statusTeam = StatusTeamOf(status, team);
        var teamSummary = team.Name is { } teamName && published is not null
            && published.TryGetValue("team-summary:" + Model.ActivityLane.DirId(team.Dir) + "/" + teamName, out var served)
            ? served
            : null;
        var memberStates = teamSummary?.Obj("member_states");

        var rows = team.Doc.TryGetValue("members", out var membersValue) && membersValue.Kind == JsonKind.Array
            ? WatchJson.Objects(membersValue.AsArray())
            : (IReadOnlyList<JsonObject>)[];
        // The roster is the team file, as the web reads it; the status document's row only adds what it knows of a member on that roster.
        var statusRows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (statusTeam is not null && statusTeam.TryGetValue("members", out var sm) && sm.Kind == JsonKind.Array)
        {
            foreach (var m in WatchJson.Objects(sm.AsArray()))
            {
                if (m.Str("name") is { } statusName)
                {
                    _ = statusRows.TryAdd(statusName, m);
                }
            }
        }

        var everyone = new List<JsonObject>();
        foreach (var r in rows.Where(static r => r.Str("name") is not null))
        {
            JsonObject m;
            if (statusRows.TryGetValue(src.Clean(r.Str("name")!, NameCap), out var known))
            {
                m = new JsonObject();
                foreach (var key in known.Keys)
                {
                    m[key] = known[key];
                }

                m["row"] = r;
            }
            else
            {
                m = new JsonObject
                {
                    ["name"] = src.Clean(r.Str("name")!, NameCap),
                    ["role"] = src.Clean(JsStringOrEmpty(r, "role"), NameCap),
                    ["kind"] = src.Clean(JsStringOrEmpty(r, "kind"), NameCap) is { Length: > 0 } k ? k : "claude",
                    ["route"] = Truthy(Get(r, "route")) ? Get(r, "route") : "peer",
                    ["live"] = false,
                    ["activity"] = "unknown",
                    ["activity_at"] = JsonValue.Null,
                    ["blocked_by"] = JsonValue.Null,
                    ["row"] = r,
                };
            }

            if (team.Name is { } stateTeam)
            {
                ApplyPublishedState(m, memberStates, team.Dir, stateTeam);
            }

            everyone.Add(m);
        }

        // A member whose name is not a plain, unique name could not be addressed by a path: it is left out and counted.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<JsonObject>();
        foreach (var m in everyone)
        {
            if (m.Str("name") is { } n && MemberName().IsMatch(n) && seen.Add(n))
            {
                members.Add(m);
            }
        }

        var skipped = everyone.Count - members.Count;
        var dispatches = statusTeam is not null && statusTeam.TryGetValue("dispatches", out var dv) && dv.Kind == JsonKind.Array
            ? [.. WatchJson.Objects(dv.AsArray())]
            : new List<JsonObject>();
        var open = dispatches.Where(static d => d.TryGetValue("reported_at", out var r) && r.Kind == JsonKind.Null).ToList();
        var lastReport = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in dispatches)
        {
            if (d.Str("reported_at") is { Length: > 0 } at && d.Str("member") is { Length: > 0 } member)
            {
                lastReport[member] = !lastReport.TryGetValue(member, out var existing) || string.CompareOrdinal(at, existing) > 0 ? at : existing;
            }
        }

        var overdue = 0;
        var boxes = new Dictionary<string, (int Unacked, int Overdue)>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            var unacked = 0;
            var late = 0;
            if (m.Str("name") is { Length: > 0 } name)
            {
                try
                {
                    (unacked, late) = src.BoxCounts(src.BoxDirFor(team.File, name), nowMs);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // an unreadable box counts as empty
                    unacked = 0;
                    late = 0;
                }
            }

            boxes[m.Str("name")!] = (unacked, late);
            overdue += late;
        }

        var work = OpenWork(src, orch, team, herdr, nowMs);
        var summary = new JsonObject
        {
            ["teams"] = 1,
            ["members"] = members.Count,
            ["live"] = members.Count(static m => m.TryGetValue("live", out var l) && l.Kind == JsonKind.Bool && l.AsBool()),
            ["working"] = members.Count(static m => m.Str("activity") == "working"),
            ["blocked"] = members.Count(IsBlocked),
            ["owed"] = open.Count,
            ["ctx_high"] = members.Count(static m => m.Int("context_pct") is { } p && p >= ContextAlerts.Warn),
            ["overdue"] = overdue,
            ["joins_pending"] = work.Joins.Count,
            ["streams_stuck"] = StreamsStuck(work.Stuck, teamSummary),
        };
        var truncated = statusTeam is not null && statusTeam.TryGetValue("dispatches_truncated", out var tr) && tr.Kind == JsonKind.Number ? tr.AsNumber() : 0;
        return new Facts
        {
            Members = members,
            Skipped = skipped,
            Dispatches = dispatches,
            Open = open,
            LastReport = lastReport,
            Boxes = boxes,
            Summary = summary,
            Work = work,
            Truncated = truncated,
        };
    }

    private static string JsStringOrEmpty(JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind != JsonKind.Null ? JsString(v) : string.Empty;

    private static string RepoName(string dir)
    {
        var path = Ah.Core.Text.AhPaths.RepoRootOf(dir.TrimEnd('/')) ?? dir;
        return Path.GetFileName(path.TrimEnd('/'));
    }

    // ------------------------------------------------------------------ nodes

    private static JsonObject Crumb(string kind, string id, string label, string path) =>
        new() { ["kind"] = kind, ["id"] = id, ["label"] = label, ["path"] = path };

    private static JsonObject Envelope(string kind, string id, string path, JsonArray breadcrumb, double now) =>
        new()
        {
            ["view_schema"] = ViewSchema,
            ["kind"] = kind,
            ["id"] = id,
            ["path"] = path,
            ["breadcrumb"] = breadcrumb,
            ["at"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
        };

    private static JsonArray Arr(params JsonValue[] items)
    {
        var a = new JsonArray();
        foreach (var i in items)
        {
            a.Add(i);
        }

        return a;
    }

    /// <summary>What one build reads once: the status of each directory, the discovered orchestrators and each team's facts.</summary>
    private sealed class BuildSession
    {
        private readonly IViewSource _src;
        private readonly double _now;
        private readonly Func<HerdrReading> _herdr;
        private readonly Dictionary<string, JsonObject> _statusByDir = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Facts> _factsCache = new(StringComparer.Ordinal);
        private readonly Lazy<IReadOnlyDictionary<string, JsonObject>?> _published;

        internal BuildSession(IViewSource src, double now, IReadOnlyList<string>? extraDirs, Func<HerdrReading>? herdr)
        {
            _src = src;
            _published = new(src.PublishedTeamSummaries);
            _now = now;
            _herdr = herdr ?? (static () => new HerdrReading(null, HerdrUnreachable));
            Registrations = new Lazy<Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)>>(src.RegistrationIndex);
            using (ViewTiming.Section("discover"))
            {
                Orchs = Discover(src, extraDirs ?? [], StatusOf);
            }

            foreach (var o in Orchs)
            {
                foreach (var t in o.Teams)
                {
                    _ = StatusOf(t.Dir);
                }
            }
        }

        internal Lazy<Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)>> Registrations { get; }

        internal List<Orch> Orchs { get; }

        internal JsonObject StatusOf(string dir)
        {
            if (!_statusByDir.TryGetValue(dir, out var status))
            {
                using (ViewTiming.Section("compute-status"))
                {
                    status = _src.ComputeStatus(dir, _now);
                }

                _statusByDir[dir] = status;
            }

            return status;
        }

        internal Facts FactsOf(Orch o, TeamEntry t)
        {
            var k = $"{o.Oid}|{t.Tkey}";
            if (!_factsCache.TryGetValue(k, out var f))
            {
                f = TeamFacts(_src, o, t, _statusByDir, _published.Value, _now, _herdr);
                _factsCache[k] = f;
            }

            return f;
        }
    }

    /// <summary>
    /// The view at <paramref name="path"/>: 200 with the node view, or 404 with <c>{view_schema, error: "not_found", path, nearest}</c>
    /// where <c>nearest</c> is the deepest valid ancestor. Reads only.
    /// </summary>
    internal static BuiltView Build(
        IViewSource src, string path, double now, IReadOnlyList<string>? extraDirs = null, Func<HerdrReading>? herdr = null)
    {
        ArgumentNullException.ThrowIfNull(src);
        return Build(src, path, now, new BuildSession(src, now, extraDirs, herdr));
    }

    /// <summary>The view at <paramref name="path"/> from the facts <paramref name="buildSession"/> has read and keeps reading.</summary>
    private static BuiltView Build(IViewSource src, string path, double now, BuildSession buildSession)
    {
        var registrations = buildSession.Registrations;
        var orchs = buildSession.Orchs;
        JsonObject StatusOf(string dir) => buildSession.StatusOf(dir);
        Facts FactsOf(Orch o, TeamEntry t) => buildSession.FactsOf(o, t);

        JsonObject OrchSummary(Orch o) => o.Teams.Aggregate(Zero(), (acc, t) => AddSummary(acc, FactsOf(o, t).Summary));

        var optionsAt = path.IndexOf('?', StringComparison.Ordinal);
        var options = optionsAt >= 0 ? path[(optionsAt + 1)..] : string.Empty;
        var segs = (optionsAt >= 0 ? path[..optionsAt] : path).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(static s =>
        {
            try
            {
                return Uri.UnescapeDataString(s);
            }
            catch (UriFormatException)
            {
                return s;
            }
        }).ToList();
        const string rootPath = "/";
        string OPath(Orch o) => $"/o/{Seg(o.Oid)}";
        string TPath(Orch o, TeamEntry t) => $"{OPath(o)}/t/{Seg(t.Tkey)}";
        string MPath(Orch o, TeamEntry t, string name) => $"{TPath(o, t)}/m/{Seg(name)}";
        string SPath(Orch o, TeamEntry t, string name) => $"{TPath(o, t)}/s/{Seg(name)}";

        BuiltView NotFound(string nearest) => new(404, new JsonObject
        {
            ["view_schema"] = ViewSchema,
            ["error"] = "not_found",
            ["path"] = Text(src, JsonValue.FromString(path), 400) ?? string.Empty,
            ["nearest"] = nearest,
        });

        string OrchLabel(Orch o) =>
            $"{Text(src, JsonValue.FromString(RepoName(o.Dir)), NameCap) ?? "?"} · {(o.Pid is null ? "no orchestrator" : $"pid {o.Pid.Value.ToString(CultureInfo.InvariantCulture)}")}{(o.NoTeam ? " · no team" : string.Empty)}";

        string OrchState(Orch o) => o.Pid is null ? "none" : o.Live ? "live" : "orphaned";
        string TeamLabel(TeamEntry t) => Text(src, JsonValue.FromString(t.Key), NameCap) ?? string.Empty;

        ModelCtx Ctx() => new()
        {
            Src = src,
            Now = now,
            Orchs = orchs,
            FactsOf = FactsOf,
            StatusOf = StatusOf,
            Registrations = registrations,
            OPath = OPath,
            TPath = TPath,
            MPath = MPath,
            SPath = SPath,
            OrchLabel = OrchLabel,
            OrchState = OrchState,
            TeamLabel = TeamLabel,
            NotFound = NotFound,
            Runs = new Lazy<IReadOnlyList<SubagentRun>>(() => src.SubagentRuns(now)),
            Held = new Lazy<HashSet<string>>(() => HeldSessionsOf(src)),
            Options = options,
        };

        // The path a member slice serves, under a document: the same document of the node that path names now.
        if (segs.Count == 6 && Array.IndexOf(ModelKinds, segs[0]) >= 0 && segs[1] == Model.TreeIds.NodePathRoot && segs[4] == "m")
        {
            foreach (var o in orchs)
            {
                if (o.Teams.FirstOrDefault(t => Model.ActivityLane.DirId(t.Dir) == segs[2] && (t.Name ?? "default") == segs[3]) is { } held)
                {
                    return Build(src, "/" + segs[0] + (segs[5] == Model.TreeIds.Orchestrator ? OPath(o) : MPath(o, held, segs[5])), now, buildSession);
                }
            }

            return NotFound(rootPath);
        }

        if (segs.Count > 0 && Array.IndexOf(ModelKinds, segs[0]) >= 0)
        {
            using var documentTimer = ViewTiming.Section("document:" + segs[0]);
            return ModelDocument(Ctx(), segs[0], string.Concat(segs.Select(static s => "/" + Seg(s))), [.. segs.Skip(1)]);
        }

        // The path a member slice serves: the node the tree serves for that member now, whichever process holds its team.
        if (segs.Count == 5 && segs[0] == Model.TreeIds.NodePathRoot && segs[3] == "m")
        {
            foreach (var o in orchs)
            {
                if (o.Teams.FirstOrDefault(t => Model.ActivityLane.DirId(t.Dir) == segs[1] && (t.Name ?? "default") == segs[2]) is { } held)
                {
                    return Build(src, segs[4] == Model.TreeIds.Orchestrator ? OPath(o) : MPath(o, held, segs[4]), now, buildSession);
                }
            }

            return NotFound(rootPath);
        }

        if (IsMailTextPath(segs))
        {
            using var mailTextTimer = ViewTiming.Section("document:mail-text");
            return MailTextDocument(Ctx(), string.Concat(segs.Select(static s => "/" + Seg(s))), [.. segs.Take(segs.Count - 1)]);
        }

        if (segs.Count == 0)
        {
            var view = Envelope("root", string.Empty, rootPath, Arr(Crumb("root", string.Empty, "ah", rootPath)), now);
            var total = Zero();
            var children = new JsonArray();
            foreach (var o in orchs)
            {
                var summary = OrchSummary(o);
                _ = AddSummary(total, summary);
                children.Add(new JsonObject
                {
                    ["kind"] = "orchestrator",
                    ["id"] = o.Oid,
                    ["label"] = OrchLabel(o),
                    ["path"] = OPath(o),
                    ["state"] = OrchState(o),
                    ["dir"] = TextOrNull(src, o.Dir, 400),
                    ["summary"] = summary,
                });
                var tile = TileFacts(src, o, FactsOf, now);
                var child = children[^1].AsObject();
                foreach (var name in tile.Keys)
                {
                    child[name] = tile[name];
                }
            }

            view["children"] = children;
            view["summary"] = total;
            view["body"] = new JsonObject { ["theme"] = ViewTheme.Build() };
            return Finish(view);
        }

        if (segs[0] != "o" || segs.Count < 2)
        {
            return NotFound(rootPath);
        }

        var orch = orchs.FirstOrDefault(o => o.Oid == segs[1]);
        if (orch is null)
        {
            return NotFound(rootPath);
        }

        var oBread = Arr(Crumb("root", string.Empty, "ah", rootPath), Crumb("orchestrator", orch.Oid, OrchLabel(orch), OPath(orch)));
        if (segs.Count == 2)
        {
            var view = Envelope("orchestrator", orch.Oid, OPath(orch), oBread, now);
            view["summary"] = OrchSummary(orch);
            var children = new JsonArray();
            foreach (var t in orch.Teams)
            {
                children.Add(new JsonObject
                {
                    ["kind"] = "team",
                    ["id"] = t.Tkey,
                    ["label"] = TeamLabel(t),
                    ["path"] = TPath(orch, t),
                    ["state"] = src.TeamState(t.Doc),
                    ["summary"] = FactsOf(orch, t).Summary,
                    ["command"] = $"ah roster disband --plan{(t.Name is null ? string.Empty : $" --team {t.Name}")}",
                });
            }

            view["children"] = children;
            string? session = null;
            foreach (var t in orch.Teams)
            {
                if (t.Doc.Obj("orchestrator")?.Str("session_id") is { Length: > 0 } s)
                {
                    session = s;
                    break;
                }
            }

            view["body"] = new JsonObject
            {
                ["dir"] = TextOrNull(src, orch.Dir, 400),
                ["pid"] = orch.Pid is null ? JsonValue.Null : orch.Pid.Value,
                ["session_id"] = session is null ? JsonValue.Null : TextOrNull(src, session, NameCap),
                ["orchestrator_live"] = orch.Live,
            };
            return Finish(view);
        }

        if (segs[2] != "t" || segs.Count < 4)
        {
            return NotFound(OPath(orch));
        }

        var team = orch.Teams.FirstOrDefault(t => t.Tkey == segs[3]);
        if (team is null)
        {
            return NotFound(OPath(orch));
        }

        var facts = FactsOf(orch, team);
        var tBread = Arr([.. oBread, Crumb("team", team.Tkey, TeamLabel(team), TPath(orch, team))]);
        if (segs.Count == 4)
        {
            return TeamView(src, orch, team, facts, tBread, now, MPath, SPath, TPath, registrations);
        }

        if (segs[4] == "s" && segs.Count == 6)
        {
            var row = facts.Work.Streams.FirstOrDefault(r => r.Name == segs[5]);
            if (row is null)
            {
                return NotFound(TPath(orch, team));
            }

            var view = Envelope(
                "stream",
                row.Name,
                SPath(orch, team, row.Name),
                Arr([.. tBread, Crumb("stream", row.Name, Text(src, JsonValue.FromString(row.Name), NameCap) ?? string.Empty, SPath(orch, team, row.Name))]),
                now);
            view["summary"] = new JsonObject { ["glyph_state"] = row.GlyphState };
            view["children"] = new JsonArray();
            view["body"] = StreamBody(src, facts, row);
            return Finish(view);
        }

        if (segs[4] != "m" || segs.Count != 6)
        {
            return NotFound(TPath(orch, team));
        }

        var member = facts.Members.FirstOrDefault(m => m.Str("name") == segs[5]);
        if (member is null)
        {
            return NotFound(TPath(orch, team));
        }

        return MemberView(src, orch, team, facts, member, tBread, now, MPath, registrations);
    }

    private static BuiltView TeamView(
        IViewSource src,
        Orch orch,
        TeamEntry team,
        Facts facts,
        JsonArray tBread,
        double now,
        Func<Orch, TeamEntry, string, string> mPath,
        Func<Orch, TeamEntry, string, string> sPath,
        Func<Orch, TeamEntry, string> tPath,
        Lazy<Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)>> registrations)
    {
        var view = Envelope("team", team.Tkey, tPath(orch, team), tBread, now);
        view["summary"] = facts.Summary;
        var children = new JsonArray();
        foreach (var m in facts.Members)
        {
            var name = m.Str("name")!;
            var ctx = m.Int("context_pct");
            var ctxUsed = m.Int("context_used");
            var memberSummary = new JsonObject
                {
                    ["role"] = Orn(Get(m, "role")),
                    ["kind"] = Orn(Get(m, "kind")),
                    ["route"] = Orn(Get(m, "route")),
                    ["model"] = TextOrNull(src, Get(m.Obj("row") ?? new JsonObject(), "model"), NameCap),
                    ["activity"] = Orn(Get(m, "activity")),
                    ["activity_at"] = Orn(Get(m, "activity_at")),
                    ["context_pct"] = ctx is null ? JsonValue.Null : ctx.Value,
                    ["context_used"] = ctxUsed is null ? JsonValue.Null : ctxUsed.Value,
                    ["context_level"] = ContextAlerts.Level(Get(m, "context_pct")),
                    ["blocked_by"] = Truthy(Get(m, "blocked_by")) ? Get(m, "blocked_by") : JsonValue.Null,
                    ["owed"] = facts.Open.Count(d => d.Str("member") == name),
                    ["last_report_at"] = facts.LastReport.TryGetValue(name, out var lr) && lr.Length > 0 ? lr : JsonValue.Null,
                    ["stream"] = Truthy(Get(m, "stream")) ? Get(m, "stream") : JsonValue.Null,
                    ["pid"] = PlaceOf(src, registrations, m).Pid,
                };
            PassThrough(m, memberSummary, "branch", "last_report_line");
            if (m.Str("work_tree") is { Length: > 0 } producedTree && TextOrNull(src, WorkTreeName(producedTree), NameCap) is { IsNull: false } treeName)
            {
                memberSummary["work_tree"] = treeName;
            }

            var memberRow = new JsonObject
            {
                ["kind"] = "member",
                ["id"] = name,
                ["label"] = name,
                ["path"] = mPath(orch, team, name),
                ["state"] = m.TryGetValue("live", out var live) && live.Kind == JsonKind.Bool && live.AsBool() ? "live" : "stale",
                ["summary"] = memberSummary,
            };
            if (Model.TreeIds.LivePathOf(team.Dir, team.Name ?? "default", JsonValue.FromObject(team.Doc), name, child => src.ReadTeam(team.Dir, child) is { } read ? JsonValue.FromObject(read) : (JsonValue?)null) is { } livePath)
            {
                memberRow["live_path"] = livePath;
            }

            children.Add(memberRow);
        }

        var w = facts.Work;
        JsonValue ExpiresIn(JsonObject j) =>
            Truthy(Get(j, "expires")) ? Math.Max(0, Math.Floor((JsNum(j, "expires_at") - now) / 1000)) : JsonValue.Null;

        foreach (var j in w.Joins)
        {
            var id = j.Str("id") ?? string.Empty;
            var cmd = src.JoinDecisionCommands(id);
            var createdMs = ParseMs(j.Str("created"));
            children.Add(new JsonObject
            {
                ["kind"] = "join",
                ["id"] = TextOrNull(src, Get(j, "id"), NameCap),
                ["label"] = Text(src, Get(j, "name_hint"), NameCap) is { } nh ? nh : TextOrNull(src, Get(j, "id"), NameCap),
                ["path"] = JsonValue.Null,
                ["state"] = "pending",
                ["command"] = Get(cmd, "accept"),
                ["commands"] = cmd,
                ["summary"] = new JsonObject
                {
                    ["harness"] = TextOrNull(src, Get(j, "harness"), NameCap),
                    ["role_wanted"] = TextOrNull(src, Get(j, "role_wanted"), NameCap),
                    ["created_at"] = IsoOf(createdMs),
                    ["expires_at"] = Truthy(Get(j, "expires")) ? IsoOf(JsNum(j, "expires_at")) : JsonValue.Null,
                    ["age_sec"] = AgeSec(j.Str("created"), now),
                    ["expires_in_sec"] = ExpiresIn(j),
                },
            });
        }

        foreach (var r in w.Streams)
        {
            var peers = r.FactsOf.TryGetValue("peers", out var pv) && pv.Kind == JsonKind.Array ? pv.AsArray().Count : 0;
            var waiting = r.FactsOf.TryGetValue("waiting_on", out var wv) && wv.Kind == JsonKind.Array ? wv.AsArray().Count : 0;
            children.Add(new JsonObject
            {
                ["kind"] = "stream",
                ["id"] = TextOrNull(src, r.Name, NameCap),
                ["label"] = TextOrNull(src, r.Name, NameCap),
                ["path"] = sPath(orch, team, r.Name),
                ["state"] = TextOrNull(src, Get(r.Rec, "state"), NameCap),
                ["summary"] = new JsonObject
                {
                    ["glyph_state"] = r.GlyphState,
                    ["branch"] = TextOrNull(src, Get(r.FactsOf, "branch"), NameCap),
                    ["work_tree"] = TextOrNull(src, WorkTreeName(r.FactsOf.Obj("worktree")?.Str("path")), NameCap),
                    ["peers"] = peers,
                    ["waiting"] = waiting,
                },
            });
        }

        view["children"] = children;

        var open = new JsonArray();
        foreach (var d in facts.Open)
        {
            var entry = new JsonObject
            {
                ["id"] = Get(d, "id"),
                ["slug"] = Get(d, "slug"),
                ["to_name"] = Get(d, "to_name"),
                ["eta"] = Get(d, "eta"),
                ["sent_at"] = Get(d, "sent_at"),
                ["state"] = StateAt(d, now),
            };
            PassThrough(d, entry, "title", "item");
            open.Add(entry);
        }

        var streams = new JsonArray();
        foreach (var r in w.Streams)
        {
            var worktree = r.FactsOf.Obj("worktree");
            streams.Add(new JsonObject
            {
                ["name"] = TextOrNull(src, r.Name, NameCap),
                ["state"] = TextOrNull(src, Get(r.Rec, "state"), NameCap),
                ["glyph_state"] = r.GlyphState,
                ["branch"] = TextOrNull(src, Get(r.FactsOf, "branch"), NameCap),
                ["worktree_exists"] = worktree is not null && worktree.TryGetValue("exists", out var ex) && ex.Kind == JsonKind.Bool && ex.AsBool(),
                ["path"] = sPath(orch, team, r.Name),
            });
        }

        var links = new JsonArray();
        foreach (var l in w.Links)
        {
            var peers = new JsonArray();
            if (l.TryGetValue("peers", out var lp) && lp.Kind == JsonKind.Array)
            {
                foreach (var p in lp.AsArray())
                {
                    if (Text(src, p, NameCap) is { } t)
                    {
                        peers.Add(t);
                    }
                }
            }

            links.Add(new JsonObject
            {
                ["id"] = TextOrNull(src, Get(l, "id"), NameCap),
                ["peers"] = peers,
                ["state"] = TextOrNull(src, Get(l, "state"), NameCap),
                ["remaining_min"] = RemainingMin(src, l, now),
                ["command"] = $"ah msg link show {Text(src, Get(l, "id"), NameCap) ?? "null"}",
            });
        }

        var joins = new JsonArray();
        foreach (var j in w.Joins)
        {
            var id = j.Str("id") ?? string.Empty;
            joins.Add(new JsonObject
            {
                ["id"] = TextOrNull(src, Get(j, "id"), NameCap),
                ["harness"] = TextOrNull(src, Get(j, "harness"), NameCap),
                ["name_hint"] = TextOrNull(src, Get(j, "name_hint"), NameCap),
                ["role_wanted"] = TextOrNull(src, Get(j, "role_wanted"), NameCap),
                ["why"] = TextOrNull(src, Get(j, "why"), TextCap),
                ["created_at"] = IsoOf(ParseMs(j.Str("created"))),
                ["expires_at"] = Truthy(Get(j, "expires")) ? IsoOf(JsNum(j, "expires_at")) : JsonValue.Null,
                ["age_sec"] = AgeSec(j.Str("created"), now),
                ["expires_in_sec"] = ExpiresIn(j),
                ["commands"] = src.JoinDecisionCommands(id),
            });
        }

        view["body"] = new JsonObject
        {
            ["transport"] = TextOrNull(src, Get(team.Doc, "transport"), NameCap),
            ["mesh_policy"] = src.MeshPolicy(team.Doc) is { } mp ? TextOrNull(src, mp, NameCap) : JsonValue.Null,
            ["open_dispatches"] = open,
            ["owed_total"] = facts.Open.Count,
            ["dispatches_truncated"] = facts.Truncated,
            ["skipped_members"] = facts.Skipped,
            ["commands"] = Arr(new JsonObject
            {
                ["label"] = "plan a disband",
                ["command"] = $"ah roster disband --plan{(team.Name is null ? string.Empty : $" --team {team.Name}")}",
            }),
            ["streams"] = streams,
            ["links"] = links,
            ["links_truncated"] = w.LinksTruncated,
            ["joins"] = joins,
            ["reason"] = w.Reason is null ? JsonValue.Null : w.Reason,
        };
        return Finish(view);
    }

    private static BuiltView MemberView(
        IViewSource src,
        Orch orch,
        TeamEntry team,
        Facts facts,
        JsonObject member,
        JsonArray tBread,
        double now,
        Func<Orch, TeamEntry, string, string> mPath,
        Lazy<Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)>> registrations)
    {
        var name = member.Str("name")!;
        var row = member.Obj("row") ?? new JsonObject();
        var path = mPath(orch, team, name);
        var view = Envelope("member", name, path, Arr([.. tBread, Crumb("member", name, name, path)]), now);
        var place = PlaceOf(src, registrations, member);
        var placement = src.Placement(row, team.Doc.Str("transport"));
        var box = facts.Boxes.TryGetValue(name, out var b) ? b : (0, 0);
        view["summary"] = new JsonObject { ["owed"] = facts.Open.Count(d => d.Str("member") == name) };
        view["children"] = new JsonArray();
        var pasteable = placement.TryGetValue("pasteable", out var pv) && pv.Kind == JsonKind.Bool && pv.AsBool();
        var ctx = member.Int("context_pct");
        var ctxUsed = member.Int("context_used");
        var owed = new JsonArray();
        foreach (var d in facts.Open.Where(d => d.Str("member") == name))
        {
            owed.Add(new JsonObject { ["id"] = Get(d, "id"), ["slug"] = Get(d, "slug"), ["sent_at"] = Get(d, "sent_at"), ["eta"] = Get(d, "eta") });
        }

        var adapter = Truthy(Get(row, "adapter")) ? Get(row, "adapter") : row.TryGetValue("appserver_pid", out _) ? JsonValue.FromString("appserver") : JsonValue.Null;
        view["body"] = new JsonObject
        {
            ["role"] = Orn(Get(member, "role")),
            ["label"] = Truthy(Get(member, "label")) ? Get(member, "label") : name,
            ["model"] = TextOrNull(src, Get(row, "model"), NameCap),
            ["effort"] = TextOrNull(src, Get(row, "effort"), NameCap),
            ["kind"] = Orn(Get(member, "kind")),
            ["route"] = Orn(Get(member, "route")),
            ["stream"] = Truthy(Get(member, "stream")) ? Get(member, "stream") : JsonValue.Null,
            ["session_id"] = Truthy(Get(member, "session_id")) ? Get(member, "session_id") : JsonValue.Null,
            ["pid"] = place.Pid,
            ["work_tree"] = place.WorkTree,
            ["placement"] = new JsonObject
            {
                ["transport"] = TextOrNull(src, Get(placement, "transport"), NameCap),
                ["pane_id"] = TextOrNull(src, Get(placement, "pane"), NameCap),
                ["pasteable"] = pasteable,
                ["reason"] = pasteable ? JsonValue.Null : TextOrNull(src, Get(placement, "reason"), NameCap),
            },
            ["live"] = member.TryGetValue("live", out var live) && live.Kind == JsonKind.Bool && live.AsBool(),
            ["activity"] = Orn(Get(member, "activity")),
            ["activity_at"] = Truthy(Get(member, "activity_at")) ? Get(member, "activity_at") : JsonValue.Null,
            ["state_since"] = member.Str("session_id") is { Length: > 0 } stateSession && src.ActivityOf(team.Dir, stateSession)?.StateSince is { } stateSince ? stateSince : JsonValue.Null,
            ["last_tool"] = Truthy(Get(member, "last_tool")) ? Get(member, "last_tool") : JsonValue.Null,
            ["last_tool_at"] = Truthy(Get(member, "last_tool_at")) ? Get(member, "last_tool_at") : JsonValue.Null,
            ["context_pct"] = ctx is null ? JsonValue.Null : ctx.Value,
            ["context_used"] = ctxUsed is null ? JsonValue.Null : ctxUsed.Value,
            ["context_level"] = ContextAlerts.Level(Get(member, "context_pct")),
            ["blocked_by"] = Truthy(Get(member, "blocked_by")) ? Get(member, "blocked_by") : JsonValue.Null,
            ["blocked_note"] = Truthy(Get(member, "blocked_note")) ? Get(member, "blocked_note") : JsonValue.Null,
            ["owed"] = owed,
            ["last_report_at"] = facts.LastReport.TryGetValue(name, out var lr) && lr.Length > 0 ? lr : JsonValue.Null,
            ["box"] = new JsonObject
            {
                ["unacked"] = box.Item1,
                ["overdue"] = box.Item2,
                ["adapter"] = TextOrNull(src, adapter, NameCap),
                ["adapter_state"] = TextOrNull(src, Get(row, "adapter_note"), TextCap),
            },
        };
        return Finish(view);
    }

    /// <summary>
    /// A member's own pid, from its session's registration, and the basename of its work tree: the root assigned in the team document,
    /// else the checkout holding the registered working directory. Null for what is unknown.
    /// </summary>
    private static (JsonValue Pid, JsonValue WorkTree) PlaceOf(IViewSource src, Lazy<Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)>> registrations, JsonObject member)
    {
        var (pid, checkout, _) = member.Str("session_id") is { Length: > 0 } sessionId ? registrations.Value(sessionId) : (null, null, null);
        var row = member.Obj("row") ?? new JsonObject();
        var root = HierarchyModule.SelectWorkRoot(row.Str("work_root"), checkout);
        var name = root is null ? null : Path.GetFileName(root.TrimEnd('/'));
        return (pid is null ? JsonValue.Null : pid.Value, TextOrNull(src, string.IsNullOrEmpty(name) ? null : name, NameCap));
    }

    /// <summary>Copies the named text fields the producer wrote onto <paramref name="to"/>; one it did not write (an older producer) stays absent.</summary>
    private static void PassThrough(JsonObject from, JsonObject to, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (from.TryGetValue(key, out var v) && v.IsText)
            {
                to[key] = v;
            }
        }
    }

    /// <summary>The name of the directory a work tree path ends in, the same form a member's work tree is served in; null for no path.</summary>
    private static string? WorkTreeName(string? path)
    {
        var name = path is null ? null : Path.GetFileName(path.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static BuiltView Finish(JsonObject view) => new(200, ViewRev.Finish(view));

    private static double ParseMs(string? iso) => iso is not null && Timestamps.TryParseMs(iso, out var ms) ? ms : double.NaN;

    private static double JsNum(JsonObject o, string key) =>
        o.TryGetValue(key, out var v) && v.Kind == JsonKind.Number ? v.AsNumber() : double.NaN;
}
