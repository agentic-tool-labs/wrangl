using Ah.Core.Text;

namespace Ah.Core.Tests;

public sealed class RemovedWorktreeDirTests
{
    private const string Leftover = "/r/.claude/worktrees/gone/.claude/ah2";

    [Fact]
    public void AWorktreeDirectoryWithNoGitIsRemoved() =>
        Assert.True(AhPaths.IsRemovedWorktreeDir(Leftover, static _ => false));

    [Fact]
    public void AWorktreeDirectoryWithItsGitIsNot() =>
        Assert.False(AhPaths.IsRemovedWorktreeDir(Leftover, static path => path == "/r/.claude/worktrees/gone/.git"));

    [Theory]
    [InlineData("/r/.claude/ah2")] // Not under .claude/worktrees: a plain directory with no .git, as a sandbox has.
    [InlineData("/r/worktrees/x/.claude/ah2")] // A folder called worktrees that is not Claude's.
    [InlineData("/r/.claude/worktrees/gone/other")] // Not a project directory.
    public void AnythingElseIsNot(string dir) =>
        Assert.False(AhPaths.IsRemovedWorktreeDir(dir, static _ => false));
}
