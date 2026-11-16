using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Commands;

/// <summary>
/// <c>pause-member &lt;member&gt;|--team T</c> and <c>resume-member &lt;member&gt;|--team T</c> (b456): the pause is a row in the state
/// database, and while it stands ahd sends the member no check-in, overdue, resume or idle nudge and re-delivers nothing to it, and raises
/// no alarm on its requests. Resume restarts its request clocks from that moment. Each member is told by one mail event. Unlike
/// <c>quit</c> (alias <c>pause</c>) this closes nothing and holds no team.
/// </summary>
internal static class MemberPauseVerb
{
    private static readonly string[] Flags = ["team", "cwd", "orchestrator-pid"];

    [Verb("roster:pause-member")]
    internal static int Pause(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, c => Run(c, true));

    [Verb("roster:resume-member")]
    internal static int Resume(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, c => Run(c, false));

    private static int Run(RosterContext c, bool paused)
    {
        var verb = paused ? "pause-member" : "resume-member";
        StreamSupport.RejectUnknownFlags(c, Flags, verb);
        var args = c.Opts.Positionals;
        if (args.Count > 1 || (args.Count == 0 && c.Opts.Text("team") is null))
        {
            throw new RosterFailure($"{verb}: usage: ah roster {verb} <member> | ah roster {verb} --team <T>");
        }

        if (c.TeamFile is null)
        {
            throw new RosterFailure($"{verb}: the default (unnamed) team cannot be paused member by member: its boxes are not on the delivery ladder; name the team with --team <T>; nothing was changed");
        }

        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        var teamName = WorkTreeAssignment.TeamOf(c);
        var names = DismissVerb.Members(team).Select(static m => MemberPlanning.Text(m.Member("name"))).OfType<string>().ToList();
        if (args.Count == 1)
        {
            if (!names.Contains(args[0]))
            {
                throw new RosterFailure($"{verb}: {args[0]} is not a member of {teamName}; nothing was changed");
            }

            names = [args[0]];
        }

        var at = c.Services.Clock.UtcNow.ToUnixTimeMilliseconds();
        var stamp = at.ToString(CultureInfo.InvariantCulture);
        var done = new JsonArray();
        var told = new JsonArray();
        foreach (var name in names)
        {
            c.Services.Roster.SetPause(c.Dir, c.TeamFile, name, paused, at);
            done.Add(name);
            var text = paused
                ? "ah: you are paused by your orchestrator. Stop where you are, start nothing new, and wait; ahd will not nudge you until you are resumed."
                : "ah: you are resumed. Continue your open brief from where you stopped; its clock restarts now.";
            var reached = c.Services.EventMail?.SendEvent(
                c.Cwd,
                c.Dir,
                $"{verb}:{teamName}:{name}:{stamp}",
                paused ? DeliveryLadder.MemberPausedEvent : DeliveryLadder.MemberResumedEvent,
                text,
                new JsonObject { ["member"] = name, ["team"] = teamName },
                [$"{name}@{teamName}"]) ?? [];
            foreach (var address in reached)
            {
                told.Add(address);
            }
        }

        c.Out(new JsonObject { ["verb"] = verb, ["paused"] = paused, ["team"] = teamName, ["members"] = done, ["told"] = told });
        return 0;
    }
}
