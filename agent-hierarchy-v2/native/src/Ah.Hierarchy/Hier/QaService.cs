using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>One job of the QA queue as the routes show it.</summary>
public sealed record QaJob(
    string Id,
    string Sha,
    string? Base,
    string Set,
    string Repo,
    bool Fresh,
    string Priority,
    IReadOnlyList<string> Requesters,
    string? Team,
    long Created,
    string State,
    string? ClaimedBy,
    long? ClaimedAt,
    string? ResultPath,
    string? Verdict,
    long? EndedAt,
    string? Reason);

/// <summary>What a QA call answers: the job it concerns, or an error <paramref name="Code"/> with its <paramref name="Message"/>; <paramref name="Reason"/> says why a claim returned no job.</summary>
public sealed record QaReply(QaJob? Job, string? Code, string? Message, string? Reason = null);

/// <summary>
/// The QA queue the daemon serves. Every call first returns the jobs of dead claimers to the queue. Callers pass identities that
/// have already been proven; this service only compares them.
/// </summary>
public interface IHierarchyQa
{
    /// <summary>Queues the request or joins it to the job for the same commit and set; a stored passing result gives a job that is already <c>done</c>.</summary>
    QaReply Submit(string repo, string sha, string? baseSha, string setName, bool fresh, string? priority, string requester);

    /// <summary>Claims the first admissible job for <paramref name="claimer"/>; the reply has no job when none is admissible.</summary>
    QaReply Claim(string claimer, bool allowFull);

    /// <summary>Ends the claim of <paramref name="caller"/>: with the verdict of the result file at <paramref name="resultPath"/>, or as failed with <paramref name="failure"/>.</summary>
    QaReply Done(string id, string caller, string? resultPath, string? failure);

    /// <summary>Returns a claimed job to the queue when <paramref name="mayRelease"/> accepts the address that claimed it.</summary>
    QaReply Release(string id, Func<string, bool> mayRelease);

    /// <summary>Closes a queued job as <c>withdrawn</c> when <paramref name="mayWithdraw"/> accepts its repository; a claimed job is refused <c>qa-claimed</c>, an ended one answers unchanged.</summary>
    QaReply Withdraw(string id, string? reason, Func<string, bool> mayWithdraw);

    QaReply Find(string id);

    /// <summary>The queued and claimed jobs, or every job with <paramref name="all"/>, oldest first.</summary>
    IReadOnlyList<QaJob> List(bool all);
}

/// <summary>What <see cref="HierarchyModule.CreateQa"/> needs from its host.</summary>
/// <param name="Probe">Reads the host's load and memory for admission.</param>
/// <param name="Clock">Stamps jobs.</param>
/// <param name="Files">Reads result files and the settings.</param>
/// <param name="IsLive">Whether the registration that holds an address is alive.</param>
/// <param name="NewId">Makes the id of a new job.</param>
/// <param name="SettingsPaths">The <c>agent-hierarchy.json</c> files whose <c>qa</c> object sets the limits, later ones overriding earlier ones.</param>
/// <param name="ResultsRoot">The directory that holds <c>&lt;sha&gt;/&lt;set&gt;/result.json</c>.</param>
/// <param name="Log">Where a one-line warning goes.</param>
/// <param name="DeliverVerdict">Told of every job that ends with a verdict.</param>
public sealed record QaDependencies(
    IHostProbe Probe,
    IClock Clock,
    IFileReader Files,
    Func<string, bool> IsLive,
    Func<string> NewId,
    IReadOnlyList<string> SettingsPaths,
    string ResultsRoot,
    Action<string> Log,
    Action<QaJob> DeliverVerdict)
{
    /// <summary>Told of every submit that puts a new job in the queue, not of one that joins a job already queued or ends from a stored result.</summary>
    public Action<QaJob>? NotifyQueued { get; init; }
}

internal sealed partial class QaService : IHierarchyQa
{
    private static readonly string[] Priorities = ["gate", "lane", "adhoc"];
    private static readonly string[] PassingStatuses = ["PASS", "FLAKY-PASS"];

