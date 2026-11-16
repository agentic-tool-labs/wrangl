using System.Buffers.Text;
using System.Text;

namespace Ah.Watch.Model;

/// <summary>
/// The opaque cursor of a list page: the keyset tuple of the list kind (an exchange id, a byte offset, <c>(at, stream, seq)</c>), as URL-safe
/// base64 of its parts joined by a unit separator. A client passes one back as it got it.
/// </summary>
internal static class ListCursor
{
    private const char Separator = '\u001f';

    public static string Encode(params string[] parts) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Join(Separator, parts)));

    /// <summary>The parts of <paramref name="cursor"/>, or null when it is not a cursor or does not have exactly <paramref name="count"/> parts.</summary>
    public static string[]? Decode(string cursor, int count)
    {
        if (cursor.Length == 0 || cursor.Length > 1024 || !Base64Url.IsValid(cursor))
        {
            return null;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }

        var parts = text.Split(Separator);
        return parts.Length == count && parts.All(static p => p.Length > 0) ? parts : null;
    }
}
