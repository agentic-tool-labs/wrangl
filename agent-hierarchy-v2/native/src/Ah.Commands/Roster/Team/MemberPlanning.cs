using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// What a team-creating verb does with the members it is about to launch: the models given at invocation, the names under a
/// team prefix, the members left to task-gopher, and the refusals for a member with no model or an advise-class model that is not
/// declared at a top tier.
/// </summary>
internal static class MemberPlanning
{
    private static readonly string[] ChainWorkClasses = ["design", "review", "implement"];

    // ---------------------------------------------------------------- small readers

    internal static string? Text(JsonValue? value) => value is { IsText: true } v ? v.AsString() : null;

    internal static string? Text(JsonObject member, string key) => member.Get(key) is { IsText: true } v ? v.AsString() : null;

    /// <summary>The member's kind: its own, else the default kind.</summary>
    internal static string KindOf(RosterContext c, JsonValue member) =>
        Text(c.Services.Config.ResolveKind(member)) ?? c.Services.Launch!.Registry.KindDefault;

    internal static bool HasPane(RosterContext c, JsonValue? route) => c.Services.Config.RouteHasPane(route);

    internal static JsonObject Obj(JsonValue member) => member.IsMap ? member.AsObject() : new JsonObject();

    /// <summary><c>{ ...member, [key]: value }</c>.</summary>
    internal static JsonObject With(JsonObject member, string key, JsonValue value)
    {
        var copy = member.Clone();
        copy[key] = value;
        return copy;
    }

    // ---------------------------------------------------------------- models given at invocation

