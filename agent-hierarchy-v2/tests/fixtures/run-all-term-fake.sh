#!/bin/bash
# A fake test for test-run-all.sh: @M@ is replaced by the scenario directory. It takes a second to finish after TERM,
# and has a background child, so the runner's stop of its process group can be told from a release of the lock alone.
trap 'sleep 1; echo x >"@M@/termed"; exit 143' TERM
sleep 300 & echo $! >"@M@/gc.pid"
echo $$ >"@M@/t.pid"
wait
