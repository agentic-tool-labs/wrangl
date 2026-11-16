using Ah.Core.Json;
using Ah.Tui.Adapters;
using Ah.Tui.Core;
using static Ah.Tui.Tests.ScreenFixtures;

namespace Ah.Tui.Tests;

/// <summary>The three screens as functions of slices, state and size, with their goldens; each row of the spec's tables has a case here.</summary>
public sealed class TuiScreensTests
{
    private static readonly TuiScreenState OnTeam = TuiScreens.OpenNamed(TuiScreenState.Initial, Slices(), "ah2-tm4")!;

    private static TuiScreenState OnAgent(string member) => OnTeam with { Depth = 2, Agents = new TuiLevel(member) };

    private static TuiFrame Frame(TuiScreenState s, int cols = 100, int? rows = 30, IReadOnlyDictionary<string, JsonValue>? slices = null) =>
        TuiScreens.Frame(s, Input(slices), cols, rows);

    private static Dictionary<string, JsonValue> Without(params string[] ids)
    {
        var slices = Slices();
        foreach (var id in ids)
        {
            slices.Remove(id);
        }

        return slices;
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    [InlineData(40, 10)]
    public void TheGoldensOfEachScreen(int cols, int rows)
    {
        TreeFixtures.Golden($"screen-teams-{cols}x{rows}.txt", Text(Frame(TuiScreenState.Initial, cols, rows)));
        TreeFixtures.Golden($"screen-team-{cols}x{rows}.txt", Text(Frame(OnTeam, cols, rows)));
        TreeFixtures.Golden($"screen-agent-{cols}x{rows}.txt", Text(Frame(OnAgent(Impl), cols, rows)));
    }

    [Fact]
    public void TheGoldensOfOneFrameAndOfLoading()
    {
        TreeFixtures.Golden("screen-once-teams.txt", Text(Frame(TuiScreenState.Initial, 100, null)));
        TreeFixtures.Golden("screen-once-team.txt", Text(Frame(OnTeam, 100, null)));
        TreeFixtures.Golden("screen-loading.txt", Text(Frame(TuiScreenState.Initial, 80, 24, new Dictionary<string, JsonValue>())));
        TreeFixtures.Golden("screen-down.txt", Text(TuiScreens.Frame(TuiScreenState.Initial, new TuiScreenInput(new Dictionary<string, JsonValue>(), Now, TimeZoneInfo.Utc, Down: true), 80, 24)));
    }

    [Fact]
    public void TeamsAreInTheOrderTheyAreBuiltWithALaneIndentedUnderItsParent()
    {
        var text = Text(Frame(TuiScreenState.Initial));
        var lines = text.Split('\n');
        Assert.Equal(1, LineOf(Frame(TuiScreenState.Initial), "TEAM"));
        Assert.StartsWith("▸ ah2-tm4 ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("    ah2-tm4-tui ", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("  engram ", lines[4], StringComparison.Ordinal);
    }

    [Fact]
    public void AParentRowKeepsTheRollupOfItsLanes()
    {
        var row = Frame(TuiScreenState.Initial).Lines[2].Text;
        Assert.Matches(@"ah2-tm4\s+■ needs you( \S+)?\s+1\s+3\s+1\s+7/12", row);
    }

    [Fact]
    public void AHeldTeamReadsHeldAndAPlanNotReadShowsNoNeedsCount()
    {
        var row = Frame(TuiScreenState.Initial).Lines[4].Text;
        Assert.Contains("‖ Held", row, StringComparison.Ordinal);
        Assert.Matches(@"Held\s+–\s+0", row);
    }

    [Fact]
    public void NameFallsBackToTheTeamRecordThenTheId()
    {
        var slices = Without("team-summary:d1/engram");
        Assert.Contains("engram", Text(Frame(TuiScreenState.Initial, slices: slices)), StringComparison.Ordinal);
        Assert.Equal("engram", TeamSlices.NameOf(Without("team-summary:d1/engram", Other), Other));
    }

    [Fact]
    public void AnOrchestratorAudienceAlertShowsOnTheTeamScreen()
    {
        var slices = Slices();
        slices["team-summary:d1/ah2-tm4"] = J($$"""{"name":"ah2-tm4","state":"idle","plan_loaded":true,"alerts":[{"kind":"stuck","audience":"orchestrator","member":"{{Impl}}","detail":"No word on this exchange","since":"{{Now.AddMinutes(-12):O}}"}]}""");
        var text = Text(Frame(OnTeam, slices: slices));
        Assert.Contains("FOR THE ORCHESTRATOR · NOT COUNTED", text, StringComparison.Ordinal);
        Assert.Contains("No word on this exchange", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAUserAlertIsNotListedForTheOrchestrator() =>
        Assert.DoesNotContain("FOR THE ORCHESTRATOR", Text(Frame(OnTeam)), StringComparison.Ordinal);

    [Fact]
    public void AnEndedTeamIsHiddenUntilE()
    {
        Assert.DoesNotContain("old", Text(Frame(TuiScreenState.Initial)), StringComparison.Ordinal);
        Assert.Contains("old", Text(Frame(Act(TuiScreenState.Initial, "e"))), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeEOnAnotherScreenDoesNothing() =>
        Assert.False(Act(OnTeam, "e").Ended);

    [Fact]
    public void TheHeaderSaysHowManyAreLiveAndHowOldTheDataIs() =>
        Assert.Contains("3 live · updated 4s ago", Frame(TuiScreenState.Initial).Lines[0].Text, StringComparison.Ordinal);

    [Fact]
    public void NoTeamsSaysWhatToDo()
    {
        var slices = new Dictionary<string, JsonValue>(StringComparer.Ordinal) { ["list:teams"] = J("""{"teams":[]}""") };
        Assert.Contains(TuiScreens.NoTeams, Text(Frame(TuiScreenState.Initial, slices: slices)), StringComparison.Ordinal);
    }

    [Fact]
    public void AgentsListOrchestratorFirstWithStatusSinceLivenessAndItem()
    {
        var f = Frame(OnTeam);
        Assert.StartsWith("AGENTS", f.Lines[1].Text, StringComparison.Ordinal);
        Assert.Matches(@"^▸ orchestrator\s+● working 2m\s+live\s+Ship train 22", f.Lines[2].Text);
        Assert.Matches(@"^  impl-41\s+● working 8m\s+live\s+Trim the terminal UI", f.Lines[3].Text);
        Assert.Matches(@"^  reviewer-9\s+× ended 1h\s+gone\s+Review the trim", f.Lines[4].Text);
        Assert.Equal([Orch, Impl, Reviewer], f.RowIds.Take(3));
    }

    [Fact]
    public void TheItemIsTheDaemonsTitleElseTheOpenExchanges()
    {
        Assert.Contains("Ship train 22", Text(Frame(OnTeam)), StringComparison.Ordinal);
        Assert.Contains("Trim the terminal UI", Frame(OnTeam).Lines[3].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSitrepStacksCompletedInProgressUpNextThenTheRestWithRowsThatAreOnlyTitles()
    {
        var f = Frame(OnTeam, 120, 40);
        int[] order = [LineOf(f, "[-] Completed 0 of 5"), LineOf(f, "[-] In progress 1"), LineOf(f, "[-] Up next 0"), LineOf(f, "[-] Waiting on you 1"), LineOf(f, "[+] Report in, no one on it 1")];
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Equal("    Trim the terminal UI", f.Lines[SitrepLine(f, "Trim the terminal UI")].Text);
    }

    /// <summary>The line below SITREP that shows <paramref name="text"/>: an agent's row can show the same words.</summary>
    private static int SitrepLine(TuiFrame frame, string text) =>
        frame.Lines.ToList().FindIndex(LineOf(frame, "SITREP"), l => l.Text.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void AnEmptyRegionOfTheThreeShowsItsZeroAndNegativeAnEmptyOtherIsNotShown()
    {
        var text = Text(Frame(OnTeam, 120, 40));
        Assert.Contains("Up next 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Blocked 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsNotMovingStartsFoldedAndNegativeWaitingOnYouStartsOpen()
    {
        var text = Text(Frame(OnTeam, 120, 40));
        Assert.DoesNotContain("\n    Report in\n", text, StringComparison.Ordinal);
        Assert.Contains("Pick the DB migration order", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AClickAndEnterOnAHeadingFoldItAndNegativeNeitherLeavesTheScreen()
    {
        var f = Frame(OnTeam, 120, 40);
        var clicked = Act(OnTeam, Click(3, LineOf(f, "[-] In progress 1")), cols: 120, rows: 40);
        var stop = f.RowIds.ToList().IndexOf(SitrepView.RegionStop + "in_progress");
        var keyed = Act(OnTeam, string.Concat(Enumerable.Repeat("j", stop)) + "\r", cols: 120, rows: 40);
        foreach (var s in new[] { clicked, keyed })
        {
            var text = Text(Frame(s, 120, 40));
            Assert.Contains("[+] In progress 1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\n    Trim the terminal UI\n", text, StringComparison.Ordinal);
            Assert.Equal(1, s.Depth);
            Assert.Null(s.Detail);
            Assert.Contains("[+] In progress 1", Text(Frame(Act(s, "\u001b\r"), 120, 40)), StringComparison.Ordinal);
            Assert.Contains("[-] In progress 1", Text(Frame(Act(s, Click(3, LineOf(Frame(s, 120, 40), "[+] In progress 1")), cols: 120, rows: 40), 120, 40)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFoldedStartOpensOnAClickAndNegativeItsNeighbourStaysFolded()
    {
        var slices = Slices();
        slices["sitrep:d1/ah2-tm4"] = Sr("""{"not_moving":[{"key":"s:n","title":"Stopped","stream":"s"}],"not_moving_total":1,"stale":[{"key":"s:o","title":"Idle one","stream":"s"}],"stale_total":1}""");
        var f = Frame(OnTeam, 120, 40, slices);
        Assert.Contains("[+] Stale 1", Text(f), StringComparison.Ordinal);
        var open = Act(OnTeam, Click(3, LineOf(f, "[+] Report in")), slices, 120, 40);
        var text = Text(Frame(open, 120, 40, slices));
        Assert.Contains("Stopped", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Idle one", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASitrepRowOpensTheSameDialogByClickAndByEnter()
    {
        var slices = Slices();
        slices["sitrep:d1/ah2-tm4"] = Sr("""{"in_progress":[{"key":"tui-trim:at-14","title":"Trim the terminal UI","team":"ah2-tm4","stream":"tui-trim","state":"in_progress"}],"in_progress_total":1}""");
        var f = Frame(OnTeam, 120, 40, slices);
        var clicked = Act(OnTeam, Click(6, SitrepLine(f, "Trim the terminal UI")), slices, 120, 40);
        var stop = f.RowIds.ToList().FindIndex(static id => id.StartsWith(SitrepView.ItemStop, StringComparison.Ordinal));
        var keyed = Act(OnTeam, string.Concat(Enumerable.Repeat("j", stop)) + "\r", slices, 120, 40);
        foreach (var s in new[] { clicked, keyed })
        {
            Assert.Equal(Team + "|tui-trim|at-14", s.Detail);
            Assert.Contains("State   ▶ in progress", Text(Frame(s, 120, 40, slices)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABacklogRowOpensFromTheBacklogSlice()
    {
        var slices = Slices();
        slices["sitrep:d1/ah2-tm4"] = Sr("""{"up_next":[{"key":"b7","title":"Backlog item","source":"backlog"}]}""");
        var f = Frame(OnTeam, 120, 40, slices);
        var s = Act(OnTeam, Click(6, SitrepLine(f, "Backlog item")), slices, 120, 40);
        Assert.Equal("bl:d1/b7", s.Detail);
        Assert.Contains("backlog:d1", TuiScreens.Wanted(s, slices, Now));
        Assert.Contains("Holder  ah2-tm4-impl-41", Text(Frame(s, 120, 40, slices)), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeABodyServingNoRegionsDrawsNoHeadingEvenWithRows()
    {
        var slices = Slices();
        slices["sitrep:d1/ah2-tm4"] = J("""{"in_progress":[{"key":"p","title":"Unlisted row","state":"in_progress"}],"in_progress_total":1}""");
        var text = Text(Frame(OnTeam, 120, 40, slices));
        Assert.DoesNotContain("In progress", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unlisted row", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheOldHiddenSectionsKeyIsGone() =>
        Assert.Equal(OnTeam, Act(OnTeam, "x"));

    [Fact]
    public void WhatLeftTheViewIsCountedSinceTheTeamLastClosed()
    {
        var left = Act(OnTeam, "\u001b", Slices());
        var later = Slices();
        later["sitrep:d1/ah2-tm4"] = Sr("""{"as_of":1,"waiting_on_you":[{"key":"w9","title":"A new one","since":1}],"in_progress":[{"key":"w1","title":"Moved on","state":"in_progress","since":1}]}""");
        var back = Act(left, "\r", later);
        Assert.Contains("left the view", Frame(back, 120, slices: later).Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAFirstVisitCountsNothingLeft() =>
        Assert.DoesNotContain("left the view", Text(Frame(OnTeam, 120)), StringComparison.Ordinal);

    [Fact]
    public void ALaneTeamHasItsAgentsAndNoSitrep()
    {
        var lane = TuiScreens.OpenNamed(TuiScreenState.Initial, Slices(), "ah2-tm4-tui")!;
        Assert.Contains(SitrepView.PerTopLevelTeam, Text(Frame(lane)), StringComparison.Ordinal);
        Assert.DoesNotContain(TuiScreens.Wanted(lane, Slices(), Now), id => id.StartsWith("sitrep:", StringComparison.Ordinal));
    }

    [Fact]
    public void ASliceNotYetArrivedReadsLoadingInThatSection()
    {
        var noSitrep = Frame(OnTeam, slices: Without("sitrep:d1/ah2-tm4"));
        Assert.Contains("Ship train 22", Text(noSitrep), StringComparison.Ordinal);
        Assert.Equal(TuiScreens.Loading, noSitrep.Lines[LineOf(noSitrep, "SITREP") + 1].Text);

        var noTeam = Frame(OnTeam, slices: Without(Team));
        Assert.Equal(TuiScreens.Loading, noTeam.Lines[LineOf(noTeam, "AGENTS") + 1].Text);

        var noItems = Frame(OnAgent(Impl), slices: Without("plan-items:d1/ah2-tm4/main"));
        Assert.Equal(TuiScreens.Loading, noItems.Lines[LineOf(noItems, "WORK ITEMS") + 1].Text);
        Assert.Contains(TuiScreens.Loading, Text(Frame(OnAgent(Impl), slices: Without("plan:d1/ah2-tm4"))), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentHeaderIsTheDaemonsMemberState() =>
        Assert.Contains("● working 8m · live · tool Edit · tui-trim", Frame(OnAgent(Impl)).Lines[0].Text, StringComparison.Ordinal);

    [Fact]
    public void AnAgentsItemsHeldByNameOrIdInProgressFirstThenNewest()
    {
        var f = Frame(OnAgent(Impl));
        Assert.Equal(["tui-trim|at-14", "tui-trim|at-15", "main|at-12"], f.RowIds);
        Assert.Matches(@"^▸ at-14\s+▶ in progress\s+Trim the terminal UI\s+8m", f.Lines[2].Text);
        Assert.Matches(@"^  at-12\s+✓ done\s+Fix message age\s+2h", f.Lines[4].Text);
    }

    [Fact]
    public void NegativeSomeoneElsesItemsAndTheBacklogAreNotListed()
    {
        var text = Text(Frame(OnAgent(Impl)));
        Assert.DoesNotContain("Someone else's", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Backlog item", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentHoldingNothingSaysSo() =>
        Assert.Contains(TuiScreens.NoItems, Text(Frame(OnAgent(Orch))), StringComparison.Ordinal);

    [Fact]
    public void AnAgentOfATeamWithNoPlanHoldsNothingOnceAReplyLeftThePlanOut()
    {
        var slices = Slices();
        var plan = TeamSlices.PlanIds(slices, OnTeam.Teams.Id!)[0];
        Assert.True(slices.Remove(plan));
        string Shown(Func<IEnumerable<string>, bool>? answered) => Text(TuiScreens.Frame(OnAgent(Impl), Input(slices) with { Answered = answered }, 100, 30));

        Assert.Contains(TuiScreens.NoItems, Shown(ids => ids.Contains(plan)), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAPlanNotYetAnsweredIsStillLoading()
    {
        var slices = Slices();
        Assert.True(slices.Remove(TeamSlices.PlanIds(slices, OnTeam.Teams.Id!)[0]));
        foreach (var answered in new Func<IEnumerable<string>, bool>?[] { null, static _ => false })
        {
            var text = Text(TuiScreens.Frame(OnAgent(Impl), Input(slices) with { Answered = answered }, 100, 30));
            Assert.Contains("loading", text, StringComparison.Ordinal);
            Assert.DoesNotContain(TuiScreens.NoItems, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NegativeAHeldPlanIsListedHoweverItWasAnswered()
    {
        var text = Text(TuiScreens.Frame(OnAgent(Impl), Input() with { Answered = static _ => true }, 100, 30));
        Assert.DoesNotContain(TuiScreens.NoItems, text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLogAreaBeforeTheItemIdsArrive()
    {
        var f = Text(Frame(Open14));
        Assert.DoesNotContain("LOG", f, StringComparison.Ordinal);
        Assert.Contains("at-14", f, StringComparison.Ordinal);
    }

    private const string Log14 = "item-log:d1/14";
    private const string Log15 = "item-log:d1/15";

    private static TuiScreenState Open14 => OnAgent(Impl) with { Detail = Team + "|tui-trim|at-14" };

    private static long Ms(int minutesAgo) => Now.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds();

    /// <summary>The fixture's items with work ids (at-12 has none, as an item not migrated), and at-14's log in the shape of <c>ItemLogLaneTests</c>' lane.</summary>
    private static Dictionary<string, JsonValue> WithLogs(string workId14 = "atp-14")
    {
        var slices = Slices();
        slices["plan-items:d1/ah2-tm4/tui-trim"] = J($$"""
            {"items":[{"key":"at-14","work_id":"{{workId14}}","title":"Trim the terminal UI","state":"in_progress","holder":"{{Impl}}","state_since":"{{Now.AddMinutes(-8):O}}"},
                      {"key":"at-15","work_id":"atp-15","title":"Also trim the docs","state":"in_progress","holder":"ah2-tm4-impl-41","state_since":"{{Now.AddMinutes(-20):O}}"}]}
            """);
        slices[Log14] = J($$"""
            {"id":"atp-14","more":3,
             "entries":[{"ts_ms":{{Ms(59)}},"type":"assignment","title":"Assigned to impl-41","by":"orchestrator","body":"the entry's body"},
                        {"ts_ms":{{Ms(56)}},"type":"note","title":"Piece 1 started","by":"impl-41","body":""}],
             "links":{"blocked_by":["atp-9"],"blocks":["atp-20"]},
             "attachments":[{"ref":"a","kind":"url"},{"ref":"b","kind":"url"}],"children":[]}
            """);
        slices[Log15] = J($$"""{"id":"atp-15","more":0,"entries":[{"ts_ms":{{Ms(5)}},"type":"note","title":"Docs started","by":"impl-41","body":""}],"links":{"blocked_by":[],"blocks":[]},"attachments":[],"children":[]}""");
        return slices;
    }

    private static Dictionary<string, JsonValue> WithDescription(string? description)
    {
        var slices = WithLogs();
        var escaped = description is null ? "null" : JsonText.Stringify(description, 0);
        slices[Log14] = J($$"""{"id":"atp-14","description":{{escaped}},"more":0,"entries":[],"links":{"blocked_by":[],"blocks":[]},"attachments":[],"children":[]}""");
        return slices;
    }

    [Fact]
    public void ThePopupShowsTheDescriptionAboveTheLogPlainAndStrippedOfControlCodes()
    {
        var f = Frame(Open14, slices: WithDescription("**Summary**\n\nFix \u001b[31mit\u001b[0m, a *little*\u0007\n"));

        var heading = LineOf(f, "DESCRIPTION");
        Assert.Contains("│ Summary ", f.Lines[heading + 1].Text, StringComparison.Ordinal);
        Assert.Contains("│ Fix it, a *little* ", f.Lines[heading + 2].Text, StringComparison.Ordinal);
        Assert.Equal(heading + 4, LineOf(f, "LOG  atp-14"));
        Assert.DoesNotContain("\u001b", Text(f), StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", Text(f), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePopupShowsALongDescriptionWholeAndScrollsToItsEnd()
    {
        var slices = WithDescription(string.Join('\n', Enumerable.Range(1, 40).Select(static i => "line " + i)) + "\n" + new string('w', 100));

        var tall = Text(Frame(Open14, rows: 80, slices: slices));
        foreach (var i in Enumerable.Range(1, 40))
        {
            Assert.Contains("│ line " + i + " ", tall, StringComparison.Ordinal);
        }

        Assert.Contains("│ " + new string('w', 76) + " │", tall, StringComparison.Ordinal);
        Assert.Contains("│ " + new string('w', 24) + " ", tall, StringComparison.Ordinal);
        Assert.DoesNotContain("ah work show", tall, StringComparison.Ordinal);

        var end = Text(Frame(Act(Open14, string.Concat(Enumerable.Repeat("j", 60)), slices), slices: slices));
        Assert.Contains("│ line 40 ", end, StringComparison.Ordinal);
        Assert.DoesNotContain("│ line 1 ", end, StringComparison.Ordinal);
    }

    [Fact]
    public void ASitrepRowOpensThePopupWithTheItemsDescription()
    {
        var slices = WithDescription("Why this item matters");
        slices["sitrep:d1/ah2-tm4"] = Sr("""{"in_progress":[{"key":"tui-trim:at-14","title":"Trim the terminal UI","team":"ah2-tm4","stream":"tui-trim","state":"in_progress"}],"in_progress_total":1}""");

        var s = Act(OnTeam, Click(6, SitrepLine(Frame(OnTeam, 120, 40, slices), "Trim the terminal UI")), slices, 120, 40);

        Assert.Contains(Log14, TuiScreens.Wanted(s, slices, Now));
        var f = Frame(s, 120, 40, slices);
        Assert.Equal(LineOf(f, "DESCRIPTION") + 1, LineOf(f, "│ Why this item matters "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NegativeAnItemWithNoDescriptionHasNoHeading(string? description) =>
        Assert.DoesNotContain("DESCRIPTION", Text(Frame(Open14, slices: WithDescription(description))), StringComparison.Ordinal);

    [Fact]
    public void LogOfReadsTheDescriptionAsPlainLines()
    {
        var log = TeamSlices.LogOf(WithDescription("**A**\n\nb\tc")[Log14], Now, TimeZoneInfo.Utc);
        Assert.Equal(["A", "b c"], log.Description);

        Assert.Empty(TeamSlices.LogOf(WithDescription(null)[Log14], Now, TimeZoneInfo.Utc).Description);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(40, 10)]
    public void TheGoldensOfAnItemsLog(int cols, int rows) =>
        TreeFixtures.Golden($"screen-agent-log-{cols}x{rows}.txt", Text(Frame(Open14, cols, rows, WithLogs())));

    [Fact]
    public void TheDialogListsTheItemsLogOldestFirstWithItsIdMoreAndFacts()
    {
        var f = Frame(Open14, 120, slices: WithLogs());
        var text = Text(f);
        Assert.Matches(@"│ LOG  atp-14\s+3 older not shown\s+│", text);
        Assert.True(LineOf(f, "Assigned to impl-41") < LineOf(f, "Piece 1 started"));
        Assert.Matches(@"│ 04:01\s+assignment\s+Assigned to impl-41\s+orchestrator\s+│", text);
        Assert.Contains("blocked by atp-9 · blocks atp-20 · 2 attachments", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the entry's body", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Docs started", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentPageListsItemsWithNoLogAndNegativeTheDialogIsNotOpenUntilAsked()
    {
        var f = Frame(OnAgent(Impl), slices: WithLogs());
        Assert.DoesNotContain("LOG", Text(f), StringComparison.Ordinal);
        Assert.Null(f.Box);
        Assert.Contains("atp-14", Text(f), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialogShowsStateHolderAndNoteCentredOverTheView()
    {
        var slices = WithLogs();
        slices["plan-items:d1/ah2-tm4/tui-trim"] = J($$"""{"items":[{"key":"at-14","work_id":"atp-14","title":"Trim the terminal UI","state":"blocked","holder":"{{Impl}}","note":"waits on the build"}]}""");
        var f = Frame(Open14, 100, 30, slices);
        var text = Text(f);
        Assert.Contains("State   ‖ blocked", text, StringComparison.Ordinal);
        Assert.Contains("Holder  impl-41", text, StringComparison.Ordinal);
        Assert.Contains("waits on the build", text, StringComparison.Ordinal);
        Assert.Equal(new TuiBox(10, 3, 90, 27, 85), f.Box);
        Assert.StartsWith("ah ›", f.Lines[0].Text, StringComparison.Ordinal);
        Assert.Equal('┌', f.Lines[3].Text[10]);
        Assert.Equal('┐', f.Lines[3].Text[89]);
        Assert.Equal('└', f.Lines[26].Text[10]);
    }

    [Fact]
    public void NegativeAnItemWithNoNoteHasNoNoteHeading() =>
        Assert.DoesNotContain("NOTE", Text(Frame(Open14, slices: WithLogs())), StringComparison.Ordinal);

    [Fact]
    public void EscTheCloseControlAndAClickOutsideCloseTheDialogAndTheRowStaysChosen()
    {
        var slices = WithLogs();
        var opened = Act(OnAgent(Impl), "j\r", slices);
        Assert.NotNull(opened.Detail);
        var box = Frame(opened, slices: slices).Box!;
        foreach (var closing in new[] { "\u001b", Click(box.CloseX + 1, box.Y0), Click(box.X0 - 1, box.Y0), Click(box.X0 + 1, box.Y1) })
        {
            var closed = Act(opened, closing, slices);
            Assert.Null(closed.Detail);
            Assert.Equal((2, "tui-trim|at-15"), (closed.Depth, closed.Items.Id));
        }
    }

    [Fact]
    public void NegativeAClickInsideTheDialogAndOnItsTitleLeaveItOpenAndKeysDoNotReachTheViewBehind()
    {
        var box = Frame(Open14, slices: WithLogs()).Box!;
        Assert.NotNull(Act(Open14, Click(box.X0 + 3, box.Y0 + 3), WithLogs()).Detail);
        Assert.NotNull(Act(Open14, Click(box.CloseX - 1, box.Y0), WithLogs()).Detail);
        Assert.Equal(Open14.Items.Index, Act(Open14, "j", WithLogs()).Items.Index);
        Assert.Equal(Open14.Depth, Act(Open14, "\u001b", WithLogs()).Depth);
    }

    [Fact]
    public void NegativeWithNoDialogAClickOnARowDoesNotClose()
    {
        var f = Frame(OnAgent(Impl), slices: WithLogs());
        Assert.Null(Act(OnAgent(Impl), Click(0, f.Lines.Count - 2), WithLogs()).Detail);
    }

    [Fact]
    public void NegativeNoMoreAndNoFactsLeaveNoLineForThem()
    {
        var s = OnAgent(Impl) with { Items = new TuiLevel("tui-trim|at-15"), Detail = Team + "|tui-trim|at-15" };
        var text = Text(Frame(s, slices: WithLogs()));
        Assert.Contains("Docs started", text, StringComparison.Ordinal);
        Assert.DoesNotContain("older not shown", text, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked by", text, StringComparison.Ordinal);
        Assert.DoesNotContain("attachment", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullLogBodySaysThereIsNoLogAndNegativeAMissingOneIsLoadingUntilItSettles()
    {
        var slices = WithLogs();
        slices[Log14] = J("null");
        Assert.Contains(TuiScreens.NoLog, Text(Frame(Open14, slices: slices)), StringComparison.Ordinal);

        Assert.True(slices.Remove(Log14));
        var loading = Frame(Open14, slices: slices);
        Assert.Contains(TuiScreens.Loading, loading.Lines[LineOf(loading, "LOG  atp-14") + 1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(TuiScreens.NoLog, Text(loading), StringComparison.Ordinal);
        var settled = TuiScreens.Frame(Open14, Input(slices) with { Answered = static ids => ids.Contains(Log14) }, 100, 30);
        Assert.Contains(TuiScreens.NoLog, Text(settled), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("atp-x")]
    [InlineData("atp-")]
    [InlineData("14")]
    [InlineData("atp-0")]
    [InlineData("atp-1.5")]
    [InlineData("atp-99999999999999999999")]
    public void NegativeAMalformedWorkIdShowsNoLogAndAsksForNone(string workId)
    {
        var slices = WithLogs(workId);
        var text = Text(Frame(Open14, slices: slices));
        Assert.Contains(workId, text, StringComparison.Ordinal);
        Assert.DoesNotContain("LOG", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TuiScreens.Wanted(Open14, slices, Now), static id => id.StartsWith(TeamSlices.ItemLogHead, StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemWithNoWorkIdShowsItsKeyAndNoLog()
    {
        var s = OnAgent(Impl) with { Items = new TuiLevel("main|at-12"), Detail = Team + "|main|at-12" };
        var text = Text(Frame(s, slices: WithLogs()));
        Assert.Contains("Item    at-12", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LOG", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LogTextIsMadeSafeToDraw()
    {
        var slices = WithLogs();
        slices[Log14] = J("""{"entries":[{"ts_ms":1,"type":"no\u001b[31mte","title":"two\nlines\u001b[2J","by":"x\u0007y"}],"more":0}""");
        var text = Text(Frame(Open14, slices: slices));
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", text, StringComparison.Ordinal);
        Assert.Contains("two lines", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheOpenItemsLogIsAskedForRightAfterThePlan()
    {
        var open = TuiScreens.Wanted(Open14, WithLogs(), Now);
        Assert.Equal(["plan:d1/ah2-tm4", "plan-items:d1/ah2-tm4/main", "plan-items:d1/ah2-tm4/tui-trim", Log14], open.Skip(1).Take(4));
        Assert.DoesNotContain(Log15, open);
        Assert.DoesNotContain(TuiScreens.Wanted(OnAgent(Impl), WithLogs(), Now), static id => id.StartsWith(TeamSlices.ItemLogHead, StringComparison.Ordinal));
        Assert.DoesNotContain(TuiScreens.Wanted(OnTeam, WithLogs(), Now), static id => id.StartsWith(TeamSlices.ItemLogHead, StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyAndAClickBothOpenAnItemsDialogAndItsLogFollows()
    {
        var slices = WithLogs();
        var keyed = Act(OnAgent(Impl), "j\r", slices);
        var clicked = Act(OnAgent(Impl), Click(3, LineOf(Frame(OnAgent(Impl), slices: slices), "atp-15")), slices);
        foreach (var s in new[] { keyed, clicked })
        {
            Assert.Equal((2, "tui-trim|at-15", Team + "|tui-trim|at-15"), (s.Depth, s.Items.Id, s.Detail));
            Assert.Contains("Docs started", Text(Frame(s, slices: slices)), StringComparison.Ordinal);
            Assert.Contains(Log15, TuiScreens.Wanted(s, slices, Now));
        }
    }

    [Fact]
    public void NegativeMovingOverAnItemDoesNotOpenItsDialogNorAskForItsLog()
    {
        var slices = WithLogs();
        var moved = Act(OnAgent(Impl), "j", slices);
        Assert.Null(moved.Detail);
        Assert.DoesNotContain(Log15, TuiScreens.Wanted(moved, slices, Now));
    }

    [Fact]
    public void AnItemThatLeftItsListSaysSoAndNegativeOneWhoseListHasNotArrivedIsLoading()
    {
        var gone = Open14 with { Detail = Team + "|tui-trim|at-99" };
        Assert.Contains(TuiScreens.NoItem, Text(Frame(gone, slices: WithLogs())), StringComparison.Ordinal);
        var slices = WithLogs();
        Assert.True(slices.Remove("plan-items:d1/ah2-tm4/tui-trim"));
        var waiting = Text(Frame(gone, slices: slices));
        Assert.Contains(TuiScreens.Loading, waiting, StringComparison.Ordinal);
        Assert.DoesNotContain(TuiScreens.NoItem, waiting, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWheelScrollsALongLog()
    {
        var slices = WithLogs();
        var entries = string.Join(",", Enumerable.Range(0, 60).Select(i => $$"""{"ts_ms":{{Ms(50)}},"type":"note","title":"Entry {{i}}","by":"x"}"""));
        slices[Log14] = J($$"""{"entries":[{{entries}}],"more":0}""");
        var top = Frame(Open14, rows: 20, slices: slices);
        Assert.Contains("Entry 0 ", Text(top), StringComparison.Ordinal);
        Assert.DoesNotContain("Entry 59", Text(top), StringComparison.Ordinal);

        var down = Act(Open14, "\u001b[<65;3;5M", slices, rows: 20);
        Assert.Equal(3, down.DetailScroll);
        Assert.Equal(0, down.Items.Scroll);
        var paged = Act(Open14, string.Concat(Enumerable.Repeat("\u001b[6~", 5)), slices, rows: 20);
        Assert.Contains("Entry 59", Text(Frame(paged, rows: 20, slices: slices)), StringComparison.Ordinal);
    }

    [Fact]
    public void ANullBodyIsHeldOnlyForAnItemLog()
    {
        var held = ReadModelPaths.All([J("""{"slices":[{"id":"item-log:d1/2","body":null},{"id":"team:d1/x","body":null},{"id":"item-log:d1/3"}]}""")]);
        Assert.True(held["item-log:d1/2"].IsNull);
        Assert.False(held.ContainsKey("team:d1/x"));
        Assert.False(held.ContainsKey("item-log:d1/3"));
    }

    [Fact]
    public async Task ALogWithANullBodyIsAnsweredAndNegativeNotAskedForAgain()
    {
        var token = TestContext.Current.CancellationToken;
        var slices = Slices();
        slices["item-log:d1/9"] = J("null");
        var source = new SliceSource(() => slices);
        using var feed = new SliceFeed(source, new FakeDiscovery(), new ManualClock(), static () => { }, static _ => { });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = feed.RunAsync(stop.Token);
        feed.Want(["item-log:d1/9"]);
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        int Count()
        {
            lock (source.Polls)
            {
                return source.Polls.Count;
            }
        }

        while (Count() < 2 && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(10, token);
        }

        Assert.True(feed.Answered(["item-log:d1/9"]));
        lock (source.Polls)
        {
            Assert.Equal(["item-log:d1/9"], source.Polls[0].Fresh);
            Assert.Empty(source.Polls[1].Fresh);
        }

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public void TheHeadersItemStaysTheDaemonsWithTwoInProgress() =>
        Assert.Contains("Trim the terminal UI", Frame(OnTeam).Lines[3].Text, StringComparison.Ordinal);

    [Fact]
    public void ThePollAsksForTheScreenFirstThenTheTeams()
    {
        Assert.Equal([Team, Lane, Other, Ended], TuiScreens.Wanted(TuiScreenState.Initial, Slices(), Now));
        var team = TuiScreens.Wanted(OnTeam, Slices(), Now);
        Assert.Equal([Team, "list:open:" + Team, "sitrep:d1/ah2-tm4", Orch, Impl, Reviewer, "exchange:d1/x1", "exchange:d1/x2", Lane, Other, Ended], team);
        var agent = TuiScreens.Wanted(OnAgent(Impl), Slices(), Now);
        Assert.Equal([Team, "plan:d1/ah2-tm4", "plan-items:d1/ah2-tm4/main", "plan-items:d1/ah2-tm4/tui-trim", "list:open:" + Team], agent.Take(5));
    }

    [Fact]
    public void AGoneTeamGoesUpOneLevelToTheNearestRow()
    {
        var slices = Slices();
        slices["list:teams"] = J($$"""{"teams":[{"id":"{{Other}}"}]}""");
        var settled = TuiScreens.Settle(OnTeam with { Teams = OnTeam.Teams with { Index = 5 } }, slices);
        Assert.Equal(0, settled.Depth);
        Assert.Equal(2, Frame(settled, slices: slices).Lines.ToList().FindIndex(l => l.Text.StartsWith("▸ engram", StringComparison.Ordinal)));
    }

    [Fact]
    public void AGoneAgentGoesUpOneLevel()
    {
        var slices = Without();
        slices[Team] = J($$"""{"name":"ah2-tm4","orchestrator":"{{Orch}}","members":[]}""");
        Assert.Equal(1, TuiScreens.Settle(OnAgent(Impl), slices).Depth);
    }

    [Fact]
    public void NegativeATeamListNotYetReadGivesNothingUp()
    {
        Assert.Equal(1, TuiScreens.Settle(OnTeam, new Dictionary<string, JsonValue>()).Depth);
        Assert.Equal(2, TuiScreens.Settle(OnAgent(Impl), Without(Team)).Depth);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(60)]
    [InlineData(40)]
    [InlineData(20)]
    public void ANarrowWindowDropsRightmostColumnsAndNeverOverflows(int cols)
    {
        foreach (var s in new[] { TuiScreenState.Initial, OnTeam, OnAgent(Impl) })
        {
            Assert.All(Frame(s, cols).Lines, l => Assert.True(TuiText.Width(l.Text) <= cols, l.Text));
        }

        Assert.Equal(cols >= 72, Frame(TuiScreenState.Initial, cols).Lines[1].Text.Contains("UPDATED", StringComparison.Ordinal));
    }

    [Fact]
    public void TextFromTheDaemonIsMadeSafeToDraw()
    {
        var slices = Slices();
        slices["team-summary:d1/engram"] = J("""{"name":"en\u001b[31mgram","state":"idle"}""");
        Assert.DoesNotContain("\u001b", Text(Frame(TuiScreenState.Initial, slices: slices)), StringComparison.Ordinal);
    }

    // ---- the mouse and the keys

    [Fact]
    public void AClickOnATeamOpensItAsEnterDoes()
    {
        var f = Frame(TuiScreenState.Initial);
        var clicked = Act(TuiScreenState.Initial, Click(5, LineOf(f, "engram")));
        var keyed = Act(TuiScreenState.Initial, "jj\r");
        Assert.Equal((1, Other), (clicked.Depth, clicked.Teams.Id));
        Assert.Equal((keyed.Depth, keyed.Teams.Id), (clicked.Depth, clicked.Teams.Id));
    }

    [Fact]
    public void AClickOnAnAgentOpensIt()
    {
        var clicked = Act(OnTeam, Click(3, LineOf(Frame(OnTeam), "impl-41")));
        Assert.Equal((2, Impl), (clicked.Depth, clicked.Agents.Id));
    }

    [Fact]
    public void AClickOnAnItemChoosesItAndStaysOnTheScreen()
    {
        var s = OnAgent(Impl);
        var clicked = Act(s, Click(3, LineOf(Frame(s), "at-12")));
        Assert.Equal((2, "main|at-12"), (clicked.Depth, clicked.Items.Id));
    }

    [Fact]
    public void AClickOnACrumbGoesToThatLevel()
    {
        var s = OnAgent(Impl);
        Assert.Equal(0, Act(s, Click(0, 0)).Depth);
        Assert.Equal(1, Act(s, Click(6, 0)).Depth);
        Assert.Equal(2, Act(s, Click(16, 0)).Depth);
        Assert.Equal(0, Act(OnTeam, Click(1, 0)).Depth);
    }

    [Fact]
    public void NegativeAClickBetweenCrumbsOrOnTheHeaderTextDoesNothing()
    {
        var s = OnAgent(Impl);
        Assert.Equal(s.Depth, Act(s, Click(3, 0)).Depth);
        Assert.Equal(s.Depth, Act(s, Click(80, 0)).Depth);
    }

    [Fact]
    public void TheWheelScrollsThreeRowsAndMovesNoSelection()
    {
        var down = Act(OnTeam, "\u001b[<65;5;5M", rows: 8);
        Assert.Equal(3, down.Agents.Scroll);
        Assert.Equal(OnTeam.Agents.Id, down.Agents.Id);
        Assert.Equal(0, Act(down, "\u001b[<64;5;5M", rows: 8).Agents.Scroll);
    }

    [Fact]
    public void NegativeTheWheelCannotScrollPastTheEnd() =>
        Assert.Equal(0, Act(TuiScreenState.Initial, "\u001b[<65;5;5M").Teams.Scroll);

    [Theory]
    [InlineData("\u001b[<2;5;4M")] // right click
    [InlineData("\u001b[<0;5;4m")] // release
    [InlineData("\u001b[<35;5;4M")] // motion
    [InlineData("\u001b[<1;5;4M")] // middle
    [InlineData("\u001b[<0;x;4M")] // malformed
    public void NegativeOtherReportsDoNothing(string report) =>
        Assert.Equal(TuiScreenState.Initial, Act(TuiScreenState.Initial, report));

    [Fact]
    public void NegativeAClickOnAShipRecordAScreenHeadingOrPastTheLastRowDoesNothing()
    {
        var f = Frame(OnTeam);
        foreach (var text in new[] { "Shipped ship-21", "SITREP", "AGENTS" })
        {
            var after = Act(OnTeam, Click(3, LineOf(f, text)));
            Assert.Equal((1, OnTeam.Agents.Id), (after.Depth, after.Agents.Id));
        }

        Assert.Equal(1, Act(OnTeam, Click(3, 29)).Depth);
        Assert.Equal(0, Act(TuiScreenState.Initial, Click(3, 20)).Depth);
        Assert.Equal(0, Act(TuiScreenState.Initial, Click(3, 1)).Depth);
    }

    [Fact]
    public void KeysMoveOpenAndGoBack()
    {
        Assert.Equal(1, Act(TuiScreenState.Initial, "j").Teams.Index);
        Assert.Equal(0, Act(TuiScreenState.Initial, "jk").Teams.Index);
        Assert.Equal(2, Act(TuiScreenState.Initial, "\u001b[F").Teams.Index);
        Assert.Equal(0, Act(TuiScreenState.Initial, "\u001b[F\u001b[H").Teams.Index);
        Assert.Equal(1, Act(TuiScreenState.Initial, "\r").Depth);
        Assert.Equal(2, Act(OnTeam, "\r").Depth);
        Assert.Equal(1, Act(OnAgent(Impl), "\u001b").Depth);
        Assert.Equal(0, Act(OnTeam, "\u001b").Depth);
        Assert.Equal(0, Act(TuiScreenState.Initial, "\u001b").Depth);
    }

    [Fact]
    public void PagingScrollsTheBodyAndTheSelectionStays()
    {
        var s = Act(OnTeam, "\u001b[6~", rows: 8);
        Assert.True(s.Agents.Scroll > 0);
        Assert.Equal(OnTeam.Agents.Id, s.Agents.Id);
        Assert.Equal(0, Act(s, "\u001b[5~", rows: 8).Agents.Scroll);
    }

    [Fact]
    public void MovingKeepsTheSelectionOnScreen()
    {
        var s = Act(OnTeam, "jj", rows: 4);
        Assert.Contains(Frame(s, rows: 4).Lines, l => l.Kind == TuiLineKind.Selected && l.Text.Contains("reviewer-9", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHelpPageTakesAnyKeyAndQQuits()
    {
        var help = Act(TuiScreenState.Initial, "?");
        Assert.True(help.Help);
        Assert.Contains("Keys", Text(Frame(help)), StringComparison.Ordinal);
        Assert.False(Act(help, "z").Help);
        var (_, action) = TuiScreens.Reduce(help, new KeyEvent("q"), Frame(help), Slices());
        Assert.IsType<QuitAction>(action);
        Assert.True(Act(help, Click(3, 3)).Help);
    }

    [Theory]
    [InlineData("q", typeof(QuitAction))]
    [InlineData("\u0003", typeof(QuitAction))]
    [InlineData("r", typeof(RefreshAction))]
    public void QuitAndRefreshGoToTheLoop(string typed, Type expected)
    {
        var (_, action) = TuiScreens.Reduce(TuiScreenState.Initial, TuiNav.ParseInput(typed)[0], Frame(TuiScreenState.Initial), Slices());
        Assert.IsType(expected, action);
    }

    [Fact]
    public void ASelectionFollowsItsRowWhenRowsMove()
    {
        var s = Act(TuiScreenState.Initial, "j");
        Assert.Equal(Lane, s.Teams.Id);
        var fewer = Slices();
        fewer["list:teams"] = J($$"""{"teams":[{"id":"{{Team}}"},{"id":"{{Other}}"}]}""");
        Assert.Contains(Frame(s, slices: fewer).Lines, l => l.Text.StartsWith("▸ engram", StringComparison.Ordinal));
    }
}

/// <summary>The command line: what it reaches, what it prints, how it exits.</summary>
public sealed class TuiAppTests
{
    private static async Task<(int Code, string Out, string Err, FakeTerminal Terminal)> Run(string[] args, bool tty = false, Func<string, string?>? env = null, IViewSource? source = null, bool down = false)
    {
        var terminal = new FakeTerminal();
        terminal.EndInput();
        var discovery = new FakeDiscovery();
        if (down)
        {
            discovery.Probe = null;
        }

        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var host = new TuiHost(stdout, stderr, env ?? (_ => null), tty, tty, source ?? new SliceSource(PlansRead), discovery, terminal, new SystemTuiClock());
        var code = await TuiApp.RunAsync(args, host, TestContext.Current.CancellationToken);
        return (code, stdout.ToString(), stderr.ToString(), terminal);
    }

    [Fact]
    public async Task AnIdIsAnsweredWhenHeldOrOnceItHasSettledAndNegativeNotByAReplyAlone()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        using var feed = new SliceFeed(new SliceSource(Slices), new FakeDiscovery(), clock, static () => { }, static _ => { });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = feed.RunAsync(stop.Token);
        feed.Want(["list:teams", "never-served"]);
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        while (!feed.Slices.ContainsKey("list:teams") && DateTime.UtcNow < ceiling)
        {
            await Task.Delay(10, token);
        }

        Assert.True(feed.Answered(["list:teams"]));
        Assert.False(feed.Answered(["never-served"]));
        Assert.False(feed.Answered(["not-wanted"]));

        clock.Advance(TuiClientRules.Settling - TimeSpan.FromMilliseconds(1));
        Assert.False(feed.Answered(["never-served"]));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(feed.Answered(["never-served"]));
        Assert.False(feed.Answered(["not-wanted"]));

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task OnceWithNoTeamListPrintsAtTheDeadlineAndNegativeNotBefore()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var run = TuiOnce.RunScreenAsync(null, new SliceSource(static () => new Dictionary<string, JsonValue>()), new FakeDiscovery(), clock, 100, stdout, stderr, token);

        await clock.WaitingAsync(token);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        Assert.Empty(stdout.ToString());

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.Contains("loading", stdout.ToString(), StringComparison.Ordinal);
    }

    private static async Task<TimeSpan> AdvanceUntilAsync(ManualClock clock, Func<bool> done, CancellationToken token)
    {
        var start = clock.UtcNow;
        var ceiling = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < ceiling)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5, token);
        }

        return clock.UtcNow - start;
    }

    [Fact]
    public async Task OnceWaitsPastTheDeadlineForAListABuildingDaemonThenPrintsIt()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var list = new Dictionary<string, JsonValue> { ["list:teams"] = JsonValue.FromObject(new JsonObject { ["teams"] = JsonValue.FromArray(new JsonArray()) }) };
        var source = new SliceSource(() => list)
        {
            Answer = new ViewOutcome(ViewOutcomeKind.View, View: JsonValue.FromObject(new JsonObject { ["building"] = true })),
        };
        var run = TuiOnce.RunScreenAsync(null, source, new FakeDiscovery(), clock, 100, stdout, stderr, token);

        var waited = await AdvanceUntilAsync(clock, () => run.IsCompleted || clock.UtcNow >= DateTimeOffset.Parse("2026-10-11T12:00:06Z", System.Globalization.CultureInfo.InvariantCulture), token);
        Assert.False(run.IsCompleted, $"printed after {waited}");

        source.Answer = null;
        _ = await AdvanceUntilAsync(clock, () => run.IsCompleted, token);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.DoesNotContain("loading", stdout.ToString(), StringComparison.Ordinal);
    }

    private static ViewOutcome EmptyList(bool building) => new(ViewOutcomeKind.View, View: JsonValue.FromObject(new JsonObject
    {
        ["seq"] = 1,
        ["epoch"] = "e",
        ["building"] = building,
        ["slices"] = JsonValue.FromArray(new JsonArray { JsonValue.FromObject(new JsonObject { ["id"] = "list:teams", ["as_of"] = 1, ["body"] = JsonValue.FromObject(new JsonObject { ["teams"] = JsonValue.FromArray(new JsonArray()) }) }) }),
    }));

    [Fact]
    public async Task OnceDoesNotSayNoTeamsWhileTheDaemonBuildsTheListThenPrintsTheTeam()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var stdout = new StringWriter();
        var slices = OneTeam(planLoaded: true);
        var source = new SliceSource(() => slices) { Answer = EmptyList(building: true) };
        var run = TuiOnce.RunScreenAsync(null, source, new FakeDiscovery(), clock, 100, stdout, new StringWriter(), token);

        var started = clock.UtcNow;
        var waited = await AdvanceUntilAsync(clock, () => run.IsCompleted || clock.UtcNow >= started + TimeSpan.FromSeconds(6), token);
        Assert.False(run.IsCompleted, $"printed after {waited}");

        source.Answer = null;
        _ = await AdvanceUntilAsync(clock, () => run.IsCompleted, token);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.DoesNotContain(TuiScreens.NoTeams, stdout.ToString(), StringComparison.Ordinal);
        Assert.Matches(@"\bui\b", stdout.ToString());
    }

    [Fact]
    public async Task OnceSaysNoTeamsAtTheCeilingIfTheDaemonNeverFinishesBuilding()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var stdout = new StringWriter();
        var source = new SliceSource(static () => new Dictionary<string, JsonValue>()) { Answer = EmptyList(building: true) };
        var run = TuiOnce.RunScreenAsync(null, source, new FakeDiscovery(), clock, 100, stdout, new StringWriter(), token);

        var waited = await AdvanceUntilAsync(clock, () => run.IsCompleted, token);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.True(waited >= TuiClientRules.BuildingCeiling, $"printed after {waited}");
        Assert.Contains(TuiScreens.NoTeams, stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeAnEmptyListFromADaemonThatIsNotBuildingSaysNoTeamsWithoutWaiting()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var stdout = new StringWriter();
        var source = new SliceSource(static () => new Dictionary<string, JsonValue>()) { Answer = EmptyList(building: false) };
        var run = TuiOnce.RunScreenAsync(null, source, new FakeDiscovery(), clock, 100, stdout, new StringWriter(), token);

        var waited = await AdvanceUntilAsync(clock, () => run.IsCompleted, token);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.True(waited < TuiClientRules.BuildingCeiling, $"printed after {waited}");
        Assert.Contains(TuiScreens.NoTeams, stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnceStopsWaitingForABuildingDaemonAtTheCeilingAndNegativeNotBefore()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var source = new SliceSource(static () => new Dictionary<string, JsonValue>())
        {
            Answer = new ViewOutcome(ViewOutcomeKind.View, View: JsonValue.FromObject(new JsonObject { ["pending"] = JsonValue.FromArray(new JsonArray { "list:teams" }) })),
        };
        var run = TuiOnce.RunScreenAsync(null, source, new FakeDiscovery(), clock, 100, stdout, stderr, token);

        var waited = await AdvanceUntilAsync(clock, () => run.IsCompleted, token);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.True(waited >= TuiClientRules.BuildingCeiling, $"printed after {waited}");
        Assert.Contains("loading", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummaryIsReadAgainSoonWithoutAListAndNegativeOnlyEachRoundWithOne()
    {
        var token = TestContext.Current.CancellationToken;
        async Task<int> ReadsInASecond(Dictionary<string, JsonValue> slices)
        {
            var clock = new ManualClock();
            var source = new SliceSource(() => slices);
            using var feed = new SliceFeed(source, new FakeDiscovery(), clock, static () => { }, static _ => { });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var running = feed.RunAsync(stop.Token);
            await clock.WaitingAsync(token);
            for (var i = 0; i < 10; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(5, token);
            }

            var reads = source.SummaryReads;
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(30), token);
            return reads;
        }

        Assert.True(await ReadsInASecond([]) > 1);
        Assert.Equal(1, await ReadsInASecond(new Dictionary<string, JsonValue> { ["list:teams"] = JsonValue.FromObject(new JsonObject { ["teams"] = JsonValue.FromArray(new JsonArray()) }) }));
    }

    /// <summary>The fixture with every plan read, so a plain frame on the real clock does not wait for one.</summary>
    private static Dictionary<string, JsonValue> PlansRead()
    {
        var slices = Slices();
        slices["team-summary:d1/engram"] = J("""{"name":"engram","state":"idle","held":true,"needs_you":0,"plan_loaded":true}""");
        return slices;
    }

    private static Dictionary<string, JsonValue> OneTeam(bool planLoaded) => new()
    {
        ["list:teams"] = J("""{"teams":[{"id":"team:d1/ui"}]}"""),
        ["team:d1/ui"] = J("""{"name":"ui","members":[]}"""),
        ["team-summary:d1/ui"] = J($$"""{"name":"ui","state":"idle","needs_you":0,"plan_loaded":{{(planLoaded ? "true" : "false")}}}"""),
    };

    [Fact]
    public async Task OnceWaitsForAPlanNotYetReadThenPrintsItsCountAndNegativeAReadPlanPrintsAtOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var stdout = new StringWriter();
        var slices = OneTeam(planLoaded: false);
        var run = TuiOnce.RunScreenAsync(null, new SliceSource(() => slices), new FakeDiscovery(), clock, 100, stdout, new StringWriter(), token);

        var six = clock.UtcNow + TimeSpan.FromSeconds(6);
        _ = await AdvanceUntilAsync(clock, () => run.IsCompleted || clock.UtcNow >= six, token);
        Assert.False(run.IsCompleted);

        slices = OneTeam(planLoaded: true);
        var after = await AdvanceUntilAsync(clock, () => run.IsCompleted, token);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.True(after < TimeSpan.FromSeconds(1), $"printed {after} after the plan was read");
        Assert.Matches(@"\bui\b.*idle +0 ", stdout.ToString());

        var readClock = new ManualClock();
        var read = OneTeam(planLoaded: true);
        var prompt = TuiOnce.RunScreenAsync(null, new SliceSource(() => read), new FakeDiscovery(), readClock, 100, new StringWriter(), new StringWriter(), token);
        var waited = await AdvanceUntilAsync(readClock, () => prompt.IsCompleted, token);
        Assert.True(waited < TuiClientRules.Settling, $"printed after {waited}");
    }

    [Fact]
    public async Task OnceStopsWaitingForAPlanAtTheCeilingAndNegativeNotBefore()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var stdout = new StringWriter();
        var slices = OneTeam(planLoaded: false);
        var run = TuiOnce.RunScreenAsync(null, new SliceSource(() => slices), new FakeDiscovery(), clock, 100, stdout, new StringWriter(), token);

        var waited = await AdvanceUntilAsync(clock, () => run.IsCompleted, token);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), token));
        Assert.True(waited >= TuiClientRules.BuildingCeiling, $"printed after {waited}");
        Assert.Matches(@"\bui\b.*idle +– ", stdout.ToString());
    }

    [Fact]
    public async Task TheSummaryIsReadAgainSoonForAPlanNotYetReadAndNegativeNotPastTheCeiling()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var slices = OneTeam(planLoaded: false);
        var source = new SliceSource(() => slices);
        using var feed = new SliceFeed(source, new FakeDiscovery(), clock, static () => { }, static _ => { });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = feed.RunAsync(stop.Token);
        await clock.WaitingAsync(token);

        async Task<int> ReadsIn(TimeSpan span)
        {
            var before = source.SummaryReads;
            var end = clock.UtcNow + span;
            _ = await AdvanceUntilAsync(clock, () => clock.UtcNow >= end, token);
            return source.SummaryReads - before;
        }

        Assert.True(await ReadsIn(TimeSpan.FromSeconds(1)) > 1);
        _ = await ReadsIn(TuiClientRules.BuildingCeiling + TimeSpan.FromSeconds(1));
        Assert.True(await ReadsIn(TimeSpan.FromSeconds(4)) <= 1);

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task OncePrintsTeamsWithNoEscapeAndNeverTouchesTheTerminal()
    {
        var (code, output, _, terminal) = await Run(["--once"], tty: true);
        Assert.Equal(0, code);
        Assert.Contains("ah2-tm4", output, StringComparison.Ordinal);
        Assert.Contains("TEAM", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.Empty(terminal.Snapshot());
    }

    [Fact]
    public async Task OnceWithATeamPrintsThatTeamsScreen()
    {
        var (code, output, _, _) = await Run(["--once", "--team", "ah2-tm4"]);
        Assert.Equal(0, code);
        Assert.Contains("AGENTS", output, StringComparison.Ordinal);
        Assert.Contains("Pick the DB migration order", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeOnceWithAnUnknownTeamSaysSoOnStderrWithExitTwo()
    {
        var (code, output, err, _) = await Run(["--once", "--team", "nosuch"]);
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("nosuch", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnceWithNoDaemonSaysSoWithExitThree()
    {
        var (code, output, _, _) = await Run(["--once"], down: true);
        Assert.Equal(3, code);
        Assert.Equal("ahd is not running\n", output);
    }

    [Fact]
    public async Task OnceAgainstAFaultingDaemonExitsOneAndNotDown()
    {
        var source = new SliceSource(Slices) { Answer = new ViewOutcome(ViewOutcomeKind.Error, Status: 500, Code: "boom") };
        var (code, output, err, _) = await Run(["--once"], source: source);
        Assert.Equal(1, code);
        Assert.Empty(output);
        Assert.Contains("500 boom", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTerminalBehavesAsOnce()
    {
        var (code, output, _, terminal) = await Run([], tty: false);
        Assert.Equal(0, code);
        Assert.Contains("TEAM", output, StringComparison.Ordinal);
        Assert.Empty(terminal.Snapshot());
    }

    [Fact]
    public async Task ADumbTerminalBehavesAsOnceWithNoMouseEscape()
    {
        var (code, output, _, terminal) = await Run([], tty: true, env: name => name == "TERM" ? "dumb" : null);
        Assert.Equal(0, code);
        Assert.Contains("TEAM", output, StringComparison.Ordinal);
        Assert.Empty(terminal.Snapshot());
    }

    [Fact]
    public async Task ATerminalGetsTheScreenWithTheMouse()
    {
        var (code, _, _, terminal) = await Run([], tty: true);
        Assert.Equal(0, code);
        Assert.Equal(TuiModes.Enter(true), terminal.Snapshot()[0]);
        Assert.Equal(TuiModes.Leave(true), terminal.Snapshot()[^1]);
    }

    [Fact]
    public async Task NoColorStillHasTheMouse()
    {
        var (_, _, _, terminal) = await Run([], tty: true, env: name => name == "NO_COLOR" ? "1" : null);
        Assert.Equal(TuiModes.Enter(true), terminal.Snapshot()[0]);
    }

    [Theory]
    [InlineData("--classic")]
    [InlineData("--path", "/x")]
    [InlineData("--theme", "dark")]
    [InlineData("--reduced-motion")]
    public async Task NegativeTheRemovedFlagsAreUsageErrors(params string[] args)
    {
        var (code, _, _, terminal) = await Run(args, tty: true);
        Assert.Equal(2, code);
        Assert.Empty(terminal.Snapshot());
    }

    [Fact]
    public async Task HelpShowsTheUsage()
    {
        var (code, output, _, _) = await Run(["--help"]);
        Assert.Equal(0, code);
        Assert.Contains("--team", output, StringComparison.Ordinal);
        Assert.Contains("Shift", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--classic", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeATeamFlagWithNoValueIsAUsageError() =>
        Assert.Equal("--team needs a value", TuiArgs.Parse(["--team"]).UsageError);
}
