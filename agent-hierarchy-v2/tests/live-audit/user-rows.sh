#!/usr/bin/env bash
# Audit rows the daemon refuses from an agent's shell: D07, D09, B05, B08. The user pastes this into their own terminal.
#   bash user-rows.sh              every row; AUDIT_FILTER="D07 B08" runs only those
#   bash user-rows.sh --self-test  only the predicates, each against an input that must not match; no daemon, no copy
# Works on a copy of the real home under /private/tmp/ahprobe.XXXXXX with its own AH_TEST=1 daemon. The real daemon is never stopped,
# drained or signalled, and nothing is written outside the probe root. No browser, no window. Prints a results file path at the end.
USER_ROWS_BUDGET_S=${USER_ROWS_BUDGET_S:-1200}
USER_ROWS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
REAL_USER_HOME=$(python3 -I -c 'import os,pwd; print(os.path.realpath(pwd.getpwuid(os.getuid()).pw_dir))')
[ -n "$REAL_USER_HOME" ] || { echo "user-rows: cannot read the real home" >&2; exit 1; }
HOME=$REAL_USER_HOME
unset WRANGL_HIERARCHY_DIR
PROBE_NEED_CLAUDE=0
AUDIT_PHASE=user
. "$USER_ROWS_DIR/lib-audit.sh"
REAL_AH="$REAL_USER_HOME/.claude/ah2"

