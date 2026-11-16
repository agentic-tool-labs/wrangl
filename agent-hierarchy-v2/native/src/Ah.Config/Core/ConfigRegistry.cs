using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>One role class (<c>CLASSES</c> in Node): the one place class behaviour lives.</summary>
internal sealed record ClassSpec(
    string Name,
    string Builtin,
    string? Step,
    IReadOnlyList<string> Models,
    bool Chain,
    bool Tier,
    bool OwesResponse,
    bool UltraGate,
    bool RoleDispatchable);

/// <summary>The built-in role rows, the class table and the pure naming rules of the role registry.</summary>
internal static partial class ConfigRegistry
{
    internal const string ConfigBasename = "agent-hierarchy.json";
    internal const int ConfigVersion = 1;
    internal const string DefaultRoster = "default";
    internal const string KindDefault = "claude";
    internal const string OrchestrateClass = "orchestrate";
    internal const int OneLineMax = 160;

    internal static readonly string[] ReasoningModels = ["opus", "sonnet", "fable", "inherit"];
    internal static readonly string[] TopTierModels = ["fable", "opus"];
    internal static readonly string[] HandoffModes = ["auto", "confirm"];
    internal static readonly string[] MsgsModes = ["required", "off"];
    internal static readonly string[] RouteValues = ["peers"];
    internal static readonly string[] StaleRouteValues = ["subagents", "prefer-peers"];
    internal static readonly string[] DispatchModes = ["peer", "model"];
    internal static readonly string[] RosterLevels = ["repo-user", "repo", "global"];
    internal static readonly string[] RosterLayoutValues = ["auto", "columns", "grid"];
    internal static readonly string[] BuiltinFixedKeys = ["class", "label", "description", "routes"];
    internal static readonly string[] ReservedRoleNames = ["orchestrator", "advisor", "other", "chief"];
    internal static readonly string[] PackRoleFields = ["class", "agent", "label", "description", "routes", "model", "dispatch"];

    internal static readonly IReadOnlyList<ClassSpec> Classes =
    [
        new("advise", "ultra-advisor", "Escalate", TopTierModels, true, true, false, true, false),
        new("design", "architect", "Design", ReasoningModels, true, true, true, false, false),
        new("review", "reviewer", "Review", ReasoningModels, true, false, false, false, false),
        new("implement", "implementor", "Implement", ReasoningModels, true, false, true, false, false),
        new("legwork", "task-runner", null, [.. ReasoningModels, "haiku"], false, false, false, false, true),
        new(OrchestrateClass, "sub-orchestrator", "Orchestrate", ReasoningModels, true, false, true, false, false),
    ];

    /// <summary>
    /// The built-in roles in display order. <c>Model</c> is the model a subagent dispatch runs on; <c>MemberModel</c> is the model
    /// a peer member of the role launches on without being asked, null for a role whose members are asked at spawn.
    /// <c>Since</c> is the plugin version that first shipped the role built in, null for a role that was built in before users
    /// could define roles of their own: only a role with a version can supersede a custom row a user stored under its name.
    /// </summary>
    private static readonly (string Name, string Class, string Label, string Model, string? Delegate, string? Routes, string? MemberModel, string? Since, bool SubagentOnly)[] BuiltinRows =
    [
        ("ultra-advisor", "advise", "Ultra-Advisor", "fable", null, null, null, null, false),
        ("architect", "design", "Architect", "opus", null, null, null, null, false),
        ("reviewer", "review", "Reviewer", "opus", null, null, null, null, false),
        ("implementor", "implement", "Implementor", "inherit", null, null, null, null, false),
        ("docs-writer", "implement", "Docs-Writer", "sonnet", null, "Standalone documentation outside of code: READMEs, docs/, guides, changelogs", "sonnet", "2.3.0", false),
        ("qa", "implement", "QA", "sonnet", null, null, null, "2.5.0", false),
        ("ux-engineer", "implement", "UX-Engineer", "opus", null, "Web UI work: CSS, markup, visual tokens, layout, interaction and accessibility under web/ (not daemon, data or protocol work)", "opus", "2.5.0", false),
        ("task-runner", "legwork", "Task-Runner", "haiku", "task-gopher", null, null, null, false),
        ("backlog-groomer", "legwork", "Backlog-Groomer", "sonnet", null, "Grooming the project backlog through ah backlog: triage, ranking, dedupe, proven-done items and the groom record", null, "2.5.0", true),
        ("sub-orchestrator", "orchestrate", "Sub-Orchestrator", "sonnet", null, null, "sonnet", "2.5.0", false),
        (DeputyChiefRole, "orchestrate", "Deputy-Chief", "sonnet", null, null, "sonnet", "2.5.0", false),
    ];

