using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.DaemonClient;
using Ah.Tui.Core;

namespace Ah.Tui.Adapters;

/// <summary>
/// The client's only dealings with a daemon: it finds one without starting anything, and it makes two kinds of GET, <c>/v1/health</c>
/// and <c>/v1/view</c>. It never starts, stops, drains or writes to a daemon. The read model's manifest (<see cref="ReadModelPaths.Summary"/>)
/// and a poll of the slice feed each come back as a view.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The semaphore is never asked for its wait handle, so it holds nothing to release.")]
internal sealed class DaemonViewSource(IDaemonClient daemon, IUnixHttpTransport transport) : IViewSource, IDaemonDiscovery
{
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromMilliseconds(500);
    internal const int ViewTimeoutSeconds = 15;
    internal const int MaxInFlight = 8;

    /// <summary>The view requests in flight, held and plain together: a request over the cap waits for a slot.</summary>
    private readonly SemaphoreSlim _inFlight = new(MaxInFlight, MaxInFlight);

    public async Task<DaemonProbe?> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await daemon.DiscoverAsync(DiscoverTimeout, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return null;
            }

            var reply = await transport.SendAsync(info.Socket, "GET", "/v1/health", default, DiscoverTimeout, cancellationToken).ConfigureAwait(false);
            if (reply.Status != 200 || !JsonText.TryParse(reply.Body.Span, out var health) || !health.IsMap)
            {
                return null;
            }

            return TuiRender.Truthy(TuiRender.M(health, "draining")) ? null : new DaemonProbe(health, info.Socket);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<ViewOutcome> GetViewAsync(DaemonProbe probe, string path, string? since, int waitSeconds, CancellationToken cancellationToken)
    {
        var slot = false;
        try
        {
            await _inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            slot = true;
            // Each route is a literal at its call, so the transport scan can hold the client to its GET routes.
            var timeout = TimeSpan.FromSeconds(ViewTimeoutSeconds);
            var model = path == ReadModelPaths.Summary;
            var reply = model
                ? await transport.SendAsync(probe.Socket, "GET", "/v1/view/summary", default, timeout, cancellationToken).ConfigureAwait(false)
                : await transport.SendAsync(probe.Socket, "GET", "/v1/view" + Query(path, since, waitSeconds), default, TimeSpan.FromSeconds(waitSeconds > 0 ? waitSeconds + ViewTimeoutSeconds : ViewTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            return Outcome(reply, model);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return ViewOutcome.Aborted;
        }
        catch (Exception)
        {
            return ViewOutcome.Down;
        }
        finally
        {
            if (slot)
            {
                _ = _inFlight.Release();
            }
        }
    }

    /// <summary>One poll of the slice feed. It holds outside the view requests' slots, so a held poll cannot starve them.</summary>
    public async Task<ViewOutcome> GetSlicesAsync(DaemonProbe probe, SlicePoll poll, CancellationToken cancellationToken)
    {
        try
        {
            var query = DaemonClientModule.SliceEventsQuery(poll.Sub, poll.Fresh, poll.Since, poll.Epoch, poll.WaitSeconds);
            var reply = await transport.SendAsync(probe.Socket, "GET", "/v1/view?path=%2Fevents" + query, default, TimeSpan.FromSeconds(poll.WaitSeconds + ViewTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            return Outcome(reply, model: true);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return ViewOutcome.Aborted;
        }
        catch (Exception)
        {
            return ViewOutcome.Down;
        }
    }

    /// <summary>A reply as an outcome; a read-model reply's 200 is the view itself, with no view envelope to read.</summary>
    private static ViewOutcome Outcome(HttpReply reply, bool model)
    {
        JsonValue? body = JsonText.TryParse(reply.Body.Span, out var parsed) ? parsed : (JsonValue?)null;
        return model && reply.Status == 200 && body is { IsMap: true } doc ? new ViewOutcome(ViewOutcomeKind.View, View: doc) : ViewOutcome.FromReply(reply.Status, body);
    }

    /// <summary>The query as a URL's form encoding makes it: <c>path</c>, then <c>since</c> and <c>wait</c> only when re-polling.</summary>
    internal static string Query(string path, string? since, int waitSeconds)
    {
        var q = new StringBuilder("?path=").Append(FormEncode(path));
        if (since is not null)
        {
            q.Append("&since=").Append(FormEncode(since));
        }

        if (waitSeconds > 0)
        {
            q.Append("&wait=").Append(waitSeconds.ToString(CultureInfo.InvariantCulture));
        }

        return q.ToString();
    }

    internal static string FormEncode(string value) => DaemonClientModule.FormEncode(value);
}
