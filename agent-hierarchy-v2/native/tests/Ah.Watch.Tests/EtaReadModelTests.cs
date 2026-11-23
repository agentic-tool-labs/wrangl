using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The ETA read model: slices carry file facts and no clock state; a gates change rereads the open requests of its directory; the projection is computed when a response is built.</summary>
public sealed class EtaReadModelTests
{
    private const string Canon = "/repo/.claude/ah2";
    private const string Other = "/other/.claude/ah2";
    private const string Peer = "/home/u/.claude/ah2/peer-pending.jsonl";

    [ThreadStatic]
    private static MessagesLane? lanes;
    private const string Created = "2026-10-21T08:00:00Z";
    private static readonly long CreatedMs = DateTimeOffset.Parse(Created, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private sealed class Reader : IViewStateReader
    {
        public Dictionary<(string Dir, string Id), bool> Open { get; } = [];

        public Dictionary<string, int> Reads { get; } = [];

        /// <summary>The gates file's rows; null makes <c>gates.read</c> fail.</summary>
        public JsonArray? Gates { get; set; }

        public Dictionary<string, double> Heard { get; } = [];

        public Dictionary<string, double> Busy { get; } = [];

        public Dictionary<string, double> Activity { get; } = [];

        /// <summary>Open requests that carry no eta facts, as one not sent by the Orchestrator does.</summary>
        public HashSet<string> NoFacts { get; } = [];

        /// <summary>Requests addressed to a role, with no name of their own for the addressee.</summary>
        public HashSet<string> RoleOnly { get; } = [];

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            var name = domain + "." + op;
            if (name == "msgs.keys")
            {
                var ids = new JsonArray();
                foreach (var id in Open.Keys.Where(k => k.Dir == hierarchyDir).Select(static k => k.Id))
                {
                    ids.Add(id);
                }

                return JsonValue.FromObject(new JsonObject { ["ids"] = ids });
            }

            if (name == "gates.read")
            {
                return Gates is null ? throw new InvalidOperationException("no gates") : JsonValue.FromArray(Gates);
            }

            if (name == "msgs.heard")
            {
                var heard = new JsonObject();
                foreach (var asked in args.Get("ids")!.Value.AsArray())
                {
                    heard[asked.AsString()] = JsonValue.FromNumber(Heard.GetValueOrDefault(asked.AsString()));
                }

                return JsonValue.FromObject(new JsonObject { ["heard"] = heard });
            }

            var key = args.Get("id")!.Value.AsString();
            Reads[key] = Reads.GetValueOrDefault(key) + 1;
            if (!Open.TryGetValue((hierarchyDir, key), out var open))
            {
                return JsonValue.Null;
            }

            return JsonValue.FromObject(new JsonObject
            {
                ["id"] = key,
                ["slug"] = "s",
                ["to"] = "implementor",
                ["from"] = "orchestrator",
                ["to_name"] = RoleOnly.Contains(key) ? JsonValue.Null : "cd",
                ["team"] = "tm",
                ["created"] = Created,
                ["has_request"] = true,
                ["has_response"] = !open,
                ["eta"] = "small",
                ["eta_facts"] = open && !NoFacts.Contains(key)
                    ? new JsonObject { ["threshold_s"] = 300, ["start_ms"] = CreatedMs, ["heard_ms"] = Heard.GetValueOrDefault(key), ["addressee_session"] = "s-" + key, ["busy_since_ms"] = Busy.GetValueOrDefault(key), ["busy_at_ms"] = Busy.GetValueOrDefault(key), ["activity_ms"] = Activity.GetValueOrDefault(key), ["nudge_ms"] = new JsonArray(), ["checked_ms"] = new JsonArray() }
                    : JsonValue.Null,
            });
        }
    }

    private sealed class Clock
    {
        public long Now { get; set; } = 5_000_000;

        public long Read() => Now;
    }

