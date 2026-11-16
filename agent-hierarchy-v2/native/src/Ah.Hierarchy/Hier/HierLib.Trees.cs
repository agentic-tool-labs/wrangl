using Ah.Config;

namespace Ah.Hierarchy;

internal sealed partial class HierLib
{
    public IReadOnlyList<TreeFacts> TreeFactsOfTeam(string dir, string team)
    {
        if (ReadTeamOrNull(dir, team) is not { } record)
        {
            return [];
        }

        GrantsView? view = null;
        try
        {
            view = ConfigModule.CreateWorktreeGrants(_files, _environment).Read(dir, team);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // a grants document that cannot be read leaves the team on its team-file roots
        }

        string? TopOf(string? path) => string.IsNullOrEmpty(path) ? null : Exists(path) ? GitTop(path) ?? path : path;

        var teamRoot = TextOf(record.Member("expected_root"));
        var members = new List<TreeMember>();
        if (record.Member("members") is { IsArray: true } list)
        {
            foreach (var m in list.AsArray())
            {
                if (TextOf(m.Member("name")) is not { Length: > 0 } name)
                {
                    continue;
                }

                var root = view is { Present: true } && view.Members.TryGetValue(name, out var granted) ? granted.WorkRoot : TextOf(m.Member("expected_root")) ?? teamRoot;
                var live = HierarchyModule.AttributedRunning(AttributedLivenessIn(dir, name, team));
                members.Add(new TreeMember(name, TextOf(m.Member("role")), TextOf(m.Member("stream")), TopOf(root), live));
            }
        }

        var streams = new Dictionary<string, string>(StringComparer.Ordinal);
        if (record.Member("streams") is { IsMap: true } streamMap)
        {
            var objects = streamMap.AsObject();
            foreach (var key in objects.Keys)
            {
                if (TopOf(TextOf(objects[key].Member("worktree"))) is { } worktree)
                {
                    streams[key] = worktree;
                }
            }
        }

        return TreeOwnership.Compute(members, streams, _config.IsPlaced);
    }
}
