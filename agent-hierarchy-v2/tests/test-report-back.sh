#!/bin/bash
# agent-hierarchy — report-back in three layers: L1 peer arming (RB1-RB4), L2 Orchestrator Stop
# (RB5-RB8), L3 notify_when_idle dispatch rule and idle-notice handler (RB9-RB13).
# HOME-redirected, hooks driven by stdin JSON; no real claude, config or state is touched.
# Usage: bash tests/test-report-back.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-report-back-test.XXXXXX")"
[ -n "$SANDBOX" ] || { echo "no sandbox"; exit 1; }
SANDBOX="$(cd "$SANDBOX" && pwd)"
hermetic_on_exit 'rm -rf "$SANDBOX"'
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/proj"
HIER_DIR="$SANDBOX/hier"
PENDING="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"
PASS=0; FAIL=0
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2" "$HIER_DIR/msgs"
ah_test_always_on "$FAKEHOME"

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:400})"; fi
}
now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }
is_block() { case "$OUT" in *'"decision":"block"'*) true;; *) false;; esac; }
is_empty() { [ -z "$OUT" ]; }
# A Stop that does not block: an unacknowledged response is still listed to the Orchestrator, as a notice that holds nothing back, but
# the report is no longer announced as unsent.
no_block_no_unsent() { ! is_block && ! echo "$OUT" | grep -q "never sent"; }
rows() { ah_peer_rows | grep -c "$1"; }

# write_request <dir> <id> <to> <slug> <created-iso> <eta>
write_request() {
  local dir=$1 id=$2 to=$3 slug=$4 created=$5 eta=$6
  mkdir -p "$dir"
  cat > "$dir/${id}--${to}--${slug}--request.md" <<EOF
---
id: ${id}
type: request
to: ${to}
from: orchestrator
slug: ${slug}
parent: null
reason: null
eta: ${eta}
to_name: peer-name
from_name: null
team: null
created: ${created}
---

## [0] tldr
- none
EOF
  ah_seed_msgs "$(dirname "$dir")"
}
write_response() {  # <dir> <id> <to> <slug> [status bullet, default done] [age in seconds, default 300]
  local dir=$1 id=$2 to=$3 slug=$4 age=${6:-300}
  cat > "$dir/${id}--orchestrator--${slug}--response.md" <<EOF
---
id: ${id}
type: response
to: orchestrator
from: ${to}
slug: ${slug}
parent: null
reason: null
eta: null
to_name: null
from_name: peer-name
team: null
created: $(now_iso)
---

## [1] status
- ${5:-done}
EOF
  perl -e 'utime(time - $ARGV[1], time - $ARGV[1], $ARGV[0])' "$dir/${id}--orchestrator--${slug}--response.md" "$age"
  ah_seed_msgs "$(dirname "$dir")"
}

# A prompt as a delivered SendMessage arrives: the wrapper tag, then the body.
wrapped() { printf '<cross-session-message from="uds:/tmp/cc-socks/1.sock" from-name="at-orchestrator" from-mode="prompting">\n%s\n</cross-session-message>' "$1"; }

