using System.Security.Cryptography;
using Ah.Abstractions;

namespace Ah.Core.Digests;

/// <summary>A file's bytes and their digest; <see cref="Bytes"/> is null and <see cref="Digest"/> is <see cref="ContentDigest.Absent"/> when there is no file.</summary>
public readonly record struct DigestedBytes(byte[]? Bytes, string Digest);

/// <summary>The content digest the state hub's preconditions compare: SHA-256 as lower-case hex, or <c>absent</c> for no file.</summary>
public static class ContentDigest
{
    public const string Absent = "absent";

    /// <summary>
    /// The bytes of <paramref name="path"/> and their digest in one read. A file or parent directory that does not
    /// exist gives <c>(null, "absent")</c>; every other read error is thrown.
    /// </summary>
    public static DigestedBytes ReadWithDigest(IFileReader files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            var bytes = files.ReadBytes(path);
            return new DigestedBytes(bytes, OfBytes(bytes));
        }
        catch (FileNotFoundException)
        {
            return new DigestedBytes(null, Absent);
        }
        catch (DirectoryNotFoundException)
        {
            return new DigestedBytes(null, Absent);
        }
    }

    public static string OfFile(IFileReader files, string path) => ReadWithDigest(files, path).Digest;

    public static string OfBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Whether <paramref name="expect"/> is <see cref="Absent"/> or 64 lower-case hex characters, with nothing before or after.</summary>
    public static bool IsValidExpect(string? expect)
    {
        if (expect is null)
        {
            return false;
        }

        if (expect == Absent)
        {
            return true;
        }

        if (expect.Length != 64)
        {
            return false;
        }

        foreach (var c in expect)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
