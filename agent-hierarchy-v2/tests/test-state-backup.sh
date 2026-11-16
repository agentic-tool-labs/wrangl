#!/bin/bash
# agent-hierarchy — state backup and restore scripts (CO-1 .. CO-5, CO-7): dry-run default, what is copied and left
# out, every refusal, restore moving later state aside, and the static rules. Everything runs in a sandbox under
# /private/tmp/ahprobe.* with HOME, AH_HOME and WRANGL_HIERARCHY_DIR pointed into it; the real home is never read.
# Usage: bash tests/test-state-backup.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
BACKUP="$PLUGIN/scripts/state-backup.sh"
RESTORE="$PLUGIN/scripts/state-restore.sh"
SB=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
[ -n "$SB" ] || exit 1
HOLD_PIDS=""
cleanup() {
  for p in $HOLD_PIDS; do ah_signal_child TERM "$p"; done
  case "$SB" in /private/tmp/ahprobe.??????) rm -rf "$SB" ;; esac
}
hermetic_on_exit cleanup
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (rc=${RC:-?} OUT=${OUT:0:300})"; fi; }

HOME_DIR="$SB/home"
AH="$HOME_DIR/.claude/ah2"
HIER="$SB/hier"
mkdir -p "$AH/registrations" "$AH/mailbox/box1" "$AH/logs" "$AH/run" "$AH/trusted" "$HIER/teams" "$HIER/msgs"
printf '{"session":"s1"}\n' > "$AH/registrations/s1.json"
printf '{"id":1}\n{"id":2}\n' > "$AH/mailbox/box1/entries.jsonl"
printf 'log\n' > "$AH/logs/ahd.log"
printf '{"pin":"x"}\n' > "$AH/trusted/abc.json"
printf '{"name":"t"}\n' > "$HIER/teams/t.json"
printf 'hello\n' > "$HIER/msgs/m1.md"
printf '{"u":1}\n' > "$HOME_DIR/.claude/ah2/agent-hierarchy.usage.jsonl"
chmod 0600 "$AH/registrations/s1.json"
mkfifo "$AH/mailbox/box1/pipe"
printf '{"pid":999999,"at":1}' > "$AH/mailbox/box1/.domain.lock"   # a stale lock: no such pid
printf '{"pid":999999}' > "$AH/run/ahd.json"
printf 'paste' > "$AH/run/ah-paste-1.txt"
node -e 'const s=require("net").createServer(); s.listen(process.argv[1], () => process.kill(process.pid, "SIGKILL"))' "$AH/run/ahd.sock" 2>/dev/null
node -e 'const s=require("net").createServer(); s.listen(process.argv[1], () => process.kill(process.pid, "SIGKILL"))' "$AH/mailbox/box1/s.sock" 2>/dev/null

run() { # run <script> [args...]: sandboxed environment, working directory outside any checkout
  OUT=$(cd "$SB" && env -i PATH="$PATH" HOME="$HOME_DIR" AH_HOME="$AH" WRANGL_HIERARCHY_DIR="$HIER" bash "$@" 2>&1); RC=$?
}
# digest <dir> [exclude-prefix...]: sha256 over sorted "relpath mode content-hash" of regular files
digest() {
  node -e '
    const fs = require("fs"), path = require("path"), crypto = require("crypto");
    const [root, ...skip] = process.argv.slice(1);
    const rows = [];
    (function walk(d) {
      for (const n of fs.readdirSync(d)) {
        const p = path.join(d, n), rel = path.relative(root, p), st = fs.lstatSync(p);
        if (skip.some((s) => rel === s || rel.startsWith(s + "/") || n === ".domain.lock")) continue;
        if (st.isDirectory()) walk(p);
        else if (st.isFile()) rows.push(rel + " " + (st.mode & 0o7777).toString(8) + " " + crypto.createHash("sha256").update(fs.readFileSync(p)).digest("hex"));
      }
    })(root);
    console.log(crypto.createHash("sha256").update(rows.sort().join("\n")).digest("hex"));
  ' "$@"
}
count_files() { find "$1" -type f | wc -l | tr -d ' '; }
tree_sig() { # every path and size under the home, including specials: detects any change
  (cd "$HOME_DIR" && find . -print0 | LC_ALL=C sort -z | xargs -0 stat -f '%N %z %Lp' 2>/dev/null | shasum -a 256 | cut -d' ' -f1)
}

