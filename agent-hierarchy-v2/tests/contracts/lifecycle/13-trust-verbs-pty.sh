#!/bin/bash
# K5 scenario 13: `ahd home trust|untrust` on a pseudo-terminal it owns, against the real binary (TA-4). In test mode the trust files live in
# AH_HOME, so the sandbox is the whole world: a terminal writes the file (mode 0600, one line, the realpath), a spawned session (AH_SPAWN_DEPTH
# at any value), a redirected stdin or a redirected stderr is refused with exit 2 and writes nothing, and untrust removes only the named line.
# Native only: the verbs exist in the native binary alone. The pty is created by tests/contract-tools/trust-pty.py.
. "$(dirname "$0")/_lib.sh"
if [ "$IMPL_CLIENT" != native ]; then echo "this scenario's client is the native ahd (IMPL_CLIENT=$IMPL_CLIENT)"; exit 77; fi
lc_runtimes
lc_init
command -v python3 >/dev/null 2>&1 || { echo "no python3"; exit 77; }
unset AH_SPAWN_DEPTH
(cd "$PROJ" && timeout 120 python3 "$LC_PLUGIN/tests/contract-tools/trust-pty.py" "$AHD_NATIVE_BIN" "$PROJ")
rc=$?
lc_check "the pty trust scenarios all passed" '[ "$rc" -eq 0 ]'
lc_check "the real home was not given a trust file by this scenario" '[ ! -e "$HOME/.claude/ah2/trusted-homes" ]'
lc_done
