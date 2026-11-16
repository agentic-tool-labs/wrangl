using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Config;

/// <summary>Kinds, harnesses, tiers and the class table as data: the parts of the registry that are plain values.</summary>
internal static partial class ConfigRegistry
{
    internal const string KindCustom = "custom";
    internal const string CodexAppserverHarness = "codex-appserver";
    internal const string RouteMail = "mail";

    internal static readonly string[] BuiltinHarnesses = ["claude", "codex", "claude-mod", "stub", "probe"];
    internal static readonly string[] TestHarnesses = ["stub", "probe"];

    internal static readonly string[] TierNames = ["haiku", "sonnet", "opus", "fable"];

    [GeneratedRegex("^[a-z][a-z0-9-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindRegex();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HarnessNameRegex();

    [GeneratedRegex("^[A-Za-z0-9._/:@+,-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SelfCareTokenRegex();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HerdrNameRegex();

    /// <summary><c>KIND_RE.test(x)</c>: the value is coerced to text the way <c>RegExp.prototype.test</c> does.</summary>
    internal static bool IsKindName(JsonValue? value) => KindRegex().IsMatch(JsConvert.ToJsString(value));

    internal static bool IsHarnessName(JsonValue? value) => HarnessNameRegex().IsMatch(JsConvert.ToJsString(value));

    internal static bool IsSelfCareToken(JsonValue? value) => SelfCareTokenRegex().IsMatch(JsConvert.ToJsString(value));

    internal static bool IsRoleName(JsonValue? value) => RoleNameRegex().IsMatch(JsConvert.ToJsString(value));

    internal static bool IsAgentRef(JsonValue? value) => AgentRefRegex().IsMatch(JsConvert.ToJsString(value));

    internal static bool IsLabel(JsonValue? value) => LabelRegex().IsMatch(JsConvert.ToJsString(value));

    internal static string BuiltinLabel(string role) => BuiltinRows.First(r => r.Name == role).Label;

    /// <summary>The built-in role names a hierarchy session name may carry: the built-ins, then <c>orchestrator</c>.</summary>
    internal static IEnumerable<string> HierarchyNameRoles => [.. Roles, "orchestrator"];

    /// <summary><c>toLowerCase</c> for the characters that can matter to an ASCII pattern: ASCII letters, the Kelvin sign, and U+0130.</summary>
    internal static string LowerForAsciiPatterns(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z')
            {
                sb.Append((char)(c + 32));
            }
            else if (c == '\u212A')
            {
                sb.Append('k');
            }
            else if (c == '\u0130')
            {
                sb.Append("i\u0307");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary><c>tierOf</c>: the rank of the first family word in a model name (haiku 1 .. fable 4), or null.</summary>
    internal static int? TierOf(JsonValue? model)
    {
        if (model is not { IsText: true } m || m.AsString().Length == 0)
        {
            return null;
        }

        var s = LowerForAsciiPatterns(m.AsString());
        static bool Letter(char c) => c is >= 'a' and <= 'z';
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && Letter(s[i - 1]))
            {
                continue;
            }

            for (var t = 0; t < TierNames.Length; t++)
            {
                var word = TierNames[t];
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                {
                    continue;
                }

                var end = i + word.Length;
                if (end == s.Length || !Letter(s[end]))
                {
                    return t + 1;
                }
            }
        }

        return null;
    }

    /// <summary><c>m &amp;&amp; m.kind</c>, with <c>undefined</c> and <c>null</c> both meaning the default kind.</summary>
    internal static JsonValue ResolveKind(JsonValue? member)
    {
        JsonValue? kind;
        if (member is null)
        {
            kind = null;
        }
        else if (!member.Value.IsTruthy)
        {
            kind = member;
        }
        else
        {
            kind = member.Value.Member("kind");
        }

        return kind is null || kind.Value.IsNull ? KindDefault : kind.Value;
    }

    /// <summary>Whether a team member runs in a pane: not the default kind, and routed <c>pane</c>. A pure function of the member row.</summary>
    internal static bool IsPaneMember(JsonValue? member) =>
        member is { IsTruthy: true } m
        && !(ResolveKind(m) is { IsText: true } k && k.AsString() == KindDefault)
        && m.Member("route") is { IsText: true } route && route.AsString() == "pane";

    /// <summary><c>validateHerdrName</c>: <c>{ok: true}</c> or <c>{ok: false, why}</c>.</summary>
    internal static JsonObject ValidateHerdrName(JsonValue? name)
    {
        if (name is not { IsText: true } n || n.AsString().Length == 0)
        {
            return new JsonObject { ["ok"] = false, ["why"] = "name must be a non-empty string" };
        }

        var s = n.AsString();
        if (!HerdrNameRegex().IsMatch(s))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["why"] = $"derived name {JsonText.Stringify(s, 0)} ({s.Length} chars) does not match Herdr's agent-name rule [a-z][a-z0-9_-]{{0,31}} (max 32 characters) — create the team with a shorter `--team <name>`, or use a shorter role or --member name",
            };
        }

        return new JsonObject { ["ok"] = true };
    }

    internal static JsonArray StringArray(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values)
        {
            a.Add(v);
        }

        return a;
    }

    private static JsonObject ContractOf(string[] required, string[][] anyOf, string[] forbidden, string[] discouraged, string[] missingRecommended)
    {
        var req = StringArray(required);
        foreach (var group in anyOf)
        {
            req.Add(StringArray(group));
        }

        return new JsonObject
        {
            ["required"] = req,
            ["forbidden"] = StringArray(forbidden),
            ["discouraged"] = StringArray(discouraged),
            ["missingRecommended"] = StringArray(missingRecommended),
        };
    }

    /// <summary><c>CLASSES</c> as Node holds it, key for key. Built fresh: callers may keep and change what they get.</summary>
    internal static JsonObject ClassesJson()
    {
        var result = new JsonObject();
        foreach (var c in Classes)
        {
            JsonObject? contract = c.Name switch
            {
                "advise" => ContractOf(["Read", "SendMessage"], [], [], ["Edit", "NotebookEdit", "advisor"], []),
                "design" => ContractOf(["Read", "SendMessage", "Write"], [], [], ["Bash", "NotebookEdit", "advisor"], []),
                "review" => ContractOf(["Read", "SendMessage"], [], ["Edit", "Write", "NotebookEdit"], ["advisor"], ["Bash"]),
                "implement" => ContractOf(["Read", "SendMessage"], [["Edit", "Write"], ["Write", "Bash"]], [], ["advisor"], ["Bash"]),
                _ => null,
            };
            result[c.Name] = new JsonObject
            {
                ["builtin"] = c.Builtin,
                ["step"] = c.Step is null ? JsonValue.Null : c.Step,
                ["models"] = StringArray(c.Models),
                ["chain"] = c.Chain,
                ["tier"] = c.Tier,
                ["owesResponse"] = c.OwesResponse,
                ["ultraGate"] = c.UltraGate,
                ["roleDispatchable"] = c.RoleDispatchable,
                ["contract"] = contract is null ? JsonValue.Null : contract,
            };
        }

        return result;
    }

    internal static JsonObject RoleLabelsJson()
    {
        var o = new JsonObject();
        foreach (var r in Roles)
        {
            o[r] = BuiltinLabel(r);
        }

        return o;
    }

    internal static JsonObject RoleDefaultsJson()
    {
        var o = new JsonObject();
        foreach (var r in Roles)
        {
            o[r] = RoleDefaults(r);
        }

        return o;
    }

    internal static JsonObject ValidModelsByRoleJson()
    {
        var o = new JsonObject();
        foreach (var r in Roles)
        {
            o[r] = StringArray(ValidModels(r));
        }

        return o;
    }
}