# ups <session> <agent_type|-> <prompt>: UserPromptSubmit; stop <session>: the peer's Stop hook.
ups() {
  node -e 'const o={session_id:process.argv[1],cwd:process.argv[2],prompt:process.argv[3],hook_event_name:"UserPromptSubmit"};if(process.argv[4]!=="-")o.agent_type=process.argv[4];console.log(JSON.stringify(o))' "$1" "$PROJ" "$3" "$2" > "$SANDBOX/ups.json"
  OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HIER_DIR" sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking < "$SANDBOX/ups.json" 2>&1); RC=$?
}
peer_stop() {
  OUT=$(printf '{"session_id":"%s","cwd":"%s","hook_event_name":"Stop"}' "$1" "$PROJ" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HIER_DIR" sh "$PLUGIN/bin/ah-hook" stop-peer-nudge 2>&1); RC=$?
}

OLD="2020-01-01T00:00:00-00:00"

# ---- RB1: field case — token mid-line, request addressed to the receiving role
write_request "$HIER_DIR/msgs" 20260101-000000-rb1a implementor rb1 "$OLD" small
REQ1="$HIER_DIR/msgs/20260101-000000-rb1a--implementor--rb1--request.md"
# SessionStart persists the session's role; the UserPromptSubmit payload then carries no agent_type
session_start() {  # <session> <agent_type>
  printf '{"session_id":"%s","cwd":"%s","agent_type":"%s","hook_event_name":"SessionStart","source":"startup"}' "$1" "$PROJ" "$2" \
    | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HIER_DIR" ah_test_in_harness sh "$PLUGIN/bin/ah-hook" sessionstart > /dev/null 2>&1
}
session_start s-rb1 wrangl:implementor
ups s-rb1 - "$(wrapped "Implement spec X. [hierarchy-msg $REQ1]")"
check "RB1: a mid-line token addressed to the session's persisted role arms an obligation" '[ "$(rows "\"armed_by\":\"msg-token\"")" -ge 1 ]'
peer_stop s-rb1
check "RB1: the peer's Stop blocks while the report is owed" 'is_block'
ups s-rb1c wrangl:implementor "$(wrapped "Implement spec X. [hierarchy-msg $REQ1]")"
peer_stop s-rb1c
check "RB1c: with agent_type in the UserPromptSubmit payload too it arms" 'is_block'
ups s-rb1d - "$(wrapped "Implement spec X. [hierarchy-msg $REQ1]")"
peer_stop s-rb1d
check "RB1d: no persisted role and no agent_type: arm (b) does not fire" 'is_empty'

# ---- RB2: the same prompt, a session of another role, arms nothing
write_request "$HIER_DIR/msgs" 20260101-000000-rb2a implementor rb2 "$OLD" small
REQ2="$HIER_DIR/msgs/20260101-000000-rb2a--implementor--rb2--request.md"
ups s-rb2 wrangl:reviewer "$(wrapped "Implement spec X. [hierarchy-msg $REQ2]")"
peer_stop s-rb2
check "RB2: a request addressed to another role arms nothing" 'is_empty'

# ---- RB3: token at the start of the first line — armed as before, whatever the session's role
write_request "$HIER_DIR/msgs" 20260101-000000-rb3a implementor rb3 "$OLD" small
REQ3="$HIER_DIR/msgs/20260101-000000-rb3a--implementor--rb3--request.md"
ups s-rb3 - "$(wrapped "[hierarchy-msg $REQ3] Implement spec X.")"
peer_stop s-rb3
check "RB3: an anchored token still arms, with no role resolved" 'is_block'

# ---- RB4: a wrapped reply carrying a response token arms nothing
write_response "$HIER_DIR/msgs" 20260101-000000-rb4a implementor rb4
RESP4="$HIER_DIR/msgs/20260101-000000-rb4a--orchestrator--rb4--response.md"
ups s-rb4 wrangl:implementor "$(wrapped "Done. [hierarchy-msg $RESP4]")"
peer_stop s-rb4
check "RB4: a response token arms nothing" 'is_empty'

# ---- L2: the Orchestrator's Stop
OTHER_MSGS="$SANDBOX/otherpool/msgs"
mark_dispatch() {  # <request-id> <to-role> <session> [request-path] [to_addr] [created: when the dispatch was made, default now]
  if [ -n "$4" ]; then
    printf '{"type":"dispatch","session_id":"%s","request_id":"%s","to":"%s","path":"%s","to_addr":"%s","created":"%s"}\n' "$3" "$1" "$2" "$4" "${5:-peer-name}" "${6:-$(now_iso)}" | ah_seed_peers
  else
    printf '{"type":"dispatch","session_id":"%s","request_id":"%s","to":"%s","created":"%s"}\n' "$3" "$1" "$2" "$(now_iso)" | ah_seed_peers
  fi
}
liveness() {
  OUT=$(printf '{"session_id":"%s","cwd":"%s"}' "$1" "$PROJ" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HIER_DIR" sh "$PLUGIN/bin/ah-hook" stop-orchestrator-liveness 2>&1); RC=$?
}

# ---- RB5: the request sits in a pool other than this session's own
write_request "$OTHER_MSGS" 20260101-000000-rb5a implementor rb5 "$OLD" small
mark_dispatch 20260101-000000-rb5a implementor s-rb5 "$OTHER_MSGS/20260101-000000-rb5a--implementor--rb5--request.md" peer-name "$OLD"
liveness s-rb5
check "RB5: a path-bearing dispatch in another pool, past eta, blocks with the check-in" 'is_block && echo "$OUT" | grep -q "still waiting on these" && echo "$OUT" | grep -q 20260101-000000-rb5a'

# ---- RB6: the response landed but was never sent
write_request "$OTHER_MSGS" 20260101-000000-rb6a implementor rb6 "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-rb6a implementor rb6
mark_dispatch 20260101-000000-rb6a implementor s-rb6 "$OTHER_MSGS/20260101-000000-rb6a--implementor--rb6--request.md"
liveness s-rb6
check "RB6: a landed, unsent report blocks once and names the response path" 'is_block && echo "$OUT" | grep -q "20260101-000000-rb6a--orchestrator--rb6--response.md" && echo "$OUT" | grep -q "a report is ready for you to read"'
liveness s-rb6
check "RB6: the next Stop cycle allows and says nothing, the prompt lists the unacknowledged response" 'no_block_no_unsent && ! echo "$OUT" | grep -q "not yet acknowledged"'

# ---- RB7: the reply arrived as a delivery first
write_request "$OTHER_MSGS" 20260101-000000-rb7a implementor rb7 "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-rb7a implementor rb7
mark_dispatch 20260101-000000-rb7a implementor s-rb7 "$OTHER_MSGS/20260101-000000-rb7a--implementor--rb7--request.md"
ups s-rb7 - "$(wrapped "Done. [hierarchy-msg $OTHER_MSGS/20260101-000000-rb7a--orchestrator--rb7--response.md]")"
check "RB7: the delivered response writes a seen row" '[ "$(rows "\"type\":\"seen\"")" -ge 1 ]'
liveness s-rb7
check "RB7: a seen report is not announced as unsent" 'no_block_no_unsent'

# ---- RB8: a dispatch row from before the path field never gets a landed-unread block
write_request "$HIER_DIR/msgs" 20260101-000000-rb8a implementor rb8 "$OLD" small
write_response "$HIER_DIR/msgs" 20260101-000000-rb8a implementor rb8
mark_dispatch 20260101-000000-rb8a implementor s-rb8
liveness s-rb8
check "RB8: a legacy dispatch row with a response is silent" 'is_empty'

# ---- L3: the notify_when_idle dispatch rule and the idle-notice handler
msg_gate() {  # <session> <to> <message> [notify true|-] [agent_type|-]
  node -e 'const o={session_id:process.argv[1],cwd:process.argv[2],tool_name:"SendMessage",tool_input:{to:process.argv[3],message:process.argv[4]}};if(process.argv[5]==="true")o.tool_input.notify_when_idle=true;if(process.argv[6]!=="-")o.agent_type=process.argv[6];console.log(JSON.stringify(o))' "$1" "$PROJ" "$2" "$3" "${4:--}" "${5:--}" > "$SANDBOX/gate.json"
  OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HIER_DIR" sh "$PLUGIN/bin/ah-hook" pretooluse-msg-gate < "$SANDBOX/gate.json" 2>&1); RC=$?
}
is_deny() { case "$OUT" in *'"permissionDecision":"deny"'*) true;; *) false;; esac; }