# ---- CO-1: dry run is the default
SIG0=$(tree_sig)
run "$BACKUP"
check "CO-1: no --apply prints the plan and exits 0" "[ $RC -eq 0 ] && printf '%s' \"\$OUT\" | grep -q 'dry run' && printf '%s' \"\$OUT\" | grep -q '$AH'"
check "CO-1: no --apply writes nothing" "[ \"\$(tree_sig)\" = \"$SIG0\" ]"
run "$BACKUP" --out "$SB/bk-dry"
check "CO-1: --out without --apply is still a dry run and creates nothing" "[ $RC -eq 0 ] && [ ! -e '$SB/bk-dry' ]"
run "$BACKUP" --apply
check "CO-1: --apply without --out exits 2" "[ $RC -eq 2 ]"
run "$BACKUP" --bogus
check "CO-1: an unknown option exits 2" "[ $RC -eq 2 ]"

# ---- CO-2: a backup of the populated home
SRC_AH_DIGEST=$(digest "$AH" run)
SRC_HIER_DIGEST=$(digest "$HIER")
run "$BACKUP" --apply --out "$SB/bk1"
check "CO-2: --apply --out copies and exits 0" "[ $RC -eq 0 ] && [ -f '$SB/bk1/BACKUP.txt' ]"
D_AH=$(awk -F '\t' -v p="$AH" '$1=="entry" && $5==p {print $3}' "$SB/bk1/BACKUP.txt")
D_HIER=$(awk -F '\t' -v p="$HIER" '$1=="entry" && $5==p {print $3}' "$SB/bk1/BACKUP.txt")
check "CO-2: AH_HOME and the hierarchy dir are recorded entries" "[ -n '$D_AH' ] && [ -n '$D_HIER' ]"
check "CO-2: the AH_HOME copy equals the source apart from what is left out" "[ \"\$(digest '$SB/bk1/payload/$D_AH')\" = '$SRC_AH_DIGEST' ]"
check "CO-2: the hierarchy copy equals the source" "[ \"\$(digest '$SB/bk1/payload/$D_HIER')\" = '$SRC_HIER_DIGEST' ]"
check "CO-2: the home-level file lives in the home, so the home copy carries it" "[ -f '$SB/bk1/payload/$D_AH/agent-hierarchy.usage.jsonl' ] && [ \"\$(find '$SB/bk1/payload' -maxdepth 1 -type f | wc -l | tr -d ' ')\" = 0 ]"
check "CO-2: BACKUP.txt records plugin version, git rev, time and paths" \
  "grep -q '^plugin_version=[0-9]' '$SB/bk1/BACKUP.txt' && grep -q '^git_rev=' '$SB/bk1/BACKUP.txt' && grep -q '^time=20' '$SB/bk1/BACKUP.txt' && grep -q '$AH' '$SB/bk1/BACKUP.txt'"
check "CO-2: run/ (pid file, socket, paste payload) is not in the payload" "[ ! -e '$SB/bk1/payload/$D_AH/run' ]"
check "CO-2: no socket or fifo is in the payload" "[ -z \"\$(find '$SB/bk1/payload' \\( -type s -o -type p \\))\" ]"
check "CO-2: the stale .domain.lock is not in the payload" "[ -z \"\$(find '$SB/bk1/payload' -name .domain.lock)\" ]"
check "CO-2: BACKUP.txt lists the socket and the fifo it left out" "grep -q 's.sock' '$SB/bk1/BACKUP.txt' && grep -q 'pipe' '$SB/bk1/BACKUP.txt'"
check "CO-2: a mode is kept (0600 stays 0600)" "[ \"\$(stat -f %Lp '$SB/bk1/payload/$D_AH/registrations/s1.json')\" = 600 ]"
check "CO-2: the source is untouched by the backup" "[ \"\$(digest '$AH' run)\" = '$SRC_AH_DIGEST' ] && [ -S '$AH/run/ahd.sock' ]"

