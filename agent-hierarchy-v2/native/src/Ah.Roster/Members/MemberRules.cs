using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

internal sealed class MemberRules(IMemberRegistry registry) : IMemberRules
{
    private const string KindShapeSource = "^[a-z][a-z0-9-]*$";
    private const string HarnessNameSource = "^[a-z][a-z0-9-]{1,31}$";

    // ---------------------------------------------------------------- helpers

    private static string J(JsonValue? v) => v is null ? "undefined" : JsonText.Stringify(v.Value, 0);

    private static string S(JsonValue? v) => JsConvert.ToJsString(v);

    /// <summary>A member as the validators read it: an object, or an array, which has none of the fields.</summary>
    private static JsonObject? Fields(JsonValue? member) => member switch
    {
        { IsMap: true } m => m.AsObject(),
        { IsArray: true } => new JsonObject(),
        _ => null,
    };

    /// <summary>The field <paramref name="name"/> when it is present and not null.</summary>
    private static JsonValue? Set(JsonObject m, string name) => m.Get(name) is { IsNull: false } v ? v : (JsonValue?)null;

    private static bool IsPresent(JsonValue? v) => v is not null && !v.Value.IsNull;

    private static bool Is(JsonValue? v, string text) => v is { IsText: true } t && t.AsString() == text;

    private static bool IsString(JsonValue? v) => v is { IsText: true };

    private static bool IsNonEmptyString(JsonValue? v) => v is { IsText: true } t && t.AsString().Length > 0;

    /// <summary>The refusal for a role whose class is a chain class but which cannot be a peer, or null.</summary>
    private string? SubagentOnlyError(JsonValue? role, JsonObject? resolved) =>
        role is { IsText: true } r && registry.RoleClass(r.AsString(), resolved) is { } cls && registry.ClassIsChain(cls) && !registry.IsPeerEligible(r.AsString(), resolved)
            ? $"role {J(role)} is subagent-only: it runs as an Agent dispatch and cannot be a roster member"
            : null;

    private string? RoleClassOf(JsonObject m, JsonObject? resolved) => registry.RoleClass(S(m.Get("role")), resolved);

    // ---------------------------------------------------------------- kind and route

    /// <summary>A member's effective kind: an absent or null kind is the default.</summary>
    private JsonValue ResolveKind(JsonObject m) => Set(m, "kind") ?? registry.KindDefault;

    private static bool RouteHasPane(JsonValue? route) => Is(route, "peer") || Is(route, "pane");

        public JsonArray? MemberArgs(JsonValue? member)
    {
        var a = Fields(member)?.Get("args");
        return a is { IsArray: true } list && list.AsArray().Count > 0 ? list.AsArray() : null;
    }

