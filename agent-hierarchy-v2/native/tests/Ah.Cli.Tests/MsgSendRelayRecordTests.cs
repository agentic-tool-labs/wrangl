using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// A send by a live Claude session of an entry that goes by SendMessage leaves the sender one obligation row naming the entry and
/// the line it must send. A sender that is not a live Claude, a send that goes by the pull adapter, and a repeat of the same id
/// leave none (or no second one).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgSendRelayRecordTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed record Rig(ServiceGraph Graph, CliContext Context, string TeamFile);

    private Rig Build(string harness, bool pullAdapter = false, bool singleCarrier = false, bool receiverSession = false, DaemonReply? health = null, bool daemonPresent = false)
    {
        if (!singleCarrier)
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
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Present = daemonPresent, Reply = request => health is not null && request.PathAndQuery == "/v1/health" ? health : new DaemonReply(0, default, "down") });
        var orchBox = graph.Mailbox.BoxDirFor(teamFile, "orchestrator");
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite("s-orch", [new BoxRef("orchestrator@tm", orchBox)], teamFile, harness, System.Environment.ProcessId, PidStart: FakeRunner.SelfStart, Bound: true)));
        if (pullAdapter)
        {
            graph.Mailbox.WriteHeartbeat(graph.Mailbox.BoxDirFor(teamFile, "tm-implementor"), "stub", "s-impl", 5_000);
        }

        if (receiverSession)
        {
            var implBox = graph.Mailbox.BoxDirFor(teamFile, "tm-implementor");
            Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite("s-impl", [new BoxRef("tm-implementor@tm", implBox)], teamFile, "claude", System.Environment.ProcessId, PidStart: FakeRunner.SelfStart, Bound: true)));
        }

        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        return new Rig(graph, context, teamFile);
    }

    private static readonly string SelfPid = System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    private (int Exit, string Out) Send(Rig rig, string id)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(
            rig.Context,
            ["send", "tm-implementor@tm", "--text", "hello", "--id", id, "--session", "s-orch", "--json", "--cwd", _sandbox.Root],
            stdout,
            stderr);
        return (exit, stdout.ToString());
    }

    private static List<JsonObject> Rows(Rig rig, string entry) =>
    [
        .. rig.Graph.Hierarchy.Core.ReadPeerRecords()
            .Where(static r => r.Kind == JsonKind.Map)
            .Select(static r => r.AsObject())
            .Where(r => r.TryGetValue("relay", out var relay) && relay.Kind == JsonKind.Map
                && relay.AsObject().TryGetValue("entry", out var e) && e.Kind == JsonKind.Text && e.AsString() == entry),
    ];

    [Fact]
    public void ALiveClaudeSenderOfAnEntryThatGoesBySendMessageIsLeftTheObligationToSendIt()
    {
        var rig = Build("claude");

        var (exit, stdout) = Send(rig, "e-relay");

        Assert.Equal(0, exit);
        Assert.Contains("\"sendmessage\"", stdout, StringComparison.Ordinal);
        var row = Assert.Single(Rows(rig, "e-relay"));
        var relay = row["relay"].AsObject();
        Assert.Equal("s-orch", row["session_id"].AsString());
        Assert.Equal("pending", row["status"].AsString());
        Assert.Equal("tm-implementor@tm", relay["address"].AsString());
        Assert.Equal("tm-implementor", relay["to"].AsString());
        Assert.Contains("[ah-entry e-relay to tm-implementor@tm]", relay["line"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InSingleCarrierModeALiveClaudeReceiverMakesTheRelayTheCarrierAndPrintsTheOneSendMessage()
    {
        var rig = Build("claude", singleCarrier: true, receiverSession: true);
        var stdout = new StringWriter();

        var exit = MsgVerb.Run(rig.Context, ["send", "tm-implementor@tm", "--text", "hello", "--id", "e-relay", "--session", "s-orch", "--cwd", _sandbox.Root], stdout, new StringWriter());

        Assert.Equal(0, exit);
        // ah2-317 E4: the relay line ends with the action to take, never "nothing more to do"
        var line = Assert.Single(stdout.ToString().Split('\n'), static l => l.StartsWith("delivery:", StringComparison.Ordinal));
        Assert.Equal("delivery: relay — ah delivers it another way if you do not; make this one SendMessage now: SendMessage \"ah mail from orchestrator@tm to tm-implementor@tm (note): hello [ah-entry e-relay to tm-implementor@tm]\" to tm-implementor", line);
        Assert.DoesNotContain("nothing more to do", line, StringComparison.Ordinal);
        _ = Assert.Single(Rows(rig, "e-relay"));
    }

    [Fact]
    public void InSingleCarrierModeAReceiverWithNoLiveSessionIsCarrierPromptAndLeavesNoObligation()
    {
        var rig = Build("claude", singleCarrier: true);
        var stdout = new StringWriter();

        var exit = MsgVerb.Run(rig.Context, ["send", "tm-implementor@tm", "--text", "hello", "--id", "e-relay", "--session", "s-orch", "--cwd", _sandbox.Root], stdout, new StringWriter());

        Assert.Equal(0, exit);
        // ah2-683 P3': an unregistered receiver gets one instruction and no SendMessage request
        var lines = stdout.ToString().TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("delivery: queued — tm-implementor@tm has not registered; ah delivers it when it registers, or by its fallback after the pull timeout; nothing to send now, and if ah asks for a relay, send that relay as printed", lines[1]);
        Assert.DoesNotContain("SendMessage", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(Rows(rig, "e-relay"));
    }

    private static DaemonReply Health(string json) => new(200, System.Text.Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ADaemonOfAnEarlierBuildGetsNoClaimRecordForANewEntryAndKeepsTheEarlierLadder()
    {
        var rig = Build("claude", singleCarrier: true, receiverSession: true, health: Health("{\"version\":\"2.1.0\",\"protocols\":[1],\"impl\":\"native\"}"));
        var implBox = rig.Graph.Mailbox.BoxDirFor(rig.TeamFile, "tm-implementor");
        var stdout = new StringWriter();

        var exit = MsgVerb.Run(rig.Context, ["send", "tm-implementor@tm", "--text", "hello", "--id", "e-old", "--session", "s-orch", "--cwd", _sandbox.Root], stdout, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Null(rig.Graph.Mailbox.ReadClaim(implBox, "e-old"));
        Assert.DoesNotContain("delivery: plugin prompt", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADaemonThatReportsMailClaimsGetsTheClaimRecord()
    {
        var rig = Build("claude", singleCarrier: true, receiverSession: true, health: Health("{\"version\":\"2.1.0\",\"protocols\":[1,2]}"));
        var implBox = rig.Graph.Mailbox.BoxDirFor(rig.TeamFile, "tm-implementor");

        _ = MsgVerb.Run(rig.Context, ["send", "tm-implementor@tm", "--text", "hello", "--id", "e-new", "--session", "s-orch", "--cwd", _sandbox.Root], new StringWriter(), new StringWriter());

        Assert.NotNull(rig.Graph.Mailbox.ReadClaim(implBox, "e-new"));
    }

    private bool ClaimWritten(DaemonReply health, string id, bool daemonPresent = false)
    {
        var rig = Build("claude", singleCarrier: true, receiverSession: true, health: health, daemonPresent: daemonPresent);
        var implBox = rig.Graph.Mailbox.BoxDirFor(rig.TeamFile, "tm-implementor");

        _ = MsgVerb.Run(rig.Context, ["send", "tm-implementor@tm", "--text", "hello", "--id", id, "--session", "s-orch", "--cwd", _sandbox.Root], new StringWriter(), new StringWriter());

        return rig.Graph.Mailbox.ReadClaim(implBox, id) is not null;
    }

    private static readonly DateTimeOffset SentAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADaemonThatDoesNotAnswerInTimeGetsNoClaimRecord() =>
        Assert.False(ClaimWritten(new DaemonReply(0, default, "timeout", true, SentAt), "e-slow"));

    [Fact]
    public void ADaemonThatAnswersWithAServerErrorGetsNoClaimRecord() =>
        Assert.False(ClaimWritten(new DaemonReply(0, default, "http 500 -", false, SentAt), "e-500"));

    [Fact]
    public void ADaemonThatCouldNotBeSpokenToAfterTheRequestWentOutGetsNoClaimRecord() =>
        Assert.False(ClaimWritten(new DaemonReply(0, default, "down", false, SentAt), "e-cut"));

    [Fact]
    public void AnAnswerWithNoHealthBodyOrNoProtocolListGetsNoClaimRecord()
    {
        Assert.False(ClaimWritten(new DaemonReply(200, default), "e-empty"));
        Assert.False(ClaimWritten(Health("{\"version\":\"2.1.0\",\"impl\":\"native\"}"), "e-noproto"));
        Assert.False(ClaimWritten(Health("[1,2]"), "e-array"));
    }

    [Fact]
    public void WhenNoDaemonExistsTheRequestIsNeverSentAndTheClaimRecordIsWritten() =>
        Assert.True(ClaimWritten(new DaemonReply(0, default, "down"), "e-nodaemon"));

    [Fact]
    public void ARunningDaemonWhoseDiscoveryTimedOutSoTheRequestNeverWentOutGetsNoClaimRecord() =>
        Assert.False(ClaimWritten(new DaemonReply(0, default, "down"), "e-slowdiscover", daemonPresent: true));

    [Fact]
    public void ReleaseHandsASentRelayToThePluginPromptAndRefusesAnEntryThatIsNotOne()
    {
        var rig = Build("claude", singleCarrier: true, receiverSession: true);
        var implBox = rig.Graph.Mailbox.BoxDirFor(rig.TeamFile, "tm-implementor");
        _ = MsgVerb.Run(rig.Context, ["send", "tm-implementor@tm", "--text", "hello", "--id", "e-relay", "--session", "s-orch", "--cwd", _sandbox.Root], new StringWriter(), new StringWriter());
        var early = new StringWriter();
        var tooEarly = MsgVerb.Run(rig.Context, ["release", "e-relay", "--session", "s-orch", "--orchestrator-pid", SelfPid, "--cwd", _sandbox.Root], new StringWriter(), early);
        Assert.NotEqual(0, tooEarly);
        Assert.Contains("only a sent relay that has not been acknowledged can be released", early.ToString(), StringComparison.Ordinal);
        var claim = rig.Graph.Mailbox.Claim(implBox, "e-relay", ClaimActions.Take, new JsonObject { ["session_id"] = "s-orch" });
        Assert.True(claim.Won);
        Assert.True(rig.Graph.Mailbox.Claim(implBox, "e-relay", ClaimActions.Confirm, new JsonObject { ["session_id"] = "s-orch" }).Won);
        var stdout = new StringWriter();

        var exit = MsgVerb.Run(rig.Context, ["release", "e-relay", "--session", "s-orch", "--orchestrator-pid", SelfPid, "--json", "--cwd", _sandbox.Root], stdout, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Equal("relay-failed", rig.Graph.Mailbox.ReadClaim(implBox, "e-relay")!.Get("state")!.Value.AsString());
        var again = MsgVerb.Run(rig.Context, ["release", "e-relay", "--session", "s-orch", "--orchestrator-pid", SelfPid, "--cwd", _sandbox.Root], new StringWriter(), new StringWriter());
        Assert.NotEqual(0, again);
    }

    [Fact]
    public void ReleaseOfAnEntryInNoMailboxOfTheSessionIsRefused()
    {
        var rig = Build("claude", singleCarrier: true, receiverSession: true);
        var stderr = new StringWriter();

        var exit = MsgVerb.Run(rig.Context, ["release", "no-such-entry", "--session", "s-orch", "--orchestrator-pid", SelfPid, "--cwd", _sandbox.Root], new StringWriter(), stderr);

        Assert.NotEqual(0, exit);
        Assert.Contains("has no claim in a mailbox this session may release", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatOfTheSameIdLeavesStillOneObligation()
    {
        var rig = Build("claude");

        _ = Send(rig, "e-relay");
        _ = Send(rig, "e-relay");

        _ = Assert.Single(Rows(rig, "e-relay"));
    }

    [Fact]
    public void ASenderThatIsNotALiveClaudeLeavesNoObligationAndStillGetsTheLine()
    {
        var rig = Build("stub");

        var (exit, stdout) = Send(rig, "e-relay");

        Assert.Equal(0, exit);
        Assert.Contains("\"sendmessage\"", stdout, StringComparison.Ordinal);
        Assert.Empty(Rows(rig, "e-relay"));
    }

    [Fact]
    public void AnEntryTheRecipientsPullAdapterTakesIsNotSentByTheSenderAndLeavesNoObligation()
    {
        var rig = Build("claude", pullAdapter: true);

        var (exit, stdout) = Send(rig, "e-relay");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\"sendmessage\"", stdout, StringComparison.Ordinal);
        Assert.Empty(Rows(rig, "e-relay"));
    }
}
