using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>resume --all [--yes]</c>: one command after a reboot, for every top team this session owns. A quit hold resumes as
/// <c>resume --hold</c> does; a stop is listed, because its assessment must be read; an unheld team is recovered and its missing members
/// relaunched, with no brief. A team this session does not own is never touched, only listed: a months-old dead team must not spend tokens.
/// </summary>
internal static class ResumeAll
{
    internal static int Run(RosterContext c)
    {
        if (c.Opts.Has("team") || c.Opts.Has("relaunch") || c.Opts.IsTrue("relaunch-missing") || c.Opts.Positionals.Count > 0)
        {
            throw HoldStore.Refuse(c, "bad_flags", "resume --all cannot be combined with --team, --relaunch, --relaunch-missing or a path");
        }

        var proof = ResumeVerb.Prove(c);
        if (proof.Refusal is { } refusal)
        {
            return refusal;
        }

        var roster = c.Services.Roster;
        var teams = new JsonArray();
        var notOwned = new JsonArray();
        var exits = new List<int>();
        var bare = new JsonArray();
        foreach (var team in new string?[] { null }.Concat(roster.ListTeamNames(c.Dir)))
        {
            var record = roster.ReadTeamRecord(c.Dir, team);
            if (record.IsNull || record.Member("parent") is { IsMap: true })
            {
                continue;
            }

            if (!roster.IsTeamOwnedBy(record, proof.Invoker!))
            {
                notOwned.Add(NotOwned(c, team, record));
                continue;
            }

            var (output, exit) = OneTeam(c, team);
            teams.Add(output);
            exits.Add(exit);
            if (output.Get("bare_waiting") is { IsArray: true } waiting)
            {
                bare.AddRange(waiting.AsArray());
            }
        }

        var result = new JsonObject { ["verb"] = "resume", ["teams"] = teams, ["not_owned"] = notOwned, ["bare_waiting"] = bare };
        if (bare.Count > 0 && !MemberLaunchDoctor.RecoveryOn(c))
        {
            result["next"] = "ah roster recover enable";
            exits.Add(3);
        }

        c.Out(result);
        return exits.Contains(1) ? 1 : exits.Contains(3) ? 3 : 0;
    }

    private static JsonObject NotOwned(RosterContext c, string? team, JsonValue record)
    {
        var arg = HoldStore.TeamArg(team);
        return new JsonObject
        {
            ["team"] = team ?? "_default",
            ["life"] = c.Services.Roster.TeamLifeOf(record).ToString().ToLowerInvariant(),
            ["next"] = c.Services.Roster.HoldOf(c.Dir, team) is not null
                ? $"ah roster resume --hold --team {arg} --yes"
                : BehindWindDown(c, team)
                    ? $"ah roster resume --checkpoint --team {arg} --yes"
                    : $"ah roster resume --checkpoint --team {arg} --relaunch-missing --yes",
        };
    }

    /// <summary>Whether a team is behind a wind-down; a store that cannot say leaves the listing as it is, since this only names a command.</summary>
    private static bool BehindWindDown(RosterContext c, string? team)
    {
        var read = c.Services.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = c.Dir, ["team"] = team ?? "_default" });
        try
        {
            return CheckpointVerb.BehindWindDown(c, team, read.Member("found") is { IsTruthy: true } ? read.Member("record") : null);
        }
        catch (RosterFailure)
        {
            return false;
        }
    }

    private static (JsonObject Output, int Exit) OneTeam(RosterContext c, string? team)
    {
        var name = team ?? "_default";
        var trapped = c.TrapRefusals;
        c.TrapRefusals = true;
        try
        {
            return CheckpointVerb.WithTeam(c, team, () =>
            {
                var hold = c.Services.Roster.HoldOf(c.Dir, team);
                if (hold is null)
                {
                    return Unheld(c, team);
                }

                if (hold.Kind == "stop")
                {
                    return (new JsonObject { ["team"] = name, ["from"] = "stop", ["status"] = "stopped", ["next"] = $"ah roster resume --hold --team {HoldStore.TeamArg(team)}" }, 0);
                }

                var own = HoldStore.ReadOwn(c, team) ?? throw new RosterFailure($"team {name} has an unreadable hold file at {hold.Path}; fix or remove it by hand");
                return ResumeHold.ResumeFromQuit(c, team, own);
            });
        }
        catch (RosterFailure error)
        {
            return (new JsonObject { ["team"] = name, ["error"] = error.Message }, 1);
        }
        finally
        {
            c.TrapRefusals = trapped;
        }
    }

    /// <summary>An unheld team and its unheld lanes, parents first: a rerun after a killed resume finds its released teams here.</summary>
    private static (JsonObject Output, int Exit) Unheld(RosterContext c, string? team)
    {
        var yes = c.Opts.IsTrue("yes");
        var (output, exit) = FromRows(c, team, yes);
        var lanes = new JsonArray();
        var kept = new JsonArray();
        var bare = ResumeHold.BareWaiting(output);
        var recovered = new JsonArray();
        recovered.AddRange(output.Get("recovered") is { IsArray: true } own ? own.AsArray() : []);
        var exits = new List<int> { exit };
        foreach (var lane in QuitVerb.ParentsFirst(c, team).Skip(1).Where(t => !c.Services.Roster.ReadTeamRecord(c.Dir, t).IsNull))
        {
            if (c.Services.Roster.HoldOf(c.Dir, lane) is { } hold)
            {
                kept.Add($"lane {lane} keeps its own {hold.Kind}; resume it with ah roster resume --hold --team {lane}");
                continue;
            }

            var (laneOutput, laneExit) = CheckpointVerb.WithTeam(c, lane, () => FromRows(c, lane, yes));
            lanes.Add(laneOutput);
            exits.Add(laneExit);
            bare.AddRange(ResumeHold.BareWaiting(laneOutput));
        }

        output["from"] = "none";
        output["lanes"] = lanes;
        output["lanes_kept"] = kept;
        output["bare_waiting"] = bare;
        return (output, exits.Contains(1) ? 1 : exits.Contains(3) ? 3 : 0);
    }

    private static (JsonObject Output, int Exit) FromRows(RosterContext c, string? team, bool yes)
    {
        JsonObject? recovered = null;
        if (yes)
        {
            var (restamped, down) = TeamRecover.Restamp(c, c.Services.Roster.ReadTeamRecord(c.Dir, team));
            recovered = new JsonObject { ["team"] = team ?? "_default", ["restamped"] = ResumeHold.TeamNames(restamped), ["down"] = ResumeHold.TeamNames(down) };
        }

        var (_, output, mailFailed) = ResumeCheckpoint.ResumeFromRows(c, yes);
        if (recovered is not null)
        {
            output["recovered"] = new JsonArray { recovered };
        }

        return (output, ResumeCheckpoint.ExitCode(output, mailFailed));
    }
}
