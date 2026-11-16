using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ah.Hierarchy;

/// <summary>The version marker of a progress record: the format version it was created with and what kind of record it is.</summary>
internal sealed record ProgressMarker(int Major, int Minor, string? Kind, bool Present, string? Plan = null);

/// <summary>What a stream checklist says: the counts of its three lists, its <c>State:</c> and <c>Updated:</c>, and one warning per unparsable known item.</summary>
internal sealed record StreamChecklist(
    int Done,
    int InProgress,
    int NotStarted,
    string? State,
    DateTimeOffset? Updated,
    IReadOnlyList<string> Warnings);

/// <summary>One item line under a checklist heading: the heading's section, whether it is ticked, and its text after the marker.</summary>
internal sealed record ChecklistItem(string Section, bool Checked, string Text);

/// <summary>The parts of a report a journal entry carries: its status line and its tagged lines, in order.</summary>
internal sealed record ReportExtract(string? Status, IReadOnlyList<KeyValuePair<string, string>> Tagged);

/// <summary>
/// The shapes of the progress records (the checklist, the journal, the rollup), their version markers, and the readers that
/// tolerate what they do not know. Pure text in, text out: nothing here touches a file.
/// </summary>
internal static partial class ProgressFormat
{
    /// <summary>The format version this build writes.</summary>
    public const string Version = "1.0";

    /// <summary>The format version of a stream checklist rendered from its plan log, and of the plan log's rows.</summary>
    public const string PlanVersion = "1.1";

    /// <summary>The highest major version this build reads and writes.</summary>
    public const int KnownMajor = 1;

    /// <summary>The tags a report line may carry to be copied into the journal.</summary>
    public static readonly IReadOnlyList<string> Tags = ["Decision", "Deviation", "Roadblock", "Outcome", "Detail"];

    /// <summary>The minutes the newest journal entry may lead the checklist's <c>Updated:</c> before the stream is stale.</summary>
    public const int StaleMinutes = 30;

    public static string MarkdownMarker(string kind) => $"<!-- ah-progress v{Version} kind={kind} -->";

    public static string NudgedMarker() => $"# ah-progress v{Version} kind=nudged";

    public static string OffMarker() => $"# ah-progress v{Version} kind=off";

    [GeneratedRegex(@"^(?:<!--[ \t]*|#[ \t]*)ah-progress[ \t]+v([0-9]+)\.([0-9]+)(?:[ \t]+kind=([A-Za-z0-9_-]+))?(?:[ \t]+plan=([A-Za-z0-9_-]+))?[ \t]*(?:-->)?[ \t]*\z")]
    private static partial Regex MarkerPattern();

    /// <summary>The marker on the first line of <paramref name="text"/>; a file with no marker reads as v1.0.</summary>
    public static ProgressMarker ReadMarker(string text)
    {
        var first = text.Length == 0 ? string.Empty : text.Split('\n', 2)[0].TrimEnd('\r');
        var m = MarkerPattern().Match(first);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return new ProgressMarker(1, 0, null, false);
        }

