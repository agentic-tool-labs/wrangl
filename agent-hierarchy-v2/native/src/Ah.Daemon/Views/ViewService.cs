using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Watch;

namespace Ah.Daemon;

/// <summary>What the view and status routes read.</summary>
/// <param name="Source">The reads behind a view build.</param>
/// <param name="Revs">Reads watched status documents with their revisions.</param>
/// <param name="StatusDirs">The watched status dirs; the watch module's backstop job keeps it current.</param>
/// <param name="Clock">The wall clock.</param>
/// <param name="HerdrAgents">
/// Reads <c>herdr agent list</c> within the given timeout and throws when herdr cannot answer. It is called off the loop, at most
/// once at a time and once every 2 s.
/// </param>
/// <param name="BuildLog">Called once per view build; a test counts builds with it.</param>
/// <param name="BuildDelay">A test's stand-in for a hierarchy that is slow to read: each build takes at least this long.</param>
/// <param name="InspectTimers">Answers <c>GET /v1/inspect/timers</c> for an optional session; null leaves the route unregistered.</param>
/// <param name="InspectEvents">Answers <c>GET /v1/inspect/events</c> for an optional box address; null leaves the route unregistered.</param>
/// <param name="EventFeed">A test's stand-in for finding new events, given the time; null finds them by comparing reads of <paramref name="Source"/>.</param>
/// <param name="Log">The daemon's log line writer, or null.</param>
/// <param name="Timing">Whether each view request logs one <c>view-timing</c> line; set from <c>AH_VIEW_TIMING=1</c>.</param>
/// <param name="Model">The event-driven read model behind <c>GET /v1/view/{name}</c> and the <c>sub</c> form of the events long-poll; null leaves both off.</param>
/// <param name="ReadModelStats">The read model's counters for <c>GET /v1/read-model</c> (the <c>parked</c> member is the parked list); null, or a null answer, is a 404.</param>
/// <param name="SubscribeWrites">Given the action to run on each write through this daemon; null when nothing reports writes.</param>
/// <param name="Rebuild">Drops the held derived state for <c>POST /v1/read-model/rebuild</c>; null registers no such route.</param>
public sealed record ViewServiceDependencies(
    IViewSource Source,
    IStatusRevs Revs,
    IStatusDirs StatusDirs,
    IClock Clock,
    Func<TimeSpan, CancellationToken, Task<JsonArray>> HerdrAgents,
    Action? BuildLog = null,
    TimeSpan? BuildDelay = null,
    Func<string?, JsonObject>? InspectTimers = null,
    Func<string?, JsonObject>? InspectEvents = null,
    Func<double, ViewEventBatch>? EventFeed = null,
    Action<string>? Log = null,
    bool Timing = false,
    IReadModel? Model = null,
    Func<JsonObject?>? ReadModelStats = null,
    Action<Action>? SubscribeWrites = null,
    Action? Rebuild = null);

/// <summary>The <c>/v1/view</c> and <c>/v1/status</c> routes.</summary>
internal sealed class ViewService
{
    /// <summary>The events are read again at most this often.</summary>
    internal const int ShareMs = 900;

    /// <summary>
    /// How long, from the moment the model of the hierarchy starts reading it, the model is kept and every page is derived from it. The change
    /// signal of the events (<c>ViewEvents.Stamp</c>) covers the status revisions, the team files and the box dirs only, not the message
    /// pools, the progress logs, the activity files or git, which the pages read too, so a change is found by this age and not by a stamp.
    /// </summary>
    internal const int EpochMs = 3000;

    /// <summary>The longest an epoch stretches for a slow build.</summary>
    internal const int MaxEpochMs = 30_000;

    /// <summary>An epoch lasts at least this many times as long as the build it took.</summary>
    internal const int BuildShare = 5;

    /// <summary>The most pages one epoch keeps; a page past it is built and not kept.</summary>
    internal const int MaxShared = 64;

    /// <summary>A long-polling status client is answered within this of a change.</summary>
    internal const int PollMs = 200;

    /// <summary>A failing events read is logged at most this often.</summary>
    internal const int RefreshFailureLogMs = 60_000;

    /// <summary>A released view waiter pauses this long before it is answered.</summary>
    internal const int ReleasedPauseMs = 1000;

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private sealed class Shared(Task<ViewAnswer> task)
    {
        internal Task<ViewAnswer> Task { get; } = task;
    }

