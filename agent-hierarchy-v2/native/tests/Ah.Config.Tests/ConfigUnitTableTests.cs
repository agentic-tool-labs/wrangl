using System.Reflection;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Config.Tests;

/// <summary>
/// Runs the unit tables of <c>lib-config</c> and <c>lib-gate</c> that this assembly's exports are pinned by. The same
/// tables run on Node through <c>tests/contract-tools/units-run.mjs</c>; a case passes here only when its recorded Node
/// result is reproduced. Tables of functions that Ah.Config does not own are skipped here (Ah.Hierarchy.Tests runs the core).
/// </summary>
public sealed class ConfigUnitTableTests
{
    private const string PluginRoot = "/ahroot";

    private static readonly string NativeRoot = typeof(ConfigUnitTableTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    // Kept apart from tests/contracts/units so the core runner in Ah.Hierarchy.Tests, which fails on a function it does not
    // dispatch, never enumerates these.
    private static string UnitsDirectory => Path.Combine(NativeRoot, "..", "tests", "contracts", "units-c1a");

    private static IEnumerable<string> TableFiles() =>
        Directory.EnumerateFiles(UnitsDirectory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal);

    private static readonly HashSet<string> Owned = new(StringComparer.Ordinal)
    {
        "ROLES", "MSG_ROLES", "ROLE_LABELS", "ROLE_DEFAULTS", "VALID_MODELS_BY_ROLE", "CLASSES", "CLASS_NAMES", "PEER_ELIGIBLE_ROLES",
        "REASONING_MODELS", "TOP_TIER_MODELS", "HANDOFF_MODES", "MSGS_MODES", "ROUTE_VALUES", "STALE_ROUTE_VALUES", "DISPATCH_MODES",
        "ROSTER_LEVELS", "BUILTIN_HARNESSES", "TEST_HARNESSES", "CONFIG_VERSION", "CONFIG_BASENAME", "KIND_DEFAULT", "KIND_CUSTOM",
        "CODEX_APPSERVER_HARNESS", "ROUTE_MAIL", "TIER",
        "KIND_RE", "HARNESS_NAME_RE", "SELF_CARE_TOKEN_RE", "ROLE_NAME_RE", "AGENT_REF_RE", "LABEL_RE",
        "isBuiltinHarness", "resolveKind", "routeHasPane", "isPaneMember", "paneSendText", "validateHerdrName", "tierOf", "roleClass",
        "classProp", "isBuiltinRole", "customRoleNames", "registryRoles", "chainRoles", "roleLabel", "roleAgent", "isOverride",
        "classBuiltin", "peerName", "customRoleFromSuffix", "hierarchyNameParts", "resolvedPeerTargets", "resolvedPeerTarget",
        "rosterMemberFor", "responseCommand", "fillCommand", "normalizeSessionId", "isGatedPeerTarget",
        "AGENT_READ_CAP", "TASK_GOPHER", "parseAgentFrontmatter", "packAgentParse", "packAgentFindings", "packToolReport",
        "normalizeDescription", "hasContractErrors", "formatFindings", "validateAgentContract", "locateAgentFile", "readAgentFile",
        "validateRole", "packFindings", "packAgentRefs", "subagentType", "isAlternative", "customTierText", "availabilityView",
        "teamIsPartial", "ownedTeamsLead", "ownedTeamConfigs",
        "MAX_SESSIONS", "NO_SESSION_KEY", "SESSION_ROLE_VERSION",
        "readSessionRole", "write", "read", "set", "get",
        "DEFAULT_ROSTER", "SHELL_SELF_CARE", "SELF_CARE_BUILTIN_ROLES", "ONE_LINE_MAX", "HOOK_ERROR_LOG", "MEMBER_ASK_TAIL",
        "userConfigPath", "projectConfigPath", "checkoutRoot", "pathSlug", "mainCheckoutRoot", "rosterLevelPaths", "rosterLevelCandidates",
        "teamLayoutPreference", "isValidTeamAlias", "suggestTeamAlias", "normalizeRosterBlock", "dropNonObjectMembers", "rosterBlocksOf",
        "rosterMemberNames", "resolveRoster", "namedRosterKeys", "selectableRosters", "activeRosterSetting", "rosterSelection",
        "selectionView", "rosterSelectionProblem", "sessionRosterSelection", "ownedRosterSelections", "declaredModelTiers",
        "declaredTier", "nonClaudeModelText", "roleNameError", "agentRefError", "oneLineError", "defaultLabel", "checkCustomRow",
        "recentHookErrors", "isMemberSession", "askDecision", "isTopLevelAgentSession", "hierarchyRoleOf", "lookupRole",
        "PACK_MANIFEST", "PACK_AGENT_KEYS", "UNATTENDED_LINE", "MCP_TOOL_PREFIX", "PIN_RE", "installRecords", "packRecords", "parseFrom",
        "isFromRow", "hiddenCharAt", "escapeTerminal", "pluginNameAt", "readPackManifest", "packTree", "packPin", "packDigest",
        "readStoredCopy", "packNameClaims", "packClaimMessage", "packRoleState", "expandFromRow", "packExtras",
        "findGitRoot", "isTeamAliasShape", "validateTeamAlias", "roleFromName", "teamPrefixInfo", "staleTeamKeys",
        "designRoutingBlock", "contractBlock", "buildNudge", "buildDirective", "buildRoleSessionNotice", "statusReport",
        "CONVENTIONS_PATH", "git", "tryGit", "refExists", "originDefault", "validateConventions",
        "loadConventions", "globMatch",
    };

    // The text builders read the shipped contract files, so they run against the real plugin root.
    private static readonly HashSet<string> TextFunctions = new(StringComparer.Ordinal)
    {
        "designRoutingBlock", "contractBlock", "buildNudge", "buildDirective", "buildRoleSessionNotice", "statusReport",
    };

    private static readonly string RealPluginRoot = Path.GetFullPath(Path.Combine(NativeRoot, ".."));

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in TableFiles())
        {
            var table = UnitTable.Load(file);
            if (!Owned.Contains(table.Fn))
            {
                continue;
            }

            foreach (var c in table.Cases)
            {
                data.Add(Path.GetRelativePath(UnitsDirectory, file), c.Id);
            }
        }

