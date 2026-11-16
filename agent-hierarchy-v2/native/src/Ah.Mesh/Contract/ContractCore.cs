using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Mesh;

/// <summary>
/// The participation contract of the daemon in its two renderings (JSON and a markdown guide). The prose lives in one file,
/// <c>hooks/participation-contract.md</c> at the plugin root: requirement texts under stable ids and <c>{{placeholders}}</c> where
/// a table belongs. The tables are generated from the route table and constants the daemon enforces.
/// </summary>
public interface IContractCore
{
    /// <summary>The contract as a JSON-able object: <c>{contract_version, protocol, requirements, routes, enums, rules}</c>.</summary>
    JsonObject Document();

    /// <summary>The JSON rendering: one line, as the daemon sends it.</summary>
    string RenderJson();

    /// <summary>The guide: the prose with each placeholder replaced by its generated table.</summary>
    string RenderGuide();

    /// <summary>The requirements of the prose: <c>### &lt;id&gt; (&lt;level&gt;)</c> headings, each with the text up to the next heading.</summary>
    JsonArray Requirements();
}

internal sealed partial class ContractCore(IFileReader files, IAhEnvironment environment) : IContractCore
{
    private const string ProseName = "participation-contract.md";
    private const string ContractVersion = "1.1";
    private const int Protocol = 1;

