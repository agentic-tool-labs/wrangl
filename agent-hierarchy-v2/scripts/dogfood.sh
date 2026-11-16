#!/bin/bash
# agent-hierarchy — reinstalls a pushed build of this plugin as the user's installed plugin, without touching the repo's working tree.
#
# Usage: scripts/dogfood.sh [--plan] [--migrate] [--repo DIR] <sha>
#        scripts/dogfood.sh --swap
#
#   <sha>         the commit to install (any ref git resolves in the repo)
#   --plan        print every step and run nothing that writes
#   --migrate     the one-time move from the marketplace the plugin is installed from today to the dogfood marketplace:
#                 snapshot the plugin's options, uninstall, install from the dogfood marketplace, restore the options and
#                 check their count. Run it once, from your own terminal (it edits your Claude Code settings, which an agent
#                 session may not); without it the script refuses until the move is done. A run that stopped part way is
#                 resumed by running the same command again.
#   --repo DIR    the repository holding <sha> (default: the one the current directory is in)
#   --swap        restart the daemon on the installed binary, but only at a quiet point (no live member working or blocked
#                 in `ah roster status`); never forced. Then read the daemon's rejection log for 60 s and report the
#                 bad_request rows. Exit 3 when no quiet point came within 30 minutes (AH_DOGFOOD_WAIT_SECONDS).
#
# Each build is the plugin version "<release version>.<commit count>", which sorts above every earlier build on the line and
# below the next release; the commit is recorded in <AH_HOME>/dogfood/builds.jsonl. Builds follow one line of history: a commit
# that is the last build or an ancestor of it is "unchanged"; one that does not descend from it is refused. A build whose
# native contract differs from the installed one is not installed.
#
# Environment: AH_HOME (default ~/.claude/ah2), CLAUDE_CONFIG_DIR (default ~/.claude), CLAUDE_BIN (default `claude`),
# AH_DOGFOOD_WAIT_SECONDS (1800), AH_DOGFOOD_POLL_SECONDS (15), AH_DOGFOOD_VERIFY_SECONDS (60).
# Exit codes: 0 done, planned, or nothing to do; 1 a step failed or the build was refused; 2 a usage error; 3 the swap is deferred.

set -u

plan=false
migrate=false
swap=false
repo=""
sha_arg=""
while [ $# -gt 0 ]; do
    case "$1" in
        --plan) plan=true; shift ;;
        --migrate) migrate=true; shift ;;
        --swap) swap=true; shift ;;
        --repo)
            if [ $# -lt 2 ] || [ -z "$2" ]; then echo "error: --repo requires a value" >&2; exit 2; fi
            repo="$2"; shift 2 ;;
        -h|--help) sed -n '2,28p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*) echo "error: unrecognized argument: $1" >&2; exit 2 ;;
        *)
            if [ -n "$sha_arg" ]; then echo "error: one commit only" >&2; exit 2; fi
            sha_arg="$1"; shift ;;
    esac
done
if $swap; then
    if $migrate || [ -n "$sha_arg" ]; then echo "error: --swap takes no commit and no --migrate" >&2; exit 2; fi
else
    [ -n "$sha_arg" ] || { echo "error: a commit is required" >&2; exit 2; }
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib-version.sh
. "$script_dir/lib-version.sh" || { echo "error: cannot load $script_dir/lib-version.sh" >&2; exit 2; }
# shellcheck source=lib-progress.sh
. "$script_dir/lib-progress.sh" || { echo "error: cannot load $script_dir/lib-progress.sh" >&2; exit 2; }

