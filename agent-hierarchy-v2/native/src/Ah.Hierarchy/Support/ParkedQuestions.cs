using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Hierarchy;

/// <summary>One question an Orchestrator was not allowed to ask while work was in flight, folded from its lines in the store.</summary>
public sealed class ParkedQuestion(
    string id,
    string at,
    string team,
    string? sessionId,
    string? toolUseId,
    int index,
    JsonValue question,
    string? source,
    string defaultKey,
    bool pushed = false,
    string? answeredAt = null,
    IReadOnlyList<string>? answeredLabels = null)
{
    public string Id { get; } = id;

    public string At { get; } = at;

    public string Team { get; } = team;

    public string? SessionId { get; } = sessionId;

    public string? ToolUseId { get; } = toolUseId;

    public int Index { get; } = index;

    public JsonValue Question { get; } = question;

    public string? Source { get; } = source;

    public string Default { get; } = defaultKey;

    public bool Pushed { get; } = pushed;

    public string? AnsweredAt { get; } = answeredAt;

    public IReadOnlyList<string> AnsweredLabels { get; } = answeredLabels ?? [];

    /// <summary>The member (<c>name@team</c>) the question was parked for, who also gets its answer; null when the Orchestrator alone does.</summary>
    public string? AskedFor { get; init; }

    /// <summary>How urgent the question is: <c>blocker</c>, <c>soon</c> or <c>eventually</c>; null (read as <c>soon</c>) when it was parked with none.</summary>
    public string? Class { get; init; }

    internal ParkedQuestion With(bool? pushed = null, string? answeredAt = null, IReadOnlyList<string>? answeredLabels = null) =>
        new(Id, At, Team, SessionId, ToolUseId, Index, Question, Source, Default, pushed ?? Pushed, answeredAt ?? AnsweredAt, answeredLabels ?? AnsweredLabels) { AskedFor = AskedFor, Class = Class };

    public bool Open => AnsweredAt is null;

    public string Text => Member("question") ?? string.Empty;

    public string Header => Member("header") is { Length: > 0 } header ? header : "Question";

    public IReadOnlyList<string> Labels => Question.Member("options") is { IsArray: true } options
        ? [.. options.AsArray().Select(o => o.IsText ? o.AsString() : o.Member("label") is { IsText: true } l ? l.AsString() : null).OfType<string>()]
        : [];

    private string? Member(string name) => Question.Member(name) is { IsText: true } t ? t.AsString() : null;
}

/// <summary>
/// The store of questions parked for the user: <c>parked-questions.jsonl</c> beside <c>asks.jsonl</c>, append-only, owner-only. A question
/// is one <c>parked</c> line; a later <c>pushed</c> line says the user was told, an <c>answered</c> line says it was answered. The state
/// of a question is the fold of its lines by id. Nothing here grants anything: consent and authority come only from the asks row.
/// </summary>
public static class ParkedQuestions
{
    public const string FileName = "parked-questions.jsonl";

    public static string PathIn(string dir) => dir.TrimEnd('/') + "/" + FileName;

