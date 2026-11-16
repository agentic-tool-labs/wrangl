using System.Text;
using Ah.Core.Json;
using Ah.Hooks.Plumbing;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The block a sub-orchestrator session is told at start: the workstream it runs, its duties and how it closes its sub-team. It is
/// shown to a member whose row has the orchestrate class and to a session that owns a team with a parent; every other session gets nothing.
/// </summary>
internal static class SubOrchestratorStart
{
    internal static string? Block(GateContext c, AdvisoryServices advisory, JsonObject resolved, string cwd, string sessionId, int ppid, string? paneId, string? source, string? childRefusal = null, string? adoptedFrom = null)
    {
        var s = c.Services;
        var dir = s.Config.HierarchyDir(cwd);
        var boxes = advisory.Callers.ResolveCaller(new CallerRequest(cwd, sessionId.Length > 0 ? sessionId : null, ppid, paneId));
        foreach (var box in boxes.Where(b => b.Team is not null && b.Name is not null && b.TeamFile is not null))
        {
            var team = s.Roster.ReadTeamFileAt(box.TeamFile!);
            if (!team.IsMap)
            {
                continue;
            }

            if (box.Name == "orchestrator")
            {
                if (team.Member("parent") is { IsMap: true } parent && Text(parent, "team") is { } manager && Text(parent, "stream") is { } stream)
                {
                    return Compose(advisory, sessionId, manager, stream, team.Member("workstream"), source, LaneLine(s, dir, manager, stream), childRefusal, adoptedFrom, PausedLine(team.Member("workstream")));
                }

                continue;
            }

            var row = team.Member("members") is { IsArray: true } list ? list.AsArray().FirstOrDefault(m => Text(m, "name") == box.Name) : default;
            if (row.IsMap && Text(row, "role") is { } role && s.Config.RoleClass(role, resolved) == "orchestrate")
            {
                var streams = team.Member("streams");
                var ws = streams is { IsMap: true } map
                    ? map.AsObject().Keys.FirstOrDefault(k => Text(map.AsObject()[k], "sub_orchestrator") == box.Name)
                    : null;
                var lane = ws is null || s.Roster.FindChildTeam(dir, box.Team!, ws) is not { } laneChild ? JsonValue.Null : s.Roster.ReadTeamRecord(dir, laneChild);
                return Compose(advisory, sessionId, box.Team!, ws ?? "<workstream>", null, source, ws is null ? null : LaneLine(s, dir, box.Team!, ws), childRefusal, adoptedFrom, PausedLine(lane.Member("workstream")));
            }
        }

        return null;
    }

    /// <summary>The line telling a sub-orchestrator its lane is paused, or null when it is not. A new lead does not resume a paused lane.</summary>
    private static string? PausedLine(JsonValue? workstream)
    {
        if (workstream is not { IsMap: true } record || Text(record, "state") != "paused")
        {
            return null;
        }

        return $"your lane is paused since {Text(record, "updated") ?? "(unknown)"} by {Text(record, "paused_by") ?? "(unknown)"} ({Text(record, "note") ?? "no note"}); when you resume, run ah roster stream-report --state working --summary \"<one line>\"";
    }

    private static string? Text(JsonValue v, string key) => v.Member(key) is { IsText: true } t ? t.AsString() : null;

    /// <summary>Where the lane stands: its base, and its open streams and live members against the caps the top Orchestrator set.</summary>
    private static string? LaneLine(GateServices s, string dir, string manager, string stream)
    {
        var managerTeam = s.Roster.ReadTeamRecord(dir, manager);
        if (managerTeam.IsNull)
        {
            return null;
        }

        var (maxMembers, maxStreams) = RosterModule.LaneCaps(managerTeam, stream);
        var laneBase = RosterModule.LaneBase(managerTeam, stream) ?? "(none recorded)";
        var childName = s.Roster.FindChildTeam(dir, manager, stream);
        var child = childName is null ? JsonValue.Null : s.Roster.ReadTeamRecord(dir, childName);
        var open = child.Member("streams") is { IsMap: true } streams
            ? streams.AsObject().Keys.Count(k => Text(streams.AsObject()[k], "state") != "done")
            : 0;
        var live = childName is not null && child.Member("members") is { IsArray: true } members
            ? members.AsArray().Count(m => Text(m, "name") is { } name && Ah.Hierarchy.HierarchyModule.AttributedRunning(s.Hierarchy.Roster.AttributedLivenessIn(dir, name, childName)))
            : 0;
        return $"Lane {stream}: base {laneBase}, streams {open}/{maxStreams}, members {live}/{maxMembers}; child streams must be named {stream}-…";
    }

