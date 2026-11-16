using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>Why a mailbox call refused: a code (<c>bad_request</c>, <c>bad_address</c>, <c>unknown_address</c>, <c>ambiguous_address</c>, <c>busy</c>, <c>write_failed</c>, <c>from_mismatch</c>, ...), its text, and the candidates an ambiguity names.</summary>
public sealed record MailboxFailure(string Code, string Message, IReadOnlyList<string>? Candidates = null);

/// <summary>A mailbox call's result: the value, or the failure when it refused. Nothing was written on a failure unless the call says so.</summary>
public sealed record MailboxOutcome<T>(T? Value, MailboxFailure? Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>A named team file visible from a request: the team name, the file, its record, and whether its Orchestrator process is alive.</summary>
public sealed record VisibleTeam(string Team, string Path, JsonValue Data, bool Live);

/// <summary>A named team an invoker owns and its team file.</summary>
internal sealed record OwnedTeam(string Team, string TeamFile);

/// <summary>A member of a visible team that carries a given name.</summary>
public sealed record MemberMatch(string Name, string Team, string? Role, string Address);

/// <summary>
/// An address resolved to its team and box. <paramref name="Member"/> is the member's row (null for the Orchestrator),
/// <paramref name="Orchestrator"/> the team's <c>orchestrator</c> block, <paramref name="Dir"/> the recipient's box directory.
/// </summary>
public sealed record ResolvedAddress(string Name, string Team, string Address, JsonValue? Member, JsonValue? Orchestrator, string TeamPath, string? Home, string Dir);

/// <summary>A relay that was sent and has not been shown: the entry, its recipient, and when the claim reached <c>relay-sent</c>.</summary>
public sealed record HeldRelay(string Id, string To, string Since);

/// <summary>The ladder rows of one entry of a box, oldest first.</summary>
public sealed record LadderEntryRows(string Id, IReadOnlyList<JsonObject> Rows);

/// <summary>A box's ladder for a sweep: the rows of each entry whose ladder is open, and the newest state by id of each settled one.</summary>
public sealed record LadderSweepRows(IReadOnlyList<LadderEntryRows> Open, IReadOnlyDictionary<string, string> Settled);

/// <summary>Entries parsed from a byte offset, oldest first, and the cursor after the last complete line.</summary>
public sealed record EntriesResult(IReadOnlyList<JsonValue> Entries, long Next);

/// <summary>
/// Which entries of a box <see cref="IMailbox.Inbox"/> returns: the unacked ones by default; <paramref name="All"/> adds the acked;
/// <paramref name="Overdue"/> keeps the unacked ones older than the pull timeout; <paramref name="Since"/> is a byte offset.
/// </summary>
public sealed record InboxOptions(bool All = false, bool Overdue = false, double? NowMs = null, long Since = 0);

/// <summary>A refusal the mailbox raises across its own internal calls and turns into a <see cref="MailboxFailure"/> at its boundary.</summary>
internal sealed class MailboxError(string code, string message, IReadOnlyList<string>? candidates = null) : IOException(message)
{
    internal string Code { get; } = code;

    internal IReadOnlyList<string>? Candidates { get; } = candidates;

    internal MailboxFailure ToFailure() => new(Code, Message, Candidates);
}
