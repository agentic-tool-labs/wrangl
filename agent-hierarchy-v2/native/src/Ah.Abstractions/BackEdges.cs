namespace Ah.Abstractions;

/// <summary>
/// The session that asks which teams it owns: its process id and Claude session id, either of which may be unknown, and the
/// start time of that process when it was read (null when unknown, which matches any recorded start).
/// </summary>
public sealed record TeamInvoker(int? Pid, string? SessionId, string? PidStart = null);

/// <summary>A team the session is attributed to without owning it: the team file it was launched into, or the live team holding its pane.</summary>
/// <param name="TeamName">The team's name, or null for the default team.</param>
/// <param name="Home">The hierarchy directory that holds the team file.</param>
/// <param name="Via"><c>env</c> or <c>pane</c>.</param>
public sealed record MemberTeamMatch(string? TeamName, string Home, string Via);

/// <summary>The roster block key a team file records. <see cref="Key"/> is null for the default block.</summary>
public sealed record RosterBlockKey(string? Key);

/// <summary>Implemented by Roster; read by Config.</summary>
public interface ITeamFiles
{
    /// <summary>
    /// The team file as canonical UTF-8 JSON when it exists and holds a record with a members array, else null.
    /// A null <paramref name="team"/> reads the default team.
    /// </summary>
    ReadOnlyMemory<byte>? ReadTeam(string hierarchyDirectory, string? team = null);

    /// <summary>The teams the invoker owns, the default team (null) first and the rest by name.</summary>
    IReadOnlyList<string?> OwnedTeams(string hierarchyDirectory, TeamInvoker invoker);

    /// <summary>The team a session that owns none belongs to, or null.</summary>
    MemberTeamMatch? MemberTeam(string hierarchyDirectory, IReadOnlyList<string> homes, string? paneId);

    /// <summary>The naming prefix a legacy <c>team.json</c>'s members were named under, or null.</summary>
    string? LegacyTeamPrefix(string hierarchyDirectory, string? expectedPrefix = null);

    /// <summary>The roster block key the team records; null when the team has no file.</summary>
    RosterBlockKey? TeamRosterEntry(string hierarchyDirectory, string? team);
}

/// <summary>
/// What a team write or clear through the database authority did. <see cref="Code"/> is <c>ok</c>, <c>not-database</c>,
/// <c>invalid-team</c>, <c>digest-mismatch</c> (the team changed since the writer read it; the writer
/// reads again and retries) or <c>failed</c>.
/// </summary>
/// <param name="Ok">True when the database took the change, whether or not its export could be written yet.</param>
/// <param name="Code">The outcome code.</param>
/// <param name="Message">Why, or the note that the export was not written and will be retried.</param>
/// <param name="Changed">True when a row was written or removed.</param>
public sealed record TeamAuthorityResult(bool Ok, string Code, string Message, bool Changed = false);

/// <summary>The outcome of a team snapshot write: whether the row was stored, why not, and how many older rows of that team were pruned.</summary>
public sealed record SnapshotWriteResult(bool Ok, string Message, int Removed);

/// <summary>What a registration write decided, given the registration the session already has: write <see cref="SessionDecision.Document"/>, leave it as it is, or refuse for <see cref="SessionDecision.Why"/>.</summary>
public enum SessionVerdict
{
    Write,
    Unchanged,
    Refused,
}

/// <summary>The decision of one registration write; <paramref name="Document"/> is the exact bytes the registration file holds and is set only for <see cref="SessionVerdict.Write"/>.</summary>
public sealed record SessionDecision(SessionVerdict Verdict, ReadOnlyMemory<byte> Document = default, string? Why = null);

/// <summary>
/// What a registration write or removal through the database authority did. <see cref="Code"/> is <c>ok</c>, <c>refused</c> (the decision
/// refused; <see cref="Message"/> is its reason), <c>not-database</c> (the file is the record), <c>outside-home</c> or <c>failed</c>.
/// <paramref name="Exported"/> holds the document the caller writes as the registration file after a committed write; it is empty when nothing was written.
/// </summary>
public sealed record SessionAuthorityResult(bool Ok, string Code, string Message, bool Changed = false, ReadOnlyMemory<byte> Exported = default);

/// <summary>One <c>sessions</c> row as the usage report reads it; times are Unix ms, <paramref name="EndedAt"/> null while live.</summary>
public sealed record SessionHistoryRow(string SessionId, string? Team, string? Member, long StartedAt, long? EndedAt);

/// <summary>
/// Implemented by the state database; called by the mailbox state domain. While the sessions domain is the database's, a registration is a
/// row and its document, written in one transaction, and the registration file is the export the caller writes after the commit.
/// </summary>
public interface ISessionAuthority
{
    /// <summary>Whether the sessions domain is the database's now. False costs one small file check.</summary>
    bool IsDatabase { get; }

    /// <summary>
    /// Registers <paramref name="sessionId"/> in one transaction: <paramref name="decide"/> gets the document of its live registration (null when it has none) and
    /// says what to do. <paramref name="registrationFile"/> must be the session's own file under the ah home this authority belongs to.
    /// </summary>
    SessionAuthorityResult Register(string registrationFile, string sessionId, Func<ReadOnlyMemory<byte>?, SessionDecision> decide);

