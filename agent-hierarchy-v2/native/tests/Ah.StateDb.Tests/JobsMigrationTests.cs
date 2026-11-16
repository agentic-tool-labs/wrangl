using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V27 (per-dir-files-to-db S8): a version 23 database gets <c>jobs</c>; the source reads one team home's rows by team, then id.</summary>
public sealed class JobsMigrationTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahjobsmig").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string DbPath => Path.Join(_home, "state", "ah.db");

    [Fact]
    public void AVersion23DatabaseGetsTheTableAndTheSourceReadsOneHomesRowsByTeamThenId()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            raw.Exec("DROP TABLE jobs");
            raw.Exec("UPDATE meta SET value = '23' WHERE key = 'schema_version'");
        }

        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using (var raw = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 1000))
        {
            Assert.Equal(StateDbModule.SchemaVersion.ToString(CultureInfo.InvariantCulture), raw.ScalarText("SELECT value FROM meta WHERE key = 'schema_version'"));
            raw.Exec("INSERT INTO jobs(team_home, team, id, body, ended_at) VALUES ('/h', 'tm', 'b', '{}', NULL), ('/h', 'tm', 'a', '{}', 5), ('/h', 'aa', 'z', '{}', NULL), ('/other', 'tm', 'x', '{}', NULL)");
        }

        Assert.Equal(["aa/z", "tm/a", "tm/b"], StateDbModule.CreateJobs(_home).Rows("/h").Select(static r => r.Team + "/" + r.Id));

        // the negative: a home with no rows reads none, not another home's
        Assert.Empty(StateDbModule.CreateJobs(_home).Rows("/none"));
    }

    [Fact]
    public void ABusyReadThatMustAnswerThrowsAndAnyOtherReadsNone()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        using var holder = SqliteConnection.Open(DbPath, SqliteOpenMode.ReadWrite, 0);
        holder.Exec("PRAGMA locking_mode=EXCLUSIVE");
        holder.Exec("BEGIN EXCLUSIVE");
        holder.Exec("INSERT INTO jobs(team_home, team, id, body, ended_at) VALUES ('/h', 'tm', 'a', '{}', NULL)");
        holder.Exec("COMMIT");

        var busy = Assert.Throws<InvalidOperationException>(() => StateDbModule.CreateJobs(_home).Rows("/h", mustAnswer: true));
        Assert.StartsWith("the state database is busy; try again", busy.Message, StringComparison.Ordinal);

        // the negative: a lister keeps reading the busy database as no rows
        Assert.Empty(StateDbModule.CreateJobs(_home).Rows("/h"));
    }

    [Fact]
    public void TheJobsTakeMovesTheJobsCounterSoAHeldRowSetIsReadAgain()
    {
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        var hier = Directory.CreateDirectory(Path.Join(_home, "hier", "jobs", "tm")).Parent!.Parent!.FullName;
        var before = StateDbModule.DomainSeq(_home, "jobs") ?? 0;

        // the negative: a dir with no job records takes nothing and leaves the counter where it was
        Assert.False(new StateDbJobs(_home).Take(Path.Join(_home, "nojobs")));
        Assert.Equal(before, StateDbModule.DomainSeq(_home, "jobs") ?? 0);

        File.WriteAllText(Path.Join(hier, "jobs", "tm", "j1.json"), "{\"id\":\"j1\",\"owner\":\"impl\"}");
        Assert.True(new StateDbJobs(_home).Take(hier));
        Assert.Equal(before + 1, StateDbModule.DomainSeq(_home, "jobs"));
    }
}
