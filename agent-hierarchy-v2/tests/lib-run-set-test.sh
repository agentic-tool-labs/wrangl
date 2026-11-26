# agent-hierarchy — the shared setup of the tests/run-set.sh suites (tests/test-run-set-s<K>.sh): the sandbox, the scratch
# repository, the stand-ins for the clone's commands and the helpers. Each suite sources lib-hermetic.sh and lib-test-env.sh
# first, then this, then runs its own sections and ends with its PASS and FAIL count. The cases were one suite (tests/test-run-set.sh)
# until it ran too close to run-all's per-suite timeout; they are split by section, and no case depends on another suite's.
#   . "$(cd "$(dirname "$0")" && pwd)/lib-run-set-test.sh"

. "$(cd "$(dirname "$0")" && pwd)/lib-pid.sh"
# a run under run-all.sh inherits its held slot; this test starts from none
unset AH_TEST_SLOT_HELD
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
RUNSET="$PLUGIN/tests/run-set.sh"
TOOLS="$PLUGIN/tests/run-set-tools.mjs"
PFX="$(git -C "$PLUGIN" rev-parse --show-prefix)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:700})"; fi; }

export AH_RESULTS_DIR="$SB/results"
export AH_TEST_SLOT_WAIT=60
# the stand-in sets are tiny, so the percentage limit is off here; the cases of section 12 turn it on
export AH_SYSTEMIC_PCT=100
# reruns go through the machine-wide slot limiter; this test uses slots of its own
export AH_SLOTS_DIR="$SB/slots"
REPO="$SB/repo"
GIT() { git -C "$REPO" -c user.name=t -c user.email=t@t "$@"; }
mkdir -p "$REPO" && GIT init -q
mkdir -p "$REPO/${PFX}native/tests/Ah.Fake.Tests"
: > "$REPO/${PFX}native/tests/Ah.Fake.Tests/Ah.Fake.Tests.csproj"
mkdir -p "$REPO/${PFX}native/tests/Ah.Helper"; : > "$REPO/${PFX}native/tests/Ah.Helper/Ah.Helper.csproj"

# ---- the stand-ins, each told where to keep its state ($ST) and recording what it was called with
stubs() { # <state dir> <with-dotnet-script: yes|no>
  local st=$1 d="$REPO/${PFX}"
  mkdir -p "$d/scripts" "$d/tests" "$st"
  rm -f "$d/scripts/dotnet-test.sh"
  if [ "$2" = yes ]; then
    cat > "$d/scripts/dotnet-test.sh" <<'DOTNET'
#!/bin/bash
ST="@ST@"
echo "AH_TEST_SLOT_HELD=${AH_TEST_SLOT_HELD:-} $*" >> "$ST/dotnet.calls"
echo "split=${AH_TEST_HEAVY_SPLIT:-} projects=${AH_DOTNET_TEST_PROJECTS:-}" >> "$ST/dotnet.env"
pwd >> "$ST/dotnet.pwd"
if [ -f "$ST/dotnet.kill-job" ]; then
  # the job is the child of the topmost run-set.sh above this script
  c=$PPID; p=$(ps -o ppid= -p "$c" | tr -d ' ')
  while case "$(ps -o command= -p "$(ps -o ppid= -p "$p" | tr -d ' ')")" in */run-set.sh*) true;; *) false;; esac; do c=$p; p=$(ps -o ppid= -p "$c" | tr -d ' '); done
  kill -9 "$c"; sleep 1  # pid-owned: this stand-in's own ancestor, the job of the run-set this test started, read from the live process tree just now
