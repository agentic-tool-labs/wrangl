using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Daemon;

/// <summary>What the bridge runs on.</summary>
/// <param name="Clock">The wall clock: the idle limit is read from it.</param>
/// <param name="Files">Reads the static page files.</param>
/// <param name="Writer">Writes and removes <c>ui.json</c>.</param>
/// <param name="RunDir">The run directory <c>ui.json</c> lives in.</param>
/// <param name="StaticRoot">The directory the page files are served from; empty serves none.</param>
/// <param name="Log">Writes one line to the daemon log.</param>
/// <param name="View">The <c>GET /v1/view</c> handler the daemon's own table holds.</param>
/// <param name="ProcessId">The daemon's pid.</param>
/// <param name="Disabled">Whether the kill switch is on: every lease is refused.</param>
/// <param name="RequestHeaders">Time allowed to receive a request's headers.</param>
/// <param name="KeepAlive">How long an idle keep-alive connection is kept.</param>
/// <param name="RequestRead">Time allowed to receive a request body.</param>
/// <param name="DrainToken">Cancelled when the daemon starts to drain.</param>
internal sealed record UiBridgeOptions(
    IClock Clock,
    IFileReader Files,
    IRawFileWriter Writer,
    string RunDir,
    string StaticRoot,
    Action<string> Log,
    Func<RouteRequest, CancellationToken, Task<RouteResponse>> View,
    int ProcessId,
    Func<bool> Disabled,
    TimeSpan RequestHeaders,
    TimeSpan KeepAlive,
    TimeSpan RequestRead,
    CancellationToken DrainToken)
{
    /// <summary>The ah home: the parent of the run directory.</summary>
    internal string Home => Path.GetDirectoryName(RunDir)!;

    /// <summary>How long a stop waits for requests still open before it drops them.</summary>
    internal TimeSpan StopDeadline { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long the listener may go without an authenticated request.</summary>
    internal TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The answer route's host support; null answers 503.</summary>
    internal UiAnswerSupport? Answer { get; init; }

    /// <summary>The team-close route's host support; null answers 503.</summary>
    internal UiTeamCloseSupport? TeamClose { get; init; }

    /// <summary>The rules routes' host support; null answers 503.</summary>
    internal UiRulesSupport? Rules { get; init; }
}

/// <summary>The bridge is switched off by <c>AH_UI=off</c>.</summary>
internal sealed class UiDisabledException() : Exception("browser UI disabled");

/// <summary>The daemon is stopping and takes no new lease.</summary>
internal sealed class UiClosedException() : Exception("the daemon is stopping");

/// <summary>One open lease: the listener stays up while at least one is.</summary>
internal sealed class UiLease(int pid, bool service = false)
{
    internal int Pid { get; } = pid;

    /// <summary>A lease held by `ahd ui start`: while it is held the listener is not stopped for lack of requests.</summary>
    internal bool Service { get; } = service;

    /// <summary>Completes when the bridge ends the lease (the last lease dropped, idle, or the daemon stopping).</summary>
    internal TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A lease and what the client needs to open the page.</summary>
/// <param name="Lease">The lease.</param>
/// <param name="Port">The listener's port.</param>
/// <param name="MovedFrom">The remembered port the listener could not take, or 0.</param>
internal readonly record struct UiGrant(UiLease Lease, int Port, int MovedFrom = 0);

/// <summary>
/// The loopback bridge: starts a listener when the first lease arrives and stops it when the last lease drops, after 15 minutes
/// without an authenticated request, or when the daemon stops. Start and stop run one at a time under one lock.
/// </summary>
internal sealed class UiBridge(UiBridgeOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _life = new(1, 1);
    private readonly List<UiLease> _leases = [];
    private UiSession? _session;
    private bool _refusing;

    private string InfoPath => Path.Combine(options.RunDir, "ui.json");

    /// <summary>The last port bound. It outlives the listener, unlike <c>ui.json</c>: the page's browser storage is per origin, so the port is kept stable.</summary>
    private string PortPath => Path.Combine(options.RunDir, "ui-port");

    /// <summary>Whether a listener is up. A live listener keeps the daemon from idle-exiting.</summary>
    internal bool Active => Volatile.Read(ref _session) is not null;

    /// <summary>The live listener's port, or 0.</summary>
    internal int Port => Volatile.Read(ref _session)?.Port ?? 0;

    /// <summary>The leases held now.</summary>
    internal int Leases
    {
        get
        {
            lock (_leases)
            {
                return _leases.Count;
            }
        }
    }

    /// <summary>Removes a <c>ui.json</c> a previous daemon left behind. Runs before this daemon's own reopen, so it is never this daemon's.</summary>
    internal void RemoveStale() => RemoveInfo();

    /// <summary>
    /// Adds a lease, binding a fresh listener first when none is live. Waits for a stop that is running.
    /// Throws <see cref="UiDisabledException"/>, <see cref="UiClosedException"/> or <see cref="UiBindException"/>.
    /// </summary>
    internal async Task<UiGrant> OpenLeaseAsync(int pid, CancellationToken cancellation, bool service = false)
    {
        await _life.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_refusing)
            {
                throw new UiClosedException();
            }

            if (options.Disabled())
            {
                throw new UiDisabledException();
            }

            var opened = false;
            if (_session is null)
            {
                try
                {
                    _session = await BindAsync().ConfigureAwait(false);
                }
                catch (UiBindException e)
                {
                    options.Log($"bridge bind failed: {e.Message}");
                    throw;
                }

                opened = true;
            }

            var lease = new UiLease(pid, service);
            lock (_leases)
            {
                _leases.Add(lease);
            }

            _session.Touch();

            try
            {
                WriteInfo(_session);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lock (_leases)
                {
                    _ = _leases.Remove(lease);
                }

                if (opened)
                {
                    await StopLockedAsync("ui.json not written").ConfigureAwait(false);
                }

                options.Log($"bridge bind failed: ui.json not written: {e.GetType().Name}");
                throw new UiBindException("ui.json not written", e);
            }

            if (opened)
            {
                options.Log($"bridge open (port {_session.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}, lease pid {pid.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            }

            return new UiGrant(lease, _session.Port, _session.MovedFrom);
        }
        finally
        {
            _ = _life.Release();
        }
    }

    /// <summary>Ends <paramref name="lease"/>. The last lease dropping stops the listener.</summary>
    internal async Task DropLeaseAsync(UiLease lease)
    {
        await _life.WaitAsync().ConfigureAwait(false);
        try
        {
            bool removed;
            int left;
            lock (_leases)
            {
                removed = _leases.Remove(lease);
                left = _leases.Count;
            }

            if (!removed)
            {
                return;
            }

            _ = lease.Ended.TrySetResult();
            if (left == 0)
            {
                await StopLockedAsync("lease dropped").ConfigureAwait(false);
            }
            else if (_session is { } session)
            {
                try
                {
                    WriteInfo(session);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    options.Log($"bridge info file not rewritten: {e.GetType().Name}");
                }
            }
        }
        finally
        {
            _ = _life.Release();
        }
    }

    /// <summary>Stops the listener when it has seen no authenticated request for the idle limit.</summary>
    internal async Task CheckIdleAsync()
    {
        if (Volatile.Read(ref _session) is null)
        {
            return;
        }

        await _life.WaitAsync().ConfigureAwait(false);
        try
        {
            bool serviceHeld;
            lock (_leases)
            {
                serviceHeld = _leases.Exists(static l => l.Service);
            }

            if (!serviceHeld && _session is { } session && options.Clock.UtcNow - session.LastAuthenticated >= options.IdleLimit)
            {
                await StopLockedAsync("idle").ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _life.Release();
        }
    }

    /// <summary>Stops the listener and, with <paramref name="refuseNewLeases"/>, takes no lease again (the daemon is stopping).</summary>
    internal async Task StopAsync(string reason, bool refuseNewLeases)
    {
        await _life.WaitAsync().ConfigureAwait(false);
        try
        {
            if (refuseNewLeases)
            {
                _refusing = true;
            }

            await StopLockedAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _ = _life.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync("daemon stop", refuseNewLeases: true).ConfigureAwait(false);

    /// <summary>The stop sequence: closing, the close signal, the stop with its deadline, the dispose, then <c>ui.json</c> goes. Runs under the lifecycle lock.</summary>
    private async Task StopLockedAsync(string reason)
    {
        if (_session is not { } session)
        {
            return;
        }

        session.BeginClose();
        UiLease[] ended;
        lock (_leases)
        {
            ended = [.. _leases];
            _leases.Clear();
        }

        foreach (var lease in ended)
        {
            _ = lease.Ended.TrySetResult();
        }

        await session.StopAsync(options.StopDeadline).ConfigureAwait(false);
        session.Dispose();
        RemoveInfo();
        Volatile.Write(ref _session, null);
        options.Log($"bridge close ({reason})");
    }

    /// <summary>Binds the remembered port, or any free one when none is remembered or it is taken, and remembers what it got.</summary>
    private async Task<UiSession> BindAsync()
    {
        UiSession session;
        var wanted = RememberedPort();
        if (wanted == 0)
        {
            session = await UiSession.StartAsync(options).ConfigureAwait(false);
        }
        else
        {
            try
            {
                session = await UiSession.StartAsync(options, wanted).ConfigureAwait(false);
            }
            catch (UiBindException e)
            {
                options.Log($"bridge port {wanted.ToString(System.Globalization.CultureInfo.InvariantCulture)} not bound: {e.Message}");
                session = await UiSession.StartAsync(options).ConfigureAwait(false);
                session.MovedFrom = wanted;
            }
        }

        try
        {
            options.Writer.WriteAtomic(
                PortPath,
                $"{PortPath}.{options.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.tmp",
                Utf8Text.Encode(session.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            options.Log($"bridge: port not remembered: {e.GetType().Name}");
        }

        return session;
    }

    private int RememberedPort()
    {
        try
        {
            return int.TryParse(options.Files.ReadText(PortPath).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535 ? port : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void WriteInfo(UiSession session)
    {
        UiLease? first;
        lock (_leases)
        {
            first = _leases.FirstOrDefault();
        }

        var doc = new JsonObject
        {
            ["v"] = 1,
            ["port"] = session.Port,
            ["lease_pid"] = first?.Pid ?? 0,
            ["started"] = Timestamps.ToIsoString(session.Started),
        };
        options.Writer.WriteAtomic(
            InfoPath,
            $"{InfoPath}.{options.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.tmp",
            Utf8Text.Encode(JsonText.Stringify(doc, 0) + "\n"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void RemoveInfo()
    {
        try
        {
            _ = options.Writer.Unlink(InfoPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            options.Log($"bridge: ui.json not removed: {e.GetType().Name}");
        }
    }
}
