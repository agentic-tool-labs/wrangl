using System.Text.RegularExpressions;

namespace Ah.Mesh;

/// <summary>
/// The one place an <c>[ah-entry …]</c> marker is written and read. A marker names the entries a message carries; the
/// <c>to</c> form also names the mailbox they are in.
/// </summary>
internal static partial class EntryMarkerText
{
    private const string Id = "[A-Za-z0-9._-]+";
    private const string IdList = "[A-Za-z0-9._,-]+";
    private const string Address = "[A-Za-z0-9._-]+@[A-Za-z0-9._-]+";

    [GeneratedRegex(@"\[ah-entry (" + Id + ") to (" + Address + @")\]", RegexOptions.CultureInvariant)]
    private static partial Regex ToForm();

    [GeneratedRegex(@"\[ah-entry (" + IdList + @")\]", RegexOptions.CultureInvariant)]
    private static partial Regex ListForm();

    [GeneratedRegex(@"\A\[ah-entry " + IdList + "( to " + Address + @")?\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex WholeLine();

    [GeneratedRegex(@"\[ah-entry (" + Id + ") to (" + Address + @")\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingToForm();

    [GeneratedRegex(@"\(ah-entry (" + Id + ") to (" + Address + @")\]", RegexOptions.CultureInvariant)]
    private static partial Regex NeutralisedToForm();

    [GeneratedRegex(@"\A" + Address + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex WholeAddress();

    /// <summary>Whether <paramref name="text"/> is exactly a mailbox address as a marker writes it.</summary>
    internal static bool IsAddress(string text) => WholeAddress().IsMatch(text);

    /// <summary><c>[ah-entry {id} to {address}]</c>.</summary>
    internal static string Format(string id, string address) => $"[ah-entry {id} to {address}]";

    /// <summary><c>[ah-entry {id1},{id2}]</c>.</summary>
    internal static string FormatList(IEnumerable<string> ids) => $"[ah-entry {string.Join(',', ids)}]";

    /// <summary>The markers in <paramref name="text"/>: the <c>to</c> form first, then the list form, each in text order.</summary>
    internal static List<EntryMarker> Parse(string? text)
    {
        var source = text ?? string.Empty;
        var markers = new List<EntryMarker>();
        foreach (Match m in ToForm().Matches(source))
        {
            markers.Add(new EntryMarker([m.Groups[1].Value], m.Groups[2].Value));
        }

        foreach (Match m in ListForm().Matches(source))
        {
            markers.Add(new EntryMarker([.. m.Groups[1].Value.Split(',').Where(s => s.Length > 0)], null));
        }

        return markers;
    }

    /// <summary>Whether <paramref name="line"/> is nothing but one marker of either form.</summary>
    internal static bool IsMarkerLine(string line) => WholeLine().IsMatch(line);

    /// <summary>
    /// The <c>to</c>-form markers in <paramref name="text"/> whose opening bracket the prompt's neutraliser turned into a parenthesis.
    /// Only the check of a relay obligation the sender already holds may use this; a marker that was not neutralised never matches.
    /// </summary>
    internal static List<EntryMarker> ParseNeutralised(string? text)
    {
        var markers = new List<EntryMarker>();
        foreach (Match m in NeutralisedToForm().Matches(text ?? string.Empty))
        {
            markers.Add(new EntryMarker([m.Groups[1].Value], m.Groups[2].Value));
        }

        return markers;
    }

    /// <summary>The <c>to</c>-form marker that ends <paramref name="text"/>, or null.</summary>
    internal static EntryMarker? ParseTrailing(string text)
    {
        var m = TrailingToForm().Match(text);
        return m.Success ? new EntryMarker([m.Groups[1].Value], m.Groups[2].Value) : null;
    }
}
