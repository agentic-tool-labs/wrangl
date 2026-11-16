#!/usr/bin/env bash
# Phase 6: checklist, Sitrep, ledger, UI and TUI (spec §5).
AUDIT_PHASE=6
. "$(dirname "$0")/lib-audit.sh"
audit_init 45
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"
start_orch || { echo "orch failed"; exit 9; }
spawn_member implementor --allow-main || { echo "impl failed"; exit 9; }
IMPL=$SPAWNED_PANE; IMPL_NAME=$SPAWNED_NAME; TXI=$SPAWNED_TX
HTTP() { curl -s --max-time "${2:-10}" --unix-socket "$AH_HOME/run/ahd.sock" "http://x$1"; }

if want D01; then begin D01
  do_in "$ORCH" d01 <<'EOF'
$AH roster checklist add "audit item one" --team $TEAM --cwd "$REPO"; echo "add rc=$?"
ID=$($AH roster checklist show --all --plain --team $TEAM --cwd "$REPO" | grep -oE 'b[0-9]+|[a-z_/-]*audit-item-one' | head -1); echo "id=$ID"
$AH roster checklist set "$ID" --state dispatched --note audit --team $TEAM --cwd "$REPO"; echo "set rc=$?"
$AH roster checklist show --plain --team $TEAM --cwd "$REPO"; echo "plain rc=$?"
$AH roster checklist show --all --team $TEAM --cwd "$REPO"; echo "all rc=$?"
$AH roster checklist show --stale --team $TEAM --cwd "$REPO"; echo "stale rc=$?"
$AH roster checklist show "$ID" --trail --team $TEAM --cwd "$REPO"; echo "trail rc=$?"
$AH roster checklist done "$ID" --note audit --team $TEAM --cwd "$REPO"; echo "done rc=$?"
EOF
  keep d01
  do_in "$IMPL" d01m <<'EOF'
$AH roster checklist add "member's item" --team $TEAM --cwd "$REPO"; echo "member rc=$?"
EOF
  keep d01m
  find "$HIER" -name '*.plan.jsonl' 2>/dev/null | scrub | ev D01 plan-files.txt
  chk "verbs exit 0" "out_of d01 | grep -q 'add rc=0' && out_of d01 | grep -q 'done rc=0' && out_of d01 | grep -q 'trail rc=0'"
  chk "plan file, or the DB plan table" "[ -s '$T/evidence/D01/plan-files.txt' ] || [ \"\$(sqlite3 -readonly '$AH_HOME/state/ah.db' 'select count(*) from plan_events')\" -gt 0 ]"
  chk "member refused" "out_of d01m | grep -qE 'member rc=[1-9]'"
  finish "checklist verbs" "add, set, done, show --plain --all --stale, show <id> --trail"
fi

