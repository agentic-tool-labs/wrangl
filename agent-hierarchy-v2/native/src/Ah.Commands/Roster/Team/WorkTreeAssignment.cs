using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>
/// What a spawn records in a team's grants document: the work tree of the member it launched. A team whose grants are not in
/// force is left alone: no grants rows, no main-checkout refusal, no member tmp. An explicit <c>--stream</c> still launches in the
/// stream's recorded worktree. Also the refusal of an implementor whose work tree would be the main checkout.
/// </summary>
internal static class WorkTreeAssignment
{
    internal const string ImplementClass = "implement";

    /// <summary>A work tree: its root, and whether it is the repository's main checkout (its <c>.git</c> is a directory).</summary>
    internal sealed record Tree(string Root, bool IsMainCheckout);

    private static string Team(RosterContext c) => c.TeamFile ?? "team";

    /// <summary>The name of the team whose grants and work trees the verb works on.</summary>
    internal static string TeamOf(RosterContext c) => Team(c);

    /// <summary>Whether the team's grants document is in force (mode <c>log</c> or <c>enforce</c>).</summary>
    internal static bool Active(RosterContext c)
    {
        try
        {
            var status = c.Services.Hierarchy.StateCalls.Grants("status", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c) });
            return status.IsMap && status.Member("active") is { Kind: JsonKind.Bool, IsTruthy: true };
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return false;
        }
    }

    /// <summary>
    /// Gives a team that is being created its grants document in <c>enforce</c> when it has none. A team that already holds a
    /// document, in any mode, keeps it. Returns a warning to show when the document could not be written, else null.
    /// </summary>
    internal static string? EnsureDocument(RosterContext c, string? mode = null)
    {
        try
        {
            var status = c.Services.Hierarchy.StateCalls.Grants("status", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c) });
            if (status.IsMap && status.Member("present") is { Kind: JsonKind.Bool, IsTruthy: true })
            {
                return null;
            }

            c.Services.Hierarchy.StateCalls.Grants("gate", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c), ["mode"] = mode ?? "enforce" });
            return null;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return $"the team's grants document was not written: {e.Message}";
        }
    }

    /// <summary>
    /// Recreates the team's grants document in <paramref name="mode"/> when it has none (a resume of a team whose document was
    /// removed). A team that holds a document keeps it. Returns a warning when the write failed, else null.
    /// </summary>
    internal static string? Restore(RosterContext c, string mode)
    {
        try
        {
            var status = c.Services.Hierarchy.StateCalls.Grants("status", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c) });
            if (status.IsMap && status.Member("present") is { Kind: JsonKind.Bool, IsTruthy: true })
            {
                return null;
            }

            c.Services.Hierarchy.StateCalls.Grants("gate", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c), ["mode"] = mode });
            return null;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return $"the team's grants document was not restored: {e.Message}";
        }
    }

    /// <summary>
    /// Whether <paramref name="workRoot"/> is still a work tree of this team's repository family: a directory with its own
    /// <c>.git</c> entry whose team home is the one this team uses.
    /// </summary>
    internal static bool IsFamilyTree(RosterContext c, string workRoot)
    {
        if (TreeOf(c, workRoot) is not { } tree || tree.Root != workRoot)
        {
            return false;
        }

        var roster = c.Services.Roster;
        var config = c.Services.Config;
        return roster.TeamHome(config.HierarchyDir(workRoot)) == roster.TeamHome(config.HierarchyDir(c.Cwd));
    }

    /// <summary>Whether <paramref name="root"/> is <c>/</c>, the home directory or an ancestor of it, or <c>~/.claude</c> or an ancestor of it.</summary>
    internal static bool TooBroad(RosterContext c, string root)
    {
        var files = c.Services.Files;
        var homePath = c.Services.Environment.HomeDirectory;
        var home = files.Exists(homePath) ? files.RealPath(homePath) : homePath;
        var canonical = files.Exists(root) ? files.RealPath(root) : root;
        return canonical == "/" || CanonicalPath.IsUnder(home, canonical) || CanonicalPath.IsUnder(NodePath.Join(home, ".claude"), canonical);
    }

    /// <summary>
    /// The work root of a member launched in <paramref name="path"/>: its git top level, or <paramref name="path"/> itself when
    /// that top level is too broad to be a work tree (a dotfiles repository at the home directory, say) or when there is none.
    /// </summary>
    internal static string WorkRootOf(RosterContext c, string path)
    {
        var top = TreeOf(c, path)?.Root;
        return top is null || (top != path && TooBroad(c, top)) ? path : top;
    }

    /// <summary>The work tree that holds <paramref name="path"/>: its nearest ancestor with a <c>.git</c> entry, or null.</summary>
    internal static Tree? TreeOf(RosterContext c, string path)
    {
        var files = c.Services.Files;
        for (var at = path; at.Length > 0; at = NodePath.Dirname(at))
        {
            if (files.Stat(NodePath.Join(at, ".git"), followSymlinks: false) is { } git)
            {
                return new Tree(at, git.Kind == FileKind.Directory);
            }

            if (at == "/")
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Refuses, before anything launches, an implementor whose work tree is the main checkout unless <c>--allow-main</c> is given.
    /// Applies only while the team's grants are in force and the member is not joining a stream (whose work tree it gets).
    /// </summary>
    internal static void RefuseMainCheckout(RosterContext c, string label, string member, string? role, string launchCwd, bool inStream)
    {
        if (role is null || c.Services.Config.RoleClass(role, c.Registry) != ImplementClass || c.Opts.IsTrue("allow-main") || inStream)
        {
            return;
        }

        if (TreeOf(c, launchCwd) is not { IsMainCheckout: true } tree || !Active(c))
        {
            return;
        }

        var message =
            $"{label}: {member} is an implement-class role ({role}) and would work in the main checkout {tree.Root}. Give it its own work tree with --stream <name> or --cwd <work tree>, or pass --allow-main to work in the main checkout; nothing was launched";
        throw c.Refuse(
            new JsonObject { ["refused"] = "main-checkout", ["member"] = member, ["work_root"] = tree.Root, ["message"] = message },
            message);
    }

    /// <summary>
    /// Refuses a spawn into a work root that another member of the team holds (in the grants, else by its expected_root) and whose
    /// liveness is not known to be false, unless <c>--replace-live</c> is given. Only a live writer holding the tree refuses a writer;
    /// read-only roles share a tree freely, a stream's tree included.
    /// </summary>
    internal static void RefuseLiveOccupant(RosterContext c, string label, string member, string? role, string workRoot, JsonValue team)
    {
        if (!Ah.Hierarchy.HierarchyModule.IsWriterRole(role) || c.Opts.IsTrue("replace-live")
            || c.Services.Launch is not { } launch || team.Member("members") is not { IsArray: true } list)
        {
            return;
        }

        foreach (var m in list.AsArray())
        {
            if (!m.IsMap || m.Member("name") is not { IsText: true } nameValue || nameValue.AsString() == member
                || !Ah.Hierarchy.HierarchyModule.IsWriterRole(FieldOf(m, "role"))
                || (RecordedRoot(c, nameValue.AsString()) ?? ExpectedRootOf(c, m, team)) is not { } held || !c.Services.Config.IsPlaced(workRoot, held))
            {
                continue;
            }

            var liveness = launch.Teardown.MemberLiveness(c.Dir, m, TeamOf(c));
            var live = liveness.Get("live") is { IsTruthy: true };
            if (liveness.Get("holds_claim") is not { IsTruthy: true })
            {
                continue;
            }

            var holder = nameValue.AsString();
            var holderRole = FieldOf(m, "role") ?? "no role";
            var message = live
                ? $"{label}: tree {workRoot} is owned by live {holder} ({holderRole}); open a stream for a new worktree, or remove {holder} first. Pass --replace-live to spawn anyway."
                : $"{label}: {holder} ({holderRole}) may be live (its liveness could not be checked) in tree {workRoot}, so spawning {member} there could put two writers in one tree. Pass --replace-live to spawn anyway.";
            throw c.Refuse(
                new JsonObject { ["refused"] = "live-member", ["member"] = holder, ["work_root"] = workRoot, ["message"] = message },
                message);
        }
    }

    /// <summary>
    /// Refuses to move a running member out of the tree its session started in. Claude Code pins that session to that tree (the
    /// isolation record of its row, or, for a row with no record, its expected root) and refuses Bash and git anywhere else, while
    /// the grants follow the new tree, so after the move the member could work in neither. Only a relaunch re-roots it.
    /// </summary>
    internal static void RefuseLiveReroot(RosterContext c, string member, JsonValue row, string workRoot)
    {
        var isolated = Ah.Roster.RosterModule.IsolatedTreeOf(row) ?? FieldOf(row, "expected_root");
        if (isolated is null || RealOf(c, isolated) == RealOf(c, workRoot) || c.Services.Launch is not { } launch)
        {
            return;
        }

        var liveness = launch.Teardown.MemberLiveness(c.Dir, row, TeamOf(c));
        if (liveness.Get("live") is not { IsTruthy: true } && liveness.Get("indeterminate") is not { IsTruthy: true })
        {
            return;
        }

        var message = $"{member} is running in {isolated} and Claude Code keeps that session in that tree, so it cannot be re-rooted to {workRoot} while it runs; dismiss it and spawn it again into the new tree (a relaunch is needed). Nothing was changed.";
        throw c.Refuse(
            new JsonObject { ["refused"] = "relaunch-needed", ["member"] = member, ["isolated_worktree"] = isolated, ["work_root"] = workRoot, ["message"] = message },
            message);
    }

    /// <summary>
    /// Refuses, before anything launches or is recorded, a member whose <paramref name="root"/> is the worktree of a stream other
    /// than <paramref name="stream"/> (a member of no stream counts as another stream's). <paramref name="hint"/> says how to fix it.
    /// </summary>
    internal static void RefuseCrossStreamRoot(RosterContext c, string label, string member, string? stream, string root, JsonValue team, Func<string, string> hint)
    {
        if (team.Member("streams") is not { IsMap: true } streams)
        {
            return;
        }

        var objects = streams.AsObject();
        foreach (var name in objects.Keys)
        {
            if (name == stream || FieldOf(objects[name], "worktree") is not { Length: > 0 } worktree || !c.Services.Config.IsPlaced(root, worktree))
            {
                continue;
            }

            var message = $"{label}: {root} is stream {name}'s worktree; {hint(name)}";
            throw c.Refuse(
                new JsonObject { ["refused"] = "cross-stream-root", ["member"] = member, ["stream"] = name, ["work_root"] = root, ["message"] = message },
                message);
        }
    }

    /// <summary>
    /// The work tree of the member's own <c>expected_root</c>, for a team whose grants hold no root for it. A member in a stream
    /// whose root is the team's (inherited, or written out as the same path) is skipped: the stream's tree is not that root.
    /// </summary>
    private static string? ExpectedRootOf(RosterContext c, JsonValue memberRow, JsonValue team)
    {
        var inStream = memberRow.Member("stream") is { IsText: true } stream && stream.AsString().Length > 0;
        var teamRoot = team.Member("expected_root") is { IsText: true } inherited ? WorkRootOf(c, inherited.AsString()) : null;
        var root = memberRow.Member("expected_root") is { IsText: true } own ? WorkRootOf(c, own.AsString()) : teamRoot;
        return inStream && root is not null && teamRoot is not null && RealOf(c, root) == RealOf(c, teamRoot) ? null : root;
    }

    private static string? FieldOf(JsonValue row, string key) => row.Member(key) is { IsText: true } text && text.AsString().Length > 0 ? text.AsString() : null;

    private static string RealOf(RosterContext c, string path) => c.Services.Files.Exists(path) ? c.Services.Files.RealPath(path) : path;

    internal static bool SameTree(RosterContext c, string a, string b) => RealOf(c, a) == RealOf(c, b);

    /// <summary>
    /// Records <paramref name="workRoot"/> as the member's work tree when the team's grants document exists. Returns a warning to
    /// show when the record could not be made (the member is already running), else null.
    /// </summary>
    internal static string? Assign(RosterContext c, string member, string? role, string workRoot, string? stream, PreparedTmp? tmp = null)
    {
        if (TooBroad(c, workRoot))
        {
            return $"the work tree of {member} was not recorded: {workRoot} is the root, the home directory or one of its ancestors, or ~/.claude; launch the member in a work tree";
        }

        var args = new JsonObject
        {
            ["dir"] = c.TeamHome,
            ["team"] = Team(c),
            ["member"] = member,
            ["work_root"] = workRoot,
            ["only_existing"] = true,
        };
        if (role is not null)
        {
            args["role"] = role;
        }

        if (stream is not null)
        {
            args["stream"] = stream;
        }

        if (tmp is not null)
        {
            args["set_tmp"] = MemberTmp.Record(tmp);
        }

        try
        {
            c.Services.Hierarchy.StateCalls.Grants("assign", args);
            return null;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return $"the work tree of {member} was not recorded in the team's grants: {e.Message}";
        }
    }

    /// <summary>
    /// Gives each member of <paramref name="team"/> that is running (or whose liveness could not be told) and holds no work-tree
    /// record one, in the team's work tree of its recorded root. A member that already has a record is left as it is. Returns the
    /// adopted names, the warnings of records that could not be written, and every named member that was not adopted with the reason.
    /// </summary>
    internal static (List<string> Adopted, List<string> Warnings, List<(string Name, string Why)> Skipped) Adopt(RosterContext c, JsonValue team)
    {
        var adopted = new List<string>();
        var warnings = new List<string>();
        var skipped = new List<(string Name, string Why)>();
        if (c.Services.Launch is not { } launch || team.Member("members") is not { IsArray: true } list)
        {
            return (adopted, warnings, skipped);
        }

        foreach (var m in list.AsArray())
        {
            if (!m.IsMap || m.Member("name") is not { IsText: true } nameValue)
            {
                continue;
            }

            var name = nameValue.AsString();
            if (RecordedRoot(c, name) is not null)
            {
                continue;
            }

            var expected = m.Member("expected_root") is { IsText: true } own ? own : team.Member("expected_root");
            if (expected is not { IsText: true } expectedRoot)
            {
                skipped.Add((name, "no expected_root is recorded for it or for its team, so its work tree is unknown"));
                continue;
            }

            var state = launch.Teardown.MemberLiveness(c.Dir, m, c.TeamFile);
            if (state.Get("live") is not { IsTruthy: true } && state.Get("indeterminate") is not { IsTruthy: true })
            {
                skipped.Add((name, "not running: " + (state.Get("why") is { IsText: true } why ? why.AsString() : "no live session found")));
                continue;
            }

            var root = WorkRootOf(c, expectedRoot.AsString());
            var role = m.Member("role") is { IsText: true } r && r.AsString().Length > 0 ? r.AsString() : null;
            var stream = m.Member("stream") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
            if (Assign(c, name, role, root, stream) is { } warning)
            {
                warnings.Add(warning);
                skipped.Add((name, warning));
                continue;
            }

            adopted.Add(name);
        }

        return (adopted, warnings, skipped);
    }

    /// <summary>A work root held by two or more live members of one team; <see cref="Stream"/> is set when they all got it from the same stream.</summary>
    internal sealed record SharedRoot(string Root, List<string> Members, string? Stream);

    /// <summary>
    /// The roots, compared after realpath, that two or more live members of <paramref name="team"/> (the team record) hold in the
    /// grants. <paramref name="alsoLive"/> is counted live without asking, for a member that was only just launched; live members the
    /// grants hold no work root for are added to <paramref name="unknownRoot"/> when it is given.
    /// </summary>
    internal static List<SharedRoot> SharedRoots(RosterContext c, string team, JsonValue record, string? alsoLive = null, List<string>? unknownRoot = null)
    {
        var shared = new List<SharedRoot>();
        if (c.Services.Launch is not { } launch || record.Member("members") is not { IsArray: true } list)
        {
            return shared;
        }

        var status = c.Services.Hierarchy.StateCalls.Grants("status", new JsonObject { ["dir"] = c.TeamHome, ["team"] = team });
        if (status.Member("members") is not { IsMap: true } rows)
        {
            return shared;
        }

        var files = c.Services.Files;
        var byRoot = new Dictionary<string, List<(string Name, string? Stream)>>(StringComparer.Ordinal);
        foreach (var m in list.AsArray())
        {
            if (!m.IsMap || m.Member("name") is not { IsText: true } nameValue)
            {
                continue;
            }

            var name = nameValue.AsString();
            if (name != alsoLive && launch.Teardown.MemberLiveness(c.Dir, m, team).Get("live") is not { IsTruthy: true })
            {
                continue;
            }

            if (rows.Member(name) is not { IsMap: true } row || row.Member("work_root") is not { IsText: true } rootValue)
            {
                unknownRoot?.Add(name);
                continue;
            }

            var root = rootValue.AsString();
            root = files.Exists(root) ? files.RealPath(root) : root;
            if (!byRoot.TryGetValue(root, out var holders))
            {
                byRoot[root] = holders = [];
            }

            holders.Add((name, row.Member("stream") is { IsText: true } st && st.AsString().Length > 0 ? st.AsString() : null));
        }

        foreach (var (root, holders) in byRoot.Where(kv => kv.Value.Count > 1))
        {
            var stream = holders[0].Stream is { } first && holders.All(h => h.Stream == first) ? first : null;
            shared.Add(new SharedRoot(root, [.. holders.Select(h => h.Name)], stream));
        }

        return shared;
    }

    /// <summary>The work root the grants recorded for <paramref name="member"/>, or null.</summary>
    internal static string? RecordedRootOf(RosterContext c, string member) => RecordedRoot(c, member);

    private static string? RecordedRoot(RosterContext c, string member)
    {
        try
        {
            var status = c.Services.Hierarchy.StateCalls.Grants(
                "status", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c), ["member"] = member });
            return status.IsMap && status.Member("work_root") is { IsText: true } root ? root.AsString() : null;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return null;
        }
    }

    /// <summary>Records <paramref name="tmp"/> on the member's existing work-tree record, leaving the rest of the record as it is.</summary>
    internal static string? SetTmp(RosterContext c, string member, PreparedTmp tmp)
    {
        try
        {
            c.Services.Hierarchy.StateCalls.Grants(
                "assign",
                new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c), ["member"] = member, ["tmp_only"] = true, ["set_tmp"] = MemberTmp.Record(tmp) });
            return null;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return $"the tmp of {member} was not recorded in the team's grants: {e.Message}";
        }
    }

    /// <summary>The tmp recorded for <paramref name="member"/>, or null when there is none (or no document).</summary>
    internal static Ah.Config.GrantTmp? RecordedTmp(RosterContext c, string member)
    {
        try
        {
            var status = c.Services.Hierarchy.StateCalls.Grants(
                "status", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c), ["member"] = member });
            return status.IsMap && status.Member("tmp") is { IsMap: true } t && t.Member("root") is { IsText: true } root
                ? new Ah.Config.GrantTmp(
                    root.AsString(),
                    t.Member("alias") is { IsText: true } alias ? alias.AsString() : string.Empty,
                    t.Member("kind") is { IsText: true } kind ? kind.AsString() : "claude")
                : null;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return null;
        }
    }

    /// <summary>
    /// Drops the member's work-tree record, and with it every grant it held, after removing its tmp.
    /// A team with no grants document is left alone.
    /// </summary>
    internal static string? Release(RosterContext c, string member)
    {
        var tmpWarning = MemberTmp.Dismiss(c, member);
        try
        {
            c.Services.Hierarchy.StateCalls.Grants("unassign", new JsonObject { ["dir"] = c.TeamHome, ["team"] = Team(c), ["member"] = member });
            return tmpWarning;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or Ah.Core.Json.StoreUnavailableException))
        {
            return $"the grants of {member} were not released: {e.Message}";
        }
    }
}
