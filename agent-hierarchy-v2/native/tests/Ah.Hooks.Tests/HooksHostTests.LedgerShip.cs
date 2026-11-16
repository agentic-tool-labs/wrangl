using Ah.Core.Json;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private static string Repo(Rig rig) => rig.Sandbox.Root + "/proj";

    private static void Commit(Rig rig) => GitIn(Repo(rig), "-c", "user.email=t@t", "-c", "user.name=t", "commit", "--allow-empty", "-q", "-m", "more");

    private static string Rev(Rig rig, string rev) => RunGit(Repo(rig), "rev-parse", rev);

    private static string RunGit(string dir, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, WorkingDirectory = dir };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    private static List<string?> LedgerOperationIds(Rig rig) =>
        [.. rig.Writer.Calls.Zip(rig.Writer.OperationIds).Where(p => p.First == "progress.ledger-append").Select(p => p.Second)];

    [Fact]
    public void ALedgerLineFromAToolCallIsWrittenUnderAnOperationIdThatNamesTheCallAndTheEventSoARepeatedHookWritesItOnce()
    {
        var rig = PushRepoRig();
        GitIn(Repo(rig), "branch", "lane-ui", "main");
        Commit(rig);
        GitIn(Repo(rig), "tag", "v2.5.0");

        RunBash(rig, "git merge lane-ui && git push origin v2.5.0");

        var ids = LedgerOperationIds(rig);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.All(ids, id => Assert.StartsWith("toolu_9:", id, StringComparison.Ordinal));
        RunBash(rig, "git merge lane-ui && git push origin v2.5.0");
        Assert.Equal([.. ids, .. ids], LedgerOperationIds(rig));
    }

    private static bool? Confirmed(JsonValue row) => row.Member("confirmed") is { IsBool: true } flag ? flag.IsTruthy : null;

    [Fact]
    public void ATagPushOnALineWithASemicolonIsRecordedUnconfirmedOnExitZeroAndNotOnANonZeroExit()
    {
        var ok = PushRepoRig();
        GitIn(Repo(ok), "tag", "v2.5.0");
        var failed = PushRepoRig();
        GitIn(Repo(failed), "tag", "v2.5.0");

        var row = Assert.Single(RunBash(ok, "git push origin v2.5.0 > /tmp/p.log 2>&1; echo exit=$?"));

        Assert.Equal("ship", row.Member("kind")!.Value.AsString());
        Assert.False(Confirmed(row));
        Assert.True(row.Member("tag")!.Value.IsTruthy);
        Assert.Empty(RunBash(failed, "git push origin v2.5.0; echo exit=$?", response: "{\"exit_code\":1}"));
    }

    [Fact]
    public void ABranchPushOnALineWithASemicolonOrAPipeIsRecordedOnlyWhenTheRemoteTrackingRefNamesTheSentCommit()
    {
        foreach (var command in new[] { "git push origin feat; echo exit=$?", "git push origin feat 2>&1 | tail -3" })
        {
            var landed = PushRepoRig();
            GitIn(Repo(landed), "update-ref", "refs/remotes/origin/feat", "HEAD");
            var missing = PushRepoRig();
            var behind = PushRepoRig();
            GitIn(behind.Sandbox.Root + "/proj", "update-ref", "refs/remotes/origin/feat", "HEAD");
            Commit(behind);

            var row = Assert.Single(RunBash(landed, command));

            Assert.Equal("feat", row.Member("ref")!.Value.AsString());
            Assert.Equal(Rev(landed, "feat"), row.Member("sha")!.Value.AsString());
            Assert.True(Confirmed(row));
            Assert.False(row.Member("tag")!.Value.IsTruthy);
            Assert.Empty(RunBash(missing, command));
            Assert.Empty(RunBash(behind, command));
        }
    }

    [Fact]
    public void AMergeFollowedByAnotherCommandIsRecordedOnlyWhenNoMergeIsInProgressAndTheMergedRefIsInHead()
    {
        var landed = PushRepoRig();
        GitIn(Repo(landed), "branch", "lane-ui", "main");
        var conflicted = PushRepoRig();
        GitIn(Repo(conflicted), "branch", "lane-ui", "main");
        File.WriteAllText(Repo(conflicted) + "/.git/MERGE_HEAD", Rev(conflicted, "main") + "\n");
        var unknown = PushRepoRig();

        var row = Assert.Single(RunBash(landed, "git merge lane-ui; git status"));

        Assert.Equal("merge", row.Member("kind")!.Value.AsString());
        Assert.True(Confirmed(row));
        Assert.Equal("feat", row.Member("into")!.Value.AsString());
        Assert.Empty(RunBash(conflicted, "git merge lane-ui; git status"));
        Assert.Empty(RunBash(unknown, "git merge no-such-branch; git status"));
    }

    [Fact]
    public void ACommandsJoinedOnlyByAndAreTrustedOnTheExitStatusWithNoReadOfGit()
    {
        var rig = PushRepoRig();
        GitIn(Repo(rig), "branch", "lane-ui", "main");
        Commit(rig);
        GitIn(Repo(rig), "update-ref", "refs/remotes/origin/main", "HEAD~1");
        GitIn(Repo(rig), "update-ref", "refs/remotes/origin/feat", "HEAD~1");

        var rows = RunBash(rig, "git merge lane-ui && git push origin feat");

        Assert.Equal(["merge", "ship"], rows.Select(r => r.Member("kind")!.Value.AsString()));
        Assert.All(rows, r => Assert.True(Confirmed(r)));
        Assert.Empty(RunBash(PushRepoRig(), "git merge lane-ui && git push origin feat", response: "{\"exit_code\":1}"));
    }

    [Fact]
    public void AGhPrMergeOnALineWithASemicolonIsRecordedUnconfirmedOnExitZeroAndAloneIsConfirmed()
    {
        var piped = PushRepoRig();
        var alone = PushRepoRig();
        var failed = PushRepoRig();

        var unconfirmed = Assert.Single(RunBash(piped, "gh pr merge 12; echo exit=$?"));
        var confirmed = Assert.Single(RunBash(alone, "gh pr merge 12"));

        Assert.False(Confirmed(unconfirmed));
        Assert.True(Confirmed(confirmed));
        Assert.Empty(RunBash(failed, "gh pr merge 12; echo exit=$?", response: "{\"exit_code\":2}"));
    }

    private static List<JsonValue> Pushed(string command, Action<Rig>? setup = null) => PushedFrom(command, setup).Rows;

    /// <summary>Runs <paramref name="command"/> in a repo where <c>feat</c> is one commit ahead of <c>main</c>; also returns both commits.</summary>
    private static (List<JsonValue> Rows, string Main, string Head) PushedFrom(string command, Action<Rig>? setup = null)
    {
        var rig = PushRepoRig();
        GitIn(Repo(rig), "update-ref", "refs/remotes/origin/up", "HEAD");
        Commit(rig);
        setup?.Invoke(rig);
        return (RunBash(rig, command), Rev(rig, "main"), Rev(rig, "feat"));
    }

    private static void AssertSent(string command, string expectedRef, bool fromMain, Action<Rig>? setup = null)
    {
        var (rows, main, head) = PushedFrom(command, setup);

        var row = Assert.Single(rows);
        Assert.Equal(expectedRef, row.Member("ref")!.Value.AsString());
        Assert.Equal(fromMain ? main : head, row.Member("sha")!.Value.AsString());
        Assert.True(Confirmed(row));
    }

    [Fact]
    public void EachPushFormNamesTheRemoteSideRefAndTakesTheShaFromTheSource()
    {
        var upstream = (Rig r) =>
        {
            GitIn(Repo(r), "config", "branch.feat.remote", "origin");
            GitIn(Repo(r), "config", "branch.feat.merge", "refs/heads/up");
        };

        AssertSent("git push", "up", false, upstream);
        AssertSent("git push origin", "up", false, upstream);
        AssertSent("git push origin", "feat", false);
        AssertSent("git push -u origin HEAD", "feat", false);
        AssertSent("git push origin HEAD:rel", "rel", false);
        AssertSent("git push origin main", "main", true);
        AssertSent("git push origin main:rel", "rel", true);
        AssertSent("git push origin +main:rel", "rel", true);
    }

    [Theory]
    [InlineData("git push")]
    [InlineData("git push origin :rel")]
    [InlineData("git push --delete origin feat")]
    [InlineData("git push --all origin")]
    [InlineData("git push --mirror origin")]
    [InlineData("git push --tags origin")]
    public void APushWithNoSingleRefRecordsNothing(string command)
    {
        Assert.Empty(Pushed(command));
    }

    [Fact]
    public void AValueTakingOptionsValueIsNotReadAsTheMergedOrPushedRef()
    {
        var rig = PushRepoRig();
        GitIn(Repo(rig), "branch", "lane-ui", "main");

        var merged = Assert.Single(RunBash(rig, "git merge --no-ff -m 'ship the lane ui' -s ort lane-ui"));

        Assert.Equal("merge", merged.Member("kind")!.Value.AsString());
        Assert.Equal("lane-ui", merged.Member("ref")!.Value.AsString());
        Assert.Empty(RunBash(PushRepoRig(), "git merge --no-ff -m 'ship the lane ui'"));
        AssertSent("git push -o ci.skip origin main:rel", "rel", true);
        AssertSent("git push --push-option ci.skip origin HEAD", "feat", false);
    }

    [Fact]
    public void APushOfADetachedHeadRecordsNothingButTheSamePushOnABranchDoes()
    {
        Assert.Empty(Pushed("git push origin HEAD", r => GitIn(Repo(r), "checkout", "-q", "--detach")));
        Assert.Single(Pushed("git push origin HEAD"));
    }

    /// <summary>
    /// A repo where ah/x has its own commit, merged into feat, and ah/y has one that is not; item a is linked to a request of slug x and item b to
    /// one of slug y, both in_progress. Returns the rig, its pool and a reader of the two items' states.
    /// </summary>
    private static (Rig Rig, Func<(string A, string B)> States) ShipRig()
    {
        var rig = PushRepoRig();
        rig.Writer.Executing = [.. rig.Services.Hierarchy.StateDomains.Where(static d => d.Name == "progress")];
        var pool = Ah.Hooks.Lifecycle.ProgressPool.Of(rig.Services, Repo(rig));
        var msgs = Path.Combine(pool, "msgs");
        _ = Directory.CreateDirectory(msgs);
        File.WriteAllText(Path.Combine(msgs, "20261101-100000-aaaa--implementor--x--request.md"), "---\nid: 20261101-100000-aaaa\nslug: x\n---\nbuild x\n");
        File.WriteAllText(Path.Combine(msgs, "20261101-100000-bbbb--implementor--y--request.md"), "---\nid: 20261101-100000-bbbb\nslug: y\n---\nbuild y\n");
        GitIn(Repo(rig), "checkout", "-q", "-b", "ah/x", "main");
        Commit(rig);
        GitIn(Repo(rig), "checkout", "-q", "-b", "ah/y", "main");
        GitIn(Repo(rig), "-c", "user.email=t@t", "-c", "user.name=t", "commit", "--allow-empty", "-q", "-m", "y work"); // its own message, else the same sha as ah/x's
        GitIn(Repo(rig), "checkout", "-q", "feat");
        GitIn(Repo(rig), "update-ref", "-m", "released", "refs/remotes/origin/feat", "main"); // the release tip before any ship
        GitIn(Repo(rig), "merge", "-q", "--ff-only", "ah/x");
        const string at = "2026-01-01T00:00:00Z";
        var p = rig.Services.Hierarchy.Progress;
        Assert.Null(p.RecordChecklist(pool, "tm", "_run", "orchestrator@tm",
        [
            new Ah.Hierarchy.PlanInput("a", "add", "Build x", At: at), new Ah.Hierarchy.PlanInput("a", "start", At: at), new Ah.Hierarchy.PlanInput("a", "ref", Ref: "20261101-100000-aaaa", At: at),
            new Ah.Hierarchy.PlanInput("b", "add", "Build y", At: at), new Ah.Hierarchy.PlanInput("b", "start", At: at), new Ah.Hierarchy.PlanInput("b", "ref", Ref: "20261101-100000-bbbb", At: at),
        ]).Refused);
        return (rig, () =>
        {
            var items = p.ReadPlan(pool, "tm", "_run")!.Items;
            return (items.Single(static i => i.Item == "a").State, items.Single(static i => i.Item == "b").State);
        });
    }

    /// <summary>A push of <paramref name="branch"/> as git leaves it: the remote-tracking ref moves to the branch (its old value stays in the reflog), then the hook sees the command.</summary>
    private static List<JsonValue> Push(Rig rig, string branch)
    {
        GitIn(Repo(rig), "update-ref", "-m", "push", "refs/remotes/origin/" + branch, branch);
        return RunBash(rig, "git push origin " + branch);
    }

    [Theory]
    [InlineData("item=_run/a", "waiting_user")]
    [InlineData("ah:x", "in_progress")]
    public void ALiveQuestionTaggedWithAnItemPutsOnlyThatItemOnTheUsersList(string source, string expected)
    {
        var (rig, states) = ShipRig();
        var pre = "{\"hook_event_name\":\"PreToolUse\",\"session_id\":\"sess-1\",\"tool_use_id\":\"t1\",\"cwd\":\"" + Repo(rig) + "\",\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"metadata\":{\"source\":\"" + source + "\"},\"questions\":[{\"question\":\"Which?\"}]}}";

        _ = rig.Run("activity", pre, services: AsOrchestrator(rig, true));

        Assert.Equal((expected, "in_progress"), states());
    }

    [Theory]
    [InlineData("PostToolUseFailure")]
    [InlineData("PermissionDenied")]
    public void ADeniedOrFailedTaggedQuestionLeavesItsItemInItsPriorState(string hookEvent)
    {
        var (rig, states) = ShipRig();
        var services = AsOrchestrator(rig, true);
        string Event(string name) => "{\"hook_event_name\":\"" + name + "\",\"session_id\":\"sess-1\",\"tool_use_id\":\"t1\",\"cwd\":\"" + Repo(rig) + "\",\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"metadata\":{\"source\":\"item=_run/a\"},\"questions\":[{\"question\":\"Which?\"}]}}";
        _ = rig.Run("activity", Event("PreToolUse"), services: services);
        Assert.Equal(("waiting_user", "in_progress"), states());

        _ = rig.Run("posttooluse-ask-record", Event(hookEvent), services: services);

        Assert.Equal(("in_progress", "in_progress"), states());
    }

    [Fact]
    public void NegativeAPushContainingAnItemsBranchWritesARowAndClosesNothing()
    {
        var (rig, states) = ShipRig();

        var row = Assert.Single(Push(rig, "feat"));

        Assert.Equal("ship", row.Member("kind")!.Value.AsString());
        Assert.Equal(("in_progress", "in_progress"), states());
    }

    [Fact]
    public void NegativeMergingAnItemsBranchIntoAnotherWritesARowAndClosesNothing()
    {
        var (rig, states) = ShipRig();

        var row = Assert.Single(RunBash(rig, "git merge ah/x"));

        Assert.Equal("merge", row.Member("kind")!.Value.AsString());
        Assert.Equal(("in_progress", "in_progress"), states());
    }

    [Fact]
    public void NegativeAMergeOnADetachedHeadWritesARowAndClosesNothing()
    {
        var (rig, states) = ShipRig();
        GitIn(Repo(rig), "checkout", "-q", "--detach");

        _ = Assert.Single(RunBash(rig, "git merge ah/x"));

        Assert.Equal(("in_progress", "in_progress"), states());
    }

    [Fact]
    public void NegativePushingTheItemsOwnBranchClosesNothing()
    {
        var (rig, states) = ShipRig();

        var row = Assert.Single(RunBash(rig, "git push origin ah/x"));

        Assert.Equal("ah/x", row.Member("ref")!.Value.AsString());
        Assert.Equal(("in_progress", "in_progress"), states());
    }

    [Fact]
    public void NegativeMergingMainIntoTheItemsBranchClosesNothing()
    {
        var (rig, states) = ShipRig();
        GitIn(Repo(rig), "checkout", "-q", "main");
        Commit(rig);
        GitIn(Repo(rig), "checkout", "-q", "ah/x");
        GitIn(Repo(rig), "-c", "user.email=t@t", "-c", "user.name=t", "merge", "-q", "--no-edit", "main");

        var row = Assert.Single(RunBash(rig, "git merge main"));

        Assert.Equal("ah/x", row.Member("into")!.Value.AsString());
        Assert.Equal(("in_progress", "in_progress"), states());
    }
}
