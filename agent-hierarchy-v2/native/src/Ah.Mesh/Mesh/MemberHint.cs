using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>
/// The commands a hint names to bring back a member whose process is gone: never chained, never one that closes a pane holding a live
/// session, abandons a brief or stops a job (ah2-593 Amendments 1 and 2).
/// </summary>
internal static partial class MemberHint
{
    [System.Text.RegularExpressions.GeneratedRegex("^" + Ah.Config.ConfigModule.RoleNamePattern + "\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex RoleRe();

    /// <summary>
    /// Whether a checkpoint <paramref name="record"/> lists <paramref name="member"/> and so does the snapshot it names (read through
    /// <paramref name="roster"/>), which the resume relaunches from: only then does the resume command apply to it (ah2-613). A snapshot
    /// that cannot be read lists no one.
    /// </summary>
    internal static bool Lists(JsonValue? record, string member, Ah.Roster.IRosterCore roster)
    {
        if (!Names(record, member) || record?.Member("snapshot") is not { IsText: true } snapshot)
        {
            return false;
        }

        try
        {
            return Names(roster.ReadSnapshot(snapshot.AsString()), member);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool Names(JsonValue? doc, string member) =>
        doc?.Member("members") is { IsArray: true } members
        && members.AsArray().Any(m => m.Member("name") is { IsText: true } n && n.AsString() == member);

    /// <summary>The member back on its old session from the team's checkpoint; it only plans until the user agrees to <c>--yes</c>.</summary>
    internal static string Resume(string team, string member) => $"ah roster resume --checkpoint --team {team} --relaunch {member}";

    /// <summary>A fresh member of <paramref name="role"/> (<c>&lt;role&gt;</c> when it is not a role name), on <paramref name="stream"/> when it is a valid stream name.</summary>
    internal static string Respawn(string role, string? stream, string team) =>
        $"ah roster spawn-one {(RoleRe().IsMatch(role) ? role : "<role>")}{(stream is { } s && Ah.Config.ConfigModule.IsStreamName(s) ? $" --stream {s}" : string.Empty)} --team {team}";

    /// <summary>The hint's lines: the resume (when given), the fresh spawn, and the old session id as information (when given).</summary>
    internal static IReadOnlyList<string> Lines(string? resume, string respawn, string? session)
    {
        var lines = new List<string>();
        if (resume is not null)
        {
            lines.Add($"Back on its old session (ask the user first; add --yes only once they agree): `{resume}`");
        }

        lines.Add($"Or a fresh member: `{respawn}`");
        if (session is not null)
        {
            lines.Add($"Its old session id: {session}");
        }

        return lines;
    }
}
