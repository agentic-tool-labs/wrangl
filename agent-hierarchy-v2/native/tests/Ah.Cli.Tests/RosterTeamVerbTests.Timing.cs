using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary><c>ah roster timing</c> (spec ah-stage-timing §2): the default view, <c>--item</c>, <c>--batch</c>, <c>--plain</c> and its refusals.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string TimingSha = "374a3da44aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string TimingRow(string hhmm, string item, string ev, string? extra = null) =>
        $"{{\"v\":1,\"ts\":\"2026-11-01T{hhmm}:00Z\",\"team\":\"proj\",\"stream\":\"_run\",\"item\":\"{item}\",\"event\":\"{ev}\"{extra},\"by\":\"orchestrator@proj\"}}";

    private static string TimingCause(string ev, string hhmm, string reference = "r") => $",\"cause\":{{\"event\":\"{ev}\",\"ref\":\"{reference}\",\"at\":\"2026-11-01T{hhmm}:00Z\"}}";

    /// <summary>Item a built, reviewed, approved and shipped in <see cref="TimingSha"/>; item b still building.</summary>
    private void TimingBoard()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        Directory.CreateDirectory(ProgressDir);
        File.WriteAllLines(Path.Combine(ProgressDir, "_run.plan.jsonl"),
        [
            TimingRow("09:00", "a", "add", ",\"title\":\"A\""),
            TimingRow("10:00", "a", "start"),
            TimingRow("11:00", "a", "review", TimingCause("reply", "11:00")),
            TimingRow("11:30", "a", "approve", TimingCause("reply", "11:30")),
            TimingRow("12:00", "a", "done", TimingCause("ship", "12:00", TimingSha)),
            TimingRow("09:00", "b", "add", ",\"title\":\"B\""),
            TimingRow("10:00", "b", "start"),
        ]);
        File.WriteAllText(LedgerPath, $"{{\"v\":1,\"at\":\"2026-11-01T12:00:00Z\",\"kind\":\"ship\",\"team\":\"proj\",\"sha\":\"{TimingSha}\",\"ref\":null,\"text\":\"shipped\"}}\n");
    }

    private (int Code, string Stdout, string Stderr) Timing(params string[] args) => Roster(["timing", .. args, "--team", "proj"]);

    [Fact]
    public void TimingBareShowsTheLastBatchAndTheOpenGroups()
    {
        TimingBoard();

        var (code, stdout, stderr) = Timing();

        Assert.True(code == 0, stdout + stderr);
        var json = JsonText.Parse(stdout);
        var batch = Assert.Single(json.Member("batches")!.Value.AsArray());
        Assert.Equal(TimingSha, batch.Member("sha")!.Value.AsString());
        Assert.Equal(3600, batch.Member("stages")!.Value.Member("build")!.Value.AsNumber());
        Assert.Equal("review", batch.Member("critical")!.Value.Member("stage")!.Value.AsString());
        Assert.Equal(1800, batch.Member("critical")!.Value.Member("ship_wait_s")!.Value.AsNumber());
        var open = Assert.Single(json.Member("groups")!.Value.AsArray());
        Assert.Equal("b", open.Member("key")!.Value.AsString());
        Assert.Equal("build", open.Member("current")!.Value.Member("stage")!.Value.AsString());
        Assert.Null(open.Member("intervals"));
        Assert.Equal(["team", "groups", "batches", "signals", "recent"], json.AsObject().Keys);
        Assert.True(json.Member("signals")!.Value.IsArray);
        Assert.Empty(json.Member("recent")!.Value.AsArray());
    }

    [Fact]
    public void TimingItemShowsThatGroupWithItsIntervals()
    {
        TimingBoard();

        var (code, stdout, stderr) = Timing("--item", "a");

        Assert.True(code == 0, stdout + stderr);
        var json = JsonText.Parse(stdout);
        var group = Assert.Single(json.Member("groups")!.Value.AsArray());
        Assert.Equal(7200, group.Member("wall_s")!.Value.AsNumber());
        Assert.Equal(["queued", "build", "review", "ship_wait"], group.Member("intervals")!.Value.AsArray().Select(static i => i.Member("stage")!.Value.AsString()));
        Assert.Empty(json.Member("batches")!.Value.AsArray());
        Assert.Empty(json.Member("recent")!.Value.AsArray());
    }

    [Fact]
    public void TimingBatchTakesAShortShaAndPlainPrintsText()
    {
        TimingBoard();

        var (code, stdout, stderr) = Timing("--batch", "374a3da44", "--plain");

        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("batch 374a3da44 at 2026-11-01T12:00:00Z: 1 item(s), 1 group(s), wall 2h00m, rework 0\n  build 1h00m review 30m ship_wait 30m\n  gated by a in review; ready 30m before ship\ngroup a: wall 2h00m", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTimingRefusesAnUnknownShaAndBothFlagsWithExitTwo()
    {
        TimingBoard();

        var unknown = Timing("--batch", "deadbee");
        var both = Timing("--batch", "374a3da44", "--item", "a");

        Assert.Equal(2, unknown.Code);
        Assert.Contains("no ship deadbee", unknown.Stderr, StringComparison.Ordinal);
        Assert.Single(unknown.Stderr.Trim().Split('\n'));
        Assert.Equal(2, both.Code);
        Assert.Contains("cannot be used together", both.Stderr, StringComparison.Ordinal);
    }
}
