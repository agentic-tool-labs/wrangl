using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.DaemonClient;

/// <summary>The files, sockets and processes a real heal run touches, for the state home the client was made for.</summary>
internal sealed class AhdHealEnvironment(AhdClient client, DaemonClientDependencies deps, RunPaths paths, Action<string> progress) : IHealEnvironment
{
    private const int LogCopyBytes = 10 * 1024 * 1024;

    private string StateFile => Path.Combine(paths.Run, "heal-state.json");

    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public DateTimeOffset Now => deps.Clock.UtcNow;

    public TimeSpan Elapsed => _elapsed.Elapsed;

    public Task DelayAsync(TimeSpan span) => Task.Delay(span);

    public HealTarget? ReadTarget()
    {
        var info = client.ReadInfo();
        if (info?.Int("pid") is not { } pid || info.Str("started_at") is not { Length: > 0 } started
            || !DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return null;
        }

        // The uptime is counted from the later of the recorded start and the process table's: a record that names a start far in
        // the past (a stale or hand-edited ahd.json) cannot shorten the grace.
        var procStart = deps.StartTime?.Invoke((int)pid);
        if (ProcessStart.TryParseUtc(procStart, out var procAt) && procAt > at)
        {
            at = procAt;
        }

        return new HealTarget((int)pid, started, at, procStart);
    }

    /// <summary>
    /// The same process still: alive, still the one <c>ahd.json</c> names with the same recorded start, its command line this state
    /// home's daemon, and, where the process table gives start times, the start it had when the run began (a reused pid has another).
    /// A <c>ps</c> that cannot answer counts as not holding: on doubt nothing is signalled.
    /// </summary>
    public bool IdentityHolds(HealTarget target)
    {
        var info = client.ReadInfo();
        return deps.Inspector.PidAlive(target.Pid)
            && info?.Int("pid") == target.Pid
            && info.Str("started_at") == target.StartedAt
            && client.IsOurDaemon(target.Pid)
            && (target.ProcStart is null || deps.StartTime is not { } startOf
                || ProcessStart.Compare(startOf(target.Pid), static () => null, target.ProcStart) == StartMatch.Same);
    }

    public bool Alive(int pid) => deps.Inspector.PidAlive(pid);

    public string Home => paths.Home;

    public bool LifeHeld() => client.LifeHeld();

    public bool Gone(HealTarget target) => !deps.Inspector.PidAlive(target.Pid);

    public bool SameProcess(HealTarget target) =>
        target.ProcStart is null || deps.StartTime is not { } startOf
            ? IdentityHolds(target)
            : deps.Inspector.PidAlive(target.Pid)
                && ProcessStart.Compare(startOf(target.Pid), static () => null, target.ProcStart) == StartMatch.Same;

