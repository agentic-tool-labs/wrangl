using System.Runtime.CompilerServices;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>The served <c>plan</c> document of an orchestrator: its shape, its streams and items, the caps, and where its facts come from.</summary>
public sealed class PlanDocumentTests
{
    private const string Stage1 = "6dacf39e";
    private const string Rev8 = "2869a26e";

    /// <summary>A progress reader with the records the test arranges; it serves reads only.</summary>
    private sealed class FakeProgress : IViewProgress
    {
        internal Dictionary<string, TeamProgress> Teams { get; } = [];

        internal Dictionary<(string Team, string Stream), PlanState> Plans { get; } = [];

        internal Dictionary<string, ProgressStreamInfo> Infos { get; } = [];

        public TeamProgress Read(string pool, string team) => Teams.GetValueOrDefault(team) ?? new TeamProgress(false, false, [], []);

        public PlanState? ReadPlan(string pool, string team, string stream) => Plans.GetValueOrDefault((team, stream));

        public ProgressStreamInfo Describe(string pool, string team, string stream) => Infos.GetValueOrDefault(stream) ?? new ProgressStreamInfo("—", "—", "—", null);

        /// <summary>The rows <c>queue.read</c> answers per team; a team with no entry is a reader without the op.</summary>
        internal Dictionary<string, JsonArray> Queues { get; } = [];

        internal List<(string Pool, string Team)> QueueCalls { get; } = [];

        public JsonArray? ReadQueue(string pool, string team)
        {
            QueueCalls.Add((pool, team));
            return Queues.GetValueOrDefault(team);
        }

        internal PlanState? Backlog { get; set; }

        public PlanState? ReadBacklog(string pool) => Backlog;
    }

    [Fact]
    public void TheBacklogIsListedFirstAsATeamlessStreamWithWaveAndSize()
    {
        var rig = Full();
        rig.Progress.Backlog = new PlanState(
            [Item("b1", "Groom loop", "not_started", "2026-10-17T02:00:00Z") with { Wave = "now", Size = "M", Rank = 1024 }],
            0);

        var streams = M(Warmed(rig), "streams").AsArray();

        Assert.Equal(3, streams.Count);
        Assert.Equal("backlog", M(streams[0], "stream").AsString());
        Assert.Equal("Backlog", M(streams[0], "title").AsString());
        Assert.Equal(string.Empty, M(streams[0], "team").AsString());
        var item = M(streams[0], "items").AsArray()[0];
        Assert.Equal("now", M(item, "wave").AsString());
        Assert.Equal("M", M(item, "size").AsString());
        Assert.Equal("suborch-p1", M(streams[1], "stream").AsString());
    }

    [Fact]
    public void WithNoBacklogTheStreamsAreTheTeamStreamsOnly()
    {
        var rig = Full();

        var streams = M(Warmed(rig), "streams").AsArray();

        Assert.Equal(2, streams.Count);
        Assert.DoesNotContain(streams, static s => M(s, "stream").AsString() == "backlog");
    }

    private static StreamProgress Stream(string name, int done, int open, int notStarted, string updated, bool stale, bool planned, string title) =>
        new(name, true, true, done, open, notStarted, "open", DateTimeOffset.Parse(updated, System.Globalization.CultureInfo.InvariantCulture), null, stale, false, [], planned, title);

    private static PlanItem Item(string slug, string title, string state, string added, string? started = null, string? done = null, string[]? commits = null, string? branch = null, int? issue = null, string? note = null) =>
        new(slug, title, state, added, started, done, commits ?? [], branch, issue, note);

    private static void Settle(GitRunner runner) => Assert.True(runner.WhenIdle().Wait(TimeSpan.FromSeconds(10)), "the runner did not go idle");

    private static string Golden(string name, [CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(caller)!, "Goldens", name + ".json"));

    private static JsonValue M(JsonValue v, string key) => v.Member(key)!.Value;

    private static JsonValue PlanOf(ModelSource src, string suffix = "") => M(src.Build("/plan" + src.Paths().Orch + suffix), "body");

    private static string Compact(JsonValue v) => JsonText.Stringify(v, 0);

