using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Roster;

namespace Ah.Commands;

internal static class TeamsVerb
{
    /// <summary>The scope of a listing that belongs to no team: herdr is the only registry a loose peer can be in.</summary>
    private static readonly TeamScope NoTeam = new(null, NoTeam: true);

    /// <summary>
    /// Read-only inventory of every team file in the hierarchy dir, so a stale or a sibling orchestrator's team is not invisible:
    /// each row with its misplaced members, its untracked live sessions and, for an orphan, what still depends on it.
    /// </summary>
    internal static int Run(RosterContext c)
    {
        if (c.Opts.IsTrue("worktrees"))
        {
            return Worktrees(c);
        }

        if (c.Services.Teardown is not { } teardown)
        {
            c.Stderr.WriteLine(
                "ah: roster teams is not implemented natively: the launch layer (Ah.Launch teardown over herdr and the mux runner) is not composed");
            return 70;
        }

        var dir = c.Dir;
        var roster = c.Services.Roster;
        var hierarchy = c.Services.Hierarchy;
        var context = c.LaunchContext();
        var invoker = c.Invoker();
        var span = Ah.Core.Time.PhaseTrace.Begin();
        var rows = AllTeamRows(c, dir, invoker);
        Ah.Core.Time.PhaseTrace.End("teams.team-rows", span);

        // A live "up" row whose pid is alive; an unclean exit leaves misplaced:true as the latest row forever otherwise.
        span = Ah.Core.Time.PhaseTrace.Begin();
        var liveUp = new List<JsonValue>();
        foreach (var record in hierarchy.Roster.LatestRoster(dir))
        {
            if (record.Member("status") is { IsText: true } status && status.AsString() == "up"
                && !hierarchy.Roster.RecordProvenDead(record))
            {
                liveUp.Add(record);
            }
        }

        Ah.Core.Time.PhaseTrace.End("teams.latest-roster", span);
        span = Ah.Core.Time.PhaseTrace.Begin();
        var byPane = roster.PaneResolver(dir);
        Ah.Core.Time.PhaseTrace.End("teams.pane-resolver", span);
        var livenessOf = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        // ah2-514 LB-3: no member row is in two team files (a lent member stays a row of its home team; a sub-orchestrator is its
        // child team's orchestrator, not a member row there), so the per-team counts add up to each session once
        var liveMembers = 0;

        foreach (var row in rows)
        {
            var name = row.Get("name") is { IsText: true } n ? n.AsString() : null;
            span = Ah.Core.Time.PhaseTrace.Begin();
            var team = roster.ReadTeamRecord(dir, name);
            var members = team.Member("members") is { IsArray: true } list ? list.AsArray() : new JsonArray();
            Ah.Core.Time.PhaseTrace.End("teams.team-file", span);

            // A team's agents are the members that are live or cannot be ruled out; a dead row is not an agent. member_rows keeps the file's row count.
            span = Ah.Core.Time.PhaseTrace.Begin();
            var agents = 0;
            foreach (var m in members)
            {
                if (m.IsMap && m.Member("name") is { IsText: true } && LivenessOnce(teardown, dir, name, m, livenessOf) is var liveness
                    && (liveness.Get("live") is { IsTruthy: true } || liveness.Get("indeterminate") is { IsTruthy: true }))
                {
                    agents++;
                }
            }

            Ah.Core.Time.PhaseTrace.End("teams.member-liveness", span);
            row["member_rows"] = row.Get("members") ?? JsonValue.FromNumber(members.Count);
            row["members"] = agents;
            liveMembers += agents;
            // for branch-sweep.sh to keep: every member row's recorded work tree, live or not (a held member has no process), and the
            // team's latest checkpoint's orchestrator and member trees (a held Orchestrator). A checkpoint that cannot be read leaves
            // the field out, and the sweep then removes nothing.
            if (MemberWorktrees(c, dir, name, members) is { } trees)
            {
                row["member_worktrees"] = trees;
            }

            row["parent"] = team.Member("parent") is { IsMap: true } link
                ? new JsonObject { ["team"] = RosterJson.OrNull(link.Member("team")), ["stream"] = RosterJson.OrNull(link.Member("stream")) }
                : JsonValue.Null;
            if (c.Opts.IsTrue("members"))
            {
                row["member_status"] = MemberStatus(c, dir, name, members);
            }

            span = Ah.Core.Time.PhaseTrace.Begin();
            var flagged = new JsonArray();
            var unattributed = 0;
            var seen = new HashSet<int>();

            for (var i = 0; i < liveUp.Count; i++)
            {
                var r = liveUp[i];
                if (r.Member("pane_id") is not { IsTruthy: true } pane || !pane.IsText)
                {
                    continue;
                }

                var match = byPane(pane.AsString());
                if (match is null || match.TeamName != name)
                {
                    continue;
                }

                seen.Add(i);
                var root = roster.ExpectedRootFor(team, match.Member);
                var rootText = root.IsText ? root.AsString() : null;
                var observedCwd = r.Member("cwd") ?? JsonValue.Null;
                if (!string.IsNullOrEmpty(rootText) && !c.Services.Config.IsPlaced(Real(hierarchy, observedCwd), rootText))
                {
                    flagged.Add(Flag(r, match.Member.Member("name"), observedCwd));
                }
            }

            for (var i = 0; i < liveUp.Count; i++)
            {
                var r = liveUp[i];
                if (r.Member("misplaced") is not { IsTruthy: true } || seen.Contains(i))
                {
                    continue;
                }

                var explicitTeam = r.IsMap && r.AsObject().ContainsKey("team");
                var rowTeam = explicitTeam ? r.Member("team") ?? JsonValue.Null : JsonValue.Null;
                var rowTeamName = rowTeam.IsText ? rowTeam.AsString() : null;
                if (rowTeamName != name)
                {
                    continue;
                }

                var roleMembers = new List<JsonValue>();
                foreach (var m in members)
                {
                    if (m.Member("role") is { } role && r.Member("role") is { } want && SameText(role, want)
                        && c.Services.Config.RouteHasPane(m.Member("route")))
                    {
                        roleMembers.Add(m);
                    }
                }

                if (explicitTeam && roleMembers.Count == 1)
                {
                    flagged.Add(Flag(r, roleMembers[0].Member("name"), r.Member("cwd") ?? JsonValue.Null));
                }
                else
                {
                    unattributed++;
                }
            }

            Ah.Core.Time.PhaseTrace.End("teams.misplaced", span);
            span = Ah.Core.Time.PhaseTrace.Begin();
            var tracked = TrackedOf(members);
            var untracked = teardown.UntrackedLive(dir, new TeamScope(name), tracked, context);
            Ah.Core.Time.PhaseTrace.End("teams.untracked-live", span);
            row["misplaced_members"] = flagged;
            row["misplaced_unattributed"] = unattributed;
            span = Ah.Core.Time.PhaseTrace.Begin();
            row["stale_stream_worktrees"] = StreamSupport.StaleStreamWorktrees(c, team);
            Ah.Core.Time.PhaseTrace.End("teams.stale-stream-worktrees", span);
            row["untracked_live"] = untracked;
            span = Ah.Core.Time.PhaseTrace.Begin();
            row["gate"] = GateReport.Gate(c, dir, name);
            Ah.Core.Time.PhaseTrace.End("teams.gate", span);
            span = Ah.Core.Time.PhaseTrace.Begin();
            row["idle_since"] = IdleSince(c, dir, members, name);
            Ah.Core.Time.PhaseTrace.End("teams.idle-since", span);
            if (row.Get("orphaned") is { IsTruthy: true } && name is not null)
            {
                span = Ah.Core.Time.PhaseTrace.Begin();
                RosterJson.Spread(row, OrphanDependents(c, teardown, dir, name, untracked, livenessOf));
                Ah.Core.Time.PhaseTrace.End("teams.orphan-dependents", span);
            }
        }

        var trackedAll = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var name = row.Get("name") is { IsText: true } n ? n.AsString() : null;
            if (roster.ReadTeamRecord(dir, name).Member("members") is { IsArray: true } list)
            {
                trackedAll.UnionWith(TrackedOf(list.AsArray()));
            }
        }

