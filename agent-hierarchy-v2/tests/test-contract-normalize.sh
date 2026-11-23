#!/bin/bash
# agent-hierarchy — the contract normalizer (tests/contract-tools/normalize.mjs, rules in tests/contracts/normalize.json):
# every rule's example maps exactly and its counterexample is unchanged; a rule missing either is rejected; an over-broad rule
# is caught by its counterexample; placeholders are numbered by first appearance so a mix-up between two ids is still a
# difference; keyed rules act on parsed JSON and JSONL; both spellings of a root (/tmp and /private/tmp) are one placeholder.
# Usage: bash tests/test-contract-normalize.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
N="$PLUGIN/tests/contract-tools/normalize.mjs"
RULES="$PLUGIN/tests/contracts/normalize.json"
SB="$(mktemp -d "${TMPDIR:-/tmp}/ah-cnorm-test.XXXXXX")"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:300})"; fi; }
nz() { node --input-type=module -e "const M = await import('$N'); const rules = M.loadRules(process.argv[1]); $1" "$RULES" "${@:2}"; }

# ---- C0-1: the committed rule set
OUT=$(node "$N" --self-test "$RULES" 2>&1); RC=$?
check "self-test: every rule's example maps exactly and its counterexample is unchanged" '[ $RC = 0 ] && [ "$OUT" = "normalize self-test: ok" ]'
OUT=$(nz 'console.log(rules.rules.map(r => r.name).join(","))')
check "the initial rule set has the spec table rules, in order, with the four a recorded view case proved necessary (plugin-version, runner-pid, view-oid, view-tkey, view-rev), the runner start a recorded registration proved necessary (runner-pid-start), the version a debug build reports (dev-version), the host harness count of roster teams (live-sessions-harnesses-json), the health body's gc counters (gc-json), then the command-form rules" '[ "$OUT" = "root,plugin,repo-root,plugin-version,dev-version,runner-pid,runner-pid-start,probe-sandbox,iso-utc,iso-local,iso-compact,stale-team-stamp,msg-id,op-id,view-oid,view-tkey,view-rev,tmp-name,pid-json,epoch-json,clock-json,load-sample-json,live-sessions-harnesses-json,gc-json,minute-json,digest-json,prompt-id-json,impl-json,node-warning,age-text,dispatch-age,node-version,uncommitted-count,doctor-repo-state,doctor-repo-detail,registration-pid-start,registration-bound,team-pid-start,cli-form-path-quoted,cli-form-path-bare,cli-form-ahd-path-quoted,cli-form-ahd-path-bare,cli-form-bin,cli-form-ahd-bin,cli-form-name,cli-form-ahd-usage,cli-form-ahd-reason,cli-form-gate-usage,cli-form-ahd-contract,cli-form-intake-usage,cli-form-docs-path,usage-runtime-dir,hook-error-unit,usage-header-time,sha256-json,git-base-json" ]'

# context rules run before the pattern rules: a plugin checked out under a probe sandbox name is still <PLUGIN>
nzs() { node --input-type=module -e "import { loadRules, createNormalizer } from '$N'; const r = loadRules('$RULES'); const n = createNormalizer(r, { T: '/private/tmp/ahprobe.AAAAAA', roots: { home: '/private/tmp/ahprobe.AAAAAA/h' }, plugin: '/private/tmp/ahprobe.BBBBBB/agent-hierarchy' }); process.stdout.write(n.text(process.argv[1], process.argv[2]));" "$1" "$2"; }
OUT=$(nzs stdout 'node "/private/tmp/ahprobe.BBBBBB/agent-hierarchy/hooks/msg.mjs" in /private/tmp/ahprobe.CCCCCC/x')
check "a plugin checked out under a /tmp/ahprobe.* name is <PLUGIN> (context rules run before the sandbox-name pattern); another probe name is <PROBE:1>" '[ "$OUT" = "node \"<PLUGIN>/hooks/msg.mjs\" in /private/tmp/<PROBE:1>/x" ]'
OUT=$(nzs stdout '/private/tmp/ahprobe.BBBBBB/agent-hierarchy-other/x')
check "...and a sibling that only starts with the plugin's name is not <PLUGIN>" '[ "$OUT" = "/private/tmp/<PROBE:1>/agent-hierarchy-other/x" ]'
OUT=$(nzs file '{"type":"cli-root","digest":"daea1956128c538a"}')
check "digest-json: a 16-hex digest value is <DIGEST>" '[ "$OUT" = "{\"type\":\"cli-root\",\"digest\":\"<DIGEST>\"}" ]'
OUT=$(nzs file '{"type":"user-prompt","prompt_id":"pd0086c41e20b"}')
check "prompt-id-json: a p plus 12 hex value under prompt_id is <PROMPTID>" '[ "$OUT" = "{\"type\":\"user-prompt\",\"prompt_id\":\"<PROMPTID>\"}" ]'
OUT=$(nzs file '{"prompt_id":"pd00","id":"pd0086c41e20b"}')
check "...and a short value under prompt_id, and a 13-character value under another key, are kept" '[ "$OUT" = "{\"prompt_id\":\"pd00\",\"id\":\"pd0086c41e20b\"}" ]'
OUT=$(nzs file '{"digest":"daea","hash":"daea1956128c538a"}')
check "...and a short value under digest, and a 16-hex value under another key, are kept" '[ "$OUT" = "{\"digest\":\"daea\",\"hash\":\"daea1956128c538a\"}" ]'

