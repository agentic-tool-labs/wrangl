using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class TranscriptLaneTests
{
    private static readonly string[] BodyKeys = ["path_ok", "length", "newest_offset", "oldest_held_offset", "turn_usage", "resets", "as_of"];
    private static readonly string[] UsageNames = ["input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"];
    private const string Dir = "/h/.ah";
    private const string File = "/h/.claude/projects/p/s.jsonl";
    private const string Subject = "sess1";

    private sealed class Reader(Func<JsonValue> record) : IViewStateReader
    {
        public int Reads { get; private set; }

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            Reads++;
            return record();
        }
    }

    private sealed class Clock
    {
        public long Now { get; set; } = 1_000_000;

        public long Read() => Now;
    }

    private sealed class Rig
    {
        public TranscriptFiles Files { get; } = new();

        public bool Subscribed { get; set; } = true;

        public string? Valid { get; set; } = File;

        public List<string> Resets { get; } = [];

        public int RootChanges { get; private set; }

        public Action? RootsHook { get; set; }

        public string? TranscriptPath { get; set; } = File;

        public string TurnAt { get; set; } = "2026-01-01T00:00:10Z";

        public bool PathUnderExtra { get; set; }

        public bool NoActivityRecord { get; set; }

        public HashSet<string> Owned { get; } = [Subject];

        public Reader State { get; }

        public TranscriptLane Lane { get; }

        public Rig()
        {
            // The activity writer spreads its extra fields into the record, so a stored record carries transcript_path at the top level.
            State = new Reader(() =>
            {
                if (NoActivityRecord)
                {
                    return JsonValue.Null;
                }

                var path = TranscriptPath is null ? JsonValue.Null : JsonValue.FromString(TranscriptPath);
                var record = new JsonObject { ["turn_at"] = TurnAt };
                if (PathUnderExtra)
                {
                    record["extra"] = JsonValue.FromObject(new JsonObject { ["transcript_path"] = path });
                }
                else
                {
                    record["transcript_path"] = path;
                }

                return JsonValue.FromObject(record);
            });
            Lane = new TranscriptLane(State, Files, new TestUsage(), p => p == Valid ? Valid : null, () => [Dir], _ => Subscribed, Resets.Add, () =>
            {
                RootChanges++;
                RootsHook?.Invoke();
            }, "/h/.claude/projects", (_, subject) => Owned.Contains(subject));
            Files.Put(File, Encoding.UTF8.GetBytes(TranscriptTailTests.User("one") + TranscriptTailTests.Assistant(1, 2, "2026-01-01T00:00:10Z")));
        }

        public static string Key => ActivityLane.KeyOf(Dir, Subject);

        public static string Id => TranscriptLane.SliceIdOf(Dir, Subject);

        public static JsonValue Body(LaneResult r) => JsonText.Parse(r.Changes.Single().Body!);
    }

    [Fact]
    public void AnUnsubscribedKeyOpensNothingAndEvictsItsSlice()
    {
        var rig = new Rig { Subscribed = false };

        var result = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal([Rig.Id], result.Evict);
        Assert.Empty(result.Changes);
        Assert.Equal((0, 0, 0), (rig.Files.Tails, rig.Files.Ranges, rig.State.Reads));
    }

    [Fact]
    public void ASubscribedKeyWithAValidPathPublishesTheWhitelistedBody()
    {
        var rig = new Rig();

        var body = Rig.Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.Equal(BodyKeys.Order(StringComparer.Ordinal), body.AsObject().Keys.Order(StringComparer.Ordinal));
        Assert.True(body.Member("path_ok")!.Value.AsBool());
        Assert.Equal(0, body.Member("oldest_held_offset")!.Value.AsNumber());
        Assert.Equal(body.Member("length")!.Value.AsNumber(), body.Member("newest_offset")!.Value.AsNumber());
        var usage = body.Member("turn_usage")!.Value;
        Assert.Equal([1, 2, 3, 4], UsageNames.Select(n => (int)usage.Member(n)!.Value.AsNumber()));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:10Z").ToUnixTimeMilliseconds(), (long)body.Member("as_of")!.Value.AsNumber());
    }

    [Fact]
    public void WithoutAValidPathTheSliceSaysSoAndNoFileIsOpened()
    {
        var rig = new Rig { Valid = "/elsewhere/s.jsonl" };

        var body = Rig.Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.False(body.Member("path_ok")!.Value.AsBool());
        Assert.Equal((0, 0, 0), (rig.Files.Tails, rig.Files.Ranges, rig.Files.WholeReads));
        rig.TranscriptPath = null;
        Assert.False(Rig.Body(rig.Lane.Reread(Rig.Key, 0)).Member("path_ok")!.Value.AsBool());
        Assert.Equal((0, 0), (rig.Files.Tails, rig.Files.Ranges));
    }

    [Fact]
    public void ThePathIsTheStoredRecordsTopLevelFieldNotOneUnderExtra()
    {
        var rig = new Rig { PathUnderExtra = true };

        var body = Rig.Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.False(body.Member("path_ok")!.Value.AsBool());
        Assert.Equal((0, 0), (rig.Files.Tails, rig.Files.Ranges));
    }

    private const string OtherProjectFile = "/h/.claude/projects/other/" + Subject + ".jsonl";

    [Fact]
    public void ASessionWithNoActivityRecordInTheTeamsDirectoryIsFoundByItsIdUnderTheProjects()
    {
        var rig = new Rig { NoActivityRecord = true, Valid = OtherProjectFile };
        rig.Files.Put(OtherProjectFile, Encoding.UTF8.GetBytes(TranscriptTailTests.User("one")));

        var body = Rig.Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.True(body.Member("path_ok")!.Value.AsBool());
        Assert.Equal(OtherProjectFile, rig.Lane.ColdPath(ActivityLane.DirId(Dir), Subject));
    }

    [Fact]
    public void ASessionWithNoRecordAndNoTranscriptFileEndsItsSlice()
    {
        var rig = new Rig { NoActivityRecord = true, Valid = OtherProjectFile };

        var result = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal([Rig.Id], result.Evict);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void ASessionTheTeamDoesNotOwnIsNeverLookedUpEvenWhenItsFileExistsUnderTheProjects()
    {
        var rig = new Rig { NoActivityRecord = true, Valid = OtherProjectFile };
        rig.Files.Put(OtherProjectFile, Encoding.UTF8.GetBytes(TranscriptTailTests.User("one")));
        rig.Owned.Clear();

        var result = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal([Rig.Id], result.Evict);
        Assert.Null(rig.Lane.ColdPath(ActivityLane.DirId(Dir), Subject));
        Assert.Equal(0, rig.Files.Lists);
        Assert.Equal((0, 0), (rig.Files.Tails, rig.Files.Ranges));
    }

    [Fact]
    public void AFoundPathIsKeptAndAMissIsKeptUntilTheProjectsDirectoryChanges()
    {
        var rig = new Rig { NoActivityRecord = true, Valid = OtherProjectFile };

        Assert.Null(rig.Lane.ColdPath(ActivityLane.DirId(Dir), Subject));
        Assert.Null(rig.Lane.ColdPath(ActivityLane.DirId(Dir), Subject));
        var scansAfterMisses = rig.Files.Lists;
        Assert.Equal(1, scansAfterMisses);

        rig.Files.Put(OtherProjectFile, Encoding.UTF8.GetBytes(TranscriptTailTests.User("one")));
        Assert.Equal(OtherProjectFile, rig.Lane.ColdPath(ActivityLane.DirId(Dir), Subject));
        Assert.Equal(OtherProjectFile, rig.Lane.ColdPath(ActivityLane.DirId(Dir), Subject));
        Assert.Equal(scansAfterMisses + 1, rig.Files.Lists);
    }

    private static Slice Row(string id, string body) => new(id, 1, 0, Encoding.UTF8.GetBytes(body), "h");

    [Theory]
    [InlineData("member:d1/team/dev", "s-1", true)]
    [InlineData("member:d1/team/dev", "s-2", false)]
    [InlineData("member:d2/team/dev", "s-1", false)]
    [InlineData("team:d1/team", "s-1", false)]
    public void ATreeRowOwnsTheSessionItNamesOnlyInItsOwnDirectory(string rowId, string subject, bool owned)
    {
        Slice[] slices = [Row(rowId, "{\"session\":\"s-1\"}"), Row("member:d1/team/idle", "{\"session\":null}")];

        Assert.Equal(owned, TranscriptLane.TreeRowOwns(slices, "d1", subject));
    }

    [Fact]
    public void AFoundFileTheValidatorRefusesIsNotServed()
    {
        var rig = new Rig { NoActivityRecord = true, Valid = "/elsewhere/s.jsonl" };
        rig.Files.Put(OtherProjectFile, Encoding.UTF8.GetBytes(TranscriptTailTests.User("one")));

        var result = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal([Rig.Id], result.Evict);
        Assert.Equal((0, 0), (rig.Files.Tails, rig.Files.Ranges));
        Assert.Null(rig.Lane.ColdPath(ActivityLane.DirId(Dir), Subject));
    }

    [Fact]
    public void AHintReadsExactlyOneRangeAndAnAppendShowsInTheRing()
    {
        var rig = new Rig();
        _ = rig.Lane.Reread(Rig.Key, 0);
        Assert.Equal((1, 0), (rig.Files.Tails, rig.Files.Ranges));

        rig.Files.Append(File, TranscriptTailTests.User("two"));
        _ = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal((1, 1), (rig.Files.Tails, rig.Files.Ranges));
        Assert.Equal(3, rig.Lane.EntriesOf(Rig.Key).Count);
    }

    [Fact]
    public void AShrinkIsCountedWithItsReasonAndTheBodyCarriesTheResetCount()
    {
        var rig = new Rig();
        _ = rig.Lane.Reread(Rig.Key, 0);

        rig.Files.Put(File, Encoding.UTF8.GetBytes(TranscriptTailTests.User("x")));
        var body = Rig.Body(rig.Lane.Reread(Rig.Key, 0));

        Assert.Equal(["shrink"], rig.Resets);
        Assert.Equal(1, body.Member("resets")!.Value.AsNumber());
    }

    [Fact]
    public void ThePathRowClassifiesTheHeldFileIgnoresItsSiblingsAndLeavesOtherPathsAlone()
    {
        var rig = new Rig();
        _ = rig.Lane.Reread(Rig.Key, 0);
        var row = rig.Lane.Row();

        Assert.Equal(PathClass.Classified, row(File, out var lane, out var key));
        Assert.Equal(("transcript", Rig.Key), (lane, key));
        Assert.Equal(PathClass.Ignored, row("/h/.claude/projects/p/other.jsonl", out _, out _));
        Assert.Null(row("/h/.claude/projects/q/s.jsonl", out _, out _));
        Assert.Null(row("/h/.claude/projects/p/s.json", out _, out _));
        Assert.Equal(["/h/.claude/projects/p"], rig.Lane.LeasedDirs());
    }

    [Fact]
    public void AnUnsubscribedKeyIsNoLongerWatchedOrClassified()
    {
        var rig = new Rig();
        _ = rig.Lane.Reread(Rig.Key, 0);
        Assert.Equal(1, rig.RootChanges);

        rig.Subscribed = false;
        _ = rig.Lane.Reread(Rig.Key, 0);

        Assert.Equal(2, rig.RootChanges);
        Assert.Empty(rig.Lane.LeasedDirs());
        Assert.Null(rig.Lane.Row()(File, out _, out _));
        Assert.Empty(rig.Lane.Keys());
    }

    [Fact]
    public void ALeaseStartPublishesTheSliceAndTheLapseEvictsItAndStopsAllReads()
    {
        var rig = new Rig();
        var clock = new Clock();
        ReadModel? holder = null;
        var lane = new TranscriptLane(rig.State, rig.Files, new TestUsage(), p => p == File ? File : null, () => [Dir], id => holder!.Leases.IsSubscribed(id));
        var paths = new PathLaneTable(() => [Dir]);
        paths.Register("transcript", lane.Row());
        using var model = new ReadModel([lane], new ModelOptions { SeedAtStart = false }, clock.Read, null, "e", paths);
        holder = model;

        var lease = model.Leases.Hold([Rig.Id]);
        Drain(model, clock);

        Assert.Contains(Rig.Id, model.Store.Current.Slices.Keys);
        Assert.Equal(1, rig.Files.Tails);

        lease.Dispose();
        clock.Now += model.Leases.LapseMs + 1;
        model.Tick();
        Drain(model, clock);

        Assert.DoesNotContain(Rig.Id, model.Store.Current.Slices.Keys);
        Assert.Empty(model.Store.Tombstones());
        var reads = (rig.Files.Tails, rig.Files.Ranges, rig.State.Reads);
        model.Hint(File);
        model.Tick();
        Drain(model, clock);
        Assert.Equal(reads, (rig.Files.Tails, rig.Files.Ranges, rig.State.Reads));
    }

    [Fact]
    public void ATokenOfAnotherLaneOrAnUnknownDirectoryNamesNoKey()
    {
        var rig = new Rig();

        Assert.Null(rig.Lane.KeyOfToken("activity:abc/s"));
        Assert.Null(rig.Lane.KeyOfToken("transcript:ffffffff/s"));
        Assert.Null(rig.Lane.KeyOfToken("transcript:nodivider"));
        Assert.Equal(Rig.Key, rig.Lane.KeyOfToken(Rig.Id));
    }

    [Fact]
    public void TheDependencyTableNamesTheTranscriptLaneAndItsSliceTemplate()
    {
        Assert.Contains("transcript:<dirId>/<subject>", SliceDependencies.Feeds(TranscriptLane.LaneName));
        Assert.Equal([TranscriptLane.LaneName], SliceDependencies.FedBy("transcript:<dirId>/<subject>"));
        Assert.DoesNotContain(SliceDependencies.Rows, r => r.Lane == "transcripts");
        Assert.Equal("transcript", SliceDependencies.PrefixOf("transcript:<dirId>/<subject>"));
        Assert.StartsWith("transcript:", Rig.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRootsCallbackRunsOutsideTheLaneLock()
    {
        var rig = new Rig();
        var reachedLane = false;
        rig.RootsHook = () => reachedLane = Task.Run(() => rig.Lane.LeasedDirs()).Wait(TimeSpan.FromSeconds(3));

        _ = rig.Lane.Reread(Rig.Key, 0);

        Assert.True(reachedLane, "another thread could not take the lane lock while the callback ran");
    }

    private static void Drain(ReadModel model, Clock clock)
    {
        for (var guard = 0; guard < 10_000; guard++)
        {
            if (model.Projector.Step())
            {
                continue;
            }

            if (model.Bus.NextDueInMs() is { } due && due > 0)
            {
                clock.Now += due;
                continue;
            }

            return;
        }

        throw new InvalidOperationException("the projector did not settle");
    }
}
