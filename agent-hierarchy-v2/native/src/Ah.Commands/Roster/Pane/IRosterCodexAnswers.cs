using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>The daemon's reply to an answer for a held Codex request: its JSON body, or why there is none.</summary>
public sealed record CodexAnswerReply(JsonObject? Body, string? Failure);

/// <summary>Reaches the daemon that holds a Codex member's pending server requests; there is no other path to them.</summary>
public interface IRosterCodexAnswers
{
    /// <summary>
    /// Sends the answer to the daemon. <see cref="CodexAnswerReply.Failure"/> says why the daemon gave none: it is down, it did not
    /// answer in time, or it refused the caller.
    /// </summary>
    CodexAnswerReply Answer(string team, string member, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text);
}
