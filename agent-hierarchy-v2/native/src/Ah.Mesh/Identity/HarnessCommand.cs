namespace Ah.Mesh;

/// <summary>Whether a process command line is the Claude harness itself, as opposed to a shell or a helper that merely runs near it.</summary>
internal static class HarnessCommand
{
    private static readonly string[] Runtimes = ["node", "bun"];

    /// <summary>
    /// True when the basename of the first word is <c>claude</c>, or it is <c>node</c> or <c>bun</c> and a later word is a path
    /// with a <c>claude-code</c> segment or ending in <c>/claude</c> (the npm and global install forms). Words are split on
    /// white space, so a path with a space in it is not matched.
    /// </summary>
    public static bool Matches(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var words = commandLine.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var first = Basename(words[0]);
        if (first == "claude")
        {
            return true;
        }

        return Runtimes.Contains(first, StringComparer.Ordinal)
            && words.Skip(1).Any(w => w.Contains('/', StringComparison.Ordinal)
                && (w.EndsWith("/claude", StringComparison.Ordinal) || w.Split('/').Contains("claude-code", StringComparer.Ordinal)));
    }

    /// <summary>
    /// Whether <paramref name="pid"/> is a Claude harness session: the environment's harness pid, when it names one, is this pid;
    /// a session-pids row exists for it from its own start on; and its command is the harness. The cheapest tests run first and a
    /// failed one ends the check.
    /// </summary>
    public static bool IsSession(
        int pid,
        string? claudePid,
        Func<int, DateTimeOffset?> startOf,
        Func<int, DateTimeOffset, bool> hasRowSince,
        Func<int, string?> commandOf,
        Func<int, string?>? executableOf = null)
    {
        if (!string.IsNullOrEmpty(claudePid) && claudePid != pid.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return false;
        }

        if (startOf(pid) is not { } started || !hasRowSince(pid, started))
        {
            return false;
        }

        return IsHarnessProcess(pid, commandOf, executableOf);
    }

    /// <summary>
    /// The command half of <see cref="IsSession"/>: the process's executable basename is <c>claude</c>, or its command line
    /// <see cref="Matches"/>. It reads no session record and no environment.
    /// </summary>
    public static bool IsHarnessProcess(int pid, Func<int, string?> commandOf, Func<int, string?>? executableOf = null) =>
        // the executable is read apart from the arguments, so a harness installed under a path with a space is still recognised
        (executableOf?.Invoke(pid) is { Length: > 0 } executable && Basename(executable.Trim()) == "claude")
        || Matches(commandOf(pid));

    private static string Basename(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
