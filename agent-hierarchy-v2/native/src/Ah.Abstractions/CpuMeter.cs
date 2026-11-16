namespace Ah.Abstractions;

/// <summary>
/// Adds up the CPU time of the thread that runs the stretches it is told about, for a job whose body does its work off the thread
/// the daemon started it on. Each stretch is read on one thread, so an <c>await</c> that resumes elsewhere must be bracketed by
/// <see cref="CloseStretch"/> and <see cref="OpenStretch"/>. Safe to read from another thread than the one measuring.
/// </summary>
public interface ICpuMeter
{
    /// <summary>Runs <paramref name="body"/> and adds the CPU its thread used.</summary>
    void Measure(Action body);

    /// <summary>Opens a stretch on the calling thread.</summary>
    void OpenStretch();

    /// <summary>Closes the stretch opened by <see cref="OpenStretch"/> and adds its CPU; does nothing when none is open.</summary>
    void CloseStretch();

    /// <summary>The CPU nanoseconds added since the last call, then starts over; null when no CPU clock is available.</summary>
    long? Take();
}
