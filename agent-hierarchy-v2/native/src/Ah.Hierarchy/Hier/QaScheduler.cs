using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>What an append to the QA store did: <paramref name="Conflict"/> names the jobs whose revision was stale, and nothing was written when it is not empty.</summary>
internal sealed record QaAppendResult(bool Ok, IReadOnlyList<string> Conflict);

/// <summary>The jobs of the QA store and the revision of its queue-epoch row, from one read.</summary>
internal sealed record QaSnapshot(IReadOnlyList<QaEntry> Entries, long Epoch);

/// <summary>
/// The jobs of the QA store as the scheduler sees them. An append is checked under the store's lock, so a stale snapshot is refused whole.
/// When <c>epoch</c> is given, the append also writes that revision of the queue-epoch row, so it conflicts with any other append that did.
/// </summary>
internal interface IQaStore
{
    QaSnapshot Read();

    QaAppendResult Append(IReadOnlyList<QaEntry> rows, long? epoch);
}

/// <summary>The QA store in the daemon: reads are lock-free, and an append goes through the <c>qa</c> state domain so its revision check runs under the domain lock.</summary>
internal sealed class HierQaStore(HierLib lib, Action<string>? log = null) : IQaStore
{
    public QaSnapshot Read()
    {
        var (jobs, epoch) = lib.QaSnapshotRows();
        var entries = new List<QaEntry>(jobs.Count);
        foreach (var row in jobs)
        {
            try
            {
                entries.Add(QaEntry.FromRow(row));
            }
            catch (InvalidOperationException e) when (e.Message.StartsWith("qa-bad-row", StringComparison.Ordinal))
            {
                // A row that fails validation can never be claimed; skipping it keeps one hand-edited line from wedging the queue.
                if (_reported.Add(e.Message))
                {
                    log?.Invoke($"qa store: skipping a row that fails validation: {e.Message}");
                }
            }
        }

        return new QaSnapshot(entries, epoch);
    }

    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public QaAppendResult Append(IReadOnlyList<QaEntry> rows, long? epoch) => lib.QaAppendRouted(rows, epoch);
}

/// <summary>The admission settings: how many non-full sets run together, and the load and memory a claim needs.</summary>
internal sealed record QaSettings(int Parallel, double AdmitLoadRatio, double AdmitFreeMemGiB)
{
    private const string ParallelKey = "parallel";
    private const string LoadRatioKey = "admit_load_ratio";
    private const string FreeMemKey = "admit_free_mem_gib";

    internal static QaSettings Default { get; } = new(2, 0.75, 4);

