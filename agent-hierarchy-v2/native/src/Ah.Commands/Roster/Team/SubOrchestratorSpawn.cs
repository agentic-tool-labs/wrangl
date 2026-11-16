using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// What a spawn of a member of the orchestrate class (a sub-orchestrator) adds to the ordinary spawn: its exact name, the rules
/// that are checked before any pane opens, and the record of it on its stream.
/// </summary>
internal static partial class SubOrchestratorSpawn
{
    private const string OrchestrateClass = "orchestrate";

    private const int NameLimit = 32;

    private const string Infix = "-so-";

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex("-[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalRegex();

    internal static bool Is(RosterContext c, string? role) =>
        role is not null && c.Services.Config.RoleClass(role, c.Registry) == OrchestrateClass;

    /// <summary>The name of the team the spawn goes into: its team file's name.</summary>
    private static string TeamName(RosterContext c, string dir) =>
        Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(dir, c.TeamFile));

    /// <summary>The name of the team the spawn goes into, for the prefix a member of it is expected to carry.</summary>
    internal static string TeamOf(RosterContext c, string dir) => TeamName(c, dir);

    /// <summary>The name a sub-orchestrator of <paramref name="stream"/> is given: <c>&lt;team&gt;-so-&lt;stream&gt;</c>, with no override.</summary>
    internal static string NameOf(RosterContext c, string dir, string stream) => TeamName(c, dir) + Infix + stream;

    /// <summary>
    /// Refuses, before anything launches, a spawn that cannot be a sub-orchestrator of an open stream of a top-level team: no
    /// stream, a team that has a parent, a stream that already has a live one, one past <c>max_lanes</c>, or a name that is too long or
    /// could be read as a derived member name.
    /// </summary>
    internal static void Preflight(RosterContext c, string dir, string label, JsonValue team, SpawnOne.StreamRef? stream, string role)
    {
        var teamName = TeamName(c, dir);
        if (stream is null)
        {
            throw Refuse(c, "stream-required", $"{label}: a {role} needs --stream <ws>, the open stream it works; nothing was launched");
        }

        if (c.Opts.Text("kind") is { Length: > 0 } kind && kind != "claude")
        {
            throw Refuse(c, "sub-orchestrator-kind", $"{label}: sub-orchestrator runs on claude only; nothing was launched");
        }

        if (team.Member("parent") is { IsTruthy: true })
        {
            throw Refuse(c, "sub-orchestrator-depth", $"{label}: team {teamName} is itself a sub-orchestrator's team, and sub-orchestrators go one level deep; nothing was launched");
        }

        if (RosterModule.LaneBase(team, stream.Name) is null)
        {
            throw Refuse(c, "lane-base-missing", $"{label}: lane-base-missing: open {stream.Name} with --base <integration branch>; nothing was launched");
        }

        var name = NameOf(c, dir, stream.Name);
        if (NameProblem(c, teamName, stream.Name, name) is { } problem)
        {
            throw Refuse(c, "sub-orchestrator-name", $"{label}: {problem}; nothing was launched");
        }

        if (c.Services.Roster.SubOrchestratorTeams(dir, name).Count > 1)
        {
            throw Refuse(c, "sub-orchestrator-name", $"{label}: the name {name} can be read as the sub-orchestrator of more than one team and stream (ambiguous split); open the work under another stream name; nothing was launched");
        }

        var members = team.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [];
        var named = MemberPlanning.Text(stream.Record.Member("sub_orchestrator"));
        if (!string.IsNullOrEmpty(named)
            && members.FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == named) is { IsMap: true } holder
            && IsLive(c, dir, holder))
        {
            throw Refuse(c, "sub-orchestrator-exists", $"{label}: stream {stream.Name} already has a live sub-orchestrator, {named}; nothing was launched");
        }

