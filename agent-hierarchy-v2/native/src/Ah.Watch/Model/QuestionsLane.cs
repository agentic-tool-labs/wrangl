using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The questions of a team's open pipeline run, <c>questions:&lt;dirId&gt;/&lt;team&gt;</c>: <c>{run?, open:[…], recent:[…], open_total, truncated, waiting_total, items:[…]}</c>.
/// It is composed only while a client holds the slice. A re-read is one <c>decisions.pipeline-run</c> read; the same read fills the rows of
/// <c>list:questions:&lt;dirId&gt;/&lt;team&gt;</c> (<see cref="QuestionsPager"/>). A team with no open run has an empty body.
/// </summary>
/// <para>Beside the run's questions it carries what waits on the user by the one fold, with or without a run: <c>items[]</c>, the items no open decision names, and <c>waiting_total</c>,
/// the fold's count (spec ah-ui-answer §2). <paramref name="waiting"/> reads the fold for a (dir, folder); null, or no team, is nothing waiting beyond the run's open questions.</para>
internal sealed class QuestionsLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<string, string, Ah.Hierarchy.ChecklistWaitingList<JsonValue, JsonValue>?>? waiting = null, Func<string, string, string, IReadOnlyDictionary<string, WorkCard>>? work = null) : IModelLane, ILeaseLane
{
    public const string LaneName = "questions";

    public const int OpenCap = 10;

    public const int ItemCap = 20;

    public const int RecentCap = 10;

    // The cuts are UTF-8 bytes, because the slice's budget is bytes (QuestionsLaneTests holds the worst case under 10240).
    public const int OpenTextBytes = 200;

    public const int RecentTextBytes = 100;

    public const int LabelBytes = 24;

    public const int OptionCap = 4;

    public const int FieldBytes = 40;

    public const int AnswerBytes = 48;

    /// <summary>How much of each question-log reply a list row carries; <c>cut</c> marks a row with a longer one.</summary>
    public const int ReplyHeadBytes = 2048;

    /// <summary>The slice body's byte budget; the cuts above keep the rows within it, and <c>dir</c> is dropped when it would not fit.</summary>
    public const int SliceBudget = 10240;

    private const string Head = LaneName + ":";

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    // The list rows of each held suffix, newest first, complete before they are stored, so the pager reads them from a request thread.
    private readonly ConcurrentDictionary<string, IReadOnlyList<KeyedRow>> _rows = new(StringComparer.Ordinal);

    private long _malformed;

    public string Name => LaneName;

    /// <summary>How many decision-log rows the lane skipped as malformed (not an object, or no <c>kind</c>/<c>id</c>), over every read.</summary>
    public long Malformed => Interlocked.Read(ref _malformed);

