using System.Globalization;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>A checklist item key (<c>&lt;stream&gt;:&lt;item&gt;</c>); a d&lt;k&gt; or q&lt;k&gt; never holds a colon.</summary>
    private static bool IsItemKey(string? id) => id is not null && id.Contains(':', StringComparison.Ordinal);

    private string TeamFlag => _teamArg is null ? string.Empty : $" --team {_teamArg}";

    /// <summary>
    /// <c>decision list --waiting</c>: everything waiting on the user for the team, each with the command that answers it. Decisions and items come
    /// through <see cref="HierarchyModule.ChecklistWaiting"/>, the fold the web list reads; the open q&lt;k&gt; rows are added here only.
    /// </summary>
    private int DecisionWaiting(string dir)
    {
        var now = _graph.Io.Clock.UtcNow;
        var open = new List<JsonValue>();
        var anchor = Hier.Decisions.OpenRunAnchor(dir, _teamArg is null ? JsonValue.Null : _teamArg);
        if (anchor.Get("error") is not { IsTruthy: true })
        {
            var lines = Hier.Decisions.ReadDecisions(Hier.Decisions.DecisionLogPath(dir, JsConvert.ToJsString(anchor["id"])))["decisions"].AsArray();
            var answered = lines.Where(l => FrontText(l.Member("kind")) == "answered").Select(l => FrontText(l.Member("answers"))).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            open.AddRange(lines.Where(l => FrontText(l.Member("kind")) == "parked" && FrontText(l.Member("id")) is { } id && !answered.Contains(id) && seen.Add(id)));
        }

        var entries = HierarchyModule.ChecklistEntries(Hier.Progress, ChecklistPool, HierarchyModule.ProgressFolderOf(_teamArg));
        var list = HierarchyModule.ChecklistWaiting(open, static d => FrontText(d.Member("item")), entries, static e => e.Item.State, static e => e.Item.Item);

        var rows = new JsonArray();
        foreach (var d in list.Decisions)
        {
            var id = FrontText(d.Member("id"))!;
            var options = d.Member("options") is { IsArray: true } o ? o.AsArray() : new JsonArray();
            var letter = FrontText(d.Member("default"));
            rows.Add(WaitingRow("decision", id, FrontText(d.Member("question")) ?? string.Empty, options, letter, FrontText(d.Member("time")), $"ah msg decision answer {id} --choice {letter ?? "a"}{TeamFlag}"));
        }

        foreach (var e in list.Items)
        {
            var key = e.Stream + ":" + e.Item.Item;
            var options = new JsonArray();
            options.AddRange((e.Item.Options ?? []).Select(static l => (JsonValue)l));
            var command = options.Count > 0 ? $"ah msg decision answer {key} --choice a{TeamFlag}" : $"ah msg decision answer {key} --choice \"other: <answer>\"{TeamFlag}";
            rows.Add(WaitingRow("item", key, e.Item.Note ?? HierarchyModule.ChecklistTitle(e.Item.Title), options, null, e.Item.StateSince, command));
        }

        // read as QuestionAnswer reads the store, so every listed q<k> is one that verb can answer
        var path = ParkedQuestions.PathIn(dir);
        var files = _graph.Io.Files;
        _ = ParkedQuestions.MatchRecorded(files, Hier.StateCalls, dir);
        // per-dir-files-to-db §6c G5: one read; a store that cannot be read lists none
        foreach (var q in ParkedQuestions.Fold(files.ReadAskLog(path).TryLines(out var lines) ? string.Join('\n', lines) : string.Empty).Where(q => q.Open))
        {
            var options = new JsonArray();
            options.AddRange(q.Labels.Select(static l => (JsonValue)l));
            var row = WaitingRow("question", q.Id, q.Header, options, null, q.At, $"ah msg decision answer {q.Id} \"{(q.Labels.Count > 0 ? q.Labels[0] : "<label>")}\"{TeamFlag}");
            row["default"] = ParkedQuestions.DefaultText(q.Default);
            rows.Add(row);
        }

        if (_opts.Has("json"))
        {
            _out.Json(new JsonObject { ["waiting"] = rows, ["total"] = (double)rows.Count });
            return 0;
        }

        if (rows.Count == 0)
        {
            _out.Text("nothing waits on you");
            return 0;
        }

        foreach (var row in rows)
        {
            _out.Text(WaitingLine(row.AsObject(), now));
        }

        return 0;
    }

    private static JsonObject WaitingRow(string kind, string id, string text, JsonArray options, string? defaultLetter, string? since, string command)
    {
        var row = new JsonObject { ["kind"] = kind, ["id"] = id, ["text"] = text, ["options"] = options };
        if (defaultLetter is not null)
        {
            row["default"] = defaultLetter;
        }

        row["since"] = since is null ? JsonValue.Null : since;
        row["answer_command"] = command;
        return row;
    }

    /// <summary>One plain line: kind, id, text, the lettered options with the default starred, the age, and the answer command.</summary>
    private static string WaitingLine(JsonObject row, DateTimeOffset now)
    {
        var options = row["options"].AsArray();
        var letter = row.Get("default") is { IsText: true } d && d.AsString() is { Length: 1 } l ? l : null;
        var listed = string.Join(", ", options.Select((o, i) => $"{(char)('a' + i)}) {o.AsString()}{(letter == ((char)('a' + i)).ToString() ? "*" : string.Empty)}"));
        var assumed = row["kind"].AsString() == "question" && row.Get("default") is { IsText: true } text ? $" | default: {text.AsString()}" : string.Empty;
        var age = row["since"] is { IsText: true } since && DateTimeOffset.TryParse(since.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? Age(now - at)
            : "?";
        return $"{row["kind"].AsString()} {row["id"].AsString()} | {row["text"].AsString()} | {(listed.Length > 0 ? listed : "no options")}{assumed} | {age} | {row["answer_command"].AsString()}";
    }

    private static string Age(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{Math.Max(0, (int)span.TotalMinutes)}m" : span.TotalHours < 48 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalDays}d";

    /// <summary>
    /// <c>decision answer &lt;stream&gt;:&lt;item&gt; (--choice &lt;a|b|c|d|other:text&gt; | "&lt;label&gt;")</c>: the user's answer to a waiting-user item from a terminal,
    /// written by <see cref="HierarchyModule.ChecklistAnswer"/>, the write the web route uses. The caller is proved as for a d&lt;k&gt; answer.
    /// </summary>
    private int ItemAnswer(string dir)
    {
        var key = _opts.Positional[1];
        var label = string.Join(' ', _opts.Positional.Skip(2)).Trim();
        if (_opts.Text("choice") is null && label.Length == 0)
        {
            throw _out.Fail("usage: ah msg decision answer <stream>:<item> (--choice <a|b|c|d|other:<text>> | \"<label>\") [--since <asked at>] [--team <name>]");
        }

        var run = TeamDecisionRun();
        if (ProveDecisionCaller(DecisionAction.Answer, dir, run, key, _opts.Text("choice") ?? label, Hier.Decisions.DecisionLogPath(dir, run), out var proofExit) is null)
        {
            return proofExit;
        }

        var entry = HierarchyModule.ChecklistEntries(Hier.Progress, ChecklistPool, HierarchyModule.ProgressFolderOf(_teamArg)).FirstOrDefault(e => e.Stream + ":" + e.Item.Item == key);
        var choice = _opts.Text("choice");
        if (choice is null)
        {
            var options = entry?.Item.Options ?? [];
            var at = options.ToList().FindIndex(o => string.Equals(o, label, StringComparison.OrdinalIgnoreCase));
            if (at < 0 && options.Count > 0)
            {
                return Refuse("unmatched_label", $"\"{label}\" is not an option for {key}. Options: {string.Join(" | ", options)}.", "answer");
            }

            choice = at >= 0 ? ((char)('a' + at)).ToString() : "other:" + label;
        }

        string? Notify(string to, string eventName, string text)
        {
            var sent = _graph.Mailbox.Send(Ah.Mesh.MeshModule.EventRequest(to, "user", _cwd, dir, eventName, text), _graph.Io.Clock.UtcNow);
            return sent.Failure is { } failure ? $"{failure.Code}: {failure.Message}" : null;
        }

        // without --since the answer is to the question as it stands now
        var since = _opts.Text("since") ?? entry?.Item.StateSince ?? string.Empty;
        var res = HierarchyModule.ChecklistAnswer(Hier.Progress, ChecklistPool, _teamArg ?? "default", key, since, choice, _graph.Io.Clock.UtcNow, Notify, team => _graph.Roster.ReadTeamRecord(dir, team));
        if (res.Get("refused") is { IsMap: true } refusal)
        {
            return Refuse(JsConvert.ToJsString(refusal.Member("reason") ?? JsonValue.Null), JsConvert.ToJsString(refusal.Member("detail") ?? JsonValue.Null), "answer");
        }

        if (res.Get("bad_request") is { IsText: true } bad)
        {
            return Refuse("bad_request", bad.AsString(), "answer");
        }

        if (res.Get("event_error") is { IsText: true } eventError)
        {
            _out.Note($"ah msg: decision answer recorded; the question-answered event was not sent: {eventError.AsString()}");
        }

        if (res.Get("asker_event_error") is { IsText: true } askerError)
        {
            _out.Note($"ah msg: decision answer recorded; the question-answered event to the asker was not sent: {askerError.AsString()}");
        }

        res["recorded_by"] = "cli";
        _out.Json(res);
        return 0;
    }
}