# ---- predicates. Pure, so --self-test can feed each one an input that must not match.
# resolves_real <path>: the path is the real home, or the real ah home or anything in it.
resolves_real() {
  local r; r=$(python3 -I -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$1")
  [ "$r" = "$REAL_USER_HOME" ] && return 0
  case "$r/" in "$REAL_AH"/*) return 0 ;; esac
  return 1
}
# plan_outside <root>: stdin is a state script's plan. Prints each absolute path outside the root; 0 when there is one.
plan_outside() {
  local out
  out=$(grep -oE '(^|[[:space:]=])/[^[:space:]),]+' | sed -E 's/^[[:space:]=]//' | grep -vE "^$1(/|\$)")
  [ -n "$out" ] && printf '%s\n' "$out"
}
# only_loopback: stdin is `lsof -nP -iTCP -sTCP:LISTEN`; at least one listener, and each on 127.0.0.1.
only_loopback() {
  local l; l=$(grep LISTEN)
  [ -n "$l" ] && ! printf '%s\n' "$l" | grep -qv ' 127\.0\.0\.1:[0-9]* (LISTEN)'
}
# gate_refused: stdin is a command's output; the daemon's user gate said no. From an agent's shell this is expected, and not a result.
gate_refused() { grep -qE 'opened only by the user|neither a session nor|needs a caller|refused to (stop|drain)|drain_refused|user_only'; }
# worst: stdin is PASS, FAIL, NOT-TESTED lines; prints the row's verdict.
worst() {
  local s; s=$(cat)
  case "$s" in *FAIL*) echo FAIL ;; *NOT-TESTED*) echo NOT-TESTED ;; "") echo NOT-TESTED ;; *) echo PASS ;; esac
}

self_test() {
  local bad=0
  st() { local name=$1 want=$2; shift 2; if eval "$*" >/dev/null 2>&1; then got=0; else got=1; fi; [ "$got" = "$want" ] && r=ok || { r=BAD; bad=1; }; echo "self-test $r: $name"; }
  local lo='ahd 1 u 9u IPv4 0t0 TCP 127.0.0.1:7000 (LISTEN)' any='ahd 1 u 9u IPv4 0t0 TCP *:7000 (LISTEN)' lan='ahd 1 u 9u IPv4 0t0 TCP 192.168.1.9:7000 (LISTEN)'
  st "real home resolves real" 0 "resolves_real '$REAL_USER_HOME'"
  st "real ah home resolves real" 0 "resolves_real '$REAL_AH'"
  st "probe path is not real" 1 "resolves_real /private/tmp/ahprobe.x/home"
  st "the real home's sibling is not real" 1 "resolves_real '$REAL_USER_HOME-other'"
  st "plan inside the root" 1 "printf '  d1  ah_home  /private/tmp/ahprobe.x/h  (3 files)\n' | plan_outside /private/tmp/ahprobe.x"
  st "plan names a path outside" 0 "printf '  d1  ah_home  /etc/x  (3 files)\n' | plan_outside /private/tmp/ahprobe.x"
  st "plan names the root's sibling" 0 "printf '  d1  ah_home  /private/tmp/ahprobe.xy/h  (3 files)\n' | plan_outside /private/tmp/ahprobe.x"
  st "plan move target outside" 0 "printf '  move aside  /private/tmp/ahprobe.x/h  ->  /Users/j/h.native\n' | plan_outside /private/tmp/ahprobe.x"
  st "loopback listener" 0 "printf '%s\n' '$lo' | only_loopback"
  st "wildcard listener" 1 "printf '%s\n' '$any' | only_loopback"
  st "LAN listener" 1 "printf '%s\n' '$lan' | only_loopback"
  st "second listener off loopback" 1 "printf '%s\n%s\n' '$lo' '$lan' | only_loopback"
  st "no listener" 1 "printf '' | only_loopback"
  st "gate refusal" 0 "echo 'ahd ui: the UI bridge is opened only by the user, from a terminal' | gate_refused"
  st "ui disabled is not a gate refusal" 1 "echo 'ahd ui: browser UI disabled' | gate_refused"
  st "worst FAIL" 0 "[ \"\$(printf 'PASS\nFAIL\nNOT-TESTED\n' | worst)\" = FAIL ]"
  st "worst NOT-TESTED" 0 "[ \"\$(printf 'PASS\nNOT-TESTED\n' | worst)\" = NOT-TESTED ]"
  st "worst PASS" 0 "[ \"\$(printf 'PASS\nPASS\n' | worst)\" = PASS ]"
  st "no check is not a pass" 0 "[ \"\$(printf '' | worst)\" = NOT-TESTED ]"
  return "$bad"
}

# ---- results
# check <row.name> <PASS|FAIL|NOT-TESTED> <why>: the one way a result is reported.
check() {
  case "$2" in PASS | FAIL | NOT-TESTED) ;; *) echo "user-rows: bad result '$2' for $1" >&2; exit 1 ;; esac
  printf '%-10s %-24s %s\n' "$2" "$1" "$3" | tee -a "$RESULTS"
}
# judge <row.name> <shell test> <why when it holds> <why when not>
judge() { if eval "$2" >/dev/null 2>&1; then check "$1" PASS "$3"; else check "$1" FAIL "$4"; fi; }
not_tested() { local why=$1 n; shift; for n in "$@"; do check "$n" NOT-TESTED "$why"; done; }

# ---- the copy
daemon_pid() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "$AH_HOME/run/ahd.json" 2>/dev/null; }
daemon_alive() { local p; p=$(daemon_pid); [ -n "$p" ] && kill -0 "$p" 2>/dev/null; }
# stop_own: our own daemon, by its own pid: TERM, then KILL. Never the real one: the pid comes from the copy's ahd.json, and the copy starts without one.
stop_own() {
  local p s i
  if resolves_real "$AH_HOME"; then echo "user-rows: stop_own refused: AH_HOME is the real home" >&2; return 1; fi
  p=$(daemon_pid); s=$(ah_pid_stamp "$p")
  [ -n "$s" ] || return 0
  ah_signal_stamped TERM "$p" "$s"
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do [ "$(ah_pid_stamp "$p")" = "$s" ] || return 0; sleep 1; done
  ah_signal_stamped KILL "$p" "$s"; sleep 1
}
# stop_job <pid> <stamp>: one of our own clients, TERM then KILL.
stop_job() {
  local i
  ah_signal_stamped TERM "$1" "$2"
  for i in 1 2 3 4 5; do [ "$(ah_pid_stamp "$1")" = "$2" ] || return 0; sleep 1; done
  ah_signal_stamped KILL "$1" "$2"
}
log_since() { sed -n "$((${1:-0} + 1)),\$p" "$AH_HOME/logs/ahd.log" 2>/dev/null; }
log_mark() { wc -l <"$AH_HOME/logs/ahd.log" 2>/dev/null | tr -d ' ' || echo 0; }

# Stands in for tools nothing in a run should reach: the real sessions' CLIs, and the system opener that would show a window.
write_refusing_shims() {
  local n
  for n in claude codex open xdg-open; do
    printf '#!/bin/sh\necho "user-rows: %s is refused here" >&2\necho "%s $*" >>"%s/shim-called"\nexit 99\n' "$n" "$n" "$T" >"$T/bin/$n"
    chmod +x "$T/bin/$n"
  done
}

# copy_home: from the real home, read only, what these rows need: the binaries, the database (rewritten below), its authority marker, and looks.json
# if it names no real path. An allowlist, since any other copied file can name real paths or live pids the test daemon could act on. So not:
#   run/        a copied ahd.json names the REAL daemon's pid and socket; the ui-* records make the test daemon open a listener (worktree.md, Probes)
#   t/          symlinks to real team tmp directories
#   registrations/, known-dirs, login.json  they name live sessions and real paths, and login's stay flag vetoes idle exit (B08)
#   hierarchy/ mailbox/ mailbox-notes/ session-roles permissions install wake/ stall/ overdue/ recover/ statusline/ *.state  real paths and live sessions
#   qa-results/ archive/ dogfood/ tmp/ logs/ state/backups/ state/timeline.db  gigabytes and history no row reads
copy_home() {
  local n src=$REAL_AH
  mkdir -p "$AH_HOME/state" "$AH_HOME/run"
  cp -RP "$src/bin" "$AH_HOME/" || return 1
  [ -f "$src/looks.json" ] && ! grep -qF "$REAL_USER_HOME" "$src/looks.json" && cp -P "$src/looks.json" "$AH_HOME/"
  cp -P "$src/state/ah.db.authority.json" "$AH_HOME/state/" || return 1
  # A consistent read of a database the real daemon holds open.
  sqlite3 -readonly "$src/state/ah.db" ".backup '$AH_HOME/state/ah.db'" || return 1
  find "$AH_HOME" -type l -exec rm -f {} + 2>/dev/null
  printf '{"version":1,"dirs":{}}\n' >"$AH_HOME/agent-hierarchy.known-dirs.json"
  rewrite_db_paths
}
# The test daemon refuses a home whose database names a path outside the probe root, and the start check sees only some of them: sessions come
# back as registrations, and cache_files and msg_messages hold paths it exports to. So every text cell has its absolute paths moved under the root and
# its pids zeroed, and the sessions and members rows, which name live real processes, go. Copy only: the real database was read by .backup above.
rewrite_db_paths() {
  python3 -I - "$AH_HOME/state/ah.db" "$T" <<'PY'
import re, sqlite3, sys
db, root = sys.argv[1:3]
PATH = re.compile(r"(?:(?<![\w.\-/~])|(?<=file://))(/(?:Users|private|tmp|var|home)/)")
PID = re.compile(r'("\w*pid"\s*:\s*)\d+')
def move(v):
    return PID.sub(r"\g<1>0", PATH.sub(lambda m: root + m.group(1), v))
c = sqlite3.connect(db)
c.create_function("ur_move", 1, move, deterministic=True)
for (t,) in c.execute("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('sessions','members','sessions_unplaced','members_unplaced')").fetchall():
    c.execute(f'DELETE FROM "{t}"')
for (t,) in c.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").fetchall():
    for row in c.execute(f'PRAGMA table_info("{t}")').fetchall():
        col = row[1]
        c.execute(f"""UPDATE "{t}" SET "{col}" = ur_move("{col}") WHERE typeof("{col}") = 'text' AND "{col}" LIKE '%/%' OR typeof("{col}") = 'text' AND "{col}" LIKE '%pid"%'""")
c.commit()
# The test-mode start check reads through a read-only connection, which cannot open a WAL file with no -shm beside it.
c.execute("PRAGMA journal_mode=DELETE")
c.close()
PY
}

# ---- canaries: the real home, read only
real_db_stat() { stat -f '%z %m' "$REAL_AH/state/ah.db" 2>/dev/null || echo absent; }
canary_start() {
  REAL_STAT0=$(real_db_stat)
  REAL_PID0=$(live_pid)
  REAL_STAMP0=$(ah_pid_stamp "$REAL_PID0")
}
canary_end() {
  local s p st hits logs
  s=$(real_db_stat); p=$(live_pid); st=$(ah_pid_stamp "$p")
  { echo "real ah.db size mtime before: $REAL_STAT0"; echo "real ah.db size mtime after:  $s"; echo "real daemon before: $REAL_PID0 $REAL_STAMP0"; echo "real daemon after:  $p $st"; } | ev canary real.txt
  judge canary.real-daemon "[ '$p' = '$REAL_PID0' ] && [ '$st' = '$REAL_STAMP0' ]" "pid $p, same start time as before the run" "real daemon was $REAL_PID0 ($REAL_STAMP0), now $p ($st)"
  hits=$(live_db_hits "${T##*/}") || hits=read-failed
  logs=$(cat "$REAL_AH/logs/ahd.log" "$REAL_AH/hierarchy/hook-errors.jsonl" 2>/dev/null | grep -cF "${T##*/}")
  echo "real DB rows naming this run: $hits; real log lines naming this run: $logs" | ev canary names.txt
  judge canary.names "[ '$hits' = 0 ] && [ '$logs' = 0 ]" "no row or log line in the real home names this run" "real home names this run: $hits DB rows, $logs log lines"
  if [ "$s" = "$REAL_STAT0" ]; then
    check canary.real-db PASS "real ah.db size and mtime unchanged ($s)"
  elif [ "$st" = "$REAL_STAMP0" ] && [ -n "$st" ]; then
    check canary.real-db NOT-TESTED "real ah.db moved ($REAL_STAT0 -> $s) with the real daemon up: it writes that file itself, so size and mtime prove nothing; canary.names is the evidence"
  else
    check canary.real-db FAIL "real ah.db changed ($REAL_STAT0 -> $s) with no real daemon to explain it"
  fi
}

