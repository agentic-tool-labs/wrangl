using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>What a soft-gate key covers: the call itself, or the whole session (once per session and agent).</summary>
internal enum SoftScope
{
    Input,
    Session,
}

/// <summary>
/// The one soft-intercept mechanism. A rule whose trigger fired calls <see cref="Bounce"/>: the first such call is denied with the
/// rule's fix, an identical retry passes where the rule allows it, and a third always passes, so no rule loops. Every outcome is one
/// <c>soft-gate</c> row in gates.jsonl. Any error is a silent pass.
/// </summary>
internal static class SoftGate
{
    public const string RowType = "soft-gate";

    private const int ReasonLimit = 200;

    // approvedBy: the rule whose `approve` row for member lifts this call; rule itself when null.
    public static HookOutcome Bounce(GateContext c, string rule, string reason, string? fix, bool retryPasses, SoftScope scope = SoftScope.Input, string? member = null, bool noGiveup = false, string? approvedBy = null)
    {
        // ah2-310 A6: outside the try, and it never throws, so a failed lookup is no member, never a silent pass.
        var who = member ?? SoftRules.MemberOfSession(c);
        try
        {
            var dir = c.Services.Config.HierarchyDir(c.Cwd());
            var all = Rows(c, dir);
            var key = Key(c, rule, scope);

            if (member is not null && Unconsumed(all, member, approvedBy ?? rule))
            {
                Record(c, dir, approvedBy ?? rule, "approved", key, 0, reason, member);
                return HookEmit.Silent;
            }

            var n = SinceReset(c, all).Count(r => r.Text("key") == key && r.Text("outcome") == "bounce");
            var outcome = n == 0 || noGiveup || (n == 1 && !retryPasses) ? "bounce" : n == 1 ? "retry-pass" : "giveup";
            // ah2-310 A3'/A7: the member is on every row, so `permissions diagnose` finds its denials; only a call that passes a member
            // is liftable, and its row names the rule an approval of it takes.
            Record(c, dir, rule, outcome, key, n, reason, who, member is null ? null : approvedBy ?? rule);
            if (outcome != "bounce")
            {
                return HookEmit.Silent;
            }

            return HookEmit.Nudge(Text(reason, fix, retryPasses), $"ah: bounced a {c.ToolName()} call ({rule}); the agent was told the fix.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.LogError(e);
            return HookEmit.Silent;
        }
    }

    /// <summary>
    /// A nudge that never denies: the first call of this session and agent gets <paramref name="text"/> as added context and a
    /// <c>nudge</c> row; every later one passes silently.
    /// </summary>
    public static HookOutcome Once(GateContext c, string rule, string text)
    {
        try
        {
            var dir = c.Services.Config.HierarchyDir(c.Cwd());
            var key = Key(c, rule, SoftScope.Session);
            if (SinceReset(c, Rows(c, dir)).Any(r => r.Text("key") == key && r.Text("outcome") == "nudge"))
            {
                return HookEmit.Silent;
            }

            Record(c, dir, rule, "nudge", key, 0, text, null);
            return HookEmit.AdditionalContext("PreToolUse", text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.LogError(e);
            return HookEmit.Silent;
        }
    }

    /// <summary>
    /// The bounce, two lines: <c>NOT DONE: &lt;what is missing&gt;</c>, then <c>Fix: &lt;the fix&gt;</c>. A reason of several lines keeps its
    /// first as what is missing and folds the rest into the fix line.
    /// </summary>
    internal static string Text(string reason, string? fix, bool retryPasses)
    {
        var lines = (reason.StartsWith("ah: ", StringComparison.Ordinal) ? reason[4..] : reason).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rest = string.Join(' ', lines.Skip(1).Concat(fix?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []));
        rest += retryPasses ? (rest.Length == 0 ? string.Empty : " ") + "Or re-run it unchanged if it is right as it is." : string.Empty;
        return "NOT DONE: " + lines.FirstOrDefault() + (rest.Length == 0 ? string.Empty : "\nFix: " + rest);
    }

    /// <summary>Every soft-gate row of the hierarchy directory, oldest first.</summary>
    public static List<JsonValue> Rows(GateContext c, string dir) =>
        [.. c.Services.Hierarchy.Roster.ReadGates(dir).Where(static r => r.Text("type") == RowType)];

    /// <summary>The rows after this session's latest <c>reset</c> row.</summary>
    public static IEnumerable<JsonValue> SinceReset(GateContext c, List<JsonValue> rows)
    {
        var session = c.SessionKey();
        var reset = rows.FindLastIndex(r => r.Text("outcome") == "reset" && r.Text("session_id") == session);
        return rows.Skip(reset + 1);
    }

    /// <summary>Appends <paramref name="row"/> as a soft-gate row of this session.</summary>
    public static void Note(GateContext c, string dir, JsonObject row)
    {
        row["type"] = RowType;
        row["session_id"] = c.SessionKey();
        c.Services.Hierarchy.Roster.AppendGateRouted(dir, row);
    }

    // An approval is unconsumed when it is newer than the latest `approved` row of the same member and rule.
    private static bool Unconsumed(List<JsonValue> rows, string member, string rule)
    {
        int Last(string outcome) => rows.FindLastIndex(r => r.Text("outcome") == outcome && r.Text("member") == member && r.Text("rule") == rule);
        return Last("approve") > Last("approved");
    }

    private static string Key(GateContext c, string rule, SoftScope scope)
    {
        var input = scope == SoftScope.Session ? string.Empty
            : c.ToolName() + (c.ToolName() == "Bash" ? c.Command() : JsonText.Stringify(c.ToolInput(), 0));
        var text = string.Join('\0', rule, c.SessionKey(), c.Input.Text("agent_id") ?? string.Empty, input);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static void Record(GateContext c, string dir, string rule, string outcome, string key, int n, string reason, string? member, string? approvableBy = null)
    {
        var row = new JsonObject
        {
            ["rule"] = rule,
            ["outcome"] = outcome,
            ["key"] = key,
            ["n"] = n,
            ["tool"] = c.ToolName() ?? string.Empty,
            ["reason"] = reason.Length > ReasonLimit ? reason[..ReasonLimit] : reason,
        };
        if (c.Input.NonEmpty("agent_id") is { } agent)
        {
            row["agent_id"] = agent;
        }

        if (c.Input.NonEmpty("tool_use_id") is { } use)
        {
            row["tool_use_id"] = use;
        }

        if (c.Services.Config.ResolveHierarchyRole(c.Input).Role is { } role)
        {
            row["role"] = role;
        }

        if (member is not null)
        {
            row["member"] = member;
        }

        if (approvableBy is not null)
        {
            row["approvable_by"] = approvableBy;
        }

        Note(c, dir, row);
    }
}
