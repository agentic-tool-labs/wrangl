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
    internal const string QuestionAnswerWriter = "decision-answer";

    /// <summary>
    /// The writer of an answer the Orchestrator relayed from a prompt the user typed. It is not <see cref="QuestionAnswerWriter"/>, so
    /// nothing that accepts only the user's own terminal answer (authority grants, member-launch consent) accepts it.
    /// </summary>
    private const string QuestionRelayWriter = "decision-answer-relayed";

    private static bool IsQuestionId(string? id) => ParkedQuestions.IsId(id);

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

        return Park(dir, caller, team, header, text, labels, assumed, askedFor, cls, null);
    }

    /// <summary>
    /// <c>ah adr ask &lt;id&gt;</c> (b195): parks a q&lt;k&gt; asking the user to settle proposed ADR <paramref name="n"/>, options Accept and Reject, stamped
    /// <see cref="ParkedQuestions.AdrAskSource"/> with the ADR number. The caller rule is park's. Refused when an open question already asks it.
    /// </summary>
    internal static int AdrAsk(CliContext context, ServiceGraph graph, string cwd, long n, string title, TextWriter stdout, TextWriter stderr)
    {
        var command = new MsgCommand(context, graph, MsgOptions.Parse(["--cwd", cwd]), "adr-ask", new MsgOutput(stdout, stderr), stdout, stderr);
        try
        {
            command._teamArg = command.ResolveTeamArg();
            var caller = command.ResolveDecisionCaller();
            if (caller.Class.Kind is not (CutoverCallerKind.TopOrchestrator or CutoverCallerKind.User))
            {
                return command.Refuse("park_not_orchestrator", "Only a team's Orchestrator parks a question. A member sends its question to its parent.", "park");
            }

            var id = AdrVerb.Id(n);
            return command.Park(command.HierarchyDir, caller, DecisionProof.TeamOf(caller.Class.Detail) ?? string.Empty, id, $"Settle {id}: {title}?", ["Accept", "Reject"], $"{id} stays proposed.", null, HierarchyModule.ChecklistClassOf((string?)null), n);
        }
        catch (MsgExit exit)
        {
            return exit.Code;
        }
    }

    private int Park(string dir, DecisionCaller caller, string team, string header, string text, IReadOnlyList<string> labels, string assumed, string? askedFor, string cls, long? adr)
    {
        var home = Hier.Messages.TeamHome(dir, null, team) is { Length: > 0 } teamHome ? teamHome : dir;
        var path = ParkedQuestions.PathIn(home);
        var files = _graph.Io.Files;
        _ = ParkedQuestions.MatchRecorded(files, Hier.StateCalls, home, mayMark: true);

        // per-dir-files-to-db §6c G5: one read; an id taken from a store that cannot be read could restart at q1 and collide
        if (!files.ReadAskLog(path).TryLines(out var stored))
        {
            return Refuse("store_unreadable", "the asks store could not be read; no question was parked", "park");
        }

        var folded = ParkedQuestions.Fold(string.Join('\n', stored));
        if (adr is not null && folded.FirstOrDefault(q => q.Open && q.Adr == adr) is { } open)
        {
            return Refuse("adr_already_asked", $"{open.Id} already asks the user to settle {AdrVerb.Id(adr.Value)}", "park");
        }

        var id = ParkedQuestions.NextId(folded);
        var options = new JsonArray();
        foreach (var label in labels)
        {
            options.Add(new JsonObject { ["label"] = label });
        }

        var question = new JsonObject { ["question"] = text, ["header"] = header, ["multiSelect"] = false, ["options"] = options };
        var at = Timestamps.ToIsoString(_graph.Io.Clock.UtcNow);
        var source = adr is null ? null : ParkedQuestions.AdrAskSource;
        var line = ParkedQuestions.ParkedLine(id, at, team, caller.SessionId, null, 0, question, source, assumed);
        if (askedFor is not null)
        {
            line["asked_for"] = askedFor;
        }

        line["class"] = cls;
        if (adr is not null)
        {
            line["adr"] = (double)adr.Value;
        }

        Hier.StateCalls.AppendParkedQuestion(home, line, new StateCallOptions(OperationId: $"park:{id}"));
        // b517: the append is not a write the daemon serves; the refresh is, and the watch model re-reads the home's parked questions on it
        Hier.Status.RefreshStatus(home);
        var parked = new ParkedQuestion(id, at, team, caller.SessionId, null, 0, question, source, assumed);
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

        // ah2-377 P5: the one answer core; who may answer and how the answer was typed stay the verb's
        var refusedExit = 0;
        JsonObject? Prove(ParkedQuestion question)
        {
            var stamp = new JsonObject { ["writer"] = QuestionAnswerWriter, ["by_user"] = byUser };
            if (byUser)
            {
                return stamp;
            }

            // The Orchestrator relays the user's answer only when a typed prompt has reached its own session since the question was parked.
            if (DecisionProof.TeamOf(caller.Class.Detail) != question.Team || caller.SessionId is not { Length: > 0 } callerSession)
            {
                refusedExit = Refuse("answer_not_user", $"Only the user can answer {id}. Run this in a terminal: ah msg decision answer {id} \"<label>\"", "answer");
                return null;
            }

            if (ParkedQuestions.IsTerminalOnly(question))
            {
                refusedExit = Refuse("answer_relay_reserved", $"{id} ({question.Header}) can only be answered by the user. Ask them to run in a terminal: ah msg decision answer {id} \"{(question.Labels.Count > 0 ? question.Labels[0] : "<label>")}\"", "answer");
                return null;
            }

            if (HierarchyModule.RelayPromptId(Hier.Peers.LatestUserPrompt(callerSession), question.At) is not { } promptId)
            {
                refusedExit = Refuse("answer_no_user_prompt", $"No prompt typed by the user has reached this session since {id} was parked, so it cannot be answered for them. Ask them, or have them run: ah msg decision answer {id} \"<label>\"", "answer");
                return null;
            }

            stamp["writer"] = QuestionRelayWriter;
            stamp["relayed_prompt"] = promptId;
            return stamp;
        }

        var now = _graph.Io.Clock.UtcNow;
        string? Notify(string to, string eventName, string text) =>
            _graph.Mailbox.Send(MeshModule.EventRequest(to, "user", _cwd, dir, eventName, text), now).Failure is { } failure ? $"{failure.Code}: {failure.Message}" : null;

        var res = Hier.Decisions.AnswerQuestion(_graph.Io.Files, Hier.StateCalls, Hier.Progress, ChecklistPool, dir, _teamArg, id, Prove, Given, _opts.Text("note"), now, Notify, t => _graph.Roster.ReadTeamRecord(dir, t), null, out var notes);
        if (res is null)
        {
            return refusedExit;
        }

        if (res.Get("refused") is { IsMap: true } refused)
        {
            return Refuse(refused.Member("reason")!.Value.AsString(), refused.Member("detail")!.Value.AsString(), "answer");
        }

        foreach (var n in notes)
        {
            _out.Note("ah msg: decision answer recorded; " + n);
        }

        var chosen = string.Join(", ", res["labels"].AsArray().Select(static l => l.AsString()));
        var result = new JsonObject { ["id"] = id, ["team"] = res["team"], ["answered"] = true, ["labels"] = chosen };
        if (_teamArg is null)
        {
            _out.Note($"ah msg: answered {id} of team {res["team"].AsString()} ({res["header"].AsString()})");
        }
        if (res.Get("relayed_prompt") is { } relayedPrompt)
        {
            result["relayed_prompt"] = relayedPrompt;
        }

        _out.Json(result);
        return 0;
    }

    /// <summary>The answer as the verb was given it, read against <paramref name="question"/>'s options: the labels or option text, and whether it is a free-text answer.</summary>
    private (IReadOnlyList<string> Given, bool FreeText) Given(ParkedQuestion question)
    {
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

        return (given, freeText);
    }
}
