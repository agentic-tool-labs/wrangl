using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The consent guards of per-dir-files-to-db S10 (spec §6c): the asks write is never served on the daemon route (G1), an asks log is
/// taken into the table with each row's <c>at</c> and <c>writer</c> as written (G2), and every asks-log read is the one typed read
/// that says when the store could not be read, each reader deciding fail-closed then (G5).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class AsksConsentGuardTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private string Hier => Path.Combine(_sandbox.Root, "hier");

    private string AsksLog => Path.Combine(Hier, "asks.jsonl");

    private static string AllowRow(DateTimeOffset at) => JsonText.Stringify(new JsonObject
    {
        ["at"] = Timestamps.ToIsoString(at),
        ["writer"] = ConfigModule.AskRecordWriter,
        ["metadata"] = new JsonObject { ["source"] = ConfigModule.AllowOnSource },
        ["response"] = new JsonObject { ["answers"] = new JsonObject { ["Widen member launch rules?"] = "Allow" } },
    }, 0);

    /// <summary>A member-launch consent row the user answered <paramref name="answer"/>, as the ask-record hook writes it.</summary>
    private static string ConsentRow(DateTimeOffset at, string answer)
    {
        var q = ConfigModule.MemberLaunchConsentQuestion;
        var asked = "[{\"question\":\"" + q + "\",\"header\":\"Member rules\",\"multiSelect\":false,\"options\":[{\"label\":\"Allow\",\"description\":\"d\"},{\"label\":\"Not now\",\"description\":\"d\"}]}]";
        return "{\"at\":\"" + Timestamps.ToIsoString(at) + "\",\"writer\":\"" + ConfigModule.AskRecordWriter + "\",\"session_id\":\"s\",\"questions\":" + asked + ",\"response\":{\"questions\":" + asked
            + ",\"answers\":{\"" + q + "\":\"" + answer + "\"},\"annotations\":{}},\"metadata\":{\"source\":\"ah:R7;consent=member-launch-rules;cv=2\"}}";
    }

    /// <summary>The Orchestrator session's recorded Authority answer.</summary>
    private static string AuthorityRow(DateTimeOffset at, string answer) =>
        "{\"at\":\"" + Timestamps.ToIsoString(at) + "\",\"session_id\":\"orch\",\"questions\":[{\"header\":\"Authority\",\"question\":\"Who?\"}],\"response\":{\"answers\":{\"Who?\":\"" + answer + "\"}}}";

    private string Plant(string name, string line)
    {
        var dir = Path.Combine(_sandbox.Root, name, ".claude", "ah2");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "asks.jsonl");
        File.WriteAllText(path, line + "\n");
        return path;
    }

    private AskLogFileReader Reader(IAskSource? asks = null) => new(PlatformModule.CreateIo(null).Files, asks ?? StateDbModule.CreateAsks(_sandbox.AhHome));

    /// <summary>An asks store whose answers the test sets: unreadable, or the given rows for the asks log and none for the rest; it counts its reads.</summary>
    private sealed class FakeAsks : IAskSource
    {
        internal bool Readable { get; set; }

        internal IReadOnlyList<string> AsksRows { get; set; } = [];

        internal List<string> Reads { get; } = [];

        public bool Holds(string path) => Path.GetFileName(path) is "asks.jsonl" or "asks-gate.jsonl" or "parked-questions.jsonl";

        public AskLog? Read(string path)
        {
            Reads.Add(path);
            return !Readable ? AskLog.Unreadable : AskLog.Of(Path.GetFileName(path) == "asks.jsonl" ? AsksRows : []);
        }

        public bool Taken(string path) => Holds(path);
    }

    private ServiceGraph Graph()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = Hier };
        return ServiceGraph.Build(PlatformModule.CreateIo(null), new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables), FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
    }

    /// <summary>A surrogate run anchor for team <c>tm</c> in <see cref="Hier"/>, declaring one grant.</summary>
    private void WriteSurrogateAnchor()
    {
        var msgs = Path.Combine(Hier, "msgs");
        Directory.CreateDirectory(msgs);
        _ = TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(msgs, "20261110-100000-aaaa--orchestrator--pipeline-run-anchor--request.md"),
            "---\nid: 20261110-100000-aaaa\ntype: request\nto: orchestrator\nfrom: orchestrator\nteam: tm\nslug: pipeline-run-anchor\ncreated: "
            + Timestamps.ToIsoString(Now.AddHours(-1)) + "\n---\n\n## constraints\n   branch: ah/x\n   authority: surrogate\n");
    }

    [Fact]
    public async Task TheAsksWriteIsNotServedOnTheDaemonRouteAndStoresNothing()
    {
        Assert.Null(StateDbModule.EnsureDatabase(_sandbox.AhHome));
        var graph = Graph();
        var path = Path.Combine(_sandbox.Root, "forged", ".claude", "ah2", "asks.jsonl");
        var args = JsonValue.FromObject(new JsonObject { ["path"] = path, ["row"] = JsonText.Parse(AllowRow(Now)) });

        var reply = await graph.State.InvokeAsync("decisions", "ask", viaPost: true, args, context: null, opId: "op-forge", static (_, work) => Task.FromResult(work()));

        Assert.Equal(400, reply.Status);
        Assert.Contains("is not served by the daemon", System.Text.Encoding.UTF8.GetString(reply.Body.Span), StringComparison.Ordinal);
        Assert.True(Reader().ReadAskLog(path).TryLines(out var lines));
        Assert.Empty(lines);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AnImportedAllowKeepsItsOwnTimeSoAnElevenMinuteOldOneIsNoFreshProof()
    {
        Assert.Null(StateDbModule.EnsureDatabase(_sandbox.AhHome));
        var stale = Plant("stale", AllowRow(Now.AddMinutes(-11)));
        var fresh = Plant("fresh", AllowRow(Now.AddMinutes(-5)));
        var reader = Reader();
        _ = StateDbModule.ImportHierDirFiles(_sandbox.AhHome, Path.GetDirectoryName(stale)!);
        _ = StateDbModule.ImportHierDirFiles(_sandbox.AhHome, Path.GetDirectoryName(fresh)!);

        // the import pass took each file; the rows are the file's lines byte for byte, never stamped with the take's time
        Assert.True(reader.ReadAskLog(stale).TryLines(out var taken));
        Assert.Equal([AllowRow(Now.AddMinutes(-11))], taken);
        Assert.True(reader.ReadAskLog(fresh).TryLines(out _));
        File.Delete(stale);
        File.Delete(fresh);

        Assert.Null(ConfigModule.FreshAllowOnProof(reader, stale, Now, null));
        // the negative: a five-minute-old Allow, read from the table after its file is gone, still proves
        Assert.Equal(Timestamps.ToIsoString(Now.AddMinutes(-5)), ConfigModule.FreshAllowOnProof(reader, fresh, Now, null));
    }

    [Fact]
    public void AnUnreadableStoreFailsAllThreeDecisionsClosedAndAReadableOneLetsTheTableDecide()
    {
        Directory.CreateDirectory(Hier);
        _sandbox.PlantTeam(Path.Combine(Hier, "teams", "tm.json"), "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Environment.ProcessId + ",\"session_id\":\"orch\"},\"members\":[]}");
        WriteSurrogateAnchor();

        // the taken file still on disk holds an old Allow, a fresh allow-on proof and the surrogate answer; the table holds the later rows
        File.WriteAllText(AsksLog, ConsentRow(Now.AddMinutes(-3), "Allow") + "\n" + AllowRow(Now.AddMinutes(-3)) + "\n" + AuthorityRow(Now.AddMinutes(-3), "Orchestrator acts for me (this run)") + "\n");
        var asks = new FakeAsks { Readable = false, AsksRows = [ConsentRow(Now.AddMinutes(-1), "Not now"), AuthorityRow(Now.AddMinutes(-1), "I decide")] };
        var reader = Reader(asks);
        var graph = Graph();

        Assert.False(ConfigModule.EvaluateMemberLaunchConsent(reader, AsksLog, enabled: true).Effective);
        Assert.Null(ConfigModule.FreshAllowOnProof(reader, AsksLog, Now, null));
        Assert.NotEqual("surrogate", HierarchyModule.ReadAuthority(graph.Hierarchy, reader, Hier, "tm", "orch").Mode);

        // the negative: readable again, the table's later decline and answer decide, and the stale file is still not read
        asks.Readable = true;
        Assert.False(ConfigModule.EvaluateMemberLaunchConsent(reader, AsksLog, enabled: true).Effective);
        Assert.Null(ConfigModule.FreshAllowOnProof(reader, AsksLog, Now, null));
        Assert.Equal("user", HierarchyModule.ReadAuthority(graph.Hierarchy, reader, Hier, "tm", "orch").Mode);

        // and the file's own rows, read by a plain reader, would have granted all three
        var plain = PlatformModule.CreateIo(null).Files;
        Assert.True(ConfigModule.EvaluateMemberLaunchConsent(plain, AsksLog, enabled: true).Effective);
        Assert.NotNull(ConfigModule.FreshAllowOnProof(plain, AsksLog, Now, null));
    }

    [Fact]
    public void EachDecisionReadsEachAsksLogOnce()
    {
        Directory.CreateDirectory(Hier);
        _sandbox.PlantTeam(Path.Combine(Hier, "teams", "tm.json"), "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Environment.ProcessId + ",\"session_id\":\"orch\"},\"members\":[]}");
        WriteSurrogateAnchor();
        var asks = new FakeAsks { Readable = true };
        var reader = Reader(asks);
        var graph = Graph();

        _ = ConfigModule.EvaluateMemberLaunchConsent(reader, AsksLog, enabled: true);
        Assert.Single(asks.Reads);
        asks.Reads.Clear();

        _ = ConfigModule.FreshAllowOnProof(reader, AsksLog, Now, null);
        Assert.Single(asks.Reads);
        asks.Reads.Clear();

        _ = HierarchyModule.ReadAuthority(graph.Hierarchy, reader, Hier, "tm", "orch");
        // one read per log: no log read twice
        Assert.NotEmpty(asks.Reads);
        Assert.Equal(asks.Reads.Distinct(StringComparer.Ordinal).Count(), asks.Reads.Count);
    }

    [Fact]
    public void ARowTheRealWriteOpStoresIsWhatTheHookServicesReader()
    {
        Assert.Null(StateDbModule.EnsureDatabase(_sandbox.AhHome));
        Directory.CreateDirectory(Hier);
        var graph = Graph();
        var context = new CliContext(graph.Environment, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        var hook = HookComposition.Create(context).Services();

        hook.Hierarchy.StateCalls.AppendAsk(Hier, JsonText.Parse(ConsentRow(Now, "Not now")));

        Assert.True(hook.Files.ReadAskLog(AsksLog).TryLines(out var lines));
        Assert.Contains(lines, static l => l.Contains("Not now", StringComparison.Ordinal));
        // the negative: the raw file reader the hook once held sees no such row, for the write stored none in the file
        Assert.True(PlatformModule.CreateIo(null).Files.ReadAskLog(AsksLog).TryLines(out var fileLines));
        Assert.DoesNotContain(fileLines, static l => l.Contains("Not now", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAsksLogTheStoreHoldsCannotBeReadAsAFile()
    {
        Assert.Null(StateDbModule.EnsureDatabase(_sandbox.AhHome));
        var path = Plant("door", ConsentRow(Now, "Allow"));
        var reader = Reader();

        // the negative: a file not yet taken is still the log, and reads as a file
        Assert.Contains("Allow", reader.ReadText(path), StringComparison.Ordinal);
        _ = StateDbModule.ImportHierDirFiles(_sandbox.AhHome, Path.GetDirectoryName(path)!);
        Assert.True(reader.ReadAskLog(path).TryLines(out _));
        _ = Assert.Throws<InvalidOperationException>(() => reader.ReadText(path));
        // the negative: any other file in the same dir reads as ever
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "notes.txt"), "x");
        Assert.Equal("x", reader.ReadText(Path.Combine(Path.GetDirectoryName(path)!, "notes.txt")));
    }
}
