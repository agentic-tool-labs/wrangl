using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy.StateCalls;
using Ah.Hooks.Activity;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Launch;
using Ah.Roster;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The Stop rule that winds a member down when ahd is not running: the member's chain of command is judged at each Stop, and only two
/// observations of Down a grace apart tell it to wind down (the first writes a <c>suspect</c> row and lets the stop through, the second
/// writes the <c>active</c> row and blocks once). Up or Unknown drops the suspicion. A top orchestrator, a subagent and a session with no
/// member box are never judged. It sends no mail.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class StopChainDown
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input) || c.Input.Text("session_id") is not { Length: > 0 } sessionId)
        {
            return HookEmit.Silent;
        }

        try
        {
            if (JobTrack.WhoIs(c) is not { } who || who.Owner == "orchestrator" || s.Sessions is not { } sessions)
            {
                return HookEmit.Silent;
            }

            var latest = LatestRow(s.Hierarchy.Core.PeerRecordsShared(), sessionId);
            var state = latest?.Member("state") is { IsText: true } st ? st.AsString() : string.Empty;
            var now = s.Clock.UtcNow;
            if (state == "active")
            {
                Release(s, sessions, who, sessionId, latest!.Value, now);
                return HookEmit.Silent;
            }

            if (state == "released" && IsCurrentIncarnation(latest!.Value, s.Process.ParentPid, sessions.Table))
            {
                return HookEmit.Silent;
            }

            var chain = DownLink(s, sessions, who, sessionId, now);
            if (chain is null)
            {
                if (state == "suspect")
                {
                    AppendRow(s, "cleared", sessionId, now);
                }

                return HookEmit.Silent;
            }

            if (state != "suspect")
            {
                AppendRow(s, "suspect", sessionId, now, who, chain);
                return HookEmit.Silent;
            }

            if (!(latest!.Value.Member("at") is { IsText: true } at
                && DateTimeOffset.TryParse(at.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since)
                && (now - since).TotalMilliseconds >= RosterModule.ChainDownGraceMs))
            {
                return HookEmit.Silent;
            }

            var link = chain.Link ?? who.Team;
            var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
            AppendRow(s, "active", sessionId, now, who, chain);
            return HookEmit.Block(advisory.Directive.WindDown(link, chain.Why, "orchestrator@" + who.Team, "branch"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return HookEmit.Silent;
        }
    }

    /// <summary>
    /// Releases a lane lead that was restored bare: at a Stop after its <c>active</c> row was written (the block, or the session-start text,
    /// was the delivery), the lead's own lane team gets a <c>released</c> marker for this Claude process and a <c>released</c> row follows.
    /// The marker goes first, so a failed row is completed by the next Stop, which finds the marker already in place. Only the session that
    /// owns the broken link is released; a child member's row names a team its session does not own.
    /// </summary>
    private static void Release(GateServices s, IGateSessions sessions, JobTrack.Who who, string sessionId, JsonValue row, DateTimeOffset now)
    {
        if (row.Member("why") is not { IsText: true } why || why.AsString() != "restored-bare"
            || row.Member("link") is not { IsText: true } link || link.AsString().Length == 0)
        {
            return;
        }

        var pid = s.Process.ParentPid;
        if (pid <= 1 || sessions.Table.StartTime(pid) is not { Length: > 0 } start
            || !(s.Roster.ReadTeamRecord(who.Dir, link.AsString()).Member("orchestrator")?.Member("session_id") is { IsText: true } linkOwner && linkOwner.AsString() == sessionId))
        {
            return;
        }

        var owned = false;
        var at = Timestamps.ToIsoString(now);
        var written = s.Hierarchy.StateCalls.UpdateTeamFile(
            s.Roster.TeamPath(who.Dir, link.AsString()),
            who.Dir,
            doc =>
            {
                owned = false;
                if (doc.Member("orchestrator") is not { IsMap: true } held
                    || !(held.Member("session_id") is { IsText: true } heldSession && heldSession.AsString() == sessionId))
                {
                    return null;
                }

                owned = true;
                if (held.Member("released") is { IsMap: true } marker
                    && JsConvert.AsInteger(marker.Member("pid")) == pid
                    && marker.Member("pid_start") is { IsText: true } markerStart && markerStart.AsString() == start)
                {
                    return null;
                }

                var marked = held.AsObject().Clone();
                marked["released"] = new JsonObject { ["pid"] = (double)pid, ["pid_start"] = start, ["at"] = at };
                var copy = doc.AsObject().Clone();
                copy["orchestrator"] = marked;
                return TeamFileOutcome.Write(copy);
            },
            new StateCallOptions(Hook: true));
        if (owned && written.Result.Ok)
        {
            var released = new JsonObject { ["type"] = "chain-down", ["state"] = "released", ["session_id"] = sessionId };
            foreach (var key in new[] { "member", "team", "link", "why" })
            {
                released[key] = row.Member(key) ?? JsonValue.Null;
            }

            released["pid"] = (double)pid;
            released["pid_start"] = start;
            released["at"] = at;
            s.Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(released));
        }
    }

    /// <summary>Whether a <c>released</c> row names the Claude process this hook runs in.</summary>
    private static bool IsCurrentIncarnation(JsonValue row, int pid, IProcessTable table) =>
        JsConvert.AsInteger(row.Member("pid")) == pid && row.Member("pid_start") is { IsText: true } start && ProcessIdentity.Same(table, pid, start.AsString());

    /// <summary>The Down chain of a member: its own team's chain, and for a lane lead first its own lane team when that is restored-bare. Null unless proven Down.</summary>
    internal static ChainState? DownLink(GateServices s, IGateSessions sessions, JobTrack.Who who, string sessionId, DateTimeOffset now, (string Session, long Pid, string? Start)? own = null)
    {
        var registrations = sessions.Sessions().Select(static r => (r.SessionId, (long)r.Pid, r.PidStart)).ToList();
        if (own is { } self)
        {
            registrations.Insert(0, self);
        }

        return LaunchModule.ChainOfSession(s.Roster, who.Dir, who.Team, sessionId, registrations, sessions.Table, now, s.Process.ParentPid, path => s.Files.Exists(path) ? s.Files.ReadText(path) : null) is { Kind: ChainKind.Down } down ? down : null;
    }

    internal static JsonValue? LatestRow(IEnumerable<JsonValue> rows, string sessionId)
    {
        JsonValue? latest = null;
        foreach (var r in rows)
        {
            if (r is { IsMap: true } && r.Member("type") is { IsText: true } t && t.AsString() == "chain-down"
                && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId)
            {
                latest = r;
            }
        }

        return latest;
    }

    internal static void AppendRow(GateServices s, string state, string sessionId, DateTimeOffset now, JobTrack.Who? who = null, ChainState? chain = null)
    {
        var row = new JsonObject { ["type"] = "chain-down", ["state"] = state, ["session_id"] = sessionId };
        if (who is not null && chain is not null)
        {
            row["member"] = who.Owner;
            row["team"] = who.Team;
            row["link"] = chain.Link ?? who.Team;
            row["why"] = chain.Why;
        }

        row["at"] = Timestamps.ToIsoString(now);
        s.Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(row));
    }
}
