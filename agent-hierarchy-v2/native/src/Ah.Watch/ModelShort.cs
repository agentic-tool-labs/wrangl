using System.Globalization;

namespace Ah.Watch;

/// <summary>The short form of a served model id that a graph pill shows: the family of a Claude id, an alias as it is, any other id without its date stamp and cut to sixteen cells.</summary>
internal static class ModelShort
{
    private const int LongestOther = 16;

    /// <summary>The short name of one id, and the version (<c>4.1</c>, or <c>4</c> when the id names no minor) a Claude id carries, null for any other.</summary>
    internal static (string Short, string? Version) Of(string model)
    {
        var id = model.Trim();
        if (id.StartsWith("claude-", StringComparison.Ordinal))
        {
            var tag = id.IndexOf('[', StringComparison.Ordinal);
            if (id.EndsWith(']') && tag > 0)
            {
                id = id[..tag];
            }

            var parts = id["claude-".Length..].Split('-');
            if (FamilyFirst(parts) is { } a)
            {
                return a;
            }

            if (VersionFirst(parts) is { } b)
            {
                return b;
            }
        }
        else
        {
            var bracket = id.IndexOf('[', StringComparison.Ordinal);
            var bare = id.EndsWith(']') && bracket > 0 ? id[..bracket] : id;
            if (bare.Length > 0 && !bare.Any(char.IsDigit))
            {
                return (id, null);
            }
        }

        var undated = DropDate(id);
        return (Length(undated) <= LongestOther ? undated : string.Concat(Elements(undated).Take(LongestOther - 1)) + "…", null);
    }

    /// <summary>The short names of the models one document serves, in order; two different ids that share a family each get their version appended so the pills tell them apart.</summary>
    internal static IReadOnlyList<string> Document(IReadOnlyList<string> models)
    {
        var parsed = models.Select(Of).ToList();
        var result = parsed.Select(static p => p.Short).ToList();
        foreach (var group in Enumerable.Range(0, models.Count).GroupBy(i => parsed[i].Short, StringComparer.Ordinal))
        {
            if (group.Select(i => models[i]).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                continue;
            }

            foreach (var i in group.Where(i => parsed[i].Version is not null))
            {
                result[i] = parsed[i].Short + " " + parsed[i].Version;
            }
        }

        return result;
    }

    private static (string Short, string? Version)? FamilyFirst(string[] parts)
    {
        if (parts.Length < 2 || !Letters(parts[0]) || !Digits(parts[1]))
        {
            return null;
        }

        var next = 2;
        string? minor = null;
        if (next < parts.Length && Digits(parts[next]) && parts[next].Length <= 2)
        {
            minor = parts[next++];
        }

        if (next < parts.Length && IsDate(parts[next]))
        {
            next++;
        }

        return next == parts.Length ? (parts[0], Version(parts[1], minor)) : null;
    }

    private static (string Short, string? Version)? VersionFirst(string[] parts)
    {
        if (parts.Length < 2 || !Digits(parts[0]))
        {
            return null;
        }

        var next = 1;
        string? minor = null;
        if (next < parts.Length && Digits(parts[next]) && parts[next].Length <= 2)
        {
            minor = parts[next++];
        }

        if (next >= parts.Length || !Letters(parts[next]))
        {
            return null;
        }

        var family = parts[next++];
        if (next < parts.Length && IsDate(parts[next]))
        {
            next++;
        }

        return next == parts.Length ? (family, Version(parts[0], minor)) : null;
    }

    private static string Version(string major, string? minor) => minor is null ? major : major + "." + minor;

    private static bool Letters(string s) => s.Length > 0 && s.All(static c => c is >= 'a' and <= 'z');

    private static bool Digits(string s) => s.Length > 0 && s.All(static c => c is >= '0' and <= '9');

    private static bool IsDate(string s) => s.Length == 8 && Digits(s);

    private static string DropDate(string id)
    {
        var cut = id.LastIndexOf('-');
        if (cut > 0 && IsDate(id[(cut + 1)..]))
        {
            return id[..cut];
        }

        var parts = id.Split('-');
        if (parts.Length >= 4 && parts[^3].Length == 4 && Digits(parts[^3]) && parts[^2].Length == 2 && Digits(parts[^2]) && parts[^1].Length == 2 && Digits(parts[^1]))
        {
            return string.Join('-', parts[..^3]);
        }

        return id;
    }

    private static IEnumerable<string> Elements(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            yield return (string)e.Current;
        }
    }

    private static int Length(string s) => new StringInfo(s).LengthInTextElements;
}
