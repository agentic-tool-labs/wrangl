using Ah.Abstractions;
using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>What the verb and the route agree a write is, before a scope has been turned into its stored form.</summary>
internal sealed record RuleAsk(string Op, string Dir, string Level, string ScopeText, string Key, string? Text, string By, string? Said, string? Applies = null, string? Choose = null);

/// <summary>The one path into the rules table, for this route and the bridge's.</summary>
internal static class RulesWrites
{
    /// <summary>Puts a work id scope in stored form and refuses one of the wrong kind; then writes.</summary>
    internal static RuleWriteResult Apply(ServiceGraph g, string ahHome, IStateDb db, RuleAsk ask, DateTimeOffset now)
    {
        var scope = ask.ScopeText;
        if (ask.Level is RulesOfEngagement.Project or RulesOfEngagement.Goal && ask.ScopeText.Length > 0)
        {
            if (WorkVerb.Resolve(g, ahHome, ask.Dir, ask.ScopeText, null).Row is not { } row)
            {
                return new RuleWriteResult(RulesOfEngagement.NotFound, $"no work item {ask.ScopeText}", null);
            }

            var kind = RulesReads.With<string?>(ahHome, null, query => HierarchyModule.WorkItemOf(query, g.Hierarchy.Progress, ask.Dir, row).Plan?.Kind);
            if (kind != ask.Level)
            {
                return new RuleWriteResult(RulesOfEngagement.Refused, $"{ask.ScopeText} is a {kind ?? "task"}, not a {ask.Level}", null);
            }

            scope = row.N.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var chain = ask.Level == RulesOfEngagement.Team ? RulesReads.TeamChain(g, ask.Dir, scope).Skip(1).ToList() : [];
        var text = ask.Text;
        // ah2-512 S5: a cap's text is generated, never typed
        if (ask.Op == "cap" && ask.Key.StartsWith(RulesOfEngagement.CapKeyPrefix, StringComparison.Ordinal))
        {
            var where = ask.Level == RulesOfEngagement.Team
                ? "per run"
                : $"for {RulesJson.ScopeOf(new RuleRow(ask.Level, scope, ask.Key, string.Empty, false, ask.By, null, 0, 0), RulesReads.With<string?>(ahHome, null, query => HierarchyModule.WorkPrefix(query, ask.Dir)))}";
            text = RulesOfEngagement.CapText(ask.Key[RulesOfEngagement.CapKeyPrefix.Length..], ask.Applies ?? string.Empty, where, ask.By);
        }

        RuleWriteResult? result = null;
        var outcome = db.WriteOnce(Guid.NewGuid().ToString("N"), now.AddSeconds(10), tx =>
        {
            result = RulesOfEngagement.Apply(
                (sql, args) => tx.Query(sql, args),
                (sql, args) => tx.Execute(sql, args),
                new RuleWrite(ask.Op, ask.Dir, ask.Level, scope, ask.Key, text, ask.By, ask.Said, now.ToUnixTimeMilliseconds(), chain, ask.Applies, ask.Choose));
            return string.Empty;
        });
        return outcome.Status == OnceStatus.Applied && result is not null
            ? result
            : new RuleWriteResult(RulesOfEngagement.Refused, $"the state database did not take the write: {outcome.Message}", null);
    }
}

/// <summary>
/// <c>POST /v1/rules</c>: writes a rule in the daemon, which is where the caller is proven from the peer process. Only who may write is checked, by the rule that guards a
/// cutover: this is a narrow write check, not a behaviour gate. The refusal text names who may. <c>said</c> is recorded, not verified.
/// <paramref name="goalWritten"/> is told the dir and team of each goal write that took, so the read model re-reads that team (ah2-53).
/// </summary>
internal sealed class RulesRoute(ServiceGraph g, string ahHome, Func<IStateDb>? stateDb, Func<int?, CutoverCaller> callerOf, Func<DateTimeOffset> now, Func<int?, string?>? startTimeOf = null, Action<string, string>? goalWritten = null)
{
    public const string Path = "POST /v1/rules";

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static RouteResponse Json(int status, JsonObject body) => new(status, NoHeaders, JsonText.ToUtf8(body, 0));

