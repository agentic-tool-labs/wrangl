using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>How the load monitor decides, from <c>&lt;AhHome&gt;/ahd-load.json</c> or the defaults.</summary>
/// <param name="CpuBusy">The share of CPU time busy at or above which every sample of <paramref name="CpuBusySeconds"/> must be.</param>
/// <param name="CpuBusySeconds">How long CPU use must stay at or above <paramref name="CpuBusy"/> before it triggers.</param>
/// <param name="MemPressure">The memory pressure level (<c>warn</c> or <c>critical</c>) at or above which the machine is loaded.</param>
/// <param name="ApiErrors">The transport plus server API errors in ten minutes that count, with <paramref name="ApiErrorSessions"/>.</param>
/// <param name="ApiErrorSessions">How many distinct sessions those errors must come from.</param>
/// <param name="Ticks">How many consecutive ticks a change needs.</param>
/// <param name="SpawnGapSeconds">The least time between two spawns while overloaded.</param>
internal sealed record LoadThresholds(
    double CpuBusy = 0.90,
    int CpuBusySeconds = 180,
    string MemPressure = "critical",
    int ApiErrors = 10,
    int ApiErrorSessions = 3,
    int Ticks = 3,
    int SpawnGapSeconds = 300)
{
    /// <summary>The share of a threshold every trigger must be under before the machine counts as recovered.</summary>
    public const double Recovery = 0.8;

    public JsonObject ToJson() => new()
    {
        ["cpu_busy"] = CpuBusy,
        ["cpu_busy_seconds"] = CpuBusySeconds,
        ["mem_pressure"] = MemPressure,
        ["api_errors"] = ApiErrors,
        ["api_error_sessions"] = ApiErrorSessions,
        ["ticks"] = Ticks,
        ["spawn_gap_s"] = SpawnGapSeconds,
    };
}

/// <summary>API errors seen in the last ten minutes, by kind, and across how many sessions.</summary>
internal sealed record ApiErrorCounts(int Transport, int RateLimit, int Server, int Other, int Sessions)
{
    public int Total => Transport + RateLimit + Server + Other;

    /// <summary>The kinds that say the service or the connection is failing, not the request.</summary>
    public int Failing => Transport + Server;

    public JsonObject ToJson() => new()
    {
        ["transport"] = Transport,
        ["rate_limit"] = RateLimit,
        ["server"] = Server,
        ["other"] = Other,
        ["total"] = Total,
        ["sessions"] = Sessions,
    };
}

/// <summary>One tick's reading of the machine.</summary>
/// <param name="At">When it was taken.</param>
/// <param name="Load1PerCpu">The 1-minute load average divided by the processor count; information only, never a trigger.</param>
/// <param name="Cpus">The processor count.</param>
/// <param name="CpuReadable">Whether this machine's CPU times can be read at all; when not, there is no CPU trigger.</param>
/// <param name="CpuBusy">The share of CPU time busy since the previous sample; null when there is none.</param>
/// <param name="MemPressure"><c>normal</c>, <c>warn</c> or <c>critical</c>; null when it cannot be read.</param>
/// <param name="Harnesses">How many Claude harness processes run.</param>
/// <param name="Api">The API errors of the last ten minutes.</param>
internal sealed record LoadSample(
    DateTimeOffset At, double Load1PerCpu, int Cpus, bool CpuReadable, double? CpuBusy, string? MemPressure, int Harnesses, ApiErrorCounts Api);

/// <summary>What the monitor has reported, kept so a restarted daemon does not report it again.</summary>
internal sealed record LoadState(string State, string Since, bool Alarmed, string? LastAdmit = null)
{
    public const string Ok = "ok";

