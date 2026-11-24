using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;
using Ah.Mesh;

namespace Ah.Commands;

/// <summary>
/// <c>roster recover --team &lt;t&gt;</c>, run by the team's owner after a reboot: every member row whose recorded process is gone
/// takes over the one live session registered in the row's pane, and a row with no such session is reported down. A live holder is
/// never displaced, and a session registered as another member never takes a row, so a pane alone proves nothing the owner has not
/// asked for.
/// </summary>
internal static class TeamRecover
{
    internal static int Run(RosterContext c)
    {
        var proved = c.Proved();
        if (proved.Marked || proved.Nearest.Kind is not (NearestKind.Orchestrator or NearestKind.Session))
        {
            throw new RosterFailure("recover --team: only the team's owner may re-stamp its members; nothing was changed.");
        }

        var record = c.Services.Roster.ReadTeamRecord(c.TeamHome, c.TeamFile);
        if (record.IsNull || !record.IsMap || record.Member("members") is not { IsArray: true } rows)
        {
            throw new RosterFailure("recover --team: no team file to recover at this scope; nothing was changed.");
        }

        if (!c.Services.Roster.IsTeamOwnedBy(record, new TeamInvoker(proved.Pid, null, proved.PidStart)))
        {
            throw new RosterFailure("recover --team: this session does not own the team; nothing was changed.");
        }

        var team = record.Member("team") is { IsText: true } named ? named.AsString() : c.TeamArg ?? string.Empty;
        var relinks = PlanRelinks(c, record, team);
        var (restamped, down, untouched) = Restamp(c, record, team);
        foreach (var line in down)
        {
            c.Stdout.WriteLine(line);
        }

        foreach (var line in ApplyRelinks(c, relinks))
        {
            c.Stdout.WriteLine(line);
        }

        foreach (var stale in StreamSupport.StaleStreamWorktrees(c, record))
        {
            c.Stdout.WriteLine($"stale-stream-worktree: stream {stale.Member("stream")!.Value.AsString()}: {stale.Member("worktree")!.Value.AsString()} is gone; {stale.Member("remedy")!.Value.AsString()}");
        }

        c.Stdout.WriteLine($"recover: re-stamped {restamped.Count.ToString(CultureInfo.InvariantCulture)}, down {down.Count.ToString(CultureInfo.InvariantCulture)}, untouched {untouched.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    /// <summary>The re-stamp of the team at the verb's scope, for a caller that has already proved it may. Returns the members re-stamped and the lines of those down.</summary>
    internal static (List<string> Restamped, List<string> Down) Restamp(RosterContext c, JsonValue record)
    {
        var (restamped, down, _) = Restamp(c, record, record.Member("team") is { IsText: true } named ? named.AsString() : c.TeamFile ?? string.Empty);
        return (restamped, down);
    }

    private static (List<string> Restamped, List<string> Down, int Untouched) Restamp(RosterContext c, JsonValue record, string team)
    {
        var rows = record.Member("members") is { IsArray: true } list ? list : new JsonArray();
        var live = c.Services.OwnerProof?.LiveSessions() ?? [];
        var names = rows.AsArray().Select(r => r.Member("name") is { IsText: true } n ? n.AsString() : string.Empty).ToHashSet(StringComparer.Ordinal);
        var heldPids = rows.AsArray().Where(r => r.Member("pid") is { IsNumber: true } p && c.PidAlive(p.AsNumber())).Select(r => r.Member("pid")!.Value.AsNumber()).ToHashSet();

        var restamp = new Dictionary<string, (double OldPid, LiveSession Session)>(StringComparer.Ordinal);
        var down = new List<string>();
        var dead = new List<(string Name, double Pid, string Role, string? Stream, string? Recorded, List<LiveSession> Matches)>();
        var untouched = 0;
        foreach (var row in rows.AsArray())
        {
            // outside-resume.md §4.2: a row with a matched bare mark keeps it until the daemon's relaunch
            if (row.Member("name") is not { IsText: true } nameValue || row.Member("pid") is not { IsNumber: true } pid || !RecordedProcessIsGone(c, row, pid.AsNumber())
                || Ah.Watch.BareMark.From(row.Member("bare")) is { State: Ah.Watch.BareMark.Matched })
            {
                untouched++;
                continue;
            }

            var name = nameValue.AsString();
            var pane = row.Member("transport_id") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
            var matches = pane is null
                ? []
                : live.Where(s => s.PaneId == pane && c.PidAlive(s.Pid) && !heldPids.Contains(s.Pid) && !NamesAnotherMember(s, name, team, names)).ToList();
            var recorded = row.Member("session_id") is { IsText: true } sid && sid.AsString().Length > 0 ? sid.AsString() : null;
            var role = row.Member("role") is { IsText: true } r ? r.AsString() : "<role>";
            var stream = row.Member("stream") is { IsText: true } st ? st.AsString() : null;
            dead.Add((name, pid.AsNumber(), role, stream, recorded, matches));
        }

        var claims = dead.Where(d => d.Matches.Count == 1).GroupBy(d => d.Matches[0].SessionId).ToDictionary(g => g.Key, g => g.Count());
        JsonValue? checkpoint = null;
        foreach (var (name, oldPid, role, stream, recorded, matches) in dead)
        {
            if (matches.Count == 1 && claims[matches[0].SessionId] == 1)
            {
                restamp[name] = (oldPid, matches[0]);
            }
            else if (matches.Count > 1)
            {
                // a live session may be this member: no command, so nothing is followed that could kill it
                down.Add($"{name}: ambiguous: {matches.Count.ToString(CultureInfo.InvariantCulture)} live sessions in pane {matches[0].PaneId}; check by hand");
            }
            else if (matches.Count == 1)
            {
                down.Add($"{name}: contested: live session {matches[0].SessionId} in pane {matches[0].PaneId} is claimed by {claims[matches[0].SessionId].ToString(CultureInfo.InvariantCulture)} rows; check by hand");
            }
            else
            {
                checkpoint ??= CheckpointRecord(c, team);
                var resume = Ah.Mesh.MeshModule.RecordListsMember(checkpoint, name) ? Ah.Mesh.MeshModule.MemberResumeCommand(team, name) : null;
                var lines = Ah.Mesh.MeshModule.MemberHintLines(resume, Ah.Mesh.MeshModule.MemberRespawnCommand(role, stream, team), recorded);
                down.Add($"{name}: down\n  {string.Join("\n  ", lines)}");
            }
        }

        if (restamp.Count > 0)
        {
            c.Services.Hierarchy.StateCalls.UpdateTeam(c.TeamHome, c.TeamFile, doc => Apply(doc, restamp));
            Announce(c, team, [.. restamp.Keys]);
        }

        return ([.. restamp.Keys], down, untouched);
    }

    /// <summary>
    /// <c>--relink-lane &lt;child&gt;</c>, given by the team's owner: a lane child whose <c>parent</c> link names this team and whose
    /// stream the team records a lead for gets the team's current <c>team_id</c> in that link. Nothing in the files can tell a lane
    /// that was disbanded and made again from one that only lost its link, so the owner naming the child is the decision, and a
    /// link that already names another id is overwritten. Every child is checked before anything is written.
    /// </summary>
    private static List<(string Child, string TeamId, string? Overwrites)> PlanRelinks(RosterContext c, JsonValue manager, string team)
    {
        var plan = new List<(string, string, string?)>();
        var roster = c.Services.Roster;
        foreach (var child in c.Opts.All("relink-lane").Distinct(StringComparer.Ordinal))
        {
            var record = roster.ReadTeamRecord(c.TeamHome, child);
            if (record.IsNull || record.Member("parent") is not { IsMap: true } link
                || link.Member("team") is not { IsText: true } linkTeam || linkTeam.AsString() != team
                || link.Member("stream") is not { IsText: true } stream)
            {
                throw new RosterFailure($"recover --relink-lane: {child} is not a lane child of {team}; nothing was changed.");
            }

            if (manager.Member("streams")?.Member(stream.AsString())?.Member("sub_orchestrator") is not { IsText: true } lead || lead.AsString().Length == 0)
            {
                throw new RosterFailure($"recover --relink-lane: {team} records no lane lead for stream {stream.AsString()}, so {child} is not re-linked; nothing was changed.");
            }

            if (manager.Member("team_id") is not { IsText: true } id || id.AsString().Length == 0)
            {
                throw new RosterFailure($"recover --relink-lane: {team} records no team_id to link to; nothing was changed.");
            }

            var old = link.Member("team_id") is { IsText: true } o && o.AsString() != id.AsString() ? o.AsString() : null;
            plan.Add((child, id.AsString(), old));
        }

        return plan;
    }

    private static List<string> ApplyRelinks(RosterContext c, List<(string Child, string TeamId, string? Overwrites)> plan)
    {
        var lines = new List<string>();
        foreach (var (child, id, overwrites) in plan)
        {
            c.Services.Hierarchy.StateCalls.UpdateTeam(c.TeamHome, child, doc =>
            {
                if (!doc.IsMap || doc.Member("parent") is not { IsMap: true } parent)
                {
                    return null;
                }

                var parentCopy = parent.AsObject().Clone();
                parentCopy["team_id"] = id;
                var copy = doc.AsObject().Clone();
                copy["parent"] = parentCopy;
                return TeamFileOutcome.Write(copy);
            });
            lines.Add($"recover: lane child {child} re-linked to this team's current incarnation (by owner){(overwrites is null ? string.Empty : $"; its link named team_id {overwrites}, now overwritten")}");
        }

        return lines;
    }

    /// <summary><paramref name="team"/>'s checkpoint record, or JSON null when it has none or it cannot be read.</summary>
    private static JsonValue CheckpointRecord(RosterContext c, string team)
    {
        try
        {
            var read = c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = c.Dir, ["team"] = team.Length > 0 ? team : "_default" });
            return read.Member("found") is { IsTruthy: true } && read.Member("record") is { } record ? record : JsonValue.Null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return JsonValue.Null;
        }
    }

