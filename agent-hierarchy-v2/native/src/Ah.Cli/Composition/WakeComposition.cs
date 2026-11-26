using System.Text;
using Ah.Abstractions;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Platform;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>The waiter's reads over the mailbox files, the run file and the process table. It asks the daemon nothing.</summary>
internal sealed class WakeComposition(ServiceGraph g, IAhEnvironment environment) : IRosterWake
{
    private readonly IProcessTable _table = PlatformModule.CreateProcessTable(g.Runner);
    private SlotFiles? _slots;

    public string AhHome => environment.AhHome;

    /// <summary>A work tree's latest commit time from the read handed to it, for <see cref="LaneActivity.LatestMs"/>; null runs the read.</summary>
    internal Func<string, Func<double?>, double?>? CommitOf { get; init; }

    public int Pid => System.Environment.ProcessId;

    public IReadOnlySet<int> Kin() =>
        new HashSet<int>([Pid, .. _table.Ancestors(Pid) ?? [], .. _table.Descendants(Pid)?.Select(static n => n.Pid) ?? []]);

    internal ServiceGraph Graph => g;

    public (string Address, string Dir)? OrchestratorBox(string sessionId)
    {
        var registration = g.Mailbox.ReadRegistration(sessionId);
        foreach (var box in g.Mailbox.RegistrationBoxes(registration is null ? JsonValue.Null : JsonValue.FromObject(registration)))
        {
            if (box.Address.StartsWith("orchestrator@", StringComparison.Ordinal))
            {
                return (box.Address, box.Dir);
            }
        }

        return null;
    }

    public (string Address, string Dir)? MemberBox(string sessionId)
    {
        var registration = g.Mailbox.ReadRegistration(sessionId);
        foreach (var box in g.Mailbox.RegistrationBoxes(registration is null ? JsonValue.Null : JsonValue.FromObject(registration)))
        {
            if (!box.Address.StartsWith("orchestrator@", StringComparison.Ordinal))
            {
                return (box.Address, box.Dir);
            }
        }

        return null;
    }

    public IReadOnlyList<JobFact> Jobs(string hierarchyDir, string team) => WatchModule.JobsFromFiles(g.Hierarchy.Status, hierarchyDir, team);

    public IReadOnlyList<WakeEvent> Unacked(string boxDir, string sessionId)
    {
        var found = new List<WakeEvent>();
        foreach (var entry in g.Mailbox.ReadEntries(boxDir).Entries)
        {
            if (entry.Member("kind") is not { IsText: true } kind || kind.AsString() != "event"
                || entry.Member("from") is not { IsText: true } from || from.AsString() != "ahd"
                || entry.Member("id") is not { IsText: true } id
                || entry.Member("text") is not { IsText: true } text || text.AsString().Length == 0
                || (entry.Member("to_session") is { IsText: true } to && to.AsString().Length > 0 && to.AsString() != sessionId)
                || g.Mailbox.IsAcked(boxDir, id.AsString()))
            {
                continue;
            }

            found.Add(new WakeEvent(id.AsString(), text.AsString().Split('\n')[0]));
        }

        return found;
    }

    public IReadOnlyList<AhdEventRow> Events(string boxDir, string sessionId)
    {
        var rows = new List<AhdEventRow>();
        foreach (var entry in g.Mailbox.ReadEntries(boxDir).Entries)
        {
            if (entry.Member("kind") is not { IsText: true } kind || kind.AsString() != "event"
                || entry.Member("from") is not { IsText: true } from || from.AsString() != "ahd"
                || entry.Member("id") is not { IsText: true } id
                || (entry.Member("to_session") is { IsText: true } to && to.AsString().Length > 0 && to.AsString() != sessionId))
            {
                continue;
            }

            var data = entry.Member("data");
            var ackPath = Path.Combine(boxDir, "acks", id.AsString());
            rows.Add(new AhdEventRow(
                id.AsString(),
                data?.Member("request_id") is { IsText: true } request ? request.AsString() : null,
                data?.Member("step") is { IsText: true } step ? step.AsString() : null,
                g.Mailbox.IsAcked(boxDir, id.AsString())
                    ? g.Io.Files.Stat(ackPath)?.MtimeMs ?? (g.Mailbox.ReadAck(boxDir, id.AsString())?.Member("at") is { IsText: true } ackedAt && Timestamps.TryParseMs(ackedAt.AsString(), out var ackedMs) ? ackedMs : 0)
                    : null,
                entry.Member("created") is { IsText: true } created ? created.AsString() : null,
                data?.Member("item") is { IsText: true } item ? item.AsString() : null,
                entry.Member("event") is { IsText: true } name ? name.AsString() : null));
        }

        return rows;
    }

