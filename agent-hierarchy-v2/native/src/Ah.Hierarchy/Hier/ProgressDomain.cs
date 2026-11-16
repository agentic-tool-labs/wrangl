using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>
/// The writers of <c>&lt;pool&gt;/progress/&lt;team&gt;/</c>. Every op names the pool (<c>dir</c>) and the team; the folder is derived
/// from them. A team whose <c>.off</c> row is present is untouched: every op answers <c>{off: true}</c> and writes nothing.
/// </summary>
[StateDomain("progress", "progress")]
internal sealed class ProgressDomain(HierLib lib) : HierDomain(lib, "progress", "progress")
{
    public override IReadOnlyList<StateOp> Ops =>
    [
        // per-dir-files-to-db S11d D2: the checklists and journals live in progress_docs, on the backlog's mode row like the logs
        Write("create-if-absent", OpStyle.Replace, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressCreateDb(tx, args)), ModeKey = Backlog.Stream },
        Write("journal-append", OpStyle.Append, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressJournalDb(tx, args)), ModeKey = Backlog.Stream },
        Write("state-cas", OpStyle.Replace, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressStateDb(tx, args)), ModeKey = Backlog.Stream },
        // per-dir-files-to-db S11d D3: .nudged, sessions.jsonl and the rollup live in progress_docs too
        Write("nudged-append", OpStyle.Append, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressNudgedDb(tx, args)), ModeKey = Backlog.Stream },
        Write("session-append", OpStyle.Append, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressSessionDb(tx, args)), ModeKey = Backlog.Stream },
        // per-dir-files-to-db S11c: the ledger and the plan logs live in progress_logs, on the backlog's mode row (db with no row), as the work registry does
        InternalWrite("ledger-append", OpStyle.Append, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressLedgerDb(tx, args)), ModeKey = Backlog.Stream },
        InternalWrite("ledger-cursor", OpStyle.Replace, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressLedgerCursorDb(tx, args)), ModeKey = Backlog.Stream },
        // b435: the release watch's prior tips, once release-watch.seen in the pool
        InternalWrite("release-seen", OpStyle.Replace, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressReleaseSeenDb(tx, args)), ModeKey = Backlog.Stream },
        // files-off-disk §6: `ah progress off|on <team>`; the team's .off row
        InternalWrite("off-set", OpStyle.Replace, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressOffSetDb(tx, args)), ModeKey = Backlog.Stream },
        Write("plan-append", OpStyle.Append, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressPlanAppendDb(tx, args)), ModeKey = Backlog.Stream },
        Write("checklist-rewrite", OpStyle.Replace, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressChecklistRewriteDb(tx, args)), ModeKey = Backlog.Stream },
        InternalRead("read", (call, _) => OpArgs.Run(call, args => Lib.ProgressReadOp(args))),
        InternalRead("plan", (call, _) => OpArgs.Run(call, args => Lib.ProgressPlanOp(args))),
        InternalRead("describe", (call, _) => OpArgs.Run(call, args => Lib.ProgressDescribeOp(args))),
        Write("rollup-resume-point", OpStyle.Append, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressResumePointDb(tx, args)), ModeKey = Backlog.Stream },
        Write("rollup-block-upsert", OpStyle.Replace, false, NoLogFiles, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.ProgressRollupBlockDb(tx, args)), ModeKey = Backlog.Stream },
        new("keys", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.ProgressKeysOp(args)), PathArgs: ["dir"]),
        new("streams", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.ProgressStreamsOp(args)), PathArgs: ["dir"]),
        new("checklists", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.ProgressChecklistsOp(args)), PathArgs: ["dir"]),
        new("events", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.ProgressEventsOp(args)), PathArgs: ["dir"]),
        // per-dir-files-to-db S11d: a progress document by its key (the path its file had)
        Read("get", (call, _) => OpArgs.Run(call, args => Lib.ProgressGetOp(args)), "dir"),

        // the backlog has no files store: its mode row starts as db, so the files body only answers when there is no database
        Write(Backlog.WriteOp, OpStyle.Replace, false, (call, _) => new OpResult(false, default, "the backlog needs the state database, and there is none"), "dir")
            with { DbBody = StatusDirtyMark.Body(Name, (call, tx) => OpArgs.Run(call, args => Lib.BacklogWriteDb(call, tx, args))), ModeKey = Backlog.Stream },

        // the work registry shares the backlog's mode row, so it adds no mode.* row an older build would refuse message writes over
        Write(Work.WriteOp, OpStyle.Replace, false, (call, _) => new OpResult(false, default, "the work registry needs the state database, and there is none"), "dir")
            with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.WorkWriteDb(tx, args)), ModeKey = Backlog.Stream },
    ];

    private static OpResult NoLogFiles(OpCall call, IStoreWriteContext? writer) => new(false, default, $"progress.{call.Op} needs the state database, and there is none");

    // a backlog write changes the health facts every status shows (ruling B1)
    public override void AfterWrite(OpCall operation, OpResult result)
    {
        if (operation.Op == Backlog.WriteOp)
        {
            Delta(operation, result, null);
        }
    }
}