    private static string SliceIdOf(string dir, string folder) => Head + ProgressLane.SuffixOf(dir, folder);

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, folder) = ProgressLane.Split(key);
        return [SliceIdOf(dir, folder)];
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _held];
        }
    }

    /// <summary>The held keys whose directory is <paramref name="dir"/>.</summary>
    public IReadOnlyList<string> HeldOfDir(string dir) => [.. Keys().Where(k => ProgressLane.Split(k).Dir == dir)];

    /// <summary>
    /// Asks <paramref name="emit"/> to re-read every held team of <paramref name="dir"/>: called when the dir's decisions log or its
    /// exchanges (a run anchor opening or closing) were re-read.
    /// </summary>
    public void EmitHeldOfDir(string dir, Action<string, string> emit)
    {
        foreach (var held in HeldOfDir(dir))
        {
            emit(LaneName, held);
        }
    }

    /// <summary>The list rows of the suffix <c>&lt;dirId&gt;/&lt;team&gt;</c>, newest first; null when no client holds its slice.</summary>
    public IReadOnlyList<KeyedRow>? RowsOf(string suffix) => _rows.GetValueOrDefault(suffix);

    public string? KeyOfToken(string token)
    {
        if (!token.StartsWith(Head, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = token[Head.Length..].Split('/');
        if (parts.Length != 2 || parts.Any(static p => p.Length == 0))
        {
            return null;
        }

        var dir = WatchModule.DirOfId(parts[0], canonicalDirs());
        return dir is null ? null : ProgressLane.KeyOf(dir, parts[1]);
    }

    public IReadOnlyList<string> LapsedKeys() => [.. Keys().Where(k => !subscribed(SliceIds(k)[0]))];

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, folder) = ProgressLane.Split(key);
        var sliceId = SliceIdOf(dir, folder);
        var suffix = ProgressLane.SuffixOf(dir, folder);
        if (!subscribed(sliceId))
        {
            lock (_gate)
            {
                _ = _held.Remove(key);
            }

            _ = _rows.TryRemove(suffix, out _);
            return new LaneResult([]) { Evict = [sliceId] };
        }

        lock (_gate)
        {
            _ = _held.Add(key);
        }

        var resolved = canonicalDirs().Contains(dir, StringComparer.Ordinal);
        var run = resolved
            ? reader.Read("decisions", "pipeline-run", dir, new JsonObject { ["dir"] = dir, ["team"] = ProgressLane.TeamNameOf(folder) })
            : JsonValue.Null;
        var team = ProgressLane.TeamNameOf(folder);
        var qa = resolved && reader.Read("decisions", "qa-log", dir, new JsonObject { ["dir"] = dir }).Member("rows") is { IsArray: true } qaRows
            ? Ah.Hierarchy.HierarchyModule.QaThreads([.. qaRows.AsArray().Where(r => LaneParts.Text(r, "team") is not { } t || t == team)])
            : [];
        // one registry read per stream that has a waiting item, so a card can lease its item-log: slice for the description
        var cards = new Dictionary<string, IReadOnlyDictionary<string, WorkCard>>(StringComparer.Ordinal);
        string? WorkId(string stream, string key) =>
            work is null ? null : (cards.TryGetValue(stream, out var c) ? c : cards[stream] = work(dir, folder, stream)).GetValueOrDefault(key)?.Id;
        var fold = Fold(run, resolved ? dir : null, resolved ? waiting?.Invoke(dir, folder) : null, qa, WorkId);
        _ = Interlocked.Add(ref _malformed, fold.Malformed);
        _rows[suffix] = fold.Rows;
        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(fold.Body), 0), 0)]);
    }

    internal sealed record Folded(JsonObject Body, IReadOnlyList<KeyedRow> Rows, int Malformed);

    private sealed record Parked(int Position, JsonValue Row, long At);

    /// <summary>
    /// Folds a <c>decisions.pipeline-run</c> reply into the slice body and the list rows (newest first). <paramref name="dir"/>, the
    /// canonical hier dir the key resolved to, is written beside <c>run</c> so a front end can run the answer verb there. <paramref name="waiting"/> fills <c>items[]</c> and <c>waiting_total</c>.
    /// </summary>
    internal static Folded Fold(JsonValue run, string? dir = null, Ah.Hierarchy.ChecklistWaitingList<JsonValue, JsonValue>? waiting = null, IReadOnlyList<Ah.Hierarchy.QaThread>? qa = null, Func<string, string, string?>? workId = null)
    {
        qa ??= [];
        var runId = run.IsMap ? LaneParts.Text(run, "run_id") : null;
        var rows = run.IsMap && run.Member("rows") is { IsArray: true } r ? r.AsArray() : new JsonArray();
        var parked = new Dictionary<string, Parked>(StringComparer.Ordinal);
        var answers = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var decided = new List<(int Position, JsonValue Row)>();
        var malformed = 0;
        var position = 0;
        foreach (var row in rows)
        {
            position++;
            var kind = row.IsMap ? LaneParts.Text(row, "kind") : null;
            if (kind is null)
            {
                malformed++;
                continue;
            }

            if (kind is not ("parked" or "answered" or "decided"))
            {
                continue;
            }

            if (LaneParts.Text(row, "id") is not { Length: > 0 } id)
            {
                malformed++;
                continue;
            }

            switch (kind)
            {
                case "parked":
                    _ = parked.TryAdd(id, new Parked(position, row, LaneParts.Ms(LaneParts.Text(row, "time"))));
                    break;
                case "answered":
                    if (LaneParts.Text(row, "answers") is { Length: > 0 } names)
                    {
                        _ = answers.TryAdd(names, row);
                    }

                    break;
                default:
                    decided.Add((position, row));
                    break;
            }
        }

        // b517: the open q<k> questions parked for the user, which no run's log holds (the fold has already dropped one the run lists by id)
        foreach (var q in (waiting?.Decisions ?? []).Where(static d => LaneParts.Text(d, "source") == ParkedLane.Source))
        {
            if (LaneParts.Text(q, "id") is { Length: > 0 } id)
            {
                _ = parked.TryAdd(id, new Parked(++position, q, LaneParts.Ms(LaneParts.Text(q, "time"))));
            }
        }

        // An answer naming no parked id is ignored.
        var open = parked.Values.Where(p => !answers.ContainsKey(LaneParts.Text(p.Row, "id")!))
            .OrderBy(static p => Ah.Hierarchy.HierarchyModule.ChecklistClassRank(LaneParts.Text(p.Row, "class"))).ThenBy(static p => p.At == 0 ? 1 : 0).ThenBy(static p => p.At).ThenBy(static p => p.Position).ToList();
        var answered = parked.Values.Where(p => answers.ContainsKey(LaneParts.Text(p.Row, "id")!))
            .Select(p => (Parked: p, Answer: answers[LaneParts.Text(p.Row, "id")!]))
            .Select(static a => (a.Parked, a.Answer, At: LaneParts.Ms(LaneParts.Text(a.Answer, "time"))))
            .OrderBy(static a => a.At == 0 ? 1 : 0).ThenByDescending(static a => a.At).ThenByDescending(static a => a.Parked.Position).ToList();

        var body = new JsonObject();
        if (runId is not null)
        {
            body["run"] = runId;
        }

        // the herdr mod reads a whole thread with `ah msg history --dir`, so a team with log rows names its dir too
        if (dir is not null && (runId is not null || qa.Count > 0 || open.Count > 0))
        {
            body["dir"] = dir;
        }

        var openRows = new JsonArray();
        foreach (var p in open.Take(OpenCap))
        {
            var o = new JsonObject { ["id"] = LaneParts.Text(p.Row, "id")! };
            SetMs(o, "at", p.At);
            // a parked question (q<k>) names its asker in asked_for and its header in header; a run's row has neither
            SetCut(o, "asker", LaneParts.Text(p.Row, "caller") ?? LaneParts.Text(p.Row, "asked_for"), FieldBytes);
            SetCut(o, "source", LaneParts.Text(p.Row, "source"), FieldBytes);
            SetCut(o, "header", LaneParts.Text(p.Row, "item") ?? LaneParts.Text(p.Row, "header"), FieldBytes);
            o["text"] = Cut(LaneParts.Text(p.Row, "question") ?? "", OpenTextBytes);
            var labels = Labels(p.Row).ToList();
            var options = new JsonArray();
            var recommended = new JsonArray();
            foreach (var label in labels.Take(OptionCap))
            {
                // strip, then cut: the cut would swallow the mark
                var (shown, marked) = Recommendation(label);
                if (marked)
                {
                    recommended.Add(options.Count);
                }

                options.Add(new JsonObject { ["label"] = Cut(shown, LabelBytes) });
            }

            o["options"] = options;
            SetRecommended(o, recommended);
            if (labels.Count > OptionCap)
            {
                o["options_total"] = labels.Count;
            }

            // stripped like the labels, so a default naming a marked option still names its label
            SetCut(o, "default", LaneParts.Text(p.Row, "default") is { } def ? Recommendation(def).Shown : null, FieldBytes);
            SetMulti(o, p.Row);
            // only when the parked row names one; a front end reads none as soon
            SetCut(o, "class", LaneParts.Text(p.Row, "class"), FieldBytes);
            // ah2-387 T1: answered only in a terminal; absent on every other card
            if (p.Row.Member("terminal_only") is { IsBool: true } terminalOnly && terminalOnly.AsBool())
            {
                o["terminal_only"] = true;
            }

            openRows.Add(o);
        }

        var recent = new List<(long At, JsonObject Row)>();
        foreach (var a in answered.Take(RecentCap))
        {
            var o = new JsonObject { ["id"] = LaneParts.Text(a.Parked.Row, "id")!, ["text"] = Cut(LaneParts.Text(a.Parked.Row, "question") ?? "", RecentTextBytes) };
            SetCut(o, "header", LaneParts.Text(a.Parked.Row, "item"), FieldBytes);
            SetCut(o, "answer", AnswerOf(a.Answer), AnswerBytes);
            SetCut(o, "by", LaneParts.Text(a.Answer, "answered_by"), FieldBytes);
            SetMs(o, "at", a.At);
            recent.Add((a.At, o));
        }

        // the question log's threads (spec qa-history-all §6): a prompt head only, the full text is the pager's; unprompted replies are not History
        var qaShown = qa.Where(static t => t.Prompt is not null || t.Ask is not null).ToList();
        foreach (var t in qaShown.Take(RecentCap))
        {
            var o = QaRow(t);
            o["text"] = Cut(LaneParts.Text(o, "text") ?? "", RecentTextBytes);
            SetCut(o, "header", LaneParts.Text(o, "header"), FieldBytes);
            SetCut(o, "answer", LaneParts.Text(o, "answer"), AnswerBytes);
            // the list row's one-line preview: the head of the first reply; the whole reply and the later ones are the pager's
            var head = LaneParts.Text(o, "reply");
            _ = o.Remove("reply");
            _ = o.Remove("later");
            SetCut(o, "reply", head, RecentTextBytes);
            recent.Add(((long)t.At, o));
        }

        // stable, so the answered rows keep their order among themselves; an unknown time sorts last
        var shownRecent = recent.OrderBy(static e => e.At == 0 ? 1 : 0).ThenByDescending(static e => e.At).Take(RecentCap).Select(static e => e.Row).ToList();
        JsonArray RecentRows()
        {
            var rows = new JsonArray();
            rows.AddRange(shownRecent.Select(static shown => JsonValue.FromObject(shown)));
            return rows;
        }

        var recentRows = RecentRows();

        body["open"] = openRows;
        body["recent"] = recentRows;
        body["open_total"] = open.Count;
        body["truncated"] = open.Count > OpenCap || answered.Count + qaShown.Count > RecentCap;
        body["waiting_total"] = waiting?.Count ?? open.Count;
        body["items"] = new JsonArray();

        // the oldest first, so the next row appears when one clears; budget left for dir, which is dropped only as today
        var itemRows = new JsonArray();
        var waitingItems = (waiting?.Items ?? []).OrderBy(static i => Ah.Hierarchy.HierarchyModule.ChecklistClassRank(LaneParts.Text(i, "class")))
            .ThenBy(static i => LaneParts.Text(i, "since") ?? string.Empty, StringComparer.Ordinal).ToList();
        var wanted = waitingItems.Take(ItemCap).Select(i => ItemRow(i, workId)).ToList();
        var used = JsonText.ToUtf8(JsonValue.FromObject(body), 0).Length;
        // The reply previews are the first thing to go: a waiting item, or the dir, is never dropped to keep one.
        if (used + wanted.Sum(static w => JsonText.ToUtf8(w, 0).Length + 1) > SliceBudget && shownRecent.Any(static shown => shown.ContainsKey("reply")))
        {
            foreach (var shown in shownRecent)
            {
                _ = shown.Remove("reply");
            }

            body["recent"] = RecentRows();
            used = JsonText.ToUtf8(JsonValue.FromObject(body), 0).Length;
        }

        foreach (var row in wanted)
        {
            var bytes = JsonText.ToUtf8(row, 0).Length + 1;
            if (used + bytes > SliceBudget)
            {
                break;
            }

            used += bytes;
            itemRows.Add(row);
        }

        body["items"] = itemRows;
        body["truncated"] = body["truncated"].AsBool() || itemRows.Count < waitingItems.Count;

        // dir is not cut: when it would take the slice past its budget it is left out, never shortened
        if (body.ContainsKey("dir") && JsonText.ToUtf8(JsonValue.FromObject(body), 0).Length > SliceBudget)
        {
            _ = body.Remove("dir");
        }

        var list = new List<(long At, int Position, JsonValue Row)>();
        foreach (var p in parked.Values)
        {
            var o = ListRow(LaneParts.Text(p.Row, "id")!, "to_you", p.At, p.Row, runId);
            SetMulti(o, p.Row);
            if (answers.TryGetValue(LaneParts.Text(p.Row, "id")!, out var answer))
            {
                if (answer.Member("choices") is { IsArray: true } choices)
                {
                    o["choices"] = choices;
                }

                SetText(o, "answer", AnswerShown(p.Row, AnswerOf(answer)));
                SetText(o, "by", LaneParts.Text(answer, "answered_by"));
                SetMs(o, "answered_at", LaneParts.Ms(LaneParts.Text(answer, "time")));
                if (LaneParts.Text(answer, "answered_by") == "surrogate")
                {
                    SetText(o, "class", LaneParts.Text(answer, "surrogate_class"));
                }
            }

            list.Add((p.At, p.Position, o));
        }

        foreach (var (pos, row) in decided)
        {
            var at = LaneParts.Ms(LaneParts.Text(row, "time"));
            var o = ListRow(LaneParts.Text(row, "id")!, "decided", at, row, runId);
            SetText(o, "answer", AnswerShown(row, AnswerOf(row)));
            SetText(o, "by", DeciderOf(row));
            SetText(o, "rationale", LaneParts.Text(row, "rationale"));
            list.Add((at, pos, o));
        }

        // after every decision-log position, so a log thread's cursor never collides with a decision's
        var qaPosition = position + 1;
        foreach (var t in qa)
        {
            list.Add(((long)t.At, qaPosition++, QaRow(t)));
        }

        var keyed = list.OrderByDescending(static l => l.At).ThenByDescending(static l => l.Position)
            .Select(static l => new KeyedRow([l.At.ToString(CultureInfo.InvariantCulture), l.Position.ToString(CultureInfo.InvariantCulture)], l.Row)).ToList();
        return new Folded(body, keyed, malformed);
    }

    /// <summary>
    /// One <c>items[]</c> row. Its question and options come whole: the plan row caps them, and the answer's letters name them. <c>work_id</c> names the
    /// registered item, whose description a card reads from its <c>item-log:</c> slice (too big for this slice's budget); null when not registered.
    /// </summary>
    private static JsonValue ItemRow(JsonValue item, Func<string, string, string?>? workId)
    {
        var options = new JsonArray();
        var recommended = new JsonArray();
        foreach (var option in item.Member("options") is { IsArray: true } listed ? listed.AsArray() : new JsonArray())
        {
            if (!option.IsText)
            {
                options.Add(option);
                continue;
            }

            var (shown, marked) = Recommendation(option.AsString());
            if (marked)
            {
                recommended.Add(options.Count);
            }

            options.Add(shown);
        }

        var row = new JsonObject
        {
            ["key"] = LaneParts.TextOrNull(item, "key"),
            ["item"] = LaneParts.TextOrNull(item, "item"),
            ["stream"] = LaneParts.TextOrNull(item, "stream"),
            ["title"] = LaneParts.Text(item, "title") is { } title ? Ah.Hierarchy.HierarchyModule.ChecklistTitle(title) : JsonValue.Null,
            ["text"] = LaneParts.TextOrNull(item, "note"),
            ["options"] = options,
            ["since"] = LaneParts.TextOrNull(item, "since"),
            ["owner"] = LaneParts.TextOrNull(item, "owner"),
            ["asker"] = LaneParts.TextOrNull(item, "asker"),
            ["class"] = Ah.Hierarchy.HierarchyModule.ChecklistClassOf(LaneParts.Text(item, "class")),
            ["work_id"] = LaneParts.Text(item, "stream") is { } stream && LaneParts.Text(item, "key") is { } key && workId?.Invoke(stream, key) is { } id ? id : JsonValue.Null,
        };
        SetRecommended(row, recommended);
        return JsonValue.FromObject(row);
    }

    /// <summary>
    /// One question-log thread as a list row, whole: <c>dir</c> is <c>jim</c> (a typed prompt), <c>terminal</c> (an AskUserQuestion dialog) or
    /// <c>unprompted</c>; <c>reply</c> is the first reply of the prompt's own turn and <c>later</c> every other one, oldest first.
    /// </summary>
    private static JsonObject QaRow(Ah.Hierarchy.QaThread t)
    {
        var head = t.Prompt ?? t.Ask ?? t.Replies[0];
        var o = new JsonObject { ["id"] = LaneParts.Text(head, "id") ?? "", ["dir"] = t.Prompt is not null ? "jim" : t.Ask is not null ? "terminal" : "unprompted" };
        SetMs(o, "at", (long)t.At);
        if (t.Ask is { } ask)
        {
            var questions = ask.Member("questions") is { IsArray: true } qs ? qs.AsArray() : new JsonArray();
            var answers = ask.Member("answers");
            SetText(o, "header", string.Join(" · ", questions.Select(static q => LaneParts.Text(q, "header")).OfType<string>()));
            o["text"] = string.Join("\n", questions.Select(static q => LaneParts.Text(q, "question") ?? ""));
            SetText(o, "answer", string.Join("\n", questions.Select(q => answers?.Member(LaneParts.Text(q, "question") ?? "") is { IsText: true } a ? a.AsString() : "").Where(static a => a.Length > 0)));
            return o;
        }

        o["text"] = t.Prompt is { } p ? LaneParts.Text(p, "text") ?? "" : "";
        if (t.Prompt?.Member("question") is { IsBool: true } question && question.AsBool())
        {
            o["question"] = true;
        }

        // the first reply (spec qa-history-replies §4.7): the direct row, else the first typed turn row's final, else its whole text
        var first = t.Prompt is null ? null
            : t.Replies.FirstOrDefault(static r => LaneParts.Text(r, "part") == "direct") is { IsMap: true } direct ? direct
            : t.Replies.FirstOrDefault(static r => LaneParts.Text(r, "trigger") == "typed") is { IsMap: true } typed ? typed : (JsonValue?)null;
        // replies are held and paged as heads; the whole thread is read on demand (list:qa-thread, QaThreadPager)
        var cut = false;
        string Head(string? text)
        {
            var head = Cut(text ?? "", ReplyHeadBytes);
            cut |= head.Length != (text ?? "").Length;
            return head;
        }

        if (first is { } reply)
        {
            SetText(o, "reply", Head(LaneParts.Text(reply, "part") == "direct" ? LaneParts.Text(reply, "text") : LaneParts.Text(reply, "final") ?? LaneParts.Text(reply, "text")));
            SetMs(o, "reply_at", reply.Member("at") is { IsNumber: true } ra ? (long)ra.AsNumber() : 0);
        }

        var later = new JsonArray();
        foreach (var r in t.Replies.Where(r => first is not { } f || LaneParts.Text(r, "id") != LaneParts.Text(f, "id")))
        {
            var l = new JsonObject { ["text"] = Head(LaneParts.Text(r, "text")) };
            SetMs(l, "at", r.Member("at") is { IsNumber: true } la ? (long)la.AsNumber() : 0);
            later.Add(l);
        }

        if (later.Count > 0)
        {
            o["later"] = later;
        }

        o["replies"] = t.Replies.Count;
        if (cut)
        {
            o["cut"] = true;
        }

        return o;
    }

    private static JsonObject ListRow(string id, string direction, long at, JsonValue row, string? runId)
    {
        var o = new JsonObject { ["id"] = id, ["dir"] = direction };
        SetMs(o, "at", at);
        SetText(o, "header", LaneParts.Text(row, "item") ?? LaneParts.Text(row, "header"));
        o["text"] = LaneParts.Text(row, "question") ?? "";
        var options = new JsonArray();
        var recommended = new JsonArray();
        foreach (var label in Labels(row))
        {
            var (shown, marked) = Recommendation(label);
            if (marked)
            {
                recommended.Add(options.Count);
            }

            options.Add(shown);
        }

        o["options"] = options;
        SetRecommended(o, recommended);
        SetText(o, "run", runId);
        return o;
    }

    /// <summary>
    /// The one recommendation rule (spec rec-badge A1): a label is marked when, trimmed, it ends with "(Recommended)" in any case and has text
    /// before it; it is shown as that text, trimmed. An unmarked label is shown as it is.
    /// </summary>
    private static (string Shown, bool Marked) Recommendation(string label)
    {
        const string mark = "(Recommended)";
        var trimmed = label.Trim();
        if (trimmed.EndsWith(mark, StringComparison.OrdinalIgnoreCase) && trimmed[..^mark.Length].Trim() is { Length: > 0 } shown)
        {
            return (shown, true);
        }

        return (label, false);
    }

    /// <summary>The answer as served: a marked option's shown label when it is that option's full stored label, else unchanged.</summary>
    private static string? AnswerShown(JsonValue row, string? answer)
    {
        foreach (var label in Labels(row))
        {
            if (string.Equals(label, answer, StringComparison.Ordinal) && Recommendation(label) is (var shown, true))
            {
                return shown;
            }
        }

        return answer;
    }

    /// <summary>Sets <c>recommended</c>, the marked options' indexes, only when there is one.</summary>
    private static void SetRecommended(JsonObject o, JsonArray recommended)
    {
        if (recommended.Count > 0)
        {
            o["recommended"] = recommended;
        }
    }

    /// <summary>Marks a row <c>multi: true</c> when its parked line is a multi-select question; a single-choice row has no <c>multi</c> key.</summary>
    private static void SetMulti(JsonObject o, JsonValue parked)
    {
        if (parked.Member("multi") is { IsBool: true } multi && multi.AsBool())
        {
            o["multi"] = true;
        }
    }

    private static string? AnswerOf(JsonValue row) =>
        LaneParts.Text(row, "decision") is { Length: > 0 } decision ? decision : LaneParts.Text(row, "choice");

    private static string? DeciderOf(JsonValue row) =>
        row.Member("decider") is { IsMap: true } decider ? LaneParts.Text(decider, "name") ?? LaneParts.Text(decider, "role") : LaneParts.Text(row, "decider");

    private static IEnumerable<string> Labels(JsonValue row)
    {
        if (row.Member("options") is not { IsArray: true } options)
        {
            yield break;
        }

        foreach (var option in options.AsArray())
        {
            if (option.IsNull)
            {
                continue;
            }

            yield return option.IsText ? option.AsString() : Encoding.UTF8.GetString(JsonText.ToUtf8(option, 0));
        }
    }

    /// <summary><paramref name="text"/> when it is at most <paramref name="maxBytes"/> UTF-8 bytes; else its longest prefix of whole characters within that many bytes, then "…".</summary>
    internal static string Cut(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var cut = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes)
            {
                break;
            }

            _ = cut.Append(rune.ToString());
        }

        return cut.Append('…').ToString();
    }

    private static void SetCut(JsonObject o, string name, string? value, int maxBytes) => SetText(o, name, value is null ? null : Cut(value, maxBytes));

    private static void SetText(JsonObject o, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            o[name] = value;
        }
    }

    private static void SetMs(JsonObject o, string name, long ms)
    {
        if (ms != 0)
        {
            o[name] = ms;
        }
    }
}

/// <summary>
/// Wraps a lane so a re-read of a key also tells <paramref name="reread"/> that key, as the lane spells it (a decisions or team key is two
/// parts, the known-dirs key one; atp-1673): the questions lane then re-reads the teams it holds there. Everything else is the wrapped lane's.
/// </summary>
internal sealed class DecisionsRereadNotifier(IModelLane inner, Action<string> reread) : IModelLane
{
    public IModelLane Inner => inner;

    public string Name => inner.Name;

    public IReadOnlyList<string> SliceIds(string key) => inner.SliceIds(key);

    public IReadOnlyList<string> Keys() => inner.Keys();

    public LaneResult Reread(string key, long nowMs)
    {
        var result = inner.Reread(key, nowMs);
        reread(key);
        return result;
    }
}
