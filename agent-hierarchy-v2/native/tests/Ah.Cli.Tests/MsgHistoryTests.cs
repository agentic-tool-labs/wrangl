using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>msg history</c>'s search, filter and paging flags (spec qa-history-replies §4.6), over rows stored through the real <c>qa-log</c> op.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgHistoryTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Dir => Path.Combine(_sandbox.Root, "hier");

    private FakeEnv Env() => new(
        _sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["AH_DAEMON"] = "off" },
        new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));

    private static ServiceGraph Graph(FakeEnv env) =>
        ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });

    /// <summary>Two questions a minute apart: p1 (t1) with a direct reply and a turn row, p2 (t2) with none.</summary>
    private void Seed()
    {
        Directory.CreateDirectory(Dir);
        var graph = Graph(Env());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        JsonValue[] rows =
        [
            HierarchyModule.QaPromptRow("p1", now - 120_000, "s1", "t1", "Why is the build red?"),
            HierarchyModule.QaDirectRow(now - 110_000, "s1", "t1", "Alpha answer", "p1"),
            HierarchyModule.QaReplyRow(now - 100_000, "s1", "t1", "work charlie work", "p1", typed: true, cut: false, final: "Bravo done", openingId: "o1"),
            HierarchyModule.QaPromptRow("p2", now - 60_000, "s1", "t2", "deploy it"),
        ];
        foreach (var row in rows)
        {
            var args = new JsonObject { ["dir"] = Dir, ["row"] = row };
            Assert.True(graph.Writer.Invoke("qa-log", "append", JsonText.ToUtf8(args, 0), new RequestContext(_sandbox.Root, Dir, Dir, string.Empty)).Ok);
        }
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var env = Env();
        var context = new CliContext(
            env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501,
            () => ReadOnlyMemory<byte>.Empty, new Lazy<ServiceGraph>(Graph(env)));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = MsgVerb.Run(context, ["history", "--dir", Dir, .. args], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string[] Ids(string json) =>
        [.. JsonText.Parse(json.TrimEnd('\n')).Member("threads")!.Value.AsArray().Select(static t => t.Member("prompt")!.Value.Member("id")!.Value.AsString())];

    [Fact]
    public void TheFlagsFilterPageAndPrintTheDirectAndFinalRepliesAndConflictingOrMalformedOnesFail()
    {
        Directory.CreateDirectory(Dir);
        Assert.Equal("no questions logged\n", Run().Out);
        Seed();

        // an empty filtered result says so; with --id the body is the id read's, with no next
        Assert.Equal("no questions match\n", Run("--search", "nowhere").Out);
        Assert.Equal("no questions match\n", Run("--team", "t2", "--answered").Out);
        var byId = JsonText.Parse(Run("--id", "p1", "--json").Out.TrimEnd('\n'));
        Assert.Equal(["p1"], Ids(JsonText.Stringify(byId, 0)));
        Assert.Null(byId.Member("next"));

        // search, team and state each keep their thread
        Assert.Equal(["p1"], Ids(Run("--search", "BUILD", "--json").Out));
        Assert.Equal(["p2"], Ids(Run("--team", "t2", "--json").Out));
        Assert.Equal(["p1"], Ids(Run("--answered", "--json").Out));
        Assert.Equal(["p2"], Ids(Run("--unanswered", "--json").Out));
        Assert.Equal(["p1", "p2"], [.. Ids(Run("--search", string.Empty, "--json").Out).Order(StringComparer.Ordinal)]);

        // --before follows the next a --json read printed; the last page's next is null
        var first = JsonText.Parse(Run("--limit", "1", "--json").Out.TrimEnd('\n'));
        Assert.Equal(["p2"], first.Member("threads")!.Value.AsArray().Select(static t => t.Member("prompt")!.Value.Member("id")!.Value.AsString()));
        var second = Run("--limit", "1", "--before", first.Member("next")!.Value.AsString(), "--json").Out;
        Assert.Equal(["p1"], Ids(second));
        Assert.True(JsonText.Parse(second.TrimEnd('\n')).Member("next")!.Value.IsNull);

        // text: the direct reply, then the final; the whole turn only with --full
        var text = Run("--search", "build").Out;
        Assert.Contains("  reply ", text, StringComparison.Ordinal);
        Assert.Contains("Alpha answer", text, StringComparison.Ordinal);
        Assert.Contains("  final reply ", text, StringComparison.Ordinal);
        Assert.Contains("Bravo done", text, StringComparison.Ordinal);
        Assert.DoesNotContain("charlie", text, StringComparison.Ordinal);
        var full = Run("--search", "build", "--full").Out;
        Assert.Contains("  whole turn ", full, StringComparison.Ordinal);
        Assert.Contains("work charlie work", full, StringComparison.Ordinal);

        // negative: both states together, and a cursor that is not one, are usage failures that print nothing
        Assert.All([Run("--answered", "--unanswered"), Run("--before", "p1"), Run("--before")], r => Assert.Equal((2, string.Empty), (r.Exit, r.Out)));
        Assert.Contains("--answered and --unanswered", Run("--answered", "--unanswered").Err, StringComparison.Ordinal);
    }
}
