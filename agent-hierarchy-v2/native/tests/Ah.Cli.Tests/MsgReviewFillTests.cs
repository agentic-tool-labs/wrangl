using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>ah2-530 K2/K3: a link's receiver files its verdict with <c>fill --review</c> while the implementor holds the response slot, and the Orchestrator gets one report.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgReviewFillTests : IDisposable
{
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly string _proj;
    private readonly string _main;
    private readonly string _teamFile;
    private readonly CliContext _context;
    private readonly ServiceGraph _graph;

    public MsgReviewFillTests()
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
            + "{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p2\"},"
            + "{\"role\":\"architect\",\"name\":\"tm-architect\",\"kind\":\"claude\",\"transport_id\":\"p3\"}]}\n");
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

    private void As(string address) => FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, address);

    private List<JsonValue> Entries(string name) => [.. _graph.Mailbox.ReadEntries(_graph.Mailbox.BoxDirFor(_teamFile, name)).Entries.Where(e => e.IsMap)];

    /// <summary>The implementor's request, its response filled, and the request handed to <paramref name="receiver"/> over link lk-1.</summary>
    private (string Id, string Path) HandedOver(string receiver)
    {
        As("orchestrator@tm");
        var made = Run("new", "--to", "implementor", "--from", "orchestrator", "--slug", "rl", "--title", "Build it", "--to-name", "tm-implementor", "--json");
        Assert.True(made.Exit == 0, made.Err);
        var request = JsonText.Parse(made.Out);
        var (id, path) = (request.Member("id")!.Value.AsString(), request.Member("path")!.Value.AsString());
        As("tm-implementor@tm");
        var filled = Run("fill", "--id", id, "--from", "implementor", "--body", "\"DONE built it\"");
        Assert.True(filled.Exit == 0, filled.Err);
        var handoff = new JsonObject
        {
            ["to"] = $"{receiver}@tm", ["from"] = "tm-implementor@tm", ["kind"] = "brief", ["ref"] = path, ["cwd"] = _proj, ["hierarchy_dir"] = _main,
            ["link"] = new JsonObject { ["id"] = "lk-1", ["from"] = "tm-implementor@tm" },
        };
        Assert.True(_sandbox.Mirrored(() => _graph.Mailbox.Send(handoff)).Ok);
        return (id, path);
    }

    [Fact]
    public void ALinkReceiverFilesAVerdictBesideATakenResponseAndTheOrchestratorGetsOneReport()
    {
        var (id, _) = HandedOver("tm-reviewer");
        As("tm-reviewer@tm");
        var before = Entries("orchestrator").Count(e => e.Member("kind")?.AsString() == "report");

        // the plain fill is still refused on the implementor's slot, and now names the command
        var plain = Run("fill", "--id", id, "--from", "reviewer", "--body", "\"VERDICT PASS\"");
        Assert.NotEqual(0, plain.Exit);
        Assert.Contains($"ah msg fill --id {id} --from reviewer --review --body-file -", plain.Err, StringComparison.Ordinal);

        var first = Run("fill", "--id", id, "--from", "reviewer", "--review", "--body", "\"VERDICT CHANGES REQUIRED rl@abc1234\\n- one\"");
        var second = Run("fill", "--id", id, "--from", "reviewer", "--review", "--body", "\"VERDICT PASS rl@abc1234\\n- none\"");

        Assert.True(first.Exit == 0, first.Err);
        Assert.True(second.Exit == 0, second.Err);
        var shown = Run("show", id, "review");
        Assert.True(shown.Exit == 0, shown.Err);
        Assert.Contains("VERDICT PASS rl@abc1234", shown.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("CHANGES REQUIRED", shown.Out, StringComparison.Ordinal);
        // the implementor's response is untouched
        Assert.Contains("DONE built it", Run("show", id, "response").Out, StringComparison.Ordinal);
        var reports = Entries("orchestrator").Where(e => e.Member("kind")?.AsString() == "report").ToList();
        Assert.Equal(before + 2, reports.Count);
        Assert.Equal($"verdict on {id}: VERDICT PASS rl@abc1234", reports[^1].Member("verdict")!.Value.AsString());
        Assert.Equal(1, reports.Count(e => e.Member("verdict")?.AsString() == $"verdict on {id}: VERDICT CHANGES REQUIRED rl@abc1234"));
    }

    [Fact]
    public void NegativeTheSlotHolderFilesNoVerdictOnItsOwnRequestFromALinkedReply()
    {
        var (id, path) = HandedOver("tm-reviewer");
        // the reviewer's reply over the link lands in the implementor's box, ref'd to the implementor's own request
        var reply = new JsonObject
        {
            ["to"] = "tm-implementor@tm", ["from"] = "tm-reviewer@tm", ["kind"] = "brief", ["ref"] = path, ["cwd"] = _proj, ["hierarchy_dir"] = _main,
            ["link"] = new JsonObject { ["id"] = "lk-1", ["from"] = "tm-reviewer@tm" },
        };
        Assert.True(_sandbox.Mirrored(() => _graph.Mailbox.Send(reply)).Ok);
        As("tm-implementor@tm");

        var refused = Run("fill", "--id", id, "--from", "implementor", "--review", "--body", "\"VERDICT PASS\"");

        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("only its link's receiver files a verdict", refused.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(Entries("orchestrator"), e => e.Member("verdict") is { IsText: true });
    }

    [Fact]
    public void NegativeAMemberTheLinkDidNotHandItToFilesNoVerdict()
    {
        var (id, _) = HandedOver("tm-reviewer");
        As("tm-architect@tm");

        var refused = Run("fill", "--id", id, "--from", "architect", "--review", "--body", "\"VERDICT PASS\"");
        var plain = Run("fill", "--id", id, "--from", "architect", "--body", "\"VERDICT PASS\"");

        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("only its link's receiver files a verdict", refused.Err, StringComparison.Ordinal);
        Assert.NotEqual(0, Run("show", id, "review").Exit);
        // negative for the hint: a caller that is not the link's receiver gets the refusal as before
        Assert.NotEqual(0, plain.Exit);
        Assert.DoesNotContain("--review", plain.Err, StringComparison.Ordinal);
    }
}
