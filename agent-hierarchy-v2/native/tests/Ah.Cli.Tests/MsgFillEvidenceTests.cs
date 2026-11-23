using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah msg fill --evidence</c> (adr-39): files are copied read-only under the ah home and cited in a generated section; a broken limit stores nothing.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgFillEvidenceTests : IDisposable
{
    private const string Id = "20261016-120000-ab12";

    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly string _proj;
    private readonly string _main;
    private readonly string _request;
    private readonly CliContext _context;

    public MsgFillEvidenceTests()
    {
        _proj = Path.Combine(_sandbox.Root, "proj");
        _main = Path.Combine(_proj, ".claude", "ah2");
        var teamFile = Path.Combine(_main, "teams", "tm.json");
        Directory.CreateDirectory(Path.Combine(_proj, ".git"));
        Directory.CreateDirectory(Path.Combine(_main, "msgs"));
        _sandbox.PlantTeam(
            teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":["
            + "{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p2\"}]}\n");
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        FakeRunner.RegisterSelf(graph.Mailbox, "o", teamFile, "orchestrator@tm");
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        _request = Path.Combine(_main, "msgs", $"{Id}--orchestrator--t1--request.md");
        File.WriteAllText(
            _request,
            $"---\nid: {Id}\ntype: request\nto: orchestrator\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: small\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-10-16T12:00:00+00:00\n---\n\n## [0] tldr\n- x\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(EvidenceDir))
        {
            File.SetUnixFileMode(EvidenceDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _sandbox.Dispose();
    }

    private string EvidenceDir => Path.Combine(_sandbox.AhHome, "evidence", Id);

    private (int Exit, string Out, string Err) Fill(params string[] extra)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] args = ["fill", "--id", Id, "--from", "orchestrator", "--req", _request, "--body", "\"see [e1]\"", .. extra, "--orchestrator-pid", System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--team", "tm", "--cwd", _proj];
        var exit = _sandbox.Mirrored(() => MsgVerb.Run(_context, args, stdout, stderr));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string Source(string name, long bytes = 3)
    {
        var path = Path.Combine(_sandbox.Root, name);
        using var file = File.Create(path);
        file.SetLength(bytes);
        return path;
    }

    private string[] Responses() => Directory.GetFiles(Path.Combine(_main, "msgs"), $"{Id}--*--response.md");

    [Fact]
    public void TwoFilesAreStoredReadOnlyAndTheBodyEndsWithTheSection()
    {
        var (exit, stdout, stderr) = Fill("--evidence", Source("shot.png"), "--evidence", Source("boot sound.wav"));

        Assert.True(exit == 0, stderr);
        Assert.Contains("\"evidence\":[{\"k\":1,\"name\":\"shot.png\",\"type\":\"image/png\",\"bytes\":3}", stdout, StringComparison.Ordinal);
        var e1 = Path.Combine(EvidenceDir, "e1-shot.png");
        Assert.True(File.Exists(e1));
        Assert.True(File.Exists(Path.Combine(EvidenceDir, "e2-boot sound.wav")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(e1));
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(EvidenceDir) & UnixFileMode.UserWrite);
        var body = File.ReadAllText(Assert.Single(Responses()));
        Assert.EndsWith(
            $"## evidence\n- [e1] shot.png · image/png · 3 B · evidence:{Id}/e1\n- [e2] boot sound.wav · audio/wav · 3 B · evidence:{Id}/e2\n",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APartThenTheFinalFillNumberOnWithoutRenumbering()
    {
        Assert.Equal(0, Fill("--part", "--evidence", Source("a.txt")).Exit);

        var (exit, _, stderr) = Fill("--evidence", Source("b.txt"));

        Assert.True(exit == 0, stderr);
        Assert.Equal(["e1-a.txt", "e2-b.txt"], Directory.GetFiles(EvidenceDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AControlCharacterInANameIsShownAsAQuestionMark()
    {
        var (exit, _, stderr) = Fill("--evidence", Source("a\nb.txt"));

        Assert.True(exit == 0, stderr);
        Assert.True(File.Exists(Path.Combine(EvidenceDir, "e1-a?b.txt")));
        Assert.EndsWith($"- [e1] a?b.txt · text/plain · 3 B · evidence:{Id}/e1\n", File.ReadAllText(Assert.Single(Responses())), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAFileOver20MiBExits2AndStoresNothing()
    {
        var (exit, _, stderr) = Fill("--evidence", Source("big.bin", 21L * 1024 * 1024));

        Assert.Equal(2, exit);
        Assert.Contains("big.bin", stderr, StringComparison.Ordinal);
        Assert.Contains("at most 20 MiB per file", stderr, StringComparison.Ordinal);
        Assert.Empty(Responses());
        Assert.False(Directory.Exists(EvidenceDir));
    }

    [Fact]
    public void NegativeASymlinkToADirectoryExits2()
    {
        var link = Path.Combine(_sandbox.Root, "dir-link");
        File.CreateSymbolicLink(link, _sandbox.Root);

        var (exit, _, stderr) = Fill("--evidence", link);

        Assert.Equal(2, exit);
        Assert.Contains("not a regular file", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(EvidenceDir));
    }
}
