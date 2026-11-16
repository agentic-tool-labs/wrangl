using System.Diagnostics;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    internal static void GitIn(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static Rig PushRepoRig()
    {
        var rig = new Rig();
        var repo = rig.Sandbox.Root + "/proj";
        GitIn(repo, "init", "-q", "-b", "main");
        _ = Directory.CreateDirectory(repo + "/.claude/ah2");
        File.WriteAllText(repo + "/.claude/ah2/ah-conventions.json", """{"version":1}""");
        GitIn(repo, "add", ".claude/ah2/ah-conventions.json");
        GitIn(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "a");
        GitIn(repo, "update-ref", "refs/remotes/origin/main", "HEAD");
        GitIn(repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        GitIn(repo, "remote", "add", "origin", "https://example.invalid/x.git");
        GitIn(repo, "checkout", "-q", "-b", "feat");
        return rig;
    }

    private static string BashDone(Rig rig, string command, string? response = null, string eventName = "PostToolUse", string? agentId = null) =>
        "{\"hook_event_name\":\"" + eventName + "\",\"session_id\":\"sess-1\"," + (agentId is null ? string.Empty : "\"agent_id\":\"" + agentId + "\",") + "\"cwd\":\"" + rig.Sandbox.Root + "/proj\",\"tool_use_id\":\"toolu_9\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\""
        + command + "\"},\"tool_response\":" + (response ?? "{\"stdout\":\"\",\"stderr\":\"\",\"interrupted\":false}") + "}";

    private static List<JsonValue> RunBash(Rig rig, string command, string? response = null, string eventName = "PostToolUse", bool recorded = true)
    {
        rig.Run("posttooluse-merge-record", BashDone(rig, command, response, eventName), services: AsOrchestrator(rig, recorded));
        return LedgerRows(rig);
    }

    private static string RefSha(Rig rig, string refPath) => File.ReadAllText(rig.Sandbox.Root + "/proj/.git/" + refPath).Trim();

    [Fact]
    public void ASuccessfulTagPushIsAShipWithTheRefAndItsSha()
    {
        var rig = PushRepoRig();
        GitIn(rig.Sandbox.Root + "/proj", "tag", "v2.5.0");

        var row = Assert.Single(RunBash(rig, "git push origin v2.5.0"));

        Assert.Equal("ship", row.Member("kind")!.Value.AsString());
        Assert.Equal("v2.5.0", row.Member("ref")!.Value.AsString());
        Assert.Equal(RefSha(rig, "refs/tags/v2.5.0"), row.Member("sha")!.Value.AsString());
        Assert.Equal("git push", row.Member("source")!.Value.Member("verb")!.Value.AsString());
    }

    [Fact]
    public void AFailedOrInterruptedPushRecordsNothing()
    {
        var failed = PushRepoRig();
        GitIn(failed.Sandbox.Root + "/proj", "tag", "v2.5.0");
        var nonZero = PushRepoRig();
        GitIn(nonZero.Sandbox.Root + "/proj", "tag", "v2.5.0");
        var interrupted = PushRepoRig();
        GitIn(interrupted.Sandbox.Root + "/proj", "tag", "v2.5.0");

        Assert.Empty(RunBash(failed, "git push origin v2.5.0", eventName: "PostToolUseFailure"));
        Assert.Empty(RunBash(nonZero, "git push origin v2.5.0", response: "{\"exit_code\":1}"));
        Assert.Empty(RunBash(interrupted, "git push origin v2.5.0", response: "{\"interrupted\":true}"));
    }

    [Fact]
    public void APushFromANonOwnerSessionRecordsNothing()
    {
        var member = PushRepoRig();
        GitIn(member.Sandbox.Root + "/proj", "tag", "v2.5.0");

        Assert.Empty(RunBash(member, "git push origin v2.5.0", recorded: false));
        Assert.Empty(RunBash(member, "git push origin feat", recorded: false));
    }

    [Fact]
    public void AMergeOrPushRunByASubagentOfTheOwnerSessionRecordsNothingWhileTheSameCallWithoutAnAgentIdDoes()
    {
        var sub = PushRepoRig();
        GitIn(sub.Sandbox.Root + "/proj", "tag", "v2.5.0");
        var main = PushRepoRig();
        GitIn(main.Sandbox.Root + "/proj", "tag", "v2.5.0");

        sub.Run("posttooluse-merge-record", BashDone(sub, "git push origin v2.5.0", agentId: "agent-7"), services: AsOrchestrator(sub, true));
        sub.Run("posttooluse-merge-record", BashDone(sub, "git merge lane-ui", agentId: "agent-7"), services: AsOrchestrator(sub, true));

        Assert.Empty(LedgerRows(sub));
        Assert.Single(RunBash(main, "git push origin v2.5.0"));
    }

    [Fact]
    public void ASuccessfulMergeIsAMergeWithTheMergedRefAndHeadSha()
    {
        var rig = PushRepoRig();

        var row = Assert.Single(RunBash(rig, "git merge lane-ui"));

        Assert.Equal("merge", row.Member("kind")!.Value.AsString());
        Assert.Equal("lane-ui", row.Member("ref")!.Value.AsString());
        Assert.Equal(RefSha(rig, "refs/heads/feat"), row.Member("sha")!.Value.AsString());
        Assert.Equal("merged lane-ui into feat", row.Member("text")!.Value.AsString());
    }

    [Fact]
    public void AGhPrMergeIsAMergeAndAMergeWithNoBranchOrACommandThatOnlyMentionsMergeIsNone()
    {
        var pr = PushRepoRig();
        var abort = PushRepoRig();
        var other = PushRepoRig();

        Assert.Equal("12", Assert.Single(RunBash(pr, "gh pr merge 12 --squash")).Member("ref")!.Value.AsString());
        Assert.Empty(RunBash(abort, "git merge --abort"));
        Assert.Empty(RunBash(other, "git log --merges"));
        Assert.Empty(RunBash(other, "gh pr view 12"));
    }
}
