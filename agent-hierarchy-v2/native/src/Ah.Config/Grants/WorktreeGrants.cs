using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>One folder a member may write besides its work tree, as the orchestrator granted it.</summary>
/// <param name="Path">The canonical real path at the moment of the grant.</param>
/// <param name="Note">Why the orchestrator granted it.</param>
/// <param name="For">The request id the grant is for; it ends when a response to that request is filed. Null: until dismissal.</param>
/// <param name="Outside">Granted with <c>--outside</c>: the path is neither in the repository family nor temp.</param>
/// <param name="At">When it was granted, as an ISO timestamp.</param>
/// <param name="Standing">Granted with <c>--standing</c>: a reviewer's grant that is kept until dismissal.</param>
public sealed record GrantExtra(string Path, string Note, string? For, bool Outside, string At, bool Standing = false);

/// <summary>A member's record: its work tree (canonical at assignment), its role and its extras.</summary>
public sealed record GrantMember(string WorkRoot, string? Stream, string? Role, IReadOnlyList<GrantExtra> Extra, GrantTmp? Tmp = null);

/// <summary>A member's role name and the class the configured roles give it; either is null when unknown.</summary>
/// <param name="Role">The role named in the member's grants record.</param>
/// <param name="Class">The class the configured roles give that role.</param>
public sealed record MemberRoleInfo(string? Role, string? Class);

/// <summary>
/// The member tmp directory recorded for a member: its real path, the short alias exported as <c>TMPDIR</c> (empty when there is
/// none), and its kind: <c>claude</c> for a team-home tmp, <c>codex</c> for the adapter's own directory.
/// </summary>
public sealed record GrantTmp(string Root, string Alias, string Kind = "claude");

/// <summary>A work tree of a member, for attributing a session by its working directory.</summary>
public readonly record struct GrantRoot(string Team, string Member, string Root);

/// <summary>
/// One team's grants document as it reads now. <see cref="Present"/> is false when there is none or it cannot be read
/// (<see cref="Problem"/> says which). <see cref="Consistent"/> is false when the audit log does not match the document's
/// recorded hash; the document is then read as <c>enforce</c> with no extras, never looser.
/// </summary>
public sealed record GrantsView(
    string Team,
    string Mode,
    bool Present,
    bool Consistent,
    string? Problem,
    IReadOnlyDictionary<string, GrantMember> Members);

/// <summary>
/// The grants records and their audit log. A reader of the document is this interface; the one writer is the
/// <c>worktree-grants</c> state domain behind it. The hash a document carries is a consistency check against accidents and
/// clumsy edits, not protection against a process that can rewrite both files.
/// </summary>
public interface IWorktreeGrants
{
    /// <summary><c>&lt;hierarchy dir&gt;/grants/&lt;team&gt;.json</c>.</summary>
    string DocumentPath(string hierarchyDir, string team);

    /// <summary>The user-level audit log, beside the hook error log.</summary>
    string AuditPath();

    /// <summary>The team's grants document, read with its consistency check.</summary>
    GrantsView Read(string hierarchyDir, string team);

    /// <summary>
    /// <see cref="Read"/> for any team of <paramref name="hierarchyDir"/> from one grants read, taken now: each call of the result
    /// returns what <see cref="Read"/> would for that team.
    /// </summary>
    Func<string, GrantsView> Reader(string hierarchyDir);

    /// <summary>Whether any team of the directory holds a document in mode <c>log</c> or <c>enforce</c>.</summary>
    bool HoldsActiveDocument(string hierarchyDir);

    /// <summary>The work tree of every member of every active document of the directory.</summary>
    IReadOnlyList<GrantRoot> MemberRoots(string hierarchyDir);

    /// <summary>
    /// The extras of <paramref name="member"/> that are in force: none when the document is inconsistent, and not those whose
    /// request already has a response filed (<paramref name="responseFiled"/> answers by request id).
    /// </summary>
    IReadOnlyList<GrantExtra> EffectiveExtras(GrantsView view, string member, Func<string, bool> responseFiled);

    /// <summary>The mode in force: <c>enforce</c> whenever the document is present but inconsistent.</summary>
    string EffectiveMode(GrantsView view);

    /// <summary>The repository family of a hierarchy directory: its main checkout and linked worktrees, canonical, from the git directory.</summary>
    IReadOnlyList<string> FamilyRoots(string hierarchyDir);

