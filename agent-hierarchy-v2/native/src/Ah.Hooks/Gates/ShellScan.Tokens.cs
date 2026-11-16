namespace Ah.Hooks.Gates;

/// <summary>One simple command of a shell line: its words without leading <c>NAME=value</c> assignments, those assignments, and the targets of its output redirections.</summary>
internal sealed record ShellCommand(IReadOnlyList<string> Argv, IReadOnlyList<string> Assignments, IReadOnlyList<string> Redirects);

internal static partial class ShellScan
{
    private static bool IsFdWord(string text) => text == "-" || text.StartsWith('$') || (text.Length > 0 && text.All(char.IsAsciiDigit));

    /// <summary>
    /// The simple commands of <paramref name="text"/>, including those inside command substitutions and <c>sh -c</c> strings, split
    /// with the same lexer the gates use. Null when the text cannot be classified (an unterminated quote, nesting too deep).
    /// A quoted word that is not one plain token has empty text. The words of a descriptor duplication (<c>2&gt;&amp;1</c>,
    /// <c>&lt;&amp;3</c>) are dropped; <c>&gt;&amp;file</c> and <c>&gt;|file</c> give <c>file</c> as a redirect target.
    /// </summary>
    internal static IReadOnlyList<ShellCommand>? Commands(string text)
    {
        try
        {
            var result = new List<(List<string> Argv, List<string> Assignments, List<string> Redirects)>();
            var lastGroup = -1;
            foreach (var cmd in CommandsOf(text, 0))
            {
                var assignments = new List<string>();
                var argv = new List<string>();
                var redirects = new List<string>();
                var clobberTarget = result.Count > 0 && cmd.Words.Count > 0 && cmd.Words[0].RedirectTarget && cmd.Group == lastGroup;
                if (clobberTarget)
                {
                    (argv, assignments, redirects) = result[^1];
                }

                foreach (var word in cmd.Words)
                {
                    if (word.RedirectTarget)
                    {
                        redirects.Add(word.Text);
                    }
                    else if (word.FdPrefix)
                    {
                        continue;
                    }
                    else if (argv.Count == 0 && IsAssignment(word.Text))
                    {
                        assignments.Add(word.Text);
                    }
                    else
                    {
                        argv.Add(word.Text);
                    }
                }

                if (!clobberTarget)
                {
                    result.Add((argv, assignments, redirects));
                }

                lastGroup = cmd.Group;
            }

            return [.. result.Select(r => new ShellCommand(r.Argv, r.Assignments, r.Redirects))];
        }
        catch (Unclassified)
        {
            return null;
        }
    }
}
