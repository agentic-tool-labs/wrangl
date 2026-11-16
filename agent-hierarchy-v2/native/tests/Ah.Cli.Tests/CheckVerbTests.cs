using Ah.Abstractions;
using Ah.Cli.Verbs;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The conformance self-check: its sandbox, its comparison of the files' answers with a daemon's, and its refusals.</summary>
public sealed class CheckVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    /// <summary>Answers each verb the check runs: <c>daemon</c> shapes the answer of a status or inspect call.</summary>
    private sealed class ScriptedRunner(Func<string[], bool, string?> daemon, bool startOk) : IProcessRunner
    {
        internal List<string[]> Calls { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            var argv = request.Argv.ToArray();
            Calls.Add(argv);
            var off = request.Environment is { } env && env.TryGetValue("AH_DAEMON", out var v) && v == "off";
            var verb = argv.Length > 1 ? argv[1] : string.Empty;
            string? body = verb switch
            {
                "start" => startOk ? "{\"ok\":true,\"pid\":1}" : "{\"ok\":false,\"error\":{\"code\":\"x\",\"message\":\"no daemon\"}}",
                "stop" => "{\"ok\":true}",
                "status" or "inspect" => daemon(argv, off),
                _ => null,
            };
            var bytes = body is null ? default : (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(body);
            return new ProcessResult(0, null, false, false, bytes, default);
        }
    }

    private static string Answer(string[] argv, bool off, string extra = "")
    {
        var name = string.Join('-', argv.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)));
        return $"{{\"ok\":true,\"source\":\"{(off ? "files" : "daemon")}\",\"pid\":{(off ? 0 : 7)},\"name\":\"{name}\"{extra}}}";
    }

    private (int Code, string Out, ScriptedRunner Runner) Run(Func<string[], bool, string?> daemon, bool startOk = true)
    {
        var runner = new ScriptedRunner(daemon, startOk);
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin", ["AH_TEST"] = "1" });
        var context = new CliContext(env, new RecordingExec(), runner, "ahd", "/opt/ahd", null, 501);
        var stdout = new StringWriter();

        var code = CheckVerb.Run(context, stdout, new StringWriter());

        return (code, stdout.ToString(), runner);
    }

    private static readonly string[] SeededFiles = ["teams/tm.json", "status.json"];

    private static JsonObject Parse(string text) => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text)).AsObject();

    [Fact]
    public void AnAnswerThatMatchesOnceTheLiveDaemonFieldsAreDroppedPasses()
    {
        var (code, output, runner) = Run((argv, off) => Answer(argv, off));

        Assert.Equal(0, code);
        var body = Parse(output);
        Assert.True(body["ok"].AsBool());
        Assert.Equal("files", body["source"].AsString());
        Assert.Equal(7, (int)body["checked"].AsNumber());
        Assert.Equal(
            ["status", "inspect boxes", "inspect entries", "inspect registrations", "inspect timers", "inspect events", "inspect status"],
            body["cases"].AsArray().Select(c => c.AsString()).ToArray());
        Assert.Contains(runner.Calls, c => c[1] == "start");
        Assert.Contains(runner.Calls, c => c[1] == "stop");
    }

    [Fact]
    public void TheSandboxDaemonIsStartedWithAnIdleExitAndNothingElseIsGivenOne()
    {
        var (_, _, runner) = Run((argv, off) => Answer(argv, off));

        var start = Assert.Single(runner.Calls, c => c[1] == "start");
        var at = Array.IndexOf(start, "--idle-exit-ms");
        Assert.True(at > 1);
        Assert.Equal("300000", start[at + 1]);
        Assert.DoesNotContain(runner.Calls, c => c[1] != "start" && c.Contains("--idle-exit-ms"));
    }

    [Fact]
    public void ADaemonAnswerThatDiffersIsAMismatchWithExitFour()
    {
        var (code, output, _) = Run((argv, off) =>
            argv[1] == "inspect" && argv[2] == "boxes" && !off ? Answer(argv, off, ",\"boxes\":[1]") : Answer(argv, off));

        Assert.Equal(4, code);
        var error = Parse(output);
        Assert.False(error["ok"].AsBool());
        Assert.Equal("mismatch", error["error"].AsObject()["code"].AsString());
        Assert.Equal("inspect boxes", error["error"].AsObject()["message"].AsString());
    }

    [Fact]
    public void ADaemonThatDoesNotServeTheCaseIsAMismatch()
    {
        var (code, output, _) = Run((argv, _) => Answer(argv, true));

        Assert.Equal(4, code);
        Assert.Contains("\"mismatch\"", output, StringComparison.Ordinal);
        Assert.Contains("the sandbox daemon did not serve it", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADaemonThatDoesNotStartIsExitOneAndAsksNothingOfIt()
    {
        var (code, output, runner) = Run((argv, off) => Answer(argv, off), startOk: false);

        Assert.Equal(1, code);
        Assert.Contains("daemon_unavailable", output, StringComparison.Ordinal);
        Assert.Equal(7, runner.Calls.Count(c => c[1] is "status" or "inspect"));
    }

    [Fact]
    public void TheTeamAndStatusFilesTheCheckSeedsCarryNoByteOrderMark()
    {
        var heads = new Dictionary<string, byte[]>();

        _ = Run((argv, off) =>
        {
            var proj = argv[Array.IndexOf(argv, "--cwd") + 1];
            foreach (var relative in SeededFiles)
            {
                var path = $"{proj}/.claude/ah2/{relative}";
                if (File.Exists(path))
                {
                    heads[relative] = File.ReadAllBytes(path)[..3];
                }
            }

            return Answer(argv, off);
        });

        Assert.Equal(2, heads.Count);
        foreach (var head in heads.Values)
        {
            Assert.NotEqual<byte>([0xEF, 0xBB, 0xBF], head);
            Assert.Equal((byte)'{', head[0]);
        }
    }

    [Fact]
    public void TheSandboxIsRemovedBeforeTheVerbReturns()
    {
        var (_, output, _) = Run((argv, off) => Answer(argv, off));

        var path = Parse(output)["sandbox"].AsString();

        Assert.Matches("^/tmp/ahprobe\\.[A-Za-z0-9]{6}$", path);
        Assert.False(Directory.Exists(path));
    }
}
