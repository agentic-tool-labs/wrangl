#!/bin/bash
# agent-hierarchy — the recorded contracts (K1 wire, K3 hooks, K4 CLI, K5 lifecycle) against the native binary, run
# through the one contract runner (C0-4), and the completeness rows of K3 and K4: every hooks.json row and every verb has a case,
# verbs.json lists exactly the verbs each CLI's help names, and coverage.md is current.
# Usage: bash tests/test-contracts.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS="$PLUGIN/tests/contract-tools"
C="$PLUGIN/tests/contracts"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:600})"; fi; }

# ---- C0-4: the whole recorded set is green against the native binary. It runs as CONTRACT_SHARDS suites of its own
# (tests/test-contracts-s<K>.sh, the body in tests/lib-contracts.sh); this suite checks the slicing and the checks that need no run.
. "$PLUGIN/tests/lib-contracts.sh"
ALL=$(node "$TOOLS/run.mjs" --impl native --list 2>&1); RC=$?
check "run.mjs --list prints the set without running it, and there is a real set behind it: at least 200 cases" '[ $RC = 0 ] && [ "$(printf "%s\n" "$ALL" | grep -c .)" -ge 200 ]'
UNION=""; SUM=0
for ((K = 0; K < CONTRACT_SHARDS; K++)); do
  PART=$(node "$TOOLS/run.mjs" --impl native --list --shard "$K/$CONTRACT_SHARDS" 2>&1)
  UNION="$UNION$PART"$'\n'; SUM=$((SUM + $(printf '%s\n' "$PART" | grep -c .)))
done
OUT="union $(printf '%s' "$UNION" | grep -c .) of $(printf '%s\n' "$ALL" | grep -c .)"
check "the shards together are the set: their cases sorted equal the list sorted, and none is in two shards" \
  '[ "$(printf "%s" "$UNION" | grep . | sort)" = "$(printf "%s\n" "$ALL" | sort)" ] && [ "$SUM" = "$(printf "%s\n" "$ALL" | grep -c .)" ]'
OUT=$(node "$TOOLS/run.mjs" --impl native --list --shard 0/1 2>&1)
check "one shard of one is the whole set (nearest non-match)" '[ "$OUT" = "$ALL" ]'
OUT=$(node "$TOOLS/run.mjs" --impl native --list --shard "$CONTRACT_SHARDS/$CONTRACT_SHARDS" 2>&1); RC=$?
check "a shard number out of range is a usage error, exit 2" '[ $RC = 2 ]'
OUT=$(node "$TOOLS/run.mjs" --impl native --list --shard x 2>&1); RC=$?
check "a shard that is not K/N is a usage error, exit 2" '[ $RC = 2 ]'
MISSING=""; for ((K = 0; K < CONTRACT_SHARDS; K++)); do [ -f "$PLUGIN/tests/test-contracts-s$K.sh" ] || MISSING="$MISSING s$K"; done
OUT="missing:$MISSING; extra: $(ls "$PLUGIN/tests"/test-contracts-s*.sh | wc -l | tr -d ' ') files"
check "there is one suite file per shard, s0 to s$((CONTRACT_SHARDS - 1)), and no other" '[ -z "$MISSING" ] && [ "$(ls "$PLUGIN/tests"/test-contracts-s*.sh | wc -l | tr -d " ")" = "$CONTRACT_SHARDS" ]'
OUT=$(env -u AHD_NATIVE_BIN -u AH_BIN node "$TOOLS/run.mjs" --impl native --shard "0/$CONTRACT_SHARDS" 2>&1); RC=$?
OUT=$(printf '%s\n' "$OUT" | grep -E '^(FAIL|contracts:)' | head -8)
check "with no native binary every case of a shard is skipped as no-native-binary, and the run fails (nothing was compared)" '[ $RC = 1 ] && echo "$OUT" | grep -qE "^contracts: 0/0 \(native\), skipped: [0-9]+ \(no-native-binary( [0-9]+)?"'
OUT=$(node "$TOOLS/run.mjs" --impl node 2>&1); RC=$?
check "--impl node no longer exists: a usage error, exit 2" '[ $RC = 2 ]'
OUT=$(node "$TOOLS/run.mjs" --impl manifest 2>&1); RC=$?
check "--impl manifest no longer exists: a usage error, exit 2" '[ $RC = 2 ]'

