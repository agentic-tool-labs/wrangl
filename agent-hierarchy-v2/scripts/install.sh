#!/bin/bash
# agent-hierarchy — builds the native ahd binary and its terminal view ahd-tui into <AH_HOME>/bin and links ahd onto PATH.
#
# Usage: scripts/install.sh [--apply] [--prefix DIR] [--no-path] [--login]
#                           [--auto-rebuild|--no-auto-rebuild] [--rebuild-only] [--quiet]
#                           [--allow-lifecycle|--no-allow-lifecycle] [--codex-trust]
#
#   (no --apply)      print each step's plan and change nothing
#   --apply           do it
#   --prefix DIR      where the ahd link goes (default ~/.local/bin)
#   --no-path         do not touch PATH (no PATH symlink, no rc-file edit)
#   --login           also run `ahd login on`
#   --auto-rebuild / --no-auto-rebuild
#                     whether a plugin update may rebuild the binary in the background at SessionStart.
#                     Neither flag: asked at a terminal (default yes); a non-interactive run takes yes.
#   --rebuild-only    build and place both binaries, update the stamp and the ah link; never touch the ahd link, PATH, rc files
#                     or the login item
#   --no-daemon-restart
#                     start nothing and leave no daemon running as a result: never restart a running daemon, whatever its
#                     version or the binary's change, and never start a stopped one (for CI and sandboxes); the caller
#                     restarts it later with `ahd restart`
#   --quiet           print nothing but errors
#   --allow-lifecycle     add allow rules for the Orchestrator's lifecycle commands, every ah command and SendMessage
#                         (`ah roster permissions rules` lists them) to your Claude Code user settings, without asking;
#                         those calls then skip the permission prompt and the auto-mode classifier
#   --no-allow-lifecycle  remove the rules ah added and remember "no"
#                         Both together is a usage error. Either, run from inside a Claude Code session, is refused
#                         before anything is built.
#                     Neither flag: at a terminal, on an install whose ah proves its caller by process ancestry and
#                     holds unreleased members, asked once ([y/N], default no); never asked with --quiet or without a
#                     terminal; a "yes" given earlier is refreshed for the new version.
#   --codex-trust     tell Codex to trust the git checkout this script runs in (`ah roster codex-trust --yes`), without
#                     asking. Run from inside a Claude Code session it is refused before anything is built.
#                     Without the flag: at a terminal, when `codex` is on PATH and the directory is a git checkout whose
#                     root Codex does not trust yet, asked once ([y/N], default no); never asked with --quiet or without
#                     a terminal.
#   --no-design-skills  do not install Anthropic's frontend-design skill (Apache-2.0), which is otherwise installed by
#                     default, at a terminal or not, into <CLAUDE_CONFIG_DIR or ~/.claude>/skills/frontend-design from a
#                     pinned commit with every file's sha256 checked first; also skips the third-party offer below.
#                     An installed copy is never deleted; a copy you edited or installed yourself is left alone. Inside an
#                     agent session the default install is skipped.
#   --with-ux-skills  install the third-party design sources for the ux-engineer role that are missing, without asking:
#                     the jakubkrehel and addyosmani plugins through the claude CLI, and manual steps for vercel-labs
#                     (ah never runs npx). Run from inside a Claude Code session it is refused.
#   --no-ux-skills    do not offer them. Both together is a usage error, as is --with-ux-skills with --no-design-skills.
#                     Neither flag: at a terminal, asked once ([Y/n], default yes) when a source is missing; a
#                     non-interactive run never installs one. A failed install never fails this script.
#
# Exit codes: 0 done (or planned); 1 a step failed; 2 refused by the home guard, or a usage error.
#
# Everything this writes is under <AH_HOME> (bin, build, install.json) or is the links <prefix>/ahd and <prefix>/ah, the PATH
# symlink of the PATH step, or the marked rc block. Nothing is computed from user input beyond --prefix.

set -u

script_dir="$(cd "$(dirname "$0")" && pwd)"
plugin_root="$(dirname "$script_dir")"
# shellcheck source=../bin/ah-testmode.sh
. "$plugin_root/bin/ah-testmode.sh" || { echo "error: cannot load $plugin_root/bin/ah-testmode.sh" >&2; exit 2; }
# shellcheck source=../bin/ah-trust.sh
. "$plugin_root/bin/ah-trust.sh" || { echo "error: cannot load $plugin_root/bin/ah-trust.sh" >&2; exit 2; }
# shellcheck source=lib-version.sh
. "$script_dir/lib-version.sh" || { echo "error: cannot load $script_dir/lib-version.sh" >&2; exit 2; }
# shellcheck source=lib-contract.sh
. "$script_dir/lib-contract.sh" || { echo "error: cannot load $script_dir/lib-contract.sh" >&2; exit 2; }
# shellcheck source=lib-progress.sh
. "$script_dir/lib-progress.sh" || { echo "error: cannot load $script_dir/lib-progress.sh" >&2; exit 2; }

apply=false
prefix="${HOME:-}/.local/bin"
no_path=false
login=false
auto_rebuild=""
rebuild_only=false
no_daemon_restart=false
quiet=false
lifecycle=""
lifecycle_conflict=false
codex_trust=false
with_ux_skills=false
no_ux_skills=false
no_design_skills=false

# The one first-party skill installed by default: anthropics/skills' frontend-design at this commit, a space-separated list of
# <file>:<sha256> for every file in its directory. Each file is downloaded and checked before anything is written. Bumping the pin
# replaces a copy this script wrote and nobody edited.
FRONTEND_DESIGN_COMMIT=dbd4588f9e1033efb41dad4bef2f7947c8993d44
FRONTEND_DESIGN_FILES="SKILL.md:d91970639e9f5c37682ac7ab60094d35f1c7c1f38d731bd56396563aee10c1d3 LICENSE.txt:0d542e0c8804e39aa7f37eb00da5a762149dc682d7829451287e11b938e94594"

usage() {
    sed -n '2,54p' "$0" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
    case "$1" in
        --apply) apply=true; shift ;;
        --prefix)
            if [ $# -lt 2 ] || [ -z "$2" ]; then
                echo "error: --prefix requires a value" >&2
                exit 2
            fi
            prefix="$2"
            shift 2
            ;;
        --no-path) no_path=true; shift ;;
        --login) login=true; shift ;;
        --auto-rebuild) auto_rebuild=true; shift ;;
        --no-auto-rebuild) auto_rebuild=false; shift ;;
        --rebuild-only) rebuild_only=true; shift ;;
        --no-daemon-restart) no_daemon_restart=true; shift ;;
        --quiet) quiet=true; shift ;;
        --allow-lifecycle) [ "$lifecycle" = remove ] && lifecycle_conflict=true; lifecycle=add; shift ;;
        --no-allow-lifecycle) [ "$lifecycle" = add ] && lifecycle_conflict=true; lifecycle=remove; shift ;;
        --codex-trust) codex_trust=true; shift ;;
        --with-ux-skills) with_ux_skills=true; shift ;;
        --no-ux-skills) no_ux_skills=true; shift ;;
        --no-design-skills) no_design_skills=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *)
            echo "error: unrecognized argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if $with_ux_skills && { $no_ux_skills || $no_design_skills; }; then
    echo "error: --with-ux-skills contradicts --no-ux-skills and --no-design-skills; give one" >&2
    exit 2
fi
if $lifecycle_conflict; then
    echo "error: --allow-lifecycle and --no-allow-lifecycle contradict each other; give one" >&2
    exit 2
fi

# The environment legs of the verb's own session refusal (LifecyclePermissions.SessionRefusal); the verb stays authoritative
# after the build and also reads the process ancestry, which a shell cannot.
in_session=""
[ "${AH_SPAWN_DEPTH+x}" ] && in_session=AH_SPAWN_DEPTH
[ -z "$in_session" ] && [ "${CLAUDECODE+x}" ] && in_session=CLAUDECODE
[ -z "$in_session" ] && [ "${CLAUDE_CODE_SESSION_ID+x}" ] && in_session=CLAUDE_CODE_SESSION_ID
if [ -n "$in_session" ] && [ -n "$lifecycle" ] && ! $rebuild_only; then
    echo "error: --allow-lifecycle and --no-allow-lifecycle change your Claude Code settings, which an agent session may not do ($in_session is set); run install.sh from a terminal outside Claude Code, or drop the flag. Nothing was built or written." >&2
    exit 1
fi
if [ -n "$in_session" ] && $codex_trust && ! $rebuild_only; then
    echo "error: --codex-trust changes your Codex configuration, which an agent session may not do ($in_session is set); run install.sh from a terminal outside Claude Code, or drop the flag. Nothing was built or written." >&2
    exit 1
fi

if [ -n "$in_session" ] && $with_ux_skills && ! $rebuild_only; then
    echo "error: --with-ux-skills installs software on your machine, which an agent session may not do ($in_session is set); run install.sh from a terminal outside Claude Code" >&2
    exit 1
fi

if [ -z "${HOME:-}" ]; then
    echo "error: HOME is not set" >&2
    exit 2
fi

