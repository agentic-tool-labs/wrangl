using Ah.Core.Json;
using Ah.Tui.Core;

namespace Ah.Tui.Tests;

/// <summary>The read model of a daemon with one team, its lane, another team and an ended one, as the three screens read it.</summary>
internal static class ScreenFixtures
{
    internal const string Team = "team:d1/ah2-tm4";
    internal const string Lane = "team:d1/ah2-tm4-tui";
    internal const string Other = "team:d1/engram";
    internal const string Ended = "team:d1/old";
    internal const string Orch = "member:d1/ah2-tm4/orchestrator";
    internal const string Impl = "member:d1/ah2-tm4/impl-41";
    internal const string Reviewer = "member:d1/ah2-tm4/reviewer-9";

    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private const long NowMs = 1793768400000;

    private static long Ago(int minutes) => NowMs - (minutes * 60_000L);

    private static string At(int minutes) => Now.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    internal static Dictionary<string, JsonValue> Slices() => new(StringComparer.Ordinal)
    {
        ["list:teams"] = J($$"""{"teams":[{"id":"{{Team}}"},{"id":"{{Lane}}"},{"id":"{{Other}}"},{"id":"{{Ended}}"}]}"""),
        ["team-summary:d1/ah2-tm4"] = J($$"""
            {"name":"ah2-tm4","state":"needs_you","needs_you":1,"plan_loaded":true,"working":2,"blocked":0,"items_done":4,"items_total":7,"updated_at":"{{At(3)}}",
             "member_states":{
               "{{Orch}}":{"state":"working","state_at":"{{At(2)}}","liveness":"live","item":"Ship train 22"},
               "{{Impl}}":{"state":"working","state_at":"{{At(8)}}","liveness":"live","tool":"Edit","branch":"tui-trim"},
               "{{Reviewer}}":{"state":"gone","state_at":"{{At(60)}}","liveness":"proven_dead"} },
             "liveness_counts":{"live":2,"unknown":0,"not_seen":0},
             "alerts":[{"kind":"needs_you","title":"A decision waits","since":"{{At(12)}}"}]}
            """),
        ["team-summary:d1/ah2-tm4-tui"] = J($$"""{"name":"ah2-tm4-tui","state":"working","needs_you":0,"plan_loaded":true,"working":1,"blocked":1,"items_done":3,"items_total":5,"updated_at":"{{At(12)}}"}"""),
        ["team-summary:d1/engram"] = J($$"""{"name":"engram","state":"idle","held":true,"needs_you":0,"plan_loaded":false,"working":0,"blocked":0,"items_done":2,"items_total":2,"updated_at":"{{At(90)}}"}"""),
        ["team-summary:d1/old"] = J($$"""{"name":"old","state":"ended","ended":true,"needs_you":0,"working":0,"blocked":0,"items_done":1,"items_total":1,"updated_at":"{{At(2880)}}"}"""),
        [Team] = J($$"""{"name":"ah2-tm4","orchestrator":"{{Orch}}","members":["{{Impl}}","{{Reviewer}}"],"plan":"plan:d1/ah2-tm4"}"""),
        [Lane] = J("""{"name":"ah2-tm4-tui","lane":{"parent_team":"ah2-tm4"},"members":["member:d1/ah2-tm4-tui/ux-1"]}"""),
        [Other] = J("""{"name":"engram","members":[]}"""),
        [Ended] = J("""{"name":"old","members":[]}"""),
        [Orch] = J("""{"name":"ah2-tm4-orchestrator","role":"orchestrator"}"""),
        [Impl] = J("""{"name":"ah2-tm4-impl-41","role":"implementor"}"""),
        [Reviewer] = J("""{"name":"ah2-tm4-reviewer-9","role":"reviewer"}"""),
        ["list:open:" + Team] = J($$"""{"open":[{"id":"exchange:d1/x1","to_member":"{{Impl}}"},{"id":"exchange:d1/x2","to_member":"{{Reviewer}}"}]}"""),
        ["exchange:d1/x1"] = J($$"""{"title":"Trim the terminal UI","created":"{{At(8)}}"}"""),
        ["exchange:d1/x2"] = J($$"""{"title":"Review the trim","created":"{{At(50)}}"}"""),
        ["sitrep:d1/ah2-tm4"] = Sr($$"""
            {"as_of":{{Ago(1)}},
             "waiting_on_you":[{"key":"w1","title":"Pick the DB migration order","lane":"main","holder":"{{Orch}}","since":{{Ago(12)}},"state":"waiting_user"}],"waiting_on_you_total":1,
             "in_progress":[{"key":"p1","title":"Trim the terminal UI","lane":"tui-trim","holder":"{{Impl}}","since":{{Ago(8)}},"state":"in_progress"}],"in_progress_total":1,
             "blocked":[],"blocked_total":0,
             "up_next":[],
             "up_next_old":[],"up_next_old_total":0,
             "completed":[{"key":"c1","kind":"ship","ref":"ship-21","sha":"fb04b16cb","at":{{Ago(100)}},"lane":"main"}],"completed_total":5,
             "not_moving":[{"key":"n1","title":"Report in","work":"with_lead","since":{{Ago(30)}}}],"not_moving_total":1,
             "stale":[],"stale_total":0}
            """),
        ["plan:d1/ah2-tm4"] = J("""{"streams":[{"stream":"main"},{"stream":"tui-trim"}]}"""),
        ["plan-items:d1/ah2-tm4/main"] = J($$"""{"items":[{"key":"at-12","title":"Fix message age","state":"done","holder":"ah2-tm4-impl-41","state_since":"{{At(120)}}"},{"key":"at-90","title":"Someone else's","state":"in_progress","holder":"other","state_since":"{{At(1)}}"}]}"""),
        ["plan-items:d1/ah2-tm4/tui-trim"] = J($$"""{"items":[{"key":"at-14","title":"Trim the terminal UI","state":"in_progress","holder":"{{Impl}}","state_since":"{{At(8)}}"},{"key":"at-15","title":"Also trim the docs","state":"in_progress","holder":"ah2-tm4-impl-41","state_since":"{{At(20)}}"}]}"""),
        ["backlog:d1"] = J("""{"items":[{"key":"b7","title":"Backlog item","wave":"now","open":true,"holder":"ah2-tm4-impl-41"}]}"""),
    };