    /// <summary>The OS temp roots, canonical.</summary>
    IReadOnlyList<string> TempRoots();

    /// <summary>
    /// Whether <paramref name="canonicalPath"/> is control state no grant may name and no session may write: a <c>.git</c> entry,
    /// the user's ah home, plugin cache and settings, and each family root's <c>.claude/ah2</c>, settings and <c>.mcp.json</c>.
    /// </summary>
    bool IsProtected(string canonicalPath, IReadOnlyList<string> familyRoots);

    /// <summary>
    /// Whether <paramref name="path"/> may be granted as an extra now, by the rules <c>allow</c> applies to a path (absolute, resolvable,
    /// not the root, home or <c>~/.claude</c>, not protected) and that it exists. <see cref="GrantPathCheck.Canonical"/> is the
    /// resolved path when it resolved; <see cref="GrantPathCheck.Refusal"/> is null when it may be granted.
    /// </summary>
    GrantPathCheck CheckPath(string hierarchyDir, string path);
}

/// <summary>The outcome of <see cref="IWorktreeGrants.CheckPath"/>.</summary>
public sealed record GrantPathCheck(string? Canonical, string? Refusal);

internal sealed class WorktreeGrants(IFileReader files, IAhEnvironment environment) : IWorktreeGrants
{
    internal const int Version = 1;

    /// <summary>The mode of a team with no grants document, and of a document that <c>allow</c> creates.</summary>
    internal const string OffMode = "off";

    internal static readonly string[] Modes = [OffMode, "log", "enforce"];

    public string DocumentPath(string hierarchyDir, string team) => NodePath.Join(hierarchyDir, "grants", team + ".json");

    public string AuditPath() => AhPaths.UserEntry(environment.HomeDirectory, AhPaths.WorktreeAuditFile);

    /// <summary>The one grants read of <paramref name="hierarchyDir"/> (per-dir-files-to-db S11e, §6c G5); false when the store could not be read.</summary>
    private bool TryDocuments(string hierarchyDir, out IReadOnlyList<GrantDocument> documents) =>
        files.ReadGrants(hierarchyDir, AuditPath()).TryDocuments(out documents);

    private static GrantsView None(string team) => new(team, OffMode, false, true, null, new Dictionary<string, GrantMember>());

    public GrantsView Read(string hierarchyDir, string team) => Reader(hierarchyDir)(team);

    public Func<string, GrantsView> Reader(string hierarchyDir) =>
        !TryDocuments(hierarchyDir, out var documents)
            ? team => None(team) with { Problem = "the grants store could not be read" }
            : team => ViewOf(team, documents.FirstOrDefault(d => d.Team == team));

    private static GrantsView ViewOf(string team, GrantDocument? document)
    {
        var none = None(team);
        if (document is null)
        {
            return none;
        }

        JsonValue doc;
        try
        {
            doc = JsonText.Parse(document.Body);
        }
        catch (JsonParseException)
        {
            return none with { Problem = "the grants document cannot be read" };
        }

        if (!doc.IsMap || doc.AsObject().Get("mode") is not { IsText: true } mode || !Modes.Contains(mode.AsString()))
        {
            return none with { Problem = "the grants document is not a record this version reads" };
        }

        var members = new Dictionary<string, GrantMember>(StringComparer.Ordinal);
        if (doc.AsObject().Get("members") is { IsMap: true } map)
        {
            foreach (var name in map.AsObject().Keys)
            {
                if (map.AsObject()[name] is { IsMap: true } m && m.AsObject().Get("work_root") is { IsText: true } root)
                {
                    members[name] = new GrantMember(root.AsString(), Text(m.AsObject(), "stream"), Text(m.AsObject(), "role"), ExtrasOf(m.AsObject()), TmpOf(m.AsObject()));
                }
            }
        }

        var recorded = Text(doc.AsObject(), "audit_hash");
        var (consistent, problem) = CheckAudit(recorded, document.AuditRow);
        return new GrantsView(team, mode.AsString(), true, consistent, problem, members);
    }

    /// <summary>Every team's view from one grants read; none when the store could not be read.</summary>
    private List<GrantsView> Views(string hierarchyDir) =>
        TryDocuments(hierarchyDir, out var documents) ? [.. documents.Select(d => ViewOf(d.Team, d))] : [];

    public bool HoldsActiveDocument(string hierarchyDir) =>
        Views(hierarchyDir).Any(v => v is { Present: true, Mode: "log" or "enforce" });

