using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Text;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// The allow hook for the ah CLIs. Every ah operation is a Bash call to one of the plugin's own scripts, and without this
/// hook each would raise a permission prompt. It grants prompt-free execution of exactly those scripts, with arguments in
/// the <see cref="AhCli"/> grammar. A partial or uncertain parse prints nothing, which leaves the user's ordinary permission
/// flow untouched. Close commands are left to their own gate. It never denies an ah call (ah-simplify S2).
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class AhCliGate
{
    public static HookOutcome Run(GateContext c)
    {
        if (c.ToolName() != "Bash")
        {
            return HookEmit.Silent;
        }

        var s = c.Services;
        var parsed = AhCli.Parse(c.Command());
        if (parsed is null || AhCli.IsCloseCommand(parsed))
        {
            return HookEmit.Silent;
        }

        if (!AhCli.ScriptUnderRoot(parsed.ScriptPath, s.Environment.Plugin?.Root, s.Files))
        {
            return HookEmit.Silent;
        }

        try
        {
            RecordDeliver(c, parsed);
            RecordMsgSend(c, parsed);
        }
        catch (Exception e)
        {
            c.LogError(e);
        }

        return HookEmit.Decision("allow", $"ah: plugin-shipped CLI — {NativeName(parsed.Script)} {(string.IsNullOrEmpty(parsed.Verb) ? "(no verb)" : parsed.Verb)}");
    }

    /// <summary>The command a role runs for a parsed script, in the native form the allow reason names.</summary>
    private static string NativeName(string script) => script switch
    {
        "roster" => "ah roster",
        "msg" => "ah msg",
        "rules" => "ah rules",
        _ => "ahd",
    };

    /// <summary>The unit never denies, so nothing it emits stays hard.</summary>
    internal static bool StaysHard(GateContext c) => false;

    [GeneratedRegex("[;&|\\r\\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentBreak();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentAtStart();

    /// <summary>
    /// Whether any segment of the command (split at <c>;</c>, <c>&amp;</c>, <c>|</c> and newlines, after any opening <c>(</c>, brace, backtick
    /// or <c>$(</c>) starts with a variable assignment, whatever its value.
    /// </summary>
    internal static bool HasAssignmentSegment(string command)
    {
        foreach (var segment in SegmentBreak().Split(command))
        {
            var word = segment.TrimStart(' ', '\t', '(', '{', '`');
            if (word.StartsWith("$(", StringComparison.Ordinal))
            {
                word = word[2..].TrimStart(' ', '\t', '(', '{', '`');
            }

            if (AssignmentAtStart().IsMatch(word))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the command has more commands than the tokenizer's budget, or an assignment whose value it cannot read reliably: its segments are then not trusted.</summary>
    internal static bool TokenizerGaveUp(string? command)
    {
        if (command is null)
        {
            return false;
        }

        var segments = Ah.Core.CoreModule.ShellCommandsChecked(command, out var unreadable);
        return unreadable || ExpandingAssignment(command)
            || segments.Any(seg => seg.ProgramName.StartsWith('$'))
            || (command.Contains("<<", StringComparison.Ordinal) && segments.Any(seg => seg.Words.Any(w => NodePath.Basename(w) is "sh" or "bash" or "zsh" or "dash" or "ksh" or "mksh" or "csh" or "tcsh" or "fish")));
    }

    /// <summary>An assignment prefix whose value holds a substitution or expansion: the tokenizer splits such a value at its inner spaces and misplaces the program word.</summary>
    private static bool ExpandingAssignment(string command) =>
        HasAssignmentSegment(command) && (command.Contains("${", StringComparison.Ordinal) || command.Contains("$(", StringComparison.Ordinal) || command.Contains('`', StringComparison.Ordinal));

    /// <summary>
    /// An <c>ah roster deliver &lt;name&gt; --req &lt;path&gt;</c> that is not <c>--wait-only</c> sends a brief to a pane member:
    /// record it as this session's dispatch, the same row a SendMessage dispatch gets, so the Orchestrator's Stop-hook
    /// check-ins cover pane work. Written before the command runs.
    /// </summary>
    private static void RecordDeliver(GateContext c, AhCommand parsed)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input) || parsed.Script != "roster" || parsed.Verb != "deliver")
        {
            return;
        }

        if (parsed.FlagText("req") is not { } req || parsed.FlagIsTrue("wait-only"))
        {
            return;
        }

        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var path = ResolvedPath(c, parsed, req);
        var meta = s.Hierarchy.Core.ParseMsgFilename(path);
        if (sessionId.Length > 0 && meta is { Type: "request" })
        {
            s.Hierarchy.Peers.AppendDispatchRecord(sessionId, meta.Id, meta.To, path);
        }
    }

    /// <summary>
    /// An <c>ah msg send &lt;address&gt; --ref &lt;request&gt;</c> mails a brief: record it as this session's dispatch, the row a
    /// SendMessage dispatch gets, with the request's path and the address. Only a send that names an existing request file records;
    /// <c>--kind</c> is not consulted. Written before the command runs, so a send that then fails still leaves the row.
    /// </summary>
    private static void RecordMsgSend(GateContext c, AhCommand parsed)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input) || parsed.Script != "msg" || parsed.Verb != "send")
        {
            return;
        }

        var reference = parsed.FlagText("ref");
        var recipient = parsed.Positional.Count == 0 ? null : parsed.Positional[0];
        if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(recipient))
        {
            return;
        }

        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var path = ResolvedPath(c, parsed, reference);
        var meta = s.Hierarchy.Core.ParseMsgFilename(path);
        if (sessionId.Length > 0 && meta is { Type: "request" } && s.Hierarchy.Core.MsgExists(path))
        {
            s.Hierarchy.Peers.AppendDispatchRecord(sessionId, meta.Id, meta.To, path, s.Hierarchy.Peers.StripRef(recipient));
        }
    }

    /// <summary>A path the command names, made absolute against the command's <c>--cwd</c> and else the hook input's cwd.</summary>
    internal static string ResolvedPath(GateContext c, AhCommand parsed, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var cwd = parsed.FlagText("cwd") ?? c.Input.Text("cwd");
        return string.IsNullOrEmpty(cwd) ? path : Path.GetFullPath(Path.Combine(cwd, path));
    }
}
