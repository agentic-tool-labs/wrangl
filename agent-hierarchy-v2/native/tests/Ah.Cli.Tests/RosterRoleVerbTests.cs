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

        public IRosterEventMail? EventMail { get; init; }
    }

    private sealed class RecordingMail : IRosterEventMail
    {
        public List<(string Id, string Event, string Text, JsonObject Data, IReadOnlyList<string> Recipients)> Sent { get; } = [];

        public IReadOnlyList<string> SendEvent(string cwd, string hierarchyDir, string id, string eventName, string text, JsonObject data, IReadOnlyList<string> recipients)
        {
            Sent.Add((id, eventName, text, data, recipients));
            return recipients;
        }
    }

    private readonly RecordingMail _mail = new();

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
        var services = new Services(graph, io.Files, io.Clock, env, Project, false) { EventMail = _mail };
        CommandsModule.ComposeRoster(() => services);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run([.. args, "--cwd", Project], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Parse(string text) => JsonText.Parse(Encoding.UTF8.GetBytes(text)).AsObject();

    private void InstallPack(string toolsLine = "tools: Read, Grep, Glob, Bash, SendMessage\n", string role = "qc")
    {
        Directory.CreateDirectory(Path.Combine(PackDir, "agents"));
        Directory.CreateDirectory(Path.Combine(PackDir, ".claude-plugin"));
        File.WriteAllText(Path.Combine(PackDir, ".claude-plugin", "plugin.json"), "{\"name\":\"demo\",\"version\":\"2.0.0\"}");
        File.WriteAllText(
            Path.Combine(PackDir, "ah-roles.json"),
            "{\"version\":1,\"roles\":{\"" + role + "\":{\"class\":\"review\",\"label\":\"QC\",\"description\":\"Checks things\",\"routes\":\"qc work\",\"model\":\"sonnet\"}}}");
        File.WriteAllText(
            Path.Combine(PackDir, "agents", role + ".md"),
            "---\nname: " + role + "\ndescription: Checks things\n" + toolsLine + "---\n\nYou check things.\n");
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

    /// <summary>The qc row of <c>role list --json --roster r</c>, with roster r naming the demo pack.</summary>
    private JsonObject QcUnderRosterR()
    {
        var init = Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market");
        Assert.True(init.Code == 0, init.Stderr);
        var list = Roster("role", "list", "--json", "--roster", "r");
        Assert.True(list.Code == 0, list.Stderr);
        return Parse(list.Stdout)["roles"].AsArray().Single(r => r.Member("name")!.Value.AsString() == "qc").AsObject();
    }

    private static List<string> CodesAt(JsonObject row, string level) =>
        row["findings"].AsArray().Where(f => f.Member("level")!.Value.AsString() == level).Select(f => f.Member("code")!.Value.AsString()).ToList();

    /// <summary>T7 (D9, adr-47): a review-class pack agent with a server grant is available, and pack show prints the ⚠ grant line as a warning.</summary>
    [Fact]
    public void T7AReviewPackAgentWithAServerGrantIsAvailableAndPackShowPrintsTheGrantLine()
    {
        InstallPack("tools: Read, Grep, SendMessage, mcp__x__*\n");

        var qc = QcUnderRosterR();
        var shown = Roster("pack", "show", "demo");

        Assert.DoesNotContain("UNAVAILABLE", qc["status"].AsString(), StringComparison.Ordinal);
        Assert.Empty(CodesAt(qc, "error"));
        Assert.Contains("pack-agent-tools", CodesAt(qc, "warn"));
        Assert.Equal(0, shown.Code);
        Assert.Contains("  ⚠ grants EVERY tool of MCP server mcp__x,", shown.Stdout, StringComparison.Ordinal);
        Assert.Contains("  WARN pack-agent-tools: tools entry \"mcp__x__*\": a server-wide grant", shown.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("  ERROR ", shown.Stdout, StringComparison.Ordinal);
    }

    /// <summary>W1: a pack agent with no tools list is available with a warning; W2: pack show shows it as a warning.</summary>
    [Fact]
    public void W1APackAgentWithNoToolsListIsAvailableWithAWarning()
    {
        InstallPack(string.Empty);

        var qc = QcUnderRosterR();
        var shown = Roster("pack", "show", "demo");

        Assert.DoesNotContain("UNAVAILABLE", qc["status"].AsString(), StringComparison.Ordinal);
        Assert.Empty(CodesAt(qc, "error"));
        Assert.Contains(qc["findings"].AsArray(), f => f.Member("code")!.Value.AsString() == "pack-agent-tools"
            && f.Member("message")!.Value.AsString().StartsWith("tools is required", StringComparison.Ordinal));
        Assert.Contains("  WARN pack-agent-tools: tools is required", shown.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Negative for W1/W2: a missing agent file is still pack-invalid, an error in role list and in pack show.</summary>
    [Fact]
    public void APackAgentWithNoAgentFileStaysUnavailableAndAnErrorInPackShow()
    {
        InstallPack();
        File.Delete(Path.Combine(PackDir, "agents", "qc.md"));

        var qc = QcUnderRosterR();
        var shown = Roster("pack", "show", "demo");

        Assert.StartsWith("UNAVAILABLE", qc["status"].AsString(), StringComparison.Ordinal);
        Assert.Contains("pack-invalid", CodesAt(qc, "error"));
        Assert.Contains("  ERROR pack-invalid: no agents/qc.md", shown.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Negative: a first-party custom role keeps its contract errors (a review class holding Edit is still refused).</summary>
    [Fact]
    public void ACustomReviewRoleHoldingEditKeepsItsError()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AgentFile("own"))!);
        File.WriteAllText(AgentFile("own"), "---\nname: own\ndescription: Own role\ntools: Read, Grep, Edit, SendMessage\n---\n\nbody\n");
        var set = Roster("role", "set", "own", "--class", "review", "--description", "Own role", "--level", "repo");

        Assert.Equal(2, set.Code);
        Assert.Contains("ERROR forbidden-tool:Edit", set.Stdout + set.Stderr, StringComparison.Ordinal);
    }

    private string PacksSeen => Path.Combine(_sandbox.Home, ".claude", "ah2", "packs-seen.json");

    private JsonValue QcUpdated() =>
        Parse(Roster("role", "list", "--json", "--roster", "r").Stdout)["roles"].AsArray().Single(r => r.Member("name")!.Value.AsString() == "qc").Member("pack")!.Value.Member("updated")!.Value;

    /// <summary>T10 (D6): first sight writes the record and mails nothing; one changed file sends one pack-updated mail; the next run sends nothing.</summary>
    [Fact]
    public void T10AChangedPackFileIsReportedOnceToTheTeamsOrchestrator()
    {
        InstallPack();
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "ah2", "teams"));
        File.WriteAllText(Path.Combine(Project, ".claude", "ah2", "teams", "proj.json"), "{\"version\":1,\"members\":[]}");

        var first = QcUpdated();
        Assert.True(first.IsNull);
        Assert.True(File.Exists(PacksSeen));
        Assert.Empty(_mail.Sent); // negative: first sight sends nothing

        var unchanged = File.ReadAllText(PacksSeen);
        Assert.True(QcUpdated().IsNull);
        Assert.Equal(unchanged, File.ReadAllText(PacksSeen)); // negative: an unchanged digest writes nothing
        Assert.Empty(_mail.Sent);

        File.AppendAllText(Path.Combine(PackDir, "agents", "qc.md"), "More.\n");
        var updated = QcUpdated();
        var again = QcUpdated();
        var text = Roster("role", "list", "--roster", "r").Stdout;

        Assert.Equal(1, (int)updated.Member("files")!.Value.Member("changed")!.Value.AsNumber());
        Assert.Equal(0, (int)updated.Member("files")!.Value.Member("added")!.Value.AsNumber());
        Assert.Equal("2.0.0", updated.Member("to_version")!.Value.AsString());
        Assert.Equal(JsonText.Stringify(updated, 0), JsonText.Stringify(again, 0));
        var sent = Assert.Single(_mail.Sent); // the second and third runs send nothing
        Assert.Equal("pack-updated", sent.Event);
        Assert.Matches("^pack-updated:demo@market:[0-9a-f]{12}$", sent.Id);
        Assert.Equal(["orchestrator@proj"], sent.Recipients);
        Assert.Equal(
            "role pack demo@market updated 2.0.0→2.0.0: files +0 −0 ~1; roles +0 −0 ~0. It takes effect at each member's next start; nothing was paused.",
            sent.Text);
        Assert.Equal("[\"agents/qc.md\"]", JsonText.Stringify(sent.Data["files"].Member("changed")!.Value, 0));
        Assert.Contains("— updated ", text, StringComparison.Ordinal);
        Assert.Contains(": files +0 −0 ~1, roles ~0", text, StringComparison.Ordinal);
        Assert.Contains("updated ", Roster("pack", "show", "demo").Stdout, StringComparison.Ordinal);
    }

    /// <summary>D6 "blocks nothing": a packs-seen record that parses but has the wrong shape leaves role list working, the pack unreported.</summary>
    [Fact]
    public void AMalformedPacksSeenRecordBlocksNothing()
    {
        InstallPack();
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        Directory.CreateDirectory(Path.GetDirectoryName(PacksSeen)!);
        File.WriteAllText(PacksSeen, "{\"demo@market\":{\"digest\":\"x\",\"files\":[[1,\"x\"]],\"previous\":{\"files\":[[1,\"y\"]]}}}");

        var list = Roster("role", "list", "--json", "--roster", "r");

        Assert.True(list.Code == 0, list.Stderr);
        Assert.True(QcUpdated().IsNull);
        Assert.Empty(_mail.Sent);
    }

    /// <summary>Negative for T10: with no team record the change is recorded and shown, and no mail is sent.</summary>
    [Fact]
    public void APackChangeOutsideATeamIsShownAndNotMailed()
    {
        InstallPack();
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        Assert.True(QcUpdated().IsNull);

        File.AppendAllText(Path.Combine(PackDir, "agents", "qc.md"), "More.\n");

        Assert.Equal(1, (int)QcUpdated().Member("files")!.Value.Member("changed")!.Value.AsNumber());
        Assert.Empty(_mail.Sent);
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

    /// <summary>ah2-562: --writes stores a review role's write folders and "" clears them; a .. glob and the built-in reviewer are refused, nothing written.</summary>
    [Fact]
    public void RoleSetWritesIsStoredClearedAndRefusedWhereItDoesNotFit()
    {
        JsonObject Qc() => Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["qc"].AsObject();
        var set = Roster("role", "set", "qc", "--class", "review", "--description", "Checks things", "--level", "repo", "--scaffold", "repo", "--writes", "tests/automation/**, docs/*.md");
        Assert.True(set.Code == 0, set.Stderr);
        Assert.Equal(["tests/automation/**", "docs/*.md"], Qc()["writes"].AsArray().Select(x => x.AsString()).ToArray());

        var before = File.ReadAllText(RepoFile);
        var dotdot = Roster("role", "set", "qc", "--writes", "tests/../src/**", "--level", "repo");
        Assert.Equal(2, dotdot.Code);
        Assert.Contains("..", dotdot.Stderr, StringComparison.Ordinal);
        var builtin = Roster("role", "set", "reviewer", "--writes", "tests/**", "--level", "repo");
        Assert.Equal(2, builtin.Code);
        Assert.Contains("--writes does not apply to a built-in role", builtin.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, Roster("role", "set", "builder", "--class", "implement", "--writes", "tests/**", "--level", "repo").Code);
        Assert.Equal(before, File.ReadAllText(RepoFile));

        Assert.Equal(0, Roster("role", "set", "qc", "--writes", "", "--level", "repo").Code);
        Assert.False(Qc().ContainsKey("writes"));
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

    // ---------------------------------------------------------------- a roster's packs (ah2-698)

    /// <summary>T2 (D1/D2): init writes a roster's packs, a member names a pack role by its own name, and roster pack changes only packs.</summary>
    [Fact]
    public void T2InitPackWritesPacksAndRosterPackAddThenRemoveLeavesMembersByteIdentical()
    {
        InstallPack();
        JsonObject Block() => Parse(File.ReadAllText(RepoFile))["rosters"].AsObject()["r"].AsObject();
        string Packs() => JsonText.Stringify(Block()["packs"], 0);
        string Members() => JsonText.Stringify(Block()["members"], 0);

        var init = Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market,other@market");
        Assert.True(init.Code == 0, init.Stderr);
        Assert.Equal("[\"demo@market\",\"other@market\"]", Packs());
        Assert.Contains("ah roster: pack other@market is not installed on this machine; its roles resolve once it is (/wrangl:agent-role install <source>)", init.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("pack demo@market is not installed", init.Stderr, StringComparison.Ordinal);
        var add = Roster("add", "--level", "repo", "--roster", "r", "--role", "qc");
        Assert.True(add.Code == 0, add.Stdout + add.Stderr);
        var members = Members();

        var added = Roster("roster", "pack", "r", "--add", "q@m");
        var removed = Roster("roster", "pack", "r", "--remove", "demo@market");

        Assert.True(added.Code == 0, added.Stderr);
        Assert.Contains("ah roster: pack q@m is not installed on this machine", added.Stderr, StringComparison.Ordinal);
        Assert.True(removed.Code == 0, removed.Stderr);
        Assert.Equal(members, Members());
        Assert.Equal("[\"other@market\",\"q@m\"]", Packs());
        var shown = Parse(removed.Stdout);
        Assert.Equal("repo", shown["level"].AsString());
        Assert.Equal("r", shown["roster"].AsString());
        Assert.Equal("[\"other@market\",\"q@m\"]", JsonText.Stringify(shown["packs"], 0));

        // Amendment 1 F7: a copied roster keeps its packs.
        Assert.Equal(0, Roster("roster", "copy", "r", "r2").Code);
        Assert.Equal("[\"other@market\",\"q@m\"]", JsonText.Stringify(Parse(File.ReadAllText(RepoFile))["rosters"].AsObject()["r2"].AsObject()["packs"], 0));
    }

    /// <summary>T2's negatives: an entry that is not plugin@marketplace is exit 2 with nothing written; a repeat add and an absent remove are no-ops.</summary>
    [Fact]
    public void T2AMalformedPackIsExitTwoAndARepeatAddOrAbsentRemoveIsANoOp()
    {
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        var before = File.ReadAllText(RepoFile);

        var badInit = Roster("init", "--level", "repo", "--route", "peer", "--roster", "s", "--pack", "game-factory");
        var badAdd = Roster("roster", "pack", "r", "--add", "game-factory");
        var badRole = Roster("roster", "pack", "r", "--add", "demo@market:qc");

        Assert.Equal(2, badInit.Code);
        Assert.Contains("init --pack: \"game-factory\" is not <plugin>@<marketplace>", badInit.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, badAdd.Code);
        Assert.Equal(2, badRole.Code);
        Assert.Equal(before, File.ReadAllText(RepoFile));

        var again = Roster("roster", "pack", "r", "--add", "demo@market");
        var absent = Roster("roster", "pack", "r", "--remove", "q@m");

        Assert.True(again.Code == 0, again.Stderr);
        Assert.True(absent.Code == 0, absent.Stderr);
        Assert.Contains("roster r names no pack q@m — nothing to remove", absent.Stderr, StringComparison.Ordinal);
        Assert.Equal("[\"demo@market\"]", JsonText.Stringify(Parse(File.ReadAllText(RepoFile))["rosters"].AsObject()["r"].AsObject()["packs"], 0));
    }

    private static void WriteRoles(string file, string roles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"version\":1,\"enabled\":true,\"roles\":" + roles + "}\n");
    }

    private JsonObject RoleRow(string name, params string[] roster)
    {
        var list = Roster(["role", "list", "--json", .. roster]);
        Assert.True(list.Code == 0, list.Stderr);
        return Parse(list.Stdout)["roles"].AsArray().Single(r => r.Member("name")!.Value.AsString() == name).AsObject();
    }

    /// <summary>Y1 (ah2-698): role set on a pack-supplied reviewer writes only the user's model; the pack agent runs with it.</summary>
    [Fact]
    public void Y1RoleSetOnAPackSuppliedReviewerWritesAPlainRowAndTheListShowsThePackAgentWithThatModel()
    {
        InstallPack(role: "reviewer");
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);

        var set = Roster("role", "set", "reviewer", "--model", "opus", "--roster", "r");

        Assert.True(set.Code == 0, set.Stderr);
        Assert.Equal("{\"model\":\"opus\"}", JsonText.Stringify(Parse(File.ReadAllText(GlobalFile))["roles"].AsObject()["reviewer"], 0));
        var row = RoleRow("reviewer", "--roster", "r");
        Assert.Equal("opus", row["model"].AsString());
        Assert.Equal("demo@market:reviewer", row["from"].AsString());
        Assert.Equal("roster:r", row["pack"].AsObject()["via"].AsString());
    }

    /// <summary>Y8/Y9 (Amendment 1): a pack custom role keeps the user's model and the pack's class, and --agent says "supplied by a pack".</summary>
    [Fact]
    public void Y8APackCustomRoleKeepsTheUsersModelAndThePacksClass()
    {
        InstallPack();
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        var data = Parse(File.ReadAllText(RepoFile));
        data["roles"] = Parse("{\"qc\":{\"model\":\"opus\",\"class\":\"implement\"}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));

        var list = Roster("role", "list", "--json", "--roster", "r");
        var agent = Roster("role", "set", "qc", "--agent", "mine", "--roster", "r");

        var qc = Parse(list.Stdout)["roles"].AsArray().Single(r => r.Member("name")!.Value.AsString() == "qc").AsObject();
        Assert.Equal("opus", qc["model"].AsString());
        Assert.Equal("review", qc["class"].AsString());
        Assert.Equal(2, agent.Code);
        Assert.Contains("role set qc: --agent can't change a role supplied by a pack (demo@market:qc) — to drop the pack: ah roster roster pack r --remove demo@market", agent.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("define your own role", agent.Stderr, StringComparison.Ordinal);
    }

    /// <summary>Y11 (Amendment 2): role set without --level on a pack-supplied role edits the level that already holds the user's row.</summary>
    [Fact]
    public void Y11RoleSetOnAPackSuppliedRoleEditsTheLevelHoldingTheUsersRow()
    {
        InstallPack(role: "reviewer");
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        var data = Parse(File.ReadAllText(RepoFile));
        data["roles"] = Parse("{\"reviewer\":{\"model\":\"sonnet\"}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));

        var set = Roster("role", "set", "reviewer", "--model", "opus", "--roster", "r");

        Assert.True(set.Code == 0, set.Stderr);
        Assert.Equal("{\"model\":\"opus\"}", JsonText.Stringify(Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["reviewer"], 0));
        Assert.False(File.Exists(GlobalFile) && Parse(File.ReadAllText(GlobalFile)).Get("roles") is { IsMap: true } g && g.AsObject().ContainsKey("reviewer"));
        Assert.Equal("opus", RoleRow("reviewer", "--roster", "r")["model"].AsString());
    }

    /// <summary>Negative for Y1: under a roster that names no pack, reviewer is the shipped agent and role set keeps today's path.</summary>
    [Fact]
    public void Y1RoleSetOnReviewerUnderARosterWithoutThePackKeepsTheShippedAgent()
    {
        InstallPack(role: "reviewer");
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);

        var set = Roster("role", "set", "reviewer", "--model", "opus");

        Assert.True(set.Code == 0, set.Stderr);
        var row = RoleRow("reviewer");
        Assert.Equal("opus", row["model"].AsString());
        Assert.False(row.ContainsKey("from"));
    }

    /// <summary>Y3/Y2 (ah2-698): a legacy row the selected roster's pack supplies is removed though members use it; a second remove names roster pack --remove.</summary>
    [Fact]
    public void Y3RoleRemoveDropsALegacyRowThatTheRostersPackSuppliesThoughMembersUseIt()
    {
        InstallPack();
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        Assert.Equal(0, Roster("add", "--level", "repo", "--roster", "r", "--role", "qc").Code);
        var data = Parse(File.ReadAllText(RepoFile));
        data["roles"] = Parse("{\"qc\":{\"from\":\"demo@market:qc\"}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));

        var removed = Roster("role", "remove", "qc", "--roster", "r");
        var again = Roster("role", "remove", "qc", "--roster", "r");

        Assert.True(removed.Code == 0, removed.Stderr);
        Assert.False(Parse(File.ReadAllText(RepoFile))["roles"].AsObject().ContainsKey("qc"));
        Assert.Equal(2, again.Code);
        Assert.Contains("no config row for it to remove — roster r's pack supplies it; to drop the pack: ah roster roster pack r --remove demo@market", again.Stderr, StringComparison.Ordinal);
    }

    /// <summary>Y6 (ah2-698): doctor names a pack role's failing pack status; its negative, a custom role's failing contract, keeps the contract text.</summary>
    [Fact]
    public void Y6DoctorNamesThePackStatusForAnUnavailablePackRole()
    {
        InstallPack();
        File.Delete(Path.Combine(PackDir, "agents", "qc.md"));
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--pack", "demo@market").Code);
        Directory.CreateDirectory(Path.GetDirectoryName(AgentFile("own"))!);
        File.WriteAllText(AgentFile("own"), "---\nname: own\ndescription: mine\ntools: Read, Edit, SendMessage\n---\nMine.\n");
        var data = Parse(File.ReadAllText(RepoFile));
        data["roles"] = Parse("{\"own\":{\"class\":\"review\",\"agent\":\"own\"}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));

        var doctor = Roster("doctor");
        var text = doctor.Stdout + doctor.Stderr;

        Assert.Contains("role qc is unavailable: its pack status is pack-invalid", text, StringComparison.Ordinal);
        Assert.Contains("role own is unavailable: its agent fails the review contract", text, StringComparison.Ordinal);
    }

    /// <summary>Review finding 2: a legacy built-in row the roster's pack supplies loses only its pack keys; the plain row left is then removed whole.</summary>
    [Fact]
    public void RoleRemoveOnALegacyBuiltinRowUnderASupplyingRosterKeepsTheUsersModel()
    {
        InstallPack(role: "reviewer");
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r", "--pack", "demo@market").Code);
        var data = Parse(File.ReadAllText(RepoFile));
        data["roles"] = Parse("{\"reviewer\":{\"from\":\"demo@market:reviewer\",\"pin\":\"sha256:x\",\"model\":\"opus\"}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));

        var first = Roster("role", "remove", "reviewer", "--roster", "r");
        var row = JsonText.Stringify(Parse(File.ReadAllText(RepoFile))["roles"].AsObject()["reviewer"], 0);
        var second = Roster("role", "remove", "reviewer", "--roster", "r");

        Assert.True(first.Code == 0, first.Stderr);
        Assert.Equal("{\"model\":\"opus\"}", row);
        Assert.True(second.Code == 0, second.Stderr);
        Assert.False(Parse(File.ReadAllText(RepoFile))["roles"].AsObject().ContainsKey("reviewer"));
    }

    /// <summary>Negative for Y3: when the roster names no pack, the legacy row is still refused while members use it.</summary>
    [Fact]
    public void Y3RoleRemoveStillRefusesALegacyRowMembersUseWhenNoRosterPackSuppliesIt()
    {
        InstallPack();
        Assert.Equal(0, Roster("init", "--level", "repo", "--route", "peer", "--roster", "r").Code);
        var data = Parse(File.ReadAllText(RepoFile));
        data["roles"] = Parse("{\"qc\":{\"from\":\"demo@market:qc\"}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));
        Assert.Equal(0, Roster("add", "--level", "repo", "--roster", "r", "--role", "qc").Code);

        var removed = Roster("role", "remove", "qc", "--roster", "r");

        Assert.Equal(2, removed.Code);
        Assert.Contains("roster members still use it", removed.Stderr, StringComparison.Ordinal);
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

    // ---------------------------------------------------------------- the removed trust verbs (ah2-698 D5)

    [Fact]
    public void RoleTrustAndRoleSetFromAnswerExitTwoWithThePointerToTheirReplacement()
    {
        InstallPack();

        var trust = Roster("role", "trust", "qc", "--pin", "sha256:" + new string('0', 64));
        var from = Roster("role", "set", "qc", "--from", "demo@market:qc", "--level", "repo");
        var pin = Roster("role", "set", "qc", "--class", "review", "--pin", "sha256:" + new string('0', 64), "--level", "repo");

        Assert.Equal(2, trust.Code);
        Assert.Contains("role trust was removed: an installed role pack is trusted, and its updates are reported in role list (adr-46)", trust.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, from.Code);
        Assert.Contains("pack roles come from a roster's packs: ah roster roster pack <roster> --add <plugin>@<marketplace> (or init --pack)", from.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, pin.Code);
        Assert.Contains("unrecognized flag --pin", pin.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile));

        // negative: a role set without --from is not the pointer; it still writes the row
        var own = Roster("role", "set", "qc", "--class", "review", "--routes", "qc work", "--description", "Checks things", "--level", "repo", "--scaffold", "repo");
        Assert.True(own.Code == 0, own.Stderr);
        Assert.DoesNotContain("roster's packs", own.Stderr, StringComparison.Ordinal);
    }
}
