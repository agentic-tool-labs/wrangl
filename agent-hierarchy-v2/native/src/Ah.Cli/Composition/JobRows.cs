using Ah.Core.Json;
using Ah.Watch;
using Ah.Watch.Model;

namespace Ah.Cli.Composition;

/// <summary>The jobs of a named team as the web view's jobs slice lists them: the record files of one refresh, with the eta threshold of each in seconds.</summary>
internal static class JobRows
{
    internal static IReadOnlyList<JobRow> Of(ServiceGraph g, string hierarchyDir, string team) =>
        [.. WatchModule.JobsFromFiles(g.Hierarchy.Status, hierarchyDir, team).Select(j => RowOf(j, g.Hierarchy.Messages.ThresholdFor(JsonValue.FromString(j.Eta))))];

    internal static JobRow RowOf(JobFact j, double thresholdSeconds) => new(
        j.Id,
        j.Owner,
        j.Kind,
        j.Label,
        j.Pid,
        j.Eta,
        thresholdSeconds,
        j.SentAtMs,
        j.ExtendedAtMs,
        j.CapAtMs,
        j.EndedAtMs,
        j.Background,
        j.Slot?.Phase,
        j.Slot?.SinceMs);
}
