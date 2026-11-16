using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch.Model;

/// <summary>One job of a team as its record says, with the eta threshold in seconds; the lane carries these file facts and nothing that depends on the clock.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Owner">The owning member's name, or <c>orchestrator</c>.</param>
/// <param name="Kind">One of test, build, slot, smoke, bg.</param>
/// <param name="Label">What the job runs.</param>
/// <param name="Pid">The job's process, or null while unknown.</param>
/// <param name="Eta">The eta word.</param>
/// <param name="ThresholdSeconds">The seconds the eta word stands for.</param>
/// <param name="SentAtMs">When the job started.</param>
/// <param name="ExtendedAtMs">When the owner last extended it, or null.</param>
/// <param name="CapAtMs">When the hard cap ends it, or null.</param>
/// <param name="EndedAtMs">When it ended, or null while open.</param>
/// <param name="Background">Whether the owner's turn is free while it runs.</param>
/// <param name="SlotPhase">Where the job stands in the test-slot queue (<c>queued</c> or holding), or null when it has no slot record.</param>
/// <param name="SlotSinceMs">When it entered that phase, or null with no slot record; the job's clock starts there once it holds the slot.</param>
public sealed record JobRow(
    string Id,
    string Owner,
    string Kind,
    string Label,
    int? Pid,
    string Eta,
    double ThresholdSeconds,
    double SentAtMs,
    double? ExtendedAtMs,
    double? CapAtMs,
    double? EndedAtMs,
    bool Background,
    string? SlotPhase = null,
    double? SlotSinceMs = null);

/// <summary>
/// The jobs of one team, <c>jobs:&lt;dirId&gt;/&lt;team&gt;</c>: <c>{rows: [{id, owner, kind, label, pid, eta, threshold_s, sent_at, extended_at, cap_at, ended_at, background}]}</c>, the
/// times as UTC ISO strings and absent ones null. The open jobs are listed, and the ones that ended in the last ten minutes. It is composed
/// only while a client holds the slice and is re-read on each reconcile tick; a response adds each row's <c>state</c>, <c>due_at</c> and
/// <c>overdue_by</c> from the clock (<see cref="JobProjection"/>), so the stored slice holds file facts only.
/// The job records' write counter (<c>version</c>, null when it cannot be read) makes a team's rows read once per write (b459 step 11).
/// </summary>
internal sealed class JobsLane(Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<string, string, double, IReadOnlyList<JobRow>> probe, Func<long?>? version = null) : IModelLane, ILeaseLane
{
    /// <summary>Each team's rows as the job records held them at a write counter. The rows carry no clock: <see cref="Listed"/> applies it at read time.</summary>
    private readonly Dictionary<(string Dir, string Folder), (long Version, IReadOnlyList<JobRow> Rows)> _rows = [];

    public const string LaneName = "jobs";

    /// <summary>How long a job that has ended stays listed.</summary>
    public const double EndedKeptMs = 10 * 60 * 1000;

    private const string Head = LaneName + ":";

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public string Name => LaneName;

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

