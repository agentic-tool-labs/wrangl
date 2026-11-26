using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Digests;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

internal sealed record InstallRecord(string Key, string Plugin, string? Marketplace, string InstallPath, string? Version, string? Scope);

internal sealed class PackTree
{
    internal List<(string Rel, string Sha)> Files { get; init; } = [];

    internal string? Symlink { get; init; }

    internal string? Error { get; init; }
}

internal sealed class PackClaims
{
    internal string? Error { get; set; }

    internal Dictionary<string, List<string>> ByRole { get; } = new(StringComparer.Ordinal);

    /// <summary>The roles each claiming file takes, in the order the claims were found.</summary>
    internal Dictionary<string, List<string>> Files { get; } = new(StringComparer.Ordinal);
}

/// <summary>One install record's view of a pack role, and what the resolver reads off it.</summary>
internal sealed class PackState
{
    internal string Status { get; set; } = "ok";

    internal string? Message { get; set; }

    internal string? Subcode { get; set; }

    internal JsonObject? ManifestRole { get; set; }

    /// <summary>Null is JavaScript <c>undefined</c>; <see cref="JsonValue.Null"/> is <c>null</c>.</summary>
    internal JsonValue? AgentText { get; set; }

    internal JsonValue? AgentPath { get; set; }

    internal JsonArray? Files { get; set; }

    internal string? Pin { get; set; }

    internal InstallRecord? Record { get; set; }

    internal (string Plugin, string? Marketplace, string Role)? From { get; set; }

    internal List<string> Records { get; set; } = [];

    internal PackState Copy() => (PackState)MemberwiseClone();
}

internal sealed partial class ConfigCore
{
    private readonly Dictionary<string, PackTree> _packTrees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackClaims> _packClaims = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------- install records

    internal List<InstallRecord> InstallRecords()
    {
        var records = new List<InstallRecord>();
        var data = TryReadJsonStrict(NodePath.Join(ClaudeDirOr(environment.GetVariable("CLAUDE_CONFIG_DIR")), "plugins", "installed_plugins.json"));
        if (data is null)
        {
            return records;
        }

        var plugins = data.Value.Member("plugins");
        var entries = new List<(string Key, JsonValue List)>();
        if (plugins is { IsMap: true } map)
        {
            foreach (var key in map.AsObject().Keys)
            {
                entries.Add((key, map.AsObject()[key]));
            }
        }
        else if (plugins is { IsArray: true } arr)
        {
            for (var i = 0; i < arr.AsArray().Count; i++)
            {
                entries.Add((i.ToString(System.Globalization.CultureInfo.InvariantCulture), arr.AsArray()[i]));
            }
        }

        foreach (var (key, list) in entries)
        {
            if (!list.IsArray)
            {
                continue;
            }

            var at = key.IndexOf('@', StringComparison.Ordinal);
            var plugin = at == -1 ? key : key[..at];
            var marketplace = at == -1 ? null : key[(at + 1)..];
            foreach (var r in list.AsArray())
            {
                if (r.Member("installPath") is not { IsText: true } installPath)
                {
                    continue;
                }

                records.Add(new InstallRecord(
                    key,
                    plugin,
                    marketplace,
                    installPath.AsString(),
                    r.Member("version") is { IsText: true } v ? v.AsString() : null,
                    r.Member("scope") is { IsText: true } s ? s.AsString() : null));
            }
        }

        return records;
    }

