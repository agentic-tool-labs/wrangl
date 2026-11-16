# agent-hierarchy: which state home the shims and install.sh may use, and where the native binary may live. POSIX sh.
# Sourced after bin/ah-testmode.sh, whose ah_test_mode, ah_resolve_path and ah_passwd_home it builds on. It is the sh twin of the
# native state-home verdict (Ah.Platform AhHomeCheck over the Ah.Config trust store); the trusted_homes rows of
# tests/contracts/home-predicate.json run against both.
#
#   ah_home_verdict PASSWD_HOME
#       Reads AH_TEST and AH_HOME. Prints the home to use and returns 0, or prints the reason and returns 1. Test mode accepts the
#       AH_HOME it is given. Otherwise the home must be absolute and resolve to <PASSWD_HOME>/.claude/ah2, or to a
#       directory listed in <that default home>/trusted-homes. An empty AH_HOME means the default home. A home at or under ah
#       v1's (<PASSWD_HOME>/.claude/agent-hierarchy) is refused before the list is read, whatever the list says.
#   ah_find_binary PASSWD_HOME CALLER_PATH SKIP_DIR [VERDICT_HOME]
#       Reads AH_TEST, AH_HOME and AH_BIN. Prints the native binary to exec and returns 0, or prints nothing and returns 1.
#       Outside test mode only <home>/bin/ahd of an accepted home counts (a refused AH_HOME falls back to the default home's
#       binary, which refuses that AH_HOME itself). In test mode: AH_BIN, then <AH_HOME>/bin/ahd, then ahd on CALLER_PATH outside
#       SKIP_DIR. The binary must be a regular executable file owned by this user that group and other cannot write.
#       VERDICT_HOME is the home a caller already got from ah_home_verdict (the default home if refused), so it is not worked out twice.
#
#   ah_route PASSWD_HOME CALLER_PATH SKIP_DIR [VERDICT_STATUS VERDICT_TEXT]
#       The verdict first (the status and words of an ah_home_verdict the caller already ran, else run here), then the route. Prints one of: "bin <path>" (exec the native binary), "none" (an accepted home with no
#       binary to run), or "refuse <reason>" (a refused home with no binary to refuse it itself). A refused home is answered by the
#       default home's binary when there is one, in its own words. Returns 0 for bin and none, 1 for refuse.
#   ah_verb_free ARGS...
#       Whether the first verb of a `ahd` command line is one the binary runs whatever the state home is (the same list as CliApp:
#       where, check, home, hook, help, --help, -h, --version, and `login` other than `login run`; none at all), so a user can recover.
#
#   ah_hook_refusal PASSWD_HOME NAME MESSAGE
#       What a hook does when no binary can answer for it, the binary's own rule: sh cannot identify a session, and a deny needs an
#       identified one, so every hook, gate or not, prints nothing and appends one hook-errors row. Always returns 0.
#
# The passwd home is a parameter, so a test hands in a sandbox and production hands in ah_passwd_home. HOME is never read here.

# A directory owned by this user that group and other cannot write (symlinks are followed).
ah_owner_only_dir() {
  [ -d "$1" ] || return 1
  [ -n "$(find -L "$1" -prune -user "$(id -u)" ! -perm -020 ! -perm -002 -print 2>/dev/null)" ]
}

