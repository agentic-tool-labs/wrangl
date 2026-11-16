using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Mesh;

/// <summary>The count of acks by an adapter for an entry no pull of this daemon returned; <c>unpulled_acks</c> on health reports it.</summary>
public interface IAckTally
{
    /// <summary>Acks since the daemon started for an entry no pull returned.</summary>
    long Unpulled { get; }
}

/// <summary>What the mailbox routes stand on; the composition root builds each from the one set of modules the daemon runs.</summary>
/// <param name="Mailbox">Reads and writes the boxes, heartbeats, acks and registrations.</param>
/// <param name="Roster">Reads a registering harness's team file.</param>
/// <param name="Config">Names the built-in harnesses and the Codex app-server harness.</param>
/// <param name="Checker">Validates the caller context a request carries.</param>
/// <param name="Files">Tells whether a registration's working directory exists.</param>
/// <param name="RowAdapted">Whether a <c>kind: "codex"</c> member row opted in to the app-server adapter.</param>
/// <param name="Tally">Where an ack of an entry nobody pulled is counted.</param>
/// <param name="Processes">Tells whether a registering pid is a process of the daemon's user.</param>
/// <param name="Binder">Decides whether a registration's claimed pid is its registrant's own or an ancestor's.</param>
/// <param name="PidRunning">The hierarchy core's process seam (one-liveness.md §3), or null to keep the kill-0 and start checks.</param>
/// <param name="Heard">Where a bound register and a proven heartbeat record their process; null records nothing.</param>
public sealed record MailboxRouteDependencies(
    IMailbox Mailbox,
    IRosterCore Roster,
    IConfigCore Config,
    IRequestContextChecker Checker,
    IFileReader Files,
    Func<JsonValue, bool> RowAdapted,
    IAckTally Tally,
    IProcessInspector Processes,
    IRegistrationBinder Binder,
    Func<JsonValue, JsonValue?, Ah.Core.Time.LivenessPolicy, bool>? PidRunning = null,
    IHeardProcesses? Heard = null);

internal sealed class AckTally : IAckTally
{
    private long _unpulled;

    public long Unpulled => Interlocked.Read(ref _unpulled);

    internal void Add(int count) => Interlocked.Add(ref _unpulled, count);
}

internal static partial class MeshRoutes
{
    private const int MaxWaitSeconds = 25;
    private const int PollMs = 200;
    private const int ReturnedCap = 2000;
    private const int MaxEntries = 200;

    private static readonly string[] AckBy = ["prompt-hook", "adapter", "cli", "sendmessage", "watcher", "paste", "stop-hook"];

    private static readonly string[] EntryFilters = ["pending", "acked", "all"];

    private static readonly string[] RegisterKeys = ["session_id", "addresses", "boxes", "team_file", "harness", "pid", "pid_start", "pane_id", "cwd", "herdr_flavour"];

    private static readonly string[] InboxKeys = ["address", "since", "wait", "adapter", "session_id", "poll_ms", "all", "overdue"];

    private static double Number(string text) => JsConvert.ToNumber(JsonValue.FromString(text));

    private static bool IsInteger(double n) => double.IsFinite(n) && double.IsInteger(n);

    private static bool Flag(string? v) => v is "1" or "true";

    internal static void RegisterMailbox(IRouteRegistry routes, MailboxRouteDependencies d)
    {
        var returned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tally = d.Tally as AckTally ?? throw new ArgumentException("the tally was not made by CreateAckTally", nameof(d));

        routes.Add("GET /v1/inbox", (request, ct) => Inbox(request, d, returned, ct));
        routes.Add("POST /v1/ack", (request, ct) => Ack(request, d, returned, tally, ct));
        routes.Add("POST /v1/decline", (request, ct) => Decline(request, d, ct));
        routes.Add("POST /v1/register", (request, ct) => Task.FromResult(RegisterSession(request, d, ct)));
        routes.Add("GET /v1/inspect/boxes", (request, ct) => Task.FromResult(InspectBoxes(request, d, ct)));
        routes.Add("GET /v1/inspect/entries", (request, ct) => Task.FromResult(InspectEntries(request, d, ct)));
        routes.Add("GET /v1/inspect/registrations", (request, ct) => Task.FromResult(InspectRegistrations(request, d, ct)));
    }

