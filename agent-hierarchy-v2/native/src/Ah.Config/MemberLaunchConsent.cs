using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Config;

/// <summary>
/// Whether the user has consented to ah writing launch-only permission rules for the members it spawns. The record is the trusted
/// AskUserQuestion row the ask-record hook writes to <c>asks.jsonl</c>; nothing else counts. A row counts only when it carries the
/// consent source tag and the ask-record hook's <c>writer</c> mark, holds exactly the canonical consent question with exactly the canonical options, and has an answer for that
/// question.
/// </summary>
internal static class MemberLaunchConsent
{
    /// <summary>The <c>writer</c> field the ask-record hook stamps on every row it appends; a row without it is not a consent row.</summary>
    public const string AskRecordWriter = "posttooluse-ask-record";

    /// <summary>The <c>metadata.source</c> key and value that mark the consent question.</summary>
    public const string SourceKey = "consent";

    public const string SourceValue = "member-launch-rules";

    public const string Allow = "Allow";

    public const string NotNow = "Not now";

    /// <summary>The text of the consent question, as asked and as the answer is keyed.</summary>
    public const string Question = "Allow ah to give the members it spawns launch-only permission rules and an OS sandbox?";

    /// <summary>The consent question's header (at most 12 characters).</summary>
    public const string Header = "Member rules";

    /// <summary>The consent question's option labels, in order.</summary>
    public static IReadOnlyList<string> Options { get; } = [Allow, NotNow];

    /// <summary>
    /// Member launch rules are on by default: effective unless <paramref name="enabled"/> is false (<c>ah allow off</c>) or the newest
    /// consent row records a decline newer than <paramref name="onAt"/>, the time of the user's last <c>ah allow on</c> (an ISO
    /// timestamp, null when there was none): the newest user act wins. No row, and a row of any consent version, leave them on. A row is a consent row only if
    /// it is a map with a timestamp, a <c>metadata.source</c> naming the consent, and <c>questions</c> that hold exactly the canonical
    /// question with exactly the canonical options; any other row is ignored. Any answer but <see cref="Allow"/> (<see cref="NotNow"/>,
    /// another label, a free-text "Other" answer, none) is a decline.
    /// </summary>
    public static MemberLaunchVerdict Evaluate(IEnumerable<JsonValue> askRows, bool enabled, string? onAt = null)
    {
        ArgumentNullException.ThrowIfNull(askRows);
        if (!enabled)
        {
            return new MemberLaunchVerdict(false, "disabled by ah allow off");
        }

        string? newestAt = null;
        JsonValue? newest = null;
        int? version = null;
        foreach (var row in askRows)
        {
            if (!row.IsMap || row.Member("writer") is not { IsText: true } writer || !IsAnswerWriter(writer.AsString())
                || row.Member("at") is not { IsText: true } at
                || row.Member("metadata")?.Member("source") is not { IsText: true } source || VersionOf(WithoutParkedRef(source.AsString())) is not { } cv
                || !AsksTheConsentQuestion(row))
            {
                continue;
            }

            if (newestAt is null || string.CompareOrdinal(at.AsString(), newestAt) >= 0)
            {
                newestAt = at.AsString();
                newest = row;
                version = cv;
            }
        }

        if (newest is null)
        {
            return new MemberLaunchVerdict(true, null);
        }

        var answer = newest.Value.Member("response")?.Member("answers")?.Member(Question);
        var declined = answer is not { IsText: true } chosen || !IsAllowAnswer(chosen.AsString());
        return declined && !OnIsNewer(onAt, newestAt)
            ? new MemberLaunchVerdict(false, "declined") { Version = version, At = newestAt }
            : new MemberLaunchVerdict(true, null) { Version = version, At = newestAt };
    }

    /// <summary>Whether the <c>ah allow on</c> time is at or after the decline's time; a time that does not parse as a date never overrides a decline.</summary>
    private static bool OnIsNewer(string? onAt, string? declinedAt) =>
        onAt is not null && declinedAt is not null
        && DateTimeOffset.TryParse(onAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var on)
        && DateTimeOffset.TryParse(declinedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var declined)
        && on >= declined;

    /// <summary>
    /// Whether an answer is consent: exactly <see cref="Allow"/>, or text that, trimmed, starts with the whole word "Allow" (any case)
    /// so a typed "Allow, again" counts while "Allowance" and "Not now" do not.
    /// </summary>
    public static bool IsAllowAnswer(string answer)
    {
        var text = answer.Trim();
        return text.StartsWith(Allow, StringComparison.OrdinalIgnoreCase)
            && (text.Length == Allow.Length || !char.IsLetterOrDigit(text[Allow.Length]));
    }

    /// <summary>The <c>writer</c> of an answer the user gave outside the harness to a question parked for them.</summary>
    public const string ParkedAnswerWriter = "decision-answer";

    private static bool IsAnswerWriter(string writer) => writer == AskRecordWriter || writer == ParkedAnswerWriter;

    /// <summary>A source tag without the one trailing <c>;ref=q&lt;k&gt;</c> that an out-of-band answer adds to name the parked question.</summary>
    private static string WithoutParkedRef(string source)
    {
        var cut = source.LastIndexOf(";ref=q", StringComparison.Ordinal);
        return cut >= 0 && source.AsSpan(cut + 6).Length > 0 && source.AsSpan(cut + 6).IndexOfAnyExceptInRange('0', '9') < 0 ? source[..cut] : source;
    }

