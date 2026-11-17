#!/usr/bin/env bash
# Wrangl: Agent Hierarchy, one-command install. Adds the wrangl marketplace to Claude Code, installs the plugin
# wrangl@wrangl at user scope, then runs the plugin's own scripts/install.sh, which builds ahd under ~/.claude/ah2/bin
# and links it onto PATH. Running it again is the upgrade path (marketplace update, plugin update, installer again).
#
#   curl -fsSL https://raw.githubusercontent.com/agentic-tool-labs/wrangl/main/install.sh | bash
#   bash install.sh                 (from a clone)
#
# It never edits Claude Code settings, shell rc files or permission rules itself (the claude CLI and the plugin
# installer do their own, consented, writes) and never uses sudo.
# WRANGL_BOOTSTRAP_TTY (tests only): the terminal to ask on, default /dev/tty.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: install.sh [--yes] [--plan] [--source <path-or-repo>] [--skip-build] [--help]

Installs Wrangl: Agent Hierarchy into Claude Code. Shows the plan first and asks before changing anything.

  --yes                   do not ask; the plugin installer runs non-interactively with --auto-rebuild
  --plan                  print the plan and exit; changes nothing
  --source <path-or-repo> marketplace source (default agentic-tool-labs/wrangl); a clone can pass its own path
  --skip-build            install the plugin only; do not run the plugin's scripts/install.sh
  --help                  this text

With no terminal to ask on and no --yes, it prints the plan and exits 0 having changed nothing.
EOF
}

MIN_CLAUDE=2.1.289   # docs/getting-started.md, "Check what you need"
MARKET=wrangl
PLUGIN=wrangl@wrangl
OLD_PLUGIN=ah@agent-tools
OFFICIAL=agentic-tool-labs/wrangl
TTY=${WRANGL_BOOTSTRAP_TTY:-/dev/tty}
CFG=${CLAUDE_CONFIG_DIR:-$HOME/.claude}
DONE=()

