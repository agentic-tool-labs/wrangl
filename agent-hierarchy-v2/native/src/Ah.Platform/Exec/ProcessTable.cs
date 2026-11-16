using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Time;

namespace Ah.Platform;

/// <summary>
/// The process table as the system <c>ps</c> reports it, asked by absolute path so the caller's PATH cannot decide who a process's
/// ancestors are. Inside a <see cref="ProcessSnapshotScope"/> every question is answered from one <c>ps -ax</c> reading.
/// </summary>
internal sealed class ProcessTable(IProcessRunner runner, TimeSpan? psLimit = null) : IProcessTable
{
    private const int HopLimit = 64;

    private readonly TimeSpan _psLimit = psLimit ?? TimeSpan.FromSeconds(2);

    private static readonly string[] PsCandidates = ["/bin/ps", "/usr/bin/ps"];

    private static readonly Dictionary<string, string?> PsEnvironment = new() { ["PATH"] = "/usr/bin:/bin", ["LC_ALL"] = "C" };

    private static readonly Dictionary<string, string?> PsUtcEnvironment = new() { ["PATH"] = "/usr/bin:/bin", ["LC_ALL"] = "C", ["TZ"] = "UTC" };

    public IReadOnlyList<int>? Ancestors(int pid) => Walk(pid, until: null);

    public IReadOnlyList<int>? AncestorsUntil(int pid, Func<int, bool> stopAt) => Walk(pid, stopAt);

    public bool? HasAncestor(int pid, int candidate) => Walk(pid, p => p == candidate) is { } chain ? chain.Contains(candidate) : null;

    /// <summary>The chain of <paramref name="pid"/>'s ancestors, ended early once it has added one for which <paramref name="until"/> is true.</summary>
    private List<int>? Walk(int pid, Func<int, bool>? until)
    {
        if (pid <= 1)
        {
            return null;
        }

        if (NativeProcInfo.Supported)
        {
            return KernelWalk(pid, until, KernelHop);
        }

        Func<int, int?> parentOf;
        if (ProcessSnapshotScope.Active is { } slot)
        {
            if ((slot.Shared ? slot.Get(ReadShared) : slot.Get(Read)) is null)
            {
                return null;
            }
            else
            {
                parentOf = p => SnapRow(slot, p, full: false)?.Parent;
            }
        }
        else if (Warm() is { } warm && warm.Rows.ContainsKey(pid))
        {
            parentOf = p => warm.Rows.TryGetValue(p, out var row) ? row.Parent : null;
        }
        else
        {
            if (PsCandidates.FirstOrDefault(File.Exists) is not { } ps)
            {
                return null;
            }

            parentOf = p => Ask(ps, "ppid=", p) is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parent) ? parent : null;
        }

        var chain = new List<int>();
        var current = pid;
        while (chain.Count < HopLimit)
        {
            if (parentOf(current) is not { } parent)
            {
                return null;
            }

            if (parent <= 0)
            {
                return chain;
            }

            if (parent == pid || chain.Contains(parent))
            {
                return null;
            }

            chain.Add(parent);
            if (parent == 1 || until?.Invoke(parent) == true)
            {
                return chain;
            }

            current = parent;
        }

