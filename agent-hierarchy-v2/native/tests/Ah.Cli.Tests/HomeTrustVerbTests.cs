using System.Globalization;
using Ah.Abstractions;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;

namespace Ah.Cli.Tests;

[Collection("HomeTrustServices")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class HomeTrustVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly PlatformIo _io = PlatformModule.CreateIo(null);
    private readonly uint _uid = PlatformModule.CurrentUid();

    private string? _defaultHome;

    private string DefaultHome => _defaultHome ?? Path.Combine(_sandbox.Root, "dh");

    private string HomesFile => Path.Combine(DefaultHome, "trusted-homes");

    private string HierarchyFile => Path.Combine(DefaultHome, "trusted-hierarchy-dirs");

    public void Dispose()
    {
        CommandsModule.ComposeHomeTrust(() => null!);
        _sandbox.Dispose();
    }

    private sealed class Services(IFileReader files, IRawFileWriter writer, ITrustStore trust, IAhEnvironment environment, uint? uid, bool terminal) : IHomeTrustServices
    {
        public ITrustStore Trust => trust;

        public IFileReader Files => files;

        public IRawFileWriter Writer => writer;

        public IAhEnvironment Environment => environment;

        public uint? CurrentUserId => uid;

        public bool IsTerminal() => terminal;
    }

    private sealed class RecordingWriter(IRawFileWriter inner) : IRawFileWriter
    {
        internal List<(string Path, string Tmp, UnixFileMode? Mode)> Writes { get; } = [];

        internal bool Fail { get; set; }

        public void WriteAtomic(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
        {
            Writes.Add((path, tmpPath, mode));
            if (Fail)
            {
                throw new IOException("the disk went away");
            }

            inner.WriteAtomic(path, tmpPath, bytes, mode);
        }

        public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => inner.CreateExclusive(path, bytes, mode);

        public void Rename(string fromPath, string toPath) => inner.Rename(fromPath, toPath);

        public bool Unlink(string path) => inner.Unlink(path);

        public void Mkdir(string path, UnixFileMode? mode = null) => inner.Mkdir(path, mode);
    }

    private (int Code, string Stdout, string Stderr, RecordingWriter Writer) Run(bool terminal, string? spawnDepth, params string[] args) =>
        RunIn(terminal, spawnDepth, testMode: true, markers: null, args);

    private (int Code, string Stdout, string Stderr, RecordingWriter Writer) RunIn(
        bool terminal, string? spawnDepth, bool testMode, Dictionary<string, string?>? markers, params string[] args)
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = testMode ? "1" : null,
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["AH_SPAWN_DEPTH"] = spawnDepth,
        };
        foreach (var marker in markers ?? [])
        {
            variables[marker.Key] = marker.Value;
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var uid = _uid;
        var trust = ConfigModule.CreateTrustStore(_io.Files, () => DefaultHome, () => uid);
        var writer = new RecordingWriter(_io.Writer);
        var services = new Services(_io.Files, writer, trust, env, uid, terminal);
        CommandsModule.ComposeHomeTrust(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "home");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString(), writer);
    }

    private (int Code, string Stdout, string Stderr, RecordingWriter Writer) Trust(params string[] args) => Run(true, null, ["trust", .. args]);

    private (int Code, string Stdout, string Stderr, RecordingWriter Writer) Untrust(params string[] args) => Run(true, null, ["untrust", .. args]);

    private string MakeDir(string name, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
    {
        var path = Path.Combine(_sandbox.Root, name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
        return Path.GetFullPath(path);
    }

    private void MakeDefaultHome()
    {
        Directory.CreateDirectory(DefaultHome);
        File.SetUnixFileMode(DefaultHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private ITrustStore Store() => ConfigModule.CreateTrustStore(_io.Files, () => DefaultHome, () => _uid);

    private static JsonValue Parse(string text) => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void TrustAddsTheRealPathAsAMode0600FileThatTheStoreHonours()
    {
        var dir = MakeDir("proj");
        var real = _io.Files.RealPath(dir);

        var (code, stdout, stderr, _) = Trust(dir);

        Assert.True(code == 0, stderr);
        Assert.Equal(real + "\n", File.ReadAllText(HomesFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(HomesFile));
        Assert.True(Store().IsTrusted(TrustFile.Homes, real));
        var result = Parse(stdout);
        Assert.True(result.Member("ok")!.Value.AsBool());
        Assert.True(result.Member("changed")!.Value.AsBool());
        Assert.False(File.Exists(HierarchyFile));
    }

    [Fact]
    public void TrustingTheSamePathTwiceLeavesOneLine()
    {
        var dir = MakeDir("proj");
        _ = Trust(dir);

        var (code, stdout, _, writer) = Trust(dir);

        Assert.Equal(0, code);
        Assert.False(Parse(stdout).Member("changed")!.Value.AsBool());
        Assert.Empty(writer.Writes);
        Assert.Single(File.ReadAllLines(HomesFile));
    }

    [Fact]
    public void TheHierarchyFlagWritesTheOtherFileOnly()
    {
        var dir = MakeDir("hier");

        var (code, _, stderr, _) = Trust(dir, "--hierarchy");

        Assert.True(code == 0, stderr);
        Assert.True(Store().IsTrusted(TrustFile.HierarchyDirs, _io.Files.RealPath(dir)));
        Assert.False(File.Exists(HomesFile));
    }

    [Fact]
    public void WithoutATerminalNothingIsWrittenAndTheDefaultHomeIsNotMade()
    {
        var dir = MakeDir("proj");

        var (code, _, stderr, writer) = Run(false, null, "trust", dir);

        Assert.Equal(2, code);
        Assert.Contains("terminals", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.False(Directory.Exists(DefaultHome));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("")]
    public void AnySpawnDepthRefusesAndWritesNothing(string depth)
    {
        var dir = MakeDir("proj");

        var (code, _, stderr, writer) = Run(true, depth, "trust", dir);

        Assert.Equal(2, code);
        Assert.Contains("agent session", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.False(Directory.Exists(DefaultHome));
    }

    public static TheoryData<string, string> SessionMarkers => new()
    {
        { "CLAUDECODE", "1" },
        { "CLAUDECODE", "" },
        { "CLAUDE_CODE_SESSION_ID", "x" },
    };

    [Theory]
    [MemberData(nameof(SessionMarkers))]
    public void OutsideTestModeAClaudeCodeSessionMarkerRefusesAndNamesNoPath(string name, string value)
    {
        var dir = MakeDir("proj");

        var (code, _, stderr, writer) = RunIn(true, null, testMode: false, new Dictionary<string, string?> { [name] = value }, "trust", dir);

        Assert.Equal(2, code);
        Assert.Contains("Claude Code", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(dir, stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.False(File.Exists(HomesFile));
    }

    [Fact]
    public void OutsideTestModeWithNoMarkerTheVerbWrites()
    {
        var dir = MakeDir("proj");

        var (code, _, stderr, _) = RunIn(true, null, testMode: false, null, "trust", dir);

        Assert.True(code == 0, stderr);
        Assert.Contains(_io.Files.RealPath(dir), File.ReadAllText(HomesFile), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SessionMarkers))]
    public void InTestModeTheSessionMarkersAreSkippedAndTheSandboxFileIsWritten(string name, string value)
    {
        var dir = MakeDir("proj");

        var (code, _, stderr, _) = RunIn(true, null, testMode: true, new Dictionary<string, string?> { [name] = value }, "trust", dir);

        Assert.True(code == 0, stderr);
        Assert.True(File.Exists(HomesFile));
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void NoRefusalTextClaimsThatAPersonIsTyping()
    {
        var dir = MakeDir("proj");
        var texts = new[]
        {
            Run(false, null, "trust", dir).Stderr,
            Run(true, "1", "trust", dir).Stderr,
            RunIn(true, null, testMode: false, new Dictionary<string, string?> { ["CLAUDECODE"] = "1" }, "trust", dir).Stderr,
        };

        foreach (var text in texts)
        {
            Assert.NotEmpty(text);
            Assert.DoesNotContain("person", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("human", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("only by you", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UntrustIsRefusedWithoutATerminalToo()
    {
        var dir = MakeDir("proj");
        _ = Trust(dir);
        var before = File.ReadAllText(HomesFile);

        var (code, _, _, writer) = Run(false, null, "untrust", dir);

        Assert.Equal(2, code);
        Assert.Empty(writer.Writes);
        Assert.Equal(before, File.ReadAllText(HomesFile));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-x")]
    public void AnUnknownFlagIsAUsageErrorAndNothingIsWritten(string flag)
    {
        var dir = MakeDir("proj");

        var (code, _, stderr, writer) = Trust(dir, flag);

        Assert.Equal(2, code);
        Assert.Contains("usage: ahd home trust|untrust <path> [--hierarchy]", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void AMissingPathAndAnExtraPathAreUsageErrors()
    {
        var dir = MakeDir("proj");

        Assert.Equal(2, Trust().Code);
        Assert.Equal(2, Trust(dir, dir).Code);
        Assert.Equal(2, Run(true, null, "forget", dir).Code);
        Assert.Equal(2, Run(true, null).Code);
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void AGroupWritableDirectoryIsRefusedWhileAnOwnerOnlyOneIsAccepted()
    {
        var loose = MakeDir("loose", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        var tight = MakeDir("tight", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var refused = Trust(loose);
        var accepted = Trust(tight);

        Assert.Equal(1, refused.Code);
        Assert.Contains("writable by its group or others", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, accepted.Code);
        Assert.DoesNotContain(loose, File.ReadAllText(HomesFile), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAndAMissingPathAreRefused()
    {
        var file = Path.Combine(_sandbox.Root, "plain.txt");
        File.WriteAllText(file, "x");

        var onFile = Trust(file);
        var missing = Trust(Path.Combine(_sandbox.Root, "nowhere"));

        Assert.Equal(1, onFile.Code);
        Assert.Contains("is not a directory", onFile.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, missing.Code);
        Assert.Contains("does not exist", missing.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(HomesFile));
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ADirectoryNamedWithANewlineIsRefusedSoNoSecondLineCanBeInjected()
    {
        var odd = Path.Combine(_sandbox.Root, "a\nb");
        Directory.CreateDirectory(odd);
        File.SetUnixFileMode(odd, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (code, _, stderr, writer) = Trust(odd);

        Assert.Equal(1, code);
        Assert.Contains("one-path-per-line", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void ASymlinkToADirectoryStoresTheTargetAndUntrustThroughTheLinkRemovesThatLine()
    {
        var target = MakeDir("real-dir");
        var other = MakeDir("other-dir");
        var link = Path.Combine(_sandbox.Root, "link-to-real");
        File.CreateSymbolicLink(link, target);
        _ = Trust(other);

        var trusted = Trust(link);

        Assert.Equal(0, trusted.Code);
        var lines = File.ReadAllLines(HomesFile);
        Assert.Equal([_io.Files.RealPath(other), _io.Files.RealPath(target)], lines);
        Assert.DoesNotContain(link, lines);

        var untrusted = Untrust(link);

        Assert.Equal(0, untrusted.Code);
        Assert.Equal([_io.Files.RealPath(other)], File.ReadAllLines(HomesFile));
    }

    [Fact]
    public void UntrustRemovesOnlyTheNamedLineAndNeverTheFile()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        _ = Trust(a);
        _ = Trust(b);

        Assert.Equal(0, Untrust(a).Code);
        Assert.Equal([_io.Files.RealPath(b)], File.ReadAllLines(HomesFile));

        var unlisted = Untrust(a);
        Assert.Equal(1, unlisted.Code);
        Assert.Contains("is not listed", unlisted.Stderr, StringComparison.Ordinal);
        Assert.Equal([_io.Files.RealPath(b)], File.ReadAllLines(HomesFile));

        Assert.Equal(0, Untrust(b).Code);
        Assert.True(File.Exists(HomesFile));
        Assert.Equal(string.Empty, File.ReadAllText(HomesFile));
    }

    [Fact]
    public void UntrustOfADirectoryThatNoLongerExistsRemovesItsListedText()
    {
        var gone = MakeDir("gone");
        var stays = MakeDir("stays");
        _ = Trust(gone);
        _ = Trust(stays);
        var listed = _io.Files.RealPath(gone);
        Directory.Delete(gone);

        var (code, _, stderr, _) = Untrust(listed);

        Assert.True(code == 0, stderr);
        Assert.Equal([_io.Files.RealPath(stays)], File.ReadAllLines(HomesFile));
    }

    [Fact]
    public void ATrustFileThatIsCurrentlyIgnoredIsNotRewritten()
    {
        var dir = MakeDir("proj");
        MakeDefaultHome();
        File.WriteAllText(HomesFile, "/somewhere/else\n");
        File.SetUnixFileMode(HomesFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);

        var (code, _, stderr, writer) = Trust(dir);

        Assert.Equal(1, code);
        Assert.Contains("currently ignored", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.Equal("/somewhere/else\n", File.ReadAllText(HomesFile));
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void AGroupWritableDefaultHomeIsNotWrittenInto()
    {
        var dir = MakeDir("proj");
        Directory.CreateDirectory(DefaultHome);
        File.SetUnixFileMode(DefaultHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);

        var (code, _, stderr, writer) = Trust(dir);

        Assert.Equal(1, code);
        Assert.Contains("writable by its group or others", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ADirectoryOwnedByAnotherUserIsRefused()
    {
        if (_uid == 0)
        {
            return;
        }

        var (code, _, stderr, writer) = Trust("/usr");

        Assert.Equal(1, code);
        Assert.Contains("is not owned by you", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ATrustFileThatIsASymlinkIsLeftAloneAndItsTargetIsNotWritten()
    {
        var dir = MakeDir("proj");
        MakeDefaultHome();
        var elsewhere = Path.Combine(_sandbox.Root, "elsewhere.txt");
        File.WriteAllText(elsewhere, "keep\n");
        File.CreateSymbolicLink(HomesFile, elsewhere);

        var (code, _, stderr, writer) = Trust(dir);

        Assert.Equal(1, code);
        Assert.Contains("currently ignored", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.Equal("keep\n", File.ReadAllText(elsewhere));
        Assert.NotNull(new FileInfo(HomesFile).LinkTarget);
    }

    private sealed class IgnoringAfterWrite(ITrustStore inner, TrustFile file) : ITrustStore
    {
        private int _reads;

        public TrustFileReport Read(TrustFile which)
        {
            var report = inner.Read(which);
            return which == file && ++_reads > 1 ? report with { IgnoredReason = "the store no longer honours it" } : report;
        }

        public bool IsTrusted(TrustFile which, string path) => inner.IsTrusted(which, path);
    }

    [Fact]
    public void AWriteTheStoreThenIgnoresIsReportedAsAFailure()
    {
        var dir = MakeDir("proj");
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var services = new Services(_io.Files, new RecordingWriter(_io.Writer), new IgnoringAfterWrite(Store(), TrustFile.Homes), env, _uid, true);
        CommandsModule.ComposeHomeTrust(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "home");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var code = verb.Run(["trust", dir], stdout, stderr);

        Assert.Equal(1, code);
        Assert.Contains("written but ignored", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Parse(stdout.ToString()).Member("ok")!.Value.AsBool());
    }

    [Fact]
    public void TheWriteIsOneAtomicReplaceFromATempFileInTheSameDirectoryWithMode0600()
    {
        var dir = MakeDir("proj");

        var (_, _, _, writer) = Trust(dir);

        var write = Assert.Single(writer.Writes);
        Assert.Equal(HomesFile, write.Path);
        Assert.Equal(DefaultHome, Path.GetDirectoryName(write.Tmp));
        Assert.NotEqual(HomesFile, write.Tmp);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, write.Mode);
        Assert.Empty(Directory.GetFiles(DefaultHome, "*.tmp"));
    }

    [Fact]
    public void AFailedWriteLeavesTheOldFileAndReportsTheError()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        _ = Trust(a);
        var before = File.ReadAllText(HomesFile);
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var writer = new RecordingWriter(_io.Writer) { Fail = true };
        var services = new Services(_io.Files, writer, Store(), env, _uid, true);
        CommandsModule.ComposeHomeTrust(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "home");
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var code = verb.Run(["trust", b], new StringWriter(CultureInfo.InvariantCulture), stderr);

        Assert.Equal(1, code);
        Assert.Contains("the disk went away", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(HomesFile));
    }

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void AnUnknownInvokingUserRefusesBeforeAnyRead()
    {
        var dir = MakeDir("proj");
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var writer = new RecordingWriter(_io.Writer);
        var services = new Services(_io.Files, writer, Store(), env, null, true);
        CommandsModule.ComposeHomeTrust(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "home");

        var code = verb.Run(["trust", dir], new StringWriter(CultureInfo.InvariantCulture), new StringWriter(CultureInfo.InvariantCulture));

        Assert.Equal(2, code);
        Assert.Empty(writer.Writes);
    }

    private string V1Home()
    {
        _defaultHome = _sandbox.AhHome;
        MakeDefaultHome();
        var v1 = Path.Combine(_sandbox.Home, ".claude", "agent-hierarchy");
        Directory.CreateDirectory(Path.Combine(v1, "sub"));
        return v1;
    }

    private void AssertRefusedAsV1(string target)
    {
        var (code, _, stderr, writer) = Trust(target);

        Assert.Equal(1, code);
        Assert.Contains("ah v1's home", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.False(File.Exists(HomesFile));
    }

    [Fact]
    public void TrustRefusesV1sHomeAndAnythingUnderIt()
    {
        var v1 = V1Home();

        AssertRefusedAsV1(v1);
        AssertRefusedAsV1(v1 + "/");
        AssertRefusedAsV1(Path.Combine(v1, "sub"));
        AssertRefusedAsV1(Path.Combine(v1, "sub", ".."));
    }

    [Fact]
    public void TrustRefusesAPathUnderV1sHomeThatDoesNotExistYetWithTheV1Wording()
    {
        var v1 = V1Home();

        AssertRefusedAsV1(Path.Combine(v1, "not", "there", "yet"));
    }

    [Fact]
    public void TrustRefusesALinkWhoseRealPathIsInV1sHome()
    {
        var v1 = V1Home();
        var toHome = Path.Combine(_sandbox.Root, "to-v1");
        var toChild = Path.Combine(_sandbox.Root, "to-v1-sub");
        File.CreateSymbolicLink(toHome, v1);
        File.CreateSymbolicLink(toChild, Path.Combine(v1, "sub"));

        AssertRefusedAsV1(toHome);
        AssertRefusedAsV1(toChild);
        AssertRefusedAsV1(Path.Combine(toHome, "sub"));
    }

    [Fact]
    public void TrustRefusesInItsOwnWordsWhenV1sHomeCannotBeResolved()
    {
        _defaultHome = _sandbox.AhHome;
        MakeDefaultHome();
        File.CreateSymbolicLink(Path.Combine(_sandbox.Home, ".claude", "agent-hierarchy"), Path.Combine(_sandbox.Root, "nowhere"));
        var target = Path.Combine(_sandbox.Root, "elsewhere");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (code, _, stderr, writer) = Trust(target);

        Assert.Equal(1, code);
        Assert.Contains("ah v1's home path (~/.claude/agent-hierarchy) cannot be resolved (a dangling link?)", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("that is ah v1's home", stderr, StringComparison.Ordinal);
        Assert.Empty(writer.Writes);
        Assert.False(File.Exists(HomesFile));
    }

    [Theory]
    [InlineData("agent-hierarchy-v2")]
    [InlineData("agent-hierarchyX")]
    [InlineData("agent-hierarchy.bak")]
    public void ASiblingThatOnlyStartsWithV1sNameIsNotV1sHomeAndCanBeTrusted(string name)
    {
        _ = V1Home();
        var sibling = Path.Combine(_sandbox.Home, ".claude", name);
        Directory.CreateDirectory(sibling);
        File.SetUnixFileMode(sibling, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (code, _, stderr, _) = Trust(sibling);

        Assert.True(code == 0, stderr);
        Assert.Equal(_io.Files.RealPath(sibling) + "\n", File.ReadAllText(HomesFile));
    }

    [Fact]
    public void UntrustStillRemovesAV1HomeEntryThatWasListedByHand()
    {
        var v1 = V1Home();
        File.WriteAllText(HomesFile, _io.Files.RealPath(v1) + "\n");

        var (code, _, stderr, _) = Untrust(v1);

        Assert.True(code == 0, stderr);
        Assert.Equal(string.Empty, File.ReadAllText(HomesFile));
    }
}
