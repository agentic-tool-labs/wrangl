// The ah2 herdr mod's link to ah (herdr-http-link.md): where ah runs (target-model.js) and how a copy reaches it.
import { test } from "node:test";
import assert from "node:assert/strict";
import { targetOf, runnerOf, paneOf, UNRESOLVED, CHECKING } from "../../herdr-desktop/mods/ah2/target-model.js";
import { readFileSync } from "node:fs";
import { terminalFinder, qaModel, sendAnswer, NO_REPLY, readThread } from "../../herdr-desktop/mods/ah2/model.js";
import { readItemLog } from "../../herdr-desktop/mods/ah2/board-model.js";
import { Link, UNREACHABLE, NO_ANSWER, TOO_OLD } from "../../herdr-desktop/mods/ah2/link-model.js";

const fixture = JSON.parse(readFileSync(new URL("../../herdr-desktop/fixture/events.json", import.meta.url), "utf8"));
// the fixture's single-choice decision card (as herdr-qa-model.test.mjs uses)
const sendCard = () => qaModel(new Map(fixture.slices.map((s) => [s.id, s])), Date.parse(fixture.observed_at)).teams[0].cards[2];

const HERE = { name: null, layout: "ready" };
const M = (name) => ({ name, layout: "ready" });

test("the resolver picks the target: platform x machines x setting, unresolved included", () => {
  const kind = (o) => {
    const t = targetOf(o);
    return t.kind === "machine" ? `machine ${t.name}` : t.kind === "wsl" ? `wsl ${t.distro ?? "default"}` : t.kind;
  };
  const cases = [
    // auto off Windows: this computer, whatever machines herdr lists
    [{ platform: "macos", machines: [HERE] }, "local"],
    [{ platform: "linux", machines: [HERE, M("box")] }, "local"],
    [{ platform: "macos", machines: [HERE, M("a"), M("b")] }, "local"],
    // auto on Windows: the one machine, else WSL; two machines are never guessed between, and no WSL is unresolved
    [{ platform: "windows", machines: [HERE, M("wsl")] }, "machine wsl"],
    [{ platform: "windows", machines: [HERE] }, "wsl default"],
    [{ platform: "windows", machines: [] }, "wsl default"],
    [{ platform: "windows", machines: [HERE, M("a"), M("b")] }, "unresolved"],
    [{ platform: "windows", machines: [HERE], wsl: false }, "unresolved"],
    [{ platform: "windows", machines: [HERE, M("")] }, "wsl default"],
    // the setting wins on every platform
    [{ platform: "windows", machines: [HERE, M("a"), M("b")], setting: "machine:b" }, "machine b"],
    [{ platform: "windows", machines: [HERE, M("a")], setting: "local" }, "local"],
    [{ platform: "windows", machines: [HERE], setting: "wsl:Ubuntu" }, "wsl Ubuntu"],
    [{ platform: "windows", machines: [HERE], setting: "wsl:", wsl: false }, "wsl default"],
    [{ platform: "macos", machines: [HERE], setting: "machine:box" }, "machine box"],
    [{ platform: "windows", machines: [HERE, M("a")], setting: "auto" }, "machine a"],
    [{ platform: "windows", machines: [HERE, M("a")], setting: "machine:" }, "machine a"],
  ];
  for (const [o, want] of cases) assert.equal(kind(o), want, JSON.stringify(o));
});

