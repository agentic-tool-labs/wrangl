using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Mesh;

/// <summary>
/// The peer mesh's links: the direct loops an Orchestrator allows between team members, one record per hierarchy dir and team in the
/// state database's <c>links</c> table (per-dir-files-to-db S11b; the old <c>&lt;hierarchy dir&gt;/links/&lt;team&gt;.json</c> is taken in once). A link is scoped to one task and ends by itself: when the task closes, when its message
/// count or its minutes run out, when a peer leaves the team, or when it is revoked. Expiry is judged lazily, whenever the file is
/// read or used.
/// </summary>
public interface ILinks
{
    /// <summary>The limits table, with <c>AH_LINK_LIMITS</c> (a JSON object) laid over it.</summary>
    JsonObject Limits();

    /// <summary>The team's links record, or an empty record when there is none or it cannot be read.</summary>
    JsonObject ReadLinks(string dir, string team);

    /// <summary>Whether <paramref name="link"/> carries a message from <paramref name="sender"/> to <paramref name="recipient"/>.</summary>
    bool Covers(JsonValue link, JsonValue? sender, JsonValue? recipient);

    /// <summary>The instant, in epoch milliseconds, a link's granted minutes run out.</summary>
    double LinkLapseMs(JsonValue link);

    /// <summary>Why <paramref name="link"/> is no longer active at <paramref name="nowMs"/> (<c>{reason, at}</c>), or null while it is.</summary>
    JsonObject? EndReason(JsonValue link, string dir, string team, double nowMs);

    /// <summary>Every link with its state judged at <paramref name="nowMs"/>; nothing is written.</summary>
    JsonArray Judge(string dir, string team, double nowMs);

    /// <summary>The active links that cover <paramref name="sender"/> to <paramref name="recipient"/>.</summary>
    JsonArray ActiveCovering(string dir, string team, JsonValue? sender, JsonValue? recipient, double nowMs);

    /// <summary>How many active requested links <paramref name="address"/> is on; a link opened by assignment is not counted.</summary>
    int ActiveCount(string dir, string team, string address, double nowMs);

    /// <summary>
    /// Whether <paramref name="record"/> (a links record as read) holds a link the Orchestrator revoked that was opened for the
    /// assignment <paramref name="key"/> between these two, in either direction. A link the tick ended does not count.
    /// </summary>
    bool RevokedAssignment(JsonObject record, string key, JsonValue? a, JsonValue? b);

    /// <summary>The most recent link covering from to to that ended by its message cap within the extension grace, or null.</summary>
    JsonValue? RecentlyCapped(string dir, string team, JsonValue? sender, JsonValue? recipient, double nowMs);

    /// <summary>
    /// The close note of a review-loop link that just ended, <c>[loop &lt;id&gt; closed &lt;reason&gt;] task &lt;task&gt; rounds &lt;n&gt;</c>, for one row of an
    /// <c>ended</c> list (<c>[id, reason, used]</c>); null when the link is not a review loop or cannot be found.
    /// </summary>
    string? LoopClosedText(string dir, string team, JsonValue endedRow);

    /// <summary>The <c>links</c> state domain: read, grant, revoke, use, release, extend, pend, sweep, deny.</summary>
    IStateDomain StateDomain();
}

