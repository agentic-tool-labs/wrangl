using System.Globalization;
using System.Text;
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
/// <c>role</c> and <c>pack</c> over the real libraries, a sandbox repository and an installed role pack: what each writes and where,
/// that a pack's role is adopted only on the pin its dry run printed, and every refusal beside its nearest accepted input.
/// </summary>
[Collection("RosterServices")]
public sealed class RosterRoleVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private bool _launchless;

    public RosterRoleVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "ah2"));
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string RepoFile => Path.Combine(Project, ".claude", "ah2", "agent-hierarchy.json");

    private string PackDir => Path.Combine(_sandbox.Root, "packs", "demo");

    private string AgentFile(string name) => Path.Combine(Project, ".claude", "agents", name + ".md");

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd, bool launchless) : IRosterCliServices
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

        public IRosterLaunch? Launch => launchless ? null : graph.Launch;

        public IRosterCallerRegistry? Callers => null;
    }

    private (int Code, string Stdout, string Stderr) Roster(params string[] args) => RosterWith(new FakeRunner(), args);

    private (int Code, string Stdout, string Stderr) RosterWith(FakeRunner runner, params string[] args)
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
            ["PATH"] = _sandbox.Bin,
            ["WRANGL_HIERARCHY_DIR"] = Path.Combine(Project, ".claude", "ah2"),
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, runner, new FakeDaemonClient());
        var services = new Services(graph, io.Files, io.Clock, env, Project, _launchless);
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run([.. args, "--cwd", Project], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Parse(string text) => JsonText.Parse(Encoding.UTF8.GetBytes(text)).AsObject();

    private void InstallPack()
    {
        Directory.CreateDirectory(Path.Combine(PackDir, "agents"));
        Directory.CreateDirectory(Path.Combine(PackDir, ".claude-plugin"));
        File.WriteAllText(Path.Combine(PackDir, ".claude-plugin", "plugin.json"), "{\"name\":\"demo\",\"version\":\"2.0.0\"}");
        File.WriteAllText(
            Path.Combine(PackDir, "ah-roles.json"),
            "{\"version\":1,\"roles\":{\"qc\":{\"class\":\"review\",\"label\":\"QC\",\"description\":\"Checks things\",\"routes\":\"qc work\",\"model\":\"sonnet\"}}}");
        File.WriteAllText(
            Path.Combine(PackDir, "agents", "qc.md"),
            "---\nname: qc\ndescription: Checks things\ntools: Read, Grep, Glob, Bash, SendMessage\n---\n\nYou check things.\n");
        var plugins = Path.Combine(_sandbox.Home, ".claude", "plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(
            Path.Combine(plugins, "installed_plugins.json"),
            "{\"plugins\":{\"demo@market\":[{\"scope\":\"user\",\"installPath\":" + JsonText.Stringify(JsonValue.FromString(PackDir), 0) + ",\"version\":\"2.0.0\"}]}}");
    }

    // ---------------------------------------------------------------- pack

    [Fact]
    public void PackListSaysSoWhenNothingIsInstalledAndListsAnInstalledPack()
    {
        var none = Roster("pack", "list");
        Assert.Equal(0, none.Code);
        Assert.Equal("no role packs installed\n", none.Stdout);

        InstallPack();
        var json = Parse(Roster("pack", "list", "--json").Stdout);
        var pack = json["packs"].AsArray()[0].AsObject();
        Assert.Equal("demo", pack["plugin"].AsString());
        Assert.Equal("market", pack["marketplace"].AsString());
        Assert.Equal("2.0.0", pack["version"].AsString());
        var role = pack["roles"].AsArray()[0].AsObject();
        Assert.Equal("qc", role["name"].AsString());
        Assert.Equal("review", role["class"].AsString());
        Assert.Equal("qc", role["agent"].AsString());
        Assert.Empty(role["adopted"].AsArray());
    }

    [Fact]
    public void PackShowReadsAnInstalledPackAndAnUninstalledOneByPath()
    {
        InstallPack();

        var shown = Parse(Roster("pack", "show", "demo", "--json").Stdout);
        Assert.True(shown["installed"].AsBool());
        Assert.StartsWith("sha256:", shown["digest"].AsString(), StringComparison.Ordinal);
        Assert.Equal("qc", shown["roles"].AsArray()[0].AsObject()["name"].AsString());

        var byPath = Parse(Roster("pack", "show", "--path", PackDir, "--json").Stdout);
        Assert.False(byPath["installed"].AsBool());
        Assert.Equal(shown["digest"].AsString(), byPath["digest"].AsString());
    }

    [Fact]
    public void PackRefusesAnUnknownPackAFlagItDoesNotTakeAndANameWithAPath()
    {
        InstallPack();

        var unknown = Roster("pack", "show", "nope");
        var flag = Roster("pack", "list", "--bogus");
        var both = Roster("pack", "show", "demo", "--path", PackDir);
        var missingPath = Roster("pack", "show", "--path", Path.Combine(_sandbox.Root, "absent"));
        var noSub = Roster("pack");

        Assert.Equal(2, unknown.Code);
        Assert.Contains("pack show: no installed role pack nope", unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, flag.Code);
        Assert.Contains("pack: unrecognized flag --bogus (use --json, --path, --cwd)", flag.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, both.Code);
        Assert.Contains("pass a plugin name or --path <dir>, not both", both.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, missingPath.Code);
        Assert.Contains("does not exist", missingPath.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, noSub.Code);
        Assert.Contains("usage: ah roster pack list", noSub.Stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- role list / set / remove

    [Fact]
    public void RoleListNamesTheBuiltInsAndSaysWhereEachIsDefined()
    {
        var json = Parse(Roster("role", "list", "--json").Stdout);

        var roles = json["roles"].AsArray();
        Assert.Contains(roles, r => r.Member("name")!.Value.AsString() == "implementor" && r.Member("builtin")!.Value.AsBool());
        Assert.Empty(json["excluded"].AsArray());
    }

    [Fact]
    public void RoleListPlacesQaWithTheImplementClassBecauseItIsPeerEligibleAndTaskRunnerIsLegwork()
    {
        var roles = Parse(Roster("role", "list", "--json").Stdout)["roles"].AsArray();

        string Placement(string name) => roles.First(r => r.Member("name")!.Value.AsString() == name).Member("placement")!.Value.AsString();
        Assert.Equal("built-in · Implement", Placement("qa"));
        Assert.Equal("legwork", Placement("task-runner"));
        Assert.StartsWith("alt. to", Placement("docs-writer"), StringComparison.Ordinal);
    }

    [Fact]
    public void RoleSetWithANewClassAndNoAgentFileFindsTheMissingFileAndAScaffoldWritesIt()
    {
        var dry = Roster("role", "set", "qc", "--class", "review", "--routes", "qc work", "--description", "Checks things", "--level", "repo", "--dry-run");
        Assert.Equal(0, dry.Code);
        var dryResult = Parse(dry.Stdout);
        Assert.True(dryResult["dry_run"].AsBool());
        Assert.False(File.Exists(RepoFile));
        Assert.False(File.Exists(AgentFile("qc")));

        var scaffolded = Roster(
            "role", "set", "qc", "--class", "review", "--routes", "qc work", "--description", "Checks things", "--level", "repo", "--scaffold", "repo");
        Assert.True(scaffolded.Code == 0, scaffolded.Stderr);
        var result = Parse(scaffolded.Stdout);
        Assert.True(result["written"].AsBool());
        Assert.True(File.Exists(AgentFile("qc")));
        Assert.Contains("name: qc", File.ReadAllText(AgentFile("qc")), StringComparison.Ordinal);
        var row = Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["qc"].AsObject();
        Assert.Equal("review", row["class"].AsString());
        Assert.Equal("qc work", row["routes"].AsString());

        var again = Roster("role", "set", "qc", "--class", "review", "--routes", "qc work", "--description", "Checks things", "--level", "repo", "--scaffold", "repo");
        Assert.Equal(2, again.Code);
        Assert.Contains("--scaffold refuses to overwrite", again.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleSetRefusesAnUnknownClassTheOrchestratorLevelFlagsOfABuiltInAndAValuelessFlag()
    {
        var noClass = Roster("role", "set", "qc", "--level", "repo");
        var builtinLabel = Roster("role", "set", "implementor", "--label", "X", "--level", "repo");
        var valueless = Roster("role", "set", "qc", "--class", "--level", "repo");
        var noName = Roster("role", "set");

        Assert.Equal(2, noClass.Code);
        Assert.Contains("--class is required for a new role", noClass.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, builtinLabel.Code);
        Assert.Contains("--label does not apply to a built-in role — only --agent, --model and --dispatch", builtinLabel.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, valueless.Code);
        Assert.Contains("role set: --class needs a value", valueless.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, noName.Code);
        Assert.Contains("role set needs a role name", noName.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));
    }

    private JsonValue? ParamsOf(string role) =>
        Parse(Roster("role", "list", "--json").Stdout)["roles"].AsArray().First(r => r.Member("name")!.Value.AsString() == role).Member("params");

    /// <summary>A1 (role-params RP-D1/D2, Amendment 1 Q2): --params writes the array and resolve keeps it, for a custom and a built-in role; "" removes it.</summary>
    [Fact]
    public void RoleSetParamsIsKeptThroughResolveAndAnEmptyValueRemovesIt()
    {
        var set = Roster("role", "set", "qc", "--class", "review", "--routes", "qc work", "--description", "Checks things", "--level", "repo", "--scaffold", "repo", "--params", "a, b");
        Assert.True(set.Code == 0, set.Stderr);
        Assert.Equal(["a", "b"], ParamsOf("qc")!.Value.AsArray().Select(x => x.AsString()).ToArray());

        var builtin = Roster("role", "set", "architect", "--params", "a", "--level", "repo");
        Assert.True(builtin.Code == 0, builtin.Stderr);
        Assert.Equal(["a"], ParamsOf("architect")!.Value.AsArray().Select(x => x.AsString()).ToArray());
        Assert.Contains("params: a", Roster("role", "list").Stdout, StringComparison.Ordinal);

        Assert.Equal(0, Roster("role", "set", "qc", "--params", "", "--level", "repo").Code);
        Assert.Null(ParamsOf("qc"));
        Assert.False(Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["qc"].AsObject().ContainsKey("params"));
    }

    /// <summary>Negative: a bad name, a repeat and nine names are refused and nothing is written.</summary>
    [Theory]
    [InlineData("Bad_Name")]
    [InlineData("a,a")]
    [InlineData("a,b,c,d,e,f,g,h,i")]
    public void RoleSetRefusesParamsThatDoNotFit(string names)
    {
        var run = Roster("role", "set", "architect", "--params", names, "--level", "repo");

        Assert.Equal(2, run.Code);
        Assert.Contains("--params:", run.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));
    }

    /// <summary>Negative: a hand-edited params that is not an array of names is dropped and the role stays usable.</summary>
    [Theory]
    [InlineData("\"x\"")]
    [InlineData("[\"Bad_Name\"]")]
    public void AMalformedParamsIsDroppedAndTheRoleKept(string declared)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AgentFile("qc"))!);
        File.WriteAllText(AgentFile("qc"), "---\nname: qc\ndescription: Checks things\ntools: Read, Grep, Glob, Bash, SendMessage\n---\n\nYou check things.\n");
        File.WriteAllText(RepoFile, "{\"version\":1,\"roles\":{\"qc\":{\"class\":\"review\",\"params\":" + declared + "},\"architect\":{\"params\":" + declared + "}}}");

        var listed = Roster("role", "list", "--json");

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home }, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var warnings = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient()).Config.ResolveConfig(Project)["warnings"].AsArray().Select(w => w.AsString()).ToList();

        Assert.Equal(0, listed.Code);
        Assert.Single(warnings, w => w.StartsWith("ah: custom role \"qc\": params ignored:", StringComparison.Ordinal));
        Assert.Single(warnings, w => w.StartsWith("ah: role architect: params ignored:", StringComparison.Ordinal));
        Assert.Null(ParamsOf("qc"));
        Assert.Null(ParamsOf("architect"));
    }

    private JsonObject? ParamRow(string name) =>
        Parse(Roster("param", "list", "--json").Stdout)["params"].AsArray().Select(r => r.AsObject()).FirstOrDefault(r => r["name"].AsString() == name);

    /// <summary>A2 (RP-D3/D4): repo-user beats repo beats global for one name; a declared name no level sets reads missing.</summary>
    [Fact]
    public void AParamResolvesRepoUserThenRepoThenGlobalAndAnUnsetDeclaredNameIsMissing()
    {
        foreach (var level in new[] { "global", "repo", "repo-user" })
        {
            var set = Roster("param", "set", "engine-profile", "/p/" + level, "--level", level);
            Assert.True(set.Code == 0, set.Stderr);
        }

        Assert.Equal(0, Roster("role", "set", "architect", "--params", "engine-profile,asset-root", "--level", "repo").Code);
        var init = Roster("init", "repo", "--route", "peer");
        Assert.True(init.Code == 0, init.Stderr);
        var add = Roster("add", "repo", "--role", "architect");
        Assert.True(add.Code == 0, add.Stderr);

        string Seen() => $"{ParamRow("engine-profile")!["value"].AsString()} ({ParamRow("engine-profile")!["level"].AsString()})";
        Assert.Equal("/p/repo-user (repo-user)", Seen());
        Assert.Equal(0, Roster("param", "unset", "engine-profile").Code);
        Assert.Equal("/p/repo (repo)", Seen());
        Assert.Equal(0, Roster("param", "unset", "engine-profile", "--level", "repo").Code);
        Assert.Equal("/p/global (global)", Seen());

        var missing = ParamRow("asset-root")!;
        Assert.Equal(JsonValue.Null, missing["value"]);
        Assert.Equal(["architect"], missing["roles"].AsArray().Select(r => r.AsString()).ToArray());
        Assert.Contains("asset-root: missing — declared by architect", Roster("param", "list").Stdout, StringComparison.Ordinal);
    }

    /// <summary>Negative: a value with a newline, one of 501 characters and a bad name are refused and nothing is written; a name no role declares is written.</summary>
    [Theory]
    [InlineData("engine-profile", "a\nb", 2)]
    [InlineData("engine-profile", null, 2)]
    [InlineData("Bad_Name", "x", 2)]
    [InlineData("undeclared", "x", 0)]
    public void ParamSetRefusesABadNameOrValueButNotAnUndeclaredName(string name, string? value, int code)
    {
        var run = Roster("param", "set", name, value ?? new string('x', 501), "--level", "repo");

        Assert.Equal(code, run.Code);
        Assert.Equal(code == 0, File.Exists(RepoFile));
    }

    /// <summary>Negative: unsetting a name no level sets is not found (exit 1).</summary>
    [Fact]
    public void ParamUnsetOfAnAbsentNameIsNotFound()
    {
        Assert.Equal(1, Roster("param", "unset", "engine-profile").Code);
        Assert.Equal(0, Roster("param", "set", "engine-profile", "x", "--level", "repo").Code);
        Assert.Equal(1, Roster("param", "unset", "engine-profile", "--level", "global").Code);
    }

    [Fact]
    public void RoleRemoveTakesTheRowOutAndRefusesARoleNothingDefines()
    {
        Assert.Equal(
            0,
            Roster("role", "set", "qc", "--class", "review", "--routes", "qc work", "--description", "Checks things", "--level", "repo", "--scaffold", "repo").Code);

        var removed = Roster("role", "remove", "qc", "--level", "repo");
        Assert.Equal(0, removed.Code);
        Assert.Equal("qc", Parse(removed.Stdout)["removed"].AsString());
        Assert.False(Parse(File.ReadAllText(RepoFile)).ContainsKey("roles") && Parse(File.ReadAllText(RepoFile))["roles"].AsObject().ContainsKey("qc"));

        var again = Roster("role", "remove", "qc", "--level", "repo");
        Assert.Equal(2, again.Code);
        Assert.Contains("role remove qc: no row for it at level \"repo\"", again.Stderr, StringComparison.Ordinal);
        var builtin = Roster("role", "remove", "implementor", "--level", "repo");
        Assert.Equal(2, builtin.Code);
        Assert.Contains("role remove implementor: no row for it at level \"repo\"", builtin.Stderr, StringComparison.Ordinal);
    }

    private string GlobalFile => Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");

    private static void WriteRoles(string file, string roles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"version\":1,\"enabled\":true,\"roles\":" + roles + "}\n");
    }

    [Fact]
    public void RoleRemoveDeletesAFormerCustomRowWithNothingElseOnItAtThatLevelAndNothingAtAnotherLevel()
    {
        const string Former = "{\"class\":\"implement\",\"agent\":\"my-docs\",\"routes\":\"x\"}";
        WriteRoles(RepoFile, "{\"docs-writer\":" + Former + ",\"reviewer\":{\"model\":\"opus\"}}");
        WriteRoles(GlobalFile, "{\"docs-writer\":" + Former + "}");
        var globalBefore = File.ReadAllText(GlobalFile);

        var removed = Roster("role", "remove", "docs-writer", "--level", "repo");

        Assert.True(removed.Code == 0, removed.Stderr);
        Assert.False(Parse(removed.Stdout)["override_only"].AsBool());
        var roles = Parse(File.ReadAllText(RepoFile))["roles"].AsObject();
        Assert.False(roles.ContainsKey("docs-writer"));
        Assert.True(roles.ContainsKey("reviewer"));
        Assert.Equal(globalBefore, File.ReadAllText(GlobalFile));
    }

    [Fact]
    public void RoleRemoveOfAFormerCustomRowKeepsTheModelDispatchAndPeerStoredBesideTheOldKeys()
    {
        WriteRoles(RepoFile, "{\"docs-writer\":{\"class\":\"implement\",\"label\":\"Docs\",\"routes\":\"x\",\"agent\":\"my-docs\",\"model\":\"opus\",\"dispatch\":\"subagent\",\"peer\":\"confirm\"}}");

        var removed = Roster("role", "remove", "docs-writer", "--level", "repo");

        Assert.True(removed.Code == 0, removed.Stderr);
        Assert.False(Parse(removed.Stdout)["override_only"].AsBool());
        var row = Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["docs-writer"].AsObject();
        Assert.Equal(["dispatch", "model", "peer"], row.Keys.Order().ToList());
        Assert.Equal("opus", row["model"].AsString());
    }

    [Fact]
    public void RoleRemoveOnAClassAndAgentRowOfAnAlwaysBuiltInRoleRemovesOnlyTheAgent()
    {
        WriteRoles(RepoFile, "{\"reviewer\":{\"class\":\"review\",\"agent\":\"my-rev\",\"model\":\"opus\"}}");

        var removed = Roster("role", "remove", "reviewer", "--level", "repo");

        Assert.True(removed.Code == 0, removed.Stderr);
        Assert.True(Parse(removed.Stdout)["override_only"].AsBool());
        var row = Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["reviewer"].AsObject();
        Assert.Equal(["class", "model"], row.Keys.Order().ToList());
    }

    [Fact]
    public void RoleRemoveStillRefusesABuiltInRowWithNoClassAndNoAgent()
    {
        WriteRoles(RepoFile, "{\"docs-writer\":{\"model\":\"opus\"}}");

        var refused = Roster("role", "remove", "docs-writer", "--level", "repo");

        Assert.Equal(2, refused.Code);
        Assert.Contains("a built-in role cannot be removed, and it has no agent override at level \"repo\"", refused.Stderr, StringComparison.Ordinal);
        Assert.True(Parse(File.ReadAllText(RepoFile))["roles"].AsObject().ContainsKey("docs-writer"));
    }

    [Fact]
    public void RoleRemoveOfAnAgentOverrideOnDocsWriterKeepsTheRestOfTheRow()
    {
        WriteRoles(RepoFile, "{\"docs-writer\":{\"agent\":\"my-docs\",\"model\":\"opus\"}}");

        var removed = Roster("role", "remove", "docs-writer", "--level", "repo");

        Assert.True(removed.Code == 0, removed.Stderr);
        Assert.True(Parse(removed.Stdout)["override_only"].AsBool());
        var row = Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["docs-writer"];
        Assert.Equal("opus", row.Member("model")!.Value.AsString());
        Assert.Null(row.Member("agent"));
    }

    [Fact]
    public void RoleSetRefusesAClassOnDocsWriterAsOnAnyBuiltIn()
    {
        var refused = Roster("role", "set", "docs-writer", "--class", "implement", "--level", "repo");

        Assert.Equal(2, refused.Code);
        Assert.Contains("--class does not apply to a built-in role", refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleSetStoresAModelOverrideForDocsWriter()
    {
        var set = Roster("role", "set", "docs-writer", "--model", "opus", "--level", "repo");

        Assert.True(set.Code == 0, set.Stderr);
        Assert.Equal("opus", Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["docs-writer"].Member("model")!.Value.AsString());
    }

    [Fact]
    public void RoleListShowsDocsWriterAsAnAlternativeToTheImplementor()
    {
        var list = Roster("role", "list");

        Assert.True(list.Code == 0, list.Stderr);
        Assert.Contains("alt. to Implementor: Standalone documentation outside of code", list.Stdout, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- adopting a pack role

    [Fact]
    public void AdoptingAPackRoleNeedsTheExactPinItsDryRunPrintedAndStoresACopy()
    {
        InstallPack();
        var from = "demo@market:qc";

        var dry = Roster("role", "set", "qc", "--from", from, "--level", "repo", "--dry-run");
        Assert.True(dry.Code == 0, dry.Stderr);
        var dryResult = Parse(dry.Stdout);
        var pin = dryResult["pin"].AsString();
        Assert.StartsWith("sha256:", pin, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));

        var noPin = Roster("role", "set", "qc", "--from", from, "--level", "repo");
        Assert.Equal(2, noPin.Code);
        Assert.Contains("committing needs --pin", noPin.Stderr, StringComparison.Ordinal);

        var wrong = Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", "sha256:" + new string('0', 64));
        Assert.Equal(2, wrong.Code);
        Assert.Contains("doesn't match what is installed now", wrong.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));

        var committed = Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", pin);
        Assert.True(committed.Code == 0, committed.Stderr);
        Assert.True(Parse(committed.Stdout)["written"].AsBool());
        var row = Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["qc"].AsObject();
        Assert.Equal(from, row["from"].AsString());
        Assert.Equal(pin, row["pin"].AsString());
        Assert.True(File.Exists(Path.Combine(_sandbox.Home, ".claude", "ah2", "trusted", pin["sha256:".Length..] + ".json")));

        var trust = Parse(Roster("role", "trust", "qc", "--level", "repo", "--dry-run").Stdout);
        Assert.True(trust["stored_copy"].AsBool());
        Assert.Empty(trust["fields_changed"].AsArray());
        Assert.Equal(pin, trust["pin"].AsString());
    }

    private string AdoptQa()
    {
        InstallPack();
        var from = "demo@market:qc";
        var pin = Parse(Roster("role", "set", "qc", "--from", from, "--level", "repo", "--dry-run").Stdout)["pin"].AsString();
        Assert.Equal(0, Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", pin).Code);
        File.WriteAllText(
            Path.Combine(PackDir, "agents", "qc.md"),
            "---\nname: qc\ndescription: Checks things\ntools: Read, Grep, Glob, Bash, SendMessage\n---\n\nYou check things carefully.\n");
        return pin;
    }

    [Fact]
    public void TrustDryRunShowsTheSystemDiffOfTheStoredCopyAgainstTheInstalledAgentFile()
    {
        AdoptQa();
        var seen = new List<(string Trusted, string Installed)>();
        var runner = new FakeRunner
        {
            Exit = _ => 1,
            Output = argv =>
            {
                seen.Add((File.ReadAllText(argv[2]), File.ReadAllText(argv[3])));
                return "--- trusted\n+++ installed\n-You check things.\n+You check things carefully.\n";
            },
        };

        var trust = Parse(RosterWith(runner, "role", "trust", "qc", "--level", "repo", "--dry-run").Stdout);

        Assert.Equal("--- trusted\n+++ installed\n-You check things.\n+You check things carefully.\n", trust["agent_diff"].AsString());
        var call = Assert.Single(runner.Calls, argv => argv[0] == "diff");
        Assert.Equal("-u", call[1]);
        Assert.Equal("trusted", Path.GetFileName(call[2]));
        Assert.Equal("installed", Path.GetFileName(call[3]));
        Assert.Equal(Path.GetDirectoryName(call[2]), Path.GetDirectoryName(call[3]));
        Assert.False(Directory.Exists(Path.GetDirectoryName(call[2])));
        var files = Assert.Single(seen);
        Assert.Contains("You check things.", files.Trusted, StringComparison.Ordinal);
        Assert.Contains("You check things carefully.", files.Installed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(127)]
    public void TrustDryRunShowsBothTextsInFullWhenTheDiffCommandFails(int exit)
    {
        AdoptQa();
        var runner = new FakeRunner { Exit = _ => exit, Output = _ => "ignored" };

        var diff = Parse(RosterWith(runner, "role", "trust", "qc", "--level", "repo", "--dry-run").Stdout)["agent_diff"].AsString();

        Assert.StartsWith("--- trusted\n", diff, StringComparison.Ordinal);
        Assert.Contains("\n+++ installed\n", diff, StringComparison.Ordinal);
        Assert.Contains("You check things carefully.", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustDryRunShowsBothTextsInFullWhenThereIsNoProcessRunner()
    {
        AdoptQa();
        _launchless = true;

        var diff = Parse(Roster("role", "trust", "qc", "--level", "repo", "--dry-run").Stdout)["agent_diff"].AsString();

        Assert.StartsWith("--- trusted\n", diff, StringComparison.Ordinal);
        Assert.Contains("\n+++ installed\n", diff, StringComparison.Ordinal);
        Assert.Contains("You check things carefully.", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustDryRunRunsNoDiffWhenTheAgentFileIsUnchanged()
    {
        InstallPack();
        var from = "demo@market:qc";
        var pin = Parse(Roster("role", "set", "qc", "--from", from, "--level", "repo", "--dry-run").Stdout)["pin"].AsString();
        Assert.Equal(0, Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", pin).Code);
        var runner = new FakeRunner { Exit = _ => 1 };

        var trust = Parse(RosterWith(runner, "role", "trust", "qc", "--level", "repo", "--dry-run").Stdout);

        Assert.Equal(string.Empty, trust["agent_diff"].AsString());
        Assert.DoesNotContain(runner.Calls, argv => argv[0] == "diff");
    }

    [Fact]
    public void RoleTrustRefusesARoleThatIsNotAdoptedFromAPack()
    {
        Assert.Equal(
            0,
            Roster("role", "set", "qc", "--class", "review", "--routes", "qc work", "--description", "Checks things", "--level", "repo", "--scaffold", "repo").Code);

        var refused = Roster("role", "trust", "qc", "--level", "repo", "--dry-run");

        Assert.Equal(2, refused.Code);
        Assert.Contains("it isn't adopted from a pack at level \"repo\"", refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AdoptingRefusesAMalformedFromAnUninstalledPluginAndAFlagTheFromFormTakesNoPartIn()
    {
        InstallPack();

        var malformed = Roster("role", "set", "qc", "--from", "nope", "--level", "repo", "--dry-run");
        var uninstalled = Roster("role", "set", "qc", "--from", "ghost:qc", "--level", "repo", "--dry-run");
        var withClass = Roster("role", "set", "qc", "--from", "demo@market:qc", "--class", "review", "--level", "repo", "--dry-run");

        Assert.Equal(2, malformed.Code);
        Assert.Contains("--from must be <plugin>@<marketplace>:<role>, got \"nope\"", malformed.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, uninstalled.Code);
        Assert.Contains("plugin ghost is not installed", uninstalled.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, withClass.Code);
        Assert.Contains("--class can't be used with --from", withClass.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AdoptingLeavesNoLevelRowAndNoTemporaryFileWhenTheStoredCopyCannotBeReplaced()
    {
        InstallPack();
        var from = "demo@market:qc";
        var pin = Parse(Roster("role", "set", "qc", "--from", from, "--level", "repo", "--dry-run").Stdout)["pin"].AsString();
        // the stored copy's path is a non-empty directory, so the final rename fails after the temporary file was flushed
        var stored = Path.Combine(_sandbox.Home, ".claude", "ah2", "trusted", pin["sha256:".Length..] + ".json");
        Directory.CreateDirectory(stored);
        File.WriteAllText(Path.Combine(stored, "keep"), "x");

        var committed = Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", pin);

        Assert.NotEqual(0, committed.Code);
        Assert.False(File.Exists(RepoFile));
        Assert.True(File.Exists(Path.Combine(stored, "keep")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(stored)!, ".*.tmp"));
    }
}
