# Helpers for the follow-up probes (brief 20261102-093325-3twq). Sourced after lib-audit.sh.
BOX() { "$AHD" inspect entries --address "orchestrator@$TEAM" --all 2>/dev/null; }
slash() { wait_ready claude "$1" 120; tm send-keys -t "$1" -l "$2"; sleep 0.5; tm send-keys -t "$1" Enter; }
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
# tx_marks <regex> <transcript...>: one line per transcript row matching, with its time, type, and whether it is a tool result.
tx_marks() {
  local re=$1; shift
  python3 -I -c '
import json, re, sys
rx = re.compile(sys.argv[1])
for p in sys.argv[2:]:
    try:
        lines = open(p, errors="replace")
    except OSError:
        continue
    for raw in lines:
        m = rx.search(raw)
        if not m:
            continue
        try:
            d = json.loads(raw)
        except ValueError:
            continue
        kind = d.get("type", "?")
        sub = (d.get("attachment") or {}).get("type", "") if isinstance(d.get("attachment"), dict) else ""
        tr = "tool_result" if "\"tool_use_id\"" in raw else "-"
        print(d.get("timestamp", "?"), kind, sub or "-", tr, raw[max(0, m.start() - 40):m.end() + 160].replace("\n", " "))
' "$re" "$@"
}
# tx_after <transcript> <tail lines>: the assistant's last turns and tool calls, readable.
tx_after() {
  python3 -I -c '
import json, sys
rows = []
for raw in open(sys.argv[1], errors="replace"):
    try:
        d = json.loads(raw)
    except ValueError:
        continue
    if d.get("type") != "assistant":
        continue
    for c in d.get("message", {}).get("content", []):
        if c.get("type") == "text":
            rows.append((d.get("timestamp"), "text", c["text"][:600]))
        elif c.get("type") == "tool_use":
            rows.append((d.get("timestamp"), "tool", c.get("name", "") + " " + json.dumps(c.get("input", {}))[:400]))
for r in rows[-int(sys.argv[2]):]:
    print(*r)
' "$1" "$2"
}
# tx_rows <transcript>: every row the session received, tool results apart, with its time: what actually woke it.
tx_rows() {
  python3 -I -c '
import json, sys
for raw in open(sys.argv[1], errors="replace"):
    try:
        d = json.loads(raw)
    except ValueError:
        continue
    k = d.get("type")
    if k == "assistant":
        for c in d.get("message", {}).get("content", []):
            n = c.get("name", "") if c.get("type") == "tool_use" else c.get("type")
            print(d.get("timestamp"), "ASSISTANT", n, str(c.get("text") or c.get("input") or "")[:160].replace("\n", " "))
        continue
    if k == "user" and "\"tool_use_id\"" in raw:
        print(d.get("timestamp"), "tool_result", "-", raw[raw.find("\"content\""):][:160])
        continue
    a = d.get("attachment")
    sub = a.get("type", "") if isinstance(a, dict) else ""
    body = json.dumps(a)[:320] if isinstance(a, dict) else json.dumps(d.get("message", d.get("content", "")))[:320]
    print(d.get("timestamp", "?"), k, sub or d.get("subtype", "-"), d.get("origin", ""), body)
' "$1"
}
RINGS='You have [0-9]+ message|ah mail for orchestrator@|[0-9]+ new; [0-9]+ unread|reminder: [0-9]+ unread'
# sample_door <evidence dir> <seconds> <orch session id>: the doorbell note and ring count over time.
sample_door() {
  local d=$1 sid=$3 end=$((SECONDS + $2))
  while [ "$SECONDS" -lt "$end" ]; do
    printf '%s rings=%s note=%s\n' "$(now)" "$(tx_marks "$RINGS" "$ORCH_TX" | grep -vc ' tool_result ')" "$(tr -d '\n' <"$AH_HOME/mailbox-notes/doorbell/$sid.json" 2>/dev/null | cut -c1-400)"
    sc=$(cap "$ORCH" | tail -8)
    if [ "$sc" != "${psc:-}" ]; then psc=$sc; { echo "== $(now)"; printf '%s\n' "$sc"; } | scrub | cut -c1-150 >>"$T/screens-$$"; fi
    sleep 5
  done | scrub | ev "$d" door-samples.txt
  [ -f "$T/screens-$$" ] && ev "$d" screen-changes.txt <"$T/screens-$$"; rm -f "$T/screens-$$"
}
# mod_state <evidence dir>: what the mod recorded of each mail it submitted, and where.
mod_state() {
  { grep -rlE 'not delivered|"delivered"' "$T" --exclude='*.jsonl' --exclude-dir=plugin --exclude-dir=msgs --exclude-dir=evidence 2>/dev/null | head -8 | while read -r f; do echo "--- $f"; tail -c 1500 "$f"; echo; done; } | scrub | ev "$1" mod-state.txt
}
