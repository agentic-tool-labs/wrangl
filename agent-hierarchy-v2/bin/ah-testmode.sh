#!/bin/sh
# agent-hierarchy: the sh twin of IAhEnvironment.TestMode, sourced by bin/ah-hook and scripts/install.sh. POSIX sh.
#
#   . "$root/bin/ah-testmode.sh"
#   ah_test_mode && echo "test mode"
#
# Test mode holds only when BOTH are true:
#   1. AH_TEST is exactly 1;
#   2. the real path of AH_HOME has an ancestor directory D (D itself counts) such that D's parent is /tmp or /private/tmp,
#      D's name is "ahprobe." followed by one or more letters and digits, and D is a directory owned by the invoking user and
#      not writable by group or other.
# The real path is taken on the longest existing prefix of AH_HOME with the remaining components literal; an empty or ".."
# component in the remainder, a relative AH_HOME, or a prefix that exists only as a dangling link means "not test mode".
# The first ancestor with such a parent and name decides. tests/contracts/home-predicate.json is the table both this
# function and the C# predicate (Ah.Platform) are run against.
#
# ah_passwd_home prints the home the password database gives the invoking user; in test mode $HOME when it is set, never otherwise.

# ah_resolve_path <absolute path>: prints the real path of the longest existing prefix with the remaining components taken
# literally. Returns 1 (and prints nothing) for a relative path, a remainder with an empty or ".." component, or a prefix that
# exists only as a dangling link. The one resolver the test-mode predicate and the trusted-home rules share.
ah_resolve_path() {
  case $1 in /*) ;; *) return 1 ;; esac
  _ahr_rest=
  _ahr_cur=$1
  while :; do
    if [ -e "$_ahr_cur" ]; then
      if [ -d "$_ahr_cur" ]; then
        _ahr_real=$(cd -P "$_ahr_cur" 2>/dev/null && pwd -P) || return 1
      else
        [ -L "$_ahr_cur" ] && return 1
        _ahr_dir=$(cd -P "$(dirname "$_ahr_cur")" 2>/dev/null && pwd -P) || return 1
        _ahr_real=${_ahr_dir%/}/$(basename "$_ahr_cur")
      fi
      break
    fi
    [ -L "$_ahr_cur" ] && return 1
    [ "$_ahr_cur" = / ] && return 1
    case $_ahr_cur in */*) ;; *) return 1 ;; esac
    _ahr_rest="/${_ahr_cur##*/}$_ahr_rest"
    _ahr_cur=${_ahr_cur%/*}
    [ -n "$_ahr_cur" ] || _ahr_cur=/
  done

  # remainder: no empty and no ".." component ("" appears as "//" or a trailing "/")
  case "$_ahr_rest" in
    *//* | */ | */../* | */..) return 1 ;;
  esac
  case "$_ahr_real" in
    /) _ahr_out=$_ahr_rest ;;
    *) _ahr_out=$_ahr_real$_ahr_rest ;;
  esac
  [ -n "$_ahr_out" ] || return 1
  printf '%s' "$_ahr_out"
}

ah_test_mode() {
  [ "${AH_TEST:-}" = 1 ] || return 1
  _ah_h=${AH_HOME-}
  _ah_h=${_ah_h#"${_ah_h%%[! 	]*}"}
  _ah_h=${_ah_h%"${_ah_h##*[! 	]}"}
  [ -n "$_ah_h" ] || return 1
  case $_ah_h in /*) ;; *) return 1 ;; esac

  _ah_resolved=$(ah_resolve_path "$_ah_h") || return 1

  _ah_d=$_ah_resolved
  while [ "$_ah_d" != / ] && [ -n "$_ah_d" ]; do
    _ah_parent=${_ah_d%/*}
    [ -n "$_ah_parent" ] || _ah_parent=/
    _ah_name=${_ah_d##*/}
    case "$_ah_parent" in
      /tmp | /private/tmp)
        case "$_ah_name" in
          ahprobe.)
            ;;
          ahprobe.*)
            _ah_tail=${_ah_name#ahprobe.}
            case "$_ah_tail" in
              '' | *[!A-Za-z0-9]*) ;;
              *)
                [ -d "$_ah_d" ] || return 1
                [ -n "$(find "$_ah_d" -prune -user "$(id -u)" ! -perm -020 ! -perm -002 -print 2>/dev/null)" ] || return 1
                return 0
                ;;
            esac
            ;;
        esac
        ;;
    esac
    _ah_d=$_ah_parent
  done
  return 1
}

# This user's uid and name, from one run of id. They start empty here, whatever the environment held, so the environment cannot set them.
_ah_uid=
_ah_user=
_ah_ph=
ah_load_ids() {
  _ah_uid=
  _ah_user=
  _ah_ids=$(id 2>/dev/null) || return 1
  case $_ah_ids in
    uid=[0-9]*'('*')'*)
      case $_ah_ids in
        *' euid='*)
          # real and effective differ: ask for the effective ones by name, as `id -u` and `id -un` answer
          _ah_uid=$(id -u 2>/dev/null) || return 1
          _ah_user=$(id -un 2>/dev/null) || return 1
          return 0
          ;;
      esac
      _ah_ids=${_ah_ids#uid=}
      _ah_uid=${_ah_ids%%'('*}
      _ah_ids=${_ah_ids#*'('}
      _ah_user=${_ah_ids%%')'*}
      ;;
    *) return 1 ;;
  esac
}

# Sets _ah_ph to the home the password database gives this user, and loads the ids, with no subshell for the caller to lose them in.
ah_load_passwd_home() {
  _ah_ph=
  ah_load_ids || return 1
  case $_ah_user in '' | *[!A-Za-z0-9._-]*) return 1 ;; esac
  # tilde expansion of ~name reads the password database, not $HOME
  eval "_ah_ph=~$_ah_user" 2>/dev/null || return 1
  # Test mode takes HOME when it is set (even empty), as the native IAhEnvironment.HomeDirectory does; outside it HOME is never read.
  if [ -n "${HOME+x}" ] && ah_test_mode; then _ah_ph=$HOME; fi
  case $_ah_ph in /*) ;; *) _ah_ph=; return 1 ;; esac
}

ah_passwd_home() {
  ah_load_passwd_home || return 1
  printf '%s\n' "$_ah_ph"
}
