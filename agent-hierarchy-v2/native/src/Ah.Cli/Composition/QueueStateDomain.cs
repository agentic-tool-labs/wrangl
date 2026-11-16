using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>
/// The <c>queue</c> state domain: one read op, <c>read {dir, team}</c>, answering <c>{rows: [...]}</c> with the team's queued items as
/// <see cref="WatchModule.QueueRowToJson"/> writes them, the form the queue lane serves. Only the in-process view reader reaches it.
/// </summary>
internal sealed class QueueStateDomain : IStateDomain
{
    private readonly Func<ServiceGraph?> _graph;
    private readonly IClock _clock;

    internal QueueStateDomain(Func<ServiceGraph?> graph, IClock clock)
    {
        _graph = graph;
        _clock = clock;
        Ops = [new StateOp("read", OpKind.Read, null, false, null, false, Read)];
    }

    public string Name => "queue";

    public IReadOnlyList<string> StoreNames { get; } = ["queue"];

    public bool HomeScoped => false;

    public IReadOnlyList<StateOp> Ops { get; }

    public string StateDirectory(RequestContext context) => NodePath.Join(context.HierarchyDirectory, ".state-queue");

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    private OpResult Read(OpCall call, IStoreWriteContext? context)
    {
        try
        {
            var args = call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span);
            var dir = args.Member("dir") is { IsText: true } d ? d.AsString() : throw new InvalidOperationException("The \"dir\" argument must be of type string");
            var team = args.Member("team") is { IsText: true } t ? t.AsString() : throw new InvalidOperationException("The \"team\" argument must be of type string");
            var graph = _graph() ?? throw new InvalidOperationException("The queue is not composed yet");
            var rows = new JsonArray();
            foreach (var row in QueueRows.Of(graph, dir, team, _clock.UtcNow.ToUnixTimeMilliseconds()))
            {
                rows.Add(WatchModule.QueueRowToJson(row));
            }

            return new OpResult(true, JsonText.ToUtf8(new JsonObject { ["rows"] = rows }, 0));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
        }
    }
}
