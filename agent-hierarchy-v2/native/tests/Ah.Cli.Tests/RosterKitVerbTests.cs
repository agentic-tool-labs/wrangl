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

/// <summary><c>roster kit export|import</c> over the real libraries, a sandbox repository and an installed role pack (spec roster-kit.md).</summary>
[Collection("RosterServices")]
public sealed class RosterKitVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    /// <summary>Set to run the next commands as a team member session would.</summary>
    private string? _teamFile;

    public RosterKitVerbTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "ah2"));
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string RepoFile => Path.Combine(Project, ".claude", "ah2", "agent-hierarchy.json");

    private string PackDir => Path.Combine(_sandbox.Root, "packs", "demo");

    private string KitFile => Path.Combine(_sandbox.Root, "team.kit.json");

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
    }

    private (int Code, string Stdout, string Stderr) Roster(params string[] args)
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
            ["WRANGL_TEAM_FILE"] = _teamFile,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient());
        var services = new Services(graph, io.Files, io.Clock, env, Project);
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

    /// <summary>Roster <c>kitted</c> at repo level: a custom role (scout, with a repo agent file), an adopted pack role (qc) and a built-in.</summary>
    private void DefineKittedRoster()
    {
        InstallPack();
        var from = "demo@market:qc";
        var pin = Parse(Roster("role", "set", "qc", "--from", from, "--level", "repo", "--dry-run").Stdout)["pin"].AsString();
        Assert.Equal(0, Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", pin).Code);
        Directory.CreateDirectory(Path.Combine(Project, ".claude", "agents"));
        File.WriteAllText(
            Path.Combine(Project, ".claude", "agents", "scout.md"),
            "---\nname: scout\ndescription: Scouts ahead\ntools: Read, Grep, Glob, SendMessage\n---\n\nYou scout ahead.\n");
        var scout = Roster("role", "set", "scout", "--class", "review", "--description", "Scouts ahead", "--level", "repo");
        Assert.True(scout.Code == 0, scout.Stderr);
        var data = Parse(File.ReadAllText(RepoFile));
        data["rosters"] = Parse("{\"kitted\":{\"members\":[{\"role\":\"qc\"},{\"role\":\"scout\"},{\"role\":\"reviewer\"}],\"max_lanes\":2}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));
    }

    // ---------------------------------------------------------------- K2

    [Fact]
    public void ExportCarriesOneAgentTextOneReferenceAndNoBuiltIn()
    {
        DefineKittedRoster();

        var export = Roster("kit", "export", "kitted", "--out", KitFile);

        Assert.True(export.Code == 0, export.Stderr);
        var kit = Parse(File.ReadAllText(KitFile));
        Assert.Equal(1, kit["kit"].AsNumber());
        Assert.Equal("kitted", kit["roster"].AsObject()["name"].AsString());
        Assert.Equal(2, kit["roster"].AsObject()["block"].AsObject()["max_lanes"].AsNumber());
        var roles = kit["roles"].AsObject();
        Assert.Equal(["qc", "scout"], roles.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("demo@market:qc", roles["qc"].AsObject()["row"].AsObject()["from"].AsString());
        Assert.Null(roles["qc"].AsObject().Get("agent"));
        var agent = roles["scout"].AsObject()["agent"].AsObject();
        Assert.Equal("scout.md", agent["file"].AsString());
        Assert.Equal(File.ReadAllText(Path.Combine(Project, ".claude", "agents", "scout.md")), agent["text"].AsString());

        var again = Roster("kit", "export", "kitted", "--out", KitFile);
        Assert.Equal(2, again.Code);
        Assert.Contains("pass --force", again.Stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- import --dry-run

    /// <summary>The kitted roster exported, then changed by <paramref name="edit"/> and written back.</summary>
    private void ExportAndEdit(Action<JsonObject> edit)
    {
        DefineKittedRoster();
        Assert.Equal(0, Roster("kit", "export", "kitted", "--out", KitFile).Code);
        var kit = Parse(File.ReadAllText(KitFile));
        edit(kit);
        File.WriteAllText(KitFile, JsonText.Stringify(JsonValue.FromObject(kit), 2));
    }

    private (int Code, string Stdout, string Stderr) DryRun() =>
        Roster("kit", "import", KitFile, "--level", "repo", "--agents", "repo", "--dry-run");

    private string Snapshot() => File.ReadAllText(RepoFile) + File.ReadAllText(Path.Combine(Project, ".claude", "agents", "scout.md"));

    private static string PinIn(string listing) => listing.TrimEnd('\n').Split('\n')[^1]["pin: ".Length..];

    private (int Code, string Stdout, string Stderr) CommitWith(string pin, string level = "repo", string agents = "repo") =>
        Roster("kit", "import", KitFile, "--level", level, "--agents", agents, "--pin", pin);

    [Fact]
    public void K6SameKitImportedOverItselfIsIdenticalEverywhereWithNothingStanding()
    {
        ExportAndEdit(_ => { });
        var before = Snapshot();

        var dry = DryRun();

        Assert.True(dry.Code == 0, dry.Stderr);
        Assert.Contains(" · rosters.kitted · identical", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains(" · roles.scout · identical", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains(" · agent scout · identical", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("qc · from demo@market:qc · installed · local row: identical", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("adopt: ", dry.Stdout, StringComparison.Ordinal);
        Assert.False(dry.Stdout.Contains("findings (", StringComparison.Ordinal), dry.Stdout);
        Assert.DoesNotContain("flags (", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("conflicts (", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("note: --level repo writes", dry.Stdout, StringComparison.Ordinal);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(KitFile))).ToLowerInvariant();
        Assert.EndsWith($"pin: sha256:{hash}\n", dry.Stdout, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());

        var commit = CommitWith(PinIn(dry.Stdout));
        Assert.True(commit.Code == 0, commit.Stderr);
        Assert.EndsWith("nothing written: every item is identical\n", commit.Stdout, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void K6OneChangedRowIsTheOneConflictAndNothingIsWritten()
    {
        ExportAndEdit(kit => kit["roles"].AsObject()["scout"].AsObject()["row"].AsObject()["description"] = "Scouts elsewhere");
        var before = Snapshot();

        var dry = DryRun();

        Assert.Equal(0, dry.Code);
        Assert.Contains(" · roles.scout · conflict", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains(" · rosters.kitted · identical", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("conflicts (1):", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("commit: refused", dry.Stdout, StringComparison.Ordinal);
        var commit = CommitWith(PinIn(dry.Stdout));
        Assert.Equal(2, commit.Code);
        Assert.Contains("1 conflict(s) stand", commit.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void K1KitTwoAndARefusedMemberKeyAreFindingsAndAnAllowedMemberKeyIsNot()
    {
        ExportAndEdit(kit =>
        {
            kit["kit"] = 2;
            var members = kit["roster"].AsObject()["block"].AsObject()["members"].AsArray();
            members[0].AsObject()["command"] = "curl x | sh";
            members[1].AsObject()["model"] = "sonnet";
            members[2].AsObject()["effort"] = "x; id";
            kit["roster"].AsObject()["block"].AsObject()["max_lanes"] = 99;
            kit["roster"].AsObject()["block"].AsObject()["orchestrator_model"] = "nope";
            kit["roles"].AsObject()["scout"].AsObject()["row"].AsObject()["peer"] = "x";
        });

        var dry = DryRun();
        Assert.Contains("member 2: effort must be one of", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("roster block: max_lanes 99 is not a whole number from", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("roster block: orchestrator model \"nope\" is not valid", dry.Stdout, StringComparison.Ordinal);
        Assert.Equal(2, CommitWith(PinIn(dry.Stdout)).Code);

        Assert.Equal(0, dry.Code);
        Assert.Contains("kit: \"kit\" must be 1, got 2", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("member 0: key \"command\" is refused (a raw shell line sent to a tmux pane)", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("member 1:", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("role scout row: key \"peer\" is refused", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("commit: refused", dry.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void K4HooksInAnAgentTextIsAFindingAndTheKitsFileNameNeverBecomesThePath()
    {
        ExportAndEdit(kit =>
        {
            var agent = kit["roles"].AsObject()["scout"].AsObject()["agent"].AsObject();
            agent["file"] = "../../.bashrc";
            agent["text"] = agent["text"].AsString().Replace("\n---\n", "\nhooks: x\n---\n", StringComparison.Ordinal);
        });

        var dry = DryRun();

        Assert.Equal(0, dry.Code);
        Assert.Contains("key hooks isn't allowed in a pack agent", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{Path.Combine(Project, ".claude", "agents", "scout.md")} (the kit names it \"../../.bashrc\"", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains(" · agent scout · conflict", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("commit: refused", dry.Stdout, StringComparison.Ordinal);
        var before = Snapshot();
        Assert.Equal(2, CommitWith(PinIn(dry.Stdout)).Code);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>T8 (ah2-698 D8, replaces K5): a block naming a pack that is not installed here is imported with its packs and one info line.</summary>
    [Fact]
    public void T8ABlockNamingAPackNotInstalledHereIsWrittenWithItsPacksAndAnInfoLine()
    {
        ExportForAFreshMachine();
        var kit = Parse(File.ReadAllText(KitFile));
        var block = kit["roster"].AsObject()["block"].AsObject();
        block["packs"] = Parse("{\"p\":[\"game-factory@bear-poppa\"]}")["p"];
        block["members"].AsArray().Add(Parse("{\"role\":\"playtester\"}"));
        File.WriteAllText(KitFile, JsonText.Stringify(JsonValue.FromObject(kit), 2));

        var dry = Roster("kit", "import", KitFile, "--level", "repo", "--agents", "repo", "--dry-run");

        Assert.True(dry.Code == 0, dry.Stderr);
        Assert.Contains("note: pack game-factory@bear-poppa is not installed here: /wrangl:agent-role install <source>\n", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("note: role qc comes from demo@market: ah roster roster pack kitted --add demo@market\n", dry.Stdout, StringComparison.Ordinal);
        Assert.False(dry.Stdout.Contains("findings (", StringComparison.Ordinal), dry.Stdout);
        var commit = CommitWith(PinIn(dry.Stdout));
        Assert.True(commit.Code == 0, commit.Stdout + commit.Stderr);
        var written = Parse(File.ReadAllText(RepoFile))["rosters"].AsObject()["kitted"].AsObject();
        Assert.Equal("[\"game-factory@bear-poppa\"]", JsonText.Stringify(written["packs"], 0));
    }

    /// <summary>
    /// T8's negative: a block naming only an installed pack prints no info line, and a member role neither the pack nor the kit offers is
    /// still a finding.
    /// </summary>
    [Fact]
    public void T8ABlockNamingAnInstalledPackPrintsNoInfoLineAndAnUnknownMemberRoleIsStillAFinding()
    {
        ExportForAFreshMachine();
        var kit = Parse(File.ReadAllText(KitFile));
        var block = kit["roster"].AsObject()["block"].AsObject();
        block["packs"] = Parse("{\"p\":[\"demo@market\"]}")["p"];
        block["members"].AsArray().Add(Parse("{\"role\":\"ghost\"}"));
        File.WriteAllText(KitFile, JsonText.Stringify(JsonValue.FromObject(kit), 2));

        var dry = Roster("kit", "import", KitFile, "--level", "repo", "--agents", "repo", "--dry-run");

        Assert.True(dry.Code == 0, dry.Stderr);
        Assert.DoesNotContain("is not installed here", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("role qc comes from", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("member role ghost is neither in the kit nor defined here", dry.Stdout, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- slice 3: the commit

    /// <summary>The kitted roster exported, then its custom role, its roster and its agent file removed here, as on a machine that never had them.</summary>
    private void ExportForAFreshMachine()
    {
        ExportAndEdit(_ => { });
        var data = Parse(File.ReadAllText(RepoFile));
        data["roles"].AsObject().Remove("scout");
        data.Remove("rosters");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));
        File.Delete(Path.Combine(Project, ".claude", "agents", "scout.md"));
    }

    private static readonly string[] KeptKeys = ["permissions", "hooks", "activeRoster"];

    private string GlobalFile => Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");

    private string UserAgent => Path.Combine(_sandbox.Home, ".claude", "agents", "scout.md");

    [Fact]
    public void K3AWrongPinOrAMemberSessionWritesNothing()
    {
        ExportForAFreshMachine();
        var pin = PinIn(Roster("kit", "import", KitFile, "--dry-run").Stdout);

        var wrong = CommitWith("sha256:" + new string('0', 64), "global", "user");
        _teamFile = Path.Combine(_sandbox.Root, "team.json");
        var member = CommitWith(pin, "global", "user");

        Assert.Equal(2, wrong.Code);
        Assert.Contains("doesn't match this kit", wrong.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, member.Code);
        Assert.Contains("kit import: refused — this session is a team member (WRANGL_TEAM_FILE is set)", member.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(GlobalFile));
        Assert.False(File.Exists(UserAgent));
    }

    [Fact]
    public void K7ACommitWritesTheNewItemsAndLeavesPermissionsHooksAndActiveRosterByteIdentical()
    {
        ExportForAFreshMachine();
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalFile)!);
        File.WriteAllText(GlobalFile, "{\"version\":1,\"permissions\":{\"allow\":[\"Bash(ls)\"]},\"hooks\":{\"Stop\":[]},\"activeRoster\":\"kitted\"}");
        string Kept() => string.Join("|", KeptKeys.Select(k => JsonText.Stringify(Parse(File.ReadAllText(GlobalFile))[k], 0)));
        var kept = Kept();
        var dry = Roster("kit", "import", KitFile, "--dry-run");
        Assert.Contains(" · rosters.kitted · new", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains(" · roles.scout · new", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{UserAgent} · agent scout · new", dry.Stdout, StringComparison.Ordinal);

        var commit = CommitWith(PinIn(dry.Stdout), "global", "user");

        Assert.True(commit.Code == 0, commit.Stdout + commit.Stderr);
        Assert.Contains($"wrote {UserAgent}\n", commit.Stdout, StringComparison.Ordinal);
        Assert.Contains($"wrote {GlobalFile}\n", commit.Stdout, StringComparison.Ordinal);
        var global = Parse(File.ReadAllText(GlobalFile));
        Assert.Equal("kitted", global["activeRoster"].AsString());
        Assert.Equal(3, global["rosters"].AsObject()["kitted"].AsObject()["members"].AsArray().Count);
        Assert.Equal("Scouts ahead", global["roles"].AsObject()["scout"].AsObject()["description"].AsString());
        Assert.Null(global["roles"].AsObject().Get("qc"));
        Assert.Equal(Parse(File.ReadAllText(KitFile))["roles"].AsObject()["scout"].AsObject()["agent"].AsObject()["text"].AsString(), File.ReadAllText(UserAgent));
        Assert.Equal(kept, Kept());
    }

    [Fact]
    public void D1AHandMadeRowIsWrittenAsGivenAndReadsIdenticalOnTheSecondImport()
    {
        ExportForAFreshMachine();
        var kit = Parse(File.ReadAllText(KitFile));
        var row = kit["roles"].AsObject()["scout"].AsObject()["row"].AsObject();
        row.Remove("label");
        row.Remove("agent");
        File.WriteAllText(KitFile, JsonText.Stringify(JsonValue.FromObject(kit), 2));

        var first = CommitWith(PinIn(Roster("kit", "import", KitFile, "--dry-run").Stdout), "global", "user");
        var second = Roster("kit", "import", KitFile, "--dry-run");
        var again = CommitWith(PinIn(second.Stdout), "global", "user");

        Assert.True(first.Code == 0, first.Stdout + first.Stderr);
        var written = Parse(File.ReadAllText(GlobalFile))["roles"].AsObject()["scout"].AsObject();
        Assert.False(written.ContainsKey("agent"));
        Assert.False(written.ContainsKey("label"));
        Assert.Contains(" · roles.scout · identical", second.Stdout, StringComparison.Ordinal);
        Assert.True(again.Code == 0, again.Stderr);
        Assert.EndsWith("nothing written: every item is identical\n", again.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegworkRoleKeepsItsModelDispatchAcrossExportImportAndASecondImport()
    {
        DefineKittedRoster();
        var data = Parse(File.ReadAllText(RepoFile));
        var local = data["roles"].AsObject()["scout"].AsObject();
        local["class"] = "legwork";
        local["dispatch"] = "model";
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));
        Assert.Equal(0, Roster("kit", "export", "kitted", "--out", KitFile).Code);
        data["roles"].AsObject().Remove("scout");
        data.Remove("rosters");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));
        File.Delete(Path.Combine(Project, ".claude", "agents", "scout.md"));

        var first = CommitWith(PinIn(Roster("kit", "import", KitFile, "--dry-run").Stdout), "global", "user");
        var second = Roster("kit", "import", KitFile, "--dry-run");
        var again = CommitWith(PinIn(second.Stdout), "global", "user");

        Assert.True(first.Code == 0, first.Stdout + first.Stderr);
        Assert.Equal("model", Parse(File.ReadAllText(GlobalFile))["roles"].AsObject()["scout"].AsObject()["dispatch"].AsString());
        Assert.Contains(" · roles.scout · identical", second.Stdout, StringComparison.Ordinal);
        Assert.True(again.Code == 0, again.Stderr);
        Assert.EndsWith("nothing written: every item is identical\n", again.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void C4AFailedLevelFileWriteRemovesTheAgentFilesThisRunCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ExportForAFreshMachine();
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalFile)!);
        File.WriteAllText(GlobalFile, "{\"version\":1}");
        var pin = PinIn(Roster("kit", "import", KitFile, "--dry-run").Stdout);
        File.SetUnixFileMode(GlobalFile, UnixFileMode.UserRead);

        var commit = CommitWith(pin, "global", "user");

        File.SetUnixFileMode(GlobalFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(2, commit.Code);
        Assert.Contains($"the level file was not written; removed the agent file(s) this run had written: {UserAgent}", commit.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(UserAgent));
        Assert.Equal("{\"version\":1}", File.ReadAllText(GlobalFile));
    }

    [Fact]
    public void K8RulesAreShownVerbatimWithTheNotImportedLine()
    {
        ExportAndEdit(kit => kit["rules"] = Parse("{\"r\":[{\"key\":\"tone\",\"text\":\"Be brief.\"}]}")["r"]);

        var dry = DryRun();

        Assert.Equal(0, dry.Code);
        Assert.Contains("  tone: Be brief.\n", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("rules: not carried by this kit (roster rules are added in a later version)", dry.Stdout, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- amendment 2 (review round 1)

    [Fact]
    public void ABuiltInModelOverrideIsExportedAsARowAndImportsWithNoFinding()
    {
        DefineKittedRoster();
        Assert.Equal(0, Roster("role", "set", "reviewer", "--model", "sonnet", "--level", "repo").Code);
        var export = Roster("kit", "export", "kitted", "--out", KitFile);
        Assert.True(export.Code == 0, export.Stderr);
        var reviewer = Parse(File.ReadAllText(KitFile))["roles"].AsObject()["reviewer"].AsObject();
        Assert.Equal("sonnet", reviewer["row"].AsObject()["model"].AsString());
        Assert.Null(reviewer.Get("agent"));

        var dry = DryRun();

        Assert.Contains(" · roles.reviewer · identical", dry.Stdout, StringComparison.Ordinal);
        Assert.False(dry.Stdout.Contains("findings (", StringComparison.Ordinal), dry.Stdout);
    }

    [Fact]
    public void RoleSetChecksApplyToKitRowsAndAPeerDispatchPasses()
    {
        ExportAndEdit(kit =>
        {
            kit["roles"].AsObject()["reviewer"] = Parse("{\"row\":{\"label\":\"Rev\",\"model\":\"haiku\"}}");
            kit["roles"].AsObject()["scout"].AsObject()["row"].AsObject()["dispatch"] = "model";
        });

        var dry = DryRun();

        Assert.Contains("role reviewer row: --label does not apply to a built-in role", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("role reviewer row: model \"haiku\" is not allowed for reviewer", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("role scout row: --dispatch model is not allowed for a review role", dry.Stdout, StringComparison.Ordinal);

        var kit = Parse(File.ReadAllText(KitFile));
        kit["roles"].AsObject()["scout"].AsObject()["row"].AsObject()["dispatch"] = "peer";
        File.WriteAllText(KitFile, JsonText.Stringify(JsonValue.FromObject(kit), 2));
        Assert.DoesNotContain("role scout row: --dispatch", DryRun().Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AKitThatIsNotARegularFileIsAUsageErrorBeforeAnyRead()
    {
        var device = Roster("kit", "import", "/dev/zero", "--dry-run");

        Assert.Equal(2, device.Code);
        Assert.Contains("/dev/zero is not a regular file", device.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void APackReferenceWithAShellMetacharacterIsAFindingAndPrintsNoAdoptCommand()
    {
        ExportAndEdit(kit =>
        {
            kit["roles"].AsObject()["x;id"] = Parse("{\"row\":{\"from\":\"demo@market:qc\"}}");
            kit["roles"].AsObject()["qc"].AsObject()["row"].AsObject()["from"] = "demo@market:qc$(id)";
        });

        var dry = DryRun();

        Assert.Contains("role x;id: ", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("role qc: from \"demo@market:qc$(id)\" must be <plugin>@<marketplace>:<role>", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("adopt: ah roster role set x;id", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("$(id) --dry-run", dry.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoKitRolesNamingOneAgentIsAFinding()
    {
        ExportAndEdit(kit =>
        {
            var copy = Parse(JsonText.Stringify(kit["roles"].AsObject()["scout"], 0));
            copy["row"].AsObject()["agent"] = "scout";
            kit["roles"].AsObject()["scout2"] = copy;
        });

        var dry = DryRun();

        Assert.Contains("role scout2: agent \"scout\" is also role scout's agent in this kit", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("role scout: agent \"scout\" is also", dry.Stdout, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- scaffold-tools Amendment 1

    private string LintAgentFile => Path.Combine(Project, ".claude", "agents", "lint.md");

    /// <summary>A review-class role made with <c>--scaffold repo</c> in roster <c>linted</c>, exported, then the machine's copy removed.</summary>
    private void ExportScaffoldedThenForget(Action<JsonObject>? edit = null)
    {
        var set = Roster("role", "set", "lint", "--class", "review", "--description", "Lints things", "--scaffold", "repo", "--level", "repo");
        Assert.True(set.Code == 0, set.Stderr);
        Assert.DoesNotContain("\ntools:", File.ReadAllText(LintAgentFile), StringComparison.Ordinal);
        var data = Parse(File.ReadAllText(RepoFile));
        data["rosters"] = Parse("{\"linted\":{\"members\":[{\"role\":\"lint\"}]}}");
        File.WriteAllText(RepoFile, JsonText.Stringify(JsonValue.FromObject(data), 2));
        var export = Roster("kit", "export", "linted", "--out", KitFile);
        Assert.True(export.Code == 0, export.Stderr);
        if (edit is not null)
        {
            var kit = Parse(File.ReadAllText(KitFile));
            edit(kit);
            File.WriteAllText(KitFile, JsonText.Stringify(JsonValue.FromObject(kit), 2));
        }

        File.Delete(RepoFile);
        File.Delete(LintAgentFile);
    }

    [Fact]
    public void AScaffoldedRoleIsOneFlagNoFindingAndThePinImportsIt()
    {
        ExportScaffoldedThenForget();

        var dry = DryRun();

        Assert.True(dry.Code == 0, dry.Stderr);
        Assert.Contains("flags (1) — approved by the pin:\n  role lint agent lint: inherits every tool, MCP servers included (except: Edit, Write, NotebookEdit, advisor)\n", dry.Stdout, StringComparison.Ordinal);
        Assert.True(dry.Stdout.IndexOf("flags (1)", StringComparison.Ordinal) < dry.Stdout.IndexOf("writes:", StringComparison.Ordinal), dry.Stdout);
        Assert.DoesNotContain("findings (", dry.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("inherits every tool (no tools: list)", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("    flagged tools: every tool (no tools: list) except: Edit, Write, NotebookEdit, advisor\n", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("commit: nothing stands against a commit", dry.Stdout, StringComparison.Ordinal);

        var commit = CommitWith(PinIn(dry.Stdout));

        Assert.True(commit.Code == 0, commit.Stderr);
        Assert.Contains("role lint agent lint: inherits every tool, MCP servers included", commit.Stdout, StringComparison.Ordinal);
        Assert.Contains($"wrote {LintAgentFile}\n", commit.Stdout, StringComparison.Ordinal);
        Assert.True(Parse(File.ReadAllText(RepoFile))["roles"].AsObject().ContainsKey("lint"));
    }

    [Fact]
    public void M6AKitAgentsServerGrantPrintsItsWarningLineBeforeTheFlatList()
    {
        ExportScaffoldedThenForget(kit =>
        {
            var agent = kit["roles"].AsObject()["lint"].AsObject()["agent"].AsObject();
            agent["text"] = agent["text"].AsString().Replace("\n---\n", "\ntools: Read, SendMessage, mcp__srv__*\n---\n", StringComparison.Ordinal);
        });

        var dry = DryRun();

        const string line = "    ⚠ grants EVERY tool of MCP server mcp__srv, including tools it adds later; Ahd cannot see or pin that list — source: a server you configure (not pinned)\n    flagged tools: mcp__srv__*\n";
        Assert.Contains(line, dry.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void M6NegativeAKitWithNoGrantPrintsNoWarningLine()
    {
        ExportScaffoldedThenForget();

        Assert.DoesNotContain("⚠ grants EVERY tool", DryRun().Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AKitAgentWithNoToolsAndHooksIsStillRefused()
    {
        ExportScaffoldedThenForget(kit =>
        {
            var agent = kit["roles"].AsObject()["lint"].AsObject()["agent"].AsObject();
            agent["text"] = agent["text"].AsString().Replace("\n---\n", "\nhooks: x\n---\n", StringComparison.Ordinal);
        });

        var dry = DryRun();

        Assert.Contains("key hooks isn't allowed in a pack agent", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("commit: refused", dry.Stdout, StringComparison.Ordinal);
        Assert.Equal(2, CommitWith(PinIn(dry.Stdout)).Code);
        Assert.False(File.Exists(LintAgentFile));
    }

    [Fact]
    public void APackAgentWithNoToolsIsStillRefusedAtAdoptAndAtTrust()
    {
        InstallPack();
        var from = "demo@market:qc";
        var pin = Parse(Roster("role", "set", "qc", "--from", from, "--level", "repo", "--dry-run").Stdout)["pin"].AsString();
        Assert.Equal(0, Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", pin).Code);
        File.WriteAllText(Path.Combine(PackDir, "agents", "qc.md"), "---\nname: qc\ndescription: Checks things\n---\n\nYou check things.\n");

        var trustDry = Roster("role", "trust", "qc", "--level", "repo", "--dry-run");
        Assert.Contains("tools is required", trustDry.Stdout + trustDry.Stderr, StringComparison.Ordinal);
        var trust = Roster("role", "trust", "qc", "--level", "repo", "--pin", Parse(trustDry.Stdout)["pin"].AsString());
        Assert.Equal(2, trust.Code);
        Assert.Contains("role trust qc: errors stand", trust.Stderr, StringComparison.Ordinal);

        Assert.Equal(0, Roster("role", "remove", "qc", "--level", "repo").Code);
        var adoptDry = Roster("role", "set", "qc", "--from", from, "--level", "repo", "--dry-run");
        Assert.Contains("tools is required", adoptDry.Stdout + adoptDry.Stderr, StringComparison.Ordinal);
        var adopt = Roster("role", "set", "qc", "--from", from, "--level", "repo", "--pin", Parse(adoptDry.Stdout)["pin"].AsString());
        Assert.Equal(2, adopt.Code);
        Assert.Contains("role set qc --from: errors stand", adopt.Stderr, StringComparison.Ordinal);
    }
}
