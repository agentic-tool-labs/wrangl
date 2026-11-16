using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse (Bash): when the Orchestrator's own <c>ah msg decision answer q&lt;k&gt;</c> call answered a parked question, one
/// <c>systemMessage</c> line tells the user which question was answered for them and how to correct it. The call is a relay by
/// construction: the user answers in a terminal, which is not a tool call. Only a live Orchestrator session, never a subagent, gets
/// the line, and it writes nothing.
/// </summary>
internal static partial class PostToolUseRelayNotice
{
    [GeneratedRegex(@"\bdecision\s+answer\s+(q[0-9]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AnswerCommand();

    [GeneratedRegex("\"id\"\\s*:\\s*\"(q[0-9]+)\"\\s*,\\s*\"answered\"\\s*:\\s*true\\s*,\\s*\"labels\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")(\\s*,\\s*\"relayed_prompt\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\")?", RegexOptions.CultureInvariant)]
    private static partial Regex AnsweredCall();

    public static HookOutcome? For(GateContext c)
    {
        var s = c.Services;
        if (c.ToolName() != "Bash" || c.Command() is not { } command || !AnswerCommand().IsMatch(command)
            || s.Config.IsSubagent(c.Input) || s.Advisory is not { } advisory)
        {
            return null;
        }

        var stdout = c.Input.Member("tool_response")?.Member("stdout") is { IsText: true } text ? text.AsString() : string.Empty;
        var answered = AnsweredCall().Matches(stdout);
        if (answered.Count == 0)
        {
            return null;
        }

        var boxes = advisory.Callers.SessionDeciderBoxes(c.Cwd(), c.Input.NonEmpty("session_id"));
        if (boxes.Count == 0)
        {
            return null;
        }

        var stores = boxes.Select(b => b.Dir).OfType<string>().Distinct(StringComparer.Ordinal)
            .Select(dir => ParkedQuestions.Fold(AskStore.ReadStore(s, dir))).ToList();
        var lines = answered.Select(m =>
        {
            var id = m.Groups[1].Value;
            var header = stores.Select(folded => folded.FirstOrDefault(q => q.Id == id)?.Header).FirstOrDefault(h => h is not null) ?? "Question";
            var labels = JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(m.Groups[2].Value)).AsString();
            return m.Groups[3].Success
                ? $"ah: relayed your chat answer to {id} \"{header}\": {labels}. Wrong? Run in a terminal: ah msg decision answer {id} --help"
                : $"ah: answered {id} \"{header}\" for you: {labels}. Wrong? Run: ah msg decision answer {id} --help";
        });
        return HookEmit.Warn(string.Join('\n', lines));
    }
}
