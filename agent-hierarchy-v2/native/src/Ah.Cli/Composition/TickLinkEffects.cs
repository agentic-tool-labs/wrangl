using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Composition;

/// <summary>
/// The writes the watch tick's run-link revoke makes: the <c>links</c> state operations and the <c>link-ended</c> event. The tick
/// creates no messages and mirrors no notes, so those are not supported here, and a notice that is not an event is dropped.
/// </summary>
internal sealed class TickLinkEffects(ServiceGraph graph, RequestContext context, string cwd, string hierarchyDir, Func<DateTimeOffset> now) : ILinkEffects, ILinkMirror
{
    public Task<JsonObject> LinksAsync(string op, JsonObject args, string? opId, CancellationToken cancellation)
    {
        var result = graph.Writer.Invoke("links", op, JsonText.ToUtf8(args, 0), context, new StateCallOptions(opId ?? $"{op}-{Guid.NewGuid()}"));
        if (!result.Ok)
        {
            throw new InvalidOperationException($"links {op}: {result.ErrorCode ?? result.Error}");
        }

        var parsed = JsonText.Parse(result.ResultJson.ToArray());
        return Task.FromResult(parsed.Kind == JsonKind.Map ? parsed.AsObject() : new JsonObject());
    }

    public Task<bool> CreateMessageAsync(string dir, string path, string content, string opId, CancellationToken cancellation, bool ownPoolContext = false) =>
        throw new NotSupportedException("the watch tick creates no link messages");

    public Task NotifyAsync(LinkNotice notice, CancellationToken cancellation)
    {
        if (notice.Kind == "event" && notice.Event is { } name)
        {
            _ = graph.Mailbox.Send(MeshModule.EventRequest(notice.To, notice.From, cwd, hierarchyDir, name, notice.Text), now());
        }

        return Task.CompletedTask;
    }

    public Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation) => Task.CompletedTask;
}
