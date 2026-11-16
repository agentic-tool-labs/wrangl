using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

/// <summary>Recovery from a turn that ended on an API error: which errors retry, how long to wait, and how a failure is read from a transcript.</summary>
public interface IHierarchyRecovery
{
    /// <summary>Error kinds that may clear on their own. Anything else is escalated at once and never resumed.</summary>
    IReadOnlyCollection<string> Retryable { get; }

    /// <summary>Two failures this close (milliseconds) are one failure seen twice.</summary>
    int SameFailureMs { get; }

    /// <summary>A failure's streak counts the resumes emitted for the same session in this long before it (milliseconds).</summary>
    int StreakWindowMs { get; }

    string ResumeParagraph { get; }

    /// <summary>True for an error kind that is never resumed, however often it is seen: a limit that only the passing of days clears.</summary>
    bool NeverResumed(string errorKind);

    /// <summary>Seconds to wait before resume <paramref name="streak"/> (1-based); undefined (null) for a fractional streak inside the schedule (between 1 and 3 for a rate limit, 1 and 4 otherwise).</summary>
    JsonValue? DelaySec(JsonValue? errorKind, JsonValue? streak);

    /// <summary>
    /// Seconds to wait before the resume for the <paramref name="k"/>th consecutive API-error turn end (1-based): none, a minute, five
    /// minutes, then ten minutes between tries. An error that a retry cannot fix waits ten minutes from the first.
    /// </summary>
    double ResumeDelaySec(string errorKind, int k);

    /// <summary>1 plus the distinct RESUME rows for <paramref name="subject"/> in the window before <paramref name="failedAtMs"/>.</summary>
    double StreakFor(JsonValue? rows, JsonValue? subject, double failedAtMs);

    /// <summary>One line of text with escape sequences and control characters gone, cut to <paramref name="cap"/> characters.</summary>
    string OneLine(JsonValue? value, JsonValue? cap);

    /// <summary>The error kind to record: <c>unknown</c> when the payload names none, else the name cut to a safe token.</summary>
    string ErrorKind(JsonValue? value);

    /// <summary>
    /// The kind a StopFailure records, read as <see cref="TranscriptFailure"/> reads it: the text first, so a weekly limit the payload calls <c>rate_limit</c> is never resumed.
    /// <paramref name="details"/> is the payload's error text, <paramref name="transcript"/> what <see cref="TranscriptFailure"/> found for this turn end or null; else <see cref="ErrorKind"/> of <paramref name="named"/>, the kind the payload names.
    /// </summary>
    string FailureKind(JsonValue? named, JsonValue? details, JsonValue? transcript);

    /// <summary>The real path of <paramref name="file"/> when it is an absolute <c>.jsonl</c> under <c>~/.claude/projects/</c>, else null.</summary>
    string? TranscriptPathOk(JsonValue? file);

    /// <summary>The key one turn end is known by, whichever path sees it: the session plus the error entry's transcript uuid, or its timestamp when it has none.</summary>
    string TurnEndKey(string subject, string? uuid, string failedAt);

    /// <summary>
    /// <c>{error, failed_at}</c> plus <c>uuid</c> when the entry has one when the last conversation entry of the transcript is an API-error message, else null. The file is
    /// read only when its modification time and size differ from the stamp <paramref name="seen"/> holds for it, which is updated.
    /// </summary>
    JsonValue TranscriptFailure(JsonValue? file, IDictionary<string, string>? seen);

    /// <summary>
    /// The API-error messages of the transcript <paramref name="file"/> (checked as <see cref="TranscriptPathOk"/> does) with a time
    /// of <paramref name="notBefore"/> or later, and the offset to continue from. With no <paramref name="offset"/>, or one past the end
    /// of the file, the last 256 KB are read; otherwise only the complete lines after the offset. Null when the file cannot be read.
    /// </summary>
    ApiErrorTail? ApiErrorsFrom(string? file, long? offset, DateTimeOffset notBefore) => null;
}