# the runner pid is a whole-token number: a random id that merely contains its digits is never edited
nzp() { node --input-type=module -e "import { loadRules, createNormalizer } from '$N'; const r = loadRules('$RULES'); const n = createNormalizer(r, { pid: Number(process.argv[1]) }); process.stdout.write(n.text(process.argv[2], process.argv[3]));" "$1" "$2" "$3"; }
OUT=$(nzp 74240 file '{"op_id":"128c44e3-6c3a-4e90-b1a7-9a74240bcfa8"}')
check "runner-pid: an op id whose last group contains the pid's digits still reaches the op-id rule whole (<OPID:1>), not a <PID>-edited id" '[ "$OUT" = "{\"op_id\":\"<OPID:1>\"}" ]'
OUT=$(nzp 7421 file '{"op_id":"128c44e3-6c3a-4e90-7421-bcfa8d43071d"}')
check "runner-pid: a UUID with a group equal to the pid is kept whole" '[ "$OUT" = "{\"op_id\":\"128c44e3-6c3a-4e90-7421-bcfa8d43071d\"}" ]'
OUT=$(nzp 7421 stdout 'pid 7421 op 128c44e3-6c3a-4e90-7421-bcfa8d43071d and /o/ab12.7421/t')
check "...and the real pid beside it is still <PID>, as a word and as a path part" '[ "$OUT" = "pid <PID> op 128c44e3-6c3a-4e90-7421-bcfa8d43071d and /o/ab12.<PID>/t" ]'
OUT=$(nzp 7421 stdout '128c44e3-6c3a-4e90-7421-bcfa8d43071d-7421')
check "...a pid directly after a UUID (not part of it) is still <PID>" '[ "$OUT" = "128c44e3-6c3a-4e90-7421-bcfa8d43071d-<PID>" ]'
OUT=$(nzp 7421 stdout '128c44e3-6c3a-4e90-7421-bcfa8d43071')
check "...an id one hex digit short of a UUID is not protected" '[ "$OUT" = "128c44e3-6c3a-4e90-<PID>-bcfa8d43071" ]'

# the doctor marketplace row: status and detail are masked as a consistent pair only; an inconsistent pair stays visible
pair() { nzs stdout "$(printf '"name": "marketplace",\n      "status": "%s",\n      "detail": "%s"' "$1" "$2")"; }
OUT=$(pair warn '<REPO> has <N> uncommitted change(s) — x')
check "doctor row: warn with 'has <N> uncommitted change(s)' is masked as the pair" 'echo "$OUT" | grep -q "<REPO-STATE>" && echo "$OUT" | grep -q "<REPO-DETAIL>"'
OUT=$(pair ok '<REPO> is clean')
check "doctor row: ok with 'is clean' is masked as the pair" 'echo "$OUT" | grep -q "<REPO-STATE>" && echo "$OUT" | grep -q "<REPO-DETAIL>"'
OUT=$(pair ok 'own root is not inside a git checkout — nothing to be dirty')
check "doctor row: ok with 'not inside a git checkout' is masked as the pair" 'echo "$OUT" | grep -q "<REPO-STATE>" && echo "$OUT" | grep -q "<REPO-DETAIL>"'
OUT=$(pair ok '<REPO> has <N> uncommitted change(s) — x')
check "doctor row: an inconsistent pair (ok with uncommitted changes) is NOT masked, so it fails against a recording" '! echo "$OUT" | grep -q "<REPO-STATE>" && ! echo "$OUT" | grep -q "<REPO-DETAIL>"'
OUT=$(pair warn '<REPO> is clean')
check "doctor row: the other inconsistent pair (warn with clean) is NOT masked either" '! echo "$OUT" | grep -q "<REPO-STATE>" && ! echo "$OUT" | grep -q "<REPO-DETAIL>"'

