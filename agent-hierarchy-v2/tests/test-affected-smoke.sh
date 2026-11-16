#!/bin/bash
# agent-hierarchy — tests/affected-smoke.sh selects the test classes and shell suites a diff reaches, refuses a selection that is
# too broad, runs nothing under --print, and leaves the shell selection to run-all.sh --changed. Hermetic: every case is a temp
# repo with a fake `dotnet`, a fake run-all.sh and a fake with-slot.sh that only record their arguments. Each check that guards a
# condition has its mutant: the same case run on a copy of the selector with that one condition broken.
# Usage: bash tests/test-affected-smoke.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (RC=$RC OUT=${OUT:0:600})"; fi; }
G() { git -C "$FX" -c user.name=t -c user.email=t@t -c commit.gpgsign=false "$@"; }

mkdir -p "$SB/bin"
cat > "$SB/bin/dotnet" <<'SH'
#!/bin/bash
echo "dotnet $*" >> "$AH_CALLS"
SH
chmod +x "$SB/bin/dotnet"
export AH_CALLS="$SB/calls.log"

# a test class: <project> <class> <text it contains>
tclass() {
  printf 'namespace %s;\npublic sealed class %s\n{\n    [Fact] public void A() { %s }\n}\n' "$1" "$2" "$3" > "$FX/native/tests/$1/$2.cs"
}

# fixture: a plugin root with the selector, the smoke, fakes, one suite and a few sources and tests; committed and tagged `base`
fixture() {
  FX="$SB/fx$((++N))"
  mkdir -p "$FX/tests/contract-tools" "$FX/native/src/Ah.Mesh" "$FX/native/src/Ah.Other" "$FX/docs" \
    "$FX/native/tests/Ah.Mesh.Tests" "$FX/native/tests/Ah.Core.Tests" "$FX/native/tests/Ah.Config.Tests" "$FX/native/tests/Ah.Architecture.Tests"
  cp "$PLUGIN/tests/affected-smoke.sh" "$FX/tests/"
  cp "$PLUGIN/tests/contract-tools/affected-select.mjs" "$PLUGIN/tests/contract-tools/select.mjs" "$FX/tests/contract-tools/"
  printf '#!/bin/bash\nshift 2\nexec "$@"\n' > "$FX/tests/with-slot.sh"
  printf '#!/bin/bash\necho "run-all $*" >> "$AH_CALLS"\n' > "$FX/tests/run-all.sh"
  chmod +x "$FX/tests/with-slot.sh" "$FX/tests/run-all.sh"
  mkdir -p "$FX/native/src/Ah.Roster"
  printf 'namespace Ah.Roster;\npublic sealed class RosterThing\n{\n    public int Pick() => 1;\n}\n' > "$FX/native/src/Ah.Roster/X.cs"
  printf '[{"name":"test-x","kind":"static","libs":[],"covers":["misc/**"],"units":["daemon"],"lane":"fast","reads_source":false},{"name":"test-r","kind":"static","libs":[],"covers":["native/src/Ah.Roster/**"],"units":["daemon"],"lane":"fast","reads_source":false},{"name":"test-contracts","kind":"static","libs":[],"covers":["tests/contracts/**"],"units":[],"lane":"fast","reads_source":false}]\n' > "$FX/tests/manifest.json"
  printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 1;\n    private int Hidden() => 2;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
  printf 'namespace Ah.Mesh;\npublic sealed partial class Foo\n{\n    public int Bazzle() => 1;\n}\n' > "$FX/native/src/Ah.Mesh/Foo.Bar.cs"
  printf 'namespace Ah.Other;\npublic sealed class Gizmo\n{\n    public int Turn() => 1;\n}\n' > "$FX/native/src/Ah.Other/Gizmo.cs"
  tclass Ah.Mesh.Tests UsesWidgetTests 'var w = new Widget();'
  tclass Ah.Mesh.Tests UsesFooTests 'var f = new Foo();'
  tclass Ah.Mesh.Tests UnrelatedTests 'var n = 1;'
  tclass Ah.Mesh.Tests GizmoTests 'var g = 1;'
  tclass Ah.Core.Tests CoreTests 'var c = 1;'
  tclass Ah.Config.Tests ConfigTests 'var c = 1;'
  tclass Ah.Architecture.Tests ArchTests 'var a = 1;'
  git -C "$FX" init -q
  G add -A >/dev/null
  G commit -qm base
  G tag base
}

