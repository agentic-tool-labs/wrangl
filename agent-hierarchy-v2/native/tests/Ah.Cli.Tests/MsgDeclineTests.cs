using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah msg decline</c> with the daemon down: the carrier's decline is written to the files, and everything that is not a valid decline by the carrier exits 2 with nothing written.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgDeclineTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly string _teamFile;
    private readonly CliContext _context;
    private readonly ServiceGraph _graph;
    private readonly FakeDaemonClient _daemon = new() { Reply = _ => new DaemonReply(0, default, "down") };

    public MsgDeclineTests()
    {
        var hierarchy = Path.Combine(_sandbox.Root, "hier");
        _teamFile = Path.Combine(hierarchy, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_teamFile)!);
        File.WriteAllText(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p1\"}]}\n");
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        _graph = ServiceGraph.Build(io, env, FakeRunner.KnowingSelf(), _daemon);
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, "orchestrator@tm");
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private string Box => _graph.Mailbox.BoxDirFor(_teamFile, "tm-implementor");

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(_context, [.. args, "--cwd", _sandbox.Root], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private (int Exit, string Out, string Err) Decline(params string[] extra) =>
        Run(["decline", "e1", "--orchestrator-pid", System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--json", .. extra]);

    /// <summary>Entry e1 for the member, with a rung-3 request whose carrier is <paramref name="carrier"/>.</summary>
    private void Relayed(string carrier, string? carrierSession = null)
    {
        Assert.Equal(0, Run("send", "tm-implementor@tm", "--text", "hello", "--id", "e1", "--json").Exit);
        var row = new JsonObject { ["id"] = "e1", ["rung"] = 3, ["state"] = "pending", ["attempt"] = 1, ["carrier"] = carrier };
        if (carrierSession is not null)
        {
            row["carrier_session"] = carrierSession;
        }

        Assert.True(_graph.Mailbox.AppendLadderRow(Box, row, 1));
    }

    private IReadOnlyList<string> States() =>
        [.. _graph.Mailbox.LadderRowsOf(Box, "e1").Select(r => r.Get("state")!.Value.AsString())];

    [Fact]
    public void TheCarrierDeclinesWithTheDaemonDownAndTheFilesTakeTheRow()
    {
        Relayed("orchestrator@tm");

        var (exit, stdout, stderr) = Decline("--reason", "already-reported", "--note", "reported at 11:02");

        Assert.Equal(0, exit);
        Assert.Contains("\"declined\":\"e1\"", stdout, StringComparison.Ordinal);
        Assert.Contains("ahd unavailable (down); using files", stderr, StringComparison.Ordinal);
        Assert.Equal(["pending", "declined"], States());
        var row = _graph.Mailbox.LadderRowsOf(Box, "e1")[^1];
        Assert.Equal(("orchestrator@tm", "already-reported", "reported at 11:02"), (row["by"].AsString(), row["reason"].AsString(), row["note"].AsString()));
        Assert.False(_graph.Mailbox.IsAcked(Box, "e1"));
    }

    [Fact]
    public void ADeclineByAnAddressThatIsNotTheCarrierExitsTwoAndWritesNothing()
    {
        Relayed("someone-else@tm");

        var (exit, _, stderr) = Decline("--reason", "withdrawn");

        Assert.Equal(2, exit);
        Assert.Contains("decline_refused", stderr, StringComparison.Ordinal);
        Assert.Equal(["pending"], States());
    }

    [Theory]
    [InlineData("")]
    [InlineData("because")]
    public void ADeclineWithoutAValidReasonExitsTwoAndWritesNothing(string reason)
    {
        Relayed("orchestrator@tm");

        var (exit, _, stderr) = reason.Length == 0 ? Decline() : Decline("--reason", reason);

        Assert.Equal(2, exit);
        Assert.Contains("--reason", stderr, StringComparison.Ordinal);
        Assert.Equal(["pending"], States());
    }

    [Fact]
    public void ADeclineOfAnEntryWithNoRelayRequestExitsTwoAndWritesNothing()
    {
        Assert.Equal(0, Run("send", "tm-implementor@tm", "--text", "hello", "--id", "e1", "--json").Exit);

        var (exit, _, stderr) = Decline("--reason", "withdrawn");

        Assert.Equal(2, exit);
        Assert.Contains("decline_refused", stderr, StringComparison.Ordinal);
        Assert.Empty(States());
    }

    [Fact]
    public void ADeclineOfAnEntryThatWasAlreadyPickedUpExitsTwoAndWritesNothing()
    {
        Relayed("orchestrator@tm");
        _ = _graph.Mailbox.AckIds(Box, ["e1"], "prompt-hook");

        var (exit, _, _) = Decline("--reason", "withdrawn");

        Assert.Equal(2, exit);
        Assert.Equal(["pending"], States());
    }

    [Fact]
    public void ADeclineOfARelayThatNamesAnotherCarrierSessionFromASessionThatIsNotTheCarriersOnlyRegistrationExitsTwoAndWritesNothing()
    {
        Relayed("orchestrator@tm", carrierSession: "s-carrier");
        FakeRunner.RegisterSelf(_graph.Mailbox, "o2", _teamFile, "orchestrator@tm");

        var (exit, _, stderr) = Decline("--reason", "withdrawn", "--session", "o");

        Assert.Equal(2, exit);
        Assert.Contains("decline_refused", stderr, StringComparison.Ordinal);
        Assert.Equal(["pending"], States());
    }

    [Fact]
    public void ADeclineFromTheCarriersOwnRegisteredSessionPassesTheSessionCheck()
    {
        Relayed("orchestrator@tm", carrierSession: "o");

        var (exit, _, _) = Decline("--reason", "withdrawn", "--session", "o");

        Assert.Equal(0, exit);
        Assert.Equal(["pending", "declined"], States());
    }
}
