using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>
/// What two teammates are assigned to together: <paramref name="Key"/> names it (<c>item:&lt;id&gt;</c>, <c>slug:&lt;slug&gt;</c> or
/// <c>stream:&lt;name&gt;</c>) and <paramref name="Task"/> is the first member's open request under it, the task their link is filed on;
/// null for a shared stream, which has no request of its own.
/// </summary>
public sealed record SharedAssignment(string Key, string? Task);

/// <summary>
/// The one answer to "are these two members assigned together", read by the send rule (being assigned is the permission to message
/// each other) and by the tick that ends an assignment link once the answer is no.
/// </summary>
internal static class Assignments
{
    private sealed record Open(string Id, string? Slug, string? Item);

    /// <summary>
    /// The first shared assignment of members <paramref name="a"/> and <paramref name="b"/> of <paramref name="team"/>: open requests
    /// the Orchestrator dispatched to each under one checklist item or one slug; else a work stream both rows name that is not done.
    /// Only a dispatch counts: a request a member files itself, to itself or to a teammate, assigns nobody, and so does one filed by a
    /// lead of another team.
    /// </summary>
    internal static SharedAssignment? Shared(
        IHierarchyMessages messages, IHierarchyCore core, Func<string, MeshTeam?> lookup, string dir, string team, string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b)
        {
            return null;
        }

        try
        {
            // one pass: each open request's front matter is read once and kept only when it is a dispatch to a or to b
            var mine = new List<Open>();
            var theirs = new List<Open>();
            foreach (var e in messages.ListLiveExchangesAllTeams(dir))
            {
                if (e.Member("open") is { IsTruthy: true } && e.Member("id") is { IsText: true } id
                    && e.Member("request")?.Member("path") is { IsText: true } path
                    && core.ReadMsgFile(path.AsString()).Member("fm") is { IsMap: true } fm
                    && fm.Member("to_name") is { IsText: true } to && (to.AsString() == a || to.AsString() == b)
                    && fm.Member("from") is { IsText: true } from && from.AsString() is "orchestrator" or "sub-orchestrator" or "deputy-chief"
                    && OfTeam(fm, team) && LedBy(fm, team))
                {
                    (to.AsString() == a ? mine : theirs).Add(new Open(
                        id.AsString(),
                        e.Member("slug") is { IsText: true } slug ? slug.AsString() : null,
                        fm.Member("item") is { IsText: true } item && item.AsString() is not ("" or "null") ? item.AsString() : null));
                }
            }

            foreach (var x in mine)
            {
                foreach (var y in theirs)
                {
                    if (x.Item is { Length: > 0 } item && item == y.Item)
                    {
                        return new SharedAssignment("item:" + item, x.Id);
                    }

                    if (x.Slug is { Length: > 0 } slug && slug == y.Slug)
                    {
                        return new SharedAssignment("slug:" + slug, x.Id);
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // requests that cannot be read (a file, the database) share nothing, so the send is judged as before; the stream below still answers
        }

        return Stream(lookup(team)?.Data, a, b) is { } stream ? new SharedAssignment("stream:" + stream, null) : null;
    }

    /// <summary>
    /// Whether a request is <paramref name="team"/>'s: it names that team, or names none. Member names carry their team's prefix, so a
    /// dispatch filed with no team (the default one) to a member of this team is this team's.
    /// </summary>
    private static bool OfTeam(JsonValue fm, string team) =>
        fm.Member("team") is not { IsText: true } named || named.AsString() is "" or "null" || named.AsString() == team;

    /// <summary>
    /// Whether the request's filer leads <paramref name="team"/> (ah2-49): <c>from_leads</c> names the teams whose <c>orchestrator@</c>
    /// or <c>chief@</c> box the filer held, so a sub-orchestrator in its parent team or an off-duty deputy assigns nobody. A request
    /// filed before the field existed has none and counts as it did.
    /// </summary>
    private static bool LedBy(JsonValue fm, string team) =>
        fm.Member("from_leads") is not { IsText: true } leads || leads.AsString().Split(',').Contains(team);

    /// <summary>The work stream both member rows of <paramref name="teamData"/> name, unless the team records it as done; null otherwise.</summary>
    internal static string? Stream(JsonValue? teamData, string? a, string? b)
    {
        if (teamData is not { IsMap: true } data || data.Member("members") is not { IsArray: true } rows || string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b)
        {
            return null;
        }

        string? StreamOf(string name) =>
            rows.AsArray().FirstOrDefault(m => m.IsMap && m.Member("name") is { IsText: true } n && n.AsString() == name) is { IsMap: true } row
            && row.Member("stream") is { IsText: true } s && s.AsString().Length > 0
                ? s.AsString()
                : null;

        var stream = StreamOf(a);
        if (stream is null || stream != StreamOf(b))
        {
            return null;
        }

        var done = data.Member("streams") is { IsMap: true } streams && streams.AsObject().Get(stream) is { IsMap: true } record
            && record.Member("state") is { IsText: true } state && state.AsString() == "done";
        return done ? null : stream;
    }
}