        public IReadOnlyList<string>? KindAutoModeArgs(JsonValue? member)
    {
        if (Fields(member) is not { } m || m.Get("autoMode") is not { IsTruthy: true } mode)
        {
            return null;
        }

        var kind = S(ResolveKind(m));
        if (!RosterValues.KindAutoModeArgs.TryGetValue(kind, out var table))
        {
            return null;
        }

        var key = S(mode);
        foreach (var (name, args) in table)
        {
            if (name == key)
            {
                return [.. args];
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- kind fields

        public IReadOnlyList<string> KindFieldErrors(JsonValue? member, JsonObject? resolved = null)
    {
        var errors = new List<string>();
        if (Fields(member) is not { } m)
        {
            return errors;
        }

        var kindField = Set(m, "kind");
        if (kindField is not null && !(kindField.Value.IsText && registry.IsKindShape(kindField.Value.AsString())))
        {
            errors.Add($"kind must be a non-empty lowercase string matching {KindShapeSource}, got {J(kindField)}");
            return errors;
        }

        var kind = kindField is null ? registry.KindDefault : kindField.Value.AsString();
        if (kind == registry.KindCustom)
        {
            return CustomFieldErrors(m);
        }

        if (Is(m.Get("route"), registry.RouteMail))
        {
            errors.Add($"route \"{registry.RouteMail}\" is for kind \"{registry.KindCustom}\" members only, got kind {JsonText.Stringify(kind, 0)}");
        }

        var nonClaude = kind != registry.KindDefault;
        var harness = nonClaude ? KindHarness.Get(kind) : null;
        var kindJson = JsonText.Stringify(kind, 0);

        if (Set(m, "adapter") is { } adapter)
        {
            if (!Is(adapter, "appserver") && !Is(adapter, "off"))
            {
                errors.Add($"adapter must be \"appserver\" or \"off\" (or removed), got {J(adapter)}");
            }
            else if (kind != "codex")
            {
                errors.Add($"adapter {J(adapter)} applies only to kind codex, not {kindJson} — remove it");
            }
        }

        if (nonClaude)
        {
            RosterValues.KindAutoModeArgs.TryGetValue(kind, out var mapped);
            foreach (var (key, label) in new[] { ("model", "model"), ("effort", "effort"), ("autoMode", "auto-mode") })
            {
                if (Set(m, key) is not { } value)
                {
                    continue;
                }

                if (key == "autoMode" && mapped is not null)
                {
                    var known = mapped.Any(p => Is(value, p.Key));
                    if (!known && RosterValues.AutoModeValues.Any(v => Is(value, v)))
                    {
                        errors.Add($"auto-mode {J(value)} has no {kind} equivalent — use one of {string.Join(", ", mapped.Select(p => p.Key))}");
                    }

                    continue;
                }

                if (key == "model" && harness is not null)
                {
                    if (!(value.IsText && ModelShapeOk(value.AsString())))
                    {
                        errors.Add($"model must be a non-empty string with no whitespace or control characters, got {J(value)}");
                    }

                    continue;
                }

                if (key == "model")
                {
                    errors.Add($"model has no mapping for kind {kindJson} — pass that harness's own model flag in args instead (got {J(value)})");
                    continue;
                }

                errors.Add($"{label} is a Claude Code CLI flag and has no meaning for kind {kindJson} — remove it (got {J(value)})");
            }

            if (!Is(m.Get("route"), "pane"))
            {
                errors.Add($"route must be \"pane\" for kind {kindJson} (a non-claude agent is reached through its Herdr pane, not SendMessage or the Agent tool), got {J(m.Get("route"))}");
            }
        }

        if (Set(m, "args") is { } args)
        {
            if (!args.IsArray)
            {
                errors.Add($"args must be an array of strings, got {J(args)}");
            }
            else
            {
                var list = args.AsArray();
                for (var i = 0; i < list.Count; i++)
                {
                    if (!IsNonEmptyString(list[i]))
                    {
                        errors.Add($"args[{i}] must be a non-empty string, got {J(list[i])}");
                    }
                }
            }
        }

        var memberArgs = MemberArgs(m);
        if (!nonClaude && memberArgs is not null)
        {
            errors.Add($"args is not allowed for kind \"claude\" — Claude CLI flags are set with --model/--effort/--auto-mode, which are validated; args would bypass that (got {J(m.Get("args"))})");
        }

        if (harness is not null && memberArgs is not null)
        {
            if (Set(m, "model") is not null && harness.ArgsSetModel(memberArgs))
            {
                errors.Add($"model is set, and args sets the model too — one channel per setting: drop the model flag from args, or drop model (got {J(m.Get("args"))})");
            }

            if (memberArgs.Any(a => a.IsText && a.AsString().Contains(harness.ApprovalsKey, StringComparison.Ordinal)))
            {
                errors.Add($"args must not set {harness.ApprovalsKey}: every {kind} member is launched with {string.Join(' ', harness.ApprovalsArgs)}, so an escalation waits for a human, and a later setting in args would undo that (got {J(m.Get("args"))})");
            }
        }

        if (nonClaude && memberArgs is not null && RoleClassOf(m, resolved) == "advise")
        {
            errors.Add($"args is not allowed on a non-claude advise-class member ({S(m.Get("role"))}) — it could set a second model past the tier lock, or brief the member at launch without the user's approval (got {J(m.Get("args"))})");
        }

        if (nonClaude && harness is null && RoleClassOf(m, resolved) == "advise")
        {
            errors.Add(UnmappedAdviseMessage(kind));
        }

        return errors;
    }

    /// <summary><c>/^[^\s\p{Cc}]+$/u</c>: one or more characters, none of them white space or a control character.</summary>
    private static bool ModelShapeOk(string model)
    {
        if (model.Length == 0)
        {
            return false;
        }

        foreach (var c in model)
        {
            if (JsText.IsSpace(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Control)
            {
                return false;
            }
        }

        return true;
    }

    private List<string> CustomFieldErrors(JsonObject m)
    {
        var errors = new List<string>();
        var harness = m.Get("harness");
        if (!(harness is { IsText: true } h && registry.IsHarnessName(h.AsString())))
        {
            errors.Add($"harness is required for kind \"{registry.KindCustom}\" and must match {HarnessNameSource}, got {J(harness)}");
        }
        else if (registry.BuiltinHarnesses.Contains(h.AsString()))
        {
            errors.Add($"harness {J(harness)} is built in and needs no admission — choose another name for a custom harness");
        }

        if (!Is(m.Get("route"), registry.RouteMail))
        {
            errors.Add($"route must be \"{registry.RouteMail}\" for kind \"{registry.KindCustom}\", got {J(m.Get("route"))}");
        }

        if (Set(m, "command") is { } command
            && !(command.IsText && JsText.Trim(command.AsString()).Length > 0 && command.AsString().IndexOfAny(['\0', '\r', '\n']) < 0))
        {
            errors.Add($"command must be a single-line non-empty string, got {J(command)}");
        }

        foreach (var (key, label) in new[] { ("model", "model"), ("effort", "effort"), ("autoMode", "auto-mode"), ("args", "args") })
        {
            if (Set(m, key) is { } value)
            {
                errors.Add($"{label} has no meaning for kind \"{registry.KindCustom}\" — remove it (got {J(value)})");
            }
        }

        return errors;
    }

    public string UnmappedAdviseMessage(string kind) =>
        $"The Ultra-Advisor's model must have a declared opus or fable tier, and `{kind}` has no model mapping, so its model cannot be set or declared. Use claude, or a kind with a model mapping ({string.Join(", ", KindHarness.All.Select(h => h.Key))}).";

    public IReadOnlyList<string> KindFieldWarnings(JsonValue? member, JsonObject? resolved = null, bool tier = true)
    {
        var warnings = new List<string>();
        if (Fields(member) is not { } m)
        {
            return warnings;
        }

        var kindValue = ResolveKind(m);
        if (Is(kindValue, registry.KindDefault))
        {
            return warnings;
        }

        var kind = S(kindValue);
        var cls = RoleClassOf(m, resolved);
        var autoArgs = KindAutoModeArgs(m);
        if (cls is not null && registry.ClassIsChain(cls) && autoArgs is not null && autoArgs.Contains("read-only"))
        {
            warnings.Add($"auto-mode {J(m.Get("autoMode"))} runs {kind} in a read-only sandbox, so this {S(m.Get("role"))} cannot write its report file without an approval answered in its own pane");
        }

        if (tier && cls == "advise" && KindHarness.Get(kind) is not null && m.Get("model") is { IsText: true } model && model.AsString().Length > 0)
        {
            var declared = registry.DeclaredTier(kind, model.AsString());
            if (declared is null || !RosterValues.AdviseTiers.Contains(declared))
            {
                var what = declared is not null ? $"is declared {declared}" : "has no declared tier";
                warnings.Add($"{kind} model {model.AsString()} {what}; an advise-class member needs a model declared opus or fable, so spawning it will refuse. Declare it with `ah roster tier set {kind} {model.AsString()} <opus|fable>` if that is how it compares");
            }
        }

        return warnings;
    }

    public JsonArray NormalizeMembers(JsonValue? members)
    {
        var list = members is { IsArray: true } a ? a.AsArray().ToList() : [];
        static string RoleKey(JsonValue m) => m.Member("role") is { IsTruthy: true } r ? JsConvert.ToJsString(r) : string.Empty;
        var sorted = list.OrderBy(RoleKey, Comparer<string>.Create(TextCompare.CompareText)).ToList();
        var result = new JsonArray();
        foreach (var member in sorted)
        {
            var o = new JsonObject();
            var m = Fields(member);
            if (member.IsMap && m is not null)
            {
                foreach (var key in new[] { "role", "model", "effort", "route", "adapter" })
                {
                    if (Set(m, key) is { } v)
                    {
                        o[key] = v;
                    }
                }

                var kind = ResolveKind(m);
                if (!Is(kind, registry.KindDefault))
                {
                    o["kind"] = kind;
                }

                if (MemberArgs(m) is { } args)
                {
                    var copy = new JsonArray();
                    copy.AddRange(args);
                    o["args"] = copy;
                }

                var autoMode = m.Get("auto_mode") ?? m.Get("autoMode");
                if (autoMode is { IsNull: false } mode)
                {
                    o["auto_mode"] = mode;
                }
            }

            result.Add(o);
        }

        return result;
    }

    // ---------------------------------------------------------------- whole members

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    public IReadOnlyList<string> ValidateMember(JsonValue? member, JsonObject? resolved = null)
    {
        if (Fields(member) is not { } m)
        {
            return ["member must be an object"];
        }

        var errors = new List<string>();
        var roles = registry.RegistryRoles(resolved);
        var role = m.Get("role");
        if (!(role is { IsText: true } r && roles.Contains(r.AsString())))
        {
            errors.Add($"role must be one of {Join(roles)}, got {J(role)}");
        }
        else if (SubagentOnlyError(role, resolved) is { } subagentOnly)
        {
            errors.Add(subagentOnly);
        }

        var cls = RoleClassOf(m, resolved);
        IReadOnlyList<string> validModels = cls is null ? [] : registry.ClassModels(cls) ?? [];
        var model = Set(m, "model");
        if (model is not null && Is(ResolveKind(m), registry.KindDefault) && !validModels.Any(v => Is(model, v)))
        {
            errors.Add($"model {J(model)} is not valid for role {J(role)} (allowed: {Join(validModels)})");
        }

        var effort = Set(m, "effort");
        if (effort is not null && !RosterValues.EffortValues.Any(v => Is(effort, v)))
        {
            errors.Add($"effort must be one of {Join(RosterValues.EffortValues)}, got {J(effort)}");
        }

        var route = Set(m, "route");
        if (route is not null && !RosterValues.RouteValues.Any(v => Is(route, v)))
        {
            errors.Add($"route must be one of {Join(RosterValues.RouteValues)}, got {J(route)}");
        }
        else if (Is(m.Get("route"), "subagent") && cls is not null && cls != "legwork")
        {
            errors.Add($"route \"subagent\" is not allowed for role {J(role)} — only legwork roles run as subagents");
        }

        var autoMode = Set(m, "autoMode");
        if (autoMode is not null && !RosterValues.AutoModeValues.Any(v => Is(autoMode, v)))
        {
            errors.Add($"auto-mode must be one of {Join(RosterValues.AutoModeValues)}, got {J(autoMode)}");
        }

        var onMissing = Set(m, "onMissing");
        if (onMissing is not null && !RosterValues.OnMissingValues.Any(v => Is(onMissing, v)))
        {
            var why = Is(onMissing, "never") || Is(onMissing, "prompt") ? " — only legwork roles run as subagents" : string.Empty;
            errors.Add($"on-missing must be one of {Join(RosterValues.OnMissingValues)}, got {J(onMissing)}{why}");
        }

        if (m.Get("name") is not null)
        {
            errors.Add("member must not carry a stored \"name\" — it is derived when the roster is resolved");
        }

        errors.AddRange(KindFieldErrors(m, resolved));
        if (Is(ResolveKind(m), registry.KindCustom))
        {
            errors.Add($"kind \"{registry.KindCustom}\" is a team-file row admitted with spawn-ad-hoc, not a member a roster defines");
        }

        return errors;
    }

    public IReadOnlyList<string> ValidateTeamMember(JsonValue? member, JsonObject? resolved = null)
    {
        if (member is not { IsMap: true } mv)
        {
            return [$"member must be an object, got {J(member)}"];
        }

        var m = mv.AsObject();
        var errors = new List<string>();
        var roles = registry.RegistryRoles(resolved);
        var role = m.Get("role");
        if (!(role is { IsText: true } r && roles.Contains(r.AsString())))
        {
            errors.Add($"role must be one of {Join(roles)}, got {J(role)}");
        }
        else if (SubagentOnlyError(role, resolved) is { } subagentOnly)
        {
            errors.Add(subagentOnly);
        }

        var name = m.Get("name");
        var route = m.Get("route");
        if (name is { IsText: true } n0 && n0.AsString().Length == 0)
        {
            errors.Add("name must not be an empty string");
        }
        else if (RouteHasPane(route) && !IsNonEmptyString(name))
        {
            errors.Add($"name is required and must be a non-empty string when route is {J(route)}, got {J(name)}");
        }
        else if (!RouteHasPane(route) && IsPresent(name) && !IsString(name))
        {
            errors.Add($"name must be a non-empty string or null, got {J(name)}");
        }

        if (!RosterValues.RouteValues.Any(v => Is(route, v)))
        {
            errors.Add($"route must be one of {Join(RosterValues.RouteValues)}, got {J(route)}");
        }

        errors.AddRange(KindFieldErrors(m, resolved));
        foreach (var key in new[] { "transport_id", "tab_id", "workspace_id" })
        {
            if (Set(m, key) is { } v && !v.IsText)
            {
                errors.Add($"{key} must be a string or null, got {J(v)}");
            }
        }

        return errors;
    }

    public IReadOnlyList<string> ValidateRosterBlock(JsonValue? roster, JsonObject? resolved = null)
    {
        if (roster is not { IsMap: true } rv)
        {
            return ["roster must be an object"];
        }

        var block = rv.AsObject();
        var errors = new List<string>();
        var route = block.Get("route");
        if (!RosterValues.RouteValues.Any(v => Is(route, v)) || Is(route, registry.RouteMail))
        {
            var allowed = RosterValues.RouteValues.Where(v => v != registry.RouteMail);
            errors.Add($"roster.route is required and must be one of {Join(allowed)}, got {J(route)}");
        }

        if (block.Get("members") is not { IsArray: true } list)
        {
            errors.Add("roster.members must be an array");
            return errors;
        }

        var members = list.AsArray();
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (m.IsMap && Is(m.AsObject().Get("role"), "orchestrator"))
            {
                errors.Add($"member {i}: role \"orchestrator\" is not a roster member — the Orchestrator is whatever session runs /wrangl:agent-team create");
                continue;
            }

            // A member with no route of its own inherits the block's, so the rules that read `route` see the effective one.
            var effective = m.IsMap ? m.AsObject().Clone() : new JsonObject();
            var own = m.IsMap ? m.AsObject().Get("route") : null;
            effective["route"] = own is { IsTruthy: true } ? own.Value : (route ?? JsonValue.Null);
            if (route is null)
            {
                effective.Remove("route");
            }

            foreach (var e in ValidateMember(effective, resolved))
            {
                errors.Add($"member {i}: {e}");
            }
        }

        return errors;
    }
}
