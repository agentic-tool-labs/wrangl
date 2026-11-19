using System.Text;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>ah2-275 D7: the install stamp check at session start does not wait for registration; a subagent still gets none.</summary>
public sealed class SessionStartInstallStampTests
{
    private sealed class RecordingHost : IInstallStampHost
    {
        public List<string> Rebuilds { get; } = [];

        public void WriteStamp(string path, string text) => File.WriteAllText(path, text);

        public void StartRebuild(string script, string home, IReadOnlyDictionary<string, string?> environment) => Rebuilds.Add(script);
    }

    private static (string Text, RecordingHost Host) Start(string? agentId)
    {
        var f = new OrchestrateCommandTests.Fixture();
        var home = f.Rig.Services.Environment.AhHome;
        Directory.CreateDirectory(home);
        File.WriteAllText(home + "/install.json", """{"version":"0.0.1","auto_rebuild":true,"last_rebuild":null}""");
        var host = new RecordingHost();
        var plain = f.Services();
        var services = plain with
        {
            Advisory = plain.Advisory! with { Callers = new BindingAttestationTests.Callers(registered: []), InstallStamp = host },
        };
        var e = new JsonObject
        {
            ["hook_event_name"] = "SessionStart", ["session_id"] = "s1", ["cwd"] = f.Rig.Sandbox.Root + "/proj", ["source"] = "startup", ["agent_type"] = "wrangl:orchestrator",
        };
        if (agentId is not null)
        {
            e["agent_id"] = agentId;
        }

        return (Encoding.UTF8.GetString(f.Rig.Run("sessionstart", JsonText.Stringify(e, 0), services: () => services).Stdout.Span), host);
    }

    [Fact]
    public void AnUnregisteredTopLevelSessionStartsTheRebuildAndGetsTheLine()
    {
        var (text, host) = Start(agentId: null);

        Assert.Single(host.Rebuilds);
        Assert.True(text.Contains("ahd is being rebuilt in the background for", StringComparison.Ordinal), text);
    }

    [Fact]
    public void NegativeASubagentSessionChecksNoStamp()
    {
        var (text, host) = Start(agentId: "agent-1");

        Assert.Empty(host.Rebuilds);
        Assert.DoesNotContain("ahd is being rebuilt", text, StringComparison.Ordinal);
    }
}
