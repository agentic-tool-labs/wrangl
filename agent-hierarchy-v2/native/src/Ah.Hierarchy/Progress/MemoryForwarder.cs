using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy.StateCalls;

namespace Ah.Hierarchy;

/// <summary>Which memory adapter a repo's <c>agent-hierarchy.json</c> asks for: <c>auto</c>, <c>engram</c>, <c>file</c>, <c>none</c>, or a command.</summary>
internal enum MemoryMode
{
    Auto,
    Engram,
    File,
    None,
    Command,
}

internal sealed record MemorySpec(MemoryMode Mode, IReadOnlyList<string> Command)
{
    public static readonly MemorySpec Default = new(MemoryMode.Auto, []);
}

/// <summary>
/// Reads <c>memory.adapter</c> from the project's and the user's <c>agent-hierarchy.json</c>; the project's wins. A <c>command</c>
/// adapter is honoured only from the user's file: the project's file is committed to the repo, and it must not be able to choose a
/// program the daemon runs.
/// </summary>
internal static class MemoryConfig
{
    public static MemorySpec Read(IFileReader files, string pool, string? userHome)
    {
        var layers = new List<(string Path, bool AllowCommand)> { (NodePath.Join(pool, "agent-hierarchy.json"), false) };
        if (!string.IsNullOrEmpty(userHome))
        {
            layers.Add((NodePath.Join(userHome, ".claude", "ah2", "agent-hierarchy.json"), true));
        }

        foreach (var (path, allowCommand) in layers)
        {
            try
            {
                if (!files.Exists(path) || JsonText.Parse(files.ReadText(path)).Member("memory")?.Member("adapter") is not { } adapter)
                {
                    continue;
                }

                var spec = Parse(adapter);
                if (spec.Mode == MemoryMode.Command && !allowCommand)
                {
                    continue;
                }

                return spec;
            }
            catch (Exception e) when (e is JsonParseException or IOException or UnauthorizedAccessException)
            {
                // an unreadable layer says nothing; the next one, or the default, applies
            }
        }

        return MemorySpec.Default;
    }

    private static MemorySpec Parse(JsonValue adapter)
    {
        if (adapter is { IsText: true })
        {
            return adapter.AsString() switch
            {
                "engram" => new MemorySpec(MemoryMode.Engram, []),
                "file" => new MemorySpec(MemoryMode.File, []),
                "none" => new MemorySpec(MemoryMode.None, []),
                _ => MemorySpec.Default,
            };
        }

        if (adapter.Member("command") is { IsArray: true } argv)
        {
            var words = argv.AsArray().Where(a => a.IsText).Select(a => a.AsString()).ToList();
            if (words.Count > 0)
            {
                return new MemorySpec(MemoryMode.Command, words);
            }
        }

        return MemorySpec.Default;
    }
}

/// <summary>
/// Adapter children started and their summed wall time, counted per forwarding run: <see cref="EndRun"/> closes the run in progress and
/// <see cref="Last"/> reads the last closed one, so a report shows one run's numbers and never a running total. Held in memory only, so
/// a restart starts from zero.
/// </summary>
internal sealed class ForwardStats
{
    private readonly object gate = new();
    private long children;
    private long wallMs;
    private (long Children, long WallMs) last;

    public void Add(long ms)
    {
        lock (gate)
        {
            children++;
            wallMs += ms;
        }
    }

    public (long Children, long WallMs) EndRun()
    {
        lock (gate)
        {
            last = (children, wallMs);
            children = 0;
            wallMs = 0;
            return last;
        }
    }

    public (long Children, long WallMs) Last()
    {
        lock (gate)
        {
            return last;
        }
    }
}

/// <summary>
/// Counts, per ledger, the consecutive runs that failed at one cursor position, and says once when that count reaches
/// <see cref="Threshold"/>: a ledger the adapter keeps refusing is otherwise silent. Held by the long-lived records object, so the
/// count survives from one forwarding run to the next.
/// </summary>
internal sealed class StuckLedgers
{
    public const int Threshold = 5;