# ---- the shard is the gate on the binary: what the separate native lanes used to check. One case a silent stub passes, so only the
# shard's own checks can turn it red.
ONE='hook/activity/h4-plain-posttooluse-silent'
TAG=$(basename "$SB")
shard() { # <bin> <only>: shard 0 narrowed to one case, against that binary
  OUT=$(env -u AH_TEST_FAIL_DIR AHD_NATIVE_BIN="$1" AH_BIN="$1" AH_CONTRACTS_ONLY="$2" bash -c '. "$0/tests/lib-pid.sh"; . "$0/tests/lib-contracts.sh"; contracts_shard_main 0' "$PLUGIN" 2>&1)
}
mkdir -p "$SB/silent" "$SB/leak" "$SB/leakbin"
printf '#!/bin/sh\nexit 0\n' > "$SB/silent/ahd"
printf '#!/bin/bash\n( exec -a "$AH_HOME/leak-%s" sleep 30 ) > /dev/null 2>&1 &\nexit 0\n' "$TAG" > "$SB/leak/ahd"
printf '#!/bin/bash\n( exec -a "$0-other-%s" sleep 30 ) > /dev/null 2>&1 &\nexit 0\n' "$TAG" > "$SB/leakbin/ahd"
chmod +x "$SB/silent/ahd" "$SB/leak/ahd" "$SB/leakbin/ahd"
for d in silent leak leakbin; do : > "$SB/$d/ahd-tui"; chmod +x "$SB/$d/ahd-tui"; done
shard "$SB/silent/ahd" "$ONE"
check "a shard that leaves nothing behind passes the leak check" 'echo "$OUT" | grep -q "^PASS: ...and it left nothing running under its sandbox"'
check "...and a shard that does not hold the required case has no check for it (nearest non-match)" '! echo "$OUT" | grep -q "the case the gate requires"'
shard "$SB/leak/ahd" "$ONE"
check "a shard that leaves a process under its sandbox is red, and the process is killed" 'echo "$OUT" | grep -Eq "^FAIL: ...and it left nothing running under its sandbox \(left [1-9]"'
sleep 1
check "...nothing it left is still alive" '[ -z "$(pgrep -f "leak-$TAG")" ]'
shard "$SB/leakbin/ahd" "$ONE"
sleep 1
check "a process that names only the binary, not the shard's sandbox, is not swept: it may be another run's (nearest non-match)" '[ -n "$(pgrep -f "other-$TAG")" ] && echo "$OUT" | grep -q "^PASS: ...and it left nothing running"'
for p in $(pgrep -f "other-$TAG"); do ah_signal_ours KILL "$p" "other-$TAG"; done
AH_TEST_FAIL_DIR="$SB/stray" shard "$SB/silent/ahd" 'lifecycle/13-trust-verbs-pty'
check "the shard that holds the required case is red when it did not pass there" 'echo "$OUT" | grep -q "^FAIL: ...and the case the gate requires, lifecycle/13-trust-verbs-pty"'
check "...and this failure on purpose keeps no diff where the run keeps real ones" '[ ! -e "$SB/stray" ]'

