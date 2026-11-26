using Ah.Platform;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>A transcript is accepted under the Claude configuration directory's <c>projects</c>, which <c>CLAUDE_CONFIG_DIR</c> moves (ah2-571 E3).</summary>
public sealed class TranscriptRootClaudeConfigTests
{
    private static (string? Alt, string? Home) Accepted()
    {
        var sandbox = UnitSandbox.Create();
        var alt = Path.Combine(sandbox.Root, "alt-claude");
        var altFile = Path.Combine(alt, "projects", "p", "s.jsonl");
        var homeFile = Path.Combine(sandbox.Home, ".claude", "projects", "p", "s.jsonl");
        foreach (var file in new[] { altFile, homeFile })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}\n");
        }

        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["CLAUDE_CONFIG_DIR"] = alt });
        var recovery = HierarchyModule.CreateRecovery(PlatformModule.CreateIo(null).Files, env, new FakeClock(DateTimeOffset.UnixEpoch));
        return (recovery.TranscriptPathOk(altFile), recovery.TranscriptPathOk(homeFile));
    }

    [Fact]
    public void ATranscriptUnderTheConfiguredDirectoryIsAccepted()
    {
        Assert.NotNull(Accepted().Alt);
    }

    [Fact]
    public void ATranscriptUnderTheHomeClaudeDirectoryIsRefusedWhenTheVariableIsSet()
    {
        Assert.Null(Accepted().Home);
    }
}
