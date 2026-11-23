using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The earliest instant at which a status document's content changes with the clock alone (b459 C1, guard G3). The compute tells it every
/// later instant a clock comparison flips at; an instant at or before the compute's own <c>now</c> means "no flip instant can be named",
/// and such a document is never restamped.
/// </summary>
internal sealed class StatusHorizon
{
    /// <summary>The earliest instant told so far, in epoch milliseconds; infinite when the clock alone never changes the document.</summary>
    public double Ms { get; private set; } = double.PositiveInfinity;

    /// <summary>Notes that the document changes at <paramref name="ms"/>. A value that is not a number counts as unknown.</summary>
    public void At(double ms)
    {
        if (double.IsNaN(ms))
        {
            Ms = double.NegativeInfinity;
        }
        else if (ms < Ms)
        {
            Ms = ms;
        }
    }
}

/// <summary>
/// What one status compute observed outside the status entries (b459 C1, ADR adr-22): its reads of the process table, liveness, the
/// config and the disk, with what each returned, and the horizon of its clock rules. While a replay of those reads gives the same
/// answers, the entries have not moved and the clock is inside the horizon, the document differs from a fresh compute only in its
/// clock stamps. It never leaves the process: not in the document, a route or the contract.
/// </summary>
public sealed class StatusWitness
{
    private readonly Func<double> _now;
    private readonly Func<string?> _replay;

    /// <summary>A witness of a compute at <paramref name="computedAtMs"/>: <paramref name="now"/> is the compute's clock, <paramref name="replay"/> is <see cref="Replay"/>.</summary>
    public StatusWitness(double computedAtMs, double horizonMs, Func<double> now, Func<string?> replay)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(replay);
        ComputedAtMs = computedAtMs;
        HorizonMs = horizonMs;
        _now = now;
        _replay = replay;
    }

    /// <summary>The compute's <c>now</c>, in epoch milliseconds on its own wall clock.</summary>
    public double ComputedAtMs { get; }

    /// <summary>The earliest instant a clock rule changes the document; at or before <see cref="ComputedAtMs"/> when none can be named.</summary>
    public double HorizonMs { get; }

    /// <summary>The compute's wall clock now, in epoch milliseconds.</summary>
    public double NowMs() => _now();

    /// <summary>
    /// Makes every recorded read again, with its side effects, and compares each whole answer. Null when the entries are the ones the
    /// compute read and every answer is the same; else why not: <c>entries-not-current</c>, <c>replay-differed</c> or <c>replay-threw</c>.
    /// </summary>
    public string? Replay() => _replay();

    /// <summary>A new document: <paramref name="held"/> with only its clock stamps rewritten for <paramref name="nowMs"/>. <paramref name="held"/> is not changed.</summary>
    public static JsonValue Restamp(JsonValue held, double nowMs) => HierLib.RestampedStatus(held, nowMs);
}

internal sealed partial class HierLib
{
    /// <summary>One read the compute made outside the entries: how to make it again, and what it returned.</summary>
    private sealed record Observation(Func<IStatusSource, object?> Call, object? Result);

    /// <summary>
    /// Records every call of a status compute that is not a pure read of the status entries (guard G1). It wraps an
    /// <see cref="EntryStatusSource"/> only: there the members passed straight through read the entries alone (and, for
    /// <see cref="Backlog"/>, the <c>now</c> they are given, whose flips feed the horizon). A member added to
    /// <see cref="IStatusSource"/> that reads anything else must be recorded here.
    /// </summary>
    private sealed class RecordingStatusSource(EntryStatusSource inner) : IStatusSource
    {
        internal List<Observation> Observed { get; } = [];

        private T Note<T>(Func<IStatusSource, T> call)
        {
            var result = call(inner);
            Observed.Add(new Observation(s => call(s), result));
            return result;
        }

        // recorded: the process table, liveness and its clock, the config, the disk
        public bool Orphaned(JsonValue team) => Note(s => s.Orphaned(team));

        public JsonArray Roster(string dir) => Note(s => s.Roster(dir));

        public MemberRegistrationLiveness? Registration(string name, string team) => Note(s => s.Registration(name, team));

        public (bool Live, string? SessionId) MemberLife(JsonArray roster, JsonValue name, string? team) => Note(s => s.MemberLife(roster, name, team));

        public double? ResponseMtime(string path) => Note(s => s.ResponseMtime(path));

        public string? ResponseStatus(string path) => Note(s => s.ResponseStatus(path));

        public bool Enabled(string? cwd) => Note(s => s.Enabled(cwd));

        public bool HasDir(string dir) => Note(s => s.HasDir(dir));

        public string? Branch(string workTree) => Note(s => s.Branch(workTree));