    private static RouteResponse Answer(int code, string message) => Json(200, new JsonObject { ["code"] = code, ["message"] = message });

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : null;

    public Task<RouteResponse> HandleAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        JsonObject body;
        try
        {
            body = JsonText.Parse(request.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : throw new JsonParseException("not an object");
        }
        catch (JsonParseException)
        {
            return Task.FromResult(Json(400, new JsonObject { ["error"] = "body must be a JSON object" }));
        }

        if (Text(body, "op") is not { } op || !(RulesOfEngagement.Ops.Contains(op, StringComparer.Ordinal) || RulesOfEngagement.GoalOps.Contains(op, StringComparer.Ordinal) || RulesOfEngagement.CapOps.Contains(op, StringComparer.Ordinal)) || Text(body, "dir") is not { Length: > 0 } dir
            || Text(body, "level") is not { } level || Text(body, "key") is not { } key)
        {
            return Task.FromResult(Json(400, new JsonObject { ["error"] = "op, dir, level and key are required" }));
        }

        var pinned = startTimeOf?.Invoke(request.PeerPid);
        var caller = callerOf(request.PeerPid);
        if (startTimeOf is not null && (pinned is null || startTimeOf(request.PeerPid) != pinned))
        {
            return Task.FromResult(Answer(RulesOfEngagement.Refused, "the calling process could not be pinned, so nothing was written; only the user or the top-level Orchestrator may write rules"));
        }

        // accepting a decision rule is the user's alone (spec-b19 slice 3); any lead may propose one
        if (op == "accept" && caller.Kind != CutoverCallerKind.User)
        {
            return Task.FromResult(Answer(RulesOfEngagement.NotUser, $"only the user may accept a decision rule; the caller is a {caller.Name} ({caller.Detail}). Run ah rules accept in your own terminal or use the web rules dialog"));
        }

        if (op == "propose" ? caller.Kind is not (CutoverCallerKind.User or CutoverCallerKind.TopOrchestrator or CutoverCallerKind.SubOrchestrator) : !caller.MayChangeAuthority)
        {
            return Task.FromResult(Answer(RulesOfEngagement.Refused, op == "propose"
                ? $"only the user or a lead may propose a decision rule; the caller is a {caller.Name} ({caller.Detail})"
                : $"only the user or the top-level Orchestrator may write rules; the caller is a {caller.Name} ({caller.Detail})"));
        }

        var said = Text(body, "said");
        if (op != "propose" && caller.Kind == CutoverCallerKind.TopOrchestrator && string.IsNullOrWhiteSpace(said))
        {
            return Task.FromResult(Answer(RulesOfEngagement.Refused, "an Orchestrator writes a rule on the user's own word: pass them with --said"));
        }

        if (stateDb is null)
        {
            return Task.FromResult(Json(503, new JsonObject { ["error"] = "the daemon has no state database" }));
        }

        var result = RulesWrites.Apply(g, ahHome, stateDb(), new RuleAsk(op, dir, level, Text(body, "scope") ?? string.Empty, key, Text(body, "text"), caller.Name, said, Text(body, "applies"), Text(body, "choose")), now());
        var answer = RulesJson.Answer(result, RulesReads.With<string?>(ahHome, null, q => HierarchyModule.WorkPrefix(q, dir)));
        if (RulesOfEngagement.GoalOps.Contains(op, StringComparer.Ordinal))
        {
            if (!answer.ContainsKey("goal"))
            {
                answer["goal"] = JsonValue.Null;
            }

            if (result.Code == RulesOfEngagement.Ok && Text(body, "scope") is { Length: > 0 } team)
            {
                goalWritten?.Invoke(dir, team);
            }
        }

        return Task.FromResult(Json(200, answer));
    }
}

/// <summary>How a rule reads in JSON: a project or goal scope by its work id.</summary>
internal static class RulesJson
{
    internal static string ScopeOf(RuleRow row, string? prefix) =>
        row.Level is RulesOfEngagement.Project or RulesOfEngagement.Goal && prefix is not null && long.TryParse(row.Scope, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? HierarchyModule.WorkId(prefix, n)
            : row.Scope;

    /// <summary>A write's outcome as both routes answer it.</summary>
    internal static JsonObject Answer(RuleWriteResult result, string? prefix)
    {
        var answer = new JsonObject { ["code"] = result.Code, ["message"] = result.Message };
        if (result.Reason is { } reason)
        {
            answer["reason"] = reason;
        }
        if (result.Row is { Kind: RulesOfEngagement.GoalKind } goal)
        {
            answer["goal"] = Goal(goal);
        }
        else if (result.Row is { } row)
        {
            answer["rule"] = Row(row, prefix);
        }

        if (result.Code == RulesOfEngagement.Ok)
        {
            answer["live"] = RulesOfEngagement.Live;
        }

        return answer;
    }

    /// <summary>The resolver's answer, as <c>ah rules list --json</c> prints it.</summary>
    internal static JsonObject Resolved(ResolvedRules resolved, string? prefix)
    {
        var rules = new JsonArray();
        foreach (var rule in resolved.Rules)
        {
            var overrides = new JsonArray();
            foreach (var level in rule.Overrides)
            {
                overrides.Add(level);
            }

            rules.Add(Decide(new JsonObject
            {
                ["key"] = rule.Row.Key,
                ["text"] = rule.Row.Text,
                ["level"] = rule.Row.Level,
                ["scope"] = ScopeOf(rule.Row, prefix),
                ["by"] = rule.Row.By,
                ["said"] = rule.Row.Said is { } said ? said : JsonValue.Null,
                ["updated_ms"] = rule.Row.UpdatedMs,
                ["overrides"] = overrides,
            }, rule.Row));
        }

        var off = new JsonArray();
        foreach (var row in resolved.Off)
        {
            off.Add(new JsonObject { ["key"] = row.Key, ["level"] = row.Level, ["scope"] = ScopeOf(row, prefix) });
        }

        return new JsonObject { ["rules"] = rules, ["off"] = off };
    }

    /// <summary>A team goal as <c>ah rules goal --json</c> reads it; <c>team</c> is the team whose row it is.</summary>
    internal static JsonObject Goal(RuleRow goal) => new()
    {
        ["text"] = goal.Text,
        ["done_when"] = goal.Applies is { } doneWhen ? doneWhen : JsonValue.Null,
        ["team"] = goal.Scope,
        ["by"] = goal.By,
        ["said"] = goal.Said is { } said ? said : JsonValue.Null,
        ["updated_ms"] = goal.UpdatedMs,
    };

    /// <summary>Every stored field of a row.</summary>
    internal static JsonObject Row(RuleRow row, string? prefix) => Decide(
        new()
        {
            ["key"] = row.Key,
            ["text"] = row.Text,
            ["off"] = row.Off,
            ["level"] = row.Level,
            ["scope"] = ScopeOf(row, prefix),
            ["by"] = row.By,
            ["said"] = row.Said is { } said ? said : JsonValue.Null,
            ["created_ms"] = row.CreatedMs,
            ["updated_ms"] = row.UpdatedMs,
        },
        row);

    /// <summary>A decision rule's fields; a guide rule's JSON is unchanged.</summary>
    private static JsonObject Decide(JsonObject o, RuleRow row)
    {
        if (row.IsDecision)
        {
            o["kind"] = row.Kind;
            o["applies"] = row.Applies ?? string.Empty;
            o["choose"] = row.Choose ?? string.Empty;
            o["proposed"] = row.IsProposed;
            o["accepted_at"] = row.AcceptedAt is { } at ? at : JsonValue.Null;
            o["accepted_by"] = row.AcceptedBy is { } by ? by : JsonValue.Null;
        }

        return o;
    }
}
