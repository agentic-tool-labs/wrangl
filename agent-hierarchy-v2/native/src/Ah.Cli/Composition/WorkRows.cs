using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Cli.Composition;

/// <summary>What the read model serves of the work registry (§10), through the reads the CLI uses.</summary>
internal static class WorkRows
{
    internal const int MaxEntries = 200;

    private static T Read<T>(ServiceGraph g, string ahHome, Func<Func<string, object?[], IReadOnlyList<object?[]>>, T> body, T empty)
    {
        if (g.OpenReader(ahHome).Reader is not { } reader)
        {
            return empty;
        }

        using (reader)
        {
            return body(reader.Query);
        }
    }

    /// <summary>The registered items of one stream, by the key their slice serves: <c>stream:item</c> on a plan, the bare item on the backlog, which an empty team names. An item not yet registered has no card.</summary>
    internal static IReadOnlyDictionary<string, WorkCard> Cards(ServiceGraph g, string ahHome, string dir, string team, string stream) =>
        Read<IReadOnlyDictionary<string, WorkCard>>(
            g,
            ahHome,
            q =>
            {
                if (HierarchyModule.WorkPrefix(q, dir) is not { } prefix)
                {
                    return new Dictionary<string, WorkCard>();
                }

                var board = HierarchyModule.WorkBoardOf(q, g.Hierarchy.Progress, dir);
                return board.Items.Where(v => v.Row.Team == team && v.Row.Stream == stream).ToDictionary(
                    v => team.Length == 0 ? v.Row.Item : stream + ":" + v.Row.Item,
                    v => new WorkCard(HierarchyModule.WorkId(prefix, v.Row.N), v.Row.ExtId, v.Row.ExtUrl, board.OpenBlockers(v.Row.N).Count),
                    StringComparer.Ordinal);
            },
            new Dictionary<string, WorkCard>());

    /// <summary>
    /// The <c>item-desc:</c> body of item <paramref name="n"/>: <c>{id, description}</c>, the description null when it has none. The store caps a body at
    /// 8 KiB, so this is small. Null when the directory has no registry.
    /// </summary>
    internal static JsonObject? ItemDesc(ServiceGraph g, string ahHome, string dir, long n) =>
        Read<JsonObject?>(
            g,
            ahHome,
            q => HierarchyModule.WorkPrefix(q, dir) is not { } prefix ? null : new JsonObject
            {
                ["id"] = HierarchyModule.WorkId(prefix, n),
                ["description"] = HierarchyModule.WorkDescription(q, dir, n) is { } description ? description : JsonValue.Null,
            },
            null);

    /// <summary>
    /// The <c>item-log:</c> body of item <paramref name="n"/>: <c>{id, description, entries: [{ts_ms, type, title, by, body}], more, links: {blocked_by, blocks}, attachments: [{ref, kind, title, sha}], children}</c>;
    /// the newest <see cref="MaxEntries"/> entries, oldest first, with <c>more</c> older ones left out. Null when there is no such item.
    /// </summary>
    internal static JsonObject? ItemLog(ServiceGraph g, string ahHome, string dir, long n) =>
        Read(
            g,
            ahHome,
            q =>
            {
                if (HierarchyModule.WorkPrefix(q, dir) is not { } prefix)
                {
                    return null;
                }

                var board = HierarchyModule.WorkBoardOf(q, g.Hierarchy.Progress, dir);
                if (board.Items.FirstOrDefault(v => v.Row.N == n) is not { } view)
                {
                    return null;
                }

                string Id(long other) => HierarchyModule.WorkId(prefix, other);
                static JsonArray Ids(IEnumerable<string> ids)
                {
                    var list = new JsonArray();
                    foreach (var id in ids)
                    {
                        list.Add(id);
                    }

                    return list;
                }

                var timeline = HierarchyModule.WorkTimeline(q, Ah.StateDb.StateDbModule.CreateProgressLogs(g.Environment.AhHome), dir, view, WorkVerb.MessageTitles(g, dir));
                var entries = new JsonArray();
                foreach (var entry in timeline.Skip(Math.Max(0, timeline.Count - MaxEntries)))
                {
                    entries.Add(WorkVerb.EntryJson(entry, body: true));
                }

                var attachments = new JsonArray();
                foreach (var attachment in HierarchyModule.WorkAttachments(q, dir, n))
                {
                    attachments.Add(WorkVerb.AttachmentJson(attachment));
                }

                return new JsonObject
                {
                    ["id"] = Id(n),
                    ["description"] = HierarchyModule.WorkDescription(q, dir, n) is { } description ? description : JsonValue.Null,
                    ["entries"] = entries,
                    ["more"] = Math.Max(0, timeline.Count - MaxEntries),
                    ["links"] = new JsonObject { ["blocked_by"] = Ids(board.BlockedBy(n).Select(Id)), ["blocks"] = Ids(board.Blocks(n).Select(Id)) },
                    ["attachments"] = attachments,
                    ["children"] = Ids(board.Children(view.Row).Select(c => Id(c.Row.N))),
                };
            },
            null);
}