    /// <summary>The built-in role that runs a top team's protocol while it is on duty (<c>ah roster duty on</c>); the chief stays the recorded owner.</summary>
    internal const string DeputyChiefRole = "deputy-chief";

    /// <summary>The built-in roles in display order.</summary>
    internal static readonly string[] Roles = [.. BuiltinRows.Select(r => r.Name)];

    internal static readonly string[] PeerEligibleRoles = [.. BuiltinRows.Where(r => Class(r.Class)!.Chain && !r.SubagentOnly).Select(r => r.Name)];

    /// <summary>
    /// Whether <paramref name="role"/> can run as a peer session: its class is a chain class and it is not a built-in marked
    /// subagent-only. The one fact behind <see cref="PeerEligibleRoles"/>, <see cref="ChainRoles"/> and the <c>chain</c> property of a role.
    /// </summary>
    internal static bool IsPeerEligible(string role, JsonObject? resolved) =>
        ClassOfRole(role, resolved)?.Chain == true && !IsSubagentOnly(role);

    internal static bool IsSubagentOnly(string role) => BuiltinRows.Any(r => r.Name == role && r.SubagentOnly);

    /// <summary>The name tokens a session name is matched against, in order: each chain built-in's own name, then the <c>advisor</c> alias.</summary>
    private static readonly (string Token, string Role)[] RoleTokens =
    [
        .. PeerEligibleRoles.Select(r => (r, r)),
        ("advisor", "ultra-advisor"),
    ];

    internal static IEnumerable<string> ClassNames => Classes.Select(c => c.Name);

