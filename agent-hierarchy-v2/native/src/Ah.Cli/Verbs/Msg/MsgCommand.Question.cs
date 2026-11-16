using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    private const string QuestionAnswerWriter = "decision-answer";

    /// <summary>
    /// The writer of an answer the Orchestrator relayed from a prompt the user typed. It is not <see cref="QuestionAnswerWriter"/>, so
    /// nothing that accepts only the user's own terminal answer (authority grants, member-launch consent) accepts it.
    /// </summary>
    private const string QuestionRelayWriter = "decision-answer-relayed";

    private static bool IsQuestionId(string? id) => id is { Length: > 1 } && id[0] == 'q' && id[1..].All(char.IsAsciiDigit);

    /// <summary>
    /// <c>decision answer q&lt;k&gt; &lt;option label&gt; [--note &lt;text&gt;] [--pick &lt;n&gt;]...</c>: the user's answer to a question parked while work was in
    /// flight. Only a caller with no Claude harness above it may run it. It writes the row a harness answer would have written to the asks
    /// log, then marks the question answered, then tells the Orchestrator.
    /// </summary>
    /// <summary>The open questions parked for the user, oldest first: id, header, age in minutes, what is assumed meanwhile, and the command that answers it.</summary>
    private JsonArray QuestionsWaiting(string dir)
    {
        _ = ParkedQuestions.MatchRecorded(_graph.Io.Files, Hier.StateCalls, dir);
        var path = ParkedQuestions.PathIn(dir);
        var files = _graph.Io.Files;
        var now = _graph.Io.Clock.UtcNow;
        var waiting = new JsonArray();
        // per-dir-files-to-db §6c G5: one read; a store that cannot be read lists none
        foreach (var q in ParkedQuestions.Fold(files.ReadAskLog(path).TryLines(out var lines) ? string.Join('\n', lines) : string.Empty).Where(q => q.Open))
        {
            var age = DateTimeOffset.TryParse(q.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? Math.Max(0, (now - at).TotalMinutes) : 0;
            waiting.Add(new JsonObject
            {
                ["id"] = q.Id,
                ["header"] = q.Header,
                ["age_minutes"] = Math.Floor(age),
                ["default"] = ParkedQuestions.DefaultText(q.Default),
                ["answer"] = $"ah msg decision answer {q.Id} \"{(q.Labels.Count > 0 ? q.Labels[0] : "<label>")}\"",
            });
        }

        return waiting;
    }

    /// <summary>
    /// <c>decision park --header H --question Q --option A --option B --default D</c>: the Orchestrator's question for the user, asked without
    /// holding its turn. The answer paths are those of any parked question; the output says what to do next.
    /// </summary>
    private int QuestionPark(string dir)
    {
        var caller = ResolveDecisionCaller();
        // ah2-32: the user parks too, which is how the admin session (ah admin grant) arrives
        if (caller.Class.Kind is not (CutoverCallerKind.TopOrchestrator or CutoverCallerKind.User))
        {
            return Refuse("park_not_orchestrator", "Only a team's Orchestrator parks a question. A member sends its question to its parent.", "park");
        }

        var header = _opts.Text("header");
        var text = _opts.Text("question");
        var labels = _opts.Texts("option");
        var assumed = _opts.Text("default");
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(text) || labels.Count < 2 || string.IsNullOrWhiteSpace(assumed))
        {
            return Refuse("bad_request", "usage: ah msg decision park --header <h> --question <q> --option <label> --option <label>... --default <what you assume until it is answered> [--for <name@team>] [--class blocker|soon|eventually]", "park");
        }

        var cls = _opts.Text("class") ?? HierarchyModule.ChecklistClassOf((string?)null);
        if (!HierarchyModule.ChecklistClasses.Contains(cls, StringComparer.Ordinal))
        {
            return Refuse("bad_request", $"--class {cls} is not one of {string.Join(", ", HierarchyModule.ChecklistClasses)}", "park");
        }

        var team = DecisionProof.TeamOf(caller.Class.Detail) ?? string.Empty;
        var askedFor = _opts.Text("for");
        if (askedFor is not null && HierarchyModule.AskerRefusal(askedFor, team, t => _graph.Roster.ReadTeamRecord(dir, t)) is { } notMember)
        {
            return Refuse("not_a_member", notMember, "park");
        }

        var home = Hier.Messages.TeamHome(dir, null, team) is { Length: > 0 } teamHome ? teamHome : dir;
        var path = ParkedQuestions.PathIn(home);
        var files = _graph.Io.Files;
        _ = ParkedQuestions.MatchRecorded(files, Hier.StateCalls, home, mayMark: true);

        // per-dir-files-to-db §6c G5: one read; an id taken from a store that cannot be read could restart at q1 and collide
        if (!files.ReadAskLog(path).TryLines(out var stored))
        {
            return Refuse("store_unreadable", "the asks store could not be read; no question was parked", "park");
        }

        var id = ParkedQuestions.NextId(ParkedQuestions.Fold(string.Join('\n', stored)));
        var options = new JsonArray();
        foreach (var label in labels)
        {
            options.Add(new JsonObject { ["label"] = label });
        }

        var question = new JsonObject { ["question"] = text, ["header"] = header, ["multiSelect"] = false, ["options"] = options };
        var at = Timestamps.ToIsoString(_graph.Io.Clock.UtcNow);
        var line = ParkedQuestions.ParkedLine(id, at, team, caller.SessionId, null, 0, question, null, assumed);
        if (askedFor is not null)
        {
            line["asked_for"] = askedFor;
        }

        line["class"] = cls;

        Hier.StateCalls.AppendParkedQuestion(home, line, new StateCallOptions(OperationId: $"park:{id}"));
        // b517: the append is not a write the daemon serves; the refresh is, and the watch model re-reads the home's parked questions on it
        Hier.Status.RefreshStatus(home);
        var parked = new ParkedQuestion(id, at, team, caller.SessionId, null, 0, question, null, assumed);
        _out.Json(new JsonObject { ["id"] = id, ["parked"] = true, ["next"] = ParkedQuestions.ParkedReason([parked]) });
        return 0;
    }

    private int QuestionAnswer(string dir)
    {
        var id = _opts.Positional[1];
        var caller = ResolveDecisionCaller();
        var byUser = caller.Class.Kind == CutoverCallerKind.User;
        if (!byUser && caller.Class.Kind is not (CutoverCallerKind.TopOrchestrator or CutoverCallerKind.User))
        {
            return Refuse("answer_not_user", $"Only the user can answer {id}. Run this in a terminal: ah msg decision answer {id} \"<label>\"", "answer");
        }

        // b433: --team reads that team's store (where park wrote it) and only that team's questions
        if (_teamArg is not null && Hier.Messages.TeamHome(dir, null, _teamArg) is { Length: > 0 } teamHome)
        {
            dir = teamHome;
        }

        var path = ParkedQuestions.PathIn(dir);
        var files = _graph.Io.Files;
        _ = ParkedQuestions.MatchRecorded(files, Hier.StateCalls, dir);
        // per-dir-files-to-db §6c G5: one read; a store that cannot be read is its own failure, not an unknown question
        if (!files.ReadAskLog(path).TryLines(out var stored))
        {
            return Refuse("store_unreadable", $"the asks store could not be read; {id} was not answered", "answer");
        }

        var question = ParkedQuestions.Fold(string.Join('\n', stored)).FirstOrDefault(q => q.Id == id && (_teamArg is null || q.Team == _teamArg));
        if (question is null)
        {
            return Refuse("unknown_question", $"No parked question {id} for this team.", "answer");
        }

        if (!question.Open)
        {
            return Refuse("already_answered", $"{id} was already answered \"{string.Join(", ", question.AnsweredLabels)}\" at {question.AnsweredAt}", "answer");
        }

        string? relayedPrompt = null;
        if (!byUser)
        {
            // The Orchestrator relays the user's answer only when a typed prompt has reached its own session since the question was parked.
            if (DecisionProof.TeamOf(caller.Class.Detail) != question.Team || caller.SessionId is not { Length: > 0 } callerSession)
            {
                return Refuse("answer_not_user", $"Only the user can answer {id}. Run this in a terminal: ah msg decision answer {id} \"<label>\"", "answer");
            }

            if (ParkedQuestions.IsTerminalOnly(question))
            {
                return Refuse("answer_relay_reserved", $"{id} ({question.Header}) can only be answered by the user. Ask them to run in a terminal: ah msg decision answer {id} \"{(question.Labels.Count > 0 ? question.Labels[0] : "<label>")}\"", "answer");
            }

            if (HierarchyModule.RelayPromptId(Hier.Peers.LatestUserPrompt(callerSession), question.At) is not { } promptId)
            {
                return Refuse("answer_no_user_prompt", $"No prompt typed by the user has reached this session since {id} was parked, so it cannot be answered for them. Ask them, or have them run: ah msg decision answer {id} \"<label>\"", "answer");
            }

            relayedPrompt = promptId;
        }

        var options = question.Labels;
        // b431: --choice <letter|other:text>, the form the waiting list prints for d<k> and items, is accepted here too; other:<text> that is no option is a free-text answer
        var choice = _opts.Text("choice")?.Trim();
        var other = choice is not null && choice.StartsWith("other:", StringComparison.OrdinalIgnoreCase) ? choice[6..].Trim() : null;
        var freeText = other is { Length: > 0 } && ParkedQuestions.MatchLabel(question, other) is null;
        var given = choice is not null
            ? [other ?? (choice is [var l] && char.ToLowerInvariant(l) is var c and >= 'a' and <= 'z' && c - 'a' < options.Count ? options[c - 'a'] : choice)]
            : _opts.Texts("pick").Count > 0
            ? _opts.Texts("pick").Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= options.Count ? options[n - 1] : p).ToList()
            : [string.Join(' ', _opts.Positional.Skip(2)).Trim()];
        if (given.Count == 0 || given.Any(g => g.Length == 0))
        {
            throw _out.Fail("usage: ah msg decision answer <q id> (<option label> | --choice <a|b|...|other:<text>>) [--note <text>] [--pick <n>]...");
        }

        var chosen = new List<string>();
        foreach (var label in given)
        {
            var match = freeText ? label : ParkedQuestions.MatchLabel(question, label);
            if (match is null)
            {
                return Refuse("unmatched_label", $"\"{label}\" is not an option for {id}. Options: {string.Join(" | ", options)}.", "answer");
            }

            chosen.Add(match);
        }

        var now = _graph.Io.Clock.UtcNow;
        var at = Timestamps.ToIsoString(now);
        var questions = new JsonArray();
        questions.Add(question.Question);
        var answers = new JsonObject { [question.Text] = string.Join(", ", chosen) };
        var response = new JsonObject { ["answers"] = answers };
        if (_opts.Text("note") is { } note)
        {
            response["note"] = note;
        }

        JsonValue session = question.SessionId is { } sessionId ? sessionId : JsonValue.Null;
        var writer = relayedPrompt is null ? QuestionAnswerWriter : QuestionRelayWriter;
        var evidence = relayedPrompt is null ? string.Empty : $";relayed-prompt={relayedPrompt}";
        var row = AskRecord.Row(at, writer, session, questions, response, question.Source is null ? $"ah:relay{evidence};ref={id}" : $"{question.Source};ref={id}{evidence}");
        var calls = Hier.StateCalls;
        calls.AppendAsk(dir, row, new StateCallOptions(OperationId: $"ans:{id}"));
        ParkedQuestions.Answer(calls, dir, id, at, chosen, writer, $"ans:{id}", freeText: freeText);
        // b517: as park does, so the answered question leaves the watch model's waiting list
        Hier.Status.RefreshStatus(dir);
        _ = Hier.Progress.AppendLedger(ChecklistPool, question.Team, AskRecord.Ruling(questions, response, null));
        if (byUser)
        {
            _ = _graph.Mailbox.Send(
                MeshModule.EventRequest($"orchestrator@{question.Team}", "user", _cwd, dir, "question-answered", $"{id} answered: {question.Header} = {string.Join(", ", chosen)}"),
                now);
        }

        // b19: the member it was parked for gets the same event, re-checked as still on the question's team; a skip or a failed send is noted, never fatal
        if (question.AskedFor is { } gone && HierarchyModule.AskerRefusal(gone, question.Team, t => _graph.Roster.ReadTeamRecord(dir, t)) is { } notMember)
        {
            _out.Note($"ah msg: decision answer recorded; the question-answered event to the asker was not sent: {notMember}");
        }
        else if (question.AskedFor is { } asker
            && _graph.Mailbox.Send(MeshModule.EventRequest(asker, "user", _cwd, dir, "question-answered", $"{id} answered: {question.Header} = {string.Join(", ", chosen)}"), now).Failure is { } failure)
        {
            _out.Note($"ah msg: decision answer recorded; the question-answered event to {asker} was not sent: {failure.Code}: {failure.Message}");
        }

        var result = new JsonObject { ["id"] = id, ["team"] = question.Team, ["answered"] = true, ["labels"] = string.Join(", ", chosen) };
        if (_teamArg is null)
        {
            _out.Note($"ah msg: answered {id} of team {question.Team} ({question.Header})");
        }
        if (relayedPrompt is not null)
        {
            result["relayed_prompt"] = relayedPrompt;
        }

        _out.Json(result);
        return 0;
    }
}
