using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>A session the daemon watches for API errors, and the Orchestrator session that watches it.</summary>
/// <param name="Id">The session id.</param>
/// <param name="Owner">The registered Orchestrator session that watches it (itself for an Orchestrator).</param>
/// <param name="Own">Whether it is an Orchestrator's own session.</param>
/// <param name="Name">The member's name, or null for an Orchestrator.</param>
/// <param name="Role">The member's role.</param>
/// <param name="Team">The member's team.</param>
internal sealed record OutageSubject(string Id, string Owner, bool Own, string? Name, string? Role, string? Team);

/// <summary>One watched session with its activity record, as the outage pre-pass reads it.</summary>
/// <param name="Subject">The session.</param>
/// <param name="Activity">Its activity record, or null.</param>
/// <param name="NoResume">Whether a failure of it is escalated and never resumed.</param>
/// <param name="Handled">Whether the failure that began at the given time already has a resume or an escalation.</param>
internal sealed record OutageInput(OutageSubject Subject, JsonObject? Activity, bool NoResume, Func<string, bool> Handled);

/// <summary>A resume sent to the probe session to find out whether the connection is back.</summary>
internal sealed record OutageProbe(OutageSubject Subject, string Body, JsonObject Data);

/// <summary>The episode a failure belongs to when it happened inside an outage, and the last failure time that rule covers.</summary>
internal sealed record OutageForced(string Episode, double UntilMs);

/// <summary>What the per-session recovery needs to know about outages: whom to leave alone, until when, and which episode a release belongs to.</summary>
internal sealed record OutageView(
    IReadOnlySet<string> Held,
    IReadOnlyDictionary<string, double> ReleaseAtMs,
    IReadOnlyDictionary<string, OutageForced> Forced)
{
    internal static OutageView None { get; } = new(new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, double>(), new Dictionary<string, OutageForced>());
}

/// <summary>What the pre-pass decided for one tick.</summary>
/// <param name="View">What the per-session recovery reads.</param>
/// <param name="Rows">The <c>outage</c> rows to record.</param>
/// <param name="Events">The events for each Orchestrator session, by session id.</param>
/// <param name="Probes">The probe resumes due.</param>
internal sealed record OutagePlan(
    OutageView View,
    IReadOnlyList<PeerAppend> Rows,
    IReadOnlyDictionary<string, List<WatchEvent>> Events,
    IReadOnlyList<OutageProbe> Probes);

/// <summary>
/// The daemon-wide watch for a shared connection outage. When two or more sessions fail within a minute it holds every resume,
/// resumes one probe session on the usual schedule, and, when any session ends a turn normally, releases the held sessions one
/// after another. Its state is the latest <c>outage</c> peer row per start time.
/// </summary>
internal static class OutageWatch
{
    internal const double WindowMs = 60_000;

    internal const double StaggerMs = 15_000;

    internal const double OngoingMs = 2 * 3_600_000;

    private sealed record HeldSession(string Session, string FailedAt, string? Role);

    private sealed class State
    {
        internal string StartedAt { get; set; } = string.Empty;

        internal List<HeldSession> Held { get; } = [];

        internal string? Probe { get; set; }

        internal int Probes { get; set; }

        internal string Status { get; set; } = "open";

        internal string? EndedAt { get; set; }

        internal string? LastProbeAt { get; set; }

        internal string? ProbeFailedAt { get; set; }

        internal int Unanswered { get; set; }

        internal string? Reason { get; set; }

        internal List<(string Session, string Reason)> Dropped { get; } = [];

        internal HashSet<string> WriteFailed { get; } = new(StringComparer.Ordinal);

        internal List<(string Session, string At)> Release { get; } = [];

        internal HashSet<string> Acked { get; } = new(StringComparer.Ordinal);