ah_home="${AH_HOME:-$HOME/.claude/ah2}"
config_dir="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
claude_bin="${CLAUDE_BIN:-claude}"
dog="$ah_home/dogfood"
market="$dog/marketplace"
market_name=wrangl-dogfood
plugin_name=wrangl
old_plugin=ah2
old_market="${AH_DOGFOOD_OLD_MARKET:-old-marketplace}"
plugin_subdir=agent-hierarchy-v2
stamp="$ah_home/install.json"
settings_file="$config_dir/settings.json"
installed_file="$config_dir/plugins/installed_plugins.json"
known_file="$config_dir/plugins/known_marketplaces.json"
# The source the lifecycle permission rules are built from: the rule list and the doc that defines it. A difference between two
# commits in these paths means the rules to grant may have changed.
PERMISSION_RULE_PATHS="$plugin_subdir/native/src/Ah.Commands/Roster/Read/PermissionsVerb.cs $plugin_subdir/docs/lifecycle-permissions.md"

step() { printf '\n== %s\n' "$1"; }
say() { printf '%s\n' "$*"; }
would() { printf 'would: %s\n' "$*"; }
rollback_export=false
fail_hint=""
rollback() {
    $rollback_export || return 0
    rollback_export=false
    rm -rf "$market/$plugin_name"
    if [ -d "$market/$plugin_name.prev" ]; then mv "$market/$plugin_name.prev" "$market/$plugin_name"; fi
    if [ -f "$market/.claude-plugin/marketplace.json.prev" ]; then mv "$market/.claude-plugin/marketplace.json.prev" "$market/.claude-plugin/marketplace.json"; else rm -f "$market/.claude-plugin/marketplace.json"; fi
    printf 'dogfood: rolled the export back to the previous build\n' >&2
}
die() {
    printf 'dogfood: %s\n' "$*" >&2
    rollback
    [ -z "$fail_hint" ] || printf 'dogfood: %s\n' "$fail_hint" >&2
    exit 1
}

json_get() { # json_get <file> <js expression over d> [argv[3] for the expression]
    node -e 'const d = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); const v = eval(process.argv[2]); if (v !== undefined && v !== null) process.stdout.write(String(v))' "$1" "$2" "${3:-}"
}