# ---- K3: every hooks.json row has a case
OUT=$(node "$TOOLS/coverage.mjs" --out "$SB/coverage.md" 2>&1)
check "K3: every row of hooks/hooks.json has at least one case" 'grep -q "^Every row has at least one case\." "$SB/coverage.md"'
check "K4: every verb of verbs.json has at least one case" 'grep -q "^Every verb has at least one case\." "$SB/coverage.md"'
OUT=$(node "$TOOLS/coverage.mjs" --check 2>&1); RC=$?
check "coverage.md is generated and current" '[ $RC = 0 ]'
OUT=$(node -e '
const fs=require("fs"), path=require("path");
const C=process.argv[1];
const rows=[]; const doc=JSON.parse(fs.readFileSync(path.join(C,"..","..","hooks","registry.json"),"utf8"));
for (const [ev,groups] of Object.entries(doc.hooks||doc)) for (const g of groups) for (const h of g.hooks||[]) rows.push(ev+" "+(h.command.match(/ah-hook" ([a-z0-9-]+)$/)||[])[1]);
console.log(rows.length)' "$C")
# 46: the rows the cases were written against, the PreToolUse gates being one chain row; the count is not itemised.
check "hooks.json has the 46 rows the cases were written against (a row added later fails the coverage row above)" '[ "$OUT" = 46 ]'

# ---- K4: verbs.json against each CLI's help and dispatch
cat >"$SB/verbs.mjs" <<EOF
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { join } from "node:path";
const P = "$PLUGIN";
const verbs = JSON.parse(readFileSync(join(P, "tests/contracts/cli/verbs.json"), "utf8"));
const env = { PATH: process.env.PATH, HOME: "$SB", AH_HOME: "$SB/ah", TMPDIR: "$SB", AH_DAEMON: "off", AH_TEST: "1" };
const shim = (cli, args) => (cli === "ahd-ctl" ? [join(P, "bin", "ahd"), ...args] : [join(P, "bin", "ah"), cli, ...args]);
const run = (cli, args) => spawnSync("sh", shim(cli, args), { env: { ...env, AH_BIN: process.env.AHD_NATIVE_BIN }, encoding: "utf8", cwd: "$SB" });
const have = (cli) => new Set(verbs.filter((v) => v.cli === cli).map((v) => v.verb));
const bad = [];
// the first word after the script name on each usage line, and "a | b | c" alternatives on ahd-ctl's
function helpVerbs(cli) {
  const out = run(cli, ["--help"]).stdout;
  const s = new Set();
  for (const line of out.split("\\n")) {
    const m = new RegExp("^\\\\s+" + cli + "\\\\.mjs\\\\s+(.+)$").exec(line);
    if (!m) continue;
    const rest = m[1].replace(/\\(.*$/, "").split("--")[0];
    // ahd-ctl's usage lines list alternatives ("start | stop | restart"); the others name one verb per line
    const alts = cli === "ahd-ctl" ? rest.split(/\\s*\\|\\s*/) : [rest];
    for (const alt of alts) { const w = alt.trim().split(/\\s+/)[0]; if (/^[a-z][a-z-]*\$/.test(w)) s.add(w); }
  }
  return s;
}
for (const cli of ["roster", "msg", "ahd-ctl"]) {
  const h = helpVerbs(cli), mine = have(cli);
  for (const w of h) if (!mine.has(w)) bad.push(cli + ": help names '" + w + "' but verbs.json lacks it");
}
// the dispatch: an unknown command prints the CLI's own verb list; every word of it is in verbs.json
for (const [cli, re] of [["roster", /usage: ah roster ([a-z|-]+)/], ["msg", /usage: ah msg ([a-z|-]+)/]]) {
  const r = run(cli, ["zz-unknown-verb"]);
  const m = re.exec(r.stderr);
  if (!m) { bad.push(cli + ": no usage line for an unknown verb"); continue; }
  // a verb that exists natively only has no Node recording and is not in verbs.json (see NATIVE_ONLY_VERBS in coverage.mjs)
  for (const w of m[1].split("|")) if (!have(cli).has(w) && !(cli === "msg" && ["status", "decline", "withdraw", "forward", "close", "next", "release", "due", "history"].includes(w)) && !(cli === "roster" && ["wake", "overdue", "stall-diagnose", "checklist", "team"].includes(w))) bad.push(cli + ": dispatch lists '" + w + "' but verbs.json lacks it");
}
// every verb of verbs.json is accepted by its CLI: it is not answered as an unknown command
for (const v of verbs) {
  const r = run(v.cli, [v.verb, "--help-not-a-flag-zz"]);
  const said = r.stderr + r.stdout;
  // roster answers an unknown verb with its verb list alone; msg and ahd name the verb
  const unknown = new RegExp("unknown (command|verb) '?\\"?" + v.verb + "'?\\"?").test(said) || (v.cli === "roster" && /^ah roster: usage: ah roster [a-z|-]+\\s*$/m.test(said));
  if (unknown) bad.push(v.cli + " " + v.verb + ": the CLI answers it as an unknown verb");
}
console.log(bad.length ? bad.join("\\n") : "ok " + verbs.length);
EOF
OUT=$(node "$SB/verbs.mjs" 2>&1)
check "K4: verbs.json lists every verb the help and the dispatch name, and the CLI accepts every verb it lists" 'echo "$OUT" | grep -q "^ok "'
# ---- the recorded cases carry what a contract case must
OUT=$(node -e '
const fs=require("fs"), p=require("path");
const walk=d=>fs.existsSync(d)?fs.readdirSync(d).flatMap(n=>{const f=p.join(d,n);return fs.statSync(f).isDirectory()?walk(f):[f]}):[];
const bad=[]; let n=0;
for (const d of ["hooks","cli/cases","wire/cases"]) for (const f of walk(p.join(process.argv[1],d))) {
  if (!f.endsWith(".json")) continue; n++;
  const c=JSON.parse(fs.readFileSync(f,"utf8"));
  if (c.id !== p.basename(f,".json")) bad.push(f+": id");
  if (!["cli","hook","wire","state-op","lifecycle"].includes(c.kind)) bad.push(f+": kind");
  if (!c.about) bad.push(f+": about");
  if (c.mutant === undefined) bad.push(f+": mutant key missing");
  if (!c.expect && !c.pending) bad.push(f+": no expect and not pending");
  // A route or verb the Node implementation never had is recorded from the native one, and says why in `note`.
  if (c.expect && !(c.recorded && c.recorded.plugin_version && (c.recorded.impl === "node" || (c.recorded.impl === "native" && c.recorded.note)))) bad.push(f+": recorded metadata");
}
console.log(bad.length ? bad.slice(0,8).join("\n") : "ok "+n)' "$C")
check "every case file has its id equal to its file name, a kind, an about, a mutant key, and either a recorded expect or a pending marker" 'echo "$OUT" | grep -q "^ok "'

# A failing shard's diffs outlive its sandbox, within a bound
KD="$SB/keep"; mkdir -p "$KD/from/lifecycle"
for i in $(seq 10 34); do echo "diff $i" > "$KD/from/lifecycle/$i.diff"; done
head -c 70000 /dev/zero > "$KD/from/lifecycle/10.diff"; echo other > "$KD/from/lifecycle/not-a-diff.txt"
AH_TEST_FAIL_DIR="$KD/to" contracts_keep_diffs "$KD/from"; RC=$?
check "contracts_keep_diffs keeps the first 20 diffs, by name" '[ $RC = 0 ] && [ "$(ls "$KD/to/lifecycle" | wc -l | tr -d " ")" = 20 ] && [ -f "$KD/to/lifecycle/29.diff" ] && [ ! -e "$KD/to/lifecycle/30.diff" ]'
check "...each cut to 64 KiB" '[ "$(wc -c < "$KD/to/lifecycle/10.diff" | tr -d " ")" = 65536 ]'
check "...and only diffs (nearest non-match)" '[ ! -e "$KD/to/lifecycle/not-a-diff.txt" ]'
env -u AH_TEST_FAIL_DIR bash -c '. "$0"; contracts_keep_diffs "$1"; echo "rc=$?"' "$PLUGIN/tests/lib-contracts.sh" "$KD/from" > "$KD/none.out" 2>&1
check "...with no directory named it keeps nothing and is no failure" 'grep -qx "rc=0" "$KD/none.out"'
AH_TEST_FAIL_DIR="$KD/to2" contracts_keep_diffs "$KD/absent"; RC=$?
check "...a shard with no failure leaves no directory" '[ $RC = 0 ] && [ ! -e "$KD/to2" ]'

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