change() { G add -A >/dev/null; G commit -qm change; }

# run the smoke of the fixture: smoke [args...] -> RC, OUT; the calls are in $AH_CALLS
smoke() {
  : > "$AH_CALLS"
  OUT=$(cd "$FX" && PATH="$SB/bin:$PATH" AHD_NATIVE_BIN="${NATIVE_BIN-/usr/bin/true}" bash "$FX/tests/affected-smoke.sh" "$@" 2>&1); RC=$?
}
calls() { cat "$AH_CALLS"; }
listed() { echo "$OUT" | grep -q "^  Ah\.[A-Za-z]*\.Tests\.$1\b"; }

# a copy of the selector with one condition broken: mutate <from> <to> (the text must be there)
mutate() {
  node -e 'const fs=require("fs");const f=process.argv[1];const t=fs.readFileSync(f,"utf8");if(!t.includes(process.argv[2])){console.error("mutant text not found");process.exit(1)}fs.writeFileSync(f,t.split(process.argv[2]).join(process.argv[3]))' \
    "$FX/tests/contract-tools/affected-select.mjs" "$1" "$2"
}

# S1. a type a test class names changes, the test file does not: the class is selected; one that does not name it is not
fixture
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 2;\n    private int Hidden() => 2;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --print --base base
check "S1 a class that names a changed type is selected though its file did not change" '[ $RC = 0 ] && listed UsesWidgetTests'
check "S1 nearest non-match: a class that names no changed type is not selected" '! listed UnrelatedTests && ! listed UsesFooTests'
fixture
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 2;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; mutate 'const typeRe = types.size ?' 'const typeRe = false ?'; mutate 'const memberRe = members.size ?' 'const memberRe = false ?'
smoke --print --base base
check "S1 mutant (only changed test files are selected): the class is no longer listed, so S1 fails on it" '[ $RC = 0 ] && ! listed UsesWidgetTests'

# S2. a partial Foo.Bar.cs changes: the classes naming Foo are selected
fixture
printf 'namespace Ah.Mesh;\npublic sealed partial class Foo\n{\n    public int Bazzle() => 2;\n}\n' > "$FX/native/src/Ah.Mesh/Foo.Bar.cs"
change; smoke --print --base base
check "S2 a changed partial selects the classes that name its type" 'listed UsesFooTests && ! listed UsesWidgetTests'

# S3. a deleted type: the classes that name it are selected (the type is read from the base)
fixture
G rm -q native/src/Ah.Mesh/Widget.cs
change; smoke --print --base base
check "S3 a deleted file's type selects the classes that name it" 'listed UsesWidgetTests && ! listed UnrelatedTests'
fixture
printf 'namespace Ah.Mesh;\npublic sealed class Cog\n{\n    public int Whirr() => 1;\n}\n' > "$FX/native/src/Ah.Mesh/Parts.cs"
tclass Ah.Mesh.Tests UsesCogTests 'var c = new Cog();'
G add -A >/dev/null; G commit -qm parts; G tag -f base >/dev/null
G rm -q native/src/Ah.Mesh/Parts.cs
change; smoke --print --base base
check "S3 a type deleted from a file of another name is found from the base text" 'listed UsesCogTests && ! listed UnrelatedTests'
fixture
printf 'namespace Ah.Mesh;\npublic sealed class Cog\n{\n    public int Whirr() => 1;\n}\n' > "$FX/native/src/Ah.Mesh/Parts.cs"
tclass Ah.Mesh.Tests UsesCogTests 'var c = new Cog();'
G add -A >/dev/null; G commit -qm parts; G tag -f base >/dev/null
G rm -q native/src/Ah.Mesh/Parts.cs
change; mutate 'const readOld = (p) => (p ? (tryGit("show", `${mergeBase}:./${p}`) ?? "") : "");' 'const readOld = (p) => "";'
smoke --print --base base
check "S3 mutant (the base text is not read): the class naming the deleted type is no longer listed" '! listed UsesCogTests'