# the health `impl` is the runtime under test: <IMPL> when it is, left visible (and so a failure against a recording) when it is not
implr() { node --input-type=module -e "import { loadRules, createNormalizer } from '$N'; const r = loadRules('$RULES'); const n = createNormalizer(r, { impl: process.argv[1] }); process.stdout.write(JSON.stringify(n.json(JSON.parse(process.argv[2]))));" "$1" "$2"; }
OUT=$(implr native '{"impl":"native","protocol":1}')
check "impl-json: a health impl equal to the impl under test (native) is <IMPL>" '[ "$OUT" = "{\"impl\":\"<IMPL>\",\"protocol\":1}" ]'
OUT=$(implr node '{"impl":"node","protocol":1}')
check "impl-json: ...and so is node under node, which is how the Node recording and a native run agree" '[ "$OUT" = "{\"impl\":\"<IMPL>\",\"protocol\":1}" ]'
OUT=$(implr native '{"impl":"node","protocol":1}')
check "impl-json: a WRONG impl (a native run answering node) is not masked, so it fails against the recorded <IMPL>" '[ "$OUT" = "{\"impl\":\"node\",\"protocol\":1}" ]'
OUT=$(implr node '{"implementation":"node"}')
check "impl-json: another key holding the same word is untouched" '[ "$OUT" = "{\"implementation\":\"node\"}" ]'

# a rule without a counterexample is rejected, and so is one without an example
node -e 'const d = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); delete d.rules[2].counterexample; require("fs").writeFileSync(process.argv[2], JSON.stringify(d))' "$RULES" "$SB/nocounter.json"
OUT=$(node "$N" --self-test "$SB/nocounter.json" 2>&1); RC=$?
check "a rule without a counterexample is rejected" '[ $RC != 0 ] && echo "$OUT" | grep -q "missing counterexample"'
node -e 'const d = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); delete d.rules[3].example; require("fs").writeFileSync(process.argv[2], JSON.stringify(d))' "$RULES" "$SB/noexample.json"
OUT=$(node "$N" --self-test "$SB/noexample.json" 2>&1); RC=$?
check "a rule without an example is rejected" '[ $RC != 0 ] && echo "$OUT" | grep -q "missing example"'

# the mutant: an over-broad iso-utc pattern also erases a bare date; its counterexample catches it
node -e 'const d = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); d.rules.find((r) => r.name === "iso-utc").pattern = "\\d{4}-\\d\\d-\\d\\d(T\\d\\d:\\d\\d:\\d\\d(\\.\\d{1,3})?Z)?"; require("fs").writeFileSync(process.argv[2], JSON.stringify(d))' "$RULES" "$SB/broad.json"
OUT=$(node "$N" --self-test "$SB/broad.json" 2>&1); RC=$?
check "an over-broad iso pattern that erases a bare date fails its counterexample" '[ $RC = 1 ] && echo "$OUT" | grep -q "iso-utc: counterexample"'
node -e 'const d = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); d.rules.find((r) => r.name === "msg-id").pattern = "\\d{8}-\\d{6}-\\w+"; require("fs").writeFileSync(process.argv[2], JSON.stringify(d))' "$RULES" "$SB/broadid.json"
OUT=$(node "$N" --self-test "$SB/broadid.json" 2>&1); RC=$?
check "an over-broad msg-id pattern (\\w+ tail) fails its counterexample" '[ $RC = 1 ] && echo "$OUT" | grep -q "msg-id: counterexample"'