    private static (ReadModel Model, Reader Reader, Clock Clock, Clock Wall) Make(ComposerRegistry? composers = null)
    {
        var reader = new Reader();
        var clock = new Clock();
        var wall = new Clock { Now = CreatedMs };
        ReadModel? model = null;
        var canonical = new[] { Canon, Other };
        var lane = new MessagesLane(reader, () => canonical, () => canonical, rereadSelf: key => model!.Emit(MessagesLane.LaneName, key));
        model = new ReadModel([lane], new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, MessageIdOf, null, null, Peer), composers) { WallClockMs = wall.Read };
        lanes = lane;
        return (model, reader, clock, wall);
    }

    private static string? MessageIdOf(string name) => name.Split("--")[0] is { Length: 20 } id ? id : null;

    private static void Drain(ReadModel model, Clock clock)
    {
        for (var guard = 0; guard < 10_000; guard++)
        {
            if (model.Projector.Step())
            {
                continue;
            }

            if (model.Bus.NextDueInMs() is { } due && due > 0)
            {
                clock.Now += due;
                continue;
            }

            return;
        }

        throw new InvalidOperationException("the projector did not settle");
    }

    private static void Load(ReadModel model, Clock clock, string dir, string id)
    {
        model.Emit(MessagesLane.LaneName, MessagesLane.KeyOf(dir, id));
        Drain(model, clock);
    }

    private static JsonObject Body(ReadModel model, string dir, string id) =>
        JsonText.Parse(model.Store.Current.Slices[MessagesLane.SliceIdOf(dir, id)].Body).AsObject();

    private static string StateAt(ReadModel model, Clock wall, long nowMs, string dir, string id)
    {
        wall.Now = nowMs;
        var sliceId = MessagesLane.SliceIdOf(dir, id);
        var reply = JsonText.Parse(model.PollAsync(0, "e1", [sliceId], TimeSpan.Zero, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        return reply.Member("slices")!.Value.AsArray().Single().Member("body")!.Value.AsObject().Get("state")!.Value.AsString();
    }

    private const string A = "20261021-080000-aaaa";
    private const string B = "20261021-080000-bbbb";
    private const string C = "20261021-080000-cccc";
    private const string D = "20261021-080000-dddd";

    private static string TeamKey => Ah.Watch.Model.ActivityLane.DirId(Canon) + "/tm";

    private static string OpenList => "list:open:team:" + TeamKey;

    private static string Assignee => "member:" + TeamKey + "/cd";

    /// <summary>Publishes the team summary a served state reads: one member's state, a composed needs_you count and a composed alert.</summary>
    private sealed class FixedSummary(string? state, int needsYou, string? liveness = null, string? closedMark = null, bool stuckAsUnknown = false) : ISliceComposer
    {
        public string SlicePrefix => "team-summary";

        public IReadOnlyList<string> Lanes => [MessagesLane.LaneName];

        public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) => ["team-summary:" + TeamKey];

        public IReadOnlyList<string> AllIds(IRecordView records) => ["team-summary:" + TeamKey];

        public SliceChange Compose(string sliceId, IRecordView records)
        {
            var states = new JsonObject();
            if (state is not null)
            {
                var entry = new JsonObject { ["state"] = state };
                if (liveness is not null)
                {
                    entry["liveness"] = liveness;
                }

                if (stuckAsUnknown)
                {
                    entry[TeamSummaryComposer.StuckAsUnknownKey] = true;
                }

                states[Assignee] = JsonValue.FromObject(entry);
            }

            var alerts = new JsonArray { JsonValue.FromObject(new JsonObject { ["kind"] = "blocked", ["since"] = "2026-10-21T08:00:00.000Z", ["text"] = "Waiting on a permission" }) };
            var body = new JsonObject
            {
                ["running"] = true,
                ["working"] = 1,
                ["needs_you"] = needsYou,
                ["state"] = SummaryData.TeamState(true, true, needsYou, 1),
                ["alerts"] = alerts,
                ["member_states"] = states,
            };
            if (closedMark is not null)
            {
                body["closed_mark"] = closedMark;
            }

            return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), 1);
        }
    }

    private sealed record Stuck(JsonObject Entry, JsonObject Exchange, JsonObject List, JsonObject Summary);

    /// <summary>One open task (sent at the fixture's created time) served at <paramref name="nowMs"/> with its assignee in <paramref name="state"/> and last active at <paramref name="activityMs"/>.</summary>
    private static Stuck ServedStuck(string? state, long nowMs, long activityMs, int composedNeedsYou = 0, bool roleOnly = false, long heardMs = 0, string? closedMark = null, string? liveness = null, bool stuckAsUnknown = false)
    {
        var (model, reader, clock, wall) = Make(new ComposerRegistry().Register(new FixedOpenList(A)).Register(new FixedSummary(state, composedNeedsYou, liveness, closedMark, stuckAsUnknown)));
        using var owner = model;
        reader.Open[(Canon, A)] = true;
        reader.Activity[A] = activityMs;
        reader.Heard[A] = heardMs;
        if (roleOnly)
        {
            _ = reader.RoleOnly.Add(A);
        }

        Load(model, clock, Canon, A);
        wall.Now = nowMs;
        JsonObject Slice(string view, Func<JsonObject, bool> pick) =>
            JsonText.Parse(model.Manifest(view)!).Member("slices")!.Value.AsArray().Select(static s => s.Member("body")!.Value.AsObject()).Single(pick);
        var list = Slice("exchanges", static b => b.Get("open") is { IsArray: true });
        var exchange = Slice("exchanges", static b => b.Get("has_request") is { IsBool: true });
        var summary = Slice("summary", static b => b.Get("member_states") is { IsMap: true });
        return new Stuck(list.Get("open")!.Value.AsArray().Single().AsObject(), exchange, list, summary);
    }

    private const long Min = 60_000;

    [Fact]
    public void AnOpenEntryWithNoEtaFactsHasNoLatenessOrStuckFieldsAndIsNotCounted()
    {
        var (model, reader, clock, wall) = Make(new ComposerRegistry().Register(new FixedOpenList(A, B)).Register(new FixedSummary("working", 0)));
        using var owner = model;
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, B)] = true;
        _ = reader.NoFacts.Add(B);
        reader.Activity[A] = CreatedMs;
        Load(model, clock, Canon, A);
        Load(model, clock, Canon, B);
        wall.Now = CreatedMs + 30 * Min;

        var slices = JsonText.Parse(model.Manifest("exchanges")!).Member("slices")!.Value.AsArray().Select(static s => s.Member("body")!.Value.AsObject()).ToList();
        var list = slices.Single(static b => b.Get("open") is { IsArray: true });
        var entries = list.Get("open")!.Value.AsArray().Select(static e => e.AsObject()).ToDictionary(static e => e.Get("id")!.Value.AsString());
        var bare = entries[MessagesLane.SliceIdOf(Canon, B)];

        foreach (var key in new[] { "eta_state", "due_at", "overdue_by", "stuck", "stuck_since", "idle_for" })
        {
            Assert.False(bare.TryGetValue(key, out _), key);
        }

        Assert.True(entries[MessagesLane.SliceIdOf(Canon, A)].Get("stuck")!.Value.AsBool());
        Assert.Equal(1, list.Get("overdue")!.Value.AsNumber());
        Assert.Equal(1, list.Get("stuck")!.Value.AsNumber());
    }

    [Fact]
    public void ATaskWhoseLiveWorkingAssigneeHasBeenQuietForTheStallLimitIsStuckSinceTheLimit()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 19 * Min);

        Assert.True(served.Entry.Get("stuck")!.Value.AsBool());
        Assert.Equal(Iso(CreatedMs + 29 * Min), served.Entry.Get("stuck_since")!.Value.AsString());
        Assert.Equal(11 * 60, served.Entry.Get("idle_for")!.Value.AsNumber());
    }

    [Fact]
    public void ATaskWhoseAssigneeWasActiveInsideTheStallLimitIsNotStuck()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 21 * Min);

        Assert.False(served.Entry.Get("stuck")!.Value.AsBool());
        Assert.False(served.Entry.TryGetValue("stuck_since", out _));
        Assert.False(served.Entry.TryGetValue("idle_for", out _));
    }

    [Fact]
    public void AnAssigneeWithNoRecentToolUseThatSentAMessageInsideTheStallLimitIsNotStuck()
    {
        var spoke = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs, heardMs: CreatedMs + 25 * Min);
        var silent = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs, heardMs: CreatedMs + 15 * Min);

        Assert.False(spoke.Entry.Get("stuck")!.Value.AsBool());
        Assert.True(silent.Entry.Get("stuck")!.Value.AsBool()); // a message older than the limit does not keep it alive
    }

    [Fact]
    public void ActivityFromBeforeTheTaskWasSentDoesNotAgeIt()
    {
        var served = ServedStuck("idle", CreatedMs + 5 * Min, CreatedMs - 35 * Min);

        Assert.False(served.Entry.Get("stuck")!.Value.AsBool());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("gone")]
    [InlineData(null)]
    public void ATaskWhoseAssigneeIsNotServedAsWorkingOrIdleIsNotStuck(string? state) =>
        Assert.False(ServedStuck(state, CreatedMs + 30 * Min, CreatedMs).Entry.Get("stuck")!.Value.AsBool());

    // ah2-242 G1: an assignee idle only for want of an activity record and not live is unknown to the stuck rule, as before C1
    [Fact]
    public void ATaskWhoseAssigneeIsIdleOnlyForWantOfAnActivityRecordIsNotStuck()
    {
        Assert.False(ServedStuck("idle", CreatedMs + 30 * Min, CreatedMs, liveness: "unknown", stuckAsUnknown: true).Entry.Get("stuck")!.Value.AsBool());

        // negative: the same idle assignee without the mark is stuck
        Assert.True(ServedStuck("idle", CreatedMs + 30 * Min, CreatedMs, liveness: "unknown").Entry.Get("stuck")!.Value.AsBool());
    }

    [Fact]
    public void ABlockedAssigneesTaskIsNotStuckAndNeedsYouIsNotCountedTwice()
    {
        var served = ServedStuck("blocked", CreatedMs + 30 * Min, CreatedMs, composedNeedsYou: 1);

        Assert.False(served.Entry.Get("stuck")!.Value.AsBool());
        Assert.Equal(1, served.Summary.Get("needs_you")!.Value.AsNumber());
        Assert.Equal(0, served.Summary.Get("stuck_tasks")!.Value.AsNumber());
    }

    [Fact]
    public void TheListEntryTheExchangeBodyAndTheListCountAgreeOnStuck()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 10 * Min);

        foreach (var key in new[] { "stuck", "stuck_since", "idle_for" })
        {
            Assert.Equal(JsonText.Stringify(served.Exchange.Get(key)!.Value, 0), JsonText.Stringify(served.Entry.Get(key)!.Value, 0));
        }

        Assert.Equal(1, served.List.Get("stuck")!.Value.AsNumber());
    }

    [Fact]
    public void AClosedTeamRaisesNoStuckAlert()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 10 * Min, closedMark: Iso(CreatedMs));

        Assert.Equal(Iso(CreatedMs), served.Summary.Get("closed_at")!.Value.AsString());
        Assert.Empty(served.Summary.Get("alerts")!.Value.AsArray());
    }

    [Fact]
    public void AStuckAlertSpeaksOfTheExchangeNotTheMember()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 10 * Min);

        var stuck = served.Summary.Get("alerts")!.Value.AsArray().Single(static a => a.Member("kind")!.Value.AsString() == "stuck");

        Assert.StartsWith("No word on this exchange for ", stuck.Member("text")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamWhoseMarkerALiveMemberIgnoresKeepsItsStuckAlert()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 10 * Min, closedMark: Iso(CreatedMs), liveness: "live");

        Assert.False(served.Summary.ContainsKey("closed_at"));
        Assert.Contains(served.Summary.Get("alerts")!.Value.AsArray(), static a => a.Member("kind")!.Value.AsString() == "stuck");
    }

    [Fact]
    public void AStuckTaskIsAnOrchestratorAlertAfterTheComposedAlertsAndIsNotCountedInNeedsYou()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 10 * Min, composedNeedsYou: 1);

        Assert.Equal(1, served.Summary.Get("needs_you")!.Value.AsNumber());
        Assert.Equal(1, served.Summary.Get("stuck_tasks")!.Value.AsNumber());
        var alerts = served.Summary.Get("alerts")!.Value.AsArray().Select(static a => a.AsObject()).ToList();
        Assert.Equal(["blocked", "stuck"], alerts.Select(static a => a.Get("kind")!.Value.AsString()));
        Assert.Equal("orchestrator", alerts[1].Get("audience")!.Value.AsString());
        Assert.Null(alerts[0].Get("audience")); // the composed alert stays the user's
        Assert.Equal(Assignee, alerts[1].Get("member")!.Value.AsString());
        Assert.Equal(Iso(CreatedMs + 20 * Min), alerts[1].Get("since")!.Value.AsString());
        Assert.Equal(MessagesLane.SliceIdOf(Canon, A), alerts[1].Get("exchange")!.Value.AsString());
    }

    [Fact]
    public void ATeamWhoseOnlyAlertIsAStuckTaskDoesNotReadNeedsYou()
    {
        var quiet = ServedStuck("working", CreatedMs + 5 * Min, CreatedMs + 4 * Min);
        Assert.Equal("working", quiet.Summary.Get("state")!.Value.AsString());

        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 10 * Min);

        Assert.Equal(1, served.Summary.Get("stuck_tasks")!.Value.AsNumber());
        Assert.Equal(0, served.Summary.Get("needs_you")!.Value.AsNumber());
        Assert.Equal("working", served.Summary.Get("state")!.Value.AsString());
    }

    [Fact]
    public void ARoleAddressedRequestIsStuckOnItsListEntryAndOnItsExchangeBody()
    {
        var served = ServedStuck("working", CreatedMs + 30 * Min, CreatedMs + 10 * Min, roleOnly: true);

        Assert.True(served.Exchange.Get("to_name")!.Value.IsNull);
        Assert.True(served.Entry.Get("stuck")!.Value.AsBool());
        Assert.True(served.Exchange.Get("stuck")!.Value.AsBool());
        Assert.Equal(served.Entry.Get("stuck_since")!.Value.AsString(), served.Exchange.Get("stuck_since")!.Value.AsString());
    }

    [Fact]
    public async Task APollReturnsWhenATaskCrossesTheStuckInstantWithNoInputChange()
    {
        var (model, reader, clock, wall) = Make(new ComposerRegistry().Register(new FixedOpenList(A)).Register(new FixedSummary("working", 0)));
        using var owner = model;
        reader.Open[(Canon, A)] = true;
        reader.Activity[A] = CreatedMs;
        reader.Heard[A] = CreatedMs + 9 * Min; // a word from the assignee counts as activity: the stall limit runs from it
        Load(model, clock, Canon, A);
        var sliceId = MessagesLane.SliceIdOf(Canon, A);
        var at = CreatedMs + 19 * Min;
        wall.Now = at - 500;
        var first = JsonText.Parse(await model.PollAsync(0, "e1", [sliceId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var cursor = (long)first.Member("seq")!.Value.AsNumber();

        var pending = model.PollAsync(cursor, "e1", [sliceId], TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        wall.Now = at + 1;
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(finished == pending, "the poll did not return at the stuck instant");
        Assert.True(JsonText.Parse(await pending).Member("slices")!.Value.AsArray().Single().Member("body")!.Value.AsObject().Get("stuck")!.Value.AsBool());
    }

    /// <summary>Publishes one open list naming the given exchanges, so the serve-time projection of its entries can be read.</summary>
    private sealed class FixedOpenList(params string[] ids) : ISliceComposer
    {
        public string SlicePrefix => "list:open:team";

        public IReadOnlyList<string> Lanes => [MessagesLane.LaneName];

        public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) => [OpenList];

        public IReadOnlyList<string> AllIds(IRecordView records) => [OpenList];

        public SliceChange Compose(string sliceId, IRecordView records)
        {
            var open = new JsonArray();
            foreach (var id in ids)
            {
                open.Add(JsonValue.FromObject(new JsonObject { ["id"] = MessagesLane.SliceIdOf(Canon, id), ["to_member"] = Assignee, ["from_member"] = JsonValue.Null }));
            }

            return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(new JsonObject { ["team"] = "team:d1/tm", ["open"] = open }), 0), 1);
        }
    }

    private static (ReadModel Model, JsonObject List, Dictionary<string, JsonObject> Exchanges) ServedOpenList(params (string Id, bool Open, double Heard)[] exchanges)
    {
        var (model, reader, clock, wall) = Make(new ComposerRegistry().Register(new FixedOpenList([.. exchanges.Select(static e => e.Id)])));
        foreach (var (id, open, heard) in exchanges)
        {
            reader.Open[(Canon, id)] = open;
            reader.Heard[id] = heard;
            Load(model, clock, Canon, id);
        }

        wall.Now = CreatedMs + 400_000;
        var slices = JsonText.Parse(model.Manifest("exchanges")!).Member("slices")!.Value.AsArray().Select(static s => s.Member("body")!.Value.AsObject()).ToList();
        var list = slices.Single(b => b.Get("team") is { IsText: true } t && t.AsString() == "team:d1/tm");
        return (model, list, slices.Where(b => b.Get("id") is { IsText: true }).ToDictionary(static b => MessagesLane.SliceIdOf(Canon, b.Get("id")!.Value.AsString())));
    }

    [Fact]
    public void AnOpenListEntryCarriesTheSameLatenessAsItsExchangeSliceAndTheListCountsTheOverdueEntries()
    {
        var (model, list, exchanges) = ServedOpenList((A, true, 0), (B, true, CreatedMs + 390_000), (C, true, CreatedMs + 150_000));
        using var owner = model;

        var entries = list.Get("open")!.Value.AsArray().Select(static e => e.AsObject()).ToDictionary(static e => e.Get("id")!.Value.AsString());
        var states = new HashSet<string>();
        foreach (var (id, entry) in entries)
        {
            var served = exchanges[id];
            Assert.Equal(served.Get("state")!.Value.AsString(), entry.Get("eta_state")!.Value.AsString());
            Assert.Equal(served.Get("due_at")!.Value.AsString(), entry.Get("due_at")!.Value.AsString());
            Assert.Equal(served.Get("state")!.Value.AsString() == "overdue", entry.TryGetValue("overdue_by", out var by) && by.AsNumber() == served.Get("overdue_by")!.Value.AsNumber());
            _ = states.Add(entry.Get("eta_state")!.Value.AsString());
        }

        Assert.Equal(["due-soon", "on-time", "overdue"], states.Order(StringComparer.Ordinal));
        Assert.Equal(entries.Values.Count(static e => e.Get("eta_state")!.Value.AsString() == "overdue"), (int)list.Get("overdue")!.Value.AsNumber());
        Assert.Equal(1, (int)list.Get("overdue")!.Value.AsNumber());
    }

    private static JobRow JobRowAt(string id, double sentAtMs, double? endedAtMs = null) =>
        new(id, "impl", "test", "dotnet test", 4242, "small", 300, sentAtMs, null, null, endedAtMs, false);

    /// <summary>The served open-team list of a team whose jobs slice is held and holds <paramref name="jobs"/>, with the wall clock 400 s after the exchanges were created.</summary>
    private static (ReadModel Model, JsonObject List, IDisposable Lease, Clock Wall) ServedOpenListWithJobs(Func<double, IReadOnlyList<JobRow>> jobs, params (string Id, bool Open, double Heard)[] exchanges) =>
        ServedOpenListWithJobs(jobs, true, exchanges);

    private static (ReadModel Model, JsonObject List, IDisposable Lease, Clock Wall) ServedOpenListWithJobs(Func<double, IReadOnlyList<JobRow>> jobs, bool holdSlice, params (string Id, bool Open, double Heard)[] exchanges)
    {
        var reader = new Reader();
        var clock = new Clock();
        var wall = new Clock { Now = CreatedMs };
        ReadModel? model = null;
        var canonical = new[] { Canon, Other };
        var lane = new MessagesLane(reader, () => canonical, () => canonical, rereadSelf: key => model!.Emit(MessagesLane.LaneName, key));
        var jobsLane = new JobsLane(() => canonical, id => model!.Leases.IsSubscribed(id), (_, _, now) => jobs(now));
        var composers = new ComposerRegistry().Register(new FixedOpenList([.. exchanges.Select(static e => e.Id)]));
        model = new ReadModel([lane, jobsLane], new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, MessageIdOf, null, null, Peer), composers) { WallClockMs = wall.Read };
        lanes = lane;
        foreach (var (id, open, heard) in exchanges)
        {
            reader.Open[(Canon, id)] = open;
            reader.Heard[id] = heard;
            Load(model, clock, Canon, id);
        }

        var lease = holdSlice ? model.Leases.Hold(["jobs:" + TeamKey]) : model.Leases.Hold([]);
        if (holdSlice)
        {
            model.Emit(JobsLane.LaneName, ProgressLane.KeyOf(Canon, "tm"));
        }

        Drain(model, clock);
        wall.Now = CreatedMs + 400_000;
        var slices = JsonText.Parse(model.Manifest("exchanges")!).Member("slices")!.Value.AsArray().Select(static s => s.Member("body")!.Value.AsObject()).ToList();
        return (model, slices.Single(b => b.Get("team") is { IsText: true } t && t.AsString() == "team:d1/tm"), lease, wall);
    }

    private const double ServedAt = 400_000;

    [Theory]
    [InlineData("overdue-job", 1)]
    [InlineData("not-yet-due-job", 0)]
    [InlineData("ended-job", 0)]
    public void TheTeamOverdueCountIncludesAnOpenJobPastItsDueTimeAndNotAFreshOrAnEndedOne(string kind, int expected)
    {
        var served = CreatedMs + ServedAt;
        var (model, list, lease, _) = ServedOpenListWithJobs(
            _ => kind switch
            {
                "overdue-job" => [JobRowAt("j", served - 1_000_000)],
                "not-yet-due-job" => [JobRowAt("j", served - 60_000)],
                _ => [JobRowAt("j", served - 1_000_000, endedAtMs: 4_999_000)],
            },
            (B, true, CreatedMs + 390_000));
        using var owner = model;
        using var held = lease;

        Assert.Equal(expected, (int)list.Get("overdue")!.Value.AsNumber());
    }

    [Fact]
    public void AnOverdueDispatchAndAnOverdueJobBothCountInTheTeamOverdueCount()
    {
        var served = CreatedMs + ServedAt;
        var (model, list, lease, _) = ServedOpenListWithJobs(_ => [JobRowAt("j", served - 1_000_000)], (A, true, 0));
        using var owner = model;
        using var held = lease;

        Assert.Equal(2, (int)list.Get("overdue")!.Value.AsNumber());
    }

    /// <summary>The served state of member <c>cd</c> whose summary says <paramref name="liveness"/> and whose team has the one job <paramref name="job"/>; no client holds the jobs slice.</summary>
    private static string ServedMemberState(string liveness, JobRow job, string state = "working")
    {
        var reader = new Reader();
        var clock = new Clock();
        var wall = new Clock { Now = CreatedMs };
        ReadModel? model = null;
        var canonical = new[] { Canon, Other };
        var lane = new MessagesLane(reader, () => canonical, () => canonical, rereadSelf: key => model!.Emit(MessagesLane.LaneName, key));
        var jobsLane = new JobsLane(() => canonical, id => model!.Leases.IsSubscribed(id), (_, _, _) => [job]);
        var composers = new ComposerRegistry().Register(new FixedOpenList(A)).Register(new FixedSummary(state, 0, liveness));
        model = new ReadModel([lane, jobsLane], new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, MessageIdOf, null, null, Peer), composers) { WallClockMs = wall.Read };
        using var owner = model;
        reader.Open[(Canon, A)] = true;
        reader.Heard[A] = CreatedMs;
        Load(model, clock, Canon, A);
        wall.Now = CreatedMs + 400_000;
        var summary = JsonText.Parse(model.Manifest("summary")!).Member("slices")!.Value.AsArray().Select(static s => s.Member("body")!.Value.AsObject()).Single(static b => b.Get("member_states") is { IsMap: true });
        return summary.Get("member_states")!.Value.AsObject().Get(Assignee)!.Value.AsObject().Str("state")!;
    }

    [Theory]
    [InlineData("live", "cd", 1_000_000, null, "blocked-on-job")]
    [InlineData("live", "cd", 60_000, null, "working")]
    [InlineData("live", "cd", 1_000_000, 4_999_000.0, "working")]
    [InlineData("live", "impl", 1_000_000, null, "working")]
    [InlineData("proven_dead", "cd", 1_000_000, null, "working")]
    public void AMemberIsServedBlockedOnJobOnlyWhileLiveAndOwningAnOverdueOpenJob(string liveness, string owner, long ageMs, double? endedAtMs, string expected)
    {
        var served = CreatedMs + 400_000;
        var job = JobRowAt("j", served - ageMs, endedAtMs) with { Owner = owner };

        Assert.Equal(expected, ServedMemberState(liveness, job));
    }

    /// <summary>ah2-475 J4: a live member blocked or failed that also owns an overdue job keeps its state; a working one becomes blocked-on-job.</summary>
    [Theory]
    [InlineData("blocked", "blocked")]
    [InlineData("failed", "failed")]
    [InlineData("working", "blocked-on-job")]
    [InlineData("idle", "blocked-on-job")]
    public void ABlockedOrFailedMemberStaysSoWhenItAlsoOwnsAnOverdueJob(string state, string expected)
    {
        var job = JobRowAt("j", CreatedMs + 400_000 - 1_000_000, null) with { Owner = "cd" };

        Assert.Equal(expected, ServedMemberState("live", job, state));
    }

    [Theory]
    [InlineData("overdue-job", 1)]
    [InlineData("not-yet-due-job", 0)]
    public void TheTeamOverdueCountComesFromTheJobRecordsWhenNoClientHoldsTheJobsSlice(string kind, int expected)
    {
        var served = CreatedMs + ServedAt;
        var (model, list, lease, _) = ServedOpenListWithJobs(
            _ => [JobRowAt("j", kind == "overdue-job" ? served - 1_000_000 : served - 60_000)],
            holdSlice: false,
            (B, true, CreatedMs + 390_000));
        using var owner = model;
        using var held = lease;

        Assert.False(model.Store.Current.Slices.ContainsKey("jobs:" + TeamKey));
        Assert.Equal(expected, (int)list.Get("overdue")!.Value.AsNumber());
    }

    [Fact]
    public async Task OnePollReadsATeamsJobFilesOnceHoweverManyPartsOfTheResponseNeedThem()
    {
        var reads = 0;
        var served = CreatedMs + ServedAt;
        var (model, _, lease, wall) = ServedOpenListWithJobs(
            now =>
            {
                reads++;
                return [JobRowAt("j", served - 1_000_000)];
            },
            holdSlice: false,
            (B, true, CreatedMs + 390_000));
        using var owner = model;
        using var held = lease;
        reads = 0;
        wall.Now = (long)served + 1;

        _ = await model.PollAsync(0, "e1", [OpenList], TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(1, reads);
    }

    [Fact]
    public void TheUnnamedTeamsOpenListReadsTheJobsOfItsDefaultProgressFolderAndNoOtherTeamsJobs()
    {
        var folders = new List<string>();
        var lane = new JobsLane(() => [Canon], _ => false, (_, folder, _) =>
        {
            folders.Add(folder);
            return [JobRowAt("j", 0)];
        });

        Assert.NotNull(lane.BodyOfOpenTeam(ActivityLane.DirId(Canon) + "/default", 1));
        Assert.NotNull(lane.BodyOfOpenTeam(ActivityLane.DirId(Canon) + "/tm", 1));
        Assert.Null(lane.BodyOfOpenTeam(ActivityLane.DirId(Other) + "/tm", 1));
        Assert.Null(lane.BodyOfOpenTeam("nodir/tm", 1));
        Assert.Equal(["_default", "tm"], folders);
        Assert.Equal("d1/default", JobsLane.OpenTeamSuffixOf("d1/_default"));
        Assert.Equal("d1/tm", JobsLane.OpenTeamSuffixOf("d1/tm"));
    }

    [Fact]
    public void HeldJobRowsAreReadAgainOnlyWhenTheWriteCounterMovesAndTheClockStillCountsOverdueOnThem()
    {
        var version = 1L;
        var reads = 0;
        IReadOnlyList<JobRow> rows = [JobRowAt("j", 0)];
        var lane = new JobsLane(() => [Canon], _ => false, (_, _, _) =>
        {
            reads++;
            return rows;
        }, () => version);
        var team = ActivityLane.DirId(Canon) + "/tm";
        int Overdue(double now) => JobProjection.OverdueCount(lane.BodyOfOpenTeam(team, now)!, now, EtaProjection.DefaultPollMs);

        Assert.Equal(0, Overdue(1));
        rows = [JobRowAt("j", 0), JobRowAt("k", 0)];

        // the counter has not moved: the held row set stands, yet its 300 s threshold passing is counted at read time
        Assert.Equal(1, Overdue(400_000));
        version = 2;
        Assert.Equal(2, Overdue(400_000));
        Assert.Equal(2, reads);
        lane.Forget();
        _ = Overdue(400_000);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task APollHoldingOnlyTheOpenListReturnsTheListWithTheNewCountWhenAJobCrossesItsDueTime()
    {
        var sent = CreatedMs + ServedAt - 100_000;
        var (model, _, lease, wall) = ServedOpenListWithJobs(_ => [JobRowAt("j", sent)], holdSlice: false, (B, true, CreatedMs + 390_000));
        using var owner = model;
        using var held = lease;
        var row = $"{{\"rows\":[{{\"id\":\"j\",\"eta\":\"small\",\"threshold_s\":300,\"sent_at\":\"{Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)sent))}\",\"extended_at\":null,\"ended_at\":null}}]}}";
        var dueSoon = JobProjection.NextChange(JsonText.Parse(row).AsObject(), sent + 1000, EtaProjection.DefaultPollMs)!.Value;
        var at = JobProjection.NextChange(JsonText.Parse(row).AsObject(), dueSoon + 1, EtaProjection.DefaultPollMs)!.Value;
        wall.Now = (long)at - 500;
        var first = JsonText.Parse(await model.PollAsync(0, "e1", [OpenList], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var cursor = (long)first.Member("seq")!.Value.AsNumber();

        var pending = model.PollAsync(cursor, "e1", [OpenList], TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        wall.Now = (long)at + 1;
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(finished == pending, "the poll did not return at the job's due time");
        var slices = JsonText.Parse(await pending).Member("slices")!.Value.AsArray().Select(static s => s.AsObject()).ToList();
        var list = slices.Single(s => s.Str("id") == OpenList).Get("body")!.Value.AsObject();
        Assert.Equal(1, (int)list.Get("overdue")!.Value.AsNumber());
    }

    [Fact]
    public async Task APollHoldingTheJobsSliceAndTheOpenListReturnsTheListWithTheNewCountWhenAJobCrossesItsDueTime()
    {
        var sent = CreatedMs + ServedAt - 100_000;
        var (model, _, lease, wall) = ServedOpenListWithJobs(_ => [JobRowAt("j", sent)], (B, true, CreatedMs + 390_000));
        using var owner = model;
        using var held = lease;
        var jobsId = "jobs:" + TeamKey;
        var row = $"{{\"rows\":[{{\"id\":\"j\",\"eta\":\"small\",\"threshold_s\":300,\"sent_at\":\"{Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)sent))}\",\"extended_at\":null,\"ended_at\":null}}]}}";
        var dueSoon = JobProjection.NextChange(JsonText.Parse(row).AsObject(), sent + 1000, EtaProjection.DefaultPollMs)!.Value;
        var at = JobProjection.NextChange(JsonText.Parse(row).AsObject(), dueSoon + 1, EtaProjection.DefaultPollMs)!.Value;
        wall.Now = (long)at - 500;
        var first = JsonText.Parse(await model.PollAsync(0, "e1", [OpenList, jobsId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var cursor = (long)first.Member("seq")!.Value.AsNumber();

        var pending = model.PollAsync(cursor, "e1", [OpenList, jobsId], TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        wall.Now = (long)at + 1;
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(finished == pending, "the poll did not return at the job's due time");
        var slices = JsonText.Parse(await pending).Member("slices")!.Value.AsArray().Select(static s => s.AsObject()).ToList();
        var list = slices.Single(s => s.Str("id") == OpenList).Get("body")!.Value.AsObject();
        Assert.Equal(1, (int)list.Get("overdue")!.Value.AsNumber());
    }

    [Fact]
    public void AnOpenListEntryWithNoEtaHasNoLatenessFieldsAndIsNotCounted()
    {
        var (model, list, _) = ServedOpenList((A, true, 0), (C, false, 0));
        using var owner = model;

        var closed = list.Get("open")!.Value.AsArray().Select(static e => e.AsObject()).Single(e => e.Get("id")!.Value.AsString() == MessagesLane.SliceIdOf(Canon, C));
        Assert.False(closed.TryGetValue("eta_state", out _));
        Assert.False(closed.TryGetValue("due_at", out _));
        Assert.False(closed.TryGetValue("overdue_by", out _));
        Assert.Equal(1, (int)list.Get("overdue")!.Value.AsNumber());
    }

    [Fact]
    public void AnOpenDispatchSliceCarriesFileFactsAndNoClockStateAndAnAnsweredOneCarriesNoFacts()
    {
        var (model, reader, clock, _) = Make();
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, C)] = false;

        Load(model, clock, Canon, A);
        Load(model, clock, Canon, C);

        var open = Body(model, Canon, A);
        Assert.True(open.Get("eta_facts")!.Value.IsMap);
        Assert.Equal("small", open.Get("eta")!.Value.AsString());
        Assert.False(open.TryGetValue("state", out _));
        Assert.False(open.TryGetValue("overdue_by", out _));
        Assert.True(Body(model, Canon, C).Get("eta_facts")!.Value.IsNull);
        Assert.Equal(CreatedMs, model.Store.Current.Slices[MessagesLane.SliceIdOf(Canon, A)].AsOfMs);
    }

    [Fact]
    public void AGateLogNoticeRereadsTheOpenRequestsOfThatDirectoryOnly()
    {
        var (model, reader, clock, _) = Make();
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, B)] = true;
        reader.Open[(Canon, C)] = false;
        reader.Open[(Other, D)] = true;
        foreach (var (dir, id) in reader.Open.Keys.ToList())
        {
            Load(model, clock, dir, id);
        }

        reader.Reads.Clear();
        model.Emit(MessagesLane.LaneName, WatchModule.GatesKey(Canon));
        Drain(model, clock);

        Assert.Equal(1, reader.Reads.GetValueOrDefault(A));
        Assert.Equal(1, reader.Reads.GetValueOrDefault(B));
        Assert.Equal(0, reader.Reads.GetValueOrDefault(C));
        Assert.Equal(0, reader.Reads.GetValueOrDefault(D));
    }

    [Fact]
    public void AGateLogNoticeServesItsSoftGateRowsAndNoOtherRow()
    {
        var (model, reader, clock, _) = Make();
        reader.Gates =
        [
            JsonValue.FromObject(new JsonObject { ["type"] = "soft-gate", ["rule"] = "conduit", ["outcome"] = "bounce", ["reason"] = "r", ["ts"] = "t" }),
            JsonValue.FromObject(new JsonObject { ["type"] = "route", ["session_id"] = "s" }),
        ];

        model.Emit(MessagesLane.LaneName, WatchModule.GatesKey(Canon));
        Drain(model, clock);

        var row = Assert.Single(JsonText.Parse(model.Store.Current.Slices[MessagesLane.SoftGatesSliceOf(Canon)].Body).AsObject().Get("rows")!.Value.AsArray()).AsObject();
        Assert.Equal("conduit", row.Get("rule")!.Value.AsString());
        Assert.Equal("bounce", row.Get("outcome")!.Value.AsString());
        Assert.Equal("r", row.Get("reason")!.Value.AsString());
    }

    [Fact]
    public void NegativeAGatesFileEventRereadsNothingSinceTheLogIsATable()
    {
        var (model, reader, clock, _) = Make();
        reader.Open[(Canon, A)] = true;
        Load(model, clock, Canon, A);
        reader.Reads.Clear();

        model.Hint(Canon + "/gates.jsonl");
        model.Hint(Canon + "/gates.jsonl.tmp");
        model.Hint("/elsewhere/.claude/ah2/gates.jsonl");
        Drain(model, clock);

        Assert.Empty(reader.Reads);
    }

    [Fact]
    public void TheServedStateMovesFromOnTimeToDueSoonToOverdueAsTheClockMovesWithNoRecompose()
    {
        var (model, reader, clock, wall) = Make();
        reader.Open[(Canon, A)] = true;
        Load(model, clock, Canon, A);
        var version = model.Store.Current.Slices[MessagesLane.SliceIdOf(Canon, A)].Version;
        var due = CreatedMs + 300_000;

        Assert.Equal("on-time", StateAt(model, wall, due - 200_000, Canon, A));
        Assert.Equal("due-soon", StateAt(model, wall, due - 30_000, Canon, A));
        Assert.Equal("overdue", StateAt(model, wall, due, Canon, A));
        Assert.Equal(version, model.Store.Current.Slices[MessagesLane.SliceIdOf(Canon, A)].Version);
        Assert.False(Body(model, Canon, A).TryGetValue("state", out _));
    }

    [Fact]
    public void TheTeamOverdueCountCountsOnlyOpenDispatchesPastDue()
    {
        var (model, reader, clock, _) = Make();
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, C)] = false;
        Load(model, clock, Canon, A);
        Load(model, clock, Canon, C);
        var bodies = new[] { Body(model, Canon, A), Body(model, Canon, C) };

        Assert.Equal(0, EtaProjection.OverdueCount(bodies, CreatedMs + 100_000, 15_000));
        Assert.Equal(1, EtaProjection.OverdueCount(bodies, CreatedMs + 400_000, 15_000));
    }

    private sealed class RecordingJobs : Ah.Abstractions.IJobRegistry
    {
        public List<string> Names { get; } = [];

        public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler) => Names.Add(name);
    }

    private sealed class NoHost : IStallHost
    {
        public bool Owner() => true;

        public IReadOnlyList<StallTeam> Teams() => [];

        public IReadOnlyList<StallMember> Members(StallTeam team) => [];

        public StallJudgement Judge(StallTeam team, StallMember member) => throw new InvalidOperationException();

        public IReadOnlyList<string> OpenWork(StallTeam team, StallMember member) => [];

        public void Mail(WatchMail mail)
        {
        }

        public void Journal(JsonObject row)
        {
        }

        public string? ReportedSession(string key) => null;

        public void BeginSweep()
        {
        }

        public void ForgetStaleReported(TimeSpan unseenFor)
        {
        }

        public void SetReportedSession(string key, string? session)
        {
        }
    }

    [Fact]
    public void TheEtaWorkAddsNoDaemonJobBeyondTheOneStallWatchJob()
    {
        var jobs = new RecordingJobs();

        WatchModule.RegisterStallWatch(jobs, new NoHost(), StallSettings.Defaults, () => DateTimeOffset.UnixEpoch);

        Assert.Equal(["stall-watch"], jobs.Names);
        Assert.DoesNotContain(typeof(MessagesLane).GetConstructors().SelectMany(static c => c.GetParameters()), static p => p.ParameterType == typeof(Ah.Abstractions.IJobRegistry));
        Assert.DoesNotContain(typeof(ReadModel).GetConstructors().SelectMany(static c => c.GetParameters()), static p => p.ParameterType == typeof(Ah.Abstractions.IJobRegistry));
    }

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void AHeardRowRereadsOnlyTheRequestWhoseAddresseeWasHeardAndTheServedStateLeavesOverdue()
    {
        var (model, reader, clock, wall) = Make();
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, B)] = true;
        Load(model, clock, Canon, A);
        Load(model, clock, Canon, B);
        var late = CreatedMs + 400_000;
        Assert.Equal("overdue", StateAt(model, wall, late, Canon, A));
        reader.Reads.Clear();

        reader.Heard[A] = CreatedMs + 390_000;
        model.Hint(Peer);
        Drain(model, clock);

        Assert.Equal(1, reader.Reads.GetValueOrDefault(A));
        Assert.Equal(0, reader.Reads.GetValueOrDefault(B));
        Assert.NotEqual("overdue", StateAt(model, wall, late, Canon, A));
        Assert.Equal("overdue", StateAt(model, wall, late, Canon, B));
    }

    [Fact]
    public void APeerFileChangeWithNoNewHeardRowRereadsNothing()
    {
        var (model, reader, clock, _) = Make();
        reader.Open[(Canon, A)] = true;
        Load(model, clock, Canon, A);
        reader.Reads.Clear();

        model.Hint(Peer);
        Drain(model, clock);

        Assert.Empty(reader.Reads);
    }

    [Fact]
    public void AnActivityChangeOfTheAddresseeRereadsItsRequestsAndNoOthers()
    {
        var (model, reader, clock, _) = Make();
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, B)] = true;
        Load(model, clock, Canon, A);
        Load(model, clock, Canon, B);
        reader.Reads.Clear();

        lanes!.RereadSession(Canon, "s-" + A);
        Drain(model, clock);

        Assert.Equal(1, reader.Reads.GetValueOrDefault(A));
        Assert.Equal(0, reader.Reads.GetValueOrDefault(B));
    }

    private static (string Hold, string Free) Next(double busyAt, double now)
    {
        var (model, reader, clock, _) = Make();
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, B)] = true;
        reader.Busy[A] = busyAt;
        Load(model, clock, Canon, A);
        Load(model, clock, Canon, B);
        var held = EtaProjection.Of(Body(model, Canon, A), now, 15_000)!;
        var free = EtaProjection.Of(Body(model, Canon, B), now, 15_000)!;
        return (held.Get("next_nudge")!.Value.AsObject().Get("at")!.Value.AsString(), free.Get("next_nudge")!.Value.AsObject().Get("at")!.Value.AsString());
    }

    [Fact]
    public void AFreshWorkingWriteDefersTheCheckInUntilTwoMinutesAfterItAndAnIdleAddresseeIsNotDeferred()
    {
        var due = CreatedMs + 300_000;

        var (held, free) = Next(due - 30_000, due);

        Assert.Equal(Iso(due + 90_000), held);
        Assert.Equal(Iso(due), free);
    }

    [Fact]
    public void AStaleWorkingWriteGivesNoHold()
    {
        var due = CreatedMs + 300_000;

        var (stale, _) = Next(due - 130_000, due);
        var (justFresh, _) = Next(due - 119_000, due);

        Assert.Equal(Iso(due), stale);
        Assert.Equal(Iso(due + 1_000), justFresh);
    }

    [Fact]
    public void ContinuousWorkDefersTheCheckInOnlyUpToThreeThresholdsAfterTheBase()
    {
        var now = CreatedMs + 1_200_000;

        var (held, _) = Next(now - 10_000, now);

        Assert.Equal(Iso(CreatedMs + 900_000), held);
    }

    [Fact]
    public void AFirstLoadCarriesTheServedStateAsTheDeltaDoes()
    {
        var (model, reader, clock, wall) = Make();
        reader.Open[(Canon, A)] = true;
        reader.Open[(Canon, C)] = false;
        Load(model, clock, Canon, A);
        Load(model, clock, Canon, C);
        wall.Now = CreatedMs + 400_000;

        var manifest = JsonText.Parse(model.Manifest("exchanges")!).Member("slices")!.Value.AsArray().Select(static s => s.Member("body")!.Value.AsObject()).ToList();

        Assert.Equal("overdue", manifest.Single(b => b.Get("id")!.Value.AsString() == A).Get("state")!.Value.AsString());
        Assert.False(manifest.Single(b => b.Get("id")!.Value.AsString() == C).TryGetValue("state", out _));
    }

    [Fact]
    public async Task APollWaitsNoLongerThanTheNextTimeChangeAndThenSendsTheReprojectedSliceWithNoPublish()
    {
        var (model, reader, clock, wall) = Make();
        reader.Open[(Canon, A)] = true;
        Load(model, clock, Canon, A);
        var sliceId = MessagesLane.SliceIdOf(Canon, A);
        var due = CreatedMs + 300_000;
        wall.Now = due - 500;
        var first = JsonText.Parse(await model.PollAsync(0, "e1", [sliceId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var cursor = (long)first.Member("seq")!.Value.AsNumber();
        var publish = model.Store.Current.Publish;

        var pending = model.PollAsync(cursor, "e1", [sliceId], TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        wall.Now = due + 1;
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(finished == pending, "the poll did not return at the next time change");
        var slice = JsonText.Parse(await pending).Member("slices")!.Value.AsArray().Single();
        Assert.Equal("overdue", slice.Member("body")!.Value.AsObject().Get("state")!.Value.AsString());
        Assert.Equal(publish, model.Store.Current.Publish);
    }

    [Fact]
    public async Task ACursorWithAServedTimeGetsNoReprojectionOfAnUnchangedSliceAndAStaleCursorGetsOne()
    {
        var (model, reader, clock, wall) = Make();
        reader.Open[(Canon, A)] = true;
        Load(model, clock, Canon, A);
        var sliceId = MessagesLane.SliceIdOf(Canon, A);
        wall.Now = CreatedMs + 10_000;
        var first = JsonText.Parse(await model.PollAsync(0, "e1", [sliceId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var cursor = (long)first.Member("seq")!.Value.AsNumber();
        wall.Now = CreatedMs + 20_000;

        var current = JsonText.Parse(await model.PollAsync(cursor, "e1", [sliceId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var stale = JsonText.Parse(await model.PollAsync(model.Store.Current.Publish, "e1", [sliceId], TimeSpan.Zero, TestContext.Current.CancellationToken));

        Assert.Empty(current.Member("slices")!.Value.AsArray());
        Assert.Single(stale.Member("slices")!.Value.AsArray());
    }
}