# --- --swap: the daemon, at a quiet point -------------------------------------------------------------------------------
if $swap; then
    ahd="$ah_home/bin/ahd"
    [ -x "$ahd" ] || die "no daemon binary at $ahd"
    mkdir -p "$dog" || die "cannot create $dog"
    installed=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$stamp" 2>/dev/null | head -1)
    [ -n "$installed" ] || die "no installed build is stamped in $stamp"
    if command -v ah >/dev/null 2>&1; then ah_cli=$(command -v ah); else ah_cli="$script_dir/../bin/ah"; fi
    wait_s="${AH_DOGFOOD_WAIT_SECONDS:-1800}"
    poll_s="${AH_DOGFOOD_POLL_SECONDS:-15}"
    verify_s="${AH_DOGFOOD_VERIFY_SECONDS:-60}"
    for pair in "AH_DOGFOOD_WAIT_SECONDS=$wait_s" "AH_DOGFOOD_POLL_SECONDS=$poll_s" "AH_DOGFOOD_VERIFY_SECONDS=$verify_s"; do
        case "${pair#*=}" in ''|*[!0-9]*) echo "error: ${pair%%=*} must be a whole number of seconds, not '${pair#*=}'" >&2; exit 2 ;; esac
    done

    # busy_members prints the live members that are working or blocked, or "?" when the status cannot be read.
    # TODO: also require the daemon's codex_pending count to be 0 once the Codex question relay (ah/codex-question-relay) merges and
    # `ahd msg daemon status --json` reports it; until then a pending Codex request shows as a blocked member.
    busy_members() {
        "$ah_cli" roster status --cwd "$PWD" 2>/dev/null | node -e '
            let t = ""; process.stdin.on("data", (c) => (t += c)).on("end", () => {
                try {
                    const d = JSON.parse(t); const busy = [];
                    for (const team of d.teams || []) for (const m of team.members || [])
                        if (m.live !== false && (m.activity === "working" || m.activity === "blocked")) busy.push(m.name + ":" + m.activity);
                    process.stdout.write(busy.join(" "));
                } catch { process.stdout.write("?"); }
            })'
    }

    step "Quiet point"
    if $plan; then
        would "wait up to ${wait_s}s for no live member working or blocked in \`ah roster status\`, then $ahd restart"
        would "wait ${verify_s}s, then read $ah_home/logs/rejected.jsonl and rejected.jsonl.1 for bad_request rows since the restart"
        exit 0
    fi
    waited=0
    while :; do
        busy=$(busy_members)
        [ -z "$busy" ] && break
        if [ "$waited" -ge "$wait_s" ]; then
            if [ "$busy" = "?" ]; then why="the member status could not be read"; else why="still working or blocked: $busy"; fi
            say "dogfood daemon: deferred: $why; the next client on a newer version drains the old daemon first; otherwise swap at the next quiet point with scripts/dogfood.sh --swap"
            exit 3
        fi
        sleep "$poll_s"
        waited=$((waited + poll_s))
        [ "$poll_s" -gt 0 ] || waited=$((waited + 1))
    done
    say "quiet: no live member is working or blocked"

    step "Swap"
    swap_time=$(node -e 'process.stdout.write(new Date().toISOString())')
    # A restart can report failure only because the old daemon exited slowly: then keep watching, start the new one if nothing has, and let the version decide.
    restart_failed=false
    "$ahd" restart > "$dog/restart.log" 2>&1 || restart_failed=true
    running=""
    tries=0
    max_tries=30
    if $restart_failed; then max_tries=180; fi
    while [ "$tries" -lt "$max_tries" ]; do
        status_json=$("$ahd" status 2>/dev/null || true)
        case "$status_json" in
            *'"up":true'*)
                running=$(printf '%s' "$status_json" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' | head -1)
                if ! $restart_failed || [ "$running" = "$installed" ]; then break; fi
                ;;
            *)
                if $restart_failed; then "$ahd" start >> "$dog/restart.log" 2>&1 || true; fi
                ;;
        esac
        tries=$((tries + 1)); sleep 1
    done
    if [ "$running" != "$installed" ]; then
        if $restart_failed; then die "$ahd restart failed and the daemon reports version ${running:-none (not up)}, not $installed (log: $dog/restart.log)"; fi
        die "after the restart the daemon reports version ${running:-none (not up)}, not $installed"
    fi
    if $restart_failed; then say "the restart reported a failure, but the daemon came up on the installed version (log: $dog/restart.log)"; fi
    say "daemon restarted: version $running"

    step "Rejections since the swap"
    sleep "$verify_s"
    rejected=$(node -e '
        const fs = require("fs"); const base = process.argv[1]; const since = process.argv[2]; const rows = []; let probes = 0;
        for (const f of [base + ".1", base]) {
            if (!fs.existsSync(f)) continue;
            for (const line of fs.readFileSync(f, "utf8").split("\n")) {
                if (!line.trim()) continue;
                try {
                    const r = JSON.parse(line);
                    if (r.code !== "bad_request" || typeof r.ts !== "string" || r.ts < since) continue;
                    // ponytail: the route probe `ah msg due` sends is answered 400 by design and older daemons log it; delete this
                    // skip once every daemon runs the build that stops logging it.
                    if (r.method === "GET" && r.path === "/v1/due" && r.message === "session must be a non-empty string") probes++;
                    else rows.push(r);
                } catch { /* a torn line is skipped */ }
            }
        }
        for (const r of rows) console.log("bad_request client_version=" + r.client_version + " caller=" + r.caller + " path=" + r.path + " field=" + r.field);
        console.log("probe=" + probes);
        ' "$ah_home/logs/rejected.jsonl" "$swap_time")
    probes=$(printf '%s\n' "$rejected" | sed -n 's/^probe=//p' | tail -n 1)
    rejected=$(printf '%s\n' "$rejected" | grep -v '^probe=' || true)
    errors=0
    [ -z "$rejected" ] || errors=$(printf '%s\n' "$rejected" | wc -l | tr -d ' ')
    [ -z "$rejected" ] || printf '%s\n' "$rejected"
    say "dogfood daemon: swapped to $running, post-swap errors $errors, probe ${probes:-0}"
    [ "$errors" -eq 0 ] || say "open or update a tracked issue naming these client versions; a bad_request other than the route probe is a client the new daemon refused"
    exit 0
fi

if [ -z "$repo" ]; then
    repo=$(git -C "$PWD" rev-parse --show-toplevel 2>/dev/null) || die "the current directory is not in a git repository; give --repo"
fi

if $migrate && ! $plan && [ -n "${CLAUDECODE:-}" ]; then
    die "--migrate edits your Claude Code settings, which an agent session may not; run \`scripts/dogfood.sh --migrate $sha_arg\` from your terminal"
fi

# --- 0. Resolve the commit and follow one line of history ----------------------------------------------------------------
step "Resolve"
sha=$(git -C "$repo" rev-parse --verify "$sha_arg^{commit}" 2>/dev/null) || die "$sha_arg is not a commit in $repo"
sha7=$(printf '%s' "$sha" | cut -c1-7)

builds="$dog/builds.jsonl"
last_sha=""
last_version=""
if [ -s "$builds" ]; then
    last_line=$(tail -n 1 "$builds")
    last_sha=$(printf '%s' "$last_line" | sed -n 's/.*"sha":"\([^"]*\)".*/\1/p')
    last_version=$(printf '%s' "$last_line" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p')
fi
is_ancestor() { # is_ancestor <a> <b>: 0 yes, 1 no; anything else stops the run
    git -C "$repo" merge-base --is-ancestor "$1" "$2" 2>/dev/null
    local rc=$?
    case $rc in 0|1) return $rc ;; *) die "git merge-base --is-ancestor $1 $2 failed (exit $rc)" ;; esac
}
if [ -n "$last_sha" ]; then
    if [ "$sha" = "$last_sha" ]; then
        say "unchanged: $sha7 is the last build"
        exit 0
    fi
    if git -C "$repo" cat-file -e "$last_sha^{commit}" 2>/dev/null; then
        if is_ancestor "$sha" "$last_sha"; then
            say "unchanged (older than installed): $sha7 is an ancestor of the last build $(printf '%s' "$last_sha" | cut -c1-7)"
            exit 0
        fi
        if ! is_ancestor "$last_sha" "$sha"; then
            die "refused: $sha is not a descendant of the last build $last_sha; builds follow one line of history. Nothing was changed. Choosing a new line is for the user to decide (see reference/dogfood.md)."
        fi
    else
        die "refused: the last build $last_sha is not in $repo, so $sha cannot be shown to descend from it. Nothing was changed. Choosing a new line is for the user to decide (see reference/dogfood.md)."
    fi
