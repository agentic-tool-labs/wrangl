using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Mesh;

internal static partial class MeshRoutes
{
    private const int DueTickMs = 200;
    private const int DueMaxWaitSeconds = 25;
    private const string DueAdapter = "claude-mod";

    /// <summary>The watched key of a store in the database: the sessions counter, or a box's stamp. Anything else is a path to stat.</summary>
    private const string SessionsKey = "db:sessions";
    private const string BoxKeyPrefix = "db:box:";

    internal static void RegisterDue(IRouteRegistry routes, IMailDue due, IMailbox mailbox, IFileReader files, IDueTimer? timer = null, Action<string>? log = null)
    {
        var t = timer ?? SystemDueTimer.Instance;
        var scanner = new DueScanner(files, mailbox, t, log);
        routes.Add("GET /v1/due", (request, ct) => Due(request, due, mailbox, files, t, scanner, ct));
    }

    /// <summary>
    /// The one 200 ms tick behind every held <c>/v1/due</c> request. Each tick reads every distinct watched key once and wakes
    /// the requests whose keys changed or whose own wake time has passed. It runs only while a request is held. A store that cannot
    /// answer skips the comparing for that tick and is logged once per run of failures; nothing leaves the loop, whose death would
    /// leave <c>_running</c> true and every held request unwoken.
    /// </summary>
    private sealed class DueScanner(IFileReader files, IMailbox mailbox, IDueTimer timer, Action<string>? log)
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _oldMods = new(StringComparer.Ordinal);
        private readonly List<Held> _held = [];
        private bool _running;
        private bool _failing;

        /// <summary>Logs a session whose mod is older than this daemon expects, once per session per daemon run: only a session restart loads a new mod.</summary>
        internal void NoteMod(DueRequest asked)
        {
            if (asked.ModBuild >= DueRequest.CurrentModBuild)
            {
                return;
            }

            lock (_gate)
            {
                if (!_oldMods.Add(asked.Session))
                {
                    return;
                }
            }

            log?.Invoke($"session {asked.Session} runs an old mod (build {asked.ModBuild}, this daemon expects {DueRequest.CurrentModBuild}); restart that session to load the new one");
        }

        internal sealed class Held
        {
            internal string[] Paths { get; set; } = [];

            internal string Keys { get; set; } = string.Empty;

            internal long WakeAtMs { get; set; }

            internal TaskCompletionSource Signal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal Task WaitAsync(Held held, CancellationToken cancellationToken)
        {
            Task task;
            lock (_gate)
            {
                held.Signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                task = held.Signal.Task;
                if (!_held.Contains(held))
                {
                    _held.Add(held);
                }

                if (!_running)
                {
                    _running = true;
                    _ = Task.Run(LoopAsync, CancellationToken.None);
                }
            }

            if (cancellationToken.CanBeCanceled)
            {
                var signal = held.Signal;
                cancellationToken.Register(() => signal.TrySetResult());
            }

            return task;
        }

        internal void Release(Held held)
        {
            lock (_gate)
            {
                _held.Remove(held);
            }
        }

        private async Task LoopAsync()
        {
            while (true)
            {
                await timer.DelayAsync(DueTickMs, CancellationToken.None).ConfigureAwait(false);
                Held[] snapshot;
                lock (_gate)
                {
                    if (_held.Count == 0)
                    {
                        _running = false;
                        return;
                    }

                    snapshot = [.. _held];
                }

                var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
                var now = timer.NowMs;
                var failed = false;
                foreach (var held in snapshot)
                {
                    if (now >= held.WakeAtMs)
                    {
                        held.Signal.TrySetResult();
                    }
                    else if (!failed)
                    {
                        try
                        {
                            if (KeysOf(files, mailbox, held.Paths, stamps) != held.Keys)
                            {
                                held.Signal.TrySetResult();
                            }
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            failed = true;
                            if (!_failing)
                            {
                                log?.Invoke("the /v1/due tick skipped: " + e.Message);
                            }
                        }
                    }
                }

                _failing = failed;
            }
        }
    }

    /// <summary>The stamps of <paramref name="paths"/> joined; <paramref name="stamps"/> keeps each key's stamp for the rest of one tick.</summary>
    private static string KeysOf(IFileReader files, IMailbox mailbox, string[] paths, Dictionary<string, string>? stamps = null)
    {
        var parts = new string[paths.Length];
        for (var i = 0; i < paths.Length; i++)
        {
            if (stamps is null || !stamps.TryGetValue(paths[i], out var stamp))
            {
                stamp = Stamp(files, mailbox, paths[i]);
                stamps?.Add(paths[i], stamp);
            }

            parts[i] = stamp;
        }

        return string.Join('|', parts);
    }