        public string? Checkout(string cwd) => Note(s => s.Checkout(cwd));

        // from its entry, else the decision log of no run: read where the entry is missing, so it is recorded
        public JsonObject Decisions(string dir, string anchorId) => Note(s => s.Decisions(dir, anchorId));

        // ah2-509: the resources table and the process table, so a claim, a release or a holder's death re-derives the document
        public JsonArray Resources() => Note(static s => s.Resources());

        // ah2-512: the spend ledger and its caps, so a count, a cap change or a park re-derives the document
        public JsonArray Spend(string dir, string? team) => Note(s => s.Spend(dir, team));

        // not recorded: the entries alone
        public IReadOnlyList<string> TeamNames(string dir) => inner.TeamNames(dir);

        public JsonValue TeamRecord(string dir, string? team) => inner.TeamRecord(dir, team);

        public IReadOnlyList<JsonValue> SessionRows(string dir) => inner.SessionRows(dir);

        public JsonArray Exchanges(string dir) => inner.Exchanges(dir);

        public JsonArray Gates(string dir) => inner.Gates(dir);

        public JsonValue RequestFm(JsonValue exchange) => inner.RequestFm(exchange);

        public (string? Title, string? Source) Title(JsonValue exchange) => inner.Title(exchange);

        public Dictionary<string, JsonValue> Origins(JsonArray requests) => inner.Origins(requests);

        public JsonValue Activity(string dir, string file) => inner.Activity(dir, file);

        public JsonArray Subagents(string sessionId) => inner.Subagents(sessionId);

        public JsonArray RunExchanges(string dir, string? team, long sinceMs) => inner.RunExchanges(dir, team, sinceMs);

        public IReadOnlyList<JsonValue> TeamTags(IReadOnlyList<JsonValue> exchanges) => inner.TeamTags(exchanges);

        public BacklogHealth? Backlog(string dir, double nowMs, Action<double>? flip = null) => inner.Backlog(dir, nowMs, flip);
    }

    /// <summary>Whether two recorded answers are the same whole value: JSON by structure, everything else by its own equality.</summary>
    private static bool SameAnswer(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (JsonArray x, JsonArray y) => ReferenceEquals(x, y) || SameJson(x, y),
        (JsonObject x, JsonObject y) => ReferenceEquals(x, y) || SameJson(x, y),
        (JsonValue x, JsonValue y) => SameJson(x, y),
        _ => a.Equals(b),
    };

