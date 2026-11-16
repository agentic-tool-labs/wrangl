using System.Security.Cryptography;
using System.Text;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>The revision of a view: a hash of its content without <c>at</c>, <c>rev</c> and the clock fields.</summary>
internal static class ViewRev
{
    /// <summary>Fields that are a function of the moment of the build, not of what changed: a client ages the timestamps it came with.</summary>
    internal static readonly IReadOnlySet<string> ClockFields = new HashSet<string>(StringComparer.Ordinal) { "age_sec", "expires_in_sec" };

    /// <summary>
    /// The first 16 hex characters of the SHA-256 of <paramref name="view"/>'s JSON text with the top-level <c>at</c> and every
    /// clock field removed, written by the one canonical writer so the same view hashes the same in both runtimes.
    /// </summary>
    internal static string Of(JsonObject view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var content = new JsonObject();
        foreach (var key in view.Keys)
        {
            if (key != "at" && !ClockFields.Contains(key))
            {
                content[key] = Strip(view[key]);
            }
        }

        var text = JsonText.Stringify(JsonValue.FromObject(content), 0);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>The view with <c>rev</c> appended after its own fields (a view already carrying <c>rev</c> keeps its place).</summary>
    internal static JsonObject Finish(JsonObject view)
    {
        var body = new JsonObject();
        foreach (var key in view.Keys)
        {
            body[key] = view[key];
        }

        body["rev"] = Of(view);
        return body;
    }

    private static JsonValue Strip(JsonValue value)
    {
        switch (value.Kind)
        {
            case JsonKind.Map:
                var map = new JsonObject();
                foreach (var key in value.AsObject().Keys)
                {
                    if (!ClockFields.Contains(key))
                    {
                        map[key] = Strip(value.AsObject()[key]);
                    }
                }

                return JsonValue.FromObject(map);
            case JsonKind.Array:
                var array = new JsonArray();
                foreach (var item in value.AsArray())
                {
                    array.Add(Strip(item));
                }

                return JsonValue.FromArray(array);
            default:
                return value;
        }
    }
}

/// <summary>A watched status document with its revision.</summary>
/// <param name="Rev">The revision: a counter seeded from the newest <c>status_entries</c> write in microseconds, only ever rising, and moved only when the document's content (without its clock stamps) changes.</param>
/// <param name="Doc">The document.</param>
public sealed record StatusReading(long Rev, JsonValue Doc);

/// <summary>Reads watched status documents with their revisions.</summary>
public interface IStatusRevs
{
    /// <summary>The revision and status document of hierarchy dir <paramref name="dir"/>, or null when it cannot be read (the dir is then forgotten).</summary>
    StatusReading? Read(string dir);

    /// <summary>Forgets a dir's revision state.</summary>
    void Forget(string dir);
}

/// <summary>
/// The revisions of watched status documents (files-off-disk F3a): the signature is the dir's <c>status.stamp</c>
/// (<c>maxChangedMs:rowCount</c> of its <c>status_entries</c>), the rev is that max in microseconds and only rises
/// (<c>max(prev + 1, max)</c>), and the document is <c>status.read</c>, re-read when the stamp moves. When the stamp has not moved and
/// the TTL is over, the document is computed again only if something its compute observed has moved (b459 C1, ADR adr-22): otherwise
/// the held document is served with its clock stamps rewritten.
/// </summary>
internal sealed class StatusRevs(IViewStateReader state, Func<long>? nowMs = null, Func<JsonValue, Ah.Hierarchy.StatusWitness?>? witnessOf = null) : IStatusRevs
{
    /// <summary>How long a document whose stamp has not moved is served before it is checked again: liveness and expiry come from the
    /// clock and the process table, which the stamp does not see. J1 (b459): was 2000; a tap may show liveness up to 5 s old.</summary>
    internal const long TtlMs = 5000;

    /// <summary>
    /// The longest a document is served after it was computed without being computed again (b459 C1 guard G4, Ultra-Advisor ruling
    /// 20261113-234021-79wb): a restamp is refused once the TTL it would be served for ends past this, on the compute's wall clock or
    /// on this cache's monotonic clock, whichever is further along. So a rule or input the witness missed shows at most this stale,
    /// whatever the wall clock does. A decision listed for Jim.
    /// </summary>
    internal const long RestampCapMs = 60_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byDir = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Dir, string What), long> _counts = [];
    private readonly Func<long> _now = nowMs ?? (static () => Environment.TickCount64);