    /// <summary>
    /// The settings in the <c>qa</c> object of <c>agent-hierarchy.json</c> files, later files overriding earlier ones. A key no file sets keeps
    /// its default silently; a key a file gives an invalid value keeps the earlier value (or the default) and adds one warning line naming it.
    /// </summary>
    internal static (QaSettings Settings, IReadOnlyList<string> Warnings) Read(IFileReader files, IEnumerable<string> paths)
    {
        var result = Default;
        var warned = new Dictionary<string, string>(StringComparer.Ordinal);

        void Take(JsonObject qa, string key, Func<JsonValue, bool> valid, string bounds, Func<QaSettings, JsonValue, QaSettings> set)
        {
            if (qa.Get(key) is not { } value)
            {
                return;
            }

            if (valid(value))
            {
                result = set(result, value);
            }
            else
            {
                warned[key] = $"qa.{key}: {JsonText.Stringify(value, 0)} is not {bounds}; using the default";
            }
        }

        foreach (var path in paths)
        {
            JsonObject? data;
            try
            {
                data = JsonText.Parse(files.ReadText(path)) is { IsMap: true } v ? v.AsObject() : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
            {
                continue;
            }

            if (data?.Get("qa") is not { IsMap: true } map)
            {
                continue;
            }

            var qa = map.AsObject();
            Take(qa, ParallelKey, v => v.IsNumber && double.IsFinite(v.AsNumber()) && v.AsNumber() == Math.Floor(v.AsNumber()) && v.AsNumber() >= 1 && v.AsNumber() <= int.MaxValue, "a whole number of at least 1", (s, v) => s with { Parallel = (int)v.AsNumber() });
            Take(qa, LoadRatioKey, v => v.IsNumber && double.IsFinite(v.AsNumber()) && v.AsNumber() > 0 && v.AsNumber() <= 4, "a number above 0 and at most 4", (s, v) => s with { AdmitLoadRatio = v.AsNumber() });
            Take(qa, FreeMemKey, v => v.IsNumber && double.IsFinite(v.AsNumber()) && v.AsNumber() >= 0, "a number of at least 0", (s, v) => s with { AdmitFreeMemGiB = v.AsNumber() });
        }

        var warnings = new List<string>();
        foreach (var key in new[] { ParallelKey, LoadRatioKey, FreeMemKey })
        {
            if (warned.TryGetValue(key, out var line))
            {
                warnings.Add(line);
            }
        }

        return (result, warnings);
    }
}

/// <summary>A stored passing result: the file it lives in and the status it holds.</summary>
internal sealed record QaCachedResult(string Path, string Verdict);

internal sealed record QaSubmission(string Repo, string Sha, string? Base, string Set, bool Fresh, string Priority, string Requester, string? Team, QaCachedResult? Cached = null);

/// <summary>The outcome of finishing or releasing a claim: the changed entry, or the error code.</summary>
internal sealed record QaChangeResult(QaEntry? Entry, string? Error);

/// <summary>The outcome of a submit: the entry that holds the request, or the error (<c>qa-store-busy</c>).</summary>
internal sealed record QaSubmitResult(QaEntry? Entry, string? Error);

/// <summary>The outcome of a claim: the claimed job, or the reason nothing was claimed (null when no job was claimable).</summary>
internal sealed record QaNextResult(QaEntry? Entry, string? Reason);

/// <summary>
/// Decides which queued QA job a claimer gets and keeps the store's jobs honest: dedupe on submit, priority and exclusivity, load and
/// memory admission, and the return of a lost claim. Every change is a read, a decision and an append of the next revision, repeated up to
/// three times when the store reports a stale revision.
/// </summary>
internal sealed class QaScheduler(IQaStore store, IHostProbe probe, IClock clock, QaSettings settings, Func<string, bool> isLive, Func<string> newId)
{
    internal const string StoreBusy = "qa-store-busy";

    private const int Attempts = 3;
    private const string Full = "full";
    private static readonly string[] Priorities = ["gate", "lane", "adhoc"];

    /// <summary>The rows to append, what the call answers, and whether the write changes the queue's shape (then it also bumps the queue epoch).</summary>
    private readonly record struct Plan<T>(IReadOnlyList<QaEntry> Rows, T Result, bool ShapeChange = false);

    private long NowMs() => clock.UtcNow.ToUnixTimeMilliseconds();

    private static int Rank(string priority) => Array.IndexOf(Priorities, priority);

    private static bool IsFull(QaEntry entry) => entry.Set == Full;

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="decide"/> over a fresh read and appends the rows it returns, again from a fresh read when the store says a revision was stale; null when every attempt was stale.</summary>
    private Plan<T>? Transact<T>(Func<IReadOnlyList<QaEntry>, Plan<T>> decide)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var snapshot = store.Read();
            var plan = decide(snapshot.Entries);
            if (plan.Rows.Count == 0 || store.Append(plan.Rows, plan.ShapeChange ? snapshot.Epoch + 1 : null).Ok)
            {
                return plan;
            }
        }

