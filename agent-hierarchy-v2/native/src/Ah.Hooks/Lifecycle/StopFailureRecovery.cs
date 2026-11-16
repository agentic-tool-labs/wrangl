using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.DaemonClient;
using Ah.Hierarchy;
using Ah.Hooks.Activity;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// StopFailure: a turn ended on an API error. Records it as the session's activity <c>failed</c> so the daemon can resume the
/// session after a backoff, or escalate. Recorded for every non-subagent session with a hierarchy dir, with or without a
/// role. For an orchestrator whose failure will not be resumed (a non-retryable error, a spent retry budget, or a daemon that
/// does not own the session) it also asks the terminal for a desktop notification. Never blocks; any failure is silent.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class StopFailureRecovery
{
    private static readonly TimeSpan DaemonProbe = TimeSpan.FromMilliseconds(300);

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (c.Input.Text("hook_event_name") != "StopFailure")
        {
            return HookEmit.Silent;
        }

        var agentId = c.Input.Text("agent_id");
        if (s.Config.IsSubagent(c.Input) || sessionId.Length == 0)
        {
            advisory.TurnEnds?.Record(new TurnEndRecord("StopFailure", sessionId, agentId, null, null, null, s.Config.IsSubagent(c.Input) ? "skipped:subagent" : "skipped:no-session"));
            return HookEmit.Silent;
        }

        var recovery = advisory.Recovery;
        var cwd = c.Cwd();
        var dir = s.Config.HierarchyDir(cwd);
        var transcript = recovery.TranscriptPathOk(c.Input.Member("transcript_path"));
        var found = transcript is null ? JsonValue.Null : recovery.TranscriptFailure(JsonValue.FromString(transcript), new Dictionary<string, string>());
        var payloadDetails = FirstPresent(c.Input.Member("error_details"), c.Input.Member("error_message"));
        var error = recovery.FailureKind(FirstPresent(c.Input.Member("error"), c.Input.Member("error_type")), payloadDetails, found);
        var details = recovery.OneLine(payloadDetails, JsonValue.FromNumber(300));
        var code = recovery.OneLine(c.Input.Member("error_code") is { IsText: true } ec ? ec : string.Empty, JsonValue.FromNumber(40));
        var nowMs = (double)s.Clock.UtcNow.ToUnixTimeMilliseconds();
        var current = s.Hierarchy.Status.ReadActivityRecord(dir, sessionId);
        var currentFailed = current.Member("activity") is { IsText: true } act && act.AsString() == "failed";
        var failedMs = double.NaN;
        if (currentFailed && current.Member("failed_at") is { IsText: true } fa && Timestamps.TryParseMs(fa.AsString(), out var parsed))
        {
            failedMs = parsed;
        }

        JsonValue failedAt = Timestamps.ToIsoString(s.Clock.UtcNow);
        // The streak is the resumes already emitted for this session, not a counter an end of turn could reset.
        // A store that cannot answer leaves the streak unknown (no number is made up); the failure is still recorded.
        double? streak;
        try
        {
            using var decisions = s.Hierarchy.Core.DecisionReads();
            streak = recovery.StreakFor(
                JsonValue.FromArray([.. s.Hierarchy.Core.PeerRecordsWhere(new PeerQuery("subject", sessionId, new HashSet<string>(StringComparer.Ordinal) { "watch-event" }))]),
                sessionId,
                nowMs);
        }
        catch (StoreUnavailableException unreadable)
        {
            c.LogError(unreadable);
            streak = null;
        }

        if (currentFailed
            && current.Member("source") is { IsText: true } src && src.AsString() == "transcript"
            && Math.Abs(nowMs - failedMs) <= recovery.SameFailureMs)
        {
            // The transcript detector saw this same end first: one failure, seen twice.
            if (current.Member("streak") is { IsNumber: true } cs && cs.AsNumber() == Math.Floor(cs.AsNumber()) && cs.AsNumber() > 0)
            {
                streak = cs.AsNumber();
            }

            failedAt = current.Member("failed_at") ?? JsonValue.Null;
        }

        var turnEndKey = TurnEndKeyOf(recovery, current, sessionId, found, failedAt);
        var failed = new JsonObject
        {
            ["activity"] = "failed",
            ["extra"] = new JsonObject
            {
                ["error"] = error,
                ["error_details"] = details.Length > 0 ? details : JsonValue.Null,
                ["error_code"] = code.Length > 0 ? code : JsonValue.Null,
                ["source"] = "stopfailure",
                ["failed_at"] = failedAt,
                ["streak"] = streak is { } known ? known : JsonValue.Null,
                ["transcript_path"] = transcript is null ? JsonValue.Null : transcript,
                ["turn_end_key"] = turnEndKey,
            },
        };
        string action;
        try
        {
            ActivityRecorder.MirrorTo(failed, ActivityRecorder.MirrorDir(s, cwd, dir));
            action = s.Hierarchy.Status.RecordActivityRouted(dir, sessionId, failed, hook: true)
                ? "recorded"
                : "error:activity record not written";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            action = "error:" + recovery.OneLine(e.Message, JsonValue.FromNumber(200));
        }

        advisory.TurnEnds?.Record(new TurnEndRecord("StopFailure", sessionId, agentId, dir, error, turnEndKey, action));

        // the hook's parent is the harness; CLAUDE_PID can only veto it (a wrapper shell between them), never name another process
        var parent = s.Process.ParentPid;
        var vetoed = s.Environment.GetVariable("CLAUDE_PID") is { Length: > 0 } claude && claude != parent.ToString(CultureInfo.InvariantCulture);
        var orchestrator = (!vetoed && parent > 1 && s.Roster.OwnedTeams(dir, new TeamInvoker(parent, sessionId)).Count > 0)
            || s.Config.ReadSessionRole(sessionId) is { IsText: true } role && role.AsString() == "orchestrator";
        if (!orchestrator || Resumed(s, advisory, sessionId) || HoldGate.Of(s, cwd, sessionId) is not null)
        {
            return HookEmit.Silent;
        }

        var label = new string(recovery.OneLine(NodePath.Basename(cwd), JsonValue.FromNumber(40)).Where(ch => ch is >= ' ' and <= '~' and not ';').ToArray());
        if (label.Length == 0)
        {
            label = "session";
        }

        var kind = new string(error.Where(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_').ToArray());
        var text = $"ah: {label} stopped on an API error ({kind}). Open it to continue.";
        if (text.Length > 120)
        {
            text = text[..120];
        }

        // OSC 9 desktop notification, then BEL.
        return HookEmit.Terminal($"\u001b]9;{text}\u0007\u0007");
    }

    /// <summary>
    /// The key the transcript detector would give this turn end: the one it already recorded, else the error entry's uuid when the
    /// transcript already holds it, else this hook's own failure time.
    /// </summary>
    private static string TurnEndKeyOf(IHierarchyRecovery recovery, JsonValue current, string sessionId, JsonValue found, JsonValue failedAt)
    {
        if (current.Member("turn_end_key") is { IsText: true } known && current.Member("source") is { IsText: true } src && src.AsString() == "transcript"
            && failedAt.IsText && current.Member("failed_at") is { IsText: true } cf && cf.AsString() == failedAt.AsString())
        {
            return known.AsString();
        }

        if (found.IsMap && found.Member("failed_at") is { IsText: true } at)
        {
            return recovery.TurnEndKey(sessionId, found.Member("uuid") is { IsText: true } id ? id.AsString() : null, at.AsString());
        }

        return recovery.TurnEndKey(sessionId, null, failedAt.IsText ? failedAt.AsString() : string.Empty);
    }

    /// <summary>
    /// The daemon's resume reaches this session only through a mod that is pulling its mail; a StopFailure is no Stop, so
    /// without one nothing else wakes it. Owning the session is not enough.
    /// </summary>
    private static bool Resumed(GateServices s, AdvisoryServices advisory, string sessionId)
    {
        bool owns;
        try
        {
            owns = DaemonClientModule.OwnsTimersAsync(advisory.Daemon, sessionId, DaemonProbe).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            owns = false;
        }

        if (!owns)
        {
            return false;
        }

        var nowMs = (double)s.Clock.UtcNow.ToUnixTimeMilliseconds();
        return advisory.Mailbox.OrchestratorPullFresh(sessionId, nowMs);
    }

    /// <summary>JavaScript's <c>a ?? b</c>: <paramref name="first"/> unless it is absent or null.</summary>
    private static JsonValue FirstPresent(JsonValue? first, JsonValue? second) =>
        first is { IsNull: false } f ? f : second ?? JsonValue.Null;
}