# the registration start time: dropped only from a registration file, and only when its value has the ps lstart shape
drop() { node --input-type=module -e "import { loadRules, createNormalizer } from '$N'; const r = loadRules('$RULES'); const n = createNormalizer(r, {}); process.stdout.write(n.text('file', process.argv[1], { path: process.argv[2] }));" "$1" "$2"; }
OUT=$(drop '{"v":1,"pid_start":"Mon Oct 12 00:39:19 2026","at":"x"}' ah/registrations/s-1.json)
check "registration-pid-start: the key is dropped from a registration file when its value is a ps start time" '[ "$OUT" = "{\"v\":1,\"at\":\"x\"}" ]'
OUT=$(drop '{"v":1,"pid_start":"Mon Oct  2 00:39:19 2026"}' ah/registrations/s-1.json)
check "...including a single-digit day, padded as ps prints it" '[ "$OUT" = "{\"v\":1}" ]'
OUT=$(drop '{"v":1,"pid_start":"Mon Oct 12 00:39:19 2026"}' proj/.claude/ah2/peers.jsonl)
check "...and kept in any other file" '[ "$OUT" = "{\"v\":1,\"pid_start\":\"Mon Oct 12 00:39:19 2026\"}" ]'
OUT=$(drop '{"v":1,"pid_start":"yesterday"}' ah/registrations/s-1.json)
check "...and kept when the value is not a ps start time" '[ "$OUT" = "{\"v\":1,\"pid_start\":\"yesterday\"}" ]'
OUT=$(drop '{"v":1,"pid_start":"Mon Oct 12 00:39:19 2026 and more"}' ah/registrations/s-1.json)
check "...and kept when the value only starts like one" '[ "$OUT" = "{\"v\":1,\"pid_start\":\"Mon Oct 12 00:39:19 2026 and more\"}" ]'
OUT=$(drop '{"v":1,"nested":{"pid_start":"Mon Oct 12 00:39:19 2026"}}' ah/registrations/s-1.json)
check "...a nested key of the same name is dropped too (it is the same record shape)" '[ "$OUT" = "{\"v\":1,\"nested\":{}}" ]'
OUT=$(drop '{"v":1,"pid_start":"Mon Oct 12 00:39:19 2026"}' ah/registrations/sub/s-1.json)
check "...but a file in a folder below registrations is not in scope (the glob is one level)" '[ "$OUT" = "{\"v\":1,\"pid_start\":\"Mon Oct 12 00:39:19 2026\"}" ]'

# the registration binding flag: native writes it, Node never did; dropped only from a registration file and only as a boolean
OUT=$(drop '{"v":1,"bound":true,"at":"x"}' ah/registrations/s-1.json)
check "registration-bound: the key is dropped from a registration file when its value is a boolean" '[ "$OUT" = "{\"v\":1,\"at\":\"x\"}" ]'
OUT=$(drop '{"v":1,"bound":false}' ah/registrations/s-1.json)
check "...false as well" '[ "$OUT" = "{\"v\":1}" ]'
OUT=$(drop '{"v":1,"bound":true}' proj/.claude/ah2/peers.jsonl)
check "...and kept in any other file" '[ "$OUT" = "{\"v\":1,\"bound\":true}" ]'
OUT=$(drop '{"v":1,"bound":"trueish"}' ah/registrations/s-1.json)
check "...the value shape is anchored: a string that only starts like a boolean is kept" '[ "$OUT" = "{\"v\":1,\"bound\":\"trueish\"}" ]'
OUT=$(drop '{"v":1,"bound":"xfalse"}' ah/registrations/s-1.json)
check "...and so is one that only ends like one" '[ "$OUT" = "{\"v\":1,\"bound\":\"xfalse\"}" ]'
OUT=$(drop '{"v":1,"bound":"maybe"}' ah/registrations/s-1.json)
check "...kept when the value is not of the boolean shape" '[ "$OUT" = "{\"v\":1,\"bound\":\"maybe\"}" ]'
OUT=$(drop '{"v":1,"bound":1}' ah/registrations/s-1.json)
check "...kept when the value is a number" '[ "$OUT" = "{\"v\":1,\"bound\":1}" ]'