    [GeneratedRegex("^" + ConfigModule.RoleNamePattern + "\\z", RegexOptions.CultureInvariant)]
    internal static partial Regex RoleNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}(:[A-Za-z0-9][A-Za-z0-9_.-]{0,63})?\\z", RegexOptions.CultureInvariant)]
    internal static partial Regex AgentRefRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 -]{0,31}\\z", RegexOptions.CultureInvariant)]
    internal static partial Regex LabelRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TeamAliasShapeRegex();

    [GeneratedRegex("-\\z|-[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalSuffixRegex();

    [GeneratedRegex("-[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingOrdinalRegex();

    internal static ClassSpec? Class(string? name) => Classes.FirstOrDefault(c => c.Name == name);

    internal static bool IsBuiltinRole(string? role) => role is not null && BuiltinRows.Any(r => r.Name == role);

    internal static string? BuiltinClass(string role) => BuiltinRows.FirstOrDefault(r => r.Name == role).Class;

    /// <summary>The built-in's routes sentence from the table; null for a role that is not an alternative to its class default.</summary>
    internal static string? BuiltinRoutes(string role) => BuiltinRows.FirstOrDefault(r => r.Name == role).Routes;

    /// <summary>The model a member of the built-in <paramref name="role"/> launches on when nothing names one; null when members are asked.</summary>
    internal static string? MemberDefaultModel(string role) => BuiltinRows.FirstOrDefault(r => r.Name == role).MemberModel;

    /// <summary>The plugin version that first shipped the built-in <paramref name="role"/>; null for a role that was built in from the start.</summary>
    internal static string? BuiltinSince(string role) => BuiltinRows.FirstOrDefault(r => r.Name == role).Since;

    internal static JsonObject RoleDefaults(string role)
    {
        var row = BuiltinRows.First(r => r.Name == role);
        var defaults = new JsonObject { ["model"] = row.Model };
        if (row.Delegate is not null)
        {
            defaults["delegate"] = row.Delegate;
        }

        return defaults;
    }

    internal static IReadOnlyList<string> ValidModels(string role) => Class(BuiltinClass(role))!.Models;

    /// <summary>The class of a role: a built-in's from the table, a custom row's from its resolved entry; null when unknown.</summary>
    internal static string? RoleClass(string role, JsonObject? resolved)
    {
        if (IsBuiltinRole(role))
        {
            return BuiltinClass(role);
        }

        var entry = resolved?.Get("roles")?.Member(role);
        return entry?.Member("class")?.NonEmptyText is { } cls && Class(cls) is not null ? cls : null;
    }

    internal static ClassSpec? ClassOfRole(string role, JsonObject? resolved) => Class(RoleClass(role, resolved));

    /// <summary>Custom role names in the resolved registry, sorted by name (UTF-16 order).</summary>
    internal static List<string> CustomRoleNames(JsonObject? resolved)
    {
        var roles = resolved?.Get("roles");
        if (roles is null || !roles.Value.IsMap)
        {
            return [];
        }

        var names = roles.Value.AsObject().Keys.Where(r => !IsBuiltinRole(r)).ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    internal static List<string> RegistryRoles(JsonObject? resolved) => [.. Roles, .. CustomRoleNames(resolved)];

    internal static List<string> JoinableRoles(JsonObject? resolved) =>
        RegistryRoles(resolved).Where(r => !IsSubagentOnly(r)).ToList();

    internal static List<string> ChainRoles(JsonObject? resolved) =>
        RegistryRoles(resolved).Where(r => IsPeerEligible(r, resolved)).ToList();

    /// <summary><c>roleAgent</c>: the entry's <c>agent</c> when it is a non-empty string, else the built-in agent of the plugin named <paramref name="pluginName"/>.</summary>
    internal static string RoleAgent(string role, JsonValue? entry, string pluginName) =>
        entry?.Member("agent")?.NonEmptyText ?? pluginName + ":" + DefaultAgentName(role);

    /// <summary>The agent file name a built-in role launches by default: its own name, except a role of the orchestrate class, which runs the Sub-Orchestrator agent; the Deputy-Chief runs its own.</summary>
    internal static string DefaultAgentName(string role) => IsBuiltinRole(role) && BuiltinClass(role) == OrchestrateClass && role != DeputyChiefRole ? "sub-orchestrator" : role;

    internal static string PeerName(string repoBasename, string role) => Ah.Core.Text.MemberNames.Fit(repoBasename, role);

    internal static bool IsTeamAliasShape(string? name) => name is not null && TeamAliasShapeRegex().IsMatch(name);

    /// <summary><c>customRoleFromSuffix</c>: the longest custom registry role that <paramref name="name"/> ends with, as <c>-role</c> or <c>-role-N</c>.</summary>
    internal static string? CustomRoleFromSuffix(string? name, JsonObject? resolved)
    {
        if (name is null || CustomRoleNames(resolved).Count == 0)
        {
            return null;
        }

        var baseName = TrailingOrdinalRegex().Replace(name, string.Empty, 1);
        string? best = null;
        foreach (var role in RegistryRoles(resolved))
        {
            if (baseName.EndsWith("-" + role, StringComparison.Ordinal) && (best is null || role.Length > best.Length))
            {
                best = role;
            }
        }

        return best is not null && !IsBuiltinRole(best) ? best : null;
    }

    /// <summary>
    /// <c>roleFromName</c>: a suffix-anchored custom role, else the built-in role whose <c>-</c>-split segments occur as a contiguous run of
    /// the name's segments (after the trailing ordinal is dropped). The role with the most segments wins, then the one that ends later in the name.
    /// </summary>
    internal static string? RoleFromName(string? name, JsonObject? resolved = null)
    {
        if (name is null)
        {
            return null;
        }

        var custom = CustomRoleFromSuffix(name, resolved);
        if (custom is not null)
        {
            return custom;
        }

        var segments = TrailingOrdinalRegex().Replace(name, string.Empty, 1).Split('-');
        string? best = null;
        var bestLength = 0;
        var bestEnd = -1;
        foreach (var (token, role) in RoleTokens)
        {
            var parts = token.Split('-');
            for (var start = segments.Length - parts.Length; start >= 0; start--)
            {
                if (!segments.AsSpan(start, parts.Length).SequenceEqual(parts))
                {
                    continue;
                }

                var end = start + parts.Length;
                if (parts.Length > bestLength || (parts.Length == bestLength && end > bestEnd))
                {
                    best = role;
                    bestLength = parts.Length;
                    bestEnd = end;
                }

                break;
            }
        }

        return best;
    }

    /// <summary>Role tokens too short to match inside a word (a two-letter token would refuse <c>squad</c>-style aliases): they match only as a whole <c>-</c> segment run, in an alias check.</summary>
    private static readonly HashSet<string> SegmentOnlyAliasTokens = new(StringComparer.Ordinal) { "qa" };

    /// <summary>
    /// The strict resolution an alias is checked with: could a peer name derived from the alias be taken for another role? A suffix-anchored custom
    /// role, else the first role token in table order that the name contains as a substring, except a segment-only token, which must be a whole
    /// <c>-</c> segment run. Looser than <see cref="RoleFromName"/>, which resolves a session's role and matches whole segments only.
    /// </summary>
    internal static string? AliasRoleFromName(string name, JsonObject? resolved)
    {
        if (CustomRoleFromSuffix(name, resolved) is { } custom)
        {
            return custom;
        }

        var segments = TrailingOrdinalRegex().Replace(name, string.Empty, 1).Split('-');
        foreach (var (token, role) in RoleTokens)
        {
            var matches = SegmentOnlyAliasTokens.Contains(token)
                ? Enumerable.Range(0, segments.Length).Any(start => segments.AsSpan(start).StartsWith(token.Split('-')))
                : name.Contains(token, StringComparison.Ordinal);
            if (matches)
            {
                return role;
            }
        }

        return null;
    }

    /// <summary><c>validateTeamAlias</c>: <c>{ok: true}</c> or <c>{ok: false, why}</c>.</summary>
    internal static JsonObject ValidateTeamAlias(JsonValue alias, JsonObject? resolved = null)
    {
        if (!alias.IsText || alias.AsString().Length == 0)
        {
            return Fail("alias must be a non-empty string");
        }

        var name = alias.AsString();
        if (!IsTeamAliasShape(name))
        {
            return Fail("alias must start with a letter or digit, be 1-32 characters, and contain only letters, digits, and -");
        }

        var collides = ChainRoles(resolved).FirstOrDefault(role => AliasRoleFromName(PeerName(name, role), resolved) != role);
        if (collides is not null)
        {
            return Fail($"alias collides with role-token matching: a \"{collides}\" peer name derived from it would not resolve back to role \"{collides}\"");
        }

        return new JsonObject { ["ok"] = true };

        static JsonObject Fail(string why) => new() { ["ok"] = false, ["why"] = why };
    }

    internal static bool IsValidTeamAlias(JsonValue alias) => ValidateTeamAlias(alias).Get("ok")!.Value.AsBool();

    internal static string? RoleNameError(JsonValue name)
    {
        if (!name.IsText || !RoleNameRegex().IsMatch(name.AsString()))
        {
            return "name must match /^[a-z][a-z0-9-]{0,30}$/";
        }

        var s = name.AsString();
        if (OrdinalSuffixRegex().IsMatch(s))
        {
            return "name must not end in - or -<digits> (that suffix is a peer instance ordinal)";
        }

        if (IsBuiltinRole(s) || ReservedRoleNames.Contains(s))
        {
            return $"name {JsonText.Stringify(s, 0)} is reserved";
        }

        return null;
    }

    internal static string? AgentRefError(JsonValue? agent)
    {
        if (agent is not { IsText: true } text || !AgentRefRegex().IsMatch(text.AsString()))
        {
            return $"agent {JsStringify(agent)} must match /^[A-Za-z0-9][A-Za-z0-9_.-]{{0,63}}(:[A-Za-z0-9][A-Za-z0-9_.-]{{0,63}})?$/";
        }

        return null;
    }

    internal static string? OneLineError(string field, JsonValue value)
    {
        if (!value.IsText || JsText.Trim(value.AsString()).Length == 0)
        {
            return $"{field} must be a non-empty string";
        }

        var s = value.AsString();
        if (s.Length > OneLineMax)
        {
            return $"{field} must be at most {OneLineMax} characters";
        }

        if (s.AsSpan().IndexOfAny('\n', '\r', '`') >= 0)
        {
            return $"{field} must be one line with no backticks";
        }

        return null;
    }

    /// <summary><c>ui-implementor</c> becomes <c>Ui-Implementor</c>.</summary>
    internal static string DefaultLabel(string name) =>
        string.Join('-', name.Split('-').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    /// <summary><c>JSON.stringify(value)</c> for a value that may be absent: <c>undefined</c> is text.</summary>
    internal static string JsStringify(JsonValue? value) => value is null ? "undefined" : JsonText.Stringify(value.Value, 0);

    internal static string JsStringify(JsonValue value) => JsonText.Stringify(value, 0);
}
