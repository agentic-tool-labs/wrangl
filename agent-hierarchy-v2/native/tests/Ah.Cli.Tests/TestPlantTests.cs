using System.Runtime.Versioning;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Db;
using Ah.Core.Json;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>Each plant helper's write reads back through the seam a product read uses, and the verb is refused outside test mode.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class TestPlantTests : IDisposable
{
    private const string Doc = """{"version":1,"team_id":"t-a","orchestrator":{"session_id":null,"pid":null},"members":[]}""";

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Hier => Path.Combine(_sandbox.Root, "proj", ".claude", "ah2");

    private string TeamFile => Path.Combine(Hier, "teams", "a.json");

    private ServiceGraph Graph(bool testMode = true)
    {
        var variables = new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = _sandbox.AhHome, ["AH_DAEMON"] = "off" };
        if (testMode)
        {
            variables["AH_TEST"] = "1";
        }

        return ServiceGraph.Build(PlatformModule.CreateIo(null), new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables), new FakeRunner(), new FakeDaemonClient());
    }

    /// <summary>Creates the database, so the next plant lands in it.</summary>
    private void CutOver()
    {
        _ = Directory.CreateDirectory(Path.Combine(Hier, "teams"));
        using var db = StateDbModule.Open(_sandbox.AhHome, mayCreate: true);
    }

    private (int Code, string Out, string Err) Seed(ServiceGraph graph, bool testMode, string stdin, params string[] args)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = testMode ? "1" : null };
        var context = new CliContext(new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables), null!, new FakeRunner(), "ah", "ah", _sandbox.Home, 501, () => System.Text.Encoding.UTF8.GetBytes(stdin), new Lazy<ServiceGraph>(() => graph));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = DbVerb.Run(context, ["test-seed", .. args], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void TeamPlantOnAHomeWithNoDatabaseCreatesItAndWritesNoFile()
    {
        var graph = Graph();
        TestPlant.Team(graph.Roster, graph.Writer, TeamFile, Doc);
        Assert.False(graph.Roster.ReadTeamWithDigest(TeamFile)["doc"].IsNull);
        Assert.Equal("t-a", graph.Roster.ReadTeamWithDigest(TeamFile)["doc"].Member("team_id")!.Value.AsString());
        Assert.False(File.Exists(TeamFile));
    }

    [Fact]
    public void NegativeATeamSeedOverAPlantedFileIsStored()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(TeamFile)!);
        File.WriteAllText(TeamFile, Doc);
        var graph = Graph();

        Assert.Equal(0, Seed(graph, testMode: true, Doc, "teams", "--path", TeamFile).Code);

        Assert.Equal("t-a", graph.Roster.ReadTeamWithDigest(TeamFile)["doc"].Member("team_id")!.Value.AsString());
    }

    [Fact]
    public void APeerRowSeedReadsBackThroughThePeerLog()
    {
        var graph = Graph();

        Assert.Equal(0, Seed(graph, testMode: true, """{"type":"dispatch","session_id":"s-1","request_id":"r1"}""", "peer-rows").Code);

        Assert.Equal("r1", Assert.Single(graph.Hierarchy.Core.ReadPeerRecordsKnown()!).Member("request_id")!.Value.AsString());
    }

    [Fact]
    public void NegativeAPeerRowSeedWithAnArgumentIsAUsageErrorAndStoresNothing()
    {
        var graph = Graph();

        var (code, _, err) = Seed(graph, testMode: true, """{"type":"dispatch","session_id":"s-1"}""", "peer-rows", "--dir", _sandbox.Root);

        Assert.Equal(2, code);
        Assert.Contains("usage:", err, StringComparison.Ordinal);
        Assert.Empty(graph.Hierarchy.Core.ReadPeerRecordsKnown() ?? []);
    }

    [Fact]
    public void TeamPlantReadsBackFromTheRowOnceTeamsAreOnTheDatabase()
    {
        CutOver();
        var graph = Graph();
        TestPlant.Team(graph.Roster, graph.Writer, TeamFile, Doc);

        File.Delete(TeamFile);
        Assert.Equal("t-a", graph.Roster.ReadTeamWithDigest(TeamFile)["doc"].Member("team_id")!.Value.AsString());
    }

    [Fact]
    public void RegistrationPlantReadsBackThroughTheMailbox()
    {
        var graph = Graph();
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        TestPlant.Registration(graph.Writer, "s-1", TeamFile, _sandbox.Root, ("m@a", box));
        var read = graph.Mailbox.ReadRegistration("s-1");
        Assert.NotNull(read);
        Assert.Equal(box, graph.Mailbox.RegistrationBoxes(read!).Single().Dir);
    }

    [Fact]
    public void RegistrationPlantReadsBackOnceSessionsAreOnTheDatabase()
    {
        CutOver();
        var graph = Graph();
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        TestPlant.Registration(graph.Writer, "s-1", TeamFile, _sandbox.Root, ("m@a", box));
        Assert.NotNull(graph.Mailbox.ReadRegistration("s-1"));
    }

    [Fact]
    public void AnInvalidRegistrationIsRefusedNotPlanted()
    {
        var graph = Graph();
        Assert.Throws<InvalidOperationException>(() => TestPlant.Registration(graph.Writer, "not a name!", TeamFile, _sandbox.Root, ("m@a", "/nope")));
    }

    private Ah.Abstractions.StateResult AppendMail(ServiceGraph graph, string box) =>
        graph.Writer.Invoke("mailbox", "append", JsonText.ToUtf8(new JsonObject { ["dir"] = box, ["entry"] = new JsonObject { ["id"] = "e1", ["from"] = "o@a", ["to"] = "m@a", ["kind"] = "note", ["text"] = "hi" } }, 0), new Ah.Abstractions.RequestContext(_sandbox.Root, _sandbox.Root, string.Empty, string.Empty));

    private void SetMode(string domain, string mode)
    {
        Assert.Null(StateDbModule.EnsureDatabase(_sandbox.AhHome));
        var set = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES (?1, ?2)", "mode." + domain, mode);
            return 0;
        });
        Assert.True(set.Ok, set.Message);
    }

    [Fact]
    public void AMailWriteOnAHomeStillOnFilesIsRefusedAndWritesNoFile()
    {
        var graph = Graph();
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        SetMode("mail", "files");

        var result = AppendMail(graph, box);

        Assert.False(result.Ok);
        Assert.Equal("store_not_imported", result.ErrorCode);
        Assert.Contains("ah db list", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(box, "entries.jsonl")));
    }

    [Fact]
    public void NegativeAMailWriteWithAnExplicitDbModeIsStored()
    {
        var graph = Graph();
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        SetMode("mail", "db");

        Assert.True(AppendMail(graph, box).Ok);
        Assert.Equal("e1", graph.Mailbox.ReadEntries(box).Entries.Single().Member("id")!.Value.AsString());
    }

    [Fact]
    public void NegativeAFilesRowForMessagesIsNotRefusedAsAFilesStoreThisBuildStoppedWriting()
    {
        var graph = Graph();
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        SetMode("msgs", "files");

        Assert.True(AppendMail(graph, box).Ok);
    }

    [Fact]
    public void APlantedTeamSessionAndMailLeaveNoLegacyPath()
    {
        var graph = Graph();
        TestPlant.Team(graph.Roster, graph.Writer, TeamFile, Doc);
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        TestPlant.Registration(graph.Writer, "s1", TeamFile, _sandbox.Root, ("m@a", box));
        Assert.True(AppendMail(graph, box).Ok);

        Assert.False(File.Exists(TeamFile));
        Assert.False(Directory.Exists(Path.Combine(_sandbox.AhHome, "registrations")));
        Assert.False(Directory.Exists(Path.Combine(_sandbox.AhHome, "mailbox")));
    }

    [Fact]
    public void MailPlantReadsBackThroughTheMailbox()
    {
        var graph = Graph();
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        TestPlant.Mail(graph.Writer, box, new JsonObject { ["id"] = "e1", ["from"] = "o@a", ["to"] = "m@a", ["kind"] = "note", ["text"] = "hi" }, _sandbox.Root);
        Assert.Equal("e1", graph.Mailbox.ReadEntries(box).Entries.Single().Member("id")!.Value.AsString());
    }

    [Fact]
    public void TheVerbPlantsEachKindInTestMode()
    {
        var graph = Graph();
        var box = graph.Mailbox.BoxDirFor(TeamFile, "m");
        Assert.Equal(0, Seed(graph, true, Doc, "teams", "--path", TeamFile).Code);
        Assert.Equal("t-a", graph.Roster.ReadTeamWithDigest(TeamFile)["doc"].Member("team_id")!.Value.AsString());

        var register = JsonText.Stringify(new JsonObject { ["session_id"] = "s-2", ["boxes"] = new JsonArray { new JsonObject { ["address"] = "m@a", ["dir"] = box } }, ["team_file"] = TeamFile }, 0);
        Assert.Equal(0, Seed(graph, true, register, "sessions").Code);
        Assert.NotNull(graph.Mailbox.ReadRegistration("s-2"));

        Assert.Equal(0, Seed(graph, true, """{"id":"e2","from":"o@a","to":"m@a","kind":"note","text":"hi"}""", "mail", "--dir", box).Code);
        Assert.Equal("e2", graph.Mailbox.ReadEntries(box).Entries.Single().Member("id")!.Value.AsString());
    }

    [Fact]
    public void TheVerbIsRefusedWithoutTestModeAndWritesNothing()
    {
        var graph = Graph();
        var (code, _, err) = Seed(graph, false, Doc, "teams", "--path", TeamFile);
        Assert.Equal(2, code);
        Assert.Contains("test mode only", err, StringComparison.Ordinal);
        Assert.False(File.Exists(TeamFile));
    }

    [Fact]
    public void TheVerbRefusesAnUnknownKind()
    {
        Assert.Equal(2, Seed(Graph(), true, Doc, "peers").Code);
    }

    [Fact]
    public void TheVerbRefusesInputThatIsNotJson()
    {
        var graph = Graph();
        Assert.Equal(2, Seed(graph, true, "not json", "teams", "--path", TeamFile).Code);
        Assert.False(File.Exists(TeamFile));
    }
}
