using Ah.TestKit;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The peers lane feeds the member verdicts: a member with no registration is judged by its newest peers.jsonl row, and only a changed newest row republishes.</summary>
public sealed partial class TreeLanesTests
{
    private static string PeerRow(string name, string status, string at, long? pid = null, string? start = null) =>
        "{\"name\":\"" + name + "\",\"status\":\"" + status + "\",\"ts\":\"" + at + "\""
        + (pid is { } p ? ",\"pid\":" + p : string.Empty)
        + (start is { } s ? ",\"pid_start\":\"" + s + "\"" : string.Empty) + "}";

    private static string LivenessOf(TreeReader reader, string member)
    {
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;
        return Text(Summary(model).Member("member_states")!.Value.Member(MemberId(model, member))!.Value, "liveness");
    }

    private static TreeReader WithPeerRow(string row, params long[] running)
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.LivePids.AddRange(running);
        reader.PeerRows.Add(row);
        return reader;
    }

    /// <summary>A reader whose member dev, of the given harness kind, has no registration and one newest up row naming a running process.</summary>
    private static TreeReader KindReader(string? kind, long? pid = 4100, string? start = null, bool running = true, string status = "up")
    {
        var reader = WithPeerRow(PeerRow("dev", status, Iso(FixtureNowMs - 60_000), pid, start), running && pid is { } p ? [p] : []);
        reader.Teams[TeamPath] = kind is null ? Doc("so", "dev", "qa") : Doc("so", "dev", "qa").Replace("\"name\":\"dev\"", "\"name\":\"dev\",\"kind\":\"" + kind + "\"", StringComparison.Ordinal);
        reader.Registrations.Remove("s-dev");
        return reader;
    }

    [Fact]
    public void PK1AnUpRowOfAMemberOfAnotherKindNamingARunningProcessWithNoStartIsLive() =>
        Assert.Equal("live", LivenessOf(KindReader("codex"), "dev"));

    [Theory]
    [InlineData("claude")]
    [InlineData(null)]
    public void PK2TheSameRowOfAClaudeMemberOrOneWithNoKindIsUnknown(string? kind) =>
        Assert.Equal("unknown", LivenessOf(KindReader(kind), "dev"));

    [Fact]
    public void PK3ADownRowOfAMemberOfAnotherKindWithAStartWhoseProcessHasExitedIsProvenDead() =>
        Assert.Equal("proven_dead", LivenessOf(KindReader("codex", 4100, "ps-4100", running: false, status: "down"), "dev"));

    public static TheoryData<string> AgreeIds => [.. LivenessAgreeCase.All.Select(static c => c.Id)];

    /// <summary>The evidence of an agreement case, laid out as the read model's state reader and process table show it.</summary>
    private static TreeReader ReaderFor(LivenessAgreeCase c)
    {
        var reader = Truth();
        if (c.ObserverSeesOthers)
        {
            reader.LivePids.AddRange([1, 42]);
        }

        if (c.Registration is { } registration)
        {
            reader.LivePids.RemoveAll(p => p == registration.Pid);
            if (registration.Running)
            {
                reader.LivePids.Add(registration.Pid);
            }
        }

        if (c.Row is { } row)
        {
            reader.PeerRows.Add(PeerRow(c.Member, row.Status, Iso(FixtureNowMs - (row.AgeSeconds * 1000L)), row.Pid, row.PidStart));
            if (row is { Running: true, Pid: { } pid })
            {
                reader.LivePids.Add(pid);
            }
        }

        return reader;
    }

    [Theory]
    [MemberData(nameof(AgreeIds))]
    public void AGTheReadModelAnswersEveryAgreementCaseAsTheSharedTableSays(string id)
    {
        var c = LivenessAgreeCase.All.Single(x => x.Id == id);

        Assert.Equal(c.Expected, LivenessOf(ReaderFor(c), c.Member));
    }

    private static List<string> PublishedPeers(ReadModel model) =>
        [.. model.Store.Current.Slices.Keys.Where(static k => k.StartsWith("peers:", StringComparison.Ordinal))];

    private static string PeersBody(ReadModel model) => Body(model, PeersLane.SliceIdOf(Canon));

    [Fact]
    public void PL1AChangedNewestRowRepublishesThePeersSliceAndRecomposesTheSummary()
    {
        var reader = WithPeerRow(PeerRow("qa", "seen", "2026-06-01T12:50:00.000Z"));
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        Assert.Equal("live", Text(Summary(model).Member("member_states")!.Value.Member(MemberId(model, "qa"))!.Value, "liveness"));
        var before = PeersBody(model);

        reader.PeerRows.Clear();
        reader.PeerRows.Add(PeerRow("qa", "down", "2026-06-01T12:59:00.000Z", 78, "ps-78"));
        model.Emit("peers", Canon);
        Drain(model, clock);

        Assert.NotEqual(before, PeersBody(model));
        Assert.Equal("proven_dead", Text(Summary(model).Member("member_states")!.Value.Member(MemberId(model, "qa"))!.Value, "liveness"));
    }

    [Fact]
    public void PL2ARereadThatLeavesEveryNewestRowAsItWasPublishesNothing()
    {
        var reader = WithPeerRow(PeerRow("qa", "seen", "2026-06-01T12:50:00.000Z"));
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        var before = Projected(model);

        reader.PeerRows.Add(PeerRow("qa", "seen", "2026-06-01T12:50:00.000Z"));
        reader.PeerRows.RemoveAt(reader.PeerRows.Count - 1);
        model.Emit("peers", Canon);
        Drain(model, clock);

        Assert.Equal(before, Projected(model));
    }

    [Fact]
    public void PL3ThePeersLaneReadsThroughTheStateReaderAndNeverParsesTheFile()
    {
        var reader = WithPeerRow(PeerRow("qa", "seen", "2026-06-01T12:50:00.000Z"));
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.True(reader.Calls.GetValueOrDefault("peers.latest") >= 1);
        Assert.Single(PublishedPeers(model));
        Assert.Contains("\"qa\"", PeersBody(model), StringComparison.Ordinal);
    }
}
