using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ah.Abstractions;

namespace Ah.Core.Time;

public static class Ids
{
    private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary><c>YYYYMMDD-HHMMSS-xxxx</c>: the writer's local time and four base-36 characters from random 32-bit words.</summary>
    public static string NewId(IClock clock, IRandom random)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        var now = clock.UtcNow;
        var local = now.UtcDateTime + clock.LocalOffset(now);
        var suffix = new StringBuilder();
        Span<byte> word = stackalloc byte[4];
        while (suffix.Length < 4)
        {
            random.Fill(word);
            AppendBase36(suffix, BinaryPrimitives.ReadUInt32BigEndian(word));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{local:yyyyMMdd-HHmmss}-{suffix.ToString(0, 4)}");
    }

    private static void AppendBase36(StringBuilder sb, uint value)
    {
        Span<char> buffer = stackalloc char[7];
        var at = buffer.Length;
        do
        {
            buffer[--at] = Base36[(int)(value % 36)];
            value /= 36;
        }
        while (value != 0);

        sb.Append(buffer[at..]);
    }
}
