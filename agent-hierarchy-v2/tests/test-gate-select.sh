#!/bin/bash
# agent-hierarchy — tests/contract-tools/gate-select.mjs, the ship gate's affected-only selection: a change in a project selects the test
# projects that reach it through ProjectReference, transitively, and nothing else; a path it cannot map asks for the full suite.
# Hermetic: a temp git repo with two src projects (B references A) and their test projects.
# Usage: bash tests/test-gate-select.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d "${TMPDIR:-/tmp}/ahprobe.XXXXXX")"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (RC=$RC OUT=$OUT)"; fi; }
G() { git -C "$SB" -c user.name=t -c user.email=t@t -c commit.gpgsign=false "$@"; }
proj() { mkdir -p "$SB/native/$1"; printf '<Project>%s</Project>\n' "$2" > "$SB/native/$1/$(basename "$1").csproj"; echo "class C {}" > "$SB/native/$1/C.cs"; }
ref() { printf '<ProjectReference Include="%s" />' "$@"; }

G init -q
proj src/Ah.A ""
proj src/Ah.B "$(ref ../Ah.A/Ah.A.csproj)"
proj tests/Ah.A.Tests "$(ref ../../src/Ah.A/Ah.A.csproj)"
proj tests/Ah.B.Tests "$(ref ../../src/Ah.B/Ah.B.csproj)"
proj src/Ah.G ""
echo '<Project/>' > "$SB/native/Directory.Build.props"
printf '<Project><ProjectReference Include="$(AhNativeRoot)src/Ah.G/Ah.G.csproj" OutputItemType="Analyzer" /></Project>\n' > "$SB/native/Directory.Build.targets"
G add -A; G commit -qm base; G tag base

sel() { OUT="$(node "$PLUGIN/tests/contract-tools/gate-select.mjs" --repo "$SB" --base "$1" --sha HEAD 2>&1)"; RC=$?; }

# a change in B selects B's tests only; a change in A reaches B's tests through B, transitively
echo "class D {}" > "$SB/native/src/Ah.B/D.cs"; G add -A; G commit -qm b; G tag b
sel base
check "change in B selects only Ah.B.Tests" '[ $RC = 0 ] && [ "$OUT" = "dotnet native/tests/Ah.B.Tests/Ah.B.Tests.csproj" ]'
echo "class E {}" > "$SB/native/src/Ah.A/E.cs"; G add -A; G commit -qm a
sel b
check "change in A selects both test projects" '[ $RC = 0 ] && [ "$OUT" = "$(printf "dotnet native/tests/Ah.A.Tests/Ah.A.Tests.csproj\ndotnet native/tests/Ah.B.Tests/Ah.B.Tests.csproj")" ]'

# negative: a build file belongs to no project, so the gate runs the full suite
echo '<Project><!-- x --></Project>' > "$SB/native/Directory.Build.props"; G add -A; G commit -qm props
sel base
check "Directory.Build.props gives the full suite" '[ $RC = 3 ] && [ "$OUT" = "full native/Directory.Build.props" ]'

# negative: a rerun on the same sha has nothing in its diff, and that is never "run nothing"
sel HEAD
check "an empty change set gives the full suite" '[ $RC = 3 ] && [ "$OUT" = "full (empty change set)" ]'

# negative: a project Directory.Build.targets pulls into every project, though no csproj names it
G tag pre-g; echo "class F {}" > "$SB/native/src/Ah.G/F.cs"; G add -A; G commit -qm g
sel pre-g
check "a change in a project every project gets gives the full suite" '[ $RC = 3 ] && [ "$OUT" = "full native/src/Ah.G/F.cs (pulled into every project by a Directory.Build file)" ]'

# negative: an Include rooted in a property the selector cannot resolve could pull in anything
G tag pre-c; proj src/Ah.C '<Compile Include="$(Other)x.cs" />'; G add -A; G commit -qm c
sel pre-c
check "an unresolvable \$(...) Include gives the full suite" '[ $RC = 3 ] && [ "$OUT" = "full native/src/Ah.C/Ah.C.csproj: Compile \$(Other)x.cs cannot be resolved" ]'

echo "gate-select: $PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