    /// <summary>A model of the hierarchy and whether it was read with herdr's real reading.</summary>
    private sealed record EpochModel(IViewModel Model, bool RealHerdr);

    /// <summary>
    /// One model of the hierarchy and the pages derived from it. The model reads the hierarchy from <see cref="Now"/> on, and neither
    /// it nor a page derived from it is served to a new request <see cref="EpochMs"/> after that. A page still being built when the
    /// epoch ends is waited for by a request for the same path in the next epoch (<see cref="InFlight"/>), so a slow page is never built
    /// twice at once, but it is not kept once it lands.
    /// </summary>
    private sealed class Epoch(double now, IReadOnlyList<string> dirs)
    {
        internal double Now { get; } = now;

        internal IReadOnlyList<string> Dirs { get; } = dirs;

        internal Dictionary<string, Shared> Pages { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, Shared> InFlight { get; } = new(StringComparer.Ordinal);

        internal EpochModel? Current;

        /// <summary>The longest any page of this epoch took to build. Guarded by the service's gate.</summary>
        internal long SlowestMs;

        internal Task<HerdrReading>? Snapshot;
    }

    /// <summary>Wraps a builder of one page at a time as a model that shares nothing between pages.</summary>
    private sealed class FuncModel(Func<string, double, IReadOnlyList<string>, Func<HerdrReading>, ViewAnswer> builder, double now, IReadOnlyList<string> dirs, Func<HerdrReading> herdr) : IViewModel
    {
        private volatile bool _used;

        public bool UsedHerdr => _used;

        public ViewAnswer Build(string path) => builder(path, now, dirs, () =>
        {
            _used = true;
            return herdr();
        });
    }

    private readonly ViewServiceDependencies _d;
    private readonly Func<double, IReadOnlyList<string>, Func<HerdrReading>, IViewModel> _models;
    private readonly HerdrCache _herdr;
    private readonly ViewWaiters _waiters = new();
    private readonly object _gate = new();
    private readonly Lazy<ViewEventLog> _events;
    private long _epochMs = EpochMs;
    private long _writeAt = long.MinValue;
    private bool _herdrWanted;
    private Epoch? _epoch;
    private Task? _refreshing;
    private EventCursor? _cursor;
    private long _snapshotAt = -1;

    internal ViewService(
        ViewServiceDependencies dependencies,
        Func<string, double, IReadOnlyList<string>, Func<HerdrReading>, ViewAnswer>? builder = null,
        Func<double, IReadOnlyList<string>, Func<HerdrReading>, IViewModel>? models = null)
    {
        _d = dependencies;
        dependencies.SubscribeWrites?.Invoke(WriteHappened);
        _models = models
            ?? (builder is null
                ? (now, dirs, herdr) => WatchModule.CreateViewModel(dependencies.Source, now, dirs, herdr)
                : (now, dirs, herdr) => new FuncModel(builder, now, dirs, herdr));
        _events = new Lazy<ViewEventLog>(() => new ViewEventLog(NowMs().ToString(CultureInfo.InvariantCulture)));
        _herdr = new HerdrCache(dependencies.HerdrAgents, NowMs);
    }

    internal int Waiting => _waiters.Count;

    private long NowMs() => _d.Clock.UtcNow.ToUnixTimeMilliseconds();

    internal static RouteResponse Json(int status, JsonObject body)
    {
        if (WatchModule.CurrentViewTiming is not { } timing)
        {
            return new(status, NoHeaders, JsonText.ToUtf8(body, 0));
        }

        var start = Stopwatch.GetTimestamp();
        var bytes = JsonText.ToUtf8(body, 0);
        timing.AddJson(Stopwatch.GetTimestamp() - start);
        return new(status, NoHeaders, bytes);
    }

