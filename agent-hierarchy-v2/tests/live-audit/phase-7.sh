#!/usr/bin/env bash
# Phase 7: DB backup and restore, daemon (spec §5). 90 min budget, 70 of it B03's wait.
AUDIT_PHASE=7
. "$(dirname "$0")/lib-audit.sh"
audit_init 90
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"; ST="$AH_HOME/state"
start_orch || { echo "orch failed"; exit 9; }
spawn_member implementor --allow-main || { echo "impl failed"; exit 9; }
IMPL=$SPAWNED_PANE; IMPL_NAME=$SPAWNED_NAME; TXI=$SPAWNED_TX
T0=$SECONDS
do_ah() { "$AH" "$@" --cwd "$REPO" 2>&1; }
# `ah db` matches exact arg lists and takes no --cwd (DbVerb.cs): AH_HOME in the env selects the sandbox.
do_db() { "$AH" db "$@" 2>&1; }
daemon_pid() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "$AH_HOME/run/ahd.json" 2>/dev/null; }
stop_daemon_hard() { local p s; p=$(daemon_pid); s=$(ah_pid_stamp "$p"); "$AHD" stop >>"$T/cap/restarts.txt" 2>&1; sleep 5; [ "$(ah_pid_stamp "$p")" = "$s" ] && [ -n "$s" ] && ah_signal_stamped KILL "$p" "$s"; sleep 1; }