    private sealed record Rig(ModelSource Src, FakeProgress Progress, PlanLinkageTests.FakeRepo Repo, GitRunner Runner);

    /// <summary>The arrangement the normative <c>full</c> fixture describes.</summary>
    private static Rig Full()
    {
        var progress = new FakeProgress();
        progress.Teams["ah2-tm3"] = new TeamProgress(
            false,
            true,
            [
                Stream("suborch-p1", 2, 1, 1, "2026-10-17T02:56:36Z", false, true, "sub-orchestrators Phase 1 (ah2 2.5.0)"),
                Stream("docs-241", 1, 1, 0, "2026-10-17T02:10:00Z", true, false, "docs pass for 2.4.1"),
            ],
            []);
        progress.Infos["suborch-p1"] = new ProgressStreamInfo("ah2-tm3-implementor", "ah/suborch-p1", "wt", null);
        progress.Infos["docs-241"] = new ProgressStreamInfo("ah2-tm3-docs-writer-2", "ah/docs-241", "wt", null);
        progress.Plans[("ah2-tm3", "suborch-p1")] = new PlanState(
            [
                Item("stage-1", "Stage 1: orchestrate class, spawn rules, create --parent", PlanDoneState, "2026-10-16T12:00:00Z", "2026-10-16T12:05:00Z", "2026-10-16T16:19:22Z", [Stage1], "ah/suborch-p1"),
                Item("void-stub", "Clear void stub request", "dropped", "2026-10-16T21:00:00Z", note: "not needed after rev 8"),
                Item("rev8-registered-state", "Rev 8: SO limits keyed on registered state", PlanDoneState, "2026-10-17T01:40:00Z", "2026-10-17T01:45:00Z", "2026-10-17T02:26:28Z", [Rev8], "ah/suborch-p1"),
                Item("live-trial", "Live trial after 2.4.2 ships", "in_progress", "2026-10-17T02:00:00Z", "2026-10-17T03:00:00Z", issue: 77),
                Item("push-branches", "Push ah/suborch-p1 when 2.5.0 is assembled", "not_started", "2026-10-17T02:00:00Z"),
            ],
            0);
        var repo = new PlanLinkageTests.FakeRepo { TagList = "ah2-2.4.0\t2026-10-15T18:00:00Z\nah2-2.4.1\t2026-10-17T01:30:00Z\n" };
        repo.Contains[Stage1] = "ah2-2.4.1\n";
        repo.Local[Stage1] = "ah/suborch-p1\nah/v2\n";
        repo.Remote[Stage1] = "origin/ah/suborch-p1\n";
        repo.Local[Rev8] = "ah/suborch-p1\n";
        var runner = new GitRunner(repo, () => 1_000_000);
        var src = new ModelSource { TeamName = "ah2-tm3", ProgressReader = progress, GitReader = runner };
        src.Member("impl");
        src.TreePaths.Add("/m/repo");
        src.SmallTexts["/m/repo/.git/config"] = "[remote \"origin\"]\n\turl = https://github.com/example/agent-tools.git\n";
        return new Rig(src, progress, repo, runner);
    }

    private const string PlanDoneState = "done";

    /// <summary>Builds until every git read the first builds started has landed.</summary>
    private static JsonValue Warmed(Rig rig, string suffix = "")
    {
        for (var i = 0; i < 4; i++)
        {
            _ = PlanOf(rig.Src, suffix);
            Settle(rig.Runner);
        }

        return PlanOf(rig.Src, suffix);
    }

    private static void AssertFixture(string name, JsonValue served)
    {
        var fixture = JsonText.Parse(Golden(name));
        var copy = JsonText.Parse(Compact(served));
        copy.AsObject()["generated_at"] = M(fixture, "generated_at");
        M(copy, "node").AsObject()["path"] = M(M(fixture, "node"), "path");
        Assert.Equal(Compact(fixture), Compact(copy));
    }

    [Fact]
    public void P15TheServedBodyForTheArrangedStateEqualsTheFullFixtureInKeysOrderTokensAndReasons()
    {
        var rig = Full();

        AssertFixture("plan-full", Warmed(rig));
    }

