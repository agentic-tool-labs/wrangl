using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>The read operations of the progress domain: the team's folder, a stream's plan and a stream's description.</summary>
internal sealed partial class HierLib
{
    private ProgressReader ProgressReads => new(_files, _roster, _progressLogs, _progressDocs);

    /// <summary>
    /// <c>progress.get</c>: <c>{dir, key}</c> is the progress document whose key (the path its file had, as <c>journal-append</c>'s
    /// <c>file</c> and <c>rollup-resume-point</c>'s <c>rollup</c> give it) is <c>key</c>: <c>{found, key, team, name, kind, body}</c>, or <c>{found: false}</c>.
    /// </summary>
    internal JsonValue ProgressGetOp(JsonValue args) => _progressDocs?.ByKey(ArgText(args, "key")) is { } doc
        ? new JsonObject { ["found"] = true, ["key"] = doc.Key, ["team"] = doc.Team, ["name"] = doc.Name, ["kind"] = doc.Kind, ["body"] = doc.Body }
        : new JsonObject { ["found"] = false };

    private static string ProgressTeam(JsonValue args) =>
        ArgText(args, "team") is var team && (team == DefaultScopeFolder || ProgressNamePattern().IsMatch(team) && !team.Contains("..", StringComparison.Ordinal))
            ? team
            : throw new InvalidOperationException($"\"team\" is not a plain name: {team}");

    /// <summary><c>progress.read</c>: <c>{dir, team}</c> is the team's progress folder as <see cref="ProgressJson.ToJson(TeamProgress)"/> writes it.</summary>
    internal JsonValue ProgressReadOp(JsonValue args) => ProgressJson.ToJson(ProgressReads.Read(ArgText(args, "dir"), ProgressTeam(args)));

    /// <summary><c>progress.plan</c>: <c>{dir, team, stream}</c> is the stream's folded plan, or null when it has none.</summary>
    internal JsonValue ProgressPlanOp(JsonValue args)
    {
        // the one seam: the team-less stream backlog is the project's backlog in the state database; every team stream is its plan log
        if (OpArgs.Text(args, "team") is null or "" && OpArgs.Text(args, "stream") == Backlog.Stream)
        {
            return BacklogPlanOf(ArgText(args, "dir")) is { } backlog ? ProgressJson.ToJson(backlog, full: true) : JsonValue.Null;
        }

        return ProgressReads.ReadPlan(ArgText(args, "dir"), ProgressTeam(args), ProgressName(args, "stream")) is { } plan ? ProgressJson.ToJson(plan) : JsonValue.Null;
    }

    /// <summary><c>progress.describe</c>: <c>{dir, team, stream}</c> is the stream as the team file records it.</summary>
    internal JsonValue ProgressDescribeOp(JsonValue args) =>
        ProgressJson.ToJson(ProgressReads.Describe(ArgText(args, "dir"), ProgressTeam(args), ProgressName(args, "stream")));

    /// <summary>The most a stream's plan log gives on a first read; a longer log gives its tail and says so.</summary>
    internal const int ProgressFirstReadCap = 1024 * 1024;

    private const string PlanLogSuffix = ".plan.jsonl";

    /// <summary>
    /// <c>progress.keys</c>: <c>{dir}</c> is the teams of the pool that hold a progress document (per-dir-files-to-db S11d: rows of
    /// <c>progress_docs</c>), a folder on disk, or a progress log row (S11c). A team whose <c>.off</c> row is present is left out.
    /// </summary>
    internal JsonValue ProgressKeysOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var root = NodePath.Join(dir, "progress");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (Lstat(root) is { Kind: FileKind.Directory })
        {
            foreach (var name in _files.List(root))
            {
                if (!name.StartsWith('.') && Lstat(NodePath.Join(root, name)) is { Kind: FileKind.Directory })
                {
                    names.Add(name);
                }
            }
        }

        names.UnionWith(_progressDocs?.Teams(dir) ?? []);
        names.UnionWith(_progressLogs?.Teams(dir, null) ?? []);
        names.RemoveWhere(name => ProgressOff(dir, name));

        var teams = new JsonArray();
        foreach (var name in names)
        {
            teams.Add(name);
        }