    private readonly Dictionary<string, (long Offset, int Runs)> runs = [];

    /// <summary>Notes a failed run at <paramref name="offset"/>; the number of consecutive failed runs at that offset so far.</summary>
    public int Failed(string key, long offset)
    {
        lock (runs)
        {
            var count = runs.TryGetValue(key, out var seen) && seen.Offset == offset ? seen.Runs + 1 : 1;
            runs[key] = (offset, count);
            return count;
        }
    }

    private readonly HashSet<string> salvaging = [];

    /// <summary>True while a refused batch of this ledger is being delivered piece by piece over several runs.</summary>
    public bool IsSalvaging(string key)
    {
        lock (runs)
        {
            return salvaging.Contains(key);
        }
    }

    public void SalvageUnderway(string key)
    {
        lock (runs)
        {
            _ = salvaging.Add(key);
        }
    }

    private readonly Dictionary<string, long> candidates = [];

    /// <summary>True when the ledger row <paramref name="at"/> was the refused line of an earlier run of this ledger's salvage.</summary>
    public bool IsCandidate(string key, long at)
    {
        lock (runs)
        {
            return candidates.TryGetValue(key, out var seen) && seen == at;
        }
    }

    public void SetCandidate(string key, long at)
    {
        lock (runs)
        {
            candidates[key] = at;
        }
    }

    public void ClearCandidate(string key)
    {
        lock (runs)
        {
            _ = candidates.Remove(key);
        }
    }

    public void Cleared(string key)
    {
        lock (runs)
        {
            _ = runs.Remove(key);
            _ = salvaging.Remove(key);
            _ = candidates.Remove(key);
        }
    }
}

/// <summary>
/// Hands each team's new complete ledger lines (up to 1 MiB) to the memory adapter in one run, newline-separated on stdin. The batch
/// counts as delivered only when the adapter exits 0 within the timeout; the cursor then moves past it once. A failure or a timeout
/// leaves the cursor, and the same batch is sent next run. The adapter runs only when a ledger has new bytes.
/// </summary>
internal sealed class MemoryForwarder(IProgressLogSource? logs, IHierarchyStateCalls calls, IProcessRunner runner, StuckLedgers? stuck = null, Action<string>? onStuck = null, ForwardStats? stats = null, string? path = null, Action<string, string>? onAlarm = null, Func<string, string, bool>? isOff = null)
{
    // The adapter's exit codes that say it cannot work at all (75: a tool it needs is missing; 127: it could not start), as opposed to
    // refusing what it was given.
    private static readonly int[] BrokenExits = [75, 127];

    /// <summary>The most adapter runs one forwarding run of one ledger spends splitting a refused batch.</summary>
    public const int SalvageChildren = 16;

    private string lastFailure = string.Empty;

    private bool lastBroken;

    public static readonly TimeSpan AdapterTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PerLineTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan MaxBatchTimeout = TimeSpan.FromSeconds(60);

    public const string EngramScript = "ah-memory-engram";

    /// <summary>The argv (without <c>record</c>) the spec resolves to, or null when nothing is to be forwarded.</summary>
    public static IReadOnlyList<string>? Resolve(IFileReader files, MemorySpec spec, string pluginRoot, string? path)
    {
        var script = NodePath.Join(pluginRoot, "bin", EngramScript);
        switch (spec.Mode)
        {
            case MemoryMode.Command:
                return spec.Command;
            case MemoryMode.Engram:
                return files.Exists(script) ? [script] : null;
            case MemoryMode.Auto:
                return files.Exists(script) && OnPath(files, "engram", path) ? [script] : null;
            default:
                return null;
        }
    }

    private static bool OnPath(IFileReader files, string name, string? path) =>
        (path ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries).Any(dir => files.IsExecutable(NodePath.Join(dir, name)));