    /// <summary>Ends <paramref name="sessionId"/>, keeping its row; <see cref="SessionAuthorityResult.Changed"/> is true when it was live.</summary>
    SessionAuthorityResult Unregister(string registrationFile, string sessionId);

    /// <summary>The document of the session's live registration, or null when it has none. A store that cannot answer throws, rather than saying none.</summary>
    ReadOnlyMemory<byte>? ReadStored(string sessionId) => null;

    /// <summary>Every live registration's document by session id, in id order.</summary>
    IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> ListStored() => [];

    /// <summary>Every ended (not yet pruned) registration's document by session id, for liveness evidence only: a dead session still names its process.</summary>
    IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> ListEnded() => [];

    /// <summary>Every placed session row of hierarchy dir <paramref name="hierDir"/>, live and ended (not yet pruned), for the usage report.</summary>
    IReadOnlyList<SessionHistoryRow> ListHistory(string hierDir) => [];

    /// <summary>Ends the live registration of <paramref name="sessionId"/> only while it still records this pid and start, whose process is proven gone; true when one ended.</summary>
    bool EndGone(string sessionId, long pid, string pidStart) => false;

    /// <summary>Deletes the session rows that ended before <paramref name="endedBeforeMs"/>, or with <paramref name="countOnly"/> only counts them; returns the count.</summary>
    int PruneEnded(long endedBeforeMs, bool countOnly) => 0;

    /// <summary>A counter that moves whenever any session row changes, whichever process changed it; 0 when none has.</summary>
    long ChangeToken() => 0;
}

/// <summary>
/// Implemented by the state database; read by the mailbox. A box is named by its key, <c>&lt;team&gt;~&lt;sha10&gt;/&lt;name&gt;</c> under the mailbox root, and
/// each record is the JSON bytes its op stored. A store that cannot answer throws, rather than saying none.
/// </summary>
public interface IMailStore
{
    /// <summary>Whether the mail domain is the database's now. False costs one small file check.</summary>
    bool IsDatabase { get; }

    /// <summary>A counter that moves on every mail write, whichever process made it; 0 when none has.</summary>
    long ChangeToken();

    /// <summary>The change marker of the box: the mail counter and the newest entry the box holds.</summary>
    string BoxStamp(string box);

    /// <summary>The key of the newest entry the box holds, 0 when none: unlike <see cref="BoxStamp"/> it moves only when mail reaches this box.</summary>
    long NewestEntry(string box);

    /// <summary>The key of every box that holds a row.</summary>
    IReadOnlyList<string> Boxes();

    /// <summary>How many boxes hold a heartbeat: a heartbeat leaves <see cref="ChangeToken"/> where it was, yet a box it alone holds is listed.</summary>
    long HeartbeatBoxes();

    /// <summary>
    /// For every brief entry with a text <c>ref</c>, in a box whose key has a team part, that ref and the state of the entry's newest ladder row
    /// in its box, empty when it has none. One query, no row parsed.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string>> BriefLadderStates();

    /// <summary>The box's entries after the cursor <paramref name="afterPk"/>, oldest first, by their cursor.</summary>
    IReadOnlyList<KeyValuePair<long, ReadOnlyMemory<byte>>> Entries(string box, long afterPk);

    /// <summary>
    /// The box's entry count and its entries after <paramref name="afterPk"/>, oldest first, from one read: a holder of the rows up to
    /// the cursor reloads the box when what it holds plus the new rows is not the count (a row was deleted), or when
    /// <c>CursorId</c>, the id of the row at <paramref name="afterPk"/> (null when none), is not the id it held there (a deleted pk reused).
    /// <c>LastId</c> is the id of the newest row returned, else <c>CursorId</c>. This default knows no ids and returns null for both.
    /// </summary>
    (long Count, string? CursorId, string? LastId, IReadOnlyList<KeyValuePair<long, ReadOnlyMemory<byte>>> Rows) CountedEntries(string box, long afterPk)
    {
        var all = Entries(box, 0);
        return (all.Count, null, null, [.. all.Where(r => r.Key > afterPk)]);
    }

    /// <summary>The box's ack count as text: acks are only inserted, so a held ack set is current while it reads the same.</summary>
    string AckStamp(string box) => AckIds(box).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The box's unacked entries whose id starts with <paramref name="idPrefix"/>, oldest first: a range of the (box, id) index, no other row read.</summary>
    IReadOnlyList<ReadOnlyMemory<byte>> UnackedEntriesWithIdPrefix(string box, string idPrefix);

    /// <summary>The box's routing rows, oldest first.</summary>
    IReadOnlyList<ReadOnlyMemory<byte>> Attempts(string box);

    /// <summary>The routing rows of entry <paramref name="id"/> in the box, oldest first: a range of the (box, id, pk) index.</summary>
    IReadOnlyList<ReadOnlyMemory<byte>> Attempts(string box, string id);

