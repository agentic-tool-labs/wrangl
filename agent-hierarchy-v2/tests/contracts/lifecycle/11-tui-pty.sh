#!/bin/bash
# K5 scenario 11: the native terminal client on a pseudo-terminal it owns (U-P3). Entering sets the alternate screen, hides the cursor and
# turns the mouse on with the line discipline raw; q, Ctrl-C, SIGINT, SIGTERM and SIGHUP each restore the terminal and exit 0; a resize
# redraws at the new size. Native only: the client under test is `ahd-tui` beside AHD_NATIVE_BIN. The pty is created by tests/contract-tools/
# tui-pty.py in a sandbox HOME/AH_HOME; no daemon runs (the client shows "ahd is not running") and no user terminal is touched.
. "$(dirname "$0")/_lib.sh"
if [ "$IMPL_CLIENT" != native ]; then echo "this scenario's client is the native ahd-tui (IMPL_CLIENT=$IMPL_CLIENT)"; exit 77; fi
lc_runtimes
lc_init
TUI="$(dirname "$AHD_NATIVE_BIN")/ahd-tui"
[ -x "$TUI" ] || { echo "no ahd-tui beside $AHD_NATIVE_BIN"; exit 77; }
command -v python3 >/dev/null 2>&1 || { echo "no python3"; exit 77; }
unset NO_COLOR COLUMNS
(cd "$PROJ" && timeout 90 python3 "$LC_PLUGIN/tests/contract-tools/tui-pty.py" "$TUI")
rc=$?
lc_check "the pty scenarios all passed" '[ "$rc" -eq 0 ]'
lc_check "no ahd-tui process of this sandbox remains" '[ -z "$(pgrep -f "^$TUI\$" 2>/dev/null)" ]'
lc_done
