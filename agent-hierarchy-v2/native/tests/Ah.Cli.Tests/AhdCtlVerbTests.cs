using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.AhdCtl;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The flat <c>ahd contract|inspect</c> verbs: their argument rules and the daemon-or-files answer.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class AhdCtlVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed record Outcome(int Code, string Out, string Err)
    {
        internal JsonObject Body => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(Out)).AsObject();
    }

    private static readonly Lazy<ServiceGraph> NeverResolved = new(() => throw new InvalidOperationException("the services were resolved"));

    private CliContext ContextWith(Lazy<ServiceGraph>? services)
    {
        var hierarchy = Path.Combine(_sandbox.Root, "hier");
        Directory.CreateDirectory(hierarchy);
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        return new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, services);
    }

    private Lazy<ServiceGraph> RealServices()
    {
        _sandbox.Write("plugin/hooks/participation-contract.md", "# The participation contract\n");
        var context = ContextWith(null);
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), context.Env, new FakeRunner(), new FakeDaemonClient());
        return new Lazy<ServiceGraph>(graph);
    }

    private Outcome Run(Lazy<ServiceGraph>? services, FakeDaemonClient? client, params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = AhdCtlVerb.Run(ContextWith(services), argv[0], argv[1..], stdout, stderr, client ?? new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        return new Outcome(code, stdout.ToString(), stderr.ToString());
    }

    private static DaemonReply Json(int status, string body) => new(status, System.Text.Encoding.UTF8.GetBytes(body));

    private static string UsageMessage(Outcome o)
    {
        Assert.Equal(2, o.Code);
        var error = o.Body["error"].AsObject();
        Assert.Equal("usage", error["code"].AsString());
        return error["message"].AsString();
    }

    [Fact]
    public void WithoutComposedServicesTheVerbSaysSoAndExitsSeventy()
    {
        var o = Run(null, null, "contract");

        Assert.Equal(AhdCtlVerb.NotComposed, o.Code);
        Assert.Contains("have not been composed", o.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("inspect", "nope")]
    public void InspectWithNoKnownSubIsAUsageErrorAndNeverResolvesTheServices(params string[] argv) =>
        Assert.Contains("inspect needs one of", UsageMessage(Run(NeverResolved, null, argv)), StringComparison.Ordinal);

    [Fact]
    public void InspectEntriesNeedsAnAddress()
    {
        Assert.Contains("--address", UsageMessage(Run(NeverResolved, null, "inspect", "entries")), StringComparison.Ordinal);

        var withAddress = Run(RealServices(), null, "inspect", "entries", "--address", "nobody@none");
        Assert.NotEqual(2, withAddress.Code);
    }

    [Theory]
    [InlineData("inspect", "status")]
    [InlineData("inspect", "status", "--dir", "relative/dir")]
    public void InspectStatusNeedsAnAbsoluteDirectory(params string[] argv) =>
        Assert.Contains("--dir <absolute hierarchy dir>", UsageMessage(Run(NeverResolved, null, argv)), StringComparison.Ordinal);

    [Fact]
    public void AFlagTheSubDoesNotTakeIsRefused()
    {
        Assert.Equal("contract does not take --address", UsageMessage(Run(NeverResolved, null, "contract", "--address", "a@b")));
        Assert.Equal("inspect boxes does not take --bogus", UsageMessage(Run(NeverResolved, null, "inspect", "boxes", "--bogus", "x")));
    }

    [Fact]
    public void AnExtraPositionalIsRefused() =>
        Assert.Contains("unexpected argument", UsageMessage(Run(NeverResolved, null, "contract", "extra")), StringComparison.Ordinal);

    [Fact]
    public void ACommandAfterTheDoubleDashIsRefusedOutsideConform() =>
        Assert.Equal("inspect takes no -- command", UsageMessage(Run(NeverResolved, null, "inspect", "boxes", "--", "x")));

    [Fact]
    public void MoreThanOneEntryModeIsRefused() =>
        Assert.Contains("at most one of --pending, --acked, --all", UsageMessage(Run(NeverResolved, null, "inspect", "entries", "--address", "a@b", "--pending", "--acked")), StringComparison.Ordinal);

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("x")]
    public void ACountOutsideOneToTwoHundredIsRefused(string n) =>
        Assert.Contains("-n must be a whole number from 1 to 200", UsageMessage(Run(NeverResolved, null, "inspect", "entries", "--address", "a@b", "-n", n)), StringComparison.Ordinal);

    [Theory]
    [InlineData("1")]
    [InlineData("200")]
    public void ACountAtTheEdgesOfTheRangeIsAccepted(string n) =>
        Assert.NotEqual(2, Run(RealServices(), null, "inspect", "entries", "--address", "nobody@none", "-n", n).Code);

    [Fact]
    public void APlainUsageErrorGoesToStandardErrorAsOneLine()
    {
        var o = Run(NeverResolved, null, "inspect", "--plain");

        Assert.Equal(2, o.Code);
        Assert.Empty(o.Out);
        Assert.StartsWith("error: usage: inspect needs one of", o.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractTheDaemonServesIsShownWithItsSourceAndIsAskedForWithoutStartingADaemon()
    {
        var client = new FakeDaemonClient { Reply = _ => Json(200, "{\"version\":\"9.9.9\",\"contract\":7}") };

        var o = Run(RealServices(), client, "contract");

        Assert.Equal(0, o.Code);
        Assert.Equal("daemon", o.Body["source"].AsString());
        Assert.Equal(7, (int)o.Body["contract"].AsNumber());
        var request = Assert.Single(client.Requests);
        Assert.Equal("/v1/contract", request.PathAndQuery);
        Assert.False(request.StartIfDown);
    }

    [Fact]
    public void AContractIsReadFromTheFilesWhenNoDaemonAnswers()
    {
        var o = Run(RealServices(), null, "contract");

        Assert.Equal(0, o.Code);
        Assert.Equal("files", o.Body["source"].AsString());
        Assert.True(o.Body["requirements"].IsArray);
    }

    [Fact]
    public void ADaemonErrorThatIsTheFinalWordIsReportedWithExitOne()
    {
        var client = new FakeDaemonClient { Reply = _ => Json(403, "{\"error\":{\"code\":\"not_admitted\",\"message\":\"no\"}}") };

        var o = Run(RealServices(), client, "inspect", "registrations");

        Assert.Equal(1, o.Code);
        var error = o.Body["error"].AsObject();
        Assert.Equal("not_admitted", error["code"].AsString());
        Assert.Equal("no", error["message"].AsString());
    }

    [Theory]
    [InlineData(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"x\"}}")]
    [InlineData(400, "{\"error\":{\"code\":\"bad_request\",\"message\":\"x\",\"field\":\"cwd\"}}")]
    [InlineData(500, "{\"error\":{\"code\":\"internal\",\"message\":\"x\"}}")]
    [InlineData(200, "not json")]
    public void AnAnswerThatIsNotTheFinalWordFallsBackToTheFiles(int status, string body)
    {
        var client = new FakeDaemonClient { Reply = _ => Json(status, body) };

        var o = Run(RealServices(), client, "inspect", "registrations");

        Assert.Equal(0, o.Code);
        Assert.Equal("files", o.Body["source"].AsString());
    }

    [Fact]
    public void ARegistrationsAnswerFromTheDaemonIsPassedThrough()
    {
        var client = new FakeDaemonClient { Reply = _ => Json(200, "{\"registrations\":[{\"session_id\":\"s1\"}]}") };

        var o = Run(RealServices(), client, "inspect", "registrations");

        Assert.Equal(0, o.Code);
        Assert.Equal("daemon", o.Body["source"].AsString());
        Assert.Equal("s1", o.Body["registrations"].AsArray().Single().AsObject()["session_id"].AsString());
        Assert.Equal("/v1/inspect/registrations", Assert.Single(client.Requests).PathAndQuery);
    }

    [Theory]
    [InlineData("/v1/inspect/registrations", "registrations")]
    [InlineData("/v1/inspect/boxes", "boxes")]
    [InlineData("/v1/inspect/timers", "timers")]
    [InlineData("/v1/inspect/events", "events")]
    public void TheFilesAnswerOfAnInspectPathIsItsListDocument(string path, string key)
    {
        var graph = RealServices().Value;

        var document = AhdCtlVerb.InspectFiles(graph, path, null, null);

        Assert.NotNull(document);
        Assert.True(document.Get(key) is { IsArray: true });
    }

    [Theory]
    [InlineData("/v1/inspect/entries")]
    [InlineData("/v1/health")]
    [InlineData("/v1/inspect/boxes/")]
    public void APathThatIsNotAnInspectListHasNoFilesAnswer(string path) =>
        Assert.Null(AhdCtlVerb.InspectFiles(RealServices().Value, path, null, null));

    [Fact]
    public void ABoxesQueryCarriesTheAddress()
    {
        var client = new FakeDaemonClient { Reply = _ => Json(200, "{\"boxes\":[]}") };

        _ = Run(RealServices(), client, "inspect", "boxes", "--address", "a b@tm");

        Assert.Equal("/v1/inspect/boxes?address=a%20b%40tm", Assert.Single(client.Requests).PathAndQuery);
    }

    private string StatusDir(string name, string json = "{\"state\":\"ok\"}")
    {
        var dir = Path.Combine(_sandbox.Root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status.json"), json);
        return dir;
    }

    [Fact]
    public void WithNoDaemonTheStatusIsComputedInProcessOnlyForTheHierarchyDirTheCwdResolvesTo()
    {
        var known = StatusDir("hier");
        var other = StatusDir("elsewhere");

        var asked = Run(RealServices(), null, "inspect", "status", "--dir", known);
        var refused = Run(RealServices(), null, "inspect", "status", "--dir", other);

        Assert.Equal(0, asked.Code);
        Assert.Equal("files", asked.Body["source"].AsString());
        // files-off-disk F3a: the status.compute document; the left-over status.json is never read (the negative)
        Assert.Equal(1, (int)asked.Body["status"].AsObject()["schema"].AsNumber());
        Assert.Null(asked.Body["status"].AsObject().Get("state"));
        Assert.Equal(1, refused.Code);
        Assert.Equal("not_found", refused.Body["error"].AsObject()["code"].AsString());
    }

    [Fact]
    public void AnUnknownStatusDirIsRefusedTheSameWhetherTheDaemonIsUpOrDown()
    {
        var other = StatusDir("elsewhere");
        var up = new FakeDaemonClient { Reply = _ => Json(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"x\"}}") };

        var withDaemon = Run(RealServices(), up, "inspect", "status", "--dir", other);
        var withoutDaemon = Run(RealServices(), null, "inspect", "status", "--dir", other);

        Assert.Equal(1, withDaemon.Code);
        Assert.Equal(withoutDaemon.Out, withDaemon.Out);
    }

    [Fact]
    public void ADaemonErrorThatIsTheFinalWordForStatusIsReportedNotAnsweredFromTheFiles()
    {
        var known = StatusDir("hier");
        var client = new FakeDaemonClient { Reply = _ => Json(403, "{\"error\":{\"code\":\"not_admitted\",\"message\":\"no\"}}") };

        var o = Run(RealServices(), client, "inspect", "status", "--dir", known);

        Assert.Equal(1, o.Code);
        Assert.Equal("not_admitted", o.Body["error"].AsObject()["code"].AsString());
    }

    [Fact]
    public void ADaemonStatusAnswerIsPassedThroughWithItsRevision()
    {
        var known = StatusDir("hier");
        var client = new FakeDaemonClient { Reply = _ => Json(200, "{\"rev\":5,\"doc\":{\"state\":\"live\"}}") };

        var o = Run(RealServices(), client, "inspect", "status", "--dir", known);

        Assert.Equal("daemon", o.Body["source"].AsString());
        Assert.Equal(5, (int)o.Body["rev"].AsNumber());
        Assert.Equal("live", o.Body["status"].AsObject()["state"].AsString());
    }

    [Fact]
    public void APlainViewNeverPrintsAControlOrEscapeCharacterANonControlFieldIsUnchanged()
    {
        var hostile = "evil\u001b]52;c;AAAA\u0007\nforged  line";
        var body = "{\"registrations\":[{\"session_id\":\"s1\",\"address\":" + JsonText.Stringify(hostile, 0) + ",\"harness\":\"x\",\"live\":true,\"adapter\":\"none\",\"pid\":7}]}";
        var client = new FakeDaemonClient { Reply = _ => Json(200, body) };

        var o = Run(RealServices(), client, "inspect", "registrations", "--plain");

        Assert.Equal(0, o.Code);
        Assert.DoesNotContain('\u001b', o.Out);
        Assert.DoesNotContain('\u0007', o.Out);
        Assert.Single(o.Out.TrimEnd('\n').Split('\n'));
        Assert.StartsWith("s1  \"evil", o.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void APlainViewOfOrdinaryFieldsIsPrintedAsTheyAre()
    {
        var client = new FakeDaemonClient { Reply = _ => Json(200, "{\"registrations\":[{\"session_id\":\"s1\",\"address\":\"imp@tm\",\"harness\":\"claude\",\"live\":true,\"adapter\":\"none\",\"pid\":7}]}") };

        var o = Run(RealServices(), client, "inspect", "registrations", "--plain");

        Assert.Equal("s1  imp@tm  claude  pid 7  live  adapter none\n", o.Out);
    }

    [Fact]
    public void APlainErrorLineCarriesNoControlCharacterFromTheDaemonsWords()
    {
        var client = new FakeDaemonClient { Reply = _ => Json(403, "{\"error\":{\"code\":\"not_admitted\",\"message\":\"no\\u001b[31m red\"}}") };

        var o = Run(RealServices(), client, "inspect", "registrations", "--plain");

        Assert.Equal(1, o.Code);
        Assert.DoesNotContain('\u001b', o.Err);
        Assert.StartsWith("error: not_admitted: ", o.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void APlainRenderingOfAnEmptyRegistrationsViewIsNotJson()
    {
        var o = Run(RealServices(), null, "inspect", "registrations", "--plain");

        Assert.Equal(0, o.Code);
        Assert.DoesNotContain("\"ok\"", o.Out, StringComparison.Ordinal);
        Assert.NotEmpty(o.Out);
    }
}
