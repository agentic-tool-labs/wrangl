using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Hierarchy;

internal sealed partial class HierLib
{
    private const string StatusTagRulePrefix = "- Tag decisions, deviations, roadblocks, outcomes and details as ";

    private const string ReleasePlaceholderPrefix = "- Release: <ready branch=<branch> sha=<sha> inflight=none | not-ready";

    private const string CheckpointPlaceholderPrefix = "- Checkpoint: <ready branch=<branch> sha=<sha> inflight=none | not-ready";

    public IReadOnlyList<string> PoolDirs(string dir, IReadOnlyList<string> teams, IReadOnlyList<string>? trees = null) =>
        EnumeratePools(dir, teams, trees).Pools;

    public IReadOnlyList<string> Family(string teamFile)
    {
        var home = RosterModule.TeamFileHome(teamFile);
        if (home is null || !teamFile.EndsWith(".json", StringComparison.Ordinal)
            || !_roster.ReadTeamFileAt(teamFile).IsMap)
        {
            return [];
        }

        // PoolDirs finds a team's member pools through teams/<name>.json, so the name is the file name; a team file kept elsewhere has no named team to enumerate.
        var team = NodePath.Basename(NodePath.Dirname(teamFile)) == "teams" ? NodePath.Basename(teamFile)[..^5] : string.Empty;
        return PoolDirs(home, [team]).DistinctBy(PoolKey, StringComparer.Ordinal).ToList();
    }

    public JsonValue Locate(string id, string type, IReadOnlyList<string> teamFiles, IReadOnlyList<string> fallbackDirs)
    {
        var pools = teamFiles.SelectMany(teamFile => Family(teamFile).Select(pool => (Pool: pool, TeamFile: (string?)teamFile)))
            .Concat(fallbackDirs.Select(pool => (Pool: pool, TeamFile: (string?)null)));
        foreach (var candidate in pools.DistinctBy(p => PoolKey(p.Pool), StringComparer.Ordinal))
        {
            if (FindByIdAndType(candidate.Pool, id, type).Member("path") is { IsText: true } path)
            {
                return new JsonObject
                {
                    ["path"] = path,
                    ["pool"] = candidate.Pool,
                    ["team_file"] = candidate.TeamFile is null ? JsonValue.Null : candidate.TeamFile,
                };
            }
        }

        return JsonValue.Null;
    }

    public JsonObject ResponsePool(string requestPath)
    {
        if (_core.ReadMsgFile(requestPath).Member("fm")?.Member("team_file") is not { IsText: true } file || file.AsString().Length == 0)
        {
            return new JsonObject { ["status"] = "no-team" };
        }

        if (!_roster.TeamExistsAt(file.AsString()))
        {
            return new JsonObject { ["status"] = "no-team" };
        }

        var pool = PoolOfMessage(requestPath);
        var key = PoolKey(pool);
        if (Family(file.AsString()).Any(dir => PoolKey(dir) == key))
        {
            return new JsonObject { ["status"] = "allowed", ["pool"] = pool, ["team_file"] = file };
        }

        return new JsonObject { ["status"] = "refused", ["reason"] = $"dir {pool} is outside the request's hierarchy directories" };
    }

    /// <summary>The pool a message file sits in: the parent of its nearest <c>msgs</c> directory, so a file under <c>msgs/archive</c> belongs to the same pool.</summary>
    private static string PoolOfMessage(string messagePath)
    {
        for (var at = NodePath.Dirname(messagePath); at.Length > 0 && NodePath.Dirname(at) != at; at = NodePath.Dirname(at))
        {
            if (NodePath.Basename(at) == "msgs")
            {
                return NodePath.Dirname(at);
            }
        }

        return NodePath.Dirname(NodePath.Dirname(messagePath));
    }

