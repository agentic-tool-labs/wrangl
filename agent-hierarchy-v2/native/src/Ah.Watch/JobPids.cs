using Ah.Abstractions;

namespace Ah.Watch;

/// <summary>Which process below the owner's Claude session is a job's own, found from the process tree.</summary>
internal static class JobPids
{
    private const double StartSlackMs = 1000;

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
                && taken?.Contains(n.Pid) != true && n.Command is { } command && Names(command, token))
            .ToList();
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

    /// <summary>Whether <paramref name="command"/> is a plain shell run with <c>-c</c>, the wrapper a tool call starts a command under.</summary>
    internal static bool IsShellWrapper(string? command)
    {
        var words = (command ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && words[0].Split('/')[^1] is "sh" or "bash" or "zsh" && words.Skip(1).Any(static w => w.StartsWith('-') && w.Contains('c', StringComparison.Ordinal));
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

    /// <summary>The first word of the command that is not a wrapper, without its directory; null for an empty label.</summary>
    internal static string? ProgramOf(string label)
    {
        foreach (var word in label.Split([' ', '\t', ';', '&', '|', '('], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = word.Contains('/', StringComparison.Ordinal) ? word[(word.LastIndexOf('/') + 1)..] : word;
            if (name.Length > 0 && !Wrappers.Contains(name, StringComparer.Ordinal) && !name.Contains('=', StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }
}
