# agent-hierarchy — signalling a process a test knows by number.
#
# A pid recorded earlier can belong to an unrelated process by the time it is signalled: a busy machine reuses pids within
# minutes, and the signal kills whatever holds the number, the user's own work included. Every signal a test sends to a number
# goes through one of these. Each checks that the process is still the one the test means and signals nothing (returns 1)
# when it is not. tests/test-pid-safety.sh fails a test that signals a number any other way.
#
#   ah_pid_named <pid> <needle>            0 when the process exists and its command line contains <needle>
#   ah_signal_ours <sig> <pid> <needle>    signals while the command line still contains <needle> (a sandbox path); status 2 and
#                                          nothing signalled when the binary under test's path (AH_BIN, AHD_NATIVE_BIN) contains it
#   ah_pid_stamp <pid>                     the process's start time (ps lstart), empty when it is gone
#   ah_signal_stamped <sig> <pid> <stamp>  signals while the start time is still <stamp>; for a process whose command line
#                                          cannot name the sandbox: record the stamp with the pid
#   ah_signal_child <sig> <pid>            signals while the process is a child of this shell (a job the test started)

ah_pid_named() {
  [ -n "$1" ] && [ -n "$2" ] || return 1
  case "$(ps -ww -p "$1" -o command= 2>/dev/null)" in
    *"$2"*) return 0 ;;
    *) return 1 ;;
  esac
}

# A needle that the binary under test's own path contains also names every process running that binary, other tests' included, so it
# is refused (status 2, nothing signalled): the gate publishes the binary into a sandbox of its own, and a needle taken from the
# command line of a run is that directory.
_ahp_needle_ok() {
  local _ahp_b
  for _ahp_b in "${AH_BIN:-}" "${AHD_NATIVE_BIN:-}"; do
    [ -n "$_ahp_b" ] || continue
    case "$_ahp_b" in *"$1"*) return 1 ;; esac
  done
  return 0
}

ah_signal_ours() {
  if ! _ahp_needle_ok "$3"; then
    echo "ah_signal_ours: refusing needle '$3': it is part of the binary under test's path and would name every process running it" >&2
    return 2
  fi
  ah_pid_named "$2" "$3" && kill "-$1" "$2" 2>/dev/null
}

ah_pid_stamp() {
  [ -n "$1" ] || return 0
  ps -ww -p "$1" -o lstart= 2>/dev/null | sed 's/^ *//; s/ *$//'
}

ah_signal_stamped() {
  [ -n "$2" ] && [ -n "$3" ] && [ "$(ah_pid_stamp "$2")" = "$3" ] && kill "-$1" "$2" 2>/dev/null
}

ah_signal_child() {
  local _ahp_pp
  [ -n "$2" ] || return 1
  _ahp_pp=$(ps -ww -p "$2" -o ppid= 2>/dev/null | tr -d ' ')
  { [ "$_ahp_pp" = "$$" ] || [ "$_ahp_pp" = "${BASHPID:-$$}" ]; } && kill "-$1" "$2" 2>/dev/null
}
