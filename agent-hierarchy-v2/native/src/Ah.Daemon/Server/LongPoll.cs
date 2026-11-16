using System.Globalization;

namespace Ah.Daemon;

/// <summary>The one long-poll helper: wait validation, the waiter crowd cap, and the wait itself.</summary>
internal static class LongPoll
{
    /// <summary>The longest <c>wait</c>, in seconds, a long-poll route accepts.</summary>
    internal const int MaxWaitSeconds = 25;

    /// <summary>Waiting view clients kept at once; past it the oldest is released.</summary>
    internal const int MaxViewWaiters = 16;

    internal static string WaitMessage => $"wait must be a whole number of seconds from 0 to {MaxWaitSeconds}";

    /// <summary>
    /// Reads a <c>wait</c> query value. Absent is 0. Anything but a whole number of seconds from 0 to 25 is refused,
    /// as the Node daemon refuses it (400 <c>bad_request</c>).
    /// </summary>
    internal static bool TryParseWait(string? raw, out int seconds)
    {
        seconds = 0;
        if (raw is null)
        {
            return true;
        }

        // Number(raw): the empty string is 0; a decimal that is whole ("5.0", "1e1") counts; anything else is not an integer.
        var text = raw.Trim();
        double value;
        if (text.Length == 0)
        {
            value = 0;
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Floor(value) != value || value < 0 || value > MaxWaitSeconds)
        {
            return false;
        }

        seconds = (int)value;
        return true;
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for <paramref name="wake"/>. Returns true when it fired. Returns false at expiry
    /// or when <paramref name="drain"/> is cancelled: the route then answers its <c>unchanged</c> body at once.
    /// </summary>
    internal static async Task<bool> WaitAsync(TimeSpan wait, Task wake, CancellationToken drain)
    {
        if (wait <= TimeSpan.Zero || drain.IsCancellationRequested)
        {
            return wake.IsCompletedSuccessfully;
        }

        using var timeout = new CancellationTokenSource(wait);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, drain);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = linked.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), stop);
        var first = await Task.WhenAny(wake, stop.Task).ConfigureAwait(false);
        return ReferenceEquals(first, wake) && wake.IsCompletedSuccessfully;
    }
}

/// <summary>The view waiters, oldest first. Past the cap the oldest is told to release.</summary>
internal sealed class ViewWaiters(int max = LongPoll.MaxViewWaiters)
{
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];

    internal sealed class Waiter
    {
        private int _release;

        internal bool Released => Volatile.Read(ref _release) != 0;

        internal void Release() => Volatile.Write(ref _release, 1);
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    internal Waiter Add()
    {
        var w = new Waiter();
        lock (_gate)
        {
            _waiters.Add(w);
            while (_waiters.Count > max)
            {
                _waiters[0].Release();
                _waiters.RemoveAt(0);
            }
        }

        return w;
    }

    internal void Remove(Waiter w)
    {
        lock (_gate)
        {
            _ = _waiters.Remove(w);
        }
    }
}