# a rename: both the old and the new name count
fixture
G mv native/src/Ah.Mesh/Widget.cs native/src/Ah.Mesh/Sprocket.cs
printf 'namespace Ah.Mesh;\npublic sealed class Sprocket\n{\n    public int Spin() => 1;\n}\n' > "$FX/native/src/Ah.Mesh/Sprocket.cs"
tclass Ah.Mesh.Tests UsesSprocketTests 'var s = new Sprocket();'
change; smoke --print --base base
check "a rename selects the classes of the old and the new type name" 'listed UsesWidgetTests && listed UsesSprocketTests'

# S5. a docs-only change: Core, Config and Architecture and the contracts, no other class
fixture
printf 'text\n' > "$FX/docs/guide.md"
change; smoke --base base
check "S5 a docs-only change selects no test class outside the always-in projects" '[ $RC = 0 ] && echo "$OUT" | grep -q "selected C# test classes: 0"'
check "S5 it runs the three always-in projects unfiltered and the contracts suites" 'calls | grep -q "^dotnet test tests/Ah.Core.Tests$" && calls | grep -q "^dotnet test tests/Ah.Config.Tests$" && calls | grep -q "^dotnet test tests/Ah.Architecture.Tests$" && calls | grep -q "^run-all --exact contracts$" && ! calls | grep -q -- "--filter"'
fixture
printf 'namespace Ah.Other;\npublic sealed class Gizmo\n{\n    public int Turn() => 2;\n}\n' > "$FX/native/src/Ah.Other/Gizmo.cs"
change; smoke --base base
check "S5 nearest non-match: a source change does not run the contracts suites" '! calls | grep -q "contracts"'

# the <Type>Tests class of a changed type is selected though no test names the type
check "a changed type selects its <Type>Tests class" 'calls | grep -q "FullyQualifiedName~Ah.Mesh.Tests.GizmoTests"'

# S6. an over-broad selection: exit 3, nothing runs
fixture
for p in Ah.P1.Tests Ah.P2.Tests Ah.P3.Tests; do
  mkdir -p "$FX/native/tests/$p"
  tclass $p "${p//./}OneTests" 'var w = new Widget();'
  tclass $p "${p//./}TwoTests" 'var w = new Widget();'
done
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 3;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --base base
check "S6 three whole projects selected: exit 3, the message names the class count, nothing runs" '[ $RC = 3 ] && echo "$OUT" | grep -q "selection too broad (7 classes); this is QA'"'"'s full suite, not a smoke" && [ ! -s "$AH_CALLS" ]'
mutate 'if (tooBroad) process.exit(3);' 'if (false) process.exit(3);'
smoke --base base
check "S6 mutant (run anyway): dotnet is called, so S6 fails on it" 'calls | grep -q "^dotnet test"'
fixture
for p in Ah.P1.Tests Ah.P2.Tests; do
  mkdir -p "$FX/native/tests/$p"
  tclass $p "${p//./}OneTests" 'var w = new Widget();'
  tclass $p "${p//./}TwoTests" 'var w = new Widget();'
done
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 3;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --base base
check "S6 nearest non-match: two whole projects still run" '[ $RC = 0 ] && calls | grep -q "^dotnet test tests/Ah.P2.Tests --filter"'