    /// <summary>For a request to a sub-orchestrator of <paramref name="team"/>: when its lane's workstream last reported, and its state.</summary>
    internal (double? UpdatedMs, string? State) StreamOf(string hdir, string? team, string? member, Func<string, string, JsonValue> teamRecord, Func<string, string, string, string?>? findChildTeam = null)
    {
        findChildTeam ??= g.Roster.FindChildTeam;
        if (team is null || member is null || teamRecord(hdir, team) is not { IsMap: true } manager
            || manager.Member("streams") is not { IsMap: true } streams)
        {
            return (null, null);
        }

        var all = streams.AsObject();
        var ws = all.Keys.FirstOrDefault(k => all[k].Member("sub_orchestrator") is { IsText: true } so && so.AsString() == member);
        if (ws is null || findChildTeam(hdir, team, ws) is not { } childName)
        {
            return (null, null);
        }

        var childRecord = teamRecord(hdir, childName);
        var workstream = childRecord.Member("workstream");
        var state = workstream?.Member("state") is { IsText: true } st ? st.AsString() : null;
        double? reported = workstream?.Member("updated") is { IsText: true } updated && Ah.Core.Time.Timestamps.TryParseMs(updated.AsString(), out var ms) ? ms : null;
        var active = LaneActivity.LatestMs(g, hdir, childName, childRecord, g.Io.Clock.UtcNow.ToUnixTimeMilliseconds(), CommitOf);
        return (active is { } a && (reported is null || a > reported) ? a : reported, state);
    }

    /// <summary>How far ahead the failsafe's clock is held for the charter of a paused lane: until the lane reports again, in effect.</summary>
    private const double PausedHoldMs = 3650d * 24 * 60 * 60 * 1000;