        return data;
    }

    /// <summary>
    /// Cases whose Node result embeds the text of a V8 <c>JSON.parse</c> error. The native parser words its errors its own
    /// way, so these cases check the whole result except that text, which only has to open with "invalid: ".
    /// </summary>
    private static readonly HashSet<string> JsonMessageCases = new(StringComparer.Ordinal)
    {
        "loadConventions/invalid-json-word",
        "loadConventions/invalid-json-empty",
        "loadConventions/invalid-json-truncated",
        "loadConventions/invalid-json-trailing-comma",
        "loadConventions/invalid-json-trailing-garbage",
    };

    [Fact]
    public void EveryOwnedExportHasATable()
    {
        var present = TableFiles().Select(f => UnitTable.Load(f).Fn).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(Owned.Where(o => !present.Contains(o)).OrderBy(o => o, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesNode(string tableFile, string caseId)
    {
        var table = UnitTable.Load(Path.Combine(UnitsDirectory, tableFile));
        var c = table.Cases.Single(x => x.Id == caseId);
        var sandbox = UnitSandbox.Create();
        sandbox.WriteFiles(c);
        JsonValue? result = null;
        var threw = false;
        try
        {
            result = Contract(Dispatch(table.Fn, c, sandbox));
        }
        catch (Exception)
        {
            threw = true;
        }

        if (JsonMessageCases.Contains($"{table.Fn}/{c.Id}"))
        {
            Assert.False(threw);
            var actual = result!.Value.AsObject();
            var expected = c.Expect!.Value.AsObject();
            Assert.StartsWith("invalid: ", actual["status"].AsString(), StringComparison.Ordinal);
            Assert.StartsWith("invalid: ", expected["status"].AsString(), StringComparison.Ordinal);
            foreach (var key in expected.Keys.Where(k => k != "status"))
            {
                Assert.Equal(JsonText.Stringify(expected[key], 0), JsonText.Stringify(actual[key], 0));
            }

            return;
        }

        if (Environment.GetEnvironmentVariable("AH_UNITS_RERECORD") == "1" && !threw && result is { IsText: true } text
            && sandbox.Verify(c, result, threw) is not null)
        {
            var tablePath = Path.Combine(UnitsDirectory, tableFile);
            RerecordTextExpect(tablePath, c.Id, text.AsString());
            var rewritten = UnitTable.Load(tablePath).Cases.Single(x => x.Id == caseId);
            Assert.True(sandbox.Verify(rewritten, result, threw) is null, $"{table.Fn} / {c.Id}: still differs after the insert: {sandbox.Verify(rewritten, result, threw)}");
            return;
        }

        Assert.True(sandbox.Verify(c, result, threw) is null, $"{table.Fn} / {c.Id}: {sandbox.Verify(c, result, threw)}");
    }

    /// <summary>
    /// Opt-in re-recording (<c>AH_UNITS_RERECORD=1</c> with <c>AH_UNITS_INSERT_LINE</c> naming the start of a line the builder
    /// now emits): a stale text case gets exactly that one line inserted into its recorded <c>"expect"</c>, at the position the
    /// builder puts it. Nothing else in the table changes, so a diff is the inserted line and no other drift gets pinned.
    /// </summary>
    private static void RerecordTextExpect(string path, string caseId, string actual)
    {
        var prefix = Environment.GetEnvironmentVariable("AH_UNITS_INSERT_LINE");
        Assert.False(string.IsNullOrEmpty(prefix), "AH_UNITS_INSERT_LINE must name the line to insert");
        var actualLines = actual.Split('\n');
        var inserted = Array.FindIndex(actualLines, l => l.Contains(prefix!, StringComparison.Ordinal));
        Assert.True(inserted >= 0, $"{caseId}: the builder emits no line containing the prefix");
        var lines = File.ReadAllLines(path).ToList();
        var at = lines.FindIndex(l => l == $"      \"id\": \"{caseId}\",");
        var expectAt = at < 0 ? -1 : lines.FindIndex(at, l => l.StartsWith("      \"expect\": \"", StringComparison.Ordinal));
        Assert.True(expectAt > at && at >= 0, $"no one-line expect for {caseId}");
        var recorded = JsonText.Parse(Encoding.UTF8.GetBytes(lines[expectAt].Trim().Substring("\"expect\": ".Length).TrimEnd(','))).AsString().Split('\n').ToList();
        Assert.True(!recorded.Any(l => l.Contains(prefix!, StringComparison.Ordinal)), $"{caseId}: already holds the line");
        if (inserted == recorded.Count + 1 && actualLines[inserted - 1].Length == 0)
        {
            recorded.Add(string.Empty);
        }

        recorded.Insert(inserted, actualLines[inserted]);
        lines[expectAt] = "      \"expect\": " + JsonText.Stringify(string.Join("\n", recorded), 0) + (lines[expectAt].EndsWith(',') ? "," : string.Empty);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    /// <summary>Node writes the plugin root as <c>$AHROOT</c> in a table; the fake plugin here sits at <see cref="PluginRoot"/>.</summary>
    private static JsonValue? Contract(JsonValue? value) =>
        value is null
            ? (JsonValue?)null
            : JsonText.Parse(JsonText.Stringify(value.Value, 0)
                .Replace(RealPluginRoot, "$AHROOT", StringComparison.Ordinal)
                .Replace(PluginRoot, "$AHROOT", StringComparison.Ordinal));

    private static JsonValue Strings(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values)
        {
            a.Add(v);
        }

        return a;
    }

    /// <summary>Node's <c>Map</c> results are written <c>{"$map": [[key, value], ...]}</c> in a table.</summary>
    private static JsonValue AsMaps(JsonObject claims)
    {
        var result = new JsonObject { ["error"] = claims["error"] };
        foreach (var name in new[] { "byRole", "files" })
        {
            var pairs = new JsonArray();
            var map = claims[name].AsObject();
            foreach (var key in map.Keys)
            {
                var pair = new JsonArray();
                pair.Add(key);
                pair.Add(map[key]);
                pairs.Add(pair);
            }

            result[name] = new JsonObject { ["$map"] = pairs };
        }

        return result;
    }

    /// <summary>Runs <c>git</c> as the case's own <c>bin/git</c>, the way its <c>PATH</c> puts it first on Node.</summary>
    private sealed class SandboxGitRunner(IProcessRunner inner, string binDirectory) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request)
        {
            var script = Path.Combine(binDirectory, "git");
            if (request.Argv.Count > 0 && request.Argv[0] == "git" && File.Exists(script))
            {
                var argv = new List<string> { script };
                argv.AddRange(request.Argv.Skip(1));
                return inner.Run(request with { Argv = argv });
            }

            return inner.Run(request);
        }
    }

    private static GitScope ScopeOf(JsonValue? value) => value switch
    {
        { IsText: true } t => new GitScope(t.AsString()),
        { IsMap: true } m => new GitScope(
            m.AsObject().Get("cwd") is { IsText: true } cwd ? cwd.AsString() : string.Empty,
            m.AsObject().Get("gitDir") is { IsText: true } g ? g.AsString() : null,
            m.AsObject().Get("workTree") is { IsText: true } w ? w.AsString() : null),
        _ => throw new ArgumentException("not a git scope"),
    };

    private static JsonValue OriginJson(OriginDefaultRef? origin) =>
        origin is null ? JsonValue.Null : new JsonObject { ["ref"] = origin.Ref, ["branch"] = origin.Branch };

    private static JsonValue Nullable(string? s) => s is null ? JsonValue.Null : s;

    private static ResolveOptions Options(JsonObject? o) => o is null
        ? new ResolveOptions()
        : new ResolveOptions(
            Team: o.Get("team") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null,
            TeamHome: o.Get("teamHome") is { IsText: true } h && h.AsString().Length > 0 ? h.AsString() : null,
            DefaultTeam: o.Get("defaultTeam") is { IsBool: true } d && d.AsBool(),
            Pid: o.Get("pid"),
            SessionId: o.Get("sessionId") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null,
            Roster: o.Get("roster"));

    /// <summary>Calls a state op body the way the engine would: a write's DbBody over <see cref="TableHomeDocs"/>.</summary>
    private static JsonValue? RunOp(IFileReader files, IAhEnvironment env, UnitCase c, UnitSandbox sandbox, string domainName, string opName, JsonValue? args)
    {
        var instant = c.Now is not null ? DateTimeOffset.Parse(c.Now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        var docs = new TableHomeDocs(env);
        var domains = ConfigModule.CreateStateDomains(files, env, new FakeClock(instant), docs: docs);
        var op = domains.Single(d => d.Name == domainName).Ops.Single(o => o.Name == opName);
        var bytes = Ah.Core.Text.Utf8Text.Encode(JsonText.Stringify(args ?? new JsonObject(), 0));
        var context = new RequestContext(sandbox.WorkingDirectory(c), sandbox.WorkingDirectory(c), sandbox.WorkingDirectory(c), sandbox.WorkingDirectory(c));
        var call = new OpCall(opName, bytes, context);
        var result = op.DbBody is { } dbBody ? dbBody(call, docs) : op.Body(call, op.Kind == OpKind.Write ? new DirectWriteContext() : null);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error);
        }

        return JsonText.Parse(result.ResultJson.Span);
    }

    private sealed class DirectWriteContext : IStoreWriteContext
    {
        public void Write(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
        {
            File.WriteAllBytes(tmpPath, bytes.ToArray());
            File.Move(tmpPath, path, true);
        }

        public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
        {
            if (File.Exists(path))
            {
                return false;
            }

            File.WriteAllBytes(path, bytes.ToArray());
            return true;
        }

        public void Append(string path, ReadOnlySpan<byte> record)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
            stream.Write(record);
        }

        public void Rename(string fromPath, string toPath) => File.Move(fromPath, toPath, true);

        public bool Remove(string path)
        {
            var existed = File.Exists(path);
            File.Delete(path);
            return existed;
        }

        public void Mkdir(string path, UnixFileMode? mode = null) => Directory.CreateDirectory(path);
    }

    private static JsonValue? Dispatch(string fn, UnitCase c, UnitSandbox sandbox)
    {
        var env = new FakeEnvironment(sandbox.Environment(c), new PluginInfo(TextFunctions.Contains(fn) ? RealPluginRoot : PluginRoot, "wrangl", "0.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        IRosterCore roster = null!;
        IConfigCore config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(sandbox.WorkingDirectory(c), 4242), docs: new TableHomeDocs(env));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()));
        var root = Path.GetDirectoryName(sandbox.Environment(c)["HOME"])!;
        var conventions = ConfigModule.CreateConventions(new SandboxGitRunner(PlatformModule.CreateProcessRunner(), Path.Combine(root, "bin")));
        var text = ConfigModule.CreateDirectiveText(config, ConfigModule.CreateRoleContracts(config, io.Files, env), () => roster);

        JsonValue? Arg(int i) => i < c.Args.Count ? sandbox.Decode(c.Args[i]) : null;
        string? Text(int i) => Arg(i) is { IsText: true } t ? t.AsString() : null;
        JsonObject? Obj(int i) => Arg(i) is { IsMap: true } m ? m.AsObject() : null;
        string Req(int i) => Text(i) ?? throw new ArgumentException($"argument {i} is not text");

        switch (fn)
        {
            case "ROLES": return Strings(config.Roles);
            case "MSG_ROLES": return Strings(config.MsgRoles);
            case "ROLE_LABELS": return config.RoleLabels;
            case "ROLE_DEFAULTS": return config.RoleDefaults;
            case "VALID_MODELS_BY_ROLE": return config.ValidModelsByRole;
            case "CLASSES": return config.Classes;
            case "CLASS_NAMES": return Strings(config.ClassNames);
            case "PEER_ELIGIBLE_ROLES": return Strings(config.PeerEligibleRoles);
            case "REASONING_MODELS": return Strings(config.ReasoningModels);
            case "TOP_TIER_MODELS": return Strings(config.TopTierModels);
            case "HANDOFF_MODES": return Strings(config.HandoffModes);
            case "MSGS_MODES": return Strings(config.MsgsModes);
            case "ROUTE_VALUES": return Strings(config.RouteValues);
            case "STALE_ROUTE_VALUES": return Strings(config.StaleRouteValues);
            case "DISPATCH_MODES": return Strings(config.DispatchModes);
            case "ROSTER_LEVELS": return Strings(config.RosterLevels);
            case "BUILTIN_HARNESSES": return Strings(config.BuiltinHarnesses);
            case "TEST_HARNESSES": return Strings(config.TestHarnesses);
            case "CONFIG_VERSION": return config.ConfigVersion;
            case "CONFIG_BASENAME": return config.ConfigBasename;
            case "KIND_DEFAULT": return config.KindDefault;
            case "KIND_CUSTOM": return config.KindCustom;
            case "CODEX_APPSERVER_HARNESS": return config.CodexAppserverHarness;
            case "ROUTE_MAIL": return config.RouteMail;
            case "TIER": return config.Tier;

            case "KIND_RE": return config.IsKindName(Arg(0));
            case "HARNESS_NAME_RE": return config.IsHarnessName(Arg(0));
            case "SELF_CARE_TOKEN_RE": return config.IsSelfCareToken(Arg(0));
            case "ROLE_NAME_RE": return config.IsRoleName(Arg(0));
            case "AGENT_REF_RE": return config.IsAgentRef(Arg(0));
            case "LABEL_RE": return config.IsLabel(Arg(0));

            case "isBuiltinHarness": return config.IsBuiltinHarness(Arg(0));
            case "resolveKind": return config.ResolveKind(Arg(0));
            case "routeHasPane": return config.RouteHasPane(Arg(0));
            case "isPaneMember": return config.IsPaneMember(Arg(0));
            case "paneSendText": return config.PaneSendText(Arg(0), Arg(1), Arg(2));
            case "validateHerdrName": return config.ValidateHerdrName(Arg(0));
            case "tierOf": return config.TierOf(Arg(0)) is { } tier ? tier : JsonValue.Null;
            case "roleClass": return Nullable(config.RoleClass(Req(0), Obj(1)));
            case "classProp": return config.ClassProp(Req(0), Obj(1), Req(2));
            case "isBuiltinRole": return config.IsBuiltinRole(Text(0));
            case "customRoleNames": return Strings(config.CustomRoleNames(Obj(0)));
            case "registryRoles": return Strings(config.RegistryRoles(Obj(0)));
            case "chainRoles": return Strings(config.ChainRoles(Obj(0)));
            case "roleLabel": return config.RoleLabel(Req(0), Obj(1));
            case "roleAgent": return config.RoleAgent(Req(0), Arg(1));
            case "isOverride": return config.IsOverride(Req(0), Arg(1));
            case "classBuiltin": return Nullable(config.ClassBuiltin(Text(0)));
            case "peerName": return config.PeerName(Req(0), Req(1));
            case "customRoleFromSuffix": return Nullable(config.CustomRoleFromSuffix(Text(0), Obj(1)));
            case "hierarchyNameParts": return config.HierarchyNameParts(Text(0), Obj(1)) is { } parts ? parts : JsonValue.Null;
            case "resolvedPeerTargets": return config.ResolvedPeerTargets(Req(0), Arg(1), Req(2));
            case "resolvedPeerTarget": return config.ResolvedPeerTarget(Req(0), Arg(1), Req(2));
            case "rosterMemberFor": return config.RosterMemberFor(Obj(0), Req(1));
            case "responseCommand": return config.ResponseCommand(c.Args.Count == 0 || Arg(0) is null ? null : Arg(0));
            case "fillCommand": return config.FillCommand(c.Args.Count == 0 || Arg(0) is null ? null : Arg(0));

            case "AGENT_READ_CAP": return config.AgentReadCap;
            case "TASK_GOPHER": return config.TaskGopher;
            case "parseAgentFrontmatter": return config.ParseAgentFrontmatter(Req(0));
            case "packAgentParse": return config.PackAgentParse(Req(0), Text(1));
            case "packAgentFindings": return config.PackAgentFindings(Req(0), Text(1));
            case "packToolReport": return config.PackToolReport(Arg(0));
            case "normalizeDescription": return config.NormalizeDescription(Arg(0));
            case "hasContractErrors": return config.HasContractErrors(Arg(0));
            case "formatFindings": return config.FormatFindings(Arg(0)!.Value.AsArray());
            case "validateAgentContract": return config.ValidateAgentContract(Obj(0)!);
            case "locateAgentFile": return config.LocateAgentFile(Arg(0), Text(1));
            case "readAgentFile": return config.ReadAgentFile(Arg(0), Text(1));
            case "validateRole": return config.ValidateRole(Req(0), Obj(1));
            case "packFindings": return config.PackFindings(Req(0), Obj(1)!, Obj(2));
            case "packAgentRefs": return Strings(config.PackAgentRefs(Obj(0)!));
            case "subagentType": return config.SubagentType(Req(0), Arg(1));
            case "isAlternative": return config.IsAlternative(Req(0), Arg(1));
            case "customTierText": return config.CustomTierText(Obj(0));
            case "availabilityView": return config.AvailabilityView(Obj(0)!);
            case "teamIsPartial": return config.TeamIsPartial(Req(0), Text(1), Text(2), Obj(3), Obj(4));
            case "ownedTeamsLead": return config.OwnedTeamsLead(Arg(0)!.Value.AsArray());
            case "ownedTeamConfigs": return config.OwnedTeamConfigs(Obj(0), Options(Obj(1))) is { } configs ? configs : JsonValue.Null;

            case "MAX_SESSIONS": return config.MaxSessions;
            case "NO_SESSION_KEY": return config.NoSessionKey;
            case "SESSION_ROLE_VERSION": return config.SessionRoleVersion;
            case "readSessionRole": return config.ReadSessionRole(Arg(0));
            case "write": return RunOp(io.Files, env, c, sandbox, "ultra-gate", "write", Arg(0));
            case "read": return RunOp(io.Files, env, c, sandbox, "ultra-gate", "read", Arg(0));
            case "set": return RunOp(io.Files, env, c, sandbox, "session-roles", "set", Arg(0));
            case "get": return RunOp(io.Files, env, c, sandbox, "session-roles", "get", Arg(0));

            case "DEFAULT_ROSTER": return config.DefaultRoster;
            case "SHELL_SELF_CARE": return config.ShellSelfCare;
            case "SELF_CARE_BUILTIN_ROLES": return Strings(config.SelfCareBuiltinRoles);
            case "ONE_LINE_MAX": return config.OneLineMax;
            case "HOOK_ERROR_LOG": return config.HookErrorLogPath;
            case "MEMBER_ASK_TAIL": return config.MemberAskTail;
            case "userConfigPath": return config.UserConfigPath();
            case "projectConfigPath": return Nullable(config.ProjectConfigPath(Arg(0)));
            case "checkoutRoot": return Nullable(config.CheckoutRoot(Text(0)));
            case "pathSlug": return config.PathSlug(Req(0));
            case "mainCheckoutRoot": return Nullable(config.MainCheckoutRoot(Req(0)));
            case "rosterLevelPaths": return config.RosterLevelPaths(Text(0));
            case "rosterLevelCandidates": return config.RosterLevelCandidates(Text(0));
            case "teamLayoutPreference": return config.TeamLayoutPreference();
            case "isValidTeamAlias": return config.IsValidTeamAlias(Arg(0));
            case "suggestTeamAlias": return config.SuggestTeamAlias(Arg(0), Arg(1));
            case "normalizeRosterBlock": return config.NormalizeRosterBlock(Obj(0)!, Obj(1));
            case "dropNonObjectMembers": return config.DropNonObjectMembers(Obj(0)!);
            case "rosterBlocksOf": return config.RosterBlocksOf(Obj(0)!);
            case "rosterMemberNames": return config.RosterMemberNames(Arg(0)!.Value.AsArray(), Req(1));
            case "resolveRoster": return config.ResolveRoster(Text(0), Text(1), Text(2), Obj(3));
            case "namedRosterKeys": return config.NamedRosterKeys(Text(0));
            case "selectableRosters": return config.SelectableRosters(Text(0));
            case "activeRosterSetting": return config.ActiveRosterSetting(Text(0));
            case "rosterSelection": return config.RosterSelection(Text(0), Arg(1));
            case "selectionView": return config.SelectionView(Obj(0)!);
            case "rosterSelectionProblem": return config.RosterSelectionProblem(Text(0), Obj(1)!);
            case "sessionRosterSelection": return config.SessionRosterSelection(Text(0), Options(Obj(1)));
            case "ownedRosterSelections": return config.OwnedRosterSelections(Text(0), Options(Obj(1))) is { } selections ? selections : JsonValue.Null;
            case "declaredModelTiers": return config.DeclaredModelTiers();
            case "declaredTier": return config.DeclaredTier(Arg(0), Arg(1));
            case "nonClaudeModelText": return config.NonClaudeModelText(Arg(0));
            case "roleNameError": return config.RoleNameError(Arg(0));
            case "agentRefError": return config.AgentRefError(Arg(0));
            case "oneLineError": return config.OneLineError(Req(0), Arg(1));
            case "defaultLabel": return config.DefaultLabel(Req(0));
            case "checkCustomRow": return config.CheckCustomRow(Req(0), Arg(1) ?? JsonValue.Null);
            case "recentHookErrors":
            {
                var now = c.Now is not null ? DateTimeOffset.Parse(c.Now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
                return Arg(0) is { IsNumber: true } hours ? config.RecentHookErrors(now, hours.AsNumber()) : config.RecentHookErrors(now);
            }

            case "isMemberSession": return config.IsMemberSession;
            case "askDecision": return config.AskDecision(Req(0));
            case "isTopLevelAgentSession": return config.IsTopLevelAgentSession(Arg(0));
            case "hierarchyRoleOf":
            {
                var opts = Obj(1);
                var resolved = opts?.Get("resolved") is { IsMap: true } rv ? rv.AsObject() : null;
                var cwd = opts?.Get("cwd") is { IsText: true } cv && cv.AsString().Length > 0 ? cv.AsString() : null;
                return config.HierarchyRoleOf(Arg(0), resolved, cwd);
            }

            case "lookupRole": return config.LookupRole(Arg(0), Text(1));

            case "PACK_MANIFEST": return config.PackManifestName;
            case "PACK_AGENT_KEYS": return Strings(config.PackAgentKeys);
            case "UNATTENDED_LINE": return config.UnattendedLine;
            case "MCP_TOOL_PREFIX": return config.McpToolPrefix;
            case "PIN_RE": return config.IsPin(Arg(0));
            case "installRecords": return config.InstallRecordList();
            case "packRecords": return config.PackRecordList();
            case "parseFrom": return config.ParseFrom(Arg(0));
            case "isFromRow": return config.IsFromRow(Arg(0));
            case "hiddenCharAt": return config.HiddenCharAt(Arg(0));
            case "escapeTerminal": return config.EscapeTerminal(Arg(0));
            case "pluginNameAt": return config.PluginNameAt(Req(0));
            case "readPackManifest": return config.ReadPackManifest(Req(0), Req(1));
            case "packTree": return config.PackTreeJson(Req(0));
            case "packPin": return config.PackPinOf(Obj(0)!, Req(1), Arg(2)!.Value.AsArray());
            case "packDigest": return config.PackDigest(Arg(0)!.Value.AsArray());
            case "readStoredCopy": return config.StoredCopy(Arg(0));
            case "packNameClaims": return AsMaps(config.PackNameClaimsJson(Req(0), Arg(1)));
            case "packClaimMessage": return config.PackClaimMessage(Req(0), Req(1), Arg(2)!.Value.AsArray().Select(a => a.AsString()).ToList());
            case "packRoleState": return config.PackRoleStateJson(Arg(0), Arg(1));
            case "expandFromRow": return config.ExpandFromRow(Obj(0)!);
            case "packExtras": return config.PackExtras(Req(0), Arg(1));
            case "findGitRoot": return Nullable(config.FindGitRoot(Req(0)));
            case "isTeamAliasShape": return config.IsTeamAliasShape(Text(0));
            case "validateTeamAlias": return config.ValidateTeamAlias(Arg(0) ?? JsonValue.Null, Obj(1));
            case "roleFromName": return Nullable(config.RoleFromName(Text(0), Obj(1)));
            case "teamPrefixInfo":
            {
                var info = config.TeamPrefixInfo(Text(0), Text(1));
                return new JsonObject { ["prefix"] = info.Prefix, ["source"] = info.Source };
            }

            case "staleTeamKeys": return config.StaleTeamKeys(Text(0), Obj(1));

            case "designRoutingBlock": return Nullable(text.DesignRoutingBlock(Obj(0)!));
            case "contractBlock":
            {
                var peerRoute = Obj(2)?.Get("peerRoute") is { IsBool: true } peer && peer.AsBool();
                return Nullable(text.ContractBlock(Req(0), Obj(1), peerRoute));
            }

            case "buildNudge": return text.BuildNudge(Obj(0)!);
            case "buildDirective":
            {
                var extra = Obj(2);
                var hierDir = extra?.Get("hierDir") is { IsText: true } h ? h.AsString() : null;
                var model = extra?.Get("model") is { IsText: true } m ? m.AsString() : null;
                return text.BuildDirective(Obj(0)!, Text(1), hierDir, model);
            }

            case "buildRoleSessionNotice": return text.BuildRoleSessionNotice(Req(0), Text(1), Obj(2));
            case "statusReport": return text.StatusReport(Text(0));

            case "CONVENTIONS_PATH": return conventions.ConventionsPath;
            case "git": return conventions.Git(ScopeOf(Arg(0)), Arg(1)!.Value.AsArray().Select(a => a.AsString()).ToList());
            case "tryGit": return Nullable(conventions.TryGit(ScopeOf(Arg(0)), Arg(1)!.Value.AsArray().Select(a => a.AsString()).ToList()));
            case "refExists": return conventions.RefExists(ScopeOf(Arg(0)), Req(1));
            case "originDefault": return OriginJson(conventions.OriginDefault(ScopeOf(Arg(0))));
            case "validateConventions": return Nullable(conventions.ValidateConventions(Arg(0) ?? JsonValue.Null));
            case "loadConventions":
            {
                var result = conventions.LoadConventions(ScopeOf(Arg(0)));
                return new JsonObject
                {
                    ["opted_in"] = result.OptedIn,
                    ["default"] = OriginJson(result.Default),
                    ["status"] = result.Status,
                    ["conventions"] = result.Conventions is null ? JsonValue.Null : result.Conventions,
                };
            }

            case "globMatch": return conventions.GlobMatch(Req(0), Req(1));

            case "normalizeSessionId": return config.NormalizeSessionId(Arg(0));
            case "isGatedPeerTarget": return config.IsGatedPeerTarget(Arg(0), Arg(1));
            default:
                throw new NotSupportedException("no native dispatcher for " + fn);
        }
    }
}
