#!/bin/bash
# agent-hierarchy — AOT-publishes ahd (Ah.Cli) and ahd-tui (Ah.Tui) of this tree into one bin directory and stamps it, so a lane
# or a suite can run against binaries proven to come from this source. tests/run-set.sh publishes here.
#
#   bash tests/publish-bin.sh --out DIR [--log-dir DIR] [--no-slot]
#   bash tests/publish-bin.sh --stamp
#
#   --out DIR      the bin directory to create: ahd, ahd-tui, the two .contract sidecars and source.stamp
#   --log-dir DIR  where each project's publish output goes (default: DIR itself)
#   --no-slot      publish without taking a machine-wide slot of tests/with-slot.sh
#   --stamp        print the stamp of the current source tree (HEAD plus a hash of the uncommitted and untracked native/ files)
#
# The sidecars are {"version": <plugin.json>, "contract": <native/cutover.json>} for this machine's runtime identifier.
# Exit: 0 published and stamped; 2 a usage error, an unknown platform, an unreadable version, or a failed publish.

HERE="$(cd "$(dirname "$0")" && pwd -P)"
PLUGIN="$(cd "$HERE/.." && pwd -P)"

source_stamp() {
  local head dirty
  head="$(git -C "$PLUGIN" rev-parse HEAD 2>/dev/null)" || { echo nogit; return; }
  dirty="$( { git -C "$PLUGIN" diff HEAD -- native; ( cd "$PLUGIN" && git ls-files -o --exclude-standard -z -- native | xargs -0 shasum 2>/dev/null ); } 2>/dev/null | shasum | cut -d' ' -f1)"
  echo "$head+$dirty"
}

OUT=""; LOGDIR=""; SLOT=1; STAMP_ONLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) [ $# -ge 2 ] || { echo "publish-bin: --out needs a directory" >&2; exit 2; }; OUT="$2"; shift 2;;
    --log-dir) [ $# -ge 2 ] || { echo "publish-bin: --log-dir needs a directory" >&2; exit 2; }; LOGDIR="$2"; shift 2;;
    --no-slot) SLOT=0; shift;;
    --stamp) STAMP_ONLY=1; shift;;
    -h|--help) sed -n 2,14p "$0"; exit 0;;
    *) echo "publish-bin: unknown option $1" >&2; exit 2;;
  esac
done
if [ "$STAMP_ONLY" = 1 ]; then source_stamp; exit 0; fi
[ -n "$OUT" ] || { echo "publish-bin: --out is required" >&2; exit 2; }
[ -n "$LOGDIR" ] || LOGDIR="$OUT"

slot() { local eta=$1; shift; if [ "$SLOT" = 1 ]; then bash "$HERE/with-slot.sh" --eta "$eta" --tokens 2 "$@"; else "$@"; fi; }

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID=osx-arm64;;
  Darwin-x86_64) RID=osx-x64;;
  Linux-x86_64) RID=linux-x64;;
  Linux-aarch64|Linux-arm64) RID=linux-arm64;;
  *) echo "publish-bin: no runtime identifier for $(uname -s)-$(uname -m)" >&2; exit 2;;
esac
VERSION="$(node -p "require('$PLUGIN/.claude-plugin/plugin.json').version")"
CONTRACT="$(node -p "require('$PLUGIN/native/cutover.json').contract")"
NAME="$(node -p "require('$PLUGIN/.claude-plugin/plugin.json').name")"
case "$VERSION$CONTRACT$NAME" in ''|*[!0-9A-Za-z.+-]*) echo "publish-bin: unreadable plugin version, contract or name" >&2; exit 2;; esac
mkdir -p "$LOGDIR" || exit 2
cat > "$LOGDIR/publish.sh" <<PUBLISH
#!/bin/bash
exec bash "$PLUGIN/scripts/publish-native.sh" --dotnet dotnet --native "$PLUGIN/native" --rid "$RID" --out "$OUT" \\
  --version "$VERSION" --contract "$CONTRACT" --name "$NAME" --warnaserror --log-dir "$LOGDIR"
PUBLISH
echo "publish-bin: publishing ahd and ahd-tui ($RID, $VERSION, contract $CONTRACT) into $OUT"
slot large bash "$LOGDIR/publish.sh" || exit 2
printf '{"version":"%s","contract":%s}' "$VERSION" "$CONTRACT" > "$OUT/ahd.contract"
printf '{"version":"%s","contract":%s}' "$VERSION" "$CONTRACT" > "$OUT/ahd-tui.contract"
source_stamp > "$OUT/source.stamp"
[ -x "$OUT/ahd" ] && [ -e "$OUT/ahd-tui" ] || { echo "publish-bin: the publish produced no ahd and ahd-tui in $OUT" >&2; exit 2; }
exit 0
