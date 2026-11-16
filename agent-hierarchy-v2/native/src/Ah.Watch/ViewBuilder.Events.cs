using Ah.Core.Json;

namespace Ah.Watch;

internal static partial class ViewBuilder
{
    private const int SnapshotEntriesPerBox = 20;

    /// <summary>The facts events are found from, read once across every orchestrator and team.</summary>
    internal static ViewSnapshot EventSnapshot(IViewSource src, double now, IReadOnlyList<string>? extraDirs = null, Func<HerdrReading>? herdr = null)
    {
        ArgumentNullException.ThrowIfNull(src);
        var session = new BuildSession(src, now, extraDirs, herdr);
        var snapshot = new ViewSnapshot();
        foreach (var orch in session.Orchs)
        {
            var oPath = $"/o/{Seg(orch.Oid)}";
            foreach (var team in orch.Teams)
            {
                var tPath = $"{oPath}/t/{Seg(team.Tkey)}";
                var facts = session.FactsOf(orch, team);
                _ = snapshot.TeamFiles.Add(team.File);
                foreach (var member in facts.Members)
                {
                    var name = member.Str("name")!;
                    var path = $"{tPath}/m/{Seg(name)}";
                    var live = member.TryGetValue("live", out var l) && l.Kind == JsonKind.Bool && l.AsBool();
                    snapshot.Members[path] = new ViewSnapshot.MemberFact(name, live, member.Str("activity") ?? "unknown");
                    AddBox(src, snapshot, team, name, path);
                }

                AddBox(src, snapshot, team, "orchestrator", oPath);
                foreach (var stream in facts.Work.Streams)
                {
                    snapshot.Streams[$"{tPath}/s/{Seg(stream.Name)}"] = new ViewSnapshot.StreamFact(stream.Name, stream.GlyphState == "done" || stream.Rec.Str("state") == "done");
                }

                foreach (var join in facts.Work.Joins)
                {
                    if (join.Str("id") is { Length: > 0 } id)
                    {
                        _ = snapshot.Joins.Add(id);
                    }
                }
            }
        }

        return snapshot;
    }

    private static void AddBox(IViewSource src, ViewSnapshot snapshot, TeamEntry team, string member, string nodePath)
    {
        _ = snapshot.Boxes.Add(nodePath);
        _ = snapshot.BoxDirs.Add(src.BoxDirFor(team.File, member));
        foreach (var e in src.BoxEntries(src.BoxDirFor(team.File, member), SnapshotEntriesPerBox, index: true))
        {
            if (e.Str("id") is not { } id)
            {
                continue;
            }

            snapshot.Mail[nodePath + "|" + id] = new ViewSnapshot.MailFact(
                nodePath,
                id,
                e.Str("from") ?? "?",
                e.Str("to") ?? "?",
                e.Str("kind") ?? "mail",
                ParseMs(e.Str("created")),
                e.TryGetValue("acked", out var a) && a.Kind == JsonKind.Bool && a.AsBool());
        }
    }
}
