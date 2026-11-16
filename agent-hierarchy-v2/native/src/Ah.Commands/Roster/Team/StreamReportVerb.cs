using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>stream-report</c>: the one status record of a sub-orchestrator's workstream, kept in its team file. It runs in a session that
/// owns a team with a parent, and writes nothing else.
/// </summary>
internal static class StreamReportVerb
{
    private const int SummaryLimit = 160;

    private static readonly string[] Flags = ["state", "summary", "note", "result", "team", "cwd"];

    private static readonly string[] States = ["working", "blocked", "review", "paused", "done"];

    [Verb("roster:stream-report")]
    internal static int Report(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        StreamSupport.RejectUnknownFlags(c, Flags, "stream-report");
        var state = c.Opts.Text("state");
        if (state is null || !States.Contains(state))
        {
            throw new RosterFailure($"stream-report: --state must be one of {string.Join(", ", States)}");
        }

        var summary = c.Opts.Text("summary") ?? throw new RosterFailure("stream-report: --summary \"<one line>\" is required");
        CheckLine(summary, "summary");
        var note = c.Opts.Text("note");
        if (state == "paused" && string.IsNullOrEmpty(note))
        {
            throw new RosterFailure("stream-report: --state paused needs --note \"<why>\" (paused-needs-note); nothing was changed");
        }

        if (note is not null)
        {
            CheckLine(note, "note");
        }

        var result = c.Opts.Text("result");
        if (c.Opts.Has("result"))
        {
            CheckResult(c, state, result);
        }

        var roster = c.Services.Roster;
        var dir = c.Dir;
        if (c.Proved().Carve is { Disagreement: null } carve)
        {
            c.TeamFile = ChildOf(c, carve);
        }

        var team = roster.ReadTeamRecord(dir, c.TeamFile);
        var name = c.TeamFile ?? "the default team";
        if (team.IsNull || team.Member("parent") is not { IsTruthy: true })
        {
            c.TeamFile = LaneChild(c, team, name);
            team = roster.ReadTeamRecord(dir, c.TeamFile);
            name = c.TeamFile!;
        }

        var manager = ManagerOwner(c, team);
        string? by = null;
        if (roster.OwnedTeams(dir, c.Invoker()).Contains(c.TeamFile))
        {
            by = c.Proved().Carve is { Disagreement: null } so ? so.Member : HierarchyModule.OrchestratorAddress(name);
        }
        else if (manager is not null && (ManagerRecovers(c, team) || state == "paused" || team.Member("workstream")?.Member("state") is { IsText: true } held && held.AsString() == "paused"))
        {
            by = HierarchyModule.OrchestratorAddress(manager);
        }
        else if (manager is not null)
        {
            throw new RosterFailure(
                $"stream-report: this session does not own {name}; it owns the manager team {manager}, which may report here only with --state paused --note \"<why>\", to leave paused, or once the lane's sub-orchestrator process is gone; other states are for the sub-orchestrator; nothing was changed");
        }
        else
        {
            throw new RosterFailure($"stream-report: this session does not own {name}; nothing was changed");
        }

        var now = c.Services.Hierarchy.Messages.LocalIso();
        var workstream = new JsonObject { ["state"] = state, ["summary"] = summary, ["updated"] = now };
        if (note is not null)
        {
            workstream["note"] = note;
        }

        if (state == "paused")
        {
            workstream["paused_by"] = by;
        }

        if (result is not null)
        {
            workstream["result"] = result;
        }

        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                if (!doc.IsMap)
                {
                    throw new RosterFailure($"stream-report: the team file of {name} is gone or unreadable; nothing was changed");
                }

                var next = doc.AsObject().Clone();
                next["workstream"] = workstream;
                return TeamFileOutcome.Write(next);
            });
        c.Out(new JsonObject { ["team"] = name, ["workstream"] = workstream });
        return 0;
    }

    /// <summary>
    /// The child team of the lane named beside a team that has no parent, or of its one open lane when neither a lane nor <c>--team</c>
    /// was given; otherwise the refusal names the <c>--team</c> of each open lane.
    /// </summary>
    private static string LaneChild(RosterContext c, JsonValue team, string name)
    {
        var streams = !team.IsNull && team.Member("streams") is { IsMap: true } map ? map.AsObject() : new JsonObject();
        var lanes = streams.Keys.Select(ws => (Stream: ws, Child: c.Services.Roster.FindChildTeam(c.Dir, name, ws))).Where(l => l.Child is not null).ToList();
        var named = c.Opts.Positionals.Count > 0;
        if (named && lanes.FirstOrDefault(l => l.Stream == c.Opts.Positionals[0]).Child is { } child)
        {
            return child;
        }

        var open = lanes.Where(l => MemberPlanning.Text(streams[l.Stream].Member("state")) != "done").ToList();
        if (open.Count == 1 && !named && c.TeamArg is null && !c.TeamDefaultExplicit)
        {
            return open[0].Child!;
        }

        var next = open.Count == 0 ? "it has no open lane to report on" : $"a lane reports on its child team: {string.Join(", ", open.Select(l => $"--team {l.Child} (lane {l.Stream})"))}";
        throw new RosterFailure($"stream-report: {name} is not a sub-orchestrator's team (it has no parent); {next}; nothing was changed");
    }

    /// <summary>The child team of the stream the proved sub-orchestrator runs; a <c>--team</c> naming any other team is refused.</summary>
    private static string ChildOf(RosterContext c, SoCarve carve)
    {
        var manager = SubOrchestratorCarve.ReadTeam(c, carve.Team);
        var all = manager.Member("streams") is { IsMap: true } streams ? streams.AsObject() : new JsonObject();
        var ws = all.Keys.FirstOrDefault(k => all[k].Member("sub_orchestrator") is { IsText: true } so && so.AsString() == carve.Member);
        var child = ws is null ? null : c.Services.Roster.FindChildTeam(c.Dir, carve.Team, ws);
        if (child is null || (c.TeamArg is not null && c.TeamFile != child))
        {
            throw new OwnerRefusal(
                "not-child-team",
                "member-marker",
                "ah roster stream-report: a sub-orchestrator reports only on its own child team; nothing was changed.",
                "owner proof: the named team is not the child team of this sub-orchestrator's stream.");
        }

        return child;
    }

    /// <summary>The managing team's owner reporting for a sub-orchestrator whose process is gone.</summary>
    private static bool ManagerRecovers(RosterContext c, JsonValue child)
    {
        var owner = child.Member("orchestrator");
        var pid = RosterJson.Member(owner, "pid");
        var start = RosterJson.Member(owner, "pid_start") is { IsText: true } text ? text.AsString() : null;
        return pid is { IsNumber: true } held && (c.Services.Callers?.OwnerProcessIsGone(held.AsNumber(), start) ?? false);
    }

    /// <summary>The name of the managing team of <paramref name="child"/> when the caller owns it, else null.</summary>
    private static string? ManagerOwner(RosterContext c, JsonValue child)
    {
        if (!c.Proved().Owns || child.Member("parent")?.Member("team") is not { IsText: true } managerName
            || c.Services.Roster.ReadTeamRecord(c.Dir, managerName.AsString()) is not { IsMap: true } manager
            || !c.Services.Roster.IsTeamOwnedBy(manager, c.Invoker()))
        {
            return null;
        }

        return managerName.AsString();
    }

    /// <summary>A one-line text of at most <see cref="SummaryLimit"/> characters with no backtick.</summary>
    private static void CheckLine(string text, string flag)
    {
        if (text.Length > SummaryLimit)
        {
            throw new RosterFailure($"stream-report: --{flag} is {text.Length} characters; the limit is {SummaryLimit}");
        }

        if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
        {
            throw new RosterFailure($"stream-report: --{flag} must be one line");
        }

        if (text.Contains('`', StringComparison.Ordinal))
        {
            throw new RosterFailure($"stream-report: --{flag} must not contain a backtick");
        }
    }

    /// <summary>A result is only reported with <c>done</c>, and names an existing response file by absolute path.</summary>
    private static void CheckResult(RosterContext c, string state, string? result)
    {
        if (state != "done")
        {
            throw new RosterFailure("stream-report: --result goes only with --state done");
        }

        if (string.IsNullOrEmpty(result) || !result.StartsWith('/'))
        {
            throw new RosterFailure("stream-report: --result must be an absolute path");
        }

        if (!result.EndsWith("--response.md", StringComparison.Ordinal))
        {
            throw new RosterFailure("stream-report: --result must be a response file (its name ends in --response.md)");
        }

        if (!c.Services.Hierarchy.Core.MsgExists(result))
        {
            throw new RosterFailure($"stream-report: --result {result} does not exist");
        }
    }
}