/// <summary>One API-error message of a transcript: Recovery's error kind and the time the message carries.</summary>
public sealed record ApiErrorEntry(string Kind, DateTimeOffset At);

/// <summary>The API errors found in a stretch of a transcript and where the next stretch starts.</summary>
public sealed record ApiErrorTail(long Offset, IReadOnlyList<ApiErrorEntry> Errors);

internal sealed partial class RecoveryLib(IFileReader files, IAhEnvironment environment, IClock clock) : IHierarchyRecovery
{
    private const int TailBytes = 64 * 1024;

    private const int FirstReadBytes = 256 * 1024;

    private const int AppendBytes = 1024 * 1024;

    /// <summary>Seconds before resume 1, 2, 3 and every later one: a short first wait, then ten minutes between tries.</summary>
    private static readonly double[] Backoff = [10, 60, 300, 600];

    /// <summary>A rate limit lasts minutes, so a short retry only spends a request: its waits are longer.</summary>
    private static readonly double[] RateLimitBackoff = [120, 480, 1200];

    /// <summary>Seconds before the resume for the first, second, third and every later consecutive failure.</summary>
    private static readonly double[] ResumeBackoff = [0, 60, 300, 600];

    private static readonly string[] RetryableKinds = ["server_error", "overloaded", "rate_limit", "unknown", TransportKind];

    /// <summary>The kind of a turn that ended because the connection dropped.</summary>
    internal const string TransportKind = "transport";

    internal const string WeeklyLimitKind = "weekly_limit";

    /// <summary>The texts of the synthetic API-error message a dropped connection leaves; each is what the message starts with.</summary>
    private static readonly string[] TransportTexts =
    [
        "Connection lost mid-response",
        "The response stopped arriving",
        "No response from API",
        "Can't reach the API server",
    ];

    public IReadOnlyCollection<string> Retryable => RetryableKinds;

    public bool NeverResumed(string errorKind) => errorKind == WeeklyLimitKind;

    public int SameFailureMs => 10000;

    public int StreakWindowMs => 45 * 60 * 1000;

    public string ResumeParagraph =>
        "Continue the interrupted task. First re-check state: re-read any file you were changing, and for anything with side effects (push, merge, send, close, delete) confirm whether it already happened before doing it again. If you were mid-report, finish and send the report.";

    public JsonValue? DelaySec(JsonValue? errorKind, JsonValue? streak)
    {
        var row = errorKind is { IsText: true } e && e.AsString() == "rate_limit" ? RateLimitBackoff : Backoff;
        var n = JsConvert.ToNumber(streak);
        if (double.IsNaN(n) || n == 0)
        {
            n = 1;
        }

        var index = Math.Min(Math.Max(n, 1), row.Length) - 1;
        return index == Math.Floor(index) ? row[(int)index] : null;
    }

    public double ResumeDelaySec(string errorKind, int k) =>
        RetryableKinds.Contains(errorKind) ? ResumeBackoff[Math.Clamp(k, 1, ResumeBackoff.Length) - 1] : ResumeBackoff[^1];

    public string TurnEndKey(string subject, string? uuid, string failedAt) =>
        subject + ":" + (string.IsNullOrEmpty(uuid) ? failedAt : uuid);

    public double StreakFor(JsonValue? rows, JsonValue? subject, double failedAtMs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rows is { IsArray: true } list)
        {
            foreach (var r in list.AsArray())
            {
                if (!r.IsTruthy || r.Member("type") is not { IsText: true } type || type.AsString() != "watch-event"
                    || r.Member("kind") is not { IsText: true } kind || kind.AsString() != "RESUME" || !SameSubject(r.Member("subject"), subject))
                {
                    continue;
                }

                var ts = r.Member("ts");
                if (!Timestamps.TryParseMs(ts is null ? null : JsConvert.ToJsString(ts), out var at)
                    || at >= failedAtMs || at < failedAtMs - StreakWindowMs)
                {
                    continue;
                }

                var failedAt = r.Member("failed_at");
                seen.Add(JsOps.SetKey(failedAt is { IsText: true } ? failedAt.Value : ts ?? JsonValue.Null) ?? "ref:" + seen.Count);
            }
        }

