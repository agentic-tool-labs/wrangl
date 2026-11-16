using Ah.Abstractions;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The typed line <c>/wrangl:orchestrator</c> or <c>/wrangl:orchestrator off</c> (UserPromptSubmit receives it as typed); <c>/wrangl:orchestrate</c>
/// is an alias. It makes a plain session the Orchestrator now, with the directive in this turn, or ends that, by setting or clearing
/// the session's flag; a session that is a member, a subagent or another <c>--agent</c> session is refused with the prompt blocked.
/// Any other prompt is not this command.
/// </summary>
internal static class OrchestrateCommand
{
    private const string Usage = "usage: /wrangl:orchestrate [off]";

    /// <summary>The outcome for the prompt, or null when the prompt is not this command.</summary>
    internal static HookOutcome? Run(GateContext c)
    {
        var s = c.Services;
        var words = (c.Input.Text("prompt") ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || (words[0] != "/" + s.Environment.Qualified("orchestrate") && words[0] != "/" + s.Environment.Qualified("orchestrator")))
        {
            return null;
        }

        var off = words.Length == 2 && words[1] == "off";
        if (words.Length > 2 || (words.Length == 2 && !off))
        {
            return HookEmit.Block($"wrangl:orchestrate refused: {Usage}");
        }

        var cwd = c.Cwd();
        try
        {
            if (Refusal(c, off) is { } refusal)
            {
                return HookEmit.Block("wrangl:orchestrate refused: " + refusal);
            }

            if (OrchestratorIdentity.LaunchedAsAgent(c))
            {
                return Say("already the Orchestrator (launched with --agent).");
            }

            return off ? Off(c, cwd) : On(c, cwd);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return HookEmit.Block("wrangl:orchestrate refused: the hook failed; the session is unchanged.");
        }
    }

    private static string? Refusal(GateContext c, bool off)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return "a subagent cannot become the Orchestrator.";
        }

        if (string.IsNullOrWhiteSpace(c.Input.Text("session_id")))
        {
            return "no session id.";
        }

        if (c.Input.Member("agent_type") is { IsTruthy: true })
        {
            return OrchestratorIdentity.LaunchedAsAgent(c)
                ? off ? "launched with --agent; relaunch without it." : null
                : "this session was launched with another --agent.";
        }

        if (s.Config.ResolveHierarchyRole(c.Input).Role is not null)
        {
            return "a member session cannot become the Orchestrator.";
        }

        return s.Environment.GetVariable("WRANGL_TEAM_FILE") is { Length: > 0 }
            ? "this session belongs to a team as a member."
            : null;
    }

    private static HookOutcome On(GateContext c, string cwd)
    {
        if (OrchestratorIdentity.Flagged(c))
        {
            return Say("already on.");
        }

        if (RepoIsOff(c, cwd))
        {
            return Say("hierarchy is off in this repo; run /wrangl:hierarchy on first");
        }

        if (!ProcessKnown(c))
        {
            return HookEmit.Block("wrangl:orchestrate refused: this session's process cannot be identified.");
        }

        var written = OrchestratorIdentity.Write(c, "on", cwd);
        if (!written.Ok)
        {
            return HookEmit.Block($"wrangl:orchestrate refused: the flag was not recorded ({written.Error}).");
        }

        if (!OrchestratorIdentity.Changed(written))
        {
            return Say("already on.");
        }

        var line = "on: this session is now the Orchestrator.";
        return SessionStart.OptInPayload(c, cwd) is { } payload ? Say(line + "\n\n" + payload) : Say(line);
    }

    private static HookOutcome Off(GateContext c, string cwd)
    {
        var written = OrchestratorIdentity.Write(c, "off", cwd);
        if (!written.Ok)
        {
            return HookEmit.Block($"wrangl:orchestrate refused: the flag was not cleared ({written.Error}).");
        }

        var line = OrchestratorIdentity.Changed(written) ? "off: this session is no longer the Orchestrator." : "was not on.";
        line += " This stops future injection; text already in context can't be removed.";
        ImplicitTeam.Drop(c, cwd);
        return Say(OrchestratorIdentity.OwnsTeam(c, cwd) ? line + " this session owns a team and stays in." : line);
    }

    /// <summary>
    /// Opts the session in on an ah2 act of its own, in one write: the text for this turn, or null when the session is left alone
    /// (already in, declined, refused by the command's rules, or in a repo that is switched off). <paramref name="trigger"/> names the act.
    /// </summary>
    internal static string? Implicitly(GateContext c, string trigger)
    {
        var cwd = c.Cwd();

        // ponytail: "owns a team and has no flag" stands for "this call made the team"; a resumed owner without a flag gets the directive once more. Compare ownership before the call if that shows up.
        var alreadyIn = trigger == ImplicitOptIn.TeamAct ? OrchestratorIdentity.AlwaysOn(c, cwd) : OrchestratorIdentity.In(c);
        if (Refusal(c, off: false) is not null || OrchestratorIdentity.LaunchedAsAgent(c) || OrchestratorIdentity.Declined(c) || alreadyIn
            || RepoIsOff(c, cwd) || !ProcessKnown(c))
        {
            return null;
        }

        var written = OrchestratorIdentity.Write(c, "on", cwd);
        if (!written.Ok)
        {
            throw new InvalidOperationException($"the flag was not recorded ({written.Error})");
        }

        if (!OrchestratorIdentity.Changed(written))
        {
            return null;
        }

        var line = $"Wrangl: this session is now the Orchestrator (opted in by {trigger}). /{c.Services.Environment.Qualified("orchestrator")} off undoes it.";
        return SessionStart.OptInPayload(c, cwd) is { } payload ? line + "\n\n" + payload : line;
    }

    private static bool RepoIsOff(GateContext c, string cwd)
    {
        var resolved = c.Services.Config.ResolveConfig(cwd);
        return resolved.Get("configured") is { IsTruthy: true } && resolved.Get("enabled") is not { IsTruthy: true };
    }

    private static bool ProcessKnown(GateContext c)
    {
        var pid = c.Services.Process.ParentPid;
        return pid > 1 && c.Services.Sessions?.Table.StartTime(pid) is { Length: > 0 };
    }

    private static HookOutcome Say(string text) => HookEmit.AdditionalContext("UserPromptSubmit", "wrangl:orchestrate " + text);
}
