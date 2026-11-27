#!/bin/bash
# agent-hierarchy — role packs: plugins that carry ah-roles.json, inspected with `pack list|show`, their roles
# reached through legacy `from` rows (the pin ignored, adr-46) and spawned in any slot.
# HOME-redirected with a fake installed_plugins.json pointing at sandbox plugin dirs.
# Usage: bash tests/test-role-packs.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
# Every Claude session exports some of these; a test must not inherit any of them.
unset WRANGL_TEAM_FILE CLAUDE_PID WRANGL_ROSTER WRANGL_HIERARCHY_DIR
# This test's Claude directory is $FAKEHOME/.claude (its agents, install records and trust): a launch reads agent files from
# CLAUDE_CONFIG_DIR when one is set, and run-all.sh hands every test an empty one of its own.
unset CLAUDE_CONFIG_DIR
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-role-packs-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
# Run from the sandbox, so nothing reads the state of the checkout this runs from.
cd "$SANDBOX" || exit 1
# No test may reach the real herdr or tmux: a stub that fails every call sits first on PATH (a failing herdr or tmux is what a
# machine without a running multiplexer gives), and the guard in lib-hermetic.sh is told it is a fake.
mkdir -p "$SANDBOX/nolaunch"; printf '#!/bin/sh\nexit 1\n' > "$SANDBOX/nolaunch/herdr"; cp "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"; chmod +x "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"
export PATH="$SANDBOX/nolaunch:$PATH"; export AH_TEST_FAKE_BIN="$SANDBOX/nolaunch"
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/proj"
PACKS="$SANDBOX/packs"
GLOBAL_CFG="$FAKEHOME/.claude/ah2/agent-hierarchy.json"
REPO_CFG="$PROJ/.claude/ah2/agent-hierarchy.json"
IPJ="$FAKEHOME/.claude/plugins/installed_plugins.json"
TRUSTED="$FAKEHOME/.claude/ah2/trusted"
# The hook payloads below name sessions g1 and rg; each counts only when a registration proves it belongs to this shell. Session rp
# is the SessionStart payload: that hook runs under a claude-named parent and registers the session itself, so it must be unregistered.
for _sid in g1 rg; do ah_test_prove_self orchestrator proj "" "" "$_sid" > /dev/null; done
LINE="takes work in unattended /wrangl:pipeline runs (pushes draft PRs, does not merge)"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:300} ERR=${ERR:0:300})"; fi
}