test("a runner runs ah where the target is, and says why when it cannot", async () => {
  const calls = [];
  const exec = async (cmd, args, opts) => (calls.push([cmd, args, opts]), { code: 0, stdout: "", stderr: "" });
  const read = async (path, opts) => (calls.push(["read", path, opts]), "text");
  const one = async (t, go) => {
    calls.length = 0;
    await go(runnerOf(exec, read, t, { platform: "windows" }));
    return calls[0];
  };
  const ahWhere = (r) => r.exec("ah", ["where"], { timeout: 10000 });
  assert.deepEqual(await one({ kind: "local" }, ahWhere), ["ah", ["where"], { timeout: 10000 }]);
  assert.deepEqual(await one({ kind: "machine", name: "m" }, ahWhere), ["ah", ["where"], { timeout: 10000, on: "m" }]);
  assert.deepEqual(await one({ kind: "wsl", distro: "Ubuntu" }, ahWhere), ["wsl.exe", ["-d", "Ubuntu", "--exec", "ah", "where"], { timeout: 10000 }]);
  assert.deepEqual(await one({ kind: "wsl", distro: null }, ahWhere), ["wsl.exe", ["--exec", "ah", "where"], { timeout: 10000 }]);
  assert.deepEqual(await one({ kind: "machine", name: "m" }, (r) => r.read("/h/CHANGELOG.md")), ["read", "/h/CHANGELOG.md", { on: "m" }]);
  assert.equal(await runnerOf(exec, read, { kind: "wsl", distro: null }).read("/h/x"), null, "a file in a WSL home is not read");
  // install: Windows to a remote target converts herdr's home with wslpath; this computer runs it unchanged
  const wslInstall = await one({ kind: "wsl", distro: null }, (r) => r.install("C:\\h", { timeout: 60000 }));
  assert.deepEqual(wslInstall, ["wsl.exe", ["--exec", "sh", "-c", 'HERDR_APP_HOME="$(wslpath -u "$1")" exec ah herdr install', "sh", "C:\\h"], { timeout: 60000 }]);
  calls.length = 0;
  await runnerOf(exec, read, { kind: "machine", name: "m" }, { platform: "macos" }).install("/h", {});
  assert.deepEqual(calls[0], ["sh", ["-c", 'HERDR_APP_HOME="$1" exec ah herdr install', "sh", "/h"], {}], "off Windows the install is unchanged");
  calls.length = 0;
  await runnerOf(exec, read, { kind: "local" }, { platform: "macos" }).install("/h", {}, ["--replace-layout"]);
  assert.deepEqual(calls[0], ["sh", ["-c", 'HERDR_APP_HOME="$1" exec ah herdr install --replace-layout', "sh", "/h"], {}], "the Settings button's flag is appended");

  // the reasons a view shows
  const reason = (p) => p.then(() => "ok", (e) => e.down ?? `no reason: ${e.message}`);
  assert.equal(await reason(runnerOf(exec, read, { kind: "unresolved" }).exec("ah", [])), UNRESOLVED);
  const dead = async () => { throw new Error("can't start wsl.exe"); };
  assert.equal(await reason(runnerOf(dead, read, { kind: "wsl", distro: "Ubuntu", auto: false }).exec("ah", [])), "WSL is not running (Ubuntu)");
  assert.equal(await reason(runnerOf(dead, read, { kind: "machine", name: "m" }).exec("ah", [])), "WSL is not running (m)");
  let missing = 0;
  assert.equal(await reason(runnerOf(dead, read, { kind: "wsl", distro: null, auto: true }, { onMissingWsl: () => missing++ }).exec("ah", [])), UNRESOLVED);
  assert.equal(missing, 1, "auto WSL with no wsl.exe makes the next target unresolved");
  // the nearest inputs that must keep their own error: a timeout, and this computer
  const slow = async () => { throw new Error("ah took longer than 10 s and was stopped"); };
  assert.equal(await reason(runnerOf(slow, read, { kind: "wsl", distro: "Ubuntu" }).exec("ah", [])), "no reason: ah took longer than 10 s and was stopped");
  assert.equal(await reason(runnerOf(dead, read, { kind: "local" }).exec("ah", [])), "no reason: can't start wsl.exe");
});

test("in a herdr check a runner runs no ah and reads no file, whatever the target (b102)", async () => {
  const calls = [];
  const exec = async (...a) => (calls.push(a), { code: 0, stdout: "", stderr: "" });
  const read = async (...a) => (calls.push(a), "text");
  const reason = (p) => p.then(() => "ok", (e) => e.down ?? `no reason: ${e.message}`);
  let missing = 0;
  const targets = [{ kind: "local" }, { kind: "machine", name: "m" }, { kind: "wsl", distro: "Ubuntu", auto: false }, { kind: "wsl", distro: null, auto: true }, { kind: "unresolved" }];
  for (const t of targets) {
    const r = runnerOf(exec, read, t, { platform: "windows", check: true, onMissingWsl: () => missing++ });
    assert.equal(await reason(r.exec("ah", ["where"])), CHECKING, `exec ${t.kind}`);
    assert.equal(await reason(r.install("/h", {})), CHECKING, `install ${t.kind}`);
    assert.equal(await r.read("/h/CHANGELOG.md"), null, `read ${t.kind}`);
  }
  assert.deepEqual(calls, [], "herdr's exec and readTextFile are never called");
  assert.equal(missing, 0, "auto WSL: nothing ran, so onMissingWsl is not told");
  // the nearest input that must not match: check false runs as today
  await runnerOf(exec, read, { kind: "local" }, { check: false }).exec("ah", ["where"]);
  assert.deepEqual(calls, [["ah", ["where"], {}]]);
});

