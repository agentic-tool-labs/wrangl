using Ah.Core.Json;
using Ah.Launch;
using Ah.Mesh;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// The <c>launched-outside-ah</c> doctor row. Each pane member's Claude command line is read from the multiplexer and classified
/// the way the launch builder defines a launch by ah: <c>no-ah-launch</c> (a terminal multiplexer restoring its panes, or a hand
/// launch: model, effort and hooks were not applied) and <c>drift</c> (launched by ah, but agent, model or effort differ from the
/// row now) are warnings. The daemon's <c>bare</c> mark adds what only it can know (whether the process matches its member). A pane
/// that cannot be read and has no mark is reported as not checked, never as fine. Never red; no row for a team with no pane member.
/// </summary>
internal static class MemberLaunchDoctor
{
    private enum Reading
    {
        Unreadable,
        NoClaude,
        Classified,
    }

    /// <summary>Whether the daemon's recovery switch is on, read the way the daemon reads it.</summary>
    internal static bool RecoveryOn(RosterContext c) =>
        WatchModule.ReadRecoverySwitch(c.Services.HomeDocs).On;

    internal static (string Status, string Detail)? Row(RosterContext c)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.Member("members") is not { IsArray: true } list)
        {
            return null;
        }

        var outside = new List<string>();
        var unmatched = new List<string>();
        var drifted = new List<string>();
        var notChecked = new List<string>();
        var watched = false;
        foreach (var member in list.AsArray().Where(m => m.IsMap))
        {
            if (MemberPlanning.Text(member.AsObject(), "name") is not { } name || MemberPlanning.Text(member.AsObject(), "transport_id") is not { } pane)
            {
                continue;
            }

            if (global::Ah.Roster.RosterModule.StartingAgeSeconds(member, c.Services.Clock.UtcNow) is not null)
            {
                continue;
            }

            watched = true;
            var mark = BareMark.From(member.Member("bare"));
            var (reading, result) = Read(c, member, name, pane);
            if (reading == Reading.NoClaude || result is { Verdict: AhLaunchVerdict.AhLaunch })
            {
                continue;
            }

            if (result is { Verdict: AhLaunchVerdict.Drift })
            {
                drifted.Add(result.Drift!);
            }
            else if (result is { Verdict: AhLaunchVerdict.NoAhLaunch } || mark is { State: BareMark.Matched or BareMark.Unmatched })
            {
                if (mark is { State: BareMark.Unmatched })
                {
                    unmatched.Add($"{name} ({string.Join(", ", mark.Failed)})");
                }
                else
                {
                    var unseen = PaneIsMissingFromAgentList(c, pane) ? $"; herdr's agent list has no agent in pane {pane}, so the daemon cannot see it idle and will not relaunch it" : string.Empty;
                    var hint = WatchModule.IsBareOrchestrator(MemberPlanning.Text(member.AsObject(), "role"), name, null)
                        ? WatchModule.BareOrchestratorSentence
                        : WatchModule.BareSentence(RecoveryOn(c), c.TeamFile ?? "@default", name);
                    outside.Add($"{name} (launched outside ah: model/effort/hooks not applied; {hint}{unseen})");
                }
            }
            else if (mark is { State: BareMark.Drift })
            {
                drifted.AddRange(mark.Failed);
            }
            else
            {
                notChecked.Add(name);
            }
        }

        if (!watched)
        {
            return null;
        }

        var parts = new List<string>();
        if (outside.Count > 0)
        {
            parts.Add(string.Join("; ", outside));
        }

        if (unmatched.Count > 0)
        {
            parts.Add("a Claude without ah's launch runs in the pane but does not match the member, not acted on: " + string.Join(", ", unmatched));
        }

        if (drifted.Count > 0)
        {
            parts.Add(string.Join("; ", drifted));
        }

        if (notChecked.Count > 0)
        {
            parts.Add("not checked (the pane could not be read and the daemon has not looked): " + string.Join(", ", notChecked));
        }

        return parts.Count == 0 ? ("ok", "every pane member was launched by ah") : ("warn", string.Join(" | ", parts));
    }

    /// <summary>True only when herdr answered its agent list and no agent in it sits in <paramref name="pane"/>.</summary>
    private static bool PaneIsMissingFromAgentList(RosterContext c, string pane)
    {
        if (c.Services.Launch is not { } launch)
        {
            return false;
        }

        try
        {
            return !launch.Herdr.Agents().Any(a => a.Member("pane_id") is { IsText: true } p && p.AsString() == pane);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static (Reading Reading, AhLaunchResult? Result) Read(RosterContext c, JsonValue member, string name, string pane)
    {
        if (c.Services.Launch is not { } launch)
        {
            return (Reading.Unreadable, null);
        }

        IReadOnlyList<PaneProcess>? processes;
        try
        {
            processes = LaunchModule.PaneProcessesOf(launch.Herdr.Invoke(["pane", "process-info", "--pane", pane]));
        }
        catch (InvalidOperationException)
        {
            return (Reading.Unreadable, null);
        }

        if (processes is null)
        {
            return (Reading.Unreadable, null);
        }

        var claudes = processes.Where(p => MeshModule.IsHarnessCommand(string.Join(' ', p.Argv))).ToList();
        if (claudes.Count == 0)
        {
            return (Reading.NoClaude, null);
        }

        var expected = new AhLaunchExpectation(
            launch.Shapes.AgentRefOf(member, c.LaunchContext()),
            name,
            c.Services.Roster.TeamPath(c.TeamHome, c.TeamFile),
            MemberPlanning.Text(member.AsObject(), "model"),
            MemberPlanning.Text(member.AsObject(), "effort"));
        var results = claudes.Select(p => LaunchModule.ClassifyLaunch(p.Argv, expected, path => c.Services.Files.Exists(path) ? c.Services.Files.ReadText(path) : null)).ToList();
        var picked = results.FirstOrDefault(r => r.Verdict == AhLaunchVerdict.AhLaunch)
            ?? results.FirstOrDefault(r => r.Verdict == AhLaunchVerdict.Drift)
            ?? results[0];
        return (Reading.Classified, picked);
    }
}
