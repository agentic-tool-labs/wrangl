using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// What <c>create</c> plans from: the members of a history entry or of the resolved roster, their derived names, the models they
/// need, and the shape each launch would take. Planning writes nothing.
/// </summary>
internal static partial class CreatePlanning
{
    private static readonly string[] LayoutValues = ["auto", "columns", "grid"];

    // ---------------------------------------------------------------- history entries

    /// <summary>The history entry <c>--from</c> names: by id, else the most recently used one with that alias.</summary>
    internal static JsonObject ResolveHistoryEntry(RosterContext c, string dir)
    {
        var key = c.Opts.Text("from") ?? "true";
        var history = c.Services.Roster.ReadHistory(dir);
        var teams = history.Get("teams") is { IsArray: true } list ? list.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var byId = teams.FirstOrDefault(t => MemberPlanning.Text(t, "id") == key);
        if (byId is not null)
        {
            return byId;
        }

        JsonObject? best = null;
        foreach (var t in teams.Where(t => MemberPlanning.Text(t, "alias") == key))
        {
            if (best is null || string.CompareOrdinal(MemberPlanning.JsConvertText(best.Get("last_used")), MemberPlanning.JsConvertText(t.Get("last_used"))) < 0)
            {
                best = t;
            }
        }

        if (best is not null)
        {
            return best;
        }

        var ids = string.Join(", ", teams.Select(t => MemberPlanning.JsConvertText(t.Get("id"))));
        throw new RosterFailure(
            $"create --from: no history entry matches {JsonText.Quote(key)} — available ids: {(ids.Length > 0 ? ids : "(none)")}");
    }

    /// <summary>
    /// Stored history members (<c>role/model/effort/route/auto_mode</c>, no name) renamed to the plan's <c>autoMode</c> and
    /// validated as a roster block, which rejects a stale entry instead of repairing it.
    /// </summary>
    internal static List<JsonObject> ValidateHistoryMembers(RosterContext c, JsonObject entry)
    {
        var renamed = HistoryMembers(entry);
        var block = HistoryBlock(c, renamed);
        var errors = c.Services.Launch!.Rules.ValidateRosterBlock(block, c.Registry);
        if (errors.Count > 0)
        {
            throw new RosterFailure(string.Join("; ", errors));
        }

        return renamed;
    }

    private static List<JsonObject> HistoryMembers(JsonObject entry)
    {
        var stored = entry.Get("members") is { IsArray: true } list ? list.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var result = new List<JsonObject>();
        foreach (var m in stored)
        {
            var o = new JsonObject();
            CopyIfPresent(o, "role", m, "role");
            CopyIfPresent(o, "model", m, "model");
            CopyIfPresent(o, "effort", m, "effort");
            CopyIfPresent(o, "route", m, "route");
            CopyIfPresent(o, "autoMode", m, "auto_mode");
            result.Add(o);
        }

        return result;
    }

    private static JsonObject HistoryBlock(RosterContext c, List<JsonObject> renamed)
    {
        var route = renamed.Count > 0 && renamed[0].Get("route") is { IsTruthy: true } r ? r : JsonValue.FromString("peer");
        var members = new JsonArray();
        members.AddRange(renamed.Select(m => (JsonValue)m));
        return c.Services.Config.NormalizeRosterBlock(new JsonObject { ["route"] = route, ["members"] = members }, c.Registry);
    }

    private static void CopyIfPresent(JsonObject target, string key, JsonObject source, string sourceKey)
    {
        if (source.Get(sourceKey) is { } v)
        {
            target[key] = v;
        }
    }

    // ---------------------------------------------------------------- launch shapes

    /// <summary>
    /// A member's launch shape behind the spawn-seam revalidation, with its notices on stderr. A refusal that is the whole command's
    /// ends it.
    /// </summary>
    internal static JsonObject ShapeFor(RosterContext c, JsonValue member, string transport)
    {
        var outcome = c.Services.Launch!.Shapes.ShapeFor(member, transport, c.LaunchContext());
        foreach (var line in outcome.Notices)
        {
            c.Stderr.Write(line);
            c.Stderr.Write('\n');
        }

        if (outcome.Fail is not null)
        {
            throw new RosterFailure(outcome.Fail);
        }

        return outcome.Shape;
    }

    /// <summary><c>{ ...member, route }</c>: the member with the route it launches by.</summary>
    internal static JsonObject WithRoute(JsonObject member, JsonValue? route)
    {
        var copy = member.Clone();
        if (route is { IsNull: false } r)
        {
            copy["route"] = r;
        }
        else
        {
            copy.Remove("route");
        }

        return copy;
    }

    /// <summary>The route a member launches by: its own, else its block's.</summary>
    internal static JsonValue? EffectiveRoute(JsonObject member, JsonValue? blockRoute) =>
        member.Get("route") is { IsTruthy: true } own ? own : blockRoute is { IsTruthy: true } b ? b : (JsonValue?)null;

