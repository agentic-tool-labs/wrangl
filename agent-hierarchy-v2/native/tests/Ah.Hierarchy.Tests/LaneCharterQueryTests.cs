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
/// ah-msgs-fast-2 S1/S2: the charters of running lanes found by the per-lane query (db mode) are the ones the rule finds over every
/// request (files mode), on a fixture of open, done and child-done lanes, closed and duplicate candidates, early and unparseable times,
/// and a request whose team comes from its team file.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LaneCharterQueryTests : IDisposable
{
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var o in _open)
        {
            o.Dispose();
        }
    }

    internal sealed record Msg(string Id, string Type, string To, string? ToName, string? Team, string Created, string Body = "", string Extra = "");

    private static string Report => "Done: the work is finished.\n";

    /// <summary>Lanes of team <c>mgr</c>: s1, s2 (role lane-lead), s5, s6, s7 run; s3's child team is done; s4 is done.</summary>
    private static string ManagerTeam => """
        {"members":[{"name":"so-2","role":"lane-lead"}],
         "streams":{
          "s1":{"sub_orchestrator":"so-1","state":"working","opened":"2026-05-28T00:00:00Z"},
          "s2":{"sub_orchestrator":"so-2","state":"working","opened":"2026-05-28T00:00:00Z"},
          "s3":{"sub_orchestrator":"so-3","state":"working","opened":"2026-05-28T00:00:00Z"},
          "s4":{"sub_orchestrator":"so-4","state":"done","opened":"2026-05-28T00:00:00Z"},
          "s5":{"sub_orchestrator":"so-5","state":"working","opened":"2026-05-28T00:00:00Z"},
          "s6":{"sub_orchestrator":"so-6","state":"working","opened":"2026-05-28T00:00:00Z"},
          "s7":{"sub_orchestrator":"so-7","state":"working"}}}
        """;

    private static string ChildTeam => """{"parent":{"team":"mgr","stream":"s3"},"workstream":{"state":"done"}}""";

    internal static readonly string[] Expected = ["20260528-000002-c002", "20260528-000005-c005", "20260528-000010-c010"];

    private static List<Msg> Fixture() =>
    [
        // s1: one written before the lane opened, then two after it: the older of those is the charter, answered
        new("20260528-000001-c001", "request", "sub-orchestrator", "so-1", "mgr", "2026-05-27T23:00:00Z"),
        new("20260528-000002-c002", "request", "sub-orchestrator", "so-1", "mgr", "2026-05-28T00:01:00Z"),
        new("20260528-000002-c002", "response", "sub-orchestrator", null, "mgr", "2026-05-28T00:05:00Z", Report),
        new("20260528-000003-c003", "request", "sub-orchestrator", "so-1", "mgr", "2026-05-28T00:02:00Z"),
        // s2: addressed by role, not literally sub-orchestrator; the first names another sub-orchestrator
        new("20260528-000004-c004", "request", "lane-lead", "so-other", "mgr", "2026-05-28T00:01:00Z"),
        new("20260528-000005-c005", "request", "lane-lead", "so-2", "mgr", "2026-05-28T00:03:00Z"),
        new("20260528-000005-c005", "response", "lane-lead", null, "mgr", "2026-05-28T00:06:00Z", Report),
        // s5: the oldest is closed, so the lane has none and the newer one is not promoted
        new("20260528-000006-c006", "request", "sub-orchestrator", "so-5", "mgr", "2026-05-28T00:01:00Z"),
        new("20260528-000006-c006", "response", "sub-orchestrator", null, "mgr", "2026-05-28T00:04:00Z", "", "closed: true\n"),
        new("20260528-000007-c007", "request", "sub-orchestrator", "so-5", "mgr", "2026-05-28T00:02:00Z"),
        // s3 (child done) and s4 (done) have no charter
        new("20260528-000008-c008", "request", "sub-orchestrator", "so-3", "mgr", "2026-05-28T00:01:00Z"),
        new("20260528-000008-c008", "response", "sub-orchestrator", null, "mgr", "2026-05-28T00:04:00Z", Report),
        new("20260528-000009-c009", "request", "sub-orchestrator", "so-4", "mgr", "2026-05-28T00:01:00Z"),
        new("20260528-000009-c009", "response", "sub-orchestrator", null, "mgr", "2026-05-28T00:04:00Z", Report),
        // s6: no team header, the team file names the manager
        new("20260528-000010-c010", "request", "sub-orchestrator", "so-6", null, "2026-05-28T00:01:00Z", "", "team_file: /x/teams/mgr.json\n"),
        new("20260528-000010-c010", "response", "sub-orchestrator", null, "mgr", "2026-05-28T00:04:00Z", Report),
        // s7: a time that does not parse, and the right time in another team
        new("20260528-000011-c011", "request", "sub-orchestrator", "so-7", "mgr", "not a time"),
        new("20260528-000012-c012", "request", "sub-orchestrator", "so-7", "other", "2026-05-28T00:01:00Z"),
    ];

    private static string Name(Msg m) => $"{m.Id}--{m.To}--s--{m.Type}.md";

    private static string Text(Msg m) =>
        $"---\nid: {m.Id}\ntype: {m.Type}\nto: {m.To}\nfrom: orchestrator\nslug: s\n"
        + (m.ToName is null ? "" : $"to_name: {m.ToName}\n")
        + (m.Team is null ? "" : $"team: {m.Team}\n")
        + $"created: {m.Created}\n{m.Extra}---\n{m.Body}";

    private static HierarchyServices Build(UnitSandbox sandbox, long nowMs, ITeamAuthority? teams = null)
    {
        var variables = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["TZ"] = "UTC" };
        var env = new FakeEnvironment(variables, new PluginInfo(HierarchyTableFns.AhRoot, "wrangl", "0.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(sandbox.Root, 4242));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, teamAuthority: teams);
        var route = new MsgsRoute(StateDbModule.CreateMsgSource(sandbox.Home), StateDbModule.CreateModeReader(sandbox.Home), () => StateDbModule.DomainFlipped(sandbox.Home, "msgs"));
        return HierarchyModule.Create(new HierarchyEnvironment(
            io.Files, env, new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(nowMs)), new SequenceRandom(), io.Processes, config, roster, null,
            new ProcessFacts(sandbox.Root, 4242), null, null, null, null, route));
    }

    /// <summary>Writes the pool and imports every message into the database through the one front-matter reading the write path uses; the live flag is the write-time one.</summary>
    internal (UnitSandbox Sandbox, string Pool) Seed(IEnumerable<Msg>? extra = null, bool only = false)
    {
        var sandbox = UnitSandbox.Create();
        var pool = Path.Combine(sandbox.Root, "a");
        Directory.CreateDirectory(Path.Combine(pool, "msgs"));
        Directory.CreateDirectory(Path.Combine(pool, "teams"));
        File.WriteAllText(Path.Combine(pool, "teams", "mgr.json"), ManagerTeam);
        File.WriteAllText(Path.Combine(pool, "teams", "child.json"), ChildTeam);
        _open.Add(StateDbModule.Open(sandbox.Home, mayCreate: true));
        Insert(sandbox.Home, pool, (only ? [] : Fixture()).Concat(extra ?? []), bump: false);
        return (sandbox, pool);
    }

    /// <summary>Writes <paramref name="msgs"/> into the pool and the database; <paramref name="bump"/> counts the write in the store's version, as the write path does.</summary>
    private static void Insert(string home, string pool, IEnumerable<Msg> msgs, bool bump)
    {
        var rows = new List<MsgFileRow>();
        foreach (var m in msgs)
        {
            var path = Path.Combine(pool, "msgs", Name(m));
            var bytes = new UTF8Encoding(false).GetBytes(Text(m));
            File.WriteAllBytes(path, bytes);
            var fm = MessageText.ParseFrontmatter(Encoding.UTF8.GetString(bytes))!.Value.Fields;
            rows.Add(MsgFileRows.FromFrontMatter(pool, path, Name(m), m.Id, m.Type, m.To, "s", fm, false, MsgRows.Sealed, bytes));
        }

        var written = StateDbModule.OpenDirect(home).Write(tx =>
        {
            foreach (var row in rows)
            {
                tx.Execute(MsgRows.InsertMessageSql, MsgRows.MessageArgs(row));
            }

            long Pk(MsgFileRow r) => (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(r))[0][0]!;
            foreach (var row in rows)
            {
                tx.Execute(MsgRows.InsertBodySql, MsgRows.BodyArgs(Pk(row), row, 1_780_000_000_000));
            }

            foreach (var pair in MsgRows.Pair(rows))
            {
                tx.Execute(MsgRows.InsertExchangeSql, MsgRows.ExchangeArgs(pair, Pk(pair.Request), pair.Response is null ? null : Pk(pair.Response), pair.Response is null ? null : 1_780_000_000_000));
            }

            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.msgs', 'db')");
            if (bump)
            {
                tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 1, 0) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1");
            }

            return 0;
        });
        Assert.True(written.Ok, written.Message);
    }

    /// <summary>b459 negative: the full listing is kept by the store's version, so an exchange written between two reads is in the second.</summary>
    [Fact]
    public void NegativeAnExchangeWrittenBetweenTwoListingsIsInTheSecond()
    {
        var (sandbox, pool) = Seed();
        var core = Build(sandbox, 1_780_000_000_000, StateDbModule.OpenTeamAuthority(sandbox.Home)).Core;
        var before = core.ListExchanges([pool]).Count;
        Assert.Equal(before, core.ListExchanges([pool]).Count);

        Insert(sandbox.Home, pool, [new Msg("20260528-000013-c013", "request", "implementor", "im-1", "mgr", "2026-05-28T00:09:00Z")], bump: true);
        Assert.Equal(before + 1, core.ListExchanges([pool]).Count);
    }

    [Fact]
    public void TheCharterQueryFindsTheChartersTheRuleFindsOverEveryRequest()
    {
        var (sandbox, pool) = Seed();
        var db = Build(sandbox, 1_780_000_000_000).Core;

        Assert.Equal(Expected, db.OpenLaneCharterIds(pool).Order(StringComparer.Ordinal));
    }

    private static string OpenText(JsonArray exchanges) =>
        string.Join("\n", exchanges.Where(static e => e.AsObject()["open"].IsTruthy).Select(static e => JsonText.Stringify(e, 0)));

    /// <summary>S2: an answered charter is not live, yet the live scope still lists it open as a charter; done and child-done lanes' answered requests leave it.</summary>
    [Fact]
    public void TheLiveScopeAddsTheAnsweredChartersOfRunningLanesAndNothingElse()
    {
        var (sandbox, pool) = Seed();

        // answered 11 minutes and a millisecond ago: only the live flag, or the charter query, keeps an exchange in the live scope
        var later = Build(sandbox,1_780_000_000_000 + (11 * 60 * 1000) + 1).Core;
        var full = later.ListExchanges([pool]);
        var live = later.ListExchanges([pool], live: true);
        Assert.Equal(OpenText(full), OpenText(live));

        string? Of(JsonArray list, string id) => list.Select(static e => e.AsObject()).Where(e => e["id"].AsString() == id).Select(static e => JsonText.Stringify(e, 0)).SingleOrDefault();
        foreach (var charter in Expected)
        {
            Assert.Contains("\"open\":true,\"charter\":true", Of(live, charter), StringComparison.Ordinal);
        }

        // the answered requests of the child-done (c008) and done (c009) lanes are reported, so not live and not charters
        Assert.Null(Of(live, "20260528-000008-c008"));
        Assert.Null(Of(live, "20260528-000009-c009"));
        Assert.Contains("\"open\":false", Of(full, "20260528-000009-c009"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The charter flag is read only when the pool has an answered request to the sub-orchestrator. The live scope asks the pool, so an
    /// answered one that has left the live set still turns the flag on, and with none the flag stays off as in the full list.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLiveScopeFlagsAnUnansweredCharterExactlyWhenTheFullListDoes(bool answeredElsewhere)
    {
        Msg[] msgs = answeredElsewhere
            ? [Fixture()[0], new("20260528-000001-c001", "response", "sub-orchestrator", null, "mgr", "2026-05-28T00:04:00Z", Report), Fixture()[1]]
            : [Fixture()[1]];
        var (sandbox, pool) = Seed(msgs, only: true);
        var later = Build(sandbox,1_780_000_000_000 + (11 * 60 * 1000) + 1).Core;
        var full = OpenText(later.ListExchanges([pool]));

        Assert.Equal(full, OpenText(later.ListExchanges([pool], live: true)));
        Assert.Equal(answeredElsewhere, full.Contains("\"charter\":true", StringComparison.Ordinal));
    }

    /// <summary>
    /// S3: a team's open exchanges filtered on the stored team equal those filtered on the request files' headers, for the manager team,
    /// another team and no team; and inside a listing scope, where one live listing serves every team, they are the same again.
    /// </summary>
    [Fact]
    public void ATeamsOpenExchangesFromTheStoredTeamAreTheSameInAndOutOfAListingScope()
    {
        var (sandbox, pool) = Seed();
        var db = Build(sandbox, 1_780_000_000_000).Messages;
        foreach (JsonValue team in new[] { (JsonValue)"mgr", "other", JsonValue.Null, "nobody" })
        {
            var expected = JsonText.Stringify(db.OpenExchanges(pool, team), 0);
            using (Ah.Core.Files.ListingScope.Begin())
            {
                Assert.Equal(expected, JsonText.Stringify(db.OpenExchanges(pool, team), 0));
                Assert.Equal(expected, JsonText.Stringify(db.OpenExchanges(pool, team), 0));
            }
        }

        Assert.Contains("20260528-000012-c012", JsonText.Stringify(db.OpenExchanges(pool, "other"), 0), StringComparison.Ordinal);
        Assert.DoesNotContain("20260528-000012-c012", JsonText.Stringify(db.OpenExchanges(pool, "mgr"), 0), StringComparison.Ordinal);
        Assert.Contains("20260528-000010-c010", JsonText.Stringify(db.OpenExchanges(pool, JsonValue.Null), 0), StringComparison.Ordinal);
    }

    /// <summary>S3: a listing a caller changes inside a listing scope does not change what the next caller in the scope gets.</summary>
    [Fact]
    public void NegativeAChangedLiveListingInsideAScopeIsNotSeenByTheNextCaller()
    {
        var (sandbox, pool) = Seed();
        var core = Build(sandbox,1_780_000_000_000).Core;
        var expected = JsonText.Stringify(core.ListExchanges([pool], live: true), 0);
        using var scope = Ah.Core.Files.ListingScope.Begin();
        var first = core.ListExchanges([pool], live: true);
        first[0].AsObject()["open"] = "changed";
        first.Add(JsonValue.Null);
        Assert.Equal(expected, JsonText.Stringify(core.ListExchanges([pool], live: true), 0));
    }

    /// <summary>S5/S7: the exchange of one id read by id is the full listing's exchange of that id, charters and answered ones included; an unknown id has none.</summary>
    [Fact]
    public void AnExchangeReadByIdIsTheFullListingsExchangeOfThatId()
    {
        var (sandbox, pool) = Seed();
        var core = Build(sandbox,1_780_000_000_000 + (11 * 60 * 1000) + 1).Core;
        var full = core.ListExchanges([pool]);
        foreach (var e in full)
        {
            var id = e.AsObject()["id"].AsString();
            Assert.Equal(JsonText.Stringify(e, 0), JsonText.Stringify(Assert.Single(core.ListExchangesWithIds([pool], [id])), 0));
        }

        Assert.Empty(core.ListExchangesWithIds([pool], ["20991231-000000-none"]));
    }

    /// <summary>
    /// Review F1: a page and a count filter on the stored team, empty text standing for none, and on the recipient; a team not named
    /// (here mgr against none, other against mgr) matches nothing of it.
    /// </summary>
    [Fact]
    public void APageAndACountKeepOnlyTheNamedTeamsAndRecipient()
    {
        var (sandbox, pool) = Seed([new("20260528-000013-c013", "request", "reviewer", null, null, "2026-05-28T00:01:00Z")]);
        var core = Build(sandbox,1_780_000_000_000).Core;
        string[] Ids(IReadOnlyList<string>? teams, string? to) => [.. core.ExchangeRowsBefore([pool], null, 100, teams, to)!.Select(static r => r.Id).Distinct()];
        Assert.Equal(["20260528-000013-c013", "20260528-000010-c010"], Ids([string.Empty], null));
        Assert.Equal(["20260528-000013-c013"], Ids([string.Empty], "reviewer"));
        Assert.Equal(["20260528-000012-c012"], Ids(["other"], null));
        Assert.Equal(["20260528-000012-c012", "20260528-000010-c010"], Ids(["other", string.Empty], "sub-orchestrator"));
        Assert.DoesNotContain("20260528-000010-c010", Ids(["mgr"], null));
        Assert.DoesNotContain("20260528-000012-c012", Ids(["mgr"], null));
        Assert.Equal(13, Ids(null, null).Length);
        Assert.Equal(2, core.ExchangeCount([pool], [string.Empty], null));
        Assert.Equal(1, core.ExchangeCount([pool], [string.Empty], "reviewer"));
        Assert.Equal(0, core.ExchangeCount([pool], ["nobody"], null));
    }

    [Fact]
    public void NegativeATeamFileNamingAnotherManagerMakesNoCharter()
    {
        // the nearest input that must not match: the team comes from the team file, and it names another team
        var (sandbox, pool) = Seed([new("20260528-000000-c000", "request", "sub-orchestrator", "so-6", null, "2026-05-28T00:00:30Z", "", "team_file: /x/teams/other.json\n")]);
        var db = Build(sandbox,1_780_000_000_000).Core;

        Assert.Equal(Expected, db.OpenLaneCharterIds(pool).Order(StringComparer.Ordinal));
    }
}
