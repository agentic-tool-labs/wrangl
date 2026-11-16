using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>team-summary counts the member rows of a team, ignores activity keys no member owns, carries the team's alerts, and publishes only when a state-bearing value changes.</summary>
public sealed partial class TreeLanesTests
{
    private sealed class PidTable(List<long> running) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) =>
            !running.Contains(pid) ? null : PidStartOverrides.TryGetValue(pid, out var start) ? start : "ps-" + pid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Start texts the fixture's process table prints in place of the default <c>ps-&lt;pid&gt;</c>.</summary>
    private static readonly Dictionary<long, string> PidStartOverrides = [];

    /// <summary>Composes through the real summary composer and counts how often each slice is composed.</summary>
    private sealed class CountingSummary(ISliceComposer inner) : ISliceComposer
    {
        public Dictionary<string, int> Composed { get; } = [];

        public string SlicePrefix => inner.SlicePrefix;

        public IReadOnlyList<string> Lanes => inner.Lanes;

        public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) => inner.Affected(changed, previous, current, records);

        public IReadOnlyList<string> AllIds(IRecordView records) => inner.AllIds(records);

        public SliceChange Compose(string sliceId, IRecordView records)
        {
            Composed[sliceId] = Composed.GetValueOrDefault(sliceId) + 1;
            return inner.Compose(sliceId, records);
        }
    }

    private static (ReadModel Model, Clock Clock) MakeWithActivity(TreeReader reader, ISliceComposer? summary = null, IModelLane? plan = null, Func<string, string, bool>? teamHeld = null, long? wallClockMs = null, Func<string, string?>? branchOf = null)
    {
        var clock = new Clock();
        string[] canonical = [Canon];
        var activity = new TimelineRecorder(new ActivityLane(reader, () => canonical), null, static _ => { }, null, new WorkingShare());
        var processes = new ProcessesLane(reader, () => canonical, new PidTable(reader.LivePids), static () => FixtureNowMs - LaneBehindByMs, static dir => FactsOfDir?.Invoke(dir) ?? new MemberFacts([], new Dictionary<string, string>(), []));
        var lanes = TreeModel.Lanes(reader, () => canonical, Home, null, branchOf)
            .Append(new MessagesLane(reader, () => canonical))
            .Append(new StatusLane(reader, () => canonical))
            .Append(activity)
            .Append(processes)
            .Append(new PeersLane(reader, () => canonical))
            .ToList();
        if (plan is not null)
        {
            lanes.Add(plan);
        }

        LastProcesses = processes;
        var composers = summary is null
            ? TreeModel.Composers(static () => FixtureNowMs, null, () => processes.ObservedAtMs, teamHeld: teamHeld)
            : new ComposerRegistry().Register(summary);
        var model = new ReadModel(lanes, new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, home: Home), composers)
        {
            WallClockMs = () => wallClockMs ?? FixtureNowMs + ServeLaterMs,
            ProcessesObservedAtMs = () => processes.ObservedAtMs,
        };
        return (model, clock);
    }

    private static (ReadModel Model, Clock Clock) SeededWithActivity(TreeReader reader, ISliceComposer? summary = null, IModelLane? plan = null, Func<string, string, bool>? teamHeld = null, long? wallClockMs = null, Func<string, string?>? branchOf = null)
    {
        var (model, clock) = MakeWithActivity(reader, summary, plan, teamHeld, wallClockMs, branchOf);
        foreach (var lane in new[] { "teams", "worktrees", "decisions", "registrations", "known-dirs", "messages", "status", "activity" })
        {
            model.EmitKeySet(lane);
        }

        model.Emit("processes", Canon);
        model.Emit("peers", Canon);
        Drain(model, clock);
        return (model, clock);
    }

    private static string Act(string activity, string turnAt = "2026-06-01T11:59:00Z", string toolAt = "2026-06-01T12:00:00Z", string ctxAt = "2026-06-01T12:00:00Z") =>
        "{\"activity\":\"" + activity + "\",\"at\":\"" + toolAt + "\",\"tool\":\"Bash\",\"tool_at\":\"" + toolAt + "\",\"turn_at\":\"" + turnAt + "\",\"context\":{\"used\":1,\"window_source\":\"unknown\",\"at\":\"" + ctxAt + "\"}}";

    private static string SummaryId => $"team-summary:{D}/default";

    private static JsonValue Summary(ReadModel model) => JsonText.Parse(Body(model, SummaryId));

    private static long Num(JsonValue body, string name) => (long)body.Member(name)!.Value.AsNumber();

    private static List<JsonValue> Alerts(ReadModel model) => [.. Summary(model).Member("alerts")!.Value.AsArray()];

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Text(JsonValue body, string name) => body.Member(name)!.Value.AsString();

    [Theory]
    [InlineData("working", true, 2, 0, false, "live")]
    [InlineData("ended", false, 0, 0, true, "ended")]
    [InlineData("ended", false, 0, 1, true, "stale")]
    [InlineData("needs_you", false, 0, 0, true, "ended")]
    [InlineData("needs_you", true, 1, 0, false, "live")]
    public void ASummaryServesWhetherItEndedAndItsStatus(string state, bool running, long members, long streams, bool ended, string status)
    {
        var body = new JsonObject { ["state"] = state, ["running"] = running, ["members"] = members, ["streams_open"] = streams, ["member_states"] = JsonValue.FromObject(new JsonObject { ["m"] = 1 }) };
        SummaryData.ApplyStatus(body);
        Assert.Equal(ended, body.Get("ended")!.Value.AsBool());
        Assert.Equal(status, body.Get("status")!.Value.AsString());
    }

    [Theory]
    [InlineData(1, 0, 0, false, null)] // something waits on you
    [InlineData(1, 0, 0, true, null)] // held, something still waits on you: visible, reads Held
    [InlineData(0, 1, 0, false, null)] // an item is being worked
    [InlineData(0, 1, 0, true, null)] // held does not hide a working item
    [InlineData(0, 0, 1, false, null)] // a member working, not held
    [InlineData(0, 0, 1, true, "updated_at")] // held: never kept on show by its members alone
    [InlineData(0, 0, 0, true, "updated_at")] // held, idle: quiet at once
    [InlineData(0, 0, 0, false, "stale_at")] // a live idle team goes quiet at the stale line
    public void QuietAtIsTheOneInstantClientsCompareAndNegativeNullWhileWorkOrAWaitIsOpen(long waiting, long workingItems, long workingMembers, bool held, string? from)
    {
        var body = new JsonObject
        {
            ["state"] = "idle", ["running"] = true, ["members"] = 1, ["streams_open"] = 0, ["member_states"] = JsonValue.FromObject(new JsonObject()),
            ["waiting_on_you"] = waiting, ["in_progress_now"] = workingItems, ["working"] = workingMembers, ["held"] = held,
            ["updated_at"] = "2026-11-01T00:00:00.000Z", ["stale_at"] = "2026-11-02T00:00:00.000Z",
        };
        SummaryData.ApplyStatus(body);
        var quiet = body.Get("quiet_at");
        if (from is null)
        {
            Assert.True(quiet is null || quiet.Value.IsNull);
        }
        else
        {
            Assert.Equal(body.Get(from)!.Value.AsString(), quiet!.Value.AsString());
        }
    }

    [Theory]
    [InlineData("ended", false, 1, 0, "updated_at")] // has members, none runs, nothing waits: quiet at once
    [InlineData("idle", false, 0, 0, "updated_at")] // nobody in it, nothing waits: quiet at once
    [InlineData("needs_you", false, 1, 1, null)] // stopped with something waiting: stays on show
    [InlineData("idle", false, 0, 1, null)] // nobody in it, something waiting: stays on show
    [InlineData("idle", true, 1, 0, "stale_at")] // a live idle member still waits for stale_at
    public void QuietAtIsAtOnceForATeamWithNoMemberLeftAndNegativeNotWhileSomethingWaitsOrAMemberRuns(string state, bool running, long members, long waiting, string? from)
    {
        var states = new JsonObject { ["a"] = running ? "idle" : "gone" };
        var body = new JsonObject
        {
            ["state"] = state, ["running"] = running, ["members"] = members, ["streams_open"] = 0, ["member_states"] = JsonValue.FromObject(states),
            ["waiting_on_you"] = waiting, ["in_progress_now"] = 0, ["working"] = 0, ["held"] = false,
            ["updated_at"] = "2026-11-01T00:00:00.000Z", ["stale_at"] = "2026-11-02T00:00:00.000Z",
        };
        SummaryData.ApplyStatus(body);
        var quiet = body.Get("quiet_at");
        if (from is null)
        {
            Assert.True(quiet is null || quiet.Value.IsNull);
        }
        else
        {
            Assert.Equal(body.Get(from)!.Value.AsString(), quiet!.Value.AsString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSummaryServesWhetherItsTeamIsHeldAndNegativeNotOtherwise(bool held)
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        var (model, _) = SeededWithActivity(reader, teamHeld: (_, _) => held);
        using var owner = model;

        Assert.Equal(held, Summary(model).Member("held")!.Value.AsBool());
    }

    [Fact]
    public void NegativeATeamWithADecisionAndNoOneRunningIsNotQuiet()
    {
        var body = new JsonObject { ["state"] = "needs_you", ["running"] = false, ["members"] = 0, ["streams_open"] = 0, ["waiting_on_you"] = 1, ["member_states"] = JsonValue.FromObject(new JsonObject()), ["stale_at"] = "2026-11-02T00:00:00.000Z" };
        SummaryData.ApplyStatus(body);
        Assert.True(body.Get("quiet_at")!.Value.IsNull);
    }

    [Fact]
    public void ATeamWithADecisionAndNoMembersHasNotEnded()
    {
        var body = new JsonObject { ["state"] = "needs_you", ["running"] = false, ["members"] = 0, ["streams_open"] = 0, ["member_states"] = JsonValue.FromObject(new JsonObject()) };
        SummaryData.ApplyStatus(body);
        Assert.False(body.Get("ended")!.Value.AsBool());
    }

    [Theory]
    [InlineData("needs_you", null, "Waiting on you · 2", "Waiting on you · 2", "Waiting on you")]
    [InlineData("stuck", "member:d/t/dev", "No activity", "dev: task stuck", "No activity")]
    [InlineData("stuck", "", "No activity", "task stuck", "No activity")]
    [InlineData("error", "member:d/t/dev", "Last turn failed", "dev failed", "Last turn failed")]
    [InlineData("blocked", "member:d/t/dev", "Waiting on a permission", "dev is blocked", "Waiting on a permission")]
    public void AnAlertServesItsCard(string kind, string? member, string text, string title, string detail)
    {
        Assert.Equal((title, detail), SummaryData.AlertCard(kind, member, text));
    }

    [Theory]
    [InlineData("""{"state":"done","counts":{"blocked":1}}""", "done")]
    [InlineData("""{"state":"open","counts":{"blocked":1,"waiting_user":1}}""", "blocked")]
    [InlineData("""{"state":"open","counts":{"waiting_user":1},"review_open":true}""", "waiting")]
    [InlineData("""{"state":"open","counts":{"done":1},"review_open":true}""", "review")]
    [InlineData("""{"state":"open","counts":{"waiting_orch":1}}""", "in_progress")]
    [InlineData("""{"state":"open","counts":{"not_started":3}}""", "not_started")]
    [InlineData("""{"stream":"s"}""", "not_started")]
    public void AStreamServesItsStatus(string stream, string status)
    {
        Assert.Equal(status, PlanSummaryComposer.StatusOf(JsonText.Parse(stream)));
    }

    [Fact]
    public void ALiveTeamGoesStaleADayAfterItsNewestChange()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var body = Summary(model);
        Assert.Equal("live", Text(body, "status"));
        Assert.False(body.Member("ended")!.Value.AsBool());
        var updated = DateTimeOffset.Parse(Text(body, "updated_at"), CultureInfo.InvariantCulture);
        Assert.Equal(updated.AddHours(24), DateTimeOffset.Parse(Text(body, "stale_at"), CultureInfo.InvariantCulture));
    }

    private static string AlertText(JsonValue summary) =>
        Text(Assert.Single(summary.Member("alerts")!.Value.AsArray()), "text");

    [Fact]
    public void ALeadParkedOnAQuestionShowsTheQuestionInItsAlert()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["so"] = """{"activity":"blocked","blocked_by":"question","note":"Ship it?","at":"2026-06-01T12:00:00Z"}""";
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var body = Summary(model);

        Assert.Equal("Asking in the terminal: Ship it?", AlertText(body));
        Assert.Equal(1, body.Member("needs_you")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeABlockedMemberWithANoteStillReadsAsAPermission()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = """{"activity":"blocked","blocked_by":"permission","note":"rm -rf","at":"2026-06-01T12:00:00Z"}""";
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Equal("Waiting on a permission", AlertText(Summary(model)));
    }

    [Fact]
    public void ALeadsActivityIsFoundWhenItRunsOutsideItsTeamsCheckout()
    {
        const string Other = "/elsewhere/.claude/ah2";
        var team = new TeamEntry(Canon, TeamPath, "default", JsonText.Parse("{}"), 0);
        var mine = new LaneRecord(JsonText.Parse("""{"activity":"blocked"}"""), 5);
        var older = new LaneRecord(JsonText.Parse("""{"activity":"idle"}"""), 1);
        var stranger = new LaneRecord(JsonText.Parse("""{"activity":"working"}"""), 9);
        var records = new Records(new()
        {
            [("activity", ActivityLane.KeyOf(Other, "so"))] = mine,
            [("activity", ActivityLane.KeyOf("/older/.claude/ah2", "so"))] = older,
            [("activity", ActivityLane.KeyOf(Other, "s-other"))] = stranger,
        });

        Assert.Same(mine, TreeData.ActivityOf(records, team, "so"));

        // negative: another session's record is never taken for this one, and the team's own record wins over a newer one elsewhere
        Assert.Null(TreeData.ActivityOf(records, team, "s-none"));
        var own = new LaneRecord(JsonText.Parse("""{"activity":"idle"}"""), 2);
        var withOwn = new Records(new() { [("activity", ActivityLane.KeyOf(Canon, "so"))] = own, [("activity", ActivityLane.KeyOf(Other, "so"))] = mine });
        Assert.Same(own, TreeData.ActivityOf(withOwn, team, "so"));
    }

    [Fact]
    public void ALeadsActivityOutsideItsTeamsCheckoutRecomposesTheTeamUnlessTheTeamHoldsItsOwn()
    {
        const string Other = "/elsewhere/.claude/ah2";
        var seen = new LaneRecord(JsonText.Parse("""{"activity":"blocked"}"""), 5);
        var changed = new LaneKey(ActivityLane.LaneName, ActivityLane.KeyOf(Other, "so"));
        Dictionary<(string Lane, string Key), LaneRecord> all = new()
        {
            [(TeamLane.LaneName, TeamLane.KeyOf(Canon, TeamPath))] = new LaneRecord(JsonValue.FromObject(TeamLane.ValueOf(Canon, TeamPath, "default", JsonText.Parse(Doc("so", "dev", "qa")))), 1),
            [(RegistrationLane.LaneName, "so")] = new LaneRecord(JsonText.Parse(Reg("so", "orchestrator@default", 1, "2026-06-01T10:00:30Z").TrimEnd('}') + ",\"team_files\":[\"" + TeamPath + "\"]}"), 1),
            [(ActivityLane.LaneName, changed.Key)] = seen,
        };

        Assert.Equal([(ActivityLane.DirId(Canon), "default")], SummaryData.TeamsAffected(changed, null, seen, new Records(all)));

        // negative: once the team's own directory holds the session's record, that one is what it reads
        all[(ActivityLane.LaneName, ActivityLane.KeyOf(Canon, "so"))] = seen;
        Assert.Empty(SummaryData.TeamsAffected(changed, null, seen, new Records(all)));
    }

    [Fact]
    public void TheSummaryCountsTheMemberRowsAndIgnoresAnActivityKeyNoMemberOwns()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("working");
        reader.Activities["so"] = Act("idle");
        reader.Activities["s-stale"] = Act("blocked");
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var body = Summary(model);
        Assert.Equal(3, Num(body, "members"));
        Assert.Equal(1, Num(body, "working"));
        Assert.Equal(0, Num(body, "blocked"));
        Assert.Equal(0, Num(body, "needs_you"));
        Assert.Empty(Alerts(model));
        Assert.Equal("default", Text(body, "name"));
    }

    private static string ActUsing(long used, string turnAt = "2026-06-01T11:59:00Z") =>
        Act("working", turnAt).Replace("\"used\":1,", "\"used\":" + used.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",", StringComparison.Ordinal);

    [Fact]
    public void AMembersContextIsServedAndFollowsItsReading()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = ActUsing(1000);
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        Assert.Contains("\"context_text\":\"1k\"", Body(model, MemberId("dev")), StringComparison.Ordinal);

        reader.Activities["s-dev"] = ActUsing(55_000);
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);

        Assert.Contains("\"context_text\":\"55k\"", Body(model, MemberId("dev")), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAnActivityWriteThatKeepsTheReadingLeavesTheMemberAlone()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = ActUsing(1000);
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        var version = VersionOf(model, MemberId("dev"));

        reader.Activities["s-dev"] = ActUsing(1000, "2026-06-01T11:59:30Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);

        Assert.Equal(version, VersionOf(model, MemberId("dev")));
    }

    [Fact]
    public void AnOpenExchangeToAGoneMemberIsStaleAndOneToNobodyIsNot()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 77]);
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        reader.Activities["s-dev"] = Act("working");
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["b"] = Msg("developer", "orchestrator", "ghost", null, "default");
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var stale = Summary(model).Member("stale_ids")!.Value.AsArray().Select(static s => s.AsString());
        Assert.Equal(["exchange:" + D + "/a"], stale);
    }

    [Fact]
    public void NegativeAnOpenExchangeToARunningMemberIsNotStale()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Empty(Summary(model).Member("stale_ids")!.Value.AsArray());
    }

    [Fact]
    public void AMemberWithNoActivityRecordIsUnknownAndNotCountedWorking()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Equal(3, Num(Summary(model), "members"));
        Assert.Equal(0, Num(Summary(model), "working"));
        Assert.Equal(["unknown", "unknown", "unknown"], SummaryData.Members(model.Records, TreeData.Teams(model.Records).Single(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Select(static m => m.State));

        reader.Activities["s-dev"] = Act("working");
        var (withRecord, _) = SeededWithActivity(reader);
        using var second = withRecord;
        Assert.Equal(1, Num(Summary(withRecord), "working"));
        Assert.Contains("working", SummaryData.Members(withRecord.Records, TreeData.Teams(withRecord.Records).Single(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Select(static m => m.State));
    }

    [Fact]
    public void FiftyToolCallsThatChangeNoStatePublishNothingAndAStateChangePublishesOnce()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("working");
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        var before = model.Store.Current.Slices[SummaryId].Version;
        var updatedAt = Text(Summary(model), "updated_at");
        var start = DateTimeOffset.Parse("2026-06-01T12:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        for (var i = 1; i <= 50; i++)
        {
            var toolAt = Iso(start + (i * 1000L)).Replace(".000Z", "Z", StringComparison.Ordinal);
            reader.Activities["s-dev"] = Act("working", toolAt: toolAt, ctxAt: toolAt);
            model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
            Drain(model, clock);
            Assert.NotEqual(toolAt, Text(Summary(model), "updated_at"));
        }

        Assert.Equal(before, model.Store.Current.Slices[SummaryId].Version);
        Assert.Equal(updatedAt, Text(Summary(model), "updated_at"));

        reader.Activities["s-dev"] = Act("idle", toolAt: "2026-06-01T12:05:00Z", ctxAt: "2026-06-01T12:05:00Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);

        Assert.NotEqual(before, model.Store.Current.Slices[SummaryId].Version);
        Assert.Equal(0, Num(Summary(model), "working"));
        Assert.Equal("2026-06-01T12:05:00.000Z", Text(Summary(model), "updated_at"));
    }

    [Fact]
    public void ABlockedMemberIsABlockedAlertAndAFailedOneAnErrorAlertWithTheTimeTheirStateBegan()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("blocked", toolAt: "2026-06-01T12:00:05Z");
        reader.Activities["so"] = Act("failed", toolAt: "2026-06-01T12:00:09Z");
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;

        var body = Summary(model);
        Assert.Equal(2, Num(body, "blocked"));
        Assert.Equal(2, Num(body, "needs_you"));
        Assert.Equal(0, Num(body, "waiting_on_you"));
        var alerts = Alerts(model);
        Assert.Equal(["error", "blocked"], alerts.Select(static i => Text(i, "kind")));
        Assert.Equal($"member:{D}/default/@orchestrator", Text(alerts[0], "member"));
        Assert.Equal("2026-06-01T12:00:09.000Z", Text(alerts[0], "since"));
        Assert.Null(body.Member("alerts_truncated"));

        var version = model.Store.Current.Slices[SummaryId].Version;
        reader.Activities["s-dev"] = Act("blocked", toolAt: "2026-06-01T12:03:00Z", ctxAt: "2026-06-01T12:03:00Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);
        Assert.Equal(version, model.Store.Current.Slices[SummaryId].Version);
        Assert.Equal("2026-06-01T12:00:05.000Z", Text(Alerts(model)[1], "since"));
    }

    private const string ClosedAt = "2026-06-01T09:00:00.000Z";

    private static string ClosedDoc() => Doc("so", "dev").TrimEnd('}') + ",\"closed\":{\"at\":\"" + ClosedAt + "\",\"by\":\"ui\"}}";

    /// <summary>Waiting decision alert and count, as a closed team that had one would compose them.</summary>
    private static JsonObject ClosedBody(string liveness)
    {
        var alerts = new JsonArray();
        alerts.Add(new JsonObject { ["kind"] = "needs_you", ["text"] = "Waiting on you · 2" });
        alerts.Add(new JsonObject { ["kind"] = "blocked", ["text"] = "Waiting on a permission" });
        return new JsonObject
        {
            ["closed_mark"] = ClosedAt,
            ["state"] = "needs_you",
            ["running"] = true,
            ["members"] = 1,
            ["streams_open"] = 0,
            ["needs_you"] = 3,
            ["waiting_on_you"] = 2,
            ["waiting_rows"] = new JsonArray { new JsonObject { ["kind"] = "decision" } },
            ["waiting_truncated"] = true,
            ["alerts"] = alerts,
            ["member_states"] = new JsonObject { ["m"] = new JsonObject { ["liveness"] = liveness } },
        };
    }

    [Theory]
    [InlineData("proven_dead")]
    [InlineData("unknown")]
    public void AClosedTeamWithNobodyLiveReadsEndedWithNothingWaiting(string liveness)
    {
        var body = ClosedBody(liveness);

        SummaryData.ApplyStatus(body);

        Assert.Equal((ClosedAt, ClosedAt), (body.Get("closed_at")!.Value.AsString(), body.Get("quiet_at")!.Value.AsString()));
        Assert.Equal(("ended", "ended", true), (body.Get("status")!.Value.AsString(), body.Get("state")!.Value.AsString(), body.Get("ended")!.Value.AsBool()));
        Assert.Equal((0d, 0d), (body.Get("needs_you")!.Value.AsNumber(), body.Get("waiting_on_you")!.Value.AsNumber()));
        Assert.Empty(body.Get("waiting_rows")!.Value.AsArray());
        Assert.False(body.ContainsKey("waiting_truncated"));
        Assert.Empty(body.Get("alerts")!.Value.AsArray());
    }

    [Fact]
    public void AClosedTeamWithALiveMemberIgnoresTheMarker()
    {
        var body = ClosedBody("live");

        SummaryData.ApplyStatus(body);

        Assert.False(body.ContainsKey("closed_at"));
        Assert.True(body.Get("quiet_at")!.Value.IsNull);
        Assert.Equal(("live", 3d, 2d), (body.Get("status")!.Value.AsString(), body.Get("needs_you")!.Value.AsNumber(), body.Get("waiting_on_you")!.Value.AsNumber()));
        Assert.Equal(2, body.Get("alerts")!.Value.AsArray().Count);
    }

    private static JsonObject WithLiveOrchestrator(JsonObject body, string? other = null)
    {
        var states = new JsonObject { ["member:d/t/@orchestrator"] = new JsonObject { ["liveness"] = "live" } };
        if (other is not null)
        {
            states[other] = new JsonObject { ["liveness"] = "live" };
        }

        body["member_states"] = states;
        return body;
    }

    [Fact]
    public void ALiveOrchestratorDoesNotHoldAClosedTeamOnShow()
    {
        var body = WithLiveOrchestrator(ClosedBody("live"));

        SummaryData.ApplyStatus(body);

        Assert.Equal(ClosedAt, body.Get("closed_at")!.Value.AsString());
        Assert.Equal(("ended", true, false), (body.Get("status")!.Value.AsString(), body.Get("ended")!.Value.AsBool(), body.Get("running")!.Value.AsBool()));
        Assert.Equal(0d, body.Get("needs_you")!.Value.AsNumber());
        Assert.Empty(body.Get("alerts")!.Value.AsArray());
    }

    [Fact]
    public void AClosedTeamRaisesNoCardForItsLiveOrchestratorBlockedElsewhere()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = ClosedDoc();
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        reader.LivePids.AddRange([1, 77]);
        reader.Activities["so"] = Act("blocked");
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Equal(ClosedAt, Text(Summary(model), "closed_at"));
        Assert.Empty(Alerts(model));
    }

    [Theory]
    [InlineData("member:d/t/dev")]
    [InlineData("member:d/t/orchestrator")] // a real member may carry the display name
    public void ALiveMemberBesideTheOrchestratorStillIgnoresTheMarker(string member)
    {
        var body = WithLiveOrchestrator(ClosedBody("live"), member);

        SummaryData.ApplyStatus(body);

        Assert.False(body.ContainsKey("closed_at"));
        Assert.Equal(("live", true), (body.Get("status")!.Value.AsString(), body.Get("running")!.Value.AsBool()));
    }

    [Fact]
    public void ALiveOrchestratorWithNoMarkerReadsAsUsual()
    {
        var body = WithLiveOrchestrator(ClosedBody("live"));
        _ = body.Remove("closed_mark");

        SummaryData.ApplyStatus(body);

        Assert.False(body.ContainsKey("closed_at"));
        Assert.Equal(("live", true, 3d), (body.Get("status")!.Value.AsString(), body.Get("running")!.Value.AsBool(), body.Get("needs_you")!.Value.AsNumber()));
    }

    [Fact]
    public void AnOpenTeamServesNoCloseFields()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Null(Summary(model).Member("closed_at"));
        Assert.Null(Summary(model).Member("closed_mark"));
    }

    [Fact]
    public void TheComposedSummaryCarriesTheRecordsMarkerThroughToClosed()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = ClosedDoc();
        reader.Registrations["so"] = (Reg("so", "orchestrator@default", 88, "2026-06-01T10:00:30Z"), [TeamPath]);
        reader.LivePids.Add(77);
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        var (gone, _) = SeededWithActivity(reader);
        using var goneOwner = gone;
        Assert.Equal(ClosedAt, Text(Summary(gone), "closed_at"));
        Assert.Equal("ended", Text(Summary(gone), "status"));

        // the recorded Orchestrator alone is running: it is still counted, and the team still reads closed
        reader.LivePids.Add(88);
        var (orchestrator, _) = SeededWithActivity(reader);
        using var orchestratorOwner = orchestrator;
        Assert.Equal(ClosedAt, Text(Summary(orchestrator), "closed_at"));
        Assert.Equal("ended", Text(Summary(orchestrator), "status"));
        Assert.Equal(1, Num(Summary(orchestrator), "members"));

        // a spawn into the closed team makes it visible by itself
        reader.LivePids.Add(42);
        var (live, _) = SeededWithActivity(reader);
        using var liveOwner = live;
        Assert.Null(Summary(live).Member("closed_at"));
        Assert.Equal("live", Text(Summary(live), "status"));

        // a reopen removes the marker: the live Orchestrator alone reads as usual again
        _ = reader.LivePids.RemoveAll(static p => p == 42);
        reader.Teams[TeamPath] = Doc("so", "dev");
        var (reopened, _) = SeededWithActivity(reader);
        using var reopenedOwner = reopened;
        Assert.Null(Summary(reopened).Member("closed_at"));
        Assert.Equal("live", Text(Summary(reopened), "status"));
    }

    [Fact]
    public void AMemberWhoseProcessIsGoneIsNeitherWorkingNorBlockedNorAnAlert()
    {
        var reader = Truth();
        reader.Activities["s-dev"] = Act("blocked");
        reader.LivePids.AddRange([1, 42]);
        var (live, _) = SeededWithActivity(reader);
        using var liveOwner = live;
        Assert.Equal(1, Num(Summary(live), "blocked"));

        _ = reader.LivePids.RemoveAll(static p => p == 42);
        reader.LivePids.Add(77);
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        var (gone, _) = SeededWithActivity(reader);
        using var goneOwner = gone;
        Assert.Equal(0, Num(Summary(gone), "blocked"));
        Assert.Empty(Alerts(gone));
    }

    [Fact]
    public void AMemberWhoseProcessIsGoneIsNotAnAgentAndALiveOneIs()
    {
        var reader = Truth();
        reader.Activities["s-dev"] = Act("working");
        reader.LivePids.AddRange([1, 42]);
        var (live, _) = SeededWithActivity(reader);
        using var liveOwner = live;
        Assert.Equal(3, Num(Summary(live), "members"));

        _ = reader.LivePids.RemoveAll(static p => p == 42);
        reader.LivePids.Add(77);
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        var (gone, _) = SeededWithActivity(reader);
        using var goneOwner = gone;
        Assert.Equal(2, Num(Summary(gone), "members"));
        Assert.True(Summary(gone).Member("running")!.Value.AsBool());
    }

    [Fact]
    public void AnOpenRequestToALiveMemberIsInFlightAndOneToAGoneMemberIsStale()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("working");
        var (live, liveClock) = SeededWithActivity(reader);
        using var liveOwner = live;
        Send(live, liveClock, reader, "x1", PairMsg("developer", "orchestrator", "dev", null, Ago(10 * Minute)));
        Assert.Equal(1, Num(Summary(live), "in_flight"));
        Assert.Equal(0, Num(Summary(live), "stale"));

        _ = reader.LivePids.RemoveAll(static p => p == 42);
        reader.LivePids.Add(77);
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        var (gone, goneClock) = SeededWithActivity(reader);
        using var goneOwner = gone;
        Send(gone, goneClock, reader, "x1", PairMsg("developer", "orchestrator", "dev", null, Ago(10 * Minute)));
        Assert.Equal(0, Num(Summary(gone), "in_flight"));
        Assert.Equal(1, Num(Summary(gone), "stale"));
        Assert.True(Summary(gone).Member("oldest_in_flight_at")!.Value.IsNull, "a stale request does not set the oldest in-flight time");
    }

    [Fact]
    public void AnOpenRequestToAMemberTheTeamNoLongerHasIsStale()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("working");
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;

        Send(model, clock, reader, "x1", PairMsg("developer", "orchestrator", "dismissed-one", null, Ago(10 * Minute)));

        Assert.Equal(0, Num(Summary(model), "in_flight"));
        Assert.Equal(1, Num(Summary(model), "stale"));
    }

    private static TreeReader Crowd(int blocked)
    {
        var reader = new TreeReader();
        var names = Enumerable.Range(0, blocked).Select(i => "m" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        reader.Teams[TeamPath] = Doc("so", names);
        reader.KnownDirs.Add(Canon);
        for (var i = 0; i < blocked; i++)
        {
            var s = "s-m" + i.ToString(CultureInfo.InvariantCulture);
            reader.Registrations[s] = (Reg(s, names[i], 1000 + i, "2026-06-01T10:05:00Z"), [TeamPath]);
            reader.LivePids.Add(1000 + i);
            reader.Activities[s] = Act("blocked", toolAt: "2026-06-01T12:" + (i % 60).ToString("00", CultureInfo.InvariantCulture) + ":00Z");
        }

        return reader;
    }

    [Fact]
    public void ATeamsAlertsHoldTheNewestTwentyAndSayTheyWereTruncated()
    {
        var (model, _) = SeededWithActivity(Crowd(30));
        using var owner = model;

        var body = Summary(model);
        var alerts = body.Member("alerts")!.Value.AsArray();
        Assert.Equal(20, alerts.Count);
        Assert.True(body.Member("alerts_truncated")!.Value.AsBool());
        var sinces = alerts.Select(static i => Text(i, "since")).ToList();
        Assert.Equal(sinces.OrderByDescending(static x => x, StringComparer.Ordinal), sinces);
        Assert.Equal("2026-06-01T12:29:00.000Z", sinces[0]);
    }

    [Fact]
    public void ExactlyTwentyAlertsAreNotTruncated()
    {
        var (model, _) = SeededWithActivity(Crowd(20));
        using var owner = model;

        var body = Summary(model);
        Assert.Equal(20, body.Member("alerts")!.Value.AsArray().Count);
        Assert.Null(body.Member("alerts_truncated"));
    }

    [Fact]
    public void AnAlertGoesWhenItsStateEndsAndAComeBackIsANewAlertWithANewTime()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("blocked", toolAt: "2026-06-01T12:00:05Z");
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        Assert.Single(Alerts(model));

        reader.Activities["s-dev"] = Act("working", toolAt: "2026-06-01T12:01:00Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);
        Assert.Empty(Alerts(model));

        reader.Activities["s-dev"] = Act("blocked", toolAt: "2026-06-01T12:02:00Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);
        Assert.Equal("2026-06-01T12:02:00.000Z", Text(Assert.Single(Alerts(model)), "since"));
    }

    /// <summary>One plan log for the default team, in the shape the real logs have; no checklists.</summary>
    private sealed class PlanReader : IViewStateReader
    {
        private const string Rows = """
            [{"ts":"2026-06-01T10:00:00Z","stream":"s","item":"ask","event":"add","title":"Ask the user","by":"orchestrator@default"},
             {"ts":"2026-06-01T10:01:00Z","stream":"s","item":"ask","event":"wait-user","note":"Which one?","by":"orchestrator@default"},
             {"ts":"2026-06-01T10:02:00Z","stream":"s","item":"build","event":"add","title":"Build it","by":"orchestrator@default"},
             {"ts":"2026-06-01T10:03:00Z","stream":"s","item":"build","event":"assign","holder":"dev","by":"orchestrator@default"},
             {"ts":"2026-06-01T10:04:00Z","stream":"s","item":"build","event":"start","by":"orchestrator@default"}]
            """;

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => op switch
        {
            "keys" => JsonText.Parse("""{"teams":["_default"]}"""),
            "streams" => JsonText.Parse("""{"streams":[]}"""),
            _ => JsonText.Parse("{\"streams\":[{\"stream\":\"s\",\"rows\":" + Rows + ",\"offset\":5,\"length\":5}]}"),
        };
    }

    [Fact]
    public void TheCountsNeedAClientOnTheProgressLaneAndNoPlanSliceHeld()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("working");
        var connected = false;
        var plan = new LazyLane(new ProgressLane(new PlanReader(), TestFolds.New, static () => [Canon]), () => connected);
        var (model, clock) = SeededWithActivity(reader, plan: plan);
        using var owner = model;

        model.EmitKeySet(ProgressLane.LaneName);
        Drain(model, clock);
        Assert.Equal((0, 0, 0), (Num(Summary(model), "waiting_on_you"), Num(Summary(model), "in_progress_now"), Num(Summary(model), "items_total")));

        connected = true;
        plan.Arrive(model);
        Drain(model, clock);

        var body = Summary(model);
        Assert.Equal((1, 1, 2), (Num(body, "waiting_on_you"), Num(body, "in_progress_now"), Num(body, "items_total")));
        Assert.Equal("Ask the user", Text(Assert.Single(body.Member("waiting_rows")!.Value.AsArray()), "title"));
        Assert.DoesNotContain(model.Store.Current.Slices.Keys, static id => id.StartsWith("plan", StringComparison.Ordinal));
    }

    [Fact]
    public void APlanNotYetReadSaysSoAndALoadedZeroIsLoaded()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        var progress = new ProgressLane(new NoPlanReader(), TestFolds.New, static () => [Canon]);
        Assert.True(progress.PlanPending(Canon, "_default"), "not listed yet");
        Assert.Equal([ProgressLane.KeyOf(Canon, "_default")], progress.Keys());
        Assert.True(progress.PlanPending(Canon, "_default"), "listed and not read: the case of a silent team");
        Assert.False(progress.PlanPending(Canon, "no-such-folder"), "negative: listed, and this team has no plan to wait for");
        _ = progress.Reread(ProgressLane.KeyOf(Canon, "_default"), 1);
        Assert.False(progress.PlanPending(Canon, "_default"), "read, though it held nothing");

        var (pending, _) = SeededWithActivity(reader, summary: new TeamSummaryComposer(planPending: static (_, _) => true));
        using (pending)
        {
            Assert.False(Summary(pending).Member("plan_loaded")!.Value.AsBool());
        }

        var (loaded, _) = SeededWithActivity(reader, summary: new TeamSummaryComposer(planPending: static (_, _) => false));
        using var owner = loaded;
        Assert.True(Summary(loaded).Member("plan_loaded")!.Value.AsBool());
        Assert.Equal(0, Num(Summary(loaded), "items_total"));
    }

    /// <summary>One item done on June 1st, another still moving; the times are the log's own.</summary>
    private sealed class DoneReader : IViewStateReader
    {
        private const string Rows = """
            [{"ts":"2026-06-01T10:00:00Z","stream":"s","item":"a","event":"add","title":"make-the-ship","by":"o"},
             {"ts":"2026-06-01T10:05:00Z","stream":"s","item":"a","event":"done","by":"o"},
             {"ts":"2026-06-01T10:06:00Z","stream":"s","item":"b","event":"add","title":"Moving","by":"o"}]
            """;

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => op switch
        {
            "keys" => JsonText.Parse("""{"teams":["_default"]}"""),
            "streams" => JsonText.Parse("""{"streams":[]}"""),
            _ => JsonText.Parse("{\"streams\":[{\"stream\":\"s\",\"rows\":" + Rows + ",\"offset\":3,\"length\":3}]}"),
        };
    }

    [Fact]
    public void DoneIsDatedByTheRecordSoARestartLosesNoneAndAnOlderDayStaysOlder()
    {
        // A lane with no memory is what a restart leaves: it reads the same time at any later read.
        foreach (var at in new long[] { FixtureNowMs, FixtureNowMs + 86_400_000 })
        {
            var progress = new ProgressLane(new DoneReader(), TestFolds.New, static () => [Canon]);
            _ = progress.Reread(ProgressLane.KeyOf(Canon, "_default"), at);
            var items = progress.ItemsBodyOf(ProgressLane.SuffixOf(Canon, "_default"), "s")!.Value.Member("items")!.Value.AsArray();
            Assert.Equal(DateTimeOffset.Parse("2026-06-01T10:05:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), (long)items.Single(i => Text(i, "key") == "s:a").Member("done_seen_at")!.Value.AsNumber());
            Assert.Equal("make the ship", Text(items.Single(i => Text(i, "key") == "s:a"), "title"));
            Assert.True(items.Single(i => Text(i, "key") == "s:b").Member("done_seen_at")!.Value.IsNull, "negative: not done");
        }
    }

    [Fact]
    public void AnExchangeWithNoTitleReadsItsSlugWithSpaces()
    {
        var reader = Truth();
        reader.Exchanges["x1"] = Msg("developer", "orchestrator", "dev", null, "default").Replace("\"slug\":\"s\"", "\"slug\":\"make-the-ship\"", StringComparison.Ordinal);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;
        Assert.Contains("\"title\":\"make the ship\"", Body(model, MessagesLane.SliceIdOfDirId(D, "x1")), StringComparison.Ordinal);
    }

    private sealed class NoPlanReader : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) =>
            op == "keys" ? JsonText.Parse("""{"teams":["_default"]}""") : JsonText.Parse("""{"streams":[]}""");
    }

    [Fact]
    public void TheStampOfTheStatusDocumentIsNotTheTeamsLastWork()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("idle");
        reader.Statuses[Canon] = """{"written_at":"2030-01-01T00:00:00.000Z"}""";
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        var before = Text(Summary(model), "updated_at");
        Assert.StartsWith("2026", before, StringComparison.Ordinal);

        // negative: another reading of the document leaves it where it was, so a silent team goes quiet
        reader.Statuses[Canon] = """{"written_at":"2031-01-01T00:00:00.000Z"}""";
        model.Emit(StatusLane.LaneName, Canon);
        Drain(model, clock);
        Assert.Equal(before, Text(Summary(model), "updated_at"));
    }

    [Fact]
    public void TheSummaryTellsAMembersToolAndItsItemBranchAndWorktreeOnlyWhenKnown()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("working");
        var (model, clock) = SeededWithActivity(reader, plan: new ProgressLane(new PlanReader(), TestFolds.New, static () => [Canon]));
        using var owner = model;
        model.EmitKeySet(ProgressLane.LaneName);
        Drain(model, clock);
        var dev = Summary(model).Member("member_states")!.Value.Member(MemberId("dev"))!.Value;
        Assert.Equal("Build it", Text(dev, "item"));
        Assert.Null(Summary(model).Member("member_states")!.Value.Member(TreeIds.Member(Canon, "default", TreeIds.Orchestrator))?.Member("item"));
        Assert.Null(dev.Member("branch"));
        Assert.Equal(0, Num(Summary(model), "worktrees"));
    }

    [Fact]
    public void WorktreesCountsTheMembersDistinctRootsAndAMemberTellsItsBranchAndRoot()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Teams[TeamPath] = Doc("so", "dev", "qa")
            .Replace("\"transport_id\":\"p-dev\",\"stream\":null", "\"transport_id\":\"p-dev\",\"stream\":\"ah/x\",\"expected_root\":\"/w/x\"", StringComparison.Ordinal)
            .Replace("\"transport_id\":\"p-qa\",\"stream\":null", "\"transport_id\":\"p-qa\",\"stream\":null,\"expected_root\":\"/w/x\"", StringComparison.Ordinal);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;
        var dev = Summary(model).Member("member_states")!.Value.Member(MemberId("dev"))!.Value;
        Assert.Equal(("ah/x", "/w/x"), (Text(dev, "branch"), Text(dev, "work_root")));
        Assert.Equal(1, Num(Summary(model), "worktrees")); // negative: two members on one root are one worktree
    }

    [Theory]
    [InlineData("live", "dev", null)] // a team with a live member does not go quiet
    [InlineData("not_seen", "dev", "stale_at")] // negative: not proven live
    [InlineData("proven_dead", "dev", "stale_at")] // negative: proven dead
    [InlineData("live", "@orchestrator", "stale_at")] // negative: an Orchestrator may be leading another team
    public void ALiveMemberKeepsATeamFromGoingQuiet(string liveness, string member, string? quietAt)
    {
        var body = new JsonObject
        {
            ["state"] = "idle", ["running"] = true, ["members"] = 1, ["streams_open"] = 0,
            ["updated_at"] = "2026-11-01T00:00:00.000Z", ["stale_at"] = "2026-11-02T00:00:00.000Z",
            ["member_states"] = JsonValue.FromObject(new JsonObject { ["member:d/t/" + member] = JsonValue.FromObject(new JsonObject { ["liveness"] = liveness }) }),
        };
        SummaryData.ApplyStatus(body);
        Assert.Equal(quietAt is null ? JsonValue.Null : body.Get(quietAt)!.Value, body.Get("quiet_at")!.Value);
    }

    [Theory]
    [InlineData(1, null, true)]
    [InlineData(0, null, false)]
    [InlineData(1, "2026-11-01T12:00:00.000Z", false)]
    public void UnfinishedWorkKeepsATeamFromGoingQuietUnlessItIsClosed(int unfinished, string? closed, bool onShow)
    {
        // Nobody left in the team: with nothing unfinished it goes quiet at its last work.
        var body = new JsonObject
        {
            ["state"] = "ended", ["running"] = false, ["members"] = 0, ["streams_open"] = 0, ["unfinished"] = unfinished,
            ["updated_at"] = "2026-11-01T00:00:00.000Z", ["stale_at"] = "2026-11-02T00:00:00.000Z",
        };
        if (closed is not null)
        {
            body["closed_mark"] = closed;
        }

        SummaryData.ApplyStatus(body);
        Assert.Equal(onShow, body.Get("quiet_at")!.Value.IsNull);
    }

    /// <summary>The team reads of a tree reader; every other read answers nothing.</summary>
    private sealed class TeamsOnlyReader(TreeReader teams) : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) =>
            domain == "team" ? teams.Read(domain, op, hierarchyDir, args) : JsonValue.Null;
    }

    [Fact]
    public async Task SitrepResolvesTheDefaultTeamByItsNameOrItsProgressFolder()
    {
        var root = Directory.CreateTempSubdirectory("ah-sitrep-work-").FullName;
        var dir = Path.Combine(root, "h");
        var home = Path.Combine(root, "home");
        _ = Directory.CreateDirectory(dir);
        _ = Directory.CreateDirectory(home);
        var reader = new TreeReader();
        reader.Teams[dir + "/team.json"] = Doc("so", "dev", "qa");
        string? seenFolder = null;
        Func<string, (Ah.Hierarchy.ChecklistLiveness? Live, IReadOnlyList<JsonValue> Decisions, IReadOnlyDictionary<string, string> Owners)>? work = null;
        var options = TestFolds.Options with
        {
            Home = home,
            SitrepProbe = (_, team, _, w) =>
            {
                seenFolder = team;
                Volatile.Write(ref work, w);
                return (JsonText.Parse("{}"), [team]);
            },
        };
        using var model = LazyAwareModel.Unwrap(WatchModule.CreateReadModel(new TeamsOnlyReader(reader), () => [dir], options))!;
        try
        {
            WatchModule.StartReadModel(model);
            var id = "sitrep:" + ActivityLane.DirId(dir) + "/default";
            var since = 0L;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((Volatile.Read(ref work) is not { } w || w("_default").Live is null) && DateTime.UtcNow < deadline)
            {
                var reply = JsonText.Parse(await model.PollAsync(since, null, ["list:teams", id], TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
                since = (long)reply.Member("seq")!.Value.AsNumber();
            }

            Assert.Equal("default", seenFolder);
            Assert.NotNull(work!("_default").Live);
            Assert.NotNull(work("default").Live);
            Assert.Null(work("_nosuch").Live);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ASliceHeldBeforeItsDirectoryIsKnownIsReadOnceItIsAndNegativeOneOfAnUnknownDirectoryIsNot()
    {
        var root = Directory.CreateTempSubdirectory("ah-sitrep-early-").FullName;
        var dir = Path.Combine(root, "h");
        var home = Path.Combine(root, "home");
        _ = Directory.CreateDirectory(dir);
        _ = Directory.CreateDirectory(home);
        var reader = new TreeReader();
        reader.Teams[dir + "/team.json"] = Doc("so", "dev", "qa");
        IReadOnlyList<string> known = [];
        var probed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var options = TestFolds.Options with
        {
            Home = home,
            SitrepProbe = (at, team, _, _) =>
            {
                probed.Enqueue(at);
                return (JsonText.Parse("{}"), [team]);
            },
        };
        using var model = LazyAwareModel.Unwrap(WatchModule.CreateReadModel(new TeamsOnlyReader(reader), () => Volatile.Read(ref known), options))!;
        try
        {
            WatchModule.StartReadModel(model);
            string[] held = ["sitrep:" + ActivityLane.DirId(dir) + "/default", "sitrep:" + ActivityLane.DirId(dir + "-other") + "/default"];
            var since = 0L;
            async Task PollOnce()
            {
                var reply = JsonText.Parse(await model.PollAsync(since, null, held, TimeSpan.FromMilliseconds(200), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
                since = (long)reply.Member("seq")!.Value.AsNumber();
            }

            for (var i = 0; i < 3; i++)
            {
                await PollOnce();
            }

            Assert.Empty(probed);

            Volatile.Write(ref known, [dir]);
            WatchModule.ReconcileReadModel(model, "teams");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (probed.IsEmpty && DateTime.UtcNow < deadline)
            {
                await PollOnce();
            }

            Assert.Equal(dir, Assert.Single(probed.Distinct()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheWaitAgeIsTheOldestDecisionTimeAndADaemonRestartLeavesItAlone()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Statuses[Canon] = "{\"schema\":1,\"enabled\":true,\"teams\":[{\"team\":\"default\",\"members\":[],\"dispatches\":[],\"pipeline\":{\"anchor_id\":\"a1\",\"round_cap\":3,\"items\":[]}}]}";
        reader.Decisions[(Canon, "a1")] =
        [
            "{\"kind\":\"parked\",\"id\":\"d2\",\"question\":\"Which?\",\"time\":\"2026-06-01T10:10:00Z\"}",
            "{\"kind\":\"parked\",\"id\":\"d1\",\"question\":\"Which?\",\"time\":\"2026-06-01T09:00:00Z\"}",
        ];

        var (first, _) = SeededWithActivity(reader);
        using (first)
        {
            Assert.Equal("2026-06-01T09:00:00.000Z", Text(Assert.Single(Alerts(first)), "since"));
        }

        // A new daemon holds no memory of the first one.
        var (second, _) = SeededWithActivity(reader);
        using var owner = second;
        Assert.Equal("2026-06-01T09:00:00.000Z", Text(Assert.Single(Alerts(second)), "since"));
    }

    [Fact]
    public void NegativeAWaitWithNoRecordedTimeServesNoAgeRatherThanWhenItWasFirstSeen()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Statuses[Canon] = "{\"schema\":1,\"enabled\":true,\"teams\":[{\"team\":\"default\",\"members\":[],\"dispatches\":[],\"pipeline\":{\"anchor_id\":\"a1\",\"round_cap\":3,\"items\":[]}}]}";
        reader.Decisions[(Canon, "a1")] = ["{\"kind\":\"parked\",\"id\":\"d1\",\"question\":\"Which?\"}"];
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.True(Assert.Single(Alerts(model)).Member("since")!.Value.IsNull);
    }

    [Fact]
    public void ADecisionWaitingIsANeedsYouAlertSinceItWasParkedAndCountsInTheSummary()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        static string Parked(string id) => "{\"kind\":\"parked\",\"id\":\"" + id + "\",\"question\":\"Which?\",\"time\":\"2026-06-01T10:10:00Z\"}";
        reader.Statuses[Canon] = "{\"schema\":1,\"enabled\":true,\"teams\":[{\"team\":\"default\",\"members\":[],\"dispatches\":[],\"pipeline\":{\"anchor_id\":\"a1\",\"round_cap\":3,\"items\":[]}}]}";
        reader.Decisions[(Canon, "a1")] = [Parked("d1"), Parked("d2")];
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Equal(2, Num(Summary(model), "needs_you"));
        Assert.Equal(2, Num(Summary(model), "waiting_on_you"));
        var item = Assert.Single(Alerts(model));
        Assert.Equal("needs_you", Text(item, "kind"));
        Assert.Equal("Waiting on you · 2", Text(item, "text"));
        Assert.Equal("2026-06-01T10:10:00.000Z", Text(item, "since"));
        Assert.Null(item.Member("member"));

        reader.Decisions[(Canon, "a1")] = [Parked("d1"), Parked("d2"), Parked("d3")];
        model.Emit("decisions", DecisionsLane.KeyOf(Canon, "a1"));
        Drain(model, clock);
        var raised = Assert.Single(Alerts(model));
        Assert.Equal("Waiting on you · 3", Text(raised, "text"));
        Assert.Equal("2026-06-01T10:10:00.000Z", Text(raised, "since"));

        reader.Decisions[(Canon, "a1")] = [];
        model.Emit("decisions", DecisionsLane.KeyOf(Canon, "a1"));
        Drain(model, clock);
        Assert.Empty(Alerts(model));
    }

    [Fact]
    public void FeedNewestAtMovesOnANewOrAnsweredExchangeAndOnNothingElse()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42]);
        reader.Activities["s-dev"] = Act("working");
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        Assert.True(Summary(model).Member("feed_newest_at")!.Value.IsNull);

        Send(model, clock, reader, "x1", PairMsg("developer", "orchestrator", "dev", null, Ago(10 * Minute)));
        var created = Text(Summary(model), "feed_newest_at");
        Assert.Equal(Iso(FixtureNowMs - (10 * Minute)), created);

        reader.Activities["s-dev"] = Act("working", toolAt: "2026-06-01T12:30:00Z", ctxAt: "2026-06-01T12:30:00Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);
        Assert.Equal(created, Text(Summary(model), "feed_newest_at"));

        var answered = PairMsg("developer", "orchestrator", "dev", null, Ago(10 * Minute), open: false, responded: Ago(2 * Minute));
        Send(model, clock, reader, "x1", answered);
        Assert.Equal(Iso(FixtureNowMs - (2 * Minute)), Text(Summary(model), "feed_newest_at"));
        Assert.Equal(0, Num(Summary(model), "in_flight"));
    }

    [Fact]
    public void AnActivityWriteRecomposesOnlyTheSummaryOfTheTeamThatOwnsTheSession()
    {
        var reader = Truth();
        reader.LivePids.AddRange([1, 42, 99]);
        const string OtherPath = Canon + "/teams/other.json";
        reader.Teams[OtherPath] = Doc("so2", "dev");
        reader.Registrations["s-x"] = (Reg("s-x", "dev", 99, "2026-06-01T10:05:00Z"), [OtherPath]);
        reader.Activities["s-dev"] = Act("working");
        reader.Activities["s-x"] = Act("working");
        reader.Activities["s-stale"] = Act("working");
        var counting = new CountingSummary(new TeamSummaryComposer(static () => FixtureNowMs));
        var (model, clock) = SeededWithActivity(reader, counting);
        using var owner = model;
        Assert.True(model.Store.Current.Slices.ContainsKey($"team-summary:{D}/other"));
        counting.Composed.Clear();

        reader.Activities["s-dev"] = Act("idle", toolAt: "2026-06-01T12:10:00Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-dev"));
        Drain(model, clock);

        Assert.Equal([SummaryId], counting.Composed.Keys.ToList());

        counting.Composed.Clear();
        reader.Activities["s-stale"] = Act("blocked", toolAt: "2026-06-01T12:11:00Z");
        model.Emit("activity", ActivityLane.KeyOf(Canon, "s-stale"));
        Drain(model, clock);

        Assert.Empty(counting.Composed);
    }

    [Fact]
    public void ARemovedTeamRemovesItsSummary()
    {
        var reader = Truth();
        reader.Activities["s-dev"] = Act("blocked");
        var (model, clock) = SeededWithActivity(reader);
        using var owner = model;
        Assert.True(model.Store.Current.Slices.ContainsKey(SummaryId));

        _ = reader.Teams.Remove(TeamPath);
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.False(model.Store.Current.Slices.ContainsKey(SummaryId));
    }

    [Fact]
    public void TheSummaryViewHoldsTheTeamListAndTheSummariesAndNoAlertsSliceExists()
    {
        Assert.Equal(["list:teams", "team-summary:"], ModelViews.PrefixesOf("summary")!);
        Assert.DoesNotContain("team-summary:", ModelViews.PrefixesOf("tree")!);
        var (model, _) = SeededWithActivity(Truth());
        using var owner = model;
        Assert.DoesNotContain(model.Store.Current.Slices.Keys, static id => id.StartsWith("alerts:", StringComparison.Ordinal));
    }
}
