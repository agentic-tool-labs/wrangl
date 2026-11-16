using System.Globalization;

namespace Ah.Core.Json;

/// <summary>The one reading of a message's front matter into a <see cref="MsgFileRow"/>, for the importer and the write path.</summary>
public static class MsgFileRows
{
    /// <summary>
    /// The row of the message file <paramref name="path"/>. The header columns come from <paramref name="frontMatter"/> when it is a map
    /// and only when a field is text; <c>closed</c> and <c>withdrawn</c> are set only by the text <c>true</c>. The creation time is
    /// null when <c>created</c> does not parse.
    /// </summary>
    public static MsgFileRow FromFrontMatter(
        string poolDir, string path, string name, string id, string type, string to, string slug, JsonValue? frontMatter, bool archived, string state, byte[]? bytes)
    {
        string? Field(string key) => frontMatter is { IsMap: true } map && map.Member(key) is { IsText: true } v ? v.AsString() : null;
        var created = Field("created");
        long? createdMs = created is not null && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToUnixTimeMilliseconds() : null;
        return new MsgFileRow(
            MsgRows.PoolKey(poolDir), name, id, type, to, slug, Field("from"), Field("to_name"), Field("from_name"), Field("team"), Field("parent"),
            created, createdMs, archived, state, bytes, MsgRows.PathKey(path),
            Field("closed") == "true", Field("withdrawn") == "true", Field("closed_by"), Field("closed_reason"));
    }
}
