using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch.Model;

/// <summary>
/// What the judge needs about a directory's members beyond the process table: the fresh heartbeats of pid-less sessions, and the panes herdr
/// shows running. <paramref name="Starting"/> names the members inside the starting window: the team slice carries no <c>spawned_at</c>, and
/// this changes the body, and so recomposes the summary, when a window ends with nothing else changing. <paramref name="QueueWaiting"/> counts, by
/// team, the queued items with no member to take them: the queue lane runs only while a client holds it, and the Home row reads summaries alone.
/// </summary>
public sealed record MemberFacts(IReadOnlyList<string> Heartbeats, IReadOnlyDictionary<string, string> Panes, IReadOnlyList<string> Starting, IReadOnlyDictionary<string, int>? QueueWaiting = null);

/// <summary>
/// The processes lane: one key per canonical hierarchy directory, one slice per key, <c>processes:&lt;dirId&gt;</c>, whose body is
/// <c>{pids:[{pid,start}]}</c> sorted by pid. The pids are the ones the directory's sessions recorded; <c>start</c> is the string the
/// process table prints for the live process (the string process identity compares), or null when the pid is not in the table. The
/// slice's <c>as_of</c> is 0. Each entry also says whether its command line is the harness's (<c>cmdline</c>: harness, not_harness or
/// unreadable), and the body says whether a process other than the daemon and pid 1 was seen in it (<c>observer_can_see_reference</c>),
/// which is what lets an absent pid prove anything. When the table was read is <see cref="ObservedAtMs"/>, outside the body.
/// <see cref="Poll"/> reads the process table once for every directory and queues each key; a re-read that follows uses that reading
/// instead of taking its own. <paramref name="memberFacts"/> adds what else the judge needs that only the host can read, once per poll:
/// <c>heartbeats</c>, the sessions of pid-less registrations with a fresh heartbeat, and <c>panes</c>, member name to why herdr shows it running.
/// </summary>
internal sealed class ProcessesLane(
    IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, IProcessTable table, Func<long>? nowMs = null, Func<string, MemberFacts>? memberFacts = null, Func<string, string?>? pidsStamp = null) : IModelLane
{
    /// <summary>Each dir's recorded pids with the stamp they were read at (b459 step 28): read again only when the stamp moves or cannot be had.</summary>
    private readonly Dictionary<string, (string Stamp, List<long> Pids)> _heldPids = new(StringComparer.Ordinal);

    private long _pidReads;

    /// <summary>How many times a dir's recorded pids were read rather than taken from the hold, for the tests of the hold.</summary>
    internal long PidReads => Interlocked.Read(ref _pidReads);

    /// <summary>Forgets every held pid list: the doctor's rebuild.</summary>
    internal void ForgetPids()
    {
        lock (_heldPids)
        {
            _heldPids.Clear();
        }
    }

    private volatile IReadOnlyDictionary<string, MemberFacts>? _tickFacts;
    private long _factsDueMs;

    /// <summary>The pids each dir's last poll read, taken by the first re-read of that dir after it: reading them again there doubled the poll's cost.</summary>
    private readonly Dictionary<string, List<long>> _polledPids = new(StringComparer.Ordinal);

    /// <summary>Each dir's pids as its last due poll read them; only <see cref="Poll"/> touches it, and the poller runs one poll at a time.</summary>
    private readonly Dictionary<string, List<long>> _lastPids = new(StringComparer.Ordinal);

    private long _pidsDueMs;

    public const string LaneName = "processes";

    private readonly Func<long> _now = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private volatile IReadOnlyDictionary<long, string?>? _tick;

    private long _tickAtMs;

    public string Name => LaneName;

    /// <summary>
    /// When the process table was last read, in epoch ms, or 0 before any reading. It is kept beside the slice, never in it: every poll moves
    /// it, and a body that carried it would republish on every poll.
    /// </summary>
    public long ObservedAtMs => Interlocked.Read(ref _tickAtMs);

    public static string SliceIdOf(string dir) => "processes:" + ActivityLane.DirId(dir);

    public IReadOnlyList<string> SliceIds(string key) => [SliceIdOf(key)];

    public IReadOnlyList<string> Keys() => [.. canonicalDirs().Distinct(StringComparer.Ordinal)];

    /// <summary>Takes one process-table reading for every directory's pids and queues every key through <paramref name="emit"/>.</summary>
    public void Poll(Action<string, string> emit)
    {
        using var pass = ProcessSnapshotScope.BeginPass();
        var dirs = Keys();

        // Reads the peers roster of every dir: like the member facts it waits ten times what the last reading cost, and until then the
        // last pids stand, so a new session's pid can be seen that much late. A dir with no reading yet is read at once.
        var pidsDue = _now() >= _pidsDueMs;
        var pidsStarted = _now();
        using (ProcessSnapshotScope.Begin())
        {
            var starts = new Dictionary<long, string?>();
            foreach (var dir in dirs)
            {
                if (pidsDue || !_lastPids.TryGetValue(dir, out var pids))
                {
                    pids = PidsOf(dir);
                    _lastPids[dir] = pids;
                }

                lock (_polledPids)
                {
                    _polledPids[dir] = pids;
                }

                foreach (var pid in pids)
                {
                    if (!starts.ContainsKey(pid))
                    {
                        starts[pid] = table.StartTime(pid);
                    }
                }
            }

            _tick = starts;
            Interlocked.Exchange(ref _tickAtMs, _now());
        }

        if (pidsDue)
        {
            var finished = _now();
            _pidsDueMs = finished + (10 * (finished - pidsStarted));
        }

        // Reads every team of every dir: it waits ten times what the last reading cost, so it never takes more than a tenth of a thread. Until then the last facts stand.
        if (memberFacts is not null && _now() >= _factsDueMs)
        {
            var started = _now();
            _tickFacts = dirs.ToDictionary(static d => d, memberFacts, StringComparer.Ordinal);
            var finished = _now();
            _factsDueMs = finished + (10 * (finished - started));
        }

        foreach (var dir in dirs)
        {
            emit(Name, dir);
        }
    }

    public LaneResult Reread(string key, long nowMs)
    {
        var id = SliceIdOf(key);
        if (!canonicalDirs().Contains(key, StringComparer.Ordinal))
        {
            return new LaneResult([new SliceChange(id, null)]) { Record = LaneRecord.Absent };
        }

        List<long>? polled;
        lock (_polledPids)
        {
            _ = _polledPids.Remove(key, out polled);
        }

        var pids = polled ?? PidsOf(key);
        var tick = _tick;
        var starts = new Dictionary<long, string?>();
        var list = new JsonArray();
        var sawAnother = false;

        // Shared, command lines included: outside a scope each live pid of each key forked its own ps on every poll.
        using (ProcessSnapshotScope.Begin(shared: true))
        {
            foreach (var pid in pids)
            {
                starts[pid] = tick is not null && tick.TryGetValue(pid, out var known) ? known : table.StartTime(pid);
            }

            foreach (var pid in pids)
            {
                sawAnother |= starts[pid] is not null && pid != Environment.ProcessId && pid > 1;
                list.Add(new JsonObject
                {
                    ["pid"] = pid,
                    ["start"] = starts[pid] is { } start ? JsonValue.FromString(start) : JsonValue.Null,
                    ["cmdline"] = CmdlineOf(pid, starts[pid]),
                });
            }
        }

        _ = Interlocked.CompareExchange(ref _tickAtMs, _now(), 0);
        var body = new JsonObject
        {
            ["pids"] = JsonValue.FromArray(list),
            ["observer_can_see_reference"] = sawAnother,
        };
        if (FactsOf(key) is { } facts)
        {
            if (facts.Heartbeats.Count > 0)
            {
                body["heartbeats"] = Sorted(facts.Heartbeats);
            }

            if (facts.Panes.Count > 0)
            {
                var panes = new JsonObject();
                foreach (var (member, why) in facts.Panes.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    panes[member] = why;
                }

                body["panes"] = JsonValue.FromObject(panes);
            }

            if (facts.Starting.Count > 0)
            {
                body["starting"] = Sorted(facts.Starting);
            }

            if (facts.QueueWaiting is { Count: > 0 } queueWaiting)
            {
                var counts = new JsonObject();
                foreach (var (team, count) in queueWaiting.OrderBy(static q => q.Key, StringComparer.Ordinal))
                {
                    counts[team] = JsonValue.FromNumber(count);
                }

                body["queue_waiting"] = JsonValue.FromObject(counts);
            }
        }

        return new LaneResult([new SliceChange(id, JsonText.ToUtf8(body, 0), 0)]) { Record = new LaneRecord(JsonValue.FromObject(body), 0) };
    }

    private static JsonValue Sorted(IReadOnlyList<string> texts)
    {
        var list = new JsonArray();
        foreach (var text in texts.Order(StringComparer.Ordinal))
        {
            list.Add(JsonValue.FromString(text));
        }

        return JsonValue.FromArray(list);
    }

    private MemberFacts? FactsOf(string dir) =>
        _tickFacts is { } tick && tick.TryGetValue(dir, out var facts) ? facts : memberFacts?.Invoke(dir);

    /// <summary><c>harness</c> or <c>not_harness</c> when the process is in the table and its command line could be read; else <c>unreadable</c>.</summary>
    private string CmdlineOf(long pid, string? start) =>
        start is null || pid > int.MaxValue || table.Command((int)pid) is not { Length: > 0 } command
            ? "unreadable"
            : Ah.Mesh.MeshModule.IsHarnessCommand(command) ? "harness" : "not_harness";

    private List<long> PidsOf(string dir)
    {
        // read before the pids: a write between the two leaves them on the older stamp, so the next read takes them again
        var stamp = pidsStamp?.Invoke(dir);
        if (stamp is not null)
        {
            lock (_heldPids)
            {
                if (_heldPids.TryGetValue(dir, out var held) && held.Stamp == stamp)
                {
                    return held.Pids;
                }
            }
        }

        _ = Interlocked.Increment(ref _pidReads);
        var pids = ReadPids(dir);
        if (stamp is not null)
        {
            lock (_heldPids)
            {
                _heldPids[dir] = (stamp, pids);
            }
        }

        return pids;
    }

    private List<long> ReadPids(string dir)
    {
        var result = new SortedSet<long>();
        var read = reader.Read("session-pids", "pids", dir, new JsonObject { ["dir"] = dir });
        if (read.Member("sessions") is { IsArray: true } sessions)
        {
            foreach (var session in sessions.AsArray())
            {
                if (session.Member("pid") is { IsNumber: true } pid && pid.AsNumber() > 0 && pid.AsNumber() == Math.Floor(pid.AsNumber()))
                {
                    _ = result.Add((long)pid.AsNumber());
                }
            }
        }

        // The pid an up or down peers row names is observed too, for a member that has no registration.
        if (reader.Read("peers", "latest", dir, new JsonObject { ["dir"] = dir }) is { IsArray: true } rows)
        {
            foreach (var row in rows.AsArray())
            {
                if (row.Member("status") is { IsText: true } status && status.AsString() is "up" or "down"
                    && row.Member("pid") is { IsNumber: true } pid && pid.AsNumber() > 0 && pid.AsNumber() == Math.Floor(pid.AsNumber()))
                {
                    _ = result.Add((long)pid.AsNumber());
                }
            }
        }

        return [.. result];
    }
}
