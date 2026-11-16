using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah allow on|off|list</c> and <c>roster permissions show</c> over the real libraries and a sandbox, with the owner proof supplied by a fake.</summary>
[Collection("RosterServices")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MemberAllowVerbTests : IDisposable
{
    private const string ProofQuestion = "Widen member launch rules?";
    private readonly Sandbox _sandbox = new();
    private bool _noPlugin;

    public MemberAllowVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Hierarchy);
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string Hierarchy => Path.Combine(Project, ".claude", "ah2");

    private string Asks => Path.Combine(Hierarchy, "asks.jsonl");

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private sealed class FakeProof(bool user, bool orchestrator) : IRosterOwnerProof
    {
        public bool IsUser() => user;

        public OwnerProofResult Check(string dir, string? team) =>
            orchestrator ? new(true, null, "orchestrator", false, null) : new(false, "refused", "not-owner", false, null);

        public OwnerActor Actor() => new(4242, "Thu Oct 15 20:38:50 2026", [], "ah allow");

        public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) => new(NearestKind.None);
    }

    private sealed class Services(ServiceGraph graph, PlatformIo io, IAhEnvironment environment, string cwd, IRosterOwnerProof? proof) : IRosterCliServices
    {
        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => io.Clock;

        public IFileReader Files => io.Files;

        public IRawFileWriter? Writer => io.Writer;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers => null;

        public IRosterOwnerProof? OwnerProof => proof;
    }

    private (ServiceGraph Graph, PlatformIo Io, IAhEnvironment Env) Build()
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["PATH"] = _sandbox.Bin,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
        };
        var plugin = _noPlugin ? null : new PluginInfo(Path.Combine(_sandbox.Root, "plugin"), "wrangl", "1.2.3", "1");
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, plugin);
        var io = PlatformModule.CreateIo(null);
        return (ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient()), io, env);
    }

    private string UserConfig => Build().Graph.Config.UserConfigPath();

    private string ProjectConfig => Build().Graph.Config.ProjectConfigPath(Project)!;

    private (int Code, string Stdout, string Stderr) Run(bool user, bool orchestrator, params string[] args)
    {
        var (graph, io, env) = Build();
        CommandsModule.ComposeRoster(() => new Services(graph, io, env, Project, new FakeProof(user, orchestrator)));
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run([.. args, "--cwd", Project], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Stdout, string Stderr) AsUser(params string[] args) => Run(true, false, args);

    private (int Code, string Stdout, string Stderr) AsOrchestrator(params string[] args) => Run(false, true, args);

    private (int Code, string Stdout, string Stderr) AsMember(params string[] args) => Run(false, false, args);

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private void Append(string line)
    {
        File.AppendAllText(Asks, line + "\n");
    }

    private void SeedConsent(string label, DateTime at, int cv = 2, bool writer = true)
    {
        var q = ConfigModule.MemberLaunchConsentQuestion;
        var asked = "[{\"question\":\"" + q + "\",\"header\":\"Member rules\",\"multiSelect\":false,\"options\":[{\"label\":\"Allow\",\"description\":\"d\"},{\"label\":\"Not now\",\"description\":\"d\"}]}]";
        Append("{\"at\":\"" + Stamp(at) + "\"" + (writer ? ",\"writer\":\"" + ConfigModule.AskRecordWriter + "\"" : string.Empty)
            + ",\"session_id\":\"s\",\"questions\":" + asked + ",\"response\":{\"questions\":" + asked + ",\"answers\":{\"" + q + "\":\"" + label + "\"},\"annotations\":{}},\"metadata\":{\"source\":\"ah:R7;consent=member-launch-rules;cv=" + cv + "\"}}");
    }

    private void SeedProof(DateTime at, string answer = "Allow", string? source = null, bool writer = true, string? question = null)
    {
        var key = question ?? ProofQuestion;
        Append("{\"at\":\"" + Stamp(at) + "\"" + (writer ? ",\"writer\":\"" + ConfigModule.AskRecordWriter + "\"" : string.Empty)
            + ",\"session_id\":\"s\",\"questions\":[],\"response\":{\"answers\":{\"" + key + "\":\"" + answer + "\"}},\"metadata\":{\"source\":\"" + (source ?? ConfigModule.AllowOnSource) + "\"}}");
    }

    private JsonObject ReadUserConfig() => JsonText.Parse(File.ReadAllText(UserConfig)).AsObject();

    private JsonObject ListJson() => JsonText.Parse(Encoding.UTF8.GetBytes(AsMember("member-allow", "list", "--json").Stdout)).AsObject();

    [Fact]
    public void OffByTheUserWritesEnabledFalseAndKeepsTheOtherKeys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserConfig)!);
        File.WriteAllText(UserConfig, "{\"keep\":1}");

        var (code, stdout, stderr) = AsUser("member-allow", "off");

        Assert.True(code == 0, stderr);
        Assert.Contains("off", stdout, StringComparison.Ordinal);
        var config = ReadUserConfig();
        Assert.False(config["member_allow"].Member("enabled")!.Value.AsBool());
        Assert.Equal(1, config["keep"].AsNumber());
    }

    [Fact]
    public void OffByTheOrchestratorIsAllowedAndByAMemberIsRefusedWithNothingWritten()
    {
        var refused = AsMember("member-allow", "off");

        Assert.NotEqual(0, refused.Code);
        Assert.Contains("members cannot change member launch rules", refused.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(UserConfig));
        Assert.Equal(0, AsOrchestrator("member-allow", "off").Code);
        Assert.False(ReadUserConfig()["member_allow"].Member("enabled")!.Value.AsBool());
    }

    [Fact]
    public void AFalseInTheUserConfigWinsOverATrueInTheProjectConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectConfig)!);
        File.WriteAllText(ProjectConfig, "{\"member_allow\":{\"enabled\":true}}");
        _ = AsUser("member-allow", "off");

        Assert.False(ListJson()["enabled"].AsBool());
        Assert.Equal("off", ListJson()["consent"].Member("state")!.Value.AsString());
    }

    [Fact]
    public void ATrueInTheUserConfigDoesNotOverrideAFalseInTheProjectConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectConfig)!);
        File.WriteAllText(ProjectConfig, "{\"member_allow\":{\"enabled\":false}}");
        _ = AsUser("member-allow", "on");

        Assert.False(ListJson()["enabled"].AsBool());
    }

    [Fact]
    public void OnByTheUserEnablesAndAsksForNothing()
    {
        _ = AsUser("member-allow", "off");

        var (code, stdout, stderr) = AsUser("member-allow", "on");

        Assert.True(code == 0, stderr);
        Assert.DoesNotContain("consent needed", stdout, StringComparison.Ordinal);
        Assert.True(ReadUserConfig()["member_allow"].Member("enabled")!.Value.AsBool());
        Assert.Equal("given", ListJson()["consent"].Member("state")!.Value.AsString());
    }

    [Fact]
    public void OnByAMemberIsRefusedAndWritesNothing()
    {
        var (code, _, stderr) = AsMember("member-allow", "on");

        Assert.NotEqual(0, code);
        Assert.Contains("members cannot change member launch rules", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(UserConfig));
    }

    [Fact]
    public void OnByTheOrchestratorWithNoProofIsRefusedAndWritesNothing()
    {
        var (code, _, stderr) = AsOrchestrator("member-allow", "on");

        Assert.NotEqual(0, code);
        Assert.Contains("widening needs the user or a fresh Allow proof (ah:R7;allow=on:member-launch-rules)", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(UserConfig));
    }

    [Fact]
    public void OnByTheOrchestratorWithAFreshAllowProofWorksOnceOnly()
    {
        SeedProof(DateTime.UtcNow.AddMinutes(-1));

        var first = AsOrchestrator("member-allow", "on");
        var second = AsOrchestrator("member-allow", "on");

        Assert.True(first.Code == 0, first.Stderr);
        Assert.NotEqual(0, second.Code);
        Assert.Contains("widening needs", second.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("older")]
    [InlineData("not-allow")]
    [InlineData("other-source")]
    [InlineData("no-writer")]
    [InlineData("other-question")]
    public void OnByTheOrchestratorIsRefusedForAnythingButAFreshHookWrittenAllow(string flaw)
    {
        switch (flaw)
        {
            case "other-question":
                SeedProof(DateTime.UtcNow.AddMinutes(-1), question: "Something else entirely?");
                break;
            case "older":
                SeedProof(DateTime.UtcNow.AddMinutes(-11));
                break;
            case "not-allow":
                SeedProof(DateTime.UtcNow.AddMinutes(-1), answer: "Not now");
                break;
            case "other-source":
                SeedProof(DateTime.UtcNow.AddMinutes(-1), source: "ah:R7;allow=on:something-else");
                break;
            default:
                SeedProof(DateTime.UtcNow.AddMinutes(-1), writer: false);
                break;
        }

        var (code, _, stderr) = AsOrchestrator("member-allow", "on");

        Assert.NotEqual(0, code);
        Assert.Contains("widening needs", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(UserConfig));
    }

    [Fact]
    public void OnAfterADeclineMakesTheRulesGivenUntilANewerDeclineArrives()
    {
        SeedConsent("Not now", DateTime.UtcNow.AddMinutes(-30));
        Assert.Equal("declined", ListJson()["consent"].Member("state")!.Value.AsString());

        _ = AsUser("member-allow", "on");

        Assert.Equal("given", ListJson()["consent"].Member("state")!.Value.AsString());
        SeedConsent("Not now", DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("declined", ListJson()["consent"].Member("state")!.Value.AsString());
    }

    [Fact]
    public void ListNoLongerReportsAReaskField()
    {
        Assert.Null(ListJson().Get("reask"));
        Assert.DoesNotContain("reask", AsMember("member-allow", "list").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWithoutADeclineWritesNoOnTime()
    {
        SeedConsent("Allow", DateTime.UtcNow.AddMinutes(-30));

        _ = AsUser("member-allow", "on");

        Assert.Null(ReadUserConfig()["member_allow"].Member("reask_at"));
    }

    [Theory]
    [InlineData("given", "Allow", 2)]
    [InlineData("declined", "Not now", 2)]
    [InlineData("given", "Allow", 1)]
    public void ListNamesTheConsentStateItsVersionAndTime(string state, string label, int cv)
    {
        SeedConsent(label, DateTime.UtcNow.AddMinutes(-5), cv);

        var consent = ListJson()["consent"];

        Assert.Equal(state, consent.Member("state")!.Value.AsString());
        Assert.Equal(cv, consent.Member("cv")!.Value.AsNumber());
        Assert.True(consent.Member("at")!.Value.IsText);
    }

    [Fact]
    public void ListWithNoConsentRowSaysGivenAndIsTextByDefault()
    {
        Assert.Equal("given", ListJson()["consent"].Member("state")!.Value.AsString());

        var text = AsMember("member-allow", "list").Stdout;

        Assert.Contains("enabled: true", text, StringComparison.Ordinal);
        Assert.Contains("consent: given", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclineRowWithoutTheHookWriterMarkIsIgnored()
    {
        SeedConsent("Not now", DateTime.UtcNow.AddMinutes(-5), writer: false);

        Assert.Equal("given", ListJson()["consent"].Member("state")!.Value.AsString());
    }

    [Fact]
    public void AnUnknownActionAndAnUnknownFlagAreRefused()
    {
        Assert.NotEqual(0, AsUser("member-allow", "toggle").Code);
        Assert.NotEqual(0, AsUser("member-allow", "list", "--force").Code);
    }

    [Fact]
    public void ShowPrintsTheLaunchFileWhenThereIsOne()
    {
        Directory.CreateDirectory(Path.Combine(Hierarchy, "launch"));
        File.WriteAllText(Path.Combine(Hierarchy, "launch", "impl.settings.json"), "{\"permissions\":{\"allow\":[\"Bash(git status:*)\"]}}\n");

        var (code, stdout, _) = AsMember("permissions", "show", "impl");

        Assert.Equal(0, code);
        Assert.Contains("Bash(git status:*)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowSaysWhyAMemberHasNoLaunchFile()
    {
        Assert.Contains("no launch file for this member yet", AsMember("permissions", "show", "impl").Stdout, StringComparison.Ordinal);

        SeedConsent("Not now", DateTime.UtcNow.AddMinutes(-5));
        Assert.Contains("inline (no permissions): declined", AsMember("permissions", "show", "impl").Stdout, StringComparison.Ordinal);

        _ = AsUser("member-allow", "off");
        Assert.Contains("inline (no permissions): disabled by ah allow off", AsMember("permissions", "show", "impl").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowSaysTheGeneratorIsUnavailableWithoutAPluginAndRefusesAPathAsAName()
    {
        _noPlugin = true;

        Assert.Contains("inline (no permissions): generator unavailable", AsMember("permissions", "show", "impl").Stdout, StringComparison.Ordinal);
        Assert.NotEqual(0, AsMember("permissions", "show", "../x").Code);
        Assert.NotEqual(0, AsMember("permissions", "show").Code);
    }
}
