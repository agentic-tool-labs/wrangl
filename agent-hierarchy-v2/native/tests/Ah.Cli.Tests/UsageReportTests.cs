using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The usage report: its windows, the one-record-per-agent rule, role attribution and the incremental transcript scan.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class UsageReportTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private ServiceGraph? _graph;
    private IClock? _clock;

    public void Dispose() => _sandbox.Dispose();

    private ServiceGraph Graph() => _graph ??= ServiceGraph.Build(
        PlatformModule.CreateIo(null),
        new FakeEnv(
            _sandbox.Home,
            _sandbox.AhHome,
            new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home },
            new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1")),
        new FakeRunner(),
        new FakeDaemonClient());

    private string CacheFile => Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.usage-cache.json");

    private string Proj => Path.Combine(_sandbox.Root, "proj");

    private static string Iso(TimeSpan ago) => Timestamps.ToIsoString(DateTimeOffset.UtcNow - ago);

    private static string Record(string agent, string type, TimeSpan ago, long output, string session = "s1", string? projectDir = null, bool found = true) =>
        "{" +
        $"\"ts\":\"{Iso(ago)}\",\"session_id\":\"{session}\",\"agent_id\":\"{agent}\",\"agent_type\":\"{type}\"," +
        (projectDir is null ? string.Empty : $"\"project_dir\":\"{projectDir.Replace("\\", "\\\\", StringComparison.Ordinal)}\",") +
        $"\"calls\":1,\"in\":10,\"out\":{output.ToString(CultureInfo.InvariantCulture)},\"cache_read\":0,\"cache_create\":0,\"found\":{(found ? "true" : "false")}" +
        "}";

    private void Usage(params string[] lines) => TestHomeDocs.PutUsage(_sandbox.AhHome, lines);

    private static string Turn(string timestamp, long output) =>
        $"{{\"type\":\"assistant\",\"timestamp\":\"{timestamp}\",\"message\":{{\"model\":\"m\",\"usage\":{{\"input_tokens\":1,\"output_tokens\":{output.ToString(CultureInfo.InvariantCulture)},\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}}}";

    private (int Code, string Out) Run(string cwd, params string[] args)
    {
        var g = Graph();
        var stdout = new StringWriter();
        var code = CommandsModule.RunUsageReport(
            new UsageReportServices(g.Io.Files, g.Io.Writer, _clock ?? g.Io.Clock, g.Config, HierarchyModule.CreateUsage(g.Io.Files, g.Environment, Ah.StateDb.StateDbModule.CreateUsageRows(_sandbox.AhHome)), cwd, 4242),
            args,
            stdout);
        return (code, stdout.ToString());
    }

    private JsonObject Json(string cwd, params string[] args) =>
        JsonText.Parse(Run(cwd, [.. args, "--json"]).Out).AsObject();

    private static JsonObject Windowed(JsonObject report) => report["windowed"].AsObject()["roles"].AsObject();

    [Theory]
    [InlineData("week", 167, true)]
    [InlineData("week", 169, false)]
    [InlineData("month", 719, true)]
    [InlineData("month", 721, false)]
    [InlineData("all", 9600, true)]
    public void AWindowKeepsARecordOnlyWithinItsCutoff(string window, int hoursAgo, bool included)
    {
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(hoursAgo), 100));

        var roles = Windowed(Json(Proj, window));

        Assert.Equal(included, roles.ContainsKey("implementor"));
    }

    [Fact]
    public void TheDayWindowStartsAtLocalMidnight()
    {
        var now = DateTimeOffset.UtcNow;
        var day = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date;
        var midnight = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
        Usage(
            Record("before", "wrangl:reviewer", now - midnight + TimeSpan.FromMinutes(1), 100),
            Record("after", "wrangl:architect", now - midnight - TimeSpan.FromMinutes(1), 100));

        var roles = Windowed(Json(Proj, "day"));

        Assert.False(roles.ContainsKey("reviewer"));
        Assert.True(roles.ContainsKey("architect"));
    }

    /// <summary>US fall-back day: EDT (-4) until 06:00Z, EST (-5) after. Local midnight is 04:00Z, not now − now's offset (05:00Z).</summary>
    private sealed class FallBackClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public TimeSpan LocalOffset(DateTimeOffset instant) =>
            TimeSpan.FromHours(instant < new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero) ? -4 : -5);
    }

    [Fact]
    public void OnAFallBackDayTheDayWindowStartsAtMidnightInTheOffsetThenInForce()
    {
        _clock = new FallBackClock();
        var at = (int hour, int minute) => DateTimeOffset.UtcNow - new DateTimeOffset(2026, 11, 1, hour, minute, 0, TimeSpan.Zero);
        Usage(
            Record("before", "wrangl:reviewer", at(3, 59), 100),
            Record("inTheExtraHour", "wrangl:architect", at(4, 30), 100));

        var roles = Windowed(Json(Proj, "day"));

        Assert.False(roles.ContainsKey("reviewer"));
        Assert.True(roles.ContainsKey("architect"));
    }

    [Fact]
    public void AnAgentResumedAfterItsFirstRecordIsCountedOnceAtItsLatestRecord()
    {
        Usage(
            Record("a1", "wrangl:implementor", TimeSpan.FromHours(3), 100),
            Record("a1", "wrangl:implementor", TimeSpan.FromHours(2), 900),
            Record("a2", "wrangl:implementor", TimeSpan.FromHours(2), 50));

        var row = Windowed(Json(Proj, "week"))["implementor"].AsObject();

        Assert.Equal(2, row["agents"].AsNumber());
        Assert.Equal(950, row["out"].AsNumber());
    }

    [Fact]
    public void ADocsWriterSubagentIsBucketedUnderItsOwnRoleAndListedBetweenTheImplementorAndTheTaskRunner()
    {
        Usage(
            Record("a1", "wrangl:task-runner", TimeSpan.FromHours(1), 10),
            Record("a2", "wrangl:docs-writer", TimeSpan.FromHours(1), 20),
            Record("a3", "wrangl:implementor", TimeSpan.FromHours(1), 30));

        var roles = Windowed(Json(Proj, "week"));
        var table = Run(Proj, "week").Out;

        Assert.Equal(20, roles["docs-writer"].AsObject()["out"].AsNumber());
        var at = (string role) => table.IndexOf("  " + role, StringComparison.Ordinal);
        Assert.True(at("implementor") < at("docs-writer") && at("docs-writer") < at("task-runner"), table);
    }

    [Fact]
    public void AUxEngineerSubagentIsBucketedUnderItsOwnRole()
    {
        Usage(Record("a1", "wrangl:ux-engineer", TimeSpan.FromHours(1), 40));

        var roles = Windowed(Json(Proj, "week"));

        Assert.Equal(40, roles["ux-engineer"].AsObject()["out"].AsNumber());
    }

    [Fact]
    public void AnAgentTypeThatOnlyLooksLikeUxEngineerIsNotBucketedAsIt()
    {
        Usage(Record("a1", "wrangl:ux-engineer-x", TimeSpan.FromHours(1), 7));

        var roles = Windowed(Json(Proj, "week"));

        Assert.False(roles.ContainsKey("ux-engineer"));
    }

    [Fact]
    public void AnAgentTypeThatOnlyLooksLikeDocsWriterIsNotBucketedAsIt()
    {
        Usage(Record("a1", "wrangl:docs-writer-x", TimeSpan.FromHours(1), 20));

        var roles = Windowed(Json(Proj, "week"));

        Assert.False(roles.ContainsKey("docs-writer"));
    }

    [Fact]
    public void ARecordWithoutAnAgentIdIsNotCounted()
    {
        Usage("{\"ts\":\"" + Iso(TimeSpan.FromHours(1)) + "\",\"session_id\":\"s1\",\"agent_type\":\"wrangl:implementor\",\"calls\":1,\"in\":1,\"out\":1}");

        var report = Json(Proj, "week");

        Assert.Equal(0, report["records"].AsNumber());
        Assert.Empty(Windowed(report).Keys);
    }

    [Fact]
    public void ARoleDefinedInTheWorkingDirectoryIsAttributedAndElsewhereItIsOther()
    {
        _sandbox.Write(
            "proj/.claude/ah2/agent-hierarchy.json",
            "{\"version\":1,\"roles\":{\"godot-implementor\":{\"class\":\"implement\",\"agent\":\"godot-implementor\",\"label\":\"G\",\"description\":\"d\",\"routes\":\"r\",\"model\":\"opus\"}}}");
        Usage(Record("a1", "godot-implementor", TimeSpan.FromHours(1), 100));

        var known = Windowed(Json(Proj, "week"));
        var elsewhere = Windowed(Json(Path.Combine(_sandbox.Root, "other"), "week"));

        Assert.True(known.ContainsKey("godot-implementor"));
        Assert.False(known.ContainsKey("other"));
        Assert.True(elsewhere.ContainsKey("other"));
        Assert.False(elsewhere.ContainsKey("godot-implementor"));
    }

    [Fact]
    public void AMessageOverSeveralLinesCountsOnceAcrossScansAndNegativeAnotherMessageCountsAndAnOldCacheIsReadAgain()
    {
        static string Said(string id) => Turn(Iso(TimeSpan.FromHours(3)), 10).Replace("\"message\":{", "\"message\":{\"id\":\"" + id + "\",", StringComparison.Ordinal) + "\n";
        var transcripts = Path.Combine(_sandbox.Root, "transcripts");
        Directory.CreateDirectory(transcripts);
        var transcript = Path.Combine(transcripts, "s1.jsonl");
        File.WriteAllText(transcript, Said("m1") + Said("m1"));
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(2), 1, projectDir: transcripts));

        var once = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();
        Assert.Equal((1, 10), ((int)once["calls"].AsNumber(), (int)once["out"].AsNumber()));

        File.AppendAllText(transcript, Said("m1") + Said("m2"));
        var grown = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();
        Assert.Equal((2, 20), ((int)grown["calls"].AsNumber(), (int)grown["out"].AsNumber()));

        var cache = Directory.GetFiles(_sandbox.Root, "agent-hierarchy.usage-cache.json", SearchOption.AllDirectories).Single();
        var old = File.ReadAllText(cache).Replace("\"v\":2", "\"v\":1", StringComparison.Ordinal).Replace("\"out\":20", "\"out\":999", StringComparison.Ordinal);
        File.WriteAllText(cache, old);
        var read = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();
        Assert.Equal(20, read["out"].AsNumber());
    }

    [Fact]
    public void TheMainTranscriptIsSummedOnceAndOnlyFromTheLastCompleteLine()
    {
        var transcripts = Path.Combine(_sandbox.Root, "transcripts");
        Directory.CreateDirectory(transcripts);
        var transcript = Path.Combine(transcripts, "s1.jsonl");
        var first = Turn(Iso(TimeSpan.FromHours(5)), 10) + "\n" + Turn(Iso(TimeSpan.FromHours(4)), 20) + "\n";
        File.WriteAllText(transcript, first + Turn(Iso(TimeSpan.FromHours(3)), 40));
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(2), 1, projectDir: transcripts));

        var once = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();
        var again = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();
        File.AppendAllText(transcript, "\n" + Turn(Iso(TimeSpan.FromHours(1)), 80) + "\n");
        var grown = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();

        Assert.Equal(2, once["calls"].AsNumber());
        Assert.Equal(30, once["out"].AsNumber());
        Assert.Equal(2, again["calls"].AsNumber());
        Assert.Equal(4, grown["calls"].AsNumber());
        Assert.Equal(150, grown["out"].AsNumber());
    }

    [Fact]
    public void ATranscriptThatIsGoneKeepsItsCachedSums()
    {
        var transcripts = Path.Combine(_sandbox.Root, "transcripts");
        Directory.CreateDirectory(transcripts);
        var transcript = Path.Combine(transcripts, "s1.jsonl");
        File.WriteAllText(transcript, Turn(Iso(TimeSpan.FromHours(3)), 40) + "\n");
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(2), 1, projectDir: transcripts));
        _ = Json(Proj, "week");

        File.Delete(transcript);
        var row = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();

        Assert.Equal(1, row["calls"].AsNumber());
        Assert.Equal(40, row["out"].AsNumber());
    }

    [Fact]
    public void ACorruptCacheIsRebuiltFromTheTranscripts()
    {
        var transcripts = Path.Combine(_sandbox.Root, "transcripts");
        Directory.CreateDirectory(transcripts);
        File.WriteAllText(Path.Combine(transcripts, "s1.jsonl"), Turn(Iso(TimeSpan.FromHours(3)), 40) + "\n");
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(2), 1, projectDir: transcripts));
        File.WriteAllText(CacheFile, "{not json");

        var row = Windowed(Json(Proj, "week"))["orchestrator"].AsObject();

        Assert.Equal(40, row["out"].AsNumber());
        Assert.StartsWith("{\"v\":2,\"files\":{", File.ReadAllText(CacheFile), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCacheIsPersistedOnAHomeWhoseStateDirectoryDoesNotExistYet()
    {
        var stateDirectory = Path.GetDirectoryName(CacheFile)!;
        Directory.Delete(stateDirectory, recursive: true); // the sandbox made the database; this home is one no msg verb ran in yet
        Assert.False(Directory.Exists(stateDirectory));

        var (code, _) = Run(Proj, "week");

        Assert.Equal(0, code);
        Assert.StartsWith("{\"v\":2,\"files\":{", File.ReadAllText(CacheFile), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(stateDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(999949, "999.9k")]
    [InlineData(1000000, "1.0M")]
    [InlineData(2450000, "2.5M")]
    public void CountsAreShortenedAtThousandsAndMillions(long output, string shown)
    {
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(1), output));

        var row = Run(Proj, "week").Out.Split('\n').First(l => l.StartsWith("  implementor", StringComparison.Ordinal));

        Assert.Contains(" " + shown + " ", row.Replace("  ", " ", StringComparison.Ordinal) + " ", StringComparison.Ordinal);
    }

    [Fact]
    public void TheLatestSessionIsTheFirstRecordOfTheNewestTimestamp()
    {
        var at = Iso(TimeSpan.FromHours(2));
        Usage(
            "{\"ts\":\"" + at + "\",\"session_id\":\"first\",\"agent_id\":\"a1\",\"agent_type\":\"wrangl:reviewer\",\"calls\":1,\"in\":1,\"out\":1}",
            "{\"ts\":\"" + at + "\",\"session_id\":\"second\",\"agent_id\":\"a2\",\"agent_type\":\"wrangl:reviewer\",\"calls\":1,\"in\":1,\"out\":1}",
            Record("a3", "wrangl:reviewer", TimeSpan.FromHours(9), 1, session: "older"));

        var report = Json(Proj);

        Assert.Equal("first", report["latest_session"].AsString());
    }

    [Fact]
    public void TheDailyTotalsReachFourteenDaysBackAndThirtyForTheMonthWindow()
    {
        Usage(
            Record("a1", "wrangl:reviewer", TimeSpan.FromDays(13), 1),
            Record("a2", "wrangl:reviewer", TimeSpan.FromDays(15), 1),
            Record("a3", "wrangl:reviewer", TimeSpan.FromDays(29), 1),
            Record("a4", "wrangl:reviewer", TimeSpan.FromDays(31), 1));

        var byDefault = Json(Proj)["daily"].AsObject().Count;
        var byMonth = Json(Proj, "month")["daily"].AsObject().Count;

        Assert.Equal(1, byDefault);
        Assert.Equal(3, byMonth);
    }

    [Fact]
    public void WithNoUsageFileTheReportSaysNothingIsRecordedYet()
    {
        var text = Run(Proj).Out;

        Assert.Contains("(0 subagent records)", text, StringComparison.Ordinal);
        Assert.Contains("No usage recorded yet.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LAST 7 DAYS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordMissingItsTranscriptIsCountedAsAnAgentAndNamedInTheTable()
    {
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(1), 5, found: false), Record("a2", "wrangl:implementor", TimeSpan.FromHours(1), 5, found: false));

        var text = Run(Proj, "week").Out;

        Assert.Contains("(2 transcripts not found)", text, StringComparison.Ordinal);
    }

    private const string TeamJson = "{\"orchestrator\":{\"session_id\":\"lead\"},\"members\":[{\"name\":\"peer\",\"session_id\":\"p1\"},{\"name\":\"same\",\"session_id\":\"p1\"},{\"name\":\"gone\",\"session_id\":\"p9\"},{\"name\":\"new\"}]}";

    private (int Code, string Out, string Err) RunTeam(params string[] args)
    {
        var g = Graph();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var services = new UsageReportServices(g.Io.Files, g.Io.Writer, g.Io.Clock, g.Config, HierarchyModule.CreateUsage(g.Io.Files, g.Environment, Ah.StateDb.StateDbModule.CreateUsageRows(_sandbox.AhHome)), Proj, 4242)
        {
            TeamRecord = team => team == "tm" ? JsonText.Parse(TeamJson) : JsonValue.Null,
            ProjectsDir = Path.Combine(_sandbox.Home, ".claude", "projects"),
        };
        var code = CommandsModule.RunUsageReport(services, args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private void TeamTranscripts()
    {
        var a = Path.Combine(_sandbox.Home, ".claude", "projects", "-repo-a");
        var b = Path.Combine(_sandbox.Home, ".claude", "projects", "-repo-b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "lead.jsonl"), Turn("2026-01-01T00:00:00Z", 100) + "\n");
        File.WriteAllText(Path.Combine(b, "p1.jsonl"), Turn("2026-01-01T00:00:00Z", 20) + "\n");
        File.WriteAllText(Path.Combine(b, "stranger.jsonl"), Turn("2026-01-01T00:00:00Z", 5000) + "\n");
    }

    [Fact]
    public void TeamCountsEachMembersTranscriptWhereverItLivesWithItsSubagentsAndNoStranger()
    {
        TeamTranscripts();
        Usage(Record("sub", "wrangl:task-runner", TimeSpan.FromDays(400), 3, session: "p1"), Record("other", "wrangl:reviewer", TimeSpan.FromHours(1), 7000, session: "stranger"));

        var (code, stdout, _) = RunTeam("--team", "tm", "--json");

        Assert.Equal(0, code);
        var members = JsonText.Parse(stdout).AsObject()["members"].AsObject();
        Assert.Equal(100, members["orchestrator"].Member("out")!.Value.AsNumber());
        Assert.Equal(23, members["peer"].Member("out")!.Value.AsNumber());
        Assert.Equal(2, members["peer"].Member("agents")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeTeamCountsARepeatedSessionOnceAndMarksATranscriptItCannotFind()
    {
        TeamTranscripts();

        var (_, stdout, _) = RunTeam("--team", "tm", "--json");

        var members = JsonText.Parse(stdout).AsObject()["members"].AsObject();
        Assert.False(members.ContainsKey("same"));
        Assert.Equal(20, members["peer"].Member("out")!.Value.AsNumber());
        Assert.Equal(1, members["gone"].Member("missing")!.Value.AsNumber());
        Assert.Equal(1, members["new"].Member("missing")!.Value.AsNumber());
    }

    [Fact]
    public void TeamTextNamesTheMemberColumnAndWhatItLeavesOut()
    {
        TeamTranscripts();

        var (_, stdout, _) = RunTeam("--team", "tm");

        Assert.Contains("team tm", stdout, StringComparison.Ordinal);
        Assert.Contains("  member", stdout, StringComparison.Ordinal);
        Assert.Contains("every session the team had", stdout, StringComparison.Ordinal);
    }

    private static readonly long Rowed = DateTimeOffset.Parse("2026-01-01T00:05:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Fact]
    public void TeamCountsInventoryAndEndedRowsOfAClosedTeamOnceAndFleetTotalsEachSessionOnce()
    {
        TeamTranscripts();
        var b = Path.Combine(_sandbox.Home, ".claude", "projects", "-repo-b");
        File.WriteAllText(Path.Combine(b, "p2.jsonl"), Turn("2026-01-01T00:00:00Z", 7) + "\n");
        File.WriteAllText(Path.Combine(b, "p3.jsonl"), Turn("2026-01-01T00:00:00Z", 3) + "\n");
        var progress = Path.Combine(_sandbox.Root, "progress");
        Directory.CreateDirectory(Path.Combine(progress, "tm"));
        Directory.CreateDirectory(Path.Combine(progress, "tm2"));
        File.WriteAllText(
            Path.Combine(progress, "tm", "sessions.jsonl"),
            "{\"at\":\"2026-01-01T00:00:00Z\",\"member\":\"orchestrator\",\"role\":\"orchestrator\",\"session_id\":\"lead\",\"event\":\"orchestrator-start\"}\n" +
            "{\"at\":\"2026-01-01T00:01:00Z\",\"member\":\"peer\",\"role\":\"implementor\",\"session_id\":\"p1\",\"event\":\"register\"}\n");
        // the negative: p1 is in tm2's inventory too, later, so --fleet counts it under tm only
        File.WriteAllText(Path.Combine(progress, "tm2", "sessions.jsonl"), "{\"at\":\"2026-01-02T00:00:00Z\",\"member\":\"x\",\"session_id\":\"p1\"}\n");
        var g = Graph();
        var services = new UsageReportServices(g.Io.Files, g.Io.Writer, g.Io.Clock, g.Config, HierarchyModule.CreateUsage(g.Io.Files, g.Environment, Ah.StateDb.StateDbModule.CreateUsageRows(_sandbox.AhHome)), Proj, 4242)
        {
            ProjectsDir = Path.Combine(_sandbox.Home, ".claude", "projects"),
            ProgressDir = progress,
            SessionHistory = () =>
            [
                // after the orchestrator-start line, which has no row: that alone is not pruning
                new Ah.Abstractions.SessionHistoryRow("p1", "tm", "peer", Rowed, Rowed + 1),
                new Ah.Abstractions.SessionHistoryRow("p2", "tm", "old", Rowed, Rowed + 1),
                new Ah.Abstractions.SessionHistoryRow("p3", "tm2", "y", 1, null),
            ],
        };

        var team = new StringWriter();
        Assert.Equal(0, CommandsModule.RunUsageReport(services, ["--team", "tm", "--json"], team, new StringWriter()));
        var doc = JsonText.Parse(team.ToString()).AsObject();
        Assert.Equal(3, doc["sessions"].AsArray().Count);
        Assert.Equal(127, doc["total"].Member("out")!.Value.AsNumber());
        Assert.DoesNotContain(doc["notes"].AsArray(), n => n.AsString().Contains("pruned", StringComparison.Ordinal));

        var fleet = new StringWriter();
        Assert.Equal(0, CommandsModule.RunUsageReport(services, ["--fleet", "--json"], fleet, new StringWriter()));
        var teams = JsonText.Parse(fleet.ToString()).AsObject()["teams"].AsObject();
        Assert.Equal(127, teams["tm"].Member("out")!.Value.AsNumber());
        Assert.Equal(3, teams["tm2"].Member("out")!.Value.AsNumber());
        Assert.Equal(130, JsonText.Parse(fleet.ToString()).AsObject()["total"].Member("out")!.Value.AsNumber());
    }

    [Theory]
    [InlineData(2, "--team")]
    [InlineData(2, "--team", "--json")]
    [InlineData(2, "--team", "tm", "day")]
    [InlineData(1, "--team", "nope")]
    public void NegativeTeamRefusesAMissingNameAWindowOrAnUnknownTeam(int exit, params string[] args)
    {
        var (code, stdout, stderr) = RunTeam(args);

        Assert.Equal(exit, code);
        Assert.Empty(stdout);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void NegativeATeamNamedLikeAWindowIsATeamNotAWindowError()
    {
        var (code, _, stderr) = RunTeam("--team", "day");

        Assert.Equal(1, code);
        Assert.Contains("no team 'day'", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tm", 0)]
    [InlineData("../../x", 1)]
    public void TeamReadsTheRealRecordAndANameThatEscapesFindsNoTeam(string team, int exit)
    {
        var g = Graph();
        var hierarchy = Path.Combine(_sandbox.Root, "pj", ".claude", "ah2");
        _sandbox.PlantTeam(Path.Combine(hierarchy, "teams", "tm.json"), TeamJson);
        File.WriteAllText(Path.Combine(_sandbox.Root, "pj", ".claude", "x.json"), TeamJson);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var services = new UsageReportServices(g.Io.Files, g.Io.Writer, g.Io.Clock, g.Config, HierarchyModule.CreateUsage(g.Io.Files, g.Environment, Ah.StateDb.StateDbModule.CreateUsageRows(_sandbox.AhHome)), Proj, 4242)
        {
            TeamRecord = name => Ah.Cli.Verbs.UsageReportVerb.TeamRecord(g, hierarchy, name),
        };

        Assert.Equal(exit, CommandsModule.RunUsageReport(services, ["--team", team], stdout, stderr));
    }

    [Fact]
    public void NegativeWithoutTeamTheReportIsUnchanged()
    {
        Usage(Record("a1", "wrangl:implementor", TimeSpan.FromHours(1), 100));

        var text = Run(Proj, "week").Out;

        Assert.Contains("  role", text, StringComparison.Ordinal);
        Assert.DoesNotContain("  member", text, StringComparison.Ordinal);
    }
}