    internal static RouteResponse Error(int status, string code, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    internal static RouteResponse BadRequest(string message) => Error(400, "bad_request", message);

    /// <summary>Sleeps; false when the request is over (the client left or the daemon drains).</summary>
    private static async Task<bool> Pause(int ms, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(ms, cancellation).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- /v1/view

    internal async Task<RouteResponse> ViewAsync(RouteRequest request, CancellationToken cancellation)
    {
        var q = QueryString.Parse(request.Query);
        var path = q.TryGetValue("path", out var p) ? p : "/";
        if (!path.StartsWith('/') || path.Length > 1024)
        {
            return BadRequest("path must be a view path starting with /");
        }

        if (!LongPoll.TryParseWait(q.GetValueOrDefault("wait"), out var wait))
        {
            return BadRequest(LongPoll.WaitMessage);
        }

        var since = q.GetValueOrDefault("since");
        if (path == EventsPath && q.ContainsKey("sub") && _d.Model is { } model)
        {
            return await SliceEventsAsync(model, q, wait, cancellation).ConfigureAwait(false);
        }

        if (wait > 0 && since is null)
        {
            return BadRequest("wait needs since: without a cursor the current view is always new, so a long-poll would return at once");
        }

        if (path == EventsPath)
        {
            return await EventsAsync(since, wait, cancellation).ConfigureAwait(false);
        }

        var timing = _d.Timing ? WatchModule.OpenViewTiming(path) : null;
        var end = NowMs() + (wait * 1000L);
        var waiter = wait > 0 ? _waiters.Add() : null;
        try
        {
            while (true)
            {
                var built = await BuildSharedAsync(path).ConfigureAwait(false);
                if (built.Status != 200)
                {
                    return Json(404, built.Body);
                }

                var current = built.Body;
                var rev = current.Str("rev");
                if (since is null || rev != since)
                {
                    return Json(200, current);
                }

                var unchanged = new JsonObject
                {
                    ["view_schema"] = current.TryGetValue("view_schema", out var schema) ? schema : JsonValue.Null,
                    ["unchanged"] = true,
                    ["rev"] = rev ?? string.Empty,
                };
                if (wait == 0 || NowMs() >= end || cancellation.IsCancellationRequested)
                {
                    return Json(200, unchanged);
                }

                if (waiter!.Released)
                {
                    _ = await Pause(ReleasedPauseMs, cancellation).ConfigureAwait(false);
                    return Json(200, unchanged);
                }

                if (!await Pause(PollMs, cancellation).ConfigureAwait(false))
                {
                    return Json(200, unchanged);
                }
            }
        }
        finally
        {
            if (waiter is not null)
            {
                _waiters.Remove(waiter);
            }

            if (timing is not null)
            {
                _d.Log?.Invoke(timing.Line());
            }
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>
    /// <c>GET /v1/view/{name}</c>: the manifest of a view, from the read model's published map alone. An unknown name is a 404, and so is
    /// a daemon without a read model.
    /// </summary>
    internal Task<RouteResponse> ManifestAsync(RouteRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.PathParameters.GetValueOrDefault("name");
        return Task.FromResult(
            _d.Model is { } model && name is not null && model.Manifest(name) is { } bytes
                ? new RouteResponse(200, NoHeaders, bytes)
                : Json(404, new JsonObject { ["error"] = "not_found" }));
    }

    private static readonly IReadOnlyDictionary<string, string> NoStore = new Dictionary<string, string> { ["Cache-Control"] = "no-store" };

    /// <summary>
    /// <c>GET /v1/actions</c> and <c>GET /v1/actions/{*id}</c>: the actions of the root, or of the member whose slice id is
    /// <c>member:&lt;dirId&gt;/&lt;team&gt;/&lt;member&gt;</c>, as <c>{node:{kind, path}, actions}</c>. It is the <c>body</c> of the view builder's own
    /// <c>actions</c> document, the one the terminal viewer shows; nothing is composed here and nothing is published. An id that is not of that
    /// form is a 400 <c>bad_member</c>, and one that names no current member is a 404 <c>no_node</c>.
    /// </summary>
    internal async Task<RouteResponse> ActionsAsync(RouteRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = "/actions";
        if (request.PathParameters.GetValueOrDefault("id") is { } id)
        {
            var parts = id.StartsWith("member:", StringComparison.Ordinal) ? id["member:".Length..].Split('/') : [];
            if (parts.Length != 3 || parts.Any(static p => p.Length == 0))
            {
                return Refusal(400, "bad_member");
            }

            path += "/member/" + string.Join('/', parts.Select(Uri.EscapeDataString));
        }

        cancellation.ThrowIfCancellationRequested();
        var built = await BuildSharedAsync(path).ConfigureAwait(false);
        if (built.Status == 200 && built.Body.TryGetValue("body", out var body) && body.IsMap)
        {
            return new RouteResponse(200, NoStore, JsonText.ToUtf8(body, 0));
        }

        return built.Status is 200 or 404 ? Refusal(404, "no_node") : new RouteResponse(built.Status, NoStore, JsonText.ToUtf8(built.Body, 0));
    }

    private static RouteResponse Refusal(int status, string error) => new(status, NoStore, JsonText.ToUtf8(new JsonObject { ["error"] = error }, 0));

    /// <summary>
    /// <c>GET /v1/list/{*id}</c>: one page of a list, from the read model's pagers. An unknown list prefix is a 404, and so is a daemon
    /// without a read model.
    /// </summary>
    internal async Task<RouteResponse> ListAsync(RouteRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_d.Model is not { } model || request.PathParameters.GetValueOrDefault("id") is not { } id)
        {
            return Json(404, new JsonObject { ["error"] = "not_found" });
        }

        if (RepeatedKey(request.Query) is { } repeated)
        {
            return new RouteResponse(400, NoStore, JsonText.ToUtf8(new JsonObject { ["error"] = "bad_filter", ["key"] = repeated }, 0));
        }

        var (status, body) = await model.ListAsync(id, QueryString.Parse(request.Query), cancellation).ConfigureAwait(false);
        return new RouteResponse(status, NoStore, body);
    }

    /// <summary>The first query key that appears more than once in <paramref name="query"/>, or null; the parsed dictionary keeps one value per key, so repeats are only visible here.</summary>
    private static string? RepeatedKey(string query)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            if (!seen.Add(key))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>GET /v1/read-model</c>: <c>{counters, parked, as_of}</c>, the read model's health counters read when the request arrives. Nothing
    /// here is a slice, so reading it publishes nothing. A daemon without a read model answers 404 <c>no_read_model</c>.
    /// </summary>
    internal Task<RouteResponse> ReadModelAsync(RouteRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_d.ReadModelStats?.Invoke() is not { } stats)
        {
            return Task.FromResult(Json(404, new JsonObject { ["error"] = "no_read_model" }));
        }

        var counters = new JsonObject();
        foreach (var key in stats.Keys.Where(static k => k != "parked"))
        {
            counters[key] = stats[key];
        }

        return Task.FromResult(Json(200, new JsonObject
        {
            ["counters"] = counters,
            ["parked"] = stats.ContainsKey("parked") ? stats["parked"] : JsonValue.FromArray(new JsonArray()),
            ["as_of"] = _d.Clock.UtcNow.ToUnixTimeMilliseconds(),
        }));
    }

    /// <summary><c>POST /v1/read-model/rebuild</c>: drops the held derived state, so each next read reads the store again (the on-demand rebuild <c>ah roster doctor</c> asks for).</summary>
    internal Task<RouteResponse> RebuildAsync(RouteRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        _d.Rebuild?.Invoke();
        return Task.FromResult(Json(200, new JsonObject { ["rebuilt"] = true, ["as_of"] = _d.Clock.UtcNow.ToUnixTimeMilliseconds() }));
    }

    /// <summary>
    /// The slice form of <c>GET /v1/view?path=/events</c>: <c>since</c> is the cursor a previous reply returned, a whole number clients echo back without comparing it, <c>epoch</c> the daemon instance the client
    /// last saw, and <c>sub</c> a comma-separated list of view names (<c>view:members</c>) and slice ids. It reads only the published map.
    /// </summary>
    private static async Task<RouteResponse> SliceEventsAsync(IReadModel model, Dictionary<string, string> q, int wait, CancellationToken cancellation)
    {
        var since = 0L;
        if (q.TryGetValue("since", out var raw) && !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out since))
        {
            return BadRequest("since must be a publish counter, a non-negative integer");
        }

        var sub = q["sub"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fresh = q.GetValueOrDefault("fresh")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fresh is not null && fresh.Contains("*"))
        {
            // "*" stands for every exact slice id of the subscription (no view, no list), so a first poll need not repeat them all.
            fresh = [.. fresh.Where(static f => f != "*").Concat(sub.Where(static id => !id.StartsWith("view:", StringComparison.Ordinal) && !id.StartsWith("list:", StringComparison.Ordinal))).Distinct(StringComparer.Ordinal)];
        }

        var bytes = await model.PollAsync(since, q.GetValueOrDefault("epoch"), sub, TimeSpan.FromSeconds(wait), cancellation, fresh).ConfigureAwait(false);
        return new RouteResponse(200, NoHeaders, bytes);
    }

    /// <summary>The view path of the events long-poll.</summary>
    internal const string EventsPath = "/events";

    /// <summary>Reads the hierarchy again (at most once per <see cref="ShareMs"/>) and keeps what changed since the last read as events.</summary>
    private Task RefreshEventsAsync()
    {
        lock (_gate)
        {
            if (_refreshing is { IsCompleted: false })
            {
                return _refreshing;
            }

            var now = NowMs();
            if (_snapshotAt >= 0 && now - _snapshotAt < ShareMs)
            {
                return Task.CompletedTask;
            }

            _refreshing = Task.Run(() =>
            {
                try
                {
                    ViewEventBatch fresh;
                    EventCursor? next = null;
                    if (_d.EventFeed is { } feed)
                    {
                        fresh = feed(now);
                    }
                    else
                    {
                        using var processes = ProcessSnapshotScope.Begin();
                        using var listings = WatchModule.OpenBuildScope();
                        var step = WatchModule.StepEvents(_cursor, _d.Source, _d.Revs, _d.StatusDirs.Snapshot(), now);
                        next = step.Cursor;
                        fresh = step.Batch;
                    }

                    _events.Value.Append(fresh.Events, fresh.Dropped);
                    lock (_gate)
                    {
                        _cursor = next ?? _cursor;
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // A read that fails leaves the events as they were: the page is still answered and the next window reads again.
                    LogRefreshFailure(e, now);
                }
                finally
                {
                    lock (_gate)
                    {
                        _snapshotAt = now;
                    }
                }
            });
            return _refreshing;
        }
    }

    private long _refreshFailureLoggedAt = -1;

    /// <summary>Writes one line for a failed read, and no more than one per minute while the failure lasts.</summary>
    private void LogRefreshFailure(Exception error, long now)
    {
        if (_d.Log is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_refreshFailureLoggedAt >= 0 && now - _refreshFailureLoggedAt < RefreshFailureLogMs)
            {
                return;
            }

            _refreshFailureLoggedAt = now;
        }

        _d.Log($"view events: the hierarchy read failed: {error.GetType().Name}: {error.Message}");
    }

    private static RouteResponse EventsPage(ViewEventLog.Page page)
    {
        var events = new JsonArray();
        foreach (var (id, e) in page.Events)
        {
            events.Add(WatchModule.EventJson(e, id));
        }

        return Json(200, new JsonObject
        {
            ["view_schema"] = WatchModule.ViewSchema,
            ["kind"] = "events",
            ["cursor"] = page.Cursor,
            ["resync"] = page.Resync,
            ["events"] = events,
        });
    }

    /// <summary>
    /// <c>GET /v1/view?path=/events&amp;since=&lt;cursor&gt;&amp;wait=&lt;s&gt;</c>: the events after a cursor, long-polling like the status
    /// route. With no cursor, or one that cannot be continued, the answer asks the client to read the root view and carries the
    /// cursor to continue from.
    /// </summary>
    private async Task<RouteResponse> EventsAsync(string? since, int wait, CancellationToken cancellation)
    {
        if (since is not null && !ViewEventLog.TryParse(since, out _, out _))
        {
            return BadRequest("since must be an events cursor, <epoch>.<seq>");
        }

        var end = NowMs() + (wait * 1000L);
        var waiter = wait > 0 ? _waiters.Add() : null;
        try
        {
            while (true)
            {
                await RefreshEventsAsync().ConfigureAwait(false);
                var page = _events.Value.Read(since);
                if (page.Resync || page.Events.Count > 0 || wait == 0 || NowMs() >= end || cancellation.IsCancellationRequested)
                {
                    return EventsPage(page);
                }

                if (waiter!.Released)
                {
                    _ = await Pause(ReleasedPauseMs, cancellation).ConfigureAwait(false);
                    return EventsPage(page);
                }

                if (!await Pause(PollMs, cancellation).ConfigureAwait(false))
                {
                    return EventsPage(page);
                }
            }
        }
        finally
        {
            if (waiter is not null)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>
    /// The view at <paramref name="path"/>: one build of the whole hierarchy per epoch, however many requests and however many pages
    /// ask, and the answer for a path kept until the epoch ends. A request that finds the page being built waits for it.
    /// </summary>
    private async Task<ViewAnswer> BuildSharedAsync(string path)
    {
        var (task, builder) = Acquire(path);
        if (builder)
        {
            return await task.ConfigureAwait(false);
        }

        using (WatchModule.ViewTimingSection(task.IsCompleted ? "shared-hit" : "shared-wait"))
        {
            return await task.ConfigureAwait(false);
        }
    }

    private (Task<ViewAnswer> Task, bool Builder) Acquire(string path)
    {
        lock (_gate)
        {
            var now = NowMs();
            var epoch = _epoch;
            if (epoch is not null)
            {
                var age = now - epoch.Now;
                var ended = age >= _epochMs || (_writeAt >= epoch.Now && age >= EpochMs);
                if (epoch.Pages.TryGetValue(path, out var hit) && (!ended || !hit.Task.IsCompleted))
                {
                    return (hit.Task, false);
                }

                if (ended)
                {
                    epoch = null;
                }
            }

            epoch ??= NextEpoch(now);
            if (epoch.InFlight.TryGetValue(path, out var carried) && !carried.Task.IsCompleted)
            {
                return (carried.Task, false);
            }

            var entry = new Shared(DeriveAsync(epoch, path));
            if (epoch.Pages.Count < MaxShared)
            {
                epoch.Pages[path] = entry;
                Forget(epoch.Pages, path, entry);
            }

            return (entry.Task, true);
        }
    }

    /// <summary>
    /// A new epoch, current from now. A page of the epoch it follows that is still being built can be waited for in it, and is
    /// dropped from it when it lands. Call under the gate.
    /// </summary>
    private Epoch NextEpoch(long now)
    {
        var next = new Epoch(now, _d.StatusDirs.Snapshot());
        if (_epoch is { } old)
        {
            foreach (var (path, entry) in old.Pages.Concat(old.InFlight))
            {
                if (!entry.Task.IsCompleted)
                {
                    next.InFlight[path] = entry;
                    Forget(next.InFlight, path, entry, always: true);
                }
            }
        }

        _epoch = next;
        return next;
    }

    /// <summary>
    /// Takes <paramref name="entry"/> out of <paramref name="pages"/> when its build ends and failed, so the next request builds it
    /// again, or, with <paramref name="always"/>, however it ended.
    /// </summary>
    private void Forget(Dictionary<string, Shared> pages, string path, Shared entry, bool always = false) =>
        _ = entry.Task.ContinueWith(
            finished =>
            {
                lock (_gate)
                {
                    if ((always || !finished.IsCompletedSuccessfully) && pages.TryGetValue(path, out var kept) && ReferenceEquals(kept, entry))
                    {
                        _ = pages.Remove(path);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// The page now, from the epoch's model. herdr is read (once per epoch, off this thread) only when a herdr-transport team has a
    /// stream; the model that did not need it is the only one.
    /// </summary>
    private async Task<ViewAnswer> DeriveAsync(Epoch epoch, string path)
    {
        var started = NowMs();

        // A hierarchy that asked for herdr last time will ask again: read it first, or every epoch builds its model twice.
        var known = _herdr.Fresh() ?? (Volatile.Read(ref _herdrWanted) ? await SnapshotOf(epoch).ConfigureAwait(false) : null);
        while (true)
        {
            var current = ModelOf(epoch, known);
            var answer = await Task.Run(() => Derive(epoch, current.Model, path)).ConfigureAwait(false);
            Volatile.Write(ref _herdrWanted, current.Model.UsedHerdr);
            if (!current.Model.UsedHerdr || current.RealHerdr)
            {
                NoteBuildTime(epoch, NowMs() - started);
                return answer;
            }

            var snapshot = await SnapshotOf(epoch).ConfigureAwait(false);
            lock (epoch)
            {
                if (ReferenceEquals(epoch.Current, current))
                {
                    epoch.Current = new EpochModel(_models(epoch.Now, epoch.Dirs, () => snapshot), RealHerdr: true);
                }
            }
        }
    }

    /// <summary>A write through this daemon: the epoch it fell in ends as soon as <see cref="EpochMs"/> has passed, however far it was stretched.</summary>
    internal void WriteHappened()
    {
        lock (_gate)
        {
            _writeAt = NowMs();
        }
    }

    /// <summary>
    /// Stretches the epoch so whole-hierarchy builds take at most one part in <see cref="BuildShare"/> of a core however many clients poll:
    /// the slowest build of <paramref name="epoch"/> makes the next epoch start no sooner than that many times later. It stays between
    /// <see cref="EpochMs"/> and <see cref="MaxEpochMs"/>.
    /// </summary>
    private void NoteBuildTime(Epoch epoch, long ms)
    {
        lock (_gate)
        {
            epoch.SlowestMs = Math.Max(epoch.SlowestMs, ms);
            _epochMs = Math.Clamp(epoch.SlowestMs * BuildShare, EpochMs, MaxEpochMs);
        }
    }

    private EpochModel ModelOf(Epoch epoch, HerdrReading? known)
    {
        lock (epoch)
        {
            return epoch.Current ??= new EpochModel(_models(epoch.Now, epoch.Dirs, () => known ?? new HerdrReading(null, WatchModule.HerdrUnreachable)), known is not null);
        }
    }

    private Task<HerdrReading> SnapshotOf(Epoch epoch)
    {
        lock (epoch)
        {
            return epoch.Snapshot ??= _herdr.SnapshotAsync();
        }
    }

    private ViewAnswer Derive(Epoch epoch, IViewModel model, string path)
    {
        WatchModule.CurrentViewTiming?.AddBuild();
        _d.BuildLog?.Invoke();
        if (_d.BuildDelay is { } delay && delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }

        using var processes = ProcessSnapshotScope.Begin();
        ViewAnswer answer;
        using (WatchModule.OpenBuildScope())
        {
            answer = model.Build(path);
        }

        return answer;
    }

    // ---------------------------------------------------------------- /v1/inspect/*

    /// <summary>A route answering a read-only inspection: <paramref name="argument"/> names the one query parameter it takes.</summary>
    internal static Func<RouteRequest, CancellationToken, Task<RouteResponse>> Inspection(string argument, Func<string?, JsonObject> read) =>
        (request, _) =>
        {
            var value = QueryString.Parse(request.Query).GetValueOrDefault(argument);
            return Task.FromResult(Json(200, read(string.IsNullOrEmpty(value) ? null : value)));
        };

    // ---------------------------------------------------------------- /v1/status

    internal async Task<RouteResponse> StatusAsync(RouteRequest request, CancellationToken cancellation)
    {
        var q = QueryString.Parse(request.Query);
        var dir = q.GetValueOrDefault("dir");
        if (dir is null || !dir.StartsWith('/'))
        {
            return BadRequest("dir must be an absolute hierarchy dir");
        }

        if (!LongPoll.TryParseWait(q.GetValueOrDefault("wait"), out var wait))
        {
            return BadRequest(LongPoll.WaitMessage);
        }

        var sinceText = q.GetValueOrDefault("since");
        if (wait > 0 && sinceText is null)
        {
            return BadRequest("wait needs since: without a cursor the current document is always new, so a long-poll would return at once");
        }

        double? since = null;
        if (sinceText is not null)
        {
            var text = sinceText.Trim();
            var number = text.Length == 0 ? 0 : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                return BadRequest("since must be a rev");
            }

            since = number;
        }

        var end = NowMs() + (wait * 1000L);
        while (true)
        {
            var current = ReadStatus(dir);
            if (current is null)
            {
                return Error(404, "not_found", $"no status document is served for {dir}");
            }

            if (since is null || current.Rev != since || wait == 0 || NowMs() >= end || cancellation.IsCancellationRequested)
            {
                return Json(200, new JsonObject { ["rev"] = (double)current.Rev, ["doc"] = current.Doc });
            }

            if (!await Pause(PollMs, cancellation).ConfigureAwait(false))
            {
                continue;
            }
        }
    }

    /// <summary>The revision and document of a watched dir, or null when the dir is unwatched, gone or its document unreadable.</summary>
    private StatusReading? ReadStatus(string dir)
    {
        if (!_d.StatusDirs.Contains(dir))
        {
            return null;
        }

        var reading = _d.Revs.Read(dir);
        if (reading is null)
        {
            _d.StatusDirs.Remove(dir);
        }

        return reading;
    }
}