    /// <summary>Forwards every team ledger under <paramref name="pool"/>; returns the lines delivered.</summary>
    public int Run(string pool, Func<IReadOnlyList<string>?> argvOf, string repo, TimeSpan? timeout = null)
    {
        var delivered = 0;
        var resolved = false;
        IReadOnlyList<string>? argv = null;
        IReadOnlyList<string>? Once()
        {
            if (!resolved)
            {
                argv = argvOf();
                resolved = true;
            }

            return argv;
        }

        var root = NodePath.Join(pool, "progress");
        // b435: the teams from the database only (a null log takes no old file); a team with no ledger row forwards nothing
        foreach (var team in logs?.Teams(pool, null) ?? [])
        {
            if (isOff?.Invoke(pool, team) == true)
            {
                continue;
            }

            delivered += RunTeam(pool, root, team, Once, repo, timeout ?? AdapterTimeout);
        }

        return delivered;
    }

    private int RunTeam(string pool, string root, string team, Func<IReadOnlyList<string>?> argvOf, string repo, TimeSpan timeout)
    {
        // per-dir-files-to-db S11c: the cursor and each row's At are ledger row seqs, no longer byte offsets
        // the cursor and the rows in one read: a cursor that cannot be read forwards nothing, never the whole ledger again
        if (logs!.LedgerAfterCursor(pool, team) is not { } since)
        {
            return 0;
        }

        var offset = since.Cursor;
        var key = pool + "|" + team;
        var rows = new List<(byte[] Bytes, long At)>();
        var batch = 0L;
        foreach (var row in since.Rows)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(row.Body + "\n");
            if (rows.Count > 0 && batch + bytes.Length > 1 << 20)
            {
                break;
            }

            batch += bytes.Length;
            rows.Add((bytes, row.Seq));
        }

        if (rows.Count == 0)
        {
            stuck?.Cleared(key);
            return 0;
        }

        var end = rows[^1].At;

        var lines = rows.Count;
        if (lines > 0)
        {
            if (argvOf() is not { } argv)
            {
                return 0;
            }

            TimeSpan Limit(int count) => TimeSpan.FromTicks(Math.Min(timeout.Ticks + count * PerLineTimeout.Ticks, MaxBatchTimeout.Ticks));
            bool Send(int from, int to) => Deliver(argv, [.. rows.Skip(from).Take(to - from).SelectMany(r => r.Bytes)], repo, team, root, Limit(to - from));

            if (!Send(0, lines))
            {
                var runs = stuck?.Failed(key, offset) ?? 0;
                var reason = lastFailure;
                if ((runs >= StuckLedgers.Threshold || stuck?.IsSalvaging(key) == true) && lines > 1 && !lastBroken
                    && Salvage(rows, Send, team, key) is var (done, skipped) && done > 0)
                {
                    if (done == lines)
                    {
                        WriteCursor(pool, team, end);
                        stuck?.Cleared(key);
                    }
                    else
                    {
                        WriteCursor(pool, team, rows[done - 1].At);
                        stuck?.SalvageUnderway(key);
                    }

                    return done - skipped;
                }

                if (runs == StuckLedgers.Threshold)
                {
                    var refused = $"the memory adapter has refused the ledger batch after row {offset} for {StuckLedgers.Threshold} runs in a row{reason}";
                    onStuck?.Invoke($"{team}: {refused}");
                    onAlarm?.Invoke(team, refused);
                }

                return 0;
            }
        }