# ---- CO-3: refusals
sleep 300 & HOLD=$!
HOLD_PIDS="$HOLD"
printf '{"pid":%s,"at":1}' "$HOLD" > "$AH/run/ahd.json"
run "$BACKUP" --apply --out "$SB/bk-live"
check "CO-3: a live daemon (pid in run/ahd.json) refuses with exit 1" "[ $RC -eq 1 ] && [ ! -e '$SB/bk-live' ] && printf '%s' \"\$OUT\" | grep -q 'live'"
printf '{"pid":999999}' > "$AH/run/ahd.json"
printf '{"pid":%s,"at":1}' "$HOLD" > "$AH/mailbox/box1/.domain.lock"
run "$BACKUP" --apply --out "$SB/bk-lock"
check "CO-3: a live .domain.lock refuses with exit 1" "[ $RC -eq 1 ] && [ ! -e '$SB/bk-lock' ]"
printf '{"pid":999999,"at":1}' > "$AH/mailbox/box1/.domain.lock"
run "$BACKUP" --apply --out "$AH/inside"
check "CO-3: --out inside a source exits 2" "[ $RC -eq 2 ] && [ ! -e '$AH/inside' ]"
run "$BACKUP" --apply --out /
check "CO-3: --out / exits 2" "[ $RC -eq 2 ]"
run "$BACKUP" --apply --out "$HOME_DIR"
check "CO-3: --out the home dir itself exits 2" "[ $RC -eq 2 ]"
run "$BACKUP" --apply --out "$HOME_DIR/.claude"
check "CO-3: --out above a source exits 2" "[ $RC -eq 2 ]"
mkdir -p "$SB/bk-full" && printf 'x' > "$SB/bk-full/keep"
run "$BACKUP" --apply --out "$SB/bk-full"
check "CO-3: a non-empty --out exits 1 and changes nothing" "[ $RC -eq 1 ] && [ \"\$(ls '$SB/bk-full')\" = keep ]"
# the nearest inputs that must NOT refuse: a stale lock and an existing empty --out
mkdir -p "$SB/bk-empty"
run "$BACKUP" --apply --out "$SB/bk-empty"
check "CO-3: a stale lock and an existing empty --out are accepted" "[ $RC -eq 0 ] && [ -f '$SB/bk-empty/BACKUP.txt' ]"
ah_signal_child TERM "$HOLD"; HOLD_PIDS=""

# ---- CO-4: restore after later activity
printf '{"session":"s1","changed":true}\n' > "$AH/registrations/s1.json"
printf '{"session":"s2"}\n' > "$AH/registrations/s2.json"
printf '{"op":1}\n' > "$AH/mailbox/box1/.executed-ops.jsonl"
printf 'late\n' > "$HIER/msgs/m2.md"
printf '{"u":1}\n{"u":2}\n' > "$HOME_DIR/.claude/ah2/agent-hierarchy.usage.jsonl"
PRE_AH=$(count_files "$AH"); PRE_HIER=$(count_files "$HIER")
run "$RESTORE" --from "$SB/bk1" --apply
check "CO-4: restore --apply exits 0" "[ $RC -eq 0 ]"
check "CO-4: AH_HOME equals the backed-up state" "[ \"\$(digest '$AH' run)\" = '$SRC_AH_DIGEST' ]"
check "CO-4: the hierarchy dir equals the backed-up state" "[ \"\$(digest '$HIER')\" = '$SRC_HIER_DIGEST' ]"
ASIDE_AH=$(ls -d "$AH".native.* 2>/dev/null | head -1)
ASIDE_HIER=$(ls -d "$HIER".native.* 2>/dev/null | head -1)
check "CO-4: the later state sits in <name>.native.<ts> next to each" "[ -d '$ASIDE_AH' ] && [ -d '$ASIDE_HIER' ]"
check "CO-4: the moved-aside dirs hold every file they held (nothing deleted)" "[ \"\$(count_files '$ASIDE_AH')\" = '$PRE_AH' ] && [ \"\$(count_files '$ASIDE_HIER')\" = '$PRE_HIER' ]"
check "CO-4: the later registration is in the moved-aside copy, not the restored one" "[ -f '$ASIDE_AH/registrations/s2.json' ] && [ ! -e '$AH/registrations/s2.json' ]"
check "CO-4: the restored home-level file is the backed-up one" "[ \"\$(wc -l < '$HOME_DIR/.claude/ah2/agent-hierarchy.usage.jsonl' | tr -d ' ')\" = 1 ]"
check "CO-4: the moved-aside home keeps the later home-level content" "[ \"\$(wc -l < '$ASIDE_AH/agent-hierarchy.usage.jsonl' | tr -d ' ')\" = 2 ]"