    /// <summary>
    /// The box's ladder rows split on each entry's newest ladder row: the rows (oldest first) of every entry whose newest state is not in
    /// <paramref name="settled"/>, and that newest state by id for every entry whose newest state is.
    /// </summary>
    (IReadOnlyList<ReadOnlyMemory<byte>> Open, IReadOnlyList<KeyValuePair<string, string>> Settled) LadderSweep(string box, IReadOnlyList<string> settled);

    /// <summary>The ack of the entry, or null.</summary>
    ReadOnlyMemory<byte>? Ack(string box, string id);

    /// <summary>The ids acked in the box.</summary>
    IReadOnlyList<string> AckIds(string box);

    /// <summary>The claim of the entry, or null.</summary>
    ReadOnlyMemory<byte>? Claim(string box, string id);

    /// <summary>The ids in the box that have a claim, in byte order.</summary>
    IReadOnlyList<string> ClaimIds(string box);

    /// <summary>The box's heartbeats by the name each is kept under, in byte order.</summary>
    IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> Adapters(string box);

    /// <summary>The note, or null.</summary>
    ReadOnlyMemory<byte>? Note(string kind, string name);
}

/// <summary>
/// Implemented by the state database; called by Roster's team domain. While the teams domain is the database's, a team write is a row
/// and document written in one transaction and the team file is the export regenerated from it.
/// </summary>
public interface ITeamAuthority
{
    /// <summary>Whether the teams domain is the database's now. False costs one small file check.</summary>
    bool IsDatabase { get; }

    /// <summary>
    /// Writes <paramref name="document"/>, the exact bytes the team file would hold, as the team of <paramref name="teamFile"/> and regenerates that
    /// team's export. When <paramref name="expectedDigest"/> is given the database compares it with the digest of the stored document (or
    /// <c>absent</c>) in the same transaction and writes nothing on a difference.
    /// </summary>
    TeamAuthorityResult Write(string teamFile, ReadOnlyMemory<byte> document, string? expectedDigest = null);

    /// <summary>Removes the team of <paramref name="teamFile"/> and its export, under the same digest check as <see cref="Write"/>.</summary>
    TeamAuthorityResult Clear(string teamFile, string? expectedDigest = null);

    /// <summary>
    /// The document the database holds for the team of <paramref name="teamFile"/>, or null; a stale or missing export is rewritten first so
    /// the file digest a writer later presents matches this document.
    /// </summary>
    ReadOnlyMemory<byte>? Read(string teamFile);

    /// <summary>The document the database holds for the team of <paramref name="teamFile"/>, or null, leaving the export alone: a read on a path that writes nothing.</summary>
    ReadOnlyMemory<byte>? ReadStored(string teamFile) => Read(teamFile);

    /// <summary>The names of the unfenced teams the database holds in the hierarchy directory <paramref name="hierDir"/>.</summary>
    IReadOnlyList<string> ListTeams(string hierDir) => [];

    /// <summary>A counter that moves whenever any team row changes, whichever process changed it; 0 when none has.</summary>
    long ChangeToken() => 0;

    /// <summary>The team-history document the database holds for the team home <paramref name="teamHome"/>, verbatim, or null when it holds none.</summary>
    string? ReadHistory(string teamHome);

    /// <summary>Writes <paramref name="document"/> as the team-history row of the team home <paramref name="teamHome"/>, replacing any row, in one transaction.</summary>
    TeamAuthorityResult WriteHistory(string teamHome, string document);

    /// <summary>The snapshot file names (<c>&lt;team&gt;-&lt;ts&gt;.json</c>) the database holds for <paramref name="team"/> in the team home <paramref name="teamHome"/>.</summary>
    IReadOnlyList<string> SnapshotFiles(string teamHome, string team);

    /// <summary>The snapshot document the database holds at (<paramref name="teamHome"/>, <paramref name="file"/>), verbatim, or null.</summary>
    string? ReadSnapshot(string teamHome, string file);

    /// <summary>
    /// Stores <paramref name="document"/> as the snapshot <c>&lt;team&gt;-&lt;ts&gt;.json</c> of <paramref name="teamHome"/>, then, in the same
    /// transaction, deletes that team's rows beyond the newest <paramref name="keep"/> by ts, never <paramref name="pinned"/>. A null
    /// <paramref name="keep"/> prunes nothing.
    /// </summary>
    SnapshotWriteResult WriteSnapshot(string teamHome, string team, string ts, string document, int? keep, string? pinned);
}

/// <summary>Implemented by Hierarchy; called by Roster after a write, outside the op, once its lock is released.</summary>
public interface IStatusRefresher
{
    void Refresh(string hierarchyDirectory);

    /// <summary>
    /// The status side of a write of <paramref name="domain"/> (ah-msgs-fast-2 §3.8 item 3): its delta, or today's refresh of
    /// <paramref name="refreshDir"/> while no status dir with persisted entries lists it.
    /// </summary>
    void Delta(string domain, OpCall operation, OpResult result, string? refreshDir)
    {
        if (refreshDir is not null)
        {
            Refresh(refreshDir);
        }
    }
}