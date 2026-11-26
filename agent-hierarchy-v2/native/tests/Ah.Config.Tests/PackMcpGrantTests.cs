using System.Reflection;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Config.Tests;

/// <summary>ah2-508: a pack agent's MCP tools grouped by server in the dry run, and the <c>mcp__&lt;server&gt;__*</c> grant (M1–M4).</summary>
public sealed class PackMcpGrantTests : IDisposable
{
    private static readonly string PluginRoot = Path.GetFullPath(Path.Combine(typeof(PackMcpGrantTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!, ".."));

    private readonly string _sandbox = Directory.CreateTempSubdirectory("ah-cfg-mcp-").FullName;
    private readonly IConfigCore _config;
    private readonly string _plugin;

    public PackMcpGrantTests()
    {
        var home = Path.Combine(_sandbox, "home");
        Directory.CreateDirectory(home);
        _plugin = Path.Combine(_sandbox, "plug");
        Directory.CreateDirectory(Path.Combine(_plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(_plugin, ".claude-plugin", "plugin.json"), "{\"name\":\"x\"}");
        File.WriteAllText(Path.Combine(_plugin, ".mcp.json"), "{\"mcpServers\":{\"y\":{\"command\":\"y\"}}}");
        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = home }, new PluginInfo(PluginRoot, "wrangl", "0.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        IRosterCore roster = null!;
        _config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(_sandbox, 4242));
        roster = RosterModule.CreateCore(io.Files, env, _config, io.Processes);
    }

    public void Dispose() => Directory.Delete(_sandbox, recursive: true);

    private JsonObject Parse(string tools) => _config.PackAgentParse($"---\nname: pk\ndescription: d\ntools: {tools}\n---\nbody\n", "p");

    private static List<string> Codes(JsonObject parsed, string level) =>
        parsed["findings"].AsArray().Where(f => f.Member("level")!.Value.AsString() == level).Select(f => f.Member("code")!.Value.AsString()).ToList();

    [Fact]
    public void M1ToolsOfTwoServersPrintTwoGroupedLinesWithTheirSource()
    {
        var fm = Parse("Read, mcp__srv__a, mcp__srv__b, mcp__srv__c, mcp__srv__d, mcp__plugin_x_y__run, Bash")["fm"];
        var report = _config.PackToolReport(fm, _plugin);
        Assert.Equal("[\"mcp__srv__a\",\"mcp__srv__b\",\"mcp__srv__c\",\"mcp__srv__d\"]", JsonText.Stringify(report["mcp"].Member("mcp__srv")!.Value, 0));
        Assert.Equal(
            [
                "MCP server mcp__srv: 4 tools listed (mcp__srv__a, mcp__srv__b, mcp__srv__c, …) — source: not in this plugin",
                "MCP server mcp__plugin_x_y: 1 tools listed (mcp__plugin_x_y__run) — source: plugin's own .mcp.json",
            ],
            _config.PackToolLines(report, _plugin));
        Assert.False(report.ContainsKey("server_grants")); // negative: named tools are not a server grant
        Assert.Empty(_config.PackToolLines(_config.PackToolReport(Parse("Read, Bash")["fm"], _plugin), _plugin)); // negative: no MCP tool, no line
    }

    [Theory]
    [InlineData("mcp__srv__*", true)]
    [InlineData("mcp__sfx-gen-ctl__*", true)]
    [InlineData("mcp__*", false)] // negative: no server
    [InlineData("mcp__a*__*", false)] // negative: a * in the server name
    [InlineData("mcp__srv__a*", false)] // negative: a * in the tool part
    [InlineData("mcp__srv__**", false)] // negative: more than the one trailing *
    [InlineData("*", false)]
    [InlineData("Bash(git:*)", false)]
    public void M2OnlyAServerAndATrailingWildcardIsAccepted(string entry, bool accepted)
    {
        var parsed = Parse($"Read, {entry}");
        Assert.Equal(!accepted, Codes(parsed, "error").Contains("pack-agent-tools"));
    }

    [Fact]
    public void M2ABareServerNameWarnsAndIsKept()
    {
        var parsed = Parse("Read, mcp__srv");
        Assert.Empty(Codes(parsed, "error"));
        var warn = parsed["findings"].AsArray().Single();
        Assert.Equal("warn", warn.Member("level")!.Value.AsString());
        Assert.Contains("matches no tools in Claude Code's tools field; write mcp__srv__*", warn.Member("message")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal("[\"Read\",\"mcp__srv\"]", JsonText.Stringify(parsed["fm"].Member("tools")!.Value, 0));
        Assert.Empty(Parse("Read, mcp__srv__tool")["findings"].AsArray()); // negative: a named tool does not warn
    }

    [Fact]
    public void M3AServerGrantIsReportedWithItsSourceAndItsLineComesFirst()
    {
        var report = _config.PackToolReport(Parse("Read, mcp__other__t, mcp__ext__*, mcp__plugin_x_y__*")["fm"], _plugin);
        Assert.Equal(
            "[{\"server\":\"mcp__ext\",\"source\":\"user\",\"pinned\":false},{\"server\":\"mcp__plugin_x_y\",\"source\":\"plugin\",\"pinned\":true}]",
            JsonText.Stringify(report["server_grants"], 0));
        var lines = _config.PackToolLines(report, _plugin);
        Assert.Equal(
            "⚠ grants EVERY tool of MCP server mcp__ext, including tools it adds later; Ahd cannot see or pin that list — source: a server you configure (not pinned)",
            lines[0]);
        Assert.EndsWith("source: plugin's own .mcp.json (pinned)", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("MCP server mcp__other:", lines[2], StringComparison.Ordinal);
        Assert.False(report["mcp"].AsObject().ContainsKey("mcp__ext")); // negative: a grant is not grouped as a listed tool
    }

    [Theory]
    [InlineData("review", "mcp__x__*", true)]
    [InlineData("design", "mcp__x__*", true)]
    [InlineData("advise", "mcp__x__*", true)]
    [InlineData("orchestrate", "mcp__x__*", true)] // a class outside implement and legwork, with no contract map
    [InlineData("implement", "mcp__x__*", false)] // negative: implement may use it
    [InlineData("legwork", "mcp__x__*", false)] // negative: legwork may use it
    [InlineData("review", "mcp__x__tool", false)] // negative: a named MCP tool keeps today's handling
    public void M4AServerGrantIsRefusedForReviewDesignAndAdviseOnly(string cls, string entry, bool refused)
    {
        var text = $"---\nname: pk\ndescription: d\ntools: Read, SendMessage, Write, {entry}\n---\nbody\n";
        var result = _config.ValidateAgentContract(new JsonObject
        {
            ["role"] = "pk",
            ["cls"] = cls,
            ["agent"] = "pk",
            ["cwd"] = _sandbox,
            ["inMemory"] = new JsonObject { ["path"] = "p", ["text"] = text, ["fm"] = _config.PackAgentParse(text, "p")["fm"] },
        });
        var hits = result["findings"].AsArray().Where(f => f.Member("code")!.Value.AsString() == "pack-agent-tools").ToList();
        Assert.Equal(refused, hits.Count == 1);
        if (refused)
        {
            Assert.Equal("error", hits[0].Member("level")!.Value.AsString());
            Assert.Contains($"a server-wide grant cannot be checked against the {cls} contract (it may include write tools)", hits[0].Member("message")!.Value.AsString(), StringComparison.Ordinal);
        }
    }
}
