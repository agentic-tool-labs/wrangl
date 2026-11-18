using System.Reflection;
using Ah.Abstractions;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Config.Tests;

/// <summary>
/// The top-level <c>dispatch</c> setting (ah2-200 D1, D4): four values, the most specific config wins, absent or unknown is <c>peers</c>;
/// under <c>subagents</c> a chain role's line carries its Agent call instead of a spawn, and under <c>peers</c> the text is today's.
/// </summary>
public sealed class ConfigDispatchTests
{
    private static readonly string PluginRoot = Path.GetFullPath(Path.Combine(typeof(ConfigDispatchTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!, ".."));

    private const string TodayHeader = "Roles — dispatch route per role below. Ultra-Advisor, Architect, Reviewer, Implementor are peer sessions, never subagents: SendMessage the live one; none live → spawn it with the command on its line, then SendMessage the name it prints (Ultra-Advisor: no per-spawn approval; inside a /wrangl:pipeline run the run anchor's `ultra-advisor: yes|no`, asked once at run start, decides — item 7). Legwork (Task-Runner) always spawns or delegates to task-gopher; pass `model` on the Agent call — agent frontmatter is fallback only:";

    [Theory]
    [InlineData(null, null, null, null, false)] // no config: absent
    [InlineData("{\"version\":1}", "{\"version\":1}", null, null, false)] // configs without the key: absent
    [InlineData("{\"version\":1,\"dispatch\":\"peers\"}", null, "peers", "user", false)]
    [InlineData("{\"version\":1,\"dispatch\":\"subagents\"}", null, "subagents", "user", false)]
    [InlineData(null, "{\"version\":1,\"dispatch\":\"prefer-peers\"}", "prefer-peers", "project", false)]
    [InlineData("{\"version\":1,\"dispatch\":\"peers\"}", "{\"version\":1,\"dispatch\":\"prefer-subagents\"}", "prefer-subagents", "project", false)] // the project layer wins
    [InlineData("{\"version\":1,\"dispatch\":\"subagents\"}", "{\"version\":1}", "subagents", "user", false)] // a layer without the key decides nothing
    [InlineData(null, "{\"version\":1,\"dispatch\":\"subagent\"}", null, null, true)] // negative: a near-miss value is unknown, warned, absent
    [InlineData("{\"version\":1,\"dispatch\":\"subagents\"}", "{\"version\":1,\"dispatch\":\"Subagents\"}", null, null, true)] // negative: case matters; the winning unknown value does not fall back
    [InlineData(null, "{\"version\":1,\"dispatch\":true}", null, null, false)] // negative: a non-text value decides nothing
    public void TheFourValuesResolveAndNegativeAnythingElseIsPeers(string? user, string? project, string? expected, string? source, bool warned)
    {
        WithConfig(user, project, (config, _, repo) =>
        {
            var resolved = config.ResolveConfig(repo);
            Assert.Equal(expected, resolved.Get("dispatch") is { IsText: true } d ? d.AsString() : null);
            Assert.Equal(source, resolved.Get("dispatchSource") is { IsText: true } s ? s.AsString() : null);
            Assert.Equal(expected ?? "peers", DirectiveText.DispatchMode(resolved));
            Assert.Equal(warned, resolved.Get("warnings")!.Value.AsArray().Any(static w => w.AsString().StartsWith("ah: dispatch ", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void UnderSubagentsTheArchitectLineCarriesAnAgentCallAndNoSpawn()
    {
        var lines = DirectiveLines("{\"version\":1,\"dispatch\":\"subagents\"}");
        var architect = Assert.Single(lines, static l => l.StartsWith("- Architect — ", StringComparison.Ordinal));
        Assert.Contains("Agent(subagent_type:\"wrangl:architect\"", architect, StringComparison.Ordinal);
        Assert.DoesNotContain("spawn", architect, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage", architect, StringComparison.Ordinal);
        Assert.Contains(lines, static l => l.StartsWith("Roles — ", StringComparison.Ordinal) && l.Contains("are subagents, never peer sessions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"version\":1}")] // absent: today's text
    [InlineData("{\"version\":1,\"dispatch\":\"peers\"}")]
    [InlineData("{\"version\":1,\"dispatch\":\"nonsense\"}")] // negative: an unknown value is today's text, not a subagent line
    public void UnderPeersTheArchitectLineIsTodaysLine(string user)
    {
        var lines = DirectiveLines(user);
        var architect = Assert.Single(lines, static l => l.StartsWith("- Architect — ", StringComparison.Ordinal));
        Assert.Equal("- Architect — SendMessage its live teammate (names: `ListAgents` / `ah roster teams`); none live → spawn per item 13, then SendMessage the name it prints.", architect);
        Assert.Contains(TodayHeader, lines);
    }

    [Theory]
    [InlineData("subagents", true)]
    [InlineData("prefer-subagents", true)]
    [InlineData("peers", false)] // negative: peers keeps today's peer offers
    [InlineData("prefer-peers", false)] // negative: the nearest prefer mode keeps them too
    public void UnderConfirmItemZeroRecommendsTheSubagentOnlyInSubagentModes(string mode, bool subagent)
    {
        var lines = DirectiveLines($"{{\"version\":1,\"handoffs\":\"confirm\",\"dispatch\":\"{mode}\"}}");
        var item0 = Assert.Single(lines, static l => l.StartsWith("0. Handoff gate", StringComparison.Ordinal));
        Assert.Equal(subagent, item0.Contains("\"Dispatch the <role> subagent (Recommended)\"", StringComparison.Ordinal));
        Assert.Equal(!subagent, item0.Contains("\"Spawn the <role> peer (Recommended)\"", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderPreferPeersTheSubagentExampleAsksForNoModelTheLinesDoNotShow()
    {
        var header = Assert.Single(DirectiveLines("{\"version\":1,\"dispatch\":\"prefer-peers\"}"), static l => l.StartsWith("Roles — ", StringComparison.Ordinal));
        Assert.Contains("`Agent(subagent_type:\"wrangl:<role>\")`, `model` from `ah roster show` or left out", header, StringComparison.Ordinal);
        Assert.DoesNotContain("model:\"<model>\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeUnderPreferSubagentsWithNoOwnedTeamThePeerFallbackIsItem13()
    {
        var header = Assert.Single(DirectiveLines("{\"version\":1,\"dispatch\":\"prefer-subagents\"}"), static l => l.StartsWith("Roles — ", StringComparison.Ordinal));
        Assert.Contains("A peer session (spawn per item 13, then SendMessage", header, StringComparison.Ordinal);
        Assert.DoesNotContain("--team", header, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeLegworkStaysAnAgentCallUnderPeers()
    {
        var lines = DirectiveLines("{\"version\":1,\"dispatch\":\"peers\"}");
        Assert.Contains(lines, static l => l.StartsWith("- Task-Runner — Agent(", StringComparison.Ordinal));
    }

    private static List<string> DirectiveLines(string user)
    {
        List<string> lines = [];
        WithConfig(user, null, (config, text, repo) => lines = [.. text.BuildDirective(config.ResolveConfig(repo), null).Split('\n')]);
        return lines;
    }

    private static void WithConfig(string? user, string? project, Action<IConfigCore, IDirectiveText, string> body)
    {
        var sandbox = Directory.CreateTempSubdirectory("ah-cfg-dispatch-").FullName;
        try
        {
            var home = Path.Combine(sandbox, "home");
            var repo = Path.Combine(sandbox, "proj");
            Directory.CreateDirectory(Path.Combine(home, ".claude", "ah2"));
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Directory.CreateDirectory(Path.Combine(repo, ".claude", "ah2"));
            if (user is not null)
            {
                File.WriteAllText(Path.Combine(home, ".claude", "ah2", "agent-hierarchy.json"), user);
            }

            if (project is not null)
            {
                File.WriteAllText(Path.Combine(repo, ".claude", "ah2", "agent-hierarchy.json"), project);
            }

            var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = home }, new PluginInfo(PluginRoot, "wrangl", "0.0.0", "1"));
            var io = PlatformModule.CreateIo(null);
            IRosterCore roster = null!;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(repo, 4242));
            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
            var text = ConfigModule.CreateDirectiveText(config, ConfigModule.CreateRoleContracts(config, io.Files, env), () => roster);
            body(config, text, repo);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
