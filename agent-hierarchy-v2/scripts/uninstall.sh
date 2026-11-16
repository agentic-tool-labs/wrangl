#!/bin/bash
# agent-hierarchy — removes what scripts/install.sh put in place. A dry run unless --apply.
#
# Usage: scripts/uninstall.sh [--apply] [--prefix DIR] [--remove-sdk] [--remove-build-cache] [--remove-lifecycle-rules]
#
#   --prefix DIR              the directory holding the ahd and ah links (default ~/.local/bin)
#   --remove-sdk              also remove <AH_HOME>/dotnet (the privately installed SDK)
#   --remove-build-cache      also remove <AH_HOME>/build
#   --remove-lifecycle-rules  remove the allow rules install.sh added to your Claude Code settings without asking
#                             (otherwise asked at a terminal, default no); rules you wrote are kept
#
# Every removal is one of a fixed set of names under <AH_HOME> or the prefix. The ahd link is removed only when it
# points at <AH_HOME>/bin/ahd, the ah link only when it points at a Wrangl plugin's bin/ah. Nothing else in <AH_HOME>
# is touched. Exit codes: 0 done (or planned); 1 --remove-lifecycle-rules failed, or was given inside an agent session; 2 usage.

set -u

apply=false
prefix="${HOME:-}/.local/bin"
remove_sdk=false
remove_build_cache=false
remove_rules=false
plugin_root="$(dirname "$(cd "$(dirname "$0")" && pwd)")"

usage() {
    sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
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
        --remove-sdk) remove_sdk=true; shift ;;
        --remove-build-cache) remove_build_cache=true; shift ;;
        --remove-lifecycle-rules) remove_rules=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *)
            echo "error: unrecognized argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

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
# The plugin directory install.sh linked <prefix>/ah into, read before step 5 removes the stamp.
stamp_root=$(sed -n 's/.*"plugin_root":"\([^"]*\)".*/\1/p' "$ah_home/install.json" 2>/dev/null | head -1)

# The same session test and [y/N] question as install.sh: the user's own run at a terminal is the consent to change their
# Claude Code settings, and an agent session never gives it.
in_session=""
[ "${AH_SPAWN_DEPTH+x}" ] && in_session=AH_SPAWN_DEPTH
[ -z "$in_session" ] && [ "${CLAUDECODE+x}" ] && in_session=CLAUDECODE
[ -z "$in_session" ] && [ "${CLAUDE_CODE_SESSION_ID+x}" ] && in_session=CLAUDE_CODE_SESSION_ID
if [ -n "$in_session" ] && $remove_rules; then
    echo "error: --remove-lifecycle-rules changes your Claude Code settings, which an agent session may not do ($in_session is set); run uninstall.sh from a terminal outside Claude Code, or drop the flag. Nothing was removed." >&2
    exit 1
fi
interactive=false
if [ -t 0 ] && [ -r /dev/tty ]; then
    interactive=true
fi
ask_no_default() {
    printf '%s [y/N] ' "$1"
    local reply=""
    read -r reply < /dev/tty || true
    case "$reply" in
        [yY]*) return 0 ;;
        *) return 1 ;;
    esac
}

say() {
    echo "$@"
}

would() {
    echo "would: $*"
}

# Removes one file or directory, and only when it is inside <AH_HOME> or the prefix.
safe_rm() {
    local path="$1" recursive="${2:-no}"
    case "$path" in
        "$ah_home"/?*|"$prefix"/?*) ;;
        *)
            echo "error: refusing to remove $path: it is outside $ah_home and $prefix" >&2
            return 1
            ;;
    esac
    if [ "$recursive" = recursive ]; then
        rm -rf "$path"
    else
        rm -f "$path"
    fi
}

