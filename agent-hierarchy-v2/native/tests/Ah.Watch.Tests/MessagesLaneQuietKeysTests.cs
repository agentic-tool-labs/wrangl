using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A periodic reconcile reads again only what moved: an answered exchange no write touched is skipped.</summary>
public sealed class MessagesLaneQuietKeysTests
{
    private const string Dir = "/h/.claude/ah2";
    private const string Closed = "20261103-005522-12h1";
    private const string Open = "20261103-005522-99zz";

    private sealed class Reader : IViewStateReader
    {
        public JsonValue Version = 1;
        public string[] Changed = [];
        public bool Fails;
        public List<long> AskedSince { get; } = [];

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            switch (domain + "." + op)
            {
                case "msgs.version":
                    return Fails ? throw new InvalidOperationException("still on files") : new JsonObject { ["version"] = Version };
                case "msgs.changed":
                    AskedSince.Add((long)args["since"].AsNumber());
                    return new JsonObject { ["ids"] = JsonValue.FromArray([.. Changed.Select(JsonValue.FromString)]) };
                case "msgs.read":
                    var id = args["id"].AsString();
                    return new JsonObject { ["id"] = id, ["slug"] = "s", ["to"] = "impl", ["from"] = "o", ["has_response"] = id == Closed };
                default:
                    return JsonValue.Null;
            }
        }
    }

    private static (MessagesLane Lane, Reader Reader) Seeded()
    {
        var reader = new Reader();
        var lane = new MessagesLane(reader, static () => [Dir]);
        _ = lane.Reread(MessagesLane.KeyOf(Dir, Closed), 0);
        _ = lane.Reread(MessagesLane.KeyOf(Dir, Open), 0);
        return (lane, reader);
    }

    [Fact]
    public void NegativeTheFirstPassHasNoMarkSoNothingIsQuiet()
    {
        var (lane, _) = Seeded();

        Assert.Empty(lane.QuietKeys());
    }

    [Fact]
    public void AnAnsweredExchangeNoWriteTouchedIsQuietAndAnOpenOneIsNot()
    {
        var (lane, reader) = Seeded();
        _ = lane.QuietKeys();

        Assert.Equal([MessagesLane.KeyOf(Dir, Closed)], lane.QuietKeys());
        Assert.Empty(reader.AskedSince); // negative: an unmoved counter costs no query per pool
    }

    [Fact]
    public void NegativeAnAnsweredExchangeAWriteTouchedIsNotQuiet()
    {
        var (lane, reader) = Seeded();
        _ = lane.QuietKeys();

        reader.Version = 5;
        reader.Changed = [Closed];

        Assert.Empty(lane.QuietKeys());
        Assert.Equal([1L], reader.AskedSince);
    }

    [Fact]
    public void AWriteToAnotherExchangeLeavesThisOneQuietAndMovesTheMark()
    {
        var (lane, reader) = Seeded();
        _ = lane.QuietKeys();

        reader.Version = 5;
        reader.Changed = [Open];
        Assert.Equal([MessagesLane.KeyOf(Dir, Closed)], lane.QuietKeys());

        reader.Version = 6;
        _ = lane.QuietKeys();
        Assert.Equal([1L, 5L], reader.AskedSince);
    }

    [Fact]
    public void NegativeACounterThatWentBackNamesNone()
    {
        var (lane, reader) = Seeded();
        reader.Version = 9;
        _ = lane.QuietKeys();

        reader.Version = 3;

        Assert.Empty(lane.QuietKeys());
    }

    [Fact]
    public void NegativeAStoreThatCannotAnswerNamesNoneAndTheNextPassStartsOver()
    {
        var (lane, reader) = Seeded();
        _ = lane.QuietKeys();

        reader.Fails = true;
        Assert.Empty(lane.QuietKeys());

        reader.Fails = false;
        Assert.Empty(lane.QuietKeys());
        Assert.Single(lane.QuietKeys());
    }

    [Fact]
    public void NegativeAKeyNeverReadIsNotQuiet()
    {
        var lane = new MessagesLane(new Reader(), static () => [Dir]);
        _ = lane.QuietKeys();

        Assert.Empty(lane.QuietKeys());
    }

    private sealed class ListingLane(IReadOnlyList<string> listed, IReadOnlySet<string> quiet) : IModelLane, IQuietKeys
    {
        public List<string> Reads { get; } = [];

        public string Name => "l";

        public LaneResult Reread(string key, long nowMs)
        {
            Reads.Add(key);
            return new LaneResult([]) { Record = new LaneRecord(JsonValue.FromObject(new JsonObject()), 0) };
        }

        public IReadOnlyList<string> SliceIds(string key) => [];

        public IReadOnlyList<string> Keys() => listed;

        public IReadOnlySet<string> QuietKeys() => quiet;
    }

    [Fact]
    public void AReconcileSkipsAQuietKeyButAKeyGoneFromTruthIsStillRead()
    {
        // "gone" holds a record and is named quiet, but is no longer listed: reading it is what drops its slice.
        var lane = new ListingLane(["quiet", "moved"], new HashSet<string> { "quiet", "gone" });
        var clock = 0L;
        var scheduler = new ModelScheduler(new ModelOptions(), () => clock);
        var bus = new ModelBus(() => clock);
        var projector = new Projector(bus, new SliceStore("e"), new Dictionary<string, IModelLane> { ["l"] = lane }, scheduler, new ModelCounters(), () => clock, composers: new ComposerRegistry());
        _ = bus.Emit(new LaneKey("l", "gone"), NoticeOrigin.Routed, 0);
        while (projector.Step())
        {
            clock += 1000;
        }

        lane.Reads.Clear();
        _ = bus.Emit(new LaneKey("l", LaneKey.KeySet), NoticeOrigin.Reconcile, 0);
        while (projector.Step())
        {
            clock += 1000;
        }

        Assert.Equal(["gone", "moved"], lane.Reads.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NegativeARealKeySetNoticeReadsEveryKey()
    {
        var lane = new ListingLane(["quiet", "moved"], new HashSet<string> { "quiet" });
        var clock = 0L;
        var bus = new ModelBus(() => clock);
        var projector = new Projector(bus, new SliceStore("e"), new Dictionary<string, IModelLane> { ["l"] = lane }, new ModelScheduler(new ModelOptions(), () => clock), new ModelCounters(), () => clock);
        _ = bus.Emit(new LaneKey("l", LaneKey.KeySet), NoticeOrigin.Routed, 0);
        for (var i = 0; i < 20 && lane.Reads.Count < 2; i++)
        {
            _ = projector.Step();
            clock += 1000;
        }

        Assert.Equal(["moved", "quiet"], lane.Reads.Order(StringComparer.Ordinal));
    }
}
