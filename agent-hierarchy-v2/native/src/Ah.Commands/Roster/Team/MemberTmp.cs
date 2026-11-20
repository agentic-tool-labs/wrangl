using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Codex;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>A member's tmp as made at spawn: the real directory, the alias exported as <c>TMPDIR</c> (empty when none), and the kind.</summary>
internal sealed record PreparedTmp(string Root, string Alias, string Kind, string? Reason = null)
{
    /// <summary>Whether a directory was made; false for a resume that went on without one.</summary>
    internal bool Made => Reason is null;

    /// <summary>What the member's team-file row records: its path, or <c>none (reason)</c>.</summary>
    internal string Recorded => Made ? Root : $"none ({Reason})";

    /// <summary>The <c>TMPDIR</c> a Claude member exports: the short alias, else the real directory (a long path still beats the system default, which the write gate does not allow).</summary>
    internal string Export => Alias.Length > 0 ? Alias : Root;
}

/// <summary>
/// The per-member tmp directory: made at spawn when the team's grants are in force, recorded in the grants document, and removed
/// when a close of the member verifies (a <c>_dismissed/</c> entry an older version left is deleted 72 hours on by the next spawn,
/// dismissal or scratch sweep). A Claude
/// member's tmp is the real directory <c>&lt;ah home&gt;/t/&lt;key&gt;</c>, outside every checkout; a Codex
/// member's is the adapter's own directory. A record an older version made names
/// <c>&lt;team home&gt;/tmp/&lt;team&gt;/&lt;member&gt;</c> (or <c>&lt;team home&gt;/tmp/&lt;member&gt;</c>) with a link under the ah home; it keeps working and is removed the same way.
/// </summary>
internal static class MemberTmp
{
    /// <summary>The longest alias path, in bytes, that leaves room for a socket file name under the 104-byte limit.</summary>
    internal const int AliasBudget = 60;

    /// <summary>How long a dismissed tmp, or the tmp of a team with no live member, is kept before a sweep deletes it.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(72);

    /// <summary>The bytes a close of <paramref name="members"/> would remove: the recorded tmp sizes added up.</summary>
    internal static long PlanBytes(RosterContext c, IEnumerable<string> members) =>
        members.Select(m => Describe(c, m)?.Get("size_bytes")).Where(s => s is { IsNumber: true }).Sum(s => (long)s!.Value.AsNumber());

    internal static string TmpRoot(RosterContext c) => NodePath.Join(c.TeamHome, "tmp");

    internal static string AliasRoot(RosterContext c) => NodePath.Join(c.Services.Environment.AhHome, "t");

    /// <summary>The alias names a member's tmp may take, shortest first: 8, 12 and 16 hex characters of SHA-256(team id, 0, member).</summary>
    internal static IReadOnlyList<string> Keys(string teamId, string member)
    {
        var bytes = new List<byte>(Encoding.UTF8.GetBytes(teamId)) { 0 };
        bytes.AddRange(Encoding.UTF8.GetBytes(member));
        var hex = Convert.ToHexStringLower(SHA256.HashData([.. bytes]));
        return [hex[..8], hex[..12], hex[..16]];
    }

    private static string TeamId(RosterContext c)
    {
        var record = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        return record.Member("team_id") is { IsText: true } id ? id.AsString() : TeamName(c);
    }

    private static string TeamName(RosterContext c) => TeamDirName(c, c.TeamFile);

