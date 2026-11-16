using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Digests;

namespace Ah.StateDb.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class StateDbTeamAuthorityTests : IDisposable
{
    private const string Body = "{\"team_id\":\"t1\",\"orchestrator\":{\"name\":\"orch\"},\"members\":[{\"name\":\"orch\",\"role\":\"orchestrator\"}]}\n";
    private const string Newer = "{\"team_id\":\"t1\",\"orchestrator\":{\"name\":\"orch\"},\"members\":[{\"name\":\"orch\",\"role\":\"orchestrator\"},{\"name\":\"worker\",\"role\":\"implementor\"}]}\n";

    private readonly string _root = Directory.CreateTempSubdirectory("ahteamauth").FullName;
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var d in _open)
        {
            d.Dispose();
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string Home => Path.Join(_root, "ah");

    private string Dir(string name)
    {
        var dir = Path.Join(_root, name);
        Directory.CreateDirectory(Path.Join(dir, "teams"));
        return StateDbTeamKey.RealPath(dir);
    }

    private static string TeamFile(string dir, string name) => Path.Join(dir, "teams", name + ".json");

    /// <summary>A database that holds the team alpha of <paramref name="dir"/>.</summary>
    private ITeamAuthority Flipped(string dir)
    {
        var db = StateDbModule.Open(Home, mayCreate: true);
        _open.Add(db);
        var authority = StateDbModule.OpenTeamAuthority(Home);
        var written = authority.Write(TeamFile(dir, "alpha"), Utf8(Body));
        Assert.True(written.Ok, written.Message);
        return authority;
    }

    [Fact]
    public void AStoreHeldExclusivelyThrowsTheOneTypeWithinTheBudgetAndNeverReadsAsAbsent()
    {
        var dir = Dir("p");
        var authority = Flipped(dir);
        Assert.NotNull(authority.ReadStored(TeamFile(dir, "alpha")));
        _open.ForEach(static d => d.Dispose());
        _open.Clear();
        using var holder = SqliteConnection.Open(StateDbBackups.DatabasePath(Home), SqliteOpenMode.ReadWrite, 0);
        holder.Exec("PRAGMA locking_mode=EXCLUSIVE");
        holder.Exec("BEGIN EXCLUSIVE");
        holder.Exec("UPDATE meta SET value = value WHERE key = 'schema_version'");
        holder.Exec("COMMIT");

        var clock = Stopwatch.StartNew();
        _ = Assert.Throws<Ah.Core.Json.StoreUnavailableException>(() => authority.ReadStored(TeamFile(dir, "alpha")));
        clock.Stop();
        _ = Assert.Throws<Ah.Core.Json.StoreUnavailableException>(() => authority.ReadStored(TeamFile(dir, "nobody")));
        _ = Assert.Throws<Ah.Core.Json.StoreUnavailableException>(() => authority.ListTeams(dir));
        _ = Assert.Throws<Ah.Core.Json.StoreUnavailableException>(() => authority.ChangeToken());
        Assert.True(clock.ElapsedMilliseconds < 1000, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void NegativeAStoreIdleAgainAnswersAndAnAbsentTeamIsNull()
    {
        var dir = Dir("p");
        var authority = Flipped(dir);

        Assert.NotNull(authority.ReadStored(TeamFile(dir, "alpha")));
        Assert.Null(authority.ReadStored(TeamFile(dir, "nobody")));
        Assert.Equal(["alpha"], authority.ListTeams(dir));
    }

    [Fact]
    public void TheImportPassTakesAnUntakenTeamFileOnceAndNeverAStampedOne()
    {
        var dir = Dir("p");
        var authority = Flipped(dir);
        File.WriteAllText(TeamFile(dir, "beta"), Body);
        File.WriteAllText(TeamFile(dir, "gamma"), Body);
        File.WriteAllText(TeamFile(dir, "gamma") + ".stamp", string.Empty);

        // the negative: a listing alone takes nothing (files-off-disk §4)
        Assert.Equal(["alpha"], authority.ListTeams(dir));
        _ = StateDbModule.ImportHierDirFiles(Home, dir, force: true);
        Assert.Equal(["alpha", "beta"], authority.ListTeams(dir));

        // taken once: a cleared team stays cleared on the next listing, though its file is still there
        Assert.True(authority.Clear(TeamFile(dir, "beta")).Ok);
        Assert.Equal(["alpha"], authority.ListTeams(dir));
    }

    [Fact]
    public void ATeamImportedByThePassAndThenDisbandedStaysGoneOnTheNextPass()
    {
        var dir = Dir("p");
        var authority = Flipped(dir);
        File.WriteAllText(TeamFile(dir, "beta"), Body);

        // the negative: a listing alone takes nothing (files-off-disk §4)
        Assert.Equal(["alpha"], authority.ListTeams(dir));
        _ = StateDbModule.ImportHierDirFiles(Home, dir, force: true);
        Assert.NotNull(authority.ReadStored(TeamFile(dir, "beta")));
        Assert.True(authority.Clear(TeamFile(dir, "beta")).Ok);

        // the row and its take were stored in one transaction, so the file still beside it is never taken again
        _ = StateDbModule.ImportHierDirFiles(Home, dir, force: true);
        Assert.Equal(["alpha"], authority.ListTeams(dir));
        Assert.Null(authority.ReadStored(TeamFile(dir, "beta")));
    }

    [Fact]
    public void NegativeAListingNeverOverwritesAStoredTeamWithItsUntakenFile()
    {
        var dir = Dir("p");
        var authority = Flipped(dir);
        File.WriteAllText(TeamFile(dir, "alpha"), Newer);

        Assert.Equal(["alpha"], authority.ListTeams(dir));
        Assert.Equal(Body, Encoding.UTF8.GetString(authority.ReadStored(TeamFile(dir, "alpha"))!.Value.Span));
    }

    [Fact]
    public void ANewerSchemaRefusesTheReadRatherThanMisreadingIt()
    {
        var dir = Dir("p");
        var authority = Flipped(dir);
        using (var writer = SqliteConnection.Open(StateDbBackups.DatabasePath(Home), SqliteOpenMode.ReadWrite, 1000))
        {
            writer.Exec($"UPDATE meta SET value = '{(StateDbModule.SchemaVersion + 1).ToString(CultureInfo.InvariantCulture)}' WHERE key = 'schema_version'");
        }

        var refused = Assert.Throws<Ah.Core.Json.StoreUnavailableException>(() => authority.ReadStored(TeamFile(dir, "alpha")));

        Assert.Contains("newer", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheCurrentSchemaIsNotRefusedAndAWriteMovesTheToken()
    {
        var dir = Dir("p");
        var authority = Flipped(dir);
        var before = authority.ChangeToken();
        using (var writer = SqliteConnection.Open(StateDbBackups.DatabasePath(Home), SqliteOpenMode.ReadWrite, 1000))
        {
            writer.Exec("UPDATE teams SET doc = doc");
        }

        Assert.NotNull(authority.ReadStored(TeamFile(dir, "alpha")));
        Assert.True(authority.ChangeToken() > before);
    }

    private (string? Doc, long? At) Row(string dir, string name)
    {
        var reader = StateDbModule.OpenReader(Home).Reader!;
        _open.Add(reader);
        var rows = reader.Query($"SELECT doc, updated_at FROM teams WHERE hier_dir = '{dir}' AND team = '{name}'");
        return rows.Count == 0 ? (null, null) : ((string?)rows[0][0], (long?)rows[0][1]);
    }

    private int Members(string dir, string name)
    {
        var reader = StateDbModule.OpenReader(Home).Reader!;
        _open.Add(reader);
        return reader.Query($"SELECT name FROM members WHERE hier_dir = '{dir}' AND team = '{name}'").Count;
    }

    [Fact]
    public void AWriteStoresTheRowAndMembersInTheDatabaseAndWritesNoFile()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");

        var result = authority.Write(file, Encoding.UTF8.GetBytes(Newer));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("ok", result.Code);
        Assert.True(result.Changed);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal(Newer, Row(dir, "beta").Doc);
        Assert.Equal(2, Members(dir, "beta"));
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + ".stamp"));
    }

    [Fact]
    public void TheFirstWriteOfAHomeWithNoDatabaseCreatesItAndWritesNoFile()
    {
        var authority = StateDbModule.OpenTeamAuthority(Home);
        var dir = Dir("alpha-home");
        var file = TeamFile(dir, "beta");
        Assert.False(authority.IsDatabase);

        var result = authority.Write(file, Utf8(Body));

        Assert.True(result.Ok, result.Message);
        Assert.True(authority.IsDatabase);
        Assert.Equal(Body, Row(dir, "beta").Doc);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void NegativeAHomeThatLostItsDatabaseIsNotGivenANewOneByAWrite()
    {
        var authority = Flipped(Dir("alpha-home"));
        _open.ForEach(static d => d.Dispose());
        _open.Clear();
        var database = StateDbBackups.DatabasePath(Home);
        File.Delete(database);

        var result = authority.Write(TeamFile(Dir("alpha-home"), "beta"), Utf8(Body));

        Assert.False(result.Ok);
        Assert.Contains("store-missing", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public void WritingTheDocumentTheRowAlreadyHoldsChangesNothing()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");
        _ = authority.Write(file, Encoding.UTF8.GetBytes(Newer));
        var before = Row(dir, "beta");

        var again = authority.Write(file, Encoding.UTF8.GetBytes(Newer));

        Assert.True(again.Ok);
        Assert.False(again.Changed);
        Assert.Equal(before, Row(dir, "beta"));
    }

    [Fact]
    public void AWriteIntoADirectoryNoCutoverNamedIsStored()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var other = Dir("other-home");
        var file = TeamFile(other, "gamma");

        var result = authority.Write(file, Encoding.UTF8.GetBytes(Body));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(Body, Row(other, "gamma").Doc);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("{\"team_id\":\"t1\"}")]
    [InlineData("not json")]
    public void ADocumentThatIsNotATeamIsRefusedAndWritesNothing(string text)
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");

        var result = authority.Write(file, Encoding.UTF8.GetBytes(text));

        Assert.False(result.Ok);
        Assert.Equal("invalid-team", result.Code);
        Assert.Null(Row(dir, "beta").Doc);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void APathWithNoUsableTeamKeyIsRefused()
    {
        var authority = Flipped(Dir("alpha-home"));

        var result = authority.Write("relative/beta.json", Encoding.UTF8.GetBytes(Body));

        Assert.False(result.Ok);
        Assert.Equal("invalid-team", result.Code);
    }

    [Fact]
    public void AClearRemovesTheRowAndItsMembers()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");
        _ = authority.Write(file, Encoding.UTF8.GetBytes(Newer));

        var result = authority.Clear(file);

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Changed);
        Assert.Null(Row(dir, "beta").Doc);
        Assert.Equal(0, Members(dir, "beta"));
    }

    [Fact]
    public void AClearOfATeamThatIsNotThereChangesNothingAndLeavesOtherTeamsAlone()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var kept = TeamFile(dir, "beta");
        _ = authority.Write(kept, Encoding.UTF8.GetBytes(Newer));

        var result = authority.Clear(TeamFile(dir, "missing"));

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.Equal(Newer, Row(dir, "beta").Doc);
    }

    [Fact]
    public void AReadReturnsTheDatabaseDocumentAndWritesNoFile()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");
        _ = authority.Write(file, Encoding.UTF8.GetBytes(Newer));

        Assert.Equal(Newer, Encoding.UTF8.GetString(authority.Read(file)!.Value.Span));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AMissedTeamIsTakenFromItsFileOnceAndDoesNotComeBackAfterAClear()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "handmade");
        File.WriteAllText(file, Body);

        // a by-path read that misses takes the file (atp-1518: every team write builds on it, so absent would drop its members)
        Assert.Equal(Body, Encoding.UTF8.GetString(authority.Read(file)!.Value.Span));
        Assert.False(File.Exists(file + ".stamp"));
        using (var reader = SqliteConnection.Open(StateDbBackups.DatabasePath(Home), SqliteOpenMode.ReadOnly, 1000))
        {
            Assert.Single(StateDbRows.Run(reader, "SELECT 1 FROM meta WHERE key = ?1", [StateDbStoreImport.TakenKey(file)]));
        }

        Assert.True(authority.Clear(file).Changed);

        Assert.Null(authority.Read(file));
    }

    [Fact]
    public void AReadOfATeamTheDatabaseDoesNotHoldIsNullAndCreatesNoFile()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "missing");

        Assert.Null(authority.Read(file));
        Assert.False(File.Exists(file));
    }

    private const string Third = "{\"team_id\":\"t1\",\"orchestrator\":{\"name\":\"orch\"},\"members\":[{\"name\":\"orch\",\"role\":\"orchestrator\"},{\"name\":\"w2\",\"role\":\"reviewer\"}]}\n";

    private static string DigestOf(string text) => ContentDigest.OfBytes(Encoding.UTF8.GetBytes(text));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void TwoWritersThatReadTheSameDocumentCannotBothWriteAndTheLaterOneGetsADigestMismatchAndChangesNothing()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");
        Assert.True(authority.Write(file, Utf8(Body), "absent").Ok);
        var read = DigestOf(Body);

        var first = authority.Write(file, Utf8(Newer), read);
        var second = authority.Write(file, Utf8(Third), read);

        Assert.True(first.Ok, first.Message);
        Assert.False(second.Ok);
        Assert.Equal("digest-mismatch", second.Code);
        Assert.False(second.Changed);
        Assert.Equal(Newer, Row(dir, "beta").Doc);
        Assert.True(authority.Write(file, Utf8(Third), DigestOf(Newer)).Ok);
    }

    [Fact]
    public void ACreatorThatExpectedTheTeamToBeAbsentIsRefusedOnceTheTeamExists()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");
        Assert.True(authority.Write(file, Utf8(Body), "absent").Ok);

        var again = authority.Write(file, Utf8(Newer), "absent");

        Assert.Equal("digest-mismatch", again.Code);
        Assert.Equal(Body, Row(dir, "beta").Doc);
    }

    [Fact]
    public void AClearAgainstAStaleDigestIsRefusedAndKeepsTheTeam()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var file = TeamFile(dir, "beta");
        Assert.True(authority.Write(file, Utf8(Body), "absent").Ok);
        Assert.True(authority.Write(file, Utf8(Newer), DigestOf(Body)).Ok);

        var stale = authority.Clear(file, DigestOf(Body));

        Assert.Equal("digest-mismatch", stale.Code);
        Assert.Equal(Newer, Row(dir, "beta").Doc);
    }

    [Fact]
    public void AMarkerWhoseDatabaseFileIsMissingIsRefusedLikeAMissingStore()
    {
        var home = Path.Join(_root, "gone");
        StateDbAuthority.Write(home, new AuthorityMarker("id"));
        var authority = new StateDbTeamAuthority(home, new StateDbDirect(home, DirectWriteOptions.Default));
        var dir = Dir("alpha-home");

        var result = authority.Write(TeamFile(dir, "beta"), Utf8(Body));

        Assert.True(authority.IsDatabase);
        Assert.False(result.Ok);
        Assert.NotEqual("not-database", result.Code);
        Assert.False(File.Exists(TeamFile(dir, "beta")));
    }

    [Fact]
    public void AClearNeverDeletesATeamFileThatHasNoStampBesideIt()
    {
        var dir = Dir("alpha-home");
        var authority = Flipped(dir);
        var hand = TeamFile(dir, "handmade");
        File.WriteAllText(hand, Body);

        var result = authority.Clear(hand);

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.True(File.Exists(hand));
    }

    [Fact]
    public void AHookSizedAuthorityGivesUpOnABusyDatabaseInsideTheHooksBudget()
    {
        var dir = Dir("alpha-home");
        _ = Flipped(dir);
        var authority = new StateDbTeamAuthority(Home, new StateDbDirect(Home, DirectWriteOptions.Hook));
        using var blocker = SqliteConnection.Open(StateDbBackups.DatabasePath(Home), SqliteOpenMode.ReadWrite, 100);
        blocker.Exec("BEGIN IMMEDIATE");

        var watch = Stopwatch.StartNew();
        var result = authority.Write(TeamFile(dir, "beta"), Utf8(Newer));
        watch.Stop();
        blocker.Exec("ROLLBACK");

        Assert.False(result.Ok);
        Assert.Contains("database busy", result.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"took {watch.Elapsed.TotalMilliseconds} ms");
        Assert.Null(Row(dir, "beta").Doc);
    }

    [Fact]
    public void AWriteToAnUnreadableDatabaseIsRefusedAndWritesNoFile()
    {
        var home = Path.Join(_root, "unreadable");
        var database = StateDbBackups.DatabasePath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.WriteAllText(database, "this is not a database");
        StateDbAuthority.Write(home, new AuthorityMarker("id"));
        var authority = new StateDbTeamAuthority(home, new StateDbDirect(home, DirectWriteOptions.Default));
        var dir = Dir("alpha-home");

        var result = authority.Write(TeamFile(dir, "beta"), Utf8(Body));

        Assert.True(authority.IsDatabase);
        Assert.False(result.Ok);
        Assert.NotEqual("not-database", result.Code);
        Assert.False(File.Exists(TeamFile(dir, "beta")));
    }
}
