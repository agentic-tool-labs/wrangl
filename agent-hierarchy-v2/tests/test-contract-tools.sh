#!/bin/bash
# agent-hierarchy — the contract tooling itself (tests/contract-tools/): the tree diff ignores logs and locks but reports a
# changed team file (C0-6); the runner refuses a sandbox that is
# not ours and removes nothing (C0-16); no tooling file imports from hooks/ (C0-14); routes.json is complete (C0-3); a recorded
# case with a changed byte fails with a diff (the nearest non-match of every recorded row).
# Usage: bash tests/test-contract-tools.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:500})"; fi; }
TOOLS="$PLUGIN/tests/contract-tools"

# ---- C0-14: the tooling does not import from hooks/
OUT=$(grep -rnE "(import|require)[^;]*['\"][^'\"]*hooks/|from ['\"][^'\"]*\.\./hooks|import\([^)]*hooks/" "$PLUGIN/tests/contracts" "$PLUGIN/tests/contract-tools" "$PLUGIN/tests/parity" --include='*.mjs' --include='*.js' 2>/dev/null | head -5)
check "C0-14: no file under tests/contracts, tests/contract-tools or tests/parity imports from hooks/" '[ -z "$OUT" ]'
# the nearest non-match: the rule sees a planted import
mkdir -p "$SB/plant"
printf 'import { x } from "../../hooks/lib-hier.mjs";\n' >"$SB/plant/bad.mjs"
OUT=$(grep -rnE "(import|require)[^;]*['\"][^'\"]*hooks/|from ['\"][^'\"]*\.\./hooks|import\([^)]*hooks/" "$SB/plant" --include='*.mjs' | head -1)
check "...and the same rule finds a planted import (the check can fail)" '[ -n "$OUT" ]'

# ---- C0-6: the tree diff
cat >"$SB/td.mjs" <<EOF
import { snapshot, treeDiff, rules } from "$TOOLS/lib-case.mjs";
import { createNormalizer } from "$TOOLS/normalize.mjs";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
const root = process.argv[2];
const roots = { home: join(root, "home"), ah: join(root, "ah"), proj: join(root, "proj") };
for (const r of Object.values(roots)) mkdirSync(r, { recursive: true });
mkdirSync(join(roots.proj, ".claude/ah2/teams"), { recursive: true });
writeFileSync(join(roots.proj, ".claude/ah2/teams/a.json"), '{"team":"a","pid":1}\n');
const ignore = rules().ignore_paths;
const before = snapshot(roots, ignore);
// changes: a log, a lock, a stale aside, a tmp that vanishes, a changed team file, a new file
mkdirSync(join(roots.ah, "logs"), { recursive: true });
writeFileSync(join(roots.ah, "logs/ahd.log"), "line\n");
mkdirSync(join(roots.ah, "run"), { recursive: true });
writeFileSync(join(roots.ah, "run/ahd.lock"), "{}\n");
writeFileSync(join(roots.ah, "run/ahd.lock.stale.1.abc"), "{}\n");
writeFileSync(join(roots.proj, ".claude/ah2/teams/a.json"), '{"team":"a","pid":2}\n');
writeFileSync(join(roots.proj, ".claude/ah2/new.json"), '{"n":1}\n');
mkdirSync(join(roots.ah, "stall"), { recursive: true });
writeFileSync(join(roots.ah, "stall/ticks.json"), '{"stall-watch":{"daemon_pid":1}}\n');
writeFileSync(join(roots.ah, "stall/other.json"), "{}\n");
mkdirSync(join(roots.ah, "state/backups"), { recursive: true });
writeFileSync(join(roots.ah, "state/backups/ah-1.jsonl.partial"), "{}\n");
writeFileSync(join(roots.ah, "state/other.json"), "{}\n");
writeFileSync(join(roots.ah, "state/.domain.lock.1.abc"), "{}\n");
writeFileSync(join(roots.ah, "state/.domain.locked"), "{}\n");
writeFileSync(join(roots.ah, "stall/ticks.json.tmp.ac8c05d6"), "{}\n");
writeFileSync(join(roots.ah, "state/x.json.tmp"), "{}\n");
const after = snapshot(roots, ignore);
const norm = createNormalizer(rules(), { T: root, roots, plugin: "/x" });
const d = treeDiff(before, after, norm);
console.log(JSON.stringify({ added: d.added.map((e) => e.path), changed: d.changed.map((e) => [e.path, e.content]), removed: d.removed }));
EOF
OUT=$(node "$SB/td.mjs" "$SB/td" 2>&1)
check "C0-6: the tree diff ignores ah/logs/, ah/run/*.lock, *.stale.*, an atomic write's *.tmp.* and the daemon's ah/stall/ticks.json, and reports the new files (x.json.tmp, with no suffix after .tmp, included)" '[ "$(echo "$OUT" | node -e "const d=JSON.parse(require(\"fs\").readFileSync(0,\"utf8\")); console.log(d.added.sort().join(\",\"))")" = "ah/stall/other.json,ah/state/.domain.locked,ah/state/other.json,ah/state/x.json.tmp,proj/.claude/ah2/new.json" ]'
check "...and a changed team file is reported with its normalized content" 'echo "$OUT" | grep -q "\"proj/.claude/ah2/teams/a.json\",\"{\\\\\"team\\\\\":\\\\\"a\\\\\",\\\\\"pid\\\\\":\\\\\"<PID>\\\\\"}\\\\n\""'

