using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy.StateCalls;
using Ah.Roster;

namespace Ah.Hierarchy;

/// <summary>Who a stream is, for the skeleton of its checklist: the owner, the branch, the work tree and the goal.</summary>
public sealed record ProgressStreamInfo(string Owner, string Branch, string WorkTree, string? Goal);

/// <summary>What appending a report to a journal did: appended, a duplicate that was left out, refiled over an earlier entry, the team's progress being off, or skipped (a newer format).</summary>
public sealed record ProgressAppend(bool Appended, bool Duplicate, bool Refiled, bool Off, string? Skipped);

/// <summary>One stream's records: the counts and times its checklist and journal state, whether it is stale, and what could not be read.</summary>
public sealed record StreamProgress(
    string Stream,
    bool HasChecklist,
    bool HasJournal,
    int Done,
    int InProgress,
    int NotStarted,
    string? State,
    DateTimeOffset? Updated,
    DateTimeOffset? NewestEntry,
    bool Stale,
    bool NewerFormat,
    IReadOnlyList<string> Warnings,
    bool Planned = false,
    string? Title = null);

/// <summary>A team's progress folder: whether it is off, whether the rollup exists, its streams, and the files a newer format wrote.</summary>
public sealed record TeamProgress(bool Off, bool RollupPresent, IReadOnlyList<StreamProgress> Streams, IReadOnlyList<string> NewerFormatFiles);

/// <summary>A report journaled to a stream after its checklist was last updated, for which the Orchestrator has not been nudged.</summary>
public sealed record ProgressNudge(string Stream, string ReportId, string Role, IReadOnlyList<string>? Open = null, string Updated = "");

/// <summary>What closing a stream's checklist did: <c>State:</c> rewritten, or why not; and the open items it still lists.</summary>
public sealed record ProgressDone(bool Changed, int InProgress, string? Reason);

/// <summary>The progress records of a team: creating them, journaling reports into them, reading their counts and staleness, and the Orchestrator's nudge set.</summary>
public interface IHierarchyProgress
{
    /// <summary>Whether the team's progress is off: its <c>.off</c> row is present.</summary>
    bool IsOff(string pool, string team);

    /// <summary>Sets (<c>ah progress off</c>) or clears (<c>on</c>) the team's off switch; the write's answer, <c>{team, off}</c>.</summary>
    JsonValue SetOff(string pool, string team, bool off) => throw new NotSupportedException("progress off needs the state database");

    /// <summary>Creates the team's <c>rollup.md</c> and its <c>main.md</c> checklist when absent; returns the files it created. Nothing when progress is off.</summary>
    IReadOnlyList<string> EnsureTeam(string pool, string team, ProgressStreamInfo main);

    /// <summary>Creates <c>&lt;stream&gt;.md</c> from the template when absent; returns the files it created. Nothing when progress is off.</summary>
    IReadOnlyList<string> EnsureStream(string pool, string team, string stream, ProgressStreamInfo info);

    /// <summary>
    /// Journals the report in <paramref name="responsePath"/> (a <c>&lt;id&gt;--&lt;to&gt;--&lt;slug&gt;--response.md</c> file) into
    /// <paramref name="stream"/>, creating the team's missing records first. The entry links to the file under <c>.claude/ah2</c>
    /// only. A failure throws.
    /// </summary>
    ProgressAppend AppendReport(string pool, string team, string stream, ProgressStreamInfo info, string from, string responsePath);

    /// <summary>
    /// Sets the response's slug line in the managed block of the team's <c>rollup.md</c> (see <c>rollup-block-upsert</c>), with the report's
    /// own status; the Orchestrator's text outside the markers is never touched. False when progress is off or the rollup's markers are damaged.
    /// </summary>
    bool UpdateRollupBlock(string pool, string team, string stream, string from, string responsePath);

    /// <summary>
    /// Sets the status of the response's slug line in the rollup's managed block to <c>acked</c> and its time to now, keeping the stream, from
    /// and response-path columns. A slug with no line gets none. False when nothing changed.
    /// </summary>
    bool AcknowledgeRollupLine(string pool, string team, string responsePath);

    /// <summary>The stream the member belongs to in the team's file; <c>main</c> when the member, the file or its stream is absent.</summary>
    string StreamOf(string pool, string team, string member);

    /// <summary>A stream as the team file records it: its branch and work tree, and its first member as owner (<c>—</c> when none).</summary>
    ProgressStreamInfo Describe(string pool, string team, string stream);

    /// <summary>The team's progress folder as it stands; every stream with a checklist or a journal is listed.</summary>
    TeamProgress Read(string pool, string team);

    /// <summary>Sets a stream's checklist to <c>State: done</c> when it has a <c>State:</c> to set; reports the open items it still lists.</summary>
    ProgressDone MarkDone(string pool, string team, string stream);

    /// <summary>The streams whose newest journaled report is newer than their checklist and has not been nudged yet.</summary>
    IReadOnlyList<ProgressNudge> PendingNudges(string pool, string team);

