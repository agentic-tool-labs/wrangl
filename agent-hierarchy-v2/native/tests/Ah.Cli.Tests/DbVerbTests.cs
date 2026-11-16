using Ah.Cli.Verbs.Db;
using Ah.StateDb;

namespace Ah.Cli.Tests;

/// <summary><c>ah db backup|list|restore|check</c> against a throwaway ah home: output, exit codes, and what a restore changes.</summary>
public sealed class DbVerbTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _home = Directory.CreateTempSubdirectory("ahdbverb").FullName;

    private static readonly IStateDbBackups Backups = StateDbModule.CreateBackups();

    private string DbPath => Path.Join(_home, "state", "ah.db");

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private void MakeDatabase()
    {
        using var db = StateDbModule.Open(_home, mayCreate: true);
        Assert.Equal(StateDbAvailability.Ready, db.Status.Availability);
    }

    private (int Code, string Out, string Err) Run(Func<string, int?>? daemon, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = DbVerb.Run(_home, Noon, daemon ?? (_ => null), args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("cutover", "teams")]
    [InlineData("cutover", "msgs")]
    [InlineData("cutback", "sessions")]
    [InlineData("verify", "msgs")]
    [InlineData("export-root", "add")]
    [InlineData("archive-exports", "msgs")]
    [InlineData("export", "--domain", "msgs")]
    public void ARemovedVerbIsAUsageError(params string[] args)
    {
        var (code, stdout, stderr) = Run(null, args);

        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains("usage: ah db", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("cutover", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAVerbThatStayedIsNoUsageError()
    {
        var (code, _, stderr) = Run(null, "list");

        Assert.Equal(0, code);
        Assert.DoesNotContain("usage: ah db", stderr, StringComparison.Ordinal);
    }

    /// <summary><c>ah db status-rebuild</c> with no hierarchy services refuses with exit 1 and writes nothing to stdout.</summary>
    [Fact]
    public void StatusRebuildWithNoHierarchyServicesRefuses()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Assert.Equal(1, DbVerb.StatusRebuild(null, "/h/x", stdout, stderr));
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("no hierarchy services", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("list extra")]
    [InlineData("check extra")]
    [InlineData("backup --label")]
    [InlineData("backup stray")]
    [InlineData("restore a b")]
    [InlineData("restore --apply --apply")]
    [InlineData("restore --bogus")]
    [InlineData("init")]
    [InlineData("init --bogus")]
    [InlineData("prune")]
    [InlineData("prune --yes")]
    [InlineData("prune --unplaced --unplaced")]
    [InlineData("prune --unplaced --bogus")]
    public void AMalformedCommandPrintsTheUsageAndExitsTwo(string line)
    {
        var (code, stdout, err) = Run(null, line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.StartsWith("usage: ah db", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ABackupLabelWithAnUnsafeCharacterIsRefusedBeforeAnythingIsWritten()
    {
        MakeDatabase();

        var (code, _, err) = Run(null, "backup", "--label", "a/b");

        Assert.Equal(2, code);
        Assert.Contains("label", err, StringComparison.Ordinal);
        Assert.Empty(Backups.List(_home));
    }

    [Fact]
    public void BackupWithoutADatabaseFailsAndWritesNothing()
    {
        var (code, _, err) = Run(null, "backup");

        Assert.Equal(1, code);
        Assert.Contains("no state database", err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_home, "state", "backups")));
    }

    [Fact]
    public void BackupListAndCheckRoundTripThroughTheVerbs()
    {
        MakeDatabase();

        var backup = Run(null, "backup", "--label", "manual");
        var list = Run(null, "list");
        var check = Run(null, "check");

        Assert.Equal(0, backup.Code);
        Assert.Contains($"backed up ah-20261021T120000Z-v{Ah.StateDb.StateDbModule.SchemaVersion}-manual.db", backup.Out, StringComparison.Ordinal);
        Assert.Equal(0, list.Code);
        Assert.Contains($"ah-20261021T120000Z-v{Ah.StateDb.StateDbModule.SchemaVersion}-manual.db\t0h00m\t", list.Out, StringComparison.Ordinal);
        Assert.Contains($"schema {Ah.StateDb.StateDbModule.SchemaVersion}\tok", list.Out, StringComparison.Ordinal);
        Assert.Equal(0, check.Code);
        Assert.Contains("state database: integrity ok", check.Out, StringComparison.Ordinal);
        Assert.Contains($"newest backup ah-20261021T120000Z-v{Ah.StateDb.StateDbModule.SchemaVersion}-manual.db: ok", check.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ListWithNoBackupsSaysSoAndCheckOfADamagedDatabaseExitsOne()
    {
        Assert.Equal("no backups", Run(null, "list").Out.Trim());

        Directory.CreateDirectory(Path.Join(_home, "state"));
        File.WriteAllText(DbPath, new string('x', 4096));
        var (code, stdout, _) = Run(null, "check");

        Assert.Equal(1, code);
        Assert.Contains("DAMAGED", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreIsADryRunByDefaultEvenWhileTheDaemonRunsAndChangesNothing()
    {
        MakeDatabase();
        _ = Run(null, "backup");
        var before = File.ReadAllBytes(DbPath);

        var (code, stdout, _) = Run(_ => 4242, "restore");

        Assert.Equal(0, code);
        Assert.Contains("dry run", stdout, StringComparison.Ordinal);
        Assert.Contains("--apply", stdout, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.False(Directory.Exists(Path.Join(_home, "state", "replaced")));
    }

    [Fact]
    public void RestoreWithApplyIsRefusedWhileTheDaemonRunsAndDoneOnceItIsStopped()
    {
        MakeDatabase();
        _ = Run(null, "backup");
        var before = File.ReadAllBytes(DbPath);

        var refused = Run(_ => 4242, "restore", "--apply");

        Assert.Equal(1, refused.Code);
        Assert.Contains("ahd is running (pid 4242); stop ahd, then retry", refused.Err, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.False(Directory.Exists(Path.Join(_home, "state", "replaced")));

        var done = Run(null, "restore", "--apply");

        Assert.Equal(0, done.Code);
        Assert.Contains($"restored ah-20261021T120000Z-v{Ah.StateDb.StateDbModule.SchemaVersion}.db", done.Out, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Join(_home, "state", "replaced"), "ah-*.db"));
        Assert.Contains(Backups.List(_home), b => b.Label == "pre-restore");
    }

    [Fact]
    public void ARestoreThatFailsAfterTheOldDatabaseWasMovedPutsItBackAndExitsOne()
    {
        MakeDatabase();
        _ = Run(null, "backup");
        Directory.CreateDirectory(DbPath + ".restoring");

        var (code, _, err) = Run(null, "restore", "--apply");

        Assert.Equal(1, code);
        Assert.Contains("were put back", err, StringComparison.Ordinal);
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void RestoreOfAnUnknownBackupFailsWithTheReason()
    {
        MakeDatabase();

        var (code, _, err) = Run(null, "restore", "ah-20200101T000000Z-v1.db");

        Assert.Equal(1, code);
        Assert.Contains("no backup named", err, StringComparison.Ordinal);
    }

    private void SetAside()
    {
        MakeDatabase();
        var written = StateDbModule.OpenDirect(_home).Write(tx =>
        {
            tx.Execute("INSERT INTO sessions_unplaced(session_id, reason, started_at, last_seen_at) VALUES ('a', 'no team file', 1, 1)");
            tx.Execute("INSERT INTO sessions_unplaced(session_id, reason, started_at, last_seen_at) VALUES ('b', 'no registration', 1, 1)");
            tx.Execute("INSERT INTO sessions_unplaced(session_id, reason, started_at, last_seen_at) VALUES ('c', 'no team file', 1, 1)");
            return 0;
        });
        Assert.True(written.Ok, written.Message);
    }

    [Fact]
    public void PruneUnplacedWithoutYesReportsWhatItWouldRemoveAndRemovesNothing()
    {
        SetAside();

        var (code, stdout, _) = Run(null, "prune", "--unplaced");

        Assert.Equal(0, code);
        Assert.Contains("would remove 3 unplaced session(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("--yes", stdout, StringComparison.Ordinal);
        Assert.Equal(3L, StateDbModule.OpenReader(_home).Reader!.Query("SELECT count(*) FROM sessions_unplaced")[0][0]);
    }

    private void Write(string sql)
    {
        var written = StateDbModule.OpenDirect(_home).Write(tx =>
        {
            tx.Execute(sql);
            return 0;
        });
        Assert.True(written.Ok, written.Message);
    }

    private const string LiveRow = "INSERT INTO sessions_unplaced(session_id, reason, started_at, last_seen_at, doc) VALUES ('live', 'no team file', 1, 1, '{\"session_id\":\"live\"}')";

    private IEnumerable<string> UnplacedIds() =>
        StateDbModule.OpenReader(_home).Reader!.Query("SELECT session_id FROM sessions_unplaced ORDER BY session_id").Select(r => (string)r[0]!);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativePruneKeepsALiveRowWithOrWithoutAnAuthorityRow(bool authorityRow)
    {
        SetAside();
        Write(LiveRow);
        if (authorityRow)
        {
            Write("INSERT OR REPLACE INTO meta(key, value) VALUES ('authority.sessions', 'db')");
        }

        var (code, stdout, _) = Run(null, "prune", "--unplaced", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("removed 3 unplaced session(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("1 session(s) with a document stay", stdout, StringComparison.Ordinal);
        Assert.Equal(["live"], UnplacedIds());
    }

    [Fact]
    public void NegativePruneKeepsAnEndedRowWithADocument()
    {
        MakeDatabase();
        Write(LiveRow);
        Write("UPDATE sessions_unplaced SET ended_at = 2");

        var (code, stdout, _) = Run(null, "prune", "--unplaced", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("removed 0 unplaced session(s)", stdout, StringComparison.Ordinal);
        Assert.Equal(["live"], UnplacedIds());
    }

    [Fact]
    public void PruneRemovesAnEndedRowWithNoDocument()
    {
        SetAside();
        Write("UPDATE sessions_unplaced SET ended_at = 2");

        _ = Run(null, "prune", "--unplaced", "--yes");

        Assert.Empty(UnplacedIds());
    }

    [Fact]
    public void NegativeTheDoctorDoesNotOfferToPruneALiveRow()
    {
        MakeDatabase();
        Write(LiveRow);

        Assert.DoesNotContain("ah db prune", Backups.Health(_home, Noon, BackupSettings.Default).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PruneUnplacedWithYesRemovesThemAndTheDoctorNoteGoesAway()
    {
        SetAside();
        Assert.Contains("unplaced sessions: 3 (no registration: 1, no team file: 2; after an upgrade", Backups.Health(_home, Noon, BackupSettings.Default).Detail, StringComparison.Ordinal);

        var (code, stdout, _) = Run(null, "prune", "--unplaced", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("removed 3 unplaced session(s)", stdout, StringComparison.Ordinal);
        Assert.Contains(Backups.List(_home), b => b.Name.Contains("pre-prune", StringComparison.Ordinal));
        Assert.Equal(0L, StateDbModule.OpenReader(_home).Reader!.Query("SELECT count(*) FROM sessions_unplaced")[0][0]);
        Assert.DoesNotContain("unplaced", Backups.Health(_home, Noon, BackupSettings.Default).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PruneUnplacedWithoutYesTakesNoBackup()
    {
        SetAside();

        _ = Run(null, "prune", "--unplaced");

        Assert.DoesNotContain(Backups.List(_home), b => b.Name.Contains("pre-prune", StringComparison.Ordinal));
    }

    [Fact]
    public void PruneUnplacedRefusesToDeleteWhenTheBackupBeforeItFails()
    {
        SetAside();
        var backups = Path.Join(_home, "state", "backups");
        if (Directory.Exists(backups))
        {
            Directory.Delete(backups, recursive: true);
        }

        File.WriteAllText(backups, "a file where the backup directory must be");

        var (code, _, err) = Run(null, "prune", "--unplaced", "--yes");

        Assert.Equal(1, code);
        Assert.Contains("nothing was removed", err, StringComparison.Ordinal);
        Assert.Equal(3L, StateDbModule.OpenReader(_home).Reader!.Query("SELECT count(*) FROM sessions_unplaced")[0][0]);
    }

    [Fact]
    public void PruneUnplacedWithoutADatabaseFailsAndCreatesNothing()
    {
        var (code, _, err) = Run(null, "prune", "--unplaced", "--yes");

        Assert.Equal(1, code);
        Assert.Contains("no state database", err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(_home, "state", "ah.db")));
    }

    [Fact]
    public void ADatabaseWithNothingSetAsideHasNoUnplacedNoteInTheDoctorRow()
    {
        MakeDatabase();

        Assert.DoesNotContain("unplaced", Backups.Health(_home, Noon, BackupSettings.Default).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void InitAcceptEmptyIsRefusedBecauseNothingProvesTheUserApprovedAndCreatesNothing()
    {
        var (code, _, err) = Run(null, "init", "--accept-empty");

        Assert.Equal(1, code);
        Assert.Contains("only the user", err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(_home, "state", "ah.db")));
    }
}