# ---- rows
GUARD_ROUTES='GET:/ui/v1/looks POST:/ui/v1/looks POST:/ui/v1/looks/merge POST:/ui/v1/answer POST:/ui/v1/team-close GET:/ui/v1/view GET:/ui/v1/read-model GET:/ui/v1/list/x GET:/ui/v1/actions'
DATA_ROUTES='/ui/v1/view /ui/v1/read-model /ui/v1/looks /ui/v1/actions'
http() { curl -sS --noproxy '*' --max-time 10 "$@"; }

row_d07() {
  local out="$T/cap/ui.out" err="$T/cap/ui.err" pid stamp port base i r m url bad=
  local rest="D07.loopback D07.foreign-origin D07.data D07.ui-off"
  daemon_alive || start_daemon
  "$AHD" ui --print --no-open >"$out" 2>"$err" &
  pid=$!; stamp=$(ah_pid_stamp "$pid"); probe_record_pid "$pid"
  poll 15 "grep -q '/ui/' '$out' || ! kill -0 $pid"
  cat "$out" "$err" | scrub | ev D07 ui-out.txt
  if ! grep -q '/ui/' "$out"; then
    stop_job "$pid" "$stamp"
    if gate_refused <"$err"; then
      # shellcheck disable=SC2086
      not_tested "the daemon's user gate refused: run this from your own terminal, not an agent's shell" D07.link $rest
    else
      check D07.link FAIL "no link in 15 s: $(head -1 "$err" | scrub | cut -c1-160)"
      # shellcheck disable=SC2086
      not_tested "no link to test with" $rest
    fi
    return
  fi
  check D07.link PASS "ahd ui --print --no-open printed a /ui/ link; no window"
  url=$(grep -oE 'http://127\.0\.0\.1:[0-9]+/ui/' "$out" | head -1)
  port=$(printf '%s' "$url" | sed -E 's|^http://127\.0\.0\.1:([0-9]+)/.*|\1|'); base="http://127.0.0.1:$port"

  lsof -nP -iTCP:"$port" -sTCP:LISTEN >"$T/cap/lsof.txt" 2>&1
  ev D07 lsof.txt <"$T/cap/lsof.txt"
  judge D07.loopback "[ -n '$(daemon_pid)' ] && only_loopback <'$T/cap/lsof.txt' && grep -q '[^0-9]$(daemon_pid) ' '$T/cap/lsof.txt'" "port $port listens on 127.0.0.1 only, held by this run's daemon" "off loopback, or not this run's daemon: lsof.txt"

  for r in $GUARD_ROUTES; do
    m=${r%%:*}; i=$(http -o /dev/null -w '%{http_code}' -X "$m" -H 'Origin: http://evil.example' "$base${r#*:}")
    echo "$m ${r#*:} $i" >>"$T/cap/foreign-origin.txt"
    [ "$i" = 403 ] || bad="$bad $m:${r#*:}=$i"
  done
  ev D07 foreign-origin.txt <"$T/cap/foreign-origin.txt"
  judge D07.foreign-origin "[ -z '$bad' ]" "every /ui/v1 route answered 403 to a foreign Origin" "with a foreign Origin:$bad"

  bad=
  for r in $DATA_ROUTES; do
    i=$(http -o "$T/cap/data.tmp" -w '%{http_code}' "$base$r")
    echo "GET $r $i $(wc -c <"$T/cap/data.tmp" | tr -d ' ') bytes" >>"$T/cap/data.txt"
    { [ "$i" = 200 ] && [ -s "$T/cap/data.tmp" ]; } || bad="$bad $r=$i"
  done
  ev D07 data.txt <"$T/cap/data.txt"
  judge D07.data "[ -z '$bad' ]" "each read route answered 200 with a body, with no sign-in" "with no sign-in:$bad"
  stop_job "$pid" "$stamp"

  # AH_UI is read by the daemon, so it takes a daemon started with it. Exit 3 alone is not enough: the user gate exits 3 too.
  restart_daemon AH_UI=off
  AH_UI=off perl -e 'alarm 20; exec @ARGV' "$AHD" ui --print --no-open >"$T/cap/off.out" 2>&1
  i=$?
  { echo "rc=$i"; scrub <"$T/cap/off.out"; } | ev D07 ui-off.txt
  if gate_refused <"$T/cap/off.out"; then
    check D07.ui-off NOT-TESTED "the user gate answered first: run from your own terminal"
  else
    judge D07.ui-off "[ '$i' = 3 ] && grep -q 'browser UI disabled' '$T/cap/off.out'" "AH_UI=off: exit 3, browser UI disabled" "AH_UI=off: rc=$i, ui-off.txt"
  fi
  restart_daemon
}

