using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb.Tests;

/// <summary>A backup's logical export, written straight from the rows' bytes (ah2-1), is byte for byte what the JSON writer gives for each row.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class StateDbExportTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahexport").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void EveryExportedRowIsTheJsonWritersBytesForThatRow()
    {
        using (var db = StateDbModule.Open(_home, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, db.Status.Availability);
        }

        var live = StateDbBackups.DatabasePath(_home);
        using (var connection = SqliteConnection.Open(live, SqliteOpenMode.ReadWrite, 2000))
        {
            connection.Exec("CREATE TABLE probe_export(i, r, t, b, n)");
            connection.Exec(
                """
                INSERT INTO probe_export VALUES
                  (1, 1.5, 'plain', x'00ff10', NULL),
                  (-42, 1e21, 'quote " back \ slash', x'', NULL),
                  (9007199254740991, -0.0, 'tab' || char(9) || 'nl' || char(10) || 'cr' || char(13) || 'bs' || char(8) || 'ff' || char(12) || 'c1' || char(1) || 'c31' || char(31), NULL, NULL),
                  (9007199254740992, 1e-7, 'é ☃ 😀 ' || char(8232) || ' del' || char(127), NULL, NULL),
                  (9007199254740993, 123456789012345680000.0, '', NULL, NULL),
                  (-9223372036854775807, 0.1, CAST(x'61006263' AS TEXT), NULL, NULL),
                  (0, 2.0, CAST(x'61ff62eda080' AS TEXT), NULL, NULL)
                """);
        }

        var backup = StateDbBackups.Take(_home, new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        Assert.True(backup.HasExport);

        List<byte[]> expected = [];
        using (var connection = SqliteConnection.Open(backup.Path, SqliteOpenMode.ReadOnly, 2000))
        using (var rows = connection.Prepare("SELECT * FROM probe_export"))
        {
            while (rows.Step())
            {
                var row = new JsonObject();
                for (var c = 0; c < rows.ColumnCount; c++)
                {
                    row[rows.ColumnName(c)] = rows.ColumnType(c) switch
                    {
                        1 => rows.Int64(c),
                        2 => rows.Double(c),
                        3 => rows.Text(c),
                        4 => new JsonObject { ["base64"] = Convert.ToBase64String(rows.Blob(c)) },
                        _ => JsonValue.Null,
                    };
                }

                expected.Add(JsonText.ToUtf8(new JsonObject { ["table"] = "probe_export", ["row"] = row }, 0));
            }
        }

        var marker = Encoding.UTF8.GetBytes("{\"table\":\"probe_export\",");
        var lines = SplitLines(File.ReadAllBytes(Path.ChangeExtension(backup.Path, ".jsonl"))).Where(l => l.AsSpan().StartsWith(marker)).ToList();
        Assert.Equal(7, expected.Count);
        Assert.Equal(expected.Select(Encoding.UTF8.GetString), lines.Select(Encoding.UTF8.GetString));
        Assert.True(expected.Zip(lines).All(p => p.First.AsSpan().SequenceEqual(p.Second)));
        // every other line of the export still parses as one row object
        Assert.All(SplitLines(File.ReadAllBytes(Path.ChangeExtension(backup.Path, ".jsonl"))), l => Assert.True(JsonText.Parse(l).IsMap));
    }

    private static List<byte[]> SplitLines(byte[] all)
    {
        List<byte[]> lines = [];
        var start = 0;
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i] == (byte)'\n')
            {
                lines.Add(all[start..i]);
                start = i + 1;
            }
        }

        Assert.Equal(all.Length, start);
        return lines;
    }
}
