using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>One API-error record found in a session's transcript: the session, Recovery's error kind and when the record says it happened.</summary>
public sealed record ApiErrorSighting(string Session, string Kind, DateTimeOffset At);

/// <summary>What the load watch reads from the machine and keeps on disk.</summary>
public interface ILoadSource
{
    int Cpus { get; }

    /// <summary>The 1-minute load average; null when the system does not say.</summary>
    double? Load1();

    /// <summary>
    /// Whether the machine's CPU times can be read, and the share of CPU time that was busy since the previous call: null on the first
    /// call, when no CPU time passed, when a counter went backwards, and always when they cannot be read.
    /// </summary>
    (bool Readable, double? Share) CpuBusy();

    /// <summary><c>normal</c>, <c>warn</c> or <c>critical</c>; null when it cannot be read.</summary>
    string? MemoryPressure();

    /// <summary>How many Claude harness processes run, from one read of the process table.</summary>
    int Harnesses();

    /// <summary>The text of the thresholds file; null when there is none.</summary>
    string? ConfigText();

    /// <summary>The text of the state file; null when there is none.</summary>
    string? ReadState();

    void WriteState(string text);
}

/// <summary>What a spawn asked of the pacing: whether it may start now, and when the next one may when it may not.</summary>
/// <param name="Admitted">Whether the spawn may start now.</param>
/// <param name="NextAt">When the next spawn may start; set when not admitted.</param>
/// <param name="Triggers">The load triggers that hold, with their values.</param>
/// <param name="Harnesses">How many Claude harness processes ran at the last sample; no new read is made for it.</param>
public sealed record SpawnAdmission(bool Admitted, DateTimeOffset? NextAt, IReadOnlyList<string> Triggers, int Harnesses = 0);

/// <summary>A change of the load state that the Orchestrators are told about once.</summary>
public sealed record LoadAlarm(string Kind, string Text, JsonObject Data);

/// <summary>The daemon's machine-load monitor as its callers see it.</summary>
public interface ILoadWatch
{
    /// <summary>
    /// Takes one sample. <paramref name="newErrors"/> gives the API errors found since the last sample, asked with the earliest time that
    /// still counts. Returns the alarm to mail when the state changed, else null.
    /// </summary>
    LoadAlarm? Tick(Func<DateTimeOffset, IReadOnlyList<ApiErrorSighting>> newErrors);

    /// <summary>The <c>load</c> object of <c>daemon status</c>.</summary>
    JsonObject Status();

    /// <summary>Whether a new spawn may start now; an admitted one is recorded unless <paramref name="dryRun"/>.</summary>
    SpawnAdmission AdmitSpawn(bool dryRun);
}

/// <summary>
/// The one machine-load monitor of the daemon: it samples the machine once per bare-watch sweep, keeps the API errors of the last ten
/// minutes, answers <c>daemon status</c> and paces new spawns while the machine is overloaded. It never stops or signals anything.
/// </summary>
internal sealed class LoadWatch(ILoadSource source, Func<DateTimeOffset> now) : ILoadWatch
{
    public const string AlarmOverloaded = "machine-overloaded";

    public const string AlarmRecovered = "machine-load-ok";

