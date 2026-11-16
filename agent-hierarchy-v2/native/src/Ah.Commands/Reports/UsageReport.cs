using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>What the usage report reads and writes: the usage file and the cache beside it, the clock, and the roles of the working directory.</summary>
public sealed record UsageReportServices(
    IFileReader Files,
    IRawFileWriter Writer,
    IClock Clock,
    IConfigCore Config,
    IHierarchyUsage Usage,
    string Cwd,
    int ProcessId)
{
    /// <summary>A team's record by name, <see cref="JsonValue.Null"/> when there is none; <c>--team</c> reads only this.</summary>
    public Func<string, JsonValue>? TeamRecord { get; init; }

    /// <summary>The directory holding one directory per project, each with its sessions' transcripts.</summary>
    public string? ProjectsDir { get; init; }

    /// <summary>The <c>progress</c> directory of the pool, one folder per team with its <c>sessions.jsonl</c> inventory.</summary>
    public string? ProgressDir { get; init; }

    /// <summary>The StateDb <c>sessions</c> rows of this hierarchy dir, live and ended.</summary>
    public Func<IReadOnlyList<SessionHistoryRow>>? SessionHistory { get; init; }

    /// <summary>The names of the teams with a record now; <c>--fleet</c> adds them to the inventory and StateDb teams.</summary>
    public Func<IReadOnlyList<string>>? TeamNames { get; init; }
}

/// <summary>
/// <c>ah usage-report [session|day|week|month|all] [--json]</c>: tokens per hierarchy role from the usage file and the main-session
/// transcripts it names, scanned incrementally into a cache beside the usage file. <c>--team &lt;name&gt;</c> instead gives tokens per
/// member of that team. Pure local computation.
/// </summary>
internal static class UsageReport
{
    private const double DayMs = 24 * 60 * 60 * 1000;
    private static readonly string[] Windows = ["session", "day", "week", "month", "all"];
    private static readonly string[] RoleOrder = ["orchestrator", .. ConfigModule.BuiltinRoles, "other"];
    private static readonly string[] UsageKeys = ["calls", "in", "out", "cache_read", "cache_create"];

    private sealed class Bucket(JsonObject usage)
    {
        public HashSet<string> Agents { get; } = new(StringComparer.Ordinal);

        public double Missing { get; set; }

        public JsonObject Usage { get; } = usage;
    }

    private sealed class Aggregate
    {
        public JsonObject Order { get; } = new();

        public Dictionary<string, Bucket> Roles { get; } = new(StringComparer.Ordinal);

        public JsonObject OtherTypes { get; } = new();
    }

    /// <summary>
    /// Tokens per session of a team: every session its progress inventory, its StateDb rows or its record names (dismissed members and a
    /// closed team included), each the whole transcript plus that session's subagents, with a subtotal per member and a team total.
    /// </summary>
    private static int TeamReport(UsageReportServices s, IReadOnlyList<string> args, int at, bool asJson, TextWriter stdout, TextWriter? stderr)
    {
        var name = at + 1 < args.Count && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : null;
        if (name is null || args.Where((_, i) => i != at + 1).Any(a => Array.IndexOf(Windows, a) >= 0))
        {
            stderr?.WriteLine("usage: ah usage-report --team <name> [--json] (it counts whole sessions, so it takes no window)");
            return 2;
        }

        var history = s.SessionHistory?.Invoke() ?? [];
        var seats = TeamSeats(s, name, history, out var known);
        if (!known)
        {
            stderr?.WriteLine($"ah usage-report: no team '{name}' here: no record, no session inventory and no session rows");
            return 1;
        }

        var counter = new SeatCounter(s);
        foreach (var seat in seats)
        {
            counter.Count(seat);
        }

        var notes = TeamNotes(seats, history.Where(h => h.Team == name).ToList());
        if (asJson)
        {
            var members = new JsonObject();
            foreach (var group in seats.GroupBy(x => x.Member))
            {
                members[group.Key] = UsageRow(s, group);
            }

            JsonArray sessionRows = [.. seats.Select(x => (JsonValue)SeatJson(s, x))];
            JsonArray noteRows = [.. notes.Select(n => (JsonValue)n)];
            stdout.Write(JsonText.Stringify(
                new JsonObject
                {
                    ["generated"] = Timestamps.ToIsoString(s.Clock.UtcNow),
                    ["team"] = name,
                    ["sessions"] = sessionRows,
                    ["members"] = members,
                    ["total"] = UsageRow(s, seats),
                    ["notes"] = noteRows,
                },
                2) + "\n");
            return 0;
        }

        var o = new List<string>
        {
            $"team {name} — tokens of every session the team had, with its subagents",
            $"  {Pad("member", 26)}{Pad("role", 15)}{Pad("session", 10)}{Pad("started", 17)}{Pad("ended", 17)}{UsageHead()}",
        };
        foreach (var group in seats.GroupBy(x => x.Member))
        {
            foreach (var x in group)
            {
                o.Add($"  {Pad(x.Member, 26)}{Pad(x.Role ?? "-", 15)}{Pad(SliceHead(x.Session ?? "-", 8), 10)}{Pad(Stamp(x.Started), 17)}{Pad(x.Live ? "live" : Stamp(x.Ended), 17)}{UsageCells(x.Bucket.Usage)}{MissingNote(x.Bucket.Missing)}");
            }

            if (group.Count() > 1)
            {
                o.Add($"  {Pad("  subtotal", 85)}{UsageCells(UsageRow(s, group))}");
            }
        }

        o.Add($"  {Pad("total", 85)}{UsageCells(UsageRow(s, seats))}");
        o.Add(string.Empty);
        o.AddRange(notes);
        o.Add("in = fresh input tokens; cache reads shown separately (≈10x cheaper).");
        stdout.Write(string.Join("\n", o) + "\n");
        return 0;
    }