# S6, the class count: 250 selected classes run, 251 do not (the project is not whole: 40% of it is selected)
fixture
mkdir -p "$FX/native/tests/Ah.Big.Tests"
for i in $(seq 1 249); do tclass Ah.Big.Tests "Sel${i}Tests" 'var w = new Widget();'; done
for i in $(seq 1 375); do tclass Ah.Big.Tests "Idle${i}Tests" 'var n = 1;'; done
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 4;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --print --why --base base
check "S6 250 selected classes outside the always-in projects are still a smoke" '[ $RC = 0 ] && echo "$OUT" | grep -q "selected C# test classes: 250" && echo "$OUT" | grep -q "^bounds: 250 classes (the user.s ruling)"'
tclass Ah.Big.Tests Sel250Tests 'var w = new Widget();'
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 5;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --print --base base
check "S6 one class more (over 150): exit 3" '[ $RC = 3 ]'

# S7. --print runs nothing
fixture
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 6;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --print --base base
check "S7 --print lists the selection and calls neither dotnet nor run-all" '[ $RC = 0 ] && listed UsesWidgetTests && [ ! -s "$AH_CALLS" ]'
smoke --base base
check "S7 nearest non-match: without --print dotnet is called" 'calls | grep -q "^dotnet test tests/Ah.Mesh.Tests --filter"'

# the suites select.mjs picks for a changed path, without the smoke's option: what run-all.sh --changed selects
plain_select() { printf 'test-x\ntest-r\ntest-contracts\n' > "$SB/names"; node "${SELECT:-$FX/tests/contract-tools/select.mjs}" --manifest "$FX/tests/manifest.json" --names "$SB/names" --lane fast --changed-list "$1" | tr '\n' ' '; }

# S8. for a non-native path the shell selection equals what run-all.sh --changed selects, and each suite runs by name
fixture
mkdir -p "$FX/misc"; printf 'x\n' > "$FX/misc/a.txt"
change; smoke --base base
check "S8 a non-native path: the smoke lists exactly the suites run-all.sh --changed selects, and runs them with --exact" '[ "$(plain_select misc/a.txt)" = "test-x " ] && echo "$OUT" | grep -q "^shell suites (.*): test-x$" && calls | grep -q "^run-all --exact x$"'
fixture
mkdir -p "$FX/other"; printf 'x\n' > "$FX/other/x.txt"
change; smoke --base base
check "S8 nearest non-match: a path no suite covers runs no shell suite" '! calls | grep -q "^run-all" && echo "$OUT" | grep -q "^shell suites (.*): none$"'

# S10. a native path selects, for the smoke, only the suites that cover it; without the option every suite with units, as today
fixture
printf 'namespace Ah.Roster;\npublic sealed class RosterThing\n{\n    public int Pick() => 2;\n}\n' > "$FX/native/src/Ah.Roster/X.cs"
printf 'namespace Ah.Other;\npublic sealed class Gizmo\n{\n    public int Turn() => 9;\n}\n' > "$FX/native/src/Ah.Other/Gizmo.cs"
change; smoke --base base
check "S10 with the option a native path selects only the suite covering it, and a path nothing covers is reported" '[ $RC = 0 ] && echo "$OUT" | grep -q "^shell suites (.*): test-r$" && echo "$OUT" | grep -q "^no shell suite covers native/src/Ah.Other/Gizmo.cs$" && calls | grep -q "^run-all --exact r$" && ! calls | grep -q "run-all --exact x"'
check "S10 nearest non-match: without the option the same native path selects every suite with units" '[ "$(plain_select native/src/Ah.Roster/X.cs)" = "test-x test-r " ]'
mutate_select() { node -e 'const fs=require("fs");const f=process.argv[1];const t=fs.readFileSync(f,"utf8");if(!t.includes(process.argv[2]))process.exit(1);fs.writeFileSync(f,t.split(process.argv[2]).join(process.argv[3]))' "$FX/tests/contract-tools/select.mjs" "$1" "$2"; }
mutate_select 'args.includes("--native-covers")' 'true'
check "S10 mutant (the option on by default): without the option only the covering suite is selected, so the default-unchanged check fails" '[ "$(plain_select native/src/Ah.Roster/X.cs)" = "test-r " ]'

