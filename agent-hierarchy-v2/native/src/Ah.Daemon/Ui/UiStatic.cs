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

        if (!ContentTypes.TryGetValue(Path.GetExtension(relative).ToLowerInvariant(), out var contentType))
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
            return (files.ReadBytes(full), contentType);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