    /// <summary>A dir's held document: <c>Canonical</c> is its text without clock stamps, which the rev compares; <c>Witness</c> is its compute's,
    /// null when it left none; <c>ComputedTick</c> is this cache's monotonic clock when the document was computed (a restamp keeps it).</summary>
    private sealed record Entry(string Signature, long Rev, JsonValue Doc, long ReadAtMs, string Canonical, Ah.Hierarchy.StatusWitness? Witness, long ComputedTick);

    private int _counted;

    private static readonly string[] RefusalReasons =
        ["no-witness", "entries-not-current", "replay-differed", "replay-threw", "past-horizon", "past-cap", "clock-stepped-back"];

    /// <summary>What happened to a dir's reads that did not serve the held document as it was, by name: <c>restamp</c>, <c>recompute:first</c>,
    /// <c>recompute:stamp</c>, <c>recompute:ttl</c>, and <c>refused:&lt;reason&gt;</c> (no-witness, entries-not-current, replay-differed, replay-threw, past-horizon,
    /// past-cap, clock-stepped-back). For the probe and the tests; never in a document.</summary>
    internal IReadOnlyDictionary<string, long> Counters(string dir)
    {
        lock (_gate)
        {
            return _counts.Where(c => c.Key.Dir == dir).ToDictionary(static c => c.Key.What, static c => c.Value, StringComparer.Ordinal);
        }
    }

    private void Count(string dir, string what)
    {
        lock (_gate)
        {
            _counts[(dir, what)] = _counts.GetValueOrDefault((dir, what)) + 1;
        }
    }

    private long Total(Func<string, bool> what)
    {
        lock (_gate)
        {
            return _counts.Where(c => what(c.Key.What)).Sum(static c => c.Value);
        }
    }

    /// <summary>The revision and status document of hierarchy dir <paramref name="dir"/>, or null when it cannot be read (the dir is then forgotten).</summary>
    public StatusReading? Read(string dir)
    {
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            Ah.Core.CoreModule.CensusAdd(this, "watch.status_docs", static r => { lock (r._gate) { return r._byDir.Count; } });
            Ah.Core.CoreModule.CensusAdd(this, "watch.status_restamps", static r => r.Total(static w => w == "restamp"));
            Ah.Core.CoreModule.CensusAdd(this, "watch.status_restamps_refused", static r => r.Total(static w => w.StartsWith("refused:", StringComparison.Ordinal)));
            Ah.Core.CoreModule.CensusAdd(this, "watch.status_recomputes_ttl", static r => r.Total(static w => w == "recompute:ttl"));
            Ah.Core.CoreModule.CensusAdd(this, "watch.status_recomputes_stamp", static r => r.Total(static w => w == "recompute:stamp"));
            // why a restamp was refused, by reason: the one number that says whether the cheap path is being taken under load
            foreach (var reason in RefusalReasons)
            {
                var what = "refused:" + reason;
                Ah.Core.CoreModule.CensusAdd(this, "watch.status_refused." + reason, r => r.Total(w => w == what));
            }
        }

        string signature;
        try
        {
            signature = state.Read("status", "stamp", dir, new JsonObject { ["dir"] = dir }).Member("stamp") is { IsText: true } s ? s.AsString() : string.Empty;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Forget(dir);
            return null;
        }

        Entry? previous;
        lock (_gate)
        {
            _ = _byDir.TryGetValue(dir, out previous);
        }

        var now = _now();
        var sameStamp = previous is not null && previous.Signature == signature;
        if (sameStamp && now - previous!.ReadAtMs < TtlMs)
        {
            return new StatusReading(previous.Rev, previous.Doc);
        }