internal sealed partial class HierLib
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex ProgressNamePattern();

    private static string ProgressName(JsonValue args, string key)
    {
        var name = ArgText(args, key);
        if (key == "stream" && name.StartsWith('_'))
        {
            return name == ProgressFormat.RunStream && args.Member("checklist") is { IsTruthy: true }
                ? name
                : throw new InvalidOperationException($"reserved-stream: \"{name}\" starts with _; those names are not streams");
        }

        if (!ProgressNamePattern().IsMatch(name) || name.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"\"{key}\" is not a plain name: {name}");
        }

        return name;
    }

    /// <summary>The folder the default-scope team uses; no team name can start with an underscore, so it cannot collide with one.</summary>
    private const string DefaultScopeFolder = "_default";

    private static string ProgressFolder(JsonValue args) =>
        NodePath.Join(ArgText(args, "dir"), "progress", ArgText(args, "team") == DefaultScopeFolder ? DefaultScopeFolder : ProgressName(args, "team"));

    // files-off-disk §6: the switch is the team's .off row, no longer a file in its folder
    // every progress_docs key, the off row's included, is ProgressDocKey's: the raw dir arg, as every reader passes it
    private bool ProgressOff(string dir, string team) => _progressDocs?.IsOff(dir, team) ?? false;

    private bool ProgressOff(JsonValue args, string folder) => ProgressDocKey(args, folder) is var (dir, team) && ProgressOff(dir, team);

    /// <summary>Inside a write's DbBody: the off row read through the op's own transaction, so a busy pooled read never lets an off team be written to.</summary>
    private bool ProgressOff(IDbOpTransaction tx, JsonValue args, string folder) =>
        ProgressDocKey(args, folder) is var (dir, team) && _progressDocs?.IsOff(tx, dir, team) == true;

    private static JsonObject ProgressOffResult() => new() { ["off"] = true };

    /// <summary><c>progress.off-set</c>: <c>{dir, team, off}</c> sets or clears the team's off switch: <c>{team, off}</c>.</summary>
    internal JsonValue ProgressOffSetDb(IDbOpTransaction tx, JsonValue args)
    {
        var (dir, team) = ProgressDocKey(args, ProgressFolder(args));
        var off = args.Member("off") is { IsTruthy: true };
        ProgressDocsOrFail.SetOff(tx, dir, team, off);
        return new JsonObject { ["team"] = team, ["off"] = off };
    }

    /// <summary>
    /// Appends one <c>Resume point: &lt;path&gt;</c> line to the team's rollup. The rollup is the Orchestrator's own document; apart from
    /// the managed block between its markers, this is the only line a verb adds to it. Nothing is written, and the answer says why, when
    /// the team has no rollup or progress is off. <c>rollup</c> in the answer is the rollup's key, the path its file had.
    /// </summary>
    internal JsonValue ProgressResumePointDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var path = ArgText(args, "path");
        if (path.Contains('\n', StringComparison.Ordinal) || path.Length == 0)
        {
            throw new InvalidOperationException("rollup-resume-point: path must be one non-empty line");
        }

        var (dir, team) = ProgressDocKey(args, folder);
        var docs = ProgressDocsOrFail;
        if (docs.Body(tx, dir, team, RollupName) is not { } text)
        {
            return new JsonObject { ["appended"] = false, ["reason"] = "no rollup" };
        }

        docs.Append(tx, dir, team, RollupName, (text.EndsWith('\n') ? string.Empty : "\n") + "Resume point: " + path + "\n");
        return new JsonObject { ["appended"] = true, ["rollup"] = NodePath.Join(folder, RollupName) };
    }

    private const string RollupName = "rollup.md";

    internal const string RollupBlockBegin = "<!-- ah:rollup-block:begin (managed by ah; text between the markers is rewritten) -->";

    internal const string RollupBlockEnd = "<!-- ah:rollup-block:end -->";

    private static string RollupField(JsonValue args, string key)
    {
        var text = ArgText(args, key).Replace('\n', ' ').Replace('\r', ' ').Replace('|', '/').Trim();
        return text.Length == 0 ? "-" : text;
    }

    /// <summary>
    /// Sets the one line of <c>args.slug</c> in the managed block of the team's rollup:
    /// <c>stream | slug | status | from | UTC time | response path</c>. Only the text between the two marker lines is ever rewritten.
    /// A rollup with no markers gets the block appended once; a missing rollup is created holding just the block; a rollup with a
    /// lone or misordered marker is left alone.
    /// </summary>
    internal JsonValue ProgressRollupBlockDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var (dir, team) = ProgressDocKey(args, folder);
        var docs = ProgressDocsOrFail;
        var (result, next) = RollupBlockChange(args, docs.Body(tx, dir, team, RollupName));
        if (next is not null)
        {
            _ = docs.Put(tx, dir, team, RollupName, next);
        }

        return result;
    }

    /// <summary>The answer of a block upsert on a rollup reading <paramref name="current"/> (null: none), and the rollup's new text (null: unchanged).</summary>
    private static (JsonObject Result, string? Next) RollupBlockChange(JsonValue args, string? current)
    {
        var slug = ProgressName(args, "slug");
        var ack = args.Member("ack") is { IsTruthy: true };
        string line;
        if (ack)
        {
            // An ack only restamps a line that is already there: it keeps the stream, from and response path, and adds none.
            var cells = current?.Split('\n')
                .SkipWhile(l => l.TrimEnd('\r') != RollupBlockBegin).Skip(1)
                .TakeWhile(l => l.TrimEnd('\r') != RollupBlockEnd)
                .Select(l => l.Split(" | "))
                .FirstOrDefault(c => c.Length == 6 && c[1] == slug);
            if (cells is null)
            {
                return (new JsonObject { ["updated"] = false, ["no_line"] = true }, null);
            }

            line = string.Join(" | ", cells[0], slug, "acked", cells[3], RollupField(args, "time"), cells[5]);
        }
        else
        {
            line = string.Join(" | ", RollupField(args, "stream"), slug, RollupField(args, "status"), RollupField(args, "from"), RollupField(args, "time"), RollupField(args, "path"));
        }

        string next;
        if (current is null)
        {
            next = RollupBlockBegin + "\n" + line + "\n" + RollupBlockEnd + "\n";
        }
        else
        {
            var lines = current.Split('\n').ToList();
            var begin = lines.FindIndex(l => l.TrimEnd('\r') == RollupBlockBegin);
            var end = lines.FindIndex(l => l.TrimEnd('\r') == RollupBlockEnd);
            if (begin < 0 && end < 0)
            {
                next = current + (current.Length == 0 || current.EndsWith('\n') ? string.Empty : "\n") + RollupBlockBegin + "\n" + line + "\n" + RollupBlockEnd + "\n";
            }
            else if (begin < 0 || end < begin || lines.FindLastIndex(l => l.TrimEnd('\r') == RollupBlockBegin) != begin || lines.FindLastIndex(l => l.TrimEnd('\r') == RollupBlockEnd) != end)
            {
                return (new JsonObject { ["updated"] = false, ["skipped"] = "markers" }, null);
            }
            else
            {
                var mine = lines.FindIndex(begin + 1, end - begin - 1, l => l.Split(" | ") is { Length: >= 2 } cells && cells[1] == slug);
                if (mine >= 0)
                {
                    lines[mine] = line;
                }
                else
                {
                    lines.Insert(end, line);
                }

                next = string.Join("\n", lines);
            }
        }

        return next == current
            ? (new JsonObject { ["updated"] = false, ["unchanged"] = true }, null)
            : (new JsonObject { ["updated"] = true, ["created"] = current is null }, next);
    }

    private IProgressDocSource ProgressDocsOrFail => _progressDocs ?? throw new InvalidOperationException("the progress documents need the state database, and there is none");

    /// <summary>The pool and team of a progress op, as <c>progress_docs</c> keys them.</summary>
    private static (string Dir, string Team) ProgressDocKey(JsonValue args, string folder) => (ArgText(args, "dir"), NodePath.Basename(folder));

    /// <summary>Creates <c>args.file</c> in the team's documents with <c>args.content</c> unless it exists: <c>{created}</c>.</summary>
    internal JsonValue ProgressCreateDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var (dir, team) = ProgressDocKey(args, folder);
        return new JsonObject { ["created"] = ProgressDocsOrFail.Put(tx, dir, team, ProgressName(args, "file"), ArgText(args, "content"), createOnly: true) };
    }

    /// <summary>
    /// Appends one entry to <c>&lt;stream&gt;.journal.md</c>, creating the journal, the stream's checklist and the rollup first when
    /// they are absent, all in the one transaction. The same request id with the same content hash is not appended twice; the same id
    /// with other content is appended again as refiled. A journal written by a newer major is left alone. <c>file</c> is the journal's key.
    /// </summary>
    internal JsonValue ProgressJournalDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var stream = ProgressName(args, "stream");
        var requestId = ArgText(args, "request_id");
        var sha = ArgText(args, "sha");
        var tagged = new List<KeyValuePair<string, string>>();
        if (args.Member("tagged") is { IsArray: true } rows)
        {
            foreach (var row in rows.AsArray())
            {
                tagged.Add(new KeyValuePair<string, string>(ArgText(row, "tag"), ArgText(row, "text")));
            }
        }

        var extract = new ReportExtract(args.Member("status") is { IsText: true } s ? s.AsString() : null, tagged);
        var (dir, team) = ProgressDocKey(args, folder);
        var docs = ProgressDocsOrFail;
        if (args.Member("rollup_md") is { IsText: true } rollup)
        {
            _ = docs.Put(tx, dir, team, "rollup.md", rollup.AsString(), createOnly: true);
        }

        if (args.Member("stream_md") is { IsText: true } checklist)
        {
            _ = docs.Put(tx, dir, team, stream + ".md", checklist.AsString(), createOnly: true);
        }

        var name = stream + ".journal.md";
        var path = NodePath.Join(folder, name);
        var existing = docs.Body(tx, dir, team, name);
        if (existing is not null && !ProgressFormat.Writable(existing))
        {
            return new JsonObject { ["appended"] = false, ["skipped"] = "newer-format", ["file"] = path };
        }

        var seen = existing is null ? [] : ProgressFormat.EntryHashes(existing, requestId);
        if (seen.Contains(sha, StringComparer.Ordinal))
        {
            return new JsonObject { ["appended"] = false, ["duplicate"] = true, ["file"] = path };
        }

        var entry = ProgressFormat.FormatEntry(
            ArgText(args, "time"),
            ArgText(args, "from"),
            ArgText(args, "kind"),
            requestId,
            seen.Count > 0,
            extract,
            ArgText(args, "report_path"),
            sha);
        if (existing is null)
        {
            _ = docs.Put(tx, dir, team, name, ProgressFormat.JournalHeader() + entry);
        }
        else
        {
            docs.Append(tx, dir, team, name, (existing.EndsWith('\n') ? string.Empty : "\n") + entry);
        }

        return new JsonObject { ["appended"] = true, ["refiled"] = seen.Count > 0, ["file"] = path };
    }

    /// <summary>
    /// Rewrites the <c>State:</c> word of <c>&lt;stream&gt;.md</c> to <c>args.state</c> when its body still hashes to <c>args.expect_sha</c>:
    /// <c>{changed}</c>, or <c>{changed: false, conflict | no_state | skipped}</c> with the body untouched. The hash is checked in the write's transaction.
    /// </summary>
    internal JsonValue ProgressStateDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var (dir, team) = ProgressDocKey(args, folder);
        var name = ProgressName(args, "stream") + ".md";
        var path = NodePath.Join(folder, name);
        var current = ProgressDocsOrFail.Body(tx, dir, team, name) ?? throw new InvalidOperationException($"{path} does not exist");
        if (!string.Equals(ProgressFormat.Sha256(current), ArgText(args, "expect_sha"), StringComparison.Ordinal))
        {
            return new JsonObject { ["changed"] = false, ["conflict"] = true };
        }

        if (!ProgressFormat.Writable(current))
        {
            return new JsonObject { ["changed"] = false, ["skipped"] = "newer-format" };
        }

        var next = ProgressFormat.WithState(current, ArgText(args, "state"));
        if (next is null)
        {
            return new JsonObject { ["changed"] = false, ["no_state"] = true };
        }

        _ = ProgressDocsOrFail.Put(tx, dir, team, name, next);
        return new JsonObject { ["changed"] = true };
    }

    /// <summary>Records that the Orchestrator was nudged about (<c>args.stream</c>, <c>args.report_id</c>) once: <c>{appended}</c>.</summary>
    internal JsonValue ProgressNudgedDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var line = ProgressName(args, "stream") + " " + ArgText(args, "report_id")
            + (args.Member("updated") is { IsText: true } updated && updated.AsString().Length > 0 && !updated.AsString().Contains(' ', StringComparison.Ordinal) ? " " + updated.AsString() : string.Empty);
        var (dir, team) = ProgressDocKey(args, folder);
        var docs = ProgressDocsOrFail;
        var existing = docs.Body(tx, dir, team, NudgedName);
        if (existing is null)
        {
            docs.Append(tx, dir, team, NudgedName, ProgressFormat.NudgedMarker() + "\n" + line + "\n");
            return new JsonObject { ["appended"] = true };
        }

        if (!ProgressFormat.Writable(existing))
        {
            return new JsonObject { ["appended"] = false, ["skipped"] = "newer-format" };
        }

        if (existing.Split('\n').Any(l => string.Equals(l.TrimEnd('\r'), line, StringComparison.Ordinal)))
        {
            return new JsonObject { ["appended"] = false };
        }

        docs.Append(tx, dir, team, NudgedName, (existing.EndsWith('\n') ? string.Empty : "\n") + line + "\n");
        return new JsonObject { ["appended"] = true };
    }

    private const string NudgedName = ".nudged";

    /// <summary>
    /// Appends <c>args.rows</c> (plan rows) to <c>&lt;stream&gt;.plan.jsonl</c>: <c>{appended: n}</c>, or <c>{appended: 0, refused}</c>
    /// with nothing written when any row may not follow the ones before it. With <c>args.import</c> a done row may carry no commits,
    /// and the op refuses when the log already exists; with <c>args.bare_done</c> a done row may carry no commits and nothing else changes.
    /// </summary>
    internal JsonValue ProgressPlanAppendDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var (dir, team, log) = (ArgText(args, "dir"), NodePath.Basename(folder), ProgressName(args, "stream") + ".plan.jsonl");
        var path = NodePath.Join(folder, log);
        var existing = ProgressReader.LogText(ProgressLogRows(tx, dir, team, log));
        var import = args.Member("import") is { IsTruthy: true };
        var bareDone = args.Member("bare_done") is { IsTruthy: true };
        if (import && existing is not null)
        {
            return new JsonObject { ["appended"] = 0, ["refused"] = "a plan already exists for this stream" };
        }

        var rows = args.Member("rows") is { IsArray: true } list ? list.AsArray() : throw new InvalidOperationException("rows is not an array");
        var text = existing ?? string.Empty;
        var fold = PlanFoldOf(path, text);
        var added = new List<string>();
        var count = 0;
        foreach (var row in rows)
        {
            var refusal = ProgressFormat.RefusePlanRow(row, fold.State, import || bareDone);
            if (refusal is not null)
            {
                return new JsonObject { ["appended"] = 0, ["refused"] = refusal };
            }

            fold.Apply(row);
            added.Add(JsonText.Stringify(row, 0));
            count++;
        }

        if (count == 0)
        {
            _planFolds[path] = (text, fold);
            return new JsonObject { ["appended"] = 0 };
        }

        foreach (var body in added)
        {
            AppendProgressLogRow(tx, dir, team, log, body);
        }

        _planFolds[path] = (text + string.Concat(added.Select(static b => b + "\n")), fold);
        return new JsonObject { ["appended"] = count };
    }

    private IProgressLogSource ProgressLogsOrFail => _progressLogs ?? throw new InvalidOperationException("the progress logs need the state database, and there is none");

    /// <summary>The rows of a progress log in a write's transaction, its old file taken first.</summary>
    private IReadOnlyList<ProgressLogRow> ProgressLogRows(IDbOpTransaction tx, string dir, string team, string log)
    {
        ProgressLogsOrFail.Take(tx, dir, team, log);
        return [.. tx.Query("SELECT seq, body FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 AND log = ?3 ORDER BY seq", ProgressLogsOrFail.Key(dir), team, log)
            .Select(static r => new ProgressLogRow((long)r[0]!, (string)r[1]!))];
    }

    private void AppendProgressLogRow(IDbOpTransaction tx, string dir, string team, string log, string body) => tx.Execute(
        "INSERT INTO progress_logs(hier_dir, team, log, seq, body) VALUES (?1, ?2, ?3, (SELECT COALESCE(MAX(seq), 0) + 1 FROM progress_logs WHERE hier_dir = ?1 AND team = ?2 AND log = ?3), ?4)",
        ProgressLogsOrFail.Key(dir), team, log, body);

    /// <summary>The last plan text each plan-append saw and its fold, by path; a call takes its entry out, so two calls never share a fold.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, PlanFold Fold)> _planFolds = new(StringComparer.Ordinal);

    /// <summary>
    /// The fold of <paramref name="text"/>: the one kept for <paramref name="path"/> with only the rows after its text folded in, when
    /// that text ends a line and starts <paramref name="text"/>; else a fold of the whole text.
    /// </summary>
    private PlanFold PlanFoldOf(string path, string text)
    {
        if (_planFolds.TryRemove(path, out var kept) && kept.Text.EndsWith('\n') && text.StartsWith(kept.Text, StringComparison.Ordinal))
        {
            kept.Fold.ApplyText(text[kept.Text.Length..]);
            return kept.Fold;
        }

        var fold = new PlanFold();
        fold.ApplyText(text);
        return fold;
    }

    /// <summary>
    /// Rewrites the Checklist block of <c>&lt;stream&gt;.md</c> from <c>&lt;stream&gt;.plan.jsonl</c> and sets its <c>Updated:</c> to
    /// <c>args.updated</c>: <c>{rewritten}</c>. Only a file whose version line says <c>plan=jsonl</c> is rewritten; <c>args.optin</c>
    /// makes the version line say it first. Otherwise <c>{rewritten: false}</c> with <c>refused: not-planned</c>, <c>skipped:
    /// newer-format</c> or <c>no_checklist</c>, and the file untouched.
    /// </summary>
    internal JsonValue ProgressChecklistRewriteDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var stream = ProgressName(args, "stream");
        var (dir, team) = ProgressDocKey(args, folder);
        var name = stream + ".md";
        var current = ProgressDocsOrFail.Body(tx, dir, team, name);
        if (current is null)
        {
            return new JsonObject { ["rewritten"] = false, ["no_checklist"] = true };
        }

        if (!ProgressFormat.Writable(current))
        {
            return new JsonObject { ["rewritten"] = false, ["skipped"] = "newer-format" };
        }

        var optIn = args.Member("optin") is { IsTruthy: true };
        if (!optIn && !ProgressFormat.IsPlanned(current))
        {
            return new JsonObject { ["rewritten"] = false, ["refused"] = "not-planned" };
        }

        var plan = ProgressFormat.FoldPlan(ProgressReader.LogText(ProgressLogRows(tx, dir, team, stream + ".plan.jsonl")));
        var next = ProgressFormat.WithRenderedChecklist(current, plan, ArgText(args, "updated"), optIn);
        if (next is null)
        {
            return new JsonObject { ["rewritten"] = false, ["no_checklist"] = true };
        }

        _ = ProgressDocsOrFail.Put(tx, dir, team, name, next);
        return new JsonObject { ["rewritten"] = true };
    }

    /// <summary>Whether the inventory already holds an <c>orchestrator-start</c> row for the session; a line that is not JSON is skipped.</summary>
    private static bool StartRecorded(string inventory, string session)
    {
        foreach (var line in inventory.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var row = JsonText.Parse(line);
                if (row.Member("event") is { IsText: true } kind && kind.AsString() == "orchestrator-start"
                    && row.Member("session_id") is { IsText: true } id && id.AsString() == session)
                {
                    return true;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a line another writer left is not this reader's to judge
            }
        }

        return false;
    }

    /// <summary>
    /// Appends <c>args.row</c> (an object) to <c>sessions.jsonl</c> with the format version added: <c>{appended}</c>. An
    /// <c>orchestrator-start</c> row for a session that already has one is not appended (<c>appended</c> is false).
    /// </summary>
    internal JsonValue ProgressSessionDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var row = args.Member("row") is { IsMap: true } r ? JsOps.Spread(r) : throw new InvalidOperationException("row is not an object");
        row["v"] = ProgressFormat.Version;
        var (dir, team) = ProgressDocKey(args, folder);
        var docs = ProgressDocsOrFail;
        if (row.Get("event") is { IsText: true } kind && kind.AsString() == "orchestrator-start"
            && row.Get("session_id") is { IsText: true } session && session.AsString().Length > 0
            && docs.Body(tx, dir, team, SessionsName) is { } existing && StartRecorded(existing, session.AsString()))
        {
            return new JsonObject { ["appended"] = false };
        }

        docs.Append(tx, dir, team, SessionsName, JsonText.Stringify(row, 0) + "\n");
        return new JsonObject { ["appended"] = true };
    }

    private const string SessionsName = "sessions.jsonl";

    /// <summary>
    /// Appends <c>args.row</c> to the team's <c>ledger.jsonl</c> as one line: <c>text</c> goes through the secret check and the length cut,
    /// every other string loses its control characters. The one writer of the ledger.
    /// </summary>
    internal JsonValue ProgressLedgerDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var row = args.Member("row") is { IsMap: true } r ? JsOps.Spread(r) : throw new InvalidOperationException("row is not an object");
        if (row.Get("kind") is not { IsText: true } kind || !LedgerText.Kinds.Contains(kind.AsString()))
        {
            throw new InvalidOperationException("the ledger row has no known kind");
        }

        var line = new JsonObject { ["v"] = 1 };
        foreach (var key in row.Keys)
        {
            var value = row[key];
            line[key] = key == "text" ? LedgerText.Text(value.IsText ? value.AsString() : string.Empty)
                : value.IsText ? LedgerText.Clean(value.AsString())
                : value.IsMap ? CleanLedgerMap(value)
                : value;
        }

        var (dir, team) = (ArgText(args, "dir"), NodePath.Basename(folder));
        ProgressLogsOrFail.Take(tx, dir, team, LedgerLog);
        AppendProgressLogRow(tx, dir, team, LedgerLog, JsonText.Stringify(line, 0));
        return new JsonObject { ["appended"] = true };
    }

    internal const string LedgerLog = "ledger.jsonl";

    /// <summary>Sets the seq of the last ledger row the memory adapter has been given (<c>args.seq</c>).</summary>
    internal JsonValue ProgressLedgerCursorDb(IDbOpTransaction tx, JsonValue args)
    {
        var folder = ProgressFolder(args);
        if (ProgressOff(tx, args, folder))
        {
            return ProgressOffResult();
        }

        var seq = args.Member("seq") is { IsNumber: true } n && n.AsNumber() >= 0
            ? (long)n.AsNumber()
            : throw new InvalidOperationException("seq is not a whole number, 0 or more");
        var (dir, team) = (ArgText(args, "dir"), NodePath.Basename(folder));
        ProgressLogsOrFail.Take(tx, dir, team, LedgerLog);
        ProgressLogsOrFail.SetLedgerCursor(tx, dir, team, seq);
        return new JsonObject { ["written"] = true };
    }

    /// <summary>Stores the release watch's prior tips of the pool (<c>args.text</c>).</summary>
    internal JsonValue ProgressReleaseSeenDb(IDbOpTransaction tx, JsonValue args)
    {
        ProgressLogsOrFail.SetReleaseSeen(tx, ArgText(args, "dir"), ArgText(args, "text"));
        return new JsonObject { ["written"] = true };
    }

    private static JsonValue CleanLedgerMap(JsonValue map)
    {
        var cleaned = new JsonObject();
        var source = JsOps.Spread(map);
        foreach (var key in source.Keys)
        {
            var value = source[key];
            cleaned[key] = value.IsText ? LedgerText.Clean(value.AsString()) : value;
        }

        return cleaned;
    }
}
