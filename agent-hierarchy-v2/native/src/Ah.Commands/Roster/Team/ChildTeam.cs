using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>The parent team and stream a <c>create --parent &lt;team&gt; --stream &lt;ws&gt;</c> names, and what the new team takes from them.</summary>
internal sealed record ChildTeamSpec(string Parent, string Stream, JsonValue StreamRecord, string? ParentTeamId = null);

/// <summary>
/// <c>create --parent --stream</c>: the team of a sub-orchestrator. The parent team's stream is copied into it, it is born
/// <c>parent</c>-marked with a workstream status, and its grants document takes the parent's gate mode.
/// </summary>
internal static class ChildTeam
{
    /// <summary>The spec the flags name, or null when neither is given; refuses, before anything is written, a spec that cannot be a child team.</summary>
    internal static ChildTeamSpec? Parse(RosterContext c, string dir)
    {
        var parent = c.Opts.Text("parent");
        var stream = c.Opts.Text("stream");
        if (!c.Opts.Has("parent") && !c.Opts.Has("stream"))
        {
            return null;
        }

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(stream))
        {
            throw new RosterFailure("create: --parent <team> and --stream <ws> go together; pass both or neither");
        }

        var roster = c.Services.Roster;
        var team = roster.ReadTeamRecord(dir, parent);
        if (team.IsNull)
        {
            throw new RosterFailure($"create --parent: team {parent} does not exist; nothing was written");
        }

        var record = c.Services.Launch!.Streams.StreamsOf(team).Get(stream);
        if (record is not { IsTruthy: true } found || MemberPlanning.Text(found.Member("state")) == "done")
        {
            throw new RosterFailure($"create --parent: team {parent} has no open stream named {stream}; nothing was written");
        }

        if (c.Proved().Carve is null && MemberPlanning.Text(found.Member("sub_orchestrator")) is { Length: > 0 })
        {
            throw new RosterFailure($"create --parent: child-team-owned-by-so: the lane's sub-orchestrator creates its child team at start; nothing was written");
        }

        if (c.Proved().Carve is not null && roster.FindChildTeam(dir, parent, stream) is { } existing)
        {
            var child = roster.ReadTeamRecord(dir, existing);
            var fix = roster.TeamIsOrphaned(child) && !(child.Member("members") is { IsArray: true } members && members.AsArray().Count > 0)
                ? $"; it is orphaned with no members: take it over with `ah roster adopt --team {existing}`"
                : string.Empty;
            throw new RosterFailure($"create --parent: lane-child-exists: stream {stream} already has the child team {existing}; a lane has one child team; nothing was written{fix}");
        }

        if (team.Member("parent") is { IsTruthy: true })
        {
            throw new RosterFailure($"create --parent: team {parent} is itself a sub-team, and sub-orchestrators go one level deep; nothing was written");
        }

        foreach (var owned in roster.OwnedTeams(dir, c.Invoker()))
        {
            if (roster.ReadTeamRecord(dir, owned).Member("parent") is { IsTruthy: true })
            {
                throw new RosterFailure($"create --parent: you already own a sub-team ({owned ?? "the default team"}); a sub-orchestrator owns exactly one; nothing was written");
            }
        }

        return new ChildTeamSpec(parent, stream, found, MemberPlanning.Text(team.Member("team_id")));
    }

    /// <summary>Names the team <c>&lt;parent&gt;-&lt;ws&gt;</c> when <c>--team</c> does not.</summary>
    internal static void NameTheTeam(RosterContext c, ChildTeamSpec spec)
    {
        if (c.TeamArg is not null || c.TeamDefaultExplicit)
        {
            return;
        }

        c.TeamArg = $"{spec.Parent}-{spec.Stream}";
        c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, c.TeamArg);
        RosterCli.ResolveTeamFileScope(c);
    }

    /// <summary>What the team document carries because it is a child: its parent, its copy of the stream, and a workstream status.</summary>
    internal static void AddRecords(RosterContext c, JsonObject team, ChildTeamSpec spec)
    {
        Ah.Hierarchy.HierarchyModule.AddLaneChildRecords(team, spec.Parent, spec.Stream, spec.StreamRecord, c.Services.Hierarchy.Messages.LocalIso(), spec.ParentTeamId);
    }

    /// <summary>The gate mode of the parent team's grants document, or null when the parent has none.</summary>
    internal static string? ParentGateMode(RosterContext c, ChildTeamSpec spec)
    {
        try
        {
            var status = c.Services.Hierarchy.StateCalls.Grants("status", new JsonObject { ["dir"] = c.TeamHome, ["team"] = spec.Parent });
            return status.IsMap && status.Member("present") is { IsTruthy: true } && MemberPlanning.Text(status.Member("mode")) is { Length: > 0 } mode ? mode : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Creates the checklist for the child's copy of the stream, beside the one the team's own progress directory got.</summary>
    internal static string? EnsureChecklist(RosterContext c, ChildTeamSpec spec)
    {
        try
        {
            var tree = MemberPlanning.Text(spec.StreamRecord.Member("worktree"));
            var info = new Ah.Hierarchy.ProgressStreamInfo(
                "—",
                MemberPlanning.Text(spec.StreamRecord.Member("branch")) is { Length: > 0 } branch ? branch : "—",
                tree is { Length: > 0 } ? Path.GetFileName(tree.TrimEnd('/')) : "—",
                null);
            c.Services.Hierarchy.Progress.EnsureStream(c.ProgressPool, c.ProgressTeam, spec.Stream, info);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"progress: the stream's checklist was not created: {e.Message}";
        }
    }
}
