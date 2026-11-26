using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>duty on &lt;member&gt;|off</c>: the team's owner puts a Deputy-Chief member on duty, or takes it off. Only <c>duty</c> is written;
/// the <c>orchestrator</c> record stays the chief's. While the deputy's row is live it owns the team too and holds
/// <c>orchestrator@T</c>, and the chief holds <c>chief@T</c>; both sessions' mailbox registrations are rewritten here so the boxes
/// move at once. A lane team has no deputy. <c>off</c> with no deputy on duty changes nothing.
/// </summary>
internal static class DutyVerb
{
    private static readonly string[] Flags = ["team", "cwd", "orchestrator-pid", "cause"];

    [Verb("roster:duty")]
    internal static int Duty(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        StreamSupport.RejectUnknownFlags(c, Flags, "duty");
        var args = c.Opts.Positionals;
        var on = args.Count == 2 && args[0] == "on";
        if (!on && !(args.Count == 1 && args[0] == "off"))
        {
            throw new RosterFailure("duty: usage: ah roster duty on <member> | ah roster duty off");
        }

        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.IsNull)
        {
            throw new RosterFailure("duty: no team record here; nothing was changed");
        }

        if (team.Member("parent") is { IsMap: true })
        {
            const string Lane = "ah roster duty: a lane team has no deputy; its lead answers to orchestrator@<manager>; nothing was changed";
            throw c.Refuse(new JsonObject { ["refused"] = "duty-lane-team", ["message"] = Lane }, Lane);
        }

        var teamName = Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(c.Dir, c.TeamFile));
        var previous = MemberPlanning.Text(team.Member("duty")?.Member("deputy"));
        if (!on)
        {
            if (previous is null)
            {
                c.Out(new JsonObject { ["duty"] = "off", ["changed"] = false });
                return 0;
            }

            Write(c, teamName, null);
            var offWarnings = Reregister(c, teamName, previous);
            c.Out(WithWarnings(new JsonObject { ["duty"] = "off", ["changed"] = true, ["deputy"] = previous }, offWarnings));
            return 0;
        }

        var name = args[1];
        var row = DismissVerb.Members(team).FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == name);
        if (!row.IsMap || MemberPlanning.Text(row.Member("role")) != Ah.Config.ConfigModule.DeputyChiefRole)
        {
            var notDeputy = $"ah roster duty: {name} is not a {Ah.Config.ConfigModule.DeputyChiefRole} member of {teamName}; nothing was changed";
            throw c.Refuse(new JsonObject { ["refused"] = "duty-not-deputy", ["message"] = notDeputy }, notDeputy);
        }

        var duty = new JsonObject
        {
            ["deputy"] = name,
            ["since"] = Ah.Core.Time.Timestamps.ToIsoString(c.Services.Clock.UtcNow),
            ["cause"] = c.Opts.Text("cause") ?? "duty on",
        };
        Write(c, teamName, duty);
        var warnings = Reregister(c, teamName, name);
        if (previous is not null && previous != name)
        {
            warnings.AddRange(Reregister(c, teamName, previous).Where(w => !warnings.Contains(w)));
        }

        c.Out(WithWarnings(new JsonObject { ["duty"] = "on", ["changed"] = true, ["deputy"] = name }, warnings));
        return 0;
    }

    private static JsonObject WithWarnings(JsonObject output, List<string> warnings)
    {
        if (warnings.Count > 0)
        {
            output["warnings"] = TeardownSupport.Strings(warnings);
        }

        return output;
    }

    /// <summary>Clears <c>duty</c> when <paramref name="member"/> is the deputy on it, before the member's row goes (dismiss). The chief's boxes move back.</summary>
    internal static void ClearFor(RosterContext c, string dir, string member, List<string> warnings)
    {
        if (MemberPlanning.Text(c.Services.Roster.ReadTeamRecord(dir, c.TeamFile).Member("duty")?.Member("deputy")) != member)
        {
            return;
        }

        var teamName = Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(dir, c.TeamFile));
        Write(c, teamName, null, dir);
        warnings.AddRange(Reregister(c, teamName, null, dir));
    }

    private static void Write(RosterContext c, string teamName, JsonObject? duty, string? dir = null)
    {
        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir ?? c.Dir,
            teamName,
            doc =>
            {
                if (!doc.IsMap)
                {
                    return null;
                }

                var next = doc.AsObject().Clone();
                if (duty is null)
                {
                    next.Remove("duty");
                }
                else
                {
                    next["duty"] = duty;
                }

                return TeamFileOutcome.Write(next);
            });
    }

    /// <summary>
    /// Rewrites the mailbox registrations of the chief and of <paramref name="deputy"/>'s row, so each holds the boxes the duty now
    /// gives it. The duty is already written; each holder whose registration was not rewritten comes back as a warning, since its
    /// boxes have not moved.
    /// </summary>
    private static List<string> Reregister(RosterContext c, string teamName, string? deputy, string? dir = null)
    {
        var warnings = new List<string>();
        if (c.Services.Callers is not { } callers)
        {
            return warnings;
        }

        var team = c.Services.Roster.ReadTeamRecord(dir ?? c.Dir, teamName);
        var holders = new List<(string Name, JsonValue Row)> { ("the chief", team.Member("orchestrator") ?? JsonValue.Null) };
        if (deputy is not null)
        {
            holders.Add((deputy, DismissVerb.Members(team).FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == deputy)));
        }

        foreach (var (name, holder) in holders)
        {
            if (MemberPlanning.Text(holder.Member("session_id")) is not { } session || holder.Member("pid") is not { IsNumber: true } pid)
            {
                warnings.Add($"duty: {name} has no recorded session and pid; its mailbox registration was not rewritten, so its boxes have not moved");
                continue;
            }

            var pane = MemberPlanning.Text(holder.Member("transport_id"))
                ?? MemberPlanning.Text(c.Services.Mailbox?.ReadRegistration(session)?.Get("pane_id") ?? JsonValue.Null);
            string? failed;
            try
            {
                failed = callers.Register(c.Cwd, session, (int)pid.AsNumber(), pane, teamName, false).Count == 0 ? "was refused" : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed = "failed: " + e.Message;
            }

            if (failed is not null)
            {
                warnings.Add($"duty: re-registering {name} (session {session}) {failed}; its boxes have not moved");
            }
        }

        return warnings;
    }
}
