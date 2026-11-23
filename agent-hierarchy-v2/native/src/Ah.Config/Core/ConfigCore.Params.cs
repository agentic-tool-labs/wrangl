using Ah.Core.Json;

namespace Ah.Config;

/// <summary>
/// Role parameters (role-params RP-D3, RP-D4): a role's <c>params</c> names the values its members need, and the top-level <c>params</c> object of the level files holds
/// them. The one reader; spawn, the session notice, the brief footer and <c>ah roster param list</c> all read through it.
/// </summary>
internal sealed partial class ConfigCore
{
    internal const string ParamsKey = "params";

    internal const int MaxParamValue = 500;

    /// <summary>
    /// Every parameter set at any level: per name the first level that has it, most specific first (repo-user, repo, global), as <c>activeRoster</c> is read. A missing or
    /// unreadable level file gives no value; a value that is not text is not a value.
    /// </summary>
    public IReadOnlyList<RoleParam> ParamValues(string? cwd)
    {
        var found = new SortedDictionary<string, RoleParam>(StringComparer.Ordinal);
        foreach (var (level, _, data) in ExistingLevelFiles(cwd))
        {
            if (data.Get(ParamsKey) is not { IsMap: true } values)
            {
                continue;
            }

            var map = values.AsObject();
            foreach (var name in map.Keys)
            {
                if (map[name] is { IsText: true } value && !found.ContainsKey(name))
                {
                    found[name] = new RoleParam(name, value.AsString(), level);
                }
            }
        }

        return [.. found.Values];
    }

    /// <summary>
    /// The parameters <paramref name="role"/> declares in <paramref name="resolved"/> (the resolved config, read for <paramref name="cwd"/> when null), in declared order,
    /// each with its value and level, or none. Empty when the role declares none.
    /// </summary>
    public IReadOnlyList<RoleParam> RoleParams(string? cwd, string role, JsonObject? resolved = null)
    {
        resolved ??= ResolveConfig(cwd);
        if (resolved.Get("roles")?.Member(role)?.Member(ParamsKey) is not { IsArray: true } declared)
        {
            return [];
        }

        var values = ParamValues(cwd).ToDictionary(p => p.Name, StringComparer.Ordinal);
        return [.. declared.AsArray().Where(n => n.IsText).Select(n => values.TryGetValue(n.AsString(), out var set) ? set : new RoleParam(n.AsString(), null, null))];
    }

    /// <summary>Why <paramref name="name"/> is not a parameter name, or null.</summary>
    public string? ParamNameError(string name) =>
        ConfigRegistry.ParamNameRegex().IsMatch(name) ? null : $"parameter name {JsonText.Quote(name)} must match ^[a-z][a-z0-9-]{{0,39}}$";

    /// <summary>Why <paramref name="value"/> is not a parameter value (1 to 500 characters, no control character), or null. A value is never opened or checked as a path.</summary>
    public string? ParamValueError(string value) =>
        value.Length is 0 or > MaxParamValue ? $"a parameter value is 1 to {MaxParamValue} characters; this has {value.Length}"
        : value.Any(char.IsControl) ? "a parameter value holds no control character or newline"
        : null;
}
