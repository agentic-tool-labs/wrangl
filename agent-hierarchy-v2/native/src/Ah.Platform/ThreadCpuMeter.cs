using Ah.Abstractions;

namespace Ah.Platform;

/// <summary>An <see cref="ICpuMeter"/> over the thread CPU clock. One stretch is open at a time, as the job it meters runs one body at a time.</summary>
internal sealed class ThreadCpuMeter : ICpuMeter
{
    private long _ns;
    private long _mark = -1;
    private int _unavailable;

    public void Measure(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        OpenStretch();
        try
        {
            body();
        }
        finally
        {
            CloseStretch();
        }
    }

    public void OpenStretch() => _mark = PlatformModule.ThreadCpuNs() ?? -1;

    public void CloseStretch()
    {
        var from = _mark;
        _mark = -1;
        if (from < 0)
        {
            return;
        }

        if (PlatformModule.ThreadCpuNs() is { } to)
        {
            _ = Interlocked.Add(ref _ns, to - from);
        }
        else
        {
            _ = Interlocked.Exchange(ref _unavailable, 1);
        }
    }

    public long? Take()
    {
        var ns = Interlocked.Exchange(ref _ns, 0);
        return Interlocked.Exchange(ref _unavailable, 0) == 1 || PlatformModule.ThreadCpuNs() is null ? null : ns;
    }
}
