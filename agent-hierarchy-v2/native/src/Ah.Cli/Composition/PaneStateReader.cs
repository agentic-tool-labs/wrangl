using Ah.Abstractions;
using Ah.Commands;
using Ah.Core.Text;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Composition;

/// <summary>One process of the process table: its pid, its parent's pid and its full command line.</summary>
internal sealed record ProcessRow(int Pid, int Parent, string Command);

/// <summary>
/// Pane questions answered from one multiplexer listing per transport and one process-table reading, each taken the first time a
/// question needs it and kept for the rest of the run.
/// </summary>
internal sealed class PaneStateReader(Func<string, PaneListing> list, Func<IReadOnlyList<ProcessRow>?> processes, Func<IReadOnlyDictionary<int, string>?>? executables = null, Func<Ah.Core.Json.JsonArray>? agents = null) : IPaneState
{
    public Ah.Core.Json.JsonArray? Agents()
    {
        try
        {
            return agents?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private readonly Dictionary<string, PaneListing> _listings = new(StringComparer.Ordinal);

    private IReadOnlyList<ProcessRow>? _rows;

    private bool _read;

    private IReadOnlyDictionary<int, string>? _executables;

    /// <summary>The reader over the real multiplexers and <c>ps</c>.</summary>
    internal static PaneStateReader Over(IMuxRunner mux, IFileReader files, IAhEnvironment environment, IProcessRunner runner, IHerdr herdr)
    {
        string? defaultSocket;
        try
        {
            defaultSocket = $"{environment.HomeDirectory}/.config/herdr/herdr.sock";
        }
        catch (InvalidOperationException)
        {
            defaultSocket = null;
        }

        var panes = new PaneMux(
            mux, files, environment.GetVariable("TMUX_TMPDIR"), PlatformModule.CurrentUid(), defaultSocket,
            socket => DaemonServices.HerdrCallEnvironment(DaemonServices.ProcessEnvironment(), socket),
            herdr);
        return new PaneStateReader(transport => panes.Enumerate(transport, withPids: true), () => ReadProcesses(runner), () => ReadExecutables(runner), () => herdr.Agents());
    }

    public PaneReading Pane(string transport, string transportId)
    {
        var listing = Listing(transport);
        if (listing.Failure is not null)
        {
            return new PaneReading(PaneKnowledge.Unknown, null, $"the {transport} pane listing failed: {listing.Failure}");
        }

        if (listing.Panes.FirstOrDefault(p => p.PaneId == transportId) is { } found)
        {
            return new PaneReading(PaneKnowledge.Present, found.RootPid);
        }

        if (listing.Truncated)
        {
            return new PaneReading(PaneKnowledge.Unknown, null, $"pane {transportId} is not in the {transport} listing, which was cut at {PaneMux.MaxInstances} instances or {PaneMux.MaxPanesPerInstance} panes each, so it may be past the cut; `ah roster recover now <member>` re-checks just that pane");
        }

        return listing.Answered
            ? new PaneReading(PaneKnowledge.Absent, null)
            : new PaneReading(PaneKnowledge.Unknown, null, $"pane {transportId} is not in the {transport} listing, and at least one {transport} instance returned no usable pane list, so it may be in that one");
    }

    public IReadOnlyList<TabPane>? TabPanes(string transport, string tabId)
    {
        var listing = Listing(transport);
        return listing.Failure is not null || !listing.Answered
            ? null
            : listing.Panes.Where(p => p.TabId == tabId).Select(p => new TabPane(p.PaneId, p.RootPid)).ToList();
    }

    public void Refresh() => _listings.Clear();

    public bool? HarnessRunning(int rootPid)
    {
        if (!_read)
        {
            _rows = processes();
            _executables = executables?.Invoke();
            _read = true;
        }

        if (_rows is not { } rows)
        {
            return null;
        }

        var commands = new Dictionary<int, string>();
        var children = new Dictionary<int, List<int>>();
        foreach (var row in rows)
        {
            commands.TryAdd(row.Pid, row.Command);
            if (!children.TryGetValue(row.Parent, out var kids))
            {
                children[row.Parent] = kids = [];
            }

            kids.Add(row.Pid);
        }

        var paths = _executables;
        if (executables is not null && paths is null)
        {
            return null;
        }

        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!seen.Add(pid))
            {
                continue;
            }

            if (MeshModule.IsHarnessProcess(pid, p => commands.GetValueOrDefault(p), paths is null ? null : p => paths.GetValueOrDefault(p)))
            {
                return true;
            }

            if (children.TryGetValue(pid, out var below))
            {
                foreach (var child in below)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return false;
    }

    private PaneListing Listing(string transport)
    {
        if (!_listings.TryGetValue(transport, out var listing))
        {
            _listings[transport] = listing = list(transport);
        }

        return listing;
    }

    /// <summary>The executable path of every process in one <c>ps</c> reading (a path with a space cannot be told from a command line), or null when it failed.</summary>
    private static IReadOnlyDictionary<int, string>? ReadExecutables(IProcessRunner runner)
    {
        var result = runner.Run(new ProcessRequest(["ps", "-axww", "-o", "pid=,comm="], null, null, default, null, 16 * 1024 * 1024));
        return result is { ExitCode: 0, TimedOut: false, Truncated: false } ? ParseExecutables(Utf8Text.Decode(result.Stdout.Span)) : null;
    }

    internal static IReadOnlyDictionary<int, string> ParseExecutables(string text)
    {
        var paths = new Dictionary<int, string>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
            {
                paths.TryAdd(pid, parts[1]);
            }
        }

        return paths;
    }

    /// <summary>The whole process table in one <c>ps</c> reading, or null when it failed, timed out or was cut short.</summary>
    private static IReadOnlyList<ProcessRow>? ReadProcesses(IProcessRunner runner)
    {
        // -ww: ps cuts a command line to the terminal width otherwise.
        // TODO: read the process snapshot peer-proof adds (ProcessSnapshotScope) instead of this second ps shape, once that branch has landed here.
        var result = runner.Run(new ProcessRequest(["ps", "-axww", "-o", "pid=,ppid=,command="], null, null, default, null, 16 * 1024 * 1024));
        return result is { ExitCode: 0, TimedOut: false, Truncated: false } ? Parse(Utf8Text.Decode(result.Stdout.Span)) : null;
    }

    internal static IReadOnlyList<ProcessRow> Parse(string text)
    {
        var rows = new List<ProcessRow>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3
                && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
                && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parent))
            {
                rows.Add(new ProcessRow(pid, parent, parts[2]));
            }
        }

        return rows;
    }
}
