using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>Classifier blocks in <c>roster doctor</c> and the owner-only <c>roster block-triage</c> that decides them.</summary>
[Collection("RosterServices")]
public sealed class BlockTriageTests : IDisposable
{
    private const string Team =
        "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":4242,\"session_id\":\"orch-tm\",\"pid_start\":\"Thu Oct 15 16:38:50 2026\"},\"members\":[{\"name\":\"tm-dev\",\"role\":\"dev\"}]}";

    private const string Section = "classifier blocks (7 d)";

    private readonly Sandbox _sandbox = new();

    public BlockTriageTests()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Path.Combine(Hierarchy, "teams"));
        File.WriteAllText(Path.Combine(Hierarchy, "teams", "tm.json"), Team);
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private sealed class FakeProof(OwnerProofResult result) : IRosterOwnerProof
    {
        public OwnerProofResult Check(string dir, string? team) => result;

        public OwnerActor Actor() => new(4242, "Thu Oct 15 20:38:50 2026", [new ChainHop(90, "Thu Oct 15 20:38:51 2026", "sh -c ah roster block-triage")], "ah roster block-triage");

        public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) => new(NearestKind.None);
    }

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd, IRosterOwnerProof? proof)
        : IRosterCliServices
    {
        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => null;

        public IRosterLaunch? Launch => null;

        public IRosterCallerRegistry? Callers => null;

        public IRosterOwnerProof? OwnerProof => proof;
    }

    private (ServiceGraph Graph, IAhEnvironment Env, IFileReader Files, IClock Clock) Build()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
        };
        var fake = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(Project, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        return (ServiceGraph.Build(io, fake, FakeRunner.KnowingSelf(), new FakeDaemonClient()), fake, io.Files, io.Clock);
    }

    private (int Code, string Stdout, string Stderr) Roster(string[] args, IRosterOwnerProof? proof)
    {
        var (graph, fake, files, clock) = Build();
        var services = new Services(graph, files, clock, fake, Project, proof);
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static OwnerProofResult Allowed() => new(true, null, "orchestrator", false, null);

    private static OwnerProofResult Refused() => new(false, MeshModule.OwnerVerbRefusal, "no-orchestrator", false, null);

    private void Block(string id, string verdict, string label, string command, string role, TimeSpan age)
    {
        Build().Graph.Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(new JsonObject
        {
            ["type"] = "watch-event",
            ["session_id"] = "s1",
            ["kind"] = "CLASSIFIER-DENIED",
            ["tool_use_id"] = id,
            ["verdict"] = verdict,
            ["label"] = label,
            ["command"] = command,
            ["role"] = role,
            ["ts"] = Timestamps.ToIsoString(DateTimeOffset.UtcNow - age),
        }));
    }

    private int PeerRows() => Build().Graph.Hierarchy.Core.ReadPeerRecords().Count;

    private (int Code, string Stdout) Doctor()
    {
        var r = Roster(["doctor", "--cwd", Project], new FakeProof(Allowed()));
        Assert.True(r.Stdout.Length > 0, "doctor printed nothing; stderr: " + r.Stderr);
        return (r.Code, r.Stdout);
    }

    private void Accepts(string target, string decision)
    {
        var r = Roster(Triage(target, decision), new FakeProof(Allowed()));
        Assert.True(r.Code == 0, $"exit {r.Code}; stderr: {r.Stderr}");
    }

    private string[] Triage(string target, string decision) =>
        ["block-triage", target, "--decision", decision, "--note", "x", "--team", "tm", "--cwd", Project];

    [Fact]
    public void TwoDeniedGroupsAndANoVerdictShowAsGroupsWithCountsAndTriagingOneRemovesIt()
    {
        Block("t1", "denied", "Modify Shared Resources", "bash ship.sh a", "orchestrator", TimeSpan.FromHours(1));
        Block("t2", "denied", "Modify Shared Resources", "bash ship.sh b", "orchestrator", TimeSpan.FromHours(2));
        Block("t3", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));
        Block("t4", "no-verdict", "unknown", "ls", "implementor", TimeSpan.FromHours(1));

        var before = Doctor().Stdout;

        Assert.Contains(Section, before, StringComparison.Ordinal);
        Assert.Contains("denied|Modify Shared Resources|bash|orchestrator x2", before, StringComparison.Ordinal);
        Assert.Contains("denied|unknown|python3|implementor x1", before, StringComparison.Ordinal);
        Assert.Contains("no-verdict|unknown|ls|implementor x1", before, StringComparison.Ordinal);

        var done = Roster(Triage("denied|Modify Shared Resources|bash|orchestrator", "outward-confirm"), new FakeProof(Allowed()));

        Assert.Equal(0, done.Code);
        Assert.Contains("\"triaged\":2", done.Stdout.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var after = Doctor().Stdout;
        Assert.DoesNotContain("Modify Shared Resources", after, StringComparison.Ordinal);
        Assert.Contains("python3", after, StringComparison.Ordinal);
    }

    [Fact]
    public void ATriagedToolUseIdLeavesTheOtherBlocksOfItsGroup()
    {
        Block("t1", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));
        Block("t2", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));

        Accepts("t1", "guidance");

        Assert.Contains("python3|implementor x1", Doctor().Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeANonOwnerIsRefusedAndNothingIsRecorded()
    {
        Block("t1", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));
        var lines = PeerRows();

        var result = Roster(Triage("t1", "none"), new FakeProof(Refused()));

        Assert.NotEqual(0, result.Code);
        Assert.Contains(MeshModule.OwnerVerbRefusal, result.Stderr, StringComparison.Ordinal);
        Assert.Equal(lines, PeerRows());
    }

    [Fact]
    public void NegativeABlockOlderThanSevenDaysIsNotShown()
    {
        Block("t1", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromDays(8));

        Assert.DoesNotContain(Section, Doctor().Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeNoBlocksMeansNoSectionAndTheExitCodeIsTheSameWithOrWithoutOne()
    {
        var without = Doctor();
        Block("t1", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));
        var with = Doctor();

        Assert.DoesNotContain(Section, without.Stdout, StringComparison.Ordinal);
        Assert.Contains(Section, with.Stdout, StringComparison.Ordinal);
        Assert.Equal(without.Code, with.Code);
    }

    [Theory]
    [InlineData("allow-rule")]
    [InlineData("issue:abc")]
    [InlineData("upstream:ftp://x")]
    [InlineData("")]
    public void NegativeAnythingButTheValidDecisionsIsRefusedWithTheValidList(string decision)
    {
        Block("t1", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));

        var result = Roster(Triage("t1", decision), new FakeProof(Allowed()));

        Assert.NotEqual(0, result.Code);
        Assert.Contains("issue:<#N>, upstream:<url>, guidance, outward-confirm, none", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(Section, Doctor().Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("issue:#42")]
    [InlineData("upstream:https://github.com/x/y/issues/1")]
    [InlineData("guidance")]
    [InlineData("outward-confirm")]
    [InlineData("none")]
    public void EveryValidDecisionIsAccepted(string decision)
    {
        Block("t1", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));

        Accepts("t1", decision);
    }

    [Fact]
    public void NegativeAKeyThatMatchesNoUntriagedBlockIsRefused()
    {
        Block("t1", "denied", "unknown", "python3 -", "implementor", TimeSpan.FromHours(1));

        var result = Roster(Triage("denied|unknown|perl|implementor", "none"), new FakeProof(Allowed()));

        Assert.NotEqual(0, result.Code);
        Assert.Contains("no untriaged block matches", result.Stderr, StringComparison.Ordinal);
    }
}