    internal static JsonValue J(string json) => JsonText.Parse(json);

    /// <summary>The regions member the daemon serves in a Sitrep body, as SitrepRows composes it.</summary>
    internal const string SitrepRegions = """
        "regions":[{"col":"c","list":"completed","total":"completed_total","label":"Completed","optional":false,"folded":false},
        {"col":"p","list":"in_progress","total":"in_progress_total","label":"In progress","optional":false,"folded":false},
        {"col":"n","list":"up_next","total":null,"label":"Up next","optional":false,"folded":false},
        {"col":"w","list":"waiting_on_you","total":"waiting_on_you_total","label":"Waiting on you","optional":true,"folded":false},
        {"col":"b","list":"blocked","total":"blocked_total","label":"Blocked","optional":true,"folded":false},
        {"col":"m","list":"not_moving","total":"not_moving_total","label":"Report in, no one on it","optional":true,"folded":true},
        {"col":"s","list":"stale","total":"stale_total","label":"Stale","optional":true,"folded":true},
        {"col":"o","list":"up_next_old","total":"up_next_old_total","label":"Up next, old","optional":true,"folded":true}]
        """;

    /// <summary>A Sitrep body from its own members, with the regions the daemon serves in front.</summary>
    internal static JsonValue Sr(string json) => J("{" + SitrepRegions + "," + json.TrimStart()[1..]);

    internal static TuiScreenInput Input(IReadOnlyDictionary<string, JsonValue>? slices = null) =>
        new(slices ?? Slices(), Now, TimeZoneInfo.Utc, LastGood: Now.AddSeconds(-4));

    internal static string Text(TuiFrame frame) => string.Join("\n", frame.Lines.Select(static l => l.Text)) + "\n";

    /// <summary>One event through the real decoder, on the frame on show; the state after it.</summary>
    internal static TuiScreenState Act(TuiScreenState s, string typed, IReadOnlyDictionary<string, JsonValue>? slices = null, int cols = 100, int rows = 30)
    {
        var held = slices ?? Slices();
        foreach (var ev in TuiNav.ParseInput(typed))
        {
            (s, _) = TuiScreens.Reduce(s, ev, TuiScreens.Frame(s, Input(held), cols, rows), held);
        }

        return s;
    }

    /// <summary>A left press at a 0-based cell, as a terminal reports it.</summary>
    internal static string Click(int x, int y) => $"\u001b[<0;{x + 1};{y + 1}M";

    /// <summary>The 0-based line that shows <paramref name="text"/>.</summary>
    internal static int LineOf(TuiFrame frame, string text) => frame.Lines.ToList().FindIndex(l => l.Text.Contains(text, StringComparison.Ordinal));
}

/// <summary>A daemon's read model: the summary manifest on a plain read, and a held poll that answers what it is asked for fresh, then holds.</summary>
internal sealed class SliceSource(Func<IReadOnlyDictionary<string, JsonValue>> slices) : IViewSource
{
    private static readonly string[] SummaryIds = ["list:teams"];

    internal int SummaryReads { get; private set; }

    internal List<SlicePoll> Polls { get; } = [];

    internal ViewOutcome? Answer { get; set; }

    public Task<ViewOutcome> GetViewAsync(DaemonProbe daemon, string path, string? since, int waitSeconds, CancellationToken cancellationToken)
    {
        SummaryReads++;
        if (Answer is { } answer)
        {
            return Task.FromResult(answer);
        }

        var all = slices();
        return Task.FromResult(new ViewOutcome(ViewOutcomeKind.View, View: Reply(all.Keys.Where(static k => SummaryIds.Contains(k) || k.StartsWith("team-summary:", StringComparison.Ordinal)), all)));
    }

    public async Task<ViewOutcome> GetSlicesAsync(DaemonProbe daemon, SlicePoll poll, CancellationToken cancellationToken)
    {
        lock (Polls)
        {
            Polls.Add(poll);
        }

        if (poll.Fresh.Count > 0)
        {
            return new ViewOutcome(ViewOutcomeKind.View, View: Reply(poll.Fresh, slices()));
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // the poll ended
        }

        return ViewOutcome.Aborted;
    }

    private static JsonValue Reply(IEnumerable<string> ids, IReadOnlyDictionary<string, JsonValue> all)
    {
        var list = new JsonArray();
        foreach (var id in ids.Where(all.ContainsKey))
        {
            list.Add(new JsonObject { ["id"] = id, ["as_of"] = 1, ["body"] = all[id] });
        }

        return new JsonObject { ["seq"] = 1, ["epoch"] = "e", ["slices"] = list };
    }
}