    /// <summary>The fold of the file's lines, in the order the questions were parked. Unreadable lines are skipped.</summary>
    public static IReadOnlyList<ParkedQuestion> Fold(string? text)
    {
        var order = new List<string>();
        var parked = new Dictionary<string, ParkedQuestion>(StringComparer.Ordinal);
        foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonValue row;
            try
            {
                row = JsonText.Parse(line);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
            {
                continue;
            }

            if (!row.IsMap || row.Member("id") is not { IsText: true } idValue)
            {
                continue;
            }

            var id = idValue.AsString();
            switch (row.Member("kind") is { IsText: true } kind ? kind.AsString() : null)
            {
                case "parked" when !parked.ContainsKey(id) && row.Member("question") is { IsMap: true } question:
                    order.Add(id);
                    parked[id] = new ParkedQuestion(
                        id, Text(row, "at") ?? string.Empty, Text(row, "team") ?? string.Empty, Text(row, "session_id"), Text(row, "tool_use_id"),
                        row.Member("index") is { IsNumber: true } index ? (int)index.AsNumber() : 0, question, Text(row, "source"), Text(row, "default") ?? "none")
                    {
                        AskedFor = Text(row, "asked_for"),
                        Class = Text(row, "class"),
                    };
                    break;
                case "pushed" when parked.TryGetValue(id, out var open):
                    parked[id] = open.With(pushed: true);
                    break;
                case "answered" when parked.TryGetValue(id, out var asked) && asked.AnsweredAt is null:
                    parked[id] = asked.With(
                        answeredAt: Text(row, "at") ?? string.Empty,
                        answeredLabels: row.Member("labels") is { IsArray: true } labels ? [.. labels.AsArray().Where(l => l.IsText).Select(l => l.AsString())] : []);
                    break;
            }
        }

        return [.. order.Select(id => parked[id])];
    }

    private static string? Text(JsonValue row, string field) => row.Member(field) is { IsText: true } t ? t.AsString() : null;

    /// <summary>
    /// Whether the question grants authority or consent and so only the user's own terminal answer counts: the Authority bootstrap question
    /// (by its header) or the member-rules consent question (by its source tag). The readers of those answers accept no relayed answer.
    /// </summary>
    public static bool IsTerminalOnly(ParkedQuestion q) =>
        q.Header == AnchorAuthority.QuestionHeader || Ah.Config.ConfigModule.IsMemberLaunchConsentSource(q.Source);

    public static JsonObject ParkedLine(string id, string at, string team, string? sessionId, string? toolUseId, int index, JsonValue question, string? source, string defaultKey) =>
        new()
        {
            ["kind"] = "parked",
            ["id"] = id,
            ["at"] = at,
            ["team"] = team,
            ["session_id"] = sessionId is null ? JsonValue.Null : sessionId,
            ["tool_use_id"] = toolUseId is null ? JsonValue.Null : toolUseId,
            ["index"] = (double)index,
            ["question"] = question,
            ["source"] = source is null ? JsonValue.Null : source,
            ["default"] = defaultKey,
        };

    public static JsonObject PushedLine(string id, string at) => new() { ["kind"] = "pushed", ["id"] = id, ["at"] = at };

    public static JsonObject AnsweredLine(string id, string at, IReadOnlyList<string> labels, string writer, string asksOp, string? via = null, bool freeText = false)
    {
        var array = new JsonArray();
        foreach (var label in labels)
        {
            array.Add(label);
        }

        var line = new JsonObject { ["kind"] = "answered", ["id"] = id, ["at"] = at, ["labels"] = array, ["writer"] = writer, ["asks_op"] = asksOp };
        if (via is not null)
        {
            line["via"] = via;
        }

        if (freeText)
        {
            line["free_text"] = true;
        }

        return line;
    }

    /// <summary>Question text as matched: case-folded, whitespace runs collapsed to one space, trimmed, trailing punctuation stripped.</summary>
    public static string NormaliseText(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var end = collapsed.Length;
        while (end > 0 && char.IsPunctuation(collapsed[end - 1]))
        {
            end--;
        }

        return collapsed[..end].TrimEnd();
    }

    /// <summary>Whether <paramref name="questionText"/> is <paramref name="q"/>'s text after <see cref="NormaliseText"/>; empty text matches nothing.</summary>
    public static bool SameText(ParkedQuestion q, string? questionText) =>
        questionText is { Length: > 0 } && NormaliseText(questionText) is { Length: > 0 } asked && asked == NormaliseText(q.Text);

