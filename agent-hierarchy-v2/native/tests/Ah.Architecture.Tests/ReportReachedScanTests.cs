namespace Ah.Architecture.Tests;

/// <summary>
/// Whether a landed report reached the Orchestrator is answered in one place, <c>IMailboxHooks.ReportReached</c>. The dispatch clock's
/// host, the Stop hook and the prompt hook ask it, and none of them keeps a check of its own.
/// </summary>
public sealed class ReportReachedScanTests
{
    private static string Source(string project, string relative) =>
        File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", project, relative));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("Ah.Cli", "Composition/WatchHostAdapter.cs", "ReportReached(")]
    [InlineData("Ah.Hooks", "Lifecycle/StopOrchestratorLiveness.cs", "ReportReached(")]
    [InlineData("Ah.Hooks", "Activity/UserPromptSubmitPeerTracking.cs", "ReachedOf(")]
    public void EachSiteThatDecidesWhetherAReportWasSentAsksTheSharedPredicate(string project, string file, string call) =>
        Assert.Contains(call, Source(project, file), StringComparison.Ordinal);

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("Ah.Cli", "Composition/WatchHostAdapter.cs")]
    [InlineData("Ah.Hooks", "Lifecycle/StopOrchestratorLiveness.cs")]
    [InlineData("Ah.Hooks", "Activity/UserPromptSubmitPeerTracking.cs")]
    public void NoSiteKeepsItsOwnCheckOfTheMailboxTheSeenRowsOrTheAuthor(string project, string file)
    {
        var text = Source(project, file);

        Assert.DoesNotContain(".ReportDelivered(", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".ReportsDelivered(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bool WrittenByOrchestrator(", text, StringComparison.Ordinal);
    }
}
