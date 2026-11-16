using Ah.Abstractions;

namespace Ah.Platform;

/// <summary>
/// The one routine behind the bounded JSON-lines logs: make the parent directory, move the file to <c>.1</c> once it is over
/// <see cref="CapBytes"/>, then append the line. A file that cannot be rotated is appended to anyway.
/// </summary>
internal static class RotatedJsonl
{
    internal const long CapBytes = 1024 * 1024;

    internal static void Append(IRawFileWriter writer, IRecordAppender appender, string path, ReadOnlySpan<byte> line)
    {
        writer.Mkdir(Path.GetDirectoryName(path)!);
        try
        {
            if (new FileInfo(path).Length > CapBytes)
            {
                writer.Rename(path, path + ".1");
            }
        }
        catch (IOException)
        {
            // no log yet, or it could not be rotated: append anyway
        }

        appender.Append(path, [.. line, (byte)'\n']);
    }
}