ah_home="${AH_HOME:-}"
ah_home="${ah_home#"${ah_home%%[![:space:]]*}"}"
ah_home="${ah_home%"${ah_home##*[![:space:]]}"}"
[ -n "$ah_home" ] || ah_home="$HOME/.claude/ah2"

target="$ah_home/bin/ahd"
link="$prefix/ahd"
stamp="$ah_home/install.json"
lock_file="$ah_home/bin/.rebuild.lock"
rebuild_lock_stale_ms=600000

# Styling: only a real terminal gets it, so a piped run is byte-stable.
if [ -t 1 ] && [ -n "${TERM:-}" ] && [ "${TERM:-}" != dumb ] && [ -z "${NO_COLOR:-}" ]; then
    T_BOLD=$'\033[1m'; T_CYAN=$'\033[36m'; T_RESET=$'\033[0m'
else
    T_BOLD=""; T_CYAN=""; T_RESET=""
fi

say() {
    $quiet || echo "$@"
}

would() {
    $quiet || echo "would: $*"
}

step() {
    $quiet && return 0
    echo
    echo "${T_CYAN}${T_BOLD}── $*${T_RESET}"
}

err() {
    echo "error: $*" >&2
}

interactive=false
if [ -t 0 ] && [ -r /dev/tty ]; then
    interactive=true
fi

# One [y/N] question, defaulting to no. Only a terminal is ever asked.
ask_no_default() {
    printf '%s [y/N] ' "$1"
    local reply=""
    read -r reply < /dev/tty || true
    case "$reply" in
        [yY]*) return 0 ;;
        *) return 1 ;;
    esac
}

# One [Y/n] question, asked at the step it concerns, defaulting to yes. Only a terminal is ever asked.
ask_step() {
    printf '%s [Y/n] ' "$1"
    local reply=""
    read -r reply < /dev/tty || true
    case "$reply" in
        [nN]*) return 1 ;;
        *) return 0 ;;
    esac
}

cleanup_dirs=()
lock_content=""
lock_held=false
rebuild_lock_guarded=false

cleanup() {
    local d
    for d in "${cleanup_dirs[@]+"${cleanup_dirs[@]}"}"; do
        case "$d" in
            "$ah_home"/build/stage.*) [ -d "$d" ] && rm -rf "$d" ;;
            /tmp/ahfd.*) [ -d "$d" ] && rm -rf "$d" ;;
        esac
    done
    if $lock_held && [ -f "$lock_file" ] && [ "$(cat "$lock_file" 2>/dev/null)" = "$lock_content" ]; then
        rm -f "$lock_file"
    fi
}
trap cleanup EXIT

detect_rid() {
    local os arch
    os=$(uname -s)
    arch=$(uname -m)

    case "$os" in
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                x86_64) echo "osx-x64" ;;
                *)
                    echo "error: unsupported Darwin architecture: $arch" >&2
                    exit 1
                    ;;
            esac
            ;;
        Linux)
            case "$arch" in
                x86_64) echo "linux-x64" ;;
                aarch64|arm64) echo "linux-arm64" ;;
                *)
                    echo "error: unsupported Linux architecture: $arch" >&2
                    exit 1
                    ;;
            esac
            ;;
        *)
            echo "error: unsupported OS: $os" >&2
            exit 1
            ;;
    esac
}

# AOT publish drives a platform linker that no SDK carries. Checked before the SDK is resolved so a machine
# that would fail a long download-and-build hears about the missing 30-second fix first.
toolchain_problem() {
    case "$(uname -s)" in
        Darwin)
            if ! xcode-select -p >/dev/null 2>&1 && ! command -v cc >/dev/null 2>&1; then
                echo "the Xcode command line tools are not installed; run: xcode-select --install"
            fi
            ;;
        Linux)
            local missing=""
            command -v clang >/dev/null 2>&1 || missing="clang"
            [ -f /usr/include/zlib.h ] || missing="$missing zlib-headers"
            if [ -n "$missing" ]; then
                local fix="install clang and the zlib development headers with your package manager"
                if command -v apt-get >/dev/null 2>&1; then
                    fix="run: sudo apt-get install clang zlib1g-dev"
                elif command -v dnf >/dev/null 2>&1; then
                    fix="run: sudo dnf install clang zlib-devel"
                elif command -v zypper >/dev/null 2>&1; then
                    fix="run: sudo zypper install clang zlib-devel"
                elif command -v pacman >/dev/null 2>&1; then
                    fix="run: sudo pacman -S clang zlib"
                fi
                echo "building needs$( for m in $missing; do printf ' %s' "$m"; done ) — $fix"
            fi
            ;;
    esac
}

# True when this dotnet can build this repo: matched against the SDK list, not `--version`.
has_net10_sdk() {
    "$1" --list-sdks 2>/dev/null | grep -q '^10\.'
}

# The SDK this dotnet uses, as its --list-sdks line ("10.0.105 [/path/sdk]"), or the command when that cannot be read.
sdk_in_use() {
    local v line=""
    v=$("$1" --version 2>/dev/null)
    [ -n "$v" ] && line=$("$1" --list-sdks 2>/dev/null | grep -F "$v [" | head -1)
    echo "${line:-$1}"
}

# True when restore log $1 has an advisory (NU1901-NU1904) on a pack the SDK pins: Microsoft.DotNet.ILCompiler (also as
# runtime.<rid>.Microsoft.DotNet.ILCompiler) or Microsoft.NETCore.App.*. The SDK is out of date.
sdk_advisory() {
    grep -E 'NU190[1-4]' "$1" 2>/dev/null | grep -Eq 'Microsoft\.DotNet\.ILCompiler|Microsoft\.NETCore\.App\.'
}

download_to() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$1" -o "$2"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$2" "$1"
    else
        err "neither curl nor wget is available to download $1"
        return 1
    fi
}

# Whether $1 is an entry of $PATH, a trailing slash on either side not counting.
path_entry_present() {
    local want="${1%/}" rest="$PATH:" entry
    while [ -n "$rest" ]; do
        entry="${rest%%:*}"
        rest="${rest#*:}"
        [ "${entry%/}" = "$want" ] && return 0
    done
    return 1
}

is_homebrew_prefix_dir() {
    local dir="$1" brew_prefix
    case "$dir" in
        /opt/homebrew|/opt/homebrew/*) return 0 ;;
        /usr/local/Homebrew|/usr/local/Homebrew/*) return 0 ;;
    esac
    if command -v brew >/dev/null 2>&1; then
        brew_prefix=$(brew --prefix 2>/dev/null || true)
        if [ -n "$brew_prefix" ]; then
            case "$dir" in
                "$brew_prefix"|"$brew_prefix"/*) return 0 ;;
            esac
        fi
    fi
    return 1
}

# Writes (or replaces) the delimited PATH block in rc_file. If the markers already exist, only the lines between
# them are replaced, so re-running the installer never appends a second block.
install_path_block() {
    local rc_file="$1" block="$2" tmp start_line rel_end_line end_line link_dir hops

    # Follow a symlinked rc file to the file it names, so a dotfile manager's link is not replaced by a file.
    hops=0
    while [ -L "$rc_file" ] && [ "$hops" -lt 16 ]; do
        link_dir=$(dirname "$rc_file")
        rc_file=$(readlink "$rc_file")
        case "$rc_file" in
            /*) ;;
            *) rc_file="$link_dir/$rc_file" ;;
        esac
        hops=$((hops + 1))
    done

    tmp="$rc_file.ah-tmp-$$"
    if [ -f "$rc_file" ]; then
        cp -p "$rc_file" "$tmp"
    else
        : > "$tmp"
    fi

    if [ -f "$rc_file" ] && grep -qxF '# >>> ah >>>' "$rc_file"; then
        start_line=$(grep -nxF '# >>> ah >>>' "$rc_file" | head -1 | cut -d: -f1)
        rel_end_line=$(tail -n "+$start_line" "$rc_file" | grep -nxF '# <<< ah <<<' | head -1 | cut -d: -f1)
        end_line=$((start_line + rel_end_line - 1))

        if [ "$start_line" -gt 1 ]; then
            head -n "$((start_line - 1))" "$rc_file" > "$tmp"
        else
            : > "$tmp"
        fi
        printf '%s\n' "$block" >> "$tmp"
        tail -n "+$((end_line + 1))" "$rc_file" >> "$tmp"
    elif [ -f "$rc_file" ]; then
        cat "$rc_file" > "$tmp"
        printf '\n' >> "$tmp"
        printf '%s\n' "$block" >> "$tmp"
    else
        printf '%s\n' "$block" >> "$tmp"
    fi

    mv "$tmp" "$rc_file"
}

json_str() {
    printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

# "null" or a quoted JSON string.
json_str_or_null() {
    if [ -z "$1" ]; then
        printf 'null'
    else
        printf '"%s"' "$(json_str "$1")"
    fi
}

# A top-level string field of the stamp, or empty.
stamp_field() {
    [ -f "$stamp" ] || return 0
    sed -n "s/.*\"$1\":\"\\([^\"]*\\)\".*/\\1/p" "$stamp" | head -1
}

stamp_raw_field() {
    [ -f "$stamp" ] || return 0
    sed -nE "s/.*\"$1\":(null|true|false|\"[^\"]*\"|\{[^}]*\}|[0-9]+).*/\1/p" "$stamp" | head -1
}

# The home the password database lists for this user.
passwd_home() {
    local user
    user=$(id -un 2>/dev/null) || return 1
    case "$user" in
        ""|*[!A-Za-z0-9._-]*) return 1 ;;
    esac
    local home
    home=$(eval "printf '%s' ~$user" 2>/dev/null) || return 1
    case "$home" in "~"*|"") return 1 ;; esac
    printf '%s' "$home"
}

physical_dir() {
    (cd "$1" 2>/dev/null && pwd -P)
}

# Test mode (bin/ah-testmode.sh) and dotnet, launchctl and systemctl all resolve into a directory in AH_TEST_FAKE_BIN.
fake_toolchain() {
    ah_test_mode || return 1
    local tool path dir listed ok d
    for tool in dotnet launchctl systemctl; do
        path=$(command -v "$tool" 2>/dev/null) || return 1
        dir=$(physical_dir "$(dirname "$path")") || return 1
        ok=false
        listed="${AH_TEST_FAKE_BIN:-}"
        local IFS=:
        for d in $listed; do
            [ -n "$d" ] || continue
            if [ "$(physical_dir "$d")" = "$dir" ]; then
                ok=true
                break
            fi
        done
        $ok || return 1
    done
    return 0
}

