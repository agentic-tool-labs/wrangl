#!/bin/bash
# agent-hierarchy — the Codex login handling of tests/lib-probe.sh, driven with a FAKE login in a fake HOME (no codex, no real
# auth, no network): a signal during cleanup leaves no login value on disk and the sandbox is still removed; the leak check looks
# for every string value in auth.json (not a fixed list of keys) and reports a planted one; a login with nothing extractable
# fails the check instead of passing it. PROBE_LIB names another lib-probe.sh to run the same cases against.
# Usage: bash tests/test-probe-cleanup.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
LIB="${PROBE_LIB:-$PLUGIN/tests/lib-probe.sh}"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
LEFT=""
hermetic_on_exit 'for d in $LEFT; do case "$d" in /private/tmp/ahprobe.??????) rm -rf "$d";; esac; done; rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:300})"; fi; }

# The fake login. Values are obviously fake and share no prefix with a real key.
FAKEHOME="$SB/home"
mkdir -p "$FAKEHOME/.codex"
cat >"$FAKEHOME/.codex/auth.json" <<'JSON'
{"auth_mode":"chatgpt","OPENAI_API_KEY":"FAKEAPIKEY-aaaaaaaaaaaaaaaaaaaa","tokens":{"id_token":"FAKEIDTOKEN.bbbbbbbbbbbb.cccccccccccc","access_token":"FAKEACCESS-dddddddddddddddddddd","refresh_token":"FAKEREFRESH-eeeeeeeeeeeeeeeeeeee","account_id":"fake-account-ffff-ffff-ffff"},"last_refresh":"2026-01-01T00:00:00.000Z"}
JSON
chmod 0600 "$FAKEHOME/.codex/auth.json"
# Every string value of 16 characters or more that is not a timestamp, with the key it sits under: "<key>\t<value>".
node -e 'const j=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));const out=[];(function w(o,k){if(typeof o==="string"){if(o.length>=16&&!/^\d{4}-\d{2}-\d{2}T/.test(o))out.push(k+"\t"+o);}else if(o&&typeof o==="object")for(const x of Object.keys(o))w(o[x],x);})(j,"");process.stdout.write(out.join("\n")+"\n");' "$FAKEHOME/.codex/auth.json" >"$SB/values.tsv"
NVALUES=$(wc -l <"$SB/values.tsv" | tr -d ' ')

# The driver: sources the lib, takes the sandbox, then does what MODE says and exits so the EXIT trap runs the cleanup.
cat >"$SB/driver.sh" <<'DRV'
#!/bin/bash
LIB=$1; MODE=$2; TFILE=$3; PLANT=${4:-}; REL=${5:-}
PROBE_NEED_CLAUDE=0
PROBE_NEED_CODEX=0
PROBE_NEED_CODEX_AUTH=1
. "$LIB"
PROBE_NAME=cleanuptest
probe_init
echo "$T" >"$TFILE"
case "$MODE" in
  signal) tm() { kill -INT $$; return 0; } ;;
  plant) printf '%s\n' "$PLANT" >"$T/ws/planted.txt" ;;
  plantat) mkdir -p "$(dirname "$T/$REL")"; printf '%s\n' "$PLANT" >"$T/$REL" ;;
  late)
    # A process that ignores TERM and writes a new login (and a copy of its value into the workspace) every time the file is
    # removed, as a helper that outlives its server would. Only kill -9 stops it.
    bash -c 'T=$1; n=0; v=0; trap "n=\$((n+1))" TERM; while :; do if [ "$n" -ge 1 ] && [ ! -e "$T/codex/auth.json" ]; then v=$((v+1)); echo "FAKELATE-$v-zzzzzzzzzzzzzzzz" >"$T/ws/late.txt"; printf "{\"k\":\"FAKELATE-%s-zzzzzzzzzzzzzzzz\"}" "$v" >"$T/codex/auth.json"; fi; sleep 0.05; done' _ "$T" &
    probe_record_pid $!
    ;;
esac
exit 0
DRV

# run <mode> [value to plant]: sets RC, ERR (stderr), TDIR (the run's sandbox); records the dir if it survived.
run() {
  local n=$((${RUNN:-0}+1)); RUNN=$n
  HOME="$FAKEHOME" bash "$SB/driver.sh" "$LIB" "$1" "$SB/t-$n" "${2:-}" "${3:-}" >"$SB/out-$n" 2>"$SB/err-$n"
  RC=$?
  ERR=$(cat "$SB/err-$n")
  TDIR=$(cat "$SB/t-$n" 2>/dev/null)
  case "$TDIR" in /private/tmp/ahprobe.??????) [ -e "$TDIR" ] && LEFT="$LEFT $TDIR";; esac
}
# No file of the run's sandbox, if it survived, holds any of the fake values.
holds_no_value() {
  [ -n "$TDIR" ] || return 0
  [ -e "$TDIR" ] || return 0
  ! grep -rqF -f <(cut -f2 "$SB/values.tsv") "$TDIR" 2>/dev/null
}

check "the fake login yields several values to look for (not just access and refresh)" '[ "$NVALUES" -ge 5 ]'

run normal
check "a normal exit: rc 0, the sandbox is gone, no value in stderr" '[ $RC = 0 ] && [ -n "$TDIR" ] && [ ! -e "$TDIR" ] && ! printf %s "$ERR" | grep -qF -f <(cut -f2 "$SB/values.tsv")'