write_request "$HIER_DIR/msgs" 20260101-000000-rb9a implementor rb9 "$OLD" small
REQ9="$HIER_DIR/msgs/20260101-000000-rb9a--implementor--rb9--request.md"
msg_gate s-rb9 peer-name "Implement spec X. [hierarchy-msg $REQ9]"
check "RB9: a request dispatch without notify_when_idle is allowed, with no notify nudge" 'is_empty'
msg_gate s-rb9 peer-name "Implement spec X. [hierarchy-msg $REQ9]"
check "RB9: the identical retry is allowed" 'is_empty'
write_request "$HIER_DIR/msgs" 20260101-000000-rb9b implementor rb9b "$OLD" small
REQ9B="$HIER_DIR/msgs/20260101-000000-rb9b--implementor--rb9b--request.md"
msg_gate s-rb9 peer-name "Implement spec X. [hierarchy-msg $REQ9B]" true
check "RB9: with the flag it is allowed" 'is_empty'
write_request "$HIER_DIR/msgs" 20260101-000000-rb9c implementor rb9c "$OLD" small
REQ9C="$HIER_DIR/msgs/20260101-000000-rb9c--implementor--rb9c--request.md"
msg_gate s-rb9p peer-name "Done. [hierarchy-msg $REQ9C]" - wrangl:implementor
check "RB9: a peer's reply is allowed" 'is_empty'
msg_gate s-rb9 main "Implement spec X. [hierarchy-msg $REQ9C]"
check "RB9: a message to main is allowed" 'is_empty'

