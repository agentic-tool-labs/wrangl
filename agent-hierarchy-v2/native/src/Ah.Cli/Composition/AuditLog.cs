using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>The one append of an audit row to a JSON-lines file under the AH home; cutover and decision attempts share it.</summary>
internal static class AuditLog
{
    /// <summary>Appends <paramref name="row"/> as one line, creating the directory; returns why it could not, or null.</summary>
    public static string? Append(string path, JsonObject row)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.AppendAllText(path, JsonText.Stringify(row, 0) + "\n");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
