using Ah.Core;
using Ah.Core.Shell;

namespace Ah.Hooks.Gates;

/// <summary>
/// Reads one shell segment of a compound command as an <c>ah</c> call. The segment is lexed from its raw slice so redirections and
/// pipes around it never reach the parser; a segment whose form could hide the program or its arguments is refused.
/// </summary>
internal static class TolerantAhCall
{
    /// <summary>
    /// The segment parsed as an ah call, or null when its form is not plain (nested, derived, wrapped, assigned, substituted,
    /// heredoc, <c>$'…'</c>, an empty word) or the grammar does not read it. <paramref name="line"/> is the normalised call text.
    /// </summary>
    internal static AhCommand? Parse(GateServices s, string command, CommandSegment seg, out string line)
    {
        line = string.Empty;
        if (PlainArgv(command, seg) is not { } argv)
        {
            return null;
        }

        line = string.Join(' ', argv.Skip(1).Select(CoreModule.QuoteShellWord).Prepend(CoreModule.QuoteShellWord(WithResolvedCliWord(s, argv[0]))));
        return AhCli.ParseWithProgress(line);
    }

    /// <summary>The segment's program as the shell resolves it, or null under the same refusals as <see cref="Parse"/>. No verb grammar is applied.</summary>
    internal static string? Program(GateServices s, string command, CommandSegment seg) =>
        PlainArgv(command, seg) is { } argv ? WithResolvedCliWord(s, argv[0]) : null;

    private static List<string>? PlainArgv(string command, CommandSegment seg)
    {
        // The segmenter drops a substitution's body from the segment it sits in and drops heredoc bodies, and $'…' is not
        // plain quoting, so the whole text is checked for each of them too.
        if (seg.Nested || seg.Derived || !seg.Reliable || seg.Wrappers.Count > 0 || seg.Assigned.Count > 0
            || seg.Words.Any(w => w.Contains('$') || w.Contains('`'))
            || command.Contains('`') || command.Contains("$(", StringComparison.Ordinal)
            || command.Contains("<(", StringComparison.Ordinal) || command.Contains(">(", StringComparison.Ordinal)
            || command.Contains("<<", StringComparison.Ordinal) || command.Contains("$'", StringComparison.Ordinal)
            || seg.Start < 0 || seg.End > command.Length || seg.Start >= seg.End
            || ShellScan.Commands(command[seg.Start..seg.End]) is not { Count: 1 } lexed
            || lexed[0].Argv.Count == 0)
        {
            return null;
        }

        return WithQuotedWordsRestored(lexed[0].Argv, seg.Words);
    }

    /// <summary>
    /// The lexer gives a quoted word that is not one plain token (a title with a space in it) empty text, which this parser cannot
    /// read as an argument. The segment's own words hold the real text, and are used for those words when the two lists line up
    /// word for word (same count, every non-empty word equal). Null when a word stays empty or the lists do not line up, which is
    /// the same refusal as before; a call with a redirection in the same segment has more segment words than arguments.
    /// </summary>
    private static List<string>? WithQuotedWordsRestored(IReadOnlyList<string> lexed, IReadOnlyList<string> words)
    {
        var argv = lexed.ToList();
        if (!argv.Any(a => a.Length == 0))
        {
            return argv;
        }

        if (words.Count != argv.Count)
        {
            return null;
        }

        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i].Length == 0)
            {
                argv[i] = words[i];
            }
            else if (argv[i] != words[i])
            {
                return null;
            }
        }

        return argv.Any(a => a.Length == 0) ? null : argv;
    }

    /// <summary>
    /// The command with its leading word made an absolute path when that word is a bare <c>ah</c> (found on <c>PATH</c>) or starts with
    /// <c>~/</c> (expanded with the environment's home directory): the shell resolves those before the program runs, so the parser,
    /// which reads absolute paths only, would otherwise deny a form the owner-verb gate matches. Nothing is trusted from the
    /// substitution: the parsed path still has to resolve under the plugin's own root (<see cref="AhCli.ScriptUnderRoot"/>), so an
    /// <c>ah</c> elsewhere is refused there.
    /// </summary>
    private static string WithResolvedCliWord(GateServices s, string command)
    {
        var start = command.Length - command.TrimStart(' ', '\t').Length;
        var end = command.IndexOfAny([' ', '\t'], start);
        var word = end < 0 ? command[start..] : command[start..end];
        string? resolved = null;
        if (word == "ah")
        {
            foreach (var dir in (s.Environment.GetVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = dir.TrimEnd('/') + "/ah";
                if (dir.StartsWith('/') && s.Files.Exists(candidate) && s.Files.IsExecutable(candidate))
                {
                    resolved = candidate;
                    break;
                }
            }
        }

        else if (word.StartsWith("~/", StringComparison.Ordinal) && s.Environment.HomeDirectory is { Length: > 0 } home && home.StartsWith('/'))
        {
            resolved = home.TrimEnd('/') + word[1..];
        }

        return resolved is null ? command : command[..start] + resolved + (end < 0 ? string.Empty : command[end..]);
    }
}
