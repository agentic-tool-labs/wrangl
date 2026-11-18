#!/bin/bash
# agent-hierarchy — publishes the two native binaries, ahd (Ah.Cli) and ahd-tui (Ah.Tui), into one directory.
# scripts/install.sh and tests/publish-bin.sh both publish through this script, so the pair is built one way.
#
# Usage: scripts/publish-native.sh --dotnet CMD --native DIR --rid RID --out DIR
#                                  --version V --contract C --name NAME [--warnaserror] [--log-dir DIR] [--artifacts DIR]
#                                  [--no-smoke] [--restore-log FILE]
#        scripts/publish-native.sh --smoke-only AHD       serve-start AHD in a throwaway sandbox, nothing else
#        scripts/publish-native.sh --check-log FILE       fail when FILE holds the AOT "will always throw" line, nothing else
#
#   --dotnet CMD      the dotnet executable
#   --native DIR      the plugin's native/ directory
#   --out DIR         where both executables are written (created if missing)
#   --warnaserror     fail a publish on any warning
#   --log-dir DIR     write each project's output to <DIR>/publish-<project>.log; otherwise it goes to this script's output
#   --artifacts DIR   where the build's bin and obj trees go, instead of <native>/artifacts, so a build run from a
#                     read-only or cached plugin directory writes nothing under it
#   --no-smoke        skip the serve smoke test (for a binary built for another platform, which cannot run here)
#   --restore-log FILE  after a successful restore, copy its output to FILE (install.sh reads it for SDK package advisories)
#
# A publish can succeed and still yield an ahd whose daemon can never start: the native compiler logs, for the serve verb,
# "will always throw because: Failed to load assembly" and the binary is some 4 MB smaller. Neither -warnaserror nor
# `ahd where` notices. So this script restores the solution once, serially, and publishes each project without a restore of
# its own; fails on that line in a project's publish output; and, after both publishes, starts `ahd serve` from the output
# directory in a throwaway home and requires it to come up (and then stops it).
#
# Beside each binary it writes <binary>.contract ({"version","contract"}), through the function scripts/install.sh writes the
# installed pair's sidecars with, so the tests that run a published ahd find the sidecars the installed one has.
#
# Exit codes: 0 both published and the daemon started; 2 a usage error, a publish failed (the project is named on stderr),
# the guard line was seen, or the smoke test failed.

set -u

script_dir="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib-contract.sh
. "$script_dir/lib-contract.sh" || { echo "publish-native: cannot load $script_dir/lib-contract.sh" >&2; exit 2; }
# shellcheck source=lib-progress.sh
. "$script_dir/lib-progress.sh" || { echo "publish-native: cannot load $script_dir/lib-progress.sh" >&2; exit 2; }

dotnet_cmd=""
native=""
rid=""
out=""
version=""
contract=""
name=""
warnaserror=false
log_dir=""
artifacts=""
smoke=true
smoke_only=""
check_log=""
restore_log=""

# The line the native compiler writes for a verb it compiled to a throw.
AOT_THROW='will always throw because: Failed to load assembly'

while [ $# -gt 0 ]; do
    case "$1" in
        --dotnet) dotnet_cmd="${2:-}"; shift 2 ;;
        --native) native="${2:-}"; shift 2 ;;
        --rid) rid="${2:-}"; shift 2 ;;
        --out) out="${2:-}"; shift 2 ;;
        --version) version="${2:-}"; shift 2 ;;
        --contract) contract="${2:-}"; shift 2 ;;
        --name) name="${2:-}"; shift 2 ;;
        --warnaserror) warnaserror=true; shift ;;
        --log-dir) log_dir="${2:-}"; shift 2 ;;
        --artifacts) artifacts="${2:-}"; shift 2 ;;
        --no-smoke) smoke=false; shift ;;
        --smoke-only) smoke_only="${2:-}"; shift 2 ;;
        --check-log) check_log="${2:-}"; shift 2 ;;
        --restore-log) restore_log="${2:-}"; shift 2 ;;
        *) echo "publish-native: unrecognized argument: $1" >&2; exit 2 ;;
    esac
done

# check_aot_log FILE: status 1, with the lines on stderr, when FILE holds the guard line.
check_aot_log() {
    local hits
    hits="$(grep -F "$AOT_THROW" "$1" 2>/dev/null | head -3)"
    [ -z "$hits" ] && return 0
    echo "publish-native: the native compiler compiled a verb to a throw, so this ahd cannot serve:" >&2
    printf '  %s\n' "$hits" >&2
    return 1
}

# smoke_serve AHD: starts `AHD serve` in a throwaway test-mode home and waits for it to write its run file, then stops it.
SMOKE_SB=""
SMOKE_PID=""
# smoke_cleanup: stops the smoke daemon this script started and removes its sandbox; runs on any exit, and on an interrupt.
smoke_cleanup() {
    if [ -n "$SMOKE_PID" ] && kill -0 "$SMOKE_PID" 2>/dev/null; then
        kill -KILL "$SMOKE_PID" 2>/dev/null
        wait "$SMOKE_PID" 2>/dev/null
    fi
    SMOKE_PID=""
    case "$SMOKE_SB" in /tmp/ahprobe.??????) rm -rf "$SMOKE_SB" ;; esac
    SMOKE_SB=""
}

