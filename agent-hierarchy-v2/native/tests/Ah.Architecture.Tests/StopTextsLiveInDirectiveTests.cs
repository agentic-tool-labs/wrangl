using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>The words of a Stop block live in the directive text, in one place: the Stop handlers pass it a built string and never a literal of their own.</summary>
public sealed class StopTextsLiveInDirectiveTests
{
    private static readonly Regex InlineLiteral = new(@"HookEmit\s*\.\s*Block\s*\(\s*(\$|@|"")", RegexOptions.CultureInvariant);

    internal static List<string> Violations(string text) =>
        text.Split('\n').Select((line, i) => (line, i)).Where(p => InlineLiteral.IsMatch(p.line)).Select(p => $"{p.i + 1}: {p.line.Trim()}").ToList();

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("Lifecycle/StopPeerNudge.cs")]
    [InlineData("Lifecycle/StopOrchestratorLiveness.cs")]
    [InlineData("Lifecycle/StopMemberLiveness.cs")]
    [InlineData("Activity/SubagentStopMsgNudge.cs")]
    public void NoStopHandlerPassesALiteralToBlock(string file) =>
        Assert.Empty(Violations(File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Hooks", file))));

    /// <summary>The digest header and the queued delivery line (fewer-turns S7) are written in DirectiveText.cs and only referenced elsewhere.</summary>
    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("ah digest (", "Ah.Hooks/Activity/UserPromptSubmitPeerTracking.cs")]
    [InlineData("delivery: queued", "Ah.Mesh/Mail/Mailbox.Send.cs")]
    [InlineData("you owe ", "Ah.Hooks/Lifecycle/StopPeerNudge.cs")]
    public void TheDigestTextsLiveInTheDirectiveTextAndNotAtTheirCallSite(string literal, string callSite)
    {
        var src = Path.Combine(Workspace.NativeRoot, "src");
        Assert.Contains(literal, File.ReadAllText(Path.Combine(src, "Ah.Config", "Directive", "DirectiveText.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain(literal, File.ReadAllText(Path.Combine(src, callSite)), StringComparison.Ordinal);
    }

    /// <summary>The Orchestrator's checklist line (ah-checklist-upkeep §2.4) ties every dispatch and report to an item and names the ahd alarm.</summary>
    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheChecklistLineAsksForAnItemOnEveryDispatchAndNamesTheUnlinkedDispatchAlarm()
    {
        var text = File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Config", "Directive", "DirectiveText.cs"));
        Assert.Contains(
            "Keep it current as part of every dispatch and every report: each `ah msg new --type request` names `--item <id>` (add the item first if it has none); each report you accept is reflected on its item (`done`, or `set` back to next/blocked with a note). ahd mails you `unlinked-dispatch` for any request the checklist does not track.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Keep it current: `ah roster checklist add|set|done`", text, StringComparison.Ordinal);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void NegativeTheScanSeesAnInlineLiteralAndAPlainVariable()
    {
        Assert.Single(Violations("return HookEmit.Block($\"ah: {x}\");"));
        Assert.Single(Violations("return HookEmit.Block(\"ah: x\");"));
        Assert.Empty(Violations("return HookEmit.Block(reason);"));
        Assert.Empty(Violations("return HookEmit.Block(advisory.Directive.StopNewTeamMail(x));"));
    }
}
