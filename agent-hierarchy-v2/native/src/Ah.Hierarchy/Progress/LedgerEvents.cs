using System.Text.RegularExpressions;

namespace Ah.Hierarchy;

/// <summary>Turns a delivered report into the ledger events it carries: a verdict, its decisions, and a blocker opened or cleared.</summary>
internal static partial class LedgerEvents
{
    private const string RefiledSuffix = " (refiled)";

    [GeneratedRegex(@"^VERDICT[ \t]+(.+?)[ \t]+([a-z0-9][a-z0-9-]*)@([0-9a-f]{7,40})(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex VerdictPattern();

    [GeneratedRegex(@"^(PASS WITH NITS|PASS|APPROVED|CHANGES REQUIRED|FAIL)[ \t]*[.:!]?\z", RegexOptions.CultureInvariant)]
    private static partial Regex BareVerdictPattern();

    [GeneratedRegex(@"^(PASS|FAIL)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QaVerdictPattern();

    [GeneratedRegex(@"(?<![0-9a-f])[0-9a-f]{7,40}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();

    /// <summary>Whether a report's status opens a blocker: <c>BLOCKED</c>, or <c>NEEDS-</c> and a role.</summary>
    public static bool IsBlocked(string? status) =>
        status is not null && (status.StartsWith("BLOCKED", StringComparison.Ordinal) || status.StartsWith("NEEDS-", StringComparison.Ordinal));

    /// <summary>The request id a response file name starts with.</summary>
    public static string RequestIdOf(string path)
    {
        var name = Path.GetFileName(path);
        var cut = name.IndexOf("--", StringComparison.Ordinal);
        return cut > 0 ? name[..cut] : name;
    }

    /// <summary>
    /// The events of a delivered report, without the blocker clearing (that needs the ledger). <paramref name="qa"/> marks a report from
    /// the QA role. A refiled report's events carry a <c>(refiled)</c> suffix.
    /// </summary>
    public static List<LedgerEvent> FromReport(ReportExtract extract, string stream, string slug, bool qa, string responsePath, string responseText, bool refiled)
    {
        var events = new List<LedgerEvent>();
        var suffix = refiled ? RefiledSuffix : string.Empty;
        LedgerEvent Make(string kind, string text, string? verdict = null, string? sha = null) =>
            new(kind, Stream: stream, Sha: sha, Verdict: verdict, Slug: slug, Text: text + suffix, SourceType: "report", SourcePath: responsePath);

        var status = extract.Status;
        if (status is not null)
        {
            if (VerdictPattern().Match(status) is { Success: true } v)
            {
                events.Add(Make("verdict", status, v.Groups[1].Value, ExpandSha(v.Groups[3].Value, responseText)));
            }
            else if (qa && QaVerdictPattern().Match(status) is { Success: true } q)
            {
                var sha = ShaPattern().Match(status) is { Success: true } s ? ExpandSha(s.Value, responseText) : null;
                events.Add(Make("verdict", status, q.Groups[1].Value.ToUpperInvariant(), sha));
            }
            else if (IsBlocked(status))
            {
                events.Add(Make("blocker", status));
            }
        }

        foreach (var (tag, text) in extract.Tagged)
        {
            if (tag == "Decision")
            {
                events.Add(Make("decision", text));
            }
        }

        return events;
    }

    /// <summary>The verdict word(s) and slug of a <c>VERDICT &lt;x&gt; &lt;slug&gt;@&lt;sha&gt;</c> status line, or null when the line is not one.</summary>
    public static (string Verdict, string Slug)? Verdict(string? status) =>
        status is not null && VerdictPattern().Match(status) is { Success: true } v ? (v.Groups[1].Value, v.Groups[2].Value) : null;

    /// <summary>The commit a <c>VERDICT</c> line names, or null when the line is not one.</summary>
    internal static string? VerdictSha(string? status) =>
        status is not null && VerdictPattern().Match(status) is { Success: true } v ? v.Groups[3].Value : null;

    /// <summary>A review report's verdict: a <see cref="Verdict"/> line, or a line that is only a verdict word, which names no slug. A sentence that opens with one is not a verdict.</summary>
    public static (string Verdict, string Slug)? ReviewVerdict(string? line) =>
        Verdict(line) ?? (line is not null && BareVerdictPattern().Match(line) is { Success: true } bare ? (bare.Groups[1].Value, string.Empty) : null);

    /// <summary>The 40-hex sha in <paramref name="text"/> that begins with <paramref name="prefix"/>, else the prefix as given.</summary>
    private static string ExpandSha(string prefix, string text)
    {
        if (prefix.Length == 40)
        {
            return prefix;
        }

        foreach (Match m in ShaPattern().Matches(text))
        {
            if (m.Value.Length == 40 && m.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return m.Value;
            }
        }

        return prefix;
    }
}
