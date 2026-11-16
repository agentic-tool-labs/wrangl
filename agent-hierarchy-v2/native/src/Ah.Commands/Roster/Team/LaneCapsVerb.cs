using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>lane-caps</c>: the top Orchestrator sets the member and open-stream caps of one lane in the managing team's file. It is not a
/// carved verb, so a lane leader cannot raise its own cap. Lowering a cap below the lane's current count dismisses no one; the next
/// spawn is what the cap refuses.
/// </summary>
internal static class LaneCapsVerb
{
    private static readonly string[] Flags = ["stream", "members", "streams", "max-lanes", "team", "cwd", "orchestrator-pid"];

    [Verb("roster:lane-caps")]
    internal static int Set(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        StreamSupport.RejectUnknownFlags(c, Flags, "lane-caps");
        if (c.Opts.Has("max-lanes"))
        {
            return SetMaxLanes(c);
        }

        if (!c.Opts.Has("stream") && !c.Opts.Has("members") && !c.Opts.Has("streams"))
        {
            return Show(c);
        }

        var ws = c.Opts.Text("stream") ?? throw new RosterFailure("lane-caps: name the lane's stream with --stream <ws>");
        if (!c.Opts.Has("members") && !c.Opts.Has("streams"))
        {
            throw new RosterFailure("lane-caps: give --members, --streams or both; nothing was changed");
        }

        var manager = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (manager.IsNull || manager.Member("streams")?.Member(ws) is not { IsMap: true })
        {
            throw new RosterFailure($"lane-caps: no stream {ws} in this team; nothing was changed");
        }

        var current = RosterModule.LaneCaps(manager, ws);
        var members = SubOrchestratorSpawn.CapOf(c, "lane-caps", "members", c.Opts.Text("members"), current.Members);
        var streams = SubOrchestratorSpawn.CapOf(c, "lane-caps", "streams", c.Opts.Text("streams"), current.Streams);
        var managerName = Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(c.Dir, c.TeamFile));
        var result = c.Services.Hierarchy.StateCalls.UpdateTeam(
            c.Dir,
            managerName,
            doc =>
            {
                if (!doc.IsMap || doc.Member("streams")?.Member(ws) is not { IsMap: true } record)
                {
                    return null;
                }

                var next = doc.AsObject().Clone();
                var streamRecords = next.Get("streams")!.Value.AsObject().Clone();
                var updated = record.AsObject().Clone();
                var lane = updated.Get("lane") is { IsMap: true } existing ? existing.AsObject().Clone() : new JsonObject();
                lane["max_members"] = members;
                lane["max_streams"] = streams;
                updated["lane"] = lane;
                streamRecords[ws] = updated;
                next["streams"] = streamRecords;
                return TeamFileOutcome.Write(next);
            });
        if (!result.Changed && (members, streams) != current)
        {
            throw new RosterFailure("lane-caps: the team file changed under lane-caps; retry; nothing was written.");
        }

        c.Out(new JsonObject { ["lane_caps"] = true, ["stream"] = ws, ["members"] = members, ["streams"] = streams });
        return 0;
    }

    private static int Show(RosterContext c)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.IsNull)
        {
            throw new RosterFailure("lane-caps: no team record here; nothing was changed");
        }

        var (maxLanes, source, _, _) = EffectiveLaneCap.Of(c, team);
        var lanes = new JsonObject();
        if (team.Member("streams") is { IsMap: true } streams)
        {
            foreach (var ws in streams.AsObject().Keys.Order(StringComparer.Ordinal))
            {
                var (members, open) = RosterModule.LaneCaps(team, ws);
                lanes[ws] = new JsonObject { ["members"] = members, ["streams"] = open };
            }
        }

        c.Out(new JsonObject { ["lane_caps"] = true, ["max_lanes"] = maxLanes, ["max_lanes_default"] = source == "default", ["max_lanes_source"] = source, ["lanes"] = lanes });
        return 0;
    }

    private static int SetMaxLanes(RosterContext c)
    {
        if (c.Opts.Has("stream"))
        {
            const string Team = "ah: --max-lanes is team-wide; drop --stream";
            throw c.Refuse(new JsonObject { ["refused"] = "max-lanes-is-team-wide", ["message"] = Team }, Team);
        }

        if (c.Opts.Has("members") || c.Opts.Has("streams"))
        {
            throw new RosterFailure("lane-caps: --max-lanes is team-wide; set --members/--streams in a separate lane-caps --stream <ws> call");
        }

        if (c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile).IsNull)
        {
            throw new RosterFailure("lane-caps: no team record here; nothing was changed");
        }

        if (!int.TryParse(c.Opts.Text("max-lanes"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maxLanes)
            || maxLanes < RosterModule.LaneCapMin || maxLanes > RosterModule.LaneCapMax)
        {
            var range = $"ah: --max-lanes must be an integer from {RosterModule.LaneCapMin} to {RosterModule.LaneCapMax}";
            throw c.Refuse(new JsonObject { ["refused"] = "max-lanes-range", ["message"] = range }, range);
        }

        var managerName = Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(c.Dir, c.TeamFile));
        var result = c.Services.Hierarchy.StateCalls.UpdateTeam(
            c.Dir,
            managerName,
            doc =>
            {
                if (!doc.IsMap)
                {
                    return null;
                }

                var next = doc.AsObject().Clone();
                next["max_lanes"] = maxLanes;
                return TeamFileOutcome.Write(next);
            });
        if (!result.Changed && RosterModule.MaxLanes(c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile)).Value != maxLanes)
        {
            throw new RosterFailure("lane-caps: the team file changed under lane-caps; retry; nothing was written.");
        }

        c.Out(new JsonObject { ["lane_caps"] = true, ["max_lanes"] = maxLanes });
        return 0;
    }
}
