using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Hooks.Plumbing;

/// <summary>
/// The peer-log questions of a hook asked of the running daemon, which keeps the peer log parsed. Any answer that is not a clean 200
/// is answered by the local backing instead, with one line in the fallback log saying why; a failed ask never stands for an empty
/// result. Nothing is kept between calls: each question reads the log as it is.
/// </summary>
internal sealed class DaemonPeerLatest(IDaemonClient daemon, string hierarchyDir, IPeerLatest local, TimeSpan timeout) : IPeerLatest
{
    internal const string Verb = "peers-pending";

    /// <inheritdoc />
    public IReadOnlyList<JsonValue> PendingFor(JsonValue? sessionId) =>
        Ask(sessionId, count: false) is { Kind: JsonKind.Array } rows ? [.. rows.AsArray()] : local.PendingFor(sessionId);

    /// <inheritdoc />
    public bool HasPending(JsonValue? sessionId) =>
        Ask(sessionId, count: true) is { Kind: JsonKind.Number } count ? count.AsNumber() > 0 : local.HasPending(sessionId);

    /// <summary>The reply's <c>rows</c> array (or <c>count</c> number), or null when the daemon did not give a clean answer; a session that is not text is never asked about.</summary>
    private JsonValue? Ask(JsonValue? sessionId, bool count)
    {
        if (sessionId is not { IsText: true } session || session.AsString().Length == 0)
        {
            return null;
        }

        var path = $"/v1/peers/pending?dir={Uri.EscapeDataString(hierarchyDir)}&session={Uri.EscapeDataString(session.AsString())}{(count ? "&count=1" : string.Empty)}";
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var reply = daemon.RequestAsync(new DaemonRequest("GET", path, Timeout: timeout, StartIfDown: false), cts.Token).GetAwaiter().GetResult();
            if (reply.Status == 200)
            {
                if (JsonText.TryParse(reply.Body.Span, out var body) && body.IsMap && body.Member(count ? "count" : "rows") is { } answer
                    && answer.Kind == (count ? JsonKind.Number : JsonKind.Array))
                {
                    return answer;
                }

                daemon.RecordFallback(Verb, "parse");
                return null;
            }

            daemon.RecordFallback(Verb, reply.TimedOut ? "timeout" : reply.FallbackReason is { Length: > 0 } why ? why : $"status={reply.Status}");
            return null;
        }
        catch (OperationCanceledException)
        {
            daemon.RecordFallback(Verb, "timeout");
            return null;
        }
    }
}
