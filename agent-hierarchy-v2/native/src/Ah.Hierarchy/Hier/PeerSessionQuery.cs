using Ah.Core.Json;

namespace Ah.Hierarchy;

public static partial class HierarchyModule
{
    /// <summary>
    /// The peer rows of <paramref name="sessionId"/>, narrowed to <paramref name="types"/> when it names some (none: any type), in log order.
    /// The rows are the cache's: a caller must only read them. A session id that is null or empty names no key range, so the whole log is
    /// returned and the caller's own test decides, as it did before the query existed.
    /// </summary>
    public static IReadOnlyList<JsonValue> SessionRows(this IHierarchyCore core, string? sessionId, params string[] types)
    {
        ArgumentNullException.ThrowIfNull(core);
        return string.IsNullOrEmpty(sessionId)
            ? core.PeerRecordsShared()
            : core.PeerRecordsWhere(new PeerQuery("session_id", sessionId, types.Length == 0 ? null : new HashSet<string>(types, StringComparer.Ordinal)));
    }
}