# a drop rule must be explicit, scoped and shaped; a plain keyed rule cannot drop
dropbad() { node -e 'const d = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); const r = d.rules.find((x) => x.name === "registration-pid-start"); eval(process.argv[3]); require("fs").writeFileSync(process.argv[2], JSON.stringify(d))' "$RULES" "$SB/$1.json" "$2"; node "$N" --self-test "$SB/$1.json" 2>&1; }
OUT=$(dropbad nopaths 'delete r.paths'); check "a drop rule without paths is refused" 'echo "$OUT" | grep -q "needs paths"'
OUT=$(dropbad noshape 'delete r.value_pattern'); check "a drop rule without a value shape is refused" 'echo "$OUT" | grep -q "needs a value_pattern"'
OUT=$(dropbad noaction 'delete r.action'); check "a keyed rule with no replace and no drop action is refused (a plain keyed rule cannot drop)" 'echo "$OUT" | grep -q "missing replace"'
OUT=$(dropbad badaction 'r.action = "erase"'); check "an unknown action is refused" 'echo "$OUT" | grep -q "unknown action"'

# ---- C0-2: placeholders are numbered by first appearance within one case
OUT=$(nz 'const n = M.createNormalizer(rules, {}); console.log(n.text("stdout", "a 20261011-120941-czme b 20261011-111019-mcki c 20261011-120941-czme"))')
check "two different message ids become <MSGID:1> and <MSGID:2>, and a repeated one keeps its number" '[ "$OUT" = "a <MSGID:1> b <MSGID:2> c <MSGID:1>" ]'
OUT=$(nz 'const n = M.createNormalizer(rules, {}); const a = n.text("stdout", "20261011-120941-czme 20261011-111019-mcki 20261011-120941-czme"); const m = M.createNormalizer(rules, {}); const b = m.text("stdout", "20261011-120941-czme 20261011-111019-mcki 20261011-111019-mcki"); console.log(a === b ? "same" : "differ")')
check "a mix-up of references (X Y X against X Y Y) is a difference after normalization" '[ "$OUT" = differ ]'
OUT=$(nz 'const n = M.createNormalizer(rules, {}); const a = n.text("stdout", "20261011-120941-czme 20261011-111019-mcki"); const m = M.createNormalizer(rules, {}); const b = m.text("stdout", "20261011-120941-czme 20261011-120941-czme"); console.log(a === b ? "same" : "differ")')
check "two different ids against one id are a difference (not every id mapped to one placeholder)" '[ "$OUT" = differ ]'
OUT=$(nz 'const n = M.createNormalizer(rules, {}); n.text("stdout", "20261011-120941-czme"); console.log(n.text("stderr", "x 20261011-111019-mcki y 20261011-120941-czme"))')
check "numbering is shared across the streams of one case (stderr continues stdout)" '[ "$OUT" = "x <MSGID:2> y <MSGID:1>" ]'

# ---- roots: both spellings are one placeholder; a sibling name is not eaten
OUT=$(nz 'const ctx = {T: "/private/tmp/ahprobe.q1w2e3", roots: {home: "/private/tmp/ahprobe.q1w2e3/home", ah: "/private/tmp/ahprobe.q1w2e3/ah", proj: "/private/tmp/ahprobe.q1w2e3/proj"}}; const n = M.createNormalizer(rules, ctx); console.log(n.text("stdout", "/tmp/ahprobe.q1w2e3/home/a /private/tmp/ahprobe.q1w2e3/home/b /private/tmp/ahprobe.q1w2e3/homework/c"))')
check "/tmp and /private/tmp spellings of a root are one placeholder; <T> covers a sibling name" '[ "$OUT" = "<ROOT:home>/a <ROOT:home>/b <T>/homework/c" ]'

