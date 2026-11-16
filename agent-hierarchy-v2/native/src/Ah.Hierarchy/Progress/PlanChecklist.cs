using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// A stream checklist (<c>&lt;stream&gt;.md</c>) read as a plan source. A file is a checklist only when its first non-blank line is a
/// <c>kind=stream</c> marker of format major 1. The item lines come from <see cref="ProgressFormat.Scan"/>; the state of an item is the
/// section it sits under, and a checkbox that disagrees with its section, or a plain list line, only adds to <c>warnings</c>.
/// </summary>
internal static partial class PlanChecklist
{
    /// <summary>The most bytes read from one checklist.</summary>
    public const int MaxBytes = 256 * 1024;

    public const int MaxItems = 500;

    public const int MaxTitle = 200;

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex FieldGap();

    [GeneratedRegex(@"[\f\n\r\t\v\u0085\p{Z}]+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The parsed checklist, or null when <paramref name="text"/> is not a stream checklist. <paramref name="cut"/> says the text is the
    /// first <see cref="MaxBytes"/> of a longer file; <paramref name="mtimeMs"/> stands in for <c>Updated:</c> when it cannot be read.
    /// </summary>
    public static JsonObject? Parse(string stream, string text, bool cut, long mtimeMs)
    {
        var body = text.TrimStart('\r', '\n', ' ', '\t', '﻿');
        if (ProgressFormat.ReadMarker(body) is not { Present: true, Kind: "stream", Major: 1 })
        {
            return null;
        }

        var scan = ProgressFormat.Scan(body, acceptPlain: false);
        var fields = Fields(body);
        var warnings = scan.NotCounted;
        var items = new JsonArray();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = cut;
        foreach (var item in scan.Items)
        {
            var done = item.Section == ProgressFormat.SectionDone;
            if (item.Checked != done)
            {
                warnings++;
            }

            if (items.Count >= MaxItems)
            {
                truncated = true;
                continue;
            }

            var title = Cap(item.Text.Trim());
            items.Add(new JsonObject
            {
                ["key"] = KeyOf(stream, title, seen),
                ["title"] = title,
                ["state"] = item.Section switch { ProgressFormat.SectionDone => "done", ProgressFormat.SectionProgress => "in_progress", _ => "not_started" },
            });
        }

        return new JsonObject
        {
            ["goal"] = Text(ProgressFormat.ChecklistTitle(body, stream)),
            ["owner"] = Text(fields.GetValueOrDefault("Owner")),
            ["branch"] = Text(fields.GetValueOrDefault("Branch")),
            ["work_tree"] = Text(fields.GetValueOrDefault("Work tree")),
            ["md_state"] = Text(scan.State),
            ["as_of"] = scan.Updated is { } updated ? updated.ToUnixTimeMilliseconds() : mtimeMs,
            ["blockers"] = Blockers(body),
            ["warnings"] = warnings,
            ["truncated"] = truncated,
            ["items"] = items,
        };
    }

    private static JsonValue Text(string? value) => value is null ? JsonValue.Null : Cap(value);

    /// <summary>The first <see cref="MaxTitle"/> characters of <paramref name="value"/>, one fewer when that would split a surrogate pair.</summary>
    internal static string Cap(string value)
    {
        if (value.Length <= MaxTitle)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[MaxTitle - 1]) ? MaxTitle - 1 : MaxTitle)];
    }

    /// <summary>The stream and a hash of the normalised title; a title that repeats in the stream gets <c>~2</c>, <c>~3</c> and so on.</summary>
    private static string KeyOf(string stream, string title, Dictionary<string, int> seen)
    {
        var normal = Whitespace().Replace(title, " ").Trim().ToLowerInvariant();
        var key = stream + ":" + ProgressFormat.Sha256(normal)[..12];
        var count = seen.GetValueOrDefault(key) + 1;
        seen[key] = count;
        return count == 1 ? key : key + "~" + count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>Owner: … Branch: … Work tree: … State: …</c> line: fields split on two or more spaces, <c>—</c> and an empty value meaning none.</summary>
    private static Dictionary<string, string> Fields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith("Owner:", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var part in FieldGap().Split(line.Trim()))
            {
                var colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && part[(colon + 1)..].Trim() is { Length: > 0 } value && value != "—")
                {
                    fields[part[..colon].Trim()] = value;
                }
            }

            break;
        }

        return fields;
    }

    /// <summary>The non-blank lines under <c>## Blockers</c>, up to the next heading.</summary>
    private static int Blockers(string body)
    {
        var inside = false;
        var count = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                inside = line.TrimEnd().Equals("## Blockers", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inside && line.Trim().Length > 0)
            {
                count++;
            }
        }

        return count;
    }
}