# ---- CO-5: restore refusals
SIG1=$(tree_sig)
run "$RESTORE" --from "$SB/bk1"
check "CO-5: no --apply prints the plan, exits 0, changes nothing" "[ $RC -eq 0 ] && printf '%s' \"\$OUT\" | grep -q 'dry run' && [ \"\$(tree_sig)\" = '$SIG1' ]"
mkdir -p "$SB/not-a-backup"
run "$RESTORE" --from "$SB/not-a-backup" --apply
check "CO-5: a --from without BACKUP.txt exits 2" "[ $RC -eq 2 ] && [ \"\$(tree_sig)\" = '$SIG1' ]"
run "$RESTORE" --apply
check "CO-5: no --from exits 2" "[ $RC -eq 2 ]"
sleep 300 & HOLD=$!
HOLD_PIDS="$HOLD"
mkdir -p "$AH/run"
printf '{"pid":%s,"at":1}' "$HOLD" > "$AH/run/ahd.json"
SIG2=$(tree_sig)
run "$RESTORE" --from "$SB/bk1" --apply
check "CO-5: a live daemon refuses with exit 1 and changes nothing" "[ $RC -eq 1 ] && [ \"\$(tree_sig)\" = '$SIG2' ]"
ah_signal_child TERM "$HOLD"; HOLD_PIDS=""
printf '{"pid":999999}' > "$AH/run/ahd.json"
# a payload that lost a file is not restored
mkdir -p "$SB/moved-out"
mv "$SB/bk1/payload/$D_HIER/msgs/m1.md" "$SB/moved-out/m1.md"
SIG3=$(tree_sig)
run "$RESTORE" --from "$SB/bk1" --apply
check "CO-5: a payload that does not match BACKUP.txt refuses with exit 1" "[ $RC -eq 1 ] && [ \"\$(tree_sig)\" = '$SIG3' ]"
mv "$SB/moved-out/m1.md" "$SB/bk1/payload/$D_HIER/msgs/m1.md"
run "$RESTORE" --from "$SB/bk1" --apply
check "CO-5: the same backup with the file back restores again (a second aside gets its own name)" "[ $RC -eq 0 ] && [ \"\$(ls -d '$AH'.native.* | wc -l | tr -d ' ')\" -ge 2 ]"

# ---- the plan names what it copies and, separately, what it did not find or does not copy
# listing_ok <script>: a dry run of that script prints the found directories, then the "not found / not copied" section
# naming run/, the specials, and every repo-local store
listing_ok() {
  local out
  out=$(cd "$SB" && env -i PATH="$PATH" HOME="$HOME_DIR" AH_HOME="$AH" WRANGL_HIERARCHY_DIR="$HIER" bash "$1" 2>&1) || return 1
  local head_line src_line
  head_line=$(printf '%s\n' "$out" | grep -n 'not found / not copied:' | head -1 | cut -d: -f1)
  src_line=$(printf '%s\n' "$out" | grep -n "^  d0  ah_home  $AH" | head -1 | cut -d: -f1)
  [ -n "$head_line" ] && [ -n "$src_line" ] && [ "$src_line" -lt "$head_line" ] || return 1
  local tail_text
  tail_text=$(printf '%s\n' "$out" | sed -n "$head_line,\$p")
  for want in 'run/' '.domain.lock' 'sockets, fifos' 'agent-hierarchy.json' 'standing instructions' 'scaffolds' 'intake'; do
    printf '%s' "$tail_text" | grep -q -- "not copied:.*$want" || return 1
  done
  printf '%s' "$tail_text" | grep -q 'not found:.*trusted-hierarchy-dirs' || return 1
}
check "plan: found directories first, then a separate not found / not copied list naming run/, specials and the repo-local stores" "listing_ok '$BACKUP'"
check "plan: the repo-local stores are never in the backup record" \
  "! grep -q 'agent-hierarchy.json' '$SB/bk1/BACKUP.txt'"