    public async Task<HealProbe> ProbeAsync(TimeSpan deadline)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var reply = await client
                .HttpJsonAsync(paths.Sock, "GET", "/v1/health", null, (int)deadline.TotalMilliseconds, CancellationToken.None)
                .ConfigureAwait(false);
            // A complete reply of any status is an answer: a draining daemon or one whose health handler errors is serving, not hung.
            _ = reply;
            return new HealProbe(true, clock.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return HealProbe.Miss(clock.ElapsedMilliseconds);
        }
    }

    /// <summary>One signal to one process through the platform call: never a <c>kill</c> found on PATH, and never pid 0, 1 or a negative pid (a process group, or everything).</summary>
    public void Signal(int pid, bool kill)
    {
        if (pid > 1)
        {
            _ = deps.Signal?.Invoke(pid, kill ? 9 : 15);
        }
    }

    public string CopyLog()
    {
        try
        {
            var tail = deps.Files.ReadTail(paths.Log, LogCopyBytes).Bytes;
            var copy = Path.Combine(paths.Home, "logs", "ahd-hung-" + Now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".log");
            deps.Writer.WriteAtomic(copy, copy + ".tmp", tail);
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public async Task<int?> StartAsync()
    {
        var found = await client.EnsureAsync(new EnsureOptions(6000, Explicit: true), CancellationToken.None).ConfigureAwait(false);
        return found?.Pid;
    }

    public HealState? ReadState() => HealStateFile.Read(deps.Files, StateFile);

    public void WriteState(HealState state)
    {
        deps.Writer.Mkdir(paths.Run);
        deps.Writer.WriteAtomic(StateFile, StateFile + ".tmp", HealStateFile.Bytes(state));
    }

    /// <summary>
    /// One row of the stall journal. The journal is a table of the state database, which a hung daemon may hold: a row that cannot
    /// be written goes to the heal's own output (its log, when it was launched detached) and never stops the run.
    /// </summary>
    public void Journal(JsonObject row)
    {
        row["at"] = Now.ToString("o", CultureInfo.InvariantCulture);
        try
        {
            if (deps.HealJournal is { } journal)
            {
                journal(row);
                return;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            progress("journal row not written (" + e.Message + ")");
        }

        progress("journal: " + JsonText.Stringify(row, 0));
    }

    public void Progress(string line) => progress(line);

    public LifeRecord? ReadLife()
    {
        try
        {
            var path = Path.Combine(paths.Run, "ahd.life.json");
            if (!deps.Files.Exists(path) || !JsonText.TryParse(deps.Files.ReadBytes(path), out var parsed) || parsed.AsObject() is not { } doc)
            {
                return null;
            }

            return doc.Int("pid") is { } pid && doc.Str("start") is { } start && doc.Str("home") is { } home ? new LifeRecord((int)pid, start, home) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? StartOf(int pid) => deps.StartTime?.Invoke(pid);

    public bool IsOurDaemon(int pid) => client.IsOurDaemon(pid);

    public bool AskerIsUser() => deps.CallerIsUser?.Invoke() == true;

    public int OwnPid => Environment.ProcessId;

    public void LogLifeKill(HealTarget target, string reason) => client.LogLifeKill(target.Pid, target.StartedAt, reason);
}

/// <summary><c>run/heal-state.json</c>: <c>{restarts: [iso…], last_capped_row?, last?: {kind, at, detail, log_copy}}</c>. The heal writes it under its lock; the doctor row reads it.</summary>
internal static class HealStateFile
{
    /// <summary>
    /// The state, empty when there is no file, and null when the file is there and cannot be read: not JSON, no <c>restarts</c>
    /// array, or an entry of it that is not a time. A file of the wrong shape could hide any number of restarts.
    /// </summary>
    internal static HealState? Read(IFileReader files, string path)
    {
        byte[] bytes;
        try
        {
            if (!files.Exists(path))
            {
                return HealState.Empty;
            }

            bytes = files.ReadBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!JsonText.TryParse(bytes, out var parsed) || parsed.AsObject() is not { } doc)
        {
            return null;
        }

        if (!doc.TryGetValue("restarts", out var list) || !list.IsArray)
        {
            return null;
        }

        List<DateTimeOffset> restarts = [];
        foreach (var item in list.AsArray())
        {
            if (!item.IsText || Time(item.AsString()) is not { } at)
            {
                return null;
            }

            restarts.Add(at);
        }

        var last = doc.Map("last") is { } l && l.Str("kind") is { Length: > 0 } kind && Time(l.Str("at")) is { } lat
            ? new HealLast(kind, lat, l.Str("detail") ?? string.Empty, l.Str("log_copy") ?? string.Empty)
            : null;
        return new HealState(restarts, Time(doc.Str("last_capped_row")), last);
    }

    internal static byte[] Bytes(HealState state)
    {
        var doc = new JsonObject
        {
            ["restarts"] = Jx.Arr(state.Restarts.Select(static t => (JsonValue)Iso(t))),
        };
        if (state.LastCappedRow is { } capped)
        {
            doc["last_capped_row"] = Iso(capped);
        }

        if (state.Last is { } last)
        {
            doc["last"] = Jx.Obj(("kind", last.Kind), ("at", Iso(last.At)), ("detail", last.Detail), ("log_copy", last.LogCopy));
        }

        return JsonText.ToUtf8(JsonValue.FromObject(doc), 0).ToArray();
    }

    private static string Iso(DateTimeOffset at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(string? text) =>
        text is { Length: > 0 } && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}
