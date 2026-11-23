using System.Globalization;
using System.Text;

namespace Ah.Watch;

/// <summary>
/// The commands that are not jobs: the wake sources (<c>ah roster wake</c>, <c>ah roster overdue</c>), which the wake files already track,
/// a resource claim (<c>ah roster resource claim</c>), which waits no longer than its own <c>--wait</c>, and a bare <c>sleep N</c>, which is bounded by its own argument. A command counts only when it is exactly one simple command whose
/// program resolves to the real binary, so nothing that merely contains such text, or runs a look-alike, is excluded.
/// </summary>
internal static class JobExclusion
{
    private static readonly string[] SleepBinaries = ["/bin/sleep", "/usr/bin/sleep"];

    internal static bool IsExcluded(string? command, Func<string, string?> realPath, IReadOnlyCollection<string> ahBinaries, Func<string, bool>? fileExists = null)
    {
        if (Words(command) is not { Count: > 0 } argv || realPath(argv[0]) is not { } program)
        {
            return false;
        }

        // ah2-509: a waiting resource claim is a wait bounded by its own --wait, like wake
        if (argv.Count >= 3 && argv[1] == "roster" && (argv[2] is "wake" or "overdue" || (argv[2] == "resource" && argv.Count >= 4 && argv[3] == "claim"))
            && (ahBinaries.Contains(program, StringComparer.Ordinal) || IsInstalledAh(program, fileExists)))
        {
            return true;
        }

        return argv.Count == 2 && SleepBinaries.Contains(program, StringComparer.Ordinal)
            && double.TryParse(argv[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Whether <paramref name="program"/> is the <c>bin/ah</c> of a plugin install: a session can be told the launcher of another install of this plugin than the one its hooks run from.</summary>
    private static bool IsInstalledAh(string program, Func<string, bool>? fileExists) =>
        fileExists is not null
        && Path.GetFileName(program) == "ah"
        && Path.GetDirectoryName(program) is { } bin && Path.GetFileName(bin) == "bin"
        && Path.GetDirectoryName(bin) is { } root && fileExists(Path.Combine(root, ".claude-plugin", "plugin.json"));

    /// <summary>The words of a single simple command, split as a shell would; null when the line is more than one command or uses any expansion or redirection.</summary>
    internal static List<string>? Words(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch is '`' or '\n' or '\r' || (ch == '$' && i + 1 < command.Length && command[i + 1] == '('))
            {
                return null;
            }

            if (quote == '\'')
            {
                if (ch == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    _ = current.Append(ch);
                }

                continue;
            }

            if (quote == '"')
            {
                if (ch == '"')
                {
                    quote = '\0';
                }
                else if (ch == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\')
                {
                    _ = current.Append(command[++i]);
                }
                else
                {
                    _ = current.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    inWord = true;
                    break;
                case '\\' when i + 1 < command.Length:
                    _ = current.Append(command[++i]);
                    inWord = true;
                    break;
                case ';' or '&' or '|' or '<' or '>' or '(' or ')':
                    return null;
                case ' ' or '\t':
                    if (inWord)
                    {
                        words.Add(current.ToString());
                        _ = current.Clear();
                        inWord = false;
                    }

                    break;
                default:
                    _ = current.Append(ch);
                    inWord = true;
                    break;
            }
        }

        if (quote != '\0')
        {
            return null;
        }

        if (inWord)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
