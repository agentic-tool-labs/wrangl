using System.Globalization;
using Ah.Core.Json;
using Ah.Tui.Core;
using static Ah.Tui.Tests.TreeFixtures;

namespace Ah.Tui.Tests;

/// <summary>The Sitrep's model and its stacked lines: regions, one line a row, what starts folded, and what each row opens.</summary>
public sealed class SitrepViewTests
{
    private const string TeamId = "team:d1/alpha";

    private static readonly string[] Headings = ["[-] Completed 2 of 39", "[-] In progress 2", "[-] Up next 3", "[-] Blocked 1", "[+] Stale 1"];

    private static string Ms(int minutes) => TeamsFixtures.Now.AddMinutes(-minutes).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static string Done(string key, string title, int at) =>
        "{\"kind\":\"item\",\"key\":\"" + key + "\",\"title\":\"" + title + "\",\"team\":\"alpha\",\"stream\":\"s1\",\"lane\":null,\"holder\":\"impl\",\"done_at\":" + Ms(at) + "}";

    private static string Open(string key, string title, string state, int since, string? lane = null, string holder = "impl") =>
        "{\"key\":\"" + key + "\",\"title\":\"" + title + "\",\"team\":\"alpha\",\"stream\":\"s1\",\"lane\":" + (lane is null ? "null" : "\"" + lane + "\"") + ",\"holder\":\"" + holder + "\",\"state\":\"" + state + "\",\"since\":" + Ms(since) + ",\"last_activity\":" + Ms(since) + "}";

    private static string Queued(string key, string title, string role, int since, int stall, params string[] idle) =>
        "{\"key\":\"" + key + "\",\"title\":\"" + title + "\",\"team\":\"alpha\",\"stream\":\"s1\",\"lane\":null,\"role\":\"" + role + "\",\"queued_since\":" + Ms(since) + ",\"stall_at\":" + Ms(stall) + ",\"could_take\":[" + string.Join(',', idle.Select(static i => "\"" + i + "\"")) + "],\"source\":\"queue\"}";

    private const string BacklogRow = "{\"key\":\"b1\",\"title\":\"From the backlog\",\"team\":null,\"stream\":null,\"lane\":null,\"role\":null,\"queued_since\":null,\"stall_at\":null,\"could_take\":[],\"source\":\"backlog\"}";

    private const string Decision = "{\"kind\":\"decision\",\"key\":\"d1\",\"title\":\"Pick one\",\"team\":\"alpha\"}";

    private static string Ship => "{\"kind\":\"ship\",\"key\":\"ship:abc\",\"sha\":\"abcdef123456\",\"ref\":\"main\",\"tag\":\"v1\",\"team\":\"alpha\",\"lane\":null,\"at\":" + Ms(10) + "}";

    private static string Body(string[]? completed = null, string[]? progress = null, string[]? blocked = null, string[]? stale = null, string[]? next = null, string[]? waiting = null, int total = 39)
    {
        completed ??= [Ship, Done("s1:done", "Done one", 2), Done("s1:old", "Done long ago", 800)];
        progress ??= [Open("s1:build", "Build it", "in_progress", 52, "lane-ui"), Open("s1:ask", "Ask one", "waiting_user", 7)];
        blocked ??= [Open("s1:fix", "Fix it", "blocked", 12)];
        stale ??= [Open("s1:stale", "Stale one", "in_progress", 120, holder: "qa")];
        next ??= [Queued("s1:q1", "Stalled one", "reviewer", 3, 1, "rev"), Queued("s1:q2", "Waiting one", "implementor", 3, -20), BacklogRow];
        waiting ??= [];
        static string L(string[] r) => "[" + string.Join(',', r) + "]";
        return "{" + ScreenFixtures.SitrepRegions + ",\"as_of\":" + Ms(2) + ",\"completed_total\":" + total.ToString(CultureInfo.InvariantCulture) + ",\"in_progress_total\":" + progress.Length.ToString(CultureInfo.InvariantCulture)
            + ",\"blocked_total\":" + blocked.Length.ToString(CultureInfo.InvariantCulture) + ",\"stale_total\":" + stale.Length.ToString(CultureInfo.InvariantCulture)
            + ",\"completed\":" + L(completed) + ",\"in_progress\":" + L(progress) + ",\"blocked\":" + L(blocked) + ",\"stale\":" + L(stale) + ",\"up_next\":" + L(next) + ",\"waiting_on_you\":" + L(waiting) + "}";
    }

    private static Dictionary<string, JsonValue> Slices(string? sitrep = null) => new(StringComparer.Ordinal)
    {
        ["list:teams"] = Parse("{\"teams\":[{\"id\":\"" + TeamId + "\",\"name\":\"alpha\",\"dir_id\":\"d1\"}]}"),
        ["team-summary:d1/alpha"] = Parse("{\"name\":\"alpha\",\"needs_you\":2,\"working\":3,\"blocked\":1}"),
        [TeamId] = Parse("{\"name\":\"alpha\"}"),
        ["sitrep:d1/alpha"] = Parse(sitrep ?? Body()),
    };

