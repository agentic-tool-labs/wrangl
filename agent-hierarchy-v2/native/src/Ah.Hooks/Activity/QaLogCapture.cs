using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Activity;

/// <summary>
/// Writes the user's question log (spec qa-history-all §4): the Orchestrator's typed prompts, its replies, and its terminal dialogs, one
/// row to each hierarchy dir the session decides for. A failed write is logged and changes nothing; nothing here prints.
/// </summary>
internal static class QaLogCapture
{
    /// <summary>Appends the row <paramref name="build"/> makes for each team the session is the live Orchestrator of.</summary>
    internal static void Append(GateContext c, AdvisoryServices advisory, string sessionId, Func<string?, JsonObject> build) =>
        EachDir(c, advisory, sessionId, (dir, team) => c.Services.Hierarchy.StateCalls.AppendQaLog(dir, build(team)));

    /// <summary>Runs <paramref name="write"/> once for each hierarchy dir the session decides for, with the team a row there names; a failure is logged per dir.</summary>
    private static void EachDir(GateContext c, AdvisoryServices advisory, string sessionId, Action<string, string?> write)
    {
        var s = c.Services;
        var boxes = advisory.Callers.SessionDeciderBoxes(c.Cwd(), sessionId);
        foreach (var dir in boxes.Select(static b => b.Dir).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            try
            {
                s.Bootstrap.EnsureHierarchyDir(dir);
                write(dir, TeamOf(boxes, dir));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }
        }
    }

    /// <summary>Whether a question boundary's text passes the typed test: the rule that wrote the question rows (spec §4.5).</summary>
    private static bool IsQuestion(AdvisoryServices advisory, GateServices s, string prompt) =>
        UserPromptSubmitPeerTracking.IsTypedPrompt(advisory, prompt, prompt, s.Hierarchy.Peers.ParseWrapper(prompt));

    /// <summary>The id of the session's question row in <paramref name="dir"/> that <paramref name="question"/>, a transcript boundary's text, names (Amendment 1 G1).</summary>
    private static string? QuestionIn(GateServices s, string dir, string sessionId, string? question) =>
        question is null ? null : HierarchyModule.QaQuestionFor(s.Hierarchy.Decisions.QaLogRows(dir), sessionId, question);