    [GeneratedRegex(@"^### (P-[A-Z0-9-]+) \((must|should|may)\)[" + JsText.SpaceClass + @"]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RequirementHeading();

    [GeneratedRegex(@"^#{1,3} ", RegexOptions.CultureInvariant)]
    private static partial Regex AnyHeading();

    [GeneratedRegex(@"\{\{([a-z-]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private string ProseText()
    {
        var root = environment.Plugin?.Root ?? throw new InvalidOperationException("the plugin root is unknown");
        return files.ReadText(NodePath.Join(root, "hooks", ProseName));
    }

    public JsonArray Requirements() => ParseRequirements(ProseText());

    /// <summary>The requirements of <paramref name="text"/>.</summary>
    internal static JsonArray ParseRequirements(string text)
    {
        var found = new List<(string Id, string Level, List<string> Lines)>();
        List<string>? current = null;
        foreach (var line in text.Split('\n'))
        {
            var heading = RequirementHeading().Match(line);
            if (heading.Success)
            {
                current = [];
                found.Add((heading.Groups[1].Value, heading.Groups[2].Value, current));
                continue;
            }

            if (AnyHeading().IsMatch(line))
            {
                current = null;
            }
            else
            {
                current?.Add(line);
            }
        }

        var all = new JsonArray();
        foreach (var (id, level, lines) in found)
        {
            all.Add(new JsonObject { ["id"] = id, ["level"] = level, ["text"] = JsText.Trim(string.Join('\n', lines)) });
        }

        return all;
    }

    public JsonObject Document()
    {
        var template = JsonText.Parse(ContractData.DocumentJson).AsObject();
        var document = new JsonObject
        {
            ["contract_version"] = template["contract_version"],
            ["protocol"] = template["protocol"],
            ["requirements"] = Requirements(),
        };
        foreach (var key in template.Keys.Where(k => k is not ("contract_version" or "protocol" or "requirements")))
        {
            document[key] = template[key];
        }

        return document;
    }

    public string RenderJson() => JsonText.Stringify(Document(), 0);

    // ---------------------------------------------------------------- the guide

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Text(JsonValue? value) => value is { } v ? (v.IsText ? v.AsString() : JsonText.Stringify(v, 0)) : string.Empty;

    private static string FieldTable(JsonObject fields)
    {
        var rows = new List<string> { "| field | type | required | notes |", "|---|---|---|---|" };
        foreach (var name in fields.Keys)
        {
            var f = fields[name].AsObject();
            var enumPart = f.Get("enum") is { IsArray: true } e
                ? $" ({string.Join(", ", e.AsArray().Select(x => $"`{Text(x)}`"))})"
                : string.Empty;
            var required = f.Get("required") is { IsTruthy: true } ? "yes" : "no";
            var note = f.Get("note") is { IsTruthy: true } n ? Text(n) : string.Empty;
            rows.Add($"| `{name}` | {Text(f.Get("type"))}{enumPart} | {required} | {Cell(note)} |");
        }

        return string.Join('\n', rows);
    }

    private static string RenderRoutes(JsonArray routes)
    {
        var output = new List<string>();
        foreach (var item in routes)
        {
            var r = item.AsObject();
            output.Add($"### `{Text(r.Get("method"))} {Text(r.Get("path"))}`");
            output.Add(string.Empty);
            output.Add(Text(r.Get("purpose")));
            output.Add(string.Empty);
            var query = r.Get("query");
            var fieldsValue = query is { IsTruthy: true } ? query : r.Get("body");
            if (fieldsValue is { IsTruthy: true, IsMap: true } fv)
            {
                var all = fv.AsObject();
                var rest = new JsonObject();
                foreach (var key in all.Keys.Where(k => k != "args"))
                {
                    rest[key] = all[key];
                }

                output.Add(query is { IsTruthy: true } ? "Query:" : "Body:");
                output.Add(string.Empty);
                output.Add(FieldTable(rest));
                output.Add(string.Empty);
                if (all.Get("args") is { IsMap: true } args && args.AsObject().Get("fields") is { IsTruthy: true, IsMap: true } argFields)
                {
                    output.Add("`args` fields:");
                    output.Add(string.Empty);
                    output.Add(FieldTable(argFields.AsObject()));
                    output.Add(string.Empty);
                }
            }

            output.Add($"Response: {Text(r.Get("response"))}");
            output.Add(string.Empty);
        }

        return TrimEndJs(string.Join('\n', output));
    }

    private static string TrimEndJs(string s)
    {
        var end = s.Length;
        while (end > 0 && JsText.IsSpace(s[end - 1]))
        {
            end--;
        }

        return s[..end];
    }

    private static string Ticked(IEnumerable<JsonValue> values) => string.Join(", ", values.Select(v => $"`{Text(v)}`"));

    private static string RenderEnums(JsonObject enums)
    {
        var refusals = enums["send_refusals"].AsObject();
        return string.Join(
            '\n',
            $"- `ack_by` (the `by` of an ack): {Ticked(enums["ack_by"].AsArray())}. A participant uses `adapter`.",
            $"- `send_keys` (the body keys of `POST /v1/send`): {Ticked(enums["send_keys"].AsArray())}.",
            $"- `send_refusals` (the `error.code` of a refused `POST /v1/send`, with its HTTP status): {string.Join(", ", refusals.Keys.Select(c => $"`{c}` ({Text(refusals[c])})"))}.",
            $"- `entry_filters` (the `filter` of `inspect/entries`): {Ticked(enums["entry_filters"].AsArray())}.",
            $"- A harness name matches `{Text(enums["harness_name_pattern"])}`.",
            $"- The built-in harnesses, which register without admission: {Ticked(enums["builtin_harnesses"].AsArray())}.",
            $"- A requirement's level is one of {Ticked(enums["requirement_levels"].AsArray())}.");
    }

    private static string RenderRules(JsonObject rules) => string.Join(
        '\n',
        "| rule | value |",
        "|---|---|",
        $"| heartbeat fresh factor | a heartbeat is fresh while its age is at most {Text(rules["heartbeat_fresh_factor"])} x `poll_ms` |",
        $"| default `poll_ms` | {Text(rules["poll_ms_default"])} |",
        $"| longest `wait` of a long-poll | {Text(rules["inbox_wait_max_s"])} s |",
        $"| largest request body | {Text(rules["max_request_body_bytes"])} bytes |",
        $"| entry marker | `{Text(rules["marker_format"])}` |");

    private static string RenderVersions() => string.Join(
        '\n',
        "| | |",
        "|---|---|",
        $"| contract version | `{ContractVersion}` (`<major>.<minor>`) |",
        $"| protocol | `{Protocol}` (the `/v{Protocol}` prefix of every route) |",
        string.Empty,
        "A minor bump of the contract version is additive: a new optional field, route or requirement at `should` or `may`. A major bump changes",
        "a `must`, or removes or renames something, and ships with a new protocol; `/v1` stays served while `protocols` on health lists 1.");

    public string RenderGuide()
    {
        var doc = Document();
        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["versions"] = RenderVersions(),
            ["routes"] = RenderRoutes(doc["routes"].AsArray()),
            ["enums"] = RenderEnums(doc["enums"].AsObject()),
            ["rules"] = RenderRules(doc["rules"].AsObject()),
            ["response-shape"] = ContractData.ResponseShape,
        };
        return Placeholder().Replace(ProseText(), m =>
            parts.TryGetValue(m.Groups[1].Value, out var table)
                ? table
                : throw new InvalidOperationException($"the participation contract names an unknown placeholder {{{{{m.Groups[1].Value}}}}}"));
    }
}