fi
dir=""; filter=""; prev=""
for a in "$@"; do case "$prev" in --results-directory) dir=$a;; --filter) filter=$a;; esac; prev=$a; done
mkdir -p "$dir"
[ -f "$ST/dotnet.sleep" ] && sleep "$(cat "$ST/dotnet.sleep")"
[ -f "$ST/dotnet.build-fails" ] && { echo "build failed"; exit 1; }
trx() { # <passed> then the failed names
  local p=$1; shift; local n=0 defs="" res="" f
  for f in "$@"; do
    n=$((n + 1))
    defs="$defs<UnitTest name=\"m$n\" id=\"id$n\"><TestMethod codeBase=\"/x/Ah.Fake.Tests.dll\" className=\"${f%.*}\" name=\"${f##*.}\" /></UnitTest>"
    res="$res<UnitTestResult testId=\"id$n\" testName=\"m$n\" outcome=\"Failed\"><Output><ErrorInfo><Message>boom $f&#xA;second line</Message></ErrorInfo></Output></UnitTestResult>"
  done
  echo "<?xml version=\"1.0\"?><TestRun><TestDefinitions>$defs</TestDefinitions><Results>$res</Results><ResultSummary outcome=\"$(cat "$ST/dotnet.outcome" 2>/dev/null || { [ $# -gt 0 ] && echo Failed || echo Completed; })\"><Counters total=\"$((p + $#))\" executed=\"$((p + $#))\" passed=\"$p\" failed=\"$#\" /></ResultSummary></TestRun>" > "$dir/r-$$.trx"
}
if [ -n "$filter" ]; then
  name="${filter#FullyQualifiedName=}"
  echo "$name" >> "$ST/dotnet.reruns"
  echo "start $name" >> "$ST/dotnet.rerun-events"
  [ -f "$ST/dotnet.rerun-sleep" ] && sleep "$(cat "$ST/dotnet.rerun-sleep")"
  echo "end $name" >> "$ST/dotnet.rerun-events"
  if grep -qx "$name" "$ST/passes-alone" 2>/dev/null; then trx 1; exit 0; fi
  trx 0 "$name"; exit 1
fi
echo run >> "$ST/dotnet.runs"
fails=()
while read -r l; do [ -n "$l" ] && fails+=("$l"); done < "$ST/fails" 2>/dev/null
trx "$(cat "$ST/dotnet.passed" 2>/dev/null || echo 3)" ${fails[@]+"${fails[@]}"}
if [ -f "$ST/dotnet.aborted-project" ]; then
  echo '<TestRun><ResultSummary outcome="Aborted"><Counters total="0" passed="0" failed="0" /></ResultSummary></TestRun>' > "$dir/b-$$.trx"
fi
[ -f "$ST/dotnet.crash-log" ] && echo 'Test host process crashed'
[ -f "$ST/dotnet.says" ] && cat "$ST/dotnet.says"
[ "${#fails[@]}" = 0 ]
DOTNET
    sed -i.bak "s#@ST@#$st#" "$d/scripts/dotnet-test.sh"; rm -f "$d/scripts/dotnet-test.sh.bak"
  fi
  cat > "$d/tests/publish-bin.sh" <<'PUBLISH'
#!/bin/bash
ST="@ST@"
out=""; prev=""
for a in "$@"; do [ "$prev" = --out ] && out=$a; prev=$a; done
echo "publish $*" >> "$ST/publish.calls"
pwd >> "$ST/publish.pwd"
[ -f "$ST/publish.fails" ] && { echo "publish failed"; exit 2; }
mkdir -p "$out"; printf '#!/bin/bash\nexit 0\n' > "$out/ahd"; chmod +x "$out/ahd"; : > "$out/ahd-tui"
PUBLISH
  cat > "$d/tests/run-all.sh" <<'RUNALL'