        internal JsonObject ToRow(DateTimeOffset ts)
        {
            var held = new JsonArray();
            foreach (var h in Held)
            {
                var entry = new JsonObject { ["session_id"] = h.Session, ["failed_at"] = h.FailedAt };
                if (h.Role is not null)
                {
                    entry["role"] = h.Role;
                }

                held.Add(JsonValue.FromObject(entry));
            }

            var release = new JsonArray();
            foreach (var (session, at) in Release)
            {
                release.Add(JsonValue.FromObject(new JsonObject { ["session_id"] = session, ["at"] = at }));
            }

            var dropped = new JsonArray();
            foreach (var (session, reason) in Dropped)
            {
                dropped.Add(JsonValue.FromObject(new JsonObject { ["session_id"] = session, ["reason"] = reason }));
            }

            var row = new JsonObject
            {
                ["type"] = "outage",
                ["ts"] = Timestamps.ToIsoString(ts),
                ["started_at"] = StartedAt,
                ["held"] = JsonValue.FromArray(held),
                ["probe_session"] = Probe is null ? JsonValue.Null : JsonValue.FromString(Probe),
                ["probes"] = Probes,
                ["status"] = Status,
                ["release"] = JsonValue.FromArray(release),
                ["unanswered"] = Unanswered,
                ["dropped_probes"] = JsonValue.FromArray(dropped),
            };
            if (Reason is not null)
            {
                row["reason"] = Reason;
            }

            if (ProbeFailedAt is not null)
            {
                row["probe_failed_at"] = ProbeFailedAt;
            }

            if (EndedAt is not null)
            {
                row["ended_at"] = EndedAt;
            }

            if (LastProbeAt is not null)
            {
                row["last_probe_at"] = LastProbeAt;
            }

            return row;
        }
    }

    internal static OutagePlan Evaluate(double now, IReadOnlyList<OutageInput> inputs, IReadOnlyList<JsonObject> peerRecords, IHierarchyRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(peerRecords);
        ArgumentNullException.ThrowIfNull(recovery);
        var nowTs = DateTimeOffset.FromUnixTimeMilliseconds((long)now);
        var byId = new Dictionary<string, OutageInput>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            _ = byId.TryAdd(input.Subject.Id, input);
        }

        var states = ReadStates(peerRecords);
        var rows = new List<PeerAppend>();
        var events = new Dictionary<string, List<WatchEvent>>(StringComparer.Ordinal);
        var probes = new List<OutageProbe>();

        void Emit(State s, string owner, string kindName, string text)
        {
            if (!events.TryGetValue(owner, out var list))
            {
                list = [];
                events[owner] = list;
            }

            list.Add(new WatchEvent(null, "OUTAGE", text, new JsonObject { ["outage"] = kindName, ["started_at"] = s.StartedAt }) { Name = "outage", Extra = new JsonObject { ["outage"] = kindName, ["started_at"] = s.StartedAt } });
        }

        static bool Told(State s, string owner, string kindName) => s.Acked.Contains(AckKey(owner, kindName));

        IEnumerable<string> HeldOwners(State s) =>
            s.Held.Select(h => byId.TryGetValue(h.Session, out var i) ? i.Subject.Owner : null).OfType<string>().Distinct();

        // A failure the daemon would act on: retryable and recent. Whether it was already resumed or escalated is a separate question.
        (string FailedAt, double Ms, string Error)? Failure(OutageInput input)
        {
            var a = input.Activity;
            if (a is null || a.Str("activity") != "failed" || a.Str("failed_at") is not { } failedAt || !Timestamps.TryParseMs(failedAt, out var ms))
            {
                return null;
            }

            var error = a.Str("error") is { Length: > 0 } e ? e : "unknown";
            return (failedAt, ms, error);
        }

        bool Retryable(OutageInput input, (string FailedAt, double Ms, string Error) f) =>
            !input.NoResume && recovery.Retryable.Contains(f.Error) && now - f.Ms <= recovery.StreakWindowMs;

        bool Resumable(OutageInput input, (string FailedAt, double Ms, string Error) f) => Retryable(input, f) && !input.Handled(f.FailedAt);

        bool EndedNormally(OutageInput input, double afterMs) =>
            input.Activity is { } a && a.Str("activity") is "idle" or "blocked" && Timestamps.TryParseMs(a.Str("at") ?? string.Empty, out var at) && at > afterMs;

        string Names(State s, string owner) =>
            string.Join(", ", s.Held.Where(h => byId.TryGetValue(h.Session, out var i) && i.Subject.Owner == owner).Select(h => byId[h.Session].Subject.Name ?? "this session"));

        void Notify(State s)
        {
            foreach (var owner in HeldOwners(s))
            {
                if (Told(s, owner, "start"))
                {
                    continue;
                }

                var count = s.Held.Count(h => byId.TryGetValue(h.Session, out var i) && i.Subject.Owner == owner);
                Emit(s, owner, "start", $"ah: connection outage: {count.ToString(CultureInfo.InvariantCulture)} sessions held ({Names(s, owner)}); probing at 10 s, 60 s, 5 min, then every 10 min.");
            }
        }

