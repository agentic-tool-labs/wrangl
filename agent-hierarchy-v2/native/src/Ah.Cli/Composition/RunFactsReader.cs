using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// The run facts a run-scoped link reads, over the hierarchy's exchange listing, the team record and the run anchor's resolver:
/// the one implementation both the daemon and the CLI hand to the link commands and the send path.
/// </summary>
internal sealed class RunFactsReader(HierarchyServices hierarchy, IFileReader files, IRosterCore roster, IConfigCore config) : IRunFacts
{
    public RunAnchor? Anchor(string hierarchyDir, string team)
    {
        if (roster.ReadTeamRecord(hierarchyDir, team).Member("orchestrator")?.Member("session_id") is not { IsText: true } session)
        {
            return null;
        }

        var read = HierarchyModule.ReadAuthority(hierarchy, files, hierarchyDir, team, session.AsString());
        return read.Run is { IsText: true } id ? new RunAnchor(id.AsString(), read.Mode == "surrogate") : null;
    }

    public string? SlugOf(string hierarchyDir, string team, string taskId) =>
        hierarchy.Messages.ListExchangesAllTeams(hierarchyDir)
            .FirstOrDefault(e => e.Member("id") is { IsText: true } id && id.AsString() == taskId
                && e.Member("request")?.Member("path") is { IsText: true } path
                && hierarchy.Core.ReadMsgFile(path.AsString()).Member("fm") is { IsMap: true } fm
                && fm.Member("team") is { IsText: true } t && t.AsString() == team)
            .Member("slug") is { IsText: true } slug ? slug.AsString() : null;

    public string? NewestRequestTo(string hierarchyDir, string team, string slug, string roleClass)
    {
        var resolved = config.ResolveConfig(ConfigRoot(hierarchyDir));
        string? newestId = null;
        string? newestTo = null;
        foreach (var (id, fm) in RequestsOf(hierarchyDir, team, slug))
        {
            if (fm.Get("to") is { IsText: true } role && config.RoleClass(role.AsString(), resolved) == roleClass
                && (newestId is null || string.CompareOrdinal(id, newestId) > 0))
            {
                newestId = id;
                newestTo = fm.Get("to_name") is { IsText: true } name ? name.AsString() : null;
            }
        }

        return newestTo;
    }

    public bool OpenFor(string hierarchyDir, string team, string slug, string member) =>
        hierarchy.Messages.ListLiveExchangesAllTeams(hierarchyDir).Any(e =>
            e.Member("slug") is { IsText: true } s && s.AsString() == slug && e.Member("open") is { IsTruthy: true }
            && e.Member("request")?.Member("path") is { IsText: true } path
            && hierarchy.Core.ReadMsgFile(path.AsString()).Member("fm") is { IsMap: true } fm
            && fm.Member("to_name") is { IsText: true } to && to.AsString() == member
            && fm.Member("team") is { IsText: true } t && t.AsString() == team);

    private IEnumerable<(string Id, JsonObject Fm)> RequestsOf(string hierarchyDir, string team, string slug)
    {
        foreach (var e in hierarchy.Messages.ListExchangesAllTeams(hierarchyDir))
        {
            if (e.Member("slug") is { IsText: true } s && s.AsString() == slug && e.Member("id") is { IsText: true } id
                && e.Member("request")?.Member("path") is { IsText: true } path
                && hierarchy.Core.ReadMsgFile(path.AsString()).Member("fm") is { IsMap: true } fm
                && fm.Member("team") is { IsText: true } t && t.AsString() == team)
            {
                yield return (id.AsString(), fm.AsObject());
            }
        }
    }

    /// <summary>The repository the hierarchy directory (<c>&lt;repo&gt;/.claude/ah2</c>) belongs to, where the role registry is resolved from.</summary>
    private static string ConfigRoot(string hierarchyDir) =>
        Path.GetDirectoryName(Path.GetDirectoryName(hierarchyDir.TrimEnd('/'))) ?? hierarchyDir;
}
