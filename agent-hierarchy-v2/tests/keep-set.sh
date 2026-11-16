#!/bin/bash
# agent-hierarchy — the ah-simplify D4 keep-set (E1-E5): existing tests only, run after every slice.
# Usage: tests/keep-set.sh [--no-build]
# Builds native/ in Release once (MSBUILDDISABLENODEREUSE=1), then runs each E item's filter with --no-build,
# the mod tests and the web tests. Anything that writes state runs under a throwaway HOME. Exit 0 only when every item passes.
# AH_KEEP_SET_CONFIG: the configuration to build and test (default Release). AH_KEEP_SET_ONLY=<item>:<project or suite file>: only that one row.
set -u
config=${AH_KEEP_SET_CONFIG:-Release}
only=${AH_KEEP_SET_ONLY:-}
here=$(cd "$(dirname "$0")/.." && pwd)
native="$here/native"
export MSBUILDDISABLENODEREUSE=1
T=$(mktemp -d /tmp/ah-keepset.XXXXXX)
[ -n "$T" ] || exit 2
trap 'rm -rf "$T"' EXIT

if [ "${1:-}" != "--no-build" ]; then
    (cd "$native" && dotnet build -c "$config" -v q -nologo) > "$T/build.log" 2>&1 || { tail -30 "$T/build.log"; echo "keep-set: build FAILED"; exit 1; }
fi

fails=(); ran=0
run() { # item project filter...
    local item=$1 project=$2; shift 2
    [ -z "$only" ] || [ "$only" = "$item:$project" ] || return 0
    ran=1
    local filter
    filter=$(printf 'FullyQualifiedName~%s|' "$@"); filter=${filter%|}
    local start=$SECONDS
    if HOME="$T/home" dotnet test "$native/tests/$project/$project.csproj" -c "$config" --no-build --filter "$filter" > "$T/$item-$project.log" 2>&1; then
        echo "PASS $item $project ($((SECONDS - start))s) $(grep -Eo 'Passed: +[0-9]+' "$T/$item-$project.log" | tail -1)"
    else
        echo "FAIL $item $project"; grep -E '^\s+Failed |error' "$T/$item-$project.log" | head -20
        fails+=("$item:$project")
    fi
}
mkdir -p "$T/home"

# E1 channels: pane `roster deliver`, mailbox relay with mail-claim, the report reaching the orchestrator.
run E1 Ah.Mesh.Tests MailClaimTests MailRelayClaimTests MailSingleCarrierTests MailboxReportDeliveredTests
run E1 Ah.Cli.Tests RosterSendBriefTests RosterPaneVerbTests MsgSendRelayRecordTests
# E2 state: /v1/state writes into Ah.StateDb and back; the daemon-down fallback.
run E2 Ah.StateDb.Tests StateDbTeamAuthorityTests StateDbSessionAuthorityTests StateDbWritersRoutedTests
run E2 Ah.State.Tests StateClientTests
run E2 Ah.DaemonClient.Tests DaemonClientModuleTests DaemonPresentTests
# E3 messaging: new -> send -> inbox -> ack, and the response report.
run E3 Ah.Cli.Tests MsgSendFallbackTests MsgInboxLadderTests MsgSkeletonReportTests MessagePoolResolutionTests MsgPrincipalTests
# E4 nudges: StopPeerNudge owed/cleared, SubagentStopMsgNudge, StopOrchestratorLiveness (+WakeRule).
run E4 Ah.Hooks.Tests StopPeerNudgeReportTests SubagentStopMsgNudgeTests StopOrchestratorLivenessTests
# E5 UI APIs: health, view, list, actions, read-model, status, due, peers/pending, inspect, ui lease + bridge.
run E5 Ah.Daemon.Tests StateRouteTests ReadModelRouteTests ActionsRouteTests UiBridgeTests UiLeaseRouteTests ViewServiceTests HealthExtrasTests UiServiceEndToEndTests
run E5 Ah.Mesh.Tests MailDueRouteTests MailboxRoutesTests
run E5 Ah.Cli.Tests PeerPendingRouteTests

for suite in "bash $here/tests/test-mod-plugin-test.sh" "sh $here/tests/web/run.sh"; do
    [ -z "$only" ] || [ "$only" = "E5:${suite##*/}" ] || continue
    ran=1
    start=$SECONDS
    if (cd "$here" && HOME="$T/home" $suite) > "$T/suite.log" 2>&1; then
        echo "PASS E5 ${suite##*/} ($((SECONDS - start))s)"
    else
        echo "FAIL E5 ${suite##*/}"; tail -20 "$T/suite.log"; fails+=("E5:${suite##*/}")
    fi
done

if [ -n "$only" ] && [ "$ran" = 0 ]; then echo "keep-set: no row is named $only"; exit 2; fi
if [ ${#fails[@]} -gt 0 ]; then echo "keep-set: FAILED ${fails[*]}"; exit 1; fi
echo "keep-set: all E1-E5 passed"
