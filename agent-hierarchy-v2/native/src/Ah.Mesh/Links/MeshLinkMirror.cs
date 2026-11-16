using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>Writes the record notes of link commands through the mesh sender's mirror, under one caller's request context.</summary>
internal sealed class MeshLinkMirror(MeshSend send, RequestContext context) : ILinkMirror
{
    public Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        send.MirrorLinked(
            mirror.Link,
            mirror.Task,
            mirror.TaskHome,
            new MeshParty("member", mirror.FromAddress),
            new MeshParty("member", mirror.ToAddress, Role: mirror.ToRole),
            mirror.N is { } n ? n : JsonValue.Null,
            new JsonObject { ["id"] = mirror.EntryId, ["text"] = mirror.Text },
            context);
        return Task.CompletedTask;
    }
}
