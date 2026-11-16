using Ah.Core.Json;

namespace Ah.Tui.Core;

/// <summary>
/// The client's reads of the read model: the summary manifest on a plain read every <see cref="SummaryRound"/>, and one held poll of the slice feed
/// subscribed to the ids the current screen wants, as the session's tree held them. It never writes to a daemon and never starts one.
/// </summary>
internal sealed class SliceFeed(IViewSource source, IDaemonDiscovery discovery, ITuiClock clock, Action changed, Action<Exception> failed) : IDisposable
{
    private static readonly TimeSpan SummaryRound = TimeSpan.FromSeconds(5);

    /// <summary>The round while the summary has not given the team list yet, or for a while has a plan not yet read: a daemon that just started is still composing it.</summary>
    private static readonly TimeSpan NoListRound = TimeSpan.FromMilliseconds(200);

    private readonly DateTimeOffset _started = clock.UtcNow;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Dictionary<string, JsonValue> _slices = new(StringComparer.Ordinal);
    private JsonValue? _summary;
    private JsonValue? _store;
    private List<string> _ids = [];
    private readonly Dictionary<string, DateTimeOffset> _wantedAt = new(StringComparer.Ordinal);
    private bool _reload;
    private CancellationTokenSource? _poll;
    private TaskCompletionSource _wake = NewKick();
    private TaskCompletionSource _kick = NewKick();
    private DaemonProbe? _info;
    private bool _down;
    private string _fault = string.Empty;
    private DateTimeOffset? _lastGood;
    private bool _building;

    private static TaskCompletionSource NewKick() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every slice held, by id.</summary>
    internal IReadOnlyDictionary<string, JsonValue> Slices
    {
        get
        {
            lock (_gate)
            {
                return _slices;
            }
        }
    }

    /// <summary>Whether no daemon answers, or the one found is draining.</summary>
    internal bool Down
    {
        get
        {
            lock (_gate)
            {
                return _down;
            }
        }
    }

