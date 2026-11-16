using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Daemon;

/// <summary>
/// herdr's agent list, read at most once every 2 s by one in-flight query shared by every view build, never on the loop. A herdr
/// that has not answered in 1.5 s is "not reachable": the views say <c>unknown</c> and nothing waits longer.
/// </summary>
internal sealed class HerdrCache(Func<TimeSpan, CancellationToken, Task<JsonArray>> read, Func<long> nowMs)
{
    internal const int TtlMs = 2000;
    internal const int TimeoutMs = 1500;

    private readonly object _gate = new();
    private (HerdrReading Reading, long At)? _reading;
    private Task<HerdrReading>? _flight;

    /// <summary>The cached reading while it is fresh, else null.</summary>
    internal HerdrReading? Fresh()
    {
        lock (_gate)
        {
            return _reading is { } r && nowMs() - r.At < TtlMs ? r.Reading : null;
        }
    }

    /// <summary>The cached reading while it is fresh, else the one in-flight query (started when there is none). Never throws.</summary>
    internal Task<HerdrReading> SnapshotAsync()
    {
        lock (_gate)
        {
            if (_reading is { } r && nowMs() - r.At < TtlMs)
            {
                return Task.FromResult(r.Reading);
            }

            if (_flight is { } running)
            {
                return running;
            }

            {
                var query = Query();
                _flight = query;
                _ = query.ContinueWith(
                    _ =>
                    {
                        lock (_gate)
                        {
                            _flight = null;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return query;
            }
        }
    }

    private async Task<HerdrReading> Query()
    {
        HerdrReading reading;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs));
            var agents = await read(TimeSpan.FromMilliseconds(TimeoutMs), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            reading = new HerdrReading(agents, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            reading = new HerdrReading(null, WatchModule.HerdrUnreachable);
        }

        lock (_gate)
        {
            _reading = (reading, nowMs());
        }

        return reading;
    }
}