# Whether the resolved path $2 is a line of <default home>/trusted-homes under passwd home $1. The file counts only when it is a
# regular file reached without a link, owned by this user, not writable by group or other, at most 64 KiB, in a default home this
# user owns that group and other cannot write. An entry counts only when its real path is a directory owned by this user that group
# and other cannot write. A blank or non-absolute line is skipped.
ah_listed_home() {
  _ath_dh="$1/.claude/ah2"
  _ath_file="$_ath_dh/trusted-homes"
  # find does not follow the link here, so -type f also refuses a symbolic link in place of the file.
  [ -n "$(find "$_ath_file" -prune -type f -user "$(id -u)" ! -perm -020 ! -perm -002 -print 2>/dev/null)" ] || return 1
  ah_owner_only_dir "$_ath_dh" || return 1
  _ath_size=$(wc -c 2>/dev/null <"$_ath_file" | tr -d ' \t') || return 1
  [ -n "$_ath_size" ] && [ "$_ath_size" -le 65536 ] || return 1
  # A line is trimmed of spaces, tabs and carriage returns, the characters the binary trims; nothing else is whitespace here.
  _ath_cr=$(printf '\r')
  while IFS= read -r _ath_line || [ -n "$_ath_line" ]; do
    _ath_line=${_ath_line#"${_ath_line%%[! 	$_ath_cr]*}"}
    _ath_line=${_ath_line%"${_ath_line##*[! 	$_ath_cr]}"}
    case $_ath_line in /*) ;; *) continue ;; esac
    [ -d "$_ath_line" ] || continue
    _ath_entry=$(cd -P "$_ath_line" 2>/dev/null && pwd -P) || continue
    ah_owner_only_dir "$_ath_entry" || continue
    [ "$_ath_entry" = "$2" ] && return 0
  done <"$_ath_file"
  return 1
}

ah_home_verdict() {
  _ath_h=${AH_HOME-}
  _ath_h=${_ath_h#"${_ath_h%%[! 	]*}"}
  _ath_h=${_ath_h%"${_ath_h##*[! 	]}"}
  if ah_test_mode; then
    printf '%s' "$_ath_h"
    return 0
  fi
  case $1 in
    /*) ;;
    *) printf '%s' "the passwd database has no home directory for this user"; return 1 ;;
  esac
  _ath_def=$(ah_resolve_path "${1%/}/.claude/ah2") || {
    printf '%s' "the default state home cannot be resolved"
    return 1
  }
  if [ -z "$_ath_h" ]; then
    printf '%s' "$_ath_def"
    return 0
  fi
  case $_ath_h in
    /*) ;;
    *) printf '%s' "AH_HOME is not an absolute path"; return 1 ;;
  esac
  _ath_res=$(ah_resolve_path "$_ath_h") || {
    printf '%s' "AH_HOME cannot be resolved"
    return 1
  }
  # Before the trust list: a hand-edited list must not admit a path in ah v1's tree (segment test on real paths).
  _ath_v1=$(ah_resolve_path "${1%/}/.claude/agent-hierarchy") || {
    printf '%s' "ah v1's home path (~/.claude/agent-hierarchy) cannot be resolved (a dangling link?)"
    return 1
  }
  case $_ath_res in
    "$_ath_v1" | "$_ath_v1"/*)
      printf '%s' "AH_HOME is ah v1's home; Wrangl uses its own: unset AH_HOME for Wrangl"
      return 1
      ;;
  esac
  if [ "$_ath_res" = "$_ath_def" ] || ah_listed_home "${1%/}" "$_ath_res"; then
    printf '%s' "$_ath_res"
    return 0
  fi
  printf '%s' "AH_HOME is not the default home and is not trusted"
  return 1
}

# A regular executable file, owned by this user, that group and other cannot write (symlinks are followed).
ah_trusted_binary() {
  [ -f "$1" ] && [ -x "$1" ] || return 1
  [ -n "$_ah_uid" ] || _ah_uid=$(id -u)
  # An empty uid makes find fail, so nothing is printed and the binary is not trusted.
  [ -n "$(find -L "$1" -prune -user "$_ah_uid" ! -perm -020 ! -perm -002 -print 2>/dev/null)" ]
}

# Whether $1, followed through its symbolic links, is a file in the shim directory $2 (the shims, bin/ah, bin/ahd and bin/ah-hook,
# live there). A shim is never the native binary: exec'ing one from another shim loops. An empty $2 matches nothing.
ah_is_shim() {
  [ -n "$2" ] || return 1
  _ath_p=$1
  for _ath_hop in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    [ -L "$_ath_p" ] || break
    _ath_l=$(readlink "$_ath_p") || break
    case $_ath_l in /*) _ath_p=$_ath_l ;; *) _ath_p=${_ath_p%/*}/$_ath_l ;; esac
  done
  _ath_pd=$(cd -P "${_ath_p%/*}" 2>/dev/null && pwd -P) || return 1
  [ "$_ath_pd" = "$2" ]
}

ah_find_binary() {
  if ah_test_mode; then
    _ath_th=${AH_HOME-}
    _ath_th=${_ath_th#"${_ath_th%%[! 	]*}"}
    _ath_th=${_ath_th%"${_ath_th##*[! 	]}"}
    if [ -n "${AH_BIN:-}" ] && [ -f "$AH_BIN" ] && [ -x "$AH_BIN" ] && ! ah_is_shim "$AH_BIN" "$3"; then
      printf '%s' "$AH_BIN"
      return 0
    fi
    if [ -f "$_ath_th/bin/ahd" ] && [ -x "$_ath_th/bin/ahd" ] && ! ah_is_shim "$_ath_th/bin/ahd" "$3"; then
      printf '%s' "$_ath_th/bin/ahd"
      return 0
    fi
    _ath_ifs=$IFS
    case $- in *f*) _ath_noglob=1 ;; *) _ath_noglob= ;; esac
    set -f
    IFS=:
    for _ath_pdir in $2; do
      IFS=$_ath_ifs
      [ -n "$_ath_pdir" ] || continue
      _ath_found=$_ath_pdir/ahd
      [ -f "$_ath_found" ] && [ -x "$_ath_found" ] || continue
      _ath_fdir=$(cd "$_ath_pdir" 2>/dev/null && pwd -P) || _ath_fdir=
      if [ -n "$3" ] && [ "$_ath_fdir" = "$3" ]; then continue; fi
      ah_is_shim "$_ath_found" "$3" && continue
      [ -n "$_ath_noglob" ] || set +f
      printf '%s' "$_ath_found"
      return 0
    done
    IFS=$_ath_ifs
    [ -n "$_ath_noglob" ] || set +f
    return 1
  fi
  case $1 in
    /*) ;;
    *) return 1 ;;
  esac
  _ath_home=${4:-}
  [ -n "$_ath_home" ] || _ath_home=$(ah_home_verdict "$1") || _ath_home="${1%/}/.claude/ah2"
  if ah_trusted_binary "$_ath_home/bin/ahd" && ! ah_is_shim "$_ath_home/bin/ahd" "$3"; then
    printf '%s' "$_ath_home/bin/ahd"
    return 0
  fi
  return 1
}

ah_route() {
  if [ "$#" -ge 5 ]; then
    _ath_reason=$5
    _ath_rc=$4
  else
    _ath_reason=$(ah_home_verdict "$1")
    _ath_rc=$?
  fi
  if [ "$_ath_rc" = 0 ]; then
    if _ath_bin=$(ah_find_binary "$1" "$2" "$3" "$_ath_reason"); then
      printf 'bin %s' "$_ath_bin"
      return 0
    fi
    printf 'none'
    return 0
  fi
  if _ath_bin=$(ah_find_binary "$1" "$2" "$3" "${1%/}/.claude/ah2"); then
    printf 'bin %s' "$_ath_bin"
    return 0
  fi
  printf 'refuse %s' "$_ath_reason"
  return 1
}

ah_verb_free() {
  _ath_v=${1-}
  _ath_next=${2-}
  if [ "$_ath_v" = ah ] && [ "$#" -gt 1 ]; then
    _ath_v=$2
    _ath_next=${3-}
  fi
  case $_ath_v in
    '' | where | check | home | hook | --help | -h | help | --version) return 0 ;;
    login) [ "$_ath_next" != run ] && return 0 ;;
  esac
  return 1
}

# One row in <passwd home>/.claude/ah2/hierarchy/hook-errors.jsonl, the shape the binary writes; the file moves to .1 first when it is
# over 1 MiB. Never fails: a log that cannot be written has nowhere left to report to. The size check and the move are not atomic:
# two hooks crossing the limit together can replace a just-rotated .1, losing the older rows. That is accepted; `mv -n` would
# instead leave a stale .1 in place forever and stop the log from ever rotating again.
ah_log_hook_error() {
  case $1 in /*) ;; *) return 0 ;; esac
  _ath_ldir="${1%/}/.claude/ah2/hierarchy"
  mkdir -p "$_ath_ldir" 2>/dev/null || return 0
  _ath_log="$_ath_ldir/hook-errors.jsonl"
  _ath_lsize=$(wc -c 2>/dev/null <"$_ath_log" | tr -d ' \t')
  if [ -n "$_ath_lsize" ] && [ "$_ath_lsize" -gt 1048576 ]; then
    mv "$_ath_log" "$_ath_log.1" 2>/dev/null
  fi
  _ath_ts=$(date -u +%Y-%m-%dT%H:%M:%S.000Z)
  printf '{"ts":"%s","hook":"%s","event":null,"session_id":null,"message":"%s","stack":[]}\n' "$_ath_ts" "$2" "$3" >>"$_ath_log" 2>/dev/null
  return 0
}

ah_hook_refusal() {
  ah_log_hook_error "$1" "$2" "$3"
  return 0
}