    /// <summary>The daemon's complaint when it answered with something other than a view; empty otherwise.</summary>
    internal string Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }
    }

    internal DateTimeOffset? LastGood
    {
        get
        {
            lock (_gate)
            {
                return _lastGood;
            }
        }
    }

    /// <summary>Whether the last summary said the daemon is still building it: <c>building</c>, or slices <c>pending</c>.</summary>
    internal bool Building
    {
        get
        {
            lock (_gate)
            {
                return _building;
            }
        }
    }

    /// <summary>
    /// Whether every one of these ids is settled: held, or wanted for <see cref="TuiClientRules.Settling"/> without arriving, so it is not served.
    /// A reply alone says nothing: a poll returns as soon as anything is ready, and a lane leased by that poll is composed after it.
    /// </summary>
    internal bool Answered(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            return ids.All(id => _slices.ContainsKey(id) || (_wantedAt.TryGetValue(id, out var at) && now - at >= TuiClientRules.Settling));
        }
    }

    /// <summary>Subscribes the slice poll to these ids, cut to one poll's budget. A change ends the poll in flight, so the next names them and the daemon's lease on the rest ends.</summary>
    internal void Want(IEnumerable<string> ids)
    {
        var kept = ReadModelPaths.Within(ids);
        lock (_gate)
        {
            if (kept.SequenceEqual(_ids, StringComparer.Ordinal))
            {
                return;
            }

            _ids = kept;
            var now = clock.UtcNow;
            foreach (var id in _wantedAt.Keys.Except(kept, StringComparer.Ordinal).ToList())
            {
                _ = _wantedAt.Remove(id);
            }

            foreach (var id in kept)
            {
                _ = _wantedAt.TryAdd(id, now);
            }

            _poll?.Cancel();
            _ = _wake.TrySetResult();
        }
    }

    /// <summary>Reads everything again, now.</summary>
    internal void Refresh()
    {
        TaskCompletionSource kick;
        lock (_gate)
        {
            kick = _kick;
            _reload = true;
            _poll?.Cancel();
        }

        _ = kick.TrySetResult();
    }

    /// <summary>Runs the reads until <paramref name="cancellationToken"/> ends them.</summary>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await Task.WhenAll(SummaryAsync(link.Token), SlicesAsync(link.Token)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task SummaryAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var info = await discovery.DiscoverAsync(ct).ConfigureAwait(false);
                ViewOutcome got = info is null ? ViewOutcome.Down : await source.GetViewAsync(info, ReadModelPaths.Summary, null, 0, ct).ConfigureAwait(false);
                var round = got.Kind == ViewOutcomeKind.Down ? TuiClientRules.Retry : SummaryRound;
                if (got.Kind != ViewOutcomeKind.Aborted)
                {
                    lock (_gate)
                    {
                        _info = info;
                        Take(got);
                        if (got is { Kind: ViewOutcomeKind.View, View: { } view })
                        {
                            _summary = view;
                            Merged();
                            _building = TuiRender.M(view, "building") is { IsTruthy: true } || TuiRender.M(view, "pending") is { IsArray: true } p && p.AsArray().Count > 0;
                            if (!_slices.ContainsKey("list:teams")
                                || ((TeamSlices.AnyPlanNotRead(_slices) || (_building && TeamSlices.ListIsEmpty(_slices))) && clock.UtcNow - _started < TuiClientRules.BuildingCeiling))
                            {
                                round = NoListRound;
                            }
                        }
                    }

                    changed();
                }

                _ = await RefreshedDuringAsync(round, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the session ended
        }
        catch (Exception e)
        {
            failed(e);
        }
    }

    /// <summary>What an answer says of the daemon. Call under the gate.</summary>
    private void Take(ViewOutcome got)
    {
        switch (got.Kind)
        {
            case ViewOutcomeKind.View:
                _down = false;
                _fault = string.Empty;
                _lastGood = clock.UtcNow;
                break;
            case ViewOutcomeKind.Down:
                _down = true;
                _fault = string.Empty;
                _info = null;
                break;
            case ViewOutcomeKind.Error:
                _down = false;
                _fault = TuiClientRules.FaultText(got);
                break;
        }
    }

    private void Merged() => _slices = ReadModelPaths.All([_summary, _store]);

    /// <summary>
    /// The one held poll of the slice feed, for as long as the session lasts: a first request asks for the bodies it does not hold, then each holds
    /// until something changes. A resync or a new epoch reads everything again; a failure backs off as a held view does.
    /// </summary>
    private async Task SlicesAsync(CancellationToken ct)
    {
        long seq = 0;
        string? epoch = null;
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<string> sub;
                List<string> fresh;
                Task wake;
                DaemonProbe? info;
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_gate)
                {
                    if (_ids.Count == 0 || _info is null)
                    {
                        _wake = NewKick();
                    }

                    wake = _wake.Task;
                    sub = _ids;
                    info = _info;
                    if (_reload)
                    {
                        _reload = false;
                        seq = 0;
                        epoch = null;
                    }

                    var held = seq == 0 ? [] : ReadModelPaths.All([_store]).Keys.ToHashSet(StringComparer.Ordinal);
                    fresh = [.. sub.Where(id => !held.Contains(id))];
                    _poll = sub.Count > 0 ? poll : null;
                }

                if (sub.Count == 0)
                {
                    await wake.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (info is null)
                {
                    // The summary's round finds the daemon; this waits for it, or for a refresh.
                    _ = await RefreshedDuringAsync(TuiClientRules.Retry, ct).ConfigureAwait(false);
                    info = await discovery.DiscoverAsync(ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _info = info;
                        _poll = null;
                    }

                    continue;
                }

                var request = new SlicePoll(sub, fresh, seq, epoch, TuiClientRules.LongPollSeconds);
                ViewOutcome got;
                try
                {
                    got = await source.GetSlicesAsync(info, request, poll.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // r or new ids cancelled this round, not the session.
                    got = ViewOutcome.Aborted;
                }

                lock (_gate)
                {
                    _poll = null;
                }

                if (got.Kind == ViewOutcomeKind.Aborted)
                {
                    continue;
                }

                if (got.Kind != ViewOutcomeKind.View || got.View is not { } reply)
                {
                    lock (_gate)
                    {
                        Take(got);
                    }

                    changed();
                    seq = 0;
                    epoch = null;
                    if (await RefreshedDuringAsync(TuiClientRules.Backoff(++failures), ct).ConfigureAwait(false))
                    {
                        failures = 0;
                    }

                    continue;
                }

                failures = 0;
                var served = TuiRender.M(reply, "epoch") is { IsText: true } e ? e.AsString() : null;
                if ((TuiRender.M(reply, "resync") is { IsBool: true } resync && resync.AsBool()) || (epoch is not null && served != epoch))
                {
                    seq = 0;
                    epoch = null;
                    continue;
                }

                epoch = served;
                seq = TuiRender.M(reply, "seq") is { IsNumber: true } n ? (long)n.AsNumber() : 0;
                lock (_gate)
                {
                    Take(got);
                    _store = ReadModelPaths.Merge(_store, reply, fresh, _ids);
                    Merged();
                }

                changed();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the session ended
        }
        catch (Exception e)
        {
            failed(e);
        }
        finally
        {
            lock (_gate)
            {
                _poll = null;
            }
        }
    }

    /// <summary>Waits out <paramref name="wait"/>; true when a refresh ended the wait first.</summary>
    private async Task<bool> RefreshedDuringAsync(TimeSpan wait, CancellationToken ct)
    {
        Task kicked;
        lock (_gate)
        {
            kicked = _kick.Task;
        }

        if (kicked.IsCompleted)
        {
            lock (_gate)
            {
                _kick = NewKick();
                kicked = _kick.Task;
            }
        }

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = clock.DelayAsync(wait, wake.Token);
        if (await Task.WhenAny(delay, kicked).ConfigureAwait(false) != kicked)
        {
            await delay.ConfigureAwait(false);
            return false;
        }

        await wake.CancelAsync().ConfigureAwait(false);
        return true;
    }
}
