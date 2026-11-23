using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Config;

/// <summary>What <c>process.cwd()</c> and <c>process.ppid</c> are for this process; the composition root supplies them.</summary>
public sealed record ProcessFacts(string CurrentDirectory, int ParentPid);

/// <summary>
/// The options of <c>resolveConfig</c>. A null <see cref="Pid"/> or <see cref="Roster"/> is JavaScript <c>undefined</c>;
/// <see cref="JsonValue.Null"/> is JavaScript <c>null</c> (for <see cref="Roster"/>, the default roster block).
/// </summary>
public sealed record ResolveOptions(
    string? Team = null,
    string? TeamHome = null,
    bool DefaultTeam = false,
    JsonValue? Pid = null,
    string? SessionId = null,
    JsonValue? Roster = null);

public sealed record TeamPrefixResult(string Prefix, string Source);

/// <summary>One role parameter: its value as written and the level it came from (<c>repo-user</c>, <c>repo</c> or <c>global</c>), or both null when no level sets it.</summary>
public sealed record RoleParam(string Name, string? Value, string? Level);

/// <summary><c>resolveHierarchyRole</c>: the role, whether the payload named it directly, and the config read to find it, when one was read.</summary>
public sealed record HierarchyRole(string? Role, bool Direct, JsonObject? Resolved)
{
    /// <summary>
    /// True for any resolved role other than the Orchestrator, whether the payload named it or the session map recorded it.
    /// A caller with no resolvable role is not restricted, so this is a belt against copying an identity, not a boundary.
    /// </summary>
    public bool Restricted => Role is not null && Role != "orchestrator";
}

/// <summary>The plugin CLIs a role is told to run.</summary>
public enum CliTool
{
    Roster,
    Msg,
    Gate,
    AhdCtl,
}

/// <summary>The core exports of the Node <c>lib-config</c>: config resolution, the role registry and the hook-input rules.</summary>
public interface IConfigCore
{
    /// <summary>The hierarchy runtime directory for a cwd (<c>WRANGL_HIERARCHY_DIR</c>, else the repo's <c>.claude/ah2</c>, else under the home directory).</summary>
    string HierarchyDir(string? cwd);

    /// <summary>The main checkout's hierarchy directory when <paramref name="cwd"/> is in a linked worktree, else null.</summary>
    string? MainHierarchyDir(string? cwd);

    /// <summary>
    /// Whether a session working in <paramref name="observedCwd"/> is where a member whose root is <paramref name="expectedRoot"/>
    /// belongs: both resolve, through their real paths, to the same git-root hierarchy directory. <c>WRANGL_HIERARCHY_DIR</c> is not
    /// consulted, since it would make every pair the same.
    /// </summary>
    bool IsPlaced(string observedCwd, string expectedRoot);

    string? FindGitRoot(string start);

    string TeamPrefix(string? cwd, string? team);

    TeamPrefixResult TeamPrefixInfo(string? cwd, string? team);

    /// <summary>The resolved configuration object, key for key as Node builds it.</summary>
    JsonObject ResolveConfig(string? cwd, ResolveOptions? options = null);

    /// <summary>The <c>enabled</c> of <see cref="ResolveConfig"/> for <paramref name="cwd"/>, read from the same layers without resolving the rest.</summary>
    bool ConfigEnabled(string? cwd);

    /// <summary>
    /// Whether the repository <paramref name="dir"/> belongs to has ah2 enabled in its config. A repository nobody configured is
    /// not enabled, whatever the default of <c>enabled</c> is; a directory no repository root can be derived from is not either.
    /// </summary>
    bool DirEnabled(string dir);

    /// <summary>The calling session's hierarchy role: from the payload's <c>agent_type</c>, else the persisted session map.</summary>
    HierarchyRole ResolveHierarchyRole(JsonValue input, JsonObject? resolved = null);

    /// <summary>True when the payload carries a non-empty <c>agent_id</c>.</summary>
    bool IsSubagent(JsonValue input);

    /// <summary>
    /// The pure half of <c>readHookInput</c>: decode, trim, parse. Empty input gives <c>{}</c>; input that does not parse
    /// is logged once through <paramref name="log"/> and gives <c>{}</c>.
    /// </summary>
    JsonValue ParseHookInput(ReadOnlySpan<byte> stdin, IHookErrorLog log);

    bool IsTeamAliasShape(string? name);

    /// <summary><c>{ok: true}</c> or <c>{ok: false, why}</c>.</summary>
    JsonObject ValidateTeamAlias(JsonValue teamAlias, JsonObject? resolved = null);

    string? RoleFromName(string? name, JsonObject? resolved = null);

    /// <summary>The strict resolution a team alias is checked with: a role token inside a word counts. A gate that denies uses it for a name outside the roster.</summary>
    string? AliasRoleFromName(string name, JsonObject? resolved = null);

    // ---------------------------------------------------------------- registry constants (lib-config exports of the same names)