    internal static bool RecordedProcessIsGone(RosterContext c, JsonValue row, double pid)
    {
        if (!c.PidAlive(pid))
        {
            return true;
        }

        return row.Member("pid_start") is { IsText: true } recorded && OrchestratorSession.StartOf(c, pid) is { } actual && actual != recorded.AsString();
    }

    private static bool NamesAnotherMember(LiveSession session, string name, string team, HashSet<string> members) =>
        session.Addresses.Any(a =>
        {
            var at = a.LastIndexOf('@');
            return at > 0 && a[(at + 1)..] == team && a[..at] != name && members.Contains(a[..at]);
        });

    private static TeamFileOutcome? Apply(JsonValue doc, Dictionary<string, (double OldPid, LiveSession Session)> restamp)
    {
        if (!doc.IsMap || doc.AsObject().Get("members") is not { IsArray: true } rows)
        {
            return null;
        }

        var members = new JsonArray();
        var changed = false;
        foreach (var row in rows.AsArray())
        {
            if (row.IsMap && row.Member("name") is { IsText: true } n && restamp.TryGetValue(n.AsString(), out var plan) && row.Member("pid") is { IsNumber: true } held && held.AsNumber() == plan.OldPid)
            {
                var next = row.AsObject().Clone();
                next["pid"] = (double)plan.Session.Pid;
                if (plan.Session.PidStart is { } start)
                {
                    next["pid_start"] = start;
                }
                else
                {
                    next.Remove("pid_start");
                }

                next["session_id"] = plan.Session.SessionId;
                members.Add(next);
                changed = true;
            }
            else
            {
                members.Add(row);
            }
        }

        if (!changed)
        {
            return null;
        }

        var copy = doc.AsObject().Clone();
        copy["members"] = members;
        return TeamFileOutcome.Write(copy);
    }

    private static void Announce(RosterContext c, string team, List<string> restamped)
    {
        try
        {
            var names = new JsonArray();
            foreach (var m in restamped)
            {
                names.Add(m);
            }

            var id = $"team-recovered:{team}:{c.Services.Clock.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            c.Services.EventMail?.SendEvent(
                c.Cwd,
                c.Dir,
                id,
                "team-recovered",
                $"team {team} recovered after a restart; re-stamped: {string.Join(", ", restamped)}",
                new JsonObject { ["team"] = team, ["restamped"] = names },
                [.. restamped.Select(m => $"{m}@{team}")]);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // an alert only: the rows are already re-stamped
        }
    }
}