    public IReadOnlyList<GrantRoot> MemberRoots(string hierarchyDir) =>
        [.. Views(hierarchyDir).Where(v => v is { Present: true, Mode: "log" or "enforce" })
            .SelectMany(v => v.Members.Select(m => new GrantRoot(v.Team, m.Key, m.Value.WorkRoot)))];

    public IReadOnlyList<GrantExtra> EffectiveExtras(GrantsView view, string member, Func<string, bool> responseFiled)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(responseFiled);
        if (!view.Consistent || !view.Members.TryGetValue(member, out var m))
        {
            return [];
        }

        return [.. m.Extra.Where(x => x.For is null || !responseFiled(x.For))];
    }

    public string EffectiveMode(GrantsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Present && !view.Consistent ? "enforce" : view.Mode;
    }

    /// <summary>The SHA-256 of one audit row as the document records it.</summary>
    internal static string HashOf(string row) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row)));

    private static (bool Consistent, string? Problem) CheckAudit(string? recorded, string? row)
    {
        if (recorded is null)
        {
            return (true, null);
        }

        return row is not null && HashOf(row) == recorded
            ? (true, null)
            : (false, "the audit log is missing or does not match the grants document");
    }

    /// <summary>The teams that have a grants document in <paramref name="hierarchyDir"/>, by one grants read; none when the store could not be read.</summary>
    public IReadOnlyList<string> Teams(string hierarchyDir) =>
        TryDocuments(hierarchyDir, out var documents) ? [.. documents.Select(static d => d.Team)] : [];

    public IReadOnlyList<string> FamilyRoots(string hierarchyDir) =>
        GrantPaths.FamilyRoots(files, hierarchyDir, p => CanonicalPath.Resolve(files, p) ?? p);

    public IReadOnlyList<string> TempRoots() => GrantPaths.TempRoots(environment, p => CanonicalPath.Resolve(files, p) ?? p);

    public bool IsProtected(string canonicalPath, IReadOnlyList<string> familyRoots)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        ArgumentNullException.ThrowIfNull(familyRoots);
        var home = environment.HomeDirectory;
        return GrantPaths.IsProtected(canonicalPath, CanonicalPath.Resolve(files, home) ?? home, familyRoots);
    }

    public GrantPathCheck CheckPath(string hierarchyDir, string path)
    {
        ArgumentNullException.ThrowIfNull(hierarchyDir);
        ArgumentNullException.ThrowIfNull(path);
        if (!NodePath.IsAbsolute(path))
        {
            return new GrantPathCheck(null, $"{path} is not an absolute path");
        }

        var canonical = CanonicalPath.Resolve(files, path);
        if (canonical is null)
        {
            return new GrantPathCheck(null, $"{path} cannot be resolved (a link loop or a path that climbs past a missing name)");
        }

        var home = environment.HomeDirectory;
        var forbidden = GrantPaths.Forbidden(canonical, CanonicalPath.Resolve(files, home) ?? home, FamilyRoots(hierarchyDir));
        if (forbidden is null && files.Stat(canonical) is null)
        {
            forbidden = $"{canonical} does not exist: it cannot be granted";
        }

        return new GrantPathCheck(canonical, forbidden);
    }

    private static GrantTmp? TmpOf(JsonObject member) =>
        member.Get("tmp") is { IsMap: true } t && Text(t.AsObject(), "root") is { Length: > 0 } root
            ? new GrantTmp(root, Text(t.AsObject(), "alias") ?? string.Empty, Text(t.AsObject(), "kind") ?? "claude")
            : null;

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : null;

    private static List<GrantExtra> ExtrasOf(JsonObject member)
    {
        var extras = new List<GrantExtra>();
        if (member.Get("extra") is { IsArray: true } list)
        {
            foreach (var item in list.AsArray())
            {
                if (item is { IsMap: true } x && x.AsObject().Get("path") is { IsText: true } path)
                {
                    extras.Add(new GrantExtra(
                        path.AsString(),
                        Text(x.AsObject(), "note") ?? string.Empty,
                        Text(x.AsObject(), "for"),
                        x.AsObject().Get("outside") is { Kind: JsonKind.Bool, IsTruthy: true },
                        Text(x.AsObject(), "at") ?? string.Empty,
                        x.AsObject().Get("standing") is { Kind: JsonKind.Bool, IsTruthy: true }));
                }
            }
        }

        return extras;
    }
}