row_b05() {
  local out="$T/b05-out" root=$T plan="$T/cap/b05-plan.txt" rp="$T/cap/b05-restore-plan.txt" o sum0 sum1
  stop_own
  (cd "$T/ws" && "$T/plugin/scripts/state-backup.sh") >"$plan" 2>&1
  scrub <"$plan" | ev B05 plan.txt
  if o=$(plan_outside "$root" <"$plan"); then
    printf '%s\n' "$o" | scrub | ev B05 outside.txt
    not_tested "the backup plan names a path outside the copy, nothing applied: $(head -1 <<<"$o" | scrub)" B05.plan B05.apply B05.backup-last B05.restore-plan B05.round-trip
    return
  fi
  if ! grep -qE '^  [df][0-9]+  ' "$plan"; then
    check B05.plan FAIL "the plan lists no source: plan.txt"
    not_tested "no plan to apply" B05.apply B05.backup-last B05.restore-plan B05.round-trip
    return
  fi
  check B05.plan PASS "every path in the backup plan is inside the copy"
  (cd "$T/ws" && "$T/plugin/scripts/state-backup.sh" --apply --out "$out") >"$T/cap/b05-apply.txt" 2>&1
  o=$?
  scrub <"$T/cap/b05-apply.txt" | cut -c1-300 | ev B05 apply.txt
  judge B05.apply "[ '$o' = 0 ] && [ -d '$out/payload' ]" "backup applied into the copy" "backup rc=$o: apply.txt"
  judge B05.backup-last "[ -f '$out/BACKUP.txt' ] && [ -z \"\$(find '$out/payload' -type f -newer '$out/BACKUP.txt' | head -1)\" ]" "BACKUP.txt exists and no payload file is newer" "BACKUP.txt missing or not last"

  # Round trip: change the copy, restore, and the change is gone with the database as it was.
  sum0=$(shasum -a 256 <"$AH_HOME/state/ah.db")
  echo tampered >"$AH_HOME/user-rows-marker"
  sqlite3 "$AH_HOME/state/ah.db" "create table user_rows_marker(x)"
  (cd "$T/ws" && "$T/plugin/scripts/state-restore.sh" --from "$out") >"$rp" 2>&1
  scrub <"$rp" | ev B05 restore-plan.txt
  if o=$(plan_outside "$root" <"$rp"); then
    printf '%s\n' "$o" | scrub | ev B05 outside-restore.txt
    not_tested "the restore plan names a path outside the copy, not applied: $(head -1 <<<"$o" | scrub)" B05.restore-plan B05.round-trip
    return
  fi
  check B05.restore-plan PASS "every path in the restore plan is inside the copy"
  (cd "$T/ws" && "$T/plugin/scripts/state-restore.sh" --from "$out" --apply) >"$T/cap/b05-restore.txt" 2>&1
  o=$?
  scrub <"$T/cap/b05-restore.txt" | cut -c1-300 | ev B05 restore.txt
  sum1=$(shasum -a 256 <"$AH_HOME/state/ah.db")
  { echo "restore rc=$o"; echo "ah.db sum before tamper: $sum0"; echo "ah.db sum after restore: $sum1"; [ -e "$AH_HOME/user-rows-marker" ] && echo "marker still there"; } | ev B05 round-trip.txt
  judge B05.round-trip "[ '$o' = 0 ] && [ '$sum0' = '$sum1' ] && [ ! -e '$AH_HOME/user-rows-marker' ] && [ \"\$(sqlite3 -readonly '$AH_HOME/state/ah.db' 'pragma integrity_check')\" = ok ]" "restore brought the copy back: same ah.db, the marker gone" "restore rc=$o, sums or marker differ: round-trip.txt"
  start_daemon
}

