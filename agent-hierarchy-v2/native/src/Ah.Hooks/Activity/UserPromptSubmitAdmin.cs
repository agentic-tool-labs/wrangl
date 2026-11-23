using System.Globalization;
using Ah.Abstractions;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Activity;

/// <summary>
/// ah2-499 (adr-35): the typed line <c>/wrangl:admin [on|off|show]</c> makes this session's harness the admin session, or with <c>off</c>
/// ends that when this session holds the grant. Only a prompt the user submitted counts: the bridge marks its own prompts
/// <c>plugin_submitted</c>, and a command the model runs raises no prompt event. The grant is written through the same record and
/// validation as <c>ah admin grant</c>, with <c>via: prompt</c>. Every outcome of the command is one context line; the prompt is never blocked,
/// and any prompt that is not the command is left alone.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class UserPromptSubmitAdmin
{
    private const string Name = "userpromptsubmit-admin";

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        var command = "/" + s.Environment.Qualified("admin");
        var text = (c.Input.Text("prompt") ?? string.Empty).TrimStart();
        if (!text.StartsWith(command, StringComparison.Ordinal) || (text.Length > command.Length && !char.IsWhiteSpace(text[command.Length])))
        {
            // a prompt that names the command but is not it: logged only, so an expanded or rewritten form can be diagnosed
            if (text.Contains(s.Environment.Qualified("admin"), StringComparison.Ordinal))
            {
                Log(c, "wrangl:admin not matched: the prompt names the command but does not start with it as typed; nothing changed.");
            }

            return HookEmit.Silent;
        }

        var argument = text[command.Length..].Trim();
        if (c.Input.Member("plugin_submitted") is { IsTruthy: true })
        {
            return Say(c, "no change: the plugin submitted this prompt; only a prompt the user types grants admin.");
        }

        if (argument is not ("" or "on" or "off"))
        {
            return Say(c, "no change: only on and off change the grant.");
        }

        try
        {
            return argument == "off" ? Off(c) : On(c);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return Say(c, "no change: the hook failed.");
        }
    }

    private static HookOutcome On(GateContext c)
    {
        var s = c.Services;
        var table = s.Sessions?.Table;
        if (table is null || MeshModule.HarnessOf(table, s.Process.ParentPid) is not { } harness)
        {
            return Say(c, "no grant: this session's Claude Code process cannot be identified.");
        }

        if (MeshModule.AdminPromptGrantRefusal(table, harness, s.Sessions!.Executable) is { } refusal)
        {
            return Say(c, "no grant: " + refusal);
        }

        if (s.HomeDocs is not { } docs || !MeshModule.TryReadAdminGrant(docs, s.Environment.AhHome, out var earlier))
        {
            return Say(c, "no grant: the state database cannot be read.");
        }

        var session = c.Input.Text("session_id");
        if (MeshModule.WriteAdminGrant(table, docs, s.Environment.AhHome, harness, session, s.Clock.UtcNow, MeshModule.AdminGrantViaPrompt) is { } error)
        {
            return Say(c, $"no grant: {error}");
        }

        var replaced = MeshModule.AdminGrantPid(earlier) is { } old && old != harness ?$" It replaces the grant held by pid {Pid(old)}." : string.Empty;
        return Say(c, $"on: this session (pid {Pid(harness)}) is now the admin session, via prompt.{replaced}");
    }

    private static HookOutcome Off(GateContext c)
    {
        var s = c.Services;
        if (s.HomeDocs is not { } docs || !MeshModule.TryReadAdminGrant(docs, s.Environment.AhHome, out _))
        {
            return Say(c, "no change: the state database cannot be read.");
        }

        if (s.Sessions?.Table is not { } table || !MeshModule.HoldsAdminGrant(table, s.Process.ParentPid, docs, s.Environment.AhHome))
        {
            return Say(c, "no change: this session is not the admin session.");
        }

        return MeshModule.RevokeAdminGrant(docs, s.Environment.AhHome) is { } error
            ? Say(c, $"no change: {error}")
            : Say(c, "off: this session is no longer the admin session.");
    }

    private static string Pid(int pid) => pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>The outcome as this turn's context line, and the same line in the hook log, so a grant that did or did not happen leaves a trace.</summary>
    private static HookOutcome Say(GateContext c, string text)
    {
        Log(c, "wrangl:admin " + text);
        return HookEmit.AdditionalContext("UserPromptSubmit", "wrangl:admin " + text);
    }

    private static void Log(GateContext c, string line)
    {
        try
        {
            c.Services.ErrorLog.Log(Name, "trace: " + line);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // the log is a trace only; the outcome stands without it
        }
    }
}
