#!/usr/bin/env node
// One stateful fake of the herdr CLI for the shell tests. A test installs it as `herdr` with ah_test_install_fake_herdr
// (lib-test-env.sh); nothing here is test-specific, so per-test behaviour is a state seed or one of these environment knobs:
//   FAKE_STATE_DIR                where the state lives (herdr.json) and calls/ (one JSON
//                                 file per call: {argv, exit, start_ms, end_ms, t_ns, pid, bin}; t_ns orders the calls)
//   FAKE_HERDR_FAIL=1             every call fails
//   FAKE_HERDR_FAIL_CMD="a b,c d" those two-word commands fail
//   FAKE_HERDR_FAIL_ONCE="a b"    that command fails on its first call only (a marker file in the state dir remembers)
//   FAKE_HERDR_FAIL_ALWAYS_NAME=N `agent start N` fails
//   FAKE_HERDR_FORGET_AGENTS=1    `agent start` succeeds but registers nothing, so every later agent get/list answers not found
//   FAKE_HERDR_AGENT_CWD=D        `agent start` reports its pane's working directory as D
//   FAKE_HERDR_CLOSE_NOOP=1       `pane close` succeeds without removing the pane
//   FAKE_HERDR_RENAME_ON_CLOSE="<pane>=<name>"  closing that pane renames its agent instead of removing it
//   FAKE_HERDR_LIST_FAIL=1        every `agent list` fails;  FAKE_HERDR_LIST_FAIL_FROM=N  the Nth and later ones fail (listcount in the state dir counts them)
//   FAKE_HERDR_NAMELESS_FROM=N + FAKE_HERDR_NAMELESS_PANE=P  from the Nth `agent list` on, that pane's agent is listed without a name; FAKE_HERDR_NAMELESS_SESSION=S lists it under agent session S instead of its own
//   FAKE_HERDR_READ_FAIL_FIRST=N  the first N `agent read` calls fail (readcount in the state dir counts them); later ones print an idle Claude composer
//                                 (or the agent's `screen` in the state); `agent prompt` turns the agent's status to working
//   FAKE_HERDR_PROMPT_NOOP=1      `agent prompt` succeeds but leaves the agent's status alone, so a paste is never seen to start a turn
//   FAKE_HERDR_READ_DELAY_MS=N    every `agent read` takes N ms before it answers
//   FAKE_HERDR_SOCKET=P           `session list` names P as the one instance (without it the command is unhandled, as for most tests)
//   FAKE_HERDR_ON_START_TEAM="src::dst"  as an agent starts, copy src over dst (empty src removes dst)
// Every success prints {id, result} on stdout and exits 0; every failure prints {error:{code,message}} on stderr and exits 1.
// A command not listed below fails with error.code "unhandled". The shapes are the ones the production parsers read.
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

const args = process.argv.slice(2);
const dir = process.env.FAKE_STATE_DIR;
if (!dir) {
  process.stderr.write("fake herdr: FAKE_STATE_DIR is not set\n");
  process.exit(1);
}
const startMs = Date.now();
mkdirSync(join(dir, "calls"), { recursive: true });
const startNs = process.hrtime.bigint().toString();

function finish(code) {
  const file = join(dir, "calls", `${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}.json`);
  writeFileSync(file, JSON.stringify({ argv: args, start_ms: startMs, end_ms: Date.now(), t_ns: startNs, exit: code, pid: process.pid, bin: "herdr" }));
  process.exit(code);
}
const ok = (result) => { console.log(JSON.stringify({ id: "fake", result })); finish(0); };
const err = (code, message) => { process.stderr.write(JSON.stringify({ error: { code, message } }) + "\n"); finish(1); };

const stFile = join(dir, "herdr.json");
const st = existsSync(stFile) ? JSON.parse(readFileSync(stFile, "utf8")) : { next: 1, self: "w1:p0", tabs: {}, panes: {}, agents: {} };
st.tabs ??= {}; st.panes ??= {}; st.agents ??= {};
const save = () => writeFileSync(stFile, JSON.stringify(st));
const cmd = `${args[0]} ${args[1]}`;
const flag = (n) => { const i = args.indexOf(`--${n}`); return i < 0 ? undefined : args[i + 1]; };
const area = () => st.area || { x: 0, y: 0, width: 200, height: 50 };
const tabPanes = (t) => Object.entries(st.panes).filter(([, p]) => p.tab === t);

if (process.env.FAKE_HERDR_FAIL === "1") err("forced", "forced failure");
if ((process.env.FAKE_HERDR_FAIL_CMD || "").split(",").includes(cmd)) err("forced", `forced ${cmd} failure`);
if (process.env.FAKE_HERDR_FAIL_ONCE === cmd) {
  const marker = join(dir, `fail-once-${cmd.replace(/ /g, "_")}`);
  if (!existsSync(marker)) { writeFileSync(marker, ""); err("forced", `forced ${cmd} failure (once)`); }
}

