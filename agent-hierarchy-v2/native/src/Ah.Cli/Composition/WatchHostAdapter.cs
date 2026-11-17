using Ah.Abstractions;
using Ah.Codex;
using System.Text;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Roster;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>herdr, as the watch reads it: the agent list and one agent's visible screen. Supplied by the launch layer.</summary>
internal interface IHerdrSeam
{
    /// <summary><c>herdr agent list</c> as rows of <c>{name, pane_id, agent_status}</c>, or null when herdr cannot answer.</summary>
    IReadOnlyList<JsonObject>? AgentList();

    /// <summary>One <c>visible</c> read of an agent's screen on the herdr server of <paramref name="flavour"/> (null: the env's); "" when it cannot be read.</summary>
    string ReadVisibleScreen(string agentName, string? flavour = null);

    /// <summary><c>hc label</c> on a herdr-core terminal; false when it failed (hc absent included).</summary>
    bool Label(string address, IReadOnlyList<string> pairs) => true;
}

/// <summary>One delivery-ladder pass. Supplied by the mesh and launch layers.</summary>
internal interface ILadderSeam
{
    /// <summary>Runs one pass and returns its counts <c>{pending, sending, paused, escalated}</c>.</summary>
    Task<JsonObject> RunAsync(double nowMs, double pollMs, CancellationToken cancellation);
}

/// <summary>The reads and the pass the watch host cannot make from the landed services yet, each a named seam.</summary>
/// <param name="Herdr">The herdr reads.</param>
/// <param name="Ladder">The delivery-ladder pass.</param>
/// <param name="PromptHeading">The heading of a recognised prompt of a harness kind, or null.</param>
internal sealed record WatchHostSeams(IHerdrSeam Herdr, ILadderSeam Ladder, Func<string, string?, string?> PromptHeading)
{
    private sealed class NoHerdr : IHerdrSeam
    {
        public IReadOnlyList<JsonObject>? AgentList() => null;

        public string ReadVisibleScreen(string agentName, string? flavour = null) => string.Empty;
    }

    private sealed class NoLadder : ILadderSeam
    {
        public Task<JsonObject> RunAsync(double nowMs, double pollMs, CancellationToken cancellation) =>
            Task.FromResult(new JsonObject { ["pending"] = 0, ["sending"] = 0, ["paused"] = 0, ["escalated"] = 0 });
    }

    private sealed class LaunchHerdr(IScreenReading reading, Func<string, IScreenReading> readingFor, HerdrCore core) : IHerdrSeam
    {
        public bool Label(string address, IReadOnlyList<string> pairs) => core.Label(address, pairs, 1000).Ok;

        public IReadOnlyList<JsonObject>? AgentList() =>
            reading.HerdrAgentList() is { } agents ? [.. agents.Where(static a => a.Kind == JsonKind.Map).Select(static a => a.AsObject())] : null;

        public string ReadVisibleScreen(string agentName, string? flavour = null) =>
            (flavour is null ? reading : readingFor(flavour)).ReadVisibleScreen(agentName);
    }

    /// <summary>herdr answers nothing, no prompt has a heading and the ladder never runs: the host without the launch layer.</summary>
    internal static WatchHostSeams Unavailable { get; } = new(new NoHerdr(), new NoLadder(), static (_, _) => null);

    /// <summary>
    /// herdr and the prompt headings from the launch layer's own readers (the daemon's mux guard applies). The ladder pass is
    /// <paramref name="ladder"/>; with none the ladder never runs.
    /// </summary>
    internal static WatchHostSeams FromLaunch(
        IProcessRunner runner,
        IAhEnvironment environment,
        IFileReader files,
        IRecordAppender appender,
        IConfigCore config,
        IScreenRules screens,
        int ownPid,
        Action<string>? report = null,
        ILadderSeam? ladder = null)
    {
        var mux = new MuxRunner(runner, new MuxGuard(environment, files, appender, ownPid, report));
        var harnesses = new KindHarnesses(RosterModule.CreateMemberRegistry(config, files, environment).KindDefault);
        var herdr = new Herdr(mux, environment, files);
        var reading = new ScreenReading(herdr, environment, config, screens, harnesses);
        return new WatchHostSeams(
            new LaunchHerdr(reading, flavour => reading with { Herdr = herdr.ForFlavour(flavour) }, new HerdrCore(mux, environment)),
            ladder ?? new NoLadder(),
            (kind, recognized) => recognized is null ? null : harnesses.PromptHeading(kind, recognized));
    }
}

/// <summary>The ladder's panes and paste, over the launch layer's paste service.</summary>
internal sealed class LaunchDeliveryPaste(IPaste paste) : IDeliveryPaste
{
    public JsonObject PlacementOf(JsonValue member, string? teamTransport) => paste.PlacementOf(member, teamTransport);

    public PasteOutcome Paste(JsonValue member, string teamName, string? teamTransport, string payload, string entryId, string boxDir, int headerChars = 0)
    {
        var result = paste.Paste(new PasteOptions(member, teamName, teamTransport, payload, entryId.Length == 0 ? null : new PasteEntry(entryId, boxDir), Source: "ladder", MaxChars: PasteOptions.DefaultMaxChars + headerChars));
        return new PasteOutcome(
            result.TryGetValue("outcome", out var outcome) && outcome.Kind == JsonKind.Text ? outcome.AsString() : "refused",
            result.TryGetValue("reason", out var reason) && reason.Kind == JsonKind.Text ? reason.AsString() : null);
    }
}

/// <summary>The watch host's ladder pass, over the mesh's delivery ladder.</summary>
internal sealed class MeshLadderSeam(IDeliveryLadder ladder) : ILadderSeam
{
    public async Task<JsonObject> RunAsync(double nowMs, double pollMs, CancellationToken cancellation)
    {
        var counts = await ladder.RunAsync(nowMs, pollMs, cancellation).ConfigureAwait(false);
        return new JsonObject
        {
            ["pending"] = counts.Pending,
            ["sending"] = counts.Sending,
            ["paused"] = counts.Paused,
            ["escalated"] = counts.Escalated,
        };
    }
}

