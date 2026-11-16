namespace Ah.Tui.Core;

/// <summary>One row of a region: its title, made safe to draw, and what it opens. A ship, merge or decision row is no work item, so it has no <paramref name="Card"/> and is no stop.</summary>
internal sealed record SitrepRow(string Key, string Title, int Section, string? Card);

/// <summary>One region as the slice serves it, in the order it stacks: <paramref name="List"/> and <paramref name="Total"/> name the body's rows and count.</summary>
internal sealed record SitrepRegion(string List, string? Total, string Label, bool Optional, bool Folded);

internal sealed record SitrepModel(string Name, int Needs, int Working, int Blocked, string? Updated, int Left, IReadOnlyList<SitrepRegion> Regions, IReadOnlyList<string> Counts, IReadOnlyList<SitrepRow> Rows, bool PlanPending = false)
{
    internal int? NeedsShown => TeamSlices.NeedsShown(Needs, PlanPending);
}

/// <summary>
/// The Sitrep's regions, stacked: Completed, In progress and Up next always, then the rest of what the slice serves only when it has any. A heading folds its region;
/// what the viewer folded or opened is kept in memory only, so it resets on restart.
/// </summary>
internal static class SitrepView
{
    internal const string PerTopLevelTeam = "Sitrep is per top-level team";

    /// <summary>The stop id of a region's heading, before its list's name.</summary>
    internal const string RegionStop = "region:";

    /// <summary>The stop id of an item's row, before its card.</summary>
    internal const string ItemStop = "item:";

    /// <summary>One line of the stack: a region's heading or a row; <paramref name="Stop"/> is its stop id, null when it is no stop.</summary>
    internal readonly record struct StackLine(string Text, bool Heading, string? Stop);

    /// <summary>The regions on show, each heading with its count and, unless folded, one line a row. <paramref name="toggled"/> names the lists the viewer flipped from where they start.</summary>
    internal static List<StackLine> StackedLines(SitrepModel m, IReadOnlySet<string> toggled)
    {
        var lines = new List<StackLine>();
        for (var i = 0; i < m.Regions.Count; i++)
        {
            var section = m.Regions[i];
            if (section.Optional && m.Counts[i] == "0")
            {
                continue;
            }

            var folded = section.Folded != toggled.Contains(section.List);
            lines.Add(new($"{(folded ? "[+]" : "[-]")} {section.Label} {m.Counts[i]}", true, RegionStop + section.List));
            if (!folded)
            {
                lines.AddRange(m.Rows.Where(r => r.Section == i).Select(static r => new StackLine(r.Title, false, r.Card is null ? null : ItemStop + r.Card)));
            }
        }

        return lines;
    }
}
