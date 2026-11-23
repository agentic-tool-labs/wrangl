using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The event-driven read model of the view: lanes emit <c>{lane, key}</c> notices, one projector re-reads each key and publishes the
/// slices it feeds, and a request only reads the published map. A request never reads state and never starts a build.
/// </summary>
internal sealed class ReadModel : IReadModel
{
    private readonly IReadOnlyDictionary<string, IModelLane> _lanes;
    private readonly PathLaneTable? _paths;
    private readonly ModelOptions _options;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, long> _reconcileDueMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _reconcileStartedMs = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _passStartedMs = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _passBegun = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _reconcileCostMs = new(StringComparer.Ordinal);
    private readonly Projector _projector;
    private int _seeding;

    public ReadModel(IEnumerable<IModelLane> lanes, ModelOptions? options = null, Func<long>? nowMs = null, IProjectorHooks? hooks = null, string? epoch = null, PathLaneTable? paths = null, ComposerRegistry? composers = null)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        _options = options ?? new ModelOptions();
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
        _paths = paths;
        _lanes = lanes.ToDictionary(static l => l.Name, StringComparer.Ordinal);
        Epoch = epoch ?? Guid.NewGuid().ToString("N");
        Store = new SliceStore(Epoch);
        Bus = new ModelBus(_nowMs);
        Scheduler = new ModelScheduler(_options, _nowMs);
        Counters = new ModelCounters();
        Leases = new SubscriptionLeases(_nowMs);
        // The default clock is uptime, right for scheduling and leases. A lane stores or compares its now against event times, so it is given epoch milliseconds.
        _projector = new Projector(Bus, Store, _lanes, Scheduler, Counters, _nowMs, hooks, Tick, composers, () => IsSeeding, BeginPassTiming, nowMs is null ? () => WallClockMs() : null);
        Leases.Started += OnLeaseStarted;
    }

    public string Epoch { get; }

    internal SliceStore Store { get; }

    /// <summary>The pagers of the lanes, by list prefix; lanes register theirs when the model is assembled.</summary>
    internal ListPagerRegistry Pagers { get; } = new();

    internal ModelBus Bus { get; }

    internal ModelScheduler Scheduler { get; }

    /// <summary>The slice ids subscribed by any poll now (a lane asks before it does per-key work).</summary>
    internal SubscriptionLeases Leases { get; }

    internal ModelCounters Counters { get; }

    internal Projector Projector => _projector;

    /// <summary>The lane named <paramref name="name"/>, if the model has one. The set of lanes is fixed at construction, so any thread may ask.</summary>
    internal IModelLane? Lane(string name) => _lanes.GetValueOrDefault(name);

    /// <summary>The file-change hint source this model owns and stops with it.</summary>
    internal IDisposable? Watcher { get; set; }

    internal bool ProjectorAlive => _projector.Alive;

    internal IRecordView Records => _projector.Records;

    internal IReadOnlyList<ParkedSlice> Parked => _projector.Parked;

    /// <summary>Starts the projector thread. With <see cref="ModelOptions.SeedAtStart"/> every lane's key set is enumerated once, at the lowest priority, whether or not a client is connected.</summary>
    public void Start()
    {
        BeginSeed();
        _projector.Start();
    }

    /// <summary>
    /// Moves a pending pass's start to the moment the projector first takes the lane's key-set notice, so the pass time does not include
    /// waiting behind other lanes. Later takes of the same pass keep that start.
    /// </summary>
    private void BeginPassTiming(string lane)
    {
        if (_passStartedMs.ContainsKey(lane) && _passBegun.TryAdd(lane, true))
        {
            _passStartedMs[lane] = _nowMs();
        }
    }

    internal void BeginSeed()
    {
        if (!_options.SeedAtStart)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _seeding, 1);
        var now = _nowMs();
        foreach (var lane in _lanes.Keys)
        {
            _ = _passStartedMs.TryAdd(lane, now);
            _ = Bus.Emit(new LaneKey(lane, LaneKey.KeySet), NoticeOrigin.Reconcile, 0);
        }
    }

    /// <summary>A routed or hook-observed write happened for <paramref name="key"/> of <paramref name="lane"/>: queue a re-read.</summary>
    public void Emit(string lane, string key) => Emit(lane, key, NoticeOrigin.Routed);

    internal void Emit(string lane, string key, NoticeOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(key);
        _ = Bus.Emit(new LaneKey(lane, key), origin, Scheduler.CoalesceMs);
    }

    /// <summary>A write changed the lane in a way that names no one key (a sweep): enumerate its key set and re-read each key.</summary>
    public void EmitKeySet(string lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        _ = Bus.Emit(new LaneKey(lane, LaneKey.KeySet), NoticeOrigin.Routed, Scheduler.CoalesceMs);
    }

    /// <summary>
    /// A file-change hint for <paramref name="path"/> (created, changed, deleted or renamed): the path table turns it into a lane key and
    /// a notice, drops a non-canonical copy, or counts it as unclassified.
    /// </summary>
    internal void Hint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var lane = string.Empty;
        var key = string.Empty;
        switch (_paths?.Classify(path, out lane, out key) ?? PathClass.Unclassified)
        {
            case PathClass.Classified:
                _ = Bus.Emit(new LaneKey(lane, key), NoticeOrigin.Hint, Scheduler.CoalesceMs);
                break;
            case PathClass.NonCanonical:
                Counters.NoncanonicalHint();
                break;
            case PathClass.Unclassified:
                Counters.UnclassifiedHint();
                break;
            case PathClass.Ignored:
                break;
        }
    }

    public Task<(int Status, byte[] Body)> ListAsync(string sliceId, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sliceId);
        ArgumentNullException.ThrowIfNull(query);
        Scheduler.Touch();
        return ProjectedList(sliceId, ListEndpoint.ServeAsync(Pagers, sliceId, query, ct));
    }

    private async Task<(int Status, byte[] Body)> ProjectedList(string sliceId, Task<(int Status, byte[] Body)> served)
    {
        var (status, bytes) = await served.ConfigureAwait(false);
        if (status != 200 || !sliceId.StartsWith(OpenTeamPrefix, StringComparison.Ordinal) || JsonText.Parse(bytes) is not { IsMap: true } page)
        {
            return (status, bytes);
        }

        var body = page.AsObject();
        var (overdue, stuck) = TeamCounts(sliceId, WallClockMs());
        body["overdue"] = JsonValue.FromNumber(overdue);
        body["stuck"] = JsonValue.FromNumber(stuck);
        return (status, JsonText.ToUtf8(JsonValue.FromObject(body), 0));
    }

    /// <summary>
    /// The initial document of a view, from the published map alone: every slice of the view, the ids still pending, and whether the
    /// start seed has finished. Null when <paramref name="view"/> names no view.
    /// </summary>
    public byte[]? Manifest(string view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Scheduler.Touch();
        if (ModelViews.PrefixesOf(view) is not { } prefixes)
        {
            return null;
        }

        var state = Store.Current;
        var nowMs = (double)WallClockMs();
        var slices = new JsonArray();
        foreach (var slice in state.Slices.Values.Where(s => prefixes.Any(p => s.Id.StartsWith(p, StringComparison.Ordinal))).OrderBy(static s => s.Id, StringComparer.Ordinal))
        {
            slices.Add(JsonValue.FromObject(Projected(slice, nowMs)));
        }

        var pending = new JsonArray();
        foreach (var id in PendingIds(state).Where(id => prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal))).Order(StringComparer.Ordinal))
        {
            pending.Add(id);
        }

        return JsonText.ToUtf8(
            JsonValue.FromObject(WithFreshness(new JsonObject
            {
                ["epoch"] = Epoch,
                ["seq"] = JsonValue.FromNumber(Cursor.Pack(state.Publish, nowMs)),
                ["building"] = IsSeeding,
                ["slices"] = JsonValue.FromArray(slices),
                ["pending"] = JsonValue.FromArray(pending),
            })),
            0);
    }

    /// <summary>
    /// The long-poll: every subscribed slice whose version is above <paramref name="since"/>, with the removed ids, waiting up to
    /// <paramref name="wait"/> for the first one. A client of another epoch, or one whose <paramref name="since"/> is older than the
    /// tombstone ring, is told to reload the manifest. It reads only the published map.
    /// </summary>
    public async Task<byte[]> PollAsync(long since, string? epoch, IReadOnlyCollection<string>? subscribed, TimeSpan wait, CancellationToken ct, IReadOnlyCollection<string>? fresh = null)
    {
        Counters.Poll();
        using var poll = Scheduler.BeginPoll();
        using var lease = Leases.Hold(subscribed);
        var (publishSince, servedMs) = Cursor.Unpack(since, WallClockMs());
        if (epoch is not null && epoch != Epoch)
        {
            return DeltaJson(new ModelDelta(Epoch, Store.Current.Publish, true, [], []));
        }

        var subset = ModelViews.Subscription(subscribed);
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            var delta = Store.Since(publishSince, subset);
            if (fresh is { Count: > 0 } && !delta.Resync)
            {
                // A slice the client does not hold is sent whatever its version: one that was published while no poll named it has a
                // version at or below the client's cursor and would never reach it.
                var held = Store.Current.Slices;
                var sent = delta.Changed.Select(static s => s.Id).ToHashSet(StringComparer.Ordinal);
                var extra = fresh.Where(id => (subset is null || subset(id)) && !sent.Contains(id) && held.ContainsKey(id)).Distinct(StringComparer.Ordinal).Select(id => held[id]).ToList();
                if (extra.Count > 0)
                {
                    delta = delta with { Changed = [.. delta.Changed, .. extra] };
                }

                fresh = null;
            }

            var wallNow = (double)WallClockMs();
            double? earliest = null;
            var timed = delta.Resync ? [] : TimeDriven(subset, servedMs, wallNow, delta.Changed, out earliest);
            if (timed.Count > 0)
            {
                delta = delta with { Changed = [.. delta.Changed, .. timed] };
            }

            if (delta.Resync || delta.Changed.Count > 0 || delta.Removed.Count > 0)
            {
                return DeltaJson(delta);
            }

            var left = deadline - Environment.TickCount64;
            if (left <= 0 || ct.IsCancellationRequested)
            {
                return DeltaJson(delta);
            }

            if (earliest is { } due)
            {
                left = Math.Min(left, Math.Max(1, (long)Math.Ceiling(due - wallNow)));
            }

            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(TimeSpan.FromMilliseconds(left));
                await Store.WaitAfterAsync(delta.Publish, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                continue;
            }
            catch (OperationCanceledException)
            {
                return DeltaJson(Store.Since(publishSince, subset));
            }
        }
    }

    /// <summary>
    /// The projector's idle duties, run only when no real notice is queued: the end of the start seed, and the periodic reconcile of a
    /// lane (every max(floor, 10 x its last cost)), which runs only while a client is connected.
    /// </summary>
    internal void Tick()
    {
        var now = _nowMs();
        _ = IsSeeding;
        foreach (var lane in _lanes.Values.OfType<ILeaseLane>())
        {
            foreach (var key in lane.LapsedKeys())
            {
                _ = Bus.Emit(new LaneKey(((IModelLane)lane).Name, key), NoticeOrigin.Routed, 0);
            }
        }

        foreach (var lane in _lanes.Keys)
        {
            var drained = !Bus.AnyPendingKey(lane) && !Bus.HasKeySet(lane);
            if (drained && _reconcileStartedMs.TryGetValue(lane, out var started))
            {
                _reconcileCostMs[lane] = now - started;
                _ = _reconcileStartedMs.Remove(lane);
            }

            if (drained && _passStartedMs.TryRemove(lane, out var passStarted))
            {
                _ = _passBegun.TryRemove(lane, out _);
                Counters.FullPass(now - passStarted);
            }
        }

        if (!Scheduler.ClientConnected)
        {
            return;
        }

        foreach (var lane in _lanes.Keys)
        {
            if (_reconcileStartedMs.ContainsKey(lane))
            {
                continue;
            }

            if (_reconcileDueMs.TryGetValue(lane, out var due) && now < due)
            {
                continue;
            }

            _reconcileStartedMs[lane] = now;
            var cost = _reconcileCostMs.GetValueOrDefault(lane);
            _reconcileDueMs[lane] = now + Math.Max(_options.ReconcileFloorMs, 10 * cost);
            _ = _passStartedMs.TryAdd(lane, now);
            _ = Bus.Emit(new LaneKey(lane, LaneKey.KeySet), NoticeOrigin.Reconcile, 0);
        }
    }

    private void OnLeaseStarted(string token)
    {
        foreach (var lane in _lanes.Values.OfType<ILeaseLane>())
        {
            if (lane.KeyOfToken(token) is { } key)
            {
                _ = Bus.Emit(new LaneKey(((IModelLane)lane).Name, key), NoticeOrigin.Routed, Scheduler.CoalesceMs);
            }
        }
    }

    /// <summary>The directories changed: a token held before its directory was known, as a client's first poll can be, resolves to its lane's key now.</summary>
    internal void ResolveLiveLeases()
    {
        foreach (var token in Leases.LiveTokens())
        {
            OnLeaseStarted(token);
        }
    }

    /// <summary>Requests a reconcile of <paramref name="lane"/> now (a watcher overflow or a detected fallback), through the same bus.</summary>
    internal void ReconcileNow(string lane)
    {
        _ = Bus.Emit(new LaneKey(lane, LaneKey.KeySet), NoticeOrigin.Reconcile, 0);
        _ = _passStartedMs.TryAdd(lane, _nowMs());
    }

    public void Dispose() => _ = Close();

    /// <summary>Disposes the model and answers how long its watcher and projector join took, for the daemon's stop log.</summary>
    internal string Close()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Watcher?.Dispose();
        var watcher = clock.ElapsedMilliseconds;
        _projector.Dispose();
        var projector = clock.ElapsedMilliseconds - watcher;
        if (!_projector.Alive)
        {
            // a projector still running past its join keeps waiting on the bus
            Bus.Dispose();
        }

        return $"watcher {watcher} ms, projector join {projector} ms";
    }

    private bool IsSeeding
    {
        get
        {
            if (Volatile.Read(ref _seeding) == 0)
            {
                return false;
            }

            if (Bus.Count == 0)
            {
                _ = Interlocked.Exchange(ref _seeding, 0);
                return false;
            }

            return true;
        }
    }

    private IEnumerable<string> PendingIds(ModelState state)
    {
        foreach (var (name, lane) in _lanes)
        {
            foreach (var key in Bus.PendingKeys(name))
            {
                foreach (var id in lane.SliceIds(key))
                {
                    if (!state.Slices.ContainsKey(id))
                    {
                        yield return id;
                    }
                }
            }
        }
    }

    /// <summary>The wall clock, in epoch milliseconds, that the serve-time ETA projection reads; the slices themselves never see it.</summary>
    internal Func<long> WallClockMs { get; init; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// When the processes lane last read the process table, in epoch ms (0 for never), or null when this model has no such lane. It is lane
    /// metadata, not slice content: a team summary is served with its members' deaths unproven once this is older than the observation bound.
    /// </summary>
    internal Func<long>? ProcessesObservedAtMs { get; init; }

    private static readonly long ObservationBoundMs = Ah.Core.Time.LivenessRules.ObservationAgeSeconds * 1000L;

    /// <summary>Whether the processes reading is too old at <paramref name="nowMs"/> for a member's death to rest on it.</summary>
    private bool ObservationIsStale(double nowMs) => ProcessesObservedAtMs?.Invoke() is { } at && (at <= 0 || nowMs - at > ObservationBoundMs);

    /// <summary>
    /// <paramref name="doc"/> with what a front end's freshness mark reads: <c>data_stale</c>, whether the observation behind the slices is too old
    /// now, and <c>observed_at</c>, when it was made. An empty long-poll reply carries them too, so a reply arriving says nothing of the data by itself.
    /// </summary>
    private JsonObject WithFreshness(JsonObject doc)
    {
        doc["data_stale"] = ObservationIsStale(WallClockMs());
        doc["observed_at"] = ProcessesObservedAtMs?.Invoke() is { } at ? SummaryData.Iso(at) : JsonValue.Null;
        return doc;
    }

    /// <summary>The model's scheduling clock.</summary>
    internal long NowMs => _nowMs();

    /// <summary>The teams to retire now. A model still seeding or with an old processes reading says nothing: what it lacks reads as nobody being there.</summary>
    internal RetirePass TeamsToRetire(long nowMs, Func<string, string, bool> recordedDead) =>
        IsSeeding || ObservationIsStale(WallClockMs())
            ? RetirePass.Empty
            : TeamRetire.Find(
                Records,
                _lanes.TryGetValue(TeamLane.LaneName, out var teams) ? teams.Keys() : [],
                id => Store.Current.Slices.TryGetValue(id, out var slice) && JsonText.Parse(slice.Body) is { IsMap: true } body ? SummaryAsServed(body.AsObject(), WallClockMs()) : null,
                nowMs,
                recordedDead);

    /// <summary>
    /// The summary body as served: the fields it holds for an unobserved processes lane replace the others when the reading is stale, and the
    /// member that carries them is dropped either way.
    /// </summary>
    private JsonObject SummaryAsServed(JsonObject body, double nowMs)
    {
        if (body.Get(TeamSummaryComposer.UnobservedKey) is not { IsMap: true } alt)
        {
            return body;
        }

        var served = body.Clone();
        _ = served.Remove(TeamSummaryComposer.UnobservedKey);
        if (ObservationIsStale(nowMs))
        {
            var fields = alt.AsObject();
            foreach (var key in fields.Keys)
            {
                served[key] = fields[key];
            }
        }

        return served;
    }

    /// <summary>The watch period the check-in episodes are grouped by in the serve-time projection.</summary>
    internal double ProjectionPollMs { get; init; } = EtaProjection.DefaultPollMs;

    private byte[] DeltaJson(ModelDelta delta)
    {
        var slices = new JsonArray();
        var nowMs = (double)WallClockMs();
        foreach (var slice in delta.Changed)
        {
            slices.Add(JsonValue.FromObject(Projected(slice, nowMs)));
        }

        var removed = new JsonArray();
        foreach (var id in delta.Removed)
        {
            removed.Add(id);
        }

        return JsonText.ToUtf8(
            JsonValue.FromObject(WithFreshness(new JsonObject
            {
                ["epoch"] = delta.Epoch,
                ["seq"] = JsonValue.FromNumber(Cursor.Pack(delta.Publish, nowMs)),
                ["resync"] = delta.Resync,
                ["slices"] = JsonValue.FromArray(slices),
                ["removed"] = JsonValue.FromArray(removed),
            })),
            0);
    }

    private const string OpenTeamPrefix = "list:open:team:";
    private const string ExchangePrefix = "exchange:";
    private const string SummaryPrefix = "team-summary:";
    private const string JobsPrefix = JobsLane.LaneName + ":";

    /// <summary>
    /// The exchange slices of <paramref name="subset"/> whose state changes by time alone in <c>(servedMs, nowMs]</c> (all open ones when the
    /// cursor carries no served time), with the open-team list of each team they belong to, and <paramref name="earliest"/>, the first instant
    /// after <paramref name="nowMs"/> at which any subscribed slice changes by time.
    /// </summary>
    private List<Slice> TimeDriven(Func<string, bool>? subset, double? servedMs, double nowMs, IReadOnlyList<Slice> already, out double? earliest)
    {
        earliest = null;
        var sent = already.Select(static s => s.Id).ToHashSet(StringComparer.Ordinal);
        var due = new List<Slice>();
        var teams = new HashSet<string>(StringComparer.Ordinal);
        var all = Store.Current.Slices;
        foreach (var slice in all.Values)
        {
            if (slice.Id.StartsWith(JobsPrefix, StringComparison.Ordinal) && (subset is null || subset(slice.Id)))
            {
                var jobsBody = ParsedOf(slice);
                if (jobsBody.IsMap)
                {
                    if (JobProjection.NextChange(jobsBody.AsObject(), nowMs, ProjectionPollMs) is { } soon && (earliest is null || soon < earliest))
                    {
                        earliest = soon;
                    }

                    if ((servedMs is null || JobProjection.NextChange(jobsBody.AsObject(), servedMs.Value, ProjectionPollMs) is { } passed && passed <= nowMs) && !sent.Contains(slice.Id))
                    {
                        due.Add(slice);
                        _ = teams.Add(OpenTeamPrefix + JobsLane.OpenTeamSuffixOf(slice.Id[JobsPrefix.Length..]));
                    }
                }

                continue;
            }

            if (slice.Id.StartsWith(OpenTeamPrefix, StringComparison.Ordinal) && (subset is null || subset(slice.Id))
                && JobsOfOpenTeam(slice.Id, nowMs) is { } teamJobs)
            {
                if (JobProjection.NextChange(teamJobs, nowMs, ProjectionPollMs) is { } soonest && (earliest is null || soonest < earliest))
                {
                    earliest = soonest;
                }

                if ((servedMs is null || JobProjection.NextChange(teamJobs, servedMs.Value, ProjectionPollMs) is { } crossedAt && crossedAt <= nowMs) && !sent.Contains(slice.Id))
                {
                    _ = teams.Add(slice.Id);
                }

                continue;
            }

            if (slice.Id.StartsWith(SummaryPrefix, StringComparison.Ordinal) && (subset is null || subset(slice.Id))
                && ParsedOf(slice) is { IsMap: true } composed && LivenessProjection.NextChange(composed.AsObject()) is { } becomesNotSeen)
            {
                if (becomesNotSeen > nowMs && (earliest is null || becomesNotSeen < earliest))
                {
                    earliest = becomesNotSeen;
                }

                var passed = servedMs is null ? becomesNotSeen <= nowMs : becomesNotSeen > servedMs.Value && becomesNotSeen <= nowMs;
                if (passed && !sent.Contains(slice.Id) && !due.Contains(slice))
                {
                    due.Add(slice);
                }
            }

            if (slice.Id.StartsWith(SummaryPrefix, StringComparison.Ordinal) && ProcessesObservedAtMs is { } observed && (subset is null || subset(slice.Id)))
            {
                var summary = ParsedOf(slice);
                if (summary.IsMap && summary.AsObject().Get(TeamSummaryComposer.UnobservedKey) is { IsMap: true } && observed() is > 0 and var at)
                {
                    var goesStale = at + ObservationBoundMs + 1;
                    if (goesStale > nowMs && (earliest is null || goesStale < earliest))
                    {
                        earliest = goesStale;
                    }

                    var crossed = servedMs is null ? ObservationIsStale(nowMs) : goesStale > servedMs.Value && goesStale <= nowMs;
                    if (crossed && !sent.Contains(slice.Id) && !due.Contains(slice))
                    {
                        due.Add(slice);
                    }
                }

                continue;
            }

            if (!slice.Id.StartsWith(ExchangePrefix, StringComparison.Ordinal) || (subset is not null && !subset(slice.Id)))
            {
                continue;
            }

            var parsed = ParsedOf(slice);
            if (!parsed.IsMap || parsed.AsObject().Get("eta_facts") is not { IsMap: true })
            {
                continue;
            }

            var body = parsed.AsObject();
            if (NextTimeChange(body, nowMs) is { } next && (earliest is null || next < earliest))
            {
                earliest = next;
            }

            var changedByTime = servedMs is null || NextTimeChange(body, servedMs.Value) is { } fell && fell <= nowMs;
            if (changedByTime && !sent.Contains(slice.Id))
            {
                due.Add(slice);
                _ = teams.Add(OpenTeamListId(body));
            }
        }

        foreach (var listId in teams)
        {
            if (all.TryGetValue(listId, out var list) && (subset is null || subset(listId)) && !sent.Contains(listId) && !due.Contains(list))
            {
                due.Add(list);
            }

            var summaryId = SummaryPrefix + listId[OpenTeamPrefix.Length..];
            if (all.TryGetValue(summaryId, out var summary) && (subset is null || subset(summaryId)) && !sent.Contains(summaryId))
            {
                due.Add(summary);
            }
        }

        return due;
    }

    /// <summary>The first instant after <paramref name="nowMs"/> at which the dispatch a body describes changes by time alone: its eta state, or the moment its task would become stuck.</summary>
    private double? NextTimeChange(JsonObject body, double nowMs)
    {
        var eta = EtaProjection.NextChange(body, nowMs, ProjectionPollMs);
        var stuck = StuckProjection.StuckAt(body) is { } at && at > nowMs ? at : (double?)null;
        return eta is null ? stuck : stuck is null ? eta : Math.Min(eta.Value, stuck.Value);
    }

    private static string OpenTeamListId(JsonObject exchange) =>
        OpenTeamPrefix + (exchange.Str("dir_id") ?? string.Empty) + "/" + TeamLane.IdName(exchange.Str("team"));

    /// <summary>
    /// Adds <c>eta_state</c>, <c>due_at</c> and (only when overdue) <c>overdue_by</c> to each entry of <paramref name="open"/> whose exchange slice
    /// carries eta facts, from the same <see cref="EtaProjection.Of"/> the exchange slice itself is served with, and returns how many entries are overdue.
    /// </summary>
    private int ProjectEntries(JsonArray open, double nowMs, out List<JsonObject> stuck)
    {
        var overdue = 0;
        stuck = [];
        foreach (var entry in open)
        {
            if (!entry.IsMap || entry.Member("id") is not { IsText: true } id
                || !Store.Current.Slices.TryGetValue(id.AsString(), out var exchange)
                || ParsedOf(exchange) is not { IsMap: true } parsed
                || EtaProjection.Of(parsed.AsObject(), nowMs, ProjectionPollMs) is not { } fields)
            {
                continue;
            }

            var state = fields.Str("state");
            var target = entry.AsObject();
            target["eta_state"] = state ?? string.Empty;
            target["due_at"] = fields["due_at"];
            if (state == "overdue")
            {
                target["overdue_by"] = fields["overdue_by"];
                overdue++;
            }

            if (StuckProjection.Of(parsed.AsObject(), AssigneeState(AssigneeOf(id.AsString())), nowMs) is { } stuckFields)
            {
                foreach (var key in stuckFields.Keys)
                {
                    target[key] = stuckFields[key];
                }

                if (stuckFields.Get("stuck") is { IsBool: true } flag && flag.AsBool())
                {
                    stuck.Add(target);
                }
            }
        }

        return overdue;
    }

    /// <summary>The <c>audience</c> of an alert that is the Orchestrator's to handle, not the user's: a client leaves it out of the user's attention count.</summary>
    internal const string OrchestratorAudience = "orchestrator";

    /// <summary>
    /// The team summary as a response carries it: <c>stuck</c> is the number of the team's open tasks that are stuck now, not part of <c>needs_you</c>, and each
    /// one is an alert of kind <c>stuck</c> with <c>audience</c> <see cref="OrchestratorAudience"/> after the composed alerts, oldest first, within <see cref="TeamSummaryComposer.AlertCap"/>. The alert's
    /// <c>text</c> states the idle time as of this response and is not updated afterwards; a client shows the age from the alert's <c>since</c>.
    /// <c>state</c> is recomputed here from the same counts. The list-level <c>overdue</c> and <c>stuck</c> counts cover the stored open list, which holds at most
    /// <see cref="OpenExchangeListComposer.MaxOpen"/> exchanges, newest first.
    /// </summary>
    private void ProjectSummary(JsonObject body, string openListId, double nowMs)
    {
        LivenessProjection.Project(body, nowMs);
        var stuck = new List<JsonObject>();
        if (Store.Current.Slices.TryGetValue(openListId, out var list) && JsonText.Parse(list.Body).Member("open") is { IsArray: true } open)
        {
            _ = ProjectEntries(open.AsArray(), nowMs, out stuck);
        }

        // a stuck member is the Orchestrator's to handle: counted in stuck_tasks, never in the user's needs_you. The summary's stuck is the Stuck column's items.
        body["stuck_tasks"] = JsonValue.FromNumber(stuck.Count);
        var needsYou = body.Get("needs_you") is { IsNumber: true } n ? n.AsNumber() : 0;
        body["state"] = SummaryData.TeamState(
            body.Get("member_states") is { IsMap: true } served && served.AsObject().Keys.Count > 0,
            body.Get("running") is { IsBool: true } running && running.AsBool(),
            (long)needsYou,
            body.Get("working") is { IsNumber: true } working ? (long)working.AsNumber() : 0);
        SummaryData.ApplyStatus(body);
        if (stuck.Count == 0 || body.ContainsKey("closed_at"))
        {
            return;
        }

        var alerts = body.Get("alerts") is { IsArray: true } held ? held.AsArray() : new JsonArray();
        var truncated = body.Get("alerts_truncated") is { IsBool: true } flag && flag.AsBool();
        foreach (var entry in stuck.OrderBy(static e => e.Str("stuck_since"), StringComparer.Ordinal))
        {
            if (alerts.Count >= TeamSummaryComposer.AlertCap)
            {
                truncated = true;
                break;
            }

            var exchange = entry.Str("id") is { } id && Store.Current.Slices.TryGetValue(id, out var slice) ? ParsedOf(slice) : default;
            var title = exchange.IsMap ? exchange.AsObject().Str("title") ?? exchange.AsObject().Str("slug") : null;
            var idle = (long)(entry.Get("idle_for") is { IsNumber: true } secs ? secs.AsNumber() : 0);
            var text = "No word on this exchange for " + (idle >= 3600 ? idle / 3600 + " h" : idle / 60 + " min") + (title is null ? string.Empty : " · " + title);
            var card = SummaryData.AlertCard("stuck", entry.Str("to_member"), text);
            alerts.Add(JsonValue.FromObject(new JsonObject
            {
                ["title"] = card.Title,
                ["detail"] = card.Detail,
                ["kind"] = "stuck",
                ["audience"] = OrchestratorAudience,
                ["exchange"] = entry.Str("id") ?? string.Empty,
                ["member"] = entry.Str("to_member") ?? string.Empty,
                ["since"] = entry.Str("stuck_since") ?? string.Empty,
                ["text"] = text,
            }));
        }

        body["alerts"] = JsonValue.FromArray(alerts);
        if (truncated)
        {
            body["alerts_truncated"] = true;
        }
    }

    /// <summary>Each published slice's parsed body at its version (b459 step 11); read-only for every holder.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Version, byte[] Bytes, JsonValue Body)> _parsed = new(StringComparer.Ordinal);

    /// <summary>The slice's parsed body, held while the slice keeps its version and bytes. The caller reads it and never writes into it.</summary>
    private JsonValue ParsedOf(Slice slice)
    {
        if (_parsed.TryGetValue(slice.Id, out var held) && held.Version == slice.Version && ReferenceEquals(held.Bytes, slice.Body))
        {
            return held.Body;
        }

        var body = JsonText.Parse(slice.Body);
        _parsed[slice.Id] = (slice.Version, slice.Body, body);
        var published = Store.Current.Slices;
        if (_parsed.Count > published.Count + 64)
        {
            foreach (var id in _parsed.Keys.Where(id => !published.ContainsKey(id)))
            {
                _ = _parsed.TryRemove(id, out _);
            }
        }

        return body;
    }

    /// <summary>Drops every held parsed body (the on-demand rebuild).</summary>
    internal void ForgetParsed() => _parsed.Clear();

    private sealed record AssigneeIndex(ModelState State, Dictionary<string, string> Members);

    private volatile AssigneeIndex? _assignees;

    /// <summary>
    /// The member slice id an open exchange is addressed to, as the team's open list resolved it (a request addressed to a role has no name of its own);
    /// null when no open list names it. Both the list entries and the exchange bodies read their assignee here.
    /// </summary>
    private string? AssigneeOf(string exchangeSliceId)
    {
        var state = Store.Current;
        var held = _assignees;
        if (held is null || !ReferenceEquals(held.State, state))
        {
            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var list in state.Slices.Values.Where(static s => s.Id.StartsWith(OpenTeamPrefix, StringComparison.Ordinal)))
            {
                if (ParsedOf(list).Member("open") is { IsArray: true } open)
                {
                    foreach (var entry in open.AsArray())
                    {
                        if (entry.Member("id") is { IsText: true } id && entry.Member("to_member") is { IsText: true } to)
                        {
                            members[id.AsString()] = to.AsString();
                        }
                    }
                }
            }

            held = new AssigneeIndex(state, members);
            _assignees = held;
        }

        return held.Members.GetValueOrDefault(exchangeSliceId);
    }

    /// <summary>
    /// The served state of a member, from its team's summary slice, where <c>gone</c> is the shared verdict's proven death and nothing else:
    /// a member whose liveness is not <c>proven_dead</c> never reads as gone here. Only a member whose state is <c>working</c> or <c>idle</c>
    /// can have a stuck task.
    /// </summary>
    private string? AssigneeState(string? memberId) =>
        StuckProjection.SummaryOf(memberId) is { } summaryId && Store.Current.Slices.TryGetValue(summaryId, out var summary)
        && ParsedOf(summary) is { IsMap: true } parsed
        && SummaryAsServed(parsed.AsObject(), WallClockMs()).Get("member_states")?.Member(memberId!) is { IsMap: true } served
            ? served.Member(TeamSummaryComposer.StuckAsUnknownKey) is { IsBool: true } ? "unknown" : StuckProjection.ServedState(served)
            : null;

    private (int Overdue, int Stuck) TeamCounts(string openListId, double nowMs)
    {
        if (!Store.Current.Slices.TryGetValue(openListId, out var list) || JsonText.Parse(list.Body).Member("open") is not { IsArray: true } open)
        {
            return (OverdueJobs(openListId, nowMs), 0);
        }

        var overdue = ProjectEntries(open.AsArray(), nowMs, out var stuck);
        return (overdue + OverdueJobs(openListId, nowMs), stuck.Count);
    }

    /// <summary>
    /// How many of the team's jobs are overdue at <paramref name="nowMs"/>, counted from the job records, so that the team's <c>overdue</c> count
    /// covers jobs as well as dispatches whether or not any client holds the jobs slice. The stored open list stays clock-free.
    /// </summary>
    private int OverdueJobs(string openListId, double nowMs) =>
        JobsOfOpenTeam(openListId, nowMs) is { } jobs ? JobProjection.OverdueCount(jobs, nowMs, ProjectionPollMs) : 0;

    /// <summary>
    /// A live member that owns an overdue job is served as <c>blocked-on-job</c>, from the job records at serve time. The stored summary and
    /// the state the stuck rule reads are left alone, so a dispatch's stuck verdict does not change with it.
    /// </summary>
    private void MarkBlockedOnJob(JsonObject body, string openListId, double nowMs)
    {
        if (body.Get("member_states") is not { IsMap: true } states || JobsOfOpenTeam(openListId, nowMs) is not { } jobs)
        {
            return;
        }

        var owners = JobProjection.OverdueOwners(jobs, nowMs, ProjectionPollMs);
        if (owners.Count == 0)
        {
            return;
        }

        foreach (var key in states.AsObject().Keys.ToList())
        {
            if (states.AsObject().Get(key) is { IsMap: true } entry && entry.AsObject().Str("liveness") == "live" && !WatchModule.BlockedWinsOverJob(entry.AsObject().Str("state")) && owners.Contains(key[(key.LastIndexOf('/') + 1)..]))
            {
                entry.AsObject()["state"] = "blocked-on-job";
            }
        }
    }

    /// <summary>The team's job rows read from the records now, or null when the view has no jobs source, the team's directory is unknown, or the team has no job to list.</summary>
    private JsonObject? JobsOfOpenTeam(string openListId, double nowMs)
    {
        lock (_jobsReadGate)
        {
            if (_jobsReadAtMs != nowMs)
            {
                _jobsRead.Clear();
                _jobsReadAtMs = nowMs;
            }

            if (_jobsRead.TryGetValue(openListId, out var held))
            {
                return held;
            }
        }

        var read = Lane(JobsLane.LaneName) is JobsLane lane && lane.BodyOfOpenTeam(openListId[OpenTeamPrefix.Length..], nowMs) is { } body && body.Get("rows") is { IsArray: true } rows && rows.AsArray().Count > 0
            ? body
            : null;
        lock (_jobsReadGate)
        {
            if (_jobsReadAtMs == nowMs)
            {
                _jobsRead[openListId] = read;
            }
        }

        return read;
    }

    private readonly object _jobsReadGate = new();
    private readonly Dictionary<string, JsonObject?> _jobsRead = new(StringComparer.Ordinal);
    private double _jobsReadAtMs = double.NaN;

    /// <summary>
    /// The slice as a response carries it: an open dispatch's body gains <c>state</c>, <c>due_at</c>, <c>overdue_by</c> and <c>next_nudge</c>, and a
    /// team's open list gains the team's <c>overdue</c> count, all as of <paramref name="nowMs"/>. The stored slice is not touched.
    /// </summary>
    private JsonObject Projected(Slice slice, double nowMs)
    {
        // b459 step 11: the one table of the slices a response writes into; a slice it does not name is served the shared held body, so a
        // projection that writes is only possible by adding its row here, which also makes its body parsed afresh
        var project = Projections.FirstOrDefault(p => slice.Id.StartsWith(p.Prefix, StringComparison.Ordinal)).Project;
        if (project is null)
        {
            return SliceStore.ToJson(slice, ParsedOf(slice));
        }

        var json = SliceStore.ToJson(slice);
        if (json.Get("body") is { IsMap: true } body)
        {
            project(this, json, body.AsObject(), slice, nowMs);
        }

        return json;
    }

    private static readonly (string Prefix, Action<ReadModel, JsonObject, JsonObject, Slice, double> Project)[] Projections =
    [
        (ExchangePrefix, static (m, _, body, slice, nowMs) => m.ProjectExchange(body, slice, nowMs)),
        (OpenTeamPrefix, static (m, _, body, slice, nowMs) => m.ProjectOpenTeam(body, slice, nowMs)),
        (SummaryPrefix, static (m, json, body, slice, nowMs) =>
        {
            var served = m.SummaryAsServed(body, nowMs);
            if (!ReferenceEquals(served, body))
            {
                json["body"] = JsonValue.FromObject(served);
            }

            m.ProjectSummary(served, OpenTeamPrefix + slice.Id[SummaryPrefix.Length..], nowMs);
            m.MarkBlockedOnJob(served, OpenTeamPrefix + slice.Id[SummaryPrefix.Length..], nowMs);
        }),
        (JobsPrefix, static (m, _, body, _, nowMs) => JobProjection.Project(body, nowMs, m.ProjectionPollMs)),
        ("activity:", static (_, _, body, _, nowMs) => WorkingShare.Project(body, (long)nowMs)),
    ];

    private void ProjectExchange(JsonObject body, Slice slice, double nowMs)
    {
        if (EtaProjection.Of(body, nowMs, ProjectionPollMs) is not { } fields)
        {
            return;
        }

        foreach (var key in fields.Keys)
        {
            body[key] = fields[key];
        }

        if (StuckProjection.Of(body, AssigneeState(AssigneeOf(slice.Id)), nowMs) is { } stuckFields)
        {
            foreach (var key in stuckFields.Keys)
            {
                body[key] = stuckFields[key];
            }
        }
    }

    private void ProjectOpenTeam(JsonObject body, Slice slice, double nowMs)
    {
        var stuckCount = 0;
        var overdue = 0;
        if (body.Get("open") is { IsArray: true } open)
        {
            overdue = ProjectEntries(open.AsArray(), nowMs, out var stuck);
            stuckCount = stuck.Count;
        }

        body["overdue"] = JsonValue.FromNumber(overdue + OverdueJobs(slice.Id, nowMs));
        body["stuck"] = JsonValue.FromNumber(stuckCount);
    }
}

