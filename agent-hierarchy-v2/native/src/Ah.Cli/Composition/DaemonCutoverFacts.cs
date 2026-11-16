using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Cli.Composition;

/// <summary>
/// The facts of <see cref="ICutoverFacts"/> read from the mailbox, the team records of the registered hierarchy directories and the
/// process table. The CLI's decision verbs build it over their own service graph.
/// </summary>
internal sealed class DaemonCutoverFacts(
    ServiceGraph graph, IProcessTable table, Func<IReadOnlyList<string>> hierarchyDirs) : ICutoverFacts
{
    public string? TeamFileEntry(string sessionId, string address)
    {
        var registration = graph.Mailbox.LiveRegistrations().FirstOrDefault(r => r.Get("session_id") is { IsText: true } id && id.AsString() == sessionId);
        return registration?.Get("team_files") is { IsMap: true } files && files.AsObject().Get(address) is { IsText: true } path ? path.AsString() : null;
    }

    public JsonValue ReadTeamFile(string path) => graph.Roster.ReadTeamFileAt(path);

    public bool ProcessIs(int pid, string pidStart) => ProcessIdentity.Same(table, pid, pidStart);

    public bool IsMemberProcess(int pid)
    {
        foreach (var dir in hierarchyDirs())
        {
            foreach (var name in graph.Roster.ListTeamNames(dir))
            {
                if (graph.Roster.ReadTeamRecord(dir, name) is { IsMap: true } record
                    && record.AsObject().Get("members") is { IsArray: true } members
                    && members.AsArray().Any(m => m.IsMap && NamesPid(m.AsObject(), pid)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool ManagerNamesLane(string hierDir, string team) =>
        graph.Roster.ListTeamNames(hierDir).Any(manager => graph.Roster.ReadTeamRecord(hierDir, manager) is { IsMap: true } record
            && Ah.Hierarchy.HierarchyModule.ManagerNamesLane(manager, record, team));

    internal static bool NamesPid(JsonObject row, int pid) =>
        (row.Get("pid") is { IsNumber: true } own && own.AsNumber() == pid)
        || (row.Get("bare") is { IsMap: true } bare && bare.AsObject().Get("pid") is { IsNumber: true } barePid && barePid.AsNumber() == pid);
}
