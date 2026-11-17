using Ah.Hooks.Activity;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks;

/// <summary>The native hook units that are not gates: session lifecycle (H3) and prompt, activity and tool tracking (H4).</summary>
internal static class AdvisoryUnits
{
    public static IReadOnlyList<GateUnit> All { get; } =
    [
        new("posttooluse-roster", PostToolUseRoster.Run, GateClass.NotAGate),
        new("posttooluse-merge-record", PostToolUseMergeRecord.Run, GateClass.NotAGate),
        new("posttooluse-commit-log", PostToolUseCommitLog.Run, GateClass.NotAGate),
        new("posttooluse-ask-record", PostToolUseAskRecord.Run, GateClass.NotAGate),
        new("posttooluse-push-record", PostToolUsePushRecord.Run, GateClass.NotAGate),
        new("posttooluse-implicit-opt-in", ImplicitOptIn.Run, GateClass.NotAGate),
        new("subagent-inflight", SubagentInflight.Run, GateClass.NotAGate),
        new("posttooluse-peer-resolve", PostToolUsePeerResolve.Run, GateClass.NotAGate),
        new("subagentstop-usage", SubagentStopUsage.Run, GateClass.NotAGate),
        new("stop-chain-down", StopChainDown.Run, GateClass.Advisory),
        new("stop-peer-nudge", StopPeerNudge.Run, GateClass.Advisory),
        new("subagentstart-cli-root", SubagentStartCliRoot.Run, GateClass.NotAGate),
        new("activity", ActivityRecorder.Run, GateClass.NotAGate),
        new("subagentstop-msg-nudge", SubagentStopMsgNudge.Run, GateClass.Advisory),
        new("sessionend-roster", SessionEndRoster.Run, GateClass.NotAGate),
        new("stream-label", StreamLabel.Run, GateClass.NotAGate),
        new("stopfailure-recovery", StopFailureRecovery.Run, GateClass.NotAGate),
        new("permission-denied-alarm", PermissionDeniedAlarm.Run, GateClass.Advisory),
        new("userpromptsubmit-peer-tracking", UserPromptSubmitPeerTracking.Run, GateClass.NotAGate),
        new("stop-orchestrator-liveness", StopOrchestratorLiveness.Run, GateClass.Advisory),
        new("stop-member-liveness", StopMemberLiveness.Run, GateClass.Advisory),
        new("sessionstart", SessionStart.Run, GateClass.NotAGate),
        new("session-directive", SessionDirective.Run, GateClass.NotAGate),
        new("session-index", SessionIndex.Run, GateClass.NotAGate),
        new("session-rules", SessionRules.Run, GateClass.NotAGate),
        new("job-track", JobTrack.Run, GateClass.NotAGate),
    ];
}
