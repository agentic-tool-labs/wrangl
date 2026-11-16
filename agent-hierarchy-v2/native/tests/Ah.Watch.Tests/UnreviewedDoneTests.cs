using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>b487: a DONE build with no review exchange 15 minutes on raises one <c>unreviewed-done</c> alarm.</summary>
public sealed class UnreviewedDoneTests
{
    private const string Sid = "orch-session";
    private const double Started = 1_000_000_000_000;
    private const double Minute = 60_000;

    private static DoneReport Report(double reportedMs, bool reviewed = false) =>
        new("20261113-171842-fwup", "Alarm when a finished build has no review", "implementor-25@t", "t", reportedMs, reviewed);

    private static (IReadOnlyList<WatchEvent> Events, IReadOnlyList<PeerAppend> Rows) At(double now, DoneReport report, IReadOnlyList<JsonObject>? peers = null) =>
        UnreviewedDone.Evaluate(Sid, now, Started, [report], peers ?? []);

    [Fact]
    public void ReviewedWithinFifteenMinutesNeverAlarms()
    {
        var filed = Started + Minute;
        Assert.Empty(At(filed + (20 * Minute), Report(filed, reviewed: true)).Events);
    }

    [Fact]
    public void UnreviewedAlarmsExactlyOnceAndNeverAgain()
    {
        var filed = Started + Minute;
        var report = Report(filed);
        Assert.Empty(At(filed + (14 * Minute), report).Events);
        var first = At(filed + (15 * Minute), report);
        var alarm = Assert.Single(first.Events);
        Assert.Equal("unreviewed-done", DispatchClock.EventName[alarm.Kind]);
        Assert.Contains("implementor-25@t", alarm.Text, StringComparison.Ordinal);
        Assert.Contains("20261113-171842-fwup", alarm.Text, StringComparison.Ordinal);
        Assert.Contains("\"Alarm when a finished build has no review\"", alarm.Text, StringComparison.Ordinal);
        Assert.Contains("15m ago", alarm.Text, StringComparison.Ordinal);
        Assert.Contains("No review exchange found for this DONE report. If it was reviewed outside a link, ignore this; otherwise send it to the item's reviewer.", alarm.Text, StringComparison.Ordinal);
        var rows = first.Rows.Select(static r => r.Row).ToList();
        Assert.Single(rows);
        Assert.Empty(At(filed + (30 * Minute), report, rows).Events);
    }

    [Fact]
    public void NeedsDecisionReportNeverAlarms()
    {
        // the host passes only reports whose text carries a status: DONE line
        Assert.False(Ah.Hierarchy.HierarchyModule.IsDoneReport("## [0] tldr\n- [1] status: NEEDS-DECISION scope\n"));
        Assert.False(Ah.Hierarchy.HierarchyModule.IsDoneReport("- [1] status: DONE-ISH\n"));
        Assert.True(Ah.Hierarchy.HierarchyModule.IsDoneReport("## [0] tldr\n- [1] status: DONE abc1234\n"));
    }

    [Fact]
    public void ReportOlderThanAnHourOrBeforeTheDaemonStartedNeverAlarms()
    {
        var filed = Started + Minute;
        Assert.Empty(At(filed + (61 * Minute), Report(filed)).Events);
        Assert.Single(At(filed + (60 * Minute), Report(filed)).Events);
        var before = Started - Minute;
        Assert.Empty(At(before + (20 * Minute), Report(before)).Events);
    }
}