    private static SitrepModel Model(Dictionary<string, JsonValue> slices, IReadOnlyDictionary<string, int>? seen = null) =>
        TeamSlices.Sitrep(slices, TeamId, seen, TeamsFixtures.Now)!;

    private static List<SitrepView.StackLine> Stack(SitrepModel m, params string[] toggled) =>
        SitrepView.StackedLines(m, new HashSet<string>(toggled, StringComparer.Ordinal));

    private static string Text(SitrepModel m, params string[] toggled) =>
        string.Join("\n", Stack(m, toggled).Select(static l => l.Text));

    [Fact]
    public void RegionsStackInOrderWithTheirServedTotals()
    {
        var text = Text(Model(Slices()));
        var order = Headings.Select(h => text.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void ACountWithNothingCutIsJustTheCount()
    {
        var text = Text(Model(Slices(Body(total: 2))));
        Assert.Contains("[-] Completed 2\n", text, StringComparison.Ordinal);
        Assert.Contains("Shipped", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowIsItsTitleAloneAndNegativeNoStateLaneHolderOrAge()
    {
        var lines = Stack(Model(Slices()));
        Assert.Equal("Build it", lines.Single(static l => l.Text.Contains("Build it", StringComparison.Ordinal)).Text);
        Assert.Equal("Fix it", lines.Single(static l => l.Text.Contains("Fix it", StringComparison.Ordinal)).Text);
        Assert.Equal("Stalled one", lines.Single(static l => l.Text.Contains("Stalled one", StringComparison.Ordinal)).Text);
        Assert.Equal("From the backlog", lines.Single(static l => l.Text.Contains("backlog", StringComparison.Ordinal)).Text);
    }

    [Fact]
    public void AnItemIsAStopAndNegativeAShipRecordOrDecisionIsNot()
    {
        var lines = Stack(Model(Slices(Body(waiting: [Decision]))));
        Assert.Equal(SitrepView.ItemStop + TeamId + "|s1|build", lines.Single(static l => l.Text == "Build it").Stop);
        Assert.Equal(SitrepView.ItemStop + "bl:d1/b1", lines.Single(static l => l.Text == "From the backlog").Stop);
        Assert.Null(lines.Single(static l => l.Text.StartsWith("Shipped main", StringComparison.Ordinal)).Stop);
        Assert.Null(lines.Single(static l => l.Text == "Pick one").Stop);
    }

    [Fact]
    public void TheThreeShowAtZeroAndNegativeAnEmptyOtherIsLeftOut()
    {
        var text = Text(Model(Slices(Body(completed: [], progress: [], next: [], blocked: [], stale: [], total: 0))));
        Assert.Equal("[-] Completed 0\n[-] In progress 0\n[-] Up next 0", text);
    }

    [Fact]
    public void FlippingARegionFoldsItAndNegativeLeavesTheOthersAlone()
    {
        var m = Model(Slices());
        var text = Text(m, "in_progress");
        Assert.Contains("[+] In progress 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Build it", text, StringComparison.Ordinal);
        Assert.Contains("Done one", text, StringComparison.Ordinal);
        Assert.Contains("Fix it", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FlippingARegionThatStartsFoldedOpensItAndNegativeUnflippedItShowsNoRows()
    {
        var m = Model(Slices());
        Assert.DoesNotContain("Stale one", Text(m), StringComparison.Ordinal);
        Assert.Contains("Stale one", Text(m, "stale"), StringComparison.Ordinal);
        Assert.Contains("[-] Stale 1", Text(m, "stale"), StringComparison.Ordinal);
    }

    [Fact]
    public void WhatLeftIsCountedFromTheBaseline()
    {
        var first = Slices();
        var seen = TeamSlices.SitrepSnap(first, TeamId);
        var later = Model(Slices(Body(
            completed: [Done("s1:done", "Done one", 2)],
            progress: [Open("s1:build", "Build it", "in_progress", 52, "lane-ui"), Open("s1:new", "Brand new", "in_progress", 1)],
            blocked: [Open("s1:ask", "Ask one", "blocked", 1)],
            stale: [])), seen);

        // Gone since: the ship record, s1:old, s1:fix and s1:stale.
        Assert.Equal(4, later.Left);
    }

    [Fact]
    public void AServedEscapeIsNotDrawn()
    {
        var text = Text(Model(Slices(Body(blocked: [Open("s1:x", "\\u001b[31mred title", "blocked", 5)]))));
        Assert.Contains("red title", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }
}