    /// <summary><c>--fleet</c>: one total row per team of this hierarchy dir, each counted by the <c>--team</c> rule, and a grand total.</summary>
    private static int FleetReport(UsageReportServices s, bool asJson, TextWriter stdout)
    {
        var history = s.SessionHistory?.Invoke() ?? [];
        var names = new List<string>();
        void AddName(string? team)
        {
            if (team is { Length: > 0 } && !names.Contains(team, StringComparer.Ordinal))
            {
                names.Add(team);
            }
        }

        var progress = s.ProgressDir ?? string.Empty;
        foreach (var folder in progress.Length > 0 && s.Files.Exists(progress) ? s.Files.List(progress).Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
        {
            if (s.Files.Exists(NodePath.Join(progress, folder, "sessions.jsonl")))
            {
                AddName(folder);
            }
        }

        foreach (var row in history)
        {
            AddName(row.Team);
        }

        foreach (var team in s.TeamNames?.Invoke() ?? [])
        {
            AddName(team);
        }

        var byTeam = names.Select(n => (Name: n, Seats: TeamSeats(s, n, history, out _))).ToList();

        // A session under two teams belongs to the one whose inventory line is first; else to the first team that lists it.
        var owner = new Dictionary<string, (string Team, long At)>(StringComparer.Ordinal);
        foreach (var (team, seats) in byTeam)
        {
            foreach (var seat in seats.Where(x => x.Session is not null))
            {
                var at = seat.InventoryAt ?? long.MaxValue;
                if (!owner.TryGetValue(seat.Session!, out var held) || at < held.At)
                {
                    owner[seat.Session!] = (team, at);
                }
            }
        }

        var counter = new SeatCounter(s);
        var rows = new List<(string Team, JsonObject Usage)>();
        foreach (var (team, seats) in byTeam)
        {
            var mine = seats.Where(x => x.Session is null || owner[x.Session].Team == team).ToList();
            foreach (var seat in mine)
            {
                counter.Count(seat);
            }

            rows.Add((team, UsageRow(s, mine)));
        }

        var total = s.Usage.ZeroUsage();
        double sessions = 0;
        double missing = 0;
        foreach (var (_, usage) in rows)
        {
            AddInto(total, usage);
            sessions += ToNumber(usage["sessions"]);
            missing += ToNumber(usage["missing"]);
        }

        total["sessions"] = sessions;
        total["missing"] = missing;
        if (asJson)
        {
            var teams = new JsonObject();
            foreach (var (team, usage) in rows)
            {
                teams[team] = usage;
            }

            stdout.Write(JsonText.Stringify(new JsonObject { ["generated"] = Timestamps.ToIsoString(s.Clock.UtcNow), ["teams"] = teams, ["total"] = total }, 2) + "\n");
            return 0;
        }

        var o = new List<string> { "fleet — tokens of every team in this hierarchy dir", $"  {Pad("team", 30)}{RPad("sessions", 9)}{UsageHead()}" };
        o.AddRange(rows.Select(r => $"  {Pad(r.Team, 30)}{RPad(JsConvert.ToJsString(r.Usage["sessions"]), 9)}{UsageCells(r.Usage)}{MissingNote(ToNumber(r.Usage["missing"]))}"));
        o.Add($"  {Pad("total", 30)}{RPad(JsConvert.ToJsString(total["sessions"]), 9)}{UsageCells(total)}");
        stdout.Write(string.Join("\n", o) + "\n");
        return 0;
    }

    /// <summary>One session a team had; <see cref="Session"/> is null for a record member with no session yet.</summary>
    private sealed class Seat(string? session, string member, IHierarchyUsage usage)
    {
        public string? Session { get; } = session;

        public string Member { get; set; } = member;

        public string? Role { get; set; }

        public long? Started { get; set; }

        public long? Ended { get; set; }

        public bool Live { get; set; }

        public long? InventoryAt { get; set; }

        public Bucket Bucket { get; } = new(usage.ZeroUsage());
    }

    /// <summary>
    /// The sessions of <paramref name="team"/>, deduplicated by session id, from its progress inventory, then its StateDb rows, then its
    /// record; member and role come from the first source that names them, the times from StateDb, else the inventory's <c>at</c>.
    /// <paramref name="known"/> is false when no source has the team.
    /// </summary>
    private static List<Seat> TeamSeats(UsageReportServices s, string team, IReadOnlyList<SessionHistoryRow> history, out bool known)
    {
        var seats = new List<Seat>();
        var byId = new Dictionary<string, Seat>(StringComparer.Ordinal);
        Seat Take(string session, string? member)
        {
            if (!byId.TryGetValue(session, out var seat))
            {
                seat = new Seat(session, member ?? "-", s.Usage);
                byId[session] = seat;
                seats.Add(seat);
            }
            else if (seat.Member == "-" && member is { Length: > 0 })
            {
                seat.Member = member;
            }

            return seat;
        }

        var inventory = s.ProgressDir is { } dir ? NodePath.Join(dir, HierarchyModule.ProgressFolderOf(team), "sessions.jsonl") : null;
        var lines = inventory is not null && s.Files.Exists(inventory) ? s.Files.ReadText(inventory).Split('\n') : [];
        foreach (var line in lines.Where(l => l.Length > 0))
        {
            JsonValue row;
            try
            {
                row = JsonText.Parse(line);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }

            if (row.Member("session_id") is not { IsText: true } id || id.AsString().Length == 0)
            {
                continue;
            }

            var seat = Take(id.AsString(), row.Member("member") is { IsText: true } m ? m.AsString() : null);
            seat.Role ??= row.Member("role") is { IsText: true } r ? r.AsString() : null;
            if (row.Member("at") is { IsText: true } stamp && DateTimeOffset.TryParse(stamp.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                seat.InventoryAt ??= when.ToUnixTimeMilliseconds();
                seat.Started ??= seat.InventoryAt;
            }
        }

        var fromDb = history.Where(h => h.Team == team).OrderBy(h => h.StartedAt).ToList();
        foreach (var row in fromDb)
        {
            var seat = Take(row.SessionId, row.Member);
            seat.Started = row.StartedAt;
            seat.Ended = row.EndedAt;
            seat.Live = row.EndedAt is null;
        }

        var record = s.TeamRecord?.Invoke(team) ?? JsonValue.Null;
        if (record.Member("orchestrator")?.Member("session_id") is { IsText: true } lead && lead.AsString().Length > 0)
        {
            Take(lead.AsString(), "orchestrator").Role ??= "orchestrator";
        }

        foreach (var member in record.Member("members") is { IsArray: true } members ? members.AsArray() : [])
        {
            if (member.Member("name") is not { IsText: true } memberName)
            {
                continue;
            }

            var role = member.Member("role") is { IsText: true } r ? r.AsString() : null;
            if (member.Member("session_id") is { IsText: true } sid && sid.AsString().Length > 0)
            {
                Take(sid.AsString(), memberName.AsString()).Role ??= role;
            }
            else
            {
                seats.Add(new Seat(null, memberName.AsString(), s.Usage) { Role = role });
            }
        }

        known = record.IsMap || lines.Any(l => l.Length > 0) || fromDb.Count > 0;
        return seats;
    }

    /// <summary>Counts each session once: its whole transcript, found by session id under the projects dir, plus its subagents.</summary>
    private sealed class SeatCounter(UsageReportServices s)
    {
        private readonly List<JsonValue> _records = LatestPerAgent(s.Usage.ReadUsageRecords());
        private readonly List<string> _projects = s.ProjectsDir is { } root && s.Files.Exists(root) ? s.Files.List(root).Select(d => NodePath.Join(root, d)).ToList() : [];

        public void Count(Seat seat)
        {
            if (seat.Session is not { } session)
            {
                seat.Bucket.Missing += 1;
                return;
            }

            var file = _projects.Select(p => NodePath.Join(p, session + ".jsonl")).FirstOrDefault(s.Files.Exists);
            if (file is not null && s.Usage.SumTranscript(file) is { IsMap: true } sum)
            {
                s.Usage.AddUsage(seat.Bucket.Usage, sum);
                seat.Bucket.Agents.Add(session);
            }
            else
            {
                seat.Bucket.Missing += 1;
            }

            foreach (var r in _records.Where(r => r.Member("session_id") is { IsText: true } id && id.AsString() == session))
            {
                s.Usage.AddUsage(seat.Bucket.Usage, r);
                seat.Bucket.Agents.Add(r.Member("agent_id")!.Value.AsString());
            }
        }
    }

    /// <summary>The footer lines: pruned StateDb history, and Orchestrator sessions that may predate the inventory.</summary>
    private static List<string> TeamNotes(List<Seat> seats, List<SessionHistoryRow> rows)
    {
        var notes = new List<string>();
        var inDb = rows.Select(r => r.SessionId).ToHashSet(StringComparer.Ordinal);
        if (rows.Count > 0 && rows.Min(r => r.StartedAt) is var oldest
            && seats.Any(x => x.Session is not null && x.Member != "orchestrator" && !inDb.Contains(x.Session) && x.InventoryAt < oldest))
        {
            notes.Add("older sessions pruned: their StateDb rows are gone; the inventory still names them.");
        }

        var lead = seats.Where(x => x.Member == "orchestrator" && x.Started is not null).Select(x => x.Started!.Value).DefaultIfEmpty(long.MaxValue).Min();
        var crew = seats.Where(x => x.Member != "orchestrator" && x.Started is not null).Select(x => x.Started!.Value).DefaultIfEmpty(long.MaxValue).Min();
        if (lead != long.MaxValue && lead > crew)
        {
            notes.Add($"orchestrator sessions before {Stamp(lead)} may be missing");
        }

        return notes;
    }

    private static JsonObject UsageRow(UsageReportServices s, IEnumerable<Seat> seats)
    {
        var row = s.Usage.ZeroUsage();
        double sessions = 0;
        double agents = 0;
        double missing = 0;
        foreach (var seat in seats)
        {
            AddInto(row, seat.Bucket.Usage);
            sessions += seat.Session is null ? 0 : 1;
            agents += seat.Bucket.Agents.Count;
            missing += seat.Bucket.Missing;
        }

        row["sessions"] = sessions;
        row["agents"] = agents;
        row["missing"] = missing;
        return row;
    }

    private static JsonObject SeatJson(UsageReportServices s, Seat x)
    {
        var row = new JsonObject
        {
            ["member"] = x.Member,
            ["role"] = x.Role is null ? JsonValue.Null : x.Role,
            ["session_id"] = x.Session is null ? JsonValue.Null : x.Session,
            ["started"] = x.Started is { } a ? Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds(a)) : JsonValue.Null,
            ["ended"] = x.Live ? "live" : x.Ended is { } b ? Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds(b)) : JsonValue.Null,
            ["agents"] = x.Bucket.Agents.Count,
            ["missing"] = x.Bucket.Missing,
        };
        foreach (var key in UsageKeys)
        {
            row[key] = x.Bucket.Usage.Get(key) ?? JsonValue.Null;
        }

        return row;
    }

