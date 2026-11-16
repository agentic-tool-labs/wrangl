using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Cli.Verbs.Msg;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>Attachments, external ids and the export a mirror reads (ah-work-items S4), in a real git repository under a temp home, through the production composition.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WorkAttachTests : IDisposable
{
    private const string Team = "tm";

    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly CliContext _context;
    private readonly FakeEnv _env;
    private readonly IProcessRunner _runner;
    private readonly string _cwd;
    private readonly string _dir;

    public WorkAttachTests()
    {
        _cwd = Path.Combine(Directory.ResolveLinkTarget(_sandbox.Root, returnFinalTarget: true)?.FullName ?? _sandbox.Root, "my-cool-repo");
        _cwd = new DirectoryInfo(Directory.CreateDirectory(_cwd).FullName).FullName;
        _dir = Path.Combine(_cwd, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(_dir, "teams"));
        File.WriteAllText(
            Path.Combine(_dir, "teams", Team + ".json"),
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":["
            + "{\"role\":\"architect\",\"name\":\"tm-architect\",\"kind\":\"claude\",\"transport_id\":\"p1\"},{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p2\"},{\"role\":\"reviewer\",\"name\":\"tm-reviewer\",\"kind\":\"claude\",\"transport_id\":\"p3\"}]}\n");
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        // the planted team file reaches the store through the import pass (files-off-disk §4)
        _ = StateDbModule.ImportHierDirFiles(_sandbox.AhHome, _dir, force: true);

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = _dir, ["WRANGL_TEAM_FILE"] = Path.Combine(_dir, "teams", Team + ".json"),
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var runner = new RealGit();
        _env = env;
        _runner = runner;
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _context = new CliContext(env, new RecordingExec(), runner, "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        Git("init", "-q");
        Git("config", "user.email", "t@example.invalid");
        Git("config", "user.name", "t");
    }

    public void Dispose() => _sandbox.Dispose();

    /// <summary>Git is the real one, for real shas; everything else the mail path asks about this process answers as a known live self.</summary>
    private sealed class RealGit : IProcessRunner
    {
        private readonly IProcessRunner _git = PlatformModule.CreateProcessRunner();
        private readonly IProcessRunner _rest = FakeRunner.KnowingSelf();

        public ProcessResult Run(ProcessRequest request) => (request.Argv[0] == "git" ? _git : _rest).Run(request);
    }

    private string Git(params string[] args)
    {
        var result = ConfigModule.CreateConventions(_graph.Runner, _graph.Io.Files, () => _sandbox.Home).TryGit(new GitScope(_cwd), args);
        Assert.True(result is not null, "git " + string.Join(' ', args));
        return result.Trim();
    }

    private string Commit(string relative)
    {
        Write(relative);
        Git("add", relative);
        Git("commit", "-q", "-m", "x");
        return Git("log", "-1", "--format=%h");
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_cwd, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x\n");
    }

    private (int Code, string Out, string Err) Work(params string[] args) => Run(WorkVerb.Run, [.. args, "--cwd", _cwd]);

    private (int Code, string Out, string Err) Backlog(params string[] args) => Run(BacklogVerb.Run, [.. args, "--cwd", _cwd]);

    private (int Code, string Out, string Err) Run(Func<CliContext, string[], TextWriter, TextWriter, int> verb, string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = verb(_context, args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static void Ok((int Code, string Out, string Err) result) => Assert.True(result.Code == 0, result.Err);

    private string AddRow(string title = "Row")
    {
        var added = Work("add", title);
        Ok(added);
        return added.Out.Trim();
    }

    private string AddTeamItem(string slug)
    {
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, Team, "_run", "o@" + Team, [new PlanInput(slug, "add", "Do " + slug)]).Refused);
        return Work("show", "_run/" + slug, "--json").Out.Split('"')[3];
    }

    private JsonValue Exported(params string[] more) => JsonText.Parse(Work(["export", "--json", .. more]).Out);

    private static JsonValue ItemOf(JsonValue export, string id) =>
        export.Member("items")!.Value.AsArray().Single(i => i.Member("id")!.Value.AsString() == id);

    private string? Scalar(string sql)
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return reader.Query(sql)[0][0]?.ToString();
    }

    [Fact]
    public void ACommittedSpecRecordsItsShaAndAnUncommittedFileSaysSo()
    {
        var sha = Commit("docs/specs/a.md");
        Write("notes.md");
        var id = AddRow();

        Ok(Work("attach", id, "docs/specs/a.md"));
        Ok(Work("attach", id, "notes.md", "--title", "My notes"));

        var shown = Work("show", id).Out;
        Assert.Contains($"spec docs/specs/a.md @{sha}", shown, StringComparison.Ordinal);
        Assert.Contains("file notes.md uncommitted (My notes)", shown, StringComparison.Ordinal);
        Assert.Contains($"spec docs/specs/a.md @{sha}", Work("show", id, "--json").Out, StringComparison.Ordinal);

        // the negative: another item's show lists none of them
        Assert.DoesNotContain("attachments:", Work("show", AddRow("Other")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AColonAfterASlashIsAPathNotAUrl()
    {
        Write("docs/a:b.md");
        var id = AddRow();

        Ok(Work("attach", id, "docs/a:b.md"));

        Assert.Contains("file docs/a:b.md uncommitted", Work("show", id).Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TITLE_TOO_LONG")]
    public void AnAttachmentTitleMustFit(string title)
    {
        Write("a.md");
        var id = AddRow();

        Assert.Equal(2, Work("attach", id, "a.md", "--title", title == "" ? title : new string('x', 121)).Code);

        Ok(Work("attach", id, "a.md", "--title", new string('x', 120)));
    }

    [Fact]
    public void AnAttachmentOnNoItemIsRefused()
    {
        var result = _graph.Writer.Invoke(
            "progress", HierarchyModule.WorkWriteOp, JsonText.ToUtf8(new JsonObject { ["dir"] = _dir, ["attach"] = new JsonObject { ["n"] = 999, ["ref"] = "a.md", ["kind"] = "file" } }, 0), _graph.Hierarchy.Messages.PoolContext(_dir), new StateCallOptions(OperationId: "t1"));

        Assert.False(result.Ok);
    }

    [Fact]
    public void ASpecUnderASubfolderIsAlsoASpecAndAPathElsewhereIsAFile()
    {
        Commit("sub/docs/specs/a.md");
        Commit("docs/other.md");
        var id = AddRow();

        Ok(Work("attach", id, "sub/docs/specs/a.md"));
        Ok(Work("attach", id, "docs/other.md"));

        var shown = Work("show", id).Out;
        Assert.Contains("spec sub/docs/specs/a.md", shown, StringComparison.Ordinal);
        Assert.Contains("file docs/other.md", shown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("/etc/hosts")]
    [InlineData("docs/../../outside.md")]
    public void APathOutsideTheRepoIsRefused(string path)
    {
        var id = AddRow();

        var refused = Work("attach", id, path);

        Assert.Equal(2, refused.Code);
        Assert.Contains("outside the repository", refused.Err, StringComparison.Ordinal);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM work_attachments"));
    }

    [Fact]
    public void AnAbsolutePathInsideTheRepoIsStoredRelative()
    {
        Commit("docs/a.md");
        var id = AddRow();

        Ok(Work("attach", id, Path.Combine(_cwd, "docs", "a.md")));

        Assert.Equal("docs/a.md", Scalar("SELECT ref FROM work_attachments"));
    }

    [Fact]
    public void AUrlMustBeHttpOrHttps()
    {
        var id = AddRow();

        Ok(Work("attach", id, "https://example.com/x"));
        Assert.Contains("url https://example.com/x", Work("show", id).Out, StringComparison.Ordinal);

        foreach (var bad in new[] { "javascript:alert(1)", "file:///etc/hosts", "ftp://example.com/x" })
        {
            Assert.Equal(2, Work("attach", id, bad).Code);
        }

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_attachments"));
    }

    [Fact]
    public void TheSameRefUpdatesItsTitleAndShaAndRemoveDeletesIt()
    {
        Commit("a.md");
        var id = AddRow();
        Ok(Work("attach", id, "a.md", "--title", "Old"));
        var newer = Commit("a2.md");
        File.WriteAllText(Path.Combine(_cwd, "a.md"), "changed\n");
        Git("add", "a.md");
        Git("commit", "-q", "-m", "y");
        newer = Git("log", "-1", "--format=%h");

        Ok(Work("attach", id, "a.md", "--title", "New"));

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_attachments"));
        Assert.Contains($"file a.md @{newer} (New)", Work("show", id).Out, StringComparison.Ordinal);

        // no title given keeps the one there
        Ok(Work("attach", id, "a.md"));
        Assert.Contains("(New)", Work("show", id).Out, StringComparison.Ordinal);

        Ok(Work("attach", id, "a.md", "--remove"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM work_attachments"));
        Assert.DoesNotContain("attachments:", Work("show", id).Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("ftp://example.com/x")]
    [InlineData("example.com/x")]
    [InlineData("https://example.com/a b")]
    [InlineData("http:///x")]
    public void AUrlOfAnyOtherSchemeIsRefused(string url)
    {
        var row = AddRow();
        var team = AddTeamItem("t");

        Assert.Equal(2, Work("set", row, "--ext", "LIN-1", "--url", url).Code);
        Assert.Equal(2, Work("set", team, "--ext", "LIN-2", "--url", url).Code);

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM work_items WHERE ext_url IS NOT NULL"));
    }

    [Fact]
    public void AUrlNeedsAnExtAndTheLimitsHold()
    {
        var id = AddTeamItem("t");

        Assert.Equal(2, Work("set", id, "--url", "https://example.com/x").Code);
        Assert.Equal(2, Work("set", id, "--ext", new string('x', 129)).Code);
        Assert.Equal(2, Work("set", id, "--ext", "LIN-1", "--url", "https://example.com/" + new string('x', 500)).Code);

        Ok(Work("set", id, "--ext", new string('x', 128)));
        Ok(Work("set", id, "--url", "https://example.com/" + new string('x', 400)));
    }

    [Fact]
    public void ExtAndUrlClearAsTheTableSays()
    {
        var id = AddTeamItem("t");
        Ok(Work("set", id, "--ext", "LIN-1", "--url", "https://example.com/1"));
        Assert.Contains("ext: LIN-1 https://example.com/1", Work("show", id).Out, StringComparison.Ordinal);
        Assert.Contains("Do t", Work("show", "LIN-1").Out, StringComparison.Ordinal);

        Ok(Work("set", id, "--url", ""));
        Assert.Contains("ext: LIN-1\n", Work("show", id).Out, StringComparison.Ordinal);

        Ok(Work("set", id, "--url", "https://example.com/1"));
        Ok(Work("set", id, "--ext", ""));
        Assert.DoesNotContain("ext:", Work("show", id).Out, StringComparison.Ordinal);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM work_items WHERE ext_id IS NOT NULL OR ext_url IS NOT NULL"));
    }

    [Fact]
    public void ANewIdDropsTheUrlOfTheOldOne()
    {
        var id = AddTeamItem("t");
        Ok(Work("set", id, "--ext", "LIN-1", "--url", "https://example.com/1"));

        // the negative: the same id again keeps its URL
        Ok(Work("set", id, "--ext", "LIN-1"));
        Assert.Contains("ext: LIN-1 https://example.com/1", Work("show", id).Out, StringComparison.Ordinal);

        Ok(Work("set", id, "--ext", "LIN-2"));

        Assert.Contains("ext: LIN-2\n", Work("show", id).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExtHeldElsewhereIsExitThreeNamingTheHolder()
    {
        var row = AddRow();
        var team = AddTeamItem("t");
        var other = AddTeamItem("u");
        Ok(Work("set", row, "--ext", "LIN-1"));
        Ok(Work("set", team, "--ext", "LIN-2"));

        var onTeam = Work("set", other, "--ext", "LIN-1");
        Assert.Equal(3, onTeam.Code);
        Assert.Contains(row, onTeam.Err, StringComparison.Ordinal);

        var onRow = Work("set", row, "--ext", "LIN-2");
        Assert.Equal(3, onRow.Code);
        Assert.Contains(team, onRow.Err, StringComparison.Ordinal);

        // the negative: setting an item's own id again is no conflict
        Ok(Work("set", row, "--ext", "LIN-1"));
    }

    [Fact]
    public void ABacklogRowsExtStaysInBothStores()
    {
        var id = AddRow();

        Ok(Work("set", id, "--ext", "LIN-1", "--url", "https://example.com/1"));
        Assert.Equal("LIN-1", Scalar("SELECT ext FROM plan_items"));
        Assert.Equal("LIN-1", Scalar("SELECT ext_id FROM work_items"));
        Assert.Equal("https://example.com/1", Scalar("SELECT ext_url FROM work_items"));

        Ok(Backlog("set", id, "--ext", "LIN-2"));
        Assert.Equal("LIN-2", Scalar("SELECT ext FROM plan_items"));
        Assert.Equal("LIN-2", Scalar("SELECT ext_id FROM work_items"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM work_items WHERE ext_url IS NOT NULL"));

        Ok(Work("set", id, "--ext", ""));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM plan_items WHERE ext IS NULL"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_items WHERE ext_id IS NULL"));

        // the empty text still refused on the old verb, as before
        Assert.Equal(2, Backlog("set", id, "--ext", "").Code);
    }

    [Fact]
    public void ClearingARowsExtAlsoClearsItsIssue()
    {
        Git("remote", "add", "origin", "https://github.com/o/r.git");
        var id = AddRow();

        Ok(Work("set", id, "--ext", "github:o/r#7"));
        Assert.Contains("\"issue\":7", Scalar("SELECT doc FROM plan_items"), StringComparison.Ordinal);

        Ok(Work("set", id, "--ext", ""));
        Assert.DoesNotContain("\"issue\":7", Scalar("SELECT doc FROM plan_items"), StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUseReadsTheBacklogsExt()
    {
        Ok(Backlog("add", "Row", "--ext", "LIN-9"));
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("DELETE FROM work_items");
            tx.Execute("DELETE FROM work_repos");
            tx.Execute("DELETE FROM work_prefixes");
            return 0;
        });
        Assert.True(written.Ok, written.Message);

        Assert.Contains("Row", Work("show", "LIN-9").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowTheRegistryMissedAlsoReadsItsExt()
    {
        Ok(Backlog("add", "Row", "--ext", "LIN-9"));
        Ok(Work("list"));
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("DELETE FROM work_items");
            return 0;
        });
        Assert.True(written.Ok, written.Message);

        Ok(Work("list"));

        Assert.Equal("LIN-9", Scalar("SELECT ext_id FROM work_items"));
    }

    [Fact]
    public void ExportCarriesWhatAMirrorNeedsAndNoBodies()
    {
        var parent = Work("add", "P", "--kind", "project").Out.Trim();
        var row = Work("add", "Row", "--parent", parent).Out.Trim();
        var team = AddTeamItem("t");
        Ok(Work("link", row, "--blocked-by", team));
        Ok(Work("set", row, "--ext", "LIN-1", "--url", "https://example.com/1"));
        Ok(Work("attach", row, "https://example.com/spec"));
        Ok(Work("record", row, "--type", "note", "--title", "Hello", "--body", "secret body"));

        var raw = Work("export", "--json").Out;
        var json = JsonText.Parse(raw);

        Assert.DoesNotContain("secret body", raw, StringComparison.Ordinal);
        Assert.True(json.Member("as_of_ms")!.Value.AsNumber() > 0);
        var item = ItemOf(json, row);
        Assert.Equal("Row", item.Member("title")!.Value.AsString());
        Assert.Equal(parent, item.Member("parent")!.Value.AsString());
        Assert.Equal(team, item.Member("blocked_by")!.Value.AsArray().Single().AsString());
        Assert.Equal("LIN-1", item.Member("ext_id")!.Value.AsString());
        Assert.Equal("https://example.com/1", item.Member("ext_url")!.Value.AsString());
        Assert.Single(item.Member("attachments")!.Value.AsArray());
        Assert.Equal("b2", item.Member("aliases")!.Value.AsArray().Single().AsString());
        Assert.True(item.Member("log_count")!.Value.AsNumber() >= 2);
        Assert.Equal(row, ItemOf(json, team).Member("blocks")!.Value.AsArray().Single().AsString());
    }

    [Fact]
    public void ExportSinceIncludesALogOnlyChangeAndLeavesOutWhatDidNotMove()
    {
        var a = AddRow("A");
        var b = AddRow("B");
        var asOf = SettledAsOf();
        Assert.Equal(1, Work("export", "--since", asOf.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json").Code);

        Ok(Work("record", a, "--type", "note", "--title", "Only a log entry"));

        var later = Exported("--since", asOf.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var item = Assert.Single(later.Member("items")!.Value.AsArray());
        Assert.Equal(a, item.Member("id")!.Value.AsString());
        Assert.NotEqual(b, item.Member("id")!.Value.AsString());
    }

    /// <summary>An as_of_ms once the running second, which it stays behind, is over.</summary>
    private long SettledAsOf()
    {
        Thread.Sleep(2100);
        return (long)Exported().Member("as_of_ms")!.Value.AsNumber();
    }

    [Fact]
    public void ExportSinceSeesAPlanChangeOnARowAndOnATeamItem()
    {
        var row = AddRow("A");
        var team = AddTeamItem("t");
        var since = SettledAsOf().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Thread.Sleep(1100);

        Ok(Backlog("set", row, "--title", "A2"));
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, Team, "_run", "o", [new PlanInput("t", "retitle", "T2")]).Refused);

        var ids = Exported("--since", since).Member("items")!.Value.AsArray().Select(static i => i.Member("id")!.Value.AsString()).ToList();
        Assert.Contains(row, ids);
        Assert.Contains(team, ids);
    }

    [Fact]
    public void AChangeInTheSecondAnExportRanIsNotLost()
    {
        var row = AddRow("A");
        var team = AddTeamItem("t");

        var since = ((long)Exported().Member("as_of_ms")!.Value.AsNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, Team, "_run", "o", [new PlanInput("t", "retitle", "T2")]).Refused);

        var ids = Exported("--since", since).Member("items")!.Value.AsArray().Select(static i => i.Member("id")!.Value.AsString()).ToList();
        Assert.Contains(team, ids);
        Assert.NotEmpty(row);
    }

    [Fact]
    public void AsOfNeverMovesBehindSince()
    {
        AddRow();
        const long Future = 4_000_000_000_000;

        var export = Exported("--since", Future.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Empty(export.Member("items")!.Value.AsArray());
        Assert.Equal(Future, (long)export.Member("as_of_ms")!.Value.AsNumber());
    }

    [Fact]
    public void ALinkWritesAnEntryOnBothItemsAndARecordCannotForgeOne()
    {
        var a = AddRow("A");
        var b = AddRow("B");

        Ok(Work("link", a, "--blocked-by", b));
        Ok(Work("link", a, "--blocked-by", b));
        Assert.Contains($"blocked by {b}", Work("log", a).Out, StringComparison.Ordinal);
        Assert.Contains($"blocks {a}", Work("log", b).Out, StringComparison.Ordinal);
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM work_log WHERE type = 'link'"));

        Ok(Work("link", a, "--blocked-by", b, "--remove"));
        Assert.Contains($"no longer blocked by {b}", Work("log", a).Out, StringComparison.Ordinal);

        Assert.Equal(2, Work("record", a, "--type", "link", "--title", "x").Code);
    }

    [Fact]
    public void CardsCarryTheIdExtAndOnlyOpenBlockersOfThatStream()
    {
        var row = AddRow("Row");
        var open = AddRow("Open");
        var closed = AddRow("Closed");
        var team = AddTeamItem("t");
        Ok(Work("set", row, "--ext", "LIN-1", "--url", "https://example.com/1"));
        Ok(Work("link", row, "--blocked-by", open));
        Ok(Work("link", row, "--blocked-by", closed));
        Ok(Backlog("drop", closed, "--note", "no"));

        var backlog = WorkRows.Cards(_graph, _sandbox.AhHome, _dir, string.Empty, HierarchyModule.BacklogStream);
        var stream = WorkRows.Cards(_graph, _sandbox.AhHome, _dir, Team, "_run");

        Assert.Equal(new Ah.Watch.Model.WorkCard(row, "LIN-1", "https://example.com/1", 1), backlog["b1"]);
        Assert.Equal(team, Assert.Single(stream).Value.Id);
        Assert.Empty(WorkRows.Cards(_graph, _sandbox.AhHome, _dir, Team, "other"));
    }

    /// <summary>The body a real read model serves for the slice, once it has one; null JSON when it serves the null body. Fails the test if it never does.</summary>
    private static async Task<JsonValue> ServedAsync(IReadModel model, string id, Func<JsonValue, bool> until, params string[] others)
    {
        string[] wanted = [id, .. others];
        var last = "nothing";
        for (var i = 0; i < 200; i++)
        {
            // From cursor 0 a held slice answers at once, so the wait has to be here for the model to get a turn.
            await Task.Delay(50);
            var reply = JsonText.Parse(await model.PollAsync(0, null, wanted, TimeSpan.FromMilliseconds(100), CancellationToken.None, wanted));
            foreach (var slice in reply.Member("slices")?.AsArray() ?? [])
            {
                if (slice.Member("id")?.AsString() == id && slice.Member("body") is { } body)
                {
                    last = JsonText.Stringify(body, 0);
                    if (until(body))
                    {
                        return body;
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"{id} never reached the state asked for; last served: {last}");
    }

    /// <summary>The directory's id in a slice id; <c>WatchModule.DirOfId</c> confirms it is what the model calls the directory.</summary>
    private string DirId()
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_dir)))[..8].ToLowerInvariant();
        Assert.Equal(_dir, WatchModule.DirOfId(id, [_dir]));
        return id;
    }

    private IReadModel ServedModel(Func<string, long, JsonObject?> itemLog)
    {
        var model = WatchModule.CreateReadModel(
            new StateServiceReader(_graph.InternalStateRead),
            () => [_dir],
            new ReadModelOptions
            {
                NewPlanFold = HierarchyModule.CreatePlanFold,
                WorkCardsProbe = (dir, folder, stream) => WorkRows.Cards(_graph, _sandbox.AhHome, dir, folder, stream),
                ItemLogProbe = itemLog,
            });
        WatchModule.StartReadModel(model);
        return model;
    }

    [Fact]
    public async Task ATeamItemsServedPlanItemCarriesItsCard()
    {
        var team = AddTeamItem("t");
        Ok(Work("set", team, "--ext", "LIN-9", "--url", "https://example.com/9"));
        using var model = ServedModel((_, _) => null);
        var suffix = DirId() + "/" + Team;

        var body = await ServedAsync(model, $"plan-items:{suffix}/_run", static b => b.Member("items")?.AsArray().Count > 0, "plan:" + suffix);

        var item = Assert.Single(body.Member("items")!.Value.AsArray());
        Assert.StartsWith("_run:", item.Member("key")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(team, item.Member("work_id")!.Value.AsString());
        Assert.Equal("LIN-9", item.Member("ext_id")!.Value.AsString());
        Assert.Equal("https://example.com/9", item.Member("ext_url")!.Value.AsString());
        Assert.Equal(0, item.Member("blocked_by_open")!.Value.AsNumber());
    }

    [Fact]
    public async Task AnItemWithNoRowIsServedANullBodyAndOneDeletedWhileHeldTurnsToIt()
    {
        var present = true;
        using var model = ServedModel((_, n) => n == 1 && present ? new JsonObject { ["id"] = "w-1" } : null);
        var dirId = DirId();

        Assert.True((await ServedAsync(model, $"item-log:{dirId}/99999", static _ => true)).IsNull);
        Assert.Equal("w-1", (await ServedAsync(model, $"item-log:{dirId}/1", static b => !b.IsNull)).Member("id")!.Value.AsString());

        present = false;
        model.Emit("item-log", _dir);

        Assert.True((await ServedAsync(model, $"item-log:{dirId}/1", static b => b.IsNull)).IsNull);
    }

    [Fact]
    public void TheItemLogBodyHasEntriesLinksAttachmentsAndChildren()
    {
        var parent = Work("add", "P", "--kind", "project").Out.Trim();
        var child = Work("add", "C", "--parent", parent).Out.Trim();
        var other = AddRow("O");
        Ok(Work("link", parent, "--blocked-by", other));
        Ok(Work("link", other, "--blocked-by", child));
        Ok(Work("attach", parent, "https://example.com/spec"));
        Ok(Work("record", parent, "--type", "note", "--title", "Hello", "--body", "the body"));
        var n = long.Parse(parent[(parent.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        var body = JsonValue.FromObject(WorkRows.ItemLog(_graph, _sandbox.AhHome, _dir, n)!);

        Assert.Equal(parent, body.Member("id")!.Value.AsString());
        var note = body.Member("entries")!.Value.AsArray().Single(static e => e.Member("title")!.Value.AsString() == "Hello");
        Assert.Equal("the body", note.Member("body")!.Value.AsString());
        Assert.Equal(0, body.Member("more")!.Value.AsNumber());
        Assert.Equal(other, body.Member("links")!.Value.Member("blocked_by")!.Value.AsArray().Single().AsString());
        Assert.Empty(body.Member("links")!.Value.Member("blocks")!.Value.AsArray());
        Assert.Equal("https://example.com/spec", body.Member("attachments")!.Value.AsArray().Single().Member("ref")!.Value.AsString());
        Assert.Equal(child, body.Member("children")!.Value.AsArray().Single().AsString());
    }

    [Fact]
    public void TheItemLogBodyHasTheDescriptionOrNull()
    {
        var with = Work("add", "With", "--body", "First line\nsecond line").Out.Trim();
        var without = Work("add", "Without").Out.Trim();
        Ok(Work("record", without, "--type", "note", "--title", "Not one", "--body", "a note"));
        JsonValue? Description(string id) =>
            JsonValue.FromObject(WorkRows.ItemLog(_graph, _sandbox.AhHome, _dir, long.Parse(id[(id.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture))!).Member("description");

        Assert.Equal("First line\nsecond line", Description(with)!.Value.AsString());
        Assert.True(Description(without)!.Value.IsNull);
    }

    [Fact]
    public void TheItemLogKeepsTheNewestEntriesAndCountsTheRest()
    {
        var id = AddRow();
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            for (var i = 0; i < WorkRows.MaxEntries + 5; i++)
            {
                tx.Execute("INSERT INTO work_log(hier_dir, n, ts_ms, type, title, by) SELECT hier_dir, n, ?1, 'note', ?2, 'x' FROM work_items", 4_000_000_000_000 + i, "e" + i);
            }

            return 0;
        });
        Assert.True(written.Ok, written.Message);
        var n = long.Parse(id[(id.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        var body = JsonValue.FromObject(WorkRows.ItemLog(_graph, _sandbox.AhHome, _dir, n)!);

        var entries = body.Member("entries")!.Value.AsArray();
        Assert.Equal(WorkRows.MaxEntries, entries.Count);
        Assert.Equal("e" + (WorkRows.MaxEntries + 4), entries[^1].Member("title")!.Value.AsString());
        Assert.True(body.Member("more")!.Value.AsNumber() >= 5);
    }

    [Fact]
    public void NoSuchItemHasNoLog()
    {
        AddRow();

        Assert.Null(WorkRows.ItemLog(_graph, _sandbox.AhHome, _dir, 999));
    }

    [Fact]
    public void TheWritesNameTheTeamOfTheItemTheyChange()
    {
        var row = AddRow();
        var team = AddTeamItem("t");
        var other = AddRow("Other");
        var written = new List<JsonValue>();
        var daemon = new FakeDaemonClient { Info = FakeDaemonClient.Up() };
        daemon.Reply = request =>
        {
            if (request.PathAndQuery == "/v1/health")
            {
                return new DaemonReply(200, "{\"impl\":\"native\",\"protocols\":[1,2]}"u8.ToArray());
            }

            if (request.PathAndQuery.EndsWith("/" + HierarchyModule.WorkWriteOp, StringComparison.Ordinal))
            {
                written.Add(JsonText.Parse(request.Body.Span).Member("args")!.Value);
            }

            throw new IOException("the daemon went away, so the client writes for itself");
        };
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, _runner, daemon);
        var context = new CliContext(_env, new RecordingExec(), _runner, "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        string TeamOf(params string[] args)
        {
            written.Clear();
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.True(WorkVerb.Run(context, [.. args, "--cwd", _cwd], stdout, stderr) == 0, stderr.ToString());
            Assert.NotEmpty(written);
            return written[^1].Member("team")!.Value.AsString();
        }

        Assert.Equal(Team, TeamOf("record", team, "--type", "note", "--title", "x"));
        Assert.Equal(string.Empty, TeamOf("record", row, "--type", "note", "--title", "x"));
        Assert.Equal(Team, TeamOf("set", team, "--ext", "LIN-1"));
        Assert.Equal(Team, TeamOf("attach", team, "https://example.com/1"));
        Assert.Equal(Team, TeamOf("link", team, "--blocked-by", row));
        Assert.Equal(Team, TeamOf("link", row, "--blocks", team));
        Assert.Equal(string.Empty, TeamOf("link", row, "--blocked-by", other));
    }

    [Fact]
    public void ALinkPrintsWhatItDid()
    {
        var a = AddRow("A");
        var b = AddRow("B");

        Assert.Equal($"{a} blocked by {b}\n", Work("link", a, "--blocked-by", b).Out);
        Assert.Equal($"{a} no longer blocked by {b}\n", Work("link", a, "--blocked-by", b, "--remove").Out);
    }

    [Fact]
    public void ADependsCycleStaysInThePlanAndOutOfTheLinks()
    {
        var a = AddRow("A");
        var b = AddRow("B");
        Ok(Backlog("set", a, "--depends", b));

        Ok(Backlog("set", b, "--depends", a));

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_links"));
        Assert.Contains("\"depends\":[\"b1\"]", Scalar("SELECT doc FROM plan_items WHERE item = 'b2'"), StringComparison.Ordinal);
    }

    [Fact]
    public void BackfillLeavesADependsCycleOutOfTheLinks()
    {
        var a = AddRow("A");
        var b = AddRow("B");
        Ok(Backlog("set", a, "--depends", b));
        Ok(Backlog("set", b, "--depends", a));
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("DELETE FROM work_links");
            tx.Execute("DELETE FROM work_items");
            tx.Execute("DELETE FROM work_repos");
            tx.Execute("DELETE FROM work_prefixes");
            return 0;
        });
        Assert.True(written.Ok, written.Message);

        Ok(Work("list"));

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_links"));
    }

    [Fact]
    public void ALoopAlreadyInTheDataEndsTheWalk()
    {
        var a = AddRow("A");
        var b = AddRow("B");
        var c = AddRow("C");
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("INSERT INTO work_links(hier_dir, blocker_n, blocked_n, by, ts_ms) SELECT hier_dir, 1, 2, 'x', 0 FROM work_repos");
            tx.Execute("INSERT INTO work_links(hier_dir, blocker_n, blocked_n, by, ts_ms) SELECT hier_dir, 2, 1, 'x', 0 FROM work_repos");
            return 0;
        });
        Assert.True(written.Ok, written.Message);

        Ok(Work("link", c, "--blocked-by", a));

        Assert.Contains(b, Work("show", a).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AUrlOrAPathOutsideTakesNoAutoAttachSlot()
    {
        AddTeamItem("widget");

        WorkVerb.AutoAttach(_graph, _sandbox.Home, _sandbox.AhHome, _cwd, _dir, Team, "_run/widget", "https://h/docs/specs/u.md ../../docs/specs/o1.md ../../docs/specs/o2.md ../../docs/specs/o3.md docs/specs/a.md");

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_attachments"));
        Assert.Equal("docs/specs/a.md", Scalar("SELECT ref FROM work_attachments"));
    }

    private string HolderOf(string id) => ItemOf(Exported(), id).Member("holder")!.Value.AsString();

    /// <summary>An item built and reported DONE, then under review with the review's <c>--item</c> on it: its worker still holds it (board-derive B2).</summary>
    private string Reviewed(string review)
    {
        var id = AddTeamItem("w");
        Request("20261103-110000-bbbb", "implementor", "_run/w");
        Assert.Equal(0, Reply("20261103-110000-bbbb", "implementor", "DONE\n- built\n").Code);
        Request(review, "reviewer", "_run/w");
        Assert.Equal("implementor-1", HolderOf(id));
        return id;
    }

    private (int Code, string Out, string Err) LinkFromReviewer(string to, string task)
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "s-reviewer", Path.Combine(_dir, "teams", Team + ".json"), "tm-reviewer@" + Team);
        return Msg("link-request", "--to", to, "--task", task, "--why", "fix it", "--session", "s-reviewer", "--cwd", _cwd);
    }

    [Fact]
    public void AReviewersLinkMovesTheItemToTheWorker()
    {
        var id = Reviewed("20261103-120000-aaaa");

        Ok(LinkFromReviewer("tm-implementor@" + Team, "20261103-120000-aaaa"));

        Assert.Equal("tm-implementor", HolderOf(id));
    }

    [Theory]
    [InlineData("tm-architect@tm")]
    [InlineData("tm-implementor@tm,tm-architect@tm")]
    public void ALinkToTheArchitectOrToSeveralMovesNothing(string to)
    {
        var id = Reviewed("20261103-120000-aaaa");

        Ok(LinkFromReviewer(to, "20261103-120000-aaaa"));

        Assert.Equal("implementor-1", HolderOf(id));
    }

    [Fact]
    public void AMembersLinkRequestWithTheDaemonUpIsGrantedOnTheFirstTry()
    {
        AddTeamItem("r");
        Request("20261103-120000-aaaa", "reviewer", "_run/r");
        var ahdSends = 0;
        var daemon = new FakeDaemonClient { Info = FakeDaemonClient.Up() };
        daemon.Reply = request =>
        {
            if (request.PathAndQuery == "/v1/health")
            {
                return new DaemonReply(200, "{\"impl\":\"native\",\"protocols\":[1,2]}"u8.ToArray());
            }

            if (request.PathAndQuery.StartsWith("/v1/send", StringComparison.Ordinal))
            {
                // the real send route's refusal (MeshRoutes): no caller may send as the daemon
                if (JsonText.Parse(request.Body.Span).Member("from") is { IsText: true } from && from.AsString() == "ahd")
                {
                    ahdSends++;
                    return new DaemonReply(400, "{\"error\":{\"code\":\"from_mismatch\",\"message\":\"--from ahd is reserved for the daemon's own events\"}}"u8.ToArray());
                }

                return new DaemonReply(0, default, "down");
            }

            throw new IOException("the daemon went away, so the client writes for itself");
        };
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, _runner, daemon);
        var context = new CliContext(_env, new RecordingExec(), _runner, "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        FakeRunner.RegisterSelf(graph.Mailbox, "s-reviewer", Path.Combine(_dir, "teams", Team + ".json"), "tm-reviewer@" + Team);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = MsgVerb.Run(context, ["link-request", "--to", "tm-implementor@" + Team, "--task", "20261103-120000-aaaa", "--why", "fix it", "--session", "s-reviewer", "--cwd", _cwd, "--team", Team, "--json"], stdout, stderr);

        Assert.True(code == 0, stderr.ToString());
        Assert.StartsWith("lk-", JsonText.Parse(stdout.ToString()).Member("link")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(0, ahdSends);
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM msg_messages WHERE type = 'request' AND slug LIKE 'lreq-%'"));
    }

    [Fact]
    public void ARefusedLinkRequestFilesNothingAndAMemberStillCannotSendAsTheDaemon()
    {
        AddTeamItem("r");
        Request("20261103-120000-aaaa", "reviewer", "_run/r");

        Assert.NotEqual(0, LinkFromReviewer("ghost@" + Team, "20261103-120000-aaaa").Code);
        Assert.NotEqual(0, Msg("send", "tm-implementor@" + Team, "--from", "ahd", "--text", "x", "--session", "s-reviewer", "--cwd", _cwd).Code);

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM msg_messages WHERE type = 'request' AND slug LIKE 'lreq-%'"));
    }

    [Fact]
    public void AReviewsOwnItemStaysWithTheReviewerOverALink()
    {
        var id = AddTeamItem("r");
        Request("20261103-120000-aaaa", "reviewer", "_run/r");

        Ok(LinkFromReviewer("tm-implementor@" + Team, "20261103-120000-aaaa"));

        Assert.Equal("reviewer-1", HolderOf(id));
    }

    [Fact]
    public void AFailingVerdictHandsTheItemBackToItsWorker()
    {
        var id = Reviewed("20261103-120000-aaaa");

        Assert.Equal(0, Reply("20261103-120000-aaaa", "reviewer", "VERDICT CHANGES REQUIRED s@abc1234\n").Code);

        var item = ItemOf(Exported(), id);
        Assert.Equal("implementor-1", item.Member("holder")!.Value.AsString());
        Assert.Equal("dispatched", item.Member("state")!.Value.AsString());
    }

    [Fact]
    public void BacklogSetDependsWritesLinkEntriesOnlyForWhatMoved()
    {
        Ok(Backlog("add", "A", "--wave", "now"));
        Ok(Backlog("add", "B", "--wave", "now"));
        Ok(Backlog("add", "C", "--wave", "now"));

        Ok(Backlog("set", "b1", "--depends", "b2"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM work_log WHERE type = 'link'"));
        Ok(Backlog("set", "b1", "--depends", "b2"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM work_log WHERE type = 'link'"));

        Ok(Backlog("set", "b1", "--depends", "b3"));
        Assert.Equal("6", Scalar("SELECT COUNT(*) FROM work_log WHERE type = 'link'"));
    }

    /// <summary>b459 C3: the ready backlog and a team's stream names are held; negative: a backlog write and a document row with no counter move (the import take) are both seen.</summary>
    [Fact]
    public void TheReadyBacklogAndStreamNamesAreHeldAndNegativeABacklogWriteOrATakenDocumentIsSeen()
    {
        Ok(Backlog("add", "A", "--wave", "now"));
        var watch = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable, null);
        var ready = watch.ReadyBacklog("no-session", _dir);
        Assert.Same(ready, watch.ReadyBacklog("no-session", _dir));

        Ok(Backlog("add", "B", "--wave", "now"));
        Assert.Equal(2, watch.ReadyBacklog("no-session", _dir).Count);

        var names = _graph.Hierarchy.Progress.StreamNames(_dir, Team);
        Assert.Same(names, _graph.Hierarchy.Progress.StreamNames(_dir, Team));
        Assert.True(StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("INSERT INTO progress_docs(pool, team, name, kind, body, updated_ms) VALUES (?1, ?2, 'taken.md', 'checklist', '# taken', 0)", Ah.Core.Json.MsgRows.PoolKey(_dir), Team);
            return 0;
        }).Ok);
        Assert.Contains("taken", _graph.Hierarchy.Progress.StreamNames(_dir, Team));
    }

    [Fact]
    public void NextHandsOutOnlyWorkThatCanStart()
    {
        var waiting = Work("add", "Waiting", "--wave", "now").Out.Trim();
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, Team, "_run", "o", [new PlanInput("started", "add", "Started"), new PlanInput("started", "start")]).Refused);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, Team, "_run", "o", [new PlanInput("stuck", "add", "Stuck"), new PlanInput("stuck", "block", Note: "why")]).Refused);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, Team, "_run", "o", [new PlanInput("userwait", "add", "Waits"), new PlanInput("userwait", "wait-user", Note: "why")]).Refused);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, Team, "_run", "o", [new PlanInput("nexted", "add", "Nexted"), new PlanInput("nexted", "next")]).Refused);
        Ok(Backlog("drop", waiting, "--note", "no"));

        var next = Work("next");

        Assert.Contains("Nexted", next.Out, StringComparison.Ordinal);
        Assert.Equal("Nexted", Work("list", "--ready", "--json").Out.Split("\"title\":\"")[1].Split('"')[0]);
        Assert.Single(JsonText.Parse(Work("list", "--ready", "--json").Out).Member("items")!.Value.AsArray());
    }

    private (int Code, string Out, string Err) Msg(params string[] args) => Run(MsgVerb.Run, [.. args, "--team", Team]);

    private string BodyFile(string text)
    {
        var path = Path.Combine(_sandbox.Root, "body.md");
        File.WriteAllText(path, text);
        return path;
    }

    private void Request(string id, string role, string item)
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", Path.Combine(_dir, "teams", Team + ".json"), "orchestrator@" + Team);
        var r = Msg("new", "--type", "request", "--to", role, "--to-name", role + "-1", "--from", "orchestrator", "--slug", "s", "--title", "Design the thing", "--id", id, "--session", "o", "--body-file", BodyFile("go\n"), "--item", item, "--cwd", _cwd);
        Assert.True(r.Code == 0, r.Err);
    }

    private (int Code, string Out, string Err) Reply(string id, string role, string body)
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "s-" + role, Path.Combine(_dir, "teams", Team + ".json"), $"{Team}-{role}@{Team}");
        return Msg("new", "--type", "response", "--id", id, "--from", role, "--session", "s-" + role, "--body-file", BodyFile(body), "--cwd", _cwd);
    }

    [Fact]
    public void AnArchitectsReplyAttachesTheSpecsItNames()
    {
        var sha = Commit("docs/specs/plan.md");
        var id = AddTeamItem("widget");
        Request("20261103-120000-aaaa", "architect", "_run/widget");

        var reply = Reply("20261103-120000-aaaa", "architect", "DONE\nSee docs/specs/plan.md, again docs/specs/plan.md, and docs/specs/b.md, docs/specs/c.md, docs/specs/d.md.\n");

        Assert.True(reply.Code == 0, reply.Err);
        Assert.Equal("3", Scalar("SELECT COUNT(*) FROM work_attachments"));
        Assert.Equal("auto", Scalar("SELECT DISTINCT by FROM work_attachments"));
        Assert.Contains($"spec docs/specs/plan.md @{sha}", Work("show", id).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherRolesReplyAttachesNothing()
    {
        Commit("docs/specs/plan.md");
        AddTeamItem("widget");
        Request("20261103-120000-aaaa", "implementor", "_run/widget");

        Assert.Equal(0, Reply("20261103-120000-aaaa", "implementor", "DONE\nSee docs/specs/plan.md\n").Code);

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM work_attachments"));
    }

    [Fact]
    public void AnAutoAttachFailureNeverFailsTheReply()
    {
        AddTeamItem("widget");
        Request("20261103-120000-aaaa", "architect", "_run/widget");
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("ALTER TABLE work_attachments RENAME TO work_attachments_away");
            return 0;
        });
        Assert.True(written.Ok, written.Message);

        var reply = Reply("20261103-120000-aaaa", "architect", "DONE\nSee docs/specs/plan.md\n");

        Assert.True(reply.Code == 0, reply.Err);
    }

    [Fact]
    public void ARequestWithNoItemAttachesToTheItemItMade()
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", Path.Combine(_dir, "teams", Team + ".json"), "orchestrator@" + Team);
        var made = Msg("new", "--type", "request", "--to", "architect", "--from", "orchestrator", "--slug", "s", "--title", "Design the thing", "--id", "20261103-120000-aaaa", "--session", "o", "--body-file", BodyFile("go\n"), "--cwd", _cwd);
        Assert.True(made.Code == 0, made.Err);

        Assert.Equal(0, Reply("20261103-120000-aaaa", "architect", "DONE\nSee docs/specs/plan.md\n").Code);

        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_attachments"));
    }

    [Fact]
    public void AutoAttachSkipsAMissAPathOutsideNoBodyAndAnotherTeamsItem()
    {
        AddTeamItem("widget");
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "other", "_run", "o", [new PlanInput("theirs", "add", "Theirs")]).Refused);
        Ok(Work("list"));

        WorkVerb.AutoAttach(_graph, _sandbox.Home, _sandbox.AhHome, _cwd, _dir, Team, "nosuch", "docs/specs/a.md");
        WorkVerb.AutoAttach(_graph, _sandbox.Home, _sandbox.AhHome, _cwd, _dir, Team, "_run/widget", "../../docs/specs/a.md");
        WorkVerb.AutoAttach(_graph, _sandbox.Home, _sandbox.AhHome, _cwd, _dir, Team, "_run/widget", null);
        WorkVerb.AutoAttach(_graph, _sandbox.Home, _sandbox.AhHome, _cwd, _dir, Team, "other/_run/theirs", "docs/specs/a.md");
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM work_attachments"));

        // the negative: a backlog row is everyone's
        var row = AddRow();
        WorkVerb.AutoAttach(_graph, _sandbox.Home, _sandbox.AhHome, _cwd, _dir, Team, row, "docs/specs/a.md");
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM work_attachments"));
    }
}