# 1. Guard: $HOME must be the passwd home of this user (S4).
guard_problem=""
real_home=$(passwd_home) || real_home=""
if [ -z "$real_home" ] || [ "$(physical_dir "$HOME")" != "$(physical_dir "$real_home")" ]; then
    if ! fake_toolchain; then
        guard_problem="\$HOME ($HOME) is not this user's passwd home (${real_home:-unknown})"
    fi
fi

step "Guard"
if [ -n "$guard_problem" ]; then
    if $apply; then
        err "refusing to install: $guard_problem"
        exit 2
    fi
    say "warning: a real run would be refused: $guard_problem"
else
    say "HOME is this user's home."
fi

# 1b. The shims run <home>/bin/ahd only for the default home, a home listed in <default home>/trusted-homes, or a test sandbox
# (bin/ah-trust.sh). This only reports; installing grants nothing and never changes the trust files.
step "State home"
home_problem=""
if [ -n "$real_home" ]; then
    home_problem=$(ah_home_verdict "$real_home") && home_problem=""
fi
if [ -z "$home_problem" ]; then
    say "AH_HOME is the default home, a trusted home, or a test sandbox: the shims will use $ah_home/bin/ahd."
else
    shown=$(printf '%s' "$ah_home" | LC_ALL=C tr -cd '[:print:]' | cut -c1-200)
    say "warning: the shims will not use $shown/bin/ahd: $home_problem."
    if [ -t 1 ]; then
        say "Trust it once, from this terminal:  ahd home trust $shown"
        say "or unset AH_HOME to use the default home."
    else
        say "Ask the user to trust this home from a terminal, or to unset AH_HOME."
    fi
fi

# Reads a JSON-ish scalar from a small file with sed; both manifests are flat objects of this plugin.
plugin_json="$plugin_root/.claude-plugin/plugin.json"
cutover_json="$plugin_root/native/cutover.json"
plugin_version=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$plugin_json" 2>/dev/null | head -1)
plugin_name=$(sed -n 's/.*"name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$plugin_json" 2>/dev/null | head -1)
contract=""
if [ -f "$cutover_json" ]; then
    contract=$(sed -n 's/.*"contract"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$cutover_json" | head -1)
fi
if [ -z "$plugin_version" ] || [ -z "$plugin_name" ]; then
    err "could not read the plugin's version and name from $plugin_json"
    exit 1
fi
if [ -z "$contract" ]; then
    say "note: $cutover_json has no contract; building with contract 0"
    contract=0
fi

# The auto_rebuild answer is needed for the stamp; a flag pins it, otherwise a terminal is asked.
resolve_auto_rebuild() {
    [ -n "$auto_rebuild" ] && return 0
    if $rebuild_only; then
        auto_rebuild=$(stamp_raw_field auto_rebuild)
        [ -n "$auto_rebuild" ] || auto_rebuild=true
    elif $interactive && $apply; then
        if ask_step "Let a plugin update rebuild ahd in the background at session start?"; then
            auto_rebuild=true
        else
            auto_rebuild=false
        fi
    else
        auto_rebuild=true
    fi
}

# Rebuild lock: a file {pid, at, nonce}, linked into place; stale after 10 minutes. Creating it and removing a stale one happen
# under flock(1) on <lock>.guard where flock(1) exists. Without it a stale lock whose pid is gone (or unreadable) is moved aside
# and compared with what was judged: removed when they match, put back (or dropped, if a new lock is already there) when another
# installer replaced it in between. A stale lock whose pid is alive is left alone and the run fails naming it (status 3).
# AH_INSTALL_FLOCK=off makes the script behave as if flock(1) were absent.
rebuild_lock_round() {
    local nonce at tmp seen seen_at now_ms
    nonce="$$-$RANDOM$RANDOM-$(date +%s)"
    at=$(( $(date +%s) * 1000 ))
    tmp="$lock_file.$$.$RANDOM"
    printf '{"pid":%s,"at":%s,"nonce":"%s"}' "$$" "$at" "$nonce" > "$tmp"
    if ln "$tmp" "$lock_file" 2>/dev/null; then
        rm -f "$tmp"
        return 0
    fi
    rm -f "$tmp"
    seen=$(cat "$lock_file" 2>/dev/null) || return 2
    seen_at=$(printf '%s' "$seen" | sed -n 's/.*"at":\([0-9]*\).*/\1/p')
    now_ms=$(( $(date +%s) * 1000 ))
    if [ $(( now_ms - ${seen_at:-0} )) -lt "$rebuild_lock_stale_ms" ]; then
        return 1
    fi
    if [ "$rebuild_lock_guarded" = true ]; then
        # inside the guard nothing else creates or removes the lock: the judged file is removed only if it is still there
        if [ "$(cat "$lock_file" 2>/dev/null)" = "$seen" ]; then
            rm -f "$lock_file"
        fi
        return 2
    fi
    seen_pid=$(printf '%s' "$seen" | sed -n 's/.*"pid":\([0-9][0-9]*\).*/\1/p')
    # alive means the process exists: kill -0 also fails for another user's process (EPERM), which ps -p still finds
    if [ -n "$seen_pid" ] && { kill -0 "$seen_pid" 2>/dev/null || ps -p "$seen_pid" >/dev/null 2>&1; }; then
        seen_age_min=$(( (now_ms - ${seen_at:-0}) / 60000 ))
        return 3
    fi
    # two installers can both judge this stale lock (no flock(1) to serialise them); the move aside and compare below makes
    # one lose cleanly: the loser treats the round as held, and no lock is deleted that its taker did not judge
    local aside="$lock_file.stale.$$.$RANDOM"
    mv "$lock_file" "$aside" 2>/dev/null || return 2
    if [ "$(cat "$aside" 2>/dev/null)" = "$seen" ]; then
        rm -f "$aside"
        say "removed the stale rebuild lock of pid ${seen_pid:-unknown} at $lock_file"
        return 2
    fi
    # another installer replaced the lock in between: its lock goes back unless a newer one is already in place
    ln "$aside" "$lock_file" 2>/dev/null
    rm -f "$aside"
    return 1
}

take_rebuild_lock() {
    local round rc
    rebuild_lock_guarded=false
    if [ "${AH_INSTALL_FLOCK:-}" != off ] && command -v flock >/dev/null 2>&1; then
        rebuild_lock_guarded=true
    fi
    for round in 1 2 3 4 5 6; do
        if $rebuild_lock_guarded; then
            ( flock -x -w 10 9 || exit 1; rebuild_lock_round ) 9>"$lock_file.guard"
            rc=$?
        else
            rebuild_lock_round
            rc=$?
        fi
        case "$rc" in
            0)
                lock_content=$(cat "$lock_file")
                lock_held=true
                return 0
                ;;
            1) return 1 ;;
            3) return 3 ;;
        esac
    done
    return 1
}

now_utc() {
    date -u +%Y-%m-%dT%H:%M:%SZ
}

# Writes install.json through tmp and rename. $1 = last_rebuild (raw JSON), $2 = link path or empty.
# built_at is kept when nothing else in the stamp changed, so a re-run leaves the stamp byte-identical.
write_stamp() {
    local last="$1" link_value="$2" contract_json old_built built candidate body tmp
    case "$contract" in
        ''|*[!0-9]*) contract_json="\"$(json_str "$contract")\"" ;;
        *) contract_json="$contract" ;;
    esac
    local design_json=""
    if [ -n "$fd_state" ]; then
        design_json=",\"design_skills\":{\"frontend_design\":\"$(json_str "$fd_state")\",\"frontend_design_sha256\":$(json_str_or_null "$fd_sha"),\"frontend_design_files\":$(json_str_or_null "$fd_files")}"
    fi
    local ux_json="" ux_parts=""
    [ -n "$ux_vercel" ] && ux_parts="\"vercel-labs\":\"$(json_str "$ux_vercel")\""
    [ -n "$ux_jakub" ] && ux_parts="${ux_parts:+$ux_parts,}\"jakubkrehel\":\"$(json_str "$ux_jakub")\""
    [ -n "$ux_addy" ] && ux_parts="${ux_parts:+$ux_parts,}\"addyosmani\":\"$(json_str "$ux_addy")\""
    [ -n "$ux_parts" ] && ux_json=",\"ux_skills\":{$ux_parts}"
    render() {
        printf '{"version":"%s","contract":%s,"binary":"%s","link":%s,"plugin_root":"%s","built_at":"%s","auto_rebuild":%s%s%s,"last_rebuild":%s}' \
            "$(json_str "$plugin_version")" "$contract_json" "$(json_str "$target")" \
            "$(json_str_or_null "$link_value")" "$(json_str "$plugin_root")" \
            "$1" "$auto_rebuild" "$design_json" "$ux_json" "$last"
    }
    built=$(now_utc)
    old_built=$(stamp_field built_at)
    if [ -n "$old_built" ]; then
        candidate=$(render "$old_built")
        if [ "$candidate" = "$(cat "$stamp" 2>/dev/null)" ]; then
            built="$old_built"
        fi
    fi
    body=$(render "$built")
    tmp="$stamp.tmp.$$"
    mkdir -p "$ah_home"
    printf '%s\n' "$body" > "$tmp" && mv -f "$tmp" "$stamp"
}

# Removes the staging directory this run made, and nothing else.
remove_stage() {
    case "$stage" in
        "$ah_home"/build/stage.?*) [ -d "$stage" ] && rm -rf "$stage" ;;
    esac
}

# Under --rebuild-only a failed step is recorded in the stamp, then the run fails.
fail_step() {
    err "$*"
    if $rebuild_only && $apply && [ -f "$stamp" ]; then
        # Only last_rebuild changes: the binary on disk is still the old one, so the rest of the stamp stays true.
        local failed tmp
        failed="{\"version\":\"$(json_str "$plugin_version")\",\"at\":\"$(now_utc)\",\"state\":\"failed\"}"
        tmp="$stamp.tmp.$$"
        sed -E "s/\"last_rebuild\":(null|\{[^}]*\})\}\$/\"last_rebuild\":$(printf '%s' "$failed" | sed 's/[\/&]/\\&/g')}/" "$stamp" > "$tmp" && mv -f "$tmp" "$stamp"
    fi
    exit 1
}

