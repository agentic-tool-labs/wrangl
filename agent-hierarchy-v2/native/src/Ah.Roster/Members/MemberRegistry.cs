using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>
/// The role and kind facts the member validators read from the configuration layer. Ah.Roster does not hold a second
/// copy of them: the composition root passes the Ah.Config registry's answers through this seam.
/// </summary>
public interface IMemberRegistry
{
    /// <summary>The kind of a member that names no kind.</summary>
    string KindDefault { get; }

    /// <summary>The kind of an agent runtime ah has no adapter for.</summary>
    string KindCustom { get; }

    /// <summary>The route of a member reachable only through its mailbox.</summary>
    string RouteMail { get; }

    /// <summary>The harness names ah ships adapters for.</summary>
    IReadOnlyList<string> BuiltinHarnesses { get; }

    /// <summary>Whether <paramref name="kind"/> is shaped like a kind: a lowercase letter, then lowercase letters, digits and hyphens.</summary>
    bool IsKindShape(string kind);

    /// <summary>Whether <paramref name="name"/> is shaped like a custom harness name.</summary>
    bool IsHarnessName(string name);

    /// <summary>Every registry role: the built-ins in display order, then the custom roles of <paramref name="resolved"/> by name.</summary>
    IReadOnlyList<string> RegistryRoles(JsonObject? resolved);

    /// <summary>The class of the role named <paramref name="role"/>, or null when it is unknown.</summary>
    string? RoleClass(string role, JsonObject? resolved);

    /// <summary>The models a class allows, or null when there is no such class.</summary>
    IReadOnlyList<string>? ClassModels(string cls);

    /// <summary>Whether the class is a chain class, one the Orchestrator hands work to in turn.</summary>
    bool ClassIsChain(string cls);

    /// <summary>Whether the role can be a peer: its class is a chain class and the role is not marked as running only as a subagent.</summary>
    bool IsPeerEligible(string role, JsonObject? resolved);

    /// <summary>The tier the user declared for <paramref name="model"/> of <paramref name="kind"/>, or null.</summary>
    string? DeclaredTier(string kind, string model);
}

/// <summary>
/// The member schema: what a roster member, a team member and a whole roster block may hold. Every function is a pure function
/// of the JSON it is given and the registry facts behind <see cref="IMemberRegistry"/>.
/// </summary>
public interface IMemberRules
{
    /// <summary>A member's <c>args</c> as a list, with absent and empty the same thing (null).</summary>
    JsonArray? MemberArgs(JsonValue? member);

    /// <summary>The native flags a non-claude member's <c>autoMode</c> becomes, or null when it has none or its kind has no mapping.</summary>
    IReadOnlyList<string>? KindAutoModeArgs(JsonValue? member);

    /// <summary>Everything the <c>kind</c> field changes about a member, as error messages in the order they are found.</summary>
    IReadOnlyList<string> KindFieldErrors(JsonValue? member, JsonObject? resolved = null);

    /// <summary>
    /// Warnings, not errors, for a non-claude member: a chain member whose auto-mode leaves it a read-only sandbox, and (with
    /// <paramref name="tier"/>) an advise-class member whose model is not declared opus or fable.
    /// </summary>
    IReadOnlyList<string> KindFieldWarnings(JsonValue? member, JsonObject? resolved = null, bool tier = true);

    /// <summary>Why an advise-class member cannot be of <paramref name="kind"/>, a kind with no model mapping.</summary>
    string UnmappedAdviseMessage(string kind);

    /// <summary>
    /// A committed team's members stripped to the config that reproduces them (role, model, effort, route, adapter, kind, args,
    /// auto_mode), sorted by role.
    /// </summary>
    JsonArray NormalizeMembers(JsonValue? members);

    /// <summary>Validation errors for one roster member object; empty means valid.</summary>
    IReadOnlyList<string> ValidateMember(JsonValue? member, JsonObject? resolved = null);

    /// <summary>Validation errors for one team-file member object (which does carry a <c>name</c>); empty means valid.</summary>
    IReadOnlyList<string> ValidateTeamMember(JsonValue? member, JsonObject? resolved = null);

    /// <summary>Validation errors for a whole <c>roster</c> block (<c>{route, members}</c>); empty means valid.</summary>
    IReadOnlyList<string> ValidateRosterBlock(JsonValue? roster, JsonObject? resolved = null);
}

/// <summary>
/// Reading a harness's terminal screen: whether it is an idle, empty composer, which prompt it ends with, and which answers may be
/// relayed. Pure functions of the screen text and the harness tables.
/// </summary>
public interface IScreenRules
{
    /// <summary>Whether the screen is the kind's idle, empty composer (see the harness table for the layout).</summary>
    bool IsComposer(string kind, string? screen);

    /// <summary>What a Claude Code screen shows: <c>prompt</c> with its option <c>block</c>, <c>composer</c>, <c>draft</c>, <c>busy</c>, <c>recognized</c>.</summary>
    JsonObject RecognizeClaude(string? screen);

    /// <summary>What a kind's screen shows, given Herdr's status for it: <c>composer</c>, <c>prompt</c> and its option <c>block</c>.</summary>
    JsonObject RecognizeScreen(string kind, string? screen, string? agentStatus);

    /// <summary>The answers to offer for a prompt while its option block is <paramref name="block"/>, as <c>{id, label, description, grants}</c>.</summary>
    JsonArray PromptOptions(string kind, string blockedBy, JsonValue? block, JsonValue? context);

    /// <summary>The prompts a kind relays answers for, in table order; empty for a kind with none.</summary>
    IReadOnlyList<string> RelayedPrompts(string kind);

    /// <summary>Every answer row of a prompt (offered on a screen or not), or null when the kind has none for that prompt.</summary>
    IReadOnlyList<PromptRow>? PromptRows(string kind, string blockedBy);

