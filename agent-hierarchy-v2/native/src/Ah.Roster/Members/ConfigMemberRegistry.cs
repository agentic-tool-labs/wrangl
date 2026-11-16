using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

/// <summary>
/// The member registry over the config library: its role, class and harness tables are the only copy, and the declared model tiers are
/// read from the user's global config file, <c>&lt;home&gt;/.claude/ah2/agent-hierarchy.json</c>.
/// </summary>
public sealed record ConfigMemberRegistry(IConfigCore Config, IFileReader Files, IAhEnvironment Environment) : IMemberRegistry
{
    public string KindDefault => Config.KindDefault;

    public string KindCustom => Config.KindCustom;

    public string RouteMail => Config.RouteMail;

    public IReadOnlyList<string> BuiltinHarnesses => Config.BuiltinHarnesses;

    public bool IsKindShape(string kind) => Config.IsKindName(kind);

    public bool IsHarnessName(string name) => Config.IsHarnessName(name);

    public IReadOnlyList<string> RegistryRoles(JsonObject? resolved) => Config.RegistryRoles(resolved);

    public string? RoleClass(string role, JsonObject? resolved) => Config.RoleClass(role, resolved);

    public IReadOnlyList<string>? ClassModels(string cls) =>
        Config.Classes.Get(cls) is { IsMap: true } c && c.Member("models") is { IsArray: true } models
            ? models.AsArray().Where(v => v.IsText).Select(v => v.AsString()).ToList()
            : null;

    public bool IsPeerEligible(string role, JsonObject? resolved) =>
        Config.ClassProp(role, resolved, "chain") is { IsBool: true } chain && chain.AsBool();

    public bool ClassIsChain(string cls) =>
        Config.Classes.Get(cls) is { IsMap: true } c && c.Member("chain") is { IsTruthy: true };

    public string? DeclaredTier(string kind, string model)
    {
        try
        {
            var path = AhPaths.UserEntry(Environment.HomeDirectory, AhPaths.ConfigFile);
            var doc = JsonText.Parse(Files.ReadBytes(path));
            if (kind == KindDefault || !IsKindShape(kind)
                || doc.Member("modelTiers") is not { IsMap: true } tiers
                || tiers.AsObject().Get(kind) is not { IsMap: true } models
                || models.AsObject().Get(model) is not { IsText: true } tier
                || !Config.Tier.ContainsKey(tier.AsString()))
            {
                return null;
            }

            return tier.AsString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
