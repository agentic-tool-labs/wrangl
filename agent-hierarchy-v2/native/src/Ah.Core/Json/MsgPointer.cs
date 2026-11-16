namespace Ah.Core.Json;

/// <summary>
/// The agent-facing pointer to a message (ah-msgs-db-only §3.2, §5.1): its id and type word, never its key path. The id and type are
/// read from the key's file name <c>&lt;id&gt;--&lt;to&gt;--&lt;slug&gt;--&lt;type&gt;.md</c>, so a key stored before ids were emitted renders the same way.
/// </summary>
public static class MsgPointer
{
    /// <summary>The <c>ah msg show</c> arguments of <paramref name="key"/>: <c>{id}</c>, <c>{id} response</c> or <c>{id} note</c>; null when the key is not a message file name.</summary>
    public static string? Handle(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var name = key[(key.LastIndexOf('/') + 1)..];
        if (!name.EndsWith(".md", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = name[..^3].Split("--");
        if (parts.Length < 4 || parts[0].Length != 20)
        {
            return null;
        }

        return parts[^1] switch
        {
            "request" => parts[0],
            "response" or "note" => parts[0] + " " + parts[^1],
            _ => null,
        };
    }

    /// <summary>The id of <paramref name="key"/>, or the key itself when it is not a message file name.</summary>
    public static string Id(string key) => Handle(key) is { } h ? h.Split(' ')[0] : key;

    /// <summary><c>[hierarchy-msg {id}[ type]]</c>; a key that is not a message file name is carried as it is.</summary>
    public static string Token(string key) => $"[hierarchy-msg {Handle(key) ?? key}]";

    /// <summary><see cref="Token"/> followed by <c>read it with `ah msg show {id}[ type]`</c>.</summary>
    public static string ShowLine(string key) => Handle(key) is { } h ? $"[hierarchy-msg {h}] read it with `ah msg show {h}`" : Token(key);
}
