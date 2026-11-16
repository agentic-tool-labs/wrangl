using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

public sealed class HeldPeerRowsTests
{
    private sealed class Log : IPeerRecordSource
    {
        public List<PeerRowAt> Rows_ { get; } = [];

        public long Seq { get; set; }

        public long Written { get; set; }

        public List<long> Reads { get; } = [];

        public void Add()
        {
            Rows_.Add(new PeerRowAt(++Seq, JsonValue.FromString("r" + Seq.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            Written++;
        }

        public IReadOnlyList<JsonValue> ReadAll() => throw new NotSupportedException();

        public IReadOnlyList<JsonValue> Rows(PeerQuery query) => throw new NotSupportedException();

        public IReadOnlyList<JsonValue> LatestByKeyFor(JsonValue? sessionId) => throw new NotSupportedException();

        public IReadOnlyList<PeerRowAt> Since(long cursor)
        {
            Reads.Add(cursor);
            return [.. Rows_.Where(r => r.Seq > cursor)];
        }

        public long Version() => Written;

        public long Count() => Rows_.Count;
    }

    [Fact]
    public void AnUnchangedLogIsNotReadAgain()
    {
        var log = new Log();
        log.Add();
        var held = new HeldPeerRows();

        var first = held.Read(log);

        Assert.Same(first, held.Read(log));
        Assert.Equal([0], log.Reads);
    }

    [Fact]
    public void AnAppendReadsOnlyTheNewRows()
    {
        var log = new Log();
        log.Add();
        var held = new HeldPeerRows();
        _ = held.Read(log);
        log.Add();

        Assert.Equal(2, held.Read(log).Count);
        Assert.Equal([0, 1], log.Reads);
    }

    [Fact]
    public void AnEmptyLogThatGrowsReadsFromTheStart()
    {
        var log = new Log();
        var held = new HeldPeerRows();
        Assert.Empty(held.Read(log));
        log.Add();

        Assert.Single(held.Read(log));
    }

    [Fact]
    public void ACompactionThatLeavesFewerRowsReadsEverythingAgain()
    {
        var log = new Log();
        log.Add();
        log.Add();
        var held = new HeldPeerRows();
        _ = held.Read(log);
        log.Rows_.RemoveAt(0);
        log.Written++;

        Assert.Single(held.Read(log));
        Assert.Equal([0, 2, 0], log.Reads);
    }

    [Fact]
    public void ASourceThatCannotCountIsReadInFull()
    {
        var log = new NoCount();
        var held = new HeldPeerRows();
        _ = held.Read(log);
        log.Version_ = 1;
        _ = held.Read(log);

        Assert.Equal(3, log.Reads);
    }

    private sealed class NoCount : IPeerRecordSource
    {
        public long Version_ { get; set; }

        public int Reads { get; private set; }

        public IReadOnlyList<JsonValue> ReadAll() => throw new NotSupportedException();

        public IReadOnlyList<JsonValue> Rows(PeerQuery query) => throw new NotSupportedException();

        public IReadOnlyList<JsonValue> LatestByKeyFor(JsonValue? sessionId) => throw new NotSupportedException();

        public IReadOnlyList<PeerRowAt> Since(long cursor)
        {
            Reads++;
            return [];
        }

        public long Version() => Version_;
    }
}
