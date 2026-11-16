using System.Runtime.CompilerServices;
using System.Text;
using Ah.Core.Json;
using Ah.Hooks.Activity;
using Ah.Hooks.Plumbing;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private const string Multiline = "git commit -m \"$(cat <<'EOF'\nthe message\n\nthe body\nEOF\n)\"";

    private static string Proj(Rig rig) => rig.Sandbox.Root + "/proj";

    private static string Commit(Rig rig, string subject, params string[] more)
    {
        GitIn(Proj(rig), ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "--allow-empty-message", "-m", subject, .. more]);
        return RefSha(rig, "refs/heads/feat");
    }

    private static List<(string Cwd, string? Session, string Subject, string Sha)> LogRun(Rig rig, string command, JsonObject? response = null, string eventName = "PostToolUse", string tool = "Bash", TimeSpan later = default)
    {
        var logged = new List<(string, string?, string, string)>();
        var evt = new JsonObject
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = "sess-1",
            ["cwd"] = Proj(rig),
            ["tool_name"] = tool,
            ["tool_input"] = new JsonObject { ["command"] = command },
            ["tool_response"] = response ?? new JsonObject { ["stdout"] = string.Empty, ["interrupted"] = false },
        };
        var services = rig.Services with { Clock = new FakeClock(DateTimeOffset.UtcNow + later), WorkCommit = (cwd, session, subject, sha) => logged.Add((cwd, session, subject, sha)) };
        var outcome = PostToolUseCommitLog.Run(new GateContext(services, JsonText.Parse(Encoding.UTF8.GetBytes(JsonText.Stringify(evt, 0))), "posttooluse-commit-log"));
        Assert.Equal(HookEmit.Silent, outcome);
        return logged;
    }

    [Theory]
    [InlineData("git commit -m the-subject")]
    [InlineData("git add . && git commit -m the-subject")]
    [InlineData("git add . && git commit -m the-subject && git log -1")]
    [InlineData("git commit -m the-subject > /dev/null")]
    [InlineData(Multiline)]
    [InlineData("git commit -F - <<'EOF'\nthe message\nEOF")]
    public void ASuccessfulCommitLogsItsSubjectAndShaForThePayloadsSession(string command)
    {
        var rig = PushRepoRig();
        var sha = Commit(rig, "the subject");

        var logged = Assert.Single(LogRun(rig, command));

        Assert.Equal((Proj(rig), "sess-1", "the subject", sha), logged);
    }

    [Fact]
    public void ACommitElsewhereReadsThatRepository()
    {
        var rig = PushRepoRig();
        var other = Proj(rig) + "/other";
        _ = Directory.CreateDirectory(other);
        GitIn(other, "init", "-q");
        GitIn(other, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "theirs");

        foreach (var command in new[] { "git -C other commit -m theirs", "cd other && git commit -m theirs" })
        {
            var logged = Assert.Single(LogRun(rig, command));
            Assert.Equal("theirs", logged.Subject);
            Assert.Equal(other, logged.Cwd);
        }
    }

    [Theory]
    [InlineData("git --git-dir=other/.git --work-tree=other commit -m theirs")]
    [InlineData("git --git-dir=other/.git commit -m theirs")]
    [InlineData("git --work-tree=other commit -m theirs")]
    [InlineData("GIT_DIR=other/.git git commit -m theirs")]
    public void ACommitNamingItsGitDirOrWorkTreeLogsNothing(string command)
    {
        var rig = PushRepoRig();
        var other = Proj(rig) + "/other";
        _ = Directory.CreateDirectory(other);
        GitIn(other, "init", "-q");
        GitIn(other, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "theirs");

        Assert.Empty(LogRun(rig, command));
    }

    [Fact]
    public void AnAmendLogsOnceMoreWithTheNewSha()
    {
        var rig = PushRepoRig();
        var first = Commit(rig, "one");
        Assert.Equal(first, Assert.Single(LogRun(rig, "git commit -m one")).Sha);
        var amended = Commit(rig, "one again", "--amend");

        var logged = Assert.Single(LogRun(rig, "git commit --amend -m 'one again'"));

        Assert.NotEqual(first, amended);
        Assert.Equal((amended, "one again"), (logged.Sha, logged.Subject));
    }

    [Fact]
    public void TwoCommitsOfOneLineLogHeadOnce()
    {
        var rig = PushRepoRig();
        var sha = Commit(rig, "two");

        Assert.Equal(sha, Assert.Single(LogRun(rig, "git commit -m one && git commit -m two")).Sha);
    }

    [Fact]
    public void ACommitWithNoSubjectIsTitledByItsSha()
    {
        var rig = PushRepoRig();
        var sha = Commit(rig, string.Empty);

        Assert.Equal(sha, Assert.Single(LogRun(rig, "git commit --allow-empty-message -m ''")).Subject);
    }

    [Theory]
    [InlineData("git commit -m x; echo done")]
    [InlineData("git commit -m x | tail -1")]
    [InlineData("git commit -m x 2>&1 | tail -3")]
    [InlineData("git commit -m x || echo failed")]
    public void WhereTheStatusDoesNotSpeakOnlyAFreshHeadLogs(string command)
    {
        var rig = PushRepoRig();
        var sha = Commit(rig, "s");

        Assert.Equal(sha, Assert.Single(LogRun(rig, command)).Sha);
        Assert.Empty(LogRun(rig, command, later: TimeSpan.FromHours(1)));
    }

    [Fact]
    public void WhereTheStatusSpeaksAnOldHeadStillLogs()
    {
        var rig = PushRepoRig();
        _ = Commit(rig, "s");

        Assert.Single(LogRun(rig, "git commit -m s", later: TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData("(git commit -m x)")]
    [InlineData("echo $(git commit -m x)")]
    [InlineData("git commit --dry-run -m x")]
    [InlineData("git commit --help")]
    [InlineData("git log --grep commit")]
    [InlineData("echo git commit")]
    [InlineData("git commit-tree HEAD^{tree} -m x")]
    public void WhatIsNotATopLevelCommitLogsNothingEvenWithAFreshHead(string command)
    {
        var rig = PushRepoRig();
        _ = Commit(rig, "older");

        Assert.Empty(LogRun(rig, command));
    }

    [Fact]
    public void AFailedOrInterruptedCommitLogsNothing()
    {
        var rig = PushRepoRig();
        _ = Commit(rig, "older");

        Assert.Empty(LogRun(rig, "git commit -m x", new JsonObject { ["exit_code"] = 1 }));
        Assert.Empty(LogRun(rig, "git commit -m x", new JsonObject { ["interrupted"] = true }));
        Assert.Empty(LogRun(rig, "git commit -m x", eventName: "PostToolUseFailure"));
    }

    [Fact]
    public void AnotherToolLogsNothing()
    {
        var rig = PushRepoRig();
        _ = Commit(rig, "older");

        Assert.Empty(LogRun(rig, "git commit -m x", tool: "Monitor"));
    }

    [Fact]
    public void ALoggerThatThrowsLeavesARowAndDoesNotFailTheTool()
    {
        var rig = PushRepoRig();
        _ = Commit(rig, "s");
        var evt = "{\"hook_event_name\":\"PostToolUse\",\"session_id\":\"sess-1\",\"cwd\":\"" + Proj(rig) + "\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git commit -m s\"},\"tool_response\":{}}";
        var services = rig.Services with { WorkCommit = static (_, _, _, _) => throw new InvalidOperationException("the write failed") };

        var outcome = PostToolUseCommitLog.Run(new GateContext(services, JsonText.Parse(Encoding.UTF8.GetBytes(evt)), "posttooluse-commit-log"));

        Assert.Equal(HookEmit.Silent, outcome);
        Assert.Contains(rig.Errors.Records, static e => e.Message.Contains("the write failed", StringComparison.Ordinal));
    }

    private static string HooksJsonPath([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "hooks", "registry.json"));

    [Fact]
    public void TheUnitIsRegisteredOnBashPostToolUseAndKnownToTheHost()
    {
        var entries = JsonText.Parse(File.ReadAllText(HooksJsonPath())).Member("hooks")!.Value.Member("PostToolUse")!.Value.AsArray();

        Assert.Contains(
            entries.Where(static e => e.Member("matcher")!.Value.AsString() == "Bash").SelectMany(static e => e.Member("hooks")!.Value.AsArray()),
            static h => h.Member("command")!.Value.AsString().EndsWith(" posttooluse-commit-log", StringComparison.Ordinal));
        Assert.Contains(AdvisoryUnits.All, static u => u.Name == "posttooluse-commit-log");
    }
}
