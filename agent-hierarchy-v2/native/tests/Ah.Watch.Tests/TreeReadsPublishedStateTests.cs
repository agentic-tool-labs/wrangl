#pragma warning disable xUnit1051 // every wait here carries its own hard deadline

using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>TUI Tree and Sky read the roster from the team file and each member's state from the summary the web reads, never a stale status row.</summary>
public sealed class TreeReadsPublishedStateTests
{
    private static ModelSource Source()
    {
        var src = new ModelSource { TeamName = "alpha" }.Member("impl", activity: "working", live: false).Member("rev", role: "reviewer", activity: "idle");

        // On the team file, with no row in the stale status document.
        src.TeamRows["late"] = new JsonObject { ["name"] = "late", ["role"] = "implementor" };
        return src;
    }

    private static JsonObject State(string state, string liveness) => new() { ["state"] = state, ["liveness"] = liveness };

    private static void Publish(ModelSource src, params (string Member, JsonObject State)[] states)
    {
        var map = new JsonObject();
        foreach (var (member, state) in states)
        {
            map[TreeIds.Member(ModelSource.Dir, "alpha", member)] = state;
        }

        src.Published = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["team-summary:" + ActivityLane.DirId(ModelSource.Dir) + "/alpha"] = new JsonObject { ["member_states"] = map },
        };
    }

    private static JsonObject RootSummary(ModelSource src) => src.Build("/").Obj("summary")!;

    [Fact]
    public void AMemberMissingFromTheStatusRowIsStillCounted() => Assert.Equal(3, RootSummary(Source()).Int("members"));

    [Fact]
    public void AStatusRowMemberNoLongerOnTheTeamFileIsNotCounted()
    {
        var src = Source();
        _ = src.TeamRows.Remove("rev");

        Assert.Equal(2, RootSummary(src).Int("members"));
    }

    [Theory]
    [InlineData("blocked-on-job", "blocked")]
    [InlineData("blocked", "blocked")]
    [InlineData("failed", "blocked")]
    [InlineData("working", "busy")]
    [InlineData("idle", "idle")]
    [InlineData("unknown", "live")]
    public void ATileShowsThePublishedState(string published, string tile)
    {
        var member = new JsonObject { ["name"] = "impl" };
        var states = new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State(published, "live") };

        ViewBuilder.ApplyPublishedState(member, states, ModelSource.Dir, "alpha");

        Assert.Equal(tile, ViewBuilder.MemberGraphState(member));
    }

    [Fact]
    public void APublishedUnknownLeavesAStatusRowsLiveReading()
    {
        var member = new JsonObject { ["name"] = "impl", ["live"] = true };
        var states = new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State("unknown", "unknown") };

        ViewBuilder.ApplyPublishedState(member, states, ModelSource.Dir, "alpha");

        Assert.Equal("live", ViewBuilder.MemberGraphState(member));
    }

    [Fact]
    public void NegativeAPublishedProvenDeadEndsAStatusRowsLiveReading()
    {
        var member = new JsonObject { ["name"] = "impl", ["live"] = true };
        var states = new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State("gone", "proven_dead") };

        ViewBuilder.ApplyPublishedState(member, states, ModelSource.Dir, "alpha");

        Assert.Equal("notlive", ViewBuilder.MemberGraphState(member));
    }

    [Fact]
    public void NegativeAPublishedUnknownDoesNotMakeANotLiveStatusRowLive()
    {
        var member = new JsonObject { ["name"] = "impl", ["live"] = false };
        var states = new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State("unknown", "unknown") };

        ViewBuilder.ApplyPublishedState(member, states, ModelSource.Dir, "alpha");

        Assert.Equal("notlive", ViewBuilder.MemberGraphState(member));
    }

    [Fact]
    public void APublishedLiveWinsOverANotLiveStatusRow()
    {
        var member = new JsonObject { ["name"] = "impl", ["live"] = false };
        var states = new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State("idle", "live") };

        ViewBuilder.ApplyPublishedState(member, states, ModelSource.Dir, "alpha");

        Assert.Equal("idle", ViewBuilder.MemberGraphState(member));
    }

    [Fact]
    public void NegativeANotLiveMemberIsNotBlockedOnItsTile()
    {
        var member = new JsonObject { ["name"] = "impl" };
        var states = new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State("blocked-on-job", "unknown") };

        ViewBuilder.ApplyPublishedState(member, states, ModelSource.Dir, "alpha");

        Assert.Equal("notlive", ViewBuilder.MemberGraphState(member));
    }

    /// <summary>ah2-397 A1: a status row blocked by a permission that the sidebar's rule calls working (a screen-clear mark) is neither counted nor drawn blocked.</summary>
    [Fact]
    public void AStatusRowBlockedByThatTheSidebarCallsWorkingIsNotBlocked()
    {
        var src = Source();
        src.Status["impl"]["activity"] = "blocked";
        src.Status["impl"]["blocked_by"] = "permission";
        Publish(src, ("impl", State("working", "live")));
        var member = new JsonObject { ["name"] = "impl", ["live"] = true, ["activity"] = "blocked", ["blocked_by"] = "permission" };

        ViewBuilder.ApplyPublishedState(member, new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State("working", "live") }, ModelSource.Dir, "alpha");

        Assert.Equal(0, RootSummary(src).Int("blocked"));
        Assert.Equal("busy", ViewBuilder.MemberGraphState(member));
    }

    [Fact]
    public void NegativeAStatusRowBlockedByThatTheSidebarCallsBlockedIsBlocked()
    {
        var src = Source();
        src.Status["impl"]["activity"] = "blocked";
        src.Status["impl"]["blocked_by"] = "permission";
        Publish(src, ("impl", State("blocked", "live")));
        var member = new JsonObject { ["name"] = "impl", ["live"] = true, ["activity"] = "blocked", ["blocked_by"] = "permission" };

        ViewBuilder.ApplyPublishedState(member, new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "impl")] = State("blocked", "live") }, ModelSource.Dir, "alpha");

        Assert.Equal(1, RootSummary(src).Int("blocked"));
        Assert.Equal("blocked", ViewBuilder.MemberGraphState(member));
    }

    [Fact]
    public void AnEndedTeamHasNoStuckStreams() => Assert.Equal(0, ViewBuilder.StreamsStuck(2, new JsonObject { ["state"] = "ended" }));

    [Fact]
    public void NegativeARunningTeamKeepsItsStuckStreams() => Assert.Equal(2, ViewBuilder.StreamsStuck(2, new JsonObject { ["state"] = "working" }));

    [Fact]
    public void NegativeATeamWithNoPublishedSummaryKeepsItsStuckStreams() => Assert.Equal(2, ViewBuilder.StreamsStuck(2, null));

    [Fact]
    public void AMemberPublishedAtTheWarningCountsAsHighContext()
    {
        var src = Source();
        var high = State("working", "live");
        high["context_pct"] = ContextAlerts.Warn;
        Publish(src, ("impl", high));

        Assert.Equal(1, RootSummary(src).Int("ctx_high"));
    }

    [Fact]
    public void NegativeAMemberPublishedBelowTheWarningIsNotHighContext()
    {
        var src = Source();
        var low = State("working", "live");
        low["context_pct"] = ContextAlerts.Warn - 1;
        Publish(src, ("impl", low));

        Assert.Equal(0, RootSummary(src).Int("ctx_high"));
    }

    [Fact]
    public void LiveAndWorkingAreThePublishedStates()
    {
        var src = Source();
        Publish(src, ("impl", State("idle", "live")), ("rev", State("working", "live")), ("late", State("working", "live")));

        var summary = RootSummary(src);

        Assert.Equal(3, summary.Int("live"));
        Assert.Equal(2, summary.Int("working"));
    }

    [Fact]
    public void AMemberThePublishedSummaryDoesNotHoldKeepsItsStatusRow()
    {
        var src = Source();
        Publish(src, ("rev", State("working", "live")));

        var summary = RootSummary(src);

        // impl is working and not live in its own row; late has neither.
        Assert.Equal(2, summary.Int("working"));
        Assert.Equal(1, summary.Int("live"));
    }

    [Fact]
    public void ASummaryOfAnotherTeamIsNotRead()
    {
        var src = Source();
        src.Published = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["team-summary:" + ActivityLane.DirId(ModelSource.Dir) + "/beta"] = new JsonObject
            {
                ["member_states"] = new JsonObject { [TreeIds.Member(ModelSource.Dir, "alpha", "rev")] = State("working", "live") },
            },
        };

        Assert.Equal(1, RootSummary(src).Int("working"));
    }

    [Fact]
    public void AWorkTreeThatListsTheTeamCostsNoStatusOfItsOwn()
    {
        var src = Source();
        src.WorkTreeDirs.AddRange(["/m/repo/.claude/worktrees/a/.claude/ah2", "/m/repo/.claude/worktrees/b/.claude/ah2"]);

        _ = RootSummary(src);

        Assert.Equal(ModelSource.Dir, Assert.Single(src.StatusReads));
    }

    [Fact]
    public async Task TwoEpochsNeverReadTheHierarchyAtOnce()
    {
        var running = 0;
        var most = 0;
        var src = Source();
        src.DuringStatus = () =>
        {
            var now = Interlocked.Increment(ref running);
            _ = Interlocked.Exchange(ref most, Math.Max(Volatile.Read(ref most), now));
            Thread.Sleep(200);
            _ = Interlocked.Decrement(ref running);
        };
        var first = WatchModule.CreateViewModel(src, ModelSource.Now);
        var second = WatchModule.CreateViewModel(src, ModelSource.Now + 3000);

        await Task.WhenAll(Task.Run(() => first.Build("/")), Task.Run(() => second.Build("/")));

        Assert.Equal(2, src.StatusReads.Count);
        Assert.Equal(1, Volatile.Read(ref most));
    }

    [Fact]
    public async Task APageOfAModelAlreadyReadDoesNotWaitForAnotherEpochsRead()
    {
        var src = Source();
        var first = WatchModule.CreateViewModel(src, ModelSource.Now);
        _ = first.Build("/");
        using var inRead = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        src.DuringStatus = () =>
        {
            inRead.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var second = Task.Run(() => WatchModule.CreateViewModel(src, ModelSource.Now + 3000).Build("/"));
        Assert.True(inRead.Wait(TimeSpan.FromSeconds(20)));

        var page = await Task.Run(() => first.Build("/")).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.Set();
        _ = await second;

        Assert.Equal(200, page.Status);
    }

    [Fact]
    public void TheDirTheTeamIsReadThroughStillHasItsStatusRead()
    {
        var src = Source();

        _ = RootSummary(src);

        Assert.Contains(ModelSource.Dir, src.StatusReads);
    }

    [Fact]
    public void AGoneMemberIsNeitherLiveNorWorking()
    {
        var src = Source();
        Publish(src, ("impl", State("gone", "proven_dead")));

        Assert.Equal(0, RootSummary(src).Int("working"));
    }
}