# ---- C0-16: cleanup refusal
mkdir -p "$SB/notours"; : >"$SB/notours/keep"
OUT=$(T="$SB/notours" node "$TOOLS/run.mjs" --kind cli 2>&1); RC=$?
check "C0-16: the runner given T=<not an ahprobe sandbox> exits 2 and removes nothing" '[ $RC = 2 ] && [ -f "$SB/notours/keep" ] && echo "$OUT" | grep -q refusing'
OUT=$(T="$SB/notours" node "$TOOLS/check-routes.mjs" 2>&1); RC=$?
check "...and the route check" '[ $RC = 2 ] && [ -f "$SB/notours/keep" ]'

# ---- C0-5 and the runner's failure path, on a copy of the tooling and one recorded CLI case (the real cases are not edited)
P="$SB/plug"
mkdir -p "$P/tests"
cp -R "$PLUGIN/tests/contract-tools" "$P/tests/contract-tools"
mkdir -p "$P/tests/contracts/cli/cases/msg/help"
cp "$PLUGIN/tests/contracts/normalize.json" "$P/tests/contracts/normalize.json"
cp "$PLUGIN/tests/contracts/cli/cases/msg/help/help-flag.json" "$P/tests/contracts/cli/cases/msg/help/help-flag.json"
ln -s "$PLUGIN/hooks" "$P/hooks"; ln -s "$PLUGIN/bin" "$P/bin"; mkdir -p "$P/native"; cp "$PLUGIN/native/cutover.json" "$P/native/"
ln -s "$PLUGIN/.claude-plugin" "$P/.claude-plugin"
OUT=$(node "$P/tests/contract-tools/run.mjs" --kind cli 2>&1); RC=$?
check "...the runner passes it" '[ $RC = 0 ] && echo "$OUT" | grep -q "contracts: 1/1"'
node -e '
const fs=require("fs"); const f=process.argv[1]; const c=JSON.parse(fs.readFileSync(f));
c.expect.stdout = c.expect.stdout.replace("message-file CLI", "message-file CLX");
fs.writeFileSync(f, JSON.stringify(c,null,2)+"\n")' "$P/tests/contracts/cli/cases/msg/help/help-flag.json"
OUT=$(node "$P/tests/contract-tools/run.mjs" --kind cli 2>&1); RC=$?
check "the runner fails that case, prints the diff path, and the diff shows the changed line" '[ $RC = 1 ] && echo "$OUT" | grep -q "FAIL cli/msg/help/help-flag" && D=$(echo "$OUT" | sed -n "s/.*diff: \(.*\.diff\)$/\1/p" | head -1) && [ -f "$D" ] && grep -q "^[-+].*message-file CL" "$D"'
KEPT=$(echo "$OUT" | sed -n 's/^contracts: sandbox kept at \(.*\)$/\1/p'); case "$KEPT" in /private/tmp/ahprobe.??????) rm -rf "$KEPT";; esac

