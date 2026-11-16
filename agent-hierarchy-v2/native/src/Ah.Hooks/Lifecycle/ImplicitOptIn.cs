using Ah.Abstractions;
using Ah.Core;
using Ah.Core.Shell;
using Ah.Core.Text;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// An ah2 act by the session opts it in: a typed ah2 command, a model <c>Skill</c> call of this plugin, or a call of this plugin's
/// <c>ah</c> that leaves the session owning a team. Any other <c>ah</c> call, such as a read, leaves a bare session bare. It runs after the
/// act, so nothing here can hold it up: a failure is silent with one hook-errors row. Turning the hierarchy off is not such an act.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class ImplicitOptIn
{
    /// <summary>The trigger of an <c>ah</c> call that left the session owning a team; owning it makes the session in, so <see cref="OrchestrateCommand.Implicitly"/> does not ask that.</summary>
    internal const string TeamAct = "`ah`";

    private const string CommandName = "<command-name>";
    private const string CommandNameEnd = "</command-name>";
    private const string CommandArgs = "<command-args>";
    private const string CommandArgsEnd = "</command-args>";

    /// <summary>The PostToolUse and PostToolUseFailure unit.</summary>
    public static HookOutcome Run(GateContext c)
    {
        try
        {
            return !c.Services.Config.IsSubagent(c.Input) && ToolTrigger(c) is { } trigger && OrchestrateCommand.Implicitly(c, trigger) is { } text
                ? HookEmit.AdditionalContext(c.Input.Text("hook_event_name") ?? "PostToolUse", text)
                : HookEmit.Silent;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return HookEmit.Silent;
        }
    }

    /// <summary>The outcome for a typed ah2 command that opts the session in, or null. The caller has ruled out a subagent.</summary>
    internal static HookOutcome? FromPrompt(GateContext c)
    {
        try
        {
            return TypedTrigger(c) is { } trigger && OrchestrateCommand.Implicitly(c, trigger) is { } text
                ? HookEmit.AdditionalContext("UserPromptSubmit", text)
                : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return null;
        }
    }

    private static string? ToolTrigger(GateContext c)
    {
        var s = c.Services;
        switch (c.ToolName())
        {
            case "Skill":
                var prefix = s.Environment.PluginName + ":";
                return c.ToolInput().Get("skill") is { IsText: true } skill && skill.AsString().StartsWith(prefix, StringComparison.Ordinal)
                    && !TurnsOff(skill.AsString()[prefix.Length..], c.ToolInput().Get("args") is { IsText: true } args ? args.AsString() : string.Empty)
                    ? $"Skill {skill.AsString()}"
                    : null;
            case "Bash":
                return c.Command() is { } command && CoreModule.ShellCommands(command).Any(seg => IsPluginAh(s, command, seg)) && OrchestratorIdentity.OwnsTeam(c, c.Cwd()) ? TeamAct : null;
            default:
                return null;
        }
    }

    // ponytail: TolerantAhCall's refusals decide what is missed: an assignment (`FOO=1 ah …`), `$AH` as the program, and `$(`, `<<` or a backtick anywhere in the command. The next plain `ah` call opts in; read past them if a session stays out too long.
    private static bool IsPluginAh(GateServices s, string command, CommandSegment seg) =>
        TolerantAhCall.Program(s, command, seg) is { } program && NodePath.Basename(program) == "ah"
        && AhCli.ScriptUnderRoot(program, s.Environment.Plugin?.Root, s.Files);

    /// <summary>The first word of the prompt when it names a command or skill of this plugin, whether or not it is qualified, and does not turn the hierarchy off.</summary>
    private static string? TypedTrigger(GateContext c)
    {
        var s = c.Services;
        var prompt = (c.Input.Text("prompt") ?? string.Empty).TrimStart();
        var (word, args) = prompt.StartsWith(CommandName, StringComparison.Ordinal) ? Tagged(prompt) : Plain(prompt);
        if (word is null || !word.StartsWith('/'))
        {
            return null;
        }

        var prefix = s.Environment.PluginName + ":";
        var qualified = word.StartsWith("/" + prefix, StringComparison.Ordinal);
        var local = qualified ? word[(prefix.Length + 1)..] : word[1..];
        return (qualified || Ships(s, local)) && !TurnsOff(local, args) ? word : null;
    }

    private static (string? Word, string Args) Plain(string prompt)
    {
        var words = prompt.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? (null, string.Empty) : (words[0], words.Length > 1 ? words[1] : string.Empty);
    }

    private static (string? Word, string Args) Tagged(string prompt)
    {
        var end = prompt.IndexOf(CommandNameEnd, StringComparison.Ordinal);
        if (end < 0)
        {
            return (null, string.Empty);
        }

        var open = prompt.IndexOf(CommandArgs, end, StringComparison.Ordinal);
        var close = open < 0 ? -1 : prompt.IndexOf(CommandArgsEnd, open, StringComparison.Ordinal);
        return (prompt[CommandName.Length..end].Trim(), close < 0 ? string.Empty : prompt[(open + CommandArgs.Length)..close]);
    }

    private static bool TurnsOff(string name, string args) => name is "orchestrate" or "orchestrator" or "hierarchy" && args.Trim() is "off" or "always-on off";

    /// <summary>Whether this plugin ships a command or skill of that name, read from the plugin itself so that the list cannot drift.</summary>
    private static bool Ships(GateServices s, string name) =>
        name.Length > 0 && !name.Contains('/', StringComparison.Ordinal) && !name.Contains("..", StringComparison.Ordinal)
        && s.Environment.Plugin?.Root is { Length: > 0 } root
        && (s.Files.Exists(NodePath.Join(root, "commands", name + ".md")) || s.Files.Exists(NodePath.Join(root, "skills", name, "SKILL.md")));
}