# no native binary: run-all.sh would exit 1 without running anything, so the shell part is skipped, loudly, and the C# part decides
fixture
mkdir -p "$FX/misc"; printf 'x\n' > "$FX/misc/a.txt"
change; NATIVE_BIN="" smoke --base base
check "without a native binary the shell part is skipped with a notice and run-all.sh is not called" '[ $RC = 0 ] && echo "$OUT" | grep -q "shell part SKIPPED: no native binary" && ! calls | grep -q "^run-all"'
smoke --base base
check "nearest non-match: with a native binary the same change calls run-all.sh" 'calls | grep -q "^run-all --exact x$"'

# S4. only a private member changes: the classes naming the type are still selected (rule 2), a class naming only the private member is not
fixture
tclass Ah.Mesh.Tests NamesHiddenTests 'var h = obj.Hidden();'
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 1;\n    private int Hidden() => 3;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --print --base base
check "S4 a private-only change selects the classes naming the type but not a class naming only the private member" 'listed UsesWidgetTests && ! listed NamesHiddenTests && ! listed UnrelatedTests'
mutate '(?:public|internal|protected)' '(?:public|internal|protected|private)'
smoke --print --base base
check "S4 mutant (private member names are matched): the class naming only Hidden is selected, so S4 fails" 'listed NamesHiddenTests'

# S9. one reverse hop: a test naming only the type that wraps the changed type is selected
mkfx9() {
  fixture
  printf 'namespace Ah.Mesh;\npublic sealed class Inner\n{\n    public int Core() => 1;\n}\n' > "$FX/native/src/Ah.Mesh/Inner.cs"
  printf 'namespace Ah.Mesh;\npublic sealed class Outer\n{\n    private readonly Inner _i = new Inner();\n}\n' > "$FX/native/src/Ah.Mesh/Outer.cs"
  tclass Ah.Mesh.Tests OuterOnlyTests 'var o = new Outer();'
}
bump_inner() { printf 'namespace Ah.Mesh;\npublic sealed class Inner\n{\n    public int Core() => 2;\n}\n' > "$FX/native/src/Ah.Mesh/Inner.cs"; }
mkfx9; G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null; bump_inner
change; smoke --print --why --base base
check "S9 a class naming only Outer is selected when Inner, which Outer.cs names, changes (its output says why)" 'listed OuterOnlyTests && echo "$OUT" | grep -q "OuterOnlyTests.*Outer (wraps Inner)"'
check "S9 nearest non-match: a class naming neither is not selected" '! listed UnrelatedTests'
mutate 'if (refs.length > HUB_MAX)' 'if (true)'
smoke --print --base base
check "S9 mutant (no hop): OuterOnlyTests is not selected, so S9 fails" '! listed OuterOnlyTests'
mkfx9; mkdir -p "$FX/native/src/Ah.Other"
printf 'namespace Ah.Other;\npublic sealed class Outer\n{\n}\n' > "$FX/native/src/Ah.Other/Outer.cs"
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null; bump_inner
change; smoke --print --base base
check "S9 a wrapper name declared in two src projects is ambiguous and dropped" '! listed OuterOnlyTests'
mkfx9; mkdir -p "$FX/native/src/Ah.Other"
printf 'namespace Ah.Other;\npublic sealed class Outer\n{\n}\n' > "$FX/native/src/Ah.Other/Outer.cs"
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null; bump_inner
change; mutate '.size === 1' '.size >= 1'
smoke --print --base base
check "S9 mutant (no ambiguity check): the ambiguous wrapper is used, so the previous check fails" 'listed OuterOnlyTests'
# a type with more than 15 referrer files is a hub: matched directly, not expanded, and listed
hubfx() { # <referrer files>
  mkfx9
  for i in $(seq 1 "$1"); do printf 'namespace Ah.Mesh;\npublic sealed class Ref%s\n{\n    private Inner _i;\n}\n' "$i" > "$FX/native/src/Ah.Mesh/Ref$i.cs"; done
  tclass Ah.Mesh.Tests RefOneTests 'var r = new Ref1();'
  G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null; bump_inner
  change
}
hubfx 16; smoke --print --base base
check "S9 Inner with 16 referrer files (Outer.cs makes 17) is a hub: not expanded, and --print lists it" '! listed RefOneTests && ! listed OuterOnlyTests && echo "$OUT" | grep -q "^hub Inner (17 referrers): direct matches only$"'
hubfx 13; smoke --print --base base
check "S9 nearest non-match: 14 referrer files is not a hub, so the wrappers are used" 'listed RefOneTests && listed OuterOnlyTests && ! echo "$OUT" | grep -q "^hub "'
hubfx 16; mutate 'refs.length > HUB_MAX' 'refs.length > 1000'
smoke --print --base base
check "S9 mutant (hubs expanded): RefOneTests is selected, so the hub check fails" 'listed RefOneTests'