# --- 2. Toolchain ---
step "Toolchain"

rid=$(detect_rid) || exit 1
say "Detected runtime identifier: $rid"

problem=$(toolchain_problem)
if [ -n "$problem" ]; then
    if $apply; then
        fail_step "$problem"
    fi
    say "warning: $problem (a real run will stop here until it is fixed)"
fi

dotnet_cmd=""
bootstrap_sdk=false
sdk_dir="$ah_home/dotnet"

# Written at each private install (its mtime is when; its text the version, for the reader). A still-flagged private SDK
# installed less than sdk_stamp_days ago is not offered the install again: that update just ran and did not clear it.
sdk_stamp="$sdk_dir/ah-installed-version"
sdk_stamp_days=7

# True when the stamp exists, is readable, and was written less than sdk_stamp_days ago (-mmin: BSD and GNU find).
sdk_stamp_fresh() {
    [ -r "$sdk_stamp" ] && [ -n "$(find "$sdk_stamp" -mmin -$((sdk_stamp_days * 24 * 60)) 2>/dev/null)" ]
}

# The current .NET 10 SDK, installed privately into $sdk_dir (the channel install brings the current patch; no PATH change).
# Status 1 with the reason in $sdk_error on a failed download or install; the caller decides whether that ends the run.
install_private_sdk() {
    local rc=0
    sdk_error=""
    mkdir -p "$sdk_dir"
    install_script=$(mktemp)
    if ! download_to "https://dot.net/v1/dotnet-install.sh" "$install_script"; then
        rm -f "$install_script"
        sdk_error="could not download dotnet-install.sh"
        return 1
    fi
    bash "$install_script" --channel 10.0 --install-dir "$sdk_dir" --no-path || rc=$?
    rm -f "$install_script"
    if [ "$rc" -ne 0 ]; then
        sdk_error="the .NET SDK installer exited with status $rc"
        return 1
    fi
    if ! has_net10_sdk "$sdk_dir/dotnet"; then
        sdk_error="the SDK installer finished but $sdk_dir/dotnet reports no .NET 10 SDK"
        return 1
    fi
    "$sdk_dir/dotnet" --version > "$sdk_stamp" 2>/dev/null || true
}

# The private SDK first: a user who chose it is not asked about the system one again.
if [ -x "$sdk_dir/dotnet" ] && has_net10_sdk "$sdk_dir/dotnet"; then
    dotnet_cmd="$sdk_dir/dotnet"
    say "Using the .NET SDK previously installed into $sdk_dir"
elif command -v dotnet >/dev/null 2>&1 && has_net10_sdk dotnet; then
    dotnet_cmd=dotnet
else
    bootstrap_sdk=true
    dotnet_cmd="$sdk_dir/dotnet"
fi

if $bootstrap_sdk; then
    if ! $apply; then
        would "download dotnet-install.sh and install the .NET 10 SDK into $sdk_dir (private to that directory; no PATH changes; asks first at a terminal)"
    elif ! $interactive; then
        fail_step "no .NET 10 SDK found. Install one, or run this at a terminal to have it installed privately into $sdk_dir (about a few hundred MB)."
    elif ask_step "No .NET 10 SDK found. Install one privately into $sdk_dir?"; then
        install_private_sdk || fail_step "$sdk_error"
    else
        fail_step "a .NET 10 SDK is required"
    fi
fi

# --- 3. Build ---
step "Build"

stage="$ah_home/build/stage.$$"
if $apply; then
    mkdir -p "$ah_home/bin" || fail_step "cannot create $ah_home/bin"
    chmod 0755 "$ah_home/bin"
    take_rebuild_lock; lock_rc=$?
    if [ "$lock_rc" -eq 3 ]; then
        fail_step "rebuild lock at $lock_file is stale (${seen_age_min} min old) but its pid $seen_pid is still running; stop that process, or remove the file if it is not an install: rm $lock_file"
    elif [ "$lock_rc" -ne 0 ]; then
        say "rebuild already running"
        exit 0
    fi
    resolve_auto_rebuild
    mkdir -p "$stage" || fail_step "cannot create $stage"
    cleanup_dirs+=("$stage")
    say "Building ahd and ahd-tui $plugin_version (contract $contract) for $rid ..."
    # publish-native.sh also fails on a compiler log that says a verb will always throw, and serve-starts the staged ahd in a
    # throwaway home, so a binary whose daemon cannot start never reaches the move below.
    # Env rather than flags: no build server or node is left running after the build.
    publish_stage() { # publish_stage DIR: builds with $dotnet_cmd into DIR
        DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
            MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false \
            bash "$script_dir/publish-native.sh" --dotnet "$dotnet_cmd" --native "$plugin_root/native" --rid "$rid" --out "$1" \
            --version "$plugin_version" --contract "$contract" --name "$plugin_name" --artifacts "$1/artifacts" \
            --restore-log "$1/restore.log"
    }
    restage() { rm -rf "$stage" && mkdir -p "$stage" || fail_step "cannot create $stage"; }
    # An out-of-date SDK (an advisory on a package it supplies), or a failed build with the system SDK, is offered the
    # current SDK privately, once, at a terminal. The background rebuild never installs one. A build that succeeded is kept.
    can_ask=false
    $interactive && ! $rebuild_only && can_ask=true
    private_offer="Install the current .NET 10 SDK privately into $sdk_dir and build with that?"
    if ! publish_stage "$stage"; then
        if [ "$dotnet_cmd" = "$sdk_dir/dotnet" ]; then
            # A broken private SDK falls back to a working system one, once, with no question: nothing is installed.
            command -v dotnet >/dev/null 2>&1 && has_net10_sdk dotnet || fail_step "dotnet publish failed"
            say "The build failed with the private .NET SDK in $sdk_dir; building once more with the system dotnet."
            dotnet_cmd=dotnet
            restage
            publish_stage "$stage" || fail_step "dotnet publish failed with the private .NET SDK and with the system one. Delete the private SDK directory $sdk_dir (rm -rf $sdk_dir) and run this again."
        elif ! $can_ask; then
            at_terminal="Run this at a terminal"
            $interactive && $rebuild_only && at_terminal="Run bash $script_dir/install.sh --apply (without --rebuild-only) at a terminal"
            fail_step "dotnet publish failed with the .NET SDK $(sdk_in_use "$dotnet_cmd"). $at_terminal to have the current .NET 10 SDK installed privately into $sdk_dir and the build retried with it."
        elif ask_no_default "The build failed with the .NET SDK $(sdk_in_use "$dotnet_cmd"). $private_offer"; then
            install_private_sdk || fail_step "$sdk_error"
            dotnet_cmd="$sdk_dir/dotnet"
            restage
            publish_stage "$stage" || fail_step "dotnet publish failed"
        else
            fail_step "dotnet publish failed"
        fi
    elif sdk_advisory "$stage/restore.log"; then
        sdk_note="The .NET SDK in use, $(sdk_in_use "$dotnet_cmd"), is out of date (a package it supplies has a published advisory); the build still works."
        if [ "$dotnet_cmd" = "$sdk_dir/dotnet" ] && sdk_stamp_fresh; then
            # A private install ran within sdk_stamp_days and did not clear the advisory: asking again would not either.
            say "$sdk_note"
        elif ! $can_ask; then
            say "$sdk_note To update it, run at a terminal: bash $script_dir/install.sh --apply"
        else
            say "$sdk_note"
            if ask_no_default "$private_offer"; then
                # The second build goes into its own stage; the first build is kept until that one succeeds.
                stage2="$stage.sdk"
                cleanup_dirs+=("$stage2")
                first_dotnet="$dotnet_cmd"
                if ! install_private_sdk; then
                    say "The .NET SDK install failed ($sdk_error); carrying on with the first build."
                elif ! { dotnet_cmd="$sdk_dir/dotnet"; mkdir -p "$stage2" && publish_stage "$stage2"; }; then
                    dotnet_cmd="$first_dotnet"
                    say "The build with the new private .NET SDK failed; carrying on with the first build."
                else
                    rm -rf "$stage"
                    stage="$stage2"
                fi
            fi
        fi
    fi
    find "$stage" -maxdepth 1 -type f -name '*.pdb' -delete
    [ -x "$stage/ahd" ] || fail_step "expected published binary not found at $stage/ahd"
    [ -x "$stage/ahd-tui" ] || fail_step "expected published binary not found at $stage/ahd-tui"
else
    would "$dotnet_cmd publish $plugin_root/native/src/Ah.Cli -c Release -r $rid -o $ah_home/build/stage.<pid> -p:AhVersion=$plugin_version -p:AhContract=$contract -p:AhPluginName=$plugin_name -p:ArtifactsPath=$ah_home/build/stage.<pid>/artifacts"
    would "$dotnet_cmd publish $plugin_root/native/src/Ah.Tui -c Release -r $rid -o $ah_home/build/stage.<pid> -p:AhVersion=$plugin_version -p:AhContract=$contract -p:AhPluginName=$plugin_name -p:ArtifactsPath=$ah_home/build/stage.<pid>/artifacts (ahd-tui, which 'ahd tui' runs)"
    would "remove *.pdb from the stage"
fi

# --- 4. Verify ---
step "Verify"