    /// <summary>The directory name of <paramref name="team"/> under the tmp root: the stem of its team file, unique within a team home.</summary>
    internal static string TeamDirName(RosterContext c, string? team) =>
        Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(c.TeamHome, team));

    /// <summary>The directory that holds this team's member tmp directories and its <c>_dismissed/</c>.</summary>
    internal static string TeamRoot(RosterContext c) => NodePath.Join(TmpRoot(c), TeamName(c));

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// The tmp a member about to launch gets, made now (<paramref name="create"/>) or only named. Every Claude member whose team home is in a
    /// git repository gets one, a real directory under the alias root (outside every checkout, so nothing is made in the repository), and
    /// the member's grants need no document. When it cannot be made a new spawn is refused; a <paramref name="resume"/> (or a create, once its preflight passed) goes on without it and the result says why
    /// (<see cref="PreparedTmp.Reason"/>). Null for a Codex member whose team has no grants in force, and for a dry run with no scratch operations.
    /// </summary>
    internal static PreparedTmp? Prepare(RosterContext c, string member, bool codex, bool create, List<string> warnings, bool resume = false, bool onCreate = false)
    {
        var services = c.Services;
        if (codex)
        {
            return WorkTreeAssignment.Active(c)
                ? new PreparedTmp(CodexModule.MemberTmpDir(services.Environment.AhHome, TeamName(c), member), string.Empty, "codex")
                : null;
        }

        var aliasRoot = AliasRoot(c);
        var keys = Keys(TeamId(c), member);
        var path = NodePath.Join(aliasRoot, keys[0]);
        if (services.Scratch is not { } scratch)
        {
            return create ? Fail(c, member, path, "scratch operations are not available", resume, onCreate, warnings) : null;
        }

        if (!create)
        {
            return new PreparedTmp(path, path, "claude");
        }

        try
        {
            SweepAll(c, scratch);
            if (!scratch.InGitRepository(c.TeamHome))
            {
                warnings.Add($"{member}: no member tmp and TMPDIR is not set: {IRosterScratch.NotGitRepository}");
                return new PreparedTmp(string.Empty, string.Empty, "none", IRosterScratch.NotGitRepository);
            }

            var made = scratch.MakeMemberDirectory(aliasRoot, keys);
            if (Bytes(made) > AliasBudget)
            {
                warnings.Add($"{member}: TMPDIR {made} is longer than {AliasBudget} bytes, so a socket made in it may not fit");
            }

            return new PreparedTmp(made, made, "claude");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Fail(c, member, path, e.Message, resume, onCreate, warnings);
        }
    }

    private static PreparedTmp Fail(RosterContext c, string member, string path, string reason, bool resume, bool onCreate, List<string> warnings)
    {
        var message = $"cannot create member tmp {path}: {reason}";
        if (resume)
        {
            warnings.Add($"{member}: {message}; {(onCreate ? "started" : "resumed")} with no TMPDIR");
            return new PreparedTmp(string.Empty, string.Empty, "none", reason);
        }

        throw c.Refuse(
            new JsonObject { ["refused"] = "member-tmp", ["member"] = member, ["path"] = path, ["detail"] = reason, ["message"] = message },
            message);
    }

    /// <summary>The tmp path <paramref name="member"/>'s team-file row records, or null.</summary>
    private static string? RowTmp(RosterContext c, string member) =>
        c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile).Member("members") is { IsArray: true } list
            ? list.AsArray().Where(m => m.Member("name") is { IsText: true } n && n.AsString() == member)
                .Select(m => m.Member("tmp") is { IsText: true } t ? t.AsString() : null).FirstOrDefault()
            : null;

    private static void SweepAll(RosterContext c, IScratchDirectories scratch)
    {
        var now = c.Services.Clock.UtcNow;
        scratch.SweepDismissed(c.TeamHome, TeamRoot(c), now, Retention);
        scratch.SweepDismissed(c.TeamHome, TmpRoot(c), now, Retention);
    }

    /// <summary>The <c>set_tmp</c> value of an assign for <paramref name="tmp"/>.</summary>
    internal static JsonObject Record(PreparedTmp tmp) =>
        new() { ["root"] = tmp.Root, ["alias"] = tmp.Alias, ["kind"] = tmp.Kind };

    /// <summary>The recorded tmp of <paramref name="member"/>: its path, size in bytes and kind; null when none is recorded.</summary>
    internal static JsonObject? Describe(RosterContext c, string member)
    {
        if (WorkTreeAssignment.RecordedTmp(c, member) is not { } tmp)
        {
            return null;
        }

        var size = c.Services.Scratch is { } scratch ? SafeSize(scratch, tmp.Root) : null;
        return new JsonObject
        {
            ["path"] = tmp.Root,
            ["kind"] = tmp.Kind,
            ["size_bytes"] = size is null ? JsonValue.Null : JsonValue.FromNumber(size.Value),
        };
    }

    private static long? SafeSize(IScratchDirectories scratch, string path)
    {
        try
        {
            return scratch.Size(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Dismisses <paramref name="member"/>'s tmp: removes its directory under the alias root, or for an older record a team-home tmp (never one outside the team's tmp root) and its alias while it still
    /// points there, and sweeps what was dismissed more than 72 hours ago. A Codex tmp is the adapter's and is left alone.
    /// Returns a warning when something could not be done, else null.
    /// </summary>
    internal static string? Dismiss(RosterContext c, string member)
    {
        var services = c.Services;
        if (services.Scratch is not { } scratch)
        {
            return null;
        }

        var top = TmpRoot(c);
        try
        {
            var realTop = services.Files.Exists(top) ? services.Files.RealPath(top) : top;
            // a team with no grants row records no tmp: the member's directory at its spawn path is still its own (b435)
            var spawned = NodePath.Join(realTop, TeamName(c), member);
            var keyed = NodePath.Join(AliasRoot(c), Keys(TeamId(c), member)[0]);
            var tmp = WorkTreeAssignment.RecordedTmp(c, member)
                ?? (services.Files.Exists(NodePath.Join(TeamRoot(c), member))
                    ? new Ah.Config.GrantTmp(spawned, Keys(TeamId(c), member).Select(k => NodePath.Join(AliasRoot(c), k)).FirstOrDefault(a => scratch.AliasTarget(a) == spawned) ?? string.Empty, "claude")
                    : RowTmp(c, member) == keyed ? new Ah.Config.GrantTmp(keyed, keyed, "claude") : null);
            if (tmp is not { Kind: "claude" })
            {
                return null;
            }

            if (Path.GetDirectoryName(tmp.Root) == AliasRoot(c).TrimEnd('/'))
            {
                scratch.RemoveMemberDirectory(AliasRoot(c), tmp.Root);
                SweepAll(c, scratch);
                return null;
            }

            var parent = Path.GetDirectoryName(tmp.Root);
            string? root = null;
            if (Path.GetFileName(tmp.Root) == member)
            {
                if (parent == NodePath.Join(realTop, TeamName(c)))
                {
                    root = TeamRoot(c);
                }
                else if (parent == realTop)
                {
                    root = top;
                }
            }

            if (root is null)
            {
                return $"the tmp of {member} ({tmp.Root}) is not under {TeamRoot(c)}; it was left in place";
            }

            if (tmp.Alias.Length > 0 && scratch.AliasTarget(tmp.Alias) is { } target && target == tmp.Root)
            {
                scratch.RemoveAlias(AliasRoot(c), tmp.Alias);
            }

            scratch.Dismiss(c.TeamHome, root, member, services.Clock.UtcNow);
            SweepAll(c, scratch);
            return null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return $"the tmp of {member} was not dismissed: {e.Message}";
        }
    }
}