        // A failure that waits in the queue of a finished outage is already accounted for: it is released in its turn, not a new outage.
        bool Queued(OutageInput input, (string FailedAt, double Ms, string Error) f) =>
            states.Any(s => s.Status == "over" && Timestamps.TryParseMs(s.EndedAt ?? string.Empty, out var ended) && f.Ms <= ended
                && (s.Release.Any(r => r.Session == input.Subject.Id) || (s.Reason == "no-probe" && s.Held.Any(h => h.Session == input.Subject.Id))));

        void Finish(State s, string? reason)
        {
            s.Status = "over";
            s.EndedAt = Timestamps.ToIsoString(nowTs);
            s.Reason = reason;
            if (reason is not null)
            {
                return;
            }

            var stillFailed = s.Held
                .Where(h => byId.TryGetValue(h.Session, out var i) && Failure(i) is not null)
                .OrderBy(h => byId[h.Session].Subject.Own ? 0 : 1)
                .ThenBy(static h => Timestamps.TryParseMs(h.FailedAt, out var ms) ? ms : double.MaxValue)
                .ToList();
            for (var i = 0; i < stillFailed.Count; i++)
            {
                s.Release.Add((stillFailed[i].Session, Timestamps.ToIsoString(nowTs.AddMilliseconds(i * StaggerMs))));
            }
        }

        // Drops the current probe and takes the next held session that can still be probed; false, with the outage finished, when none is left.
        bool Repick(State s, string reason)
        {
            s.Dropped.Add((s.Probe ?? string.Empty, reason));
            s.Unanswered = 0;
            s.ProbeFailedAt = null;
            var next = s.Held
                .OrderBy(h => byId.TryGetValue(h.Session, out var i) && i.Subject.Own ? 0 : 1)
                .ThenBy(static h => Timestamps.TryParseMs(h.FailedAt, out var ms) ? ms : double.MaxValue)
                .Select(static h => h.Session)
                .FirstOrDefault(id => s.Dropped.All(d => d.Session != id) && byId.TryGetValue(id, out var i) && Failure(i) is not null);
            s.Probe = next;
            if (next is null)
            {
                Finish(s, "no-probe");
                return false;
            }

            return true;
        }

