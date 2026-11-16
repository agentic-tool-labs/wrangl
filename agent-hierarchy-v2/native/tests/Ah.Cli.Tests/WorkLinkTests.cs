using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>Links, projects and goals (ah-work-items S3, §7), against a temp home's state database through the production composition.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WorkLinkTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly CliContext _context;
    private readonly string _dir;

    public WorkLinkTests()
    {
        var cwd = Path.Combine(_sandbox.Root, "my-cool-repo");
        _dir = Path.Combine(cwd, ".claude", "ah2");
        Directory.CreateDirectory(_dir);
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = _dir, ["WRANGL_TEAM_FILE"] = Path.Combine(_dir, "teams", "tm.json"),
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private (int Code, string Out, string Err) Work(params string[] args) => Run(WorkVerb.Run, args);

    private (int Code, string Out, string Err) Backlog(params string[] args) => Run(BacklogVerb.Run, args);

    private (int Code, string Out, string Err) Run(Func<CliContext, string[], TextWriter, TextWriter, int> verb, string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = verb(_context, args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A new backlog row, in wave now unless told otherwise; its work id.</summary>
    private string Add(string title, params string[] more)
    {
        var added = Work(["add", title, .. more.Contains("--wave") || more.Contains("--kind") ? more : ["--wave", "now", .. more]]);
        Assert.True(added.Code == 0, added.Err);
        return added.Out.Trim();
    }

    private string AddTeamItem(string slug, string team = "tm")
    {
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, team, "_run", "o@" + team, [new PlanInput(slug, "add", "Do " + slug)]).Refused);
        var shown = Work("show", "_run/" + slug, "--json");
        Assert.True(shown.Code == 0, shown.Err);
        return shown.Out.Split('"')[3];
    }

    private long Scalar(string sql)
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return (long)reader.Query(sql)[0][0]!;
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

    private List<string> Ids(params string[] listArgs)
    {
        var listed = Work(["list", .. listArgs]);
        return [.. listed.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => l.Split(' ')[0])];
    }

    private static void Ok((int Code, string Out, string Err) result) => Assert.True(result.Code == 0, result.Err);

    [Fact]
    public void BothDirectionsPrintAndBlocksIsTheSameRow()
    {
        var a = Add("A");
        var b = Add("B");

        Ok(Work("link", a, "--blocked-by", b));
        Ok(Work("link", b, "--blocks", a));

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM work_links"));
        Assert.Contains($"blocked by: {b} (open)", Work("show", a).Out, StringComparison.Ordinal);
        Assert.Contains($"blocks: {a}", Work("show", b).Out, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked by:", Work("show", b).Out, StringComparison.Ordinal);
        Assert.Contains($"[blocked by {b}]", Work("list").Out, StringComparison.Ordinal);

        Ok(Work("link", a, "--blocked-by", b, "--remove"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM work_links"));
        Assert.DoesNotContain("blocked by:", Work("show", a).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelfLinkAndACycleAreRefusedNamingThePath()
    {
        var a = Add("A");
        var b = Add("B");
        var c = Add("C");
        var d = Add("D");
        var self = Work("link", a, "--blocked-by", a);
        Assert.Equal(2, self.Code);
        Assert.Contains($"{a} blocked by {a}", self.Err, StringComparison.Ordinal);
        Ok(Work("link", a, "--blocked-by", b));
        Ok(Work("link", b, "--blocked-by", c));

        var cycle = Work("link", c, "--blocked-by", a);

        Assert.Equal(2, cycle.Code);
        Assert.Contains($"{c} blocked by {a} blocked by {b} blocked by {c}", cycle.Err, StringComparison.Ordinal);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM work_links"));

        // the negative: a diamond is not a cycle
        Ok(Work("link", a, "--blocked-by", d));
        Ok(Work("link", b, "--blocked-by", d));
    }

    [Fact]
    public void ALinkNeitherChangesStateNorGatesAMove()
    {
        var a = Add("A");
        var b = Add("B");
        Ok(Work("link", a, "--blocked-by", b));

        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM plan_items WHERE state = 'not_started'"));
        Ok(Backlog("claim", "b1", "--holder", "impl-1"));
    }

    [Fact]
    public void ReadyAndBlockedFollowTheBlockersState()
    {
        var a = Add("A");
        var b = Add("B");
        var c = Add("C");
        Ok(Work("link", a, "--blocked-by", b));
        Ok(Work("link", a, "--blocked-by", c));

        Assert.Equal([b, c], Ids("--ready"));
        Assert.Equal([a], Ids("--blocked"));

        // a blocker dropped or done counts as closed
        Ok(Backlog("drop", "b2", "--note", "no"));
        Assert.Equal([a], Ids("--blocked"));
        Ok(Backlog("done", "b3", "--commit", "abc1234"));
        Assert.Empty(Ids("--blocked"));
        Assert.Equal([a], Ids("--ready"));
    }

    [Fact]
    public void BacklogDoneClosesAnUnderWayChildButKeepsAWaitingOneAndAnUnparentedSiblingAndAnUnderWayChildHoldsItsParentFromReady()
    {
        var epic = Add("Epic");
        // the default team's folder: the directory's team list always includes it
        const string team = "_default";
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, team, "_run", "o@" + team, [new PlanInput("child", "add", "Child"), new PlanInput("child", "parent") { Parent = "b1" }]).Refused);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, team, "_run", "o@" + team, [new PlanInput("sibling", "add", "Sibling")]).Refused);
        Assert.Contains(epic, Ids("--ready"));
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, team, "_run", "o@" + team, [new PlanInput("waiter", "add", "Waiter"), new PlanInput("waiter", "parent") { Parent = "b1" }, new PlanInput("waiter", "wait-user", Note: "which way?"), new PlanInput("child", "start")]).Refused);
        Assert.DoesNotContain(epic, Ids("--ready"));

        var done = Backlog("done", "b1", "--commit", "abc1234");

        Assert.True(done.Code == 0, done.Err);
        Assert.Contains("closed checklist item _run/child", done.Err, StringComparison.Ordinal);
        Assert.Contains("not closed: _run/waiter (waiting_user)", done.Err, StringComparison.Ordinal);
        string StateOf(string id) => HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _dir, team).Single(e => e.Item.Item == id).Item.State;
        Assert.Equal("done", StateOf("child"));
        Assert.Equal("waiting_user", StateOf("waiter"));
        Assert.NotEqual("done", StateOf("sibling"));
    }

    [Fact]
    public void AGoneBlockerCountsAsClosed()
    {
        var a = Add("A");
        var team = AddTeamItem("t");
        Ok(Work("link", a, "--blocked-by", team));
        Assert.Equal([a], Ids("--blocked"));

        TestProgressLogs.Drop(_sandbox.AhHome, _dir, "tm", "_run.plan.jsonl");

        Assert.Empty(Ids("--blocked"));
        Assert.Contains(a, Ids("--ready"));
        Assert.Contains($"blocked by: {team} (closed)", Work("show", a).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyLeavesOutHeldUntriagedProjectsAndGoals()
    {
        var ready = Add("Free");
        var held = Add("Held");
        Ok(Backlog("claim", "b2", "--holder", "impl-1"));
        var untriaged = Work("add", "Later on", "--size", "S").Out.Trim();
        var project = Add("P", "--kind", "project");
        var goal = Add("G", "--kind", "goal");

        var listed = Ids("--ready");

        Assert.Equal([ready], listed);
        Assert.Contains(held, Ids());
        Assert.Contains(untriaged, Ids());
        Assert.Contains(project, Ids());
        Assert.Contains(goal, Ids());
    }

    [Fact]
    public void NextIsBacklogOrderThenTeamItemsAndSkipsWhatIsNotReady()
    {
        var team = AddTeamItem("t");
        var later = Add("Later", "--wave", "later");
        var now = Add("Now", "--wave", "now");
        var project = Add("P", "--kind", "project", "--wave", "now");

        Assert.StartsWith(now + " ", Work("next").Out, StringComparison.Ordinal);
        Ok(Backlog("drop", now, "--note", "no"));
        Assert.StartsWith(later + " ", Work("next").Out, StringComparison.Ordinal);
        Ok(Backlog("drop", later, "--note", "no"));
        Assert.StartsWith(team + " ", Work("next").Out, StringComparison.Ordinal);
        Ok(Work("set", project, "--title", "Still P"));

        Assert.Equal(1, Work("next", "--under", project).Code);
    }

    [Fact]
    public void ABlockerOnATeamItemIsUnseenByBacklogNextAndSeenByWorkNext()
    {
        var a = Add("A");
        var team = AddTeamItem("t");

        Ok(Work("link", a, "--blocked-by", team));

        Assert.StartsWith("b1 ", Backlog("next").Out, StringComparison.Ordinal);
        Assert.StartsWith(team + " ", Work("next").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkBetweenBacklogRowsKeepsBacklogNextRight()
    {
        var a = Add("A");
        var b = Add("B");

        Ok(Work("link", a, "--blocked-by", b));

        Assert.Contains("\"b2\"", Backlog("show", "b1", "--json").Out.Split("depends", 2)[1], StringComparison.Ordinal);
        Assert.StartsWith("b2 ", Backlog("next").Out, StringComparison.Ordinal);

        Ok(Work("link", a, "--blocked-by", b, "--remove"));
        Assert.StartsWith("b1 ", Backlog("next").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void BacklogSetDependsReplacesTheRowsBlockers()
    {
        var a = Add("A");
        var b = Add("B");
        var c = Add("C");
        var team = AddTeamItem("t");
        Ok(Work("link", a, "--blocked-by", b));
        Ok(Work("link", a, "--blocked-by", team));

        Ok(Backlog("set", "b1", "--depends", "b3"));

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM work_links"));
        Assert.Contains($"blocked by: {c} (open)", Work("show", a).Out, StringComparison.Ordinal);

        // a set of anything else leaves the links alone
        Ok(Backlog("set", "b1", "--note", "n"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM work_links"));

        // and by work id, which the plan log stores as the row
        Ok(Backlog("set", "b1", "--depends", b));
        Assert.Contains($"blocked by: {b} (open)", Work("show", a).Out, StringComparison.Ordinal);
        Assert.Contains("\"b2\"", Backlog("show", "b1", "--json").Out.Split("depends", 2)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUseLoadsExistingDepends()
    {
        Ok(Backlog("add", "A", "--wave", "now"));
        Ok(Backlog("add", "B", "--wave", "now"));
        Ok(Backlog("set", "b1", "--depends", "b2"));
        Sql("DELETE FROM work_links", "DELETE FROM work_items", "DELETE FROM work_repos", "DELETE FROM work_prefixes");

        Assert.Equal(0, Work("list").Code);

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM work_links WHERE blocker_n = 2 AND blocked_n = 1"));
    }

    [Fact]
    public void BacklogNextSkipsOnlyProjectsAndGoals()
    {
        Add("P", "--kind", "project", "--wave", "now");
        Add("G", "--kind", "goal", "--wave", "now");
        Assert.Equal(1, Backlog("next").Code);

        Add("E", "--kind", "epic", "--wave", "now");

        Assert.StartsWith("b3 ", Backlog("next").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AShipClosesAnEpicButLeavesAProjectOrGoalOpen()
    {
        Add("P", "--kind", "project");
        Add("G", "--kind", "goal");
        Add("E", "--kind", "epic", "--wave", "now");

        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, "b1", "abc1234"));
        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, "b2", "abc1234"));
        Assert.Null(BacklogVerb.CloseShipped(_graph, _sandbox.AhHome, _dir, "b3", "abc1234"));

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM plan_items WHERE item = 'b3' AND state = 'done'"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM plan_items WHERE item IN ('b1', 'b2') AND state = 'not_started'"));
    }

    [Fact]
    public void CreatingAProjectDoesNotMakeTheGroomerDue()
    {
        Add("P", "--kind", "project");
        Add("G", "--kind", "goal");

        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM plan_items WHERE wave = 'later'"));
        Assert.Contains("untriaged: 0", Backlog("health").Out, StringComparison.Ordinal);

        // the negative: a task with no wave is still untriaged
        Ok(Work("add", "T"));
        Assert.Contains("untriaged: 1", Backlog("health").Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("goal", "project", true)]
    [InlineData("epic", "goal", true)]
    [InlineData("task", "project", true)]
    [InlineData("task", "goal", true)]
    [InlineData("project", "goal", false)]
    [InlineData("goal", "goal", false)]
    [InlineData("project", "project", false)]
    [InlineData("goal", "epic", false)]
    [InlineData("task", "task", false)]
    public void TheRankRuleCoversTheNewKinds(string kind, string parentKind, bool allowed)
    {
        var parent = Add("Parent", "--kind", parentKind, "--wave", "later");

        var added = Work("add", "Child", "--kind", kind, "--parent", parent, "--wave", "later");

        Assert.Equal(allowed ? 0 : 2, added.Code);
    }

    [Fact]
    public void AnItemFloatsAtAnyLevelAndEachMoveIsAHistoryEntry()
    {
        var project = Add("P", "--kind", "project");
        var goal = Add("G", "--kind", "goal");
        var task = Add("T");

        // at the root, directly under a project, directly under a goal with no project, and back
        Assert.Contains(task, Ids("--under", "none"));
        Ok(Work("set", task, "--parent", project));
        Assert.Contains(task, Ids("--under", project));
        Assert.DoesNotContain(task, Ids("--under", "none"));
        Ok(Work("set", task, "--parent", goal));
        Assert.Contains(task, Ids("--under", goal));
        Assert.DoesNotContain(task, Ids("--under", project));
        Ok(Work("set", task, "--parent", "none"));
        Assert.Contains(task, Ids("--under", "none"));
        Assert.DoesNotContain(task, Ids("--under", goal));

        var log = Work("log", task, "--all").Out;
        Assert.Contains("parent: b1", log, StringComparison.Ordinal);
        Assert.Contains("parent: b2", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamItemFloatsToo()
    {
        var project = Add("P", "--kind", "project");
        var team = AddTeamItem("t");

        Ok(Work("set", team, "--parent", project));

        Assert.Contains(team, Ids("--under", project));
        Assert.Contains($"parent: {project} P", Work("show", team).Out, StringComparison.Ordinal);
        Assert.Contains($"children: {team}", Work("show", project).Out, StringComparison.Ordinal);
        Ok(Work("set", team, "--parent", "none"));
        Assert.Contains(team, Ids("--under", "none"));
    }

    [Fact]
    public void ATeamItemCannotBeAParent()
    {
        var team = AddTeamItem("t");
        var row = Add("R");

        var refused = Work("set", row, "--parent", team);

        Assert.Equal(2, refused.Code);
        Assert.Contains("backlog row", refused.Err, StringComparison.Ordinal);
        Assert.Equal(2, Work("add", "X", "--parent", team).Code);
    }

    [Fact]
    public void ParentByWorkIdStoresTheBacklogId()
    {
        var project = Add("P", "--kind", "project");
        Assert.NotEqual("b1", project);

        var child = Add("C", "--parent", project);
        Ok(Backlog("add", "D", "--parent", project));

        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM plan_items WHERE doc LIKE '%\"parent\":\"b1\"%'"));
        Assert.Contains(child, Ids("--under", project));

        // the negative: a text that names no row is refused, as before
        Assert.Equal(2, Backlog("add", "E", "--parent", "nosuch").Code);
    }

    [Fact]
    public void UnderWalksToAnyDepthAndADroppedLevelLeavesItsChildren()
    {
        var project = Add("P", "--kind", "project");
        var goal = Add("G", "--kind", "goal", "--parent", project);
        var task = Add("T", "--parent", goal);
        var other = Add("Other");

        Assert.Equal([goal, task], Ids("--under", project));
        Assert.DoesNotContain(other, Ids("--under", project));

        Ok(Backlog("drop", "b2", "--note", "no"));

        Assert.Equal([task], Ids("--under", project));
        Assert.Contains($"parent: {goal} G", Work("show", task).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void SetRoutesByPlacementAndRefusesAGonePlacement()
    {
        var row = Add("Old row");
        var team = AddTeamItem("t");

        Ok(Work("set", row, "--title", "New row"));
        Ok(Work("set", team, "--title", "New team"));

        Assert.Contains("New row", Work("show", row).Out, StringComparison.Ordinal);
        Assert.Contains("New team", Work("show", team).Out, StringComparison.Ordinal);
        Assert.Equal(2, Work("set", row).Code);

        TestProgressLogs.Drop(_sandbox.AhHome, _dir, "tm", "_run.plan.jsonl");
        Assert.Equal(2, Work("set", team, "--title", "Too late").Code);
    }
}