# Removes the delimited PATH block (and the single blank line the installer put before it, if present) from
# rc_file, leaving every other line byte-identical to what the installer found there.
strip_path_block() {
    local rc_file="$1" tmp start_line rel_end_line end_line cut_from prev_line link_dir hops

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
    cp -p "$rc_file" "$tmp"

    start_line=$(grep -nxF '# >>> ah >>>' "$rc_file" | head -1 | cut -d: -f1)
    rel_end_line=$(tail -n "+$start_line" "$rc_file" | grep -nxF '# <<< ah <<<' | head -1 | cut -d: -f1)
    if [ -z "$rel_end_line" ]; then
        rm -f "$tmp"
        echo "error: $rc_file has a start marker but no end marker; leaving it untouched" >&2
        return 1
    fi
    end_line=$((start_line + rel_end_line - 1))

    cut_from="$start_line"
    if [ "$start_line" -gt 1 ]; then
        prev_line=$(sed -n "$((start_line - 1))p" "$rc_file")
        if [ -z "$prev_line" ]; then
            cut_from=$((start_line - 1))
        fi
    fi

    if [ "$cut_from" -gt 1 ]; then
        head -n "$((cut_from - 1))" "$rc_file" > "$tmp"
    else
        : > "$tmp"
    fi
    tail -n "+$((end_line + 1))" "$rc_file" >> "$tmp"

    mv "$tmp" "$rc_file"
}

# --- 1. Login item ---
echo
echo "── Login item"
if [ -x "$target" ]; then
    if $apply; then
        "$target" login off || say "warning: 'ahd login off' failed; the login item may still be present"
    else
        would "run $target login off"
    fi
else
    say "No binary at $target; no login item to turn off."
fi

# --- 2. Lifecycle permissions ---
# The allow rules install.sh added with the user's consent. `ah roster permissions remove`, the call install.sh
# --no-allow-lifecycle makes, removes exactly the rules its ledger says ah added, so a rule the user wrote is never touched.
# It runs before step 5 removes the binary that reads the ledger.
echo
echo "── Lifecycle permissions"
rules_settings="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/settings.json"
rules_failed=false
rules_remove() {
    local out
    if out=$(AH_HOME="$ah_home" "$target" --plugin-root "$plugin_root" ah roster permissions remove --cwd "$HOME" 2>&1); then
        say "Removed the allow rules ah added from $rules_settings (rules you wrote were kept)."
    else
        echo "error: the lifecycle rules were not removed: ${out:-no output from ah roster permissions}. Retry from a terminal: uninstall.sh --apply --remove-lifecycle-rules" >&2
        rules_failed=true
    fi
}
if [ ! -x "$target" ]; then
    say "No binary at $target to read the ledger of allow rules ah added; any left in $rules_settings are listed in $plugin_root/docs/lifecycle-permissions.md."
elif ! $apply; then
    if $remove_rules; then
        would "remove the allow rules ah added to $rules_settings (the ledger lists them; rules you wrote are kept)"
    else
        would "ask (at a terminal, default no) whether to remove the allow rules ah added to $rules_settings (rules you wrote are kept); --remove-lifecycle-rules removes them without asking"
    fi
elif [ -n "$in_session" ]; then
    say "Lifecycle permissions: skipped ($in_session is set, so this runs inside an agent session). To remove them, run from a terminal: uninstall.sh --apply --remove-lifecycle-rules"
elif $remove_rules; then
    rules_remove
else
    rules_state=$(AH_HOME="$ah_home" "$target" --plugin-root "$plugin_root" ah roster permissions check --cwd "$HOME" 2>/dev/null || true)
    case "$rules_state" in
        ask|"skip: the user answered no") say "No allow rules are recorded as added by ah; nothing to remove." ;;
        *)
            if $interactive; then
                if ask_no_default "Remove the allow rules ah added to $rules_settings (rules you wrote are kept)?"; then
                    rules_remove
                else
                    say "Kept them. To remove them later: uninstall.sh --apply --remove-lifecycle-rules"
                fi
            else
                say "Kept any allow rules ah added (no terminal to ask). To remove them: uninstall.sh --apply --remove-lifecycle-rules"
            fi
            ;;
    esac
fi

# --- 3. Daemon ---
echo
echo "── Daemon"
if [ -x "$target" ]; then
    if $apply; then
        "$target" stop >/dev/null 2>&1 || true
        say "Asked the daemon to stop (failures are ignored)."
    else
        would "run $target stop (failures ignored)"
    fi
else
    say "No binary at $target; nothing to stop."
fi

