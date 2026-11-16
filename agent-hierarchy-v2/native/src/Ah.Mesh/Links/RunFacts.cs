using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>A team's single open pipeline run anchor and whether its grant is <c>surrogate</c>, re-verified against the user's recorded answer.</summary>
public sealed record RunAnchor(string Id, bool Surrogate);

/// <summary>
/// What a run-scoped link needs to know about the pipeline run, read-only. A link auto-granted inside a run carries the run anchor's
/// id; it lives only while that anchor is the team's open one, both members are on the team, and some request on its slug is still
/// open for either member.
/// </summary>
public interface IRunFacts
{
    /// <summary>The team's one open run anchor, or null when there is none, there are several, or the Orchestrator's session is unknown.</summary>
    RunAnchor? Anchor(string hierarchyDir, string team);

    /// <summary>The slug of the exchange <paramref name="taskId"/>, or null.</summary>
    string? SlugOf(string hierarchyDir, string team, string taskId);

    /// <summary>The <c>to_name</c> of the newest request with <paramref name="slug"/> addressed to a role of class <paramref name="roleClass"/> (<c>review</c>, <c>design</c> or <c>implement</c>); null when there is none or it names a role rather than a member.</summary>
    string? NewestRequestTo(string hierarchyDir, string team, string slug, string roleClass);

    /// <summary>Whether some request with <paramref name="slug"/> addressed to <paramref name="member"/> has no response yet.</summary>
    bool OpenFor(string hierarchyDir, string team, string slug, string member);
}

/// <summary>The one liveness rule of a run-scoped link, used by the send path and by the tick that revokes dead ones.</summary>
internal static class RunLinks
{
    /// <summary>Whether a link is live. A link without an <c>anchor</c> is not run-scoped and always passes.</summary>
    internal static bool Live(IRunFacts? facts, IMeshRule rule, Func<string, MeshTeam?> lookup, string dir, string team, JsonObject link)
    {
        if (link.Get("anchor") is not { IsText: true } anchor)
        {
            return true;
        }

        if (facts is null || facts.Anchor(dir, team) is not { } current || current.Id != anchor.AsString())
        {
            return false;
        }

        var members = Members(link);
        if (members.Count < 2 || members.Any(a => rule.Classify(a, lookup) is not { Kind: "member" } p || p.Team != team))
        {
            return false;
        }

        if (link.Get("task") is not { IsText: true } task || facts.SlugOf(dir, team, task.AsString()) is not { } slug)
        {
            return false;
        }

        return members.Any(a => rule.Classify(a, lookup).Name is { } name && facts.OpenFor(dir, team, slug, name));
    }

    private static List<string> Members(JsonObject link)
    {
        var all = new List<string>();
        if (link.Get("requester") is { IsText: true } r)
        {
            all.Add(r.AsString());
        }

        if (link.Get("peers") is { IsArray: true } peers)
        {
            all.AddRange(peers.AsArray().Where(static p => p.IsText).Select(static p => p.AsString()));
        }

        return all;
    }
}
