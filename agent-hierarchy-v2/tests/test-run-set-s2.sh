#!/bin/bash
# agent-hierarchy — tests/run-set.sh, part 2 of 3 (section 13): --base, the verdict against a base commit, the fingerprint and what
# makes a base a baseline. Stand-ins and store as in part 1; the setup is tests/lib-run-set-test.sh's.
# Usage: bash tests/test-run-set-s2.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-run-set-test.sh"

# 13. --base: the verdict against a base commit (NEW, FIXED, STILL-FAILING, FLAKY, ADDED, REMOVED), and fail fast counting NEW only
fullruns() { grep "^WAIT" "$1" | grep -vc -- "--exact"; }
mkcommit sb-base; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"
BASE=$SHA; BST=$ST
mkcommit sb-head; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "base fails {s1,s2}, head fails {s2,s3}: NEW {s3}, FIXED {s1}, STILL {s2}, and the new-failures exit (4)" '[ $RC = 4 ] && [ "$(vjson shell r.verdict)" = "\"new-failures\"" ] && [ "$(vjson shell r.classes.new)" = "[\"test-s3\"]" ] && [ "$(vjson shell r.classes.fixed)" = "[\"test-s1\"]" ] && [ "$(vjson shell r.classes.still)" = "[\"test-s2\"]" ]'
check "...the base was not stored before, so the call ran it (once), and the result.json names it" '[ "$(fullruns "$BST/shell.calls")" = 1 ] && [ "$(vjson shell r.base)" = "\"$BASE\"" ]'
check "...only the NEW failure was rerun alone; the STILL-FAILING one was not" '[ "$(grep -c -- "--exact" "$ST/shell.calls")" = 1 ] && grep -q -- "--exact s3" "$ST/shell.calls"'
check "...the verdict line and the NEW name are printed" 'printf %s "$OUT" | grep -q "verdict=new-failures" && printf %s "$OUT" | grep -q "NEW test-s3"'
check "...the shell set ran with run-all early abort off" 'grep -q -- "--no-abort" "$ST/shell.calls"'
rs "$SHA" shell --base "$BASE"
check "the second call answers from the store (head and base): same verdict, exit 4, the base run once" '[ $RC = 4 ] && printf %s "$OUT" | grep -q "cached=yes" && [ "$(fullruns "$BST/shell.calls")" = 1 ]'
rs "$SHA" shell
check "the same stored result without --base keeps its plain exit (1: FAIL) and prints no verdict (nearest non-match)" '[ $RC = 1 ] && ! printf %s "$OUT" | grep -q "verdict="'

mkcommit sb-still; printf 'test-s2\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "head fails {s2} only, as the base does: verdict clean, exit 0, although the status is FAIL" '[ $RC = 0 ] && [ "$(vjson shell r.verdict)" = "\"clean\"" ] && [ "$(rjson shell status)" = FAIL ] && [ "$(vjson shell r.classes.still)" = "[\"test-s2\"]" ] && [ "$(vjson shell r.classes.new)" = "[]" ]'
check "...a failure that already fails at the base is not rerun" '! grep -q -- "--exact" "$ST/shell.calls"'

mkcommit sb-added; printf 'test-s4\n' > "$ST/shell.extra"; printf 'test-s4\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "a suite that exists only at the head and fails is NEW (and ADDED); the base's failures that now pass are FIXED" '[ $RC = 4 ] && [ "$(vjson shell r.classes.new)" = "[\"test-s4\"]" ] && [ "$(vjson shell r.classes.added)" = "[\"test-s4\"]" ] && [ "$(vjson shell r.classes.fixed)" = "[\"test-s1\",\"test-s2\"]" ]'
mkcommit sb-added-ok; printf 'test-s5\n' > "$ST/shell.extra"; printf 'test-s2\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "a suite that exists only at the head and passes is ADDED and not NEW (nearest non-match): verdict clean" '[ $RC = 0 ] && [ "$(vjson shell r.classes.added)" = "[\"test-s5\"]" ] && [ "$(vjson shell r.classes.new)" = "[]" ]'
mkcommit sb-removed; printf 'test-s1\n' > "$ST/shell.drop"; : > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "a suite that failed at the base and is not at the head is REMOVED, not FIXED" '[ $RC = 0 ] && [ "$(vjson shell r.classes.removed)" = "[\"test-s1\"]" ] && [ "$(vjson shell r.classes.fixed)" = "[\"test-s2\"]" ]'

