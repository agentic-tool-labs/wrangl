using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>
/// The install-stamp notice: one line when the binary's stamp is behind the plugin, and the opt-in background rebuild behind a
/// host seam. No test here starts a process: the host is a recorder.
/// </summary>
public sealed class InstallStampNoticeTests
{
    private const string PluginVersion = "0.2.0";

    private sealed class RecordingHost : IInstallStampHost
    {
        public List<(string Path, string Text)> Writes { get; } = [];

        public List<(string Script, string Home, IReadOnlyDictionary<string, string?> Environment)> Rebuilds { get; } = [];

        public void WriteStamp(string path, string text)
        {
            Writes.Add((path, text));
            File.WriteAllText(path, text);
        }

        public void StartRebuild(string script, string home, IReadOnlyDictionary<string, string?> environment) =>
            Rebuilds.Add((script, home, environment));
    }

    private sealed class NoMesh : IMeshGate
    {
        public MailboxOutcome<MeshGateVerdict> Check(MeshGateRequest request) => new(null, null);

        public string? LinkEnvelopeFor(RequestContext context, string? sessionId, string? from, double? nowMs = null) => null;

        public void RevokeLinksFor(string teamPath, IReadOnlyList<string>? names)
        {
        }
    }

    private sealed class Rig
    {
        public Rig(string contract = "1", bool withScript = true, string? spawnDepth = null, bool withPlugin = true)
        {
            Sandbox = UnitSandbox.Create();
            Home = Sandbox.Root + "/ah";
            PluginRoot = Sandbox.Root + "/plugin";
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(PluginRoot + "/scripts");
            if (withScript)
            {
                File.WriteAllText(PluginRoot + "/scripts/install.sh", "#!/bin/sh\n");
            }

            var variables = new Dictionary<string, string>
            {
                ["HOME"] = Sandbox.Home,
                ["PATH"] = "/usr/bin",
                ["AH_HOME"] = Home,
                ["AH_TEST"] = "1",
            };
            if (spawnDepth is not null)
            {
                variables["AH_SPAWN_DEPTH"] = spawnDepth;
            }

            var env = new FakeEnvironment(variables, withPlugin ? new PluginInfo(PluginRoot, "wrangl", PluginVersion, contract) : null);
            var io = PlatformModule.CreateIo(null);
            Clock = new FakeClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
            var facts = new ProcessFacts(Sandbox.Root, 4242);
            IRosterCore? roster = null;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster!, facts);
            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
            var bootstrap = HierarchyModule.CreateBootstrap(io.Writer);
            var writer = new NoWriter();
            var hierarchy = HierarchyModule.Create(
                new HierarchyEnvironment(io.Files, env, Clock, io.Random, io.Processes, config, roster, writer, facts, bootstrap) { MsgsRoute = TestMsgStore.Route(env.AhHome) });
            Services = new GateServices(env, Clock, io.Files, config, roster, hierarchy, bootstrap, writer, new RecordingHookErrorLog(), facts, new NoMesh(), ConfigModule.CreateConventions(PlatformModule.CreateProcessRunner()));
        }

        public UnitSandbox Sandbox { get; }

        public string Home { get; }

        public string PluginRoot { get; }

        public FakeClock Clock { get; }

        public GateServices Services { get; }

        public RecordingHost Host { get; } = new();

        public string StampPath => Home + "/install.json";

        public void WriteStamp(string json) => File.WriteAllText(StampPath, json);

        public IReadOnlyList<string> Lines() => HooksModule.InstallStampLines(Services, Host, PluginVersion);

