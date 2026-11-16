namespace Ah.Watch;

/// <summary>
/// The event-driven read model of the view. Lanes emit key notices; a single projector re-reads each key through the state reader
/// and publishes the slices it feeds; a request reads only the published map, so it performs no state read and starts no build.
/// </summary>
public interface IReadModel : IDisposable
{
    /// <summary>The daemon instance this model's publish counter belongs to; a client that holds another epoch reloads the manifest.</summary>
    string Epoch { get; }

    /// <summary>A write happened for <paramref name="key"/> of <paramref name="lane"/> (a routed write, or a hook that observed one): queue a re-read.</summary>
    void Emit(string lane, string key);

    /// <summary>A write changed <paramref name="lane"/> without naming one key (a sweep): enumerate its key set and re-read each key.</summary>
    void EmitKeySet(string lane);

    /// <summary>
    /// The initial JSON document of <paramref name="view"/>: <c>{epoch, seq, building, slices, pending}</c>, built from the published
    /// map alone. Null when no such view exists.
    /// </summary>
    byte[]? Manifest(string view);

    /// <summary>
    /// The JSON document <c>{epoch, seq, resync, slices, removed}</c> of the changes after publish counter <paramref name="since"/> among
    /// the subscribed slice ids (all when null), waiting up to <paramref name="wait"/> for the first.
    /// </summary>
    Task<byte[]> PollAsync(long since, string? epoch, IReadOnlyCollection<string>? subscribed, TimeSpan wait, CancellationToken ct, IReadOnlyCollection<string>? fresh = null);

    /// <summary>
    /// One page of <c>GET /v1/list/&lt;slice-id&gt;</c>: the status and the JSON body. It reads only what the model has published, or the
    /// lane's own read op for a page that is not held; a page is never published. An unknown prefix is 404, a malformed query 400.
    /// </summary>
    Task<(int Status, byte[] Body)> ListAsync(string sliceId, IReadOnlyDictionary<string, string> query, CancellationToken ct);
}