# can fail: a script that drops the listing is rejected by the check above
mkdir -p "$SB/mut/scripts"
sed '/not found \/ not copied/d; /  not copied:/d' "$BACKUP" > "$SB/mut/scripts/state-backup.sh"
check "can fail: a backup script without the not-copied listing is rejected" "! listing_ok '$SB/mut/scripts/state-backup.sh'"
sed '/<repo>\/.claude\/ah2\/agent-hierarchy.json/d' "$BACKUP" > "$SB/mut/scripts/state-backup-no-repo.sh"
check "can fail: a listing without the repo-local stores is rejected" "! listing_ok '$SB/mut/scripts/state-backup-no-repo.sh'"
check "can fail: the mutated copies differ from the script (the sed did something)" \
  "! cmp -s '$BACKUP' '$SB/mut/scripts/state-backup.sh' && ! cmp -s '$BACKUP' '$SB/mut/scripts/state-backup-no-repo.sh'"

# ---- trusted-hierarchy-dirs: copied when present, never an error when absent, never removed by a restore
printf '%s\n' "$SB/trusted-here" > "$AH/trusted-hierarchy-dirs"
run "$BACKUP" --apply --out "$SB/bk-thd"
check "trusted-hierarchy-dirs inside AH_HOME is copied with it" \
  "[ $RC -eq 0 ] && [ -f '$SB/bk-thd/payload/d0/trusted-hierarchy-dirs' ]"

AH2="$SB/ah2"
mkdir -p "$AH2/registrations" "$HOME_DIR/.claude/ah2"
printf '{"session":"s9"}\n' > "$AH2/registrations/s9.json"
THD_HOME="$HOME_DIR/.claude/ah2/trusted-hierarchy-dirs"
run_ah2() { OUT=$(cd "$SB" && env -i PATH="$PATH" HOME="$HOME_DIR" AH_HOME="$AH2" bash "$@" 2>&1); RC=$?; }
mv "$AH/trusted-hierarchy-dirs" "$SB/thd-aside"
run_ah2 "$BACKUP" --apply --out "$SB/bk-ah2-absent"
check "trusted-hierarchy-dirs absent: the backup succeeds and says it was not found" \
  "[ $RC -eq 0 ] && printf '%s' \"\$OUT\" | grep -q 'not found:.*trusted-hierarchy-dirs' && ! grep -q 'trusted-hierarchy-dirs' '$SB/bk-ah2-absent/BACKUP.txt'"
printf '%s\n' "$SB/trusted-here" > "$THD_HOME"
run_ah2 "$BACKUP" --apply --out "$SB/bk-ah2-present"
check "trusted-hierarchy-dirs beside a different AH_HOME is its own file entry" \
  "[ $RC -eq 0 ] && awk -F '\t' -v p='$THD_HOME' '\$1==\"entry\" && \$5==p {f=1} END{exit f?0:1}' '$SB/bk-ah2-present/BACKUP.txt'"
printf 'later\n' > "$SB/later-thd"
cp "$SB/later-thd" "$THD_HOME"
OUT=$(cd "$SB" && env -i PATH="$PATH" HOME="$HOME_DIR" AH_HOME="$AH2" bash "$RESTORE" --from "$SB/bk-ah2-absent" --apply 2>&1); RC=$?
check "restore never removes a trusted-hierarchy-dirs file the backup did not hold" \
  "[ $RC -eq 0 ] && [ -f '$THD_HOME' ] && cmp -s '$THD_HOME' '$SB/later-thd'"

# ---- CO-7: static rules
check "CO-7: neither script contains rm" "! grep -nE '(^|[^[:alnum:]_.-])rm([^[:alnum:]_-]|$)' '$BACKUP' '$RESTORE'"
check "CO-7: neither script names the retired writer" "! grep -nE 'state-writer|writer_mismatch' '$BACKUP' '$RESTORE'"
check "CO-7: both default to a dry run" "grep -q '^apply=false' '$BACKUP' && grep -q '^apply=false' '$RESTORE'"
check "CO-7: both parse as bash" "bash -n '$BACKUP' && bash -n '$RESTORE'"
check "CO-7: --help prints the header and exits 0" "bash '$BACKUP' --help | grep -q 'Usage:' && bash '$RESTORE' --help | grep -q 'Usage:'"

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