# ---- keyed rules on JSON, JSONL and text
OUT=$(nz 'const n = M.createNormalizer(rules, {}); console.log(n.text("stdout", JSON.stringify({pid: 7, ppid: 1, ts: 1791733607123, age_sec: 3, count: 9, nested: {pid: 8}}, null, 2) + "\n"))')
check "keyed rules act on parsed JSON, nested keys included, and keep the indent and the trailing newline" '[ "$OUT" = "$(printf "{\n  \"pid\": \"<PID>\",\n  \"ppid\": \"<PID>\",\n  \"ts\": \"<EPOCHMS>\",\n  \"age_sec\": \"<CLOCK>\",\n  \"count\": 9,\n  \"nested\": {\n    \"pid\": \"<PID>\"\n  }\n}")" ]'
OUT=$(nz 'const n = M.createNormalizer(rules, {}); console.log(JSON.stringify(n.text("file", "{\"pid\":1}\n{\"pid\":2,\"at\":1791733607123}\n")))')
check "JSONL: every line is normalized and the line structure is kept" '[ "$OUT" = "\"{\\\"pid\\\":\\\"<PID>\\\"}\\n{\\\"pid\\\":\\\"<PID>\\\",\\\"at\\\":\\\"<EPOCHMS>\\\"}\\n\"" ]'
OUT=$(nz 'const n = M.createNormalizer(rules, {}); console.log(n.text("stdout", "pid: 12 (\"pid\": 77) and \"age_sec\": 5"))')
check "text that is not JSON: the \`\"key\": value\` form is normalized, a bare word is not" '[ "$OUT" = "pid: 12 (\"pid\": \"<PID>\") and \"age_sec\": \"<CLOCK>\"" ]'
OUT=$(nz 'const n = M.createNormalizer(rules, {}); console.log(JSON.stringify(n.json({pid: 5, items: [{ts: 1791733607123, id: "20261011-120941-czme"}], at: "2026-10-11T12:00:00Z"})))')
check "n.json(): the same rules on a parsed value (wire bodies)" '[ "$OUT" = "{\"pid\":\"<PID>\",\"items\":[{\"ts\":\"<EPOCHMS>\",\"id\":\"<MSGID:1>\"}],\"at\":\"<ISO:1>\"}" ]'
OUT=$(nz 'const n = M.createNormalizer(rules, {}); console.log(JSON.stringify(n.text("stderr", "(node:4242) ExperimentalWarning: x\n(Use `node --trace-warnings ...` to show where)\nkept\n(node:1) Other\nkept 2\n")))')
check "node-warning removes the warning line and its trace hint, and only those" '[ "$OUT" = "\"kept\\nkept 2\\n\"" ]'

# the native command form (0114 §4c: flag day, `<root>/bin/ah roster|msg|gate`, `<root>/bin/ahd`, `ah roster ...`) maps back to the form
# the Node recordings hold, quoting kept; a Node-form text is a fixed point
cf() { nzs stdout "$1"; }
OUT=$(cf 'a `<PLUGIN>/bin/ah roster deliver x`, "<PLUGIN>/bin/ah" msg new; <PLUGIN>/bin/ahd status; see ah msg link and bin/ah roster')
check "cli-form: the native spellings map to the Node recording's, quoted or not" '[ "$OUT" = "a \`node <PLUGIN>/hooks/roster.mjs deliver x\`, node \"<PLUGIN>/hooks/msg.mjs\" new; node <PLUGIN>/hooks/ahd-ctl.mjs status; see msg.mjs link and hooks/roster.mjs" ]'
OUT=$(cf 'node "<PLUGIN>/hooks/msg.mjs" new; node <PLUGIN>/hooks/roster.mjs teams; roster.mjs show')
check "...and a Node-form text is unchanged (the rules are idempotent on the recordings)" '[ "$OUT" = "node \"<PLUGIN>/hooks/msg.mjs\" new; node <PLUGIN>/hooks/roster.mjs teams; roster.mjs show" ]'
OUT=$(cf 'ah: roster rows; <PLUGIN>/bin/ah hook x; <PLUGIN>/bin/ahd.pid; ah messages; the ahd is up')
check "...a neighbour that is not the command (ah: roster, ah hook, ahd.pid, ah messages, prose ahd) is kept" '[ "$OUT" = "ah: roster rows; <PLUGIN>/bin/ah hook x; <PLUGIN>/bin/ahd.pid; ah messages; the ahd is up" ]'

# the command-form family rewrites output only: never a `path` kind, so no file name is edited (a path rule would hide a renamed file)
OUT=$(nz 'console.log(rules.rules.filter(r => r.name.startsWith("cli-form-") && r.applies_to.some(k => !["stdout","stderr","file"].includes(k))).map(r => r.name).join(","))')
check "cli-form rules apply to stdout, stderr and file only (no path kind)" '[ -z "$OUT" ]'
OUT=$(cf '"/x y/bin/ah" msg new; /h/été/bin/ah gate status; ah rosteré')
check "cli-form: a quoted path with a space, a non-ASCII path and a non-ASCII neighbour map like the C# twin's rows" '[ "$OUT" = "node \"/x y/hooks/msg.mjs\" new; node /h/été/hooks/gate.mjs status; roster.mjsé" ]'

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