test("on a herdr machine target, a terminal's M/ prefix is stripped to match ah's pane id", () => {
  const terms = [{ address: "box/t4", state: "live", labels: {} }, { address: "t9", state: "live", labels: {} }];
  assert.equal(paneOf({ kind: "machine", name: "box" }, "box/t4"), "t4");
  assert.equal(paneOf({ kind: "machine", name: "box" }, "boxer/t4"), "boxer/t4", "another machine's prefix is not stripped");
  assert.equal(paneOf({ kind: "local" }, "box/t4"), "box/t4");
  assert.equal(terminalFinder(terms, { kind: "machine", name: "box" })({ pane: "t4", name: "x" }), "box/t4", "focus gets the address terminals() gave");
  assert.equal(terminalFinder(terms, { kind: "local" })({ pane: "t4", name: "x" }), null);
  assert.equal(terminalFinder(terms)({ pane: "t9", name: "x" }), "t9");
});

// ---- the link (link-model.js) against a stub fetch and a stub runner ----

const REPLY = { epoch: "e1", seq: 3, slices: [] };
// A stub world: `bridge(url, init)` answers fetch; `where` is what `ah where` reports; `lease` what `ahd ui start` does.
function world({ bridge, where = { live: true, port: 4100 }, lease = { code: 0 }, kind = "local", fetchOk = true } = {}) {
  const w = { calls: [], fetches: [], slept: [], clock: 0, where, lease };
  const exec = async (cmd, args) => {
    w.calls.push([cmd, ...args].join(" "));
    if (cmd === "ah" && args[0] === "where") return { code: 0, stdout: JSON.stringify({ binary: {}, ui: w.where }), stderr: "" };
    if (cmd === "ahd") return { stdout: "", stderr: "", ...w.lease, ...(w.lease.code === 0 ? (w.where = { live: true, port: w.where.port ?? 4100 }, {}) : {}) };
    return { code: 0, stdout: JSON.stringify(REPLY), stderr: "" };
  };
  const target = kind === "wsl" ? { kind: "wsl", distro: "Ubuntu" } : { kind: "local" };
  w.link = new Link({
    fetch: fetchOk ? async (url, init) => { w.fetches.push([init?.method ?? "GET", url]); return bridge(url, init); } : undefined,
    runner: () => ({ target, exec }),
    // the 2 s wait passes at once; a request's deadline never comes (the stub always answers first)
    sleep: (ms) => (w.slept.push(ms), ms <= 2000 ? Promise.resolve() : new Promise(() => {})),
    backoff: [2000, 4000, 8000, 16000, 30000],
    now: () => w.clock,
  });
  w.poll = () => w.link.poll({ sub: "view:summary", since: 3, epoch: "e1", wait: "25" }, 35000, { abandoned: 0 }).then((r) => r, (e) => ({ down: e.message }));
  w.execPolls = () => w.calls.filter((c) => c.startsWith("ah view events")).length;
  return w;
}
const reply = (status, body) => ({ status, text: async () => (typeof body === "string" ? body : JSON.stringify(body)) });
const refused = () => { throw new Error("fetching http://127.0.0.1:4100/ failed: error sending request: tcp connect error: Connection refused (os error 61)"); };