    private static string Compose(AdvisoryServices advisory, string sessionId, string manager, string stream, JsonValue? workstream, string? source, string? laneLine, string? childRefusal = null, string? adoptedFrom = null, string? pausedLine = null)
    {
        var b = new StringBuilder();
        if (source is "compact" or "resume")
        {
            _ = b.Append("Re-read your lane brief/charter before any other action.\n");
        }

        _ = b.Append("SUB-ORCHESTRATOR: you run workstream `").Append(stream).Append("` for the orchestrator of team `").Append(manager)
            .Append("` (orchestrator@").Append(manager).Append(").\n");
        if (laneLine is not null)
        {
            _ = b.Append("- ").Append(laneLine).Append('\n');
        }

        if (pausedLine is not null)
        {
            _ = b.Append("- ").Append(pausedLine).Append('\n');
        }

        var brief = advisory.MailboxHooks.OpenMailBriefs(sessionId.Length > 0 ? sessionId : null) is { Count: > 0 } open ? open[0] : null;
        if (brief is not null)
        {
            _ = b.Append("- Brief: ").Append(brief.Ref).Append('\n');
        }

        if (source == "compact" && workstream is { IsMap: true } record)
        {
            _ = b.Append("- Current workstream record: ").Append(Encoding.UTF8.GetString(JsonText.ToUtf8(record, 0))).Append('\n');
        }

        var childLine = adoptedFrom is null
            ? $"Your sub-team `{manager}-{stream}` is created for you at start, empty."
            : $"You took over your lane's sub-team `{manager}-{stream}` from session {adoptedFrom} (its process is gone); its members and streams are as they were.";
        var heldChild = childRefusal is not null
            && (childRefusal.StartsWith("lane-child-", StringComparison.Ordinal) || childRefusal.StartsWith("not-child-team", StringComparison.Ordinal));
        if (heldChild)
        {
            _ = b.Append($"- Your lane's sub-team `{manager}-{stream}` exists but was not taken over at start ({childRefusal}). Do not create it again. ");
            _ = childRefusal!.StartsWith("not-child-team", StringComparison.Ordinal)
                ? b.Append("It is not the child of this stream; report NEEDS-DECISION to the orchestrator.")
                : b.Append("If its owner is gone, run `ah roster adopt --team ").Append(manager).Append('-').Append(stream)
                    .Append(" --orchestrator-pid <your claude pid>`, or start this session again (resume or /clear); if another live session holds it, report NEEDS-DECISION to the orchestrator.");
            _ = b.Append(" Do not staff it until it is yours.\n");
        }

        _ = b.Append(heldChild ? string.Empty : childRefusal is null
            ?$"- {childLine} Staff it: `ah roster spawn-one <role> --team {manager}-{stream}` (or `spawn-ad-hoc`). Open child streams as `ah roster stream-open {stream}-<name> --team {manager}-{stream}` (the `{stream}-` prefix is required). Report with `ah progress --team {manager}-{stream}`. `--team {manager}` is refused by design.\n"
            : $"- Your sub-team was not created at start ({childRefusal}); create it with `ah roster create --parent {manager} --stream {stream}`, or start this session again (resume or /clear). Then staff it with `ah roster spawn-one <role> --team {manager}-{stream}`, open child streams as `ah roster stream-open {stream}-<name> --team {manager}-{stream}`, report with `ah progress --team {manager}-{stream}`. `--team {manager}` is refused by design.\n")
            .Append("- Report status with `ah roster stream-report --state working|blocked|review|done --summary \"<one line>\"`.\n")
            .Append("- On each state change send one note: `ah msg send orchestrator@").Append(manager)
            .Append(" --kind note` whose first line is `[workstream ").Append(stream).Append(" <state>] <summary>`.\n")
            .Append("- Interim status goes as `msg send --kind note`. Write your charter response only at lane-done.\n")
            .Append("- Finishing the lane: run `stream-report --state done`, then file your charter response (`ah msg fill --id <charter id> --from sub-orchestrator --body-file -`; it is refused until the lane is done), ")
            .Append("send it with `ah msg send orchestrator@").Append(manager).Append(" --kind report --ref <charter id>`, and attach it with `stream-report --state done --result <its response file>`. Never a pointer-only SendMessage.\n")
            .Append("- Closing your sub-team: collect release answers first, then wait for the orchestrator's close green light, then run `disband --close`. ")
            .Append("Never pass `--ignore-release` or `--kill`.\n")
            .Append("- Your own release answer: `Release: not-ready reason=sub-team live (<n> members, <m> open exchanges)` while any sub-team member's process is not gone or any exchange is open; ")
            .Append("`ready` only after your sub-team is closed and your worktree is clean.\n")
            .Append("- User questions go to the orchestrator as a request (`reason: context`, slug `ws-").Append(stream).Append("-question`), together with `stream-report --state blocked`.\n")
            .Append("- An Ultra-Advisor need goes to the orchestrator as NEEDS-ULTRA-ADVISOR.\n")
            .Append("- You do not spawn sub-orchestrators. You never push; you merge only on your own lane branch, in your lane's tree.\n")
            .Append("- You decide contained, reversible calls in your own lane. Shipping stays with the orchestrator.");
        return b.ToString();
    }
}
