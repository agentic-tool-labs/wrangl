using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Hierarchy;

public static partial class HierarchyModule
{
    /// <summary>How long open background work counts as work. A slot-held test is killed at 3 x its eta, an hour at the largest; past this the task's end was lost.</summary>
    public const double BackgroundWorkHoldMs = 3_600_000;

    /// <summary>Whether an activity <paramref name="record"/> that says idle has background work open, recorded within <see cref="BackgroundWorkHoldMs"/>, so its member reads as working.</summary>
    public static bool IdleOnBackgroundWork(JsonValue record, double nowMs) => IdleOnBackgroundWorkUntil(record, nowMs) is not null;

    /// <summary>When <see cref="IdleOnBackgroundWork"/>, true of <paramref name="record"/> at <paramref name="nowMs"/>, stops being true by the clock alone; null when it is not true now.</summary>
    public static double? IdleOnBackgroundWorkUntil(JsonValue record, double nowMs) =>
        record.Member("activity") is { IsText: true } activity && activity.AsString() == "idle"
        && record.Member("bg_open") is { IsNumber: true } open && open.AsNumber() > 0
        && (record.Member("state_since") ?? record.Member("at")) is { IsText: true } since
        && Timestamps.TryParseMs(since.AsString(), out var sinceMs)
        && nowMs - sinceMs < BackgroundWorkHoldMs
            ? sinceMs + BackgroundWorkHoldMs
            : null;
}

/// <summary>
/// What is known about one member, gathered by the caller, for <see cref="HierarchyModule.MemberVerdict"/>. Times are Unix
/// milliseconds; an absent source is null.
/// </summary>
public sealed record MemberEvidence
{
    /// <summary>True, false, or null when the liveness check could not tell.</summary>
    public bool? Live { get; init; }

    /// <summary>The activity record's <c>activity</c>: working, idle, blocked, failed or unknown.</summary>
    public string? Activity { get; init; }

    /// <summary>The activity record's <c>at</c>.</summary>
    public double? AtMs { get; init; }

    /// <summary>The activity record's <c>state_since</c>: when <see cref="Activity"/> last changed.</summary>
    public double? StateSinceMs { get; init; }

    /// <summary>The activity record's <c>working_at</c>: the last write whose activity was working.</summary>
    public double? WorkingAtMs { get; init; }

    /// <summary>The activity record's <c>working_since</c>: when the latest stretch of working began; kept after it ends, so it never means "working now".</summary>
    public double? WorkingSinceMs { get; init; }

    /// <summary>The activity record's <c>bg_open</c>: open background tasks when the member's turn last ended.</summary>
    public int BgOpen { get; init; }

    /// <summary>The activity record's <c>compacted_at</c>.</summary>
    public double? CompactedAtMs { get; init; }

    /// <summary>The herdr <c>agent_status</c> of the member's pane: working, idle, done, blocked; null without a pane.</summary>
    public string? Herdr { get; init; }

    /// <summary>The ListAgents tag: busy, shell, idle; null when none.</summary>
    public string? Tag { get; init; }

    /// <summary>Whether the visible screen is a recognised prompt (a permission, picker or trust prompt, or a named Codex prompt).</summary>
    public bool PromptRecognised { get; init; }

    /// <summary>Whether a dispatch to the member is open.</summary>
    public bool DispatchOpen { get; init; }

    /// <summary>Codex members have no hooks, so they never reach <c>stalled-after-compact</c>.</summary>
    public bool Codex { get; init; }

    public double NowMs { get; init; }

    /// <summary>How long a compaction must have gone unanswered before it counts as a stall.</summary>
    public double DebounceMs { get; init; } = 5 * 60 * 1000;
}