# C-3. selected shell suites that did not run are said on the last line
fixture
mkdir -p "$FX/misc"; printf 'x\n' > "$FX/misc/a.txt"
change; NATIVE_BIN="" smoke --base base
check "C-3 suites selected but no native binary: the last line is passed (shell part SKIPPED: 1 suites)" '[ $RC = 0 ] && [ "$(echo "$OUT" | tail -1)" = "affected-smoke: passed (shell part SKIPPED: 1 suites)" ]'
smoke --base base
check "C-3 nearest non-match: with a binary the last line is plain passed" '[ "$(echo "$OUT" | tail -1)" = "affected-smoke: passed" ]'

# C-1. a changed test helper feeds the type list; a non-Fact partial half attaches to its class; a native path that maps to nothing is named
fixture
printf 'namespace Ah.Mesh.Tests;\ninternal sealed class Rig\n{\n}\n' > "$FX/native/tests/Ah.Mesh.Tests/Rig.cs"
tclass Ah.Mesh.Tests UsesRigTests 'var r = new Rig();'
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf 'namespace Ah.Mesh.Tests;\ninternal sealed class Rig\n{\n    public int Spin() => 1;\n}\n' > "$FX/native/tests/Ah.Mesh.Tests/Rig.cs"
change; smoke --print --base base
check "C-1 a changed test helper file selects the classes that name its type" 'listed UsesRigTests && ! listed UnrelatedTests'
mutate 'native\/(src|tests)\/.*\.cs$/.test(p));' 'native\/(src)\/.*\.cs$/.test(p));'
smoke --print --base base
check "C-1 mutant (only src feeds the types): the class is not selected, so C-1 fails" '! listed UsesRigTests'
fixture
printf 'namespace Ah.Mesh.Tests;\npublic sealed partial class HelperPartialTests\n{\n    private static object Make() => new Widget();\n}\n' > "$FX/native/tests/Ah.Mesh.Tests/HelperPartialTests.Part.cs"
printf 'namespace Ah.Mesh.Tests;\npublic sealed partial class HelperPartialTests\n{\n    [Fact] public void A() { var n = 1; }\n}\n' > "$FX/native/tests/Ah.Mesh.Tests/HelperPartialTests.cs"
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 5;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --print --base base
check "C-1 a class whose only mention of the changed type is in its non-Fact partial half is selected" 'listed HelperPartialTests'
fixture
printf '{}\n' > "$FX/native/src/Ah.Mesh/data.json"; printf '{}\n' > "$FX/native/src/Ah.Roster/data.json"
printf 'namespace Ah.Mesh;\npublic sealed class Widget\n{\n    public int Spin() => 6;\n}\n' > "$FX/native/src/Ah.Mesh/Widget.cs"
change; smoke --print --base base
check "C-1 a native path no type, class or suite covers is named: not mapped: <path>" 'echo "$OUT" | grep -q "^not mapped: native/src/Ah.Mesh/data.json$"'
check "C-1 nearest non-match: a covered native path and a C# source change are not listed as unmapped" '! echo "$OUT" | grep -q "^not mapped: native/src/Ah.Roster/data.json$" && ! echo "$OUT" | grep -q "^not mapped: native/src/Ah.Mesh/Widget.cs$"'
mutate 'for (const p of notMapped) lines.push(' 'for (const p of []) lines.push('
smoke --print --base base
check "C-1 mutant (the notice is not printed): the unmapped path is silent, so C-1 fails" '! echo "$OUT" | grep -q "^not mapped:"'