    // The main pool of a project root, which costs a walk up to the git root per call. Keyed by the root, so another root is never
    // served this one's pool. This HierLib outlives a command (the daemon's service graph holds it), so an answer, including "none",
    // is kept only for MainPoolKeepMs: long enough to cover one build's repeated listings, short enough that a work tree
    // created later is seen on the next refresh.
    private const double MainPoolKeepMs = 2000;

    // ponytail: no pruning, one entry per project root ever listed in this process (a few dozen bytes each); drop entries older
    // than MainPoolKeepMs on insert if a daemon is ever seen to list thousands of distinct roots.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Pool, double AtMs)> _mainPools = new(StringComparer.Ordinal);

    private string? MainPoolOf(string projectRoot)
    {
        var now = NowMs;
        // A negative age is a clock set back: the kept answer is then of unknown age, so it is walked again.
        if (_mainPools.TryGetValue(projectRoot, out var kept) && now - kept.AtMs is >= 0 and < MainPoolKeepMs)
        {
            return kept.Pool;
        }

        var found = _config.MainHierarchyDir(projectRoot);
        _mainPools[projectRoot] = (found ?? string.Empty, now);
        return found;
    }

    public IReadOnlyList<string> Trees(string dir, IReadOnlyList<string> teams, IReadOnlyList<string>? trees = null) =>
        EnumeratePools(dir, teams, trees).Trees;