    /// <summary>Records that <paramref name="nudge"/> was raised, so it is raised once.</summary>
    void MarkNudged(string pool, string team, ProgressNudge nudge);

    /// <summary>Appends one session-inventory row to the team's <c>sessions.jsonl</c>.</summary>
    void AppendSession(string pool, string team, JsonObject row);

    /// <summary>
    /// Appends <paramref name="ev"/> to the team's <c>ledger.jsonl</c>, the one writer of it. Never throws: a failure is returned in
    /// <see cref="LedgerAppend.Error"/> and logged once as a hook error.
    /// </summary>
    LedgerAppend AppendLedger(string pool, string team, LedgerEvent ev);

    /// <summary>The team's ledger lines in file order; a line that does not parse is skipped, and a missing ledger is empty.</summary>
    IReadOnlyList<JsonValue> ReadLedger(string pool, string team);

    /// <summary>The release watch's prior tips of the pool, from the state database; null when none is stored, and throws when the database cannot be read.</summary>
    string? ReleaseSeen(string pool) => null;

    /// <summary>Stores the release watch's prior tips of the pool in the state database.</summary>
    void SaveReleaseSeen(string pool, string text)
    {
    }

    /// <summary>
    /// The recall digest of the team's ledger, checked against the live files (the msgs, the team file), or null when there is nothing to
    /// recall. <paramref name="includeLanes"/> adds one line per lane child team. Never throws.
    /// </summary>
    string? RecallDigest(string pool, string team, bool includeLanes);

    /// <summary>The most characters one recalled digest holds; a session recalling several teams gets as many whole digests as fit in this many.</summary>
    const int DigestMaxChars = 2000;

    /// <summary>
    /// Hands the new lines of each team ledger under <paramref name="pool"/> to the memory adapter the repo's config names (the Engram
    /// script under <paramref name="pluginRoot"/>, a command, or none for <c>file</c> and <c>none</c>); returns the lines delivered.
    /// Called by the daemon, never by a hook. Never throws. <c>alarm</c>, when given, is called with the team and a message once when a
    /// team's ledger has been refused for the stuck threshold of runs in a row, and not again until a run of that ledger succeeds.
    /// </summary>
    int ForwardLedgers(string pool, IProcessRunner runner, string pluginRoot, string? path, string repo, Action<string, string>? alarm = null);

    /// <summary>Closes the forwarding run in progress: the adapter children <see cref="ForwardLedgers"/> started since the last close and their summed wall milliseconds; the next run counts from zero.</summary>
    (long Children, long WallMs) EndForwardRun();

    /// <summary>The numbers <see cref="EndForwardRun"/> returned last, or zeros before any run has ended.</summary>
    (long Children, long WallMs) LastForwardRun();

    /// <summary>
    /// Records what a delivered report carries (its verdict, its decisions, a blocker opened or cleared) after the journal took it.
    /// A duplicate delivery records nothing. Never throws.
    /// </summary>
    void RecordReport(string pool, string team, string stream, string from, string responsePath, ProgressAppend appended);

    /// <summary>
    /// Records plan events for a stream: appends them to <c>&lt;stream&gt;.plan.jsonl</c>, then re-renders the stream's Checklist block
    /// from the log. The first event of a stream creates its checklist when absent and opts it into the plan.
    /// </summary>
    PlanWriteResult RecordPlan(string pool, string team, string stream, string by, IReadOnlyList<PlanInput> events, string? session = null);

    /// <summary>
    /// The checklist core's entry: like <see cref="RecordPlan"/> but a stream named <c>_run</c> is accepted (loose ends that belong to no
    /// stream). Only <c>ah checklist</c> reaches it.
    /// </summary>
    PlanWriteResult RecordChecklist(string pool, string team, string stream, string by, IReadOnlyList<PlanInput> events, string? session = null);

    /// <summary>Opts an existing checklist into the plan: one <c>add</c> (and <c>start</c> or <c>done</c>) event per item line it holds, then renders it.</summary>
    PlanImportResult ImportPlan(string pool, string team, string stream, string by, string? session = null);

    /// <summary>The stream's plan log folded; null when the stream has none.</summary>
    PlanState? ReadPlan(string pool, string team, string stream);

    /// <summary>Drops the plans <see cref="ReadPlan"/> and the stream lists <see cref="StreamNames"/> hold, so each is read again next time (the on-demand rebuild).</summary>
    void ForgetPlans()
    {
    }

    /// <summary>The names of the team's streams, in the order <see cref="Read"/> gives them (b459 C3: held on the team's document count).</summary>
    IReadOnlyList<string> StreamNames(string pool, string team) => [.. Read(pool, team).Streams.Select(static s => s.Stream)];

    /// <summary>The stream's plan log as text, one row a line; null when the stream has none.</summary>
    string? PlanLogText(string pool, string team, string stream) => null;

    /// <summary>The team's progress documents (checklists, journals, rollup, ...), in name order.</summary>
    IReadOnlyList<ProgressDoc> Docs(string pool, string team) => [];

