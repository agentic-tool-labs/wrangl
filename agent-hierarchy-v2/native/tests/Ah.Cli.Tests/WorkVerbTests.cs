using System.Diagnostics;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah work</c> and the work registry (ah-work-items S1) against a temp home's state database, through the production composition.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class WorkVerbTests : IDisposable
{
    private static readonly string[] Writers = ["p", "q"];

    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly CliContext _context;
    private readonly string _dir;
    private readonly string _cwd;

    public WorkVerbTests()
    {
        _cwd = Path.Combine(_sandbox.Root, "my-cool-repo");
        _dir = Path.Combine(_cwd, ".claude", "ah2");
        Directory.CreateDirectory(_dir);
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        (_graph, _context) = Compose(_dir, "tm");
    }

    public void Dispose() => _sandbox.Dispose();

    private (ServiceGraph Graph, CliContext Context) Compose(string dir, string? team, bool inDaemon = false)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = dir };
        if (team is not null)
        {
            variables["WRANGL_TEAM_FILE"] = Path.Combine(dir, "teams", team + ".json");
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") }, inDaemon);
        return (graph, new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph)));
    }

    [Fact]
    public void AWriteRecordsWhetherTheDaemonOrTheCallerAppliedIt()
    {
        AddTeamItem("x");
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM applied_writes WHERE path <> 'direct'"));

        var (daemonGraph, _) = Compose(_dir, "tm", inDaemon: true);
        Assert.Null(daemonGraph.Hierarchy.Progress.RecordChecklist(_dir, "tm", "_run", "o", [new PlanInput("y", "add", "Do y")]).Refused);
        Assert.True(Scalar("SELECT COUNT(*) FROM applied_writes WHERE path = 'daemon'") > 0);
    }

    private (int Code, string Out, string Err) Work(params string[] args) => Run(WorkVerb.Run, _context, args);

    private (int Code, string Out, string Err) Backlog(params string[] args) => Run(BacklogVerb.Run, _context, args);

    private static (int Code, string Out, string Err) Run(Func<CliContext, string[], TextWriter, TextWriter, int> verb, CliContext context, string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = verb(context, args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private void AddTeamItem(string slug, string team = "tm", string stream = "_run", string? at = null)
    {
        var result = _graph.Hierarchy.Progress.RecordChecklist(_dir, team, stream, "o@" + team, [new PlanInput(slug, "add", "Do " + slug) { At = at }]);
        Assert.Null(result.Refused);
    }

    private void Sql(params string[] statements)
    {
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            foreach (var statement in statements)
            {
                tx.Execute(statement);
            }

            return 0;
        });
        Assert.True(written.Ok, written.Message);
    }

    private void ForgetRegistry() => Sql("DELETE FROM work_items", "DELETE FROM work_repos", "DELETE FROM work_prefixes");

    private long Scalar(string sql)
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return (long)reader.Query(sql)[0][0]!;
    }

    private static string FirstWord(string line) => line.Split(' ')[0];

    private static List<string> Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    [Fact]
    public void ANewBacklogRowIsRegisteredInItsOwnWriteAndAnEditIsNot()
    {
        Assert.Equal(0, Backlog("add", "First").Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM work_items"));

        // the negative: writing to an existing row allocates nothing
        Assert.Equal(0, Backlog("set", "b1", "--title", "Renamed").Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM work_items"));
        Assert.Equal(1L, Scalar("SELECT seq FROM work_repos"));
    }

    [Fact]
    public void BackfillKeepsBacklogNumbersAndOrdersTeamItemsByAddedAt()
    {
        Assert.Equal(0, Backlog("add", "One").Code);
        Assert.Equal(0, Backlog("add", "Two").Code);
        AddTeamItem("later", at: "2026-11-03T12:00:00Z");
        AddTeamItem("earlier", at: "2026-11-03T09:00:00Z");
        ForgetRegistry();

        var r = Work("list");

        Assert.True(r.Code == 0, r.Err);
        var lines = Lines(r.Out);
        Assert.Equal(["mcr-1", "mcr-2", "mcr-3", "mcr-4"], lines.Select(FirstWord));
        Assert.Contains("One", lines[0], StringComparison.Ordinal);
        Assert.Contains("Two", lines[1], StringComparison.Ordinal);
        Assert.Contains("Do earlier", lines[2], StringComparison.Ordinal);
        Assert.Contains("Do later", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstTeamItemAfterABacklogFirstUseStillBackfillsOlderItemsFirst()
    {
        AddTeamItem("older", at: "2026-11-03T09:00:00Z");
        ForgetRegistry();
        Assert.Equal(0, Backlog("add", "A row").Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM work_items"));

        AddTeamItem("newer", at: "2026-11-03T10:00:00Z");

        var lines = Lines(Work("list").Out);
        Assert.Contains("Do older", lines[1], StringComparison.Ordinal);
        Assert.Contains("Do newer", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ANewBacklogRowAfterBackfillDrawsTheNextNumberNotItsOwn()
    {
        Assert.Equal(0, Backlog("add", "One").Code);
        AddTeamItem("x");
        Assert.Equal(0, Backlog("add", "Two").Code);

        var shown = Work("show", "b2");

        Assert.True(shown.Code == 0, shown.Err);
        Assert.StartsWith("mcr-3 Two", shown.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultPrefixIsDerivedFromTheRepoRootBasename()
    {
        foreach (var (name, expected) in new[] { ("agent-tools", "at"), ("engram", "eng"), ("ab", "ab"), ("9lives", "w"), ("9-lives", "w"), ("a.b_c-d", "abcd"), ("Mixed-Case", "mc") })
        {
            var dir = Path.Combine(_sandbox.Root, name, ".claude", "ah2");
            Directory.CreateDirectory(dir);
            var (_, context) = Compose(dir, null);
            var r = Run(WorkVerb.Run, context, ["prefix"]);
            Assert.True(r.Code == 0, r.Err);
            Assert.Equal(expected, r.Out.Trim());
        }

        // the negative: a dir that is not .claude/ah2 is named for itself, not for its grandparent
        var plain = Path.Combine(_sandbox.Root, "outer", "inner", "own-dir");
        Directory.CreateDirectory(plain);
        Assert.Equal("od", Run(WorkVerb.Run, Compose(plain, null).Context, ["prefix"]).Out.Trim());
    }

    [Fact]
    public void APrefixChangeKeepsOldIdsResolvingAndPrintsTheNewOne()
    {
        AddTeamItem("x");
        Assert.Equal("mcr", Work("prefix").Out.Trim());

        Assert.Equal(0, Work("prefix", "ah").Code);

        var shown = Work("show", "mcr-1");
        Assert.True(shown.Code == 0, shown.Err);
        Assert.StartsWith("ah-1 ", shown.Out, StringComparison.Ordinal);
        Assert.StartsWith("ah-1 ", Work("show", "ah-1").Out, StringComparison.Ordinal);

        // the negative: a prefix this repo never had names nothing
        Assert.Equal(1, Work("show", "zz-1").Code);
    }

    [Theory]
    [InlineData("B9x")]
    [InlineData("9x")]
    [InlineData("abcdefghi")]
    [InlineData("a-b")]
    public void ABadPrefixIsRefusedAndChangesNothing(string prefix)
    {
        AddTeamItem("x");

        var r = Work("prefix", prefix);

        Assert.Equal(2, r.Code);
        Assert.Equal("mcr", Work("prefix").Out.Trim());
    }

    [Fact]
    public void TwoReposWithOnePrefixAreIndependent()
    {
        AddTeamItem("x");
        var otherDir = Path.Combine(_sandbox.Root, "other", "my-cool-repo", ".claude", "ah2");
        Directory.CreateDirectory(otherDir);
        var (otherGraph, otherContext) = Compose(otherDir, "tm");
        Assert.Null(otherGraph.Hierarchy.Progress.RecordChecklist(otherDir, "tm", "_run", "o", [new PlanInput("y", "add", "Do y")]).Refused);

        Assert.Contains("Do x", Work("show", "mcr-1").Out, StringComparison.Ordinal);
        Assert.Contains("Do y", Run(WorkVerb.Run, otherContext, ["show", "mcr-1"]).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ReusingASlugGivesANewNumberAndTheOldIdReadsGone()
    {
        AddTeamItem("reused", at: "2026-11-03T09:00:00Z");
        TestProgressLogs.Drop(_sandbox.AhHome, _dir, "tm", "_run.plan.jsonl");

        AddTeamItem("reused", at: "2026-11-03T10:00:00Z");

        Assert.Contains("state: gone", Work("show", "mcr-1").Out, StringComparison.Ordinal);
        Assert.Contains("state: not_started", Work("show", "mcr-2").Out, StringComparison.Ordinal);
        Assert.StartsWith("mcr-2 ", Work("show", "reused").Out, StringComparison.Ordinal);

        // the negative: the same registration again is not a reuse
        ForgetNothingReconcile();
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM work_items"));
    }

    private void ForgetNothingReconcile() => Assert.Equal(0, Work("list").Code);

    [Fact]
    public void AGonePlacementKeepsItsIdAndIsLeftOutOfTheOpenList()
    {
        AddTeamItem("x");
        AddTeamItem("y", stream: "main");
        TestProgressLogs.Drop(_sandbox.AhHome, _dir, "tm", "_run.plan.jsonl");

        Assert.DoesNotContain("mcr-1 ", Work("list").Out, StringComparison.Ordinal);
        Assert.Contains("mcr-1 gone", Work("list", "--state", "all").Out, StringComparison.Ordinal);
        Assert.Contains("mcr-1 gone", Work("list", "--state", "gone").Out, StringComparison.Ordinal);
        Assert.Equal(0, Work("show", "mcr-1").Code);
    }

    [Fact]
    public void ABareSlugPrefersTheCallersTeamThenTheRepoAndSeveralMatchesAreRefused()
    {
        AddTeamItem("shared", team: "tm");
        AddTeamItem("shared", team: "other");
        AddTeamItem("twice", team: "other");
        AddTeamItem("twice", team: "other", stream: "main");
        AddTeamItem("onlythere", team: "other");

        Assert.StartsWith("mcr-1 ", Work("show", "shared").Out, StringComparison.Ordinal);
        Assert.StartsWith("mcr-2 ", Work("show", "other/_run/shared").Out, StringComparison.Ordinal);
        Assert.Contains("Do onlythere", Work("show", "onlythere").Out, StringComparison.Ordinal);
        Assert.Contains("Do twice", Work("show", "main/twice").Out, StringComparison.Ordinal);

        var ambiguous = Work("show", "twice");
        Assert.Equal(2, ambiguous.Code);
        Assert.Contains("mcr-3", ambiguous.Err, StringComparison.Ordinal);
        Assert.Contains("mcr-4", ambiguous.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissReconcilesAndAnUnknownIdIsExitOne()
    {
        AddTeamItem("x");
        ForgetRegistry();

        Assert.Contains("Do x", Work("show", "x").Out, StringComparison.Ordinal);

        Assert.Equal(1, Work("show", "nope").Code);
    }

    [Fact]
    public void AFailedRegistrationDoesNotFailTheAppendAndTheNextReconcileAssignsTheId()
    {
        AddTeamItem("first");
        Sql("ALTER TABLE work_items RENAME TO work_items_away");

        var appended = _graph.Hierarchy.Progress.RecordChecklist(_dir, "tm", "_run", "o", [new PlanInput("second", "add", "Do second")]);

        Assert.Null(appended.Refused);
        Assert.Equal(1, appended.Appended);
        Sql("ALTER TABLE work_items_away RENAME TO work_items");
        Assert.Contains("Do second", Work("show", "second").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOffTeamRegistersNothingAndIsNotAnError()
    {
        // another team's item first, so a stray registration would land instead of asking for a backfill
        AddTeamItem("other");
        TestProgressDocs.Off(_sandbox.AhHome, _dir, "offteam");

        var result = _graph.Hierarchy.Progress.RecordChecklist(_dir, "offteam", "_run", "o", [new PlanInput("x", "add", "Do x")]);

        Assert.Null(result.Refused);
        Assert.Equal(0, Work("list").Code);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM work_items WHERE team = 'offteam'"));
    }

    [Fact]
    public void AnUnreadableRegistryResolvesToNoRowInsteadOfThrowing()
    {
        AddTeamItem("x");
        Assert.NotNull(WorkVerb.Resolve(_graph, _sandbox.AhHome, _dir, "mcr-1", "tm").Row);
        Sql("ALTER TABLE work_items RENAME TO work_items_away");

        Assert.Null(WorkVerb.Resolve(_graph, _sandbox.AhHome, _dir, "mcr-1", "tm").Row);
    }

    [Fact]
    public void ATeamTurnedOffIsLeftOutOfReconcile()
    {
        AddTeamItem("x");
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "later", "_run", "o", [new PlanInput("y", "add", "Do y")]).Refused);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM work_items"));
        Sql("DELETE FROM work_items WHERE team = 'later'");
        Directory.CreateDirectory(Path.Combine(_dir, "progress", "later"));
        TestProgressDocs.Off(_sandbox.AhHome, _dir, "later");

        Assert.Equal(0, Work("list").Code);

        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM work_items WHERE team = 'later'"));
    }

    [Fact]
    public void AListThatFindsNothingNewWritesNothing()
    {
        AddTeamItem("x");
        var writes = Scalar("SELECT COUNT(*) FROM applied_writes");

        Assert.Equal(0, Work("list").Code);
        Assert.Equal(writes, Scalar("SELECT COUNT(*) FROM applied_writes"));

        // the negative: a new item does write, so the count above can move
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "tm", "_run", "o", [new PlanInput("y", "add", "Do y")]).Refused);
        Assert.True(Scalar("SELECT COUNT(*) FROM applied_writes") > writes);
    }

    [Fact]
    public void ARetryWithTheSameOperationIdLandsOnce()
    {
        AddTeamItem("x");
        var context = _graph.Hierarchy.Messages.PoolContext(_dir);
        byte[] Args(string at) => JsonText.ToUtf8(new JsonObject { ["dir"] = _dir, ["register"] = new JsonArray { new JsonObject { ["team"] = "tm", ["stream"] = "s", ["item"] = "retried", ["added_at"] = at } } }, 0);

        // a changed added_at is what would allocate again, so only the op id stops the second call
        Assert.True(_graph.Writer.Invoke("progress", HierarchyModule.WorkWriteOp, Args("2026-01-01T00:00:00Z"), context, new StateCallOptions(OperationId: "same")).Ok);
        Assert.True(_graph.Writer.Invoke("progress", HierarchyModule.WorkWriteOp, Args("2026-01-02T00:00:00Z"), context, new StateCallOptions(OperationId: "same")).Ok);
        Assert.Equal(2L, Scalar("SELECT seq FROM work_repos"));

        // the negative: another op id does land
        Assert.True(_graph.Writer.Invoke("progress", HierarchyModule.WorkWriteOp, Args("2026-01-02T00:00:00Z"), context, new StateCallOptions(OperationId: "other")).Ok);
        Assert.Equal(3L, Scalar("SELECT seq FROM work_repos"));
    }

    [Fact]
    public void BudgetsHoldOnFiveHundredItems()
    {
        var longest = new string('t', 120);
        var events = Enumerable.Range(0, 500).Select(i => new PlanInput($"i{i}", "add", longest)).ToList();
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "tm", "_run", "o", events).Refused);

        var list = Work("list");
        var lines = Lines(list.Out);
        Assert.Equal(51, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= 160, l));
        Assert.Equal("+450 more: --limit or filter", lines[^1]);

        // the negative: asking for more shows more, and no footer once all fit
        var all = Lines(Work("list", "--limit", "500").Out);
        Assert.Equal(500, all.Count);
        Assert.DoesNotContain("more:", all[^1], StringComparison.Ordinal);

        var json = JsonText.Parse(Work("list", "--json").Out);
        Assert.Equal(50, json.Member("items")!.Value.AsArray().Count);
        Assert.Equal(450, (int)json.Member("more")!.Value.AsNumber());
        Assert.All(json.Member("items")!.Value.AsArray(), i => Assert.True(i.Member("title")!.Value.AsString().Length <= 120));

        Assert.True(Work("show", "i7").Out.Length <= 4096);
        Assert.True(Work("show", "i7", "--json").Out.Length <= 4096);
    }

    [Fact]
    public void AListLineStaysWithinItsBudgetWithEveryFieldAtItsLongest()
    {
        var name = new string('n', 60);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, name, name, "o", [new PlanInput("x", "add", new string('t', 120)), new PlanInput("x", "assign") { Holder = name }]).Refused);

        var line = Assert.Single(Lines(Work("list").Out));

        Assert.True(line.Length <= 160, $"{line.Length}: {line}");
        Assert.StartsWith("mcr-1 not_started ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ListFiltersKeepOnlyWhatMatches()
    {
        AddTeamItem("a", team: "tm");
        AddTeamItem("b", team: "other");
        Assert.Equal(0, Backlog("add", "Row", "--kind", "epic").Code);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "tm", "_run", "o", [new PlanInput("a", "assign") { Holder = "impl" }]).Refused);

        Assert.Single(Lines(Work("list", "--team", "other").Out));
        Assert.Single(Lines(Work("list", "--holder", "impl").Out));
        Assert.Single(Lines(Work("list", "--kind", "epic").Out));
        Assert.Equal(1, Work("list", "--holder", "nobody").Code);
        Assert.Equal(2, Work("list", "--limit", "x").Code);
    }

    [Fact]
    public void BacklogVerbsTakeAWorkIdAndABacklogIdIsUnchanged()
    {
        AddTeamItem("x");
        Assert.Equal(0, Backlog("add", "Row").Code);
        var byBacklogId = Backlog("show", "b1", "--json");
        Assert.True(byBacklogId.Code == 0, byBacklogId.Err);

        var byWorkId = Backlog("show", "mcr-2", "--json");

        Assert.True(byWorkId.Code == 0, byWorkId.Err);
        Assert.Equal(byBacklogId.Out, byWorkId.Out);
        Assert.Equal(0, Backlog("set", "mcr-2", "--title", "Renamed").Code);
        Assert.Contains("Renamed", Backlog("show", "b1").Out, StringComparison.Ordinal);

        // the negatives: a work id naming a team item is no backlog item, and an unknown one stays unknown
        Assert.NotEqual(0, Backlog("show", "mcr-1").Code);
        Assert.NotEqual(0, Backlog("show", "b99").Code);
    }

    private (int Code, string Err) Msg(params string[] args)
    {
        var (code, _, err) = Run(MsgVerb.Run, _context, [.. args, "--cwd", _cwd]);
        return (code, err);
    }

    private string BodyFile()
    {
        var path = Path.Combine(_sandbox.Root, "body.md");
        File.WriteAllText(path, "do it");
        return path;
    }

    [Fact]
    public void MsgNewItemTakesAWorkIdPerPlacement()
    {
        WriteTeamFile();
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", Path.Combine(_dir, "teams", "tm.json"), "orchestrator@tm");
        AddTeamItem("mine");
        AddTeamItem("theirs", team: "other");
        Assert.Equal(0, Backlog("add", "Row").Code);
        string[] Request(string id, string item) => ["new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Fix the widget title", "--id", id, "--session", "o", "--body-file", BodyFile(), "--item", item];

        var mine = Msg(Request("20261103-120000-aaaa", "mcr-1"));
        Assert.True(mine.Code == 0, mine.Err);
        Assert.Contains("item: _run/mine", Message("20261103-120000-aaaa"), StringComparison.Ordinal);

        var row = Msg(Request("20261103-120001-bbbb", "mcr-3"));
        Assert.True(row.Code == 0, row.Err);

        // the same path as naming b1: the message carries what --item b1 writes, and the row gets the ref
        Assert.Contains("item: b1", Message("20261103-120001-bbbb"), StringComparison.Ordinal);
        Assert.Contains("20261103-120001-bbbb", Backlog("show", "b1", "--history", "--json").Out, StringComparison.Ordinal);

        var theirs = Msg(Request("20261103-120002-cccc", "mcr-2"));
        Assert.Equal(2, theirs.Code);
        Assert.Contains("team other", theirs.Err, StringComparison.Ordinal);

        // the negative: an unknown id is refused as before
        var unknown = Msg(Request("20261103-120003-dddd", "mcr-99"));
        Assert.Equal(2, unknown.Code);
        Assert.Contains("no item mcr-99", unknown.Err, StringComparison.Ordinal);
    }

    private string Message(string id)
    {
        var (code, text, err) = Run(MsgVerb.Run, _context, ["show", id, "--cwd", _cwd, "--team", "tm"]);
        Assert.True(code == 0, err);
        return text;
    }

    private void WriteTeamFile()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "teams"));
        File.WriteAllText(
            Path.Combine(_dir, "teams", "tm.json"),
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},"
            + "\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p1\"}]}\n");
    }

    [Fact]
    public void TheMainCheckoutAndALinkedWorktreeShareOneCounterAndOneList()
    {
        var main = Path.Combine(_sandbox.Root, "wt-repo");
        var linked = Path.Combine(_sandbox.Root, "wt-linked");
        Directory.CreateDirectory(main);
        MessagePoolResolutionTests.Git(main, "init", "-q");
        MessagePoolResolutionTests.Git(main, "-c", "user.name=T", "-c", "user.email=t@example.invalid", "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-qm", "base");
        MessagePoolResolutionTests.Git(main, "worktree", "add", "-q", "-b", "member", linked);

        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, PlatformModule.CreateProcessRunner(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        var mainDir = BacklogVerb.DirOf(graph, main);

        // from the linked work tree the caller passes the pool, the main checkout's dir
        var pool = HierarchyModule.ProgressPoolOf(graph.Config.MainHierarchyDir(linked), graph.Config.HierarchyDir(linked));
        Assert.Null(graph.Hierarchy.Progress.RecordChecklist(pool, "tm", "_run", "o", [new PlanInput("fromlinked", "add", "Do fromlinked")]).Refused);
        Assert.Null(graph.Hierarchy.Progress.RecordChecklist(mainDir, "tm", "_run", "o", [new PlanInput("frommain", "add", "Do frommain")]).Refused);

        var fromLinked = Run(WorkVerb.Run, context, ["list", "--cwd", linked]);
        var fromMain = Run(WorkVerb.Run, context, ["list", "--cwd", main]);

        Assert.True(fromLinked.Code == 0, fromLinked.Err);
        Assert.Equal(fromMain.Out, fromLinked.Out);
        Assert.Equal(["wr-1", "wr-2"], Lines(fromLinked.Out).Select(FirstWord));
    }

    [Fact]
    public async Task TwoProcessesAllocateTwoHundredIdsWithNoDuplicateAndNoGap()
    {
        var binary = Path.Combine(AppContext.BaseDirectory, "ahd.dll");
        Assert.True(File.Exists(binary), $"the test host does not carry {binary}");
        AddTeamItem("seed");
        var processes = Writers.Select(name =>
        {
            var file = Path.Combine(_sandbox.Root, name + ".json");
            var rows = new JsonArray();
            foreach (var i in Enumerable.Range(0, 100))
            {
                rows.Add(new JsonObject { ["title"] = $"{name}{i}" });
            }

            File.WriteAllText(file, JsonText.Stringify(rows, 0));
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { binary, "ah", "backlog", "import", file, "--cwd", _cwd })
            {
                start.ArgumentList.Add(arg);
            }

            start.Environment.Clear();
            start.Environment["HOME"] = _sandbox.Home;
            start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            start.Environment["AH_HOME"] = _sandbox.AhHome;
            start.Environment["AH_DAEMON"] = "off";
            start.Environment["AH_TEST"] = "1";
            start.Environment["WRANGL_HIERARCHY_DIR"] = _dir;
            return Process.Start(start)!;
        }).ToList();

        foreach (var process in processes)
        {
            using (process)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                limit.CancelAfter(TimeSpan.FromMinutes(4));
                var err = process.StandardError.ReadToEndAsync(limit.Token);
                var output = process.StandardOutput.ReadToEndAsync(limit.Token);
                await process.WaitForExitAsync(limit.Token);
                await output;
                Assert.True(process.ExitCode == 0, await err);
            }
        }

        // the seed's one number, then 200 from the two writers
        Assert.Equal(201L, Scalar("SELECT COUNT(*) FROM work_items"));
        Assert.Equal(201L, Scalar("SELECT COUNT(DISTINCT n) FROM work_items"));
        Assert.Equal(1L, Scalar("SELECT MIN(n) FROM work_items"));
        Assert.Equal(201L, Scalar("SELECT MAX(n) FROM work_items"));
        Assert.Equal(201L, Scalar("SELECT seq FROM work_repos"));
    }
}
