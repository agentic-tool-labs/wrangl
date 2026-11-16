using System.Reflection;
using Ah.Abstractions;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Config.Tests;

/// <summary>What a member is told when its chain of command is down, and the agent-file line that makes it obey.</summary>
public sealed class WindDownTextTests
{
    private static readonly string NativeRoot = typeof(WindDownTextTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static readonly string PluginRoot = Path.GetFullPath(Path.Combine(NativeRoot, ".."));

    public static TheoryData<string> MemberFiles => new()
    {
        "agents/implementor.md", "agents/reviewer.md", "agents/architect.md", "agents/ux-engineer.md",
        "agents/docs-writer.md", "agents/qa.md", "agents/sub-orchestrator.md", "agents/ultra-advisor.md",
    };

    private static string Text(string form)
    {
        var sandbox = Directory.CreateTempSubdirectory("ah-winddown-").FullName;
        try
        {
            var home = Path.Combine(sandbox, "home");
            var project = Path.Combine(sandbox, "proj");
            Directory.CreateDirectory(Path.Combine(home, ".claude", "ah2"));
            Directory.CreateDirectory(Path.Combine(project, ".claude"));
            File.WriteAllText(Path.Combine(home, ".claude", "ah2", "agent-hierarchy.json"), "{\"version\":1,\"enabled\":true,\"roles\":{}}\n");
            var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = home }, new PluginInfo(PluginRoot, "wrangl", "0.0.0", "1"));
            var io = PlatformModule.CreateIo(null);
            IRosterCore roster = null!;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(project, 4242));
            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
            var directive = ConfigModule.CreateDirectiveText(config, ConfigModule.CreateRoleContracts(config, io.Files, env), () => roster);
            return directive.WindDown("team ah2-tm3", "orchestrator is Orphaned", "orchestrator@ah2-tm3", form);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void ARoleThatCanCommitIsToldToCommitAndNoteBranchAndSha()
    {
        var text = Text("branch");

        Assert.Contains("(2) commit your work on your branch", text, StringComparison.Ordinal);
        Assert.Contains("branch <b> @ <sha>", text, StringComparison.Ordinal);
        Assert.Contains("(4) end your turn", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("no-tree")]
    [InlineData("anything else")]
    public void NegativeAFormWithoutACommitIsNotToldToCommit(string form)
    {
        var text = Text(form);

        Assert.DoesNotContain("commit your work", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<sha>", text, StringComparison.Ordinal);
        Assert.Contains("(3) end your turn", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("read-only")]
    public void EveryFormNamesTheLinkTheReasonTheNoteCommandAndTheWaitForAJob(string form)
    {
        var text = Text(form);

        Assert.Contains("(team ah2-tm3: orchestrator is Orphaned)", text, StringComparison.Ordinal);
        Assert.Contains("ah msg send orchestrator@ah2-tm3 --kind note --text", text, StringComparison.Ordinal);
        Assert.Contains("if a background job is running, wait for it", text, StringComparison.Ordinal);
        Assert.Contains("if it cannot be sent, say so in your final message", text, StringComparison.Ordinal);
        Assert.Contains("Do not start new work.", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MemberFiles))]
    public void AMemberAgentFileTellsTheRoleToObeyAWindDown(string file)
    {
        var text = File.ReadAllText(Path.Combine(PluginRoot, file));

        Assert.Contains("On a `wind-down` from ah, do exactly what it says and stop.", text, StringComparison.Ordinal);
    }
}
