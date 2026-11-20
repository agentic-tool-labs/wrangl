namespace Ah.Abstractions;

/// <summary>A member's tmp directory that was renamed into <c>_dismissed/</c>, with when and how big.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Member">The member it belonged to.</param>
/// <param name="At">When it was dismissed, from its name.</param>
/// <param name="Size">Bytes, links not followed.</param>
public sealed record DismissedScratch(string Path, string Member, DateTimeOffset At, long Size);

/// <summary>
/// The narrow set of scratch-directory operations a member tmp needs: create it under the alias root (outside every checkout),
/// remove it on dismissal, measure it and sweep what is long idle. A legacy tmp root is the team home's <c>tmp</c> directory or one
/// directory below it and is checked by real path against the team home it is given; a member directory is checked to lie
/// directly under the alias root it was given, and a link is never followed when deleting.
/// A refused or failed operation throws <see cref="InvalidOperationException"/> or <see cref="IOException"/>.
/// </summary>
public interface IScratchDirectories
{
    /// <summary>
    /// Makes a member's tmp as a real directory, mode 0700, at the first of <paramref name="keys"/> under <paramref name="aliasRoot"/>
    /// that is free, already a directory (kept), or a link (an alias an older version made: the link is removed, never its target).
    /// Returns its path.
    /// </summary>
    string MakeMemberDirectory(string aliasRoot, IReadOnlyList<string> keys);

    /// <summary>Removes <paramref name="path"/> when it is a real directory directly under the alias root (never following a link); returns whether it was removed.</summary>
    bool RemoveMemberDirectory(string aliasRoot, string path);

    /// <summary>
    /// Finds, and with <paramref name="commit"/> deletes, each of <paramref name="paths"/> that is a real directory directly under the alias
    /// root and more than <paramref name="olderThan"/> old by its newest modification time anywhere in its tree; any other path is skipped.
    /// </summary>
    ScratchSweepReport SweepMemberDirectories(IReadOnlyCollection<string> paths, DateTimeOffset now, TimeSpan olderThan, bool commit);

    /// <summary>Where <paramref name="link"/> points, or null when it is not a link or does not exist.</summary>
    string? AliasTarget(string link);

    /// <summary>Unlinks <paramref name="link"/> when it is a link under the alias root; returns whether one was removed.</summary>
    bool RemoveAlias(string aliasRoot, string link);

    /// <summary>
    /// Removes <c>&lt;tmpRoot&gt;/&lt;member&gt;</c> (never following a link); returns the removed path, or null when the member has no
    /// directory.
    /// </summary>
    string? Dismiss(string teamHome, string tmpRoot, string member, DateTimeOffset at);

    /// <summary>The bytes under <paramref name="path"/>, counting regular files only and never following a link.</summary>
    long Size(string path);

    /// <summary>The dismissed directories of <paramref name="tmpRoot"/>, oldest first.</summary>
    IReadOnlyList<DismissedScratch> Dismissed(string teamHome, string tmpRoot);

    /// <summary>Deletes the dismissed directories older than <paramref name="olderThan"/>; returns how many.</summary>
    int SweepDismissed(string teamHome, string tmpRoot, DateTimeOffset now, TimeSpan olderThan);

    /// <summary>
    /// Finds, and with <paramref name="commit"/> deletes, the scratch under <c>&lt;teamHome&gt;/tmp</c> that is more than <paramref name="olderThan"/> old
    /// by its newest modification time anywhere in its tree: every dismissed directory (whose dismissal stamp must also be that old),
    /// and every member directory whose owner <paramref name="ownerOf"/> does not say is <see cref="ScratchOwner.Live"/>.
    /// A directory directly under <c>tmp</c> is a legacy member directory when <paramref name="isMemberName"/> says so, else a team's.
    /// <paramref name="ownerOf"/> takes the team (null for a legacy directory) and the member.
    /// </summary>
    ScratchSweepReport Sweep(string teamHome, DateTimeOffset now, TimeSpan olderThan, Func<string, bool> isMemberName, Func<string?, string, ScratchOwner> ownerOf, bool commit);
}

/// <summary>What is known of the member a scratch directory belongs to.</summary>
public enum ScratchOwner
{
    /// <summary>No readable record names the member: nothing proves it is running or gone.</summary>
    Unowned,

    /// <summary>A readable record lists the member and its session is proven gone.</summary>
    ProvenDead,

    /// <summary>The member may be running: it is listed and not proven gone, or its record cannot be read.</summary>
    Live,
}

/// <summary>One directory a scratch sweep found.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Bytes">Bytes under it, links not followed.</param>
public sealed record ScratchSweepEntry(string Path, long Bytes);

/// <summary>What a scratch sweep found, and whether it deleted it.</summary>
/// <param name="Entries">The directories found.</param>
/// <param name="Deleted">True when they were deleted.</param>
public sealed record ScratchSweepReport(IReadOnlyList<ScratchSweepEntry> Entries, bool Deleted)
{
    /// <summary>The bytes of every entry.</summary>
    public long Bytes => Entries.Sum(e => e.Bytes);
}
