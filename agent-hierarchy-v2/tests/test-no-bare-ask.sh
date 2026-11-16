#!/bin/bash
# agent-hierarchy — native code may name the PreToolUse decision `ask` in one place only: the AskDecision helper in
# ConfigCore.Paths.cs, which turns it into a deny in a member session. A gate added later that writes its own prompt would
# leave a member waiting at a prompt nobody watches. Comments are not code.
# Usage: bash tests/test-no-bare-ask.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:300})"; fi
}

# <dir>: every file:line under it where code (comments blanked) assigns the string literal "ask" to a decision key
# (["...Decision"] = "ask" or "...decision": "ask"), other than the one in ConfigCore.Paths.cs, which must hold exactly one.
# Other uses of the word (a ledger answer, a UI action kind) are not decisions and are not scanned.
bare() {
  node - "$1" <<'JS'
const fs = require("fs"), path = require("path");
const dir = process.argv[2], found = [];
let inHelper = 0;
const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)]);
for (const file of walk(dir).filter((n) => n.endsWith(".cs"))) {
  const f = path.basename(file);
  const src = fs.readFileSync(file, "utf8");
  // Comments blanked, strings kept: enough for a literal-name test.
  const code = src.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, " ")).replace(/(^|[^:"'`\\])\/\/[^\n]*/g, (m, a) => a + " ".repeat(m.length - a.length));
  code.split("\n").forEach((line, i) => {
    for (const m of line.matchAll(/[Dd]ecision"\s*(\]\s*=|:)\s*"ask"/g)) {
      if (f === "ConfigCore.Paths.cs") inHelper++; else found.push(`${f}:${i + 1}`);
    }
    if (f === "ConfigCore.Paths.cs") return;
    // A permission-decision key (permissionDecision and the like) given anything but a string literal could carry ask from a
    // constant, and an identifier named for a decision holding "ask" is the same thing one step removed. HookEmit.cs is the
    // one emitter that takes the decision as a parameter; a plain "decision" key is the pipeline's own record, not a prompt.
    if ((f !== "HookEmit.cs" && /[a-z]Decision"\s*(\]\s*=|:)\s*(?!")\S/.test(line)) || /\b\w*Decision\w*\s*=\s*"ask"/.test(line)) found.push(`${f}:${i + 1}`);
  });
}
if (inHelper !== 1) found.push(`ConfigCore.Paths.cs: ${inHelper} literal ask decisions, expected exactly 1`);
process.stdout.write(found.join("\n"));
JS
}

OUT=$(bare "$PLUGIN/native/src")
check "no native file names the ask decision except AskDecision in ConfigCore.Paths.cs" '[ -z "$OUT" ]'

T=$(mktemp -d); [ -n "$T" ] && [ -d "$T" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$T"'
printf '%s\n' 'return new JsonObject { ["decision"] = "ask", ["reason"] = reason };' > "$T/ConfigCore.Paths.cs"
OUT=$(bare "$T")
check "the fixture helper alone passes the scan" '[ -z "$OUT" ]'
printf '%s\n' 'var d = new JsonObject { ["permissionDecision"] = "ask" };' > "$T/NewGate.cs"
OUT=$(bare "$T")
check "a new gate with a bare ask decision fails the scan" 'printf "%s" "$OUT" | grep -q "NewGate.cs:1"'
printf '%s\n' '// the gate says "ask" in a comment' '/* and "ask" here */' '/// <summary>"ask" in a doc comment</summary>' > "$T/NewGate.cs"
OUT=$(bare "$T")
check "ask in a comment is not code" '[ -z "$OUT" ]'
rm -f "$T/NewGate.cs"
printf '\n%s\n' 'var extra = new JsonObject { ["decision"] = "ask" };' >> "$T/ConfigCore.Paths.cs"
OUT=$(bare "$T")
check "a second ask literal in ConfigCore.Paths.cs fails the scan" 'printf "%s" "$OUT" | grep -q "expected exactly 1"'

printf '%s\n' 'c.Stdout.WriteLine(ledger?.Answer switch { "yes" => "sync", _ => "ask", });' 'Action(id, label, key, "ask", "owner", target);' 'var d = new JsonObject { ["decision"] = "deny", ["reason"] = r };' > "$T/Other.cs"
OUT=$(bare "$T" | grep -v 'expected exactly 1')
check "an ask that is not assigned to a decision key (a ledger answer, an action kind), and a literal deny decision, are not flagged" '[ -z "$OUT" ]'
printf '%s\n' 'var d = new JsonObject { ["permissionDecision"] = AskDecision };' > "$T/Other.cs"
OUT=$(bare "$T")
check "a decision key set from a constant (the ask routed through one) fails the scan" 'printf "%s" "$OUT" | grep -q "Other.cs:1"'
printf '%s\n' 'const string PermissionDecision = "ask";' > "$T/Other.cs"
OUT=$(bare "$T")
check "an identifier named for a decision holding the ask literal fails the scan" 'printf "%s" "$OUT" | grep -q "Other.cs:1"'
printf '%s\n' 'const string Verdict = "ask";' 'var Decisions = new List<string>();' > "$T/Other.cs"
OUT=$(bare "$T" | grep -v 'expected exactly 1')
check "an ask held by an identifier not named for a decision, and a decision-named identifier not assigned ask, are not flagged" '[ -z "$OUT" ]'
printf '%s\n' 'var rec = new JsonObject { ["decision"] = chosen };' > "$T/Other.cs"
OUT=$(bare "$T" | grep -v 'expected exactly 1')
check "the pipeline's plain decision key set from a variable is not a permission decision" '[ -z "$OUT" ]'
printf '%s\n' 'var d = new JsonObject { ["permissionDecision"] = decision };' > "$T/HookEmit.cs"
OUT=$(bare "$T" | grep -v 'expected exactly 1')
check "the HookEmit emitter, which takes the decision as a parameter, is not flagged" '[ -z "$OUT" ]'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