    /// <summary>The option of <paramref name="q"/> that <paramref name="label"/> names, ignoring case and surrounding space; null when it names none.</summary>
    public static string? MatchLabel(ParkedQuestion q, string label) =>
        q.Labels.FirstOrDefault(o => string.Equals(o.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The open question a dialog question stands for. With a <c>ref=q&lt;k&gt;</c> tag, that question, and only when the dialog question has its
    /// text, or has its option labels as a set that no other open parked question has: a tag cannot carry an answer to another question, and
    /// two questions with the same options cannot be told apart by their options. Without a tag, the open question with the same text. Text
    /// compares after <see cref="NormaliseText"/>; labels compare trimmed and ignoring case. Null when none matches.
    /// </summary>
    public static ParkedQuestion? MatchAsked(IReadOnlyList<ParkedQuestion> folded, string? questionText, IReadOnlyList<string> askedLabels, string? source)
    {
        bool SameText(ParkedQuestion q) => ParkedQuestions.SameText(q, questionText);
        bool SameLabels(ParkedQuestion q) => askedLabels.Count > 0
            && askedLabels.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(q.Labels.Select(l => l.Trim()));
        var refs = (source ?? string.Empty).Split(';').Where(p => p.StartsWith("ref=", StringComparison.Ordinal)).Select(p => p[4..]).ToList();
        if (refs.Count == 0)
        {
            return folded.FirstOrDefault(q => q.Open && SameText(q));
        }

        var named = folded.FirstOrDefault(q => q.Open && refs.Contains(q.Id, StringComparer.Ordinal));
        return named is not null && (SameText(named) || (SameLabels(named) && folded.Count(q => q.Open && SameLabels(q)) == 1)) ? named : null;
    }

    /// <summary>
    /// The options a dialog answer chose for <paramref name="q"/>: the whole answer when it is one option, else its comma-separated parts when
    /// every part is one. Null when any part is not an option, so a free-text reply resolves nothing.
    /// </summary>
    public static IReadOnlyList<string>? ChosenLabels(ParkedQuestion q, string answer)
    {
        if (MatchLabel(q, answer) is { } whole)
        {
            return [whole];
        }

        var parts = answer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var matched = parts.Select(p => MatchLabel(q, p)).ToList();
        return parts.Length > 0 && matched.All(m => m is not null) ? [.. matched.OfType<string>()] : null;
    }

    /// <summary>
    /// Matches the dialog answers recorded in <paramref name="dir"/>'s asks log to the questions still parked there, as the ask-record hook
    /// matches a fresh one: an answer recorded while the store could not be read was matched to nothing (§6c G5), and is matched by the next
    /// readable read. Only a row the hook wrote after the question was parked, and appended after the dir's sweep watermark, counts. With no
    /// watermark yet, a write path (<paramref name="mayMark"/>: a park, the ask-record hook) records one and a read path matches nothing,
    /// so a refused or listing verb writes nothing. Either log unreadable matches nothing. Returns how
    /// many were marked answered.
    /// </summary>
    public static int MatchRecorded(IFileReader files, IHierarchyStateCalls calls, string dir, bool hook = false, bool mayMark = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!files.ReadAskLog(PathIn(dir)).TryLines(out var parkedLines))
        {
            return 0;
        }

        var folded = Fold(string.Join('\n', parkedLines));
        var open = folded.Where(static q => q.Open).ToList();

        // a row answers once: an answered line carrying its at (the live hook's, or an earlier match's) consumed it
        var consumed = folded.Select(static q => q.AnsweredAt).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (!files.ReadAskLog(dir.TrimEnd('/') + "/asks.jsonl").TryLines(out var asks))
        {
            return 0;
        }

        var rows = new List<(JsonValue Row, string At, double Ms, int Index)>();
        for (var i = 0; i < asks.Count; i++)
        {
            try
            {
                var row = JsonText.Parse(asks[i]);
                if (row.Member("at") is { IsText: true } at && Ah.Core.Time.Timestamps.TryParseMs(at.AsString(), out var ms))
                {
                    rows.Add((row, at.AsString(), ms, i));
                }
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
            {
            }
        }

        // the watermark: every row already in the log the first time a sweep ran is taken as consumed, so rows from before the sweep
        // existed are never replayed; the first sweep records how many there were. It is a position in the log, not a time: a row
        // appended later with an older at is still after it
        var mark = parkedLines.Select(static l => l.Contains("\"swept\"", StringComparison.Ordinal) ? MarkOf(l) : null).LastOrDefault(static m => m is not null);
        if (mark is null && !mayMark)
        {
            return 0;
        }

        // at, the newest row's time, is written for a reader of an older build; n is the mark
        var newest = rows.Count == 0 ? 0 : rows.Max(static r => r.Ms);
        void Mark() => calls.AppendParkedQuestion(
            dir,
            new JsonObject { ["kind"] = "swept", ["at"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)newest)), ["n"] = (double)asks.Count },
            new StateCallOptions(OperationId: $"swept:{dir}:n{asks.Count}", Hook: hook));
        if (mark is null)
        {
            mark = asks.Count;
            Mark();
        }

        var matched = 0;
        foreach (var (row, rowAt, rowMs, index) in rows)
        {
            if (open.Count == 0)
            {
                break;
            }

            if (index < mark || consumed.Contains(rowAt) || row.Member(SweepField) is not { IsNumber: true }
                || row.Member("writer") is not { IsText: true } writer || writer.AsString() != Ah.Config.ConfigModule.AskRecordWriter
                || row.Member("questions") is not { IsArray: true } questions)
            {
                continue;
            }

            var source = row.Member("metadata")?.Member("source") is { IsText: true } tag ? tag.AsString() : null;
            var answers = row.Member("response")?.Member("answers");
            foreach (var asked in questions.AsArray())
            {
                var text = asked.Member("question") is { IsText: true } t ? t.AsString() : null;
                var labels = asked.Member("options") is { IsArray: true } options
                    ? options.AsArray().Select(static o => o.IsText ? o.AsString() : o.Member("label") is { IsText: true } l ? l.AsString() : null).OfType<string>().ToList()
                    : [];
                var before = open.Where(q => Ah.Core.Time.Timestamps.TryParseMs(q.At, out var parkedMs) && parkedMs < rowMs).ToList();
                if (MatchAsked(before, text, labels, source) is not { } parked || text is null || answers?.Member(text) is not { IsText: true } answer)
                {
                    continue;
                }

                var chosen = ChosenLabels(parked, answer.AsString());
                var freeText = chosen is null && SameText(parked, text);
                if (chosen is null && !freeText)
                {
                    continue;
                }

                Answer(calls, dir, parked.Id, rowAt, chosen ?? [answer.AsString()], Ah.Config.ConfigModule.AskRecordWriter, "matched:" + parked.Id, hook, "dialog", freeText);
                _ = open.Remove(parked);
                matched++;
            }
        }

        // the mark advances past every row this sweep considered, so the next one rescans only what came after (a write path only)
        if (mayMark && asks.Count > mark)
        {
            Mark();
        }

        return matched;
    }

