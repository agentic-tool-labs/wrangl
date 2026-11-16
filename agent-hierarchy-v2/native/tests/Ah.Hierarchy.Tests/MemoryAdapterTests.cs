using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;

namespace Ah.Hierarchy.Tests;

/// <summary>The memory adapter: which one the config picks, that a line is handed over exactly once and only recorded as delivered after the adapter said so, and that none records nothing.</summary>
public sealed class MemoryAdapterTests
{
    private sealed class Adapter(Func<int, byte[], bool> accepts) : IProcessRunner
    {
        public List<(IReadOnlyList<string> Argv, string Line, TimeSpan? Timeout, string? Team)> Calls { get; } = [];

        public List<string> Stored { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            var line = System.Text.Encoding.UTF8.GetString(request.Stdin.Span);
            Calls.Add((request.Argv, line, request.Timeout, request.Environment?["AH_MEMORY_TEAM"]));
            var ok = accepts(Calls.Count, request.Stdin.ToArray());
            if (ok)
            {
                Stored.AddRange(line.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }

            return new ProcessResult(ok ? 0 : 1, null, false, false, default, default);
        }
    }

    private static void UserConfig(ProgressDomainTests.Rig rig, string json)
    {
        var dir = Path.Combine(rig.Home, ".claude", "ah2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "agent-hierarchy.json"), json);
    }

    private static void ProjectConfig(ProgressDomainTests.Rig rig, string json) => File.WriteAllText(Path.Combine(rig.Pool, "agent-hierarchy.json"), json);

    private static void Event(ProgressDomainTests.Rig rig, string text) =>
        Assert.True(rig.Services.Progress.AppendLedger(rig.Pool, "t", new LedgerEvent("decision", Text: text)).Appended);

    // per-dir-files-to-db S11c: the cursor is the seq of the last row given, in the database; "none" before the first
    private static Ah.Abstractions.IProgressLogSource Logs(ProgressDomainTests.Rig rig) => Ah.StateDb.StateDbModule.CreateProgressLogs(rig.Home + "/.claude/ah2");

    private static string CursorOf(ProgressDomainTests.Rig rig) =>
        Logs(rig).LedgerCursor(rig.Pool, "t") is var seq and > 0 ? seq.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";

    private static long LedgerSize(ProgressDomainTests.Rig rig) => Logs(rig).Rows(rig.Pool, "t", "ledger.jsonl") is { Count: > 0 } rows ? rows[^1].Seq : 0;

    private static int Forward(ProgressDomainTests.Rig rig, IProcessRunner runner, string? path = null) =>
        rig.Services.Progress.ForwardLedgers(rig.Pool, runner, "/no/plugin", path, "repo");

    private const string CommandConfig = """{"memory":{"adapter":{"command":["fake-adapter","--flag"]}}}""";

    [Fact]
    public void ALineIsDeliveredOnceAfterTheAdapterFailsTwiceAndTheCursorMovesOnlyAfterEachSuccess()
    {
        var rig = ProgressDomainTests.Make();
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        Event(rig, "two");
        Event(rig, "three");
        var adapter = new Adapter((n, _) => n > 2);

        Assert.Equal(0, Forward(rig, adapter));
        Assert.Equal("none", CursorOf(rig));
        Assert.Equal(0, Forward(rig, adapter));
        Assert.Equal("none", CursorOf(rig));
        Assert.Equal(3, Forward(rig, adapter));

        Assert.Equal(["one", "two", "three"], adapter.Stored.Select(l => JsonText.Parse(l).Member("text")!.Value.AsString()));
        Assert.Equal(LedgerSize(rig).ToString(System.Globalization.CultureInfo.InvariantCulture), CursorOf(rig));
        Assert.Equal(0, Forward(rig, adapter));
        Assert.Equal(3, adapter.Calls.Count);
        Assert.All(adapter.Calls, call => Assert.Equal(["fake-adapter", "--flag", "record"], call.Argv));
        Assert.All(adapter.Calls, call => Assert.Equal(TimeSpan.FromSeconds(5) + (3 * TimeSpan.FromMilliseconds(50)), call.Timeout));
        Assert.All(adapter.Calls, call => Assert.Equal("t", call.Team));
    }

    [Fact]
    public void ThreeNewLinesGoToTheAdapterInOneRunAndTheCursorMovesOnlyAfterItExitsZero()
    {
        var rig = ProgressDomainTests.Make();
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        Event(rig, "two");
        Event(rig, "three");
        var cursorDuringRun = "unset";
        var adapter = new Adapter((_, _) =>
        {
            cursorDuringRun = CursorOf(rig);
            return true;
        });

        Assert.Equal(3, Forward(rig, adapter));

        var call = Assert.Single(adapter.Calls);
        Assert.Equal(3, call.Line.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("\n", call.Line, StringComparison.Ordinal);
        Assert.Equal("none", cursorDuringRun);
        Assert.Equal(LedgerSize(rig).ToString(System.Globalization.CultureInfo.InvariantCulture), CursorOf(rig));
    }

    [Fact]
    public void AnAdapterExitingNonZeroLeavesTheCursorAndTheNextRunResendsTheSameBatch()
    {
        var rig = ProgressDomainTests.Make();
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        Event(rig, "two");
        var adapter = new Adapter((n, _) => n > 1);

        Assert.Equal(0, Forward(rig, adapter));
        Assert.Equal("none", CursorOf(rig));
        Assert.Equal(2, Forward(rig, adapter));

        Assert.Equal(2, adapter.Calls.Count);
        Assert.Equal(adapter.Calls[0].Line, adapter.Calls[1].Line);
        Assert.Equal(["one", "two"], adapter.Stored.Select(l => JsonText.Parse(l).Member("text")!.Value.AsString()));
    }

    private sealed class Scripted(Func<string, (int Exit, string Stderr)> answer) : IProcessRunner
    {
        public List<string> Batches { get; } = [];

        public List<IReadOnlyDictionary<string, string?>?> Environments { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            var text = System.Text.Encoding.UTF8.GetString(request.Stdin.Span);
            Batches.Add(text);
            Environments.Add(request.Environment);
            var (exit, stderr) = answer(text);
            return new ProcessResult(exit, null, false, false, default, System.Text.Encoding.UTF8.GetBytes(stderr));
        }
    }

    [Fact]
    public void TheAdapterChildGetsThePathTheDaemonHasSoItCanFindEngram()
    {
        var rig = ProgressDomainTests.Make();
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        var adapter = new Scripted(_ => (0, string.Empty));

        Assert.Equal(1, Forward(rig, adapter, "/opt/tools:/usr/bin"));

        var environment = Assert.Single(adapter.Environments)!;
        Assert.Equal("/opt/tools:/usr/bin", environment["PATH"]);
        Assert.Equal("repo", environment["AH_MEMORY_REPO"]);
    }

    [Fact]
    public void TheStuckLogLineCarriesTheAdaptersExitAndTheTailOfItsStderr()
    {
        var errors = new Errors();
        var rig = ProgressDomainTests.Make(errors: errors);
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        var adapter = new Scripted(_ => (75, "ah-memory-engram: engram is not on PATH"));

        for (var run = 1; run <= StuckLedgers.Threshold; run++)
        {
            _ = Forward(rig, adapter);
        }

        var line = Assert.Single(errors.Lines);
        Assert.Contains("(exit 75: ah-memory-engram: engram is not on PATH)", line, StringComparison.Ordinal);
        Assert.Equal("none", CursorOf(rig));
    }

    [Fact]
    public void ALineTheAdapterRefusesOnItsOwnWhileItAcceptsTheRestIsSkippedAndLoggedAfterFiveRuns()
    {
        var errors = new Errors();
        var rig = ProgressDomainTests.Make(errors: errors);
        UserConfig(rig, CommandConfig);
        foreach (var text in new[] { "one", "two", "POISON", "four", "five" })
        {
            Event(rig, text);
        }

        var adapter = new Scripted(batch => batch.Contains("POISON", StringComparison.Ordinal) ? (1, "bad record") : (0, string.Empty));

        for (var run = 1; run < StuckLedgers.Threshold; run++)
        {
            Assert.Equal(0, Forward(rig, adapter));
        }

        Assert.Empty(errors.Lines);
        Assert.Equal(2, Forward(rig, adapter));
        Assert.NotEqual(LedgerSize(rig).ToString(System.Globalization.CultureInfo.InvariantCulture), CursorOf(rig));
        Assert.Empty(errors.Lines);
        Assert.Equal(2, Forward(rig, adapter));

        Assert.Equal(LedgerSize(rig).ToString(System.Globalization.CultureInfo.InvariantCulture), CursorOf(rig));
        var stored = adapter.Batches.Where(b => !b.Contains("POISON", StringComparison.Ordinal)).SelectMany(b => b.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Select(l => JsonText.Parse(l).Member("text")!.Value.AsString()).ToList();
        Assert.Equal(["five", "four", "one", "two"], stored.Distinct().Order(StringComparer.Ordinal));
        var line = Assert.Single(errors.Lines);
        Assert.Contains("skipped ledger row", line, StringComparison.Ordinal);
        Assert.Contains("POISON", line, StringComparison.Ordinal);
        Assert.Contains("bad record", line, StringComparison.Ordinal);
        Assert.Equal(0, Forward(rig, adapter));
    }

    [Fact]
    public void ALedgerRefusedForTheThresholdRunsRaisesOneAlarmUntilItRecoversAndAnotherAfterTheNextStreak()
    {
        var rig = ProgressDomainTests.Make();
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        var accept = false;
        var adapter = new Adapter((_, _) => accept);
        var alarms = new List<(string Team, string Text)>();
        void Run() => rig.Services.Progress.ForwardLedgers(rig.Pool, adapter, "/no/plugin", "/usr/bin", "repo", (team, text) => alarms.Add((team, text)));

        for (var run = 1; run < StuckLedgers.Threshold; run++)
        {
            Run();
        }

        Assert.Empty(alarms);
        Run();
        Run();
        Run();
        var alarm = Assert.Single(alarms);
        Assert.Equal("t", alarm.Team);
        Assert.Contains("refused the ledger batch after row 0", alarm.Text, StringComparison.Ordinal);

        accept = true;
        Run();
        accept = false;
        Event(rig, "two");
        for (var run = 1; run <= StuckLedgers.Threshold; run++)
        {
            Run();
        }

        Assert.Equal(2, alarms.Count);
    }

    [Fact]
    public void ASixHundredLineBatchWithOnePoisonLineSpendsAtMostTheCapPerRunAndStillSkipsThePoison()
    {
        var errors = new Errors();
        var rig = ProgressDomainTests.Make(errors: errors);
        UserConfig(rig, CommandConfig);
        for (var i = 0; i < 600; i++)
        {
            Event(rig, i == 300 ? "POISON" : "row" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var adapter = new Scripted(batch => batch.Contains("POISON", StringComparison.Ordinal) ? (1, "bad record") : (0, string.Empty));
        var size = LedgerSize(rig).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var runs = 0;
        var mostChildren = 0;
        while (CursorOf(rig) != size && runs < 20)
        {
            var before = adapter.Batches.Count;
            _ = Forward(rig, adapter);
            mostChildren = Math.Max(mostChildren, adapter.Batches.Count - before);
            runs++;
        }

        Assert.Equal(size, CursorOf(rig));
        Assert.InRange(runs, StuckLedgers.Threshold + 1, StuckLedgers.Threshold + 8);
        Assert.True(mostChildren <= 1 + MemoryForwarder.SalvageChildren, $"{mostChildren} adapter runs in one forwarding run");
        var skipped = Assert.Single(errors.Lines, l => l.Contains("skipped ledger row", StringComparison.Ordinal));
        Assert.Contains("POISON", skipped, StringComparison.Ordinal);
        var delivered = adapter.Batches.Where(b => !b.Contains("POISON", StringComparison.Ordinal)).SelectMany(b => b.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Distinct().Count();
        Assert.Equal(599, delivered);
    }

    [Fact]
    public void AnAdapterThatAcceptsTheFirstPieceAndThenFailsEveryTrySkipsNothingAndKeepsTheAcceptedPrefix()
    {
        var errors = new Errors();
        var rig = ProgressDomainTests.Make(errors: errors);
        UserConfig(rig, CommandConfig);
        foreach (var text in new[] { "one", "two", "three", "four", "five", "six" })
        {
            Event(rig, text);
        }

        var tries = 0;
        var adapter = new Scripted(_ => ++tries == StuckLedgers.Threshold + 1 ? (0, string.Empty) : (1, "database is locked"));

        for (var run = 1; run <= StuckLedgers.Threshold + 12; run++)
        {
            _ = Forward(rig, adapter);
        }

        Assert.DoesNotContain(errors.Lines, l => l.Contains("skipped the ledger line", StringComparison.Ordinal));
        var cursor = CursorOf(rig);
        Assert.NotEqual("none", cursor);
        Assert.NotEqual(LedgerSize(rig).ToString(System.Globalization.CultureInfo.InvariantCulture), cursor);
        Assert.Single(adapter.Batches, b => b.Contains("three", StringComparison.Ordinal) && !b.Contains("four", StringComparison.Ordinal));
        Assert.DoesNotContain(adapter.Batches.Skip(StuckLedgers.Threshold + 1), b => b.Contains("one", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(75)]
    [InlineData(127)]
    [InlineData(1)]
    public void ABrokenAdapterOrOneThatRefusesEveryLineSkipsNothingAndKeepsTheCursor(int exit)
    {
        var errors = new Errors();
        var rig = ProgressDomainTests.Make(errors: errors);
        UserConfig(rig, CommandConfig);
        foreach (var text in new[] { "one", "two", "three" })
        {
            Event(rig, text);
        }

        var adapter = new Scripted(_ => (exit, "refused"));

        for (var run = 1; run <= StuckLedgers.Threshold + 2; run++)
        {
            Assert.Equal(0, Forward(rig, adapter));
        }

        Assert.Equal("none", CursorOf(rig));
        Assert.DoesNotContain(errors.Lines, l => l.Contains("skipped the ledger line", StringComparison.Ordinal));
        Assert.Contains(errors.Lines, l => l.Contains("refused the ledger batch", StringComparison.Ordinal));
    }

    [Fact]
    public void EachForwardingRunReportsItsOwnChildrenAndWallTimeAndAFreshRecordsObjectStartsFromZero()
    {
        var rig = ProgressDomainTests.Make();
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        var adapter = new Adapter((n, _) =>
        {
            Thread.Sleep(15);
            return n > 1;
        });

        Assert.Equal((0, 0), rig.Services.Progress.LastForwardRun());
        _ = Forward(rig, adapter);
        _ = Forward(rig, adapter);
        var first = rig.Services.Progress.EndForwardRun();
        Event(rig, "two");
        _ = Forward(rig, adapter);
        var second = rig.Services.Progress.EndForwardRun();
        var idle = rig.Services.Progress.EndForwardRun();

        Assert.Equal(2, first.Children);
        Assert.True(first.WallMs >= 20, $"wall {first.WallMs} ms");
        Assert.Equal(1, second.Children);
        Assert.True(second.WallMs is >= 10 and < 100, $"wall {second.WallMs} ms");
        Assert.Equal((0, 0), idle);
        Assert.Equal((0, 0), rig.Services.Progress.LastForwardRun());
        Assert.Equal((0, 0), ProgressDomainTests.Make().Services.Progress.LastForwardRun());
    }

    [Fact]
    public void NegativeALedgerOnlyInAnOldFileIsNotReadByAForwardingRun()
    {
        var rig = ProgressDomainTests.Make();
        var folder = Path.Combine(rig.Pool, "progress", "old");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ledger.jsonl"), """{"v":1,"kind":"decision","text":"old"}""" + "\n");
        var adapter = new Adapter((_, _) => true);
        var logs = Ah.StateDb.StateDbModule.CreateProgressLogs(rig.Home + "/.claude/ah2");
        var forwarder = new MemoryForwarder(logs, rig.Services.StateCalls, adapter);

        Assert.Equal(0, forwarder.Run(rig.Pool, () => ["fake-adapter"], "repo"));
        Assert.Empty(adapter.Calls);
        Assert.Empty(logs.Teams(rig.Pool, null));
    }

    [Fact]
    public void NoNewBytesRunNoAdapterAndNeverResolveTheAdapter()
    {
        var rig = ProgressDomainTests.Make();
        Event(rig, "one");
        var adapter = new Adapter((_, _) => true);
        var resolves = 0;
        IReadOnlyList<string>? Argv()
        {
            resolves++;
            return ["fake-adapter"];
        }

        var forwarder = new MemoryForwarder(Ah.StateDb.StateDbModule.CreateProgressLogs(rig.Home + "/.claude/ah2"), rig.Services.StateCalls, adapter);

        Assert.Equal(1, forwarder.Run(rig.Pool, Argv, "repo"));
        Assert.Equal((1, 1), (resolves, adapter.Calls.Count));
        Assert.Equal(0, forwarder.Run(rig.Pool, Argv, "repo"));
        Assert.Equal((1, 1), (resolves, adapter.Calls.Count));
    }


    private sealed class Errors : IHookErrorLog
    {
        public List<string> Lines { get; } = [];

        public void Log(string source, string message) => Lines.Add(source + ": " + message);

        public void Log(HookErrorRecord record) => Lines.Add(record.Hook + ": " + record.Message);
    }

    [Fact]
    public void ALedgerTheAdapterKeepsRefusingLogsOneHookErrorOnTheFifthRunAndANewlyAcceptedLineStartsTheCountOver()
    {
        var errors = new Errors();
        var rig = ProgressDomainTests.Make(errors: errors);
        UserConfig(rig, CommandConfig);
        Event(rig, "one");
        var accept = false;
        var adapter = new Adapter((_, _) => accept);

        for (var run = 1; run <= 4; run++)
        {
            Forward(rig, adapter);
        }

        Assert.Empty(errors.Lines);
        Forward(rig, adapter);
        Assert.Single(errors.Lines);
        Assert.StartsWith("ledger-forward", errors.Lines[0], StringComparison.Ordinal);
        Forward(rig, adapter);
        Forward(rig, adapter);
        Assert.Single(errors.Lines);

        accept = true;
        Assert.Equal(1, Forward(rig, adapter));
        accept = false;
        Event(rig, "two");
        for (var run = 1; run <= 4; run++)
        {
            Forward(rig, adapter);
        }

        Assert.Single(errors.Lines);
    }

    [Fact]
    public void NoneWritesNoLedgerLineAndRecallsNothingAndForwardsNothing()
    {
        var rig = ProgressDomainTests.Make();
        ProjectConfig(rig, """{"memory":{"adapter":"none"}}""");
        Directory.CreateDirectory(Path.Combine(rig.Pool, "progress", "t"));
        File.WriteAllText(Path.Combine(rig.Pool, "progress", "t", "ledger.jsonl"), """{"v":1,"at":"2026-10-23T10:00:00.000Z","kind":"decision","text":"old","source":{}}""" + "\n");
        var adapter = new Adapter((_, _) => true);

        var result = rig.Services.Progress.AppendLedger(rig.Pool, "t", new LedgerEvent("decision", Text: "new"));

        Assert.False(result.Appended);
        Assert.Single(File.ReadAllLines(Path.Combine(rig.Pool, "progress", "t", "ledger.jsonl")));
        Assert.Null(rig.Services.Progress.RecallDigest(rig.Pool, "t", false));
        Assert.Equal(0, Forward(rig, adapter));
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public void AnotherValueOrNoConfigIsNotNoneAndStillWritesTheLedger()
    {
        var rig = ProgressDomainTests.Make();
        ProjectConfig(rig, """{"memory":{"adapter":"file"}}""");

        Event(rig, "kept");

        Assert.Single(Logs(rig).Rows(rig.Pool, "t", "ledger.jsonl"));
        ProjectConfig(rig, """{"memory":{"adapter":"bogus"}}""");
        Event(rig, "kept too");
        Assert.Equal(2, Logs(rig).Rows(rig.Pool, "t", "ledger.jsonl").Count);
    }

    [Fact]
    public void AutoWithNoEngramOnPathIsFileAndForwardsNothingButAutoWithEngramHandsTheLineToTheScript()
    {
        var rig = ProgressDomainTests.Make();
        var plugin = Path.Combine(rig.Home, "plugin");
        Directory.CreateDirectory(Path.Combine(plugin, "bin"));
        var script = Path.Combine(plugin, "bin", "ah-memory-engram");
        File.WriteAllText(script, "#!/bin/sh\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tools = Path.Combine(rig.Home, "tools");
        Directory.CreateDirectory(tools);
        Event(rig, "one");
        var adapter = new Adapter((_, _) => true);

        Assert.Equal(0, rig.Services.Progress.ForwardLedgers(rig.Pool, adapter, plugin, tools, "repo"));
        Assert.Empty(adapter.Calls);

        var engram = Path.Combine(tools, "engram");
        File.WriteAllText(engram, "#!/bin/sh\n");
        File.SetUnixFileMode(engram, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal(1, rig.Services.Progress.ForwardLedgers(rig.Pool, adapter, plugin, tools, "repo"));
        Assert.Equal([script, "record"], Assert.Single(adapter.Calls).Argv);
    }

    [Fact]
    public void FileForwardsNothingEvenWithEngramOnPath()
    {
        var rig = ProgressDomainTests.Make();
        ProjectConfig(rig, """{"memory":{"adapter":"file"}}""");
        Event(rig, "one");
        var adapter = new Adapter((_, _) => true);

        Assert.Equal(0, rig.Services.Progress.ForwardLedgers(rig.Pool, adapter, "/no/plugin", "/usr/bin", "repo"));
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public void ACommandAdapterIsHonouredFromTheUsersFileOnlyAndNeverFromTheRepos()
    {
        var project = ProgressDomainTests.Make();
        ProjectConfig(project, CommandConfig);
        Event(project, "one");
        var user = ProgressDomainTests.Make();
        UserConfig(user, CommandConfig);
        Event(user, "one");
        var fromProject = new Adapter((_, _) => true);
        var fromUser = new Adapter((_, _) => true);

        Assert.Equal(0, Forward(project, fromProject));
        Assert.Equal(1, Forward(user, fromUser));

        Assert.Empty(fromProject.Calls);
        Assert.Single(fromUser.Calls);
    }

    [Fact]
    public void AppendingALedgerLineNeverRunsTheAdapterThatIsTheDaemonsJob()
    {
        var rig = ProgressDomainTests.Make();
        var marker = Path.Combine(rig.Home, "adapter-ran");
        UserConfig(rig, """{"memory":{"adapter":{"command":["/bin/sh","-c","touch \"$1\"","ah",""" + $"\"{marker}\"" + """]}}}""");

        Event(rig, "one");
        rig.Services.Progress.RecordReport(rig.Pool, "t", "main", "m1", Path.Combine(rig.Pool, "msgs", "missing--x--y--response.md"), new ProgressAppend(true, false, false, false, null));

        Assert.False(File.Exists(marker));
        Assert.Equal(1, Forward(rig, PlatformModule.CreateProcessRunner()));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void AnAdapterThatHangsIsKilledAtTheFiveSecondTimeoutAndTheLineIsKeptForTheNextRun()
    {
        var rig = ProgressDomainTests.Make();
        UserConfig(rig, """{"memory":{"adapter":{"command":["/bin/sh","-c","sleep 31.7","ah"]}}}""");
        Event(rig, "one");

        var watch = Stopwatch.StartNew();
        var delivered = Forward(rig, PlatformModule.CreateProcessRunner());
        watch.Stop();

        Assert.Equal(0, delivered);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15));
        Assert.Equal("none", CursorOf(rig));
        using var check = Process.Start(new ProcessStartInfo("pgrep", ["-f", "sleep 31.7"]) { RedirectStandardOutput = true })!;
        Assert.Equal(string.Empty, check.StandardOutput.ReadToEnd().Trim());
        check.WaitForExit();
    }
}
