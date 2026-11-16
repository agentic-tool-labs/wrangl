#!/bin/bash
# agent-hierarchy — production code never touches Codex credentials.
#
# Static checks over the shipped code (hooks/, mod/, bin/, native/src/): no name of the Codex auth file or its token fields, and no
# JSON-RPC method that asks the app-server about an account or a token. The set of methods the code sends is also pinned to
# an allow-list, so a new request cannot slip in unnoticed. tests/ is excluded: the live-probe scripts are the one place a
# login copy is allowed.
#
# Each check is proven against a scratch tree that holds one planted name, so the scan is known to be able to fail.

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
HERE="$(cd "$(dirname "$0")" && pwd)"
PLUGIN="$(cd "$HERE/.." && pwd)"
PASS=0
FAIL=0
check() { # <label> <command string>
  if eval "$2"; then PASS=$((PASS + 1)); else FAIL=$((FAIL + 1)); echo "FAIL: $1"; fi
}

# The auth file and its token fields.
FIELD_NAMES='auth\.json|access_token|refresh_token|id_token|OPENAI_API_KEY'
# Methods and fields through which a server hands out an account or a token.
RPC_NAMES='getAuthStatus|includeToken|authToken|account/read|account/login|account/logout|account/chatgptAuthTokens|loginApiKey|loginChatGpt|getAccount'
# The only JSON-RPC methods the shipped code may send.
ALLOWED_METHODS='initialize initialized thread/start thread/resume turn/start turn/steer'

scan_names() { # <root> <pattern>: matches in the shipped directories under root
  local root=$1 d
  # The one allowed hit: Ah.Codex/CodexRequests.cs lists the token-refresh request among those the adapter returns on without
  # answering, so it never replies with a token.
  for d in hooks mod bin native/src; do [ -e "$root/$d" ] && grep -rnE --exclude-dir=node_modules --exclude-dir=obj -- "$2" "$root/$d"; done \
    | grep -vE 'Ah\.Codex/CodexRequests\.cs:[0-9]+: +"account/chatgptAuthTokens/refresh",$'
  return 0
}

methods_sent() { # <root>: every literal method passed to request( or notify(, one per line
  local root=$1 d
  for d in hooks mod bin native/src; do [ -e "$root/$d" ] && grep -rhoE --exclude-dir=node_modules --exclude-dir=obj -- '(request|notify|RequestAsync|Notify)\( *"[A-Za-z/_]+"' "$root/$d"; done | sed -E 's/.*"([A-Za-z\/_]+)"$/\1/' | sort -u
  return 0
}

stray_methods() { # <root>: sent methods that are not in the allow-list
  local m
  for m in $(methods_sent "$1"); do
    case " $ALLOWED_METHODS " in *" $m "*) ;; *) echo "$m" ;; esac
  done
}

# ---- the shipped tree
check "no name of the auth file or a token field in hooks/, mod/ or bin/" '[ -z "$(scan_names "$PLUGIN" "$FIELD_NAMES")" ]'
check "no account or token JSON-RPC name in hooks/, mod/ or bin/" '[ -z "$(scan_names "$PLUGIN" "$RPC_NAMES")" ]'
check "the methods the code sends are exactly within the allow-list" '[ -z "$(stray_methods "$PLUGIN")" ]'
check "the scan sees the methods the adapter really sends (it is not scanning nothing)" '[ "$(methods_sent "$PLUGIN" | wc -l | tr -d " ")" -ge 5 ]'
check "no native Ah.Codex source names the Codex home or its config (.codex, CODEX_HOME)" '[ -z "$(grep -rnE --include=*.cs -- "\.codex|CODEX_HOME" "$PLUGIN/native/src/Ah.Codex" 2>/dev/null)" ]'
check "the scan sees the methods the C# adapter sends (the C# pattern is not matching nothing)" '[ "$(grep -rhoE "RequestAsync\( *\"[A-Za-z/_]+\"" "$PLUGIN/native/src/Ah.Codex" | wc -l | tr -d " ")" -ge 3 ]'
check "the C# methods are inside the allow-list too" '[ -z "$(stray_methods "$PLUGIN")" ]'

# ---- the scan can fail: one plant per check in a scratch tree, and the nearest non-match beside it
T=$(mktemp -d "${TMPDIR:-/tmp}/ah-nocred.XXXXXX")
if [ -z "$T" ] || [ ! -d "$T" ]; then echo "FAIL: could not make a scratch directory"; exit 1; fi
mkdir -p "$T/hooks" "$T/mod" "$T/bin"
printf 'const f = "x/auth.json";\n' > "$T/hooks/a.mjs"
check "plant: the auth file name is found in hooks/" '[ -n "$(scan_names "$T" "$FIELD_NAMES")" ]'
printf 'const f = "authorize";\nconst g = "auth";\nconst h = "id";\nconst k = "token_count";\n' > "$T/hooks/a.mjs"
check "nearest non-match: auth, authorize, id and token_count alone are not flagged" '[ -z "$(scan_names "$T" "$FIELD_NAMES")" ]'
printf '' > "$T/hooks/a.mjs"
printf 'const t = j.refresh_token;\n' > "$T/mod/v.ts"
check "plant: a token field name is found in mod/" '[ -n "$(scan_names "$T" "$FIELD_NAMES")" ]'
rm -f "$T/mod/v.ts"
printf 'client.request("getAuthStatus", {});\n' > "$T/bin/ahd"
check "plant: an auth-status method is found in bin/" '[ -n "$(scan_names "$T" "$RPC_NAMES")" ]'
printf 'client.request("thread/resume", {});\n' > "$T/bin/ahd"
check "nearest non-match: thread/resume is not an account or token method" '[ -z "$(scan_names "$T" "$RPC_NAMES")" ]'
printf 'client.request("account/rateLimits/read", {});\n' > "$T/bin/ahd"
check "plant: a method outside the allow-list is reported" '[ "$(stray_methods "$T")" = "account/rateLimits/read" ]'
printf 'client.request("turn/start", {});\nclient.notify("initialized");\n' > "$T/bin/ahd"
check "nearest non-match: allowed methods report nothing" '[ -z "$(stray_methods "$T")" ]'
mkdir -p "$T/native/src/Ah.Codex"
printf '        "account/chatgptAuthTokens/refresh",\n' > "$T/native/src/Ah.Codex/CodexRequests.cs"
check "allow-list: the token-refresh name in CodexRequests.cs is not flagged" '[ -z "$(scan_names "$T" "$RPC_NAMES")" ]'
printf '        "account/chatgptAuthTokens/refresh",\n' > "$T/native/src/Ah.Codex/Other.cs"
check "nearest non-match: the same name in another file is flagged" '[ -n "$(scan_names "$T" "$RPC_NAMES")" ]'
printf 'client.request("account/chatgptAuthTokens/refresh", tok);\n' > "$T/native/src/Ah.Codex/CodexRequests.cs"
rm -f "$T/native/src/Ah.Codex/Other.cs"
check "nearest non-match: that name sent from CodexRequests.cs is flagged" '[ -n "$(scan_names "$T" "$RPC_NAMES")" ]'
rm -rf "$T/native"
rm -f "$T/bin/ahd" "$T/hooks/a.mjs"
rmdir "$T/hooks" "$T/mod" "$T/bin" "$T"

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
