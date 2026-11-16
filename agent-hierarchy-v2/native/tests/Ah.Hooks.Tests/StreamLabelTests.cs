using System.Reflection;
using Ah.Abstractions;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>
/// A stream member's own hook events re-render its stream's tab label through <c>ah roster stream-label</c>; everything else
/// leaves the multiplexer alone. The runner is a recorder: no test here starts a process.
/// </summary>
public sealed class StreamLabelTests
{
    private const string Pane = "p-member";

    private sealed class Recorder(int exitCode = 0, bool timedOut = false) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            Requests.Add(request);
            return new ProcessResult(exitCode, null, timedOut, false, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        }
    }

    /// <summary>An <see cref="IMailbox"/> that answers only the named-team-path question, the one the hook asks.</summary>
    public class NamedTeamOnly : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IMailbox.IsNamedTeamPath)
                ? Path.GetFileName(Path.GetDirectoryName((string)args![0]!)) == "teams"
                : throw new NotSupportedException(targetMethod.Name);
    }

    private static string Team(string stream = "build", string streamState = "open", bool member = true, bool memberStream = true)
    {
        var me = member
            ? "{\"name\":\"me\",\"transport_id\":\"" + Pane + "\"" + (memberStream ? ",\"stream\":\"" + stream + "\"" : string.Empty) + "}"
            : "null";
        return "{\"version\":1,\"expected_root\":\"/work/repo\",\"members\":[null,{\"name\":\"other\",\"transport_id\":\"p-other\",\"stream\":\"build\"}," + me
            + "],\"streams\":{\"build\":{\"state\":\"" + streamState + "\"}}}";
    }

    private static (HooksHostTests.Rig Rig, Recorder Runner, Func<string, string, HookOutcome> Fire) Setup(
        string teamJson, string teamPath = "team.json", int exit = 0, bool timedOut = false, bool withPane = true, bool withTeam = true)
    {
        var rig = new HooksHostTests.Rig();
        var file = rig.Sandbox.Root + "/proj/.claude/ah2/" + teamPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, teamJson);
        var variables = new Dictionary<string, string> { ["HOME"] = rig.Sandbox.Home, ["AH_HOME"] = rig.Sandbox.Root + "/ah", ["AH_TEST"] = "1" };
        if (withPane)
        {
            variables["HERDR_PANE_ID"] = Pane;
        }

        if (withTeam)
        {
            variables["WRANGL_TEAM_FILE"] = file;
        }

        var runner = new Recorder(exit, timedOut);
        var advisory = new AdvisoryServices(
            DispatchProxy.Create<IMailbox, NamedTeamOnly>(), null!, null!, null!, runner, null!, null!, null!, null!, null!, null!);
        var services = rig.Services with
        {
            Environment = new FakeEnvironment(variables, new PluginInfo("/ahroot", "wrangl", "0.0.0", "1")),
            Advisory = advisory,
        };
        HookOutcome Fire(string eventName, string extra) =>
            rig.Run("stream-label", $$"""{"hook_event_name":"{{eventName}}","session_id":"s1","cwd":"{{rig.Sandbox.Root}}/proj"{{extra}}}""", services: () => services);
        return (rig, runner, Fire);
    }

    [Theory]
    [InlineData("UserPromptSubmit", "working")]
    [InlineData("Stop", "idle")]
    [InlineData("Notification", "blocked")]
    public void AnOpenStreamMemberRendersItsOwnStateThroughTheRosterVerb(string eventName, string state)
    {
        var (rig, runner, fire) = Setup(Team());

        var outcome = fire(eventName, string.Empty);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Empty(rig.Errors.Records);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(
            ["/ahroot/bin/ah", "roster", "stream-label", "build", "--self-pane", Pane, "--self-state", state, "--cwd", "/work/repo"],
            request.Argv);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout);
    }

    [Fact]
    public void ANamedTeamFileAddsTheTeamNameAndTheLegacyFileDoesNot()
    {
        var (_, named, fireNamed) = Setup(Team(), "teams/alpha.json");
        fireNamed("Stop", string.Empty);
        var (_, legacy, fireLegacy) = Setup(Team(), "team.json");
        fireLegacy("Stop", string.Empty);

        Assert.Equal(["--team", "alpha"], Assert.Single(named.Requests).Argv.TakeLast(2));
        Assert.DoesNotContain("--team", Assert.Single(legacy.Requests).Argv);
    }

    [Theory]
    [InlineData("PostToolUse", "")]
    [InlineData("SessionStart", "")]
    [InlineData("Stop", ",\"agent_id\":\"a1\",\"agent_type\":\"task-gopher:task-gopher\"")]
    public void AnEventThatIsNotTheMembersOwnTurnBoundaryRendersNothing(string eventName, string extra)
    {
        var (rig, runner, fire) = Setup(Team());

        var outcome = fire(eventName, extra);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Empty(runner.Requests);
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void ASessionOutsideAHerdrPaneOrATeamRendersNothing()
    {
        var (_, noPane, firePane) = Setup(Team(), withPane: false);
        var (_, noTeam, fireTeam) = Setup(Team(), withTeam: false);

        firePane("Stop", string.Empty);
        fireTeam("Stop", string.Empty);

        Assert.Empty(noPane.Requests);
        Assert.Empty(noTeam.Requests);
    }

    [Theory]
    [InlineData("done", true, true)]
    [InlineData("open", false, true)]
    [InlineData("open", true, false)]
    public void AMemberWhoseStreamIsNotOpenOrWhoIsNotAStreamMemberRendersNothing(string streamState, bool member, bool memberStream)
    {
        var (rig, runner, fire) = Setup(Team(streamState: streamState, member: member, memberStream: memberStream));

        fire("Stop", string.Empty);

        Assert.Empty(runner.Requests);
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void AMemberWhoseStreamIsNotInTheStreamsTableRendersNothing()
    {
        var (_, runner, fire) = Setup(Team(stream: "missing"));

        fire("Stop", string.Empty);

        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void AFailedOrTimedOutRenderIsOneErrorRowAndNeverBlocks(int exit, bool timedOut)
    {
        var (rig, runner, fire) = Setup(Team(), exit: exit, timedOut: timedOut);

        var outcome = fire("Stop", string.Empty);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Single(runner.Requests);
        Assert.Single(rig.Errors.Records);
    }

    [Fact]
    public void AnUnreadableTeamFileIsOneErrorRowAndNeverBlocks()
    {
        var (rig, runner, fire) = Setup("not json");

        var outcome = fire("Stop", string.Empty);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Empty(runner.Requests);
        Assert.Single(rig.Errors.Records);
    }
}
