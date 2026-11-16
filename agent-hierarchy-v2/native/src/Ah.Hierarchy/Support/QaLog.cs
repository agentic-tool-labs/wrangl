using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>
/// What a tail scan of a transcript found (spec qa-history-replies §4.1): the whole turn's text (null when the turn had none), the turn's
/// opening user text, and whether the opening was found. <see cref="Final"/> is the text after the turn's last tool call (null when it had
/// none), <see cref="OpeningId"/> the opening entry's uuid, <see cref="Question"/> the boundary text of the turn's latest real question
/// (null when the scan met none), and <see cref="Direct"/> the text between that question and its first tool call after it.
/// </summary>
public sealed record QaTurnReply(string? Text, string? Opening, bool Found)
{
    public string? Question { get; init; }

    public string? Final { get; init; }

    public bool HadTool { get; init; }

    public string? OpeningId { get; init; }

    public string? Direct { get; init; }
}

/// <summary>One question-log thread: the typed prompt (null for replies with no prompt in the log), its replies oldest first, or an ask.</summary>
public sealed record QaThread(JsonValue? Prompt, IReadOnlyList<JsonValue> Replies, JsonValue? Ask)
{
    public double At => (Prompt ?? Ask ?? Replies[0]).Member("at") is { IsNumber: true } n ? n.AsNumber() : 0;
}

/// <summary>A place in the History's order (spec qa-history-replies §4.6): a thread's <c>At</c> and its head row's id, written <c>&lt;at&gt;:&lt;id&gt;</c>.</summary>
public sealed record QaCursor(double At, string Id)
{
    public override string ToString() => At.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + Id;
}

/// <summary>
/// What a History read keeps (§4.6): <see cref="Search"/> a plain substring, any case, of the prompt, every reply's text and final, and an
/// ask's questions and answers; <see cref="Team"/> the head row's team, a null team showing under every team; <see cref="Since"/> epoch ms;
/// <see cref="Answered"/> true for answered, false for unanswered; <see cref="Id"/> the one thread with that head id; <see cref="Before"/>
/// the threads after that cursor; <see cref="Limit"/> at most <c>100</c>. Unprompted threads only with <see cref="All"/> or <see cref="Id"/>.
/// </summary>
public sealed record QaHistoryQuery
{
    public string? Search { get; init; }

    public string? Team { get; init; }

    public double? Since { get; init; }

    public bool? Answered { get; init; }

    public bool Questions { get; init; }

    public bool All { get; init; }

    public string? Id { get; init; }

    public QaCursor? Before { get; init; }

    public int Limit { get; init; } = 20;
}

/// <summary>One page of History threads, newest first, and the cursor of its last thread when more match (null when none do).</summary>
public sealed record QaPage(IReadOnlyList<QaThread> Threads, QaCursor? Next);

/// <summary>
/// The user's question log, the <c>qa_log</c> table per hierarchy dir (per-dir-files-to-db S6): every prompt the user typed to the Orchestrator,
/// every reply the Orchestrator gave, and every AskUserQuestion dialog, one row each (spec qa-history-all §3). Append-only,
/// trimmed to the last <see cref="RetentionDays"/> days.
/// </summary>
internal static partial class QaLog
{
    public const int RetentionDays = 30;

    public const int PromptCap = 16 * 1024;

    public const int ReplyCap = 64 * 1024;

    public const int TranscriptScanCap = 4 * 1024 * 1024;

    /// <summary>How far back a row is kept: an append drops the dir's rows older than this.</summary>
    public const double RetentionMs = RetentionDays * 86_400_000d;

    public static JsonObject PromptRow(string id, double atMs, string sessionId, string? team, string text)
    {
        var row = Head("prompt", id, atMs, sessionId, team);
        row["question"] = IsQuestion(text);
        Text(row, text, PromptCap);
        return row;
    }