if $apply; then
    verify_home="$stage/verify-home"
    verify_ah="$stage/verify-ah-home"
    mkdir -p "$verify_home" "$verify_ah"
    where_json=$(AH_PLUGIN_ROOT="$plugin_root" HOME="$verify_home" AH_HOME="$verify_ah" "$stage/ahd" where 2>/dev/null) \
        || fail_step "'ahd where' failed on the built binary; refusing to install it"
    got_version=$(printf '%s' "$where_json" | sed -n 's/.*"binary":{[^}]*"version":"\([^"]*\)".*/\1/p')
    got_contract=$(printf '%s' "$where_json" | sed -nE 's/.*"binary":\{[^}]*"contract":("[^"]*"|[0-9]+).*/\1/p' | tr -d '"')
    got_plugin=$(printf '%s' "$where_json" | sed -n 's/.*"binary":{[^}]*"plugin_name":"\([^"]*\)".*/\1/p')
    if [ "$got_version" != "$plugin_version" ] || [ "$got_contract" != "$contract" ] || [ "$got_plugin" != "$plugin_name" ]; then
        fail_step "the built binary reports version '$got_version', contract '$got_contract', plugin '$got_plugin'; expected '$plugin_version', '$contract', '$plugin_name'. Nothing was installed."
    fi
    say "The built binary reports version $got_version, contract $got_contract."
else
    would "run <stage>/ahd where against a throwaway HOME and AH_HOME and check version, contract and plugin name"
fi

# --- 5. Place ---
step "Place"

binary_changed=false
if $apply; then
    old_sum=""
    [ -f "$target" ] && old_sum=$(cksum < "$target")
    new_sum=$(cksum < "$stage/ahd")
    [ "$old_sum" = "$new_sum" ] || binary_changed=true
    # ahd-tui goes first: ahd is the binary whose contract it is checked against, so it is placed last.
    tui_target="$ah_home/bin/ahd-tui"
    tmp_tui="$ah_home/bin/ahd-tui.$$.tmp"
    mv -f "$stage/ahd-tui" "$tmp_tui" || fail_step "cannot move ahd-tui into $ah_home/bin"
    chmod 0755 "$tmp_tui"
    mv -f "$tmp_tui" "$tui_target" || fail_step "cannot replace $tui_target"
    ah_write_contract_sidecar "$tui_target" "$plugin_version" "$contract" || fail_step "cannot write $tui_target.contract"
    say "Installed $tui_target"
    tmp_bin="$ah_home/bin/ahd.$$.tmp"
    mv -f "$stage/ahd" "$tmp_bin" || fail_step "cannot move the binary into $ah_home/bin"
    chmod 0755 "$tmp_bin"
    mv -f "$tmp_bin" "$target" || fail_step "cannot replace $target"
    ah_write_contract_sidecar "$target" "$plugin_version" "$contract" || fail_step "cannot write $target.contract"
    say "Installed $target"
    remove_stage
else
    would "move the staged binaries over $target and $ah_home/bin/ahd-tui (atomic renames) and write $ah_home/bin/ahd.contract and $ah_home/bin/ahd-tui.contract"
fi

# --- 6. Link ---
link_value=""
link_failed=false
daemon_failed=false
if $rebuild_only; then
    link_value=$(stamp_field link)
    say "Skipping the link (--rebuild-only)."
else
    step "Link"
    old_manual_link=false
    if [ -L "$link" ]; then
        existing=$(readlink "$link")
        case "$existing" in
            */plugins/cache/*/bin/ahd|*/agent-hierarchy/bin/ahd|*/agent-hierarchy-v2/bin/ahd) old_manual_link=true ;;
        esac
    fi
    if [ -L "$link" ] && [ "$(readlink "$link")" = "$target" ]; then
        say "$link already links to $target; nothing to do."
        link_value="$link"
    elif [ ! -e "$link" ] && [ ! -L "$link" ]; then
        if $apply; then
            mkdir -p "$prefix" && ln -s "$target" "$link" && say "Linked $link -> $target"
        else
            would "link $link -> $target"
        fi
        link_value="$link"
    elif $old_manual_link; then
        if $apply; then
            ln -sfn "$target" "$link" && say "Replaced the old manual link $link (it pointed into $existing) with $link -> $target"
        else
            would "replace the old manual link $link (it points into $existing) with $link -> $target"
        fi
        link_value="$link"
    else
        say "Not touching $link: something else is there (not a link to $target, and not the old manual link)."
        link_failed=true
    fi
fi

# --- 7. PATH ---
path_advice=""
if $rebuild_only; then
    :
elif $no_path; then
    step "PATH"
    say "Skipping PATH setup (--no-path)."
else
    step "PATH"
    symlink_path=""
    case "$(path_entry_present "$prefix" && echo yes)" in
        yes)
            say "$prefix is already on \$PATH; not touching any startup file."
            ;;
        *)
            for candidate_dir in "$HOME/bin" "/usr/local/bin"; do
                case ":$PATH:" in
                    *":$candidate_dir:"*) ;;
                    *) continue ;;
                esac
                [ -d "$candidate_dir" ] || continue
                [ -w "$candidate_dir" ] || continue
                if is_homebrew_prefix_dir "$candidate_dir"; then
                    continue
                fi

                candidate_link="$candidate_dir/ahd"
                if [ -e "$candidate_link" ] || [ -L "$candidate_link" ]; then
                    if [ -L "$candidate_link" ] && [ "$(readlink "$candidate_link")" = "$target" ]; then
                        symlink_path="$candidate_link"
                        say "$candidate_link already links to $target; nothing to do."
                        break
                    else
                        say "Not using $candidate_link for \$PATH: something else already exists there; leaving it untouched."
                        continue
                    fi
                fi

                if $apply; then
                    ln -s "$target" "$candidate_link"
                    say "Symlinked $candidate_link -> $target"
                else
                    would "symlink $candidate_link -> $target"
                fi
                symlink_path="$candidate_link"
                break
            done

            if [ -z "$symlink_path" ]; then
                if [[ "$prefix" == "$HOME"/* ]]; then
                    prefix_repr="\$HOME${prefix#"$HOME"}"
                else
                    prefix_repr="$prefix"
                fi

                shell_name=$(basename "${SHELL:-}")
                rc_file=""
                case "$shell_name" in
                    zsh) rc_file="$HOME/.zshrc" ;;
                    bash)
                        if [ -f "$HOME/.bashrc" ]; then
                            rc_file="$HOME/.bashrc"
                        else
                            rc_file="$HOME/.bash_profile"
                        fi
                        ;;
                esac

                if [ "$shell_name" = "fish" ]; then
                    path_advice="set -gx PATH $prefix_repr \$PATH"
                else
                    path_advice="export PATH=\"$prefix_repr:\$PATH\""
                fi

                if [ -z "$rc_file" ]; then
                    say "Shell '$shell_name' is not zsh or bash; not editing any startup file."
                    say "Add this line to your shell's startup file yourself:"
                    say "  $path_advice"
                else
                    block_content="# >>> ah >>>
# Added by agent-hierarchy's installer. Remove with scripts/uninstall.sh.
case \":\$PATH:\" in
  *\":$prefix_repr:\"*) ;;
  *) export PATH=\"$prefix_repr:\$PATH\" ;;
esac
# <<< ah <<<"
                    if ! $apply; then
                        would "back up $rc_file (if it exists) to ${rc_file}.ah-backup-<UTC timestamp>"
                        if [ -f "$rc_file" ] && grep -qxF '# >>> ah >>>' "$rc_file"; then
                            would "replace the existing ah PATH block in $rc_file (asks first at a terminal)"
                        else
                            would "append an ah PATH block to $rc_file (asks first at a terminal)"
                        fi
                    elif ! $interactive; then
                        say "Not editing $rc_file without your consent (this run is not at a terminal)."
                        say "Add this line to $rc_file yourself:"
                        say "  $path_advice"
                    elif ask_step "Add $prefix to PATH by editing $rc_file?"; then
                        if [ -f "$rc_file" ]; then
                            path_backup="${rc_file}.ah-backup-$(date -u +%Y%m%dT%H%M%SZ)"
                            cp -p "$rc_file" "$path_backup"
                            say "Backed up $rc_file to $path_backup"
                        fi
                        install_path_block "$rc_file" "$block_content"
                        say "Updated $rc_file to add $prefix to \$PATH"
                    else
                        say "Not editing $rc_file. Add this line yourself:"
                        say "  $path_advice"
                    fi
                fi
            fi
            ;;
    esac
fi


claude_dir="${CLAUDE_CONFIG_DIR:-${HOME:-}/.claude}"
skills_dir="$claude_dir/skills"
fd_dest="$skills_dir/frontend-design"

# obj_field <flat json object> <key>: the string value of a key, empty when absent or null.
obj_field() {
    printf '%s' "$1" | sed -nE "s/.*\"$2\":\"([^\"]*)\".*/\1/p" | head -1
}

design_prev=$(stamp_raw_field design_skills)
fd_state=$(obj_field "$design_prev" frontend_design)
fd_sha=$(obj_field "$design_prev" frontend_design_sha256)
fd_files=$(obj_field "$design_prev" frontend_design_files)
ux_prev=$(stamp_raw_field ux_skills)
ux_vercel=$(obj_field "$ux_prev" vercel-labs)
ux_jakub=$(obj_field "$ux_prev" jakubkrehel)
ux_addy=$(obj_field "$ux_prev" addyosmani)

# --- 7c. design skills: frontend-design ---
# Installed by default, first-party, from the pinned commit above: every file is downloaded and checked before anything is written.
# The stamp claims a copy only when this script wrote it, recording each file and its hash. A claimed copy that still holds exactly
# those files, each unchanged, is replaced when the pin moves; any other directory is the user's and is never touched or deleted.
sha256_of() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" 2>/dev/null | cut -d' ' -f1
    elif command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" 2>/dev/null | cut -d' ' -f1
    fi
}