    /// <summary><c>ROLES</c>: the built-in roles in display order.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary><c>MSG_ROLES</c>: <c>orchestrator</c> then <see cref="Roles"/>.</summary>
    IReadOnlyList<string> MsgRoles { get; }

    IReadOnlyList<string> ClassNames { get; }

    IReadOnlyList<string> PeerEligibleRoles { get; }

    IReadOnlyList<string> ReasoningModels { get; }

    IReadOnlyList<string> TopTierModels { get; }

    IReadOnlyList<string> HandoffModes { get; }

    IReadOnlyList<string> MsgsModes { get; }

    IReadOnlyList<string> RouteValues { get; }

    IReadOnlyList<string> StaleRouteValues { get; }

    IReadOnlyList<string> DispatchModes { get; }

    IReadOnlyList<string> RosterLevels { get; }

    IReadOnlyList<string> BuiltinHarnesses { get; }

    IReadOnlyList<string> TestHarnesses { get; }

    int ConfigVersion { get; }

    string ConfigBasename { get; }

    string KindDefault { get; }

    string KindCustom { get; }

    string CodexAppserverHarness { get; }

    string RouteMail { get; }

    /// <summary><c>CLASSES</c>, key for key, including each class's tool contract. A fresh object each time.</summary>
    JsonObject Classes { get; }

    JsonObject RoleLabels { get; }

    JsonObject RoleDefaults { get; }

    JsonObject ValidModelsByRole { get; }

    /// <summary><c>TIER</c>: <c>{haiku: 1, sonnet: 2, opus: 3, fable: 4}</c>.</summary>
    JsonObject Tier { get; }

    /// <summary>The installed plugin's root directory; refuses when the plugin location is not known.</summary>
    string PluginRoot { get; }

    /// <summary><c>MCP_TOOL_PREFIX</c>: every MCP tool's name starts with it.</summary>
    string McpToolPrefix { get; }

    /// <summary>The path of an ah binary as agents are told to type it and as permission rules spell it, under <paramref name="root"/> or the plugin root.</summary>
    string AhCommandPath(string program = "ah", string? root = null);

    /// <summary>The stable link <c>~/.local/bin/ah</c> as a path, whether or not it exists or may be used.</summary>
    string StableAhPath { get; }

    /// <summary>The plugin directory the stable link may be trusted to lead to, or null when it must not be used.</summary>
    string? StableAhPluginDir();

    /// <summary>
    /// The command line a role runs for one of the plugin's CLIs: <c>&lt;plugin&gt;/bin/ah roster|msg|gate</c>, or
    /// <c>&lt;plugin&gt;/bin/ahd</c> for the daemon CLI. Only the executable path is quoted, and only when it is not made of
    /// self-care-safe characters (or always, with <paramref name="alwaysQuote"/>), so the line passes the self-care gate unchanged.
    /// </summary>
    string CliCommand(CliTool tool, bool alwaysQuote = false);

    // ---------------------------------------------------------------- pure helpers

    /// <summary><c>KIND_RE.test(x)</c> and its siblings; the value is coerced to text as <c>RegExp.test</c> does.</summary>
    bool IsKindName(JsonValue? value);

    bool IsHarnessName(JsonValue? value);

    /// <summary><c>SELF_CARE_TOKEN_RE.test(x)</c>.</summary>
    bool IsSelfCareToken(JsonValue? value);

    /// <summary><c>ROLE_NAME_RE.test(x)</c>.</summary>
    bool IsRoleName(JsonValue? value);

    /// <summary><c>AGENT_REF_RE.test(x)</c>.</summary>
    bool IsAgentRef(JsonValue? value);

    /// <summary><c>LABEL_RE.test(x)</c>.</summary>
    bool IsLabel(JsonValue? value);

    bool IsBuiltinHarness(JsonValue? harness);

    /// <summary>A member's kind: <c>claude</c> when absent or null, else its value as written.</summary>
    JsonValue ResolveKind(JsonValue? member);

    bool RouteHasPane(JsonValue? route);

    bool IsPaneMember(JsonValue? member);

    string PaneSendText(JsonValue? member, JsonValue? reqPath, JsonValue? cwd);

    /// <summary><c>{ok: true}</c> or <c>{ok: false, why}</c>.</summary>
    JsonObject ValidateHerdrName(JsonValue? name);

    /// <summary>The tier rank (haiku 1 .. fable 4) of a model name, or null when it names no family.</summary>
    int? TierOf(JsonValue? model);

    bool IsBuiltinRole(string? role);

    string? RoleClass(string role, JsonObject? resolved);

    /// <summary>One class property (a key of <see cref="Classes"/>' rows), or null (<c>undefined</c>) when the role is unknown.</summary>
    JsonValue? ClassProp(string role, JsonObject? resolved, string prop);

    IReadOnlyList<string> CustomRoleNames(JsonObject? resolved);

    IReadOnlyList<string> RegistryRoles(JsonObject? resolved);

    IReadOnlyList<string> ChainRoles(JsonObject? resolved);

    /// <summary>The registry roles a session can join the mesh as: every role but a built-in marked subagent-only.</summary>
    IReadOnlyList<string> JoinableRoles(JsonObject? resolved);

