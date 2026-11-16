using System.Text;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Config;

/// <summary>Config paths, roster levels and roster selection, model tiers, stale-key reports, and the hook-error log reader.</summary>
internal sealed partial class ConfigCore
{
    // ---------------------------------------------------------------- paths

    public string DefaultRoster => ConfigRegistry.DefaultRoster;

    public string ShellSelfCare => "self-care";

    public IReadOnlyList<string> SelfCareBuiltinRoles => ["architect"];

    public int OneLineMax => ConfigRegistry.OneLineMax;

    public string? ProjectConfigPath(JsonValue? cwd) =>
        cwd is { IsText: true } c && c.AsString().Length > 0 ? ProjectEntryOf(Resolve(c.AsString()), ConfigRegistry.ConfigBasename) : null;

    public string? CheckoutRoot(string? cwd)
    {
        var root = FindGitRoot(CwdOr(cwd));
        if (root is null)
        {
            return null;
        }

        var main = MainCheckoutRoot(root);
        return string.IsNullOrEmpty(main) ? root : main;
    }

    public JsonObject RosterLevelPaths(string? cwd)
    {
        var resolvedCwd = Resolve(CwdOr(cwd));
        var repoRoot = FindGitRoot(resolvedCwd) ?? resolvedCwd;
        return new JsonObject
        {
            ["global"] = UserConfigPath(),
            ["repo"] = ProjectEntryOf(repoRoot, ConfigRegistry.ConfigBasename),
            ["repo-user"] = AhPaths.RepoUserConfig(Home, PathSlug(repoRoot)),
        };
    }

    public JsonObject RosterLevelCandidates(string? cwd)
    {
        var candidates = RosterLevelCandidateLists(cwd);
        var result = new JsonObject();
        foreach (var level in new[] { "repo-user", "repo", "global" })
        {
            result[level] = ConfigRegistry.StringArray(candidates[level]);
        }

        return result;
    }

    public JsonObject TeamLayoutPreference()
    {
        var (layout, warnings) = TeamLayoutPreferenceTuple();
        return new JsonObject { ["layout"] = layout ?? JsonValue.Null, ["warnings"] = ConfigRegistry.StringArray(warnings) };
    }

    // ---------------------------------------------------------------- team aliases

    public bool IsValidTeamAlias(JsonValue? teamAlias) => teamAlias is { } a && ConfigRegistry.IsValidTeamAlias(a);

    /// <summary>
    /// A team name to OFFER when the one a create would use cannot be used. Without a herdr context it sanitizes the name so it still
    /// resembles the repo, with <c>team</c> as the last resort; under herdr every <c>&lt;name&gt;&lt;suffix&gt;</c> must also be a legal Herdr name.
    /// Returns a name, or null when none fits.
    /// </summary>
    public JsonValue SuggestTeamAlias(JsonValue? raw, JsonValue? context)
    {
        var text = raw is null || raw.Value.IsNull ? string.Empty : JsConvert.ToJsString(raw);
        var herdr = context is { IsTruthy: true } ctx && ctx.Member("transport") is { IsText: true } t && t.AsString() == "herdr";
        if (!herdr)
        {
            var sanitized = TrimTrailingHyphens(TakeFirst(StripLeadingNonAlnum(ReplaceOutside(text, keepUpper: true)), 32));
            var teamPrefixed = TrimTrailingHyphens(TakeFirst("team-" + sanitized, 32));
            foreach (var candidate in new[] { sanitized, teamPrefixed })
            {
                if (ConfigRegistry.IsValidTeamAlias(candidate))
                {
                    return candidate;
                }
            }

            return "team";
        }

        var suffixes = new List<string>();
        if (context!.Value.Member("suffixes") is { IsTruthy: true } sv && sv.IsArray)
        {
            suffixes.AddRange(sv.AsArray().Select(s => JsConvert.ToJsString(s)));
        }

        if (suffixes.Any(s => !IsHerdrSuffix(s)))
        {
            return JsonValue.Null;
        }

        var budget = 32 - Math.Max(0, suffixes.Select(s => s.Length).DefaultIfEmpty(0).Max());
        if (budget < 1)
        {
            return JsonValue.Null;
        }

        string Fit(string name) => TrimTrailingHyphens(TakeFirst(name, budget));
        var lowered = ConfigRegistry.LowerForAsciiPatterns(text);
        var spaced = CollapseHyphens(ReplaceOutside(lowered, keepUpper: false));
        var start = 0;
        while (start < spaced.Length && !(spaced[start] is >= 'a' and <= 'z'))
        {
            start++;
        }

        var baseName = Fit(spaced[start..]);
        var candidates = new List<string> { baseName, Fit("team-" + baseName) };
        if (budget >= 4)
        {
            candidates.Add("team");
        }

        var resolved = context.Value.Member("resolved") is { IsMap: true } rm ? rm.AsObject() : null;
        foreach (var candidate in candidates)
        {
            if (ConfigRegistry.ValidateTeamAlias(candidate, resolved).Get("ok")!.Value.AsBool()
                && suffixes.All(s => ConfigRegistry.ValidateHerdrName(candidate + s).Get("ok")!.Value.AsBool()))
            {
                return candidate;
            }
        }

        return JsonValue.Null;
    }

