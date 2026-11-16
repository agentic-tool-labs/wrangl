using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The <c>authority:</c> line of the run anchor and the user's recorded answer it must match. The grant is the user's: an anchor
/// may say <c>surrogate</c> only when this session's latest recorded answer to the Authority question chose it.
/// </summary>
internal static partial class AnchorAuthority
{
    public const string Surrogate = "surrogate";

    public const string User = "user";

    /// <summary>The header of the bootstrap question and the label whose choice grants the surrogate role.</summary>
    public const string QuestionHeader = "Authority";

    public const string SurrogateLabel = "Orchestrator acts for me (this run)";

    /// <summary>How long after an anchor's creation an answer is still taken as the one that preceded it, for clocks that disagree.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(10);

    private static bool TryInstant(string? text, out DateTimeOffset instant)
    {
        instant = default;
        return text is not null && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out instant);
    }

    [GeneratedRegex("^[ \\t]*authority:[ \\t]*(.*?)[ \\t]*\\r?(?=\\n|\\z)",RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LineRegex();

    [GeneratedRegex("^created:[ \\t]*([^ \\t\\r\\n]+)[ \\t]*\\r?(?=\\n|\\z)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex CreatedRegex();

    /// <summary>The <c>created:</c> value of the first front-matter line that has one; null for none.</summary>
    public static string? CreatedOf(string messageText)
    {
        var head = messageText.StartsWith("---", StringComparison.Ordinal) && messageText.IndexOf("\n---", 3, StringComparison.Ordinal) is > 0 and var end
            ? messageText[..end]
            : string.Empty;
        return CreatedRegex().Match(head) is { Success: true } m ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("^team:[ \\t]*([^ \\t\\r\\n]+)[ \\t]*\\r?(?=\\n|\\z)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex TeamRegex();

    /// <summary>The <c>team:</c> value of the front matter; null for none.</summary>
    public static string? TeamOf(string messageText)
    {
        var head = messageText.StartsWith("---", StringComparison.Ordinal) && messageText.IndexOf("\n---", 3, StringComparison.Ordinal) is > 0 and var end
            ? messageText[..end]
            : string.Empty;
        return TeamRegex().Match(head) is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>The values of every <c>authority:</c> line in <paramref name="body"/>, in order.</summary>
    public static IReadOnlyList<string> Values(string body) =>
        [.. LineRegex().Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant())];

    /// <summary>The grant an anchor body states: surrogate or user, null for no line, several lines or any other value.</summary>
    public static string? ModeOf(string body) =>
        Values(body) is [var only] && only is Surrogate or User ? only : null;

    /// <summary>
    /// The mode the latest Authority answer of <paramref name="sessionId"/> in an asks log maps to: surrogate for the surrogate label,
    /// user for any other answer, null when this session recorded none. Rows of other sessions and unreadable lines are ignored.
    /// </summary>
    public static string? RecordedMode(string asksText, string? sessionId, string? afterIso, string? anchorCreatedIso) =>
        LatestAnswer(asksText, sessionId, afterIso, anchorCreatedIso, QuestionHeader) is { } answer
            ? answer.Answer == SurrogateLabel ? Surrogate : User
            : null;

    /// <summary>The writer of an answer the Orchestrator relayed from chat (<c>ah msg question answer --relay</c>); it never grants Authority.</summary>
    private const string RelayedWriter = "decision-answer-relayed";

    /// <summary>A recorded answer: the text of the question the user was asked and the answer given.</summary>
    public readonly record struct RecordedAnswer(string Question, string Answer);

    /// <summary>
    /// The latest answer of <paramref name="sessionId"/> to a question headed <paramref name="header"/> that was given after
    /// <paramref name="afterIso"/> and no later than the anchor's creation plus <see cref="Skew"/>; null when there is none. Rows of
    /// other sessions and unreadable lines are ignored.
    /// </summary>
    public static RecordedAnswer? LatestAnswer(string asksText, string? sessionId, string? afterIso, string? anchorCreatedIso, string header)
    {
        // an answer belongs to a run only if it came after the previous run closed and before this anchor was made (plus clock skew);
        // an anchor whose creation time is unknown is bound to no answer
        if (string.IsNullOrEmpty(sessionId) || !TryInstant(anchorCreatedIso, out var created))
        {
            return null;
        }

        var notAfter = created + Skew;
        DateTimeOffset? after = TryInstant(afterIso, out var boundary) ? boundary : null;
        if (afterIso is not null && after is null)
        {
            return null;
        }

        bool InWindow(DateTimeOffset when) => (after is null || when > after) && when <= notAfter;
        var parked = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var rows = new List<JsonValue>();
        foreach (var line in asksText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonValue parsed;
            try
            {
                parsed = JsonText.Parse(line);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }

            if (parsed.Member("kind") is { IsText: true } kind && kind.AsString() == "parked"
                && parsed.Member("id") is { IsText: true } parkedId && parsed.Member("at") is { IsText: true } parkedAt && TryInstant(parkedAt.AsString(), out var parkedWhen))
            {
                parked.TryAdd(parkedId.AsString(), parkedWhen);
            }

            rows.Add(parsed);
        }

        // An answer given out of band comes hours after the question was parked; it belongs to the run its question was parked in.
        bool AnswersParkedInWindow(JsonValue row) =>
            row.Member("writer") is { IsText: true } writer && writer.AsString() == "decision-answer"
            && row.Member("metadata")?.Member("source") is { IsText: true } source
            && source.AsString().Split(';').FirstOrDefault(p => p.StartsWith("ref=", StringComparison.Ordinal)) is { } reference
            && parked.TryGetValue(reference[4..], out var parkedWhen) && InWindow(parkedWhen);

        RecordedAnswer? latest = null;
        foreach (var row in rows)
        {
            if (!(row.Member("at") is { IsText: true } at && TryInstant(at.AsString(), out var when) && (InWindow(when) || AnswersParkedInWindow(row)))
                || row.Member("session_id") is not { IsText: true } sid || sid.AsString() != sessionId
                || (header == QuestionHeader && row.Member("writer") is { IsText: true } w && w.AsString() == RelayedWriter)
                || row.Member("questions") is not { IsArray: true } questions
                || row.Member("response")?.Member("answers") is not { IsMap: true } answers)
            {
                continue;
            }

            foreach (var question in questions.AsArray())
            {
                if (question.Member("header") is { IsText: true } asked && asked.AsString() == header
                    && question.Member("question") is { IsText: true } text
                    && answers.AsObject().Get(text.AsString()) is { IsText: true } answer)
                {
                    latest = new RecordedAnswer(text.AsString(), answer.AsString());
                }
            }
        }

        return latest;
    }

    /// <summary>
    /// Why writing <paramref name="body"/> over <paramref name="previous"/> (null for a new anchor) is refused, or null when it is
    /// allowed. Without a recorded surrogate answer only <c>authority: user</c> or no line passes; once a line exists its value
    /// never changes.
    /// </summary>
    public static string? Refusal(string body, string? previous, string? recorded)
    {
        var values = Values(body);
        if (values.Any(v => v is not (Surrogate or User)))
        {
            return "the authority line must say surrogate or user";
        }

        if (values.Count > 1)
        {
            return "the anchor carries more than one authority line";
        }

        if (values.Contains(Surrogate) && recorded != Surrogate)
        {
            return "authority: surrogate needs the user's recorded answer \"" + SurrogateLabel + "\" to the Authority question in this session";
        }

        if (previous is not null && Values(previous) is [var was] && (values.Count != 1 || values[0] != was))
        {
            return "the authority line of an anchor is set once and never changed";
        }

        return null;
    }
}
