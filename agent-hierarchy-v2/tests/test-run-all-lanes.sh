#!/bin/bash
# agent-hierarchy — the T0 additions to tests/run-all.sh: --impl, --lane, --with-serial, --changed, --first, --fail-fast,
# --tap and the lock rule for partial runs, run here on a throwaway plugin directory (a copy of the runner, a manifest, fake
# suites and a git history) so no real suite runs. Also: tests/manifest.json has exactly one entry per tests/test-*.sh.
# Usage: bash tests/test-run-all-lanes.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:700})"; fi; }

# ---- the sandbox plugin: runner copy, selection tool, leak lib, fake suites, manifest, history
P="$SB/plug"; LOG="$SB/events.log"; LK="$SB/lock"
mkdir -p "$P/tests/contract-tools" "$P/hooks"
cp "$PLUGIN/tests/run-all.sh" "$PLUGIN/tests/lib-daemon-leak.sh" "$P/tests/"
cp "$PLUGIN/tests/contract-tools/select.mjs" "$P/tests/contract-tools/"
: >"$P/hooks/x.mjs"; : >"$P/hooks/y.mjs"; : >"$P/hooks/lib-z.mjs"
fake() { printf '#!/bin/bash\necho "%s impl=${AH_IMPL-unset} tap=${AH_PARITY_TAP-none}" >>"%s"\n%s\n' "$1" "$LOG" "${2:-exit 0}" >"$P/tests/$1.sh"; }
fake test-a; fake test-b; fake test-c; fake test-d; fake test-s; fake test-n; fake test-u; fake test-mod-readonly
fake test-bad 'exit 1'
cat >"$P/tests/manifest.json" <<'EOF'
[
 {"name":"test-a","kind":"contract","libs":[],"covers":["hooks/x.mjs"],"units":[],"lane":"fast","reads_source":false},
 {"name":"test-b","kind":"contract","libs":[],"covers":["hooks/y.mjs","hooks/lib-z.mjs"],"units":[],"lane":"fast","reads_source":false},
 {"name":"test-c","kind":"static","libs":[],"covers":[],"units":[],"lane":"fast","reads_source":false},
 {"name":"test-d","kind":"contract","libs":[],"covers":["hooks/x.mjs"],"units":[],"lane":"full","reads_source":false},
 {"name":"test-s","kind":"contract","libs":[],"covers":["hooks/x.mjs"],"units":[],"lane":"serial","reads_source":false},
 {"name":"test-n","kind":"internal","libs":[],"covers":["hooks/lib-*.mjs"],"units":[],"lane":"fast","reads_source":false},
 {"name":"test-u","kind":"contract","libs":[],"covers":[],"units":["u1"],"lane":"fast","reads_source":false},
 {"name":"test-mod-readonly","kind":"contract","libs":[],"covers":["hooks/y.mjs"],"units":[],"lane":"full","reads_source":false},
 {"name":"test-bad","kind":"contract","libs":[],"covers":["hooks/y.mjs"],"units":[],"lane":"fast","reads_source":false}
]
EOF
git -C "$P" init -q -b main && git -C "$P" add -A && git -C "$P" -c user.name=t -c user.email=t@t commit -q -m base
BASE=$(git -C "$P" rev-parse HEAD)

printf '#!/bin/sh\nexit 0\n' >"$SB/native-stub"; chmod 755 "$SB/native-stub"
R() { AHD_NATIVE_BIN="${AHD_NATIVE_BIN-$SB/native-stub}" AH_RUN_ALL_LEAK=off AH_RUN_ALL_SETTLE=0 AH_RUN_ALL_LOCK="$LK" AH_RUN_ALL_SERIAL="${SER-test-s}" bash "$P/tests/run-all.sh" "$@" 2>&1; }
ran() { sed 's/ impl=.*//' "$LOG" | sort | tr '\n' ' ' | sed 's/ $//'; }
order() { sed 's/ impl=.*//' "$LOG" | tr '\n' ' ' | sed 's/ $//'; }
drop_kept() { local p; p=$(printf '%s\n' "$OUT" | sed -n 's/^run-all: kept \(\/private\/tmp\/ahprobe\.[A-Za-z0-9]\{6\}\).*/\1/p' | head -1); case "$p" in /private/tmp/ahprobe.??????) rm -rf "$p";; esac; }
mkdir_lock() { mkdir "$LK" && echo "held by the test" >"$LK/owner"; }
rmdir_lock() { rm -f "$LK/owner"; rmdir "$LK" 2>/dev/null; }

