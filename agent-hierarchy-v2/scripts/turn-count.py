#!/usr/bin/env python3
"""Turns per team-hour, by what opened each turn (fewer-turns spec S0, §8). Read-only; run by hand.

usage: turn-count.py TEAM_FILE [--hours H] [--projects DIR]
       turn-count.py --self-test

Sessions come from the team file (orchestrator.session_id, members[].session_id); each
transcript is <projects>/*/<session_id>.jsonl. A turn opens on a prompt row (the
turn-times.py `is_prompt` boundary) or on a Stop hook's blocking-error attachment, the
row a Stop-hook-forced continuation leaves in the transcript.
"""
import glob, json, os, sys, tempfile
from collections import Counter
from datetime import datetime, timedelta, timezone

CLASSES = ("ah-mail", "cross-session", "stop-hook", "task-notification", "async-rewake", "user", "other")


def ts(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def is_prompt(d):
    if d.get("type") != "user" or d.get("isSidechain"):
        return False
    c = (d.get("message") or {}).get("content")
    if isinstance(c, str):
        return True
    return bool(c) and not any(b.get("type") == "tool_result" for b in c)


def is_stop_block(d):
    a = d.get("attachment") or {}
    return a.get("type") == "hook_blocking_error" and a.get("hookEvent") == "Stop"


def text(d):
    c = (d.get("message") or {}).get("content")
    if isinstance(c, list):
        c = " ".join(b.get("text", "") for b in c if b.get("type") == "text")
    return str(c or "")


def classify(d):
    if is_stop_block(d):
        return "stop-hook"
    t = text(d)
    if "ah mail for " in t:
        return "ah-mail"
    if "<cross-session-message" in t:
        return "cross-session"
    if "<task-notification>" in t:
        return "task-notification"
    # async-rewake: no pattern until E6 shows the row shape; such turns land in user/other.
    if not d.get("isMeta"):
        return "user"
    return "other"


def count(path, since, until):
    classes, requests, reports = Counter(), 0, 0
    with open(path, errors="replace") as f:
        for line in f:
            try:
                d = json.loads(line)
            except ValueError:
                continue
            if not d.get("timestamp") or d.get("isSidechain") or not since <= ts(d["timestamp"]) <= until:
                continue
            if d.get("type") == "api-request":
                requests += 1
            elif is_prompt(d) or is_stop_block(d):
                k = classify(d)
                classes[k] += 1
                if k == "ah-mail":
                    reports += text(d).count("- report from ")
    return classes, requests, reports


def sessions(team):
    o = team.get("orchestrator") or {}
    out = [("orchestrator", "orchestrator", o.get("session_id"))]
    out += [(m.get("role"), m.get("name"), m.get("session_id")) for m in team.get("members") or []]
    return [s for s in out if s[2]]


def report(team_file, hours, projects, until=None):
    team = json.load(open(team_file))
    until = until or datetime.now(timezone.utc)
    since = until - timedelta(hours=hours)
    rows, total, total_req, total_rep = [], Counter(), 0, 0
    for role, name, sid in sessions(team):
        paths = glob.glob(os.path.join(projects, "*", sid + ".jsonl"))
        c, r, rep = Counter(), 0, 0
        for p in paths:
            c2, r2, rep2 = count(p, since, until)
            c += c2; r += r2; rep += rep2
        rows.append((role, name, sid, c, r, bool(paths)))
        total += c; total_req += r; total_rep += rep
    ph = lambda n: f"{n / hours:6.1f}"
    out = [f"team {team.get('team_id') or team_file}  window {since:%Y-%m-%d %H:%M}..{until:%H:%M} UTC ({hours:g} h)",
           f"{'role':<16}{'name':<32}{'turns/h':>8}{'req/h':>8}  by class (per hour)"]
    for role, name, sid, c, r, found in rows:
        by = "  ".join(f"{k} {c[k] / hours:.1f}" for k in CLASSES if c[k])
        out.append(f"{role:<16}{name[:31]:<32}{ph(sum(c.values())):>8}{ph(r):>8}  {by if found else '(no transcript)'}")
    out.append(f"{'TEAM':<48}{ph(sum(total.values())):>8}{ph(total_req):>8}  "
               + "  ".join(f"{k} {total[k] / hours:.1f}" for k in CLASSES if total[k]))
    out.append(f"reports delivered by ah mail: {total_rep} ({total_rep / hours:.1f}/h)")
    return "\n".join(out), rows, total, total_req, total_rep


def self_test():
    now = datetime(2026, 11, 8, 12, 0, tzinfo=timezone.utc)
    at = lambda m: (now - timedelta(minutes=m)).isoformat().replace("+00:00", "Z")
    user = lambda m, content, **kw: dict(type="user", timestamp=at(m), message={"role": "user", "content": content}, **kw)
    rows = [
        user(100, "please build it"),                                           # user
        {"type": "api-request", "timestamp": at(99)},
        {"type": "api-request", "timestamp": at(99)},                           # retry
        user(98, [{"type": "tool_result", "tool_use_id": "t", "content": "ah mail for x"}]),  # not a turn
        user(90, "The ah2 plugin sent a message:\nah mail for o@t (2):\n- report from implementor@t\n- report from reviewer@t", isMeta=True),
        user(80, "Another Claude session sent a message:\n<cross-session-message from=\"x\">hi", isMeta=True),
        user(70, [{"type": "text", "text": "<task-notification>\n<task-id>b1</task-id>"}], isMeta=True),
        {"type": "attachment", "timestamp": at(60), "attachment": {"type": "hook_blocking_error", "hookEvent": "Stop"}},
        {"type": "attachment", "timestamp": at(59), "attachment": {"type": "hook_blocking_error", "hookEvent": "PreToolUse"}},  # not a turn
        {"type": "attachment", "timestamp": at(58), "attachment": {"type": "hook_success", "hookEvent": "Stop"}},  # not a turn
        user(50, "<system-reminder>meta</system-reminder>", isMeta=True),     # other
        user(40, "sidechain prompt", isSidechain=True),                         # not a turn
        user(200, "outside the window"),                                        # not counted
        {"type": "api-request", "timestamp": at(300)},                          # not counted
        user(30, "brief to a member: ah mail for m@t (1):\n- brief from orchestrator@t"),  # ah-mail, no report
    ]
    with tempfile.TemporaryDirectory() as tmp:
        team_file = os.path.join(tmp, "t.json")
        json.dump({"team_id": "fixture", "orchestrator": {"session_id": "s-orch"},
                   "members": [{"role": "implementor", "name": "t-implementor", "session_id": "s-impl"},
                               {"role": "reviewer", "name": "t-reviewer", "session_id": None}]}, open(team_file, "w"))
        os.makedirs(os.path.join(tmp, "projects", "p"))
        with open(os.path.join(tmp, "projects", "p", "s-orch.jsonl"), "w") as f:
            f.write("not json\n" + "".join(json.dumps(r) + "\n" for r in rows))
        text_out, srows, total, req, rep = report(team_file, 2, os.path.join(tmp, "projects"), until=now)
    print(text_out)
    assert [s[1] for s in srows] == ["orchestrator", "t-implementor"], srows  # null session_id skipped
    assert total == Counter({"ah-mail": 2, "user": 1, "cross-session": 1, "task-notification": 1,
                             "stop-hook": 1, "other": 1}), total
    assert req == 2 and rep == 2, (req, rep)
    assert srows[1][5] is False  # missing transcript reported, not fatal
    print("self-test ok")


def main(argv):
    if "--self-test" in argv:
        return self_test()
    hours, projects, team_file = 2.0, os.path.expanduser("~/.claude/projects"), None
    it = iter(argv)
    for a in it:
        if a == "--hours":
            hours = float(next(it))
        elif a == "--projects":
            projects = next(it)
        else:
            team_file = a
    if not team_file:
        sys.exit(__doc__)
    print(report(team_file, hours, projects)[0])


if __name__ == "__main__":
    main(sys.argv[1:])
