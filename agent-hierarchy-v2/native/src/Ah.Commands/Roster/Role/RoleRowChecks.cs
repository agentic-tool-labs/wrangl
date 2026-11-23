using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// The checks <c>role set</c> applies to one role row, shared with <c>roster kit import</c>. Each returns null when the row passes,
/// else the reason as <c>role set</c> prints it after <c>role set &lt;name&gt;: </c>.
/// </summary>
internal static class RoleRowChecks
{
    /// <summary>The fields a built-in role's row may not set: only agent, model and dispatch apply.</summary>
    internal static readonly string[] NotOnBuiltin = ["class", "label", "description", "routes", "scaffold", "shell", "writes"];

    internal static string NotOnBuiltinError(string field) =>
        $"--{field} does not apply to a built-in role — only --agent, --model and --dispatch";

    internal static string? DispatchError(Ah.Config.IConfigCore config, JsonObject row) =>
        row.Get("dispatch") is { } d && !(d.IsText && config.DispatchModes.Contains(d.AsString()))
            ? $"--dispatch must be one of {string.Join(", ", config.DispatchModes)}"
            : null;

    internal static string? DispatchModelError(string? dispatch, string cls) =>
        dispatch == "model" && cls != "legwork"
            ? $"--dispatch model is not allowed for a {cls} role — only legwork roles run as subagents"
            : null;

    internal static string? ModelError(Ah.Config.IConfigCore config, string name, string cls, JsonValue model)
    {
        var allowed = config.Classes.Get(cls) is { IsMap: true } row && row.Member("models") is { IsArray: true } m
            ? m.AsArray().Select(x => MemberPlanning.JsConvertText(x)).ToList()
            : [];
        return model.IsText && allowed.Contains(model.AsString())
            ? null
            : $"model {JsonText.Stringify(model, 0)} is not allowed for {name} (allowed: {string.Join(", ", allowed)})";
    }

    /// <summary>A built-in's <c>agent</c>: a valid reference, and no first-party agent but its own.</summary>
    internal static string? BuiltinAgentError(Ah.Config.IConfigCore config, string name, JsonValue agent)
    {
        if (config.AgentRefError(agent) is { IsTruthy: true } why)
        {
            return MemberPlanning.JsConvertText(why);
        }

        var text = MemberPlanning.JsConvertText(agent);
        return text.StartsWith(config.PluginName + ":", StringComparison.Ordinal) && text != config.Qualified(name)
            ? $"{config.PluginName}:* agents belong to the built-ins"
            : null;
    }

    /// <summary>An agent maps to exactly one role: the reason when another role of <paramref name="registry"/> already has <paramref name="agent"/>.</summary>
    internal static string? OwnerError(Ah.Config.IConfigCore config, JsonObject registry, string name, string agent)
    {
        var roles = registry.Get("roles") is { IsMap: true } r ? r.AsObject() : new JsonObject();
        var owner = config.RegistryRoles(registry).FirstOrDefault(x => x != name && config.RoleAgent(x, roles.Get(x)) == agent);
        return owner is null ? null : $"agent {JsonText.Quote(agent)} is already the {owner} role's agent — an agent maps to exactly one role";
    }
}