test("a good reply comes over the bridge with ah's own query, and no exec poll", async () => {
  const w = world({ bridge: () => reply(200, REPLY) });
  assert.deepEqual(await w.poll(), REPLY);
  assert.deepEqual(w.fetches, [["GET", "http://127.0.0.1:4100/ui/v1/view?path=%2Fevents&since=3&epoch=e1&wait=20&sub=view%3Asummary"]],
    "a long-poll waits 20 s over HTTP, inside herdr's fixed 30 s");
  await w.link.poll({ sub: "view:summary", since: 0, epoch: null, wait: "0" }, 10000, { abandoned: 0 });
  assert.equal(w.fetches.at(-1)[1], "http://127.0.0.1:4100/ui/v1/view?path=%2Fevents&since=0&wait=0&sub=view%3Asummary", "wait=0 stays 0");
  assert.deepEqual(w.calls, ["ah where"]);
  assert.deepEqual(await w.poll(), REPLY);
  assert.deepEqual(w.calls, ["ah where"], "the port is kept: no second discovery");
});

test("refused (WSL): down, then rediscover; refused twice on the port ah where says is live: exec mode", async () => {
  const w = world({ bridge: refused, kind: "wsl" });
  assert.deepEqual(await w.poll(), { down: UNREACHABLE });
  assert.equal(w.link.port, null, "a refusal drops the port");
  const second = await w.poll();
  assert.deepEqual(w.calls.filter((c) => c === "ah where").length, 2, "rediscovered");
  assert.deepEqual(second, REPLY, "the second refusal of the same live port goes to exec, which answers");
  assert.equal(w.link.execReason, "127.0.0.1:4100 not reachable from Windows (WSL localhost forwarding?)");
  // the nearest input that must not reach exec mode: a refusal, then a good reply, then a refusal
  let n = 0;
  const flap = world({ bridge: () => (n++ === 1 ? reply(200, REPLY) : refused()), kind: "wsl" });
  await flap.poll();
  await flap.poll();
  await flap.poll();
  assert.equal(flap.link.execReason, null);
});

test("refused (this computer): never exec mode; back to discovery, and the link comes back with the bridge", async () => {
  let up = false;
  const w = world({ bridge: () => (up ? reply(200, REPLY) : refused()) });
  for (let i = 0; i < 3; i++) assert.deepEqual(await w.poll(), { down: UNREACHABLE });
  assert.equal(w.link.execReason, null, "three refusals: still not exec mode");
  w.where = { live: false, port: 4100 }; // ahd restarted with no bridge
  assert.deepEqual(await w.poll(), REPLY, "no live bridge: the exec poll, as today");
  assert.ok(!w.calls.some((c) => c.startsWith("ahd")), "no lease, no ahd start");
  w.where = { live: true, port: 4100 };
  up = true;
  w.clock += 30000;
  const fetched = w.fetches.length;
  assert.deepEqual(await w.poll(), REPLY);
  assert.equal(w.fetches.length, fetched + 1, "back on the bridge");
});

test("421 and 403 are exec mode with the code; 404 is exec mode, ahd too old", async () => {
  for (const code of [421, 403]) {
    const w = world({ bridge: () => reply(code, { error: code === 421 ? "host" : "origin" }) });
    assert.deepEqual(await w.poll(), REPLY);
    assert.equal(w.link.execReason, `bridge refused this client (${code})`);
    assert.equal(w.execPolls(), 1);
    await w.poll();
    assert.equal(w.fetches.length, 1, "exec mode for the rest of the copy's life");
  }
  const old = world({ bridge: () => reply(404, { error: "not_found" }) });
  await old.poll();
  assert.equal(old.link.execReason, TOO_OLD);
  // the nearest input that must not: ahd's own error reply is down on the same port
  const err = world({ bridge: () => reply(400, { error: "bad_request" }) });
  assert.deepEqual(await err.poll(), { down: "ahd answered 400" });
  assert.equal(err.link.execReason, null);
  assert.equal(err.link.port, 4100);
});

test("a non-ahd reply twice on the same port is exec mode; once is a rediscover", async () => {
  const w = world({ bridge: () => reply(200, "<html>another program</html>") });
  assert.deepEqual(await w.poll(), { down: UNREACHABLE });
  assert.equal(w.link.execReason, null, "once: rediscover");
  assert.deepEqual(await w.poll(), REPLY);
  assert.equal(w.link.execReason, "port 4100 is another program");
  // JSON that is not ahd's (no epoch, no seq) counts too
  const j = world({ bridge: () => reply(200, { ok: true }) });
  await j.poll();
  await j.poll();
  assert.equal(j.link.execReason, "port 4100 is another program");
  // the nearest input that must not: the port moved between the two
  const moved = world({ bridge: () => reply(200, { ok: true }) });
  await moved.poll();
  moved.where = { live: true, port: 4200 };
  await moved.poll();
  assert.equal(moved.link.execReason, null);
});

