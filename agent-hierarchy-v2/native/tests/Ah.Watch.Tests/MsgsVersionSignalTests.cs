using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A message write raises no file event: the view follows the store's write counter while a client is connected, re-reads only what changed, and reads nothing without a client (ah-msgs-db-only §8, §12.6).</summary>
public sealed class MsgsVersionSignalTests
{
    private const string Dir = "/h/.claude/ah2";
    private const string Id = "20261103-005522-12h1";
    private const string Other = "20261103-005522-99zz";

    private sealed class Reader : IViewStateReader
    {
        public long Version;
        public string Body = "first";
        public int VersionReads;
        public int MessageReads;
        public int OtherReads;
        public int KeyReads;
        public long AskedSince = -1;

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            switch (domain + "." + op)
            {
                case "msgs.version":
                    _ = Interlocked.Increment(ref VersionReads);
                    return new JsonObject { ["version"] = Volatile.Read(ref Version) };
                case "msgs.keys":
                    _ = Interlocked.Increment(ref KeyReads);
                    return new JsonObject { ["ids"] = JsonValue.FromArray([JsonValue.FromString(Id), JsonValue.FromString(Other)]) };
                case "msgs.changed":
                    Volatile.Write(ref AskedSince, (long)args["since"].AsNumber());
                    return new JsonObject { ["ids"] = JsonValue.FromArray([JsonValue.FromString(Id)]) };
                case "msgs.read":
                    if (args["id"].AsString() == Other)
                    {
                        _ = Interlocked.Increment(ref OtherReads);
                        return new JsonObject { ["id"] = Other, ["slug"] = "other", ["team"] = "t", ["to"] = "impl", ["from"] = "o" };
                    }

                    _ = Interlocked.Increment(ref MessageReads);
                    return new JsonObject { ["id"] = Id, ["slug"] = Volatile.Read(ref Body), ["team"] = "t", ["to"] = "impl", ["from"] = "o" };
                case "activity.keys":
                    return new JsonObject { ["subjects"] = new JsonArray() };
                default:
                    return JsonValue.Null;
            }
        }
    }

    private sealed class Calls
    {
        public int Full;
        public List<long> Since = [];
    }

    [Fact]
    public void AnEmptyDirSetLoadsAgainUntilAPoolAppearsAndThenStopsLoading()
    {
        var loads = 0;
        IReadOnlyList<string> found = [];
        var dirs = new DirSet(() =>
        {
            loads++;
            return found;
        });

        Assert.Empty(dirs.CurrentOrRefresh());
        Assert.Equal(1, loads);

        found = ["/p"];
        Assert.Equal(["/p"], dirs.CurrentOrRefresh());
        Assert.Equal(2, loads);

        Assert.Equal(["/p"], dirs.CurrentOrRefresh()); // negative: a known set costs no load
        Assert.Equal(2, loads);
    }

    private static (MsgsVersionSignal Signal, Calls Calls) Signal(Func<long?> version)
    {
        var calls = new Calls();
        return (new MsgsVersionSignal(version, () => calls.Full++, calls.Since.Add), calls);
    }

    [Fact]
    public void TheFirstPollIsAFullPass()
    {
        var (signal, calls) = Signal(static () => 7);

        signal.Poll();

        Assert.Equal(1, calls.Full);
        Assert.Empty(calls.Since);
    }

    [Fact]
    public void NegativeAnUnchangedCounterReadsNothing()
    {
        var (signal, calls) = Signal(static () => 7);
        signal.Poll();

        signal.Poll();
        signal.Poll();

        Assert.Equal(1, calls.Full);
        Assert.Empty(calls.Since);
    }

    [Fact]
    public void NegativeAHigherCounterReadsWhatChangedSinceTheLastAndNoFullPass()
    {
        long version = 7;
        var (signal, calls) = Signal(() => version);
        signal.Poll();

        version = 9;
        signal.Poll();
        signal.Poll();

        Assert.Equal(1, calls.Full);
        Assert.Equal([7L], calls.Since);
    }

    [Fact]
    public void ACounterThatWentBackIsAFullPass()
    {
        long version = 7;
        var (signal, calls) = Signal(() => version);
        signal.Poll();

        version = 3;
        signal.Poll();

        Assert.Equal(2, calls.Full);
        Assert.Empty(calls.Since);
    }

    [Fact]
    public void AnArrivalIsAFullPassOfAnUnchangedCounter()
    {
        var (signal, calls) = Signal(static () => 7);
        signal.Poll();

        signal.Forget();
        signal.Poll();

        Assert.Equal(2, calls.Full);
    }

    [Fact]
    public void APollThatFailsIsRetriedFromTheSameCounter()
    {
        long version = 7;
        var fail = true;
        var since = new List<long>();
        var signal = new MsgsVersionSignal(() => version, static () => { }, s =>
        {
            since.Add(s);
            if (fail)
            {
                throw new InvalidOperationException("store down");
            }
        });
        signal.Poll();

        version = 8;
        _ = Assert.Throws<InvalidOperationException>(signal.Poll);
        fail = false;
        signal.Poll();
        signal.Poll();

        Assert.Equal([7L, 7L], since);
    }

    [Fact]
    public void NegativeNoDirectoriesReadNothing()
    {
        var (signal, calls) = Signal(static () => null);

        signal.Poll();

        Assert.Equal(0, calls.Full);
        Assert.Empty(calls.Since);
    }

    [Fact]
    public void NegativeWithNoClientThePollerReadsNoVersion()
    {
        var reader = new Reader();
        using var model = WatchModule.Assemble(reader, () => [Dir], null, new TimelineTests.MemoryStore(), () => 7);

        Thread.Sleep(2500);

        Assert.Equal(0, Volatile.Read(ref reader.VersionReads));
    }

    [Fact]
    public void AClientsArrivalReadsTheVersionAtOnceAndRefreshesTheLane()
    {
        var reader = new Reader();
        using var model = WatchModule.Assemble(reader, () => [Dir], null, new TimelineTests.MemoryStore(), () => 7);
        var inner = LazyAwareModel.Unwrap(model)!;
        model.EmitKeySet(MessagesLane.LaneName);
        Drain(inner, () => Volatile.Read(ref reader.MessageReads) > 0, 3000);
        var before = Volatile.Read(ref reader.MessageReads);

        _ = model.Manifest("members");
        Drain(inner, () => Volatile.Read(ref reader.MessageReads) > before, 3000);

        Assert.True(Volatile.Read(ref reader.VersionReads) >= 1);
        Assert.True(Volatile.Read(ref reader.MessageReads) > before, "the first poll of a client's arrival must refresh the messages lane");
    }

    [Fact]
    public void AClientThatComesBackRefreshesAnUnchangedCounterOnItsFirstPoll()
    {
        var reader = new Reader();
        var clock = 1_000_000L;
        using var model = WatchModule.Assemble(reader, () => [Dir], null, new TimelineTests.MemoryStore(), () => 7, () => Volatile.Read(ref clock));
        var inner = LazyAwareModel.Unwrap(model)!;
        model.EmitKeySet(MessagesLane.LaneName);
        void Tick() => Interlocked.Add(ref clock, 10);
        Drain(inner, () => Volatile.Read(ref reader.MessageReads) > 0, 3000, Tick);
        _ = model.Manifest("members");
        Drain(inner, static () => false, 300, Tick);
        var before = Volatile.Read(ref reader.MessageReads);

        Interlocked.Add(ref clock, 3_600_000);
        _ = model.Manifest("members");
        Drain(inner, () => Volatile.Read(ref reader.MessageReads) > before, 1500, Tick);

        Assert.True(Volatile.Read(ref reader.MessageReads) > before, "the first poll after a client comes back must refresh with the counter unchanged");
    }

    /// <summary>The §12.6 case: a fill changes the store and only the counter says so. No hint, no emit: the slice follows from the poller's timer alone, and only the changed message is read again.</summary>
    [Fact]
    public void TheViewFollowsAFillWithNoFileEventAndReadsOnlyThatMessage()
    {
        var reader = new Reader();
        using var model = WatchModule.Assemble(reader, () => [Dir], null, new TimelineTests.MemoryStore(), () => 7);
        var inner = LazyAwareModel.Unwrap(model)!;
        var slice = MessagesLane.SliceIdOf(Dir, Id);
        string Held() => inner.Store.Current.Slices.TryGetValue(slice, out var s) ? System.Text.Encoding.UTF8.GetString(s.Body.ToArray()) : string.Empty;

        model.EmitKeySet(MessagesLane.LaneName);
        Drain(inner, () => Held().Contains("first", StringComparison.Ordinal), 3000);
        Assert.Contains("first", Held(), StringComparison.Ordinal);

        _ = model.Manifest("members");
        var settled = Volatile.Read(ref reader.OtherReads);
        Drain(inner, () => Volatile.Read(ref reader.OtherReads) > settled, 1000);
        Drain(inner, static () => false, 300);
        var otherBefore = Volatile.Read(ref reader.OtherReads);
        var keysBefore = Volatile.Read(ref reader.KeyReads);
        Volatile.Write(ref reader.Body, "filled");
        _ = Interlocked.Increment(ref reader.Version);

        Drain(inner, () => Held().Contains("filled", StringComparison.Ordinal), 6000, () => model.Manifest("members"));

        Assert.Contains("filled", Held(), StringComparison.Ordinal);
        Assert.Equal(0L, Volatile.Read(ref reader.AskedSince));
        Assert.Equal(otherBefore, Volatile.Read(ref reader.OtherReads));
        Assert.Equal(keysBefore, Volatile.Read(ref reader.KeyReads));
    }

    private static void Drain(ReadModel model, Func<bool> done, int ms, Action? keepConnected = null)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until && !done())
        {
            keepConnected?.Invoke();
            if (!model.Projector.Step())
            {
                Thread.Sleep(10);
            }
        }
    }
}