        WriteCursor(pool, team, end);
        stuck?.Cleared(key);
        return lines;
    }

    /// <summary>
    /// A batch the adapter keeps refusing: delivers it again in halves, down to single lines, left to right, in at most
    /// <see cref="SalvageChildren"/> adapter runs. A line refused twice on its own is skipped and logged only when a piece after it was
    /// accepted in the same run (so the adapter works), it was also the refused line of an earlier run (so the refusal is not a passing
    /// failure such as a locked database), and no other line was skipped in this run. A refused line that does not qualify is
    /// remembered and left unsettled: the rows before it are settled and the cursor stops there. Returns how many leading rows are
    /// settled (delivered or skipped) and how many of those were skipped; the next run carries on from there. Null when the adapter
    /// looks broken or accepted nothing, so nothing is skipped.
    /// </summary>
    private (int Done, int Skipped)? Salvage(List<(byte[] Bytes, long At)> rows, Func<int, int, bool> send, string team, string key)
    {
        var budget = SalvageChildren;
        var accepted = false;
        var lastAcceptedFrom = -1;
        var done = 0;
        var refused = new List<int>();

        bool Try(int from, int to)
        {
            if (budget == 0)
            {
                return false;
            }

            budget--;
            if (send(from, to))
            {
                accepted = true;
                lastAcceptedFrom = Math.Max(lastAcceptedFrom, from);
                return true;
            }

            return false;
        }

        bool Walk(int from, int to)
        {
            if (Try(from, to))
            {
                done = to;
                return true;
            }

            if (lastBroken || budget == 0)
            {
                return false;
            }

            if (to - from == 1)
            {
                if (Try(from, to))
                {
                    done = to;
                    return true;
                }

                if (lastBroken)
                {
                    return false;
                }

                refused.Add(from);
                done = to;
                return true;
            }

            var mid = from + ((to - from) / 2);
            return Walk(from, mid) && Walk(mid, to);
        }

        var half = rows.Count / 2;
        _ = Walk(0, half) && Walk(half, rows.Count);
        if (lastBroken || !accepted)
        {
            return null;
        }

        var skipped = 0;
        foreach (var index in refused)
        {
            if (skipped == 0 && lastAcceptedFrom > index && stuck?.IsCandidate(key, rows[index].At) == true)
            {
                skipped++;
                stuck.ClearCandidate(key);
                onStuck?.Invoke($"{team}: skipped ledger row {rows[index].At}, which the memory adapter refused on its own in two runs while it accepted the rest{lastFailure}: {Describe(rows[index].Bytes)}");
                continue;
            }

            stuck?.SetCandidate(key, rows[index].At);
            done = Math.Min(done, index);
            break;
        }

        return (done, skipped);
    }

    /// <summary>The kind and the start of the text of a ledger line, or the start of the line when it is not an object with them.</summary>
    private static string Describe(byte[] line)
    {
        var raw = System.Text.Encoding.UTF8.GetString(line).Trim();
        string Clip(string s) => s.Length > 160 ? s[..160] + "..." : s;
        try
        {
            var row = JsonText.Parse(line);
            return row.Member("text") is { IsText: true } text
                ? $"{(row.Member("kind") is { IsText: true } kind ? kind.AsString() : "?")}: {Clip(text.AsString())}"
                : Clip(raw);
        }
        catch (JsonParseException)
        {
            return Clip(raw);
        }
    }

    private bool Deliver(IReadOnlyList<string> argv, byte[] lines, string repo, string team, string cacheDir, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var environment = new Dictionary<string, string?> { ["AH_MEMORY_REPO"] = repo, ["AH_MEMORY_TEAM"] = team, ["AH_MEMORY_CACHE_DIR"] = cacheDir };
            if (path is not null)
            {
                environment["PATH"] = path;
            }

            var result = runner.Run(new ProcessRequest([.. argv, "record"], Environment: environment, Stdin: lines, Timeout: timeout));
            if (result is { ExitCode: 0, TimedOut: false })
            {
                return true;
            }

            lastBroken = result.TimedOut || (result.ExitCode is { } code && BrokenExits.Contains(code));
            var stderr = System.Text.Encoding.UTF8.GetString(result.Stderr.Span).Trim();
            stderr = stderr.Length > 300 ? "..." + stderr[^300..] : stderr;
            lastFailure = $" (exit {(result.TimedOut ? "timeout" : result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "signal")}{(stderr.Length > 0 ? ": " + stderr : string.Empty)})";
            return false;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lastBroken = true;
            lastFailure = $" ({e.Message})";
            return false;
        }
        finally
        {
            stats?.Add(clock.ElapsedMilliseconds);
        }
    }

    private void WriteCursor(string pool, string team, long seq) =>
        calls.Progress("ledger-cursor", new JsonObject { ["dir"] = pool, ["team"] = team, ["seq"] = seq });
}