mkcommit sb-flaky; printf 'test-s3\n' > "$ST/shell.fails"; printf 'test-s3\n' > "$ST/shell.passes-alone"
rs "$SHA" shell --base "$BASE"
check "a NEW candidate that passes when rerun alone is FLAKY, not NEW: verdict clean, exit 0, status FLAKY-PASS" '[ $RC = 0 ] && [ "$(rjson shell status)" = FLAKY-PASS ] && [ "$(vjson shell r.classes.flaky)" = "[\"test-s3\"]" ] && [ "$(vjson shell r.classes.new)" = "[]" ]'

# fail fast counts the NEW failures only
mkcommit sb-dn-base; printf 'Ah.Fake.Tests.C.F1\nAh.Fake.Tests.C.F2\nAh.Fake.Tests.C.F3\nAh.Fake.Tests.C.F4\nAh.Fake.Tests.C.F5\nAh.Fake.Tests.C.F6\nAh.Fake.Tests.C.F7\nAh.Fake.Tests.C.F8\nAh.Fake.Tests.C.F9\nAh.Fake.Tests.C.F10\nAh.Fake.Tests.C.F11\nAh.Fake.Tests.C.F12\n' > "$ST/fails"
DBASE=$SHA; DBST=$ST
mkcommit sb-dn-head; cp "$DBST/fails" "$ST/fails"; printf 'Ah.Fake.Tests.C.N1\n' >> "$ST/fails"
rs "$SHA" dotnet --base "$DBASE"
check "R1 with --base: 12 failures that already fail at the base plus 1 NEW is not SYSTEMIC; only the NEW one is rerun; verdict new-failures (exit 4)" '[ $RC = 4 ] && [ "$(rjson dotnet status)" = FAIL ] && [ "$(calls dotnet.reruns)" = 1 ] && [ "$(vjson dotnet r.classes.new)" = "[\"Ah.Fake.Tests.C.N1\"]" ] && [ "$(vjson dotnet r.classes.still.length)" = 12 ]'
check "...the base itself, with 12 failures, was run to the end and stored as FAIL, not SYSTEMIC" '[ "$(node "$TOOLS" field "$AH_RESULTS_DIR/$DBASE/dotnet/result.json" status)" = FAIL ]'
mkcommit sb-dn-nobase; cp "$SB/st-sb-dn-head/fails" "$ST/fails"
rs "$SHA" dotnet
check "R1 without --base is unchanged (nearest non-match): the same 13 failures are SYSTEMIC, exit 1, no rerun" '[ $RC = 1 ] && [ "$(rjson dotnet status)" = SYSTEMIC ] && [ "$(calls dotnet.reruns)" = 0 ]'
mkcommit sb-dn-allnew; printf 'Ah.Fake.Tests.C.M1\nAh.Fake.Tests.C.M2\nAh.Fake.Tests.C.M3\nAh.Fake.Tests.C.M4\nAh.Fake.Tests.C.M5\nAh.Fake.Tests.C.M6\nAh.Fake.Tests.C.M7\nAh.Fake.Tests.C.M8\nAh.Fake.Tests.C.M9\nAh.Fake.Tests.C.M10\nAh.Fake.Tests.C.M11\n' > "$ST/fails"
rs "$SHA" dotnet --base "$DBASE"
check "R1 with --base: 11 NEW failures are SYSTEMIC all the same (nearest non-match), and the verdict says new-failures" '[ $RC = 4 ] && [ "$(rjson dotnet status)" = SYSTEMIC ] && [ "$(calls dotnet.reruns)" = 0 ]'