    [Fact]
    public void P15TheServedBodyWhenThereAreNoRecordsEqualsTheEmptyFixture()
    {
        var src = new ModelSource { ProgressReader = new FakeProgress() };
        src.Member("impl");

        AssertFixture("plan-empty", PlanOf(src));
    }

    [Fact]
    public void TheDocumentIsPlainTextOfTheViewEnvelopeAndTheOrchestratorNodeAndNoOtherNodeServesIt()
    {
        var rig = Full();
        var paths = rig.Src.Paths();

        var doc = rig.Src.Build("/plan" + paths.Orch);

        Assert.Equal("plan", M(doc, "kind").AsString());
        Assert.Equal(paths.Orch, M(M(doc, "body"), "node").Member("path")!.Value.AsString());
        Assert.Equal("not_found", M(rig.Src.Build("/plan" + paths.Team), "error").AsString());
        Assert.Equal("not_found", M(rig.Src.Build("/plan" + paths.Member), "error").AsString());
        Assert.Equal("not_found", M(rig.Src.Build("/plan/o/nope"), "error").AsString());
    }

    [Fact]
    public void P10AStreamWithoutAPlanFileHasNoItemsAndCountsFromTheChecklistReader()
    {
        var rig = Full();

        var docs = M(M(Warmed(rig), "streams").AsArray()[1], "planned");
        var stream = M(Warmed(rig), "streams").AsArray()[1];

        Assert.False(docs.AsBool());
        Assert.Equal("docs-241", M(stream, "stream").AsString());
        Assert.Equal("[]", Compact(M(stream, "items")));
        Assert.Equal("{\"done\":1,\"in_progress\":1,\"not_started\":0,\"dropped\":0}", Compact(M(stream, "counts")));
        Assert.Equal(0, (int)M(stream, "more").AsNumber());
    }

    [Fact]
    public void NegativeAStreamWithAPlanFileIsPlannedAndItsCountsComeFromThePlan()
    {
        var rig = Full();

        var stream = M(Warmed(rig), "streams").AsArray()[0];

        Assert.True(M(stream, "planned").AsBool());
        Assert.Equal("{\"done\":2,\"in_progress\":1,\"not_started\":1,\"dropped\":1}", Compact(M(stream, "counts")));
        Assert.Equal(5, M(stream, "items").AsArray().Count);
    }

    [Fact]
    public void P11ATeamWithProgressOffServesNoStreamsAndSaysProgressIsOff()
    {
        var src = new ModelSource { TeamName = "ah2-tm3" };
        var progress = new FakeProgress();
        progress.Teams["ah2-tm3"] = new TeamProgress(true, true, [Stream("s", 1, 0, 0, "2026-10-17T02:00:00Z", false, false, "t")], []);
        src.ProgressReader = progress;
        src.Member("impl");

        var body = PlanOf(src);

        Assert.False(M(M(body, "source"), "present").AsBool());
        Assert.Equal("progress is off", M(M(body, "source"), "reason").AsString());
        Assert.Equal("[]", Compact(M(body, "streams")));
    }

    [Fact]
    public void NegativeATeamWithRecordsIsPresentWithNoReasonAndOneWithNoFolderSaysNoProgressRecords()
    {
        var rig = Full();
        var src = new ModelSource { ProgressReader = new FakeProgress() };
        src.Member("impl");

        var present = M(PlanOf(rig.Src), "source");
        var absent = M(PlanOf(src), "source");

        Assert.True(M(present, "present").AsBool());
        Assert.True(M(present, "reason").IsNull);
        Assert.Equal("no progress records", M(absent, "reason").AsString());
    }

    [Fact]
    public void ASourceWithNoProgressReaderServesNoRecords()
    {
        var src = new ModelSource();
        src.Member("impl");

        var source = M(PlanOf(src), "source");

        Assert.False(M(source, "present").AsBool());
        Assert.Equal("no progress records", M(source, "reason").AsString());
    }

