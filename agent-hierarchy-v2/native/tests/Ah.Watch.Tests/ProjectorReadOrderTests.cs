using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>Who a team is and what its plan holds are read ahead of a lane's backlog, so a silent team or a member with no write since a restart does not read as nothing there.</summary>
public sealed class ProjectorReadOrderTests
{
    private sealed class ListedLane(string name, string[] keys, List<string> read) : IModelLane
    {
        public string Name => name;

        public IReadOnlyList<string> SliceIds(string key) => [];

        public IReadOnlyList<string> Keys() => keys;

        public LaneResult Reread(string key, long nowMs)
        {
            read.Add(name + "/" + key);
            return new LaneResult([]) { Record = new LaneRecord(JsonText.Parse("{}"), 1) };
        }
    }

    private static void Drain(ReadModel model, List<string> read)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (read.Count < 4 && Environment.TickCount64 < deadline)
        {
            _ = model.Projector.Step();
        }
    }

    private static List<string> Reads(string second, bool passAfterSeed = false)
    {
        var read = new List<string>();
        var now = 1_000L;
        IModelLane[] lanes = [new ListedLane("backlogged", ["a", "b", "c"], read), new ListedLane(second, ["k"], read)];
        using var model = new ReadModel(lanes, new ModelOptions { SeedAtStart = true }, () => now, null, "e", null, new ComposerRegistry());
        model.BeginSeed();
        Drain(model, read);
        if (passAfterSeed)
        {
            // The queue is empty: this ends the seed.
            model.Tick();
            read.Clear();
            model.Emit("backlogged", LaneKey.KeySet, NoticeOrigin.Reconcile);
            model.Emit(second, LaneKey.KeySet, NoticeOrigin.Reconcile);
            now += 60_000;
            Drain(model, read);
        }

        return read;
    }

    [Fact]
    public void NegativeAPassAfterTheSeedWaitsItsTurn() =>
        Assert.Equal("backlogged/a", Reads(ProgressLane.LaneName, passAfterSeed: true)[0]);

    [Theory]
    [InlineData(RegistrationLane.LaneName)]
    [InlineData(TeamLane.LaneName)]
    [InlineData(ProgressLane.LaneName)]
    public void ALaneTheSummaryStandsOnIsReadAheadOfABacklog(string lane) =>
        Assert.Equal(lane + "/k", Reads(lane)[0]);

    [Fact]
    public void NegativeAnyOtherLaneWaitsItsTurn() =>
        Assert.Equal("backlogged/a", Reads("other")[0]);
}
