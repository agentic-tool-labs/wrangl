using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>
/// The <c>member-tmp</c> doctor row: every member of the team with a pane has a recorded tmp that exists, is ignored by git and is
/// its own. It reads the team file's <c>tmp</c> records and never a live member's environment; it is never red.
/// </summary>
internal static class MemberTmpDoctor
{
    internal static (string Status, string Detail)? Row(RosterContext c)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.Member("members") is not { IsArray: true } list)
        {
            return null;
        }

        var members = list.AsArray().Where(m => m.IsMap && m.Member("transport_id") is { IsNull: false, IsTruthy: true } && m.Member("name") is { IsText: true } && global::Ah.Roster.RosterModule.StartingAgeSeconds(m, c.Services.Clock.UtcNow) is null).ToList();
        if (members.Count == 0)
        {
            return null;
        }

        var files = c.Services.Files;
        var problems = new List<string>();
        var byPath = members
            .Where(m => m.Member("tmp") is { IsText: true } t && !t.AsString().StartsWith("none (", StringComparison.Ordinal))
            .GroupBy(m => m.Member("tmp")!.Value.AsString(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Member("name")!.Value.AsString()).ToList(), StringComparer.Ordinal);
        foreach (var member in members)
        {
            var name = member.Member("name")!.Value.AsString();
            if (member.Member("tmp") is not { IsText: true } recorded)
            {
                problems.Add($"{name}: unknown (spawned before tmp was recorded)");
                continue;
            }

            var path = recorded.AsString();
            if (path.StartsWith("none (", StringComparison.Ordinal))
            {
                problems.Add($"{name}: {path}");
            }
            else if (!files.Exists(path))
            {
                problems.Add($"{name}: tmp {path} is missing");
            }
            else if (c.Services.Scratch?.IsIgnored(c.TeamHome, NodePath.Join(path, ".probe")) != true)
            {
                problems.Add($"{name}: tmp {path} is not ignored by git");
            }
            else if (byPath.TryGetValue(path, out var sharers))
            {
                problems.Add($"{name}: tmp {path} is shared with {string.Join(", ", sharers.Where(s => s != name))}");
            }
        }

        return problems.Count == 0
            ? ("ok", $"{members.Count} member(s) each have their own existing, git-ignored tmp")
            : ("warn", string.Join("; ", problems));
    }
}