    public static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly List<ApiErrorSighting> _errors = [];
    private LoadMonitor? _monitor;
    private LoadThresholds _thresholds = new();
    private string? _problem;
    private string? _lastAdmit;
    private LoadSample _sample = new(default, 0, 0, CpuReadable: true, null, null, 0, new ApiErrorCounts(0, 0, 0, 0, 0));

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseIso(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>Reads the thresholds file and, the first time, the saved state; a changed threshold set replaces the monitor and keeps its state.</summary>
    private LoadMonitor Ensure(DateTimeOffset at)
    {
        var (thresholds, problem) = LoadConfig.Parse(source.ConfigText());
        _problem = problem;
        if (_monitor is null)
        {
            var restored = LoadState.Parse(source.ReadState());
            _lastAdmit = restored?.LastAdmit;
            _thresholds = thresholds;
            _monitor = new LoadMonitor(thresholds, restored, Iso(at));
        }
        else if (thresholds != _thresholds)
        {
            _thresholds = thresholds;
            _monitor = new LoadMonitor(thresholds, _monitor.Current, Iso(at));
        }

        return _monitor;
    }

    private void Save(LoadMonitor monitor) => source.WriteState(JsonText.Stringify((monitor.Current with { LastAdmit = _lastAdmit }).ToJson(), 0));

    /// <summary>The bucket of <see cref="ApiErrorCounts"/> a Recovery error kind belongs to.</summary>
    internal static string Bucket(string kind) => kind switch
    {
        "server_error" or "overloaded" => "server",
        "transport" => "transport",
        "rate_limit" => "rate_limit",
        _ => "other",
    };

    internal static ApiErrorCounts Count(IEnumerable<ApiErrorSighting> errors)
    {
        int transport = 0, rate = 0, server = 0, other = 0;
        var failingSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in errors)
        {
            switch (Bucket(e.Kind))
            {
                case "transport":
                    transport++;
                    failingSessions.Add(e.Session);
                    break;
                case "server":
                    server++;
                    failingSessions.Add(e.Session);
                    break;
                case "rate_limit":
                    rate++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return new ApiErrorCounts(transport, rate, server, other, failingSessions.Count);
    }

    /// <summary>
    /// Takes one sample. <paramref name="newErrors"/> gives the API errors found since the last sample, asked with the earliest time that
    /// still counts. Returns the alarm to mail when the state changed, else null.
    /// </summary>
    public LoadAlarm? Tick(Func<DateTimeOffset, IReadOnlyList<ApiErrorSighting>> newErrors)
    {
        ArgumentNullException.ThrowIfNull(newErrors);
        var at = now();
        var from = at - ErrorWindow;
        var fresh = newErrors(from);
        var cpus = Math.Max(1, source.Cpus);
        var load1 = (source.Load1() ?? 0) / cpus;
        var cpu = source.CpuBusy();
        var memory = source.MemoryPressure();
        var harnesses = source.Harnesses();
        lock (_gate)
        {
            var monitor = Ensure(at);
            _errors.AddRange(fresh);
            _ = _errors.RemoveAll(e => e.At < from);
            _sample = new LoadSample(at, load1, cpus, cpu.Readable, cpu.Share, memory, harnesses, Count(_errors));
            var transition = monitor.Tick(_sample, Iso(at));
            if (transition == LoadTransition.None)
            {
                return null;
            }

            Save(monitor);
            var overloaded = transition == LoadTransition.Overloaded;
            return new LoadAlarm(
                overloaded ? AlarmOverloaded : AlarmRecovered,
                monitor.AlarmText(transition),
                new JsonObject { ["state"] = monitor.Current.State, ["load"] = monitor.Status(_sample, _problem) });
        }
    }

    /// <summary>The <c>load</c> object of <c>daemon status</c>.</summary>
    public JsonObject Status()
    {
        lock (_gate)
        {
            return (_monitor ?? Ensure(now())).Status(_sample, _problem);
        }
    }

    /// <summary>
    /// Whether a new spawn may start. While overloaded, one may start only when none was admitted within the spawn gap; an admitted
    /// spawn is recorded unless <paramref name="dryRun"/>. The check and the record are one step, so two callers never both pass one gap.
    /// </summary>
    public SpawnAdmission AdmitSpawn(bool dryRun)
    {
        lock (_gate)
        {
            var at = now();
            var monitor = Ensure(at);
            IReadOnlyList<string> triggers = monitor.Current.State == LoadState.Overloaded ? monitor.Triggers : [];
            if (monitor.Current.State == LoadState.Overloaded && ParseIso(_lastAdmit) is { } last && last + TimeSpan.FromSeconds(_thresholds.SpawnGapSeconds) > at)
            {
                return new SpawnAdmission(false, last + TimeSpan.FromSeconds(_thresholds.SpawnGapSeconds), triggers, _sample.Harnesses);
            }

            if (!dryRun)
            {
                _lastAdmit = Iso(at);
                Save(monitor);
            }

            return new SpawnAdmission(true, null, triggers, _sample.Harnesses);
        }
    }
}