    JsonValue RoleLabel(string role, JsonObject? resolved);

    string RoleAgent(string role, JsonValue? entry);

    /// <summary>The plugin's own name: the namespace of its agents and skills.</summary>
    string PluginName { get; }

    /// <summary><c>&lt;plugin&gt;:&lt;name&gt;</c> in this plugin's namespace.</summary>
    string Qualified(string name);

    /// <summary>The built-in role an agent type names in this plugin's namespace, or null.</summary>
    string? BuiltinRoleOf(string? agentType);

    bool IsOverride(string role, JsonValue? entry);

    string? ClassBuiltin(string? cls);

    string PeerName(string repoBasename, string role);

    string? CustomRoleFromSuffix(string? name, JsonObject? resolved);

    /// <summary><c>{prefix, role}</c> for a hierarchy session name, or null.</summary>
    JsonObject? HierarchyNameParts(string? name, JsonObject? resolved = null);

    JsonArray ResolvedPeerTargets(string role, JsonValue? entry, string repoBasename);

    JsonValue ResolvedPeerTarget(string role, JsonValue? entry, string repoBasename);

    JsonValue RosterMemberFor(JsonObject? resolved, string role);

    /// <summary><c>responseCommand({id, role, req, cwd})</c>: the line a role runs to create its response file.</summary>
    string ResponseCommand(JsonValue? options = null);

    /// <summary><c>fillCommand({id, role, req, cwd})</c>.</summary>
    string FillCommand(JsonValue? options = null);

    // ---------------------------------------------------------------- gate and session-role (pure parts)

    /// <summary><c>normalizeSessionId</c>: the trimmed id when it is a non-blank string, else <c>__nosession__</c>.</summary>
    string NormalizeSessionId(JsonValue? sessionId);

    // ---------------------------------------------------------------- agent files and the class contract

    /// <summary><c>AGENT_READ_CAP</c>: only this many bytes of an agent file are read.</summary>
    int AgentReadCap { get; }

    /// <summary><c>TASK_GOPHER</c>: the agent ref legwork is handed to.</summary>
    string TaskGopher { get; }

    /// <summary>Where an agent ref's file is: <c>{kind, path, level, shadowed, error, candidates}</c>.</summary>
    JsonObject LocateAgentFile(JsonValue? reference, string? cwd);

    /// <summary>Locate an agent ref's file and parse its frontmatter. Never throws.</summary>
    JsonObject ReadAgentFile(JsonValue? reference, string? cwd);

    JsonObject ParseAgentFrontmatter(string text);

    /// <summary><c>{findings, fm}</c>: a pack agent's frontmatter read strictly.</summary>
    JsonObject PackAgentParse(string text, string? path = null);

    JsonArray PackAgentFindings(string text, string? path = null);

    /// <summary><c>{effective, flagged}</c> from a pack agent's frontmatter.</summary>
    JsonObject PackToolReport(JsonValue? fm);

    /// <summary>A description as one directive-safe line, or null.</summary>
    JsonValue NormalizeDescription(JsonValue? text);

    /// <summary>
    /// Checks one custom row or built-in override against its class contract. <paramref name="spec"/> holds <c>role, cls, agent</c> and
    /// optionally <c>description, builtin, cwd, inMemory</c>. Returns <c>{findings, file, description}</c>.
    /// </summary>
    JsonObject ValidateAgentContract(JsonObject spec);

    bool HasContractErrors(JsonValue? findings);

    string FormatFindings(JsonArray findings);

    JsonArray PackFindings(string role, JsonObject pack, JsonObject? parsed = null);

    /// <summary>Validates one registry role when it needs validating; null (JSON null) when it does not.</summary>
    JsonValue ValidateRole(string role, JsonObject? resolved);

    IReadOnlyList<string> PackAgentRefs(JsonObject resolved);

    string SubagentType(string role, JsonValue? entry);

    /// <summary>A custom row that is an in-chain alternative; null (<c>undefined</c>) when the row's class is unknown.</summary>
    JsonValue? IsAlternative(string role, JsonValue? entry);

    string CustomTierText(JsonObject? resolved);

    /// <summary><c>{view, unavailable}</c>: the directive's view of the registry.</summary>
    JsonObject AvailabilityView(JsonObject resolved);

    bool LegworkHandedOff(JsonValue? member, JsonObject? registry, Func<bool> handoff);

    /// <summary><c>true</c>/<c>false</c>, or JSON null when the team's recorded roster block no longer resolves.</summary>
    JsonValue TeamIsPartial(string dir, string? cwd, string? teamName, JsonObject? team, JsonObject? registry = null);

    string OwnedTeamsLead(JsonArray teams);

    /// <summary>One <c>{team, resolved}</c> per owned team when the session owns more than one, else null.</summary>
    JsonArray? OwnedTeamConfigs(JsonObject? resolved, ResolveOptions? options = null);

    // ---------------------------------------------------------------- paths, roster levels and selection

    string DefaultRoster { get; }

