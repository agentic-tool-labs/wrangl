using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

internal sealed partial class HierLib : IHierarchyDecisions
{
    private static readonly string[] Letters = ["a", "b", "c", "d"];

    private static readonly string[] WhyUser = ["dangerous", "unsure", "cap"];

    private static readonly string[] InputFields =
        ["kind", "item", "source", "question", "options", "default", "choice", "decider", "rationale", "revert", "review", "why_user", "dangerous", "exchange"];

    /// <summary>The class of process that wrote a line, set by the verb that proved it; optional here so a line written by another path still reads.</summary>
    private const string CallerField = "caller";

    /// <summary>The optional input field that makes a parked question multi-select; written only when true, so a single-choice line is unchanged.</summary>
    private const string MultiField = "multi";

    /// <summary>The answered line's structured multi-select value, written on every answer to a multi question and on no other.</summary>
    private const string ChoicesField = "choices";

    private static bool IsMulti(JsonObject obj) => obj.Get(MultiField) is { IsBool: true } m && m.AsBool();

    private static bool IsMultiLine(JsonValue parked) => parked.Member(MultiField) is { IsBool: true } m && m.AsBool();

    private static readonly string[] DeciderKeys = ["role", "name", "model"];

    private static readonly string[] WriterFields = ["id", "time", "run", "decision", "qkey"];

    private const int RefusedDetailMax = 160;

    [GeneratedRegex("^other: [^" + Ws + "]", RegexOptions.CultureInvariant)]
    private static partial Regex OtherChoiceRegex();

