#!/bin/bash
# agent-hierarchy — bin/ah, the sh shim for the agent-facing CLI: what it hands the native binary (the root, the verb tree, the
# exit code and stdio unchanged), what it refuses (a state home that is neither the default nor trusted), where the root comes from
# (its own location outside test mode), and what it says when there is no binary. The routing is
# bin/ah-trust.sh's, which tests/test-shim-trust.sh covers; these rows run the shim itself, against a recording fake binary in a
# test sandbox, and never write outside it.
# Usage: bash tests/test-shim-ah.sh   (exits 0 iff all rows pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
AH="$PLUGIN/bin/ah"
T=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
case "$T" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
hermetic_on_exit 'rm -rf "$T"'
T=$(cd "$T" && pwd -P)
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=${RC:-} OUT=${OUT:0:200} ERR=${ERR:0:200})"; fi
}

SB="$T/ah"; mkdir -p "$SB/bin" "$T/home" "$T/ln" "$T/fakepath"
ARGV="$T/argv"; ENVF="$T/env"; STDIN="$T/stdin"; CANARY="$T/canary"
# a fake native binary: records its argv, the plugin root it was given and its stdin; exits with FAKE_EXIT (default 0)
cat > "$SB/bin/ahd" <<EOF
#!/bin/sh
printf '%s\n' "\$@" > "$ARGV"
printf '%s\n' "\${AH_PLUGIN_ROOT-unset}" > "$ENVF"
cat > "$STDIN"
echo "binary-out"
echo "binary-err" >&2
exit \${FAKE_EXIT:-0}
EOF
chmod 755 "$SB/bin/ahd"
# run <env assignments...> -- <ah args...>  -> RC, OUT, ERR
run() {
  local envs=() a
  while [ "$#" -gt 0 ] && [ "$1" != -- ]; do envs+=("$1"); shift; done
  shift
  rm -f "$ARGV" "$ENVF" "$STDIN"
  ERR=$(mktemp "$T/err.XXXXXX")
  OUT=$(env -i PATH=/usr/bin:/bin HOME="$T/home" "${envs[@]}" sh "$AH" "$@" 2>"$ERR" </dev/null); RC=$?
  ERR=$(cat "$ERR")
}
argv_is() { [ "$(cat "$ARGV" 2>/dev/null)" = "$(printf '%s\n' "$@")" ]; }

# ---- the shim hands the binary the root and the verb tree, and the binary's exit code and output come back unchanged
run AH_TEST=1 AH_HOME="$SB" -- roster teams --cwd "$T"
check "test mode: the binary is run as <bin> --plugin-root <root> ah <args>" 'argv_is --plugin-root "$PLUGIN" ah roster teams --cwd "$T"'
check "test mode: stdout is the binary's" '[ "$OUT" = binary-out ] && [ $RC = 0 ]'
check "test mode: stderr is the binary's" '[ "$ERR" = binary-err ]'
check "the plugin root travels in the environment too" '[ "$(cat "$ENVF")" = "$PLUGIN" ]'
run AH_TEST=1 AH_HOME="$SB" FAKE_EXIT=7 -- msg send x --kind note
check "the binary's exit code is the shim's" '[ $RC = 7 ] && argv_is --plugin-root "$PLUGIN" ah msg send x --kind note'
printf 'stdin-text\n' | env -i PATH=/usr/bin:/bin HOME="$T/home" AH_TEST=1 AH_HOME="$SB" sh "$AH" msg fill >/dev/null 2>&1
check "stdin reaches the binary" '[ "$(cat "$STDIN")" = stdin-text ]'
for verb in gate push-guard hook; do
  run AH_TEST=1 AH_HOME="$SB" -- $verb one two
  check "ah $verb goes to the binary's ah tree" 'argv_is --plugin-root "$PLUGIN" ah '"$verb"' one two'
done

# ---- the root: test mode keeps what a test set; outside test mode it is the shim's own location
run AH_TEST=1 AH_HOME="$SB" AH_PLUGIN_ROOT="$T/otherroot" -- roster teams
check "test mode: an AH_PLUGIN_ROOT a test set is kept, and no --plugin-root is passed" '[ "$(cat "$ENVF")" = "$T/otherroot" ] && argv_is ah roster teams'
ln -s "$AH" "$T/ln/ah"
rm -f "$ARGV"; env -i PATH=/usr/bin:/bin HOME="$T/home" AH_TEST=1 AH_HOME="$SB" sh "$T/ln/ah" roster teams >/dev/null 2>&1 </dev/null
check "run through a symlink, the root is the plugin the link points into" 'argv_is --plugin-root "$PLUGIN" ah roster teams'

