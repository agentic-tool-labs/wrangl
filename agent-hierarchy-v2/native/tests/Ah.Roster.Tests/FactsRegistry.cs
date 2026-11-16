using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Roster.Tests;

/// <summary>
/// An <see cref="IMemberRegistry"/> built from the recorded facts file tests/contracts/facts/registry.json, so the validators are
/// tested against the facts Node holds and not a second copy. Custom roles come from the <c>resolved</c> argument, and declared tiers
/// from the <c>modelTiers</c> of the home directory's configuration file.
/// </summary>
internal sealed class FactsRegistry : IMemberRegistry
{
    private readonly JsonObject _facts;
    private readonly string _home;
    private readonly Regex _kind;
    private readonly Regex _harness;

    internal FactsRegistry(string nativeRoot, string home)
    {
        var path = Path.Combine(nativeRoot, "..", "tests", "contracts", "facts", "registry.json");
        _facts = JsonText.Parse(File.ReadAllBytes(path)).AsObject();
        _home = home;
        _kind = new Regex(_facts["kindRe"].AsString().Replace("$", "\\z", StringComparison.Ordinal), RegexOptions.CultureInvariant);
        _harness = new Regex(_facts["harnessNameRe"].AsString().Replace("$", "\\z", StringComparison.Ordinal), RegexOptions.CultureInvariant);
    }

    public string KindDefault => _facts["kindDefault"].AsString();

    public string KindCustom => _facts["kindCustom"].AsString();

    public string RouteMail => _facts["routeMail"].AsString();

    public IReadOnlyList<string> BuiltinHarnesses => _facts["builtinHarnesses"].AsArray().Select(v => v.AsString()).ToList();

    public bool IsKindShape(string kind) => _kind.IsMatch(kind);

    public bool IsHarnessName(string name) => _harness.IsMatch(name);

    private IReadOnlyList<string> Builtins => _facts["roles"].AsArray().Select(v => v.AsString()).ToList();

    public IReadOnlyList<string> RegistryRoles(JsonObject? resolved)
    {
        var custom = new List<string>();
        if (resolved?.Get("roles") is { IsMap: true } roles)
        {
            custom.AddRange(roles.AsObject().Keys.Where(k => !Builtins.Contains(k)));
            custom.Sort(StringComparer.Ordinal);
        }

        return [.. Builtins, .. custom];
    }

    public string? RoleClass(string role, JsonObject? resolved)
    {
        if (_facts["roleClass"].AsObject().Get(role) is { IsText: true } builtin)
        {
            return builtin.AsString();
        }

        var entry = resolved?.Get("roles") is { IsMap: true } roles ? roles.AsObject().Get(role) : null;
        return entry?.Member("class") is { IsText: true } cls && _facts["classes"].AsObject().ContainsKey(cls.AsString()) ? cls.AsString() : null;
    }

    public IReadOnlyList<string>? ClassModels(string cls) =>
        _facts["classes"].AsObject().Get(cls) is { } c ? c.AsObject()["models"].AsArray().Select(v => v.AsString()).ToList() : null;

    public bool IsPeerEligible(string role, JsonObject? resolved) =>
        RoleClass(role, resolved) is { } cls && ClassIsChain(cls) && !_facts["subagentOnly"].AsArray().Any(v => v.AsString() == role);

    public bool ClassIsChain(string cls) =>
        _facts["classes"].AsObject().Get(cls) is { } c && c.AsObject()["chain"].AsBool();

    /// <summary>The tier of opus on the facts' scale.</summary>
    internal int TopTier => (int)_facts["tiers"].AsObject()["opus"].AsNumber();

    /// <summary>A model's family tier from <c>claude-&lt;family&gt;-...</c> or a bare family name; null when unknown.</summary>
    internal int? TierOf(JsonValue? model)
    {
        if (model is not { IsText: true } m || m.AsString().Length == 0)
        {
            return null;
        }

        var found = Regex.Match(m.AsString().ToLowerInvariant(), "(?:^|[^a-z])(haiku|sonnet|opus|fable)(?:[^a-z]|\\z)", RegexOptions.CultureInvariant);
        return found.Success ? (int)_facts["tiers"].AsObject()[found.Groups[1].Value].AsNumber() : null;
    }

    public string? DeclaredTier(string kind, string model)
    {
        try
        {
            var doc = JsonText.Parse(File.ReadAllBytes(Path.Combine(_home, ".claude", "ah2", "agent-hierarchy.json")));
            if (kind == KindDefault || !IsKindShape(kind)
                || doc.Member("modelTiers") is not { IsMap: true } tiers
                || tiers.AsObject().Get(kind) is not { IsMap: true } models
                || models.AsObject().Get(model) is not { IsText: true } tier
                || !_facts["tiers"].AsObject().ContainsKey(tier.AsString()))
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
