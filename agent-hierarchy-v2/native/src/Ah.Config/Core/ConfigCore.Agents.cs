using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>Agent files: where an agent ref's file is, what it says, the class-contract validator, and the registry views built on it.</summary>
internal sealed partial class ConfigCore
{
    private const int AgentReadCapBytes = 16 * 1024;

    public int AgentReadCap => AgentReadCapBytes;

    public string TaskGopher => "task-gopher:task-gopher";

    private static JsonArray NoFixes() => new();

    private const string ClaudeDirKey = ConfigModule.ClaudeDirKey;

    /// <summary><paramref name="claudeDir"/> when given, else <c>~/.claude</c>.</summary>
    private string ClaudeDirOr(string? claudeDir) => Ah.Core.CoreModule.ClaudeConfigDir(claudeDir, Home);

    // ---------------------------------------------------------------- where an agent file is

    /// <summary><c>installed_plugins.json</c>'s <c>plugins</c> entries as <c>(key, records)</c>, each record list an array; empty when unreadable.</summary>
    private List<(string Key, JsonArray Records)> InstalledPluginEntries(string? claudeDir)
    {
        var result = new List<(string, JsonArray)>();
        var data = TryReadJson(NodePath.Join(ClaudeDirOr(claudeDir), "plugins", "installed_plugins.json"));
        if (data is not { IsMap: true } doc || doc.AsObject().Get("plugins") is not { } plugins || !(plugins.IsMap || plugins.IsArray))
        {
            return result;
        }

        if (plugins.IsMap)
        {
            var map = plugins.AsObject();
            foreach (var key in map.Keys)
            {
                if (map[key].IsArray)
                {
                    result.Add((key, map[key].AsArray()));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <c>locateAgentFile</c>: <c>{kind, path, level, shadowed, error, candidates}</c> (and <c>records</c> for a plugin ref). A bare name
    /// is looked up in <c>&lt;repoRoot&gt;/.claude/agents/</c> then <c>~/.claude/agents/</c>; <c>plugin:agent</c> goes through the install records.
    /// </summary>
    public JsonObject LocateAgentFile(JsonValue? reference, string? cwd, string? claudeDir = null)
    {
        var colon = reference is { IsText: true } r ? r.AsString().IndexOf(':', StringComparison.Ordinal) : -1;
        if (colon == -1)
        {
            var name = reference is null ? "undefined" : JsConvert.ToJsString(reference);
            var start = Resolve(string.IsNullOrEmpty(cwd) ? process.CurrentDirectory : cwd);
            var root = FindGitRoot(start) ?? start;
            var repo = NodePath.Join(root, ".claude", "agents", name + ".md");
            var user = NodePath.Join(ClaudeDirOr(claudeDir), "agents", name + ".md");
            var inRepo = files.Exists(repo);
            var inUser = files.Exists(user);
            var candidates = ConfigRegistry.StringArray([repo, user]);
            string? path = inRepo ? repo : inUser ? user : null;
            return new JsonObject
            {
                ["kind"] = "bare",
                ["path"] = path is null ? JsonValue.Null : path,
                ["level"] = inRepo ? "repo" : inUser ? "user" : JsonValue.Null,
                ["shadowed"] = inRepo && inUser ? user : JsonValue.Null,
                ["error"] = path is null ? "agent-not-found" : JsonValue.Null,
                ["candidates"] = candidates,
            };
        }

        var text = reference!.Value.AsString();
        var plugin = text[..colon];
        var agent = text[(colon + 1)..];
        var recordCount = 0;
        var paths = new List<string>();
        foreach (var (key, list) in InstalledPluginEntries(claudeDir))
        {
            if (key.Split('@')[0] != plugin)
            {
                continue;
            }

            foreach (var rec in list)
            {
                recordCount++;
                if (rec.IsTruthy && rec.Member("installPath") is { IsText: true } ip)
                {
                    paths.Add(NodePath.Join(ip.AsString(), "agents", agent + ".md"));
                }
            }
        }

        var found = paths.FirstOrDefault(files.Exists);
        return new JsonObject
        {
            ["kind"] = "plugin",
            ["path"] = found is null ? JsonValue.Null : found,
            ["level"] = found is null ? JsonValue.Null : "plugin",
            ["shadowed"] = JsonValue.Null,
            ["error"] = found is null ? "plugin-unresolvable" : JsonValue.Null,
            ["records"] = recordCount,
            ["candidates"] = ConfigRegistry.StringArray(paths),
        };
    }

    private string ReadHead(string path)
    {
        var bytes = files.ReadBytes(path);
        return Utf8Text.Decode(bytes.AsSpan(0, Math.Min(bytes.Length, AgentReadCapBytes)));
    }

    private static JsonObject Spread(JsonObject first, params (string Key, JsonValue Value)[] then)
    {
        var o = first.Clone();
        foreach (var (k, v) in then)
        {
            o[k] = v;
        }

        return o;
    }

    /// <summary>
    /// <c>readAgentFile</c>: locate an agent ref's file and parse its frontmatter. Never throws. Keys:
    /// <c>found, frontmatter, name, description, model, tools, disallowedTools, parseErrors, location</c> (<c>unreadable</c> when the read failed).
    /// </summary>
    public JsonObject ReadAgentFile(JsonValue? reference, string? cwd, string? claudeDir = null)
    {
        JsonObject location;
        try
        {
            location = LocateAgentFile(reference, cwd, claudeDir);
        }
        catch (Exception)
        {
            location = new JsonObject
            {
                ["kind"] = "bare",
                ["path"] = JsonValue.Null,
                ["level"] = JsonValue.Null,
                ["shadowed"] = JsonValue.Null,
                ["error"] = "agent-not-found",
                ["candidates"] = new JsonArray(),
            };
        }

        var empty = new JsonObject
        {
            ["found"] = JsonValue.Null,
            ["frontmatter"] = false,
            ["name"] = JsonValue.Null,
            ["description"] = JsonValue.Null,
            ["model"] = JsonValue.Null,
            ["tools"] = JsonValue.Null,
            ["disallowedTools"] = JsonValue.Null,
            ["parseErrors"] = new JsonArray(),
            ["location"] = location,
        };
        if (location.Get("path") is not { IsText: true } path)
        {
            return empty;
        }

        try
        {
            return Spread(PackAgentText.ParseAgentFrontmatter(ReadHead(path.AsString())), ("found", path), ("location", location));
        }
        catch (Exception)
        {
            return Spread(empty, ("found", path), ("unreadable", true));
        }
    }

    public JsonObject ParseAgentFrontmatter(string text) => PackAgentText.ParseAgentFrontmatter(text);

    /// <summary><c>{findings, fm}</c>: a pack agent's frontmatter read strictly.</summary>
    public JsonObject PackAgentParse(string text, string? path = null)
    {
        var (findings, fm) = PackAgentText.PackAgentParse(text, path);
        return new JsonObject { ["findings"] = findings, ["fm"] = fm };
    }

    public JsonArray PackAgentFindings(string text, string? path = null) => PackAgentText.PackAgentParse(text, path).Findings;

    /// <summary>
    /// <c>{effective, flagged}</c>: a pack agent's tools less <c>disallowedTools</c>, and the ones worth flagging. When the
    /// effective tools name MCP tools, <c>mcp</c> maps each server to its listed tools; when they hold an
    /// <c>mcp__&lt;server&gt;__*</c> grant, <c>server_grants</c> lists <c>{server, source, pinned}</c>. A server is the plugin's own
    /// when <paramref name="pluginDir"/>'s <c>.mcp.json</c> defines it.
    /// </summary>
    public JsonObject PackToolReport(JsonValue? fm, string? pluginDir = null)
    {
        var report = PackToolBasics(fm);
        var own = pluginDir is null ? [] : PluginServerPrefixes(pluginDir);
        var mcp = new JsonObject();
        var grants = new JsonArray();
        foreach (var t in report["effective"].AsArray())
        {
            if (!t.IsText || !t.AsString().StartsWith(PackAgentText.McpToolPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = t.AsString();
            if (PackAgentText.IsServerGrant(name))
            {
                var server = name[..^3];
                var pinned = own.Contains(server);
                grants.Add(new JsonObject { ["server"] = server, ["source"] = pinned ? "plugin" : "user", ["pinned"] = pinned });
                continue;
            }

            var cut = name.LastIndexOf("__", StringComparison.Ordinal);
            var key = cut > PackAgentText.McpToolPrefix.Length - 2 ? name[..cut] : name;
            var list = mcp.Get(key) is { IsArray: true } l ? l.AsArray() : new JsonArray();
            list.Add(name);
            mcp[key] = list;
        }

        if (mcp.Count > 0)
        {
            report["mcp"] = mcp;
        }

        if (grants.Count > 0)
        {
            report["server_grants"] = grants;
        }

        return report;
    }

    /// <summary>
    /// The text lines for a <see cref="PackToolReport"/>: one ⚠ line per server grant first, then one flagged line per MCP server
    /// whose tools are listed by name.
    /// </summary>
    public IReadOnlyList<string> PackToolLines(JsonValue? report, string? pluginDir = null)
    {
        var lines = new List<string>();
        var own = pluginDir is null ? [] : PluginServerPrefixes(pluginDir);
        if (report?.Member("server_grants") is { IsArray: true } grants)
        {
            foreach (var g in grants.AsArray())
            {
                var source = g.Member("pinned") is { IsTruthy: true } ? "plugin's own .mcp.json (pinned)" : "a server you configure (not pinned)";
                lines.Add($"⚠ grants EVERY tool of MCP server {JsConvert.ToJsString(g.Member("server"))}, including tools it adds later; Ahd cannot see or pin that list — source: {source}");
            }
        }

        if (report?.Member("mcp") is { IsMap: true } mcp)
        {
            foreach (var key in mcp.AsObject().Keys)
            {
                var names = mcp.AsObject()[key].AsArray().Select(x => x.AsString()).ToList();
                var shown = string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : string.Empty);
                var source = own.Contains(key) ? "plugin's own .mcp.json" : "not in this plugin";
                lines.Add($"MCP server {key}: {names.Count} tools listed ({shown}) — source: {source}");
            }
        }

        return lines;
    }

    /// <summary>The <c>mcp__plugin_&lt;plugin&gt;_&lt;server&gt;</c> prefixes of the servers the plugin's own <c>.mcp.json</c> defines.</summary>
    private HashSet<string> PluginServerPrefixes(string pluginDir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (TryReadJsonStrict(NodePath.Join(pluginDir, ".mcp.json"))?.Member("mcpServers") is { IsMap: true } servers)
        {
            var plugin = PluginNameAt(pluginDir);
            foreach (var key in servers.AsObject().Keys)
            {
                set.Add($"{PackAgentText.McpToolPrefix}plugin_{plugin}_{key}");
            }
        }

        return set;
    }

    private static JsonObject PackToolBasics(JsonValue? fm)
    {
        var disallowed = fm?.Member("disallowedTools") is { IsArray: true } d ? d.AsArray().Select(x => x).ToList() : [];
        var effective = new JsonArray();
        var flagged = new JsonArray();
        if (fm?.Member("tools") is { IsArray: true } tools)
        {
            foreach (var t in tools.AsArray())
            {
                if (disallowed.Any(x => JsonEquals(x, t)))
                {
                    continue;
                }

                effective.Add(t);
                if (t.IsText && (PackAgentText.FlaggedTools.Contains(t.AsString()) || t.AsString().StartsWith(PackAgentText.McpToolPrefix, StringComparison.Ordinal)))
                {
                    flagged.Add(t);
                }
            }
        }

        return new JsonObject { ["effective"] = effective, ["flagged"] = flagged };
    }

    private static bool JsonEquals(JsonValue a, JsonValue b) =>
        a.IsText && b.IsText ? a.AsString() == b.AsString() : a.Kind == b.Kind && JsonText.Stringify(a, 0) == JsonText.Stringify(b, 0);

    public JsonValue NormalizeDescription(JsonValue? text) =>
        PackAgentText.NormalizeDescription(text) is { } s ? s : JsonValue.Null;

    // ---------------------------------------------------------------- the class-contract validator

    private static JsonObject DescriptionOf(string? text, string? source) => new()
    {
        ["text"] = text is null ? JsonValue.Null : text,
        ["source"] = source is null ? JsonValue.Null : source,
    };

    /// <summary>
    /// <c>validateAgentContract({role, cls, agent, description, builtin, cwd, inMemory})</c>: checks one custom row, or one built-in
    /// override, against its class's contract. Returns <c>{findings, file, description}</c>. <paramref name="spec"/> carries
    /// <c>role, cls, agent</c>, and optionally <c>description, builtin, cwd, inMemory: {path, text, fm?}</c>.
    /// </summary>
    public JsonObject ValidateAgentContract(JsonObject spec)
    {
        var role = JsConvert.ToJsString(spec.Get("role"));
        var cls = JsConvert.ToJsString(spec.Get("cls"));
        var agentValue = spec.Get("agent");
        var agent = agentValue is { IsText: true } at ? at.AsString() : throw new ArgumentException("agent must be text", nameof(spec));
        var description = spec.Get("description") is { IsTruthy: true } dv ? dv : (JsonValue?)null;
        var builtin = spec.Get("builtin") is { IsTruthy: true };
        var cwd = spec.Get("cwd") is { IsText: true } cv ? cv.AsString() : null;
        var claudeDir = spec.Get(ClaudeDirKey) is { IsText: true } cd ? cd.AsString() : null;
        var inMemory = spec.Get("inMemory") is { IsMap: true } im ? im.AsObject() : null;

        var findings = new JsonArray();
        JsonObject file;
        if (inMemory is not null)
        {
            var memPath = inMemory.Get("path") is { IsText: true } mp ? mp.AsString() : null;
            var fm = inMemory.Get("fm") is { IsMap: true } f ? f.AsObject() : PackAgentText.ParseAgentFrontmatter(inMemory.Get("text") is { IsText: true } mt ? mt.AsString() : "undefined");
            file = Spread(fm, ("found", memPath is null ? JsonValue.Null : memPath), ("location", new JsonObject
            {
                ["kind"] = "bare",
                ["path"] = memPath is null ? JsonValue.Null : memPath,
                ["level"] = JsonValue.Null,
                ["shadowed"] = JsonValue.Null,
                ["error"] = JsonValue.Null,
                ["candidates"] = ConfigRegistry.StringArray(memPath is null ? [] : [memPath]),
            }));
        }
        else
        {
            file = ReadAgentFile(agent, cwd, claudeDir);
        }

        var loc = file["location"].AsObject();
        JsonArray ScaffoldFix() => PackAgentText.Fixes(PackAgentText.Fix("scaffold",
            $"ah roster role set {role} --scaffold repo (or --scaffold user) writes a template that passes the {cls} contract"));

        var normalized = file.Get("description") is { IsTruthy: true } fd ? PackAgentText.NormalizeDescription(fd) : null;
        var effDescription = description is { } desc
            ? new JsonObject { ["text"] = desc, ["source"] = "config" }
            : normalized is not null ? DescriptionOf(normalized, "frontmatter") : DescriptionOf(null, null);

        JsonObject Result() => new() { ["findings"] = findings, ["file"] = file, ["description"] = effDescription };
        var error = loc.Get("error") is { IsText: true } err ? err.AsString() : null;
        var candidates = loc.Get("candidates") is { IsArray: true } cs ? cs.AsArray().Select(c => JsConvert.ToJsString(c)).ToList() : [];
        if (error == "agent-not-found")
        {
            var fixes = new JsonArray();
            if (!builtin)
            {
                foreach (var f in ScaffoldFix())
                {
                    fixes.Add(f);
                }
            }

            foreach (var p in candidates)
            {
                fixes.Add(PackAgentText.Fix("create-file", p));
            }

            findings.Add(PackAgentText.Finding("error", "agent-not-found", null, null,
                $"No agent file for {JsonText.Stringify(agent, 0)}: looked in {string.Join(" and ", candidates)}.", fixes));
            return Result();
        }

        if (error == "plugin-unresolvable")
        {
            var why = loc.Get("records") is { IsTruthy: true }
                ? $"no agents/{(agent.Contains(':', StringComparison.Ordinal) ? agent.Split(':')[1] : "undefined")}.md under its install path(s)"
                : "the plugin has no install record in ~/.claude/plugins/installed_plugins.json";
            findings.Add(PackAgentText.Finding("error", "plugin-unresolvable", null, null, $"Cannot validate {JsonText.Stringify(agent, 0)}: {why}.",
                PackAgentText.Fixes(PackAgentText.Fix("copy-agent",
                    $"copy the agent into .claude/agents/{role}.md as a user-owned file, then point the role at the bare name {role}"))));
            return Result();
        }

        var path = file.Get("found") is { IsText: true } pf ? pf.AsString() : null;
        if (file.Get("unreadable") is { IsTruthy: true })
        {
            findings.Add(PackAgentText.Finding("error", "no-frontmatter", path, null, $"{path ?? "undefined"} could not be read.",
                PackAgentText.Fixes(PackAgentText.Fix("edit-frontmatter", $"make {path ?? "undefined"} readable, with a --- frontmatter block"))));
            return Result();
        }

        if (file.Get("frontmatter") is not { IsTruthy: true })
        {
            var last = agent.Split(':')[^1];
            findings.Add(PackAgentText.Finding("error", "no-frontmatter", path, null,
                $"{path ?? "undefined"} has no --- frontmatter block, so Claude Code cannot load it as an agent.",
                PackAgentText.Fixes(PackAgentText.Fix("edit-frontmatter", $"add a --- block at the top with name: {last} and a description"))));
            return Result();
        }

        var parseErrors = file["parseErrors"].AsArray().Select(e => e.AsString()).ToList();
        foreach (var field in parseErrors)
        {
            findings.Add(PackAgentText.Finding("error", "unparseable-field", path, field, $"Frontmatter {field} is present but cannot be parsed.",
                PackAgentText.Fixes(PackAgentText.Fix("edit-frontmatter", field is "tools" or "disallowedTools"
                    ? $"write {field} as a comma list (Read, Grep), a [flow, list] or a block list of - items"
                    : $"write {field} as a plain one-line value"))));
        }

        var expectedName = agent.Split(':')[^1];
        var fileName = file.Get("name") is { IsText: true } nm ? nm.AsString() : null;
        if (!parseErrors.Contains("name") && fileName != expectedName)
        {
            findings.Add(PackAgentText.Finding("error", "name-mismatch", path, "name",
                $"Frontmatter name is {(fileName is null ? "null" : JsonText.Stringify(fileName, 0))}, but Claude Code loads this agent by name {JsonText.Stringify(expectedName, 0)}.",
                PackAgentText.Fixes(PackAgentText.Fix("edit-frontmatter", $"set name: {expectedName}"))));
        }

        var classSpec = ConfigRegistry.Class(cls) ?? throw new ArgumentException("unknown class " + cls, nameof(spec));
        var allowed = classSpec.Models;
        if (file.Get("model") is { IsText: true } model && !parseErrors.Contains("model") && !allowed.Contains(PackAgentText.ModelAlias(model.AsString())!))
        {
            findings.Add(PackAgentText.Finding("error", "model-not-allowed", path, "model",
                $"Frontmatter model {JsonText.Stringify(model, 0)} is outside the {cls} allowlist ({string.Join(", ", allowed)}); it runs whenever the row is inherit or a subagent is dispatched without a model.",
                PackAgentText.Fixes(
                    PackAgentText.Fix("edit-frontmatter", "remove model: from the agent file (the row's model then governs)"),
                    PackAgentText.Fix("edit-frontmatter", $"set model: to one of {string.Join(", ", allowed)}"))));
        }

        // adr-41: a server-wide grant only for implement and legwork, whether or not the class has a contract map.
        if (cls is not ("implement" or "legwork") && !parseErrors.Contains("tools") && file.Get("tools") is { IsArray: true } grantTools)
        {
            foreach (var entry in grantTools.AsArray().Select(x => x.AsString()).Where(PackAgentText.IsServerGrant))
            {
                findings.Add(PackAgentText.Finding("error", "pack-agent-tools", path, "tools",
                    $"tools entry {JsonText.Stringify(entry, 0)}: a server-wide grant cannot be checked against the {cls} contract (it may include write tools)",
                    PackAgentText.Fixes(PackAgentText.Fix("edit-frontmatter", $"list the {entry[..^3]} tools this agent needs by name"))));
            }
        }

        var contract = ConfigRegistry.ClassesJson().Get(cls)!.Value.AsObject().Get("contract");
        if (contract is { IsMap: true } contractValue && !parseErrors.Contains("tools") && !parseErrors.Contains("disallowedTools"))
        {
            ContractFindings(contractValue.AsObject(), cls, path, file, findings);
        }

        if (!builtin && !effDescription["text"].IsTruthy)
        {
            findings.Add(PackAgentText.Finding("warn", "no-description", path, "description",
                "No config description and no frontmatter description: a side role's directive line would be empty.",
                PackAgentText.Fixes(PackAgentText.Fix("set-description", $"ah roster role set {role} --description \"<one line>\""))));
        }

        if (loc.Get("shadowed") is { IsTruthy: true } shadowed)
        {
            var shadowedText = JsConvert.ToJsString(shadowed);
            findings.Add(PackAgentText.Finding("warn", "shadowed-agent", path, null,
                $"{JsonText.Stringify(agent, 0)} exists at both levels: {path ?? "undefined"} was validated and loads here; {shadowedText} is shadowed in this repo.",
                PackAgentText.Fixes(PackAgentText.Fix("remove-file",
                    $"delete or rename the shadowed user file {shadowedText}, or rename the repo agent {path ?? "undefined"}"))));
        }

        return Result();
    }

    private static void ContractFindings(JsonObject contract, string cls, string? path, JsonObject file, JsonArray findings)
    {
        var tools = file.Get("tools") is { IsArray: true } t ? t.AsArray().Select(x => x.AsString()).ToList() : null;
        var disallowed = file.Get("disallowedTools") is { IsTruthy: true } d && d.IsArray ? d.AsArray().Select(x => x.AsString()).ToList() : [];
        var allowlist = tools is not null && !tools.Contains("*");
        static bool Lists(List<string> list, string tool) => list.Any(e => e == tool || e.StartsWith(tool + "(", StringComparison.Ordinal));
        bool Present(string tool) => (!allowlist || Lists(tools!, tool)) && !Lists(disallowed, tool);
        JsonObject AddFix(string tool) => Lists(disallowed, tool)
            ? PackAgentText.Fix("edit-frontmatter", $"remove {tool} from disallowedTools")
            : PackAgentText.Fix("edit-frontmatter", $"add {tool} to tools (you use an allowlist)");
        JsonObject RemoveFix(string tool) => allowlist
            ? PackAgentText.Fix("edit-frontmatter", $"remove {tool} from tools")
            : PackAgentText.Fix("edit-frontmatter", $"add {tool} to disallowedTools");

        foreach (var req in contract["required"].AsArray())
        {
            var alts = req.IsArray ? req.AsArray().Select(a => a.AsString()).ToList() : [req.AsString()];
            if (alts.Any(Present))
            {
                continue;
            }

            var fixes = new JsonArray();
            foreach (var a in alts)
            {
                fixes.Add(AddFix(a));
            }

            findings.Add(PackAgentText.Finding("error", $"missing-tool:{string.Join('|', alts)}", path, "tools",
                $"The {cls} contract needs {string.Join(" or ", alts)}, and this agent's effective tools lack {(alts.Count > 1 ? "all of them" : "it")}.", fixes));
        }

        foreach (var tool in contract["forbidden"].AsArray().Select(x => x.AsString()))
        {
            if (!Present(tool))
            {
                continue;
            }

            findings.Add(PackAgentText.Finding("error", $"forbidden-tool:{tool}", path, "tools",
                $"A {cls}-class agent must not have {tool}: a reviewer that can edit ends up validating its own fixes.",
                PackAgentText.Fixes(RemoveFix(tool), PackAgentText.Fix("change-class", "implement"))));
        }

        foreach (var tool in contract["discouraged"].AsArray().Select(x => x.AsString()))
        {
            if (Present(tool))
            {
                findings.Add(PackAgentText.Finding("warn", $"discouraged-tool:{tool}", path, "tools", $"{tool} is outside the {cls} convention.", PackAgentText.Fixes(RemoveFix(tool))));
            }
        }

        foreach (var tool in contract["missingRecommended"].AsArray().Select(x => x.AsString()))
        {
            if (!Present(tool))
            {
                findings.Add(PackAgentText.Finding("warn", $"missing-recommended:{tool}", path, "tools",
                    $"A {cls}-class agent is expected to have {tool}.", PackAgentText.Fixes(AddFix(tool))));
            }
        }

        foreach (var entry in tools ?? [])
        {
            if (PackAgentText.ScopedToolName(entry) is not { } scoped)
            {
                continue;
            }

            findings.Add(PackAgentText.Finding("warn", "scoped-tool-entry", path, "tools",
                $"tools entry {JsonText.Stringify(entry, 0)} grants the whole {scoped} tool: Claude Code does not enforce the scope.",
                PackAgentText.Fixes(PackAgentText.Fix("edit-frontmatter",
                    $"remove {entry} from tools, or list plain {scoped} knowingly; to restrict a tool, add it to disallowedTools"))));
        }
    }

    public bool HasContractErrors(JsonValue? findings) =>
        findings is { IsArray: true } a && a.AsArray().Any(f => f.Member("level") is { IsText: true } l && l.AsString() == "error");

    public string FormatFindings(JsonArray findings) =>
        string.Join('\n', findings.Select(f =>
        {
            var lines = new List<string> { $"{JsConvert.ToJsString(f.Member("level")).ToUpperInvariant()} {JsConvert.ToJsString(f.Member("code"))}: {JsConvert.ToJsString(f.Member("message"))}" };
            if (f.Member("fix") is { IsArray: true } fixes)
            {
                lines.AddRange(fixes.AsArray().Select(x => $"  fix: [{JsConvert.ToJsString(x.Member("kind"))}] {JsConvert.ToJsString(x.Member("detail"))}"));
            }

            return string.Join('\n', lines);
        }));

    // ---------------------------------------------------------------- packs and registry views

    /// <summary>An adopted role's pack findings: its pack reason when it isn't <c>ok</c>, then the pack-agent frontmatter checks.</summary>
    public JsonArray PackFindings(string role, JsonObject pack, JsonObject? parsed = null)
    {
        var findings = new JsonArray();
        var status = pack.Get("status") is { IsText: true } s ? s.AsString() : null;
        if (status != "ok")
        {
            JsonObject fix = status is "pack-changed" or "pack-untrusted-here"
                ? PackAgentText.Fix("trust", $"review it with `ah roster role trust {role} --dry-run`, then commit with the pin it prints")
                : status == "pack-ambiguous"
                    ? PackAgentText.Fix("uninstall", "uninstall all but one install of the plugin")
                    : PackAgentText.Fix("remove", $"`ah roster role remove {role}`, or reinstall the pack");
            var subcode = pack.Get("subcode") is { IsTruthy: true } sc ? JsConvert.ToJsString(sc) : null;
            var tail = subcode is not null && subcode != status ? $" [{subcode}]" : string.Empty;
            findings.Add(PackAgentText.Finding("error", status ?? "undefined", pack.Get("agentPath") is { IsText: true } ap ? ap.AsString() : null, null,
                $"{role} (from {JsConvert.ToJsString(pack.Get("from"))}): {JsConvert.ToJsString(pack.Get("message"))}{tail}", PackAgentText.Fixes(fix)));
        }

        if (parsed is not null)
        {
            if (parsed.Get("findings") is { IsArray: true } pf)
            {
                findings.AddRange(pf.AsArray());
            }
        }
        else if (pack.Get("agentText") is { IsText: true } text)
        {
            findings.AddRange(PackAgentText.PackAgentParse(text.AsString(), pack.Get("agentPath") is { IsText: true } p ? p.AsString() : null).Findings);
        }

        return findings;
    }

    /// <summary>
    /// <c>validateRole</c>: validates one registry role when it needs validating (every custom row and every built-in override;
    /// this plugin's own shipped agents are trusted and give null). Returns <c>{findings, file, description}</c>.
    /// </summary>
    public JsonValue ValidateRole(string role, JsonObject? resolved)
    {
        var entry = resolved?.Get("roles")?.Member(role);
        if (entry is not { IsTruthy: true } e)
        {
            return JsonValue.Null;
        }

        var pack = e.Member("pack") is { IsMap: true } pm ? pm.AsObject() : null;
        var packTruthy = e.Member("pack") is { IsTruthy: true };
        (JsonArray Findings, JsonObject Fm)? parsed = null;
        JsonObject? inMemory = null;
        if (pack is not null && pack.Get("agentText") is { IsText: true } agentText)
        {
            var agentPath = pack.Get("agentPath") is { IsText: true } ap ? ap.AsString() : null;
            parsed = PackAgentText.PackAgentParse(agentText.AsString(), agentPath);
            inMemory = new JsonObject
            {
                ["path"] = agentPath is null ? JsonValue.Null : agentPath,
                ["text"] = agentText,
                ["fm"] = parsed.Value.Fm,
            };
        }

        JsonObject result;
        var cwd = resolved?.Get("cwd") is { IsText: true } cv ? cv : (JsonValue?)null;
        var claudeDir = resolved?.Get(ClaudeDirKey) is { IsText: true } cd ? cd : (JsonValue?)null;
        if (ConfigRegistry.IsBuiltinRole(role))
        {
            if (!IsOverride(role, e))
            {
                return JsonValue.Null;
            }

            var spec = new JsonObject { ["role"] = role, ["cls"] = ConfigRegistry.BuiltinClass(role)!, ["agent"] = ConfigRegistry.RoleAgent(role, e, PluginName), ["builtin"] = true };
            if (cwd is not null)
            {
                spec["cwd"] = cwd.Value;
            }

            if (claudeDir is not null)
            {
                spec[ClaudeDirKey] = claudeDir.Value;
            }

            if (inMemory is not null)
            {
                spec["inMemory"] = inMemory;
            }

            result = ValidateAgentContract(spec);
        }
        else
        {
            var spec = new JsonObject { ["role"] = role, ["cls"] = e.Member("class") ?? JsonValue.Null, ["agent"] = e.Member("agent") ?? JsonValue.Null };
            if (e.Member("description") is { IsTruthy: true } description)
            {
                spec["description"] = description;
            }

            if (cwd is not null)
            {
                spec["cwd"] = cwd.Value;
            }

            if (claudeDir is not null)
            {
                spec[ClaudeDirKey] = claudeDir.Value;
            }

            if (inMemory is not null)
            {
                spec["inMemory"] = inMemory;
            }

            result = ValidateAgentContract(spec);
        }

        if (!packTruthy)
        {
            return result;
        }

        var merged = new JsonArray();
        var parsedObject = parsed is null ? null : new JsonObject { ["findings"] = parsed.Value.Findings, ["fm"] = parsed.Value.Fm };
        merged.AddRange(PackFindings(role, pack ?? new JsonObject(), parsedObject));
        merged.AddRange(result["findings"].AsArray());
        return Spread(result, ("findings", merged));
    }

    /// <summary>The agents adopted pack roles resolve to — a <c>from</c> row's, or a built-in's agent a pack overrides.</summary>
    public IReadOnlyList<string> PackAgentRefs(JsonObject resolved)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var role in ConfigRegistry.RegistryRoles(resolved))
        {
            var entry = resolved.Get("roles")?.Member(role);
            if (entry is { IsTruthy: true } e && e.Member("from") is { IsTruthy: true })
            {
                var agent = ConfigRegistry.RoleAgent(role, e, PluginName);
                if (seen.Add(agent))
                {
                    ordered.Add(agent);
                }
            }
        }

        return ordered;
    }

    public string SubagentType(string role, JsonValue? entry)
    {
        if (role == "task-runner" && entry?.Member("delegate") is { IsText: true } d && d.AsString() == "task-gopher" && !IsOverride(role, entry))
        {
            return TaskGopher;
        }

        return ConfigRegistry.RoleAgent(role, entry, PluginName);
    }

    /// <summary>
    /// A custom row is an in-chain alternative: a chain class with <c>routes</c>. JavaScript's <c>&amp;&amp;</c> chain gives <c>false</c>,
    /// or <c>undefined</c> (null here) when the row's class is not in the table.
    /// </summary>
    public JsonValue? IsAlternative(string role, JsonValue? entry)
    {
        if (ConfigRegistry.IsBuiltinRole(role))
        {
            return ConfigRegistry.BuiltinRoutes(role) is not null
                && entry?.Member("routes") is { IsText: true } builtinRoutes && builtinRoutes.AsString().Length > 0;
        }

        if (entry is not { IsTruthy: true } e)
        {
            return false;
        }

        var cls = e.Member("class") is { IsText: true } c ? ConfigRegistry.Class(c.AsString()) : null;
        if (cls is null)
        {
            return null;
        }

        if (!cls.Chain)
        {
            return false;
        }

        return e.Member("routes") is { IsText: true } r && r.AsString().Length > 0;
    }

    public string CustomTierText(JsonObject? resolved)
    {
        var labels = ConfigRegistry.CustomRoleNames(resolved)
            .Where(r => ConfigRegistry.ClassOfRole(r, resolved)?.Tier == true)
            .Select(r => JsConvert.ToJsString(RoleLabel(r, resolved)))
            .ToList();
        return labels.Count > 0 ? ", and custom: " + string.Join(", ", labels) : string.Empty;
    }

    /// <summary>
    /// <c>availabilityView</c>: the directive's view of the registry: a custom row that fails its class contract is dropped from the
    /// view, an override that fails reverts to the shipped agent, and an available custom row gains its effective description.
    /// Returns <c>{view, unavailable}</c>.
    /// </summary>
    public JsonObject AvailabilityView(JsonObject resolved)
    {
        if (resolved.Get("roles") is null or { IsNull: true })
        {
            throw new InvalidOperationException("resolved.roles is not an object");
        }

        var roles = resolved.Get("roles") is { IsMap: true } rv ? rv.AsObject().Clone() : new JsonObject();
        var unavailable = new JsonArray();
        foreach (var role in ConfigRegistry.RegistryRoles(resolved))
        {
            var entry = resolved.Get("roles")?.Member(role);
            var builtin = ConfigRegistry.IsBuiltinRole(role);
            if (builtin && !IsOverride(role, entry))
            {
                continue;
            }

            JsonValue result;
            try
            {
                result = ValidateRole(role, resolved);
            }
            catch (Exception)
            {
                result = new JsonObject
                {
                    ["findings"] = PackAgentText.Fixes(PackAgentText.Finding("error", "agent-not-found", null, null, "validation failed", NoFixes())),
                    ["description"] = new JsonObject { ["text"] = JsonValue.Null },
                };
            }

            if (!result.IsTruthy)
            {
                continue;
            }

            var failed = HasContractErrors(result.Member("findings"));
            if (builtin)
            {
                if (!failed)
                {
                    continue;
                }

                var rest = entry!.Value.AsObject().Clone();
                var agent = rest.Get("agent");
                rest.Remove("agent");
                roles[role] = rest;
                unavailable.Add($"{role} ({(agent is null ? "undefined" : JsConvert.ToJsString(agent))})");
                continue;
            }

            if (failed)
            {
                roles.Remove(role);
                unavailable.Add(role);
                continue;
            }

            var withDescription = entry!.Value.AsObject().Clone();
            withDescription["effectiveDescription"] = result.Member("description")?.Member("text") ?? JsonValue.Null;
            roles[role] = withDescription;
        }

        var view = resolved.Clone();
        view["roles"] = roles;
        return new JsonObject { ["view"] = view, ["unavailable"] = unavailable };
    }

    // ---------------------------------------------------------------- teams

    /// <summary><c>legworkHandedOff</c>: a claude-kind legwork member with no model whose legwork task-gopher takes (asked last).</summary>
    public bool LegworkHandedOff(JsonValue? member, JsonObject? registry, Func<bool> handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        if (member is not { IsTruthy: true } m)
        {
            throw new ArgumentException("member is not an object", nameof(member));
        }

        var kind = ResolveKind(m);
        return kind.IsText && kind.AsString() == ConfigRegistry.KindDefault
               && !(m.Member("model") is { IsTruthy: true })
               && m.Member("role") is { IsText: true } role && ConfigRegistry.RoleClass(role.AsString(), registry) == "legwork"
               && handoff();
    }

    /// <summary>
    /// Whether a team is partial: some roster member that <c>create</c> would launch has no record, matched by its derived name or a
    /// record's <c>renamed_from</c>. Null when the team's recorded roster block no longer resolves (either answer would be a guess).
    /// </summary>
    public JsonValue TeamIsPartial(string dir, string? cwd, string? teamName, JsonObject? team, JsonObject? registry = null)
    {
        if (team is null || team.Get("members") is not { IsArray: true } records || string.IsNullOrEmpty(cwd))
        {
            return JsonValue.Null;
        }

        var rosterKey = Teams.TeamRosterEntry(dir, teamName)?.Key;
        var r = ResolveRosterObject(cwd, rosterKey, TeamPrefix(cwd, teamName), registry);
        var resolvedKey = r?.Get("teamKey") is { IsText: true } rk && rk.AsString().Length > 0 ? rk.AsString() : null;
        if (r is null || resolvedKey != (string.IsNullOrEmpty(rosterKey) ? null : rosterKey))
        {
            return JsonValue.Null;
        }

        bool? installed = null;
        bool Handoff() => installed ??= LocateAgentFile(TaskGopher, cwd).Get("path") is { IsText: true } p && p.AsString().Length > 0;
        var blockRoute = r.Get("route");
        var members = r["members"].AsArray();
        foreach (var m in members)
        {
            var route = m.Member("route") is { IsTruthy: true } mr ? mr : blockRoute;
            if (!RouteHasPane(route) || LegworkHandedOff(m, registry, Handoff))
            {
                continue;
            }

            var name = m.Member("name");
            var recorded = records.AsArray().Any(rec =>
                rec.IsTruthy && (JsonSame(rec.Member("name"), name) || JsonSame(rec.Member("renamed_from"), name)));
            if (!recorded)
            {
                return true;
            }
        }

        return false;
    }

    private static bool JsonSame(JsonValue? a, JsonValue? b) =>
        a is { } x && b is { } y && x.Kind == y.Kind && (x.IsText ? x.AsString() == y.AsString() : x.IsNumber ? x.AsNumber() == y.AsNumber() : x.IsBool && x.AsBool() == y.AsBool());

    /// <summary>The lead line of every surface that shows several owned teams.</summary>
    public string OwnedTeamsLead(JsonArray teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        var names = teams.Select(t => t.IsNull ? "@default" : JsConvert.ToJsString(t));
        return $"You own {teams.Count} live teams: {string.Join(", ", names)}. Pass --team <name> to ah roster team verbs and to ah msg new; a member name already says its team.";
    }

    /// <summary>
    /// For a session that owns more than one live team: one <c>{team, resolved}</c> per owned team, each resolved as that team, in
    /// <c>ownedTeams</c> order. Null for a session with at most one.
    /// </summary>
    public JsonArray? OwnedTeamConfigs(JsonObject? resolved, ResolveOptions? options = null)
    {
        if (resolved?.Get("ownedTeams") is not { IsArray: true } owned || owned.AsArray().Count < 2)
        {
            return null;
        }

        var cwd = resolved.Get("cwd") is { IsText: true } c ? c.AsString() : null;
        var teamHome = HierarchyDir(cwd);
        var result = new JsonArray();
        foreach (var team in owned.AsArray())
        {
            var o = options ?? new ResolveOptions();
            o = team.IsNull ? o with { DefaultTeam = true, TeamHome = teamHome } : o with { Team = team.IsText ? team.AsString() : o.Team, TeamHome = teamHome };
            result.Add(new JsonObject { ["team"] = team, ["resolved"] = ResolveConfig(cwd, o) });
        }

        return result;
    }
}