# gone <seconds> <pid> <stamp>: the process ended.
gone() { poll "$1" "[ \"\$(ah_pid_stamp '$2')\" != '$3' ]"; }

# verb_exits <row.name> <verb>: `ahd <verb>` from this shell; the daemon must log it, exit and remove its socket.
verb_exits() {
  local name=$1 verb=$2 mark p st rc f="$T/cap/$2.out"
  daemon_alive || start_daemon
  mark=$(log_mark); p=$(daemon_pid); st=$(ah_pid_stamp "$p")
  "$AHD" "$verb" >"$f" 2>&1; rc=$?
  { echo "rc=$rc"; scrub <"$f"; echo '--- log'; log_since "$mark" | grep -E 'drain|stop' | scrub | cut -c1-200; } | ev B08 "$verb.txt"
  if gate_refused <"$f" || log_since "$mark" | grep -q 'drain refused'; then
    check "$name" NOT-TESTED "the daemon refused this shell: run from your own terminal"
    return
  fi
  if gone 30 "$p" "$st" && [ ! -S "$AH_HOME/run/ahd.sock" ] && log_since "$mark" | grep -q 'drain requested'; then
    check "$name" PASS "ahd $verb: logged, process gone within 30 s, socket removed"
  else
    check "$name" FAIL "ahd $verb: rc=$rc; process, socket or log wrong: $verb.txt"
  fi
}

