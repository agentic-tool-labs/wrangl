using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>The Node <c>lib-config</c> core: paths, hook-input rules, role lookup, and (in the other parts) config resolution.</summary>
internal sealed partial class ConfigCore(IFileReader files, IAhEnvironment environment, Func<ITeamFiles> teamFiles, ProcessFacts process, ITrustStore? trust = null, IHomeDocs? docs = null) : IConfigCore
{
    private string Home => environment.HomeDirectory;

    private ITeamFiles Teams => teamFiles();

    private string Resolve(params string[] parts) => NodePath.Resolve(process.CurrentDirectory, parts);

    private string CwdOr(string? cwd) => string.IsNullOrEmpty(cwd) ? process.CurrentDirectory : cwd;

    public bool IsSubagent(JsonValue input) => input.Member("agent_id")?.NonEmptyText is not null;

    public bool IsTeamAliasShape(string? name) => ConfigRegistry.IsTeamAliasShape(name);

    public JsonObject ValidateTeamAlias(JsonValue teamAlias, JsonObject? resolved = null) => ConfigRegistry.ValidateTeamAlias(teamAlias, resolved);

    public string? RoleFromName(string? name, JsonObject? resolved = null) => ConfigRegistry.RoleFromName(name, resolved);

    public string? AliasRoleFromName(string name, JsonObject? resolved = null) => ConfigRegistry.AliasRoleFromName(name, resolved);

    public JsonValue ParseHookInput(ReadOnlySpan<byte> stdin, IHookErrorLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var raw = JsText.Trim(Utf8Text.Decode(stdin));
        if (raw.Length == 0)
        {
            return new JsonObject();
        }

        try
        {
            return JsonText.Parse(raw);
        }
        catch (JsonParseException e)
        {
            var head = raw.Length > 40 ? raw[..40] : raw;
            log.Log("readHookInput", $"{e.Message} — unparseable payload, {raw.Length} bytes: {head}");
            return new JsonObject();
        }
    }

    // ---------------------------------------------------------------- paths

    public string UserConfigPath() => AhPaths.UserEntry(Home, ConfigRegistry.ConfigBasename);

    public string PacksSeenPath() => AhPaths.UserEntry(Home, "packs-seen.json");

    public string? FindGitRoot(string start)
    {
        var dir = Resolve(start);
        while (true)
        {
            if (files.Exists(NodePath.Join(dir, ".git")))
            {
                return dir;
            }

            var parent = NodePath.Dirname(dir);
            if (parent == dir)
            {
                return null;
            }

            dir = parent;
        }
    }

    public string HierarchyDir(string? cwd) => OverrideDirectory() ?? DerivedHierarchyDir(cwd);

    public string? MainHierarchyDir(string? cwd) => OverrideDirectory() is not null ? null : DerivedMainHierarchyDir(cwd);

    /// <summary>A path that does not exist is placed nowhere: the git-root walk would otherwise climb out of a removed tree to the main checkout.</summary>
    public bool IsPlaced(string observedCwd, string expectedRoot) =>
        files.Exists(Resolve(observedCwd)) && files.Exists(Resolve(expectedRoot))
        && DerivedHierarchyDir(Real(observedCwd)) == DerivedHierarchyDir(Real(expectedRoot));

