using System.Globalization;
using Ah.Abstractions;

namespace Ah.Core.Files;

/// <summary>A cheap change marker of a file: its size and modification time.</summary>
public static class FileStamps
{
    /// <summary><c>size:mtime</c> of <paramref name="path"/>, <c>-</c> when it is absent and <c>?</c> when it cannot be read.</summary>
    /// <param name="files">The file reader.</param>
    /// <param name="path">The file to stamp.</param>
    /// <returns>The stamp.</returns>
    public static string Of(IFileReader files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            return files.Stat(path) is { } st ? st.Size.ToString(CultureInfo.InvariantCulture) + ":" + st.MtimeMs.ToString("R", CultureInfo.InvariantCulture) : "-";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    /// <summary>The stamp of a mailbox's entries file and its acks, joined with <c>|</c>.</summary>
    /// <param name="files">The file reader.</param>
    /// <param name="boxDir">The mailbox directory.</param>
    /// <returns>The box stamp.</returns>
    public static string OfBox(IFileReader files, string boxDir) =>
        Of(files, boxDir + "/entries.jsonl") + "|" + Of(files, boxDir + "/acks");
}
