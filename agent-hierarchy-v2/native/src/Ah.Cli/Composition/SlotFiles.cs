using Ah.Abstractions;
using Ah.Mesh;
using Ah.Watch;

namespace Ah.Cli.Composition;

internal sealed class SlotFiles(ServiceGraph graph, Action<string>? log)
{
    private IProcessTable? _table;

    public SlotState? StateOf(string session) => WatchModule.SlotOf(Read(), session);

    /// <summary>The slot records, read once; ask about each member with <see cref="WatchModule.SlotOf"/>.</summary>
    public IReadOnlyList<SlotRecord> Read()
    {
        _table ??= Ah.Platform.PlatformModule.CreateProcessTable(graph.Runner);
        return WatchModule.SlotRecords(graph.Io.Files, graph.Environment, _table, pid => MeshModule.CallerSessionIds(_table, pid, graph.Mailbox), log);
    }
}
