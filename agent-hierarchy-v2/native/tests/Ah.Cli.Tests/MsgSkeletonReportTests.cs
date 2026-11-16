using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A response file that is still the generated template is refused as a report; other kinds may carry it.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgSkeletonReportTests : IDisposable
{
    private const string Id = "20260601-000001-aaaa";
    private const string Skeleton = "## [0] tldr\n- [1] status: \n- [2] changes: \n\n## [1] status\n- none\n\n## [2] changes\n- none\n";

    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly string _hierarchy;
    private readonly string _teamFile;
    private readonly CliContext _context;
    private readonly ServiceGraph _graph;

    public MsgSkeletonReportTests()
    {
        _hierarchy = Path.Combine(_sandbox.Root, "hier");
        _teamFile = Path.Combine(_hierarchy, "teams", "tm.json");
        _sandbox.PlantTeam(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":1,\"session_id\":\"o\"},\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p1\"}]}\n");

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = _hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });

        // the caller is the implementor: a live registered session that owns the address tm-implementor@tm and its box
        FakeRunner.RegisterSelf(_graph.Mailbox, "s-i", _teamFile, "tm-implementor@tm");
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private string MsgsDir => Path.Combine(_hierarchy, "msgs");

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = _sandbox.Mirrored(() => MsgVerb.Run(_context, [.. args, "--cwd", _sandbox.Root, "--session", "s-i", "--json"], stdout, stderr));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string Response(string body)
    {
        Directory.CreateDirectory(MsgsDir);
        var path = Path.Combine(MsgsDir, Id + "--orchestrator--work--response.md");
        File.WriteAllText(path, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: work\n---\n\n{body}");
        return path;
    }

    [Fact]
    public void ASkeletonResponseSentAsAReportIsRefusedWithExitTwo()
    {
        var path = Response(Skeleton);

        var (exit, _, stderr) = Run("send", "orchestrator@tm", "--ref", path);

        Assert.Equal(2, exit);
        var id = Ah.Core.Json.MsgPointer.Id(path);
        Assert.Contains($"error: bad_request: response {id} is still the template — fill it with `ah msg fill --id {id} --from <your role> --body-file -`, then send again.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameResponseAfterTypingIntoStatusIsSentWithExitZero()
    {
        var path = Response(Skeleton.Replace("- [1] status: \n", "- [1] status: DONE\n"));

        Assert.Equal(0, Run("send", "orchestrator@tm", "--ref", path).Exit);
    }

    [Fact]
    public void ASkeletonRefSentAsABriefIsNotRefused()
    {
        var path = Response(Skeleton);

        var (exit, stdout, stderr) = Run("send", "orchestrator@tm", "--kind", "brief", "--ref", path);

        Assert.True(exit == 0, stdout + stderr);
    }
}