    /// <summary>The teams of the pool that hold a progress document, in order.</summary>
    IReadOnlyList<string> DocTeams(string pool) => [];

    /// <summary>The progress document whose key (the path its file had) is <paramref name="key"/>, or null.</summary>
    ProgressDoc? DocByKey(string key) => null;
}

/// <summary>One event of a plan: what <c>ah progress item</c> was asked to record. <c>At</c> overrides the time (import).</summary>
public sealed record PlanInput(
    string Item,
    string Event,
    string? Title = null,
    IReadOnlyList<string>? Commits = null,
    string? Branch = null,
    int? Issue = null,
    string? Note = null,
    string? At = null,
    int? Rank = null,
    string? Area = null,
    string? Holder = null,
    string? Role = null,
    string? Todo = null,
    string? Ref = null)
{
    /// <summary>The line a <c>source</c> event records. No verb takes it from the command line; <c>ah msg new</c> writes its auto-created mark with it.</summary>
    public string? Source { get; init; }

    /// <summary>The backlog id of the item's parent for a <c>parent</c> event; empty clears it.</summary>
    public string? Parent { get; init; }

    /// <summary>The event that made the transition table write this row; null for a hand-written (override) row.</summary>
    public PlanCause? Cause { get; init; }

    /// <summary>The sha the item's <c>ah/&lt;slug&gt;</c> branch stood at when it was dispatched; written on the dispatch's <c>ref</c> row.</summary>
    public string? Base { get; init; }

    /// <summary>The answers a <c>wait-user</c> event offers; no verb takes any other event's.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>The user's answer a <c>wait-orch</c> event records. No verb flag writes it.</summary>
    public string? Answer { get; init; }

    /// <summary>The member (<c>name@team</c>) a <c>wait-user</c> event asks for, who also gets the answer.</summary>
    public string? AskedFor { get; init; }

    /// <summary>How urgent a <c>wait-user</c> event's question is: <c>blocker</c>, <c>soon</c> or <c>eventually</c>.</summary>
    public string? Class { get; init; }

    /// <summary>The checklist item (<c>stream/id</c>) an <c>after</c> event names; empty clears it.</summary>
    public string? After { get; init; }

    /// <summary>On a <c>report</c> row: the DONE's sha was dropped because it equals the item's base, so nothing was committed (ah2-312 M3'').</summary>
    public bool AtBase { get; init; }
}

/// <summary>What recording plan events did: nothing (progress is off), a refusal with nothing written, or the count appended and, in <c>Warning</c>, why the Checklist block could not be rewritten afterwards (null when it was).</summary>
public sealed record PlanWriteResult(bool Off, int Appended, string? Refused, string? Warning);

/// <summary>What importing a checklist did: the items it added, the lines it could not read, or why it did nothing.</summary>
public sealed record PlanImportResult(bool Off, int Items, IReadOnlyList<string> Unparsed, string? Refused, string? Warning);

internal sealed class ProgressRecords(IFileReader files, IClock clock, IHierarchyStateCalls calls, IRosterCore roster, HierarchyCore core, IHookErrorLog? errors = null, Func<string?>? userHome = null, IProgressLogSource? logs = null, IProgressDocSource? docs = null) : IHierarchyProgress
{
    private static readonly StateCallOptions HookCall = new(Hook: true);

    /// <summary>A response's stored text.</summary>
    private string ReadMsg(string path)
    {
        using var db = core.DbMsgs();
        return HierarchyCore.ReadMsgText(path, db);
    }

    /// <summary>
    /// A checklist counts as edited after a report only when its file was modified more than this long after the entry. The report
    /// that creates a checklist writes the file within the same operation, so its modified time lands just past the entry time.
    /// </summary>
    private const double EditGraceSeconds = 2;

    private readonly ProgressReader _reader = new(files, roster, logs, docs);

    public IReadOnlyList<ProgressDoc> Docs(string pool, string team) => docs?.Docs(pool, team) ?? [];

    public IReadOnlyList<string> DocTeams(string pool) => docs?.Teams(pool) ?? [];

    public ProgressDoc? DocByKey(string key) => docs?.ByKey(key);

    private string Now() => Timestamps.LocalIso(clock);

    private static JsonObject Args(string pool, string team) => new() { ["dir"] = pool, ["team"] = team };

    public bool IsOff(string pool, string team) => _reader.IsOff(pool, team);

    public ProgressStreamInfo Describe(string pool, string team, string stream) => _reader.Describe(pool, team, stream);

    public TeamProgress Read(string pool, string team) => _reader.Read(pool, team);

    public PlanState? ReadPlan(string pool, string team, string stream) => _reader.ReadPlan(pool, team, stream);

    public void ForgetPlans() => _reader.ForgetPlans();

    public IReadOnlyList<string> StreamNames(string pool, string team) => _reader.StreamNames(pool, team);

