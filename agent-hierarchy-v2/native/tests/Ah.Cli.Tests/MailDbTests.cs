using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>Mail in the database answers as mail in files does, with the checks of each op inside its transaction; a home that was not flipped never touches the tables.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailDbTests : IDisposable
{
    private const string Session = "s-1";

    private Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Home => _sandbox.AhHome;

    private string Box(string name = "imp", string team = "tm~0123456789") => Path.Combine(Home, "mailbox", team, name);

    private ServiceGraph Graph() => ServiceGraph.Build(
        PlatformModule.CreateIo(null),
        new FakeEnv(_sandbox.Home, Home, new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = Home, ["AH_DAEMON"] = "off", ["AH_TEST"] = "1" }),
        new FakeRunner(),
        new FakeDaemonClient());

    private void Flip() => Assert.Null(StateDbModule.FlipMailToDatabase(Home));

    private IMailStore Store => StateDbModule.OpenMailStore(Home);

    private string KeyOf(string dir) => Path.GetRelativePath(Path.Combine(Home, "mailbox"), dir);

    private StateResult Run(ServiceGraph graph, string op, JsonObject args, string? opId = null)
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_sandbox.Root, "proj")).FullName;
        var context = new RequestContext(cwd, cwd, string.Empty, string.Empty);
        return graph.Writer.Invoke("mailbox", op, JsonText.ToUtf8(args, 0), context, opId is null ? null : new StateCallOptions(opId));
    }

    private static JsonValue Ok(StateResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonText.Parse(result.ResultJson.Span);
    }

    private static JsonObject Entry(string id, string kind = "note", string? reference = null, string? digest = null, string text = "hi")
    {
        var entry = new JsonObject { ["id"] = id, ["kind"] = kind, ["from"] = "user", ["created"] = "2026-10-12T00:00:00.000Z", ["text"] = text, ["route"] = "pull" };
        if (reference is not null)
        {
            entry["ref"] = reference;
            entry["ref_sha256"] = digest ?? string.Empty;
        }

        return entry;
    }

    private static bool Flag(JsonValue result, string name) => result.Member(name) is { IsTruthy: true };

    private JsonValue Append(ServiceGraph graph, string dir, JsonObject entry, string? opId = null) =>
        Ok(Run(graph, "append", new JsonObject { ["dir"] = dir, ["entry"] = entry }, opId));

    private static string CaseRoot()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var candidate = Path.Combine(at.FullName, "tests", "contracts", "state", "cases", "mailbox");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("tests/contracts/state/cases/mailbox");
    }

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var file in Directory.EnumerateFiles(CaseRoot(), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file), false);
            data.Add(Path.GetFileName(file), true);
        }

        return data;
    }

    private string Planted(string text) =>
        text.Replace("<ROOT:ah>", Home, StringComparison.Ordinal).Replace("<ROOT:proj>", Path.Combine(_sandbox.Root, "proj"), StringComparison.Ordinal);

    private (bool Ok, string Result) RunCaseStep(ServiceGraph graph, JsonValue step)
    {
        var body = step.Member("body")!.Value;
        var result = graph.Writer.Invoke(
            step.Member("domain")!.Value.AsString(),
            step.Member("op")!.Value.AsString(),
            JsonText.ToUtf8(body.Member("args")!.Value, 0),
            new RequestContext(Path.Combine(_sandbox.Root, "proj"), Path.Combine(_sandbox.Root, "proj"), string.Empty, string.Empty),
            new StateCallOptions(body.Member("op_id")!.Value.AsString()));
        return (result.Ok, result.Ok ? JsonText.Stringify(JsonText.Parse(result.ResultJson.Span), 0) : string.Empty);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryMailboxCaseAnswersAsRecordedInBothModes(string file, bool database)
    {
        var definition = JsonText.Parse(Planted(File.ReadAllText(Path.Combine(CaseRoot(), file))));
        _ = Directory.CreateDirectory(Path.Combine(_sandbox.Root, "proj"));
        if (database)
        {
            Flip();
        }

        var graph = Graph();
        var input = definition.Member("input")!.Value;
        if (input.Member("setup") is { IsArray: true } setup)
        {
            foreach (var step in setup.AsArray())
            {
                Assert.True(RunCaseStep(graph, step).Ok);
            }
        }

        var (ok, result) = RunCaseStep(graph, input);

        var expected = definition.Member("expect")!.Value.Member("response")!.Value;
        Assert.Equal(expected.Member("ok")!.Value.AsBool(), ok);
        Assert.Equal(JsonText.Stringify(expected.Member("result")!.Value, 0), result);
    }

    /// <summary>b459 step 15: a box's scan and acks are held and read by delta; a new entry shows, an ack drops it, a deleted row (a prune) drops it.</summary>
    [Fact]
    public void AHeldBoxShowsANewEntryAndNegativeAnAckOrAPruneDropsIt()
    {
        Flip();
        var graph = Graph();
        string Pending() => string.Join(',', graph.Mailbox.Pending(Box()).Select(static e => e.Member("id")!.Value.AsString()));
        _ = Append(graph, Box(), Entry("e1"));
        Assert.Equal("e1", Pending());

        _ = Append(graph, Box(), Entry("e2"));
        _ = Append(graph, Box(), Entry("e3"));
        Assert.Equal("e1,e2,e3", Pending());

        _ = Ok(Run(graph, "ack", new JsonObject { ["dir"] = Box(), ["id"] = "e1", ["by"] = "t" }));
        Assert.Equal("e2,e3", Pending());

        // the prune deletes a held row with no mail write: the count guard reads the box again
        Assert.True(StateDbModule.OpenDirect(Home).Write(tx =>
        {
            tx.Execute($"DELETE FROM mail_entries WHERE box = '{KeyOf(Box())}' AND id = 'e2'");
            return 0;
        }).Ok);
        Assert.Equal("e3", Pending());
    }

    /// <summary>b500: the box's top row is deleted and an insert reuses its pk; the count is unchanged, the id at the held cursor is not.</summary>
    [Fact]
    public void AReusedTopPkReloadsTheHeldBox()
    {
        var (graph, mailbox, store) = CountedMailbox();
        string Pending() => string.Join(',', mailbox.Pending(Box()).Select(static e => e.Member("id")!.Value.AsString()));
        _ = Append(graph, Box(), Entry("e1"));
        _ = Append(graph, Box(), Entry("e2"));
        Assert.Equal("e1,e2", Pending());

        Assert.True(StateDbModule.OpenDirect(Home).Write(tx =>
        {
            tx.Execute($"DELETE FROM mail_entries WHERE box = '{KeyOf(Box())}' AND id = 'e2'");
            return 0;
        }).Ok);
        _ = Append(graph, Box(), Entry("e3"));
        Assert.Equal(2L, Assert.Single(Store.Entries(KeyOf(Box()), 1)).Key);

        Assert.Equal("e1,e3", Pending());
        Assert.Equal(2, store.FullReads);
    }

    /// <summary>b500: negative, a box with no write since its last scan is not read in full again.</summary>
    [Fact]
    public void NegativeAnUnchangedHeldBoxDoesNotReload()
    {
        var (graph, mailbox, store) = CountedMailbox();
        _ = Append(graph, Box(), Entry("e1"));
        _ = mailbox.Pending(Box());
        _ = Append(graph, Box(), Entry("e2"));
        _ = mailbox.Pending(Box());

        _ = mailbox.Pending(Box());

        Assert.Equal(1, store.FullReads);
    }

    private (ServiceGraph Graph, IMailbox Mailbox, CountingMailStore Store) CountedMailbox()
    {
        Flip();
        var graph = Graph();
        var env = new FakeEnv(_sandbox.Home, Home, new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = Home, ["AH_DAEMON"] = "off", ["AH_TEST"] = "1" });
        var store = new CountingMailStore(Store);
        return (graph, MeshModule.CreateMailbox(graph.Io.Files, env, graph.Io.Clock, graph.Io.Random, graph.Hierarchy, graph.Roster, graph.Writer, null, null, null, store), store);
    }

    /// <summary>The store, counting the held scan's full reads (a counted read from cursor 0).</summary>
    private sealed class CountingMailStore(IMailStore inner) : IMailStore
    {
        public int FullReads { get; private set; }

        public bool IsDatabase => inner.IsDatabase;

        public long ChangeToken() => inner.ChangeToken();

        public string BoxStamp(string box) => inner.BoxStamp(box);

        public long NewestEntry(string box) => inner.NewestEntry(box);

        public IReadOnlyList<string> Boxes() => inner.Boxes();

        public long HeartbeatBoxes() => inner.HeartbeatBoxes();

        public IReadOnlyList<KeyValuePair<string, string>> BriefLadderStates() => inner.BriefLadderStates();

        public IReadOnlyList<KeyValuePair<long, ReadOnlyMemory<byte>>> Entries(string box, long afterPk) => inner.Entries(box, afterPk);

        public (long Count, string? CursorId, string? LastId, IReadOnlyList<KeyValuePair<long, ReadOnlyMemory<byte>>> Rows) CountedEntries(string box, long afterPk)
        {
            FullReads += afterPk == 0 ? 1 : 0;
            return inner.CountedEntries(box, afterPk);
        }

        public string AckStamp(string box) => inner.AckStamp(box);

        public IReadOnlyList<ReadOnlyMemory<byte>> UnackedEntriesWithIdPrefix(string box, string idPrefix) => inner.UnackedEntriesWithIdPrefix(box, idPrefix);

        public IReadOnlyList<ReadOnlyMemory<byte>> Attempts(string box) => inner.Attempts(box);

        public IReadOnlyList<ReadOnlyMemory<byte>> Attempts(string box, string id) => inner.Attempts(box, id);

        public (IReadOnlyList<ReadOnlyMemory<byte>> Open, IReadOnlyList<KeyValuePair<string, string>> Settled) LadderSweep(string box, IReadOnlyList<string> settled) => inner.LadderSweep(box, settled);

        public ReadOnlyMemory<byte>? Ack(string box, string id) => inner.Ack(box, id);

        public IReadOnlyList<string> AckIds(string box) => inner.AckIds(box);

        public ReadOnlyMemory<byte>? Claim(string box, string id) => inner.Claim(box, id);

        public IReadOnlyList<string> ClaimIds(string box) => inner.ClaimIds(box);

        public IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> Adapters(string box) => inner.Adapters(box);

        public ReadOnlyMemory<byte>? Note(string kind, string name) => inner.Note(kind, name);
    }

    [Fact]
    public void AnAckTwiceKeepsTheFirstWriter()
    {
        Flip();
        var graph = Graph();

        Assert.True(Flag(Ok(Run(graph, "ack", new JsonObject { ["dir"] = Box(), ["id"] = "e1", ["by"] = "first" })), "fresh"));
        Assert.False(Flag(Ok(Run(graph, "ack", new JsonObject { ["dir"] = Box(), ["id"] = "e1", ["by"] = "second" })), "fresh"));

        Assert.Equal("first", graph.Mailbox.ReadAck(Box(), "e1")!.Value.Member("by")!.Value.AsString());
    }

    [Fact]
    public void AReportOfTheSameRefAndDigestIsTheOneAlreadyThere()
    {
        Flip();
        var graph = Graph();
        Assert.False(Flag(Append(graph, Box(), Entry("r1", "report", "/ref", "aa")), "existed"));

        var again = Append(graph, Box(), Entry("r2", "report", "/ref", "aa"));

        Assert.True(Flag(again, "existed"));
        Assert.Equal("r1", again.Member("same")!.Value.AsString());
        _ = Assert.Single(graph.Mailbox.ReadEntries(Box()).Entries);
    }

    [Fact]
    public void NegativeAReportOfAnotherDigestIsANewEntry()
    {
        Flip();
        var graph = Graph();
        _ = Append(graph, Box(), Entry("r1", "report", "/ref", "aa"));

        Assert.False(Flag(Append(graph, Box(), Entry("r2", "report", "/ref", "bb")), "existed"));

        Assert.Equal(2, graph.Mailbox.ReadEntries(Box()).Entries.Count);
    }

    [Fact]
    public void AnEntryOfTheSameIdExistsAndAnotherBoxIsItsOwn()
    {
        Flip();
        var graph = Graph();
        _ = Append(graph, Box(), Entry("e1"));

        Assert.True(Flag(Append(graph, Box(), Entry("e1")), "existed"));
        Assert.False(Flag(Append(graph, Box("other"), Entry("e1")), "existed"));
    }

    [Fact]
    public void ANoteWritesOnlyOverTheSeqItExpects()
    {
        Flip();
        var graph = Graph();
        JsonObject Note(double seq, double expect) => new() { ["kind"] = "doorbell", ["name"] = Session, ["data"] = new JsonObject { ["seq"] = seq }, ["expect_seq"] = expect };

        Assert.True(Flag(Ok(Run(graph, "note", Note(1, 0))), "written"));
        Assert.False(Flag(Ok(Run(graph, "note", Note(2, 0))), "written"));
        Assert.Contains("\"seq\":1", Encoding.UTF8.GetString(Store.Note("doorbell", Session)!.Value.Span), StringComparison.Ordinal);
        Assert.True(Flag(Ok(Run(graph, "note", Note(2, 1))), "written"));
        Assert.True(Flag(Ok(Run(graph, "note", new JsonObject { ["kind"] = "doorbell", ["name"] = Session, ["clear"] = true })), "removed"));
        Assert.Null(Store.Note("doorbell", Session));
    }

    [Fact]
    public void ARetryWithTheSameOpIdReplaysItsAnswer()
    {
        Flip();
        var graph = Graph();
        var args = new JsonObject { ["dir"] = Box(), ["id"] = "e1", ["by"] = "first" };

        Assert.True(Flag(Ok(Run(graph, "ack", args, "op-1")), "fresh"));
        Assert.True(Flag(Ok(Run(graph, "ack", args, "op-1")), "fresh"));
        Assert.False(Flag(Ok(Run(graph, "ack", args, "op-2")), "fresh"));
    }

    [Fact]
    public async Task SixteenWritersOnOneBoxLoseNothingAndKeepTheirOrder()
    {
        Flip();
        var writers = Enumerable.Range(0, 16).Select(w => Task.Run(() =>
        {
            var graph = Graph();
            for (var i = 0; i < 10; i++)
            {
                _ = Append(graph, Box(), Entry(string.Create(CultureInfo.InvariantCulture, $"w{w}-{i:00}")));
            }
        }));

        await Task.WhenAll(writers);

        var ids = Graph().Mailbox.ReadEntries(Box()).Entries.Select(static e => e.Member("id")!.Value.AsString()).ToList();
        Assert.Equal(160, ids.Count);
        foreach (var writer in ids.GroupBy(static id => id[..id.IndexOf('-', StringComparison.Ordinal)]))
        {
            Assert.Equal(writer.Order(StringComparer.Ordinal), writer);
        }
    }

    [Fact]
    public void AnEntryOverTheLimitIsRefused()
    {
        Flip();
        var graph = Graph();

        var refused = Run(graph, "append", new JsonObject { ["dir"] = Box(), ["entry"] = Entry("big", text: new string('x', 4096)) });

        Assert.False(refused.Ok);
        Assert.Equal("bad_request", refused.ErrorCode);
        Assert.Empty(graph.Mailbox.ReadEntries(Box()).Entries);
    }

    [Fact]
    public void NegativeABoxOutsideTheMailboxRootIsRefused()
    {
        Flip();

        Assert.False(Run(Graph(), "append", new JsonObject { ["dir"] = Path.Combine(_sandbox.Root, "elsewhere"), ["entry"] = Entry("e1") }).Ok);
    }

    private static string Text(JsonObject? record, string key) => record?.Get(key) is { IsText: true } text ? text.AsString() : "-";

    /// <summary>One box's worth of every record, through the real ops, read back through the mailbox.</summary>
    private string Exercise()
    {
        var graph = Graph();
        _ = Append(graph, Box(), Entry("e1"));
        _ = Append(graph, Box(), Entry("e2"));
        _ = Append(graph, Box("arch", "ab~0123456789"), Entry("e3"));
        Assert.True(Flag(Ok(Run(graph, "attempt", new JsonObject { ["dir"] = Box(), ["row"] = new JsonObject { ["id"] = "e1", ["ladder"] = true, ["state"] = "tried" }, ["seq"] = 1 })), "written"));
        Assert.False(Flag(Ok(Run(graph, "attempt", new JsonObject { ["dir"] = Box(), ["row"] = new JsonObject { ["id"] = "e1", ["ladder"] = true, ["state"] = "tried" }, ["seq"] = 1 })), "written"));
        Assert.False(Flag(Ok(Run(graph, "attempt", new JsonObject { ["dir"] = Box(), ["row"] = new JsonObject { ["id"] = "e1", ["state"] = "again" }, ["once"] = true })), "written"));
        _ = Ok(Run(graph, "ack", new JsonObject { ["dir"] = Box(), ["id"] = "e1", ["by"] = "cli" }));
        graph.Mailbox.WriteHeartbeat(Box(), "claude-mod", Session, 5000);
        var mail = graph.Mailbox;
        var claim = mail.Claim(Box(), "e2", ClaimActions.Take, new JsonObject { ["session_id"] = Session, ["holder"] = "imp@tm" });
        var lines = new List<string>
        {
            "boxes " + string.Join(',', mail.ListBoxes().Select(static b => b.Address)),
            "entries " + string.Join(',', mail.ReadEntries(Box()).Entries.Select(static e => e.Member("id")!.Value.AsString())),
            "inbox " + string.Join(',', mail.Inbox(Box()).Entries.Select(static e => e.Member("id")!.Value.AsString())),
            "acked " + mail.IsAcked(Box(), "e1") + " " + mail.IsAcked(Box(), "e2") + " " + mail.AckedIn(Box())("e1") + " " + mail.AckedIn(Box())("e2"),
            "attempts " + string.Join(',', mail.ReadAttempts(Box()).Select(static r => r.Get("id")!.Value.AsString() + ":" + (r.Get("state") is { IsText: true } state ? state.AsString() : "-"))),
            "ladder " + mail.LadderRowsOf(Box(), "e1").Count,
            "beat " + Text(mail.ReadHeartbeat(Box(), Session), "adapter"),
            "readable " + mail.RecordsReadable(Box(), "e1"),
            "claim " + claim.Won + " " + Text(mail.ReadClaim(Box(), "e2"), "state"),
        };
        return string.Join('\n', lines);
    }

    [Fact]
    public void TheMailboxReadsTheSameFromRowsAsFromFiles()
    {
        var fromFiles = Exercise();
        Assert.Contains("beat claude-mod", fromFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("claim True -", fromFiles, StringComparison.Ordinal);
        var first = _sandbox;
        _sandbox = new Sandbox();
        try
        {
            Flip();
            Assert.Equal(fromFiles, Exercise());
        }
        finally
        {
            _sandbox.Dispose();
            _sandbox = first;
        }
    }

    [Fact]
    public void AnEmptyBoxIsNotListedOnceTheDatabaseHoldsTheMail()
    {
        Flip();
        _ = Directory.CreateDirectory(Box());

        Assert.Empty(Graph().Mailbox.ListBoxes());
    }

    [Fact]
    public void AHeldBoxListShowsANewBoxOnTheNextReadWhetherAnEntryOrOnlyAHeartbeatMadeIt()
    {
        Flip();
        var graph = Graph();
        var mail = graph.Mailbox;
        _ = Append(graph, Box(), Entry("e1"));
        Assert.Equal(["imp@tm"], mail.ListBoxes().Select(static b => b.Address));

        _ = Append(graph, Box("arch"), Entry("e2"));
        Assert.Equal(["arch@tm", "imp@tm"], mail.ListBoxes().Select(static b => b.Address).Order(StringComparer.Ordinal));

        // a heartbeat leaves the mail counter where it was, yet the box it alone holds is listed
        mail.WriteHeartbeat(Box("rev"), "claude-mod", Session, 5000);
        Assert.Contains("rev@tm", mail.ListBoxes().Select(static b => b.Address));
    }

    [Fact]
    public void NegativeAHomeWithNoDatabaseHasNoMailRows()
    {
        Directory.Delete(Path.Combine(Home, "state"), recursive: true);

        Assert.False(Store.IsDatabase);
        Assert.Null(Graph().Mailbox.StoredBoxStamp(Box()));
    }

    [Fact]
    public void AFlippedHomeWritesRowsAndNoFiles()
    {
        Flip();
        var graph = Graph();
        _ = Append(graph, Box(), Entry("e1"));
        _ = Ok(Run(graph, "ack", new JsonObject { ["dir"] = Box(), ["id"] = "e1" }));

        Assert.False(Directory.Exists(Box()));
        _ = Assert.Single(Store.Entries(KeyOf(Box()), 0));
        Assert.NotNull(graph.Mailbox.StoredBoxStamp(Box()));
    }

    [Fact]
    public void AWithdrawalShowsOnTheNextReadAndALiveBriefElsewhereKeepsItOut()
    {
        Flip();
        var graph = Graph();
        _ = Append(graph, Box(), Entry("b1", "brief", "/h/msgs/x--request.md"));
        _ = Append(graph, Box("rev"), Entry("n1"));
        Assert.Empty(graph.Mailbox.WithdrawnRequestFiles());

        graph.Mailbox.AppendAttempt(Box(), new JsonObject { ["id"] = "b1", ["ladder"] = true, ["rung"] = 3, ["state"] = "withdrawn" });

        Assert.Contains("x--request.md", graph.Mailbox.WithdrawnRequestFiles());
        Assert.Contains("x--request.md", graph.Mailbox.WithdrawnRequestFiles());

        // the same request re-addressed to another box is live again
        _ = Append(graph, Box("rev"), Entry("b2", "brief", "/h/msgs/x--request.md"));
        Assert.Empty(graph.Mailbox.WithdrawnRequestFiles());
    }

    /// <summary>b414: from rows, only the box's unacked rewake entries are read (negative: a plain id, a near-prefix id and an acked rewake are not).</summary>
    [Fact]
    public void RewakesAreWithdrawnFromAnIdPrefixQueryOfTheUnackedRows()
    {
        Flip();
        var graph = Graph();
        foreach (var id in new[] { "rewake-a", "rewake-acked", "plain-1", "rewakeX" })
        {
            _ = Append(graph, Box(), Entry(id));
        }

        _ = Ok(Run(graph, "ack", new JsonObject { ["dir"] = Box(), ["id"] = "rewake-acked" }));

        var rows = Store.UnackedEntriesWithIdPrefix(KeyOf(Box()), "rewake-");
        Assert.Equal("rewake-a", JsonText.Parse(Assert.Single(rows).Span).Member("id")!.Value.AsString());
        Assert.Equal(1, graph.Mailbox.WithdrawRewakes(Box(), null, null, null, "expired"));
        Assert.Equal(0, graph.Mailbox.WithdrawRewakes(Box(), null, null, null, "expired"));
    }

    /// <summary>b459 step 9: an acked entry's ladder still reads by id, and the unacked sweep drops it and picks up a row written after it was kept.</summary>
    [Fact]
    public void AnAckedEntrysLadderStillReadsInFullById()
    {
        Flip();
        var graph = Graph();
        var mailbox = graph.Mailbox;
        _ = Append(graph, Box(), Entry("e-old"));
        _ = Append(graph, Box(), Entry("e-new"));
        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "e-old", ["rung"] = 2, ["state"] = "pending" }, 1));
        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "e-old", ["rung"] = 2, ["state"] = "delivered" }, 2));

        _ = Ok(Run(graph, "ack", new JsonObject { ["dir"] = Box(), ["id"] = "e-old" }));
        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "e-new", ["rung"] = 2, ["state"] = "pending" }, 1));

        Assert.Equal(2, mailbox.LadderRowsOf(Box(), "e-old").Count);
        Assert.Equal(["e-old", "e-new"], mailbox.LadderRowsByEntry(Box()).Select(static r => r.Id));
    }

    /// <summary>b459 step 9: the stored sweep splits on the newest ladder state, never on the ack, and sees a row written after it was kept.</summary>
    [Fact]
    public void TheStoredSweepSplitsOnTheNewestLadderStateAndNotOnTheAck()
    {
        Flip();
        var graph = Graph();
        var mailbox = graph.Mailbox;
        foreach (var id in new[] { "acked-delivered", "unacked-withdrawn", "acked-pending", "bare" })
        {
            _ = Append(graph, Box(), Entry(id));
        }

        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "acked-delivered", ["rung"] = 2, ["state"] = "pending" }, 1));
        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "acked-delivered", ["rung"] = 2, ["state"] = "delivered" }, 2));
        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "unacked-withdrawn", ["rung"] = 2, ["state"] = "withdrawn" }, 1));
        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "acked-pending", ["rung"] = 2, ["state"] = "pending" }, 1));
        _ = mailbox.AckIds(Box(), ["acked-delivered", "acked-pending"], "test");

        var sweep = mailbox.LadderSweep(Box());
        Assert.Equal(new Dictionary<string, string> { ["acked-delivered"] = "delivered", ["unacked-withdrawn"] = "withdrawn" }, sweep.Settled);
        Assert.Equal(["acked-pending"], sweep.Open.Select(static e => e.Id));

        Assert.True(mailbox.AppendLadderRow(Box(), new JsonObject { ["id"] = "acked-pending", ["rung"] = 2, ["state"] = "delivered" }, 2));
        Assert.Equal("delivered", mailbox.LadderSweep(Box()).Settled["acked-pending"]);
        Assert.False(mailbox.LadderSweep(Box()).Settled.ContainsKey("bare"));
    }

    [Fact]
    public void TheNewestEntryMovesOnMailToTheBoxAndNotOnAHeartbeat()
    {
        Flip();
        var graph = Graph();
        Assert.Equal(0, graph.Mailbox.StoredNewestEntry(Box()));
        _ = Append(graph, Box(), Entry("e1"));
        var before = graph.Mailbox.StoredNewestEntry(Box());
        Assert.True(before > 0);

        var stamp = graph.Mailbox.StoredBoxStamp(Box());
        graph.Mailbox.WriteHeartbeat(Box(), "claude-mod", Session, 5000);
        Assert.Equal(stamp, graph.Mailbox.StoredBoxStamp(Box())); // a heartbeat leaves the mail counter (StateOp.LeavesSeq)
        Assert.Equal(before, graph.Mailbox.StoredNewestEntry(Box()));

        _ = Append(Graph(), Box(), Entry("e2"));
        Assert.True(graph.Mailbox.StoredNewestEntry(Box()) > before);
    }

    [Fact]
    public void TheStampMovesOnAWriteByAnotherProcess()
    {
        Flip();
        var graph = Graph();
        _ = Append(graph, Box(), Entry("e1"));
        var before = graph.Mailbox.StoredBoxStamp(Box());

        _ = Append(Graph(), Box(), Entry("e2"));

        Assert.NotEqual(before, graph.Mailbox.StoredBoxStamp(Box()));
        Assert.Equal(2, graph.Mailbox.ReadEntries(Box()).Entries.Count);
    }

    // ---------------------------------------------------------------- session marks

    private string Mark(string session = Session) => Path.Combine(Home, "session-marks", session);

    private void CutOverSessions()
    {
        _ = Directory.CreateDirectory(Path.Combine(Home, "registrations"));
        using var db = StateDbModule.Open(Home, mayCreate: true);
    }

    private JsonObject Registration(string session = Session, string box = "imp", long? pid = null) => new()
    {
        ["session_id"] = session,
        ["boxes"] = new JsonArray { new JsonObject { ["address"] = box + "@tm", ["dir"] = Box(box) } },
        ["pid"] = pid ?? Environment.ProcessId,
        ["bound"] = true,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARegisterPutsTheMarkDownAndAReplayPutsItBackAndUnregisterTakesItAway(bool database)
    {
        if (database)
        {
            CutOverSessions();
        }

        var graph = Graph();
        Assert.True(Flag(Ok(Run(graph, "register", Registration(), "op-reg")), "registered"));
        Assert.True(File.Exists(Mark()));
        Assert.Empty(File.ReadAllBytes(Mark()));

        File.Delete(Mark());
        Assert.True(Flag(Ok(Run(graph, "register", Registration(), "op-reg")), "registered"));
        Assert.True(File.Exists(Mark()));

        Assert.True(Flag(Ok(Run(graph, "unregister", new JsonObject { ["session_id"] = Session })), "removed"));
        Assert.False(File.Exists(Mark()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARegisterWhoseMarkCannotBeWrittenFailsAndStoresNothing(bool database)
    {
        if (database)
        {
            CutOverSessions();
        }

        File.WriteAllText(Path.Combine(Home, "session-marks"), "in the way");
        var graph = Graph();

        Assert.False(Run(graph, "register", Registration()).Ok);

        Assert.Null(graph.Mailbox.ReadRegistration(Session));
    }

    [Fact]
    public void NegativeAnIdThatIsNoNameGetsNoMark()
    {
        var graph = Graph();

        Assert.False(Flag(Ok(Run(graph, "register", Registration("../evil"))), "registered"));

        Assert.False(Directory.Exists(Path.Combine(Home, "session-marks")));
    }

    [Fact]
    public void AMarkThatCannotBeRemovedDoesNotFailTheUnregister()
    {
        var graph = Graph();
        Assert.True(Flag(Ok(Run(graph, "register", Registration())), "registered"));
        File.Delete(Mark());
        _ = Directory.CreateDirectory(Path.Combine(Mark(), "occupied"));

        Assert.True(Flag(Ok(Run(graph, "unregister", new JsonObject { ["session_id"] = Session })), "removed"));
    }

    // ---------------------------------------------------------------- the long poll

    private string _pidStart = string.Empty;

    /// <summary>The real process table, with this test process standing for the claude a route call is proved against.</summary>
    private sealed class SelfIsClaude(IProcessTable real) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => real.Ancestors(pid);

        public bool? HasAncestor(int pid, int candidate) => real.HasAncestor(pid, candidate);

        public string? StartTime(long pid) => real.StartTime(pid);

        public string? LocalStartTime(long pid) => real.LocalStartTime(pid);

        public string? Tty(int pid) => real.Tty(pid);

        public string? Command(int pid) => pid == Environment.ProcessId || real.HasAncestor(pid, Environment.ProcessId) == true ? "claude" : real.Command(pid);
    }

    private sealed class Routes : IRouteRegistry
    {
        internal Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> All { get; } = [];

        public void Add(string route, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) => All[route] = handler;
    }

    private static string DueInput()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return "{\"session\":\"" + Session + "\",\"now\":" + now + ",\"busy_at\":null,\"poll_ms\":5000,\"handed\":{},\"state\":null}";
    }

    /// <summary>A database holding the sessions and the mail, with this process registered on <paramref name="box"/>; every file gone, so only the rows can wake a request.</summary>
    private (ServiceGraph Graph, Func<int, Task<JsonValue>> Held) RowsOnlyDue(string box, Func<IMailDue, IMailDue>? wrap = null)
    {
        CutOverSessions();
        Flip();
        var graph = Graph();
        var env = new FakeEnv(_sandbox.Home, Home, new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = Home, ["AH_DAEMON"] = "off", ["AH_TEST"] = "1" });
        var table = new SelfIsClaude(PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()));
        var mailbox = MeshModule.CreateMailbox(graph.Io.Files, env, graph.Io.Clock, graph.Io.Random, graph.Hierarchy, graph.Roster, graph.Writer, table, null, StateDbModule.OpenSessionAuthority(Home), Store);
        var registration = Registration(box: box);
        registration["pid_start"] = table.StartTime(Environment.ProcessId) ?? string.Empty;
        Assert.True(Flag(Ok(Run(graph, "register", registration)), "registered"));
        File.Delete(Path.Combine(Home, "registrations", Session + ".json"));
        var routes = new Routes();
        var due = MeshModule.CreateMailDue(mailbox, MeshModule.CreateMailCore(graph.Io.Clock), graph.Io.Files, env);
        MeshModule.RegisterDueRoute(routes, wrap is null ? due : wrap(due), mailbox, graph.Io.Files);
        _pidStart = registration["pid_start"].AsString();
        return (graph, async wait =>
        {
            var response = await routes.All["GET /v1/due"](
                new RouteRequest("GET", "/v1/due", "in=" + Uri.EscapeDataString(DueInput()) + "&wait=" + wait.ToString(CultureInfo.InvariantCulture), new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), Environment.ProcessId),
                CancellationToken.None);
            Assert.Equal(200, response.Status);
            return JsonText.Parse(response.Body.ToArray());
        });
    }

    private static async Task StillHeld(Task<JsonValue> asked)
    {
        if (asked.IsCompleted)
        {
            Assert.Fail("answered early: " + JsonText.Stringify(await asked, 0));
        }
    }

    private static string[] Ids(JsonValue answer) => [.. answer.Member("ids")!.Value.AsArray().Select(static i => i.AsString())];

    [Fact]
    public async Task AHeldRequestWakesOnANewEntryFromAnotherProcess()
    {
        var (_, held) = RowsOnlyDue("imp");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var asked = held(10);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await StillHeld(asked);

        _ = Append(Graph(), Box(), Entry("e1"));

        var answer = await asked.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        Assert.Equal(["e1"], Ids(answer));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), clock.Elapsed.ToString());
    }

    /// <summary>The real decision, with <paramref name="after"/> run once right after the first computation has read the boxes.</summary>
    private sealed class AfterFirstCompute(IMailDue inner, Action after) : IMailDue
    {
        private int _done;

        public DueAnswer Compute(DueRequest request, IDueBoxSource? source = null)
        {
            var answer = inner.Compute(request, source);
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                after();
            }

            return answer;
        }

        public string RegistrationFile(string session) => inner.RegistrationFile(session);

        public DueRequest? ParseRequest(JsonValue input, out string? problem) => inner.ParseRequest(input, out problem);

        public JsonObject AnswerJson(DueAnswer answer, string? daemonSocket) => inner.AnswerJson(answer, daemonSocket);
    }

    [Fact]
    public async Task MailThatLandsWhileTheAnswerIsComputedStillWakesTheHeldRequest()
    {
        var (_, held) = RowsOnlyDue("imp", due => new AfterFirstCompute(due, () => _ = Append(Graph(), Box(), Entry("e1"))));
        // the route's longest hold, so an answer within the deadline below is the wake and not the hold running out
        var answer = await held(25).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(["e1"], Ids(answer));
    }

    [Fact]
    public async Task AHeldRequestWakesOnARegistrationChange()
    {
        var (graph, held) = RowsOnlyDue("imp");
        _ = Append(graph, Box("other"), Entry("e2"));
        // the route's longest hold, so an answer within the deadline below is the wake and not the hold running out
        var asked = held(25);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await StillHeld(asked);

        // the row alone, as a store with no exports will write it: no file changes for the old stat stamp to see
        var moved = new JsonObject
        {
            ["v"] = 1,
            ["session_id"] = Session,
            ["address"] = "other@tm",
            ["addresses"] = new JsonArray { "other@tm" },
            ["boxes"] = new JsonObject { ["other@tm"] = Box("other") },
            ["pid"] = Environment.ProcessId,
            ["pid_start"] = _pidStart,
            ["bound"] = true,
            ["at"] = "2026-11-04T00:00:00.000Z",
        };
        var written = StateDbModule.OpenSessionAuthority(Home).Register(
            Path.Combine(Home, "registrations", Session + ".json"), Session, _ => new SessionDecision(SessionVerdict.Write, Encoding.UTF8.GetBytes(JsonText.Stringify(moved, 0) + "\n")));
        Assert.True(written.Ok, written.Message);

        var answer = await asked.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(["e2"], Ids(answer));
    }

    [Fact]
    public async Task NegativeAChangeWithNothingDueLeavesTheRequestHeld()
    {
        var (_, held) = RowsOnlyDue("imp");
        // a 6 s hold: on a loaded machine the two delays below have outrun a 2 s one, and the hold then ended by itself
        var asked = held(6);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        _ = Append(Graph(), Box("other"), Entry("e9"));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        await StillHeld(asked);
        Assert.Empty(Ids(await asked.WaitAsync(TimeSpan.FromSeconds(14), TestContext.Current.CancellationToken)));
    }
}