# --- 4. Links ---
echo
echo "── Links"
remove_link() {
    local candidate="$1"
    if [ -L "$candidate" ]; then
        if [ "$(readlink "$candidate")" = "$target" ]; then
            if $apply; then
                rm -f "$candidate"
                say "Removed $candidate -> $target"
            else
                would "remove $candidate -> $target"
            fi
        else
            say "Not removing $candidate: it is a link, but not to $target."
        fi
    elif [ -e "$candidate" ]; then
        say "Not removing $candidate: it is not a link ahd created."
    fi
}
remove_link "$link"
for candidate_dir in "$HOME/bin" "/usr/local/bin"; do
    [ "$candidate_dir/ahd" = "$link" ] && continue
    remove_link "$candidate_dir/ahd"
done

# <prefix>/ah leads to a plugin's bin/ah, not to <AH_HOME>; once the plugin is uninstalled it is a broken link. It is removed
# only when it is a link into a Wrangl plugin: the marketplace cache, the dogfood marketplace's cache, or the stamp's plugin root.
ah_link="$prefix/ah"
if [ -L "$ah_link" ]; then
    ah_to=$(readlink "$ah_link")
    case "$ah_to" in /*) ;; *) ah_to="$prefix/$ah_to" ;; esac
    case "$ah_to" in
        */plugins/cache/wrangl/wrangl/*/bin/ah|*/plugins/cache/wrangl-dogfood/wrangl/*/bin/ah) ah_ours=true ;;
        *) [ -n "$stamp_root" ] && [ "$ah_to" = "$stamp_root/bin/ah" ] && ah_ours=true || ah_ours=false ;;
    esac
    if ! $ah_ours; then
        say "Not removing $ah_link: it is a link, but not to a Wrangl plugin's bin/ah."
    elif $apply; then
        rm -f -- "$ah_link"
        say "Removed $ah_link -> $ah_to"
    else
        would "remove $ah_link -> $ah_to"
    fi
elif [ -e "$ah_link" ]; then
    say "Not removing $ah_link: it is not a link the installer created."
fi

# A failed rule removal stops here: the binary is what reads the ledger, so removing it would strand the rules.
if $apply && $rules_failed; then
    echo "error: stopped before removing the binary, so the rules can still be removed. Fix the error above and run this again. To finish the uninstall and leave the rules in place, run it again and answer no to removing them (without --remove-lifecycle-rules). To put the links and the daemon back, run scripts/install.sh --apply. The rules are described in $plugin_root/docs/lifecycle-permissions.md." >&2
    exit 1
fi

# --- 5. Binaries, sidecars and stamp ---
echo
echo "── Files"
for name in "$ah_home/bin/ahd" "$ah_home/bin/ahd.contract" "$ah_home/bin/ahd-tui" "$ah_home/bin/ahd-tui.contract" "$ah_home/install.json"; do
    if [ -e "$name" ] || [ -L "$name" ]; then
        if $apply; then
            safe_rm "$name" && say "Removed $name"
        else
            would "remove $name"
        fi
    fi
done

# --- 6. rc block ---
echo
echo "── PATH block"
found_block=false
for rc_file in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile"; do
    if [ -f "$rc_file" ] && grep -qxF '# >>> ah >>>' "$rc_file"; then
        found_block=true
        if $apply; then
            strip_path_block "$rc_file" && say "Removed the ah PATH block from $rc_file"
        else
            would "remove the ah PATH block from $rc_file"
        fi
    fi
done
$found_block || say "No ah PATH block found."

# --- 7. Optional ---
if $remove_sdk; then
    echo
    echo "── SDK"
    if [ -d "$ah_home/dotnet" ]; then
        if $apply; then
            safe_rm "$ah_home/dotnet" recursive && say "Removed $ah_home/dotnet"
        else
            would "remove $ah_home/dotnet"
        fi
    else
        say "No private SDK at $ah_home/dotnet."
    fi
fi
if $remove_build_cache; then
    echo
    echo "── Build cache"
    if [ -d "$ah_home/build" ]; then
        if $apply; then
            safe_rm "$ah_home/build" recursive && say "Removed $ah_home/build"
        else
            would "remove $ah_home/build"
        fi
    else
        say "No build cache at $ah_home/build."
    fi
fi

echo
if $apply; then
    say "Done. The rest of $ah_home was left as it was."
else
    say "This was a dry run. Re-run with --apply to do it."
fi
$rules_failed && exit 1
exit 0
