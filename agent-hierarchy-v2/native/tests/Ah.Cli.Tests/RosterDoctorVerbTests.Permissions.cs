using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary>
/// <c>ah roster permissions</c> and the doctor's <c>lifecycle-permissions</c> row: the allow rules for the Orchestrator's lifecycle
/// commands in a sandbox user settings file. Nothing here reads or writes the real <c>~/.claude</c>: the home, the state home and
/// the Claude config directory are all inside the sandbox.
/// </summary>
public sealed partial class RosterDoctorVerbTests
{
    private static readonly string[] Lifecycle = ["disband", "dismiss", "resume", "snapshot", "spawn-one", "stream-open", "stream-done", "progress", "create", "spawn-ad-hoc", "adopt", "untrack", "stream-report", "close-approve", "checkpoint", "wake", "overdue", "stall-diagnose", "checklist", "move"];

    private string SettingsFile => Path.Combine(_sandbox.Home, ".claude", "settings.json");

    /// <summary>The permissions ledger row (b397 group 2: a <c>meta</c> row), or null.</summary>
    private string? Ledger => Ah.TestKit.TestHomeDocs.Text(_sandbox.AhHome, Ah.TestKit.TestHomeDocs.Permissions);

    /// <summary>The installed plugin version the rules name: real path, plugin-manager record, owned by the user and writable by nobody else.</summary>
    private string _pluginRoot = string.Empty;

    /// <summary>A root to run with instead of the installed one.</summary>
    private string? _rootUnderTest;

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static string RealPath(string path) => PlatformModule.CreateIo(null).Files.RealPath(path);

    private string ClaudeDir => Path.Combine(_sandbox.Home, ".claude");

    /// <summary>Makes <paramref name="dir"/> a plugin version with an executable <c>bin/ah</c>, every directory above it up to the cache mode 755, and records it as installed.</summary>
    private void InstallPlugin(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var ah = Path.Combine(dir, "bin", "ah");
        File.WriteAllText(ah, "#!/bin/sh\n");
        File.SetUnixFileMode(ah, Private);
        for (var d = Path.Combine(dir, "bin"); d.Length > Path.Combine(ClaudeDir, "plugins").Length; d = Path.GetDirectoryName(d)!)
        {
            File.SetUnixFileMode(d, Private);
        }

        _pluginRoot = RealPath(dir);
        RecordInstalled(_pluginRoot);
    }

    private void RecordInstalled(params string[] roots)
    {
        var records = string.Join(",", roots.Select(r => $"{{\"scope\":\"user\",\"installPath\":{JsonText.Stringify(JsonValue.FromString(r), 0)},\"version\":\"1.2.3\"}}"));
        var record = Path.Combine(ClaudeDir, "plugins", "installed_plugins.json");
        File.WriteAllText(record, $"{{\"version\":2,\"plugins\":{{\"wrangl@mkt\":[{records}]}}}}");
    }

    private string PluginPrefix => _pluginRoot + "/bin/ah roster";

    private string RuleOf(string verb, string? prefix = null) => $"Bash({prefix ?? PluginPrefix} {verb}:*)";

    /// <summary>Every rule <c>add</c> writes, in order: one per lifecycle verb, then all of ah, then SendMessage, then cd.</summary>
    private IEnumerable<string> AllRules(string? prefix = null) =>
        [.. Lifecycle.Select(v => RuleOf(v, prefix)), $"Bash({(prefix ?? PluginPrefix)[..^" roster".Length]}:*)", "SendMessage", "Bash(cd:*)"];

    private void WriteSettings(string json, string? path = null)
    {
        path ??= SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private List<string> Allow(string? path = null) =>
        Parse(File.ReadAllText(path ?? SettingsFile))["permissions"].AsObject()["allow"].AsArray().Select(v => v.AsString()).ToList();

    private void WriteLedger(string answer, IEnumerable<string> added, string? root = null)
    {
        var rules = string.Join(",", added.Select(r => JsonText.Stringify(JsonValue.FromString(r), 0)));
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.TestKit.TestHomeDocs.Permissions, $"{{\"v\":1,\"added\":[{rules}],\"plugin_root\":{JsonText.Stringify(JsonValue.FromString(root ?? _pluginRoot), 0)},\"at\":\"2026-01-01T00:00:00.000Z\",\"answer\":\"{answer}\"}}");
    }

    private (int Code, string Stdout, string Stderr) Permissions(string action, Dictionary<string, string?>? extra = null)
    {
        _ancestry ??= NearestKind.Session;

        // the ledger is a meta row: the verb writes it into the home's database, which only the daemon creates
        using (Ah.StateDb.StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
        }

        return Roster(extra, "permissions", action);
    }

