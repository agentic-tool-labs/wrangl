#!/bin/bash
# agent-hierarchy — scripts/install.sh and scripts/uninstall.sh in a sandbox, against a fake dotnet whose publish
# writes stub ahd and ahd-tui binaries. Nothing here touches the real home, a real launchd or systemd, or a real SDK.
# Usage: bash tests/test-native-install.sh   (exits 0 iff every row passes)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"

T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$T" ] && [ -d "$T" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$T"'

FAIL=0
ok()  { echo "ok   $1"; }
bad() { echo "FAIL $1"; FAIL=1; }
check() { # check <name> <command...>
  local name="$1"; shift
  if "$@" >/dev/null 2>&1; then ok "$name"; else bad "$name"; fi
}
same() { [ "$1" = "$2" ]; }
# both <exit code wanted> <exit code got> <grep pattern> <text>: the exit code is the wanted one AND the text holds the pattern
# (a `test ... && grep ...` written after `check name` would put the grep outside the check).
both() { [ "$2" -eq "$1" ] && grep -q -- "$3" <<<"$4"; }

# A tree listing with content checksums: equal before and after means nothing was written.
snapshot() {
  (cd "$1" && find . -print | sort | while IFS= read -r p; do
    if [ -L "$p" ]; then echo "$p -> $(readlink "$p")"
    elif [ -f "$p" ]; then echo "$p $(cksum < "$p")"
    else echo "$p"; fi
  done)
}

# --- the sandbox -------------------------------------------------------------------------------------------
export HOME="$T/home"
export AH_HOME="$HOME/.claude/ah2"
export AH_TEST=1
unset AH_SPAWN_DEPTH CLAUDECODE CLAUDE_CODE_SESSION_ID CLAUDE_CONFIG_DIR
export FAKE_LOG="$T/fake.log"
FAKEBIN="$T/fakebin"
PLUGIN_COPY="$T/plugin"
mkdir -p "$HOME" "$FAKEBIN" "$PLUGIN_COPY/scripts" "$PLUGIN_COPY/.claude-plugin" "$PLUGIN_COPY/native/src/Ah.Cli"
export AH_TEST_FAKE_BIN="$FAKEBIN"
export PATH="$FAKEBIN:$PATH"
: > "$FAKE_LOG"

mkdir -p "$PLUGIN_COPY/bin"
cp "$PLUGIN/bin/ah-testmode.sh" "$PLUGIN/bin/ah-trust.sh" "$PLUGIN_COPY/bin/"
cp "$PLUGIN/scripts/install.sh" "$PLUGIN/scripts/lib-version.sh" "$PLUGIN/scripts/lib-contract.sh" "$PLUGIN/scripts/lib-progress.sh" "$PLUGIN/scripts/uninstall.sh" "$PLUGIN/scripts/publish-native.sh" "$PLUGIN_COPY/scripts/"
printf '{"name":"agent-hierarchy","version":"1.2.3"}\n' > "$PLUGIN_COPY/.claude-plugin/plugin.json"
printf '{"contract":4,"units":{"daemon":"node"}}\n' > "$PLUGIN_COPY/native/cutover.json"
INSTALL="$PLUGIN_COPY/scripts/install.sh"
UNINSTALL="$PLUGIN_COPY/scripts/uninstall.sh"

# frontend-design: the copy of install.sh under test pins two small fixture files instead of the real upstream ones, and a fake
# curl serves them, so no case reaches the network. The shipped script's own pins are checked as text further down.
FD_FIX="$T/fdfix"; export FD_FIX
mkdir -p "$FD_FIX" "$T/fdfix2"
printf 'fixture skill v1\n' > "$FD_FIX/SKILL.md"; printf 'fixture license v1\n' > "$FD_FIX/LICENSE.txt"
printf 'fixture skill v2\n' > "$T/fdfix2/SKILL.md"; printf 'fixture license v2\n' > "$T/fdfix2/LICENSE.txt"
printf 'not what was pinned\n' > "$FD_FIX/BAD"
FD_SKILL_SHA=$(shasum -a 256 "$FD_FIX/SKILL.md" | cut -d' ' -f1); FD_LIC_SHA=$(shasum -a 256 "$FD_FIX/LICENSE.txt" | cut -d' ' -f1)
FD_SKILL_SHA2=$(shasum -a 256 "$T/fdfix2/SKILL.md" | cut -d' ' -f1); FD_LIC_SHA2=$(shasum -a 256 "$T/fdfix2/LICENSE.txt" | cut -d' ' -f1)
sed "s|^FRONTEND_DESIGN_FILES=.*|FRONTEND_DESIGN_FILES=\"SKILL.md:$FD_SKILL_SHA LICENSE.txt:$FD_LIC_SHA\"|" "$INSTALL" > "$INSTALL.tmp" && mv "$INSTALL.tmp" "$INSTALL"
cat > "$FAKEBIN/curl" <<'EOF'
#!/bin/bash
echo "curl $*" >> "$FAKE_LOG"
[ "${FAKE_CURL_FAIL:-}" = 1 ] && exit 22
out=""; url=""
while [ $# -gt 0 ]; do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    --max-time|--proto) shift 2 ;;
    -*) shift ;;
    *) url="$1"; shift ;;
  esac
done
name="${url##*/}"
src="$FD_FIX/$name"
[ "${FAKE_CURL_BAD:-}" = "$name" ] && src="$FD_FIX/BAD"
[ -f "$src" ] || exit 22
cp "$src" "$out"
EOF
chmod +x "$FAKEBIN/curl"

for tool in launchctl systemctl; do
  printf '#!/bin/sh\necho "%s $*" >> "$FAKE_LOG"\nexit 0\n' "$tool" > "$FAKEBIN/$tool"
  chmod +x "$FAKEBIN/$tool"
done

cat > "$FAKEBIN/dotnet" <<'EOF'
#!/bin/bash
echo "dotnet $*" >> "$FAKE_LOG"
case "$1" in
  --list-sdks) echo "10.0.301 [/fake/sdk]"; exit 0 ;;
  --version) echo "10.0.301"; exit 0 ;;
  restore) [ -n "${FAKE_RESTORE_OUT:-}" ] && echo "$FAKE_RESTORE_OUT"; exit 0 ;;
  publish)
    [ "${FAKE_PUBLISH_FAIL:-}" = 1 ] && { echo "fake publish failed" >&2; exit 1; }
    project="$2"
    case "$project" in */"${FAKE_PUBLISH_FAIL_PROJECT:-none}") echo "fake publish of $project failed" >&2; exit 1 ;; esac
    out=""; ver=""; con=""; name=""
    while [ $# -gt 0 ]; do
      case "$1" in
        -o) out="$2"; shift 2 ;;
        -p:AhVersion=*) ver="${1#-p:AhVersion=}"; shift ;;
        -p:AhContract=*) con="${1#-p:AhContract=}"; shift ;;
        -p:AhPluginName=*) name="${1#-p:AhPluginName=}"; shift ;;
        *) shift ;;
      esac
    done
    [ -n "${FAKE_CONTRACT:-}" ] && con="$FAKE_CONTRACT"
    mkdir -p "$out"
    case "$project" in
      */Ah.Tui)
        [ "${FAKE_TUI_MISSING:-}" = 1 ] && exit 0
        cat > "$out/ahd-tui" <<STUB
#!/bin/sh
echo "ahd-tui \$*" >> "$FAKE_LOG"
echo "TUI-OK \$*"
# build ${FAKE_BUILD_ID:-0}
exit 0
STUB
        chmod +x "$out/ahd-tui"
        : > "$out/ahd-tui.pdb"
        exit 0 ;;
    esac
    cat > "$out/ahd" <<STUB
#!/bin/sh
echo "ahd \$*" >> "$FAKE_LOG"
case "\$1" in
  --plugin-root) case "\$*" in
      *"permissions check"*) echo "\${FAKE_PERM_STATE:-skip: stub}" ;;
      *"permissions add"*|*"permissions sync"*|*"permissions remove"*)
        if [ -n "\${FAKE_PERM_REFUSE:-}" ]; then echo "permissions: refused: \${FAKE_PERM_REFUSE_TEXT:-stub session}" >&2; exit 1; fi
        if [ -n "\${FAKE_REWRITE_LEDGER:-}" ]; then printf '{"v":1,"added":[],"plugin_root":"x","at":"2099-01-01T00:00:00.000Z","answer":"yes"}\\n' > "\$AH_HOME/permissions.json"; fi
        # the shape of the verb's Report: indented JSON with "changed" and a "notices" array
        printf '{\n  "action": "VERB-REPORT",\n  "changed": %s,\n  "notices": [' "\${FAKE_PERM_CHANGED:-true}"
        [ -n "\${FAKE_NOTICE:-}" ] && printf '\n    "%s"\n  ' "\$FAKE_NOTICE"
        printf '],\n  "warnings": ['
        [ -n "\${FAKE_WARNING:-}" ] && printf '\n    "%s"\n  ' "\$FAKE_WARNING"
        printf ']\n}\n' ;;
      *"codex-trust"*)
        if [ -n "\${FAKE_TRUST_FAIL:-}" ]; then echo "refused: stub" >&2; exit 2; fi
        printf '{\n  "result": "%s"\n}\n' "\${FAKE_TRUST_RESULT:-unchanged}" ;;
      *"permissions rules"*) printf '%s\n' "\${FAKE_RULES-Bash(/p/bin/ah roster one:*)
Bash(/p/bin/ah roster two:*)
Bash(/p/bin/ah roster three:*)}" ;;
    esac ;;
  serve) mkdir -p "\$AH_HOME/run"; : > "\$AH_HOME/run/ahd.json"; trap 'exit 0' TERM; while :; do sleep 0.1; done ;;
  where) echo '{"binary":{"path":"x","version":"$ver","contract":$con,"plugin_name":"$name"},"plugin":null,"node":null,"compatible":false,"stamp":null,"login":"off"}' ;;
  start|restart)
    if [ "\${FAKE_START_FAIL:-}" = refused ]; then
      case " \$* " in
        *" --plain "*) echo "error: drain_refused: no caller" >&2 ;;
        *) echo '{"ok":false,"error":{"code":"drain_refused","message":"no caller"}}' ;;
      esac
    fi
    [ "\${FAKE_START_FAIL:-}" = other ] && echo "error: start failed: other" >&2
    [ -n "\${FAKE_START_FAIL:-}" ] && exit 1
    : > "\$AH_HOME/fake-daemon-up" ;;
  status)
    if [ -n "\${FAKE_STATUS:-}" ]; then rm -f "\$AH_HOME/fake-daemon-up"; printf '%s\n' "\$FAKE_STATUS"
    elif [ -f "\$AH_HOME/fake-daemon-up" ]; then rm -f "\$AH_HOME/fake-daemon-up"; echo '{"up":true,"pid":4242,"version":"1.2.3"}'
    else echo '{"up":false}'; fi ;;
  tui)
    self="\$0"; [ -L "\$self" ] && self="\$(readlink "\$self")"; dir="\$(dirname "\$self")"
    [ -x "\$dir/ahd-tui" ] || { echo "ahd: tui needs ahd-tui beside \$self, and it is not there" >&2; exit 70; }
    grep -q '"contract":$con}' "\$dir/ahd-tui.contract" 2>/dev/null || { echo "ahd: ahd-tui contract does not match" >&2; exit 70; }
    shift; exec "\$dir/ahd-tui" "\$@" ;;
esac
# build ${FAKE_BUILD_ID:-0}
exit 0
STUB
    chmod +x "$out/ahd"
    : > "$out/ahd.pdb"
    exit 0 ;;
esac
exit 0
EOF
chmod +x "$FAKEBIN/dotnet"

run_install() { bash "$INSTALL" "$@"; }
publishes() { grep -c '^dotnet publish' "$FAKE_LOG"; }

# --- P-10: a dry run changes nothing -----------------------------------------------------------------------
mkdir -p "$AH_HOME/msgs"; echo keep > "$AH_HOME/msgs/keep.txt"
before="$(snapshot "$HOME")"
out="$(run_install --prefix "$T/bin" 2>&1)"; rc=$?
after="$(snapshot "$HOME")"
check "P-10 dry run exits 0 and prints a plan" test "$rc" -eq 0 -a -n "$out"
check "P-10 dry run leaves HOME and AH_HOME byte-identical" same "$before" "$after"
check "P-10 dry run creates no prefix" test ! -e "$T/bin"
check "P-10 the sandbox AH_HOME (an ahprobe directory, AH_TEST=1) is reported as one the shims will use" sh -c 'printf %s "$1" | grep -q "the shims will use"' _ "$out"
out_strict="$(env -u AH_TEST bash "$INSTALL" --prefix "$T/bin" 2>&1)"
check "P-10 an AH_HOME that is neither the default home nor trusted is reported, outside test mode" sh -c 'printf %s "$1" | grep -q "the shims will not use"' _ "$out_strict"
check "P-10 and the report names no trust command to a reader that is not a terminal" sh -c '! printf %s "$1" | grep -q "ahd home trust"' _ "$out_strict"
check "P-10 dry run ran no publish" test "$(publishes)" -eq 0
check "P-10 the dry-run plan publishes ahd-tui as well" grep -q 'Ah.Tui .*ahd-tui' <<<"$out"

# --- P-9: the home guard -----------------------------------------------------------------------------------
mkdir -p "$T/other"
printf '#!/bin/sh\nexit 0\n' > "$T/other/dotnet"; chmod +x "$T/other/dotnet"
before="$(snapshot "$T")"
env -u AH_TEST bash "$INSTALL" --apply --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "P-9 redirected HOME without AH_TEST exits 2" test "$rc" -eq 2
check "P-9 ... and the tree is unchanged" same "$before" "$(snapshot "$T")"

PATH="$T/other:$PATH" bash "$INSTALL" --apply --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "P-9 AH_TEST=1 with a dotnet outside AH_TEST_FAKE_BIN exits 2" test "$rc" -eq 2
check "P-9 ... and nothing was written" same "$before" "$(snapshot "$T")"

