using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Commands;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah roster checklist</c> takes a work id only after its own lookup misses, so a literal slug named like one wins (ah-work-items §5.1, §11).</summary>
[Collection("RosterServices")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WorkChecklistAliasTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly FakeEnv _env;
    private readonly RosterComposition _services;
    private readonly string _cwd;
    private readonly string _dir;

    public WorkChecklistAliasTests()
    {
        _cwd = Path.Combine(_sandbox.Root, "proj");
        _dir = Path.Combine(_cwd, ".claude", "ah2");
        Directory.CreateDirectory(_dir);
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = _dir };
        _env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        _graph = ServiceGraph.Build(io, _env, new FakeRunner(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _services = new RosterComposition(_graph, io.Clock, _env);
    }

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private (int Code, string Out, string Err) Checklist(params string[] args)
    {
        CommandsModule.ComposeRoster(() => _services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(["checklist", .. args, "--cwd", _cwd, "--team", "tm", "--plain"], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string WorkShow(string id)
    {
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        var code = WorkVerb.Run(context, ["show", id, "--cwd", _cwd], stdout, stderr);
        Assert.True(code == 0, stderr.ToString());
        return stdout.ToString();
    }

    private void Add(string slug, string team = "tm") =>
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, team, "_run", "o", [new PlanInput(slug, "add", "Do " + slug)]).Refused);

    [Fact]
    public void ChecklistAddNudgesOnStderrWithStdoutAndExitUnchangedAndTheNudgedIdResolvesAtOnce()
    {
        var added = Checklist("add", "Fix the widget");

        Assert.Equal(0, added.Code);
        Assert.Equal("checklist: fix-the-widget → queued\n", added.Out);
        Assert.Equal("ah: _run/fix-the-widget has no description; add one: ah work record _run/fix-the-widget --type description --title Description --body-file - (see the write-ticket skill)\n", added.Err);

        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        var code = WorkVerb.Run(context, ["record", "_run/fix-the-widget", "--type", "description", "--title", "Description", "--body", "What is asked", "--cwd", _cwd], stdout, stderr);
        Assert.True(code == 0, stderr.ToString());
        Assert.Contains("description:\nWhat is asked\n", WorkShow("_run/fix-the-widget"), StringComparison.Ordinal);
    }

    [Fact]
    public void AChecklistTwinAddsNothingAndDoesNotNudge()
    {
        Assert.Contains("has no description", Checklist("add", "Fix the widget").Err, StringComparison.Ordinal);

        var twin = Checklist("add", "Fix the widget");

        Assert.Equal(0, twin.Code);
        Assert.Empty(twin.Err);
    }

    [Fact]
    public void ChecklistSetNoteIsNotADescription()
    {
        Assert.Equal(0, Checklist("add", "Fix the widget").Code);

        var set = Checklist("set", "fix-the-widget", "--note", "a remark");

        Assert.Equal(0, set.Code);
        Assert.DoesNotContain("has no description", set.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("description:", WorkShow("fix-the-widget"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALiteralSlugNamedLikeAWorkIdWinsInTheChecklistAndTheWorkIdWinsInAhWork()
    {
        Add("real");
        Add("pro-1");

        var checklist = Checklist("show", "pro-1", "--trail");

        Assert.True(checklist.Code == 0, checklist.Err);
        Assert.Contains("pro-1", checklist.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("real", checklist.Out, StringComparison.Ordinal);
        Assert.Contains("Do real", WorkShow("pro-1"), StringComparison.Ordinal);
    }

    [Fact]
    public void ChecklistParentTakesAWorkIdOfABacklogRowAndRefusesATeamItems()
    {
        Assert.True(_graph.Writer.Invoke("progress", HierarchyModule.BacklogWriteOp, Ah.Core.Json.JsonText.ToUtf8(new Ah.Core.Json.JsonObject { ["dir"] = _dir, ["add"] = "Row" }, 0), _graph.Hierarchy.Messages.PoolContext(_dir), new StateCallOptions(OperationId: "row")).Ok);
        Add("mine");

        var added = Checklist("add", "A child", "--parent", "pro-1");
        Assert.True(added.Code == 0, added.Err);
        Assert.Contains(HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _dir, "tm"), e => e.Item.Parent == "b1");

        // the negatives: a team item's id, and an id nothing holds
        Assert.NotEqual(0, Checklist("add", "Another", "--parent", "pro-2").Code);
        Assert.NotEqual(0, Checklist("add", "Third", "--parent", "pro-99").Code);

        // a team item whose slug reads like a backlog id is still a team item
        Add("b9");
        Assert.NotEqual(0, Checklist("add", "Fourth", "--parent", "pro-4").Code);
    }

    [Fact]
    public void AnotherTeamsIdNeverNamesAnItemOfTheSameSlugHere()
    {
        Add("shared");
        Add("shared", team: "other");

        // pro-2 is the other team's; its stream and slug also exist here, and must not be taken for it
        var theirs = Checklist("show", "pro-2", "--trail");

        Assert.NotEqual(0, theirs.Code);
        Assert.Contains("no item pro-2", theirs.Err, StringComparison.Ordinal);
        Assert.Equal(0, Checklist("show", "pro-1", "--trail").Code);
    }

    [Fact]
    public void AWorkIdNamesTheItemWhenNoSlugMatchesAndAnotherTeamsItemStaysUnknown()
    {
        Add("mine");
        Add("theirs", team: "other");

        var mine = Checklist("show", "pro-1", "--trail");
        Assert.True(mine.Code == 0, mine.Err);
        Assert.Contains("mine", mine.Out, StringComparison.Ordinal);

        // the negatives: an id in another team, and an id nothing holds, are the same miss as before
        var theirs = Checklist("show", "pro-2", "--trail");
        Assert.NotEqual(0, theirs.Code);
        Assert.Contains("no item pro-2", theirs.Err, StringComparison.Ordinal);
        Assert.NotEqual(0, Checklist("show", "pro-99", "--trail").Code);
    }

    private (int Code, string Out, string Err) Cli(Func<CliContext, string[], TextWriter, TextWriter, int> verb, params string[] args)
    {
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        var code = verb(context, [.. args, "--cwd", _cwd], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A new backlog row in wave now; its work id.</summary>
    private string BacklogRow(string title)
    {
        var added = Cli(WorkVerb.Run, "add", title, "--wave", "now");
        Assert.True(added.Code == 0, added.Err);
        return added.Out.Trim();
    }

    private PlanItem Backlog(string id)
    {
        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        return HierarchyModule.BacklogGet(reader.Query, _dir, id)!.Item;
    }

    private string? ParentOf(string title) => HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _dir, "tm").Single(e => e.Item.Title == title).Item.Parent;

    [Fact]
    public void TheLastParentedRowDoneClosesItsBacklogItemWithTheRowsCommits()
    {
        _ = BacklogRow("Ship it");
        Assert.Equal(0, Checklist("add", "First half", "--parent", "b1").Code);
        Assert.Equal(0, Checklist("add", "Second half", "--parent", "b1").Code);

        Assert.Equal(0, Checklist("done", "first-half", "--commit", "abc1234").Code);
        Assert.Equal("not_started", Backlog("b1").State);

        var done = Checklist("done", "second-half", "--commit", "def5678");

        Assert.True(done.Code == 0, done.Err);
        var item = Backlog("b1");
        Assert.Equal("done", item.State);
        Assert.Equal(["abc1234", "def5678"], item.Commits);
    }

    [Fact]
    public void AnOpenWorkRowHoldsTheBacklogItemAndAWaitingUserQuestionDoesNot()
    {
        _ = BacklogRow("Ship it");
        Assert.Equal(0, Checklist("add", "Build", "--parent", "b1").Code);
        Assert.Equal(0, Checklist("add", "Other work", "--parent", "b1").Code);
        Assert.Equal(0, Checklist("add", "Which way", "--parent", "b1").Code);
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "tm", "_run", "o", [new PlanInput("which-way", "wait-user", Note: "which way?")]).Refused);

        Assert.Equal(0, Checklist("done", "build", "--commit", "abc1234").Code);
        Assert.Equal("not_started", Backlog("b1").State);

        Assert.Equal(0, Checklist("done", "other-work", "--commit", "def5678").Code);
        Assert.Equal("done", Backlog("b1").State);
        Assert.Equal("waiting_user", HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _dir, "tm").Single(e => e.Item.Item == "which-way").Item.State);
    }

    [Fact]
    public void AnOpenBacklogSubItemHoldsTheAutomaticCloseUntilItIsDroppedAndARowThenCloses()
    {
        Assert.Equal(0, Cli(WorkVerb.Run, "add", "Epic", "--kind", "epic", "--wave", "now").Code);
        var sub = Cli(WorkVerb.Run, "add", "Sub", "--wave", "now", "--parent", "b1");
        Assert.True(sub.Code == 0, sub.Err);
        Assert.Equal(0, Checklist("add", "Build", "--parent", "b1").Code);

        Assert.Equal(0, Checklist("done", "build", "--commit", "abc1234").Code);
        Assert.Equal("not_started", Backlog("b1").State);

        Assert.Equal(0, Cli(BacklogVerb.Run, "drop", "b2", "--note", "not needed").Code);
        Assert.Equal(0, Checklist("add", "Finish", "--parent", "b1").Code);
        Assert.Equal(0, Checklist("done", "finish", "--commit", "def5678").Code);

        Assert.Equal("done", Backlog("b1").State);
    }

    [Fact]
    public void ClosingTheLastSubItemClosesTheParentAndNegativeAnOpenRowOrAnotherOpenSubItemHoldsIt()
    {
        Assert.Equal(0, Cli(WorkVerb.Run, "add", "Epic", "--kind", "epic", "--wave", "now").Code);
        Assert.Equal(0, Cli(WorkVerb.Run, "add", "Sub", "--wave", "now", "--parent", "b1").Code);
        Assert.Equal(0, Checklist("add", "Own", "--parent", "b1").Code);

        // the parent's own open row holds it
        Assert.Equal(0, Cli(BacklogVerb.Run, "done", "b2", "--commit", "abc1234").Code);
        Assert.Equal("not_started", Backlog("b1").State);

        Assert.Equal(0, Cli(WorkVerb.Run, "add", "Epic2", "--kind", "epic", "--wave", "now").Code);
        Assert.Equal(0, Cli(WorkVerb.Run, "add", "A", "--wave", "now", "--parent", "b3").Code);
        Assert.Equal(0, Cli(WorkVerb.Run, "add", "B", "--wave", "now", "--parent", "b3").Code);

        // another open sub-item holds it
        Assert.Equal(0, Cli(BacklogVerb.Run, "done", "b4", "--commit", "def5678").Code);
        Assert.Equal("not_started", Backlog("b3").State);

        // the last one closes it, with that sub-item's commit and a note naming it
        Assert.Equal(0, Cli(BacklogVerb.Run, "done", "b5", "--commit", "fed8765").Code);
        var parent = Backlog("b3");
        Assert.Equal("done", parent.State);
        Assert.Equal(["fed8765"], parent.Commits);
        Assert.Contains("closed with its last sub-item b5", parent.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWithNoParentClosesNoBacklogItem()
    {
        _ = BacklogRow("Ship it");
        Assert.Equal(0, Checklist("add", "Loose").Code);

        Assert.Equal(0, Checklist("done", "loose", "--commit", "abc1234").Code);

        Assert.Equal("not_started", Backlog("b1").State);
    }

    [Fact]
    public void ATitleEndingInAWorkIdOrABacklogIdLinksTheRow()
    {
        var id = BacklogRow("Ship it");
        _ = BacklogRow("Ship more");

        Assert.Equal(0, Checklist("add", $"Build it [{id}]").Code);
        Assert.Equal(0, Checklist("add", "Build more [b2]").Code);

        Assert.Equal("b1", ParentOf($"Build it [{id}]"));
        Assert.Equal("b2", ParentOf("Build more [b2]"));
    }

    [Theory]
    [InlineData("Build it [pro-99]")]
    [InlineData("Build it [b99]")]
    [InlineData("Build [b1] it")]
    [InlineData("Build it b1")]
    public void AnUnknownOrMisplacedIdLinksNothingAndIsNoError(string title)
    {
        _ = BacklogRow("Ship it");

        var added = Checklist("add", title);

        Assert.True(added.Code == 0, added.Err);
        Assert.Null(ParentOf(title));
    }

    [Fact]
    public void AnExplicitParentWinsOverTheIdInTheTitle()
    {
        _ = BacklogRow("Ship it");
        _ = BacklogRow("Ship more");

        Assert.Equal(0, Checklist("add", "Build it [b1]", "--parent", "b2").Code);

        Assert.Equal("b2", ParentOf("Build it [b1]"));
    }

    [Fact]
    public void BacklogDoneWithAnOpenQuestionClosesTheItemAndNamesTheQuestion()
    {
        _ = BacklogRow("Ship it");
        // the default team's folder: backlog done reads the directory's teams, and it always includes this one
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "_default", "_run", "o", [new PlanInput("which-way", "add", "Which way"), new PlanInput("which-way", "parent") { Parent = "b1" }, new PlanInput("which-way", "wait-user", Note: "which way?")]).Refused);

        var done = Cli(BacklogVerb.Run, "done", "b1", "--commit", "abc1234");

        Assert.True(done.Code == 0, done.Err);
        Assert.Equal("done", Backlog("b1").State);
        Assert.Single(done.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l == "backlog done: b1 is done; left open under it: _run/which-way (waiting_user)");
        Assert.Equal("waiting_user", HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, _dir, "_default").Single(e => e.Item.Item == "which-way").Item.State);
    }
}