# ---- a named divergence is a skip that is not a failure; any other skip still fails the run (the lanes verdict applies the same rule)
cp "$PLUGIN/tests/contracts/cli/cases/msg/help/help-flag.json" "$P/tests/contracts/cli/cases/msg/help/help-flag.json"
node -e '
const fs=require("fs"); const c=JSON.parse(fs.readFileSync(process.argv[1])); c.id="help-flag-two";
fs.writeFileSync(process.argv[2], JSON.stringify(c,null,2)+"\n")' "$P/tests/contracts/cli/cases/msg/help/help-flag.json" "$P/tests/contracts/cli/cases/msg/help/help-flag-two.json"
mkdir -p "$P/tests/contracts/divergence"
printf '{"excludedCases":["cli/msg/help-flag"]}\n' >"$P/tests/contracts/divergence/x.json"
OUT=$(node "$P/tests/contract-tools/run.mjs" --kind cli 2>&1); RC=$?
check "a case named in a divergence ledger is skipped as named-divergence and the run still exits 0" \
  '[ $RC = 0 ] && echo "$OUT" | grep -q "^skip cli/msg/help/help-flag  (named-divergence)$" && echo "$OUT" | grep -q "^contracts: 1/1 (native), skipped: 1 (named-divergence)$" && ! echo "$OUT" | grep -q -- "--require-native"'
OUT=$(env -u AHD_NATIVE_BIN node "$P/tests/contract-tools/run.mjs" --kind cli 2>&1); RC=$?
check "...and a skip that is not named (no native binary) beside it fails the run: exit 1, the named one still named" \
  '[ $RC = 1 ] && echo "$OUT" | grep -q "^skip cli/msg/help/help-flag  (named-divergence)$" && echo "$OUT" | grep -q "^skip cli/msg/help/help-flag-two  (no-native-binary)$" && echo "$OUT" | grep -q -- "--require-native: 1 case(s) were skipped"'
OUT=$(env -u AHD_NATIVE_BIN node "$P/tests/contract-tools/run.mjs" --kind cli --allow-skips 2>&1); RC=$?
check "...and --allow-skips lets that unnamed skip exit 0" '[ $RC = 0 ]'
rm -f "$P/tests/contracts/divergence/x.json" "$P/tests/contracts/cli/cases/msg/help/help-flag-two.json"

# ---- rerecord does not rewrite a case that carries a mutant unless --recheck-mutants is given
CASE="$P/tests/contracts/cli/cases/msg/help/help-flag.json"
node -e '
const fs=require("fs"); const f=process.argv[1]; const c=JSON.parse(fs.readFileSync(f));
c.mutant="a help text that names no verb"; c.expect.stdout = c.expect.stdout.replace("message-file CLI", "message-file CLX");
fs.writeFileSync(f, JSON.stringify(c,null,2)+"\n")' "$CASE"
BEFORE=$(cksum <"$CASE")
OUT=$(node "$P/tests/contract-tools/rerecord.mjs" --kind cli --write 2>&1); RC=$?
check "rerecord --write leaves a stale case that carries a mutant untouched and says why" '[ $RC = 0 ] && [ "$(cksum <"$CASE")" = "$BEFORE" ] && echo "$OUT" | grep -q "skip cli/msg/help/help-flag: carries a mutant"'
OUT=$(node "$P/tests/contract-tools/rerecord.mjs" --kind cli --write --recheck-mutants 2>&1); RC=$?
check "...and with --recheck-mutants it rewrites that case and lists it for a mutant re-run" '[ $RC = 0 ] && grep -q "message-file CLI" "$CASE" && echo "$OUT" | grep -q "mutant re-run: cli/msg/help/help-flag"'
cp "$PLUGIN/tests/contracts/cli/cases/msg/help/help-flag.json" "$CASE"

