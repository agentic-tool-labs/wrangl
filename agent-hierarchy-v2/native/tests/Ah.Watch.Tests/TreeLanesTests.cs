using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The Tree's lanes and composers over a fake state reader: slice bodies, projection equals rebuild per slice type, read-count ceilings, zero reads on the request path, and the path table rows.</summary>
public sealed partial class TreeLanesTests
{
    private const string Canon = "/repo/.claude/ah2";
    private const string Home = "/home/ah2";
    private const string TeamPath = Canon + "/team.json";
    private const string AliasPath = Canon + "/teams/rel.json";
    private const string KnownDirsFile = "/user/.claude/ah2/agent-hierarchy.known-dirs.json";

    /// <summary>The reads one event for one key costs. Each is a checked-in constant asserted as equality.</summary>
    private const int ReadsPerRegistrationEvent = 1;
    private const int ReadsPerTeamEvent = 1;
    private const int ReadsPerGrantsEvent = 1;
    private const int ReadsPerDecisionsEvent = 1;
    private const int ReadsPerKnownDirsEvent = 1;
    private const int BytesPerEventCeiling = 600;

    private sealed class TreeReader : IViewStateReader
    {
        private readonly object _gate = new();

        public Dictionary<string, string> Teams { get; } = [];

        public Dictionary<string, (string Json, string[] Files)> Registrations { get; } = [];

        public Dictionary<(string Dir, string Team), string> Grants { get; } = [];

        public Dictionary<(string Dir, string Run), string[]> Decisions { get; } = [];

        public List<string> KnownDirs { get; } = [];

        public Dictionary<string, string> Exchanges { get; } = [];

        public Dictionary<string, string> Statuses { get; } = [];

        public Dictionary<string, string> Activities { get; } = [];

        public List<long> LivePids { get; } = [];

        /// <summary>The newest peers.jsonl row per member, as <c>peers.latest</c> returns them.</summary>
        public List<string> PeerRows { get; } = [];

        /// <summary>Every pid a session recorded: the running ones, and each registration's pid whether or not it still runs.</summary>
        private IEnumerable<long> RecordedPids() =>
            LivePids.Concat(Registrations.Values.Select(r => JsonText.Parse(r.Json).Member("pid") is { IsNumber: true } p ? (long)p.AsNumber() : 0L).Where(static p => p > 0)).Distinct();

        public Dictionary<string, int> Calls { get; } = [];

        public long Bytes { get; set; }

        public int Total
        {
            get
            {
                lock (_gate)
                {
                    return Calls.Values.Sum();
                }
            }
        }

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            lock (_gate)
            {
                var name = domain + "." + op;
                Calls[name] = Calls.GetValueOrDefault(name) + 1;
                switch (name)
                {
                    case "team.keys":
                        return Obj("teams", Array(Teams.Keys.Where(p => p.StartsWith(hierarchyDir + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(p => "{\"name\":null,\"path\":\"" + p + "\"}"), raw: true));
                    case "team.read":
                        return Counted(Teams.TryGetValue(args.Get("path")!.Value.AsString(), out var team) ? team : null);
                    case "mailbox.keys":
                        return Obj("sessions", Array(Registrations.Keys.Order(StringComparer.Ordinal)));
                    case "mailbox.registration":
                        if (!Registrations.TryGetValue(args.Get("session_id")!.Value.AsString(), out var reg))
                        {
                            return JsonValue.Null;
                        }

                        Bytes += reg.Json.Length;
                        return JsonText.Parse("{\"registration\":" + reg.Json + ",\"team_files\":[" + string.Join(",", reg.Files.Select(f => "\"" + f + "\"")) + "]}");
                    case "worktree-grants.keys":
                        return Obj("teams", Array(Grants.Keys.Where(k => k.Dir == hierarchyDir).Select(static k => k.Team).Order(StringComparer.Ordinal)));
                    case "worktree-grants.status":
                        return Counted(Grants.TryGetValue((hierarchyDir, args.Get("team")!.Value.AsString()), out var grants) ? grants : "{\"present\":false}");
                    case "decisions.keys":
                        return Obj("runs", Array(Decisions.Keys.Where(k => k.Dir == hierarchyDir).Select(static k => k.Run).Order(StringComparer.Ordinal)));
                    case "decisions.read":
                        var path = args.Get("path")!.Value.AsString();
                        var key = Decisions.Keys.FirstOrDefault(k => path == k.Dir + "/pipeline/" + k.Run + "/decisions.jsonl");
                        var rows = key == default ? [] : Decisions[key];
                        var json = "{\"decisions\":[" + string.Join(",", rows.Select(t => t.StartsWith('{') ? t : "{\"kind\":\"decided\",\"time\":\"" + t + "\"}")) + "],\"skipped\":0}";
                        Bytes += json.Length;
                        return JsonText.Parse(json);
                    case "msgs.keys":
                        return Obj("ids", Array(Exchanges.Keys.OrderByDescending(static i => i, StringComparer.Ordinal)));
                    case "msgs.read":
                        return Counted(Exchanges.TryGetValue(args.Get("id")!.Value.AsString(), out var exchange) ? exchange : null);
                    case "status.read":
                        return Counted(Statuses.TryGetValue(hierarchyDir, out var status) ? status : null);
                    case "activity.keys":
                        return Obj("subjects", Array(Activities.Keys.Order(StringComparer.Ordinal)));
                    case "activity.read":
                        return Counted(Activities.TryGetValue(args.Get("subject")!.Value.AsString(), out var activity) ? activity : null);
                    case "session-pids.pids":
                        return JsonText.Parse("{\"sessions\":[" + string.Join(",", RecordedPids().Select(p => "{\"pid\":" + p + "}")) + "]}");
                    case "peers.latest":
                        return Counted("[" + string.Join(",", PeerRows) + "]");
                    case "known-dirs.read":
                        return Counted("{\"dirs\":[" + string.Join(",", KnownDirs.Select(d => "\"" + d + "\"")) + "]}");
                    default:
                        throw new InvalidOperationException("unexpected read " + name);
                }
            }
        }

        private JsonValue Counted(string? json)
        {
            if (json is null)
            {
                return JsonValue.Null;
            }

            Bytes += json.Length;
            return JsonText.Parse(json);
        }

        private static JsonValue Obj(string name, JsonArray array) => JsonValue.FromObject(new JsonObject { [name] = JsonValue.FromArray(array) });

        private static JsonArray Array(IEnumerable<string> items, bool raw = false)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                array.Add(raw ? JsonText.Parse(item) : item);
            }

