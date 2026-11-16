using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>What <c>msg inbox</c> shows of an entry's standing on the delivery ladder.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgInboxLadderTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Rig
    {
        internal required ServiceGraph Graph { get; init; }

        internal required CliContext Context { get; init; }

        internal required string TeamFile { get; init; }

        internal required string Root { get; init; }

        internal string Run(params string[] argv)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = MsgVerb.Run(Context, [.. argv, "--json", "--cwd", Root], stdout, stderr);
            Assert.True(exit == 0, stderr.ToString());
            return stdout.ToString();
        }
    }

    private Rig Build()
    {
        var hierarchy = Path.Combine(_sandbox.Root, "hier");
        var teamFile = Path.Combine(hierarchy, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(teamFile)!);
        File.WriteAllText(
            teamFile,
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
        var graph = ServiceGraph.Build(io, env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        FakeRunner.RegisterSelf(graph.Mailbox, "o", teamFile, "orchestrator@tm");
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        return new Rig { Graph = graph, Context = context, TeamFile = teamFile, Root = _sandbox.Root };
    }

    private static JsonObject OnlyEntry(string stdout) =>
        JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(stdout)).AsObject()["entries"].AsArray().Single().AsObject();

    [Fact]
    public void AFreshEntryIsShownPendingAtRung3WithNothingElse()
    {
        var rig = Build();
        _ = rig.Run("send", "tm-implementor@tm", "--text", "hello", "--id", "e-1");

        var entry = OnlyEntry(rig.Run("inbox", "--address", "tm-implementor@tm"));

        Assert.Equal("{\"rung\":3,\"state\":\"pending\"}", JsonText.Stringify(entry["ladder"], 0));
    }

    [Fact]
    public void AnEntryWaitingOnARetryIsShownWithItsAttemptAndTheNextAttemptAndOnceDueWithoutThat()
    {
        var rig = Build();
        _ = rig.Run("send", "tm-implementor@tm", "--text", "hello", "--id", "e-1");
        var box = rig.Graph.Mailbox.BoxDirFor(rig.TeamFile, "tm-implementor");
        var future = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        Assert.True(rig.Graph.Mailbox.AppendLadderRow(
            box,
            new JsonObject { ["id"] = "e-1", ["rung"] = 4, ["state"] = "pending", ["attempt"] = 2, ["next_at"] = future },
            1));
        var waiting = OnlyEntry(rig.Run("inbox", "--address", "tm-implementor@tm"))["ladder"].AsObject();
        Assert.True(rig.Graph.Mailbox.AppendLadderRow(
            box,
            new JsonObject { ["id"] = "e-1", ["rung"] = 4, ["state"] = "pending", ["attempt"] = 2, ["next_at"] = past },
            2));

        var due = OnlyEntry(rig.Run("inbox", "--address", "tm-implementor@tm"))["ladder"].AsObject();

        Assert.Equal(["rung", "state", "attempt", "next_at"], waiting.Keys.ToList());
        Assert.Equal(future, waiting["next_at"].AsString());
        Assert.Equal(["rung", "state", "attempt"], due.Keys.ToList());
    }
}