# the fingerprint
mkcommit sb-fp-head; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
CLAUDECODE=1 rs "$SHA" shell --base "$BASE"
check "a head made inside an agent session against a base made outside one is incomparable (exit 5), names the reason, reports no NEW" '[ $RC = 5 ] && [ "$(vjson shell r.verdict)" = "\"incomparable\"" ] && printf %s "$OUT" | grep -q "agent session" && [ "$(vjson shell r.classes.new)" = "[]" ]'
check "...the base was not run again, and R1 and run-all were left as without --base (no --no-abort)" '[ "$(fullruns "$BST/shell.calls")" = 1 ] && ! grep -q -- "--no-abort" "$ST/shell.calls"'
mkcommit sb-fp-head2; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
CLAUDECODE=1 rs "$SHA" shell --base "$BASE" --fresh-base
check "--fresh-base runs the base again where this run is, and then compares (NEW {s3}, exit 4)" '[ "$(fullruns "$BST/shell.calls")" = 2 ] && [ $RC = 4 ] && [ "$(vjson shell r.classes.new)" = "[\"test-s3\"]" ]'
mkcommit sb-fp-head3; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
CLAUDECODE=1 rs "$SHA" shell --base "$BASE"
check "a base that was made where this run is needs no --fresh-base (nearest non-match): compared, exit 4" '[ $RC = 4 ] && [ "$(fullruns "$BST/shell.calls")" = 2 ]'
mkcommit sb-nofp-base; printf 'test-s1\n' > "$ST/shell.fails"
NOFP=$SHA; rs "$NOFP" shell
node -e 'const fs=require("fs"),f=process.argv[1],r=JSON.parse(fs.readFileSync(f,"utf8"));delete r.env;fs.writeFileSync(f,JSON.stringify(r))' "$AH_RESULTS_DIR/$NOFP/shell/result.json"
mkcommit sb-nofp-head; printf 'test-s1\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$NOFP"
check "a base result with no fingerprint is incomparable, not guessed at" '[ $RC = 5 ] && printf %s "$OUT" | grep -q "no environment fingerprint"'

# what makes a base a baseline: compared on results written by hand
FPJ=$(node "$TOOLS" env)
mkres() { # <file> <status> <failures json> <env json>
  node -e 'require("fs").writeFileSync(process.argv[1], JSON.stringify({ sha: "x", set: "shell", status: process.argv[2], failures: JSON.parse(process.argv[3]), flaky: [], counts: { total: 30, passed: 0, failed: 0 }, env: JSON.parse(process.argv[4]) }))' "$@"; }
cmpv() { node "$TOOLS" compare --head "$CH" --base "$CB" --basesha b > "$CO" 2>&1; CRC=$?; }
CH="$SB/head.json"; CB="$SB/base.json"; CO="$SB/cmp.out"
F2='[{"name":"t1","failed_twice":true}]'; F1='[{"name":"t1","failed_twice":false}]'
mkres "$CH" FAIL "$F2" "$FPJ"; mkres "$CB" FAIL "$F2" "$FPJ"; cmpv
check "a failure that failed again on its isolated rerun at the base is STILL, verdict clean" '[ $CRC = 0 ] && grep -q "\"verdict\": \"clean\"" "$CO" && grep -q "t1" "$CO"'
mkres "$CB" FAIL "$F1" "$FPJ"; cmpv
check "a base failure that was not confirmed on its rerun is no baseline: the same failure at the head is NEW (exit 4)" '[ $CRC = 4 ]'
FS='[{"name":"t1","first_error":"boom","failed_twice":false}]'; FN='[{"name":"t1","first_error":"not-rerun: boom","failed_twice":false}]'
mkres "$CB" FAIL "$FS" "$FPJ"; cmpv
check "a still-class base failure (failed at its own base, stored failed_twice false with its error) is a baseline: STILL, verdict clean" '[ $CRC = 0 ] && grep -q "\"verdict\": \"clean\"" "$CO"'
check "...and the base's failure list names it" '[ "$(node "$TOOLS" keys "$CB")" = t1 ]'
mkres "$CB" FAIL "$FN" "$FPJ"; cmpv
check "a base row that was never rerun (not-rerun) is no baseline: NEW (exit 4) (nearest non-match)" '[ $CRC = 4 ] && [ -z "$(node "$TOOLS" keys "$CB")" ]'
mkres "$CB" SYSTEMIC "$F2" "$FPJ"; cmpv
check "a base that ended SYSTEMIC is incomparable (exit 5), never a baseline" '[ $CRC = 5 ] && grep -q "SYSTEMIC" "$CO"'
mkres "$CB" ERROR "$F2" "$FPJ"; cmpv
check "...and so is one that ended ERROR" '[ $CRC = 5 ]'
mkres "$CB" FLAKY-PASS "[]" "$FPJ"; cmpv
check "a base that ended FLAKY-PASS is a baseline (nearest non-match): its failures are none, so the head failure is NEW" '[ $CRC = 4 ]'
mkres "$CB" FAIL "$F2" "$(node -e 'const f=JSON.parse(process.argv[1]); f.workers+=1; console.log(JSON.stringify(f))' "$FPJ")"; cmpv
check "a base run with another worker count is incomparable (exit 5)" '[ $CRC = 5 ] && grep -q "workers" "$CO"'
mkres "$CB" FAIL "$F2" "$(node -e 'const f=JSON.parse(process.argv[1]); f.sandbox=true; console.log(JSON.stringify(f))' "$FPJ")"; cmpv
check "a base run with another sandbox flag is incomparable (exit 5)" '[ $CRC = 5 ] && grep -q "sandbox" "$CO"'
WK() { env "$@" node "$TOOLS" env | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).workers)'; }
check "the fingerprint records run-all's suite worker count: two thirds of the cpus up to 12, and no more than the granted test tokens under a held grant" \
  '[ "$(WK AH_TEST_SLOT_HELD=1 AH_TEST_TOKENS_GRANTED=1)" = 1 ] && [ "$(WK AH_TEST_SLOT_HELD=1 AH_TEST_TOKENS_GRANTED=1000)" -ge 1 ] && [ "$(WK AH_TEST_SLOT_HELD=0)" -le 12 ]'