    /// <summary>How many plan reads folded rows, for the tests of the plan hold.</summary>
    internal long PlanFolds => _reader.PlanFolds;

    public string? PlanLogText(string pool, string team, string stream) => _reader.PlanText(pool, team, stream);

    private List<string> Create(string pool, string team, IEnumerable<KeyValuePair<string, string>> items)
    {
        var made = new List<string>();
        foreach (var (file, content) in items)
        {
            var args = Args(pool, team);
            args["file"] = file;
            args["content"] = content;
            var result = calls.Progress("create-if-absent", args);
            if (result.Member("off") is { IsTruthy: true })
            {
                return [];
            }

            if (result.Member("created") is { IsTruthy: true })
            {
                made.Add(file);
            }
        }

        return made;
    }

    private string StreamMd(string stream, ProgressStreamInfo info, string? updated = null) =>
        ProgressFormat.StreamTemplate(stream, info.Goal, info.Owner, info.Branch, info.WorkTree, updated ?? Now());

    public IReadOnlyList<string> EnsureTeam(string pool, string team, ProgressStreamInfo main) =>
        Create(pool, team, [new("rollup.md", ProgressFormat.RollupTemplate(team, Now())), new("main.md", StreamMd("main", main))]);

    public IReadOnlyList<string> EnsureStream(string pool, string team, string stream, ProgressStreamInfo info) =>
        Create(pool, team, [new(stream + ".md", StreamMd(stream, info))]);

    public string StreamOf(string pool, string team, string member)
    {
        var record = roster.ReadTeamRecord(pool, ProgressReader.TeamFileOf(team));
        if (!record.IsNull && record.Member("members") is { IsArray: true } members)
        {
            foreach (var m in members.AsArray())
            {
                if (m.Member("name") is { IsText: true } name && name.AsString() == member && m.Member("stream") is { IsText: true, } s && s.AsString().Length > 0)
                {
                    return s.AsString();
                }
            }
        }

        return "main";
    }

    public ProgressAppend AppendReport(string pool, string team, string stream, ProgressStreamInfo info, string from, string responsePath)
    {
        var parts = NodePath.Basename(responsePath).Split("--");
        if (parts.Length < 4)
        {
            throw new InvalidOperationException($"{NodePath.Basename(responsePath)} is not a <id>--<to>--<slug>--response.md file");
        }

        var requestId = parts[0];
        var kind = parts[^2];
        var marker = responsePath.IndexOf("/.claude/ah2/", StringComparison.Ordinal);
        var reportLabel = marker >= 0 ? responsePath[(marker + 1)..] : NodePath.Basename(responsePath);
        var text = ReadMsg(responsePath);
        var extract = ProgressFormat.ExtractReport(text);
        var tagged = new JsonArray();
        foreach (var (tag, line) in extract.Tagged)
        {
            tagged.Add(new JsonObject { ["tag"] = tag, ["text"] = line });
        }

        var args = Args(pool, team);
        args["stream"] = stream;
        args["request_id"] = requestId;
        args["sha"] = ProgressFormat.Sha256(text);
        args["from"] = from;
        args["kind"] = kind;
        var instant = clock.UtcNow;
        args["time"] = Timestamps.LocalIso(clock, instant);
        args["report_path"] = reportLabel;
        args["status"] = extract.Status is { } status ? status : JsonValue.Null;
        args["tagged"] = tagged;
        args["stream_md"] = StreamMd(stream, info, Timestamps.LocalIso(clock, instant.AddSeconds(-1)));
        args["rollup_md"] = ProgressFormat.RollupTemplate(team, Now());
        var result = calls.Progress("journal-append", args);
        bool Flag(string key) => result.Member(key) is { IsTruthy: true };
        return new ProgressAppend(Flag("appended"), Flag("duplicate"), Flag("refiled"), Flag("off"), result.Member("skipped") is { IsText: true } s ? s.AsString() : null);
    }

    public bool UpdateRollupBlock(string pool, string team, string stream, string from, string responsePath)
    {
        var parts = NodePath.Basename(responsePath).Split("--");
        if (parts.Length < 4)
        {
            throw new InvalidOperationException($"{NodePath.Basename(responsePath)} is not a <id>--<to>--<slug>--response.md file");
        }

        var marker = responsePath.IndexOf("/.claude/ah2/", StringComparison.Ordinal);
        var args = Args(pool, team);
        args["stream"] = stream;
        args["slug"] = parts[^2];
        args["status"] = ProgressFormat.ExtractReport(ReadMsg(responsePath)).Status is { } status ? status : "-";
        args["from"] = from;
        args["time"] = clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        args["path"] = marker >= 0 ? responsePath[(marker + 1)..] : NodePath.Basename(responsePath);
        return calls.Progress("rollup-block-upsert", args).Member("updated") is { IsTruthy: true };
    }

