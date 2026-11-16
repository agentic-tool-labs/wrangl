using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch.Tests;

/// <summary>The eight groups of the orchestrator detail document (spec 2.5, 2.6; tests D1 to D9, D14 to D18 and the fixture shapes).</summary>
public sealed class DetailGroupsTests
{
    private const double Now = ModelSource.Now;
    private const string Session = "sess-orch";
    private const string MainHier = "/m/repo/.claude/ah2";

    private sealed class FakeGit : IProcessRunner
    {
        internal List<ProcessRequest> Requests { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            var text = request.Argv.Contains("describe") ? "ah2-v2.4.1\n" : "# branch.oid 0123456789abcdef\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0\n";
            return new ProcessResult(0, null, false, false, Encoding.UTF8.GetBytes(text), default);
        }
    }

    private static void Settle(GitRunner runner) => Assert.True(runner.WhenIdle().Wait(TimeSpan.FromSeconds(10)), "the runner did not go idle");

    private static string Iso(double ms) => Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));

    private static ModelSource Source() => new() { TeamName = "ah2-tm3" };

    private static JsonObject Detail(ModelSource s)
    {
        var orch = s.Paths().Orch;
        return s.Build("/detail" + orch);
    }

    private static JsonObject Field(JsonObject doc, string key) =>
        doc["body"].AsObject()["fields"].AsArray().Select(static f => f.AsObject()).Single(f => f["key"].AsString() == key);

    private static JsonObject Sub(JsonObject group, string key) =>
        group["value"].AsArray().Select(static f => f.AsObject()).Single(f => f["key"].AsString() == key);

    private static JsonObject Vitals(JsonObject doc, string key) => Sub(Field(doc, "vitals"), key);

    private static string Text(JsonObject o, string key) => o[key].AsString();

    private static StatuslineTap Tap(string json, double ageMinutes) => new(_ => new TapFile(json, Now - (ageMinutes * 60_000)), () => Now);

    private const string TapInput =
        "{\"model\":{\"id\":\"claude-opus-4-1[1m]\",\"display_name\":\"Opus 4.1 (1M context)\"},\"workspace\":{\"current_dir\":\"/w/x\"},\"effort\":{\"level\":\"high\"},"
        + "\"context_window\":{\"context_window_size\":1000000,\"used_percentage\":41,\"current_usage\":{\"input_tokens\":400000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":10000}},"
        + "\"rate_limits\":{\"five_hour\":{\"used_percentage\":63.54,\"resets_at\":1800003600},\"seven_day\":{\"used_percentage\":22,\"resets_at\":1800500000}}}";

    [Fact]
    public void WithoutTheTapTheContextIsTheOneReadingOfTheSessionsModelWithItsTime()
    {
        var s = Source();
        s.Activity[Session] = new ActivityHistory(null, [], 300_000, "idle", "claude-opus-4", "2026-06-02T09:00:00Z");

        var value = Vitals(Detail(s), "context")["value"].AsObject();

        Assert.Equal(1_000_000, value["window"].AsNumber());
        Assert.Equal(ContextReading.Of("claude-opus-4", 300_000).Pct, (long)value["pct"].AsNumber());
        Assert.Equal("2026-06-02T09:00:00Z", Text(value, "as_of"));
    }

    [Fact]
    public void NegativeAReadingWhoseModelNamesNoWindowHasNoPercent()
    {
        var s = Source();
        s.Activity[Session] = new ActivityHistory(null, [], 82_000, "idle");

        var context = Vitals(Detail(s), "context");

        Assert.False(context["present"].AsBool());
        Assert.Equal("window unknown", Text(context, "reason"));
    }

    [Fact]
    public void D1WithoutTheTapTheContextIsPresentUnverifiedOnAnAssumedTwoHundredThousandWindow()
    {
        var s = Source();
        s.Activity[Session] = new ActivityHistory(null, [], 82_000, "idle", "claude-opus-4");

        var doc = Detail(s);
        var context = Vitals(doc, "context");

        Assert.True(context["present"].AsBool());
        Assert.False(context["verified"].AsBool());
        Assert.Equal("activity record; window from its model", Text(context, "source"));
        Assert.True(context["reason"].IsNull);
        Assert.Equal(41.0, context["value"].AsObject()["pct"].AsNumber());
        Assert.Equal(200_000, context["value"].AsObject()["window"].AsNumber());
        Assert.Equal(82_000, context["value"].AsObject()["used"].AsNumber());
        Assert.True(context["value"].AsObject()["as_of"].IsNull);
        Assert.False(context["value"].AsObject()["stale"].AsBool());
        Assert.False(Field(doc, "vitals")["verified"].AsBool());
    }

    [Fact]
    public void D1NegativeAFreshTapReplacesTheAssumedWindowWithTheTrueOne()
    {
        var s = Source();
        s.Activity[Session] = new ActivityHistory(null, [], 82_000, "idle", "claude-opus-4");
        s.TapReader = Tap(TapInput, 1);

        var context = Vitals(Detail(s), "context");

        Assert.Equal("statusline", Text(context, "source"));
        Assert.Equal(1_000_000, context["value"].AsObject()["window"].AsNumber());
    }

    [Fact]
    public void D2NoActivityRecordAndNoTapGiveAbsentContextActivityModelAndEffort()
    {
        var s = Source();

        var doc = Detail(s);

        Assert.Equal("no activity record", Text(Vitals(doc, "context"), "reason"));
        Assert.False(Vitals(doc, "context")["present"].AsBool());
        Assert.Equal("no activity record", Text(Field(doc, "activity"), "reason"));
        Assert.Equal("tap off", Text(Vitals(doc, "model"), "reason"));
        Assert.Equal("statusline", Text(Vitals(doc, "model"), "source"));
        Assert.Equal("tap off", Text(Vitals(doc, "effort"), "reason"));
    }

    [Fact]
    public void D2NegativeAStaleTapGivesStaleAsTheReasonAndATapWithoutAModelGivesNotInData()
    {
        var stale = Source();
        stale.TapReader = Tap(TapInput, 90);
        var bare = Source();
        bare.TapReader = Tap("{\"rate_limits\":{}}", 1);

        Assert.Equal("statusline data stale", Text(Vitals(Detail(stale), "model"), "reason"));
        Assert.Equal("not in statusline data", Text(Vitals(Detail(bare), "model"), "reason"));
    }

    [Fact]
    public void D2NegativeARecordWithoutAContextReadingIsNotNoActivityRecord()
    {
        var s = Source();
        s.Activity[Session] = new ActivityHistory("2026-06-02T09:00:00.000Z", [], null, "idle");

        var context = Vitals(Detail(s), "context");

        Assert.Equal("no context reading", Text(context, "reason"));
    }

    [Fact]
    public void D3ALinkedWorkTreesBranchIsReadFromFilesWithNoGitProcess()
    {
        var s = Source();
        var fake = new FakeGit();
        s.GitReader = new GitRunner(fake, () => Now);
        s.SmallTexts["/m/repo/.claude/worktrees/rc3-tui/.git"] = "gitdir: /m/repo/.git/worktrees/rc3-tui\n";
        s.SmallTexts["/m/repo/.git/worktrees/rc3-tui/HEAD"] = "ref: refs/heads/ah/ui-detail\n";

        var branch = Vitals(Detail(s), "branch");

        Assert.True(branch["present"].AsBool());
        Assert.Equal("ah/ui-detail", branch["value"].AsObject()["name"].AsString());
        Assert.False(branch["value"].AsObject()["detached"].AsBool());
        Assert.Equal("git HEAD file", Text(branch, "source"));
        Assert.DoesNotContain(fake.Requests, static r => r.WorkingDirectory == "/m/repo/.claude/worktrees/rc3-tui");
    }

    [Fact]
    public void D4ADetachedHeadServesTheShaAndNoName()
    {
        var s = Source();
        s.SmallTexts["/m/repo/.claude/worktrees/rc3-tui/.git/HEAD"] = "0123456789abcdef0123456789abcdef01234567\n";

        var value = Vitals(Detail(s), "branch")["value"].AsObject();

        Assert.True(value["name"].IsNull);
        Assert.True(value["detached"].AsBool());
        Assert.Equal("0123456", value["sha"].AsString());
    }

    [Fact]
    public void D4NegativeNoGitFilesGiveNoGit()
    {
        var branch = Vitals(Detail(Source()), "branch");

        Assert.False(branch["present"].AsBool());
        Assert.Equal("no git", Text(branch, "reason"));
    }

    [Fact]
    public void D16AFreshTapServesRateLimitsTheTrueWindowAndTheTapsModelEffortAndCwd()
    {
        var s = Source();
        s.TapReader = Tap(TapInput, 2);
        s.RegisteredCwd = "/registered/cwd";

        var doc = Detail(s);
        var limits = Vitals(doc, "rate_limits");
        var context = Vitals(doc, "context");

        Assert.True(limits["verified"].AsBool());
        Assert.Equal("statusline", Text(limits, "source"));
        var windows = limits["value"].AsObject()["windows"].AsArray();
        Assert.Equal(["five_hour", "seven_day"], windows.Select(static w => w.AsObject()["name"].AsString()));
        Assert.Equal(63.5, windows[0].AsObject()["pct"].AsNumber());
        Assert.False(limits["value"].AsObject()["stale"].AsBool());
        Assert.True(context["verified"].AsBool());
        Assert.Equal("statusline", Text(context, "source"));
        Assert.Equal(1_000_000, context["value"].AsObject()["window"].AsNumber());
        Assert.Equal(410_000, context["value"].AsObject()["used"].AsNumber());
        Assert.Equal("claude-opus-4-1[1m]", Vitals(doc, "model")["value"].AsObject()["id"].AsString());
        Assert.Equal("statusline", Text(Vitals(doc, "model"), "source"));
        Assert.Equal("high", Vitals(doc, "effort")["value"].AsObject()["level"].AsString());
        Assert.Equal("/w/x", Vitals(doc, "cwd")["value"].AsString());
        Assert.True(Field(doc, "vitals")["verified"].AsBool() || !Vitals(doc, "branch")["verified"].AsBool());
    }

    [Fact]
    public void D17ThirtyMinutesIsServedStaleAndNinetyMinutesIsAbsent()
    {
        var thirty = Source();
        thirty.TapReader = Tap(TapInput, 30);
        var ninety = Source();
        ninety.TapReader = Tap(TapInput, 90);
        ninety.Activity[Session] = new ActivityHistory(null, [], 20_000, "idle", "claude-opus-4");

        var served = Vitals(Detail(thirty), "rate_limits");
        var gone = Vitals(Detail(ninety), "rate_limits");

        Assert.True(served["present"].AsBool());
        Assert.True(served["value"].AsObject()["stale"].AsBool());
        Assert.False(string.IsNullOrEmpty(Text(served["value"].AsObject(), "as_of")));
        Assert.False(gone["present"].AsBool());
        Assert.Equal("statusline data stale", Text(gone, "reason"));
        Assert.Equal("activity record; window from its model", Text(Vitals(Detail(ninety), "context"), "source"));
    }

    [Fact]
    public void D18NoTapFileAndAnUnparsableOneAreBothTapOffAndContextFallsBack()
    {
        var off = Source();
        var broken = Source();
        broken.TapReader = Tap("{broken", 1);
        broken.Activity[Session] = new ActivityHistory(null, [], 20_000, "idle", "claude-opus-4");

        Assert.Equal("tap off", Text(Vitals(Detail(off), "rate_limits"), "reason"));
        Assert.Equal("tap off", Text(Vitals(Detail(broken), "rate_limits"), "reason"));
        Assert.Equal("activity record; window from its model", Text(Vitals(Detail(broken), "context"), "source"));
    }

    [Fact]
    public void D18NegativeATapWithoutRateLimitsIsNotInStatuslineDataNotTapOff()
    {
        var s = Source();
        s.TapReader = Tap("{\"model\":{\"id\":\"m\"}}", 1);

        var limits = Vitals(Detail(s), "rate_limits");

        Assert.Equal("not in statusline data", Text(limits, "reason"));
    }

    private static JsonObject Row(string json) => JsonText.Parse(json).AsObject();

    [Fact]
    public void D6AMemberNamedInTheOpenOutagesHeldIsTheOnlyOneBadgedHeldAndHeldBeatsItsActivity()
    {
        var s = Source();
        foreach (var n in new[] { "m1", "m2", "m3", "m4", "m5" })
        {
            _ = s.Member(n, activity: n == "m3" ? "failed" : "working");
        }

        s.ExtraTeams.Add("t2");
        s.MemberTeam["m4"] = "t2";
        s.MemberTeam["m5"] = "t2";

        s.Peers.Add(Row("{\"type\":\"outage\",\"ts\":\"2026-06-02T10:00:00.000Z\",\"started_at\":\"2026-06-02T09:59:00.000Z\",\"status\":\"open\",\"probe_session\":\"sess-m3\",\"probes\":1,\"held\":[{\"session_id\":\"sess-m3\",\"failed_at\":\"2026-06-02T09:59:00.000Z\",\"role\":\"implementor\"}],\"dropped_probes\":[]}"));

        var doc = Detail(s);
        var members = Field(doc, "roster")["value"].AsObject()["members"].AsArray().Select(static m => m.AsObject()).ToList();

        Assert.Equal(5, members.Count);
        Assert.Equal(["ah2-tm3", "ah2-tm3", "ah2-tm3", "t2", "t2"], members.Select(static m => m["team"].AsString()));
        Assert.Equal(["working", "working", "held", "working", "working"], members.Select(static m => m["badge"].AsString()));
        Assert.Equal("sess-m3", Field(doc, "flight")["value"].AsObject()["held"].AsArray()[0].AsObject()["session_id"].AsString());
        Assert.Equal("m3", Field(doc, "flight")["value"].AsObject()["held"].AsArray()[0].AsObject()["name"].AsString());
        Assert.Equal("m3", Field(doc, "flight")["value"].AsObject()["outage"].AsObject()["probe"].AsString());
        var memberDetail = s.Build("/detail" + s.Paths("m3").Member)["body"].AsObject()["fields"].AsArray().Select(static f => f.AsObject()).Single(f => f["key"].AsString() == "badge");
        Assert.Equal("held", memberDetail["value"].AsString());
    }

    [Fact]
    public void D6NegativeAnOverOutageAndAnOutageOfStrangersHoldNobody()
    {
        var s = Source();
        _ = s.Member("m1");
        s.Peers.Add(Row("{\"type\":\"outage\",\"ts\":\"2026-06-02T10:00:00.000Z\",\"started_at\":\"2026-06-02T09:59:00.000Z\",\"status\":\"over\",\"ended_at\":\"2026-06-02T10:00:00.000Z\",\"held\":[{\"session_id\":\"sess-m1\",\"failed_at\":\"x\"}],\"dropped_probes\":[]}"));
        s.Peers.Add(Row("{\"type\":\"outage\",\"ts\":\"2026-06-02T10:05:00.000Z\",\"started_at\":\"2026-06-02T10:04:00.000Z\",\"status\":\"open\",\"held\":[{\"session_id\":\"sess-stranger\",\"failed_at\":\"x\"}],\"dropped_probes\":[]}"));

        var doc = Detail(s);
        var flight = Field(doc, "flight")["value"].AsObject();

        Assert.Equal("working", Field(doc, "roster")["value"].AsObject()["members"].AsArray()[0].AsObject()["badge"].AsString());
        Assert.Empty(flight["held"].AsArray());
        Assert.Equal("over", flight["outage"].AsObject()["status"].AsString());
    }

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static JsonObject Started(Ah.Core.Time.StartMatch verdict, string recorded)
    {
        var s = Source();
        s.PidStart = recorded;
        s.StartVerdict = verdict;
        return Field(Detail(s), "started");
    }

    [Fact]
    public void D19aACurrentRecordWithTheSameProcessIsReadAsUtcAndVerified()
    {
        var started = Started(Ah.Core.Time.StartMatch.Same, "Sat Oct 17 04:54:08 2026");

        Assert.Equal("2026-10-17T04:54:08Z", started["value"].AsString());
        Assert.True(started["verified"].AsBool());
        Assert.Equal("team file (pid_start)", started["source"].AsString());
        Assert.Equal(("2026-10-17T04:54:08Z", true), ViewBuilder.StartedFrom("Sat Oct 17 04:54:08 2026", Ah.Core.Time.StartMatch.Same, NewYork));
    }

    [Fact]
    public void D19bALegacyRecordIsReadInTheDaemonsLocalZoneAndVerified()
    {
        Assert.Equal(("2026-10-17T04:54:08Z", true), ViewBuilder.StartedFrom("Sat Oct 17 00:54:08 2026", Ah.Core.Time.StartMatch.Legacy, NewYork));
        Assert.Equal(("2026-01-17T05:54:08Z", true), ViewBuilder.StartedFrom("Sat Jan 17 00:54:08 2026", Ah.Core.Time.StartMatch.Legacy, NewYork));

        var served = Started(Ah.Core.Time.StartMatch.Legacy, "Sat Oct 17 00:54:08 2026");

        Assert.Equal(ViewBuilder.StartedIso("Sat Oct 17 00:54:08 2026", ViewBuilder.StartZone), served["value"].AsString());
        Assert.True(served["verified"].AsBool());
    }

    [Fact]
    public void D19cAGoneProcessIsReadAsUtcAndUnverified()
    {
        var started = Started(Ah.Core.Time.StartMatch.Unknown, "Sat Oct 17 04:54:08 2026");

        Assert.Equal("2026-10-17T04:54:08Z", started["value"].AsString());
        Assert.False(started["verified"].AsBool());
        Assert.True(started["present"].AsBool());
        Assert.Equal(("2026-10-17T04:54:08Z", false), ViewBuilder.StartedFrom("Sat Oct 17 04:54:08 2026", Ah.Core.Time.StartMatch.Unknown, NewYork));
    }

    [Fact]
    public void D19dAReusedPidIsReadAsUtcAndUnverifiedWhateverTheLocalZone()
    {
        var started = Started(Ah.Core.Time.StartMatch.Different, "Sat Oct 17 04:54:08 2026");

        Assert.Equal("2026-10-17T04:54:08Z", started["value"].AsString());
        Assert.False(started["verified"].AsBool());
        Assert.Equal(("2026-10-17T04:54:08Z", false), ViewBuilder.StartedFrom("Sat Oct 17 04:54:08 2026", Ah.Core.Time.StartMatch.Different, NewYork));
    }

    [Fact]
    public void ARosterRowCarriesTheNodePathOfItsMemberRightAfterItsName()
    {
        var s = new ModelSource { TeamName = "ah2-tm3" }.Member("impl", "implementor", "working").Member("rev", "reviewer", "idle");
        var paths = s.Paths();

        var rows = Field(Detail(s), "roster")["value"].AsObject()["members"].AsArray().ToList().Select(static r => r.AsObject()).ToList();

        Assert.Equal(["name", "path", "team"], rows[0].Keys.Take(3).ToList());
        Assert.Equal(paths.Member, rows.Single(r => r["name"].AsString() == "impl")["path"].AsString());
        Assert.Equal(paths.Team + "/m/rev", rows.Single(r => r["name"].AsString() == "rev")["path"].AsString());
        Assert.NotEqual(rows[0]["path"].AsString(), rows[1]["path"].AsString());
    }

    [Fact]
    public void NegativeOnlyALegacyMatchIsReadInTheLocalZone()
    {
        foreach (var match in new[] { Ah.Core.Time.StartMatch.Same, Ah.Core.Time.StartMatch.Different, Ah.Core.Time.StartMatch.Unknown })
        {
            Assert.Equal("2026-10-17T00:54:08Z", ViewBuilder.StartedFrom("Sat Oct 17 00:54:08 2026", match, NewYork).Iso);
        }
    }

    [Theory]
    [InlineData("not a start time")]
    [InlineData("2026-10-17T00:54:08Z")]
    [InlineData("Sat Oct 17 2026")]
    public void D20AnUnparseableStartIsNoStartAtAll(string recorded)
    {
        Assert.Null(ViewBuilder.StartedIso(recorded, TimeZoneInfo.Utc));
        var s = Source();
        s.PidStart = recorded;

        var started = Field(Detail(s), "started");

        Assert.False(started["present"].AsBool());
        Assert.Equal("start time unreadable", started["reason"].AsString());
    }

    [Fact]
    public void D6NegativeAnOutageThatIsOverIsTheLatestAndBadgesNobodyHeld()
    {
        var s = Source();
        _ = s.Member("m1");
        s.Peers.Add(Row("{\"type\":\"outage\",\"ts\":\"2026-06-02T10:00:00.000Z\",\"started_at\":\"2026-06-02T09:59:00.000Z\",\"status\":\"over\",\"ended_at\":\"2026-06-02T10:00:00.000Z\",\"held\":[{\"session_id\":\"sess-m1\",\"failed_at\":\"x\"}],\"dropped_probes\":[]}"));

        var doc = Detail(s);

        Assert.Equal("working", Field(doc, "roster")["value"].AsObject()["members"].AsArray()[0].AsObject()["badge"].AsString());
        Assert.Equal("working", s.Build("/detail" + s.Paths("m1").Member)["body"].AsObject()["fields"].AsArray().Select(static f => f.AsObject()).Single(static f => f["key"].AsString() == "badge")["value"].AsString());
    }

    private static JsonObject WatchEvent(string kind, string requestId, string session = Session) =>
        Row($"{{\"type\":\"watch-event\",\"session_id\":\"{session}\",\"request_id\":\"{requestId}\",\"kind\":\"{kind}\",\"text\":\"t\",\"ts\":\"2026-06-02T10:00:00.000Z\"}}");

    private static DispatchCandidate Candidate(string id, string to, double ageSeconds, bool open = true, string hdir = MainHier) =>
        new(id, "implementor", "slug-" + id, to + "@ah2-tm3", to, "small", Iso(Now - (ageSeconds * 1000)), open, "/r", false, string.Empty, hdir);

    [Fact]
    public void D7OpenRequestsAreOkCheckinOrSilentFromWhatWasEmittedAndNameTheirPool()
    {
        var s = Source();
        s.Candidates.Add(Candidate("a", "m1", 100));
        s.Candidates.Add(Candidate("b", "m2", 700, hdir: "/m/repo/.claude/worktrees/wt2/.claude/ah2"));
        s.Candidates.Add(Candidate("c", "m3", 1000));
        s.Candidates.Add(Candidate("d", "m4", 5000, open: false));
        s.Peers.Add(WatchEvent("CHECK-IN", "b"));
        s.Peers.Add(WatchEvent("CHECK-IN", "c"));
        s.Peers.Add(WatchEvent("SILENT", "c"));
        s.Peers.Add(WatchEvent("SILENT", "a", session: "sess-other"));
        s.Peers.Add(WatchEvent("SILENT", "z"));

        var flight = Field(Detail(s), "flight")["value"].AsObject();
        var requests = flight["requests"].AsArray().Select(static r => r.AsObject()).ToList();

        Assert.Equal(["a", "b", "c"], requests.Select(static r => r["id"].AsString()).Order());
        Assert.Equal("ok", requests.Single(static r => r["id"].AsString() == "a")["level"].AsString());
        Assert.Equal("checkin", requests.Single(static r => r["id"].AsString() == "b")["level"].AsString());
        Assert.Equal("silent", requests.Single(static r => r["id"].AsString() == "c")["level"].AsString());
        Assert.Equal("main", requests.Single(static r => r["id"].AsString() == "a")["pool"].AsString());
        Assert.Equal("wt2", requests.Single(static r => r["id"].AsString() == "b")["pool"].AsString());
        Assert.Equal("m1", requests.Single(static r => r["id"].AsString() == "a")["to"].AsString());
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public void D7NegativeAnAgePastEveryThresholdWithNoEmittedRowIsOkAndAnUnavailablePoolIsNull()
    {
        var old = Source();
        old.Candidates.Add(Candidate("a", "m1", 99_999));
        Assert.Equal("ok", Field(Detail(old), "flight")["value"].AsObject()["requests"].AsArray()[0].AsObject()["level"].AsString());

        var unreadable = Source();
        unreadable.PeersThrow = true;
        unreadable.Candidates.Add(Candidate("a", "m1", 100));
        Assert.Equal("ok", Field(Detail(unreadable), "flight")["value"].AsObject()["requests"].AsArray()[0].AsObject()["level"].AsString());

        var s = Source();
        s.UnavailableKeys.Add("pool");
        s.Candidates.Add(Candidate("a", "m1", 100, hdir: "/m/repo/.claude/worktrees/wt2/.claude/ah2"));
        var request = Field(Detail(s), "flight")["value"].AsObject()["requests"].AsArray()[0].AsObject();
        Assert.True(request["pool"].IsNull);
        Assert.Equal("ok", request["level"].AsString());
    }

    [Fact]
    public void AClassifierNoVerdictRowIsListedAsUnfinishedNotAsNeedingTheUserWhileARealDenialStillIs()
    {
        var s = Source();
        s.Peers.Add(Row("{\"type\":\"watch-event\",\"session_id\":\"sess-orch\",\"request_id\":null,\"kind\":\"CLASSIFIER-DENIED\",\"verdict\":\"no-verdict\",\"text\":\"nv\",\"ts\":\"2026-06-02T10:00:00.000Z\"}"));
        s.Peers.Add(Row("{\"type\":\"watch-event\",\"session_id\":\"sess-orch\",\"request_id\":null,\"kind\":\"CLASSIFIER-DENIED\",\"verdict\":\"denied\",\"text\":\"real\",\"ts\":\"2026-06-02T10:01:00.000Z\"}"));

        var items = Field(Detail(s), "feed")["value"].AsObject()["items"].AsArray().Select(static i => i.AsObject()).ToList();

        Assert.Equal(["step-needs-user", "step-unfinished"], items.Select(static i => i["kind"].AsString()));
    }

    [Fact]
    public void D8TheFeedHoldsOnlyThisOrchestratorsEventsNewestFirstAndCapsAtFifty()
    {
        var s = Source();
        _ = s.Member("m1");
        s.Candidates.Add(Candidate("req-1", "m1", 100));
        s.Peers.Add(Row("{\"type\":\"watch-event\",\"session_id\":\"sess-orch\",\"request_id\":\"req-1\",\"kind\":\"CHECK-IN\",\"text\":\"ah: check\",\"ts\":\"2026-06-02T10:00:00.000Z\"}"));
        s.Peers.Add(Row("{\"type\":\"watch-event\",\"session_id\":\"sess-other\",\"request_id\":\"req-9\",\"kind\":\"CHECK-IN\",\"text\":\"not mine\",\"ts\":\"2026-06-02T10:01:00.000Z\"}"));
        s.Peers.Add(Row("{\"type\":\"watch-event\",\"session_id\":\"sess-orch\",\"request_id\":null,\"kind\":\"OUTAGE\",\"text\":\"skipped\",\"ts\":\"2026-06-02T10:02:00.000Z\"}"));
        s.Peers.Add(Row("{\"type\":\"watch-event\",\"session_id\":\"sess-orch\",\"request_id\":null,\"kind\":\"CONTEXT-HIGH\",\"text\":\"ctx\",\"ts\":\"2026-06-02T10:03:00.000Z\"}"));
        s.Peers.Add(Row("{\"type\":\"dispatch\",\"session_id\":\"sess-orch\",\"request_id\":\"req-1\",\"to_addr\":\"m1@ah2-tm3\",\"created\":\"2026-06-02T09:00:00.000Z\",\"path\":\"/x/req-1--m1--work--request.md\"}"));
        s.Peers.Add(Row("{\"type\":\"dispatch\",\"session_id\":\"sess-other\",\"request_id\":\"req-8\",\"to_addr\":\"z@t\",\"created\":\"2026-06-02T11:00:00.000Z\"}"));

        var feed = Field(Detail(s), "feed")["value"].AsObject();
        var items = feed["items"].AsArray().Select(static i => i.AsObject()).ToList();

        Assert.Equal(["context", "check-in", "dispatch"], items.Select(static i => i["kind"].AsString()));
        Assert.Equal("m1", items[1]["subject"].AsString());
        Assert.Equal("m1", items[2]["subject"].AsString());
        Assert.Equal("work", items[2]["text"].AsString());
        Assert.DoesNotContain(items, static i => i["text"].AsString() is "not mine" or "skipped");
        Assert.Equal(0, feed["more"].AsNumber());

        for (var i = 0; i < 60; i++)
        {
            s.Peers.Add(Row($"{{\"type\":\"watch-event\",\"session_id\":\"sess-orch\",\"request_id\":null,\"kind\":\"BLOCKED\",\"text\":\"b{i}\",\"ts\":\"2026-06-03T10:{i:00}:00.000Z\"}}"));
        }

        var capped = Field(Detail(s), "feed")["value"].AsObject();
        Assert.Equal(50, capped["items"].AsArray().Count);
        Assert.Equal(13, capped["more"].AsNumber());
        Assert.Equal("b59", capped["items"].AsArray()[0].AsObject()["text"].AsString());
    }

    [Fact]
    public void D8NegativeUnreadablePeerRecordsAbsentTheFeedAndALongTextIsCapped()
    {
        var broken = Source();
        broken.PeersThrow = true;
        var feed = Field(Detail(broken), "feed");
        Assert.False(feed["present"].AsBool());
        Assert.Equal("peer records unreadable", Text(feed, "reason"));

        var s = Source();
        s.Peers.Add(Row("{\"type\":\"watch-event\",\"session_id\":\"sess-orch\",\"request_id\":null,\"kind\":\"BLOCKED\",\"text\":\"" + new string('x', 400) + "\",\"ts\":\"2026-06-02T10:00:00.000Z\"}"));
        var text = Field(Detail(s), "feed")["value"].AsObject()["items"].AsArray()[0].AsObject()["text"].AsString();
        Assert.Equal(300, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void D8OutageRowsMakeStartOverAndDroppedProbeItemsOncePerOutage()
    {
        var s = Source();
        _ = s.Member("m1");
        s.Peers.Add(Row("{\"type\":\"outage\",\"ts\":\"2026-06-02T10:00:00.000Z\",\"started_at\":\"2026-06-02T09:59:00.000Z\",\"status\":\"open\",\"held\":[{\"session_id\":\"sess-m1\",\"failed_at\":\"x\"}],\"dropped_probes\":[]}"));
        s.Peers.Add(Row("{\"type\":\"outage\",\"ts\":\"2026-06-02T10:30:00.000Z\",\"started_at\":\"2026-06-02T09:59:00.000Z\",\"status\":\"over\",\"ended_at\":\"2026-06-02T10:29:00.000Z\",\"held\":[{\"session_id\":\"sess-m1\",\"failed_at\":\"x\"}],\"dropped_probes\":[{\"session_id\":\"sess-m1\",\"reason\":\"gone\"}]}"));

        var items = Field(Detail(s), "feed")["value"].AsObject()["items"].AsArray().Select(static i => i.AsObject()).ToList();

        Assert.Equal(["probe-dropped", "outage-over", "outage-start"], items.Select(static i => i["kind"].AsString()));
        Assert.Equal("connection outage: 1 sessions held (m1)", items[2]["text"].AsString());
        Assert.Equal("outage over after 30m", items[1]["text"].AsString());
        Assert.Equal("probe m1 dropped: gone", items[0]["text"].AsString());
    }

    [Fact]
    public void D9ThreeDecisionRowsGiveThreeTwoDecisionsAndOneQuestion()
    {
        var s = Source();
        s.Run = Row(
            "{\"run_id\":\"run-1\",\"started_at\":\"2026-06-02T09:00:00.000Z\",\"rows\":["
            + "{\"id\":\"d1\",\"time\":\"2026-06-02T09:10:00.000Z\",\"kind\":\"decided\",\"item\":\"AC-1\",\"choice\":\"A\",\"decision\":\"keep it\",\"decider\":\"orchestrator\",\"rationale\":\"matches\",\"dangerous\":false,\"qkey\":\"AC-1|q1\"},"
            + "{\"id\":\"d2\",\"time\":\"2026-06-02T09:20:00.000Z\",\"kind\":\"decided\",\"item\":\"AC-2\",\"choice\":\"B\",\"decision\":\"drop it\",\"decider\":\"orchestrator\",\"rationale\":null,\"dangerous\":false,\"qkey\":\"AC-2|q2\"},"
            + "{\"id\":\"d3\",\"time\":\"2026-06-02T09:30:00.000Z\",\"kind\":\"parked\",\"item\":\"AC-7\",\"question\":\"Delete the branch?\",\"options\":[\"delete\",\"keep\"],\"why_user\":\"dangerous\",\"dangerous\":true,\"qkey\":\"AC-7|q7\"}]}");

        var doc = Detail(s);
        var run = Sub(Field(doc, "pipeline"), "run")["value"].AsObject();
        var decisions = Field(doc, "decisions")["value"].AsObject();

        Assert.Equal(3, run["decisions"].AsNumber());
        Assert.Equal("2026-06-02T09:30:00.000Z", run["last_decision_at"].AsString());
        Assert.Equal(2, decisions["decisions"].AsArray().Count);
        Assert.Equal("drop it", decisions["decisions"].AsArray()[0].AsObject()["choice"].AsString());
        Assert.Single(decisions["questions"].AsArray());
        Assert.Equal(["delete", "keep"], decisions["questions"].AsArray()[0].AsObject()["options"].AsArray().Select(static o => o.AsString()));
        Assert.True(decisions["questions"].AsArray()[0].AsObject()["dangerous"].AsBool());
    }

    [Fact]
    public void D9NegativeAUserDecidedRowAndAnAnsweredQuestionAreNeitherDecisionNorQuestion()
    {
        var s = Source();
        s.Run = Row(
            "{\"run_id\":\"run-1\",\"started_at\":null,\"rows\":["
            + "{\"id\":\"d1\",\"time\":\"2026-06-02T09:10:00.000Z\",\"kind\":\"parked\",\"item\":\"AC-7\",\"question\":\"q\",\"options\":[\"a\"],\"why_user\":\"unsure\",\"dangerous\":false,\"qkey\":\"k7\"},"
            + "{\"id\":\"d2\",\"time\":\"2026-06-02T09:20:00.000Z\",\"kind\":\"decided\",\"item\":\"AC-7\",\"choice\":\"A\",\"decision\":\"a\",\"decider\":\"user\",\"dangerous\":false,\"qkey\":\"k7\"}]}");

        var decisions = Field(Detail(s), "decisions")["value"].AsObject();

        Assert.Empty(decisions["decisions"].AsArray());
        Assert.Empty(decisions["questions"].AsArray());
        Assert.True(Sub(Field(Detail(s), "pipeline"), "run")["value"].AsObject()["started_at"].IsNull);
    }

    private static Ah.Hierarchy.ParkedQuestion Parked(string id, string? answeredAt = null) =>
        new(id, "2026-06-02T09:40:00.000Z", "tm", "sess-1", "toolu_1", 0, JsonText.Parse("""{"question":"Who decides?","header":"Authority","options":[{"label":"me"},{"label":"you"}]}"""), "ah:R8", "reserved", answeredAt: answeredAt);

    [Fact]
    public void D9AnOpenParkedQuestionIsAQuestionWaitingForYouEvenWithNoPipelineRun()
    {
        var s = Source();
        s.Parked.Add(Parked("q1"));
        s.Parked.Add(Parked("q2", answeredAt: "2026-06-02T09:50:00.000Z"));

        var decisions = Field(Detail(s), "decisions")["value"].AsObject();

        var question = Assert.Single(decisions["questions"].AsArray()).AsObject();
        Assert.Equal("q1", question["item"].AsString());
        Assert.Equal("Authority: Who decides?", question["question"].AsString());
        Assert.Equal(["me", "you"], question["options"].AsArray().Select(static o => o.AsString()));
        Assert.Contains("ah msg decision answer q1", question["why_user"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void D9NoRunIsNoPipelineRunForBothGroups()
    {
        var doc = Detail(Source());

        Assert.Equal("no pipeline run", Text(Field(doc, "decisions"), "reason"));
        Assert.Equal("no pipeline run", Text(Sub(Field(doc, "pipeline"), "run"), "reason"));
    }

    [Fact]
    public void D14TheWorktreesAreTheEnumerationsTreesInOrderAndAMissingOneStartsNoGit()
    {
        var s = Source();
        var fake = new FakeGit();
        s.GitReader = new GitRunner(fake, () => Now);
        s.TreePaths.AddRange(["/m/repo", "/w/rc3-w3", "/w/gone"]);
        s.MissingDirs.Add("/w/gone");

        _ = Detail(s);
        Settle(s.GitReader);
        var doc = Detail(s);
        Settle(s.GitReader);
        var trees = Field(doc, "worktrees")["value"].AsObject()["trees"].AsArray().Select(static t => t.AsObject()).ToList();

        Assert.Equal(["/m/repo", "/w/rc3-w3", "/w/gone"], trees.Select(static t => t["path"].AsString()));
        Assert.Equal(["repo", "rc3-w3", "gone"], trees.Select(static t => t["tree"].AsString()));
        Assert.Equal("ok", trees[0]["state"].AsString());
        Assert.Equal("main", trees[0]["branch"].AsString());
        Assert.Equal("missing", trees[2]["state"].AsString());
        Assert.True(trees[2]["branch"].IsNull);
        Assert.True(trees[2]["dirty"].IsNull);
        Assert.DoesNotContain(fake.Requests, static r => r.WorkingDirectory == "/w/gone");
    }

    [Fact]
    public void D14NegativeSeventeenTreesCapAtSixteenWithMoreOneAndAnUnreadableEnumerationIsViewUnavailable()
    {
        var s = Source();
        s.GitReader = new GitRunner(new FakeGit(), () => Now);
        s.TreePaths.AddRange(Enumerable.Range(0, 17).Select(static i => "/t/" + i));
        var worktrees = Field(Detail(s), "worktrees")["value"].AsObject();
        Assert.Equal(16, worktrees["trees"].AsArray().Count);
        Assert.Equal(1, worktrees["more"].AsNumber());

        var broken = Source();
        broken.TreesThrow = true;
        var doc = Detail(broken);
        Assert.Equal("view unavailable", Text(Field(doc, "worktrees"), "reason"));
        Assert.Equal("view unavailable", Text(Sub(Field(doc, "pipeline"), "branches"), "reason"));
    }

    [Fact]
    public void BranchesAreDerivedFromTheTreesAPushedOneHasAnUpstreamAndNoCommitsAhead()
    {
        var s = Source();
        s.GitReader = new GitRunner(new FakeGit(), () => Now);
        s.TreePaths.Add("/m/repo");
        _ = Detail(s);
        Settle(s.GitReader);

        var doc = Detail(s);
        Settle(s.GitReader);
        var branches = Sub(Field(doc, "pipeline"), "branches")["value"].AsArray();

        Assert.Equal("pushed", branches[0].AsObject()["state"].AsString());
        Assert.Equal(0, branches[0].AsObject()["ahead"].AsNumber());
        Assert.Equal("ah2-v2.4.1", Sub(Field(doc, "pipeline"), "release_tag")["value"].AsObject()["name"].AsString());
    }

    [Fact]
    public void TheEightGroupsFollowTheSevenExistingFieldsInOrderWithTheSpecKeysAndLabels()
    {
        var doc = Detail(Source());

        var fields = doc["body"].AsObject()["fields"].AsArray().Select(static f => (f.AsObject()["key"].AsString(), f.AsObject()["label"].AsString())).ToList();

        Assert.Equal(
            [
                ("name", "name"), ("state", "state"), ("repo", "repo"), ("pid", "pid"), ("session", "session"), ("teams", "teams"), ("started", "started"),
                ("vitals", "Session vitals"), ("roster", "Team roster"), ("flight", "Work in flight"), ("feed", "Events"), ("activity", "Activity"),
                ("pipeline", "Pipeline / release"), ("decisions", "Issues & decisions"), ("worktrees", "Worktrees"),
            ],
            fields);
    }
}