    private static bool SameJson(JsonValue a, JsonValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        switch (a.Kind)
        {
            case JsonKind.Map:
                var (x, y) = (a.AsObject(), b.AsObject());
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x.Count != y.Count)
                {
                    return false;
                }

                foreach (var key in x.Keys)
                {
                    if (!y.TryGetValue(key, out var other) || !SameJson(x[key], other))
                    {
                        return false;
                    }
                }

                return true;
            case JsonKind.Array:
                var (p, q) = (a.AsArray(), b.AsArray());
                if (ReferenceEquals(p, q))
                {
                    return true;
                }

                if (p.Count != q.Count)
                {
                    return false;
                }

                for (var i = 0; i < p.Count; i++)
                {
                    if (!SameJson(p[i], q[i]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return JsonText.Stringify(a, 0) == JsonText.Stringify(b, 0);
        }
    }

    /// <summary>
    /// <see cref="ReadStatus"/> for <c>status.read</c>, with the compute's witness in <paramref name="witness"/> (b459 C1). The witness is
    /// handed out with the one document it was recorded for and kept nowhere else, so a document can never be paired with another
    /// compute's. A compute on the stores, or on entries that are not held under a stamp, has none (guard G5).
    /// <paramref name="at"/> is the compute's <c>now</c>; when null it is read once the dir's compute lock is held, so two computes of
    /// one dir never share an instant they did not both observe under.
    /// </summary>
    internal JsonObject ReadStatusWitnessed(string dir, double? at, out StatusWitness? witness)
    {
        witness = null;
        string? stamp = null;
        if (string.IsNullOrEmpty(dir) || HeldStatusSource(dir, out stamp) is not { } source)
        {
            return ComputeStatus(dir, at ?? NowMs, dir);
        }

        // the held source fills its derived caches on first use; one compute at a time per dir
        lock (source)
        {
            var now = at ?? NowMs;
            if (stamp is null)
            {
                return ComputeStatusFrom(source, dir, now, dir);
            }

            var recorder = new RecordingStatusSource(source);
            var horizon = new StatusHorizon();
            var document = ComputeStatusFrom(recorder, dir, now, dir, null, horizon);
            var observed = recorder.Observed;
            // weak: a reader may keep the witness longer than the source is held
            var held = new WeakReference<EntryStatusSource>(source);
            witness = new StatusWitness(now, horizon.Ms, () => NowMs, () => ReplayStatus(dir, held, stamp, observed));
            return document;
        }
    }

    private string? ReplayStatus(string dir, WeakReference<EntryStatusSource> held, string stamp, List<Observation> observed)
    {
        try
        {
            // guard G5: the check ReadStatus makes. The same held source under the same stamp is the same entries.
            if (!held.TryGetTarget(out var source) || !ReferenceEquals(HeldStatusSource(dir, out var current), source) || current != stamp)
            {
                return "entries-not-current";
            }

            lock (source)
            {
                foreach (var o in observed)
                {
                    if (!SameAnswer(o.Call(source), o.Result))
                    {
                        return "replay-differed";
                    }
                }
            }

            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "replay-threw";
        }
    }

    /// <summary><paramref name="held"/> with its clock stamps written for <paramref name="nowMs"/> by the compute's own writer and expiry; a new document.</summary>
    internal static JsonValue RestampedStatus(JsonValue held, double nowMs) =>
        HierarchyModule.StatusClockStamps(held, IsoNowOf(nowMs), IsoNowOf(nowMs + ExpiresAfterMs));
}

public static partial class HierarchyModule
{
    /// <summary>
    /// A copy of status document <paramref name="document"/> with the fields stamped with the instant it was computed set to
    /// <paramref name="at"/>, or removed when it is null: the top-level <c>written_at</c> and <c>expires_at</c> (the latter set to
    /// <paramref name="expiresAt"/>), the <c>at</c> of the first entry of the doc's and each owner's <c>timeline</c>, and the <c>at</c> of
    /// each dispatch's first state unless it is <c>working</c> (reported, stalled and blocked are stamped now). The one statement of
    /// which fields those are. <paramref name="document"/> is never changed; the copy shares every part that has no stamp.
    /// </summary>
    public static JsonValue StatusClockStamps(JsonValue document, string? at, string? expiresAt)
    {
        if (!document.IsMap)
        {
            return document;
        }

        static void Put(JsonObject o, string key, string? value)
        {
            if (value is null)
            {
                _ = o.Remove(key);
            }
            else if (o.ContainsKey(key))
            {
                o[key] = value;
            }
        }

        JsonValue First(JsonValue timeline)
        {
            if (timeline is not { IsArray: true } || timeline.AsArray().Count == 0 || timeline.AsArray()[0] is not { IsMap: true } first)
            {
                return timeline;
            }

            var stamped = first.AsObject().Clone();
            Put(stamped, "at", at);
            var copy = new JsonArray { stamped };
            copy.AddRange(timeline.AsArray().Skip(1));
            return copy;
        }

        var o = document.AsObject().Clone();
        Put(o, "written_at", at);
        Put(o, "expires_at", expiresAt);
        if (o.Get("timeline") is { } own)
        {
            o["timeline"] = First(own);
        }

        if (o.Get("owners") is { IsArray: true } owners)
        {
            var copy = new JsonArray();
            foreach (var owner in owners.AsArray())
            {
                if (owner.IsMap && owner.Member("timeline") is { } timeline)
                {
                    var stamped = owner.AsObject().Clone();
                    stamped["timeline"] = First(timeline);
                    copy.Add(stamped);
                }
                else
                {
                    copy.Add(owner);
                }
            }

            o["owners"] = copy;
        }

        if (o.Get("teams") is { IsArray: true } teams)
        {
            var copy = new JsonArray();
            foreach (var team in teams.AsArray())
            {
                if (!team.IsMap || team.Member("dispatches") is not { IsArray: true } dispatches)
                {
                    copy.Add(team);
                    continue;
                }

                var items = new JsonArray();
                foreach (var dispatch in dispatches.AsArray())
                {
                    if (dispatch.IsMap && dispatch.Member("states") is { IsArray: true } states && states.AsArray().Count > 0 && states.AsArray()[0] is { IsMap: true } state
                        && !(state.Member("state") is { IsText: true } name && name.AsString() == "working"))
                    {
                        var first = state.AsObject().Clone();
                        Put(first, "at", at);
                        var restated = new JsonArray { first };
                        restated.AddRange(states.AsArray().Skip(1));
                        var stamped = dispatch.AsObject().Clone();
                        stamped["states"] = restated;
                        items.Add(stamped);
                    }
                    else
                    {
                        items.Add(dispatch);
                    }
                }

                var withItems = team.AsObject().Clone();
                withItems["dispatches"] = items;
                copy.Add(withItems);
            }

            o["teams"] = copy;
        }

        return o;
    }
}
