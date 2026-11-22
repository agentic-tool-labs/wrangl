using Ah.Abstractions;

namespace Ah.Daemon;

/// <summary>
/// Reads the static files of the bridge page from one directory. Read-only, an extension allow-list, no directory listing, and
/// a path that resolves outside the directory is not found.
/// </summary>
internal sealed class UiStatic(IFileReader files, string root)
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.Ordinal)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".woff2"] = "font/woff2",
    };

    /// <summary>The file served for the request path <paramref name="requestPath"/> (which starts with <c>/ui/</c>), or null.</summary>
    internal (byte[] Bytes, string ContentType)? Read(string requestPath)
    {
        if (root.Length == 0 || !requestPath.StartsWith("/ui/", StringComparison.Ordinal))
        {
            return null;
        }

        var relative = requestPath[4..];
        if (relative.Length == 0)
        {
            relative = "index.html";
        }

        if (relative.Contains('\\', StringComparison.Ordinal) || relative.Contains('\0', StringComparison.Ordinal)
            || relative.Split('/').Any(static s => s is "" or "." or ".."))
        {
            return null;
        }

        var extension = Path.GetExtension(relative).ToLowerInvariant();
        if (!ContentTypes.TryGetValue(extension, out var contentType))
        {
            return null;
        }

        var directory = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(directory, relative));
        if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = files.ReadBytes(full);
            return (extension == ".js" && !relative.StartsWith("vendor/", StringComparison.Ordinal) ? BlankLineComments(bytes) : bytes, contentType);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="source"/> with every line whose first non-blank characters (space or tab) are <c>//</c> made empty; the
    /// line's terminator (<c>\n</c> or <c>\r\n</c>) stays, so line numbers match the source (adr-29).
    /// </summary>
    private static byte[] BlankLineComments(ReadOnlySpan<byte> source)
    {
        var output = new byte[source.Length];
        var length = 0;
        while (!source.IsEmpty)
        {
            var end = source.IndexOf((byte)'\n');
            var line = end < 0 ? source : source[..(end + 1)];
            source = source[line.Length..];
            if (line.TrimStart(" \t"u8).StartsWith("//"u8))
            {
                line = line.EndsWith("\r\n"u8) ? "\r\n"u8 : line.EndsWith("\n"u8) ? "\n"u8 : ReadOnlySpan<byte>.Empty;
            }

            line.CopyTo(output.AsSpan(length));
            length += line.Length;
        }

        return output[..length];
    }
}