row_b08() {
  local mark p st o rc
  # Idle exit: the copy has no registration, and no login.json stay flag.
  stop_own
  mark=$(log_mark)
  env AHD_IDLE_EXIT_MS=3000 "$AHD" start >"$T/cap/idle-start.txt" 2>&1
  p=$(daemon_pid); st=$(ah_pid_stamp "$p")
  if [ -z "$st" ]; then
    check B08.idle-exit FAIL "no daemon came up with AHD_IDLE_EXIT_MS=3000"
  else
    gone 40 "$p" "$st"; rc=$?
    log_since "$mark" | scrub | cut -c1-200 | ev B08 idle-log.txt
    judge B08.idle-exit "[ '$rc' = 0 ] && log_since '$mark' | grep -q 'idle with no live registration'" "AHD_IDLE_EXIT_MS=3000: exit logged, process gone" "the daemon pid $p did not exit idle within 40 s, or logged nothing: idle-log.txt"
    judge B08.idle-socket "[ '$rc' = 0 ] && [ ! -S '$AH_HOME/run/ahd.sock' ]" "socket removed on idle exit" "socket still there after idle exit"
  fi
  stop_own

  verb_exits B08.drain drain
  verb_exits B08.stop stop

  # restart: a new process on the socket, and the old one gone.
  daemon_alive || start_daemon
  mark=$(log_mark); p=$(daemon_pid); st=$(ah_pid_stamp "$p")
  # To a file, as start_daemon does: a daemon spawned here could hold a $(...) pipe open.
  "$AHD" restart >"$T/cap/restart.out" 2>&1; rc=$?
  o=$(cat "$T/cap/restart.out")
  { echo "rc=$rc"; printf '%s\n' "$o" | scrub; echo '--- log'; log_since "$mark" | grep -E 'drain|listening' | scrub | cut -c1-200; } | ev B08 restart.txt
  if printf '%s' "$o" | gate_refused; then
    check B08.restart NOT-TESTED "the daemon refused this shell: run from your own terminal"
  else
    judge B08.restart "gone 30 '$p' '$st' && poll 20 \"[ -S '$AH_HOME/run/ahd.sock' ]\" && [ -n \"\$(ah_pid_stamp \"\$(daemon_pid)\")\" ] && [ \"\$(daemon_pid)\" != '$p' ]" "ahd restart: old process gone, a new one on the socket" "ahd restart: rc=$rc, restart.txt"
  fi

  # An older client beside a newer daemon uses it as it is. `ahd ui` is the ahd verb that goes through the client's version comparison;
  # `ahd start` does not, so it could not see this. Its lease or its gate refusal shows it reached the daemon. A drain refusal would also
  # leave the pid alone, so the log must show no drain attempt.
  start_daemon
  mark=$(log_mark); p=$(daemon_pid); st=$(ah_pid_stamp "$p")
  AHD_VERSION_OVERRIDE=0.0.1 perl -e 'alarm 8; exec @ARGV' "$AHD" ui --print --no-open >"$T/cap/old-client.out" 2>&1
  o=$(cat "$T/cap/old-client.out")
  { printf '%s\n' "$o" | scrub; echo '--- log'; log_since "$mark" | scrub | cut -c1-200; } | ev B08 old-client.txt
  judge B08.old-client "[ \"\$(ah_pid_stamp '$p')\" = '$st' ] && ! log_since '$mark' | grep -q 'drain' && printf '%s' \"\$o\" | grep -qE '127[.]0[.]0[.]1:[0-9]+/ui/|opened only by the user'" "AHD_VERSION_OVERRIDE=0.0.1 ahd ui: reached the same daemon, no drain asked" "the older client changed the daemon, asked for a drain, or never reached it: old-client.txt"

  # The control: the same comparison the other way round does drain, so the check above can tell.
  restart_daemon AHD_VERSION_OVERRIDE=0.0.1
  mark=$(log_mark); p=$(daemon_pid); st=$(ah_pid_stamp "$p")
  perl -e 'alarm 8; exec @ARGV' "$AHD" ui --print --no-open >"$T/cap/newer-client.out" 2>&1
  o=$(cat "$T/cap/newer-client.out")
  { printf '%s\n' "$o" | scrub; echo '--- log'; log_since "$mark" | scrub | cut -c1-200; } | ev B08 newer-client.txt
  if log_since "$mark" | grep -q 'drain refused' || printf '%s' "$o" | gate_refused; then
    check B08.newer-client NOT-TESTED "the older daemon refused this shell's drain: run from your own terminal"
  else
    judge B08.newer-client "gone 30 '$p' '$st' && log_since '$mark' | grep -q 'drain requested'" "the control: a newer client drained the older daemon" "the newer client left the older daemon up: newer-client.txt"
  fi
  stop_own
}

# fixture_team: a team in the copy with no session behind it: a team file and a registration naming a `sleep` of ours as the orchestrator.
# Same shape as tests/test-ahd-team-hold.sh. Each file is under $T/ws, so the test daemon's root check holds.
FIXTURE_TEAM=auditbox-user
fixture_team() {
  local members=${1:-}
  HIER="$T/ws/.claude/ah2"; TF="$HIER/teams/$FIXTURE_TEAM.json"
  mkdir -p "$HIER/teams" "$T/ws/box"
  if [ -z "${HOLDER:-}" ]; then
    sleep 1200 &
    HOLDER=$!; HOLDER_STAMP=$(ah_pid_stamp "$HOLDER"); probe_record_pid "$HOLDER"
  fi
  printf '{"version":1,"team_id":"t-%s","orchestrator":{"pid":%s,"session_id":"orch"},"members":[%s]}\n' "$FIXTURE_TEAM" "$HOLDER" "$members" >"$TF"
  mkdir -p "$AH_HOME/registrations"
  printf '{"v":1,"session_id":"orch","address":"orchestrator@%s","addresses":["orchestrator@%s"],"boxes":{"orchestrator@%s":"%s"},"team_file":"%s","team_files":["%s"],"harness":"stub","pid":%s,"pid_start":"%s"}\n' \
    "$FIXTURE_TEAM" "$FIXTURE_TEAM" "$FIXTURE_TEAM" "$T/ws/box" "$TF" "$TF" "$HOLDER" "$HOLDER_STAMP" >"$AH_HOME/registrations/orch.json"
}
fixture_gone() {
  [ -n "${HOLDER:-}" ] && stop_job "$HOLDER" "$HOLDER_STAMP"
  HOLDER=
  rm -f "$AH_HOME/registrations/orch.json"
}

# tui_once [file]: one frame, never a hang. rc is the command's; perl's alarm kills a hang (rc 142).
tui_once() { COLUMNS=160 perl -e 'alarm 20; exec @ARGV' "$AHD" tui --once 2>&1; }