test("503 closing waits 2 s and rediscovers without going down; a timeout keeps the port", async () => {
  let n = 0;
  const w = world({ bridge: () => (n++ === 0 ? reply(503, { error: "closing" }) : reply(200, REPLY)) });
  assert.deepEqual(await w.poll(), REPLY);
  assert.deepEqual(w.slept.filter((ms) => ms === 2000), [2000]);
  assert.equal(w.calls.filter((c) => c === "ah where").length, 2);
  const slow = world({ bridge: () => { throw new Error("fetching x failed: operation timed out"); } });
  assert.deepEqual(await slow.poll(), { down: NO_ANSWER });
  assert.equal(slow.link.port, 4100, "a timeout retries the same port");
  const late = world({ bridge: () => { late.clock += 30000; throw new Error("fetching x failed: error sending request"); } });
  assert.deepEqual(await late.poll(), { down: NO_ANSWER }, "a rejection after herdr's 30 s is its timeout");
});

test("no live bridge (WSL): the service lease, then ah where again; it failing is one exec poll, paced by the backoff", async () => {
  const w = world({ bridge: () => reply(200, REPLY), where: { live: false, port: 4100 }, kind: "wsl" });
  assert.deepEqual(await w.poll(), REPLY);
  assert.deepEqual(w.calls, ["ah where", "ahd ui start --no-open --json", "ah where"]);
  // this computer never takes the lease (so never starts ahd): no live bridge is today's exec poll
  const mac = world({ bridge: () => reply(200, REPLY), where: { live: false, port: 4100 } });
  assert.deepEqual(await mac.poll(), REPLY);
  assert.deepEqual(mac.calls, ["ah where", "ah view events --sub view:summary --since 3 --wait 25 --epoch e1"]);
  const down = world({ bridge: () => reply(200, REPLY), where: { live: false, port: null }, lease: { code: 4, stderr: "ahd ui: the daemon is not reachable" }, kind: "wsl" });
  await down.poll();
  assert.deepEqual(down.calls, ["ah where", "ahd ui start --no-open --json", "ah view events --sub view:summary --since 3 --wait 25 --epoch e1"]);
  await down.poll();
  assert.equal(down.calls.filter((c) => c === "ah where").length, 1, "no second discovery inside the 2 s step");
  down.clock += 2000;
  await down.poll();
  assert.equal(down.calls.filter((c) => c === "ah where").length, 2);
  const off = world({ bridge: () => reply(200, REPLY), where: { live: false, port: null }, lease: { code: 4, stderr: "ahd ui: browser UI disabled" }, kind: "wsl" });
  await off.poll();
  assert.equal(off.link.execReason, "AH_UI=off");
  const none = world({ bridge: () => reply(200, REPLY), fetchOk: false });
  assert.deepEqual(await none.poll(), REPLY);
  assert.deepEqual(none.calls, ["ah view events --sub view:summary --since 3 --wait 25 --epoch e1"], "no fetch: exec, no discovery");
});

