#!/bin/sh
# Compatibility shim: the token bucket lives in bin/ah-test-run (also `ah test run`). The flags are the same, and --eta is now optional.
exec "$(cd "$(dirname "$0")" && pwd)/../bin/ah-test-run" "$@"