check "...the granted tokens do not change it when no slot is held (nearest non-match)" '[ "$(WK AH_TEST_SLOT_HELD=0 AH_TEST_TOKENS_GRANTED=1)" = "$(WK AH_TEST_SLOT_HELD=0 AH_TEST_TOKENS_GRANTED=1000)" ]'

# a base is run to the end: 13 failures at the base are not SYSTEMIC there, and the stored base is FAIL
mkcommit sb-bs-base; printf 'Ah.Fake.Tests.C.B1\nAh.Fake.Tests.C.B2\nAh.Fake.Tests.C.B3\nAh.Fake.Tests.C.B4\nAh.Fake.Tests.C.B5\nAh.Fake.Tests.C.B6\nAh.Fake.Tests.C.B7\nAh.Fake.Tests.C.B8\nAh.Fake.Tests.C.B9\nAh.Fake.Tests.C.B10\nAh.Fake.Tests.C.B11\nAh.Fake.Tests.C.B12\nAh.Fake.Tests.C.B13\n' > "$ST/fails"
BSB=$SHA; BSF=$(cat "$ST/fails")
mkcommit sb-bs-head; printf '%s\n' "$BSF" > "$ST/fails"
rs "$SHA" dotnet --base "$BSB"
check "a base run for --base has no systemic limit: 13 failures there are stored as FAIL, not SYSTEMIC, and 13 STILL at the head are a clean verdict" \
  '[ "$(node "$TOOLS" field "$AH_RESULTS_DIR/$BSB/dotnet/result.json" status)" = FAIL ] && [ $RC = 0 ] && [ "$(vjson dotnet "r.classes.still.length")" = 13 ]'
mkcommit sb-bs-plain; printf '%s\n' "$BSF" > "$ST/fails"
rs "$SHA" dotnet
check "the same 13 failures run without --base are SYSTEMIC (nearest non-match: the limits stay for a plain run)" '[ "$(rjson dotnet status)" = SYSTEMIC ]'

# the base must be an ancestor of the sha
rs "$BSB" dotnet --base "$SHA"
check "a --base that is not an ancestor of the sha is a usage error (exit 2)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "ancestor"'