    /// <summary>
    /// A turn row: the whole turn's text, and its <c>final</c> part when given. With <paramref name="openingId"/> its id is the turn's own
    /// (<c>t</c> + a digest of the session and the opening), so a second Stop of the same turn replaces it; without, it is today's <c>r</c> id.
    /// </summary>
    public static JsonObject ReplyRow(double atMs, string sessionId, string? team, string text, string? promptId, bool typed, bool cut, string? final = null, string? openingId = null)
    {
        var id = openingId is null ? IdOf("r", sessionId, atMs, text) : "t" + Digest(sessionId + "\0" + openingId);
        var row = Head("reply", id, atMs, sessionId, team);
        row["prompt_id"] = promptId is null ? JsonValue.Null : promptId;
        row["trigger"] = typed ? "typed" : "later";
        Text(row, text, ReplyCap);
        if (cut && !row.ContainsKey("truncated"))
        {
            row["truncated"] = true;
        }

        if (!string.IsNullOrWhiteSpace(final))
        {
            var bytes = Utf8Text.Encode(final);
            row["final"] = bytes.Length <= ReplyCap ? final : Utf8Text.Decode(bytes.AsSpan(0, ReplyCap)).TrimEnd('�');
            if (bytes.Length > ReplyCap)
            {
                row["final_truncated"] = true;
            }
        }

        return row;
    }

    /// <summary>A question's direct reply (§5): id <c>d</c> + its prompt id, so the first write wins and every later one is ignored.</summary>
    public static JsonObject DirectRow(double atMs, string sessionId, string? team, string text, string promptId)
    {
        var row = Head("reply", "d" + promptId, atMs, sessionId, team);
        row["part"] = "direct";
        row["prompt_id"] = promptId;
        row["trigger"] = "typed";
        Text(row, text, ReplyCap);
        return row;
    }

    public static JsonObject AskRow(double atMs, string sessionId, string? team, JsonValue questions, JsonValue response)
    {
        var row = Head("ask", IdOf("a", sessionId, atMs, JsonText.Stringify(questions, 0)), atMs, sessionId, team);
        row["questions"] = questions;
        row["answers"] = response.Member("answers") ?? JsonValue.Null;
        return row;
    }

    private static JsonObject Head(string kind, string id, double atMs, string sessionId, string? team) => new()
    {
        ["v"] = 1d,
        ["kind"] = kind,
        ["id"] = id,
        ["at"] = atMs,
        ["session_id"] = sessionId,
        ["team"] = team is null ? JsonValue.Null : team,
    };

    private static string IdOf(string prefix, string sessionId, double atMs, string text) => prefix + Digest(sessionId + "\0" + atMs + "\0" + text);

