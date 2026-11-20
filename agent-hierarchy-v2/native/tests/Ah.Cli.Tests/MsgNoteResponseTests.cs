using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>Answering a note: <c>msg new --type response --note &lt;id&gt;</c> makes a response file for the note entry, and sending it records it on the note's thread.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgNoteResponseTests : IDisposable
{
    private const string NoteId = "20260601-000001-aaaa";

    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly string _hierarchy;
    private readonly string _teamFile;
    private readonly CliContext _context;
    private readonly ServiceGraph _graph;

    public MsgNoteResponseTests()
    {
        var pid = System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
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
        var registered = _graph.Writer.Invoke(
            "mailbox",
            "register",
            JsonText.ToUtf8(
                new JsonObject
                {
                    ["session_id"] = "s-i",
                    ["boxes"] = new JsonArray { new JsonObject { ["address"] = "tm-implementor@tm", ["dir"] = _graph.Mailbox.BoxDirFor(_teamFile, "tm-implementor") } },
                    ["team_file"] = _teamFile,
                    ["harness"] = "claude",
                    ["pid"] = long.Parse(pid, CultureInfo.InvariantCulture),
                    ["pid_start"] = FakeRunner.SelfStart,
                },
                0),
            new RequestContext(_sandbox.Root, _sandbox.Root, string.Empty, string.Empty));
        Assert.True(registered.Ok, registered.Error);
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

    /// <summary>A note from the Orchestrator, by default in the implementor's box.</summary>
    private void Note(string to = "tm-implementor@tm")
    {
        var sent = _graph.Mailbox.Send(new JsonObject
        {
            ["to"] = to,
            ["from"] = "orchestrator@tm",
            ["kind"] = "note",
            ["text"] = "need a ruling",
            ["id"] = NoteId,
            ["cwd"] = _sandbox.Root,
            ["hierarchy_dir"] = _hierarchy,
        });
        Assert.True(sent.Failure is null, sent.Failure?.Message);
    }

    private string Respond() => Path.Combine(MsgsDir, NoteId + "--orchestrator--note--response.md");

    private IReadOnlyList<JsonValue> BoxOf(string member) => _graph.Mailbox.ReadEntries(_graph.Mailbox.BoxDirFor(_teamFile, member)).Entries;

    [Fact]
    public void ABadTitleIsRefusedAndAGoodOneGoesThroughWithTheAccountAtItsLimit()
    {
        // one request in flight and the account at 99.5%: usage refuses nothing
        Assert.Equal(0, Run("new", "--to", "reviewer", "--from", "implementor", "--slug", "first", "--title", "Check the first change").Exit);
        var taps = Path.Combine(_sandbox.Home, ".claude", "ah2", "statusline");
        Directory.CreateDirectory(taps);
        File.WriteAllText(
            Path.Combine(taps, "s1.json"),
            string.Create(CultureInfo.InvariantCulture, $"{{\"session_id\":\"s1\",\"rate_limits\":{{\"seven_day\":{{\"used_percentage\":99.5,\"resets_at\":{DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()}}}}}}}"));

        var badTitle = Run("new", "--to", "reviewer", "--from", "implementor", "--slug", "second", "--title", "Fix x # y");
        var goodTitle = Run("new", "--to", "reviewer", "--from", "implementor", "--slug", "third", "--title", "Check the second change");

        Assert.NotEqual(0, badTitle.Exit);
        Assert.Contains("cannot contain \" #\"", badTitle.Err, StringComparison.Ordinal);
        Assert.Equal(0, goodTitle.Exit);
        Assert.DoesNotContain("usage", goodTitle.Err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANoteInTheCallersInboxGetsAResponseFileNamedForTheNoteAndItsSender()
    {
        Note();

        var (exit, stdout, stderr) = Run("new", "--type", "response", "--note", NoteId, "--from", "implementor");

        Assert.True(exit == 0, stderr);
        Assert.Equal(NoteId, JsonText.Parse(stdout).AsObject()["id"].AsString());
        var text = File.ReadAllText(Respond());
        foreach (var line in new[] { "id: " + NoteId, "type: response", "to: orchestrator", "from: implementor", "slug: note", "parent: null", "reason: null", "eta: null", "to_name: null" })
        {
            Assert.Contains(line + "\n", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnUnknownNoteOrOneInSomeoneElsesBoxIsRefusedAndNothingIsWritten()
    {
        Note(to: "orchestrator@tm");

        var unknown = Run("new", "--type", "response", "--note", "20260601-000002-bbbb", "--from", "implementor");
        var other = Run("new", "--type", "response", "--note", NoteId, "--from", "implementor");

        Assert.NotEqual(0, unknown.Exit);
        Assert.Contains("no note 20260601-000002-bbbb in your inbox", unknown.Err, StringComparison.Ordinal);
        Assert.NotEqual(0, other.Exit);
        Assert.Contains($"no note {NoteId} in your inbox", other.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(MsgsDir) && Directory.GetFiles(MsgsDir, "*--response.md").Length > 0);
    }

    [Fact]
    public void ASecondResponseForTheSameNoteIsRefusedAndTheFirstIsKept()
    {
        Note();
        Assert.Equal(0, Run("new", "--type", "response", "--note", NoteId, "--from", "implementor").Exit);
        File.AppendAllText(Respond(), "filled\n");

        var again = Run("new", "--type", "response", "--note", NoteId, "--from", "implementor");

        Assert.NotEqual(0, again.Exit);
        Assert.Contains("id already used by", again.Err, StringComparison.Ordinal);
        Assert.EndsWith("filled\n", File.ReadAllText(Respond()), StringComparison.Ordinal);
    }

    /// <summary>ah2-317 E1: a note's --ref may be a stored request's id, with --text beside it; a missing file is still refused.</summary>
    [Fact]
    public void ANoteReferencesAStoredRequestByIdButNotAMissingFile()
    {
        var made = Run("new", "--to", "reviewer", "--from", "implementor", "--slug", "asked", "--title", "Check the change");
        Assert.True(made.Exit == 0, made.Err);
        var requestId = JsonText.Parse(made.Out).AsObject()["id"].AsString();

        var sent = Run("send", "orchestrator@tm", "--kind", "note", "--ref", requestId, "--text", "see the request");
        var missing = Run("send", "orchestrator@tm", "--kind", "note", "--ref", Path.Combine(_sandbox.Root, "nope.md"));

        Assert.True(sent.Exit == 0, sent.Err);
        Assert.Contains(BoxOf("orchestrator"), static e => e.Member("text")?.AsString() == "see the request" && e.Member("ref") is { IsText: true });
        Assert.NotEqual(0, missing.Exit);
        Assert.Contains("is not an existing file", missing.Err, StringComparison.Ordinal);
    }

    /// <summary>ah2-317 E3: a title over the limit is refused before --body-file is read; one at the limit goes on to read it (here: a missing file).</summary>
    [Theory]
    [InlineData(61, "title-too-long")]
    [InlineData(60, "--body-file: cannot read")]
    public void ATitleIsCheckedBeforeTheBodyIsRead(int length, string error)
    {
        var title = "Check " + new string('x', length - 6);

        var made = Run("new", "--to", "reviewer", "--from", "implementor", "--slug", "long", "--title", title, "--body-file", Path.Combine(_sandbox.Root, "absent.md"));

        Assert.NotEqual(0, made.Exit);
        Assert.Contains(error, made.Err, StringComparison.Ordinal);
    }

    /// <summary>
    /// ah2-317 E3: an empty --ref (an id a failed pipeline did not capture) says so where the send would fail with the three-way error; a non-empty
    /// one does not, and a note with an empty --ref and --text still sends its text.
    /// </summary>
    /// A null <paramref name="reference"/> is a bare --ref (an unquoted empty $ID); "" is a quoted one.
    [Theory]
    [InlineData("report", null, null, 1, true)]
    [InlineData("brief", "", "x", 1, true)]
    [InlineData("report", "20260601-000099-zzzz", null, 1, false)]
    [InlineData("note", "", "just the text", 0, false)]
    public void AnEmptyRefSaysTheIdIsEmpty(string kind, string? reference, string? text, int exit, bool empty)
    {
        string[] args = ["send", "orchestrator@tm", "--ref", .. reference is null ? Array.Empty<string>() : [reference], "--kind", kind, .. text is null ? Array.Empty<string>() : ["--text", text]];

        var sent = Run(args);

        Assert.Equal(exit == 0, sent.Exit == 0);
        Assert.Equal(empty, (sent.Out + sent.Err).Contains("the id is empty", StringComparison.Ordinal));
        if (exit == 0)
        {
            Assert.Contains(BoxOf("orchestrator"), e => e.Member("text")?.AsString() == text && e.Member("ref") is null);
        }
    }

    [Theory]
    [InlineData("--id", "20260601-000003-cccc")]
    [InlineData("--req", "/tmp/x--request.md")]
    public void NoteDoesNotMixWithIdOrReq(string flag, string value)
    {
        Note();

        var (exit, _, stderr) = Run("new", "--type", "response", "--note", NoteId, "--from", "implementor", flag, value);

        Assert.NotEqual(0, exit);
        Assert.Contains("--note applies only to --type response, and replaces --req and --id", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASentReportForTheNoteCarriesReplyToAndAReportForAnythingElseDoesNot()
    {
        Note();
        Assert.Equal(0, Run("new", "--type", "response", "--note", NoteId, "--from", "implementor").Exit);
        File.AppendAllText(Respond(), "ruling: yes\n");
        var plain = Path.Combine(MsgsDir, "20260601-000009-bbbb--orchestrator--work--response.md");
        File.WriteAllText(plain, "---\nid: 20260601-000009-bbbb\ntype: response\n---\n\nDONE\n");

        var answered = Run("send", "orchestrator@tm", "--kind", "report", "--ref", Respond(), "--id", "20260601-000010-dddd");
        var unrelated = Run("send", "orchestrator@tm", "--kind", "report", "--ref", plain, "--id", "20260601-000011-eeee");

        Assert.True(answered.Exit == 0, answered.Err);
        Assert.True(unrelated.Exit == 0, unrelated.Err);
        var entries = BoxOf("orchestrator");
        var reply = entries.Single(e => e.Member("id")!.Value.AsString() == "20260601-000010-dddd");
        var other = entries.Single(e => e.Member("id")!.Value.AsString() == "20260601-000011-eeee");
        Assert.Equal(NoteId, reply.Member("reply_to")!.Value.AsString());
        Assert.Null(other.Member("reply_to"));
    }
}