    public bool AcknowledgeRollupLine(string pool, string team, string responsePath)
    {
        var parts = NodePath.Basename(responsePath).Split("--");
        if (parts.Length < 4)
        {
            return false;
        }

        var args = Args(pool, team);
        args["slug"] = parts[^2];
        args["ack"] = true;
        args["time"] = clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return calls.Progress("rollup-block-upsert", args).Member("updated") is { IsTruthy: true };
    }

    public ProgressDone MarkDone(string pool, string team, string stream)
    {
        if (IsOff(pool, team))
        {
            return new ProgressDone(false, 0, "progress is off");
        }

        var text = _reader.DocText(pool, team, stream + ".md");
        if (text is null)
        {
            return new ProgressDone(false, 0, "it has no checklist");
        }

        var open = ProgressFormat.ReadChecklist(text).InProgress;
        var args = Args(pool, team);
        args["stream"] = stream;
        args["expect_sha"] = ProgressFormat.Sha256(text);
        args["state"] = "done";
        var result = calls.Progress("state-cas", args);
        if (result.Member("changed") is { IsTruthy: true })
        {
            return new ProgressDone(true, open, null);
        }

        var reason = result.Member("off") is { IsTruthy: true } ? "progress is off"
            : result.Member("conflict") is { IsTruthy: true } ? "the checklist changed while it was being read"
            : result.Member("no_state") is { IsTruthy: true } ? "the checklist has no State: line"
            : "the checklist was written by a newer ah";
        return new ProgressDone(false, open, reason);
    }

    public IReadOnlyList<ProgressNudge> PendingNudges(string pool, string team)
    {
        var docs = _reader.Docs(pool, team);
        if (docs.Count == 0 || IsOff(pool, team))
        {
            return [];
        }

        var nudged = (docs.GetValueOrDefault(".nudged")?.Body ?? string.Empty)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToHashSet(StringComparer.Ordinal);
        var pending = new List<ProgressNudge>();
        foreach (var doc in docs.Values.Where(static d => d.Kind == "journal").OrderBy(static d => d.Name, StringComparer.Ordinal))
        {
            var stream = doc.Name[..^".journal.md".Length];
            var journal = doc.Body;
            if (!ProgressFormat.Writable(journal) || ProgressFormat.EntryHeads(journal) is not [.., var newest])
            {
                continue;
            }

            var checklistDoc = docs.GetValueOrDefault(stream + ".md");
            var checklist = checklistDoc?.Body;
            var updated = checklist is null ? null : ProgressFormat.ReadChecklist(checklist).Updated;
            var modified = checklistDoc is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(checklistDoc.UpdatedMs);
            if (updated is { } u && u >= newest.Key)
            {
                continue;
            }

            if (modified is { } m && m > newest.Key.AddSeconds(EditGraceSeconds))
            {
                continue;
            }

            var updatedKey = updated is { } stamp ? stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : "none";
            if (nudged.Contains(stream + " " + newest.Value) || nudged.Any(l => l.StartsWith(stream + " ", StringComparison.Ordinal) && l.EndsWith(" " + updatedKey, StringComparison.Ordinal) && l.Split(' ').Length == 3))
            {
                continue;
            }

            if (ImplementReport(journal, newest.Value, pool) is not { } role)
            {
                continue;
            }

            var open = ReadPlan(pool, team, stream)?.Items.Where(ChecklistRules.IsOpen).Select(static i => i.Item).ToList() ?? [];
            if (open.Count == 0)
            {
                continue;
            }

            pending.Add(new ProgressNudge(stream, newest.Value, role, open, updatedKey));
        }

        return pending;
    }

    /// <summary>The roles whose reports finish checklist items. The built-in "implement" class also holds qa, whose report finishes no item, so the class is not used.</summary>
    private static readonly HashSet<string> BuildingRoles = new(StringComparer.Ordinal) { "implementor", "ux-engineer", "docs-writer" };