    /// <summary><c>SHELL_SELF_CARE</c>: the <c>shell</c> registry value that opts a custom role into the self-care shell gate.</summary>
    string ShellSelfCare { get; }

    /// <summary>Why <paramref name="value"/> is not a role's <c>params</c> (1 to 8 distinct names, each <c>^[a-z][a-z0-9-]{0,39}$</c>), or null when it is.</summary>
    string? ParamsError(JsonValue value);

    /// <summary>Why <paramref name="value"/> is not a review role's <c>writes</c> (a non-empty list of checkout-relative globs, <c>*</c> and <c>**</c> only), or null when it is.</summary>
    string? WritesError(JsonValue value);

    /// <summary>Whether a write glob matches every path (<c>**</c>, <c>*</c>, <c>**/*</c>).</summary>
    bool WriteGlobMatchesAll(string glob);

    /// <summary>Whether a normalised checkout-relative path matches any glob of a valid <c>writes</c> list; false for an invalid one.</summary>
    bool WritesMatch(JsonValue writes, string relative);

    /// <summary>Every role parameter set at any level, the most specific level's value per name (repo-user, repo, global). Never throws for a missing or unreadable level file.</summary>
    IReadOnlyList<RoleParam> ParamValues(string? cwd);

    /// <summary>The parameters <paramref name="role"/> declares, in order, each with its value and level or none; <paramref name="resolved"/> is read for <paramref name="cwd"/> when null.</summary>
    IReadOnlyList<RoleParam> RoleParams(string? cwd, string role, JsonObject? resolved = null);

    string? ParamNameError(string name);

    string? ParamValueError(string value);

    IReadOnlyList<string> SelfCareBuiltinRoles { get; }

    int OneLineMax { get; }

    string UserConfigPath();

    string? ProjectConfigPath(JsonValue? cwd);

    /// <summary>The checkout a cwd is in: a linked worktree's main checkout, else the enclosing git checkout, else null.</summary>
    string? CheckoutRoot(string? cwd);

    string PathSlug(string absolutePath);

    string? MainCheckoutRoot(string worktreeRoot);

    /// <summary><c>{global, repo, "repo-user"}</c>: the config file of each roster level for a cwd.</summary>
    JsonObject RosterLevelPaths(string? cwd);

    /// <summary>Candidate config paths per roster level, most specific first.</summary>
    JsonObject RosterLevelCandidates(string? cwd);

    /// <summary><c>{layout, warnings}</c>: the team layout preference stored in the global config.</summary>
    JsonObject TeamLayoutPreference();

    bool IsValidTeamAlias(JsonValue? teamAlias);

    /// <summary>A team name to offer when the one a create would use cannot be used; JSON null when none fits.</summary>
    JsonValue SuggestTeamAlias(JsonValue? raw, JsonValue? context);

    JsonObject NormalizeRosterBlock(JsonObject block, JsonObject? registry);

    /// <summary>Drops, in place, every roster member that is not an object, and returns what it dropped.</summary>
    JsonArray DropNonObjectMembers(JsonObject data);

    /// <summary>Every roster block a level file holds, as <c>[label, key, block]</c>.</summary>
    JsonArray RosterBlocksOf(JsonObject data);

    JsonArray RosterMemberNames(JsonArray members, string repoBasename);

    /// <summary><c>{level, route, members, path, teamKey}</c>, or JSON null when no level has a roster.</summary>
    JsonValue ResolveRoster(string? cwd, string? rosterKey, string? namingPrefix, JsonObject? registry);

    JsonArray NamedRosterKeys(string? cwd);

    JsonArray SelectableRosters(string? cwd);

    /// <summary><c>{value, level, path}</c>, or JSON null when no <c>activeRoster</c> is set.</summary>
    JsonValue ActiveRosterSetting(string? cwd);

    /// <summary>Which roster block a command uses, and why. <paramref name="options"/> is <c>{flag, team: {dir, name}}</c>.</summary>
    JsonObject RosterSelection(string? cwd, JsonValue? options);

    JsonObject SelectionView(JsonObject selection);

    /// <summary>Why a selection is unusable, or JSON null when it is usable.</summary>
    JsonValue RosterSelectionProblem(string? cwd, JsonObject selection);

    JsonObject SessionRosterSelection(string? cwd, ResolveOptions? options = null);

    JsonArray? OwnedRosterSelections(string? cwd, ResolveOptions? options = null);

    /// <summary><c>{warnings, aliases}</c>: config keys that do nothing any more.</summary>
    JsonObject StaleTeamKeys(string? cwd, JsonObject? registry = null);

    /// <summary><c>{tiers, warnings}</c>: the tiers declared for non-Claude models in the global config.</summary>
    JsonObject DeclaredModelTiers();

    /// <summary>
    /// <c>{timeline_retention_days, read_model_max_mb, warnings}</c> from the global config's optional <c>ui</c> object: the days of timeline
    /// kept live (1 to 3650, default 7) and the read model's memory ceiling in MB (16 or more, null when unset); an invalid value gives its default and a warning.
    /// </summary>
    JsonObject UiSettings();