/// <summary>The watch tick's reads and writes (<see cref="IWatchHost"/>), over the services that own each store.</summary>
internal class WatchHostAdapter(
    ServiceGraph graph, IScreenRules screens, IClock clock, WatchHostSeams seams, Action<string>? log = null, Func<IReadOnlyList<string>, IReadOnlyList<string>>? admitDirs = null) : IWatchHost
{
    private IMailbox Mail_ => graph.Mailbox;

    internal virtual HierarchyServices Hs => graph.Hierarchy;

    private IMailboxHooks? _mailHooks;

    private IMailboxHooks MailHooks =>
        _mailHooks ??= MeshModule.CreateMailboxHooks(graph.Mailbox, MeshModule.CreateRelayObligations(graph.Mailbox, graph.Hierarchy), graph.Hierarchy, clock);

    // ---------------------------------------------------------------- chain of command

    private IDirectiveText? _directive;

    public string WindDownText(string link, string why, string orchestratorAddress)
    {
        _directive ??= ConfigModule.CreateDirectiveText(
            graph.Config,
            ConfigModule.CreateRoleContracts(graph.Config, graph.Io.Files, graph.Io.Environment),
            () => graph.Roster);
        return _directive.WindDown(link, why, orchestratorAddress, "branch");
    }

    public ChainState ChainOf(WatchRegistration member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var box = member.Boxes.FirstOrDefault(static b => b.Address.Contains('@', StringComparison.Ordinal) && !b.Address.StartsWith("orchestrator@", StringComparison.Ordinal));
        if (box is null || member.HierarchyDir.Length == 0)
        {
            return new ChainState(ChainKind.Unknown, null, "no member box");
        }

        var table = ProcessTree;
        var registrations = new List<(string Session, long Pid, string? Start)>();
        foreach (var reg in Mail_.LiveRegistrations())
        {
            if (Str(reg, "session_id") is { } session && reg.TryGetValue("pid", out var pid) && pid.Kind == JsonKind.Number)
            {
                registrations.Add((session, (long)pid.AsNumber(), Str(reg, "pid_start")));
            }
        }

        return LaunchModule.ChainOfSession(
            graph.Roster,
            member.HierarchyDir,
            box.Address[(box.Address.IndexOf('@', StringComparison.Ordinal) + 1)..],
            member.SessionId,
            registrations,
            table,
            clock.UtcNow,
            Environment.ProcessId,
            path => graph.Io.Files.Exists(path) ? graph.Io.Files.ReadText(path) : null);
    }

    // ---------------------------------------------------------------- registrations

    public IReadOnlyList<WatchRegistration> LiveRegistrations()
    {
        var list = new List<WatchRegistration>();
        foreach (var reg in Mail_.LiveRegistrations())
        {
            if (admitDirs is { } admit && HierarchyDirOf(reg) is { } dir && admit([dir]).Count == 0)
            {
                continue;
            }

            var boxes = Mail_.RegistrationBoxes(JsonValue.FromObject(reg)).Select(static b => new WatchBox(b.Address, b.Dir)).ToList();
            list.Add(new WatchRegistration(
                Str(reg, "session_id") ?? string.Empty,
                Str(reg, "harness"),
                reg.TryGetValue("pid", out var pid) && pid.Kind == JsonKind.Number ? (long)pid.AsNumber() : null,
                Str(reg, "at"),
                boxes,
                HierarchyDirOf(reg) ?? string.Empty));
        }

        return list;
    }

    // F2: never a rebuild on the watch's 2 s job; the daemon's background rebuild, as a publish schedules it
    public void StatusDirFirstSeen(string dir) => _ = Hs.Status.ScheduleStatusEntries(dir);

    public void HealDirtyStatus() => Hs.Status.HealDirtyStatus();

    public IReadOnlyList<string> LiveHierarchyDirs()
    {
        IReadOnlyList<string> live = [.. Mail_.LiveRegistrations().SelectMany(static r => MeshModule.RegistrationTeamFiles(r).Select(static f => NodePath.Dirname(NodePath.Dirname(f))))];
        return admitDirs is { } admit ? admit(live) : live;
    }

    public bool PullFresh(string boxDir, string sessionId, double nowMs) => Mail_.PullFresh(boxDir, sessionId, nowMs);

    public double PullBeatMs(string boxDir, string sessionId, double nowMs) =>
        Mail_.ReadHeartbeat(boxDir, sessionId, nowMs)?.Get("at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var ms) ? ms : 0;

    public string? SessionCwd(string sessionId)
    {
        var reg = Mail_.ReadRegistration(sessionId);
        if (reg is null)
        {
            return null;
        }

        if (Str(reg, "cwd") is { Length: > 0 } cwd)
        {
            return cwd;
        }

        return Str(reg, "team_file") is { Length: > 0 } teamFile
            ? NodePath.Dirname(NodePath.Dirname(NodePath.Dirname(NodePath.Dirname(teamFile))))
            : null;
    }

    // ---------------------------------------------------------------- dispatch clock

    public double ThresholdSeconds(string eta) => Hs.Messages.ThresholdFor(JsonValue.FromString(eta));

    /// <summary>The home's message, peer-record, session-pid and team counters, read once a tick; null when any cannot be read. The doctor's rebuild moves it.</summary>
    public string? DispatchInputsStamp(string sessionId, double nowMs) =>
        SnapshotOf(nowMs).Once("dispatch-inputs", () =>
            Hs.Core.MsgsVersion() is { } msgs && Hs.Core.PeerRecordsVersion() is { } peers
            && Ah.StateDb.StateDbModule.DomainSeq(graph.Environment.AhHome, "session-pids") is { } pids
            && Ah.StateDb.StateDbModule.DomainSeq(graph.Environment.AhHome, "teams") is { } teams
                ? $"{Volatile.Read(ref _forgets)}:{msgs}:{peers}:{pids}:{teams}"
                : string.Empty) is { Length: > 0 } stamp ? stamp : null;

    private int _forgets;

    public IReadOnlyList<DispatchCandidate> Dispatches(string sessionId, double nowMs)
    {
        // every tick reads the front matter of each candidate; one connection per read spent most of an idle daemon's CPU parsing the schema
        using var sharedReads = Hs.Core.ShareMsgReads();
        var candidates = new List<DispatchCandidate>();
        var lap = System.Diagnostics.Stopwatch.StartNew();
        var laps = new StringBuilder();
        void Lap(string name)
        {
            _ = laps.Append(' ').Append(name).Append('=').Append(lap.ElapsedMilliseconds);
            lap.Restart();
        }

        var pools = RefreshIndex(sessionId, SnapshotOf(nowMs));
        Lap("index");
        var peerRecords = SessionPeerRecords(sessionId, SnapshotOf(nowMs));
        var shown = Hs.Peers.ReportShownFor(JsonValue.FromString(sessionId), peerRecords);
        Lap("shown");
        var rows = Objects(Hs.Peers.LatestDispatchRows(JsonValue.FromString(sessionId), peerRecords)).ToList();
        Lap($"rows({rows.Count})");
        // b475: when each request's brief was first sent, the origin the Stop hook's clock uses (its earliest dispatch row)
        var sentAt = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in peerRecords)
        {
            if (r.Member("type") is { IsText: true } rowType && rowType.AsString() == "dispatch" && r.Member("request_id") is { IsText: true } sentId
                && r.Member("created") is { IsText: true } sentText && Timestamps.TryParseMs(sentText.AsString(), out var sentMs)
                && (!sentAt.TryGetValue(sentId.AsString(), out var earliest) || sentMs < earliest))
            {
                sentAt[sentId.AsString()] = sentMs;
            }
        }

        var derived = DerivedRows(sessionId, SnapshotOf(nowMs), rows, nowMs);
        Lap($"derived({derived.Rows.Count})");
        long reachedBuildMs = 0;
        var reached = new Lazy<Func<string, string, bool>>(() =>
        {
            var building = System.Diagnostics.Stopwatch.StartNew();
            var built = MailHooks.ReportReached(sessionId, SnapshotOf(nowMs).ReachOnce(MailHooks.BeginReach), peerRecords);
            reachedBuildMs = building.ElapsedMilliseconds;
            return built;
        });
        var ownPools = SessionPools(sessionId, SnapshotOf(nowMs)).ToHashSet(StringComparer.Ordinal);
        var foreignPools = new HashSet<string>(StringComparer.Ordinal);
        var memoHitsAtStart = _exchangeMemo.Hits;
        var memoBuildsAtStart = _exchangeMemo.Builds;
        var landedRows = 0;
        var withdrawn = SnapshotOf(nowMs).WithdrawnOnce(graph.Mailbox.WithdrawnRequestFiles);
        Lap("withdrawn");
        pools = WithRowPools(sessionId, SnapshotOf(nowMs), pools, rows.Concat(derived.Rows), shown, withdrawn, nowMs);
        Lap("row-pools");
        var teamRecords = new Dictionary<(string Dir, string Team), JsonValue>();
        JsonValue TeamRecord(string hdir, string team)
        {
            if (!teamRecords.TryGetValue((hdir, team), out var record))
            {
                record = graph.Roster.ReadTeamRecord(hdir, team);
                teamRecords[(hdir, team)] = record;
            }

            return record;
        }

        var wake = new WakeComposition(graph, graph.Environment) { CommitOf = CommitPerTick };
        var childTeams = new Dictionary<(string Dir, string Team, string Stream), string?>();
        var streamMemo = new Dictionary<(string hdir, string?, string?), (double? UpdatedMs, string? State)>();
        var pauseMemo = new Dictionary<(string hdir, string?, string?), MemberPause?>();
        string? ChildTeam(string hdir, string team, string stream)
        {
            if (!childTeams.TryGetValue((hdir, team, stream), out var child))
            {
                child = graph.Roster.FindChildTeam(hdir, team, stream);
                childTeams[(hdir, team, stream)] = child;
            }

            return child;
        }

        // b341: the member leads a stream of its team, and that stream's child team holds an open request the member sent (b376: or its orchestrating role sent).
        var exchangeSource = new DaemonServices.OpenExchangeSource(graph);
        bool LaneDelegating(string hdir, string? team, string? member)
        {
            if (team is not { Length: > 0 } || member is not { Length: > 0 } || TeamRecord(hdir, team).Member("streams") is not { IsMap: true } streams)
            {
                return false;
            }

            foreach (var ws in streams.AsObject().Keys)
            {
                if (streams.AsObject().Get(ws) is { IsMap: true } record && Str(record.AsObject(), "sub_orchestrator") == member && ChildTeam(hdir, team, ws) is { } child
                    && exchangeSource.OpenExchanges(hdir, child, allTeams: false).Any(e => exchangeSource.FrontMatter(e.RequestPath) is { } open && LaneActivity.FromLead(open, member)))
                {
                    return true;
                }
            }

            return false;
        }

        // read once, before the rows: a response written during the loop moves it, so the next tick derives again
        var msgsVersion = Hs.Core.MsgsVersion();
        long tLandedOnly = 0, tDelivered = 0, tPath = 0, tShown = 0, tExchange = 0, tFront = 0, tFollow = 0, tStream = 0, tManager = 0;
        var mark = System.Diagnostics.Stopwatch.GetTimestamp();
        long Since() { var now = System.Diagnostics.Stopwatch.GetTimestamp(); var e = now - mark; mark = now; return e; }
        foreach (var row in rows.Concat(derived.Rows))
        {
            var isDerived = row.ContainsKey("derived");
            if (Str(row, "request_id") is not { } requestId)
            {
                continue;
            }

            var rowPath = RequestPathOf(row, pools, nowMs);
            tPath += Since();
            if (rowPath is not { } path)
            {
                continue;
            }

            var wasShown = shown(JsonValue.FromString(requestId));
            tShown += Since();
            if (wasShown)
            {
                continue;
            }

            if (withdrawn.Contains(NodePath.Basename(path)))
            {
                continue;
            }

            var hdir = NodePath.Dirname(NodePath.Dirname(path));
            if (!ownPools.Contains(hdir))
            {
                _ = foreignPools.Add(hdir);
            }

            var found = ExchangeOf(pools, hdir, requestId);
            tExchange += Since();
            var front = found is null ? null : FrontMatterOf(found);
            tFront += Since();
            if (found is not { } exchange || front is not { } fm || Str(fm, "from") != "orchestrator")
            {
                continue;
            }

            var viaEntry = isDerived && derived.EntryIds.Contains(requestId);
            if (isDerived && (!(derived.Owned.Contains(Str(fm, "team") ?? string.Empty) || viaEntry) || !WithinHorizon(Str(fm, "created"), nowMs)))
            {
                continue;
            }

            var open = exchange.TryGetValue("open", out var o) && o.Kind == JsonKind.Bool && o.AsBool();
            var responsePath = ObjectOf(exchange, "response") is { } response ? Str(response, "path") : null;
            var slug = Str(exchange, "slug") ?? string.Empty;
            var computed = Path.Combine(NodePath.Dirname(path), $"{requestId}--{Str(fm, "from")}--{slug}--response.md");
            var isLanded = !open && responsePath is not null && graph.Landed.Of(msgsVersion, responsePath, () => Hs.Messages.TryResponseLanded(responsePath, nowMs), static landed => landed is not null) == true;
            if (isLanded)
            {
                landedRows++;
            }

            tLandedOnly += Since();
            var landed = isLanded && !reached.Value(requestId, responsePath!);
            tDelivered += Since();
            if (isDerived && !open && !landed)
            {
                continue;
            }

            var followUp = string.Empty;
            if (!open && responsePath is not null)
            {
                var text = Hs.Messages.ReportFollowUp(JsonValue.FromString(responsePath));
                followUp = text.Kind == JsonKind.Text ? text.AsString() : string.Empty;
            }

            tFollow += Since();
            var managerTeam = Str(fm, "team") is { Length: > 0 } sub
                && TeamRecord(hdir, sub).Member("parent")?.Member("team") is { IsText: true } manager
                    ? manager.AsString()
                    : null;
            tManager += Since();
            var streamKey = (hdir, Str(fm, "team"), Str(fm, "to_name") ?? MemberOfAddress(Str(row, "to_addr")) ?? Str(fm, "to"));
            if (!streamMemo.TryGetValue(streamKey, out var streamOf))
            {
                // b434: per request this re-listed the lane's exchanges and ran git log per stream, ~600 ms a pass over 300 rows
                streamOf = StreamOf(wake, streamKey.hdir, streamKey.Item2, streamKey.Item3, TeamRecord, ChildTeam);
                streamMemo[streamKey] = streamOf;
            }

            var (streamActive, streamState) = streamOf;
            if (!pauseMemo.TryGetValue(streamKey, out var pause))
            {
                pause = streamKey.Item3 is { Length: > 0 } pausedName ? graph.Roster.PauseOf(hdir, streamKey.Item2, pausedName) : null;
                pauseMemo[streamKey] = pause;
            }

            var charterOfLane = open && SnapshotOf(nowMs).Once("charters\0" + hdir, () => Hs.Core.OpenLaneCharterIds(hdir)).Contains(requestId);
            var pausedLane = charterOfLane && streamState == "paused";
            var activeLane = charterOfLane && streamState is "working" or "review" or "blocked";
            tStream += Since();
            string? afterSlug = null, afterEta = null;
            double? afterClosed = null;
            if (Str(fm, "after") is { Length: > 0 } afterId && ExchangeOf(pools, hdir, afterId) is { } before && FrontMatterOf(before) is { } beforeFm)
            {
                afterSlug = Str(before, "slug") is { Length: > 0 } beforeSlug ? beforeSlug : afterId;
                afterEta = Str(beforeFm, "eta");
                var beforeOpen = before.TryGetValue("open", out var bo) && bo.Kind == JsonKind.Bool && bo.AsBool();
                if (!beforeOpen && ObjectOf(before, "response") is { } beforeResponse && Str(beforeResponse, "path") is { } beforePath
                    && graph.Io.Files.Stat(beforePath) is { } beforeStat)
                {
                    afterClosed = beforeStat.MtimeMs;
                }
            }

            candidates.Add(new DispatchCandidate(
                requestId,
                Str(exchange, "to") ?? string.Empty,
                slug,
                Str(row, "to_addr"),
                Str(fm, "to_name"),
                Str(fm, "eta") is { Length: > 0 } eta ? eta : "medium",
                Str(fm, "created"),
                open,
                open ? computed : responsePath ?? computed,
                landed,
                followUp,
                hdir)
            {
                Team = Str(fm, "team"),
                Item = Str(fm, "item"),
                Title = Str(fm, "title"),
                Note = Str(fm, "reason") == Ah.Hierarchy.HierarchyModule.AutoTrackedNoteReason,
                PlainSend = Str(fm, "reason") == Ah.Hierarchy.HierarchyModule.PlainSendReason,
                ManagerTeam = managerTeam,
                StreamActiveMs = streamActive,
                PausedLane = pausedLane,
                MemberPaused = pause is { Paused: true },
                MemberResumedMs = pause is { Paused: false, AtMs: var resumedAt } ? resumedAt : null,
                ActiveLane = activeLane,
                LaneCharter = charterOfLane,
                LaneDelegating = open && LaneDelegating(hdir, Str(fm, "team"), Str(fm, "to_name")),
                AfterSlug = afterSlug,
                AfterEta = afterEta,
                AfterClosedMs = afterClosed,
                SentMs = sentAt.TryGetValue(requestId, out var sentFirst) ? sentFirst : null,
                Undelivered = isDerived && !viaEntry,
                BriefUnread = derived.UnreadBriefs.Contains(requestId),
                RequestPath = path,
            });
        }

        Lap($"loop({candidates.Count})");
        static long Ms(long ticks) => ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;
        var loopLaps = $" loop({candidates.Count}) ms: path={Ms(tPath)} shown-call={Ms(tShown)} exchange={Ms(tExchange)} frontmatter={Ms(tFront)} landed+delivered={Ms(tLandedOnly + tDelivered)} (landed={Ms(tLandedOnly)} delivered={Ms(tDelivered)}) followup={Ms(tFollow)} manager={Ms(tManager)} stream={Ms(tStream)};";
        if (laps.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(p => long.Parse(p[(p.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture) >= 100))
        {
            log?.Invoke(DispatchesSlowLine(laps.ToString(), loopLaps, sessionId, reachedBuildMs, foreignPools.Count, landedRows, $"{_exchangeMemo.Hits - memoHitsAtStart}/{_exchangeMemo.Builds - memoBuildsAtStart}"));
        }

        return candidates;
    }

    /// <summary>b487's settled response paths, per pool, for the life of this daemon.</summary>
    private readonly DoneReportScan _doneScan = new(clock.UtcNow.ToUnixTimeMilliseconds());

    public IReadOnlyList<DoneReport> DoneReports(string sessionId, double nowMs)
    {
        var reports = new List<DoneReport>();
        if (Mail_.ReadRegistration(sessionId) is not { } reg)
        {
            return reports;
        }

        var snapshot = SnapshotOf(nowMs);
        HashSet<string>? alarmed = null;
        foreach (var hdir in MeshModule.RegistrationTeamFiles(reg).Select(static f => NodePath.Dirname(NodePath.Dirname(f))).Distinct(StringComparer.Ordinal))
        {
            var names = TeamsOf(hdir, sessionId).Select(static n => n ?? string.Empty).ToList();
            if (names.Count == 0)
            {
                continue;
            }

            var owned = names.ToHashSet(StringComparer.Ordinal);
            var pools = snapshot.PoolsOf(hdir + "\0" + string.Join('\0', names), () => Hs.Messages.PoolDirs(hdir, names));
            // the derived pass's key: one versioned listing serves both
            var all = Objects(snapshot.ExchangesOf("derived\0" + string.Join('\0', pools), () => ListExchangesMemo(pools)));
            var resolved = PerTick("config\0" + hdir, () => graph.Config.ResolveConfig(NodePath.Dirname(NodePath.Dirname(hdir))));
            string? ClassOf(string role) => graph.Config.RoleClass(role, resolved);
            string? MemberClass(string address) =>
                address.Split('@', 2) is [var name, var team]
                && TeamRecordOf(hdir, team).Member("members") is { IsArray: true } members
                && Objects(members.AsArray()).FirstOrDefault(r => Str(r, "name") == name) is { } row && Str(row, "role") is { } role
                    ? ClassOf(role)
                    : null;

            alarmed ??= PeerRecords().Where(static r => Str(r, "type") == WatchModule.UnreviewedDoneRowType).Select(static r => Str(r, "request_id")).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            reports.AddRange(_doneScan.Scan(pools, all, owned, alarmed, nowMs, new DoneScanReads(
                role => role != WatchModule.QaRole && ClassOf(role) == "implement",
                Hs.Core.MsgTimeMs,
                FrontMatterOf,
                path => Hs.Core.ReadMsgFile(path) is { Kind: JsonKind.Map } file ? Str(file.AsObject(), "text") : null,
                (id, member, team) => DoneReportScan.HasReview(
                    id, member, all, FrontMatterOf, ClassOf,
                    team is { Length: > 0 } && graph.Links.ReadLinks(hdir, team).TryGetValue("links", out var links) && links.IsArray ? Objects(links.AsArray()) : [],
                    MemberClass))));
        }

        return reports;
    }

    /// <summary>
    /// The index over the session's pools and the pools of the rows still in play, listed as one set: the rows of a session sit in
    /// many pools outside its own, and an index asked for each of them would list every pool again for each one. The set is the
    /// same from tick to tick, so the listing is kept by <see cref="ExchangeMemo"/> under one key.
    /// </summary>
    private IExchangeIndex WithRowPools(string sessionId, TickSnapshot snapshot, IExchangeIndex index, IEnumerable<JsonObject> rows, Func<JsonValue?, bool> shown, IReadOnlySet<string> withdrawn, double nowMs)
    {
        var extra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (Str(row, "request_id") is not { } requestId || RequestPathOf(row, index, nowMs) is not { } path
                || withdrawn.Contains(NodePath.Basename(path)) || shown(JsonValue.FromString(requestId)))
            {
                continue;
            }

            var pool = NodePath.Dirname(NodePath.Dirname(path));
            if (!index.Pools.Contains(pool))
            {
                _ = extra.Add(pool);
            }
        }

        if (extra.Count == 0)
        {
            return index;
        }

        List<string> all = [.. index.Pools, .. extra];
        var widened = snapshot.IndexOf("rows\0" + string.Join('\0', all), () => HierarchyModule.CreateExchangeIndex(Hs.Core, all, ListExchangesMemo));
        lock (_indexes)
        {
            _indexes[sessionId] = (widened, clock.UtcNow.ToUnixTimeMilliseconds());
        }

        return widened;
    }

    /// <summary>The slow-dispatches log line: the lap and loop timings as they have always read, then the session, the one-off cost of building the reached check, the foreign pools hit and the landed rows.</summary>
    internal static string DispatchesSlowLine(string laps, string loopLaps, string sessionId, long reachedBuildMs, int foreignPools, int landedRows, string? exchangeMemo = null) =>
        $"dispatches slow:{laps}{loopLaps} sid={(sessionId.Length > 8 ? sessionId[..8] : sessionId)} reached-build={reachedBuildMs} foreign={foreignPools} landed-rows={landedRows}{(exchangeMemo is null ? string.Empty : " xmemo=" + exchangeMemo)}";

    /// <summary>How long after its <c>created</c> a request the session's dispatch rows do not name is still timed from its file.</summary>
    private const double DerivedHorizonMs = 24 * 3600 * 1000;

    private static bool WithinHorizon(string? created, double nowMs) =>
        created is not null && Timestamps.TryParseMs(created, out var ms) && nowMs - ms <= DerivedHorizonMs;

    /// <summary>
    /// Whether an exchange id's stamp puts it past the horizon, so its front matter need not be read. No file is consulted: a message kept only in the database has none.
    /// The id does not say its zone, so a day of margin; <see cref="WithinHorizon"/> then decides exactly. An id with no stamp is not older.
    /// </summary>
    internal static bool IdOlderThanHorizon(string id, double nowMs) =>
        id.Length >= 15
        && DateTimeOffset.TryParseExact(id[..15], "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
        && nowMs - at.ToUnixTimeMilliseconds() > DerivedHorizonMs + (24 * 3600 * 1000);

    /// <summary>The brief entries from an owned team's Orchestrator that sit in that team's member boxes, as <c>ref</c> and <c>to</c> pairs; only those boxes are read.</summary>
    private JsonArray BriefEntriesOf(IReadOnlyList<string> ownedTeams, HashSet<string>? read = null)
    {
        // the listing reads every box of the owned teams (hundreds, each two store reads); unchanged while no mail is written
        var key = string.Join('\0', ownedTeams) + "\0\0" + (read is null ? "*" : string.Join('\0', read.Order(StringComparer.Ordinal)));
        var token = Mail_.MailChangeToken();
        if (token is not null && _briefMemo is { } held && held.Token == token && held.Key == key)
        {
            return held.Found;
        }

        var found = new JsonArray();
        foreach (var box in Mail_.ListBoxes().Where(b => ownedTeams.Any(team => b.Address.EndsWith("@" + team, StringComparison.Ordinal))))
        {
            // a closed member's box (no roster row, no live exchange) adds no candidate: skipped, never moved; unless its newest entry is
            // inside the horizon, which a brief of a request the owned listing does not hold (a foreign pool) can be
            if (read is not null && MemberOfAddress(box.Address) is { } member && !read.Contains(member)
                && Mail_.StoredNewestEntry(box.Dir) is { } newest
                && Mail_.ReadEntries(box.Dir, Math.Max(0, newest - 1)).Entries is var tail
                && !tail.Any(e => e.IsMap && e.Member("id") is { IsText: true } id && !IdOlderThanHorizon(id.AsString(), NowMs())))
            {
                continue;
            }

            // one listing of the box's acks, read only for a box that holds a brief; not one lookup per entry
            var acked = new Lazy<Func<string, bool>>(() => Mail_.AckedIn(box.Dir));
            foreach (var entry in Mail_.ReadEntries(box.Dir).Entries)
            {
                static string? Field(JsonValue v, string key) => v.Member(key) is { IsText: true } f ? f.AsString() : null;
                if (!entry.IsMap || Field(entry, "kind") != "brief" || Field(entry, "ref") is not { } reference
                    || !reference.EndsWith("--request.md", StringComparison.Ordinal)
                    || Field(entry, "from") is not { } from || !ownedTeams.Any(team => from == HierarchyModule.OrchestratorAddress(team))
                    || Hs.Core.ParseMsgFilename(reference) is not { Type: "request" })
                {
                    continue;
                }

                found.Add(new JsonObject
                {
                    ["ref"] = reference,
                    ["to"] = Field(entry, "to") ?? string.Empty,
                    ["unread"] = Field(entry, "id") is { } id && !acked.Value(id),
                });
            }
        }

        _briefMemo = new BriefMemo(token, key, found);
        return found;
    }

    /// <summary>A brief listing under the mail counter read before it: a write between the two is listed again next time, never missed. Callers must not change it.</summary>
    private sealed record BriefMemo(long? Token, string Key, JsonArray Found);

    private volatile BriefMemo? _briefMemo;

    private sealed record DerivedSet(List<JsonObject> Rows, HashSet<string> Owned, HashSet<string> EntryIds, HashSet<string> UnreadBriefs);

    /// <summary>
    /// The requests of the teams this session owns that no dispatch row names, as rows marked <c>derived</c> so the dispatch loop
    /// filters them like any row: every request file of the owned teams' pools, plus the requests that brief entries from the
    /// team's orchestrator in a member's box point at. Ownership is the team file's, never a liveness reading.
    /// </summary>
    private DerivedSet DerivedRows(string sessionId, TickSnapshot snapshot, IReadOnlyList<JsonObject> rows, double nowMs)
    {
        var set = new DerivedSet([], new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        if (Mail_.ReadRegistration(sessionId) is not { } reg)
        {
            return set;
        }

        var taken = rows.Select(static r => Str(r, "request_id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        JsonObject Add(string id, string path)
        {
            if (!byId.TryGetValue(id, out var row))
            {
                row = new JsonObject { ["request_id"] = id, ["path"] = path, ["derived"] = true };
                byId[id] = row;
                set.Rows.Add(row);
            }

            return row;
        }

        var ownedNamed = new List<string>();

        // the members whose boxes are read (b283): a roster row, or an exchange to it that is open, inside the horizon or a dispatch row's; null reads every box
        HashSet<string>? read = new(StringComparer.Ordinal) { "orchestrator" };
        foreach (var hdir in MeshModule.RegistrationTeamFiles(reg).Select(static f => NodePath.Dirname(NodePath.Dirname(f))).Distinct(StringComparer.Ordinal))
        {
            var names = TeamsOf(hdir, sessionId).Select(static n => n ?? string.Empty).ToList();
            if (names.Count == 0)
            {
                continue;
            }

            set.Owned.UnionWith(names);
            ownedNamed.AddRange(names.Where(static n => n.Length > 0));
            foreach (var name in names.Where(static n => n.Length > 0))
            {
                if (TeamRecordOf(hdir, name) is { Kind: JsonKind.Map } team && team.AsObject().TryGetValue("members", out var m) && m.Kind == JsonKind.Array)
                {
                    read?.UnionWith(Objects(m.AsArray()).Select(static r => Str(r, "name")).OfType<string>());
                }
            }

            var pools = snapshot.PoolsOf(hdir + "\0" + string.Join('\0', names), () => Hs.Messages.PoolDirs(hdir, names));
            var exchanges = snapshot.ExchangesOf("derived\0" + string.Join('\0', pools), () => ListExchangesMemo(pools));
            foreach (var exchange in Objects(exchanges))
            {
                if (Str(exchange, "id") is not { } id || ObjectOf(exchange, "request") is not { } request)
                {
                    continue;
                }

                var recent = !IdOlderThanHorizon(id, nowMs);
                if (recent && Str(request, "path") is { } path)
                {
                    _ = Add(id, path);
                }

                if (read is not null && (recent || taken.Contains(id) || exchange.Get("open") is { IsTruthy: true }))
                {
                    if (ObjectOf(request, "meta") is { } meta && Str(meta, "to_name") is { } toName)
                    {
                        _ = read.Add(toName);
                    }
                    else
                    {
                        read = null; // a live request with no member name: its box is unknown, so none is skipped
                    }
                }
            }
        }

        var briefs = ownedNamed.Count == 0
            ? new JsonArray()
            : snapshot.ExchangesOf(
                "briefs\0" + string.Join('\0', ownedNamed.Order(StringComparer.Ordinal)) + "\0\0" + (read is null ? "*" : string.Join('\0', read.Order(StringComparer.Ordinal))),
                () => BriefEntriesOf(ownedNamed, read));
        foreach (var entry in Objects(briefs))
        {
            var reference = Str(entry, "ref")!;
            var meta = Hs.Core.ParseMsgFilename(reference)!;
            if (entry.Get("unread") is { IsTruthy: true })
            {
                _ = set.UnreadBriefs.Add(meta.Id);
            }

            if (IdOlderThanHorizon(meta.Id, nowMs))
            {
                continue;
            }

            var row = Add(meta.Id, reference);
            if (Str(entry, "to") is { Length: > 0 } to)
            {
                row["to_addr"] = to;
            }

            _ = set.EntryIds.Add(meta.Id);
        }

        set.Rows.RemoveAll(r => taken.Contains(Str(r, "request_id")!));
        return set;
    }

    private static string? MemberOfAddress(string? address) =>
        address is { Length: > 0 } && address.IndexOf('@', StringComparison.Ordinal) is var at and > 0 ? address[..at] : null;

    private static (double? UpdatedMs, string? State) StreamOf(WakeComposition wake, string hdir, string? team, string? member, Func<string, string, JsonValue> teamRecord, Func<string, string, string, string?> findChildTeam) =>
        wake.StreamOf(hdir, team, member, teamRecord, findChildTeam);

    /// <summary>
    /// The shared parse of the peer records: callers must not change a record. Read once per tick for each version of the peer log, so a
    /// write during the tick is seen by the reads after it, as when every read queried the log (b459).
    /// </summary>
    public IReadOnlyList<JsonObject> PeerRecords() =>
        Hs.Core.PeerRecordsVersion() is { } version ? PerTick("peer-records\0" + version.ToString(System.Globalization.CultureInfo.InvariantCulture), ReadPeerSnapshot) : ReadPeerSnapshot();

    /// <summary>The types the readers of <see cref="SessionPeerRecords"/> keep: the shown-report test (seen, surfaced) and the dispatch rows.</summary>
    private static readonly HashSet<string> DispatchPeerTypes = new(StringComparer.Ordinal) { "dispatch", "seen", "surfaced" };

    /// <summary>
    /// The peer records of <see cref="DispatchPeerTypes"/> whose <c>session_id</c> is the text <paramref name="sessionId"/>, in log order,
    /// read once per tick by that key: the whole log is not parsed and held to answer one session (b459).
    /// </summary>
    private IReadOnlyList<JsonValue> SessionPeerRecords(string sessionId, TickSnapshot tick) =>
        tick.Once("peer-of\0" + sessionId, () => graph.SessionPeers.Of(
            Hs.Core.PeerRecordsVersion(),
            sessionId,
            () => (IReadOnlyList<JsonValue>)[.. Hs.Core.PeerRecordsWhere(new PeerQuery("session_id", sessionId, DispatchPeerTypes)).Where(static r => r.Member("session_id") is { IsText: true })]));

    private IReadOnlyList<JsonObject> ReadPeerSnapshot() =>
        [.. Hs.Core.PeerRecordsWhere(new PeerQuery(Types: WatchModule.PeerSnapshotTypes)).Where(static v => v.Kind == JsonKind.Map).Select(static v => v.AsObject())];

    /// <summary>The gate rows of a hierarchy dir, held parsed by ReadGates and read again only as a delta (b459 step 13): callers must not change a row.</summary>
    public IReadOnlyList<JsonObject> Gates(string hierarchyDir) => Objects(Hs.Roster.ReadGates(hierarchyDir));

    public IReadOnlyList<JsonObject>? GatesOrNull(string hierarchyDir, out string? why) => Hs.Roster.ReadGatesOrNull(hierarchyDir, out why) is { } rows ? Objects(rows) : null;

    public double DispatchBaseMs(string sessionId, string member)
    {
        var pools = CurrentIndex(sessionId);
        foreach (var row in DispatchRows(sessionId))
        {
            if (Str(row, "request_id") is not { } requestId || RequestPathOf(row, pools, NowMs()) is not { } path)
            {
                continue;
            }

            if (OpenRequestFrontMatter(pools, path, requestId) is { } fm && Str(fm, "to_name") == member
                && Str(fm, "created") is { } created && Timestamps.TryParseMs(created, out var ms))
            {
                return ms;
            }
        }

        return 0;
    }

    public IReadOnlyList<string> AddressesOf(string sessionId, string name)
    {
        var addresses = new List<string>();
        var pools = CurrentIndex(sessionId);
        var shown = Hs.Peers.ReportShownFor(JsonValue.FromString(sessionId));
        foreach (var row in DispatchRows(sessionId))
        {
            if (Str(row, "to_addr") is not { Length: > 0 } toAddr || Str(row, "request_id") is not { } requestId
                || shown(JsonValue.FromString(requestId)))
            {
                continue;
            }

            var mine = toAddr == name;
            if (!mine && RequestPathOf(row, pools, NowMs()) is { } path)
            {
                mine = OpenRequestFrontMatter(pools, path, requestId) is { } fm && Str(fm, "to_name") == name;
            }

            if (mine && !addresses.Contains(toAddr))
            {
                addresses.Add(toAddr);
            }
        }

        return addresses;
    }

    // ---------------------------------------------------------------- context, blocked, recovery

    public IReadOnlyList<ContextSubject> ContextSubjects(string sessionId, string hierarchyDir)
    {
        var subjects = new List<ContextSubject>();
        foreach (var name in TeamsOf(hierarchyDir, sessionId))
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var team = TeamRecordOf(hierarchyDir, name);
            if (team.Kind != JsonKind.Map)
            {
                continue;
            }

            foreach (var fill in Objects(Hs.Status.MemberFills(hierarchyDir, team, name)))
            {
                if (Str(fill, "name") is not { Length: > 0 } memberName)
                {
                    continue;
                }

                subjects.Add(new ContextSubject(
                    Str(fill, "session_id"), memberName, $"{memberName}@{name}", ObjectOf(fill, "context"), Str(fill, "role")));
            }
        }

        return subjects;
    }

    public IReadOnlyList<OwnedTeam> OwnedTeams(string sessionId, string hierarchyDir)
    {
        var owned = new List<OwnedTeam>();
        var roster = RosterOf(hierarchyDir);
        foreach (var name in TeamsOf(hierarchyDir, sessionId))
        {
            var team = name is null ? graph.Roster.ReadTeamRecord(hierarchyDir, name) : TeamRecordOf(hierarchyDir, name);
            if (team.Kind != JsonKind.Map)
            {
                continue;
            }

            var doc = team.AsObject();
            var rows = doc.TryGetValue("members", out var m) && m.Kind == JsonKind.Array ? Objects(m.AsArray()) : [];
            var members = new List<MemberView>();
            foreach (var described in Objects(Hs.Status.DescribeMembers(hierarchyDir, team, roster, name)))
            {
                var memberName = Str(described, "name");
                var row = rows.FirstOrDefault(r => Str(r, "name") == memberName);
                if (memberName is null || row is null)
                {
                    continue;
                }

                members.Add(new MemberView(
                    memberName,
                    row,
                    described.TryGetValue("live", out var live) && live.Kind == JsonKind.Bool ? live.AsBool() : null,
                    Str(described, "activity"),
                    Str(described, "blocked_by"),
                    Str(described, "activity_at"),
                    Str(described, "session_id"))
                {
                    Paused = graph.Roster.PauseOf(hierarchyDir, name, memberName) is { Paused: true },
                });
            }

            owned.Add(new OwnedTeam(name, doc, members));
        }

        return owned;
    }

    public IReadOnlyList<JsonObject>? HerdrAgents() => seams.Herdr.AgentList();

    public bool WriteTerminalLabels(string address, IReadOnlyList<string> pairs) => seams.Herdr.Label(address, pairs);

    public string? SessionTerminal(string home, string sessionId) =>
        Hs.Roster.AttributedRoster(home)
            .Where(r => r.Member("session_id") is { IsText: true } s && s.AsString() == sessionId)
            .Select(static r => r.Member("pane_id") is { IsText: true } p ? p.AsString() : null)
            .LastOrDefault(static p => Ah.Core.Mux.MuxIds.IsHerdrCoreAddress(p));

    public PromptRead ReadPrompt(JsonObject memberRow, JsonObject team)
    {
        var kind = KindOf(memberRow);
        var session = Str(memberRow, "session_id");
        var flavour = Ah.Core.Mux.HerdrFlavour.Resolve(string.IsNullOrEmpty(session) ? null : graph.Mailbox.ReadRegistration(session), team, null, JsonValue.FromObject(memberRow));
        var screen = seams.Herdr.ReadVisibleScreen(Str(memberRow, "name") ?? string.Empty, flavour);
        var seen = screens.RecognizeScreen(kind, screen, "blocked");
        return new PromptRead(screens.ScreenHash(screen), screen, Str(seen, "prompt"));
    }

    public string KindOf(JsonObject memberRow) => graph.Config.ResolveKind(JsonValue.FromObject(memberRow)) is { Kind: JsonKind.Text } kind ? kind.AsString() : "claude";

    public bool RowAdapted(JsonObject memberRow) => CodexModule.RowAdapted(JsonValue.FromObject(memberRow));

    public string? PromptHeading(string kind, string? recognized) => seams.PromptHeading(kind, recognized);

    public string RosterCommand => graph.Config.CliCommand(CliTool.Roster);

    // ---------------------------------------------------------------- writes

    public virtual void AppendGates(IReadOnlyList<GateAppend> gates)
    {
        foreach (var gate in gates)
        {
            Hs.StateCalls.AppendGate(gate.Dir, JsonValue.FromObject(gate.Row));
        }
    }

    public virtual void MarkScreenClear(string home, string sessionId, bool clear, double atMs)
    {
        // the record's other copy is the one under the member's own checkout, which the hook writes alongside the team's (G5)
        var cwd = graph.Mailbox.ReadRegistration(sessionId) is { } registration ? Str(registration, "cwd") : null;
        var own = string.IsNullOrEmpty(cwd) ? null : graph.Config.HierarchyDir(cwd);
        var at = clear ? Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)atMs)) : null;
        _ = Hs.Status.MarkScreenClearRouted(home, JsonValue.FromString(sessionId), at, own);
    }

    public virtual void AppendPeerRows(IReadOnlyList<PeerAppend> rows)
    {
        foreach (var row in rows)
        {
            Hs.Peers.RecordPeerRow(JsonValue.FromObject(row.Row));
        }
    }

    public virtual void MarkSurfaced(string sessionId, IReadOnlyList<string> requestIds)
    {
        foreach (var id in requestIds)
        {
            Hs.Peers.AppendReportRecord(JsonValue.FromString("surfaced"), JsonValue.FromString(sessionId), JsonValue.FromString(id));
        }
    }

    public virtual void RecordWatchEvents(string sessionId, IReadOnlyList<WatchEvent> events)
    {
        var ts = Timestamps.ToIsoString(clock.UtcNow);
        foreach (var ev in events)
        {
            var row = new JsonObject
            {
                ["type"] = "watch-event",
                ["session_id"] = sessionId,
                ["request_id"] = ev.RequestId is null ? JsonValue.Null : JsonValue.FromString(ev.RequestId),
                ["kind"] = ev.Kind,
                ["text"] = ev.Text,
                ["ts"] = ts,
            };
            if (ev.Extra is { } extra)
            {
                foreach (var key in extra.Keys)
                {
                    row[key] = extra[key];
                }
            }

            Hs.Peers.RecordPeerRow(JsonValue.FromObject(row));
        }
    }

    public virtual void Mail(WatchMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        var request = MeshModule.EventRequest(mail.To, "ahd", mail.Cwd, mail.HierarchyDir, mail.Event, mail.Text);
        request["session_id"] = JsonValue.Null;
        request["data"] = mail.Data;
        request["to_session"] = mail.ToSession;
        if (mail.Id is not null)
        {
            request["id"] = mail.Id;
        }

        if (AlreadyMailed(mail))
        {
            return;
        }

        var outcome = Mail_.Send(request, mail.CreatedAt ?? clock.UtcNow);
        if (!outcome.Ok)
        {
            throw new InvalidOperationException(outcome.Failure?.ToString() ?? "the mail was refused");
        }
    }

    /// <summary>
    /// Whether the box already holds an entry for this event of this request at this interval of its clock (acked or not), so the one
    /// writer of daemon events never mails the same event twice. An event whose data names no interval is not checked here, but the mailbox still answers an identical one (same payload) sent within its repeat window with the earlier entry. The interval is a
    /// number (a dispatch's due slot) or a text (a lane episode).
    /// </summary>
    public bool AlreadyMailed(WatchMail mail)
    {
        if (!mail.Data.TryGetValue("due_slot", out var slot) || !(slot.IsNumber || slot.IsText) || !mail.Data.TryGetValue("request_id", out var request) || !request.IsText)
        {
            return false;
        }

        var box = Mail_.ListBoxes().FirstOrDefault(b => b.Address == mail.To);
        return box is not null && Mail_.ReadEntries(box.Dir).Entries.Any(e =>
            e.Member("kind") is { IsText: true } kind && kind.AsString() == "event"
            && e.Member("event") is { IsText: true } name && name.AsString() == mail.Event
            && e.Member("data")?.Member("request_id") is { IsText: true } rid && rid.AsString() == request.AsString()
            && e.Member("data")?.Member("due_slot") is { } held && (slot.IsNumber ? held.IsNumber && held.AsNumber() == slot.AsNumber() : held.IsText && held.AsString() == slot.AsString()));
    }

    public virtual Task SweepLinksAsync(IReadOnlyList<WatchBox> boxes, string cwd, string hierarchyDir, double nowMs, CancellationToken cancellation)
    {
        if (hierarchyDir.Length == 0)
        {
            return Task.CompletedTask;
        }

        var context = new RequestContext(cwd, hierarchyDir, string.Empty, string.Empty);
        var lookup = Mail_.TeamLookup(context);
        var teams = boxes.Select(b => Mail_.SplitAddress(b.Address)?.Team).Where(static t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal);
        foreach (var team in teams)
        {
            if (lookup(team!) is not { Dir: { } dir })
            {
                continue;
            }

            var tick = SnapshotOf(nowMs);
            if (tick.Swept(dir, team!))
            {
                continue;
            }

            var args = new JsonObject { ["dir"] = dir, ["team"] = team!, ["now"] = nowMs };
            var swept = graph.Writer.Invoke("links", "sweep", JsonText.ToUtf8(args, 0), context, new StateCallOptions($"sweep-{Guid.NewGuid()}"));
            if (!swept.Ok)
            {
                log?.Invoke($"link sweep for {team}: {swept.ErrorCode ?? swept.Error}");
                continue;
            }

            tick.MarkSwept(dir, team!);

            var effects = new TickLinkEffects(graph, context, cwd, hierarchyDir, () => clock.UtcNow);
            var runLinks = MeshModule.CreateLinkCommands(
                MeshModule.CreateMeshRule(graph.Links, graph.Environment), graph.Links, graph.Hierarchy.Core, graph.Hierarchy.Messages, graph.Io.Files, graph.Io.Clock, effects, effects, graph.RunFacts);
            try
            {
                runLinks.RevokeDeadRunLinksAsync(new LinkContext(hierarchyDir, hierarchyDir), lookup, team!, cancellation, nowMs).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.Invoke($"run-link revoke for {team}: {e.Message}");
            }

            var result = JsonText.Parse(swept.ResultJson.ToArray());
            if (result.Kind != JsonKind.Map || !result.AsObject().TryGetValue("ended", out var ended) || ended.Kind != JsonKind.Array)
            {
                continue;
            }

            foreach (var item in ended.AsArray())
            {
                if (item.Kind != JsonKind.Array)
                {
                    continue;
                }

                var parts = item.AsArray().ToList();
                var text = string.Join(' ', parts.Take(2).Select(Show)) + $" used={(parts.Count > 2 ? Show(parts[2]) : "undefined")}";
                var announce = MeshModule.EventRequest(HierarchyModule.OrchestratorAddress(team), "ahd", cwd, hierarchyDir, "link-ended", text);
                _ = Mail_.Send(announce, clock.UtcNow);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary><c>inspectTimers</c> over this host.</summary>
    public JsonObject InspectTimers(string? session, double nowMs, double pollMs) => WatchModule.InspectTimers(this, nowMs, pollMs, session);

    /// <summary><c>inspectEvents</c> over the mailbox's boxes and entries.</summary>
    public JsonObject InspectEvents(string? address) =>
        WatchModule.InspectEvents(
            Mail_.ListBoxes().Select(static b => new WatchBox(b.Address, b.Dir)),
            dir => [.. Mail_.ReadEntries(dir).Entries.Where(static v => v.Kind == JsonKind.Map).Select(static v => v.AsObject())],
            (dir, id) => Mail_.IsAcked(dir, id),
            address);

    public virtual Task<JsonObject> RunLadderAsync(double nowMs, double pollMs, CancellationToken cancellation) =>
        seams.Ladder.RunAsync(nowMs, pollMs, cancellation);

    // ---------------------------------------------------------------- recovery host

    public bool PeerBusy(string dir, string name, string? team) =>
        Hs.Roster.AttributedLiveness(Hs.Roster.AttributedRoster(dir), JsonValue.FromString(name), team).Get("rec") is { } rec && (rec.Member("busy") is { IsBool: true } busy && busy.AsBool() || rec.Member("shell") is { IsBool: true } shell && shell.AsBool());

    public double? PeerBusySeenMs(string dir, string name, string? team) =>
        Hs.Roster.AttributedLiveness(Hs.Roster.AttributedRoster(dir), JsonValue.FromString(name), team).Get("rec")?.Member("ts") is { IsText: true } ts
        && Ah.Core.Time.Timestamps.TryParseMs(ts.AsString(), out var ms) ? ms : null;

    public IReadOnlyList<ChecklistEntry> Checklist(string session, string home, string? team)
    {
        try
        {
            var main = SessionCwd(session) is { } cwd ? graph.Config.MainHierarchyDir(cwd) : null;
            return graph.Checklists.Of(HierarchyModule.ProgressPoolOf(main, home), HierarchyModule.ProgressTeamOf(team));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"checklist of {team ?? "_default"} for {session} not read ({e.Message})");
            return [];
        }
    }

    public IReadOnlyList<ReadyBacklogItem> ReadyBacklog(string session, string home)
    {
        try
        {
            var dir = SessionCwd(session) is { } cwd ? graph.Config.MainHierarchyDir(cwd) ?? home : home;

            // b459 C3: the board reads the work registry, the backlog, the plan logs (all written under the "backlog" counter) and the
            // teams' stream lists (document names, only ever added by an insert); read before the board, so a write between the two re-reads next time.
            // J3: the held answer is given before a reader is opened, so a tick with nothing new opens none.
            var stamp = Ah.StateDb.StateDbModule.DomainSeq(graph.Environment.AhHome, "backlog") is { } seq && _progressDocs.Stamp(dir) is { } count ? $"{seq}:{count}" : null;
            if (stamp is not null)
            {
                lock (_readyBacklogs)
                {
                    if (_readyBacklogs.TryGetValue(dir, out var held) && held.Stamp == stamp)
                    {
                        return held.Items;
                    }
                }
            }

            if (graph.OpenReader(graph.Environment.AhHome).Reader is not { } reader)
            {
                return [];
            }

            using (reader)
            {
                var board = HierarchyModule.WorkBoardOf(reader.Query, Hs.Progress, dir);
                IReadOnlyList<ReadyBacklogItem> items = [.. board.Items
                    .Where(v => v.Row.IsBacklog && v.Plan?.Wave is "now" or "next" && board.IsReady(v))
                    .Select(static v => new ReadyBacklogItem(v.Row.Item, v.Plan!.Title, v.Plan.Wave, v.Plan.Rank))];
                if (stamp is not null)
                {
                    lock (_readyBacklogs)
                    {
                        _readyBacklogs[dir] = (stamp, items);
                    }
                }

                return items;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"ready backlog for {session} not read ({e.Message})");
            return [];
        }
    }

    private readonly Dictionary<string, (string Stamp, IReadOnlyList<ReadyBacklogItem> Items)> _readyBacklogs = new(StringComparer.Ordinal);

    private readonly IProgressDocSource _progressDocs = Ah.StateDb.StateDbModule.CreateProgressDocs(graph.Environment.AhHome);

    /// <summary>Forgets every held ready backlog: the doctor's rebuild.</summary>
    internal void ForgetReadyBacklogs()
    {
        _ = Interlocked.Increment(ref _forgets); // the tick's quiet-dispatch hold (J3) reads this through DispatchInputsStamp

        lock (_readyBacklogs)
        {
            _readyBacklogs.Clear();
        }
    }

    public int? SessionBudgetOf(string home, string? team) => SessionBudgets.Of(graph, home, team, log);

    public IReadOnlyList<JobFact> Jobs(string session, string home, string? team) =>
        WatchModule.JobsFromFiles(Hs.Status, SessionCwd(session) is { } cwd ? graph.Config.MainHierarchyDir(cwd) ?? home : home, team, log);

    public IReadOnlyList<LaneFact> Lanes(string session, string home, IReadOnlyList<OwnedTeam> owned)
    {
        var parents = owned.Select(static t => t.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (parents.Count == 0)
        {
            return [];
        }

        var wake = new WakeComposition(graph, graph.Environment) { CommitOf = CommitPerTick };
        var scan = PerTick("pool-scan\0" + home, () => new WakeComposition.PoolScan(wake, home));
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var lanes = new List<LaneFact>();
        foreach (var parentTeam in parents)
        {
            foreach (var (child, stream, record, name, streamState, updated) in LaneStreams.Of(graph, home, parentTeam))
            {
                var orchestrator = record.Member("orchestrator");
                var subSession = orchestrator?.Member("session_id") is { IsText: true } sid ? sid.AsString() : string.Empty;
                var pid = orchestrator?.Member("pid") is { IsNumber: true } p ? (int?)p.AsNumber() : null;
                var pidStart = orchestrator?.Member("pid_start") is { IsText: true } start ? start.AsString() : null;
                var verdict = pid is { } known ? Hs.Core.Liveness?.PidVerdict(known, pidStart) : null;
                var dead = verdict == Ah.Core.Time.LivenessVerdict.ProvenDead;
                var laneJobs = WatchModule.JobsFromFiles(Hs.Status, home, child);
                var overdueJob = WatchModule.JobOverdueOf(laneJobs, "orchestrator", ThresholdSeconds, now, 30000);
                var blocked = overdueJob is not null && verdict is not null && !dead;
                var waitingOnSlot = overdueJob is null && laneJobs.Any(static j => !j.Ended && j.Owner == "orchestrator" && j.Slot is { Phase: "queued" });
                double? oldestUnacked = null;
                string? unackedId = null;
                if (wake.OrchestratorBoxOfTeam(home, child, record) is { } box)
                {
                    foreach (var e in wake.Events(box.Dir, box.Session).Where(static e => e.AckedAtMs is null && e.Created is not null))
                    {
                        if (Ah.Core.Time.Timestamps.TryParseMs(e.Created!, out var ms) && (oldestUnacked is null || ms < oldestUnacked))
                        {
                            oldestUnacked = ms;
                            unackedId = e.Id;
                        }
                    }
                }

                var workstream = record.Member("workstream");
                var open = wake.OpenDispatches(home, child, subSession, scan, record)
                    .Select(d => new LaneDispatch(d.RequestId, d.ToName, WatchModule.OverdueStateOf(d, now, 30000)))
                    .ToList();
                var deadAnchor = pid is { } deadPid ? $"{deadPid}:{pidStart}" : null;
                var lastAlive = dead && subSession.Length > 0 ? LastSeenMs(subSession) : null;
                var jobDue = overdueJob is null ? (double?)null : Math.Max(overdueJob.SentAtMs, overdueJob.ExtendedAtMs ?? 0);
                var pausedBy = workstream?.Member("paused_by") is { IsText: true } by ? by.AsString() : null;
                var note = workstream?.Member("note") is { IsText: true } noteText ? noteText.AsString() : null;
                var childActive = LaneActivity.LatestMs(graph, home, child, record, now, CommitPerTick);
                lanes.Add(new LaneFact(child, name, dead, blocked, oldestUnacked, streamState, updated, open, parentTeam, stream, deadAnchor, overdueJob?.Id, unackedId, lastAlive, jobDue, pausedBy, note, waitingOnSlot, childActive));
            }
        }

        return lanes;
    }

    /// <summary>When the state database last saw <paramref name="sessionId"/> alive, or null when it records none or cannot be read.</summary>
    private double? LastSeenMs(string sessionId)
    {
        try
        {
            if (graph.OpenReader(graph.Environment.AhHome).Reader is not { } reader)
            {
                return null;
            }

            using (reader)
            {
                foreach (var row in reader.Query("SELECT last_seen_at FROM sessions WHERE session_id = ?1", [sessionId]))
                {
                    return row[0] is long ms ? ms : null;
                }

                return null;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"last seen of {sessionId} not read ({e.Message})");
            return null;
        }
    }

    private IProcessTable? _processTable;

    internal virtual IProcessTable ProcessTree => _processTable ??= Ah.Platform.PlatformModule.CreateProcessTable(graph.Runner);

    public IReadOnlyList<JobFact> ReconcileJobs(string session, string home, string? team, IReadOnlyList<JobFact> jobs)
    {
        var hier = SessionCwd(session) is { } cwd ? graph.Config.MainHierarchyDir(cwd) ?? home : home;
        PruneEndedJobs(hier);
        if (!jobs.Any(static j => !j.Ended))
        {
            return jobs;
        }

        using var processes = ProcessSnapshotScope.Begin(shared: true);
        try
        {
            var files = WatchModule.JobRecordFiles(Hs.Status, WatchModule.JobsDir(hier, team));
            var ownerPids = Hs.Status.SessionPidsOf(hier).ToDictionary(static e => e.Key, static e => (int)e.Value, StringComparer.Ordinal);
            var live = new List<JobFact>();
            var taken = jobs.Where(static j => !j.Ended && j.Pid is not null).Select(static j => j.Pid!.Value).ToHashSet();
            var slotRecords = new Lazy<IReadOnlyList<SlotRecord>>(() => SlotRecordsNow());
            foreach (var job in jobs)
            {
                if (job.Ended)
                {
                    live.Add(job);
                    continue;
                }

                var file = files.FirstOrDefault(f => f.Record.Get("id") is { IsText: true } id && id.AsString() == job.Id);
                var now = Hs.Messages.LocalIso();
                var nowMs = (double)clock.UtcNow.ToUnixTimeMilliseconds();
                var below = ownerPids.TryGetValue(job.Session, out var owner) ? ProcessTree.Descendants(owner) : null;
                var provenBelow = below is not null && graph.Io.Processes.PidAlive(owner) ? below : null;
                var mine = job.Pid is null || below is null || file is null ? null : DeeperThanWrapper(job, below, taken);
                if (job.Pid is null && file is not null && below is not null && WatchModule.JobPick(job, below, taken) is { } picked)
                {
                    mine = picked;
                }

                // each write passes ifOpen with only the tick's own fields: the put merges them onto the row as stored and leaves a job a hook
                // has ended since this read as it is, inside the write itself
                if (mine is not null && mine.Pid != job.Pid && file is not null)
                {
                    _ = taken.Add(mine.Pid);
                    var start = ProcessTree.StartTime(mine.Pid);
                    Hs.StateCalls.PutJob(hier, file.Team, file.Id, WatchModule.JobPatch(file.Record, WatchModule.JobMerged(file.Record, mine.Pid, null, start, mine.Group > 1 ? mine.Group : null)), ifOpen: true);
                    live.Add(job with { Pid = mine.Pid, PidStart = start, Pgid = mine.Group > 1 ? mine.Group : null, Descendants = ProcessTree.Descendants(mine.Pid)?.Select(static n => n.Pid).ToList() ?? [] });
                    continue;
                }

                if (!WatchModule.JobOpen(job, j => WatchModule.JobRunning(j, ProcessTree, graph.Io.Processes.PidAlive), nowMs, provenBelow))
                {
                    if (file is not null)
                    {
                        var exit = job.Pid is null ? WatchModule.JobForegroundOver(job, provenBelow, nowMs) ?? "unseen" : null;
                        Hs.StateCalls.PutJob(hier, file.Team, file.Id, WatchModule.JobPatch(file.Record, WatchModule.JobEnded(file.Record, now, exit)), ifOpen: true);
                    }

                    continue;
                }

                var slot = job.Pid is null ? null : WatchModule.JobSlotFor(slotRecords.Value, job.Pid, job.PidStart);
                if (slot != job.Slot && file is not null)
                {
                    Hs.StateCalls.PutJob(hier, file.Team, file.Id, WatchModule.JobPatch(file.Record, WatchModule.JobSlotted(file.Record, slot)), ifOpen: true);
                }

                live.Add((job.Pid is { } known ? job with { Descendants = ProcessTree.Descendants(known)?.Select(static n => n.Pid).ToList() ?? [] } : job) with { Slot = slot });
            }

            return live;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"jobs of {team ?? "_default"} for {session} not reconciled ({e.Message})");
            return jobs;
        }
    }

    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _pruned = new(StringComparer.Ordinal);

    private void PruneEndedJobs(string hier)
    {
        var now = clock.UtcNow;
        lock (_pruned)
        {
            if (_pruned.TryGetValue(hier, out var last) && now - last < PruneEvery)
            {
                return;
            }

            _pruned[hier] = now;
        }

        try
        {
            Hs.StateCalls.PruneJobs(hier, (now - Ah.Hierarchy.StateCalls.IHierarchyStateCalls.JobRetention).ToUnixTimeMilliseconds());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"ended jobs of {hier} not pruned ({e.Message})");
        }
    }

    private static ProcessNode? DeeperThanWrapper(JobFact job, IReadOnlyList<ProcessNode> below, HashSet<int> taken)
    {
        var current = below.FirstOrDefault(n => n.Pid == job.Pid);
        if (current is null || !Ah.Watch.WatchModule.JobIsWrapper(current.Command))
        {
            return null;
        }

        _ = taken.Remove(current.Pid);
        var deeper = WatchModule.JobPick(job with { Pid = null }, below, taken);
        _ = taken.Add(current.Pid);
        return deeper is not null && !Ah.Watch.WatchModule.JobIsWrapper(deeper.Command) ? deeper : null;
    }

    private SlotFiles? _slots;

    private IReadOnlyList<SlotRecord> SlotRecordsNow() => (_slots ??= new SlotFiles(graph, log)).Read();

    public SlotState? SlotStateOf(string session, double nowMs) => (_slots ??= new SlotFiles(graph, log)).StateOf(session);

    public bool TeamHeld(string home, string? team) => graph.Roster.HoldOf(home, team) is not null;

    public string RecordVerdict(JsonObject? record, double nowMs, bool busy, double debounceMs) =>
        Ah.Hierarchy.HierarchyModule.RecordVerdict(record is null ? JsonValue.Null : JsonValue.FromObject(record), nowMs, busy ? "busy" : null, dispatchOpen: true, debounceMs: debounceMs);

    public JsonObject? ReadActivity(string dir, string subject)
    {
        var record = Hs.Status.ReadActivityRecord(dir, JsonValue.FromString(subject));
        return record.Kind == JsonKind.Map ? record.AsObject() : null;
    }

    public void RecordFailed(string dir, string subject, JsonObject extra) =>
        _ = Hs.Status.RecordActivityRouted(
            dir, JsonValue.FromString(subject), new JsonObject { ["activity"] = "failed", ["extra"] = extra });

    public JsonArray PeerRows() => [.. Hs.Core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "watch-event" }))];

    public void WithdrawRewakes(string address, string? session, string? keepId, double? createdBeforeMs, string reason)
    {
        if (Mail_.ListBoxes().FirstOrDefault(b => b.Address == address) is { } box)
        {
            _ = Mail_.WithdrawRewakes(box.Dir, session, keepId, createdBeforeMs, reason);
        }
    }


    // ---------------------------------------------------------------- helpers

    private static string? HierarchyDirOf(JsonObject reg) =>
        Str(reg, "team_file") is { Length: > 0 } teamFile ? NodePath.Dirname(NodePath.Dirname(teamFile)) : null;

    private static string? Str(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind == JsonKind.Text ? v.AsString() : null;

    private static JsonObject? ObjectOf(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind == JsonKind.Map ? v.AsObject() : null;

    private static IReadOnlyList<JsonObject> Objects(JsonArray array) => [.. array.Where(static v => v.Kind == JsonKind.Map).Select(static v => v.AsObject())];

    private static string Show(JsonValue v) => v.Kind == JsonKind.Text ? v.AsString() : JsonText.Stringify(v, 0);

    private IReadOnlyList<JsonObject> DispatchRows(string sessionId) =>
        Objects(Hs.Peers.LatestDispatchRows(JsonValue.FromString(sessionId)));

    /// <summary>
    /// The pools the session's exchanges live in: for each hierarchy directory of its teams, the pools of those teams' members
    /// and streams (the shared enumeration). Read once per call that goes over the session's dispatch rows.
    /// </summary>
    private List<string> SessionPools(string sessionId, TickSnapshot? snapshot = null)
    {
        var pools = new List<string>();
        if (Mail_.ReadRegistration(sessionId) is { } reg)
        {
            foreach (var group in MeshModule.RegistrationTeamFiles(reg).GroupBy(static f => NodePath.Dirname(NodePath.Dirname(f)), StringComparer.Ordinal))
            {
                var teams = group.Select(static f => Path.GetFileNameWithoutExtension(f)).Distinct(StringComparer.Ordinal).ToList();
                var enumerated = snapshot is null
                    ? Hs.Messages.PoolDirs(group.Key, teams)
                    : snapshot.PoolsOf(group.Key + "\0" + string.Join('\0', teams), () => Hs.Messages.PoolDirs(group.Key, teams));
                pools.AddRange(enumerated.Where(p => !pools.Contains(p)));
            }
        }

        return pools;
    }

    private readonly object _tickGate = new();
    private TickSnapshot? _tick;

    public void BeginTick(double nowMs)
    {
        lock (_tickGate)
        {
            _tick = new TickSnapshot(nowMs, System.Environment.TickCount64);
        }
    }

    /// <summary>The snapshot of the tick in progress, for the reads that are not handed the tick's instant; null when none was begun or it is too old to be this tick's.</summary>
    private TickSnapshot? CurrentTick()
    {
        lock (_tickGate)
        {
            return _tick is { } tick && System.Environment.TickCount64 - tick.CreatedMs <= TickSnapshot.MaxAgeMs ? tick : null;
        }
    }

    /// <summary>What <paramref name="build"/> returns, shared by every read of the tick in progress; built afresh each time when no tick is in progress.</summary>
    internal T PerTick<T>(string key, Func<T> build)
        where T : notnull =>
        CurrentTick() is { } tick ? tick.Once(key, build) : build();

    /// <summary>
    /// A work tree's latest commit time, read by <paramref name="read"/> (a <c>git log</c>) once per tick (b459 step 19: the dispatch and lanes laps ran
    /// it per lane and per stream, about 60% of the tick's time on the live-size probe). Across ticks it is held by <see cref="ServiceGraph.LaneCommits"/>
    /// while the tree's HEAD and reflog are unchanged.
    /// </summary>
    internal double? CommitPerTick(string tree, Func<double?> read) => PerTick("lane-commit\0" + tree, () => Tuple.Create(graph.LaneCommits.Of(tree, read))).Item1;

    /// <summary>The roster of a hierarchy dir, parsed once per tick; the readers only look rows up.</summary>
    private JsonArray RosterOf(string hierarchyDir) => PerTick("roster\0" + hierarchyDir, () => Hs.Roster.ReadRoster(hierarchyDir));

    /// <summary>The teams a session owns in a hierarchy dir, read once per tick.</summary>
    private IReadOnlyList<string?> TeamsOf(string hierarchyDir, string sessionId) =>
        PerTick("teams-of\0" + hierarchyDir + "\0" + sessionId, () => graph.Roster.TeamsOfSession(hierarchyDir, sessionId));

    /// <summary>A team record, read once per tick; the readers of the tick do not change it.</summary>
    private JsonValue TeamRecordOf(string hierarchyDir, string name) =>
        PerTick("team-record\0" + hierarchyDir + "\0" + name, () => graph.Roster.ReadTeamRecord(hierarchyDir, name));

    private TickSnapshot SnapshotOf(double nowMs)
    {
        var real = System.Environment.TickCount64;
        lock (_tickGate)
        {
            if (_tick is null || _tick.NowMs != nowMs || real - _tick.CreatedMs > TickSnapshot.MaxAgeMs)
            {
                _tick = new TickSnapshot(nowMs, real);
            }

            return _tick;
        }
    }

    /// <summary>How long the exchanges listed for a session are reused by the reads that do not start a tick.</summary>
    private const double IndexMaxAgeMs = 60_000;

    private readonly Dictionary<string, (IExchangeIndex Index, double BuiltMs)> _indexes = new(StringComparer.Ordinal);
    private int _indexesCounted;

    internal ExchangeMemo Memo => _exchangeMemo;

    private readonly ExchangeMemo _exchangeMemo = new(graph.Io.Files, () => clock.UtcNow.ToUnixTimeMilliseconds(), version: () => graph.Hierarchy.Core.MsgsVersion());

    /// <summary>The exchanges over <paramref name="pools"/>, kept across ticks while every file the listing read is unchanged.</summary>
    private Ah.Core.Json.JsonArray ListExchangesMemo(IReadOnlyList<string> pools) => _exchangeMemo.Get(pools, () => Hs.Core.ListExchanges(pools));

    /// <summary>
    /// The session's exchanges, listed afresh: <c>Dispatches</c> starts every tick, so it replaces the index the tick's other
    /// reads (<c>DispatchBaseMs</c>, <c>AddressesOf</c>) then look up in.
    /// </summary>
    private IExchangeIndex RefreshIndex(string sessionId, TickSnapshot? snapshot = null)
    {
        var sessionPools = SessionPools(sessionId, snapshot);
        var index = snapshot is null
            ? HierarchyModule.CreateExchangeIndex(Hs.Core, sessionPools, ListExchangesMemo)
            : snapshot.IndexOf(string.Join('\0', sessionPools), () => HierarchyModule.CreateExchangeIndex(Hs.Core, sessionPools, ListExchangesMemo));
        var built = (index, clock.UtcNow.ToUnixTimeMilliseconds());
        lock (_indexes)
        {
            foreach (var stale in _indexes.Where(kv => built.Item2 - kv.Value.BuiltMs >= IndexMaxAgeMs).Select(static kv => kv.Key).ToList())
            {
                _ = _indexes.Remove(stale);
            }

            if (Interlocked.Exchange(ref _indexesCounted, 1) == 0)
            {
                Ah.Core.CoreModule.CensusAdd(this, "cli.session_exchange_indexes", static a => a._indexes.Count);
            }

            _indexes[sessionId] = built;
        }

        return built.Item1;
    }

    /// <summary>The index of the tick in progress; one is listed when none is held or the held one is older than a tick can be.</summary>
    private IExchangeIndex CurrentIndex(string sessionId)
    {
        lock (_indexes)
        {
            if (_indexes.TryGetValue(sessionId, out var held) && clock.UtcNow.ToUnixTimeMilliseconds() - held.BuiltMs < IndexMaxAgeMs)
            {
                return held.Index;
            }
        }

        return RefreshIndex(sessionId);
    }

    private double NowMs() => clock.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>How old a dispatch row without a <c>path</c> may be and still be looked up by its request id.</summary>
    private const double PathlessRowMaxAgeMs = 24 * 3600 * 1000;

    /// <summary>
    /// The path of the request file a dispatch row names: the row's own <c>path</c>, else (a row written without one) the request
    /// file of its id in the session's pools when the row is at most a day old; null when there is none.
    /// </summary>
    private static string? RequestPathOf(JsonObject row, IExchangeIndex index, double nowMs)
    {
        if (Str(row, "path") is { Length: > 0 } own)
        {
            return own;
        }

        if (Str(row, "request_id") is not { } requestId || Str(row, "created") is not { } created
            || !Timestamps.TryParseMs(created, out var createdMs) || nowMs - createdMs > PathlessRowMaxAgeMs)
        {
            return null;
        }

        return ExchangeOf(index, string.Empty, requestId) is { } exchange && ObjectOf(exchange, "request") is { } request ? Str(request, "path") : null;
    }

    /// <summary>The exchange of a request id over the session's pools and the pool the dispatch row's own file sits in.</summary>
    private static JsonObject? ExchangeOf(IExchangeIndex index, string rowPool, string requestId) =>
        index.Find(requestId, rowPool) is { IsMap: true } exchange ? exchange.AsObject() : null;

    private JsonObject? FrontMatterOf(JsonObject exchange)
    {
        var request = ObjectOf(exchange, "request");
        var path = request is null ? null : Str(request, "path");
        if (path is null)
        {
            return null;
        }

        // b459 step 24: held while the message store's version holds, read once per tick; a failed read (no front matter) is not held
        var version = PerTick("msgs-version", () => Tuple.Create(Hs.Core.MsgsVersion())).Item1;
        return graph.FrontMatters.Of(
            version,
            path,
            () =>
            {
                var file = Hs.Core.ReadMsgFile(path);
                return file.Kind == JsonKind.Map ? ObjectOf(file.AsObject(), "fm") : null;
            },
            static fm => fm is not null);
    }

    /// <summary>The front matter of the request of an OPEN exchange found beside a dispatch row's file; null when it is closed or unknown.</summary>
    private JsonObject? OpenRequestFrontMatter(IExchangeIndex index, string rowPath, string requestId)
    {
        var exchange = ExchangeOf(index,NodePath.Dirname(NodePath.Dirname(rowPath)), requestId);
        return exchange is not null && exchange.TryGetValue("open", out var open) && open.Kind == JsonKind.Bool && open.AsBool()
            ? FrontMatterOf(exchange)
            : null;
    }
}