    /// <summary>One planned member: what it is, where it launches and the shape of the launch.</summary>
    internal static JsonObject PlanEntry(RosterContext c, JsonObject m, JsonValue? blockRoute, string transport)
    {
        var route = EffectiveRoute(m, blockRoute);
        var launch = c.Services.Launch!;
        var entry = new JsonObject();
        CopyIfPresent(entry, "role", m, "role");
        CopyIfPresent(entry, "name", m, "name");
        entry["kind"] = c.Services.Config.ResolveKind(m);
        CopyIfPresent(entry, "model", m, "model");
        CopyIfPresent(entry, "effort", m, "effort");
        if (route is { } r)
        {
            entry["route"] = r;
        }

        CopyIfPresent(entry, "autoMode", m, "autoMode");
        CopyIfPresent(entry, ClaudeEnvironment.Field, m, ClaudeEnvironment.Field);
        entry["args"] = launch.Rules.MemberArgs(m) is { } args ? args : JsonValue.Null;
        entry["spawn"] = MemberPlanning.HasPane(c, route) ? ShapeFor(c, WithRoute(m, route), transport) : JsonValue.Null;
        if (MemberPlanning.Text(m, "role") is { } role && c.Services.Config.RoleParams(c.Cwd, role, c.Registry) is { Count: > 0 } declared)
        {
            var values = new JsonObject();
            foreach (var p in declared)
            {
                values[p.Name] = p.Value is { } v ? v : JsonValue.Null;
            }

            entry["params"] = values;
        }

        return entry;
    }

    /// <summary>
    /// The warning lines for <paramref name="entries"/>' unset params (RP-D6): one per member and name. A missing value never stops a spawn; the member starts and is told.
    /// </summary>
    internal static List<string> MissingParamWarnings(IEnumerable<JsonValue> entries)
    {
        var lines = new List<string>();
        foreach (var e in entries)
        {
            if (e.Member("params") is not { IsMap: true } values)
            {
                continue;
            }

            var map = values.AsObject();
            foreach (var name in map.Keys.Where(k => map[k].IsNull))
            {
                lines.Add($"{MemberPlanning.Text(e.Member("name"))} (role {MemberPlanning.Text(e.Member("role"))}) needs parameter {name}, which is not set for this repo; it was started anyway and told so. Set it: ah roster param set {name} <value>");
            }
        }

        return lines;
    }

    private static JsonArray ToArray(IEnumerable<JsonObject> items)
    {
        var array = new JsonArray();
        array.AddRange(items.Select(i => (JsonValue)i));
        return array;
    }

    // ---------------------------------------------------------------- the plan

    /// <summary>The plan for <c>create</c>'s member source: the history entry of <c>--from</c>, else the live roster.</summary>
    internal static JsonObject GetMembersPlan(RosterContext c, string dir) =>
        c.Opts.Text("from") is not null ? PlanMembersFromHistory(c, ResolveHistoryEntry(c, dir), dir) : ResolveMembersPlan(c, dir);

    /// <summary>The roster's plan: refuse a live team, clear a stale one, then name, validate and shape every member.</summary>
    internal static JsonObject ResolveMembersPlan(RosterContext c, string dir)
    {
        TeamScopes.RefuseOrClearExistingTeam(c, dir);
        var resolved = c.Services.Config.ResolveRoster(c.Cwd, c.RosterArg, c.RepoBasename, c.Registry);
        if (!resolved.IsMap)
        {
            throw new RosterFailure("no roster resolves at any level — hand off to `ah roster init`");
        }

        var transport = c.Services.Launch!.Transports.Detect();
        var blockRoute = resolved.Member("route");
        var source = resolved.Member("members") is { IsArray: true } ms ? ms.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var members = MemberPlanning.RenameInUse(c, source, c.RepoBasename);
        var plan = Plan(c, members, blockRoute, transport, out var planned, out var skipped);
        var layout = CreateLayout(c);
        var result = new JsonObject();
        CopyIfPresent(result, "level", resolved.AsObject(), "level");
        CopyIfPresent(result, "path", resolved.AsObject(), "path");
        if (c.Selection is { } selection && MemberPlanning.Text(selection, "source") != "default")
        {
            result["selection"] = c.Services.Config.SelectionView(selection);
        }

        result["transport"] = transport;
        result["layout"] = layout;
        result["layout_plan"] = LayoutPlan(c, layout, transport, plan);
        result["members"] = ToArray(plan);
        var named = c.Services.Config.NamedRosterKeys(c.Cwd);
        if (named.Count > 0)
        {
            result["named_rosters"] = named;
        }

        MemberPlanning.SpreadRenamed(result, planned);
        return MemberPlanning.WithSkipped(c, result, skipped.Select(m => MemberPlanning.SkippedEntry(c, m)).ToList());
    }