    /// <summary>
    /// The PostToolUse step (§4.2, Amendment 1 G1-G2): on a question's first or second tool step, its direct reply, filed under the
    /// stored question the transcript names. A boundary no stored question row matches files nothing, so its typed test never matters
    /// here (Amendment 2). Every call past the second step returns after the transcript read, before any database work.
    /// </summary>
    internal static void Direct(GateContext c, AdvisoryServices advisory)
    {
        var s = c.Services;
        try
        {
            if (c.Input.Text("tool_use_id") is not { Length: > 0 } toolUseId || advisory.Recovery.TranscriptPathOk(c.Input.Member("transcript_path")) is not { } transcript
                || HierarchyModule.QaEarlyStepDirect(s.Files, transcript, toolUseId, static _ => true) is not { } step
                || !Ah.Hooks.Lifecycle.StopOrchestratorLiveness.QaLogSession(c, out var gated, out var sessionId))
            {
                return;
            }

            var at = s.Clock.UtcNow.ToUnixTimeMilliseconds();
            EachDir(c, gated, sessionId, (dir, team) =>
            {
                if (QuestionIn(s, dir, sessionId, step.Question) is { } promptId)
                {
                    s.Hierarchy.StateCalls.AppendQaLog(dir, HierarchyModule.QaDirectRow(at, sessionId, team, step.Direct, promptId));
                }
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    /// <summary>The team a row in <paramref name="dir"/> names: the one team the session leads there, or null when it leads several, so every team's History shows it.</summary>
    internal static string? TeamOf(IReadOnlyList<DeciderBox> boxes, string dir)
    {
        var teams = boxes.Where(b => b.Dir == dir).Select(static b => b.Team).Distinct(StringComparer.Ordinal).ToList();
        return teams.Count == 1 ? teams[0] : null;
    }

    /// <summary>
    /// The Stop step (spec qa-history-replies §4.3, Amendment 1 G1): the direct reply of the turn's latest real question when it has one
    /// and the stored question its transcript text names is found (ignored when already held), then the turn row, filed under that
    /// question, else under the session's latest typed prompt as before. A boundary a stored question row matches is a real question;
    /// the typed test on its text decides only when none does (Amendment 2). The turn row is upserted by the turn's own id, so a Stop a
    /// Stop hook held replaces it. A turn with no text writes no turn row, nor does a turn with no tool call that a real question opened
    /// when its direct row was written: that row holds the same text. Each append is tried on its own.
    /// </summary>
    internal static void Reply(GateContext c, AdvisoryServices advisory, string sessionId)
    {
        var s = c.Services;
        try
        {
            var boxes = advisory.Callers.SessionDeciderBoxes(c.Cwd(), sessionId);
            // one dir's unreadable log leaves that dir with no stored questions; the other dirs' turn rows are still written
            IReadOnlyList<JsonValue> RowsOf(string dir)
            {
                try
                {
                    return s.Hierarchy.Decisions.QaLogRows(dir);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    c.LogError(e);
                    return [];
                }
            }

            var rows = boxes.Select(static b => b.Dir).OfType<string>().Distinct(StringComparer.Ordinal).ToDictionary(static d => d, RowsOf, StringComparer.Ordinal);
            bool Real(string boundary) => rows.Values.Any(r => HierarchyModule.QaQuestionFor(r, sessionId, boundary) is not null) || IsQuestion(advisory, s, boundary);
            var transcript = advisory.Recovery.TranscriptPathOk(c.Input.Member("transcript_path"));
            var scan = transcript is null ? new QaTurnReply(null, null, false) : HierarchyModule.QaReplyOf(s.Files, transcript, null, Real);
            var typed = scan.Found && Real(scan.Opening ?? string.Empty);
            var latest = s.Hierarchy.Peers.LatestUserPrompt(sessionId)?.Member("prompt_id") is { IsText: true } pid ? pid.AsString() : null;
            var at = s.Clock.UtcNow.ToUnixTimeMilliseconds();
            var text = scan.Found ? scan.Text : c.Input.Text("last_assistant_message") ?? scan.Text;
            var openingId = scan.Found ? scan.OpeningId : null;
            foreach (var (dir, held) in rows)
            {
                var team = TeamOf(boxes, dir);
                var question = scan.Question is null ? null : HierarchyModule.QaQuestionFor(held, sessionId, scan.Question);
                var directWritten = false;
                if (question is not null && !string.IsNullOrWhiteSpace(scan.Direct))
                {
                    try
                    {
                        s.Bootstrap.EnsureHierarchyDir(dir);
                        // A turn with no tool call has no PostToolUse row to keep: its direct row is the whole reply so far, so a Stop
                        // that follows a held Stop replaces it with the longer text. With a tool call the first row written stays.
                        s.Hierarchy.StateCalls.AppendQaLog(dir, HierarchyModule.QaDirectRow(at, sessionId, team, scan.Direct, question), replace: !scan.HadTool);
                        directWritten = true;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        c.LogError(e);
                    }
                }

                if (string.IsNullOrWhiteSpace(text) || (typed && !scan.HadTool && directWritten))
                {
                    continue;
                }

                try
                {
                    s.Bootstrap.EnsureHierarchyDir(dir);
                    var row = HierarchyModule.QaReplyRow(at, sessionId, team, text, question ?? latest, typed, !scan.Found, scan.Final, openingId);
                    s.Hierarchy.StateCalls.AppendQaLog(dir, row, replace: openingId is not null);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    c.LogError(e);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }
}