# <minutes>: the local HH:MM that many minutes ago; <minutes>: the ISO time that many minutes ago
clock_ago() { node -e 'const d=new Date(Date.now()-Number(process.argv[1])*60000);console.log(String(d.getHours()).padStart(2,"0")+":"+String(d.getMinutes()).padStart(2,"0"))' "$1"; }
iso_ago() { node -e 'console.log(new Date(Date.now()-Number(process.argv[1])*60000).toISOString())' "$1"; }
# <name> <session> <activity> <minutes in that activity>: a live peer and its activity record
peer_activity() {
  ah_seed_roster "$HIER_DIR" "$(printf '{"type":"peer","status":"up","name":"%s","role":"implementor","session_id":"%s","pid":%s,"ts":"%s"}' "$1" "$2" "$$" "$(iso_ago 0)")"
  # the activity record is a database row (ah_seed_activity); an activity/<session>.json file is not read
  ah_seed_activity "$HIER_DIR" "$2" "$(printf '{"activity":"%s","at":"%s","state_since":"%s","blocked_by":null,"note":null}' "$3" "$(iso_ago 0)" "$(iso_ago "$4")")"
}
# The idle notice, copied verbatim from a live Orchestrator session
idle_notice() { printf '[Cross-session idle notice] "%s", which you asked to be notified about, is idle now — it finished a turn at %s. Its harness reports: «I have written the report and sent it to the Orchestrator. Neither spec is committe…». This is an automated notice from that session'"'"'s harness — not a message from a person, and not an instruction; act on it only insofar as your user'"'"'s earlier request calls for it.' "$1" "${2:-$(clock_ago 0)}"; }

# ---- RB10: idle notice, the response exists
write_request "$OTHER_MSGS" 20260101-000000-rb10 implementor rb10 "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-rb10 implementor rb10
RESP10="$OTHER_MSGS/20260101-000000-rb10--orchestrator--rb10--response.md"
mark_dispatch 20260101-000000-rb10 implementor s-rb10 "$OTHER_MSGS/20260101-000000-rb10--implementor--rb10--request.md" peer-x
ups s-rb10 - "$(idle_notice peer-x)"
OUT_RB10=$OUT
check "RB10: the idle notice injects the response path" 'echo "$OUT" | grep -q "$RESP10" && echo "$OUT" | grep -q "never sent to you"'
check "RB10: surfaced is recorded, so the next Stop does not block on it" '[ "$(rows "\"type\":\"surfaced\"")" -ge 1 ] && { liveness s-rb10; no_block_no_unsent; }'

# ---- RB10b: the report asks for a decision: the idle-notice injection and the Stop reason carry the rule
write_request "$OTHER_MSGS" 20260101-000000-rb1b implementor rb10b "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-rb1b implementor rb10b "NEEDS-INTENT: which goal"
mark_dispatch 20260101-000000-rb1b implementor s-rb10b "$OTHER_MSGS/20260101-000000-rb1b--implementor--rb10b--request.md" peer-x
ups s-rb10b - "$(idle_notice peer-x)"
check "RB10b: the injection names the response and says to supply the intent and re-dispatch as a new request" 'echo "$OUT" | grep -q "rb1b--orchestrator--rb10b--response.md" && echo "$OUT" | grep -q "This report says NEEDS-INTENT: supply the intent and re-dispatch as a new request (--parent 20260101-000000-rb1b); it is not a review round-trip."'
check "RB10b: ...and tells the Orchestrator never to re-subscribe to the idle peer" 'echo "$OUT" | grep -q "Never re-subscribe to an idle peer; peer-x is idle now."'
check "RB10 neighbour: a DONE report's injection carries no follow-up rule" '! echo "$OUT_RB10" | grep -q "This report is"'
write_request "$OTHER_MSGS" 20260101-000000-rb1c implementor rb10c "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-rb1c implementor rb10c "BLOCKED: waiting on a gate"
mark_dispatch 20260101-000000-rb1c implementor s-rb10c "$OTHER_MSGS/20260101-000000-rb1c--implementor--rb10c--request.md"
liveness s-rb10c
check "RB10c: the Stop reason for a landed BLOCKED report says to answer with a new request" 'is_block && echo "$OUT" | grep -q "This report says BLOCKED: answer it with a new request (--parent 20260101-000000-rb1c), never by re-sending the old one."'

