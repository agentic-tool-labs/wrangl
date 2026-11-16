using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>
/// The <c>autoMode</c> block of a member's launch settings: the policy file's prose, with its four path tokens filled in, merged after the
/// caller's own entries. The literal <c>$defaults</c> splices the built-in entries and stays first in each section.
/// </summary>
internal static class MemberAutoMode
{
    internal const string Defaults = "$defaults";

    /// <summary>The values the policy prose may name: <c>{work_root}</c>, <c>{tmp}</c>, <c>{ah}</c> and <c>{plugin_root}</c>.</summary>
    internal sealed record Tokens(string WorkRoot, string? Tmp, string? Ah, string PluginRoot);

    /// <summary>
    /// The block to emit, or null when the policy names none or names a malformed one (not an object, a section that is not an array, an
    /// element that is not text, or a section whose first element is not <c>$defaults</c>); the launch file is then built without it.
    /// </summary>
    internal static JsonObject? Compose(JsonValue? policy, JsonValue? caller, Tokens tokens)
    {
        if (policy is not { IsMap: true } map)
        {
            return null;
        }

        var sections = new List<(string Name, List<string> Entries)>();
        foreach (var name in map.AsObject().Keys)
        {
            if (map.AsObject().Get(name) is not { IsArray: true } array)
            {
                return null;
            }

            var entries = new List<string>();
            foreach (var element in array.AsArray())
            {
                if (element is not { IsText: true } text)
                {
                    return null;
                }

                entries.Add(text.AsString());
            }

            if (entries.Count == 0 || entries[0] != Defaults)
            {
                return null;
            }

            sections.Add((name, entries));
        }

        if (sections.Count == 0)
        {
            return null;
        }

        var result = new JsonObject();
        if (caller is { IsMap: true } callerMap)
        {
            foreach (var name in callerMap.AsObject().Keys)
            {
                result[name] = callerMap.AsObject().Get(name)!.Value;
            }
        }

        foreach (var (name, entries) in sections)
        {
            var merged = new List<string> { Defaults };
            foreach (var entry in CallerEntries(caller, name).Concat(entries.Skip(1).Select(e => Fill(e, tokens))))
            {
                if (entry != Defaults && !merged.Contains(entry, StringComparer.Ordinal))
                {
                    merged.Add(entry);
                }
            }

            var list = new JsonArray();
            foreach (var entry in merged)
            {
                list.Add(JsonValue.FromString(entry));
            }

            result[name] = list;
        }

        return result;
    }

    /// <summary>The text entries of the caller's section, or none when it is not an array of text.</summary>
    private static List<string> CallerEntries(JsonValue? caller, string section)
    {
        if (caller is not { IsMap: true } map || map.AsObject().Get(section) is not { IsArray: true } array || array.AsArray().Any(static e => e is not { IsText: true }))
        {
            return [];
        }

        return array.AsArray().Select(static e => e.AsString()).ToList();
    }

    private static string Fill(string text, Tokens tokens) =>
        text.Replace("{work_root}", tokens.WorkRoot.TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{tmp}", tokens.Tmp is { Length: > 0 } ? tokens.Tmp : "$TMPDIR", StringComparison.Ordinal)
            .Replace("{ah}", tokens.Ah is { Length: > 0 } ? tokens.Ah : "ah", StringComparison.Ordinal)
            .Replace("{plugin_root}", tokens.PluginRoot, StringComparison.Ordinal);
}