        return null;
    }

    /// <summary>Adds the request to the queued or running job for the same commit and set, or queues a new one. A fresh request never joins a job and is never joined.</summary>
    internal QaSubmitResult Submit(QaSubmission submission)
    {
        var plan = Transact(entries =>
        {
            var match = submission.Fresh
                ? null
                : entries.FirstOrDefault(e => !e.Fresh && e.Sha == submission.Sha && e.Set == submission.Set && e.State is "queued" or "claimed");
            if (match is null && !submission.Fresh && submission.Cached is { } cached)
            {
                var finished = new QaEntry(
                    newId(), 1, submission.Sha, submission.Base, submission.Set, submission.Repo, false, submission.Priority,
                    [submission.Requester], submission.Team, NowMs(), "done", null, null, cached.Path, cached.Verdict, NowMs(), 0, null, new JsonObject());
                return new Plan<QaEntry>([finished], finished);
            }

            if (match is null)
            {
                var created = new QaEntry(
                    newId(), 1, submission.Sha, submission.Base, submission.Set, submission.Repo, submission.Fresh, submission.Priority,
                    [submission.Requester], submission.Team, NowMs(), "queued", null, null, null, null, null, 0, null, new JsonObject());
                return new Plan<QaEntry>([created], created, ShapeChange: true);
            }

            var requesters = match.Requesters.Contains(submission.Requester) ? match.Requesters : [.. match.Requesters, submission.Requester];
            var priority = match.State == "queued" && Rank(submission.Priority) < Rank(match.Priority) ? submission.Priority : match.Priority;
            if (requesters == match.Requesters && priority == match.Priority)
            {
                return new Plan<QaEntry>([], match);
            }

            var joined = match with { Rev = match.Rev + 1, Requesters = requesters, Priority = priority };
            return new Plan<QaEntry>([joined], joined);
        });
        return plan is { } done ? new QaSubmitResult(done.Result, null) : new QaSubmitResult(null, StoreBusy);
    }

    /// <summary>
    /// Claims the first claimable queued job for <paramref name="claimer"/>, if the host has room for it. A job is claimable when no gate
    /// job waits ahead of a lane or adhoc one, a full set runs only alone and nothing runs beside it, and other sets stay within the
    /// parallel limit. The host is read once, and only to admit the job the order picked.
    /// </summary>
    internal QaNextResult Next(string claimer, bool allowFull)
    {
        HostSnapshot? snapshot = null;
        var plan = Transact(entries =>
        {
            var candidate = Candidate(entries, allowFull);
            if (candidate is null)
            {
                return new Plan<QaNextResult>([], new QaNextResult(null, null));
            }

            snapshot ??= probe.Read();
            if (Admission(candidate, snapshot) is { } reason)
            {
                return new Plan<QaNextResult>([], new QaNextResult(null, reason));
            }

            var claimed = candidate with { Rev = candidate.Rev + 1, State = "claimed", ClaimedBy = claimer, ClaimedAt = NowMs() };
            return new Plan<QaNextResult>([claimed], new QaNextResult(claimed, null), ShapeChange: true);
        });
        return plan?.Result ?? new QaNextResult(null, StoreBusy);
    }

    private QaEntry? Candidate(IReadOnlyList<QaEntry> entries, bool allowFull)
    {
        var running = entries.Where(e => e.State == "claimed").ToList();
        var gateWaiting = entries.Any(e => e.State == "queued" && e.Priority == "gate");
        var fullRunning = running.Any(IsFull);
        return entries
            .Where(e => e.State == "queued" && (e.Priority == "gate" || !gateWaiting))
            .Where(e => IsFull(e) ? allowFull && running.Count == 0 : !fullRunning && running.Count < settings.Parallel)
            .OrderBy(e => Rank(e.Priority))
            .ThenBy(e => e.Created)
            .FirstOrDefault();
    }

    private string? Admission(QaEntry candidate, HostSnapshot host)
    {
        if (host.LoadAverage1 is not { } load)
        {
            return "probe-unavailable: load";
        }

        if (host.AvailableMemoryGiB is not { } memory)
        {
            return "probe-unavailable: memory";
        }

        var limit = settings.AdmitLoadRatio * host.LogicalCores;
        if (IsFull(candidate))
        {
            limit = Math.Min(limit, 0.5 * host.LogicalCores);
        }

        if (!(load < limit))
        {
            return $"load-high: {Number(load)} >= {Number(limit)}";
        }

        return !(memory >= settings.AdmitFreeMemGiB) ?$"memory-low: {Number(memory)} < {Number(settings.AdmitFreeMemGiB)}" : null;
    }

    internal IReadOnlyList<QaEntry> Entries() => store.Read().Entries;

    /// <summary>
    /// Ends a claimed job for its claimer: <c>done</c> with the verdict and result path, or <c>failed</c> with the error line as the reason.
    /// Anyone but the claimer is refused <c>not-claimer</c>.
    /// </summary>
    internal QaChangeResult Finish(string id, string caller, string? resultPath, string? verdict, string? error)
    {
        var plan = Transact(entries =>
        {
            if (entries.FirstOrDefault(e => e.Id == id) is not { } entry)
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "qa-unknown-entry"));
            }

            if (entry.State != "claimed")
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "qa-not-claimed"));
            }

            if (entry.ClaimedBy != caller)
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "not-claimer"));
            }

            var ended = error is null
                ? entry with { Rev = entry.Rev + 1, State = "done", ResultPath = resultPath, Verdict = verdict, EndedAt = NowMs() }
                : entry with { Rev = entry.Rev + 1, State = "failed", Reason = error, EndedAt = NowMs() };
            return new Plan<QaChangeResult>([ended], new QaChangeResult(ended, null));
        });
        return plan?.Result ?? new QaChangeResult(null, StoreBusy);
    }

    /// <summary>Returns a claimed job to the queue when <paramref name="mayRelease"/> accepts its claimer.</summary>
    internal QaChangeResult Release(string id, Func<string, bool> mayRelease)
    {
        var plan = Transact(entries =>
        {
            if (entries.FirstOrDefault(e => e.Id == id) is not { } entry)
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "qa-unknown-entry"));
            }

            if (entry.State != "claimed" || entry.ClaimedBy is not { } claimer)
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "qa-not-claimed"));
            }

            if (!mayRelease(claimer))
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "not-allowed"));
            }

            var queued = entry with { Rev = entry.Rev + 1, State = "queued", ClaimedBy = null, ClaimedAt = null };
            return new Plan<QaChangeResult>([queued], new QaChangeResult(queued, null), ShapeChange: true);
        });
        return plan?.Result ?? new QaChangeResult(null, StoreBusy);
    }

    /// <summary>
    /// Closes a queued job as <c>withdrawn</c> with <paramref name="reason"/> when <paramref name="mayWithdraw"/> accepts it. A claimed job
    /// answers <c>qa-claimed</c> with the entry; a job that already ended answers its entry unchanged. No load gate.
    /// </summary>
    internal QaChangeResult Withdraw(string id, string? reason, Func<QaEntry, bool> mayWithdraw)
    {
        var plan = Transact(entries =>
        {
            if (entries.FirstOrDefault(e => e.Id == id) is not { } entry)
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "qa-unknown-entry"));
            }

            if (!mayWithdraw(entry))
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(null, "not-allowed"));
            }

            if (entry.State != "queued")
            {
                return new Plan<QaChangeResult>([], new QaChangeResult(entry, entry.State == "claimed" ? "qa-claimed" : null));
            }

            var withdrawn = entry with { Rev = entry.Rev + 1, State = "withdrawn", Reason = reason, EndedAt = NowMs() };
            return new Plan<QaChangeResult>([withdrawn], new QaChangeResult(withdrawn, null), ShapeChange: true);
        });
        return plan?.Result ?? new QaChangeResult(null, StoreBusy);
    }

    /// <summary>
    /// Returns each claimed job whose claimer is no longer live to the queue, and fails a job that loses its claimer a second time. Returns
    /// how many jobs changed; 0 when none did or the store stayed busy.
    /// </summary>
    internal int StalePass()
    {
        var plan = Transact(entries =>
        {
            var lost = entries
                .Where(e => e.State == "claimed" && e.ClaimedBy is { } claimer && !isLive(claimer))
                .Select(e => e.LostClaims >= 1
                    ? e with { Rev = e.Rev + 1, State = "failed", LostClaims = e.LostClaims + 1, EndedAt = NowMs(), Reason = "claimer-lost-twice" }
                    : e with { Rev = e.Rev + 1, State = "queued", LostClaims = e.LostClaims + 1, ClaimedBy = null, ClaimedAt = null })
                .ToList();
            return new Plan<int>(lost, lost.Count);
        });
        return plan?.Result ?? 0;
    }
}