fi

count=$(git -C "$repo" rev-list --count "$sha") || die "cannot count the commits up to $sha"
plugin_json_at_sha=$(git -C "$repo" show "$sha:$plugin_subdir/.claude-plugin/plugin.json" 2>/dev/null) || die "$sha has no $plugin_subdir/.claude-plugin/plugin.json"
base=$(printf '%s\n' "$plugin_json_at_sha" | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
[ -n "$base" ] || die "cannot read the plugin version at $sha"
version="$base.$count"
case "$version" in *[!0-9A-Za-z.+-]*) die "the version $version has a character outside [0-9A-Za-z.+-]" ;; esac
if [ -n "$last_version" ] && [ "$version" = "$last_version" ]; then
    die "refused: $sha would be version $version, the version of the last build $last_sha; a wrong commit count must never read as unchanged. Nothing was changed."
fi
new_contract=$(git -C "$repo" show "$sha:$plugin_subdir/native/cutover.json" 2>/dev/null | sed -n 's/.*"contract"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' | head -1)
[ -n "$new_contract" ] || die "cannot read the native contract at $sha ($plugin_subdir/native/cutover.json)"
old_version=""
old_contract=""
if [ -f "$stamp" ]; then
    old_version=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$stamp" | head -1)
    old_contract=$(sed -n 's/.*"contract"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$stamp" | head -1)
fi
say "commit $sha7, version $version (release $base, commit count $count), contract $new_contract"
say "installed: ${old_version:-none}, contract ${old_contract:-none}"

if [ -n "$old_version" ] && ! $migrate && [ "$(ah_version_cmp "$version" "$old_version")" != 1 ]; then
    say "unchanged: $version is not above the installed $old_version"
    exit 0
fi

# --- 1. The contract gate -----------------------------------------------------------------------------------------------
step "Contract gate"
if [ -z "$old_contract" ]; then
    die "no installed build is stamped in $stamp; run the plugin's install.sh --apply first"
fi
if [ "$old_contract" != "$new_contract" ]; then
    say "contract $old_contract -> $new_contract: the reinstall needs every session restarted; not done automatically."
    say "Run the manual path from a terminal when sessions can restart: scripts/dogfood.sh with the daemon restart, then restart the sessions."
    exit 1
fi
say "contract $new_contract matches the installed build"

# --- helpers that write -------------------------------------------------------------------------------------------------
logs="$dog/logs"
n_calls=0
run_claude() {
    n_calls=$((n_calls + 1))
    local log
    log="$logs/$(date -u +%Y%m%dT%H%M%SZ)-$n_calls.log"
    say "claude $*"
    run_progress "claude $*" "$log" "$claude_bin" "$@"
    local rc=$?
    [ $rc -eq 0 ] || die "claude $* exited $rc (log: $log)"
}

# --- 2. Export ----------------------------------------------------------------------------------------------------------
step "Export"
if $plan; then
    would "git -C $repo archive $sha $plugin_subdir, extracted to a temporary directory, then renamed to $market/$plugin_name.next"
    would "set .claude-plugin/plugin.json version to $version in the export only; the repo working tree is not touched"
else
    mkdir -p "$market/.claude-plugin" "$logs" || die "cannot create $market"
    tmp=$(mktemp -d "$dog/export.XXXXXX") || die "cannot create a temporary directory under $dog"
    [ -n "$tmp" ] || die "mktemp gave no directory"
    git -C "$repo" archive "$sha" "$plugin_subdir" | tar -x -C "$tmp" || { rm -rf "$tmp"; die "git archive of $sha failed"; }
    [ -d "$tmp/$plugin_subdir" ] || { rm -rf "$tmp"; die "the archive holds no $plugin_subdir"; }
    node -e '
        const fs = require("fs"); const p = process.argv[1] + "/.claude-plugin/plugin.json";
        const d = JSON.parse(fs.readFileSync(p, "utf8")); d.version = process.argv[2];
        fs.writeFileSync(p, JSON.stringify(d, null, 2) + "\n")' "$tmp/$plugin_subdir" "$version" || { rm -rf "$tmp"; die "cannot stamp the exported plugin.json"; }
    rm -rf "$market/$plugin_name.next"
    mv "$tmp/$plugin_subdir" "$market/$plugin_name.next" || { rm -rf "$tmp"; die "cannot place the export"; }
    rm -rf "$tmp"
    say "exported $sha7 as $version"
fi

# --- 3. Swap the export dir and write the marketplace -------------------------------------------------------------------
# The publish below reads these, so they change first; a publish that fails puts the previous export back.
step "Marketplace"
if $plan; then
    would "rename $market/$plugin_name to $plugin_name.prev and $plugin_name.next to $plugin_name"
    would "write $market/.claude-plugin/marketplace.json naming $plugin_name $version"
else
    rm -rf "$market/$plugin_name.prev"
    rm -f "$market/.claude-plugin/marketplace.json.prev"
    if [ -f "$market/.claude-plugin/marketplace.json" ]; then cp "$market/.claude-plugin/marketplace.json" "$market/.claude-plugin/marketplace.json.prev" || die "cannot keep the previous marketplace file"; fi
    if [ -d "$market/$plugin_name" ]; then mv "$market/$plugin_name" "$market/$plugin_name.prev" || die "cannot keep the previous export"; fi
    rollback_export=true
    mv "$market/$plugin_name.next" "$market/$plugin_name" || die "cannot swap in the new export"
    printf '{"name":"%s","owner":{"name":"%s"},"plugins":[{"name":"%s","source":"./%s","version":"%s"}]}\n' \
        "$market_name" "${USER:-user}" "$plugin_name" "$plugin_name" "$version" > "$market/.claude-plugin/marketplace.json" \
        || die "cannot write the marketplace file"
fi

# --- 4. Publish ---------------------------------------------------------------------------------------------------------
step "Publish"
key="$plugin_name@$market_name"
if $migrate; then
    if $plan; then
        would "snapshot the options of $old_plugin@$old_market from $settings_file and count them (a run resumed after the uninstall reuses the earlier snapshot)"
        would "$claude_bin plugin marketplace add $market   (skipped when $market_name is already known)"
        would "$claude_bin plugin uninstall $old_plugin@$old_market   (skipped when it is not installed)"
        would "$claude_bin plugin install $key"
        would "restore the options under $key in $settings_file and verify the count matches"
    else
        old_installed=false
        grep -q "\"$old_plugin@$old_market\"" "$installed_file" 2>/dev/null && old_installed=true
        options_snapshot=""
        if ! $old_installed; then
            options_snapshot=$(ls -t "$dog"/userconfig.*.json 2>/dev/null | head -n 1)
        fi
        if [ -z "$options_snapshot" ]; then
            options_snapshot="$dog/userconfig.$(date -u +%Y%m%dT%H%M%SZ).json"
            node -e '
                const fs = require("fs"); const s = fs.existsSync(process.argv[1]) ? JSON.parse(fs.readFileSync(process.argv[1], "utf8")) : {};
                const o = (((s.pluginConfigs || {})[process.argv[2]] || {}).options) || {};
                fs.writeFileSync(process.argv[3], JSON.stringify(o, null, 2) + "\n")' \
                "$settings_file" "$old_plugin@$old_market" "$options_snapshot" || die "cannot snapshot the plugin options"
        else
            say "resuming: reusing the earlier snapshot $options_snapshot"
        fi
        before=$(node -e 'process.stdout.write(String(Object.keys(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))).length))' "$options_snapshot") || die "cannot read the snapshot $options_snapshot"
        say "snapshot: $before option(s) in $options_snapshot"
        if grep -q "\"$market_name\"" "$known_file" 2>/dev/null; then say "the $market_name marketplace is already added"; else run_claude plugin marketplace add "$market"; fi
        if $old_installed; then
            run_claude plugin uninstall "$old_plugin@$old_market"
        else
            say "$old_plugin@$old_market is not installed; resuming after the uninstall"
        fi
        fail_hint="$old_plugin@$old_market is uninstalled; its options are in $options_snapshot. Run this command again to resume, or go back with: claude plugin install $old_plugin@$old_market"
        if grep -q "\"$key\"" "$installed_file" 2>/dev/null; then say "$key is already installed"; else run_claude plugin install "$key"; fi
        node -e '
            const fs = require("fs"); const f = process.argv[1]; const key = process.argv[2];
            const o = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));
            if (Object.keys(o).length === 0) process.exit(0);
            const s = fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, "utf8")) : {};
            s.pluginConfigs = s.pluginConfigs || {}; s.pluginConfigs[key] = Object.assign({}, s.pluginConfigs[key], { options: Object.assign({}, (s.pluginConfigs[key] || {}).options, o) });
            fs.writeFileSync(f + ".tmp", JSON.stringify(s, null, 2) + "\n"); fs.renameSync(f + ".tmp", f)' \
            "$settings_file" "$key" "$options_snapshot" || die "cannot restore the plugin options"
        after=$(json_get "$settings_file" "Object.keys(((d.pluginConfigs||{})['$key']||{}).options||{}).length")
        [ "${after:-0}" = "$before" ] || die "the options were not restored: $before in the snapshot, ${after:-0} now"
        say "restored $after option(s)"
        fail_hint=""
    fi
