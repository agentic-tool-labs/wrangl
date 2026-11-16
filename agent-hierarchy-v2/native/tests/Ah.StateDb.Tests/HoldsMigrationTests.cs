using Ah.Abstractions;

namespace Ah.StateDb.Tests;

/// <summary>Migration V35 (per-dir-files-to-db S11a-holds): a team home's old hold files become rows, once, verbatim; a read never mistakes unreadable for no row.</summary>
public sealed class HoldsMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ahholds").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Plant(string team, string body)
    {
        var folder = Path.Join(_root, "th", "holds", team);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, "hold.json"), body);
        return Path.Join(_root, "th");
    }

    [Fact]
    public void WithNoDatabaseYetTheHoldIsItsFile()
    {
        var teamHome = Plant("t", "{\"kind\": \"quit\"}\n");
        var holds = StateDbModule.CreateHolds(Path.Join(_root, "no-db-home"));

        Assert.Equal(new HoldRead("{\"kind\": \"quit\"}\n", null), holds.Read(teamHome, "t"));
        Assert.Equal(new HoldRead(null, null), holds.Read(teamHome, "other"));
    }

    [Fact]
    public void TheImportPassTakesTheHoldsVerbatimACorruptOneIncluded()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Plant("t", "{\"kind\": \"quit\"}\n");
        _ = Plant("bad", "{not json");
        var holds = StateDbModule.CreateHolds(home);

        // the negative: a read alone takes nothing (files-off-disk §4)
        Assert.Equal(new HoldRead(null, null), holds.Read(teamHome, "t"));
        _ = StateDbModule.ImportHierDirFiles(home, teamHome, force: true);

        Assert.Equal(new HoldRead("{\"kind\": \"quit\"}\n", null), holds.Read(teamHome, "t"));
        Assert.Equal(new HoldRead("{not json", null), holds.Read(teamHome, "bad"));

        // the negative: a file written after the take is not the hold; the row is
        File.WriteAllText(Path.Join(teamHome, "holds", "t", "hold.json"), "{\"kind\": \"stop\"}\n");
        Assert.Equal("{\"kind\": \"quit\"}\n", holds.Read(teamHome, "t").Body);
    }

    [Fact]
    public void AMemberPauseIsOneRowThatResumeOverwrites()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Path.Join(_root, "th");
        var holds = StateDbModule.CreateHolds(home);
        holds.SetPause(teamHome, "t", "impl", true, 1000);
        Assert.Equal(new MemberPause(true, 1000), holds.PauseOf(teamHome, "t", "impl"));

        holds.SetPause(teamHome, "t", "impl", false, 2000);
        Assert.Equal(new MemberPause(false, 2000), holds.PauseOf(teamHome, "t", "impl"));
    }

    [Fact]
    public void NegativeAPauseCoversOnlyItsOwnMemberAndTeam()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Path.Join(_root, "th");
        var holds = StateDbModule.CreateHolds(home);
        holds.SetPause(teamHome, "t", "impl", true, 1000);

        Assert.Null(holds.PauseOf(teamHome, "t", "impl-2"));
        Assert.Null(holds.PauseOf(teamHome, "u", "impl"));
        Assert.Null(StateDbModule.CreateHolds(Path.Join(_root, "no-db-home")).PauseOf(teamHome, "t", "impl"));
    }

    [Fact]
    public void ChangeWritesInOneTransactionAndARefusalWritesNothing()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Path.Join(_root, "th");
        var holds = StateDbModule.CreateHolds(home);
        holds.Change(teamHome, "t", current => current is null ? new HoldWrite("{\"kind\": \"quit\"}\n") : null);
        Assert.Throws<TimeoutException>(() => holds.Change(teamHome, "t", _ => throw new TimeoutException()));
        Assert.Equal("{\"kind\": \"quit\"}\n", holds.Read(teamHome, "t").Body);

        holds.Change(teamHome, "t", _ => new HoldWrite(null));
        Assert.Equal(new HoldRead(null, null), holds.Read(teamHome, "t"));
    }

    [Fact]
    public void AReleaseWithAnArchiveWritesTheArchiveRowAndOneWithoutWritesNone()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Path.Join(_root, "th");
        var holds = StateDbModule.CreateHolds(home);
        holds.Change(teamHome, "t", _ => new HoldWrite("{\"kind\": \"quit\"}\n"));
        holds.Change(teamHome, "t", _ => new HoldWrite(null));
        holds.Change(teamHome, "u", _ => new HoldWrite("{\"kind\": \"quit\"}\n"));
        holds.Change(teamHome, "u", _ => new HoldWrite(null, "20261111-120000", "{\"kind\": \"quit\", \"resumed_at\": \"x\"}\n"));

        using var reader = StateDbModule.OpenReader(home).Reader!;
        var rows = reader.Query("SELECT team, stamp, body FROM hold_archive");
        var row = Assert.Single(rows);
        Assert.Equal("u", (string)row[0]!);
        Assert.Equal("20261111-120000", (string)row[1]!);
        Assert.Contains("resumed_at", (string)row[2]!, StringComparison.Ordinal);
        Assert.Equal(new HoldRead(null, null), holds.Read(teamHome, "u"));
    }

    [Fact]
    public void AHoldFileWrittenAfterTheTakeHoldsTheTeam()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Plant("t", "{\"kind\": \"quit\"}\n");
        var holds = StateDbModule.CreateHolds(home);
        _ = StateDbModule.ImportHierDirFiles(home, teamHome, force: true);
        Assert.Equal(new HoldRead(null, null), holds.Read(teamHome, "u"));

        // an older binary writes a hold file after the move: held, never released
        var late = Plant("u", "{\"kind\": \"stop\"}\n");
        File.SetLastWriteTimeUtc(Path.Join(late, "holds", "u", "hold.json"), DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(holds.Read(teamHome, "u").Unreadable);

        // the negative: a file no newer than the take is not a hold
        File.SetLastWriteTimeUtc(Path.Join(late, "holds", "u", "hold.json"), DateTime.UtcNow.AddDays(-1));
        Assert.Equal(new HoldRead(null, null), holds.Read(teamHome, "u"));
    }

    [Fact]
    public void AReleaseOnAnUntakenTeamHomeSeesTheFilesHoldAndThePassDoesNotBringItBack()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Plant("t", "{\"kind\": \"quit\"}\n");
        var holds = StateDbModule.CreateHolds(home);
        string? seen = null;

        // the write's own take (atp-1518): decide builds on the file's hold, never on "none"
        holds.Change(teamHome, "t", current =>
        {
            seen = current;
            return current is null ? null : new HoldWrite(null);
        });

        Assert.Equal("{\"kind\": \"quit\"}\n", seen);
        _ = StateDbModule.ImportHierDirFiles(home, teamHome, force: true);
        Assert.Equal(new HoldRead(null, null), holds.Read(teamHome, "t"));
    }

    [Fact]
    public void AnUnreadableDatabaseIsUnreadableNeverNoRow()
    {
        var home = Path.Join(_root, "home");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        var teamHome = Path.Join(_root, "th");
        var holds = StateDbModule.CreateHolds(home);
        holds.Change(teamHome, "t", _ => new HoldWrite("{\"kind\": \"quit\"}\n"));
        File.WriteAllText(StateDbBackups.DatabasePath(home), "not a database");

        Assert.NotNull(holds.Read(teamHome, "t").Unreadable);
    }
}