    /// <summary>
    /// <c>{retention_days, prune, warnings}</c> from the global config's optional <c>state</c> object: the days a settled peer row is kept (1 to
    /// 3650, default 14) and the prune mode, <c>count</c> (only count what would go, the default) or <c>on</c>; an invalid value gives the
    /// default and a warning.
    /// </summary>
    JsonObject StateSettings();

    /// <summary>
    /// The user's standing approval for the Orchestrator to grant bounded one-time Codex approvals: the top-level
    /// <c>codex_orchestrator_approvals</c> of the user-level config only: true when the file or the key is absent or the value is the boolean
    /// <c>true</c>; false for any other value, and when the file exists but cannot be read as an object. No project or team config can set it.
    /// </summary>
    bool CodexOrchestratorApprovals();

    JsonValue DeclaredTier(JsonValue? kind, JsonValue? model);

    string NonClaudeModelText(JsonValue? member);

    JsonValue RoleNameError(JsonValue? name);

    JsonValue AgentRefError(JsonValue? agent);

    JsonValue OneLineError(string field, JsonValue? value);

    string DefaultLabel(string name);

    /// <summary>Validates one custom or pack role row as the config loader does: <c>{error}</c> or the normalised row.</summary>
    JsonObject CheckCustomRow(string name, JsonValue raw);

    // ---------------------------------------------------------------- role packs

    /// <summary><c>PACK_MANIFEST</c>: the file name that makes an installed plugin a role pack.</summary>
    string PackManifestName { get; }

    /// <summary><c>PACK_AGENT_KEYS</c>: the frontmatter keys a pack agent may use.</summary>
    IReadOnlyList<string> PackAgentKeys { get; }

    /// <summary><c>UNATTENDED_LINE</c>: the dry run's line for an implement-class pack role that routes work.</summary>
    string UnattendedLine { get; }

    /// <summary><c>PIN_RE</c>: whether a value is a <c>sha256:</c> pin.</summary>
    bool IsPin(JsonValue? value);

    /// <summary><c>installRecords</c>: every install record with an install path, as <c>{key, plugin, marketplace, installPath, version, scope}</c>.</summary>
    JsonArray InstallRecordList();

    /// <summary><c>packRecords</c>: the install records whose install path holds a pack manifest.</summary>
    JsonArray PackRecordList();

    /// <summary><c>parseFrom</c>: <c>{plugin, marketplace, role}</c> of a <c>&lt;plugin&gt;[@&lt;marketplace&gt;]:&lt;role&gt;</c>, or JSON null.</summary>
    JsonValue ParseFrom(JsonValue? from);

    /// <summary><c>isFromRow</c>: whether a config row adopts a pack role.</summary>
    bool IsFromRow(JsonValue? raw);

    /// <summary><c>hiddenCharAt</c>: the first hidden character as <c>U+XXXX at line L, column C</c>, or JSON null.</summary>
    JsonValue HiddenCharAt(JsonValue? text);

    /// <summary><c>escapeTerminal</c>: text with every hidden character shown as <c>\u{...}</c>.</summary>
    string EscapeTerminal(JsonValue? text);

    /// <summary><c>pluginNameAt</c>: a plugin's name at a directory.</summary>
    string PluginNameAt(string dir);

    /// <summary><c>readPackManifest</c>: <c>{error, warnings, roles}</c>; never throws.</summary>
    JsonObject ReadPackManifest(string dir, string plugin);

    /// <summary><c>packTree</c>: <c>{files, symlink, error}</c> of a plugin tree.</summary>
    JsonObject PackTreeJson(string root);

    /// <summary><c>packPin</c>: the pin over a row, an agent's text and a tree's files.</summary>
    string PackPinOf(JsonObject row, string agentText, JsonArray files);

    /// <summary><c>packDigest</c>: a tree's content digest.</summary>
    string PackDigest(JsonArray files);

    /// <summary><c>readStoredCopy</c>: this machine's stored copy for a pin, or JSON null.</summary>
    JsonValue StoredCopy(JsonValue? pin);

    /// <summary><c>packNameClaims</c>: <c>{error, byRole, files}</c>, the two maps as objects in the order their entries were found.</summary>
    JsonObject PackNameClaimsJson(string dir, JsonValue? manifest);

    /// <summary><c>packClaimMessage</c>: the finding for agent files that take a pack agent's name.</summary>
    string PackClaimMessage(string plugin, string agent, IReadOnlyList<string> files);

    /// <summary><c>packRoleState</c>: pack role <paramref name="from"/> as installed now, checked against <paramref name="pin"/>.</summary>
    JsonObject PackRoleStateJson(JsonValue? from, JsonValue? pin);

    /// <summary><c>expandFromRow</c>: a <c>from</c> row expanded into the full row resolution checks, <c>{raw, pack}</c> or <c>{error}</c>.</summary>
    JsonObject ExpandFromRow(JsonObject entry);

    /// <summary><c>packExtras</c>: what a plugin carries besides its roles.</summary>
    JsonArray PackExtras(string dir, JsonValue? manifest);