# NEW-1. the private nested helpers of an edited test class do not feed the type list
fixture
printf 'namespace Ah.Mesh.Tests;\npublic sealed class OwnerTests\n{\n    private sealed class Rig\n    {\n    }\n\n    [Fact] public void A() { var n = 1; }\n}\n' > "$FX/native/tests/Ah.Mesh.Tests/OwnerTests.cs"
tclass Ah.Mesh.Tests UsesRigTests 'Rig r = null;'
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf 'namespace Ah.Mesh.Tests;\npublic sealed class OwnerTests\n{\n    private sealed class Rig\n    {\n    }\n\n    [Fact] public void A() { var n = 2; }\n}\n' > "$FX/native/tests/Ah.Mesh.Tests/OwnerTests.cs"
change; smoke --print --base base
check "NEW-1 editing a test class does not select the classes that name its private nested helper" 'listed OwnerTests && ! listed UsesRigTests'
mutate '? topTypesIn :' '? typesIn :'
smoke --print --base base
check "NEW-1 mutant (nested helpers feed the types): the other class is selected, so NEW-1 fails" 'listed UsesRigTests'

# C-2. a select.mjs that fails stops the smoke with exit 2 and its stderr
fixture
mkdir -p "$FX/misc"; printf 'x\n' > "$FX/misc/a.txt"
printf 'console.error("boom from select");process.exit(5);\n' > "$FX/tests/contract-tools/select.mjs"
change; smoke --base base
check "C-2 a select.mjs that exits non-zero: exit 2, its stderr is printed, nothing runs" '[ $RC = 2 ] && echo "$OUT" | grep -q "boom from select" && [ ! -s "$AH_CALLS" ]'
mutate 'if (r.error || r.status !== 0)' 'if (false)'
smoke --base base
check "C-2 mutant (the result is not checked): the smoke carries on, so C-2 fails" '[ $RC != 2 ]'

# N-2. run-all.sh and the selector are the work tree's, found from the cwd, even when the script is another copy
fixture
mkdir -p "$FX/misc"; printf 'x\n' > "$FX/misc/a.txt"
change; : > "$AH_CALLS"
OUT=$(cd "$FX/native" && PATH="$SB/bin:$PATH" AHD_NATIVE_BIN=/usr/bin/true bash "$PLUGIN/tests/affected-smoke.sh" --base base 2>&1); RC=$?
check "N-2 run from inside a work tree with another copy of the script: the work tree's run-all.sh runs the suite" '[ $RC = 0 ] && calls | grep -q "^run-all --exact x$"'