        return chain;
    }

    /// <summary>
    /// The walk asked of the kernel, which describes every user's process, so a process it does not know has exited and the walk
    /// is unanswered. Each hop is a separate question, so a parent pid that exits and is reused between two of them would hand
    /// the walk a stranger's ancestry; a parent never starts after its child, so a hop that does ends the walk unanswered.
    /// </summary>
    internal static List<int>? KernelWalk(int pid, Func<int, bool>? until, Func<int, (int Parent, long StartMicros)?> hop)
    {
        if (hop(pid) is not var (parent, childStart))
        {
            return null;
        }

        var chain = new List<int>();
        while (chain.Count < HopLimit)
        {
            if (parent <= 0)
            {
                return chain;
            }

            if (parent == pid || chain.Contains(parent)
                || hop(parent) is not var (grandparent, start) || start > childStart)
            {
                return null;
            }

            chain.Add(parent);
            if (parent == 1 || until?.Invoke(parent) == true)
            {
                return chain;
            }

            childStart = start;
            parent = grandparent;
        }

        return chain;
    }

    private static (int Parent, long StartMicros)? KernelHop(int pid) =>
        NativeProcInfo.TryLookupMicros(pid, out var parent, out long start) ? (parent, start) : null;

    public string? StartTime(long pid)
    {
        if (pid <= 1 || pid > int.MaxValue)
        {
            return null;
        }

        return ProcessSnapshotScope.ActivePass is { } pass ? pass.PerPid("start", (int)pid, StartTimeNow) : StartTimeNow((int)pid);
    }

    private string? StartTimeNow(int pid)
    {
        if (ProcessSnapshotScope.Active is { } slot)
        {
            return SnapRow(slot, pid, full: false) is { } row ? row.Start : null;
        }

        if (NativeProcInfo.Supported)
        {
            return NativeProcInfo.TryLookup(pid, out _, out var kernelStart) ? kernelStart : null;
        }

        return PsCandidates.FirstOrDefault(File.Exists) is { } ps ? Ask(ps, "lstart=", pid, PsUtcEnvironment) : null;
    }

    public string? LocalStartTime(long pid)
    {
        if (pid <= 1 || pid > int.MaxValue)
        {
            return null;
        }

        if (ProcessSnapshotScope.Active is { } slot)
        {
            return SnapRow(slot, (int)pid, full: false) is { } row ? InLocalZone(row.Start) : null;
        }

        if (NativeProcInfo.Supported)
        {
            return NativeProcInfo.TryLookup((int)pid, out _, out var kernelStart) ? InLocalZone(kernelStart) : null;
        }

        return PsCandidates.FirstOrDefault(File.Exists) is { } ps ? Ask(ps, "lstart=", (int)pid, PsEnvironment) : null;
    }

    /// <summary>A start time as the snapshot holds it (UTC, <c>ps -o lstart=</c> spacing) written in the local zone, the text <c>ps</c> prints without <c>TZ=UTC</c>.</summary>
    private static string? InLocalZone(string utcStart)
    {
        var normal = string.Join(' ', utcStart.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!DateTime.TryParseExact(normal, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        return string.Create(CultureInfo.InvariantCulture, $"{local:ddd MMM} {local.Day,2} {local:HH:mm:ss yyyy}");
    }

    public string? Tty(int pid)
    {
        if (pid <= 1)
        {
            return null;
        }

        if (ProcessSnapshotScope.Active is { } slot && HeldRow(slot, pid) is { } row)
        {
            return row.Tty;
        }

        return PsCandidates.FirstOrDefault(File.Exists) is { } ps ? TtyName(Ask(ps, "tty=", pid)) : null;
    }

    public string? Command(int pid)
    {
        if (pid <= 1)
        {
            return null;
        }

        if (ProcessSnapshotScope.Active is { } slot && HeldRow(slot, pid) is { } row)
        {
            return row.Command;
        }

        return PsCandidates.FirstOrDefault(File.Exists) is { } ps ? Ask(ps, "command=", pid, wide: true) : null;
    }

    /// <summary>One-pid asks a scope makes before a command or terminal question takes the reading of every process.</summary>
    private const int OnePidAsks = 4;

    /// <summary>
    /// The row of <paramref name="pid"/> for a command or terminal question: from a reading already in hand (the scope's own, or for a
    /// shared scope one taken in the last second); else, for a scope's first <see cref="OnePidAsks"/> such questions, null so the
    /// caller asks the one pid; else from the scope's reading. A reading of every process can outlast the ps limit on a loaded
    /// machine, where a one-pid ask does not, so a scope with few questions never waits on one.
    /// </summary>
    // ponytail: a fixed budget per scope; replace with a cost estimate if scopes asking 2-4 pids show up in ps fork counts.
    private Row? HeldRow(ProcessSnapshotScope.Slot slot, int pid)
    {
        if ((slot.Held<Snapshot>() ?? (slot.Shared ? Warm() : null)) is { } held && held.Rows.TryGetValue(pid, out var row))
        {
            return row;
        }

        return slot.CountAsk() > OnePidAsks ? SnapRow(slot, pid, full: true) : null;
    }

    /// <summary>A terminal name, empty for the placeholders <c>ps</c> prints for a process without one, null when nothing was read.</summary>
    private static string? TtyName(string? text) => text is "??" or "?" or "-" ? string.Empty : text;

    private string? Ask(string ps, string field, int pid, Dictionary<string, string?>? environment = null, bool wide = false)
    {
        var pidText = pid.ToString(CultureInfo.InvariantCulture);
        var result = runner.Run(new ProcessRequest(
            wide ? [ps, "-ww", "-o", field, "-p", pidText] : [ps, "-o", field, "-p", pidText],
            Environment: environment ?? PsEnvironment,
            Timeout: _psLimit));
        if (result.ExitCode != 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(result.Stdout.Span).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// One process of a reading. Its command line is decoded from the reading's bytes when first asked: a reading has about a
    /// thousand rows, a pass asks a handful, and the command lines are most of the reading.
    /// </summary>
    internal sealed class Row(int parent, string? tty, string start, ReadOnlyMemory<byte> command, int group = 0)
    {
        private string? _command;

        public int Parent => parent;

        public string? Tty => tty;

        public string Start => start;

        public int Group => group;

        public string? Command => command.IsEmpty ? null : (_command ??= Encoding.UTF8.GetString(command.Span).Trim()) is { Length: > 0 } text ? text : null;
    }

    private sealed record Snapshot(Dictionary<int, Row> Rows, long TakenAt);

    private static readonly long ShareTicks = Stopwatch.Frequency; // one second
    private static readonly long RefreshFloorTicks = Stopwatch.Frequency / 40; // 25 ms
    private readonly object _shareGate = new();
    private Snapshot? _shared;

    private static long Age(Snapshot s) => Stopwatch.GetTimestamp() - s.TakenAt;

    /// <summary>The shared reading while it is under a second old, else null.</summary>
    private Snapshot? Warm() => Volatile.Read(ref _shared) is { } s && Age(s) < ShareTicks ? s : null;

    /// <summary>
    /// The one reading a scope uses. A burst of calls shares one <c>ps -ax</c>: every call in the next second reuses it, so the
    /// daemon forks about one <c>ps</c> a second however many requests arrive.
    /// </summary>
    private Snapshot? ReadShared()
    {
        lock (_shareGate)
        {
            if (Warm() is { } warm)
            {
                return warm;
            }

            var fresh = Read();
            Volatile.Write(ref _shared, fresh);
            return fresh;
        }
    }

    /// <summary>
    /// The row of <paramref name="pid"/> in the scope's reading. Where the kernel can be asked, a row without terminal and command
    /// comes from the kernel alone, for any user's process, and a process it does not know has exited, so no <c>ps</c> is run. A
    /// <paramref name="full"/> row needs the reading; a shared scope that does not find the process in a reading older than 25 ms
    /// takes one new reading only when the process exists.
    /// </summary>
    private Row? SnapRow(ProcessSnapshotScope.Slot slot, int pid, bool full)
    {
        if (!full && NativeProcInfo.Supported)
        {
            return NativeProcInfo.TryLookup(pid, out var parent, out var start) ? new Row(parent, null, start, null) : null;
        }

        if ((slot.Shared ? slot.Get(ReadShared) : slot.Get(Read)) is not { } snapshot)
        {
            return null;
        }

        if (snapshot.Rows.TryGetValue(pid, out var row))
        {
            return row;
        }

        if (!slot.Shared || Age(snapshot) < RefreshFloorTicks || (NativeProcInfo.Supported && !NativeProcInfo.TryLookup(pid, out _, out _)))
        {
            return null;
        }

        lock (_shareGate)
        {
            if (Volatile.Read(ref _shared) is { } current && current.TakenAt != snapshot.TakenAt && current.Rows.TryGetValue(pid, out row))
            {
                return row;
            }

            var fresh = Read();
            Volatile.Write(ref _shared, fresh);
            return fresh is not null && fresh.Rows.TryGetValue(pid, out row) ? row : null;
        }
    }

    private Snapshot? _sharedGroups;

    /// <summary>
    /// The reading with process groups. Inside an open grouped span it is that span's own; inside a shared scope it is the one reading
    /// every shared scope in the last second reuses; anywhere else it is a new one, as a question that must be current expects.
    /// </summary>
    private Snapshot? GroupsReading()
    {
        if (ProcessSnapshotScope.ActiveGroups is { } slot)
        {
            return slot.GetOnceRead(() => Read(groups: true));
        }

        if (ProcessSnapshotScope.Active is not { Shared: true })
        {
            return Read(groups: true);
        }

        lock (_shareGate)
        {
            if (Volatile.Read(ref _sharedGroups) is { } warm && Age(warm) < ShareTicks)
            {
                return warm;
            }

            var fresh = Read(groups: true);
            Volatile.Write(ref _sharedGroups, fresh);
            return fresh;
        }
    }

    public IReadOnlyList<ProcessNode>? Descendants(int pid) => pid <= 1 || GroupsReading() is not { } snapshot ? null : DescendantsIn(snapshot.Rows, pid);

    public IReadOnlyList<ProcessNode>? All() => GroupsReading() is not { } snapshot ? null : [.. snapshot.Rows.Select(static r => NodeOf(r.Key, r.Value))];

    public IReadOnlyList<ProcessNode>? InGroup(int pgid) =>
        pgid <= 1 || GroupsReading() is not { } snapshot ? null : [.. snapshot.Rows.Where(r => r.Value.Group == pgid).Select(static r => NodeOf(r.Key, r.Value))];

    private static ProcessNode NodeOf(int pid, Row row)
    {
        var started = DateTime.TryParseExact(row.Start, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowInnerWhite | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? new DateTimeOffset(utc, TimeSpan.Zero)
            : (DateTimeOffset?)null;
        return new ProcessNode(pid, started, row.Command, row.Parent, row.Group);
    }

    internal static IReadOnlyList<ProcessNode> DescendantsIn(IReadOnlyDictionary<int, Row> rows, int pid)
    {
        var found = new List<ProcessNode>();
        var seen = new HashSet<int> { pid };
        var frontier = new Queue<int>([pid]);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var (child, row) in rows.Where(r => r.Value.Parent == parent))
            {
                if (!seen.Add(child))
                {
                    continue;
                }

                found.Add(NodeOf(child, row));
                frontier.Enqueue(child);
            }
        }

        return found;
    }

    internal static IReadOnlyDictionary<int, Row> RowsOf(string psOutput, bool groups = false) => RowsOf(Encoding.UTF8.GetBytes(psOutput), groups);

    /// <summary>The rows of one <c>ps</c> reading, read from its bytes: no string of the whole reading and none per line.</summary>
    internal static Dictionary<int, Row> RowsOf(ReadOnlyMemory<byte> psOutput, bool groups)
    {
        var rows = new Dictionary<int, Row>();
        var all = psOutput.Span;
        for (var at = 0; at <= all.Length;)
        {
            var next = all[at..].IndexOf((byte)'\n');
            var end = next < 0 ? all.Length : at + next;
            if (ParseRow(psOutput[at..end], groups) is { } parsed)
            {
                rows[parsed.Pid] = parsed.Row;
            }

            at = end + 1;
        }

        return rows;
    }

    /// <summary>
    /// One reading of every process: pid, parent, terminal, start time (UTC) and command line. Null when <c>ps</c> cannot be run,
    /// answers with an error, or its output was cut short.
    /// </summary>
    private Snapshot? Read() => Read(groups: false);

    private Snapshot? Read(bool groups)
    {
        if (PsCandidates.FirstOrDefault(File.Exists) is not { } ps)
        {
            return null;
        }

        var result = runner.Run(new ProcessRequest(
            [ps, "-axww", "-o", groups ? "pid=,ppid=,pgid=,lstart=,command=" : "pid=,ppid=,tty=,lstart=,command="],
            Environment: PsUtcEnvironment,
            Timeout: _psLimit,
            OutputCapBytes: 16 << 20));
        if (result.ExitCode != 0 || result.Truncated)
        {
            return null;
        }

        return new Snapshot(RowsOf(result.Stdout, groups), Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// One line of the reading. The start time is five fields (<c>Sat Oct  7 12:12:54 2026</c>); it is rebuilt with the day padded
    /// to two places, the spacing <c>ps -o lstart= -p</c> prints, so both ways of asking give the same text.
    /// </summary>
    private static (int Pid, Row Row)? ParseRow(ReadOnlyMemory<byte> line, bool groups)
    {
        // The first eight fields are split on spaces, each trimmed, empty ones dropped; the rest of the line is the command.
        var text = line.Span;
        Span<Range> fields = stackalloc Range[8];
        var count = 0;
        var at = 0;
        while (count < 8 && at < text.Length)
        {
            var space = text[at..].IndexOf((byte)' ');
            var end = space < 0 ? text.Length : at + space;
            var field = Trimmed(text, at, end);
            if (field.End.Value > field.Start.Value)
            {
                fields[count++] = field;
            }

            at = end + 1;
        }

        if (count < 8
            || !Number(text[fields[0]], out var pid)
            || !Number(text[fields[1]], out var parent)
            || !Number(text[fields[5]], out var day))
        {
            return null;
        }

        var start = string.Create(CultureInfo.InvariantCulture, $"{Text(text[fields[3]])} {Text(text[fields[4]])} {day,2} {Text(text[fields[6]])} {Text(text[fields[7]])}");
        var rest = at < text.Length ? Trimmed(text, at, text.Length) : default;
        var command = line[rest];
        if (groups)
        {
            return Number(text[fields[2]], out var group) ? (pid, new Row(parent, null, start, command, group)) : null;
        }

        return (pid, new Row(parent, TtyName(Text(text[fields[2]])), start, command));

        static string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes).Trim();

        // digits only, as NumberStyles.None reads them
        static bool Number(ReadOnlySpan<byte> bytes, out int value)
        {
            value = 0;
            long read = 0;
            foreach (var digit in bytes)
            {
                if (digit is < (byte)'0' or > (byte)'9' || (read = (read * 10) + (digit - '0')) > int.MaxValue)
                {
                    return false;
                }
            }

            value = (int)read;
            return bytes.Length > 0;
        }

        // the ASCII white space at both ends; a field that is only other white space still reads as empty once decoded and trimmed
        static Range Trimmed(ReadOnlySpan<byte> bytes, int from, int to)
        {
            while (from < to && bytes[from] is (byte)' ' or (>= 9 and <= 13))
            {
                from++;
            }

            while (to > from && bytes[to - 1] is (byte)' ' or (>= 9 and <= 13))
            {
                to--;
            }

            return from..to;
        }
    }
}