# in_session ignores the variables that configure a CLI
INS() { env -i PATH="$PATH" "$@" node "$PLUGIN/tests/run-set-tools.mjs" env | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).in_session)'; }
check "CODEX_HOME and CLAUDE_CONFIG_DIR alone do not make a run in-session" '[ "$(INS CODEX_HOME=/x CLAUDE_CONFIG_DIR=/y)" = false ]'
check "...another CODEX_ variable does (nearest non-match), and so do CLAUDECODE and HERDR_PANE_ID" '[ "$(INS CODEX_HOME=/x CODEX_FOO=1)" = true ] && [ "$(INS CLAUDECODE=1)" = true ] && [ "$(INS HERDR_PANE_ID=p)" = true ]'

# a base that cannot be run
mkcommit sb-err-base; touch "$ST/dotnet.build-fails"
EBASE=$SHA
mkcommit sb-err-head; : > "$ST/fails"
rs "$SHA" dotnet --base "$EBASE"
check "a base whose run could not complete leaves the verdict incomparable (exit 5), says it could not be run, and the head is still judged on its own: PASS" '[ $RC = 5 ] && [ "$(rjson dotnet status)" = PASS ] && printf %s "$OUT" | grep -q "could not be run"'

# full
mkcommit sb-full-base; : > "$ST/fails"; : > "$ST/shell.fails"
FBASE=$SHA
mkcommit sb-full-head; printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$FBASE"
check "full with --base: a dotnet failure absent at the base is NEW under full, with its set, exit 4" '[ $RC = 4 ] && [ "$(vjson full r.verdict)" = "\"new-failures\"" ] && [ "$(vjson full r.classes.new)" = "[\"dotnet|Ah.Fake.Tests.C.Z\"]" ]'

# a full base that exists only as its components
mkcommit sb-fullc-base; : > "$ST/fails"; : > "$ST/shell.fails"
FCB=$SHA; rs "$FCB" dotnet; rs "$FCB" shell
mkcommit sb-fullc-head; printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"
rs "$SHA" full --base "$FCB"
check "full with --base where the base was run as dotnet and shell apart: its full result is made, the verdict compares (NEW dotnet|Z, exit 4), not incomparable" \
  '[ $RC = 4 ] && [ -f "$AH_RESULTS_DIR/$FCB/full/result.json" ] && [ "$(vjson full r.classes.new)" = "[\"dotnet|Ah.Fake.Tests.C.Z\"]" ]'

check "the verdict is in <sha>/<set>/verdict-<base>.json with the sha, set and base, and result.json carries no verdict fields" \
  '[ "$(vjson full r.sha)" = "\"$SHA\"" ] && [ "$(vjson full r.set)" = "\"full\"" ] && [ "$(vjson full r.base)" = "\"$FCB\"" ] && [ -f "$AH_RESULTS_DIR/$SHA/full/verdict-$FCB.json" ] && ! grep -q "\"verdict\"" "$AH_RESULTS_DIR/$SHA/full/result.json"'
rs "$SHA" full
check "a caller without --base on the same result prints no verdict and reuses it (nearest non-match)" '! printf %s "$OUT" | grep -q "verdict=" && printf %s "$OUT" | grep -q "cached=yes"'
VF1="$AH_RESULTS_DIR/$SHA/full/verdict-$FCB.json"
check "a verdict records the hashes of the head and base results and verdictcheck accepts it while they match" \
  '[ -f "$VF1" ] && node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" | grep -qx ok'
cp "$AH_RESULTS_DIR/$SHA/full/result.json" "$SB/head.keep"; printf '\n' >> "$AH_RESULTS_DIR/$SHA/full/result.json"
check "...a head result.json that changed after the verdict makes it stale (exit 1)" '! node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" >/dev/null'
cp "$SB/head.keep" "$AH_RESULTS_DIR/$SHA/full/result.json"; cp "$AH_RESULTS_DIR/$FCB/full/result.json" "$SB/base.keep"; printf '\n' >> "$AH_RESULTS_DIR/$FCB/full/result.json"
check "...and so does a base result.json that changed" '! node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" >/dev/null'
cp "$SB/base.keep" "$AH_RESULTS_DIR/$FCB/full/result.json"
# a run that replaces the result deletes the verdicts that judged the old one
printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"
rs "$SHA" full --fresh
check "a --fresh run without --base that rewrites the result deletes the set's verdict files (a stale clean verdict cannot outlive it)" '[ ! -e "$VF1" ]'
rs "$SHA" full --base "$FCB"
check "...and running with --base again makes a new verdict that checks out (nearest non-match)" '[ -f "$VF1" ] && node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" | grep -qx ok'

