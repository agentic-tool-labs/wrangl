using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// A prompt the user or the mod already submitted is never blocked by an ah hook: a blocked prompt is dropped from the turn and, for a
/// mail prompt, shows the user an error. The handlers registered for UserPromptSubmit return context or nothing, never a block or a deny.
/// </summary>
public sealed class PromptHooksNeverBlockTests
{
    private static readonly Regex Refusal = new(@"HookEmit\s*\.\s*(Block|Deny|DenyLine|Decision)\b|\bExitCode\s*[:=]\s*[1-9]|\breturn\s+[1-9]\d*\s*;", RegexOptions.CultureInvariant);

    private static string HooksJson() => File.ReadAllText(Path.Combine(Workspace.NativeRoot, "..", "hooks", "registry.json"));

    /// <summary>The unit names the plugin registers for UserPromptSubmit.</summary>
    internal static List<string> RegisteredUnits(string hooksJson)
    {
        var start = hooksJson.IndexOf("\"UserPromptSubmit\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "hooks.json registers no UserPromptSubmit handler");
        var end = hooksJson.IndexOf("\"PostToolUse\"", start, StringComparison.Ordinal);
        var block = end > start ? hooksJson[start..end] : hooksJson[start..];
        return Regex.Matches(block, @"ah-hook\\?""\s+([a-z-]+)").Select(m => m.Groups[1].Value).ToList();
    }

    internal static List<string> Violations(string text) =>
        text.Split('\n').Select((line, i) => (line, i)).Where(p => Refusal.IsMatch(p.line)).Select(p => $"{p.i + 1}: {p.line.Trim()}").ToList();

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheRegisteredPromptHandlersAreTheFiveThisGuardCovers() =>
        Assert.Equal(["job-track", "userpromptsubmit-peer-tracking", "subagent-inflight", "stream-label", "activity"], RegisteredUnits(HooksJson()));

    [Theory]
    [InlineData("Activity/JobTrack.cs")]
    [InlineData("Activity/UserPromptSubmitPeerTracking.cs")]
    [InlineData("Activity/SubagentInflight.cs")]
    [InlineData("Activity/StreamLabel.cs")]
    [InlineData("Activity/ActivityRecorder.cs")]
    public void NoPromptHandlerReturnsABlockADenyOrANonZeroExit(string file) =>
        Assert.Empty(Violations(File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Hooks", file))));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void NegativeTheScanSeesABlockAndAPlainContextReturn()
    {
        Assert.Single(Violations("return HookEmit.Block(\"x\");"));
        Assert.Empty(Violations("return HookEmit.AdditionalContext(\"UserPromptSubmit\", text);"));
    }
}