    // ---------------------------------------------------------------- hook errors, plugin meta, member sessions

    string HookErrorLogPath { get; }

    /// <summary>Hook-error log entries from the last <paramref name="hours"/> hours, oldest first.</summary>
    JsonArray RecentHookErrors(DateTimeOffset now, double hours = 24);

    /// <summary>The daemon's rejected-request log.</summary>
    string RejectedLogPath { get; }

    /// <summary>The rows of the JSON-lines file <paramref name="path"/> whose <c>ts</c> is within the last <paramref name="hours"/> hours, oldest first; an absent or unreadable file reads as empty.</summary>
    JsonArray RecentRows(string path, DateTimeOffset now, double hours = 24);

    /// <summary>This plugin's version, or <c>"?"</c>.</summary>
    JsonValue PluginVersion();

    string CliRootLine();

    bool IsMemberSession { get; }

    string MemberAskTail { get; }

    /// <summary><c>{decision: "ask"|"deny", reason}</c>: a member session never asks.</summary>
    JsonObject AskDecision(string reason);

    bool IsTopLevelAgentSession(JsonValue? input);

    JsonValue HierarchyRoleOf(JsonValue? agentType, JsonObject? resolved = null, string? cwd = null);

    /// <summary><c>{role, resolved}</c>.</summary>
    JsonObject LookupRole(JsonValue? agentType, string? cwd);

    int SessionRoleVersion { get; }

    /// <summary><c>MAX_SESSIONS</c>: sessions a gate or session-role file keeps.</summary>
    int MaxSessions { get; }

    string NoSessionKey { get; }

    /// <summary>The role recorded for a session, or JSON null. An authority: the ah CLI gate restricts a session by it. A subagent shares its parent's session id.</summary>
    JsonValue ReadSessionRole(JsonValue? sessionId);

    /// <summary>The session's record (<c>pid</c>, <c>pid_start</c>, <c>at</c>) when it is the Orchestrator by command, else JSON null. A store that is absent or unreadable reads as not flagged.</summary>
    JsonValue ReadOrchestratorFlag(JsonValue? sessionId);

    /// <summary>Whether the user turned this session off with <c>/wrangl:orchestrator off</c>; only an explicit opt-in clears it.</summary>
    bool ReadOrchestratorDeclined(JsonValue? sessionId);

    /// <summary>True when a SendMessage target names <paramref name="expectedName"/>, with or without a trailing <c>[ref]</c>.</summary>
    bool IsGatedPeerTarget(JsonValue? target, JsonValue? expectedName);
}

public static partial class ConfigModule
{
    /// <summary>The grammar of a role name, unanchored: the one source for the role registry and for the recipient part of a message file name.</summary>
    public const string RoleNamePattern = "[a-z][a-z0-9-]{0,30}";

    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>The one line a session or brief gets when its role parameters cannot be read (RP-D7). The session always starts.</summary>
    public const string ParamsUnreadableLine = "ah: role parameters could not be read; run ah roster param list";

    /// <summary>
    /// The role-parameter block a member's session notice and its briefs carry (RP-D7, RP-D8), or null when the role declares none. Each value through
    /// <see cref="Ah.Core.Text.SafeText"/>, so a value cannot forge a frame.
    /// </summary>
    public static string? ParamBlock(IReadOnlyList<RoleParam> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (declared.Count == 0)
        {
            return null;
        }

        var lines = new List<string> { "Role parameters for this repo (`ah roster param list` shows them):" };
        lines.AddRange(declared.Select(p => p.Value is { } value
            ? $"- {p.Name}: {Ah.Core.Text.SafeText.Render(value, (500 * 6) + 2)} ({p.Level})"
            : $"- {p.Name}: NOT SET. Do not guess it: say so in your report and ask your lead to have it set (ah roster param set {p.Name} <value>)."));
        return string.Join('\n', lines);
    }

    /// <summary>The shape of a team alias or named-roster name: 1-32 letters, digits or <c>-</c>, starting with a letter or digit.</summary>
    public static bool IsTeamAliasShape(string? name) => ConfigRegistry.IsTeamAliasShape(name);

    /// <summary>Every built-in role in display order.</summary>
    public static IReadOnlyList<string> BuiltinRoles => ConfigRegistry.Roles;

    /// <summary>The model a member of the built-in <paramref name="role"/> launches on when nothing names one; null when its members are asked.</summary>
    public static string? MemberDefaultModel(string role) => ConfigRegistry.MemberDefaultModel(role);

    /// <summary>The plugin version that first shipped the built-in <paramref name="role"/>; null for a role that was built in from the start.</summary>
    public static string? BuiltinSince(string role) => ConfigRegistry.BuiltinSince(role);

    /// <summary>The built-in roles whose class is <paramref name="className"/>, in display order.</summary>
    public static IReadOnlyList<string> BuiltinRolesOfClass(string className) =>
        [.. ConfigRegistry.Roles.Where(r => ConfigRegistry.BuiltinClass(r) == className)];

