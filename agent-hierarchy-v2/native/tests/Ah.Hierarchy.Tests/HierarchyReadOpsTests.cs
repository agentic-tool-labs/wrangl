using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>The read ops of the session-pids, activity and known-dirs domains return what the library reads of the same stores return.</summary>
public sealed class HierarchyReadOpsTests
{
    internal sealed record Rig(HierarchyServices Services, InProcessStateWriter Writer, string Repo, string Pool)
    {
        public string AhHome { get; init; } = string.Empty;
    }

    private sealed class RowsOnly(string session, string document) : ISessionAuthority
    {
        public int SingleReads { get; private set; }

        public bool IsDatabase => true;

        public SessionAuthorityResult Register(string registrationFile, string sessionId, Func<ReadOnlyMemory<byte>?, SessionDecision> decide) => throw new NotSupportedException();

        public SessionAuthorityResult Unregister(string registrationFile, string sessionId) => throw new NotSupportedException();

        public ReadOnlyMemory<byte>? ReadStored(string sessionId)
        {
            SingleReads++;
            return null;
        }

        public IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> ListStored() =>
            [KeyValuePair.Create(session, new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(document)))];
    }

    /// <summary>The real gate store whose row reads fail as a busy database would while <see cref="Busy"/> is set; its stamp still reads.</summary>
    private sealed class BusyRows(IGateSource real) : IGateSource
    {
        public bool Busy { get; set; }

        public IReadOnlyList<string> Bodies(string hierDir) => Busy ? [] : real.Bodies(hierDir);

        public IReadOnlyList<string>? BodiesOrNull(string hierDir, out string? why)
        {
            why = Busy ? "busy" : null;
            return Busy ? null : real.BodiesOrNull(hierDir, out why);
        }

        public string? Stamp(string hierDir) => real.Stamp(hierDir);
    }

    internal static Rig Make(Func<IFileReader, IFileReader>? wrapFiles = null, MsgsRoute? msgsRoute = null, Func<string, PlanState?>? backlogPlan = null, ISessionAuthority? sessions = null, bool decisionsInDb = false, Func<IGateSource, IGateSource>? wrapGates = null)
    {
        var sandbox = UnitSandbox.Create();
        var repo = Path.Combine(sandbox.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        var user = Path.Combine(sandbox.Home, ".claude", "ah2");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "agent-hierarchy.json"), "{\"enabled\":true}\n");
        var env = new FakeEnvironment(
            new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["TZ"] = "UTC" },
            new PluginInfo(HierarchyTableFns.AhRoot, "wrangl", "0.0.0", "1"));
        var plain = PlatformModule.CreateIo(null);
        var io = wrapFiles is null ? plain : plain with { Files = wrapFiles(plain.Files) };
        var facts = new ProcessFacts(repo, 4242);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, facts);
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
        var writer = new InProcessStateWriter { AhHome = env.AhHome, DecisionsInDb = decisionsInDb };
        var services = HierarchyModule.Create(new HierarchyEnvironment(
            io.Files, env, new FakeClock(DateTimeOffset.Parse("2026-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new SequenceRandom(), io.Processes, config, roster, writer, facts, HierarchyModule.CreateBootstrap(io.Writer)) { MsgsRoute = msgsRoute ?? TestMsgStore.Route(env.AhHome), BacklogPlan = backlogPlan, Sessions = sessions, SessionPids = Ah.StateDb.StateDbModule.CreateSessionPids(env.AhHome), Activity = Ah.StateDb.StateDbModule.CreateActivity(env.AhHome), Gates = (wrapGates ?? (static g => g))(Ah.StateDb.StateDbModule.CreateGates(env.AhHome)), QaLogRows = Ah.StateDb.StateDbModule.CreateQaLog(env.AhHome), RosterRecords = Ah.StateDb.StateDbModule.CreateRosterRecords(env.AhHome), Jobs = Ah.StateDb.StateDbModule.CreateJobs(env.AhHome), Decisions = decisionsInDb ? Ah.StateDb.StateDbModule.CreateDecisions(env.AhHome) : null, ProgressLogs = Ah.StateDb.StateDbModule.CreateProgressLogs(env.AhHome), Checkpoints = Ah.StateDb.StateDbModule.CreateCheckpoints(env.AhHome), ProgressDocs = Ah.StateDb.StateDbModule.CreateProgressDocs(env.AhHome), QaJobBodies = () => Ah.StateDb.StateDbModule.QaJobBodies(env.AhHome), KnownDirRows = () => Ah.StateDb.StateDbModule.KnownDirs(env.AhHome) });
        writer.Domains = services.StateDomains;
        var pool = Path.Combine(repo, ".claude", "ah2");
        Directory.CreateDirectory(pool);
        return new Rig(services, writer, repo, pool) { AhHome = env.AhHome };
    }

    internal static StateResult Read(Rig rig, string domain, string op, JsonObject args) =>
        rig.Writer.Invoke(domain, op, JsonText.ToUtf8(args, 0), new RequestContext(rig.Repo, rig.Pool, string.Empty, string.Empty));

    internal static JsonValue Parsed(StateResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonText.Parse(Utf8Text.Decode(result.ResultJson.Span));
    }

    private static void Session(Rig rig, string sessionId, int pid) =>
        rig.Services.Status.AppendSessionPid(rig.Pool, JsonValue.FromString(sessionId), JsonValue.FromNumber(pid));

    // fewer-turns S3.2: M-3 (an unset eta is medium, 600 s) and M-4 (an explicit small stays 300 s).
    [Fact]
    public void AnUnsetEtaCountsAsMediumAndAnExplicitSmallStaysSmall()
    {
        var m = Make().Services.Messages;

        Assert.Equal(600, m.ThresholdFor(JsonValue.Null));
        Assert.Equal(600, m.ThresholdFor(null));
        Assert.Equal(300, m.ThresholdFor(JsonValue.FromString("small")));
    }

    [Fact]
    public void SessionPidsListsTheLatestPidOfEachSession()
    {
        var rig = Make();
        Session(rig, "s1", 100);
        Session(rig, "s2", 200);
        Session(rig, "s1", 101);

        var sessions = Parsed(Read(rig, "session-pids", "pids", new JsonObject { ["dir"] = rig.Pool })).Member("sessions")!.Value.AsArray();

        Assert.Equal(["s1:101", "s2:200"], sessions.Select(s => $"{s.Member("session_id")!.Value.AsString()}:{s.Member("pid")!.Value.AsNumber()}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SessionPidsOfADirWithoutARecordIsEmptyAndWithoutADirIsRefused()
    {
        var rig = Make();

        Assert.Empty(Parsed(Read(rig, "session-pids", "pids", new JsonObject { ["dir"] = rig.Pool })).Member("sessions")!.Value.AsArray());
        Assert.False(Read(rig, "session-pids", "pids", new JsonObject()).Ok);
    }

    [Fact]
    public void OfPidReturnsOnlyTheSessionsOfThatPidAtOrAfterTheCutoff()
    {
        var rig = Make();
        Session(rig, "s1", 100);
        Session(rig, "s2", 200);

        var hit = Parsed(Read(rig, "session-pids", "of-pid", new JsonObject { ["dir"] = rig.Pool, ["pid"] = 100, ["not_before"] = "2026-06-01T00:00:00Z" })).Member("rows")!.Value.AsArray();
        var other = Parsed(Read(rig, "session-pids", "of-pid", new JsonObject { ["dir"] = rig.Pool, ["pid"] = 300, ["not_before"] = "2026-06-01T00:00:00Z" })).Member("rows")!.Value.AsArray();
        var late = Parsed(Read(rig, "session-pids", "of-pid", new JsonObject { ["dir"] = rig.Pool, ["pid"] = 100, ["not_before"] = "2026-06-01T12:00:01Z" })).Member("rows")!.Value.AsArray();

        Assert.Equal(["s1"], hit.Select(r => r.Member("session_id")!.Value.AsString()));
        Assert.Empty(other);
        Assert.Empty(late);
    }

    private static int SessionPidRowCount(Rig rig) => Ah.StateDb.StateDbModule.CreateSessionPids(rig.AhHome).Rows(rig.Pool).Count;

    // per-dir-files-to-db S1: one row per (dir, session, pid), and no file under the hierarchy dir
    [Fact]
    public void AHundredAppendsOfOnePidAreOneRowAndNoFileAppears()
    {
        var rig = Make();
        for (var i = 0; i < 100; i++)
        {
            Session(rig, "s1", 100);
        }

        Assert.Equal(1, SessionPidRowCount(rig));
        Assert.False(File.Exists(Path.Combine(rig.Pool, "session-pids.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(rig.Pool, ".state-session-pids")));
    }

    [Fact]
    public void APidThatComesBackKeepsTwoRowsAndOfPidStillListsTheSession()
    {
        var rig = Make();
        Session(rig, "s1", 100);
        Session(rig, "s1", 200);
        Session(rig, "s1", 100);

        Assert.Equal(2, SessionPidRowCount(rig));
        var hit = Parsed(Read(rig, "session-pids", "of-pid", new JsonObject { ["dir"] = rig.Pool, ["pid"] = 100, ["not_before"] = "2026-06-01T00:00:00Z" })).Member("rows")!.Value.AsArray();
        Assert.Equal(["s1"], hit.Select(r => r.Member("session_id")!.Value.AsString()));
        Assert.Equal([KeyValuePair.Create("s1", 100L)], rig.Services.Status.SessionPidsOf(rig.Pool));
    }

    [Fact]
    public void NegativeAnotherSessionOnTheSamePidIsItsOwnRow()
    {
        var rig = Make();
        Session(rig, "s1", 100);
        Session(rig, "s2", 100);

        Assert.Equal(2, SessionPidRowCount(rig));
    }

    [Fact]
    public void OfPidRefusesAFractionalPidAndAnUnparseableTime()
    {
        var rig = Make();

        Assert.False(Read(rig, "session-pids", "of-pid", new JsonObject { ["dir"] = rig.Pool, ["pid"] = 1.5, ["not_before"] = "2026-06-01T00:00:00Z" }).Ok);
        Assert.False(Read(rig, "session-pids", "of-pid", new JsonObject { ["dir"] = rig.Pool, ["pid"] = 100, ["not_before"] = "not a time" }).Ok);
        Assert.False(Read(rig, "session-pids", "of-pid", new JsonObject { ["dir"] = rig.Pool, ["not_before"] = "2026-06-01T00:00:00Z" }).Ok);
    }

    private static string StateSinceAfterIdleWrite(string recordFields, JsonObject fields)
    {
        var rig = Make();
        Assert.True(Read(rig, "activity", "put", new JsonObject { ["dir"] = rig.Pool, ["subject"] = "s1", ["body"] = $"{{\"activity\":\"idle\",\"at\":\"2026-01-01T02:00:00.000Z\",\"state_since\":\"2026-01-01T01:00:00.000Z\",{recordFields}}}" }).Ok);
        _ = Record(rig, "s1", fields);
        return rig.Services.Status.ReadActivityRecord(rig.Pool, JsonValue.FromString("s1")).Member("state_since")!.Value.AsString();
    }

    /// <summary>Seeds <paramref name="body"/> as the activity record of <paramref name="subject"/> in the rig's pool, through <c>activity.put</c>.</summary>
    internal static void PutActivity(Rig rig, string subject, string body) =>
        Assert.True(Read(rig, "activity", "put", new JsonObject { ["dir"] = rig.Pool, ["subject"] = subject, ["body"] = body }).Ok);

    /// <summary><c>activity.record</c> of <paramref name="fields"/> for <paramref name="subject"/> in the rig's pool: its <c>written</c>.</summary>
    private static bool Record(Rig rig, string subject, JsonObject fields)
    {
        var args = fields.Clone();
        args["dir"] = rig.Pool;
        args["subject"] = subject;
        return Parsed(Read(rig, "activity", "record", args)).Member("written")!.Value.AsBool();
    }

    [Fact]
    public void ARecordedActivityIsOneRowAndNoFile()
    {
        var rig = Make();
        var mirror = Path.Combine(rig.Repo, "mirror");
        Directory.CreateDirectory(mirror);

        Assert.True(Record(rig, "s1", new JsonObject { ["activity"] = "working", ["blocked_by"] = JsonValue.Null, ["mirror_dir"] = mirror }));
        Assert.True(Record(rig, "s1", new JsonObject { ["activity"] = "idle", ["blocked_by"] = JsonValue.Null, ["mirror_dir"] = mirror }));

        var activity = Ah.StateDb.StateDbModule.CreateActivity(rig.AhHome);
        Assert.Single(activity.Rows(rig.Pool));
        Assert.Single(activity.Rows(mirror));
        Assert.False(Directory.Exists(Path.Combine(rig.Pool, "activity")));
        Assert.Equal("idle", rig.Services.Status.ReadActivityRecord(mirror, JsonValue.FromString("s1")).Member("activity")!.Value.AsString());

        // negative: another subject in the same dir is its own row, and a clear takes only the named one
        Assert.True(Record(rig, "s2", new JsonObject { ["activity"] = "working", ["blocked_by"] = JsonValue.Null }));
        Assert.True(Parsed(Read(rig, "activity", "clear", new JsonObject { ["dir"] = rig.Pool, ["subject"] = "s1", ["mirror_dir"] = mirror })).Member("cleared")!.Value.AsBool());
        Assert.Equal(["s2"], activity.Rows(rig.Pool).Select(static r => r.Subject));
        Assert.Empty(activity.Rows(mirror));
    }

    // ah2-74 G1: the mark sets screen_clear_at on both copies and nothing else; no row is created for a subject with none
    [Fact]
    public void AScreenClearMarkSetsOnlyItsFieldOnBothCopiesAndCreatesNoRow()
    {
        var rig = Make();
        var mirror = Path.Combine(rig.Repo, "mirror");
        Directory.CreateDirectory(mirror);
        Assert.True(Record(rig, "s1", new JsonObject { ["activity"] = "blocked", ["blocked_by"] = "permission", ["mirror_dir"] = mirror }));
        var before = rig.Services.Status.ReadActivityRecord(rig.Pool, JsonValue.FromString("s1")).AsObject();

        Assert.True(rig.Services.Status.MarkScreenClearRouted(rig.Pool, JsonValue.FromString("s1"), "2026-11-17T13:10:15.000Z", mirror));

        foreach (var dir in new[] { rig.Pool, mirror })
        {
            var after = rig.Services.Status.ReadActivityRecord(dir, JsonValue.FromString("s1")).AsObject();
            Assert.Equal("2026-11-17T13:10:15.000Z", after["screen_clear_at"].AsString());
            Assert.Equal("blocked", after["activity"].AsString());
            Assert.Equal(before["at"].AsString(), after["at"].AsString());
        }

        // a mark already set is kept, not moved
        Assert.False(rig.Services.Status.MarkScreenClearRouted(rig.Pool, JsonValue.FromString("s1"), "2026-11-17T13:10:30.000Z", mirror));
        Assert.Equal("2026-11-17T13:10:15.000Z", rig.Services.Status.ReadActivityRecord(rig.Pool, JsonValue.FromString("s1")).Member("screen_clear_at")!.Value.AsString());

        // B2: unmark removes only the mark, on both copies; a second unmark changes nothing
        Assert.True(rig.Services.Status.MarkScreenClearRouted(rig.Pool, JsonValue.FromString("s1"), null, mirror));
        foreach (var dir in new[] { rig.Pool, mirror })
        {
            var after = rig.Services.Status.ReadActivityRecord(dir, JsonValue.FromString("s1")).AsObject();
            Assert.False(after.ContainsKey("screen_clear_at"));
            Assert.Equal("blocked", after["activity"].AsString());
            Assert.Equal(before["at"].AsString(), after["at"].AsString());
        }

        Assert.False(rig.Services.Status.MarkScreenClearRouted(rig.Pool, JsonValue.FromString("s1"), null, mirror));

        // negative: a subject with no record gets none
        Assert.False(rig.Services.Status.MarkScreenClearRouted(rig.Pool, JsonValue.FromString("s9"), "2026-11-17T13:10:15.000Z", mirror));
        Assert.Null(Ah.StateDb.StateDbModule.CreateActivity(rig.AhHome).Row(rig.Pool, "s9"));
    }

    private static JsonObject IdleWrite() => new() { ["activity"] = "idle", ["blocked_by"] = JsonValue.Null };

    [Fact]
    public void AStopAfterACompactionInsideTheTurnMovesStateSinceSoTheStallStillReads()
    {
        var since = StateSinceAfterIdleWrite("\"working_at\":\"2026-01-01T00:59:00.000Z\",\"compacted_at\":\"2026-01-01T01:30:00.000Z\"", IdleWrite());

        Assert.True(string.CompareOrdinal(since, "2026-01-01T01:30:00.000Z") > 0, since);
    }

    [Theory]
    [InlineData("\"working_at\":\"2026-01-01T00:59:00.000Z\"")]
    [InlineData("\"working_at\":\"2026-01-01T01:40:00.000Z\",\"compacted_at\":\"2026-01-01T00:30:00.000Z\"")]
    public void AStopWithNoCompactionSinceWorkingHoldsStateSince(string fields) =>
        Assert.Equal("2026-01-01T01:00:00.000Z", StateSinceAfterIdleWrite(fields, IdleWrite()));

    [Fact]
    public void TheCompactionWriteItselfHoldsStateSince()
    {
        var fields = IdleWrite();
        fields["extra"] = new JsonObject { ["compacted_at"] = "2026-01-01T01:50:00.000Z" };

        Assert.Equal("2026-01-01T01:00:00.000Z", StateSinceAfterIdleWrite("\"working_at\":\"2026-01-01T00:59:00.000Z\",\"compacted_at\":\"2026-01-01T01:30:00.000Z\"", fields));
    }

    [Fact]
    public void ActivityReadOfARecordedSubjectIsTheLibraryRecordAndAnotherSubjectIsNull()
    {
        var rig = Make();
        var fields = new JsonObject { ["activity"] = "working", ["blocked_by"] = JsonValue.Null };
        Assert.True(Record(rig, "s1", fields));

        var read = Parsed(Read(rig, "activity", "read", new JsonObject { ["dir"] = rig.Pool, ["subject"] = "s1" }));

        Assert.False(read.IsNull);
        Assert.Equal("working", read.Member("activity")!.Value.AsString());
        Assert.Equal(
            Utf8Text.Decode(JsonText.ToUtf8(rig.Services.Status.ReadActivityRecord(rig.Pool, JsonValue.FromString("s1")), 0)),
            Utf8Text.Decode(JsonText.ToUtf8(read, 0)));
        Assert.True(Parsed(Read(rig, "activity", "read", new JsonObject { ["dir"] = rig.Pool, ["subject"] = "s2" })).IsNull);
    }

    [Fact]
    public void ActivityReadOfASubjectWithoutARecordIsNullAndWithoutADirIsRefused()
    {
        var rig = Make();

        Assert.True(Parsed(Read(rig, "activity", "read", new JsonObject { ["dir"] = rig.Pool, ["subject"] = "nobody" })).IsNull);
        Assert.False(Read(rig, "activity", "read", new JsonObject { ["subject"] = "nobody" }).Ok);
    }

    [Fact]
    public void OpenExchangesOfAPoolWithNoMessagesIsEmptyForOneTeamAndForAllAndWithoutADirIsRefused()
    {
        var rig = Make();

        Assert.Empty(Parsed(Read(rig, "msgs", "open-exchanges", new JsonObject { ["dir"] = rig.Pool })).Member("exchanges")!.Value.AsArray());
        Assert.Empty(Parsed(Read(rig, "msgs", "open-exchanges", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t" })).Member("exchanges")!.Value.AsArray());
        Assert.Empty(Parsed(Read(rig, "msgs", "open-exchanges", new JsonObject { ["dir"] = rig.Pool, ["all_teams"] = true })).Member("exchanges")!.Value.AsArray());
        Assert.False(Read(rig, "msgs", "open-exchanges", new JsonObject()).Ok);
    }

    [Fact]
    public void ActivityKeysListsTheSubjectsOfADirByOneListingAndOpensNoRecord()
    {
        var rig = Make();
        Assert.Empty(Parsed(Read(rig, "activity", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("subjects")!.Value.AsArray());

        PutActivity(rig, "s2", "{}");
        PutActivity(rig, "pane-lead", "{}");
        PutActivity(rig, "s1", "not json"); // a record that does not parse is still listed: no content is read
        var other = HierarchyReadOpsTests.Make();
        PutActivity(other, "s9", "{}"); // negative: another dir's row is not listed
        var subjects = Parsed(Read(rig, "activity", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("subjects")!.Value.AsArray().Select(s => s.AsString()).ToList();

        Assert.Equal(["pane-lead", "s1", "s2"], subjects);

        Assert.False(Read(rig, "activity", "keys", new JsonObject()).Ok);
    }

    private const string IdA = "20261017-120000-aa11";
    private const string IdB = "20261017-130000-bb22";
    private const string IdC = "20261017-140000-cc33";

    private static string MsgFile(string pool, string id, string type, string body, string slug = "r2", string to = "implementor", string from = "orchestrator", string created = "2026-10-17T12:00:00-04:00", bool closeFence = true, string? item = null)
    {
        var dir = Path.Combine(pool, "msgs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}--{to}--{slug}--{type}.md");
        return TestMsgStore.Plant(path, $"---\nid: {id}\ntype: {type}\nto: {to}\nfrom: {from}\nslug: {slug}\n{(item is null ? string.Empty : $"item: {item}\n")}parent: null\ncreated: {created}\n{(closeFence ? "---\n" : string.Empty)}\n{body}\n");
    }

    [Fact]
    public void MsgsKeysListsTheRequestIdsOfThePoolNewestFirstAndOpensNoFile()
    {
        var rig = Make();
        Assert.Empty(Parsed(Read(rig, "msgs", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("ids")!.Value.AsArray());

        var a = MsgFile(rig.Pool, IdA, "request", "a");
        _ = MsgFile(rig.Pool, IdB, "request", "b");
        _ = MsgFile(rig.Pool, IdB, "response", "b reply");
        _ = MsgFile(rig.Pool, IdC, "response", "orphan response with no request");
        _ = MsgFile(rig.Pool, "20261017-150000-dd44", "note", "a note");
        File.WriteAllText(Path.Combine(rig.Pool, "msgs", "notes.txt"), "x");
        Directory.CreateDirectory(Path.Combine(rig.Pool, "msgs", "archive"));
        File.WriteAllText(Path.Combine(rig.Pool, "msgs", "archive", $"20261017-160000-ee55--implementor--old--request.md"), "x");
        File.SetUnixFileMode(a, UnixFileMode.None);
        try
        {
            var ids = Parsed(Read(rig, "msgs", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("ids")!.Value.AsArray().Select(i => i.AsString()).ToList();
            Assert.Equal([IdB, IdA], ids); // an unreadable request is still listed: no content is read; a response alone, a note and the archive are not exchanges
        }
        finally
        {
            File.SetUnixFileMode(a, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.False(Read(rig, "msgs", "keys", new JsonObject()).Ok);
    }

    [Fact]
    public void MsgsReadGivesTheHeaderAndTheTwoExcerptsOfAnExchange()
    {
        var rig = Make();
        var requestFile = MsgFile(rig.Pool, IdA, "request", "please build it", created: "2026-10-17T12:00:00-04:00");
        var responseFile = MsgFile(rig.Pool, IdA, "response", "built", to: "orchestrator", from: "implementor", created: "2026-10-17T13:30:00-04:00");

        var both = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA }));
        Assert.Equal(IdA, both.Member("id")!.Value.AsString());
        Assert.Equal("r2", both.Member("slug")!.Value.AsString());
        Assert.Equal("implementor", both.Member("to")!.Value.AsString());
        Assert.Equal("orchestrator", both.Member("from")!.Value.AsString());
        Assert.Equal("2026-10-17T12:00:00-04:00", both.Member("created")!.Value.AsString());
        Assert.Equal("2026-10-17T13:30:00-04:00", both.Member("response_created")!.Value.AsString());
        Assert.True(both.Member("has_request")!.Value.AsBool());
        Assert.True(both.Member("has_response")!.Value.AsBool());
        Assert.Equal("please build it", both.Member("request_excerpt")!.Value.AsString().Trim());
        Assert.Equal("built", both.Member("response_excerpt")!.Value.AsString().Trim());
        Assert.False(both.Member("excerpt_truncated")!.Value.AsBool());
        Assert.Equal(new FileInfo(requestFile).Length, both.Member("request_bytes")!.Value.AsNumber());
        Assert.Equal(new FileInfo(responseFile).Length, both.Member("response_bytes")!.Value.AsNumber());

        _ = MsgFile(rig.Pool, IdB, "request", "only a request");
        var open = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdB }));
        Assert.False(open.Member("has_response")!.Value.AsBool());
        Assert.True(open.Member("response_excerpt")!.Value.IsNull);
        Assert.True(open.Member("response_bytes")!.Value.IsNull);
        Assert.True(open.Member("request_bytes")!.Value.AsNumber() > 0);
    }

    [Fact]
    public void ADecisionLogIsTakenIntoTheTableOnceAndTheRunListingTakesARunNeverReadById()
    {
        var rig = Make(decisionsInDb: true);
        string Log(string run) => Path.Combine(rig.Pool, "pipeline", run, "decisions.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(Log("run-a"))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Log("run-b"))!);
        File.WriteAllText(Log("run-a"), "{\"kind\":\"merge\",\"pr\":1}\nnot json\n");
        File.WriteAllText(Log("run-b"), "{\"kind\":\"merge\",\"pr\":2}\n");
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(rig.AhHome, rig.Pool);

        // the import pass took the run files; a later write's row lands after the file's lines, and the file is left as it was
        var appended = Read(rig, "decisions", "append", new JsonObject { ["path"] = Log("run-a"), ["merge"] = new JsonObject { ["pr"] = 3, ["sha"] = "abc", ["method"] = "squash" } });
        Assert.True(appended.Ok, appended.Error);
        var a = Parsed(Read(rig, "decisions", "read", new JsonObject { ["path"] = Log("run-a") }));
        Assert.Equal([1d, 3d], a.Member("decisions")!.Value.AsArray().Select(static d => d.Member("pr")!.Value.AsNumber()));
        Assert.Equal(1d, a.Member("skipped")!.Value.AsNumber());
        Assert.Equal("{\"kind\":\"merge\",\"pr\":1}\nnot json\n", File.ReadAllText(Log("run-a")));

        // run-b, never read by id, is listed from its rows, which outlive its file
        string[] Runs() => [.. Parsed(Read(rig, "decisions", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("runs")!.Value.AsArray().Select(static r => r.AsString())];
        Assert.Equal(["run-a", "run-b"], Runs());
        File.Delete(Log("run-b"));
        Assert.Equal(["run-a", "run-b"], Runs());

        // the negative: with mode.decisions set back to files, the log is read from its file again, without the database's row
        Assert.True(Ah.StateDb.StateDbModule.OpenDirect(rig.AhHome).Write(tx =>
        {
            tx.Execute("UPDATE meta SET value = 'files' WHERE key = 'mode.decisions'");
            return 0;
        }).Ok);
        Assert.Equal([1d], Parsed(Read(rig, "decisions", "read", new JsonObject { ["path"] = Log("run-a") })).Member("decisions")!.Value.AsArray().Select(static d => d.Member("pr")!.Value.AsNumber()));
    }

    [Fact]
    public void ARouteAnsweredBeforeTheUpgradeIsStillTheEffectiveRouteAfterIt()
    {
        var rig = Make();
        File.WriteAllText(
            Path.Combine(rig.Pool, "gates.jsonl"),
            "{\"type\":\"route\",\"session_id\":\"s1\",\"value\":\"peers\",\"ts\":\"2026-05-01T00:00:00.000Z\"}\nnot json\n");
        // a row stored since the upgrade, before the first read, stays the later one
        Assert.True(Read(rig, "gates", "put", new JsonObject { ["dir"] = rig.Pool, ["body"] = "{\"type\":\"soft-gate\",\"outcome\":\"approve\"}" }).Ok);
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(rig.AhHome, rig.Pool);

        var route = rig.Services.Roster.EffectiveRoute(rig.Pool, null, "s1");

        Assert.Equal("peers", route.Get("value")!.Value.AsString());
        Assert.Equal("session", route.Get("source")!.Value.AsString());
        Assert.Equal(["route", "soft-gate"], rig.Services.Roster.ReadGates(rig.Pool).Select(static r => r.Member("type")!.Value.AsString()));

        // the negatives: another session's route is not this one's, and the file is taken once, so a later line in it is never read
        Assert.Equal("default", rig.Services.Roster.EffectiveRoute(rig.Pool, null, "s2").Get("source")!.Value.AsString());
        File.AppendAllText(Path.Combine(rig.Pool, "gates.jsonl"), "{\"type\":\"route\",\"session_id\":\"s2\",\"value\":\"peers\"}\n");
        Assert.Equal("default", rig.Services.Roster.EffectiveRoute(rig.Pool, null, "s2").Get("source")!.Value.AsString());
    }

    [Fact]
    public void AJobStillOpenBeforeTheUpgradeIsReadAfterItAndAnEndedOrUnreadableOneIsNot()
    {
        var rig = Make();
        var team = Path.Combine(rig.Pool, "jobs", "tm");
        Directory.CreateDirectory(team);
        File.WriteAllText(Path.Combine(team, "j-open.json"), "{\"id\":\"j-open\",\"owner\":\"o\"}");
        File.WriteAllText(Path.Combine(team, "j-done.json"), "{\"id\":\"j-done\",\"ended_at\":\"2026-05-01T00:00:00.000Z\"}");
        File.WriteAllText(Path.Combine(team, "j-bad.json"), "not json");
        // a row stored since the upgrade, before the first read, is kept
        Assert.True(Read(rig, "jobs", "put", new JsonObject { ["dir"] = rig.Pool, ["team"] = "tm", ["id"] = "j-new", ["record"] = new JsonObject { ["id"] = "j-new" } }).Ok);
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(rig.AhHome, rig.Pool);

        Assert.Equal(["j-new", "j-open"], rig.Services.Status.JobRows(rig.Pool).Select(static r => r.Id));
        Assert.False(File.Exists(Path.Combine(team, "j-new.json")));

        // the negative: the dir is taken once, so a record planted after the first read is never read
        File.WriteAllText(Path.Combine(team, "j-late.json"), "{\"id\":\"j-late\"}");
        Assert.Equal(["j-new", "j-open"], rig.Services.Status.JobRows(rig.Pool).Select(static r => r.Id));
    }

    [Fact]
    public void APutIfOpenWritesAnOpenJobAndLeavesAnEndedOrMissingOneAsItIs()
    {
        var rig = Make();
        JsonValue Put(string id, JsonObject record, bool ifOpen) =>
            Parsed(Read(rig, "jobs", "put", new JsonObject { ["dir"] = rig.Pool, ["team"] = "tm", ["id"] = id, ["record"] = record, ["if_open"] = ifOpen }));

        _ = Put("j-done", new JsonObject { ["id"] = "j-done", ["ended_at"] = "2026-06-01T11:00:00.000Z", ["exit"] = "7" }, ifOpen: false);
        _ = Put("j-open", new JsonObject { ["id"] = "j-open" }, ifOpen: false);

        Assert.True(Put("j-open", new JsonObject { ["id"] = "j-open", ["pid"] = 5 }, ifOpen: true).Member("written")!.Value.AsBool());
        // the negatives: an ended job keeps its end, and a job that is not there is not created
        Assert.False(Put("j-done", new JsonObject { ["id"] = "j-done", ["pid"] = 5 }, ifOpen: true).Member("written")!.Value.AsBool());
        Assert.False(Put("j-gone", new JsonObject { ["id"] = "j-gone" }, ifOpen: true).Member("written")!.Value.AsBool());
        _ = Put("j-odd", new JsonObject { ["id"] = "j-odd", ["ended_at"] = "not a time" }, ifOpen: false);
        Assert.False(Put("j-odd", new JsonObject { ["id"] = "j-odd" }, ifOpen: true).Member("written")!.Value.AsBool());

        var rows = rig.Services.Status.JobRows(rig.Pool).ToDictionary(static r => r.Id, static r => JsonText.Parse(r.Body));
        Assert.Equal(["j-done", "j-odd", "j-open"], rows.Keys.Order());
        Assert.Equal("7", rows["j-done"].Member("exit")!.Value.AsString());
        Assert.Equal(5, rows["j-open"].Member("pid")!.Value.AsNumber());
    }

    [Fact]
    public void APutIfOpenMergesOnlyItsOwnFieldsOntoTheRowAsStoredSoAHooksChangeSurvives()
    {
        var rig = Make();
        JsonValue Put(JsonObject record, bool ifOpen) =>
            Parsed(Read(rig, "jobs", "put", new JsonObject { ["dir"] = rig.Pool, ["team"] = "tm", ["id"] = "j", ["record"] = record, ["if_open"] = ifOpen }));

        // the tick read {id, slot}; a hook then attached a tool_use_id; the tick's patch sets pid and drops slot
        _ = Put(new JsonObject { ["id"] = "j", ["slot"] = "s", ["label"] = "old" }, ifOpen: false);
        _ = Put(new JsonObject { ["id"] = "j", ["slot"] = "s", ["label"] = "old", ["tool_use_id"] = "hook" }, ifOpen: false);
        Assert.True(Put(new JsonObject { ["pid"] = 5, ["slot"] = JsonValue.Null }, ifOpen: true).Member("written")!.Value.AsBool());

        var row = JsonText.Parse(Assert.Single(rig.Services.Status.JobRows(rig.Pool)).Body);
        Assert.Equal("hook", row.Member("tool_use_id")!.Value.AsString());
        Assert.Equal(5, row.Member("pid")!.Value.AsNumber());
        Assert.Null(row.Member("slot"));

        // the negative: a field the patch does not name is the row's, not reset
        Assert.Equal("old", row.Member("label")!.Value.AsString());
    }

    [Fact]
    public void AMergePutWritesItsOwnFieldsOntoAnEndedRowAndLeavesAMissingOneAlone()
    {
        var rig = Make();
        JsonValue Put(string id, JsonObject record, bool merge) =>
            Parsed(Read(rig, "jobs", "put", new JsonObject { ["dir"] = rig.Pool, ["team"] = "tm", ["id"] = id, ["record"] = record, ["merge"] = merge }));

        // `job extend` read {id, ended_at}; the tick then set slot; the verb's patch names only eta
        _ = Put("j", new JsonObject { ["id"] = "j", ["ended_at"] = "2026-06-01T11:00:00.000Z", ["slot"] = "tick" }, merge: false);
        Assert.True(Put("j", new JsonObject { ["eta"] = "large" }, merge: true).Member("written")!.Value.AsBool());

        var row = JsonText.Parse(Assert.Single(rig.Services.Status.JobRows(rig.Pool)).Body);
        Assert.Equal("large", row.Member("eta")!.Value.AsString());
        Assert.Equal("tick", row.Member("slot")!.Value.AsString());
        Assert.Equal("2026-06-01T11:00:00.000Z", row.Member("ended_at")!.Value.AsString());

        // the negative: a patch never creates a row from its few fields
        Assert.False(Put("j-gone", new JsonObject { ["eta"] = "large" }, merge: true).Member("written")!.Value.AsBool());
    }

    [Fact]
    public void MsgsReadGivesTheEtaFactsOfAnOpenRequestFromTheGateLogAndNullOnceAnswered()
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", "open one", created: "2026-10-17T12:00:00Z");
        _ = MsgFile(rig.Pool, IdB, "request", "answered one", created: "2026-10-17T12:00:00Z");
        _ = MsgFile(rig.Pool, IdB, "response", "done", to: "orchestrator", from: "implementor", created: "2026-10-17T12:10:00Z");
        foreach (var row in new[]
        {
            $"{{\"type\":\"liveness-nudge\",\"request_id\":\"{IdA}\",\"ts\":\"2026-10-17T12:06:00Z\"}}",
            $"{{\"type\":\"liveness-checked\",\"request_id\":\"{IdA}\",\"ts\":\"2026-10-17T12:07:00Z\"}}",
            $"{{\"type\":\"liveness-nudge\",\"request_id\":\"{IdB}\",\"ts\":\"2026-10-17T12:05:00Z\"}}",
        })
        {
            Assert.True(Read(rig, "gates", "put", new JsonObject { ["dir"] = rig.Pool, ["body"] = row }).Ok);
        }

        var open = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA }));
        var facts = open.Member("eta_facts")!.Value.AsObject();
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), facts.Get("start_ms")!.Value.AsNumber());
        Assert.Equal([DateTimeOffset.Parse("2026-10-17T12:06:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()], facts.Get("nudge_ms")!.Value.AsArray().Select(static v => (long)v.AsNumber()));
        Assert.Single(facts.Get("checked_ms")!.Value.AsArray());
        // the fixture sets no eta, which reads medium (fewer-turns S3.2)
        Assert.Equal(600, facts.Get("threshold_s")!.Value.AsNumber());

        var answered = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdB }));
        Assert.True(answered.Member("eta_facts")!.Value.IsNull);
    }

    [Fact]
    public void NegativeAGateRowWrittenAfterAReadIsInTheNextRead()
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", "open one", created: "2026-10-17T12:00:00Z");
        void Nudge(string ts) => Assert.True(Read(rig, "gates", "put", new JsonObject { ["dir"] = rig.Pool, ["body"] = $"{{\"type\":\"liveness-nudge\",\"request_id\":\"{IdA}\",\"ts\":\"{ts}\"}}" }).Ok);
        JsonArray Nudges() => Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA })).Member("eta_facts")!.Value.AsObject().Get("nudge_ms")!.Value.AsArray();

        Nudge("2026-10-17T12:06:00Z");
        Assert.Single(Nudges());

        Nudge("2026-10-17T12:16:00Z");
        Assert.Equal(2, Nudges().Count);
    }

    [Fact]
    public void NegativeAGateReadThatFailsAfterItsStampIsNotHeldForThatStamp()
    {
        BusyRows? gates = null;
        var rig = Make(wrapGates: g => gates = new BusyRows(g));
        _ = MsgFile(rig.Pool, IdA, "request", "open one", created: "2026-10-17T12:00:00Z");
        Assert.True(Read(rig, "gates", "put", new JsonObject { ["dir"] = rig.Pool, ["body"] = $"{{\"type\":\"liveness-nudge\",\"request_id\":\"{IdA}\",\"ts\":\"2026-10-17T12:06:00Z\"}}" }).Ok);
        JsonArray Nudges() => Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA })).Member("eta_facts")!.Value.AsObject().Get("nudge_ms")!.Value.AsArray();

        gates!.Busy = true;
        Assert.Empty(Nudges());

        gates.Busy = false;
        Assert.Single(Nudges());
    }

    /// <summary>An open request dispatched to a bare member name (as dispatch rows write <c>to_addr</c>), the member registered as <paramref name="registered"/> on session s9, which used a tool at 12:30.</summary>
    private static JsonObject EtaFactsWithRegistration(string registered)
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", "open one", created: "2026-10-17T12:00:00Z");
        rig.Services.Peers.AppendPeerRecord(new FsStoreWriteContext(), JsonValue.FromObject(new JsonObject { ["type"] = "dispatch", ["request_id"] = IdA, ["session_id"] = "s-orch", ["to_addr"] = "tm-implementor-1" }));
        var registrations = Path.Combine(rig.AhHome, "registrations");
        _ = Directory.CreateDirectory(registrations);
        File.WriteAllText(Path.Combine(registrations, "s9.json"), $"{{\"address\":\"{registered}\",\"addresses\":[\"{registered}\"],\"at\":\"2026-10-17T11:00:00Z\"}}");
        PutActivity(rig, "s9", "{\"activity\":\"working\",\"at\":\"2026-10-17T12:01:00Z\",\"tool_at\":\"2026-10-17T12:30:00Z\"}");
        return Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA })).Member("eta_facts")!.Value.AsObject();
    }

    private static (JsonObject Facts, RowsOnly Rows) EtaFactsFromRows(string registered)
    {
        var rows = new RowsOnly("s9", $"{{\"address\":\"{registered}\",\"addresses\":[\"{registered}\"],\"at\":\"2026-10-17T11:00:00Z\"}}");
        var rig = Make(sessions: rows);
        _ = MsgFile(rig.Pool, IdA, "request", "open one", created: "2026-10-17T12:00:00Z");
        rig.Services.Peers.AppendPeerRecord(new FsStoreWriteContext(), JsonValue.FromObject(new JsonObject { ["type"] = "dispatch", ["request_id"] = IdA, ["session_id"] = "s-orch", ["to_addr"] = "tm-implementor-1" }));
        return (Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA })).Member("eta_facts")!.Value.AsObject(), rows);
    }

    [Fact]
    public void AnAddresseeIsFoundInTheListedRowsWithNoReadPerSession()
    {
        var (facts, rows) = EtaFactsFromRows("tm-implementor-1@tm");

        Assert.Equal("s9", facts.Get("addressee_session")!.Value.AsString());
        Assert.Equal(0, rows.SingleReads);
    }

    [Fact]
    public void NegativeARowHoldingALongerNameIsNotTheAddressee()
    {
        var (facts, _) = EtaFactsFromRows("tm-implementor-10@tm");

        Assert.True(facts.Get("addressee_session")!.Value.IsNull);
    }

    [Fact]
    public void ARequestDispatchedToABareMemberNameTakesItsActivityFromTheMembersTeamQualifiedRegistration()
    {
        var facts = EtaFactsWithRegistration("tm-implementor-1@tm");

        Assert.Equal("s9", facts.Get("addressee_session")!.Value.AsString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), facts.Get("activity_ms")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeABareMemberNameDoesNotMatchALongerNameThatStartsWithIt()
    {
        var facts = EtaFactsWithRegistration("tm-implementor-10@tm");

        Assert.True(facts.Get("addressee_session")!.Value.IsNull);
        Assert.Equal(0, facts.Get("activity_ms")!.Value.AsNumber());
    }

    [Fact]
    public void MsgsReadGivesTheItemOfTheRequestAndNeverTheOneOfTheResponse()
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", "with an item", item: "s1/i3");
        _ = MsgFile(rig.Pool, IdB, "request", "no item");
        _ = MsgFile(rig.Pool, IdB, "response", "done", to: "orchestrator", from: "implementor", item: "s9/i9");

        Assert.Equal("s1/i3", Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA })).Member("item")!.Value.AsString());
        Assert.Null(Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdB })).Member("item"));
    }

    [Fact]
    public void MsgsReadGivesTheNamesAndTheTeamOfTheRequestAndNullWhereTheFrontMatterHasNone()
    {
        var rig = Make();
        var msgs = Path.Combine(rig.Pool, "msgs");
        Directory.CreateDirectory(msgs);
        _ = TestMsgStore.Plant(
            Path.Combine(msgs, $"{IdA}--implementor--r2--request.md"),
            $"---\nid: {IdA}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: r2\nparent: null\nto_name: ah2-tm3-implementor-29\nfrom_name: null\nteam: ah2-tm3\ncreated: 2026-10-17T12:00:00-04:00\n---\n\nbody\n");
        _ = TestMsgStore.Plant(
            Path.Combine(msgs, $"{IdA}--orchestrator--r2--response.md"),
            $"---\nid: {IdA}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: r2\nparent: null\nto_name: someone-else\nfrom_name: ah2-tm3-implementor-29\nteam: another\ncreated: 2026-10-17T13:30:00-04:00\n---\n\nreply\n");
        _ = MsgFile(rig.Pool, IdB, "request", "a request with no names or team");

        var named = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA }));
        Assert.Equal("ah2-tm3-implementor-29", named.Member("to_name")!.Value.AsString());
        Assert.True(named.Member("from_name")!.Value.IsNull);
        Assert.Equal("ah2-tm3", named.Member("team")!.Value.AsString());

        var bare = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdB }));
        Assert.True(bare.Member("to_name")!.Value.IsNull);
        Assert.True(bare.Member("from_name")!.Value.IsNull);
        Assert.True(bare.Member("team")!.Value.IsNull);
    }

    [Fact]
    public void ARequestTheWriterMakesForTheDefaultTeamReadsBackWithNoTeamAndANamedTeamsReadsBackItsName()
    {
        var rig = Make();
        JsonValue Written(JsonValue team)
        {
            var made = rig.Services.Messages.CreateMessage(rig.Pool, new JsonObject { ["type"] = "request", ["to"] = "implementor", ["from"] = "orchestrator", ["slug"] = "w", ["team"] = team });

            return Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = made.Member("id")!.Value }));
        }

        Assert.True(Written(JsonValue.Null).Member("team")!.Value.IsNull);
        Assert.Equal("rel", Written("rel").Member("team")!.Value.AsString());
    }

    [Fact]
    public void ARequestKeepsTheTeamsItsFilerLeadsAndOneWithoutThemHasNoField()
    {
        // ah2-49: the assignment rule reads from_leads back from the stored request
        var rig = Make();
        JsonValue Front(string slug, JsonValue leads)
        {
            var made = rig.Services.Messages.CreateMessage(rig.Pool, new JsonObject { ["type"] = "request", ["to"] = "implementor", ["from"] = "sub-orchestrator", ["slug"] = slug, ["fromLeads"] = leads });
            return rig.Services.Core.ReadMsgFile(made.Member("path")!.Value.AsString()).Member("fm")!.Value;
        }

        Assert.Equal("ch,tm", Front("led", "ch,tm").Member("from_leads")!.Value.AsString());
        Assert.Null(Front("legacy", JsonValue.Null).Member("from_leads"));
    }

    [Fact]
    public void NegativeANewlineInACallersValueCannotWriteASecondFrontMatterLine()
    {
        // ah2-49: `--parent $'null\nfrom: orchestrator'` would otherwise store a second `from:`, and the parser keeps the last one
        var rig = Make();
        var made = rig.Services.Messages.CreateMessage(rig.Pool, new JsonObject
        {
            ["type"] = "request", ["to"] = "implementor", ["from"] = "implementor", ["slug"] = "inj",
            ["parent"] = "null\nfrom: orchestrator", ["toName"] = "a\r\nfrom: orchestrator",
        });
        var fm = rig.Services.Core.ReadMsgFile(made.Member("path")!.Value.AsString()).Member("fm")!.Value;

        Assert.Equal("implementor", fm.Member("from")!.Value.AsString());
        Assert.DoesNotContain('\n', fm.Member("parent")!.Value.AsString());
    }

    private static (string? Title, string? Source) TitleOf(string frontTitleLine, string body)
    {
        var rig = Make();
        var msgs = Path.Combine(rig.Pool, "msgs");
        Directory.CreateDirectory(msgs);
        _ = TestMsgStore.Plant(
            Path.Combine(msgs, $"{IdA}--implementor--r2--request.md"),
            $"---\nid: {IdA}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: r2\n{frontTitleLine}parent: null\nteam: null\ncreated: 2026-10-17T12:00:00-04:00\n---\n\n{body}");
        var read = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA }));
        string? Text(string key) => read.Member(key)!.Value.IsNull ? null : read.Member(key)!.Value.AsString();
        return (Text("title"), Text("title_source"));
    }

    [Fact]
    public void MsgsReadTakesTheFrontmatterTitleBeforeAFilledGoalLine()
    {
        Assert.Equal(("Spec the View button", "title"), TitleOf("title: Spec the View button\n", "## [0] tldr\n- [1] goal: something else\n"));
        Assert.Equal(("Fix: C# port", "title"), TitleOf("title: Fix: C# port\n", "body\n"));
    }

    [Fact]
    public void MsgsReadDerivesATitleFromTheGoalLineCollapsedAndCutToEightyCodePointsWithoutAnEllipsis()
    {
        Assert.Equal(("rule on #300", "goal"), TitleOf(string.Empty, "## [0] tldr\n- [1] goal: rule   on\t#300\n"));
        var (title, source) = TitleOf(string.Empty, "- [1] goal: " + string.Concat(Enumerable.Repeat("🙂", 200)) + "\n");
        Assert.Equal("goal", source);
        Assert.Equal(string.Concat(Enumerable.Repeat("🙂", 80)), title);
    }

    [Fact]
    public void MsgsReadHasNoTitleForAnUntouchedSkeletonOrANoneGoalOrOnlySectionHeads()
    {
        Assert.Equal((null, null), TitleOf(string.Empty, "## [0] tldr\n- [1] goal: \n\n## [1] goal\n- none\n"));
        Assert.Equal((null, null), TitleOf(string.Empty, "## [0] tldr\n- [1] goal: none\n\n## [1] goal\n- none\n"));
        Assert.Equal((null, null), TitleOf(string.Empty, "- [1] goal: NONE\n"));
    }

    [Fact]
    public void MsgsReadFallsBackToTheFirstHeadingOfABodyFileBriefAndSkipsSectionHeads()
    {
        Assert.Equal(("Task: spec \"x\" (Lane F)", "heading"), TitleOf(string.Empty, "Work tree: /w\n\n# Task: spec \"x\" (Lane F)\n\n## [0] tldr\n"));
        Assert.Equal(("Plan", "heading"), TitleOf(string.Empty, "## [0] tldr\n### Plan\n"));
        Assert.Equal((null, null), TitleOf(string.Empty, "#nospace\n"));
    }

    [Fact]
    public void MsgsReadDoesNotLookForAGoalLineBeyondTheFirst4096Characters()
    {
        Assert.Equal((null, null), TitleOf(string.Empty, new string('x', 4200) + "\n- [1] goal: late\n"));
    }

    [Fact]
    public void MsgsReadFlattensAHandWrittenFrontmatterTitleStrippingControlCharactersAndCuttingAtEightyRunes()
    {
        var (title, source) = TitleOf("title: \u001b[31mred\u0007 " + new string('x', 100) + "\n", "body\n");

        Assert.Equal("title", source);
        Assert.Equal("[31mred " + new string('x', 72), title);
        Assert.DoesNotContain(title!, char.IsControl);
        Assert.Equal(("Plain words", "title"), TitleOf("title: Plain\u0001 words\n", "body\n"));
    }

    [Fact]
    public void AutoTitleMakesAReadableTitleFromTheFirstLineAndNullWhereNoneCanBeMade()
    {
        var messages = Make().Services.Messages;

        Assert.Equal("Make UI slick", messages.AutoTitle("Make UI slick\nand then the rest"));
        Assert.Equal("fix 312 now", messages.AutoTitle("fix #312 now"));
        var long60 = messages.AutoTitle("Implement the whole thing with every single detail spelled out in order");
        Assert.NotNull(long60);
        Assert.True(long60!.Length <= 60);
        Assert.Equal("Make UI slick", messages.AutoTitle("[hierarchy-msg /abs/path.md]\nMake UI slick"));
        Assert.Null(messages.AutoTitle("[hierarchy-msg /abs/path.md]"));
        Assert.Null(messages.AutoTitle("so-lanes p1e"));
        Assert.Null(messages.AutoTitle("ok"));
        Assert.Null(messages.AutoTitle("#312"));
        Assert.Null(messages.AutoTitle(string.Empty));
    }

    [Fact]
    public void MsgsReadTreatsAnEmptyOrNullFrontmatterTitleAsAbsent()
    {
        Assert.Equal(("from goal", "goal"), TitleOf("title: \n", "- [1] goal: from goal\n"));
        Assert.Equal(("from goal", "goal"), TitleOf("title: null\n", "- [1] goal: from goal\n"));
        Assert.Equal(("Foo", "title"), TitleOf("title: Foo #bar\n", "- [1] goal: from goal\n"));
    }

    [Fact]
    public void MsgsReadOfAnIdWithNoRequestInThePoolIsNull()
    {
        var rig = Make();
        Assert.True(Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA })).IsNull);
        _ = MsgFile(rig.Pool, IdA, "response", "a response alone");
        Assert.True(Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA })).IsNull);
        Assert.False(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool }).Ok);
    }

    [Fact]
    public void MsgsReadCutsAnExcerptAt4096BytesOnACharacterBoundaryAndSaysSo()
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", new string('é', 3000));
        _ = MsgFile(rig.Pool, IdB, "request", new string('x', 4096));
        _ = MsgFile(rig.Pool, IdC, "request", new string('x', 200_000));

        var cut = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdA }));
        var excerpt = cut.Member("request_excerpt")!.Value.AsString().TrimStart('\n');
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(excerpt) <= 4096);
        Assert.DoesNotContain('�', excerpt);
        Assert.True(excerpt.Length >= 2040 && excerpt.All(c => c == 'é'));
        Assert.True(cut.Member("excerpt_truncated")!.Value.AsBool());

        var exact = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdB }));
        Assert.Equal(4096, exact.Member("request_excerpt")!.Value.AsString().Count(c => c == 'x'));
        Assert.False(exact.Member("excerpt_truncated")!.Value.AsBool()); // exactly the cap is not a cut

        var huge = Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = IdC }));
        Assert.True(huge.Member("request_excerpt")!.Value.AsString().Length <= 4096);
        Assert.True(huge.Member("excerpt_truncated")!.Value.AsBool());
    }

    private static JsonValue ReadExchange(Rig rig, string id) =>
        Parsed(Read(rig, "msgs", "read", new JsonObject { ["dir"] = rig.Pool, ["id"] = id }));

    [Fact]
    public void MsgsReadOfAFileWhoseFrontmatterDoesNotCloseAnswersWithAnErrorAndNoHeaderSoNothingIsRetried()
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", "torn write", closeFence: false);

        var torn = ReadExchange(rig, IdA);

        Assert.Contains("missing or does not close", torn.Member("error")!.Value.AsString());
        Assert.True(torn.Member("has_request")!.Value.AsBool());
        Assert.Equal("r2", torn.Member("slug")!.Value.AsString());
        Assert.True(torn.Member("created")!.Value.IsNull);
        Assert.True(torn.Member("request_excerpt")!.Value.IsNull);
        Assert.True(torn.Member("title")!.Value.IsNull);
        Assert.True(torn.Member("title_source")!.Value.IsNull);
    }

    [Fact]
    public void MsgsReadGivesNoDerivedTitleForARequestThatCannotBeReadEvenWhenItsBodyHoldsAGoalLine()
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", "- [1] goal: Make UI slick\n", closeFence: false);

        var torn = ReadExchange(rig, IdA);

        Assert.Contains("missing or does not close", torn.Member("error")!.Value.AsString());
        Assert.True(torn.Member("title")!.Value.IsNull);
        Assert.True(torn.Member("title_source")!.Value.IsNull);
    }

    [Fact]
    public void MsgsReadOfAWellFormedExchangeHasNoErrorAndABadResponseDoesNotHideTheRequest()
    {
        var rig = Make();
        _ = MsgFile(rig.Pool, IdA, "request", "fine");
        Assert.Null(ReadExchange(rig, IdA).Member("error"));

        _ = MsgFile(rig.Pool, IdB, "request", "request is fine");
        _ = MsgFile(rig.Pool, IdB, "response", "torn", to: "orchestrator", closeFence: false);
        var mixed = ReadExchange(rig, IdB);

        Assert.Equal("request is fine", mixed.Member("request_excerpt")!.Value.AsString());
        Assert.True(mixed.Member("has_response")!.Value.AsBool());
        Assert.True(mixed.Member("response_excerpt")!.Value.IsNull);
        Assert.Contains("--response.md", mixed.Member("error")!.Value.AsString());
    }

    [Fact]
    public void MsgsReadOfACrlfFileGivesAnExcerptWithoutCarriageReturns()
    {
        var rig = Make();
        Directory.CreateDirectory(Path.Combine(rig.Pool, "msgs"));
        _ = TestMsgStore.Plant(
            Path.Combine(rig.Pool, "msgs", $"{IdA}--implementor--r2--request.md"),
            "---\r\nid: " + IdA + "\r\nto: implementor\r\nfrom: orchestrator\r\ncreated: 2026-10-17T12:00:00-04:00\r\n---\r\n\r\nline one\r\nline two\r\n");

        var read = ReadExchange(rig, IdA);

        Assert.Equal("line one\nline two", read.Member("request_excerpt")!.Value.AsString());
        Assert.Equal("orchestrator", read.Member("from")!.Value.AsString());
        Assert.Null(read.Member("error"));
    }

    [Fact]
    public void StatusRefreshWritesNoFileAndNoStateDirInAMainOrALinkedTreesH()
    {
        var rig = Make();
        var tree = Path.Combine(Path.GetDirectoryName(rig.Repo)!, "tree");
        var gitdir = Path.Combine(rig.Repo, ".git", "worktrees", "tree");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "gitdir"), Path.Combine(tree, ".git") + "\n");
        File.WriteAllText(Path.Combine(gitdir, "commondir"), "../..\n");
        var treePool = Path.Combine(tree, ".claude", "ah2");
        Directory.CreateDirectory(treePool);
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + gitdir + "\n");

        JsonValue Refresh(string cwd, string dir, string main) => Parsed(rig.Writer.Invoke(
            "status", "refresh", JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["enabled"] = true, ["event"] = false }, 0), new RequestContext(cwd, dir, main, string.Empty))).Member("written")!.Value;

        // files-off-disk F3a: status is db-only; refresh writes no status.json and never makes .state-status/
        Assert.False(Refresh(rig.Repo, rig.Pool, string.Empty).AsBool());
        Assert.False(File.Exists(Path.Combine(rig.Pool, "status.json")));
        Assert.False(Directory.Exists(Path.Combine(rig.Pool, ".state-status")));
        Assert.False(Refresh(tree, treePool, rig.Pool).AsBool());
        Assert.False(File.Exists(Path.Combine(treePool, "status.json")));

        // a db-only write from the tree leaves its H absent; a file-backed one still creates it
        Directory.Delete(treePool);
        rig.Services.StateCalls.AppendGate(treePool, JsonValue.FromObject(new JsonObject { ["gate"] = "g" }));
        Assert.False(Directory.Exists(treePool));
        // jobs went db-only with the batch; an ask (the decisions domain) is still file-backed
        rig.Services.StateCalls.AppendAsk(treePool, JsonValue.FromObject(new JsonObject { ["q"] = "?" }));
        Assert.True(Directory.Exists(treePool));
    }

    [Fact]
    public void StatusReadIsTheComputedDocumentEvenOverAStaleFileAndAnInternalRead()
    {
        var rig = Make();
        var op = rig.Services.StateDomains.Single(d => d.Name == "status").Ops.Single(o => o.Name == "read");
        Assert.Equal(OpKind.Read, op.Kind);
        Assert.False(op.Daemon);

        // negative: a left-over status.json is never read
        File.WriteAllText(Path.Combine(rig.Pool, "status.json"), "{\"schema\":1,\"tea");
        var doc = Parsed(Read(rig, "status", "read", new JsonObject { ["dir"] = rig.Pool }));
        Assert.Equal(rig.Services.Status.StatusSchema, doc.Member("schema")!.Value.AsNumber());
        Assert.True(doc.Member("enabled")!.Value.AsBool());
        Assert.False(Read(rig, "status", "read", new JsonObject()).Ok);

        // a disabled repo reads as disabled, as the old status.json carried it
        File.WriteAllText(Path.Combine(rig.Pool, "agent-hierarchy.json"), "{\"enabled\":false}\n");
        Assert.False(Parsed(Read(rig, "status", "read", new JsonObject { ["dir"] = rig.Pool })).Member("enabled")!.Value.AsBool());
    }

    [Fact]
    public void DecisionsKeysListsTheRunsThatHaveALogByOneListingAndOpensNoLog()
    {
        var rig = Make();
        Assert.Empty(Parsed(Read(rig, "decisions", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("runs")!.Value.AsArray());

        var pipeline = Path.Combine(rig.Pool, "pipeline");
        Directory.CreateDirectory(Path.Combine(pipeline, "run-b"));
        Directory.CreateDirectory(Path.Combine(pipeline, "run-a"));
        Directory.CreateDirectory(Path.Combine(pipeline, "run-empty"));
        File.WriteAllText(Path.Combine(pipeline, "run-b", "decisions.jsonl"), "{}\n");
        var unreadable = Path.Combine(pipeline, "run-a", "decisions.jsonl");
        File.WriteAllText(unreadable, "{}\n");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        File.WriteAllText(Path.Combine(pipeline, "run-empty", "notes.txt"), "x");
        File.WriteAllText(Path.Combine(pipeline, "stray.txt"), "x");
        try
        {
            var runs = Parsed(Read(rig, "decisions", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("runs")!.Value.AsArray().Select(s => s.AsString()).ToList();

            Assert.Equal(["run-a", "run-b"], runs); // a log it could not open is still listed; a run without a log and a stray file are not
        }
        finally
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.False(Read(rig, "decisions", "keys", new JsonObject()).Ok);
    }

    [Fact]
    public void KnownDirsReadListsTheNotedDirsAndIsEmptyBeforeAny()
    {
        var rig = Make();
        Assert.Empty(Parsed(Read(rig, "known-dirs", "read", new JsonObject())).Member("dirs")!.Value.AsArray());

        Session(rig, "s1", 100);

        Assert.Equal([rig.Pool], Parsed(Read(rig, "known-dirs", "read", new JsonObject())).Member("dirs")!.Value.AsArray().Select(d => d.AsString()));

        // noting a dir again keeps its one row
        Assert.Equal(1, Parsed(Read(rig, "known-dirs", "note", new JsonObject { ["hierarchy_dir"] = rig.Pool })).Member("count")!.Value.AsNumber());
    }

    private static JsonObject QaJob(string id, long rev, string state, long? endedAt = null) => new()
    {
        ["id"] = id, ["rev"] = rev, ["sha"] = new string('a', 40), ["base"] = JsonValue.Null, ["set"] = "full", ["repo"] = "/r", ["fresh"] = false,
        ["priority"] = "adhoc", ["requesters"] = new JsonArray { "me" }, ["team"] = JsonValue.Null, ["created"] = 1, ["state"] = state,
        ["claimed_by"] = JsonValue.Null, ["claimed_at"] = JsonValue.Null, ["result_path"] = JsonValue.Null, ["verdict"] = JsonValue.Null,
        ["ended_at"] = endedAt is { } e ? e : JsonValue.Null, ["lost_claims"] = 0, ["reason"] = JsonValue.Null,
    };

    [Fact]
    public void QaAppendStoresEachJobsLatestSnapshotAndRefusesAStaleRevision()
    {
        var rig = Make();
        Assert.True(Parsed(Read(rig, "qa", "append", new JsonObject { ["rec"] = QaJob("j1", 1, "queued") })).Member("ok")!.Value.AsBool());

        // the nearest non-match: rev 1 again is stale, and writes nothing
        var stale = Parsed(Read(rig, "qa", "append", new JsonObject { ["rec"] = new JsonArray { QaJob("j2", 1, "queued"), QaJob("j1", 1, "failed", 3) } }));
        Assert.False(stale.Member("ok")!.Value.AsBool());
        Assert.Equal(["j1"], stale.Member("conflict")!.Value.AsArray().Select(c => c.AsString()));

        Assert.True(Parsed(Read(rig, "qa", "append", new JsonObject { ["rec"] = QaJob("j1", 2, "done", 5) })).Member("ok")!.Value.AsBool());
        var jobs = Parsed(Read(rig, "qa", "read", new JsonObject())).AsArray();
        Assert.Equal(["j1 2 done"], jobs.Select(j => $"{j.Member("id")!.Value.AsString()} {j.Member("rev")!.Value.AsNumber()} {j.Member("state")!.Value.AsString()}"));
    }

    [Fact]
    public void QaCompactDropsOnlyFinishedJobsPastTheRetention()
    {
        var rig = Make();
        const long Now = 1_000_000_000;
        var cutoff = Now - (long)QaRows.RetentionMs;
        var rec = new JsonArray { QaJob("old", 1, "done", cutoff - 1), QaJob("edge", 1, "failed", cutoff), QaJob("open", 1, "queued") };
        Assert.True(Parsed(Read(rig, "qa", "append", new JsonObject { ["rec"] = rec })).Member("ok")!.Value.AsBool());

        Assert.Equal(1, Parsed(Read(rig, "qa", "compact", new JsonObject { ["now_ms"] = Now })).Member("dropped")!.Value.AsNumber());
        Assert.Equal(["edge", "open"], Parsed(Read(rig, "qa", "read", new JsonObject())).AsArray().Select(j => j.Member("id")!.Value.AsString()));
    }

    private static string Text(JsonValue v) => Utf8Text.Decode(JsonText.ToUtf8(v, 0));

    private static ProgressStreamInfo Info => new("worker", "ah/x", "wt", "the goal");

    private static JsonObject ProgressArgs(Rig rig, string team, string? stream = null)
    {
        var args = new JsonObject { ["dir"] = rig.Pool, ["team"] = team };
        if (stream is not null)
        {
            args["stream"] = stream;
        }

        return args;
    }

    [Fact]
    public void ProgressReadIsTheLibraryReadOfTheFolder()
    {
        var rig = Make();
        _ = rig.Services.Progress.EnsureTeam(rig.Pool, "t", Info);

        var viaOp = Parsed(Read(rig, "progress", "read", ProgressArgs(rig, "t")));

        var library = rig.Services.Progress.Read(rig.Pool, "t");
        Assert.Equal(Text(ProgressJson.ToJson(library)), Text(viaOp));
        var back = ProgressJson.ReadTeam(viaOp);
        Assert.True(back.RollupPresent);
        Assert.Equal("main", Assert.Single(back.Streams).Stream);
    }

    [Fact]
    public void ProgressReadOfATeamWithNoFolderIsEmptyAndOfAnOffTeamIsOff()
    {
        var rig = Make();

        Assert.Empty(ProgressJson.ReadTeam(Parsed(Read(rig, "progress", "read", ProgressArgs(rig, "none")))).Streams);

        _ = rig.Services.Progress.EnsureTeam(rig.Pool, "t", Info);
        TestProgressDocs.Off(rig.AhHome, rig.Pool, "t");

        Assert.True(ProgressJson.ReadTeam(Parsed(Read(rig, "progress", "read", ProgressArgs(rig, "t")))).Off);
    }

    [Fact]
    public void ProgressPlanIsTheLibraryPlanAndNullWhenAStreamHasNone()
    {
        var rig = Make();
        _ = rig.Services.Progress.EnsureTeam(rig.Pool, "t", Info);
        Assert.True(Parsed(Read(rig, "progress", "plan", ProgressArgs(rig, "t", "main"))).IsNull);

        _ = rig.Services.Progress.RecordPlan(rig.Pool, "t", "main", "me", [new PlanInput("one", "add", "First item")]);

        var viaOp = Parsed(Read(rig, "progress", "plan", ProgressArgs(rig, "t", "main")));
        Assert.Equal(Text(ProgressJson.ToJson(rig.Services.Progress.ReadPlan(rig.Pool, "t", "main")!)), Text(viaOp));
        Assert.Equal("First item", Assert.Single(ProgressJson.ReadPlan(viaOp)!.Items).Title);
    }

    [Fact]
    public void ProgressPlanWithNoTeamAndStreamBacklogIsTheBacklogWithEveryField()
    {
        var item = new PlanItem("b1", "Groom loop", "not_started", "2026-06-01T12:00:00Z", null, null, [], null, null, null) with { Wave = "now", Size = "M", Sources = ["groom-1:N1"] };
        var rig = Make(backlogPlan: dir => new PlanState([item], 0));

        var read = ProgressJson.ReadPlan(Parsed(Read(rig, "progress", "plan", ProgressArgs(rig, string.Empty, "backlog"))))!;

        var back = Assert.Single(read.Items);
        Assert.Equal(("b1", "now", "M"), (back.Item, back.Wave, back.Size));
        Assert.Equal(["groom-1:N1"], back.Sources!);
    }

    [Fact]
    public void ATeamStreamNamedBacklogIsStillItsPlanLog()
    {
        var rig = Make(backlogPlan: dir => throw new InvalidOperationException("the backlog must not be read"));
        _ = rig.Services.Progress.EnsureTeam(rig.Pool, "t", Info);
        _ = rig.Services.Progress.RecordPlan(rig.Pool, "t", "backlog", "me", [new PlanInput("one", "add", "Team item")]);

        var read = ProgressJson.ReadPlan(Parsed(Read(rig, "progress", "plan", ProgressArgs(rig, "t", "backlog"))))!;

        Assert.Equal("Team item", Assert.Single(read.Items).Title);
        Assert.True(Parsed(Read(Make(), "progress", "plan", ProgressArgs(rig, string.Empty, "backlog"))).IsNull);
    }

    [Fact]
    public void ProgressDescribeIsTheLibraryDescription()
    {
        var rig = Make();

        var viaOp = Parsed(Read(rig, "progress", "describe", ProgressArgs(rig, "t", "main")));

        Assert.Equal(Text(ProgressJson.ToJson(rig.Services.Progress.Describe(rig.Pool, "t", "main"))), Text(viaOp));
        Assert.Equal("—", ProgressJson.ReadInfo(viaOp).Branch);
    }

    [Fact]
    public void ProgressReadsRefuseAnImplausibleTeamOrStreamAndAMissingDir()
    {
        var rig = Make();

        Assert.False(Read(rig, "progress", "read", ProgressArgs(rig, "../x")).Ok);
        Assert.False(Read(rig, "progress", "read", ProgressArgs(rig, "a/b")).Ok);
        Assert.False(Read(rig, "progress", "plan", ProgressArgs(rig, "t", "a/b")).Ok);
        Assert.False(Read(rig, "progress", "describe", ProgressArgs(rig, "t")).Ok);
        Assert.False(Read(rig, "progress", "read", new JsonObject { ["team"] = "t" }).Ok);
        Assert.True(Read(rig, "progress", "read", ProgressArgs(rig, "_default")).Ok);
    }

    [Fact]
    public void StatusComputeIsTheLibraryStatusOfThePoolAtTheGivenTime()
    {
        var rig = Make();
        const double now = 1_780_000_000_000;

        var viaOp = Parsed(Read(rig, "status", "compute", new JsonObject { ["dir"] = rig.Pool, ["now_ms"] = now }));

        Assert.Equal(Text(JsonValue.FromObject(rig.Services.Status.ComputeStatus(rig.Pool, now, rig.Pool, true))), Text(viaOp));
        Assert.True(viaOp.IsMap);
    }

    [Fact]
    public void StatusComputeRefusesAMissingOrNonNumericNowAndAMissingDir()
    {
        var rig = Make();

        Assert.False(Read(rig, "status", "compute", new JsonObject { ["dir"] = rig.Pool }).Ok);
        Assert.False(Read(rig, "status", "compute", new JsonObject { ["dir"] = rig.Pool, ["now_ms"] = "1" }).Ok);
        Assert.False(Read(rig, "status", "compute", new JsonObject { ["now_ms"] = 1 }).Ok);
    }

    [Fact]
    public void MsgsTreesIsTheLibraryTreeListForTheTeamsAndRefusesTeamsThatAreNotAnArray()
    {
        var rig = Make();
        var teams = new JsonArray { "t" };

        var viaOp = Parsed(Read(rig, "msgs", "trees", new JsonObject { ["dir"] = rig.Pool, ["teams"] = teams }));

        Assert.Equal(rig.Services.Messages.Trees(rig.Pool, ["t"]), viaOp.Member("trees")!.Value.AsArray().Select(x => x.AsString()).ToList());
        Assert.False(Read(rig, "msgs", "trees", new JsonObject { ["dir"] = rig.Pool, ["teams"] = "t" }).Ok);
        Assert.False(Read(rig, "msgs", "trees", new JsonObject { ["dir"] = rig.Pool }).Ok);
    }

    [Fact]
    public void TheQuestionLogTakesItsFileOnceBeforeNewerRowsAndAnAppendDropsRowsPastThirtyDays()
    {
        var rig = Make();
        var log = Path.Combine(rig.Pool, "qa-log.jsonl");
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        string Row(string id, long at) => $"{{\"kind\":\"prompt\",\"id\":\"{id}\",\"at\":{at}}}";
        string[] Ids() => [.. Parsed(Read(rig, "decisions", "qa-log", new JsonObject { ["dir"] = rig.Pool })).Member("rows")!.Value.AsArray().Select(static r => r.Member("id")!.Value.AsString())];
        void Append(string id, long at) => Assert.True(Read(rig, "qa-log", "append", new JsonObject { ["dir"] = rig.Pool, ["row"] = JsonText.Parse(Row(id, at)) }).Ok);

        // the history written before the upgrade, a bad line among it, and a row stored since the upgrade, before the first read
        File.WriteAllText(log, Row("p1", now - 1000) + "\nnot json\n" + Row("p2", now - 500) + "\n");
        Append("p3", now);
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(rig.AhHome, rig.Pool);
        Assert.Equal(["p1", "p2", "p3"], Ids());
        Assert.Equal("p2", Parsed(Read(rig, "decisions", "qa-thread", new JsonObject { ["dir"] = rig.Pool, ["id"] = "p2" })).Member("prompt")!.Value.Member("id")!.Value.AsString());

        // negative: the file is taken once, so a line added to it later is never read; nothing is written beside it
        File.AppendAllText(log, Row("p9", now) + "\n");
        Assert.Equal(["p1", "p2", "p3"], Ids());

        // negative: a row 30 days and a minute old goes on the next append; one a minute inside the window stays
        Append("old", now - (30L * 86_400_000) - 60_000);
        Assert.Contains(Ah.StateDb.StateDbModule.CreateQaLog(rig.AhHome).Bodies(rig.Pool), static b => b.Contains("\"old\"", StringComparison.Ordinal));
        Append("p4", now);
        Assert.Equal(["p1", "p2", "p3", "p4"], Ids());
        Assert.DoesNotContain(Ah.StateDb.StateDbModule.CreateQaLog(rig.AhHome).Bodies(rig.Pool), static b => b.Contains("\"old\"", StringComparison.Ordinal));
        Append("edge", now - (30L * 86_400_000) + 60_000);
        Append("p5", now);
        Assert.Equal(["p1", "p2", "p3", "p4", "edge", "p5"], Ids());
        Assert.False(Directory.Exists(Path.Combine(rig.Pool, ".state-qa-log")));
    }

    [Fact]
    public void DecisionsPipelineRunIsNullWhenNoRunIsOpen()
    {
        var rig = Make();

        Assert.True(Parsed(Read(rig, "decisions", "pipeline-run", new JsonObject { ["dir"] = rig.Pool })).IsNull);
        Assert.True(Parsed(Read(rig, "decisions", "pipeline-run", new JsonObject { ["dir"] = rig.Pool, ["team"] = "t" })).IsNull);
        Assert.False(Read(rig, "decisions", "pipeline-run", new JsonObject()).Ok);
    }
}
