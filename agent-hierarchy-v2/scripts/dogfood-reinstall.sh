#!/bin/bash
# agent-hierarchy — one command to a new dogfood build: refresh the marketplace, replace the installed plugin, build and place its binaries.
#
# Usage: scripts/dogfood-reinstall.sh [--dry-run]
#
#   --dry-run   print the steps and run none of them
#
# Steps, stopping at the first one that fails:
#   1. claude plugin marketplace update <marketplace>
#   2. claude plugin uninstall wrangl@<marketplace>
#   3. claude plugin install wrangl@<marketplace>
#   4. bash <the new install path>/scripts/install.sh --apply --auto-rebuild
#   5. print what to reload
# The marketplace is the one in the installed plugin record (~/.claude/plugins/installed_plugins.json). Nothing is pushed or tagged;
# which build the marketplace serves is decided where it is published.
# Exit codes: 0 done (or planned); 1 a step failed; 2 usage error or no installed plugin to read the marketplace from.

set -u

plugin=wrangl
claude_bin="${CLAUDE_BIN:-claude}"
installed_file="$HOME/.claude/plugins/installed_plugins.json"
dry=false
[ $# -le 1 ] || { echo "usage: scripts/dogfood-reinstall.sh [--dry-run]" >&2; exit 2; }
case "${1:-}" in
    "") ;;
    --dry-run) dry=true ;;
    *) echo "usage: scripts/dogfood-reinstall.sh [--dry-run]" >&2; exit 2 ;;
esac

keys="$(grep -o "\"$plugin@[^\"]*\"" "$installed_file" 2>/dev/null | tr -d '"' | sort -u)"
[ "$(printf '%s' "$keys" | grep -c .)" -le 1 ] || { echo "dogfood-reinstall: $installed_file names more than one $plugin install ($(echo $keys)); remove the extra with: claude plugin uninstall <key>" >&2; exit 2; }
key="$keys"
[ -n "$key" ] || { echo "dogfood-reinstall: $installed_file names no installed $plugin; install it once with: claude plugin install $plugin@<marketplace>" >&2; exit 2; }
market="${key#*@}"

step() {
    local n="$1"; shift
    echo "[$n/5] $*"
    $dry && return 0
    "$@" || {
        echo "dogfood-reinstall: step $n failed: $*" >&2
        [ "$n" = 3 ] && echo "dogfood-reinstall: $plugin is uninstalled; put it back with: $claude_bin plugin install $key" >&2
        exit 1
    }
}

step 1 "$claude_bin" plugin marketplace update "$market"
step 2 "$claude_bin" plugin uninstall "$key"
step 3 "$claude_bin" plugin install "$key"

if $dry; then
    echo "[4/5] bash <the new install path of $key>/scripts/install.sh --apply --auto-rebuild"
else
    install_path="$(awk -v k="\"$key\"" '$0 ~ k {f=1} f && /"installPath"/ {gsub(/.*"installPath": *"|".*/, ""); print; exit}' "$installed_file")"
    [ -n "$install_path" ] && [ -f "$install_path/scripts/install.sh" ] || { echo "dogfood-reinstall: step 4 failed: $installed_file has no install path with scripts/install.sh for $key" >&2; exit 1; }
    step 4 bash "$install_path/scripts/install.sh" --apply --auto-rebuild
fi

echo "[5/5] done. Run /reload-plugins in this session and in every live member session."
