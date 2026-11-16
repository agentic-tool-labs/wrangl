using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>What the <c>ah backlog</c> verbs share with the plan view: the item's JSON shape and the origin repository.</summary>
public static partial class WatchModule
{
    /// <summary>A backlog item in the plan view's item shape; <paramref name="originUrl"/> builds the issue URL.</summary>
    public static JsonObject BacklogItemJson(PlanItem item, string? originUrl) => ViewBuilder.BacklogItemJson(item, originUrl);

    /// <summary>The origin remote's URL in a git config text; null when there is none.</summary>
    public static string? OriginUrl(string gitConfig) => PlanLinkage.OriginUrl(gitConfig);

    /// <summary><c>owner/repo</c> of a GitHub origin URL; null for any other remote.</summary>
    public static string? OriginRepo(string? originUrl) => PlanLinkage.OwnerRepo(originUrl);
}
