namespace Ah.Abstractions;

/// <summary>A member's tmp directory that was renamed into <c>_dismissed/</c>, with when and how big.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Member">The member it belonged to.</param>
/// <param name="At">When it was dismissed, from its name.</param>
/// <param name="Size">Bytes, links not followed.</param>
public sealed record DismissedScratch(string Path, string Member, DateTimeOffset At, long Size);

/// <summary>
/// The narrow set of scratch-directory operations a member tmp needs: create it, give it a short alias, rename it away on
/// dismissal, measure it and delete what was dismissed long ago. A tmp root is the team home's <c>tmp</c> directory or one
/// directory below it and is checked by real path against the team home it is given; a target is checked to lie under the
/// alias root it was given, a link is never followed when deleting, and only <c>_dismissed/</c> is ever deleted from.
/// A refused or failed operation throws <see cref="InvalidOperationException"/> or <see cref="IOException"/>.
/// </summary>
public interface IScratchDirectories
{
    /// <summary>Creates <c>&lt;tmpRoot&gt;/&lt;member&gt;</c> with mode 0700 (and <paramref name="tmpRoot"/> itself); returns its real path.</summary>
    string CreateMemberDirectory(string teamHome, string tmpRoot, string member);

    /// <summary>
    /// Points an alias under <paramref name="aliasRoot"/> at <paramref name="target"/>: the first of <paramref name="keys"/> that is free,
    /// already points at the target, or is dangling. An alias that points at another existing directory is never re-pointed.
    /// Returns the alias path.
    /// </summary>
    string LinkAlias(string aliasRoot, IReadOnlyList<string> keys, string target);

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