    private static bool IsHerdrSuffix(string s)
    {
        if (s.Length < 2 || s[0] != '-')
        {
            return false;
        }

        return s.Skip(1).All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');
    }

    /// <summary>Every UTF-16 unit outside <c>[A-Za-z0-9-]</c> (or outside <c>[a-z0-9-]</c>) becomes a hyphen.</summary>
    private static string ReplaceOutside(string s, bool keepUpper)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' || (keepUpper && c is >= 'A' and <= 'Z');
            sb.Append(ok ? c : '-');
        }

        return sb.ToString();
    }

    private static string StripLeadingNonAlnum(string s)
    {
        var i = 0;
        while (i < s.Length && !(s[i] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
        {
            i++;
        }

        return s[i..];
    }

    private static string CollapseHyphens(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '-' && sb.Length > 0 && sb[^1] == '-')
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string TakeFirst(string s, int count) => s.Length <= count ? s : s[..count];

    private static string TrimTrailingHyphens(string s) => s.TrimEnd('-');

    // ---------------------------------------------------------------- roster blocks and selection

    public JsonObject NormalizeRosterBlock(JsonObject block, JsonObject? registry) => NormalizeRosterBlockCore(block, registry);

    public JsonArray DropNonObjectMembers(JsonObject data) => DropNonObjectMembersCore(data);

    public JsonArray RosterBlocksOf(JsonObject data)
    {
        var result = new JsonArray();
        foreach (var (label, key, block) in RosterBlockTuples(data))
        {
            result.Add(new JsonArray { label, key is null ? JsonValue.Null : key, block ?? JsonValue.Null });
        }

        return result;
    }

    public JsonArray RosterMemberNames(JsonArray members, string repoBasename) => RosterMemberNamesCore(members, repoBasename);

    public JsonValue ResolveRoster(string? cwd, string? rosterKey, string? namingPrefix, JsonObject? registry) =>
        ResolveRosterObject(cwd, rosterKey, namingPrefix, registry) is { } r ? r : JsonValue.Null;

    public JsonArray NamedRosterKeys(string? cwd) => Strings(NamedRosterKeyList(cwd));

    public JsonArray SelectableRosters(string? cwd) => Strings(SelectableRosterList(cwd));

    public JsonValue ActiveRosterSetting(string? cwd)
    {
        if (ActiveRosterSettingTuple(cwd) is not { } a)
        {
            return JsonValue.Null;
        }

        return new JsonObject { ["value"] = a.Value, ["level"] = a.Level, ["path"] = a.Path };
    }

    /// <summary>
    /// Which roster block a command uses, and why. <paramref name="options"/> is <c>{flag, team: {dir, name}}</c>, both optional.
    /// </summary>
    public JsonObject RosterSelection(string? cwd, JsonValue? options)
    {
        JsonValue? flag = options?.Member("flag");
        (string Dir, string? Name)? team = null;
        if (options?.Member("team") is { IsTruthy: true } t)
        {
            var dir = t.Member("dir") is { IsText: true } d ? d.AsString() : throw new ArgumentException("team.dir must be text", nameof(options));
            team = (dir, t.Member("name") is { IsText: true } n ? n.AsString() : null);
        }

        return RosterSelectionFor(cwd, flag, team);
    }

    public JsonObject SelectionView(JsonObject selection)
    {
        var view = new JsonObject();
        foreach (var key in new[] { "roster", "source", "level", "path" })
        {
            if (selection.Get(key) is { } v)
            {
                view[key] = v;
            }
        }

        return view;
    }

    public JsonValue RosterSelectionProblem(string? cwd, JsonObject selection) =>
        RosterSelectionProblemText(cwd, selection) is { } problem ? problem : JsonValue.Null;

    public JsonObject SessionRosterSelection(string? cwd, ResolveOptions? options = null)
    {
        var resolvedCwd = Resolve(CwdOr(cwd));
        var o = options ?? new ResolveOptions();
        var (team, home, _, _) = ResolveTeamScope(resolvedCwd, o);
        return ScopedRosterSelection(resolvedCwd, o, team, home);
    }

    public JsonArray? OwnedRosterSelections(string? cwd, ResolveOptions? options = null)
    {
        var resolvedCwd = Resolve(CwdOr(cwd));
        var o = options ?? new ResolveOptions();
        var (_, _, _, owned) = ResolveTeamScope(resolvedCwd, o);
        if (owned.Count < 2)
        {
            return null;
        }

        var home = HierarchyDir(resolvedCwd);
        var result = new JsonArray();
        foreach (var team in owned)
        {
            result.Add(new JsonObject
            {
                ["team"] = team is null ? JsonValue.Null : team,
                ["selection"] = ScopedRosterSelection(resolvedCwd, o, team, home),
            });
        }

        return result;
    }

    // ---------------------------------------------------------------- declared model tiers

    /// <summary>
    /// <c>{timeline_retention_days, read_model_max_mb, warnings}</c> from the optional <c>ui</c> object of the global config: the days
    /// of timeline kept live (1 to 3650, default 7) and the read model's memory ceiling in MB (16 or more; null when unset). A value
    /// outside its range or of the wrong type falls back to its default and adds a warning.
    /// </summary>
    public bool CodexOrchestratorApprovals()
    {
        var path = UserConfigPath();
        try
        {
            if (!files.Exists(path))
            {
                return true;
            }

            return JsonText.Parse(files.ReadText(path)) is { IsMap: true } doc
                && (doc.AsObject().Get("codex_orchestrator_approvals") is not { } setting || (setting.IsBool && setting.AsBool()));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a file that exists but cannot be read may hold the user's "off": everything then goes to the user. The same holds for a
            // value that is present but is not the boolean true.
            return false;
        }
    }

    public JsonObject UiSettings()
    {
        var warnings = new JsonArray();
        var path = UserConfigPath();
        var ui = (TryReadJson(path) is { IsMap: true } doc ? doc.AsObject() : null)?.Get("ui");
        JsonObject? map = null;
        if (ui is { IsMap: true } present)
        {
            map = present.AsObject();
        }
        else if (ui is { IsNull: false })
        {
            warnings.Add($"ah: ui in {path} is not an object — ignoring it.");
        }

        long Whole(string key, long fallback, long min, long max, bool allowAbsent, out bool set)
        {
            set = false;
            if (map?.Get(key) is not { } raw || raw.IsNull)
            {
                return fallback;
            }

            if (raw.IsNumber && raw.AsNumber() == Math.Floor(raw.AsNumber()) && raw.AsNumber() >= min && raw.AsNumber() <= max)
            {
                set = true;
                return (long)raw.AsNumber();
            }

            warnings.Add($"ah: ui.{key} in {path} must be a whole number from {min} to {max} — using {(allowAbsent ? "no limit" : fallback.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");
            return fallback;
        }

        var days = Whole("timeline_retention_days", 7, 1, 3650, allowAbsent: false, out _);
        var mb = Whole("read_model_max_mb", 0, 16, int.MaxValue, allowAbsent: true, out var mbSet);
        return new JsonObject
        {
            ["timeline_retention_days"] = days,
            ["read_model_max_mb"] = mbSet ? mb : JsonValue.Null,
            ["warnings"] = warnings,
        };
    }

    /// <summary>
    /// <c>{retention_days, prune, warnings}</c> from the optional <c>state</c> object of the global config: the days a settled peer row is kept
    /// (a whole number from 1 to 3650, default 14), and whether the hourly prune only counts what it would drop (<c>count</c>)
    /// or deletes (<c>on</c>, the default when the key is absent). A <c>prune</c> that is set but not recognised gives <c>count</c> and a warning;
    /// a <c>retention_days</c> outside its range or of the wrong type gives the default and a warning.
    /// </summary>
    public JsonObject StateSettings()
    {
        const long Default = 14;
        var warnings = new JsonArray();
        var path = UserConfigPath();
        var state = (TryReadJson(path) is { IsMap: true } doc ? doc.AsObject() : null)?.Get("state");
        var days = Default;
        var prune = "on";
        if (state is { IsMap: true } map)
        {
            if (map.AsObject().Get("prune") is { IsNull: false } mode)
            {
                if (mode.IsText && mode.AsString() is "count" or "on")
                {
                    prune = mode.AsString();
                }
                else
                {
                    prune = "count";
                    warnings.Add($"ah: state.prune in {path} must be \"count\" or \"on\" — using \"count\".");
                }
            }

            if (map.AsObject().Get("retention_days") is { IsNull: false } raw)
            {
                if (raw.IsNumber && raw.AsNumber() == Math.Floor(raw.AsNumber()) && raw.AsNumber() >= 1 && raw.AsNumber() <= 3650)
                {
                    days = (long)raw.AsNumber();
                }
                else
                {
                    warnings.Add($"ah: state.retention_days in {path} must be a whole number from 1 to 3650 — using {Default.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
                }
            }
        }
        else if (state is { IsNull: false })
        {
            warnings.Add($"ah: state in {path} is not an object — ignoring it.");
        }

        return new JsonObject { ["retention_days"] = days, ["prune"] = prune, ["warnings"] = warnings };
    }

    /// <summary><c>{tiers, warnings}</c>: the tiers the user declared for non-Claude models, in the global config only.</summary>
    public JsonObject DeclaredModelTiers()
    {
        var warnings = new JsonArray();
        var tiers = new JsonObject();
        JsonObject Result() => new() { ["tiers"] = tiers, ["warnings"] = warnings };
        var path = UserConfigPath();
        var data = TryReadJson(path) is { IsMap: true } doc ? doc.AsObject() : null;
        var raw = data?.Get("modelTiers");
        if (raw is null || raw.Value.IsNull)
        {
            return Result();
        }

        if (!raw.Value.IsMap)
        {
            warnings.Add($"ah: modelTiers in {path} is not an object of {{\"<kind>\": {{\"<model>\": \"<tier>\"}}}} — ignoring it.");
            return Result();
        }

        var tierNames = new[] { "haiku", "sonnet", "opus", "fable" };
        var map = raw.Value.AsObject();
        foreach (var kind in map.Keys)
        {
            var models = map[kind];
            if (kind == ConfigRegistry.KindDefault)
            {
                warnings.Add($"ah: modelTiers.claude in {path} is ignored — a Claude model carries its own tier.");
                continue;
            }

            if (!ConfigRegistry.IsKindName(kind) || !models.IsMap)
            {
                warnings.Add($"ah: modelTiers.{kind} in {path} is not an object of {{\"<model>\": \"<tier>\"}} — ignoring it.");
                continue;
            }

            var byModel = models.AsObject();
            foreach (var model in byModel.Keys)
            {
                var tier = byModel[model];
                if (!tier.IsText || Array.IndexOf(tierNames, tier.AsString()) < 0)
                {
                    warnings.Add($"ah: modelTiers.{kind}.{model} in {path} is {JsonText.Stringify(tier, 0)}, not a tier ({string.Join(", ", tierNames)}) — ignoring it.");
                    continue;
                }

                if (tiers.Get(kind) is not { IsMap: true } existing)
                {
                    existing = new JsonObject();
                    tiers[kind] = existing;
                }

                existing.AsObject()[model] = tier;
            }
        }

        return Result();
    }

    /// <summary>The tier name declared for <paramref name="model"/> of <paramref name="kind"/>, or JSON null. Never read from the model's name.</summary>
    public JsonValue DeclaredTier(JsonValue? kind, JsonValue? model)
    {
        var key = kind is null ? "undefined" : JsConvert.ToJsString(kind);
        if (DeclaredModelTiers()["tiers"].AsObject().Get(key) is { IsMap: true } byModel && model is { IsText: true } m && byModel.AsObject().Get(m.AsString()) is { } tier)
        {
            return tier;
        }

        return JsonValue.Null;
    }

    /// <summary>A non-Claude member's model as status shows it: <c>&lt;kind&gt;:&lt;model&gt;(&lt;declared tier&gt;|?)</c>.</summary>
    public string NonClaudeModelText(JsonValue? member)
    {
        var kind = ResolveKind(member);
        var model = member is { IsTruthy: true } m ? m.Member("model") : null;
        var modelText = model is { IsTruthy: true } mt ? JsConvert.ToJsString(mt) : "?";
        var tier = DeclaredTier(kind, member is { IsTruthy: true } ? model : member);
        return $"{JsConvert.ToJsString(kind)}:{modelText}({(tier.IsTruthy ? JsConvert.ToJsString(tier) : "?")})";
    }

    // ---------------------------------------------------------------- stale keys

    /// <summary>
    /// Config keys that do nothing any more, as warnings a person reads (CLI output only): <c>teamAlias</c>, a roster block's
    /// <c>layout</c>, <c>teamLayout</c> outside the global file, subagent opt-ins of chain roles, invalid <c>modelTiers</c> and
    /// non-object roster members. Returns <c>{warnings, aliases}</c>.
    /// </summary>
    public JsonObject StaleTeamKeys(string? cwd, JsonObject? registry = null)
    {
        var warnings = new JsonArray();
        foreach (var w in DeclaredModelTiers()["warnings"].AsArray().Concat(UiSettings()["warnings"].AsArray()).Concat(StateSettings()["warnings"].AsArray()))
        {
            warnings.Add(w);
        }

        var aliases = new JsonArray();
        var candidates = RosterLevelCandidateLists(cwd);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var userPath = UserConfigPath();
        foreach (var path in candidates["repo-user"].Concat(candidates["repo"]).Concat(candidates["global"]))
        {
            if (!seen.Add(path) || !files.Exists(path) || TryReadJson(path) is not { IsMap: true } doc)
            {
                continue;
            }

            var data = doc.AsObject();
            foreach (var d in DropNonObjectMembersCore(data))
            {
                var o = d.AsObject();
                warnings.Add($"roster `{path}`: `members[{JsConvert.ToJsString(o["index"])}]` is not an object (`{o["type"].AsString()}`); ignored");
            }

            string SubagentOnly(string what) =>
                $"ah: {what} in {path} is ignored — only legwork roles run as subagents. It is migrated at the next CLI write to that file, or delete it by hand.";

            if (data.Get("teamAlias") is { } alias)
            {
                warnings.Add($"ah: teamAlias in {path} is ignored — a team's name is chosen when it is created (`ah roster create --team <name>`). It has no effect; delete it from {path} to drop this warning, or it is removed at the next CLI write to that file.");
                if (alias is { IsText: true } a && a.AsString().Length > 0)
                {
                    aliases.Add(a);
                }
            }

            foreach (var (label, _, block) in RosterBlockTuples(data))
            {
                if (block is { IsMap: true } b && b.AsObject().Get("layout") is not null)
                {
                    warnings.Add($"ah: {label}.layout in {path} is ignored — a team's layout is chosen when it is created (`ah roster create --mode <auto|columns|grid>`), and an explicit --mode becomes the default for future teams. It has no effect; delete it from {path} to drop this warning, or it is removed at the next CLI write to that file.");
                }
            }

            if (path != userPath && data.Get("teamLayout") is not null)
            {
                warnings.Add($"ah: teamLayout in {path} is ignored — the default team layout is read from the global config ({userPath}) only, and an explicit `create --mode <m>` sets it. It has no effect; delete it from {path} to drop this warning, or it is removed at the next CLI write to that file.");
            }

            if (data.Get("route") is { IsText: true } route && ConfigRegistry.StaleRouteValues.Contains(route.AsString()))
            {
                warnings.Add(SubagentOnly($"route {JsonText.Stringify(route, 0)}"));
            }

            if (data.Get("roles") is { IsMap: true } roleRows)
            {
                var rows = roleRows.AsObject();
                foreach (var role in rows.Keys)
                {
                    var cls = ConfigRegistry.RoleClass(role, registry);
                    if (cls is not null && cls != "legwork" && rows[role] is { IsMap: true } row && row.AsObject().Get("dispatch") is { IsText: true } dispatch && dispatch.AsString() == "model")
                    {
                        warnings.Add(SubagentOnly($"roles.{role}.dispatch \"model\""));
                    }
                }
            }

            foreach (var (label, _, block) in RosterBlockTuples(data))
            {
                if (block is not { IsMap: true } bv)
                {
                    continue;
                }

                var b = bv.AsObject();
                if (b.Get("route") is { IsText: true } br && br.AsString() == "subagent")
                {
                    warnings.Add(SubagentOnly($"{label}.route \"subagent\""));
                }

                if (b.Get("members") is not { IsArray: true } members)
                {
                    continue;
                }

                var index = 0;
                foreach (var m in members.AsArray())
                {
                    var i = index++;
                    var cls = m.IsMap && m.Member("role") is { IsText: true } mr ? ConfigRegistry.RoleClass(mr.AsString(), registry) : null;
                    if (cls is null)
                    {
                        continue;
                    }

                    var roleText = JsConvert.ToJsString(m.Member("role"));
                    if (cls != "legwork" && m.Member("route") is { IsText: true } mroute && mroute.AsString() == "subagent")
                    {
                        warnings.Add(SubagentOnly($"{label}.members[{i}] ({roleText}) route \"subagent\""));
                    }

                    if (m.Member("onMissing") is { IsText: true } om && om.AsString() is "never" or "prompt")
                    {
                        warnings.Add(SubagentOnly($"{label}.members[{i}] ({roleText}) onMissing {JsonText.Stringify(om, 0)}"));
                    }
                }
            }
        }

        return new JsonObject { ["warnings"] = warnings, ["aliases"] = aliases };
    }

    // ---------------------------------------------------------------- role-row validation

    public JsonValue RoleNameError(JsonValue? name) =>
        name is null ? "name must match /^[a-z][a-z0-9-]{0,30}$/" : ConfigRegistry.RoleNameError(name.Value) is { } e ? e : JsonValue.Null;

    public JsonValue AgentRefError(JsonValue? agent) =>
        ConfigRegistry.AgentRefError(agent) is { } e ? e : JsonValue.Null;

    public JsonValue OneLineError(string field, JsonValue? value) =>
        ConfigRegistry.OneLineError(field, value ?? JsonValue.Null) is { } e ? e : JsonValue.Null;

    public string DefaultLabel(string name) => ConfigRegistry.DefaultLabel(name);

    public JsonObject CheckCustomRow(string name, JsonValue raw) => CheckCustomRowCore(name, raw);

    // ---------------------------------------------------------------- hook errors, plugin meta, member sessions

    public string HookErrorLogPath => AhPaths.HookErrorLog(Home);

    public string RejectedLogPath => AhPaths.RejectedLog(environment.AhHome);

    /// <summary>The hook name under which auto-mode denials are written to the hook-error log; they are records of a refusal, not hook failures.</summary>
    public const string DenialHook = "permission-denied-alarm";

    /// <summary>
    /// Entries of the hook-error log from the last <paramref name="hours"/> hours, oldest first, without the auto-mode denial records.
    /// An absent or unreadable log reads as empty.
    /// </summary>
    public JsonArray RecentHookErrors(DateTimeOffset now, double hours = 24)
    {
        var result = new JsonArray();
        foreach (var entry in RecentRows(HookErrorLogPath, now, hours))
        {
            if (!(entry.IsMap && entry.AsObject().Get("hook") is { IsText: true } hook && hook.AsString() == DenialHook))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    public JsonArray RecentRows(string path, DateTimeOffset now, double hours = 24)
    {
        var result = new JsonArray();
        try
        {
            var cutoff = now.ToUnixTimeMilliseconds() - (hours * 3600 * 1000);
            foreach (var line in files.ReadText(path).Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JsonValue entry;
                try
                {
                    entry = JsonText.Parse(line);
                }
                catch (Exception)
                {
                    continue;
                }

                if (entry.IsMap && entry.AsObject().Get("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var ms) && ms >= cutoff)
                {
                    result.Add(entry);
                }
            }
        }
        catch (Exception)
        {
            return new JsonArray();
        }

        return result;
    }

    private JsonValue? _pluginVersion;

    /// <summary>This plugin's version, or <c>"?"</c> when its <c>plugin.json</c> cannot be read. Read once.</summary>
    public JsonValue PluginVersion()
    {
        if (_pluginVersion is { } cached)
        {
            return cached;
        }

        JsonValue version = "?";
        try
        {
            var root = (environment.Plugin ?? throw new InvalidOperationException("plugin location unknown")).Root;
            var doc = JsonText.Parse(files.ReadText(NodePath.Join(root, ".claude-plugin", "plugin.json")));
            if (doc.Member("version") is { IsTruthy: true } v)
            {
                version = v;
            }
        }
        catch (Exception)
        {
            version = "?";
        }

        _pluginVersion = version;
        return version;
    }

    /// <summary>The <c>ah CLI root:</c> line appended to every SessionStart injection.</summary>
    public string CliRootLine()
    {
        var root = PluginRoot;
        return $"ah CLI root (v{JsConvert.ToJsString(PluginVersion())}): {root} — roster: `{CliCommand(CliTool.Roster)} <verb> --cwd <abs cwd>`, " +
               $"messages: `{CliCommand(CliTool.Msg)} <verb> --cwd <abs cwd>` (verbs: {root}/docs/cli-tools.md)";
    }

    public bool IsMemberSession => environment.GetVariable("WRANGL_TEAM_FILE") is { Length: > 0 };

    public string MemberAskTail =>
        "No one is at this session's prompt, so ah does not ask here. Do not retry this command. Put what you needed in your report as BLOCKED or NEEDS-DECISION for your Orchestrator, who will ask the user.";

    /// <summary>The one place a PreToolUse prompt is decided: <c>ask</c>, except in a member session, where it is a <c>deny</c> with the member tail.</summary>
    public JsonObject AskDecision(string reason) => IsMemberSession
        ? new JsonObject { ["decision"] = "deny", ["reason"] = $"{reason} {MemberAskTail}" }
        : new JsonObject { ["decision"] = "ask", ["reason"] = reason };

    public bool IsTopLevelAgentSession(JsonValue? input) =>
        input?.Member("agent_type") is { IsText: true } t && t.AsString().Length > 0 && !(input.Value.Member("agent_id")?.NonEmptyText is not null);

    public JsonValue HierarchyRoleOf(JsonValue? agentType, JsonObject? resolved = null, string? cwd = null) =>
        agentType is { IsText: true } t && HierarchyRoleOf(t.AsString(), resolved, cwd) is { } role ? role : JsonValue.Null;

    /// <summary><c>{role, resolved}</c>: <c>resolved</c> is read only when the type is not <c>ah:&lt;builtin&gt;</c>, and is null when the read fails.</summary>
    public JsonObject LookupRole(JsonValue? agentType, string? cwd)
    {
        if (agentType is not { IsText: true } t || t.AsString().Length == 0)
        {
            return new JsonObject { ["role"] = JsonValue.Null, ["resolved"] = JsonValue.Null };
        }

        var type = t.AsString();
        if (BuiltinRoleOf(type) is { } builtinRole)
        {
            return new JsonObject { ["role"] = builtinRole, ["resolved"] = JsonValue.Null };
        }

        JsonObject? resolved;
        try
        {
            resolved = ResolveConfig(string.IsNullOrEmpty(cwd) ? process.CurrentDirectory : cwd);
        }
        catch (Exception)
        {
            resolved = null;
        }

        var role = HierarchyRoleOf(type, resolved);
        return new JsonObject { ["role"] = role is null ? JsonValue.Null : role, ["resolved"] = resolved is null ? JsonValue.Null : resolved };
    }
}