# --- P-11: a full apply, then an identical re-run ----------------------------------------------------------
mkdir -p "$HOME/bin"
export PATH="$HOME/bin:$PATH"
out="$(run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
[ "$rc" -eq 0 ] || echo "$out" | sed 's/^/    | /'
check "P-11 apply exits 0" test "$rc" -eq 0
check "P-11 the binary is placed" test -x "$AH_HOME/bin/ahd"
check "P-11 the sidecar is the JSON document the dispatcher reads" same "$(cat "$AH_HOME/bin/ahd.contract" 2>/dev/null)" '{"version":"1.2.3","contract":4}'
check "P-11 the prefix link points at the binary" same "$(readlink "$T/bin/ahd")" "$AH_HOME/bin/ahd"
check "P-11 the stamp names version, contract, binary, link and plugin root" \
  grep -q "\"version\":\"1.2.3\",\"contract\":4,\"binary\":\"$AH_HOME/bin/ahd\",\"link\":\"$T/bin/ahd\",\"plugin_root\":\"$PLUGIN_COPY\"" "$AH_HOME/install.json"
check "P-11 the stamp is one line ending with last_rebuild null" grep -q '"auto_rebuild":true\(,"[a-z_]*":\({[^}]*}\|[^,}]*\)\)*,"last_rebuild":null}$' "$AH_HOME/install.json"
check "P-11 the PDB was removed with the stage" test ! -e "$AH_HOME/build/stage.$$"
check "P-11 ahd-tui is placed beside ahd and executable" test -x "$AH_HOME/bin/ahd-tui"
check "P-11 its sidecar is the same JSON document" same "$(cat "$AH_HOME/bin/ahd-tui.contract" 2>/dev/null)" '{"version":"1.2.3","contract":4}'
check "P-11 both projects were published" test "$(grep -c '^dotnet publish .*/Ah.Cli ' "$FAKE_LOG")" -ge 1 -a "$(grep -c '^dotnet publish .*/Ah.Tui ' "$FAKE_LOG")" -ge 1
check "P-11 no PDB was installed" test -z "$(ls "$AH_HOME/bin" | grep '[.]pdb$')"
check "P-11 no temporary file was left in bin" test -z "$(ls -a "$AH_HOME/bin" | grep '[.]tmp$')"
check "P-11 the publish was given the three identity properties" \
  grep -q 'publish .*-p:AhVersion=1.2.3 -p:AhContract=4 -p:AhPluginName=agent-hierarchy' "$FAKE_LOG"
check "P-11 every publish was given an ArtifactsPath inside its stage, so nothing is built under the plugin root" \
  test "$(grep -c "^dotnet publish .*-p:ArtifactsPath=$AH_HOME/build/stage\.[0-9]*/artifacts\$" "$FAKE_LOG")" -eq "$(publishes)"
n_log=$(wc -l < "$FAKE_LOG")
bash "$PLUGIN_COPY/scripts/publish-native.sh" --dotnet "$FAKEBIN/dotnet" --native "$PLUGIN_COPY/native" --rid osx-arm64 --out "$T/other/direct-out" \
  --version 1.2.3 --contract 4 --name agent-hierarchy >/dev/null 2>&1
check "P-11 publish-native.sh run with no --artifacts passes no ArtifactsPath (the dev lanes keep the default)" \
  test "$(tail -n +$((n_log + 1)) "$FAKE_LOG" | grep -c '^dotnet publish')" -eq 2 -a "$(tail -n +$((n_log + 1)) "$FAKE_LOG" | grep -c 'ArtifactsPath')" -eq 0
check "P-11 the PATH symlink tier linked from HOME/bin" same "$(readlink "$HOME/bin/ahd")" "$AH_HOME/bin/ahd"
out_tui="$("$T/bin/ahd" tui --once 2>&1)"; rc_tui=$?
check "P-11 'ahd tui' through the prefix link finds and runs ahd-tui" both 0 "$rc_tui" 'TUI-OK --once' "$out_tui"
out_tui="$("$HOME/bin/ahd" tui 2>&1)"; rc_tui=$?
check "P-11 'ahd tui' through the PATH link runs ahd-tui too" both 0 "$rc_tui" 'TUI-OK' "$out_tui"
check "P-11 no rc file was written at a non-interactive run" test ! -e "$HOME/.zshrc" -a ! -e "$HOME/.bashrc" -a ! -e "$HOME/.bash_profile"
stray="$(cd "$T" && find . -mindepth 1 \
  -not -path './fakebin*' -not -path './plugin*' -not -path './other*' -not -path './fake.log' \
  -not -path './home/.claude/ah2*' -not -path './home/.claude' -not -path './home' \
  -not -path './fdfix*' -not -path './home/.claude/skills*' \
  -not -path './bin*' -not -path './home/bin*' | sort)"
check "P-11 apply wrote nothing outside AH_HOME, the prefix, the PATH link and the frontend-design skill directory" test -z "$stray"
stamp1="$(cat "$AH_HOME/install.json")"; sidecar1="$(cat "$AH_HOME/bin/ahd.contract")"; link1="$(readlink "$T/bin/ahd")"
p1="$(publishes)"
run_install --apply --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "P-11 a re-run exits 0" test "$rc" -eq 0
check "P-11 a re-run rebuilt both binaries" test "$(publishes)" -eq $((p1 + 2))
check "P-11 a re-run leaves the stamp byte-identical" same "$stamp1" "$(cat "$AH_HOME/install.json")"
check "P-11 a re-run leaves the sidecar and link identical" same "$sidecar1|$link1" "$(cat "$AH_HOME/bin/ahd.contract")|$(readlink "$T/bin/ahd")"

# --- P-20: an out-of-date SDK is noticed and said; the install still completes ------------------------------
adv="/x/Ah.Cli.csproj : warning NU1903: Package 'Microsoft.DotNet.ILCompiler' 10.0.7 has a known high severity vulnerability"
out="$(FAKE_RESTORE_OUT="$adv" run_install --apply --prefix "$T/bin" </dev/null 2>&1)"; rc=$?
check "P-20 an SDK-package advisory, not at a terminal: the install completes" test "$rc" -eq 0 -a -x "$AH_HOME/bin/ahd"
check "P-20 ... and says the SDK in use (version and path) is out of date, with the command to update" \
  grep -q 'The .NET SDK in use, 10.0.301 \[/fake/sdk\], is out of date.*the build still works. To update it, run at a terminal: bash .*install.sh --apply' <<<"$out"
check "P-20 ... and installs no SDK" test ! -e "$AH_HOME/dotnet"
out="$(run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-20 a clean restore prints nothing new (nearest non-match)" test -z "$(grep -i 'out of date' <<<"$out")"
out="$(FAKE_RESTORE_OUT="/x/Ah.Cli.csproj : warning NU1903: Package 'Newtonsoft.Json' 12.0.1 has a known high severity vulnerability" run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-20 an advisory on a package the SDK does not supply prints nothing new (nearest non-match)" test -z "$(grep -i 'out of date' <<<"$out")"
out="$(FAKE_RESTORE_OUT="/x/Ah.Cli.csproj : warning NU1903: Package 'runtime.native.System.Security.Cryptography.OpenSsl' 4.3.0 has a known high severity vulnerability" run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-20 a bare runtime.* package is not an SDK pack (nearest non-match of runtime.<rid>.Microsoft.DotNet.ILCompiler)" test -z "$(grep -i 'out of date' <<<"$out")"

# --- P-21: a private SDK: a broken one falls back to the system SDK; a stamped one is not offered the same install ------
mkdir -p "$AH_HOME/dotnet"
cat > "$AH_HOME/dotnet/dotnet" <<EOF
#!/bin/bash
case "\$1" in
  --list-sdks) echo "10.0.100 [/priv/sdk]"; exit 0 ;;
  --version) echo "10.0.100"; exit 0 ;;
  publish) [ "\${FAKE_PRIV_PUBLISH_FAIL:-}" = 1 ] && { echo "private publish failed" >&2; exit 1; } ;;
esac
exec "$FAKEBIN/dotnet" "\$@"
EOF
chmod +x "$AH_HOME/dotnet/dotnet"
out="$(FAKE_PRIV_PUBLISH_FAIL=1 run_install --apply --prefix "$T/bin" </dev/null 2>&1)"; rc=$?
check "P-21 a private SDK whose publish fails, a working system SDK: the build completes with the system one" \
  both 0 "$rc" 'building once more with the system dotnet' "$out"
out="$(run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-21 a working private SDK builds with no fallback (nearest non-match)" test -z "$(grep 'system dotnet' <<<"$out")"
echo "10.0.100" > "$AH_HOME/dotnet/ah-installed-version"
out="$(FAKE_RESTORE_OUT="$adv" run_install --apply --prefix "$T/bin" </dev/null 2>&1)"; rc=$?
check "P-21 a flagged private SDK with a fresh install stamp: the line, and no update offered" \
  test "$rc" -eq 0 -a -n "$(grep '10.0.100 \[/priv/sdk\], is out of date' <<<"$out")" -a -z "$(grep 'To update it' <<<"$out")"
touch -t 202001010000 "$AH_HOME/dotnet/ah-installed-version"
out="$(FAKE_RESTORE_OUT="$adv" run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-21 ... a stamp 7 days old or older offers the update again (nearest non-match)" grep -q 'To update it' <<<"$out"
rm -f "$AH_HOME/dotnet/ah-installed-version"
out="$(FAKE_RESTORE_OUT="$adv" run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-21 ... and so does a missing stamp" grep -q 'To update it' <<<"$out"
rm -rf "$AH_HOME/dotnet"

# --- the PATH step is skipped whole when the prefix is already on PATH --------------------------------------
BASEPATH="$FAKEBIN:/usr/bin:/bin"
path_row() { # path_row <PATH value> [install args...]: the dry-run text, with a zsh login shell
  local p="$1"; shift
  PATH="$p" SHELL=/bin/zsh bash "$INSTALL" --prefix "$T/bin" "$@" 2>&1
}
out="$(path_row "$T/bin:$BASEPATH")"
check "PATH skip: a prefix on PATH says so and plans no rc edit" sh -c 'printf %s "$1" | grep -q "already on .PATH" && ! printf %s "$1" | grep -qi "PATH block\|Add this line\|symlink"' _ "$out"
out="$(path_row "$T/bin/:$BASEPATH")"
check "PATH skip: a PATH entry with a trailing slash counts as the prefix" sh -c 'printf %s "$1" | grep -q "already on .PATH"' _ "$out"
out="$(PATH="$T/bin:$BASEPATH" SHELL=/bin/zsh bash "$INSTALL" --prefix "$T/bin/" 2>&1)"
check "PATH skip: a prefix given with a trailing slash counts as on PATH" sh -c 'printf %s "$1" | grep -q "already on .PATH"' _ "$out"
out="$(path_row "$T/bin2:$T/bin/sub:$T/binx:$BASEPATH")"
check "PATH skip (negative): a sibling, a subdirectory and a longer name are not the prefix" sh -c '! printf %s "$1" | grep -q "already on .PATH" && printf %s "$1" | grep -q "append an ah PATH block"' _ "$out"
out="$(path_row "$BASEPATH" --no-path)"
check "PATH skip (negative): --no-path still skips the step for its own reason" sh -c 'printf %s "$1" | grep -q "Skipping PATH setup" && ! printf %s "$1" | grep -q "already on .PATH"' _ "$out"
check "PATH skip: the dry runs wrote nothing" test ! -e "$HOME/.zshrc"

# --- P-12: a wrong contract is never placed ----------------------------------------------------------------
printf 'old binary\n' > "$AH_HOME/bin/ahd"
old_sum="$(cksum < "$AH_HOME/bin/ahd")"
old_tui_sum="$(cksum < "$AH_HOME/bin/ahd-tui")|$(cat "$AH_HOME/bin/ahd-tui.contract")"
FAKE_CONTRACT=999 run_install --apply --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "P-12 a binary that reports the wrong contract exits 1" test "$rc" -eq 1
check "P-12 ... and the installed binary is unchanged" same "$old_sum" "$(cksum < "$AH_HOME/bin/ahd")"
check "P-12 ... and so are ahd-tui and its sidecar" same "$old_tui_sum" "$(cksum < "$AH_HOME/bin/ahd-tui")|$(cat "$AH_HOME/bin/ahd-tui.contract")"
run_install --apply --prefix "$T/bin" >/dev/null 2>&1   # put a good binary back

# --- a failing or missing ahd-tui build places nothing --------------------------------------------------------
whole_before="$(cksum < "$AH_HOME/bin/ahd")|$(cksum < "$AH_HOME/bin/ahd-tui")|$(cat "$AH_HOME/bin/ahd-tui.contract")"
FAKE_BUILD_ID=5 FAKE_PUBLISH_FAIL_PROJECT=Ah.Tui run_install --apply --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "an ahd-tui publish that fails exits 1" test "$rc" -eq 1
check "... and neither installed binary nor the sidecar changed" same "$whole_before" "$(cksum < "$AH_HOME/bin/ahd")|$(cksum < "$AH_HOME/bin/ahd-tui")|$(cat "$AH_HOME/bin/ahd-tui.contract")"
out="$(FAKE_BUILD_ID=5 FAKE_TUI_MISSING=1 run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
check "a publish that produced no ahd-tui exits 1 and names it" both 1 "$rc" 'ahd-tui' "$out"
check "... and nothing was placed" same "$whole_before" "$(cksum < "$AH_HOME/bin/ahd")|$(cksum < "$AH_HOME/bin/ahd-tui")|$(cat "$AH_HOME/bin/ahd-tui.contract")"

# --- P-13: the old manual link is replaced; a foreign file is refused --------------------------------------
rm -f "$T/bin/ahd"
mkdir -p "$T/cache/plugins/cache/x/y/1.0.0/bin"
ln -s "$T/cache/plugins/cache/x/y/1.0.0/bin/ahd" "$T/bin/ahd"
out="$(run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
check "P-13 the old manual link is replaced" same "$(readlink "$T/bin/ahd")" "$AH_HOME/bin/ahd"
check "P-13 ... and it says so" grep -q 'old manual link' <<<"$out"
for tree in agent-hierarchy agent-hierarchy-v2; do
  rm -f "$T/bin/ahd"
  mkdir -p "$T/checkout/$tree/bin"
  ln -s "$T/checkout/$tree/bin/ahd" "$T/bin/ahd"
  out="$(run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
  check "P-13 a manual link into a checkout's $tree/bin/ahd is replaced" same "$(readlink "$T/bin/ahd")" "$AH_HOME/bin/ahd"