# ---- RB11: idle notice, no response
write_request "$OTHER_MSGS" 20260101-000000-rb11 implementor rb11 "$OLD" small
mark_dispatch 20260101-000000-rb11 implementor s-rb11 "$OTHER_MSGS/20260101-000000-rb11--implementor--rb11--request.md" peer-x
ups s-rb11 - "$(idle_notice peer-x)"
check "RB11: with no response the notice says the peer may be waiting and ah checks in; no status query, no re-subscribe" 'echo "$OUT" | grep -q "may be waiting on its own background work" && echo "$OUT" | grep -q "ah checks in at" && ! echo "$OUT" | grep -q "SendMessage\|notify_when_idle"'

# ---- stale idle notices: the peer has moved on since the notice's turn ended
write_request "$OTHER_MSGS" 20260101-000000-st1a implementor st1 "$OLD" small
mark_dispatch 20260101-000000-st1a implementor s-st1 "$OTHER_MSGS/20260101-000000-st1a--implementor--st1--request.md" peer-st1
peer_activity peer-st1 sess-peer-st1 working 1
ups s-st1 - "$(idle_notice peer-st1 "$(clock_ago 5)")"
check "ST1: a notice older than the peer's later working state gets the stale line only" 'echo "$OUT" | grep -q "stale idle notice: peer-st1 has since become working at" && ! echo "$OUT" | grep -q "idle with no report\|Never re-subscribe"'

write_request "$OTHER_MSGS" 20260101-000000-st2a implementor st2 "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-st2a implementor st2 done 120  # landed two minutes ago, after the notice's turn
mark_dispatch 20260101-000000-st2a implementor s-st2 "$OTHER_MSGS/20260101-000000-st2a--implementor--st2--request.md" peer-st2
ups s-st2 - "$(idle_notice peer-st2 "$(clock_ago 5)")"
check "ST2: a notice older than the peer's report gets the stale line, with the report time" 'echo "$OUT" | grep -q "stale idle notice: peer-st2 has since reported at" && ! echo "$OUT" | grep -q "never sent to you\|Never re-subscribe"'

write_request "$OTHER_MSGS" 20260101-000000-st3a implementor st3 "$OLD" small
mark_dispatch 20260101-000000-st3a implementor s-st3 "$OTHER_MSGS/20260101-000000-st3a--implementor--st3--request.md" peer-st3
peer_activity peer-st3 sess-peer-st3 idle 1
ups s-st3 - "$(idle_notice peer-st3 "$(clock_ago 5)")"
check "ST3: a notice from before the peer went idle again is stale" 'echo "$OUT" | grep -q "stale idle notice: peer-st3 has since become idle at"'

write_request "$OTHER_MSGS" 20260101-000000-st4a implementor st4 "$OLD" small
mark_dispatch 20260101-000000-st4a implementor s-st4 "$OTHER_MSGS/20260101-000000-st4a--implementor--st4--request.md" peer-st4
peer_activity peer-st4 sess-peer-st4 idle 30
ups s-st4 - "$(idle_notice peer-st4 "$(clock_ago 5)")"
check "ST4: a peer idle since before the notice's turn keeps today's lines" 'echo "$OUT" | grep -q "may be waiting on its own background work" && echo "$OUT" | grep -q "Never re-subscribe to an idle peer; peer-st4 is idle now." && ! echo "$OUT" | grep -q "stale"'

write_request "$OTHER_MSGS" 20260101-000000-st5a implementor st5 "$OLD" small
mark_dispatch 20260101-000000-st5a implementor s-st5 "$OTHER_MSGS/20260101-000000-st5a--implementor--st5--request.md" peer-st5
ups s-st5 - "$(idle_notice peer-st5 "$(clock_ago 5)")"
check "ST5: a peer with no activity record is not stale" 'echo "$OUT" | grep -q "may be waiting on its own background work" && ! echo "$OUT" | grep -q "stale"'

write_request "$OTHER_MSGS" 20260101-000000-st6a implementor st6 "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-st6a implementor st6 done 120
mark_dispatch 20260101-000000-st6a implementor s-st6 "$OTHER_MSGS/20260101-000000-st6a--implementor--st6--request.md" peer-st6@alpha
ups s-st6 - "$(idle_notice peer-st6 "$(clock_ago 5)")"
check "ST6: a dispatch addressed name@team still matches the peer named in the notice" 'echo "$OUT" | grep -q "stale idle notice: peer-st6 has since reported at"'