    /// <summary><c>JSON.parse(readFileSync(path, "utf8"))</c> in a try, with no existence check first.</summary>
    private JsonValue? TryReadJsonStrict(string path)
    {
        try
        {
            return JsonText.Parse(files.ReadText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- pack manifest

    internal JsonObject CheckCustomRowCore(string name, JsonValue raw)
    {
        static JsonObject Error(string message) => new() { ["error"] = message };

        var nameErr = ConfigRegistry.RoleNameError(name);
        if (nameErr is not null)
        {
            return Error(nameErr);
        }

        if (!raw.IsMap)
        {
            return Error("row must be an object");
        }

        var r = raw.AsObject();
        var clsName = r.Get("class") is { IsText: true } c ? c.AsString() : null;
        var cls = ConfigRegistry.Class(clsName);
        if (cls is null)
        {
            return Error($"class is required, one of {string.Join(", ", ConfigRegistry.ClassNames)}");
        }

        var warnings = new JsonArray();
        var agent = r.Get("agent") ?? name;
        var agentErr = ConfigRegistry.AgentRefError(agent);
        if (agentErr is not null)
        {
            return Error(agentErr);
        }

        if (agent.AsString().StartsWith(PluginName + ":", StringComparison.Ordinal))
        {
            return Error($"{PluginName}:* agents belong to the built-ins");
        }

        var label = r.Get("label") ?? ConfigRegistry.DefaultLabel(name);
        if (!label.IsText || !ConfigRegistry.LabelRegex().IsMatch(label.AsString()))
        {
            return Error("label must match /^[A-Za-z0-9][A-Za-z0-9 -]{0,31}$/");
        }

        var row = new JsonObject { ["class"] = clsName!, ["agent"] = agent, ["label"] = label };
        if (r.Get("description") is { } description)
        {
            var e = ConfigRegistry.OneLineError("description", description);
            if (e is not null)
            {
                return Error(e);
            }

            row["description"] = description;
        }

        if (r.Get("routes") is { } routes)
        {
            var e = ConfigRegistry.OneLineError("routes", routes);
            if (e is not null)
            {
                return Error(e);
            }

            if (cls.Chain)
            {
                row["routes"] = routes;
            }
            else
            {
                warnings.Add("routes is ignored on a legwork role — legwork is never routed within the chain");
            }
        }

        var rawModel = r.Get("model");
        if (clsName == "advise" && (rawModel is null || (rawModel.Value.IsText && rawModel.Value.AsString() == "inherit")))
        {
            return Error($"an advise-class role needs an explicit model ({string.Join(", ", cls.Models)})");
        }

        var model = rawModel ?? "inherit";
        if (!model.IsText || !cls.Models.Contains(model.AsString()))
        {
            return Error($"model {JsonText.Stringify(model, 0)} is not allowed for class {clsName} (allowed: {string.Join(", ", cls.Models)})");
        }

        row["model"] = model;
        if (r.Get("shell") is { IsText: true } shell && shell.AsString() == "self-care")
        {
            row["shell"] = shell;
        }

        // a malformed params is dropped, never the role
        if (r.Get("params") is { } declared)
        {
            if (ConfigRegistry.ParamsError(declared) is { } why)
            {
                warnings.Add($"params ignored: {why}");
            }
            else
            {
                row["params"] = declared;
            }
        }

        // ah2-562: only a review-class row keeps writes; a malformed one is dropped, never the role, so the role writes nothing
        if (clsName == "review" && r.Get("writes") is { } writes)
        {
            if (ConfigRegistry.WritesError(writes) is { } why)
            {
                warnings.Add($"writes ignored: {why}");
            }
            else
            {
                row["writes"] = writes;
            }
        }

        if (cls.Chain)
        {
            if (r.Get("dispatch") is { } dispatch)
            {
                row["dispatch"] = dispatch;
            }

            if (r.Get("peer") is { } peer)
            {
                row["peer"] = peer;
            }
        }

        return new JsonObject { ["row"] = row, ["warnings"] = warnings };
    }

    private JsonObject CheckPackRole(string name, JsonValue raw, string plugin)
    {
        var warnings = new JsonArray();

        JsonObject Bad(string code, string message) => new()
        {
            ["raw"] = raw,
            ["agent"] = JsonValue.Null,
            ["row"] = JsonValue.Null,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            ["warnings"] = warnings,
        };

        var nameErr = ConfigRegistry.RoleNameError(name);
        if (nameErr is not null)
        {
            return Bad("pack-invalid", $"role {JsonText.Stringify(name, 0)}: {nameErr}");
        }

        if (!raw.IsMap)
        {
            return Bad("pack-invalid", $"role {name}: must be an object");
        }

        var r = raw.AsObject();
        if (r.Get("peer") is not null)
        {
            return Bad("pack-invalid", $"role {name}: peer is a local setting, not allowed in a pack");
        }

        foreach (var k in r.Keys)
        {
            if (!ConfigRegistry.PackRoleFields.Contains(k))
            {
                warnings.Add($"role {name}: unknown key {JsonText.Stringify(k, 0)} is ignored");
            }
        }

        foreach (var k in ConfigRegistry.PackRoleFields)
        {
            if (r.Get(k) is { IsText: true } text && PackText.HiddenCharAt(text.AsString()) is { } at)
            {
                return Bad("pack-hidden-chars", $"role {name}: {k} holds a hidden character ({at})");
            }
        }

        var agent = r.Get("agent") ?? name;
        if (!agent.IsText || agent.AsString().Length == 0 || ForbiddenAgentChars(agent.AsString()))
        {
            return Bad("pack-invalid", $"role {name}: agent {ConfigRegistry.JsStringify(agent)} must name an agent in this plugin, with no \":\", \"/\", \"\\\" or \"..\"");
        }

        var fields = new JsonObject();
        foreach (var k in ConfigRegistry.PackRoleFields)
        {
            if (r.Get(k) is { } v)
            {
                fields[k] = v;
            }
        }

        fields["agent"] = $"{plugin}:{agent.AsString()}";
        var checkedRow = CheckCustomRowCore(name, fields);
        if (checkedRow.Get("error") is { } error)
        {
            return Bad("pack-invalid", $"role {name}: {error.AsString()}");
        }

        foreach (var w in checkedRow["warnings"].AsArray())
        {
            warnings.Add($"role {name}: {w.AsString()}");
        }

        return new JsonObject
        {
            ["raw"] = raw,
            ["agent"] = agent,
            ["row"] = checkedRow["row"],
            ["error"] = JsonValue.Null,
            ["warnings"] = warnings,
        };

        static bool ForbiddenAgentChars(string s) => s.AsSpan().IndexOfAny(':', '/', '\\') >= 0 || s.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>A pack's <c>ah-roles.json</c> at <paramref name="dir"/>, read and checked. Never throws.</summary>
    public JsonObject ReadPackManifest(string dir, string plugin)
    {
        var path = NodePath.Join(dir, "ah-roles.json");

        JsonObject Unreadable(string code, string message) => new()
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            ["warnings"] = new JsonArray(),
            ["roles"] = new JsonObject(),
        };

        string text;
        try
        {
            text = files.ReadText(path);
        }
        catch (Exception)
        {
            return Unreadable("pack-missing", $"no ah-roles.json in {dir}");
        }

        JsonValue data;
        try
        {
            data = JsonText.Parse(text);
        }
        catch (JsonParseException)
        {
            return Unreadable("pack-invalid", $"{path} is not valid JSON");
        }

        if (!data.IsMap)
        {
            return Unreadable("pack-invalid", $"{path} must hold a JSON object");
        }

        var doc = data.AsObject();
        var version = doc.Get("version");
        if (version is not { IsNumber: true } v || v.AsNumber() != 1)
        {
            return Unreadable("pack-version", $"{path}: version must be 1, got {ConfigRegistry.JsStringify(version)}");
        }

        if (doc.Get("roles") is not { IsMap: true } rolesValue)
        {
            return Unreadable("pack-invalid", $"{path}: roles must be an object");
        }

        var warnings = new JsonArray();
        foreach (var k in doc.Keys)
        {
            if (k is not ("version" or "roles"))
            {
                warnings.Add($"unknown key {JsonText.Stringify(k, 0)} is ignored");
            }
        }

        var roles = new JsonObject();
        foreach (var name in rolesValue.AsObject().Keys)
        {
            roles[name] = CheckPackRole(name, rolesValue.AsObject()[name], plugin);
        }

        return new JsonObject { ["error"] = JsonValue.Null, ["warnings"] = warnings, ["roles"] = roles };
    }

    // ---------------------------------------------------------------- pin and stored copy

    /// <summary><c>decodeUtf8</c>: the text of <paramref name="bytes"/> keeping a BOM, or null when they are not valid UTF-8.</summary>
    private static string? DecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    internal PackTree PackTreeOf(string root)
    {
        if (_packTrees.TryGetValue(root, out var cached))
        {
            return cached;
        }

        var found = new List<(string, string)>();
        string? symlink = null;

        void Walk(string dir, string rel)
        {
            foreach (var name in ListForTree(dir))
            {
                if (rel.Length == 0 && name == ".git")
                {
                    continue;
                }

                var p = NodePath.Join(dir, name);
                var r = rel.Length > 0 ? $"{rel}/{name}" : name;
                var st = files.Stat(p, followSymlinks: false) ?? throw new IOException($"ENOENT: no such file or directory, lstat '{p}'");
                if (st.Kind == FileKind.Symlink)
                {
                    symlink ??= r;
                }
                else if (st.Kind == FileKind.Directory)
                {
                    Walk(p, r);
                }
                else if (st.Kind == FileKind.File)
                {
                    found.Add((r, ContentDigest.OfBytes(ReadForTree(p))));
                }
            }
        }

        PackTree result;
        try
        {
            Walk(root, string.Empty);
            found.Sort(static (a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            result = new PackTree { Files = found, Symlink = symlink };
        }
        catch (Exception e)
        {
            result = new PackTree { Error = e.Message };
        }

        _packTrees[root] = result;
        return result;
    }

    /// <summary>The names in a directory; a failure reads as Node's <c>readdirSync</c> error does, since the message is part of the result.</summary>
    private IReadOnlyList<string> ListForTree(string dir)
    {
        try
        {
            return files.List(dir);
        }
        catch (Exception)
        {
            var st = files.Stat(dir);
            throw new IOException(st is null
                ? $"ENOENT: no such file or directory, scandir '{dir}'"
                : st.Value.Kind != FileKind.Directory
                    ? $"ENOTDIR: not a directory, scandir '{dir}'"
                    : $"EACCES: permission denied, scandir '{dir}'");
        }
    }

    private byte[] ReadForTree(string path)
    {
        try
        {
            return files.ReadBytes(path);
        }
        catch (Exception)
        {
            throw new IOException(files.Stat(path) is null
                ? $"ENOENT: no such file or directory, open '{path}'"
                : $"EACCES: permission denied, open '{path}'");
        }
    }

    private static JsonArray TreeFiles(IEnumerable<(string Rel, string Sha)> files)
    {
        var array = new JsonArray();
        foreach (var (rel, sha) in files)
        {
            var pair = new JsonArray();
            pair.Add(rel);
            pair.Add(sha);
            array.Add(pair);
        }

        return array;
    }

    /// <summary>The pin: <c>sha256:</c> over <c>JSON.stringify([1, row-with-sorted-keys, agentText, files])</c>.</summary>
    internal static string PackPin(JsonObject row, string agentText, JsonArray treeFiles)
    {
        var keys = row.Keys.ToList();
        keys.Sort(StringComparer.Ordinal);
        var sorted = new JsonObject();
        foreach (var k in keys)
        {
            sorted[k] = row[k];
        }

        var doc = new JsonArray { 1, sorted, agentText, treeFiles };
        return "sha256:" + ContentDigest.OfBytes(JsonText.ToUtf8(doc, 0));
    }

    internal JsonObject? ReadStoredCopy(JsonValue? pin)
    {
        if (pin is not { IsText: true } p || !PinRegex().IsMatch(p.AsString()))
        {
            return null;
        }

        var pinText = p.AsString();
        try
        {
            var path = AhPaths.TrustedPackFile(Home, pinText["sha256:".Length..]);
            var c = JsonText.Parse(files.ReadText(path));
            if (c.Member("row") is { IsMap: true } row && c.Member("agentText") is { IsText: true } text && c.Member("files") is { IsArray: true } list
                && PackPin(row.AsObject(), text.AsString(), list.AsArray()) == pinText)
            {
                return c.AsObject();
            }
        }
        catch (Exception)
        {
            // no usable copy
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="role"/>'s pack row holds its design-slot opt-in (adr-42): the row's <c>slots</c> names <c>design</c>, its
    /// pack is as pinned, and this machine's stored copy for the pin records the same opt-in for this role, written only by a trust commit.
    /// </summary>
    public bool DesignOptInHolds(string role, JsonValue? entry)
    {
        static bool NamesDesign(JsonValue? v) => v is { IsArray: true } a && a.AsArray().Any(x => x is { IsText: true } t && t.AsString() == "design");

        if (entry is not { IsMap: true } e || e.Member("from") is not { IsText: true } || !NamesDesign(e.Member("slots")))
        {
            return false;
        }

        if (!(e.Member("pack") is { IsMap: true } pack && pack.Member("status") is { IsText: true } status && status.AsString() == "ok"))
        {
            return false;
        }

        return ReadStoredCopy(e.Member("pin"))?.Get("slots") is { IsMap: true } recorded && NamesDesign(recorded.AsObject().Get(role));
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PinRegex();

    // ---------------------------------------------------------------- name claims

    private PackClaims PackNameClaims(string dir, JsonObject manifest)
    {
        if (_packClaims.TryGetValue(dir, out var cached))
        {
            return cached;
        }

        var result = new PackClaims();
        _packClaims[dir] = result;
        var found = new SortedSet<string>(StringComparer.Ordinal);

        void Walk(string p)
        {
            FileStat? st;
            try
            {
                st = files.Stat(p, followSymlinks: false);
            }
            catch (Exception)
            {
                return;
            }

            if (st is null)
            {
                return;
            }

            if (st.Value.Kind == FileKind.Directory)
            {
                foreach (var n in files.List(p))
                {
                    Walk(NodePath.Join(p, n));
                }
            }
            else if (st.Value.Kind == FileKind.File && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(p);
            }
        }

        Walk(NodePath.Join(dir, "agents"));
        JsonValue? pj = TryReadJsonStrict(NodePath.Join(dir, ".claude-plugin", "plugin.json"));
        if (pj is { IsMap: true } plugin && plugin.AsObject().Get("agents") is { } agents)
        {
            List<string>? paths = null;
            if (agents.IsText)
            {
                paths = [agents.AsString()];
            }
            else if (agents.IsArray && agents.AsArray().All(a => a.IsText))
            {
                paths = agents.AsArray().Select(a => a.AsString()).ToList();
            }

            if (paths is null)
            {
                result.Error = "plugin.json's agents must be a path or a list of paths";
                return result;
            }

            foreach (var p in paths)
            {
                var abs = NodePath.Resolve(dir, dir, p);
                var rel = NodePath.Relative(dir, dir, abs);
                if (rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || NodePath.IsAbsolute(rel))
                {
                    result.Error = $"plugin.json's agents path {PackText.EscapeTerminal(JsonText.Stringify(p, 0))} resolves outside the plugin";
                    return result;
                }

                Walk(abs);
            }
        }

        var roles = new List<(string Role, string Agent)>();
        if (manifest.Get("roles") is { IsMap: true } rolesMap)
        {
            foreach (var role in rolesMap.AsObject().Keys)
            {
                var m = rolesMap.AsObject()[role];
                if (m.IsMap && m.Member("agent")?.NonEmptyText is { } agentName && m.Member("error") is not { IsTruthy: true })
                {
                    roles.Add((role, agentName));
                }
            }
        }

        foreach (var abs in found)
        {
            PackText.Declared decl;
            try
            {
                var text = DecodeStrictUtf8(files.ReadBytes(abs));
                decl = text is null ? PackText.Declared.UnsureDeclared : PackText.DeclaredAgentName(text);
            }
            catch (Exception)
            {
                decl = PackText.Declared.UnsureDeclared;
            }

            var relPath = NodePath.Relative(dir, dir, abs);
            foreach (var (role, agentName) in roles)
            {
                if (SameFile(abs, NodePath.Join(dir, "agents", agentName + ".md")))
                {
                    continue;
                }

                var agent = agentName.ToLowerInvariant();
                var base0 = NodePath.Basename(abs);
                var stem = base0.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? base0[..^3] : base0;
                if (!(decl.Unsure || (decl.Name is not null && string.Equals(decl.Name.ToLowerInvariant(), agent, StringComparison.Ordinal)) || (decl.None && string.Equals(stem.ToLowerInvariant(), agent, StringComparison.Ordinal))))
                {
                    continue;
                }

                if (!result.ByRole.TryGetValue(role, out var list))
                {
                    result.ByRole[role] = list = [];
                }

                list.Add(relPath);
                if (!result.Files.TryGetValue(relPath, out var fileRoles))
                {
                    result.Files[relPath] = fileRoles = [];
                }

                fileRoles.Add(role);
            }
        }

        return result;
    }

    private bool SameFile(string a, string b)
    {
        try
        {
            var x = files.Stat(a);
            var y = files.Stat(b);
            return x is not null && y is not null && x.Value.Inode == y.Value.Inode && x.Value.Device == y.Value.Device;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string PackClaimMessage(string plugin, string agent, IReadOnlyList<string> claimingFiles)
    {
        var names = string.Join(", ", claimingFiles.Select(PackText.EscapeTerminal));
        var many = claimingFiles.Count > 1;
        return $"{names} also {(many ? "take" : "takes")} the agent name {agent}, so Claude Code might launch {(many ? "one of them" : "it")} as {PackText.EscapeTerminal(plugin)}:{agent} instead of agents/{agent}.md";
    }

    // ---------------------------------------------------------------- pack role state

    private PackState PackCandidate(InstallRecord record, string role)
    {
        var state = new PackState
        {
            Record = record,
            AgentText = JsonValue.Null,
            AgentPath = JsonValue.Null,
        };
        var manifest = ReadPackManifest(record.InstallPath, record.Plugin);
        if (manifest.Get("error") is { IsMap: true } manifestError)
        {
            var code = manifestError.Member("code")!.Value.AsString();
            state.Status = code == "pack-missing" ? "pack-missing" : "pack-invalid";
            state.Subcode = code;
            state.Message = manifestError.Member("message")!.Value.AsString();
            return state;
        }

        var roles = manifest["roles"].AsObject();
        if (roles.Get(role) is not { IsMap: true } rv)
        {
            state.Status = "pack-missing";
            state.Message = $"{record.Key} offers no role {role}";
            return state;
        }

        var r = rv.AsObject();
        if (r.Get("error") is { IsMap: true } roleError)
        {
            state.Status = "pack-invalid";
            state.Subcode = roleError.Member("code")!.Value.AsString();
            state.Message = roleError.Member("message")!.Value.AsString();
            state.ManifestRole = r;
            return state;
        }

        var agent = r["agent"].AsString();
        var agentPath = NodePath.Join(record.InstallPath, "agents", agent + ".md");
        byte[] buf;
        try
        {
            buf = files.ReadBytes(agentPath);
        }
        catch (Exception)
        {
            state.Status = "pack-invalid";
            state.Message = $"{record.Key} has no agents/{agent}.md";
            state.ManifestRole = r;
            state.AgentPath = agentPath;
            return state;
        }

        var agentText = DecodeStrictUtf8(buf);
        if (agentText is null)
        {
            state.Status = "pack-invalid";
            state.Message = $"{agentPath} is not valid UTF-8";
            state.ManifestRole = r;
            state.AgentPath = agentPath;
            return state;
        }

        state.ManifestRole = r;
        state.AgentPath = agentPath;
        var hidden = PackText.HiddenCharAt(agentText);
        if (hidden is not null)
        {
            state.Status = "pack-invalid";
            state.Subcode = "pack-hidden-chars";
            state.Message = $"{agentPath} holds a hidden character ({hidden})";
            return state;
        }

        state.AgentText = agentText;
        var claims = PackNameClaims(record.InstallPath, manifest);
        if (claims.Error is not null)
        {
            state.Status = "pack-invalid";
            state.Message = $"{record.Key}: {claims.Error}";
            return state;
        }

        if (claims.ByRole.TryGetValue(role, out var claimedBy))
        {
            state.Status = "pack-invalid";
            state.Message = PackClaimMessage(record.Plugin, agent, claimedBy);
            return state;
        }

        var tree = PackTreeOf(record.InstallPath);
        if (tree.Error is not null)
        {
            state.Status = "pack-invalid";
            state.Message = $"{record.InstallPath} could not be read: {tree.Error}";
            return state;
        }

        if (tree.Symlink is not null)
        {
            state.Status = "pack-symlink";
            state.Message = $"{record.InstallPath} holds a symbolic link, {tree.Symlink}, and what it points at isn't pinned";
            return state;
        }

        state.Files = TreeFiles(tree.Files);
        state.Pin = PackPin(r["raw"].IsMap ? r["raw"].AsObject() : new JsonObject(), agentText, state.Files);
        return state;
    }

    /// <summary>
    /// Pack role <paramref name="from"/> as installed now, checked against <paramref name="pin"/> (null skips the pin and
    /// stored-copy checks, for a first adoption).
    /// </summary>
    internal PackState PackRoleState(string from, string? pin)
    {
        var f = PackText.ParseFrom(from);
        if (f is null || f.Value.Marketplace is null)
        {
            return new PackState
            {
                Status = "pack-invalid",
                Message = $"from {JsonText.Stringify(from, 0)} must be <plugin>@<marketplace>:<role>",
                From = f,
            };
        }

        var fv = f.Value;
        var records = InstallRecords().Where(r => r.Plugin == fv.Plugin).ToList();
        if (records.Count == 0)
        {
            return new PackState { Status = "pack-missing", Message = $"plugin {fv.Plugin} is not installed", From = f };
        }

        var listed = records.Select(r => $"{r.Key} ({r.InstallPath})").ToList();
        if (!records.Any(r => r.Marketplace == fv.Marketplace))
        {
            return new PackState
            {
                Status = "pack-missing",
                Message = $"{fv.Plugin}@{fv.Marketplace} is not installed (installed under that name: {string.Join(", ", listed)})",
                From = f,
                Records = listed,
            };
        }

        var candidates = records.Select(r => PackCandidate(r, fv.Role)).ToList();
        var chosen = candidates.First(c => c.Record!.Marketplace == fv.Marketplace);
        var state = chosen.Copy();
        state.From = f;
        state.Records = listed;
        if (chosen.Status is "pack-missing" or "pack-invalid")
        {
            return state;
        }

        if (candidates.Select(c => c.Pin ?? c.Status).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            state.Status = "pack-ambiguous";
            state.Message = $"{records.Count} install records of {fv.Plugin} differ, and which one Claude Code loads can't be known: {string.Join(", ", state.Records)}";
            return state;
        }

        if (chosen.Status != "ok" || pin is null)
        {
            return state;
        }

        if (chosen.Pin != pin)
        {
            state.Status = "pack-changed";
            state.Message = $"{fv.Plugin} changed since this role was trusted (pinned {Truncate(pin)}…, now {Truncate(chosen.Pin!)}…)";
            return state;
        }

        if (ReadStoredCopy(pin) is null)
        {
            state.Status = "pack-untrusted-here";
            state.Message = $"this machine holds no stored copy of what {Truncate(pin)}… pinned, so nobody here has trusted it";
            return state;
        }

        return state;

        static string Truncate(string s) => s.Length > 19 ? s[..19] : s;
    }

    /// <summary>
    /// A <c>from</c> row expanded into the full row resolution checks: the pack's own fields (from the installed manifest,
    /// else from this machine's stored copy for the row's pin), with the row's own label, description, routes, model and
    /// dispatch laid over them. <c>{raw, pack}</c>, or <c>{error}</c>.
    /// </summary>
    public JsonObject ExpandFromRow(JsonObject entry) => ExpandFromRow(entry, pinIgnored: false);

    /// <summary>
    /// <see cref="ExpandFromRow(JsonObject)"/>; with <paramref name="pinIgnored"/> (a row a roster's packs supply, ah2-698 D3) the row's
    /// pin is not checked and no stored copy is read.
    /// </summary>
    internal JsonObject ExpandFromRow(JsonObject entry, bool pinIgnored)
    {
        var from = entry["from"].AsString();
        var state = PackRoleState(from, pinIgnored ? null : entry.Get("pin") is { IsText: true } pinText ? pinText.AsString() : string.Empty);
        JsonObject? source = state.ManifestRole is { } mr && mr.Get("error") is not { IsMap: true } && mr["raw"].IsMap ? mr["raw"].AsObject() : null;
        if (source is null && !pinIgnored)
        {
            var copy = ReadStoredCopy(entry.Get("pin"));
            if (copy is not null)
            {
                source = copy["row"].AsObject();
            }
        }

        if (state.From is null || source is null)
        {
            return new JsonObject { ["error"] = $"{state.Status}: {state.Message}" };
        }

        var f = state.From.Value;
        var raw = new JsonObject();
        foreach (var k in ConfigRegistry.PackRoleFields)
        {
            if (source.Get(k) is { } v)
            {
                raw[k] = v;
            }
        }

        raw["agent"] = $"{f.Plugin}:{(source.Get("agent") is { } a ? JsConvert.ToJsString(a) : f.Role)}";
        foreach (var k in new[] { "label", "description", "routes", "model", "dispatch" })
        {
            if (entry.Get(k) is not { } ev)
            {
                continue;
            }

            if (ev.IsText && ev.AsString().Length == 0 && k is "routes" or "description")
            {
                raw.Remove(k);
            }
            else
            {
                raw[k] = ev;
            }
        }

        var pack = new JsonObject
        {
            ["status"] = state.Status,
            ["message"] = state.Message is null ? JsonValue.Null : state.Message,
        };
        pack["subcode"] = string.IsNullOrEmpty(state.Subcode) ? JsonValue.Null : state.Subcode;
        pack["from"] = entry["from"];
        if (state.AgentText is { } agentText)
        {
            pack["agentText"] = agentText;
        }

        if (state.AgentPath is { } agentPath)
        {
            pack["agentPath"] = agentPath;
        }

        if (state.Pin is not null)
        {
            pack["pinNow"] = state.Pin;
        }
        else if (state.AgentText is not null)
        {
            pack["pinNow"] = JsonValue.Null;
        }

        return new JsonObject { ["raw"] = raw, ["pack"] = pack };
    }

    /// <summary>A built-in's <c>from</c> row: the pack supplies only its agent; its class must be the built-in's.</summary>
    internal JsonObject BuiltinFromRow(string role, JsonObject entry, bool pinIgnored = false)
    {
        var expanded = ExpandFromRow(entry, pinIgnored);
        var f = PackText.ParseFrom(entry["from"].AsString());
        var guess = f is null ? Qualified(role) : $"{f.Value.Plugin}:{f.Value.Role}";
        var result = entry.Clone();
        if (expanded.Get("error") is { } error)
        {
            var text = error.AsString();
            var status = text[..text.IndexOf(':', StringComparison.Ordinal)];
            result["agent"] = guess;
            result["pack"] = new JsonObject
            {
                ["status"] = status,
                ["message"] = text[(status.Length + 2)..],
                ["subcode"] = JsonValue.Null,
                ["from"] = entry["from"],
                ["agentText"] = JsonValue.Null,
                ["agentPath"] = JsonValue.Null,
                ["pinNow"] = JsonValue.Null,
            };
            return result;
        }

        var rawRow = expanded["raw"].AsObject();
        var pack = expanded["pack"].AsObject();
        var expectedClass = ConfigRegistry.BuiltinClass(role);
        var gotClass = rawRow.Get("class");
        if (gotClass is not { IsText: true } gc || gc.AsString() != expectedClass)
        {
            pack = pack.Clone();
            pack["status"] = "pack-invalid";
            pack["message"] = $"the pack role's class, {JsConvert.ToJsString(gotClass)}, isn't the {role}'s class, {expectedClass}";
        }

        result["agent"] = rawRow["agent"];
        result["pack"] = pack;
        return result;
    }
}