# fd_dir_matches <name:sha list>: the directory holds exactly those files (no others, no links), each with that sha256.
fd_dir_matches() {
    local list="$1" e name want listed names
    [ -d "$fd_dest" ] && [ ! -L "$fd_dest" ] || return 1
    listed=$(ls -A "$fd_dest" 2>/dev/null | sort | tr '\n' ' ')
    names=$(for e in $list; do echo "${e%%:*}"; done | sort | tr '\n' ' ')
    [ -n "$names" ] && [ "$listed" = "$names" ] || return 1
    for e in $list; do
        name=${e%%:*}
        want=${e#*:}
        [ -f "$fd_dest/$name" ] && [ ! -L "$fd_dest/$name" ] && [ "$(sha256_of "$fd_dest/$name")" = "$want" ] || return 1
    done
    return 0
}

fd_pinned_sha() { # fd_pinned_sha <file name>
    local e
    for e in $FRONTEND_DESIGN_FILES; do
        if [ "${e%%:*}" = "$1" ]; then
            printf '%s' "${e#*:}"
            return 0
        fi
    done
    return 1
}

# Returns 1 with the destination untouched when a download fails or a hash differs.
fd_fetch() {
    local tmp e name want have new old
    command -v curl >/dev/null 2>&1 || return 1
    tmp=$(mktemp -d /tmp/ahfd.XXXXXX) || return 1
    [ -n "$tmp" ] && [ -d "$tmp" ] || return 1
    cleanup_dirs+=("$tmp")
    for e in $FRONTEND_DESIGN_FILES; do
        name=${e%%:*}
        want=${e#*:}
        if ! curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 --max-time 20 \
            -o "$tmp/$name" "https://raw.githubusercontent.com/anthropics/skills/$FRONTEND_DESIGN_COMMIT/skills/frontend-design/$name"; then
            rm -rf "$tmp"
            return 1
        fi
        have=$(sha256_of "$tmp/$name")
        if [ -z "$have" ] || [ "$have" != "$want" ]; then
            rm -rf "$tmp"
            return 1
        fi
    done
    # Both work directories are made here with mktemp, so the only directories removed are ones this call created.
    mkdir -p "$skills_dir" || { rm -rf "$tmp"; return 1; }
    new=$(mktemp -d "$skills_dir/.frontend-design.XXXXXX") || { rm -rf "$tmp"; return 1; }
    old=$(mktemp -d "$skills_dir/.frontend-design.XXXXXX") || { rm -rf "$new" "$tmp"; return 1; }
    for e in $FRONTEND_DESIGN_FILES; do
        name=${e%%:*}
        if ! cp "$tmp/$name" "$new/$name"; then
            rm -rf "$new" "$old" "$tmp"
            return 1
        fi
    done
    if [ -e "$fd_dest" ] && ! mv "$fd_dest" "$old/prev"; then
        rm -rf "$new" "$old" "$tmp"
        return 1
    fi
    if ! mv "$new" "$fd_dest"; then
        [ -e "$old/prev" ] && mv "$old/prev" "$fd_dest"
        rm -rf "$new" "$old" "$tmp"
        return 1
    fi
    rm -rf "$old" "$tmp"
}

fd_shown=$(printf '%s' "$fd_dest" | sed "s|^${HOME:-/nonexistent}/|~/|")
step "Design skills (frontend-design)"
if $rebuild_only; then
    say "frontend-design: skipped (--rebuild-only)."
elif ! $apply; then
    would "install Anthropic's frontend-design skill (Apache-2.0) into $fd_shown from a pinned commit, verifying each file's sha256 first (skipped with --no-design-skills or inside an agent session)"
elif $no_design_skills; then
    say "frontend-design: skipped (--no-design-skills). An installed copy stays; delete $fd_shown to remove it."
    fd_state=optout
elif [ -n "$in_session" ]; then
    say "frontend-design: skipped in an agent session; run the installer from a terminal."
    fd_state=agent-session
else
    fd_want=$(fd_pinned_sha SKILL.md)
    if [ ! -e "$fd_dest" ] && [ ! -L "$fd_dest" ]; then
        fd_go=true
    elif [ -n "$fd_files" ] && fd_dir_matches "$FRONTEND_DESIGN_FILES"; then
        # Exactly the pinned files, and this script claimed the directory earlier: keep the claim.
        say "frontend-design: present."
        [ "$fd_state" = installed ] || fd_state=present
        fd_sha="$fd_want"
        fd_files="$FRONTEND_DESIGN_FILES"
        fd_go=false
    elif [ -z "$fd_files" ] && fd_dir_matches "$FRONTEND_DESIGN_FILES"; then
        # Identical to the pin, but nothing says this script wrote it: present, and not claimed.
        say "frontend-design: present."
        fd_state=present
        fd_go=false
    elif [ -n "$fd_files" ] && fd_dir_matches "$fd_files"; then
        fd_go=true
    else
        say "frontend-design: present (user-managed); left untouched."
        fd_state=user-managed
        fd_go=false
    fi
    if $fd_go && fd_fetch; then
        fd_state=installed
        fd_sha="$fd_want"
        fd_files="$FRONTEND_DESIGN_FILES"
        echo "Added Anthropic's frontend-design skill (Apache-2.0) to $fd_shown. Remove: delete that folder, or re-run with --no-design-skills."
    elif $fd_go; then
        echo "frontend-design: download failed or did not verify; skipped" >&2
        fd_state=failed
    fi
fi

# --- 7d. design skills: the third-party sources (offered, never installed without a yes or --with-ux-skills) ---
plugin_cached() { # plugin_cached <marketplace> <plugin>: where Claude Code keeps an installed plugin
    [ -d "$claude_dir/plugins/cache/$1/$2" ]
}

ux_detect() { # ux_detect <source>: whether it is already installed
    case "$1" in
        vercel-labs) [ -d "$skills_dir/web-design-guidelines" ] || [ -d "$skills_dir/composition-patterns" ] ;;
        jakubkrehel) plugin_cached interfaces interfaces || [ -d "$skills_dir/better-interface" ] ;;
        addyosmani) plugin_cached addy-web-quality-skills web-quality-skills || [ -d "$skills_dir/web-quality-audit" ] ;;
        *) return 1 ;;
    esac
}

ux_set() { # ux_set <source> <state>
    case "$1" in
        vercel-labs) ux_vercel="$2" ;;
        jakubkrehel) ux_jakub="$2" ;;
        addyosmani) ux_addy="$2" ;;
    esac
}

ux_block() {
    cat >&2 <<'EOF'
Optional design sources for the ux-engineer role. They are third-party, never bundled, and the role works without them:
  vercel-labs/agent-skills       web-design-guidelines, composition-patterns            MIT   install by hand (npx skills add)
  jakubkrehel/skills             plugin "interfaces": better-interface, better-layout,
                                 better-typography, better-colors, better-accessibility,
                                 interface-review                                       MIT   Claude Code plugin
  addyosmani/web-quality-skills  plugin "web-quality-skills": accessibility, performance,
                                 core-web-vitals, web-quality-audit                     MIT   Claude Code plugin
EOF
}

ux_manual() { # ux_manual <source> <repo> <plugin@marketplace>
    case "$1" in
        vercel-labs)
            echo "vercel-labs/agent-skills documents only 'npx skills add vercel-labs/agent-skills'. ah never runs npx; install it yourself if you want it." >&2
            ;;
        *)
            echo "In Claude Code, run: /plugin marketplace add $2   then   /plugin install $3" >&2
            ;;
    esac
}

# Adds the marketplace, then installs the plugin, each shown through run_progress; a failure is reported by the caller, and
# its log is kept.
ux_install() { # ux_install <repo> <plugin@marketplace>
    local log rc
    log="$(mktemp "${TMPDIR:-/tmp}/ah-ux-install.XXXXXX")" || return 1
    run_progress "claude plugin marketplace add $1" "$log" claude plugin marketplace add "$1" \
        && run_progress "claude plugin install $2" "$log" claude plugin install "$2"
    rc=$?
    [ $rc -eq 0 ] && rm -f "$log"
    return $rc
}

step "Design skills (optional third-party sources)"
if $rebuild_only; then
    say "design sources: skipped (--rebuild-only)."
elif ! $apply; then
    would "offer the optional third-party design sources (vercel-labs, jakubkrehel, addyosmani) when any is missing; never install one without a yes or --with-ux-skills"
elif $no_ux_skills || $no_design_skills; then
    say "design sources: skipped (--no-ux-skills or --no-design-skills)."
else
    ux_missing=""
    for ux_src in vercel-labs jakubkrehel addyosmani; do
        if ux_detect "$ux_src"; then
            ux_set "$ux_src" present
        else
            ux_missing="$ux_missing $ux_src"
        fi
    done
    if [ -z "$ux_missing" ]; then
        say "design sources: all installed."
    else
        ux_run=false
        if $with_ux_skills; then
            ux_run=true
        elif $interactive && ! $quiet && [ -z "$in_session" ]; then
            echo "Design sources not installed:$ux_missing." >&2
            ux_block
            if ask_step "Install the plugin sources now (vercel-labs only gets manual steps)?"; then
                ux_run=true
            else
                for ux_src in $ux_missing; do ux_set "$ux_src" declined; done
                say "Not installing. Re-run install.sh with --with-ux-skills any time."
            fi
        elif ! $quiet; then
            echo "Design sources not installed:$ux_missing." >&2
            ux_block
            echo "To install the plugin sources: re-run with --with-ux-skills." >&2
        fi
        if $ux_run; then
            for ux_src in $ux_missing; do
                case "$ux_src" in
                    vercel-labs)
                        ux_manual vercel-labs
                        ux_set vercel-labs manual
                        ;;
                    jakubkrehel) ux_repo=jakubkrehel/skills; ux_plug=interfaces@interfaces ;;
                    addyosmani) ux_repo=addyosmani/web-quality-skills; ux_plug=web-quality-skills@addy-web-quality-skills ;;
                esac
                [ "$ux_src" = vercel-labs ] && continue
                if ! command -v claude >/dev/null 2>&1; then
                    ux_manual "$ux_src" "$ux_repo" "$ux_plug"
                    ux_set "$ux_src" manual
                elif ux_install "$ux_repo" "$ux_plug"; then
                    ux_set "$ux_src" installed
                    say "design sources: installed $ux_plug."
                else
                    echo "error: could not install $ux_plug; the install continues without it." >&2
                    ux_set "$ux_src" failed
                fi
            done
        fi
    fi
