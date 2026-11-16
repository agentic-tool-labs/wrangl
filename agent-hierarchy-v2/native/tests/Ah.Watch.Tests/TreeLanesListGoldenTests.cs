using System.Globalization;
using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The <c>/list</c> envelope as the pagers serve it: one sample page per list kind, and each kind's prefix and filters.</summary>
public sealed partial class TreeLanesTests
{
    private sealed class FixedTranscript(TranscriptTail tail, string path) : ITranscriptSource
    {
        public (IReadOnlyList<TranscriptEntry> Ring, string? Path) Held(string dirId, string subject) => (tail.Entries, path);

        public string? ColdPath(string dirId, string subject) => null;
    }

    private static async Task<JsonValue> SampleAsync(IListPager pager, string sliceId, int limit)
    {
        var request = new ListRequest(sliceId, null, null, limit, new Dictionary<string, string>());
        return Assert.IsType<ListResult.Found>(await pager.PageAsync(request, CancellationToken.None)).Page.ToJson();
    }

    private static JsonObject KindOf(IListPager[] pagers, JsonValue sample)
    {
        var prefixes = new JsonArray();
        foreach (var pager in pagers)
        {
            prefixes.Add(pager.Prefix);
        }

        var filters = new JsonArray();
        foreach (var filter in pagers[0].Filters)
        {
            filters.Add(filter);
        }

        return new JsonObject { ["prefixes"] = prefixes, ["filters"] = filters, ["sample"] = sample };
    }

    [Fact]
    public async Task TheListEnvelopeAndEachKindsPrefixFiltersAndSamplePageMatchTheGolden()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("tester", "developer", "qa", "dev", "default");
        reader.Exchanges["c"] = Msg("tester", "orchestrator", "qa", null, "default");
        var (model, _) = Seeded(reader);
        using var owner = model;
        Func<ModelState> state = () => model.Store.Current;
        var team = "/" + "default";
        var exchanges = new ExchangeListPager(TeamExchangeListComposer.ExchangesPrefix + ":", state, (_, _) => null);
        var feed = new ExchangeListPager(TeamExchangeListComposer.FeedPrefix + ":", state, (_, _) => null);
        var dirExchanges = new ExchangeListPager(ExchangeListComposer.ExchangesPrefix + ":", state, (_, _) => null, teamScoped: false);
        var dirFeed = new ExchangeListPager(ExchangeListComposer.FeedPrefix + ":", state, (_, _) => null, teamScoped: false);
        var open = new OpenListPager(state, (_, _) => null);

        var changes = string.Join(
            ',',
            Enumerable.Range(1, 3).Reverse().Select(n =>
                "{\"at\":\"2026-06-02T10:00:0" + n.ToString(CultureInfo.InvariantCulture) + "Z\",\"seq\":" + n.ToString(CultureInfo.InvariantCulture) + ",\"stream\":\"tui\",\"item\":\"i" + (n % 2).ToString(CultureInfo.InvariantCulture) + "\",\"kind\":\"state\",\"from\":\"next\",\"to\":\"in_progress\",\"by\":\"implementor\"}"));
        var planState = new ModelState(
            1,
            System.Collections.Immutable.ImmutableDictionary<string, Slice>.Empty.WithComparers(StringComparer.Ordinal).Add(
                "list:plan-changes:" + D + team,
                new Slice("list:plan-changes:" + D + team, 1, 0, Encoding.UTF8.GetBytes("{\"rows\":[" + changes + "],\"total\":3,\"tail_cursor\":null}"), "h")));
        var held = JsonText.Parse(Encoding.UTF8.GetBytes("[" + changes + "]")).AsArray();
        var plan = new PlanChangesPager(() => planState, suffix => suffix == D + team ? held : null);

        using var timelineStore = TimelineDb.Open(TimelineDb.NewPath());
        timelineStore.Append(
        [
            new TimelineRow(0, D, "alice", null, "busy", 1_799_999_000_000),
            new TimelineRow(0, D, "bob", null, "busy", 1_799_999_100_000),
            new TimelineRow(0, D, "alice", "busy", "idle", 1_799_999_200_000),
        ]);
        var timeline = new TimelinePager(timelineStore, static () => 7, static () => 1_800_000_000_000, static () => 3);
        var spans = new TimelineSpansPager(timelineStore, static () => 7, static () => 1_800_000_000_000, state, (_, _) => null);

        var files = new TranscriptFiles();
        const string TranscriptPath = "/t/s.jsonl";
        files.Put(TranscriptPath, []);
        files.Append(
            TranscriptPath,
            "{\"type\":\"user\",\"message\":{\"content\":\"hello\"}}\n"
            + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}}\n"
            + "{\"type\":\"user\",\"message\":{\"content\":\"thanks\"}}\n");
        var tail = new TranscriptTail(files, new TestUsage());
        _ = tail.Advance(TranscriptPath);
        var transcript = new TranscriptPager(new FixedTranscript(tail, TranscriptPath), files, new TestUsage(), static () => 3);

        var firstPage = await SampleAsync(exchanges, ExchangesListId, 2);
        var kinds = new JsonObject
        {
            ["exchanges"] = KindOf([exchanges, dirExchanges], firstPage),
            ["feed"] = KindOf([feed, dirFeed], await SampleAsync(feed, "list:feed:team:" + D + team, 2)),
            ["open"] = KindOf([open], await SampleAsync(open, "list:open:team:" + D + team, 2)),
            ["timeline"] = KindOf([timeline], await SampleAsync(timeline, "list:timeline:" + D, 2)),
            ["timeline-spans"] = KindOf([spans], await SampleAsync(spans, "list:timeline-spans:" + D + team, 2)),
            ["transcript"] = KindOf([transcript], await SampleAsync(transcript, "list:transcript:" + D + "/alice", 2)),
            ["plan-changes"] = KindOf([plan], await SampleAsync(plan, "list:plan-changes:" + D + team, 2)),
        };
        var envelopeKeys = new JsonArray();
        foreach (var key in firstPage.AsObject().Keys)
        {
            envelopeKeys.Add(key);
        }

        var shared = new JsonArray { "after", "before", "limit" };
        var document = new JsonObject
        {
            ["envelope_keys"] = envelopeKeys,
            ["shared_keys"] = shared,
            ["limit"] = new JsonObject { ["default"] = ListRequest.DefaultLimit, ["max"] = ListRequest.MaxLimit },
            ["kinds"] = kinds,
        };

        ModelGolden.Check(document, "list-envelope");
    }
}
