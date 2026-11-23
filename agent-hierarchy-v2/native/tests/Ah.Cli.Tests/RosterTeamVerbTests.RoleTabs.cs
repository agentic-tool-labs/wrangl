using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>Where a herdr spawn outside any stream puts its pane: its role's tab, never the Orchestrator's.</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>The herdr calls a spawn makes, answered over a table of tabs and panes.</summary>
    private sealed class HerdrFake
    {
        private int _next;

        internal List<(string Id, string Label)> Tabs { get; } = [("tCaller", "orchestrator")];

        internal List<(string Pane, string Tab)> Panes { get; } = [("pOrch", "tCaller")];

        /// <summary>The zoomed pane of each zoomed tab.</summary>
        internal Dictionary<string, string> Zoom { get; } = [];

        /// <summary>The agent name of a pane, for <c>agent list</c>.</summary>
        internal Dictionary<string, string> Agents { get; } = [];

        internal bool FailUnzoom { get; set; }

        internal bool FailMove { get; set; }

        internal bool FailTabClose { get; set; }

        /// <summary>A move that answers <c>changed: true</c> and leaves the pane where it was.</summary>
        internal bool LieAboutMove { get; set; }

        /// <summary>A move whose reply carries no <c>move_result</c> at all.</summary>
        internal bool OmitMoveResult { get; set; }

        /// <summary>Layout replies leave out the tab id, as when the pre-move tab cannot be read.</summary>
        internal bool HideTab { get; set; }

        private string MoveReply(string from) =>
            OmitMoveResult
                ? "{\"result\":{}}"
                : $"{{\"result\":{{\"move_result\":{{\"changed\":true,\"previous_tab_id\":\"{from}\"}}}}}}";

        /// <summary>Layout replies leave the zoom out, as a client that cannot read it would see.</summary>
        internal bool HideZoom { get; set; }

        /// <summary>Every <c>agent start</c> line is refused with herdr's <c>agent_name_taken</c>.</summary>
        internal bool NameTaken { get; set; }

        internal int Exit(string[] argv) =>
            (NameTaken && argv[0] == "/bin/sh")
            || (argv.Length > 3 && argv[0] == "herdr"
            && ((FailUnzoom && argv[1] == "pane" && argv[2] == "zoom" && argv.Contains("--off"))
                || (FailMove && argv[1] == "pane" && argv[2] == "move")
                || (FailTabClose && argv[1] == "tab" && argv[2] == "close")))
                ? 1
                : 0;

        private static string Flag(string[] argv, string name)
        {
            var at = Array.IndexOf(argv, name);
            return at >= 0 && at + 1 < argv.Length ? argv[at + 1] : string.Empty;
        }

        private string TabOf(string pane) => Panes.FirstOrDefault(p => p.Pane == pane).Tab ?? "tCaller";

        internal string Answer(string[] argv)
        {
            if (argv[0] == "/bin/sh")
            {
                if (NameTaken)
                {
                    return "{\"error\":{\"code\":\"agent_name_taken\",\"message\":\"agent name is already in use\"}}";
                }

                var started = Regex.Match(argv[^1], @"--pane (\S+)");
                return started.Success
                    ? $"{{\"result\":{{\"pane\":{{\"pane_id\":\"{started.Groups[1].Value}\",\"tab_id\":\"{TabOf(started.Groups[1].Value)}\"}}}}}}"
                    : "{}";
            }

            if (argv[0] != "herdr" || argv.Length < 3)
            {
                return string.Empty;
            }

            switch (argv[1] + " " + argv[2])
            {
                case "tab list":
                    return "{\"result\":{\"tabs\":[" + string.Join(",", Tabs.Select(t => $"{{\"tab_id\":\"{t.Id}\",\"label\":\"{t.Label}\"}}")) + "]}}";
                case "tab create":
                    var tab = "t" + (++_next).ToString(CultureInfo.InvariantCulture);
                    var root = "r" + _next.ToString(CultureInfo.InvariantCulture);
                    Tabs.Add((tab, Flag(argv, "--label")));
                    Panes.Add((root, tab));
                    return $"{{\"result\":{{\"tab\":{{\"tab_id\":\"{tab}\"}},\"root_pane\":{{\"pane_id\":\"{root}\"}}}}}}";
                case "pane list":
                    return "{\"result\":{\"panes\":[" + string.Join(",", Panes.Select(p => $"{{\"pane_id\":\"{p.Pane}\",\"tab_id\":\"{p.Tab}\"}}")) + "]}}";
                case "pane layout":
                    var of = TabOf(Flag(argv, "--pane"));
                    var zoomState = (HideTab ? string.Empty : $"\"tab_id\":\"{of}\",")
                        + (!HideZoom && Zoom.TryGetValue(of, out var zoomedPane)
                            ? $"\"zoomed\":true,\"focused_pane_id\":\"{zoomedPane}\","
                            : "\"zoomed\":false,");
                    return "{\"result\":{\"layout\":{" + zoomState + "\"panes\":[" + string.Join(
                        ",", Panes.Where(p => p.Tab == of).Select(p => $"{{\"pane_id\":\"{p.Pane}\",\"rect\":{{\"x\":0,\"y\":0,\"width\":100,\"height\":50}}}}")) + "]}}}";
                case "pane zoom":
                    var zoomTab = TabOf(argv[3]);
                    if (argv.Contains("--off"))
                    {
                        _ = Zoom.Remove(zoomTab);
                    }
                    else
                    {
                        Zoom[zoomTab] = argv[3];
                    }

                    return "{}";
                case "pane move":
                    var moving = argv[3];
                    var from = TabOf(moving);
                    if (Zoom.ContainsKey(from))
                    {
                        return $"{{\"result\":{{\"move_result\":{{\"changed\":false,\"reason\":\"zoomed_tab\",\"previous_tab_id\":\"{from}\"}}}}}}";
                    }

                    if (LieAboutMove)
                    {
                        return MoveReply(from);
                    }

                    var into = Flag(argv, "--tab");
                    if (into.Length == 0)
                    {
                        into = "t" + (++_next).ToString(CultureInfo.InvariantCulture);
                        Tabs.Add((into, "moved"));
                    }

                    Panes.RemoveAll(p => p.Pane == moving);
                    Panes.Add((moving, into));
                    return MoveReply(from);
                case "agent list":
                    return "{\"result\":{\"agents\":[" + string.Join(
                        ",", Agents.Select(a => $"{{\"name\":\"{a.Value}\",\"pane_id\":\"{a.Key}\",\"tab_id\":\"{TabOf(a.Key)}\",\"workspace_id\":\"w1\"}}")) + "]}}";
                case "pane close":
                    Panes.RemoveAll(p => p.Pane == argv[3]);
                    _ = Agents.Remove(argv[3]);
                    return "{}";
                case "tab close":
                    if (FailTabClose)
                    {
                        return "{}";
                    }

                    Tabs.RemoveAll(t => t.Id == argv[3]);
                    foreach (var gone in Panes.Where(p => p.Tab == argv[3]).ToList())
                    {
                        _ = Agents.Remove(gone.Pane);
                    }

                    Panes.RemoveAll(p => p.Tab == argv[3]);
                    return "{}";
                case "pane split":
                    var created = "n" + (++_next).ToString(CultureInfo.InvariantCulture);
                    Panes.Add((created, TabOf(Flag(argv, "--pane"))));
                    return $"{{\"result\":{{\"pane\":{{\"pane_id\":\"{created}\"}}}}}}";
                default:
                    return "{}";
            }
        }
    }

    private HerdrFake OnHerdr(string teamMembers)
    {
        var herdr = new HerdrFake();
        PutHerdrOnPath();
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HERDR_PANE_ID"] = "pOrch";
        _extraEnv["HERDR_TAB_ID"] = "tCaller";
        _runner.Output = herdr.Answer;
        _runner.Exit = herdr.Exit;
        WriteTeam("proj", LiveTeamJson("herdr", teamMembers));
        return herdr;
    }

    private int CallIndex(params string[] words) =>
        _runner.Calls.FindIndex(argv => argv.Length > 0 && words.All(w => argv.Contains(w)));

    /// <summary>A herdr with the member <c>proj-implementor</c> in pane pA of tab tSrc, next to pB.</summary>
    private HerdrFake WithMemberToMove(string recordedTab = "tSrc")
    {
        var herdr = OnHerdr(
            "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"pA\",\"tab_id\":\"" + recordedTab + "\",\"model\":\"sonnet\"}");
        herdr.Tabs.Add(("tSrc", "src"));
        herdr.Panes.Add(("pA", "tSrc"));
        herdr.Panes.Add(("pB", "tSrc"));
        herdr.Agents["pA"] = "proj-implementor";
        return herdr;
    }

    private (int Code, JsonObject Out) MoveToNewTab()
    {
        var (code, stdout, _) = Roster("move", "proj-implementor", "--new-tab", "--team", "proj");
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject());
    }

    /// <summary>A herdr team whose stream <c>s1</c> sits in tab tS with its members' panes; <paramref name="livePanes"/> names the panes still alive.</summary>
    private HerdrFake WithStreamTab(string state, params string[] livePanes)
    {
        var members = string.Join(
            ",", new[] { ("proj-implementor", "w1:pA"), ("proj-reviewer", "w1:pB") }.Select(
                m => $"{{\"role\":\"{m.Item1[5..]}\",\"name\":\"{m.Item1}\",\"route\":\"peer\",\"transport_id\":\"{m.Item2}\",\"tab_id\":\"tS\",\"stream\":\"s1\",\"model\":\"sonnet\"}}"));
        var herdr = new HerdrFake();
        PutHerdrOnPath();
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HERDR_PANE_ID"] = "pOrch";
        _extraEnv["HERDR_TAB_ID"] = "tCaller";
        _runner.Output = herdr.Answer;
        _runner.Exit = herdr.Exit;
        WriteTeam("proj", LiveTeamJson("herdr", members).TrimEnd('}') + ",\"streams\":{\"s1\":{\"state\":\"" + state + "\",\"tab_id\":\"tS\",\"opened\":\"2026-01-01T00:00:00.000Z\"}}}");
        herdr.Tabs.Add(("tS", "s1"));
        foreach (var pane in livePanes)
        {
            herdr.Panes.Add(("w1:" + pane, "tS"));
            herdr.Agents["w1:" + pane] = pane == "pA" ? "proj-implementor" : "proj-reviewer";
        }

        Watch(herdr);
        return herdr;
    }

    /// <summary>Makes the pane-state fake read each tab's panes from <paramref name="herdr"/> as they stand when asked, so closes show.</summary>
    private void Watch(HerdrFake herdr) =>
        _panes.TabSource = tab => herdr.Panes
            .Where(p => p.Tab == tab)
            .Select(p => (p.Pane, (int?)Math.Abs(p.Pane.Aggregate(7, (h, ch) => unchecked((h * 31) + ch))) + 1))
            .ToList();

    /// <summary>Adds a pane to a tab of <paramref name="herdr"/> that no member of the team owns.</summary>
    private static void AddForeign(HerdrFake herdr, string tab, string pane) => herdr.Panes.Add((pane, tab));

    /// <summary>Records <paramref name="pane"/> as the root pane ah made when it created stream s1's tab, and puts that pane in the tab.</summary>
    private void RecordRoot(HerdrFake herdr, string pane)
    {
        _sandbox.PlantTeam(
            TeamPath("proj"),
            _sandbox.TeamText(TeamPath("proj")).Replace("\"tab_id\":\"tS\",\"opened\"", $"\"tab_id\":\"tS\",\"root_pane_id\":\"{pane}\",\"opened\"", StringComparison.Ordinal));
        herdr.Panes.Add((pane, "tS"));
    }

    /// <summary>Two done streams: s1 (tab tS) holds proj-implementor in w1:pA, s2 (tab tT) holds proj-reviewer in w1:pB; both panes are alive.</summary>
    private HerdrFake WithTwoDoneStreams()
    {
        var members = "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"w1:pA\",\"tab_id\":\"tS\",\"stream\":\"s1\",\"model\":\"sonnet\"},"
            + "{\"role\":\"reviewer\",\"name\":\"proj-reviewer\",\"route\":\"peer\",\"transport_id\":\"w1:pB\",\"tab_id\":\"tT\",\"stream\":\"s2\",\"model\":\"sonnet\"}";
        var herdr = new HerdrFake();
        PutHerdrOnPath();
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HERDR_PANE_ID"] = "pOrch";
        _extraEnv["HERDR_TAB_ID"] = "tCaller";
        _runner.Output = herdr.Answer;
        _runner.Exit = herdr.Exit;
        WriteTeam(
            "proj",
            LiveTeamJson("herdr", members).TrimEnd('}')
            + ",\"streams\":{\"s1\":{\"state\":\"done\",\"tab_id\":\"tS\",\"opened\":\"2026-01-01T00:00:00.000Z\"},"
            + "\"s2\":{\"state\":\"done\",\"tab_id\":\"tT\",\"opened\":\"2026-01-02T00:00:00.000Z\"}}}");
        herdr.Tabs.Add(("tS", "s1"));
        herdr.Tabs.Add(("tT", "s2"));
        herdr.Panes.Add(("w1:pA", "tS"));
        herdr.Panes.Add(("w1:pB", "tT"));
        herdr.Agents["w1:pA"] = "proj-implementor";
        herdr.Agents["w1:pB"] = "proj-reviewer";
        Watch(herdr);
        return herdr;
    }

    private JsonObject DisbandClosing()
    {
        var token = TokenOf(Roster("disband", "--ignore-release", "--team", "proj").Stdout);
        var (code, stdout, err) = Roster("disband", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", "proj");
        Assert.True(code == 0, err + stdout);
        return JsonText.Parse(stdout).AsObject();
    }

    [Fact]
    public void DisbandClosesTheTabOfAStreamWhoseMembersAreGoneAndKeepsTheTabOfOneWhoseMemberPaneIsLeft()
    {
        var herdr = WithTwoDoneStreams();
        _extraEnv["HERDR_PANE_ID"] = "w1:pB";
        _panes.HarnessPanes.Add("w1:pB");

        var closed = DisbandClosing();

        Assert.Equal(["tS"], closed["tabs_closed"].AsArray().Select(t => t.AsString()).ToArray());
        var keptTab = closed["tabs_kept"].AsArray().Single();
        Assert.Equal("s2", keptTab.Member("stream")!.Value.AsString());
        Assert.Equal("harness running", keptTab.Member("reason")!.Value.AsString());
        Assert.Equal("w1:pB", keptTab.Member("pane")!.Value.AsString());
        Assert.DoesNotContain(herdr.Tabs, t => t.Id == "tS");
        Assert.Contains(herdr.Tabs, t => t.Id == "tT");
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("tab") && argv.Contains("close") && argv.Contains("tT"));
    }

    [Fact]
    public void DisbandSaysSoWhenClosingAStreamTabFails()
    {
        var herdr = WithTwoDoneStreams();
        herdr.FailTabClose = true;

        var closed = DisbandClosing();

        Assert.Null(closed.Get("tabs_closed"));
        Assert.Contains(herdr.Tabs, t => t.Id == "tS");
        Assert.Contains(closed["warnings"].AsArray(), w => w.AsString().Contains("closing its tab tS failed", StringComparison.Ordinal));
    }

    [Fact]
    public void UntrackingAMemberOfADoneStreamClosesNoTab()
    {
        var herdr = WithTwoDoneStreams();

        var (code, _, err) = Roster("untrack", "proj-implementor", "--team", "proj");

        Assert.True(code == 0, err);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[1] == "tab" && argv[2] == "close");
        Assert.Contains(herdr.Tabs, t => t.Id == "tS");
    }

    [Fact]
    public void StreamStatusReadsTabPanesThroughThePaneStateSeamAndNeverListsHerdrPanesItself()
    {
        WithTwoDoneStreams();

        var (code, stdout, err) = Roster("stream-status", "--team", "proj");

        Assert.True(code == 0, err);
        Assert.Equal(2, JsonText.Parse(stdout).Member("streams")!.Value.AsArray().Count(s => s.Member("tab")!.Value.Member("member_panes") is not null));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "pane" && argv[2] == "list");
    }

    private JsonObject DismissClosing(string member)
    {
        var token = TokenOf(Roster("dismiss", member, "--plan", "--ignore-release", "--team", "proj").Stdout);
        var (code, stdout, err) = Roster("dismiss", member, "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", "proj");
        Assert.True(code == 0, err);
        Assert.True(JsonText.Parse(stdout).AsObject().Get("results") is not null, stdout);
        return JsonText.Parse(stdout).AsObject();
    }

    [Fact]
    public void DismissingTheLastMemberOfADoneStreamClosesItsTab()
    {
        var herdr = WithStreamTab("done", "pA");

        var closed = DismissClosing("proj-implementor");

        Assert.Equal(["tS"], closed["tabs_closed"].AsArray().Select(t => t.AsString()).ToArray());
        Assert.Null(closed.Get("tabs_kept"));
        Assert.DoesNotContain(herdr.Tabs, t => t.Id == "tS");
        Assert.Contains(herdr.Tabs, t => t.Id == "tCaller");
        Assert.True(_panes.Refreshed > 0);
    }

    [Fact]
    public void DismissingAMemberWhoseStreamTabStillHoldsAnotherMemberKeepsTheTab()
    {
        var herdr = WithStreamTab("done", "pA", "pB");
        _panes.HarnessPanes.Add("w1:pB");

        var kept = DismissClosing("proj-implementor");

        var keptTab = kept["tabs_kept"].AsArray().Single();
        Assert.Equal("s1", keptTab.Member("stream")!.Value.AsString());
        Assert.Equal("tS", keptTab.Member("tab")!.Value.AsString());
        Assert.Equal("harness running", keptTab.Member("reason")!.Value.AsString());
        Assert.Equal("w1:pB", keptTab.Member("pane")!.Value.AsString());
        Assert.Null(kept.Get("tabs_closed"));
        Assert.Contains(herdr.Tabs, t => t.Id == "tS");
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("tab") && argv.Contains("close"));
    }

    [Fact]
    public void DismissingTheLastMemberOfAStreamThatIsStillOpenLeavesItsTabAlone()
    {
        var herdr = WithStreamTab("open", "pA");

        var closed = DismissClosing("proj-implementor");

        Assert.Null(closed.Get("tabs_closed"));
        Assert.Null(closed.Get("tabs_kept"));
        Assert.Contains(herdr.Tabs, t => t.Id == "tS");
    }

    [Fact]
    public void StreamDoneClosesItsTabOnlyWhenNoMemberPaneIsLeftInIt()
    {
        var herdr = WithStreamTab("open");
        var (code, stdout, err) = Roster("stream-done", "s1", "--team", "proj");
        Assert.True(code == 0, err);
        Assert.Equal("tS", JsonText.Parse(stdout).Member("tab_closed")!.Value.AsString());
        Assert.DoesNotContain(herdr.Tabs, t => t.Id == "tS");

        var live = WithStreamTab("open", "pB");
        _panes.HarnessPanes.Add("w1:pB");
        var (liveCode, liveOut, liveErr) = Roster("stream-done", "s1", "--team", "proj");
        Assert.True(liveCode == 0, liveErr);
        Assert.Equal("harness running", JsonText.Parse(liveOut).Member("tab_kept")!.Value.AsString());
        Assert.Contains(live.Tabs, t => t.Id == "tS");
    }

    [Fact]
    public void StreamStatusSaysHowManyMemberPanesADoneStreamTabHoldsAndWhetherItIsClosable()
    {
        WithStreamTab("done", "pA");
        _panes.HarnessPanes.Add("w1:pA");
        JsonValue TabOfS1() => JsonText.Parse(Roster("stream-status", "--team", "proj").Stdout).Member("streams")!.Value.AsArray()[0].Member("tab")!.Value;
        var held = TabOfS1();
        Assert.Equal(1, held.Member("member_panes")!.Value.AsNumber());
        Assert.False(held.Member("closable")!.Value.AsBool());

        WithStreamTab("done");
        var empty = TabOfS1();
        Assert.Equal(0, empty.Member("member_panes")!.Value.AsNumber());
        Assert.True(empty.Member("closable")!.Value.AsBool());

        WithStreamTab("open", "pA");
        var open = TabOfS1();
        Assert.Null(open.Member("member_panes"));
    }

    private JsonObject StreamDoneS1()
    {
        var (code, stdout, err) = Roster("stream-done", "s1", "--team", "proj");
        Assert.True(code == 0, err + stdout);
        return JsonText.Parse(stdout).AsObject();
    }

    private void AssertTabKept(JsonObject result, HerdrFake herdr, string reason, string? pane)
    {
        Assert.Equal(reason, result["tab_kept"].AsString());
        Assert.Null(result.Get("tab_closed"));
        if (pane is null)
        {
            Assert.Null(result.Get("tab_kept_pane"));
        }
        else
        {
            Assert.Equal(pane, result["tab_kept_pane"].AsString());
        }

        Assert.Contains(herdr.Tabs, t => t.Id == "tS");
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[1] == "tab" && argv[2] == "close");
    }

    [Fact]
    public void AUserShellInAStreamTabKeepsTheTabAndNamesThePane()
    {
        var herdr = WithStreamTab("open");
        AddForeign(herdr, "tS", "w1:pShell");

        AssertTabKept(StreamDoneS1(), herdr, "foreign pane", "w1:pShell");
    }

    [Fact]
    public void TheOrchestratorsOwnPaneInAStreamTabKeepsTheTab()
    {
        var herdr = WithStreamTab("open");
        AddForeign(herdr, "tS", "pOrch");

        AssertTabKept(StreamDoneS1(), herdr, "foreign pane", "pOrch");
    }

    [Fact]
    public void AnotherTeamsMemberPaneInAStreamTabKeepsTheTab()
    {
        var herdr = WithStreamTab("open");
        WriteTeam("other", LiveTeamJson("herdr", "{\"role\":\"implementor\",\"name\":\"other-implementor\",\"route\":\"peer\",\"transport_id\":\"w1:pX\",\"tab_id\":\"tX\",\"model\":\"sonnet\"}"));
        AddForeign(herdr, "tS", "w1:pX");

        AssertTabKept(StreamDoneS1(), herdr, "foreign pane", "w1:pX");
    }

    [Fact]
    public void DeadMemberPanesAndTheRecordedRootShellDoNotKeepTheTab()
    {
        var herdr = WithStreamTab("open", "pA");
        RecordRoot(herdr, "w1:pR");

        var done = StreamDoneS1();

        Assert.Equal("tS", done["tab_closed"].AsString());
        Assert.DoesNotContain(herdr.Tabs, t => t.Id == "tS");
    }

    [Fact]
    public void ARunningHarnessInAnAhCreatedPaneKeepsTheTab()
    {
        var herdr = WithStreamTab("open", "pA");
        _panes.HarnessPanes.Add("w1:pA");

        AssertTabKept(StreamDoneS1(), herdr, "harness running", "w1:pA");
    }

    [Fact]
    public void APaneWhoseProcessTreeCannotBeReadCountsAsRunningAHarness()
    {
        var herdr = WithStreamTab("open", "pA");
        _panes.TabSource = tab => herdr.Panes.Where(p => p.Tab == tab).Select(p => (p.Pane, (int?)null)).ToList();

        AssertTabKept(StreamDoneS1(), herdr, "harness running", "w1:pA");
    }

    [Fact]
    public void APaneListThatCannotBeHadKeepsTheTab()
    {
        var herdr = WithStreamTab("open");
        _panes.TabSource = null;

        AssertTabKept(StreamDoneS1(), herdr, "panes unknown", null);
    }

    [Fact]
    public void ATabRecordedWithNoRootPaneCountsItsShellAsForeign()
    {
        var herdr = WithStreamTab("open");
        herdr.Panes.Add(("w1:pR", "tS"));

        AssertTabKept(StreamDoneS1(), herdr, "foreign pane", "w1:pR");
    }

    [Fact]
    public void DisbandClosesTheCleanStreamTabAndKeepsTheOneHoldingAUserShell()
    {
        var herdr = WithTwoDoneStreams();
        AddForeign(herdr, "tT", "w1:pShell");

        var closed = DisbandClosing();

        Assert.Equal(["tS"], closed["tabs_closed"].AsArray().Select(t => t.AsString()).ToArray());
        var kept = closed["tabs_kept"].AsArray().Single();
        Assert.Equal("tT", kept.Member("tab")!.Value.AsString());
        Assert.Equal("foreign pane", kept.Member("reason")!.Value.AsString());
        Assert.Equal("w1:pShell", kept.Member("pane")!.Value.AsString());
        Assert.Contains(herdr.Tabs, t => t.Id == "tT");
    }

    [Fact]
    public void StreamStatusClosableAgreesWithTheCloseDecision()
    {
        var herdr = WithStreamTab("done");
        JsonValue TabOfS1() => JsonText.Parse(Roster("stream-status", "--team", "proj").Stdout).Member("streams")!.Value.AsArray()[0].Member("tab")!.Value;
        Assert.True(TabOfS1().Member("closable")!.Value.AsBool());

        AddForeign(herdr, "tS", "w1:pShell");
        var foreign = TabOfS1();
        Assert.False(foreign.Member("closable")!.Value.AsBool());
        Assert.Equal("foreign pane", foreign.Member("kept")!.Value.AsString());
        Assert.Equal("w1:pShell", foreign.Member("kept_pane")!.Value.AsString());

        herdr.Panes.RemoveAll(p => p.Pane == "w1:pShell");
        _panes.TabSource = null;
        var unknown = TabOfS1();
        Assert.False(unknown.Member("closable")!.Value.AsBool());
        Assert.Equal("panes unknown", unknown.Member("kept")!.Value.AsString());
    }

    [Fact]
    public void ReapClosesNoStreamTab()
    {
        var herdr = WithStreamTab("done");

        var (code, _, err) = Roster("reap", "--commit");

        Assert.True(code == 0, err);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[1] == "tab" && argv[2] == "close");
        Assert.Contains(herdr.Tabs, t => t.Id == "tS");
    }

    [Fact]
    public void OpeningAStreamMakesNoTabAndRecordsNoTabOrRootPane()
    {
        var herdr = new HerdrFake();
        PutHerdrOnPath();
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HERDR_PANE_ID"] = "pOrch";
        _extraEnv["HERDR_TAB_ID"] = "tCaller";
        _runner.Output = herdr.Answer;
        _runner.Exit = herdr.Exit;
        WriteTeam("proj", LiveTeamJson("herdr", "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"w1:pA\",\"tab_id\":\"tCaller\",\"model\":\"sonnet\"}"));
        var panes = herdr.Panes.Count;

        var (code, stdout, err) = Roster("stream-open", "s1", "--no-worktree", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, err + stdout);
        var record = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("streams")!.Value.Member("s1")!.Value;
        Assert.True(record.Member("tab_id")!.Value.IsNull);
        Assert.Null(record.Member("root_pane_id"));
        Assert.Equal(panes, herdr.Panes.Count);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("tab") && argv.Contains("create"));
    }

    [Fact]
    public void TheFirstSpawnIntoAnUnjoinedStreamMakesItsTabAndRecordsTheTabAndRootPane()
    {
        var herdr = new HerdrFake();
        PutHerdrOnPath();
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HERDR_PANE_ID"] = "pOrch";
        _extraEnv["HERDR_TAB_ID"] = "tCaller";
        _runner.Output = herdr.Answer;
        _runner.Exit = herdr.Exit;
        WriteTeam("proj", LiveTeamJson("herdr", "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"w1:pA\",\"tab_id\":\"tCaller\",\"model\":\"sonnet\"}"));
        Assert.Equal(0, Roster("stream-open", "s1", "--no-worktree", "--team", "proj", "--orchestrator-pid", Self).Code);
        var before = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("streams")!.Value.Member("s1")!.Value;
        Assert.True(before.Member("tab_id")!.Value.IsNull);

        var (code, stdout, err) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--cwd", Project);

        Assert.True(code == 0, err + stdout);
        var after = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("streams")!.Value.Member("s1")!.Value;
        Assert.False(after.Member("tab_id")!.Value.IsNull);
        Assert.False(after.Member("root_pane_id")!.Value.IsNull);
        Assert.DoesNotContain(".claude", TabCreateCwd(), StringComparison.Ordinal);
    }

    private string TabCreateCwd()
    {
        var create = _runner.Calls.Last(argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "tab" && argv[2] == "create");
        return create[Array.IndexOf(create, "--cwd") + 1];
    }

    [Fact]
    public void ASpawnIntoAReopenedStreamWhoseOldTabIsGoneLaunchesInTheStreamsWorkTree()
    {
        var herdr = new HerdrFake();
        PutHerdrOnPath();
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HERDR_PANE_ID"] = "pOrch";
        _extraEnv["HERDR_TAB_ID"] = "tCaller";
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        var tree = MakeWorkTree("s1");
        _runner.Output = argv => argv.Contains("worktree") && argv.Contains("list")
            ? $"worktree {Project}\nHEAD 0000000000000000000000000000000000000000\nbranch refs/heads/main\n\nworktree {tree}\nHEAD 1111111111111111111111111111111111111111\nbranch refs/heads/feat/s1\n"
            : herdr.Answer(argv);
        _runner.Exit = herdr.Exit;
        WriteTeam("proj", LiveTeamJson("herdr", string.Empty).TrimEnd('}')
            + ",\"streams\":{\"s1\":{\"state\":\"done\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":\"" + tree
            + "\",\"tab_id\":\"tOld\",\"opened\":\"2026-10-11T00:00:00.000Z\",\"closed\":\"2026-10-11T02:00:00.000Z\"}}}");

        var (openCode, openOut, openErr) = Roster("stream-open", "s1", "--team", "proj", "--orchestrator-pid", Self);
        Assert.True(openCode == 0, openErr + openOut);
        Assert.True(JsonText.Parse(openOut).Member("reopened")!.Value.AsBool());
        var (code, stdout, err) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--cwd", Project);

        Assert.True(code == 0, err + stdout);
        Assert.Equal(tree, TabCreateCwd());
        var root = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("streams")!.Value.Member("s1")!.Value.Member("root_pane_id")!.Value.AsString();
        Assert.Contains($"--pane {root}", _runner.Calls.Last(argv => argv[0] == "/bin/sh")[^1], StringComparison.Ordinal);
    }

    private (int Code, string Launch, HerdrFake Herdr) SpawnIntoStreamTabHolding(string pane)
    {
        var herdr = WithStreamTab("open");
        herdr.Panes.RemoveAll(p => p.Pane == "pOrch");
        herdr.Panes.Add((pane, "tS"));
        var (code, stdout, err) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--cwd", Project);
        Assert.True(code == 0, err + stdout);
        var launch = _runner.Calls.Where(argv => argv[0] == "/bin/sh").Select(argv => argv[^1]).LastOrDefault() ?? string.Empty;
        return (code, launch, herdr);
    }

    [Fact]
    public void ASpawnIntoAStreamTabWhoseOnlyPaneIsTheCallersOwnSplitsItAndNeverLaunchesIntoIt()
    {
        var (_, launch, _) = SpawnIntoStreamTabHolding("pOrch");

        Assert.Contains(_runner.Calls, argv => argv.Length > 4 && argv[1] == "pane" && argv[2] == "split" && argv.Contains("pOrch"));
        Assert.DoesNotContain("--pane pOrch", launch, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpawnIntoAStreamTabWhoseOnlyPaneIsAnotherSessionsShellStillLaunchesIntoThatPaneWithoutASplit()
    {
        var (_, launch, _) = SpawnIntoStreamTabHolding("pShell");

        Assert.Contains("--pane pShell", launch, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[1] == "pane" && argv[2] == "split");
    }

    [Fact]
    public void ASpawnIntoAStreamTabWhoseOnlyPaneRunsAHarnessSplitsItAndNeverLaunchesIntoIt()
    {
        _panes.HarnessPanes.Add("pBusy");

        var (_, launch, _) = SpawnIntoStreamTabHolding("pBusy");

        Assert.Contains(_runner.Calls, argv => argv.Length > 4 && argv[1] == "pane" && argv[2] == "split" && argv.Contains("pBusy"));
        Assert.DoesNotContain("--pane pBusy", launch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoConcurrentFirstSpawnsIntoAnUnjoinedStreamMakeOneTabAndBothLandInIt()
    {
        var herdr = new HerdrFake();
        PutHerdrOnPath();
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HERDR_PANE_ID"] = "pOrch";
        _extraEnv["HERDR_TAB_ID"] = "tCaller";
        _runner.Output = herdr.Answer;
        _runner.Exit = herdr.Exit;
        WriteTeam("proj", LiveTeamJson("herdr", "{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"w1:pA\",\"tab_id\":\"tCaller\",\"model\":\"sonnet\"}"));
        Assert.Equal(0, Roster("stream-open", "s1", "--no-worktree", "--team", "proj", "--orchestrator-pid", Self).Code);
        var tabsBefore = herdr.Panes.Select(p => p.Tab).Distinct().Count();

        var first = Task.Run(() => Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--cwd", Project));
        var second = Task.Run(() => Roster("spawn-ad-hoc", "reviewer", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--cwd", Project));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.True(r.Code == 0, r.Stderr + r.Stdout));
        var team = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        var tab = team.Member("streams")!.Value.Member("s1")!.Value.Member("tab_id")!.Value.AsString();
        Assert.Equal(tabsBefore + 1, herdr.Panes.Select(p => p.Tab).Distinct().Count());
        var joined = team.Member("members")!.Value.AsArray().Where(m => m.Member("stream") is { IsText: true }).ToList();
        Assert.Equal(2, joined.Count);
        Assert.All(joined, m => Assert.Contains(herdr.Panes, p => p.Tab == tab && p.Pane == m.Member("transport_id")!.Value.AsString().Split(':').Last()));
    }

    [Fact]
    public void AMoveOutOfAZoomedTabUnzoomsMovesAndZoomsTheSamePaneBack()
    {
        WithMemberToMove().Zoom["tSrc"] = "pB";

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.True(moved["moved"].AsBool());
        Assert.Equal("restored", moved["rezoom"].AsString());
        var off = CallIndex("herdr", "pane", "zoom", "pB", "--off");
        var move = CallIndex("herdr", "pane", "move", "pA");
        var on = CallIndex("herdr", "pane", "zoom", "pB", "--on");
        Assert.True(off >= 0 && off < move && move < on, $"order was off={off} move={move} on={on}");
    }

    [Fact]
    public void AMoveHerdrAnswersAsChangedButThatLeavesThePaneInItsTabIsNotMovedEvenWithAStaleRecord()
    {
        var herdr = WithMemberToMove(recordedTab: "tOld");
        herdr.LieAboutMove = true;

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.False(moved["moved"].AsBool());
        Assert.Contains("same tab", moved["reason"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMoveReplyWithNoMoveResultThatLeftThePaneInItsTabIsNotMoved()
    {
        var herdr = WithMemberToMove(recordedTab: "tOld");
        herdr.OmitMoveResult = true;
        herdr.LieAboutMove = true;

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.False(moved["moved"].AsBool());
        Assert.Contains("same tab", moved["reason"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMoveReplyWithNoMoveResultAfterWhichThePaneIsInANewTabIsMoved()
    {
        var herdr = WithMemberToMove();
        herdr.OmitMoveResult = true;

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.True(moved["moved"].AsBool());
    }

    [Fact]
    public void AMoveWhosePreMoveTabCannotBeReadIsNotReportedAsMovedEvenWithAStaleRecord()
    {
        var herdr = WithMemberToMove(recordedTab: "tOld");
        herdr.HideTab = true;

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.False(moved["moved"].AsBool());
        Assert.Contains("cannot confirm", moved["reason"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMoveOutOfATabThatIsNotZoomedTouchesNoZoom()
    {
        WithMemberToMove();

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.True(moved["moved"].AsBool());
        Assert.False(moved.ContainsKey("rezoom"));
        Assert.False(Called("herdr", "pane", "zoom"));
    }

    [Fact]
    public void AZoomedTabRefusalIsSurfacedWhenTheZoomCannotBeRead()
    {
        var herdr = WithMemberToMove();
        herdr.Zoom["tSrc"] = "pB";
        herdr.HideZoom = true;

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.False(moved["moved"].AsBool());
        Assert.Equal("zoomed_tab", moved["reason"].AsString());
    }

    [Fact]
    public void AMoveThatFailsAfterTheUnzoomStillZoomsBackAndFails()
    {
        var herdr = WithMemberToMove();
        herdr.Zoom["tSrc"] = "pB";
        herdr.FailMove = true;

        var (code, _) = MoveToNewTab();

        Assert.NotEqual(0, code);
        Assert.True(Called("herdr", "pane", "zoom", "pB", "--on"));
    }

    [Fact]
    public void WhenTheZoomedPaneIsTheOneMovedItIsNotZoomedBackAndSaysSo()
    {
        WithMemberToMove().Zoom["tSrc"] = "pA";

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.True(moved["moved"].AsBool());
        Assert.Equal("skipped (moved pane was zoomed)", moved["rezoom"].AsString());
        Assert.False(Called("herdr", "pane", "zoom", "--on"));
    }

    [Fact]
    public void AnUnzoomThatFailsMeansNoMove()
    {
        var herdr = WithMemberToMove();
        herdr.Zoom["tSrc"] = "pB";
        herdr.FailUnzoom = true;

        var (code, moved) = MoveToNewTab();

        Assert.Equal(0, code);
        Assert.False(moved["moved"].AsBool());
        Assert.False(Called("herdr", "pane", "move"));
    }

    [Fact]
    public void JoiningAZoomedRoleTabUnzoomsSplitsAndZoomsBack()
    {
        var herdr = OnHerdr(ReviewerRow("proj-reviewer", "pRev", "tRev"));
        herdr.Tabs.Add(("tRev", "proj: reviewer"));
        herdr.Panes.Add(("pRev", "tRev"));
        herdr.Panes.Add(("pOther", "tRev"));
        herdr.Zoom["tRev"] = "pOther";

        var (code, _) = SpawnReviewer();

        Assert.Equal(0, code);
        var off = CallIndex("herdr", "pane", "zoom", "pOther", "--off");
        var split = CallIndex("herdr", "pane", "split", "pRev");
        var on = CallIndex("herdr", "pane", "zoom", "pOther", "--on");
        Assert.True(off >= 0 && off < split && split < on, $"order was off={off} split={split} on={on}");
    }

    private static string ReviewerRow(string name, string pane, string tab) =>
        $"{{\"role\":\"reviewer\",\"name\":\"{name}\",\"route\":\"peer\",\"transport_id\":\"{pane}\",\"tab_id\":\"{tab}\",\"model\":\"sonnet\"}}";

    private (int Code, JsonObject Out) SpawnReviewer(params string[] extra)
    {
        var (code, stdout, _) = Roster(["spawn-ad-hoc", "reviewer", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, .. extra]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject());
    }

    private bool Called(params string[] words) =>
        _runner.Calls.Any(argv => argv.Length > 0 && words.All(w => argv.Contains(w)));

    [Fact]
    public void TheFirstOfARoleOpensItsOwnLabelledTabAndNeverSplitsTheCallersPane()
    {
        var herdr = OnHerdr(string.Empty);

        var (code, spawned) = SpawnReviewer();

        Assert.Equal(0, code);
        Assert.True(spawned["spawned"].AsBool());
        Assert.Contains(herdr.Tabs, t => t.Label == "proj: reviewer");
        Assert.True(Called("herdr", "tab", "create", "--label", "proj: reviewer"));
        Assert.False(Called("herdr", "pane", "split", "pOrch"));
        Assert.Equal("t1", spawned["member"].AsObject()["tab_id"].AsString());
    }

    [Fact]
    public void ASecondOfARoleSplitsInTheFirstsTab()
    {
        var herdr = OnHerdr(ReviewerRow("proj-reviewer", "pRev", "tRev"));
        herdr.Tabs.Add(("tRev", "proj: reviewer"));
        herdr.Panes.Add(("pRev", "tRev"));

        var (code, spawned) = SpawnReviewer();

        Assert.Equal(0, code);
        Assert.True(Called("herdr", "pane", "split", "pRev"));
        Assert.False(Called("herdr", "tab", "create"));
        Assert.False(Called("herdr", "pane", "split", "pOrch"));
        Assert.Equal("tRev", spawned["member"].AsObject()["tab_id"].AsString());
    }

    [Fact]
    public void ASameRoleMemberInTheCallersOwnTabDoesNotAttractTheNewMember()
    {
        var herdr = OnHerdr(ReviewerRow("proj-reviewer", "pRev", "tCaller"));
        herdr.Panes.Add(("pRev", "tCaller"));

        var (code, _) = SpawnReviewer();

        Assert.Equal(0, code);
        Assert.True(Called("herdr", "tab", "create", "--label", "proj: reviewer"));
        Assert.False(Called("herdr", "pane", "split", "pRev"));
        Assert.False(Called("herdr", "pane", "split", "pOrch"));
    }

    [Fact]
    public void ASameRoleMemberWhoseTabIsGoneGetsANewTab()
    {
        var herdr = OnHerdr(ReviewerRow("proj-reviewer", "pRev", "tGone"));
        herdr.Panes.Add(("pRev", "tGone"));

        var (code, _) = SpawnReviewer();

        Assert.Equal(0, code);
        Assert.True(Called("herdr", "tab", "create"));
        Assert.False(Called("herdr", "pane", "split", "pRev"));
    }

    [Fact]
    public void ASplitFlagSplitsTheCallersPaneAndOpensNoTab()
    {
        OnHerdr(string.Empty);

        var (code, _) = SpawnReviewer("--split");

        Assert.Equal(0, code);
        Assert.True(Called("herdr", "pane", "split", "pOrch"));
        Assert.False(Called("herdr", "tab", "create"));
    }
}