        var open = states.LastOrDefault(static s => s.Status == "open");
        if (open is null)
        {
            var candidates = inputs
                .DistinctBy(static i => i.Subject.Id)
                .Select(i => (Input: i, Failure: Failure(i)))
                .Where(c => c.Failure is { } f && Retryable(c.Input, f) && !Queued(c.Input, f))
                .Select(static c => (c.Input, Failure: c.Failure!.Value))
                .OrderBy(static c => c.Failure.Ms)
                .ToList();
            while (candidates.Count >= 2)
            {
                var cluster = candidates.Where(c => c.Failure.Ms <= candidates[0].Failure.Ms + WindowMs).ToList();
                var unresumed = cluster.Where(c => !c.Input.Handled(c.Failure.FailedAt)).ToList();
                if (cluster.Count >= 2 && unresumed.Count > 0)
                {
                    open = new State { StartedAt = Timestamps.ToIsoString(nowTs) };
                    foreach (var (input, failure) in unresumed)
                    {
                        open.Held.Add(new HeldSession(input.Subject.Id, failure.FailedAt, input.Subject.Role));
                    }

                    open.Probe = (unresumed.FirstOrDefault(static c => c.Input.Subject.Own) is { Input: not null } own ? own : unresumed[0]).Input.Subject.Id;
                    states.Add(open);
                    Notify(open);
                    rows.Add(new PeerAppend(open.ToRow(nowTs)));
                    break;
                }

                candidates.RemoveAt(0);
            }
        }
        else
        {
            var changed = false;
            foreach (var input in inputs.DistinctBy(static i => i.Subject.Id))
            {
                if (Failure(input) is { } f && Resumable(input, f) && open.Held.All(h => h.Session != input.Subject.Id))
                {
                    open.Held.Add(new HeldSession(input.Subject.Id, f.FailedAt, input.Subject.Role));
                    changed = true;
                }
            }

            _ = Timestamps.TryParseMs(open.StartedAt, out var startedMs);
            var lastProbeMs = Timestamps.TryParseMs(open.LastProbeAt ?? string.Empty, out var lp) ? lp : startedMs;
            var probeEnded = open.Probe is { } ps && byId.TryGetValue(ps, out var probeInput) && EndedNormally(probeInput, lastProbeMs);
            if (probeEnded || inputs.Any(i => EndedNormally(i, startedMs)))
            {
                Finish(open, null);
                changed = true;
            }
            else
            {
                var sendNow = false;
                if (open.Probe is { } current)
                {
                    var lost = !byId.ContainsKey(current) ? "left-watch" : open.WriteFailed.Contains(AckKey(current, open.Probes.ToString(CultureInfo.InvariantCulture))) ? "write-failed" : null;
                    if (lost is not null)
                    {
                        sendNow = Repick(open, lost);
                        changed = true;
                    }
                }

                if (open.Status == "open" && open.Probe is { } probeId && byId.TryGetValue(probeId, out var probe) && Failure(probe) is { } pf)
                {
                    var delay = recovery.DelaySec(JsonValue.FromString(pf.Error), JsonValue.FromNumber(open.Probes + 1));
                    var reference = open.Probes == 0 ? pf.Ms : Math.Max(pf.Ms, lastProbeMs);
                    var due = sendNow || (delay is { Kind: JsonKind.Number } d && now >= reference + (d.AsNumber() * 1000));
                    if (due && !sendNow)
                    {
                        open.Unanswered = open.Probes > 0 && pf.FailedAt == open.ProbeFailedAt ? open.Unanswered + 1 : 0;
                        if (open.Unanswered >= 2)
                        {
                            sendNow = Repick(open, "unanswered");
                            changed = true;
                            if (sendNow && open.Probe is { } next && byId.TryGetValue(next, out var replacement) && Failure(replacement) is { } npf)
                            {
                                probe = replacement;
                                pf = npf;
                            }
                            else
                            {
                                due = false;
                            }
                        }
                    }

                    if (due && open.Status == "open")
                    {
                        open.Probes++;
                        open.LastProbeAt = Timestamps.ToIsoString(nowTs);
                        open.ProbeFailedAt = pf.FailedAt;
                        var n = open.Probes.ToString(CultureInfo.InvariantCulture);
                        probes.Add(new OutageProbe(
                            probe.Subject,
                            $"Your last turn ended on an API error ({pf.Error}) at {ClockOf(pf.FailedAt)}; this is probe {n} of a connection outage and resumes only this session, to find out whether the connection is back.\n{recovery.ResumeParagraph}",
                            new JsonObject { ["subject"] = probe.Subject.Id, ["error"] = pf.Error, ["failed_at"] = pf.FailedAt, ["probe"] = open.Probes, ["started_at"] = open.StartedAt }));
                        changed = true;
                    }
                }

                if (open.Status == "open")
                {
                    if (now - startedMs >= OngoingMs)
                    {
                        foreach (var owner in HeldOwners(open).Where(o => Told(open, o, "start") && !Told(open, o, "ongoing")))
                        {
                            Emit(open, owner, "ongoing", "ah: outage ongoing for 2 h; still probing every 10 min. Needs a person if the network is down.");
                        }
                    }

                    Notify(open);
                }
            }

            if (changed)
            {
                rows.Add(new PeerAppend(open.ToRow(nowTs)));
            }
        }

        if (states.Count > 0 && states[^1] is { Status: "over" } finished)
        {
            _ = Timestamps.TryParseMs(finished.StartedAt, out var finishedStart);
            _ = Timestamps.TryParseMs(finished.EndedAt ?? string.Empty, out var finishedEnd);
            foreach (var owner in HeldOwners(finished).Where(o => Told(finished, o, "start") && !Told(finished, o, "over")))
            {
                var n = finished.Release.Count(r => byId.TryGetValue(r.Session, out var i) && i.Subject.Owner == owner);
                Emit(
                    finished,
                    owner,
                    "over",
                    finished.Reason == "no-probe"
                        ? $"ah: outage handling stopped: no held session could be probed ({Names(finished, owner)}); resuming each on its own schedule."
                        : $"ah: outage over after {Duration(finishedEnd - finishedStart)}; resuming {n.ToString(CultureInfo.InvariantCulture)} sessions, 15 s apart.");
            }
        }

