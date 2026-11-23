using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>ah2-512 slice 2: caps as rules rows, their sums, and the one park per cap version and run.</summary>
public sealed partial class SpendTests
{
    private const string Anchor = "20261123-100000-aaaa";

    private (int Code, string Out, string Err) Rules(params string[] args) =>
        Run((context, _, stdout, stderr) => RulesVerb.Run(context, [.. args, "--cwd", _cwd], stdout, stderr));

    private string Add(string title, params string[] more)
    {
        var added = Run((c, _, o, e) => WorkVerb.Run(c, ["add", title, .. more.Contains("--kind") ? more : ["--wave", "now", .. more], "--cwd", _cwd], o, e));
        Assert.True(added.Code == 0, added.Err);
        return added.Out.Trim();
    }

    /// <summary>An open run anchor for team <c>tm</c>.</summary>
    private void OpenRun() =>
        _ = TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(_dir, "msgs", $"{Anchor}--orchestrator--pipeline-run-anchor--request.md"),
            $"---\nid: {Anchor}\ntype: request\nto: orchestrator\nfrom: orchestrator\nteam: tm\nslug: pipeline-run-anchor\ncreated: 2026-11-23T10:00:00.000Z\n---\n\n## goal\n   run\n");

    /// <summary>The parked lines of team <c>tm</c>'s store.</summary>
    private List<JsonValue> Parked() => Run((_, g, stdout, _) =>
    {
        var home = g.Hierarchy.Messages.TeamHome(_dir, null, "tm") is { Length: > 0 } h ? h : _dir;
        Assert.True(g.Io.Files.ReadAskLog(ParkedQuestions.PathIn(home)).TryLines(out var lines));
        stdout.Write(string.Join('\n', lines));
        return 0;
    }).Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(l))).Where(l => l.Member("kind")!.Value.AsString() == "parked").ToList();

    [Fact]
    public void ATeamCapIgnoresAPreviousRunAndAProjectCapCountsAChildItemsRow()
    {
        _inTeam = true;
        Assert.Equal(0, Rules("cap", "--team", "tm", "--unit", "credits", "--max", "10").Code);
        var outcome = _db.WriteOnce(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(10), tx =>
        {
            tx.Execute("INSERT INTO spend(hier_dir, team, run, unit, micros, what, \"by\", ts_ms) VALUES (?1, 'tm', '20261101-000000-zzzz', 'credits', 50000000, 'old', 'user', 1)", HierarchyModule.WorkDirKey(_dir));
            return string.Empty;
        });
        Assert.Equal(OnceStatus.Applied, outcome.Status);
        OpenRun();

        var counted = Spend("add", "4", "credits", "--what", "a");
        Assert.True(counted.Code == 0, counted.Err);
        Assert.Equal("counted 4 credits (run total 4/10 credits).\n", counted.Out);

        var project = Add("P", "--kind", "project");
        var child = Add("C", "--parent", project);
        var outside = Add("O");
        Assert.Equal(0, Rules("cap", "--project", project, "--unit", "images", "--max", "5").Code);
        Assert.DoesNotContain("Cap reached", Spend("add", "3", "images", "--what", "a", "--item", child).Out, StringComparison.Ordinal);

        // nearest row that must not count: an item outside the project
        Assert.DoesNotContain("Cap reached", Spend("add", "3", "images", "--what", "a", "--item", outside).Out, StringComparison.Ordinal);
        var crossed = Spend("add", "3", "images", "--what", "a", "--item", child);
        Assert.Contains($"Cap reached for project {project} (6/5 images): asked the user (q1)", crossed.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowOnATaskUnderAGoalUnderAProjectCountsTowardBothCaps()
    {
        // the kind ranks put a project over a goal (ProgressPlan.KindRank)
        var project = Add("P", "--kind", "project");
        var goal = Add("G", "--kind", "goal", "--parent", project);
        var task = Add("T", "--parent", goal);
        Assert.Equal(0, Rules("cap", "--goal", goal, "--unit", "images", "--max", "2").Code);
        Assert.Equal(0, Rules("cap", "--project", project, "--unit", "images", "--max", "2").Code);

        var crossed = Spend("add", "3", "images", "--what", "a", "--item", task);

        Assert.Contains($"Cap reached for goal {goal} (3/2 images)", crossed.Out, StringComparison.Ordinal);
        Assert.Contains($"Cap reached for project {project} (3/2 images)", crossed.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberSettingACapGetsTheRulesWriteRefusalAndARepoScopeIsAUsageError()
    {
        _caller = new CutoverCaller(CutoverCallerKind.Member, "impl@tm");
        var cap = Rules("cap", "--team", "tm", "--unit", "credits", "--max", "10");
        var add = Rules("add", "--team", "tm", "--key", "k", "--text", "t");

        Assert.Equal(2, cap.Code);
        Assert.Equal(add.Err.Replace("ah rules add: ", "ah rules cap: ", StringComparison.Ordinal), cap.Err);
        Assert.Contains("only the user or the top-level Orchestrator may write rules", cap.Err, StringComparison.Ordinal);

        _caller = new CutoverCaller(CutoverCallerKind.User, "user");
        var repo = Rules("cap", "--repo", "--unit", "credits", "--max", "10");
        Assert.Equal(2, repo.Code);
        Assert.Contains("no --repo", repo.Err, StringComparison.Ordinal);

        // the nearest that must pass: a team scope from the user
        Assert.Equal(0, Rules("cap", "--team", "tm", "--unit", "credits", "--max", "10").Code);
    }

    [Fact]
    public void TwoCrossingAddsParkOneQuestionForTheOrchestratorAndAReSetParksASecond()
    {
        _inTeam = true;
        OpenRun();
        Assert.Equal(0, Rules("cap", "--team", "tm", "--unit", "credits", "--max", "5").Code);

        var first = Spend("add", "6", "credits", "--what", "a");
        Assert.Equal(0, first.Code);
        Assert.Equal("counted 6 credits (run total 6/5 credits). Cap reached for team tm (6/5 credits): asked the user (q1); calls are not stopped.\n", first.Out);
        var second = Spend("add", "1", "credits", "--what", "a");
        Assert.Equal(0, second.Code);
        Assert.Contains("already asked the user (q1); calls are not stopped.", second.Out, StringComparison.Ordinal);

        var parked = Assert.Single(Parked());
        var question = parked.Member("question")!.Value;
        Assert.Equal(SpendVerb.ParkHeader, question.Member("header")!.Value.AsString());
        Assert.Equal(["Raise the cap", "Keep going, no more asks for this cap", "Tell the team to stop paid calls"], question.Member("options")!.Value.AsArray().Select(o => o.Member("label")!.Value.AsString()));
        Assert.Equal("Keep going, no more asks for this cap", parked.Member("default")!.Value.AsString());
        Assert.Equal("orchestrator@tm", parked.Member("asked_for")!.Value.AsString());
        Assert.Equal("team tm: 6/5 credits (this run). Paid calls continue until you answer.", question.Member("question")!.Value.AsString());

        Assert.Equal(0, Rules("cap", "--team", "tm", "--unit", "credits", "--max", "5").Code);
        Assert.Contains("asked the user (q2); calls are not stopped.", Spend("add", "1", "credits", "--what", "a").Out, StringComparison.Ordinal);
        Assert.Equal(2, Parked().Count);
    }

    [Fact]
    public void TheStatusDocumentCarriesATeamsSpendWithItsCapStateAndNoKeyForATeamWithNone()
    {
        _inTeam = true;
        File.WriteAllText(Path.Combine(_dir, "teams", "other.json"), "{\"members\":[]}");
        OpenRun();
        Assert.Equal(0, Rules("cap", "--team", "tm", "--unit", "credits", "--max", "5").Code);
        Assert.Equal(0, Spend("add", "6", "credits", "--what", "a").Code);
        Assert.Equal(0, Spend("add", "3", "images", "--what", "a").Code);

        string Board(string team) => Run((_, g, stdout, _) =>
        {
            stdout.Write(JsonText.Stringify(SpendVerb.Board(g, _sandbox.AhHome, _dir, team), 0));
            return 0;
        }).Out;
        Assert.Equal(
            "[{\"unit\":\"credits\",\"total\":\"6\",\"max\":\"5\",\"scope\":\"team tm\",\"state\":\"asked q1\"},{\"unit\":\"images\",\"total\":\"3\",\"max\":null,\"scope\":\"team tm\",\"state\":null}]",
            Board("tm"));

        // nearest team that must not get entries: one in the same dir that counted nothing
        Assert.Equal("[]", Board("other"));
    }
}
