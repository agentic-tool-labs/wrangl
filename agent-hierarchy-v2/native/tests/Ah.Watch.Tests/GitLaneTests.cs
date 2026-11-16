using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class GitLaneTests
{
    private const string Dir = "/h/.ah";

    private sealed class Grants : IViewStateReader
    {
        public string? WorkRoot { get; set; } = "/w/alpha";

        public Dictionary<string, string> Registrations { get; } = [];

        public Dictionary<string, string[]> MoreAddresses { get; } = [];

        public string? SecondRoot { get; set; }

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (domain == "mailbox")
            {
                var id = args["session_id"].AsString();
                var registration = new JsonObject();
                if (Registrations.TryGetValue(id, out var address))
                {
                    registration["address"] = address;
                }

                if (MoreAddresses.TryGetValue(id, out var more))
                {
                    var list = new JsonArray();
                    foreach (var m in more)
                    {
                        list.Add(m);
                    }

                    registration["addresses"] = JsonValue.FromArray(list);
                }

                return registration.Count == 0 ? JsonValue.FromObject(new JsonObject()) : JsonValue.FromObject(new JsonObject { ["registration"] = JsonValue.FromObject(registration) });
            }

            return Grant(op);
        }

        private JsonValue Grant(string op)
        {
            if (op == "keys")
            {
                return JsonValue.FromObject(new JsonObject { ["teams"] = JsonValue.FromArray(new JsonArray { "t1" }) });
            }

            var members = new JsonObject
            {
                ["m1"] = JsonValue.FromObject(new JsonObject { ["work_root"] = WorkRoot is null ? JsonValue.Null : JsonValue.FromString(WorkRoot) }),
            };
            if (SecondRoot is not null)
            {
                members["m2"] = JsonValue.FromObject(new JsonObject { ["work_root"] = SecondRoot });
            }

            return JsonValue.FromObject(new JsonObject { ["present"] = true, ["members"] = JsonValue.FromObject(members) });
        }
    }

    private sealed class FakeGit : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Func<ProcessRequest, ProcessResult>? Behave { get; set; }

        public string? Output { get; set; }

        public ProcessResult Run(ProcessRequest request)
        {
            Requests.Add(request);
            return Behave is not null
                ? Behave(request)
                : Output is null
                    ? Result(exit: 128, stderr: "fatal: unable to read")
                    : Result(stdout: Output);
        }

        public static ProcessResult Result(int exit = 0, bool timedOut = false, bool truncated = false, string stdout = "", string stderr = "") =>
            new(timedOut ? null : exit, null, timedOut, truncated, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr));
    }

    private sealed class OtherLane : IModelLane
    {
        public string Name => "other";

        public IReadOnlyList<string> SliceIds(string key) => ["other:" + key];

        public IReadOnlyList<string> Keys() => [];

        public LaneResult Reread(string key, long nowMs) => new([new SliceChange("other:" + key, Encoding.UTF8.GetBytes(key), 0)]) { Record = new LaneRecord(JsonValue.FromString(key), 0) };
    }

    private sealed class ScriptedActivity : IModelLane
    {
        public string State { get; set; } = "";

        public string Name => ActivityLane.LaneName;

        public IReadOnlyList<string> SliceIds(string key) => [];

        public IReadOnlyList<string> Keys() => [];

        public LaneResult Reread(string key, long nowMs) => new([new SliceChange("a:" + key, Encoding.UTF8.GetBytes("{\"activity\":\"" + State + "\"}"), 0)]);
    }

    private sealed class Rig
    {
        public long Now { get; set; } = 1_000_000;

        public Grants State { get; } = new();

        public FakeGit Git { get; } = new() { Output = "# branch.oid abc123\0# branch.head main\0" };

        public List<string> Roots => [.. Git.Requests.Select(r => r.WorkingDirectory!)];

        public string? Output
        {
            get => Git.Output;
            set => Git.Output = value;
        }

        public List<string> Emitted { get; } = [];

        public GitLane Lane { get; }

        public Rig() => Lane = new GitLane(State, () => [Dir], Git, () => Now, Emitted.Add, "/h");

        public static string Key => GitLane.KeyOf(Dir, "t1", "m1");
    }

    private static JsonValue Body(LaneResult r) => JsonText.Parse(r.Changes.Single().Body!);

    [Fact]
    public void AStatusIsParsedIntoTheWhitelistedBodyWithFilesInPathOrder()
    {
        var rig = new Rig
        {
            Output = "# branch.oid 0123abc\0# branch.head feature\0# branch.upstream origin/feature\0# branch.ab +2 -1\0"
                + "1 .M N... 100644 100644 100644 aaa bbb src/b.cs\0"
                + "2 R. N... 100644 100644 100644 aaa bbb R100 src/new.cs\0src/old.cs\0"
                + "u UU N... 100644 100644 100644 100644 a b c src/a.cs\0"
                + "? zzz.txt\0! ignored.bin\0",
        };

        var body = Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.Equal("feature", body.Member("branch")!.Value.AsString());
        Assert.Equal("0123abc", body.Member("head")!.Value.AsString());
        Assert.Equal("origin/feature", body.Member("upstream")!.Value.AsString());
        Assert.Equal((2, 1), ((int)body.Member("ahead")!.Value.AsNumber(), (int)body.Member("behind")!.Value.AsNumber()));
        var files = body.Member("files")!.Value.AsArray().Select(f => (f.Member("path")!.Value.AsString(), f.Member("xy")!.Value.AsString())).ToList();
        Assert.Equal([("src/a.cs", "UU"), ("src/b.cs", ".M"), ("src/new.cs", "R."), ("zzz.txt", "??")], files);
        Assert.Equal(4, body.Member("files_total")!.Value.AsNumber());
        Assert.False(body.Member("truncated")!.Value.AsBool());
        Assert.Equal(0, rig.Lane.Reread(Rig.Key, 0).Changes.Single().AsOfMs);
    }

    [Fact]
    public void MoreThanFiveHundredFilesAreCutAtFiveHundredAndSaidSo()
    {
        var many = string.Concat(Enumerable.Range(0, 501).Select(i => $"? f{i:D4}.txt\0"));
        var rig = new Rig { Output = "# branch.oid abc\0# branch.head main\0" + many };

        var body = Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.Equal(500, body.Member("files")!.Value.AsArray().Count);
        Assert.Equal(501, body.Member("files_total")!.Value.AsNumber());
        Assert.True(body.Member("truncated")!.Value.AsBool());
    }

    [Fact]
    public void AFreshRepoAndADetachedHeadHaveNoHeadOrBranch()
    {
        var rig = new Rig { Output = "# branch.oid (initial)\0# branch.head (detached)\0" };

        var body = Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.True(body.Member("head")!.Value.IsNull);
        Assert.True(body.Member("branch")!.Value.IsNull);
        Assert.True(body.Member("ahead")!.Value.IsNull);
    }

    [Fact]
    public void AFailedRunIsAnErrorCodeWithNoOutputText()
    {
        var rig = new Rig { Output = null };

        var body = Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.Equal("failed", body.Member("error")!.Value.AsString());
        Assert.Equal(0, body.Member("files_total")!.Value.AsNumber());
    }

    [Theory]
    [InlineData(true, false, 0, "", "timeout")]
    [InlineData(false, true, 0, "", "truncated")]
    [InlineData(false, false, 128, "fatal: not a git repository (or any of the parent directories): .git", "not_a_repo")]
    [InlineData(false, false, 128, "fatal: unable to read tree", "failed")]
    public void AnErrorCodeIsMappedFromTheRunnersResultAndNoStderrIsCarried(bool timedOut, bool truncated, int exit, string stderr, string code)
    {
        var rig = new Rig();
        rig.Git.Behave = _ => FakeGit.Result(exit, timedOut, truncated, stderr: stderr);

        var json = Encoding.UTF8.GetString(rig.Lane.Reread(Rig.Key, 0).Changes.Single().Body!);

        Assert.Equal(code, JsonText.Parse(json).Member("error")!.Value.AsString());
        Assert.DoesNotContain("fatal", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExitZeroRunHasNoErrorMember()
    {
        var rig = new Rig();

        Assert.Null(Body(rig.Lane.Reread(Rig.Key, 0)).Member("error"));
    }

    [Fact]
    public void GitRunsWithAFifteenSecondTimeoutAndNoOptionalLocks()
    {
        var rig = new Rig();

        _ = rig.Lane.Reread(Rig.Key, 0);

        var request = Assert.Single(rig.Git.Requests);
        Assert.Equal(TimeSpan.FromSeconds(15), request.Timeout);
        Assert.Equal("0", request.Environment!["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("/w/alpha", request.WorkingDirectory);
        Assert.Equal("status", request.Argv[3]);
    }

    [Fact]
    public void AGitThatTimesOutIsATimeoutErrorAndAnotherLanesKeyStillPublishes()
    {
        var rig = new Rig();
        rig.Git.Behave = _ => FakeGit.Result(timedOut: true);
        var other = new OtherLane();
        var model = new ReadModel([rig.Lane, other], new ModelOptions { SeedAtStart = false }, () => rig.Now, null, "e", null, new ComposerRegistry());
        using var owner = model;
        model.Emit(GitLane.LaneName, Rig.Key);
        model.Emit("other", "o1");
        rig.Now += 200;

        while (model.Projector.Step())
        {
        }

        var slices = model.Store.Current.Slices;
        Assert.Contains("\"error\":\"timeout\"", Encoding.UTF8.GetString(slices[GitLane.SliceIdOf(Dir, "t1", "m1")].Body), StringComparison.Ordinal);
        Assert.Contains("o1", Encoding.UTF8.GetString(slices["other:o1"].Body), StringComparison.Ordinal);
    }

    [Fact]
    public void ARefreshIsOneGitRunAndASecondOneWithinFiveSecondsRunsNone()
    {
        var rig = new Rig();
        _ = rig.Lane.Reread(Rig.Key, 0);
        rig.Now += 4_999;

        _ = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal(["/w/alpha"], rig.Roots);
        Assert.Empty(rig.Lane.LapsedKeys());
        rig.Now += 1;
        Assert.Equal([Rig.Key], rig.Lane.LapsedKeys());

        _ = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal(2, rig.Roots.Count);
        Assert.Empty(rig.Lane.LapsedKeys());
    }

    [Fact]
    public void AWorktreeWithoutAWorkRootHasNoSliceAndGitNeverRuns()
    {
        var rig = new Rig();
        rig.State.WorkRoot = null;

        var result = rig.Lane.Reread(Rig.Key, 0);

        Assert.Null(result.Changes.Single().Body);
        Assert.Empty(rig.Roots);
    }

    [Fact]
    public void WithNoClientConnectedGitNeverRunsAndAClientArrivalRunsItOnce()
    {
        var rig = new Rig();
        var connected = false;
        var lazy = new LazyLane(rig.Lane, () => connected);

        _ = lazy.Reread(Rig.Key, 0);
        _ = lazy.Reread(Rig.Key, 0);
        Assert.Empty(rig.Roots);

        connected = true;
        _ = lazy.Reread(Rig.Key, 0);
        Assert.Single(rig.Roots);
    }

    [Fact]
    public void ATransitionRefreshesOnlyTheWorktreeOfTheSubjectsMember()
    {
        var rig = new Rig();
        rig.State.Registrations["s-1"] = "m1@t1";
        rig.State.Registrations["s-2"] = "m2@t1";
        rig.State.Registrations["s-3"] = "m1@t9";
        _ = rig.Lane.Keys();

        rig.Lane.Notify("/other/.ah", "s-1");
        rig.Lane.Notify(Dir, "unregistered");
        rig.Lane.Notify(Dir, "s-2");
        rig.Lane.Notify(Dir, "s-3");
        Assert.Empty(rig.Emitted);

        rig.Lane.Notify(Dir, "s-1");
        Assert.Equal([Rig.Key], rig.Emitted);
    }

    [Fact]
    public void OneRecorderFeedsBothTheTimelineAndGitFromASingleStateMap()
    {
        var inner = new ScriptedActivity();
        var appended = new List<string>();
        var seen = new List<(string, string)>();
        var store = new TimelineTests.MemoryStore();
        var recorder = new TimelineRecorder(inner, store, appended.Add, (d, s) => seen.Add((d, s)));
        var key = ActivityLane.KeyOf(Dir, "s-1");

        inner.State = "idle";
        _ = recorder.Reread(key, 1);
        Assert.Empty(seen);
        inner.State = "busy";
        _ = recorder.Reread(key, 2);
        _ = recorder.Reread(key, 3);

        Assert.Equal([(Dir, "s-1")], seen);
        Assert.Equal([Dir, Dir], appended);
    }

    [Fact]
    public void WithNoTimelineStoreTheRecorderStillReportsTransitionsAndAppendsNothing()
    {
        var inner = new ScriptedActivity();
        var seen = new List<(string, string)>();
        var appended = new List<string>();
        var recorder = new TimelineRecorder(inner, null, appended.Add, (d, s) => seen.Add((d, s)));
        var key = ActivityLane.KeyOf(Dir, "s-1");

        inner.State = "idle";
        _ = recorder.Reread(key, 1);
        inner.State = "busy";
        _ = recorder.Reread(key, 2);

        Assert.Equal([(Dir, "s-1")], seen);
        Assert.Empty(appended);
    }

    [Fact]
    public void ASessionRegisteredUnderSeveralAddressesNotifiesEachOfTheirWorktrees()
    {
        var rig = new Rig();
        rig.State.SecondRoot = "/w/beta";
        rig.State.Registrations["s-1"] = "m1@t1";
        rig.State.MoreAddresses["s-1"] = ["m2@t1", "m3@t1"];
        _ = rig.Lane.Keys();

        rig.Lane.Notify(Dir, "s-1");

        Assert.Equal([Rig.Key, GitLane.KeyOf(Dir, "t1", "m2")], rig.Emitted.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void MembersSharingOneWorkRootShareOneGitRunAndDifferentRootsDoNot()
    {
        var rig = new Rig();
        rig.State.SecondRoot = "/w/alpha";
        var second = GitLane.KeyOf(Dir, "t1", "m2");

        var a = rig.Lane.Reread(Rig.Key, 0);
        var b = rig.Lane.Reread(second, 0);

        Assert.Single(rig.Git.Requests);
        Assert.Equal(a.Changes.Single().Body, b.Changes.Single().Body);

        rig.State.SecondRoot = "/w/beta";
        rig.Now += GitLane.MinRefreshMs;
        _ = rig.Lane.Reread(second, 0);

        Assert.Equal(["/w/alpha", "/w/beta"], rig.Roots);
    }

    [Fact]
    public void AMemberThatReusedASharedRootOutcomeStillGetsItsTrailingRefreshAfterTheFloor()
    {
        var rig = new Rig();
        rig.State.SecondRoot = "/w/alpha";
        var second = GitLane.KeyOf(Dir, "t1", "m2");
        _ = rig.Lane.Reread(Rig.Key, 0);
        rig.Now += 1_000;
        rig.Output = "# branch.oid abc123\0# branch.head changed\0";

        var stale = Body(rig.Lane.Reread(second, 0));
        Assert.Equal("main", stale.Member("branch")!.Value.AsString());
        Assert.Single(rig.Git.Requests);
        Assert.Empty(rig.Lane.LapsedKeys());

        rig.Now += GitLane.MinRefreshMs;
        Assert.Contains(second, rig.Lane.LapsedKeys());
        var fresh = Body(rig.Lane.Reread(second, 0));

        Assert.Equal("changed", fresh.Member("branch")!.Value.AsString());
        Assert.Equal(2, rig.Git.Requests.Count);
    }

    [Fact]
    public void TheSharedRootCacheForgetsRootsOlderThanTheFloor()
    {
        var rig = new Rig();
        rig.State.SecondRoot = "/w/beta";
        _ = rig.Lane.Reread(Rig.Key, 0);
        rig.Now += GitLane.MinRefreshMs;
        _ = rig.Lane.Reread(GitLane.KeyOf(Dir, "t1", "m2"), 0);

        Assert.Equal(1, rig.Lane.CachedRoots);
    }

    [Fact]
    public void AnArgumentListWithSomeOtherConfigStillGetsTheFsmonitorHookTurnedOffOnce()
    {
        var other = GitRunner.Request("/t", ["git", "-c", "color.ui=false", "status"], GitRunner.DefaultTimeout);
        var already = GitRunner.Request("/t", GitRunner.StatusArgv, GitRunner.DefaultTimeout);

        Assert.Contains("core.fsmonitor=false", other.Argv);
        Assert.Contains("color.ui=false", other.Argv);
        Assert.Single(already.Argv, static a => a == "core.fsmonitor=false");
    }

    [Fact]
    public void EveryGitTheWatchSideStartsHasTheFsmonitorHookOffWhateverBuiltItsArguments()
    {
        var verify = GitRunner.Request("/t", GitRunner.VerifyArgv(new string('a', 40)), GitRunner.DefaultTimeout);
        var status = GitRunner.Request("/t", GitRunner.StatusArgv, GitRunner.DefaultTimeout);

        Assert.Equal(["git", "-c", "core.fsmonitor=false", "rev-parse"], verify.Argv.Take(4).ToArray());
        Assert.Equal(["git", "-c", "core.fsmonitor=false", "status"], status.Argv.Take(4).ToArray());
    }

    [Fact]
    public void WithNoClientADueKeyIsNotReEmittedByTheSweep()
    {
        var rig = new Rig();
        var connected = true;
        var lazy = new LazyLane(rig.Lane, () => connected);
        _ = lazy.Reread(Rig.Key, 0);
        _ = lazy.Reread(Rig.Key, 0);
        rig.Now += GitLane.MinRefreshMs;
        connected = false;

        Assert.Empty(lazy.LapsedKeys());

        connected = true;
        Assert.Equal([Rig.Key], lazy.LapsedKeys());
    }

    [Fact]
    public void ATokenOfAnotherLaneNamesNoKeyAndTheSliceTokenNamesTheWorktree()
    {
        var rig = new Rig();

        Assert.Null(rig.Lane.KeyOfToken("transcript:abc/s"));
        Assert.Null(rig.Lane.KeyOfToken("files:" + ActivityLane.DirId(Dir) + "/only-two"));
        Assert.Equal(Rig.Key, rig.Lane.KeyOfToken(GitLane.SliceIdOf(Dir, "t1", "m1")));
    }

    [Fact]
    public void AHeldBackRefreshComesDueThroughTheLazyWrapperToo()
    {
        var rig = new Rig();
        var lazy = new LazyLane(rig.Lane, () => true);
        _ = lazy.Reread(Rig.Key, 0);
        _ = lazy.Reread(Rig.Key, 0);
        Assert.Empty(lazy.LapsedKeys());

        rig.Now += GitLane.MinRefreshMs;

        Assert.Equal([Rig.Key], lazy.LapsedKeys());
        Assert.Null(lazy.KeyOfToken("transcript:x/y"));
    }
}