    /// <summary>
    /// The role of the sender of report <paramref name="reportId"/> when it is an implement-class role whose report is not BLOCKED or NEEDS-…;
    /// null for any other sender, a missing or unreadable report file, or a report whose first status line starts BLOCKED or NEEDS.
    /// Reads the first 40 lines of the file the journal entry links.
    /// </summary>
    private string? ImplementReport(string journal, string reportId, string pool)
    {
        string? link = null;
        var inEntry = false;
        foreach (var raw in journal.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inEntry = line.EndsWith(" " + reportId, StringComparison.Ordinal) || line.EndsWith(" " + reportId + " (refiled)", StringComparison.Ordinal);
            }
            else if (inEntry && line.StartsWith("- Report: ", StringComparison.Ordinal))
            {
                link = line["- Report: ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(link))
        {
            return null;
        }

        var path = link.StartsWith('/') ? link : NodePath.Join(NodePath.Dirname(NodePath.Dirname(pool)), link);
        string? text;
        try
        {
            text = _reader.ReadIfPresent(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (text is null)
        {
            return null;
        }

        var lines = text.Split('\n').Take(40).Select(static l => l.TrimEnd('\r')).ToList();
        var from = lines.Take(20).FirstOrDefault(static l => l.StartsWith("from:", StringComparison.Ordinal))?["from:".Length..].Trim().Split('@')[0];
        if (from is null || !BuildingRoles.Contains(from))
        {
            return null;
        }

        var status = lines.Select(static l => l.TrimStart()).FirstOrDefault(static l => l.StartsWith("- [1] status:", StringComparison.Ordinal));
        var value = status is null ? string.Empty : status["- [1] status:".Length..].Trim();
        return value.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) || value.StartsWith("NEEDS", StringComparison.OrdinalIgnoreCase) ? null : from;
    }

    public void MarkNudged(string pool, string team, ProgressNudge nudge)
    {
        ArgumentNullException.ThrowIfNull(nudge);
        var args = Args(pool, team);
        args["stream"] = nudge.Stream;
        args["report_id"] = nudge.ReportId;
        if (nudge.Updated.Length > 0)
        {
            args["updated"] = nudge.Updated;
        }

        calls.Progress("nudged-append", args, HookCall);
    }

    public void AppendSession(string pool, string team, JsonObject row)
    {
        var args = Args(pool, team);
        args["row"] = row;
        calls.Progress("session-append", args, HookCall);
    }

    public LedgerAppend AppendLedger(string pool, string team, LedgerEvent ev)
    {
        try
        {
            if (MemoryOf(pool).Mode == MemoryMode.None)
            {
                return new LedgerAppend(false, true, null);
            }

            var args = Args(pool, team);
            args["row"] = new JsonObject
            {
                ["at"] = Timestamps.ToIsoString(clock.UtcNow),
                ["kind"] = ev.Kind,
                ["team"] = team,
                ["stream"] = Nullable(ev.Stream),
                ["sha"] = Nullable(ev.Sha),
                ["base"] = Nullable(ev.Base),
                ["ref"] = Nullable(ev.Ref),
                ["verdict"] = Nullable(ev.Verdict),
                ["slug"] = Nullable(ev.Slug),
                ["into"] = Nullable(ev.Into),
                ["tag"] = ev.Tag is { } tag ? tag : JsonValue.Null,
                ["confirmed"] = ev.Confirmed is { } confirmed ? confirmed : JsonValue.Null,
                ["text"] = ev.Text,
                ["source"] = new JsonObject
                {
                    ["type"] = ev.SourceType,
                    ["path"] = Nullable(ev.SourcePath),
                    ["verb"] = Nullable(ev.SourceVerb),
                    ["tool_use_id"] = Nullable(ev.ToolUseId),
                },
            };
            if (ev.Phase is not null)
            {
                args["row"].AsObject()["phase"] = ev.Phase;
            }

            if (ev.Result is not null)
            {
                args["row"].AsObject()["result"] = ev.Result;
            }

            var options = ev.ToolUseId is { Length: > 0 } toolUse
                ? new StateCallOptions(OperationId: $"{toolUse}:{ev.Kind}:{ev.Ref}:{team}", Hook: true)
                : HookCall;
            var result = calls.Progress("ledger-append", args, options);
            return result.Member("off") is { IsTruthy: true } ? new LedgerAppend(false, true, null) : new LedgerAppend(true, false, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            errors?.Log("ledger-append", e.Message);
            return new LedgerAppend(false, false, e.Message);
        }
    }

    public string? ReleaseSeen(string pool) => logs?.ReleaseSeen(pool);

    public void SaveReleaseSeen(string pool, string text) => calls.Progress("release-seen", new JsonObject { ["dir"] = pool, ["text"] = text });

    public JsonValue SetOff(string pool, string team, bool off) => calls.Progress("off-set", new JsonObject { ["dir"] = pool, ["team"] = team, ["off"] = off });

    public IReadOnlyList<JsonValue> ReadLedger(string pool, string team)
    {
        var rows = new List<JsonValue>();
        var text = _reader.LogText(pool, team, "ledger.jsonl");
        if (text is null)
        {
            return rows;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (JsonText.Parse(line) is { IsMap: true } row)
                {
                    rows.Add(row);
                }
            }
            catch (JsonParseException)
            {
                // a torn or foreign line is not a record
            }
        }

        return rows;
    }

    public string? RecallDigest(string pool, string team, bool includeLanes)
    {
        try
        {
            if (MemoryOf(pool).Mode == MemoryMode.None)
            {
                return null;
            }

            var record = roster.ReadTeamRecord(pool, ProgressReader.TeamFileOf(team));
            var live = new DigestLive(
                path => files.Exists(path) ? files.ReadText(path) : null,
                stream => !record.IsNull && record.Member("streams")?.Member(stream)?.Member("state") is { IsText: true } state ? state.AsString() : null,
                member => !record.IsNull && record.Member("members") is { IsArray: true } list
                    && list.AsArray().Any(m => m.Member("name") is { IsText: true } n && n.AsString() == member));
            var lanes = new List<KeyValuePair<string, string>>();
            if (includeLanes)
            {
                var streams = !record.IsNull && record.Member("streams") is { IsMap: true } map ? map.AsObject().Keys : [];
                foreach (var stream in streams)
                {
                    if (roster.FindChildTeam(pool, team, stream) is { Length: > 0 } child
                        && LedgerDigest.LastEvent(ReadLedger(pool, child)) is { } last)
                    {
                        lanes.Add(new(child, last));
                    }
                }
            }

            return LedgerDigest.Render(team, ReadLedger(pool, team), live, lanes);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            errors?.Log("ledger-recall", e.Message);
            return null;
        }
    }

    private readonly StuckLedgers stuckLedgers = new();

    private MemorySpec MemoryOf(string pool) => MemoryConfig.Read(files, pool, userHome?.Invoke());

    private readonly ForwardStats forwardStats = new();

    public (long Children, long WallMs) EndForwardRun() => forwardStats.EndRun();

    public (long Children, long WallMs) LastForwardRun() => forwardStats.Last();

    public int ForwardLedgers(string pool, IProcessRunner runner, string pluginRoot, string? path, string repo, Action<string, string>? alarm = null)
    {
        try
        {
            return new MemoryForwarder(logs, calls, runner, stuckLedgers, message => errors?.Log("ledger-forward", message), forwardStats, path, alarm, IsOff)
                .Run(pool, () => MemoryForwarder.Resolve(files, MemoryOf(pool), pluginRoot, path), repo);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            errors?.Log("ledger-forward", e.Message);
            return 0;
        }
    }

    public void RecordReport(string pool, string team, string stream, string from, string responsePath, ProgressAppend appended)
    {
        if (appended.Duplicate || appended.Off || appended.Skipped is not null)
        {
            return;
        }

        try
        {
            var name = NodePath.Basename(responsePath).Split("--");
            if (name.Length < 4)
            {
                return;
            }

            var text = ReadMsg(responsePath);
            var extract = ProgressFormat.ExtractReport(text);
            var events = LedgerEvents.FromReport(extract, stream, name[2], RoleOf(pool, team, from) == "qa", responsePath, text, appended.Refiled);
            if (!LedgerEvents.IsBlocked(extract.Status) && OpenBlocker(ReadLedger(pool, team), name[0]))
            {
                events.Add(new LedgerEvent("blocker-cleared", Stream: stream, Slug: name[2], Text: "cleared by " + (extract.Status ?? "a later report"), SourceType: "report", SourcePath: responsePath));
            }

            foreach (var ev in events)
            {
                AppendLedger(pool, team, ev);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            errors?.Log("ledger-report", e.Message);
        }
    }

    private static bool OpenBlocker(IReadOnlyList<JsonValue> rows, string requestId)
    {
        var open = false;
        foreach (var row in rows)
        {
            if (row.Member("source")?.Member("path") is { IsText: true } path && LedgerEvents.RequestIdOf(path.AsString()) == requestId
                && row.Member("kind") is { IsText: true } kind)
            {
                open = kind.AsString() == "blocker" || (open && kind.AsString() != "blocker-cleared");
            }
        }

        return open;
    }

    private string? RoleOf(string pool, string team, string member)
    {
        var record = roster.ReadTeamRecord(pool, ProgressReader.TeamFileOf(team));
        if (!record.IsNull && record.Member("members") is { IsArray: true } members)
        {
            foreach (var m in members.AsArray())
            {
                if (m.Member("name") is { IsText: true } name && name.AsString() == member && m.Member("role") is { IsText: true } role)
                {
                    return role.AsString();
                }
            }
        }

        return null;
    }

    private static JsonValue Nullable(string? value) => value is null ? JsonValue.Null : value;


    private PlanWriteResult Record(string pool, string team, string stream, string by, string? session, IReadOnlyList<PlanInput> events, bool import, bool bareDone = false)
    {
        if (IsOff(pool, team))
        {
            return new PlanWriteResult(true, 0, null, null);
        }

        var run = stream == ProgressFormat.RunStream;
        if (!run && _reader.DocText(pool, team, stream + ".md") is null)
        {
            _ = EnsureStream(pool, team, stream, Describe(pool, team, stream));
        }

        var now = ProgressFormat.PlanTime(clock.UtcNow);
        var rows = new JsonArray();
        foreach (var e in events)
        {
            rows.Add(ProgressFormat.PlanRow(e.At ?? now, team, stream, e.Item, e.Event, by, e.Title, e.Commits, e.Branch, e.Issue, e.Note, session, e.Rank, e.Area, e.Holder, e.Role, e.Todo, e.Ref, e.Source, e.Parent, e.Cause, e.Base, e.Options, e.Answer, e.AskedFor, e.Class, e.After, e.AtBase));
        }

        var append = Args(pool, team);
        append["stream"] = stream;
        append["checklist"] = run;
        append["rows"] = rows;
        append["import"] = import;
        append["bare_done"] = bareDone;
        var appended = calls.Progress("plan-append", append);
        if (appended.Member("off") is { IsTruthy: true })
        {
            return new PlanWriteResult(true, 0, null, null);
        }

        if (appended.Member("refused") is { IsText: true } refused)
        {
            return new PlanWriteResult(false, 0, refused.AsString(), null);
        }

        var count = appended.Member("appended") is { IsNumber: true } n ? (int)n.AsNumber() : 0;
        if (count > 0 && !import)
        {
            RegisterAdded(pool, team, stream, rows);
        }

        if (run)
        {
            return new PlanWriteResult(false, count, null, null);
        }

        var rewrite = Args(pool, team);
        rewrite["stream"] = stream;
        rewrite["updated"] = Now();
        rewrite["optin"] = !(_reader.DocText(pool, team, stream + ".md") is { } before && ProgressFormat.IsPlanned(before));
        var written = calls.Progress("checklist-rewrite", rewrite);
        var warning = written.Member("rewritten") is { IsTruthy: true } ? null
            : written.Member("skipped") is { IsText: true } ? "the checklist was written by a newer ah; it was not re-rendered"
            : written.Member("no_checklist") is { IsTruthy: true } ? "the checklist has no ## Checklist block; it was not re-rendered"
            : "the checklist was not re-rendered";
        return new PlanWriteResult(false, count, null, warning);
    }

    /// <summary>Gives each added item its work id (ah-work-items §3.2). A failure here never fails the append: the next reconcile assigns the id.</summary>
    private void RegisterAdded(string pool, string team, string stream, JsonArray rows)
    {
        var register = new JsonArray();
        foreach (var row in rows)
        {
            if (ProgressFormat.PlanText(row, "event") == "add" && ProgressFormat.PlanText(row, "item") is { } item)
            {
                register.Add(Work.RegisterRow(team, stream, item, ProgressFormat.PlanText(row, "ts")));
            }
        }

        if (register.Count == 0)
        {
            return;
        }

        try
        {
            if (calls.Progress(Work.WriteOp, new JsonObject { ["dir"] = pool, ["register"] = register }).Member("needs_backfill") is { IsTruthy: true })
            {
                Work.Reconcile(this, calls, pool, known: null);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errors?.Log("work-register", e.Message);
        }
    }

    public PlanWriteResult RecordPlan(string pool, string team, string stream, string by, IReadOnlyList<PlanInput> events, string? session = null) =>
        stream.StartsWith('_') ? new PlanWriteResult(false, 0, "reserved-stream: names that start with _ are not streams", null) : Record(pool, team, stream, by, session, events, import: false);

    public PlanWriteResult RecordChecklist(string pool, string team, string stream, string by, IReadOnlyList<PlanInput> events, string? session = null) =>
        stream.StartsWith('_') && stream != ProgressFormat.RunStream
            ? new PlanWriteResult(false, 0, "reserved-stream: only _run is a checklist stream", null)
            : Record(pool, team, stream, by, session, events, import: false, bareDone: events.Any(static e => e.Event == "done" && e.Commits is not { Count: > 0 }));

    public PlanImportResult ImportPlan(string pool, string team, string stream, string by, string? session = null)
    {
        if (IsOff(pool, team))
        {
            return new PlanImportResult(true, 0, [], null, null);
        }

        if (stream.StartsWith('_'))
        {
            return new PlanImportResult(false, 0, [], "reserved-stream: names that start with _ are not streams", null);
        }

        if (_reader.PlanText(pool, team, stream) is not null)
        {
            return new PlanImportResult(false, 0, [], $"stream {stream} already has a plan; import is a one-time opt-in", null);
        }

        var text = _reader.DocText(pool, team, stream + ".md");
        if (text is null)
        {
            return new PlanImportResult(false, 0, [], $"stream {stream} has no checklist to import", null);
        }

        if (!ProgressFormat.Writable(text))
        {
            return new PlanImportResult(false, 0, [], $"stream {stream}'s checklist was written by a newer ah", null);
        }

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var now = ProgressFormat.PlanTime(clock.UtcNow);
        var events = new List<PlanInput>();
        var unparsed = new List<string>();
        foreach (var line in ProgressFormat.ReadImportLines(text))
        {
            var slug = ProgressFormat.ImportSlug(line.Title, taken);
            events.Add(new PlanInput(slug, "add", line.Title));
            if (line.Section == ProgressFormat.SectionProgress)
            {
                events.Add(new PlanInput(slug, "start"));
            }
            else if (line.Section == ProgressFormat.SectionDone)
            {
                events.Add(new PlanInput(slug, "done", Commits: line.Commits, Note: line.DoneAt is null ? "imported; done time unknown" : null, At: line.DoneAt ?? now));
            }

            if (line.Unparsed is { } why)
            {
                unparsed.Add($"{slug}: {why}");
            }
        }

        var result = Record(pool, team, stream, by, session, events, import: true);
        return new PlanImportResult(result.Off, result.Appended == 0 ? 0 : taken.Count, unparsed, result.Refused, result.Warning);
    }
}
