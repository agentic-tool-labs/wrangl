using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ah.Hierarchy;

/// <summary>One completed-work event for a team's memory ledger. <c>Text</c> is raw; the writer cleans it.</summary>
public sealed record LedgerEvent(
    string Kind,
    string? Stream = null,
    string? Sha = null,
    string? Base = null,
    string? Ref = null,
    string? Verdict = null,
    string? Slug = null,
    string Text = "",
    string SourceType = "verb",
    string? SourcePath = null,
    string? SourceVerb = null,
    string? ToolUseId = null,
    string? Into = null,
    bool? Tag = null,
    bool? Confirmed = null,
    string? Phase = null,
    string? Result = null);

/// <summary>What appending a ledger line did: written, left alone because the team's progress is off, or failed (the message).</summary>
public sealed record LedgerAppend(bool Appended, bool Off, string? Error);

/// <summary>Makes ledger text safe to store and to quote: ANSI escapes and control characters out, one line, secrets redacted, at most <see cref="MaxChars"/>.</summary>
internal static partial class LedgerText
{
    public const int MaxChars = 200;

    public const string Redacted = "[redacted: secret pattern]";

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "ship", "merge", "verdict", "ruling", "decision", "train", "member", "blocker", "blocker-cleared", "qa", "gate",
    };

    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b.", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----|(?<![A-Za-z0-9_])AKIA[0-9A-Z]{16}(?![A-Za-z0-9_])|(?<![A-Za-z0-9_])(?:ghp|gho)_[A-Za-z0-9]{8,}|(?<![A-Za-z0-9_])github_pat_[A-Za-z0-9_]{8,}|(?<![A-Za-z0-9_])xox[abp]-[A-Za-z0-9-]{6,}|(?<![A-Za-z0-9_])sk-[A-Za-z0-9_-]{21,}|(?<![A-Za-z0-9_])(?i:password|token|secret)[\p{Zs}\t\n\v\f\r\u0085  ]*[=:][\p{Zs}\t\n\v\f\r\u0085  ]*[^ \t\r\n]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    /// <summary>Control and format characters (zero-width, bidi) and ANSI escapes out; newlines, tabs and the Unicode line and paragraph separators as a space; trimmed. No length cut.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var plain = AnsiPattern().Replace(value, string.Empty);
        var builder = new StringBuilder(plain.Length);
        foreach (var ch in plain)
        {
            var category = char.GetUnicodeCategory(ch);
            builder.Append(
                ch is '\n' or '\r' or '\t' || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' '
                : char.IsControl(ch) || category == UnicodeCategory.Format ? '\0'
                : ch);
        }

        return builder.ToString().Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>The ledger <c>text</c> for <paramref name="value"/>: the secret check runs on the raw text, so a hit replaces all of it.</summary>
    public static string Text(string? value)
    {
        var cleaned = Clean(value);
        if (SecretPattern().IsMatch(value ?? string.Empty) || SecretPattern().IsMatch(cleaned))
        {
            return Redacted;
        }

        return cleaned.Length <= MaxChars ? cleaned : cleaned[..MaxChars];
    }
}