    /// <summary>The plan for a history entry: the same path as the roster's from the named members on.</summary>
    internal static JsonObject PlanMembersFromHistory(RosterContext c, JsonObject entry, string dir)
    {
        TeamScopes.RefuseOrClearExistingTeam(c, dir);
        var renamed = ValidateHistoryMembers(c, entry);
        var block = HistoryBlock(c, renamed);
        var transport = c.Services.Launch!.Transports.Detect();
        var blockRoute = block.Get("route");
        var named = NamedMembers(c, block);
        var members = MemberPlanning.RenameInUse(c, named, c.RepoBasename);
        var plan = Plan(c, members, blockRoute, transport, out var planned, out var skipped);
        var layout = CreateLayout(c);
        var result = new JsonObject
        {
            ["level"] = entry.Get("roster_level") is { IsTruthy: true } level ? level : JsonValue.Null,
            ["path"] = JsonValue.Null,
            ["transport"] = transport,
            ["layout"] = layout,
            ["layout_plan"] = LayoutPlan(c, layout, transport, plan),
            ["members"] = ToArray(plan),
        };
        MemberPlanning.SpreadRenamed(result, planned);
        return MemberPlanning.WithSkipped(c, result, skipped.Select(m => MemberPlanning.SkippedEntry(c, m)).ToList());
    }

    private static List<JsonObject> Plan(
        RosterContext c, List<JsonObject> members, JsonValue? blockRoute, string transport, out List<JsonObject> planned, out List<JsonObject> skipped)
    {
        var models = MemberPlanning.MemberModelOverrides(c, members);
        var named = members
            .Select(m => MemberPlanning.WithModel(m, MemberPlanning.Text(m, "name") is { } n && models.TryGetValue(n, out var model) ? model : null))
            .ToList();
        var defaulted = named.Where(m => !MemberPlanning.HasOwnModel(m) && MemberPlanning.LaunchModel(c, m) is not null).Select(m => MemberPlanning.Text(m, "name")).ToHashSet();
        // create is run by the team's lead: a Claude member launches in the caller's Claude environment
        var rows = named
            .Select(m => MemberPlanning.WithLaunchModel(c, m))
            .Select(m => MemberPlanning.KindOf(c, m) == c.Services.Launch!.Registry.KindDefault ? MemberPlanning.With(m, ClaudeEnvironment.Field, ClaudeEnvironment.Caller(c)) : m)
            .ToList();
        skipped = rows.Where(m => MemberPlanning.HasPane(c, EffectiveRoute(m, blockRoute)) && MemberPlanning.AutoSkipped(c, m)).ToList();
        var left = skipped;
        planned = rows.Where(m => !left.Contains(m)).ToList();
        return planned.Select(m =>
        {
            var entry = PlanEntry(c, m, blockRoute, transport);
            if (defaulted.Contains(MemberPlanning.Text(m, "name")) && MemberPlanning.Text(m, "model") is { } shipped)
            {
                entry["model_note"] = shipped + " (shipped default)";
            }

            return entry;
        }).ToList();
    }

    private static List<JsonObject> NamedMembers(RosterContext c, JsonObject block)
    {
        var members = block.Get("members") is { IsArray: true } ms ? ms.AsArray() : new JsonArray();
        var named = c.Services.Config.RosterMemberNames(members, c.RepoBasename).Select(MemberPlanning.Obj).ToList();
        foreach (var m in named)
        {
            if (MemberPlanning.Text(m, "name") is { } name && name.Length > Ah.Core.Text.MemberNames.MaxLength)
            {
                var message = $"create: member name {name} is longer than {Ah.Core.Text.MemberNames.MaxLength} characters even with the team prefix cut — shorten the role name — nothing was written";
                throw c.Refuse(new JsonObject { ["refused"] = "member-name-too-long", ["name"] = name, ["message"] = message }, message);
            }
        }

        return named;
    }

    private static JsonValue LayoutPlan(RosterContext c, JsonObject layout, string transport, List<JsonObject> plan) =>
        c.Services.Launch!.Layout.LayoutPlan(MemberPlanning.Text(layout, "mode") ?? "auto", transport, plan.Select(m => (JsonValue)m).ToList(), c.Cwd) is { } p
            ? p
            : JsonValue.Null;