done
rm -f "$T/bin/ahd"
mkdir -p "$T/checkout/agent-hierarchy-other/bin"
ln -s "$T/checkout/agent-hierarchy-other/bin/ahd" "$T/bin/ahd"
out="$(run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
check "P-13 a link into a sibling that only starts with the plugin's name is foreign, refused with exit 1" test "$rc" -eq 1
check "P-13 ... and left pointing where it did" same "$(readlink "$T/bin/ahd")" "$T/checkout/agent-hierarchy-other/bin/ahd"
rm -f "$T/bin/ahd"
printf 'someone else\n' > "$T/bin/ahd"
out="$(run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
check "P-13 a foreign file at the link path is refused with exit 1" test "$rc" -eq 1
check "P-13 ... untouched" same "$(cat "$T/bin/ahd")" "someone else"
check "P-13 ... while the other steps still ran" test -x "$AH_HOME/bin/ahd" -a -f "$AH_HOME/install.json"
check "P-13 ... and the stamp records no link" grep -q '"link":null' "$AH_HOME/install.json"
rm -f "$T/bin/ahd"
run_install --apply --prefix "$T/bin" >/dev/null 2>&1

# --- P-15: --rebuild-only, and the rebuild lock ------------------------------------------------------------
link_before="$(readlink "$T/bin/ahd")"
mkdir -p "$T/bin2"
run_install --apply --rebuild-only --quiet --prefix "$T/bin2" >/dev/null 2>&1; rc=$?
check "P-15 --rebuild-only exits 0" test "$rc" -eq 0
check "P-15 --rebuild-only creates no link in another prefix" test ! -e "$T/bin2/ahd"
check "P-15 --rebuild-only leaves the existing link alone" same "$(readlink "$T/bin/ahd")" "$link_before"
check "P-15 --rebuild-only records last_rebuild ok" grep -q '"last_rebuild":{"version":"1.2.3","at":"[^"]*","state":"ok"}}$' "$AH_HOME/install.json"
check "P-15 --rebuild-only keeps the stamp's link" grep -q "\"link\":\"$T/bin/ahd\"" "$AH_HOME/install.json"
check "P-15 --quiet printed nothing" same "$(run_install --apply --rebuild-only --quiet --prefix "$T/bin2" 2>&1)" ""
old_ahd="$(cksum < "$AH_HOME/bin/ahd")"; old_tui="$(cksum < "$AH_HOME/bin/ahd-tui")"
FAKE_BUILD_ID=7 run_install --apply --rebuild-only --quiet --prefix "$T/bin2" >/dev/null 2>&1; rc=$?
check "P-15 --rebuild-only replaces ahd and ahd-tui together" test "$rc" -eq 0 -a "$(cksum < "$AH_HOME/bin/ahd")" != "$old_ahd" -a "$(cksum < "$AH_HOME/bin/ahd-tui")" != "$old_tui"
printf '{"contract":5,"units":{"daemon":"node"}}\n' > "$PLUGIN_COPY/native/cutover.json"
run_install --apply --rebuild-only --quiet --prefix "$T/bin2" >/dev/null 2>&1
check "P-15 --rebuild-only rewrites both sidecars for a new contract" \
  same "$(cat "$AH_HOME/bin/ahd.contract")|$(cat "$AH_HOME/bin/ahd-tui.contract")" '{"version":"1.2.3","contract":5}|{"version":"1.2.3","contract":5}'
printf '{"contract":4,"units":{"daemon":"node"}}\n' > "$PLUGIN_COPY/native/cutover.json"
run_install --apply --rebuild-only --quiet --prefix "$T/bin2" >/dev/null 2>&1
check "P-15 ... and back" same "$(cat "$AH_HOME/bin/ahd.contract")|$(cat "$AH_HOME/bin/ahd-tui.contract")" '{"version":"1.2.3","contract":4}|{"version":"1.2.3","contract":4}'


now_ms=$(( $(date +%s) * 1000 ))
printf '{"pid":1,"at":%s,"nonce":"held"}' "$now_ms" > "$AH_HOME/bin/.rebuild.lock"
p2="$(publishes)"
out="$(run_install --apply --rebuild-only --prefix "$T/bin2" 2>&1)"; rc=$?
check "P-15 a fresh lock: exit 0 and 'rebuild already running'" both 0 "$rc" 'rebuild already running' "$out"
check "P-15 a fresh lock: no publish" test "$(publishes)" -eq "$p2"
check "P-15 a fresh lock is left in place" grep -q '"nonce":"held"' "$AH_HOME/bin/.rebuild.lock"
printf '{"pid":1,"at":%s,"nonce":"old"}' "$(( now_ms - 11 * 60 * 1000 ))" > "$AH_HOME/bin/.rebuild.lock"
if command -v flock >/dev/null 2>&1; then
  run_install --apply --rebuild-only --prefix "$T/bin2" >/dev/null 2>&1; rc=$?
  check "P-15 a lock older than 10 minutes is taken over (flock(1) present)" test "$rc" -eq 0 -a "$(publishes)" -gt "$p2"
  check "P-15 ... and released afterwards" test ! -e "$AH_HOME/bin/.rebuild.lock"
else
  rm -f "$AH_HOME/bin/.rebuild.lock"
fi

# a failing rebuild is recorded and leaves the rest of the stamp true
good_stamp_head="$(sed 's/"last_rebuild":.*//' "$AH_HOME/install.json")"
FAKE_PUBLISH_FAIL=1 run_install --apply --rebuild-only --quiet --prefix "$T/bin2" >/dev/null 2>&1; rc=$?
check "P-15 a failed rebuild exits 1" test "$rc" -eq 1
check "P-15 ... records last_rebuild failed" grep -q '"last_rebuild":{"version":"1.2.3","at":"[^"]*","state":"failed"}}$' "$AH_HOME/install.json"
check "P-15 ... and keeps every other field" same "$good_stamp_head" "$(sed 's/"last_rebuild":.*//' "$AH_HOME/install.json")"
check "P-15 ... and leaves the lock released" test ! -e "$AH_HOME/bin/.rebuild.lock"

# the SessionStart hook records "started" in this one-line form (InstallStampNotice); a failed rebuild must still record failed
sed -E 's/"last_rebuild":(null|\{[^}]*\})\}$/"last_rebuild":{"version":"1.2.3","at":"2026-10-11T12:00:00.000Z","state":"started"}}/' "$AH_HOME/install.json" > "$T/hook-stamp" && mv -f "$T/hook-stamp" "$AH_HOME/install.json"
check "P-15 the hook-form seed says started" grep -q '"state":"started"}}$' "$AH_HOME/install.json"
FAKE_PUBLISH_FAIL=1 run_install --apply --rebuild-only --quiet --prefix "$T/bin2" >/dev/null 2>&1
check "P-15 a failed rebuild over the hook's started stamp records failed" grep -q '"last_rebuild":{"version":"1.2.3","at":"[^"]*","state":"failed"}}$' "$AH_HOME/install.json"
rm -f "$AH_HOME/bin/.rebuild.lock"

# --- the rebuild lock without flock(1): a stale lock of a gone pid is taken over; of a live pid, named and failed ---
now_ms=$(( $(date +%s) * 1000 ))
sh -c 'exit 0' & dead_pid=$!; wait "$dead_pid"
printf '{"pid":%s,"at":%s,"nonce":"old"}' "$dead_pid" "$(( now_ms - 11 * 60 * 1000 ))" > "$AH_HOME/bin/.rebuild.lock"
p3="$(publishes)"
out="$(AH_INSTALL_FLOCK=off run_install --apply --rebuild-only --prefix "$T/bin2" 2>&1)"; rc=$?
check "P-15 no flock(1): a stale lock of a gone pid is taken over and the rebuild runs" test "$rc" -eq 0 -a "$(publishes)" -gt "$p3"
check "P-15 ... one line says the stale lock of that pid was removed" sh -c 'printf %s "$1" | grep -q "removed the stale rebuild lock of pid $2 at .*/.rebuild.lock"' _ "$out" "$dead_pid"
check "P-15 ... and the run's own lock is released, with no aside copy left" test ! -e "$AH_HOME/bin/.rebuild.lock" -a -z "$(ls -a "$AH_HOME/bin" | grep 'rebuild.lock.stale')"
# negative: a lock under 10 minutes old with a gone pid is not taken over
printf '{"pid":%s,"at":%s,"nonce":"held"}' "$dead_pid" "$now_ms" > "$AH_HOME/bin/.rebuild.lock"
p3="$(publishes)"
out="$(AH_INSTALL_FLOCK=off run_install --apply --rebuild-only --prefix "$T/bin2" 2>&1)"; rc=$?
check "P-15 no flock(1): a fresh lock of a gone pid stays: exit 0, busy, no publish" test "$rc" -eq 0 -a "$(publishes)" -eq "$p3" -a "$(grep -c 'rebuild already running' <<<"$out")" -gt 0
check "P-15 ... and the lock is untouched" grep -q '"nonce":"held"' "$AH_HOME/bin/.rebuild.lock"
# negative: a stale lock whose pid is alive is left alone, named, and the rebuild records failed
sed -E 's/"last_rebuild":(null|\{[^}]*\})\}$/"last_rebuild":{"version":"1.2.3","at":"2026-10-11T12:00:00.000Z","state":"started"}}/' "$AH_HOME/install.json" > "$T/hook-stamp" && mv -f "$T/hook-stamp" "$AH_HOME/install.json"
printf '{"pid":%s,"at":%s,"nonce":"old"}' "$$" "$(( now_ms - 11 * 60 * 1000 ))" > "$AH_HOME/bin/.rebuild.lock"
p3="$(publishes)"
out="$(AH_INSTALL_FLOCK=off run_install --apply --rebuild-only --prefix "$T/bin2" 2>&1)"; rc=$?
check "P-15 no flock(1): a stale lock of a live pid fails the run: exit 1, no publish" test "$rc" -eq 1 -a "$(publishes)" -eq "$p3"
check "P-15 ... the message names the lock and the pid" sh -c 'printf %s "$1" | grep -q "rebuild lock at .*/.rebuild.lock is stale ([0-9]* min old) but its pid $2 is still running"' _ "$out" "$$"
check "P-15 ... the lock is untouched" grep -q '"nonce":"old"' "$AH_HOME/bin/.rebuild.lock"
check "P-15 ... no aside copy was made" test -z "$(ls -a "$AH_HOME/bin" | grep 'rebuild.lock.stale')"
check "P-15 ... and the stamp records failed" grep -q '"last_rebuild":{"version":"1.2.3","at":"[^"]*","state":"failed"}}$' "$AH_HOME/install.json"
rm -f "$AH_HOME/bin/.rebuild.lock"
# negative: a stale lock whose pid exists but cannot be signalled (pid 1, another user's: kill -0 fails with EPERM) is alive
printf '{"pid":1,"at":%s,"nonce":"old"}' "$(( now_ms - 11 * 60 * 1000 ))" > "$AH_HOME/bin/.rebuild.lock"
p3="$(publishes)"
out="$(AH_INSTALL_FLOCK=off run_install --apply --rebuild-only --prefix "$T/bin2" 2>&1)"; rc=$?
check "P-15 no flock(1): a stale lock of a live pid that cannot be signalled fails the run: exit 1, no publish" test "$rc" -eq 1 -a "$(publishes)" -eq "$p3"
check "P-15 ... the message names pid 1" sh -c 'printf %s "$1" | grep -q "rebuild lock at .*/.rebuild.lock is stale ([0-9]* min old) but its pid 1 is still running"' _ "$out"
check "P-15 ... the lock is still there" grep -q '"pid":1,.*"nonce":"old"' "$AH_HOME/bin/.rebuild.lock"
rm -f "$AH_HOME/bin/.rebuild.lock"
if command -v flock >/dev/null 2>&1; then
  printf '{"pid":1,"at":%s,"nonce":"old"}' "$(( now_ms - 11 * 60 * 1000 ))" > "$AH_HOME/bin/.rebuild.lock"
  p5="$(publishes)"
  run_install --apply --rebuild-only --prefix "$T/bin2" >/dev/null 2>&1; rc=$?
  check "P-15 with flock(1): a stale lock is taken over and released" test "$rc" -eq 0 -a "$(publishes)" -gt "$p5" -a ! -e "$AH_HOME/bin/.rebuild.lock"
fi

# --- login and daemon branches ----------------------------------------------------------------------------
: > "$FAKE_LOG"
run_install --apply --login --prefix "$T/bin" >/dev/null 2>&1
check "install --login runs 'ahd login on'" grep -q '^ahd login on$' "$FAKE_LOG"
: > "$FAKE_LOG"
run_install --apply --rebuild-only --login --prefix "$T/bin" >/dev/null 2>&1
check "--rebuild-only never runs 'ahd login on'" test -z "$(grep '^ahd login' "$FAKE_LOG")"

printf '{"contract":4,"units":{"daemon":"native"}}\n' > "$PLUGIN_COPY/native/cutover.json"
: > "$FAKE_LOG"
FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --prefix "$T/bin" >/dev/null 2>&1
check "an older running daemon is restarted" grep -q '^ahd restart --plain$' "$FAKE_LOG"
: > "$FAKE_LOG"
FAKE_STATUS='{"up":true,"version":"1.2.3"}' run_install --apply --prefix "$T/bin" >/dev/null 2>&1
check "an up-to-date daemon is not restarted" test -z "$(grep '^ahd restart' "$FAKE_LOG")"
# the same version can be a different build: a running daemon of the replaced binary is restarted, once
: > "$FAKE_LOG"
FAKE_BUILD_ID=2 FAKE_STATUS='{"up":true,"version":"1.2.3"}' run_install --apply --prefix "$T/bin" >/dev/null 2>&1
check "a daemon of the same version is restarted when the installed binary changed" grep -q '^ahd restart --plain$' "$FAKE_LOG"
: > "$FAKE_LOG"
FAKE_BUILD_ID=2 FAKE_STATUS='{"up":true,"version":"1.2.3"}' run_install --apply --prefix "$T/bin" >/dev/null 2>&1
check "... and not again when the binary is the same (nearest non-match)" test -z "$(grep '^ahd restart' "$FAKE_LOG")"
: > "$FAKE_LOG"
FAKE_BUILD_ID=3 FAKE_STATUS='{"up":true,"version":"1.2.3"}' run_install --apply --rebuild-only --prefix "$T/bin" >/dev/null 2>&1
check "a rebuild that changed the binary restarts a running daemon too" grep -q '^ahd restart --plain$' "$FAKE_LOG"