    // ---------------------------------------------------------------- GET /v1/inbox

    private static async Task<RouteResponse> Inbox(RouteRequest request, MailboxRouteDependencies d, Dictionary<string, List<string>> returned, CancellationToken ct)
    {
        var all = ParseQuery(request.Query);
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in InboxKeys)
        {
            if (all.TryGetValue(key, out var v))
            {
                q[key] = v;
            }
        }

        var address = q.GetValueOrDefault("address");
        if (d.Mailbox.SplitAddress(address) is null)
        {
            return Error(400, "bad_address", "address must be name@team");
        }

        var (context, refused) = ContextOf(ContextObject(all), d.Checker, optional: false);
        if (refused is not null)
        {
            return refused;
        }

        var resolved = d.Mailbox.ResolveAddress(address, context!);
        string box;
        if (resolved.Failure is { } failure)
        {
            var own = failure.Code == "unknown_address" ? OwnBoxFor(request, d, q.TryGetValue("session_id", out var hint) && hint.Length > 0 ? hint : null, address!, write: false) : null;
            if (own is null)
            {
                return Refusal(failure.Code, failure.Message, failure.Candidates);
            }

            box = own;
        }
        else
        {
            box = resolved.Value!.Dir;
        }

        var wait = q.TryGetValue("wait", out var waitText) ? Number(waitText) : 0;
        if (!IsInteger(wait) || wait < 0 || wait > MaxWaitSeconds)
        {
            return Error(400, "bad_request", $"wait must be a whole number of seconds from 0 to {MaxWaitSeconds}");
        }

        if (wait > 0 && !q.ContainsKey("since"))
        {
            return Error(400, "bad_request", "wait needs since: unacked entries are always new, so a long-poll without a cursor would return at once");
        }

        var since = q.TryGetValue("since", out var sinceText) ? Number(sinceText) : 0;
        if (!IsInteger(since) || since < 0 || since >= (double)long.MaxValue)
        {
            return Error(400, "bad_request", "since must be a byte offset");
        }

        var pollMs = q.TryGetValue("poll_ms", out var pollText) ? Number(pollText) : IMailCore.DefaultPollMs;
        if (!double.IsFinite(pollMs) || pollMs <= 0)
        {
            return Error(400, "bad_request", "poll_ms must be positive");
        }

        var (principal, choice, notCaller) = CallerOf(request, d.Mailbox, q.TryGetValue("session_id", out var sid) && sid.Length > 0 ? sid : null, write: false);
        if (notCaller is not null)
        {
            return notCaller;
        }

        if (!MayActFor(principal, choice, address!, d.Mailbox))
        {
            return CallerRefusal(CallerIdentity.NotYourSession);
        }

        // only the session that holds the box keeps its heartbeat; an Orchestrator or the user reading it leaves the heartbeat alone
        var adapter = OwnBox(choice, address!) ? q.GetValueOrDefault("adapter") : null;
        var sessionId = choice.Session?.SessionId;
        if (!string.IsNullOrEmpty(adapter) && choice.Session is { } heard)
        {
            d.Heard?.Add(heard.Pid, heard.PidStart);
        }

