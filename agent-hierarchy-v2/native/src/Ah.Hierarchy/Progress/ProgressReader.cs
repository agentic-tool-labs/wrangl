using Ah.Abstractions;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Hierarchy;

/// <summary>The reads of a team's progress folder, shared by the progress records and the progress read ops: they need the file reader and the team files, nothing else.</summary>
internal sealed class ProgressReader(IFileReader files, IRosterCore roster, IProgressLogSource? logs = null, IProgressDocSource? docs = null)
{
    internal static string Folder(string pool, string team) => NodePath.Join(pool, "progress", team);

    internal static string? TeamFileOf(string team) => team is "_default" or "" ? null : team;

    internal string? ReadIfPresent(string path) => files.Exists(path) ? files.ReadText(path) : null;

    /// <summary>The team's progress documents (per-dir-files-to-db S11d: rows of <c>progress_docs</c>), by old file name; none when the database is too busy to answer.</summary>
    internal IReadOnlyDictionary<string, ProgressDoc> Docs(string pool, string team) =>
        Busy(() => docs?.Docs(pool, team) ?? [], []).ToDictionary(static d => d.Name, StringComparer.Ordinal);

    /// <summary>The body of the team's document <paramref name="name"/>, or null (also when the database is busy).</summary>
    internal string? DocText(string pool, string team, string name) => Busy(() => docs?.Doc(pool, team, name)?.Body, null);

    private static T Busy<T>(Func<T> read, T busy)
    {
        try
        {
            return read();
        }
        catch (Ah.Core.Json.StoreUnavailableException)
        {
            return busy;
        }
    }