# ---- rerecord --fields replaces only the named keys of a case whose stdout and tree_diff both differ
node -e '
const fs=require("fs"); const f=process.argv[1]; const c=JSON.parse(fs.readFileSync(f));
c.expect.stdout = c.expect.stdout.replace("message-file CLI", "message-file CLX"); c.expect.tree_diff.added = ["stale/file"];
fs.writeFileSync(f, JSON.stringify(c,null,2)+"\n")' "$CASE"
OUT=$(node "$P/tests/contract-tools/rerecord.mjs" --kind cli --write --recheck-mutants --fields stdout 2>&1); RC=$?
check "rerecord --fields stdout rewrites the stdout of a case and leaves its differing tree_diff as stored" \
  '[ $RC = 0 ] && grep -q "message-file CLI" "$CASE" && grep -q "stale/file" "$CASE" && echo "$OUT" | grep -q "rewrote cli/msg/help/help-flag: stdout$"'
OUT=$(node "$P/tests/contract-tools/rerecord.mjs" --kind cli --write --recheck-mutants 2>&1); RC=$?
check "...and without --fields the same case gets its tree_diff re-recorded too" '[ $RC = 0 ] && ! grep -q "stale/file" "$CASE"'
cp "$PLUGIN/tests/contracts/cli/cases/msg/help/help-flag.json" "$CASE"

# ---- the load-time namespace translation rewrites an agent ref and leaves the ask-class tag alone
NS=$(node -e 'process.stdout.write(JSON.parse(require("fs").readFileSync(process.argv[1])).name)' "$P/.claude-plugin/plugin.json")
OUT=$(node -e 'import("file://" + process.argv[1]).then((m) => process.stdout.write(m.toPluginNamespace("Agent(ah:architect) metadata.source ah:<class> and ah:qa")))' "$P/tests/contract-tools/lib-case.mjs" 2>&1); RC=$?
check "toPluginNamespace rewrites ah:architect and ah:qa and keeps ah:<class> (the tag the product prints as is)" '[ $RC = 0 ] && [ "$OUT" = "Agent($NS:architect) metadata.source ah:<class> and $NS:qa" ]'

# ---- a path-qualified key names exactly one case, where kind/cli/id also covers every case that shares the id
mkdir -p "$P/tests/contracts/cli/cases/msg/help2"
cp "$PLUGIN/tests/contracts/cli/cases/msg/help/help-flag.json" "$P/tests/contracts/cli/cases/msg/help2/help-flag.json"
printf '{"excludedCases":["cli/msg/help/help-flag"]}\n' >"$P/tests/contracts/divergence/x.json"
OUT=$(node "$P/tests/contract-tools/run.mjs" --kind cli 2>&1); RC=$?
check "a path-qualified key skips its own case as named-divergence and not the same-id case in another directory, which runs and passes" \
  '[ $RC = 0 ] && echo "$OUT" | grep -q "^skip cli/msg/help/help-flag  (named-divergence)$" && ! echo "$OUT" | grep -q "^skip cli/msg/help2/help-flag" && echo "$OUT" | grep -q "^contracts: 1/1 (native), skipped: 1 (named-divergence)$"'
printf '{"excludedCases":["cli/msg/help-flag"]}\n' >"$P/tests/contracts/divergence/x.json"
OUT=$(node "$P/tests/contract-tools/run.mjs" --kind cli 2>&1); RC=$?
check "...and the kind/cli/id key still skips both cases that share the id" \
  '[ $RC = 0 ] && echo "$OUT" | grep -q "^skip cli/msg/help/help-flag  (named-divergence)$" && echo "$OUT" | grep -q "^skip cli/msg/help2/help-flag  (named-divergence)$"'
rm -rf "$P/tests/contracts/divergence/x.json" "$P/tests/contracts/cli/cases/msg/help2"

