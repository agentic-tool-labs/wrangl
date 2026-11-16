using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The peer-log questions of a hook answered from this process's own read of the peer log.</summary>
internal sealed class LocalPeerLatest(IHierarchyPeers peers) : IPeerLatest
{
    /// <inheritdoc />
    public IReadOnlyList<JsonValue> PendingFor(JsonValue? sessionId) => peers.PendingFor(sessionId);

    /// <inheritdoc />
    public bool HasPending(JsonValue? sessionId) => peers.PendingFor(sessionId).Count > 0;
}