        var dir = canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == parts[0]);
        return dir is null ? null : ProgressLane.KeyOf(dir, parts[1]);
    }

    public IReadOnlyList<string> LapsedKeys() => [.. Keys().Where(k => !subscribed(SliceIds(k)[0]))];

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, folder) = ProgressLane.Split(key);
        var sliceId = SliceIdOf(dir, folder);
        if (!subscribed(sliceId))
        {
            lock (_gate)
            {
                _ = _held.Remove(key);
            }

            return new LaneResult([]) { Evict = [sliceId] };
        }

        lock (_gate)
        {
            _ = _held.Add(key);
        }

        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(BodyOf(dir, folder, nowMs)), 0), 0)]);
    }

    /// <summary>The team's job rows: the held ones while the write counter has not moved, else read again.</summary>
    private IReadOnlyList<JobRow> RowsOf(string dir, string folder, double nowMs)
    {
        // read before the rows: a write between the two leaves the older counter held, so the next read reads again
        if (version?.Invoke() is not { } now)
        {
            return probe(dir, folder, nowMs);
        }

        lock (_gate)
        {
            if (_rows.TryGetValue((dir, folder), out var held) && held.Version == now)
            {
                return held.Rows;
            }
        }

        var rows = probe(dir, folder, nowMs);
        lock (_gate)
        {
            _rows[(dir, folder)] = (now, rows);
        }

        return rows;
    }

    /// <summary>Drops every held row set, so the next read of each team reads the records (the on-demand rebuild).</summary>
    internal void Forget()
    {
        lock (_gate)
        {
            _rows.Clear();
        }
    }

    private JsonObject BodyOf(string dir, string folder, double nowMs)
    {
        var rows = new JsonArray();
        foreach (var row in Listed(RowsOf(dir, folder, nowMs), nowMs))
        {
            rows.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["owner"] = row.Owner,
                ["kind"] = row.Kind,
                ["label"] = row.Label,
                ["pid"] = row.Pid is { } pid ? pid : JsonValue.Null,
                ["eta"] = row.Eta,
                ["threshold_s"] = row.ThresholdSeconds,
                ["sent_at"] = Iso(row.SentAtMs),
                ["extended_at"] = row.ExtendedAtMs is { } ext ? Iso(ext) : JsonValue.Null,
                ["cap_at"] = row.CapAtMs is { } cap ? Iso(cap) : JsonValue.Null,
                ["ended_at"] = row.EndedAtMs is { } end ? Iso(end) : JsonValue.Null,
                ["background"] = row.Background,
                ["slot_phase"] = row.SlotPhase is { } phase ? phase : JsonValue.Null,
                ["slot_since"] = row.SlotSinceMs is { } since ? Iso(since) : JsonValue.Null,
            });
        }

        return new JsonObject { ["rows"] = JsonValue.FromArray(rows) };
    }

    /// <summary>
    /// The body of the team's jobs read from the job records now, whether or not any client holds the slice; null when the team's directory is not
    /// one the view knows. <paramref name="openTeamSuffix"/> is the team as an open list names it, <c>&lt;dirId&gt;/&lt;team name&gt;</c>.
    /// </summary>
    internal JsonObject? BodyOfOpenTeam(string openTeamSuffix, double nowMs)
    {
        var at = openTeamSuffix.IndexOf('/', StringComparison.Ordinal);
        if (at <= 0 || at == openTeamSuffix.Length - 1 || KeyOfToken(Head + openTeamSuffix[..at] + "/" + ProgressLane.FolderOf(openTeamSuffix[(at + 1)..])) is not { } key)
        {
            return null;
        }

        var (dir, folder) = ProgressLane.Split(key);
        return BodyOf(dir, folder, nowMs);
    }

    /// <summary>The open list's team suffix (<c>&lt;dirId&gt;/&lt;team name&gt;</c>) of a jobs slice suffix (<c>&lt;dirId&gt;/&lt;progress folder&gt;</c>): the one translation between the two spellings of a team.</summary>
    internal static string OpenTeamSuffixOf(string jobsSuffix)
    {
        var at = jobsSuffix.IndexOf('/', StringComparison.Ordinal);
        return at < 0 ? jobsSuffix : jobsSuffix[..at] + "/" + ProgressLane.TeamNameOf(jobsSuffix[(at + 1)..]);
    }

    /// <summary>The jobs the slice lists at <paramref name="nowMs"/>: the open ones and those that ended no more than ten minutes before, oldest start first.</summary>
    internal static IReadOnlyList<JobRow> Listed(IReadOnlyList<JobRow> rows, double nowMs) =>
        [.. rows.Where(r => r.EndedAtMs is not { } ended || nowMs - ended <= EndedKeptMs).OrderBy(static r => r.SentAtMs).ThenBy(static r => r.Id, StringComparer.Ordinal)];

    private static string Iso(double ms) => Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));
}
