using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>ah-ui-ia U0 (c): the sitrep lane re-reads a held Sitrep on a progress change of its team or of a lane team, a ledger append included.</summary>
public sealed class SitrepLaneTests
{
    private const string Dir = "/canon/hier";

    private static string DirId => ActivityLane.DirId(Dir);

    private static SitrepLane Lane(Func<JsonValue> body) =>
        new(() => [Dir, "/canon/other"], static _ => true, (_, team, _) => (body(), team == "mgr" ? ["mgr", "mgr-s1"] : [team]));

    [Fact]
    public void NegativeNoProgressFileClassifiesSinceTheLogsAndTheOffSwitchAreRows()
    {
        var table = new PathLaneTable(() => [Dir]);

        // the ledger, plan, checklist and .off are rows, refreshed by the write relay (files-off-disk §4, F4)
        Assert.Equal(PathClass.Unclassified, table.Classify(Dir + "/progress/mgr-s1/ledger.jsonl", out _, out _));
        Assert.Equal(PathClass.Unclassified, table.Classify(Dir + "/progress/mgr-s1/.off", out _, out _));
    }

    [Theory]
    [InlineData("/progress/mgr/ledger.json", "Unclassified")]
    [InlineData("/progress/mgr/ledger.jsonl.bak", "Unclassified")]
    [InlineData("/progress/mgr/ledger.jsonl.tmp", "Ignored")]
    [InlineData("/progress/mgr/x/ledger.jsonl", "Unclassified")]
    public void NegativeTheNearestLedgerNamesAreNotProgress(string tail, string expected)
    {
        var table = new PathLaneTable(() => [Dir]);

        Assert.Equal(expected, table.Classify(Dir + tail, out var lane, out _).ToString());
        Assert.Equal(string.Empty, lane);
    }

    [Fact]
    public void AHeldSitrepIsCoveredByItsOwnTeamAndItsLaneTeamButNotAnotherTeamOrDirectory()
    {
        var lane = Lane(static () => JsonValue.Null);
        var key = lane.KeyOfToken($"sitrep:{DirId}/mgr")!;
        _ = lane.Reread(key, 0);

        Assert.Equal([key], lane.HeldCovering(ProgressLane.KeyOf(Dir, "mgr")));
        Assert.Equal([key], lane.HeldCovering(ProgressLane.KeyOf(Dir, "mgr-s1")));
        Assert.Empty(lane.HeldCovering(ProgressLane.KeyOf(Dir, "mgr-s2")));
        Assert.Empty(lane.HeldCovering(ProgressLane.KeyOf("/canon/other", "mgr-s1")));
    }

    [Fact]
    public void TheSitrepIsFedByTheProgressMessagesTeamsAndBacklogLanesAndByWhoIsWorking()
    {
        Assert.Equal(["activity", "backlog", "messages", "processes", "progress", "teams"], SliceDependencies.FedBy("sitrep:<dirId>/<t>").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NegativeATokenThatIsNotASitrepOrHasNoTeamNamesNoKey()
    {
        var lane = Lane(static () => JsonValue.Null);

        Assert.Null(lane.KeyOfToken($"queue:{DirId}/mgr"));
        Assert.Null(lane.KeyOfToken($"sitrep:{DirId}/"));
        Assert.Null(lane.KeyOfToken("sitrep:nodir/mgr"));
    }

    [Fact]
    public void AReReadWithNoContentChangeServesTheSameBytes()
    {
        var lane = Lane(static () => JsonText.Parse("{\"as_of\":5,\"completed\":[]}"));
        var key = lane.KeyOfToken($"sitrep:{DirId}/mgr")!;

        var first = Assert.Single(lane.Reread(key, 1).Changes);
        var second = Assert.Single(lane.Reread(key, 2).Changes);

        Assert.Equal($"sitrep:{DirId}/mgr", first.Id);
        Assert.Equal(first.Body, second.Body);
    }
}