    private readonly QaScheduler _scheduler;
    private readonly QaDependencies _d;
    private readonly string _resultsRoot;

    internal QaService(HierLib lib, QaDependencies d)
    {
        _d = d;
        _resultsRoot = d.ResultsRoot;
        var (settings, warnings) = QaSettings.Read(d.Files, d.SettingsPaths);
        foreach (var warning in warnings)
        {
            d.Log(warning);
        }

        _scheduler = new QaScheduler(new HierQaStore(lib, d.Log), d.Probe, d.Clock, settings, d.IsLive, d.NewId);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex SetName();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha();

    internal static QaJob Job(QaEntry e) => new(
        e.Id, e.Sha, e.Base, e.Set, e.Repo, e.Fresh, e.Priority, e.Requesters, e.Team, e.Created, e.State, e.ClaimedBy, e.ClaimedAt,
        e.ResultPath, e.Verdict, e.EndedAt, e.Reason);

    private static QaReply Refused(string code, string message) => new(null, code, message);

    private static QaReply Of(QaChangeResult result, string unknownMessage) => result switch
    {
        { Entry: { } entry } => new QaReply(Job(entry), null, null),
        { Error: "qa-unknown-entry" } => Refused("qa-unknown-entry", unknownMessage),
        { Error: "qa-not-claimed" } => Refused("qa-not-claimed", "that entry is not claimed"),
        { Error: "not-claimer" } => Refused("not-claimer", "only the claimer may end that entry"),
        { Error: "not-allowed" } => Refused("not-allowed", "only the claimer's team owner or the user may release that entry"),
        _ => Refused(result.Error ?? "qa-store-busy", "the QA store stayed busy; try again"),
    };

    public QaReply Submit(string repo, string sha, string? baseSha, string setName, bool fresh, string? priority, string requester)
    {
        _scheduler.StalePass();
        var lowered = sha.ToLowerInvariant();
        if (!Sha().IsMatch(lowered))
        {
            return Refused("qa-bad-request", "sha must be 40 hex digits");
        }

        if (!SetName().IsMatch(setName))
        {
            return Refused("qa-bad-request", "set must be lower-case letters, digits, '_' or '-', at most 64 characters, starting with a letter or digit");
        }

        if (priority is not null && !Priorities.Contains(priority, StringComparer.Ordinal))
        {
            return Refused("qa-bad-request", "priority must be gate, lane or adhoc");
        }

        if (baseSha is not null && !Sha().IsMatch(baseSha.ToLowerInvariant()))
        {
            return Refused("qa-bad-request", "base must be 40 hex digits");
        }

        var at = requester.IndexOf('@', StringComparison.Ordinal);
        QaCachedResult? cached = fresh ? null : CachedResult(lowered, setName);
        var result = _scheduler.Submit(new QaSubmission(
            repo, lowered, baseSha?.ToLowerInvariant(), setName, fresh, priority ?? "adhoc", requester, at > 0 ? requester[(at + 1)..] : null, cached));
        if (result.Entry is not { } entry)
        {
            return Refused(result.Error ?? "qa-store-busy", "the QA store stayed busy; try again");
        }

        var job = Job(entry);
        if (cached is not null && entry.State == "done" && entry.ResultPath == cached.Path && entry.Rev == 1)
        {
            _d.DeliverVerdict(job);
        }
        else if (entry.State == "queued" && entry.Rev == 1)
        {
            try
            {
                _d.NotifyQueued?.Invoke(job);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _d.Log($"qa: the queued notice for {job.Id} failed ({e.GetType().Name}); the job stays queued");
            }
        }

        return new QaReply(job, null, null);
    }

    public QaReply Claim(string claimer, bool allowFull)
    {
        _scheduler.StalePass();
        var result = _scheduler.Next(claimer, allowFull);
        return result.Entry is { } entry
            ? new QaReply(Job(entry), null, null)
            : result.Reason == QaScheduler.StoreBusy ? Refused(QaScheduler.StoreBusy, "the QA store stayed busy; try again") : new QaReply(null, null, null, result.Reason);
    }

    public QaReply Done(string id, string caller, string? resultPath, string? failure)
    {
        _scheduler.StalePass();
        string? verdict = null;
        if (failure is null)
        {
            if (resultPath is null || _scheduler.Entries().FirstOrDefault(e => e.Id == id) is not { } entry)
            {
                return resultPath is null
                    ? Refused("qa-bad-request", "give a result path or an error line")
                    : Refused("qa-unknown-entry", $"no entry {id}");
            }

            verdict = ResultStatus(resultPath, entry.Sha, entry.Set);
            if (verdict is null)
            {
                return Refused("qa-bad-result", "the result file must be the result.json of that commit and set, with a status");
            }
        }
        else if (failure.Length == 0 || failure.Contains('\n', StringComparison.Ordinal))
        {
            return Refused("qa-bad-request", "the error must be one non-empty line");
        }

        var result = _scheduler.Finish(id, caller, resultPath, verdict, failure);
        if (result.Entry is { } ended && ended.Verdict is not null)
        {
            _d.DeliverVerdict(Job(ended));
        }

        return Of(result, $"no entry {id}");
    }

    public QaReply Release(string id, Func<string, bool> mayRelease)
    {
        _scheduler.StalePass();
        return Of(_scheduler.Release(id, mayRelease), $"no entry {id}");
    }

    public QaReply Withdraw(string id, string? reason, Func<string, bool> mayWithdraw)
    {
        _scheduler.StalePass();
        var result = _scheduler.Withdraw(id, reason, e => mayWithdraw(e.Repo));
        return result is { Error: "qa-claimed", Entry: { } claimed }
            ? Refused("qa-claimed", $"entry {id} is claimed by {claimed.ClaimedBy}; use ah qa release or ah qa done")
            : result.Error == "not-allowed" ? Refused("not-allowed", "only a session that may submit for that entry's repository may withdraw it") : Of(result, $"no entry {id}");
    }

    public QaReply Find(string id)
    {
        _scheduler.StalePass();
        return _scheduler.Entries().FirstOrDefault(e => e.Id == id) is { } entry
            ? new QaReply(Job(entry), null, null)
            : Refused("qa-unknown-entry", $"no entry {id}");
    }

    public IReadOnlyList<QaJob> List(bool all)
    {
        _scheduler.StalePass();
        return [.. _scheduler.Entries().Where(e => all || e.State is "queued" or "claimed").OrderBy(e => e.Created).Select(Job)];
    }

    private QaCachedResult? CachedResult(string sha, string set)
    {
        var path = Path.Combine(_resultsRoot, sha, set, "result.json");
        return ResultStatus(path, sha, set) is { } status && PassingStatuses.Contains(status, StringComparer.Ordinal) && !_d.Files.ReadText(path).Contains("\"not_run\"", StringComparison.Ordinal) ? new QaCachedResult(path, status) : null;
    }

    /// <summary>The <c>status</c> of the result file at <paramref name="path"/>, or null when the path is not <c>result.json</c> under the results directory of that commit and set (symbolic links resolved) or the file has no text status.</summary>
    private string? ResultStatus(string path, string sha, string set)
    {
        try
        {
            var real = _d.Files.RealPath(path);
            var expected = Path.Combine(_d.Files.RealPath(_resultsRoot), sha, set) + Path.DirectorySeparatorChar;
            if (!real.StartsWith(expected, StringComparison.Ordinal) || real.IndexOf(Path.DirectorySeparatorChar, expected.Length) >= 0)
            {
                return null;
            }

            return JsonText.Parse(_d.Files.ReadText(real)) is { IsMap: true } map && map.AsObject().Get("status") is { IsText: true } status && status.AsString().Length > 0
                ? status.AsString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }
}