    [GeneratedRegex("^[a-z0-9-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeciderRoleRegex();

    [GeneratedRegex("^## (?:\\[[0-9]+\\] )?constraints(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ConstraintsHeadingRegex();

    [GeneratedRegex(
        "^[" + Ws + "]*(?:[-*][" + Ws + "]+)?`?(?<key>[a-z][a-z0-9-]*):[" + Ws + "]*(?<value>[^\\n\\r\\u2028\\u2029]*?)`?[" + Ws + "]*\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConstraintLineRegex();

    [GeneratedRegex(
        "^[" + Ws + "]*gh[ \\t]+pr[ \\t]+merge[ \\t]+(?<pr>[1-9][0-9]*)[ \\t]+--match-head-commit[ \\t]+(?<sha>[0-9a-f]{40})[ \\t]+--(?<method>merge|squash|rebase)[" + Ws + "]*\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex SingleMergeRegex();

    [GeneratedRegex(
        "^[" + Ws + "]*gh[ \\t]+pr[ \\t]+ready[ \\t]+(?<ready>[1-9][0-9]*)[ \\t]+&&[ \\t]+gh[ \\t]+pr[ \\t]+merge[ \\t]+(?<pr>[1-9][0-9]*)[ \\t]+--match-head-commit[ \\t]+(?<sha>[0-9a-f]{40})[ \\t]+--(?<method>merge|squash|rebase)[" + Ws + "]*\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompoundMergeRegex();

    [GeneratedRegex("[" + Ws + "]+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRunRegex();

    public string DecisionLogPath(string dir, string runId) => NodePath.Join(dir, "pipeline", runId, "decisions.jsonl");

    public JsonObject OpenRunAnchor(string dir, JsonValue? team, JsonArray? exchanges = null) =>
        RunAnchorOf(OpenExchanges(dir, team, exchanges ?? AnchorExchangesAllTeams(dir)));

    /// <summary>The one open run anchor among the open exchanges of a team, or the error saying why there is none.</summary>
    private static JsonObject RunAnchorOf(JsonArray open)
    {
        var anchors = open
            .Where(e => Member(e, "slug") is { IsText: true } s && s.AsString() == HierarchyModule.PipelineAnchorSlug)
            .ToList();
        if (anchors.Count == 1)
        {
            return new JsonObject { ["id"] = Member(anchors[0], "id"), ["path"] = Member(Member(anchors[0], "request"), "path") };
        }

        var anchorSlug = HierarchyModule.PipelineAnchorSlug;
        return new JsonObject
        {
            ["error"] = anchors.Count > 0
                ? $"{anchors.Count} open {anchorSlug} exchanges for this team ({string.Join(", ", anchors.Select(a => JsConvert.ToJsString(Member(a, "id"))))}) — exactly one is required"
                : $"no open {anchorSlug} exchange for this team — no pipeline run is live",
        };
    }

    public string? LastAnchorCloseTime(string dir, string? team, string? beforeIso)
    {
        DateTimeOffset? newest = null;
        DateTimeOffset? limit = beforeIso is not null && TryParseInstant(beforeIso, out var parsedLimit) ? parsedLimit : null;
        foreach (var e in AnchorExchangesAllTeams(dir))
        {
            if (!(Member(e, "slug") is { IsText: true } slug && slug.AsString() == HierarchyModule.PipelineAnchorSlug) || Member(e, "open").IsTruthy
                || Member(Member(e, "response"), "path") is not { IsText: true } responsePath)
            {
                continue;
            }

            // only this team's runs bound it: another team's run closing in the same pool says nothing about this one
            if (team is not null && Member(Member(e, "request"), "path") is { IsText: true } requestPath
                && _core.ReadMsgFile(requestPath.AsString()).Member("fm")?.Member("team") is { IsText: true } closedTeam && closedTeam.AsString() != team)
            {
                continue;
            }

            // a close time that cannot be read puts the boundary at the end of time, so no earlier answer is counted
            var at = _core.ReadMsgFile(responsePath.AsString()).Member("fm")?.Member("created") is { IsText: true } created && TryParseInstant(created.AsString(), out var parsed)
                ? parsed
                : DateTimeOffset.MaxValue;
            // a run that closed after this anchor was made is a later run's business
            if (limit is { } cap && at > cap)
            {
                continue;
            }

            if (newest is null || at > newest)
            {
                newest = at;
            }
        }

        return newest?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool KnownRun(string dir, JsonValue? runId)
    {
        if (runId is not { IsText: true } r || !IdRegex().IsMatch(r.AsString()))
        {
            return false;
        }

        return HasDecisionLog(dir, r.AsString())
            || AnchorExchangesAllTeams(dir).Any(e => Member(e, "id") is { IsText: true } id && id.AsString() == r.AsString()
                && Member(e, "slug") is { IsText: true } slug && slug.AsString() == HierarchyModule.PipelineAnchorSlug);
    }

    public JsonObject ReadDecisions(string path)
    {
        var decisions = new JsonArray();
        double skipped = 0;
        IEnumerable<string>? lines = DecisionRows(path);
        if (lines is null)
        {
            if (!Exists(path))
            {
                return new JsonObject { ["decisions"] = decisions, ["skipped"] = 0 };
            }

            lines = _files.ReadText(path).Split('\n');
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (JsText.Trim(line).Length == 0)
            {
                continue;
            }

            JsonValue value;
            try
            {
                value = JsonText.Parse(line);
            }
            catch (JsonParseException)
            {
                skipped++;
                continue;
            }

            if (!value.IsMap)
            {
                skipped++;
                continue;
            }

            if (value.Member("op_id") is { IsTruthy: true } id && JsOps.SetKey(id) is { } key && !seen.Add(key))
            {
                continue;
            }

            decisions.Add(value);
        }

        return new JsonObject { ["decisions"] = decisions, ["skipped"] = skipped };
    }

    private static bool IsKind(JsonValue d, string kind)
    {
        if (d.IsNull)
        {
            throw new InvalidOperationException("Cannot read properties of null (reading 'kind')");
        }

        return Member(d, "kind") is { IsText: true } k && k.AsString() == kind;
    }

    public JsonObject DecisionSummary(JsonArray decisions, JsonValue? skipped, JsonValue? asksAnswered = null) => SummarizeDecisions(decisions, skipped, asksAnswered);

    // static: the status compute calls it, and it may not reach an instance (b459 C1, guard G6)
    internal static JsonObject SummarizeDecisions(JsonArray decisions, JsonValue? skipped, JsonValue? asksAnswered = null)
    {
        var byItem = new JsonObject();
        double decided = 0;
        double flagged = 0;
        double parked = 0;
        double byRule = 0;
        var ruleKeys = new SortedSet<string>(StringComparer.Ordinal);
        var seenDecided = new HashSet<string>(StringComparer.Ordinal);
        var seenParked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in decisions)
        {
            var isDecided = IsKind(d, "decided");
            var isParked = IsKind(d, "parked");
            if (!isDecided && !isParked)
            {
                continue;
            }

            // a decision written twice is one decision: the first row of an id counts, later rows of it do not; a row with no usable id (not a non-empty string) names nothing to repeat
            if (Member(d, "id") is { IsText: true } rowId && rowId.AsString().Length > 0 && !(isDecided ? seenDecided : seenParked).Add(rowId.AsString()))
            {
                continue;
            }

            var itemKey = JsConvert.ToJsString(d.Member("item"));
            if (!(byItem.Get(itemKey) is { IsMap: true } existing))
            {
                existing = new JsonObject { ["decided"] = 0, ["parked"] = 0 };
                byItem[itemKey] = existing;
            }

            var counts = existing.AsObject();
            if (isDecided && IsRuleDecider(d.Member("decider")))
            {
                // answered by the user's standing rule: theirs, not the team's
                byRule++;
                _ = ruleKeys.Add(JsConvert.ToJsString(d.Member("decider")!.Value.Member("rule")));
            }
            else if (isDecided)
            {
                decided++;
                counts["decided"] = counts["decided"].AsNumber() + 1;
                if (Member(d, "review") is { IsBool: true } rv && rv.AsBool())
                {
                    flagged++;
                }
            }
            else
            {
                parked++;
                counts["parked"] = counts["parked"].AsNumber() + 1;
            }
        }

        // an answer closes the parked line it names; the answered and waiting keys exist only once an answer does
        var parkedIds = decisions.Where(d => IsKind(d, "parked")).Select(d => Member(d, "id") is { IsText: true } id && id.AsString().Length > 0 ? id.AsString() : null)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var answered = decisions.Where(d => IsKind(d, "answered")).Select(d => Member(d, "answers") is { IsText: true } a && a.AsString().Length > 0 ? a.AsString() : null)
            .OfType<string>().Where(parkedIds.Contains).Distinct(StringComparer.Ordinal).Count();
        var summary = new JsonObject
        {
            ["decided"] = decided,
            ["flagged"] = flagged,
            ["parked"] = parked,
        };
        if (answered > 0)
        {
            summary["answered"] = (double)answered;
            summary["waiting"] = parked - answered;
        }

        // "Answered by your rules": present only once a rule has answered one, so a log without one reads as before
        if (byRule > 0)
        {
            var keys = new JsonArray();
            foreach (var key in ruleKeys)
            {
                keys.Add(key);
            }

            summary["by_rule"] = byRule;
            summary["rules"] = keys;
        }

        var operational = decisions.Where(d => IsKind(d, "operational")).Select(d => Member(d, "id") is { IsText: true } id ? id.AsString() : null)
            .Distinct(StringComparer.Ordinal).Count();
        if (operational > 0)
        {
            summary["operational"] = (double)operational;
        }

        if (asksAnswered is { } asks)
        {
            summary["asks_answered"] = asks;
        }

        summary["by_item"] = byItem;

        // An undefined `skipped` has no key in the document, as with JSON.stringify.
        if (skipped is { } skippedCount)
        {
            summary["skipped"] = skippedCount;
        }

        summary["line"] = (answered > 0
            ? $"decisions: {JsConvert.NumberToString(decided)} decided ({JsConvert.NumberToString(flagged)} flagged), {JsConvert.NumberToString(parked - answered)} waiting for you, {answered} answered"
            : $"decisions: {JsConvert.NumberToString(decided)} decided ({JsConvert.NumberToString(flagged)} flagged), {JsConvert.NumberToString(parked)} waiting for you")
            + (operational > 0 ? $", {operational} operational" : string.Empty);
        return summary;
    }

    public string QuestionKey(JsonValue? item, JsonValue? question)
    {
        if (question is not { IsText: true } q)
        {
            throw new InvalidOperationException("question.trim is not a function");
        }

        var normalized = SpaceRunRegex().Replace(JsLower.Lower(JsText.Trim(q.AsString())), " ");
        var hash = SHA256.HashData(Utf8Text.Encode($"{JsConvert.ToJsString(item)}\n{normalized}"));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private static bool NonEmpty(JsonValue? v) => v is { IsText: true } t && JsText.Trim(t.AsString()).Length != 0;

    private static bool LetterWithin(JsonValue? v, int optionCount) =>
        v is { IsText: true } t && Array.IndexOf(Letters, t.AsString()) is var i and >= 0 && i < optionCount;

    private static bool IsOther(JsonValue? v) => v is { IsText: true } t && OtherChoiceRegex().IsMatch(t.AsString());

    private static JsonObject Invalid(string detail) => new() { ["reason"] = "invalid", ["detail"] = detail };

    /// <summary>
    /// Why a line naming rule <paramref name="key"/> may not be logged, or null: the rule must be a decision rule of the log's hierarchy that the user accepted and that is
    /// not switched off, and it never decides a dangerous question. With no rule lookup wired, or a lookup that fails, no line may name a rule.
    /// </summary>
    private JsonObject? RuleRefusal(string path, string key, bool dangerous)
    {
        if (dangerous)
        {
            return new JsonObject { ["reason"] = "rule-not-for-dangerous", ["detail"] = $"a standing rule never decides a dangerous question; park it for the user (rule {key})" };
        }

        // the log is <hierarchy dir>/pipeline/<run>/decisions.jsonl
        var dir = NodePath.Dirname(NodePath.Dirname(NodePath.Dirname(path)));
        var row = _decisionRule?.Invoke(dir, key);
        if (row is not { IsDecision: true, Off: false })
        {
            return new JsonObject { ["reason"] = "unknown-rule", ["detail"] = $"no decision rule {key} is in force here; ah rules list shows them" };
        }

        return row.IsProposed
            ? new JsonObject { ["reason"] = "rule-not-accepted", ["detail"] = $"rule {key} is proposed and decides nothing until the user accepts it (ah rules accept {key})" }
            : null;
    }

    /// <summary>A decided line's decider when one of the user's accepted standing rules answered it: exactly <c>{role: user-rule, rule: &lt;key&gt;}</c>.</summary>
    private static bool IsRuleDecider(JsonValue? decider) =>
        decider is { IsMap: true } d && d.AsObject().Keys.All(k => k is "role" or "rule")
        && d.Member("role") is { IsText: true } role && role.AsString() == RulesOfEngagement.RuleDecider && NonEmpty(d.Member("rule"));

    /// <summary>Why <paramref name="input"/> cannot be logged beside <paramref name="existing"/>, as <c>{reason, detail}</c>, or null.</summary>
    private JsonObject? Refusal(string path, JsonValue? input, JsonArray existing)
    {
        if (input is not { IsMap: true } inputValue)
        {
            return Invalid("the input must be one JSON object");
        }

        var obj = inputValue.AsObject();
        if (obj.Get("kind") is { IsText: true } kk && kk.AsString() == "merge")
        {
            return Invalid("kind merge is written only by the merge record hook");
        }

        if (obj.ContainsKey("class"))
        {
            return OperationalRefusal(obj);
        }

        var owned = obj.Keys.Where(k => Array.IndexOf(WriterFields, k) >= 0).ToList();
        if (owned.Count > 0)
        {
            return Invalid($"writer-owned field(s) supplied: {string.Join(", ", owned)} — the writer sets them");
        }

        if (obj.Get("kind") is { IsText: true } answeredKind && answeredKind.AsString() == "answered")
        {
            return AnsweredRefusal(obj, existing);
        }

        var unknown = obj.Keys.Where(k => Array.IndexOf(InputFields, k) < 0 && k != CallerField && k != MultiField).ToList();
        if (unknown.Count > 0)
        {
            return Invalid($"unknown field(s): {string.Join(", ", unknown)}");
        }

        if (obj.ContainsKey(CallerField) && !NonEmpty(obj[CallerField]))
        {
            return Invalid("caller must be a non-empty string");
        }

        if (obj.ContainsKey(MultiField) && !obj[MultiField].IsBool)
        {
            return Invalid("multi must be a boolean");
        }

        if (IsMulti(obj) && !(obj.Get("kind") is { IsText: true } mk && mk.AsString() == "parked"))
        {
            return Invalid("multi only on parked");
        }

        var missing = InputFields.Where(k => !obj.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            return Invalid($"missing field(s): {string.Join(", ", missing)}");
        }

        var kind = obj["kind"];
        var options = obj["options"];
        var decider = obj["decider"];
        if (!(kind.IsText && kind.AsString() is "decided" or "parked"))
        {
            return Invalid("kind must be decided or parked");
        }

        foreach (var k in new[] { "item", "source", "question" })
        {
            if (!NonEmpty(obj[k]))
            {
                return Invalid($"{k} must be a non-empty string");
            }
        }

        if (!(options.IsArray && options.AsArray().Count is >= 2 and <= 4 && options.AsArray().All(o => NonEmpty(o))))
        {
            return Invalid("options must be 2 to 4 non-empty strings");
        }

        var optionCount = options.AsArray().Count;
        if (!obj["default"].IsNull && !LetterWithin(obj["default"], optionCount))
        {
            return Invalid("default must be a letter within options, or null");
        }

        if (!obj["choice"].IsNull && !obj["choice"].IsText)
        {
            return Invalid("choice must be a string or null");
        }

        var deciderOk = decider.IsMap
            && decider.AsObject().Keys.All(k => Array.IndexOf(DeciderKeys, k) >= 0)
            && decider.Member("role") is { IsText: true } role && DeciderRoleRegex().IsMatch(role.AsString())
            && (decider.Member("name") is { } dn && (dn.IsNull || dn.IsText))
            && (decider.Member("model") is { } dm && (dm.IsNull || dm.IsText));
        var byRule = kind.AsString() == "decided" && IsRuleDecider(decider);
        if (!(deciderOk || byRule || (decider.IsNull && kind.AsString() == "parked")))
        {
            return Invalid("decider must be { role, name, model }, role matching ^[a-z0-9-]+$ (null only on a parked line)");
        }

        // every append path comes through here, so this is where a line is held to speaking for the user only by a rule they accepted
        if (byRule && RuleRefusal(path, JsConvert.ToJsString(decider.Member("rule")), obj.Get("dangerous") is { IsBool: true } dz && dz.AsBool()) is { } ruleRefusal)
        {
            return ruleRefusal;
        }

        if (!byRule && decider.Member("role") is { IsText: true } ruleRole && ruleRole.AsString() == RulesOfEngagement.RuleDecider)
        {
            return Invalid($"decider {RulesOfEngagement.RuleDecider} is only {{ role, rule }} on a decided line, written with --rule");
        }

        foreach (var k in new[] { "rationale", "revert" })
        {
            if (!obj[k].IsText)
            {
                return Invalid($"{k} must be a string");
            }
        }

        foreach (var k in new[] { "review", "dangerous" })
        {
            if (!obj[k].IsBool)
            {
                return Invalid($"{k} must be a boolean");
            }
        }

        if (!obj["why_user"].IsNull && !(obj["why_user"].IsText && Array.IndexOf(WhyUser, obj["why_user"].AsString()) >= 0))
        {
            return Invalid($"why_user must be {string.Join(", ", WhyUser)} or null");
        }

        if (!obj["exchange"].IsNull && !NonEmpty(obj["exchange"]))
        {
            return Invalid("exchange must be a request id or null");
        }

        var dangerous = obj["dangerous"].AsBool();
        if (kind.AsString() == "decided")
        {
            if (dangerous)
            {
                return Invalid("a decided line can't be dangerous — a dangerous question goes to the user");
            }

            if (decider.Member("role") is { IsText: true } dr && dr.AsString() == "user")
            {
                return Invalid("a decided line's decider can't be the user — the user's answers are not logged as decided");
            }

            if (!obj["why_user"].IsNull)
            {
                return Invalid("a decided line has no why_user");
            }

            if (!LetterWithin(obj["choice"], optionCount) && !IsOther(obj["choice"]))
            {
                return Invalid("choice must be a letter within options, or other: <text>");
            }

            // a standing rule's own answer is the user's word, not the team's invention, so it needs no review
            if (IsOther(obj["choice"]) && !byRule && !(obj["review"].AsBool()))
            {
                return Invalid("an other: answer always means review: true");
            }
        }
        else
        {
            if (!obj["choice"].IsNull)
            {
                return Invalid("a parked line has no choice");
            }

            if (obj["why_user"].IsNull)
            {
                return Invalid("a parked line needs why_user");
            }
        }

        if (dangerous && !(obj["why_user"].IsText && obj["why_user"].AsString() == "dangerous"))
        {
            return Invalid("dangerous: true needs why_user dangerous");
        }

        if (kind.AsString() == "decided")
        {
            var qkey = QuestionKey(obj["item"], obj["question"]);
            if (existing.Any(d => IsKind(d, "parked") && Member(d, "qkey") is { IsText: true } q && q.AsString() == qkey))
            {
                return new JsonObject { ["reason"] = "parked-before", ["detail"] = $"question {qkey} was parked earlier in this run — never re-ask" };
            }

            var earlier = existing.FirstOrDefault(d => IsKind(d, "decided") && Member(d, "qkey") is { IsText: true } q && q.AsString() == qkey);
            if (!earlier.IsNull)
            {
                return new JsonObject
                {
                    ["reason"] = "decided-before",
                    ["detail"] = $"question {qkey} was already decided in this run as {JsConvert.ToJsString(earlier.Member("id"))} — route that decision",
                };
            }

            var decided = existing.Where(d => IsKind(d, "decided")).ToList();
            var forItem = decided.Count(d => d.Member("item") is { } it && StrictEquals(it, obj["item"]));
            if (forItem >= HierarchyModule.DecisionItemCap)
            {
                return new JsonObject
                {
                    ["reason"] = "cap",
                    ["detail"] = $"item {obj["item"].AsString()} already has {forItem} decided (cap {HierarchyModule.DecisionItemCap})",
                };
            }

            if (decided.Count >= HierarchyModule.DecisionRunCap)
            {
                return new JsonObject
                {
                    ["reason"] = "cap",
                    ["detail"] = $"the run already has {decided.Count} decided (cap {HierarchyModule.DecisionRunCap})",
                };
            }
        }

        return null;
    }

    private static readonly string[] OperationalCalls = ["O1", "O2", "O3", "O4", "O5", "O6", "O7", "O8", "O9", "O10"];

    private static readonly string[] OperationalFields =
        ["class", "o", "item", "source", "question", "decision", "rationale", "revert", "decider", "why_user", "dangerous", CallerField];

    /// <summary>
    /// Why an operational line (<c>class: operational</c>) cannot be logged, as <c>{reason, detail}</c>, or null. It records a call the
    /// Orchestrator made itself, so it is the Orchestrator's own, names no reserved class, and is written outside the caps.
    /// </summary>
    private static JsonObject? OperationalRefusal(JsonObject obj)
    {
        JsonValue G(string k) => obj.Get(k) ?? JsonValue.Null;
        if (!(obj["class"].IsText && obj["class"].AsString() == "operational"))
        {
            return Invalid("class must be operational when given");
        }

        var unknown = obj.Keys.Where(k => Array.IndexOf(OperationalFields, k) < 0).ToList();
        if (unknown.Count > 0)
        {
            return Invalid($"unknown field(s) on an operational line: {string.Join(", ", unknown)}");
        }

        if (!G("why_user").IsNull || (G("dangerous").IsBool && G("dangerous").AsBool()))
        {
            return Invalid("an operational line names no reserved class: why_user and dangerous are for the user's questions");
        }

        if (!(G("o").IsText && Array.IndexOf(OperationalCalls, G("o").AsString()) >= 0))
        {
            return Invalid("o must be one of O1 to O10");
        }

        foreach (var k in new[] { "question", "decision", "rationale", "revert" })
        {
            if (!NonEmpty(G(k)))
            {
                return Invalid($"{k} must be a non-empty string");
            }
        }

        foreach (var k in new[] { "item", "source", CallerField })
        {
            if (obj.ContainsKey(k) && !NonEmpty(G(k)))
            {
                return Invalid($"{k} must be a non-empty string when given");
            }
        }

        return IsOperationalDecider(G("decider"))
            ? null
            : Invalid("decider must be { role: orchestrator, self: true } or, for a lane's call the Orchestrator relays, { role: sub-orchestrator, name: <lane>, relayed_by: orchestrator }");
    }

    /// <summary>
    /// The Orchestrator's own call, or a sub-orchestrator's call that the Orchestrator relays and logs once on its behalf. The writer
    /// accepts the relayed form from the same caller it accepts the own form from, so a lane never writes to the log itself.
    /// </summary>
    private static bool IsOperationalDecider(JsonValue decider)
    {
        if (!decider.IsMap || decider.Member("role") is not { IsText: true } role)
        {
            return false;
        }

        var keys = decider.AsObject().Keys.ToList();
        return role.AsString() switch
        {
            "orchestrator" or "deputy-chief" => keys.All(k => k is "role" or "self") && decider.Member("self") is { IsBool: true } self && self.AsBool(),
            "sub-orchestrator" => keys.All(k => k is "role" or "name" or "relayed_by")
                && NonEmpty(decider.Member("name"))
                && decider.Member("relayed_by") is { IsText: true } relayedBy && relayedBy.AsString() == "orchestrator",
            _ => false,
        };
    }

    private JsonObject AppendOperational(IStoreWriteContext writer, string path, JsonValue? runId, JsonObject obj, JsonArray decisions, string? operationId)
    {
        var id = $"o{decisions.Count(d => IsKind(d, "operational")) + 1}";
        var line = new JsonObject { ["id"] = id, ["time"] = IsoNowString(), ["run"] = runId ?? JsonValue.Null, ["kind"] = "operational" };
        foreach (var field in OperationalFields.Where(f => f is not "why_user" and not "dangerous" && obj.ContainsKey(f)))
        {
            line[field] = obj[field];
        }

        AppendLine(writer, path, line, operationId);
        return new JsonObject
        {
            ["id"] = id,
            ["path"] = path,
            ["counts"] = new JsonObject { ["run"] = new JsonObject { ["operational"] = (double)(decisions.Count(d => IsKind(d, "operational")) + 1) } },
        };
    }

    private static readonly string[] AnsweredFields = ["kind", "answers", "choice", "answered_by", "recorded_by", "caller"];

    /// <summary>The field a <c>surrogate</c> answer adds, and no other answer may carry.</summary>
    private const string SurrogateClassField = "surrogate_class";

    private static bool IsSurrogateClass(JsonValue v) => v.IsText && v.AsString() is { Length: 2 } s && s[0] == 'D' && s[1] is >= '1' and <= '5';

    /// <summary>The parked line an answer names, or a null value when the id names no parked line.</summary>
    private static JsonValue ParkedLine(JsonArray existing, string id) =>
        existing.FirstOrDefault(d => IsKind(d, "parked") && Member(d, "id") is { IsText: true } i && i.AsString() == id);

    /// <summary>Why an <c>answered</c> line cannot be logged beside <paramref name="existing"/>, as <c>{reason, detail}</c>, or null. The user's answer to a parked decision; a <c>decided</c> line is never the user's.</summary>
    private static JsonObject? AnsweredRefusal(JsonObject obj, JsonArray existing)
    {
        var unknown = obj.Keys.Where(k => Array.IndexOf(AnsweredFields, k) < 0 && k != SurrogateClassField && k != ChoicesField).ToList();
        if (unknown.Count > 0)
        {
            return Invalid($"unknown field(s): {string.Join(", ", unknown)}");
        }

        // a multi answer names its entries in choices and has no choice: the writer joins them into choice
        var hasChoices = obj.ContainsKey(ChoicesField);
        if (hasChoices && obj.ContainsKey("choice"))
        {
            return Invalid("give choice or choices, not both");
        }

        var missing = AnsweredFields.Where(k => !obj.ContainsKey(k) && !(hasChoices && k == "choice")).ToList();
        if (missing.Count > 0)
        {
            return Invalid($"missing field(s): {string.Join(", ", missing)}");
        }

        foreach (var k in hasChoices ? new[] { "answers", "recorded_by", "caller" } : new[] { "answers", "choice", "recorded_by", "caller" })
        {
            if (!NonEmpty(obj[k]))
            {
                return Invalid($"{k} must be a non-empty string");
            }
        }

        var answeredBy = obj["answered_by"].IsText ? obj["answered_by"].AsString() : null;
        if (answeredBy is not ("user" or "surrogate"))
        {
            return Invalid("answered_by must be user or surrogate");
        }

        if (answeredBy == "surrogate" && !(obj.ContainsKey(SurrogateClassField) && IsSurrogateClass(obj[SurrogateClassField])))
        {
            return Invalid("a surrogate answer needs surrogate_class D1 to D5");
        }

        if (answeredBy == "user" && obj.ContainsKey(SurrogateClassField))
        {
            return Invalid("a user answer carries no surrogate_class");
        }

        var id = obj["answers"].AsString();
        var parked = ParkedLine(existing, id);
        if (parked.IsNull)
        {
            var named = existing.FirstOrDefault(d => Member(d, "id") is { IsText: true } i && i.AsString() == id);
            if (!named.IsNull && IsKind(named, "decided"))
            {
                return new JsonObject { ["reason"] = "already-closed", ["detail"] = $"decision {id} is already closed by {id} (decided)" };
            }

            return new JsonObject
            {
                ["reason"] = "unknown-id",
                ["detail"] = named.IsNull ? $"no parked decision {id} in this run" : $"{id} is not a parked decision",
            };
        }

        var earlier = existing.FirstOrDefault(d => IsKind(d, "answered") && Member(d, "answers") is { IsText: true } a && a.AsString() == id);
        if (!earlier.IsNull)
        {
            return new JsonObject
            {
                ["reason"] = "already-closed",
                ["detail"] = $"decision {id} is already closed by {JsConvert.ToJsString(earlier.Member("id"))} (answered)",
            };
        }

        var optionCount = parked.Member("options") is { IsArray: true } options ? options.AsArray().Count : 0;
        if (!hasChoices)
        {
            return !LetterWithin(obj["choice"], optionCount) && !IsOther(obj["choice"])
                ? Invalid("choice must be a letter within options, or other: <text>")
                : null;
        }

        if (!IsMultiLine(parked))
        {
            return new JsonObject { ["reason"] = "not-multi", ["detail"] = $"decision {id} takes one choice, not several" };
        }

        if (!(obj[ChoicesField] is { IsArray: true } given && given.AsArray().Count > 0))
        {
            return Invalid("choices must be one or more entries");
        }

        var entries = obj[ChoicesField].AsArray();
        if (entries.Any(e => !LetterWithin(e, optionCount) && !IsOther(e)))
        {
            return Invalid("each choice must be a letter within options, or other: <text>");
        }

        var duplicate = entries.Select(static e => e.AsString()).GroupBy(static e => e, StringComparer.Ordinal).FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null)
        {
            return new JsonObject { ["reason"] = "duplicate-choice", ["detail"] = $"choice {duplicate.Key} is given more than once" };
        }

        return entries.Count(static e => IsOther(e)) > 1 ? Invalid("at most one other: <text> choice") : null;
    }

    /// <summary>The entries of a multi answer in option order, Other last.</summary>
    private static List<string> OrderedChoices(JsonArray entries) =>
        [.. entries.Select(static e => e.AsString()).OrderBy(static e => e.StartsWith("other:", StringComparison.Ordinal) ? 1 : 0).ThenBy(static e => e, StringComparer.Ordinal)];

    /// <summary>One append of <paramref name="record"/> as a line, led by a newline when the file does not end in one (a torn line after a crash).</summary>
    private void AppendLine(IStoreWriteContext writer, string path, JsonObject record, string? operationId, UnixFileMode? createMode = null)
    {
        if (_decisionTx.Value is { } tx && DecisionLogKey(path) is var (hierDir, run))
        {
            var dbRow = record;
            if (operationId is { Length: > 0 })
            {
                dbRow = JsOps.Spread(record);
                dbRow["op_id"] = operationId;
            }

            tx.Execute(
                "INSERT INTO decisions(hier_dir, run, seq, body) VALUES (?1, ?2, (SELECT COALESCE(MAX(seq), 0) + 1 FROM decisions WHERE hier_dir = ?1 AND run = ?2), ?3)",
                hierDir, run, JsonText.Stringify(dbRow, 0));
            return;
        }

        // per-dir-files-to-db S10: an asks log's row, in the asks write's transaction
        if (_decisionTx.Value is { } askTx && NodePath.Basename(path) is "asks.jsonl" or AsksGateFile or ParkedQuestions.FileName)
        {
            var askRow = record;
            if (operationId is { Length: > 0 })
            {
                askRow = JsOps.Spread(record);
                askRow["op_id"] = operationId;
            }

            askTx.Execute(
                "INSERT INTO asks(hier_dir, log, seq, body) VALUES (?1, ?2, (SELECT COALESCE(MAX(seq), 0) + 1 FROM asks WHERE hier_dir = ?1 AND log = ?2), ?3)",
                // keyed by the dir's real path, as the reader and the take key it (StateDbAsks.HierDirKey), so every spelling of one H meets
                PathGuard.RealPath(NodePath.Dirname(path), strict: false)!, NodePath.Basename(path), JsonText.Stringify(askRow, 0));
            return;
        }

        writer.Mkdir(NodePath.Dirname(path));
        var torn = false;
        if (Exists(path))
        {
            // the last byte alone says whether a crash tore the last line; a log can be megabytes
            var last = _files.ReadTail(path, 1).Bytes;
            torn = last.Length > 0 && last[0] != (byte)'\n';
        }

        var row = record;
        if (operationId is { Length: > 0 })
        {
            row = JsOps.Spread(record);
            row["op_id"] = operationId;
        }

        var bytes = Utf8Text.Encode((torn ? "\n" : string.Empty) + JsonText.Stringify(row, 0) + "\n");
        if (createMode is { } mode && !Exists(path) && writer.CreateExclusive(path, bytes, mode))
        {
            return;
        }

        writer.Append(path, bytes);
    }

    public JsonObject AppendDecision(IStoreWriteContext writer, string path, JsonValue? runId, JsonValue? input, string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var read = ReadDecisions(path);
        var decisions = read["decisions"].AsArray();
        var why = Refusal(path, input, decisions);
        if (why is not null)
        {
            return new JsonObject { ["refused"] = why };
        }

        var obj = input!.Value.AsObject();
        if (obj.ContainsKey("class"))
        {
            return AppendOperational(writer, path, runId, obj, decisions, operationId);
        }

        var k = decisions.Count(d => IsKind(d, "decided") || IsKind(d, "parked") || IsKind(d, "answered")) + 1;
        if (obj["kind"].AsString() == "answered")
        {
            return AppendAnswer(writer, path, runId, obj, decisions, $"d{k}", operationId);
        }

        var choice = obj["choice"];
        JsonValue decision = JsonValue.Null;
        if (obj["kind"].AsString() == "decided")
        {
            decision = IsOther(choice) ? choice : obj["options"].AsArray()[Array.IndexOf(Letters, choice.AsString())];
        }

        var line = new JsonObject { ["id"] = $"d{k}", ["time"] = IsoNowString(), ["run"] = runId ?? JsonValue.Null };
        foreach (var field in InputFields)
        {
            line[field] = obj[field];
            if (field == "question")
            {
                line["qkey"] = QuestionKey(obj["item"], obj["question"]);
            }

            if (field == "choice")
            {
                line["decision"] = decision;
            }
        }

        if (IsMulti(obj))
        {
            line[MultiField] = true;
        }

        if (obj.ContainsKey(CallerField))
        {
            line[CallerField] = obj[CallerField];
        }

        AppendLine(writer, path, line, operationId);
        var all = new JsonArray();
        foreach (var d in decisions)
        {
            all.Add(d);
        }

        all.Add(line);
        var after = DecisionSummary(all, 0);
        var item = after["by_item"].AsObject()[JsConvert.ToJsString(obj["item"])].AsObject();
        return new JsonObject
        {
            ["id"] = line["id"],
            ["path"] = path,
            ["counts"] = new JsonObject
            {
                ["run"] = new JsonObject { ["decided"] = after["decided"], ["parked"] = after["parked"] },
                ["item"] = new JsonObject { ["decided"] = item["decided"], ["parked"] = item["parked"] },
            },
        };
    }

    /// <summary>
    /// Logs the user's answer to a parked decision as an <c>answered</c> line. It copies the parked line's item, question, options and
    /// question key so the answer reads on its own, and says who relayed it (<c>recorded_by</c>), since nothing here proves what the
    /// user said.
    /// </summary>
    private JsonObject AppendAnswer(IStoreWriteContext writer, string path, JsonValue? runId, JsonObject obj, JsonArray decisions, string id, string? operationId)
    {
        var parked = ParkedLine(decisions, obj["answers"].AsString());
        string TextOf(string c) => IsOther(c) ? c : parked.Member("options")!.Value.AsArray()[Array.IndexOf(Letters, c)].AsString();

        // a multi question's answer always carries choices, even when it was given as one choice
        List<string>? entries = obj.ContainsKey(ChoicesField) ? OrderedChoices(obj[ChoicesField].AsArray())
            : IsMultiLine(parked) ? [obj["choice"].AsString()]
            : null;
        JsonValue choice = entries is null ? obj["choice"] : string.Join(",", entries);
        var line = new JsonObject
        {
            ["id"] = id,
            ["time"] = IsoNowString(),
            ["run"] = runId ?? JsonValue.Null,
            ["kind"] = "answered",
            ["answers"] = obj["answers"],
            ["item"] = parked.Member("item") ?? JsonValue.Null,
            ["question"] = parked.Member("question") ?? JsonValue.Null,
            ["qkey"] = parked.Member("qkey") ?? JsonValue.Null,
            ["options"] = parked.Member("options") ?? JsonValue.Null,
            ["choice"] = choice,
            ["decision"] = entries is not null ? string.Join("; ", entries.Select(TextOf))
                : IsOther(choice) ? choice : parked.Member("options")!.Value.AsArray()[Array.IndexOf(Letters, choice.AsString())],
        };
        if (entries is not null)
        {
            var array = new JsonArray();
            foreach (var e in entries)
            {
                array.Add(e);
            }

            line[ChoicesField] = array;
        }

        line["answered_by"] = obj["answered_by"];
        line["recorded_by"] = obj["recorded_by"];
        line["caller"] = obj["caller"];
        if (obj.ContainsKey(SurrogateClassField))
        {
            line[SurrogateClassField] = obj[SurrogateClassField];
        }

        AppendLine(writer, path, line, operationId);
        var all = new JsonArray();
        foreach (var d in decisions)
        {
            all.Add(d);
        }

        all.Add(line);
        var after = DecisionSummary(all, 0);
        var item = after["by_item"].AsObject()[JsConvert.ToJsString(line["item"])].AsObject();
        return new JsonObject
        {
            ["id"] = id,
            ["path"] = path,
            ["counts"] = new JsonObject
            {
                ["run"] = new JsonObject { ["decided"] = after["decided"], ["parked"] = after["parked"], ["answered"] = after["answered"], ["waiting"] = after["waiting"] },
                ["item"] = new JsonObject { ["decided"] = item["decided"], ["parked"] = item["parked"] },
            },
        };
    }

    /// <summary>The log's Other form is <c>other: &lt;text&gt;</c>; a front end may send <c>other:&lt;text&gt;</c> as well.</summary>
    internal static string NormaliseChoice(string choice) =>
        choice.StartsWith("other:", StringComparison.Ordinal) && choice.Length > 6 && choice[6] != ' ' ? "other: " + choice[6..] : choice;

    private static JsonObject Refused(string reason, string detail) =>
        new() { ["refused"] = new JsonObject { ["reason"] = reason, ["detail"] = detail } };

    public JsonObject? AnswerParked(
        string dir,
        JsonValue? team,
        string? expectedRun,
        string id,
        IReadOnlyList<string> choices,
        bool several,
        Func<string, string, JsonObject?> prove,
        Func<string, string, JsonObject, JsonObject> append,
        Func<string, string, string, string?>? notify,
        IHierarchyProgress? progress,
        out string? eventError)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(prove);
        ArgumentNullException.ThrowIfNull(append);
        eventError = null;
        var anchor = OpenRunAnchor(dir, team ?? JsonValue.Null);
        if (anchor.Get("error") is { IsTruthy: true } anchorError)
        {
            return Refused("no-run", JsConvert.ToJsString(anchorError));
        }

        var runId = JsConvert.ToJsString(anchor["id"]);
        if (expectedRun is not null && expectedRun != runId)
        {
            return Refused("stale-run", $"open run is {runId}");
        }

        var logPath = DecisionLogPath(dir, runId);
        if (prove(runId, logPath) is not { } stamp)
        {
            return null;
        }

        var input = new JsonObject { ["kind"] = "answered", ["answers"] = id };
        if (several)
        {
            var array = new JsonArray();
            foreach (var c in choices)
            {
                array.Add(NormaliseChoice(c));
            }

            input[ChoicesField] = array;
        }
        else
        {
            input["choice"] = choices.Count == 1 ? NormaliseChoice(choices[0]) : JsonValue.Null;
        }

        foreach (var k in new[] { "answered_by", "recorded_by", "caller", SurrogateClassField })
        {
            if (stamp.Get(k) is { } v)
            {
                input[k] = v;
            }
        }

        var res = append(logPath, runId, input);
        if (res.Get("refused") is { IsMap: true })
        {
            return res;
        }

        // the append's result names the new line; its decision text is read back from the line itself
        var newId = res.Get("id") ?? JsonValue.Null;
        var written = ReadDecisions(logPath)["decisions"].AsArray()
            .FirstOrDefault(d => IsKind(d, "answered") && Member(d, "id") is { IsText: true } i && newId.IsText && i.AsString() == newId.AsString());
        res["answers"] = id;
        res["decision"] = written.IsNull ? JsonValue.Null : Member(written, "decision");

        // with no team named, the anchor found is one with no team tag (OpenExchanges keeps only those), so there is no Orchestrator to name
        var teamName = team is { IsText: true } t ? t.AsString() : null;
        var moveError = progress is null ? null : MoveNamedItems(progress, dir, teamName, logPath, id, JsConvert.ToJsString(res.Get("decision") ?? JsonValue.Null), JsConvert.ToJsString(stamp.Get("recorded_by") ?? JsonValue.Null));

        // the user answered without the Orchestrator: wake it. The line is the record, so a failed event fails nothing.
        var byUser = JsConvert.ToJsString(stamp.Get("answered_by") ?? JsonValue.Null) == "user"
            && !JsConvert.ToJsString(stamp.Get("recorded_by") ?? JsonValue.Null).StartsWith("orchestrator@", StringComparison.Ordinal);
        if (byUser && notify is not null && teamName is null)
        {
            eventError = "no team named, so no Orchestrator to tell";
        }
        else if (byUser && notify is not null)
        {
            try
            {
                eventError = notify(
                    HierarchyModule.OrchestratorAddress(teamName!),
                    "question-answered",
                    $"{id} answered: {JsConvert.ToJsString(res.Get("decision") ?? JsonValue.Null)}");
            }
#pragma warning disable CA1031 // the answer line is the record: no failure of the wake may fail an answer already written
            catch (Exception e)
#pragma warning restore CA1031
            {
                eventError = e.Message;
            }
        }

        eventError = moveError is null ? eventError : eventError is null ? moveError : moveError + "; " + eventError;
        return res;
    }

    public JsonObject? AnswerQuestion(
        IFileReader files,
        StateCalls.IHierarchyStateCalls calls,
        IHierarchyProgress progress,
        string pool,
        string dir,
        string? team,
        string id,
        Func<ParkedQuestion, JsonObject?> prove,
        Func<ParkedQuestion, (IReadOnlyList<string> Given, bool FreeText)> given,
        string? note,
        DateTimeOffset now,
        Func<string, string, string, string?> notify,
        Func<string?, JsonValue> readTeam,
        out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(prove);
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(notify);
        var noted = new List<string>();
        notes = noted;
        var path = ParkedQuestions.PathIn(dir);
        _ = ParkedQuestions.MatchRecorded(files, calls, dir);
        // per-dir-files-to-db §6c G5: one read; a store that cannot be read is its own failure, not an unknown question
        if (!files.ReadAskLog(path).TryLines(out var stored))
        {
            return Refused("store_unreadable", $"the asks store could not be read; {id} was not answered");
        }

        var question = ParkedQuestions.Fold(string.Join('\n', stored)).FirstOrDefault(q => q.Id == id && (team is null || q.Team == team));
        if (question is null)
        {
            return Refused("unknown_question", $"No parked question {id} for this team.");
        }

        if (!question.Open)
        {
            return Refused("already_answered", $"{id} was already answered \"{string.Join(", ", question.AnsweredLabels)}\" at {question.AnsweredAt}");
        }

        if (prove(question) is not { } stamp)
        {
            return null;
        }

        var writer = stamp.Get("writer")!.Value.AsString();
        var relayedPrompt = stamp.Get("relayed_prompt") is { IsText: true } prompt ? prompt.AsString() : null;
        var byUser = stamp.Get("by_user") is { IsBool: true } u && u.AsBool();
        var options = question.Labels;
        var (labels, freeText) = given(question);
        var chosen = new List<string>();
        foreach (var label in labels)
        {
            var match = freeText ? label : ParkedQuestions.MatchLabel(question, label);
            if (match is null)
            {
                return Refused("unmatched_label", $"\"{label}\" is not an option for {id}. Options: {string.Join(" | ", options)}.");
            }

            chosen.Add(match);
        }

        var at = Ah.Core.Time.Timestamps.ToIsoString(now);
        var questions = new JsonArray();
        questions.Add(question.Question);
        var answers = new JsonObject { [question.Text] = string.Join(", ", chosen) };
        var response = new JsonObject { ["answers"] = answers };
        if (note is not null)
        {
            response["note"] = note;
        }

        JsonValue session = question.SessionId is { } sessionId ? sessionId : JsonValue.Null;
        var evidence = relayedPrompt is null ? string.Empty : $";relayed-prompt={relayedPrompt}";
        var row = AskRecord.Row(at, writer, session, questions, response, question.Source is null ? $"ah:relay{evidence};ref={id}" : $"{question.Source};ref={id}{evidence}");
        calls.AppendAsk(dir, row, new StateCallOptions(OperationId: $"ans:{id}"));
        ParkedQuestions.Answer(calls, dir, id, at, chosen, writer, $"ans:{id}", freeText: freeText);
        // b517: as park does, so the answered question leaves the watch model's waiting list
        RefreshStatus(dir);
        _ = progress.AppendLedger(pool, question.Team, AskRecord.Ruling(questions, response, null));
        var text = $"{id} answered: {question.Header} = {string.Join(", ", chosen)}";
        string? eventError = null;
        if (byUser)
        {
            eventError = notify($"orchestrator@{question.Team}", "question-answered", text);
        }

        // b19: the member it was parked for gets the same event, re-checked as still on the question's team; a skip or a failed send is noted, never fatal
        if (question.AskedFor is { } gone && HierarchyModule.AskerRefusal(gone, question.Team, readTeam) is { } notMember)
        {
            noted.Add($"the question-answered event to the asker was not sent: {notMember}");
        }
        else if (question.AskedFor is { } asker && notify(asker, "question-answered", text) is { } failure)
        {
            noted.Add($"the question-answered event to {asker} was not sent: {failure}");
        }

        var labelsJson = new JsonArray();
        foreach (var c in chosen)
        {
            labelsJson.Add(c);
        }

        var result = new JsonObject { ["id"] = id, ["team"] = question.Team, ["header"] = question.Header, ["labels"] = labelsJson };
        if (relayedPrompt is not null)
        {
            result["relayed_prompt"] = relayedPrompt;
        }

        if (eventError is not null)
        {
            result["event_error"] = eventError;
        }

        return result;
    }

    /// <summary>
    /// Records the answer on every item of the team waiting on the user that the answered decision names, as each was hidden behind it (spec ah-ui-answer §4). Two logs cannot be one
    /// write: the decision line is the record, so a failed row is returned as an error and fails nothing. An item left waiting then shows again, and can be answered or dismissed.
    /// </summary>
    private string? MoveNamedItems(IHierarchyProgress progress, string dir, string? team, string logPath, string id, string answer, string by)
    {
        var parked = ReadDecisions(logPath)["decisions"].AsArray().FirstOrDefault(d => IsKind(d, "parked") && Member(d, "id") is { IsText: true } i && i.AsString() == id);
        if (parked.IsNull || Member(parked, "item") is not { IsText: true } named || named.AsString().Length == 0)
        {
            return null;
        }

        var errors = new List<string>();
        try
        {
            var folder = HierarchyModule.ProgressFolderOf(team);
            var pool = HierarchyModule.ProgressPoolOf(PoolContext(dir).MainHierarchyDirectory, dir);
            foreach (var entry in HierarchyModule.ChecklistEntries(progress, pool, folder).Where(e => e.Item.Item == named.AsString() && e.Item.State == PlanStates.WaitingUser))
            {
                if (progress.RecordChecklist(pool, folder, entry.Stream, by, HierarchyModule.ChecklistAnswerRows(entry.Item.Item, answer, id, _clock.UtcNow)).Refused is { } refused)
                {
                    errors.Add($"{entry.Stream}:{entry.Item.Item}: {refused}");
                }
            }
        }
#pragma warning disable CA1031 // the answer line is the record: no failure of a plan row may fail an answer already written
        catch (Exception e)
#pragma warning restore CA1031
        {
            errors.Add(e.Message);
        }

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    public JsonObject ConstraintLines(JsonValue? text)
    {
        var result = new JsonObject();
        var inside = false;
        foreach (var raw in JsConvert.ToJsString(text).Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = ConstraintsHeadingRegex().IsMatch(raw);
                continue;
            }

            if (!inside)
            {
                continue;
            }

            var m = ConstraintLineRegex().Match(raw);

            // `key in out` also sees Object.prototype: the one lowercase name there is "constructor".
            if (m.Success && m.Groups["key"].Value != "constructor" && !result.ContainsKey(m.Groups["key"].Value))
            {
                result[m.Groups["key"].Value] = m.Groups["value"].Value;
            }
        }

        return result;
    }

    public JsonValue LiveRun(string? cwd)
    {
        var dirs = new List<string>();
        foreach (var d in new[] { _config.HierarchyDir(cwd), _config.MainHierarchyDir(cwd) })
        {
            if (!string.IsNullOrEmpty(d) && !dirs.Contains(d))
            {
                dirs.Add(d);
            }
        }

        var anchors = new List<(JsonValue E, string Dir)>();
        foreach (var dir in dirs)
        {
            foreach (var e in OpenExchanges(dir, null, ListLiveExchangesAllTeams(dir)))
            {
                if (Member(e, "slug") is { IsText: true } s && s.AsString() == HierarchyModule.PipelineAnchorSlug
                    && !anchors.Any(a => StrictEquals(Member(a.E, "id"), Member(e, "id"))))
                {
                    anchors.Add((e, dir));
                }
            }
        }

        if (anchors.Count != 1)
        {
            return JsonValue.Null;
        }

        var (anchor, scanned) = anchors[0];
        var parsed = _core.ReadMsgFile(Member(Member(anchor, "request"), "path").AsString());
        return new JsonObject
        {
            ["id"] = Member(anchor, "id"),
            ["dir"] = HierarchyModule.HomeOf(anchor, scanned),
            ["constraints"] = ConstraintLines(parsed.Member("text") is { } t ? t : (JsonValue)string.Empty),
        };
    }

    public string MergeCommand(JsonValue? pr, JsonValue? sha, JsonValue? method, JsonValue? draft) =>
        $"{(draft is { IsTruthy: true } ? $"gh pr ready {JsConvert.ToJsString(pr)} && " : string.Empty)}gh pr merge {JsConvert.ToJsString(pr)} --match-head-commit {JsConvert.ToJsString(sha)} --{JsConvert.ToJsString(method)}";

    public JsonValue ParseMergeForm(JsonValue? command)
    {
        var text = JsConvert.ToJsString(command);
        var single = SingleMergeRegex().Match(text);
        if (single.Success)
        {
            return MergeForm(single, false);
        }

        var compound = CompoundMergeRegex().Match(text);
        if (compound.Success && compound.Groups["ready"].Value == compound.Groups["pr"].Value)
        {
            return MergeForm(compound, true);
        }

        return JsonValue.Null;
    }

    private static JsonObject MergeForm(Match m, bool draft) => new()
    {
        ["pr"] = double.Parse(m.Groups["pr"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture),
        ["sha"] = m.Groups["sha"].Value,
        ["method"] = m.Groups["method"].Value,
        ["draft"] = draft,
    };

    public void AppendMergeRecord(IStoreWriteContext writer, string path, JsonValue merge, string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (merge.IsNull)
        {
            throw new InvalidOperationException("Cannot destructure properties of a null merge");
        }

        // A field the merge object lacks is left out of the line, as JSON.stringify leaves out an undefined one.
        var line = new JsonObject { ["kind"] = "merge" };
        foreach (var field in new[] { "pr", "sha", "method" })
        {
            if (merge.Member(field) is { } value)
            {
                line[field] = value;
            }
        }

        line["time"] = IsoNowString();
        line["approval"] = "permission prompt";
        line["ran"] = true;
        AppendLine(writer, path, line, operationId);
    }

    /// <summary>The pool dir of a decision log path; only <c>&lt;hier&gt;/pipeline/&lt;run&gt;/decisions.jsonl</c> is a log.</summary>
    internal static string PoolOfLog(string? path)
    {
        if (path is null || NodePath.Basename(path) != "decisions.jsonl" || NodePath.Basename(NodePath.Dirname(NodePath.Dirname(path))) != "pipeline")
        {
            throw new InvalidOperationException($"{path} is not a decision log");
        }

        return NodePath.Dirname(NodePath.Dirname(NodePath.Dirname(path)));
    }

    internal static string ByteCut(string text, int max)
    {
        var bytes = Utf8Text.Encode(text);
        if (bytes.Length <= max)
        {
            return text;
        }

        return Utf8Text.Decode(bytes.AsSpan(0, max)).TrimEnd('�');
    }

    private static string LogPathOf(JsonValue args)
    {
        var path = args.Member("path");
        if (path is not { IsText: true } text)
        {
            throw new InvalidOperationException($"{JsConvert.ToJsString(path)} is not a decision log");
        }

        PoolOfLog(text.AsString());
        return text.AsString();
    }

    public JsonObject DecisionAppendOp(IStoreWriteContext writer, JsonValue args, string? operationId)
    {
        var path = LogPathOf(args);
        if (args.Member("merge") is not { IsTruthy: true } merge)
        {
            throw new InvalidOperationException("decisions.append takes a merge line; a decided or parked line goes through decisions.add");
        }

        AppendMergeRecord(writer, path, merge, operationId);
        return new JsonObject { ["appended"] = true };
    }

    public JsonObject DecisionAddOp(IStoreWriteContext writer, JsonValue args, string? operationId)
    {
        var path = LogPathOf(args);
        var r = AppendDecision(writer, path, args.Member("run"), args.Member("input"), operationId);
        if (r.Get("refused") is { IsMap: true } refused)
        {
            return new JsonObject
            {
                ["refused"] = new JsonObject
                {
                    ["reason"] = refused.Member("reason") ?? JsonValue.Null,
                    ["detail"] = ByteCut(JsConvert.ToJsString(refused.Member("detail")), RefusedDetailMax),
                },
            };
        }

        return new JsonObject { ["id"] = r["id"], ["counts"] = r["counts"] };
    }

    /// <summary>
    /// <c>decisions.keys</c>: <c>{dir}</c> is <c>{runs: [...]}</c>, the pipeline runs of that hierarchy directory that have a decision log, by
    /// listing <c>pipeline/</c> and checking each run for its log. It reads no log's content.
    /// </summary>
    public JsonObject DecisionKeysOp(JsonValue args)
    {
        var dir = args.Member("dir") is { IsText: true } d ? d.AsString() : throw new InvalidOperationException("dir must be a string");
        var runs = new JsonArray();
        if (StoredDecisionRuns(dir) is { } stored)
        {
            foreach (var run in stored)
            {
                runs.Add(run);
            }

            return new JsonObject { ["runs"] = runs };
        }

        IEnumerable<string> names;
        try
        {
            names = _files.List(NodePath.Join(dir, "pipeline")).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            names = [];
        }

        foreach (var run in names.Where(run => _files.Exists(DecisionLogPath(dir, run))))
        {
            runs.Add(run);
        }

        return new JsonObject { ["runs"] = runs };
    }

    // per-dir-files-to-db S9: the decision logs are rows of the decisions table while mode.decisions is db

    /// <summary>The write transaction a decisions DbBody runs in, on this thread: the log's reads and its one append go to it.</summary>
    private readonly AsyncLocal<IDbOpTransaction?> _decisionTx = new();

    /// <summary>A body that runs in the database writes no file; the writer it is handed refuses any file write.</summary>
    private sealed class NoFileWriter : IStoreWriteContext
    {
        internal static readonly NoFileWriter Instance = new();

        private static InvalidOperationException Refused => new("a decisions database write writes no file");

        public void Write(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => throw Refused;

        public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => throw Refused;

        public void Append(string path, ReadOnlySpan<byte> record) => throw Refused;

        public void Rename(string fromPath, string toPath) => throw Refused;

        public bool Remove(string path) => throw Refused;

        public void Mkdir(string path, UnixFileMode? mode = null) => throw Refused;
    }

    /// <summary><c>(hierDir, run)</c> of a decision log path <c>&lt;hier&gt;/pipeline/&lt;run&gt;/decisions.jsonl</c>; null for any other path.</summary>
    private static (string HierDir, string Run)? DecisionLogKey(string path) =>
        NodePath.Basename(path) == "decisions.jsonl" && NodePath.Basename(NodePath.Dirname(NodePath.Dirname(path))) == "pipeline"
            ? (MsgRows.PoolKey(PoolOfLog(path)), NodePath.Basename(NodePath.Dirname(path)))
            : null;

    /// <summary>The stored lines of a decision log, inside a DbBody's transaction or from the table; null when the log is read from its file.</summary>
    private IReadOnlyList<string>? DecisionRows(string path)
    {
        if (DecisionLogKey(path) is not var (hierDir, run))
        {
            return null;
        }

        if (_decisionTx.Value is { } tx)
        {
            return [.. tx.Query("SELECT body FROM decisions WHERE hier_dir = ?1 AND run = ?2 ORDER BY seq", hierDir, run).Select(static r => (string)r[0]!)];
        }

        return _decisions?.Bodies(hierDir, run);
    }

    /// <summary>Whether <paramref name="run"/> of <paramref name="dir"/> has a decision log.</summary>
    private bool HasDecisionLog(string dir, string run) =>
        _decisions?.Bodies(dir, run) is { } rows ? rows.Count > 0 : Exists(DecisionLogPath(dir, run));

    /// <summary>The runs of <paramref name="dir"/> with a decision log, in order; null when they are listed from <c>pipeline/</c>.</summary>
    private IReadOnlyList<string>? StoredDecisionRuns(string dir) => _decisions?.Runs(dir);

    private OpResult InDecisionTx(OpCall call, IDbOpTransaction tx, Func<JsonValue, JsonValue> body)
    {
        _decisionTx.Value = tx;
        try
        {
            return OpArgs.Run(call, body);
        }
        finally
        {
            _decisionTx.Value = null;
        }
    }

    /// <summary>
    /// <c>decisions.ask</c> in the database (per-dir-files-to-db S10), in-process only (the op is not served on the daemon route): the
    /// row as its log's next row. The question log (<c>qa-log.jsonl</c>) is not an asks table log: its rows go through <c>qa-log.append</c> (S6).
    /// </summary>
    internal OpResult AsksAppendDb(OpCall call, IDbOpTransaction tx, IStoreWriteContext files) =>
        InDecisionTx(call, tx, args => AsksAppendOp(NoFileWriter.Instance, args, call.OperationId));

    /// <summary><c>decisions.append</c> in the database: the merge line as the run's next row.</summary>
    internal OpResult DecisionAppendDb(OpCall call, IDbOpTransaction tx) => InDecisionTx(call, tx, args => DecisionAppendOp(NoFileWriter.Instance, args, call.OperationId));

    /// <summary><c>decisions.add</c> in the database: the decided, parked, answered or operational line as the run's next row.</summary>
    internal OpResult DecisionAddDb(OpCall call, IDbOpTransaction tx) => InDecisionTx(call, tx, args => DecisionAddOp(NoFileWriter.Instance, args, call.OperationId));

    public JsonObject DecisionReadOp(JsonValue args) =>
        args.Member("path") is { IsText: true } path
            ? ReadDecisions(path.AsString())
            : new JsonObject { ["decisions"] = new JsonArray(), ["skipped"] = 0 };

    private string Errno(string path, Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => NonDirectoryAlong(path) ? "ENOTDIR" : "ENOENT",
        UnauthorizedAccessException => "EACCES",
        IOException when e.Message.StartsWith("too many levels of symbolic links", StringComparison.Ordinal) => "ELOOP",
        _ => e.Message,
    };

    /// <summary>Whether the longest existing prefix of <paramref name="path"/> is something other than a directory (a path through a file).</summary>
    private bool NonDirectoryAlong(string path)
    {
        try
        {
            for (var current = path; current.Length > 0; current = NodePath.Dirname(current))
            {
                if (_files.Stat(current) is { } st)
                {
                    return st.Kind != FileKind.Directory;
                }

                if (NodePath.Dirname(current) == current)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
        }

        return false;
    }

    public JsonObject ReadDecisionInput(string dir, string runId, string inputPath)
    {
        var wanted = NodePath.Dirname(DecisionLogPath(dir, runId));
        string runDir;
        string real;
        try
        {
            runDir = _files.RealPath(wanted);
        }
        catch (Exception)
        {
            return new JsonObject { ["error"] = $"--input must be inside {wanted}, which doesn't exist" };
        }

        try
        {
            real = _files.RealPath(inputPath);
        }
        catch (Exception e)
        {
            return new JsonObject { ["error"] = $"--input {inputPath} can't be read ({Errno(inputPath, e)})" };
        }

        if (!real.StartsWith(runDir + "/", StringComparison.Ordinal))
        {
            return new JsonObject { ["error"] = $"--input must be inside {runDir}, not {real}" };
        }

        var st = _files.Stat(real) ?? throw new FileNotFoundException(real);
        if (st.Kind != FileKind.File)
        {
            return new JsonObject { ["error"] = $"--input {real} is not a regular file" };
        }

        if (st.Size > HierarchyModule.DecisionInputMax)
        {
            return new JsonObject { ["error"] = $"--input {real} is over {HierarchyModule.DecisionInputMax} bytes" };
        }

        try
        {
            return new JsonObject { ["input"] = JsonText.Parse(_files.ReadText(real)) };
        }
        catch (JsonParseException e)
        {
            return new JsonObject { ["error"] = $"--input {real} must hold one JSON object ({e.Message})" };
        }
    }

    private bool Listable(string dir)
    {
        try
        {
            _files.List(dir);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            if (NonDirectoryAlong(dir))
            {
                throw new IOException("not a directory: " + dir);
            }

            return false;
        }
    }

    /// <summary>
    /// Whether the team that opened <paramref name="anchor"/> has an orchestrator that is provably gone: its recorded session is not
    /// registered live and its recorded pid is dead or now another process. A team that cannot be named or read is not proven dead.
    /// </summary>
    private bool OwnerProvenDead(string dir, JsonValue anchor)
    {
        try
        {
            var parsed = _core.ReadMsgFile(Member(Member(anchor, "request"), "path").AsString());
            if (parsed.Member("fm")?.Member("team") is not { IsText: true } teamName || teamName.AsString() is not { Length: > 0 } team)
            {
                return false;
            }

            var record = _roster.ReadTeamRecord(dir, team);
            return !record.IsNull && _roster.TeamLifeOf(record) == Ah.Roster.TeamLife.Orphaned;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public string RunLiveness(string? cwd)
    {
        try
        {
            var dirs = new List<string>();
            foreach (var d in new[] { _config.HierarchyDir(cwd), _config.MainHierarchyDir(cwd) })
            {
                if (!string.IsNullOrEmpty(d) && !dirs.Contains(d))
                {
                    dirs.Add(d);
                }
            }

            foreach (var dir in dirs)
            {
                if (!Listable(dir))
                {
                    continue;
                }

                if (OpenExchanges(dir, null, ListLiveExchangesAllTeams(dir)).Any(e => Member(e, "slug") is { IsText: true } s && s.AsString() == HierarchyModule.PipelineAnchorSlug
                    && !OwnerProvenDead(dir, e)))
                {
                    return "live";
                }
            }
        }
        catch (Exception)
        {
            return "unknown";
        }

        return "not";
    }
}