    private static string Stamp(long? ms) => ms is { } v ? Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds(v))[..16].Replace("T", " ", StringComparison.Ordinal) : "—";

    private static string UsageHead() => $"{RPad("calls", 7)}{RPad("in", 10)}{RPad("out", 10)}{RPad("cache-read", 12)}{RPad("cache-create", 14)}";

    private static string UsageCells(JsonObject u) =>
        $"{RPad(Field(u, "calls"), 7)}{RPad(Fmt(u["in"]), 10)}{RPad(Fmt(u["out"]), 10)}{RPad(Fmt(u["cache_read"]), 12)}{RPad(Fmt(u["cache_create"]), 14)}";

    private static string MissingNote(double missing) =>
        missing > 0 ? $"   ({JsConvert.ToJsString(missing)} transcript{(missing > 1 ? "s" : string.Empty)} not found)" : string.Empty;

    public static int Run(UsageReportServices s, IReadOnlyList<string> args, TextWriter stdout, TextWriter? stderr = null)
    {
        var usage = s.Usage;
        var asJson = args.Contains("--json");
        if (args.ToList().IndexOf("--team") is var teamAt and >= 0)
        {
            return TeamReport(s, args, teamAt, asJson, stdout, stderr);
        }

        if (args.Contains("--fleet"))
        {
            return FleetReport(s, asJson, stdout);
        }

        var window = args.FirstOrDefault(a => Array.IndexOf(Windows, a) >= 0) ?? string.Empty;

        var now = s.Clock.UtcNow;
        var nowMs = (double)now.ToUnixTimeMilliseconds();
        var since = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["day"] = LocalMidnightMs(s.Clock, now),
            ["week"] = nowMs - (7 * DayMs),
            ["month"] = nowMs - (30 * DayMs),
            ["all"] = 0,
            ["session"] = 0,
        };

        var agentRoles = AgentRoles(s);
        var records = LatestPerAgent(usage.ReadUsageRecords());
        var latestSession = LatestSession(records);

        var cache = LoadCache(s);
        var mainFiles = new List<(string SessionId, string File)>();
        var seenSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.Member("project_dir") is { IsTruthy: true } dir && r.Member("session_id") is { IsTruthy: true } sid
                && dir.IsText)
            {
                var id = JsConvert.ToJsString(sid);
                if (seenSessions.Add(id))
                {
                    mainFiles.Add((id, NodePath.Join(dir.AsString(), id + ".jsonl")));
                }
            }
        }

        var files = cache["files"].AsObject();
        foreach (var (sid, file) in mainFiles)
        {
            if (!files.TryGetValue(file, out var existing) || !existing.IsMap)
            {
                existing = new JsonObject
                {
                    ["offset"] = 0,
                    ["days"] = new JsonObject(),
                    ["models"] = new JsonObject(),
                    ["session_id"] = sid,
                };
                files[file] = existing;
            }

            ScanFrom(s, file, existing.AsObject(), now);
        }

        SaveCache(s, cache);

        var session = Collect(s, records, files, agentRoles, latestSession, 0, true);
        var week = Collect(s, records, files, agentRoles, latestSession, since["week"], false);
        var chosen = window.Length > 0 && window != "session" ? Collect(s, records, files, agentRoles, latestSession, since[window], false) : null;
        var dailyDays = window == "month" ? 30 : 14;

        if (asJson)
        {
            var document = new JsonObject
            {
                ["generated"] = Timestamps.ToIsoString(now),
                ["records"] = records.Count,
                ["latest_session"] = latestSession is { IsTruthy: true } ls ? ls : JsonValue.Null,
                ["session"] = Strip(session),
                ["window"] = window.Length > 0 ? window : "default",
                ["windowed"] = Strip(chosen ?? week),
                ["daily"] = DailyTotals(s, records, files, nowMs, dailyDays),
            };
            stdout.Write(JsonText.Stringify(document, 2) + "\n");
            return 0;
        }

        var o = new List<string>
        {
            $"agent-hierarchy usage — {Timestamps.ToIsoString(now)[..16].Replace("T", " ", StringComparison.Ordinal)}",
            $"data: state database ({records.Count.ToString(CultureInfo.InvariantCulture)} subagent records)",
        };
        if (records.Count == 0)
        {
            o.Add(string.Empty);
            o.Add("No usage recorded yet. The collector runs on SubagentStop, so data appears");
            o.Add("once the plugin is enabled and a session dispatches subagents.");
        }

        if (latestSession is { IsTruthy: true } latest)
        {
            o.Add(string.Empty);
            o.Add($"SESSION {SliceHead(JsConvert.ToJsString(latest), 8)} (latest with subagent activity)");
            o.AddRange(RenderTable(session));
        }

        if (chosen is not null)
        {
            o.Add(string.Empty);
            o.Add($"{window.ToUpperInvariant()} — output tokens by role");
            o.AddRange(RenderTable(chosen));
            o.Add(string.Empty);
            o.AddRange(RenderBars(chosen));
        }
        else if (records.Count > 0)
        {
            o.Add(string.Empty);
            o.Add("LAST 7 DAYS — output tokens by role");
            o.AddRange(RenderBars(week));
        }

        var daily = DailyTotals(s, records, files, nowMs, dailyDays);
        if (daily.Count > 0)
        {
            o.Add(string.Empty);
            o.Add($"DAILY output tokens (last {dailyDays.ToString(CultureInfo.InvariantCulture)} days)");
            o.AddRange(RenderDaily(daily));
        }

        var others = (chosen ?? week).OtherTypes;
        if (others.Count > 0)
        {
            o.Add(string.Empty);
            var top = others.Keys
                .Select((k, i) => (Key: k, Count: others[k].AsNumber(), Index: i))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Index)
                .Take(5)
                .Select(x => $"{x.Key} ×{JsConvert.ToJsString(x.Count)}");
            o.Add($"\"other\" agent types: {string.Join(", ", top)}");
        }

        o.Add(string.Empty);
        o.Add("in = fresh input tokens; cache reads shown separately (≈10x cheaper).");
        stdout.Write(string.Join("\n", o) + "\n");
        return 0;
    }

    // ---------------------------------------------------------------- records and cache

    /// <summary>Agent → role for the working directory's registry rows, so custom roles and overrides are attributed; null when the configuration cannot be resolved.</summary>
    private static JsonValue? AgentRoles(UsageReportServices s)
    {
        try
        {
            var resolved = s.Config.ResolveConfig(s.Cwd);
            var roles = resolved.Get("roles");
            var map = new JsonObject();
            foreach (var role in s.Config.RegistryRoles(resolved))
            {
                map[s.Config.RoleAgent(role, roles?.Member(role))] = role;
            }

            return map;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The records that name a string agent, one per session and agent: a resumed agent's later record sums its whole transcript, so it
    /// replaces the earlier one in the place the earlier one held.
    /// </summary>
    private static List<JsonValue> LatestPerAgent(JsonArray all)
    {
        var order = new List<string>();
        var latest = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var r in all)
        {
            if (r.Member("agent_id") is not { IsText: true })
            {
                continue;
            }

            var key = JsConvert.ToJsString(r.Member("session_id")) + "|" + JsConvert.ToJsString(r.Member("agent_id"));
            if (!latest.ContainsKey(key))
            {
                order.Add(key);
            }

            latest[key] = r;
        }

        return order.Select(k => latest[k]).ToList();
    }

    private static JsonValue? LatestSession(List<JsonValue> records)
    {
        JsonValue? best = null;
        foreach (var r in records)
        {
            var bestTs = best?.Member("ts") is { IsTruthy: true } t ? t : (JsonValue)string.Empty;
            if (r.Member("ts") is { } ts && JsGreater(ts, bestTs))
            {
                best = r;
            }
        }

        return best?.Member("session_id");
    }

    /// <summary><c>a &gt; b</c> in JavaScript for the values a timestamp field holds: strings by code unit, anything else as numbers.</summary>
    private static bool JsGreater(JsonValue a, JsonValue b)
    {
        if (a.IsText && b.IsText)
        {
            return string.CompareOrdinal(a.AsString(), b.AsString()) > 0;
        }

        var x = ToNumber(a);
        var y = ToNumber(b);
        return x > y;
    }

    private static double ToNumber(JsonValue v)
    {
        switch (v.Kind)
        {
            case JsonKind.Number:
                return v.AsNumber();
            case JsonKind.Bool:
                return v.AsBool() ? 1 : 0;
            case JsonKind.Null:
                return 0;
            case JsonKind.Text:
                var text = JsText.Trim(v.AsString());
                if (text.Length == 0)
                {
                    return 0;
                }

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
            default:
                return double.NaN;
        }
    }

    private static JsonObject LoadCache(UsageReportServices s)
    {
        try
        {
            var c = JsonText.Parse(s.Files.ReadText(s.Usage.CacheFile));
            // Version 1 counted a message once per transcript line, so its sums are read again from the transcripts.
            if (c.IsMap && c.AsObject().Get("v") is { IsNumber: true } v && v.AsNumber() == 2
                && c.AsObject().Get("files") is { IsMap: true })
            {
                return c.AsObject();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            // absent or corrupt: rebuilt from scratch
        }

        return new JsonObject { ["v"] = 2, ["files"] = new JsonObject() };
    }

    private static void SaveCache(UsageReportServices s, JsonObject cache)
    {
        try
        {
            var path = s.Usage.CacheFile;
            s.Writer.Mkdir(NodePath.Dirname(path));
            s.Writer.WriteAtomic(path, $"{path}.{s.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp", Utf8Text.Encode(JsonText.Stringify(cache, 0)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the cache is an optimisation: losing it costs a re-parse
        }
    }

    /// <summary>Reads [offset, end of the last complete line) of a transcript and adds its assistant turns to the entry's day buckets.</summary>
    private static void ScanFrom(UsageReportServices s, string file, JsonObject entry, DateTimeOffset now)
    {
        FileStat? stat;
        try
        {
            stat = s.Files.Stat(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (stat is not { } st)
        {
            return;
        }

        var size = (double)st.Size;
        var offset = entry.Get("offset") is { IsNumber: true } o ? o.AsNumber() : 0;
        if (size < offset)
        {
            offset = 0;
            entry["offset"] = 0;
        }

        if (size == offset)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = s.Files.ReadBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var start = (int)Math.Min(offset, bytes.Length);
        var end = (int)Math.Min(size, bytes.Length);
        var text = Utf8Text.Decode(bytes.AsSpan(start, Math.Max(0, end - start)));
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return;
        }

        var complete = text[..(lastNewline + 1)];
        entry["offset"] = offset + Encoding.UTF8.GetByteCount(complete);

        var today = Timestamps.ToIsoString(now)[..10];
        var days = entry.Get("days") is { IsMap: true } d ? d.AsObject() : new JsonObject();
        entry["days"] = days;
        var models = entry.Get("models") is { IsMap: true } m ? m.AsObject() : new JsonObject();
        entry["models"] = models;
        foreach (var line in complete.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonValue parsed;
            try
            {
                parsed = JsonText.Parse(line);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (s.Usage.AssistantUsageOf(parsed) is not { } assistant
                || (assistant.MessageId is not null && entry.Get("last_message") is { IsText: true } last && last.AsString() == assistant.MessageId))
            {
                continue;
            }

            entry["last_message"] = assistant.MessageId is null ? JsonValue.Null : assistant.MessageId;

            var turn = assistant.Usage;
            var day = assistant.Timestamp is { } stamp ? SliceHead(stamp, 10) : today;
            if (!days.TryGetValue(day, out var bucket) || !bucket.IsMap)
            {
                bucket = s.Usage.ZeroUsage();
                days[day] = bucket;
            }

            s.Usage.AddUsage(
                bucket.AsObject(),
                new JsonObject
                {
                    ["calls"] = 1,
                    ["in"] = turn.Member("input_tokens") ?? JsonValue.Null,
                    ["out"] = turn.Member("output_tokens") ?? JsonValue.Null,
                    ["cache_read"] = turn.Member("cache_read_input_tokens") ?? JsonValue.Null,
                    ["cache_create"] = turn.Member("cache_creation_input_tokens") ?? JsonValue.Null,
                });
            if (assistant.Model is { } name)
            {
                models[name] = (models.Get(name) is { IsNumber: true } count ? count.AsNumber() : 0) + 1;
            }
        }
    }

    // ---------------------------------------------------------------- aggregation

    private static bool InWindow(JsonValue? ts, double winStart)
    {
        if (winStart == 0)
        {
            return true;
        }

        return ts is { } t && Timestamps.TryParseMs(JsConvert.ToJsString(t), out var ms) && double.IsFinite(ms) && ms >= winStart;
    }

    private static bool StrictEquals(JsonValue? a, JsonValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var x = a.Value;
        var y = b.Value;
        if (x.Kind != y.Kind)
        {
            return false;
        }

        return x.Kind switch
        {
            JsonKind.Null => true,
            JsonKind.Bool => x.AsBool() == y.AsBool(),
            JsonKind.Number => x.AsNumber() == y.AsNumber(),
            JsonKind.Text => string.Equals(x.AsString(), y.AsString(), StringComparison.Ordinal),
            _ => false,
        };
    }

    private static Aggregate Collect(
        UsageReportServices s,
        List<JsonValue> records,
        JsonObject files,
        JsonValue? agentRoles,
        JsonValue? latestSession,
        double winStart,
        bool sessionOnly)
    {
        var result = new Aggregate();
        foreach (var r in records)
        {
            if (sessionOnly && !StrictEquals(r.Member("session_id"), latestSession))
            {
                continue;
            }

            if (!sessionOnly && !InWindow(r.Member("ts"), winStart))
            {
                continue;
            }

            var role = JsConvert.ToJsString(s.Usage.RoleFor(r.Member("agent_type"), agentRoles));
            if (!result.Roles.TryGetValue(role, out var b))
            {
                b = new Bucket(s.Usage.ZeroUsage());
                result.Roles[role] = b;
                result.Order[role] = 0;
            }

            b.Agents.Add(r.Member("agent_id")!.Value.AsString());
            if (r.Member("found") is { IsBool: true } found && !found.AsBool())
            {
                b.Missing += 1;
            }

            s.Usage.AddUsage(b.Usage, r);
            if (role == "other")
            {
                var type = r.Member("agent_type") is { IsTruthy: true } at ? JsConvert.ToJsString(at) : "(unknown)";
                result.OtherTypes[type] = (result.OtherTypes.Get(type) is { IsNumber: true } n ? n.AsNumber() : 0) + 1;
            }
        }

        var orch = new Bucket(s.Usage.ZeroUsage());
        foreach (var file in files.Keys)
        {
            var entry = files[file];
            if (!entry.IsMap)
            {
                continue;
            }

            var e = entry.AsObject();
            var entrySession = e.Get("session_id");
            if (sessionOnly && !StrictEquals(entrySession, latestSession))
            {
                continue;
            }

            if (!sessionOnly && winStart != 0)
            {
                var active = records.Any(r => StrictEquals(r.Member("session_id"), entrySession) && InWindow(r.Member("ts"), winStart));
                if (!active)
                {
                    continue;
                }
            }

            var contributed = false;
            if (e.Get("days") is { IsMap: true } days)
            {
                foreach (var day in days.AsObject().Keys)
                {
                    if (!sessionOnly && winStart != 0 && Timestamps.TryParseMs($"{day}T23:59:59Z", out var dayEnd) && dayEnd < winStart)
                    {
                        continue;
                    }

                    s.Usage.AddUsage(orch.Usage, days.AsObject()[day]);
                    contributed = true;
                }
            }

            if (contributed)
            {
                orch.Agents.Add(file);
            }
        }

        if (orch.Usage.Get("calls") is { } calls && ToNumber(calls) > 0)
        {
            if (!result.Roles.ContainsKey("orchestrator"))
            {
                result.Order["orchestrator"] = 0;
            }

            result.Roles["orchestrator"] = orch;
        }

        return result;
    }

    private static JsonObject DailyTotals(UsageReportServices s, List<JsonValue> records, JsonObject files, double nowMs, int days)
    {
        var totals = new JsonObject();
        var start = nowMs - (days * DayMs);
        foreach (var r in records)
        {
            if (!InWindow(r.Member("ts"), start) || r.Member("ts") is not { IsText: true } ts)
            {
                continue;
            }

            var day = SliceHead(ts.AsString(), 10);
            s.Usage.AddUsage(DayBucket(totals, day, s.Usage), r);
        }

        foreach (var file in files.Keys)
        {
            if (files[file].Member("days") is not { IsMap: true } byDay)
            {
                continue;
            }

            foreach (var day in byDay.AsObject().Keys)
            {
                if (Timestamps.TryParseMs($"{day}T23:59:59Z", out var dayEnd) && dayEnd < start)
                {
                    continue;
                }

                s.Usage.AddUsage(DayBucket(totals, day, s.Usage), byDay.AsObject()[day]);
            }
        }

        return totals;
    }

    private static JsonObject DayBucket(JsonObject totals, string day, IHierarchyUsage usage)
    {
        if (!totals.TryGetValue(day, out var existing) || !existing.IsMap)
        {
            existing = usage.ZeroUsage();
            totals[day] = existing;
        }

        return existing.AsObject();
    }

    private static JsonObject Strip(Aggregate a)
    {
        var roles = new JsonObject();
        foreach (var role in a.Order.Keys)
        {
            var b = a.Roles[role];
            var row = new JsonObject { ["agents"] = b.Agents.Count, ["missing"] = b.Missing };
            foreach (var key in UsageKeys)
            {
                row[key] = b.Usage.Get(key) ?? JsonValue.Null;
            }

            roles[role] = row;
        }

        return new JsonObject { ["roles"] = roles, ["otherTypes"] = a.OtherTypes };
    }

    // ---------------------------------------------------------------- rendering

    private static string SliceHead(string text, int length) => text.Length <= length ? text : text[..length];

    private static string Fmt(JsonValue n)
    {
        var d = ToNumber(n);
        if (d >= 1e6)
        {
            return $"{(d / 1e6).ToString("F1", CultureInfo.InvariantCulture)}M";
        }

        if (d >= 1e3)
        {
            return $"{(d / 1e3).ToString("F1", CultureInfo.InvariantCulture)}k";
        }

        return JsConvert.ToJsString(n);
    }

    private static string Pad(string text, int width) => text.PadRight(width);

    private static string RPad(string text, int width) => text.PadLeft(width);

    private static int Rank(string role) => role == "other" ? 99 : Array.IndexOf(RoleOrder, role) is var i and >= 0 ? i : 50;

    private static List<string> OrderedRoles(Aggregate a) =>
        a.Order.Keys
            .OrderBy(r => Rank(r))
            .ThenBy(r => r, Comparer<string>.Create((x, y) => Rank(x) == 50 && Rank(y) == 50 ? TextCompare.CompareText(x, y) : 0))
            .ToList();

    private static List<string> RenderTable(Aggregate a, string firstColumn = "role")
    {
        var lines = new List<string>
        {
            $"  {Pad(firstColumn, 14)}{RPad("agents", 7)}{RPad("calls", 7)}{RPad("out", 10)}{RPad("in", 10)}{RPad("cache-read", 12)}",
        };
        double agents = 0;
        var totalUsage = new JsonObject { ["calls"] = 0, ["in"] = 0, ["out"] = 0, ["cache_read"] = 0, ["cache_create"] = 0 };
        foreach (var role in OrderedRoles(a))
        {
            var b = a.Roles[role];
            var count = b.Agents.Count;
            var missing = b.Missing > 0
                ? $"   ({JsConvert.ToJsString(b.Missing)} transcript{(b.Missing > 1 ? "s" : string.Empty)} not found)"
                : string.Empty;
            lines.Add(
                $"  {Pad(role, 14)}{RPad(count.ToString(CultureInfo.InvariantCulture), 7)}{RPad(Field(b.Usage, "calls"), 7)}" +
                $"{RPad(Fmt(b.Usage["out"]), 10)}{RPad(Fmt(b.Usage["in"]), 10)}{RPad(Fmt(b.Usage["cache_read"]), 12)}{missing}");
            agents += count;
            AddInto(totalUsage, b.Usage);
        }

        lines.Add(
            $"  {Pad("total", 14)}{RPad(JsConvert.ToJsString(agents), 7)}{RPad(Field(totalUsage, "calls"), 7)}" +
            $"{RPad(Fmt(totalUsage["out"]), 10)}{RPad(Fmt(totalUsage["in"]), 10)}{RPad(Fmt(totalUsage["cache_read"]), 12)}");
        return lines;
    }

    private static string Field(JsonObject usage, string key) => JsConvert.ToJsString(usage.Get(key));

    private static void AddInto(JsonObject accumulator, JsonObject usage)
    {
        foreach (var key in UsageKeys)
        {
            accumulator[key] = ToNumber(accumulator[key]) + ToNumber(usage.Get(key) ?? JsonValue.Null);
        }
    }

    private static string Bar(double value, double max)
    {
        var width = Math.Max(value > 0 ? 1 : 0, JsRound(value / max * 24));
        return new string('█', (int)width);
    }

    private static double JsRound(double x)
    {
        var floor = Math.Floor(x);
        return x - floor >= 0.5 ? floor + 1 : floor;
    }

    private static List<string> RenderBars(Aggregate a)
    {
        var rows = OrderedRoles(a).Select(r => (Role: r, Value: ToNumber(a.Roles[r].Usage["out"]), Raw: a.Roles[r].Usage["out"])).ToList();
        var max = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Value));
        return rows.Select(r => $"  {Pad(r.Role, 14)}{Pad(Bar(r.Value, max), 25)}{Fmt(r.Raw)}").ToList();
    }

    private static List<string> RenderDaily(JsonObject totals)
    {
        var days = totals.Keys.ToList();
        days.Sort(string.CompareOrdinal);
        var max = Math.Max(1, days.Count == 0 ? 1 : days.Max(d => ToNumber(totals[d].AsObject()["out"])));
        return days.Select(d =>
        {
            var raw = totals[d].AsObject()["out"];
            return $"  {d}  {Pad(Bar(ToNumber(raw), max), 25)}{Fmt(raw)}";
        }).ToList();
    }

    private static double LocalMidnightMs(IClock clock, DateTimeOffset now)
    {
        var local = now + clock.LocalOffset(now);
        var wall = new DateTimeOffset(new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Utc));
        var offset = clock.LocalOffset(wall);
        var instant = wall - offset;
        offset = clock.LocalOffset(instant);
        return (wall - offset).ToUnixTimeMilliseconds();
    }
}
