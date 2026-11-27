namespace Ah.Core.Text;

/// <summary>
/// Where the plugin keeps what it owns, in one place: the state home under <c>~/.claude</c>, a repository's project
/// directory under its <c>.claude</c>, and the fixed-name files beside them. Nothing the plugin owns sits directly under
/// <c>~/.claude</c> or <c>&lt;repo&gt;/.claude</c>.
/// </summary>
public static class AhPaths
{
    private const string Claude = ".claude";

    /// <summary>The plugin's directory under <c>.claude</c>, at user and project level alike.</summary>
    public const string Dir = "ah2";

    /// <summary>The default state home below the user's home, as a relative path with forward slashes.</summary>
    public const string HomeRelative = ".claude/" + Dir;

    /// <summary>The directory a project directory is replaced by when the repository is the user's home.</summary>
    public const string ProjectUnderHome = "project";

    /// <summary>The user and repository config file name.</summary>
    public const string ConfigFile = "agent-hierarchy.json";

    /// <summary>The repository's standing-instructions file name (its conventions).</summary>
    public const string ConventionsFile = "ah-conventions.json";

    public const string UltraGateStateDir = "agent-hierarchy.ultra-gate.state";

    public const string SessionRolesStateDir = "agent-hierarchy.state";

    public const string PeerPendingFile = "agent-hierarchy.peer-pending.jsonl";

    public const string PeerRecordsStateDir = "agent-hierarchy.peer-records.state";

    public const string QaStateDir = "agent-hierarchy.qa.state";

    public const string ResourcesStateDir = "agent-hierarchy.resources.state";

    public const string SpendStateDir = "agent-hierarchy.spend.state";

    public const string UsageCacheFile = "agent-hierarchy.usage-cache.json";

    public const string UsageStateDir = "agent-hierarchy.usage.state";

    /// <summary>
    /// The meta key prefix of the user's index of hierarchy directories that hold an ah2-enabled session, for a daemon that learns of a
    /// directory from no team file (b397): one row per dir, the rest of the key its real path, the value its last-seen ISO time.
    /// </summary>
    public const string KnownDirKeyPrefix = "known-dir:";

    public const string KnownDirsStateDir = "agent-hierarchy.known-dirs.state";

    public const string HookErrorsFile = "hook-errors.jsonl";

    /// <summary>The user-level audit log of the worktree grants: every change and every refusal of an owner verb.</summary>
    public const string WorktreeAuditFile = "agent-hierarchy.worktree-audit.jsonl";

    private const string UserHierarchy = "hierarchy";

    /// <summary>The state home under <paramref name="userHome"/>: <c>~/.claude/ah2</c>.</summary>
    public static string HomeUnder(string userHome) => NodePath.Join(userHome, Claude, Dir);

    /// <summary>A fixed-name file or directory of the user level: <c>~/.claude/ah2/&lt;name&gt;</c>.</summary>
    public static string UserEntry(string userHome, string name) => NodePath.Join(userHome, Claude, Dir, name);

    /// <summary>
    /// A fixed-name file or directory of the state home <paramref name="ahHome"/>, which is already the <c>ah2</c> directory
    /// itself (the environment's <c>AhHome</c>): <c>&lt;ahHome&gt;/&lt;name&gt;</c>. It is <see cref="UserEntry"/> for a caller that
    /// holds the state home rather than the user's home.
    /// </summary>
    public static string HomeEntry(string ahHome, string name) => NodePath.Join(ahHome, name);

    /// <summary>The user-global hook error log: <c>~/.claude/ah2/hierarchy/hook-errors.jsonl</c>.</summary>
    /// <summary>The daemon's record of rejected requests, under the state home's <c>logs</c> directory.</summary>
    public static string RejectedLog(string ahHome) => NodePath.Join(ahHome, "logs", "rejected.jsonl");

    /// <summary>The trace of turn ends that ended on an API error, under the state home's <c>logs</c> directory.</summary>
    public static string TurnEndLog(string ahHome) => NodePath.Join(ahHome, "logs", "turn-ends.jsonl");

    public static string HookErrorLog(string userHome) => NodePath.Join(userHome, Claude, Dir, UserHierarchy, HookErrorsFile);

    /// <summary>The project directory of a working directory outside any repository: <c>~/.claude/ah2/hierarchy/&lt;basename&gt;</c>.</summary>
    public static string UserProjectDir(string userHome, string basename) => NodePath.Join(userHome, Claude, Dir, UserHierarchy, basename);

    /// <summary>
    /// A repository's project directory: <c>&lt;repo&gt;/.claude/ah2</c>. When the repository is the user's home itself that
    /// would be the state home, so it is <c>~/.claude/ah2/project</c> instead.
    /// </summary>
    public static string ProjectDir(string repoRoot, bool repoIsHome = false) =>
        repoIsHome ? NodePath.Join(repoRoot, Claude, Dir, ProjectUnderHome) : NodePath.Join(repoRoot, Claude, Dir);

    /// <summary>A fixed-name entry of a repository's project directory.</summary>
    public static string ProjectEntry(string repoRoot, string name, bool repoIsHome = false) =>
        NodePath.Join(ProjectDir(repoRoot, repoIsHome), name);

    /// <summary>The root of the repository a project directory belongs to, or null when <paramref name="dir"/> is not one.</summary>
    public static string? RepoRootOf(string dir)
    {
        if (NodePath.Basename(dir) == Dir && NodePath.Basename(NodePath.Dirname(dir)) == Claude)
        {
            return NodePath.Dirname(NodePath.Dirname(dir));
        }

        var parent = NodePath.Dirname(dir);
        if (NodePath.Basename(dir) == ProjectUnderHome && NodePath.Basename(parent) == Dir && NodePath.Basename(NodePath.Dirname(parent)) == Claude)
        {
            return NodePath.Dirname(NodePath.Dirname(parent));
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="dir"/> is the project directory left in <c>.claude/worktrees/&lt;name&gt;</c> after its worktree was removed: no <c>.git</c>
    /// there, so a cwd in it resolves to the enclosing checkout and a request naming the directory is refused.
    /// </summary>
    public static bool IsRemovedWorktreeDir(string dir, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (RepoRootOf(dir) is not { } root)
        {
            return false;
        }

        var parent = NodePath.Dirname(root);
        return NodePath.Basename(parent) == "worktrees" && NodePath.Basename(NodePath.Dirname(parent)) == Claude && !exists(NodePath.Join(root, ".git"));
    }

    /// <summary>
    /// Whether <paramref name="dir"/> is a user-global project directory, <c>&lt;home&gt;/.claude/ah2/hierarchy/&lt;basename&gt;</c>. Any
    /// other child of an <c>ah2</c> directory (registrations, teams) is not.
    /// </summary>
    public static bool IsUserProjectDir(string dir)
    {
        var hierarchy = NodePath.Dirname(dir);
        var ah2 = NodePath.Dirname(hierarchy);
        return NodePath.Basename(dir).Length > 0
            && NodePath.Basename(hierarchy) == UserHierarchy
            && NodePath.Basename(ah2) == Dir
            && NodePath.Basename(NodePath.Dirname(ah2)) == Claude;
    }

    /// <summary>The user-level repo-user layer of the config: <c>~/.claude/ah2/projects/&lt;slug&gt;/agent-hierarchy.json</c>.</summary>
    public static string RepoUserConfig(string userHome, string slug) =>
        NodePath.Join(userHome, Claude, Dir, "projects", slug, ConfigFile);
}
