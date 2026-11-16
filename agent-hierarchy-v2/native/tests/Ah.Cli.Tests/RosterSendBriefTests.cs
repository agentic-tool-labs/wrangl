using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The roster verbs' mail seam is the <c>ah msg send --ref --kind brief</c> command: its entry id, its relay line, its refusals.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class RosterSendBriefTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();

    public void Dispose() => _sandbox.Dispose();

    private (RosterComposition Seam, string Brief) Build(bool relay)
    {
        if (relay)
        {
            Directory.CreateDirectory(_sandbox.AhHome);
        }
        else
        {
            Directory.CreateDirectory(_sandbox.AhHome);
            File.WriteAllText(Path.Combine(_sandbox.AhHome, "agent-hierarchy.json"), "{\"single_carrier\":false}\n");
        }

        var hierarchy = Path.Combine(_sandbox.Root, "hier");
        var teamFile = Path.Combine(hierarchy, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(teamFile)!);
        File.WriteAllText(
            teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"s-orch\"},\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"transport_id\":\"p1\"}]}\n");
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = hierarchy,
            ["CLAUDE_CODE_SESSION_ID"] = "s-orch",
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Present = false, Reply = _ => new DaemonReply(0, default, "down") });
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite("s-orch", [new BoxRef("orchestrator@tm", graph.Mailbox.BoxDirFor(teamFile, "orchestrator"))], teamFile, "claude", System.Environment.ProcessId, PidStart: FakeRunner.SelfStart, Bound: true)));
        if (relay)
        {
            var implBox = graph.Mailbox.BoxDirFor(teamFile, "tm-implementor");
            Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite("s-impl", [new BoxRef("tm-implementor@tm", implBox)], teamFile, "claude", System.Environment.ProcessId, PidStart: FakeRunner.SelfStart, Bound: true)));
        }

        var brief = Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, Path.Combine(hierarchy, "msgs", "20260101-000000-aa11--tm-implementor--brief--request.md"), "---\nid: 20260101-000000-aa11\ntype: request\nto: tm-implementor\nfrom: orchestrator\nslug: brief\n---\n\ndo the thing\n");
        return (new RosterComposition(graph, io.Clock, env), brief);
    }

    [Fact]
    public void ABriefToALiveClaudeReceiverReturnsTheEntryIdAndTheRelayLine()
    {
        var (seam, brief) = Build(relay: true);

        var sent = seam.SendBrief(_sandbox.Root, "tm-implementor", "tm", brief);

        Assert.NotNull(sent);
        Assert.False(string.IsNullOrEmpty(sent.EntryId));
        Assert.StartsWith("delivery: relay", sent.DeliveryLine, StringComparison.Ordinal);
        Assert.Contains("tm-implementor", sent.DeliveryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ABriefToAReceiverWithNoLiveSessionReturnsTheEntryIdAndNoRelayLine()
    {
        var (seam, brief) = Build(relay: false);

        var sent = seam.SendBrief(_sandbox.Root, "tm-implementor", "tm", brief);

        Assert.NotNull(sent);
        Assert.False(string.IsNullOrEmpty(sent.EntryId));
        Assert.DoesNotContain("delivery: relay", sent.DeliveryLine ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryLineIsReadForItsIdAndTheDeliveryLineIsKept()
    {
        var sent = RosterComposition.ParseSent("entry 20261017-abc → tm-implementor@tm\ndelivery: relay — send it\n");

        Assert.Equal("20261017-abc", sent.EntryId);
        Assert.Equal("delivery: relay — send it", sent.DeliveryLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("warning: slow disk\nentry abc → x\n")]
    [InlineData("entry\n")]
    public void StdoutWithoutALeadingEntryLineGivesNoIdAndDoesNotThrow(string stdout)
    {
        var sent = RosterComposition.ParseSent(stdout);

        Assert.Null(sent.EntryId);
    }

    [Fact]
    public void ABriefWhoseFileDoesNotExistIsRefusedWithTheCommandsOwnError()
    {
        var (seam, _) = Build(relay: true);

        var failure = Assert.Throws<InvalidOperationException>(() => seam.SendBrief(_sandbox.Root, "tm-implementor", "tm", Path.Combine(_sandbox.Root, "missing.md")));

        Assert.Contains("ah msg send tm-implementor@tm failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABriefToAMemberThatIsNotInTheTeamIsRefused()
    {
        var (seam, brief) = Build(relay: true);

        _ = Assert.Throws<InvalidOperationException>(() => seam.SendBrief(_sandbox.Root, "tm-nobody", "tm", brief));
    }
}
