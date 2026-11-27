using System.Text.RegularExpressions;
using Ah.Core.Digests;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

internal sealed partial class ConfigCore
{
    private static readonly string[] PluginJsonPlainKeys = ["name", "version", "description", "author", "homepage", "repository", "license", "keywords"];

    private const string PackManifestFile = "ah-roles.json";

    [GeneratedRegex("^(README|LICENSE|CHANGELOG)(\\.[^\\n\\r\\u2028\\u2029]*)?\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadmeLikeName();

    public string PackManifestName => PackManifestFile;

    public IReadOnlyList<string> PackAgentKeys => PackAgentText.PackAgentKeys;

    public string UnattendedLine => $"takes work in unattended /{PluginName}:pipeline runs (pushes draft PRs, does not merge)";

    public bool IsPin(JsonValue? value) => value is { IsText: true } t && PinRegex().IsMatch(t.AsString());

    private static JsonObject RecordJson(InstallRecord r) => new()
    {
        ["key"] = r.Key,
        ["plugin"] = r.Plugin,
        ["marketplace"] = r.Marketplace is null ? JsonValue.Null : r.Marketplace,
        ["installPath"] = r.InstallPath,
        ["version"] = r.Version is null ? JsonValue.Null : r.Version,
        ["scope"] = r.Scope is null ? JsonValue.Null : r.Scope,
    };

    public JsonArray InstallRecordList()
    {
        var array = new JsonArray();
        foreach (var r in InstallRecords())
        {
            array.Add(RecordJson(r));
        }

        return array;
    }

    public JsonArray PackRecordList()
    {
        var array = new JsonArray();
        foreach (var r in InstallRecords())
        {
            if (files.Stat(NodePath.Join(r.InstallPath, PackManifestFile)) is not null)
            {
                array.Add(RecordJson(r));
            }
        }

        return array;
    }

    public string? ParsePack(string? pack) => PackText.ParsePack(pack);

    public JsonValue ParseFrom(JsonValue? from)
    {
        if (from is not { IsText: true } t || PackText.ParseFrom(t.AsString()) is not { } f)
        {
            return JsonValue.Null;
        }

        return new JsonObject
        {
            ["plugin"] = f.Plugin,
            ["marketplace"] = f.Marketplace is null ? JsonValue.Null : f.Marketplace,
            ["role"] = f.Role,
        };
    }

    public bool IsFromRow(JsonValue? raw) => raw is { IsMap: true } m && m.AsObject().Get("from") is { IsText: true };

    public JsonValue HiddenCharAt(JsonValue? text) =>
        PackText.HiddenCharAt(JsConvert.ToJsString(text)) is { } where ? where : JsonValue.Null;

    public string EscapeTerminal(JsonValue? text) => PackText.EscapeTerminal(JsConvert.ToJsString(text));

    public string PluginNameAt(string dir)
    {
        try
        {
            var pj = JsonText.Parse(files.ReadText(NodePath.Join(dir, ".claude-plugin", "plugin.json")));
            if (pj.Member("name") is { IsText: true } name && name.AsString().Length > 0)
            {
                return name.AsString();
            }
        }
        catch (Exception)
        {
            // falls back to the directory's name
        }

        return NodePath.Basename(NodePath.Resolve(CwdOr(null), dir));
    }

    public JsonObject PackTreeJson(string root)
    {
        var tree = PackTreeOf(root);
        return new JsonObject
        {
            ["files"] = TreeFiles(tree.Files),
            ["symlink"] = tree.Symlink is null ? JsonValue.Null : tree.Symlink,
            ["error"] = tree.Error is null ? JsonValue.Null : tree.Error,
        };
    }

    public string PackPinOf(JsonObject row, string agentText, JsonArray files) => PackPin(row, agentText, files);

    public string PackDigest(JsonArray files) => "sha256:" + ContentDigest.OfBytes(JsonText.ToUtf8(files, 0));

    public JsonObject PackNameClaimsJson(string dir, JsonValue? manifest)
    {
        var claims = PackNameClaims(dir, manifest is { IsMap: true } m ? m.AsObject() : new JsonObject());
        return new JsonObject
        {
            ["error"] = claims.Error is null ? JsonValue.Null : claims.Error,
            ["byRole"] = Lists(claims.ByRole),
            ["files"] = Lists(claims.Files),
        };

        static JsonObject Lists(Dictionary<string, List<string>> map)
        {
            var o = new JsonObject();
            foreach (var (key, values) in map)
            {
                var a = new JsonArray();
                foreach (var v in values)
                {
                    a.Add(v);
                }

                o[key] = a;
            }

            return o;
        }
    }

    public JsonObject PackRoleStateJson(JsonValue? from)
    {
        if (from is not { IsText: true } text)
        {
            return new JsonObject
            {
                ["status"] = "pack-invalid",
                ["message"] = $"from {JsonText.Stringify(from ?? JsonValue.Null, 0)} must be <plugin>@<marketplace>:<role>",
                ["from"] = JsonValue.Null,
                ["records"] = new JsonArray(),
            };
        }

        return PackStateJson(PackRoleState(text.AsString()));
    }

    /// <summary>The state as Node's <c>packRoleState</c> returns it: a candidate carries every key, an early answer only the four.</summary>
    private static JsonObject PackStateJson(PackState s)
    {
        var o = new JsonObject();
        if (s.Record is { } record)
        {
            o["record"] = RecordJson(record);
            o["status"] = s.Status;
            o["message"] = s.Message is null ? JsonValue.Null : s.Message;
            o["subcode"] = string.IsNullOrEmpty(s.Subcode) ? JsonValue.Null : s.Subcode;
            o["manifestRole"] = s.ManifestRole is null ? JsonValue.Null : s.ManifestRole;
            o["agentText"] = s.AgentText ?? JsonValue.Null;
            o["agentPath"] = s.AgentPath ?? JsonValue.Null;
            o["files"] = s.Files is null ? JsonValue.Null : s.Files;
            o["pin"] = s.Pin is null ? JsonValue.Null : s.Pin;
            if (s.Warnings.Count > 0)
            {
                var warnings = new JsonArray();
                foreach (var w in s.Warnings)
                {
                    warnings.Add(w);
                }

                o["warnings"] = warnings;
            }
        }
        else
        {
            o["status"] = s.Status;
            o["message"] = s.Message is null ? JsonValue.Null : s.Message;
        }

        o["from"] = s.From is { } f
            ? new JsonObject
            {
                ["plugin"] = f.Plugin,
                ["marketplace"] = f.Marketplace is null ? JsonValue.Null : f.Marketplace,
                ["role"] = f.Role,
            }
            : JsonValue.Null;
        var records = new JsonArray();
        foreach (var r in s.Records)
        {
            records.Add(r);
        }

        o["records"] = records;
        return o;
    }

    public JsonArray PackExtras(string dir, JsonValue? manifest)
    {
        var manifestObject = manifest is { IsMap: true } m ? m.AsObject() : new JsonObject();
        var extras = new List<JsonObject>();
        var entries = new List<string>();
        try
        {
            entries.AddRange(files.List(dir));
            entries.Sort(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            entries.Clear();
        }

        foreach (var name in entries)
        {
            if (name is PackManifestFile or "agents" or ".claude-plugin" || ReadmeLikeName().IsMatch(name))
            {
                continue;
            }

            extras.Add(new JsonObject { ["kind"] = "root", ["name"] = name });
        }

        if (TryReadJsonStrict(NodePath.Join(dir, ".claude-plugin", "plugin.json")) is { IsMap: true } pj)
        {
            foreach (var key in pj.AsObject().Keys)
            {
                if (Array.IndexOf(PluginJsonPlainKeys, key) < 0)
                {
                    extras.Add(new JsonObject { ["kind"] = "plugin.json", ["name"] = key });
                }
            }
        }

        var named = new HashSet<string>(StringComparer.Ordinal);
        if (manifestObject.Get("roles") is { IsMap: true } roles)
        {
            foreach (var key in roles.AsObject().Keys)
            {
                if (roles.AsObject()[key].Member("agent")?.NonEmptyText is { } agent)
                {
                    named.Add(agent);
                }
            }
        }

        var agents = new List<string>();
        try
        {
            agents.AddRange(files.List(NodePath.Join(dir, "agents")).Where(n => n.EndsWith(".md", StringComparison.Ordinal)));
            agents.Sort(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            agents.Clear();
        }

        foreach (var n in agents)
        {
            if (!named.Contains(n[..^3]))
            {
                extras.Add(new JsonObject { ["kind"] = "agent", ["name"] = "agents/" + n });
            }
        }

        var claims = PackNameClaims(dir, manifestObject).Files;
        foreach (var e in extras)
        {
            if (e["kind"].AsString() == "agent" && claims.TryGetValue(e["name"].AsString(), out var roleList))
            {
                e["claims"] = StringArray(roleList);
            }
        }

        foreach (var (rel, roleList) in claims)
        {
            if (!extras.Any(e => e["kind"].AsString() == "agent" && e["name"].AsString() == rel))
            {
                extras.Add(new JsonObject { ["kind"] = "agent", ["name"] = rel, ["claims"] = StringArray(roleList) });
            }
        }

        var result = new JsonArray();
        foreach (var e in extras)
        {
            result.Add(e);
        }

        return result;

        static JsonArray StringArray(List<string> values)
        {
            var a = new JsonArray();
            foreach (var v in values)
            {
                a.Add(v);
            }

            return a;
        }
    }
}
