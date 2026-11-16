using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>What the roster verbs put in the team's memory ledger when their change has committed, and that a plan, a dry run or a refusal puts nothing.</summary>
public sealed partial class RosterTeamVerbTests
{
    private string LedgerPath => Path.Combine(ProgressDir, "ledger.jsonl");

    // per-dir-files-to-db S11c: the ledger and the plan logs are progress_logs rows, read here by the old file path
    private List<JsonValue> LedgerLines() => [.. LogLines(LedgerPath).Select(JsonText.Parse)];

    private string[] LogLines(string path) => TestProgressLogs.ReadFile(_sandbox.AhHome, path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Kind(JsonValue row) => row.Member("kind")!.Value.AsString();

    private static string TextOf(JsonValue row) => row.Member("text")!.Value.AsString();

    [Fact]
    public void NegativeLedgerShipWritesItsRowAndClosesNothing()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        if (Directory.Exists(Path.Combine(Project, ".git")))
        {
            Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        }

        _ = RealGit("init", "-q");
        _ = RealGit("commit", "-q", "--allow-empty", "-m", "work");
        var sha = RealGit("rev-parse", "HEAD");
        _runner.GitPassthrough = RealRunner();
        Directory.CreateDirectory(ProgressDir);
        var plan = Path.Combine(ProgressDir, "_run.plan.jsonl");
        File.WriteAllLines(plan, [TimingRow("09:00", "a", "add", ",\"title\":\"A\""), TimingRow("10:00", "a", "start"), TimingRow("11:00", "a", "review", ",\"commits\":[\"" + sha + "\"]")]);
        _ = ComposeServices();
        var before = TestProgressLogs.ReadFile(_sandbox.AhHome, plan);
        Assert.NotEmpty(before ?? string.Empty);

        var (code, stdout, stderr) = Roster("ledger", "ship", sha, "--team", "proj", "--cwd", Project);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("ship", Kind(Assert.Single(LedgerLines())));
        Assert.Equal(before, TestProgressLogs.ReadFile(_sandbox.AhHome, plan));
    }

    [Fact]
    public void LedgerShipAddsTheBatchTimingLineAndKeepsItsKeys()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        if (Directory.Exists(Path.Combine(Project, ".git")))
        {
            Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        }

        _ = RealGit("init", "-q");
        _ = RealGit("commit", "-q", "--allow-empty", "-m", "work");
        var sha = RealGit("rev-parse", "HEAD");
        _runner.GitPassthrough = RealRunner();
        Directory.CreateDirectory(ProgressDir);
        File.WriteAllLines(Path.Combine(ProgressDir, "_run.plan.jsonl"), [
            TimingRow("09:00", "a", "add", ",\"title\":\"A\""), TimingRow("10:00", "a", "start"),
            TimingRow("11:00", "a", "review", TimingCause("reply", "11:00")), TimingRow("12:00", "a", "done", TimingCause("ship", "12:00", sha)),
        ]);

        var (code, stdout, stderr) = Roster("ledger", "ship", sha, "--team", "proj", "--cwd", Project);

        Assert.True(code == 0, stdout + stderr);
        var json = JsonText.Parse(stdout);
        Assert.Equal(["kind", "sha", "ref", "team", "appended", "timing"], json.AsObject().Keys);
        var timing = json.Member("timing")!.Value.AsString();
        Assert.StartsWith("batch 1 items, ", timing, StringComparison.Ordinal);
        Assert.Contains("build 1h00m", timing, StringComparison.Ordinal);
        Assert.Contains("; gated by a in ", timing, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecklistShipClosesTheItemsTheShaCarriesAndFailedOnlyNotesThemWhileALaterShaStaysOpen()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        if (Directory.Exists(Path.Combine(Project, ".git")))
        {
            Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        }

        _ = RealGit("init", "-q");
        _ = RealGit("commit", "-q", "--allow-empty", "-m", "shipped work");
        var shipped = RealGit("rev-parse", "HEAD");
        _ = RealGit("commit", "-q", "--allow-empty", "-m", "later work");
        var later = RealGit("rev-parse", "HEAD");
        _runner.GitPassthrough = RealRunner();
        Directory.CreateDirectory(ProgressDir);
        File.WriteAllLines(Path.Combine(ProgressDir, "_run.plan.jsonl"), [
            TimingRow("09:00", "a", "add", ",\"title\":\"A\""), TimingRow("10:00", "a", "review", ",\"commits\":[\"" + shipped + "\"]"),
            TimingRow("09:00", "b", "add", ",\"title\":\"B\""), TimingRow("10:00", "b", "review", ",\"commits\":[\"" + later + "\"]"),
        ]);
        string StateOf(string id)
        {
            var (_, show, _) = Roster("checklist", "show", "--all", "--team", "proj", "--cwd", Project);
            return JsonText.Parse(show).Member("items")!.Value.AsArray().Single(i => i.Member("id")!.Value.AsString() == id).Member("state")!.Value.AsString();
        }

        var failed = Roster("checklist", "ship", "--failed", shipped, "--team", "proj", "--cwd", Project);
        Assert.True(failed.Code == 0, failed.Stdout + failed.Stderr);
        Assert.Equal(["_run/a"], JsonText.Parse(failed.Stdout).Member("items")!.Value.AsArray().Select(static i => i.AsString()));
        Assert.Equal("in_review", StateOf("a"));
        var gate = Assert.Single(LedgerLines(), static r => Kind(r) == "gate");
        Assert.Equal(shipped, gate.Member("sha")!.Value.AsString());
        Assert.Equal("fail", gate.Member("result")!.Value.AsString());

        var ship = Roster("checklist", "ship", shipped, "--team", "proj", "--cwd", Project);

        Assert.True(ship.Code == 0, ship.Stdout + ship.Stderr);
        Assert.Equal("done", StateOf("a"));
        Assert.Equal("in_review", StateOf("b"));
        Assert.Single(LedgerLines(), static r => Kind(r) == "gate");
    }

