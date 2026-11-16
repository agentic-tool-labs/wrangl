#!/usr/bin/env node
// A fake `tmux` / `herdr` for the paste tests: `node fake-mux.mjs <tmux|herdr> <args...>`. State lives in
// the files under $FAKE_MUX: `screen` (what a read returns), `status` (herdr agent_status), `mode`
// (how the fake behaves), `calls.jsonl` (every call's argv), `buf` (the loaded tmux buffer).
// Modes: normal; nosubmit (Enter does nothing); enter-prompt / enter-idle (what Enter leaves on screen); herdr-stage (herdr's prompt call leaves the payload staged); extra (a person's text lands with the paste);
// notlive (herdr: the agent is gone).
import fs from "node:fs";
import path from "node:path";

const dir = process.env.FAKE_MUX;
const [which, ...args] = process.argv.slice(2);
const read = (f, d = "") => {
  try {
    return fs.readFileSync(path.join(dir, f), "utf8");
  } catch {
    return d;
  }
};
const write = (f, s) => fs.writeFileSync(path.join(dir, f), s);
fs.appendFileSync(path.join(dir, "calls.jsonl"), JSON.stringify([which, ...args]) + "\n");
const mode = read("mode", "normal").trim();
const idle = () => fs.readFileSync(process.env.FAKE_IDLE, "utf8");
const busy = () => fs.readFileSync(process.env.FAKE_BUSY, "utf8");
// mode "wrap": a narrow pane draws the composer in rows of 76 columns, splitting a long token at the column.
const staged = (text) => {
  const rows = mode === "wrap" ? text.match(/.{1,76}/g) : [text];
  return idle().split("\n").flatMap((l) => (l.startsWith("❯") ? rows.map((r, i) => (i === 0 ? `❯ ${r}` : `  ${r}`)) : [l])).join("\n");
};

if (which === "tmux") {
  const sub = args[0];
  if (sub === "capture-pane") {
    // After Enter the screen shows a running turn for FAKE_BUSY_CAPTURES reads, then goes back to idle.
    const left = Number(read("busyleft", "0"));
    if (left > 0) {
      if (left === 1) write("screen", idle());
      write("busyleft", String(left - 1));
    }
    process.stdout.write(read("screen"));
  }
  else if (sub === "load-buffer") write("buf", fs.readFileSync(args[args.length - 1], "utf8"));
  else if (sub === "paste-buffer") write("screen", staged(read("buf") + (mode === "extra" ? " and then some" : "")));
  else if (sub === "send-keys" && args.includes("Enter") && mode === "enter-prompt") write("screen", fs.readFileSync(process.env.FAKE_PERM, "utf8"));
  else if (sub === "send-keys" && args.includes("Enter") && mode === "enter-idle") write("screen", idle());
  else if (sub === "send-keys" && args.includes("Enter") && mode !== "nosubmit") {
    write("screen", busy());
    if (process.env.FAKE_BUSY_CAPTURES) write("busyleft", process.env.FAKE_BUSY_CAPTURES);
  }
  process.exit(0);
}

if (which === "herdr") {
  const [a, b] = args;
  if (a === "agent" && b === "get") {
    if (mode === "notlive") {
      process.stdout.write(JSON.stringify({ error: { code: "agent_not_found" } }));
      process.exit(1);
    }
    process.stdout.write(JSON.stringify({ result: { agent: { agent_status: read("status", "idle").trim(), interactive_ready: true } } }));
  } else if (a === "agent" && b === "read") process.stdout.write(read("screen"));
  else if (a === "agent" && b === "prompt" && mode === "herdr-stage") {
    // The prompt call returns with the payload still in the composer and the agent idle.
    write("screen", staged(args[3]));
    process.stdout.write(JSON.stringify({ result: { agent: { agent_status: "idle" } } }));
  } else if (a === "agent" && b === "prompt") {
    write("status", "working");
    write("screen", busy());
    process.stdout.write(JSON.stringify({ result: { agent: { agent_status: "working" } } }));
  }
  process.exit(0);
}
process.exit(0);