    /// <summary>The streams the team's documents name: each journal's and each checklist's, in order.</summary>
    internal static IReadOnlyList<string> StreamsOf(IEnumerable<ProgressDoc> docs) => [.. docs
        .Select(static d => d.Kind switch { "journal" => d.Name[..^".journal.md".Length], "checklist" => d.Name[..^".md".Length], _ => null })
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)];

    /// <summary>The text of the team's log <paramref name="log"/> (per-dir-files-to-db S11c: its rows, one line each), or null when it has none.</summary>
    internal string? LogText(string pool, string team, string log) => LogText(logs?.Rows(pool, team, log) ?? []);

    internal static string? LogText(IReadOnlyList<ProgressLogRow> rows) => rows.Count == 0 ? null : string.Concat(rows.Select(static r => r.Body + "\n"));

    internal string? PlanText(string pool, string team, string stream) => LogText(pool, team, stream + ".plan.jsonl");

    public bool IsOff(string pool, string team) => docs?.IsOff(pool, team) ?? false;

    public ProgressStreamInfo Describe(string pool, string team, string stream)
    {
        var record = roster.ReadTeamRecord(pool, TeamFileOf(team));
        string branch = "—", workTree = "—", owner = "—";
        if (!record.IsNull)
        {
            if (record.Member("streams")?.Member(stream) is { IsMap: true } s)
            {
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
        }

        return new ProgressStreamInfo(owner, branch, workTree, null);
    }

    private StreamProgress ReadStream(string pool, string team, IReadOnlyDictionary<string, ProgressDoc> docs, string stream)
    {
        var checklist = docs.GetValueOrDefault(stream + ".md")?.Body;
        var journal = docs.GetValueOrDefault(stream + ".journal.md")?.Body;
        var read = checklist is null ? null : ProgressFormat.ReadChecklist(checklist);
        var newest = journal is null ? null : ProgressFormat.EntryTimes(journal).Cast<DateTimeOffset?>().Max();
        var newer = (checklist is not null && !ProgressFormat.Writable(checklist)) || (journal is not null && !ProgressFormat.Writable(journal));
        var plan = PlanText(pool, team, stream);
        List<string> warnings = [.. read?.Warnings ?? []];
        if (plan is not null && checklist is not null && ProgressFormat.IsPlanned(checklist) && !ProgressFormat.ChecklistMatches(checklist, ProgressFormat.FoldPlan(plan)))
        {
            warnings.Add("checklist edited by hand; the plan wins");
        }

        return new StreamProgress(
            stream,
            checklist is not null,
            journal is not null,
            read?.Done ?? 0,
            read?.InProgress ?? 0,
            read?.NotStarted ?? 0,
            read?.State,
            read?.Updated,
            newest,
            ProgressFormat.IsStale(read?.Updated, newest),
            newer,
            warnings,
            plan is not null,
            checklist is null ? null : ProgressFormat.ChecklistTitle(checklist, stream));
    }

    public TeamProgress Read(string pool, string team)
    {
        var docs = Docs(pool, team);
        if (docs.Count == 0)
        {
            return new TeamProgress(IsOff(pool, team), false, [], []);
        }

        var streams = StreamsOf(docs.Values).Select(s => ReadStream(pool, team, docs, s)).ToList();
        var rollup = docs.GetValueOrDefault("rollup.md")?.Body;
        var newer = new List<string>();
        if (rollup is not null && !ProgressFormat.Writable(rollup))
        {
            newer.Add("rollup.md");
        }

        foreach (var s in streams.Where(s => s.NewerFormat))
        {
            newer.Add(s.Stream);
        }

        return new TeamProgress(IsOff(pool, team), rollup is not null, streams, newer);
    }

    /// <summary>
    /// The plan of <paramref name="stream"/> (b459 step 27): its fold is held per (pool, team, stream) with the seq of the last row it took,
    /// and a read folds only the rows appended since. A read also takes the held last row again: when it is gone or changed (the log was
    /// dropped, or dropped and written anew, b459 step 27c), the hold is dropped and the stream folds from scratch, so this equals a fresh fold.
    /// </summary>
    public PlanState? ReadPlan(string pool, string team, string stream)
    {
        if (logs is null)
        {
            return null;
        }

        var key = (pool, team, stream);
        var (held, rows) = Since(key);
        if (held is not null && rows.Count == 0)
        {
            return held.Plan;
        }

        if (rows.Count == 0)
        {
            return null;
        }

        // a held fold is fed by one reader at a time: the reader that takes the gate reads from where the hold now stands
        lock (_foldGate)
        {
            (held, rows) = Since(key);
            if (rows.Count == 0)
            {
                return held?.Plan;
            }

            _ = Interlocked.Increment(ref _planFolds);
            var fold = held?.Fold ?? new PlanFold();
            try
            {
                fold.ApplyText(LogText(rows));
            }
            catch
            {
                // a fold that threw part-way is not one to build on: the next read folds the stream from scratch
                lock (_plans)
                {
                    _ = _plans.Remove(key);
                }

                throw;
            }
            var next = new HeldPlan(fold, rows[^1], fold.State);
            lock (_plans)
            {
                _plans[key] = next;
            }

            return next.Plan;
        }
    }

    /// <summary>The stream's hold and the rows after it; no hold and every row when the held last row is no longer in the log as it was.</summary>
    private (HeldPlan? Held, IReadOnlyList<ProgressLogRow> Rows) Since((string Pool, string Team, string Stream) key)
    {
        HeldPlan? held;
        lock (_plans)
        {
            _ = _plans.TryGetValue(key, out held);
        }

        var log = key.Stream + ".plan.jsonl";
        if (held is null)
        {
            return (null, logs!.Rows(key.Pool, key.Team, log));
        }

        var rows = logs!.Rows(key.Pool, key.Team, log, held.Last.Seq - 1);
        if (rows.Count > 0 && rows[0] == held.Last)
        {
            return (held, rows.Skip(1).ToList());
        }

        lock (_plans)
        {
            _ = _plans.Remove(key);
        }

        return (null, logs.Rows(key.Pool, key.Team, log));
    }

    private readonly object _foldGate = new();

    /// <summary>Forgets every held plan and stream list: the doctor's rebuild.</summary>
    internal void ForgetPlans()
    {
        lock (_plans)
        {
            _plans.Clear();
        }

        lock (_streams)
        {
            _streams.Clear();
        }
    }

    /// <summary>
    /// The team's stream names as <see cref="Read"/> lists them (b459 C3), held on the team's document count: a name is only ever added by an
    /// insert. Read again when the count moves or cannot be had; a busy read is never held.
    /// </summary>
    internal IReadOnlyList<string> StreamNames(string pool, string team)
    {
        // read before the documents: a write between the two leaves the names on the older count, so the next read takes them again
        var stamp = docs?.Stamp(pool, team);
        if (stamp is not null)
        {
            lock (_streams)
            {
                if (_streams.TryGetValue((pool, team), out var held) && held.Stamp == stamp)
                {
                    return held.Names;
                }
            }
        }

        _ = Interlocked.Increment(ref _streamReads);
        IReadOnlyList<ProgressDoc> read;
        try
        {
            read = docs?.Docs(pool, team) ?? [];
        }
        catch (Ah.Core.Json.StoreUnavailableException)
        {
            return [];
        }

        IReadOnlyList<string> names = StreamsOf(read);
        if (stamp is not null)
        {
            lock (_streams)
            {
                _streams[(pool, team)] = (stamp, names);
            }
        }

        return names;
    }

    /// <summary>How many stream lists were read rather than held, for the tests of the hold.</summary>
    internal long StreamReads => Interlocked.Read(ref _streamReads);

    private long _streamReads;

    private readonly Dictionary<(string Pool, string Team), (string Stamp, IReadOnlyList<string> Names)> _streams = [];

    /// <summary>How many reads folded rows rather than answering from the hold, for the tests of the hold.</summary>
    internal long PlanFolds => Interlocked.Read(ref _planFolds);

    private long _planFolds;

    private sealed record HeldPlan(PlanFold Fold, ProgressLogRow Last, PlanState Plan);

    private readonly Dictionary<(string Pool, string Team, string Stream), HeldPlan> _plans = [];
}