# ---- C0-7: the manifest has one entry per suite; the lane flag at its default selects today's set
OUT=$( cd "$PLUGIN/tests" && ls test-*.sh | sed 's/\.sh$//' | sort )
MAN=$( node -e 'const m=require(process.argv[1]); console.log(m.map(e=>e.name).sort().join("\n"))' "$PLUGIN/tests/manifest.json" 2>&1 )
check "C0-7: tests/manifest.json has exactly one entry per tests/test-*.sh" '[ "$OUT" = "$MAN" ]'
OUT=$( node -e 'const m=require(process.argv[1]); const n=m.map(e=>e.name); console.log(new Set(n).size===n.length ? "unique" : "duplicates")' "$PLUGIN/tests/manifest.json" )
check "...and no name appears twice" '[ "$OUT" = unique ]'
OUT=$( node -e '
const m=require(process.argv[1]); const bad=[];
for (const e of m) { for (const k of ["name","kind","libs","covers","units","lane","reads_source"]) if (!(k in e)) bad.push(e.name+":"+k);
  if (!["contract","mixed","internal","static"].includes(e.kind)) bad.push(e.name+":kind="+e.kind);
  if (!["fast","full","serial"].includes(e.lane)) bad.push(e.name+":lane="+e.lane);
  if (!Array.isArray(e.units) || e.units.some((u)=>typeof u!=="string")) bad.push(e.name+":units"); }
console.log(bad.length ? bad.join(" ") : "ok")' "$PLUGIN/tests/manifest.json" )
check "every manifest entry has the fields and values of the spec (units are lists of unit names)" '[ "$OUT" = ok ]'
SERIAL_REAL=$(bash "$PLUGIN/tests/run-all.sh" --serial-lane | sort | tr '\n' ' ' | sed 's/ $//')
SERIAL_MAN=$( node -e 'const m=require(process.argv[1]); console.log(m.filter(e=>e.lane==="serial").map(e=>e.name).sort().join(" "))' "$PLUGIN/tests/manifest.json" )
check "the manifest's serial lane is exactly run-all.sh's SERIAL_LANE" '[ "$SERIAL_REAL" = "$SERIAL_MAN" ]'

: >"$LOG"; OUT=$(R -j 2); rc=$?
ALLRAN=$(ran)
check "no new flag: every suite runs (the sandbox's whole set), serial lane included" '[ $rc != 3 ] && [ "$ALLRAN" = "test-a test-b test-bad test-c test-d test-mod-readonly test-n test-s test-u" ]'
drop_kept
: >"$LOG"; OUT=$(R -j 2 --lane all); rc=$?
check "C0-7: --lane all selects the same set as no flag" '[ "$(ran)" = "$ALLRAN" ]'
drop_kept

# ---- lanes
: >"$LOG"; OUT=$(R -j 2 --lane fast); drop_kept
check "--lane fast: the fast suites only (not full, not serial)" '[ "$(ran)" = "test-a test-b test-bad test-c test-n test-u" ]'
: >"$LOG"; OUT=$(R -j 2 --lane full); drop_kept
check "--lane full: the full suites only" '[ "$(ran)" = "test-d test-mod-readonly" ]'
printf 'test-a\ntest-d\n' >"$SB/only.txt"
: >"$LOG"; OUT=$(R -j 2 --only "$SB/only.txt"); drop_kept
check "--only FILE: the suites the file names and no others" '[ "$(ran)" = "test-a test-d" ]'
printf 'test-s\ntest-a\n' >"$SB/only.txt"
: >"$LOG"; OUT=$(R -j 2 --only "$SB/only.txt"); drop_kept
check "...a serial-lane suite it names still runs, last, in the serial lane" '[ "$(order)" = "test-a test-s" ]'
OUT=$(R --only "$SB/no-such-file"); ONLY_RC=$?
check "...a file that is not there is a usage error (exit 2)" '[ "$ONLY_RC" = 2 ]'
: >"$LOG"; OUT=$(R -j 2 --lane fast --with-serial); drop_kept
check "--lane fast --with-serial adds the serial suite, and it runs in the serial lane" '[ "$(ran)" = "test-a test-b test-bad test-c test-n test-s test-u" ] && echo "$OUT" | grep -q "serial lane (1 tests)"'
: >"$LOG"; OUT=$(R -j 2 --lane fast a); drop_kept
check "--lane with a PREFIX narrows both: fast suites starting with test-a" '[ "$(ran)" = "test-a" ]'
OUT=$(R --lane nope); rc=$?
check "--lane with an unknown value exits 2" '[ $rc = 2 ]'

# ---- C0-10: native is the only implementation, exported to the tests as AH_IMPL=native
: >"$LOG"; OUT=$(AHD_NATIVE_BIN="$SB/native-stub" R -j 1 --impl native a); drop_kept
check "C0-10: --impl native with a binary exports AH_IMPL=native into the test" 'grep -q "^test-a impl=native" "$LOG"'
# no native binary: nothing runs (never counted as passed), the reason is printed, and the run fails
: >"$LOG"; OUT=$(env -u AHD_NATIVE_BIN bash -c 'AH_RUN_ALL_LEAK=off AH_RUN_ALL_SETTLE=0 AH_RUN_ALL_LOCK="$0" bash "$1/tests/run-all.sh" -j 1 --impl native a 2>&1' "$LK" "$P"); rc=$?
check "C0-19 run-all --impl native with no binary: no suite runs, 'SKIPPED (no-native-binary)' is printed, exit 1" '[ $rc = 1 ] && [ ! -s "$LOG" ] && echo "$OUT" | grep -q "SKIPPED (no-native-binary)"'
: >"$LOG"; OUT=$(env -u AHD_NATIVE_BIN bash -c 'AH_RUN_ALL_LEAK=off AH_RUN_ALL_SETTLE=0 AH_RUN_ALL_LOCK="$0" bash "$1/tests/run-all.sh" -j 1 --impl native --require-native a 2>&1' "$LK" "$P"); rc=$?
check "...and with --require-native it exits 1 the same way" '[ $rc = 1 ] && [ ! -s "$LOG" ]'
: >"$LOG"; OUT=$(R -j 1 --impl node a); rc=$?
check "--impl node no longer exists: exit 2 and no suite runs" '[ $rc = 2 ] && [ ! -s "$LOG" ]'
: >"$LOG"; OUT=$(R -j 1 --impl manifest a); rc=$?
check "--impl manifest no longer exists: exit 2 and no suite runs" '[ $rc = 2 ] && [ ! -s "$LOG" ]'
: >"$LOG"; OUT=$(AH_IMPL=node R -j 1 a); drop_kept
check "no --impl flag: the default is native, whatever AH_IMPL the caller's environment had" 'grep -q "^test-a impl=native" "$LOG"'
OUT=$(R --impl wasm); rc=$?
check "--impl with an unknown value exits 2" '[ $rc = 2 ]'

# ---- C0-8: --changed selects by covers
echo "// edit" >>"$P/hooks/x.mjs"; git -C "$P" -c user.name=t -c user.email=t@t commit -qam "x"
: >"$LOG"; OUT=$(R -j 2 --changed "$BASE"); drop_kept
check "C0-8: a diff that touches hooks/x.mjs selects every suite whose covers contain it (serial included), and no suite with empty covers" '[ "$(ran)" = "test-a test-d test-s" ]'
echo "// edit" >>"$P/hooks/lib-z.mjs"; git -C "$P" -c user.name=t -c user.email=t@t commit -qam "z"
: >"$LOG"; OUT=$(R -j 2 --changed "$BASE" --lane fast); drop_kept
check "a glob in covers matches (hooks/lib-*.mjs), and --lane fast narrows the changed set" '[ "$(ran)" = "test-a test-b test-n" ]'
: >"$LOG"; OUT=$(R -j 2 --changed HEAD); rc=$?
check "an empty diff selects nothing: '0/0 passed', exit 0, nothing ran" '[ $rc = 0 ] && echo "$OUT" | grep -q "run-all: 0/0 passed" && [ ! -s "$LOG" ]'
mkdir -p "$P/native/proj"; echo x >"$P/native/proj/a.cs"; git -C "$P" add -A; git -C "$P" -c user.name=t -c user.email=t@t commit -qm "native"
: >"$LOG"; OUT=$(R -j 2 --changed HEAD~1); drop_kept
check "a native/** path selects the suites with a non-empty units, and no other" '[ "$(ran)" = "test-u" ]'
OUT=$(R --changed no-such-ref); rc=$?
check "a base that git does not know exits 2" '[ $rc = 2 ]'

# ---- C0-9: --first
printf 'test-bad FAIL\ntest-a ok\ntest-u TIMEOUT\n' >"$SB/first.txt"
: >"$LOG"; OUT=$(R -j 1 --lane fast --first "$SB/first.txt"); drop_kept
check "C0-9: --first runs the listed FAIL and TIMEOUT names first, in the file's order (and not the ok line)" '[ "$(order | cut -d" " -f1-2)" = "test-bad test-u" ]'
: >"$LOG"; OUT=$(R -j 1 --lane all --first "$SB/first.txt"); drop_kept
check "...ahead of SLOW_FIRST (test-mod-readonly starts after the listed ones)" '[ "$(order | cut -d" " -f1-3)" = "test-bad test-u test-mod-readonly" ]'
: >"$LOG"; OUT=$(R -j 1 --lane all); drop_kept
check "without --first, SLOW_FIRST leads (the nearest non-match)" '[ "$(order | cut -d" " -f1)" = "test-mod-readonly" ]'

# ---- --fail-fast
: >"$LOG"; OUT=$(R -j 1 --lane fast --fail-fast); rc=$?; drop_kept
check "--fail-fast: no test starts after the first failure; exit 1" '[ $rc = 1 ] && [ "$(order | tr " " "\n" | tail -1)" = test-bad ] && echo "$OUT" | grep -q "tests not started"'
: >"$LOG"; OUT=$(R -j 1 --lane fast); drop_kept
check "without --fail-fast every fast suite runs after the failure (nearest non-match)" '[ "$(ran)" = "test-a test-b test-bad test-c test-n test-u" ]'
OUT=$(R -j 1 --fail-fast); rc=$?
check "--fail-fast without --lane and --changed (the full lane) is refused, exit 2" '[ $rc = 2 ] && echo "$OUT" | grep -q "refused"'

# ---- --tap
mkdir -p "$SB/tap"
: >"$LOG"; OUT=$(R -j 1 --tap "$SB/tap" a); drop_kept
check "--tap exports AH_PARITY_TAP=DIR/<test-name> into each test" 'grep -q "^test-a impl=native tap=$SB/tap/test-a$" "$LOG"'
: >"$LOG"; OUT=$(R -j 1 a); drop_kept
check "without --tap the variable is not set (nearest non-match)" 'grep -q "impl=native tap=none" "$LOG"'

# ---- the lock rule
mkdir_lock
: >"$LOG"; OUT=$(R -j 2 --lane fast a); rc=$?; drop_kept
check "a --lane fast run takes no lock: with the lock held by another runner it runs and exits 0" '[ $rc = 0 ] && [ -s "$LOG" ]'
OUT=$(R -j 2 --lane fast --with-serial s); rc=$?
check "...with --with-serial the selection holds a serial suite: the lock is wanted and the run exits 3" '[ $rc = 3 ]'
OUT=$(AH_RUN_ALL_LOCK_FAST=1 R -j 2 --lane fast); rc=$?
check "...AH_RUN_ALL_LOCK_FAST=1 asks a fast run to take the lock: exit 3" '[ $rc = 3 ]'
OUT=$(R -j 2 a); rc=$?
check "a run with no new flag still takes the lock: exit 3 while it is held" '[ $rc = 3 ]'
[ -f "$LK/owner" ] && [ "$(cat "$LK/owner")" = "held by the test" ]
check "a lock someone else holds is never removed by a lock-free run" '[ -d "$LK" ] && [ "$(cat "$LK/owner")" = "held by the test" ]'
rmdir_lock

# ---- long tests: a listed suite gets at least its listed seconds, whatever -t says
cat >"$P/tests/test-sleepy.sh" <<'EOF'
#!/bin/bash
sleep 5
exit 0
EOF
node -e 'const fs=require("fs");const f=process.argv[1];const m=JSON.parse(fs.readFileSync(f,"utf8"));m.push({name:"test-sleepy",kind:"static",libs:[],covers:[],units:[],lane:"fast",reads_source:false});fs.writeFileSync(f,JSON.stringify(m))' "$P/tests/manifest.json"
RES="$SB/res-long.txt"
res_of() { awk -v n="$1" '$1 == n { print $2 }' "$RES"; }
OUT=$(AH_RUN_ALL_LONG="" R -j 1 -t 2 -o "$RES" sleepy); drop_kept
check "a suite that outlives -t 2 and is not listed times out" '[ "$(res_of test-sleepy)" = TIMEOUT ]'
OUT=$(AH_RUN_ALL_LONG="test-sleepy 30" R -j 1 -t 2 -o "$RES" sleepy); drop_kept
check "...and is allowed its listed 30 s when it is listed" '[ "$(res_of test-sleepy)" = ok ]'
OUT=$(AH_RUN_ALL_LONG="test-sleepy-other 30" R -j 1 -t 2 -o "$RES" sleepy); drop_kept
check "...a listing for another name does not apply (nearest non-match)" '[ "$(res_of test-sleepy)" = TIMEOUT ]'
OUT=$(bash "$PLUGIN/tests/run-all.sh" --long-tests)
check "run-all.sh lists test-mod-readonly with at least 600 s by default" 'printf %s "$OUT" | awk "\$1 == \"test-mod-readonly\" && \$2 >= 600 { f = 1 } END { exit !f }"'
OUT=$(AH_RUN_ALL_LONG="test-sleepy 450" bash "$PLUGIN/tests/run-all.sh" --limit test-sleepy)
check "--limit prints a listed suite's floor when it is above the default" '[ "$OUT" = 450 ]'
OUT=$(AH_RUN_ALL_LONG="test-sleepy 450" bash "$PLUGIN/tests/run-all.sh" --limit test-sleepy-other)
check "...and the 300 s default for a name that is not listed (nearest non-match)" '[ "$OUT" = 300 ]'
OUT=$(AH_RUN_ALL_LONG="test-sleepy 30" bash "$PLUGIN/tests/run-all.sh" --limit test-sleepy)
check "...and the default when the listed floor is below it" '[ "$OUT" = 300 ]'

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