    private sealed class SystemDueTimer : IDueTimer
    {
        internal static readonly SystemDueTimer Instance = new();

        public long NowMs => Environment.TickCount64;

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) => Task.Delay(milliseconds, cancellationToken);
    }

    /// <summary>
    /// The daemon's own mailbox state as the box reader of a due computation. <see cref="Stamps"/> holds each watched key's stamp
    /// taken before the computation read it, so mail that lands after the read moves the key a held request waits on.
    /// </summary>
    private sealed class InProcessBoxes(IMailbox mailbox, IFileReader files) : IDueBoxSource
    {
        internal Dictionary<string, string> Stamps { get; } = new(StringComparer.Ordinal);

        public DueBoxRead? Read(string address, string dir, long since)
        {
            var key = BoxKey(mailbox, dir);
            _ = Stamps.TryAdd(key, Stamp(files, mailbox, key));
            var read = mailbox.Inbox(dir, new InboxOptions(Since: since));
            return new DueBoxRead(read.Entries, read.Next);
        }
    }

    private static async Task<RouteResponse> Due(RouteRequest request, IMailDue due, IMailbox mailbox, IFileReader files, IDueTimer timer, DueScanner scanner, CancellationToken ct)
    {
        var q = ParseQuery(request.Query);
        if (!q.TryGetValue("in", out var inText))
        {
            return Error(400, "bad_request", "in is required");
        }

        JsonValue input;
        try
        {
            input = JsonText.Parse(inText);
        }
        catch (JsonParseException)
        {
            return Error(400, "bad_request", "in must be JSON");
        }

        var parsed = due.ParseRequest(input, out var why);
        if (parsed is null)
        {
            return Error(400, "bad_request", why!);
        }

        // the session asked about must be one the peer's process tree proves, so the heartbeats written below are its own boxes'
        var (_, _, notCaller) = CallerOf(request, mailbox, parsed.Session, write: true);
        if (notCaller is not null)
        {
            return notCaller;
        }

        scanner.NoteMod(parsed);
        var wait = DueMaxWaitSeconds;
        if (q.TryGetValue("wait", out var waitText))
        {
            var n = Number(waitText);
            if (!IsInteger(n))
            {
                return Error(400, "bad_request", "wait must be a whole number of seconds");
            }

            wait = (int)Math.Clamp(n, 1, DueMaxWaitSeconds);
        }

        // ah2-70: the doorbell the mod rings between the tool calls of a busy turn is a question, never a wait. It sends no
        // `wait`, and holding it for the default made every tool call of a member with no new mail cost the full hold.
        if (parsed.Carrier == DoorbellCarrier.Tool)
        {
            wait = 0;
        }

        var source = new InProcessBoxes(mailbox, files);
        var registrationKey = RegistrationKey(due, mailbox, parsed.Session);
        source.Stamps[registrationKey] = Stamp(files, mailbox, registrationKey);
        var started = timer.NowMs;
        var end = started + (long)wait * 1000;
        // The heartbeat is written when the request starts (unless the last one is younger than a rewrite) and then only when the copy a reader sees would be half way to stale
        // (readers call a heartbeat stale after three poll intervals), or when a field other than `at` has changed.
        var rewriteAfter = (long)(Mailbox.HeartbeatFreshFactor * parsed.PollMs / 2);
        var beats = new Dictionary<string, (long At, long Through)>(StringComparer.Ordinal);
        var answer = await Task.Run(() => due.Compute(parsed, source), ct).ConfigureAwait(false);

        // A client re-sends as soon as it is answered: a heartbeat the last request wrote is taken as written at its real age, so a new
        // request writes only when that one is due a rewrite. Writing at every start cost one mail write per request per box.
        foreach (var dir in answer.BoxDirs)
        {
            if (mailbox.HeartbeatAgeMs(dir, parsed.Session, parsed.PollMs) is { } age && age < rewriteAfter)
            {
                // the stored beat's own acked_through, so an ack since it was written still gets it rewritten; none stored forces a rewrite
                beats[dir] = (started - (long)age, mailbox.ReadHeartbeat(dir, parsed.Session)?.Get("acked_through") is { IsNumber: true } stored ? (long)stored.AsNumber() : -1);
            }
        }

        var held = new DueScanner.Held();
        var recomputed = true;
        try
        {
            while (true)
            {
                var now = timer.NowMs;
                var nextBeat = long.MaxValue;
                foreach (var dir in answer.BoxDirs)
                {
                    var known = beats.TryGetValue(dir, out var last);
                    var through = recomputed || !known ? mailbox.AckedThrough(dir) : last.Through;
                    if (!known || now - last.At >= rewriteAfter || through != last.Through)
                    {
                        mailbox.WriteHeartbeat(dir, DueAdapter, parsed.Session, parsed.PollMs);
                        last = (now, through);
                        beats[dir] = last;
                    }

                    nextBeat = Math.Min(nextBeat, last.At + rewriteAfter);
                }

                recomputed = false;
                if (answer.Ids.Count > 0 || now >= end || ct.IsCancellationRequested)
                {
                    return Json(200, due.AnswerJson(answer, null));
                }

                held.Paths = DuePaths(due, mailbox, parsed.Session, answer);
                held.Keys = KeysOf(files, mailbox, held.Paths, source.Stamps);
                var wake = Math.Min(end, nextBeat);
                if (answer.NextDeadlineMs is { } due0)
                {
                    wake = Math.Min(wake, started + (long)Math.Ceiling(due0 - parsed.Now));
                }

                held.WakeAtMs = wake;
                var heldKeys = held.Keys;
                await scanner.WaitAsync(held, ct).ConfigureAwait(false);
                var elapsed = timer.NowMs - started;
                var shifted = parsed with { Now = parsed.Now + elapsed };
                if (KeysOf(files, mailbox, held.Paths) != heldKeys || (answer.NextDeadlineMs is { } deadline && shifted.Now >= deadline))
                {
                    TraceRecompute(mailbox, answer, parsed, shifted.Now);
                    source.Stamps.Clear();
                    source.Stamps[registrationKey] = Stamp(files, mailbox, registrationKey);
                    answer = await Task.Run(
                        () =>
                        {
                            // the wait runs inside the routed call's scope, whose reading is as old as the call
                            using var fresh = ProcessSnapshotScope.Begin();
                            return due.Compute(shifted, source);
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    recomputed = true;
                }
            }
        }
        finally
        {
            scanner.Release(held);
        }
    }

    // ponytail: b99 diagnostic, one row per held recompute; delete it with MailDoorbell.Trace once the live UI answer has named the cause.
    private static void TraceRecompute(IMailbox mailbox, DueAnswer answer, DueRequest parsed, double now)
    {
        if (answer.BoxDirs.Count == 0)
        {
            return;
        }

        try
        {
            (mailbox as Mailbox)?.AppendTrace(parsed.Session, new JsonObject
            {
                ["state"] = "due-recompute",
                ["dir"] = answer.BoxDirs[0],
                ["session_id"] = parsed.Session,
                ["now"] = now,
                ["busy_at"] = parsed.BusyAt is { } busy ? busy : JsonValue.Null,
                ["deadline"] = answer.NextDeadlineMs is { } deadline ? deadline : JsonValue.Null,
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = e;
        }
    }

    /// <summary>
    /// What can make an entry due: the registration and each box's entries. An ack never does, so the acks directory is not
    /// watched: an acked id is released from the mod's claim map by the next answer, and the heartbeat's <c>acked_through</c> catches
    /// up at its next rewrite. A store in the database is watched by its change token, which any change to it moves: that only
    /// recomputes the held answer, and the client is answered only if something is due.
    /// </summary>
    private static string[] DuePaths(IMailDue due, IMailbox mailbox, string session, DueAnswer answer)
    {
        var paths = new List<string> { RegistrationKey(due, mailbox, session) };
        foreach (var dir in answer.BoxDirs)
        {
            paths.Add(BoxKey(mailbox, dir));
        }

        return [.. paths];
    }

    private static string RegistrationKey(IMailDue due, IMailbox mailbox, string session) =>
        mailbox.SessionsChangeToken() is null ? due.RegistrationFile(session) : SessionsKey;

    private static string BoxKey(IMailbox mailbox, string dir) => mailbox.BoxesAreStored ? BoxKeyPrefix + dir : dir + "/entries.jsonl";

    private static string Stamp(IFileReader files, IMailbox mailbox, string key)
    {
        if (key == SessionsKey)
        {
            return mailbox.SessionsChangeToken() is { } token ? token.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
        }

        if (key.StartsWith(BoxKeyPrefix, StringComparison.Ordinal))
        {
            // What wakes a held request is what its entries file's stat is on files: new mail in its box. The mail counter moves on every heartbeat of every box, several times a second.
            return mailbox.StoredNewestEntry(key[BoxKeyPrefix.Length..]) is { } newest ? newest.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
        }

        return files.Stat(key) is { } s ? $"{s.Size}:{s.MtimeMs:R}" : "-";
    }
}
