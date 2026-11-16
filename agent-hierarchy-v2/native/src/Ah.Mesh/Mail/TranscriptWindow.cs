using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Mesh;

/// <summary>Reads the part of a growing transcript a hook has not seen yet, by a positioned read of its tail and never the whole file.</summary>
internal static class TranscriptWindow
{
    /// <summary>The most one read takes; rows older than that, when a session wrote more between two scans, are not read.</summary>
    internal const int DefaultCapBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The complete lines of the file from byte <paramref name="offset"/> (from the start when it lies past the end), decoded, and
    /// the offset after them; null when the file cannot be read. A last line with no newline yet is left for a later read. When
    /// more than <paramref name="capBytes"/> arrived since <paramref name="offset"/>, the read starts at the first line that begins
    /// inside the last <paramref name="capBytes"/>.
    /// </summary>
    internal static (string Text, long End)? ReadLinesFrom(IFileReader files, string path, long offset, int capBytes = DefaultCapBytes)
    {
        FileTail tail;
        try
        {
            if (files.Stat(path) is not { } stat)
            {
                return null;
            }

            var from = offset < 0 || offset > stat.Size ? 0 : offset;
            var want = stat.Size - from;
            if (want == 0)
            {
                return (string.Empty, from);
            }

            tail = files.ReadTail(path, (int)Math.Min(want, capBytes));
            if (tail.Length - tail.Bytes.Length > from && want < capBytes)
            {
                // the file grew between the size and the read: take what lies from the offset
                tail = files.ReadTail(path, (int)Math.Min(tail.Length - from, capBytes));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var bytes = tail.Bytes;
        var windowStart = tail.Length - bytes.Length;
        var begin = 0;
        if (tail.StartsMidFile && windowStart > Math.Max(0, offset < 0 || offset > tail.Length ? 0 : offset))
        {
            var newline = Array.IndexOf(bytes, (byte)0x0a);
            if (newline < 0)
            {
                return (string.Empty, tail.Length);
            }

            begin = newline + 1;
        }

        var last = Array.LastIndexOf(bytes, (byte)0x0a);
        if (last < begin)
        {
            return (string.Empty, windowStart + begin);
        }

        return (Utf8Text.Decode(bytes.AsSpan(begin, last + 1 - begin)), windowStart + last + 1);
    }
}