    /// <summary>The built-in roles whose class is <paramref name="className"/> and that can run as a peer session, in display order.</summary>
    public static IReadOnlyList<string> PeerEligibleBuiltinRolesOfClass(string className) =>
        [.. BuiltinRolesOfClass(className).Where(r => ConfigRegistry.PeerEligibleRoles.Contains(r))];

    /// <summary>
    /// The state domains Ah.Config writes through: <c>session-roles</c> (ops <c>set</c>, <c>get</c>) and <c>ultra-gate</c> (ops
    /// <c>write</c>, <c>read</c>). The state engine hands each write op its write context.
    /// </summary>
    /// <param name="files">File reads.</param>
    /// <param name="environment">Environment variables and the home directory.</param>
    /// <param name="clock">The clock the recorded timestamps come from.</param>
    /// <param name="processes">The process table a session role's recorded process is checked against; without one it counts as alive.</param>
    /// <param name="liveRegistration">A session's live registration (pid and start), which pins a role entry that records no process.</param>
    /// <param name="grants">The grants tables (S11e): with them every <c>worktree-grants</c> write runs in the database; without, the files.</param>
    /// <param name="docs">The home documents (b397 group 2) the session roles and the ultra-gate choices are read from; without, both read empty.</param>
    public static IReadOnlyList<IStateDomain> CreateStateDomains(
        IFileReader files,
        IAhEnvironment environment,
        IClock clock,
        IProcessTable? processes = null,
        Func<string, (long Pid, string Start)?>? liveRegistration = null,
        IGrantSource? grants = null,
        IHomeDocs? docs = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        return [new SessionRolesDomain(docs, environment, clock, processes, liveRegistration), new UltraGateDomain(docs, environment, clock), new WorktreeGrantsDomain(files, environment, clock, grants)];
    }

    /// <summary>The reader of the worktree grants documents and their audit log; the <c>worktree-grants</c> domain is its only writer.</summary>
    public static IWorktreeGrants CreateWorktreeGrants(IFileReader files, IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        return new WorktreeGrants(files, environment);
    }

    /// <summary>
    /// The role of <paramref name="member"/> of <paramref name="team"/> in the member's grants record under the first of
    /// <paramref name="homes"/> that holds one, and its class resolved through the configured roles. Either is null when the record has no
    /// role or the role has no class; the whole result is null when there is no record. <paramref name="teamRole"/>, the role the
    /// member's team row names, wins over the grants record when it is given.
    /// </summary>
    public static MemberRoleInfo? MemberRole(IWorktreeGrants grants, IEnumerable<string> homes, string team, string member, JsonObject? resolved, string? teamRole = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(homes);
        if (teamRole is { Length: > 0 })
        {
            return new MemberRoleInfo(teamRole, ConfigRegistry.RoleClass(teamRole, resolved));
        }

        foreach (var home in homes)
        {
            var view = grants.Read(home, team);
            if (view.Present && view.Members.TryGetValue(member, out var record))
            {
                return record.Role is { Length: > 0 } role ? new MemberRoleInfo(role, ConfigRegistry.RoleClass(role, resolved)) : new MemberRoleInfo(null, null);
            }
        }

        return null;
    }

    /// <param name="files">File reads.</param>
    /// <param name="environment">Environment variables and the home directory.</param>
    /// <param name="teamFiles">
    /// Team-file reads (implemented by Ah.Roster). A factory, because Ah.Roster is built over this service and the two
    /// are composed together; it is called on first use.
    /// </param>
    /// <param name="process">The current directory and parent pid of this process.</param>
    /// <param name="trust">
    /// The trust store consulted before <c>WRANGL_HIERARCHY_DIR</c> is honoured outside test mode; null trusts nothing, so an
    /// override is then ignored outside test mode.
    /// </param>
    /// <param name="docs">The home documents (b397 group 2) the session roles are read from; without, every session reads as having no role.</param>
    public static IConfigCore CreateCore(IFileReader files, IAhEnvironment environment, Func<ITeamFiles> teamFiles, ProcessFacts process, ITrustStore? trust = null, IHomeDocs? docs = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(teamFiles);
        ArgumentNullException.ThrowIfNull(process);
        return new ConfigCore(files, environment, teamFiles, process, trust, docs);
    }

    /// <summary>
    /// The directory <c>trusted-homes</c> and <c>trusted-hierarchy-dirs</c> live in: the state home itself in test mode, so a
    /// sandbox never reaches the user's real files, and otherwise <c>&lt;passwd home&gt;/.claude/ah2</c>, which no
    /// environment variable can move. Throws <see cref="InvalidOperationException"/> where the passwd database has no home for the user.
    /// </summary>
    /// <param name="environment">Supplies the test-mode predicate, the state home and the passwd-derived home.</param>
    public static string TrustHome(IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.TestMode ? environment.AhHome : Ah.Core.Text.AhPaths.HomeUnder(environment.HomeDirectory.TrimEnd('/'));
    }