        return new JsonObject { ["teams"] = teams };
    }

    /// <summary>
    /// <c>progress.events</c>: <c>{dir, team, after_offsets?}</c> is, for each <c>&lt;stream&gt;.plan.jsonl</c> of the team, the rows
    /// appended after the byte offset the caller remembers for that stream (<c>after_offsets</c> maps stream to offset; absent means from
    /// the start): <c>{streams: [{stream, rows, offset, length, truncated, reset}]}</c>. The read is positioned, so its cost follows the new
    /// rows. Only whole lines are taken, so <c>offset</c> is where the next read starts and a line still being written is left for it.
    /// An offset past the end of the file starts again from 0 (<c>reset</c>). A first read of a file longer than
    /// <see cref="ProgressFirstReadCap"/> gives its tail (<c>truncated</c>). A line that is not a JSON object is skipped.
    /// </summary>
    internal JsonValue ProgressEventsOp(JsonValue args)
    {
        var folder = ProgressFolder(args);
        var streams = new JsonArray();
        if (ProgressOff(args, folder))
        {
            return new JsonObject { ["streams"] = streams };
        }

        var offsets = args.Member("after_offsets");
        var (dir, team) = (ArgText(args, "dir"), NodePath.Basename(folder));
        foreach (var name in PlanLogNames(dir, team))
        {
            var stream = name[..^PlanLogSuffix.Length];
            var remembered = offsets?.Member(stream) is { IsNumber: true } n ? (long)n.AsNumber() : 0L;
            streams.Add(ReadPlanLog(dir, team, name, stream, remembered));
        }

        return new JsonObject { ["streams"] = streams };
    }

    /// <summary>The team's plan logs (<c>&lt;stream&gt;.plan.jsonl</c>) that hold a row, in order.</summary>
    private IEnumerable<string> PlanLogNames(string dir, string team) =>
        (_progressLogs?.Logs(dir, team) ?? []).Where(static n => n.EndsWith(PlanLogSuffix, StringComparison.Ordinal) && n.Length > PlanLogSuffix.Length);

    private const int GoalWindow = 2048;

    /// <summary>
    /// <c>progress.streams</c>: <c>{dir, team}</c> is each stream of the team (those the team file records and those with a plan log):
    /// <c>{streams: [{stream, owner, branch, work_tree, goal}]}</c>. The owner is the first member on the stream, the branch and work tree
    /// (its last path component) come from the team file, and the goal from the first heading of the stream's checklist, of which only
    /// the first <see cref="GoalWindow"/> bytes are read. A fact the files do not give is <c>—</c> (the goal is null).
    /// </summary>
    internal JsonValue ProgressStreamsOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var team = ArgText(args, "team");
        var folder = ProgressFolder(args);
        var record = _roster.ReadTeamRecord(dir, team == "_default" ? null : team);
        var docs = ProgressReads.Docs(dir, NodePath.Basename(folder));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (record.Member("streams") is { IsMap: true } recorded)
        {
            names.UnionWith(recorded.AsObject().Keys);
        }

        if (!ProgressOff(args, folder))
        {
            names.UnionWith(PlanLogNames(dir, NodePath.Basename(folder)).Select(static n => n[..^PlanLogSuffix.Length]));
        }

        var streams = new JsonArray();
        foreach (var stream in names)
        {
            var branch = "—";
            var workTree = "—";
            var owner = "—";
            JsonValue state = JsonValue.Null;
            JsonValue workTreePath = JsonValue.Null;
            if (record.Member("streams")?.Member(stream) is { IsMap: true } s)
            {
                state = s.Member("state") is { IsText: true } st && st.AsString().Length > 0 ? st : JsonValue.Null;
                workTreePath = s.Member("worktree") is { IsText: true } wp && wp.AsString().Length > 0 ? wp : JsonValue.Null;
                branch = s.Member("branch") is { IsText: true } b && b.AsString().Length > 0 ? b.AsString() : branch;
                workTree = s.Member("worktree") is { IsText: true } w && w.AsString().Length > 0 ? NodePath.Basename(w.AsString()) : workTree;
            }

            if (record.Member("members") is { IsArray: true } members)
            {
                foreach (var m in members.AsArray())
                {
                    if (m.Member("stream") is { IsText: true } ms && ms.AsString() == stream && m.Member("name") is { IsText: true } n)
                    {
                        owner = n.AsString();
                        break;
                    }
                }
            }

            JsonValue goal = JsonValue.Null;
            if (docs.GetValueOrDefault(stream + ".md") is { Kind: "checklist" } checklist
                && ProgressFormat.ChecklistTitle(Head(checklist.Body, GoalWindow).Text, stream) is { } title)
            {
                goal = title;
            }

            streams.Add(new JsonObject { ["stream"] = stream, ["owner"] = owner, ["branch"] = branch, ["work_tree"] = workTree, ["work_tree_path"] = workTreePath, ["goal"] = goal, ["state"] = state });
        }

        return new JsonObject { ["streams"] = streams };
    }

    /// <summary>
    /// <c>progress.checklists</c>: <c>{dir, team, known?}</c> is each stream checklist of the team (<c>&lt;stream&gt;.md</c> that opens with a
    /// stream marker, see <see cref="PlanChecklist"/>) as <c>{streams: [{stream, sig, ...parsed}]}</c>. <c>sig</c> is the document's update time
    /// (ms) and byte size; a document whose <c>sig</c> equals the one <c>known</c> holds for its stream is answered <c>{stream, sig, unchanged: true}</c>
    /// without being parsed. <c>hash</c> is a digest of the text read, so a caller can tell a touched document from a changed one.
    /// </summary>
    internal JsonValue ProgressChecklistsOp(JsonValue args)
    {
        var folder = ProgressFolder(args);
        var streams = new JsonArray();
        if (ProgressOff(args, folder))
        {
            return new JsonObject { ["streams"] = streams };
        }

        var known = args.Member("known");
        foreach (var doc in ProgressReads.Docs(ArgText(args, "dir"), NodePath.Basename(folder)).Values.Where(d => d.Kind == "checklist" && IsChecklistName(d.Name)).OrderBy(static d => d.Name, StringComparer.Ordinal))
        {
            var stream = doc.Name[..^".md".Length];
            var (text, size) = Head(doc.Body, PlanChecklist.MaxBytes);
            var sig = doc.UpdatedMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (known?.Member(stream) is { IsText: true } was && was.AsString() == sig)
            {
                streams.Add(new JsonObject { ["stream"] = stream, ["sig"] = sig, ["unchanged"] = true });
                continue;
            }

            if (PlanChecklist.Parse(stream, text, size > PlanChecklist.MaxBytes, doc.UpdatedMs) is { } parsed)
            {
                parsed["stream"] = stream;
                parsed["sig"] = sig;
                parsed["hash"] = ProgressFormat.Sha256(text);
                streams.Add(parsed);
            }
        }

        return new JsonObject { ["streams"] = streams };
    }

    /// <summary>The first <paramref name="max"/> bytes of <paramref name="body"/> as text (as a bounded file read gave them), and the body's whole byte size.</summary>
    private static (string Text, long Size) Head(string body, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return (bytes.Length <= max ? body : Encoding.UTF8.GetString(bytes, 0, max), bytes.Length);
    }

    private static bool IsChecklistName(string name) =>
        name.EndsWith(".md", StringComparison.Ordinal) && !name.EndsWith(".journal.md", StringComparison.Ordinal) && !name.EndsWith(".closing.md", StringComparison.Ordinal)
        && name is not ("rollup.md" or "closing.md") && ProgressNamePattern().IsMatch(name[..^".md".Length]);

    /// <summary>
    /// A plan log's rows after <paramref name="remembered"/> (per-dir-files-to-db S11c: <c>offset</c> and <c>length</c> are row seqs, not
    /// byte offsets). A remembered seq past the last row starts again from 0 (<c>reset</c>); new rows past <see cref="ProgressFirstReadCap"/>
    /// bytes give the newest that fit (<c>truncated</c>, and <c>reset</c> when some were remembered).
    /// </summary>
    private JsonObject ReadPlanLog(string dir, string team, string log, string stream, long remembered)
    {
        IReadOnlyList<ProgressLogRow> Rows(long after) => _progressLogs?.Rows(dir, team, log, after) ?? [];
        var start = Math.Max(remembered, 0);
        var fresh = Rows(start).ToList();
        var reset = fresh.Count == 0 && start > 0 && Rows(start - 1).Count == 0;
        if (reset)
        {
            start = 0;
            fresh = [.. Rows(0)];
        }

        var length = fresh.Count > 0 ? fresh[^1].Seq : start;
        var truncated = false;
        if (fresh.Sum(static r => (long)Encoding.UTF8.GetByteCount(r.Body) + 1) > ProgressFirstReadCap)
        {
            // more than the cap is new: the stream starts over from its newest window
            reset |= start != 0;
            truncated = true;
            var kept = 0L;
            var from = fresh.Count;
            while (from > 0 && kept + Encoding.UTF8.GetByteCount(fresh[from - 1].Body) + 1 <= ProgressFirstReadCap)
            {
                kept += Encoding.UTF8.GetByteCount(fresh[from - 1].Body) + 1;
                from--;
            }

            fresh = fresh[from..];
        }

        var rows = new JsonArray();
        foreach (var r in fresh)
        {
            if (TryRow(r.Body) is { } row)
            {
                rows.Add(row);
            }
        }

        var offset = fresh.Count > 0 ? fresh[^1].Seq : start;
        return new JsonObject
        {
            ["stream"] = stream,
            ["rows"] = rows,
            ["offset"] = offset,
            ["length"] = length,
            ["truncated"] = truncated,
            ["reset"] = reset,
        };
    }

    private static JsonValue? TryRow(string line)
    {
        try
        {
            return JsonText.Parse(line) is { IsMap: true } row ? (JsonValue?)row : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}