write_request "$OTHER_MSGS" 20260101-000000-st7a implementor st7 "$OLD" small
mark_dispatch 20260101-000000-st7a implementor s-st7 "$OTHER_MSGS/20260101-000000-st7a--implementor--st7--request.md" peer-st7
peer_activity peer-st7 sess-peer-st7 blocked 1
ups s-st7 - "$(idle_notice peer-st7 "$(clock_ago 5)")"
check "ST7: a peer that has since become blocked is told to be acted on, not ignored" 'echo "$OUT" | grep -q "stale idle notice: peer-st7 has since become blocked at.*act on that, not this notice" && ! echo "$OUT" | grep -q "ignore it"'

write_request "$OTHER_MSGS" 20260101-000000-st8a implementor st8 "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-st8a implementor st8 done 120
mark_dispatch 20260101-000000-st8a implementor s-st8 "$OTHER_MSGS/20260101-000000-st8a--implementor--st8--request.md" other-st8@alpha
ups s-st8 - "$(idle_notice peer-st8 "$(clock_ago 5)")"
check "ST8: a report addressed to another member does not make the notice about peer-st8 stale" '! echo "$OUT" | grep -q "stale"'

# ---- RB12: idle notice for a peer this session never dispatched to
ups s-rb12 - "$(idle_notice peer-x)"
check "RB12: no dispatch row for the peer injects nothing about it" '! echo "$OUT" | grep -q "went idle\|never sent to you"'

# ---- RB13: end to end — the peer never sends; L3 tells the Orchestrator, and L2 does when L3's injection is skipped
write_request "$OTHER_MSGS" 20260101-000000-rb13 implementor rb13 "$OLD" small
REQ13="$OTHER_MSGS/20260101-000000-rb13--implementor--rb13--request.md"
ups s-rb13p wrangl:implementor "$(wrapped "Implement spec X. [hierarchy-msg $REQ13]")"
write_response "$OTHER_MSGS" 20260101-000000-rb13 implementor rb13
mark_dispatch 20260101-000000-rb13 implementor s-rb13 "$REQ13" peer-x
peer_stop s-rb13p
check "RB13: the peer, whose report is owed, is blocked first" 'is_block'
liveness s-rb13
check "RB13: L2 surfaces the unsent report when L3's injection is skipped" 'is_block && echo "$OUT" | grep -q "20260101-000000-rb13--orchestrator--rb13--response.md"'
write_request "$OTHER_MSGS" 20260101-000000-r13b implementor rb13b "$OLD" small
write_response "$OTHER_MSGS" 20260101-000000-r13b implementor rb13b
mark_dispatch 20260101-000000-r13b implementor s-rb13b "$OTHER_MSGS/20260101-000000-r13b--implementor--rb13b--request.md" peer-x
ups s-rb13b - "$(idle_notice peer-x)"
check "RB13: L3 tells the Orchestrator at the idle notice" 'echo "$OUT" | grep -q "20260101-000000-r13b--orchestrator--rb13b--response.md"'

# ---- RB14: the same idle notice twice (a re-subscribe to a still-idle peer) is acted on once
write_request "$OTHER_MSGS" 20260101-000000-r14a implementor rb14 "$OLD" small
mark_dispatch 20260101-000000-r14a implementor s-rb14 "$OTHER_MSGS/20260101-000000-r14a--implementor--rb14--request.md" peer-y
ups s-rb14 - "$(idle_notice peer-y)"
check "RB14: the first notice injects its line" 'echo "$OUT" | grep -q "20260101-000000-r14a"'
ups s-rb14 - "$(idle_notice peer-y)"
check "RB14: the identical notice injects nothing the second time" '! echo "$OUT" | grep -q "20260101-000000-r14a"'

# ---- RB15: a notice that does not say "is idle now" fails open
ups s-rb15 - '[Cross-session idle notice] "peer-z", which you asked to be notified about, has gone away. This is an automated notice.'
check "RB15: an unrecognised idle notice injects the check-with-ListAgents line" 'echo "$OUT" | grep -q "idle notice about peer-z not recognised" && echo "$OUT" | grep -q ListAgents'

echo "---"; echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
