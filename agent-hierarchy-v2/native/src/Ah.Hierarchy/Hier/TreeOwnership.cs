namespace Ah.Hierarchy;

/// <summary>One member as the tree definitions see it: the git top-level of its effective root, and whether its session is live.</summary>
internal sealed record TreeMember(string Name, string? Role, string? Stream, string? Root, bool Live);

/// <summary>
/// Who works in which work tree. The one place the definitions live: a member's tree, writer and read-only roles, owner,
/// co-tenants and cross-stream. Every surface that warns or refuses about trees asks this, with the same path comparison
/// (<c>same</c>) the misplaced check uses.
/// </summary>
internal static class TreeOwnership
{
    private static readonly HashSet<string> ReadOnlyRoles = new(StringComparer.Ordinal) { "reviewer", "architect", "ultra-advisor", "qa" };

    /// <summary>Whether a member of <paramref name="role"/> writes to its tree: every role but the read-only ones, and a member with no role.</summary>
    public static bool IsWriter(string? role) => role is null || !ReadOnlyRoles.Contains(role);

    /// <summary>
    /// The facts of every member, in the order given. <paramref name="streams"/> maps each stream to its worktree. A member with
    /// no root has no tree and no facts beyond its name.
    /// </summary>
    public static IReadOnlyList<TreeFacts> Compute(IReadOnlyList<TreeMember> members, IReadOnlyDictionary<string, string> streams, Func<string, string, bool> same)
    {
        var facts = new List<TreeFacts>(members.Count);
        foreach (var member in members)
        {
            if (member.Root is not { Length: > 0 } root)
            {
                facts.Add(new TreeFacts(member.Name, member.Role, member.Live, null, member.Stream, null, [], null, false));
                continue;
            }

            var onTree = members.Where(m => m.Root is { Length: > 0 } other && same(root, other)).ToList();
            var tenants = onTree.Where(m => m != member && m.Live).ToList();
            var owner = onTree.FirstOrDefault(m => m.Live && IsWriter(m.Role));
            var streamOfTree = streams.Where(kv => same(root, kv.Value)).Select(kv => kv.Key).FirstOrDefault();
            var cross = streamOfTree is not null && streamOfTree != member.Stream ? streamOfTree : null;
            var conflict = IsWriter(member.Role) && tenants.Any(t => IsWriter(t.Role));
            facts.Add(new TreeFacts(member.Name, member.Role, member.Live, root, member.Stream, owner?.Name, [.. tenants.Select(t => t.Name)], cross, conflict));
        }

        return facts;
    }
}
