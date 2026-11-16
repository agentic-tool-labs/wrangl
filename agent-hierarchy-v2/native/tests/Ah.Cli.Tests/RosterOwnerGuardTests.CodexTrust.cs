using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Cli.Tests;

/// <summary><c>roster codex-trust</c>: the only writer of Codex's trust. Every case points CODEX_HOME at a directory in the sandbox.</summary>
public sealed partial class RosterOwnerGuardTests
{
    private string CodexHome => Path.Combine(_sandbox.Root, "codex-home");

    private string CodexConfig => Path.Combine(CodexHome, "config.toml");

    private (int Code, string Stdout, string Stderr) CodexTrust(params string[] extra)
    {
        _env["CODEX_HOME"] = CodexHome;
        return Run(["codex-trust", .. extra]);
    }

    private void WriteConfig(string text)
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(CodexConfig, text);
    }

    private static string Key(string stdout) => JsonText.Parse(stdout).Member("key")!.Value.AsString();

    private static string TableFor(string key) => $"[projects.\"{key}\"]\ntrust_level = \"trusted\"\n";

    private Func<IFileReader, IAhEnvironment, ICodexTrust>? _codexTrust;

    /// <summary>The real planner, with a re-read that never says trusted: the case where an edit lands but cannot be confirmed.</summary>
    private sealed class NeverTrusted(ICodexTrust real) : ICodexTrust
    {
        public string TrustRoot(string cwd) => real.TrustRoot(cwd);

        public JsonObject PlanTrust(string cwd) => real.PlanTrust(cwd);

        public JsonObject Check(string cwd) => new() { ["verdict"] = "untrusted" };
    }

    private void ReReadNeverConfirms() =>
        _codexTrust = (files, env) => new NeverTrusted(RosterModule.CreateCodexTrust(files, env, _ => null));

    /// <summary>The real planner, followed by a change to the config: another process editing it between the plan and the write.</summary>
    private sealed class EditsAfterPlanning(ICodexTrust real, string configPath, byte[] rival) : ICodexTrust
    {
        public string TrustRoot(string cwd) => real.TrustRoot(cwd);

        public JsonObject Check(string cwd) => real.Check(cwd);

        public JsonObject PlanTrust(string cwd)
        {
            var plan = real.PlanTrust(cwd);
            File.WriteAllBytes(configPath, rival);
            return plan;
        }
    }

    [Fact]
    public void AConfigChangedAfterPlanningIsRefusedNamingTheRaceAndKeepsTheOtherWritersBytes()
    {
        WriteConfig("model = \"x\"\n");
        var rival = "model = \"theirs\"\n"u8.ToArray();
        _codexTrust = (files, env) => new EditsAfterPlanning(RosterModule.CreateCodexTrust(files, env, _ => null), CodexConfig, rival);

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal("codex-config-changed", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal(rival, File.ReadAllBytes(CodexConfig));
        Assert.False(File.Exists(CodexConfig + ".ah2-bak"));
    }

    [Fact]
    public void ABomAndCrlfConfigKeepsEveryByteAroundTheAppendedTable()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. "model = \"x\"\r\n"u8.ToArray()];
        Directory.CreateDirectory(CodexHome);
        File.WriteAllBytes(CodexConfig, original);

        var (code, stdout, stderr) = CodexTrust("--yes");

        Assert.True(code == 0, stdout + stderr);
        var written = File.ReadAllBytes(CodexConfig);
        Assert.Equal(original, written[..original.Length]);
        Assert.Contains("trust_level = \"trusted\"", System.Text.Encoding.UTF8.GetString(written));
    }

    [Fact]
    public void AnEditThatCannotBeConfirmedPutsABomConfigBackByteForByte()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. "model = \"x\"\r\n"u8.ToArray()];
        Directory.CreateDirectory(CodexHome);
        File.WriteAllBytes(CodexConfig, original);
        ReReadNeverConfirms();

        var (code, _, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal(original, File.ReadAllBytes(CodexConfig));
    }

    [Fact]
    public void ReplacingAnUntrustedLevelKeepsItsTrailingComment()
    {
        var key = Key(CodexTrust().Stdout);
        WriteConfig($"[projects.\"{key}\"]\ntrust_level = \"untrusted\" # held back on purpose\n");

        var (code, _, _) = CodexTrust("--yes");

        Assert.Equal(0, code);
        Assert.Equal($"[projects.\"{key}\"]\ntrust_level = \"trusted\" # held back on purpose\n", File.ReadAllText(CodexConfig));
    }

    [Fact]
    public void AnOpenMultilineStringIsRefusedAsUnreadableAndAClosedOneIsNotTakenForATable()
    {
        var key = Key(CodexTrust().Stdout);
        WriteConfig("note = \"\"\"\n[projects.\"x\"]\n");

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal("codex-config-unreadable", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal("note = \"\"\"\n[projects.\"x\"]\n", File.ReadAllText(CodexConfig));

        var closed = $"note = \"\"\"\n[projects.\"/another\"]\ntrust_level = \"trusted\"\n\"\"\"\n";
        WriteConfig(closed);

        var (closedCode, closedOut, _) = CodexTrust("--yes");

        Assert.True(closedCode == 0, closedOut);
        Assert.Equal(closed + "\n" + TableFor(key), File.ReadAllText(CodexConfig));
    }

    [Fact]
    public void AnEditThatWouldLeaveTwoTablesForTheRootIsRefusedAndWritesNothing()
    {
        var key = Key(CodexTrust().Stdout);
        var hidden = $"note = \"\"\"\n[projects.\"{key}\"]\ntrust_level = \"trusted\"\n\"\"\"\n";
        WriteConfig(hidden);

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal("codex-config-unreadable", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal(hidden, File.ReadAllText(CodexConfig));
    }

    [Fact]
    public void ATripleQuoteAfterACommentOrInsideAnotherStringDoesNotOpenAMultilineString()
    {
        var key = Key(CodexTrust().Stdout);
        var original = "a = \"x\" # see \"\"\"\nb = 'it has \"\"\" in it'\nc = \"esc \\\"\"\" end\"\n";
        WriteConfig(original);

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.True(code == 0, stdout);
        Assert.Equal(original + "\n" + TableFor(key), File.ReadAllText(CodexConfig));
    }

    [Fact]
    public void AnEditThatCannotBeConfirmedPutsTheOriginalBytesBack()
    {
        const string Original = "# mine\nmodel = \"x\"\n";
        WriteConfig(Original);
        ReReadNeverConfirms();

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal("codex-trust-verify-failed", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal(Original, File.ReadAllText(CodexConfig));
    }

    [Fact]
    public void AnEditThatCannotBeConfirmedDeletesAConfigItCreated()
    {
        ReReadNeverConfirms();

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal("codex-trust-verify-failed", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.False(File.Exists(CodexConfig));
    }

    [Fact]
    public void CodexTrustWithoutYesPlansAndWritesNothing()
    {
        var (code, stdout, stderr) = CodexTrust();

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout);
        Assert.Equal("absent", plan.Member("state")!.Value.AsString());
        Assert.Equal("plan", plan.Member("result")!.Value.AsString());
        Assert.False(plan.Member("written")!.Value.AsBool());
        Assert.False(File.Exists(CodexConfig));
    }

    [Fact]
    public void CodexTrustWithYesCreatesAMissingConfigWithOnlyTheTableAndMode0600()
    {
        var (code, stdout, stderr) = CodexTrust("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(TableFor(Key(stdout)), File.ReadAllText(CodexConfig));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CodexConfig));
        }

        Assert.False(File.Exists(CodexConfig + ".ah2-bak"));
    }

    [Fact]
    public void CodexTrustAppendsATableToAConfigWithoutOneAndKeepsEveryOtherByteAndTheMode()
    {
        const string Original = "# my codex\nmodel = \"x\"\n\n[projects.\"/elsewhere\"]\ntrust_level = \"trusted\"\n";
        WriteConfig(Original);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(CodexConfig, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        var (code, stdout, stderr) = CodexTrust("--yes");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Original + "\n" + TableFor(Key(stdout)), File.ReadAllText(CodexConfig));
        Assert.Equal(Original, File.ReadAllText(CodexConfig + ".ah2-bak"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(CodexConfig));
        }
    }

    [Fact]
    public void CodexTrustAddsTheLevelToATableThatHasNone()
    {
        var key = Key(CodexTrust().Stdout);
        WriteConfig($"[projects.\"{key}\"]\nnote = \"mine\"\n\n[other]\na = 1\n");

        var (code, _, _) = CodexTrust("--yes");

        Assert.Equal(0, code);
        Assert.Equal($"[projects.\"{key}\"]\ntrust_level = \"trusted\"\nnote = \"mine\"\n\n[other]\na = 1\n", File.ReadAllText(CodexConfig));
    }

    [Fact]
    public void CodexTrustReplacesAnUntrustedLevelAndSaysSo()
    {
        var key = Key(CodexTrust().Stdout);
        WriteConfig($"[projects.\"{key}\"]\ntrust_level = \"untrusted\"\nnote = \"mine\"\n");

        var (_, plan, _) = CodexTrust();
        var (code, _, _) = CodexTrust("--yes");

        Assert.Contains("replaces your earlier distrust", plan, StringComparison.Ordinal);
        Assert.Equal(0, code);
        Assert.Equal($"[projects.\"{key}\"]\ntrust_level = \"trusted\"\nnote = \"mine\"\n", File.ReadAllText(CodexConfig));
    }

    [Fact]
    public void CodexTrustOnATrustedRootChangesNothingAndMakesNoBackup()
    {
        var key = Key(CodexTrust().Stdout);
        WriteConfig(TableFor(key));

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(0, code);
        Assert.Equal("unchanged", JsonText.Parse(stdout).Member("result")!.Value.AsString());
        Assert.Equal(TableFor(key), File.ReadAllText(CodexConfig));
        Assert.False(File.Exists(CodexConfig + ".ah2-bak"));
    }

    [Fact]
    public void CodexTrustRefusesAConfigItCannotDecodeAndWritesNothing()
    {
        const string Odd = "[projects.'/single/quoted']\ntrust_level = \"trusted\"\n";
        WriteConfig(Odd);

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal("codex-config-unreadable", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Equal(Odd, File.ReadAllText(CodexConfig));
        Assert.False(File.Exists(CodexConfig + ".ah2-bak"));
    }

    [Fact]
    public void CodexTrustEscapesAKeyThatNeedsIt()
    {
        var odd = Path.Combine(Project, "has \"quote\" and \\slash");
        Directory.CreateDirectory(odd);

        var (code, stdout, stderr) = CodexTrust(odd, "--yes");

        Assert.True(code == 0, stdout + stderr);
        var text = File.ReadAllText(CodexConfig);
        Assert.Contains("\\\"quote\\\"", text, StringComparison.Ordinal);
        Assert.Contains("\\\\slash", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("[projects.", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CodexTrustIsRefusedToAMemberSessionAndWritesNothing()
    {
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (code, stdout, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.Equal("not-from-member", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.False(File.Exists(CodexConfig));
    }

    [Fact]
    public void CodexTrustIsRefusedToAMemberMarkedShell()
    {
        _env[MeshModule.MemberMarkerEnv] = "proj-implementor";

        var (code, _, _) = CodexTrust("--yes");

        Assert.Equal(2, code);
        Assert.False(File.Exists(CodexConfig));
    }

    [Fact]
    public void CodexTrustRejectsAnUnknownFlagAndTwoDirectories()
    {
        Assert.Equal(2, CodexTrust("--bogus").Code);
        Assert.Equal(2, CodexTrust("/a", "/b").Code);
        Assert.False(File.Exists(CodexConfig));
    }

    /// <summary>The pre-approval store's meta row, or null when there is none.</summary>
    private string? StoreRow => Ah.TestKit.TestHomeDocs.Text(_sandbox.AhHome, RosterModule.CodexTrustPreapprovalKey);

    /// <summary>The file the store used to be; nothing writes it any more.</summary>
    private string OldStoreFile => Path.Combine(_sandbox.AhHome, "codex-trust-preapproval.json");

    private static JsonValue Scopes(string stdoutOrFile) => JsonText.Parse(stdoutOrFile).Member("scopes")!.Value;

    [Fact]
    public void PreapproveWithoutYesPlansAndWritesNothing()
    {
        var (code, stdout, _) = CodexTrust("--preapprove", Project);

        Assert.Equal(0, code);
        Assert.Equal("plan", JsonText.Parse(stdout).Member("result")!.Value.AsString());
        Assert.Null(StoreRow);
    }

    [Fact]
    public void PreapproveWithYesRecordsTheTrustRootAsARootScopeInTheDatabaseAndTouchesNoFile()
    {
        var key = Key(CodexTrust().Stdout);

        var (code, stdout, stderr) = CodexTrust("--preapprove", Project, "--yes");

        Assert.True(code == 0, stdout + stderr);
        var scope = Scopes(StoreRow!).AsArray().Single();
        Assert.Equal(key, scope.Member("path")!.Value.AsString());
        Assert.Equal("root", scope.Member("kind")!.Value.AsString());
        Assert.Equal("cli", scope.Member("via")!.Value.AsString());
        Assert.False(File.Exists(CodexConfig));
        Assert.False(File.Exists(OldStoreFile));
    }

    [Fact]
    public void PreapprovingTheSameRootTwiceRecordsItOnce()
    {
        CodexTrust("--preapprove", Project, "--yes");

        var (_, stdout, _) = CodexTrust("--preapprove", Project, "--yes");

        Assert.Equal("unchanged", JsonText.Parse(stdout).Member("result")!.Value.AsString());
        Assert.Single(Scopes(StoreRow!).AsArray());
    }

    [Fact]
    public void ParentPreapprovalRecordsTheFolderItselfAndAViaIsKept()
    {
        var (code, _, stderr) = CodexTrust("--preapprove", Project, "--parent", "--via", "bootstrap", "--yes");

        Assert.True(code == 0, stderr);
        var scope = Scopes(StoreRow!).AsArray().Single();
        Assert.Equal("parent", scope.Member("kind")!.Value.AsString());
        Assert.Equal("bootstrap", scope.Member("via")!.Value.AsString());
    }

    [Fact]
    public void ABadViaAndParentWithoutPreapproveAreRejectedAndWriteNothing()
    {
        Assert.Equal(2, CodexTrust("--preapprove", Project, "--via", "somewhere", "--yes").Code);
        Assert.Equal(2, CodexTrust("--parent", "--yes").Code);
        Assert.Equal(2, CodexTrust("--preapprove", Project, "--list").Code);
        Assert.Null(StoreRow);
    }

    [Fact]
    public void ParentPreapprovalOfSlashIsRefusedAndAnotherFolderIsNot()
    {
        var (code, _, stderr) = CodexTrust("--preapprove", "/", "--parent", "--yes");

        Assert.Equal(2, code);
        Assert.Contains("/", stderr);
        Assert.Null(StoreRow);
        Assert.Equal(0, CodexTrust("--preapprove", Project, "--parent", "--yes").Code);
    }

    [Fact]
    public void ParentPreapprovalOfTheHomeFolderWarnsAndAnotherFolderDoesNot()
    {
        var (_, home, _) = CodexTrust("--preapprove", _sandbox.Home, "--parent");
        var (_, other, _) = CodexTrust("--preapprove", Project, "--parent");

        Assert.NotNull(JsonText.Parse(home).Member("warnings"));
        Assert.Null(JsonText.Parse(other).Member("warnings"));
    }

    [Fact]
    public void RevokeRemovesThatScopeOnlyAndAnotherPathChangesNothing()
    {
        CodexTrust("--preapprove", Project, "--yes");
        var elsewhere = Path.Combine(_sandbox.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        var (_, other, _) = CodexTrust("--revoke", elsewhere);
        Assert.Equal("unchanged", JsonText.Parse(other).Member("result")!.Value.AsString());
        Assert.Single(Scopes(StoreRow!).AsArray());

        var (code, stdout, _) = CodexTrust("--revoke", Project);
        Assert.Equal(0, code);
        Assert.Equal("revoked", JsonText.Parse(stdout).Member("result")!.Value.AsString());
        Assert.Empty(Scopes(StoreRow!).AsArray());
    }

    [Fact]
    public void RevokeOfASubdirectoryRemovesTheScopeOfItsTrustRootAndASiblingRepoKeepsItsScope()
    {
        var repo = Path.Combine(_sandbox.Root, "repo-a");
        var sibling = Path.Combine(_sandbox.Root, "repo-b");
        foreach (var r in new[] { repo, sibling })
        {
            Directory.CreateDirectory(Path.Combine(r, ".git"));
        }

        var sub = Path.Combine(repo, "src");
        Directory.CreateDirectory(sub);
        CodexTrust("--preapprove", sub, "--yes");
        CodexTrust("--preapprove", sibling, "--yes");
        Assert.Equal(2, Scopes(StoreRow!).AsArray().Count);

        var (code, stdout, _) = CodexTrust("--revoke", sub);

        Assert.Equal(0, code);
        Assert.Equal("revoked", JsonText.Parse(stdout).Member("result")!.Value.AsString());
        Assert.Single(Scopes(StoreRow!).AsArray());
    }

    [Fact]
    public void RevokeAllNeedsYesAndThenDeletesTheRow()
    {
        CodexTrust("--preapprove", Project, "--yes");

        var (_, plan, _) = CodexTrust("--revoke-all");
        Assert.Equal("plan", JsonText.Parse(plan).Member("result")!.Value.AsString());
        Assert.NotNull(StoreRow);

        CodexTrust("--revoke-all", "--yes");
        Assert.Null(StoreRow);
    }

    [Fact]
    public void ListShowsTrustThePreapprovalsAndTheAutoAnswerCount()
    {
        CodexTrust("--preapprove", Project, "--yes");
        Directory.CreateDirectory(_sandbox.AhHome);
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var old = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(
            Path.Combine(_sandbox.AhHome, "codex-trust-autoanswer.jsonl"),
            $"{{\"at\":\"{old}\",\"result\":\"answered\"}}\n{{\"at\":\"{now}\",\"result\":\"answered\"}}\n");

        var (code, stdout, stderr) = CodexTrust("--list");

        Assert.True(code == 0, stdout + stderr);
        var list = JsonText.Parse(stdout);
        Assert.Single(list.Member("preapprovals")!.Value.AsArray());
        Assert.Equal(1, (int)list.Member("autoanswer")!.Value.Member("last_7_days")!.Value.AsNumber());
    }

    [Fact]
    public void PreapprovalIsRefusedToAMemberSessionAndWritesNothing()
    {
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (code, stdout, _) = CodexTrust("--preapprove", Project, "--yes");

        Assert.Equal(2, code);
        Assert.Equal("not-from-member", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Null(StoreRow);
    }

    [Fact]
    public void AStoreThatIsNotAPreapprovalRowIsRefusedNotTreatedAsEmpty()
    {
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, RosterModule.CodexTrustPreapprovalKey, "[1,2]");

        var (code, _, _) = CodexTrust("--preapprove", Project, "--yes");

        Assert.Equal(2, code);
        Assert.Equal("[1,2]", StoreRow!);
    }

    [Fact]
    public void AnOldStoreFileIsIgnored()
    {
        Directory.CreateDirectory(_sandbox.AhHome);
        File.WriteAllText(OldStoreFile, "{\"v\":1,\"scopes\":[{\"path\":\"/x\",\"kind\":\"root\"}]}");

        var (code, stdout, stderr) = CodexTrust("--list");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(JsonText.Parse(stdout).Member("preapprovals")!.Value.AsArray());
    }

}
