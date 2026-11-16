#!/bin/bash
# K5 scenario 5: the socket path limit. A path of 104 bytes is refused by the client (a warning, no daemon) and by the daemon
# started directly (it logs `socket path <path> is longer than 103 bytes; refusing to start` and exits 1).
# Negative: the same layout at 103 bytes starts.
. "$(dirname "$0")/_lib.sh"
lc_runtimes
lc_init

# AH_HOME under the sandbox, padded so that <AH_HOME>/run/ahd.sock has exactly the wanted length
pad_home() { # <total length of the socket path>
  local want=$1 base="$T/h" fixed=13 # "/run/ahd.sock" is 13 bytes
  local len=$(( want - fixed - ${#base} - 1 ))
  [ "$len" -ge 1 ] || { echo "sandbox path too long for this scenario"; exit 2; }
  printf '%s/%s' "$base" "$(printf 'p%.0s' $(seq 1 "$len"))"
}

H104=$(pad_home 104)
export AH_HOME="$H104"
mkdir -p "$AH_HOME"
lc_check "the socket path is exactly 104 bytes" '[ "$(printf %s "$AH_HOME/run/ahd.sock" | wc -c | tr -d " ")" = 104 ]'
lc_ctl start >"$T/c104.json" 2>"$T/c104.err"; RC=$?
lc_check "the client refuses: a warning naming the length and the limit, and no daemon" 'grep -q "socket path is 104 bytes; the limit is 103" "$T/c104.err" "$T/c104.json" && [ "$(lc_daemons)" = 0 ]'
lc_direct_daemon >"$T/d104.out" 2>"$T/d104.err"; DRC=$?
lc_check "the daemon started directly refuses: exit 1 and the refusal text" '[ "$DRC" = 1 ] && cat "$T/d104.out" "$T/d104.err" "$AH_HOME/ahd.log" 2>/dev/null | grep -q "is longer than 103 bytes; refusing to start"'
lc_check "...nothing was created under run/" '[ ! -S "$AH_HOME/run/ahd.sock" ] && [ ! -f "$AH_HOME/run/ahd.json" ]'

H103=$(pad_home 103)
export AH_HOME="$H103"
mkdir -p "$AH_HOME"
lc_check "the nearest non-match: a 103-byte socket path" '[ "$(printf %s "$AH_HOME/run/ahd.sock" | wc -c | tr -d " ")" = 103 ]'
lc_ctl start >"$T/c103.json" 2>"$T/c103.err"
lc_check "...starts, and a daemon is up" 'grep -q "\"ok\":true" "$T/c103.json" && lc_alive "$(lc_info_pid)"'
lc_done