row_d09() {
  local o rc pane before after row path
  fixture_team
  restart_daemon
  o=$(tui_once); rc=$?
  printf '%s\nrc=%s\n' "$o" "$rc" | scrub | ev D09 once.txt
  judge D09.once "[ '$rc' = 0 ]" "ahd tui --once: exit 0, one frame" "ahd tui --once: rc=$rc: once.txt"
  row="$FIXTURE_TEAM"
  judge D09.team-visible "printf '%s' \"\$o\" | grep -qE '$row'" "the sandbox team is in the frame" "the sandbox team is not in the frame: once.txt"
  o=$(COLUMNS=160 perl -e 'alarm 20; exec @ARGV' "$AHD" tui --once --team "$FIXTURE_TEAM" 2>&1)
  printf '%s\n' "$o" | scrub | ev D09 once-team.txt
  judge D09.team-name "printf '%s' \"\$o\" | grep -q 'AGENTS'" "--team opens the agents of $FIXTURE_TEAM" "the team's screen did not show: once-team.txt"

  # A live view in a private tmux pane: no window. The event is a member joining the team file; the new member's row must appear, not only the frame move (a clock would).
  launch tui "$T/ws" "'$AHD' tui --team '$FIXTURE_TEAM'"
  pane=$PANE
  poll 20 "tm capture-pane -p -t '$pane' | grep -qE '$row'"
  before=$(tm capture-pane -p -t "$pane")
  printf '%s\n' "$before" | scrub | ev D09 view-before.txt
  if ! printf '%s' "$before" | grep -qE "$row"; then
    check D09.live FAIL "the live view never showed the team in 20 s: view-before.txt"
  else
    fixture_team '{"role":"implementor","name":"auditmember","session_id":"s-m1","transport_id":"p-m1"}'
    poll 45 "tm capture-pane -p -t '$pane' | grep -q 'auditmember'"
    rc=$?
    after=$(tm capture-pane -p -t "$pane")
    printf '%s\n' "$after" | scrub | ev D09 view-after.txt
    judge D09.live "[ '$rc' = 0 ]" "the running view drew the new member within 45 s" "stale: the running view did not draw the new member in 45 s: view-before.txt, view-after.txt"
  fi
  tm kill-pane -t "$pane" >/dev/null 2>&1

  # The default interactive view. The fixture has no session behind it, so a blank here says nothing about a real team: not a FAIL.
  launch tui-default "$T/ws" "'$AHD' tui"
  pane=$PANE
  if poll 15 "tm capture-pane -p -t '$pane' | grep -q '$FIXTURE_TEAM'"; then
    check D09.default-view PASS "the default view names $FIXTURE_TEAM"
  else
    check D09.default-view NOT-TESTED "the default view did not show the session-less fixture team in 15 s; a real team needs a real session: view-default.txt"
  fi
  tm capture-pane -p -t "$pane" | scrub | ev D09 view-default.txt
  tm kill-pane -t "$pane" >/dev/null 2>&1

  # The view verb opens a herdr pane, and herdr is forbidden here: outside one it must decline, not hang. HERDR_PANE_ID is unset by the audit lib.
  o=$(perl -e 'alarm 20; exec @ARGV' "$AH" view open --kind tui --session orch 2>&1); rc=$?
  printf '%s\nrc=%s\n' "$o" "$rc" | scrub | ev D09 view-open.txt
  judge D09.view-open-declines "[ '$rc' = 3 ]" "ah view open --kind tui outside a herdr pane: exit 3" "ah view open --kind tui: rc=$rc: view-open.txt"
  check D09.view-open-pane NOT-TESTED "needs a real herdr pane and the sandbox keeps herdr out; the live view above ran ahd tui in a private tmux pane"

  fixture_gone
  stop_own
  o=$(tui_once); rc=$?
  printf '%s\nrc=%s\n' "$o" "$rc" | scrub | ev D09 daemon-down.txt
  judge D09.daemon-down "[ '$rc' = 3 ] && printf '%s' \"\$o\" | grep -q 'ahd is not running'" "daemon down: exit 3, says so" "daemon down: rc=$rc: daemon-down.txt"
}

# ---- run
user_rows_exit() {
  local rc=$? p s left i
  trap - EXIT
  trap '' INT TERM HUP
  case "${T:-}" in /private/tmp/ahprobe.?*) ;; *) exit "$rc" ;; esac
  stop_own
  tm kill-server >/dev/null 2>&1
  for i in "${!PIDS[@]}"; do ah_signal_stamped TERM "${PIDS[$i]}" "${PIDSTAMPS[$i]:-}"; done
  # Everything else this run started names the probe root on its command line. Each signal goes to the process just stamped.
  for s in TERM KILL; do
    for p in $(pgrep -f "$T"); do
      [ "$p" = "$$" ] && continue
      ah_signal_stamped "$s" "$p" "$(ah_pid_stamp "$p")"
    done
    sleep 1
  done
  for i in "${!PIDS[@]}"; do ah_signal_stamped KILL "${PIDS[$i]}" "${PIDSTAMPS[$i]:-}"; done
  left=$(pgrep -f "$T" | grep -vx "$$" | tr '\n' ' ')
  if [ -n "$RESULTS" ]; then
    [ "${CANARIES_DONE:-}" = 1 ] || canary_end
    judge run.no-strays "[ -z '$left' ]" "no process of this run is left" "still alive: $left"
    judge run.no-window "[ ! -s '$T/shim-called' ] && [ ! -s '$T/herdr-called' ]" "nothing reached claude, codex, open or herdr" "reached a refused tool: $(cat "$T/shim-called" "$T/herdr-called" 2>/dev/null | head -3 | tr '\n' ';')"
    summary
    if ! grep -q '^FAIL' "$RESULTS"; then
      rm -rf "${T:?}/home" "${T:?}/ah" "${T:?}/plugin" "${T:?}/ws" "${T:?}/cap" "${T:?}/tmux" "${T:?}/codex" "${T:?}/bin" "${T:?}/b05-out" "${T:?}/Users" "${T:?}/private" "${T:?}/tmp" "${T:?}/var"
    else
      rc=1
      echo "A check failed, so the copy is kept: $T/home"
    fi
  fi
  echo "Left running by this run: ${left:-none}"
  echo "Results: $RESULTS"
  echo "Evidence: $T/evidence"
  exit "$rc"
}

