using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Mesh;

/// <summary>What the paste rung did: <c>confirmed</c>, <c>unconfirmed</c>, or <c>refused</c> with a reason.</summary>
/// <param name="Outcome">The outcome name.</param>
/// <param name="Reason">Why, for an unconfirmed or refused paste.</param>
public sealed record PasteOutcome(string Outcome, string? Reason);

/// <summary>The panes, as the ladder uses them: where a member runs and one hardened paste. Implemented over the launch layer.</summary>
public interface IDeliveryPaste
{
    /// <summary><c>placementOf</c>: <c>{pasteable, reason?, transport, pane}</c> of a member on a team's transport.</summary>
    JsonObject PlacementOf(JsonValue member, string? teamTransport);

    /// <summary>
    /// Pastes <paramref name="payload"/> into the member's pane for entry <paramref name="entryId"/> of box <paramref name="boxDir"/>.
    /// The paste's character limit applies to the payload without its first <paramref name="headerChars"/> characters.
    /// An empty <paramref name="entryId"/> pastes for no entry: nothing is acked by it.
    /// </summary>
    PasteOutcome Paste(JsonValue member, string teamName, string? teamTransport, string payload, string entryId, string boxDir, int headerChars = 0);
}

/// <summary>Mails one event from <c>ahd</c> for the ladder; throws when it cannot be sent.</summary>
public interface IDeliveryMail
{
    /// <summary>Sends <paramref name="request"/> (<c>to</c>, <c>kind</c>, <c>event</c>, <c>text</c>, <c>data</c>, ...) under hierarchy dir <paramref name="home"/>.</summary>
    void Send(string home, JsonObject request);
}

/// <summary>The counts <c>daemon status</c> shows: entries by derived state; <c>Paused</c> is either pause kind.</summary>
/// <param name="Pending">Entries waiting on a rung.</param>
/// <param name="Sending">Entries a relay or paste is in flight for.</param>
/// <param name="Paused">Entries paused for a blocked or failed recipient.</param>
/// <param name="Escalated">Entries that ran out of rungs.</param>
public sealed record DeliveryCounts(int Pending, int Sending, int Paused, int Escalated);

/// <summary>One pass of the delivery ladder over every box of the live registrations' teams.</summary>
public interface IDeliveryLadder
{
    /// <summary>Runs one pass at <paramref name="nowMs"/>; <paramref name="pollMs"/> scales every wait as the daemon's clock does.</summary>
    Task<DeliveryCounts> RunAsync(double nowMs, double pollMs, CancellationToken cancellation);
}

