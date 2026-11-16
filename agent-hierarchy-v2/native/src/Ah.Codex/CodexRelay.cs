namespace Ah.Codex;

/// <summary>The handle the daemon's answer route holds on the adapter job; create it with <see cref="CodexModule.CreateRelay"/>.</summary>
public interface ICodexRelay
{
    /// <summary>The server requests held for every connected member.</summary>
    IReadOnlyList<CodexPending> Pending();

    /// <summary>The served member <paramref name="memberKey"/> (<c>team.name</c>), or null.</summary>
    CodexMember? MemberOf(string memberKey);

    /// <summary>Replies to one held request; see <see cref="CodexAnswerResult"/> for the outcomes.</summary>
    Task<CodexAnswerResult> AnswerAsync(string memberKey, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text, string? idKey = null);
}

/// <summary>
/// The relay the composition root creates, passes to <see cref="CodexModule.Register"/>, and gives to the route. Until the job has
/// started it holds nothing.
/// </summary>
internal sealed class CodexRelay : ICodexRelay
{
    private volatile CodexAdapter? _adapter;

    internal void Attach(CodexAdapter adapter) => _adapter = adapter;

    public IReadOnlyList<CodexPending> Pending() => _adapter?.PendingRequests() ?? [];

    public CodexMember? MemberOf(string memberKey) => _adapter?.MemberOf(memberKey);

    public Task<CodexAnswerResult> AnswerAsync(string memberKey, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text, string? idKey = null) =>
        _adapter is { } adapter
            ? adapter.AnswerAsync(memberKey, requestId, choice, answers, text, idKey)
            : Task.FromResult(new CodexAnswerResult("disconnected", "the Codex adapter is not running"));
}