# --force is no caller's option: only the base run that resolve_base starts reruns a stored result
rs "$SHA" shell --force
check "--force is not an option (exit 2)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "unknown option"'

# a base kept from before the fingerprint had a worker count is made again where this run is, whichever components it needs
stripw() { node -e 'const fs=require("fs"),f=process.argv[1],r=JSON.parse(fs.readFileSync(f,"utf8"));delete r.env.workers;fs.writeFileSync(f,JSON.stringify(r))' "$1"; }
hasw() { node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.exit(r.env&&r.env.workers>0?0:1)' "$1"; }
mkcommit fp-old-base; : > "$ST/fails"; : > "$ST/shell.fails"
OLDB=$SHA; rs "$OLDB" dotnet
stripw "$AH_RESULTS_DIR/$OLDB/dotnet/result.json"
mkcommit fp-old-head; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$OLDB"
check "a base whose stored dotnet result has no worker count (its shell result absent) is run again for it: both fingerprints carry workers and the verdict compares (exit 0)" \
  '[ $RC = 0 ] && hasw "$AH_RESULTS_DIR/$OLDB/full/result.json" && hasw "$AH_RESULTS_DIR/$OLDB/dotnet/result.json" && hasw "$AH_RESULTS_DIR/$SHA/full/result.json" && [ "$(vjson full r.verdict)" = "\"clean\"" ]'
stripw "$AH_RESULTS_DIR/$OLDB/dotnet/result.json"; stripw "$AH_RESULTS_DIR/$OLDB/shell/result.json"; stripw "$AH_RESULTS_DIR/$OLDB/full/result.json"
mkcommit fp-old-head2; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$OLDB" --fresh-base
check "--fresh-base with every stored base result lacking workers: all of them are made again and the verdict compares (exit 0)" \
  '[ $RC = 0 ] && hasw "$AH_RESULTS_DIR/$OLDB/full/result.json" && hasw "$AH_RESULTS_DIR/$OLDB/shell/result.json" && [ "$(vjson full r.verdict)" = "\"clean\"" ]'
# a full base whose parts were made under different fingerprints has none of its own
mkcommit fp-mix-base; : > "$ST/fails"; : > "$ST/shell.fails"
MIXB=$SHA; rs "$MIXB" dotnet; rs "$MIXB" shell
stripw "$AH_RESULTS_DIR/$MIXB/shell/result.json"
rs "$MIXB" full
check "a full result merged from a part without workers and parts with them carries no fingerprint" '! hasw "$AH_RESULTS_DIR/$MIXB/full/result.json" && [ "$(node -e "const r=JSON.parse(require(\"fs\").readFileSync(process.argv[1],\"utf8\"));console.log(r.env===undefined)" "$AH_RESULTS_DIR/$MIXB/full/result.json")" = true ]'
DOT0=$(cksum < "$AH_RESULTS_DIR/$MIXB/dotnet/result.json")
mkcommit fp-mix-head; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$MIXB"
check "without --fresh-base such a base is incomparable (exit 5) and the part without workers is not remade" '[ $RC = 5 ] && [ "$(vjson full r.verdict)" = "\"incomparable\"" ] && ! hasw "$AH_RESULTS_DIR/$MIXB/shell/result.json"'
mkcommit fp-mix-head2; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$MIXB" --fresh-base
check "--fresh-base remakes the part without workers only: shell has them now, the dotnet result is the same file, the verdict compares (exit 0)" \
  '[ $RC = 0 ] && hasw "$AH_RESULTS_DIR/$MIXB/shell/result.json" &&hasw "$AH_RESULTS_DIR/$MIXB/full/result.json" && [ "$(cksum < "$AH_RESULTS_DIR/$MIXB/dotnet/result.json")" = "$DOT0" ]'

echo "$PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
