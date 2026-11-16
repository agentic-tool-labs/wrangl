namespace Ah.Codex;

/// <summary>
/// A server request the adapter is holding for the Orchestrator. <see cref="Verbatim"/> is the request's params as the server sent
/// them (ids dropped), or null for a secret question, whose text is never kept anywhere ah writes.
/// </summary>
public sealed record CodexPending(
    string MemberKey,
    string RequestId,
    string Kind,
    string Method,
    string? Verbatim,
    bool Secret,
    bool Blocking,
    IReadOnlyList<string> Choices,
    string? ItemId = null,
    string? Cwd = null,
    string? GrantRoot = null,
    IReadOnlyList<string>? Paths = null,
    string? IdKey = null);

/// <summary>The outcome of answering a held request: <c>answered</c>, <c>steered</c>, <c>unknown-request</c>, <c>ambiguous</c>, <c>bad-choice</c>, <c>secret</c>, <c>needs-evidence</c> or <c>disconnected</c>.</summary>
public sealed record CodexAnswerResult(string Status, string? Detail = null);

/// <summary>What a member session reports about the server requests it holds and the turns it sees end.</summary>
internal sealed class CodexRequestEvents(string memberKey)
{
    public string MemberKey { get; } = memberKey;

    public Action<CodexPending>? Raised { get; init; }

    public Action<CodexPending, string>? Dropped { get; init; }

    public Action<string>? Unsupported { get; init; }

    public Action<string>? PermissionsDeclined { get; init; }

    public Action<string?>? TurnEnded { get; init; }
}

internal static class CodexRequestKinds
{
    public const string CommandApproval = "command-approval";
    public const string FileChangeApproval = "file-change-approval";
    public const string Permissions = "permissions";
    public const string Question = "question";
    public const string Elicitation = "elicitation";

    public const int MethodNotFound = -32601;
    public const int VerbatimLimit = 4096;
    public const int LastMessageLimit = 2048;

    private static readonly Dictionary<string, string> ByMethod = new(StringComparer.Ordinal)
    {
        ["item/commandExecution/requestApproval"] = CommandApproval,
        ["item/fileChange/requestApproval"] = FileChangeApproval,
        ["item/permissions/requestApproval"] = Permissions,
        ["item/tool/requestUserInput"] = Question,
        ["mcpServer/elicitation/request"] = Elicitation,
    };

    /// <summary>Requests that are not user-facing; the adapter neither holds nor answers them.</summary>
    private static readonly HashSet<string> LeftUnanswered = new(StringComparer.Ordinal)
    {
        "item/tool/call",
        "account/chatgptAuthTokens/refresh",
        "attestation/generate",
        "currentTime/read",
    };

    public static string? KindOf(string method) => ByMethod.TryGetValue(method, out var kind) ? kind : null;

    public static bool IsLeftUnanswered(string method) => LeftUnanswered.Contains(method);

    public static IReadOnlyList<string> ChoicesOf(string kind) => kind switch
    {
        CommandApproval or FileChangeApproval => ["accept", "acceptForSession", "decline", "cancel"],
        Elicitation => ["accept", "decline", "cancel"],
        Permissions => ["accept", "decline", "cancel"],
        _ => [],
    };

    public static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
}
