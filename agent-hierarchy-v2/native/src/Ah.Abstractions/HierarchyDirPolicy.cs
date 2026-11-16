namespace Ah.Abstractions;

/// <summary>The two files of the trust anchor, both under the default home.</summary>
public enum TrustFile
{
    /// <summary><c>trusted-homes</c>: state homes (<c>AH_HOME</c>) the user has trusted.</summary>
    Homes,

    /// <summary><c>trusted-hierarchy-dirs</c>: hierarchy directories the user has trusted.</summary>
    HierarchyDirs,
}

/// <summary>One line of a trust file and the verdict on it. <paramref name="Reason"/> is set when the entry does not count.</summary>
public sealed record TrustEntry(string Line, string? RealPath, bool Trusted, string? Reason);

/// <summary>
/// What a trust file says: <paramref name="Present"/> is false when there is no such file (nothing trusted, no diagnostic);
/// <paramref name="IgnoredReason"/> is set when the file exists but is not read at all.
/// </summary>
public sealed record TrustFileReport(TrustFile File, string Path, bool Present, string? IgnoredReason, IReadOnlyList<TrustEntry> Entries);

/// <summary>The one reader of the trust files. Every failure means untrusted; nothing here throws for a bad file.</summary>
public interface ITrustStore
{
    /// <summary>The state of <paramref name="file"/> and each of its entries, with the reason for every one that does not count.</summary>
    TrustFileReport Read(TrustFile file);

    /// <summary>Whether <paramref name="path"/> (compared by <c>realpath</c> on both sides) is a counted entry of <paramref name="file"/>.</summary>
    bool IsTrusted(TrustFile file, string path);
}

/// <summary>How <c>WRANGL_HIERARCHY_DIR</c> stands: unset or empty, honoured because the user trusted it, honoured in test mode, or ignored.</summary>
public enum HierarchyOverrideKind
{
    None,
    Trusted,
    TestMode,
    Untrusted,
}

/// <summary>The environment override of the hierarchy directory and what became of it. <paramref name="Path"/> is its resolved value when set.</summary>
public sealed record HierarchyOverride(HierarchyOverrideKind Kind, string? Path);

/// <summary>
/// The hierarchy-directory rules, in one place for the client and the daemon: what a caller's working directory derives to,
/// and which directories a request may claim. Nothing here reads the process environment except the one override and the
/// test-mode sandbox, both through <see cref="IAhEnvironment"/>.
/// </summary>
public interface IHierarchyDirPolicy
{
    /// <summary>The hierarchy directory <paramref name="cwd"/> derives to, <c>WRANGL_HIERARCHY_DIR</c> ignored.</summary>
    string DerivedHierarchyDir(string cwd);

    /// <summary>
    /// The main checkout's hierarchy directory when <paramref name="cwd"/> is in a linked worktree whose <c>.git</c> pointer is
    /// confirmed by the worktree's own back-pointer, else null; <c>WRANGL_HIERARCHY_DIR</c> ignored.
    /// </summary>
    string? DerivedMainHierarchyDir(string cwd);

    /// <summary>
    /// The first claim of <paramref name="context"/> that is neither derived from its <c>Cwd</c> nor trusted, or null. Checks
    /// <c>hierarchy_dir</c>, <c>main_hierarchy_dir</c> (when non-empty) and <c>messaging_socket_dir</c> (when non-empty: a directory
    /// owned by the invoking user and not group- or other-writable). Existence and ownership of the other fields are the caller's.
    /// </summary>
    RequestContextProblem? CheckClaim(RequestContext context);

    /// <summary>
    /// <paramref name="path"/> with its longest existing prefix resolved through symbolic links; null when the first missing
    /// component is itself a symbolic link. <c>..</c> segments are collapsed textually, so a caller refuses them first.
    /// </summary>
    string? Realish(string path);

    /// <summary>The state of <c>WRANGL_HIERARCHY_DIR</c> for this process.</summary>
    HierarchyOverride Override();
}
