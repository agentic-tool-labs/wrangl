using Ah.Abstractions;
using Ah.Platform;

namespace Ah.Cli.Tests;

public sealed partial class RosterDoctorVerbTests
{
    /// <summary>Makes <see cref="Project"/> a real git repository with one linked work tree, and doctor run real git.</summary>
    private void GitRepoWithLinkedWorktree()
    {
        _runner = PlatformModule.CreateProcessRunner();
        Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        Git("init", "-q");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", "x");
        Git("worktree", "add", "-q", Path.Combine(_sandbox.Root, "linked"));
    }

    private void Git(params string[] args)
    {
        var result = _runner!.Run(new ProcessRequest(["env", "-u", "GIT_DIR", "-u", "GIT_WORK_TREE", "-u", "GIT_INDEX_FILE", "git", "-C", Project, .. args], Timeout: TimeSpan.FromSeconds(30)));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} exited {result.ExitCode}");
    }

    private bool HasRow(string name) =>
        Parse(Roster(null, "doctor").Stdout)["rows"].AsArray().Any(r => r.Member("name")!.Value.AsString() == name);

    [Fact]
    public void CoreBareTrueWithALinkedWorktreeWarnsAndNamesTheFix()
    {
        GitRepoWithLinkedWorktree();
        Git("config", "core.bare", "true");

        var row = Row(Parse(Roster(null, "doctor").Stdout), "core-bare");

        Assert.Equal("warn", row["status"].AsString());
        var detail = row["detail"].AsString();
        Assert.Contains("no longer treated as a work tree", detail, StringComparison.Ordinal);
        Assert.Contains("1 linked worktree(s)", detail, StringComparison.Ordinal);
        Assert.Matches("Fix: git -C /\\S*/proj config core.bare false$", detail);
    }

    [Fact]
    public void CoreBareFalseWithALinkedWorktreeGivesNoRow()
    {
        GitRepoWithLinkedWorktree();
        Git("config", "core.bare", "false");

        Assert.False(HasRow("core-bare"));
    }

    [Fact]
    public void ABareCloneWithALinkedWorktreeGivesNoRow()
    {
        GitRepoWithLinkedWorktree();
        var bare = Path.Combine(_sandbox.Root, "bare.git");
        Git("clone", "-q", "--bare", Project, bare);
        Git("-C", bare, "worktree", "add", "-q", Path.Combine(_sandbox.Root, "bare-linked"));
        _cwdUnderTest = bare;

        Assert.False(HasRow("core-bare"));
    }
}