summary() {
  local r
  {
    echo
    echo "=== summary"
    for r in D07 D09 B05 B08 canary run; do
      grep -qE "^[A-Z-]+ +$r\." "$RESULTS" || continue
      printf '%-10s %-8s %s pass, %s fail, %s not tested\n' \
        "$(grep -E "^[A-Z-]+ +$r\." "$RESULTS" | cut -d' ' -f1 | worst)" "$r" \
        "$(grep -cE "^PASS +$r\." "$RESULTS")" "$(grep -cE "^FAIL +$r\." "$RESULTS")" "$(grep -cE "^NOT-TESTED +$r\." "$RESULTS")"
    done
  } | tee -a "$RESULTS"
}

# refuse <why>: out with nothing signalled. probe_init's EXIT trap must not run here: probe_cleanup TERMs whoever holds $AH_HOME/run/ahd.sock,
# which with AH_HOME on the real home is the real daemon. Only the empty probe root goes.
refuse() {
  trap - EXIT
  echo "user-rows: refusing: $1" >&2
  case "${T:-}" in /private/tmp/ahprobe.?*) rm -rf "$T" ;; esac
  exit 1
}

main() {
  local real
  if [ "${1:-}" = --self-test ]; then self_test; exit $?; fi
  self_test >/dev/null || { self_test | grep BAD; echo "user-rows: a predicate is broken, nothing was run" >&2; exit 1; }
  real=$(python3 -I -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$SUBJECT_LINK")
  [ -x "$real/bin/ahd" ] || { echo "user-rows: no plugin at $SUBJECT_LINK" >&2; exit 1; }
  [ -f "$REAL_AH/state/ah.db" ] || { echo "user-rows: no real database to copy" >&2; exit 1; }
  canary_start
  RESULTS=
  probe_init
  export AH_HOME="$HOME/.claude/ah2" AH_TEST=1 AH_TEST_REAL_HOME="$REAL_USER_HOME"
  local v
  for v in "$HOME" "$AH_HOME"; do
    if resolves_real "$v"; then refuse "$v is the real home"; fi
    case "$v" in "$T"/*) ;; *) refuse "$v is outside $T" ;; esac
  done
  trap user_rows_exit EXIT
  mkdir -p "$AH_HOME" "$T/evidence"
  RESULTS="$T/results.txt"
  export TMPDIR="$T/tmp"
  mkdir -p "$TMPDIR"
  perl -e '$p=getppid; $e=time+$ARGV[0]; while(time<$e){sleep 5; exit 0 if getppid!=$p} kill "TERM",$p' "$USER_ROWS_BUDGET_S" &
  probe_record_pid $!
  write_refusing_shims
  echo "user-rows: copying the real home to $AH_HOME"
  copy_home || { check run.copy FAIL "the copy failed"; exit 1; }
  cp -Rp "$real" "$T/plugin"
  export AH_PLUGIN_ROOT="$T/plugin" AH_BIN="$AH_HOME/bin/ahd"
  PROBE_PLUGIN="$T/plugin"; AH="$T/plugin/bin/ah"; AHD="$T/plugin/bin/ahd"
  { "$AH" --version 2>&1; git -C "$USER_ROWS_DIR" rev-parse HEAD; date -u +%FT%TZ; } | scrub | ev run subject.txt
  cd "$T/ws" || exit 1
  start_daemon || { check run.daemon FAIL "the sandbox daemon did not start: $(scrub <"$T/cap/ahd-start.txt" | cut -c1-200)"; exit 1; }
  check run.daemon PASS "sandbox daemon pid $(daemon_pid), AH_HOME inside the probe root"
  want D07 && row_d07
  want D09 && row_d09
  want B05 && row_b05
  want B08 && row_b08
  # What the sandbox daemon holds open, from the run's own last daemon if one is up.
  daemon_alive || start_daemon
  if daemon_alive; then
    v=$(lsof -p "$(daemon_pid)" -Fn 2>/dev/null | grep -cF "n$REAL_USER_HOME/")
    judge canary.daemon-files "[ '$v' = 0 ]" "the sandbox daemon holds no file in the real home" "the sandbox daemon holds $v files in the real home"
  else
    check canary.daemon-files NOT-TESTED "no sandbox daemon up to look at"
  fi
  stop_own
  canary_end
  CANARIES_DONE=1
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then main "$@"; fi