    private const string Unrelated = "{\"model\":\"opus\",\"permissions\":{\"allow\":[\"Bash(ls:*)\"],\"deny\":[\"Bash(rm:*)\"]},\"env\":{\"A\":\"1\"}}";

    // ---------------------------------------------------------------- add

    [Fact]
    public void AddAppendsEveryRuleKeepsEveryOtherKeyAndRuleAndBacksUpTheOriginal()
    {
        WriteSettings(Unrelated);

        var (code, stdout, stderr) = Permissions("add");

        Assert.True(code == 0, stderr);
        Assert.Equal(["Bash(ls:*)", .. AllRules()], Allow());
        var settings = Parse(File.ReadAllText(SettingsFile));
        Assert.Equal("opus", settings["model"].AsString());
        Assert.Equal("Bash(rm:*)", settings["permissions"].AsObject()["deny"].AsArray()[0].AsString());
        Assert.Equal("1", settings["env"].AsObject()["A"].AsString());
        Assert.Equal(Unrelated, File.ReadAllText(SettingsFile + ".ah2-bak"));
        var ledger = Parse(Ledger!);
        Assert.Equal("yes", ledger["answer"].AsString());
        Assert.Equal(_pluginRoot, ledger["plugin_root"].AsString());
        Assert.Equal(AllRules(), ledger["added"].AsArray().Select(v => v.AsString()));
        var report = Parse(stdout);
        Assert.True(report["changed"].AsBool());
        Assert.Equal(23, report["added"].AsArray().Count);
    }