#!/bin/bash
ST="@ST@"
# --limit NAME, as the real one: prints the seconds (here the content of shell.limit, none by default) and runs nothing
if [ "${1:-}" = --limit ]; then echo "${2:-}" >> "$ST/shell.limit-calls"; cat "$ST/shell.limit" 2>/dev/null; exit 0; fi
# --runnable NAME, as the real one: 1 for a name in shell.pruned
if [ "${1:-}" = --runnable ]; then grep -qx -- "${2:-}" "$ST/shell.pruned" 2>/dev/null && exit 1; exit 0; fi
echo "WAIT=${AH_RUN_ALL_LOCK_WAIT:-} LOCK=${AH_RUN_ALL_LOCK:-} AHD_NATIVE_BIN=${AHD_NATIVE_BIN:-} exe=$([ -x "${AHD_NATIVE_BIN:-/nonexistent}" ] && echo yes || echo no) $*" >> "$ST/shell.calls"
exact=""; out=""; only=""; prev=""
for a in "$@"; do case "$prev" in --exact) exact=$a;; -o) out=$a;; --only) only=$a;; esac; prev=$a; done
[ -f "$ST/shell.lock-timeout" ] && { echo "run-all: waited for the lock; giving up (exit 75)" >&2; exit 75; }
[ -n "$exact" ] && [ -f "$ST/shell.refuse-rerun" ] && { echo "run-all: refusing" >&2; exit 2; }
if [ -n "$exact" ]; then
  [ -f "$ST/dotnet.runs" ] || : >> "$ST/shell.rerun-before-dotnet-ended"
  echo "start $exact" >> "$ST/shell.events"
  [ -f "$ST/shell.rerun-sleep" ] && sleep "$(cat "$ST/shell.rerun-sleep")"
  echo "end $exact" >> "$ST/shell.events"
  if grep -qx "test-$exact" "$ST/shell.passes-alone" 2>/dev/null; then echo "test-$exact ok" > "$out"; exit 0; fi
  echo "test-$exact FAIL" > "$out"; exit 1
fi
rc=0; : > "$out"
suites="test-s1 test-s2 test-s3 $(cat "$ST/shell.extra" 2>/dev/null)"
[ "${!#}" = contracts-s ] && suites="test-contracts-s0 test-contracts-s1 test-contracts-s2 test-contracts-s3"
[ -n "$only" ] && suites="$(cat "$only")"
for t in $suites; do
  grep -qx "$t" "$ST/shell.drop" 2>/dev/null && continue
  if grep -qx "$t" "$ST/shell.fails" 2>/dev/null; then echo "$t FAIL" >> "$out"; rc=1; else echo "$t ok" >> "$out"; fi
done
if [ -f "$ST/shell.diff" ]; then mkdir -p "$AH_RUN_ALL_FAIL_DIR/test-s2/lifecycle"; cp "$ST/shell.diff" "$AH_RUN_ALL_FAIL_DIR/test-s2/lifecycle/12-x.diff"; fi
if [ -f "$ST/shell.log" ]; then echo "FAIL    test-s1 1s  log: $ST/shell.log"; fi
[ -f "$ST/shell.abort" ] && echo "run-all: aborted: systemic: 2 of 3 finished tests failed (stand-in)"
exit $rc
RUNALL
  local f
  for f in publish-bin run-all; do sed -i.bak "s#@ST@#$st#" "$d/tests/$f.sh"; rm -f "$d/tests/$f.sh.bak"; done
}
NEXT=0
mkcommit() { # <scenario> [no-dotnet]: a commit of stand-ins keeping state in $SB/st-<scenario>; sets SHA and ST
  NEXT=$((NEXT + 1)); ST="$SB/st-$1"
  stubs "$ST" "${2:-yes}"
  echo "$NEXT $1" > "$REPO/${PFX}marker"
  GIT add -A >/dev/null; GIT commit -q -m "$1 $NEXT"
  SHA="$(GIT rev-parse HEAD)"
}
rs() { OUT="$(bash "$RUNSET" "$REPO" "$@" 2>&1)"; RC=$?; }
calls() { [ -f "$ST/$1" ] && wc -l < "$ST/$1" | tr -d ' ' || echo 0; }
rjson() { node "$TOOLS" field "$AH_RESULTS_DIR/$SHA/$1/result.json" "$2"; }
nfail() { rjson "$1" failures | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const f=JSON.parse(s);console.log(f.length+":"+f.filter(x=>x.failed_twice).length)})'; }
vjson() { node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")); console.log(JSON.stringify(eval(process.argv[2])))' "$(ls -t "$AH_RESULTS_DIR/$SHA/$1"/verdict-*.json | head -1)" "$2"; }
change() { # <path>: a commit that changes only that file, stand-ins untouched
  NEXT=$((NEXT + 1)); mkdir -p "$REPO/${PFX}$(dirname "$1")"; echo "$NEXT" > "$REPO/${PFX}$1"
  GIT add -A >/dev/null; GIT commit -q -m "change $1"; SHA="$(GIT rev-parse HEAD)"
}
