using System.Globalization;
using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class TranscriptPagerTests
{
    private const string Path = "/t/s.jsonl";

    private const string ListId = "list:transcript:d1/alice";

    private sealed class Source(TranscriptTail? tail, string? path, string? coldPath) : ITranscriptSource
    {
        public (IReadOnlyList<TranscriptEntry> Ring, string? Path) Held(string dirId, string subject) =>
            tail is null ? ([], null) : (tail.Entries, path);

        public string? ColdPath(string dirId, string subject) => coldPath;
    }

    private static string KeyOfNumber(int n) => "m" + n.ToString("000000", CultureInfo.InvariantCulture);

    private static string LineOf(int n, int pad = 0) =>
        "{\"type\":\"user\",\"message\":{\"content\":\"" + KeyOfNumber(n) + new string('x', pad) + "\"}}\n";

    private sealed class Rig : IPagerFixture
    {
        private readonly TranscriptFiles _files = new();
        private readonly bool _leased;
        private readonly int _pad;
        private TranscriptTail? _tail;
        private int _counter;

        public Rig(bool leased, int pad = 0)
        {
            _leased = leased;
            _pad = pad;
            Pager = new TranscriptPager(new Source(null, null, null), _files, new TestUsage(), () => 7);
        }

        public TranscriptPager Pager { get; private set; }

        public TranscriptFiles Files => _files;

        public IReadOnlyList<string> Seed(int count)
        {
            _files.Put(Path, []);
            _counter = 0;
            _tail = new TranscriptTail(_files, new TestUsage());
            Pager = new TranscriptPager(new Source(_leased ? _tail : null, _leased ? Path : null, Path), _files, new TestUsage(), () => 7);
            return AppendNewer(count);
        }

        public IReadOnlyList<string> AppendNewer(int count)
        {
            var added = Enumerable.Range(_counter + 1, count).ToList();
            _files.Append(Path, string.Concat(added.Select(n => LineOf(n, _pad))));
            _counter += count;
            _ = _tail!.Advance(Path);
            return [.. added.Select(KeyOfNumber).Reverse()];
        }

        public async Task<ListPage> PageAsync(string? after, string? before, int limit) =>
            Assert.IsType<ListResult.Found>(await Pager.PageAsync(new ListRequest(ListId, after, before, limit, new Dictionary<string, string>()), CancellationToken.None)).Page;

        public long PublishCounter => 7;

        public string KeyOf(JsonValue row) => row.Member("blocks")!.Value.AsArray()[0].Member("excerpt")!.Value.AsString()[..7];
    }

    [Fact]
    public Task RingVersionIsThePublishCounter() => PagerContract.VersionIsThePublishCounterOfTheStateRead(new Rig(leased: true));

    [Fact]
    public Task ColdVersionIsThePublishCounter() => PagerContract.VersionIsThePublishCounterOfTheStateRead(new Rig(leased: false));

    [Fact]
    public Task RingBeforeAndAfterRulesHold() => PagerContract.BeforePageIsNewestFirstAndAfterPageIsOlder(new Rig(leased: true));

    [Fact]
    public Task ColdBeforeAndAfterRulesHold() => PagerContract.BeforePageIsNewestFirstAndAfterPageIsOlder(new Rig(leased: false));

    [Fact]
    public Task RingNextWalksToTheOldest() => PagerContract.NextWalksToTheOldestRowOnceEachAndEndsWithNull(new Rig(leased: true));

    [Fact]
    public Task ColdNextWalksToTheOldest() => PagerContract.NextWalksToTheOldestRowOnceEachAndEndsWithNull(new Rig(leased: false));

    [Fact]
    public Task RingCatchUpGivesEveryNewRowOnce() => PagerContract.CatchingUpAfterMoreThanALimitOfAppendsGivesEveryNewRowOnce(new Rig(leased: true));

    [Fact]
    public Task ColdCatchUpGivesEveryNewRowOnce() => PagerContract.CatchingUpAfterMoreThanALimitOfAppendsGivesEveryNewRowOnce(new Rig(leased: false));

    [Fact]
    public Task RingPrevAtTheHeadAndEmptyBefore() => PagerContract.PrevIsTheHeadCursorAtTheHeadAndBeforeItIsEmptyWithPrevEchoed(new Rig(leased: true));

    [Fact]
    public Task ColdPrevAtTheHeadAndEmptyBefore() => PagerContract.PrevIsTheHeadCursorAtTheHeadAndBeforeItIsEmptyWithPrevEchoed(new Rig(leased: false));

    [Fact]
    public Task RingEmptyAfterTheOldestEchoesNext() => PagerContract.AfterTheOldestRowIsEmptyWithNextEchoed(new Rig(leased: true));

    [Fact]
    public Task ColdEmptyAfterTheOldestEchoesNext() => PagerContract.AfterTheOldestRowIsEmptyWithNextEchoed(new Rig(leased: false));

    [Fact]
    public Task RingEmptyFileHasNoCursors() => PagerContract.AnEmptyListHasNoCursors(new Rig(leased: true));

    [Fact]
    public Task ColdEmptyFileHasNoCursors() => PagerContract.AnEmptyListHasNoCursors(new Rig(leased: false));

    [Fact]
    public Task RingHeadAndTail() => PagerContract.HeadAndTailAreTheNewestAndOldestRowsOfThePage(new Rig(leased: true));

    [Fact]
    public Task ColdHeadAndTail() => PagerContract.HeadAndTailAreTheNewestAndOldestRowsOfThePage(new Rig(leased: false));

    private static string Describe(JsonValue row) => row.Member("type")!.Value.AsString() == "oversize" ? "stub" : row.Member("blocks")!.Value.AsArray()[0].Member("excerpt")!.Value.AsString()[..7];

    private static Rig RigWithAGiantLine(int giantPad)
    {
        var rig = new Rig(leased: false);
        _ = rig.Seed(3);
        rig.Files.Append(Path, LineOf(99, giantPad));
        _ = rig.AppendNewer(2);
        return rig;
    }

    [Fact]
    public async Task ALineOverTheCapIsOneStubInTheOlderWalkAndEverythingBeyondItIsStillReachedEachOnce()
    {
        var rig = RigWithAGiantLine(5 * 1024 * 1024);
        var seen = new List<string>();
        string? after = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await rig.PageAsync(after, null, 2);
            seen.AddRange(page.Rows.Select(Describe));
            if (page.Next is null)
            {
                break;
            }

            after = page.Next;
        }

        Assert.Equal(["m000005", "m000004", "stub", "m000003", "m000002", "m000001"], seen);
    }

    [Fact]
    public async Task TheStubCarriesItsOffsetAndByteCountAndNothingElse()
    {
        var rig = RigWithAGiantLine(5 * 1024 * 1024);
        var page = await rig.PageAsync(null, null, 3);

        var stub = page.Rows[2];

        Assert.Equal("oversize", stub.Member("type")!.Value.AsString());
        Assert.Equal(LineOf(1).Length + LineOf(2).Length + LineOf(3).Length, (int)OffsetOf(stub));
        Assert.True(stub.Member("bytes")!.Value.AsNumber() > 5 * 1024 * 1024);
        Assert.Null(stub.Member("blocks"));
    }

    [Fact]
    public async Task ALineOverTheCapIsOneStubInTheNewerWalkToo()
    {
        var rig = RigWithAGiantLine(5 * 1024 * 1024);
        var oldest = await rig.PageAsync(null, null, 6);
        var m3 = ListCursor.Encode(OffsetOf(oldest.Rows[3]).ToString(CultureInfo.InvariantCulture));
        var seen = new List<string>();
        string? before = m3;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await rig.PageAsync(null, before, 1);
            if (page.Rows.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Rows.Select(Describe));
            before = page.Prev;
        }

        Assert.Equal(["stub", "m000004", "m000005"], seen);
    }

    [Fact]
    public async Task ALineJustUnderTheCapIsAnOrdinaryEntryNotAStub()
    {
        var rig = RigWithAGiantLine((3 * 1024 * 1024) + 1000);

        var page = await rig.PageAsync(null, null, 6);

        Assert.DoesNotContain("oversize", page.Rows.Select(static r => r.Member("type")!.Value.AsString()));
        Assert.Equal(6, page.Rows.Count);
    }

    private static long OffsetOf(JsonValue row) => (long)row.Member("offset")!.Value.AsNumber();

    private static async Task<List<ListPage>> WalkAsync(Rig rig, int limit)
    {
        var pages = new List<ListPage>();
        string? after = null;
        do
        {
            var page = await rig.PageAsync(after, null, limit);
            pages.Add(page);
            after = page.Next;
        }
        while (after is not null && pages.Count < 100);

        return pages;
    }

    [Fact]
    public async Task OffsetsStrictlyDescendAcrossNextPagesPastTheRingAndTheOlderPagesReadTheFile()
    {
        var rig = new Rig(leased: true);
        var seeded = rig.Seed(TranscriptTail.RingSize + 100);

        var pages = await WalkAsync(rig, 200);

        var offsets = pages.SelectMany(static p => p.Rows.Select(OffsetOf)).ToList();
        Assert.Equal(seeded.Count, offsets.Count);
        Assert.Equal(offsets.OrderByDescending(static o => o).ToList(), offsets);
        Assert.Equal(offsets.Count, offsets.Distinct().Count());
        Assert.Equal(seeded, pages.SelectMany(p => p.Rows.Select(rig.KeyOf)));
        Assert.Equal(0, pages[0].ColdReads);
        Assert.True(pages[^1].ColdReads > 0);
        Assert.All(pages, static p => Assert.Null(p.Total));
    }

    [Fact]
    public async Task AColdKeyReadsBackwardsThroughManyWindowsAndMakesNoRing()
    {
        var rig = new Rig(leased: false, pad: 400);
        var seeded = rig.Seed(2000);

        var pages = await WalkAsync(rig, 150);

        Assert.Equal(seeded, pages.SelectMany(p => p.Rows.Select(rig.KeyOf)));
        Assert.All(pages, static p => Assert.True(p.ColdReads >= 1));
        Assert.True(rig.Files.Ranges + rig.Files.Tails >= 4, "the file is several windows long");
        Assert.All(pages, static p => Assert.Null(p.Total));
    }

    [Fact]
    public async Task ABeforePageFromBelowTheRingReadsForwardAndJoinsTheRing()
    {
        var rig = new Rig(leased: true);
        _ = rig.Seed(TranscriptTail.RingSize + 100);
        var all = (await WalkAsync(rig, 200)).SelectMany(static p => p.Rows).ToList();
        string CursorOf(int fromOldest) => ListCursor.Encode(OffsetOf(all[^fromOldest]).ToString(CultureInfo.InvariantCulture));

        var belowRing = await rig.PageAsync(null, CursorOf(5), 5);
        var acrossTheBoundary = await rig.PageAsync(null, CursorOf(98), 5);

        Assert.Equal(Enumerable.Range(6, 5).Reverse().Select(n => KeyOfNumber(n)), belowRing.Rows.Select(rig.KeyOf));
        Assert.True(belowRing.ColdReads > 0);
        Assert.Equal(Enumerable.Range(99, 5).Reverse().Select(n => KeyOfNumber(n)), acrossTheBoundary.Rows.Select(rig.KeyOf));
    }

    [Fact]
    public async Task ThePageOfAHeldKeyReadsNoFile()
    {
        var rig = new Rig(leased: true);
        _ = rig.Seed(50);
        var before = rig.Files.Ranges + rig.Files.Tails;

        var page = await rig.PageAsync(null, null, 20);

        Assert.Equal(0, page.ColdReads);
        Assert.Equal(before, rig.Files.Ranges + rig.Files.Tails);
        Assert.Equal(7, page.Version);
    }

    [Fact]
    public async Task AKeyWithNoPathIsNotFoundAndABadKeyOrCursorIsRejected()
    {
        var pager = new TranscriptPager(new Source(null, null, null), new TranscriptFiles(), new TestUsage(), () => 1);
        var request = new ListRequest(ListId, null, null, 5, new Dictionary<string, string>());
        var rig = new Rig(leased: false);
        _ = rig.Seed(5);

        Assert.IsType<ListResult.NotFound>(await pager.PageAsync(request, CancellationToken.None));
        Assert.IsType<ListResult.NotFound>(await rig.Pager.PageAsync(request with { SliceId = "list:transcript:nokeyslash" }, CancellationToken.None));
        Assert.IsType<ListResult.NotFound>(await rig.Pager.PageAsync(request with { SliceId = "list:transcript:d1/" }, CancellationToken.None));
        Assert.Equal("bad_cursor", Assert.IsType<ListResult.Invalid>(await rig.Pager.PageAsync(request with { After = "!!" }, CancellationToken.None)).Reason);
        var middle = ListCursor.Encode("3");
        Assert.Equal("bad_cursor", Assert.IsType<ListResult.Invalid>(await rig.Pager.PageAsync(request with { After = middle }, CancellationToken.None)).Reason);
    }

    private static string TurnLines() =>
        "{\"type\":\"user\",\"message\":{\"content\":\"go\"}}\n"
        + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n"
        + "{\"type\":\"system\",\"subtype\":\"turn_duration\",\"durationMs\":1500}\n"
        + "{\"type\":\"user\",\"message\":{\"content\":\"again\"}}\n"
        + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n";

    [Fact]
    public async Task TurnMsComesFromTheNextTurnDurationInTheWindowAndIsNullWhenTheWindowHoldsNone()
    {
        var files = new TranscriptFiles();
        files.Put(Path, Encoding.UTF8.GetBytes(TurnLines()));
        var tail = new TranscriptTail(files, new TestUsage());
        _ = tail.Advance(Path);
        var pager = new TranscriptPager(new Source(tail, Path, Path), files, new TestUsage(), () => 1);

        var page = Assert.IsType<ListResult.Found>(await pager.PageAsync(new ListRequest(ListId, null, null, 10, new Dictionary<string, string>()), CancellationToken.None)).Page;

        var starts = page.Rows.Where(static r => r.Member("turn_start") is { IsBool: true }).ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(JsonKind.Null, starts[0].Member("turn_ms")!.Value.Kind);
        Assert.Equal(1500, starts[1].Member("turn_ms")!.Value.AsNumber());
        Assert.DoesNotContain(page.Rows, static r => r.Member("type")!.Value.AsString() == "assistant" && r.Member("turn_ms") is not null);
    }

    private static JsonValue Row(string line) =>
        new TranscriptParser(new TestUsage()).Parse(Encoding.UTF8.GetBytes(line), 0, line.Length).ToJson();

    private static string Rows(string line) => JsonText.Stringify(Row(line).Member("rows")!.Value, 0);

    [Fact]
    public void AnEntrysRowsNameEachBlockAndNegativeNeverCopyItsText()
    {
        Assert.Equal("""[{"index":0,"kind":"turn","cut":false}]""", Rows("""{"type":"user","message":{"content":"go"}}"""));
        Assert.Equal("""[{"index":0,"kind":"brief","cut":false}]""", Rows("""{"type":"user","message":{"content":"[hierarchy-msg x] go"}}"""));
        Assert.Equal(
            """[{"index":0,"kind":"thinking"},{"index":1,"kind":"tool","id":"u1"},{"index":2,"kind":"text","cut":false}]""",
            Rows("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"},{"type":"tool_use","id":"u1","name":"Read","input":{}},{"type":"text","text":"[hierarchy-msg x] done"}]}}"""));
        Assert.Equal(
            """[{"index":0,"kind":"result","tool_use_id":"u1","error":true,"lines":1}]""",
            Rows("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"u1","is_error":true,"content":"a\nb"}]}}"""));
        Assert.Equal("""[{"kind":"large"}]""", JsonText.Stringify(TranscriptEntry.Stub(0, 9).ToJson().Member("rows")!.Value, 0));
    }

    [Theory]
    [InlineData("x", 2048, false)]
    [InlineData("x", 2049, true)]
    [InlineData("x", 2041, false)]
    [InlineData("é", 1024, false)]
    [InlineData("é", 1025, true)]
    public void ATextIsCutOnlyWhenItWasLongerThanTheCap(string character, int count, bool cut)
    {
        var text = string.Concat(Enumerable.Repeat(character, count));
        Assert.Contains($"\"cut\":{(cut ? "true" : "false")}", Rows("{\"type\":\"user\",\"message\":{\"content\":\"" + text + "\"}}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageOverSeveralLinesCountsOnceAndNegativeAnotherMessageWithTheSameUsageCountsAgain()
    {
        static string Said(string id) => "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"usage\":{\"input_tokens\":10,\"output_tokens\":1},\"content\":[]}}\n";
        var lines = "{\"type\":\"user\",\"message\":{\"content\":\"one\"}}\n" + Said("m1") + Said("m1") + Said("m2")
            + "{\"type\":\"system\",\"subtype\":\"turn_duration\",\"durationMs\":9}\n";
        var files = new TranscriptFiles();
        files.Put(Path, Encoding.UTF8.GetBytes(lines));
        var tail = new TranscriptTail(files, new TestUsage());
        _ = tail.Advance(Path);
        var pager = new TranscriptPager(new Source(tail, Path, Path), files, new TestUsage(), () => 1);
        var page = Assert.IsType<ListResult.Found>(await pager.PageAsync(new ListRequest(ListId, null, null, 20, new Dictionary<string, string>()), CancellationToken.None)).Page;

        Assert.Equal(22, page.Rows.Single(static r => r.Member("turn_start") is not null).Member("turn_tokens")!.Value.AsNumber());
    }

    [Fact]
    public async Task TurnTokensAddUpTheTurnAndAreNullWhenTheWindowStopsBeforeItEndsAndNegativeOnlyATurnStartHasThem()
    {
        static string Said(int input, int output) =>
            "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":" + input.ToString(CultureInfo.InvariantCulture) + ",\"output_tokens\":" + output.ToString(CultureInfo.InvariantCulture) + ",\"cache_read_input_tokens\":999},\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n";
        var lines = "{\"type\":\"user\",\"message\":{\"content\":\"one\"}}\n" + Said(10, 1) + Said(20, 2)
            + "{\"type\":\"user\",\"message\":{\"content\":\"two\"}}\n" + Said(5, 5) + "{\"type\":\"system\",\"subtype\":\"turn_duration\",\"durationMs\":9}\n"
            + "{\"type\":\"user\",\"message\":{\"content\":\"three\"}}\n" + Said(1, 1);
        var files = new TranscriptFiles();
        files.Put(Path, Encoding.UTF8.GetBytes(lines));
        var tail = new TranscriptTail(files, new TestUsage());
        _ = tail.Advance(Path);
        var pager = new TranscriptPager(new Source(tail, Path, Path), files, new TestUsage(), () => 1);
        var page = Assert.IsType<ListResult.Found>(await pager.PageAsync(new ListRequest(ListId, null, null, 20, new Dictionary<string, string>()), CancellationToken.None)).Page;

        var starts = page.Rows.Where(static r => r.Member("turn_start") is not null).Reverse().Select(static r => r.Member("turn_tokens")!.Value).ToList();
        Assert.Equal(33, starts[0].AsNumber());
        Assert.Equal(10, starts[1].AsNumber());
        Assert.Equal(JsonKind.Null, starts[2].Kind);
        Assert.DoesNotContain(page.Rows, static r => r.Member("type")!.Value.AsString() != "user" && r.Member("turn_tokens") is not null);
    }

    [Theory]
    [InlineData("Edit", """{"old_string":"a\nb","new_string":"c"}""", """[{"kind":"del","text":"a"},{"kind":"del","text":"b"},{"kind":"add","text":"c"}]""")]
    [InlineData("Write", """{"content":"x\ny"}""", """[{"kind":"add","text":"x"},{"kind":"add","text":"y"}]""")]
    [InlineData("MultiEdit", """{"edits":[{"old_string":"a","new_string":"b"},{"old_string":"c","new_string":""}]}""", """[{"kind":"del","text":"a"},{"kind":"add","text":"b"},{"kind":"del","text":"c"}]""")]
    [InlineData("Edit", """{"old_string":7}""", "[]")]
    public void TheDiffLinesOfAnEditToolAreRemovedBeforeAddedPerEdit(string tool, string input, string lines) =>
        Assert.Equal(lines, JsonText.Stringify(JsonValue.FromArray(TranscriptParser.DiffLinesOf(tool, JsonText.Parse(Encoding.UTF8.GetBytes(input)))!), 0));

    [Fact]
    public async Task ExpandServesDiffLinesOnAnEditAndNegativeNoneOnAToolThatEditsNothing()
    {
        var files = new TranscriptFiles();
        files.Put(Path, Encoding.UTF8.GetBytes("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u1","name":"Edit","input":{"old_string":"a","new_string":"b"}},{"type":"tool_use","id":"u2","name":"Read","input":{"old_string":"a"}}]}}""" + "\n"));
        var pager = new TranscriptPager(new Source(null, null, Path), files, new TestUsage(), () => 1);
        var blocks = Assert.IsType<ListResult.Document>(await ExpandAsync(pager, "0")).Body.Member("blocks")!.Value.AsArray().ToList();
        Assert.Equal("""[{"kind":"del","text":"a"},{"kind":"add","text":"b"}]""", JsonText.Stringify(blocks[0].Member("diff")!.Value, 0));
        Assert.Null(blocks[1].Member("diff"));
    }

    private static async Task<ListResult> ExpandAsync(TranscriptPager pager, string offset) =>
        await pager.PageAsync(new ListRequest(ListId, null, null, 1, new Dictionary<string, string> { ["expand"] = offset }), CancellationToken.None);

    [Fact]
    public async Task ExpandReturnsTheFullBlocksOfOneEntry()
    {
        var files = new TranscriptFiles();
        var first = "{\"type\":\"user\",\"message\":{\"content\":\"hello\"}}\n";
        var second = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"},{\"type\":\"tool_use\",\"id\":\"u1\",\"name\":\"Edit\",\"input\":{\"file\":\"a\",\"new_string\":\"b\"}},{\"type\":\"text\",\"text\":\"done\"}]}}\n";
        var third = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"u1\",\"is_error\":true,\"content\":\"boom\\nline2\"}]}}\n";
        files.Put(Path, Encoding.UTF8.GetBytes(first + second + third));
        var pager = new TranscriptPager(new Source(null, null, Path), files, new TestUsage(), () => 1);

        var assistant = Assert.IsType<ListResult.Document>(await ExpandAsync(pager, first.Length.ToString(CultureInfo.InvariantCulture))).Body;
        var result = Assert.IsType<ListResult.Document>(await ExpandAsync(pager, (first.Length + second.Length).ToString(CultureInfo.InvariantCulture))).Body;
        var head = Assert.IsType<ListResult.Document>(await ExpandAsync(pager, "0")).Body;

        Assert.Equal("assistant", assistant.Member("type")!.Value.AsString());
        Assert.False(assistant.Member("truncated")!.Value.AsBool());
        var blocks = assistant.Member("blocks")!.Value.AsArray().ToList();
        Assert.Equal(["thinking", "tool_use", "text"], blocks.Select(static b => b.Member("kind")!.Value.AsString()));
        Assert.Equal("hmm", blocks[0].Member("text")!.Value.AsString());
        Assert.Equal("b", blocks[1].Member("input")!.Value.Member("new_string")!.Value.AsString());
        Assert.Equal("done", blocks[2].Member("text")!.Value.AsString());
        var toolResult = result.Member("blocks")!.Value.AsArray().Single();
        Assert.True(toolResult.Member("is_error")!.Value.AsBool());
        Assert.Equal("boom\nline2", toolResult.Member("content")!.Value.AsString());
        Assert.Equal("hello", head.Member("blocks")!.Value.AsArray().Single().Member("text")!.Value.AsString());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("1")]
    [InlineData("99999")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task AnOffsetThatIsNotTheStartOfAnEntryIs400BadOffset(string offset)
    {
        var files = new TranscriptFiles();
        files.Put(Path, Encoding.UTF8.GetBytes(LineOf(1) + LineOf(2)));
        var pager = new TranscriptPager(new Source(null, null, Path), files, new TestUsage(), () => 1);

        var result = await ExpandAsync(pager, offset);

        Assert.Equal("bad_offset", Assert.IsType<ListResult.Invalid>(result).Reason);
    }

    [Fact]
    public async Task AnEntryOverTheCapIsCutAtTheCapAndFlaggedTruncatedWhileOneUnderItIsWhole()
    {
        var files = new TranscriptFiles();
        var small = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('a', 100_000) + "\"}}\n";
        var big = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('b', 300_000) + "\"}}\n";
        files.Put(Path, Encoding.UTF8.GetBytes(small + big));
        var pager = new TranscriptPager(new Source(null, null, Path), files, new TestUsage(), () => 1);

        var whole = Assert.IsType<ListResult.Document>(await ExpandAsync(pager, "0")).Body;
        var cut = Assert.IsType<ListResult.Document>(await ExpandAsync(pager, small.Length.ToString(CultureInfo.InvariantCulture))).Body;

        Assert.False(whole.Member("truncated")!.Value.AsBool());
        Assert.Equal(100_000, whole.Member("blocks")!.Value.AsArray().Single().Member("text")!.Value.AsString().Length);
        Assert.True(cut.Member("truncated")!.Value.AsBool());
        Assert.Empty(cut.Member("blocks")!.Value.AsArray());
        Assert.True(files.MaxRequest <= TranscriptPager.ExpandCap + 2);
    }

    [Fact]
    public async Task ExpandOfAKeyWithNoPathIsNotFound()
    {
        var pager = new TranscriptPager(new Source(null, null, null), new TranscriptFiles(), new TestUsage(), () => 1);

        Assert.IsType<ListResult.NotFound>(await ExpandAsync(pager, "0"));
    }
}
