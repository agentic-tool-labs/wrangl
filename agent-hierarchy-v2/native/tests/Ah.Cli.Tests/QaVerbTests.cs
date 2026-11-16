using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Qa;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah qa</c>: what each verb sends to the daemon, what it prints, and which command lines it refuses before sending anything.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class QaVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static string Entry(string id = "e1", string state = "queued", long created = 0, string? claimedBy = null, string set = "core") =>
        "{\"id\":\"" + id + "\",\"sha\":\"" + new string('a', 40) + "\",\"base\":null,\"set\":\"" + set + "\",\"repo\":\"/repo\",\"fresh\":false,\"priority\":\"lane\","
        + "\"requesters\":[\"a@t\",\"b@t\"],\"team\":\"t\",\"created\":" + created + ",\"state\":\"" + state + "\",\"claimed_by\":"
        + (claimedBy is null ? "null" : "\"" + claimedBy + "\"") + ",\"claimed_at\":null,\"result_path\":null,\"verdict\":null,\"ended_at\":null,\"reason\":null}";

    private sealed record Run(int Exit, string Out, string Err, FakeDaemonClient Daemon);

    private Run Call(DaemonReply? reply, params string[] args)
    {
        var daemon = new FakeDaemonClient { Reply = _ => reply ?? new DaemonReply(200, Encoding.UTF8.GetBytes("{\"entry\":" + Entry() + ",\"reason\":null}")) };
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), daemon);
        var context = new CliContext(
            env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501,
            () => Encoding.UTF8.GetBytes(string.Empty), new Lazy<ServiceGraph>(graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = QaVerb.Run(context, args, stdout, stderr);
        return new Run(exit, stdout.ToString(), stderr.ToString(), daemon);
    }

    private static DaemonReply Ok(string json) => new(200, Encoding.UTF8.GetBytes(json));

    private static JsonObject Sent(Run run) => JsonText.Parse(run.Daemon.Requests.Single().Body.ToArray()).AsObject();

    private static string[] Submit(params string[] more) => ["submit", "--repo", "/repo", "--sha", new string('a', 40), "--set", "core", .. more];

    [Fact]
    public void SubmitPostsItsFlagsToTheQaRouteStartingTheDaemonIfNeededAndPrintsTheEntryId()
    {
        var run = Call(null, Submit("--base", new string('b', 40), "--priority", "gate", "--fresh"));

        Assert.Equal(0, run.Exit);
        Assert.Equal("entry e1\n", run.Out);
        var request = run.Daemon.Requests.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/qa/submit", request.PathAndQuery);
        Assert.True(request.StartIfDown);
        Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
        var body = Sent(run);
        Assert.Equal("/repo", body["repo"].AsString());
        Assert.Equal(new string('a', 40), body["sha"].AsString());
        Assert.Equal("core", body["set"].AsString());
        Assert.Equal(new string('b', 40), body["base"].AsString());
        Assert.Equal("gate", body["priority"].AsString());
        Assert.True(body["fresh"].AsBool());
    }

    [Fact]
    public void SubmitSendsNoOptionalKeyThatWasNotGivenAndNoSessionHintUnlessAskedFor()
    {
        var plain = Sent(Call(null, Submit()));
        Assert.Equal(["repo", "set", "sha"], plain.Keys.Order(StringComparer.Ordinal));

        var hinted = Sent(Call(null, Submit("--session", "s-1")));
        Assert.Equal("s-1", hinted["session_id"].AsString());
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("lane")]
    [InlineData("adhoc")]
    public void SubmitTakesEachOfTheThreePriorities(string priority)
    {
        Assert.Equal(0, Call(null, Submit("--priority", priority)).Exit);
    }

    [Theory]
    [InlineData("urgent")]
    [InlineData("Gate")]
    [InlineData("")]
    public void SubmitRefusesAPriorityOutsideTheThreeBeforeAskingTheDaemon(string priority)
    {
        var run = Call(null, Submit("--priority", priority));

        Assert.Equal(2, run.Exit);
        Assert.Empty(run.Daemon.Requests);
        Assert.StartsWith("ah qa: ", run.Err, StringComparison.Ordinal);
        Assert.Empty(run.Out);
    }

    [Theory]
    [InlineData("--repo")]
    [InlineData("--sha")]
    [InlineData("--set")]
    public void SubmitNeedsRepoShaAndSet(string missing)
    {
        var args = Submit().ToList();
        var at = args.IndexOf(missing);
        args.RemoveRange(at, 2);

        var run = Call(null, [.. args]);

        Assert.Equal(2, run.Exit);
        Assert.Empty(run.Daemon.Requests);
    }

    [Fact]
    public void SubmitRefusesARelativeRepoAnUnknownFlagAndStrayWords()
    {
        Assert.Equal(2, Call(null, "submit", "--repo", "rel/path", "--sha", new string('a', 40), "--set", "core").Exit);
        Assert.Equal(2, Call(null, Submit("--prio", "gate")).Exit);
        Assert.Equal(2, Call(null, Submit("extra")).Exit);
        Assert.Equal(2, Call(null, "nosuchverb").Exit);
    }

    [Fact]
    public void NextWithNoClaimableJobPrintsNothingAndJsonCarriesTheReason()
    {
        var none = Ok("{\"entry\":null,\"reason\":\"load-high: 9 >= 6\"}");

        var text = Call(none, "next");
        Assert.Equal(0, text.Exit);
        Assert.Empty(text.Out);
        Assert.Empty(text.Err);

        var json = Call(none, "next", "--json");
        Assert.Equal("{\"entry\":null,\"reason\":\"load-high: 9 >= 6\"}\n", json.Out);
    }

    [Fact]
    public void NextPrintsTheClaimedJobsFieldsAndSendsItsMaxSet()
    {
        var run = Call(null, "next", "--max-set", "other");

        Assert.Equal(0, run.Exit);
        Assert.Equal($"entry e1\nrepo /repo\nsha {new string('a', 40)}\nset core\nfresh false\npriority lane\n", run.Out);
        Assert.Equal("other", Sent(run)["max_set"].AsString());
        Assert.Equal("/v1/qa/next", run.Daemon.Requests.Single().PathAndQuery);
    }

    [Fact]
    public void NextShowsABaseWhenTheJobHasOne()
    {
        var withBase = Ok("{\"entry\":" + Entry().Replace("\"base\":null", "\"base\":\"" + new string('b', 40) + "\"", StringComparison.Ordinal) + ",\"reason\":null}");

        Assert.Contains($"base {new string('b', 40)}\n", Call(withBase, "next").Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("other")]
    public void NextTakesBothMaxSetValues(string value)
    {
        Assert.Equal(0, Call(null, "next", "--max-set", value).Exit);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("Full")]
    public void NextRefusesAnyOtherMaxSet(string value)
    {
        var run = Call(null, "next", "--max-set", value);

        Assert.Equal(2, run.Exit);
        Assert.Empty(run.Daemon.Requests);
    }

    [Fact]
    public void DoneWithAResultSendsItsAbsolutePathAndPrintsTheState()
    {
        var run = Call(Ok("{\"entry\":" + Entry(state: "done") + ",\"reason\":null}"), "done", "e1", "--result", "res/result.json");

        Assert.Equal(0, run.Exit);
        Assert.Equal("entry e1 done\n", run.Out);
        var body = Sent(run);
        Assert.Equal("e1", body["id"].AsString());
        Assert.Equal(Path.GetFullPath("res/result.json"), body["result"].AsString());
        Assert.Null(body.Get("error"));
    }

    [Fact]
    public void DoneWithAnErrorSendsTheLine()
    {
        var run = Call(Ok("{\"entry\":" + Entry(state: "failed") + ",\"reason\":null}"), "done", "e1", "--error", "qa-unknown-set: nope");

        Assert.Equal("entry e1 failed\n", run.Out);
        Assert.Equal("qa-unknown-set: nope", Sent(run)["error"].AsString());
    }

    [Fact]
    public void DoneNeedsAnIdAndExactlyOneOfResultAndError()
    {
        foreach (var args in new[]
        {
            new[] { "done" },
            ["done", "e1"],
            ["done", "e1", "--result", "/r", "--error", "x"],
            ["done", "e1", "e2", "--error", "x"],
            ["done", "e1", "--error"],
        })
        {
            var run = Call(null, args);
            Assert.Equal(2, run.Exit);
            Assert.Empty(run.Daemon.Requests);
        }
    }

    [Fact]
    public void ReleasePrintsTheQueuedEntryAndNeedsOneId()
    {
        var run = Call(null, "release", "e1");
        Assert.Equal("entry e1 queued\n", run.Out);
        Assert.Equal("/v1/qa/release", run.Daemon.Requests.Single().PathAndQuery);

        Assert.Equal(2, Call(null, "release").Exit);
        Assert.Equal(2, Call(null, "release", "e1", "e2").Exit);
    }

    [Fact]
    public void StatusOfOneEntryShowsItsFieldsStateRequestersClaimerAndAge()
    {
        var created = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        var run = Call(Ok("{\"entry\":" + Entry(state: "claimed", created: created, claimedBy: "qa1@t") + ",\"reason\":null}"), "status", "e1");

        Assert.Equal(0, run.Exit);
        Assert.Contains("state claimed\n", run.Out, StringComparison.Ordinal);
        Assert.Contains("requesters a@t,b@t\n", run.Out, StringComparison.Ordinal);
        Assert.Contains("claimed_by qa1@t\n", run.Out, StringComparison.Ordinal);
        Assert.Contains("age 2h\n", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("verdict", run.Out, StringComparison.Ordinal);
        Assert.Equal("e1", Sent(run)["id"].AsString());
    }

    [Fact]
    public void AgeIsInSecondsMinutesOrHours()
    {
        string AgeOf(TimeSpan span) =>
            Call(Ok("{\"entry\":" + Entry(created: DateTimeOffset.UtcNow.Subtract(span).ToUnixTimeMilliseconds()) + ",\"reason\":null}"), "status", "e1").Out
                .Split('\n').Single(l => l.StartsWith("age ", StringComparison.Ordinal));

        Assert.Equal("age 5m", AgeOf(TimeSpan.FromMinutes(5)));
        Assert.Matches("^age [0-9]s$", AgeOf(TimeSpan.FromSeconds(3)));
        Assert.Equal("age 3h", AgeOf(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void ListPrintsOneLinePerEntryAndNothingWhenThereAreNone()
    {
        var created = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        var list = Ok("{\"entries\":[" + Entry("e1", created: created) + "," + Entry("e2", "claimed", created, "qa1@t", "shell") + "]}");

        var run = Call(list, "list");

        Assert.Equal(0, run.Exit);
        Assert.Equal(
            "e1 queued lane core aaaaaaa requesters=2 claimer=- age=5m\ne2 claimed lane shell aaaaaaa requesters=2 claimer=qa1@t age=5m\n",
            run.Out);
        Assert.Empty(Call(Ok("{\"entries\":[]}"), "list").Out);
    }

    [Fact]
    public void ListSendsAllOnlyWhenAskedAndBareStatusListsToo()
    {
        var empty = Ok("{\"entries\":[]}");

        Assert.True(Sent(Call(empty, "list", "--all"))["all"].AsBool());
        Assert.Null(Sent(Call(empty, "list")).Get("all"));
        var bare = Call(empty, "status");
        Assert.Equal("/v1/qa/status", bare.Daemon.Requests.Single().PathAndQuery);
        Assert.Null(Sent(bare).Get("id"));
        Assert.Equal(2, Call(empty, "list", "e1").Exit);
        Assert.Equal(2, Call(empty, "status", "e1", "e2").Exit);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("next")]
    [InlineData("done")]
    [InlineData("release")]
    [InlineData("status")]
    [InlineData("list")]
    public void JsonPrintsTheDaemonsBodyOnOneLine(string verb)
    {
        var body = verb == "list" ? "{\"entries\":[]}" : "{\"entry\":" + Entry() + ",\"reason\":null}";
        string[] args = verb switch
        {
            "submit" => Submit("--json"),
            "done" => ["done", "e1", "--error", "x", "--json"],
            "release" or "status" => [verb, "e1", "--json"],
            _ => [verb, "--json"],
        };

        var run = Call(Ok(body), args);

        Assert.Equal(0, run.Exit);
        Assert.Equal(body + "\n", run.Out);
    }

    [Fact]
    public void ARefusalIsOneErrorLineOnStderrWithExitOneAndNothingOnStdoutEvenWithJson()
    {
        var refused = new DaemonReply(403, Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"qa-caller-unproven\",\"message\":\"the daemon cannot prove which session is calling\"}}"));

        var run = Call(refused, Submit("--json"));

        Assert.Equal(1, run.Exit);
        Assert.Equal("error: qa-caller-unproven: the daemon cannot prove which session is calling\n", run.Err);
        Assert.Empty(run.Out);
    }

    [Fact]
    public void ADownDaemonAndAnUnreadableAnswerAreReported()
    {
        var down = Call(new DaemonReply(0, default, "no socket"), "list");
        Assert.Equal(1, down.Exit);
        Assert.Equal("error: daemon-unavailable: no socket\n", down.Err);

        var garbled = Call(new DaemonReply(500, Encoding.UTF8.GetBytes("not json")), "list");
        Assert.Equal(1, garbled.Exit);
        Assert.Equal("error: http-500\n", garbled.Err);
    }

    [Fact]
    public void HelpAndNoVerbPrintTheUsage()
    {
        foreach (var args in new[] { new[] { "--help" }, ["submit", "--help"], [] })
        {
            var run = Call(null, args);
            Assert.Equal(0, run.Exit);
            Assert.StartsWith("usage: ah qa <verb>", run.Out, StringComparison.Ordinal);
            Assert.Empty(run.Daemon.Requests);
        }
    }
}
