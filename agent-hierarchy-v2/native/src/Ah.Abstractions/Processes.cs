using System.Diagnostics.CodeAnalysis;

namespace Ah.Abstractions;

public sealed record ProcessRequest(
    IReadOnlyList<string> Argv,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    ReadOnlyMemory<byte> Stdin = default,
    TimeSpan? Timeout = null,
    int OutputCapBytes = 1 << 20);

public sealed record ProcessResult(
    int? ExitCode,
    int? Signal,
    bool TimedOut,
    bool Truncated,
    ReadOnlyMemory<byte> Stdout,
    ReadOnlyMemory<byte> Stderr);

public sealed record DetachedSpawnRequest(
    IReadOnlyList<string> Argv,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    string LogPath);

public interface IProcessRunner
{
    /// <summary>Runs to completion with both pipes pumped, the output capped, and the whole process group killed on timeout.</summary>
    ProcessResult Run(ProcessRequest request);
}

public interface IDetachedSpawner
{
    /// <summary>Starts the process in a new session with stdout and stderr appended to the log. Returns its pid.</summary>
    int SpawnDetached(DetachedSpawnRequest request);
}

public interface IProcessInspector
{
    bool PidAlive(int pid);

    /// <summary>
    /// Whether process <paramref name="pid"/> exists and this process's user may signal it (<c>kill(pid, 0)</c> succeeds): it is the
    /// user's own process, not another user's.
    /// </summary>
    bool PidOwned(int pid);

    /// <summary>
    /// The command line of process <paramref name="pid"/>: its arguments joined by single spaces, as <c>ps -o command=</c> prints
    /// them. Null when the process does not exist or its arguments cannot be read.
    /// </summary>
    string? CommandLine(int pid);
}

/// <summary>A process below another: its pid, when it started (null when the table cannot say), its command line, its parent's pid and its process group.</summary>
public sealed record ProcessNode(int Pid, DateTimeOffset? Started, string? Command, int Parent = 0, int Group = 0);

/// <summary>What the system's process table says about a process: where it sits in the tree and when it started.</summary>
public interface IProcessTable
{
    /// <summary>
    /// The ancestors of <paramref name="pid"/>, parent first and the root (pid 1) last, at most 64 of them; <paramref name="pid"/>
    /// itself is not in the list. Null when the table cannot say: the pid is not above 1, a link of the chain cannot be read, or the
    /// chain loops.
    /// </summary>
    IReadOnlyList<int>? Ancestors(int pid);

    /// <summary>
    /// Whether <paramref name="candidate"/> is among the first 64 ancestors of <paramref name="pid"/>, asking about no link beyond
    /// the one that names it. Null under the same conditions as <see cref="Ancestors"/>, when the walk cannot finish.
    /// </summary>
    bool? HasAncestor(int pid, int candidate);

    /// <summary>
    /// The ancestors of <paramref name="pid"/> as <see cref="Ancestors"/> lists them, ended at the first one for which
    /// <paramref name="stopAt"/> is true (that one included), so a caller that only needs the nearest match asks about no
    /// process beyond it. Null under the same conditions as <see cref="Ancestors"/>.
    /// </summary>
    IReadOnlyList<int>? AncestorsUntil(int pid, Func<int, bool> stopAt) =>
        Ancestors(pid) is { } chain ? [.. chain.TakeWhile(p => !stopAt(p)), .. chain.Where(stopAt).Take(1)] : null;

    /// <summary>
    /// The start time of a live process as the system <c>ps</c> printed it (the line end trimmed) with the time zone set to UTC,
    /// or null when it cannot say.
    /// </summary>
    string? StartTime(long pid);

    /// <summary>
    /// The same start time printed in the caller's own time zone, which is the form older records hold; null when it cannot say
    /// or the table cannot print it.
    /// </summary>
    string? LocalStartTime(long pid) => null;

    /// <summary>
    /// The controlling terminal of <paramref name="pid"/> as <c>ps -o tty=</c> names it (<c>ttys003</c>), empty when it has
    /// none, or null when the table cannot say.
    /// </summary>
    string? Tty(int pid) => null;

    /// <summary>The command line of <paramref name="pid"/> as <c>ps -o command=</c> prints it, or null when the table cannot say.</summary>
    string? Command(int pid) => null;

    /// <summary>Every process below <paramref name="pid"/> (children, their children and so on), or null when the table cannot say.</summary>
    IReadOnlyList<ProcessNode>? Descendants(int pid) => null;

    /// <summary>Every process the table lists, from one read of it, or null when the table cannot say.</summary>
    IReadOnlyList<ProcessNode>? All() => null;

    /// <summary>Every process in process group <paramref name="pgid"/>, wherever it sits in the tree, or null when the table cannot say.</summary>
    IReadOnlyList<ProcessNode>? InGroup(int pgid) => null;
}

/// <summary>Who a caller is, as the process tree proves it.</summary>
public enum PrincipalKind
{
    /// <summary>Neither a proven session nor the user: reads of no-identity routes only.</summary>
    Anonymous,

    /// <summary>No harness above the caller, and a terminal of its own that no live registered harness holds. A heuristic, not a proof.</summary>
    User,

    /// <summary>A descendant (or the process) of a live registration whose recorded start time is the process's own.</summary>
    Session,
}


public interface IExec
{
    /// <summary>Replaces the current process image. Returns only by throwing.</summary>
    [DoesNotReturn]
    void Exec(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string?>? environment = null);
}