# --no-daemon-restart: a daemon that would be restarted is left alone, for an older version and for a changed binary
: > "$FAKE_LOG"
FAKE_BUILD_ID=4 FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --no-daemon-restart --prefix "$T/bin" >/dev/null 2>&1
check "--no-daemon-restart leaves an older running daemon and a changed binary alone" test -z "$(grep '^ahd restart' "$FAKE_LOG")"
: > "$FAKE_LOG"
FAKE_BUILD_ID=5 FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --rebuild-only --no-daemon-restart --prefix "$T/bin" >/dev/null 2>&1
check "--no-daemon-restart with --rebuild-only leaves it alone too" test -z "$(grep '^ahd restart' "$FAKE_LOG")"
: > "$FAKE_LOG"
FAKE_BUILD_ID=6 FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --prefix "$T/bin" >/dev/null 2>&1
check "... and the same state without the flag does restart (nearest non-match)" grep -q '^ahd restart --plain$' "$FAKE_LOG"

# A stopped daemon is started, even by --rebuild-only with an unchanged binary, and its pid and version are reported;
# a start or restart that does not bring it up fails the install.
: > "$FAKE_LOG"
out="$(run_install --apply --rebuild-only --prefix "$T/bin" 2>&1)"; rc=$?
check "a stopped daemon is started by --rebuild-only" grep -q '^ahd start --plain$' "$FAKE_LOG"
check "... and its pid and version are reported, exit 0" both 0 "$rc" 'The daemon is up: pid 4242, version 1.2.3' "$out"
: > "$FAKE_LOG"
out="$(FAKE_START_FAIL=1 run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
check "a start that fails exits 1 and says so" both 1 "$rc" 'error: the daemon did not come up' "$out"
: > "$FAKE_LOG"
out="$(FAKE_START_FAIL=1 FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
check "a restart that fails exits 1 too" both 1 "$rc" 'error: the daemon did not come up' "$out"
: > "$FAKE_LOG"
out="$(FAKE_START_FAIL=1 run_install --apply --rebuild-only --prefix "$T/bin" 2>&1)"; rc=$?
check "--rebuild-only: a start that fails is a warning and exit 0" both 0 "$rc" 'warning: the daemon did not come up' "$out"
: > "$FAKE_LOG"
out="$(FAKE_START_FAIL=1 FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --rebuild-only --prefix "$T/bin" 2>&1)"; rc=$?
check "--rebuild-only: a restart that fails is a warning and exit 0" both 0 "$rc" 'warning: the daemon did not come up' "$out"
: > "$FAKE_LOG"
out="$(FAKE_START_FAIL=refused FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --rebuild-only --prefix "$T/bin" 2>&1)"; rc=$?
check "--rebuild-only: a refused restart says the old daemon keeps running, exit 0" both 0 "$rc" 'was refused (drain_refused), so the old daemon keeps running' "$out"
check "... and does not say it did not come up" sh -c '! printf %s "$1" | grep -q "did not come up"' _ "$out"
check "... and does not echo the daemon's raw error line" sh -c '! printf %s "$1" | grep -q "^error: drain_refused"' _ "$out"
: > "$FAKE_LOG"
out="$(FAKE_START_FAIL=other FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --rebuild-only --prefix "$T/bin" 2>&1)"; rc=$?
check "--rebuild-only: a restart failing for another reason still echoes the daemon's line, exit 0 (nearest non-match)" both 0 "$rc" 'error: start failed: other' "$out"
: > "$FAKE_LOG"
out="$(FAKE_START_FAIL=refused FAKE_STATUS='{"up":true,"version":"1.0.0"}' run_install --apply --prefix "$T/bin" 2>&1)"; rc=$?
check "a refused restart without --rebuild-only is a warning too, exit 0" both 0 "$rc" 'warning: '"'"'.*restart'"'"' was refused (drain_refused)' "$out"
check "... with no error: line" sh -c '! printf %s "$1" | grep -q "error:"' _ "$out"
: > "$FAKE_LOG"
FAKE_STATUS='{"up":true,"version":"1.2.3"}' run_install --apply --rebuild-only --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "a running up-to-date daemon is neither started nor restarted, exit 0 (nearest non-match)" test "$rc" -eq 0 -a -z "$(grep -E '^ahd (start|restart)' "$FAKE_LOG")"
: > "$FAKE_LOG"
FAKE_START_FAIL=1 run_install --apply --rebuild-only --no-daemon-restart --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "--no-daemon-restart never starts a stopped daemon" test "$rc" -eq 0 -a -z "$(grep -E '^ahd (start|restart)' "$FAKE_LOG")"

# The stable ah link: created when absent, re-pointed to a directory beside the old one, never put over anything else
phys() { (cd "$1" && pwd -P); }
has() { printf '%s' "$1" | grep -q -- "$2"; }
LB="$HOME/.local/bin"
rm -rf "$LB"
CACHE="$T/cache/agent-hierarchy"
for v in 1 2 3; do mkdir -p "$CACHE/$v"; cp -R "$PLUGIN_COPY/." "$CACHE/$v/"; done
linked() { phys "$(dirname "$(readlink "$LB/ah")")/.."; }
inst() { FAKE_BUILD_ID=30 bash "$CACHE/$1/scripts/install.sh" --apply --rebuild-only --no-daemon-restart 2>&1; }
out=$(inst 1)
check "a placing install creates the ah link when it is absent" same "$(linked)" "$(phys "$CACHE/1")"
check "... and the stamp names that same directory" grep -q "\"plugin_root\":\"$CACHE/1\"" "$AH_HOME/install.json"
check "--rebuild-only does not create the ahd link" test ! -e "$LB/ahd"
out=$(inst 2)
check "a later install re-points the link to the new directory beside the old one" same "$(linked)" "$(phys "$CACHE/2")"
check "... leaves no temporary link behind" test -z "$(ls "$LB" | grep tmp)"
out=$(inst 2)
check "... and a repeat says there is nothing to do" has "$out" "already leads to"
ln -sfn /tmp/fake/bin/ah "$LB/ah"
out=$(inst 3)
check "a link to a path outside any plugin directory is left alone" same "$(readlink "$LB/ah")" /tmp/fake/bin/ah
check "... with the reason, and the fallback named" sh -c 'printf %s "$1" | grep -q "Not touching" && printf %s "$1" | grep -q "fall back"' _ "$out"
mkdir -p "$T/otherplug/bin" "$T/otherplug/.claude-plugin"
printf '{"name":"something-else","version":"1"}\n' > "$T/otherplug/.claude-plugin/plugin.json"
ln -sfn "$T/otherplug/bin/ah" "$LB/ah"
out=$(inst 3)
check "a link into another plugin's directory is left alone" same "$(readlink "$LB/ah")" "$T/otherplug/bin/ah"
mkdir -p "$T/cache2/agent-hierarchy/9"
cp -R "$PLUGIN_COPY/." "$T/cache2/agent-hierarchy/9/"
ln -sfn "$T/cache2/agent-hierarchy/9/bin/ah" "$LB/ah"
out=$(inst 3)
check "a link into a copy of this plugin elsewhere (another state home's) is left alone (nearest non-match)" same "$(readlink "$LB/ah")" "$T/cache2/agent-hierarchy/9/bin/ah"
ln -sfn "$CACHE/gone/bin/ah" "$LB/ah"
out=$(inst 3)
check "a link to a removed directory not named like a version directory is left alone" same "$(readlink "$LB/ah")" "$CACHE/gone/bin/ah"
check "... with the reason" has "$out" "is gone and is not named like a version directory"
ln -sfn "$CACHE/9.9.9+g1a2b3c4/bin/ah" "$LB/ah"
out=$(inst 3)
check "a link to a pruned dogfood version directory (+g<sha>) beside this one is re-pointed" same "$(linked)" "$(phys "$CACHE/3")"
ln -sfn "$CACHE/9.9.8/bin/ah" "$LB/ah"
out=$(inst 3)
check "a link to a pruned release directory beside this one is re-pointed" same "$(linked)" "$(phys "$CACHE/3")"
ln -sfn "$CACHE/2.5.0-pre.1.1873/bin/ah" "$LB/ah"
out=$(inst 3)
check "a link to a pruned prerelease directory beside this one is re-pointed" same "$(linked)" "$(phys "$CACHE/3")"
ln -sfn "$T/cache/elsewhere/9.9.9/bin/ah" "$LB/ah"
out=$(inst 3)
check "a version-named dangling target in another parent is left alone (the parent is the bound)" same "$(readlink "$LB/ah")" "$T/cache/elsewhere/9.9.9/bin/ah"
rm -f "$LB/ah"
printf 'mine\n' > "$LB/ah"
out=$(inst 3)
check "a regular file where the link goes is left as it is" same "$(cat "$LB/ah")" mine
rm -f "$LB/ah"
out=$(FAKE_BUILD_ID=30 bash "$CACHE/3/scripts/install.sh" --apply --rebuild-only --no-daemon-restart --prefix "$LB/" 2>&1)
check "the default prefix spelled with a trailing slash still gets the ah link" same "$(linked)" "$(phys "$CACHE/3")"
rm -f "$LB/ah"
before=$(snapshot "$T/lbin")
out=$(FAKE_BUILD_ID=30 bash "$CACHE/3/scripts/install.sh" 2>&1)
check "a plan makes no link" same "$before" "$(snapshot "$T/lbin")"
check "... and says it would" has "$out" "would: link $LB/ah"
before=$(snapshot "$HOME/.local")
out=$(FAKE_BUILD_ID=30 bash "$CACHE/3/scripts/install.sh" --apply --rebuild-only --no-daemon-restart --prefix "$T/otherbin" 2>&1)
check "a non-default --prefix makes no ah link" test ! -e "$T/otherbin/ah"
check "... leaves the default location as it was" same "$before" "$(snapshot "$HOME/.local")"
check "... and says the stable path is not in use" has "$out" "stable ah path .* is not in use"

# E-DF3: the shell compare orders a 4-part prerelease numerically, as Versions.Compare does
. "$PLUGIN/scripts/lib-version.sh"
cmp_is() { [ "$(ah_version_cmp "$1" "$2")" = "$3" ]; }
check "E-DF3 2.5.0-pre.1.9 sorts below 2.5.0-pre.1.10" cmp_is 2.5.0-pre.1.9 2.5.0-pre.1.10 -1
check "E-DF3 ... and 2.5.0-pre.1.10 above 2.5.0-pre.1.9" cmp_is 2.5.0-pre.1.10 2.5.0-pre.1.9 1
check "E-DF3 2.5.0-pre.1 < 2.5.0-pre.1.1873 < 2.5.0-pre.1.1874 < 2.5.0-pre.2" \
  sh -c '. "$1"; [ "$(ah_version_cmp 2.5.0-pre.1 2.5.0-pre.1.1873)" = -1 ] && [ "$(ah_version_cmp 2.5.0-pre.1.1873 2.5.0-pre.1.1874)" = -1 ] && [ "$(ah_version_cmp 2.5.0-pre.1.1874 2.5.0-pre.2)" = -1 ]' _ "$PLUGIN/scripts/lib-version.sh"
check "E-DF3 equal versions compare 0 (nearest non-match of the ordering)" cmp_is 2.5.0-pre.1.9 2.5.0-pre.1.9 0
check "E-DF3 a missing part counts as 0" cmp_is 2.5.0 2.5.0.0 0
check "E-DF3 1.9.0 sorts below 1.10.0 (the old three-key sort already did)" cmp_is 1.9.0 1.10.0 -1

