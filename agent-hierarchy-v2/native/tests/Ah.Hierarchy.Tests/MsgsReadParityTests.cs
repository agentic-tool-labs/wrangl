using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// The message readers over the database, on one fixture of pools. The run starts after the sealed files are deleted, so a sealed
/// message can only be answered from its stored body.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgsReadParityTests : IDisposable
{
    private const string Worker = "worker";
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var o in _open)
        {
            o.Dispose();
        }
    }

    private sealed record Spec(string Pool, string Id, string Type, string To, string Body, bool Sealed, bool Archived = false, bool Closed = false, string Slug = "s");

    private static string FileText(Spec s) =>
        $"---\nid: {s.Id}\ntype: {s.Type}\nto: {s.To}\nfrom: orchestrator\nslug: {s.Slug}\nteam: t\ncreated: 2026-05-28T00:00:00Z\n" + (s.Closed ? "closed: true\n" : "") + $"---\n{s.Body}";

    private static string Name(Spec s) => $"{s.Id}--{s.To}--{s.Slug}--{s.Type}.md";

    private static string PathOf(string root, Spec s) => Path.Combine(root, s.Pool, "msgs", s.Archived ? "archive" : "", Name(s));

    private static string Report => "Done: the work is finished.\n";

    private static readonly string BigReport = string.Concat(Enumerable.Repeat("Done: a long account of the work.\n", 200));

    private static List<Spec> Fixture() =>
    [
        new("a", "20260101-000001-r001", "request", Worker, "", false),
        new("a", "20260101-000002-r002", "request", Worker, "", false),
        new("a", "20260101-000002-r002", "response", Worker, "", true),
        new("a", "20260101-000003-r003", "request", Worker, "", false),
        new("a", "20260101-000003-r003", "response", Worker, Report, true),
        new("a", "20260101-000004-r004", "request", "orchestrator", "", false),
        new("a", "20260101-000004-r004", "response", "orchestrator", "", true),
        new("a", "20260101-000005-r005", "request", Worker, "", false),
        new("a", "20260101-000005-r005", "response", Worker, "", false),
        new("a", "20260101-000006-r006", "request", Worker, "", false),
        new("a", "20260101-000006-r006", "response", Worker, Report, false),
        new("a", "20260101-000007-r007", "request", Worker, "", false),
        new("a", "20260101-000007-r007", "response", Worker, BigReport, true),
        new("a", "20260101-000008-r008", "request", Worker, "", false),
        new("a", "20260101-000008-r008", "response", Worker, "", true, Closed: true),
        new("a", "20260101-000009-n001", "note", Worker, "a note\n", true),
        new("a", "20260101-000010-o001", "response", Worker, Report, true),
        new("a", "20260101-000011-a001", "request", Worker, "", false, Archived: true),
        new("a", "20260101-000011-a001", "response", Worker, Report, true, Archived: true),
        new("b", "20260101-000012-r010", "request", Worker, "", false),
        new("a", "20260101-000013-rx01", "request", Worker, "", false),
        new("b", "20260101-000013-rx01", "response", Worker, Report, true),
        // a custom role whose name has a digit
        new("a", "20260101-000014-q001", "request", "qa2", "", false),
    ];

    private sealed class Rig
    {
        internal required string Home { get; init; }

        internal required string Root { get; init; }

        internal required HierarchyServices Services { get; init; }
    }

    private static Rig Build(UnitSandbox sandbox, MsgsRoute? given = null, long nowMs = 1_780_000_000_000, bool dbTeams = false)
    {
        var variables = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["TZ"] = "UTC" };
        var cwd = sandbox.Root;
        var env = new FakeEnvironment(variables, new PluginInfo(HierarchyTableFns.AhRoot, "wrangl", "0.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(cwd, 4242));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, teamAuthority: dbTeams ? StateDbModule.OpenTeamAuthority(sandbox.Home) : null);
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(nowMs));
        var route = given ?? (new MsgsRoute(StateDbModule.CreateMsgSource(sandbox.Home), StateDbModule.CreateModeReader(sandbox.Home), () => StateDbModule.DomainFlipped(sandbox.Home, "msgs")));
        var services = HierarchyModule.Create(new HierarchyEnvironment(io.Files, env, clock, new SequenceRandom(), io.Processes, config, roster, null, new ProcessFacts(cwd, 4242), null, null, null, null, route));
        return new Rig { Home = sandbox.Home, Root = sandbox.Root, Services = services };
    }

    private void Import(Rig rig, IReadOnlyList<Spec> specs)
    {
        _open.Add(StateDbModule.Open(rig.Home, mayCreate: true));
        var core = rig.Services.Core;
        var rows = specs.Select(s =>
        {
            var path = PathOf(rig.Root, s);
            var meta = core.ParseMsgFilename(Name(s))!;
            var created = Ah.Core.Text.MessageFront.ParseFrontmatter(File.ReadAllText(path))!.Value.Fields["created"].AsString();
            return new MsgFileRow(
                Path.Combine(rig.Root, s.Pool), Name(s), meta.Id, meta.Type, meta.To, meta.Slug, "orchestrator", null, null, "t", null, created,
                DateTimeOffset.Parse(created, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), s.Archived,
                s.Sealed ? MsgRows.Sealed : MsgRows.Draft, File.ReadAllBytes(path), path, // a draft holds its file's bytes as the draft import stores them
                Closed: s.Closed);
        }).ToList();
        var written = StateDbModule.OpenDirect(rig.Home).Write(tx =>
        {
            foreach (var row in rows)
            {
                tx.Execute(MsgRows.InsertMessageSql, MsgRows.MessageArgs(row));
            }

            long Pk(MsgFileRow r) => (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(r))[0][0]!;
            foreach (var row in rows.Where(r => r.Bytes is not null))
            {
                tx.Execute(MsgRows.InsertBodySql, MsgRows.BodyArgs(Pk(row), row, 1_780_000_000_000));
            }

            foreach (var pair in MsgRows.Pair(rows))
            {
                tx.Execute(MsgRows.InsertExchangeSql, MsgRows.ExchangeArgs(pair, Pk(pair.Request), pair.Response is null ? null : Pk(pair.Response), pair.Response?.Bytes is null ? null : 1_780_000_000_000));
            }

            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.msgs', 'db')");
            return 0;
        });
        Assert.True(written.Ok, written.Message);
    }

    private static void Write(string root, IEnumerable<Spec> specs)
    {
        foreach (var s in specs)
        {
            var path = PathOf(root, s);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, FileText(s), new UTF8Encoding(false));
        }
    }

    private (Rig Rig, IReadOnlyList<Spec> Specs) Run(Action<List<Spec>>? tweak = null)
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        tweak?.Invoke(specs);
        Write(sandbox.Root, specs);
        Import(Build(sandbox), specs);
        foreach (var s in specs.Where(s => s.Sealed))
        {
            File.Delete(PathOf(sandbox.Root, s));
        }

        return (Build(sandbox), specs);
    }

    /// <summary>ah-msgs-db-only §3.2.1: an id token resolves through the store's one (id, type) query to the message's key, in any pool; an id two pools share does not.</summary>
    [Fact]
    public void AnIdTokenResolvesThroughTheStoreToTheKeyOfItsMessage()
    {
        var (rig, specs) = Run();
        var tokens = specs.Where(static s => s.Type is "request" or "response" or "note").ToList();

        foreach (var s in tokens)
        {
            var unique = tokens.Count(o => o.Id == s.Id && o.Type == s.Type) == 1;
            var key = rig.Services.Core.ExtractMsgToken($"[hierarchy-msg {s.Id} {s.Type}]");
            Assert.Equal(unique ? PathOf(rig.Root, s) : null, key);
        }
    }

    /// <summary>ah-msgs-fast S6: msgs/index from the exchange rows gives the same {slug, open} list as the files, after the live window too.</summary>
    [Fact]
    public void TheIndexFromTheExchangeRowsListsOpenAndClosedAfterTheLiveWindow()
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        Write(sandbox.Root, specs);
        var args = new JsonObject { ["dir"] = Path.Combine(sandbox.Root, "a") };
        Import(Build(sandbox), specs);
        var later = Build(sandbox, nowMs: 1_780_000_000_000 + (11 * 60 * 1000) + 1);

        var index = JsonText.Stringify(((HierLib)later.Services.Messages).MsgsIndexOp(args), 0);
        Assert.Contains("\"open\":false", index, StringComparison.Ordinal);
        Assert.Contains("\"open\":true", index, StringComparison.Ordinal);
    }

    private static string OpenText(JsonArray exchanges) =>
        string.Join("\n", exchanges.Where(static e => e.AsObject()["open"].IsTruthy).Select(static e => JsonText.Stringify(e, 0)));

    /// <summary>ah-msgs-fast S2: the live scope gives every open exchange the full list gives, the same JSON, and leaves closed history out.</summary>
    [Fact]
    public void TheLiveScopeHasTheSameOpenExchangesAsTheFullListAndLeavesOldClosedOnesOut()
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        specs.Add(new("a", "20260101-000015-c001", "request", "sub-orchestrator", "", false));
        specs.Add(new("a", "20260101-000015-c001", "response", "sub-orchestrator", Report, true));
        Write(sandbox.Root, specs);
        Import(Build(sandbox), specs);
        var a = Path.Combine(sandbox.Root, "a");
        var b = Path.Combine(sandbox.Root, "b");

        // answered 11 minutes and a millisecond ago: only the live flag keeps an exchange in the live scope
        var later = Build(sandbox,nowMs: 1_780_000_000_000 + (11 * 60 * 1000) + 1);
        foreach (var pools in new[] { new[] { a }, new[] { b }, new[] { a, b }, new[] { b, a } })
        {
            var full = later.Services.Core.ListExchanges(pools);
            var live = later.Services.Core.ListExchanges(pools, live: true);
            Assert.Equal(OpenText(full), OpenText(live));
            Assert.True(pools.Length == 1 && pools[0] == b ? live.Count == full.Count : live.Count < full.Count);
        }

        Assert.Equal(
            JsonText.Stringify(later.Services.Messages.OpenExchanges(a, "t", later.Services.Core.ListExchanges(a)), 0),
            JsonText.Stringify(later.Services.Messages.OpenExchanges(a, "t"), 0));

        // answered within the window: an authored exchange stays in the live scope, so the dispatch list can still show it as reported
        var soon = Build(sandbox,nowMs: 1_780_000_000_000 + (10 * 60 * 1000));
        Assert.Equal(JsonText.Stringify(soon.Services.Core.ListExchanges([a, b]), 0), JsonText.Stringify(soon.Services.Core.ListExchanges([a, b], live: true), 0));
    }

    /// <summary>ah-msgs-fast S3: the run list adds every exchange that shares a slug with the run; the slug list holds exactly one slug's exchanges.</summary>
    [Fact]
    public void TheRunAndSlugListsAddEveryExchangeOfTheirSlugsAndNothingElse()
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        Write(sandbox.Root, specs);
        Import(Build(sandbox), specs);
        var pools = new[] { Path.Combine(sandbox.Root, "a"), Path.Combine(sandbox.Root, "b") };
        var later = Build(sandbox,nowMs: 1_780_000_000_000 + (11 * 60 * 1000) + 1);
        var core = (HierarchyCore)((HierLib)later.Services.Messages).Core;
        var created = DateTimeOffset.Parse("2026-05-28T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var full = JsonText.Stringify(core.ListExchanges(pools), 0);

        // every fixture request has slug "s", team "t" and one created time: a run that started by then reaches all of them
        Assert.Equal(full, JsonText.Stringify(core.ListRunExchanges(pools, "t", created), 0));
        Assert.Equal(full, JsonText.Stringify(core.ListSlugExchanges(pools, "s"), 0));

        // the nearest inputs that must add nothing: a run that starts later, another team's run, another slug
        var live = JsonText.Stringify(core.ListExchanges(pools, live: true), 0);
        Assert.NotEqual(full, live);
        Assert.Equal(live, JsonText.Stringify(core.ListRunExchanges(pools, "t", created + 1), 0));
        Assert.Equal(live, JsonText.Stringify(core.ListRunExchanges(pools, "u", created), 0));
        Assert.Equal("[]", JsonText.Stringify(core.ListSlugExchanges(pools, "other"), 0));
    }

    /// <summary>b459 step 16: 9 sessions' pool lists over 11 pools all hit; a new message shows in exactly the lists that hold its pool; the 17th list evicts the least recently used one only.</summary>
    [Fact]
    public void NinePoolListsAllHitANewMessageShowsWhereItsPoolIsAndTheLeastRecentlyUsedListIsEvicted()
    {
        var sandbox = UnitSandbox.Create();
        static string P(int i) => $"p{i:00}";
        var specs = Enumerable.Range(0, 11).Select(i => new Spec(P(i), $"20260101-0000{i:00}-q{i:000}", "request", Worker, "", false)).ToList();
        Write(sandbox.Root, specs);
        Import(Build(sandbox), specs);
        var core = (HierarchyCore)((HierLib)Build(sandbox, dbTeams: true).Services.Messages).Core;
        string[] List(int i) => [.. Enumerable.Range(i, 3).Select(p => Path.Combine(sandbox.Root, P(p)))];
        bool Shows(int i, string id) => core.ListExchanges(List(i)).Any(e => e.Member("request")?.Member("meta")?.Member("id")?.AsString() == id);
        long Rebuilt(Action read)
        {
            var before = core.ListingRebuilds;
            read();
            return core.ListingRebuilds - before;
        }

        Assert.Equal(9, Rebuilt(() => { for (var i = 0; i < 9; i++) { _ = core.ListExchanges(List(i)); } }));
        Assert.Equal(0, Rebuilt(() => { for (var i = 0; i < 9; i++) { _ = core.ListExchanges(List(i)); } }));

        // a new message in p05 shows in lists 3, 4 and 5 and in no other
        var added = new Spec(P(5), "20260101-000099-q099", "request", Worker, "", false);
        Write(sandbox.Root, [added]);
        Import(Build(sandbox), [added]);
        // the import writes the rows directly; a store write moves the msgs counter with them
        var bumped = StateDbModule.OpenDirect(sandbox.Home).Write(static tx =>
        {
            tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 1, 0) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1");
            return 0;
        });
        Assert.True(bumped.Ok, bumped.Message);
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(i is >= 3 and <= 5, Shows(i, added.Id));
        }

        // 8 more lists make 17: only list 0, the least recently used, is evicted
        Assert.Equal(8, Rebuilt(() => { for (var i = 0; i < 8; i++) { _ = core.ListExchanges([Path.Combine(sandbox.Root, P(i))]); } }));
        Assert.Equal(0, Rebuilt(() => _ = core.ListExchanges(List(1))));
        Assert.Equal(1, Rebuilt(() => _ = core.ListExchanges(List(0))));
    }

    /// <summary>b459 step 25a: a write in one pool re-reads that pool's rows alone; negative: a request and its response in two pools still pair.</summary>
    [Fact]
    public void AWriteInOnePoolReReadsOnlyThatPoolAndACrossPoolPairStillPairs()
    {
        var sandbox = UnitSandbox.Create();
        List<Spec> specs =
        [
            new("a", "20260101-000001-x001", "request", Worker, "", false),
            new("b", "20260101-000001-x001", "response", Worker, Report, true),
            new("c", "20260101-000002-c001", "request", Worker, "", false),
        ];
        // the import writes the rows directly; a store write moves the msgs counter after them
        void Bump() => Assert.True(StateDbModule.OpenDirect(sandbox.Home).Write(static tx =>
        {
            tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 1, 0) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1");
            return 0;
        }).Ok);
        Write(sandbox.Root, specs);
        Import(Build(sandbox), specs);
        Bump();
        var core = (HierarchyCore)((HierLib)Build(sandbox, dbTeams: true).Services.Messages).Core;
        string[] pools = [Path.Combine(sandbox.Root, "a"), Path.Combine(sandbox.Root, "b"), Path.Combine(sandbox.Root, "c")];
        _ = core.ListExchanges(pools);
        var reads = core.PoolRowReads;

        var added = new Spec("c", "20260101-000003-c002", "request", Worker, "", false);
        Write(sandbox.Root, [added]);
        Import(Build(sandbox), [added]);
        Bump();

        var relisted = core.ListingRelistedIds;
        var listing = core.ListExchanges(pools);
        Assert.Equal(reads + 1, core.PoolRowReads);
        Assert.Contains(listing, e => e.Member("id")?.AsString() == added.Id);
        Assert.True(listing.Single(e => e.Member("id")?.AsString() == "20260101-000001-x001").Member("response")!.Value.IsMap);

        // b459 step 28: the update listed the new id alone, and equals a fresh listing
        Assert.Equal(relisted + 1, core.ListingRelistedIds);
        var fresh = (HierarchyCore)((HierLib)Build(sandbox, dbTeams: true).Services.Messages).Core;
        Assert.Equal(JsonText.Stringify(fresh.ListExchanges(pools), 0), JsonText.Stringify(listing, 0));

        // b459 Amendment 3: a delete leaves no changed id; the pool's count moves and the next listing reads it again
        Assert.True(StateDbModule.OpenDirect(sandbox.Home).Write(tx =>
        {
            tx.Execute("DELETE FROM msg_bodies WHERE msg_pk IN (SELECT pk FROM msg_messages WHERE id = ?1)", added.Id);
            tx.Execute("DELETE FROM msg_messages WHERE id = ?1", added.Id);
            return 0;
        }).Ok);
        Bump();
        Assert.DoesNotContain(core.ListExchanges(pools), e => e.Member("id")?.AsString() == added.Id);
    }

    /// <summary>b459 step 25c: a response's no-report fact is held with its row; negative: a second fill of that response shows on the next listing.</summary>
    [Fact]
    public void ASecondFillOfAHeldNoReportResponseShowsOnTheNextListing()
    {
        var sandbox = UnitSandbox.Create();
        var request = new Spec("a", "20260101-000001-f001", "request", Worker, "", false);
        var empty = new Spec("a", "20260101-000001-f001", "response", Worker, "", true);
        Write(sandbox.Root, [request, empty]);
        Import(Build(sandbox), [request, empty]);
        void Write2(string sql, params object?[] args) => Assert.True(StateDbModule.OpenDirect(sandbox.Home).Write(tx =>
        {
            tx.Execute(sql, args);
            tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 1, 0) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1");
            return 0;
        }).Ok);
        Write2("SELECT 1");
        var core = (HierarchyCore)((HierLib)Build(sandbox, dbTeams: true).Services.Messages).Core;
        string[] pools = [Path.Combine(sandbox.Root, "a")];
        bool Open() => core.ListExchanges(pools).Single().Member("open")!.Value.AsBool();
        Assert.True(Open());
        Assert.True(Open());

        // the response is filled again with a report: its body and message row change, as a fill writes them
        var filled = Encoding.UTF8.GetBytes(FileText(empty with { Body = Report }));
        Write2(
            "UPDATE msg_bodies SET bytes = ?1, sha256 = ?2 WHERE msg_pk = (SELECT pk FROM msg_messages WHERE id = ?3 AND type = 'response')",
            filled,
            MsgRows.Sha256Hex(filled),
            empty.Id);
        Write2("UPDATE msg_messages SET body_rev = body_rev WHERE id = ?1 AND type = 'response'", empty.Id);
        Assert.False(Open());
    }

    /// <summary>b459 step 9: a send's newest request for a slug comes from the slug list, and is the one the full listing's newest of that slug is.</summary>
    [Fact]
    public void TheSlugListGivesTheNewestRequestOfASlugWithSeveralExchangesAsTheFullListingDoes()
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        Write(sandbox.Root, specs);
        Import(Build(sandbox), specs);
        var pool = Path.Combine(sandbox.Root, "a");
        var core = (HierarchyCore)((HierLib)Build(sandbox).Services.Messages).Core;
        static string? Newest(JsonArray listed) => listed.Select(static e => e.Member("request")?.Member("meta")?.Member("id")?.AsString()).OfType<string>().Max(StringComparer.Ordinal);

        var full = core.ListExchanges([pool]).Where(static e => e.Member("slug") is { IsText: true } s && s.AsString() == "s").ToList();
        Assert.True(full.Count > 1);
        Assert.Equal(Newest([.. full]), Newest(core.ListSlugExchanges([pool], "s")));
        Assert.Null(Newest(core.ListSlugExchanges([pool], "other")));
    }

    /// <summary>delta-status R1: the db slug count is the number of exchanges the full listing pairs for the slug, an id in two pools once; an unknown slug counts 0.</summary>
    [Fact]
    public void TheDbSlugCountIsTheFullListingCountOfTheSlugAndZeroForAnUnknownSlug()
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        Write(sandbox.Root, specs);
        Import(Build(sandbox), specs);
        var pools = new[] { Path.Combine(sandbox.Root, "a"), Path.Combine(sandbox.Root, "b") };
        var db = Build(sandbox).Services.Core;
        var listed = db.ListExchanges(pools).Count(e => e.Member("slug") is { IsText: true } s && s.AsString() == "s");

        Assert.True(listed > 1);
        Assert.Equal(listed, db.CountSlugExchanges(pools, "s"));
        Assert.Equal(0, db.CountSlugExchanges(pools, "other"));
    }

    private static string StatusText(Rig rig, string dir, long nowMs)
    {
        var status = rig.Services.Status.ComputeStatus(dir, nowMs, dir, true);
        status.Remove("written_at");
        status.Remove("expires_at");
        return JsonText.Stringify(status, 0);
    }

    /// <summary>ah-msgs-fast S3: the status of a live pipeline run keeps its closed rounds, which the bounded database lists reach through the run's slugs.</summary>
    [Fact]
    public void TheStatusOfALiveRunKeepsItsClosedRoundsFromTheBoundedLists()
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        specs.Add(new("a", "20260101-000016-p001", "request", "orchestrator", "", false, Slug: HierarchyModule.PipelineAnchorSlug));
        Write(sandbox.Root, specs);
        var dir = Path.Combine(sandbox.Root, "a");
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        File.WriteAllText(Path.Combine(dir, "teams", "t.json"), "{\"team_id\":\"t-t\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId + ",\"session_id\":\"o\"},\"members\":[]}\n");

        // answered 11 minutes and a millisecond ago: the closed rounds are out of the live scope and reached only through the run's slugs
        const long later = 1_780_000_000_000 + (11 * 60 * 1000) + 1;
        Import(Build(sandbox), specs);
        var db = Build(sandbox, nowMs: later);

        Assert.True(db.Services.Core.ListExchanges([dir], live: true).Count < db.Services.Core.ListExchanges([dir]).Count);
        Assert.Contains("\"rounds\":", StatusText(db, dir, later), StringComparison.Ordinal);
    }

    private static bool OpenOf(Rig rig, string pool, string id) =>
        rig.Services.Core.ListExchanges(pool).First(e => e.AsObject()["id"].AsString() == id).AsObject()["open"].IsTruthy;

    [Fact]
    public void AResponseFilledInTheDatabaseHasLandedEvenWhenItsExportFileIsStillTheEmptyDraft()
    {
        var (rig, specs) = Run();
        var filled = specs.Single(s => s.Id == "20260101-000003-r003" && s.Type == "response");
        var path = PathOf(rig.Root, filled);
        File.WriteAllText(path, FileText(filled with { Body = "" }), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var now = (double)new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        Assert.True(rig.Services.Messages.ResponseLanded(path, now));
    }

    [Fact]
    public void AnAuthoredDraftInTheDatabaseHasLandedByItsStoredBodyWithNoFileAndNoQuietWait()
    {
        var (rig, specs) = Run();
        var draft = PathOf(rig.Root, specs.Single(s => s.Id == "20260101-000006-r006" && s.Type == "response"));
        File.Delete(draft);

        Assert.True(rig.Services.Messages.ResponseLanded(draft, 1));
    }

    [Fact]
    public void AResponseFilledInTheDatabaseIsAuthoredEvenWhenItsExportFileIsStillTheEmptyDraft()
    {
        var (rig, specs) = Run();
        var filled = specs.Single(s => s.Id == "20260101-000003-r003" && s.Type == "response");
        var path = PathOf(rig.Root, filled);
        File.WriteAllText(path, FileText(filled with { Body = "" }), new UTF8Encoding(false));

        Assert.True(rig.Services.Messages.ResponseAuthored(path));
    }

    /// <summary>The database rig with the export of one sealed message rewritten to <paramref name="stale"/>.</summary>
    private (Rig Db, string Path, Spec Spec) StaleExport(string id, string type, Func<Spec, string> stale, Action<List<Spec>>? tweak = null)
    {
        var (rig, specs) = Run(tweak);
        var spec = specs.Single(s => s.Id == id && s.Type == type);
        var path = PathOf(rig.Root, spec);
        File.WriteAllText(path, stale(spec), new UTF8Encoding(false));
        return (rig, path, spec);
    }

    /// <summary>The library's message read (<c>TryReadMsg</c>) behind the report status, the follow-up rules and the response token.</summary>
    [Fact]
    public void TheLibrarysMessageReadAnswersASealedResponseFromItsStoredBodyNotItsStaleExport()
    {
        var (db, path, spec) = StaleExport(
            "20260101-000003-r003",
            "response",
            s => FileText(s with { Body = "" }),
            specs => specs[specs.FindIndex(s => s.Id == "20260101-000003-r003" && s.Type == "response")] = specs.Single(s => s.Id == "20260101-000003-r003" && s.Type == "response") with { Body = "## [0] tldr\n- BLOCKED on the fixture\n" });
        var token = (JsonValue)$"[hierarchy-msg {path}]";

        Assert.Equal(("reported", true, true), (db.Services.Messages.ReportStatus(path, spec.Id), db.Services.Messages.ReportFollowUp(path) is { IsText: true }, db.Services.Messages.HasResponseToken(token, spec.Id)));
    }

    [Fact]
    public void FillPlansAgainstTheStoredResponseNotItsStaleExport()
    {
        var (db, path, spec) = StaleExport("20260101-000004-r004", "response", s => FileText(s).Replace("from: orchestrator", "from: intruder", StringComparison.Ordinal));
        var request = PathOf(db.Root, spec with { Type = "request", Body = "", Sealed = false });
        JsonObject Args() => new() { ["id"] = spec.Id, ["from"] = "orchestrator", ["reqPath"] = request, ["body"] = "\"Done: refilled.\"" };

        Assert.True(db.Services.Messages.FillPlan(Args()).Member("content") is { IsText: true });
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TheBytesAndTimeOfAStoredMessageAreItsStoredTextAndSealNotItsLaggingFile()
    {
        var (db, path, spec) = StaleExport("20260101-000003-r003", "response", s => FileText(s with { Body = "" }));
        var draft = PathOf(db.Root, spec with { Type = "request", Body = "", Sealed = false });
        var lagging = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, lagging);
        File.SetLastWriteTimeUtc(draft, lagging);

        Assert.Equal(Encoding.UTF8.GetBytes(FileText(spec)), db.Services.Core.MsgBytes(path));
        Assert.Equal(File.ReadAllBytes(draft), db.Services.Core.MsgBytes(draft));
        Assert.Equal(((double?)1_780_000_000_000d, (double?)1_780_000_000_000d), (db.Services.Core.MsgTimeMs(path), db.Services.Core.MsgTimeMs(draft)));
    }

    [Fact]
    public void AStoredMessageWhoseExportIsGoneStillExists()
    {
        var (rig, specs) = Run();
        var path = PathOf(rig.Root, specs.First(s => s.Sealed && !s.Archived));

        Assert.False(File.Exists(path));
        Assert.True(rig.Services.Core.MsgExists(path));
    }

    [Fact]
    public void NegativeAMessageWithNoRowAndNoFileDoesNotExist()
    {
        var (rig, _) = Run();
        var path = Path.Combine(rig.Root, "a", "msgs", "20260101-000099-zz99--worker--s--response.md");

        Assert.False(rig.Services.Core.MsgExists(path));
        Assert.Null(rig.Services.Core.MsgBytes(path));
    }

    [Fact]
    public void ADatabaseExchangeIsOpenOnlyByThePredicateOverItsFacts()
    {
        var (rig, _) = Run();
        var a = Path.Combine(rig.Root, "a");

        Assert.True(OpenOf(rig, a, "20260101-000001-r001"));
        Assert.True(OpenOf(rig, a, "20260101-000002-r002"));
        Assert.False(OpenOf(rig, a, "20260101-000003-r003"));
        Assert.False(OpenOf(rig, a, "20260101-000004-r004"));
        Assert.True(OpenOf(rig, a, "20260101-000005-r005"));
        Assert.False(OpenOf(rig, a, "20260101-000006-r006"));
        Assert.False(OpenOf(rig, a, "20260101-000007-r007"));
    }

    [Fact]
    public void AnArchivedPairIsListedByNeitherBackend()
    {
        var (rig, _) = Run();
        var a = Path.Combine(rig.Root, "a");

        Assert.DoesNotContain(rig.Services.Core.ListExchanges(a), e => e.AsObject()["id"].AsString() == "20260101-000011-a001");
        Assert.True(rig.Services.Messages.FindByIdAndType(a, "20260101-000011-a001", "request").IsNull);
    }

    [Fact]
    public void ListMessagesGivesTheArchiveOnlyWhenAskedAndOnlyTheKindsAsked()
    {
        var (rig, _) = Run();
        var a = Path.Combine(rig.Root, "a");
        static List<string> Ids(JsonArray list) => [.. list.Select(m => m.Member("meta")!.Value.Member("id")!.Value.AsString() + "/" + m.Member("meta")!.Value.Member("type")!.Value.AsString() + (m.Member("archived") is { IsTruthy: true } ? "@archive" : ""))];

        var withArchive = Ids(rig.Services.Messages.ListMessages(a, includeArchive: true, ["response"]));
        var live = Ids(rig.Services.Messages.ListMessages(a, includeArchive: false, ["response"]));
        var notes = Ids(rig.Services.Messages.ListMessages(a, includeArchive: true, ["note"]));

        Assert.Equal("20260101-000011-a001/response@archive", withArchive[^1]);
        Assert.DoesNotContain(live, i => i.Contains("a001", StringComparison.Ordinal));
        Assert.All(live, i => Assert.EndsWith("/response", i, StringComparison.Ordinal));
        Assert.Equal(["20260101-000009-n001/note"], notes);
        Assert.Contains("20260101-000014-q001/request", Ids(rig.Services.Messages.ListMessages(a, includeArchive: false, ["request"])));
        Assert.Equal("qa2", rig.Services.Core.ParseMsgFilename("20260101-000014-q001--qa2--s--request.md")?.To);
    }

    [Fact]
    public void ANoteAndAnOrphanResponseAreFoundByIdAndTypeButHaveNoExchange()
    {
        var (rig, _) = Run();
        var a = Path.Combine(rig.Root, "a");

        Assert.DoesNotContain(rig.Services.Core.ListExchanges(a), e => e.AsObject()["id"].AsString() is "20260101-000009-n001" or "20260101-000010-o001");
        Assert.False(rig.Services.Messages.FindByIdAndType(a, "20260101-000009-n001", "note").IsNull);
        Assert.False(rig.Services.Messages.FindByIdAndType(a, "20260101-000010-o001", "response").IsNull);
    }

    [Fact]
    public void ATreeWithNoMsgsDirectoryIsAPoolOnlyInTheStore()
    {
        var sandbox = UnitSandbox.Create();
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Root, "other")).FullName;
        _ = Directory.CreateDirectory(Path.Combine(tree, ".git"));
        var dir = Path.Combine(sandbox.Root, "a", ".claude", "ah2");
        static bool Listed(IReadOnlyList<string> pools) => pools.Any(static p => p.EndsWith("/other/.claude/ah2", StringComparison.Ordinal));

        using (var open = StateDbModule.Open(sandbox.Home, mayCreate: true))
        {
            Assert.True(StateDbModule.OpenDirect(sandbox.Home).Write(static tx =>
            {
                tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.msgs', 'db')");
                return true;
            }).Ok);
        }

        Assert.True(Listed(Build(sandbox).Services.Messages.PoolDirs(dir, [], [tree])));
    }

    [Fact]
    public void ADraftAndASealedMessageAreBothReadFromTheirStoredBodies()
    {
        var (rig, specs) = Run();
        var draft = specs.First(s => s is { Type: "response", Sealed: false, Id: "20260101-000006-r006" });
        var sealedMsg = specs.First(s => s is { Type: "response", Sealed: true, Id: "20260101-000003-r003" });
        File.Delete(PathOf(rig.Root, draft));

        Assert.Contains("Done:", rig.Services.Core.ReadMsgFile(PathOf(rig.Root, draft)).Member("text")!.Value.AsString());
        Assert.False(File.Exists(PathOf(rig.Root, sealedMsg)));
        Assert.Contains("Done:", rig.Services.Core.ReadMsgFile(PathOf(rig.Root, sealedMsg)).Member("text")!.Value.AsString());
    }

    [Fact]
    public void ADatabaseThatCannotBeReadThrowsAndDoesNotListFiles()
    {
        var sandbox = UnitSandbox.Create();
        var specs = Fixture();
        Write(sandbox.Root, specs);
        var rig = Build(sandbox, new MsgsRoute(new UnreadableSource(), new DbMode()));

        _ = Assert.Throws<StoreUnavailableException>(() => rig.Services.Core.ListExchanges(Path.Combine(sandbox.Root, "a")));
        _ = Assert.Throws<StoreUnavailableException>(() => rig.Services.Messages.FindByIdAndType(Path.Combine(sandbox.Root, "a"), "20260101-000001-r001", "request"));
    }

    [Fact]
    public void AReadThrowsOnAnUnreadableStoreInsideADecisionAndOutOfOne()
    {
        var sandbox = UnitSandbox.Create();
        var rig = Build(sandbox, new MsgsRoute(new UnreadableSource(), new DbMode()));
        var core = rig.Services.Core;
        var a = Path.Combine(sandbox.Root, "a");

        using (core.DecisionReads())
        {
            _ = Assert.Throws<StoreUnavailableException>(() => core.ListExchanges(a));
        }

        _ = Assert.Throws<StoreUnavailableException>(() => core.ListExchanges(a));
    }

    [Fact]
    public void NegativeAStoreNeverCreatedAnswersEmptyAndDoesNotThrow()
    {
        var sandbox = UnitSandbox.Create();
        var rig = Build(sandbox);

        Assert.False(File.Exists(Path.Combine(sandbox.Home, "state", "ah.db")));
        Assert.Empty(rig.Services.Core.ListExchanges(Path.Combine(sandbox.Root, "a")));
    }

    [Fact]
    public void NegativeTheModeRowSaysFilesAndTheMarkerSaysDbSoAStoreFailureStillThrows()
    {
        var sandbox = UnitSandbox.Create();
        var rig = Build(sandbox, new MsgsRoute(new UnreadableSource(), new FilesMode(), () => true));

        _ = Assert.Throws<StoreUnavailableException>(() => rig.Services.Core.ListExchanges(Path.Combine(sandbox.Root, "a")));
    }

    [Fact]
    public void CreatingAMessageRefusesWhenTheMessageStoreCannotAnswerItsDuplicateChecks()
    {
        var sandbox = UnitSandbox.Create();
        var rig = Build(sandbox, new MsgsRoute(new UnreadableSource(), new DbMode()));
        var a = Path.Combine(sandbox.Root, "a");

        _ = Assert.Throws<StoreUnavailableException>(() => rig.Services.Messages.CreateMessage(
            a, new JsonObject { ["type"] = "request", ["to"] = "implementor", ["from"] = "orchestrator", ["slug"] = "w", ["team"] = "tm" }));
        Assert.False(Directory.Exists(Path.Combine(a, "msgs")) && Directory.EnumerateFiles(Path.Combine(a, "msgs")).Any());
    }

    [Fact]
    public void TheLaneCharterCheckThrowsWhenTheRequestCannotBeRead()
    {
        var sandbox = UnitSandbox.Create();
        var rig = Build(sandbox, new MsgsRoute(new UnreadableSource(), new DbMode()));
        var a = Path.Combine(sandbox.Root, "a");
        var teamFile = Path.Combine(a, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(teamFile)!);
        File.WriteAllText(
            teamFile,
            "{\"team_id\":\"t-tm\",\"members\":[{\"role\":\"sub-orchestrator\",\"name\":\"tm-so\",\"kind\":\"claude\"}],"
            + "\"streams\":{\"ws1\":{\"state\":\"running\",\"sub_orchestrator\":\"tm-so\",\"opened\":\"2026-01-01T00:00:00Z\"}}}\n");
        var exchanges = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "20260101-000001-r001",
                ["request"] = new JsonObject
                {
                    ["path"] = Path.Combine(a, "msgs", "20260101-000001-r001--sub-orchestrator--w--request.md"),
                    ["meta"] = new JsonObject { ["to"] = "sub-orchestrator" },
                },
                ["response"] = JsonValue.Null,
            },
        };
        var core = rig.Services.Core;

        _ = Assert.Throws<StoreUnavailableException>(() => core.OpenLaneCharterIds(a));
    }

    internal sealed class DbMode : IMessageModeReader
    {
        public string Read(string domain) => "db";
    }

    internal sealed class FilesMode : IMessageModeReader
    {
        public string Read(string domain) => "files";
    }

    internal sealed class UnreadableSource : IMsgScope
    {
        public IMsgScope Scope() => this;

        public void Dispose()
        {
        }

        public IReadOnlyList<MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs) => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<MsgFileRow> Messages(string poolDir, string? id = null, string? type = null) => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<MsgFileRow> ArchivedMessages(string poolDir) => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<MsgFileRow> MessagesById(string id, string type) => throw new InvalidOperationException("the state database cannot be read");

        public MsgFileRow? ByPath(string path) => throw new InvalidOperationException("the state database cannot be read");

        public long Version() => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion) => throw new InvalidOperationException("the state database cannot be read");
    }

    [Fact]
    public void TheChangeTokenIsZeroBeforeAnyWriteAndFollowsDomainSeq()
    {
        var sandbox = UnitSandbox.Create();
        _open.Add(StateDbModule.Open(sandbox.Home, mayCreate: true));
        var source = StateDbModule.CreateMsgSource(sandbox.Home);
        Assert.Equal(0, source.Version());
        var written = StateDbModule.OpenDirect(sandbox.Home).Write(tx =>
        {
            tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 7, 1)");
            tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('peer-records', 99, 1)");
            return 0;
        });
        Assert.True(written.Ok, written.Message);

        Assert.Equal(7, source.Version());
    }
}
