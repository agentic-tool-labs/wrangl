using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>Whether a <c>decision add</c> object's decider names a rule, which only <c>--rule</c> writes.</summary>
    private static bool DeciderNamesRule(JsonValue input) => input.IsMap && input.Member("decider") is { IsMap: true } decider && decider.AsObject().ContainsKey("rule");

    /// <summary>
    /// <c>decision add … --rule K</c> (spec-b19 slice 3): the decided line is the user's own standing answer, so its decider becomes <c>{role: user-rule, rule: K}</c> and a null
    /// choice becomes the rule's <c>choose</c>. Only a decision rule the user accepted, that applies to this caller as its session block resolves it, may decide, and never a
    /// dangerous question. Null when <paramref name="input"/> was rewritten, else the refusal's exit code with nothing logged.
    /// </summary>
    private int? ApplyRule(JsonValue input)
    {
        if (_opts.Text("rule") is not { Length: > 0 } key)
        {
            return Refuse("invalid", _opts.Has("rule") ? "--rule needs the key of an accepted decision rule" : "a decider naming a rule is written only with --rule <key>");
        }

        if (!(input.IsMap && input.Member("kind") is { IsText: true } kind && kind.AsString() == "decided"))
        {
            return Refuse("invalid", "--rule logs a decided line: give kind decided");
        }

        if (input.Member("dangerous") is { IsBool: true } dangerous && dangerous.AsBool())
        {
            return Refuse("rule-not-for-dangerous", $"a standing rule never decides a dangerous question; park it for the user (rule {key})");
        }

        RuleRow? row;
        try
        {
            var dir = BacklogVerb.DirOf(_graph, _cwd);
            var ask = RulesStoreVerb.CallerAsk(_env, _graph, dir);
            if (ask.Team is null && _teamArg is not null)
            {
                ask = ask with { Team = _teamArg };
            }

            row = RulesReads.ForMember(_graph, _env.AhHome, dir, ask).Rules.Select(r => r.Row).FirstOrDefault(r => r.Key == key);
        }
        catch (Exception e) when (e is RulesUnreadableException or StoreUnavailableException)
        {
            return Refuse("rules-unreadable", $"the rules could not be read, so rule {key} cannot decide: {e.Message}");
        }

        if (row is not { IsDecision: true })
        {
            return Refuse("unknown-rule", $"no decision rule {key} applies here; ah rules list shows them");
        }

        if (row.IsProposed)
        {
            return Refuse("rule-not-accepted", $"rule {key} is proposed and decides nothing until the user accepts it (ah rules accept {key})");
        }

        var obj = input.AsObject();
        obj["decider"] = new JsonObject { ["role"] = RulesOfEngagement.RuleDecider, ["rule"] = key };
        if (obj.Get("choice") is not { IsText: true })
        {
            obj["choice"] = "other: " + (row.Choose ?? string.Empty).Trim();
        }

        return null;
    }
}