        var live = members.Count(m => m.IsMap && IsSubOrchestrator(c, m) && IsLive(c, dir, m));
        var (maxLanes, source, _, _) = EffectiveLaneCap.Of(c, team);
        if (live >= maxLanes)
        {
            throw Refuse(c, "sub-orchestrator-breadth", $"{label}: team {teamName} already has {live} live sub-orchestrators ({EffectiveLaneCap.Describe(maxLanes, source)}); raise it with ah roster lane-caps --max-lanes <N>; nothing was launched");
        }
    }

    /// <summary>Why <paramref name="name"/> cannot be a sub-orchestrator's name, or null when it can. Nothing is truncated.</summary>
    private static string? NameProblem(RosterContext c, string teamName, string stream, string name)
    {
        if (name.Length > NameLimit)
        {
            var fits = Math.Max(0, NameLimit - teamName.Length - Infix.Length);
            return $"the name {name} is {name.Length} characters, over the limit of {NameLimit}; the longest stream name that fits team {teamName} is {fits} characters";
        }

        if (!NameRegex().IsMatch(name))
        {
            return $"the name {name} does not match ^[a-z][a-z0-9_-]{{0,31}}$";
        }

        var roles = c.Services.Config.RegistryRoles(c.Registry);
        var clash = roles.FirstOrDefault(r => stream == r || stream.EndsWith("-" + r, StringComparison.Ordinal) || RemoveOrdinal(stream).EndsWith("-" + r, StringComparison.Ordinal));
        if (clash is not null)
        {
            return $"stream {stream} is named like the role {clash}; names ending in -<role> are reserved for a role's derived member names, and this stream's sub-orchestrator would be {name}; name the stream so it does not end in -{clash} (for example {stream}a)";
        }

        return OrdinalRegex().IsMatch(stream)
            ? $"stream {stream} ends in -<digits>; member names ending in -<n> are reserved for numbered members of a role (like {teamName}-architect-4), and this stream's sub-orchestrator would be {name}; name the stream so it does not end in -<digits> (for example {stream}a)"
            : null;
    }

    private static string RemoveOrdinal(string text) => OrdinalRegex().Replace(text, string.Empty, 1);

    private static bool IsSubOrchestrator(RosterContext c, JsonValue member) => Is(c, MemberPlanning.Text(member.Member("role")));

    /// <summary>A member counts against the lane cap and holds its name while it holds its claim: live, still starting, or undecided with a recent sign of life.</summary>
    private static bool IsLive(RosterContext c, string dir, JsonValue member) =>
        c.Services.Launch!.Teardown.MemberLiveness(dir, member, c.TeamFile).Get("holds_claim") is { IsTruthy: true };

    /// <summary>
    /// The lane caps a spawn writes: the two flags when this is a sub-orchestrator spawn (the defaults when absent), refused when
    /// outside 1..16 or not a whole number; any other role may not carry the flags. Null for a spawn that is not a sub-orchestrator's.
    /// </summary>
    internal static (int Members, int Streams)? LaneCapsOf(RosterContext c, string label, bool isSubOrchestrator)
    {
        var members = c.Opts.Text("lane-members");
        var streams = c.Opts.Text("lane-streams");
        if (!isSubOrchestrator)
        {
            if (c.Opts.Keys.Contains("lane-members") || c.Opts.Keys.Contains("lane-streams"))
            {
                throw Refuse(c, "lane-flag-role", $"{label}: --lane-members and --lane-streams apply only to a sub-orchestrator spawn; nothing was launched");
            }

            return null;
        }

        return (CapOf(c, label, "lane-members", members, RosterModule.LaneDefaultMembers), CapOf(c, label, "lane-streams", streams, RosterModule.LaneDefaultStreams));
    }

    internal static int CapOf(RosterContext c, string label, string flag, string? text, int fallback)
    {
        if (!c.Opts.Keys.Contains(flag))
        {
            return fallback;
        }

        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= RosterModule.LaneCapMin && n <= RosterModule.LaneCapMax
            ? n
            : throw Refuse(c, "lane-cap-invalid", $"{label}: --{flag} must be a whole number from {RosterModule.LaneCapMin} to {RosterModule.LaneCapMax}; nothing was launched");
    }

    /// <summary>The member cap of the lane <paramref name="team"/> is the child team of, when the caller is a lane actor (a proven sub-orchestrator, or the owner of the lane's parent acting on it); null otherwise.</summary>
    internal static int? LaneMax(RosterContext c, JsonValue team)
    {
        if (!c.LaneActor || team.Member("parent") is not { IsMap: true } link
            || MemberPlanning.Text(link.Member("team")) is not { } managerName || MemberPlanning.Text(link.Member("stream")) is not { } ws)
        {
            return null;
        }

        return RosterModule.LaneCaps(SubOrchestratorCarve.ReadTeam(c, managerName), ws).Members;
    }

    internal static string LaneFullMessage(string label, int rows, int max) =>
        $"{label}: lane-members-full: {rows}/{max}; dismiss one or ask the top Orchestrator to raise the cap; nothing was launched";

    /// <summary>
    /// Refuses a sub-orchestrator's spawn into its child team when the team already has as many member rows as its lane allows:
    /// a member counts from the moment its spawn writes its row, live or not, until it is dismissed. The top Orchestrator spawning
    /// into a team that is not a lane child is not limited by a lane; on a lane child, as the owner of its parent, it is.
    /// </summary>
    internal static void CheckLaneMembers(RosterContext c, string label, JsonValue team)
    {
        if (LaneMax(c, team) is { } max && RosterModule.LaneMemberRows(team) is var rows && RosterModule.LaneRowsAtCap(rows, max))
        {
            throw Refuse(c, "lane-members-full", LaneFullMessage(label, rows, max));
        }
    }

    /// <summary>
    /// The same refusal, taken on the document being written under the team file's lock, so two spawns cannot both pass at one below
    /// the cap. The member is already launched by then, so the message says so and carries what is left running.
    /// </summary>
    internal static void GuardAppend(RosterContext c, string label, JsonValue team, int rows, string memberName, string? orphaned)
    {
        if (LaneMax(c, team) is { } max && RosterModule.LaneRowsAtCap(rows, max))
        {
            throw Refuse(c, "lane-members-full", orphaned is null
                ? $"{label}: lane-members-full: {rows}/{max}; {memberName} was not launched or recorded"
                : $"{label}: lane-members-full: {rows}/{max}; {memberName} was launched but not recorded — {orphaned}; its pane is being closed");
        }
    }

    /// <summary>
    /// The refusal for a lane leader's team verb naming a member that is not a row of its own team file: a member lent to the lane
    /// lives in its home team's file, which the lane leader does not edit.
    /// </summary>
    internal static RosterFailure NotOwnRow(RosterContext c, string verb, string member)
    {
        var lane = c.TeamFile;
        foreach (var home in c.Services.Roster.ListTeamNames(c.Dir).Where(t => t != lane))
        {
            var lent = c.Services.Roster.ReadTeamRecord(c.Dir, home).Member("members") is { IsArray: true } rows
                && rows.AsArray().Any(m => MemberPlanning.Text(m.Member("name")) == member && MemberPlanning.Text(m.Member("lead")?.Member("team")) == lane);
            if (lent)
            {
                return Refuse(c, "lent-member", $"{verb}: lent-member: {member} is lent to this lane and lives in {home}; ask orchestrator@{home} to transfer it back; nothing was changed");
            }
        }

        return Refuse(c, "not-own-member", $"{verb}: no member named {member} in this lane's team; a lane leader acts on its own team's rows only; nothing was changed");
    }

    private static readonly string[] NotForLanes = ["orchestrator", "sub-orchestrator", "ultra-advisor"];

    /// <summary>
    /// What a lane leader's spawn into its own child team must satisfy before anything launches: a role a lane may have, one the
    /// manager's roster defines (spawn-one only: spawn-ad-hoc takes a role the roster lacks, as on the top team), an open lane, the lane's own stream (filled in when omitted), and a launch directory inside the
    /// lane's work tree. Does nothing for any other caller.
    /// </summary>
    internal static void CheckLaneSpawn(RosterContext c, string dir, string label, string role, JsonValue team)
    {
        if (!c.LaneActor || team.Member("parent") is not { IsMap: true } link
            || MemberPlanning.Text(link.Member("team")) is not { } managerName || MemberPlanning.Text(link.Member("stream")) is not { } ws)
        {
            return;
        }

        if (NotForLanes.Contains(role) || c.Services.Config.RoleClass(role, c.Registry) is OrchestrateClass or "advise")
        {
            throw Refuse(c, "role-not-for-lanes", $"{label}: {role} is not available to a lane leader; nothing was launched");
        }

        var manager = SubOrchestratorCarve.ReadTeam(c, managerName);
        if (MemberPlanning.Text(manager.Member("streams")?.Member(ws)?.Member("state")) != "open"
            || MemberPlanning.Text(team.Member("workstream")?.Member("state")) == "done")
        {
            throw Refuse(c, "lane-closed", $"{label}: lane {ws} is not open; nothing was launched");
        }

        if (c.ParentOwnerLane is { } parentLane && c.Services.Roster.TeamLifeOf(team) == Ah.Roster.TeamLife.Orphaned)
        {
            throw Refuse(c, "lane-lead-gone", $"{label}: {c.TeamArg}'s lead {parentLane.Member} is not running; respawn the lane lead first, nothing was changed");
        }

        var managerRoster = c.Services.Config.ResolveRoster(c.Cwd, c.Services.Roster.TeamRosterKey(dir, managerName), c.RepoBasename, c.Registry);
        var inRoster = label == "spawn-ad-hoc" || (managerRoster.IsMap && managerRoster.Member("members") is { IsArray: true } rows
            ? rows.AsArray().Any(m => MemberPlanning.Text(m.Member("role")) == role)
            : c.Services.Config.ChainRoles(c.Registry).Contains(role));
        if (!inRoster)
        {
            throw Refuse(c, "role-not-in-roster", $"{label}: the roster of {managerName} has no {role}; nothing was launched");
        }

        var stream = ws;
        if (c.Opts.Has("stream") && c.Opts.Text("stream") != ws)
        {
            var named = c.Opts.Text("stream");
            if (named is null || team.Member("streams")?.Member(named) is not { IsMap: true } opened
                || MemberPlanning.Text(opened.Member("state")) != "open")
            {
                throw Refuse(c, "not-child-team", $"{label}: this lane leader works stream {ws} and the open streams of its own team only; nothing was launched");
            }

            stream = named;
        }

        c.StreamOverride ??= stream;
        var tree = stream == ws
            ? MemberPlanning.Text(manager.Member("streams")?.Member(ws)?.Member("worktree")) ?? MemberPlanning.Text(team.Member("streams")?.Member(ws)?.Member("worktree"))
            : MemberPlanning.Text(team.Member("streams")?.Member(stream)?.Member("worktree"));
        if (c.Opts.Has("cwd") && c.Proved().Carve is not null)
        {
            if (tree is null)
            {
                throw Refuse(c, "cwd-outside-lane", $"{label}: --cwd {c.Cwd} cannot be checked: lane {ws} records no work tree; nothing was launched");
            }

            var messages = c.Services.Hierarchy.Messages;
            var launch = messages.RealCwd(c.Cwd);
            var root = messages.RealCwd(tree);
            if (launch != root && !launch.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                throw Refuse(c, "cwd-outside-lane", $"{label}: --cwd {c.Cwd} is outside the lane's work tree {tree}; nothing was launched");
            }
        }
    }

    /// <summary>A custom-kind member runs outside the launch path the lane guards sit on, so neither a sub-orchestrator nor the parent owner staffing its lane may spawn one.</summary>
    internal static void RefuseCustomKind(RosterContext c, string label)
    {
        if (c.LaneActor)
        {
            throw Refuse(c, "lane-custom-kind", $"{label}: --kind custom is not available to a sub-orchestrator; nothing was launched");
        }
    }

    /// <summary>Records <paramref name="member"/> as the sub-orchestrator of <paramref name="stream"/> in the team document being written, with the lane's caps.</summary>
    internal static void Record(JsonObject team, string stream, string member, (int Members, int Streams) caps)
    {
        var streams = team.Get("streams") is { IsMap: true } existing ? existing.AsObject().Clone() : new JsonObject();
        var record = streams.Get(stream) is { IsMap: true } current ? current.AsObject().Clone() : new JsonObject();
        record["sub_orchestrator"] = member;
        record["lane"] = new JsonObject { ["max_members"] = caps.Members, ["max_streams"] = caps.Streams };
        streams[stream] = record;
        team["streams"] = streams;
    }

    private static RosterFailure Refuse(RosterContext c, string code, string message) =>
        c.Refuse(new JsonObject { ["refused"] = code, ["message"] = message }, message);
}
