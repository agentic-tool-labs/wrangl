using System.Globalization;
using Ah.Abstractions;

namespace Ah.Watch;

/// <summary>Whether the stall watch acts, only journals, or is off.</summary>
public enum StallMode
{
    On,
    Report,
    Off,
}

/// <summary>
/// The stall watch's timings. Each is read from <c>AH_STALL_&lt;NAME&gt;</c>, then <c>AHD_STALL_&lt;NAME&gt;</c> (the daemon's environment
/// is scrubbed, so a test sets the second), as seconds above 0; <c>MODE</c> is <c>on</c>, <c>report</c> or <c>off</c>.
/// </summary>
/// <param name="Period">How often the watch sweeps.</param>
/// <param name="Down">How long a member must read Dead, sweep after sweep, before it is reported down.</param>
/// <param name="WarmUp">How long after the daemon starts it only observes.</param>
/// <param name="Mode">Whether the watch acts.</param>
/// <param name="WakePoll">How often the waiter looks for undelivered event mail.</param>
/// <param name="Heartbeat">How long the waiter blocks with nothing pending before it exits.</param>
public sealed record StallSettings(TimeSpan Period, TimeSpan Down, TimeSpan WarmUp, TimeSpan WakePoll, TimeSpan Heartbeat, StallMode Mode)
{
    public static StallSettings Defaults { get; } = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(20), StallMode.On);

    public static StallSettings From(IAhEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var d = Defaults;
        return new StallSettings(
            Seconds(env, "PERIOD", d.Period), Seconds(env, "DOWN", d.Down), Seconds(env, "WARMUP", d.WarmUp),
            Seconds(env, "WAKE_POLL", d.WakePoll), Seconds(env, "HEARTBEAT", d.Heartbeat), ModeOf(Raw(env, "MODE"))) { StuckAfter = StuckWindow(env), AskAlarmAfter = Seconds(env, "ASK_ALARM", d.AskAlarmAfter), AlarmRepeat = Seconds(env, "ALARM_REPEAT", d.AlarmRepeat) };
    }

    /// <summary>How often a user alarm is told again while its episode lasts (<c>ALARM_REPEAT</c>).</summary>
    public TimeSpan AlarmRepeat { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>How long an Orchestrator waits on a question in its terminal before the user's <c>alarm.command</c> runs (<c>ASK_ALARM</c>).</summary>
    public TimeSpan AskAlarmAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a stuck report stands with no tool call from the member before the sweep copies it to the top Orchestrator.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How far back a member's refusals count toward being stuck, and how long an unanswered stuck report waits before the top Orchestrator is copied (<c>STUCK_WINDOW</c>).</summary>
    public static TimeSpan StuckWindow(IAhEnvironment env) => Seconds(env, "STUCK_WINDOW", TimeSpan.FromMinutes(10));

    /// <summary>The stall watch's mode alone, for a caller that reads no other setting.</summary>
    public static StallMode ModeOf(IAhEnvironment env) => ModeOf(Raw(env, "MODE"));

    private static string? Raw(IAhEnvironment env, string name) =>
        env.GetVariable($"AH_STALL_{name}") is { Length: > 0 } a ? a : env.GetVariable($"AHD_STALL_{name}");

    private static TimeSpan Seconds(IAhEnvironment env, string name, TimeSpan fallback) =>
        Raw(env, name) is { } text && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 && n < 86_400 * 30
            ? TimeSpan.FromSeconds(n)
            : fallback;

    private static StallMode ModeOf(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "off" => StallMode.Off,
        "report" => StallMode.Report,
        _ => StallMode.On,
    };
}