    [Fact]
    public void AddCreatesTheSettingsFileWhenThereIsNoneAndMakesNoBackup()
    {
        var (code, _, stderr) = Permissions("add");

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(), Allow());
        Assert.False(File.Exists(SettingsFile + ".ah2-bak"));
    }

    [Fact]
    public void AddTwiceChangesNothingTheSecondTime()
    {
        WriteSettings(Unrelated);
        Permissions("add");
        var after = File.ReadAllText(SettingsFile);
        File.Delete(SettingsFile + ".ah2-bak");

        var (code, stdout, _) = Permissions("add");

        Assert.Equal(0, code);
        Assert.False(Parse(stdout)["changed"].AsBool());
        Assert.Equal(after, File.ReadAllText(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".ah2-bak"));
    }

    [Fact]
    public void SyncAddsNoRuleForAVerbTheEarlierYesNeverCoveredWhileAddDoes()
    {
        var earlier = Lifecycle.Where(v => v != "progress").Select(v => RuleOf(v)).ToList();
        WriteSettings($"{{\"permissions\":{{\"allow\":[{string.Join(",", earlier.Select(r => JsonText.Stringify(JsonValue.FromString(r), 0)))}]}}}}");
        WriteLedger("yes", earlier, _pluginRoot);

        var (code, stdout, stderr) = Permissions("sync");

        Assert.True(code == 0, stderr);
        Assert.Empty(Parse(stdout)["added"].AsArray());
        Assert.DoesNotContain(RuleOf("progress"), Allow());

        Permissions("add");
        Assert.Contains(RuleOf("progress"), Allow());
    }

    [Fact]
    public void AddDoesNotRecordARuleTheUserAlreadyHadAndRemoveLeavesItBehind()
    {
        WriteSettings($"{{\"permissions\":{{\"allow\":[{JsonText.Stringify(JsonValue.FromString(RuleOf("snapshot")), 0)}]}}}}");

        Permissions("add");
        var added = Parse(Ledger!)["added"].AsArray().Select(v => v.AsString()).ToList();
        Permissions("remove");

        Assert.DoesNotContain(RuleOf("snapshot"), added);
        Assert.Equal(22, added.Count);
        Assert.Equal([RuleOf("snapshot")], Allow());
    }

    [Fact]
    public void TheSettingsFileComesFromTheClaudeConfigDirWhenOneIsGiven()
    {
        var config = Path.Combine(_sandbox.Root, "cfg");
        Directory.CreateDirectory(config);
        Directory.CreateSymbolicLink(Path.Combine(config, "plugins"), Path.Combine(ClaudeDir, "plugins"));

        var (code, _, stderr) = Permissions("add", new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = config });

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(), Allow(Path.Combine(config, "settings.json")));
        Assert.False(File.Exists(SettingsFile));
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"permissions\":[]}")]
    [InlineData("{\"permissions\":{\"allow\":\"x\"}}")]
    [InlineData("{ // a comment\n}")]
    public void AMalformedSettingsFileIsRefusedAndLeftByteIdentical(string content)
    {
        WriteSettings(content);

        var (addCode, _, addErr) = Permissions("add");
        var (removeCode, _, _) = Permissions("remove");

        Assert.Equal(2, addCode);
        Assert.Contains("nothing was changed", addErr, StringComparison.Ordinal);
        Assert.Equal(2, removeCode);
        Assert.Equal(content, File.ReadAllText(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".ah2-bak"));
        Assert.Null(Ledger);
    }

    [Fact]
    public void ASettingsFileWithAnEmptyObjectIsNotMalformed()
    {
        WriteSettings("{}");

        var (code, _, stderr) = Permissions("add");

        Assert.True(code == 0, stderr);
        Assert.Equal(AllRules(), Allow());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(NearestKind.Unavailable)]
    public void AddIsRefusedWhenTheOwnerIsNotProvedByAncestry(NearestKind? kind)
    {
        _ancestry = kind;
        WriteSettings(Unrelated);

        var (code, _, stderr) = Roster(null, "permissions", "add");
        var (syncCode, _, _) = Roster(null, "permissions", "sync");
        var (checkCode, checkOut, _) = Roster(null, "permissions", "check");

        Assert.Equal(2, code);
        Assert.Contains("owner-proof", stderr, StringComparison.Ordinal);
        Assert.Equal(2, syncCode);
        Assert.Equal(0, checkCode);
        Assert.StartsWith("skip: the owner-proof doctor row is warn", checkOut, StringComparison.Ordinal);
        Assert.Equal(Unrelated, File.ReadAllText(SettingsFile));
        Assert.Null(Ledger);
    }

    [Fact]
    public void RemoveStillWorksWhenTheOwnerIsNotProvedBecauseRemovingNeedsNoProof()
    {
        Permissions("add");
        _ancestry = NearestKind.Unavailable;

        var (code, _, stderr) = Roster(null, "permissions", "remove");

        Assert.True(code == 0, stderr);
        Assert.Empty(Allow());
    }

    [Fact]
    public void AnAgentSessionNeverTouchesTheSettingsAndANormalOneDoes()
    {
        WriteSettings(Unrelated);
        var depth = new Dictionary<string, string?> { ["AH_SPAWN_DEPTH"] = "1" };

        var (add, _, addErr) = Permissions("add", depth);
        var (sync, _, _) = Permissions("sync", depth);
        var (remove, _, _) = Permissions("remove", depth);
        var (check, checkOut, _) = Permissions("check", depth);

        Assert.Equal(2, add);
        Assert.Contains("agent session", addErr, StringComparison.Ordinal);
        Assert.Equal(2, sync);
        Assert.Equal(2, remove);
        Assert.Equal(0, check);
        Assert.StartsWith("skip: this command was started by an ah agent session", checkOut, StringComparison.Ordinal);
        Assert.Equal(Unrelated, File.ReadAllText(SettingsFile));
        Assert.Null(Ledger);
    }

    [Theory]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    public void AClaudeSessionIsRefusedOutsideTestModeAndNotInIt(string variable)
    {
        WriteSettings(Unrelated);
        var live = new Dictionary<string, string?> { ["AH_TEST"] = "0", [variable] = "1" };
        var testMode = new Dictionary<string, string?> { [variable] = "1" };

        var (refused, _, err) = Permissions("add", live);
        var untouched = File.ReadAllText(SettingsFile);
        var (allowed, _, _) = Permissions("add", testMode);

        Assert.Equal(2, refused);
        Assert.Contains("Claude Code session", err, StringComparison.Ordinal);
        Assert.Equal(Unrelated, untouched);
        Assert.Equal(0, allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("add remove")]
    [InlineData("bogus")]
    public void AnActionThatIsMissingOrUnknownIsAUsageError(string action)
    {
        _ancestry = NearestKind.Session;
        var args = (string[])["permissions", .. action.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

        var (code, _, stderr) = Roster(null, args);

        Assert.Equal(2, code);
        Assert.Contains("permissions needs one action", stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- check, sync, remove

    [Fact]
    public void CheckSaysAskThenSyncAfterYesThenSkipAfterNo()
    {
        var (_, first, _) = Permissions("check");
        Permissions("add");
        var (_, second, _) = Permissions("check");
        Permissions("remove");
        var (_, third, _) = Permissions("check");

        Assert.Equal("ask\n", first.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("sync\n", second.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("skip: the user answered no\n", third.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void SyncMovesTheRecordedRulesToTheNewRootAndLeavesAHandEditedOneAlone()
    {
        const string Old = "/old/plugin/bin/ah roster";
        var handEdited = RuleOf("snapshot", Old).Replace(":*)", " --team x:*)", StringComparison.Ordinal);
        WriteSettings(JsonText.Stringify(new JsonObject
        {
            ["permissions"] = new JsonObject { ["allow"] = Array(["Bash(ls:*)", .. AllRules(Old).Where(r => !r.Contains(" snapshot:", StringComparison.Ordinal)), handEdited]) },
        }, 2));
        WriteLedger("yes", AllRules(Old), "/old/plugin");

        var (code, stdout, stderr) = Permissions("sync");

        Assert.True(code == 0, stderr);
        var expected = AllRules().Where(r => !r.Contains(" snapshot:", StringComparison.Ordinal));
        Assert.Equal(["Bash(ls:*)", .. expected, handEdited], Allow());
        var report = Parse(stdout);
        Assert.Equal([RuleOf("snapshot", Old)], report["left"].AsArray().Select(v => v.AsString()));
        Assert.Equal(20, report["added"].AsArray().Count);
        var ledger = Parse(Ledger!);
        Assert.Equal(_pluginRoot, ledger["plugin_root"].AsString());
        Assert.Equal(expected, ledger["added"].AsArray().Select(v => v.AsString()));
        Assert.True(File.Exists(SettingsFile + ".ah2-bak"));
    }

    private static JsonArray Array(IEnumerable<string> items)
    {
        var array = new JsonArray();
        array.AddRange(items.Select(i => (JsonValue)i));
        return array;
    }

    [Fact]
    public void SyncAddsNothingWhenTheUserNeverSaidYesOrSaidNo()
    {
        WriteSettings(Unrelated);

        var (neverCode, neverOut, _) = Permissions("sync");
        WriteLedger("no", []);
        var (noCode, noOut, _) = Permissions("sync");

        Assert.Equal(0, neverCode);
        Assert.False(Parse(neverOut)["changed"].AsBool());
        Assert.Equal(0, noCode);
        Assert.False(Parse(noOut)["changed"].AsBool());
        Assert.Equal(Unrelated, File.ReadAllText(SettingsFile));
        Assert.Equal("no", Parse(Ledger!)["answer"].AsString());
    }

    [Fact]
    public void SyncDoesNotPutBackARuleTheUserRemovedFromTheCurrentRoot()
    {
        Permissions("add");
        var rules = Allow().Where(r => !r.Contains(" resume:", StringComparison.Ordinal)).ToList();
        WriteSettings(JsonText.Stringify(new JsonObject { ["permissions"] = new JsonObject { ["allow"] = Array(rules) } }, 2));

        var (code, stdout, _) = Permissions("sync");

        Assert.Equal(0, code);
        Assert.Equal(rules, Allow());
        Assert.Equal([RuleOf("resume")], Parse(stdout)["left"].AsArray().Select(v => v.AsString()));
    }

    /// <summary>A ledger the database cannot read is never taken for no ledger: the write is refused and settings.json is left as it was.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnreadableLedgerRefusesTheWrite(bool unreadable)
    {
        WriteSettings(Unrelated);
        Assert.Equal(0, Permissions("add").Code);
        var before = File.ReadAllText(SettingsFile);
        if (unreadable)
        {
            _homeDocsOverride = new UnreadableHomeDocs();
        }

        var (code, _, stderr) = Permissions("remove");

        if (unreadable)
        {
            Assert.NotEqual(0, code);
            Assert.Contains("ledger cannot be read", stderr, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(SettingsFile));
        }
        else
        {
            Assert.True(code == 0, stderr);
            Assert.Equal(["Bash(ls:*)"], Allow());
        }
    }

    private sealed class UnreadableHomeDocs : Ah.Abstractions.IHomeDocs
    {
        public string? Read(string key) => null;

        public bool TryRead(string key, out string? body)
        {
            body = null;
            return false;
        }

        public void Write(string key, string? body) => throw new InvalidOperationException("must not be written");
    }

    [Fact]
    public void RemoveTakesOutExactlyTheLedgerRulesAndNoOthers()
    {
        WriteSettings(Unrelated);
        Permissions("add");
        File.Delete(SettingsFile + ".ah2-bak");
        var nearMiss = RuleOf("disband").Replace(" disband:*)", " disbandx:*)", StringComparison.Ordinal);
        var allow = Allow();
        allow.Add(nearMiss);
        allow.Add("Bash(cdx:*)");
        WriteSettings(JsonText.Stringify(new JsonObject { ["permissions"] = new JsonObject { ["allow"] = Array(allow) } }, 2));

        var (code, stdout, stderr) = Permissions("remove");

        Assert.True(code == 0, stderr);
        Assert.Equal(["Bash(ls:*)", nearMiss, "Bash(cdx:*)"], Allow());
        Assert.Equal(23, Parse(stdout)["removed"].AsArray().Count);
        var ledger = Parse(Ledger!);
        Assert.Equal("no", ledger["answer"].AsString());
        Assert.Empty(ledger["added"].AsArray());
        Assert.True(File.Exists(SettingsFile + ".ah2-bak"));
    }

    [Fact]
    public void RemoveWithNoLedgerChangesNoSettingsAndRecordsNo()
    {
        WriteSettings(Unrelated);

        var (code, stdout, _) = Permissions("remove");

        Assert.Equal(0, code);
        Assert.False(Parse(stdout)["changed"].AsBool());
        Assert.Equal(Unrelated, File.ReadAllText(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".ah2-bak"));
        Assert.Equal("no", Parse(Ledger!)["answer"].AsString());
    }

    // ---------------------------------------------------------------- doctor

    private JsonObject LifecycleRow() => Row(Parse(Roster(null, "doctor").Stdout), "lifecycle-permissions");

    [Fact]
    public void DoctorSaysTheLifecycleRulesAreOkWhenNeverAskedOrAnsweredNoAndWritesNothing()
    {
        _ancestry = NearestKind.Session;
        WriteSettings(Unrelated);

        var never = LifecycleRow();
        WriteLedger("no", []);
        var before = Tree();
        var answeredNo = LifecycleRow();

        Assert.Equal("ok", never["status"].AsString());
        Assert.Equal("not asked", never["detail"].AsString());
        Assert.Equal("ok", answeredNo["status"].AsString());
        Assert.Equal("answer no", answeredNo["detail"].AsString());
        Assert.Equal(Unrelated, File.ReadAllText(SettingsFile));
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void DoctorSaysOkWhenEveryRecordedRuleIsPresent()
    {
        Permissions("add");

        var row = LifecycleRow();

        Assert.Equal("ok", row["status"].AsString());
        Assert.Equal("lifecycle rules: 23/23; 23 rule(s) present", row["detail"].AsString());
    }

    [Fact]
    public void DoctorCountsTheRulesInTheFileNotTheLedger()
    {
        Permissions("add");
        WriteLedger("yes", Allow().Take(7));

        var row = LifecycleRow();

        Assert.StartsWith("lifecycle rules: 23/23;", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWarnsOfAMissingRuleAStaleRootAndAnExtraRuleEachWithoutRemovingAnything()
    {
        Permissions("add");
        var rules = Allow().Where(r => !r.Contains(" resume:", StringComparison.Ordinal)).ToList();
        WriteSettings(JsonText.Stringify(new JsonObject { ["permissions"] = new JsonObject { ["allow"] = Array(rules) } }, 2));
        var missing = LifecycleRow();

        Permissions("add");
        var ledger = Parse(Ledger!);
        ledger["added"] = Array(ledger["added"].AsArray().Select(v => v.AsString()).Where(r => !r.Contains(" snapshot:", StringComparison.Ordinal)));
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.TestKit.TestHomeDocs.Permissions, JsonText.Stringify(ledger, 2));
        var extra = LifecycleRow();

        ledger["plugin_root"] = "/old/plugin";
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.TestKit.TestHomeDocs.Permissions, JsonText.Stringify(ledger, 2));
        var stale = LifecycleRow();

        Assert.Equal("warn", missing["status"].AsString());
        Assert.Contains("missing from settings: " + RuleOf("resume"), missing["detail"].AsString(), StringComparison.Ordinal);
        Assert.Equal("warn", extra["status"].AsString());
        Assert.Contains("that ah did not add (left alone): " + RuleOf("snapshot"), extra["detail"].AsString(), StringComparison.Ordinal);
        Assert.Equal("warn", stale["status"].AsString());
        Assert.Contains("/old/plugin", stale["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains(RuleOf("snapshot"), Allow());
    }

    [Fact]
    public void DoctorWarnsWhenTheSettingsFileCannotBeReadAndTheUserSaidYes()
    {
        Permissions("add");
        WriteSettings("not json");

        var row = LifecycleRow();

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("is not a JSON object", row["detail"].AsString(), StringComparison.Ordinal);
    }
}
