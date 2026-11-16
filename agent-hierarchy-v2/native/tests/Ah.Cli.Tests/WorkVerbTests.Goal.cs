using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Tests;

/// <summary>ah2-53: a brief starts its footer with the team goal line, and <c>msg new --serves</c> is cleaned, never refused, and nudged for.</summary>
public sealed partial class WorkVerbTests
{
    private const string GoalLine = "Team goal, set by the user: \"ship v1\".";

    private const string Nudge = "note: team tm has a goal; say how this request serves it: --serves \"<how>\" or --serves \"off-goal: <why>\". The request was written.";

    private void SetTeamGoal(string team, string text)
    {
        using var db = StateDbModule.Open(_sandbox.AhHome, mayCreate: true);
        var result = RulesWrites.Apply(_graph, _sandbox.AhHome, db, new RuleAsk("goal", _dir, RulesOfEngagement.Team, team, RulesOfEngagement.GoalKey, text, "user", null), DateTimeOffset.UtcNow);
        Assert.True(result.Code == 0, result.Message);
    }

    private void Lead()
    {
        WriteTeamFile();
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", Path.Combine(_dir, "teams", "tm.json"), "orchestrator@tm");
    }

    private (int Code, string Out, string Err) LeadRequest(string id, params string[] more) =>
        Run(MsgVerb.Run, _context, ["new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Fix the widget title", "--id", id, "--session", "o", "--body-file", BodyFile(), .. more, "--cwd", _cwd]);

    [Fact]
    public void ABriefStartsItsFooterWithTheGoalLineWithOrWithoutAnItem()
    {
        Lead();
        SetTeamGoal("tm", "ship v1");

        // a lead's request is auto-linked to a new item unless it is an auto-tracked note, which stays without one
        var plain = LeadRequest("20261103-120000-aaaa", "--reason", HierarchyModule.AutoTrackedNoteReason);
        Assert.True(plain.Code == 0, plain.Err);
        var shown = Message("20261103-120000-aaaa");
        Assert.Contains("\n" + GoalLine, shown, StringComparison.Ordinal);
        Assert.DoesNotContain("work: ", shown, StringComparison.Ordinal);

        AddTeamItem("mine");
        var linked = LeadRequest("20261103-120001-bbbb", "--item", "mcr-1");
        Assert.True(linked.Code == 0, linked.Err);
        var withItem = Message("20261103-120001-bbbb");
        Assert.Contains(GoalLine + " Work toward it; it guides and never blocks: if work looks off-goal, say so and still do it. `ah rules goal` shows it.\nwork: mcr-1 ", withItem, StringComparison.Ordinal);

        // the negative: a response gets no goal line
        var response = Run(MsgVerb.Run, _context, ["new", "--type", "response", "--to", "orchestrator", "--from", "orchestrator", "--slug", "s", "--id", "20261103-120000-aaaa", "--team", "tm", "--cwd", _cwd]);
        Assert.True(response.Code == 0, response.Err);
        var (code, text, err) = Run(MsgVerb.Run, _context, ["show", "20261103-120000-aaaa", "response", "--cwd", _cwd, "--team", "tm"]);
        Assert.True(code == 0, err);
        Assert.DoesNotContain("Team goal", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeadRequestWithNoServesUnderAGoalIsWrittenWithANote()
    {
        Lead();
        var none = LeadRequest("20261103-120000-aaaa");
        Assert.Equal(0, none.Code);
        Assert.DoesNotContain("has a goal", none.Err, StringComparison.Ordinal);

        SetTeamGoal("tm", "ship v1");
        var bare = LeadRequest("20261103-120001-bbbb");
        Assert.Equal(0, bare.Code);
        Assert.Contains(Nudge, bare.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("serves:", Message("20261103-120001-bbbb"), StringComparison.Ordinal);

        var nullWord = LeadRequest("20261103-120002-cccc", "--serves", "null");
        Assert.Equal(0, nullWord.Code);
        Assert.Contains(Nudge, nullWord.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("serves:", Message("20261103-120002-cccc"), StringComparison.Ordinal);

        var given = LeadRequest("20261103-120003-dddd", "--serves", "off-goal: a hotfix");
        Assert.Equal(0, given.Code);
        Assert.DoesNotContain("has a goal", given.Err, StringComparison.Ordinal);
        Assert.Contains("\nserves: off-goal: a hotfix\n", Message("20261103-120003-dddd"), StringComparison.Ordinal);
    }

    [Fact]
    public void ServesLosesItsCommentMarksAndIsCutNeverRefused()
    {
        Lead();

        var hashed = LeadRequest("20261103-120000-aaaa", "--serves", "  #1 fix #12 ##now \n");
        Assert.True(hashed.Code == 0, hashed.Err);
        Assert.Contains("\nserves: 1 fix 12 now\n", Message("20261103-120000-aaaa"), StringComparison.Ordinal);

        var longer = LeadRequest("20261103-120001-bbbb", "--serves", new string('s', 200));
        Assert.Equal(0, longer.Code);
        Assert.Contains("note: --serves was cut to 160 characters", longer.Err, StringComparison.Ordinal);
        Assert.Contains("\nserves: " + new string('s', 159) + "…\n", Message("20261103-120001-bbbb"), StringComparison.Ordinal);

        // the negative: exactly 160 is kept whole and says nothing
        var exact = LeadRequest("20261103-120002-cccc", "--serves", new string('s', 160));
        Assert.DoesNotContain("was cut", exact.Err, StringComparison.Ordinal);
        Assert.Contains("\nserves: " + new string('s', 160) + "\n", Message("20261103-120002-cccc"), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeServesOnAResponseIsNotWrittenAndTheResponseIs()
    {
        Lead();
        Assert.Equal(0, LeadRequest("20261103-120000-aaaa").Code);

        var response = Run(MsgVerb.Run, _context, ["new", "--type", "response", "--to", "orchestrator", "--from", "orchestrator", "--slug", "s", "--id", "20261103-120000-aaaa", "--team", "tm", "--serves", "x", "--cwd", _cwd]);

        Assert.True(response.Code == 0, response.Err);
        Assert.Contains("note: --serves applies only to a request; not written", response.Err, StringComparison.Ordinal);
        var (code, text, err) = Run(MsgVerb.Run, _context, ["show", "20261103-120000-aaaa", "response", "--cwd", _cwd, "--team", "tm"]);
        Assert.True(code == 0, err);
        Assert.DoesNotContain("serves:", text, StringComparison.Ordinal);
    }
}