smoke_serve() {
    local ahd="$1" sb pid i up=false
    trap smoke_cleanup EXIT
    trap 'exit 130' INT TERM HUP
    sb="$(mktemp -d /tmp/ahprobe.XXXXXX)" || { echo "publish-native: cannot create a smoke sandbox" >&2; return 1; }
    SMOKE_SB="$sb"
    mkdir -p "$sb/home" "$sb/ah/run"
    env -i PATH="$PATH" HOME="$sb/home" AH_HOME="$sb/ah" AH_TEST=1 "$ahd" serve --ah-home "$sb/ah" > "$sb/serve.log" 2>&1 &
    pid=$!
    SMOKE_PID="$pid"
    for i in $(seq 1 300); do
        kill -0 "$pid" 2>/dev/null || break
        if [ -f "$sb/ah/run/ahd.json" ]; then up=true; break; fi
        sleep 0.05
    done
    if kill -0 "$pid" 2>/dev/null; then
        kill "$pid" 2>/dev/null
        for i in $(seq 1 100); do kill -0 "$pid" 2>/dev/null || break; sleep 0.05; done
        kill -0 "$pid" 2>/dev/null && kill -KILL "$pid" 2>/dev/null
    fi
    wait "$pid" 2>/dev/null
    if ! $up; then
        echo "publish-native: $ahd serve did not come up; its output:" >&2
        head -c 1500 "$sb/serve.log" >&2
        echo >&2
    fi
    SMOKE_PID=""
    smoke_cleanup
    $up
}

if [ -n "$check_log" ]; then
    check_aot_log "$check_log" || exit 2
    exit 0
fi
if [ -n "$smoke_only" ]; then
    smoke_serve "$smoke_only" || exit 2
    exit 0
fi

for required in dotnet_cmd native rid out version contract name; do
    if [ -z "${!required}" ]; then
        echo "publish-native: --${required//_cmd/} is required" >&2
        exit 2
    fi
done

# One serial restore of the whole solution for this rid, then each project is published with --no-restore. A publish that
# restores its own project implicitly can leave the other project's assets, and with them the AOT compile of the serve verb,
# unresolved: the build still succeeds and yields an ahd whose daemon cannot start.
# No MSBuild node or compiler server outlives the restore or the publish.
export MSBUILDDISABLENODEREUSE=1
restore_args=(restore "$native/Ah.sln" --disable-parallel -r "$rid" "-p:AhVersion=$version" "-p:AhContract=$contract" "-p:AhPluginName=$name")
[ -n "$artifacts" ] && restore_args+=("-p:ArtifactsPath=$artifacts")
if [ -n "$log_dir" ]; then
    "$dotnet_cmd" "${restore_args[@]}" > "$log_dir/restore.log" 2>&1 \
        || { echo "publish-native: restore failed; see $log_dir/restore.log" >&2; exit 2; }
    [ -z "$restore_log" ] || cp "$log_dir/restore.log" "$restore_log"
else
    capture="$(mktemp "${TMPDIR:-/tmp}/publish-native.XXXXXX")" || { echo "publish-native: cannot create a log file" >&2; exit 2; }
    run_progress "restore Ah.sln ($rid)" "$capture" "$dotnet_cmd" "${restore_args[@]}" \
        || { echo "publish-native: restore failed" >&2; exit 2; }
    [ -z "$restore_log" ] || cp "$capture" "$restore_log"
    rm -f "$capture"
fi

for project in Ah.Cli Ah.Tui; do
    args=(publish "$native/src/$project" -c Release -r "$rid" -o "$out" --no-restore -p:UseSharedCompilation=false)
    $warnaserror && args+=(-warnaserror)
    args+=("-p:AhVersion=$version" "-p:AhContract=$contract" "-p:AhPluginName=$name")
    [ -n "$artifacts" ] && args+=("-p:ArtifactsPath=$artifacts")
    if [ -n "$log_dir" ]; then
        capture="$log_dir/publish-$project.log"
        "$dotnet_cmd" "${args[@]}" > "$capture" 2>&1 \
            || { echo "publish-native: publish of $project failed; see $capture" >&2; exit 2; }
    else
        capture="$(mktemp "${TMPDIR:-/tmp}/publish-native.XXXXXX")" || { echo "publish-native: cannot create a log file" >&2; exit 2; }
        run_progress "publish $project ($rid, native)" "$capture" "$dotnet_cmd" "${args[@]}" \
            || { echo "publish-native: publish of $project failed" >&2; exit 2; }
    fi
    if ! check_aot_log "$capture"; then
        echo "publish-native: publish of $project is unusable (log: $capture)" >&2
        exit 2
    fi
    [ -n "$log_dir" ] || rm -f "$capture"
done

if [ -n "$version" ] && [ -n "$contract" ]; then
    for binary in ahd ahd-tui; do
        ah_write_contract_sidecar "$out/$binary" "$version" "$contract" \
            || { echo "publish-native: cannot write $out/$binary.contract" >&2; exit 2; }
    done
fi

if $smoke; then
    if [ ! -x "$out/ahd" ]; then
        echo "publish-native: no ahd at $out/ahd to smoke-test" >&2
        exit 2
    fi
    smoke_serve "$out/ahd" || { echo "publish-native: the published ahd cannot start its daemon" >&2; exit 2; }
fi