run signal
check "an INT during cleanup does not interrupt it: the sandbox is gone and holds no value" '[ -n "$TDIR" ] && [ ! -e "$TDIR" ] && holds_no_value'
check "...and the run's own exit status is kept" '[ $RC = 0 ]'

while IFS=$'\t' read -r key value; do
  run plant "$value"
  check "the leak check finds a planted value of key $key (reported, rc 1, sandbox gone)" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "CODEX LOGIN LEAK" && [ ! -e "$TDIR" ]'
  check "...and prints the key name $key, never the value" 'printf %s "$ERR" | grep -qF "key=$key" && ! printf %s "$ERR" | grep -qF "$value"'
done <"$SB/values.tsv"

# scope: Codex's own store is out of the check, everything else under the sandbox is in it
FIRSTV=$(head -1 "$SB/values.tsv" | cut -f2)
FIRSTK=$(head -1 "$SB/values.tsv" | cut -f1)
run plantat "$FIRSTV" "codex/sessions/rollout-copy.jsonl"
check "a login value inside Codex's own store (the throwaway CODEX_HOME) is not a leak: rc 0, an info line, the sandbox gone" '[ $RC = 0 ] && ! printf %s "$ERR" | grep -q "CODEX LOGIN LEAK" && printf %s "$ERR" | grep -q "info: Codex.s own store" && printf %s "$ERR" | grep -qF "$FIRSTK" && [ ! -e "$TDIR" ]'
check "...and the info line never prints the value" '! printf %s "$ERR" | grep -qF "$FIRSTV"'
for rel in "ws/capture.txt" "ah/logs/ahd.log" "ws/codex/nested.txt" "codex2/x.txt" "codex.bak/x.txt" "codexx/rollout.jsonl" "tui-capture.txt" "logs/probe.log"; do
  run plantat "$FIRSTV" "$rel"
  check "the same value at $rel is a leak: rc 1, the key name printed, the value not" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "CODEX LOGIN LEAK" && printf %s "$ERR" | grep -qF "key=$FIRSTK" && ! printf %s "$ERR" | grep -qF "$FIRSTV" && [ ! -e "$TDIR" ]'
done

# nearest non-match: a value that is not in the login is not reported
run plant "FAKE-NOT-IN-THE-LOGIN-0000000000000000"
check "a planted string that is not in the login is not a leak (nearest non-match)" '[ $RC = 0 ] && ! printf %s "$ERR" | grep -q "CODEX LOGIN LEAK"'

# a value holding line breaks: matched line by line, a short line or an empty one is never a pattern
printf '{"tokens":{"access_token":"FAKEMULTI-aaaaaaaaaaaaaaaa\\n\\nFAKEMULTI-bbbbbbbbbbbbbbbb\\nshort"}}\n' >"$FAKEHOME/.codex/auth.json"
chmod 0600 "$FAKEHOME/.codex/auth.json"
run normal
check "a login value with blank and short lines does not make every file a leak" '[ $RC = 0 ] && ! printf %s "$ERR" | grep -q "CODEX LOGIN LEAK"'
run plant "FAKEMULTI-bbbbbbbbbbbbbbbb"
check "...and a planted line of that value, not the first one, is still found" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "CODEX LOGIN LEAK"'
run plant "short"
check "...while its short line alone is not a pattern (nearest non-match)" '[ $RC = 0 ] && ! printf %s "$ERR" | grep -q "CODEX LOGIN LEAK"'

# a login rewritten after the first removal, by a process only kill -9 stops, is still read before it goes
printf '{"auth_mode":"chatgpt","k":"FAKEFIRST-aaaaaaaaaaaaaaaaaaaa"}\n' >"$FAKEHOME/.codex/auth.json"
chmod 0600 "$FAKEHOME/.codex/auth.json"
run late
check "a value a late process wrote to the login is found in the files it copied it to (reported, sandbox gone)" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "CODEX LOGIN LEAK" && [ ! -e "$TDIR" ]'
run normal
check "...and without that process a normal run is clean (nearest non-match)" '[ $RC = 0 ] && ! printf %s "$ERR" | grep -q "CODEX LOGIN LEAK"'

# a login with nothing to extract fails the check
printf '{"auth_mode":"chatgpt"}\n' >"$FAKEHOME/.codex/auth.json"
chmod 0600 "$FAKEHOME/.codex/auth.json"
run normal
check "a login with no value to look for fails the leak check instead of passing it" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "LEAK CHECK NOT DONE"'

# The fixtures' captured text goes through the same scrub as every other capture: the app-server log and `codex resume`'s stderr
# are read into a fixture only from their scrubbed copies.
# scrub itself removes the home path, the sandbox path and an email
OUT=$(HOME="$FAKEHOME" bash -c '. "$1"; REAL_HOME=$HOME; T=/private/tmp/ahprobe.ABCDEF; printf "a %s/x b a.b@example.com c %s/y\n" "$REAL_HOME" "$T" | scrub' _ "$LIB")
check "scrub removes the home path, the sandbox path and an email address" '! printf %s "$OUT" | grep -qE "$FAKEHOME|ahprobe.ABCDEF|example.com"'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
