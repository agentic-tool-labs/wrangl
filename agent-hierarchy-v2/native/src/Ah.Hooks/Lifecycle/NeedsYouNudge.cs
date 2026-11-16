using System.Text.RegularExpressions;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The Orchestrator's turn-end reminder (atp-1703 part 4): a reply that asks the user something, in a turn that set no waiting-user item,
/// gets one line of additionalContext asking for the item. It never blocks; a held Stop (<c>stop_hook_active</c>) gets none, so it says it at
/// most once a turn. The test is plain text, and a miss is fine.
/// </summary>
internal static partial class NeedsYouNudge
{
    internal const string Reminder =
        "ah: this reply asks the user something, and no waiting-user item was set this turn. If it needs their answer, park it as a Needs-you item with --class: "
        + "`ah roster checklist set <id> --state waiting-user --note \"<question>\" --class blocker|soon|eventually`, or `ah msg decision park ... --class <c>`.";

    // a "waiting on you" / "needs you" list, or a line ending in "?" that speaks to the user
    [GeneratedRegex(@"\b(waiting on you|needs[ -]you)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AsksList();

    [GeneratedRegex(@"\b(you|your|jim)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ToUser();

    // a waiting-user item set or changed by the turn's own commands
    [GeneratedRegex(@"checklist\s+set\b[^\n]*waiting[-_]user|decision\s+park\b")]
    private static partial Regex SetsItem();

    /// <summary>Whether <paramref name="reply"/> asks the user something while none of <paramref name="commands"/> set a waiting-user item.</summary>
    internal static bool Applies(string? reply, IEnumerable<string> commands) =>
        reply is { Length: > 0 } text && Asks(text) && !commands.Any(SetsItem().IsMatch);

    private static bool Asks(string text) =>
        AsksList().IsMatch(text)
        || text.Split('\n').Select(static l => l.Trim()).Any(l => l.EndsWith('?') && ToUser().IsMatch(l));

    /// <summary>The reminder for this Stop, or null. Any failure is logged and says nothing.</summary>
    internal static string? Notice(GateContext c)
    {
        try
        {
            if (c.Input.Member("stop_hook_active") is { IsBool: true } active && active.AsBool() || c.Services.Advisory is not { } advisory)
            {
                return null;
            }

            var commands = new List<string>();
            var transcript = advisory.Recovery.TranscriptPathOk(c.Input.Member("transcript_path"));
            var scan = transcript is null ? null : HierarchyModule.QaReplyOf(c.Services.Files, transcript, commands);
            return Applies(c.Input.Text("last_assistant_message") ?? scan?.Text, commands) ? Reminder : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return null;
        }
    }
}
