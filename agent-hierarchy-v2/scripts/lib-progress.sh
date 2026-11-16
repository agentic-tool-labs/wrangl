#!/bin/bash
# agent-hierarchy — run_progress: shows a slow command's progress, so a long build or plugin update never looks hung.
# Sourced by scripts/publish-native.sh, scripts/install.sh and scripts/dogfood.sh.

# run_progress LABEL LOG CMD...: runs CMD with its stdout and stderr in LOG, and returns CMD's status.
#   stdout a terminal: one line redrawn in place (spinner, elapsed time, LABEL, CMD's latest output line), finished with
#                      ✓ or ✗ and the total time.
#   otherwise:         CMD's output is printed as it comes (and kept in LOG), with no control codes.
#   On failure, the last lines of LOG and its path go to stderr.
run_progress() {
    local label="$1" log="$2" start=$SECONDS rc=0
    shift 2
    if [ -t 1 ]; then
        local frames=(⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏) i=0 cols head line pid s old_traps
        cols=$(tput cols 2>/dev/null) || cols=80
        # CMD runs in the background so the line can redraw; a background command's stdin is /dev/null.
        "$@" > "$log" 2>&1 &
        pid=$!
        # A background command ignores Ctrl-C, so an interrupt or kill of this shell stops CMD's process tree here.
        old_traps=$(trap -p INT TERM HUP)
        for s in INT TERM HUP; do
            trap "_progress_abort $s $pid $start $(printf %q "$label")" "$s"
        done
        while kill -0 "$pid" 2>/dev/null; do
            head="${frames[i]} $label · $(_progress_elapsed "$start")"
            line=$(tail -n 20 "$log" 2>/dev/null | tr -d '\r' | grep -v '^[[:space:]]*$' | tail -n 1 | sed 's/^[[:space:]]*//')
            [ -n "$line" ] && head="$head · $line"
            printf '\r\033[K%s' "${head:0:cols-1}"
            i=$(( (i + 1) % ${#frames[@]} ))
            sleep 0.2
        done
        wait "$pid" || rc=$?
        trap - INT TERM HUP
        eval "$old_traps"
        if [ $rc -eq 0 ]; then head="✓"; else head="✗"; fi
        printf '\r\033[K%s %s · %s\n' "$head" "$label" "$(_progress_elapsed "$start")"
    else
        "$@" 2>&1 | tee "$log"
        rc=${PIPESTATUS[0]}
    fi
    if [ $rc -ne 0 ]; then
        echo "--- last lines of $log:" >&2
        tail -n 15 "$log" >&2
        echo "--- full log: $log" >&2
    fi
    return $rc
}

# _progress_abort SIG PID START LABEL: run_progress's handler for SIG. Stops PID and its descendants (TERM, then KILL),
# finishes the line with ✗ interrupted, restores the caller's traps and re-raises SIG, so this shell exits 128+SIG (130 on
# Ctrl-C) or runs the caller's own handler.
_progress_abort() {
    local tree p
    tree=$(_progress_tree "$2")
    kill -TERM $tree 2>/dev/null
    sleep 0.5
    for p in $tree; do kill -0 "$p" 2>/dev/null && kill -KILL "$p" 2>/dev/null; done
    wait "$2" 2>/dev/null
    printf '\r\033[K✗ %s · interrupted · %s\n' "$4" "$(_progress_elapsed "$3")"
    trap - INT TERM HUP
    eval "$old_traps"
    kill -"$1" $$
}

# _progress_tree PID: PID and every process descended from it, one per line.
_progress_tree() {
    local c
    echo "$1"
    for c in $(pgrep -P "$1"); do _progress_tree "$c"; done
}

# _progress_elapsed START: the time since START (a $SECONDS value), as 42s or 1m12s.
_progress_elapsed() {
    local s=$((SECONDS - $1))
    if [ $s -ge 60 ]; then printf '%dm%02ds' $((s / 60)) $((s % 60)); else printf '%ds' "$s"; fi
}
