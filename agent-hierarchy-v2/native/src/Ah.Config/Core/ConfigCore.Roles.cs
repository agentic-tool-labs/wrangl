using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>The role registry's constants and pure helpers, plus the CLI strings that depend on where the plugin is installed.</summary>
internal sealed partial class ConfigCore
{
    // ---------------------------------------------------------------- constants

    public IReadOnlyList<string> Roles => ConfigRegistry.Roles;

    public IReadOnlyList<string> MsgRoles => ["orchestrator", "chief", .. ConfigRegistry.Roles];

    public IReadOnlyList<string> ClassNames => [.. ConfigRegistry.ClassNames];

    public IReadOnlyList<string> PeerEligibleRoles => ConfigRegistry.PeerEligibleRoles;

    public IReadOnlyList<string> ReasoningModels => ConfigRegistry.ReasoningModels;

    public IReadOnlyList<string> TopTierModels => ConfigRegistry.TopTierModels;

    public IReadOnlyList<string> HandoffModes => ConfigRegistry.HandoffModes;

    public IReadOnlyList<string> MsgsModes => ConfigRegistry.MsgsModes;

    public IReadOnlyList<string> RouteValues => ConfigRegistry.RouteValues;

    public IReadOnlyList<string> StaleRouteValues => ConfigRegistry.StaleRouteValues;

    public IReadOnlyList<string> DispatchModes => ConfigRegistry.DispatchModes;

    public IReadOnlyList<string> RosterLevels => ConfigRegistry.RosterLevels;

    public IReadOnlyList<string> BuiltinHarnesses => ConfigRegistry.BuiltinHarnesses;

    public IReadOnlyList<string> TestHarnesses => ConfigRegistry.TestHarnesses;

    public int ConfigVersion => ConfigRegistry.ConfigVersion;

    public string ConfigBasename => ConfigRegistry.ConfigBasename;

    public string KindDefault => ConfigRegistry.KindDefault;

    public string KindCustom => ConfigRegistry.KindCustom;

    public string CodexAppserverHarness => ConfigRegistry.CodexAppserverHarness;

    public string RouteMail => ConfigRegistry.RouteMail;

    public JsonObject Classes => ConfigRegistry.ClassesJson();

    public JsonObject RoleLabels => ConfigRegistry.RoleLabelsJson();

    public JsonObject RoleDefaults => ConfigRegistry.RoleDefaultsJson();

    public JsonObject ValidModelsByRole => ConfigRegistry.ValidModelsByRoleJson();

    // ---------------------------------------------------------------- the plugin root

    public string PluginRoot =>
        (environment.Plugin ?? throw new InvalidOperationException("the plugin location is not known")).Root;

    public string PluginName => environment.PluginName;

    public string Qualified(string name) => environment.Qualified(name);

    public string? BuiltinRoleOf(string? agentType) =>
        environment.LocalName(agentType) is { } local && ConfigRegistry.IsBuiltinRole(local) && ConfigRegistry.BuiltinClass(local) != ConfigRegistry.OrchestrateClass ? local : null;

    public string McpToolPrefix => PackAgentText.McpToolPrefix;

    /// <summary>The path of an ah binary as agents are told to type it and as permission rules spell it; the one place that form is built.</summary>
    public string AhCommandPath(string program = "ah", string? root = null) =>
        program == "ah" && root is null && StableAhPluginDir() is not null ? StableAhPath : NodePath.Join(root ?? PluginRoot, "bin", program);

    public string StableAhPath => NodePath.Join(Home, ".local", "bin", "ah");

    private string? _stableDir;
    private bool _stableDirKnown;

    /// <summary>
    /// The plugin directory the stable link leads to, or null. The link must be a symbolic link that resolves to <c>bin/ah</c> of a
    /// directory, and that directory must be the one this state home's install stamp names as its plugin root; a link to any other
    /// directory, even another install of this plugin, is not used.
    /// </summary>
    public string? StableAhPluginDir()
    {
        if (_stableDirKnown)
        {
            return _stableDir;
        }

        _stableDirKnown = true;
        try
        {
            if (files.Stat(StableAhPath, false)?.Kind != FileKind.Symlink)
            {
                return null;
            }

            var real = files.RealPath(StableAhPath);
            if (NodePath.Basename(real) != "ah" || NodePath.Basename(NodePath.Dirname(real)) != "bin")
            {
                return null;
            }

            var dir = NodePath.Dirname(NodePath.Dirname(real));
            var stamp = NodePath.Join(environment.AhHome, "install.json");
            if (!files.Exists(stamp) || !JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(files.ReadText(stamp)), out var doc) || !doc.IsMap)
            {
                return null;
            }

            if (doc.Member("plugin_root") is not { IsText: true } named || named.AsString().Length == 0 || files.RealPath(named.AsString()) != dir)
            {
                return null;
            }

            _stableDir = dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _stableDir = null;
        }

