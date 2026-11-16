using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>lane-ops B3 (S17): <c>ah msg forward</c> writes the original's payload with a <c>fwd</c> hop chain, acks the original, and refuses a member, a cycle and an entry not in the caller's box.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgForwardTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly string _proj;
    private readonly string _main;
    private readonly string _teamFile;
    private readonly CliContext _context;
    private readonly ServiceGraph _graph;

    public MsgForwardTests()
    {
        _proj = Path.Combine(_sandbox.Root, "proj");
        _main = Path.Combine(_proj, ".claude", "ah2");
        _teamFile = Path.Combine(_main, "teams", "tm.json");
        Directory.CreateDirectory(Path.Combine(_proj, ".git"));
        Directory.CreateDirectory(Path.Combine(_main, "msgs"));
        _sandbox.PlantTeam(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":["
            + "{\"role\":\"reviewer\",\"name\":\"tm-reviewer\",\"kind\":\"claude\",\"transport_id\":\"p1\"},"
            + "{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p2\"}]}\n");
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = _sandbox.Mirrored(() => MsgVerb.Run(_context, [.. args, "--orchestrator-pid", System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--team", "tm", "--cwd", _proj], stdout, stderr));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string Dir(string name) => _graph.Mailbox.BoxDirFor(_teamFile, name);

    /// <summary>A note from the implementor in the Orchestrator's box.</summary>
    private string PlantNote()
    {
        var req = new JsonObject { ["to"] = "orchestrator@tm", ["from"] = "tm-implementor@tm", ["text"] = "lane report", ["cwd"] = _proj, ["hierarchy_dir"] = _main };
        var sent = _sandbox.Mirrored(() => _graph.Mailbox.Send(req));
        return sent.Value!.Get("entry")!.Value.Member("id")!.Value.AsString();
    }

    private List<JsonValue> Entries(string name) => [.. _graph.Mailbox.ReadEntries(Dir(name)).Entries.Where(e => e.IsMap)];

    [Fact]
    public void ForwardCarriesTheNoteThenTheOriginalAndAcksIt()
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, "orchestrator@tm");
        var id = PlantNote();

        var (exit, _, err) = Run("forward", id, "--to", "tm-reviewer@tm", "--note", "triaged: yours");

        Assert.True(exit == 0, err);
        var fwd = Assert.Single(Entries("tm-reviewer"));
        Assert.Equal("lane report", fwd.Member("text")!.Value.AsString());
        Assert.Equal(id, fwd.Member("fwd")!.Value.Member("of")!.Value.AsString());
        Assert.Equal("tm-implementor@tm", fwd.Member("fwd")!.Value.Member("origin")!.Value.AsString());
        var prompt = _graph.Mailbox.BuildPrompt("tm-reviewer@tm", [fwd]);
        var noteAt = prompt.IndexOf("forwarded by orchestrator@tm", StringComparison.Ordinal);
        Assert.True(noteAt > 0 && prompt.IndexOf("triaged: yours", StringComparison.Ordinal) > noteAt && prompt.IndexOf("lane report", StringComparison.Ordinal) > noteAt, prompt);
        Assert.DoesNotContain(_graph.Mailbox.Pending(Dir("orchestrator")), e => e.Member("id")?.AsString() == id);

        // K negatives: back to the origin is a cycle; an id not in this caller's box is not its to forward
        Assert.Contains("forward_cycle", Run("forward", id, "--to", "tm-implementor@tm").Err, StringComparison.Ordinal);
        Assert.Contains("not_yours", Run("forward", "20261108-000000-zz99", "--to", "tm-reviewer@tm").Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberCannotForward()
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, "tm-implementor@tm");

        var (exit, _, err) = Run("forward", "20261108-000000-zz99", "--to", "tm-reviewer@tm");

        Assert.NotEqual(0, exit);
        Assert.Contains("forward_not_orchestrator", err, StringComparison.Ordinal);
    }
}
