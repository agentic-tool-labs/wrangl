using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Ah.Platform.Tests;

/// <summary>
/// The rows read from a <c>ps</c> reading's bytes are the rows the earlier reader gave, which decoded the whole reading and split
/// every line into strings. That reader is kept here as the reference.
/// </summary>
public sealed class ProcessRowsParityTests
{
    private static Dictionary<int, (int Parent, string? Tty, string Start, string? Command, int Group)> Reference(string psOutput, bool groups)
    {
        var rows = new Dictionary<int, (int, string?, string, string?, int)>();
        foreach (var line in psOutput.Split('\n'))
        {
            var fields = line.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 8
                || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
                || !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            {
                continue;
            }

            var start = string.Create(CultureInfo.InvariantCulture, $"{fields[3]} {fields[4]} {day,2} {fields[6]} {fields[7]}");
            var command = fields.Length > 8 && fields[8].Length > 0 ? fields[8] : null;
            if (groups)
            {
                if (int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var group))
                {
                    rows[pid] = (parent, null, start, command, group);
                }
            }
            else
            {
                rows[pid] = (parent, fields[2] is "??" or "?" or "-" ? string.Empty : fields[2], start, command, 0);
            }
        }

        return rows;
    }

    private static void Same(byte[] reading, bool groups)
    {
        var expected = Reference(Encoding.UTF8.GetString(reading), groups);
        var actual = ProcessTable.RowsOf(reading, groups);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (pid, row) in expected)
        {
            var got = actual[pid];
            Assert.Equal(row, (got.Parent, got.Tty, got.Start, got.Command, got.Group));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandWrittenReadingsGiveTheSameRows(bool groups)
    {
        string[] readings =
        [
            "  100     1 ??       Wed Oct 21 08:00:00 2026 claude\n  200   100 ttys001  Wed Oct  7 08:00:05 2026 /bin/zsh -c dotnet test Core.csproj\n",
            "  100     1   100 Wed Oct 21 08:00:00 2026 claude\n  200   100   200 Wed Oct 21 08:00:05 2026 sleep 300",
            "1 0 - Mon Jan 01 00:00:00 2026\n2 1 ? Mon Jan 1 00:00:00 2026    \n3 1 ?? Mon Jan 31 00:00:00 2026   spaced   out   command   \n",
            "  10  1 7 Tue Feb  3 01:02:03 2026 a\tb  c\td \r\n  11  1 7 Tue Feb  3 01:02:03 2026 café 日本語 --flag= \n",
            "\n\n   \nnot a row\n12 x ?? Wed Oct 21 08:00:00 2026 bad parent\n13 1 ?? Wed Oct xx 08:00:00 2026 bad day\n14 1 ?? Wed Oct 21 08:00:00\n",
            "99999999999 1 ?? Wed Oct 21 08:00:00 2026 overflow\n-5 1 ?? Wed Oct 21 08:00:00 2026 negative\n+5 1 ?? Wed Oct 21 08:00:00 2026 plus\n007 01 9 Wed Oct 021 08:00:00 2026 zeros\n",
            "20 1 ?? Wed Oct 21 08:00:00 2026 first\n20 2 ttys9 Thu Oct 22 09:00:00 2026 second wins\n",
            "30 1 ?? Wed Oct 21 08:00:00 2026  \n31 1 ?? Wed Oct 21 08:00:00 2026  padded \n",
            string.Empty,
        ];

        foreach (var reading in readings)
        {
            Same(Encoding.UTF8.GetBytes(reading), groups);
        }
    }

    [Fact]
    public void BytesThatAreNotUtf8InACommandGiveTheSameRows()
    {
        byte[] reading = [.. "40 1 ?? Wed Oct 21 08:00:00 2026 bad "u8, 0xFF, 0xC3, 0x28, .. " tail\n41 1 ?? Wed Oct 21 08:00:00 2026 ok\n"u8];

        Same(reading, groups: false);
    }

    [Theory]
    [InlineData("pid=,ppid=,tty=,lstart=,command=", false)]
    [InlineData("pid=,ppid=,pgid=,lstart=,command=", true)]
    public void ThisMachinesOwnReadingGivesTheSameRows(string columns, bool groups)
    {
        var ps = File.Exists("/bin/ps") ? "/bin/ps" : File.Exists("/usr/bin/ps") ? "/usr/bin/ps" : null;
        if (ps is null)
        {
            return;
        }

        var start = new ProcessStartInfo(ps) { RedirectStandardOutput = true };
        start.ArgumentList.Add("-axww");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(columns);

        start.Environment["LC_ALL"] = "C";
        start.Environment["TZ"] = "UTC";
        using var process = Process.Start(start)!;
        using var kept = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(kept);
        process.WaitForExit();

        var reading = kept.ToArray();
        Assert.NotEmpty(ProcessTable.RowsOf(reading, groups));
        Same(reading, groups);
    }
}