    /// <summary>Whether the answer row <paramref name="id"/> of a prompt is one the screen's option <paramref name="block"/> offers.</summary>
    bool RowOffered(string kind, string blockedBy, string id, JsonValue? block);

    /// <summary>SHA-256 hex of a screen with trailing whitespace cut, a countdown's time masked and trailing blank lines dropped.</summary>
    string ScreenHash(string? screen);
}

/// <summary>One answer a prompt can be given: its id, the keys sent to the pane when it is chosen, and whether it grants the member something (a Yes or an Allow).</summary>
public sealed record PromptRow(string Id, IReadOnlyList<string> Keys, bool Grants);

/// <summary>What Codex's own configuration says about trusting a directory; read before a launch and never written.</summary>
public interface ICodexTrust
{
    /// <summary>Where Codex saves a trust answer given in <paramref name="cwd"/>: the checkout's root, else <paramref name="cwd"/>.</summary>
    string TrustRoot(string cwd);

    /// <summary><c>{verdict, configPath, codexHome, trustRoot}</c>, verdict <c>trusted</c>, <c>untrusted</c> or <c>unknown</c>.</summary>
    JsonObject Check(string cwd);

    /// <summary>The edit that would make the config trust the trust root of <paramref name="cwd"/>: <c>{state, configPath, codexHome, trustRoot, key, edit, newText}</c>.</summary>
    JsonObject PlanTrust(string cwd);
}

/// <summary>
/// Whether a Codex trust screen may be answered for the user: the screen must be exactly Codex's trust screen, name the trust root of the
/// launch directory, and be covered by a pre-approval the user recorded in <see cref="StorePath"/>.
/// </summary>
public interface ICodexTrustAutoAnswer
{
    /// <summary>Where the user's pre-approvals are recorded: the <c>meta</c> row's key in the state database.</summary>
    string StorePath { get; }

    /// <summary>Where every use of the auto-answer is logged, one JSON row per line.</summary>
    string LogPath { get; }

    /// <summary>The folder and effective root a Codex trust screen names, as <c>{path, root}</c>, or null when it is not exactly that screen.</summary>
    JsonObject? Match(string screen);

    /// <summary>The recorded scopes. A missing row is none; a row that is not a store, or a database that cannot answer, is described in <paramref name="problem"/> and gives none.</summary>
    List<JsonObject> ReadScopes(out string? problem);

    /// <summary>The scope that covers <paramref name="root"/> (a realpath), or null.</summary>
    JsonObject? Covering(IEnumerable<JsonObject> scopes, string root);

    /// <summary><c>{answer, reason, root?, scope?}</c> for <paramref name="screen"/> shown to a member launched in <paramref name="launchCwd"/>.</summary>
    JsonObject Decide(string screen, string launchCwd);

    /// <summary>
    /// The one way a trust screen is answered under a pre-approval, after <see cref="Decide"/> said yes: <paramref name="sendEnter"/> sends
    /// the single Enter, <paramref name="composerUp"/> says whether Codex then shows its composer, and the answer counts only if Codex
    /// also saved the trust. No second key is ever sent. The attempt is logged to <see cref="LogPath"/> whatever its result. Returns
    /// <c>{root, scope, result, note}</c>, result <c>answered</c>, <c>send-failed</c> or <c>verify-failed</c>.
    /// </summary>
    JsonObject Answer(JsonObject decision, string member, JsonValue team, JsonValue screenHash, string launchCwd, Func<bool> sendEnter, Func<bool> composerUp);
}

/// <summary>The member schema's value lists.</summary>
internal static class RosterValues
{
    /// <summary>How the Orchestrator reaches a member.</summary>
    internal static IReadOnlyList<string> RouteValues { get; } = ["peer", "subagent", "pane", "mail"];

    internal static IReadOnlyList<string> EffortValues { get; } = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Permission modes, the recommended one first; <c>bypassPermissions</c> is accepted only for old configs.</summary>
    internal static IReadOnlyList<string> AutoModeValues { get; } = ["auto", "acceptEdits", "plan", "dontAsk", "manual", "bypassPermissions"];

    internal static IReadOnlyList<string> OnMissingValues { get; } = ["auto"];

    internal const string OnMissingDefault = "auto";

    /// <summary>The tiers an advise-class member's model must be declared at.</summary>
    internal static IReadOnlyList<string> AdviseTiers { get; } = ["opus", "fable"];

    /// <summary>Snapshot files kept per team.</summary>
    internal const int SnapshotKeep = 10;

    /// <summary>The age past which a team is not live by age alone, in seconds.</summary>
    internal const int TeamStaleAgeSeconds = 24 * 3600;

    /// <summary>How long after its <c>spawned_at</c> a member row without a live registration is still starting rather than dead, in seconds.</summary>
    internal const int StartingWindowSeconds = 120;

    /// <summary>
    /// Per kind, a Claude <c>--permission-mode</c> as that kind's own flags, in the order its table lists them.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>>> KindAutoModeArgs { get; } =
        new Dictionary<string, IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>>>(StringComparer.Ordinal)
        {
            ["codex"] =
            [
                new("manual", ["--sandbox", "read-only", "--ask-for-approval", "on-request"]),
                new("plan", ["--sandbox", "read-only", "--ask-for-approval", "never"]),
                new("acceptEdits", ["--sandbox", "workspace-write", "--ask-for-approval", "on-request"]),
                new("auto", ["--sandbox", "workspace-write", "--ask-for-approval", "never"]),
                new("dontAsk", ["--sandbox", "workspace-write", "--ask-for-approval", "never"]),
                new("bypassPermissions", ["--dangerously-bypass-approvals-and-sandbox"]),
            ],
        };
}