        if (sameStamp)
        {
            // the TTL is over and the entries have not moved: only the clock, the process table, the config or the disk can have
            // changed the document, and the witness says whether any did
            if (Restamped(previous!, now, out var restamped) is { } refused)
            {
                Count(dir, "refused:" + refused);
            }
            else
            {
                Count(dir, "restamp");
                lock (_gate)
                {
                    // the witness and its compute times stay: the cap counts from the real compute. Written only over the entry that
                    // was replayed: a Forget or a newer compute in between wins
                    if (_byDir.TryGetValue(dir, out var current))
                    {
                        if (!ReferenceEquals(current, previous))
                        {
                            // a newer compute landed while this replay ran: answer with it, so no reader sees the rev go backwards
                            return new StatusReading(current.Rev, current.Doc);
                        }

                        _byDir[dir] = previous! with { Doc = restamped, ReadAtMs = now };
                    }
                }

                return new StatusReading(previous!.Rev, restamped);
            }
        }

        Count(dir, sameStamp ? "recompute:ttl" : previous is null ? "recompute:first" : "recompute:stamp");
        JsonValue doc;
        try
        {
            doc = state.Read("status", "read", dir, new JsonObject { ["dir"] = dir });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            doc = JsonValue.Null;
        }

        if (!doc.IsMap)
        {
            Forget(dir);
            return null;
        }

        var micros = long.TryParse(signature.Split(':')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms * 1000 : 0;
        // a re-read whose content moved only by the clock stamps keeps the rev, so a waiting client is not woken for nothing: on the TTL, or
        // on a stamp move that rewrote nothing the document shows (b459 step 23)
        var canonical = JsonText.Stringify(WithoutClockStamps(doc), 0);
        var rev = previous is null ? micros
            : canonical == previous.Canonical ? previous.Rev
            : Math.Max(previous.Rev + 1, micros);
        lock (_gate)
        {
            _byDir[dir] = new Entry(signature, rev, doc, now, canonical, witnessOf?.Invoke(doc), now);
        }

        return new StatusReading(rev, doc);
    }

    /// <summary>
    /// The held document with its clock stamps rewritten for now, in <paramref name="document"/>, when its witness allows it (b459 C1):
    /// the compute's clock is at or after the compute and before the horizon, the TTL the restamp would be served for ends inside the
    /// cap on both clocks (<paramref name="tick"/> is this cache's monotonic now), and a replay of everything the compute observed gives
    /// the same answers. Returns null then, else the reason it is refused. The held document is never changed.
    /// </summary>
    private static string? Restamped(Entry held, long tick, out JsonValue document)
    {
        document = held.Doc;
        if (held.Witness is not { } witness)
        {
            return "no-witness";
        }

        try
        {
            var now = witness.NowMs();
            if (now < witness.ComputedAtMs)
            {
                return "clock-stepped-back";
            }

            // a restamp is served for a TTL more, unchecked: that whole window must end inside the cap. The monotonic age holds the
            // bound when the wall clock stepped back by less than the document's age
            if (now + TtlMs > witness.ComputedAtMs + RestampCapMs || tick - held.ComputedTick + TtlMs > RestampCapMs)
            {
                return "past-cap";
            }

            if (now >= witness.HorizonMs)
            {
                return "past-horizon";
            }

            if (witness.Replay() is { } why)
            {
                return why;
            }

            document = Ah.Hierarchy.StatusWitness.Restamp(held.Doc, now);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "replay-threw";
        }
    }

    /// <summary>
    /// A copy of status document <paramref name="document"/> without the fields stamped with the instant it was computed: the top-level
    /// <c>written_at</c> and <c>expires_at</c>, the <c>at</c> of the first entry of the doc's and each owner's <c>timeline</c>, and the
    /// <c>at</c> of each dispatch's first state unless it is <c>working</c> (reported, stalled and blocked are stamped now). The rules of
    /// the old status file's <c>WithoutNowStamps</c>; the field set is stated once, in <see cref="Ah.Hierarchy.HierarchyModule.StatusClockStamps"/>.
    /// </summary>
    internal static JsonValue WithoutClockStamps(JsonValue document) => Ah.Hierarchy.HierarchyModule.StatusClockStamps(document, null, null);

    public void Forget(string dir)
    {
        lock (_gate)
        {
            _ = _byDir.Remove(dir);
            foreach (var key in _counts.Keys.Where(k => k.Dir == dir).ToList())
            {
                _ = _counts.Remove(key);
            }
        }
    }
}
