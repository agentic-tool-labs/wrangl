using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Commands;

internal static class WhoamiVerb
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team", "json" };

    /// <summary>Which team and member row this session is, and how that was found; never writes.</summary>
    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"whoami: unrecognized flag --{key} (use --team or --cwd)");
            }
        }

        var dir = c.Dir;
        var hierarchy = c.Services.Hierarchy;
        var pid = c.OrchestratorPid();
        JsonValue myRow = JsonValue.Null;
        var latestStart = Ah.Core.Time.PhaseTrace.Begin();
        if (RosterContext.IsInteger(pid))
        {
            myRow = LatestRowOfPid(hierarchy.Roster.LatestRoster(dir), pid);
        }

        Ah.Core.Time.PhaseTrace.End("whoami.latest-roster", latestStart);

        var paneId = c.SessionPaneId(myRow);
        var sessionEnv = c.EnvText("CLAUDE_CODE_SESSION_ID");
        var rowSession = RosterJson.Truthy(RosterJson.Member(myRow, "session_id"));
        var mySessionId = sessionEnv is not null ? JsonValue.FromString(sessionEnv) : rowSession;

        var registeredPid = JsonValue.Null;
        var readRosterStart = Ah.Core.Time.PhaseTrace.Begin();
        if (sessionEnv is not null)
        {
            JsonValue? upRow = null;
            foreach (var row in hierarchy.Roster.ReadRoster(dir))
            {
                if (row.Member("status") is { IsText: true } status && status.AsString() == "up"
                    && row.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionEnv)
                {
                    upRow = row;
                }
            }

            if (upRow is { } up && RosterJson.IsInteger(up.Member("pid")))
            {
                registeredPid = up.Member("pid")!.Value;
            }
        }

        Ah.Core.Time.PhaseTrace.End("whoami.read-roster", readRosterStart);
        var lastBrief = JsonValue.Null;
        var peerStart = Ah.Core.Time.PhaseTrace.Begin();
        var briefRow = mySessionId.IsTruthy && mySessionId.IsText ? hierarchy.Core.LastBriefRecord(mySessionId.AsString()) : null;
        Ah.Core.Time.PhaseTrace.End("whoami.peer-records", peerStart);
        if (briefRow is { } brief)
        {
            lastBrief = Obj(
                ("from", RosterJson.OrNull(brief.Member("from"))),
                ("from_name", RosterJson.Truthy(brief.Member("from_name"))),
                ("reply_to", RosterJson.OrNull(brief.Member("reply_to"))),
                ("ts", RosterJson.OrNull(brief.Member("ts"))));
        }

        var locateStart = Ah.Core.Time.PhaseTrace.Begin();
        var found = c.Services.Roster.LocateSession(dir, c.Homes, c.TeamArg, c.TeamDefaultExplicit, paneId);
        Ah.Core.Time.PhaseTrace.End("whoami.locate-session", locateStart);
        var envInvalid = found.Get("envInvalid") is { IsMap: true } inv ? inv.AsObject() : null;
        var reason = found.Get("reason") ?? JsonValue.Null;

        JsonObject Empty(JsonValue why)
        {
            var o = Obj(
                ("member", JsonValue.Null),
                ("team", JsonValue.Null),
                ("team_file", JsonValue.Null),
                ("orchestrator", JsonValue.Null),
                ("registered_pid", registeredPid),
                ("last_observed_brief", lastBrief),
                ("reason", why),
                ("answered_by", JsonValue.Null));
            RosterJson.Spread(o, envInvalid);
            return o;
        }

        if (found.Get("answeredBy") is { IsTruthy: true } answeredBy)
        {
            var describeStart = Ah.Core.Time.PhaseTrace.Begin();
            var o = Describe(c, found, answeredBy, registeredPid, lastBrief, envInvalid);
            Ah.Core.Time.PhaseTrace.End("whoami.describe", describeStart);
            if (reason.IsTruthy)
            {
                o["reason"] = reason;
            }

            c.Out(o);
        }
        else if (found.Get("candidates") is { IsTruthy: true } candidates)
        {
            var o = Empty(reason);
            o["candidates"] = candidates;
            c.Out(o);
        }
        else
        {
            c.Out(Empty(reason));
        }

        return 0;
    }

    private static JsonObject Describe(
        RosterContext c, JsonObject found, JsonValue answeredBy, JsonValue registeredPid, JsonValue lastBrief, JsonObject? envInvalid)
    {
        var member = found.Get("member") ?? JsonValue.Null;
        var team = found.Get("team") ?? JsonValue.Null;
        var teamName = found.Get("teamName") ?? JsonValue.Null;
        var home = found.Get("home") is { IsText: true } h ? h.AsString() : c.Dir;

        JsonValue orchestratorOut = JsonValue.Null;
        if (RosterJson.Member(team, "orchestrator") is { IsMap: true } orch)
        {
            var pidValue = orch.Member("pid");
            var hasPid = RosterJson.IsInteger(pidValue);
            var pid = hasPid ? pidValue!.Value.AsNumber() : 0;
            JsonValue live = JsonValue.Null;
            JsonValue sendTo = JsonValue.Null;
            if (hasPid)
            {
                var recordedStart = orch.Member("pid_start") is { IsText: true } start && start.AsString().Length > 0 ? start.AsString() : null;
                var reused = c.Services.Hierarchy.Core.Liveness?.PidReused((int)pid, recordedStart) ?? false;
                var alive = c.PidAlive(pid) && !reused;
                live = JsonValue.FromBool(alive);
                var ownSocket = c.EnvText("CLAUDE_CODE_MESSAGING_SOCKET");
                var socketDir = ownSocket is null ? "/tmp/cc-socks" : Path.GetDirectoryName(ownSocket) ?? string.Empty;
                var socket = Path.Combine(socketDir, $"{(long)pid}.sock");
                if (alive && c.Services.Files.Exists(socket))
                {
                    sendTo = JsonValue.FromString($"uds:{socket}");
                }
            }

            orchestratorOut = Obj(
                ("pid", hasPid ? pidValue!.Value : JsonValue.Null),
                ("session_id", RosterJson.OrNull(orch.Member("session_id"))),
                ("live", live),
                ("send_to", sendTo));
        }

        JsonValue memberOut = JsonValue.Null;
        if (member.IsTruthy)
        {
            memberOut = Obj(
                ("name", RosterJson.OrNull(member.Member("name"))),
                ("role", RosterJson.OrNull(member.Member("role"))),
                ("route", RosterJson.OrNull(member.Member("route"))));
        }

        var teamNameText = teamName.IsText ? teamName.AsString() : null;
        var result = Obj(
            ("member", memberOut),
            ("team", teamName),
            ("team_file", RosterJson.Text(c.Services.Roster.TeamPath(home, teamNameText))),
            ("orchestrator", orchestratorOut),
            ("registered_pid", registeredPid),
            ("last_observed_brief", lastBrief),
            ("reason", JsonValue.Null),
            ("answered_by", answeredBy),
            ("work_tree", member.IsTruthy && member.Member("name") is { IsText: true } memberName
                ? GateReport.WorkTree(c, home, teamNameText, memberName.AsString())
                : JsonValue.Null));
        if (teamNameText is not null && member.Member("name") is { IsText: true } treeMember
            && c.Services.Hierarchy.Roster.TreeFactsOfTeam(home, teamNameText).FirstOrDefault(f => f.Name == treeMember.AsString()) is { } tree)
        {
            result["tree"] = HierarchyModule.TreeJson(tree);
            if (HierarchyModule.TreeWarning(tree) is { } warning)
            {
                c.Stderr.WriteLine($"ah: {warning}");
            }
        }

        if (Ah.Roster.RosterModule.DutyOf(team) is { } duty)
        {
            result["duty"] = duty;
        }

        if (c.Opts.Keys.Contains("json"))
        {
            result["authority"] = Authority(c, teamNameText, RosterJson.Member(team, "orchestrator")?.Member("session_id") is { IsText: true } orchSession ? orchSession.AsString() : null);
        }

        RosterJson.Spread(result, envInvalid);
        return result;
    }

    /// <summary>
    /// The run's grant, read live from the team's single open run anchor. No open anchor, several of them, or a line that is
    /// neither value all report <c>none</c>: the member then asks as it would without a grant.
    /// </summary>
    private static JsonObject Authority(RosterContext c, string? team, string? orchestratorSession)
    {
        var read = HierarchyModule.ReadAuthority(c.Services.Hierarchy, c.Services.Files, c.Dir, team, orchestratorSession);
        return Obj(("mode", read.Mode), ("run", read.Run), ("anchor", read.Anchor));
    }

    /// <summary>The newest row of the latest-roster list whose pid is <paramref name="pid"/> (<c>find</c>: the first match).</summary>
    private static JsonValue LatestRowOfPid(JsonArray rows, double pid)
    {
        foreach (var row in rows)
        {
            if (row.Member("pid") is { IsNumber: true } p && p.AsNumber() == pid)
            {
                return row;
            }
        }

        return JsonValue.Null;
    }

    internal static JsonObject Obj(params (string Key, JsonValue Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            o[key] = value;
        }

        return o;
    }
}
