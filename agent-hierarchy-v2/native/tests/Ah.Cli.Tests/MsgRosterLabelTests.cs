using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah msg roster</c>: the text label for open briefs that name a role but no instance.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgRosterLabelTests : IDisposable
{
    private static readonly string Pid = System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    private readonly Sandbox _sandbox = new();
    private readonly string _hierarchy;
    private readonly CliContext _context;
    private readonly FakeDaemonClient _daemon = new() { Reply = _ => new DaemonReply(0, default, "down") };

    public MsgRosterLabelTests()
    {
        _hierarchy = Path.Combine(_sandbox.Root, "hier");
        var teamFile = Path.Combine(_hierarchy, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(teamFile)!);
        File.WriteAllText(
            teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Pid
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p1\"}]}\n");
        _sandbox.PlantPeers(
            _hierarchy,
            "{\"type\":\"peer\",\"name\":\"tm-implementor\",\"role\":\"implementor\",\"status\":\"up\",\"pid\":" + Pid
            + ",\"session_id\":\"s1\",\"ts\":\"" + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}\n");
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = _hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), _daemon);
        FakeRunner.RegisterSelf(graph.Mailbox, "o", teamFile, "orchestrator@tm");
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        Ah.TestKit.TestMsgStore.Sync(_sandbox.AhHome, _sandbox.Root); // the hand-written briefs, stored as msg new would
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(_context, [.. args, "--cwd", _sandbox.Root], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private void OpenBrief(string toName)
    {
        var msgs = Path.Combine(_hierarchy, "msgs");
        Directory.CreateDirectory(msgs);
        File.WriteAllText(
            Path.Combine(msgs, "20260101-000000-aaaa--implementor--brief--request.md"),
            "---\nid: 20260101-000000-aaaa\ntype: request\nto: implementor\nfrom: orchestrator\nslug: brief\nto_name: " + toName
            + "\nteam: tm\ncreated: 2026-01-01T00:00:00-05:00\n---\n\n## [0] task\nx\n");
    }

    private string RosterText() => Run("roster", "--team", "tm", "--plain", "--orchestrator-pid", Pid).Out;

    [Fact]
    public void ABriefNamingARoleButNoInstanceIsLabelledNToRoleNoInstanceYet()
    {
        OpenBrief("null");

        var text = RosterText();

        Assert.Contains("open=1 (1 to role, no instance yet)", text);
        Assert.DoesNotContain("unassigned", text);
    }

    [Fact]
    public void ABriefNamingTheInstanceCarriesNoLabel()
    {
        OpenBrief("tm-implementor");

        var text = RosterText();

        Assert.Contains("open=1", text);
        Assert.DoesNotContain("no instance yet", text);
    }

    [Fact]
    public void TheJsonKeyStaysUnassigned()
    {
        OpenBrief("null");

        var json = Run("roster", "--team", "tm", "--orchestrator-pid", Pid).Out;

        Assert.Contains("\"unassigned\": 1", json.Replace("\"unassigned\":1", "\"unassigned\": 1"));
    }
}