        return 1 + seen.Count;
    }

    public string OneLine(JsonValue? value, JsonValue? cap)
    {
        if (value is not { IsText: true } v)
        {
            return string.Empty;
        }

        var text = AnsiRegex().Replace(v.AsString(), string.Empty);
        text = ControlRegex().Replace(text, " ");
        text = SpaceRegex().Replace(text, " ");
        text = JsText.Trim(text);
        var limit = JsConvert.ToNumber(cap);
        return text.Length > limit ? JsOps.SliceTo(text, limit) : text;
    }

    public string ErrorKind(JsonValue? value)
    {
        if (value is not { IsText: true } v)
        {
            return "unknown";
        }

        var token = TokenCharsRegex().Replace(v.AsString(), string.Empty);
        if (token.Length > 40)
        {
            token = token[..40];
        }

        return token.Length == 0 ? "unknown" : token;
    }

    public string FailureKind(JsonValue? named, JsonValue? details, JsonValue? transcript)
    {
        var said = details is { IsText: true } text ? KindFromText(text.AsString()) : null;
        var read = transcript?.Member("error") is { IsText: true } found && found.AsString() is WeeklyLimitKind or TransportKind ? found.AsString() : null;
        return said == WeeklyLimitKind || read == WeeklyLimitKind ? WeeklyLimitKind : said ?? read ?? ErrorKind(named);
    }

    public string? TranscriptPathOk(JsonValue? file)
    {
        if (file is not { IsText: true } f)
        {
            return null;
        }

        var path = f.AsString();
        if (path.Length > 1024 || !NodePath.IsAbsolute(path) || !path.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var root = files.RealPath(NodePath.Join(environment.HomeDirectory, ".claude", "projects"));
            var real = files.RealPath(path);
            return real.StartsWith(root + "/", StringComparison.Ordinal) ? real : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public JsonValue TranscriptFailure(JsonValue? file, IDictionary<string, string>? seen)
    {
        var real = TranscriptPathOk(file);
        if (real is null || seen is null)
        {
            return JsonValue.Null;
        }

        try
        {
            var stat = files.Stat(real) ?? throw new FileNotFoundException(real);
            var stamp = JsConvert.NumberToString(stat.MtimeMs) + ":" + JsConvert.NumberToString(stat.Size);
            if (seen.TryGetValue(real, out var previous) && previous == stamp)
            {
                return JsonValue.Null;
            }

            seen[real] = stamp;
            var tail = files.ReadTail(real, TailBytes);
            var lines = Utf8Text.Decode(tail.Bytes).Split('\n').ToList();
            if (tail.StartsMidFile)
            {
                lines.RemoveAt(0);
            }

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (JsText.Trim(lines[i]).Length == 0)
                {
                    continue;
                }

                JsonValue entry;
                try
                {
                    entry = JsonText.Parse(lines[i]);
                }
                catch (JsonParseException)
                {
                    continue;
                }

                if (!entry.IsMap || entry.Member("isSidechain") is { IsBool: true } side && side.AsBool())
                {
                    continue;
                }

                var type = entry.Member("type") is { IsText: true } t ? t.AsString() : null;
                if (type is not ("assistant" or "user"))
                {
                    continue;
                }

                if (type != "assistant" || !IsApiErrorMessage(entry))
                {
                    return JsonValue.Null;
                }

                var failedAt = FailedAt(entry);
                var failure = new JsonObject
                {
                    ["error"] = KindFromText(MessageText(entry)) ?? ErrorKind(entry.Member("error")),
                    ["failed_at"] = Timestamps.ToIsoString(failedAt),
                };
                if (entry.Member("uuid") is { IsText: true } id && id.AsString().Length > 0)
                {
                    failure["uuid"] = id;
                }

                return failure;
            }

            return JsonValue.Null;
        }
        catch (Exception)
        {
            return JsonValue.Null;
        }
    }

    /// <summary>The text of an assistant entry: its content when that is a string, else its text blocks joined.</summary>
    private static string MessageText(JsonValue entry)
    {
        var content = entry.Member("message")?.Member("content");
        if (content is { IsText: true } plain)
        {
            return plain.AsString();
        }

        return content is { IsArray: true } blocks
            ? string.Join('\n', blocks.AsArray().Select(b => b.Member("text") is { IsText: true } t ? t.AsString() : string.Empty))
            : string.Empty;
    }

    /// <summary>The weekly limit wins over a transport text; null when the text names neither.</summary>
    private static string? KindFromText(string text)
    {
        var trimmed = text.Replace('’', '\'').Trim();
        if (trimmed.Contains("weekly limit", StringComparison.OrdinalIgnoreCase))
        {
            return WeeklyLimitKind;
        }

        return TransportTexts.Any(t => trimmed.StartsWith(t, StringComparison.Ordinal)) ? TransportKind : null;
    }

    private static bool IsApiErrorMessage(JsonValue entry) => entry.Member("isApiErrorMessage") is { IsBool: true } api && api.AsBool();

    private DateTimeOffset FailedAt(JsonValue entry)
    {
        var stamped = entry.Member("timestamp");
        return Timestamps.TryParseMs(stamped is null ? null : JsConvert.ToJsString(stamped), out var at)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)at)
            : clock.UtcNow;
    }

    public ApiErrorTail? ApiErrorsFrom(string? file, long? offset, DateTimeOffset notBefore)
    {
        var real = file is null ? null : TranscriptPathOk(JsonValue.FromString(file));
        if (real is null)
        {
            return null;
        }

        try
        {
            var length = (files.Stat(real) ?? throw new FileNotFoundException(real)).Size;
            byte[] bytes;
            long start;
            var skipFirstLine = false;
            if (offset is { } known && known <= length)
            {
                if (known == length)
                {
                    return new ApiErrorTail(length, []);
                }

                start = known;
                bytes = files.ReadRange(real, known, AppendBytes).Bytes;
            }
            else
            {
                var tail = files.ReadTail(real, FirstReadBytes);
                bytes = tail.Bytes;
                start = length - bytes.Length;
                skipFirstLine = tail.StartsMidFile;
            }

            var consumed = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            if (consumed == 0 && bytes.Length >= AppendBytes)
            {
                consumed = bytes.Length;
            }
            var errors = new List<ApiErrorEntry>();
            var lines = Utf8Text.Decode(bytes.AsSpan(0, consumed).ToArray()).Split('\n');
            for (var i = skipFirstLine ? 1 : 0; i < lines.Length; i++)
            {
                if (JsText.Trim(lines[i]).Length == 0)
                {
                    continue;
                }

                JsonValue entry;
                try
                {
                    entry = JsonText.Parse(lines[i]);
                }
                catch (JsonParseException)
                {
                    continue;
                }

                if (!entry.IsMap || entry.Member("isSidechain") is { IsBool: true } side && side.AsBool()
                    || entry.Member("type") is not { IsText: true } type || type.AsString() != "assistant" || !IsApiErrorMessage(entry))
                {
                    continue;
                }

                var at = FailedAt(entry);
                if (at >= notBefore)
                {
                    errors.Add(new ApiErrorEntry(KindFromText(MessageText(entry)) ?? ErrorKind(entry.Member("error")), at));
                }
            }

            return new ApiErrorTail(start + consumed, errors);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameSubject(JsonValue? row, JsonValue? wanted) =>
        (row is null && wanted is null)
        || (row is { } r && wanted is { } w && r.Kind == w.Kind && JsOps.SetKey(r) is { } a && JsOps.SetKey(w) is { } b && a == b);

    [GeneratedRegex(@"\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)|\u001b\[[0-9;?]*[ -/]*[@-~]|\u001b[@-Z\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"[\u0000-\u001f\u007f-\u009f]+", RegexOptions.CultureInvariant)]
    private static partial Regex ControlRegex();

    [GeneratedRegex("[" + JsText.SpaceClass + "]+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRegex();

    [GeneratedRegex("[^A-Za-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenCharsRegex();
}
