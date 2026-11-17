namespace Ah.Watch;

/// <summary>
/// The session ids whose permission-blocked record the watcher's blocked pass reads (ah2-102, adr-23), shared in process between the
/// watch tick, which publishes them, and the read model, which shows a member pending its read only while it is in this set.
/// </summary>
public interface IWatchCoverage
{
    /// <summary>The set as last published; empty until the first publish.</summary>
    IReadOnlySet<string> Sessions { get; }

    /// <summary>Replaces the set; the read model is told only when it differs from the last one published.</summary>
    void Publish(IReadOnlySet<string> sessions);
}
