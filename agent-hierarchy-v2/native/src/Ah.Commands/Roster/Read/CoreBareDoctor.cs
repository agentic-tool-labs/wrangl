namespace Ah.Commands;

/// <summary>
/// The <c>core-bare</c> doctor row (b490): the repository's git config says <c>core.bare=true</c> while it has linked work trees,
/// so the main checkout is no longer treated as a work tree. Report only: doctor never writes the config; the user runs the fix.
/// </summary>
internal static class CoreBareDoctor
{
    internal static (string Status, string Detail)? Row(RosterContext c)
    {
        if (c.Services.Launch is null
            || ReleaseGate.Git(c, c.Cwd, "config", "--bool", "core.bare") is not { Ok: true } bare
            || bare.Stdout.Trim() != "true")
        {
            return null;
        }

        // A main entry with no .git inside is a repository cloned bare on purpose: core.bare=true is right there.
        var trees = BranchHygiene.Worktrees(c, c.Cwd);
        if (trees.Count < 2 || !c.Services.Files.Exists(Ah.Core.Text.NodePath.Join(trees[0].Path, ".git")))
        {
            return null;
        }

        var repo = trees[0].Path;
        return ("warn", $"{repo} has core.bare=true with {trees.Count - 1} linked worktree(s): the main checkout is no longer treated as a work tree. Fix: git -C {repo} config core.bare false");
    }
}
