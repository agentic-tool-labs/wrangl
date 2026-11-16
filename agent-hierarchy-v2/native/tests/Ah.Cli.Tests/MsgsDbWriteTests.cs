using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The msgs write ops in db mode store the rows and write the files that files mode writes, through the production composition and a temp home only.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgsDbWriteTests : IDisposable
{
    private const string Id = "20261028-153559-abcd";
    private readonly List<Rig> _rigs = [];

    public void Dispose()
    {
        foreach (var rig in _rigs)
        {
            rig.Sandbox.Dispose();
        }
    }

    private sealed class Rig
    {
        internal Rig(bool database, bool db)
        {
            Sandbox = new Sandbox();
            Dir = Path.Combine(Sandbox.Root, "work", ".claude", "ah2");
            if (database)
            {
                using var open = StateDbModule.Open(Sandbox.AhHome, mayCreate: true);
                Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
                SetMode(db ? "db" : "files"); // files mode only by an explicit row (ah-msgs-db-only §13)
            }

            var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = Sandbox.AhHome, ["HOME"] = Sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = Dir };
            Env = new FakeEnv(Sandbox.Home, Sandbox.AhHome, variables, new PluginInfo(Sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
            Graph = ServiceGraph.Build(PlatformModule.CreateIo(null), Env, FakeRunner.KnowingSelf(), new FakeDaemonClient());
        }

        internal FakeEnv Env { get; }

        /// <summary>Makes this process a live registered session owning <c>orchestrator@tm</c>, so <c>--from orchestrator</c> is its own.</summary>
        internal void ProveOrchestrator()
        {
            var teamFile = Path.Combine(Dir, "teams", "tm.json");
            TestPlant.Team(Graph.Roster, Graph.Writer, teamFile, "{\"version\":1,\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":1,\"session_id\":\"s-o\"},\"members\":[]}");
            Assert.True(Graph.Mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite("s-o", [new Ah.Mesh.BoxRef("orchestrator@tm", Graph.Mailbox.BoxDirFor(teamFile, "orchestrator"))], teamFile, "claude", Environment.ProcessId, PidStart: FakeRunner.SelfStart)));
        }

        /// <summary>One <c>ah msg</c> run from the work tree, as the CLI composes it.</summary>
        internal (int Code, string Out, string Err) Cli(params string[] args)
        {
            var context = new CliContext(Env, new RecordingExec(), new FakeRunner(), "ahd", Sandbox.Executable("install/ahd"), Sandbox.Home, 501, null, new Lazy<ServiceGraph>(Graph));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = MsgVerb.Run(context, [.. args, "--pool", Dir, "--session", "s-o", "--cwd", Path.Combine(Sandbox.Root, "work")], stdout, stderr);
            return (code, stdout.ToString(), stderr.ToString());
        }

        /// <summary>One <c>ah msg new</c>, which takes its pool from the environment and not from <c>--pool</c>.</summary>
        internal (int Code, string Out, string Err) New(params string[] args)
        {
            var context = new CliContext(Env, new RecordingExec(), new FakeRunner(), "ahd", Sandbox.Executable("install/ahd"), Sandbox.Home, 501, null, new Lazy<ServiceGraph>(Graph));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = MsgVerb.Run(context, ["new", .. args, "--session", "s-o", "--cwd", Path.Combine(Sandbox.Root, "work")], stdout, stderr);
            return (code, stdout.ToString(), stderr.ToString());
        }

        /// <summary>The same run through the CLI's entry, where the verbs' failures become exit codes.</summary>
        internal (int Code, string Out, string Err) App(params string[] args)
        {
            var context = new CliContext(Env, new RecordingExec(), new FakeRunner(), "ah", Sandbox.Executable("install/ahd"), Sandbox.Home, 501, null, new Lazy<ServiceGraph>(Graph));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = CliApp.Run(["msg", .. args, "--pool", Dir, "--session", "s-o", "--cwd", Path.Combine(Sandbox.Root, "work")], context, stdout, stderr);
            return (code, stdout.ToString(), stderr.ToString());
        }

        internal Sandbox Sandbox { get; }

        internal string Dir { get; }

        internal ServiceGraph Graph { get; }

        internal string Msgs => Path.Combine(Dir, "msgs");

        internal void SetMode(string mode) =>
            Assert.True(StateDbModule.OpenDirect(Sandbox.AhHome).Write(tx =>
            {
                tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.msgs', ?1)", mode);
                return 0;
            }).Ok);

        internal StateResult Op(string op, JsonObject args)
        {
            args["dir"] = Dir;
            return Graph.Writer.Invoke("msgs", op, JsonText.ToUtf8(args, 0), Graph.Hierarchy.Messages.PoolContext(Dir), new StateCallOptions(OperationId: Guid.NewGuid().ToString("N")));
        }

        internal StateResult Create(string name, string content) => Op("create", new JsonObject { ["path"] = Path.Combine(Msgs, name), ["content"] = content });

        internal IReadOnlyList<object?[]> Q(string sql, params object?[] args)
        {
            using var reader = StateDbModule.OpenReader(Sandbox.AhHome).Reader!;
            return reader.Query(sql, args);
        }

        internal void Exec(string sql) => Assert.True(StateDbModule.OpenDirect(Sandbox.AhHome).Write(tx =>
        {
            tx.Execute(sql);
            return true;
        }).Ok);

        /// <summary>The message's text: its stored body, or the file in files mode.</summary>
        internal string Read(string name) => Graph.Hierarchy.Core.StoredBytes(Path.Combine(Msgs, name)) is { } stored
            ? System.Text.Encoding.UTF8.GetString(stored)
            : File.ReadAllText(Path.Combine(Msgs, name));
    }

    private Rig NewRig(bool database = true, bool db = true)
    {
        var rig = new Rig(database, db);
        _rigs.Add(rig);
        return rig;
    }

    private static string RequestName => Id + "--orchestrator--s--request.md";

    private static string ResponseName => Id + "--impl--s--response.md";

    private static string Request(string extra = "") =>
        $"---\nid: {Id}\ntype: request\nto: orchestrator\nfrom: impl\nslug: s\ncreated: 2026-10-28T15:35:59-04:00\n{extra}---\n\n## [0] goal\n- [1] goal: x\n";

    private static string Response(string extra = "") =>
        $"---\nid: {Id}\ntype: response\nto: impl\nfrom: orchestrator\nslug: s\ncreated: 2026-10-28T15:36:00-04:00\n{extra}---\n\n## [0] tldr\n- [1] status:\n";

    private static string Closed => "closed: true\nclosed_by: orchestrator\nclosed_reason: done\n";

    private static void Exchange(Rig rig)
    {
        Assert.True(rig.Create(RequestName, Request()).Ok);
        Assert.True(rig.Create(ResponseName, Response()).Ok);
    }

    private static JsonObject FillArgs(Rig rig) => new()
    {
        ["path"] = Path.Combine(rig.Msgs, ResponseName),
        ["req"] = Path.Combine(rig.Msgs, RequestName),
        ["id"] = Id,
        ["from"] = "orchestrator",
        ["body"] = "\"all good\"",
    };

    private static string[] Changed(Rig rig, long since) =>
        [.. JsonText.Parse(Utf8Text.Decode(rig.Op("changed", new JsonObject { ["since"] = since }).ResultJson.Span)).Member("ids")!.Value.AsArray().Select(static i => i.AsString())];

    private static long Version(Rig rig) =>
        (long)JsonText.Parse(Utf8Text.Decode(rig.Op("version", new JsonObject()).ResultJson.Span)).Member("version")!.Value.AsNumber();

    [Fact]
    public void TheChangedReadNamesWhatAWriteChangedAndTheCounterCoversIt()
    {
        var rig = NewRig();
        Assert.Equal(0, Version(rig));
        Exchange(rig);
        var afterCreate = Version(rig);
        Assert.True(afterCreate > 0);
        Assert.Equal([Id], Changed(rig, 0));

        Assert.True(rig.Op("fill", FillArgs(rig)).Ok);

        Assert.True(Version(rig) > afterCreate);
        Assert.Equal([Id], Changed(rig, afterCreate));
    }

    [Fact]
    public void NegativeTheChangedReadLeavesOutAMessageNoLaterWriteTouched()
    {
        var rig = NewRig();
        Exchange(rig);
        var afterFirst = Version(rig);
        const string Second = "20261028-153600-efgh";

        Assert.True(rig.Create(Second + "--orchestrator--t--request.md", Request().Replace(Id, Second, StringComparison.Ordinal).Replace("slug: s", "slug: t", StringComparison.Ordinal)).Ok);

        Assert.Equal([Second], Changed(rig, afterFirst));
        Assert.Empty(Changed(rig, Version(rig)));
    }

    [Fact]
    public void AReportsDeliveredDigestAndTheViewsChangedHashAgreeWhileItsExportIsStale()
    {
        var rig = NewRig();
        Exchange(rig);
        Assert.True(rig.Op("fill", FillArgs(rig)).Ok);
        var response = Path.Combine(rig.Msgs, ResponseName);
        var stored = rig.Graph.Hierarchy.Core.StoredBytes(response)!;
        var team = Path.Combine(rig.Dir, "teams", "tm.json");
        TestPlant.Team(rig.Graph.Roster, rig.Graph.Writer, team, "{\"version\":1,\"team_id\":\"t-tm\",\"members\":[{\"name\":\"impl\",\"role\":\"implementor\",\"kind\":\"claude\"},{\"name\":\"rev\",\"role\":\"reviewer\",\"kind\":\"claude\"}],\"orchestrator\":{\"pid\":1,\"session_id\":\"o1\"}}");
        var sent = rig.Graph.Mailbox.Send(new JsonObject { ["to"] = "rev@tm", ["from"] = "impl@tm", ["kind"] = "report", ["ref"] = response, ["id"] = "r1", ["cwd"] = rig.Dir, ["hierarchy_dir"] = rig.Dir });
        Assert.NotNull(sent.Value);
        Directory.CreateDirectory(rig.Msgs);
        File.WriteAllText(response, Response()); // a stray file at the key is ignored
        var source = Ah.Watch.WatchModule.CreateViewSource(new Ah.Watch.ViewSourceServices(
            rig.Graph.Io.Files, new Ah.Hierarchy.StreamDerivations(), new DaemonServices.ViewPlacement(rig.Graph),
            State: new StateServiceReader(rig.Graph.InternalStateRead), Layout: new MailboxLayout(rig.Graph.Mailbox, rig.Graph.Environment), Config: new ViewConfig(rig.Graph.Config),
            StoredBytes: rig.Graph.Hierarchy.Core.StoredBytes));

        var box = rig.Graph.Mailbox.BoxDirFor(team, "rev");
        var delivered = Assert.Single(rig.Graph.Mailbox.ReadEntries(box).Entries);
        var row = Assert.Single(source.BoxEntries(box, 10));

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored)), delivered.Member("ref_sha256")!.Value.AsString());
        Assert.False(row["changed"].AsBool());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheViewHashesADeliveredReportWithNoMsgsDirectory(bool forgotten)
    {
        var rig = NewRig();
        Exchange(rig);
        Assert.True(rig.Op("fill", FillArgs(rig)).Ok);
        var response = Path.Combine(rig.Msgs, ResponseName);
        var team = Path.Combine(rig.Dir, "teams", "tm.json");
        TestPlant.Team(rig.Graph.Roster, rig.Graph.Writer, team, "{\"version\":1,\"team_id\":\"t-tm\",\"members\":[{\"name\":\"impl\",\"role\":\"implementor\",\"kind\":\"claude\"},{\"name\":\"rev\",\"role\":\"reviewer\",\"kind\":\"claude\"}],\"orchestrator\":{\"pid\":1}}");
        Assert.NotNull(rig.Graph.Mailbox.Send(new JsonObject { ["to"] = "rev@tm", ["from"] = "impl@tm", ["kind"] = "report", ["ref"] = response, ["id"] = "r1", ["cwd"] = rig.Dir, ["hierarchy_dir"] = rig.Dir }).Value);
        Assert.False(Directory.Exists(rig.Msgs));
        if (forgotten)
        {
            Directory.CreateDirectory(rig.Msgs);
            File.WriteAllBytes(response, rig.Graph.Hierarchy.Core.StoredBytes(response)!); // a stray file with the delivered bytes answers nothing
        }

        var source = Ah.Watch.WatchModule.CreateViewSource(new Ah.Watch.ViewSourceServices(
            rig.Graph.Io.Files, new Ah.Hierarchy.StreamDerivations(), new DaemonServices.ViewPlacement(rig.Graph),
            State: new StateServiceReader(rig.Graph.InternalStateRead), Layout: new MailboxLayout(rig.Graph.Mailbox, rig.Graph.Environment), Config: new ViewConfig(rig.Graph.Config),
            StoredBytes: forgotten ? _ => null : rig.Graph.Hierarchy.Core.StoredBytes)
        {
        });

        var row = Assert.Single(source.BoxEntries(rig.Graph.Mailbox.BoxDirFor(team, "rev"), 10));

        Assert.Equal(forgotten, row["changed"].AsBool()); // negative: a row the store no longer holds does not read as unchanged
        Assert.True(source.IsMessageFile(response));
        Assert.False(source.IsMessageFile(Path.Combine(rig.Dir, "notes", ResponseName))); // negative
    }

    /// <summary>H1 ruling B through the real engine and state client: the AfterWrite clears exactly the mark the body left, so a completed write leaves none.</summary>
    [Fact]
    public void ACompletedDatabaseWriteLeavesNoStatusDirtyMark()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);

        Assert.Empty(rig.Q("SELECT key FROM meta WHERE key LIKE 'status.dirty:%'"));
    }

    [Fact]
    public void ACreateStoresADraftRowWithItsBodyAndNoFileAndASecondCreateOfTheSameKeyFails()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);

        Assert.Equal(Request(), rig.Read(RequestName));
        Assert.False(File.Exists(Path.Combine(rig.Msgs, RequestName)));
        var row = Assert.Single(rig.Q("SELECT state, draft_path, body_rev, export_sha FROM msg_messages"));
        // the rig lives under /tmp: the row holds the key spelling (/private/tmp on macOS), not the spelling the op was given
        Assert.Equal(["draft", MsgRows.PathKey(Path.Combine(rig.Msgs, RequestName)), 1L, null], row);
        Assert.Equal("open", Assert.Single(rig.Q("SELECT state FROM msg_exchanges"))[0]);
        Assert.False(rig.Create(RequestName, Request("parent: p\n")).Ok);
        Assert.Single(rig.Q("SELECT pk FROM msg_messages"));
        Assert.True(rig.Create(Id + "--orchestrator--t--request.md", Request().Replace("slug: s", "slug: t", StringComparison.Ordinal)).Ok);
        Assert.Equal(2, rig.Q("SELECT pk FROM msg_messages").Count);
    }

    [Fact]
    public void APoolOutsideAnyRepositoryClaimsItselfAsItsCwdAndNegativeARepositoryPoolKeepsItsRoot()
    {
        var rig = NewRig();
        var user = Path.Combine(rig.Sandbox.Home, ".claude", "ah2", "hierarchy", "scratch");
        Directory.CreateDirectory(user);
        Assert.Equal(user, rig.Graph.Hierarchy.Messages.PoolContext(user).Cwd);

        var repo = Path.Combine(rig.Sandbox.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var inRepo = Path.Combine(repo, ".claude", "ah2", "hierarchy", "scratch");
        Directory.CreateDirectory(inRepo);
        Assert.NotEqual(inRepo, rig.Graph.Hierarchy.Messages.PoolContext(inRepo).Cwd);
        Assert.Equal(repo, rig.Graph.Hierarchy.Messages.PoolContext(Path.Combine(repo, ".claude", "ah2")).Cwd);
    }

    [Fact]
    public void ANoteWrittenThroughASymlinkedWorktreeIsFoundThroughTheRealSpellingOfItsPoolAndItsPath()
    {
        var rig = NewRig();
        Directory.CreateDirectory(rig.Msgs);
        var link = Path.Combine(rig.Sandbox.Root, "linked-work");
        Directory.CreateSymbolicLink(link, Path.Combine(rig.Sandbox.Root, "work"));
        var linkedDir = Path.Combine(link, ".claude", "ah2");
        var name = Id + "--orchestrator--n--note.md";
        var note = Request().Replace("type: request", "type: note", StringComparison.Ordinal);
        var args = new JsonObject { ["dir"] = linkedDir, ["path"] = Path.Combine(linkedDir, "msgs", name), ["content"] = note };
        var created = rig.Graph.Writer.Invoke("msgs", "create", JsonText.ToUtf8(args, 0), rig.Graph.Hierarchy.Messages.PoolContext(linkedDir), new StateCallOptions(OperationId: Guid.NewGuid().ToString("N")));
        Assert.True(created.Ok, created.Error);
        var real = PathGuard.RealPath(rig.Dir, strict: true)!;
        File.Delete(Path.Combine(real, "msgs", name));

        Assert.Single(rig.Graph.Hierarchy.Messages.ListMessages(real, includeArchive: false, ["note"]));
        Assert.Equal(note, rig.Graph.Hierarchy.Core.ReadMsgFile(Path.Combine(real, "msgs", name)).Member("text")?.AsString());
        Assert.Equal(note, rig.Graph.Hierarchy.Core.ReadMsgFile(Path.Combine(linkedDir, "msgs", name)).Member("text")?.AsString());

        Assert.True(rig.Create(RequestName, Request()).Ok);
        var linkedRequest = Path.Combine(linkedDir, "msgs", RequestName);

        // the archive takes a file named in the linked spelling while the op's pool is the real one
        var archived = rig.Graph.Writer.Invoke(
            "msgs", "archive", JsonText.ToUtf8(new JsonObject { ["dir"] = real, ["files"] = new JsonArray { JsonValue.FromString(linkedRequest) } }, 0),
            rig.Graph.Hierarchy.Messages.PoolContext(real), new StateCallOptions(OperationId: Guid.NewGuid().ToString("N")));
        Assert.True(archived.Ok, archived.Error);
        Assert.Equal("archived", Assert.Single(rig.Q("SELECT state FROM msg_messages WHERE type = 'request'"))[0]);
    }

    private static (bool Existing, string? Id) Held(StateResult result)
    {
        Assert.True(result.Ok, result.Error);
        var value = JsonText.Parse(System.Text.Encoding.UTF8.GetString(result.ResultJson.Span));
        return (value.Member("existing") is { IsTruthy: true }, value.Member("id") is { IsText: true } id ? id.AsString() : null);
    }

    private static StateResult DedupedCreate(Rig rig, string id, string created, string slug = "s") =>
        rig.Op("create", new JsonObject
        {
            ["path"] = Path.Combine(rig.Msgs, id + "--orchestrator--" + slug + "--request.md"),
            ["content"] = Request().Replace(Id, id, StringComparison.Ordinal).Replace("2026-10-28T15:35:59-04:00", created, StringComparison.Ordinal)
                .Replace("slug: s\n", "slug: " + slug + "\n", StringComparison.Ordinal),
            ["dedupe"] = true,
        });

    /// <summary>
    /// The CLI's retry check (a create with no <c>--id</c>, as <c>msg new</c> plans it): the same request again is the held one; the
    /// negative, another slug, is new. In db mode it reads the indexed range: a held request whose exchange opened before the window is not
    /// a match even though its header says now.
    /// </summary>
    [Fact]
    public void APlannedRetryFindsTheSameRequestByTheIndexedRangeAndAnotherSlugIsNew()
    {
        var rig = NewRig();
        JsonObject Plan(string slug) => new() { ["type"] = "request", ["to"] = "implementor", ["from"] = "orchestrator", ["slug"] = slug };
        (bool Existing, string Id) Made(JsonValue v) => (v.Member("existing") is { IsTruthy: true }, v.Member("id")!.Value.AsString());

        var first = Made(rig.Graph.Hierarchy.Messages.CreateMessage(rig.Dir, Plan("w")));
        Assert.False(first.Existing);
        Assert.Equal((true, first.Id), Made(rig.Graph.Hierarchy.Messages.CreateMessage(rig.Dir, Plan("w"))));
        Assert.False(Made(rig.Graph.Hierarchy.Messages.CreateMessage(rig.Dir, Plan("w2"))).Existing);

        // no opened_ms (a created header the store could not read): still a candidate, and the created check here holds it
        rig.Exec($"UPDATE msg_exchanges SET opened_ms = NULL WHERE id = '{first.Id}'");
        Assert.Equal((true, first.Id), Made(rig.Graph.Hierarchy.Messages.CreateMessage(rig.Dir, Plan("w"))));

        rig.Exec($"UPDATE msg_exchanges SET opened_ms = {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} WHERE id = '{first.Id}'");
        rig.Exec($"UPDATE msg_exchanges SET opened_ms = opened_ms - {11 * 60_000} WHERE id = '{first.Id}'");
        Assert.False(Made(rig.Graph.Hierarchy.Messages.CreateMessage(rig.Dir, Plan("w"))).Existing);
    }

    /// <summary>
    /// The db-mode retry check reads only the requests to the same recipient and slug opened within the retry window: one 9:59 later is
    /// the held request; the negatives, one 10:01 later and one of another slug, are new requests.
    /// </summary>
    [Fact]
    public void ADedupedCreateInDbModeFindsOnlyTheSameSlugsRequestWithinTheRetryWindow()
    {
        var rig = NewRig();
        Assert.False(Held(DedupedCreate(rig, "20261028-153600-efgh", "2026-10-28T15:36:00-04:00")).Existing);

        Assert.Equal((true, "20261028-153600-efgh"), Held(DedupedCreate(rig, "20261028-154559-ijkl", "2026-10-28T15:45:59-04:00")));
        Assert.False(Held(DedupedCreate(rig, "20261028-154601-mnop", "2026-10-28T15:46:01-04:00")).Existing);
        Assert.False(Held(DedupedCreate(rig, "20261028-153601-qrst", "2026-10-28T15:36:01-04:00", slug: "other")).Existing);
    }

    [Fact]
    public void ADedupedCreateInDbModeFindsTheHeldRequestByItsRowAndNotByAStrayFileOfTheSameKey()
    {
        var rig = NewRig();
        Directory.CreateDirectory(rig.Msgs);
        const string Stray = "20261028-153558-zzzz";
        // a file with no row: a listing of the msgs directory would take it for the held request
        File.WriteAllText(Path.Combine(rig.Msgs, Stray + "--orchestrator--s--request.md"), Request().Replace(Id, Stray, StringComparison.Ordinal));

        var first = Held(DedupedCreate(rig, "20261028-153600-efgh", "2026-10-28T15:36:00-04:00"));
        var retry = Held(DedupedCreate(rig, "20261028-153601-ijkl", "2026-10-28T15:36:01-04:00"));

        Assert.False(first.Existing);
        Assert.Equal((true, "20261028-153600-efgh"), retry);
    }

    [Fact]
    public void ACreateThatClosesTheRequestIsSealedAtRevisionOneAndAResponseSkeletonIsNot()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);
        Assert.True(rig.Create(ResponseName, Response(Closed)).Ok);

        var row = Assert.Single(rig.Q("SELECT state, body_rev, draft_path FROM msg_messages WHERE type = 'response'"));
        Assert.Equal(["sealed", 1L, null], row);
        var exchange = Assert.Single(rig.Q("SELECT state, closed_by, reason FROM msg_exchanges"));
        Assert.Equal(["closed", "orchestrator", "done"], exchange);
        Assert.Equal(Response(Closed), rig.Read(ResponseName));

        var other = NewRig();
        Exchange(other);
        Assert.Equal("draft", Assert.Single(other.Q("SELECT state FROM msg_messages WHERE type = 'response'"))[0]);
        Assert.Equal("answered", Assert.Single(other.Q("SELECT state FROM msg_exchanges"))[0]);
    }

    [Fact]
    public void AFillStoresTheBodyAsRevisionOne()
    {
        var db = NewRig();
        Exchange(db);
        Assert.True(db.Op("fill", FillArgs(db)).Ok);

        var row = Assert.Single(db.Q("SELECT state, body_rev, draft_path, export_path FROM msg_messages WHERE type = 'response'"));
        Assert.Equal(["sealed", 1L, null, MsgRows.PathKey(Path.Combine(db.Msgs, ResponseName))], row);
        Assert.Equal(db.Read(ResponseName), System.Text.Encoding.UTF8.GetString((byte[])Assert.Single(db.Q("SELECT b.bytes FROM msg_bodies b JOIN msg_messages m ON m.pk = b.msg_pk WHERE m.type = 'response'"))[0]!));
    }

    [Fact]
    public void InDbModeShowPrintsTheStoredBytesAndFillByIdCreatesTheResponseAndSplicesOneSection()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);
        rig.ProveOrchestrator();

        // a draft has no stored body until D4, so the request is read through its key; the filled response below is read with its file gone
        var shown = rig.Cli("show", Id);
        Assert.True(shown.Code == 0, shown.Err);
        Assert.Equal(Request(), shown.Out);

        var filled = rig.Cli("fill", "--id", Id, "--from", "orchestrator", "--body", "\"## [0] tldr\\n- [1] status: done\\n\\n## [1] findings\\n- none\\n\"");
        Assert.True(filled.Code == 0, filled.Err);
        Assert.Contains("\"type\":\"response\"", filled.Out, StringComparison.Ordinal);
        var response = rig.Cli("show", Id, "response");
        Assert.True(response.Code == 0, response.Err);
        Assert.Contains("type: response\n", response.Out, StringComparison.Ordinal);
        Assert.Contains("- [1] status: done\n", response.Out, StringComparison.Ordinal);

        var spliced = rig.Cli("fill", "--id", Id, "--from", "orchestrator", "--section", "1", "--body", "\"- one finding\\n\"");
        Assert.True(spliced.Code == 0, spliced.Err);

        // the sealed revision is the store's: with the export file gone, show still prints it (fill's own existence check is files-only until D4)
        var section = rig.Cli("show", Id, "response", "--section", "1");
        Assert.True(section.Code == 0, section.Err);
        Assert.Contains("## [1] findings\n- one finding\n", section.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("- none", section.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("## [0]", section.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void FillPartsKeepTheRequestOpenAndTheFinalFillKeepsEveryPartAndClosesIt()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);
        rig.ProveOrchestrator();

        Assert.Equal(0, rig.Cli("fill", "--id", Id, "--from", "orchestrator", "--part", "--body", "\"first deliverable\"").Code);
        var two = rig.Cli("fill", "--id", Id, "--from", "orchestrator", "--part", "--body", "\"second deliverable\"");
        Assert.True(two.Code == 0, two.Err);
        var partial = rig.Cli("show", Id, "response").Out;
        Assert.Contains("parts: 2\npartial: true\n", partial, StringComparison.Ordinal);
        Assert.True(MsgRows.Live(false, "impl", Id, true, System.Text.Encoding.UTF8.GetBytes(partial)));

        var final = rig.Cli("fill", "--id", Id, "--from", "orchestrator", "--body", "\"all done\"");
        Assert.True(final.Code == 0, final.Err);
        var shown = rig.Cli("show", Id, "response").Out;
        Assert.Matches("(?s)## part 1 · .*first deliverable.*## part 2 · .*second deliverable.*## final · .*all done", shown);
        Assert.DoesNotContain("partial:", shown, StringComparison.Ordinal);

        // negative: a final report without the partial mark is not live
        Assert.False(MsgRows.Live(false, "impl", Id, true, System.Text.Encoding.UTF8.GetBytes(shown)));

        // a second plain fill after parts appends: the parts and the first final stay
        var revised = rig.Cli("fill", "--id", Id, "--from", "orchestrator", "--body", "\"revised\"");
        Assert.True(revised.Code == 0, revised.Err);
        Assert.Matches("(?s)## part 1 · .*first deliverable.*## part 2 · .*second deliverable.*## final · .*all done.*## final · .*revised", rig.Cli("show", Id, "response").Out);
    }

    [Fact]
    public void AFillIntoAResponseTheStoredExchangeClosedIsRefusedBeforeTheFileIsLookedAt()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);
        Assert.True(rig.Create(ResponseName, Response(Closed)).Ok);

        var refused = rig.Op("fill", FillArgs(rig));
        Assert.False(refused.Ok);
        Assert.Contains("was closed by orchestrator", refused.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("an unknown time", refused.Error, StringComparison.Ordinal);

        var open = NewRig();
        Assert.True(open.Create(RequestName, Request()).Ok);
        Assert.Contains("no response ", open.Op("fill", FillArgs(open)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ARetireWritesANewRevisionAndAClosedExchangeAndASecondRetireChangesNothing()
    {
        var db = NewRig();
        Exchange(db);
        Assert.True(db.Op("fill", FillArgs(db)).Ok);
        var retire = new JsonObject { ["path"] = Path.Combine(db.Msgs, ResponseName), ["closed_by"] = "orchestrator", ["closed_reason"] = "done" };
        Assert.True(db.Op("retire", retire).Ok);

        Assert.Equal(2L, Assert.Single(db.Q("SELECT body_rev FROM msg_messages WHERE type = 'response'"))[0]);
        Assert.Equal("closed", Assert.Single(db.Q("SELECT state FROM msg_exchanges"))[0]);
        Assert.True(db.Op("retire", retire).Ok);
        Assert.Equal(2L, Assert.Single(db.Q("SELECT body_rev FROM msg_messages WHERE type = 'response'"))[0]);
        Assert.Equal(2L, Assert.Single(db.Q("SELECT count(*) FROM msg_bodies b JOIN msg_messages m ON m.pk = b.msg_pk WHERE m.type = 'response'"))[0]);
    }

    /// <summary>The contract row cli/msg/new/request sees only the id and path now that no file lands in its tree: the frontmatter is held here.</summary>
    [Fact]
    public void MsgNewStoresARequestSkeletonWithItsFrontmatterAndMakesNoMsgsDirectory()
    {
        var rig = NewRig();
        rig.ProveOrchestrator();

        var r = rig.New("--to", "implementor", "--from", "orchestrator", "--slug", "contract", "--title", "Check the frontmatter", "--json");

        Assert.True(r.Code == 0, r.Err + r.Out);
        var made = JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(r.Out)).AsObject();
        var id = made.Get("id")!.Value.AsString();
        var text = System.Text.Encoding.UTF8.GetString(rig.Graph.Hierarchy.Core.StoredBytes(made.Get("path")!.Value.AsString())!);
        Assert.StartsWith("---\nid: " + id + "\n", text, StringComparison.Ordinal);
        foreach (var line in new[] { "\ntype: request\n", "\nto: implementor\n", "\nfrom: orchestrator\n", "\nslug: contract\n", "\nparent: null\n", "\n## [0] tldr\n" })
        {
            Assert.Contains(line, text, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(rig.Msgs));
    }

    [Fact]
    public void ASettleStoresANewRevisionOnlyForTheMemberWhoseDecisionTheRequestHolds()
    {
        var db = NewRig();
        Assert.True(db.Create(RequestName, Request("decision: bob\n")).Ok);
        var settled = Request("decision: bob\nanswer: yes\n");
        var wrong = db.Op("settle", new JsonObject { ["path"] = Path.Combine(db.Msgs, RequestName), ["from"] = "alice", ["content"] = settled });
        Assert.False(wrong.Ok);
        Assert.Equal(Request("decision: bob\n"), db.Read(RequestName));
        Assert.Equal("draft", Assert.Single(db.Q("SELECT state FROM msg_messages"))[0]);

        Assert.True(db.Op("settle", new JsonObject { ["path"] = Path.Combine(db.Msgs, RequestName), ["from"] = "bob", ["content"] = settled }).Ok);
        Assert.Equal(settled, db.Read(RequestName));
        Assert.Equal(["sealed", 1L], Assert.Single(db.Q("SELECT state, body_rev FROM msg_messages")));
    }

    [Fact]
    public void ARetireRecordsWhenTheExchangeClosedSoOneOpenedFortyDaysAgoIsNotPrunedAtOnce()
    {
        var rig = NewRig();
        Exchange(rig);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal([null], Assert.Single(rig.Q("SELECT closed_ms FROM msg_exchanges")));
        var day = 86_400_000L;
        rig.Exec($"UPDATE msg_exchanges SET opened_ms = {before - (40 * day)}");

        Assert.True(rig.Op("retire", new JsonObject { ["path"] = Path.Combine(rig.Msgs, ResponseName), ["closed_by"] = "o", ["closed_reason"] = "r" }).Ok);

        var closed = (long)Assert.Single(rig.Q("SELECT closed_ms FROM msg_exchanges"))[0]!;
        Assert.InRange(closed, before - 5_000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000);

        // the prune predicate (StateDbMsgReader.ClosedIdsSql) at a 30-day cutoff keeps it; the opened time alone would not
        rig.Exec("UPDATE msg_exchanges SET answered_ms = NULL, live = 0");
        Assert.Empty(rig.Q($"SELECT id FROM msg_exchanges WHERE live = 0 AND COALESCE(answered_ms, closed_ms, opened_ms) < {before - (30 * day)}"));
    }

    [Fact]
    public void AWholeExchangeWritesNoFile()
    {
        var rig = NewRig();
        Exchange(rig);
        Assert.True(rig.Op("fill", FillArgs(rig)).Ok);
        rig.Graph.Hierarchy.Messages.SealForSend(Path.Combine(rig.Msgs, RequestName));
        Assert.True(rig.Op("retire", new JsonObject { ["path"] = Path.Combine(rig.Msgs, ResponseName), ["closed_by"] = "o", ["closed_reason"] = "r" }).Ok);
        Assert.True(rig.Op("archive", new JsonObject { ["files"] = new JsonArray { Path.Combine(rig.Msgs, RequestName), Path.Combine(rig.Msgs, ResponseName) } }).Ok);

        Assert.False(Directory.Exists(rig.Msgs));
        Assert.Contains("all good", rig.Read(Path.Combine("archive", ResponseName)), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAFileAtAMessagesPathIsNoMessage()
    {
        var rig = NewRig();
        Directory.CreateDirectory(rig.Msgs);
        var path = Path.Combine(rig.Msgs, RequestName);
        File.WriteAllText(path, Request());

        Assert.False(rig.Graph.Hierarchy.Core.MsgExists(path));
        Assert.Null(rig.Graph.Hierarchy.Core.MsgBytes(path));
        Assert.Null(rig.Graph.Hierarchy.Core.MsgTimeMs(path));
        Assert.True(rig.Graph.Hierarchy.Messages.ResponseLanded(path, 0) is false);
    }

    [Fact]
    public void AWriteInAHomeStillOnFilesFailsNamingTheImportAndLeavesNoFile()
    {
        var rig = NewRig(db: false);

        var refused = rig.Create(RequestName, Request());

        Assert.False(refused.Ok);
        Assert.Contains("ah db list", refused.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(rig.Msgs, RequestName)));
        var send = Assert.ThrowsAny<Exception>(() => rig.Graph.Hierarchy.Messages.SealForSend(Path.Combine(rig.Msgs, RequestName)));
        Assert.Contains("ah db list", send.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableStoreIsOneLineOnStderrAndExitOne()
    {
        var rig = NewRig();
        rig.ProveOrchestrator();
        File.WriteAllText(Path.Combine(rig.Sandbox.AhHome, "state", "ah.db"), new string('x', 8192));

        var r = rig.App("list");

        Assert.True(r.Code == 1, $"exit {r.Code}: {r.Err}");
        Assert.StartsWith("ah: the state store cannot be read (", r.Err, StringComparison.Ordinal);
        Assert.Contains("ah db restore", r.Err, StringComparison.Ordinal);
        Assert.Equal(string.Empty, r.Out);
    }

    [Fact]
    public void NegativeAReadableStoreWithNoMessagesIsNoFailure()
    {
        var rig = NewRig();
        rig.ProveOrchestrator();

        var r = rig.App("list");

        Assert.True(r.Code == 0, $"exit {r.Code}: {r.Err}");
    }

    [Fact]
    public void ADraftIsItsOnlyRevisionUntilAFillReplacesItAndALaterWriteIsTheNextRevision()
    {
        var rig = NewRig();
        Exchange(rig);
        Assert.Equal(["draft", 1L], Assert.Single(rig.Q("SELECT state, body_rev FROM msg_messages WHERE type = 'response'")));

        Assert.True(rig.Op("fill", FillArgs(rig)).Ok);

        Assert.Equal(["sealed", 1L], Assert.Single(rig.Q("SELECT state, body_rev FROM msg_messages WHERE type = 'response'")));
        Assert.True(rig.Op("retire", new JsonObject { ["path"] = Path.Combine(rig.Msgs, ResponseName), ["closed_by"] = "o", ["closed_reason"] = "r" }).Ok);
        Assert.Equal(2L, Assert.Single(rig.Q("SELECT body_rev FROM msg_messages WHERE type = 'response'"))[0]);
    }

    [Fact]
    public void ASkeletonResponseHasNotLandedAndOneFillLandsItAtOnce()
    {
        var rig = NewRig();
        Exchange(rig);
        var path = Path.Combine(rig.Msgs, ResponseName);
        Assert.False(rig.Graph.Hierarchy.Messages.ResponseLanded(path));
        Assert.Null(Assert.Single(rig.Q("SELECT answered_ms FROM msg_exchanges"))[0]);

        Assert.True(rig.Op("fill", FillArgs(rig)).Ok);

        Assert.True(rig.Graph.Hierarchy.Messages.ResponseLanded(path));
        Assert.NotNull(Assert.Single(rig.Q("SELECT answered_ms FROM msg_exchanges"))[0]);
    }

    [Fact]
    public void AnArchiveMarksTheRowsAndTheExchangeArchivedAndMovesNoFile()
    {
        var rig = NewRig();
        Exchange(rig);
        var done = rig.Op("archive", new JsonObject { ["files"] = new JsonArray { Path.Combine(rig.Msgs, RequestName), Path.Combine(rig.Msgs, ResponseName) } });

        Assert.True(done.Ok);
        Assert.False(Directory.Exists(rig.Msgs));
        var rows = rig.Q("SELECT type, state, draft_path FROM msg_messages ORDER BY type");
        Assert.Equal(["request", "archived", MsgRows.PathKey(Path.Combine(rig.Msgs, "archive", RequestName))], rows[0]);
        Assert.Equal(["response", "archived", MsgRows.PathKey(Path.Combine(rig.Msgs, "archive", ResponseName))], rows[1]);
        Assert.Equal("archived",Assert.Single(rig.Q("SELECT state FROM msg_exchanges"))[0]);
    }

    [Fact]
    public void SendingFlipsTheDraftToSealedOnceWithNoNewRevisionAndAStrayFileChangesNothing()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);
        var path = Path.Combine(rig.Msgs, RequestName);
        Directory.CreateDirectory(rig.Msgs);
        File.WriteAllText(path, Request() + "- [2] edited by hand: y\n");

        rig.Graph.Hierarchy.Messages.SealForSend(path);
        rig.Graph.Hierarchy.Messages.SealForSend(path);

        Assert.Equal(["sealed", 1L, MsgRows.PathKey(path), null], Assert.Single(rig.Q("SELECT state, body_rev, export_path, draft_path FROM msg_messages")));
        var body = (byte[])Assert.Single(rig.Q("SELECT bytes FROM msg_bodies"))[0]!;
        Assert.Equal(Request(), System.Text.Encoding.UTF8.GetString(body));

        var note = Path.Combine(rig.Msgs, Id + "--orchestrator--s--note.md");
        rig.Graph.Hierarchy.Messages.SealForSend(note);
        Assert.Single(rig.Q("SELECT pk FROM msg_messages"));
    }

    [Fact]
    public void ACreateOverTheCapIsRefusedWithNoRowOrFileWhetherSealedOrADraft()
    {
        var rig = NewRig();
        var head = Request();
        var refused = rig.Create(RequestName, head + new string('x', MsgRows.BodyCap - head.Length + 1));

        Assert.False(refused.Ok);
        Assert.Contains("holds at most 262144", refused.Error, StringComparison.Ordinal);
        Assert.Empty(rig.Q("SELECT pk FROM msg_messages"));

        Assert.True(rig.Create(RequestName, head + new string('x', MsgRows.BodyCap - head.Length)).Ok);
        Assert.False(Directory.Exists(rig.Msgs));
    }

    [Fact]
    public void ACreateThatIsSealedAtCreateAndOverTheCapIsRefusedWithNoRowOrFile()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);
        var head = Response(Closed);
        var refused = rig.Create(ResponseName, head + new string('x', MsgRows.BodyCap - head.Length + 1));

        Assert.False(refused.Ok);
        Assert.Contains("262144", refused.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(rig.Msgs, ResponseName)));
        Assert.Single(rig.Q("SELECT pk FROM msg_messages"));
    }

    [Fact]
    public async Task AFilesModeCreateThatWaitedOnThePoolLockWhileTheDomainFlippedLandsInTheDatabase()
    {
        var rig = NewRig(db: false);
        Directory.CreateDirectory(Ah.StateDb.StateDbModule.StateLockDirectory(rig.Sandbox.AhHome));
        var held = rig.Graph.Io.Locks.Acquire(Ah.StateDb.StateDbModule.StateLockPath(rig.Sandbox.AhHome, "msgs", Path.Combine(rig.Dir, ".state-msgs"), ".domain.lock"), TimeSpan.FromSeconds(5));
        Assert.NotNull(held);
        var create = Task.Run(() => rig.Create(RequestName, Request()));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(create.IsCompleted);
        rig.SetMode("db");
        held!.Dispose();

        Assert.True((await create).Ok);
        Assert.Single(rig.Q("SELECT state FROM msg_messages"));
        Assert.Equal(Request(), rig.Read(RequestName));
    }

    [Fact]
    public void SendingRefusesWhenTheStorageModeCannotBeRead()
    {
        var rig = NewRig();
        Assert.True(rig.Create(RequestName, Request()).Ok);
        File.WriteAllBytes(Path.Combine(rig.Sandbox.AhHome, "state", "ah.db"), "not a database"u8.ToArray());

        var refused = Assert.Throws<InvalidOperationException>(() => rig.Graph.Hierarchy.Messages.SealForSend(Path.Combine(rig.Msgs, RequestName)));
        Assert.Contains("cannot be read", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryQueryTheWritePathRunsUsesAnIndexAndNoTemporarySort()
    {
        var rig = NewRig();
        Exchange(rig);
        string[] queries =
        [
            MsgRows.SelectPkSql,
            "SELECT bytes FROM msg_bodies WHERE msg_pk = ?1 ORDER BY rev DESC LIMIT 1",
            "SELECT rev, sha256 FROM msg_bodies WHERE msg_pk = ?1 ORDER BY rev DESC LIMIT 1",
            "SELECT pk, type, state, \"to\", \"from\", team, created_ms, slug FROM msg_messages WHERE pool_dir = ?1 AND id = ?2 AND type IN ('request', 'response')",
            "UPDATE msg_exchanges SET request_pk = ?3, response_pk = ?4, state = ?5, opened_ms = ?6, closed_by = ?7, reason = ?8, team = ?9, \"to\" = ?10, \"from\" = ?11 WHERE pool_dir = ?1 AND id = ?2",
            "SELECT m.pk FROM msg_exchanges e JOIN msg_messages m ON m.pk = e.response_pk WHERE e.pool_dir = ?1 AND e.id = ?2 AND e.state = 'closed'",
            "UPDATE msg_messages SET state = 'archived', draft_path = ?6 WHERE pool_dir = ?1 AND id = ?2 AND type = ?3 AND \"to\" = ?4 AND slug = ?5",
            "UPDATE msg_messages SET export_sha = ?3 WHERE pk = ?1 AND body_rev = ?2 AND export_sha IS NULL",
        ];
        foreach (var sql in queries)
        {
            var parameters = System.Text.RegularExpressions.Regex.Matches(sql, @"\?(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
            var plan = string.Join("; ", rig.Q("EXPLAIN QUERY PLAN " + sql, [.. Enumerable.Range(1, parameters).Select(i => (object?)(i == 1 ? "x" : (long)i))]).Select(r => (string)r[3]!));
            var scans = plan.Split("; ").Where(p => p.StartsWith("SCAN", StringComparison.Ordinal) && !p.Contains("msg_messages_export_pending", StringComparison.Ordinal));
            Assert.True(!scans.Any(), sql + " => " + plan);
            Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
        }
    }
}