# D02 and D03 share a dispatch: the page API watches the same steps.
if want D02 || want D03; then
  HTTP "/v1/view?path=/events" 2 >/dev/null
  V0=$SECONDS
  brief_to d02 "$IMPL_NAME" implementor d02-flow "Reply DONE."; R=$(req_of d02); keep d02
  sleep 5
  sq() { tail -8 "$HIER/progress/$TEAM/_run.plan.jsonl" 2>&1 | scrub | cut -c1-700; }
  # sitrep:<dirId>/<team>, dirId the first 8 hex of the dir's SHA-256 (ActivityLane.cs:26). Both spellings of the dir, as the canonical one is the daemon's.
  SIT() { local d id; for d in "$HIER" "$(python3 -I -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$HIER")"; do id=$(printf '%s' "$d" | shasum -a 256 | cut -c1-8); HTTP "/v1/view?path=/events&sub=sitrep:$id/$TEAM"; echo; done | sed -E 's/"as_of":"[^"]*"//g'; }
  sq | ev D02 plan-after-brief.txt
  SIT | scrub | head -c 6000 | ev D03 read-model-after-brief.txt
  printf 'Done.\n' >"$T/r-d02"
  do_in "$IMPL" d02r <<EOF
\$AH msg fill --id $R --from implementor --body-file $T/r-d02 --cwd "\$REPO"
\$AH msg send orchestrator@\$TEAM --kind report --ref $R --cwd "\$REPO"
EOF
  keep d02r
  sleep 5
  sq | ev D02 plan-after-report.txt
  SIT | scrub | head -c 6000 | ev D03 read-model-after-report.txt
  do_in "$ORCH" d02a <<EOF
\$AH msg ack $R --cwd "\$REPO"
\$AH roster checklist show --all --plain --team \$TEAM --cwd "\$REPO"
EOF
  keep d02a
  sq | ev D02 plan-after-ack.txt
  if want D02; then begin D02
    # The plan log's events are assign, start and ref (D02 first rerun's evidence); "dispatched" is the state they make.
    chk "dispatched with a cause" "grep -E '\"event\":\"(assign|start|ref)\"' '$T/evidence/D02/plan-after-brief.txt' | grep -q '\"cause\"'"
    chk "waiting-orch after the response" "grep -q 'waiting-orch' '$T/evidence/D02/plan-after-report.txt'"
    finish "automatic transitions" "msg new --item; member response; ack" "The item is auto-created by the note ah prints; verdict, ledger ship and withdraw legs not staged here. ah-sitrep-events.md:38 sends a DONE reply to in_review, the row says waiting-orch; plan-after-report.txt shows which."
  fi
  if want D03; then begin D03
    chk "read model answers" "[ -s '$T/evidence/D03/read-model-after-brief.txt' ]"
    chk "the row moved between the steps" "! cmp -s '$T/evidence/D03/read-model-after-brief.txt' '$T/evidence/D03/read-model-after-report.txt'"
    chk "sitrep rows present" "grep -q -i 'sitrep\|in_progress\|up_next' '$T/evidence/D03/read-model-after-report.txt'"
    finish "Sitrep event flow" "read model through each D02 step" "Measured across 5 s sleeps, so it shows the move happened, not its latency. Long-poll leg of /v1/view not timed; files keep the read-model- name but hold the sitrep slice, as_of removed."
  fi
fi

if want D04; then begin D04
  SHA=$(git -C "$REPO" rev-parse HEAD)
  git -C "$REPO" push -q origin main 2>/dev/null
  do_in "$IMPL" d04 <<EOF
\$AH ledger ship $SHA --team \$TEAM --cwd "\$REPO"; echo "ship rc=\$?"
\$AH ledger ship 0000000000000000000000000000000000000000 --team \$TEAM --cwd "\$REPO"; echo "bogus rc=\$?"
cd "\$REPO" && git commit -q --allow-empty -m d04 && git push -q origin main; echo "push rc=\$?"
EOF
  keep d04
  L=$(find "$HIER" -name 'ledger*.jsonl' | grep -F "$TEAM" | head -1)
  tail -5 "$L" 2>/dev/null | scrub | cut -c1-300 | ev D04 ledger.txt
  # The hook only sees a push made as a Bash call of its own, by a session that decides the team (PostToolUseLedgerRecord.cs:34).
  git -C "$REPO" commit -q --allow-empty -m d04b
  direct_in "$ORCH" "$ORCH_TX" "git -C $REPO push origin main" 90; P=$?
  sleep 5
  L=$(find "$HIER" -name 'ledger*.jsonl' | grep -F "$TEAM" | head -1)
  tail -5 "$L" 2>/dev/null | scrub | cut -c1-300 | ev D04 ledger-after-push.txt
  chk "ship rows" "[ -s '$T/evidence/D04/ledger.txt' ]"
  chk "bogus refused, nothing written" "out_of d04 | grep -q 'not found' && out_of d04 | grep -qE 'bogus rc=[1-9]'"
  chk "push row by the hook" "[ $P = 0 ] && ! cmp -s '$T/evidence/D04/ledger.txt' '$T/evidence/D04/ledger-after-push.txt'"
  finish "ledger ship" "ah ledger ship <sha>; bogus sha; git push to the sandbox remote" "This row answers ah-stage-timing.md's NEEDS-EVIDENCE on push rows."
fi

# D06 before D05: D05 clears orch's session.
if want D06; then begin D06
  sleep 30; wait_ready claude "$ORCH" 180
  do_in "$ORCH" d06 <<'EOF'
$AH roster timing --help 2>&1 | head -5
I=$($AH roster checklist show --all --plain --team $TEAM --cwd "$REPO" | grep -oE '^auto-[a-z0-9-]+' | head -1); echo "item=$I"
S=$(git -C "$REPO" rev-parse HEAD~2)
$AH roster timing --item "$I" --team $TEAM --cwd "$REPO"; echo "item rc=$?"
$AH roster timing --batch "$S" --team $TEAM --cwd "$REPO"; echo "batch rc=$?"
$AH roster timing --item "$I" --batch "$S" --team $TEAM --cwd "$REPO"; echo "both rc=$?"
EOF
  keep d06
  chk "item answered with stages" "out_of d06 | grep -q 'item rc=0' && out_of d06 | grep -qE 'build|orch|spec'"
  chk "both flags refused" "out_of d06 | grep -qE 'both rc=[1-9]'"
  chk "stages not all zero" "out_of d06 | grep -E '\"(spec|build|rework|review|orch|blocked|ship_wait|qa)\"' | grep -qvE ': *0,?$'"
  finish "stage timing" "roster timing --item, --batch, both together"
fi

if want D05; then begin D05
  # The block goes to a session that owns a team, at start, resume, clear or compaction (MemoryRecall.cs:9).
  # Compaction first: it keeps the session, so a miss there is not the new session's. Each waits for the hook's own payload.
  dslash() { local c0; c0=$(grep -c "\"source\":\"$2\"" "$T/hook-payloads.jsonl"); wait_ready claude "$ORCH" 120; tm send-keys -t "$ORCH" -l "$1"; sleep 0.5; tm send-keys -t "$ORCH" Enter; poll 180 "[ \"\$(grep -c '\"source\":\"$2\"' '$T/hook-payloads.jsonl')\" -gt $c0 ]"; }
  dslash /compact compact; DC=$?; sleep 15
  cat "$(dirname "$ORCH_TX")"/*.jsonl 2>/dev/null | grep -F 'AH MEMORY' | scrub | cut -c1-800 | head -2 | ev D05 block.txt
  dslash /clear clear; DL=$?; sleep 15
  cat "$(dirname "$ORCH_TX")"/*.jsonl 2>/dev/null | grep -F 'AH MEMORY' | scrub | cut -c1-800 | tail -2 | ev D05 block-clear.txt
  find "$HIER" -name 'ledger*.jsonl' | grep -F "$TEAM" | head -1 | while read -r f; do tail -3 "$f"; done | scrub | cut -c1-300 | ev D05 ledger.txt
  if [ "$DC" != 0 ] || [ "$DL" != 0 ]; then record D05 "ledger digest" "SessionStart after D04" NOT-TESTED "driver: no SessionStart payload: compact rc=$DC clear rc=$DL"; else
  chk "AH MEMORY block after /compact" "[ -s '$T/evidence/D05/block.txt' ]"
  chk "AH MEMORY block after /clear" "[ -s '$T/evidence/D05/block-clear.txt' ]"
  finish "ledger digest" "SessionStart after D04" "Fired by /compact, then /clear, on the team's owner: a member gets nothing by design."; fi
  ORCH_TX=$(ls -t "$(dirname "$ORCH_TX")"/*.jsonl | head -1)
fi

# Orchestrator's order: a row that needs drain, stop, ui or tui is not run past the daemon's user test.
if want D07; then record D07 "web UI auth, feeds" "ahd ui --print --no-open; auth; no cookie; AH_UI=off; second code use" NOT-TESTED "user-only gate, for the user. From an agent's shell: 'opened only by the user, from a terminal' (ui-out.txt)."; fi

if want D08; then begin D08
  record D08 "web UI renders live data" "headless browser on the live URL, both themes" NOT-TESTED "user-only gate, for the user. Needs D07's link. A browser window on the user's desktop is a leak by §3.5, so a headed retry was not tried."
fi

if want D09; then record D09 "TUI feed" "ahd tui --once" NOT-TESTED "user-only gate, for the user."; fi

if want D10; then begin D10
  HTTP /v1/read-model >"$T/rm.json"; HTTP "/v1/status?dir=$HIER" >"$T/st.json"; "$AHD" inspect status --dir "$HIER" >"$T/is.json" 2>&1
  for f in rm st is; do head -c 4000 "$T/$f.json" | scrub | ev D10 "$f.txt"; done
  head -c 4000 "$HIER/status.json" | scrub | ev D10 status-json.txt
  chk "read model answers" "[ -s '$T/rm.json' ]"
  chk "status answers" "[ -s '$T/st.json' ]"
  chk "team named in status and inspect" "grep -q '$TEAM' '$T/st.json' && grep -q '$TEAM' '$T/is.json'"
  chk "live member named in status" "grep -q '$IMPL_NAME' '$T/st.json'"
  finish "read model, status" "GET /v1/read-model, /v1/status, ahd inspect status" "Field-by-field equality with status.json not computed; the four documents are kept. /v1/read-model holds counters only, no names (ViewService.cs:365)."
fi

if want D11; then begin D11
  {
    "$AH" backlog add "audit item" --cwd "$REPO"; echo "add rc=$?"
    "$AH" backlog next --cwd "$REPO"; echo "next rc=$?"
    "$AH" backlog export --cwd "$REPO"; echo "export rc=$?"
    "$AH" backlog health --cwd "$REPO"; echo "health rc=$?"
  } >"$T/d11.out" 2>&1
  scrub <"$T/d11.out" | ev D11 verbs.txt
  restart_daemon
  "$AH" backlog export --cwd "$REPO" >"$T/d11b.out" 2>&1
  scrub <"$T/d11b.out" | ev D11 after-restart.txt
  chk "add exit 0" "grep -q 'add rc=0' '$T/d11.out'"
  chk "persists across a restart" "grep -q 'audit item' '$T/d11b.out'"
  finish "backlog" "add, next, export, health; restart; export" "set, claim, done, drop, import, groomed not walked. health rc is in verbs.txt: the row asks to confirm or drop that verb."
fi

if want D12; then begin D12
  S=$(git -C "$REPO" rev-parse HEAD); printf '#!/bin/sh\nexit 0\n' >"$T/fix.sh"
  export AH_SLOTS_DIR="$T/slots"; mkdir -p "$AH_SLOTS_DIR"
  do_in "$ORCH" d12 <<EOF
\$AH qa submit --repo "\$REPO" --sha $S --set audit --priority adhoc --json --cwd "\$REPO"; echo "submit rc=\$?"
\$AH qa list --cwd "\$REPO"; echo "list rc=\$?"
\$AH qa status --cwd "\$REPO"; echo "status rc=\$?"
AH_SLOTS_DIR=$T/slots \$AH test run -- sh $T/fix.sh; echo "slot rc=\$?"
ls -A $T/slots; echo "slots left listed"
EOF
  keep d12
  chk "submit" "out_of d12 | grep -q 'submit rc=0'"
  chk "list shows it" "out_of d12 | grep -q 'audit'"
  chk "test run exit 0" "out_of d12 | grep -q 'slot rc=0'"
  finish "QA queue, test slots" "qa submit, list, status; ah test run" "next, done and re-submit (cached PASS) need a QA engineer's proven address: not staged."
fi

if want D13; then begin D13
  N0=$(live_decision_lines)
  do_in "$ORCH" d13 <<'EOF'
$AH msg decision list --team $TEAM --cwd "$REPO"; echo "list rc=$?"
EOF
  keep d13
  N1=$(live_decision_lines)
  if [ "$N0" = "$N1" ] && out_of d13 | grep -q 'list rc=2'; then
    record D13 "pipeline decision log" "read-only: decision list with no run" PASS "exit 2, no audit row added. add and answer are not run: they write to the passwd home by design (§3.5)."
  else
    record D13 "pipeline decision log" "read-only: decision list with no run" FAIL "expected exit 2 and no new row; lines $N0 -> $N1; see d13.txt. add and answer not run (§3.5)."
  fi
fi

canaries_or_stop end-6
echo "PHASE 6 DONE: $(cut -f4 "$T/rows.tsv" | sort | uniq -c | tr '\n' ' ')"
