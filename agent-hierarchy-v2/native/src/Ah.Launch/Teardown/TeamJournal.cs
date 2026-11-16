using Ah.Core.Json;

namespace Ah.Launch;

/// <summary>The bounded <c>parent_actions</c> list of a lane child team: what its parent's owner did to it, newest last.</summary>
internal static class TeamJournal
{
    /// <summary>The most entries the list keeps; older ones are dropped.</summary>
    internal const int Keep = 20;

    /// <summary>The list of <paramref name="team"/> with <paramref name="entry"/> added at the end, cut to the last <see cref="Keep"/> entries.</summary>
    internal static JsonArray Appended(JsonValue team, JsonObject entry)
    {
        var all = team.Member("parent_actions") is { IsArray: true } existing ? existing.AsArray().ToList() : [];
        all.Add(entry);
        var list = new JsonArray();
        list.AddRange(all.Skip(Math.Max(0, all.Count - Keep)));
        return list;
    }
}