        return new ProgressMarker(major, minor, m.Groups[3].Success ? m.Groups[3].Value : null, true, m.Groups[4].Success ? m.Groups[4].Value : null);
    }

    /// <summary>The version marker of a stream checklist that is rendered from its plan log.</summary>
    public static string PlanMarker() => $"<!-- ah-progress v{PlanVersion} kind=stream plan=jsonl -->";

    /// <summary>Whether the first line of <paramref name="text"/> marks a checklist rendered from a plan log.</summary>
    public static bool IsPlanned(string text) => ReadMarker(text) is { Plan: "jsonl", Kind: "stream" };

    /// <summary>Whether this build may append to or rewrite a file that starts with <paramref name="text"/>: not when a newer major wrote it.</summary>
    public static bool Writable(string text) => ReadMarker(text).Major <= KnownMajor;

    // ---------------------------------------------------------------- templates

    public static string StreamTemplate(string stream, string? goal, string owner, string branch, string workTree, string updated) =>
        string.Join(
            "\n",
            MarkdownMarker("stream"),
            $"# {stream} — {(string.IsNullOrWhiteSpace(goal) ? "goal not set" : goal.Trim())}",
            $"Owner: {owner}   Branch: {branch}   Work tree: {workTree}   State: open",
            $"Updated: {updated}",
            string.Empty,
            "## Checklist",
            "### Done",
            string.Empty,
            "### In progress",
            string.Empty,
            "### Not started",
            string.Empty,
            "## Related",
            "- Specs:   Issues:   Depends on:   Blocks:",
            string.Empty,
            "## Blockers",
            string.Empty);

    public static string RollupTemplate(string team, string updated) =>
        string.Join(
            "\n",
            MarkdownMarker("rollup"),
            $"# {team} — progress",
            $"Updated: {updated}",
            string.Empty,
            "## Goal",
            string.Empty,
            "## Streams",
            "| Stream | Owner | Branch | State |",
            "|---|---|---|---|",
            "| [main](main.md) |  |  | open |",
            string.Empty);

    public static string JournalHeader() => MarkdownMarker("journal") + "\n";

    // ---------------------------------------------------------------- the checklist

    [GeneratedRegex(@"^ {0,3}#{1,6}[ \t]+(.*?)[ \t]*#*[ \t]*\z")]
    private static partial Regex HeadingPattern();

    internal const string SectionDone = "done";
    internal const string SectionProgress = "progress";
    internal const string SectionNotStarted = "not-started";

    [GeneratedRegex(@"^[-*][ \t]+")]
    private static partial Regex ListLinePattern();

    [GeneratedRegex(@"(?:^|[ \t])State:[ \t]*([^ \t]+)")]
    private static partial Regex StatePattern();

    [GeneratedRegex(@"^Updated:[ \t]*(.*?)[ \t]*\z")]
    private static partial Regex UpdatedPattern();

    private static readonly string[] States = ["open", "done", "parked"];

    /// <summary>
    /// Reads the three checklist lists, <c>State:</c> and <c>Updated:</c> and nothing else. An unknown heading or line is skipped; a
    /// known item whose content cannot be read counts as absent and adds one warning naming its line.
    /// </summary>
    public static StreamChecklist ReadChecklist(string text)
    {
        var scan = Scan(text, acceptPlain: false);
        return new StreamChecklist(
            scan.Items.Count(static i => i.Section == SectionDone),
            scan.Items.Count(static i => i.Section == SectionProgress),
            scan.Items.Count(static i => i.Section == SectionNotStarted),
            scan.State,
            scan.Updated,
            scan.Warnings);
    }

    /// <summary>
    /// The items under the three checklist headings, in file order, with the same fence and heading rules as <see cref="ReadChecklist"/>.
    /// With <paramref name="acceptPlain"/>, a plain <c>- text</c> line at the start of a line counts too (its state comes from the
    /// heading); without it only <c>- [ ]</c> and <c>- [x]</c> lines do.
    /// </summary>
    internal static IReadOnlyList<ChecklistItem> ReadItems(string text, bool acceptPlain) => Scan(text, acceptPlain).Items;

    internal sealed record Scanned(List<ChecklistItem> Items, string? State, DateTimeOffset? Updated, List<string> Warnings, int NotCounted = 0);

    [GeneratedRegex(@"^[ \t]*[-*][ \t]+\[([ xX])\][ \t]*(.*?)[ \t]*\z")]
    private static partial Regex CheckboxItemPattern();

    [GeneratedRegex(@"^[-*][ \t]+(.*?)[ \t]*\z")]
    private static partial Regex PlainItemPattern();

    internal static Scanned Scan(string text, bool acceptPlain)
    {
        var warnings = new List<string>();
        var items = new List<ChecklistItem>();
        var notCounted = 0;
        string? state = null;
        DateTimeOffset? updated = null;
        string? section = null;
        var fenced = false;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                section = heading.Groups[1].Value.ToLowerInvariant() switch
                {
                    "done" => SectionDone,
                    "in progress" => SectionProgress,
                    "not started" => SectionNotStarted,
                    _ => null,
                };
                continue;
            }

            if (section is null)
            {
                if (state is null && !line.StartsWith('#') && StatePattern().Match(line) is { Success: true } s)
                {
                    var value = s.Groups[1].Value;
                    if (States.Contains(value, StringComparer.Ordinal))
                    {
                        state = value;
                    }
                    else
                    {
                        warnings.Add($"line {i + 1}: State: \"{value}\" is not open, done or parked");
                    }
                }
                else if (updated is null && UpdatedPattern().Match(line) is { Success: true } u)
                {
                    if (DateTimeOffset.TryParse(u.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                    {
                        updated = at;
                    }
                    else
                    {
                        warnings.Add($"line {i + 1}: Updated: \"{u.Groups[1].Value}\" is not a time");
                    }
                }

                continue;
            }

            if (CheckboxItemPattern().Match(line) is { Success: true } box)
            {
                items.Add(new ChecklistItem(section, box.Groups[1].Value != " ", box.Groups[2].Value));
            }
            else if (acceptPlain && PlainItemPattern().Match(line) is { Success: true } plain)
            {
                items.Add(new ChecklistItem(section, section == SectionDone, plain.Groups[1].Value));
            }
            else if (!acceptPlain && ListLinePattern().IsMatch(line))
            {
                notCounted++;
                var name = section switch { "done" => "Done", "progress" => "In progress", _ => "Not started" };
                warnings.Add($"line {i + 1}: \"{(line.Length > 40 ? line[..40] : line)}\" under {name} is not a checkbox line (- [ ] or - [x]), so it is not counted");
            }
        }

        return new Scanned(items, state, updated, warnings, notCounted);
    }

    /// <summary>
    /// <paramref name="text"/> with its <c>State:</c> word replaced by <paramref name="newState"/>, every other byte kept; null when
    /// the text has no <c>State:</c> to replace.
    /// </summary>
    public static string? WithState(string text, string newState)
    {
        var lines = text.Split('\n');
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (lines[i].StartsWith("##", StringComparison.Ordinal))
            {
                return null;
            }

            if (fenced || lines[i].StartsWith('#') || StatePattern().Match(lines[i]) is not { Success: true } m)
            {
                continue;
            }

            var word = m.Groups[1];
            lines[i] = string.Concat(lines[i].AsSpan(0, word.Index), newState, lines[i].AsSpan(word.Index + word.Length));
            return string.Join('\n', lines);
        }

        return null;
    }

    /// <summary>
    /// The goal in a checklist's first heading (<c># stream — goal</c>); null when there is no heading, the goal is the template's
    /// <c>goal not set</c>, or the heading only repeats the stream's name.
    /// </summary>
    internal static string? ChecklistTitle(string text, string stream)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("# ", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = line[2..].Trim();
            var cut = heading.IndexOf(" — ", StringComparison.Ordinal);
            var title = cut >= 0 ? heading[(cut + 3)..].Trim() : heading;
            return title.Length == 0 || title == "goal not set" || title == stream ? null : title;
        }

        return null;
    }

    /// <summary>Whether the newest journal entry leads the checklist's <c>Updated:</c> by more than <see cref="StaleMinutes"/>.</summary>
    public static bool IsStale(DateTimeOffset? checklistUpdated, DateTimeOffset? newestEntry) =>
        checklistUpdated is { } u && newestEntry is { } e && e - u > TimeSpan.FromMinutes(StaleMinutes);

    // ---------------------------------------------------------------- the journal

    [GeneratedRegex(@"^## ([^ \t\r\n]+) · ", RegexOptions.Multiline)]
    private static partial Regex EntryHeadPattern();

    /// <summary>The times of the entries in a journal, in file order; an entry whose time cannot be read is skipped.</summary>
    public static IReadOnlyList<DateTimeOffset> EntryTimes(string journal)
    {
        var times = new List<DateTimeOffset>();
        foreach (Match m in EntryHeadPattern().Matches(journal))
        {
            if (DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                times.Add(at);
            }
        }

        return times;
    }

    /// <summary>The request id and time of each entry in a journal, in file order; an entry whose time or id cannot be read is skipped.</summary>
    public static IReadOnlyList<KeyValuePair<DateTimeOffset, string>> EntryHeads(string journal)
    {
        var heads = new List<KeyValuePair<DateTimeOffset, string>>();
        foreach (var raw in journal.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line[3..].Split(" · ");
            if (parts.Length < 3
                || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                continue;
            }

            var tail = parts[^1];
            if (tail.EndsWith(" (refiled)", StringComparison.Ordinal))
            {
                tail = tail[..^" (refiled)".Length];
            }

            var id = tail[(tail.LastIndexOf(' ') + 1)..];
            if (id.Length > 0)
            {
                heads.Add(new KeyValuePair<DateTimeOffset, string>(at, id));
            }
        }

        return heads;
    }

    /// <summary>The marker an entry ends with, so the same report is never appended twice.</summary>
    public static string EntryKeyLine(string requestId, string sha) => $"<!-- ah-entry id={requestId} sha={sha} -->";

    /// <summary>The content hashes of the entries already journaled for <paramref name="requestId"/>, in file order.</summary>
    public static IReadOnlyList<string> EntryHashes(string journal, string requestId)
    {
        var hashes = new List<string>();
        var prefix = $"<!-- ah-entry id={requestId} sha=";
        foreach (var line in journal.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith(prefix, StringComparison.Ordinal) && t.EndsWith(" -->", StringComparison.Ordinal))
            {
                hashes.Add(t[prefix.Length..^4]);
            }
        }

        return hashes;
    }

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Control characters shown as their visible pictures (U+2400 and up), and the C1 controls, the line and paragraph separators
    /// and the bidirectional controls as their code point in text (<c>&lt;U+0085&gt;</c>), so a report cannot move the journal's
    /// lines or reorder its text.
    /// </summary>
    public static string Pictured(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch < 0x20)
            {
                sb.Append((char)(0x2400 + ch));
            }
            else if (ch == 0x7f)
            {
                sb.Append('␡');
            }
            else if (IsHidden(ch))
            {
                sb.Append("<U+").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static bool IsHidden(char ch) =>
        ch is (>= (char)0x80 and <= (char)0x9f) or (char)0x61c or (char)0x200e or (char)0x200f or (char)0x2028 or (char)0x2029
            or (>= (char)0x202a and <= (char)0x202e) or (>= (char)0x2066 and <= (char)0x2069);

    [GeneratedRegex(@"^[ \t]*(?:[-*][ \t]+)?(Decision|Deviation|Roadblock|Outcome|Detail):[ \t]*(.*?)[ \t]*\z")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[ \t]*[-*][ \t]+\[1\][ \t]+status:[ \t]*(.*?)[ \t]*\z", RegexOptions.IgnoreCase)]
    private static partial Regex StatusLinePattern();

    /// <summary>
    /// The status line of a report's <c>## [0] tldr</c> and the tagged lines under its <c>## [1] status</c>. A tag inside a code
    /// block, under any other section, or one this version does not know is not extracted.
    /// </summary>
    public static ReportExtract ExtractReport(string response)
    {
        string? status = null;
        var tagged = new List<KeyValuePair<string, string>>();
        string? section = null;
        var fenced = false;
        foreach (var raw in response.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                var name = raw[3..].Trim().ToLowerInvariant();
                section = name.StartsWith("[0]", StringComparison.Ordinal) ? "tldr"
                    : name.StartsWith("[1]", StringComparison.Ordinal) ? "status"
                    : "other";
                continue;
            }

            if (section == "tldr" && status is null && StatusLinePattern().Match(raw) is { Success: true } sl)
            {
                status = Pictured(sl.Groups[1].Value);
            }
            else if (section == "status" && TagPattern().Match(raw) is { Success: true } tag)
            {
                tagged.Add(new KeyValuePair<string, string>(tag.Groups[1].Value, Pictured(tag.Groups[2].Value)));
            }
        }

        return new ReportExtract(status, tagged);
    }

    /// <summary>One journal entry: its heading, the status line, each tagged line, the report link and the entry's key line.</summary>
    public static string FormatEntry(
        string time, string from, string kind, string requestId, bool refiled, ReportExtract report, string reportPath, string sha)
    {
        var sb = new StringBuilder();
        sb.Append("\n## ").Append(time).Append(" · ").Append(Pictured(from)).Append(" · ").Append(Pictured(kind)).Append(' ').Append(requestId);
        if (refiled)
        {
            sb.Append(" (refiled)");
        }

        sb.Append('\n');
        sb.Append("- Status: ").Append(report.Status ?? "none").Append('\n');
        foreach (var (tag, text) in report.Tagged)
        {
            sb.Append("- ").Append(tag).Append(": ").Append(text).Append('\n');
        }

        sb.Append("- Report: ").Append(Pictured(reportPath)).Append('\n');
        sb.Append(EntryKeyLine(requestId, sha)).Append('\n');
        return sb.ToString();
    }
}
