using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse (AskUserQuestion): appends what the Orchestrator asked the user and what came back, both as received, to
/// <c>&lt;hierarchy dir&gt;/asks.jsonl</c>. Only the verb that counts answers reads the response, so a response shape nobody has seen
/// still lands. The answers are the user's and are stored unredacted, so only a session that a team file
/// records as its Orchestrator writes; every other session writes nothing and leaves no error. A payload that lacks
/// the questions or the response is a hook-errors row. A write that fails, a time-budget timeout included, is a hook-errors
/// row and one additionalContext line saying the answer was not recorded; otherwise it writes no stdout, except for the
/// member-launch consent question, whose outcome (recorded in which directories, or not recorded and why) is always said. Never fails the tool.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class PostToolUseAskRecord
{
    /// <summary>Keeps this write's operation id apart from the pre-call gate's, which names the same call in the same state directory.</summary>
    internal const string RecordOperationPrefix = "rec:";

    /// <summary>
    /// How long an answer's write may take. The answer is rare and cannot be asked for again, so it gets far more than the hook
    /// default, which a busy daemon can spend before the in-process write is attempted.
    /// </summary>
    private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(10);

    private const string TracePrefix = "trace: ask-record: ";

    /// <summary>One hook-errors row that says how this run went, so a run that records nothing still leaves a trace.</summary>
    private static void Trace(GateContext c, string text)
    {
        try
        {
            c.Services.ErrorLog.Log(new HookErrorRecord(c.Name, TracePrefix + text, c.Input.Text("hook_event_name"), c.Input.Text("session_id")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the log is the last link in the chain: there is nowhere left to report to
        }
    }

    private const string NotRecordedNotice =
        "ah: the user's answer to the AskUserQuestion was NOT recorded in asks.jsonl (the write failed; see hook-errors.jsonl). " +
        "Anything that reads that file will not see this answer: ask again if the answer must persist.";

    private static HookOutcome ConsentNotice(string reason) => HookEmit.AdditionalContext("PostToolUse", "ah: consent NOT recorded: " + reason);

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (c.ToolName() != "AskUserQuestion")
        {
            return HookEmit.Silent;
        }

        AskItemMove.Run(c);

        // A denied or failed question was never answered: its item goes back off the user's list (above), and nothing is recorded as an answer or a consent.
        if (c.Input.Text("hook_event_name") is "PostToolUseFailure" or "PermissionDenied")
        {
            return HookEmit.Silent;
        }

        try
        {
            var cwd = c.Cwd();
            var sessionId = c.Input.NonEmpty("session_id");
            var tagged = c.ToolInput().Get("metadata")?.Member("source") is { IsText: true };
            Trace(c, $"entry tool_use_id={c.Input.NonEmpty("tool_use_id") ?? "none"} session={sessionId ?? "none"} cwd={cwd} metadata_source={(tagged ? "seen" : "absent")}");
            if (s.Advisory is not { } advisory)
            {
                Trace(c, "exit without recording: the advisory services are unavailable");
                return HookEmit.Silent;
            }

            var boxes = advisory.Callers.SessionDeciderBoxes(cwd, sessionId);
            var consent = c.ToolInput().Get("metadata")?.Member("source") is { IsText: true } tag
                && tag.AsString().Contains("consent=member-launch-rules", StringComparison.Ordinal);
            if (boxes.Count == 0)
            {
                Trace(c, "exit without recording: the session is not the live Orchestrator of any team");

                // A session with a decider box is in by owning its team, so this is the only line a bare session could hear.
                return consent && Ah.Hooks.Lifecycle.OrchestratorIdentity.In(c)
                    ? ConsentNotice("no team found: this session is not the live Orchestrator of any team")
                    : HookEmit.Silent;
            }

            var questions = c.ToolInput().Get("questions");
            var response = c.Input.Member("tool_response");
            if (questions is not { IsArray: true } || response is not { IsNull: false })
            {
                throw new InvalidOperationException("the AskUserQuestion payload has no questions array or no response");
            }

            var row = AskRecord.Row(
                Timestamps.ToIsoString(s.Clock.UtcNow),
                Ah.Config.ConfigModule.AskRecordWriter,
                c.Input.Member("session_id"),
                questions.Value,
                response.Value,
                c.ToolInput().Get("metadata")?.Member("source") is { IsText: true } source ? source.AsString() : null);

            // this build's answered line carries the row's own at, so the sweep may match the row; an older build's row it skips
            row[ParkedQuestions.SweepField] = 1.0;

            // The engine answers a repeated operation id from its ledger without writing, so this write needs an id of its own.
            // The tool-use id still names the call, so a hook that fires twice for one call writes it once.
            var toolUseId = c.Input.NonEmpty("tool_use_id");
            var options = new StateCallOptions(OperationId: toolUseId is null ? null : RecordOperationPrefix + toolUseId, Hook: true, Budget: WriteBudget);
            var lost = false;
            var written = 0;
            var writtenDirs = new List<string>();
            foreach (var dir in boxes.Select(b => b.Dir).OfType<string>()
                .DistinctBy(d => RealOf(s, d), StringComparer.Ordinal))
            {
                try
                {
                    s.Bootstrap.EnsureHierarchyDir(dir);
                    s.Hierarchy.StateCalls.AppendAsk(dir, row, options);
                    written++;
                    writtenDirs.Add(dir);
                }
                catch (Exception e)
                {
                    lost = true;
                    c.LogError(e);
                }
            }

            // the question log mirrors the answer only where asks.jsonl took it, so a dir that failed logs its error once
            if (sessionId is not null)
            {
                var at = s.Clock.UtcNow.ToUnixTimeMilliseconds();
                foreach (var dir in writtenDirs)
                {
                    try
                    {
                        s.Hierarchy.StateCalls.AppendQaLog(dir, HierarchyModule.QaAskRow(at, sessionId, QaLogCapture.TeamOf(boxes, dir), questions.Value, response.Value));
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        c.LogError(e);
                    }
                }
            }

            if (lost)
            {
                Trace(c, $"exit with the answer not recorded: {written} directories written, the rest failed");
                return consent ? ConsentNotice("the write failed; see hook-errors.jsonl") : HookEmit.AdditionalContext("PostToolUse", NotRecordedNotice);
            }

            Trace(c, $"recorded the answer in {written} directories");
            if (!s.Config.IsSubagent(c.Input))
            {
                RecordRuling(c, boxes, questions.Value, response.Value);
                ResolveParked(c, writtenDirs, questions.Value, response.Value, row["at"].AsString());
            }

            if (consent)
            {
                var answer = response.Value.Member("answers")?.Member(Ah.Config.ConfigModule.MemberLaunchConsentQuestion);
                return answer is { IsText: true } text && Ah.Config.ConfigModule.IsMemberLaunchAllowAnswer(text.AsString())
                    ? HookEmit.AdditionalContext("PostToolUse", "ah: consent recorded in " + string.Join(", ", writtenDirs))
                    : ConsentNotice("the answer is not Allow (it was recorded as declined)");
            }
        }
        catch (Exception e)
        {
            c.LogError(e);
        }

        return HookEmit.Silent;
    }

    /// <summary>
    /// The user answered a dialog question that stands for a parked one (its <c>ref=q&lt;k&gt;</c> tag, else the same question text) with options of
    /// that question: the parked question is marked answered, as the terminal answer marks it. An answer that is not one of its options
    /// resolves nothing.
    /// </summary>
    private static void ResolveParked(GateContext c, IReadOnlyList<string> dirs, JsonValue questions, JsonValue response, string at)
    {
        var s = c.Services;
        try
        {
            var source = c.ToolInput().Get("metadata")?.Member("source") is { IsText: true } tag ? tag.AsString() : null;
            var answers = response.Member("answers");
            var toolUseId = c.Input.NonEmpty("tool_use_id");
            // the answered line carries the asks row's own at: the key that marks the row consumed (ParkedQuestions.MatchRecorded)
            foreach (var dir in dirs)
            {
                var resolved = new HashSet<string>(StringComparer.Ordinal);
                foreach (var asked in questions.AsArray())
                {
                    var text = asked.Member("question") is { IsText: true } t ? t.AsString() : null;
                    var folded = ParkedQuestions.Fold(AskStore.ReadStore(s, dir));
                    var askedLabels = AskStore.AskedLabels(asked);
                    var parked = ParkedQuestions.MatchAsked([.. folded.Where(q => !resolved.Contains(q.Id))], text, askedLabels, source);
                    if (parked is null && source is not null && source.Contains("ref=q", StringComparison.Ordinal))
                    {
                        c.LogError(new InvalidOperationException("the answer's ref tag matched no open parked question by text or by a unique option set; nothing was resolved"));
                    }

                    if (parked is null || text is null || answers?.Member(text) is not { IsText: true } answer)
                    {
                        continue;
                    }

                    // A free-text ("Other") answer resolves only a question whose text matches, and is recorded verbatim.
                    var chosen = ParkedQuestions.ChosenLabels(parked, answer.AsString());
                    var freeText = chosen is null && ParkedQuestions.SameText(parked, text);
                    if (chosen is null && !freeText)
                    {
                        continue;
                    }

                    ParkedQuestions.Answer(
                        s.Hierarchy.StateCalls, dir, parked.Id, at, chosen ?? [answer.AsString()], Ah.Config.ConfigModule.AskRecordWriter, RecordOperationPrefix + toolUseId,
                        hook: true, via: "dialog", freeText: freeText);
                    resolved.Add(parked.Id);
                }

                // answers recorded earlier while the store could not be read were matched to nothing; match them now
                _ = ParkedQuestions.MatchRecorded(s.Files, s.Hierarchy.StateCalls, dir, hook: true, mayMark: true);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    /// <summary>Appends one <c>ruling</c> to the ledger of each team the session owns: each question's header and the label(s) the user chose.</summary>
    private static void RecordRuling(GateContext c, IReadOnlyList<Ah.Mesh.DeciderBox> boxes, JsonValue questions, JsonValue response)
    {
        var s = c.Services;
        var pool = ProgressPool.Of(s, c.Cwd());
        var ev = AskRecord.Ruling(questions, response, c.Input.NonEmpty("tool_use_id"));
        foreach (var team in boxes.Select(b => b.Team).Distinct(StringComparer.Ordinal))
        {
            s.Hierarchy.Progress.AppendLedger(pool, team, ev);
        }
    }

    private static string RealOf(GateServices s, string path)
    {
        try
        {
            return s.Files.RealPath(path) is { Length: > 0 } real ? real : path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }
}
