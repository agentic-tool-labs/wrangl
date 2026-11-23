using Ah.Cli.Composition;
using Ah.Cli.Verbs;

namespace Ah.Cli.Tests;

/// <summary>Milestone gates (ah2-503, adr-38): a gate is a work item of kind gate whose needs are attachments; it warns at start and never holds.</summary>
public sealed partial class WorkVerbTests
{
    private string AddItem(params string[] args)
    {
        var (code, output, err) = Work(["add", .. args, "--cwd", _cwd]);
        Assert.True(code == 0, err);
        return output.Trim();
    }

    [Fact]
    public void AGateTakesAProjectParentAndATaskParentKeepsTheRankRefusal()
    {
        var project = AddItem("Ship v1", "--kind", "project");
        Assert.Equal(0, Work("add", "Beta gate", "--kind", "gate", "--parent", project, "--cwd", _cwd).Code);

        // the negative: a task does not outrank a gate
        var task = AddItem("A task");
        var refused = Work("add", "Task gate", "--kind", "gate", "--parent", task, "--cwd", _cwd);
        Assert.Equal(2, refused.Code);
        Assert.Contains("cannot be the parent of a gate", refused.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AFillMustNameADeclaredNeed()
    {
        var gate = AddItem("Beta gate", "--kind", "gate");
        Assert.Equal(0, Work("gate", gate, "--need", "build", "--title", "the beta build", "--cwd", _cwd).Code);

        var undeclared = Work("attach", gate, "https://example.invalid/b", "--as", "nope", "--cwd", _cwd);
        Assert.Equal(2, undeclared.Code);
        Assert.Contains("declares no need nope", undeclared.Err, StringComparison.Ordinal);

        // the negative: a declared need takes the fill and the summary shows it met
        Assert.Equal(0, Work("attach", gate, "https://example.invalid/b", "--as", "build", "--cwd", _cwd).Code);
        var summary = Work("gate", gate, "--cwd", _cwd);
        Assert.True(summary.Code == 0, summary.Err);
        Assert.Contains("decided by: user\nbuild ✓ https://example.invalid/b\n", summary.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void StartingPastAnOpenGateWarnsAndLogsAndADoneGateIsSilent()
    {
        Lead();
        var project = AddItem("Ship v1", "--kind", "project");
        var gate = AddItem("Beta gate", "--kind", "gate", "--parent", project);
        Assert.Equal(0, Work("gate", gate, "--need", "build", "--need", "notes", "--cwd", _cwd).Code);
        Assert.Equal(0, Work("link", project, "--blocked-by", gate, "--cwd", _cwd).Code);
        AddTeamItem("mine");
        Assert.Equal(0, Work("set", "_run/mine", "--parent", project, "--cwd", _cwd).Code);

        var started = LeadRequest("20261123-120000-aaaa", "--item", "mine");
        Assert.True(started.Code == 0, started.Err);
        Assert.Contains($"past open gate {gate} \"Beta gate\": 0/2 needs filled (missing: build, notes); decided by user", started.Err, StringComparison.Ordinal);
        Assert.Contains("started past open gate " + gate, Work("log", "_run/mine", "--cwd", _cwd).Out, StringComparison.Ordinal);

        // the negative: once the gate is done the same start is silent
        Assert.Equal(0, Work("done", gate, "--note", "passed", "--cwd", _cwd).Code);
        AddTeamItem("two");
        Assert.Equal(0, Work("set", "_run/two", "--parent", project, "--cwd", _cwd).Code);
        var after = LeadRequest("20261123-120001-bbbb", "--item", "two");
        Assert.True(after.Code == 0, after.Err);
        Assert.DoesNotContain("past open gate", after.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void DoneOnAUserGateWithNoPassAnswerClosesItAndWarns()
    {
        var gate = AddItem("Beta gate", "--kind", "gate");
        var done = Work("done", gate, "--note", "early", "--cwd", _cwd);
        Assert.True(done.Code == 0, done.Err);
        Assert.Contains($"gate {gate} is decided by the user and has no Pass answer", done.Err, StringComparison.Ordinal);
        Assert.Contains("gate passed by", Work("log", gate, "--cwd", _cwd).Out, StringComparison.Ordinal);

        // the negative: a team-decided gate closes with no warning
        var team = AddItem("Team gate", "--kind", "gate");
        Assert.Equal(0, Work("gate", team, "--decider", "team", "--cwd", _cwd).Code);
        var teamDone = Work("done", team, "--note", "ok", "--cwd", _cwd);
        Assert.True(teamDone.Code == 0, teamDone.Err);
        Assert.DoesNotContain("has no Pass answer", teamDone.Err, StringComparison.Ordinal);
    }

    private void Report(string id, string verdict)
    {
        Assert.Equal(0, LeadRequest(id).Code);
        var body = Path.Combine(_sandbox.Root, id + ".md");
        File.WriteAllText(body, verdict + "\n");
        var response = Run(Verbs.Msg.MsgVerb.Run, _context, ["new", "--type", "response", "--to", "orchestrator", "--from", "orchestrator", "--slug", "s", "--id", id, "--team", "tm", "--body-file", body, "--cwd", _cwd]);
        Assert.True(response.Code == 0, response.Err);
    }

    [Fact]
    public void AReviewedNeedIsMetOnlyByAPassingReport()
    {
        Lead();
        var gate = AddItem("Beta gate", "--kind", "gate");
        Assert.Equal(0, Work("gate", gate, "--need", "build", "--review", "--cwd", _cwd).Code);
        Assert.Equal(0, Work("attach", gate, "https://example.invalid/b", "--as", "build", "--cwd", _cwd).Code);
        Report("20261123-120000-aaaa", "VERDICT CHANGES REQUIRED gates@abc1234");
        Assert.Equal(0, Work("attach", gate, "msg:20261123-120000-aaaa", "--as", "build", "--cwd", _cwd).Code);

        // the negative: a report that does not pass leaves the need unmet
        Assert.Contains("build ✗ [review: VERDICT CHANGES REQUIRED gates@abc1234]", Work("gate", gate, "--cwd", _cwd).Out, StringComparison.Ordinal);

        Report("20261123-120001-bbbb", "VERDICT PASS gates@abc1234");
        Assert.Equal(0, Work("attach", gate, "msg:20261123-120001-bbbb", "--as", "build", "--cwd", _cwd).Code);
        Assert.Contains("build ✓ [review: pass]", Work("gate", gate, "--cwd", _cwd).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AskParksOnceAndAPassAnswerMakesDoneSilent()
    {
        Lead();
        CliContext As(CutoverCallerKind kind) => _context with
        {
            DecisionCaller = () => new DecisionCaller(new CutoverCaller(kind, kind == CutoverCallerKind.User ? "who" : "orchestrator@tm"), [], "o", Environment.ProcessId, "start", "ttys000"),
        };
        var gate = AddItem("Beta gate", "--kind", "gate");
        var asked = Run(WorkVerb.Run, As(CutoverCallerKind.TopOrchestrator), ["gate", gate, "--ask", "--cwd", _cwd]);
        Assert.True(asked.Code == 0, asked.Err);
        Assert.Contains("parked q1 for the user", asked.Err, StringComparison.Ordinal);
        Assert.Contains("asked: q1 open", asked.Out, StringComparison.Ordinal);

        // the negative: asking again while q1 is open parks nothing
        var again = Run(WorkVerb.Run, As(CutoverCallerKind.TopOrchestrator), ["gate", gate, "--ask", "--cwd", _cwd]);
        Assert.Contains("q1 already asks the user", again.Err, StringComparison.Ordinal);

        var answer = Run(Verbs.Msg.MsgVerb.Run, As(CutoverCallerKind.User), ["decision", "answer", "q1", "Pass", "--cwd", _cwd]);
        Assert.True(answer.Code == 0, answer.Out + answer.Err);
        var done = Work("done", gate, "--note", "passed", "--cwd", _cwd);
        Assert.True(done.Code == 0, done.Err);
        Assert.DoesNotContain("has no Pass answer", done.Err, StringComparison.Ordinal);
        Assert.Contains("answered Pass", done.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void StartingPastAnOpenTaskSaysNothing()
    {
        Lead();
        var task = AddItem("Some task");
        AddTeamItem("mine");
        Assert.Equal(0, Work("link", "_run/mine", "--blocked-by", task, "--cwd", _cwd).Code);

        var started = LeadRequest("20261123-120000-aaaa", "--item", "mine");
        Assert.True(started.Code == 0, started.Err);
        Assert.DoesNotContain("past open gate", started.Err, StringComparison.Ordinal);
    }
}