fi

# --- 8. Stamp ---
step "Stamp"

if $apply; then
    if $rebuild_only; then
        write_stamp "{\"version\":\"$(json_str "$plugin_version")\",\"at\":\"$(now_utc)\",\"state\":\"ok\"}" "$link_value"
    else
        keep_last=$(stamp_raw_field last_rebuild)
        [ -n "$keep_last" ] || keep_last=null
        write_stamp "$keep_last" "$link_value"
    fi
    say "Wrote $stamp"
else
    would "write $stamp (version, contract, binary, link, plugin_root, built_at, auto_rebuild, last_rebuild, design_skills, ux_skills)"
fi

# --- 8b. The stable ah link ---
# Agents and permission rules name $prefix/ah, which must lead to this plugin directory. The stamp is already written, so a
# crash before the re-point leaves the link on the old directory, which the gates refuse.
ah_link="$prefix/ah"
ah_link_want="$plugin_root/bin/ah"

plugin_name_of() {
    sed -n 's/.*"name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$1/.claude-plugin/plugin.json" 2>/dev/null | head -1
}

# Prints "ours" when the link target is a directory of this plugin beside the one being installed (or the one it replaced).
ah_link_state() {
    local cur dir parent_new parent_cur name_new name_cur
    if [ ! -e "$ah_link" ] && [ ! -L "$ah_link" ]; then echo absent; return; fi
    [ -L "$ah_link" ] || { echo "not a link"; return; }
    cur=$(readlink "$ah_link")
    case "$cur" in /*) ;; *) cur="$(dirname "$ah_link")/$cur" ;; esac
    [ "$(basename "$cur")" = ah ] && [ "$(basename "$(dirname "$cur")")" = bin ] || { echo "it leads to $cur, which is not a plugin's bin/ah"; return; }
    dir=$(dirname "$(dirname "$cur")")
    if [ -d "$dir" ]; then
        dir=$(physical_dir "$dir")
        [ "$dir" = "$(physical_dir "$plugin_root")" ] && { echo current; return; }
        name_cur=$(plugin_name_of "$dir")
        name_new=$(plugin_name_of "$plugin_root")
        [ -n "$name_cur" ] && [ "$name_cur" = "$name_new" ] || { echo "it leads to $dir, which is not a directory of $name_new"; return; }
    else
        # A pruned older version: it is named like a version directory (release, prerelease or dogfood build).
        printf '%s' "$(basename "$dir")" | grep -Eq '^[0-9]+[.][0-9]+[.][0-9]+([-+.][0-9A-Za-z.+-]*)?$' ||
            { echo "it leads to $dir, which is gone and is not named like a version directory of this plugin"; return; }
    fi
    parent_new=$(dirname "$(physical_dir "$plugin_root")")
    parent_cur=$(physical_dir "$(dirname "$dir")") || parent_cur=$(dirname "$dir")
    [ "$parent_cur" = "$parent_new" ] && echo ours || echo "it leads to $dir, which is not beside $plugin_root"
}

norm_dir() {
    local d="$1"
    while [ "${#d}" -gt 1 ] && [ "${d%/}" != "$d" ]; do d="${d%/}"; done
    physical_dir "$d" || printf '%s' "$d"
}

if [ "$(norm_dir "$prefix")" = "$(norm_dir "${HOME:-}/.local/bin")" ]; then
    state=$(ah_link_state)
else
    state="prefix"
fi
case "$state" in
    prefix) say "The stable ah path ${HOME:-}/.local/bin/ah is not in use: --prefix is $prefix, so permission rules name $ah_link_want." ;;
    current) say "$ah_link already leads to $ah_link_want; nothing to do." ;;
    absent|ours)
        if $apply; then
            tmp_link="$ah_link.tmp.$$"
            if mkdir -p "$prefix" && rm -f "$tmp_link" && ln -s "$ah_link_want" "$tmp_link" && mv -f "$tmp_link" "$ah_link"; then
                say "Linked $ah_link -> $ah_link_want"
            else
                rm -f "$tmp_link"
                say "Could not link $ah_link; permission rules fall back to $ah_link_want."
            fi
        else
            would "link $ah_link -> $ah_link_want (atomically, through a temporary link and a rename)"
        fi
        ;;
    *) say "Not touching $ah_link: $state; permission rules fall back to $ah_link_want." ;;
esac

# --- 9. Daemon ---
if $no_daemon_restart; then
    step "Daemon"
    say "Leaving the daemon as it is, starting nothing (--no-daemon-restart); start or restart it later with $target start."
else
    step "Daemon"
    if $apply; then
        status_json=$("$target" status 2>/dev/null || true)
        running_version=$(printf '%s' "$status_json" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' | head -1)
        daemon_action=""
        case "$status_json" in
            *'"up":true'*)
                if [ -n "$running_version" ] && [ "$running_version" != "$plugin_version" ]; then
                    if [ "$(ah_version_cmp "$running_version" "$plugin_version")" = -1 ]; then
                        say "Restarting the daemon ($running_version < $plugin_version)"
                        daemon_action=restart
                    fi
                elif $binary_changed; then
                    # the same version can be a different build: a daemon still running the replaced binary is drained and replaced
                    say "Restarting the daemon (the installed binary changed)"
                    daemon_action=restart
                fi
                ;;
            *)
                say "The daemon is not running; starting it"
                daemon_action=start
                ;;
        esac
        if [ -n "$daemon_action" ]; then
            daemon_err=$(mktemp "${TMPDIR:-/tmp}/ah-daemon-action.XXXXXX") || daemon_err=/dev/null
            if "$target" "$daemon_action" --plain 2>"$daemon_err"; then
                after=$("$target" status 2>/dev/null || true)
            else
                after=""
            fi
            daemon_said=""
            if [ "$daemon_err" != /dev/null ]; then
                daemon_said=$(cat "$daemon_err")
                rm -f "$daemon_err"
            fi
            daemon_problem="the daemon did not come up after '$target $daemon_action'; start it with $target start"
            daemon_refused=false
            case "$daemon_said" in
                # the explanation below stands in for the daemon's own error line
                *drain_refused*) daemon_refused=true; daemon_problem="'$target $daemon_action' was refused (drain_refused), so the old daemon keeps running until the first ah verb from a registered session replaces it" ;;
                ?*) printf '%s\n' "$daemon_said" >&2 ;;
            esac
            case "$after" in
                refused) ;;
                *'"up":true'*)
                    say "The daemon is up: pid $(printf '%s' "$after" | sed -n 's/.*"pid":\([0-9][0-9]*\).*/\1/p' | head -1), version $(printf '%s' "$after" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' | head -1)."
                    ;;
                *)
                    # the background rebuild at SessionStart must not fail the session over a daemon that stayed down; a refused
                    # restart fails no install, since the next ah verb replaces the old daemon
                    if $rebuild_only || $daemon_refused; then
                        say "warning: $daemon_problem"
                    else
                        say "error: $daemon_problem"
                        daemon_failed=true
                    fi
                    ;;
            esac
        fi
    else
        would "restart a running daemon of a lower version with $target restart, or start a stopped one with $target start"
    fi
fi

# --- 10. Login ---
if ! $rebuild_only && $login; then
    step "Login"
    if $apply; then
        "$target" login on || link_failed=true
    else
        would "run $target login on"
    fi
fi

# --- 11. Lifecycle permissions ---
# Allow rules for the Orchestrator's lifecycle commands, every ah command and SendMessage, in the user's Claude Code settings, so
# those calls skip the auto-mode classifier. Only this script, run by the user in
# their own terminal, writes them; the new binary's `ah roster permissions` does the work and refuses unless its build proves its
# caller by process ancestry and holds unreleased members. Never on --rebuild-only (the background rebuild), a dry run or a failed build.
lifecycle_doc="$plugin_root/docs/lifecycle-permissions.md"
lifecycle_settings="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/settings.json"
lifecycle_revoke="install.sh --apply --no-allow-lifecycle"

lifecycle_failed=false
lifecycle_output=""

# Runs one verb action and keeps its output: the script prints its own result line, and the verb's text only on failure.
lifecycle_run() {
    local rc=0
    lifecycle_output=$(AH_HOME="$ah_home" "$target" --plugin-root "$plugin_root" ah roster permissions "$1" --cwd "$HOME" 2>&1) || rc=$?
    return $rc
}

# A refused or failed write: always on stderr (--quiet included). With the flag the run exits non-zero once everything else is done.
lifecycle_fail() { # lifecycle_fail <added|removed|refreshed> <explicit: true|false> <retry command>
    local hint=""
    case "$lifecycle_output" in *ession*) hint=" from a terminal outside Claude Code" ;; esac
    echo "error: the lifecycle rules were not $1: ${lifecycle_output:-no output from ah roster permissions}. Retry$hint: $3" >&2
    if $2; then lifecycle_failed=true; fi
}

# The rules exactly as the installed binary would write them, one per line; empty when it cannot say.
lifecycle_rule_lines() {
    AH_HOME="$ah_home" "$target" --plugin-root "$plugin_root" ah roster permissions rules --cwd "$HOME" 2>/dev/null || true
}

# The date the rules were last set (the ledger's time of the last add or sync).
lifecycle_opt_in_date() {
    sed -n 's/.*"at"[[:space:]]*:[[:space:]]*"\([0-9-]\{10\}\)T.*/\1/p' "$ah_home/permissions.json" 2>/dev/null | head -1
}