    private string Real(string path)
    {
        try
        {
            return files.RealPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// How <c>WRANGL_HIERARCHY_DIR</c> stands: honoured in test mode, or when its resolved path is a counted entry of
    /// <c>trusted-hierarchy-dirs</c>; otherwise it is ignored (and a caller that gates fails closed on <c>Untrusted</c>).
    /// </summary>
    internal HierarchyOverride OverrideState()
    {
        var env = environment.GetVariable("WRANGL_HIERARCHY_DIR");
        if (env is null || JsText.Trim(env).Length == 0)
        {
            return new HierarchyOverride(HierarchyOverrideKind.None, null);
        }

        var resolved = Resolve(JsText.Trim(env));
        if (environment.TestMode)
        {
            return new HierarchyOverride(HierarchyOverrideKind.TestMode, resolved);
        }

        return trust is not null && trust.IsTrusted(TrustFile.HierarchyDirs, resolved)
            ? new HierarchyOverride(HierarchyOverrideKind.Trusted, resolved)
            : new HierarchyOverride(HierarchyOverrideKind.Untrusted, resolved);
    }

    private string? OverrideDirectory() =>
        OverrideState() is { Kind: HierarchyOverrideKind.Trusted or HierarchyOverrideKind.TestMode } honoured ? honoured.Path : null;

    /// <summary>The project directory of a repository root: <c>.claude/ah2</c> under it, or <c>~/.claude/ah2/project</c> when the root is the user's home.</summary>
    internal string ProjectDirOf(string root) => AhPaths.ProjectDir(root, IsUserHome(root));

    internal string ProjectEntryOf(string root, string name) => AhPaths.ProjectEntry(root, name, IsUserHome(root));

    private bool IsUserHome(string root)
    {
        if (Resolve(root) == Resolve(Home))
        {
            return true;
        }

        try
        {
            return files.RealPath(root) == files.RealPath(Home);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The hierarchy directory <paramref name="cwd"/> derives to: its git root's <c>.claude/ah2</c>, else one under the home directory. No environment.</summary>
    internal string DerivedHierarchyDir(string? cwd)
    {
        var baseDir = CwdOr(cwd);
        var root = FindGitRoot(baseDir);
        if (root is not null)
        {
            return ProjectDirOf(root);
        }

        return AhPaths.UserProjectDir(Home, NodePath.Basename(Resolve(baseDir)));
    }

    /// <summary>The main checkout's <c>.claude/ah2</c> for a linked worktree whose pointer is confirmed, else null. No environment.</summary>
    internal string? DerivedMainHierarchyDir(string? cwd)
    {
        var start = CwdOr(cwd);
        if (_derivedMain.TryGetValue(start, out var known))
        {
            return known;
        }

        if (_derivedNoMain.TryGetValue(start, out var none) && MonotonicMs() - none.AtMs < NoMainTtlMs && SameGit(none.Stat, GitStat(none.Probe)))
        {
            return null;
        }

        var root = FindGitRoot(start);
        var main = root is null ? null : MainCheckoutRoot(root);
        var dir = main is null ? null : ProjectDirOf(main);

        // ponytail: kept for the process's life, as a worktree does not move under a running daemon
        if (dir is not null)
        {
            _derivedMain[start] = dir;
        }
        else if (NodePath.Join(root ?? start, ".git") is var probe && GitStat(probe) is var stat && stat is not { Kind: FileKind.File })
        {
            // Only a main checkout's .git directory, or no .git at all, keeps a null: a worktree's .git file whose back-pointer is not
            // confirmed yet is asked every time, as the repair lands in the main checkout's files, not in the .git file.
            // ponytail: a null holds for NoMainTtlMs while that .git is the same directory (or still absent at the start dir); a .git
            // added between the start dir and a root above it is seen at the next ask after that
            _derivedNoMain[start] = (probe, stat, MonotonicMs());
        }

        return dir;
    }

    /// <summary>How long a null main hierarchy dir is kept at most, however its .git looks.</summary>
    internal const long NoMainTtlMs = 60_000;

    /// <summary>The clock a kept null ages by; a test moves it.</summary>
    internal Func<long> MonotonicMs { get; set; } = static () => Environment.TickCount64;

    /// <summary>The start dirs found to have no main hierarchy dir, each with the <c>.git</c> path that answered, its stat then, and when.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Probe, FileStat? Stat, long AtMs)> _derivedNoMain = new(StringComparer.Ordinal);

    private FileStat? GitStat(string path)
    {
        try
        {
            return files.Stat(path, followSymlinks: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // the same .git: absent both times, or the same inode of the same kind
    private static bool SameGit(FileStat? before, FileStat? now) =>
        (before, now) switch
        {
            (null, null) => true,
            ({ } b, { } n) => b.Kind == n.Kind && b.Inode == n.Inode && b.Device == n.Device,
            _ => false,
        };

    /// <summary>The confirmed main hierarchy dirs found so far, by start dir. Each lookup reads <c>.git</c> files and real paths; the daemon asked it per dir on every poll.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _derivedMain = new(StringComparer.Ordinal);

    /// <summary>
    /// The main checkout root when <paramref name="worktreeRoot"/> is a linked worktree: its <c>.git</c> is a file
    /// holding <c>gitdir: &lt;main&gt;/.git/worktrees/&lt;name&gt;</c>. Read from the file system with no git call.
    /// </summary>
    public string? MainCheckoutRoot(string worktreeRoot)
    {
        var dotgit = NodePath.Join(worktreeRoot, ".git");
        string raw;
        try
        {
            raw = files.ReadText(dotgit);
        }
        catch (Exception)
        {
            return null;
        }

        var pointer = GitdirPointer(raw);
        if (pointer is null)
        {
            return null;
        }

        var gitdir = NodePath.Resolve(worktreeRoot, worktreeRoot, JsText.Trim(pointer));
        if (NodePath.Basename(NodePath.Dirname(gitdir)) != "worktrees" || !BackPointerConfirms(gitdir, worktreeRoot))
        {
            return null;
        }

        string commonDir;
        try
        {
            commonDir = NodePath.Resolve(gitdir, gitdir, JsText.Trim(files.ReadText(NodePath.Join(gitdir, "commondir"))));
        }
        catch (Exception)
        {
            commonDir = NodePath.Dirname(NodePath.Dirname(gitdir));
        }

        if (NodePath.Basename(commonDir) != ".git")
        {
            return null;
        }

        var root = NodePath.Dirname(commonDir);
        return root.Length > 0 && root != worktreeRoot ? root : null;
    }

    /// <summary>
    /// Whether <c>&lt;gitdir&gt;/gitdir</c>, the back-pointer git writes into every linked worktree, names <c>&lt;worktreeRoot&gt;/.git</c>
    /// (compared by <c>realpath</c>). A repo cannot forge it inside another repo's <c>.git/worktrees</c>, so a pointer
    /// without it never steers the main directory.
    /// </summary>
    private bool BackPointerConfirms(string gitdir, string worktreeRoot)
    {
        try
        {
            if (files.Stat(gitdir) is not { Kind: FileKind.Directory })
            {
                return false;
            }

            var named = files.ReadText(NodePath.Join(gitdir, "gitdir")).TrimEnd('\n', '\r');
            return named.Length > 0 && files.RealPath(named) == files.RealPath(NodePath.Join(worktreeRoot, ".git"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><c>/^gitdir:\s*(.+)$/m.exec(raw)[1]</c>, with <c>.</c> excluding the four line terminators.</summary>
    private static string? GitdirPointer(string raw)
    {
        static bool Terminator(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && !Terminator(raw[i - 1]))
            {
                continue;
            }

            if (string.CompareOrdinal(raw, i, "gitdir:", 0, 7) != 0)
            {
                continue;
            }

            var runStart = i + 7;
            var runEnd = runStart;
            while (runEnd < raw.Length && JsText.IsSpace(raw[runEnd]))
            {
                runEnd++;
            }

            for (var m = runEnd; m >= runStart; m--)
            {
                if (m >= raw.Length || Terminator(raw[m]))
                {
                    continue;
                }

                var end = m;
                while (end < raw.Length && !Terminator(raw[end]))
                {
                    end++;
                }

                return raw[m..end];
            }
        }

        return null;
    }

    public string PathSlug(string absolute) => Resolve(absolute).Replace('/', '-');

    internal Dictionary<string, List<string>> RosterLevelCandidateLists(string? cwd)
    {
        var resolvedCwd = Resolve(CwdOr(cwd));
        var repoRoot = FindGitRoot(resolvedCwd) ?? resolvedCwd;
        var roots = new List<string> { repoRoot };
        var main = MainCheckoutRoot(repoRoot);
        if (main is not null)
        {
            roots.Add(main);
        }

        return new Dictionary<string, List<string>>
        {
            ["repo-user"] = roots.Select(r => AhPaths.RepoUserConfig(Home, PathSlug(r))).ToList(),
            ["repo"] = roots.Select(r => ProjectEntryOf(r, ConfigRegistry.ConfigBasename)).ToList(),
            ["global"] = [UserConfigPath()],
        };
    }

    public TeamPrefixResult TeamPrefixInfo(string? cwd, string? team)
    {
        if (!string.IsNullOrEmpty(team))
        {
            return new TeamPrefixResult(team, "team");
        }

        var resolvedCwd = Resolve(CwdOr(cwd));
        var defaultPrefix = NodePath.Basename(FindGitRoot(resolvedCwd) ?? resolvedCwd);
        var legacy = Teams.LegacyTeamPrefix(HierarchyDir(resolvedCwd), defaultPrefix);
        if (!string.IsNullOrEmpty(legacy))
        {
            return new TeamPrefixResult(legacy, "legacy-team");
        }

        return new TeamPrefixResult(defaultPrefix, "default");
    }

    public string TeamPrefix(string? cwd, string? team) => TeamPrefixInfo(cwd, team).Prefix;

    // ---------------------------------------------------------------- reading JSON files the way Node does

    /// <summary><c>existsSync(path) ? JSON.parse(readFileSync(path, "utf8")) : null</c> in a try: null when absent, unreadable or invalid.</summary>
    internal JsonValue? TryReadJson(string path)
    {
        try
        {
            if (!files.Exists(path))
            {
                return null;
            }

            return JsonText.Parse(files.ReadText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- role lookup

    /// <summary><c>hierarchyRoleOf</c>: the registry role an <c>agent_type</c> names, or null.</summary>
    internal string? HierarchyRoleOf(string? agentType, JsonObject? resolved, string? cwd = null)
    {
        if (string.IsNullOrEmpty(agentType))
        {
            return null;
        }

        if (BuiltinRoleOf(agentType) is { } builtin)
        {
            return builtin;
        }

        if (resolved is null && !string.IsNullOrEmpty(cwd))
        {
            try
            {
                resolved = ResolveConfig(cwd);
            }
            catch (Exception)
            {
                resolved = null;
            }
        }

        if (resolved is not null && resolved.Get("roles") is { IsMap: true } roles)
        {
            var hit = ConfigRegistry.RegistryRoles(resolved)
                .Where(role => ConfigRegistry.RoleClass(role, resolved) != ConfigRegistry.OrchestrateClass)
                .FirstOrDefault(role => ConfigRegistry.RoleAgent(role, roles.AsObject().Get(role), PluginName) == agentType);
            if (hit is not null)
            {
                return hit;
            }
        }

        return ConfigRegistry.Roles
            .Where(role => ConfigRegistry.BuiltinClass(role) != ConfigRegistry.OrchestrateClass)
            .FirstOrDefault(role => agentType == role || agentType.EndsWith(":" + role, StringComparison.Ordinal));
    }

    public HierarchyRole ResolveHierarchyRole(JsonValue input, JsonObject? resolved = null)
    {
        var cwd = input.Member("cwd")?.NonEmptyText ?? process.CurrentDirectory;
        string? direct = null;
        var agentType = input.Member("agent_type");
        if (agentType is { IsTruthy: true })
        {
            var type = agentType.Value.IsText ? agentType.Value.AsString() : null;
            if (resolved is not null)
            {
                direct = HierarchyRoleOf(type, resolved);
            }
            else if (!string.IsNullOrEmpty(type))
            {
                // lookupRole: a built-in <plugin>:<role> needs no config; anything else reads it, failing open.
                if (BuiltinRoleOf(type) is { } builtinRole)
                {
                    direct = builtinRole;
                }
                else
                {
                    try
                    {
                        resolved = ResolveConfig(cwd);
                    }
                    catch (Exception)
                    {
                        resolved = null;
                    }

                    direct = HierarchyRoleOf(type, resolved);
                }
            }
        }

        if (direct is not null)
        {
            return new HierarchyRole(direct, true, resolved);
        }

        var persisted = ReadSessionRoleByKey(SessionKey(input.Member("session_id")));
        return new HierarchyRole(persisted, false, null);
    }

    /// <summary><c>normalizeSessionId</c>: the trimmed id when it is a non-blank string, else the no-session key.</summary>
    internal static string SessionKey(JsonValue? sessionId) =>
        sessionId is { IsText: true } s && JsText.Trim(s.AsString()).Length > 0 ? JsText.Trim(s.AsString()) : "__nosession__";

    /// <summary><c>readSessionRole</c> for a normalised key: the role recorded for the session, or null.</summary>
    internal string? ReadSessionRoleByKey(string key)
    {
        var entry = SessionRoles["sessions"].AsObject().Get(key);
        return entry is { IsMap: true } e && e.AsObject().Get("role") is { IsText: true } role ? role.AsString() : null;
    }
}