        var includeAcked = Flag(q.GetValueOrDefault("all"));
        var overdue = Flag(q.GetValueOrDefault("overdue"));
        var end = Environment.TickCount64 + (long)(wait * 1000);
        long lastBeat = 0;
        while (true)
        {
            if (!string.IsNullOrEmpty(adapter) && (lastBeat == 0 || Environment.TickCount64 - lastBeat >= pollMs / 2))
            {
                d.Mailbox.WriteHeartbeat(box, adapter, sessionId, pollMs);
                lastBeat = Math.Max(1, Environment.TickCount64);
            }

            var read = d.Mailbox.Inbox(box, new InboxOptions(All: includeAcked, Overdue: overdue, Since: (long)since));
            var draining = ct.IsCancellationRequested;
            if (read.Entries.Count > 0 || Environment.TickCount64 >= end || draining)
            {
                lock (returned)
                {
                    if (!returned.TryGetValue(address!, out var seen))
                    {
                        returned[address!] = seen = [];
                    }

                    foreach (var entry in read.Entries)
                    {
                        if (entry.Member("id") is { IsText: true } id && !seen.Contains(id.AsString()))
                        {
                            seen.Add(id.AsString());
                        }
                    }

                    if (seen.Count > ReturnedCap)
                    {
                        seen.RemoveRange(0, seen.Count - ReturnedCap);
                    }
                }

                var entries = new JsonArray();
                foreach (var entry in read.Entries)
                {
                    entries.Add(entry);
                }

                return Json(200, new JsonObject { ["address"] = address!, ["entries"] = entries, ["next"] = read.Next });
            }

            try
            {
                await Task.Delay(PollMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The drain began: the next pass answers with what the box holds.
            }
        }
    }

    // ---------------------------------------------------------------- POST /v1/decline

    private static async Task<RouteResponse> Decline(RouteRequest request, MailboxRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ParseBody(request.Body) is not { } body)
        {
            return Error(400, "bad_request", "body must be a JSON object");
        }

        var address = Text(body, "address");
        if (d.Mailbox.SplitAddress(address) is null)
        {
            return Error(400, "bad_address", "address must be name@team");
        }

        var id = Text(body, "id");
        var reason = Text(body, "reason");
        if (id is null || reason is null)
        {
            return Error(400, "bad_request", "id and reason are required");
        }

        var (context, refused) = ContextOf(body, d.Checker, optional: false);
        if (refused is not null)
        {
            return refused;
        }

        var resolved = d.Mailbox.ResolveAddress(address, context!);
        if (resolved.Failure is { } failure)
        {
            return Refusal(failure.Code, failure.Message, failure.Candidates);
        }

        var (_, choice, notCaller) = CallerOf(request, d.Mailbox, Text(body, "session_id"), write: true);
        if (notCaller is not null)
        {
            return notCaller;
        }

