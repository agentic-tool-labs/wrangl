using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>What the running daemon shows of serving mail entries that have claim records.</summary>
internal enum ClaimSupport
{
    /// <summary>Its health lists protocol 2.</summary>
    Served,

    /// <summary>Its health lists protocols and not 2: a daemon of an earlier build.</summary>
    Older,

    /// <summary>A daemon is there and did not answer in time, errored, or answered with no protocol list.</summary>
    Unanswered,

    /// <summary>No daemon exists to ask: the request was never sent.</summary>
    NoDaemon,
}

/// <summary>
/// Whether the daemon that is running serves mail entries that have claim records. Protocol 2 is that: a daemon lists it among its
/// <c>protocols</c> in its health answer, and a daemon of an earlier build lists only 1.
/// </summary>
internal static class DaemonClaims
{
    /// <summary>Whether an entry may be given a claim record: the daemon serves them, or there is no daemon whose ladder could double it.</summary>
    internal static bool MayClaim(ClaimSupport support) => support is ClaimSupport.Served or ClaimSupport.NoDaemon;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// What the running daemon shows. A claim record is only safe against a daemon known to serve it, so a daemon that is there and
    /// does not show it counts as one that does not (<see cref="ClaimSupport.Older"/> when it lists protocols without 2,
    /// <see cref="ClaimSupport.Unanswered"/> when it did not answer in time, errored, or lists none). <see cref="ClaimSupport.NoDaemon"/>
    /// only when the request never went out and no daemon process or socket exists, so the files alone serve the entry. A request that
    /// never went out because discovery of a running daemon timed out is a daemon that did not answer.
    /// </summary>
    internal static string? ReadModelOf(IDaemonClient daemon)
    {
        try
        {
            // b459 step 11: doctor is the on-demand rebuild; a daemon without the route answers 404 and the counters are read as before
            // ah2-616: only doctor reads this, and a diagnostic read launches no daemon
            _ = daemon.RequestAsync(new DaemonRequest("POST", "/v1/read-model/rebuild", default, Budget, StartIfDown: false, LaunchIfDown: false)).GetAwaiter().GetResult();
            var reply = daemon.RequestAsync(new DaemonRequest("GET", "/v1/read-model", default, Budget, StartIfDown: false, LaunchIfDown: false)).GetAwaiter().GetResult();
            if (reply.Status != 200 || reply.Body.IsEmpty || JsonText.Parse(reply.Body.Span) is not { IsMap: true } body
                || body.Member("counters") is not { IsMap: true } model)
            {
                return null;
            }

            return string.Join(' ', model.AsObject().Keys.Select(k => k + "=" + (model.Member(k) is { IsNumber: true } n ? n.AsNumber().ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "?")));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <param name="daemon">The client to ask.</param>
    /// <param name="launch">False for doctor's diagnostic read: no detached start when no daemon lives (ah2-616). The claims path keeps launching.</param>
    internal static ClaimSupport SupportOf(IDaemonClient daemon, bool launch = true)
    {
        try
        {
            var reply = daemon.RequestAsync(new DaemonRequest("GET", "/v1/health", default, Budget, StartIfDown: false, LaunchIfDown: launch)).GetAwaiter().GetResult();
            if (reply.Status == 0)
            {
                return reply.SentAt is null && !daemon.DaemonPresent() ? ClaimSupport.NoDaemon : ClaimSupport.Unanswered;
            }

            if (reply.Status != 200 || reply.Body.IsEmpty || JsonText.Parse(reply.Body.Span) is not { IsMap: true } health
                || health.AsObject().Get("protocols") is not { IsArray: true } protocols || protocols.AsArray().Count == 0)
            {
                return ClaimSupport.Unanswered;
            }

            return protocols.AsArray().Any(static p => p.IsNumber && p.AsNumber() == 2) ? ClaimSupport.Served : ClaimSupport.Older;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ClaimSupport.Unanswered;
        }
    }
}
