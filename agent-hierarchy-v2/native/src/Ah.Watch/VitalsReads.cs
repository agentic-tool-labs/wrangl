using System.Text;
using Ah.Abstractions;

namespace Ah.Watch;

/// <summary>A branch read from a checkout's HEAD file: the name, or the short sha of a detached HEAD with the reason <c>detached</c>.</summary>
internal sealed record BranchReading(string? Value, string? Reason)
{
    internal const string Detached = "detached";
    internal const string NoGit = "no git";
}

/// <summary>The branch of a checkout, read from files only; no git process is started.</summary>
internal static class GitHead
{
    private const string RefPrefix = "ref: ";
    private const string BranchPrefix = "refs/heads/";
    private const string PointerPrefix = "gitdir:";

    /// <summary>The branch <paramref name="root"/> is on; null for a detached head, or a root that is no repository or cannot be read.</summary>
    internal static string? BranchOf(IFileReader files, string root)
    {
        var head = Read(root, path => TextOf(files, path));
        return head.Reason is null ? head.Value : null;
    }

    /// <summary>A small file's text; null when the path is no readable file.</summary>
    internal static string? TextOf(IFileReader files, string path)
    {
        try
        {
            var read = files.ReadNoFollow(path, 4096);
            return read.Kind == FileKind.File ? Encoding.UTF8.GetString(read.Bytes) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The HEAD of the checkout at <paramref name="root"/>. <c>.git</c> is either a directory holding <c>HEAD</c> or, in a linked
    /// work tree, a file whose <c>gitdir:</c> line names the directory that does. <paramref name="readText"/> gives a file's text, or
    /// null when the path is not a readable file.
    /// </summary>
    internal static BranchReading Read(string root, Func<string, string?> readText)
    {
        var dotGit = Path.Join(root, ".git");
        var head = readText(Path.Join(dotGit, "HEAD"));
        if (head is null && readText(dotGit) is { } pointer && pointer.TrimStart().StartsWith(PointerPrefix, StringComparison.Ordinal))
        {
            var gitDir = pointer.TrimStart()[PointerPrefix.Length..].Split('\n')[0].Trim();
            head = gitDir.Length > 0 ? readText(Path.Join(Path.GetFullPath(gitDir, root), "HEAD")) : null;
        }

        var text = head?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new BranchReading(null, BranchReading.NoGit);
        }

        if (text.StartsWith(RefPrefix, StringComparison.Ordinal))
        {
            var name = text[RefPrefix.Length..].Trim();
            return new BranchReading(name.StartsWith(BranchPrefix, StringComparison.Ordinal) ? name[BranchPrefix.Length..] : name, null);
        }

        return text.Length is 40 or 64 && text.All(static c => char.IsAsciiHexDigit(c))
            ? new BranchReading(text[..7], BranchReading.Detached)
            : new BranchReading(null, BranchReading.NoGit);
    }
}

/// <summary>The one format of a computed instant on the detail document: UTC, to the second, with <c>Z</c>.</summary>
internal static class IsoTime
{
    internal static string Seconds(double epochMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)epochMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
