using System.Diagnostics;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hooks.Plumbing;

/// <summary>
/// The PreToolUse chain: one process runs every gate whose matcher fits the tool, one after another under one shared budget, and
/// gives Claude Code the one answer it would have built from the gates' separate outputs.
/// </summary>
internal static partial class HookHost
{
    /// <summary>The unit that runs every PreToolUse gate matching the tool in one process.</summary>
    public const string ChainName = "pretooluse-chain";

    /// <summary>
    /// Whether a <c>hooks.json</c> matcher fits a tool: <c>*</c> or an empty matcher fits every tool, a matcher of only letters, digits,
    /// <c>_</c> and <c>|</c> fits the tools its alternatives name, and any other matcher is a regular expression that must match the
    /// whole tool name.
    /// </summary>
    internal static bool MatcherFits(string matcher, string toolName)
    {
        if (matcher.Length == 0 || matcher == "*")
        {
            return true;
        }

        if (matcher.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '|'))
        {
            return matcher.Split('|').Contains(toolName, StringComparer.Ordinal);
        }

        return Regex.IsMatch(toolName, "^(?:" + matcher + ")$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The gates of <paramref name="units"/> that run for <paramref name="toolName"/>, in table order.</summary>
    internal static IReadOnlyList<GateUnit> GatesFor(string toolName, IReadOnlyList<GateUnit> units) =>
        [.. units.Where(u => u.Matcher is not null && u.Name != ChainName && GateNames.Contains(u.Name) && MatcherFits(u.Matcher, toolName))];

    /// <summary>
    /// Whether the session is positively part of ah, asked once for the whole chain. Null, with one hook-errors row, when finding out
    /// failed or outlived <paramref name="budget"/>: a deny needs a positive identification, so that passes as a bare session does.
    /// </summary>
    private static bool? Identify(JsonValue payload, IHookErrorLog errors, Func<GateServices> services, TimeSpan budget)
    {
        var ask = Task.Run(() => Ah.Hooks.Lifecycle.OrchestratorIdentity.In(new GateContext(services(), payload, ChainName)));
        try
        {
            if (ask.Wait(budget))
            {
                return ask.Result;
            }

            TryLog(errors, new HookErrorRecord(ChainName, "the session could not be identified in time; this call was not checked"));
        }
        catch (AggregateException e)
        {
            TryLog(errors, new HookErrorRecord(ChainName, "the session could not be identified; this call was not checked: " + SafeText.Render(e.InnerException?.Message ?? e.Message)));
        }

        return null;
    }

    private static HookOutcome RunChain(
        ReadOnlyMemory<byte> stdin,
        IHookErrorLog errors,
        Func<GateServices> services,
        IReadOnlyList<GateUnit> units,
        TimeSpan? deadline)
    {
        if (UnusableInput(ChainName, stdin.Span) is { } why)
        {
            return InputUnusable(ChainName, FailsClosed(ChainName, true), why, errors);
        }

        try
        {
            var payload = JsonText.Parse(stdin.Span);
            var budget = deadline ?? GateDeadline;
            var clock = Stopwatch.StartNew();
            if (Identify(payload, errors, services, budget) is not true)
            {
                return HookEmit.Silent;
            }

            var toolName = payload.Member("tool_name") is { IsText: true } tool ? tool.AsString() : string.Empty;
            var results = new List<(string Name, HookOutcome Outcome)>();
            foreach (var unit in GatesFor(toolName, units))
            {
                var remaining = budget - clock.Elapsed;
                var outcome = remaining > TimeSpan.Zero
                    ? Run(unit.Name, true, stdin, errors, services, units, remaining, identified: true)
                    : TimedOut(unit.Name, stdin, errors, services, units);
                results.Add((unit.Name, outcome));
            }

            return Merge(results, errors);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(ChainName, true, ex, errors);
        }
    }

    /// <summary>What one gate printed, read back: its decision with the reason, and the context and message it added.</summary>
    private sealed record Printed(string? Decision, string? Reason, string? Context, string? Message, JsonValue? UpdatedInput = null);

    internal const string TwoRewritesReason = "ah: two gates each rewrote this call's input, so it was held.";

    private static int Rank(string decision) => decision switch
    {
        "deny" => 3,
        "defer" => 2,
        "ask" => 1,
        "allow" => 0,
        _ => -1,
    };

    /// <summary>
    /// The answer Claude Code builds from the separate outputs of several hooks: the strongest decision (deny, then defer, ask, allow)
    /// with the reasons of the gates that gave it, every additional context and every system message. It reads what each gate
    /// printed, so it cannot drift from the printed form. A gate whose output cannot be read is that gate's failure.
    /// </summary>
    private static HookOutcome Merge(IReadOnlyList<(string Name, HookOutcome Outcome)> results, IHookErrorLog errors)
    {
        var printed = new List<Printed>();
        foreach (var (name, outcome) in results)
        {
            if (Read(outcome) is { } read)
            {
                printed.Add(read);
                continue;
            }

            if (FailsClosed(name, true))
            {
                printed.Add(new Printed(
                    "deny",
                    $"ah: the {name} gate hit an internal error and could not check this call, so it was held; see the hook error log.",
                    null,
                    "ah: a gate could not check this call, so it was held; see the hook error log."));
            }
            else
            {
                TryLog(errors, new HookErrorRecord(name, "the gate's output could not be read by the chain"));
            }
        }

        var winner = printed.Where(p => p.Decision is not null).Select(p => Rank(p.Decision!)).DefaultIfEmpty(-1).Max();
        var decision = winner < 0 ? null : printed.First(p => p.Decision is not null && Rank(p.Decision) == winner).Decision;
        var reasons = decision is null
            ? []
            : printed.Where(p => p.Decision == decision && !string.IsNullOrEmpty(p.Reason)).Select(static p => p.Reason!).ToList();

        // A rewritten input reaches the tool only when no gate holds or asks about the call. Two rewrites cannot both be applied, so the call is held.
        var rewrites = printed.Where(static p => p.UpdatedInput is not null).Select(static p => p.UpdatedInput!.Value).ToList();
        JsonValue? updated = null;
        if (rewrites.Count > 1)
        {
            if (decision != "deny")
            {
                reasons.Clear();
            }

            decision = "deny";
            reasons.Add(TwoRewritesReason);
        }
        else if (rewrites.Count == 1 && decision is null or "allow")
        {
            updated = rewrites[0];
        }
        else if (rewrites.Count == 1 && decision is "ask" or "defer")
        {
            TryLog(errors, new HookErrorRecord(ChainName, $"a gate's rewrite of the call's input was dropped because the call is held for {decision}"));
        }

        return HookEmit.Combined(
            decision,
            reasons.Count == 0 ? null : string.Join("\n\n", reasons),
            JoinNonEmpty("\n\n", printed.Select(static p => p.Context)),
            JoinNonEmpty("\n", printed.Select(static p => p.Message)),
            updated);
    }

    private static string? JoinNonEmpty(string separator, IEnumerable<string?> parts)
    {
        var kept = parts.Where(static p => !string.IsNullOrEmpty(p)).ToList();
        return kept.Count == 0 ? null : string.Join(separator, kept);
    }

    /// <summary>The decision, context and message in a gate's output; null when it exited non-zero or printed something that is not a JSON object or has a decision the chain does not know.</summary>
    private static Printed? Read(HookOutcome outcome)
    {
        if (outcome.ExitCode != 0)
        {
            return null;
        }

        if (outcome.Stdout.IsEmpty)
        {
            return new Printed(null, null, null, null);
        }

        JsonValue json;
        try
        {
            json = JsonText.Parse(outcome.Stdout.Span);
        }
        catch (JsonParseException)
        {
            return null;
        }

        if (!json.IsMap)
        {
            return null;
        }

        var inner = json.Member("hookSpecificOutput");
        var decision = inner?.Member("permissionDecision") is { IsText: true } d ? d.AsString() : null;
        if (decision is not null && Rank(decision) < 0)
        {
            return null;
        }

        return new Printed(
            decision,
            inner?.Member("permissionDecisionReason") is { IsText: true } r ? r.AsString() : null,
            inner?.Member("additionalContext") is { IsText: true } c ? c.AsString() : null,
            json.Member("systemMessage") is { IsText: true } m ? m.AsString() : null,
            inner?.Member("updatedInput") is { IsMap: true } u ? (JsonValue?)u : null);
    }
}