/// <summary>
/// The delivery ladder: an entry nobody acked moves down the rungs on its attempt rows and the clock. Rung 3 asks a carrier to relay
/// it by SendMessage, rung 4 pastes it into the recipient's pane (at most four attempts), rung 5 tells the Orchestrator it failed.
/// </summary>
/// <param name="Mailbox">Registrations, entries, acks, heartbeats, attempt rows and mail.</param>
/// <param name="Core">The pull timeout of a heartbeat.</param>
/// <param name="Roster">Team files.</param>
/// <param name="Config">Pane membership.</param>
/// <param name="Hierarchy">Recipient activity and new ids.</param>
/// <param name="Paste">Placement and the hardened paste.</param>
/// <param name="Clock">The wall clock (mail is stamped with it, not with the pass's moment).</param>
/// <param name="Mail">Where events go; null sends through <paramref name="Mailbox"/> as <c>ahd</c>.</param>
public sealed partial record DeliveryLadder(
    IMailbox Mailbox,
    IMailCore Core,
    IRosterCore Roster,
    IConfigCore Config,
    HierarchyServices Hierarchy,
    IDeliveryPaste Paste,
    IClock Clock,
    IDeliveryMail? Mail = null) : IDeliveryLadder
{
    /// <summary>How long a sent relay may go unacked before an inbox listing says it has not been shown.</summary>
    internal const double HeldRelayMs = 10 * 60_000;

    /// <summary>The poll period every constant is scaled from.</summary>
    internal const double WatchPollMs = 15_000;

    private static readonly HashSet<string> RefuseEscalates = new(StringComparer.Ordinal) { "paste-disabled", "no-pane", "bad_request" };

    [GeneratedRegex("[" + JsText.SpaceClass + "]*\\n[" + JsText.SpaceClass + "]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreaks();

    internal const double ResumeGapMs = 120_000;
    internal const double ResumeGraceMs = 60_000;
    private double _lastPassAt;
    private double _resumedAt = double.NegativeInfinity;
    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _wakeAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _wakeRefusedSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wakeCannot = new(StringComparer.Ordinal);
    private const string OrchestratorWakeText = "ah: mail waiting, run ah msg next";

    /// <summary>The ladder's constants for a poll period.</summary>
    internal sealed record Constants(double Rung3Ms, double FallbackGraceMs, double RetryMs, int MaxAttempts, double MaxAgeMs, double Scale = 1);

    /// <summary>The constants, scaled by <paramref name="pollMs"/> / 15 s.</summary>
    internal static Constants ConstantsFor(double pollMs)
    {
        var k = pollMs / WatchPollMs;
        return new Constants(10 * 60_000 * k, 120_000 * k, 300_000 * k, 4, 24 * 3_600_000 * k, k);
    }

    private sealed record Box(string Address, string Dir, JsonObject Team, string TeamName, string TeamPath, JsonObject? Member);

    /// <summary>The event <c>roster pause-member</c> mails the member it pauses (b456): the one entry of a paused box the ladder still walks.</summary>
    public const string MemberPausedEvent = "member-paused";

    /// <summary>The event <c>roster resume-member</c> mails the member it resumes (b456).</summary>
    public const string MemberResumedEvent = "member-resumed";

    /// <summary>
    /// Whether an entry in the box at <paramref name="boxAddress"/> is on the ladder: everything not from the daemon, and the daemon's
    /// <see cref="LadderedDaemonEvents"/> to the boxes each is meant for. Its other entries, and everything it writes to the
    /// Orchestrator (which the Stop hook drains), are not.
    /// </summary>
    internal static bool IsLaddered(JsonObject entry, string boxAddress, JsonObject? member) =>
        Str(entry, "from") != "ahd" || IsLadderedDaemonEvent(entry, boxAddress, member);

    /// <summary>The daemon's events that are pasted to an idle member like a mail: <c>resume</c> to any member, <c>qa-queued</c> to a QA box.</summary>
    internal static readonly IReadOnlySet<string> LadderedDaemonEvents = new HashSet<string>(StringComparer.Ordinal) { "resume", "qa-queued" };

    private static bool IsLadderedDaemonEvent(JsonObject entry, string boxAddress, JsonObject? member) =>
        Str(entry, "from") == "ahd" && Str(entry, "kind") == "event" && Str(entry, "event") is { } name && LadderedDaemonEvents.Contains(name)
        && (name == "qa-queued"
            ? MeshModule.IsQaMember(member)
            : !boxAddress.StartsWith("orchestrator@", StringComparison.Ordinal));

    private sealed record Facts(double PullTimeoutMs, bool Blocked, bool Failed, bool Working, bool PasteAllowed, string? PasteReason);

    private sealed record Carrier(string? CarrierAddress, string? CarrierSession, string? RelayEntry);

    private sealed record Relay(string Entry, string Address, string Dir, string To, string Line);

    public async Task<DeliveryCounts> RunAsync(double nowMs, double pollMs, CancellationToken cancellation)
    {
        // A long gap between passes is the machine waking, and every mod's last poll is as old as the sleep: give the mods time to poll before calling one quiet.
        if (_lastPassAt > 0 && nowMs - _lastPassAt > ResumeGapMs)
        {
            _resumedAt = nowMs;
        }

        _lastPassAt = nowMs;
        var c = ConstantsFor(pollMs);
        int pending = 0, sending = 0, paused = 0, escalated = 0;
        foreach (var box in Boxes())
        {
            cancellation.ThrowIfCancellationRequested();
            // A sealed quit holds the ladder off; while winding down or stopped only the exempt briefs are open and they still walk it.
            if (Roster.HoldOf(RosterModule.TeamFileHome(box.TeamPath) ?? string.Empty, box.TeamName) is { Phase: "held" })
            {
                continue;
            }

            IReadOnlyList<JsonValue> entries;
            try
            {
                entries = Mailbox.ReadEntries(box.Dir).Entries;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // b456: a paused member gets no re-delivery and raises no unread-at-idle; only the note telling it so walks the ladder
            var memberPaused = box.Member is not null && Str(box.Member, "name") is { } boxMember
                && Roster.PauseOf(RosterModule.TeamFileHome(box.TeamPath) ?? string.Empty, box.TeamName == "default" ? null : box.TeamName, boxMember) is { Paused: true };
            var sweep = Mailbox.LadderSweep(box.Dir);
            var byEntry = sweep.Open.ToDictionary(static r => r.Id, static r => r.Rows, StringComparer.Ordinal);
            foreach (var value in entries)
            {
                if (value.Kind != JsonKind.Map)
                {
                    continue;
                }

                var entry = value.AsObject();
                if (!IsLaddered(entry, box.Address, box.Member) || Mailbox.IsMemberDigestNote(JsonValue.FromObject(entry), box.Address)
                    || (memberPaused && Str(entry, "event") != MemberPausedEvent))
                {
                    continue;
                }

                // b459: a settled ladder is left alone, acked or not: the evaluation returns on its newest row and the state check then skips it
                if (Str(entry, "id") is { } settledId && sweep.Settled.ContainsKey(settledId))
                {
                    continue;
                }

                var rows = byEntry.TryGetValue(Str(entry, "id") ?? string.Empty, out var existing) ? [.. existing] : new List<JsonObject>();
                try
                {
                    await EvaluateEntryAsync(box, entry, rows, nowMs, c).ConfigureAwait(false);
                    var d = Derived(entry, rows);
                    var state = Str(d, "state");
                    if (DrainedState.IsTerminal(state) || Mailbox.IsAcked(box.Dir, Str(entry, "id") ?? string.Empty))
                    {
                        continue;
                    }

                    var facts = FactsOf(box, entry, nowMs);

                    if (facts.Failed || (facts.Blocked && Rung(d) == 4 && state == "pending"))
                    {
                        paused++;
                    }
                    else if (state == "escalated")
                    {
                        escalated++;
                    }
                    else if (state == "sending")
                    {
                        sending++;
                    }
                    else
                    {
                        pending++;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // one entry's failure (a busy box) is retried on the next tick and never stops the pass
                }
            }
        }

        return new DeliveryCounts(pending, sending, paused, escalated);
    }

    // ---------------------------------------------------------------- boxes

    private List<Box> Boxes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var boxes = new List<Box>();
        foreach (var path in Mailbox.LiveRegistrations().SelectMany(RegistrationTeams.Of))
        {
            if (!seen.Add(path) || !Mailbox.IsNamedTeamPath(path))
            {
                continue;
            }

            var record = Roster.ReadTeamFileAt(path);
            if (record.Kind != JsonKind.Map)
            {
                continue;
            }

            var team = record.AsObject();
            var teamName = Mailbox.TeamNameOf(path);
            boxes.Add(new Box(HierarchyModule.OrchestratorAddress(teamName), Mailbox.BoxDirFor(path, "orchestrator"), team, teamName, path, null));
            if (team.TryGetValue("members", out var members) && members.Kind == JsonKind.Array)
            {
                foreach (var m in members.AsArray())
                {
                    if (m.Kind == JsonKind.Map && Str(m.AsObject(), "name") is { Length: > 0 } name)
                    {
                        boxes.Add(new Box($"{name}@{teamName}", Mailbox.BoxDirFor(path, name), team, teamName, path, m.AsObject()));
                    }
                }
            }
        }

        return boxes;
    }

    /// <summary>The one session registered for <paramref name="address"/>, or null when none or several claim it.</summary>
    private JsonObject? SoleSession(string address)
    {
        JsonObject? found = null;
        var count = 0;
        foreach (var r in Mailbox.AllRegistrations())
        {
            var claimed = false;
            if (r.TryGetValue("addresses", out var addresses) && addresses.Kind == JsonKind.Array)
            {
                claimed = addresses.AsArray().Any(a => a.Kind == JsonKind.Text && a.AsString() == address);
            }
            else if (Str(r, "address") == address)
            {
                claimed = true;
            }

            if (claimed)
            {
                count++;
                found = r;
            }
        }

        return count == 1 ? found : null;
    }

    // ---------------------------------------------------------------- recipient facts

    /// <summary>The recipient's recorded activity: its <c>to_session</c>'s record (or the sole session registered for it), and a pane member's <c>pane-&lt;name&gt;</c> record.</summary>
    private string? ActivityOf(Box box, JsonObject? entry)
    {
        var seen = RecordsOf(box, entry).Select(r => Str(r.AsObject(), "activity")).ToList();
        return Pick(seen);
    }

    /// <summary>The recipient's verdict, from the same records as <see cref="ActivityOf"/>; the worst of them wins as it does there.</summary>
    private string? VerdictOf(Box box, JsonObject? entry, double nowMs) =>
        Pick(RecordsOf(box, entry).Select(r => (string?)HierarchyModule.RecordVerdict(r, nowMs, null, dispatchOpen: true)).ToList());

    private static string? Pick(List<string?> seen) =>
        seen.Contains("failed") ? "failed" : seen.Contains("blocked") ? "blocked" : seen.Count > 0 && !string.IsNullOrEmpty(seen[0]) ? seen[0] : null;

    private List<JsonValue> RecordsOf(Box box, JsonObject? entry)
    {
        var records = new List<JsonValue>();
        var home = RosterModule.TeamFileHome(box.TeamPath);
        if (home is null)
        {
            return records;
        }

        var files = new List<string>();
        if (box.Member is not null && Config.IsPaneMember(JsonValue.FromObject(box.Member)) && Str(box.Member, "name") is { } name)
        {
            files.Add($"pane-{name}.json");
        }

        var sid = entry is not null && Str(entry, "to_session") is { Length: > 0 } toSession
            ? toSession
            : SoleSession(box.Address) is { } sole ? Str(sole, "session_id") : null;
        if (!string.IsNullOrEmpty(sid))
        {
            files.Add($"{sid}.json");
        }

        foreach (var f in files)
        {
            var record = Hierarchy.Status.ReadActivity(home, f);
            if (record.Kind == JsonKind.Map)
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <summary>
    /// Whether the recipient's activity holds a pull entry back from the relay. A member parked on a permission prompt cannot pull
    /// at its turn end, so it is relayed at the pull timeout; the Orchestrator parked on a question is the user's to answer and its
    /// mail waits.
    /// </summary>
    private static bool HoldsFor(Box box, string? activity) =>
        activity == "blocked" ? box.Address.StartsWith("orchestrator@", StringComparison.Ordinal) : HierarchyModule.IsBusy(activity);

    private static readonly string[] FreshnessFields = ["at", "tool_at", "working_at"];

    /// <summary>
    /// Whether an activity record of the recipient was written within <see cref="HierarchyModule.BlockedStaleMs"/>, how long a recorded
    /// state is believed (b404). The newest of <c>at</c>, <c>tool_at</c> and <c>working_at</c> counts: the writer keeps <c>at</c> for an
    /// unchanged state, so one long working stint advances only the other two. A single tool call longer than the limit still reads stale.
    /// </summary>
    private bool ActivityFresh(Box box, JsonObject entry, double nowMs) =>
        RecordsOf(box, entry).Any(r => FreshnessFields.Select(k => Parse(Str(r.AsObject(), k))).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max() is var at
            && !double.IsNaN(at) && nowMs - at <= HierarchyModule.BlockedStaleMs);

    private Facts FactsOf(Box box, JsonObject entry, double nowMs)
    {
        var hb = Mailbox.ReadHeartbeat(box.Dir, Str(entry, "to_session") is { Length: > 0 } s ? s : null, nowMs);
        var activity = VerdictOf(box, entry, nowMs);
        var timeout = Core.PullTimeoutMs(hb is null ? (JsonValue?)null : JsonValue.FromObject(hb));
        if (box.Member is null)
        {
            return new Facts(timeout, activity == "blocked", activity == "failed", HoldsFor(box, activity), false, "no-pane");
        }

        var place = Paste.PlacementOf(JsonValue.FromObject(box.Member), Str(box.Team, "transport"));
        if (box.Member.TryGetValue("paste", out var p) && p.Kind == JsonKind.Bool && !p.AsBool())
        {
            return new Facts(timeout, activity == "blocked", activity == "failed", HoldsFor(box, activity), false, "paste-disabled");
        }

        var pasteable = place.TryGetValue("pasteable", out var v) && v.Kind == JsonKind.Bool && v.AsBool();
        return new Facts(timeout, activity == "blocked", activity == "failed", HoldsFor(box, activity), pasteable, pasteable ? null : Str(place, "reason"));
    }

    // ---------------------------------------------------------------- rung 3: the carrier

    private Relay? RelayOf(Box box, JsonObject entry)
    {
        var forced = Copy(entry);
        forced["route"] = "fallback";
        var name = box.Address.Split('@')[0];
        var to = new ResolvedAddress(
            name,
            box.TeamName,
            box.Address,
            box.Member is null ? (JsonValue?)null : JsonValue.FromObject(box.Member),
            box.Team.TryGetValue("orchestrator", out var orchestrator) ? orchestrator : (JsonValue?)null,
            box.TeamPath,
            RosterModule.TeamFileHome(box.TeamPath),
            box.Dir);
        var described = Mailbox.DescribeSend(forced, to, null, null, string.Empty);
        if (!described.TryGetValue("sendmessage", out var send) || send.Kind != JsonKind.Map)
        {
            return null;
        }

        return new Relay(Str(entry, "id") ?? string.Empty, box.Address, box.Dir, Str(send.AsObject(), "to") ?? string.Empty, Mailbox.SendMessageLine(send));
    }

    /// <summary>Whether the live session of the box's address is a harness other than Claude Code.</summary>
    private bool NonClaudeReceiver(Box box) =>
        SoleSession(box.Address) is { } registration && Str(registration, "harness") is { Length: > 0 } harness && harness is not ("claude" or "claude-mod");

    private Carrier ChooseCarrier(Box box, JsonObject entry, double nowMs)
    {
        var none = new Carrier(null, null, null);
        if (RelayOf(box, entry) is null)
        {
            return none;
        }

        var from = Str(entry, "from") ?? string.Empty;
        var reg = SoleSession(from);
        var live = reg is not null && Mailbox.LiveClaudeSender(Str(reg, "session_id")) ? reg : null;
        var route = Str(entry, "route");
        if (live is not null && route == "fallback")
        {
            return new Carrier(from, Str(live, "session_id"), null);
        }

        string? senderBox = null;
        if (live is not null && live.TryGetValue("boxes", out var boxes) && boxes.Kind == JsonKind.Map)
        {
            senderBox = Str(boxes.AsObject(), from);
        }

        if (live is not null && !string.IsNullOrEmpty(senderBox))
        {
            var heartbeat = Mailbox.ReadHeartbeat(senderBox, Str(live, "session_id"), nowMs);
            if (Mailbox.HeartbeatFresh(heartbeat is null ? (JsonValue?)null : JsonValue.FromObject(heartbeat), nowMs))
            {
                return new Carrier(from, Str(live, "session_id"), Hierarchy.Core.NewId());
            }
        }

        if (box.Member is null && box.Address.StartsWith("orchestrator@", StringComparison.Ordinal))
        {
            return none;
        }

        var orchAddress = HierarchyModule.OrchestratorAddress(box.TeamName);
        var orch = SoleSession(orchAddress);
        return new Carrier(orchAddress, orch is null ? null : Str(orch, "session_id"), Hierarchy.Core.NewId());
    }

    private JsonObject RelayRequest(Box box, JsonObject entry, JsonObject row, Relay relay)
    {
        var id = Str(entry, "id") ?? string.Empty;
        var line = RelayInstruction.Relayed(id, Str(entry, "created") ?? string.Empty, relay.Line);
        var facts = RelayFacts(box, entry);
        var data = new JsonObject
        {
            ["entry"] = id,
            ["recipient"] = box.Address,
            ["relay"] = new JsonObject { ["entry"] = relay.Entry, ["address"] = relay.Address, ["dir"] = relay.Dir, ["to"] = relay.To, ["line"] = line },
        };
        if (facts is not null)
        {
            data["facts"] = facts;
        }

        var request = new JsonObject
        {
            ["id"] = Str(row, "relay_entry") ?? string.Empty,
            ["to"] = Str(row, "carrier") ?? string.Empty,
            ["kind"] = "event",
            ["event"] = "relay-needed",
            ["text"] = RelayInstruction.Text(id, box.Address, relay.To, line, facts),
            ["data"] = data,
        };
        if (Str(row, "carrier_session") is { Length: > 0 } session)
        {
            request["to_session"] = session;
        }

        return request;
    }

    /// <summary>
    /// What happened since <paramref name="entry"/> was sent that bears on whether it still needs relaying, or null when nothing did:
    /// the newest report the recipient sent the sender after it, and the unread newer entries from the same sender. Facts only; neither ends the obligation.
    /// </summary>
    private JsonObject? RelayFacts(Box box, JsonObject entry)
    {
        var sender = Str(entry, "from") ?? string.Empty;
        var id = Str(entry, "id") ?? string.Empty;
        var created = Parse(Str(entry, "created"));
        var facts = new JsonObject();
        try
        {
            var senderBox = Boxes().FirstOrDefault(b => b.Address == sender);
            if (senderBox is not null)
            {
                var report = Mailbox.ReadEntries(senderBox.Dir).Entries
                    .Where(static v => v.Kind == JsonKind.Map)
                    .Select(static v => v.AsObject())
                    .Where(r => Str(r, "from") == box.Address && Parse(Str(r, "created")) > created
                        && Str(r, "ref") is { Length: > 0 } reference && reference.EndsWith("--response.md", StringComparison.Ordinal))
                    .OrderByDescending(r => Parse(Str(r, "created")))
                    .FirstOrDefault();
                if (report is not null)
                {
                    var request = Hierarchy.Core.ParseMsgFilename(Path.GetFileName(Str(report, "ref")!))?.Id;
                    var reported = new JsonObject { ["entry"] = Str(report, "id") ?? string.Empty, ["created"] = Str(report, "created") ?? string.Empty };
                    if (request is not null)
                    {
                        reported["request"] = request;
                        var names = (Str(entry, "text") ?? string.Empty) + " " + (Str(entry, "ref") ?? string.Empty);
                        reported["same_request"] = names.Contains(request, StringComparison.Ordinal);
                    }

                    facts["reported_since"] = reported;
                }
            }

            var newer = Mailbox.ReadEntries(box.Dir).Entries
                .Where(static v => v.Kind == JsonKind.Map)
                .Select(static v => v.AsObject())
                .Where(e => Str(e, "from") == sender && Str(e, "id") != id && Parse(Str(e, "created")) > created && !Mailbox.IsAcked(box.Dir, Str(e, "id") ?? string.Empty))
                .OrderByDescending(e => Parse(Str(e, "created")))
                .Take(3)
                .Select(e =>
                {
                    var item = new JsonObject { ["id"] = Str(e, "id") ?? string.Empty, ["created"] = Str(e, "created") ?? string.Empty };
                    if (Str(e, "supersedes") == id)
                    {
                        item["supersedes"] = true;
                    }

                    return item;
                })
                .ToList();
            if (newer.Count > 0)
            {
                var list = new JsonArray();
                foreach (var item in newer)
                {
                    list.Add(item);
                }

                facts["newer_from_sender"] = list;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // facts are advice to the carrier; the relay request stands without them
        }

        return facts.Keys.Any() ? facts : null;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is a brief to its request's own recipient (<c>to_name@team</c>) whose request's response holds a
    /// report (not the template) created at or after the entry. A brief to someone else with that ref, or one sent after the report, is
    /// not; false when any of it cannot be read.
    /// </summary>
    private bool Reported(JsonObject entry)
    {
        if (Str(entry, "kind") != "brief" || Str(entry, "ref") is not { Length: > 0 } reference
            || Hierarchy.Core.ParseMsgFilename(reference)?.Id is not { } request
            || Parse(Str(entry, "created")) is not (>= 0 and var sentAt))
        {
            return false;
        }

        try
        {
            return Hierarchy.Core.ReadMsgFile(reference).Member("fm") is { IsMap: true } asked
                && Str(asked.AsObject(), "to_name") is { Length: > 0 } toName && Str(asked.AsObject(), "team") is { Length: > 0 } team
                && Str(entry, "to") == $"{toName}@{team}"
                && Hierarchy.Messages.ResponsePlan(reference) is { IsMap: true } plan && plan.Member("path") is { IsText: true } path
                && Hierarchy.Messages.ReportStatus(path.AsString(), request) == "reported"
                && Hierarchy.Core.ReadMsgFile(path.AsString()).Member("fm") is { IsMap: true } answer
                && Parse(Str(answer.AsObject(), "created")) >= sentAt;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>What the Orchestrator, or the sender of an entry to the Orchestrator's own box, is told when an entry runs out of rungs: the recipient, the entry, and each rung tried with its reason.</summary>
    internal static string FailureText(JsonObject entry, string address, IEnumerable<JsonObject> attemptRows)
    {
        var id = Str(entry, "id");
        // A claim row carries no rung of its own: it belongs to the rung the entry was on, the last one named before it (b403).
        var current = Str(entry, "route") == "pull" ? "2" : "3";
        var tried = attemptRows
            .Where(r => Str(r, "id") == id && Str(r, "state") != "sending")
            .Select(r => $"rung {(current = r.ContainsKey("rung") ? Show(r, "rung") : current)} {Str(r, "state")}{(Str(r, "reason") is { Length: > 0 } reason ? $" ({reason})" : string.Empty)}")
            .ToList();
        return $"delivery-failed: {Str(entry, "kind")} {id} from {Str(entry, "from")} to {address} was never acknowledged. Tried: {string.Join("; ", tried)}.";
    }

    /// <summary>
    /// Releases a plugin-prompt claim that was never taken: it is still <c>pending</c> or <c>fallback-claimed</c> once the pull
    /// timeout has passed since the entry was created, so no session is polling for it. The entry then follows the unclaimed rungs.
    /// True when this call released the claim.
    /// </summary>
    private bool ReleaseNoPoller(Box box, JsonObject entry, JsonObject claim, double created, double now, double pullTimeoutMs)
    {
        if (NonClaudeReceiver(box) || ClaimMachine.StateOf(claim) is not (ClaimStates.Pending or ClaimStates.FallbackClaimed)
            || (claim.Get("carrier") is { IsText: true } carrier && carrier.AsString() == "relay") || !(now >= created + pullTimeoutMs))
        {
            return false;
        }

        return Mailbox.Claim(box.Dir, Str(entry, "id") ?? string.Empty, ClaimActions.Release, new JsonObject { ["reason"] = ClaimMachine.NoPollerReason, ["actor"] = "daemon" }).Won;
    }

    /// <summary>
    /// Releases the claim of an unacked entry that was handed to a Claude session (the plugin prompt, or a relay that sent it) when
    /// that session has been idle, without taking it, for at least the resubmit time since the entry was created or the idle began,
    /// whichever is later. Mails the Orchestrator once, in the same step as the release; true when this call released the claim.
    /// </summary>
    private bool ReleaseUnreadAtIdle(Box box, JsonObject entry, JsonObject claim, double created, double now)
    {
        if (box.Member is null || NonClaudeReceiver(box)
            || ClaimMachine.StateOf(claim) is not (ClaimStates.FallbackClaimed or ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed))
        {
            return false;
        }

        var records = RecordsOf(box, entry).Select(static r => r.AsObject()).ToList();
        if (records.Count == 0
            || records.Any(r => Str(r, "activity") != "idle" || Str(r, "state_since") is not { Length: > 0 } || HierarchyModule.IdleOnBackgroundWork(JsonValue.FromObject(r), now)))
        {
            return false;
        }

        var starts = records.Select(r => Parse(Str(r, "state_since"))).ToList();
        if (starts.Any(double.IsNaN))
        {
            return false;
        }

        var idleSince = starts.Max();
        if (now - Math.Max(created, idleSince) < IMailCore.ResubmitMs)
        {
            return false;
        }

        var id = Str(entry, "id") ?? string.Empty;
        var released = Mailbox.Claim(box.Dir, id, ClaimActions.Release, new JsonObject { ["reason"] = ClaimMachine.UnreadAtIdleReason, ["actor"] = "daemon" });
        if (!released.Won)
        {
            return false;
        }

        var (to, home) = EventTarget(box);
        var orch = SoleSession(to);
        try
        {
            var request = new JsonObject
            {
                ["to"] = to,
                ["kind"] = "event",
                ["event"] = "unread-at-idle",
                ["text"] = $"UNREAD-AT-IDLE: {Str(entry, "kind")} {id} from {Str(entry, "from")} to {box.Address} was handed over and not read; {box.Address} has been idle since {Iso(idleSince)}. The claim is released and the entry goes to the next delivery rung.",
                ["data"] = new JsonObject { ["entry"] = id, ["recipient"] = box.Address },
            };
            if (orch is not null && Str(orch, "session_id") is { Length: > 0 } session)
            {
                request["to_session"] = session;
            }

            SendMail(home, request);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the release and the ladder row that follows are the record; the mail is the courtesy
        }

        return true;
    }

    /// <summary>
    /// Who hears an event about this box's member, and the project home that address is sent from: its valid, live lead's Orchestrator
    /// address with the lead's team's home, else its team's Orchestrator with the member's own.
    /// </summary>
    private (string To, string Home) EventTarget(Box box)
    {
        var boxes = Boxes();
        var teamsDir = Path.GetDirectoryName(box.TeamPath);

        // Two projects may each have a team of one name: the member's own hierarchy directory decides, and a name held by several
        // other projects names nobody.
        Box? Pick(string name)
        {
            var named = boxes.Where(b => b.TeamName == name).ToList();
            return named.FirstOrDefault(b => Path.GetDirectoryName(b.TeamPath) == teamsDir) ?? (named.Count == 1 ? named[0] : null);
        }

        var to = HierarchyModule.EventAddress(
            box.TeamName,
            box.Member is { } row ? JsonValue.FromObject(row) : JsonValue.Null,
            name => Pick(name) is { } found ? JsonValue.FromObject(found.Team) : JsonValue.Null,
            Hierarchy.Core.IdentityLive);
        var team = to[(to.IndexOf('@', StringComparison.Ordinal) + 1)..];
        var path = Pick(team)?.TeamPath ?? box.TeamPath;
        return (to, RosterModule.TeamFileHome(path) ?? string.Empty);
    }

    // ---------------------------------------------------------------- one entry

    private async Task EvaluateEntryAsync(Box box, JsonObject entry, List<JsonObject> rows, double now, Constants c)
    {
        var id = Str(entry, "id") ?? string.Empty;
        lock (_gate)
        {
            if (_inflight.Contains(id))
            {
                return;
            }
        }

        var last = rows.Count > 0 ? rows[^1] : null;
        if (last is not null && DrainedState.IsSettled(Str(last, "state")))
        {
            return;
        }

        var acked = Mailbox.IsAcked(box.Dir, id);
        var created = Parse(Str(entry, "created"));
        if (!acked && last is null && now - created > c.MaxAgeMs)
        {
            return;
        }

        var nowIso = Iso(now);
        bool Write(JsonObject row)
        {
            var full = new JsonObject { ["id"] = id };
            foreach (var key in row.Keys)
            {
                full[key] = row[key];
            }

            if (!Mailbox.AppendLadderRow(box.Dir, full, rows.Count + 1L))
            {
                return false;
            }

            var kept = Copy(full);
            kept["seq"] = rows.Count + 1;
            kept["ladder"] = true;
            kept["at"] = nowIso;
            rows.Add(kept);
            last = kept;
            return true;
        }

        bool Delivered(string? reason = null)
        {
            var ack = Mailbox.ReadAck(box.Dir, id);
            var row = new JsonObject
            {
                ["rung"] = last is not null ? last["rung"] : Str(entry, "route") == "pull" ? 2 : 3,
                ["state"] = "delivered",
            };
            if (reason is not null)
            {
                row["reason"] = reason;
            }

            if (ack is { Kind: JsonKind.Map } ackValue)
            {
                if (Str(ackValue.AsObject(), "by") is { Length: > 0 } by)
                {
                    row["by"] = by;
                }

                if (Str(ackValue.AsObject(), "at") is { Length: > 0 } at)
                {
                    row["acked_at"] = at;
                }
            }

            return Write(row);
        }

        if (acked)
        {
            _ = Delivered();
            return;
        }

        // b466: a brief its recipient has reported on was read; no rung hands it over again, and no unread or failed alarm follows.
        // It is acked too, so the mod's poll does not hand it over again when no resolved record of it is left (a mod restart).
        if (Reported(entry))
        {
            _ = Mailbox.AckIds(box.Dir, [id], "adapter");
            _ = Delivered("reported");
            return;
        }

        // MailDue leaves an orchestrator box to the doorbell, so its mail is never claimed and "no poller" would be false: it waits, on any rung, while the doorbell lives.
        if (Mailbox.DoorbellLive(box.Dir, box.Address, Str(entry, "to_session"), now))
        {
            if (Mailbox.DoorbellGoneUnread(box.Dir, box.Address, Str(entry, "to_session"), now) is { } unread)
            {
                await WakeOrchestratorAsync(box, entry, unread, now, c).ConfigureAwait(false);
            }

            return;
        }

        // A live Orchestrator whose mod has gone quiet is not delivery-failed: its mail is rung when the mod returns. Past a limit of quiet it is woken by its pane, with no held
        // to go on, so a Stop can be pasted over; no ring can be tried without a mod. Only when that wake cannot carry does the walk go on, since no toast can reach a person.
        if (box.Member is null && box.Address.StartsWith("orchestrator@", StringComparison.Ordinal)
            && (Str(entry, "to_session") ?? (SoleSession(box.Address) is { } sole ? Str(sole, "session_id") : null)) is { Length: > 0 } quiet && Mailbox.DoorbellSessionLive(quiet))
        {
            var since = Math.Max(Mailbox.DoorbellLastPoll(box.Dir, quiet), Timestamps.TryParseMs(Str(entry, "created") ?? string.Empty, out var sentAt) ? sentAt : 0);
            if (now - since < IMailCore.DoorbellRemindMs || now - _resumedAt < ResumeGraceMs || !await WakeOrchestratorAsync(box, entry, quiet, now, c).ConfigureAwait(false))
            {
                return;
            }
        }

        if (last is not null && Str(last, "state") == "escalated")
        {
            return;
        }

        var f = FactsOf(box, entry, now);
        if (f.Failed)
        {
            return;
        }

        void Escalate(string? reason)
        {
            if (!Write(new JsonObject { ["rung"] = 5, ["state"] = "escalated", ["reason"] = reason is null ? JsonValue.Null : JsonValue.FromString(reason) }))
            {
                return;
            }

            // A box with no member row is the Orchestrator's: there is no one above it, so its sender is told and can re-send or report another way.
            var sender = Str(entry, "from");
            if (box.Member is null && sender is null or "ahd" or "user")
            {
                return;
            }

            var (to, home) = box.Member is null ? (sender!, RosterModule.TeamFileHome(box.TeamPath) ?? string.Empty) : EventTarget(box);
            var orch = SoleSession(to);
            try
            {
                var request = new JsonObject
                {
                    ["to"] = to,
                    ["kind"] = "event",
                    ["event"] = "delivery-failed",
                    ["text"] = FailureText(entry, box.Address, Mailbox.AttemptsOf(box.Dir, entry.Get("id"))),
                    ["data"] = new JsonObject { ["entry"] = id, ["recipient"] = box.Address },
                };
                if (orch is not null && Str(orch, "session_id") is { Length: > 0 } session)
                {
                    request["to_session"] = session;
                }

                SendMail(home, request);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the rung-5 row is the record; the mail is the courtesy
            }
        }

        bool ToRung4(string reason) => Write(new JsonObject
        {
            ["rung"] = 4,
            ["state"] = "pending",
            ["attempt"] = 1,
            ["next_at"] = nowIso,
            ["reason"] = reason,
        });

        if (last is null)
        {
            // An entry with a claim record is carried by its relay or by the plugin prompt, never by a rung-3 relay request; only a
            // receiver that is not a Claude session is pasted to, once the pull has had its time. A resume the daemon wrote has no
            // session to relay through, so with or without a claim, and on either route, it waits for the pull and is then pasted;
            // the same holds for the daemon's other laddered events.
            var daemonResume = Str(entry, "from") == "ahd" && IsLadderedDaemonEvent(entry, box.Address, box.Member);
            var claim = daemonResume ? null : Mailbox.ReadClaim(box.Dir, id);
            if (claim is not null && ClaimMachine.StateOf(claim) is ClaimStates.RelayFailed or ClaimStates.Released)
            {
                claim = null;
            }
            else if (claim is not null && ReleaseUnreadAtIdle(box, entry, claim, created, now))
            {
                claim = null;
            }
            else if (claim is not null && ReleaseNoPoller(box, entry, claim, created, now, f.PullTimeoutMs))
            {
                claim = null;
            }

            if (daemonResume || claim is not null)
            {
                if ((daemonResume || NonClaudeReceiver(box)) && now >= created + f.PullTimeoutMs)
                {
                    _ = ToRung4(daemonResume ? "ahd-" + Str(entry, "event") : "non-claude-receiver");
                }

                return;
            }

            if (Str(entry, "route") == "pull")
            {
                if (!(now >= created + f.PullTimeoutMs))
                {
                    return;
                }

                // A recipient that is mid-turn pulls at its turn end, so it is relayed only once it has had as long as the work
                // is expected to take (its eta's check-in threshold), unless it stops working first.
                var busyHoldMs = Hierarchy.Messages.ThresholdFor(entry.TryGetValue("eta", out var eta) ? eta : JsonValue.Null) * 1000 * c.Scale;
                if (f.Working && !(now >= created + busyHoldMs))
                {
                    return;
                }
            }

            var k = ChooseCarrier(box, entry, now);
            _ = Write(new JsonObject
            {
                ["rung"] = 3,
                ["state"] = "pending",
                ["attempt"] = 1,
                ["carrier"] = Nullable(k.CarrierAddress),
                ["carrier_session"] = Nullable(k.CarrierSession),
                ["relay_entry"] = Nullable(k.RelayEntry),
            });
            return;
        }

        var rung = Rung(last);
        var state = Str(last, "state");
        if (rung == 3 && state == "pending")
        {
            if (string.IsNullOrEmpty(Str(last, "carrier")))
            {
                _ = ToRung4("no-carrier");
                return;
            }

            // A claim released for having no poller, or whose relay failed, is pasted, never relayed: the mail-claim gate refuses a
            // relay of either, so a relay request for it would order a SendMessage nobody may send.
            if (ClaimMachine.StateOf(Mailbox.ReadClaim(box.Dir, id)) is ClaimStates.Released or ClaimStates.RelayFailed)
            {
                _ = ToRung4(ClaimMachine.StateOf(Mailbox.ReadClaim(box.Dir, id)) == ClaimStates.Released ? ClaimMachine.NoPollerReason : ClaimStates.RelayFailed);
                return;
            }

            if (!string.IsNullOrEmpty(Str(last, "relay_entry")))
            {
                try
                {
                    var relay = RelayOf(box, entry) ?? throw new InvalidOperationException("no relay");
                    SendMail(RosterModule.TeamFileHome(box.TeamPath) ?? string.Empty, RelayRequest(box, entry, last, relay));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _ = ToRung4("relay-send-failed");
                    return;
                }
            }

            _ = Write(new JsonObject { ["rung"] = 3, ["state"] = "sending", ["attempt"] = 1 });
            return;
        }

        if (rung == 3 && state == "sending")
        {
            var pending = rows.FirstOrDefault(r => Rung(r) == 3 && Str(r, "state") == "pending");
            var began = pending is not null ? Parse(Str(pending, "at")) : created;
            var graceFloor = Str(entry, "route") == "fallback" ? created + f.PullTimeoutMs + c.FallbackGraceMs : 0;
            var deadline = Math.Max(began + c.Rung3Ms, graceFloor);
            if (now >= deadline)
            {
                _ = ToRung4("rung3-timeout");
            }

            return;
        }

        if (rung == 4 && state == "unconfirmed")
        {
            Escalate("unconfirmed");
            return;
        }

        if (rung == 4 && state == "sending")
        {
            _ = Write(new JsonObject { ["rung"] = 4, ["state"] = "unconfirmed", ["attempt"] = last["attempt"], ["reason"] = "crash" });
            Escalate("unconfirmed");
            return;
        }

        if (rung == 4 && state == "pending")
        {
            if (Str(last, "next_at") is { Length: > 0 } nextAt && now < Parse(nextAt))
            {
                return;
            }

            if (f.Blocked)
            {
                return;
            }

            if (!f.PasteAllowed)
            {
                Escalate(f.PasteReason);
                return;
            }

            var n = last.TryGetValue("attempt", out var attemptValue) && attemptValue.Kind == JsonKind.Number && attemptValue.AsNumber() != 0 ? (int)attemptValue.AsNumber() : 1;
            lock (_gate)
            {
                _ = _inflight.Add(id);
            }

            try
            {
                if (Mailbox.IsAcked(box.Dir, id))
                {
                    _ = Delivered();
                    return;
                }

                if (!Write(new JsonObject { ["rung"] = 4, ["state"] = "sending", ["attempt"] = n, ["pid"] = Environment.ProcessId }))
                {
                    return;
                }

                var relayEntry = JsonValue.FromObject(entry);
                var payload = LineBreaks().Replace(Mailbox.RelayLine(relayEntry, box.Address), " ");
                var headerChars = Mailbox.RelayHeaderLength(relayEntry, box.Address);

                // b413: an entry withdrawn (a resume superseded) since its sending row is not typed in next to its successor.
                if (DrainedState.IsClosed(Mailbox.LadderRowsOf(box.Dir, id)))
                {
                    return;
                }

                var outcome = await Task.Run(() => Paste.Paste(
                    JsonValue.FromObject(box.Member!),
                    Str(box.Team, "team_id") is { Length: > 0 } teamId ? teamId : "team",
                    Str(box.Team, "transport"),
                    payload,
                    id,
                    box.Dir,
                    headerChars)).ConfigureAwait(false);
                if (outcome.Outcome == "confirmed")
                {
                    if (Mailbox.IsAcked(box.Dir, id))
                    {
                        _ = Delivered();
                    }
                }
                else if (outcome.Outcome == "mailed")
                {
                    // A pane herdr gives no status is not typed into; the entry stands in the mailbox, which the member reads on its own.
                    _ = Delivered();
                }
                else if (outcome.Outcome == "unconfirmed")
                {
                    _ = Write(new JsonObject { ["rung"] = 4, ["state"] = "unconfirmed", ["attempt"] = n, ["reason"] = Nullable(outcome.Reason) });
                    Escalate("unconfirmed");
                }
                else if (outcome.Reason is "blocked" or "busy"
                    || (f.Working && outcome.Reason is not "not-live" && (outcome.Reason is null || !RefuseEscalates.Contains(outcome.Reason)) && ActivityFresh(box, entry, now)))
                {
                    // A pane that is mid-turn or holds a draft is not typed into and not given up on: the item waits, with no attempt
                    // spent, until the pane is idle. So does any refused paste to a member whose fresh activity says it is working or
                    // waiting on background work (b403): it reads its mail at its next turn. A stale record spends attempts as for a
                    // gone member, and delivery-failed follows (b404).
                    _ = Write(new JsonObject
                    {
                        ["rung"] = 4,
                        ["state"] = "pending",
                        ["attempt"] = n,
                        ["next_at"] = Iso(now + c.RetryMs),
                        ["reason"] = Nullable(outcome.Reason),
                    });
                }
                else if (outcome.Reason is not null && RefuseEscalates.Contains(outcome.Reason))
                {
                    Escalate(outcome.Reason);
                }
                else if (n >= c.MaxAttempts)
                {
                    Escalate("retries-exhausted");
                }
                else
                {
                    _ = Write(new JsonObject
                    {
                        ["rung"] = 4,
                        ["state"] = "pending",
                        ["attempt"] = n + 1,
                        ["next_at"] = Iso(now + c.RetryMs),
                        ["reason"] = Nullable(outcome.Reason),
                    });
                }
            }
            finally
            {
                lock (_gate)
                {
                    _ = _inflight.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// An Orchestrator whose poll runs but whose mail sits unread is woken by a fixed line in its own pane, at most once a limit however much mail waits. The mail stays
    /// queued for <c>ah msg next</c>: the paste acks nothing and no sender is told it failed. With no pane, a failed paste, or a limit of refusals the person is told, through the
    /// doorbell's escalation. Returns whether the session cannot be woken at the moment, which is what a session with no running mod to show that escalation needs to know.
    /// </summary>
    private async Task<bool> WakeOrchestratorAsync(Box box, JsonObject entry, string session, double now, Constants c)
    {
        lock (_gate)
        {
            if (_wakeAt.GetValueOrDefault(session) > now)
            {
                return _wakeCannot.Contains(session);
            }
        }

        var facts = FactsOf(box, entry, now);
        if (facts.Blocked || facts.Failed)
        {
            return false;
        }

        lock (_gate)
        {
            if (_wakeAt.GetValueOrDefault(session) > now)
            {
                return _wakeCannot.Contains(session);
            }

            _wakeAt[session] = now + IMailCore.DoorbellRemindMs;
        }

        var pane = Mailbox.ReadRegistration(session) is { } registration ? Str(registration, "pane_id") : null;
        var member = new JsonObject { ["name"] = "orchestrator", ["transport_id"] = pane is null ? JsonValue.Null : pane, ["target"] = pane is null ? JsonValue.Null : pane };
        var outcome = await Task.Run(() => Paste.Paste(
            JsonValue.FromObject(member),
            Str(box.Team, "team_id") is { Length: > 0 } teamId ? teamId : "team",
            Str(box.Team, "transport"),
            OrchestratorWakeText,
            string.Empty,
            box.Dir)).ConfigureAwait(false);
        Mailbox.AppendAttempt(box.Dir, new JsonObject
        {
            ["state"] = "doorbell-wake",
            ["session_id"] = session,
            ["outcome"] = outcome.Outcome,
            ["reason"] = Nullable(outcome.Reason),
        });
        if (outcome.Outcome == "confirmed")
        {
            lock (_gate)
            {
                _ = _wakeRefusedSince.Remove(session);
                _ = _wakeCannot.Remove(session);
            }

            return false;
        }

        if (outcome.Reason is "blocked" or "busy" or "lock-busy")
        {
            lock (_gate)
            {
                _wakeAt[session] = now + c.RetryMs;
                _ = _wakeRefusedSince.TryAdd(session, now);
                if (now - _wakeRefusedSince[session] < IMailCore.DoorbellRemindMs)
                {
                    return _wakeCannot.Contains(session);
                }
            }
        }

        Mailbox.MarkDoorbellUncarried(session, now, outcome.Reason ?? outcome.Outcome);
        lock (_gate)
        {
            _ = _wakeCannot.Add(session);
        }

        return true;
    }

    private void SendMail(string home, JsonObject request)
    {
        if (Mail is not null)
        {
            Mail.Send(home, request);
            return;
        }

        var full = new JsonObject();
        foreach (var key in request.Keys)
        {
            full[key] = request[key];
        }

        full["from"] = "ahd";
        full["session_id"] = JsonValue.Null;
        full["cwd"] = home;
        full["hierarchy_dir"] = home;
        var outcome = Mailbox.Send(full, Clock.UtcNow);
        if (!outcome.Ok)
        {
            throw new InvalidOperationException(outcome.Failure?.ToString() ?? "the mail was refused");
        }
    }

    // ---------------------------------------------------------------- inbox view

    /// <summary>
    /// <paramref name="entries"/> of a box with each entry not from <c>ahd</c> annotated by <c>ladder: {rung, state, attempt?, next_at?, carrier?}</c>, its
    /// derived state, a paused recipient shown as <c>paused:blocked</c> or <c>paused:failed</c>; <see langword="null"/> when the team file cannot be read.
    /// </summary>
    internal IReadOnlyList<JsonValue>? ViewOf(ResolvedAddress resolved, IReadOnlyList<JsonValue> entries, double nowMs)
    {
        var record = Roster.ReadTeamFileAt(resolved.TeamPath);
        if (record.Kind != JsonKind.Map)
        {
            return null;
        }

        var member = resolved.Member is { Kind: JsonKind.Map } m ? m.AsObject() : null;
        var box = new Box(resolved.Address, resolved.Dir, record.AsObject(), resolved.Team, resolved.TeamPath, member);
        var byEntry = Mailbox.LadderRowsByEntry(box.Dir).ToDictionary(static r => r.Id, static r => r.Rows, StringComparer.Ordinal);
        var annotated = new List<JsonValue>(entries.Count);
        foreach (var value in entries)
        {
            if (value.Kind != JsonKind.Map || !IsLaddered(value.AsObject(), box.Address, box.Member) || Mailbox.IsMemberDigestNote(value, box.Address))
            {
                annotated.Add(value);
                continue;
            }

            var entry = value.AsObject();
            var rows = byEntry.TryGetValue(Str(entry, "id") ?? string.Empty, out var existing) ? [.. existing] : new List<JsonObject>();
            var derived = Derived(entry, rows);
            var rung = Rung(derived);
            var state = Str(derived, "state");
            var activity = ActivityOf(box, entry);
            var paused = activity == "failed" ? "failed" : activity == "blocked" && rung == 4 && state == "pending" ? "blocked" : null;
            var ladder = new JsonObject
            {
                ["rung"] = rung,
                ["state"] = paused is null ? Nullable(state) : JsonValue.FromString($"paused:{paused}"),
            };
            if (state is "superseded" or "declined" or "withdrawn")
            {
                foreach (var key in state is "declined" or "withdrawn" ? new[] { "by", "reason", "note", "at" } : ["by", "reason", "note"])
                {
                    if (Str(derived, key) is { Length: > 0 } text)
                    {
                        ladder[key] = text;
                    }
                }
            }

            if (rung == 4 && derived.TryGetValue("attempt", out var attempt) && attempt.Kind == JsonKind.Number && attempt.AsNumber() != 0)
            {
                ladder["attempt"] = attempt;
            }

            if (state == "pending" && Str(derived, "next_at") is { Length: > 0 } nextAt && Parse(nextAt) > nowMs)
            {
                ladder["next_at"] = nextAt;
            }

            if (rung == 3
                && rows.FirstOrDefault(static r => Rung(r) == 3 && Str(r, "state") == "pending") is { } pending
                && Str(pending, "carrier") is { Length: > 0 } carrier)
            {
                ladder["carrier"] = carrier;
            }

            var copy = Copy(entry);
            copy["ladder"] = ladder;
            if (Str(entry, "id") is { Length: > 0 } claimId && Mailbox.ReadClaim(box.Dir, claimId) is { } claim)
            {
                var claimState = Str(claim, "state") ?? string.Empty;
                var since = Parse(Str(claim, "at"));
                copy["claim"] = new JsonObject
                {
                    ["state"] = claimState,
                    ["carrier"] = Nullable(Str(claim, "carrier")),
                    ["held"] = claimState is "relay-sent" or "relay-unconfirmed" && nowMs - since >= HeldRelayMs,
                };
            }

            annotated.Add(copy);
        }

        return annotated;
    }

    // ---------------------------------------------------------------- helpers

    private static JsonObject Derived(JsonObject entry, List<JsonObject> rows) =>
        rows.Count > 0 ? rows[^1] : new JsonObject { ["rung"] = Str(entry, "route") == "pull" ? 2 : 3, ["state"] = "pending" };

    private static int Rung(JsonObject row) =>
        row.TryGetValue("rung", out var v) && v.Kind == JsonKind.Number ? (int)v.AsNumber() : 0;

    private static string? Str(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind == JsonKind.Text ? v.AsString() : null;

    private static string Show(JsonObject o, string key) =>
        o.TryGetValue(key, out var v) ? (v.Kind == JsonKind.Text ? v.AsString() : JsonText.Stringify(v, 0)) : "undefined";

    private static JsonValue Nullable(string? value) => value is null ? JsonValue.Null : JsonValue.FromString(value);

    private static double Parse(string? iso) => iso is not null && Timestamps.TryParseMs(iso, out var ms) ? ms : double.NaN;

    private static string Iso(double ms) => Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));

    private static JsonObject Copy(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var key in source.Keys)
        {
            copy[key] = source[key];
        }

        return copy;
    }
}
