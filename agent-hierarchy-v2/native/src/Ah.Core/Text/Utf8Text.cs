using System.Text;

namespace Ah.Core.Text;

/// <summary>
/// UTF-8 as Node's <c>"utf8"</c> reads and writes it: invalid sequences decode to U+FFFD, a leading BOM stays as
/// U+FEFF, and nothing written carries a BOM. Never use <c>File.ReadAllText</c> or a <c>StreamReader</c>, which strip the BOM.
/// </summary>
public static class Utf8Text
{
    private static readonly UTF8Encoding Writer = new(encoderShouldEmitUTF8Identifier: false);

    public static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    public static byte[] Encode(string text) => Writer.GetBytes(text);

    /// <summary>The most bytes <see cref="Encode(ReadOnlySpan{char}, Span{byte})"/> writes for <paramref name="chars"/> characters.</summary>
    public static int MaxBytes(int chars) => Writer.GetMaxByteCount(chars);

    /// <summary>Writes what <see cref="Encode(string)"/> gives for <paramref name="text"/> into <paramref name="into"/>; returns the byte count.</summary>
    public static int Encode(ReadOnlySpan<char> text, Span<byte> into) => Writer.GetBytes(text, into);

    /// <summary>The bytes of <paramref name="text"/>'s characters: what <see cref="Encode(string)"/> gives for its string, without making the string when the builder holds one run of characters.</summary>
    public static byte[] Encode(StringBuilder text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlyMemory<char> only = default;
        var chunks = 0;
        foreach (var chunk in text.GetChunks())
        {
            only = chunk;
            chunks++;
        }

        if (chunks != 1)
        {
            return Writer.GetBytes(text.ToString());
        }

        var bytes = new byte[Writer.GetByteCount(only.Span)];
        _ = Writer.GetBytes(only.Span, bytes);
        return bytes;
    }
}