test("the answer posts the web's body once; nothing reached ahd falls back to ah; no reply is never resent", async () => {
  const card = sendCard();
  const w = world({ bridge: (url, init) => (url.endsWith("/ui/v1/answer") ? reply(200, { ok: true, decision: "a" }) : reply(200, REPLY)) });
  await w.poll();
  let ran = 0;
  const exec = async () => (ran++, { code: 0, stdout: '{"ok":true}', stderr: "" });
  const post = (b) => w.link.answer(b);
  assert.equal((await sendAnswer(exec, card, { pick: card.start }, post)).kind, "recorded");
  assert.equal(ran, 0);
  assert.deepEqual(w.fetches.at(-1), ["POST", "http://127.0.0.1:4100/ui/v1/answer"]);
  const gone = world({ bridge: (url) => (url.endsWith("/ui/v1/answer") ? refused() : reply(200, REPLY)) });
  await gone.poll();
  assert.equal((await sendAnswer(exec, card, { pick: card.start }, (b) => gone.link.answer(b))).kind, "recorded");
  assert.equal(ran, 1, "refused: sent through ah instead");
  const mute = world({ bridge: (url) => { if (url.endsWith("/ui/v1/answer")) throw new Error("operation timed out"); return reply(200, REPLY); } });
  await mute.poll();
  assert.deepEqual(await sendAnswer(exec, card, { pick: card.start }, (b) => mute.link.answer(b)), NO_REPLY);
  assert.equal(ran, 1, "no reply: never sent again");
  const reset = world({ bridge: (url) => { if (url.endsWith("/ui/v1/answer")) throw new Error("fetching x failed: connection reset by peer"); return reply(200, REPLY); } });
  await reset.poll();
  assert.deepEqual(await sendAnswer(exec, card, { pick: card.start }, (b) => reset.link.answer(b)), NO_REPLY);
  assert.equal(ran, 1, "a failure that is not a refused connect may have reached ahd: never sent again");
  const big = world({ bridge: (url) => (url.endsWith("/ui/v1/answer") ? reply(413, { error: "too_large" }) : reply(200, REPLY)) });
  await big.poll();
  assert.equal((await sendAnswer(exec, card, { pick: card.start }, (b) => big.link.answer(b))).text, "Could not send: too_large");
  assert.equal(ran, 1, "413 goes through replyState, not retried over exec");
});

test("a History thread and an item's log come over the bridge when it is up, and through ah when it is not (ah2-43)", async () => {
  const thread = { threads: [{ prompt: { id: "p1", text: "why?" }, replies: [] }], dir: "/r/.claude/ah2" };
  const row = { id: "p1", hierDir: "/r/.claude/ah2", dirId: "d1" };
  const sel = { dirId: "d1", team: "t1", key: "s/i", stream: "s", id: "i" };
  let ran = [];
  const exec = async (cmd, args) => (ran.push(args.join(" ")), { code: 0, stdout: JSON.stringify(args[0] === "msg" ? thread : REPLY), stderr: "" });

  const up = world({ bridge: (url) => reply(200, url.includes("/ui/v1/history") ? thread : REPLY) });
  await up.poll();
  const read = (w) => (path, timeout) => w.link.read(path, timeout);
  assert.equal((await readThread(exec, row, null, read(up))).text, "why?");
  assert.deepEqual(up.fetches.at(-1), ["GET", "http://127.0.0.1:4100/ui/v1/history?dir=d1&id=p1"]);
  assert.ok(Array.isArray((await readItemLog(exec, sel, null, read(up))).rows));
  assert.match(up.fetches.at(-1)[1], /\/ui\/v1\/view\?path=%2Fevents&since=0&wait=0&sub=/);
  assert.deepEqual(ran, [], "the bridge is up: no ah process");

  // an older ahd (404 on the new route), a refused connect, and no HTTP client at all: the same reads through ah
  for (const w of [world({ bridge: (url) => (url.includes("/history") ? reply(404, { error: "not_found" }) : reply(200, REPLY)) }),
    world({ bridge: (url) => (url.includes("since=3") ? reply(200, REPLY) : refused()) }), world({ fetchOk: false })]) {
    await w.poll();
    ran = [];
    assert.equal((await readThread(exec, row, null, read(w))).text, "why?");
    assert.deepEqual(ran, ["msg history --json --id p1 --dir /r/.claude/ah2"]);
  }
  ran = [];
  assert.ok(Array.isArray((await readItemLog(exec, sel, null, null)).rows));
  assert.equal(ran.length, 1, "no link yet: the item log is one ah read");

  // ahd took the request and did not answer: the click says so and is not sent again through ah
  const mute = world({ bridge: (url) => { if (url.includes("/history")) throw new Error("operation timed out"); return reply(200, REPLY); } });
  await mute.poll();
  ran = [];
  assert.deepEqual(await readThread(exec, row, null, read(mute)), { error: "Could not read: ah did not answer" });
  assert.deepEqual(ran, []);
});
