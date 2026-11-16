using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

internal static class CheckinVerb
{
    private static readonly HashSet<string> Flags =
        new(StringComparer.Ordinal) { "cwd", "team", "orchestrator-pid" };

    /// <summary>
    /// Re-registers the calling session mid-session: compares where it runs with where its team expects it, appends a fresh
    /// roster row and records the session's mailbox registration. Exits 1 when the session is misplaced.
    /// </summary>
    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"checkin: unrecognized flag --{key} (use --team, --cwd, or --orchestrator-pid)");
            }
        }

        var dir = c.Dir;
        var services = c.Services;
        var roster = services.Roster;
        var pid = c.OrchestratorPid();
        if (!RosterContext.IsInteger(pid))
        {
            throw new RosterFailure("checkin: cannot resolve this session's pid — CLAUDE_PID is unset; pass --orchestrator-pid <pid>");
        }

        var sessionEnv = c.EnvText("CLAUDE_CODE_SESSION_ID");
        var herdrPane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(c.EnvText);
        var pidText = JsConvert.NumberToString(pid);
        JsonValue? existing = null;
        string? adoptedBy = null;

        var triedDirs = new List<string> { dir };
        if (c.MainDir is { Length: > 0 } mainDir && mainDir != dir)
        {
            triedDirs.Add(mainDir);
        }

        for (var i = 0; i < triedDirs.Count && existing is null; i++)
        {
            existing = RowForPid(services.Hierarchy.Roster.LatestRoster(triedDirs[i]), pid);
            adoptedBy = existing is null ? null : i == 0 ? "pid" : "pid-main";
        }

        if (existing is null && !c.TeamDefaultExplicit && services.Callers is { } owners)
        {
            var owned = owners.OwnedTeams(c.Cwd, sessionEnv, (int)pid);
            if (owned.Count > 0 && (c.TeamArg is null || owned.Contains(RosterModule.TeamArgName(c.TeamArg), StringComparer.Ordinal)))
            {
                existing = new JsonObject
                {
                    ["status"] = "up",
                    ["role"] = "orchestrator",
                    ["session_id"] = RosterJson.Text(sessionEnv),
                    ["pid"] = pid,
                    ["ppid"] = services.ParentPid,
                    ["cwd"] = services.Hierarchy.Messages.RealCwd(c.Cwd),
                    ["pane_id"] = RosterJson.Text(herdrPane),
                    ["tab_id"] = RosterJson.Text(c.EnvText("HERDR_TAB_ID")),
                    ["workspace_id"] = RosterJson.Text(c.EnvText("HERDR_WORKSPACE_ID")),
                };
                adoptedBy = "owner";
            }
        }

        if (existing is null && sessionEnv is not null)
        {
            existing = RowForDeadSession(c, triedDirs, sessionEnv);
            adoptedBy = existing is null ? null : "session";
        }

        if (existing is null && herdrPane is not null)
        {
            existing = RowForPane(c, dir, herdrPane);
            adoptedBy = existing is null ? null : "pane";
        }

        if (existing is null && sessionEnv is not null && c.EnvText(Ah.Mesh.MeshModule.MemberMarkerEnv) is null && c.EnvText("WRANGL_TEAM_FILE") is null)
        {
            // ah2-32: a session whose SessionStart never ran is not refused. It is the Orchestrator of its own implicit team (ah2-34),
            // which this creates when the folder's name is free, so the registration below has a mailbox to record.
            EnsureImplicitTeam(c, sessionEnv, pid);
            existing = new JsonObject { ["status"] = "up", ["role"] = "orchestrator", ["session_id"] = sessionEnv };
            adoptedBy = "late";
        }

        if (existing is not { } record)
        {
            var sid = sessionEnv is null ? "unset" : sessionEnv[..Math.Min(8, sessionEnv.Length)];
            throw new RosterFailure(
                $"checkin: no existing roster record for pid {pidText} — SessionStart must run before checkin; " +
                $"tried pid in {string.Join(", ", triedDirs)}, session {sid}, pane {herdrPane ?? "unset"}");
        }

        var role = record.Member("role") is { IsText: true } r ? r.AsString() : null;
        var paneId = c.SessionPaneId(record);

        JsonValue resolved;
        if (c.TeamDefaultExplicit)
        {
            var defaultTeam = roster.ReadTeamRecord(dir, null);
            resolved = defaultTeam.IsNull
                ? JsonValue.Null
                : JsonValue.FromObject(Pair(("teamName", JsonValue.Null), ("team", defaultTeam)));
        }
        else
        {
            resolved = roster.AttributeSessionTeam(dir, role, c.TeamArg, paneId, c.Homes);
        }

        if ((c.TeamArg is not null || c.TeamDefaultExplicit) && resolved.IsNull)
        {
            throw new RosterFailure($"checkin: no such team \"{RosterModule.TeamArgName(c.TeamArg)}\"");
        }

        var team = resolved.Member("team") ?? JsonValue.Null;
        var teamName = resolved.IsNull ? JsonValue.Null : resolved.Member("teamName") ?? JsonValue.Null;
        var expectedRoot = roster.ExpectedRootFor(team, roster.SessionMemberRow(team, paneId));
        var observed = services.Hierarchy.Messages.RealCwd(c.Cwd);
        var expectedText = expectedRoot.IsText ? expectedRoot.AsString() : null;
        var misplaced = !string.IsNullOrEmpty(expectedText) && !services.Config.IsPlaced(observed, expectedText);

        var rec = new JsonObject
        {
            ["status"] = "up",
            ["role"] = record.Member("role") ?? JsonValue.Null,
            ["session_id"] = RosterJson.Truthy(record.Member("session_id")),
            ["pid"] = pid,
            ["ppid"] = services.ParentPid,
            ["cwd"] = observed,
            ["pane_id"] = FirstText(Ah.Core.Mux.HerdrFlavour.SessionPaneId(c.EnvText), record.Member("pane_id")),
            ["tab_id"] = FirstText(c.EnvText("HERDR_TAB_ID"), record.Member("tab_id")),
            ["workspace_id"] = FirstText(c.EnvText("HERDR_WORKSPACE_ID"), record.Member("workspace_id")),
        };
        if (OrchestratorSession.StartOf(c, pid) is { } pidStart)
        {
            rec["pid_start"] = pidStart;
        }

        if (!resolved.IsNull)
        {
            rec["team"] = teamName;
        }

        Ah.Hierarchy.TreeFacts? tree = null;
        if (!string.IsNullOrEmpty(expectedText))
        {
            rec["expected_root"] = expectedText;
            rec["misplaced"] = misplaced;
            if (teamName.IsText && roster.SessionMemberRow(team, paneId).Member("name") is { IsText: true } treeMember)
            {
                tree = services.Hierarchy.Roster.TreeFactsOfTeam(dir, teamName.AsString()).FirstOrDefault(f => f.Name == treeMember.AsString());
                if (tree is not null)
                {
                    var block = Ah.Hierarchy.HierarchyModule.TreeJson(tree);
                    rec["co_tenants"] = block["co_tenants"];
                    rec["cross_stream"] = block["cross_stream"];
                }
            }
        }

        if (services.Callers is not { } callers)
        {
            c.Stderr.WriteLine(
                "ah: roster checkin is not implemented natively: the mailbox caller registration (the mesh client's RegisterCaller) is not provided");
            return 70;
        }

        services.Hierarchy.StateCalls.AppendRosterRecord(dir, rec);
        callers.Register(
            c.Cwd,
            rec["session_id"].IsText ? rec["session_id"].AsString() : null,
            (int)pid,
            paneId,
            c.TeamArg,
            c.TeamDefaultExplicit);

        var result = new JsonObject
        {
            ["checked_in"] = true,
            ["pid"] = pid,
            ["cwd"] = observed,
            ["expected_root"] = expectedRoot.IsNull ? JsonValue.Null : expectedRoot,
            ["misplaced"] = misplaced,
            ["adopted_by"] = RosterJson.Text(adoptedBy),
        };
        if (!resolved.IsNull)
        {
            result["team"] = teamName;
        }

        if (tree is not null)
        {
            result["co_tenants"] = rec["co_tenants"];
            result["cross_stream"] = rec["cross_stream"];
            if (Ah.Hierarchy.HierarchyModule.TreeWarning(tree) is { } warning)
            {
                c.Stderr.WriteLine($"ah: {warning}");
            }
        }

        // Only the team's owner re-stamps a member row, so a checkin cannot fix a row naming another process, and until then this session's mail is refused.
        var memberRow = roster.SessionMemberRow(team, paneId);
        var rowHeldElsewhere = memberRow.Member("pid") is { IsNumber: true } && !Ah.Mesh.MeshModule.IsRowProcess(memberRow.AsObject(), rec);
        if (rowHeldElsewhere)
        {
            var heldPid = memberRow.Member("pid")!.Value.AsNumber();
            var remedy = Ah.Mesh.MeshModule.HeldRowRemedy(memberRow.AsObject(), !TeamRecover.RecordedProcessIsGone(c, memberRow, heldPid), teamName.IsText ? teamName.AsString() : "@default", memberRow.Member("name") is { IsText: true } rowName ? rowName.AsString() : "<member>");
            result["row_held_elsewhere"] = true;
            result["next"] = remedy;
            c.Stderr.WriteLine($"ah: this session is checked in, but its team row still names another process, so its mail is refused; {remedy}");
        }

        c.Out(result);
        return misplaced || rowHeldElsewhere ? 1 : 0;
    }

    /// <summary>The implicit team of ah2-34, written from the CLI: nothing when the name is no team name, is taken, or the process start cannot be read.</summary>
    private static void EnsureImplicitTeam(RosterContext c, string sessionId, double pid)
    {
        var services = c.Services;
        var name = services.Config.TeamPrefix(c.Cwd, null);
        var home = services.Config.MainHierarchyDir(c.Cwd) is { Length: > 0 } main ? main : c.Dir;
        if (name.Length == 0
            || OrchestratorSession.StartOf(c, pid) is not { } start
            || services.Config.ValidateTeamAlias(name).Get("ok") is not { IsBool: true } ok
            || !ok.AsBool()
            || !services.Roster.ReadTeamRecord(home, name).IsNull)
        {
            return;
        }

        var messages = services.Hierarchy.Messages;
        var team = new JsonObject
        {
            ["version"] = 1,
            ["team_id"] = services.Hierarchy.Core.NewId(),
            ["created"] = messages.LocalIso(),
            ["roster_level"] = JsonValue.Null,
            ["transport"] = JsonValue.Null,
            ["orchestrator"] = new JsonObject { ["session_id"] = sessionId, ["pid"] = pid, ["pid_start"] = start },
            ["members"] = new JsonArray(),
            ["expected_root"] = messages.RealCwd(c.Cwd),
            ["roster"] = JsonValue.Null,
            ["layout"] = "auto",
            ["implicit"] = true,
        };
        _ = services.Hierarchy.StateCalls.UpdateTeam(home, name, doc => doc.IsMap ? null : Ah.Hierarchy.StateCalls.TeamFileOutcome.Write(team));
    }

    private static JsonValue? RowForPid(JsonArray rows, double pid)
    {
        foreach (var row in rows)
        {
            if (row.Member("pid") is { IsNumber: true } p && p.AsNumber() == pid)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// The latest row of this session id whose process is gone. Roster rows record no process start time, so a row whose pid is
    /// alive (a concurrent fork of the conversation, or a reused pid) is never adopted.
    /// </summary>
    private static JsonValue? RowForDeadSession(RosterContext c, IEnumerable<string> dirs, string sessionId)
    {
        JsonValue? found = null;
        foreach (var home in dirs)
        {
            foreach (var row in c.Services.Hierarchy.Roster.LatestRoster(home))
            {
                if (row.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                    && row.Member("pid") is { IsNumber: true } p && !c.PidAlive(p.AsNumber()))
                {
                    found = row;
                }
            }
        }

        return found;
    }

    /// <summary>A row synthesized from the team member holding this herdr pane; <c>AH_MEMBER_NAME</c>, when set, must name that member.</summary>
    private static JsonValue? RowForPane(RosterContext c, string dir, string paneId)
    {
        var located = c.Services.Roster.LocateSession(dir, c.Homes, c.TeamArg, c.TeamDefaultExplicit, paneId);
        if (located.Get("member") is not { IsMap: true } member)
        {
            return null;
        }

        var name = member.Member("name") is { IsText: true } n ? n.AsString() : null;
        if (c.EnvText(Ah.Mesh.MeshModule.MemberMarkerEnv) is { } claimed && claimed != name)
        {
            return null;
        }

        return new JsonObject
        {
            ["status"] = "up",
            ["role"] = member.Member("role") ?? JsonValue.Null,
            ["session_id"] = RosterJson.Text(c.EnvText("CLAUDE_CODE_SESSION_ID")),
        };
    }

    /// <summary><c>env || fallback || null</c> for two possibly-text values.</summary>
    private static JsonValue FirstText(string? env, JsonValue? fallback) =>
        env is not null ? JsonValue.FromString(env) : RosterJson.Truthy(fallback);

    private static JsonObject Pair(params (string Key, JsonValue Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            o[key] = value;
        }

        return o;
    }
}
