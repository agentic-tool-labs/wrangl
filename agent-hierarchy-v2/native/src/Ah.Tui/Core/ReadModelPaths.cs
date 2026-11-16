using Ah.Core.Json;

namespace Ah.Tui.Core;

/// <summary>
/// The read model as the TUI reads it, the same slices the web reads: <see cref="Summary"/> is the summary manifest (the team list and
/// one summary per team), a plain read each round. One held poll of the slice feed subscribes to the slice ids wanted (<see cref="SlicePoll"/>)
/// and keeps what it replies in one store, by id (<see cref="Merge"/>). <c>All</c> gathers every slice the replies hold, by id.
/// </summary>
internal static class ReadModelPaths
{
    internal const string Summary = "@view/summary";

    /// <summary>
    /// The most of the slice ids one poll names, encoded. The ids can go into the query twice and the daemon's socket listener takes a
    /// request line of 8 KB. Past it the ids at the end are not subscribed.
    /// </summary>
    internal const int MaxIdBytes = 3000;

    /// <summary>These ids, without repeats and in the order given, up to <see cref="MaxIdBytes"/>.</summary>
    internal static List<string> Within(IEnumerable<string> ids)
    {
        var kept = new List<string>();
        var bytes = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            bytes += Adapters.DaemonViewSource.FormEncode(id).Length + 3;
            if (bytes > MaxIdBytes)
            {
                break;
            }

            kept.Add(id);
        }

        return kept;
    }

    /// <summary>
    /// The store after a poll's reply: the reply's slices replace those with their ids, a <c>removed</c> id goes, an id the poll asked for
    /// <paramref name="fresh"/> that the reply leaves out is gone, and an id not in <paramref name="subscribed"/> any more is dropped, so a
    /// slice the daemon stops serving, or the screen stops wanting, is not shown from an old reply.
    /// </summary>
    internal static JsonValue Merge(JsonValue? store, JsonValue reply, IEnumerable<string> fresh, IEnumerable<string> subscribed)
    {
        var keep = subscribed.ToHashSet(StringComparer.Ordinal);
        var gone = fresh.ToHashSet(StringComparer.Ordinal);
        if (reply.Member("removed") is { IsArray: true } removed)
        {
            gone.UnionWith(removed.AsArray().Where(static r => r.IsText).Select(static r => r.AsString()));
        }

        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var s in Entries(store).Where(e => !gone.Contains(e.Id)).Concat(Entries(reply)))
        {
            if (keep.Contains(s.Id))
            {
                byId[s.Id] = s.Entry;
            }
        }

        var list = new JsonArray();
        foreach (var entry in byId.Values)
        {
            list.Add(entry);
        }

        return new JsonObject { ["slices"] = list };
    }

    private static IEnumerable<(string Id, JsonValue Entry)> Entries(JsonValue? v)
    {
        if (v is not { IsMap: true } m || m.Member("slices") is not { IsArray: true } list)
        {
            yield break;
        }

        foreach (var s in list.AsArray())
        {
            if (s.IsMap && s.Member("id")?.NonEmptyText is { } id)
            {
                yield return (id, s);
            }
        }
    }

    /// <summary>Every slice body these replies hold, by slice id; where two hold one id, the newer <c>as_of</c> wins.</summary>
    internal static Dictionary<string, JsonValue> All(IEnumerable<JsonValue?> replies)
    {
        var slices = new Dictionary<string, (double At, JsonValue Body)>(StringComparer.Ordinal);
        foreach (var reply in replies)
        {
            if (reply?.Member("slices") is not { IsArray: true } list)
            {
                continue;
            }

            foreach (var s in list.AsArray())
            {
                // A queue slice's body is its array of rows. An item log's null body is an answer, no such item, so it is held and not asked for again.
                if (s.Member("id")?.NonEmptyText is not { } id || s.Member("body") is not { } body
                    || !(body.IsMap || body.IsArray || (body.IsNull && id.StartsWith(TeamSlices.ItemLogHead, StringComparison.Ordinal))))
                {
                    continue;
                }

                var at = s.Member("as_of") is { IsNumber: true } n ? n.AsNumber() : 0;
                if (!slices.TryGetValue(id, out var have) || at >= have.At)
                {
                    slices[id] = (at, body);
                }
            }
        }

        return slices.ToDictionary(static kv => kv.Key, static kv => kv.Value.Body, StringComparer.Ordinal);
    }
}
