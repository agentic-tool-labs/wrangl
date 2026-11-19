using Ah.Abstractions;

namespace Ah.Watch;

/// <summary>Which process below the owner's Claude session is a job's own, found from the process tree.</summary>
internal static partial class JobPids
{
    private const double StartSlackMs = 1000;

    private const string TestRunScript = "ah-test-run";

    /// <summary>A label's leading <c>cd &lt;dir&gt; &amp;&amp;</c> or <c>cd &lt;dir&gt;;</c>, and the command after it.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*cd\s+[^\s;&|]+\s*(?:&&|;)\s*(.+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex LeadingCd();

    private static readonly string[] Wrappers = ["bash", "sh", "zsh", "env", "time", "nohup", "exec"];

    /// <summary>
    /// The oldest of <paramref name="below"/> that started at or after the job did (to the second, which is all the table prints), that no
    /// other job holds (<paramref name="taken"/>), and one of whose command words, without its directory, is the program the job's label
    /// runs (see <see cref="Names"/> for what never matches); null when none is.
    /// </summary>
    internal static ProcessNode? Pick(JobFact job, IReadOnlyList<ProcessNode> below, ISet<int>? taken = null)
    {
        var token = ProgramOf(job.Label);
        if (token is null)
        {
            return null;
        }

        var matches = below
            .Where(n => n.Started is { } started && started.ToUnixTimeMilliseconds() + StartSlackMs >= job.SentAtMs
                && taken?.Contains(n.Pid) != true && n.Command is { } command && Names(command, token) && !IsWakeSource(command))
            .ToList();
        if (token == TestRunScript)
        {
            // the outer wrapper is the job's: a test command may start a nested ah-test-run, deeper and later; the older tool-call shell
            // that names the script in its -c text is taken only when nothing else matches
            return matches.Where(static n => !IsShellWrapper(n.Command)).OrderBy(static n => n.Started).ThenBy(static n => n.Pid).FirstOrDefault()
                ?? matches.OrderBy(static n => n.Started).ThenBy(static n => n.Pid).FirstOrDefault();
        }

        var byPid = below.ToDictionary(static n => n.Pid);
        int Depth(ProcessNode n)
        {
            var depth = 0;
            for (var at = n; byPid.TryGetValue(at.Parent, out var up) && depth < 64; at = up)
            {
                depth++;
            }

            return depth;
        }

        return matches.Where(static n => !IsShellWrapper(n.Command)).OrderByDescending(Depth).ThenBy(static n => n.Started).ThenBy(static n => n.Pid).FirstOrDefault()
            ?? matches.OrderBy(static n => n.Started).ThenBy(static n => n.Pid).FirstOrDefault();
    }

    /// <summary>
    /// Whether <paramref name="command"/> runs <c>ah roster wake</c> or <c>ah roster overdue</c>: a wake source is never a job's process. An <c>ah test run</c>
    /// job's own process is <c>bin/ah-test-run</c>, which has no <c>ah</c> word, so without this the next <c>ah</c> process started after it, the waiter, was picked.
    /// </summary>
    internal static bool IsWakeSource(string command)
    {
        var words = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 2 < words.Length; i++)
        {
            if (words[i].Split('/')[^1] == "ah" && words[i + 1] == "roster" && words[i + 2] is "wake" or "overdue")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="command"/> is a plain shell run with <c>-c</c>, the wrapper a tool call starts a command under: <c>sh</c>, <c>bash</c> or
    /// <c>zsh</c> with a single-dash flag containing <c>c</c> before its first non-flag word. Words after a script path are the script's, not the shell's.
    /// </summary>
    internal static bool IsShellWrapper(string? command)
    {
        var words = (command ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0].Split('/')[^1] is not ("sh" or "bash" or "zsh"))
        {
            return false;
        }

        foreach (var word in words.Skip(1))
        {
            if (!word.StartsWith('-') || word == "--")
            {
                return false;
            }

            if (!word.StartsWith("--", StringComparison.Ordinal) && word.Contains('c', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="node"/>'s command names the program the job's label runs. A reused MSBuild or compiler-server node runs under <c>dotnet</c> but belongs to no job (see <see cref="Names"/>).</summary>
    internal static bool RunsJobsProgram(JobFact job, ProcessNode node) =>
        ProgramOf(job.Label) is { } token && node.Command is { } command && Names(command, token);

    /// <summary>
    /// Whether one of <paramref name="command"/>'s words, without its directory, is <paramref name="program"/>. A command that contains
    /// <c>MSBuild.dll</c> or <c>VBCSCompiler</c> anywhere never matches: those are reused build-server nodes that run under <c>dotnet</c> and
    /// belong to no job. The test is a plain substring over the whole command, so a job whose own command line contains either text is never
    /// matched either.
    /// </summary>
    private static bool Names(string command, string program) =>
        !command.Contains("MSBuild.dll", StringComparison.Ordinal) && !command.Contains("VBCSCompiler", StringComparison.Ordinal)
        && command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Any(word => string.Equals(word.Contains('/', StringComparison.Ordinal) ? word[(word.LastIndexOf('/') + 1)..] : word, program, StringComparison.Ordinal));

    /// <summary>
    /// The first word of the command that is not a wrapper, without its directory; null for an empty label. For <c>ah test run</c> and
    /// <c>with-slot.sh</c> it is <c>ah-test-run</c>: both exec that script under the same pid, so neither name is in the process table.
    /// For every label, not only a test run, one leading <c>cd &lt;dir&gt; &amp;&amp;</c> or <c>cd &lt;dir&gt;;</c> is skipped.
    /// </summary>
    internal static string? ProgramOf(string label)
    {
        if (LeadingCd().Match(label) is { Success: true } cd)
        {
            label = cd.Groups[1].Value;
        }

        var words = label.Split([' ', '\t', ';', '&', '|', '('], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var name = word.Contains('/', StringComparison.Ordinal) ? word[(word.LastIndexOf('/') + 1)..] : word;
            if (name.Length > 0 && !Wrappers.Contains(name, StringComparer.Ordinal) && !name.Contains('=', StringComparison.Ordinal))
            {
                return name == "with-slot.sh" || (name == "ah" && i + 2 < words.Length && words[i + 1] == "test" && words[i + 2] == "run") ? TestRunScript : name;
            }
        }

        return null;
    }
}