        span = Ah.Core.Time.PhaseTrace.Begin();
        var lent = RosterModule.LentMembers(roster, dir, role => c.Services.Config.RoleClass(role, c.Registry) == "orchestrate").Where(l => !l.Dangling).ToList();
        Ah.Core.Time.PhaseTrace.End("teams.lent-members", span);
        foreach (var row in rows)
        {
            var name = row.Get("name") is { IsText: true } n ? n.AsString() : null;
            var lentTo = new JsonArray();
            foreach (var l in lent.Where(l => l.Home == name))
            {
                lentTo.Add(new JsonObject { ["name"] = l.Name, ["lane"] = l.Lane });
            }

            var lentFrom = new JsonArray();
            foreach (var l in lent.Where(l => l.Lane == name))
            {
                lentFrom.Add(new JsonObject
                {
                    ["home"] = l.Home,
                    ["name"] = l.Name,
                    ["role"] = RosterJson.Text(l.Role),
                    ["live"] = LivenessOnce(teardown, dir, l.Home, l.Row, livenessOf).Get("live") is { IsTruthy: true },
                });
            }

            if (lentTo.Count > 0)
            {
                row["lent_to"] = lentTo;
            }

            if (lentFrom.Count > 0)
            {
                row["lent_from"] = lentFrom;
            }
        }