switch (cmd) {
  case "tab create": {
    const ws = flag("workspace") || "w1";
    const t = `${ws}:t${st.next++}`;
    const p = `${ws}:p${st.next++}`;
    st.tabs[t] = { label: flag("label") || "", ws };
    st.panes[p] = { tab: t, ws, cwd: flag("cwd") || null, rect: { ...area() } };
    save();
    ok({ type: "tab_created", tab: { tab_id: t, label: st.tabs[t].label, workspace_id: ws, pane_count: 1, focused: false }, root_pane: { pane_id: p, tab_id: t, workspace_id: ws, cwd: st.panes[p].cwd, focused: false } });
  }
  case "tab rename": {
    const t = args[2];
    if (!st.tabs[t]) err("tab_not_found", `tab ${t} not found`);
    st.tabs[t].label = args.slice(3).join(" ");
    save();
    ok({ type: "tab_info", tab: { tab_id: t, label: st.tabs[t].label, workspace_id: st.tabs[t].ws } });
  }
  case "tab list":
    ok({ type: "tab_list", tabs: Object.entries(st.tabs).filter(([, t]) => !flag("workspace") || t.ws === flag("workspace")).map(([id, t]) => ({ tab_id: id, label: t.label, workspace_id: t.ws, pane_count: tabPanes(id).length, focused: false })) });
  case "session list":
    if (!process.env.FAKE_HERDR_SOCKET) err("unhandled", "session list");
    process.stdout.write(`default ${process.env.FAKE_HERDR_SOCKET}\n`);
    finish(0);
  // falls through: finish exits
  case "pane process-info":
    // the foreground processes a test set in st.processes[pane] ({pid, argv, cwd}); none when it set nothing
    // a process that has exited leaves the pane, as in a real one
    ok({ process_info: { foreground_processes: ((st.processes || {})[flag("pane")] || []).filter((p) => { try { process.kill(p.pid, 0); return true; } catch (e) { return e.code === "EPERM"; } }) } });
  case "pane list":
    ok({ type: "pane_list", panes: Object.entries(st.panes).filter(([, p]) => !flag("workspace") || p.ws === flag("workspace")).map(([id, p]) => ({ pane_id: id, tab_id: p.tab, workspace_id: p.ws, cwd: p.cwd, focused: false })) });
  case "pane layout": {
    const pid = args.includes("--current") ? st.self : flag("pane");
    const p = st.panes[pid];
    if (!p) err("pane_not_found", `pane ${pid} not found`);
    ok({ type: "pane_layout", layout: { tab_id: p.tab, workspace_id: p.ws, area: area(), focused_pane_id: pid, panes: tabPanes(p.tab).map(([id, q]) => ({ pane_id: id, focused: false, rect: q.rect })), splits: [], zoomed: false } });
  }
  case "pane split": {
    const target = flag("pane");
    const p = st.panes[target];
    if (!p) err("pane_not_found", `pane ${target} not found`);
    const id = `${p.ws}:p${st.next++}`;
    const r = p.rect;
    let a, b;
    if (flag("direction") === "right") { const w = Math.floor(r.width / 2); a = { ...r, width: w }; b = { ...r, width: r.width - w, x: r.x + w }; }
    else { const h = Math.floor(r.height / 2); a = { ...r, height: h }; b = { ...r, height: r.height - h, y: r.y + h }; }
    p.rect = a;
    st.panes[id] = { tab: p.tab, ws: p.ws, cwd: flag("cwd") || null, rect: b };
    save();
    ok({ type: "pane_split", pane: { pane_id: id, tab_id: p.tab, workspace_id: p.ws } });
  }
  case "pane zoom":
    ok({});
  case "pane rename":
    ok({ type: "pane_info", pane: { pane_id: args[2] } });
  case "pane close": {
    const [renamePane, renameTo] = (process.env.FAKE_HERDR_RENAME_ON_CLOSE || "").split("=");
    if (renamePane === args[2] && st.agents[renamePane]) { st.agents[renamePane].name = renameTo; save(); ok({ type: "ok" }); }
    if (process.env.FAKE_HERDR_CLOSE_NOOP === "1") ok({ type: "ok" });
    delete st.panes[args[2]];
    delete st.agents[args[2]];
    save();
    ok({ type: "ok" });
  }
  case "pane move": {
    const p = st.panes[args[2]];
    if (!p) err("pane_not_found", `pane ${args[2]} not found`);
    if (args.includes("--new-tab")) { const t = `${p.ws}:t${st.next++}`; st.tabs[t] = { label: "", ws: p.ws }; p.tab = t; }
    save();
    ok({ type: "pane_moved", move_result: { pane: { pane_id: args[2], tab_id: p.tab, workspace_id: p.ws } } });
  }
  case "agent start": {
    const name = args[2];
    const pid = flag("pane");
    if (process.env.FAKE_HERDR_FAIL_ALWAYS_NAME && name === process.env.FAKE_HERDR_FAIL_ALWAYS_NAME) {
      process.stderr.write("fake herdr: agent start failed (always)\n");
      finish(1);
    }
    if (!st.panes[pid]) err("pane_not_found", `pane ${pid} not found`);
    // Stands in for another spawn finishing inside this launch: "<src>::<dst>" copies src over dst, an empty src removes dst.
    if (process.env.FAKE_HERDR_ON_START_TEAM) {
      const [src, dst] = process.env.FAKE_HERDR_ON_START_TEAM.split("::");
      if (src) {
        // With FAKE_HERDR_KEEP_ROWS=1 the copy models a concurrent spawn that reads the file and writes it back whole: the rows already
        // there (the starting member's own, written before this launch) stay unless the source names the same member. Without it the
        // source replaces the file outright.
        let prior = null;
        try { prior = JSON.parse(readFileSync(dst, "utf8")); } catch { /* no readable file: nothing to keep */ }
        mkdirSync(dirname(dst), { recursive: true });
        copyFileSync(src, dst);
        try {
          const next = JSON.parse(readFileSync(dst, "utf8"));
          const kept = process.env.FAKE_HERDR_KEEP_ROWS === "1" && Array.isArray(prior?.members) && Array.isArray(next.members) ? prior.members.filter((m) => !next.members.some((x) => x.name === m.name)) : [];
          if (kept.length) writeFileSync(dst, JSON.stringify({ ...next, members: [...next.members, ...kept] }, null, 2) + "\n");
        } catch { /* an unparseable source is left as written */ }
      } else rmSync(dst, { force: true });
    }
    if (process.env.FAKE_HERDR_AGENT_CWD) st.panes[pid].cwd = process.env.FAKE_HERDR_AGENT_CWD;
    if (process.env.FAKE_HERDR_FORGET_AGENTS !== "1") st.agents[pid] = { name, status: "idle" };
    save();
    ok({ pane: { pane_id: pid, tab_id: st.panes[pid].tab, workspace_id: st.panes[pid].ws }, agent: { name, ready: true } });
  }
  case "agent list": {
    const countFile = join(dir, "listcount");
    const n = (existsSync(countFile) ? Number(readFileSync(countFile, "utf8")) : 0) + 1;
    writeFileSync(countFile, String(n));
    if (process.env.FAKE_HERDR_LIST_FAIL === "1" || (process.env.FAKE_HERDR_LIST_FAIL_FROM && n >= Number(process.env.FAKE_HERDR_LIST_FAIL_FROM))) err("unreachable", "fake herdr: daemon unreachable");
    const nameless = process.env.FAKE_HERDR_NAMELESS_FROM && n >= Number(process.env.FAKE_HERDR_NAMELESS_FROM);
    ok({ type: "agent_list", agents: Object.entries(st.agents).filter(([id]) => st.panes[id]).map(([id, a]) => nameless && id === process.env.FAKE_HERDR_NAMELESS_PANE
      ? { pane_id: id, tab_id: st.panes[id].tab, workspace_id: st.panes[id].ws, cwd: st.panes[id].cwd, agent_status: a.status, agent_session: { value: process.env.FAKE_HERDR_NAMELESS_SESSION || `session-${a.name}` } }
      : ({ name: a.name, pane_id: id, tab_id: st.panes[id].tab, workspace_id: st.panes[id].ws, cwd: st.panes[id].cwd, agent_status: a.status, terminal_title_stripped: a.name, agent_session: { value: `session-${a.name}` } })) });
  }
  case "agent read": {
    const found = Object.entries(st.agents).find(([id, a]) => st.panes[id] && a.name === args[2]);
    if (!found) err("agent_not_found", `agent ${args[2]} not found`);
    const readFile = join(dir, "readcount");
    const reads = (existsSync(readFile) ? Number(readFileSync(readFile, "utf8")) : 0) + 1;
    writeFileSync(readFile, String(reads));
    if (process.env.FAKE_HERDR_READ_DELAY_MS) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, Number(process.env.FAKE_HERDR_READ_DELAY_MS));
    if (process.env.FAKE_HERDR_READ_FAIL_FIRST && reads <= Number(process.env.FAKE_HERDR_READ_FAIL_FIRST)) err("unreadable", "fake herdr: the screen could not be read");
    console.log(found[1].screen ?? "  earlier output\n\n────────────────────────────────────────\n❯ \n────────────────────────────────────────\n  ? for shortcuts");
    finish(0);
  }
  case "agent send-keys":
    ok({ type: "ok" });
  case "agent prompt": {
    const found = Object.entries(st.agents).find(([id, a]) => st.panes[id] && a.name === args[2]);
    if (!found) err("agent_not_found", `agent ${args[2]} not found`);
    if (process.env.FAKE_HERDR_PROMPT_NOOP !== "1") found[1].status = "working";
    save();
    ok({ type: "ok" });
  }
  case "agent get": {
    const found = Object.entries(st.agents).find(([id, a]) => st.panes[id] && a.name === args[2]);
    if (!found) err("agent_not_found", `agent ${args[2]} not found`);
    ok({ type: "agent_info", agent: { name: found[1].name, pane_id: found[0], agent_status: found[1].status, interactive_ready: found[1].status === "idle" } });
  }
}
err("unhandled", `fake herdr: unhandled args ${JSON.stringify(args)}`);
