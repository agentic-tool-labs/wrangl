using System.Diagnostics;
using System.Text;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>
/// <c>doctor --fix --statusline-tap on|off</c>: the guard, the script it runs, the report row and the prune. The script is the
/// one doctor installed, run by <c>sh</c> from the settings entry exactly as Claude Code would run it.
/// </summary>
public sealed partial class RosterDoctorVerbTests
{
    private const string TuiSettings =
        "{\n  \"theme\": \"dark\",\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"claude-tui-line\",\n    \"padding\": 0\n  },\n  \"other\": [1, 2]\n}\n";

    private string UserSettingsFile => Path.Combine(_sandbox.Home, ".claude", "settings.json");

    private string TapHome => Path.Combine(_sandbox.Home, ".claude", "ah2");

    private string TapScript => Path.Combine(TapHome, "bin", "statusline-tap.sh");

    private string TapBackup => Path.Combine(TapHome, "statusline-tap.json");

    private string TapData => Path.Combine(TapHome, "statusline");

    private void WriteUserSettings(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsFile)!);
        File.WriteAllText(UserSettingsFile, text, new UTF8Encoding(false));
    }

    private (int Code, string Stdout, string Stderr) TapOn(string mode = "on") => Roster(null, "doctor", "--fix", "--statusline-tap", mode);

    private string GuardCommand() =>
        JsonText.Parse(File.ReadAllBytes(UserSettingsFile)).Member("statusLine")!.Value.Member("command")!.Value.AsString();

    private string TapRow() => Row(Parse(Roster(null, "doctor").Stdout), "statusline-tap")["detail"].AsString();

    /// <summary>Runs a statusline command string through <c>sh -c</c> with HOME in the sandbox, as Claude Code runs it.</summary>
    private (string Stdout, string Stderr, int Code) RunStatusline(string command, string stdin)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        start.Environment.Clear();
        start.Environment["HOME"] = _sandbox.Home;
        start.Environment["PATH"] = "/usr/bin:/bin";
        using var process = Process.Start(start)!;
        var err = process.StandardError.ReadToEndAsync();
        var outText = process.StandardOutput.ReadToEndAsync();
        try
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command may exit without reading its input; that is a valid statusline command.
        }

        if (!process.WaitForExit(20000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the statusline command did not exit");
        }

        return (outText.Result, err.Result, process.ExitCode);
    }

    private const string SessionInput = "{\"session_id\":\"abc-123\",\"model\":{\"id\":\"m\"}}\n";

    [Fact]
    public void S1OnWrapsTheCommandInTheInlineGuardKeepsEveryOtherKeyAndBacksUpTheOriginal()
    {
        WriteUserSettings(TuiSettings);

        var (code, stdout, stderr) = TapOn();

        Assert.True(code == 0, stderr);
        Assert.Contains("statusline tap: on", Parse(stdout)["fix"].AsArray().Select(x => x.AsString()));
        var after = File.ReadAllText(UserSettingsFile);
        var command = GuardCommand();
        Assert.StartsWith("sh -c '", command);
        Assert.Contains("$HOME/.claude/ah2/bin/statusline-tap.sh", command);
        Assert.EndsWith(" 'claude-tui-line'", command);
        var statusLine = JsonText.Parse(File.ReadAllBytes(UserSettingsFile)).Member("statusLine")!.Value;
        Assert.Equal("command", statusLine.Member("type")!.Value.AsString());
        Assert.Equal(0, statusLine.Member("padding")!.Value.AsNumber());
        var before = TuiSettings.IndexOf("\"statusLine\"", StringComparison.Ordinal);
        Assert.Equal(TuiSettings[..before], after[..before]);
        Assert.EndsWith(",\n  \"other\": [1, 2]\n}\n", after);

        var backup = JsonText.Parse(File.ReadAllBytes(TapBackup));
        Assert.Equal("claude-tui-line", backup.Member("original")!.Value.Member("command")!.Value.AsString());
        Assert.Equal(0, backup.Member("original")!.Value.Member("padding")!.Value.AsNumber());
        Assert.Equal(UserSettingsFile, backup.Member("settings_path")!.Value.AsString());
        Assert.True(File.Exists(TapScript));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(TapScript));
        Assert.Equal("statusline tap: on", TapRow());
    }

    [Fact]
    public void S2OnTwiceIsAlreadyOnAndLeavesTheSettingsByteIdentical()
    {
        WriteUserSettings(TuiSettings);
        TapOn();
        var once = File.ReadAllBytes(UserSettingsFile);

        var (code, stdout, _) = TapOn();

        Assert.Equal(0, code);
        Assert.Contains("statusline tap: already on", Parse(stdout)["fix"].AsArray().Select(x => x.AsString()));
        Assert.Equal(once, File.ReadAllBytes(UserSettingsFile));
    }

    [Theory]
    [InlineData("{\n\t\"statusLine\":{\"type\":\"command\",\"command\":\"it's $HOME; \\\"x\\\"\",\"padding\":0 , \"refreshInterval\" : 5},\r\n\t\"a\":\"statusLine\"\n}")]
    [InlineData("{\"statusLine\": {\"type\": \"command\", \"command\": \"claude-tui-line\"}}")]
    public void S3OffRestoresTheFileByteForByteRemovesTheBackupAndLeavesTheScript(string original)
    {
        WriteUserSettings(original);
        var before = File.ReadAllBytes(UserSettingsFile);
        TapOn();
        Assert.NotEqual(before, File.ReadAllBytes(UserSettingsFile));

        var (code, stdout, stderr) = TapOn("off");

        Assert.True(code == 0, stderr);
        Assert.Contains("statusline tap: off", Parse(stdout)["fix"].AsArray().Select(x => x.AsString()));
        Assert.Equal(before, File.ReadAllBytes(UserSettingsFile));
        Assert.False(File.Exists(TapBackup));
        Assert.True(File.Exists(TapScript));
    }

    [Fact]
    public void S3OffWhenTheTapWasNeverOnChangesNothing()
    {
        WriteUserSettings(TuiSettings);

        var (code, stdout, _) = TapOn("off");

        Assert.Equal(0, code);
        Assert.Contains("statusline tap: already off", Parse(stdout)["fix"].AsArray().Select(x => x.AsString()));
        Assert.Equal(TuiSettings, File.ReadAllText(UserSettingsFile));
        Assert.False(File.Exists(TapScript));
    }

    [Theory]
    [InlineData("{\"statusLine\":{\"type\":\"static\",\"text\":\"hi\"}}", "the statusLine is not a command")]
    [InlineData("{\"statusLine\":{\"type\":\"command\"}}", "the statusLine is not a command")]
    [InlineData("{\"statusLine\": {\"type\": \"command\", \"command\": \"x\"}", "settings.json could not be read as JSON; nothing was changed")]
    public void S4RefusesWithItsTextAndChangesNothing(string settings, string text)
    {
        WriteUserSettings(settings);

        var (code, _, stderr) = TapOn();

        Assert.Equal(2, code);
        Assert.Contains(text, stderr);
        Assert.Equal(settings, File.ReadAllText(UserSettingsFile));
        Assert.False(File.Exists(TapScript));
        Assert.False(File.Exists(TapBackup));
    }

    [Theory]
    [InlineData("{\"theme\":\"dark\"}")]
    [InlineData("{}")]
    [InlineData("{\n  \"a\": 1,\n  \"b\": [1, 2]\n}\n")]
    public void U3OnWithNoStatusLineInstallsAnEmptyGuardAndOffRestoresTheFileByteForByte(string original)
    {
        WriteUserSettings(original);

        var (code, _, stderr) = TapOn();

        Assert.True(code == 0, stderr);
        var installed = File.ReadAllText(UserSettingsFile);
        Assert.Contains("statusline-tap.sh", installed, StringComparison.Ordinal);
        Assert.True(File.Exists(TapScript));

        var (offCode, _, offErr) = TapOn("off");

        Assert.True(offCode == 0, offErr);
        Assert.Equal(original, File.ReadAllText(UserSettingsFile));
        Assert.False(File.Exists(TapBackup));
    }

    [Fact]
    public void U3OnWithNoSettingsFileCreatesOneAndOffRemovesIt()
    {
        var (code, _, stderr) = TapOn();

        Assert.True(code == 0, stderr);
        Assert.Contains("statusline-tap.sh", File.ReadAllText(UserSettingsFile), StringComparison.Ordinal);

        var (offCode, _, offErr) = TapOn("off");

        Assert.True(offCode == 0, offErr);
        Assert.False(File.Exists(UserSettingsFile));
    }

    [Fact]
    public void U3ARealStatusLineIsStillWrappedNotReplacedByAnEmptyGuard()
    {
        WriteUserSettings(TuiSettings);

        TapOn();

        Assert.Contains("claude-tui-line", File.ReadAllText(UserSettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void S4RefusesAStatuslineTapWithoutFixAndAValueThatIsNotOnOrOff()
    {
        WriteUserSettings(TuiSettings);

        var (withoutFix, _, e1) = Roster(null, "doctor", "--statusline-tap", "on");
        var (badValue, _, e2) = TapOn("maybe");

        Assert.Equal(2, withoutFix);
        Assert.Contains("--statusline-tap needs --fix", e1);
        Assert.Equal(2, badValue);
        Assert.Contains("takes on or off", e2);
        Assert.Equal(TuiSettings, File.ReadAllText(UserSettingsFile));
    }

    [Fact]
    public void S5TheScriptPassesTheInputThroughByteForByteKeepsTheExitStatusAndStderrAndWritesTheCopy()
    {
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"cat; echo oops >&2; exit 3\"}}");
        TapOn();

        var (stdout, stderr, code) = RunStatusline(GuardCommand(), SessionInput);

        Assert.Equal(SessionInput, stdout);
        Assert.Equal("oops\n", stderr);
        Assert.Equal(3, code);
        Assert.Equal(SessionInput, File.ReadAllText(Path.Combine(TapData, "abc-123.json")));
    }

    [Fact]
    public void S5TheCopyKeepsAMissingTrailingNewlineAndALongSingleLine()
    {
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"cat\"}}");
        TapOn();
        var input = "{\"session_id\":\"s1\",\"pad\":\"" + new string('x', 200_000) + "\"}";

        var (stdout, _, code) = RunStatusline(GuardCommand(), input);

        Assert.Equal(0, code);
        Assert.Equal(input, stdout);
        Assert.Equal(input, File.ReadAllText(Path.Combine(TapData, "s1.json")));
    }

    [Fact]
    public void S6TheGuardRunsTheOriginalCommandWhenTheScriptIsGone()
    {
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"cat; exit 4\"}}");
        TapOn();
        File.Delete(TapScript);

        var (stdout, _, code) = RunStatusline(GuardCommand(), SessionInput);

        Assert.Equal(SessionInput, stdout);
        Assert.Equal(4, code);
        Assert.False(Directory.Exists(TapData));
        Assert.Contains("broken", TapRow());
        Assert.Contains("missing", TapRow());
    }

    [Theory]
    [InlineData("{\"session_id\":\"../../x\",\"a\":1}\n")]
    [InlineData("{\"session_id\":\"a/b\"}\n")]
    [InlineData("{\"session_id\":\"a b\"}\n")]
    [InlineData("{\"session_id\":\"a.b\"}\n")]
    [InlineData("{\"session_id\":\"\"}\n")]
    [InlineData("{\"model\":\"no session id here\"}\n")]
    [InlineData("not json at all\n")]
    public void S7AnInvalidOrMissingSessionIdWritesNoFileAndTheOutputIsUnchanged(string input)
    {
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"cat\"}}");
        TapOn();
        var before = Tree();

        var (stdout, _, code) = RunStatusline(GuardCommand(), input);

        Assert.Equal(input, stdout);
        Assert.Equal(0, code);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void S7ASessionIdOf128CharactersIsAcceptedAndOneOf129IsNot()
    {
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"cat\"}}");
        TapOn();
        var ok = new string('a', 128);
        var tooLong = new string('b', 129);

        RunStatusline(GuardCommand(), "{\"session_id\":\"" + ok + "\"}\n");
        RunStatusline(GuardCommand(), "{\"session_id\":\"" + tooLong + "\"}\n");

        Assert.True(File.Exists(Path.Combine(TapData, ok + ".json")));
        Assert.False(File.Exists(Path.Combine(TapData, tooLong + ".json")));
    }

    [Fact]
    public void S8AnUnwritableTapDirectoryWritesNothingAndTheOutputAndStatusAreUnchanged()
    {
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"cat; exit 5\"}}");
        TapOn();
        Directory.CreateDirectory(TapData);
        File.SetUnixFileMode(TapData, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (File.Exists(Path.Combine(TapData, ".probe")) || CanWriteDespiteMode(TapData))
            {
                return;
            }

            var (stdout, stderr, code) = RunStatusline(GuardCommand(), SessionInput);

            Assert.Equal(SessionInput, stdout);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(5, code);
            Assert.Empty(Directory.GetFileSystemEntries(TapData));
        }
        finally
        {
            File.SetUnixFileMode(TapData, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>True for a user the mode does not bind (root), where the unwritable-directory case cannot be built.</summary>
    private static bool CanWriteDespiteMode(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void S9TheTapFileIsMode0600InAMode0700DirectoryWrittenThroughATempFileAndRename()
    {
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"cat\"}}");
        TapOn();

        RunStatusline(GuardCommand(), SessionInput);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(TapData, "abc-123.json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(TapData));
        Assert.Equal(["abc-123.json"], Directory.GetFileSystemEntries(TapData).Select(f => Path.GetFileName(f)!).ToArray());
        Assert.Contains("mv -f \"$tmp\" \"$d/$sid.json\"", File.ReadAllText(TapScript));
    }

    [Theory]
    [InlineData("printf '%s|%s|%s\\n' 'it'\\''s' \"$HOME\" 'a b;c'")]
    [InlineData("echo \"$HOME\"; echo 'a;b'; echo \"x y\"")]
    [InlineData("echo it\\'s")]
    public void S10AnOriginalCommandWithQuotesDollarsSpacesAndSemicolonsRunsExactlyAsBeforeTheWrap(string original)
    {
        var json = "{\"statusLine\":{\"type\":\"command\",\"command\":" + JsonText.Stringify(JsonValue.FromString(original), 0) + "}}";
        WriteUserSettings(json);
        TapOn();
        var direct = RunStatusline(original, SessionInput);

        var wrapped = RunStatusline(GuardCommand(), SessionInput);

        Assert.Equal(direct, wrapped);
        Assert.NotEqual(string.Empty, wrapped.Stdout);
    }

    [Fact]
    public void S10ACommandThatExitsWithoutReadingItsInputIsNeitherAnExceptionHereNorNoiseOnStderrThroughTheTap()
    {
        var original = "echo hi; exit 3";
        var big = "{\"session_id\":\"abc-123\",\"pad\":\"" + new string('a', 1_000_000) + "\"}\n";
        WriteUserSettings("{\"statusLine\":{\"type\":\"command\",\"command\":\"" + original + "\"}}");
        TapOn();
        const string ignoreSigpipe = "trap '' PIPE; ";
        var direct = RunStatusline(ignoreSigpipe + original, big);

        var wrapped = RunStatusline(ignoreSigpipe + GuardCommand(), big);

        Assert.Equal(("hi\n", string.Empty, 3), direct);
        Assert.Equal(direct, wrapped);
        Assert.Equal(big, File.ReadAllText(Path.Combine(TapData, "abc-123.json")));
    }

    [Fact]
    public void S11AStatuslineRewrittenByAnotherToolIsReportedBrokenAndOffRefusesToOverwrite()
    {
        WriteUserSettings(TuiSettings);
        TapOn();
        var rewritten = "{\"statusLine\":{\"type\":\"command\",\"command\":\"another-tool\"}}";
        WriteUserSettings(rewritten);

        Assert.Contains("tap not installed: statusLine changed", TapRow());
        var (code, _, stderr) = TapOn("off");

        Assert.Equal(2, code);
        Assert.Contains("statusLine was changed since the tap was installed; restored nothing; the backup is kept at " + TapBackup, stderr);
        Assert.Equal(rewritten, File.ReadAllText(UserSettingsFile));
        Assert.True(File.Exists(TapBackup));
    }

    [Fact]
    public void AnEnabledAhV1IsAWarnRowNamingTheFileAndADisabledOrAh2OnlyOneIsNoRow()
    {
        bool HasRow() => Parse(Roster(null, "doctor").Stdout)["rows"].AsArray().Any(r => r.Member("name")!.Value.AsString() == "ah-v1");

        WriteUserSettings("{\"enabledPlugins\":{\"ah@agent-tools\":false,\"wrangl@wrangl\":true}}");
        Assert.False(HasRow());

        var projectSettings = Path.Combine(Project, ".claude", "settings.json");
        File.WriteAllText(projectSettings, "{\"enabledPlugins\":{\"ah@agent-tools\":true}}");
        var row = Row(Parse(Roster(null, "doctor").Stdout), "ah-v1");
        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains(projectSettings, row["detail"].AsString());
        Assert.DoesNotContain(UserSettingsFile, row["detail"].AsString());
        Assert.Contains("/plugin disable ah@agent-tools", row["detail"].AsString());
    }

    [Fact]
    public void S12AProjectStatuslineBypassesTheTapAndOneWithoutIsNotReported()
    {
        WriteUserSettings(TuiSettings);
        TapOn();
        var projectSettings = Path.Combine(Project, ".claude", "settings.json");
        File.WriteAllText(projectSettings, "{\"permissions\":{}}");
        Assert.Equal("statusline tap: on", TapRow());

        File.WriteAllText(projectSettings, "{\"statusLine\":{\"type\":\"command\",\"command\":\"mine\"}}");

        Assert.Contains("tap bypassed in this project by " + projectSettings, TapRow());
        File.Delete(projectSettings);
        var local = Path.Combine(Project, ".claude", "settings.local.json");
        File.WriteAllText(local, "{\"statusLine\":{\"type\":\"command\",\"command\":\"mine\"}}");
        Assert.Contains("tap bypassed in this project by " + local, TapRow());
    }

    [Fact]
    public void S12AProjectStatuslineIsNotReportedWhileTheTapIsOff()
    {
        File.WriteAllText(Path.Combine(Project, ".claude", "settings.json"), "{\"statusLine\":{\"type\":\"command\",\"command\":\"mine\"}}");

        Assert.Equal("statusline tap: off", TapRow());
    }

    [Fact]
    public void S13APlainFixNeverInstallsTheTap()
    {
        WriteUserSettings(TuiSettings);

        var (code, stdout, stderr) = Roster(null, "doctor", "--fix");

        Assert.True(code == 0, stderr);
        Assert.Empty(Parse(stdout)["fix"].AsArray());
        Assert.Equal(TuiSettings, File.ReadAllText(UserSettingsFile));
        Assert.False(File.Exists(TapScript));
        Assert.False(File.Exists(TapBackup));
        Assert.Equal("statusline tap: off", TapRow());
    }

    [Fact]
    public void S14FixDeletesTapFilesOlderThanSevenDaysOnly()
    {
        Directory.CreateDirectory(TapData);
        var old = Path.Combine(TapData, "old.json");
        var oldTemp = Path.Combine(TapData, ".old.1.tmp");
        var recent = Path.Combine(TapData, "recent.json");
        var justUnder = Path.Combine(TapData, "six-days.json");
        foreach (var f in new[] { old, oldTemp, recent, justUnder })
        {
            File.WriteAllText(f, "{}");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(justUnder, DateTime.UtcNow.AddDays(-6).AddHours(-23));

        Roster(null, "doctor");
        Assert.True(File.Exists(old));

        var (code, stdout, _) = Roster(null, "doctor", "--fix");

        Assert.Equal(0, code);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(justUnder));
        Assert.Contains("pruned 2 statusline tap file(s)", Parse(stdout)["fix"].AsArray().Single().AsString());
    }

    [Fact]
    public void TheStatusLineEditTouchesOnlyTheTopLevelMemberNotDecoysInNestedObjectsOrStrings()
    {
        var original = "{\"a\":{\"statusLine\":1},\"note\":\"\\\"statusLine\\\": 2\",\"statusLine\" : {\"type\":\"command\",\"command\":\"c}\"} ,\"z\":3}";
        WriteUserSettings(original);

        var (code, _, stderr) = TapOn();

        Assert.True(code == 0, stderr);
        var text = File.ReadAllText(UserSettingsFile);
        Assert.StartsWith("{\"a\":{\"statusLine\":1},\"note\":\"\\\"statusLine\\\": 2\",\"statusLine\" : {", text);
        Assert.EndsWith("} ,\"z\":3}", text);
        TapOn("off");
        Assert.Equal(original, File.ReadAllText(UserSettingsFile));
    }

    [Fact]
    public void ASettingsFileWithTwoStatusLinesIsRefusedAndUnchanged()
    {
        var settings = "{\"statusLine\":{\"type\":\"command\",\"command\":\"x\"},\"statusLine\":{\"type\":\"command\",\"command\":\"y\"}}";
        WriteUserSettings(settings);

        var (code, _, stderr) = TapOn();

        Assert.Equal(2, code);
        Assert.Contains("settings.json could not be read as JSON; nothing was changed", stderr);
        Assert.Equal(settings, File.ReadAllText(UserSettingsFile));
        Assert.False(File.Exists(TapBackup));
    }
}