if want B01; then begin B01
  { do_db backup --label audit; echo "backup rc=$?"; do_db backup --label audit; echo "collision rc=$?"; do_db backup --label 'bad label!'; echo "bad rc=$?"; do_db list; echo "list rc=$?"; do_db check; echo "check rc=$?"; } >"$T/b01.out"
  scrub <"$T/b01.out" | ev B01 verbs.txt
  ls "$ST/backups" 2>/dev/null | ev B01 files.txt
  F=$(ls "$ST"/backups/*audit*.db 2>/dev/null | head -1)
  chk "backup file named per spec" "grep -qE 'ah-.*-v[0-9]+-audit.db' '$T/evidence/B01/files.txt'"
  chk "opens with integrity ok" "[ \"\$(sqlite3 -readonly '$F' 'pragma integrity_check')\" = ok ]"
  chk "bad label refused" "grep -qE 'bad rc=[1-9]' '$T/b01.out'"
  chk "db check ok" "grep -q 'check rc=0' '$T/b01.out'"
  finish "backup, list, check" "db backup --label audit with a live team; collision; bad label" "Collision's rc in verbs.txt."
fi

if want B02; then begin B02
  note_to "$ORCH" b02-a "$IMPL_NAME" "audit before restore"
  do_db backup --label preb2 >"$T/b02-b.out"
  F=$(ls -t "$ST"/backups/*preb2*.db | head -1); FN=$(basename "$F")
  cnt() { sqlite3 -readonly "$ST/ah.db" "select (select count(*) from mail_entries where rowid <= ${B2MAX:-(select max(rowid) from mail_entries)}), (select count(*) from peer_records), (select count(*) from status_entries)"; }
  # Entries the backup holds; a later count of the same rowids shows what restore brought back. sqlite3 errors stay visible as an empty count.
  B2MAX=$(sqlite3 -readonly "$F" 'select max(rowid) from mail_entries')
  C0=$(cnt)
  do_in "$ORCH" b02-c <<'EOF'
$AH roster checklist add "audit after the backup" --team $TEAM --cwd "$REPO"
$AH msg new --to implementor --from orchestrator --slug b02-after --title "Add a later row" --team $TEAM --body-file /dev/null --cwd "$REPO" 2>&1 | head -2
EOF
  { do_db restore "$FN"; echo "dry rc=$?"; do_db restore "$FN" --apply; echo "up rc=$?"; } >"$T/b02.out"
  stop_daemon_hard
  { do_db restore "$FN" --apply; echo "apply rc=$?"; } >>"$T/b02.out" 2>&1
  start_daemon
  scrub <"$T/b02.out" | ev B02 verbs.txt
  C1=$(cnt)
  ls "$ST/replaced" 2>/dev/null | ev B02 replaced.txt
  printf 'counts before %s; after restore %s\n' "$C0" "$C1" | ev B02 counts.txt
  note_to "$ORCH" b02-d "$IMPL_NAME" "audit after restore"; E=$(entry_of b02-d); keep b02-d
  poll 120 "m04_check '$E' '$TXI'"; D=$?
  chk "dry run touches nothing" "grep -q 'dry rc=0' '$T/b02.out'"
  chk "apply with the daemon up refused" "grep -qE 'up rc=[1-9]' '$T/b02.out'"
  chk "apply with the daemon down" "grep -q 'apply rc=0' '$T/b02.out'"
  chk "moved-aside copies" "[ -s '$T/evidence/B02/replaced.txt' ]"
  chk "counts back" "[ '$C0' = '$C1' ]"
  chk "a real member gets mail afterwards" "[ $D = 0 ]"
  finish "restore" "note counts; change; dry run; --apply up (refused); stop, apply, restart"
fi

if want B04; then begin B04
  mkdir -p "$ST/backups"
  F=$(ls -t "$ST"/backups/*.db 2>/dev/null | head -1)
  D="$ST/backups/ah-20200101T000000Z-v99-damaged.db"
  cp "$F" "$D"; dd if=/dev/urandom of="$D" bs=4096 seek=1 count=4 conv=notrunc 2>/dev/null
  { do_db list; echo "list rc=$?"; do_ah roster doctor; echo "doctor rc=$?"; do_db restore "$(basename "$D")"; echo "restore rc=$?"; } >"$T/b04.out"
  scrub <"$T/b04.out" | cut -c1-400 | ev B04 verbs.txt
  rm -f "$ST/backups/ah-20200101T000000Z-v99-damaged.db"
  chk "FAILED in db list" "grep -q 'FAILED' '$T/b04.out'"
  chk "restore refuses or warns" "grep -qE 'restore rc=[1-9]' '$T/b04.out' || sed -n '/doctor rc/,\$p' '$T/b04.out' | grep -qiE 'fail|warn|damag|corrupt'"
  finish "integrity failure" "a damaged copy of a sandbox backup in the backups dir"
fi

if want B05; then record B05 "state-backup.sh, state-restore.sh" "plan, then apply inside the sandbox" NOT-TESTED "user-only gate, for the user. The script refuses a live daemon and says to run ahd stop (plan.txt), which an agent's shell is refused."; fi

if want B06; then begin B06
  {
    for v in status health version "logs -n 50" "inspect boxes" "inspect entries --address orchestrator@$TEAM --all" "inspect registrations" "inspect timers" "inspect events" "inspect status" check contract; do
      echo "--- ahd $v"; "$AHD" $v 2>&1 | cut -c1-300 | head -12; echo "rc=${PIPESTATUS[0]}"
    done
  } >"$T/b06.out"
  scrub <"$T/b06.out" | ev B06 verbs.txt
  chk "ahd check exit 0" "sed -n '/--- ahd check/,/rc=/p' '$T/b06.out' | grep -q 'rc=0'"
  chk "sandbox data in inspect" "grep -q '$TEAM' '$T/b06.out'"
  chk "no live name" "! grep -qE 'ah2-rc[0-9]' '$T/b06.out'"
  finish "ahd operations" "status, health, version, logs, inspect *, check, contract"
fi

if want B07; then begin B07
  note_to "$ORCH" b07-a "$IMPL_NAME" "audit before the crash"; EB7=$(entry_of b07-a)
  P=$(daemon_pid); S=$(ah_pid_stamp "$P"); ah_signal_stamped KILL "$P" "$S"; sleep 1
  do_ah msg daemon status >"$T/b07-1.out"
  # From a session: a bare harness shell is refused as anonymous.
  do_in "$ORCH" b07-s <<'EOF'
$AH msg send orchestrator@$TEAM --kind note --text "audit after the crash" --cwd "$REPO"; echo "send rc=$?"
EOF
  out_of b07-s >"$T/b07-2.out"
  sleep 3
  do_ah msg daemon status >"$T/b07-3.out"
  P2=$(daemon_pid)
  cat "$T/b07-1.out" "$T/b07-2.out" "$T/b07-3.out" | scrub | cut -c1-300 | ev B07 outputs.txt
  pgrep -f "$AH_HOME/bin/ahd" 2>/dev/null | wc -l | tr -d ' ' | ev B07 daemons.txt
  "$AHD" inspect entries --address "$IMPL_NAME@$TEAM" --all 2>&1 | grep -cF "${EB7:-no-entry-id}" | ev B07 entry-count.txt
  chk "auto-respawn" "[ -n '$P2' ] && [ '$P2' != '$P' ] && [ \"\$(ah_pid_stamp '$P2')\" != '' ]"
  chk "one daemon" "[ \"\$(cat '$T/evidence/B07/daemons.txt')\" = 1 ]"
  chk "entry intact" "[ \"\$(cat '$T/evidence/B07/entry-count.txt')\" -ge 1 ]"
  finish "crash recovery" "kill -9 daemon with mail; next CLI call" "Note: daemon status never starts it (docs); the send is the first call that may. Dispatch clock resume not staged."
fi

if want B09; then begin B09
  {
    do_db export --domain mail --out "$T/exp"; echo "export rc=$?"; do_db status-rebuild; echo "rebuild rc=$?"
    do_db prune --unplaced; echo "prune rc=$?"
  } >"$T/b09.out"
  scrub <"$T/b09.out" | cut -c1-300 | ev B09 verbs.txt
  sqlite3 -readonly "$ST/ah.db" "select key,value from meta where key like 'mode.%'" | ev B09 meta.txt
  chk "export exit 0" "grep -q 'export rc=0' '$T/b09.out'"
  chk "verify exit 0" "grep -q 'verify rc=0' '$T/b09.out'"
  chk "prune without --yes deletes nothing" "! grep -qiE '\"deleted\": *[1-9]' '$T/b09.out'"
  finish "db export, verify, modes" "db export, verify, status-rebuild; meta modes; prune --unplaced without --yes" "cutover and cutback deliberately not run."
fi

if want B10; then record B10 "excluded verbs" "ahd login, home trust, roster permissions, issue-intake, allow, rules, TUI mod" NOT-TESTED "sandbox-cannot-host: excluded by the spec, each reaching outside the sandbox or one-way. Recorded, not skipped silently."; fi

# B03 waits past 60 min of live team; the team keeps its daemon throughout.
if want B03; then begin B03
  N0=$(ls "$ST/backups" 2>/dev/null | grep -vcE 'audit|preb2')
  while [ $((SECONDS - T0)) -lt 4200 ]; do sleep 60; done
  ls -l "$ST/backups" 2>/dev/null | scrub | ev B03 files.txt
  N1=$(ls "$ST/backups" 2>/dev/null | grep -vcE 'audit|preb2')
  chk "a new automatic snapshot ($N0 -> $N1)" "[ '$N1' -gt '$N0' ]"
  chk "retention at most 10" "[ '$N1' -le 10 ]"
  finish "backup job" "live team in the sandbox past 60 min" "Idle-DB control not staged. Waited $((SECONDS - T0)) s."
fi

# Orchestrator's order: a row that needs drain, stop, ui or tui is not run past the daemon's user test.
if want B08; then record B08 "idle exit, drain, upgrade" "drain; AHD_IDLE_EXIT_MS short; AHD_VERSION_OVERRIDE old client" NOT-TESTED "user-only gate, for the user. An agent's shell is refused drain and stop. One pane drain ran before the order took effect: pane-proof.txt, Findings."; fi

[ -S "$AH_HOME/run/ahd.sock" ] && canaries_or_stop end-7
echo "PHASE 7 DONE: $(cut -f4 "$T/rows.tsv" | sort | uniq -c | tr '\n' ' ')"