    public const string Overloaded = "overloaded";

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["state"] = State, ["since"] = Since, ["alarmed"] = Alarmed };
        if (LastAdmit is not null)
        {
            json["last_admit"] = LastAdmit;
        }

        return json;
    }

    /// <summary>The state a file holds; null for text that is not one.</summary>
    public static LoadState? Parse(string? text)
    {
        try
        {
            return text is not null && JsonText.Parse(text) is { IsMap: true } v
                && v.Member("state") is { IsText: true } state && state.AsString() is Ok or Overloaded
                && v.Member("since") is { IsText: true } since
                ? new LoadState(state.AsString(), since.AsString(), v.Member("alarmed") is { IsTruthy: true }, v.Member("last_admit") is { IsText: true } admit ? admit.AsString() : null)
                : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>The thresholds of <c>ahd-load.json</c>.</summary>
internal static class LoadConfig
{
    /// <summary>
    /// The thresholds the file text gives, and why they are the defaults when it is invalid. A missing file (null text) or a missing
    /// key is the default; text that is not an object, or a key of the wrong type or out of range, gives every default and a problem.
    /// </summary>
    public static (LoadThresholds Thresholds, string? Problem) Parse(string? text)
    {
        var defaults = new LoadThresholds();
        if (text is null)
        {
            return (defaults, null);
        }

        JsonValue root;
        try
        {
            root = JsonText.Parse(text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (defaults, "ahd-load.json is not valid JSON; using the defaults");
        }

        if (!root.IsMap)
        {
            return (defaults, "ahd-load.json must hold one JSON object; using the defaults");
        }

        string? problem = null;
        double Number(string key, double fallback, double min, bool whole)
        {
            if (root.Member(key) is not { } v)
            {
                return fallback;
            }

            if (!v.IsNumber || double.IsNaN(v.AsNumber()) || v.AsNumber() < min || (whole && v.AsNumber() != Math.Floor(v.AsNumber())))
            {
                problem ??= $"ahd-load.json: {key} must be {(whole ? "a whole number" : "a number")} of at least {min.ToString(CultureInfo.InvariantCulture)}";
                return fallback;
            }

            return v.AsNumber();
        }

        var memory = defaults.MemPressure;
        if (root.Member("mem_pressure") is { } m)
        {
            if (m.IsText && m.AsString() is "warn" or "critical")
            {
                memory = m.AsString();
            }
            else
            {
                problem ??= "ahd-load.json: mem_pressure must be \"warn\" or \"critical\"";
            }
        }

        // load1_per_cpu is no longer a trigger: the key is accepted and ignored
        var cpuBusy = defaults.CpuBusy;
        if (root.Member("cpu_busy") is { } c)
        {
            if (c.IsNumber && c.AsNumber() is > 0 and <= 1)
            {
                cpuBusy = c.AsNumber();
            }
            else
            {
                problem ??= "ahd-load.json: cpu_busy must be a number above 0 and at most 1";
            }
        }

        var parsed = new LoadThresholds(
            cpuBusy,
            (int)Number("cpu_busy_seconds", defaults.CpuBusySeconds, 0, whole: true),
            memory,
            (int)Number("api_errors", defaults.ApiErrors, 1, whole: true),
            (int)Number("api_error_sessions", defaults.ApiErrorSessions, 1, whole: true),
            (int)Number("ticks", defaults.Ticks, 1, whole: true),
            (int)Number("spawn_gap_s", defaults.SpawnGapSeconds, 0, whole: true));
        return problem is null ? (parsed, null) : (defaults, problem + "; using the defaults");
    }
}

/// <summary>What one monitor tick changed.</summary>
internal enum LoadTransition
{
    None,

    /// <summary>The state became overloaded and was not already reported as that.</summary>
    Overloaded,

    /// <summary>The state returned to ok after an overloaded report.</summary>
    Recovered,
}

/// <summary>
/// The overloaded/ok state machine of the machine-load monitor: overloaded after <see cref="LoadThresholds.Ticks"/> consecutive
/// ticks with any trigger, ok again after as many consecutive ticks with every trigger under 80% of its threshold. It only reports;
/// it never stops anything.
/// </summary>
internal sealed class LoadMonitor
{
    private readonly LoadThresholds _t;
    private int _streak;
    private DateTimeOffset? _cpuSince;

    /// <param name="thresholds">How to decide.</param>
    /// <param name="restored">What was reported before a restart (null: nothing); the streaks always start from zero.</param>
    /// <param name="nowIso">The time a first state is stamped with.</param>
    public LoadMonitor(LoadThresholds thresholds, LoadState? restored, string nowIso)
    {
        _t = thresholds;
        Current = restored ?? new LoadState(LoadState.Ok, nowIso, Alarmed: false);
    }

    /// <summary>The state and what was reported of it.</summary>
    public LoadState Current { get; private set; }

    /// <summary>The triggers of the latest tick that hold, as text with their values.</summary>
    public IReadOnlyList<string> Triggers { get; private set; } = [];

    private static int Rank(string? level) => level switch { "critical" => 2, "warn" => 1, _ => 0 };

    private List<string> Firing(LoadSample s, double scale)
    {
        var firing = new List<string>();
        if (scale < 1)
        {
            // recovery needs every sample under 80% of the threshold, so a single busy sample is enough to hold it
            if (s.CpuBusy is { } share && share >= _t.CpuBusy * scale)
            {
                firing.Add(string.Create(CultureInfo.InvariantCulture, $"cpu busy {share:0.00} (>= {_t.CpuBusy * scale:0.00})"));
            }
        }
        else if (_cpuSince is { } since && s.At - since >= TimeSpan.FromSeconds(_t.CpuBusySeconds))
        {
            firing.Add(string.Create(CultureInfo.InvariantCulture, $"cpu busy {s.CpuBusy:0.00} for {(s.At - since).TotalSeconds:0} s (threshold {_t.CpuBusy:0.00})"));
        }

        // for recovery (scale under 1) only "critical" or "warn" itself counts, never the level below the threshold
        if (s.MemPressure is not null && Rank(s.MemPressure) >= Rank(_t.MemPressure))
        {
            firing.Add($"mem_pressure={s.MemPressure}");
        }

        if (s.Api.Failing >= Math.Ceiling(_t.ApiErrors * scale) && s.Api.Sessions >= _t.ApiErrorSessions)
        {
            firing.Add($"api_errors_10m={s.Api.Failing} across {s.Api.Sessions} sessions (>= {Math.Ceiling(_t.ApiErrors * scale)} across {_t.ApiErrorSessions})");
        }

        return firing;
    }

    /// <summary>Takes one reading. Returns what changed, which is the only moment to alarm.</summary>
    public LoadTransition Tick(LoadSample sample, string nowIso)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var over = Current.State == LoadState.Overloaded;

        // the CPU run starts at its first sample at or above the threshold; a sample under it, or with no reading, ends it
        _cpuSince = sample.CpuBusy >= _t.CpuBusy ? _cpuSince ?? sample.At : null;
        Triggers = Firing(sample, 1);
        var changing = over ? Firing(sample, LoadThresholds.Recovery).Count == 0 : Triggers.Count > 0;
        _streak = changing ? _streak + 1 : 0;
        if (_streak < _t.Ticks)
        {
            return LoadTransition.None;
        }

        _streak = 0;
        if (over)
        {
            Current = new LoadState(LoadState.Ok, nowIso, Alarmed: false);
            return LoadTransition.Recovered;
        }

        Current = new LoadState(LoadState.Overloaded, nowIso, Alarmed: true);
        return LoadTransition.Overloaded;
    }

    /// <summary>The <c>load</c> object of <c>daemon status</c>.</summary>
    public JsonObject Status(LoadSample sample, string? problem)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var triggers = new JsonArray();
        foreach (var t in Triggers)
        {
            triggers.Add(t);
        }

        var status = new JsonObject
        {
            ["state"] = Current.State,
            ["since"] = Current.Since,
            ["triggers"] = triggers,
            ["cpu_busy"] = sample.CpuBusy is { } share ? share : JsonValue.Null,
            ["load1_per_cpu"] = sample.Load1PerCpu,
            ["cpus"] = sample.Cpus,
            ["mem_pressure"] = sample.MemPressure is null ? JsonValue.Null : sample.MemPressure,
            ["harnesses"] = sample.Harnesses,
            ["api_errors_10m"] = sample.Api.ToJson(),
            ["thresholds"] = _t.ToJson(),
        };
        if (!sample.CpuReadable)
        {
            status["cpu_note"] = "cpu use not read on this platform";
        }

        if (problem is not null)
        {
            status["problem"] = problem;
        }

        return status;
    }

    /// <summary>The text of the one alarm mail for a transition; it says what the machine does and does not do about it.</summary>
    public string AlarmText(LoadTransition transition) =>
        transition == LoadTransition.Overloaded
            ? $"machine overloaded: {string.Join("; ", Triggers)}. New spawns are paced (one per {_t.SpawnGapSeconds} s); nothing is stopped."
            : "machine load is back to normal; spawns are no longer paced.";
}