        var teams = new JsonArray();
        foreach (var row in rows)
        {
            teams.Add(row);
        }

        span = Ah.Core.Time.PhaseTrace.Begin();
        var result = new JsonObject
        {
            ["teams"] = teams,
            ["live_sessions"] = new JsonObject
            {
                ["members"] = liveMembers,
                ["harnesses"] = c.Services.Callers?.ProcessTable is { } table && Ah.Mesh.MeshModule.HarnessCount(table) is { } harnesses ? harnesses : JsonValue.Null,
            },
            ["untracked_live"] = teardown.UntrackedLive(dir, NoTeam, trackedAll, context),
            ["sources"] = teardown.SourcesField(dir, NoTeam, JsonValue.Null, context),
        };
        Ah.Core.Time.PhaseTrace.End("teams.untracked-and-sources", span);
        c.Out(result);
        return 0;
    }

    /// <summary>
    /// <c>--worktrees</c>, for branch-sweep.sh: this checkout's own hierarchy dir only (not the main checkout's), one row per team with
    /// <c>name</c>, <c>life</c> (<c>closed</c> or <c>open</c>) and <c>member_worktrees</c>. It reads no liveness, herdr or gate, so one
    /// call per linked tree stays cheap; it needs no launch layer.
    /// </summary>
    private static int Worktrees(RosterContext c)
    {
        var roster = c.Services.Roster;
        var teams = new JsonArray();
        // ListTeamNames merges in the team home (the main checkout's dir for a linked tree), and ReadTeamRecord prefers the home's
        // record of a name: in a linked tree, take only the records stored in c.Dir itself, read from there (the home answers its own call)
        var own = c.TeamHome != c.Dir;
        foreach (var name in roster.ListTeamNames(c.Dir).Distinct(StringComparer.Ordinal))
        {
            var ownPath = roster.TeamPathAt(c.Dir, name);
            var homeStored = own && !roster.TeamExistsAt(ownPath);

            // a home-stored team is the home's to report, unless it was checkpointed (quit) from this tree: the checkpoint is written
            // to this dir, so this call reports its trees and keeps the tree that hosts it
            if (homeStored && !CheckpointedHere(c, name))
            {
                continue;
            }

            var team = homeStored || !own ? roster.ReadTeamRecord(c.Dir, name) : roster.ReadTeamFileAt(ownPath);
            if (!team.IsMap)
            {
                continue;
            }

            var members = homeStored ? new JsonArray() : team.Member("members") is { IsArray: true } list ? list.AsArray() : new JsonArray();
            var row = new JsonObject { ["name"] = name, ["life"] = team.Member("closed") is { IsMap: true } ? "closed" : "open" };
            if (MemberWorktrees(c, c.Dir, name, members) is { } trees)
            {
                row["member_worktrees"] = trees;
            }

            teams.Add(row);
        }

        c.Out(new JsonObject { ["teams"] = teams });
        return 0;
    }

    private static bool CheckpointedHere(RosterContext c, string team)
    {
        try
        {
            return c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = c.Dir, ["team"] = team }).Member("found") is { IsTruthy: true };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true; // unreadable: report the row; MemberWorktrees then leaves out the field and the sweep fails closed
        }
    }

    private static JsonArray? MemberWorktrees(RosterContext c, string dir, string? team, JsonArray members)
    {
        var paths = members.Select(static m => MemberPlanning.Text(m.Member("worktree"))).ToList();
        try
        {
            var read = c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = dir, ["team"] = team ?? "_default" });
            if (read.Member("found") is { IsTruthy: true } && read.Member("record") is { IsMap: true } record)
            {
                paths.Add(MemberPlanning.Text(record.Member("orchestrator")?.Member("worktree")));
                if (record.Member("members") is { IsArray: true } saved)
                {
                    paths.AddRange(saved.AsArray().Select(static m => MemberPlanning.Text(m.Member("worktree"))));
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        var trees = new JsonArray();
        trees.AddRange(paths.Where(static t => t is { Length: > 0 }).Distinct(StringComparer.Ordinal).Select(static t => JsonValue.FromString(t!)));
        return trees;
    }

    /// <summary><c>--members</c>: each member's ids and the one verdict the stall diagnosis prints for it: <c>live</c>, <c>dead</c>, <c>unknown</c>, or what blocks a live member.</summary>
    private static JsonArray MemberStatus(RosterContext c, string dir, string? team, JsonArray members)
    {
        var wake = c.Services.Wake ?? throw new RosterFailure("teams --members: not available here");
        var result = new JsonArray();
        foreach (var m in members)
        {
            if (team is null || !m.IsMap || m.Member("name") is not { IsText: true } name)
            {
                continue;
            }

            var text = wake.MemberVerdict(dir, team, name.AsString());
            var split = text.IndexOf(" — ", StringComparison.Ordinal);
            var record = c.Services.Hierarchy.Roster.AttributedLivenessIn(dir, name, team).Get("rec");
            result.Add(new JsonObject
            {
                ["name"] = name,
                ["session_id"] = RosterJson.OrNull(record?.Member("session_id")),
                ["pane_id"] = RosterJson.OrNull(m.Member("transport_id")),
                ["verdict"] = split < 0 ? text : text[..split],
                ["why"] = split < 0 ? string.Empty : text[(split + 3)..],
            });
        }

        return result;
    }

    /// <summary>A member's liveness, asked of the teardown once per call: a member counted as an agent and listed as an orphan's dependent is probed a single time.</summary>
    private static JsonObject LivenessOnce(ITeardown teardown, string dir, string? team, JsonValue member, Dictionary<string, JsonObject>? seen)
    {
        if (seen is null)
        {
            return teardown.MemberLiveness(dir, member);
        }

        var key = (team ?? string.Empty) + "/" + member.Member("name")!.Value.AsString();
        if (!seen.TryGetValue(key, out var found))
        {
            found = teardown.MemberLiveness(dir, member);
            seen[key] = found;
        }

        return found;
    }

    /// <summary>
    /// Per member of a team record: the activity record's <c>state_since</c> while the member's current state is idle, else null
    /// (working, blocked, no record, an unreadable record or none to read). Read-only; the same record the watch view reads.
    /// </summary>
    private static JsonObject IdleSince(RosterContext c, string dir, JsonArray members, string? team)
    {
        var map = new JsonObject();
        JsonArray? attributed = null;
        foreach (var m in members)
        {
            if (m.Member("name") is not { IsText: true } name)
            {
                continue;
            }

            var since = JsonValue.Null;
            if (ReleaseGate.MemberActivity(c, dir, m, name.AsString(), () => attributed ??= c.Services.Hierarchy.Roster.AttributedRoster(dir), team) is { IsMap: true } record
                && record.Member("activity") is { IsText: true } activity && activity.AsString() == "idle"
                && !HierarchyModule.IdleOnBackgroundWork(record, c.Services.Clock.UtcNow.ToUnixTimeMilliseconds())
                && record.Member("state_since") is { IsText: true } stateSince)
            {
                since = stateSince;
            }

            map[name.AsString()] = since;
        }

        return map;
    }

    /// <summary>The user's team-size rule: live members past this, the Orchestrator not counted, earn a warning.</summary>
    internal const int TeamSizeRule = 4;

    internal static List<JsonObject> AllTeamRows(RosterContext c, string dir, TeamInvoker invoker)
    {
        var rows = new List<JsonObject>();
        List<string?> names = [null];
        names.AddRange(c.Services.Roster.ListTeamNames(dir));
        foreach (var name in names)
        {
            if (DescribeTeamRow(c, dir, name, invoker) is { } row)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>One team's row: the shape shared by <c>teams</c> and <c>reap</c>; null when there is no such team file.</summary>
    internal static JsonObject? DescribeTeamRow(RosterContext c, string dir, string? name, TeamInvoker invoker)
    {
        var roster = c.Services.Roster;
        var team = roster.ReadTeamRecord(dir, name);
        if (team.IsNull)
        {
            return null;
        }

        var pid = RosterJson.Member(team.Member("orchestrator"), "pid");
        var home = roster.TeamHome(dir);
        var legacy = home != dir && roster.TeamPath(dir, name).StartsWith(dir + "/", StringComparison.Ordinal);
        var split = roster.SplitTeamCopy(dir, name);

        var row = new JsonObject { ["name"] = RosterJson.Text(name) };
        if (legacy)
        {
            row["legacy_dir"] = dir;
        }

        var warnings = new JsonArray();
        if (!split.IsNull)
        {
            warnings.Add($"Team {name ?? "(default)"} has records in both {home} and {dir} (left by an older release). Both were read.");
        }

        if (name is not null)
        {
            foreach (var share in WorkTreeAssignment.SharedRoots(c, name, team).Where(s => s.Stream is null))
            {
                warnings.Add($"{share.Root} is shared by {string.Join(", ", share.Members)} in team {name}");
            }
        }

        // fewer-turns S5.1: warn only, never refuse (D6/D7 are the user's). members[] never holds the Orchestrator.
        var live = team.Member("members") is { IsArray: true } rows
            ? rows.AsArray().Count(m => c.Services.Hierarchy.Core.PidAlive(RosterJson.Member(m, "pid") ?? JsonValue.Null))
            : 0;
        if (live > TeamSizeRule)
        {
            warnings.Add($"team-size: {live} live members (rule: {TeamSizeRule}, +1 briefly)");
        }

        if (warnings.Count > 0)
        {
            row["warnings"] = warnings;
        }

        if (team.Member("team_id") is { } teamId)
        {
            row["team_id"] = teamId;
        }

        row["members"] = team.Member("members") is { IsArray: true } list ? list.AsArray().Count : 0;
        row["orchestrator_pid"] = RosterJson.OrNull(pid);
        row["pid_alive"] = c.Services.Hierarchy.Core.PidAlive(pid ?? JsonValue.Null);
        var life = roster.TeamLifeOf(team);
        row["orphaned"] = life == TeamLife.Orphaned;
        row["life"] = team.Member("closed") is { IsMap: true } ? "closed" : life switch
        {
            TeamLife.OwnedLive => "owned-live",
            TeamLife.Orphaned => "orphaned",
            _ => "indeterminate",
        };
        row["own"] = roster.IsTeamOwnedBy(team, invoker);
        if (team.Member("created") is { } created)
        {
            row["created"] = created;
        }

        return row;
    }

    /// <summary>
    /// What still depends on an orphaned team: <c>live_members</c> (its records that are live or cannot be ruled out) and
    /// <c>attributed_live</c> (live sessions that claim the team but match no record). Each is present only when non-empty.
    /// </summary>
    internal static JsonObject OrphanDependents(
        RosterContext c,
        ITeardown teardown,
        string dir,
        string teamName,
        JsonArray untracked,
        Dictionary<string, JsonObject>? livenessOf = null)
    {
        var hierarchy = c.Services.Hierarchy;
        var team = c.Services.Roster.ReadTeamRecord(dir, teamName);
        var members = new List<JsonValue>();
        if (team.Member("members") is { IsArray: true } list)
        {
            foreach (var m in list.AsArray())
            {
                if (m.IsMap && m.Member("name") is { IsText: true })
                {
                    members.Add(m);
                }
            }
        }

        var records = hierarchy.Roster.AttributedRoster(dir);
        var core = hierarchy.Core;
        var liveMembers = new JsonArray();
        foreach (var m in members)
        {
            var liveness = LivenessOnce(teardown, dir, teamName, m, livenessOf);
            var live = liveness.Get("live") is { IsTruthy: true };
            var indeterminate = liveness.Get("indeterminate") is { IsTruthy: true };
            if (!live && !indeterminate)
            {
                continue;
            }

            JsonValue sessionId = JsonValue.Null;
            foreach (var r in records)
            {
                if (r.Member("name") is { IsText: true } rn && m.Member("name") is { IsText: true } mn && rn.AsString() == mn.AsString())
                {
                    sessionId = RosterJson.OrNull(r.Member("session_id"));
                    break;
                }
            }

            var entry = new JsonObject
            {
                ["name"] = m.Member("name")!.Value,
                ["role"] = RosterJson.OrNull(m.Member("role")),
                ["last_brief_from"] = LastBriefFrom(core, sessionId),
            };
            if (indeterminate)
            {
                entry["indeterminate"] = true;
            }

            liveMembers.Add(entry);
        }

        var attributed = new JsonArray();
        foreach (var r in untracked)
        {
            attributed.Add(new JsonObject
            {
                ["name"] = r.Member("name") ?? JsonValue.Null,
                ["role"] = r.Member("role") ?? JsonValue.Null,
                ["pane_id"] = r.Member("pane_id") ?? JsonValue.Null,
                ["last_brief_from"] = LastBriefFrom(core, r.Member("session_id") ?? JsonValue.Null),
            });
        }

        var result = new JsonObject();
        if (liveMembers.Count > 0)
        {
            result["live_members"] = liveMembers;
        }

        if (attributed.Count > 0)
        {
            result["attributed_live"] = attributed;
        }

        return result;
    }

    private static JsonValue LastBriefFrom(IHierarchyCore core, JsonValue sessionId)
    {
        if (!sessionId.IsTruthy || !sessionId.IsText || core.LastBriefRecord(sessionId.AsString()) is not { } row)
        {
            return JsonValue.Null;
        }

        var fromName = RosterJson.Truthy(row.Member("from_name"));
        return fromName.IsNull ? RosterJson.Truthy(row.Member("from")) : fromName;
    }

    private static HashSet<string> TrackedOf(JsonArray members)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            if (m.Member("name") is { IsText: true } n && n.AsString().Length > 0)
            {
                names.Add(n.AsString());
            }
        }

        return names;
    }

    private static string Real(Ah.Hierarchy.HierarchyServices hierarchy, JsonValue cwd) =>
        cwd.IsText ? hierarchy.Messages.RealCwd(cwd.AsString()) : string.Empty;

    private static JsonObject Flag(JsonValue record, JsonValue? name, JsonValue observedCwd) => new()
    {
        ["role"] = record.Member("role") ?? JsonValue.Null,
        ["name"] = name ?? JsonValue.Null,
        ["observed_cwd"] = observedCwd,
    };

    private static bool SameText(JsonValue a, JsonValue b) => a.IsText && b.IsText && a.AsString() == b.AsString();
}