/// <summary>
/// The poll cursor, a whole number a JSON number carries exactly: the publish counter in the low 32 bits and, above them, the server time
/// of the response that produced it in whole seconds modulo 2,097,151, plus one. A value of 0 there (a publish counter alone) means no served
/// time is known. The time is read back against the current time, so a cursor is good for about 24 days; an older one is resynced by
/// the publish counter long before.
/// </summary>
internal static class Cursor
{
    private const long LowMask = 0xFFFFFFFFL;
    private const long Modulus = 2_097_151;

    public static long Pack(long publish, double servedMs) => ((((long)(servedMs / 1000)) % Modulus + 1) << 32) | (publish & LowMask);

    public static (long Publish, double? ServedMs) Unpack(long cursor, double nowMs)
    {
        var tag = cursor >> 32;
        if (tag == 0)
        {
            return (cursor & LowMask, null);
        }

        var nowSeconds = (long)(nowMs / 1000);
        var age = (((nowSeconds - (tag - 1)) % Modulus) + Modulus) % Modulus;
        return (cursor & LowMask, (nowSeconds - age) * 1000d);
    }
}

/// <summary>The view names a manifest can be asked for, and the slice id prefixes each holds.</summary>
internal static class ModelViews
{
    public static IReadOnlyList<string>? PrefixesOf(string view) => view switch
    {
        "members" => ["activity:"],
        "tree" => ["team:", "member:", "topology:", "worktree:", "list:teams"],
        "exchanges" => ["exchange:", "list:exchanges:", "list:feed:", "list:open:"],
        "status" => ["status:"],
        "processes" => ["processes:"],
        "timeline" => ["timeline:"],
        "summary" => ["list:teams", "team-summary:"],
        _ => null,
    };

    /// <summary>
    /// The filter a <c>sub</c> list stands for: each token is a view name (<c>view:members</c>, every slice of that view) or a slice id.
    /// An unknown view or an id that does not exist yet matches nothing now and may match later. Null when there is no list.
    /// </summary>
    public static Func<string, bool>? Subscription(IReadOnlyCollection<string>? tokens)
    {
        if (tokens is null)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new List<string>();
        foreach (var token in tokens)
        {
            if (token.StartsWith("view:", StringComparison.Ordinal))
            {
                if (PrefixesOf(token[5..]) is { } p)
                {
                    prefixes.AddRange(p);
                }
            }
            else
            {
                _ = ids.Add(token);
            }
        }

        // asked once per published slice on every poll wake-up: a loop, not Any, so a call allocates nothing
        return id =>
        {
            if (ids.Contains(id))
            {
                return true;
            }

            foreach (var prefix in prefixes)
            {
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        };
    }
}