# the table the C# Versions.Compare is tested against: every chain ascends for every pair, every equal group compares equal
table_failures=0; table_pairs=0
while IFS= read -r line; do
  case "$line" in ''|'#'*) continue ;; esac
  read -r -a vs <<<"$line"
  if [ "${vs[0]}" = "=" ]; then
    for a in "${vs[@]:1}"; do for b in "${vs[@]:1}"; do
      table_pairs=$((table_pairs + 1)); [ "$(ah_version_cmp "$a" "$b")" = 0 ] || { table_failures=$((table_failures + 1)); echo "  $a != $b" >&2; }
    done; done
  else
    for ((i = 0; i < ${#vs[@]}; i++)); do for ((j = i + 1; j < ${#vs[@]}; j++)); do
      table_pairs=$((table_pairs + 2))
      [ "$(ah_version_cmp "${vs[i]}" "${vs[j]}")" = -1 ] || { table_failures=$((table_failures + 1)); echo "  ${vs[i]} !< ${vs[j]}" >&2; }
      [ "$(ah_version_cmp "${vs[j]}" "${vs[i]}")" = 1 ] || { table_failures=$((table_failures + 1)); echo "  ${vs[j]} !> ${vs[i]}" >&2; }
    done; done
  fi
done < "$PLUGIN/tests/fixtures/version-order.txt"
check "version order: the shared table holds for every pair (the same file Versions.Compare is tested against)" test "$table_failures" -eq 0 -a "$table_pairs" -gt 80
check "version order: a release outranks its prerelease (2.5.0 > 2.5.0-pre.1) and not the next one's (2.5.0 < 2.5.1-pre.1)" \
  sh -c '. "$1"; [ "$(ah_version_cmp 2.5.0 2.5.0-pre.1)" = 1 ] && [ "$(ah_version_cmp 2.5.0 2.5.1-pre.1)" = -1 ]' _ "$PLUGIN/scripts/lib-version.sh"

# the daemon step decides on that order: a running 4-part version below the plugin's is restarted, one above it is not
printf '{"name":"agent-hierarchy","version":"2.5.0-pre.1.10"}\n' > "$PLUGIN_COPY/.claude-plugin/plugin.json"
: > "$FAKE_LOG"
FAKE_STATUS='{"up":true,"version":"2.5.0-pre.1.9"}' run_install --apply --prefix "$T/bin" >/dev/null 2>&1
check "E-DF3 a daemon at 2.5.0-pre.1.9 is restarted for a plugin at 2.5.0-pre.1.10" grep -q '^ahd restart --plain$' "$FAKE_LOG"
: > "$FAKE_LOG"
FAKE_STATUS='{"up":true,"version":"2.5.0-pre.1.11"}' run_install --apply --prefix "$T/bin" >/dev/null 2>&1
check "E-DF3 a daemon at 2.5.0-pre.1.11 is not restarted for a plugin at 2.5.0-pre.1.10" test -z "$(grep '^ahd restart' "$FAKE_LOG")"
printf '{"name":"agent-hierarchy","version":"1.2.3"}\n' > "$PLUGIN_COPY/.claude-plugin/plugin.json"

# --- lifecycle permissions ---------------------------------------------------------------------------------
perm_calls() { grep -c 'permissions ' "$FAKE_LOG"; }
: > "$FAKE_LOG"
out="$(run_install --prefix "$T/bin" </dev/null 2>&1)"
check "L-1 a dry run names the step and where the rule list comes from" grep -q 'ah roster permissions rules' <<<"$out"
check "L-1 ... and carries no rule list of its own (nearest non-match: a Bash( rule line)" test -z "$(grep 'Bash(' <<<"$out")"
check "L-1 a dry run never calls the verb" test "$(perm_calls)" -eq 0
: > "$FAKE_LOG"
run_install --prefix "$T/bin" --allow-lifecycle </dev/null >/dev/null 2>&1
check "L-2 a dry run with --allow-lifecycle never calls the verb" test "$(perm_calls)" -eq 0

: > "$FAKE_LOG"
run_install --apply --prefix "$T/bin" --allow-lifecycle </dev/null >/dev/null 2>&1
check "L-3 --allow-lifecycle calls 'ah roster permissions add' on the new binary" \
  grep -q "^ahd --plugin-root $PLUGIN_COPY ah roster permissions add --cwd $HOME\$" "$FAKE_LOG"
check "L-3 ... and not remove" test -z "$(grep 'permissions remove' "$FAKE_LOG")"
: > "$FAKE_LOG"
run_install --apply --prefix "$T/bin" --no-allow-lifecycle </dev/null >/dev/null 2>&1
check "L-4 --no-allow-lifecycle calls 'ah roster permissions remove'" grep -q 'permissions remove --cwd' "$FAKE_LOG"
check "L-4 ... and not add" test -z "$(grep 'permissions add' "$FAKE_LOG")"

: > "$FAKE_LOG"
out="$(FAKE_PERM_STATE=ask run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "L-5 without a terminal an offer is only a hint" test -z "$(grep 'permissions add' "$FAKE_LOG")"
check "L-5 ... that names the opt-in flag" grep -q 'install.sh --apply --allow-lifecycle' <<<"$out"
: > "$FAKE_LOG"
out="$(FAKE_PERM_STATE=ask run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-5 --quiet never adds" test -z "$(grep 'permissions add' "$FAKE_LOG")"
check "L-5 ... and prints nothing for the step" same "$out" ""
: > "$FAKE_LOG"
out="$(FAKE_PERM_STATE='skip: owner proof unavailable' run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "L-6 a skip answer only asks the binary and prints no offer" test "$(perm_calls)" -eq 1 -a -z "$(grep 'permissions add' "$FAKE_LOG")" -a -z "$(grep 'allow-lifecycle' <<<"$out")"
: > "$FAKE_LOG"
FAKE_PERM_STATE=sync run_install --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
check "L-7 a 'sync' answer refreshes the rules without a prompt" grep -q 'permissions sync --cwd' "$FAKE_LOG"
: > "$FAKE_LOG"
FAKE_PERM_STATE=ask run_install --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
check "L-7 an 'ask' answer does not sync (nearest non-match)" test -z "$(grep 'permissions sync' "$FAKE_LOG")"

: > "$FAKE_LOG"
FAKE_PERM_STATE=sync run_install --apply --rebuild-only --prefix "$T/bin" --allow-lifecycle </dev/null >/dev/null 2>&1
check "L-8 --rebuild-only never calls the verb" test "$(perm_calls)" -eq 0
: > "$FAKE_LOG"
FAKE_PUBLISH_FAIL=1 FAKE_PERM_STATE=sync run_install --apply --prefix "$T/bin" --allow-lifecycle </dev/null >/dev/null 2>&1; rc=$?
check "L-9 a failed build exits non-zero and never calls the verb" test "$rc" -ne 0 -a "$(perm_calls)" -eq 0
: > "$FAKE_LOG"
out="$(run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-10 --quiet still prints the one result line of a write" test "$(grep -c 'lifecycle rules added' <<<"$out")" -eq 1 -a "$(wc -l <<<"$out")" -eq 1
check "L-10 ... naming the settings path, the backup and how to revoke" sh -c 'printf %s "$1" | grep -q "settings.json" && printf %s "$1" | grep -q "settings.json.ah2-bak" && printf %s "$1" | grep -q -- "--no-allow-lifecycle"' _ "$out"
check "L-10 ... counting the rules the binary lists (3, from the stub)" grep -q '(3 allow rules' <<<"$out"
out="$(CLAUDE_CONFIG_DIR="$T/cfg" run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-10 CLAUDE_CONFIG_DIR is the settings path the line names" grep -q "$T/cfg/settings.json" <<<"$out"
out="$(run_install --apply --quiet --prefix "$T/bin" --no-allow-lifecycle </dev/null 2>&1)"
check "L-10 --no-allow-lifecycle --quiet prints its result line too" grep -q 'lifecycle rules removed' <<<"$out"

# --- opt-in rules (#142) -----------------------------------------------------------------------------------
: > "$FAKE_LOG"
out="$(run_install --apply --prefix "$T/bin" --allow-lifecycle --no-allow-lifecycle </dev/null 2>&1)"; rc=$?
check "L-11 both flags exit 2" test "$rc" -eq 2
check "L-11 ... before any build or verb call" test "$(publishes)" -eq 0 -a "$(perm_calls)" -eq 0
: > "$FAKE_LOG"
out="$(run_install --apply --prefix "$T/bin" --allow-lifecycle --allow-lifecycle </dev/null 2>&1)"; rc=$?
check "L-11 the same flag twice is not a contradiction (nearest non-match)" test "$rc" -eq 0

# in-session refusal (#141): decided at parse time, from the verb's environment legs
for var in CLAUDECODE CLAUDE_CODE_SESSION_ID AH_SPAWN_DEPTH; do
  for flag in --allow-lifecycle --no-allow-lifecycle; do
    for mode in --apply ""; do
      : > "$FAKE_LOG"
      before="$(snapshot "$HOME")"
      out="$(env "$var=1" bash "$INSTALL" $mode --prefix "$T/bin" "$flag" </dev/null 2>&1)"; rc=$?
      after="$(snapshot "$HOME")"
      check "L-12 $var $flag ${mode:-dry-run} exits non-zero" test "$rc" -ne 0
      check "L-12 ... before any build, verb call or output of the build" test "$(publishes)" -eq 0 -a "$(perm_calls)" -eq 0 -a -z "$(grep -i 'publish\|building' <<<"$out")"
      check "L-12 ... with nothing written (no settings, no .ah2-bak, HOME and AH_HOME identical)" same "$before" "$after"
    done
  done
done
out="$(CLAUDECODE=1 bash "$INSTALL" --apply --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-12 the message points at a terminal outside Claude Code" grep -q 'terminal outside Claude Code' <<<"$out"
: > "$FAKE_LOG"
CLAUDECODE=1 bash "$INSTALL" --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1; rc=$?
check "L-12 no flag in a session is not refused: the build runs (nearest non-match)" test "$rc" -eq 0 -a "$(publishes)" -ge 1

: > "$FAKE_LOG"
rm -f "$AH_HOME/permissions.json"
out="$(CLAUDECODE=1 FAKE_PERM_STATE=ask bash "$INSTALL" --apply --prefix "$T/bin" </dev/null 2>&1)"; rc=$?
check "L-13 no flag in a session: the build runs, the step is skipped with a line, the verb is never called" test "$rc" -eq 0 -a "$(publishes)" -ge 1 -a "$(perm_calls)" -eq 0 -a -n "$(grep 'agent session' <<<"$out")"
check "L-13 ... with no ledger the line does not claim stale rules (nearest non-match)" test -z "$(grep 'old version' <<<"$out")"
mkdir -p "$AH_HOME"
printf '{"v":1,"added":[],"plugin_root":"/old","at":"2026-01-02T03:04:05.000Z","answer":"yes"}\n' > "$AH_HOME/permissions.json"
: > "$FAKE_LOG"
out="$(CLAUDECODE=1 FAKE_PERM_STATE=sync bash "$INSTALL" --apply --prefix "$T/bin" </dev/null 2>&1)"
check "L-13 ledger yes in a session: no sync, a line says the rules name the old version" test "$(perm_calls)" -eq 0 -a -n "$(grep 'old version' <<<"$out")"

# a recorded yes is refreshed visibly
: > "$FAKE_LOG"
out="$(FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-14 sync with --quiet still prints one line" test "$(wc -l <<<"$out")" -eq 1 -a -n "$(grep 'lifecycle rules refreshed' <<<"$out")"
check "L-14 ... naming the version, the date the rules were previously set and how to revoke" sh -c 'printf %s "$1" | grep -q "for 1.2.3" && printf %s "$1" | grep -q "previously set on 2026-01-02" && printf %s "$1" | grep -q -- "--no-allow-lifecycle"' _ "$out"
out="$(FAKE_PERM_STATE=ask run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-14 no sync line when the binary does not say sync (nearest non-match)" test -z "$out"
rm -f "$AH_HOME/permissions.json"
: > "$FAKE_LOG"
out="$(FAKE_PERM_STATE='skip: the user answered no' run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-14 a ledger without a yes never syncs" test "$(grep -c 'permissions sync' "$FAKE_LOG")" -eq 0 -a -z "$out"

# Already present: `check` prints "sync" for every recorded yes (PermissionsVerb.Check; RosterDoctorVerbTests pins "ask\n",
# "sync\n" and "skip: the user answered no\n"); the ledger's plugin root says the rules already name this install.
PERM_SRC="$PLUGIN/native/src/Ah.Commands/Roster/Read/PermissionsVerb.cs"
PERM_TEST="$PLUGIN/native/tests/Ah.Cli.Tests/RosterDoctorVerbTests.Permissions.cs"
check "L-15 the states install.sh branches on are the ones the verb prints and its test pins" \
  sh -c 'grep -q "\"yes\" => \"sync\"" "$1" && grep -q "_ => \"ask\"" "$1" && grep -q "Assert.Equal(\"sync\\\\n\"" "$2" && grep -q "Assert.Equal(\"ask\\\\n\"" "$2"' _ "$PERM_SRC" "$PERM_TEST"
check "L-15 the report shape install.sh parses is the one the verb renders (indented JSON with changed, notices, warnings)" \
  sh -c 'grep -qF "JsonText.Stringify(payload, 2)" "$3" && grep -qF "[\"changed\"] = changed" "$1" && grep -qF "[\"notices\"] = Texts" "$1" && grep -qF "[\"warnings\"] = Texts" "$1"' _ "$PERM_SRC" "$PERM_TEST" "$PLUGIN/native/src/Ah.Commands/Roster/Common/RosterContext.cs"
check "L-15 install.sh branches on no state the verb never prints (no ok case)" test -z "$(grep -E '^[[:space:]]+ok\)' "$INSTALL")"
printf '{"v":1,"added":[],"plugin_root":"%s","at":"2026-01-02T03:04:05.000Z","answer":"yes"}\n' "$PLUGIN_COPY" > "$AH_HOME/permissions.json"
: > "$FAKE_LOG"
out="$(FAKE_PERM_STATE=sync run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "L-15 sync with the rules already naming this root: one status line, no add, no sync" test -n "$(grep 'already in place' <<<"$out")" -a -z "$(grep 'permissions add\|permissions sync' "$FAKE_LOG")"
out="$(FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-15 ... and none with --quiet" same "$out" ""
printf '{"v":1,"added":[],"plugin_root":"%s/older","at":"2026-01-02T03:04:05.000Z","answer":"yes"}\n' "$PLUGIN_COPY" > "$AH_HOME/permissions.json"
: > "$FAKE_LOG"
out="$(FAKE_PERM_STATE=sync run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "L-15 sync with the rules naming another root still refreshes (nearest non-match)" test -n "$(grep 'permissions sync' "$FAKE_LOG")" -a -n "$(grep 'lifecycle rules refreshed' <<<"$out")" -a -z "$(grep 'already in place' <<<"$out")"
rm -f "$AH_HOME/permissions.json"

# --rebuild-only ignores the flag, and says so
: > "$FAKE_LOG"
out="$(run_install --apply --rebuild-only --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-16 --rebuild-only --allow-lifecycle: no verb call, an 'ignoring' line" test "$(perm_calls)" -eq 0 -a -n "$(grep -i 'ignoring the lifecycle' <<<"$out")"
out="$(run_install --apply --rebuild-only --prefix "$T/bin" </dev/null 2>&1)"
check "L-16 ... and no such line without a flag (nearest non-match)" test -z "$(grep -i 'ignoring the lifecycle' <<<"$out")"

# a terminal: the rules are shown before the question; Enter and n write nothing; y adds
tty_answer() { # tty_answer <answer> <env> -- runs install.sh under a pty with <answer> typed
  printf '%s\n' "$1" | env FAKE_PERM_STATE=ask script -q /dev/null bash "$INSTALL" --apply --no-ux-skills --prefix "$T/bin" 2>&1
}
: > "$FAKE_LOG"
out="$(tty_answer y)"
check "L-17 at a terminal the rules and the doc path come before the question" sh -c 'r=$(printf %s "$1" | grep -n "roster one" | head -1 | cut -d: -f1); q=$(printf %s "$1" | grep -n "Add these rules" | head -1 | cut -d: -f1); d=$(printf %s "$1" | grep -n "lifecycle-permissions.md" | head -1 | cut -d: -f1); [ -n "$r" ] && [ -n "$q" ] && [ -n "$d" ] && [ "$r" -lt "$q" ] && [ "$d" -lt "$q" ]' _ "$out"
check "L-17 y adds, with a result line" test -n "$(grep 'permissions add' "$FAKE_LOG")" -a -n "$(grep 'lifecycle rules added' <<<"$out")"
: > "$FAKE_LOG"
out="$(tty_answer "")"
check "L-17 Enter (default No) writes nothing and names the flag for later" test -z "$(grep 'permissions add' "$FAKE_LOG")" -a -n "$(grep 'install.sh --apply --allow-lifecycle' <<<"$out")"
: > "$FAKE_LOG"
out="$(tty_answer n)"
check "L-17 n writes nothing (nearest non-match of y)" test -z "$(grep 'permissions add' "$FAKE_LOG")"
: > "$FAKE_LOG"
out="$(printf 'y\n' | env FAKE_PERM_STATE=ask FAKE_RULES= script -q /dev/null bash "$INSTALL" --apply --no-ux-skills --prefix "$T/bin" 2>&1)"
check "L-17 a binary that cannot list the rules is never asked about (no add)" test -z "$(grep 'permissions add' "$FAKE_LOG")"

# the verb's own report is not echoed: only install.sh's line is
out="$(run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-18 the verb's own report is not printed on success (add)" test -z "$(grep VERB-REPORT <<<"$out")"
out="$(FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-18 ... nor on sync" test -z "$(grep VERB-REPORT <<<"$out")"
out="$(run_install --apply --quiet --prefix "$T/bin" --no-allow-lifecycle </dev/null 2>&1)"
check "L-18 ... nor on remove" test -z "$(grep VERB-REPORT <<<"$out")"

# the verb's notice about rules that exist but are off is shown after the result line, when the settings changed
NOTICE='2 new lifecycle rules available (a, b); add them with install.sh --allow-lifecycle'
printf '{"v":1,"added":[],"plugin_root":"%s/older","at":"2026-01-02T03:04:05.000Z","answer":"yes"}\n' "$PLUGIN_COPY" > "$AH_HOME/permissions.json"
out="$(FAKE_NOTICE="$NOTICE" FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-20 a sync that changed the settings prints the notice after the result line, --quiet included" test "$(sed -n 1p <<<"$out" | grep -c 'lifecycle rules refreshed')" -eq 1 -a "$(sed -n 2p <<<"$out")" = "$NOTICE" -a "$(wc -l <<<"$out")" -eq 2
out="$(FAKE_NOTICE="$NOTICE" run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-20 an add that changed the settings prints it too" test "$(sed -n 2p <<<"$out")" = "$NOTICE"
out="$(FAKE_PERM_CHANGED=false FAKE_NOTICE="$NOTICE" FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-20 nothing changed: the notice is not printed (nearest non-match)" test -z "$(grep 'new lifecycle rules' <<<"$out")"
out="$(FAKE_NOTICE="$NOTICE" FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" --no-allow-lifecycle </dev/null 2>&1)"
check "L-20 a remove never prints it" test -z "$(grep 'new lifecycle rules' <<<"$out")"
printf '{"v":1,"added":[],"plugin_root":"%s","at":"2026-01-02T03:04:05.000Z","answer":"yes"}\n' "$PLUGIN_COPY" > "$AH_HOME/permissions.json"
out="$(FAKE_NOTICE="$NOTICE" FAKE_PERM_STATE=sync run_install --apply --prefix "$T/bin" </dev/null 2>&1)"
check "L-20 the already-in-place path calls no verb and prints no notice" test -z "$(grep 'new lifecycle rules' <<<"$out")" -a -n "$(grep 'already in place' <<<"$out")"
rm -f "$AH_HOME/permissions.json"

# the refresh line names the date read before the sync rewrote the ledger
printf '{"v":1,"added":[],"plugin_root":"%s/older","at":"2026-01-02T03:04:05.000Z","answer":"yes"}\n' "$PLUGIN_COPY" > "$AH_HOME/permissions.json"
out="$(FAKE_REWRITE_LEDGER=1 FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" </dev/null 2>&1)"
check "L-21 the refresh line shows the date from before the sync, not the rewritten one" test -n "$(grep 'previously set on 2026-01-02' <<<"$out")" -a -z "$(grep 2099 <<<"$out")"
# the verb's warnings about the write are shown like its notices
out="$(FAKE_WARNING='settings.json is a link' run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-21 a warning in the report is printed after the result line" test "$(sed -n 2p <<<"$out")" = "settings.json is a link"
out="$(FAKE_PERM_CHANGED=false FAKE_WARNING='settings.json is a link' run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1)"
check "L-21 ... not when nothing changed (nearest non-match)" test -z "$(grep 'is a link' <<<"$out")"
rm -f "$AH_HOME/permissions.json"
# the retry names a terminal only for a session refusal
out="$(FAKE_PERM_REFUSE=1 FAKE_PERM_REFUSE_TEXT='settings.json is not valid JSON' run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null 2>&1 >/dev/null)"
check "L-21 a refusal that is not about a session gives a plain retry (no terminal hint)" test -n "$(grep 'Retry: ' <<<"$out")" -a -z "$(grep 'terminal outside' <<<"$out")"

# a refused verb under an explicit flag: the install is kept, the failure is loud and the exit is non-zero
for flag in --allow-lifecycle --no-allow-lifecycle; do
  : > "$FAKE_LOG"
  out="$(FAKE_PERM_REFUSE=1 run_install --apply --quiet --prefix "$T/bin" "$flag" </dev/null 2>"$T/err")"; rc=$?
  err="$(cat "$T/err")"
  check "L-19 $flag refused by the verb: exit non-zero" test "$rc" -ne 0
  check "L-19 ... the failure is on stderr even with --quiet, with the verb's reason and the retry" sh -c 'printf %s "$1" | grep -q "were not" && printf %s "$1" | grep -q "refused: stub session" && printf %s "$1" | grep -q -- "terminal outside Claude Code"' _ "$err"
  check "L-19 ... the build and install still happened" test "$(publishes)" -ge 1 -a -x "$AH_HOME/bin/ahd"
  check "L-19 ... and no success line was printed" test -z "$(grep 'lifecycle rules \(added\|removed\)' <<<"$out")"
done
: > "$FAKE_LOG"
out="$(FAKE_PERM_REFUSE=1 FAKE_PERM_STATE=sync run_install --apply --quiet --prefix "$T/bin" </dev/null 2>"$T/err")"; rc=$?
check "L-19 no flag, a refused sync: exit 0 (nearest non-match), failure still on stderr" test "$rc" -eq 0 -a -n "$(grep 'were not refreshed' "$T/err")"
run_install --apply --quiet --prefix "$T/bin" --allow-lifecycle </dev/null >/dev/null 2>&1; rc=$?
check "L-19 the same flag without a refusal exits 0 (nearest non-match)" test "$rc" -eq 0

# --- P-14: uninstall ---------------------------------------------------------------------------------------
printf '# top\n\n# >>> ah >>>\n# Added by agent-hierarchy'"'"'s installer. Remove with scripts/uninstall.sh.\n# <<< ah <<<\n# bottom\n' > "$HOME/.zshrc"
printf 'x\n' > "$AH_HOME/logs.keep"
before="$(snapshot "$HOME")"
bash "$UNINSTALL" --prefix "$T/bin" >/dev/null 2>&1
check "P-14 an uninstall dry run changes nothing" same "$before" "$(snapshot "$HOME")"
bash "$UNINSTALL" --apply --prefix "$T/bin" >/dev/null 2>&1; rc=$?
check "P-14 uninstall --apply exits 0" test "$rc" -eq 0
check "P-14 it removed the link, the binary, the sidecar and the stamp" \
  test ! -e "$T/bin/ahd" -a ! -L "$T/bin/ahd" -a ! -e "$AH_HOME/bin/ahd" -a ! -e "$AH_HOME/bin/ahd.contract" -a ! -e "$AH_HOME/install.json"
check "P-14 it removed ahd-tui and its sidecar" test ! -e "$AH_HOME/bin/ahd-tui" -a ! -e "$AH_HOME/bin/ahd-tui.contract"
check "P-14 it removed the PATH symlink" test ! -L "$HOME/bin/ahd"
check "P-14 it removed the rc block and kept every other line" same "$(cat "$HOME/.zshrc")" "$(printf '# top\n# bottom')"
check "P-14 it kept every other file in AH_HOME" test -f "$AH_HOME/msgs/keep.txt" -a -f "$AH_HOME/logs.keep"
check "P-14 it asked the binary to turn the login item off and stop" \
  test -n "$(grep '^ahd login off' "$FAKE_LOG")" -a -n "$(grep '^ahd stop' "$FAKE_LOG")"

# a link that points elsewhere is kept; a foreign file is not removed
mkdir -p "$AH_HOME/bin"
printf '#!/bin/sh\nexit 0\n' > "$AH_HOME/bin/ahd"; chmod +x "$AH_HOME/bin/ahd"
ln -s /somewhere/else/ahd "$T/bin/ahd"
bash "$UNINSTALL" --apply --prefix "$T/bin" >/dev/null 2>&1
check "P-14 a link that points elsewhere is kept" same "$(readlink "$T/bin/ahd")" "/somewhere/else/ahd"
rm -f "$T/bin/ahd"
printf 'mine\n' > "$T/bin/ahd"
bash "$UNINSTALL" --apply --prefix "$T/bin" >/dev/null 2>&1
check "P-14 a regular file at the link path is kept" same "$(cat "$T/bin/ahd")" "mine"

mkdir -p "$AH_HOME/dotnet" "$AH_HOME/build/x"
bash "$UNINSTALL" --apply --prefix "$T/bin" --remove-sdk >/dev/null 2>&1
check "P-14 --remove-sdk removes only the private SDK" test ! -e "$AH_HOME/dotnet" -a -d "$AH_HOME/build"
bash "$UNINSTALL" --apply --prefix "$T/bin" --remove-build-cache >/dev/null 2>&1
check "P-14 --remove-build-cache removes only the build cache" test ! -e "$AH_HOME/build" -a -f "$AH_HOME/msgs/keep.txt"

# --- P-16: uninstall removes <prefix>/ah only when it is a link into a Wrangl plugin --------------------------
# The links are left dangling, as /plugin uninstall leaves them.
U_CACHE="$HOME/.claude/plugins/cache"
for to in "$U_CACHE/wrangl/wrangl/2.0.0/bin/ah" "$U_CACHE/wrangl-dogfood/wrangl/2.1.0-pre.1/bin/ah"; do
  rm -f "$T/bin/ah"; ln -s "$to" "$T/bin/ah"
  out="$(bash "$UNINSTALL" --prefix "$T/bin" </dev/null 2>&1)"
  check "P-16 a dry run lists the ah link into $to and keeps it" sh -c '[ -L "$1" ] && printf %s "$2" | grep -qF "would: remove $1 -> $3"' _ "$T/bin/ah" "$out" "$to"
  bash "$UNINSTALL" --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
  check "P-16 --apply removes the ah link into $to" test ! -L "$T/bin/ah"
done
printf '{"version":"1.2.3","plugin_root":"%s","built_at":"x"}\n' "$T/manual" > "$AH_HOME/install.json"
ln -s "$T/manual/bin/ah" "$T/bin/ah"
bash "$UNINSTALL" --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
check "P-16 --apply removes the ah link into the plugin root the stamp records" test ! -L "$T/bin/ah"
ln -s "$T/manual/bin/ah" "$T/bin/ah"
bash "$UNINSTALL" --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
check "P-16 ... and keeps that link once no stamp records it (nearest non-match)" test -L "$T/bin/ah"
rm -f "$T/bin/ah"
for to in "$U_CACHE/other/wrangl/2.0.0/bin/ah" "$U_CACHE/wrangl/wrangl/2.0.0/bin/ahx" /somewhere/else/bin/ah; do
  ln -s "$to" "$T/bin/ah"
  bash "$UNINSTALL" --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
  check "P-16 a link to $to is kept" same "$(readlink "$T/bin/ah")" "$to"
  rm -f "$T/bin/ah"
done
printf 'mine\n' > "$T/bin/ah"
out="$(bash "$UNINSTALL" --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-16 a regular file named ah is kept, and said so" sh -c '[ "$(cat "$1")" = mine ] && printf %s "$2" | grep -q "Not removing $1"' _ "$T/bin/ah" "$out"
rm -f "$T/bin/ah"

# --- P-17: uninstall offers to remove the lifecycle rules, only with consent ----------------------------------
# The binary's `ah roster permissions remove` picks exactly the rules its ledger lists (RosterDoctorVerbTests.Permissions:
# AddDoesNotRecordARuleTheUserAlreadyHadAndRemoveLeavesItBehind); uninstall.sh itself never writes the settings file.
reinstall_stub() { # a logging ahd, put back before each --apply (which removes it)
  mkdir -p "$AH_HOME/bin"
  printf '#!/bin/sh\necho "ahd $*" >> "$FAKE_LOG"\ncase "$*" in *"permissions check"*) echo "${FAKE_PERM_STATE:-sync}" ;; esac\nexit 0\n' > "$AH_HOME/bin/ahd"
  chmod +x "$AH_HOME/bin/ahd"
}
reinstall_stub
U_SETTINGS="$HOME/.claude/settings.json"
printf '{"permissions":{"allow":["Bash(my own rule)","Bash(%s/bin/ah roster spawn-one:*)"]}}\n' "$PLUGIN_COPY" > "$U_SETTINGS"
U_BEFORE="$(cksum < "$U_SETTINGS")"
removes() { grep -c "^ahd --plugin-root $PLUGIN_COPY ah roster permissions remove --cwd $HOME\$" "$FAKE_LOG"; }
: > "$FAKE_LOG"
out="$(bash "$UNINSTALL" --prefix "$T/bin" </dev/null 2>&1)"
check "P-17 a dry run lists the offer and never calls the verb" sh -c '[ "$(grep -c permissions "$1")" = 0 ] && printf %s "$2" | grep -q "would: ask .*remove the allow rules ah added"' _ "$FAKE_LOG" "$out"
out="$(bash "$UNINSTALL" --prefix "$T/bin" --remove-lifecycle-rules </dev/null 2>&1)"
check "P-17 a dry run with the flag lists the removal and never calls the verb" sh -c '[ "$(grep -c permissions "$1")" = 0 ] && printf %s "$2" | grep -q "would: remove the allow rules ah added"' _ "$FAKE_LOG" "$out"
: > "$FAKE_LOG"
out="$(bash "$UNINSTALL" --apply --prefix "$T/bin" </dev/null 2>&1)"
check "P-17 no flag and no terminal: rules kept, the flag named" test "$(removes)" -eq 0 -a -n "$(grep -- '--remove-lifecycle-rules' <<<"$out")"
check "P-17 ... and the settings file is byte-identical" same "$(cksum < "$U_SETTINGS")" "$U_BEFORE"
# The answer is typed a moment late: uninstall reaches its question at once, and input written before the pty is set up is lost.
u_tty() { { sleep 1; printf '%s\n' "$1"; } | env FAKE_PERM_STATE="${2:-sync}" script -q /dev/null bash "$UNINSTALL" --apply --prefix "$T/bin" 2>&1; }
for answer in "" n; do
  reinstall_stub; : > "$FAKE_LOG"
  out="$(u_tty "$answer")"
  check "P-17 at a terminal, answer '${answer:-Enter}' asks and keeps the rules" test "$(removes)" -eq 0 -a -n "$(grep 'Remove the allow rules ah added' <<<"$out")"
done
check "P-17 ... and the settings file is byte-identical" same "$(cksum < "$U_SETTINGS")" "$U_BEFORE"
reinstall_stub; : > "$FAKE_LOG"
out="$(u_tty y)"
check "P-17 at a terminal, y removes them through the verb" test "$(removes)" -eq 1
reinstall_stub; : > "$FAKE_LOG"
out="$(u_tty y ask)"
check "P-17 nothing recorded as added: no question, no removal (nearest non-match of the row above)" test "$(removes)" -eq 0 -a -z "$(grep 'Remove the allow rules' <<<"$out")"
reinstall_stub; : > "$FAKE_LOG"
bash "$UNINSTALL" --apply --prefix "$T/bin" --remove-lifecycle-rules </dev/null >/dev/null 2>&1; rc=$?
check "P-17 --remove-lifecycle-rules removes them without asking, before the binary is removed" test "$rc" -eq 0 -a "$(removes)" -eq 1 -a ! -e "$AH_HOME/bin/ahd"
reinstall_stub; : > "$FAKE_LOG"
out="$(CLAUDECODE=1 bash "$UNINSTALL" --apply --prefix "$T/bin" --remove-lifecycle-rules </dev/null 2>&1)"; rc=$?
check "P-17 the flag inside an agent session: exit 1, nothing run or removed" test "$rc" -eq 1 -a ! -s "$FAKE_LOG" -a -x "$AH_HOME/bin/ahd"
out="$(CLAUDECODE=1 bash "$UNINSTALL" --apply --prefix "$T/bin" </dev/null 2>&1)"; rc=$?
check "P-17 no flag inside an agent session: the step is skipped and the rest runs (nearest non-match)" test "$rc" -eq 0 -a "$(removes)" -eq 0 -a ! -e "$AH_HOME/bin/ahd"
rm -f "$U_SETTINGS"

# --- Codex trust -------------------------------------------------------------------------------------------
printf '#!/bin/sh\nexit 0\n' > "$FAKEBIN/codex"
chmod +x "$FAKEBIN/codex"
mkdir -p "$T/repo" "$T/norepo"
git -C "$T/repo" init -q
trust_calls() { grep -c 'ah roster codex-trust' "$FAKE_LOG"; }
trust_yes() { grep -c 'ah roster codex-trust .* --yes' "$FAKE_LOG"; }
in_repo() { (cd "$T/repo" && FAKE_TRUST_RESULT="${FAKE_TRUST_RESULT:-plan}" run_install "$@"); }

: > "$FAKE_LOG"
in_repo --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
check "C-1 without the flag and without a terminal Codex is only asked about (plan, never --yes)" test "$(trust_calls)" -eq 1 -a "$(trust_yes)" -eq 0
: > "$FAKE_LOG"
in_repo --apply --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1
check "C-2 --codex-trust runs the writer with --yes on the checkout" grep -q "ah roster codex-trust .*repo --cwd .*repo --yes" "$FAKE_LOG"
: > "$FAKE_LOG"
FAKE_TRUST_RESULT=unchanged in_repo --apply --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1
check "C-3 an already trusted checkout is not written again (nearest non-match)" test "$(trust_calls)" -eq 1 -a "$(trust_yes)" -eq 0
: > "$FAKE_LOG"
mv "$FAKEBIN/codex" "$FAKEBIN/codex.off"
(cd "$T/repo" && FAKE_TRUST_RESULT=plan PATH="$FAKEBIN:/usr/bin:/bin" run_install --apply --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1)
check "C-4 no codex on PATH: the step does nothing" test "$(trust_calls)" -eq 0
mv "$FAKEBIN/codex.off" "$FAKEBIN/codex"
: > "$FAKE_LOG"
(cd "$T/norepo" && FAKE_TRUST_RESULT=plan run_install --apply --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1)
check "C-5 outside a git checkout: the step does nothing" test "$(trust_calls)" -eq 0
: > "$FAKE_LOG"
CLAUDECODE=1 in_repo --apply --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1; rc=$?
check "C-6 --codex-trust inside an agent session exits 1 before any build or write" test "$rc" -eq 1 -a "$(publishes)" -eq 0 -a "$(trust_calls)" -eq 0
: > "$FAKE_LOG"
CLAUDECODE=1 in_repo --apply --prefix "$T/bin" </dev/null >/dev/null 2>&1
check "C-6 ... and without the flag the session only skips the step (nearest non-match)" test "$(trust_calls)" -eq 0
: > "$FAKE_LOG"
in_repo --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1
check "C-7 a dry run never calls the writer" test "$(trust_calls)" -eq 0
: > "$FAKE_LOG"
in_repo --apply --rebuild-only --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1
check "C-8 --rebuild-only never calls the writer" test "$(trust_calls)" -eq 0
: > "$FAKE_LOG"
FAKE_TRUST_FAIL=1 in_repo --apply --prefix "$T/bin" --codex-trust </dev/null >/dev/null 2>&1; rc=$?
check "C-9 a refused write with the flag exits non-zero" test "$rc" -ne 0
mkdir -p "$T/bin9"
FAKE_TRUST_FAIL=1 in_repo --apply --prefix "$T/bin9" </dev/null >/dev/null 2>&1; rc=$?
check "C-9 ... a failed check without the flag does not (nearest non-match)" test "$rc" -eq 0

gl_has() { grep -q -- "$1" <<<"$2"; }
# --- design skills: frontend-design by default, and the optional third-party sources ---------------------------
UXBIN="$T/uxbin"
mkdir -p "$UXBIN"
UXPATH="$FAKEBIN:$UXBIN:/usr/bin:/bin:/usr/sbin:/sbin"
for tool in claude npx; do
  printf '#!/bin/sh\necho "%s $*" >> "$FAKE_LOG"\n[ "${FAKE_CLAUDE_FAIL:-}" = 1 ] && [ "%s" = claude ] && exit 1\nexit 0\n' "$tool" "$tool" > "$UXBIN/$tool"
  chmod +x "$UXBIN/$tool"
done

ux_run() { # ux_run <case id> <args...>: a fresh AH_HOME and config dir per case unless they exist; no terminal; sets ux_out, ux_err, ux_rc
  local id="$1"; shift
  mkdir -p "$T/ux-$id"
  ux_out="$(AH_HOME="$T/ux-$id" CLAUDE_CONFIG_DIR="$T/uxc-$id" PATH="${UX_PATH:-$UXPATH}" bash "${UX_INSTALL:-$INSTALL}" --prefix "$T/uxprefix-$id" --no-path "$@" </dev/null 2>"$T/ux-$id.err")"; ux_rc=$?
  ux_err="$(cat "$T/ux-$id.err")"
}
ux_stamp() { cat "$T/ux-$1/install.json" 2>/dev/null; }
ux_calls() { grep -c "^$1 " "$FAKE_LOG" 2>/dev/null; true; }
ahfd_dirs() { ls -d /tmp/ahfd.* 2>/dev/null | wc -l | tr -d ' '; }

# the default install: frontend-design, no terminal, no flag
: > "$FAKE_LOG"
ux_run 1 --apply
check "UX-1 default install: exit 0 and the one-line notice" both 0 "$ux_rc" "Added Anthropic's frontend-design skill (Apache-2.0) to .*uxc-1/skills/frontend-design. Remove: delete that folder, or re-run with --no-design-skills." "$ux_out"
check "UX-1 ... both pinned files are in place, byte for byte" test -f "$T/uxc-1/skills/frontend-design/SKILL.md" -a -f "$T/uxc-1/skills/frontend-design/LICENSE.txt" && cmp -s "$T/uxc-1/skills/frontend-design/SKILL.md" "$T/fdfix/SKILL.md" && cmp -s "$T/uxc-1/skills/frontend-design/LICENSE.txt" "$T/fdfix/LICENSE.txt"
check "UX-1 ... the stamp says installed and records the SKILL.md sha256" gl_has '"design_skills":{"frontend_design":"installed","frontend_design_sha256":"'"$FD_SKILL_SHA"'","frontend_design_files":"SKILL.md:'"$FD_SKILL_SHA"' LICENSE.txt:'"$FD_LIC_SHA"'"}' "$(ux_stamp 1)"
check "UX-1 ... no claude or npx call, and no leftover work directories" test "$(ux_calls claude)" -eq 0 -a "$(ux_calls npx)" -eq 0 -a ! -e "$T/uxc-1/skills/.frontend-design.new"
check "UX-10 every curl is an https raw.githubusercontent.com URL at a 40-hex commit under skills/frontend-design, never a branch" bash -c '
  n=$(grep -c "^curl " "$1"); [ "$n" -eq 2 ] || exit 1
  grep "^curl " "$1" | while IFS= read -r l; do
    printf "%s" "$l" | grep -qE "https://raw\.githubusercontent\.com/anthropics/skills/[0-9a-f]{40}/skills/frontend-design/(SKILL\.md|LICENSE\.txt)( |$)" || exit 1
    printf "%s" "$l" | grep -qE "/(main|master|HEAD)/" && exit 1
    printf "%s" "$l" | grep -q -- "--proto =https" || exit 1
  done' _ "$FAKE_LOG"

# second run: present, no fetch
n_curl=$(ux_calls curl)
ux_run 1 --apply
check "UX-4 second run: present, and no fetch" both 0 "$ux_rc" "frontend-design: present\\." "$ux_out"
check "UX-4 ... curl was not called again and the stamp still says installed" test "$(ux_calls curl)" -eq "$n_curl" && gl_has '"frontend_design":"installed"' "$(ux_stamp 1)"

# --no-design-skills
: > "$FAKE_LOG"
ux_run 2 --apply --no-design-skills
check "UX-2 --no-design-skills: nothing installed, no fetch, stamp optout" test "$ux_rc" -eq 0 -a ! -e "$T/uxc-2/skills/frontend-design" -a "$(ux_calls curl)" -eq 0 && gl_has '"frontend_design":"optout"' "$(ux_stamp 2)"
check "UX-2 ... and no third-party offer is printed" test -z "$(grep -i 'design sources' <<<"$ux_err")"
# the nearest input: the flag absent installs
check "UX-2 negative: the same run without the flag installs it" test -d "$T/uxc-1/skills/frontend-design"

# an agent session
: > "$FAKE_LOG"
mkdir -p "$T/ux-3"
ux_out="$(CLAUDECODE=1 AH_HOME="$T/ux-3" CLAUDE_CONFIG_DIR="$T/uxc-3" PATH="$UXPATH" bash "$INSTALL" --prefix "$T/uxprefix-3" --no-path --apply </dev/null 2>"$T/ux-3.err")"; ux_rc=$?
check "UX-3 agent session: the skip line, nothing installed, no fetch, stamp agent-session" test "$ux_rc" -eq 0 -a ! -e "$T/uxc-3/skills/frontend-design" -a "$(ux_calls curl)" -eq 0 && gl_has 'frontend-design: skipped in an agent session; run the installer from a terminal' "$ux_out" && gl_has '"frontend_design":"agent-session"' "$(ux_stamp 3)"

# a checksum mismatch on one file, and a download failure
before=$(ahfd_dirs)
: > "$FAKE_LOG"
FAKE_CURL_BAD=LICENSE.txt ux_run 5 --apply
check "UX-5 a hash mismatch on one file: nothing written, failed, exit 0, one line" test "$ux_rc" -eq 0 -a ! -e "$T/uxc-5/skills/frontend-design" -a ! -e "$T/uxc-5/skills/.frontend-design.new" && gl_has 'frontend-design: download failed or did not verify; skipped' "$ux_err" && gl_has '"frontend_design":"failed"' "$(ux_stamp 5)"
check "UX-5 ... the temporary directory is gone" test "$(ahfd_dirs)" -eq "$before"
FAKE_CURL_FAIL=1 ux_run 6 --apply
check "UX-6 a download failure: nothing written, failed, exit 0, temp dir removed" test "$ux_rc" -eq 0 -a ! -e "$T/uxc-6/skills/frontend-design" -a "$(ahfd_dirs)" -eq "$before" && gl_has '"frontend_design":"failed"' "$(ux_stamp 6)"
ux_run 11 --apply
check "UX-5/6 negative: no failure line when the files verify" test "$ux_rc" -eq 0 -a -z "$(grep 'did not verify' <<<"$ux_err")" && gl_has '"frontend_design":"installed"' "$(ux_stamp 11)"

# an existing directory the user manages
mkdir -p "$T/uxc-7/skills/frontend-design"
printf 'mine\n' > "$T/uxc-7/skills/frontend-design/SKILL.md"
: > "$FAKE_LOG"
ux_run 7 --apply
check "UX-7 an existing user-managed directory: untouched, user-managed, no fetch" test "$(cat "$T/uxc-7/skills/frontend-design/SKILL.md")" = mine -a "$(ux_calls curl)" -eq 0 && gl_has 'frontend-design: present (user-managed); left untouched.' "$ux_out" && gl_has '"frontend_design":"user-managed"' "$(ux_stamp 7)"

# a pin bump replaces what ah wrote, and leaves what the user edited
cp -R "$PLUGIN_COPY" "$T/plugin-v2"
sed "s|^FRONTEND_DESIGN_FILES=.*|FRONTEND_DESIGN_FILES=\"SKILL.md:$FD_SKILL_SHA2 LICENSE.txt:$FD_LIC_SHA2\"|" "$PLUGIN_COPY/scripts/install.sh" > "$T/plugin-v2/scripts/install.sh"
ux_run 8 --apply
: > "$FAKE_LOG"
FD_FIX="$T/fdfix2" UX_INSTALL="$T/plugin-v2/scripts/install.sh" ux_run 8 --apply
check "UX-8 an ah-written copy with an older hash is replaced when the pin moves" cmp -s "$T/uxc-8/skills/frontend-design/SKILL.md" "$T/fdfix2/SKILL.md" && gl_has "\"frontend_design_sha256\":\"$FD_SKILL_SHA2\"" "$(ux_stamp 8)" && test "$(ux_calls curl)" -eq 2
check "UX-8 ... and the replaced directory left nothing behind" test ! -e "$T/uxc-8/skills/.frontend-design.old" -a ! -e "$T/uxc-8/skills/.frontend-design.new"
ux_run 9 --apply
printf 'edited by the user\n' >> "$T/uxc-9/skills/frontend-design/SKILL.md"
: > "$FAKE_LOG"
FD_FIX="$T/fdfix2" UX_INSTALL="$T/plugin-v2/scripts/install.sh" ux_run 9 --apply
check "UX-8 negative: a copy the user edited is left untouched, user-managed, no fetch" gl_has 'edited by the user' "$(cat "$T/uxc-9/skills/frontend-design/SKILL.md")" && gl_has '"frontend_design":"user-managed"' "$(ux_stamp 9)" && test "$(ux_calls curl)" -eq 0

# a dry run and --rebuild-only write nothing
: > "$FAKE_LOG"
ux_run 10
check "UX-11 a dry run prints the would lines and fetches nothing" gl_has 'would: install Anthropic' "$ux_out" && test "$(ux_calls curl)" -eq 0 -a ! -e "$T/uxc-10/skills/frontend-design"

# --- the third-party sources ---
: > "$FAKE_LOG"
ux_run 20 --apply
check "UX-20 non-interactive: the block is printed, nothing is installed, no claude or npx call" gl_has 'Optional design sources for the ux-engineer role' "$ux_err" && test "$(ux_calls claude)" -eq 0 -a "$(ux_calls npx)" -eq 0
check "UX-20 ... and the stamp has no ux_skills entry" test -z "$(grep -o 'ux_skills' <<<"$(ux_stamp 20)")"
: > "$FAKE_LOG"
ux_run 21 --apply --with-ux-skills
check "UX-21 --with-ux-skills: each marketplace source is added and installed exactly once" test "$ux_rc" -eq 0 -a "$(grep -c '^claude plugin marketplace add jakubkrehel/skills$' "$FAKE_LOG")" -eq 1 -a "$(grep -c '^claude plugin install interfaces@interfaces$' "$FAKE_LOG")" -eq 1 -a "$(grep -c '^claude plugin marketplace add addyosmani/web-quality-skills$' "$FAKE_LOG")" -eq 1 -a "$(grep -c '^claude plugin install web-quality-skills@addy-web-quality-skills$' "$FAKE_LOG")" -eq 1
check "UX-21 ... vercel-labs gets manual text only and npx is never run" gl_has "ah never runs npx" "$ux_err" && test "$(ux_calls npx)" -eq 0 -a "$(grep -c 'vercel' "$FAKE_LOG")" -eq 0
check "UX-21 ... the stamp records installed, installed and manual" gl_has '"ux_skills":{"vercel-labs":"manual","jakubkrehel":"installed","addyosmani":"installed"}' "$(ux_stamp 21)"
: > "$FAKE_LOG"
ux_run 22 --apply --no-ux-skills
check "UX-22 --no-ux-skills: no block and no claude call" test -z "$(grep -i 'optional design sources' <<<"$ux_err")" -a "$(ux_calls claude)" -eq 0
ux_run 23 --apply --with-ux-skills --no-ux-skills
check "UX-23 both flags: exit 2, a usage error" both 2 "$ux_rc" "contradict" "$ux_err"
ux_run 24 --apply --with-ux-skills --no-design-skills
check "UX-23 --with-ux-skills with --no-design-skills: exit 2 as well" both 2 "$ux_rc" "contradict" "$ux_err"
ux_run 25 --apply --no-ux-skills
check "UX-23 negative: --no-ux-skills alone is fine" test "$ux_rc" -eq 0
mkdir -p "$T/ux-26"
ux_out="$(CLAUDECODE=1 AH_HOME="$T/ux-26" CLAUDE_CONFIG_DIR="$T/uxc-26" PATH="$UXPATH" bash "$INSTALL" --prefix "$T/uxprefix-26" --no-path --apply --with-ux-skills </dev/null 2>"$T/ux-26.err")"; ux_rc=$?
check "UX-24 --with-ux-skills in an agent session: refused with exit 1 before anything is done" test "$ux_rc" -eq 1 -a ! -e "$T/ux-26/install.json" && gl_has 'which an agent session may not do' "$(cat "$T/ux-26.err")"
: > "$FAKE_LOG"
FAKE_CLAUDE_FAIL=1 ux_run 27 --apply --with-ux-skills
check "UX-25 a failing claude: the install continues, exit 0, failed recorded" test "$ux_rc" -eq 0 && gl_has '"jakubkrehel":"failed"' "$(ux_stamp 27)" && gl_has 'could not install interfaces@interfaces' "$ux_err"
mkdir -p "$T/uxc-28/plugins/cache/interfaces/interfaces" "$T/uxc-28/skills/web-design-guidelines"
: > "$FAKE_LOG"
ux_run 28 --apply --with-ux-skills
check "UX-26 only the missing source is installed (the others are detected as present)" test "$(ux_calls claude)" -eq 2 -a "$(grep -c 'addy' "$FAKE_LOG")" -eq 2 && gl_has '"vercel-labs":"present","jakubkrehel":"present","addyosmani":"installed"' "$(ux_stamp 28)"
mkdir -p "$T/uxc-29/plugins/cache/interfaces/interfaces" "$T/uxc-29/plugins/cache/addy-web-quality-skills/web-quality-skills" "$T/uxc-29/skills/composition-patterns"
: > "$FAKE_LOG"
ux_run 29 --apply --with-ux-skills
check "UX-26 all three present: all installed, no claude call, no block" gl_has 'design sources: all installed.' "$ux_out" && test "$(ux_calls claude)" -eq 0
NOCLAUDE="$T/uxnobin"
mkdir -p "$NOCLAUDE"
: > "$FAKE_LOG"
UX_PATH="$FAKEBIN:$NOCLAUDE:/usr/bin:/bin:/usr/sbin:/sbin" ux_run 30 --apply --with-ux-skills
check "UX-27 no claude on PATH: the slash commands are printed and the stamp says manual" test "$ux_rc" -eq 0 && gl_has '/plugin marketplace add jakubkrehel/skills' "$ux_err" && gl_has '"jakubkrehel":"manual"' "$(ux_stamp 30)"

# ownership: only what this script wrote, exactly as it wrote it, is ever replaced
bump() { FD_FIX="$T/fdfix2" UX_INSTALL="$T/plugin-v2/scripts/install.sh" ux_run "$@"; }
ux_run 31 --apply
printf 'my notes\n' > "$T/uxc-31/skills/frontend-design/NOTES.md"
: > "$FAKE_LOG"
bump 31 --apply
check "UX-13 an extra file in an ah-written copy plus a pin bump: untouched, user-managed, nothing deleted, no fetch" test "$(cat "$T/uxc-31/skills/frontend-design/NOTES.md")" = "my notes" && cmp -s "$T/uxc-31/skills/frontend-design/SKILL.md" "$T/fdfix/SKILL.md" && test "$(ux_calls curl)" -eq 0 && gl_has '"frontend_design":"user-managed"' "$(ux_stamp 31)"
ux_run 32 --apply
printf 'edited license\n' > "$T/uxc-32/skills/frontend-design/LICENSE.txt"
: > "$FAKE_LOG"
bump 32 --apply
check "UX-13 an edited LICENSE.txt plus a pin bump: untouched, user-managed, no fetch" test "$(cat "$T/uxc-32/skills/frontend-design/LICENSE.txt")" = "edited license" && test "$(ux_calls curl)" -eq 0 && gl_has '"frontend_design":"user-managed"' "$(ux_stamp 32)"
mkdir -p "$T/uxc-33/skills/frontend-design"
cp "$T/fdfix/SKILL.md" "$T/fdfix/LICENSE.txt" "$T/uxc-33/skills/frontend-design/"
: > "$FAKE_LOG"
ux_run 33 --apply
check "UX-14 a user-installed copy identical to the pin is present and not claimed (no installed state, no recorded hash)" gl_has '"frontend_design":"present","frontend_design_sha256":null,"frontend_design_files":null' "$(ux_stamp 33)" && test "$(ux_calls curl)" -eq 0
bump 33 --apply
check "UX-14 ... and a later pin bump leaves it untouched, user-managed" cmp -s "$T/uxc-33/skills/frontend-design/SKILL.md" "$T/fdfix/SKILL.md" && test "$(ux_calls curl)" -eq 0 && gl_has '"frontend_design":"user-managed"' "$(ux_stamp 33)"
ux_run 34 --apply
: > "$FAKE_LOG"
bump 34 --apply
check "UX-8 an ah-written exact set plus a pin bump is replaced, nothing else left in the directory" cmp -s "$T/uxc-34/skills/frontend-design/SKILL.md" "$T/fdfix2/SKILL.md" && test "$(ls -A "$T/uxc-34/skills/frontend-design" | wc -l | tr -d ' ')" -eq 2 && test "$(ux_calls curl)" -eq 2
ux_run 34 --apply --no-design-skills
bump 34 --apply
check "UX-8 a --no-design-skills run in between keeps the claim: the copy stays and is still ah's" gl_has '"frontend_design":"present"\|"frontend_design":"installed"' "$(ux_stamp 34)"

# only directories this run created are removed; a symlinked destination is the user's
mkdir -p "$T/uxc-35/skills/.frontend-design.new" "$T/uxc-35/skills/.frontend-design.old"
printf 'keep new\n' > "$T/uxc-35/skills/.frontend-design.new/a.txt"; printf 'keep old\n' > "$T/uxc-35/skills/.frontend-design.old/b.txt"
ux_run 35 --apply
check "UX-15 a user's .frontend-design.new and .old survive a fetch, and the install still lands" test "$(cat "$T/uxc-35/skills/.frontend-design.new/a.txt")" = "keep new" -a "$(cat "$T/uxc-35/skills/.frontend-design.old/b.txt")" = "keep old" && cmp -s "$T/uxc-35/skills/frontend-design/SKILL.md" "$T/fdfix/SKILL.md"
printf 'keep new\n' > "$T/uxc-35/skills/.frontend-design.new/a.txt"
bump 35 --apply
check "UX-15 ... and they survive a replacing fetch too, with no stray work directory left" test "$(cat "$T/uxc-35/skills/.frontend-design.new/a.txt")" = "keep new" -a "$(cat "$T/uxc-35/skills/.frontend-design.old/b.txt")" = "keep old" && cmp -s "$T/uxc-35/skills/frontend-design/SKILL.md" "$T/fdfix2/SKILL.md" && test "$(ls -A "$T/uxc-35/skills" | wc -l | tr -d ' ')" -eq 3
mkdir -p "$T/uxc-36/skills" "$T/uxlink-36"
cp "$T/fdfix/SKILL.md" "$T/fdfix/LICENSE.txt" "$T/uxlink-36/"
ln -s "$T/uxlink-36" "$T/uxc-36/skills/frontend-design"
: > "$FAKE_LOG"
ux_run 36 --apply
bump 36 --apply
check "UX-16 a symlinked frontend-design is user-managed and left alone, its target untouched, no fetch" test -L "$T/uxc-36/skills/frontend-design" -a "$(ux_calls curl)" -eq 0 && cmp -s "$T/uxlink-36/SKILL.md" "$T/fdfix/SKILL.md" && gl_has '"frontend_design":"user-managed"' "$(ux_stamp 36)"

# the pins in the shipped script
check "UX-12 FRONTEND_DESIGN_COMMIT is a 40-hex commit and every FRONTEND_DESIGN_FILES entry has a 64-hex sha256" bash -c '
  c=$(sed -n "s/^FRONTEND_DESIGN_COMMIT=\\(.*\\)\$/\\1/p" "$1"); printf "%s" "$c" | grep -qE "^[0-9a-f]{40}\$" || exit 1
  f=$(sed -n "s/^FRONTEND_DESIGN_FILES=\"\\(.*\\)\"\$/\\1/p" "$1"); [ -n "$f" ] || exit 1
  for e in $f; do printf "%s" "${e#*:}" | grep -qE "^[0-9a-f]{64}\$" || exit 1; [ -n "${e%%:*}" ] || exit 1; done
  printf "%s" "$f" | grep -q "SKILL.md:" ' _ "$PLUGIN/scripts/install.sh"

# --- herdr desktop: the mod is offered, never installed without a yes ---------------------------------------
: > "$FAKE_LOG"
herdr_out=$(HC_TERMINAL_ID=t1 in_repo --apply --prefix "$T/bin" </dev/null 2>&1)
check "H-1 in herdr desktop without a terminal the mod is named and never installed" bash -c 'printf "%s" "$1" | grep -q "ah herdr install" && ! grep -q "ah herdr install" "$2"' _ "$herdr_out" "$FAKE_LOG"
herdr_out=$(HC_TERMINAL_ID= in_repo --apply --prefix "$T/bin" </dev/null 2>&1)
check "H-2 outside herdr desktop the installer does not mention it" bash -c '! printf "%s" "$1" | grep -q "herdr desktop"' _ "$herdr_out"

[ "$FAIL" -eq 0 ]