    [Fact]
    public void ChecklistShipLeavesAnItemWaitingOnTheUserOpenThoughItsBranchShippedAndClosesAQueuedOneOnTheSameRule()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));
        if (Directory.Exists(Path.Combine(Project, ".git")))
        {
            Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        }

        _ = RealGit("init", "-q");
        _ = RealGit("commit", "-q", "--allow-empty", "-m", "base");
        _ = RealGit("commit", "-q", "--allow-empty", "-m", "shipped work");
        var shipped = RealGit("rev-parse", "HEAD");
        _ = RealGit("branch", "ah/waits", shipped);
        _ = RealGit("branch", "ah/queued", shipped);
        _runner.GitPassthrough = RealRunner();
        var msgs = Path.Combine(Hierarchy, "msgs");
        Directory.CreateDirectory(msgs);
        foreach (var (id, slug) in new[] { ("20261106-120000-wa01", "waits"), ("20261106-120000-qu01", "queued") })
        {
            _ = Ah.TestKit.TestMsgStore.Plant(
                _sandbox.AhHome, Path.Combine(msgs, $"{id}--implementor--{slug}--request.md"),
                $"---\nid: {id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: {slug}\nteam: proj\ncreated: {StampNow(3)}\n---\n\nwork\n");
        }

        Directory.CreateDirectory(ProgressDir);
        File.WriteAllLines(Path.Combine(ProgressDir, "_run.plan.jsonl"), [
            TimingRow("09:00", "w", "add", ",\"title\":\"W\""), TimingRow("09:01", "w", "ref", ",\"ref\":\"20261106-120000-wa01\""), TimingRow("10:00", "w", "wait-user", ",\"note\":\"which way?\""),
            TimingRow("10:05", "w", "note", ",\"note\":\"followup:e1\"" + TimingCause("followup", "10:05", "e1")),
            TimingRow("09:00", "m", "add", ",\"title\":\"M\""), TimingRow("10:00", "m", "wait-user", ",\"note\":\"which?\""), TimingRow("10:05", "m", "note", ",\"note\":\"by hand\"" + TimingCause("manual", "10:05", "")),
            TimingRow("09:00", "l", "add", ",\"title\":\"L\""), TimingRow("10:00", "l", "wait-user", ",\"note\":\"keep files?\""), TimingRow("10:05", "l", "note", ",\"note\":\"not closed: backlog-done while waiting_user\""),
            TimingRow("09:00", "q", "add", ",\"title\":\"Q\""), TimingRow("09:01", "q", "ref", ",\"ref\":\"20261106-120000-qu01\""),
        ]);

        var ship = Roster("checklist", "ship", shipped, "--team", "proj", "--cwd", Project);

        Assert.True(ship.Code == 0, ship.Stdout + ship.Stderr);
        Assert.Equal(["_run/q"], JsonText.Parse(ship.Stdout).Member("items")!.Value.AsArray().Select(static i => i.AsString()));
        Assert.Equal(["_run/w (waiting_user)"], JsonText.Parse(ship.Stdout).Member("skipped")!.Value.AsArray().Select(static i => i.AsString()));

        // a later ship carries the held row again: listed again, noted once
        var again = Roster("checklist", "ship", shipped, "--team", "proj", "--cwd", Project);
        Assert.Equal(["_run/w (waiting_user)"], JsonText.Parse(again.Stdout).Member("skipped")!.Value.AsArray().Select(static i => i.AsString()));
        Assert.Single(LogLines(Path.Combine(ProgressDir, "_run.plan.jsonl")), static l => l.Contains("not closed: ship while waiting_user", StringComparison.Ordinal));

        // the ship and follow-up notes are trail only: the waiting item keeps its question; negative: a note by hand still replaces it
        var shown = JsonText.Parse(Roster("checklist", "show", "--team", "proj", "--cwd", Project).Stdout).Member("items")!.Value.AsArray();
        string NoteOf(string id) => shown.Single(i => i.Member("id")!.Value.AsString() == id).Member("note")!.Value.AsString();
        Assert.Equal("which way?", NoteOf("w"));
        Assert.Equal("keep files?", NoteOf("l")); // an older close note with no cause is trail only too
        Assert.Equal("by hand", NoteOf("m"));
    }

    [Fact]
    public void ASpawnThatCommittedIsAMemberLineAndADryRunOrARefusedSpawnIsNone()
    {
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;

        Assert.Equal(0, SpawnImplementor("--stream", "s1", "--dry-run").Code);
        Assert.Equal(2, Roster("spawn-one", "nobody", "--team", "proj").Code);
        Assert.Empty(LedgerLines());

        var (code, spawned) = SpawnImplementor("--stream", "s1");

        Assert.Equal(0, code);
        var row = Assert.Single(LedgerLines());
        Assert.Equal("member", Kind(row));
        Assert.Equal($"spawned {spawned["member"].AsObject()["name"].AsString()} (implementor)", TextOf(row));
        Assert.Equal("verb", row.Member("source")!.Value.Member("type")!.Value.AsString());
    }

    [Fact]
    public void StreamOpenAndStreamDoneAreTrainLinesAndADryRunOpenIsNone()
    {
        WriteTeam("proj", LiveTeamJson("terminal", TwoPeers));

        Assert.Equal(0, Roster("stream-open", "dry-1", "--no-worktree", "--dry-run", "--team", "proj", "--orchestrator-pid", Self).Code);
        Assert.Empty(LedgerLines());

        Assert.Equal(0, OpenStream().Code);
        var opened = Assert.Single(LedgerLines());
        Assert.Equal("train", Kind(opened));
        Assert.Equal("feature-1", opened.Member("stream")!.Value.AsString());
        Assert.Equal("stream-open", opened.Member("source")!.Value.Member("verb")!.Value.AsString());

        var (doneCode, doneOut, doneErr) = Roster("stream-done", "feature-1", "--team", "proj");
        Assert.True(doneCode == 0, doneOut + doneErr);
        var lines = LedgerLines();
        Assert.Equal(2, lines.Count);
        Assert.Equal("stream feature-1 done", TextOf(lines[1]));
    }

    [Fact]
    public void ResumeCheckpointWritesTheMemoryBlockToStderrOnlyWhenTheLedgerHasSomethingToRecallAndStdoutStaysOneJsonValue()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        Registered(ImplSession, live: true);
        _callers ??= new RecordingCallers { StartText = SelfStart };
        var record = WriteRecord(null, request: "req-1", open: ["open-1"]);
        string[] args = ["resume", "--checkpoint", record, "--team", "proj", "--cwd", Project, "--orchestrator-pid", Self];

        var (_, plain, plainErr) = Roster(args);
        Assert.DoesNotContain("AH MEMORY", plain + plainErr, StringComparison.Ordinal);

        Directory.CreateDirectory(ProgressDir);
        File.WriteAllText(
            LedgerPath,
            """{"v":1,"at":"2026-10-23T10:00:00.000Z","kind":"decision","team":"proj","stream":null,"sha":null,"base":null,"ref":null,"verdict":null,"slug":"foo","text":"use A","source":{"type":"report","path":null,"verb":null,"tool_use_id":null}}""" + "\n");
        var (_, withLedger, withLedgerErr) = Roster(args);

        Assert.DoesNotContain("AH MEMORY", withLedger, StringComparison.Ordinal);
        Assert.Null(Record.Exception(() => JsonText.Parse(withLedger)));
        var block = withLedgerErr[withLedgerErr.IndexOf("AH MEMORY (proj)", StringComparison.Ordinal)..].TrimEnd();
        Assert.StartsWith("AH MEMORY (proj) — ", block, StringComparison.Ordinal);
        Assert.EndsWith("use A", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ADismissThatClosedTheMemberIsARetiredLineAndTheClosePlanIsNone()
    {
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers));
        var member = TokenOf(Roster("dismiss", "proj-implementor", "--plan", "--ignore-release", "--team", "proj").Stdout);
        Assert.Empty(LedgerLines());

        var (code, _, _) = Roster("dismiss", "proj-implementor", "--close", "--confirm", "--ignore-release", "--plan-token", member, "--team", "proj");

        Assert.Equal(0, code);
        var row = Assert.Single(LedgerLines());
        Assert.Equal("member", Kind(row));
        Assert.Equal("retired proj-implementor (implementor)", TextOf(row));
    }
}
