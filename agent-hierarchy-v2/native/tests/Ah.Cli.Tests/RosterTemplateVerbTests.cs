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
/// The roster template and config verbs over the real libraries and a sandbox repository: what each writes and where, each
/// refusal with its nearest accepted input, and that a refusal leaves every level file untouched.
/// </summary>
[Collection("RosterServices")]
public sealed class RosterTemplateVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public RosterTemplateVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "ah2"));
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string RepoFile => Path.Combine(Project, ".claude", "ah2", "agent-hierarchy.json");

    private string GlobalFile => Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");

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

    private readonly bool _withPlugin = true;

    private (int Code, string Stdout, string Stderr) Roster(params string[] args) => RosterAs(null, args);

    private (int Code, string Stdout, string Stderr) RosterAs(Dictionary<string, string?>? extra, params string[] args)
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
            ["WRANGL_HIERARCHY_DIR"] = Path.Combine(Project, ".claude", "ah2"),
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                variables[key] = value;
            }
        }

        var env = _withPlugin
            ? new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"))
            : new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, FakeRunner.KnowingSelf(), new FakeDaemonClient());
        var services = new Services(graph, io.Files, io.Clock, env, Project);
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Parse(string text) => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text)).AsObject();

    private void WriteRepoFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RepoFile)!);
        File.WriteAllText(RepoFile, json);
    }

    // ---------------------------------------------------------------- init

    [Fact]
    public void InitWritesAnEmptyBlockAtTheLevelAndReportsIt()
    {
        var (code, stdout, _) = Roster("init", "repo", "--route", "peer", "--cwd", Project);

        Assert.Equal(0, code);
        var result = Parse(stdout);
        Assert.Equal("repo", result["level"].AsString());
        Assert.Equal("roster", result["container"].AsString());
        Assert.Equal("peer", result["roster"].AsObject()["route"].AsString());
        var written = Parse(File.ReadAllText(RepoFile));
        Assert.Equal("peer", written["roster"].AsObject()["route"].AsString());
        Assert.Empty(written["roster"].AsObject()["members"].AsArray());
    }

    [Fact]
    public void InitReplacesAnExistingBlockWholesale()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"layout\":\"grid\",\"members\":[{\"role\":\"implementor\"}]}}");

        var (code, _, _) = Roster("init", "repo", "--route", "peer", "--cwd", Project);

        Assert.Equal(0, code);
        var block = Parse(File.ReadAllText(RepoFile))["roster"].AsObject();
        Assert.Empty(block["members"].AsArray());
        Assert.False(block.ContainsKey("layout"));
    }

    [Theory]
    [InlineData("--route", "subagent", "only legwork roles run as subagents")]
    [InlineData("--route", "bogus", "--route must be \"peer\" or \"subagent\", got \"bogus\"")]
    public void InitRefusesARouteItCannotWrite(string flag, string value, string message)
    {
        var (code, _, stderr) = Roster("init", "repo", flag, value, "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));
    }

    [Fact]
    public void InitWithNoRouteSaysUndefinedAndWritesNothing()
    {
        var (code, _, stderr) = Roster("init", "repo", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Equal("ah roster: --route must be \"peer\" or \"subagent\", got undefined\n", stderr);
        Assert.False(File.Exists(RepoFile));
    }

    [Fact]
    public void InitRefusesALevelFileThatIsNotJsonAndLeavesItAlone()
    {
        WriteRepoFile("{ not json");

        var (code, _, stderr) = Roster("init", "repo", "--route", "peer", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Contains("nothing was written", stderr, StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(RepoFile));
    }

    [Fact]
    public void InitRefusesALevelThatIsNotOne()
    {
        var (code, _, stderr) = Roster("init", "--level", "team", "--route", "peer", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Contains("--level must be one of", stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- remove

    private const string TwoMembers =
        "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[{\"role\":\"implementor\"},{\"role\":\"reviewer\"}]}}";

    [Fact]
    public void RemoveDropsTheNamedMemberAndKeepsTheOthers()
    {
        WriteRepoFile(TwoMembers);

        var (code, stdout, _) = Roster("remove", "repo", "--member", "proj-implementor", "--cwd", Project);

        Assert.Equal(0, code);
        Assert.Equal("proj-implementor", Parse(stdout)["removed"].AsString());
        var members = Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray();
        Assert.Single(members);
        Assert.Equal("reviewer", members[0].AsObject()["role"].AsString());
    }

    [Fact]
    public void RemoveOfAnUnknownMemberIsRefusedAndWritesNothing()
    {
        WriteRepoFile(TwoMembers);

        var (code, _, stderr) = Roster("remove", "repo", "--member", "nobody", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Equal("ah roster: no member named \"nobody\" at level \"repo\"\n", stderr);
        Assert.Equal(TwoMembers, File.ReadAllText(RepoFile));
    }

    [Fact]
    public void RemoveWithoutAMemberFlagIsAUsageError()
    {
        WriteRepoFile(TwoMembers);

        var (code, _, stderr) = Roster("remove", "repo", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Equal("ah roster: remove needs --member <derived-name>\n", stderr);
    }

    // ---------------------------------------------------------------- testing

    [Fact]
    public void TestingShowGivesTheDefaultsThenASetOrderWinsForItsRoleOnly()
    {
        var (_, before, _) = Roster("testing", "show", "--cwd", Project);
        Assert.Equal("touched", Parse(before)["modes"].AsObject()["implementor"].AsString());

        var (setCode, setOut, _) = Roster("testing", "set", "implementor", "full: the whole suite, small repo", "--level", "global", "--cwd", Project);
        var (showCode, showOut, _) = Roster("testing", "show", "--cwd", Project);

        Assert.Equal(0, setCode);
        Assert.Equal("full", Parse(setOut)["mode"].AsString());
        Assert.True(File.Exists(GlobalFile));
        Assert.Equal(0, showCode);
        var shown = Parse(showOut);
        Assert.Equal("full: the whole suite, small repo", shown["testing"].AsObject()["implementor"].AsString());
        Assert.Equal("full", shown["modes"].AsObject()["implementor"].AsString());
        Assert.Equal("none", shown["modes"].AsObject()["reviewer"].AsString());

        // on an empty global file the set writes only preference keys, so the file never becomes a config layer
        Assert.All(Parse(File.ReadAllText(GlobalFile)).Keys, k => Assert.True(k is "version" or "testing", k));
    }

    /// <summary>A key that is neither a registry role nor e4 is stored with a warning; the nearest non-match, a role, warns nothing.</summary>
    [Fact]
    public void TestingSetWarnsOnAnUnknownKeyButStoresIt()
    {
        var (code, _, err) = Roster("testing", "set", "implementer", "full: typo", "--level", "global", "--cwd", Project);
        Assert.Equal(0, code);
        Assert.Contains("neither a registry role nor e4", err, StringComparison.Ordinal);

        var (roleCode, _, roleErr) = Roster("testing", "set", "implementor", "full: x", "--level", "global", "--cwd", Project);
        Assert.Equal(0, roleCode);
        Assert.DoesNotContain("neither a registry role", roleErr, StringComparison.Ordinal);
    }

    /// <summary>Negative: a set with no text, or an unknown flag, is refused and writes nothing.</summary>
    [Fact]
    public void TestingSetWithoutTextOrWithAnUnknownFlagIsRefused()
    {
        Assert.NotEqual(0, Roster("testing", "set", "implementor", "--level", "global", "--cwd", Project).Code);
        Assert.NotEqual(0, Roster("testing", "show", "--global", "--cwd", Project).Code);
        Assert.False(File.Exists(GlobalFile));
    }

    // ---------------------------------------------------------------- tier

    [Fact]
    public void TierSetThenListShowsTheDeclaration()
    {
        var (setCode, setOut, _) = Roster("tier", "set", "codex", "gpt-x", "opus", "--cwd", Project);
        var (listCode, listOut, _) = Roster("tier", "list", "--cwd", Project);

        Assert.Equal(0, setCode);
        Assert.Equal("opus", Parse(setOut)["tier"].AsString());
        Assert.Equal(0, listCode);
        Assert.Equal("opus", Parse(listOut)["modelTiers"].AsObject()["codex"].AsObject()["gpt-x"].AsString());
        Assert.True(File.Exists(GlobalFile));
    }

    [Fact]
    public void TierRemoveDeletesTheLastModelAndTheKindAndReportsAnAbsentOneAsNotRemoved()
    {
        _ = Roster("tier", "set", "codex", "gpt-x", "opus", "--cwd", Project);

        var (_, first, _) = Roster("tier", "remove", "codex", "gpt-x", "--cwd", Project);
        var (_, second, _) = Roster("tier", "remove", "codex", "gpt-x", "--cwd", Project);

        Assert.True(Parse(first)["removed"].AsBool());
        Assert.False(Parse(second)["removed"].AsBool());
        Assert.False(Parse(File.ReadAllText(GlobalFile)).ContainsKey("modelTiers"));
    }

    [Theory]
    [InlineData("claude", "m", "opus", "a Claude model carries its own tier")]
    [InlineData("codex", "m", "gold", "the tier must be one of")]
    [InlineData("Codex", "m", "opus", "kind must match ^[a-z][a-z0-9-]*$")]
    [InlineData("codex", "has space", "opus", "model must be a non-empty string")]
    public void TierSetRefusesWhatItCannotRecord(string kind, string model, string tier, string message)
    {
        var (code, _, stderr) = Roster("tier", "set", kind, model, tier, "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(GlobalFile));
    }

    [Fact]
    public void TierRefusesAFlagItDoesNotTake()
    {
        var (code, _, stderr) = Roster("tier", "list", "--level", "repo", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Equal("ah roster: tier: unrecognized flag --level (use --cwd)\n", stderr);
    }

    // ---------------------------------------------------------------- the removed verbs

    [Theory]
    [InlineData("layout", "layout was removed")]
    [InlineData("alias", "alias was removed")]
    public void ALayoutOrAliasVerbOnlyExplainsWhereTheChoiceMovedTo(string verb, string message)
    {
        var (code, stdout, stderr) = Roster(verb, "list", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, stdout);
        Assert.StartsWith("ah roster: " + message, stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- named rosters

    [Fact]
    public void RosterUseOfAnUndefinedRosterIsRefusedAndWritesNothing()
    {
        var (code, _, stderr) = Roster("roster", "use", "nope", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Contains("no level defines roster \"nope\"", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));
    }

    [Fact]
    public void RosterCopyThenUseThenDeleteWalksTheLifeOfANamedRoster()
    {
        WriteRepoFile(TwoMembers);

        var (copyCode, copyOut, _) = Roster("roster", "copy", "default", "alt", "--level", "repo", "--cwd", Project);
        var (useCode, _, _) = Roster("roster", "use", "alt", "--level", "repo", "--cwd", Project);
        var (blockedCode, _, blockedErr) = Roster("roster", "delete", "alt", "--level", "repo", "--cwd", Project);
        var (clearCode, _, _) = Roster("roster", "use", "--clear", "--level", "repo", "--cwd", Project);
        var (deleteCode, deleteOut, _) = Roster("roster", "delete", "alt", "--level", "repo", "--cwd", Project);

        Assert.Equal(0, copyCode);
        Assert.Equal(2, (int)Parse(copyOut)["members"].AsNumber());
        Assert.Equal(0, useCode);
        Assert.Equal(2, blockedCode);
        Assert.Contains("activeRoster in", blockedErr, StringComparison.Ordinal);
        Assert.Equal(0, clearCode);
        Assert.Equal(0, deleteCode);
        Assert.True(Parse(deleteOut)["deleted"].AsBool());
        Assert.False(Parse(File.ReadAllText(RepoFile)).ContainsKey("rosters") && Parse(File.ReadAllText(RepoFile))["rosters"].AsObject().ContainsKey("alt"));
    }

    [Fact]
    public void RosterCopyWithDryRunReportsAndWritesNothing()
    {
        WriteRepoFile(TwoMembers);

        var (code, stdout, _) = Roster("roster", "copy", "default", "alt", "--level", "repo", "--dry-run", "--cwd", Project);

        Assert.Equal(0, code);
        Assert.False(Parse(stdout)["copied"].AsBool());
        Assert.Equal(TwoMembers, File.ReadAllText(RepoFile));
    }

    [Fact]
    public void RosterRefusesAFlagItsSubcommandDoesNotTake()
    {
        var (code, _, stderr) = Roster("roster", "list", "--dry-run", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Contains("roster list: unrecognized flag --dry-run (use --json, --cwd)", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RosterListPlainNamesTheSelectionAndTheDefaultBlock()
    {
        var (code, stdout, _) = Roster("roster", "list", "--cwd", Project);

        Assert.Equal(0, code);
        Assert.StartsWith("selection: default (nothing selected)\n* default (roster)", stdout, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- show

    [Fact]
    public void ShowAtALevelNamesTheMembersAndSaysWhatShadowsIt()
    {
        File.WriteAllText(RepoFile, "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[{\"role\":\"implementor\"}]}}");
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalFile)!);
        File.WriteAllText(GlobalFile, "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[{\"role\":\"reviewer\"}]}}");

        var global = Parse(Roster("show", "global", "--cwd", Project).Stdout);
        var repo = Parse(Roster("show", "repo", "--cwd", Project).Stdout);

        Assert.Equal("shadowed by repo", global["shadowed"].AsString());
        Assert.Equal("reviewer", global["roster"].AsObject()["members"].AsArray()[0].Member("role")!.Value.AsString());
        Assert.True(repo["shadowed"].IsNull);
        Assert.Equal("implementor", repo["roster"].AsObject()["members"].AsArray()[0].Member("role")!.Value.AsString());
        Assert.False(string.IsNullOrEmpty(repo["roster"].AsObject()["members"].AsArray()[0].Member("name")!.Value.AsString()));
    }

    [Fact]
    public void ShowWithNoRosterGivesTheHintAndALevelWithNoBlockGivesNull()
    {
        var bare = Parse(Roster("show", "--cwd", Project).Stdout);
        Assert.True(bare["roster"].IsNull);
        Assert.Contains("spawn-ad-hoc", bare["hint"].AsString(), StringComparison.Ordinal);

        var level = Parse(Roster("show", "repo", "--cwd", Project).Stdout);
        Assert.True(level["roster"].IsNull);
        Assert.Equal("repo", level["level"].AsString());
        Assert.False(level.ContainsKey("hint"));
    }

    [Fact]
    public void ShowRefusesALevelThatIsNotOne()
    {
        var (code, stdout, stderr) = Roster("show", "--level", "universe", "--cwd", Project);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--level must be one of repo-user, repo, global, got \"universe\"", stderr, StringComparison.Ordinal);
        Assert.Equal(0, Roster("show", "--level", "repo", "--cwd", Project).Code);
    }

    // ---------------------------------------------------------------- add

    [Fact]
    public void AddAcceptsQaAsAPeerRoleLikeDocsWriter()
    {
        var qa = Roster("add", "--role", "qa", "--cwd", Project);
        Assert.Equal(0, qa.Code);
        Assert.DoesNotContain("subagent-only", qa.Stderr, StringComparison.Ordinal);

        Assert.Equal(0, Roster("add", "--role", "docs-writer", "--cwd", Project).Code);
    }

    [Fact]
    public void AddStillRefusesARoleThatIsNotInTheRegistry()
    {
        var missing = Roster("add", "--role", "no-such-role", "--cwd", Project);
        Assert.NotEqual(0, missing.Code);
    }

    [Fact]
    public void AddWithNoRosterCreatesAMinimalOneAndWritesOnlyTheTemplate()
    {
        var (code, stdout, stderr) = Roster("add", "--role", "implementor", "--cwd", Project);

        Assert.Equal(0, code);
        var result = Parse(stdout);
        Assert.Equal("repo", result["level"].AsString());
        Assert.True(result["wasDefaulted"].AsBool());
        Assert.False(result["spawned"].AsBool());
        Assert.Contains("spawn-one implementor --member", result["next_step"].AsString(), StringComparison.Ordinal);
        Assert.Contains("created a minimal one at level \"repo\"", stderr, StringComparison.Ordinal);
        var block = Parse(File.ReadAllText(RepoFile))["roster"].AsObject();
        Assert.Equal("peer", block["route"].AsString());
        Assert.Equal("implementor", block["members"].AsArray()[0].Member("role")!.Value.AsString());
        Assert.False(File.Exists(Path.Combine(Project, ".claude", "ah2", "team.json")));
    }

    [Fact]
    public void AddRefusesTheOrchestratorAndAnUnknownRoleAndLeavesTheFileAlone()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        var before = File.ReadAllText(RepoFile);

        var orchestrator = Roster("add", "--role", "orchestrator", "--cwd", Project);
        var unknown = Roster("add", "--role", "wizard", "--cwd", Project);
        var missing = Roster("add", "--cwd", Project);

        Assert.Equal(2, orchestrator.Code);
        Assert.Contains("role \"orchestrator\" is not a roster member", orchestrator.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, unknown.Code);
        Assert.Contains("--role must be one of", unknown.Stderr, StringComparison.Ordinal);
        Assert.Contains("got \"wizard\"", unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, missing.Code);
        Assert.Contains("got undefined", missing.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RepoFile));
        Assert.Equal(0, Roster("add", "--role", "reviewer", "--cwd", Project).Code);
    }

    [Fact]
    public void AddRefusesOnMissingOnASubagentMemberButTakesItOnAPeerOne()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");

        var refused = Roster("add", "--role", "task-runner", "--route", "subagent", "--on-missing", "auto", "--cwd", Project);
        Assert.Equal(2, refused.Code);
        Assert.Contains("on-missing applies only to peer-routed members", refused.Stderr, StringComparison.Ordinal);
        Assert.Empty(Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray());

        var accepted = Roster("add", "--role", "implementor", "--on-missing", "auto", "--cwd", Project);
        Assert.Equal(0, accepted.Code);
        Assert.Equal("auto", Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray()[0].Member("onMissing")!.Value.AsString());
    }

    private void InstallTaskGopher()
    {
        var plugin = Path.Combine(_sandbox.Root, "task-gopher-plugin");
        Directory.CreateDirectory(Path.Combine(plugin, "agents"));
        File.WriteAllText(Path.Combine(plugin, "agents", "task-gopher.md"), "---\nname: task-gopher\nmodel: haiku\n---\nbody\n");
        var plugins = Path.Combine(_sandbox.Home, ".claude", "plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(
            Path.Combine(plugins, "installed_plugins.json"),
            "{\"version\":2,\"plugins\":{\"task-gopher@claudetools\":[{\"installPath\":\"" + plugin + "\"}]}}");
    }

    [Fact]
    public void AddingAModelToALegworkMemberWhileTaskGopherIsInstalledWarns()
    {
        InstallTaskGopher();

        var (code, stdout, stderr) = Roster("add", "--role", "task-runner", "--model", "haiku", "--cwd", Project);

        Assert.Equal(0, code);
        var warnings = Parse(stdout).Get("warnings")!.Value.AsArray();
        Assert.Single(warnings);
        var text = warnings[0].AsString();
        Assert.Contains("task-gopher", text, StringComparison.Ordinal);
        Assert.StartsWith("task-runner has a model, so create will launch it as a dedicated peer session", text, StringComparison.Ordinal);
        Assert.Contains(text, stderr, StringComparison.Ordinal);
        Assert.Equal("haiku", Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray()[0].Member("model")!.Value.AsString());
    }

    [Fact]
    public void ARefusedAddOfAModelOnALegworkMemberDoesNotWarn()
    {
        InstallTaskGopher();
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[{\"role\":\"architect\",\"effort\":\"not-an-effort\"}]}}");
        var before = File.ReadAllText(RepoFile);

        var (code, stdout, stderr) = Roster("add", "--role", "task-runner", "--model", "haiku", "--cwd", Project);

        Assert.NotEqual(0, code);
        Assert.DoesNotContain("dedicated peer session", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("warnings", stdout, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RepoFile));
    }

    [Fact]
    public void AddingALegworkMemberWithNoModelDoesNotWarn()
    {
        InstallTaskGopher();

        var (code, stdout, _) = Roster("add", "--role", "task-runner", "--cwd", Project);

        Assert.Equal(0, code);
        Assert.Null(Parse(stdout).Get("warnings"));
    }

    [Fact]
    public void AddingAModelWhileTaskGopherIsNotInstalledDoesNotWarn()
    {
        var (code, stdout, _) = Roster("add", "--role", "task-runner", "--model", "haiku", "--cwd", Project);

        Assert.Equal(0, code);
        Assert.Null(Parse(stdout).Get("warnings"));
    }

    [Fact]
    public void AddingAModelToAChainRoleDoesNotWarn()
    {
        InstallTaskGopher();

        var (code, stdout, _) = Roster("add", "--role", "architect", "--model", "opus", "--cwd", Project);

        Assert.Equal(0, code);
        Assert.Null(Parse(stdout).Get("warnings"));
    }

    [Fact]
    public void ANonClaudeLegworkMemberDoesNotWarn()
    {
        InstallTaskGopher();

        var (code, stdout, stderr) = Roster("add", "--role", "task-runner", "--kind", "codex", "--route", "pane", "--model", "gpt-5", "--cwd", Project);

        Assert.True(code == 0, stderr);
        Assert.Null(Parse(stdout).Get("warnings"));
    }

    [Fact]
    public void AddingACodexMemberCarriesTheTrustNeedAndAClaudeMemberDoesNot()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        InstallTaskGopher();

        var codex = Roster("add", "--role", "task-runner", "--kind", "codex", "--route", "pane", "--model", "gpt-5", "--cwd", Project);

        Assert.True(codex.Code == 0, codex.Code + "|" + codex.Stderr);
        var needs = Parse(codex.Stdout)["needs"].AsArray();
        Assert.Equal("codex-trust", needs[0].AsObject()["kind"].AsString());

        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        var claude = Roster("add", "--role", "task-runner", "--cwd", Project);

        Assert.True(claude.Code == 0, claude.Code + "|" + claude.Stderr);
        Assert.Null(Parse(claude.Stdout).Get("needs"));
    }

    [Fact]
    public void AddToANamedRosterNeedsItsInitAtThatLevelFirst()
    {
        Assert.Equal(0, Roster("init", "global", "--route", "peer", "--roster", "crew", "--cwd", Project).Code);
        var refused = Roster("add", "--role", "implementor", "--roster", "crew", "--level", "repo", "--cwd", Project);
        Assert.Equal(2, refused.Code);
        Assert.Contains("no rosters.crew at level \"repo\"", refused.Stderr, StringComparison.Ordinal);
        Assert.Contains("run `ah roster init --roster crew` first", refused.Stderr, StringComparison.Ordinal);

        Assert.Equal(0, Roster("init", "repo", "--route", "peer", "--roster", "crew", "--cwd", Project).Code);
        var accepted = Roster("add", "--role", "implementor", "--roster", "crew", "--level", "repo", "--cwd", Project);
        Assert.True(accepted.Code == 0, accepted.Stderr);
        Assert.Equal("rosters.crew", Parse(accepted.Stdout)["container"].AsString());
    }

    [Fact]
    public void AddRefusesAnEffortOnANonClaudeKindAndTakesItOnClaude()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");

        var refused = Roster("add", "--role", "implementor", "--kind", "codex", "--effort", "high", "--cwd", Project);
        Assert.Equal(2, refused.Code);
        Assert.Contains("effort", refused.Stderr, StringComparison.Ordinal);
        Assert.Empty(Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray());

        Assert.Equal(0, Roster("add", "--role", "implementor", "--effort", "high", "--cwd", Project).Code);
    }

    // ---------------------------------------------------------------- edit

    private string AddMember(string role)
    {
        var (code, stdout, stderr) = Roster("add", "--role", role, "--level", "repo", "--cwd", Project);
        Assert.True(code == 0, stderr);
        return Parse(stdout)["member"].AsObject()["name"].AsString();
    }

    [Fact]
    public void EditSetsAFieldAndAnEmptyValueClearsIt()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        var name = AddMember("implementor");

        var set = Roster("edit", "--member", name, "--effort", "high", "--level", "repo", "--cwd", Project);
        Assert.Equal(0, set.Code);
        Assert.Equal("high", Parse(set.Stdout)["member"].AsObject()["effort"].AsString());

        var cleared = Roster("edit", "--member", name, "--effort", "", "--level", "repo", "--cwd", Project);
        Assert.Equal(0, cleared.Code);
        Assert.False(Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray()[0].AsObject().ContainsKey("effort"));
    }

    // ---------------------------------------------------------------- edit --orchestrator-model

    [Fact]
    public void EditOrchestratorModelWritesTheBlockKeyAndLeavesMembersAlone()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        AddMember("implementor");
        var membersBefore = Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray().ToString();

        var set = Roster("edit", "--orchestrator-model", "opus", "--level", "repo", "--cwd", Project);

        Assert.True(set.Code == 0, set.Stderr);
        var block = Parse(File.ReadAllText(RepoFile))["roster"].AsObject();
        Assert.Equal("opus", block["orchestrator_model"].AsString());
        Assert.Equal(membersBefore, block["members"].AsArray().ToString());
        Assert.Equal("opus", Parse(set.Stdout)["orchestrator_model"].AsString());
    }

    [Fact]
    public void EditOrchestratorModelEmptyRemovesTheKey()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"orchestrator_model\":\"opus\",\"members\":[]}}");
        AddMember("implementor");

        var cleared = Roster("edit", "--orchestrator-model", "", "--level", "repo", "--cwd", Project);

        Assert.True(cleared.Code == 0, cleared.Stderr);
        Assert.False(Parse(File.ReadAllText(RepoFile))["roster"].AsObject().ContainsKey("orchestrator_model"));
    }

    [Fact]
    public void EditOrchestratorModelRefusesALegworkModelAndWritesNothing()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        AddMember("implementor");
        var before = File.ReadAllText(RepoFile);

        var refused = Roster("edit", "--orchestrator-model", "haiku", "--level", "repo", "--cwd", Project);

        Assert.Equal(2, refused.Code);
        Assert.Contains("allowed: opus, sonnet, fable, inherit", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RepoFile));
    }

    [Fact]
    public void EditOrchestratorModelAcceptsTheNearestAllowedModel()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        AddMember("implementor");

        Assert.Equal(0, Roster("edit", "--orchestrator-model", "inherit", "--level", "repo", "--cwd", Project).Code);
        Assert.Equal("inherit", Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["orchestrator_model"].AsString());
    }

    [Fact]
    public void EditOrchestratorModelRefusesMixingWithAMemberEdit()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        var name = AddMember("implementor");
        var before = File.ReadAllText(RepoFile);

        var mixed = Roster("edit", "--orchestrator-model", "opus", "--member", name, "--level", "repo", "--cwd", Project);

        Assert.Equal(2, mixed.Code);
        Assert.Contains("roster-block edit", mixed.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RepoFile));
    }

    [Fact]
    public void EditWithNeitherMemberNorOrchestratorModelStillNeedsMember()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        AddMember("implementor");

        var bare = Roster("edit", "--level", "repo", "--cwd", Project);

        Assert.Equal(2, bare.Code);
        Assert.Contains("edit needs --member", bare.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowPrintsTheResolvedAndTheLevelOrchestratorModel()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"orchestrator_model\":\"opus\",\"members\":[]}}");
        AddMember("implementor");

        Assert.Equal("opus", Parse(Roster("show", "--cwd", Project).Stdout)["orchestrator_model"].AsString());
        Assert.Equal("opus", Parse(Roster("show", "--level", "repo", "--cwd", Project).Stdout)["roster"].AsObject()["orchestrator_model"].AsString());
    }

    [Fact]
    public void ShowWithoutAnOrchestratorModelHasNoSuchField()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        AddMember("implementor");

        Assert.False(Parse(Roster("show", "--cwd", Project).Stdout).ContainsKey("orchestrator_model"));
        Assert.False(Parse(Roster("show", "--level", "repo", "--cwd", Project).Stdout)["roster"].AsObject().ContainsKey("orchestrator_model"));
    }

    [Fact]
    public void ResolutionTreatsAnEmptyOrchestratorModelAsUnset()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"orchestrator_model\":\"\",\"members\":[]}}");
        AddMember("implementor");

        Assert.False(Parse(Roster("show", "--cwd", Project).Stdout).ContainsKey("orchestrator_model"));
    }

    [Fact]
    public void ResolutionIgnoresAnOrchestratorModelOnALevelWithNoMembers()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"orchestrator_model\":\"opus\",\"members\":[]}}");
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalFile)!);
        File.WriteAllText(GlobalFile, "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[{\"role\":\"implementor\"}]}}");

        var shown = Parse(Roster("show", "--cwd", Project).Stdout);

        Assert.Equal("global", shown["level"].AsString());
        Assert.False(shown.ContainsKey("orchestrator_model"));
    }

    [Fact]
    public void EditRefusesAMissingOrUnknownMemberAndAValuelessAdapterOrKind()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        var name = AddMember("implementor");
        var before = File.ReadAllText(RepoFile);

        var none = Roster("edit", "--level", "repo", "--cwd", Project);
        var unknown = Roster("edit", "--member", "nobody", "--level", "repo", "--cwd", Project);
        var adapter = Roster("edit", "--member", name, "--adapter", "--level", "repo", "--cwd", Project);
        var kind = Roster("edit", "--member", name, "--kind", "--level", "repo", "--cwd", Project);

        Assert.Equal(2, none.Code);
        Assert.Contains("edit needs --member <derived-name>", none.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, unknown.Code);
        Assert.Contains("no member named \"nobody\" at level \"repo\"", unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, adapter.Code);
        Assert.Contains("--adapter requires a value", adapter.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, kind.Code);
        Assert.Contains("--kind requires a value", kind.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RepoFile));
        Assert.Equal(0, Roster("edit", "--member", name, "--effort", "low", "--level", "repo", "--cwd", Project).Code);
    }

    [Fact]
    public void EditToANonClaudeKindClearsTheClaudeSettingsItStrandsAndRefusesAnEffortGivenWithIt()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        var name = AddMember("implementor");
        Assert.Equal(0, Roster("edit", "--member", name, "--effort", "high", "--level", "repo", "--cwd", Project).Code);

        var together = Roster("edit", "--member", name, "--kind", "codex", "--effort", "low", "--level", "repo", "--cwd", Project);
        Assert.Equal(2, together.Code);
        Assert.Contains("--effort cannot be combined with --kind codex", together.Stderr, StringComparison.Ordinal);
        Assert.Equal("high", Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray()[0].Member("effort")!.Value.AsString());

        var switched = Roster("edit", "--member", name, "--kind", "codex", "--route", "pane", "--level", "repo", "--cwd", Project);
        Assert.True(switched.Code == 0, switched.Stderr);
        Assert.Contains("cleared effort — it is a Claude CLI setting", switched.Stderr, StringComparison.Ordinal);
        var written = Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray()[0].AsObject();
        Assert.Equal("codex", written["kind"].AsString());
        Assert.False(written.ContainsKey("effort"));

        var back = Roster("edit", "--member", name, "--kind", "claude", "--level", "repo", "--cwd", Project);
        Assert.True(back.Code == 0, back.Stderr);
        Assert.False(Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray()[0].AsObject().ContainsKey("kind"));
    }

    [Fact]
    public void EditToASubagentRouteDropsAnInheritedOnMissingWithANoticeButRefusesItGivenNow()
    {
        WriteRepoFile("{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        var name = AddMember("task-runner");
        Assert.Equal(0, Roster("edit", "--member", name, "--on-missing", "auto", "--level", "repo", "--cwd", Project).Code);

        var both = Roster("edit", "--member", name, "--route", "subagent", "--on-missing", "auto", "--level", "repo", "--cwd", Project);
        Assert.Equal(2, both.Code);
        Assert.Contains("on-missing applies only to peer-routed members", both.Stderr, StringComparison.Ordinal);

        var dropped = Roster("edit", "--member", name, "--route", "subagent", "--level", "repo", "--cwd", Project);
        Assert.Equal(0, dropped.Code);
        Assert.Contains("dropped on-missing \"auto\"", dropped.Stderr, StringComparison.Ordinal);
        Assert.False(Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray()[0].AsObject().ContainsKey("onMissing"));
    }

    // ---------------------------------------------------------------- the live-team gate

    private Dictionary<string, string?> AsOwnerOfALiveTeam()
    {
        _sandbox.PlantTeam(
            Path.Combine(Project, ".claude", "ah2", "teams", "proj.json"),
            "{\"version\":1,\"team_id\":\"t-live\",\"transport\":\"tmux\",\"orchestrator\":{\"session_id\":null,\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\"},\"members\":[]}");
        return new Dictionary<string, string?> { ["CLAUDE_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };
    }

    [Theory]
    [InlineData("layout", "--mode", "grid")]
    [InlineData("alias", "--set", "x")]
    [InlineData("roster", "use", "crew")]
    [InlineData("roster", "copy", "default")]
    [InlineData("roster", "delete", "crew")]
    public void EveryVerbThatWritesTheTemplateIsRefusedWhileTheSessionOwnsALiveTeam(string verb, string a, string b)
    {
        var owner = AsOwnerOfALiveTeam();
        var args = verb == "roster" ? new[] { verb, a, b, b == "default" ? "copy-of-default" : string.Empty }.Where(x => x.Length > 0).ToArray() : [verb, a, b];

        var refused = RosterAs(owner, [.. args, "--level", "repo", "--cwd", Project]);

        Assert.Equal(2, refused.Code);
        Assert.Contains("edits the roster TEMPLATE, and this session owns live team t-live", refused.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));
    }

    /// <summary>ah2-65: the empty implicit team a session start gives the Orchestrator does not block the first-run roster init; one with a member still does.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheOwnerOfAnEmptyImplicitTeamMayInitAndAddToTheRoster(bool withMember)
    {
        var teamFile = Path.Combine(Project, ".claude", "ah2", "teams", "proj.json");
        var team = "{\"version\":1,\"team_id\":\"t-implicit\",\"implicit\":true,\"orchestrator\":{\"session_id\":null,\"pid\":" + Environment.ProcessId
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\"},\"members\":[" + (withMember ? "{\"name\":\"ah2-reviewer\",\"role\":\"reviewer\"}" : string.Empty) + "]}";
        _sandbox.PlantTeam(teamFile, team);
        var teamBefore = _sandbox.TeamText(teamFile);
        var owner = new Dictionary<string, string?> { ["CLAUDE_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };

        var init = RosterAs(owner, "init", "--level", "global", "--route", "peer", "--cwd", Project);

        if (withMember)
        {
            Assert.Equal(2, init.Code);
            Assert.Contains("edits the roster TEMPLATE, and this session owns live team t-implicit", init.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(GlobalFile));
            return;
        }

        Assert.True(init.Code == 0, init.Stderr);
        var add = RosterAs(owner, "add", "--level", "global", "--role", "architect", "--cwd", Project);
        Assert.True(add.Code == 0, add.Stderr);
        var members = Parse(File.ReadAllText(GlobalFile))["roster"].AsObject()["members"].AsArray();
        Assert.Contains(members, m => m.AsObject()["role"].AsString() == "architect");
        Assert.Equal(teamBefore, _sandbox.TeamText(teamFile));
    }

    [Fact]
    public void TheLiveTeamGateLetsAReadAndADryRunAndTheUsersOverrideThrough()
    {
        var owner = AsOwnerOfALiveTeam();
        File.WriteAllText(RepoFile, "{\"version\":1,\"rosters\":{\"crew\":{\"route\":\"peer\",\"members\":[]}}}");
        var before = File.ReadAllText(RepoFile);

        var list = RosterAs(owner, "roster", "list", "--cwd", Project);
        var show = RosterAs(owner, "show", "--cwd", Project);
        var dry = RosterAs(owner, "roster", "delete", "crew", "--level", "repo", "--dry-run", "--cwd", Project);
        var layout = RosterAs(owner, "layout", "--allow-roster-edit", "--cwd", Project);

        Assert.Equal(0, list.Code);
        Assert.Equal(0, show.Code);
        Assert.True(dry.Code == 0, dry.Stderr);
        Assert.Equal(before, File.ReadAllText(RepoFile));
        Assert.Equal(2, layout.Code);
        Assert.Contains("layout was removed", layout.Stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the level file is replaced whole

    [Fact]
    public void ALevelFileIsReplacedByRenameKeepsItsModeAndLeavesNoTemporaryFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(RepoFile, "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}");
        File.SetUnixFileMode(RepoFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var (code, _, stderr) = Roster("add", "--role", "implementor", "--level", "repo", "--cwd", Project);

        Assert.True(code == 0, stderr);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(RepoFile));
        Assert.Single(Parse(File.ReadAllText(RepoFile))["roster"].AsObject()["members"].AsArray());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(RepoFile)!, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(RepoFile)!, ".*.tmp"));
    }

    [Fact]
    public void AWriteThatCannotStartLeavesTheLevelFileAsItWasAndNoTemporaryFileBehind()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var original = "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[]}}";
        File.WriteAllText(RepoFile, original);
        var dir = Path.GetDirectoryName(RepoFile)!;
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var refused = Roster("add", "--role", "implementor", "--level", "repo", "--cwd", Project);

            Assert.NotEqual(0, refused.Code);
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(original, File.ReadAllText(RepoFile));
        Assert.Empty(Directory.GetFiles(dir, ".*.tmp"));
    }
}