    /// <summary>
    /// This invocation's <c>--member-model &lt;name&gt;=&lt;model&gt;</c> values, checked against <paramref name="members"/>, the members it is
    /// planned from: each must name one of them, a claude-kind one, with a model its class allows.
    /// </summary>
    internal static Dictionary<string, string> MemberModelOverrides(RosterContext c, IReadOnlyList<JsonObject> members)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in c.RepeatedFlag("member-model"))
        {
            var eq = value.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new RosterFailure($"{c.Command}: --member-model takes <name>=<model>, got {Quote(value)}");
            }

            var name = value[..eq];
            var model = value[(eq + 1)..];
            if (models.ContainsKey(name))
            {
                throw new RosterFailure($"{c.Command}: --member-model names {name} twice");
            }

            var m = members.FirstOrDefault(x => Text(x, "name") == name);
            if (m is null)
            {
                var names = string.Join(", ", members.Select(x => JsConvertText(x.Get("name"))));
                throw new RosterFailure(
                    $"{c.Command}: --member-model names no member {Quote(name)} — the members are: {(names.Length == 0 ? "(none)" : names)}");
            }

            var errors = InvocationModelErrors(c, m, model);
            if (errors is null)
            {
                throw new RosterFailure(
                    $"{c.Command}: --member-model {name}: {name} is kind {Quote(KindOf(c, m))}, and a model applies only to kind claude");
            }

            if (errors.Count > 0)
            {
                throw new RosterFailure($"{c.Command}: --member-model {name}: {string.Join("; ", errors)}");
            }

            models[name] = model;
        }

        return models;
    }

    /// <summary>
    /// What is wrong with running member <paramref name="m"/> on <paramref name="model"/> this time. Null for a kind with no
    /// model mapping, which cannot take a model at all.
    /// </summary>
    internal static IReadOnlyList<string>? InvocationModelErrors(RosterContext c, JsonObject m, string model)
    {
        var launch = c.Services.Launch!;
        var kind = KindOf(c, m);
        if (kind == launch.Registry.KindDefault)
        {
            var candidate = new JsonObject
            {
                ["role"] = m.Get("role") ?? JsonValue.Null,
                ["model"] = model,
            };
            return launch.Rules.ValidateMember(candidate, c.Registry);
        }

        if (!launch.Harnesses.Has(kind))
        {
            return null;
        }

        var withModel = m.Clone();
        withModel["route"] = "pane";
        withModel["model"] = model;
        return launch.Rules.KindFieldErrors(withModel, c.Registry);
    }

    /// <summary>
    /// The model <paramref name="m"/> launches on, or null when it has to be asked for: the member's own, else, for a claude-kind
    /// member whose role ships a member default, the role's effective model (the table's unless <c>role set --model</c> re-pointed
    /// it). Every path that fills or asks for a member's model goes through here, so they cannot disagree.
    /// </summary>
    internal static string? LaunchModel(RosterContext c, JsonObject m)
    {
        if (HasOwnModel(m))
        {
            return JsConvertText(m.Get("model"));
        }

        if (c.Services.Launch is not { } launch || Text(m, "role") is not { } role || ConfigModule.MemberDefaultModel(role) is not { } shipped
            || KindOf(c, m) != launch.Registry.KindDefault)
        {
            return null;
        }

        return c.Registry?.Get("roles")?.Member(role)?.Member("model") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : shipped;
    }

    /// <summary>Whether <paramref name="m"/> names a model itself.</summary>
    internal static bool HasOwnModel(JsonObject m) => m.Get("model") is { IsTruthy: true };

    /// <summary><paramref name="m"/> with <see cref="LaunchModel"/> as its model when it names none and one resolves.</summary>
    internal static JsonObject WithLaunchModel(RosterContext c, JsonObject m) =>
        !HasOwnModel(m) && LaunchModel(c, m) is { } model ? With(m, "model", model) : m;

    /// <summary>The member with <paramref name="model"/> in place of its own when one is supplied at invocation.</summary>
    internal static JsonObject WithModel(JsonObject m, string? model) => model is null ? m : With(m, "model", model);

    // ---------------------------------------------------------------- names

    /// <summary>
    /// <paramref name="members"/>, named under team <paramref name="prefix"/>, with each colliding one renamed to
    /// <c>&lt;prefix&gt;-&lt;role&gt;-&lt;n&gt;</c> and given <c>renamed_from</c>, the name it was derived as. A member collides when its
    /// name matches a <c>--names-in-use</c> value, or when the name it was derived as is held by a record another member claimed.
    /// </summary>
    internal static List<JsonObject> RenameInUse(
        RosterContext c, IReadOnlyList<JsonObject> members, string prefix, IReadOnlyList<JsonValue>? records = null, Func<string, bool>? ownLive = null)
    {
        var inUse = new HashSet<string>(c.RepeatedFlag("names-in-use"), StringComparer.Ordinal);
        var recordNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records ?? [])
        {
            if (r.Member("name") is { IsTruthy: true, IsText: true } n)
            {
                recordNames.Add(n.AsString());
            }
        }

        var ownCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Own(string name)
        {
            if (!ownCache.TryGetValue(name, out var v))
            {
                v = recordNames.Contains(name) && ownLive is not null && ownLive(name);
                ownCache[name] = v;
            }

            return v;
        }

        string Derived(JsonObject m) => Text(RosterJson.Truthy(m.Get("derived"))) ?? Text(m, "name") ?? string.Empty;

        bool ClaimedByOther(JsonObject m) => members.Any(x =>
            !ReferenceEquals(x, m) && x.Get("record") is { IsMap: true } rec && Text(rec.Member("name")) == Derived(m));

        bool Collides(JsonObject m) => (inUse.Contains(Text(m, "name") ?? string.Empty) && !Own(Text(m, "name") ?? string.Empty)) || ClaimedByOther(m);

        if (!members.Any(Collides))
        {
            return [.. members];
        }

        var taken = new HashSet<string>(recordNames, StringComparer.Ordinal);
        var result = new List<JsonObject>();
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (!Collides(m))
            {
                taken.Add(Text(m, "name") ?? string.Empty);
                result.Add(m);
                continue;
            }

            var kept = new HashSet<string>(StringComparer.Ordinal);
            for (var j = i + 1; j < members.Count; j++)
            {
                if (!Collides(members[j]))
                {
                    kept.Add(Derived(members[j]));
                }
            }

            var role = Text(m, "role") ?? string.Empty;
            var n = 2;
            while (inUse.Contains(MemberNames.Fit(prefix, role, n)) || taken.Contains(MemberNames.Fit(prefix, role, n)) || kept.Contains(MemberNames.Fit(prefix, role, n)))
            {
                n++;
            }

            taken.Add(MemberNames.Fit(prefix, role, n));
            var renamed = m.Clone();
            renamed["name"] = MemberNames.Fit(prefix, role, n);
            renamed["renamed_from"] = Derived(m);
            result.Add(renamed);
        }

        return result;
    }

    /// <summary>
    /// spawn-one's roster members of one role, each holding the team record it stands for (<c>record</c>): first a record renamed away
    /// from the member's derived name, which the member takes with its name and <c>renamed_from</c>, then a record under that derived
    /// name. Each record goes to one member at most.
    /// </summary>
    internal static List<JsonObject> ClaimRecords(IReadOnlyList<JsonObject> members, IReadOnlyList<JsonValue> records)
    {
        var claimed = new HashSet<int>();
        JsonValue? Claim(Func<JsonValue, bool> match)
        {
            for (var i = 0; i < records.Count; i++)
            {
                if (!claimed.Contains(i) && match(records[i]))
                {
                    claimed.Add(i);
                    return records[i];
                }
            }

            return null;
        }

        var first = new List<JsonObject>();
        foreach (var m in members)
        {
            var name = Text(m, "name");
            var r = Claim(x => x.Member("renamed_from") is { IsText: true } rf && rf.AsString() == name);
            if (r is { } rec)
            {
                var copy = m.Clone();
                copy["name"] = rec.Member("name") ?? JsonValue.Null;
                if (rec.Member("renamed_from") is { } from)
                {
                    copy["renamed_from"] = from;
                }

                copy["derived"] = name is null ? JsonValue.Null : name;
                copy["record"] = rec;
                first.Add(copy);
            }
            else
            {
                first.Add(m);
            }
        }

        var result = new List<JsonObject>();
        foreach (var m in first)
        {
            if (m.Get("record") is not null)
            {
                result.Add(m);
                continue;
            }

            var name = Text(m, "name");
            var r = Claim(x => x.Member("name") is { IsText: true } rn && rn.AsString() == name);
            result.Add(r is { } rec ? With(m, "record", rec) : m);
        }

        return result;
    }

    /// <summary><c>renamed_members</c> for an output: each of <paramref name="members"/> that was renamed; null when none was.</summary>
    internal static JsonArray? RenamedMembers(IEnumerable<JsonObject> members)
    {
        var renamed = new JsonArray();
        foreach (var m in members)
        {
            if (m.Get("renamed_from") is { IsTruthy: true } from)
            {
                renamed.Add(new JsonObject
                {
                    ["name"] = m.Get("name") ?? JsonValue.Null,
                    ["role"] = m.Get("role") ?? JsonValue.Null,
                    ["renamed_from"] = from,
                });
            }
        }

        return renamed.Count > 0 ? renamed : null;
    }

    /// <summary>Sets <c>renamed_members</c> on <paramref name="target"/> when any of the members was renamed.</summary>
    internal static void SpreadRenamed(JsonObject target, IEnumerable<JsonObject> members)
    {
        if (RenamedMembers(members) is { } renamed)
        {
            target["renamed_members"] = renamed;
        }
    }

    // ---------------------------------------------------------------- members left to task-gopher

    private static bool TaskGopherInstalled(RosterContext c)
    {
        if (c.Opts.IsTrue("no-legwork-handoff"))
        {
            return false;
        }

        c.TaskGopherInstalled ??= c.Services.Config.LocateAgentFile(c.Services.Config.TaskGopher, c.Cwd).Get("path") is { IsTruthy: true };
        return c.TaskGopherInstalled.Value;
    }

    /// <summary>A claude-kind legwork member with no model is not launched while task-gopher is installed.</summary>
    internal static bool AutoSkipped(RosterContext c, JsonObject m) =>
        c.Services.Config.LegworkHandedOff(m, c.Registry, () => TaskGopherInstalled(c));

    internal static JsonObject SkippedEntry(RosterContext c, JsonObject m) => new()
    {
        ["name"] = m.Get("name") ?? JsonValue.Null,
        ["role"] = m.Get("role") ?? JsonValue.Null,
        ["handoff"] = c.Services.Config.TaskGopher,
    };

    /// <summary><paramref name="obj"/> with <c>skipped_members</c>, and what to do about them, when there are any.</summary>
    internal static JsonObject WithSkipped(RosterContext c, JsonObject obj, IReadOnlyList<JsonObject> skipped)
    {
        if (skipped.Count == 0)
        {
            return obj;
        }

        var tg = c.Services.Config.TaskGopher;
        var one = skipped.Count == 1;
        var names = string.Join(", ", skipped.Select(m => JsConvertText(m.Get("name"))));
        var message =
            $"{names} {(one ? "has" : "have")} no model and task-gopher is installed, so {(one ? "it was" : "they were")} not launched: " +
            $"send that legwork to {tg} subagents, and do not ask the user about {(one ? "it" : "them")}. " +
            $"If {tg} is not among your available agent types (installed but not enabled), tell the user which member was not launched and why: " +
            "adding it means `disband`, then `create --no-legwork-handoff`, which lists it for a model like any other member. " +
            $"A driver that cannot dispatch {tg} passes --no-legwork-handoff on every create phase from the start.";
        var copy = obj.Clone();
        var list = new JsonArray();
        list.AddRange(skipped.Select(s => (JsonValue)s));
        copy["skipped_members"] = list;
        copy["message"] = message;
        return copy;
    }

    // ---------------------------------------------------------------- members with no model

    /// <summary>
    /// What a member of class <paramref name="cls"/> with no model borrows: the highest-tier model among the claude-kind design,
    /// review and implement members of <paramref name="pool"/> that <paramref name="cls"/> allows, the first in roster order on a tie.
    /// </summary>
    private static JsonObject? MemberFallback(RosterContext c, string? cls, IReadOnlyList<JsonObject> pool)
    {
        if (cls == "legwork" || cls is null)
        {
            return null;
        }

        var launch = c.Services.Launch!;
        var allowed = launch.Registry.ClassModels(cls) ?? [];
        (int Tier, string Model, string From)? best = null;
        foreach (var m in pool)
        {
            if (KindOf(c, m) != launch.Registry.KindDefault)
            {
                continue;
            }

            var role = Text(m, "role");
            var roleCls = role is null ? null : launch.Registry.RoleClass(role, c.Registry);
            if (roleCls is null || !ChainWorkClasses.Contains(roleCls))
            {
                continue;
            }

            var model = Text(m, "model");
            var tier = c.Services.Config.TierOf(m.Get("model"));
            if (tier is null || model is null || !allowed.Contains(model))
            {
                continue;
            }

            if (best is null || tier > best.Value.Tier)
            {
                best = (tier.Value, model, Text(m, "name") ?? string.Empty);
            }
        }

        return best is { } b ? new JsonObject { ["model"] = b.Model, ["from"] = b.From } : null;
    }

    /// <summary>The models of <paramref name="kind"/> declared at one of <paramref name="tiers"/>, in declaration order.</summary>
    internal static List<string> DeclaredModelsAt(RosterContext c, string kind, IReadOnlyList<string> tiers)
    {
        var result = new List<string>();
        if (c.Services.Config.DeclaredModelTiers().Get("tiers") is { IsMap: true } all && all.AsObject().Get(kind) is { IsMap: true } byModel)
        {
            var models = byModel.AsObject();
            foreach (var model in models.Keys)
            {
                if (models[model] is { IsText: true } tier && tiers.Contains(tier.AsString()))
                {
                    result.Add(model);
                }
            }
        }

        return result;
    }

    /// <summary>The claude-kind (and harness-kind) members of <paramref name="launching"/> with no model, each with what it may take.</summary>
    internal static JsonArray MembersNeedingModel(RosterContext c, IEnumerable<JsonObject> launching, IReadOnlyList<JsonObject> pool)
    {
        var launch = c.Services.Launch!;
        var result = new JsonArray();
        foreach (var m in launching)
        {
            var kind = KindOf(c, m);
            var isDefault = kind == launch.Registry.KindDefault;
            if (LaunchModel(c, m) is not null || !(isDefault || launch.Harnesses.Has(kind)))
            {
                continue;
            }

            var role = Text(m, "role") ?? string.Empty;
            var cls = launch.Registry.RoleClass(role, c.Registry);
            var entry = new JsonObject
            {
                ["name"] = m.Get("name") ?? JsonValue.Null,
                ["role"] = m.Get("role") ?? JsonValue.Null,
            };
            if (cls is not null)
            {
                entry["class"] = cls;
            }

            if (!isDefault)
            {
                entry["kind"] = kind;
                if (cls == "advise")
                {
                    var allowed = new JsonArray();
                    allowed.AddRange(DeclaredModelsAt(c, kind, RosterModule.AdviseTiers).Select(s => (JsonValue)s));
                    entry["allowed"] = allowed;
                }
                else
                {
                    entry["allowed"] = JsonValue.Null;
                }

                entry["fallback"] = JsonValue.Null;
                entry["models_command"] = RosterJson.Text(launch.Harnesses.ModelsCommand(kind));
            }
            else
            {
                var allowed = new JsonArray();
                if (cls is not null && launch.Registry.ClassModels(cls) is { } models)
                {
                    allowed.AddRange(models.Select(s => (JsonValue)s));
                }

                entry["allowed"] = allowed;
                entry["fallback"] = MemberFallback(c, cls, pool) is { } fb ? fb : JsonValue.Null;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>A create plan's pane- and peer-routed members that have no model.</summary>
    internal static JsonArray PlanNeedingModel(RosterContext c, JsonArray plan)
    {
        var members = plan.Select(Obj).ToList();
        return MembersNeedingModel(c, members.Where(m => HasPane(c, m.Get("route"))), members);
    }

    /// <summary>
    /// The refusal for members about to launch with no model. A member's model is the user's to choose, so the CLI never applies the
    /// fallback itself: it names the members and hands back two reruns, one with a literal &lt;MODEL&gt; per member and one with each
    /// fallback filled in, for a caller that cannot ask. <paramref name="adHoc"/>: the member is launched as spawn-ad-hoc launches one
    /// (a spawn-one of a role the roster lacks hands over to it), so the reruns carry only the flags spawn-ad-hoc takes.
    /// </summary>
    internal static RosterFailure RefuseMemberModels(RosterContext c, JsonArray needing, bool adHoc = false)
    {
        var cmd = c.Command;
        var shape = adHoc ? "spawn-ad-hoc" : cmd;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in needing)
        {
            if (Text(n.Member("name")) is { } name)
            {
                listed.Add(name);
            }
        }

        bool Drop(string flag, string? value) => shape switch
        {
            "create" => flag == "member-model" && value is not null
                && listed.Contains(value.Contains('=', StringComparison.Ordinal) ? value[..value.IndexOf('=', StringComparison.Ordinal)] : value[..^1]),
            "spawn-one" => flag is "member" or "model",
            _ => flag == "model",
        };

        string Tail(Func<JsonValue, string> modelOf) => shape switch
        {
            "create" => string.Join(' ', needing.Select(m => $"--member-model {RosterContext.ArgWord(JsConvertText(m.Member("name")))}={modelOf(m)}")),
            "spawn-one" => $"--member {RosterContext.ArgWord(JsConvertText(needing[0].Member("name")))} --model {modelOf(needing[0])}",
            _ => $"--model {modelOf(needing[0])}",
        };

        var rerun = c.RerunCommand(Drop, Tail(_ => "<MODEL>"));
        var everyFallback = needing.All(m => m.Member("fallback") is { IsMap: true });
        var rerunFallback = everyFallback
            ? c.RerunCommand(Drop, Tail(m => JsConvertText(m.Member("fallback")!.Value.Member("model"))))
            : null;

        string AllowedText(JsonValue m)
        {
            var kind = m.Member("kind");
            var allowed = m.Member("allowed");
            var items = allowed is { IsArray: true } a ? string.Join(", ", a.AsArray().Select(x => JsConvertText(x))) : string.Empty;
            if (kind is null)
            {
                return items;
            }

            if (allowed is null or { IsNull: true })
            {
                return $"any model {JsConvertText(kind)} accepts, which `{JsConvertText(m.Member("models_command"))}` lists";
            }

            return items.Length > 0 ? items : $"no {JsConvertText(kind)} model is declared opus or fable yet";
        }

        string Fallback(JsonValue m) => m.Member("fallback") is { IsMap: true } fb
            ? $"{JsConvertText(fb.Member("model"))}, from {JsConvertText(fb.Member("from"))}"
            : "none";

        var who = string.Join(
            "; ",
            needing.Select(m =>
                $"{JsConvertText(m.Member("name"))} ({JsConvertText(m.Member("role"))}; allowed: {AllowedText(m)}; fallback: {Fallback(m)})"));
        var harnesses = needing.Where(m => m.Member("kind") is not null).Select(m => JsConvertText(m.Member("kind"))).Distinct().ToList();
        var harnessNote = string.Concat(harnesses.Select(kind =>
            $"For a {kind} member, run `{c.Services.Launch!.Harnesses.ModelsCommand(kind)}` and offer the names it lists. " +
            $"An advise-class {kind} member's model must also be declared opus or fable: any model outside its allowed list needs `ah roster tier set {kind} <model> <tier>` first. "));
        var message =
            $"No model is defined for {(needing.Count == 1 ? "this member" : "these members")}: {who}. {harnessNote}A member's model is the user's choice: " +
            "ask the user with AskUserQuestion, one question per member, with its fallback model as the first option when it has one, " +
            $"then re-run {rerun} with each <MODEL> replaced by the answer. " +
            (rerunFallback is not null
                ? $"Only a top-level session that cannot ask the user (a non-interactive run: claude -p, the SDK, headless) runs the fallback rerun instead: {rerunFallback}. "
                : $"rerun_fallback is null — there is no fallback for {string.Join(", ", needing.Where(m => m.Member("fallback") is not { IsMap: true }).Select(m => JsConvertText(m.Member("name"))))} — so a session that cannot ask the user stops and reports these members. ") +
            "A subagent never runs rerun_fallback: it returns this refusal to its caller. Never choose a model any other way. " +
            "This is not a launch failure.";
        var fields = new JsonObject
        {
            ["refused"] = "member-model-undefined",
            ["verb"] = cmd,
            ["members"] = needing,
            ["rerun"] = rerun,
            ["rerun_fallback"] = RosterJson.Text(rerunFallback),
            ["message"] = message,
        };
        return c.Refuse(fields, message);
    }

    // ---------------------------------------------------------------- the advise-class model lock

    /// <summary>True for a non-claude advise-class member whose model is not declared at a top tier.</summary>
    internal static bool AdviseTierBlocked(RosterContext c, JsonObject m)
    {
        var launch = c.Services.Launch!;
        var kind = KindOf(c, m);
        var role = Text(m, "role") ?? string.Empty;
        if (kind == launch.Registry.KindDefault || launch.Registry.RoleClass(role, c.Registry) != "advise")
        {
            return false;
        }

        if (!launch.Harnesses.Has(kind))
        {
            return true;
        }

        var declared = c.Services.Config.DeclaredTier(kind, m.Get("model"));
        return !(declared is { IsText: true } d && RosterModule.AdviseTiers.Contains(d.AsString()));
    }

    /// <summary>The refusal for <see cref="AdviseTierBlocked"/>: nothing is launched or written.</summary>
    internal static RosterFailure RefuseAdviseTier(RosterContext c, JsonObject m)
    {
        var launch = c.Services.Launch!;
        var kind = KindOf(c, m);
        var name = JsConvertText(m.Get("name"));
        var needs = new JsonArray();
        needs.AddRange(RosterModule.AdviseTiers.Select(s => (JsonValue)s));
        if (!launch.Harnesses.Has(kind))
        {
            var message = $"{launch.Rules.UnmappedAdviseMessage(kind)} Nothing was launched or written.";
            return c.Refuse(
                new JsonObject
                {
                    ["refused"] = "advise-model-tier",
                    ["member"] = name,
                    ["kind"] = kind,
                    ["model"] = JsonValue.Null,
                    ["declared_tier"] = JsonValue.Null,
                    ["needs"] = needs,
                    ["rerun_declare"] = JsonValue.Null,
                    ["message"] = message,
                },
                message);
        }

        var declared = c.Services.Config.DeclaredTier(kind, m.Get("model"));
        var declaredText = declared is { IsText: true } d ? d.AsString() : null;
        var modelText = JsConvertText(m.Get("model"));
        var rerunDeclare = $"{c.SelfCommand} tier set {kind} {RosterContext.ArgWord(modelText)} <TIER> --cwd {RosterContext.ArgWord(c.Cwd)}";
        var roleLabel = JsConvertText(c.Services.Config.RoleLabel(Text(m, "role") ?? string.Empty, c.Registry));
        var text = declaredText is null
            ? $"{name} is an advise-class member on {kind} model {modelText}, which has no declared tier, and an advise-class model must be declared opus or fable. " +
                $"Ask the user with AskUserQuestion, header \"Model tier\": \"How does `{modelText}` compare with Claude models?\", with the options fable, opus, sonnet and haiku. " +
                $"Record the answer with {rerunDeclare}, <TIER> replaced by it, then re-run this command. Nothing was launched or written."
            : $"{name} is an advise-class member on {kind} model {modelText}, which is declared {declaredText}, and an advise-class model must be declared opus or fable, " +
                $"so this model cannot be the {roleLabel}. Tell the user so and ask for another model; never re-declare a tier yourself. Nothing was launched or written.";
        return c.Refuse(
            new JsonObject
            {
                ["refused"] = "advise-model-tier",
                ["member"] = name,
                ["kind"] = kind,
                ["model"] = m.Get("model") ?? JsonValue.Null,
                ["declared_tier"] = RosterJson.Text(declaredText),
                ["needs"] = needs,
                ["rerun_declare"] = rerunDeclare,
                ["message"] = text,
            },
            text);
    }

    // ---------------------------------------------------------------- text

    private static string Quote(string text) => JsonText.Quote(text);

    internal static string JsConvertText(JsonValue? value) => JsConvert.ToJsString(value);
}