        var session = choice.Session?.SessionId;
        var callers = choice.Session?.Addresses ?? [];
        var result = await Task.Run(() => d.Mailbox.Decline(resolved.Value!.Dir, id, reason, Text(body, "note"), session, callers), ct).ConfigureAwait(false);
        return result.Failure is { } declined ? Refusal(declined.Code, declined.Message, declined.Candidates) : Json(200, JsonValue.FromObject(result.Value!));
    }

    // ---------------------------------------------------------------- POST /v1/ack

    private static async Task<RouteResponse> Ack(
        RouteRequest request, MailboxRouteDependencies d, Dictionary<string, List<string>> returned, AckTally tally, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ParseBody(request.Body) is not { } body)
        {
            return Error(400, "bad_request", "body must be a JSON object");
        }

        var address = Text(body, "address");
        if (d.Mailbox.SplitAddress(address) is null)
        {
            return Error(400, "bad_address", "address must be name@team");
        }

        if (body.Get("ids") is not { IsArray: true } idList || idList.AsArray().Any(i => !i.IsText))
        {
            return Error(400, "bad_request", "ids must be an array of entry ids");
        }

        var by = Text(body, "by");
        if (by is null || !AckBy.Contains(by, StringComparer.Ordinal))
        {
            return Error(400, "bad_request", $"by must be one of {string.Join(", ", AckBy)}");
        }

        var (context, refused) = ContextOf(body, d.Checker, optional: false);
        if (refused is not null)
        {
            return refused;
        }

        var ids = idList.AsArray().Select(i => i.AsString()).ToList();
        if (by == "adapter")
        {
            List<string> seen;
            lock (returned)
            {
                seen = returned.TryGetValue(address!, out var s) ? [.. s] : [];
            }

            tally.Add(ids.Count(i => !seen.Contains(i)));
        }

        var resolved = d.Mailbox.ResolveAddress(address, context!);
        string? ownDir = null;
        if (resolved.Failure is { } failure)
        {
            ownDir = failure.Code == "unknown_address" ? OwnBoxFor(request, d, Text(body, "session_id"), address!, write: true) : null;
            if (ownDir is null)
            {
                return Refusal(failure.Code, failure.Message, failure.Candidates);
            }
        }

        var (principal, choice, notCaller) = CallerOf(request, d.Mailbox, Text(body, "session_id"), write: true);
        if (notCaller is not null)
        {
            return notCaller;
        }

        if (!MayActFor(principal, choice, address!, d.Mailbox))
        {
            return CallerRefusal(CallerIdentity.NotYourSession);
        }

        var result = await Task.Run(() => d.Mailbox.AckIds(ownDir ?? resolved.Value!.Dir, ids, by, choice.Session?.SessionId), ct).ConfigureAwait(false);
        return Json(200, new JsonObject { ["acked"] = Strings(result.Acked), ["fresh"] = Strings(result.Fresh), ["unknown"] = Strings(result.Unknown) });
    }

    /// <summary>
    /// The box of <paramref name="address"/> that the caller's own registration records, for an address the request's context no longer
    /// lists (a member removed from its team while its session is alive): its own entries stay reachable. Null unless the caller proves
    /// the address and holds that box.
    /// </summary>
    private static string? OwnBoxFor(RouteRequest request, MailboxRouteDependencies d, string? sessionHint, string address, bool write)
    {
        var (principal, choice, notCaller) = CallerOf(request, d.Mailbox, sessionHint, write);
        if (notCaller is not null || choice.Session is not { } held || !MayActFor(principal, choice, address, d.Mailbox))
        {
            return null;
        }

        return d.Mailbox.ReadRegistration(held.SessionId) is { } registration
            ? d.Mailbox.RegistrationBoxes(registration).FirstOrDefault(b => b.Address == address)?.Dir
            : null;
    }

    private static JsonArray Strings(IEnumerable<string> items)
    {
        var all = new JsonArray();
        foreach (var item in items)
        {
            all.Add(item);
        }

        return all;
    }

    // ---------------------------------------------------------------- POST /v1/register

    private static RouteResponse NotAdmitted(string harness, string why) =>
        Error(403, "not_admitted", $"harness {harness} is not admitted: {why}");

    private const string UnboundWarning = "unbound registration; pid is required from 2.6.0";

    /// <summary>
    /// Whether the registration names the Claude harness (<c>claude</c> or <c>claude-mod</c>) in so many words. Any other harness, or
    /// none, is checked as a non-Claude harness: no process from its pid up may be Claude.
    /// </summary>
    private static bool IsClaudeHarness(JsonObject body) =>
        body.Get("harness") is { IsText: true } h && h.AsString() is "claude" or "claude-mod";

    /// <summary>Whether a registration still holds <paramref name="pid"/>: the process is alive and has the start time the record names.</summary>
    private static bool Occupies(MailboxRouteDependencies d, JsonObject registration, long pid) =>
        d.PidRunning is { } running
            ? registration.Get("pid_start") is { IsText: true } recorded && recorded.AsString().Length > 0
                && running(JsonValue.FromNumber(pid), recorded, Ah.Core.Time.LivenessPolicy.ProvenRunning)
            : d.Processes.PidAlive((int)pid)
                && Ah.Core.Time.ProcessStart.IsSame(d.Binder.StartMatches(pid, registration.Get("pid_start") is { IsText: true } s ? s.AsString() : null));

    /// <summary>Whether the process a record names is still running: alive, and not a reused pid with another start time. A start that cannot be compared counts as running.</summary>
    private static bool HeldIsRunning(MailboxRouteDependencies d, JsonObject registration, long pid) =>
        pid is > 1 and <= int.MaxValue
        && (d.PidRunning is { } running
            ? running(JsonValue.FromNumber(pid), registration.Get("pid_start"), Ah.Core.Time.LivenessPolicy.RunningOrUnknown)
            : d.Processes.PidAlive((int)pid)
                && d.Binder.StartMatches(pid, registration.Get("pid_start") is { IsText: true } s ? s.AsString() : null) != Ah.Core.Time.StartMatch.Different);

    private static RouteResponse RegisterSession(RouteRequest request, MailboxRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ParseBody(request.Body) is not { } raw)
        {
            return Error(400, "bad_request", "body must be a JSON object");
        }

        var body = new JsonObject();
        foreach (var key in RegisterKeys)
        {
            if (raw.Get(key) is { } v)
            {
                body[key] = v;
            }
        }

        var boxes = new List<BoxRef>();
        if (body.Get("addresses") is { IsArray: true } addresses && body.Get("boxes") is { IsMap: true } boxMap)
        {
            foreach (var a in addresses.AsArray())
            {
                if (a.IsText && boxMap.AsObject().Get(a.AsString()) is { IsText: true } dir)
                {
                    boxes.Add(new BoxRef(a.AsString(), dir.AsString()));
                }
            }
        }

        var sessionId = Text(body, "session_id");
        if (sessionId is null || boxes.Count == 0 || boxes.Any(b => d.Mailbox.SplitAddress(b.Address) is null))
        {
            return Error(400, "bad_request", "a registration needs session_id, addresses and boxes (address -> box directory)");
        }

        var harness = body.Get("harness") is { IsTruthy: true } h ? JsConvert.ToJsString(h) : "claude";
        var builtin = d.Config.IsBuiltinHarness(JsonValue.FromString(harness));
        if ((builtin ? CheckBoxes(harness, body, boxes, d) : CheckAdmission(harness, body, boxes, d)) is { } refused)
        {
            return refused;
        }

        var teamFile = body.Get("team_file") is { IsText: true } tf ? tf.AsString() : null;
        long? pid = null;
        if (body.Get("pid") is { IsNumber: true } p && double.IsInteger(p.AsNumber()))
        {
            var claimed = p.AsNumber();
            if (claimed <= 1 || claimed > int.MaxValue)
            {
                return Error(400, "bad_request", "pid must be a process id above 1");
            }

            if (!d.Processes.PidOwned((int)claimed))
            {
                return NotAdmitted(harness, $"pid {JsConvert.ToJsString(claimed)} is not a process of the daemon's user");
            }

            pid = (long)claimed;
        }

        var paneId = body.Get("pane_id") is { IsNull: false } pane ? JsConvert.ToJsString(pane) : null;
        var cwd = Text(body, "cwd") is { } c && c.StartsWith('/') && d.Files.Stat(c) is { Kind: FileKind.Directory } ? c : null;
        string? pidStart = null;
        if (body.Get("pid_start") is { IsNull: false } started)
        {
            if (!(started is { IsText: true } text) || !PidStartShape(text.AsString()))
            {
                return Error(400, "bad_request", "pid_start must be the process start time as ps prints it");
            }

            pidStart = text.AsString();
        }

        var ahVersion = body.Get("ah_version") is { IsText: true } versionText && versionText.AsString().Length > 0 ? versionText.AsString() : null;
        var existing = d.Mailbox.ReadRegistration(sessionId);
        string? warning = null;
        IReadOnlyList<JsonObject> stale = [];
        bool bound;
        if (pid is not { } claimedPid)
        {
            // until 2.6.0 a register without pid is still taken, unbound: it names no process, so it confers no identity, and it
            // may only create a record, never replace one
            if (existing is not null)
            {
                return NotAdmitted(harness, $"session {sessionId} already has a registration, and a register without pid may only create one");
            }

            warning = UnboundWarning;
            bound = false;
        }
        else
        {
            pidStart ??= d.Binder.StartOf(claimedPid);
            if (!d.Binder.Binds(claimedPid, pidStart, request.PeerPid))
            {
                return NotAdmitted(harness, $"pid {claimedPid} is not the caller's own process or one of its ancestors with that start time");
            }

            if (IsClaudeHarness(body))
            {
                if (!d.Binder.IsHarnessProcess(claimedPid))
                {
                    return NotAdmitted(harness, $"pid {claimedPid} is not a Claude harness process");
                }
            }
            else if (d.Binder.NoHarnessFrom(claimedPid) is not { } noHarness)
            {
                // a line that could not be read (a ps that failed or hit its limit) may read next time: retry, not a refusal
                return Error(503, "busy", $"pid {claimedPid}'s process line could not be read to its end; try again");
            }
            else if (!noHarness)
            {
                return NotAdmitted(harness, $"pid {claimedPid} runs under a Claude session");
            }

            // a record whose process is gone (dead, or a reused pid with another start) is no one's: a process this route binds may take
            // the id over. A record whose process is still running is moved only by that session's own start.
            if (existing?.Get("pid") is { IsNumber: true } heldPid && heldPid.AsNumber() != claimedPid && HeldIsRunning(d, existing, (long)heldPid.AsNumber()))
            {
                return NotAdmitted(harness, $"session {sessionId} is registered to another process; only that session's own start moves it");
            }

            var others = d.Mailbox.AllRegistrations()
                .Where(r => JsConvert.ToJsString(r.Get("session_id")) != sessionId && r.Get("pid") is { IsNumber: true } op && op.AsNumber() == claimedPid)
                .ToList();
            if (others.Any(r => Occupies(d, r, claimedPid)))
            {
                return Error(403, "pid_occupied", $"pid {claimedPid} already holds a live registration for another session");
            }

            stale = others;
            bound = true;
        }

        // filled by the session side (ahd's env is not the session's); any other value is ignored and the stored one kept
        var flavour = Text(body, Ah.Core.Mux.HerdrFlavour.Field) is Ah.Core.Mux.HerdrFlavour.Tui or Ah.Core.Mux.HerdrFlavour.Core ? Text(body, Ah.Core.Mux.HerdrFlavour.Field) : null;
        var write = new RegistrationWrite(sessionId, boxes, teamFile, harness, pid, paneId, cwd, pidStart, bound, AhVersion: ahVersion, HerdrFlavour: flavour);
        var outcome = d.Mailbox.RegisterGuarded(write, HeldProcess(d, sessionId));
        if (outcome == RegisterOutcome.Written)
        {
            if (bound && pid is { } heardPid)
            {
                d.Heard?.Add((int)heardPid, pidStart);
            }

            // a record on this pid whose process is gone (dead, or a reused pid with another start) is not an occupant: the bind replaces it
            foreach (var gone in stale)
            {
                _ = d.Mailbox.RemoveRegistration(JsConvert.ToJsString(gone.Get("session_id")));
            }
        }
        return outcome switch
        {
            RegisterOutcome.Written => Json(200, warning is null ? new JsonObject { ["ok"] = true } : new JsonObject { ["ok"] = true, ["warning"] = warning }),
            RegisterOutcome.HeldByAnotherProcess => NotAdmitted(harness, $"session {sessionId} is registered by a live process with a different pid"),
            RegisterOutcome.BoundExists => NotAdmitted(harness, "a bound registration already exists for this session"),
            _ => Error(400, "bad_request", "session_id must match [A-Za-z0-9._-]+"),
        };
    }

    /// <summary>
    /// The process the session's held registration names, as this daemon sees it now: alive or not, and its start time when the
    /// registration recorded one (another start time is a reused pid). Null when there is no pid to ask about.
    /// </summary>
    private static RegistrationHeld? HeldProcess(MailboxRouteDependencies d, string sessionId)
    {
        var existing = d.Mailbox.ReadRegistration(sessionId);
        if (existing?.Get("pid") is not { IsNumber: true } held || !double.IsInteger(held.AsNumber())
            || held.AsNumber() <= 1 || held.AsNumber() > int.MaxValue)
        {
            return null;
        }

        var pid = (long)held.AsNumber();
        var recorded = existing.Get("pid_start") is { IsText: true } r && r.AsString().Length > 0 ? r.AsString() : null;
        return new RegistrationHeld(pid, d.Processes.PidAlive((int)pid), recorded is null ? null : d.Binder.StartAs(pid, recorded));
    }

    /// <summary>A process start time as <c>ps -o lstart=</c> prints it under the C locale: short, printable, no control characters.</summary>
    private static bool PidStartShape(string text) =>
        text.Length is > 0 and <= 64 && text.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or ':' or '.' or '_' or '-');

    /// <summary>
    /// The check every harness passes: the registration names the absolute team file it was made under, and each box is the
    /// directory that team file gives its member, so a registration can never point the daemon's jobs at another directory.
    /// </summary>
    private static RouteResponse? CheckBoxes(string harness, JsonObject body, List<BoxRef> boxes, MailboxRouteDependencies d)
    {
        var teamFile = body.Get("team_file") is { IsText: true } tf ? tf.AsString() : null;
        if (teamFile is null || !teamFile.StartsWith('/'))
        {
            return NotAdmitted(harness, "a registration needs the absolute team_file it was made under");
        }

        if (!d.Mailbox.IsNamedTeamPath(teamFile))
        {
            return NotAdmitted(harness, $"{teamFile} is not a named team file");
        }

        foreach (var box in boxes)
        {
            var parts = d.Mailbox.SplitAddress(box.Address);
            if (parts is null || NodePath.Basename(teamFile) != parts.Team + ".json" || box.Dir != d.Mailbox.BoxDirFor(teamFile, parts.Name))
            {
                return NotAdmitted(harness, $"the box of {box.Address} is not the directory its team file gives it");
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the team file named in <paramref name="body"/> admits <paramref name="harness"/> for each box: a named team file that
    /// reads, a member row that admits the harness, and the box directory the team file gives it. A correctness guard, not a security
    /// boundary: anything that can open the socket is the user and could claim a built-in harness.
    /// </summary>
    private static RouteResponse? CheckAdmission(string harness, JsonObject body, List<BoxRef> boxes, MailboxRouteDependencies d)
    {
        var teamFile = body.Get("team_file") is { IsText: true } tf ? tf.AsString() : null;
        if (teamFile is null || !teamFile.StartsWith('/'))
        {
            return NotAdmitted(harness, "a registration needs the absolute team_file it was admitted under");
        }

        if (!d.Mailbox.IsNamedTeamPath(teamFile))
        {
            return NotAdmitted(harness, $"{teamFile} is not a named team file");
        }

        var team = d.Roster.ReadTeamFileAt(teamFile);
        if (!(team.IsMap && team.AsObject().Get("members") is { IsArray: true } members))
        {
            return NotAdmitted(harness, $"{teamFile} is not a readable team file");
        }

        var name = NodePath.Basename(teamFile);
        var teamName = name.EndsWith(".json", StringComparison.Ordinal) ? name[..^".json".Length] : name;
        foreach (var box in boxes)
        {
            var parts = d.Mailbox.SplitAddress(box.Address);
            JsonValue? row = null;
            if (parts is not null && parts.Team == teamName)
            {
                foreach (var m in members.AsArray())
                {
                    if (m.IsMap && m.AsObject().Get("name") is { IsText: true } n && n.AsString() == parts.Name)
                    {
                        row = m;
                        break;
                    }
                }
            }

            if (!AdmitsHarness(row, harness, d))
            {
                return NotAdmitted(
                    harness,
                    harness == d.Config.CodexAppserverHarness
                        ? $"{box.Address} is not a codex member opted in to the app-server adapter"
                        : $"{box.Address} is not a custom member admitted under that harness");
            }

            if (box.Dir != d.Mailbox.BoxDirFor(teamFile, parts!.Name))
            {
                return NotAdmitted(harness, $"the box of {box.Address} is not the directory its team file gives it");
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a team file row admits <paramref name="harness"/>: a <c>kind: "custom"</c> row naming that harness; or, for the Codex
    /// app-server adapter's harness only, a <c>kind: "codex"</c> row opted in with <c>adapter: "appserver"</c>.
    /// </summary>
    private static bool AdmitsHarness(JsonValue? row, string harness, MailboxRouteDependencies d)
    {
        if (row is not { IsMap: true } r)
        {
            return false;
        }

        if (harness == d.Config.CodexAppserverHarness)
        {
            return d.RowAdapted(r);
        }

        return r.AsObject().Get("kind") is { IsText: true } kind && kind.AsString() == d.Config.KindCustom
            && r.AsObject().Get("harness") is { IsText: true } h && h.AsString() == harness;
    }

    // ---------------------------------------------------------------- GET /v1/inspect/*

    private static RouteResponse InspectBoxes(RouteRequest request, MailboxRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var address = ParseQuery(request.Query).TryGetValue("address", out var a) && a.Length > 0 ? a : null;
        return Json(200, new MailboxInspect(d.Mailbox).Boxes(address, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private static RouteResponse InspectEntries(RouteRequest request, MailboxRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = ParseQuery(request.Query);
        var address = query.GetValueOrDefault("address");
        if (d.Mailbox.SplitAddress(address) is null)
        {
            return Error(400, "bad_address", "address must be name@team");
        }

        var filter = query.GetValueOrDefault("filter") ?? "pending";
        if (!EntryFilters.Contains(filter, StringComparer.Ordinal))
        {
            return Error(400, "bad_request", $"filter must be one of {string.Join(", ", EntryFilters)}");
        }

        var n = query.TryGetValue("n", out var nText) ? Number(nText) : MaxEntries;
        if (!IsInteger(n) || n < 1 || n > MaxEntries)
        {
            return Error(400, "bad_request", $"n must be a whole number from 1 to {MaxEntries}");
        }

        var (context, refused) = ContextOf(ContextObject(query), d.Checker, optional: false);
        if (refused is not null)
        {
            return refused;
        }

        var resolved = d.Mailbox.ResolveAddress(address, context!);
        if (resolved.Failure is { } failure)
        {
            return Refusal(failure.Code, failure.Message, failure.Candidates);
        }

        return Json(200, new MailboxInspect(d.Mailbox).Entries(resolved.Value!.Dir, address!, filter, (int)n));
    }

    private static RouteResponse InspectRegistrations(RouteRequest request, MailboxRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ParseQuery(request.Query).TryGetValue("since", out var sinceText))
        {
            return Json(200, new MailboxInspect(d.Mailbox).Registrations(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        // The sessions counter moves on every registration write, so an unmoved counter leaves the asker's rows standing; live and adapter are not re-judged then.
        var token = d.Mailbox.SessionsChangeToken();
        var since = Number(sinceText);
        if (token is { } t && IsInteger(since) && since == t)
        {
            return Json(200, new JsonObject { ["unchanged"] = true, ["token"] = t });
        }

        var answer = new MailboxInspect(d.Mailbox).Registrations(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (token is { } moved)
        {
            answer["token"] = moved;
        }

        return Json(200, answer);
    }
}
