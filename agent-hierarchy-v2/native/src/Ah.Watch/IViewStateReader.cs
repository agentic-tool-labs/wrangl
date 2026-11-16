using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>
/// The one way a view build reads ah state: a state-domain read op, answered by the same dispatch that serves
/// <c>GET /v1/state/{domain}/{op}</c>. It exposes reads only; naming a write op is an error, never a dispatch.
/// </summary>
public interface IViewStateReader
{
    /// <summary>
    /// The result document of the read op <paramref name="domain"/>.<paramref name="op"/> for the hierarchy directory
    /// <paramref name="hierarchyDir"/> with <paramref name="args"/>. Throws <see cref="InvalidOperationException"/> when the op
    /// is unknown or is a write, or when the dispatch refuses or fails the request.
    /// </summary>
    JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args);
}