# ---- a fixture repository does not run git's background maintenance into its own .git
cat >"$SB/gitcfg.mjs" <<EOF
import { materializeTree } from "$TOOLS/lib-case.mjs";
import { execFileSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
const dir = process.argv[2];
mkdirSync(dir, { recursive: true });
const roots = materializeTree("cli-pg-merge-off", dir, { T: dir, plugin: "$PLUGIN" });
const get = (k) => { try { return execFileSync("git", ["-C", roots.proj, "config", "--local", "--get", k], { encoding: "utf8" }).trim(); } catch { return "(unset)"; } };
console.log(["gc.auto", "maintenance.auto", "gc.autoDetach", "receive.autogc"].map((k) => k + "=" + get(k)).join(" "));
EOF
OUT=$(node "$SB/gitcfg.mjs" "$SB/gitcfg" 2>&1)
check "a repository the case builder replays has gc.auto=0, maintenance.auto=false, gc.autoDetach=false and receive.autogc=false" \
  '[ "$OUT" = "gc.auto=0 maintenance.auto=false gc.autoDetach=false receive.autogc=false" ]'
git -C "$SB" init -q "$SB/plainrepo"
OUT=$(git -C "$SB/plainrepo" config --local --get gc.auto 2>&1; echo "rc=$?")
check "...and a repository made by a bare git init has none of them (the check can fail)" 'echo "$OUT" | grep -q "^rc=1$"'

# ---- C0-3: routes.json lists exactly the routes the daemon answers
OUT=$(node "$TOOLS/check-routes.mjs" 2>&1); RC=$?
check "C0-3: every listed route is answered, and the contract document and routes.json agree" '[ $RC = 0 ]'
node -e '
const fs=require("fs"); const d=JSON.parse(fs.readFileSync(process.argv[1]));
d.routes = d.routes.filter(r => r.path !== "/v1/contract/teams");
fs.writeFileSync(process.argv[2], JSON.stringify(d));' "$PLUGIN/tests/contracts/wire/routes.json" "$SB/routes-missing.json"
OUT=$(node "$TOOLS/check-routes.mjs" --routes "$SB/routes-missing.json" 2>&1); RC=$?
check "a route missing from the file fails the check" '[ $RC = 1 ] && echo "$OUT" | grep -q "contract/teams.*missing from routes.json"'
node -e '
const fs=require("fs"); const d=JSON.parse(fs.readFileSync(process.argv[1]));
d.routes.push({method:"GET",path:"/v1/ghost",hidden:false,query:{},body:null,statuses:[200],long_poll:null,max_body_bytes:65536});
fs.writeFileSync(process.argv[2], JSON.stringify(d));' "$PLUGIN/tests/contracts/wire/routes.json" "$SB/routes-ghost.json"
OUT=$(node "$TOOLS/check-routes.mjs" --routes "$SB/routes-ghost.json" 2>&1); RC=$?
check "a route listed that the daemon does not serve fails the check" '[ $RC = 1 ] && echo "$OUT" | grep -q "/v1/ghost"'

# ---- C0-18: fixtures are tracked (a .gitignore rule must not hide a fixture under tests/contracts/)
OUT=$(node "$TOOLS/check-tracked.mjs" 2>&1); RC=$?
if [ $RC = 3 ]; then echo "SKIP: C0-18 fixtures tracked: not a git work tree"; else
  check "C0-18: no file under tests/contracts is untracked or ignored by git" '[ $RC = 0 ]'
fi
mkdir -p "$SB/gitrepo/tests/contracts/trees/x/proj/.claude/ah2" "$SB/gitrepo/tests/contracts/trees/x/home"
printf '.claude/\n' >"$SB/gitrepo/.gitignore"
echo '{}' >"$SB/gitrepo/tests/contracts/trees/x/proj/.claude/team.json"
echo 'kept' >"$SB/gitrepo/tests/contracts/trees/x/home/f"
git -C "$SB/gitrepo" init -q && git -C "$SB/gitrepo" add -A . && git -C "$SB/gitrepo" -c user.name=t -c user.email=t@t commit -qm base
OUT=$(node "$TOOLS/check-tracked.mjs" --root "$SB/gitrepo" 2>&1); RC=$?
check "C0-18 the check can fail: a fixture under trees/x/.claude hidden by a .gitignore rule is reported as ignored" '[ $RC = 1 ] && echo "$OUT" | grep -q "^ignored: tests/contracts/trees/x/proj/.claude/team.json$"'
echo 'new' >"$SB/gitrepo/tests/contracts/trees/x/home/untracked.txt"
printf '' >"$SB/gitrepo/.gitignore"
OUT=$(node "$TOOLS/check-tracked.mjs" --root "$SB/gitrepo" 2>&1); RC=$?
check "...and a plain untracked fixture is reported as untracked" '[ $RC = 1 ] && echo "$OUT" | grep -q "^untracked: tests/contracts/trees/x/home/untracked.txt$"'

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