else
    if $plan; then
        would "$claude_bin plugin marketplace update $market_name"
        would "$claude_bin plugin update $key"
    else
        grep -q "\"$market_name\"" "$known_file" 2>/dev/null || die "the $market_name marketplace is not set up; run scripts/dogfood.sh --migrate $sha7 once, from your terminal"
        run_claude plugin marketplace update "$market_name"
        run_claude plugin update "$key"
    fi
fi
rollback_export=false

# --- 5. Build and place -------------------------------------------------------------------------------------------------
step "Build and place"
if $plan; then
    would "read the install path of $key from $installed_file and require version $version"
    would "bash <install path>/scripts/install.sh --apply --rebuild-only --no-daemon-restart"
    would "append {version, sha, exported_at, contract} to $builds"
else
    install_path=$(json_get "$installed_file" "(d.plugins['$key']||[])[0] && d.plugins['$key'][0].installPath")
    got_version=$(json_get "$installed_file" "(d.plugins['$key']||[])[0] && d.plugins['$key'][0].version")
    [ -n "$install_path" ] || die "$installed_file has no install of $key"
    [ "$got_version" = "$version" ] || die "$key is installed at version ${got_version:-none}, not $version; the update did not take"
    [ -f "$install_path/scripts/install.sh" ] || die "$install_path has no scripts/install.sh"
    install_log="$logs/install-$sha7.log"
    run_progress "install.sh --rebuild-only (native build)" "$install_log" \
        bash "$install_path/scripts/install.sh" --apply --rebuild-only --no-daemon-restart || die "install.sh failed (log: $install_log)"
    say "placed the binary from $install_path"