# N-4. a type after an attribute, and every type on a line, count
fixture
printf '[System.Obsolete] public sealed class Attrd { } public sealed class Second { } public sealed class Third { }\n' > "$FX/native/src/Ah.Mesh/Multi.cs"
tclass Ah.Mesh.Tests UsesAttrdTests 'Attrd a = null;'
tclass Ah.Mesh.Tests UsesThirdTests 'var t = new Third();'
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf '[System.Obsolete] public sealed class Attrd { } public sealed class Second { } public sealed class Third { int x; }\n' > "$FX/native/src/Ah.Mesh/Multi.cs"
change; smoke --print --base base
check "N-4 a type declared after an attribute and a third type on the same line are both found" 'listed UsesAttrdTests && listed UsesThirdTests && ! listed UnrelatedTests'
mutate '.split(/[;}]\s*/)' '.split(/NEVER_MATCHES/)'
smoke --print --base base
check "N-4 mutant (first type of a line only): the third type is lost" '! listed UsesThirdTests'
fixture
printf '[System.Obsolete] public sealed class Attrd { }\n' > "$FX/native/src/Ah.Mesh/Multi.cs"
tclass Ah.Mesh.Tests UsesAttrdTests 'Attrd a = null;'
G add -A >/dev/null; G commit -qm more; G tag -f base >/dev/null
printf '[System.Obsolete] public sealed class Attrd { int x; }\n' > "$FX/native/src/Ah.Mesh/Multi.cs"
change; smoke --print --base base
check "N-4 a type declared after an attribute on its line is found" 'listed UsesAttrdTests'
mutate '.replace(/^\s*(?:\[[^\]]*\]\s*)+/, "")' '.replace(/NEVER_MATCHES/, "")'
smoke --print --base base
check "N-4 mutant (attributes not skipped): the attributed type is lost, so the class is not selected" '! listed UsesAttrdTests'

# an empty diff: the always-in projects only, no shell
fixture
smoke --base base
check "an empty diff runs the three always-in projects and nothing else" '[ $RC = 0 ] && [ "$(calls | grep -c "^dotnet")" = 3 ] && ! calls | grep -q "^run-all"'

# a base that does not exist
fixture
smoke --base no-such-ref
check "an unknown base exits 2 with unknown base <ref>, and nothing runs" '[ $RC = 2 ] && echo "$OUT" | grep -q "^unknown base no-such-ref$" && [ ! -s "$AH_CALLS" ]'
smoke
check "no --base is a usage error (exit 2)" '[ $RC = 2 ]'

# a failing dotnet makes the smoke fail
fixture
printf '#!/bin/bash\necho "dotnet $*" >> "$AH_CALLS"\nexit 1\n' > "$SB/bin/dotnet-fail"; chmod +x "$SB/bin/dotnet-fail"
mkdir -p "$SB/bin-fail"; cp "$SB/bin/dotnet-fail" "$SB/bin-fail/dotnet"
: > "$AH_CALLS"
OUT=$(cd "$FX" && PATH="$SB/bin-fail:$PATH" bash "$FX/tests/affected-smoke.sh" --base base 2>&1); RC=$?
check "a failing dotnet test fails the smoke" '[ $RC = 1 ] && echo "$OUT" | grep -q "affected-smoke: FAILED"'
smoke --base base
check "nearest non-match: the same run with a passing dotnet exits 0" '[ $RC = 0 ] && echo "$OUT" | grep -q "affected-smoke: passed"'

# the Orchestrator's practice text: the ship merge and QA full beside the review
ORCH="$PLUGIN/agents/orchestrator.md"
check "agents/orchestrator.md names merge --no-ff and QA full in parallel with review" 'grep -q "merge --no-ff" "$ORCH" && grep -q "in parallel with review" "$ORCH" && grep -q "QA .full." "$ORCH"'
check "the skill ships M only when R's tip is M's first parent and B's tip its second parent" 'grep -q "first parent" "$PLUGIN/skills/agent-team/SKILL.md" && grep -q "second parent" "$PLUGIN/skills/agent-team/SKILL.md"'
check "nearest non-match: the skill text also names the practice, and the implementor text points at its testing order" 'grep -q "When an implementor reports done" "$PLUGIN/skills/agent-team/SKILL.md" && grep -q "testing order in your directive" "$PLUGIN/agents/implementor.md"'
node -e 'const fs=require("fs");fs.writeFileSync(process.argv[2],fs.readFileSync(process.argv[1],"utf8").split("in parallel with review").join("beside review"))' "$ORCH" "$SB/orch-mutant.md"
check "mutant (the phrase removed): the assertion no longer holds" '! grep -q "in parallel with review" "$SB/orch-mutant.md"'

echo "affected-smoke: $PASS passed, $FAIL failed"
[ $FAIL = 0 ]