    /// <summary>The names no role may take.</summary>
    public static IReadOnlyList<string> ReservedRoleNames() => ConfigRegistry.ReservedRoleNames;

    /// <summary>Whether a team member row is a pane member (<see cref="IConfigCore.IsPaneMember"/>): a pure function of the row.</summary>
    public static bool IsPaneMember(JsonValue? member) => ConfigRegistry.IsPaneMember(member);

    /// <summary>Whether <paramref name="name"/> is a name herdr accepts (<see cref="IConfigCore.ValidateHerdrName"/>'s <c>ok</c>): a pure function of the name.</summary>
    public static bool IsHerdrName(JsonValue? name) => ConfigRegistry.ValidateHerdrName(name).Get("ok") is { IsBool: true } ok && ok.AsBool();

    /// <summary>The class of the built-in role <paramref name="role"/>; null for any other name.</summary>
    public static string? BuiltinRoleClass(string role) => ConfigRegistry.IsBuiltinRole(role) ? ConfigRegistry.BuiltinClass(role) : null;

    /// <summary>The text of the question the user is asked before ah gives spawned members launch-only permission rules.</summary>
    public const string MemberLaunchConsentQuestion = MemberLaunchConsent.Question;

    /// <summary>The <c>writer</c> mark the ask-record hook puts on the rows it appends to the asks log; only marked rows can consent.</summary>
    public const string AskRecordWriter = MemberLaunchConsent.AskRecordWriter;

    /// <summary>Whether a recorded answer to the consent question is consent: "Allow", or typed text that begins with the word "Allow".</summary>
    public static bool IsMemberLaunchAllowAnswer(string answer) => MemberLaunchConsent.IsAllowAnswer(answer);

    /// <summary>Whether a question's source tag marks the member-rules consent question.</summary>
    public static bool IsMemberLaunchConsentSource(string? source) => MemberLaunchConsent.IsConsentSource(source);

    /// <summary>Whether the user's recorded answers at <paramref name="asksPath"/> consent to launch-only member permission rules, and when not, why.</summary>
    public static MemberLaunchVerdict EvaluateMemberLaunchConsent(IFileReader files, string asksPath, bool enabled, string? onAt = null) =>
        MemberLaunchConsent.Evaluate(files, asksPath, enabled, onAt);

    /// <summary>The one-line remedy for a cause a member launched without its launch-only rules.</summary>
    public static string LaunchRemedy(string cause) => RemedyTable.LaunchRemedy(cause);

    /// <summary>Whether only the top Orchestrator can act on a launch cause, because only it may ask the user.</summary>
    public static bool LaunchCauseNeedsTopOrchestrator(string cause) => RemedyTable.NeedsTopOrchestrator(cause);

    /// <summary>The source tag of the question whose Allow answer lets an Orchestrator run <c>ah allow on</c>.</summary>
    public const string AllowOnSource = MemberLaunchConsent.AllowOnSource;

    /// <summary>The timestamp of the newest unused, fresh Allow answer to the <c>ah allow on</c> question in <c>asks.jsonl</c>, or null.</summary>
    public static string? FreshAllowOnProof(IFileReader files, string asksPath, DateTimeOffset now, string? usedAt) =>
        MemberLaunchConsent.FreshAllowOnProof(files, asksPath, now, usedAt);

    /// <summary>
    /// The one reader of <c>trusted-homes</c> and <c>trusted-hierarchy-dirs</c>.
    /// </summary>
    /// <param name="files">The file system the files and their entries are examined on.</param>
    /// <param name="defaultHome">The directory the two files live in: <c>&lt;passwd home&gt;/.claude/ah2</c>, never derived from <c>HOME</c>.</param>
    /// <param name="currentUserId">The invoking user's id; null makes every file untrusted.</param>
    public static ITrustStore CreateTrustStore(IFileReader files, Func<string> defaultHome, Func<uint?> currentUserId)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(defaultHome);
        ArgumentNullException.ThrowIfNull(currentUserId);
        return new TrustStore(files, defaultHome, currentUserId);
    }

    /// <summary>
    /// The derive-or-trusted hierarchy-directory rules, over the same derivation <see cref="CreateCore"/> uses with the environment
    /// override left out. Needs no team files and no working directory: every path it is given is absolute.
    /// </summary>
    /// <param name="files">The file system derivations and claims are examined on.</param>
    /// <param name="environment">Where the home directory, the override and the test-mode sandbox come from.</param>
    /// <param name="trust">The trust store; null trusts nothing.</param>
    /// <param name="currentUserId">The user a socket directory must belong to; null refuses every socket directory.</param>
    public static IHierarchyDirPolicy CreateHierarchyDirPolicy(IFileReader files, IAhEnvironment environment, ITrustStore? trust, Func<uint?> currentUserId)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(currentUserId);
        var core = new ConfigCore(
            files,
            environment,
            static () => throw new InvalidOperationException("the hierarchy-directory policy reads no team files"),
            new ProcessFacts("/", 0),
            trust);
        return new HierarchyDirPolicy(core, files, environment, trust, currentUserId);
    }
}
