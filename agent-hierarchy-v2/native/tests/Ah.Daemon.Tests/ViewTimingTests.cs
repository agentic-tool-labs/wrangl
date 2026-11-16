#pragma warning disable xUnit1051 // every wait here carries its own hard deadline

using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Daemon.Tests;

/// <summary>The view-timing line of a request: one line per page build, from the counting reader, the sections and the runner decorator.</summary>
public sealed partial class ViewTimingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private const string Root = "/repo";

    private sealed class FakeFiles : IFileReader
    {
        public string ReadText(string path) => "text of " + path;

        public byte[] ReadBytes(string path) => new byte[7];

        public FileTail ReadTail(string path, int maxBytes) => new(new byte[3], 3, false);

        public FileRange ReadRange(string path, long offset, int maxBytes) => new([], 0);

        public NoFollowRead ReadNoFollow(string path, int maxBytes) => new(FileKind.File, 5, new byte[5]);

        public FileStat? Stat(string path, bool followSymlinks = true) => null;

        public bool Exists(string path) => false;

        public string RealPath(string path) => path;

        public IReadOnlyList<string> List(string directory) => [];

        public bool IsExecutable(string path) => false;
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request) => new(0, null, false, false, default, default);
    }

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

        public TimeSpan LocalOffset(DateTimeOffset instant) => TimeSpan.Zero;
    }

    private sealed class FakeRevs : IStatusRevs
    {
        public StatusReading? Read(string dir) => null;

        public void Forget(string dir)
        {
        }
    }

    private sealed class Rig(bool timing, Action<IFileReader, IProcessRunner> build, bool needsHerdr = false)
    {
        internal List<string> Lines { get; } = [];

        internal CallCounter<IFileReader> Counter { get; private set; } = null!;

        internal async Task<string?> RunAsync(string path = "/detail/x")
        {
            var counted = CallCounting.Wrap<IFileReader>(new FakeFiles(), out var counter);
            Counter = counter;
            var files = WatchModule.TimeFiles(counted);
            var runner = WatchModule.TimeProcesses(new FakeRunner());
            var deps = new ViewServiceDependencies(
                null!,
                new FakeRevs(),
                WatchModule.CreateStatusDirs(),
                new ManualClock(),
                static (_, _) => Task.FromResult(new JsonArray()),
                Log: Lines.Add,
                Timing: timing);
            var service = new ViewService(deps, (p, _, _, herdr) =>
            {
                build(files, runner);
                if (needsHerdr)
                {
                    _ = herdr();
                }

                return new ViewAnswer(200, new JsonObject { ["view_schema"] = 1, ["path"] = p, ["rev"] = "r1" });
            });
            var request = new RouteRequest("GET", "/v1/view", "path=" + Uri.EscapeDataString(path), new Dictionary<string, string>(), default, new Dictionary<string, string>());
            _ = await service.ViewAsync(request, CancellationToken.None).WaitAsync(Deadline, CancellationToken.None);
            return Lines.SingleOrDefault(static l => l.StartsWith("view-timing ", StringComparison.Ordinal));
        }
    }

    [GeneratedRegex(@"io=\{opens:(\d+),reads:(\d+),bytes:(\d+),lists:(\d+),stats:(\d+)\}")]
    private static partial Regex IoRe();

    private static double Section(string line, string name) =>
        double.Parse(Regex.Match(line, @"[{,]" + Regex.Escape(name) + @":([\d.]+)").Groups[1].Value, CultureInfo.InvariantCulture);

    private sealed class SlowModel : IViewModel
    {
        public bool UsedHerdr => false;

        public ViewAnswer Build(string path)
        {
            using (WatchModule.ViewTimingSection("build"))
            {
                Thread.Sleep(300);
            }

            return new ViewAnswer(200, new JsonObject { ["view_schema"] = 1, ["path"] = path, ["rev"] = "r1" });
        }
    }

    [Fact]
    public async Task TwoRequestsForOnePageShowOneBuildersSectionsAndOneWaiter()
    {
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var service = new ViewService(
            new ViewServiceDependencies(null!, new FakeRevs(), WatchModule.CreateStatusDirs(), new ManualClock(), static (_, _) => Task.FromResult(new JsonArray()), Log: lines.Add, Timing: true),
            models: (_, _, _) => new SlowModel());
        var request = new RouteRequest("GET", "/v1/view", "path=%2Fdetail%2Fx", new Dictionary<string, string>(), default, new Dictionary<string, string>());

        _ = await Task.WhenAll(
            service.ViewAsync(request, CancellationToken.None).WaitAsync(Deadline, CancellationToken.None),
            service.ViewAsync(request, CancellationToken.None).WaitAsync(Deadline, CancellationToken.None));

        var timed = lines.Where(static l => l.StartsWith("view-timing ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, timed.Count);
        var builder = Assert.Single(timed, static l => !l.Contains("shared-", StringComparison.Ordinal));
        var waiter = Assert.Single(timed, static l => l.Contains("shared-wait:", StringComparison.Ordinal));
        Assert.True(Section(builder, "build") >= 250, builder);
        Assert.DoesNotContain("build:", waiter, StringComparison.Ordinal);
        Assert.True(Section(waiter, "shared-wait") >= 150, waiter);
    }

    [Fact]
    public async Task NegativeARequestAloneHasNoSharedSection()
    {
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var service = new ViewService(
            new ViewServiceDependencies(null!, new FakeRevs(), WatchModule.CreateStatusDirs(), new ManualClock(), static (_, _) => Task.FromResult(new JsonArray()), Log: lines.Add, Timing: true),
            models: (_, _, _) => new SlowModel());
        var request = new RouteRequest("GET", "/v1/view", "path=%2Fdetail%2Fx", new Dictionary<string, string>(), default, new Dictionary<string, string>());

        _ = await service.ViewAsync(request, CancellationToken.None).WaitAsync(Deadline, CancellationToken.None);

        var line = Assert.Single(lines);
        Assert.DoesNotContain("shared-", line, StringComparison.Ordinal);
        Assert.True(Section(line, "build") >= 250, line);
    }

    [Fact]
    public async Task OneLinePerRequestHasIoCountsEqualToTheCountedReaderOnTheSameBuild()
    {
        var rig = new Rig(true, (files, unusedRunner) =>
        {
            _ = files.ReadText(Root + "/.claude/ah2/msgs/a.jsonl");
            _ = files.ReadBytes(Root + "/.claude/ah2/msgs/b.jsonl");
            _ = files.ReadTail(Root + "/.claude/ah2/teams/t.json", 100);
            _ = files.ReadNoFollow(Root + "/.claude/ah2/teams/u.json", 100);
            _ = files.Stat(Root + "/.claude/ah2/teams");
            _ = files.Exists(Root + "/.claude/ah2/msgs");
            _ = files.List(Root + "/.claude/ah2/msgs");
        });

        var line = await rig.RunAsync();

        Assert.NotNull(line);
        var io = IoRe().Match(line);
        Assert.True(io.Success, line);
        var reads = rig.Counter.Count("ReadText") + rig.Counter.Count("ReadBytes") + rig.Counter.Count("ReadTail") + rig.Counter.Count("ReadNoFollow");
        Assert.Equal(reads, int.Parse(io.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(reads, int.Parse(io.Groups[2].Value, CultureInfo.InvariantCulture));
        Assert.Equal(rig.Counter.Count("List"), int.Parse(io.Groups[4].Value, CultureInfo.InvariantCulture));
        Assert.Equal(rig.Counter.Count("Stat") + rig.Counter.Count("Exists"), int.Parse(io.Groups[5].Value, CultureInfo.InvariantCulture));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("text of " + Root + "/.claude/ah2/msgs/a.jsonl") + 7 + 3 + 5, int.Parse(io.Groups[3].Value, CultureInfo.InvariantCulture));
        Assert.StartsWith("view-timing path=/detail/x total_ms=", line, StringComparison.Ordinal);
        Assert.Contains(" json_ms=", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeWithTheSwitchOffNoLineIsLoggedAndNothingIsCounted()
    {
        var rig = new Rig(false, (files, unusedRunner) => files.ReadText(Root + "/.claude/ah2/msgs/a.jsonl"));

        var line = await rig.RunAsync();

        Assert.Null(line);
        Assert.Empty(rig.Lines);
        Assert.Null(WatchModule.CurrentViewTiming);
    }

    [Fact]
    public async Task TheDirectoryTallyListsAPlantedUnknownSubdirectoryIfAndOnlyIfItIsRead()
    {
        var planted = Root + "/.claude/ah2/rc3-item0-verify/deep/y.json";
        var read = await new Rig(true, (files, unusedRunner) =>
        {
            _ = files.ReadText(Root + "/.claude/ah2/msgs/a.jsonl");
            _ = files.ReadText(planted);
        }).RunAsync();
        var notRead = await new Rig(true, (files, unusedRunner) => files.ReadText(Root + "/.claude/ah2/msgs/a.jsonl")).RunAsync();

        Assert.Contains(".claude/ah2/rc3-item0-verify:files=1", read, StringComparison.Ordinal);
        Assert.Contains(".claude/ah2/msgs:files=1", read, StringComparison.Ordinal);
        Assert.DoesNotContain("rc3-item0-verify", notRead, StringComparison.Ordinal);
        Assert.Contains(".claude/ah2/msgs:files=1", notRead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTallyCutsAPathAtDepthThreeFromItsHierarchyRoot()
    {
        var line = await new Rig(true, (files, unusedRunner) =>
        {
            _ = files.ReadText("/a/b/wt1/.claude/ah2/msgs/sub/deeper/x.json");
            _ = files.ReadText("/other/wt2/.claude/ah2/msgs/y.json");
            _ = files.ReadText("/a/b/wt1/.claude/ah2/state.json");
        }).RunAsync();

        Assert.Contains(".claude/ah2/msgs:files=2", line, StringComparison.Ordinal);
        Assert.Contains(".claude/ah2:files=1", line, StringComparison.Ordinal);
        Assert.DoesNotContain("deeper", line, StringComparison.Ordinal);
        Assert.DoesNotContain("wt1", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitAndProcessTableCallsAreCountedApartAndOtherRunsAreNeither()
    {
        var line = await new Rig(true, (unusedFiles, runner) =>
        {
            _ = runner.Run(new ProcessRequest(["/usr/bin/git", "status"]));
            _ = runner.Run(new ProcessRequest(["git", "log"]));
            _ = runner.Run(new ProcessRequest(["/bin/ps", "-p", "1"]));
            _ = runner.Run(new ProcessRequest(["tmux", "list-sessions"]));
        }).RunAsync();

        Assert.Contains(" git={n:2,", line, StringComparison.Ordinal);
        Assert.Contains(" ps={n:1,", line, StringComparison.Ordinal);
        Assert.Contains(" exec={n:1,", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestThatNeedsTheHerdrSnapshotBuildsTwiceAndTheLineSaysSo()
    {
        var line = await new Rig(true, (_, _) => { }, needsHerdr: true).RunAsync();

        Assert.Contains(" builds=2 ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeARequestThatNeedsNoHerdrBuildsOnce()
    {
        var line = await new Rig(true, (_, _) => { }).RunAsync();

        Assert.Contains(" builds=1 ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeReadsAndRunsOutsideARequestRecordNothingAndStillAnswer()
    {
        var files = WatchModule.TimeFiles(new FakeFiles());
        var runner = WatchModule.TimeProcesses(new FakeRunner());

        Assert.Equal("text of /x", files.ReadText("/x"));
        Assert.Equal(0, runner.Run(new ProcessRequest(["git"])).ExitCode);
        Assert.Null(WatchModule.CurrentViewTiming);
    }
}