            return array;
        }
    }

    private sealed class Clock
    {
        public long Now { get; set; } = 5_000_000;

        public long Read() => Now;
    }

    private static string D => ActivityLane.DirId(Canon);

    private static string Doc(string orchestrator = "so", params string[] members) =>
        "{\"team_id\":\"t1\",\"created\":\"2026-06-01T10:00:00Z\",\"orchestrator\":{\"session_id\":\"" + orchestrator + "\",\"pid\":1,\"pid_start\":\"o1\"},\"members\":["
        + string.Join(",", members.Select(m => "{\"name\":\"" + m + "\",\"role\":\"" + (m == "dev" ? "developer" : "tester") + "\",\"transport_id\":\"p-" + m + "\",\"stream\":null}")) + "]}";

    private static string Reg(string session, string address, int pid, string at) =>
        "{\"session_id\":\"" + session + "\",\"address\":\"" + address + "\",\"addresses\":[\"" + address + "\"],\"pid\":" + pid + ",\"pid_start\":\"ps-" + pid + "\",\"at\":\"" + at + "\"}";

    private static string Grant(string mode, params string[] members) =>
        "{\"present\":true,\"mode\":\"" + mode + "\",\"members\":{" + string.Join(",", members.Select(m => "\"" + m + "\":{\"work_root\":\"/wt/" + m + "\",\"stream\":\"s1\",\"role\":\"developer\"}")) + "}}";

    private static TreeReader Truth()
    {
        var reader = new TreeReader();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa");
        reader.Registrations["s-dev"] = (Reg("s-dev", "dev", 42, "2026-06-01T10:05:00Z"), [TeamPath]);
        reader.Registrations["so"] = (Reg("so", "orchestrator@default", 1, "2026-06-01T10:00:30Z"), [TeamPath]);
        reader.Grants[(Canon, "default")] = Grant("enforce", "dev");
        reader.Decisions[(Canon, "run1")] = ["2026-06-01T10:10:00Z", "2026-06-01T10:20:00Z"];
        reader.KnownDirs.Add(Canon);
        return reader;
    }

    private static (ReadModel Model, Clock Clock) Make(TreeReader reader)
    {
        var clock = new Clock();
        string[] canonical = [Canon];
        var lanes = TreeModel.Lanes(reader, () => canonical, Home).Append(new MessagesLane(reader, () => canonical)).Append(new StatusLane(reader, () => canonical)).ToList();
        var model = new ReadModel(lanes, new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, home: Home), TreeModel.Composers(static () => FixtureNowMs));
        return (model, clock);
    }

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

        throw new InvalidOperationException($"the projector did not settle (faults {model.Counters.ProjectorFaults}, last {model.Counters.LastFault}, retries {model.Counters.RereadRetries})");
    }

    private static (ReadModel Model, Clock Clock) Seeded(TreeReader reader)
    {
        var (model, clock) = Make(reader);
        foreach (var lane in new[] { "teams", "worktrees", "decisions", "registrations", "known-dirs", "messages" })
        {
            model.EmitKeySet(lane);
        }

        Drain(model, clock);
        return (model, clock);
    }

    private static Dictionary<string, string> Projected(ReadModel model) =>
        model.Store.Current.Slices.ToDictionary(static kv => kv.Key, static kv => Encoding.UTF8.GetString(kv.Value.Body) + "@" + kv.Value.AsOfMs, StringComparer.Ordinal);

    private static void AssertEqualsRebuild(ReadModel model, TreeReader reader)
    {
        var (fresh, clock) = Seeded(reader);
        using var owner = fresh;
        var built = Projected(fresh);
        var projected = Projected(model);
        Assert.Equal(built.Keys.Order(StringComparer.Ordinal), projected.Keys.Order(StringComparer.Ordinal));
        foreach (var (id, body) in built)
        {
            Assert.Equal(body, projected[id]);
        }

        Assert.Equal(
            Projected(fresh).OrderBy(static kv => kv.Key, StringComparer.Ordinal),
            built.OrderBy(static kv => kv.Key, StringComparer.Ordinal));
        _ = clock;
    }

    private static string Body(ReadModel model, string id) => Encoding.UTF8.GetString(model.Store.Current.Slices[id].Body);

    private static long VersionOf(ReadModel model, string id) => model.Store.Current.Slices.TryGetValue(id, out var s) ? s.Version : -1;

    private static string TeamKey(string path = TeamPath) => TeamLane.KeyOf(Canon, path);

    [Fact]
    public void TheSeedBuildsTheTreeSlices()
    {
        var reader = Truth();
        var (model, _) = Seeded(reader);
        using var owner = model;
        var ids = model.Store.Current.Slices.Keys.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                "list:teams",
                $"team-summary:{D}/default",
                $"member:{D}/default/dev",
                $"member:{D}/default/@orchestrator",
                $"member:{D}/default/qa",
                $"team:{D}/default",
                $"topology:{D}/default",
                $"worktree:{D}/default/dev",
            }.Order(StringComparer.Ordinal),
            ids);
        Assert.Equal(
            "{\"name\":\"default\",\"display_name\":null,\"dir_id\":\"" + D + "\",\"orchestrator\":\"member:" + D + "/default/@orchestrator\",\"members\":[\"member:" + D + "/default/dev\",\"member:" + D + "/default/qa\"],"
            + "\"plan\":\"plan:" + D + "/_default\",\"pipeline\":{\"run\":\"run1\",\"decisions_count\":2,\"last_decision_at\":\"2026-06-01T10:20:00Z\",\"scope\":\"dir\"},\"worktrees\":[\"worktree:" + D + "/default/dev\"]}",
            Body(model, $"team:{D}/default"));
        Assert.Equal(
            "{\"id\":\"member:" + D + "/default/dev\",\"name\":\"dev\",\"role\":\"developer\",\"session\":\"s-dev\",\"activity\":\"activity:" + D + "/s-dev\","
            + "\"liveness\":{\"at\":\"2026-06-01T10:05:00Z\",\"pid\":42,\"pid_start\":\"ps-42\",\"session_start\":null},\"placement\":{\"transport_id\":\"p-dev\",\"stream\":null},\"last_exchanges\":[],\"last_report_at\":null,\"node_path\":\"/t/" + D + "/default/m/dev\",\"column\":2,\"context_at\":null,\"context_pct\":null,\"context_text\":\"—\",\"context_title\":\"context not reported\",\"context_over\":false}",
            Body(model, $"member:{D}/default/dev"));
        Assert.Contains("\"session\":null,\"activity\":null", Body(model, $"member:{D}/default/qa"), StringComparison.Ordinal);
        Assert.Contains("\"session\":\"so\"", Body(model, $"member:{D}/default/@orchestrator"), StringComparison.Ordinal);
        Assert.Equal(
            "{\"id\":\"worktree:" + D + "/default/dev\",\"team\":\"default\",\"assigned_member\":\"dev\",\"grant_state\":\"enforce\",\"work_root\":\"/wt/dev\",\"stream\":\"s1\"}",
            Body(model, $"worktree:{D}/default/dev"));
        Assert.Contains("\"member_worktree\":[{\"from\":\"member:" + D + "/default/dev\",\"to\":\"worktree:" + D + "/default/dev\"}]", Body(model, $"topology:{D}/default"), StringComparison.Ordinal);
        Assert.Equal("{\"teams\":[{\"id\":\"team:" + D + "/default\",\"name\":\"default\",\"display_name\":null,\"dir_id\":\"" + D + "\"}]}", Body(model, "list:teams"));
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T10:00:30Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), model.Store.Current.Slices[$"member:{D}/default/@orchestrator"].AsOfMs);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T10:20:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), model.Store.Current.Slices[$"team:{D}/default"].AsOfMs);
    }

    [Fact]
    public void ADecisionsChangeWithNoTeamEventUpdatesTheTeamPipelineAndEqualsARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Decisions[(Canon, "run2")] = ["2026-06-01T11:00:00Z"];
        model.Emit("decisions", DecisionsLane.KeyOf(Canon, "run2"));
        model.Emit("decisions", DecisionsLane.KeyOf(Canon, "run2"));
        Drain(model, clock);

        Assert.Contains("\"pipeline\":{\"run\":\"run2\",\"decisions_count\":1,\"last_decision_at\":\"2026-06-01T11:00:00Z\",\"scope\":\"dir\"}", Body(model, $"team:{D}/default"), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ADecisionsChangeInAnotherDirectoryLeavesTheTeamAlone()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var before = VersionOf(model, $"team:{D}/default");
        reader.Decisions[("/other/.claude/ah2", "run9")] = ["2026-06-02T00:00:00Z"];
        model.Emit("decisions", DecisionsLane.KeyOf("/other/.claude/ah2", "run9"));
        Drain(model, clock);

        Assert.Equal(before, VersionOf(model, $"team:{D}/default"));
    }

    [Fact]
    public void ARegistrationChangeWithNoTeamEventUpdatesTheMemberLivenessAndEqualsARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Registrations["s-dev"] = (Reg("s-dev", "dev", 77, "2026-06-01T10:30:00Z"), [TeamPath]);
        model.Emit("registrations", "s-dev");
        model.Emit("registrations", "s-dev");
        Drain(model, clock);

        Assert.Contains("\"liveness\":{\"at\":\"2026-06-01T10:30:00Z\",\"pid\":77,\"pid_start\":\"ps-77\"", Body(model, $"member:{D}/default/dev"), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ARegistrationNamingAnotherTeamFileDoesNotChangeTheMember()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var before = VersionOf(model, $"member:{D}/default/dev");
        reader.Registrations["s-x"] = (Reg("s-x", "dev", 99, "2026-06-01T12:00:00Z"), [Canon + "/teams/other.json"]);
        model.Emit("registrations", "s-x");
        Drain(model, clock);

        Assert.Equal(before, VersionOf(model, $"member:{D}/default/dev"));
        Assert.Contains("\"session\":\"s-dev\"", Body(model, $"member:{D}/default/dev"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARemovedRegistrationClearsTheMemberSessionAndEqualsARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        _ = reader.Registrations.Remove("s-dev");
        model.Emit("registrations", "s-dev");
        Drain(model, clock);

        Assert.Contains("\"session\":null,\"activity\":null", Body(model, $"member:{D}/default/dev"), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void AGrantsChangeUpdatesTheTeamTopologyAndWorktreeSlicesAndEqualsARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Grants[(Canon, "default")] = Grant("log", "dev", "qa");
        model.Emit("worktrees", WorktreeLane.KeyOf(Canon, "default"));
        model.Emit("worktrees", WorktreeLane.KeyOf(Canon, "default"));
        Drain(model, clock);

        Assert.Contains($"\"worktrees\":[\"worktree:{D}/default/dev\",\"worktree:{D}/default/qa\"]", Body(model, $"team:{D}/default"), StringComparison.Ordinal);
        Assert.Contains("\"grant_state\":\"log\"", Body(model, $"worktree:{D}/default/dev"), StringComparison.Ordinal);
        Assert.True(model.Store.Current.Slices.ContainsKey($"worktree:{D}/default/qa"));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ARemovedGrantsDocumentRemovesTheWorktreeSlices()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        _ = reader.Grants.Remove((Canon, "default"));
        model.Emit("worktrees", WorktreeLane.KeyOf(Canon, "default"));
        Drain(model, clock);

        Assert.False(model.Store.Current.Slices.ContainsKey($"worktree:{D}/default/dev"));
        Assert.Contains("\"worktrees\":[]", Body(model, $"team:{D}/default"), StringComparison.Ordinal);
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ATeamFileChangeUpdatesTheMembersAndAnAddedAndARemovedMemberEqualARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Teams[TeamPath] = Doc("so", "dev", "ops");
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.True(model.Store.Current.Slices.ContainsKey($"member:{D}/default/ops"));
        Assert.False(model.Store.Current.Slices.ContainsKey($"member:{D}/default/qa"));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ARenameIsServedAsDisplayNameOnTheTeamTheListAndTheSummaryAndTheNameStays()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Teams[TeamPath] = Doc("so", "dev", "qa").Replace("{\"team_id\"", "{\"display_name\":\"Night shift\",\"team_id\"", StringComparison.Ordinal);
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        foreach (var id in new[] { $"team:{D}/default", $"team-summary:{D}/default" })
        {
            var body = JsonText.Parse(Body(model, id));
            Assert.Equal(("default", "Night shift"), (body.Member("name")!.Value.AsString(), body.Member("display_name")!.Value.AsString()));
        }

        var row = Assert.Single(JsonText.Parse(Body(model, "list:teams")).Member("teams")!.Value.AsArray());
        Assert.Equal(("default", "Night shift"), (row.Member("name")!.Value.AsString(), row.Member("display_name")!.Value.AsString()));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void NegativeAnEmptyDisplayNameIsServedAsNone()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Teams[TeamPath] = Doc("so", "dev", "qa").Replace("{\"team_id\"", "{\"display_name\":\"\",\"team_id\"", StringComparison.Ordinal);
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.True(JsonText.Parse(Body(model, $"team-summary:{D}/default")).Member("display_name")!.Value.IsNull);
        Assert.Contains("\"display_name\":null", Body(model, "list:teams"), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletedTeamFileRemovesItsSlicesAndTheListEntry()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        _ = reader.Teams.Remove(TeamPath);
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.DoesNotContain(model.Store.Current.Slices.Keys, id => id.StartsWith("team:", StringComparison.Ordinal) || id.StartsWith("member:", StringComparison.Ordinal) || id.StartsWith("topology:", StringComparison.Ordinal));
        Assert.Equal("{\"teams\":[]}", Body(model, "list:teams"));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void AKnownDirsChangeUpdatesTheTeamListAndEqualsARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.KnownDirs.Clear();
        model.Emit("known-dirs", KnownDirsLane.Key);
        Drain(model, clock);

        Assert.Equal("{\"teams\":[]}", Body(model, "list:teams"));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ATeamAliasNamedDefaultMakesNoSliceAndAnotherAliasDoes()
    {
        var reader = Truth();
        reader.Teams[Canon + "/teams/default.json"] = Doc("so", "dev");
        reader.Teams[AliasPath] = Doc("sx", "dev");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.True(model.Store.Current.Slices.ContainsKey($"team:{D}/rel"));
        Assert.Single(model.Store.Current.Slices.Keys, id => id.StartsWith("team:", StringComparison.Ordinal) && id.EndsWith("/default", StringComparison.Ordinal));
    }

    private sealed class FixtureDecisions(JsonArray rows) : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) =>
            JsonValue.FromObject(new JsonObject { ["decisions"] = JsonValue.FromArray(rows) });
    }

    private static double DecisionsCountOf(params string[] rows)
    {
        var list = new JsonArray();
        foreach (var row in rows)
        {
            list.Add(JsonText.Parse(row));
        }

        var lane = new DecisionsLane(new FixtureDecisions(list), () => [Canon]);
        return lane.Reread(DecisionsLane.KeyOf(Canon, "run1"), 0).Record!.Value.Member("decisions_count")!.Value.AsNumber();
    }

    [Fact]
    public void AQuestionParkedAndAnsweredIsOneDecision() =>
        Assert.Equal(1, DecisionsCountOf("""{"kind":"parked","id":"d1"}""", """{"kind":"answered","id":"d2","answers":"d1"}"""));

    [Fact]
    public void NegativeADecidedRowAndAParkedOneAreTwoDecisions() =>
        Assert.Equal(2, DecisionsCountOf("""{"kind":"decided","id":"d1"}""", """{"kind":"parked","id":"d2"}"""));

    [Fact]
    public void NegativeAnAnswerToNoQuestionIsNoDecision() =>
        Assert.Equal(1, DecisionsCountOf("""{"kind":"decided","id":"d1"}""", """{"kind":"answered","id":"d2","answers":"gone"}"""));

    [Fact]
    public void DecisionRowsWithoutAUsableIdCountOncePerRowAndOnlyAWholeStringNamesADecision()
    {
        var expected = Ah.TestKit.IdlessDecisionsFixture.Expected();
        var lane = new DecisionsLane(new FixtureDecisions(Ah.TestKit.IdlessDecisionsFixture.Rows()), () => [Canon]);

        var record = lane.Reread(DecisionsLane.KeyOf(Canon, "run1"), 0).Record!.Value;

        Assert.Equal(expected.Member("decided")!.Value.AsNumber(), record.Member("by_team")!.Value.AsNumber());
        Assert.Equal(expected.Member("by_user")!.Value.AsNumber(), record.Member("by_user")!.Value.AsNumber());
        Assert.Equal(expected.Member("waiting")!.Value.AsNumber(), record.Member("waiting")!.Value.AsNumber());
        Assert.Equal(expected.Member("decision_orphans")!.Value.AsNumber(), record.Member("orphans")!.Value.AsNumber());
        Assert.Equal(expected.Member("decision_orphans")!.Value.AsNumber(), lane.Orphans);
    }

    [Fact]
    public void EventsInAnyOrderWithDuplicatesGiveTheSameSlicesAsARebuild()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Teams[TeamPath] = Doc("so", "dev", "ops");
        reader.Registrations["s-ops"] = (Reg("s-ops", "ops", 5, "2026-06-01T10:40:00Z"), [TeamPath]);
        reader.Grants[(Canon, "default")] = Grant("enforce", "dev", "ops");
        reader.Decisions[(Canon, "run1")] = ["2026-06-01T10:10:00Z", "2026-06-01T10:20:00Z", "2026-06-01T10:25:00Z"];
        model.Emit("worktrees", WorktreeLane.KeyOf(Canon, "default"));
        model.Emit("decisions", DecisionsLane.KeyOf(Canon, "run1"));
        model.Emit("registrations", "s-ops");
        model.Emit("teams", TeamKey());
        model.Emit("registrations", "s-ops");
        model.Emit("decisions", DecisionsLane.KeyOf(Canon, "run1"));
        Drain(model, clock);

        AssertEqualsRebuild(model, reader);
        Assert.Contains("\"session\":\"s-ops\"", Body(model, $"member:{D}/default/ops"), StringComparison.Ordinal);
    }

    [Fact]
    public void OneRegistrationEventReadsOneRegistrationAndRepublishesOnlyItsMember()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var versions = model.Store.Current.Slices.ToDictionary(static kv => kv.Key, static kv => kv.Value.Version);
        reader.Calls.Clear();
        reader.Bytes = 0;
        reader.Registrations["s-dev"] = (Reg("s-dev", "dev", 78, "2026-06-01T10:31:00Z"), [TeamPath]);
        model.Emit("registrations", "s-dev");
        Drain(model, clock);

        Assert.Equal(ReadsPerRegistrationEvent, reader.Calls.GetValueOrDefault("mailbox.registration"));
        Assert.Equal(ReadsPerRegistrationEvent, reader.Total);
        Assert.InRange(reader.Bytes, 1, BytesPerEventCeiling);
        var changed = model.Store.Current.Slices.Where(kv => kv.Value.Version != versions.GetValueOrDefault(kv.Key)).Select(static kv => kv.Key).ToList();
        Assert.Equal([$"member:{D}/default/dev", $"team-summary:{D}/default"], changed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OneTeamEventReadsOneTeamFile()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Calls.Clear();
        reader.Bytes = 0;
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "ops");
        model.Emit("teams", TeamKey());
        Drain(model, clock);

        Assert.Equal(ReadsPerTeamEvent, reader.Calls.GetValueOrDefault("team.read"));
        Assert.Equal(ReadsPerTeamEvent, reader.Total);
        Assert.InRange(reader.Bytes, 1, BytesPerEventCeiling);
    }

    [Fact]
    public void OneGrantsOneDecisionsAndOneKnownDirsEventEachReadOnce()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Calls.Clear();
        model.Emit("worktrees", WorktreeLane.KeyOf(Canon, "default"));
        Drain(model, clock);
        Assert.Equal(ReadsPerGrantsEvent, reader.Calls.GetValueOrDefault("worktree-grants.status"));
        Assert.Equal(ReadsPerGrantsEvent, reader.Total);

        reader.Calls.Clear();
        model.Emit("decisions", DecisionsLane.KeyOf(Canon, "run1"));
        Drain(model, clock);
        Assert.Equal(ReadsPerDecisionsEvent, reader.Calls.GetValueOrDefault("decisions.read"));
        Assert.Equal(ReadsPerDecisionsEvent, reader.Total);

        reader.Calls.Clear();
        model.Emit("known-dirs", KnownDirsLane.Key);
        Drain(model, clock);
        Assert.Equal(ReadsPerKnownDirsEvent, reader.Calls.GetValueOrDefault("known-dirs.read"));
        Assert.Equal(ReadsPerKnownDirsEvent, reader.Total);
    }

    [Fact]
    public async Task TheManifestAndThePollMakeNoStateReads()
    {
        var reader = Truth();
        var (model, _) = Seeded(reader);
        using var owner = model;
        model.Emit("teams", TeamKey());
        reader.Calls.Clear();

        var manifest = model.Manifest("tree");
        var poll = await model.PollAsync(0, "e1", ModelViewsSub(), TimeSpan.Zero, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Contains("team:" + D + "/default", Encoding.UTF8.GetString(manifest), StringComparison.Ordinal);
        Assert.Contains("list:teams", Encoding.UTF8.GetString(poll), StringComparison.Ordinal);
        Assert.Equal(0, reader.Total);
    }

    private static string[] ModelViewsSub() => ["view:tree"];

    [Fact]
    public void WithNoClientAHundredEventsAreAppliedAndNothingPollsEnumeratesOrReconciles()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var enumerations = model.Counters.KeySetEnumerations;
        var notices = model.Counters.ReconcileNotices;
        var applied = model.Counters.Applied;
        reader.Calls.Clear();

        for (var i = 0; i < 100; i++)
        {
            reader.Registrations["s-dev"] = (Reg("s-dev", "dev", 1000 + i, "2026-06-01T10:05:00Z"), [TeamPath]);
            model.Emit("registrations", "s-dev");
            Drain(model, clock);
            clock.Now += 3_600_000;
            model.Tick();
            Drain(model, clock);
        }

        Assert.Equal(applied + 100, model.Counters.Applied);
        Assert.Equal(100, reader.Calls.GetValueOrDefault("mailbox.registration"));
        Assert.Equal(100, reader.Total);
        Assert.Equal(0, model.Counters.Polls);
        Assert.Equal(enumerations, model.Counters.KeySetEnumerations);
        Assert.Equal(notices, model.Counters.ReconcileNotices);
    }

    /// <summary>A team record whose file event never came, as under load, with other hints still arriving: the next reconcile, a floor after the last, publishes it, and nothing does before then.</summary>
    [Fact]
    public void ATeamRecordWhoseEventWasLostIsPublishedByTheNextReconcileWhileOtherHintsWait()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        _ = model.Manifest("tree");
        model.Tick();
        Drain(model, clock);
        var before = Projected(model);

        bool StepTo(long ms)
        {
            clock.Now += ms;
            model.Scheduler.Touch();
            _ = model.Bus.Emit(new LaneKey("registrations", "s-dev"), NoticeOrigin.Hint, 100);
            model.Tick();
            for (var guard = 0; guard < 1000 && model.Projector.Step(); guard++)
            {
            }

            return !Projected(model).SequenceEqual(before);
        }

        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "late");

        Assert.False(StepTo(0));
        Assert.True(StepTo(61_000));
    }

    [Fact]
    public void WithAClientConnectedTheSameTickDoesStartAReconcile()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var enumerations = model.Counters.KeySetEnumerations;
        _ = model.Manifest("tree");

        model.Tick();
        Drain(model, clock);

        Assert.True(model.Counters.KeySetEnumerations > enumerations);
    }

    [Theory]
    [InlineData(Canon + "/team.json", "teams")]
    [InlineData(Canon + "/teams/rel.json", "teams")]
    [InlineData(Canon + "/pipeline/run1/decisions.jsonl", "decisions")]
    [InlineData(Home + "/registrations/s-dev.json", "registrations")]
    [InlineData(KnownDirsFile, "known-dirs")]
    public void ThePathTableClassifiesTheTreeStores(string path, string lane)
    {
        var table = new PathLaneTable(() => [Canon], home: Home, knownDirsFile: KnownDirsFile);

        Assert.Equal(PathClass.Classified, table.Classify(path, out var found, out var key));
        Assert.Equal(lane, found);
        Assert.NotEmpty(key);
    }

    [Theory]
    [InlineData(Canon + "/team.json.1a2b.tmp", "Ignored")]
    [InlineData(Canon + "/pipeline/run1", "Ignored")]
    [InlineData(Canon + "/pipeline/run1/other.txt", "Unclassified")]
    [InlineData(Canon + "/teams/rel.txt", "Unclassified")]
    [InlineData(Canon + "/teams/sub/rel.json", "Unclassified")]
    [InlineData(Canon + "/grants/.json", "Unclassified")]
    [InlineData(Canon + "/status.json", "Ignored")]
    [InlineData(Canon + "/status.json.bak", "Unclassified")]
    [InlineData(Canon + "/jobs/team/abc.json", "Ignored")]
    [InlineData(Canon + "/tmp/x.log", "Ignored")]
    [InlineData(Canon + "/.state-status/.executed-ops.jsonl", "Ignored")]
    [InlineData(Home + "/registrations/s-dev.json.tmp", "Ignored")]
    [InlineData(Home + "/registrations/nested/s.json", "Unclassified")]
    [InlineData(Home + "/registrations/.executed-ops.jsonl", "Unclassified")]
    [InlineData(Home + "/registrations/.executed-ops.jsonl.bak", "Unclassified")]
    [InlineData(Home + "/registrations/x.executed-ops.jsonl", "Unclassified")]
    [InlineData(Home + "/registrations/nested/.executed-ops.jsonl", "Unclassified")]
    [InlineData(Home + "/.executed-ops.jsonl", "Unclassified")]
    [InlineData("/elsewhere/team.json", "Unclassified")]
    [InlineData(Home + "/agent-hierarchy.known-dirs.json", "Unclassified")]
    [InlineData(KnownDirsFile + ".4242.tmp", "Ignored")]
    public void ThePathTableDoesNotClassifyTheNearestPathsThatAreNotStores(string path, string expected)
    {
        var table = new PathLaneTable(() => [Canon], home: Home, knownDirsFile: KnownDirsFile);

        Assert.Equal(expected, table.Classify(path, out var lane, out _).ToString());
        Assert.Equal(string.Empty, lane);
    }

    [Fact]
    public void TheTreeRowsNeedTheHomeDirectoryToClassifyRegistrationsAndKnownDirs()
    {
        var table = new PathLaneTable(() => [Canon]);

        Assert.Equal(PathClass.Unclassified, table.Classify(Home + "/registrations/s-dev.json", out _, out _));
        Assert.Equal(PathClass.Classified, table.Classify(TeamPath, out _, out _));
    }

    [Fact]
    public void ARealMemberNamedOrchestratorIsASeparateFlaggedSliceAndNeverBindsTheOrchestratorRegistration()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "orchestrator");
        reader.Registrations["s-fake"] = (Reg("s-fake", "orchestrator", 9, "2026-06-01T10:40:00Z"), [TeamPath]);
        var (model, _) = Seeded(reader);
        using var owner = model;

        var pseudo = Body(model, $"member:{D}/default/@orchestrator");
        var real = Body(model, $"member:{D}/default/orchestrator");

        Assert.Contains("\"session\":\"so\"", pseudo, StringComparison.Ordinal);
        Assert.DoesNotContain("conflict", pseudo, StringComparison.Ordinal);
        Assert.Contains("\"session\":null", real, StringComparison.Ordinal);
        Assert.Contains("\"conflict\":\"reserved_name\"", real, StringComparison.Ordinal);
        Assert.Contains($"\"member:{D}/default/orchestrator\"", Body(model, $"team:{D}/default"), StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberRegisteredAsNameAtTeamCarriesItsSession()
    {
        var reader = Truth();
        reader.Registrations["s-dev"] = (Reg("s-dev", "dev@default", 42, "2026-06-01T10:05:00Z"), [TeamPath]);
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains("\"session\":\"s-dev\"", Body(model, $"member:{D}/default/dev"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev@other")]
    [InlineData("dev2@default")]
    [InlineData("xdev@default")]
    public void ARegistrationForAnotherTeamOrAnotherNameDoesNotBindTheMember(string address)
    {
        var reader = Truth();
        reader.Registrations["s-dev"] = (Reg("s-dev", address, 42, "2026-06-01T10:05:00Z"), [TeamPath]);
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains("\"session\":null", Body(model, $"member:{D}/default/dev"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeamDocumentsSessionIdDoesNotBindTheOrchestratorRegistration()
    {
        var reader = Truth();
        reader.Registrations["so"] = (Reg("so", "dev", 1, "2026-06-01T10:00:30Z"), [TeamPath]);
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains("\"session\":null", Body(model, $"member:{D}/default/@orchestrator"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARoutedTeamWriteAndARoutedRegistrationEachReadExactlyOneFile()
    {
        var reader = Truth();
        var (model, clock) = Seeded(reader);
        using var owner = model;
        reader.Calls.Clear();

        model.Emit("teams", WatchModule.TeamKey(TeamPath)!);
        model.Emit("registrations", WatchModule.RegistrationKey("s-dev"));
        Drain(model, clock);

        Assert.Equal(ReadsPerTeamEvent, reader.Calls.GetValueOrDefault("team.read"));
        Assert.Equal(ReadsPerRegistrationEvent, reader.Calls.GetValueOrDefault("mailbox.registration"));
        Assert.Equal(ReadsPerTeamEvent + ReadsPerRegistrationEvent, reader.Total);
    }

    [Fact]
    public void AKeyOfADirectoryThatIsNotCanonicalReadsNothingAndMakesNoSlice()
    {
        var reader = Truth();
        const string Other = "/other/.claude/ah2";
        reader.Teams[Other + "/team.json"] = Doc("so", "dev");
        reader.Grants[(Other, "default")] = Grant("enforce", "dev");
        reader.Decisions[(Other, "run9")] = ["2026-06-02T00:00:00Z"];
        var (model, clock) = Seeded(reader);
        using var owner = model;
        var before = model.Store.Current.Slices.Keys.Order(StringComparer.Ordinal).ToList();
        reader.Calls.Clear();

        model.Emit("teams", TeamLane.KeyOf(Other, Other + "/team.json"));
        model.Emit("worktrees", WorktreeLane.KeyOf(Other, "default"));
        model.Emit("decisions", DecisionsLane.KeyOf(Other, "run9"));
        Drain(model, clock);

        Assert.Equal(0, reader.Total);
        Assert.Equal(before, model.Store.Current.Slices.Keys.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void AKeyThatFailsToReadForOneDirectoryDoesNotStopTheOthersFromBeingEnumerated()
    {
        var reader = Truth();
        var throwing = new ThrowingFor(reader, "/bad/.claude/ah2");
        var clock = new Clock();
        string[] canonical = ["/bad/.claude/ah2", Canon];
        var model = new ReadModel(TreeModel.Lanes(throwing, () => canonical, Home), new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, home: Home), TreeModel.Composers());
        using var owner = model;

        foreach (var lane in new[] { "teams", "worktrees", "decisions" })
        {
            model.EmitKeySet(lane);
        }

        Drain(model, clock);

        Assert.True(model.Store.Current.Slices.ContainsKey($"team:{D}/default"));
        Assert.True(model.Store.Current.Slices.ContainsKey($"worktree:{D}/default/dev"));
    }

    private sealed class ThrowingFor(TreeReader inner, string dir) : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) =>
            hierarchyDir == dir && op == "keys" ? throw new IOException("unreadable") : inner.Read(domain, op, hierarchyDir, args);
    }

    [Fact]
    public void AHintOnATeamFileQueuesTheTeamsKey()
    {
        var reader = Truth();
        var (model, clock) = Make(reader);
        using var owner = model;
        model.Hint(TeamPath);
        Drain(model, clock);

        Assert.Equal(1, reader.Calls.GetValueOrDefault("team.read"));
        Assert.True(model.Store.Current.Slices.ContainsKey($"team:{D}/default"));
        Assert.Equal(0, model.Counters.UnclassifiedHints);
    }
}