    [Fact]
    public void P13TheServedStaleIsTheProgressReadersAndNeverRecomputed()
    {
        var progress = new FakeProgress();
        progress.Teams["ah2-tm3"] = new TeamProgress(
            false,
            true,
            [
                new StreamProgress("a", true, true, 0, 0, 0, "open", DateTimeOffset.Parse("2026-10-17T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-10-17T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture), true, false, [], false, null),
                new StreamProgress("b", true, true, 0, 0, 0, "open", DateTimeOffset.Parse("2026-10-17T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-10-17T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture), false, false, [], false, null),
            ],
            []);
        var src = new ModelSource { TeamName = "ah2-tm3", ProgressReader = progress };
        src.Member("impl");

        var streams = M(PlanOf(src), "streams").AsArray();

        Assert.Equal([true, false], streams.Select(s => M(s, "stale").AsBool()));
        Assert.Equal("2026-10-17T02:00:00Z", M(streams[0], "updated_at").AsString());
    }

    [Fact]
    public void ItemsAreServedInProgressThenNotStartedThenDoneNewestFirstThenDropped()
    {
        var rig = Full();

        var ids = M(M(Warmed(rig), "streams").AsArray()[0], "items").AsArray().Select(i => M(i, "item").AsString());

        Assert.Equal(["live-trial", "push-branches", "rev8-registered-state", "stage-1", "void-stub"], ids);
    }

    private static Rig Many(int done, int inProgress, int notStarted)
    {
        var rig = Full();
        var items = new List<PlanItem>();
        for (var i = 0; i < done; i++)
        {
            items.Add(Item($"d{i:D3}", $"Done {i}", PlanDoneState, "2026-10-01T00:00:00Z", done: $"2026-10-02T00:{i / 60:D2}:{i % 60:D2}Z"));
        }

        for (var i = 0; i < inProgress; i++)
        {
            items.Add(Item($"p{i:D3}", $"Open {i}", "in_progress", "2026-10-01T00:00:00Z", $"2026-10-03T00:00:{i % 60:D2}Z"));
        }

        for (var i = 0; i < notStarted; i++)
        {
            items.Add(Item($"n{i:D3}", $"Next {i}", "not_started", $"2026-10-04T{i / 3600:D2}:{i / 60 % 60:D2}:{i % 60:D2}Z"));
        }

        rig.Progress.Plans[("ah2-tm3", "suborch-p1")] = new PlanState(items, 2);
        return rig;
    }

    private static JsonValue First(JsonValue body) => M(body, "streams").AsArray()[0];

    [Fact]
    public void DoneItemsBeyondTheNewestFiftyAreOnlyCountedUnlessAllIsAsked()
    {
        var rig = Many(done: 60, inProgress: 3, notStarted: 0);

        var capped = First(Warmed(rig));
        var all = First(Warmed(rig, "?all=1"));

        var items = M(capped, "items").AsArray();
        Assert.Equal(53, items.Count);
        Assert.Equal(10, (int)M(capped, "more").AsNumber());
        Assert.Equal(60, (int)M(M(capped, "counts"), "done").AsNumber());
        Assert.Equal("d059", M(items[3], "item").AsString());
        Assert.Equal("d010", M(items[^1], "item").AsString());
        Assert.Equal(63, M(all, "items").AsArray().Count);
        Assert.Equal(0, (int)M(all, "more").AsNumber());
    }

    [Fact]
    public void NegativeExactlyFiftyDoneItemsLeaveNoMoreAndTwoUnknownEventsAreServed()
    {
        var rig = Many(done: 50, inProgress: 0, notStarted: 0);

        var body = Warmed(rig);

        Assert.Equal(0, (int)M(First(body), "more").AsNumber());
        Assert.Equal(50, M(First(body), "items").AsArray().Count);
        Assert.Equal(2, (int)M(body, "unknown_events").AsNumber());
    }

    [Fact]
    public void ATwoHundredItemCapKeepsInProgressFirstAndCountsTheRest()
    {
        var rig = Many(done: 0, inProgress: 2, notStarted: 200);

        var stream = First(Warmed(rig));

        var items = M(stream, "items").AsArray();
        Assert.Equal(200, items.Count);
        Assert.Equal(2, (int)M(stream, "more").AsNumber());
        Assert.StartsWith("p", M(items[0], "item").AsString(), StringComparison.Ordinal);
        Assert.Equal(2, items.Count(i => M(i, "state").AsString() == "in_progress"));
    }

    [Fact]
    public void NegativeExactlyTwoHundredItemsLeaveNoMore()
    {
        var rig = Many(done: 0, inProgress: 0, notStarted: 200);

        Assert.Equal(0, (int)M(First(Warmed(rig)), "more").AsNumber());
    }

    [Fact]
    public void WithoutAGitRunnerAnItemWithCommitsSaysGitUnavailableAndOneWithoutSaysNoCommits()
    {
        var rig = Full();
        rig.Src.GitReader = null;

        var body = PlanOf(rig.Src);
        var items = M(First(body), "items").AsArray();

        Assert.Equal("[]", Compact(M(body, "releases")));
        Assert.Equal("no commits", M(items[0], "release_state").AsString());
        Assert.Equal("git unavailable", M(items[2], "release_state").AsString());
        Assert.Equal("git unavailable", M(items[3], "release_state").AsString());
        Assert.Equal("[]", Compact(M(items[2], "branches")));
        Assert.True(M(items[2], "pushed").IsNull);
    }

    [Fact]
    public void P14TheDocumentAsksGitOnlyTheReadOnlyListingsAndContainmentQuestions()
    {
        var rig = Full();

        _ = Warmed(rig);

        Assert.NotEmpty(rig.Repo.Argvs);
        Assert.All(rig.Repo.Argvs, a =>
        {
            Assert.Equal("git", a[0]);
            Assert.True(a[1] is "tag" or "branch", string.Join(' ', a));
            Assert.DoesNotContain(a, static w => w is "fetch" or "pull" or "push" or "checkout" or "commit");
        });
    }

    [Fact]
    public void AnIssueGetsItsPageFromAGitHubOriginAndNoUrlFromAnyOther()
    {
        var rig = Full();
        var github = M(M(Warmed(rig), "streams").AsArray()[0], "items").AsArray()[0];
        rig.Src.SmallTexts["/m/repo/.git/config"] = "[remote \"origin\"]\n\turl = /srv/git/x.git\n";

        var elsewhere = M(M(Warmed(rig), "streams").AsArray()[0], "items").AsArray()[0];

        Assert.Equal("https://github.com/example/agent-tools/issues/77", M(M(github, "issue"), "url").AsString());
        Assert.Equal(77, (int)M(M(elsewhere, "issue"), "number").AsNumber());
        Assert.True(M(M(elsewhere, "issue"), "url").IsNull);
    }

    [Fact]
    public void ARepoWithNoRemoteServesPushedNullAndAMissingConfigDoesToo()
    {
        var rig = Full();
        rig.Src.SmallTexts["/m/repo/.git/config"] = "[core]\n\tbare = false\n";

        var items = M(First(Warmed(rig)), "items").AsArray();

        Assert.True(M(items[2], "pushed").IsNull);
        Assert.True(M(items[3], "pushed").IsNull);
    }

    private static PlanItem Held(string slug, string state, string? role, string? since) =>
        new(slug, "Title " + slug, state, "2026-10-17T02:00:00Z", null, null, [], null, null, null, Role: role, StateSince: since);

    private static JsonValue ItemOf(Rig rig, string slug) =>
        M(Warmed(rig), "streams").AsArray()[0].Member("items")!.Value.AsArray().Single(i => M(i, "item").AsString() == slug);

    [Fact]
    public void TAnAPlanItemCarriesItsRoleAndNoRoleKeyWhenTheLaneHasNone()
    {
        var rig = Full();
        rig.Progress.Plans[("ah2-tm3", "suborch-p1")] = new PlanState([Held("a", "in_progress", "reviewer", null), Held("b", "in_progress", null, null)], 0);

        Assert.Equal("reviewer", M(ItemOf(rig, "a"), "role").AsString());
        Assert.False(ItemOf(rig, "b").AsObject().ContainsKey("role"));
    }

    [Fact]
    public void TAWaitingStatesAreServedVerbatimWithTheirSinceAndAnItemWithoutSinceHasNone()
    {
        var rig = Full();
        rig.Progress.Plans[("ah2-tm3", "suborch-p1")] = new PlanState(
            [Held("u", "waiting_user", "implementor", "2026-10-17T03:00:00Z"), Held("o", "waiting_orch", "implementor", "2026-10-17T03:05:00Z"), Held("n", "waiting_orch", "implementor", null)],
            0);

        Assert.Equal("waiting_user", M(ItemOf(rig, "u"), "state").AsString());
        Assert.Equal("2026-10-17T03:00:00Z", M(ItemOf(rig, "u"), "state_since").AsString());
        Assert.Equal("waiting_orch", M(ItemOf(rig, "o"), "state").AsString());
        Assert.Equal("2026-10-17T03:05:00Z", M(ItemOf(rig, "o"), "state_since").AsString());
        Assert.False(ItemOf(rig, "n").AsObject().ContainsKey("state_since"));
    }

    private static JsonObject QueuedRow(string item, string role) =>
        new() { ["item"] = item, ["role"] = role, ["queued_since"] = "2026-10-17T03:00:00Z", ["stall_at"] = "2026-10-17T03:15:00Z", ["eligible_members"] = new JsonArray(), ["last_dispatch_at"] = JsonValue.Null };

    [Fact]
    public void TAThePlanQueueIsExactlyWhatQueueReadReturnedAndTheReaderIsAskedOncePerTeam()
    {
        var rig = Full();
        var rows = new JsonArray { QueuedRow("s1/i1", "implementor"), QueuedRow("s1/i2", "reviewer") };
        rig.Progress.Queues["ah2-tm3"] = rows;

        var queue = M(PlanOf(rig.Src), "queue");

        Assert.Equal(Compact(rows), Compact(queue));
        Assert.Single(rig.Progress.QueueCalls);
        Assert.Equal("ah2-tm3", rig.Progress.QueueCalls[0].Team);
    }

    [Fact]
    public void TheSameQueuedItemIdInTwoTeamsServesTwoRowsThatDifferByTeam()
    {
        var rig = Full();
        rig.Src.ExtraTeams.Add("second");
        rig.Progress.Queues["ah2-tm3"] = new JsonArray { QueuedRow("3", "implementor") };
        rig.Progress.Queues["second"] = new JsonArray { QueuedRow("3", "implementor") };

        var queue = M(PlanOf(rig.Src), "queue").AsArray();

        Assert.Equal(2, queue.Count);
        Assert.Equal(["ah2-tm3", "second"], queue.Select(static r => M(r, "team").AsString()).Order(StringComparer.Ordinal).ToArray());
        Assert.All(queue, static r => Assert.Equal("3", M(r, "item").AsString()));
    }

    [Fact]
    public void TAAnEmptyQueueIsAnEmptyListAndAReaderWithoutTheOpLeavesTheKeyOut()
    {
        var rig = Full();
        rig.Progress.Queues["ah2-tm3"] = [];

        Assert.Equal("[]", Compact(M(PlanOf(rig.Src), "queue")));

        rig.Progress.Queues.Clear();

        Assert.False(M(rig.Src.Build("/plan" + rig.Src.Paths().Orch), "body").AsObject().ContainsKey("queue"));
    }

    [Fact]
    public void TBThePlanViewSchemaStaysOne()
    {
        var rig = Full();

        Assert.Equal(1, M(rig.Src.Build("/plan" + rig.Src.Paths().Orch), "view_schema").AsNumber());
    }

    [Fact]
    public void TheOrchestratorMenuOffersPlanAsAViewActionNamingItsNodeAndNoOtherMenuDoes()
    {
        var src = new ModelSource();
        src.Member("impl");
        var paths = src.Paths();

        static List<JsonObject> Actions(ModelSource s, string path) =>
            [.. M(M(s.Build("/actions" + path), "body"), "actions").AsArray().Select(static a => a.AsObject())];
        var plan = Actions(src, paths.Orch).Single(static a => a.Str("id") == "node.plan");

        Assert.Equal(("Plan", "P", "view"), (plan.Str("label"), plan.Str("key"), plan.Str("kind")));
        Assert.Equal("switch.plan", plan.Obj("invoke")!.Str("view"));
        Assert.Equal(paths.Orch, plan.Obj("invoke")!.Str("target"));
        Assert.DoesNotContain(Actions(src, paths.Team), static a => a.Str("id") == "node.plan");
        Assert.DoesNotContain(Actions(src, paths.Member), static a => a.Str("id") == "node.plan");
    }
}