    /// <summary>
    /// The one enumeration behind <see cref="PoolDirs"/> and <see cref="Trees"/>: the pools in the order <c>PoolDirs</c> lists them,
    /// and the trees they come from. A tree is kept whether or not it exists or has a <c>msgs</c> directory; the pools keep only
    /// those that do.
    /// </summary>
    private (List<string> Pools, List<string> Trees) EnumeratePools(string dir, IReadOnlyList<string> teams, IReadOnlyList<string>? trees)
    {
        ArgumentNullException.ThrowIfNull(teams);
        var pools = new List<string> { dir };
        // In the store a pool's messages need no directory, so a pool is one in any case; with files it is one once it has msgs/.
        bool HoldsMessages(string pool) => _core.MessagesInDb() || Exists(NodePath.Join(pool, "msgs"));

        var seen = new HashSet<string>(StringComparer.Ordinal) { PoolKey(dir) };
        var found = new List<string>();
        var seenTrees = new HashSet<string>(StringComparer.Ordinal);

        // A work tree's own pool is never the only one: messages filed from the main checkout (an orchestrator's requests, a
        // release request) sit in the main checkout's pool, so it is always searched with the one derived from the cwd.
        if (NodePath.Basename(dir) == "ah2" && NodePath.Basename(NodePath.Dirname(dir)) == ".claude"
            && MainPoolOf(NodePath.Dirname(NodePath.Dirname(dir))) is { Length: > 0 } mainPool
            && HoldsMessages(mainPool) && seen.Add(PoolKey(mainPool)))
        {
            pools.Add(mainPool);
        }

        void AddTree(string? tree)
        {
            if (string.IsNullOrEmpty(tree))
            {
                return;
            }

            var exists = Exists(tree);
            var top = exists ? GitTop(tree) ?? tree : tree;
            if (seenTrees.Add(PoolKey(top)))
            {
                found.Add(top);
            }

            if (!exists)
            {
                return;
            }

            var pool = _config.HierarchyDir(top);
            if (HoldsMessages(pool) && seen.Add(PoolKey(pool)))
            {
                pools.Add(pool);
            }
        }

        // The checkout whose hierarchy directory this is comes first: <tree>/.claude/ah2.
        var claude = NodePath.Dirname(dir);
        if (NodePath.Basename(dir) == "ah2" && NodePath.Basename(claude) == ".claude")
        {
            var main = NodePath.Dirname(claude);
            var top = Exists(main) ? GitTop(main) ?? main : main;
            if (seenTrees.Add(PoolKey(top)))
            {
                found.Add(top);
            }
        }

        var grants = ConfigModule.CreateWorktreeGrants(_files, _environment);
        var scoped = new List<(GrantsView View, List<string> Members)>();
        Func<string, GrantsView>? readTeam = null; // the dir's grants, read once at the first team that has a record (b459)
        foreach (var team in teams)
        {
            if (ReadTeamOrNull(dir, team) is not { } record)
            {
                continue;
            }

            GrantsView? view = null;
            try
            {
                view = (readTeam ??= grants.Reader(dir))(team);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // a grants document that cannot be read leaves the team on its team-file roots
            }

            var members = new List<string>();
            var teamRoot = TextOf(record.Member("expected_root"));
            if (record.Member("members") is { IsArray: true } list)
            {
                foreach (var m in list.AsArray())
                {
                    if (TextOf(m.Member("name")) is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    members.Add(name);
                    AddTree(view is { Present: true } && view.Members.TryGetValue(name, out var granted) ? granted.WorkRoot : TextOf(m.Member("expected_root")) ?? teamRoot);
                }
            }

            if (record.Member("streams") is { IsMap: true } streams)
            {
                var streamObjects = streams.AsObject();
                foreach (var key in streamObjects.Keys)
                {
                    AddTree(TextOf(streamObjects[key].Member("worktree")));
                }
            }

            if (view is { Present: true })
            {
                scoped.Add((view, members));
            }
        }

        foreach (var tree in trees ?? [])
        {
            AddTree(tree);
        }

        foreach (var (view, members) in scoped)
        {
            foreach (var member in members)
            {
                foreach (var extra in grants.EffectiveExtras(view, member, id => ResponseFiled(pools, id)))
                {
                    AddTree(extra.Path);
                }
            }
        }

        return (pools, found);
    }

    public IReadOnlyList<string> PoolsOfRequest(string requestPath)
    {
        ArgumentNullException.ThrowIfNull(requestPath);
        var home = NodePath.Dirname(NodePath.Dirname(requestPath));
        var fm = _core.ReadMsgFile(requestPath).Member("fm");
        if (TextOf(fm?.Member("team_file")) is { Length: > 0 } teamFile && teamFile.EndsWith(".json", StringComparison.Ordinal))
        {
            var fromFile = PoolDirs(NodePath.Dirname(NodePath.Dirname(teamFile)), [NodePath.Basename(teamFile)[..^".json".Length]]);
            return fromFile.Contains(home) ? fromFile : [.. fromFile, home];
        }

        var team = TextOf(fm?.Member("team"));
        return string.IsNullOrEmpty(team) || team.Contains('/', StringComparison.Ordinal) || team.Contains("..", StringComparison.Ordinal) ? [home] : PoolDirs(home, [team]);
    }

    public JsonArray ListExchangesAcross(string dir, IReadOnlyList<string> teams, IReadOnlyList<string>? trees = null) =>
        _core.ListExchanges(PoolDirs(dir, teams, trees));

    public JsonArray ListExchangesAllTeams(string dir) =>
        ListExchangesAcross(dir, _roster.ListTeamNames(dir));

    public JsonArray ListLiveExchangesAcross(string dir, IReadOnlyList<string> teams) =>
        _core.ListExchanges(PoolDirs(dir, teams, null), live: true);

    public JsonArray ListLiveExchangesAllTeams(string dir) =>
        ListLiveExchangesAcross(dir, _roster.ListTeamNames(dir));

    public IReadOnlySet<string> ClosedExchangeIdsAllTeams(string dir, long answeredBeforeMs) =>
        _core.ClosedExchangeIds(PoolDirs(dir, _roster.ListTeamNames(dir), null), answeredBeforeMs);

    /// <summary>The pipeline-anchor exchanges of every pool of every team, closed ones included (in db mode, by the slug index).</summary>
    private JsonArray AnchorExchangesAllTeams(string dir)
    {
        // b459 step 15: held per dir while the msgs, teams and worktree-grants counters read the same and every tree the pools came
        // from still exists or is still missing; a fresh array of the held rows is returned each call
        var key = _domainSeq is { } seq && seq("msgs") is { } msgs && seq("teams") is { } teams && seq("worktree-grants") is { } grants
            ? $"{msgs}:{teams}:{grants}"
            : null;
        if (key is not null)
        {
            (string Key, string[] Trees, bool[] Existed, JsonValue[] Rows) held;
            bool found;
            lock (_anchorHolds)
            {
                found = _anchorHolds.TryGetValue(dir, out held);
            }

            if (found && held.Key == key && held.Trees.Select(Exists).SequenceEqual(held.Existed))
            {
                return [.. held.Rows];
            }
        }

        var (pools, trees) = EnumeratePools(dir, _roster.ListTeamNames(dir), null);
        var existed = trees.Select(Exists).ToArray();
        var rows = _core.ListSlugExchanges(pools, HierarchyModule.PipelineAnchorSlug);
        if (key is not null)
        {
            lock (_anchorHolds)
            {
                _anchorHolds[dir] = (key, [.. trees], existed, [.. rows]);
            }
        }

        return rows;
    }

    private readonly Dictionary<string, (string Key, string[] Trees, bool[] Existed, JsonValue[] Rows)> _anchorHolds = new(StringComparer.Ordinal);

    /// <summary>Drops every dir's held anchor exchanges, so the next read lists them again (the on-demand rebuild).</summary>
    public void ForgetAnchorHolds()
    {
        lock (_anchorHolds)
        {
            _anchorHolds.Clear();
        }
    }

    private JsonArray ListExchangesOfTeam(string dir, JsonValue team) =>
        ListLiveExchangesAcross(dir, TextOf(team) is { Length: > 0 } name ? [name] : []);


    /// <summary>Whether a filled response to the request is in a pool.</summary>
    public bool ResponseFiled(IReadOnlyList<string> poolDirs, string requestId)
    {
        using var db = _core.DbMsgs();
        foreach (var pool in poolDirs)
        {
            try
            {
                if (db.Messages(pool, requestId, "response").Any(r => IsFilledResponse(HierarchyCore.ReadMsgText(r.Path, db))))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a pool or file that cannot be read holds no filed response
            }
        }

        return false;
    }

    private JsonValue? ReadTeamOrNull(string dir, string team)
    {
        try
        {
            return _roster.ReadTeamRecord(dir, team) is { IsMap: true } record ? record : (JsonValue?)null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private string PoolKey(string path)
    {
        try
        {
            return Exists(path) ? _files.RealPath(path) : Resolve(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Resolve(path);
        }
    }

    /// <summary>The top level of the git work tree holding <paramref name="path"/>: its nearest ancestor with a <c>.git</c> entry.</summary>
    private string? GitTop(string path)
    {
        for (var at = path; at.Length > 0; )
        {
            if (Exists(NodePath.Join(at, ".git")))
            {
                return at;
            }

            var parent = NodePath.Dirname(at);
            if (parent == at)
            {
                break;
            }

            at = parent;
        }

        return null;
    }

    /// <summary>A response is filled in when, past its frontmatter, any line is more than a heading, an empty summary bullet, <c>- none</c> or a line of the template.</summary>
    private static bool IsFilledResponse(string text)
    {
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---\n", 3, StringComparison.Ordinal) is var end and >= 0)
        {
            body = text[(end + 5)..];
        }

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "- none" || line.StartsWith('#') || IsEmptySummaryBullet(line)
                || line.StartsWith(StatusTagRulePrefix, StringComparison.Ordinal) || line.StartsWith(ReleasePlaceholderPrefix, StringComparison.Ordinal) || line.StartsWith(CheckpointPlaceholderPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsEmptySummaryBullet(string line)
    {
        if (!line.StartsWith("- [", StringComparison.Ordinal) || !line.EndsWith(':'))
        {
            return false;
        }

        var close = line.IndexOf(']', StringComparison.Ordinal);
        return close > 3 && line[3..close].All(char.IsAsciiDigit);
    }
}