lifecycle_ledger_root() {
    sed -n 's/.*"plugin_root"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ah_home/permissions.json" 2>/dev/null | head -1
}

lifecycle_ledger_yes() {
    grep -q '"answer"[[:space:]]*:[[:space:]]*"yes"' "$ah_home/permissions.json" 2>/dev/null
}

# The verb's report is indented JSON; its "notices" (for example rules that exist but are still off) and "warnings" (for
# example settings.json being a link) are about the write just made, so they are shown with the result line, --quiet included.
lifecycle_notices() {
    printf '%s\n' "$lifecycle_output" | grep -q '"changed": true' || return 0
    printf '%s\n' "$lifecycle_output" | awk '/"(notices|warnings)": \[$/ { in_list = 1; next } in_list && /^[[:space:]]*\]/ { in_list = 0 } in_list' | sed 's/^[[:space:]]*"//; s/",\{0,1\}$//'
}

# Every write to the settings file is announced, --quiet included.
lifecycle_added_line() {
    local count
    count=$(lifecycle_rule_lines | grep -c .)
    echo "lifecycle rules added ($count allow rules; list: ah roster permissions rules). Settings: $lifecycle_settings, previous file kept as $lifecycle_settings.ah2-bak. Revoke: $lifecycle_revoke"
    lifecycle_notices
}

lifecycle_prompt() {
    cat <<EOF
Wrangl can let your Orchestrator run its team lifecycle commands, any ah command and SendMessage without asking you each time.
It adds these allow rules to your Claude Code user settings:
$(printf '%s\n' "$1" | sed 's/^/  /')
Calls they match skip the permission prompt and the auto-mode classifier: the lifecycle commands, every ah command at this
path, SendMessage, and cd. Every other command, and every other path to ah, still asks.
Wrangl's own owner checks still refuse members and subagents. The settings file is reformatted (key order and layout are not
kept); a backup is kept as settings.json.ah2-bak. Full details: $lifecycle_doc
You can remove the rules any time with: $lifecycle_revoke
EOF
}

if $rebuild_only; then
    [ -z "$lifecycle" ] || say "Ignoring the lifecycle permissions flag: --rebuild-only skips that step."
elif [ "$lifecycle" = remove ]; then
    step "Lifecycle permissions"
    if $apply; then
        if lifecycle_run remove; then
            echo "lifecycle rules removed from $lifecycle_settings. Add them again: install.sh --apply --allow-lifecycle"
        else
            lifecycle_fail removed true "install.sh --apply --no-allow-lifecycle"
        fi
    else
        would "remove the allow rules ah added to your Claude Code settings (the ledger lists them)"
    fi
elif [ "$lifecycle" = add ]; then
    step "Lifecycle permissions"
    if $apply; then
        if lifecycle_run add; then
            lifecycle_added_line
        else
            lifecycle_fail added true "install.sh --apply --allow-lifecycle"
        fi
    else
        would "add the lifecycle allow rules (lifecycle commands, all of ah, SendMessage) to your Claude Code settings (once built, ah roster permissions rules lists them)"
    fi
elif $apply; then
    if [ -n "$in_session" ]; then
        if lifecycle_ledger_yes; then
            say "Lifecycle permissions: not refreshed ($in_session is set, so this runs inside an agent session). The rules still name the old version; rerun install.sh from a terminal, or run ah roster permissions sync there."
        else
            say "Lifecycle permissions: skipped ($in_session is set, so this runs inside an agent session). To add them later, run from a terminal: install.sh --apply --allow-lifecycle"
        fi
    else
        state=$(AH_HOME="$ah_home" "$target" --plugin-root "$plugin_root" ah roster permissions check --cwd "$HOME" 2>/dev/null || true)
        case "$state" in
            sync)
                # `check` prints sync for every recorded yes; the ledger's plugin root says whether the rules already name this version.
                if [ "$(lifecycle_ledger_root)" = "$plugin_root" ]; then
                    say "Lifecycle permissions: already in place for this version (rules last set on $(lifecycle_opt_in_date)); nothing to do."
                else
                    step "Lifecycle permissions"
                    previous_date=$(lifecycle_opt_in_date)
                    if lifecycle_run sync; then
                        echo "lifecycle rules refreshed for $plugin_version (previously set on $previous_date); revoke: $lifecycle_revoke"
                        lifecycle_notices
                    else
                        lifecycle_fail refreshed false "install.sh --apply, or ah roster permissions sync"
                    fi
                fi
                ;;
            ask)
                if $interactive && ! $quiet; then
                    step "Lifecycle permissions"
                    rules=$(lifecycle_rule_lines)
                    if [ -z "$rules" ]; then
                        say "Lifecycle permissions: skipped (the rule list could not be read, so there is nothing to approve). To add them later: install.sh --apply --allow-lifecycle"
                    else
                        lifecycle_prompt "$rules"
                        if ask_no_default "Add these rules?"; then
                            if lifecycle_run add; then
                                lifecycle_added_line
                            else
                                lifecycle_fail added false "install.sh --apply --allow-lifecycle"
                            fi
                        else
                            say "Not adding them. To add them later: install.sh --apply --allow-lifecycle"
                        fi
                    fi
                else
                    say "To let your Orchestrator run its lifecycle commands, any ah command and SendMessage without asking, run: install.sh --apply --allow-lifecycle"
                fi
                ;;
        esac
    fi
else
    step "Lifecycle permissions"
    would "ask (at a terminal, default no) whether to add the lifecycle allow rules (lifecycle commands, all of ah, SendMessage) to your Claude Code settings (once built, ah roster permissions rules lists them)"
fi

# --- 12. Codex trust ---
# Codex asks, once per git checkout, whether it may work there, and a Codex member waits on that screen until someone answers.
# The question is put here, in the user's own terminal; `ah roster codex-trust` is the only writer of the answer. Never on
# --rebuild-only, a dry run or a failed build, and never from inside an agent session.
codex_trust_failed=false
codex_trust_output=""

codex_trust_run() { # codex_trust_run [--yes]
    codex_trust_output=$(AH_HOME="$ah_home" "$target" --plugin-root "$plugin_root" ah roster codex-trust "$PWD" --cwd "$PWD" "$@" 2>&1)
}

codex_trust_unchanged() {
    printf '%s\n' "$codex_trust_output" | grep -q '"result"[[:space:]]*:[[:space:]]*"unchanged"'
}

codex_trust_fail() {
    echo "error: Codex was not told to trust $PWD: ${codex_trust_output:-no output from ah roster codex-trust}. Retry from a terminal: ah roster codex-trust --yes" >&2
    if $codex_trust; then codex_trust_failed=true; fi
}

if $rebuild_only; then
    ! $codex_trust || say "Ignoring --codex-trust: --rebuild-only skips that step."
elif ! $apply; then
    step "Codex trust"
    would "ask (at a terminal, default no, only when codex is on PATH and this is a git checkout) whether to tell Codex to trust this checkout (ah roster codex-trust)"
elif ! command -v codex >/dev/null 2>&1; then
    ! $codex_trust || say "Codex trust: skipped (no codex on PATH)."
elif ! git -C "$PWD" rev-parse --show-toplevel >/dev/null 2>&1; then
    ! $codex_trust || say "Codex trust: skipped ($PWD is not in a git checkout)."
elif [ -n "$in_session" ]; then
    say "Codex trust: skipped ($in_session is set, so this runs inside an agent session). To grant it, run from a terminal: ah roster codex-trust --yes"
elif ! codex_trust_run; then
    codex_trust_fail
elif codex_trust_unchanged; then
    say "Codex trust: Codex already trusts this checkout; nothing to do."
else
    step "Codex trust"
    if $codex_trust; then
        if codex_trust_run --yes; then
            echo "Codex now trusts this checkout; its config.toml was backed up beside itself as config.toml.ah2-bak."
        else
            codex_trust_fail
        fi
    elif $interactive && ! $quiet; then
        printf '%s\n' "$codex_trust_output"
        if ask_no_default "Tell Codex to trust this checkout?"; then
            if codex_trust_run --yes; then
                echo "Codex now trusts this checkout; its config.toml was backed up beside itself as config.toml.ah2-bak."
            else
                codex_trust_fail
            fi
        else
            say "Not granting it. To grant it later: ah roster codex-trust --yes"
        fi
    else
        say "Codex has not been told to trust this checkout, so a Codex member would wait at its trust screen. To grant it, run from a terminal: ah roster codex-trust --yes"
    fi
fi

# herdr desktop: one of its terminals sets HC_TERMINAL_ID. Offer to add Wrangl to the app; never do it without a yes.
if [ -n "${HC_TERMINAL_ID:-}" ]; then
    step "herdr desktop"
    if ! $apply; then
        would "ask (at a terminal, default no) whether to add Wrangl to the herdr desktop app: ah herdr install"
    elif [ -n "$in_session" ]; then
        say "You are in herdr desktop. To add Wrangl to it, run from a terminal: ah herdr install"
    elif $interactive && ! $quiet; then
        if ask_no_default "You are in herdr desktop. Add Wrangl to it now (ah herdr install)?"; then
            if AH_HOME="$ah_home" "$target" --plugin-root "$plugin_root" ah herdr install; then
                say "Wrangl is added to herdr desktop."
            else
                say "ah herdr install did not finish; the rest of the install is unaffected. Run it again later: ah herdr install"
            fi
        else
            say "Not adding it. To add it later: ah herdr install"
        fi
    else
        say "You are in herdr desktop. To add Wrangl to it: ah herdr install"
    fi
fi

step "Done"
if $apply; then
    say "ahd $plugin_version is installed at $target, with ahd-tui beside it."
else
    say "This was a dry run. Re-run with --apply to do it."
fi

if $link_failed || $lifecycle_failed || $codex_trust_failed || $daemon_failed; then
    exit 1
fi
exit 0