fi

# --- 6. Permission rules: do the rules in force still name the command path agents are told to type? ---------------------
# The command path is $HOME/.local/bin/ah when that link leads to this build's bin/ah and the stamp names this build's directory;
# otherwise it is the plugin directory's own bin/ah, and every build then needs a new sync.
stable="$HOME/.local/bin/ah"
cmd_path="${install_path:-}/bin/ah"
perm_reason=""
physdir() { (cd -P "$1" 2>/dev/null && pwd -P); }
# The path a chain of links ends at, with the directories above it resolved, as the daemon's real-path reads it; fails past 16 hops.
resolve_path() {
    local p="$1" hops=0 t d
    while [ -L "$p" ]; do
        [ "$hops" -lt 16 ] || return 1
        t=$(readlink "$p") || return 1
        case "$t" in /*) p="$t" ;; *) p="$(dirname "$p")/$t" ;; esac
        hops=$((hops + 1))
    done
    d=$(physdir "$(dirname "$p")") || d=$(dirname "$p")
    printf '%s/%s' "$d" "$(basename "$p")"
}
if ! $plan; then
    stamped_root=$(json_get "$stamp" "d.plugin_root") || stamped_root=""
    link_real=""
    if [ -L "$stable" ]; then
        link_real=$(resolve_path "$stable") || link_real=""
    fi
    if [ ! -L "$stable" ]; then
        perm_reason="$stable is not a link to this build"
    elif [ "$link_real" != "$(physdir "$install_path")/bin/ah" ]; then
        perm_reason="$stable leads to ${link_real:-nothing resolvable}, not to $install_path/bin/ah"
    elif [ -z "$stamped_root" ] || [ "$(physdir "$stamped_root")" != "$(physdir "$install_path")" ]; then
        perm_reason="$stamp does not name $install_path as the plugin root"
    else
        cmd_path="$stable"
    fi
fi
migrate_synced=false
if $migrate && ! $plan; then
    # Rules granted for the marketplace the plugin came from still carry its path; the user's run of this move is the consent to rewrite them.
    if sync_out=$("$install_path/bin/ah" roster permissions sync --cwd "$PWD" 2>&1); then
        say "permissions sync: $sync_out"
        migrate_synced=true
    else
        say "permissions sync failed: $sync_out"
    fi
fi
if $migrate_synced; then
    permissions="no"
elif [ -z "$last_sha" ] || $migrate; then
    permissions="needed"
else
    # shellcheck disable=SC2086
    git -C "$repo" diff --quiet "$last_sha" "$sha" -- $PERMISSION_RULE_PATHS 2>/dev/null
    case $? in 0) permissions="no" ;; 1) permissions="needed" ;; *) permissions="needed" ;; esac
fi
# A rule the ledger recorded whose prefix is not the current command path no longer matches what agents are told to type.
ledger="$ah_home/permissions.json"
if ! $plan && [ "$permissions" = "no" ] && [ -f "$ledger" ]; then
    stale=$(json_get "$ledger" "(d.added||[]).filter(r => !['Bash(', 'Bash(\"'].some(h => String(r).startsWith(h + process.argv[3]))).length" "$cmd_path") || stale=1
    [ "${stale:-1}" = "0" ] || { permissions="needed"; perm_reason="${perm_reason:-a granted rule names a command path other than $cmd_path}"; }
fi
if ! $plan && [ "$permissions" = "no" ] && [ -n "$perm_reason" ]; then
    permissions="needed"
fi

# --- 7. Verify ----------------------------------------------------------------------------------------------------------
step "Verify"
if $plan; then
    would "require $stamp to name version $version and contract $new_contract"
    would "compare the permission rule source between the last build and $sha7 (git diff --quiet over: $PERMISSION_RULE_PATHS)"
    would "list the live peers that need /reload-plugins"
    exit 0
fi
stamped=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$stamp" | head -1)
stamped_contract=$(sed -n 's/.*"contract"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$stamp" | head -1)
[ "$stamped" = "$version" ] || die "$stamp names version ${stamped:-none}, not $version"
[ "$stamped_contract" = "$new_contract" ] || die "$stamp names contract ${stamped_contract:-none}, not $new_contract"
say "install.json: version $stamped, contract $stamped_contract"
printf '{"version":"%s","sha":"%s","exported_at":"%s","contract":%s}\n' "$version" "$sha" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$new_contract" >> "$builds" || die "cannot record the build in $builds"

# --- 8. Report ----------------------------------------------------------------------------------------------------------
step "Report"
say "dogfood ${old_version:-none} -> $version ($sha7): plugin $install_path, binary placed, daemon deferred only until the next client on this version drains it and starts this one, any schema migration included; to choose the moment, scripts/dogfood.sh --swap at a quiet point before then, permissions sync $permissions, post-swap errors not read yet"
[ "$permissions" = "no" ] || say "permissions: ${perm_reason:+$perm_reason; }ask the user to run \`ah roster permissions sync\` from a terminal"
peers=$("$install_path/bin/ah" roster status --cwd "$PWD" 2>/dev/null | node -e '
    let t = ""; process.stdin.on("data", (c) => (t += c)).on("end", () => {
        try {
            const d = JSON.parse(t); const names = [];
            for (const team of d.teams || []) for (const m of team.members || []) if (m.live !== false && m.kind === "claude" && m.name) names.push(m.name);
            process.stdout.write([...new Set(names)].sort().join(" "));
        } catch { process.exit(3); }
    })') && peer_ok=true || peer_ok=false
if $peer_ok && [ -n "$peers" ]; then
    say "reload: send /reload-plugins to each live peer when it goes idle (herdr agent prompt <name> \"/reload-plugins\"): $peers"
elif $peer_ok; then
    say "reload: no live peers"
else
    say "reload: the live peers could not be listed (ah roster status failed); list them with ListAgents"
fi
