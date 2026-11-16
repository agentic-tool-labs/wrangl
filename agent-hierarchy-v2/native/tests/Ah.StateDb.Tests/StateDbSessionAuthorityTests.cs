using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;

namespace Ah.StateDb.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class StateDbSessionAuthorityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ahsessauth").FullName;
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var d in _open)
        {
            d.Dispose();
        }

        Directory.Delete(_root, recursive: true);
    }

    private string Home => Path.Join(_root, "ah");

    private string Teams => StateDbTeamKey.RealPath(Directory.CreateDirectory(Path.Join(_root, "proj", "teams")).Parent!.FullName);

    private string FileOf(string session) => Path.Join(Home, "registrations", session + ".json");

    private static string Doc(string session, string? teamFile, string cwd = "/work", string at = "2026-03-01T12:00:00.000Z") =>
        $"{{\"v\":1,\"session_id\":\"{session}\",\"address\":\"imp@alpha\",\"addresses\":[\"imp@alpha\"],\"boxes\":{{}},\"team_file\":{(teamFile is null ? "null" : "\"" + teamFile + "\"")},\"harness\":\"claude\",\"pid\":{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)},\"cwd\":\"{cwd}\",\"bound\":false,\"at\":\"{at}\",\"ah_version\":\"1.2.0\"}}\n";

    private static SessionDecision Write(string doc) => new(SessionVerdict.Write, Encoding.UTF8.GetBytes(doc));

    /// <summary>A database that holds one registration.</summary>
    private ISessionAuthority Flipped()
    {
        var db = StateDbModule.Open(Home, mayCreate: true);
        _open.Add(db);
        var authority = StateDbModule.OpenSessionAuthority(Home);
        Assert.True(authority.Register(FileOf("seed"), "seed", _ => Write(Doc("seed", Path.Join(Teams, "teams", "alpha.json")))).Ok);
        return authority;
    }

    private object?[][] Query(string sql)
    {
        var reader = StateDbModule.OpenReader(Home).Reader!;
        _open.Add(reader);
        return [.. reader.Query(sql)];
    }

    /// <summary>Puts a document for <paramref name="session"/> in <c>sessions_unplaced</c>, as an upgrade can leave one beside its placed row.</summary>
    private void PlantUnplaced(string session, string doc)
    {
        var connection = SqliteConnection.Open(Path.Join(Home, "state", "ah.db"), SqliteOpenMode.ReadWrite, 50);
        _open.Add(connection);
        using var insert = connection.Prepare("INSERT INTO sessions_unplaced(session_id, reason, started_at, last_seen_at, doc) VALUES (?1, 'old', 1, 1, ?2)");
        insert.Bind(1, session);
        insert.Bind(2, doc);
        _ = insert.Step();
    }

    [Fact]
    public void OneIdInBothTablesListsOnceWithThePlacedDocument()
    {
        var authority = Flipped();
        var placed = Doc("s2", Path.Join(Teams, "teams", "alpha.json"));
        Assert.True(authority.Register(FileOf("s2"), "s2", _ => Write(placed)).Ok);
        PlantUnplaced("s2", Doc("s2", null, cwd: "/stale"));

        var listed = authority.ListStored().Where(static row => row.Key == "s2").ToList();

        Assert.Equal(placed, Encoding.UTF8.GetString(Assert.Single(listed).Value.Span));
        Assert.Equal(placed, Encoding.UTF8.GetString(authority.ReadStored("s2")!.Value.Span));
    }

    [Fact]
    public void NegativeAnUnplacedIdWithNoLivePlacedRowStillLists()
    {
        var authority = Flipped();
        var unplaced = Doc("s3", null);
        PlantUnplaced("s3", unplaced);

        Assert.Equal(unplaced, Encoding.UTF8.GetString(authority.ListStored().Single(static row => row.Key == "s3").Value.Span));
    }

    [Fact]
    public void ASightingLeavesTheTokenAndAChangedDocumentOrEndMovesIt()
    {
        var authority = Flipped();
        Assert.True(authority.Register(FileOf("s2"), "s2", _ => Write(Doc("s2", Path.Join(Teams, "teams", "alpha.json")))).Ok);
        var connection = SqliteConnection.Open(Path.Join(Home, "state", "ah.db"), SqliteOpenMode.ReadWrite, 50);
        _open.Add(connection);

        var start = authority.ChangeToken();
        connection.Exec("UPDATE sessions SET last_seen_at = 99 WHERE session_id = 's2'");
        Assert.Equal(start, authority.ChangeToken());

        connection.Exec("UPDATE sessions SET doc = doc || ' ' WHERE session_id = 's2'");
        var afterDoc = authority.ChangeToken();
        Assert.True(afterDoc > start);

        connection.Exec("UPDATE sessions SET ended_at = 99 WHERE session_id = 's2'");
        Assert.True(authority.ChangeToken() > afterDoc);
    }

    [Fact]
    public void ARegisterStoresTheRowWithItsDocumentAndHandsBackTheBytesTheFileHolds()
    {
        var authority = Flipped();
        var teamFile = Path.Join(Teams, "teams", "alpha.json");
        var doc = Doc("s2", teamFile);

        var result = authority.Register(FileOf("s2"), "s2", _ => Write(doc));

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Changed);
        Assert.Equal(doc, Encoding.UTF8.GetString(result.Exported.Span));
        var row = Query("SELECT doc, hier_dir, team, member, ended_at FROM sessions WHERE session_id = 's2'").Single();
        Assert.Equal(doc, row[0]);
        Assert.Equal(StateDbTeamKey.Of(teamFile)!.Value.HierDir, row[1]);
        Assert.Equal("alpha", row[2]);
        Assert.Equal("imp", row[3]);
        Assert.Null(row[4]);
        Assert.Empty(Query("SELECT 1 FROM sessions_unplaced WHERE session_id = 's2'"));
    }

    [Fact]
    public void ARegistrationWithNoTeamFileIsStoredInSessionsUnplacedWithItsDocumentAndNotInSessions()
    {
        var authority = Flipped();
        var doc = Doc("s3", null);

        var result = authority.Register(FileOf("s3"), "s3", _ => Write(doc));

        Assert.True(result.Ok, result.Message);
        var row = Query("SELECT doc, reason FROM sessions_unplaced WHERE session_id = 's3'").Single();
        Assert.Equal(doc, row[0]);
        Assert.Equal("no team file", row[1]);
        Assert.Empty(Query("SELECT 1 FROM sessions WHERE session_id = 's3'"));
    }

    [Fact]
    public void AnUnplacedRegistrationThatGainsATeamFileMovesToSessionsInTheSameWrite()
    {
        var authority = Flipped();
        _ = authority.Register(FileOf("s3"), "s3", _ => Write(Doc("s3", null)));

        _ = authority.Register(FileOf("s3"), "s3", _ => Write(Doc("s3", Path.Join(Teams, "teams", "alpha.json"), cwd: "/moved")));

        Assert.Empty(Query("SELECT 1 FROM sessions_unplaced WHERE session_id = 's3'"));
        Assert.Single(Query("SELECT 1 FROM sessions WHERE session_id = 's3'"));
    }

    [Fact]
    public void TheDecisionSeesTheLiveDocumentAndNothingAfterTheSessionEnded()
    {
        var authority = Flipped();
        var doc = Doc("s2", null);
        _ = authority.Register(FileOf("s2"), "s2", _ => Write(doc));
        string? seen = null;
        _ = authority.Register(FileOf("s2"), "s2", live => { seen = live is { } d ? Encoding.UTF8.GetString(d.Span) : null; return new SessionDecision(SessionVerdict.Unchanged); });
        Assert.Equal(doc, seen);

        _ = authority.Unregister(FileOf("s2"), "s2");
        var after = "unset";
        _ = authority.Register(FileOf("s2"), "s2", live => { after = live is null ? "none" : "some"; return new SessionDecision(SessionVerdict.Unchanged); });

        Assert.Equal("none", after);
    }

    [Fact]
    public void AnUnchangedVerdictWritesNothingAndARefusalWritesNothingAndReturnsItsReason()
    {
        var authority = Flipped();
        var doc = Doc("s2", null);
        _ = authority.Register(FileOf("s2"), "s2", _ => Write(doc));

        var unchanged = authority.Register(FileOf("s2"), "s2", _ => new SessionDecision(SessionVerdict.Unchanged));
        var refused = authority.Register(FileOf("s2"), "s2", _ => new SessionDecision(SessionVerdict.Refused, Why: "held"));

        Assert.True(unchanged.Ok);
        Assert.False(unchanged.Changed);
        Assert.True(unchanged.Exported.IsEmpty);
        Assert.False(refused.Ok);
        Assert.Equal("refused", refused.Code);
        Assert.Equal("held", refused.Message);
        Assert.Equal(doc, Query("SELECT doc FROM sessions_unplaced WHERE session_id = 's2'").Single()[0]);
    }

    [Fact]
    public void AnUnregisterEndsTheRowKeepsItAndReportsChangeOnlyTheFirstTime()
    {
        var authority = Flipped();
        _ = authority.Register(FileOf("s2"), "s2", _ => Write(Doc("s2", Path.Join(Teams, "teams", "alpha.json"))));

        var first = authority.Unregister(FileOf("s2"), "s2");
        var second = authority.Unregister(FileOf("s2"), "s2");

        Assert.True(first.Ok);
        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.NotNull(Query("SELECT ended_at FROM sessions WHERE session_id = 's2'").Single()[0]);
        Assert.NotNull(Query("SELECT doc FROM sessions WHERE session_id = 's2'").Single()[0]);
    }

    [Fact]
    public void AGoneProcessEndsOnlyItsOwnRowWhichIsPrunedOnceOldEnough()
    {
        var authority = Flipped();
        var pid = Environment.ProcessId;
        var doc = Doc("s2", Path.Join(Teams, "teams", "alpha.json")).Replace("\"cwd\"", "\"pid_start\":\"A\",\"cwd\"", StringComparison.Ordinal);
        _ = authority.Register(FileOf("s2"), "s2", _ => Write(doc));

        Assert.False(authority.EndGone("s2", pid, "B"));
        Assert.Contains(authority.ListStored(), r => r.Key == "s2");
        Assert.True(authority.EndGone("s2", pid, "A"));
        Assert.DoesNotContain(authority.ListStored(), r => r.Key == "s2");
        Assert.Equal(["s2"], authority.ListEnded().Select(static r => r.Key));

        var ended = (long)Query("SELECT ended_at FROM sessions WHERE session_id = 's2'").Single()[0]!;
        Assert.Equal(0, authority.PruneEnded(ended, countOnly: true));
        Assert.Equal(1, authority.PruneEnded(ended + 1, countOnly: true));
        Assert.Equal(1, authority.PruneEnded(ended + 1, countOnly: false));
        Assert.Empty(Query("SELECT 1 FROM sessions WHERE session_id = 's2'"));
        Assert.Single(Query("SELECT 1 FROM sessions WHERE session_id = 'seed'"));
    }

    [Fact]
    public void AReRegisterAfterAnUnregisterIsLiveAgain()
    {
        var authority = Flipped();
        var doc = Doc("s2", Path.Join(Teams, "teams", "alpha.json"));
        _ = authority.Register(FileOf("s2"), "s2", _ => Write(doc));
        _ = authority.Unregister(FileOf("s2"), "s2");

        _ = authority.Register(FileOf("s2"), "s2", _ => Write(doc));

        Assert.Null(Query("SELECT ended_at FROM sessions WHERE session_id = 's2'").Single()[0]);
    }

    [Fact]
    public void AFileThatIsNotTheSessionsOwnInThisHomeIsRefusedAndNothingIsTouched()
    {
        var authority = Flipped();
        var decoy = Path.Join(_root, "decoy", "registrations");
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Join(decoy, "s2.json"), "decoy");

        var elsewhere = authority.Register(Path.Join(decoy, "s2.json"), "s2", _ => Write(Doc("s2", null)));
        var wrongName = authority.Register(FileOf("other"), "s2", _ => Write(Doc("s2", null)));
        var traversal = authority.Register(Path.Join(Home, "registrations", "..", "x", "s2.json"), "s2", _ => Write(Doc("s2", null)));
        var unregister = authority.Unregister(Path.Join(decoy, "s2.json"), "s2");

        Assert.All(new[] { elsewhere, wrongName, traversal, unregister }, r => Assert.Equal("outside-home", r.Code));
        Assert.Equal("decoy", File.ReadAllText(Path.Join(decoy, "s2.json")));
        Assert.Empty(Query("SELECT 1 FROM sessions WHERE session_id = 's2' UNION SELECT 1 FROM sessions_unplaced WHERE session_id = 's2'"));
    }

    [Fact]
    public void AHomeThatWasNeverFlippedStillRegistersInTheDatabaseAndWritesNoFile()
    {
        var authority = StateDbModule.OpenSessionAuthority(Home);
        Assert.False(authority.IsDatabase);

        var result = authority.Register(FileOf("s2"), "s2", _ => Write(Doc("s2", null)));

        Assert.True(result.Ok, result.Message);
        Assert.True(authority.IsDatabase);
        Assert.Single(Query("SELECT 1 FROM sessions_unplaced WHERE session_id = 's2'"));
        Assert.False(File.Exists(FileOf("s2")));
    }

    [Fact]
    public void AnUnreadableDatabaseFailsTheRegisterInsteadOfWritingAFile()
    {
        var db = StateDbModule.Open(Home, mayCreate: true);
        db.Dispose();
        File.WriteAllText(StateDbBackups.DatabasePath(Home), "not a database");
        var authority = StateDbModule.OpenSessionAuthority(Home);

        var result = authority.Register(FileOf("s2"), "s2", _ => Write(Doc("s2", null)));

        Assert.True(authority.IsDatabase);
        Assert.False(result.Ok);
        Assert.NotEqual("not-database", result.Code);
        Assert.False(File.Exists(FileOf("s2")));
    }
}
