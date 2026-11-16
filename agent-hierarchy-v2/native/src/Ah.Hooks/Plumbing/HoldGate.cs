using Ah.Roster;

namespace Ah.Hooks.Plumbing;

/// <summary>
/// The hold test every hook makes: the hold that governs a session, found through the one predicate on the teams the session
/// orchestrates or has a registered box in. A hold file that could not be read holds the team and is logged once per call.
/// </summary>
internal static class HoldGate
{
    /// <summary>The first hold on any team of the session, or null when every team runs (or the session has no team).</summary>
    public static EffectiveHold? Of(GateServices s, string cwd, string? sessionId)
    {
        // A unit-test rig composes no roster or config; a real session always has both.
        if (string.IsNullOrEmpty(sessionId) || (object?)s.Roster is null || (object?)s.Config is null)
        {
            return null;
        }

        var dir = s.Config.HierarchyDir(cwd);
        var teams = new HashSet<string?>(s.Roster.TeamsOfSession(dir, sessionId));
        if (s.Advisory?.Mailbox is { } mailbox && mailbox.ReadRegistration(sessionId) is { } registration)
        {
            foreach (var box in mailbox.RegistrationBoxes(registration))
            {
                if (box.Address.Split('@', 2) is [_, var team])
                {
                    _ = teams.Add(team);
                }
            }
        }

        foreach (var team in teams)
        {
            if (s.Roster.HoldOf(dir, team) is { } hold)
            {
                if (hold.ParseError is { } error)
                {
                    s.ErrorLog.Log("hold", $"{hold.Path}: {error}");
                }

                return hold;
            }
        }

        return null;
    }

    /// <summary>
    /// The line a held session's start shows. A session that orchestrates a team also gets the plan to hand the user and the resume
    /// command.
    /// </summary>
    public static string Line(GateServices s, string cwd, string? sessionId, EffectiveHold hold)
    {
        var label = hold.Kind == "stop" ? "STOPPED" : "QUIT";
        var line = $"TEAM {hold.Team ?? hold.Source ?? "default"} IS {label} since {hold.Since ?? "unknown"}. Do not start, resume or continue any work and do not send mail. Wait. Only the top Orchestrator resumes it, on the user's order.";
        if (string.IsNullOrEmpty(sessionId) || s.Roster.TeamsOfSession(s.Config.HierarchyDir(cwd), sessionId).Count == 0)
        {
            return line;
        }

        var path = hold.Kind == "stop"
            ? Field(hold, "assessment") ?? Field(hold, "evidence")
            : Field(hold, "plan");
        var top = Field(hold, "cascade_from") ?? hold.Source ?? "@default";
        return line + $"\nResume plan: {path ?? "none yet"}. Resume only when the user orders it: ah roster resume --hold --team {top}.";
    }

    private static string? Field(EffectiveHold hold, string name) =>
        hold.Record.IsMap && hold.Record.Member(name) is { IsText: true } v && v.AsString().Length > 0 ? v.AsString() : null;
}
