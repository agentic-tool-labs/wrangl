using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>Processes an evidence row can name: a pid is running with a start and a command line, or it is not running.</summary>
internal sealed class EvidenceProcesses : IProcessTable, IProcessInspector
{
    internal const string Start = "Wed Nov  4 11:00:00 2026";

    /// <summary>A day later, so the recorded start is before it under either reading of a recorded start's zone.</summary>
    internal const string OtherStart = "Thu Nov  5 15:00:00 2026";

    /// <summary>A registered process that is running as recorded, so the observer can see that a missing pid is gone.</summary>
    internal const int Reference = 50;

    private readonly Dictionary<int, (string Start, string Command)> _running = new()
    {
        [Reference] = (Start, "claude"),
        [101] = (Start, "claude"),
        [103] = (OtherStart, "claude"),
        [104] = (Start, "/bin/zsh"),
        [105] = (Start, "claude"),
        [109] = (Start, "claude"),
        [110] = (Start, "claude"),
    };

    public IReadOnlyList<int>? Ancestors(int pid) => null;

    public bool? HasAncestor(int pid, int candidate) => null;

    public string? StartTime(long pid) => _running.TryGetValue((int)pid, out var process) ? process.Start : null;

    public string? Command(int pid) => CommandLine(pid);

    public bool PidAlive(int pid) => _running.ContainsKey(pid);

    /// <summary>105 is another user's process: alive to kill-0 and the table, never owned.</summary>
    public bool PidOwned(int pid) => pid != 105 && _running.ContainsKey(pid);

    public string? CommandLine(int pid) => _running.TryGetValue(pid, out var process) ? process.Command : null;
}

