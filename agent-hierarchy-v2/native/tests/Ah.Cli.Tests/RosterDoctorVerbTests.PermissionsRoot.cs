using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary>
/// Which plugin roots may be named in an allow rule, the ancestry check on the caller, and a settings file that is a link. The
/// rules auto-approve everything under <c>&lt;root&gt;/bin/ah</c>, so a root anyone else can change must never be written.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class RosterDoctorVerbTests
{
    private const UnixFileMode Group = UnixFileMode.GroupWrite;

    private string MakeRoot(string relative)
    {
        var dir = Path.Combine(_sandbox.Root, relative);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var ah = Path.Combine(dir, "bin", "ah");
        File.WriteAllText(ah, "#!/bin/sh\n");
        File.SetUnixFileMode(ah, Private);
        File.SetUnixFileMode(Path.Combine(dir, "bin"), Private);
        File.SetUnixFileMode(dir, Private);
        return RealPath(dir);
    }

    private void AssertNothingWritten(string expectedSettings)
    {
        Assert.Equal(expectedSettings, File.ReadAllText(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".ah2-bak"));
        Assert.Null(Ledger);
    }

    // ---------------------------------------------------------------- the root

    [Theory]
    [InlineData("checkout/agent-hierarchy-v2")]
    [InlineData("copy")]
    public void ARootThatTheInstallRecordDoesNotListIsRefused(string relative)
    {
        WriteSettings(Unrelated);
        _rootUnderTest = MakeRoot(relative);

        var (add, _, addErr) = Permissions("add");
        var (sync, _, _) = Permissions("sync");
        var (_, checkOut, _) = Permissions("check");

        Assert.Equal(2, add);
        Assert.Contains("not an installed version", addErr, StringComparison.Ordinal);
        Assert.Equal(2, sync);
        Assert.StartsWith("skip: ", checkOut, StringComparison.Ordinal);
        Assert.Contains("not an installed version", checkOut, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact]
    public void WithNoInstallRecordAtAllNoRootIsInstalled()
    {
        WriteSettings(Unrelated);
        File.Delete(Path.Combine(ClaudeDir, "plugins", "installed_plugins.json"));

        var (add, _, addErr) = Permissions("add");

        Assert.Equal(2, add);
        Assert.Contains("not an installed version", addErr, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    private void RecordInstalledEntries(params string[] entries)
    {
        var record = Path.Combine(ClaudeDir, "plugins", "installed_plugins.json");
        File.WriteAllText(record, $"{{\"version\":2,\"plugins\":{{\"wrangl@mkt\":[{string.Join(",", entries)}]}}}}");
    }

    private static string Entry(string root, string version, string? lastUpdated = null) =>
        $"{{\"scope\":\"user\",\"installPath\":{JsonText.Stringify(JsonValue.FromString(root), 0)},\"version\":\"{version}\"" +
        (lastUpdated is null ? "}" : $",\"lastUpdated\":\"{lastUpdated}\"}}");

    [Fact]
    public void ARefusalNamesTheNewestInstalledCopyByLastUpdatedAndTheActionBeingRun()
    {
        WriteSettings(Unrelated);
        var newest = MakeRoot("cache/wrangl/1.0.0");
        var older = MakeRoot("cache/wrangl/2.0.0");
        RecordInstalledEntries(Entry(older, "2.0.0", "2026-11-01T00:00:00.000Z"), Entry(newest, "1.0.0", "2026-11-12T00:00:00.000Z"));
        _rootUnderTest = MakeRoot("copy");

        var (_, _, addErr) = Permissions("add");
        var (sync, _, syncErr) = Permissions("sync");
        var (_, checkOut, _) = Permissions("check");

        Assert.Contains($"the installed copy is {newest}: run {newest}/bin/ah roster permissions add", addErr, StringComparison.Ordinal);
        Assert.Equal(2, sync);
        Assert.Contains($"run {newest}/bin/ah roster permissions sync", syncErr, StringComparison.Ordinal);
        Assert.Contains($"run {newest}/bin/ah roster permissions sync", checkOut, StringComparison.Ordinal);
        Assert.DoesNotContain(older, addErr + syncErr + checkOut, StringComparison.Ordinal);
        Assert.DoesNotContain("install.sh", addErr + syncErr + checkOut, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact]
    public void WithoutLastUpdatedARefusalNamesTheHighestVersion()
    {
        WriteSettings(Unrelated);
        var low = MakeRoot("cache/wrangl/1.10.0-rc1");
        var high = MakeRoot("cache/wrangl/1.10.0");
        RecordInstalledEntries(Entry(high, "1.10.0"), Entry(low, "1.10.0-rc1"));
        _rootUnderTest = MakeRoot("copy");

        var (_, _, syncErr) = Permissions("sync");

        Assert.Contains($"the installed copy is {high}: run {high}/bin/ah roster permissions sync", syncErr, StringComparison.Ordinal);
        Assert.DoesNotContain(low, syncErr, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoInstalledCopyARefusalSaysNoneIsInstalledAndNamesNoCommand()
    {
        WriteSettings(Unrelated);
        RecordInstalledEntries();
        _rootUnderTest = MakeRoot("copy");

        var (sync, _, syncErr) = Permissions("sync");

        Assert.Equal(2, sync);
        Assert.Contains("no version of wrangl is installed", syncErr, StringComparison.Ordinal);
        Assert.DoesNotContain("roster permissions", syncErr, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact]
    public void ARootThatIsOneOfSeveralRecordedInstallsIsAccepted()
    {
        RecordInstalled(MakeRoot("other/ah2"), _pluginRoot);

        var (code, _, stderr) = Permissions("add");

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(), Allow());
    }

    [Fact]
    public void ALinkToTheInstalledRootIsAcceptedAndTheRulesCarryTheRealPath()
    {
        var link = Path.Combine(_sandbox.Root, "link");
        Directory.CreateSymbolicLink(link, _pluginRoot);
        _rootUnderTest = link;

        var (code, _, stderr) = Permissions("add");

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(), Allow());
        Assert.DoesNotContain(link, string.Concat(Allow()), StringComparison.Ordinal);
    }

    [Theory(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    [InlineData("")]
    [InlineData("bin")]
    [InlineData("bin/ah")]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("../../..")]
    public void AnythingFromTheAhBinaryUpToTheCacheThatOthersCanWriteIsRefused(string relative)
    {
        WriteSettings(Unrelated);
        var path = Path.GetFullPath(Path.Combine(_pluginRoot, relative));
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | Group);

        var (add, _, addErr) = Permissions("add");
        File.SetUnixFileMode(path, Private | UnixFileMode.OtherWrite);
        var (otherAdd, _, otherErr) = Permissions("sync");

        Assert.Equal(2, add);
        Assert.Contains("writable by its group or others", addErr, StringComparison.Ordinal);
        Assert.Contains(path, addErr, StringComparison.Ordinal);
        Assert.Equal(2, otherAdd);
        Assert.Contains("writable by its group or others", otherErr, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void TheDirectoryAboveTheCacheIsNotChecked()
    {
        var plugins = Path.Combine(ClaudeDir, "plugins");
        File.SetUnixFileMode(plugins, Private | UnixFileMode.OtherWrite);

        var (code, _, stderr) = Permissions("add");

        Assert.True(code == 0, stderr);
    }

    [Fact]
    public void ARootInsideAGitWorkTreeIsRefused()
    {
        WriteSettings(Unrelated);
        Directory.CreateDirectory(Path.Combine(ClaudeDir, "plugins", ".git"));

        var (add, _, addErr) = Permissions("add");

        Assert.Equal(2, add);
        Assert.Contains("git work tree", addErr, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact]
    public void ARootOutsideThePluginCacheIsRefusedEvenWhenRecorded()
    {
        WriteSettings(Unrelated);
        _rootUnderTest = MakeRoot("elsewhere/ah2");
        RecordInstalled(_rootUnderTest);

        var (add, _, addErr) = Permissions("add");

        Assert.Equal(2, add);
        Assert.Contains("not under the plugin cache", addErr, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Theory]
    [InlineData("v(1)")]
    [InlineData("v*")]
    [InlineData("v)")]
    public void ARootWithACharacterARuleReadsAsSyntaxIsRefused(string name)
    {
        WriteSettings(Unrelated);
        InstallPlugin(Path.Combine(ClaudeDir, "plugins", "cache", "mkt", "wrangl", name));

        var (add, _, addErr) = Permissions("add");

        Assert.Equal(2, add);
        Assert.Contains("allow rule would read as syntax", addErr, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact]
    public void RemovingRulesIsNotGatedByTheRoot()
    {
        Permissions("add");
        _rootUnderTest = MakeRoot("checkout/agent-hierarchy-v2");

        var (code, _, stderr) = Permissions("remove");

        Assert.True(code == 0, stderr);
        Assert.Empty(Allow());
    }

    // ---------------------------------------------------------------- the caller's ancestry

    [Theory]
    [InlineData(NearestKind.Session)]
    [InlineData(NearestKind.Orchestrator)]
    [InlineData(NearestKind.Member)]
    [InlineData(NearestKind.Unavailable)]
    [InlineData(null)]
    public void WithTheEnvironmentStrippedAHarnessInTheAncestryOrNoReadingRefuses(NearestKind? kind)
    {
        WriteSettings(Unrelated);
        _ancestry = kind;
        var live = new Dictionary<string, string?> { ["AH_TEST"] = "0" };

        var (add, _, addErr) = Roster(live, "permissions", "add");
        var (remove, _, _) = Roster(live, "permissions", "remove");
        var (check, checkOut, _) = Roster(live, "permissions", "check");

        Assert.Equal(2, add);
        Assert.Contains("plain terminal", addErr, StringComparison.Ordinal);
        Assert.Equal(2, remove);
        Assert.Equal(0, check);
        Assert.StartsWith("skip: ", checkOut, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact]
    public void ACleanAncestryOutsideTestModeIsOffered()
    {
        _ancestry = NearestKind.None;
        var live = new Dictionary<string, string?> { ["AH_TEST"] = "0" };

        var (check, checkOut, stderr) = Roster(live, "permissions", "check");

        Assert.True(check == 0, stderr);
        Assert.Equal("ask", checkOut.Trim());
    }

    [Fact]
    public void TestModeBypassesTheAncestryCheck()
    {
        _ancestry = NearestKind.Session;

        var (code, _, stderr) = Roster(null, "permissions", "add");

        Assert.True(code == 0, stderr);
    }

    // ---------------------------------------------------------------- a settings file that is a link

    [Fact]
    public void ALinkedSettingsFileIsWrittenThroughAndStaysALink()
    {
        var target = Path.Combine(_sandbox.Root, "dotfiles", "claude", "settings.json");
        WriteSettings(Unrelated, target);
        File.CreateSymbolicLink(SettingsFile, target);

        var (code, _, stderr) = Permissions("add");

        Assert.True(code == 0, stderr);
        Assert.NotNull(new FileInfo(SettingsFile).LinkTarget);
        Assert.Equal(["Bash(ls:*)", .. AllRules()], Allow(target));
        Assert.Equal(Unrelated, File.ReadAllText(target + ".ah2-bak"));
        Assert.False(File.Exists(SettingsFile + ".ah2-bak"));
    }

    [Fact]
    public void ALinkedSettingsFileIsRemovedFromThroughTheLinkToo()
    {
        var target = Path.Combine(_sandbox.Root, "dotfiles", "claude", "settings.json");
        WriteSettings(Unrelated, target);
        File.CreateSymbolicLink(SettingsFile, target);
        Permissions("add");

        var (code, _, stderr) = Permissions("remove");

        Assert.True(code == 0, stderr);
        Assert.NotNull(new FileInfo(SettingsFile).LinkTarget);
        Assert.Equal(["Bash(ls:*)"], Allow(target));
    }

    [Fact]
    public void ALinkToNothingIsRefusedAndStaysALink()
    {
        var target = Path.Combine(_sandbox.Root, "dotfiles", "missing.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.CreateSymbolicLink(SettingsFile, target);

        var (code, _, stderr) = Permissions("add");

        Assert.Equal(2, code);
        Assert.Contains("nothing was changed", stderr, StringComparison.Ordinal);
        Assert.Equal(target, new FileInfo(SettingsFile).LinkTarget);
        Assert.False(File.Exists(target));
        Assert.Null(Ledger);
    }

    // ---------------------------------------------------------------- ownership, links, and one install record

    private uint? _userId;

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void APluginDirectoryOwnedBySomeoneElseIsRefused()
    {
        WriteSettings(Unrelated);
        _userId = PlatformModule.CurrentUid() + 1;

        var (add, _, addErr) = Permissions("add");
        var (check, checkOut, _) = Permissions("check");

        Assert.Equal(2, add);
        Assert.Contains("is not owned by you", addErr, StringComparison.Ordinal);
        Assert.Equal(0, check);
        Assert.Contains("is not owned by you", checkOut, StringComparison.Ordinal);
        AssertNothingWritten(Unrelated);
    }

    [Fact]
    public void ALinkedPluginRootWarnsInTheReportAndTheDoctorRowThatTheRulesDoNotMatchTheCliPath()
    {
        var link = Path.Combine(_sandbox.Root, "link");
        Directory.CreateSymbolicLink(link, _pluginRoot);
        _rootUnderTest = link;

        var (code, stdout, stderr) = Permissions("add");
        var (_, doctorOut, _) = Roster(null, "doctor");

        Assert.True(code == 0, stderr);
        var warnings = Parse(stdout)["warnings"].AsArray().Select(v => v.AsString()).ToList();
        Assert.Single(warnings);
        Assert.Contains("do not match the CLI path", warnings[0], StringComparison.Ordinal);
        var row = Row(Parse(doctorOut), "lifecycle-permissions");
        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("do not match the CLI path", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlinkedRootHasNoWarningAndAnOkRow()
    {
        var (code, stdout, stderr) = Permissions("add");
        var (_, doctorOut, _) = Roster(null, "doctor");

        Assert.True(code == 0, stderr);
        Assert.Empty(Parse(stdout)["warnings"].AsArray());
        Assert.Equal("ok", Row(Parse(doctorOut), "lifecycle-permissions")["status"].AsString());
    }

    [Fact]
    public void TheDoctorsInstallRowReadsTheInstallRecordFromTheClaudeConfigDir()
    {
        var config = Path.Combine(_sandbox.Root, "cfg");
        Directory.CreateDirectory(Path.Combine(config, "plugins"));
        File.Move(Path.Combine(ClaudeDir, "plugins", "installed_plugins.json"), Path.Combine(config, "plugins", "installed_plugins.json"));

        var (_, doctorOut, _) = Roster(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = config }, "doctor");

        Assert.Contains("installed path matches own root", Row(Parse(doctorOut), "install")["detail"].AsString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the rules the verb prints

    [Fact]
    public void RulesPrintsExactlyWhatAddWritesAndWritesNothing()
    {
        var (code, stdout, stderr) = Permissions("rules");

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(), stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.False(File.Exists(SettingsFile));
        Assert.Null(Ledger);
        Permissions("add");
        Assert.Equal(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), Allow());
    }

    private string StableLink => Path.Combine(_sandbox.Home, ".local", "bin", "ah");

    private void LinkStable(string pluginDir, string stampedRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StableLink)!);
        File.CreateSymbolicLink(StableLink, Path.Combine(pluginDir, "bin", "ah"));
        Directory.CreateDirectory(_sandbox.AhHome);
        File.WriteAllText(Path.Combine(_sandbox.AhHome, "install.json"), $"{{\"version\":\"1.2.3\",\"plugin_root\":{JsonText.Stringify(JsonValue.FromString(stampedRoot), 0)}}}");
    }

    [Fact]
    public void WhileTheStableLinkLeadsToTheStampedPluginTheRulesNameTheLink()
    {
        LinkStable(_pluginRoot, _pluginRoot);

        var (code, stdout, stderr) = Permissions("rules");

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(StableLink + " roster"), stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void ALinkTheStampDoesNotNameLeavesTheRulesOnThePluginRootForm()
    {
        var other = MakeRoot("other-build");
        LinkStable(other, _pluginRoot);

        var (code, stdout, stderr) = Permissions("rules");

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(), stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void ASyncMovesRulesGrantedOnThePluginRootFormToTheStableLink()
    {
        WriteSettings(Unrelated);
        Permissions("add");
        LinkStable(_pluginRoot, _pluginRoot);

        var (code, _, stderr) = Permissions("sync");

        Assert.True(code == 0, stderr);
        Assert.Equal(["Bash(ls:*)", .. AllRules(StableLink + " roster")], Allow());
    }

    [Fact]
    public void TheSixRulesForASubOrchestratorAreWrittenAndNoRuleForAVerbOutsideTheListIs()
    {
        var (code, stdout, stderr) = Permissions("rules");

        Assert.True(code == 0, stderr);
        var rules = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(23, rules.Length);
        foreach (var verb in new[] { "create", "spawn-ad-hoc", "adopt", "untrack", "stream-report", "close-approve", "checkpoint" })
        {
            Assert.Contains(RuleOf(verb), rules);
        }

        foreach (var verb in new[] { "reap", "stream-status" })
        {
            Assert.DoesNotContain(RuleOf(verb), rules);
        }
    }

    [Fact]
    public void ASyncForAnEarlierYesRefreshesTheSevenRulesItRecordedAndAddsNoneOfTheSeven()
    {
        const string Old = "/old/plugin/bin/ah roster";
        var earlier = Lifecycle.Take(7).Select(v => RuleOf(v, Old)).ToList();
        WriteSettings(JsonText.Stringify(new JsonObject { ["permissions"] = new JsonObject { ["allow"] = Array(earlier) } }, 2));
        WriteLedger("yes", earlier, "/old/plugin");

        var (code, stdout, stderr) = Permissions("sync");

        Assert.True(code == 0, stderr);
        Assert.Equal(Lifecycle.Take(7).Select(v => RuleOf(v)), Allow());
        Assert.Equal(7, Parse(Ledger!)["added"].AsArray().Count);
        var notices = Parse(stdout)["notices"].AsArray();
        Assert.Single(notices);
        Assert.Equal(
            "16 new lifecycle rules available (progress, create, spawn-ad-hoc, adopt, untrack, stream-report, close-approve, checkpoint, wake, overdue, stall-diagnose, checklist, move, all of ah, SendMessage, cd); add them with ah roster permissions add or install.sh --allow-lifecycle",
            notices[0].AsString());
    }

    [Fact]
    public void ASyncAfterAFullOptInPrintsNoNotice()
    {
        var all = AllRules().ToList();
        WriteSettings(JsonText.Stringify(new JsonObject { ["permissions"] = new JsonObject { ["allow"] = Array(all) } }, 2));
        WriteLedger("yes", all);

        var (code, stdout, stderr) = Permissions("sync");

        Assert.True(code == 0, stderr);
        Assert.Empty(Parse(stdout)["notices"].AsArray());
    }
}