    /// <summary>
    /// The field the ask-record hook of this build sets on its rows: its answered line carries the row's own <c>at</c>, the key that marks the
    /// row consumed. A row without it (an older build's) is never matched by <see cref="MatchRecorded"/>.
    /// </summary>
    public const string SweepField = "sweep";

    /// <summary>
    /// The watermark a <c>swept</c> line records, how many asks rows were swept; null for any other line. An older build's line carries
    /// only a time: it reads as 0, so every row is considered again and the consumed guard alone keeps an answered one out.
    /// </summary>
    private static int? MarkOf(string line)
    {
        try
        {
            var row = JsonText.Parse(line);
            return row.Member("kind") is { IsText: true } kind && kind.AsString() == "swept"
                ? row.Member("n") is { IsNumber: true } n ? (int)n.AsNumber() : 0
                : null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Marks <paramref name="id"/> answered: the one write both the terminal verb and a dialog answer make.</summary>
    public static void Answer(IHierarchyStateCalls calls, string dir, string id, string at, IReadOnlyList<string> chosen, string writer, string asksOp, bool hook = false, string? via = null, bool freeText = false) =>
        calls.AppendParkedQuestion(dir, AnsweredLine(id, at, chosen, writer, asksOp, via, freeText), new StateCallOptions(OperationId: $"answered:{id}", Hook: hook));

    public static string DefaultText(string key) => key switch
    {
        "authority" => "members ask you through the orchestrator (authority: user).",
        "decisions" => "no Ultra-Advisor; a top team member decides, else a fresh subagent on the orchestrator's model.",
        "ultra-advisor" => "no Ultra-Advisor is spawned.",
        "codex-trust" => "Codex members are not trusted and are not launched.",
        "auto-merge" => "no auto-merge.",
        "member-launch" => "member launch rules are not widened (Not now).",
        "issue-scope" => "nothing is pushed and no PR is opened.",
        "reserved" => "that action is not taken.",
        "none" or "" => "nothing is assumed; that step waits.",
        _ => key,
    };

    /// <summary>The next free <c>q&lt;k&gt;</c> id of a store.</summary>
    public static string NextId(IReadOnlyList<ParkedQuestion> folded) =>
        "q" + (folded.Select(static q => int.TryParse(q.Id.AsSpan(1), out var k) ? k : 0).DefaultIfEmpty(0).Max() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private const int PushQuestionChars = 120;

    private const string PushToolName = "PushNotification ";

    /// <summary>The push the Orchestrator is told to send for a parked question.</summary>
    public static string PushLine(ParkedQuestion q)
    {
        var text = q.Text.Length <= PushQuestionChars ? q.Text : q.Text[..PushQuestionChars];
        var labels = q.Labels;
        return $"{PushToolName}\"{q.Header}: {text} Options: {string.Join(" | ", labels)}. Answer in a terminal: ah msg decision answer {q.Id} \\\"{(labels.Count > 0 ? labels[0] : "<label>")}\\\"\"";
    }

    /// <summary>What a Stop says about a parked question that was never pushed.</summary>
    public static string PushNudgeReason(ParkedQuestion q) => $"{q.Id} is parked but not pushed. Send: {PushLine(q)}";

    /// <summary>
    /// The deny reason for a call whose questions were parked: one block per question, then the closing line. With
    /// <paramref name="awayMinutes"/> the cause is the user's absence (they have not typed for that long); without it, work in flight. A
    /// question that was already parked and pushed is named, not pushed again.
    /// </summary>
    public static string ParkedReason(IReadOnlyList<ParkedQuestion> parked, int? awayMinutes = null) =>
        string.Join(
            "\n",
            parked.SelectMany(q => new[]
            {
                awayMinutes is { } n
                    ? $"Parked as {q.Id} for the user: they have not typed in this session for over {n} minutes, so a dialog here would wait with nobody there."
                    : $"Parked as {q.Id} for the user: work is in flight, and a question here would hold the whole team.",
                q.Pushed ? $"{q.Id} is already parked and pushed; do not push it again." : "Send ONE PushNotification now: " + PushLine(q)[PushToolName.Length..],
                "Until it is answered: " + DefaultText(q.Default),
            }).Append(awayMinutes is null
                ? "Then carry on with the other work."
                : parked.FirstOrDefault(static q => !IsTerminalOnly(q)) is { } relayable
                    ? $"Then carry on with other work. Do not ask it again until the user has typed. Their next typed message can answer it (relay: ah msg decision answer {relayable.Id} \"<label>\"), or a later AskUserQuestion with ref={relayable.Id} once they are back."
                    : $"Then carry on with other work. Do not ask it again until the user has typed. Only the user can answer it, from a terminal (ah msg decision answer {parked[0].Id} \"<label>\"), or a later AskUserQuestion with ref={parked[0].Id} once they are back."));
}
