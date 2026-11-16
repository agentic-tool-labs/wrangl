using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// <c>ahd new</c> over the real libraries and a sandbox repository, driven through <c>--dry-run</c> and a PATH stub <c>claude</c>:
/// the argv each input resolves to, each refusal with its nearest accepted input, and that no team record is ever written.
/// </summary>
[Collection("RosterServices")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class AhdNewVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public AhdNewVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(HierarchyDir);
        Directory.CreateDirectory(StubBin);
        Directory.CreateDirectory(EmptyBin);
        var claude = Path.Combine(StubBin, "claude");
        File.WriteAllText(claude, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > \"{ArgvFile}\"\nexit 7\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string HierarchyDir => Path.Combine(Project, ".claude", "ah2");

    private string RepoFile => Path.Combine(HierarchyDir, "agent-hierarchy.json");

    private string StubBin => Path.Combine(_sandbox.Root, "stubbin");

    private string EmptyBin => Path.Combine(_sandbox.Root, "emptybin");

    private string ArgvFile => Path.Combine(_sandbox.Root, "claude-argv");

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd) : IRosterCliServices
    {
        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers => null;

        public IRosterOwnerProof? OwnerProof => new SessionAncestorProof(System.Environment.ProcessId);
    }

    private ServiceGraph Graph(string path, Dictionary<string, string?>? extra = null)
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = HierarchyDir,
            ["PATH"] = path,
        };
        foreach (var (key, value) in extra ?? [])
        {
            variables[key] = value;
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, FakeRunner.KnowingSelf(), new FakeDaemonClient());
        CommandsModule.ComposeRoster(() => new Services(graph, io.Files, io.Clock, env, Project));
        return graph;
    }

    private (int Code, string Stdout, string Stderr) AhdNewOn(string path, params string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        Graph(path);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "new");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Stdout, string Stderr) AhdNew(params string[] args) => AhdNewOn(StubBin, args);

    private static JsonObject Parse(string text) => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text)).AsObject();

    private List<string> DryArgv(params string[] args)
    {
        var (code, stdout, stderr) = AhdNew([.. args, "--dry-run"]);
        Assert.True(code == 0, stderr);
        return Parse(stdout)["argv"].AsArray().Select(a => a.AsString()).ToList();
    }

    private void AssertRefused(string code, (int Code, string Stdout, string Stderr) result)
    {
        Assert.Equal(2, result.Code);
        Assert.Equal(code, Parse(result.Stdout)["refused"].AsString());
        Assert.False(File.Exists(ArgvFile), "nothing may be launched on a refusal");
    }

    private void WriteRoster(string block) =>
        File.WriteAllText(RepoFile, $"{{\"version\":1,\"roster\":{{\"route\":\"peer\",{block}}}}}");

    private const string OneMember = "\"members\":[{\"role\":\"implementor\"}]";

    /// <summary>A team record whose orchestrator pid is <paramref name="pid"/>, written where the CLI reads team <paramref name="team"/>.</summary>
    private string WriteTeam(string team, long pid, string members = "[]")
    {
        var path = Graph(StubBin).Roster.TeamPath(HierarchyDir, team);
        var created = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        _sandbox.PlantTeam(path, $$"""{"version":1,"team_id":"t-1","created":"{{created}}","roster_level":"repo","transport":"terminal","orchestrator":{"session_id":null,"pid":{{pid}}},"members":{{members}},"expected_root":"{{Project}}","roster":null,"layout":"auto"}""");
        return path;
    }

    /// <summary>The digest of the team record the store holds at <paramref name="path"/>: <c>absent</c> when it holds none.</summary>
    private string TeamDigest(string path) => Graph(StubBin).Roster.ReadTeamWithDigest(path)["digest"].AsString();

    private const long DeadPid = 99_999_999;

    // ---------------------------------------------------------------- argv

    [Fact]
    public void ANameAloneResolvesThePlainOrchestratorArgv()
    {
        Assert.Equal(["claude", "--agent", "wrangl:orchestrator", "--name", "jim", "--permission-mode", "auto"], DryArgv("jim"));
    }

    [Fact]
    public void AModelFlagIsPassedAndInheritPassesNone()
    {
        var sonnet = DryArgv("jim", "-m", "sonnet");
        Assert.Equal("sonnet", sonnet[sonnet.IndexOf("--model") + 1]);

        Assert.DoesNotContain("--model", DryArgv("jim", "--model", "inherit"));
    }

    [Fact]
    public void ALegworkModelIsRefused()
    {
        var refused = AhdNew("jim", "-m", "haiku", "--dry-run");

        AssertRefused("model-unusable", refused);
        Assert.Contains("allowed: opus, sonnet, fable, inherit", refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRosterOrchestratorModelIsUsedAndTheFlagWins()
    {
        WriteRoster($"\"orchestrator_model\":\"opus\",{OneMember}");

        var fromRoster = DryArgv("jim");
        Assert.Equal("opus", fromRoster[fromRoster.IndexOf("--model") + 1]);

        var fromFlag = DryArgv("jim", "-m", "sonnet");
        Assert.Equal("sonnet", fromFlag[fromFlag.IndexOf("--model") + 1]);
    }

    [Fact]
    public void AnOrchestratorModelOnALevelWithNoMembersIsNotUsed()
    {
        WriteRoster("\"orchestrator_model\":\"opus\",\"members\":[]");
        var global = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(global)!);
        File.WriteAllText(global, $"{{\"version\":1,\"roster\":{{\"route\":\"peer\",{OneMember}}}}}");

        Assert.DoesNotContain("--model", DryArgv("jim"));
    }

    [Fact]
    public void AHandEditedLegworkOrchestratorModelIsRefusedNamingTheRosterFile()
    {
        WriteRoster($"\"orchestrator_model\":\"haiku\",{OneMember}");

        var refused = AhdNew("jim", "--dry-run");

        AssertRefused("model-unusable", refused);
        Assert.Contains(RepoFile, refused.Stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- names and cli types

    [Fact]
    public void AnUppercaseNameIsRefusedAndTheLowercaseOneIsNot()
    {
        var refused = AhdNew("Alice", "--dry-run");
        AssertRefused("name-unusable", refused);
        Assert.Contains("name \"Alice\"", refused.Stderr, StringComparison.Ordinal);
        Assert.Contains("[a-z][a-z0-9_-]{0,31}", refused.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--team", refused.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--member", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, AhdNew("jim", "--dry-run").Code);
    }

    [Fact]
    public void ACliTypeOtherThanClaudeIsRefusedAndClaudeIsAccepted()
    {
        var refused = AhdNew("jim", "-ct", "codex", "--dry-run");
        AssertRefused("cli-type-unsupported", refused);
        Assert.Contains("supported: claude", refused.Stderr, StringComparison.Ordinal);

        Assert.Equal(0, AhdNew("jim", "--cli-type", "claude", "--dry-run").Code);
    }

    [Fact]
    public void NoNameIsAUsageErrorAndSoIsAnUnknownFlag()
    {
        var bare = AhdNew();
        Assert.Equal(2, bare.Code);
        Assert.Contains("usage: ahd new <name>", bare.Stderr, StringComparison.Ordinal);

        Assert.Equal(2, AhdNew("jim", "--bogus").Code);
        Assert.Equal(2, AhdNew("jim", "-t").Code);
    }

    [Fact]
    public void ADryRunWithClaudeOffPathIsRefused()
    {
        var refused = AhdNewOn(EmptyBin, "jim", "--dry-run");

        AssertRefused("cli-not-found", refused);
        Assert.Contains("PATH", refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeadMemberHoldingTheNameDoesNotRefuseIt()
    {
        WriteTeam("rc4", DeadPid, $"[{{\"role\":\"implementor\",\"name\":\"jim\",\"route\":\"peer\",\"transport_id\":\"p1\",\"expected_root\":\"{Project}\"}}]");

        Assert.Equal(0, AhdNew("jim", "--dry-run").Code);
    }

    // ---------------------------------------------------------------- teams

    [Fact]
    public void ANewTeamIsHandedOverAsNew()
    {
        var argv = DryArgv("jim", "-t", "newteam");

        Assert.Contains("newteam is new", argv[^1], StringComparison.Ordinal);
        Assert.Equal("absent", TeamDigest(Graph(StubBin).Roster.TeamPath(HierarchyDir, "newteam")));
    }

    [Fact]
    public void ATeamWhoseOwnerIsGoneIsHandedOverForAdoptionAndItsRecordIsUntouched()
    {
        var path = WriteTeam("rc4", DeadPid);
        var before = TeamDigest(path);

        var argv = DryArgv("jim", "--team", "rc4");

        Assert.Contains("rc4 exists and its owner is gone", argv[^1], StringComparison.Ordinal);
        Assert.Equal(before, TeamDigest(path));
    }

    [Fact]
    public void ATeamWhoseOwnerIsLiveIsRefusedAndItsRecordIsUntouched()
    {
        var path = WriteTeam("rc4", System.Environment.ProcessId);
        var before = TeamDigest(path);

        var refused = AhdNew("jim", "-t", "rc4", "--dry-run");

        Assert.Equal(2, refused.Code);
        Assert.Contains("a live Team", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, TeamDigest(path));
    }

    [Fact]
    public void AnUnusableTeamNameIsRefused()
    {
        AssertRefused("team-name-unusable", AhdNew("jim", "-t", "bad name!", "--dry-run"));
    }

    // ---------------------------------------------------------------- foreground launch

    [Fact]
    public void AForegroundLaunchRunsTheStubWithTheArgvAndReturnsItsExitCode()
    {
        var (code, _, stderr) = AhdNew("jim", "-m", "sonnet");

        Assert.Equal(7, code);
        Assert.Equal(["--agent", "wrangl:orchestrator", "--name", "jim", "--model", "sonnet", "--permission-mode", "auto"], File.ReadAllLines(ArgvFile));
        Assert.StartsWith("ahd new: claude --agent wrangl:orchestrator --name jim --model sonnet --permission-mode auto", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AForegroundLaunchWithATeamPassesThePromptLastAndWritesNoTeamRecord()
    {
        var path = WriteTeam("rc4", DeadPid);
        var before = TeamDigest(path);

        var (code, _, stderr) = AhdNew("jim", "-t", "rc4");

        Assert.Equal(7, code);
        Assert.Contains("rc4 exists and its owner is gone", File.ReadAllLines(ArgvFile)[^1], StringComparison.Ordinal);
        Assert.Contains("'Team rc4 exists", stderr, StringComparison.Ordinal);
        Assert.Equal(before, TeamDigest(path));
        Assert.Equal(["rc4"], Graph(StubBin).Roster.ListTeamNames(HierarchyDir));
    }

    [Fact]
    public void ADryRunLaunchesNothing()
    {
        Assert.Equal(0, AhdNew("jim", "--dry-run").Code);
        Assert.False(File.Exists(ArgvFile));
    }

    // ---------------------------------------------------------------- -i

    [Fact]
    public void InteractiveWithoutATerminalIsRefusedAndAFlagRunIsNot()
    {
        // The test host's stdin is a pipe; on a terminal this row is the manual check.
        if (!Console.IsInputRedirected)
        {
            return;
        }

        var refused = AhdNew("jim", "-i");

        AssertRefused("interactive-needs-tty", refused);
        Assert.Contains("pass the flags", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, AhdNew("jim", "--dry-run").Code);
    }

    [Fact]
    public void InteractiveIsTheOnlyWayToOmitTheName()
    {
        if (!Console.IsInputRedirected)
        {
            return;
        }

        AssertRefused("interactive-needs-tty", AhdNew("-i"));
        Assert.Equal(2, AhdNew("--dry-run").Code);
    }

    [Fact]
    public void WithoutATeamThereIsNoPrompt()
    {
        Assert.Equal("auto", DryArgv("jim")[^1]);
    }
}
