using Ah.Abstractions;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse (Bash): when the pinned <c>/pipeline</c> merge command ran, appends a <c>merge</c> line to the open run's decision
/// log. The record says the command ran; whether GitHub merged is read at report time. Its stdout is empty except for the
/// relay line of <see cref="PostToolUseRelayNotice"/>, which shares this Bash PostToolUse entry.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class PostToolUseMergeRecord
{
    public static HookOutcome Run(GateContext c)
    {
        PostToolUseLedgerRecord.Record(c);
        var relayed = PostToolUseRelayNotice.For(c);
        RecordMerge(c);
        return relayed ?? HookEmit.Silent;
    }

    private static void RecordMerge(GateContext c)
    {
        var command = c.Command() ?? string.Empty;
        if (c.ToolName() != "Bash" || !command.Contains("gh", StringComparison.Ordinal))
        {
            return;
        }

        var decisions = c.Services.Hierarchy.Decisions;
        var form = decisions.ParseMergeForm(command);
        if (!form.IsTruthy)
        {
            return;
        }

        var run = decisions.LiveRun(c.Cwd());
        if (!run.IsTruthy || run.Member("dir") is not { IsText: true } dir || run.Member("id") is not { IsText: true } id)
        {
            return;
        }

        c.Services.Hierarchy.StateCalls.AppendMergeRecord(decisions.DecisionLogPath(dir.AsString(), id.AsString()), form);
    }
}