        return new OutagePlan(ViewOf(states), rows, events, probes);
    }

    private static OutageView ViewOf(List<State> states)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        var release = new Dictionary<string, double>(StringComparer.Ordinal);
        var forced = new Dictionary<string, OutageForced>(StringComparer.Ordinal);
        foreach (var s in states)
        {
            var until = s.Status == "open" ? double.PositiveInfinity : Timestamps.TryParseMs(s.EndedAt ?? string.Empty, out var ended) ? ended : double.NegativeInfinity;
            foreach (var h in s.Held)
            {
                forced[h.Session] = new OutageForced(h.FailedAt, until);
                if (s.Status == "open")
                {
                    _ = held.Add(h.Session);
                }
            }

            foreach (var (session, at) in s.Release)
            {
                if (Timestamps.TryParseMs(at, out var ms))
                {
                    release[session] = ms;
                }
            }
        }

        return new OutageView(held, release, forced);
    }

    private static List<State> ReadStates(IReadOnlyList<JsonObject> peerRecords)
    {
        var byStart = new Dictionary<string, State>(StringComparer.Ordinal);
        var order = new List<string>();
        var acks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var writeFailed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in peerRecords)
        {
            if (row.Str("type") == "outage-probe-failed" && row.Str("started_at") is { } failStart && row.Str("session_id") is { } failSession && row.Get("probe") is { Kind: JsonKind.Number } failProbe)
            {
                if (!writeFailed.TryGetValue(failStart, out var failSet))
                {
                    failSet = new HashSet<string>(StringComparer.Ordinal);
                    writeFailed[failStart] = failSet;
                }

                _ = failSet.Add(AckKey(failSession, ((int)failProbe.AsNumber()).ToString(CultureInfo.InvariantCulture)));
                continue;
            }

            if (row.Str("type") == "outage-ack" && row.Str("started_at") is { } ackStart && row.Str("owner") is { } ackOwner && row.Str("event") is { } ackEvent)
            {
                if (!acks.TryGetValue(ackStart, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    acks[ackStart] = set;
                }

                _ = set.Add(AckKey(ackOwner, ackEvent));
                continue;
            }

            if (row.Str("type") != "outage" || row.Str("started_at") is not { } started)
            {
                continue;
            }

            var s = new State
            {
                StartedAt = started,
                Status = row.Str("status") ?? "open",
                EndedAt = row.Str("ended_at"),
                LastProbeAt = row.Str("last_probe_at"),
                Probe = row.Str("probe_session"),
                Reason = row.Str("reason"),
                ProbeFailedAt = row.Str("probe_failed_at"),
            };
            if (row.Get("unanswered") is { Kind: JsonKind.Number } unanswered)
            {
                s.Unanswered = (int)unanswered.AsNumber();
            }

            if (row.Get("dropped_probes") is { IsArray: true } dropped)
            {
                foreach (var d in dropped.AsArray().Where(static d => d.IsMap).Select(static d => d.AsObject()))
                {
                    if (d.Str("session_id") is { } dropSession && d.Str("reason") is { } dropReason)
                    {
                        s.Dropped.Add((dropSession, dropReason));
                    }
                }
            }

            if (row.Get("probes") is { Kind: JsonKind.Number } probes)
            {
                s.Probes = (int)probes.AsNumber();
            }

            if (row.Get("held") is { IsArray: true } held)
            {
                foreach (var h in held.AsArray().Where(static h => h.IsMap).Select(static h => h.AsObject()))
                {
                    if (h.Str("session_id") is { } sid && h.Str("failed_at") is { } failedAt)
                    {
                        s.Held.Add(new HeldSession(sid, failedAt, h.Str("role")));
                    }
                }
            }

            if (row.Get("release") is { IsArray: true } release)
            {
                foreach (var r in release.AsArray().Where(static r => r.IsMap).Select(static r => r.AsObject()))
                {
                    if (r.Str("session_id") is { } sid && r.Str("at") is { } at)
                    {
                        s.Release.Add((sid, at));
                    }
                }
            }

            if (!byStart.ContainsKey(started))
            {
                order.Add(started);
            }

            byStart[started] = s;
        }

        foreach (var (started, set) in acks)
        {
            if (byStart.TryGetValue(started, out var s))
            {
                s.Acked.UnionWith(set);
            }
        }

        foreach (var (started, set) in writeFailed)
        {
            if (byStart.TryGetValue(started, out var s))
            {
                s.WriteFailed.UnionWith(set);
            }
        }

        return [.. order.Select(k => byStart[k])];
    }

    internal static string AckKey(string owner, string kindName) => $"{owner}|{kindName}";

    private static string Duration(double ms)
    {
        var seconds = Math.Max(0, ms / 1000);
        return seconds < 60 ? $"{Math.Floor(seconds).ToString("0", CultureInfo.InvariantCulture)} s" : DispatchClock.FmtAge(seconds);
    }

    private static string ClockOf(string iso) =>
        Timestamps.TryParseMs(iso, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "an unknown time";
}