    private static string Digest(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Utf8Text.Encode(text)))[..12];

    /// <summary>Sets <c>text</c>, keeping its head within <paramref name="cap"/> UTF-8 bytes and marking a cut with <c>truncated</c> and the full <c>bytes</c>.</summary>
    private static void Text(JsonObject row, string text, int cap)
    {
        var bytes = Utf8Text.Encode(text);
        if (bytes.Length <= cap)
        {
            row["text"] = text;
            return;
        }

        row["text"] = Utf8Text.Decode(bytes.AsSpan(0, cap)).TrimEnd('\uFFFD');
        row["truncated"] = true;
        row["bytes"] = (double)bytes.Length;
    }

    // ---------------------------------------------------------------- question or instruction (§5)

    [GeneratedRegex("```.*?(?:```|\\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Fenced();

    [GeneratedRegex("^[ \\t]*>.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedLine();

    [GeneratedRegex("`[^`\\n]*`|\"[^\"\\n]*\"|“[^”\\n]*”|https?://\\S+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineQuoteCodeOrUrl();

    [GeneratedRegex("^[A-Za-z']+", RegexOptions.CultureInvariant)]
    private static partial Regex FirstWord();

    private static readonly HashSet<string> QuestionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "why", "how", "when", "where", "who", "which", "whose", "can", "could", "should", "would", "will", "is", "are", "was",
        "were", "do", "does", "did", "have", "has", "any", "anything",
    };

    private static readonly string[] QuestionPhrases = ["i wonder", "do you know", "tell me", "explain", "what's"];

    /// <summary>Whether a prompt looks like a question: a cheap mark that powers a filter and never drops a row.</summary>
    public static bool IsQuestion(string prompt)
    {
        var text = QuotedLine().Replace(Fenced().Replace(prompt, " "), " ").Trim();
        if (InlineQuoteCodeOrUrl().Replace(text, " ").Contains('?', StringComparison.Ordinal))
        {
            return true;
        }

        if (FirstWord().Match(text) is { Success: true } word && QuestionWords.Contains(word.Value))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        return QuestionPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- typed by the user (ah2-55)

    /// <summary>How the prompts the harness, a peer, a subagent or a plugin submit begin, after leading whitespace.</summary>
    private static readonly string[] InjectedStarts =
    [
        "<agent-message from=",
        "Another Claude session sent a message:",
        "<cross-session-message from=",
        "<task-notification>",
        "[SYSTEM NOTIFICATION - NOT USER INPUT]",
        "The wrangl plugin sent a message:",
        "The ah2 plugin sent a message:",
        "ah mail for ",
        "ah: reminder:",
        "<<autonomous-loop",
    ];

    [GeneratedRegex("\\Aah: You have [0-9]+ message", RegexOptions.CultureInvariant)]
    private static partial Regex DoorbellStart();

    [GeneratedRegex("<system-reminder>.*?(?:</system-reminder>|\\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SystemReminder();

    [GeneratedRegex("<command-args>(.*?)</command-args>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommandArgs();

    [GeneratedRegex("\\A\\[ah-entry [^\\]\\n]*\\]\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AhEntryLine();

    /// <summary>
    /// Whether a prompt is text the user typed, judged by how it starts: not a subagent hand-back or peer message, a cross-session
    /// message, a task notification, a system-reminder-only prompt, a plugin or daemon mail prompt, an <c>[ah-entry]</c>-only body, or a
    /// slash-command echo with no arguments. A shape quoted later in the text does not count.
    /// </summary>
    public static bool IsUserTyped(string prompt)
    {
        var text = prompt.TrimStart();
        if (text.Length == 0 || InjectedStarts.Any(s => text.StartsWith(s, StringComparison.Ordinal)) || DoorbellStart().IsMatch(text))
        {
            return false;
        }

        if (text.StartsWith("<system-reminder>", StringComparison.Ordinal) && SystemReminder().Replace(text, string.Empty).Trim().Length == 0)
        {
            return false;
        }

        if (text.StartsWith("<command-name>", StringComparison.Ordinal) || text.StartsWith("<command-message>", StringComparison.Ordinal)
            || text.StartsWith("<local-command-caveat>", StringComparison.Ordinal))
        {
            return CommandArgs().Match(text) is { Success: true } args && args.Groups[1].Value.Trim().Length > 0;
        }

        return !text.Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0).All(static l => AhEntryLine().IsMatch(l));
    }

    // ---------------------------------------------------------------- this turn's reply (§4)

    /// <summary>
    /// Every assistant text block after the turn's opening (§4.1), joined with a blank line, with the turn's <c>Final</c>, <c>HadTool</c>,
    /// <c>OpeningId</c> and, with <paramref name="isQuestion"/>, the <c>Direct</c> text of its latest real question. The scan reads the
    /// transcript backwards in <see cref="ScanWindow"/> windows, stops at the opening, and reads at most <see cref="TranscriptScanCap"/>
    /// bytes. A meta entry with no origin, a compact summary and a Stop hook's feedback are not an opening: the turn a Stop hook held goes
    /// on past its feedback. A mid-turn message (a <c>queued_command</c> attachment) is a question boundary, not a turn boundary.
    /// </summary>
    /// <para>With <paramref name="commands"/>, it also gathers the <c>command</c> of every tool call of the turn (last first).</para>
    public static QaTurnReply ReplyOf(IFileReader files, string transcriptPath, List<string>? commands = null, Func<string, bool>? isQuestion = null)
    {
        var blocks = new List<string>();
        var finals = new List<string>();
        var pending = new List<string>(); // text met since the last tool call met going back: the text before the earliest tool call after a boundary
        var hadTool = false;
        string? direct = null;
        string? question = null;
        foreach (var line in LinesBackward(files, transcriptPath, TranscriptScanCap, ScanWindow))
        {
            if (Entry(line.Span) is not { } entry)
            {
                continue;
            }

            var type = TypeOf(entry);
            if (type == "assistant")
            {
                foreach (var (kind, part, _) in PartsLastFirst(entry))
                {
                    if (kind == "text")
                    {
                        blocks.Add(part);
                        pending.Add(part);
                        if (!hadTool)
                        {
                            finals.Add(part);
                        }
                    }
                    else
                    {
                        hadTool = true;
                        pending.Clear();
                        if (commands is not null && part.Length > 0)
                        {
                            commands.Add(part);
                        }
                    }
                }

                continue;
            }

            var boundary = BoundaryText(entry, type, out var turnOpening);
            if (boundary is null)
            {
                continue;
            }

            if (question is null && isQuestion is not null && isQuestion(boundary))
            {
                direct = Joined([.. pending]);
                question = boundary;
            }

            if (turnOpening)
            {
                return Result(boundary, true, entry.Member("uuid") is { IsText: true } uuid ? uuid.AsString() : null);
            }
        }

        return Result(null, false, null);

        QaTurnReply Result(string? opening, bool found, string? openingId) => new(Joined(blocks), opening, found)
        {
            Final = hadTool ? Joined(finals) : null,
            HadTool = hadTool,
            OpeningId = openingId,
            Direct = direct,
            Question = question,
        };
    }

    /// <summary>
    /// PostToolUse's direct reply (§4.2 steps 1-3, Amendment 1 G2): when the tool call <paramref name="toolUseId"/> is the first or second
    /// tool step after a real question, the text between that question and its first tool call, with the question's boundary text; null
    /// when the call is a later step, follows no real question, the text is empty, or the call is not found within
    /// <see cref="FirstStepScanCap"/> bytes of the end (the transcript is written buffered, so a fast tool's call may not be there yet).
    /// </summary>
    public static (string Direct, string Question)? EarlyStepDirect(IFileReader files, string transcriptPath, string toolUseId, Func<string, bool> isQuestion)
    {
        var idBytes = Utf8Text.Encode("\"" + toolUseId + "\"");
        var found = false;
        var steps = 0; // the tool steps met going back, the call's own first
        string? stepMessage = null;
        var pending = new List<string>(); // text met since the last tool call met going back
        foreach (var line in LinesBackward(files, transcriptPath, FirstStepScanCap, FirstStepWindow))
        {
            var span = line.Span;
            if (!found && span.IndexOf(idBytes) < 0)
            {
                continue; // tool results of this step and parallel ones, and anything else written after the call
            }

            if (Entry(span) is not { } entry)
            {
                continue;
            }

            var type = TypeOf(entry);
            if (!found)
            {
                if (type != "assistant" || !PartsLastFirst(entry).Any(p => p.Kind == "tool_use" && p.Id == toolUseId))
                {
                    continue;
                }

                found = true;
            }

            if (type == "assistant")
            {
                foreach (var (kind, text, _) in PartsLastFirst(entry))
                {
                    if (kind == "text")
                    {
                        pending.Add(text);
                        continue;
                    }

                    // the tool calls of one API response are one step
                    pending.Clear();
                    var message = MessageId(entry);
                    if (steps == 0 || message is null || message != stepMessage)
                    {
                        steps++;
                        stepMessage = message;
                    }

                    if (steps > EarlySteps)
                    {
                        return null;
                    }
                }

                continue;
            }

            if (BoundaryText(entry, type, out _) is { } boundary)
            {
                return isQuestion(boundary) && Joined(pending) is { } direct ? (direct, boundary) : null;
            }
        }

        return null;
    }

    /// <summary>The tool steps of a question that try its direct reply (Amendment 1 G2); a later step does no database work.</summary>
    private const int EarlySteps = 2;

    /// <summary>
    /// The id of this session's stored question row that a transcript boundary's text names (Amendment 1 G1, Amendment 2), by the first
    /// rule that matches: (1) the row's text equals the boundary's, capped and trimmed as stored; (2) the boundary ends with a blank line
    /// and a whole row's text, as the mod bridge puts its hook context before a prompt (<c>hooks/register.ts</c>); (3) a row cut at the
    /// prompt cap whose stored text starts the boundary or follows a blank line in it. Newest first; a tie on the newest time is no match.
    /// A rule 2 match is passed over when a newer cut row matches by rule 3: the boundary is that long prompt, which ends in the older text.
    /// </summary>
    public static string? QuestionFor(IReadOnlyList<JsonValue> rows, string sessionId, string boundary)
    {
        var capped = new JsonObject();
        Text(capped, boundary, PromptCap);
        var exact = Str(capped, "text")!.Trim();
        var tail = boundary.TrimEnd();
        var prompts = rows.Where(r => KindOf(r) == "prompt" && Str(r, "session_id") == sessionId && Str(r, "id") is not null && Str(r, "text") is { Length: > 0 }).ToList();
        static bool Cut(JsonValue r) => r.Member("truncated") is { IsTruthy: true };
        static double At(JsonValue r) => r.Member("at") is { IsNumber: true } n ? n.AsNumber() : 0;

        // the newest of a rule's matches; Ambiguous when two share the newest time
        (JsonValue? Row, bool Ambiguous) Newest(IEnumerable<JsonValue> matches)
        {
            var list = matches.ToList();
            if (list.Count == 0)
            {
                return (null, false);
            }

            var newest = list.Max(At);
            var top = list.Where(r => At(r) == newest).ToList();
            return top.Count == 1 ? (top[0], false) : ((JsonValue?)null, true);
        }

        var equal = Newest(prompts.Where(r => Str(r, "text")!.Trim() == exact));
        if (equal.Row is not null || equal.Ambiguous)
        {
            return equal.Row is { } row ? Str(row, "id") : null;
        }

        var suffixes = prompts.Where(r => !Cut(r) && Str(r, "text")!.Trim() is { Length: > 0 } stored
            && tail.EndsWith(stored, StringComparison.Ordinal) && tail[..^stored.Length].TrimEnd(' ', '\t').EndsWith("\n\n", StringComparison.Ordinal)).ToList();
        var heads = prompts.Where(r => Cut(r) && Str(r, "text")! is var stored
            && (boundary.StartsWith(stored, StringComparison.Ordinal) || boundary.Contains("\n\n" + stored, StringComparison.Ordinal))).ToList();
        if (suffixes.Count > 0 && !heads.Any(h => At(h) > suffixes.Max(At)))
        {
            return Newest(suffixes).Row is { } bySuffix ? Str(bySuffix, "id") : null;
        }

        return Newest(heads).Row is { } byHead ? Str(byHead, "id") : null;
    }

    private const int ScanWindow = 256 * 1024;

    /// <summary>How far back PostToolUse reads (§4.2 budget); past it Stop backfills.</summary>
    public const int FirstStepScanCap = 256 * 1024;

    private const int FirstStepWindow = 64 * 1024;

    private const string StopFeedback = "Stop hook feedback:";

    /// <summary>The <c>origin.kind</c>s that make a meta user entry an opening (N2: the kinds seen opening a turn; any other is passed over).</summary>
    private static readonly HashSet<string> OpeningOrigins = new(StringComparer.Ordinal) { "peer", "plugin", "task-notification", "human" };

    /// <summary>The whole lines of a file from its end backwards, reading at most <paramref name="cap"/> bytes in <paramref name="window"/> windows.</summary>
    private static IEnumerable<ReadOnlyMemory<byte>> LinesBackward(IFileReader files, string path, int cap, int window)
    {
        var size = files.Stat(path)?.Size ?? 0;
        var end = size;
        byte[] carry = [];
        while (end > 0 && size - end < cap)
        {
            var start = Math.Max(0, end - window);
            var buffer = (byte[])[.. files.ReadRange(path, start, (int)(end - start)).Bytes, .. carry];
            var to = buffer.Length;
            for (var b = buffer.Length - 1; b >= 0; b--)
            {
                if (buffer[b] == (byte)'\n')
                {
                    yield return buffer.AsMemory(b + 1, to - b - 1);
                    to = b;
                }
            }

            // the piece before the window's first newline is whole only at the file's start; else it carries into the next window
            if (start == 0)
            {
                yield return buffer.AsMemory(0, to);
            }

            carry = start > 0 ? buffer[..to] : [];
            end = start;
        }
    }

    /// <summary>
    /// A conversation entry (user, assistant, or a <c>queued_command</c> attachment) that is not a sidechain's, else null. Any other line
    /// (an api-request, a snapshot, a queue operation) is passed over by a byte check before a full parse.
    /// </summary>
    private static JsonValue? Entry(ReadOnlySpan<byte> line)
    {
        if (line.Length == 0
            || (line.IndexOf("\"type\":\"user\""u8) < 0 && line.IndexOf("\"type\":\"assistant\""u8) < 0
                && (line.IndexOf("\"type\":\"attachment\""u8) < 0 || line.IndexOf("\"queued_command\""u8) < 0))
            || !JsonText.TryParse(line, out var entry) || !entry.IsMap || Flag(entry, "isSidechain"))
        {
            return null;
        }

        return entry;
    }

    private static string? TypeOf(JsonValue entry) => entry.Member("type") is { IsText: true } t ? t.AsString() : null;

    private static string? MessageId(JsonValue entry) => entry.Member("message")?.Member("id") is { IsText: true } id ? id.AsString() : null;

    /// <summary>
    /// An assistant entry's non-empty text parts and its tool calls, last first: <c>Text</c> is a text's text or a tool call's
    /// <c>input.command</c> (empty when it has none), <c>Id</c> a tool call's id.
    /// </summary>
    private static IEnumerable<(string Kind, string Text, string? Id)> PartsLastFirst(JsonValue entry)
    {
        if (entry.Member("message")?.Member("content") is not { IsArray: true } parts)
        {
            yield break;
        }

        for (var p = parts.AsArray().Count - 1; p >= 0; p--)
        {
            var part = parts.AsArray()[p];
            var kind = part.Member("type") is { IsText: true } pt ? pt.AsString() : null;
            if (kind == "text" && part.Member("text") is { IsText: true } text && text.AsString().Trim().Length > 0)
            {
                yield return ("text", text.AsString(), null);
            }
            else if (kind == "tool_use")
            {
                yield return ("tool_use", part.Member("input")?.Member("command") is { IsText: true } command ? command.AsString() : string.Empty,
                    part.Member("id") is { IsText: true } id ? id.AsString() : null);
            }
        }
    }

    /// <summary>
    /// The text of an entry that opens a question (§4.1), else null. A user entry that is a prompt (not a tool result, a compact summary
    /// or a Stop hook's feedback) and is not meta, or is meta with an opening origin, opens the turn too (<paramref name="turnOpening"/>);
    /// a <c>queued_command</c> attachment, a message absorbed mid-turn, opens a question only.
    /// </summary>
    private static string? BoundaryText(JsonValue entry, string? type, out bool turnOpening)
    {
        turnOpening = false;
        if (type == "attachment")
        {
            return entry.Member("attachment") is { IsMap: true } a && a.Member("type") is { IsText: true } at && at.AsString() == "queued_command"
                ? UserPromptText(a.Member("prompt")) ?? string.Empty
                : null;
        }

        if (type != "user" || Flag(entry, "isCompactSummary")
            || (Flag(entry, "isMeta") && !(entry.Member("origin")?.Member("kind") is { IsText: true } kind && OpeningOrigins.Contains(kind.AsString()))))
        {
            return null;
        }

        if (UserPromptText(entry.Member("message")?.Member("content")) is not { } opening || opening.StartsWith(StopFeedback, StringComparison.Ordinal))
        {
            return null;
        }

        turnOpening = true;
        return opening;
    }

    private static bool Flag(JsonValue entry, string name) => entry.Member(name) is { IsBool: true } b && b.AsBool();

    private static string? Joined(List<string> reversed)
    {
        if (reversed.Count == 0)
        {
            return null;
        }

        reversed.Reverse();
        return string.Join("\n\n", reversed);
    }

    /// <summary>The text of a user entry that is a prompt, or null for a tool result.</summary>
    private static string? UserPromptText(JsonValue? content)
    {
        if (content is { IsText: true } s)
        {
            return s.AsString();
        }

        if (content is not { IsArray: true } parts || parts.AsArray().Count == 0)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var part in parts.AsArray())
        {
            var type = part.Member("type") is { IsText: true } t ? t.AsString() : null;
            if (type == "tool_result")
            {
                return null;
            }

            if (type == "text" && part.Member("text") is { IsText: true } text)
            {
                texts.Add(text.AsString());
            }
        }

        return string.Join("\n", texts);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// The rows folded into threads, newest first: every reply joins the prompt it names; replies naming no prompt held in the log form
    /// one unprompted thread per session. A prompt the user did not type (<see cref="IsUserTyped"/>) is left out with its replies.
    /// </summary>
    public static IReadOnlyList<QaThread> Threads(IReadOnlyList<JsonValue> rows)
    {
        var prompts = new Dictionary<string, (JsonValue Row, List<JsonValue> Replies)>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var unprompted = new Dictionary<string, List<JsonValue>>(StringComparer.Ordinal);
        var threads = new List<QaThread>();
        foreach (var row in rows.Where(static r => KindOf(r) == "prompt"))
        {
            var id = Str(row, "id") ?? string.Empty;
            if (Str(row, "text") is { } text && !IsUserTyped(text))
            {
                _ = hidden.Add(id);
                continue;
            }

            prompts.TryAdd(id, (row, []));
        }

        foreach (var row in rows)
        {
            switch (KindOf(row))
            {
                case "ask":
                    threads.Add(new QaThread(null, [], row));
                    break;
                case "reply" when Str(row, "prompt_id") is { } pid && prompts.TryGetValue(pid, out var p):
                    p.Replies.Add(row);
                    break;
                case "reply" when Str(row, "prompt_id") is { } hiddenId && hidden.Contains(hiddenId):
                    break;
                case "reply":
                    var session = Str(row, "session_id") ?? string.Empty;
                    if (!unprompted.TryGetValue(session, out var list))
                    {
                        unprompted[session] = list = [];
                    }

                    list.Add(row);
                    break;
            }
        }

        threads.AddRange(prompts.Values.Select(static p => new QaThread(p.Row, p.Replies, null)));
        threads.AddRange(unprompted.Values.Select(static r => new QaThread(null, r, null)));
        return [.. threads.OrderByDescending(static t => t.At)];
    }

    /// <summary>A thread as one JSON object, <c>{prompt, replies, ask}</c>: what <c>ah msg history --json</c> prints and the thread read returns.</summary>
    public static JsonObject ThreadJson(QaThread t)
    {
        var replies = new JsonArray();
        replies.AddRange(t.Replies);
        return new JsonObject { ["prompt"] = t.Prompt ?? JsonValue.Null, ["replies"] = replies, ["ask"] = t.Ask ?? JsonValue.Null };
    }

    // ---------------------------------------------------------------- search and filters (§4.6)

    public const int MaxLimit = 100;

    /// <summary>How much of a reply's <c>text</c> and <c>final</c> a list read returns; the whole thread comes from the id read.</summary>
    public const int ListHeadBytes = 600;

    /// <summary>A cursor's text, <c>&lt;at&gt;:&lt;head id&gt;</c>, or null when it is not one.</summary>
    public static QaCursor? CursorOf(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < text.Length - 1
            && double.TryParse(text.AsSpan(0, colon), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at) && double.IsFinite(at)
                ? new QaCursor(at, text[(colon + 1)..])
                : null;
    }

    /// <summary>
    /// The threads <paramref name="q"/> keeps, newest first (<c>At</c>, then head id, both descending: a total order), cut to its limit after
    /// every filter. A cursor names a place in that order, not a thread, so a thread added since, or a cursor from another filter, moves nothing.
    /// </summary>
    // ponytail: a linear pass over the dir's whole parsed 30-day log, which every read parses anyway; past about 50 MB a dir, add FTS5 as migration V45.
    public static QaPage Page(IReadOnlyList<QaThread> threads, QaHistoryQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);
        var search = string.IsNullOrEmpty(q.Search) ? null : q.Search;
        var page = new List<QaThread>(limit);

        // ponytail: an unprompted thread (shown only with --all) is placed by its first reply, and a replaced turn row moves that reply's
        // at, so --all paging can skip or repeat such a thread across pages; give unprompted threads a fixed key if --all paging matters.
        foreach (var t in threads.OrderByDescending(static t => t.At).ThenByDescending(HeadId, StringComparer.Ordinal))
        {
            if (!Keeps(t, q, search))
            {
                continue;
            }

            if (page.Count == limit)
            {
                return new QaPage(page, new QaCursor(page[^1].At, HeadId(page[^1])));
            }

            page.Add(t);
        }

        return new QaPage(page, null);
    }

    private static bool Keeps(QaThread t, QaHistoryQuery q, string? search)
    {
        var head = HeadRow(t);
        if (q.Id is not null ? Str(head, "id") != q.Id : !(q.All || t.Prompt is not null || t.Ask is not null))
        {
            return false;
        }

        if ((q.Questions && !(t.Prompt?.Member("question") is { IsBool: true } question && question.AsBool()))
            || (q.Since is { } since && t.At < since)
            || (q.Team is { } team && Str(head, "team") is { } own && own != team)
            || (q.Answered is { } answered && (answered ? !IsAnswered(t) : !(t.Prompt is not null && t.Replies.Count == 0)))
            || (q.Before is { } c && !(t.At < c.At || (t.At == c.At && string.CompareOrdinal(HeadId(t), c.Id) < 0))))
        {
            return false;
        }

        return search is null || Searched(t).Any(s => s.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonValue HeadRow(QaThread t) => t.Prompt ?? t.Ask ?? t.Replies[0];

    private static string HeadId(QaThread t) => Str(HeadRow(t), "id") ?? string.Empty;

    private static bool IsAnswered(QaThread t) =>
        t.Prompt is not null ? t.Replies.Count > 0 : t.Ask?.Member("answers") is { IsMap: true } answers && answers.AsObject().Count > 0;

    /// <summary>The text a search reads: the prompt, every reply's <c>text</c> and <c>final</c>, and every string value of an ask's questions and answers.</summary>
    private static IEnumerable<string> Searched(QaThread t)
    {
        if (t.Prompt is { } prompt && Str(prompt, "text") is { } asked)
        {
            yield return asked;
        }

        foreach (var reply in t.Replies)
        {
            foreach (var key in (string[])["text", "final"])
            {
                if (Str(reply, key) is { } text)
                {
                    yield return text;
                }
            }
        }

        if (t.Ask is { } ask)
        {
            foreach (var text in Leaves(ask.Member("questions")).Concat(Leaves(ask.Member("answers"))))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> Leaves(JsonValue? value)
    {
        if (value is { IsText: true } text)
        {
            yield return text.AsString();
        }
        else if (value is { IsArray: true } array)
        {
            foreach (var item in array.AsArray().SelectMany(static v => Leaves(v)))
            {
                yield return item;
            }
        }
        else if (value is { IsMap: true } map)
        {
            var o = map.AsObject();
            foreach (var item in o.Keys.SelectMany(k => Leaves(o[k])))
            {
                yield return item;
            }
        }
    }

    /// <summary>A thread for a list read: <see cref="ThreadJson"/> with each reply's <c>text</c> and <c>final</c> cut to <see cref="ListHeadBytes"/>, marked <c>cut: true</c>.</summary>
    public static JsonObject ListThreadJson(QaThread t)
    {
        var replies = new JsonArray();
        foreach (var reply in t.Replies)
        {
            JsonObject? copy = null;
            foreach (var key in (string[])["text", "final"])
            {
                if (Str(reply, key) is { } text && Utf8Text.Encode(text) is { Length: > ListHeadBytes } bytes)
                {
                    copy ??= reply.AsObject().Clone();
                    copy[key] = Utf8Text.Decode(bytes.AsSpan(0, ListHeadBytes)).TrimEnd('�');
                    copy["cut"] = true;
                }
            }

            replies.Add(copy is null ? reply : copy);
        }

        return new JsonObject { ["prompt"] = t.Prompt ?? JsonValue.Null, ["replies"] = replies, ["ask"] = t.Ask ?? JsonValue.Null };
    }

    private static string? KindOf(JsonValue row) => Str(row, "kind");

    private static string? Str(JsonValue row, string key) => row.Member(key) is { IsText: true } t ? t.AsString() : null;
}
