using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.State;

namespace Ah.Daemon;

/// <summary>
/// <c>GET</c> and <c>POST /v1/state/{domain}/{op}</c>: a thin adapter from the host's request to the shared state
/// handler. A read runs on the request thread; a write runs on its domain's own worker, so a write waiting on a lock
/// occupies only that domain's worker, and the domain's after-write hook runs when the write has returned.
/// </summary>
internal static class StateRoutes
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>(StringComparer.Ordinal);

    internal static IReadOnlyList<RouteRegistration> Create(IStateService? service, DomainWorkers workers, Action<string>? log = null)
    {
        if (service is null)
        {
            return [];
        }

        Task<RouteResponse> Handle(RouteRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return HandleAsync(service, workers, log, request);
        }

        return
        [
            new RouteRegistration("GET /v1/state/{domain}/{op}", Handle),
            new RouteRegistration("POST /v1/state/{domain}/{op}", Handle),
        ];
    }

    /// <summary>The route time past which a state route writes a "slow" line splitting its worker queue wait from its run.</summary>
    private const long SlowMs = 2000;

    private static async Task<RouteResponse> HandleAsync(IStateService service, DomainWorkers workers, Action<string>? log, RouteRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        long queuedTicks = 0;
        long runTicks = 0;
        var reply = await service.HandleAsync(
            request.Method,
            request.PathParameters["domain"],
            request.PathParameters["op"],
            ParseQuery(request.Query),
            request.Body,
            (domain, work) =>
            {
                var enqueued = Stopwatch.GetTimestamp();
                return workers.For(domain).Run(() =>
                {
                    var running = Stopwatch.GetTimestamp();
                    Interlocked.Add(ref queuedTicks, running - enqueued);
                    try
                    {
                        return work();
                    }
                    finally
                    {
                        Interlocked.Add(ref runTicks, Stopwatch.GetTimestamp() - running);
                    }
                });
            }).ConfigureAwait(false);
        var totalMs = Ms(Stopwatch.GetTimestamp() - started);
        if (log is not null && totalMs > SlowMs)
        {
            var domain = request.PathParameters["domain"];
            log(string.Create(
                CultureInfo.InvariantCulture,
                $"state {domain}.{request.PathParameters["op"]} slow: {totalMs} ms: queue_wait_ms={Ms(Interlocked.Read(ref queuedTicks))} run_ms={Ms(Interlocked.Read(ref runTicks))} pending={workers.For(domain).Pending}"));
        }

        if (reply.Code == "busy")
        {
            var domain = request.PathParameters["domain"];
            log?.Invoke($"state {domain}.{request.PathParameters["op"]} refused busy (pending on {domain} worker: {workers.For(domain).Pending}): {System.Text.Encoding.UTF8.GetString(reply.Body.Span)}");
        }

        return new RouteResponse(reply.Status, NoHeaders, reply.Body);
    }

    /// <summary>The query string as <c>URLSearchParams</c> reads it: <c>+</c> is a space, escapes are decoded, the last repeat wins.</summary>
    internal static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : pair[(eq + 1)..];
            all[Decode(name)] = Decode(value);
        }

        return all;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

    private static long Ms(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);
}