# roster.mjs with HOME redirected, in PROJ: stdout in OUT, stderr in ERR.
rm_() {
  ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null
  OUT=$(HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ" 2>"$SANDBOX/err"); RC=$?
  ERR=$(cat "$SANDBOX/err")
}
# <js expression over the parsed OUT o>: its value (strings bare, anything else as JSON).
jget() {
  node -e 'const o=JSON.parse(process.argv[1]);const v=(new Function("o","return ("+process.argv[2]+")"))(o);process.stdout.write(typeof v==="string"?v:JSON.stringify(v))' "$OUT" "$1" 2>/dev/null
}
# <file> <js expression over the parsed file d>: true when the expression is.
filejs() {
  node -e 'const d=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.exit((new Function("d","return ("+process.argv[2]+")"))(d)?0:1)' "$1" "$2" 2>/dev/null
}
# <file> <js statement over the parsed file d>: edits a JSON file, creating it when missing.
mutate() {
  mkdir -p "$(dirname "$1")"
  node -e 'const fs=require("fs");const f=process.argv[1];const d=fs.existsSync(f)?JSON.parse(fs.readFileSync(f,"utf8")):{};(new Function("d",process.argv[2]))(d);fs.writeFileSync(f,JSON.stringify(d,null,2)+"\n")' "$1" "$2"
}
# <json of records keyed plugin@marketplace → [install dir, ...]>: the fake installed_plugins.json.
installed() {
  mkdir -p "$(dirname "$IPJ")"
  node -e 'const m=JSON.parse(process.argv[1]);const plugins={};for(const[k,dirs]of Object.entries(m))plugins[k]=dirs.map((d,i)=>({scope:"user",installPath:d,version:"1.0."+i}));require("fs").writeFileSync(process.argv[2],JSON.stringify({version:2,plugins},null,2))' "$1" "$IPJ"
}

# <dir> <plugin name>: a pack with an implement role (pk-impl), a review role (pk-rev), a design
# role (pk-des), a hook, a script and one agent the manifest doesn't name.
mkpack() {
  local d=$1 p=$2
  mkdir -p "$d/.claude-plugin" "$d/agents" "$d/hooks" "$d/bin"
  printf '{"name":"%s","version":"1.0.0","description":"test pack"}\n' "$p" > "$d/.claude-plugin/plugin.json"
  printf -- '---\nname: pk-impl\ndescription: Builds pack things\nmodel: opus\ntools: Read, Grep, Edit, Write, Bash, SendMessage\n---\nYou build pack features.\n' > "$d/agents/pk-impl.md"
  printf -- '---\nname: pk-rev\ndescription: Reviews pack things\nmodel: opus\ntools: Read, Grep, Bash, SendMessage\n---\nYou review pack changes.\n' > "$d/agents/pk-rev.md"
  printf -- '---\nname: pk-des\ndescription: Designs pack things\nmodel: opus\ntools: Read, Grep, Write, SendMessage\n---\nYou design pack features.\n' > "$d/agents/pk-des.md"
  printf -- '---\nname: pk-leg\ndescription: Runs pack errands\nmodel: haiku\ntools: Read, Grep, Bash\n---\nYou run pack errands.\n' > "$d/agents/pk-leg.md"
  printf -- '---\nname: pk-extra\ndescription: not a role\ntools: Read\n---\nExtra.\n' > "$d/agents/pk-extra.md"
  cat > "$d/ah-roles.json" <<'J'
{
  "version": 1,
  "roles": {
    "pk-impl": { "class": "implement", "description": "Builds pack features", "routes": "pack scenes and scripts", "model": "opus" },
    "pk-rev": { "class": "review", "description": "Reviews pack changes", "model": "opus" },
    "pk-des": { "class": "design", "description": "Designs pack features", "model": "opus" },
    "pk-leg": { "class": "legwork", "description": "Runs pack errands", "model": "haiku" }
  }
}
J
  echo '{}' > "$d/hooks/hooks.json"
  printf '#!/bin/sh\necho hi\n' > "$d/bin/tool.sh"
}

# A fresh HOME and repo, with pack `pk` installed from marketplace `mk` at $PACKS/a.
fresh() {
  rm -rf "$FAKEHOME" "$PROJ" "$PACKS" "$AH_HOME/state" # the run anchor anchor() files lives in the sandbox database
  mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2" "$PACKS"
  git -C "$PROJ" init -q
  mkpack "$PACKS/a" pk
  installed "{\"pk@mk\":[\"$PACKS/a\"]}"
}

# <name> <from> [--level repo] [--<field> <value>…]: a legacy `from` row written into the level file (adr-46: no adopt verb).
adopt() {
  local name=$1 from=$2 cfg=$GLOBAL_CFG fields=""; shift 2
  while [ $# -gt 0 ]; do
    if [ "$1" = --level ] && [ "$2" = repo ]; then cfg=$REPO_CFG; else fields="$fields, ${1#--}: \"$2\""; fi
    shift 2
  done
  mutate "$cfg" "d.roles = d.roles || {}; d.roles[\"$name\"] = {from: \"$from\"$fields}"
  RC=0
}

# The role list row of <name> as JSON, in OUT.
rolerow() { rm_ role list --json; OUT=$(node -e 'const o=JSON.parse(process.argv[1]);process.stdout.write(JSON.stringify(o.roles.find(r=>r.name===process.argv[2])||o.excluded.find(r=>r.name===process.argv[2])||null))' "$OUT" "$1"); }

# The finding codes of the role list row in OUT, or the reason it was excluded.
rowstate() { jget "o.reason || o.findings.filter(f => f.level === \"error\").map(f => f.code).join() || \"ok\""; }

decision() { node -e 'try{const o=JSON.parse(process.argv[1]);process.stdout.write(o.hookSpecificOutput.permissionDecision)}catch{process.stdout.write("none")}' "$OUT"; }

# An open pipeline-run anchor exchange in PROJ's hierarchy dir.
anchor() {
  local tf="$PROJ/.claude/ah2/teams/$(basename "$PROJ").json"
  mkdir -p "$(dirname "$tf")"
  printf '{"version":1,"team_id":"anchor","created":"2026-01-01T00:00:00Z","roster_level":"repo","transport":"terminal","orchestrator":{"session_id":null,"pid":%s},"members":[],"partial":false}\n' "$$" > "$tf"
  ah_test_prove_self orchestrator "$(basename "$PROJ")" "$tf" > /dev/null
  HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg new --type request --to orchestrator --from orchestrator --slug pipeline-run-anchor --title "Fix test titles" --cwd "$PROJ" >/dev/null 2>&1
}

# ================================================================ P1 pack list
fresh
mkdir -p "$PACKS/plain/.claude-plugin" "$PACKS/plain/agents"
echo '{"name":"plain","version":"2.0.0"}' > "$PACKS/plain/.claude-plugin/plugin.json"
installed "{\"pk@mk\":[\"$PACKS/a\"],\"plain@mk\":[\"$PACKS/plain\"]}"
rm_ pack list --json
check "P1 pack list finds the plugin with ah-roles.json and skips the one without" \
  '[ $RC = 0 ] && [ "$(jget "o.packs.map(p => p.plugin + \"@\" + p.marketplace + \":\" + p.version).join()")" = "pk@mk:1.0.0" ] && [ "$(jget "o.packs[0].roles.map(r => r.name + \"/\" + r.class).join()")" = "pk-impl/implement,pk-rev/review,pk-des/design,pk-leg/legwork" ]'
check "P1 ... and counts what else the plugin carries" '[ "$(jget "o.packs[0].also_in_plugin")" = 3 ]'

# ================================================================ P2 manifest and text checks
bad() { # <dir> <js edit of the manifest object m>: a copy of pack a with its manifest edited
  rm -rf "$1"; cp -R "$PACKS/a" "$1"
  node -e 'const fs=require("fs");const f=process.argv[1];const m=JSON.parse(fs.readFileSync(f,"utf8"));(new Function("m",process.argv[2]))(m);fs.writeFileSync(f,JSON.stringify(m,null,2))' "$1/ah-roles.json" "$2"
}
showpath() { rm_ pack show --path "$1" --json; }
roleerr() { jget "(o.roles.find(r => r.name === \"$1\") || {findings: []}).findings.map(f => f.code).join()"; }
rolewarn() { jget "(o.roles.find(r => r.name === \"$1\") || {warnings: []}).warnings.join()"; }
fresh
bad "$PACKS/v2" 'm.version = 2'
showpath "$PACKS/v2"
check "P2 version other than 1 makes the whole pack unreadable (pack-version)" '[ $RC = 0 ] && [ "$(jget o.manifest.error.code)" = pack-version ] && [ "$(jget o.roles.length)" = 0 ]'
bad "$PACKS/names" 'm.roles["Bad Name"] = {class: "implement"}; m.roles["reviewer"] = {class: "review"}'
showpath "$PACKS/names"
check "P2 a bad role name is refused, and a built-in's name is allowed (ah2-698 N1)" '[ "$(roleerr "Bad Name")" = pack-invalid ] && [ "$(jget "o.roles.find(r => r.name === \"reviewer\").agent")" = reviewer ] && [[ "$OUT" != *"is reserved"* ]]'
bad "$PACKS/agents" 'm.roles["pk-impl"].agent = "other:pk-impl"; m.roles["pk-rev"].agent = "../pk-rev"; m.roles["pk-des"].agent = "sub/pk-des"'
showpath "$PACKS/agents"
check "P2 an agent containing :, .. or / is refused" '[ "$(roleerr pk-impl)" = pack-invalid ] && [ "$(roleerr pk-rev)" = pack-invalid ] && [ "$(roleerr pk-des)" = pack-invalid ]'
bad "$PACKS/fields" 'm.roles["pk-impl"].description = "x".repeat(161); m.roles["pk-rev"].description = "two\nlines"; m.roles["pk-des"].routes = "has `tick`"'
showpath "$PACKS/fields"
check "P2 a field over 160 characters, with a newline or with a backtick is dropped with a warning, the role kept" \
  '[ "$(roleerr pk-impl)" != pack-invalid ] && [[ "$(rolewarn pk-impl)" == *"description is dropped"* ]] && [[ "$(rolewarn pk-rev)" == *"description is dropped"* ]] && [[ "$(rolewarn pk-des)" == *"routes is dropped"* ]] && [ "$(jget "\"routes\" in o.roles.find(r => r.name === \"pk-des\").fields")" = false ]'
bad "$PACKS/unknown" 'm.roles["pk-impl"].color = "red"; m.extra = 1'
showpath "$PACKS/unknown"
check "P2 an unknown key warns" '[ "$(roleerr pk-impl)" != pack-invalid ] && [[ "$(jget "o.roles[0].warnings.join()")" == *"unknown key \"color\""* ]] && [[ "$(jget "o.manifest.warnings.join()")" == *"unknown key \"extra\""* ]]'
bad "$PACKS/hidden" 'm.roles["pk-impl"].description = "safe\u202Eevil"; m.roles["pk-rev"].routes = "tag\u{E0041}char"'
printf -- '---\nname: pk-des\ndescription: Designs\xe2\x80\x8b things\nmodel: opus\ntools: Read, Grep, Write, SendMessage\n---\nBody.\n' > "$PACKS/hidden/agents/pk-des.md"
showpath "$PACKS/hidden"
check "P2 hidden characters: a bidi control and a tag character in a field drop it, a zero-width character in the agent file warns" \
  '[[ "$(rolewarn pk-impl)" == *"description is dropped"* ]] && [[ "$(rolewarn pk-rev)" == *"routes is dropped"* ]] && [[ "$(roleerr pk-des)" == *pack-hidden-chars* ]] && [[ "$(roleerr pk-des)" != *pack-invalid* ]]'
bad "$PACKS/hidden2" 'm.roles["pk-impl"].description = "sel\uFE00\uFE01\uFE0Fend"; m.roles["pk-rev"].routes = "line\u2028sep"; m.roles["pk-des"].description = "fill\u3164er"; m.roles["pk-leg"].description = "warn ⚠\uFE0F sign"'
node -e 'const fs=require("fs");const f=process.argv[1];fs.writeFileSync(f,fs.readFileSync(f,"utf8").replace("You review pack changes.","abc\u{E0100}def"))' "$PACKS/hidden2/agents/pk-extra.md"
node -e 'const fs=require("fs");const f=process.argv[1];fs.writeFileSync(f,"---\nname: pk-extra\ndescription: not a role\ntools: Read\n---\nabc\u{E0100}def\n")' "$PACKS/hidden2/agents/pk-extra.md"
showpath "$PACKS/hidden2"
check "P2 hidden characters: variation selectors, U+2028, the U+3164 filler and an emoji written with U+FE0F drop their field" \
  '[[ "$(rolewarn pk-impl)" == *"hidden character"* ]] && [[ "$(rolewarn pk-rev)" == *"hidden character"* ]] && [[ "$(rolewarn pk-des)" == *"hidden character"* ]] && [[ "$(rolewarn pk-leg)" == *"hidden character"* ]]'
check "P2 ... and the warning names the code point, line and column" '[[ "$(rolewarn pk-impl)" == *"U+FE00 at line 1, column 4"* ]]'
bad "$PACKS/hidden4" 'm.roles["pk-impl"].description = "braille\u2800blank"'
showpath "$PACKS/hidden4"
check "P2 hidden characters: the U+2800 braille blank drops its field" '[[ "$(rolewarn pk-impl)" == *"hidden character"* ]]'
rm -rf "$PACKS/hidden3"; cp -R "$PACKS/a" "$PACKS/hidden3"
node -e 'const fs=require("fs");fs.writeFileSync(process.argv[1],"---\nname: pk-rev\ndescription: Reviews\nmodel: opus\ntools: Read, Grep, SendMessage\n---\nabc\u{E0100}def\n")' "$PACKS/hidden3/agents/pk-rev.md"
showpath "$PACKS/hidden3"
check "P2 a U+E0100 variation selector in the agent file warns, at line 7, column 4" \
  '[[ "$(roleerr pk-rev)" == *pack-hidden-chars* ]] && [[ "$(jget "o.roles.find(r => r.name === \"pk-rev\").findings.map(f => f.message).join()")" == *"U+E0100 at line 7, column 4"* ]]'
rm -rf "$PACKS/utf"; cp -R "$PACKS/a" "$PACKS/utf"; printf -- '---\nname: pk-impl\ndescription: bad \xff byte\nmodel: opus\ntools: Read, Edit, Write, Bash, SendMessage\n---\nBody.\n' > "$PACKS/utf/agents/pk-impl.md"
installed "{\"pk@mk\":[\"$PACKS/utf\"]}"
adopt u1 pk@mk:pk-impl
rolerow u1
check "P2 a non-UTF-8 agent file warns, and the role stays available" '[ "$(rowstate)" = ok ] && [[ "$(jget "o.findings.map(f => f.message).join()")" == *"not valid UTF-8"* ]]'
installed "{\"pk@mk\":[\"$PACKS/a\"]}"
bad "$PACKS/ansi" 'm.roles["pk-impl"].description = "red \u001b[31mALERT\u001b[0m end"'
rm_ pack show --path "$PACKS/ansi"
check "P2 an ANSI sequence in a field drops it with a warning, and nothing raw reaches the terminal" '[ $RC = 0 ] && [[ "$OUT" == *"description is dropped"* ]] && ! printf "%s" "$OUT" | grep -q $'"'"'\x1b'"'"''
rawctl() { printf "%s" "$OUT" | LC_ALL=C grep -q $'"'"'\x1b\|\xc2\x9b'"'"'; } # a raw ESC or C1 CSI in OUT
rm -rf "$PACKS/pj"; cp -R "$PACKS/a" "$PACKS/pj"
node -e 'require("fs").writeFileSync(process.argv[1], JSON.stringify({name: "pk\u009b31m", version: "1.0\u001b[31m\u009b0m"}))' "$PACKS/pj/.claude-plugin/plugin.json"
rm_ pack show --path "$PACKS/pj"
check "P2 plugin.json's name and version print escaped in pack show (text)" '[ $RC = 0 ] && ! rawctl && [[ "$OUT" == *"pk\\u{9b}31m"* ]] && [[ "$OUT" == *"1.0\\u{1b}[31m"* ]]'
rm_ pack show --path "$PACKS/pj" --json
check "P2 ... and in pack show --json" '[ $RC = 0 ] && ! rawctl && [ "$(jget o.plugin)" = "pk\\u{9b}31m" ]'
installed "{\"pk@mk\":[\"$PACKS/pj\"]}"
rm_ pack list
check "P2 ... and in pack list (text)" '[ $RC = 0 ] && ! rawctl && [[ "$OUT" == *"1.0\\u{1b}[31m\\u{9b}0m"* ]]'
rm_ pack list --json
check "P2 ... and in pack list --json" '[ $RC = 0 ] && ! rawctl && [ "$(jget "o.packs[0].version")" = "1.0\\u{1b}[31m\\u{9b}0m" ]'
installed "{\"pk@mk\":[\"$PACKS/a\"]}"

# ================================================================ P3 a legacy from row
fresh
adopt impl pk@mk:pk-impl --model sonnet --pin "sha256:$(printf '0%.0s' {1..64})"
rolerow impl
check "P3 a legacy row resolves to the manifest's class and <plugin>:<agent>, its stale pin ignored" \
  '[ "$(jget "o.class + \"/\" + o.agent + \"/\" + o.model + \"/\" + o.from + \"/\" + o.legacy")" = "implement/pk:pk-impl/sonnet/pk@mk:pk-impl/true" ] && [[ "$(jget o.status)" != UNAVAILABLE* ]]'

# ================================================================ P4 names and flags
fresh
adopt localname pk@mk:pk-rev
rolerow localname
check "P4 a local name different from the pack's works" '[ "$(jget o.agent)" = pk:pk-rev ] && [[ "$(jget o.status)" != UNAVAILABLE* ]]'
rm_ role set localname --class review
check "P4 --class on a row that has from is refused" '[ $RC = 2 ] && [[ "$ERR" == *"adopted from a pack"* ]]'

# ================================================================ P5 a built-in via a from row
fresh
adopt reviewer pk@mk:pk-rev
rolerow reviewer
check "P5 a matching class overrides the built-in's agent, and role list shows it" \
  '[ "$(jget "o.agent + \"/\" + o.pack_override")" = "pk:pk-rev/true" ] && [[ "$(jget o.status)" != UNAVAILABLE* ]]'
rm_ role remove reviewer
check "P5 role remove restores the shipped agent" '[ $RC = 0 ] && ! filejs "$GLOBAL_CFG" "\"reviewer\" in (d.roles || {})"'

# ================================================================ P8 frontmatter allowlist (pack agents)
fresh
fm() { # <label> <frontmatter lines (printf %b)>
  rm -rf "$PACKS/fm"; cp -R "$PACKS/a" "$PACKS/fm"
  printf -- "---\nname: pk-rev\ndescription: Reviews\nmodel: opus\n$2\n---\nBody.\n" > "$PACKS/fm/agents/pk-rev.md"
  showpath "$PACKS/fm"
  check "P8 $1 is an error" '[[ "$(roleerr pk-rev)" == *pack-agent* ]]'
}
fm "permissionMode" 'tools: Read, Grep, SendMessage\npermissionMode: acceptEdits'
fm "skills" 'tools: Read, Grep, SendMessage\nskills: [x]'
fm "memory" 'tools: Read, Grep, SendMessage\nmemory: user'
fm "a made-up key" 'tools: Read, Grep, SendMessage\nzorp: 1'
fm "a quoted \"permissionMode\": key" 'tools: Read, Grep, SendMessage\n"permissionMode": acceptEdits'
fm "a duplicate key" 'tools: Read, Grep, SendMessage\ntools: Read'
fm "an &anchor" 'tools: &t [Read, Grep, SendMessage]'
fm "an *alias" 'tools: Read, Grep, SendMessage\ncolor: *t'
fm "a <<: merge key" 'tools: Read, Grep, SendMessage\n<<: *base'
fm "a missing tools" 'color: red'
fm "tools with a wildcard" 'tools: Read, mcp__srv__*, SendMessage'
fm "a line that can't be classified" 'tools: Read, Grep, SendMessage\njust some text'
fm "a tools value folded onto a second line" 'tools: Read, Grep, SendMessage\n  Write, Bash'
fm "an indented line under a one-line value" 'tools: Read, Grep, SendMessage\ncolor: red\n  bad: indentation'
fm "a list item under a key that has a value" 'tools: Read, Grep, SendMessage\ncolor: red\n  - Bash'
fm "a tab in the indentation" 'tools:\n\t- Read\n\t- SendMessage'
fm "a # comment on its own line" 'tools: Read, Grep, SendMessage\n# a note'
fm "a # comment after a value" 'tools: Read, Grep, SendMessage\ncolor: red # a note'
fm "a quoted tool entry" 'tools: "Read", Grep, SendMessage'
fm "a [flow] tool list" 'tools: [Read, Grep, SendMessage]'
fm "an Agent(x) tool entry" 'tools: Read, Agent(x), SendMessage'
fm "a | block under tools" 'tools: |\n  Read, Grep, SendMessage'
fm "a - item list under a key other than tools" 'tools: Read, Grep, SendMessage\ncolor:\n  - red'
fm "a list item back at column 0 after indented ones" 'tools:\n  - Read\n  - Grep\n- SendMessage'
fm "a list item indented further than the first (YAML folds it into the item above)" 'tools:\n  - Read\n    - Grep\n  - SendMessage'
fm "a list item indented less than the first" 'tools:\n  - Read\n - Grep\n  - SendMessage'
fm "tools: null" 'tools: null'
fm "tools: True" 'tools: True'
fm "tools: yes" 'tools: yes'
fm "a - null tool item" 'tools:\n  - Read\n  - null\n  - SendMessage'
fm "a - ~ tool item" 'tools:\n  - Read\n  - ~\n  - SendMessage'
fm "an inline False tool entry" 'tools: Read, False, SendMessage'
fm "list items indented with U+00A0" 'tools:\n\xc2\xa0\xc2\xa0- Read\n\xc2\xa0\xc2\xa0- SendMessage'
fm "list items indented with U+3000" 'tools:\n\xe3\x80\x80- Read\n\xe3\x80\x80- SendMessage'
fm "U+00A0 after tools:" 'tools:\xc2\xa0Read, Grep, SendMessage'
fm "U+3000 after tools:" 'tools:\xe3\x80\x80Read, Grep, SendMessage'
fm "U+2003 in a tools entry" 'tools: Read,\xe2\x80\x83Grep, SendMessage'
fm "maxTurns: 1e3" 'tools: Read, Grep, SendMessage\nmaxTurns: 1e3'
fm "maxTurns: 0x10" 'tools: Read, Grep, SendMessage\nmaxTurns: 0x10'
fmd() { # <label> <description and other lines (printf %b)>: pk-rev with no fixed description
  rm -rf "$PACKS/fm"; cp -R "$PACKS/a" "$PACKS/fm"
  printf -- "---\nname: pk-rev\n$2\ntools: Read, Grep, SendMessage\n---\nBody.\n" > "$PACKS/fm/agents/pk-rev.md"
  showpath "$PACKS/fm"
  check "P8 $1 is an error" '[[ "$(roleerr pk-rev)" == *pack-agent-line* ]]'
}
fmd "\": \" in a plain value" 'description: Reviews code: carefully'
fmd "a plain value ending in \":\"" 'description: Reviews code:'
fmd "a | block whose second line is indented less than its first" 'description: |\n    first\n  second'
fmd "a plain value starting with @" 'description: @x'
fmd "a plain value starting with !" 'description: !x'
fmd "a plain value starting with ]" 'description: ]x'
fmd "a plain value starting with \"- \"" 'description: - x'
fmd "a double-quoted value with an escape" 'description: "a\\x41"'
fmd "a single-quoted value with ''" "description: 'it''s'"
fmd "a quoted value with text after it" 'description: "Reviews" code'
fmd "a quoted value that doesn't close" 'description: "Reviews code'
fmd "a plain value YAML reads as a boolean" 'description: yes'
fmd "a plain value YAML reads as null" 'description: ~'
rm -rf "$PACKS/fm"; cp -R "$PACKS/a" "$PACKS/fm"
printf -- '---\nname: pk-rev\ndescription: "yes"\nmodel: opus\ntools: Read, Grep, SendMessage, NotebookEdit, Nontool\nmaxTurns: 5\n---\nBody.\n' > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 a quoted \"yes\", tool names that only start with no or n, and maxTurns: 5 are accepted" '[[ "$(roleerr pk-rev)" != *pack-agent* ]]'
printf -- '---\nname: false\ndescription: Reviews\nmodel: opus\ntools: Read, Grep, SendMessage\n---\nBody.\n' > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 name: false is an error" '[[ "$(roleerr pk-rev)" == *pack-agent-line* ]]'
printf -- '---\nname: h\xc3\xa9lper\ndescription: Reviews\nmodel: opus\ntools: Read, Grep, SendMessage\n---\nBody.\n' > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 name: hélper is an error, naming the code point, line and column" \
  '[[ "$(roleerr pk-rev)" == *pack-agent-line* ]] && [[ "$(jget "o.roles.find(r => r.name === \"pk-rev\").findings.map(f => f.message).join()")" == *"U+00E9 at line 2, column 8"* ]]'
fmd "U+2003 after model:" 'description: Reviews\nmodel:\xe2\x80\x83sonnet'
fmd "a tab after a colon" 'description: Reviews\nmodel:\topus'
fmd "a tab after a block line's indenting spaces" 'description: |\n  \tReviews'
fmd "U+00A0 in a description" 'description: Reviews\xc2\xa0code'
fmd "U+E000 in a description" 'description: Reviews \xee\x80\x80'
fmd "U+FFFF in a description" 'description: Reviews \xef\xbf\xbf'
fmd "U+00A0 after description:" 'description:\xc2\xa0Reviews'
for d in 'description: Caf\xc3\xa9 r\xc3\xa9sum\xc3\xa9' 'description: \xe3\x82\xb3\xe3\x83\xbc\xe3\x83\x89\xe3\x82\x92\xe7\xa2\xba\xe8\xaa\x8d\xe3\x81\x99\xe3\x82\x8b' 'description: >\n  R\xc3\xa9vise le code\n  tr\xc3\xa8s soigneusement'; do
  printf -- "---\nname: pk-rev\n$d\nmodel: opus\ntools: Read, Grep, SendMessage\n---\nBody.\n" > "$PACKS/fm/agents/pk-rev.md"
  showpath "$PACKS/fm"
  check "P8 a non-ASCII description is accepted: $(printf -- "$d" | head -1)" '[[ "$(roleerr pk-rev)" != *pack-agent* ]]'
done
rm -rf "$PACKS/fm"; cp -R "$PACKS/a" "$PACKS/fm"
printf -- "---\nname: pk-rev\ndescription: \"Reviews code: carefully\"\nmodel: 'opus'\ncolor: a:b\ntools: Read, Grep, SendMessage\n---\nBody.\n" > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 a quoted value holding \": \", a single-quoted one and a:b are accepted" '[[ "$(roleerr pk-rev)" != *pack-agent* ]]'
printf -- '---\nname: pk-rev\ndescription: |\n  first\n    indented more\n\n  last\nmodel: opus\ntools: Read, Grep, SendMessage\n---\nBody.\n' > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 a | block line indented more than the first is accepted" '[[ "$(roleerr pk-rev)" != *pack-agent* ]]'
rm -rf "$PACKS/fm"; cp -R "$PACKS/a" "$PACKS/fm"
printf -- '---\nname: pk-rev\ndescription: >\n  Reviews pack\n  changes carefully.\nmodel: opus\ntools:\n- Read\n- Grep\n- SendMessage\n---\nBody.\n' > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 a block list and a > block scalar are allowed" '[[ "$(roleerr pk-rev)" != *pack-agent* ]] && [ "$(jget "o.roles.find(r => r.name === \"pk-rev\").tools.effective.join()")" = "Read,Grep,SendMessage" ]'
printf -- '---\nname: pk-rev\ndescription: |\n  Reviews pack\n  changes.\nmodel: opus\ntools:\n  - Read\n  - Grep\n---\nBody.\n' > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 a | block scalar is allowed, and the tools shown are the ones the contract judges" \
  '[[ "$(roleerr pk-rev)" != *pack-agent* ]] && [ "$(jget "o.roles.find(r => r.name === \"pk-rev\").tools.effective.join()")" = "Read,Grep" ] && [[ "$(roleerr pk-rev)" == *"missing-tool:SendMessage"* ]]'
printf -- '---\nname: pk-rev\ndescription: Reviews\nmodel: opus\ntools:\n  - Read\n  - Grep\n  # - Bash\n  - SendMessage\n---\nBody.\n' > "$PACKS/fm/agents/pk-rev.md"
showpath "$PACKS/fm"
check "P8 a file with warnings shows the tools the strict read found, the list the contract judged" \
  '[[ "$(roleerr pk-rev)" == *pack-agent-line* ]] && [ "$(jget "o.roles.find(r => r.name === \"pk-rev\").tools.effective.join()")" = "Read,Grep,SendMessage" ]'
rm -rf "$PACKS/fm"; cp -R "$PACKS/a" "$PACKS/fm"
printf -- '---\nname: pk-impl\ndescription: Builds\nmodel: opus\ntools: Read, Edit, Write, Bash, Agent, mcp__srv__run, SendMessage\n---\nBody.\n' > "$PACKS/fm/agents/pk-impl.md"
showpath "$PACKS/fm"
check "P8 the tools shown flag Bash, Agent, mcp__*, Write and Edit" '[ "$(jget "o.roles.find(r => r.name === \"pk-impl\").tools.flagged.join()")" = "Edit,Write,Bash,Agent,mcp__srv__run" ]'
mkdir -p "$FAKEHOME/.claude/agents"
printf -- '---\nname: own-rev\ndescription: my own reviewer\npermissionMode: acceptEdits\n"hooks": x\ntools: Read, Grep, SendMessage\n---\nMine.\n' > "$FAKEHOME/.claude/agents/own-rev.md"
rm_ role set own-rev --class review --level global
rolerow own-rev
check "P8 the same keys in a user's own bare-name file behave as today" \
  '[[ "$(jget o.status)" != UNAVAILABLE* ]] && [ "$(jget "o.findings.some(f => f.code.startsWith(\"pack\"))")" = false ] && [ "$(jget "\"from\" in o")" = false ]'

# ================================================================ P9 an uninstalled plugin
fresh
adopt gd pk@mk:pk-impl
installed '{}'
rolerow gd
check "P9 with its record removed, the row is excluded as pack-missing" '[[ "$(rowstate)" == pack-missing* ]]'
rm_ role remove gd
check "P9 ... and role remove still works" '[ $RC = 0 ] && ! filejs "$GLOBAL_CFG" "\"gd\" in (d.roles || {})"'
fresh
adopt gd pk@mk:pk-impl
installed "{\"pk@other\":[\"$PACKS/a\"]}"
rolerow gd
check "P9 the plugin installed only from another marketplace is pack-missing, never a stand-in" '[[ "$(rowstate)" == pack-missing* ]]'

# ================================================================ P10 pack show --path
fresh
rm -rf "$PACKS/new"; cp -R "$PACKS/a" "$PACKS/new"
mkdir -p "$PACKS/new/commands"; echo '{}' > "$PACKS/new/.mcp.json"; echo 'x' > "$PACKS/new/commands/go.md"
showpath "$PACKS/new"
check "P10 pack show --path on an uninstalled dir lists the roles" '[ $RC = 0 ] && [ "$(jget o.installed)" = false ] && [ "$(jget "o.roles.map(r => r.name).join()")" = "pk-impl,pk-rev,pk-des,pk-leg" ]'
check "P10 ... and also: the hooks dir, .mcp.json, commands/ and an agent not in the manifest" \
  '[ "$(jget "o.also_in_plugin.map(e => e.kind + \":\" + e.name).join()")" = "root:.mcp.json,root:bin,root:commands,root:hooks,agent:agents/pk-extra.md" ]'

# ================================================================ P12 symbolic links
fresh
adopt gd pk@mk:pk-impl
ln -s /etc/hosts "$PACKS/a/bin/link"
rolerow gd
check "P12 a symbolic link anywhere in the pack tree leaves the role available" '[ "$(rowstate)" = ok ]'
rm_ pack show pk
check "P12 ... and pack show warns of it" '[ $RC = 0 ] && [[ "$OUT" == *"warning: symbolic link: bin/link"* ]]'

# ================================================================ P13 ambiguous installs
fresh
adopt gd pk@mk:pk-impl
cp -R "$PACKS/a" "$PACKS/b"
installed "{\"pk@mk\":[\"$PACKS/a\"],\"pk@other\":[\"$PACKS/b\"]}"
rolerow gd
check "P13 two install records with identical content stay available" '[ "$(rowstate)" = ok ]'
echo changed > "$PACKS/b/bin/tool.sh"
rolerow gd
check "P13 two marketplaces with different content give pack-ambiguous" '[[ "$(rowstate)" == *pack-ambiguous* ]]'
installed "{\"pk@mk\":[\"$PACKS/a\",\"$PACKS/b\"]}"
rolerow gd
check "P13 ... and so do two versions" '[[ "$(rowstate)" == *pack-ambiguous* ]]'

# ================================================================ T1 a pack role spawns in any slot, live run or not (adr-46)
fresh
adopt prv pk@mk:pk-rev
adopt pdes pk@mk:pk-des
adopt pimpl pk@mk:pk-impl
# The built-in's override comes from a second plugin: one agent maps to exactly one role.
mkpack "$PACKS/c" pk2
installed "{\"pk@mk\":[\"$PACKS/a\"],\"pk2@mk\":[\"$PACKS/c\"]}"
adopt reviewer pk2@mk:pk-rev --level repo
check "T1 (setup) the built-in reviewer is overridden by a pack role" 'filejs "$REPO_CFG" "d.roles.reviewer.from === \"pk2@mk:pk-rev\""'
mutate "$REPO_CFG" 'd.roster = {route: "peer", members: [{role: "reviewer", model: "opus"}]}'
anchor
for r in prv pdes; do
  rm_ spawn-ad-hoc $r --model opus --route peer --auto-mode auto --dry-run --orchestrator-pid $$
  check "T1 with a live run: spawn-ad-hoc of pack role $r spawns" '[ $RC = 0 ]'
done
rm_ spawn-one reviewer --dry-run --orchestrator-pid $$
check "T1 with a live run: spawn-one of the pack-overridden reviewer, auto mode unset, spawns" '[ $RC = 0 ] && [[ "$OUT" == *"pk2:pk-rev"* ]]'

# ================================================================ P18 the content digest
fresh
rm_ pack show pk --json
D1=$(jget o.digest)
rm_ pack show pk --json
D2=$(jget o.digest)
rm -rf "$PACKS/copy"; cp -R "$PACKS/a" "$PACKS/copy"
showpath "$PACKS/copy"
D3=$(jget o.digest)
echo more >> "$PACKS/a/bin/tool.sh"
rm_ pack show pk --json
D4=$(jget o.digest)
check "P18 pack show's digest is stable for the same tree, installed or not, and changes with it" '[[ "$D1" =~ ^sha256:[0-9a-f]{64}$ ]] && [ "$D1" = "$D2" ] && [ "$D1" = "$D3" ] && [ "$D1" != "$D4" ]'

# ================================================================ P20 name claims
mkhelper() { # pack hp at $PACKS/h: one role, helper, with a strict agents/helper.md
  rm -rf "$PACKS/h"; mkdir -p "$PACKS/h/.claude-plugin" "$PACKS/h/agents"
  printf '{"name":"hp","version":"1.0.0","description":"helper pack"}\n' > "$PACKS/h/.claude-plugin/plugin.json"
  printf '{"version":1,"roles":{"helper":{"class":"implement","description":"Helps with pack work"}}}\n' > "$PACKS/h/ah-roles.json"
  printf -- '---\nname: helper\ndescription: Helps\nmodel: opus\ntools: Read, Grep, Edit, Write, Bash, SendMessage\n---\nHelp.\n' > "$PACKS/h/agents/helper.md"
}
FX20="$SANDBOX/fx20"; mkdir -p "$FX20"
cat > "$FX20/dq.md" <<'EOF'
---
"name": helper
---
x
EOF
cat > "$FX20/sq.md" <<'EOF'
---
'name': "helper"
---
x
EOF
cat > "$FX20/esc.md" <<'EOF'
---
"na\x6de": helper
---
x
EOF
cat > "$FX20/merge.md" <<'EOF'
---
name: other
<<: {name: helper}
---
x
EOF
cat > "$FX20/anchor.md" <<'EOF'
---
name: other
color: &a red
---
x
EOF
fresh
mkhelper
installed "{\"hp@mk\":[\"$PACKS/h\"]}"
adopt hl hp@mk:helper
rolerow hl
check "P20 (baseline) the helper role is available" '[ "$(rowstate)" = ok ]'
p20() { # <label> <path the finding names> <shell edit of $PACKS/h> [claims marked in pack show, as JSON]
  mkhelper; eval "$3"
  showpath "$PACKS/h"
  local codes msg marked
  codes=$(roleerr helper)
  msg=$(jget '(o.roles.find(r => r.name === "helper").findings.find(f => f.code === "pack-warning") || {}).message')
  marked=$(jget "(o.also_in_plugin.find(e => e.name === \"$2\") || {}).claims || null")
  local want=${4-'["helper"]'} where=$2
  rolerow hl
  check "P20 $1: a warning naming $2, marked in pack show, and the role still available" \
    '[[ "$codes" == *pack-warning* ]] && [[ "$codes" != *pack-invalid* ]] && [[ "$msg" == *"$where"* ]] && [ "$marked" = "$want" ] && [ "$(rowstate)" = ok ]'
}
p20 "another file declaring name: helper, with no tools" agents/zz.md 'printf -- "---\nname: helper\ndescription: sneaky\n---\nSneaky.\n" > "$PACKS/h/agents/zz.md"'
p20 "a file in a subdirectory of agents/ declaring it" agents/sub/zz.md 'mkdir -p "$PACKS/h/agents/sub"; printf -- "---\nname: helper\n---\nx\n" > "$PACKS/h/agents/sub/zz.md"'
p20 "a file named helper.md in a subdirectory, with no name" agents/sub/helper.md 'mkdir -p "$PACKS/h/agents/sub"; printf -- "---\ndescription: no name\n---\nx\n" > "$PACKS/h/agents/sub/helper.md"'
p20 "a double-quoted \"name\" key" agents/zz.md 'cp "$FX20/dq.md" "$PACKS/h/agents/zz.md"'
p20 "a single-quoted name key with a double-quoted value" agents/zz.md 'cp "$FX20/sq.md" "$PACKS/h/agents/zz.md"'
p20 "a quoted key with an escape in it" agents/zz.md 'cp "$FX20/esc.md" "$PACKS/h/agents/zz.md"'
p20 "a <<: merge key" agents/zz.md 'cp "$FX20/merge.md" "$PACKS/h/agents/zz.md"'
p20 "an &a anchor" agents/zz.md 'cp "$FX20/anchor.md" "$PACKS/h/agents/zz.md"'
p20 "another file declaring name: Helper" agents/zz.md 'printf -- "---\nname: Helper\n---\nx\n" > "$PACKS/h/agents/zz.md"'
p20 "another file declaring name: HELPER" agents/zz.md 'printf -- "---\nname: HELPER\n---\nx\n" > "$PACKS/h/agents/zz.md"'
p20 "agents/sub/HELPER.md with no name" agents/sub/HELPER.md 'mkdir -p "$PACKS/h/agents/sub"; printf -- "---\ndescription: no name\n---\nx\n" > "$PACKS/h/agents/sub/HELPER.md"'
p20 "an agent under a path in plugin.json's agents key" extra/zz.md 'mkdir -p "$PACKS/h/extra"; printf -- "---\nname: helper\n---\nx\n" > "$PACKS/h/extra/zz.md"; printf "{\"name\":\"hp\",\"version\":\"1.0.0\",\"agents\":[\"./extra/\"]}\n" > "$PACKS/h/.claude-plugin/plugin.json"'
p20 "an agents path outside the install path" ../elsewhere 'printf "{\"name\":\"hp\",\"version\":\"1.0.0\",\"agents\":\"../elsewhere\"}\n" > "$PACKS/h/.claude-plugin/plugin.json"' null
p20 "an agents value that isn't a path or a list of paths" agents 'printf "{\"name\":\"hp\",\"version\":\"1.0.0\",\"agents\":{\"a\":1}}\n" > "$PACKS/h/.claude-plugin/plugin.json"' null
mkhelper
printf -- '---\nname: other\ndescription: unrestricted\npermissionMode: bypassPermissions\nhooks: x\n---\nx\n' > "$PACKS/h/agents/other.md"
mkdir -p "$PACKS/h/skills/helper"; printf -- '---\nname: helper\ndescription: a skill\n---\nx\n' > "$PACKS/h/skills/helper/SKILL.md"
showpath "$PACKS/h"
C20=$(roleerr helper)
rolerow hl
check "P20 an unrestricted agent named other and a skill named helper leave the role valid" \
  '[[ "$C20" != *pack-invalid* ]] && [[ "$C20" != *pack-warning* ]] && [ "$(rowstate)" = ok ]'

echo
echo "SUMMARY: $PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