    /// <summary>
    /// What every lane of one pass over a pool asks of it, read once and on first use: the open exchanges, the lane charters among them,
    /// each request's parse, the gates, the peer records and the roster. A scan holds the pool as it was when first read.
    /// </summary>
    internal sealed class PoolScan(WakeComposition wake, string hierarchyDir)
    {
        private JsonArray? _exchanges;
        private JsonArray? _open;
        private IReadOnlySet<string>? _charterIds;
        private readonly Lazy<List<JsonObject>> _gates = new(() => ObjectsOf(wake.Graph.Hierarchy.Roster.ReadGates(hierarchyDir)));
        private readonly Lazy<List<JsonObject>> _peers = new(() => ObjectsOf([.. wake.Graph.Hierarchy.Core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "heard" }))]));
        private readonly Lazy<JsonArray> _roster = new(() => wake.Graph.Hierarchy.Roster.ReadRoster(hierarchyDir));
        private readonly Dictionary<string, JsonValue> _messages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonValue> _teams = new(StringComparer.Ordinal);

        public List<JsonObject> Gates => _gates.Value;

        public List<JsonObject> Peers => _peers.Value;

        public JsonArray Roster => _roster.Value;

        private JsonArray Exchanges => _exchanges ??= wake.Graph.Hierarchy.Core.ListExchanges(hierarchyDir);

        public IReadOnlySet<string> CharterIds => _charterIds ??= wake.Graph.Hierarchy.Core.OpenLaneCharterIds(hierarchyDir);

        public JsonValue Message(string path)
        {
            if (!_messages.TryGetValue(path, out var read))
            {
                read = wake.Graph.Hierarchy.Core.ReadMsgFile(path);
                _messages[path] = read;
            }

            return read;
        }

        public JsonValue TeamRecord(string name)
        {
            if (!_teams.TryGetValue(name, out var record))
            {
                record = wake.Graph.Roster.ReadTeamRecord(hierarchyDir, name);
                _teams[name] = record;
            }

            return record;
        }

        public void SeedTeamRecord(string name, JsonValue record) => _teams[name] = record;

        /// <summary>The open exchanges whose request is tagged to <paramref name="team"/>.</summary>
        public IEnumerable<JsonValue> OpenOf(string team) =>
            (_open ??= wake.Graph.Hierarchy.Messages.OpenExchanges(hierarchyDir, null, Exchanges)).Where(e => e.Member("request")?.Member("path") is { IsText: true } path
                && Message(path.AsString()).Member("fm")?.Member("team") is { IsText: true } tag && tag.AsString() == team);
    }

    private const string SubOrchestratorRole = "sub-orchestrator";

    /// <summary>
    /// For each request a brief entry points at, the name of the member the brief was sent to (the address before its <c>@</c>): the
    /// addressee of a request whose <c>to_name</c> was never written. Read lazily, once per scan, and only when a request lacks the name.
    /// </summary>
    /// <summary>
    /// ah2-296 L1: when a part was last filed on each open request, from its response in the message store (the seal time of a response that
    /// holds only parts), or null; one store read for all of them.
    /// </summary>
    internal static IReadOnlyList<double?> PartFiledMs(Ah.Hierarchy.IHierarchyCore core, IReadOnlyList<string> responsePaths) =>
        responsePaths.Count == 0 ? [] : [.. core.ReadMsgTextsAndTimes(responsePaths).Select(static r => r.Text is { } text && MsgRows.IsPartial(text) ? r.TimeMs : null)];

    /// <summary>
    /// Each briefed request's addressee (the latest brief's), whether a brief sent it as the watch reads a send (<see cref="WatchHostAdapter.IsSentBrief"/>, from
    /// <paramref name="team"/>'s Orchestrator), and when the earliest such brief was sent, by request path key.
    /// </summary>
    private Dictionary<string, (string Name, bool Sent, double? SentMs)> BriefAddressees(string team)
    {
        var byRef = new Dictionary<string, (string Name, bool Sent, double? SentMs)>(StringComparer.Ordinal);
        var nowMs = g.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
        var orchestrator = Ah.Hierarchy.HierarchyModule.OrchestratorAddress(team);
        foreach (var box in g.Mailbox.ListBoxes())
        {
            foreach (var (key, name, sentMs, from, reference) in g.BriefAddressees.Of(box.Dir, box.Address, g.Mailbox.ReadEntries(box.Dir).Entries))
            {
                var held = byRef.TryGetValue(key, out var h) ? h : (Name: name, Sent: false, SentMs: (double?)null);
                if (WatchHostAdapter.IsSentBrief(reference, from, f => f == orchestrator, nowMs))
                {
                    held = (name, true, held.SentMs is { } before && (sentMs is not { } at || before < at) ? before : sentMs ?? held.SentMs);
                }

                byRef[key] = (name, held.Sent, held.SentMs);
            }
        }

        return byRef;
    }

    public IReadOnlyList<OverdueDispatch> OpenDispatches(string hierarchyDir, string team, string sessionId) =>
        OpenDispatches(hierarchyDir, team, sessionId, new PoolScan(this, hierarchyDir), null);

    public IReadOnlyList<OverdueDispatch> OpenDispatches(string hierarchyDir, string team, string sessionId, PoolScan scan, JsonValue? teamRecord)
    {
        if (teamRecord is { } known)
        {
            scan.SeedTeamRecord(team, known);
        }

        var found = new List<OverdueDispatch>();
        Dictionary<string, (string Name, bool Sent, double? SentMs)>? briefed = null;
        var partOf = new List<(int Index, string Path)>();
        var lanes = new Dictionary<string, (double? UpdatedMs, string? State)>(StringComparer.Ordinal);
        var gates = scan.Gates;
        var peers = scan.Peers;
        var record = scan.TeamRecord(team);
        var described = record.IsMap ? ObjectsOf(g.Hierarchy.Status.DescribeMembers(hierarchyDir, record, scan.Roster, team)) : [];
        foreach (var exchange in scan.OpenOf(team))
        {
            if (exchange.Member("request")?.Member("path") is not { IsText: true } path
                || scan.Message(path.AsString()).Member("fm") is not { IsMap: true } frontMatter)
            {
                continue;
            }

            var fm = frontMatter.AsObject();
            if (Text(fm, "from") is not (Ah.Hierarchy.HierarchyModule.OrchestratorRole or SubOrchestratorRole) || Text(fm, "type") != "request"
                || g.Hierarchy.Core.ParseMsgFilename(path.AsString())?.Id is not { } id)
            {
                continue;
            }

            if ((Text(fm, "to_name") is { Length: > 0 } named && named != "null" ? named : (briefed ??= BriefAddressees(team)).GetValueOrDefault(Ah.Core.Json.MsgRows.PathKey(path.AsString())).Name) is not { Length: > 0 } toName)
            {
                continue;
            }

            // A brief nobody sent raises no check-in (DispatchClock.HeldBack, ah2-296 L2), so there is no alarm here to miss.
            var addressee = described.FirstOrDefault(m => Text(m, "name") == toName);
            var entrySent = (briefed ??= BriefAddressees(team)).TryGetValue(Ah.Core.Json.MsgRows.PathKey(path.AsString()), out var brief) && brief.Sent ? brief.SentMs ?? 0 : (double?)null;
            var rowSent = peers.Where(r => Text(r, "type") == "dispatch" && Text(r, "request_id") == id && Text(r, "session_id") == sessionId)
                .Select(r => Text(r, "created") is { } c && Ah.Core.Time.Timestamps.TryParseMs(c, out var at) ? at : 0).DefaultIfEmpty(double.NaN).Min();
            if (((addressee is null || Text(addressee, "session_id") is not { Length: > 0 }) && entrySent is null) || (entrySent is null && double.IsNaN(rowSent)))
            {
                continue;
            }

            var eta = g.Hierarchy.Messages.EtaOf(fm.Get("eta") ?? JsonValue.Null);
            var created = Text(fm, "created");
            var createdMs = created is not null && Ah.Core.Time.Timestamps.TryParseMs(created, out var ms) ? ms : 0;
            var threshold = g.Hierarchy.Messages.ThresholdFor(eta);
            double? held = null;
            if (addressee is { } member && Text(member, "activity") == "working")
            {
                var since = Text(member, "activity_at") is { } at && Ah.Core.Time.Timestamps.TryParseMs(at, out var atMs) ? atMs : createdMs;
                held = since + (3 * threshold * 1000);
            }

            // the dispatch clock's floors: the send (b475, ah2-296 L2) here, the latest part (ah2-296 L1) after the loop in one store read
            var responsePath = exchange.Member("response")?.Member("path") is { IsText: true } response ? response.AsString() : null;
            var floorMs = Math.Max(createdMs, double.IsNaN(rowSent) ? entrySent ?? 0 : rowSent);
            if (!lanes.TryGetValue(toName, out var lane))
            {
                lane = StreamOf(hierarchyDir, team, toName, (_, name) => scan.TeamRecord(name));
                lanes[toName] = lane;
            }

            var (streamUpdatedMs, streamState) = lane;
            if (streamState is not null && scan.CharterIds.Contains(id))
            {
                if (streamState == "paused")
                {
                    held = g.Io.Clock.UtcNow.ToUnixTimeMilliseconds() + PausedHoldMs;
                }
                else if (streamUpdatedMs is { } updatedMs)
                {
                    floorMs = Math.Max(floorMs, updatedMs);
                }
            }

            // b456: ahd raises nothing for a paused member (DispatchClock), so there is no alarm to miss; resume restarts the clock
            if (g.Roster.PauseOf(hierarchyDir, team, toName) is { } pause)
            {
                if (pause.Paused)
                {
                    continue;
                }

                floorMs = Math.Max(floorMs, pause.AtMs);
            }

            found.Add(new OverdueDispatch(id, Text(fm, "slug") ?? string.Empty, toName, eta, threshold, created, $"{toName}@{team}", floorMs, sessionId, peers, gates, held)
            {
                PlainSend = Text(fm, "reason") == Ah.Hierarchy.HierarchyModule.PlainSendReason,
            });
            if (responsePath is not null)
            {
                partOf.Add((found.Count - 1, responsePath));
            }
        }

        var parts = PartFiledMs(g.Hierarchy.Core, [.. partOf.Select(static p => p.Path)]);
        for (var i = 0; i < partOf.Count; i++)
        {
            if (parts[i] is { } partMs && partMs > found[partOf[i].Index].FloorMs)
            {
                found[partOf[i].Index] = found[partOf[i].Index] with { FloorMs = partMs };
            }
        }

        return found;
    }

    public IReadOnlyList<OverdueQueueItem> QueueStalls(string pool, string hierarchyDir, string team, double nowMs)
    {
        var entries = Ah.Hierarchy.HierarchyModule.ChecklistEntries(g.Hierarchy.Progress, pool, Ah.Hierarchy.HierarchyModule.ProgressTeamOf(team));
        if (!Ah.Hierarchy.HierarchyModule.ChecklistAnyQueuedLongEnough(entries, nowMs) || g.Roster.ReadTeamRecord(hierarchyDir, team) is not { IsMap: true } record)
        {
            return [];
        }

        _slots ??= new SlotFiles(g, null);
        var slotRecords = _slots.Read();
        var scan = WatchModule.QueueStallsFromFiles(g.Hierarchy, record, entries, hierarchyDir, team, nowMs, session => WatchModule.SlotOf(slotRecords, session) is not null);
        return [.. scan.Stalled.Select(q =>
        {
            var member = q.Eligible[0];
            var idleSince = scan.Members.First(m => m.Name == member).IdleSinceMs ?? q.QueuedSinceMs;
            var item = Ah.Hierarchy.HierarchyModule.ChecklistKey(q.Entry);
            return new OverdueQueueItem($"{item}|{member}|{q.Entry.Item.StateSince}", item, member, Math.Max(q.QueuedSinceMs, idleSince) + Ah.Hierarchy.HierarchyModule.QueueStallMs);
        })];
    }

    public IReadOnlyList<(string Role, string Item)> QueueUnstaffed(string pool, string hierarchyDir, string team, double nowMs)
    {
        var entries = Ah.Hierarchy.HierarchyModule.ChecklistEntries(g.Hierarchy.Progress, pool, Ah.Hierarchy.HierarchyModule.ProgressTeamOf(team));
        if (!Ah.Hierarchy.HierarchyModule.ChecklistAnyQueuedLongEnough(entries, nowMs) || g.Roster.ReadTeamRecord(hierarchyDir, team) is not { IsMap: true } record)
        {
            return [];
        }

        _slots ??= new SlotFiles(g, null);
        var slotRecords = _slots.Read();
        var unstaffed = WatchModule.QueueUnstaffedFromFiles(
            g.Hierarchy, record, entries, hierarchyDir, team, nowMs, session => WatchModule.SlotOf(slotRecords, session) is not null, SessionBudgets.Of(g, hierarchyDir, team),
            own => SessionBudgets.TreeLive(g, hierarchyDir, team, new Dictionary<string, int>(StringComparer.Ordinal) { [team] = own })?.Live ?? own);
        return [.. unstaffed.Select(static q => (q.Role, Ah.Hierarchy.HierarchyModule.ChecklistKey(q.Entry)))];
    }

    public void Journal(JsonObject row) => StallStore.Journal(g, row);

    public int? LaunchHeal(string trigger) => g.Daemon.LaunchHeal(trigger);

    public IReadOnlyList<string> JournalRows() => StallStore.JournalRows(AhHome);

    public string? ReadState(string key) => StallStore.Read(AhHome, key);

    public void WriteState(string key, string? value) => StallStore.Put(g, key, value);

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;

    private static List<JsonObject> ObjectsOf(JsonArray array)
    {
        var list = new List<JsonObject>();
        foreach (var item in array)
        {
            if (item.IsMap)
            {
                list.Add(item.AsObject());
            }
        }

        return list;
    }

    public (string Address, string Dir, string Session)? OrchestratorBoxOfTeam(string hierarchyDir, string team) => OrchestratorBoxOfTeam(hierarchyDir, team, null);

    public (string Address, string Dir, string Session)? OrchestratorBoxOfTeam(string hierarchyDir, string team, JsonValue? teamRecord)
    {
        return Ah.Hierarchy.HierarchyModule.OrchestratingSessionOf(teamRecord ?? g.Roster.ReadTeamRecord(hierarchyDir, team), team) is { } o && OrchestratorBox(o.SessionId) is { } box
            ? (box.Address, box.Dir, o.SessionId)
            : null;
    }

    public IReadOnlyList<OverdueJobItem> JobStalls(string hierarchyDir, string team, double nowMs) =>
        WatchModule.OverdueJobItems([.. Jobs(hierarchyDir, team).Where(JobOpen).Select(j => j with { Alive = WatchModule.JobPidLive(j, _table, g.Io.Processes.PidAlive) })], eta => g.Hierarchy.Messages.ThresholdFor(JsonValue.FromString(eta)), nowMs, environment);

    public IReadOnlyList<OverdueLaneItem> LaneStalls(string hierarchyDir, string team, string sessionId)
    {
        var charters = OpenDispatches(hierarchyDir, team, sessionId);
        return [.. LaneStreams.Of(g, hierarchyDir, team)
            .Select(l => WatchModule.OverdueLaneItemOf(team, l.Stream, l.SubOrchestrator, l.StreamState, l.UpdatedMs, charters.FirstOrDefault(d => d.ToName == l.SubOrchestrator)?.RequestId))
            .OfType<OverdueLaneItem>()];
    }

    public bool JobOpen(JobFact job)
    {
        return WatchModule.JobOpen(job, j => WatchModule.JobRunning(j, _table, g.Io.Processes.PidAlive), g.Io.Clock.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool PidLive(int pid, string? recordedStart) =>
        g.Io.Processes.PidAlive(pid) && !(g.Hierarchy.Core.Liveness?.PidReused(pid, recordedStart) ?? false);

    public string MemberVerdict(string hierarchyDir, string team, string member)
    {
        string? pane = null;
        JsonValue? memberRow = null;
        var teamRecord = g.Roster.ReadTeamRecord(hierarchyDir, team);
        if (teamRecord is { IsMap: true } record && record.AsObject().Get("members") is { IsArray: true } rows)
        {
            foreach (var row in rows.AsArray())
            {
                if (row.IsMap && row.AsObject().Get("name") is { IsText: true } name && name.AsString() == member)
                {
                    memberRow = row;
                    if (row.AsObject().Get("transport_id") is { IsText: true } transport)
                    {
                        pane = global::Ah.Abstractions.HeadlessTransport.PaneOf(transport.AsString()) ?? pane;
                    }
                }
            }
        }

        var verdict = Ah.Hierarchy.HierarchyModule.JudgeMember(
            g.Hierarchy.Core.Liveness?.ForMember(member, team),
            pane,
            () =>
            {
                // ah2-145: the row's stamp beats an untagged team's tui; ahd's own env is no session's, so it is never the fallback
                var state = g.Launch.HerdrFor(Ah.Core.Mux.HerdrFlavour.Resolve(null, teamRecord, null, memberRow) ?? Ah.Core.Mux.HerdrFlavour.Tui).AgentState(member);
                return new Ah.Hierarchy.PaneProbe(state.Indeterminate, state.AgentStatus, state.AgentType, state.Why);
            });
        if (verdict.Status == Ah.Hierarchy.Verdict.Live
            && WatchModule.JobOverdueOf(WatchModule.JobsFromFiles(g.Hierarchy.Status, hierarchyDir, team), member, eta => g.Hierarchy.Messages.ThresholdFor(JsonValue.FromString(eta)), g.Io.Clock.UtcNow.ToUnixTimeMilliseconds(), 30000) is { } blocked)
        {
            return $"blocked-on-job — job {blocked.Id} ({blocked.Kind} {blocked.Label}) is past its ETA {blocked.Eta}; {verdict.Why}";
        }

        if (verdict.Status == Ah.Hierarchy.Verdict.Live
            && WatchModule.JobsFromFiles(g.Hierarchy.Status, hierarchyDir, team).FirstOrDefault(j => !j.Ended && j.Owner == member && j.Slot is { Phase: "queued" }) is { Slot: { } queued } waiting)
        {
            var age = $"{(int)(Math.Max(0, g.Io.Clock.UtcNow.ToUnixTimeMilliseconds() - queued.SinceMs) / 60000)}m";
            return $"waiting-on-slot — job {waiting.Id} ({waiting.Kind} {waiting.Label}) queued for {WatchModule.QueuedAsk(queued)} ({WatchModule.QueuedState(queued)}, {age}); {verdict.Why}";
        }

        return $"{verdict.Status.ToString().ToLowerInvariant()} — {verdict.Why}";
    }

    public bool DaemonUp()
    {
        try
        {
            return ReadText(Path.Combine(AhHome, "run", "ahd.json")) is { } text
                && JsonText.Parse(Encoding.UTF8.GetBytes(text)) is { IsMap: true } info
                && info.AsObject().Get("pid") is { IsNumber: true } pid
                && g.Io.Processes.PidAlive((int)pid.AsNumber());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return false;
        }
    }

    public string? StartOf(int pid) => _table.StartTime(pid);

    public string? ReadText(string path) => g.Io.Files.Stat(path) is null ? null : g.Io.Files.ReadText(path);

    public void WriteText(string path, string text)
    {
        g.Io.Writer.Mkdir(Path.GetDirectoryName(path)!);
        g.Io.Writer.WriteAtomic(path, path + ".tmp." + System.Environment.ProcessId, Encoding.UTF8.GetBytes(text));
    }

    public void Remove(string path) => _ = g.Io.Writer.Unlink(path);
}
