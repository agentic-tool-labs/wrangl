using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>Maps the rows the watch reads onto the plain facts of <see cref="EtaClock"/>.</summary>
internal static class EtaState
{
    public static EtaStateResult Compute(
        string requestId,
        string eta,
        double thresholdSeconds,
        string? created,
        string? toAddr,
        double floorMs,
        string sessionId,
        IReadOnlyList<JsonObject> peerRecords,
        IReadOnlyList<JsonObject> gateRows,
        bool answered,
        double? heldUntilMs,
        double nowMs,
        double pollMs) =>
        HierarchyModule.ComputeEtaState(
            new EtaFacts(
                requestId,
                eta,
                thresholdSeconds,
                created,
                floorMs,
                Liveness.Heard(peerRecords, sessionId, toAddr),
                Liveness.GateTimes(gateRows, "liveness-nudge", sessionId, requestId),
                Liveness.GateTimes(gateRows, "liveness-checked", sessionId, requestId),
                answered,
                heldUntilMs),
            nowMs,
            pollMs);
}