say() { printf '%s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

# Runs one step; a failure names the command, what was already done, and that a re-run continues from here.
run() {
  say "+ $*"
  local rc=0
  "$@" </dev/null || rc=$?
  [ "$rc" -eq 0 ] || fail_step "$rc" "$@"
  DONE+=("$*")
}
fail_step() {
  local rc=$1; shift
  printf 'error: failed (exit %s): %s\n' "$rc" "$*" >&2
  if [ ${#DONE[@]} -eq 0 ]; then
    printf 'Nothing was changed before it.\n' >&2
  else
    printf 'Already done:\n' >&2
    printf '  %s\n' "${DONE[@]}" >&2
  fi
  printf 'Fix the cause and run this script again; it picks up where it stopped.\n' >&2
  exit 1
}

have_tty() { [ -n "$TTY" ] && (exec <"$TTY") 2>/dev/null; }
# The terminal is opened once on fd 4, so successive answers are read in order.
TTY_OPEN=""
ask() {
  local reply=""
  [ -n "$TTY_OPEN" ] || { exec 4<"$TTY"; TTY_OPEN=1; }
  printf '%s [y/N] ' "$1" >>"$TTY"
  read -r reply <&4 || true
  case "$reply" in y|Y|yes|YES|Yes) return 0;; *) return 1;; esac
}

# version_ge A B: A >= B for dotted numeric versions of any length.
version_ge() {
  local IFS=. i n
  local -a a b
  read -r -a a <<<"$1"; read -r -a b <<<"$2"
  n=${#a[@]}; [ "${#b[@]}" -gt "$n" ] && n=${#b[@]}
  for ((i = 0; i < n; i++)); do
    if ((10#${a[i]:-0} > 10#${b[i]:-0})); then return 0; fi
    if ((10#${a[i]:-0} < 10#${b[i]:-0})); then return 1; fi
  done
  return 0
}

# json_records JSON KEY VALUE: one line per object whose string field KEY is VALUE.
# ponytail: splits the JSON on '}' (no jq); fields after a nested object in a record would be lost. The list outputs'
# objects are flat today; switch to a real parser if that changes.
json_records() {
  local flat
  flat=$(tr -d '\n' <<<"$1" | tr '}' '\n')
  grep -E "\"$2\"[[:space:]]*:[[:space:]]*\"$3\"" <<<"$flat" || true
}
# json_field RECORDS KEY: KEY's string value in the first record that has it.
json_field() {
  sed -n -E "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" <<<"$1" | sed -n 1p
}
# The installPath of the user-scope record (the scope this script installs at), else of the first record.
plugin_install_path() {
  local recs user p
  recs=$(json_records "$PLUGINS_JSON" id "$PLUGIN")
  user=$(grep -E '"scope"[[:space:]]*:[[:space:]]*"user"' <<<"$recs" || true)
  p=$(json_field "$user" installPath)
  [ -n "$p" ] || p=$(json_field "$recs" installPath)
  printf '%s\n' "$p"
}
read_plugins() {
  local rc=0
  PLUGINS_JSON=$(claude plugin list --json </dev/null) || rc=$?
  [ "$rc" -eq 0 ] || fail_step "$rc" claude plugin list --json
}
# norm_source S: a directory as its physical path; a GitHub repo in any recorded form as owner/repo, lower case.
norm_source() {
  if [ -d "$1" ]; then (cd "$1" && pwd -P); return; fi
  local s
  s=$(tr '[:upper:]' '[:lower:]' <<<"$1")
  s=${s#https://github.com/}; s=${s#http://github.com/}; s=${s#ssh://git@github.com/}; s=${s#git@github.com:}
  s=${s%/}; s=${s%.git}
  printf '%s\n' "$s"
}
# Highest-version directory under the plugin cache that is named as a version; empty when none.
cache_root() {
  local cache="$CFG/plugins/cache/wrangl/wrangl" best="" d v
  for d in "$cache"/*/; do
    [ -d "$d" ] || continue
    v=$(basename "$d")
    grep -Eq '^[0-9]+(\.[0-9]+)*$' <<<"$v" || continue
    if [ -z "$best" ] || ! version_ge "$best" "$v"; then best=$v; fi
  done
  [ -z "$best" ] || printf '%s\n' "$cache/$best"
}

main() {
  local yes=0 plan=0 skip_build=0 source=$OFFICIAL source_given=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --yes|-y) yes=1;;
      --plan) plan=1;;
      --source) [ $# -ge 2 ] || { usage >&2; exit 2; }; source=$2; source_given=1; shift;;
      --source=*) source=${1#--source=}; source_given=1;;
      --skip-build) skip_build=1;;
      --help|-h) usage; exit 0;;
      *) printf 'error: unknown option: %s\n' "$1" >&2; usage >&2; exit 2;;
    esac
    shift
  done

  # ---- preconditions
  case "$(uname -s)" in
    Darwin|Linux) ;;   # WSL reports Linux
    *) die "Wrangl installs on macOS or Linux only (on Windows, run this inside WSL). This system is $(uname -s).";;
  esac
  command -v claude >/dev/null 2>&1 || die "the \`claude\` command (Claude Code) is not on PATH. Install Claude Code first (https://docs.claude.com/en/docs/claude-code/setup), open a new terminal, and run this again."
  local cv
  cv=$(claude --version </dev/null 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+' | head -n1 || true)
  if [ -z "$cv" ]; then
    say "warning: could not read the Claude Code version from \`claude --version\`; Wrangl needs $MIN_CLAUDE or later. Continuing."
  elif ! version_ge "$cv" "$MIN_CLAUDE"; then
    die "Claude Code $cv is too old; Wrangl needs $MIN_CLAUDE or later. Update Claude Code (\`claude update\`) and run this again."
  fi

  # ---- what is there already (read-only)
  local markets mrec mkind="" mloc="" have_market=0 have_plugin=0
  markets=$(claude plugin marketplace list --json </dev/null) || die "\`claude plugin marketplace list --json\` failed; nothing was changed"
  mrec=$(json_records "$markets" name "$MARKET" | sed -n 1p)
  if [ -n "$mrec" ]; then
    have_market=1
    mkind=$(json_field "$mrec" source)
    mloc=$(json_field "$mrec" repo); [ -n "$mloc" ] || mloc=$(json_field "$mrec" url); [ -n "$mloc" ] || mloc=$(json_field "$mrec" path)
    if [ "$source_given" = 1 ] && [ "$(norm_source "$source")" != "$(norm_source "$mloc")" ]; then
      die "a marketplace named '$MARKET' is already configured from ${mkind:-?} ${mloc:-?}, not from --source $source. Nothing was changed. Remove it first (claude plugin marketplace remove $MARKET) and run this again, or drop --source to update the configured one."
    fi
  fi
  read_plugins
  [ -n "$(json_records "$PLUGINS_JSON" id "$PLUGIN")" ] && have_plugin=1

  local orec
  orec=$(json_records "$PLUGINS_JSON" id "$OLD_PLUGIN")
  if grep -Eq '"enabled"[[:space:]]*:[[:space:]]*true' <<<"$orec"; then
    say "WARNING: the older plugin $OLD_PLUGIN (ah v1) is enabled. Running it and Wrangl together runs every hook twice."
    say "         Disable it with:  claude plugin disable $OLD_PLUGIN   (or /plugin disable $OLD_PLUGIN inside Claude Code)"
    say "         This script does not disable it for you."
    say ""
  fi

  # ---- the plan
  local -a market_cmd plugin_cmd apply_cmd
  if [ "$have_market" = 1 ]; then
    market_cmd=(claude plugin marketplace update "$MARKET")
  else
    market_cmd=(claude plugin marketplace add "$source")
  fi
  if [ "$have_plugin" = 1 ]; then
    plugin_cmd=(claude plugin update "$PLUGIN")
  else
    plugin_cmd=(claude plugin install "$PLUGIN" --scope user)
  fi
  apply_cmd=(--apply)
  [ "$yes" = 1 ] && apply_cmd+=(--auto-rebuild)

  say "Wrangl install plan:"
  if [ "$have_market" = 1 ]; then
    say "  1. ${market_cmd[*]}"
    say "       refreshes the already-added marketplace '$MARKET' from its configured source: ${mkind:-?} ${mloc:-?}"
    [ "$(norm_source "$mloc")" = "$OFFICIAL" ] \
      || say "       WARNING: that source is not $OFFICIAL; the plugin will come from ${mloc:-an unknown source}"
  else
    say "  1. ${market_cmd[*]}"
    say "       adds the marketplace '$MARKET' (from $source) to your Claude Code user settings"
  fi
  if [ "$have_plugin" = 1 ]; then
    say "  2. ${plugin_cmd[*]}"
    say "       updates the installed plugin $PLUGIN to the marketplace's latest version"
  else
    say "  2. ${plugin_cmd[*]}"
    say "       installs the plugin $PLUGIN into Claude Code at user scope"
  fi
  if [ "$skip_build" = 1 ]; then
    say "  (--skip-build: the plugin's installer is not run)"
  else
    say "  3. bash <plugin root>/scripts/install.sh"
    say "       the plugin installer's own plan; changes nothing"
    say "  4. bash <plugin root>/scripts/install.sh ${apply_cmd[*]}"
    say "       builds ahd under ~/.claude/ah2/bin and links it onto PATH"
    say "       may install Anthropic's frontend-design skill (Apache-2.0) into $CFG/skills/frontend-design from a pinned commit, verifying each file's sha256 first"
    say "       starts the Wrangl daemon, or restarts it when the running one is older or the installed binary changed"
    [ "$yes" = 1 ] || say "       (it asks its own questions at the terminal)"
  fi
  say ""

  if [ "$plan" = 1 ]; then say "--plan: nothing was changed."; exit 0; fi
  if [ "$yes" != 1 ]; then
    if ! have_tty; then
      say "No terminal to ask on, so nothing was changed. Re-run with --yes to install:"
      say "  curl -fsSL https://raw.githubusercontent.com/agentic-tool-labs/wrangl/main/install.sh | bash -s -- --yes"
      say "An unattended run needs a .NET 10 SDK already installed (the plugin installer fetches one only at a terminal)"
      say "and a C toolchain (the Xcode command line tools on macOS; clang and the zlib headers on Linux)."
      exit 0
    fi
    ask "Proceed?" || { say "Nothing was changed."; exit 0; }
  fi

  # ---- do it
  run "${market_cmd[@]}"
  run "${plugin_cmd[@]}"

  if [ "$skip_build" = 1 ]; then
    say ""
    say "The plugin is installed; nothing was built (--skip-build). Next:"
    say "  1. Build it: run /wrangl:hierarchy install inside Claude Code, or run this script again without --skip-build."
    say "  2. Open Claude Code in a repository, run /wrangl:hierarchy init, then restart Claude Code or run /clear."
    say "Docs:       https://github.com/agentic-tool-labs/wrangl"
    say "Wrangl is in early testing: use it on work you have committed or backed up."
    exit 0
  fi

  local root=""
  read_plugins
  root=$(plugin_install_path)
  if [ -z "$root" ] || [ ! -d "$root" ]; then root=$(cache_root); fi
  if [ -z "$root" ] || [ ! -f "$root/scripts/install.sh" ]; then
    printf "error: cannot find the installed plugin: \`claude plugin list --json\` gives no installPath for %s and %s has no version-named directory with scripts/install.sh.\n" \
      "$PLUGIN" "$CFG/plugins/cache/wrangl/wrangl" >&2
    printf 'Already done:\n' >&2; printf '  %s\n' "${DONE[@]}" >&2
    exit 1
  fi
  say "Plugin root: $root"

  run bash "$root/scripts/install.sh"
  if [ "$yes" = 1 ]; then
    run bash "$root/scripts/install.sh" "${apply_cmd[@]}"
  else
    ask "Run the plugin installer with --apply?" || {
      say "Stopped before the build. The plugin is installed; to build later run: bash \"$root/scripts/install.sh\" --apply"
      exit 0
    }
    say "+ bash $root/scripts/install.sh ${apply_cmd[*]}"
    local rc=0
    bash "$root/scripts/install.sh" "${apply_cmd[@]}" <"$TTY" || rc=$?
    [ "$rc" -eq 0 ] || fail_step "$rc" bash "$root/scripts/install.sh" "${apply_cmd[@]}"
    DONE+=("bash $root/scripts/install.sh ${apply_cmd[*]}")
  fi

  say ""
  say "Wrangl is installed. Next:"
  say "  1. Open Claude Code in a repository and run:  /wrangl:hierarchy init"
  say "  2. Then restart Claude Code or run /clear, so the Orchestrator's instructions load."
  say "Check it:   ah --version   and   ahd where"
  say "Docs:       $root/docs/getting-started.md   (also https://github.com/agentic-tool-labs/wrangl)"
  say "Wrangl is in early testing: use it on work you have committed or backed up."
}

main "$@"