    /// <summary>The source tag of the question that proves an Orchestrator's <c>ah allow on</c>.</summary>
    public const string AllowOnSource = "ah:R7;allow=on:member-launch-rules";

    /// <summary>The text of the question whose Allow answer proves an <c>ah allow on</c>; the answer is keyed by it.</summary>
    public const string AllowOnQuestion = "Widen member launch rules?";

    /// <summary>How old, at most, an Allow answer may be to prove an <c>ah allow on</c>.</summary>
    public static readonly TimeSpan AllowOnProofLife = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The timestamp of the newest hook-written row (with the ask-record <c>writer</c> mark) tagged <see cref="AllowOnSource"/> whose
    /// answer is <see cref="Allow"/>, no older than <see cref="AllowOnProofLife"/> before <paramref name="now"/>, and newer than
    /// <paramref name="usedAt"/>; null when there is none.
    /// </summary>
    public static string? FreshAllowOnProof(IFileReader files, string asksPath, DateTimeOffset now, string? usedAt)
    {
        ArgumentNullException.ThrowIfNull(files);

        // per-dir-files-to-db §6c G5: one read; an asks store that cannot be read proves nothing
        if (!files.ReadAskLog(asksPath).TryLines(out var lines))
        {
            return null;
        }

        string? best = null;
        foreach (var line in lines)
        {
            JsonValue row;
            try
            {
                row = JsonText.Parse(line);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (!row.IsMap || row.Member("writer") is not { IsText: true } writer || !IsAnswerWriter(writer.AsString())
                || row.Member("at") is not { IsText: true } at
                || row.Member("metadata")?.Member("source") is not { IsText: true } source || WithoutParkedRef(source.AsString()) != AllowOnSource
                || !HasAllowAnswer(row)
                || !DateTimeOffset.TryParse(at.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                || when > now || now - when > AllowOnProofLife
                || (usedAt is not null && string.CompareOrdinal(at.AsString(), usedAt) <= 0))
            {
                continue;
            }

            if (best is null || string.CompareOrdinal(at.AsString(), best) > 0)
            {
                best = at.AsString();
            }
        }

        return best;
    }

    private static bool HasAllowAnswer(JsonValue row) =>
        row.Member("response")?.Member("answers")?.Member(AllowOnQuestion) is { IsText: true } a && a.AsString() == Allow;

    /// <summary>The same, over the lines of an <c>asks.jsonl</c> file (a missing file holds no row; a line that is not JSON is skipped).</summary>
    public static MemberLaunchVerdict Evaluate(IFileReader files, string asksPath, bool enabled, string? onAt = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        // per-dir-files-to-db §6c G3/G5: one read; while the asks store cannot be read, a decline it holds may be unseen, so the rules
        // are off for this read, as if declined; the next readable read decides as usual
        if (!files.ReadAskLog(asksPath).TryLines(out var lines))
        {
            return enabled ? new MemberLaunchVerdict(false, "the asks store could not be read") : Evaluate([], enabled, onAt);
        }

        var rows = new List<JsonValue>();
        foreach (var line in lines)
        {
            try
            {
                rows.Add(JsonText.Parse(line));
            }
            catch (JsonParseException)
            {
            }
        }

        return Evaluate(rows, enabled, onAt);
    }

    /// <summary>Whether the row's <c>questions</c> hold the canonical question with exactly the canonical option labels.</summary>
    private static bool AsksTheConsentQuestion(JsonValue row)
    {
        if (row.Member("questions") is not { IsArray: true } questions)
        {
            return false;
        }

        foreach (var q in questions.AsArray())
        {
            if (!q.IsMap || q.Member("question") is not { IsText: true } text || text.AsString() != Question || q.Member("options") is not { IsArray: true } options)
            {
                continue;
            }

            var labels = options.AsArray().Select(static o => o.IsMap && o.Member("label") is { IsText: true } l ? l.AsString() : null).ToList();
            if (labels.SequenceEqual(Options, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a question's source tag (with or without the parked <c>;ref=q&lt;k&gt;</c>) marks the member-rules consent question, by the test the reader applies to a consent row.</summary>
    public static bool IsConsentSource(string? source) => source is not null && VersionOf(WithoutParkedRef(source)) is not null;

    /// <summary>The <c>cv</c> of a source tag <c>ah:R7;consent=member-launch-rules;cv=&lt;n&gt;</c>, or null when the tag is not that.</summary>
    private static int? VersionOf(string source)
    {
        var parts = source.Split(';');
        if (parts.Length != 3 || parts[0] != "ah:R7" || parts[1] != $"{SourceKey}={SourceValue}" || !parts[2].StartsWith("cv=", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var cv))
        {
            return null;
        }

        return cv;
    }
}

/// <summary>Whether consent is in effect and, when it is not, why.</summary>
/// <param name="Effective">Whether launch rules may be written.</param>
/// <param name="Reason">One of <c>declined</c> or <c>disabled by ah allow off</c>; null when effective.</param>
public sealed record MemberLaunchVerdict(bool Effective, string? Reason)
{
    /// <summary>The consent version of the newest consent row, or null when there is none.</summary>
    public int? Version { get; init; }

    /// <summary>The timestamp of the newest consent row, or null when there is none.</summary>
    public string? At { get; init; }
}