        public (string Level, string Detail) Doctor() => HooksModule.AhdBuildRow(Services.Environment, Services.Files, PluginVersion, Clock.UtcNow);
    }

    private sealed class NoWriter : IStateWriter
    {
        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null) =>
            new(true, "{}"u8.ToArray());

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
            new(true, new StateResult(true, "{}"u8.ToArray()));
    }

    [Fact]
    public void ABehindContractGivesTheContractLineAndStartsNothing()
    {
        var rig = new Rig(contract: "2");
        rig.WriteStamp("""{"version":"0.2.0","contract":1,"link":"/opt/ahd"}""");
        var before = File.ReadAllText(rig.StampPath);

        var lines = rig.Lines();

        Assert.Equal(
            ["ahd in \"/opt/ahd\" is built for contract 1; this plugin needs 2; run /wrangl:hierarchy install to rebuild it"],
            lines);
        Assert.Empty(rig.Host.Writes);
        Assert.Empty(rig.Host.Rebuilds);
        Assert.Equal(before, File.ReadAllText(rig.StampPath));
    }

    [Fact]
    public void AnOlderVersionOnTheSameContractGivesTheVersionLine()
    {
        var rig = new Rig();
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"binary":"/opt/bin/ahd"}""");

        var lines = rig.Lines();

        Assert.Equal(["ahd in \"/opt/bin/ahd\" is older than the plugin (0.1.0 < 0.2.0); run /wrangl:hierarchy install to rebuild it"], lines);
    }

    [Theory]
    [InlineData("""{"version":"0.2.0","contract":1}""")]
    [InlineData("""{"version":"0.3.0","contract":1}""")]
    [InlineData("""{"contract":1}""")]
    [InlineData("""{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.2.0","at":"2026-10-11T11:55:00.000Z","state":"started"}}""")]
    public void AStampThatIsNotBehindOrIsAlreadyBeingRebuiltGivesNoLine(string stamp)
    {
        var rig = new Rig();
        rig.WriteStamp(stamp);

        Assert.Empty(rig.Lines());
        Assert.Empty(rig.Host.Writes);
        Assert.Empty(rig.Host.Rebuilds);
    }

    [Fact]
    public void NoStampFileGivesNoLine()
    {
        var rig = new Rig();

        Assert.Empty(rig.Lines());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    public void AMalformedStampThrowsAndWritesNothing(string stamp)
    {
        var rig = new Rig();
        rig.WriteStamp(stamp);

        Assert.ThrowsAny<Exception>(() => rig.Lines());
        Assert.Empty(rig.Host.Writes);
        Assert.Empty(rig.Host.Rebuilds);
    }

    [Fact]
    public void AnOptedInStampBehindThePluginRecordsTheStartAndStartsTheRebuildOnce()
    {
        var rig = new Rig();
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"auto_rebuild":true,"link":"/opt/ahd"}""");

        var first = rig.Lines();

        Assert.Equal(["ahd is being rebuilt in the background for 0.2.0"], first);
        var text = Assert.Single(rig.Host.Writes).Text;
        Assert.Matches("""^\{"version":"0\.1\.0",.*"last_rebuild":\{"version":"0\.2\.0","at":"[^"]*","state":"started"\}\}\n$""", text);
        var written = JsonText.Parse(text);
        Assert.Equal("started", written.Member("last_rebuild")!.Value.Member("state")!.Value.AsString());
        Assert.Equal("0.2.0", written.Member("last_rebuild")!.Value.Member("version")!.Value.AsString());
        Assert.Equal("0.1.0", written.Member("version")!.Value.AsString());
        var rebuild = Assert.Single(rig.Host.Rebuilds);
        Assert.Equal(rig.PluginRoot + "/scripts/install.sh", rebuild.Script);
        Assert.Equal(rig.Home, rebuild.Home);
        Assert.Equal("1", rebuild.Environment["AH_SPAWN_DEPTH"]);
        Assert.Equal(rig.Home, rebuild.Environment["AH_HOME"]);

        var second = rig.Lines();

        Assert.Empty(second);
        Assert.Single(rig.Host.Rebuilds);
    }

    [Fact]
    public void ARebuildStartedMoreThanADayAgoIsStartedAgain()
    {
        var rig = new Rig();
        rig.WriteStamp(
            """{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.2.0","at":"2026-10-10T11:59:59.000Z","state":"started"}}""");

        Assert.Equal(["ahd is being rebuilt in the background for 0.2.0"], rig.Lines());
        Assert.Single(rig.Host.Rebuilds);
    }

    [Fact]
    public void ARebuildStartedExactlyADayAgoIsStartedAgain()
    {
        var rig = new Rig();
        rig.WriteStamp(
            """{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.2.0","at":"2026-10-10T12:00:00.000Z","state":"started"}}""");

        Assert.Single(rig.Lines());
        Assert.Single(rig.Host.Rebuilds);
    }

    [Fact]
    public void ARebuildStartedOneMillisecondInsideFifteenMinutesIsInProgress()
    {
        var rig = new Rig();
        rig.WriteStamp(
            """{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.2.0","at":"2026-10-11T11:45:00.001Z","state":"started"}}""");

        Assert.Empty(rig.Lines());
        Assert.Empty(rig.Host.Rebuilds);
        Assert.Equal("ok", rig.Doctor().Level);
        Assert.Contains("in progress", rig.Doctor().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ARebuildStartedTwentyMinutesAgoIsStalledAndStartedAgain()
    {
        var rig = new Rig();
        rig.WriteStamp(
            """{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.2.0","at":"2026-10-11T11:40:00.000Z","state":"started"}}""");

        var doctor = rig.Doctor();
        Assert.Equal("warn", doctor.Level);
        Assert.Contains("started 20m ago never finished", doctor.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Host.Rebuilds);

        Assert.Equal(["ahd is being rebuilt in the background for 0.2.0"], rig.Lines());
        Assert.Single(rig.Host.Rebuilds);
    }

    [Fact]
    public void ARebuildForAnotherPluginVersionDoesNotBlockThisOne()
    {
        var rig = new Rig();
        rig.WriteStamp(
            """{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.1.5","at":"2026-10-11T11:59:00.000Z","state":"started"}}""");

        Assert.Single(rig.Lines());
        Assert.Single(rig.Host.Rebuilds);
    }

    [Fact]
    public void ARecentFailedRebuildGivesTheFailureLineAndStartsNothing()
    {
        var rig = new Rig();
        rig.WriteStamp(
            """{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.2.0","at":"2026-10-11T11:00:00.000Z","state":"failed"}}""");

        var lines = rig.Lines();

        Assert.Equal([$"the background rebuild of ahd failed; see \"{rig.Home}/logs/rebuild.log\""], lines);
        Assert.Empty(rig.Host.Rebuilds);
        Assert.Empty(rig.Host.Writes);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("false")]
    [InlineData("null")]
    public void OnlyABooleanTrueOptsIntoTheRebuild(string autoRebuild)
    {
        var rig = new Rig();
        rig.WriteStamp($$"""{"version":"0.1.0","contract":1,"auto_rebuild":{{autoRebuild}}}""");

        var lines = rig.Lines();

        Assert.Single(lines);
        Assert.StartsWith("ahd in \"ahd\" is older than the plugin", lines[0], StringComparison.Ordinal);
        Assert.Empty(rig.Host.Rebuilds);
        Assert.Empty(rig.Host.Writes);
    }

    [Fact]
    public void AMissingInstallScriptStartsNothingRecordsNothingAndSaysWhy()
    {
        var rig = new Rig(withScript: false);
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"auto_rebuild":true}""");

        Assert.Equal(
            [$"ahd in \"ahd\" is older than the plugin (0.1.0 < 0.2.0); it was not rebuilt in the background because the install script \"{rig.PluginRoot}/scripts/install.sh\" is missing; run /wrangl:hierarchy update"],
            rig.Lines());
        Assert.Empty(rig.Host.Rebuilds);
        Assert.Empty(rig.Host.Writes);
    }

    [Fact]
    public void AnUnknownPluginRootStartsNothingAndSaysWhy()
    {
        var rig = new Rig(withPlugin: false);
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"auto_rebuild":true}""");

        var line = Assert.Single(rig.Lines());
        Assert.EndsWith("because the plugin root is not known; run /wrangl:hierarchy update", line, StringComparison.Ordinal);
        Assert.Empty(rig.Host.Rebuilds);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    public void ASpawnDepthAboveZeroRefusesTheRebuildAndSaysWhy(string depth)
    {
        var rig = new Rig(spawnDepth: depth);
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"auto_rebuild":true}""");

        Assert.Equal(
            [$"ahd in \"ahd\" is older than the plugin (0.1.0 < 0.2.0); it was not rebuilt in the background because this process was started by another ah process (AH_SPAWN_DEPTH={depth}); run /wrangl:hierarchy update"],
            rig.Lines());
        Assert.Empty(rig.Host.Rebuilds);
        Assert.Empty(rig.Host.Writes);
    }

    [Fact]
    public void DoctorOnACurrentStampIsOkAndNamesBothVersions()
    {
        var rig = new Rig();
        rig.WriteStamp("""{"version":"0.2.0","contract":1}""");

        Assert.Equal(("ok", "stamp 0.2.0 contract 1, plugin 0.2.0 contract 1"), rig.Doctor());
    }

    [Fact]
    public void DoctorWithoutAStampSaysNotInstalledAndTheInstallFix()
    {
        var rig = new Rig();

        var (level, detail) = rig.Doctor();

        Assert.Equal("warn", level);
        Assert.Equal($"not installed: no \"{rig.StampPath}\"; run /wrangl:hierarchy install", detail);
        Assert.False(File.Exists(rig.StampPath));
    }

    [Fact]
    public void DoctorOnABehindStampWarnsWithReasonAndFixAndWritesNothing()
    {
        var rig = new Rig();
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"auto_rebuild":true}""");
        var before = File.ReadAllText(rig.StampPath);

        Assert.Equal(
            ("warn", "stamp 0.1.0 contract 1, plugin 0.2.0 contract 1; no background rebuild has started yet; the next session start starts one; run /wrangl:hierarchy update"),
            rig.Doctor());
        Assert.Equal(before, File.ReadAllText(rig.StampPath));
        Assert.Empty(rig.Host.Rebuilds);
    }

    [Fact]
    public void DoctorWithAutoRebuildOffWarnsAutoOff()
    {
        var rig = new Rig();
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"auto_rebuild":false}""");

        var (level, detail) = rig.Doctor();

        Assert.Equal("warn", level);
        Assert.Contains("auto_rebuild is off", detail, StringComparison.Ordinal);
        Assert.EndsWith("run /wrangl:hierarchy update", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorOnARecentFailureNamesTheLogAndTheUpdateFix()
    {
        var rig = new Rig();
        rig.WriteStamp(
            """{"version":"0.1.0","contract":1,"auto_rebuild":true,"last_rebuild":{"version":"0.2.0","at":"2026-10-11T11:00:00.000Z","state":"failed"}}""");

        Assert.Equal(
            ("warn", $"stamp 0.1.0 contract 1, plugin 0.2.0 contract 1; the background rebuild failed 1h ago; see \"{rig.Home}/logs/rebuild.log\"; run /wrangl:hierarchy update"),
            rig.Doctor());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void ASpawnDepthThatIsZeroOrNotAPlainIntegerCountsAsZero(string depth)
    {
        var rig = new Rig(spawnDepth: depth);
        rig.WriteStamp("""{"version":"0.1.0","contract":1,"auto_rebuild":true}""");

        Assert.Single(rig.Lines());
        Assert.Single(rig.Host.Rebuilds);
    }

    [Fact]
    public void ALinkWithQuotesControlCharactersAndALongTailRendersQuotedCappedAndControlFree()
    {
        var rig = new Rig(contract: "2");
        var link = "/opt/\"ahd\"\n\u001b[31m" + new string('x', 5000);
        rig.WriteStamp(JsonText.Stringify(new JsonObject { ["version"] = "0.2.0", ["contract"] = 1, ["link"] = link }, 0));

        var line = Assert.Single(rig.Lines());

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\u001b', line);
        Assert.StartsWith("ahd in \"/opt/\\\"ahd\\\"[31m", line, StringComparison.Ordinal);
        Assert.True(line.Length < 400, $"the line is {line.Length} characters");
        Assert.EndsWith("; run /wrangl:hierarchy install to rebuild it", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkOfExactlyTheCapIsKeptWholeAndOneOverIsCut()
    {
        var rig = new Rig(contract: "2");
        rig.WriteStamp(JsonText.Stringify(new JsonObject { ["version"] = "0.2.0", ["contract"] = 1, ["link"] = new string('a', 200) }, 0));
        Assert.Contains(new string('a', 200) + "\"", Assert.Single(rig.Lines()), StringComparison.Ordinal);

        rig.WriteStamp(JsonText.Stringify(new JsonObject { ["version"] = "0.2.0", ["contract"] = 1, ["link"] = new string('a', 201) }, 0));
        var cut = Assert.Single(rig.Lines());

        Assert.Contains(new string('a', 200) + "\"", cut, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 201), cut, StringComparison.Ordinal);
    }

    private static string WriteHerdrMarker(Rig rig, string text)
    {
        var dir = rig.Sandbox.Home + "/.config/herdr-app/mods/ah2";
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/.ah2-owned", text);
        return dir + "/.ah2-owned";
    }

    [Fact]
    public void AnOlderHerdrModMarkerGivesTheModLineWithoutAStamp()
    {
        var rig = new Rig();
        var marker = WriteHerdrMarker(rig, "0.1.0\nmods/ah2/index.html sha256 ab\n");
        var before = File.ReadAllText(marker);

        Assert.Equal(
            ["the Wrangl mod in herdr desktop is older than the plugin (0.1.0 < 0.2.0); run `ah herdr install` to update it"],
            HooksModule.HerdrModLines(rig.Services, PluginVersion));
        Assert.Equal(before, File.ReadAllText(marker));
    }

    [Fact]
    public void ACurrentOrNewerHerdrModMarkerGivesNothing()
    {
        var rig = new Rig();
        WriteHerdrMarker(rig, "0.2.0\n");
        Assert.Empty(HooksModule.HerdrModLines(rig.Services, PluginVersion));

        WriteHerdrMarker(rig, "0.3.0\n");
        Assert.Empty(HooksModule.HerdrModLines(rig.Services, PluginVersion));
    }

    [Fact]
    public void NoHerdrModMarkerGivesNothing()
    {
        var rig = new Rig();

        Assert.Empty(HooksModule.HerdrModLines(rig.Services, PluginVersion));
    }

    [Fact]
    public void AnEmptyNonVersionOrUnreadableHerdrModMarkerGivesNothingAndDoesNotThrow()
    {
        var rig = new Rig();
        WriteHerdrMarker(rig, string.Empty);
        Assert.Empty(HooksModule.HerdrModLines(rig.Services, PluginVersion));

        WriteHerdrMarker(rig, "not-a-version\n");
        Assert.Empty(HooksModule.HerdrModLines(rig.Services, PluginVersion));

        // Non-ASCII digits are not a version (Versions.Compare would rank them 0.0.0).
        WriteHerdrMarker(rig, "٢.٠\n");
        Assert.Empty(HooksModule.HerdrModLines(rig.Services, PluginVersion));

        // A directory where the marker file should be: Exists says yes, the read fails.
        var dir = rig.Sandbox.Home + "/.config/herdr-app/mods/ah2/.ah2-owned";
        File.Delete(dir);
        Directory.CreateDirectory(dir);
        Assert.Empty(HooksModule.HerdrModLines(rig.Services, PluginVersion));
    }
}
