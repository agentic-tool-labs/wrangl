using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary><c>roster tier</c>: how a non-Claude model compares with Claude's tiers, kept in the global config only.</summary>
internal static partial class TierVerb
{
    [GeneratedRegex(@"^[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindName();

    [GeneratedRegex("^[^" + JsText.SpaceClass + @"\p{Cc}]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelName();

    [Verb("roster:tier")]
    internal static int Tier(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (key != "cwd")
            {
                throw new RosterFailure($"tier: unrecognized flag --{key} (use --cwd)");
            }
        }

        var words = c.Opts.Positionals;
        var sub = words.Count > 0 ? words[0] : null;
        var config = c.Services.Config;
        var path = config.UserConfigPath();
        const string usage = "tier: use `tier set <kind> <model> <haiku|sonnet|opus|fable>`, `tier remove <kind> <model>`, or `tier list`";
        if (sub == "list" && words.Count == 1)
        {
            var declared = config.DeclaredModelTiers();
            var list = new JsonObject { ["path"] = path, ["modelTiers"] = declared.Get("tiers") ?? new JsonObject() };
            if (declared.Get("warnings") is { IsArray: true } warnings && warnings.AsArray().Count > 0)
            {
                list["warnings"] = warnings;
            }

            c.Out(JsonValue.FromObject(list));
            return 0;
        }

        if ((sub != "set" || words.Count != 4) && (sub != "remove" || words.Count != 3))
        {
            throw new RosterFailure(usage);
        }

        var kind = words[1];
        var model = words[2];
        if (!KindName().IsMatch(kind))
        {
            throw new RosterFailure($"tier {sub}: kind must match ^[a-z][a-z0-9-]*$, got {LevelFiles.Quote(kind)}");
        }

        if (kind == config.KindDefault)
        {
            throw new RosterFailure($"tier {sub}: a Claude model carries its own tier — declare tiers only for other kinds");
        }

        if (!ModelName().IsMatch(model))
        {
            throw new RosterFailure(
                $"tier {sub}: model must be a non-empty string with no whitespace or control characters, got {LevelFiles.Quote(model)}");
        }

        var files = new LevelFiles(c);
        var data = files.Read(path);
        var all = data.Get("modelTiers") is { IsMap: true } m ? m.AsObject() : new JsonObject();
        var forKind = all.Get(kind) is { IsMap: true } k ? k.AsObject() : new JsonObject();
        if (sub == "set")
        {
            var tier = words[3];
            if (!config.Tier.ContainsKey(tier))
            {
                throw new RosterFailure($"tier set: the tier must be one of {string.Join(", ", config.Tier.Keys)}, got {LevelFiles.Quote(tier)}");
            }

            forKind[model] = tier;
            all[kind] = forKind;
            data["modelTiers"] = all;
            files.Write(path, data);
            c.Out(JsonValue.FromObject(new JsonObject { ["path"] = path, ["kind"] = kind, ["model"] = model, ["tier"] = tier }));
            return 0;
        }

        var removed = forKind.ContainsKey(model);
        if (removed)
        {
            forKind.Remove(model);
            if (forKind.Count > 0)
            {
                all[kind] = forKind;
            }
            else
            {
                all.Remove(kind);
            }

            if (all.Count > 0)
            {
                data["modelTiers"] = all;
            }
            else
            {
                data.Remove("modelTiers");
            }

            files.Write(path, data);
        }

        c.Out(JsonValue.FromObject(new JsonObject { ["path"] = path, ["kind"] = kind, ["model"] = model, ["removed"] = removed }));
        return 0;
    }
}
