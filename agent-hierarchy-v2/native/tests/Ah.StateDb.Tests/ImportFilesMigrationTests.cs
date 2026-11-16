using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V18 (db-only-stores §5): what the database holds says whether the home imports itself.</summary>
public sealed class ImportFilesMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahimportmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    private string? UpgradedWith(params string[] rows)
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            // a real version 17 home holds no mode row a later migration writes (mode.decisions, mode.backlog)
            raw.Exec("DELETE FROM meta WHERE key = 'import.files' OR key LIKE 'mode.%' OR key LIKE 'authority.%'");
            foreach (var key in rows)
            {
                raw.Exec($"INSERT OR REPLACE INTO meta(key, value) VALUES ('{key}', 'db')");
            }

            raw.Exec("UPDATE meta SET value = '17' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using var read = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadOnly, 1000);
        return StateDbRows.Run(read, "SELECT value FROM meta WHERE key = 'import.files'", []).Select(static r => r[0] as string).FirstOrDefault();
    }

    private const string Manual = "manual";

    private const string Auto = "auto";

    [Theory]
    [InlineData("mode.teams")]
    [InlineData("authority.teams")]
    [InlineData("mode.sessions")]
    [InlineData("authority.sessions")]
    [InlineData("mode.peer-records")]
    [InlineData("mode.mail")]
    public void AHomeWithADomainOnTheDatabaseIsManual(string key) => Assert.Equal(Manual, UpgradedWith(key));

    [Theory]
    [InlineData("mode.msgs")]
    [InlineData("authority.msgs")]
    [InlineData("mode.backlog")]
    public void NegativeAHomeArmedOnlyByMessagesOrTheBacklogIsAuto(string key) => Assert.Equal(Auto, UpgradedWith(key));

    [Fact]
    public void NegativeAKeyThatOnlyLooksLikeAnAuthorityIsAuto() => Assert.Equal(Auto, UpgradedWith("authority_at.teams"));

    private DbOpOutcome MailWriteWithMode(string domain)
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec($"INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.{domain}', 'db')");
        }

        return StateDbModule.CreateOpRunner(_home).Run(new DbOpRequest("mail", "op-1", "direct", _ => new OpResult(true, System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}"))));
    }

    [Theory]
    [InlineData("teams")]
    [InlineData("sessions")]
    public void AFlippedStoreDomainDoesNotRefuseAMailWrite(string domain) => Assert.NotEqual(DbOpStatus.Refused, MailWriteWithMode(domain).Status);

    [Fact]
    public void NegativeAModeForAnUnknownDomainStillRefusesAMailWrite() => Assert.Equal(DbOpStatus.Refused, MailWriteWithMode("zzz").Status);
}