/// <summary>
/// One member, one moment, one answer: each row of the one-liveness-truth table through the stall alarm, the CLI and the web, over a real
/// mailbox, real team and peers files and the real judge. Only the processes and the herdr answer are staged.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class OneLivenessTableTests : IDisposable
{
    private const string Team = "tm";

    private static readonly Dictionary<string, string> Answers = new()
    {
        ["r1"] = "live",
        ["r2"] = "proven_dead",
        ["r3"] = "proven_dead",
        ["r4"] = "unknown",
        ["r5"] = "live",
        ["r6"] = "unknown",
        ["r7"] = "live",
        ["r8"] = "unknown",
        ["r9"] = "live",
        ["r10"] = "live",
        ["r11"] = "proven_dead",
        ["r12"] = "proven_dead",
    };

    private readonly Sandbox _sandbox = new();
    private readonly EvidenceProcesses _processes = new();
    private readonly ServiceGraph _graph;
    private readonly string _dir;

    public OneLivenessTableTests()
    {
        Directory.CreateDirectory(_sandbox.Bin);
        var herdr = Path.Combine(_sandbox.Bin, "herdr");
        File.WriteAllText(herdr, "#!/bin/sh\n");
        File.SetUnixFileMode(herdr, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new FakeRunner
        {
            Output = static argv => !argv.Contains("agent")
                ? argv.Contains("command=") && argv.Contains("113") ? "/bin/sh -c wrapper\n" : string.Empty
                : argv.Contains("r7")
                    ? "{\"result\":{\"agent\":{\"agent_status\":\"idle\",\"agent\":\"claude\"}}}"
                    : argv.Contains("r12") || argv.Contains("p13") || argv.Contains("p15")
                        ? "{\"result\":{\"agent\":{\"agent_status\":\"idle\",\"agent\":\"shell\"}}}"
                        : "{\"error\":{\"code\":\"agent_not_found\"}}",
        };
        _dir = Path.Combine(_sandbox.Home, "repo", ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(_dir, "teams"));
        using (var open = Ah.StateDb.StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(Ah.StateDb.StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["PATH"] = _sandbox.Bin, ["WRANGL_HIERARCHY_DIR"] = _dir };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient());
        _graph.Hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(_graph.Mailbox, _processes, _processes);
        Stage();
    }

    public void Dispose() => _sandbox.Dispose();

    private static JsonValue Row(string name)
    {
        var row = new JsonObject { ["name"] = name, ["role"] = "implementor", ["route"] = "peer" };
        if (name is "r7" or "r12")
        {
            // Teardown asks herdr only for a member of another kind (Teardown.MemberLiveness); see the spec's not-verified list.
            row["kind"] = "codex";
            row["transport_id"] = "w1:p" + name;
        }

        if (name == "r8")
        {
            row["spawned_at"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow.AddSeconds(-10));
        }

        return JsonValue.FromObject(row);
    }

    private void Register(string name, string session, long? pid, string? start, string harness = "claude")
    {
        var teamFile = _graph.Roster.TeamPath(_dir, Team);
        var box = _graph.Mailbox.BoxDirFor(teamFile, name);
        Assert.True(_graph.Mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite(session, [new BoxRef($"{name}@{Team}", box)], teamFile, harness, pid, null, _sandbox.Home, start)));
        if (pid is { } known)
        {
            _graph.Hierarchy.Status.AppendSessionPid(_dir, JsonValue.FromString(session), JsonValue.FromNumber(known));
        }
    }

    private void Stage()
    {
        var rows = new JsonArray();
        foreach (var name in Answers.Keys)
        {
            rows.Add(Row(name));
        }

        Ah.TestKit.TestPlant.Team(_graph.Roster, _graph.Writer, _graph.Roster.TeamPath(_dir, Team), new JsonObject
        {
            ["team_id"] = "t-tm",
            ["orchestrator"] = new JsonObject { ["session_id"] = "s-ref" },
            ["members"] = rows,
        });
        var start = EvidenceProcesses.Start;
        Register("ref", "s-ref", EvidenceProcesses.Reference, start);
        Register("r1", "s1", 101, start);
        Register("r2", "s2", 102, start);
        Register("r3", "s3", 103, start);
        Register("r4", "s4", 104, null);
        Register("r5", "s5", null, null, "custom");
        // fresh for 3 polls (contract heartbeat_fresh_factor): 60 s polls outlast the web's 60 s wait, which a 5 s poll (15 s) did not under load
        _graph.Mailbox.WriteHeartbeat(_graph.Mailbox.BoxDirFor(_graph.Roster.TeamPath(_dir, Team), "r5"), "claude-mod", "s5", 60_000);
        Register("r6", "s6", null, null, "custom");
        Register("r7", "s7", 107, start);
        Register("r9", "s9", 109, start);
        Register("r12", "s12", 112, start);
        var peers = new[]
        {
            PeerRow("r8", "down", -3600, 108),
            PeerRow("r9", "down", -5, 109),
            PeerRow("r10", "up", -60, 110),
            PeerRow("r11", "down", -3600, 111),
        };
        _sandbox.PlantPeers(_dir, string.Join("\n", peers));
    }

    private static string PeerRow(string name, string status, int seconds, int pid) =>
        JsonText.Stringify(JsonValue.FromObject(new JsonObject
        {
            ["type"] = "peer",
            ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow.AddSeconds(seconds)),
            ["name"] = name,
            ["role"] = "implementor",
            ["status"] = status,
            ["pid"] = pid,
            ["pid_start"] = EvidenceProcesses.Start,
        }), 0);

    private Dictionary<string, string> ThroughTheStallAlarm()
    {
        var host = new StallHost(_graph, _sandbox.AhHome, () => true, () => null);
        var team = new StallTeam(Team, _dir, _sandbox.Home, null, "s-ref");
        var answers = new Dictionary<string, string>();
        foreach (var member in host.Members(team))
        {
            answers[member.Name] = host.Judge(team, member).Verdict.Status switch
            {
                Ah.Hierarchy.Verdict.Live => "live",
                Ah.Hierarchy.Verdict.Dead => "proven_dead",
                _ => "unknown",
            };
        }

        return answers;
    }

    private Dictionary<string, string> ThroughTheCli() =>
        Answers.Keys.ToDictionary(static name => name, name => _graph.Launch.Teardown.MemberLiveness(_dir, Row(name), Team).Get("liveness")!.Value.AsString());

    private async Task<Dictionary<string, string>> ThroughTheWebAsync()
    {
        using var model = WatchModule.CreateReadModel(
            new StateServiceReader(_graph.InternalStateRead),
            () => [_dir],
            new ReadModelOptions { Home = _sandbox.AhHome, Processes = _processes, MemberFacts = dir => MemberFactsOf.Dir(_graph, dir) });
        WatchModule.StartReadModel(model);
        var last = new Dictionary<string, string>();

        // r2, r3 and r12 are proven dead only once the processes lane has read the table, which is late under load: wait for the answers
        // to the deadline, never a count of polls (a poll with nothing to wait for returns at once, so 200 of them were about 10 s).
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            // A poll is what connects a client, and only a connected client starts the processes lane.
            _ = await model.PollAsync(0, null, ["list:teams"], TimeSpan.FromMilliseconds(100), CancellationToken.None, ["list:teams"]);
            await Task.Delay(50);
            var raw = model.Manifest("summary");
            _lastServed = raw is null ? "no manifest" : System.Text.Encoding.UTF8.GetString(raw);
            foreach (var slice in raw is null ? [] : JsonText.Parse(raw).Member("slices")?.AsArray() ?? [])
            {
                if (slice.Member("id")?.AsString().EndsWith("/" + Team, StringComparison.Ordinal) is true && slice.Member("body")?.Member("member_states") is { IsMap: true } states)
                {
                    last = states.AsObject().Keys.ToDictionary(
                        static key => key[(key.LastIndexOf('/') + 1)..],
                        key => Word(states.AsObject()[key].Member("liveness")!.Value.AsString()));
                    if (Answers.All(a => last.GetValueOrDefault(a.Key) == a.Value))
                    {
                        return last;
                    }
                }
            }
        }

        return last;
    }

    /// <summary><c>not_seen</c> is a display word for <c>unknown</c>, never a verdict.</summary>
    private static string Word(string served) => served == "not_seen" ? "unknown" : served;

    private static void Same(string path, Dictionary<string, string> answers)
    {
        var wrong = Answers.Where(a => answers.GetValueOrDefault(a.Key) != a.Value).Select(a => $"{a.Key}: wanted {a.Value}, got {answers.GetValueOrDefault(a.Key) ?? "nothing"}").ToList();
        Assert.True(wrong.Count == 0, path + ": " + string.Join("; ", wrong));
    }

    [Fact]
    public void APidRecordedBeforeTheBootIsNeverLiveThroughTheBarePathsAndOneRecordedAfterIs()
    {
        DateTimeOffset? afterStart = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? beforeStart = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var rebooted = MeshModule.CreateMemberLiveness(_graph.Mailbox, _processes, _processes, () => afterStart);
        var running = MeshModule.CreateMemberLiveness(_graph.Mailbox, _processes, _processes, () => beforeStart);

        Assert.False(rebooted.PidIdentityLive(101, EvidenceProcesses.Start));
        Assert.Equal(Ah.Core.Time.LivenessVerdict.ProvenDead, rebooted.JudgePeerRow(Ah.Core.Time.PeerRowKind.Up, 101, EvidenceProcesses.Start, afterStart, null, null).Verdict);
        Assert.True(running.PidIdentityLive(101, EvidenceProcesses.Start));
        Assert.Equal(Ah.Core.Time.LivenessVerdict.Live, running.JudgePeerRow(Ah.Core.Time.PeerRowKind.Up, 101, EvidenceProcesses.Start, afterStart, null, null).Verdict);
    }

    // one-liveness.md sites 1, 2 and 7: a pid now running a process that started after the record (sites 1, 2), or another user's
    // process (site 7), is not running; the recorded one is. Each read running before.
    [Fact]
    public void TheDoorbellTheRelayCarrierAndTheViewReadAReusedPidAsNotRunning()
    {
        JsonObject Record(int pid) => new() { ["pid"] = pid, ["pid_start"] = EvidenceProcesses.Start, ["harness"] = "claude" };
        var view = WatchModule.CreateViewSource(new ViewSourceServices(
            _graph.Io.Files, new Ah.Hierarchy.StreamDerivations(), new DaemonServices.ViewPlacement(_graph), _processes, new FakeRunner(), _sandbox.Home,
            PidRunning: (pid, start, policy) => _graph.Hierarchy.Core.PidRunning(pid, start, policy)));

        Assert.False(_graph.Mailbox.RegistrationLive(Record(103)));
        Assert.True(_graph.Mailbox.RegistrationLive(Record(101)));
        Assert.False(_graph.Mailbox.LiveClaudeSender("s3"));
        Assert.True(_graph.Mailbox.LiveClaudeSender("s1"));
        Assert.False(view.OrchestratorAlive(105, EvidenceProcesses.Start));
        Assert.True(view.OrchestratorAlive(101, EvidenceProcesses.Start));
    }

    // b21 F1: a pane that outlived its Claude. The registered pid 113 was a wrapper when registered (pid_proof false) and is gone now, so
    // its exit alone leaves the member unknown; a herdr pane present with no agent then proves it dead. Teardown only.
    [Fact]
    public void AGonePidWithAPaneShowingNoAgentIsProvenDead()
    {
        Register("p13", "s13", 113, EvidenceProcesses.Start);
        Register("p14", "s14", 113, EvidenceProcesses.Start);
        Register("p15", "s15", 104, null);
        string Liveness(string name) => _graph.Launch.Teardown.MemberLiveness(_dir, JsonValue.FromObject(new JsonObject { ["name"] = name, ["role"] = "implementor", ["route"] = "peer" }), Team).Get("liveness")!.Value.AsString();

        Assert.Equal("proven_dead", Liveness("p13"));
        Assert.Equal("unknown", Liveness("p14")); // no pane (agent_not_found)
        Assert.Equal("unknown", Liveness("p15")); // pane shows a shell, but the pid is running
    }

    [Fact]
    public void TheStallAlarmGivesEachRowsAnswer() => Same("stall alarm", ThroughTheStallAlarm());

    [Fact]
    public void TheCliGivesEachRowsAnswer() => Same("cli", ThroughTheCli());

    [Fact]
    public async Task TheWebGivesEachRowsAnswer()
    {
        var answers = await ThroughTheWebAsync();
        Same("web (last served " + _lastServed[..Math.Min(_lastServed.Length, 400)] + ")", answers);
    }

    private string _lastServed = string.Empty;
}