        return _stableDir;
    }

    public string CliCommand(CliTool tool, bool alwaysQuote = false)
    {
        var program = tool == CliTool.AhdCtl ? "ahd" : "ah";
        var path = AhCommandPath(program);
        var exe = alwaysQuote ? $"\"{path}\"" : CliToken(path);
        return tool switch
        {
            CliTool.Roster => exe + " roster",
            CliTool.Msg => exe + " msg",
            CliTool.Gate => exe + " gate",
            _ => exe,
        };
    }

    // ---------------------------------------------------------------- pure helpers

    public bool IsKindName(JsonValue? value) => ConfigRegistry.IsKindName(value);

    public bool IsHarnessName(JsonValue? value) => ConfigRegistry.IsHarnessName(value);

    public bool IsSelfCareToken(JsonValue? value) => ConfigRegistry.IsSelfCareToken(value);

    public bool IsRoleName(JsonValue? value) => ConfigRegistry.IsRoleName(value);

    public bool IsAgentRef(JsonValue? value) => ConfigRegistry.IsAgentRef(value);

    public bool IsLabel(JsonValue? value) => ConfigRegistry.IsLabel(value);

    /// <summary>A harness that needs no admission: ah ships its adapter; a test-only name counts only in test mode.</summary>
    public bool IsBuiltinHarness(JsonValue? harness)
    {
        if (harness is not { IsText: true } h)
        {
            return false;
        }

        var name = h.AsString();
        return ConfigRegistry.BuiltinHarnesses.Contains(name)
            && (!ConfigRegistry.TestHarnesses.Contains(name) || environment.TestMode);
    }

    /// <summary><c>m &amp;&amp; m.kind</c>, with <c>undefined</c> and <c>null</c> both meaning the default kind.</summary>
    public JsonValue ResolveKind(JsonValue? member) => ConfigRegistry.ResolveKind(member);

    public bool RouteHasPane(JsonValue? route) => route is { IsText: true } r && r.AsString() is "peer" or "pane";

    public bool IsPaneMember(JsonValue? member) => ConfigRegistry.IsPaneMember(member);

    public string PaneSendText(JsonValue? member, JsonValue? reqPath, JsonValue? cwd)
    {
        var name = member?.Member("name");
        var req = reqPath is { IsTruthy: true } rp ? JsConvert.ToJsString(rp) : "<request path>";
        return $"ah: `{JsConvert.ToJsString(name)}` runs in `{JsConvert.ToJsString(ResolveKind(member))}` and cannot receive SendMessage. " +
               $"Brief it with `{CliCommand(CliTool.Roster, alwaysQuote: true)} deliver {JsConvert.ToJsString(name)} --req {req} --cwd {JsConvert.ToJsString(cwd)}`, run in the background.";
    }

    public JsonObject ValidateHerdrName(JsonValue? name) => ConfigRegistry.ValidateHerdrName(name);

    public int? TierOf(JsonValue? model) => ConfigRegistry.TierOf(model);

    public bool IsBuiltinRole(string? role) => ConfigRegistry.IsBuiltinRole(role);

    public string? RoleClass(string role, JsonObject? resolved) => ConfigRegistry.RoleClass(role, resolved);

    /// <summary>
    /// One class property of a role (see <see cref="Classes"/>), or null (<c>undefined</c>) when the role is unknown. The
    /// <c>chain</c> property is the role's own peer eligibility, which a built-in marked subagent-only does not share with its class.
    /// </summary>
    public JsonValue? ClassProp(string role, JsonObject? resolved, string prop) =>
        ConfigRegistry.RoleClass(role, resolved) is not { } cls ? null
        : prop == "chain" ? ConfigRegistry.IsPeerEligible(role, resolved)
        : ConfigRegistry.ClassesJson().Get(cls)?.Member(prop);

    public IReadOnlyList<string> CustomRoleNames(JsonObject? resolved) => ConfigRegistry.CustomRoleNames(resolved);

    public IReadOnlyList<string> RegistryRoles(JsonObject? resolved) => ConfigRegistry.RegistryRoles(resolved);

    public IReadOnlyList<string> ChainRoles(JsonObject? resolved) => ConfigRegistry.ChainRoles(resolved);

    public IReadOnlyList<string> JoinableRoles(JsonObject? resolved) => ConfigRegistry.JoinableRoles(resolved);

    /// <summary>A built-in's label, else the entry's <c>label</c> when truthy, else the role name.</summary>
    public JsonValue RoleLabel(string role, JsonObject? resolved)
    {
        if (ConfigRegistry.IsBuiltinRole(role))
        {
            return ConfigRegistry.BuiltinLabel(role);
        }

        var label = resolved?.Get("roles")?.Member(role)?.Member("label");
        return label is { IsTruthy: true } l ? l : role;
    }

    public string RoleAgent(string role, JsonValue? entry) => ConfigRegistry.RoleAgent(role, entry, PluginName);

    public bool IsOverride(string role, JsonValue? entry) =>
        ConfigRegistry.IsBuiltinRole(role) && ConfigRegistry.RoleAgent(role, entry, PluginName) != Qualified(ConfigRegistry.DefaultAgentName(role));

    public string? ClassBuiltin(string? cls) => ConfigRegistry.Class(cls)?.Builtin;

    public string PeerName(string repoBasename, string role) => ConfigRegistry.PeerName(repoBasename, role);

    public string? CustomRoleFromSuffix(string? name, JsonObject? resolved) => ConfigRegistry.CustomRoleFromSuffix(name, resolved);

    /// <summary><c>{prefix, role}</c> for a name of the form <c>&lt;prefix&gt;-&lt;role&gt;[-n]</c>, else null.</summary>
    public JsonObject? HierarchyNameParts(string? name, JsonObject? resolved = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var end = name.Length;
        var digits = end;
        while (digits > 0 && name[digits - 1] is >= '0' and <= '9')
        {
            digits--;
        }

        var baseName = digits < end && digits > 0 && name[digits - 1] == '-' ? name[..(digits - 1)] : name;
        var role = ConfigRegistry.CustomRoleFromSuffix(name, resolved)
                   ?? ConfigRegistry.HierarchyNameRoles.FirstOrDefault(r => baseName.EndsWith("-" + r, StringComparison.Ordinal));
        if (role is null)
        {
            return null;
        }

        var prefix = baseName[..^(role.Length + 1)];
        return prefix.Length > 0 ? new JsonObject { ["prefix"] = prefix, ["role"] = role } : null;
    }

    public JsonArray ResolvedPeerTargets(string role, JsonValue? entry, string repoBasename)
    {
        var result = new JsonArray();
        var cls = ConfigRegistry.IsBuiltinRole(role)
            ? ConfigRegistry.BuiltinClass(role)
            : entry is { IsTruthy: true } e && e.Member("class") is { IsText: true } c ? c.AsString() : null;
        if (ConfigRegistry.Class(cls) is not { Chain: true })
        {
            return result;
        }

        if (entry is not { IsTruthy: true } ent || (ent.Member("dispatch") is { IsText: true } d && d.AsString() == "model"))
        {
            return result;
        }

        var peer = ent.Member("peer");
        if (peer is { IsArray: true } list)
        {
            foreach (var p in list.AsArray())
            {
                if (p.IsText && JsText.Trim(p.AsString()).Length > 0)
                {
                    result.Add(p);
                }
            }

            return result;
        }

        if (peer is { IsTruthy: true } single && !(single.IsText && single.AsString() == "auto"))
        {
            result.Add(single);
            return result;
        }

        result.Add(ConfigRegistry.PeerName(repoBasename, role));
        return result;
    }

    public JsonValue ResolvedPeerTarget(string role, JsonValue? entry, string repoBasename)
    {
        var targets = ResolvedPeerTargets(role, entry, repoBasename);
        return targets.Count > 0 ? targets[0] : JsonValue.Null;
    }

    /// <summary>The first roster member for <paramref name="role"/>, in roster order, or <c>null</c>.</summary>
    public JsonValue RosterMemberFor(JsonObject? resolved, string role)
    {
        if (resolved?.Get("roster")?.Member("members") is { IsArray: true } members)
        {
            foreach (var m in members.AsArray())
            {
                if (m.IsTruthy && m.Member("role") is { IsText: true } r && r.AsString() == role)
                {
                    return m;
                }
            }
        }

        return JsonValue.Null;
    }

    // ---------------------------------------------------------------- the command lines roles run

    private static string CliToken(string path) => ConfigRegistry.IsSelfCareToken(path) ? path : $"\"{path}\"";

    private static JsonValue? Option(JsonValue? options, string key)
    {
        if (options is null)
        {
            return null;
        }

        if (options.Value.IsNull)
        {
            throw new ArgumentException("options is null", nameof(options));
        }

        return options.Value.Member(key);
    }

    private static string OptionText(JsonValue? options, string key, string fallback) =>
        Option(options, key) is { } v ? JsConvert.ToJsString(v) : fallback;

    /// <summary><c>&lt;plugin&gt;/bin/ah msg new --type response ...</c>: the one line a role runs to write its response file.</summary>
    public string ResponseCommand(JsonValue? options = null)
    {
        var id = OptionText(options, "id", "<id>");
        var role = OptionText(options, "role", "<your role>");
        var req = OptionText(options, "req", "<your request path>");
        var cwd = Option(options, "cwd") is { } c ? c : (JsonValue?)string.Empty;
        return $"{CliCommand(CliTool.Msg)} new --type response --id {id} --from {role} --req {req}{(cwd!.Value.IsTruthy ? " --cwd " + JsConvert.ToJsString(cwd) : string.Empty)}";
    }

    /// <summary><c>&lt;plugin&gt;/bin/ah msg fill ...</c>: the line that puts a whole report into the response file.</summary>
    public string FillCommand(JsonValue? options = null)
    {
        var id = OptionText(options, "id", "<id>");
        var role = OptionText(options, "role", "<your role>");
        var req = OptionText(options, "req", "<your request path>");
        var cwd = Option(options, "cwd") is { } c ? c : (JsonValue?)string.Empty;
        return $"{CliCommand(CliTool.Msg)} fill --id {id} --from {role} --req {req} --body '<json>'{(cwd!.Value.IsTruthy ? " --cwd " + JsConvert.ToJsString(cwd) : string.Empty)}";
    }
}