/// <summary>
/// The links library over its collaborators; <paramref name="Hierarchy"/> lists a task's exchange over the pools <paramref name="Messages"/>
/// enumerates for the link's team, <paramref name="Roster"/> reads the team.
/// </summary>
public sealed record Links(
    ILinkStore Store,
    IAhEnvironment Environment,
    IRosterCore Roster,
    IHierarchyCore Hierarchy,
    IHierarchyMessages Messages,
    IClock Clock,
    IRandom Random) : ILinks
{
    private static readonly JsonObject Defaults = new()
    {
        ["messages"] = 12,
        ["minutes"] = 30,
        ["activePerMember"] = 2,
        ["peersPerLink"] = 2,
        ["extendGraceMs"] = 10 * 60 * 1000,
        ["extensionsBeforeUser"] = 2,
    };

    public JsonObject Limits()
    {
        var table = Defaults.Clone();
        try
        {
            if (Environment.GetVariable("AH_LINK_LIMITS") is { Length: > 0 } text)
            {
                var over = JsonText.Parse(text);
                if (over.IsMap)
                {
                    foreach (var key in over.AsObject().Keys)
                    {
                        table[key] = over.AsObject()[key];
                    }
                }
                else if (over.IsArray)
                {
                    // `{...table, ...array}` lays the elements over the table under their indexes.
                    for (var i = 0; i < over.AsArray().Count; i++)
                    {
                        table[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = over.AsArray()[i];
                    }
                }
            }
        }
        catch (JsonParseException)
        {
            return Defaults.Clone();
        }

        return table;
    }

    private static JsonObject EmptyRecord() => new() { ["v"] = 1, ["links"] = new JsonArray(), ["denied"] = new JsonArray() };

    public JsonObject ReadLinks(string dir, string team) => RecordOf(Store.Body(dir, team));

    /// <summary>A stored record laid over the empty one; an empty record when there is none or it is no links record.</summary>
    private static JsonObject RecordOf(string? body)
    {
        if (body is null)
        {
            return EmptyRecord();
        }

        try
        {
            var o = JsonText.Parse(body);
            if (!o.IsMap || o.AsObject().Get("links") is not { IsArray: true })
            {
                return EmptyRecord();
            }

            var merged = EmptyRecord();
            foreach (var key in o.AsObject().Keys)
            {
                merged[key] = o.AsObject()[key];
            }

            return merged;
        }
        catch (Exception e) when (e is IOException or JsonParseException or UnauthorizedAccessException)
        {
            return EmptyRecord();
        }
    }

    // ---------------------------------------------------------------- JavaScript semantics of the fields

    private static void NullCheck(JsonValue v)
    {
        if (v.IsNull)
        {
            throw new InvalidOperationException("cannot read a property of a null link");
        }
    }

    private static JsonValue? Field(JsonValue link, string name)
    {
        NullCheck(link);
        return link.Member(name);
    }

    private static bool IsText(JsonValue? v, string text) => v is { IsText: true } t && t.AsString() == text;

    private static double Num(JsonValue? v) => JsConvert.ToNumber(v);

    /// <summary><c>a &gt;= b</c>: two strings compare as text, anything else as numbers.</summary>
    private static bool GreaterOrEqual(JsonValue? a, JsonValue? b)
    {
        if (a is { IsText: true } x && b is { IsText: true } y)
        {
            return string.CompareOrdinal(x.AsString(), y.AsString()) >= 0;
        }

        return Num(a) >= Num(b);
    }

    /// <summary><c>a + b</c> with <c>b</c> a number: numbers add, a string concatenates.</summary>
    private static JsonValue Add(JsonValue? a, double b)
    {
        if (a is { IsText: true } s)
        {
            return s.AsString() + JsConvert.NumberToString(b);
        }

        return Num(a) + b;
    }

    private static double DateParse(JsonValue? v) =>
        v is { IsText: true } t && Timestamps.TryParseMs(t.AsString(), out var ms) ? ms : double.NaN;

    private static string Iso(double ms) => Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));

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
            JsonKind.Text => x.AsString() == y.AsString(),
            _ => false,
        };
    }

    // ---------------------------------------------------------------- judging

    /// <summary>The set of addresses on a link: its requester, then its peers (a string spreads into its characters).</summary>
    private static List<JsonValue?> AddressesOf(JsonValue link)
    {
        var all = new List<JsonValue?> { Field(link, "requester") };
        switch (Field(link, "peers"))
        {
            case { IsArray: true } list:
                all.AddRange(list.AsArray().Select(v => (JsonValue?)v));
                break;
            case { IsText: true } text:
                all.AddRange(JsText.CodePoints(text.AsString()).Select(cp => (JsonValue?)char.ConvertFromUtf32(cp)));
                break;
            default:
                throw new InvalidOperationException("the link's peers are not iterable");
        }

        return all;
    }

    public bool Covers(JsonValue link, JsonValue? sender, JsonValue? recipient)
    {
        if (StrictEquals(sender, recipient))
        {
            return false;
        }

        if (IsText(Field(link, "direction"), "out"))
        {
            if (!StrictEquals(sender, Field(link, "requester")))
            {
                return false;
            }

            return Field(link, "peers") switch
            {
                { IsArray: true } list => list.AsArray().Any(p => StrictEquals(p, recipient)),
                { IsText: true } text => text.AsString().Contains(JsConvert.ToJsString(recipient), StringComparison.Ordinal),
                _ => throw new InvalidOperationException("the link's peers have no includes"),
            };
        }

        var on = AddressesOf(link);
        return on.Any(a => StrictEquals(a, sender)) && on.Any(a => StrictEquals(a, recipient));
    }

    /// <summary>
    /// What the end-reason decisions of one pass read about a team and its pools, read once however many links the pass judges: the
    /// pools of the team, the exchanges over them and the task home's archive listing, and the team record.
    /// </summary>
    private sealed class LinkEnv
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _pools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonArray> _exchanges = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<JsonValue>> _archive = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonValue> _teams = new(StringComparer.Ordinal);

        public JsonArray Exchanges(Links links, string dir, string team, string home)
        {
            var poolsKey = dir + "\0" + team;
            var key = poolsKey + "\0" + home;
            if (!_exchanges.TryGetValue(key, out var listed))
            {
                if (!_pools.TryGetValue(poolsKey, out var pools))
                {
                    pools = links.Messages.PoolDirs(dir, [team]);
                    _pools[poolsKey] = pools;
                }

                listed = links.Hierarchy.ListExchanges(pools.Contains(home) ? pools : [.. pools, home]);
                _exchanges[key] = listed;
            }

            return listed;
        }

        /// <summary>The archived responses of the pool <paramref name="home"/>, as <see cref="IHierarchyMessages.ListMessages"/> gives them.</summary>
        public IReadOnlyList<JsonValue> Archive(Links links, string home)
        {
            if (!_archive.TryGetValue(home, out var responses))
            {
                responses = [.. links.Messages.ListMessages(home, includeArchive: true, ["response"]).Where(m => m.Member("archived") is { IsTruthy: true })];
                _archive[home] = responses;
            }

            return responses;
        }

        public JsonValue Team(Links links, string dir, string team)
        {
            var key = dir + "\0" + team;
            if (!_teams.TryGetValue(key, out var record))
            {
                record = links.Roster.ReadTeamRecord(dir, team);
                _teams[key] = record;
            }

            return record;
        }
    }

    /// <summary>
    /// Whether the link's task was closed on purpose: its response file says <c>closed: true</c> (<c>ah msg close</c>) or
    /// <c>withdrawn: true</c> (<c>ah msg withdraw</c>), whether the exchange is listed or already archived. A member's plain response
    /// leaves the task's link live; a link otherwise ends only on revoke, its cap or its time.
    /// </summary>
    private bool TaskClosed(JsonValue link, string dir, string team, LinkEnv env)
    {
        var task = Field(link, "task");
        var home = Field(link, "task_home") is { IsTruthy: true } h && h.IsText ? h.AsString() : dir;
        try
        {
            foreach (var e in env.Exchanges(this, dir, team, home))
            {
                if (StrictEquals(e.Member("id"), task))
                {
                    return ExchangeClosedOnPurpose(Hierarchy, e);
                }
            }

            var id = JsConvert.ToJsString(task);
            return env.Archive(this, home)
                .Where(m => m.Member("meta")?.Member("id") is { IsText: true } i && i.AsString() == id && m.Member("path") is { IsText: true })
                .Any(m => ClosedOnPurpose(Hierarchy, m.Member("path")!.Value.AsString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a listed exchange was closed on purpose: it is not open and its response says <c>closed: true</c> or <c>withdrawn: true</c>.
    /// A member's plain response does not count, so its task stays linkable until the lead closes it.
    /// </summary>
    internal static bool ExchangeClosedOnPurpose(IHierarchyCore core, JsonValue exchange) =>
        exchange.Member("open") is not { IsTruthy: true }
            && exchange.Member("response")?.Member("path") is { IsText: true } path && ClosedOnPurpose(core, path.AsString());

    /// <summary>
    /// Whether the front matter of the response at <paramref name="path"/>, read through the msgs library (the stored body of a sealed
    /// message in db mode), has a <c>closed: true</c> or <c>withdrawn: true</c> line; a response that cannot be read is not closed.
    /// </summary>
    internal static bool ClosedOnPurpose(IHierarchyCore core, string path) =>
        core.ReadMsgFile(path).Member("text") is { IsText: true } text && ClosedOnPurposeText(text.AsString());

    /// <summary>Whether the front matter of the response <paramref name="text"/> has a <c>closed: true</c> or <c>withdrawn: true</c> line.</summary>
    private static bool ClosedOnPurposeText(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r'));
        return lines.FirstOrDefault() == "---"
            && lines.Skip(1).TakeWhile(l => l != "---").Any(l => l is "closed: true" or "withdrawn: true");
    }

    public double LinkLapseMs(JsonValue link)
    {
        var limits = Field(link, "limits") ?? throw new InvalidOperationException("the link has no limits");
        NullCheck(limits);
        return DateParse(Field(link, "granted_at")) + (Num(limits.Member("minutes")) * 60_000);
    }

    /// <summary>Whether the link was opened because its members are assigned together (it carries the assignment's key).</summary>
    private static bool IsAssigned(JsonValue link) => Field(link, "assigned") is { IsText: true };

    private static JsonObject Ended(string reason, double at) => new() { ["reason"] = reason, ["at"] = at };

    private static double TimeOr(JsonValue? stamp, double fallback)
    {
        var t = DateParse(stamp);
        return double.IsNaN(t) || t == 0 ? fallback : t;
    }

    public JsonObject? EndReason(JsonValue link, string dir, string team, double nowMs) => EndReason(link, dir, team, nowMs, new LinkEnv());

    private JsonObject? EndReason(JsonValue link, string dir, string team, double nowMs, LinkEnv env)
    {
        var state = Field(link, "state");
        if (IsText(state, "revoked"))
        {
            return Ended("revoked", TimeOr(Field(link, "ended_at"), nowMs));
        }

        if (IsText(state, "expired"))
        {
            var recorded = Field(link, "end_reason") is { IsTruthy: true } r ? JsConvert.ToJsString(r) : "expired";
            return Ended(recorded, TimeOr(Field(link, "ended_at"), nowMs));
        }

        if (TaskClosed(link, dir, team, env))
        {
            return Ended("task_closed", nowMs);
        }

        // An assignment link is extended at its caps for as long as the assignment lasts (Use), so neither cap ends it.
        if (!IsAssigned(link))
        {
            var limits = Field(link, "limits") ?? throw new InvalidOperationException("the link has no limits");
            NullCheck(limits);
            if (GreaterOrEqual(Field(link, "used"), limits.Member("messages")))
            {
                return Ended("message_cap", TimeOr(Field(link, "last_used_at"), nowMs));
            }

            var lapse = LinkLapseMs(link);
            if (nowMs >= lapse)
            {
                return Ended("time", lapse);
            }
        }

        var team0 = env.Team(this, dir, team);
        if (!team0.IsNull)
        {
            var names = new List<JsonValue?>();
            if (team0.Member("members") is { IsTruthy: true } members && members.IsArray)
            {
                foreach (var m in members.AsArray())
                {
                    names.Add(m.IsTruthy ? m.Member("name") : m);
                }
            }

            bool OnTeam(JsonValue? address)
            {
                if (address is not { IsText: true } a)
                {
                    throw new InvalidOperationException("an address is not a string");
                }

                var text = a.AsString();
                if (text.StartsWith("orchestrator@", StringComparison.Ordinal))
                {
                    return true;
                }

                var local = text.Split('@')[0];
                return names.Any(n => n is { IsText: true } t && t.AsString() == local);
            }

            var addresses = AddressesOf(link);
            if (!addresses.All(OnTeam))
            {
                return Ended("member_left", nowMs);
            }
        }

        return null;
    }

    private static JsonObject CopyOf(JsonValue v) => v.IsMap ? v.AsObject().Clone() : new JsonObject();

    public JsonArray Judge(string dir, string team, double nowMs)
    {
        var result = new JsonArray();
        var env = new LinkEnv();
        foreach (var l in ReadLinks(dir, team)["links"].AsArray())
        {
            if (!IsText(Field(l, "state"), "active"))
            {
                result.Add(l);
                continue;
            }

            var end = EndReason(l, dir, team, nowMs, env);
            if (end is null)
            {
                result.Add(l);
                continue;
            }

            var copy = CopyOf(l);
            copy["state"] = "expired";
            copy["ended_at"] = Iso(end["at"].AsNumber());
            copy["end_reason"] = end["reason"];
            result.Add(copy);
        }

        return result;
    }

    public JsonArray ActiveCovering(string dir, string team, JsonValue? sender, JsonValue? recipient, double nowMs)
    {
        var result = new JsonArray();
        foreach (var l in Judge(dir, team, nowMs))
        {
            if (IsText(l.Member("state"), "active") && Covers(l, sender, recipient))
            {
                result.Add(l);
            }
        }

        return result;
    }

    public int ActiveCount(string dir, string team, string address, double nowMs)
    {
        var count = 0;
        foreach (var l in Judge(dir, team, nowMs))
        {
            if (IsText(l.Member("state"), "active") && !IsAssigned(l) && AddressesOf(l).Any(a => a is { IsText: true } t && t.AsString() == address))
            {
                count++;
            }
        }

        return count;
    }

    public bool RevokedAssignment(JsonObject record, string key, JsonValue? a, JsonValue? b) =>
        record.Get("links") is { IsArray: true } rows && rows.AsArray().Any(l =>
            IsText(Field(l, "state"), "revoked") && IsText(Field(l, "end_reason"), "revoked") && IsText(Field(l, "assigned"), key)
            && (Covers(l, a, b) || Covers(l, b, a)));

    public JsonValue? RecentlyCapped(string dir, string team, JsonValue? sender, JsonValue? recipient, double nowMs)
    {
        var grace = Num(Limits().Get("extendGraceMs"));
        var candidates = new List<(JsonValue Link, double Ended)>();
        foreach (var l in Judge(dir, team, nowMs))
        {
            if (!IsText(l.Member("state"), "expired") || !(IsText(l.Member("end_reason"), "message_cap") || IsText(l.Member("end_reason"), "cap")) || !Covers(l, sender, recipient))
            {
                continue;
            }

            var stamp = l.Member("ended_at") is { IsTruthy: true } e ? e : l.Member("granted_at");
            if (nowMs - DateParse(stamp) <= grace)
            {
                candidates.Add((l, DateParse(l.Member("ended_at"))));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Array.prototype.sort is stable and the comparator is a plain subtraction: NaN differences compare as equal.
        var ordered = candidates.OrderByDescending(c => c.Ended, Comparer<double>.Create((x, y) => double.IsNaN(x) || double.IsNaN(y) ? 0 : x.CompareTo(y))).ToList();
        return ordered[0].Link;
    }

    /// <summary>The review rounds a loop link allows; absent on every other link.</summary>
    internal static double? RoundCap(JsonValue link) =>
        link.Member("limits") is { IsMap: true } limits && limits.Member("rounds") is { IsNumber: true } rounds ? rounds.AsNumber() : null;

    private static double RoundsDone(JsonObject link) => link.Get("rounds") is { IsNumber: true } n && double.IsFinite(n.AsNumber()) ? n.AsNumber() : 0;

    private static readonly Dictionary<string, string> LoopCloseWords = new(StringComparer.Ordinal)
    {
        ["cap"] = "cap",
        ["time"] = "minutes",
        ["task_closed"] = "report",
        ["revoked"] = "revoked",
        ["member_left"] = "member-gone",
        ["message_cap"] = "messages",
    };

    public string? LoopClosedText(string dir, string team, JsonValue endedRow)
    {
        if (!endedRow.IsArray || endedRow.AsArray().Count < 2 || endedRow.AsArray()[0] is not { IsText: true } id)
        {
            return null;
        }

        var link = ReadLinks(dir, team).Get("links") is { IsArray: true } list
            ? list.AsArray().FirstOrDefault(l => l.IsMap && l.Member("id") is { IsText: true } lid && lid.AsString() == id.AsString())
            : (JsonValue?)null;
        if (link is not { IsMap: true } found || RoundCap(found) is null)
        {
            return null;
        }

        var reason = JsConvert.ToJsString(endedRow.AsArray()[1]);
        var word = LoopCloseWords.TryGetValue(reason, out var mapped) ? mapped : reason;
        var rounds = found.Member("rounds") is { IsNumber: true } n ? n.AsNumber() : 0;
        return $"[loop {id.AsString()} closed {word}] task {JsConvert.ToJsString(found.Member("task") ?? JsonValue.Null)} rounds {JsConvert.ToJsString(rounds)}";
    }

    public IStateDomain StateDomain() => new LinksStateDomain(this);

    // ---------------------------------------------------------------- operations (the engine hands each a write context)

    private static double NowMsOf(JsonValue args, IClock clock) =>
        args.Member("now") is { IsNumber: true } n && double.IsFinite(n.AsNumber()) ? n.AsNumber() : clock.UtcNow.ToUnixTimeMilliseconds();

    private static JsonValue Short(List<JsonValue> rows)
    {
        var array = new JsonArray();
        array.AddRange(rows.Take(4));
        return array;
    }

    /// <summary>Writes the expiry of every lapsed active link into <paramref name="data"/>; the links that ended on this call.</summary>
    private List<JsonValue> PersistExpiry(string dir, string team, JsonObject data, double nowMs)
    {
        var ended = new List<JsonValue>();
        var env = new LinkEnv();
        foreach (var l in data["links"].AsArray())
        {
            if (!IsText(Field(l, "state"), "active"))
            {
                continue;
            }

            var end = EndReason(l, dir, team, nowMs, env);
            if (end is null)
            {
                continue;
            }

            var o = l.AsObject();
            o["state"] = "expired";
            o["ended_at"] = Iso(end["at"].AsNumber());
            o["end_reason"] = end["reason"];
            var row = new JsonArray { o.Get("id") ?? JsonValue.Null, end["reason"], o.Get("used") ?? JsonValue.Null };
            ended.Add(row);
        }

        return ended;
    }

    private void WriteLinks(IDbOpTransaction ctx, string? dir, string team, JsonObject data)
    {
        if (dir is null)
        {
            throw new InvalidOperationException("dir must be a string");
        }

        // the text the old file held, so an imported record and a written one read alike
        Store.Put(ctx, dir, team, JsonText.Stringify(data, 1) + "\n");
    }

    /// <summary>The <c>dir</c> argument when it is a string. Without one a read finds no links and a write fails, as the path join does.</summary>
    private static string? DirOf(JsonValue args) => args.Member("dir") is { IsText: true } d ? d.AsString() : null;

    private JsonObject Load(IDbOpTransaction ctx, string? dir, string team) => dir is null ? EmptyRecord() : RecordOf(Store.Body(ctx, dir, team));

    private static string TeamOf(JsonValue args) =>
        args.Member("team") is { IsNull: false } t ? JsConvert.ToJsString(t) : "undefined";

    private static JsonObject? FindById(JsonObject data, JsonValue? id)
    {
        foreach (var x in data["links"].AsArray())
        {
            if (StrictEquals(Field(x, "id"), id))
            {
                return x.IsMap ? x.AsObject() : null;
            }
        }

        return null;
    }

    private static JsonValue Undefined(JsonValue? v) => v ?? JsonValue.Null;

    private static void Put(JsonObject o, string key, JsonValue? v)
    {
        // JSON.stringify leaves out a member whose value is undefined.
        if (v is not null)
        {
            o[key] = v.Value;
        }
    }

    internal JsonValue Read(JsonValue args)
    {
        var dir = DirOf(args);
        var links = dir is null ? new JsonArray() : Judge(dir, TeamOf(args), NowMsOf(args, Clock));
        return new JsonObject { ["links"] = links };
    }

    internal JsonValue Grant(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var nowMs = NowMsOf(args, Clock);
        var ended = PersistExpiry(dir!, team, data, nowMs);
        var l = args.Member("link") ?? throw new InvalidOperationException("link is required");
        NullCheck(l);
        string id;
        if (l.Member("id") is { IsText: true } given && given.AsString().Length > 0)
        {
            id = given.AsString();
        }
        else
        {
            id = "lk-" + Ids.NewId(new FixedClock(Clock, DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs)), Random);
        }

        foreach (var x in data["links"].AsArray())
        {
            if (StrictEquals(Field(x, "id"), id))
            {
                return new JsonObject { ["id"] = id, ["existed"] = true, ["ended"] = Short(ended) };
            }
        }

        // One active link per pair and assignment: two sends racing to open it get the same one (b481).
        if (l.Member("assigned") is { IsText: true } key)
        {
            foreach (var x in data["links"].AsArray())
            {
                if (IsText(Field(x, "state"), "active") && StrictEquals(Field(x, "assigned"), key)
                    && l.Member("peers") is { IsArray: true } pair && pair.AsArray().Count == 1 && Covers(x, l.Member("requester"), pair.AsArray()[0]))
                {
                    if (ended.Count > 0)
                    {
                        WriteLinks(ctx, dir, team, data);
                    }

                    return new JsonObject { ["id"] = Undefined(Field(x, "id")), ["existed"] = true, ["ended"] = Short(ended) };
                }
            }

            // the Orchestrator's revoke may have landed after the rule looked: its decision stands, nothing is opened
            if (l.Member("peers") is { IsArray: true } to && to.AsArray().Count == 1 && RevokedAssignment(data, key.AsString(), l.Member("requester"), to.AsArray()[0]))
            {
                if (ended.Count > 0)
                {
                    WriteLinks(ctx, dir, team, data);
                }

                return new JsonObject { ["revoked"] = true, ["ended"] = Short(ended) };
            }
        }

        var limits = Limits();
        var link = new JsonObject { ["id"] = id };
        Put(link, "task", l.Member("task"));
        link["task_home"] = l.Member("task_home") is { IsTruthy: true } home ? home : dir is null ? JsonValue.Null : dir;
        Put(link, "peers", l.Member("peers"));
        link["direction"] = IsText(l.Member("direction"), "out") ? "out" : "both";
        Put(link, "requester", l.Member("requester"));
        Put(link, "granted_by", l.Member("granted_by"));
        Put(link, "anchor", l.Member("anchor"));
        Put(link, "assigned", l.Member("assigned"));
        link["granted_at"] = Iso(nowMs);
        var granted = new JsonObject
        {
            ["messages"] = l.Member("messages") is { IsTruthy: true } m ? m : Undefined(limits.Get("messages")),
            ["minutes"] = l.Member("minutes") is { IsTruthy: true } mi ? mi : Undefined(limits.Get("minutes")),
        };
        if (l.Member("rounds") is { IsNumber: true } rounds && rounds.AsNumber() > 0)
        {
            granted["rounds"] = rounds;
        }

        link["limits"] = granted;
        link["used"] = 0;
        link["extensions"] = 0;
        link["state"] = "active";
        data["links"].AsArray().Add(link);
        WriteLinks(ctx,dir, team, data);
        return new JsonObject { ["id"] = id, ["ended"] = Short(ended) };
    }

    internal JsonValue Revoke(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var nowMs = NowMsOf(args, Clock);
        var ended = PersistExpiry(dir!, team, data, nowMs);
        var id = args.Member("id");
        var l = FindById(data, id);
        var wasActive = l is not null && IsText(l.Get("state"), "active");
        if (wasActive)
        {
            l!["state"] = "revoked";
            l["ended_at"] = Iso(nowMs);
            l["end_reason"] = args.Member("reason") is { IsTruthy: true } reason ? reason : (JsonValue)"revoked";
        }

        WriteLinks(ctx,dir, team, data);
        var result = new JsonObject();
        Put(result, "id", id);
        result["found"] = l is not null;
        result["revoked"] = wasActive;
        result["ended"] = Short(ended);
        return result;
    }

    internal JsonValue Use(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var nowMs = NowMsOf(args, Clock);
        var ended = PersistExpiry(dir!, team, data, nowMs);
        JsonObject? l = null;
        foreach (var x in data["links"].AsArray())
        {
            if (IsText(Field(x, "state"), "active") && Covers(x, args.Member("from"), args.Member("to"))
                && (args.Member("id") is not { IsText: true } wanted || StrictEquals(Field(x, "id"), wanted)))
            {
                l = x.AsObject();
                break;
            }
        }

        if (l is null)
        {
            if (ended.Count > 0)
            {
                WriteLinks(ctx,dir, team, data);
            }

            return new JsonObject { ["none"] = true, ["ended"] = Short(ended) };
        }

        l["used"] = Add(l.Get("used"), 1);
        l["last_used_at"] = Iso(nowMs);
        var limits = l.Get("limits") ?? throw new InvalidOperationException("the link has no limits");
        if (IsAssigned(l))
        {
            ExtendAssigned(l, limits.AsObject(), nowMs);
        }
        else if (GreaterOrEqual(l.Get("used"), limits.Member("messages")))
        {
            l["state"] = "expired";
            l["ended_at"] = l["last_used_at"];
            l["end_reason"] = "message_cap";
            ended.Add(new JsonArray { Undefined(l.Get("id")), "message_cap", Undefined(l.Get("used")) });
        }

        // A review loop's round ends with the reviewer side's report; the last allowed round closes the link.
        var roundCap = RoundCap(l);
        if (roundCap is not null && args.Member("round_end") is { IsTruthy: true })
        {
            l["rounds"] = RoundsDone(l) + 1;
            if (RoundsDone(l) >= roundCap && IsText(l.Get("state"), "active"))
            {
                l["state"] = "expired";
                l["ended_at"] = l["last_used_at"];
                l["end_reason"] = "cap";
                ended.Add(new JsonArray { Undefined(l.Get("id")), "cap", Undefined(l.Get("used")) });
            }
        }

        WriteLinks(ctx,dir, team, data);
        var result = new JsonObject();
        Put(result, "id", l.Get("id"));
        result["n"] = Undefined(l.Get("used"));
        Put(result, "task", l.Get("task"));
        if (roundCap is not null)
        {
            result["rounds"] = RoundsDone(l);
            result["rounds_cap"] = roundCap.Value;
        }

        result["ended"] = Short(ended);
        return result;
    }

    /// <summary>
    /// An assignment link at a cap gets the default allowance again, and the extension is counted: its members are still assigned, so
    /// the link is never exhausted. The minutes grow by whole allowances until they cover <paramref name="nowMs"/>.
    /// </summary>
    private void ExtendAssigned(JsonObject link, JsonObject limits, double nowMs)
    {
        var defaults = Limits();
        var extended = false;
        if (GreaterOrEqual(link.Get("used"), limits.Get("messages")))
        {
            limits["messages"] = Add(limits.Get("messages"), Num(defaults.Get("messages")));
            extended = true;
        }

        var allowance = Num(defaults.Get("minutes")) * 60_000;
        var over = nowMs - LinkLapseMs(link);
        if (over >= 0 && allowance > 0)
        {
            limits["minutes"] = Add(limits.Get("minutes"), Num(defaults.Get("minutes")) * (Math.Floor(over / allowance) + 1));
            extended = true;
        }

        if (extended)
        {
            link["extensions"] = Add(link.Get("extensions") is { IsTruthy: true } ex ? ex : (JsonValue)0, 1);
        }
    }

    /// <summary>
    /// Ends one review round of a loop link by id: the reviewer's report answered the implementer's brief, which no message count covers.
    /// The last allowed round closes the link with <c>cap</c>. A link that is not an active loop link is left as it is.
    /// </summary>
    internal JsonValue Round(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var nowMs = NowMsOf(args, Clock);
        var ended = PersistExpiry(dir!, team, data, nowMs);
        var l = FindById(data, args.Member("id"));
        if (l is null || !IsText(l.Get("state"), "active") || RoundCap(l) is not { } cap)
        {
            if (ended.Count > 0)
            {
                WriteLinks(ctx,dir, team, data);
            }

            return new JsonObject { ["none"] = true, ["ended"] = Short(ended) };
        }

        l["rounds"] = RoundsDone(l) + 1;
        if (RoundsDone(l) >= cap)
        {
            l["state"] = "expired";
            l["ended_at"] = Iso(nowMs);
            l["end_reason"] = "cap";
            ended.Add(new JsonArray { Undefined(l.Get("id")), "cap", Undefined(l.Get("used")) });
        }

        WriteLinks(ctx,dir, team, data);
        var result = new JsonObject();
        Put(result, "id", l.Get("id"));
        Put(result, "task", l.Get("task"));
        result["rounds"] = RoundsDone(l);
        result["rounds_cap"] = cap;
        result["ended"] = Short(ended);
        return result;
    }

    internal JsonValue Release(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var id = args.Member("id");
        var l = FindById(data, id);
        if (l is not null && Num(l.Get("used")) > 0)
        {
            l["used"] = Num(l.Get("used")) - 1;
            var limits = l.Get("limits") ?? throw new InvalidOperationException("the link has no limits");
            if (RoundCap(l) is not null && args.Member("round_end") is { IsTruthy: true } && RoundsDone(l) > 0)
            {
                l["rounds"] = RoundsDone(l) - 1;
            }

            var underRounds = RoundCap(l) is not { } cap || RoundsDone(l) < cap;
            if (IsText(l.Get("state"), "expired") && (IsText(l.Get("end_reason"), "message_cap") || IsText(l.Get("end_reason"), "cap"))
                && !GreaterOrEqual(l.Get("used"), limits.Member("messages")) && underRounds)
            {
                l["state"] = "active";
                l.Remove("ended_at");
                l.Remove("end_reason");
            }

            WriteLinks(ctx,dir, team, data);
        }

        var result = new JsonObject();
        Put(result, "id", id);
        return result;
    }

    internal JsonValue Extend(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var nowMs = NowMsOf(args, Clock);
        var ended = PersistExpiry(dir!, team, data, nowMs);
        var id = args.Member("id");
        var l = FindById(data, id);
        var result = new JsonObject();
        if (l is null)
        {
            Put(result, "id", id);
            result["found"] = false;
            return result;
        }

        var state = l.Get("state");
        var capped = IsText(state, "expired") && (IsText(l.Get("end_reason"), "message_cap") || IsText(l.Get("end_reason"), "time"));
        if (IsText(state, "revoked") || (IsText(state, "expired") && !capped))
        {
            Put(result, "id", id);
            result["refused"] = l.Get("end_reason") is { IsTruthy: true } why ? why : Undefined(state);
            return result;
        }

        var limits = Limits();
        var own = l.Get("limits") ?? throw new InvalidOperationException("the link has no limits");
        var ownLimits = own.AsObject();
        var messages = args.Member("messages") is { IsNumber: true } mm && double.IsFinite(mm.AsNumber()) ? mm.AsNumber() : Num(limits.Get("messages"));
        var minutes = args.Member("minutes") is { IsNumber: true } mi && double.IsFinite(mi.AsNumber()) ? mi.AsNumber() : Num(limits.Get("minutes"));
        ownLimits["messages"] = Add(ownLimits.Get("messages"), messages);
        ownLimits["minutes"] = Add(ownLimits.Get("minutes"), minutes);
        l["extensions"] = Add(l.Get("extensions") is { IsTruthy: true } ex ? ex : (JsonValue)0, 1);
        l.Remove("pending_extension");
        if (capped)
        {
            l["state"] = "active";
            l.Remove("ended_at");
            l.Remove("end_reason");
        }

        WriteLinks(ctx,dir, team, data);
        Put(result, "id", l.Get("id"));
        result["extensions"] = l["extensions"];
        result["ended"] = Short(ended);
        return result;
    }

    internal JsonValue Pend(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var id = args.Member("id");
        var l = FindById(data, id);
        var result = new JsonObject();
        if (l is null)
        {
            Put(result, "id", id);
            result["found"] = false;
            return result;
        }

        var request = args.Member("request");
        var pending = l.Get("pending_extension");
        if (request is { IsTruthy: true } && pending is { IsTruthy: true } && !StrictEquals(pending, request))
        {
            Put(result, "id", l.Get("id"));
            result["existing"] = pending.Value;
            return result;
        }

        if (request is { IsTruthy: true } req)
        {
            l["pending_extension"] = req;
        }
        else
        {
            l.Remove("pending_extension");
        }

        WriteLinks(ctx,dir, team, data);
        Put(result, "id", l.Get("id"));
        return result;
    }

    internal JsonValue Sweep(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var ended = PersistExpiry(dir!, team, data, NowMsOf(args, Clock));
        if (ended.Count > 0)
        {
            WriteLinks(ctx,dir, team, data);
        }

        return new JsonObject { ["ended"] = Short(ended), ["count"] = ended.Count };
    }

    internal JsonValue Deny(JsonValue args, IDbOpTransaction ctx)
    {
        var dir = DirOf(args);
        var team = TeamOf(args);
        var data = Load(ctx, dir, team);
        var denied = data["denied"].IsArray ? data["denied"].AsArray() : throw new InvalidOperationException("denied is not an array");
        var why = args.Member("why") is { IsTruthy: true } w ? JsConvert.ToJsString(w) : string.Empty;
        var entry = new JsonObject();
        Put(entry, "request", args.Member("request"));
        entry["why"] = why.Length > 200 ? why[..200] : why;
        entry["at"] = Iso(NowMsOf(args, Clock));
        var all = new List<JsonValue>(denied) { entry };
        var kept = new JsonArray();
        kept.AddRange(all.Skip(Math.Max(0, all.Count - 50)));
        data["denied"] = kept;
        WriteLinks(ctx,dir, team, data);
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>A clock stopped at one instant, with the local offset the underlying clock has there.</summary>
    private sealed class FixedClock(IClock inner, DateTimeOffset instant) : IClock
    {
        public DateTimeOffset UtcNow => instant;

        public TimeSpan LocalOffset(DateTimeOffset at) => inner.LocalOffset(at);
    }
}

internal sealed class LinksStateDomain : IStateDomain
{
    private readonly Links _links;

    internal LinksStateDomain(Links links)
    {
        _links = links;
        Ops =
        [
            Write("grant", _links.Grant),
            Write("revoke", _links.Revoke),
            Write("use", _links.Use),
            Write("release", _links.Release),
            Write("round", _links.Round),
            Write("extend", _links.Extend),
            Write("pend", _links.Pend),
            Write("sweep", _links.Sweep),
            Write("deny", _links.Deny),
            new StateOp("read", OpKind.Read, null, false, null, true, Guard<IStoreWriteContext?>((a, _) => _links.Read(a)), PathArgs: ["dir"]),

            // Reads no store: the one lapse computation, for a caller that holds a link record. Not served by the daemon's routes.
            new StateOp("lapse", OpKind.Read, null, false, null, false, Guard<IStoreWriteContext?>((a, _) => LapseOf(_links.LinkLapseMs(a.Member("link") ?? throw new InvalidOperationException("link is required"))))),
        ];
    }

    public string Name => "links";

    public IReadOnlyList<string> StoreNames { get; } = ["links"];

    public IReadOnlyList<StateOp> Ops { get; }

    /// <summary>The directory that holds the domain lock for a request that names no target of its own.</summary>
    public string StateDirectory(RequestContext context) => NodePath.Join(context.HierarchyDirectory, ".state-links");

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    /// <summary><c>{lapse_ms}</c>, null when the link's grant time cannot be read (the computation then has no number).</summary>
    private static JsonValue LapseOf(double lapse) => new JsonObject { ["lapse_ms"] = double.IsFinite(lapse) ? lapse : JsonValue.Null };

    // per-dir-files-to-db S11b: the links live only in the links table
    public bool DbOnly => true;

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"links.{call.Op} is db-only", "op_failed");

    private static StateOp Write(string name, Func<JsonValue, IDbOpTransaction, JsonValue> body) =>
        new(name, OpKind.Write, OpStyle.Replace, false, ".domain.lock", true, DbOnlyBody, PathArgs: ["dir"]) { DbBody = Guard<IDbOpTransaction>(body) };

    private static Func<OpCall, T, OpResult> Guard<T>(Func<JsonValue, T, JsonValue> body) =>
        (call, ctx) =>
        {
            try
            {
                var args = call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span);
                return new OpResult(true, JsonText.ToUtf8(body(args, ctx), 0));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
            }
        };
}