# AH_IMPL is not a choice any more: with a binary there, AH_IMPL=node still runs the binary
run AH_TEST=1 AH_HOME="$SB" AH_IMPL=node -- roster teams --cwd x
check "AH_IMPL=node is ignored: the binary runs" 'argv_is --plugin-root "$PLUGIN" ah roster teams --cwd x && [ "$OUT" = binary-out ]'

# ---- refusal: a state home that is neither the default nor trusted nor a test sandbox
run AH_HOME="$SB" -- roster teams
check "an untrusted AH_HOME outside test mode refuses with exit 2 and runs nothing" '[ $RC = 2 ] && [ ! -e "$ARGV" ] && [ -z "$OUT" ]'
check "the refusal is the home verdict's: ah did not run, and what to do" '[[ "$ERR" == *"is not trusted, so ah did not run"* ]] && [[ "$ERR" == *"nset AH_HOME"* ]]'
run AH_TEST=1 AH_HOME=/tmp -- roster teams
check "AH_TEST=1 with an AH_HOME outside an ahprobe sandbox is not test mode: refused" '[ $RC = 2 ] && [ ! -e "$ARGV" ]'
run AH_HOME=relative/ah -- msg list
check "a relative AH_HOME is refused" '[ $RC = 2 ] && [ ! -e "$ARGV" ]'
run AH_HOME="$SB" -- where
check "a verb the binary runs whatever the home is gets past the home verdict for an untrusted AH_HOME (then the binary, or the route's own refusal)" '[[ "$ERR" != *"is not trusted, so ah did not run"* ]]'

# ---- usage
run AH_TEST=1 AH_HOME="$SB" --
check "no arguments is a usage error with exit 2" '[ $RC = 2 ] && [[ "$ERR" == usage:* ]]'

# ---- the verbs the binary runs whatever the home is (the exemption list mirrors the binary's)
free() { env -i PATH=/usr/bin:/bin sh -c '. "$1"; shift; ah_verb_free "$@"' sh "$PLUGIN/bin/ah-trust.sh" "$@"; }
free ah where; RC=$?; check "ah where is exempt" '[ $RC = 0 ]'
free ah hook pretooluse-x; RC=$?; check "ah hook is exempt" '[ $RC = 0 ]'
for h in --help -h help; do free ah "$h"; RC=$?; check "ah $h is exempt" '[ $RC = 0 ]'; done
free ah login status; RC=$?; check "ah login status is exempt" '[ $RC = 0 ]'
free ah login run; RC=$?; check "ah login run is not (the nearest non-match of the row above)" '[ $RC = 1 ]'
free ah roster teams; RC=$?; check "ah roster is not exempt" '[ $RC = 1 ]'
free ah msg send; RC=$?; check "ah msg is not exempt" '[ $RC = 1 ]'

# ---- no binary: nothing runs, and the message names the install command with this plugin's own root
mkdir -p "$T/nobin/bin"
for verbline in "roster teams" "msg list" "push-guard check --branch b" "hook sessionstart"; do
  run AH_TEST=1 AH_HOME="$T/nobin" -- $verbline
  check "no binary: 'ah $verbline' exits 1 and names install.sh --apply" '[ $RC = 1 ] && [ ! -e "$ARGV" ] && [[ "$ERR" == *"no native binary is installed"* ]] && [[ "$ERR" == *"sh $PLUGIN/scripts/install.sh --apply"* ]]'
done
run AH_TEST=1 AH_HOME="$T/nobin" AH_IMPL=node PATH="$T/fakepath:/usr/bin:/bin" -- where
check "no binary, AH_IMPL=node: nothing is handed to node" '[ $RC = 1 ] && [ ! -e "$ARGV" ]'

# ---- a PATH the environment chose cannot put a tool of its own in front of the shim's lookups (the caller's PATH is restored only
# for the final exec)
HOSTILE="$T/hostile"; mkdir -p "$HOSTILE"
for tool in id find readlink dirname cd pwd uname cut tr wc; do
  printf '#!/bin/sh\necho %s-ran >> "%s"\nexec /usr/bin/%s "$@"\n' "$tool" "$CANARY" "$tool" > "$HOSTILE/$tool"; chmod 755 "$HOSTILE/$tool"
done
rm -f "$CANARY"
run AH_TEST=1 AH_HOME="$SB" PATH="$HOSTILE:/usr/bin:/bin" -- roster teams
check "a hostile caller PATH runs none of its tools in the shim's lookups" '[ ! -e "$CANARY" ] && argv_is --plugin-root "$PLUGIN" ah roster teams'

# ---- the shim file itself
check "bin/ah is executable and parses" '[ -x "$AH" ] && sh -n "$AH"'

echo "ah shim: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