    /// <summary>
    /// The members a create run is planned from, named and with their effective routes, without planning it: the history entry's
    /// with <c>--from</c>, else the roster's.
    /// </summary>
    internal static List<JsonObject> CreateSourceMembers(RosterContext c, string dir)
    {
        if (c.Opts.Text("from") is not null)
        {
            var renamed = ValidateHistoryMembers(c, ResolveHistoryEntry(c, dir));
            var block = HistoryBlock(c, renamed);
            var named = MemberPlanning.RenameInUse(c, NamedMembers(c, block), c.RepoBasename);
            return named.Select(m => WithRoute(m, EffectiveRoute(m, block.Get("route")))).ToList();
        }

        var resolved = c.Services.Config.ResolveRoster(c.Cwd, c.RosterArg, c.RepoBasename, c.Registry);
        if (!resolved.IsMap)
        {
            return [];
        }

        var members = resolved.Member("members") is { IsArray: true } ms ? ms.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var route = resolved.Member("route");
        return MemberPlanning.RenameInUse(c, members, c.RepoBasename).Select(m => WithRoute(m, EffectiveRoute(m, route))).ToList();
    }

    // ---------------------------------------------------------------- layout

    /// <summary>The layout a team being created uses and where it came from: an explicit <c>--mode</c>, the stored preference, else <c>auto</c>.</summary>
    internal static JsonObject CreateLayout(RosterContext c)
    {
        if (c.Opts.Has("mode"))
        {
            var mode = c.Opts.Text("mode");
            if (mode is null || !LayoutValues.Contains(mode))
            {
                var shown = mode is null ? "true" : JsonText.Quote(mode);
                throw new RosterFailure($"--mode must be one of {string.Join(", ", LayoutValues)}, got {shown}");
            }

            return new JsonObject { ["mode"] = mode, ["source"] = "explicit" };
        }

        var pref = c.Services.Config.TeamLayoutPreference();
        if (pref.Get("warnings") is { IsArray: true } warnings)
        {
            foreach (var w in warnings.AsArray())
            {
                c.Stderr.Write($"ah roster: {MemberPlanning.JsConvertText(w)}\n");
            }
        }

        if (pref.Get("layout") is { IsTruthy: true, IsText: true } stored)
        {
            return new JsonObject { ["mode"] = stored.AsString(), ["source"] = "stored" };
        }

        return new JsonObject { ["mode"] = "auto", ["source"] = "default" };
    }

    /// <summary>
    /// An explicit <c>--mode</c> on a create that creates something becomes the default layout for future teams. Losing the
    /// preference never fails a create whose team already exists, so every failure is a warning.
    /// </summary>
    internal static void StoreTeamLayout(RosterContext c, JsonObject layout)
    {
        if (MemberPlanning.Text(layout, "source") != "explicit")
        {
            return;
        }

        var mode = MemberPlanning.Text(layout, "mode") ?? string.Empty;
        string reason;
        try
        {
            var path = MemberPlanning.JsConvertText(c.Services.Config.RosterLevelPaths(c.Cwd).Get("global"));
            JsonObject data;
            if (c.Services.Files.Exists(path))
            {
                var parsed = JsonText.Parse(c.Services.Files.ReadText(path));
                if (!parsed.IsMap)
                {
                    throw new InvalidOperationException($"{path} is not a JSON object");
                }

                data = parsed.AsObject();
            }
            else
            {
                data = new JsonObject { ["version"] = c.Services.Config.ConfigVersion };
            }

            if (data.Get("teamLayout") is { IsText: true } current && current.AsString() == mode)
            {
                return;
            }

            data["teamLayout"] = mode;
            new LevelFiles(c).Write(path, data);
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            reason = error.Message;
        }

        c.Stderr.Write(
            $"ah roster: warning — layout \"{mode}\" was not stored as the default for future teams ({reason}); the team itself is unaffected\n");
    }

    // ---------------------------------------------------------------- where the team is recorded

    /// <summary>Warnings a spawn carries about where its team is recorded; nothing here blocks the spawn.</summary>
    internal static List<string> SpawnPlacementWarnings(RosterContext c, string dir)
    {
        var warnings = new List<string>();
        var name = c.TeamFile ?? "(default)";
        var split = c.Services.Roster.SplitTeamCopy(dir, c.TeamFile);
        if (split.IsMap)
        {
            var path = MemberPlanning.JsConvertText(split.Member("path"));
            var home = c.Services.Roster.TeamHome(dir);
            warnings.Add($"team {name} has records in {home} and {TeamPathTail().Replace(path, string.Empty)}");
        }

        var config = c.Services.Config;
        var recorded = config.CheckoutRoot(c.Services.Hierarchy.Messages.RealCwd(c.Cwd));
        var caller = config.CheckoutRoot(c.Services.CurrentDirectory);
        if (!string.IsNullOrEmpty(recorded) && !string.IsNullOrEmpty(caller) && recorded != caller)
        {
            warnings.Add($"team {name} is recorded in {recorded}; teardown run from {caller} will not see it");
        }

        return warnings;
    }

    [GeneratedRegex(@"/teams/[^/]*\z|/team\.json\z")]
    private static partial Regex TeamPathTail();
}
