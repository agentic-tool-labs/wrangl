// The ah2 settings dialog (atp-1234), props {view:"settings"}, opened from the … menu in the sidebar's tab strip (tabs.js). Settings
// persist in Herdr's shared store (settings-model.js SETTINGS_KEY). Updates: the installed mod's version (line 1 of
// mods/ah2/.ah2-owned) against the plugin's (`ah where`), What's new from the plugin's CHANGELOG.md, and Update/Reinstall, which
// runs `ah herdr install` into this Herdr's home. Colours are theme() tokens only.

import { div, image } from "gpui-kit";
import { v_flex, h_flex, Button, Checkbox, Radio, RadioGroup, TextView } from "gpui-base";
import { readTextFile, appHome, shared, setShared, notify, openOverlay, machines, window } from "herdr";
import { ah, target } from "./ah.js";
import { targetLabel } from "./target-model.js";
import { sidebarModel } from "./model.js";
import { hasDesk } from "./dock.js";
import { SETTINGS_KEY, UPDATE_KEY, BUSY_MS, STRIP, INTERVALS, settingsOf, updateStep, versionNewer, markerVersion, changelogEntry, ABOUT, aboutVersions, updateNotify, clickFree, INSTALL_CLICK_KEY } from "./settings-model.js";
import { REBUILD_KEY } from "./rebuild-model.js";

export const openSettings = () => openOverlay({ mod: "ah2", props: { view: "settings" }, kind: "dialog", key: "ah2-settings", width: 480, height: 640 });
export const settings = () => settingsOf(shared(SETTINGS_KEY));
export const setSetting = (patch, cx) => {
  setShared(SETTINGS_KEY, { ...settings(), ...patch });
  cx?.notify();
};
export const updateState = () => {
  const u = shared(UPDATE_KEY) ?? {};
  const stuck = Boolean(u.failed?.stuck && u.failed.version === u.bundled); // installed, but it didn't take: not offered again
  return { ...u, newer: versionNewer(u.bundled, u.installed), installing: Boolean(u.installing) && Date.now() - u.installing < BUSY_MS, stuck };
};

// ---- the update check and install ----

const GO = { name: "layout.switch", args: { name: "wrangl" } }; // a notice's click: the wrangl layout, where the … menu is

async function probe() {
  const marker = await readTextFile(`${appHome()}/mods/ah2/.ah2-owned`).catch(() => null);
  let root = null;
  let bundled = null;
  let notesFor = null;
  try {
    const r = await ah().exec("ah", ["where"], { timeout: 10000 }); // reads files only: no daemon, no home
    const j = r.code === 0 ? JSON.parse(r.stdout) : null;
    root = j?.plugin?.root ?? null;
    // The binary's version: `ah herdr install` writes that one into the marker (HerdrVerb.cs), so a matching install reads equal.
    bundled = j?.binary?.version ?? null;
    notesFor = j?.plugin?.version ?? bundled;
  } catch {
    // ah not on Herdr's PATH: the bundled version stays unknown
  }
  const md = root ? await ah().read(`${root}/CHANGELOG.md`).catch(() => null) : null;
  return { installed: markerVersion(marker), bundled, notes: changelogEntry(md, notesFor) };
}

function tell(n) {
  const spec = updateNotify(n);
  if (spec) notify(spec);
}

// A click on the "available" notice (main.js hands its nextCommand here): the update path the menu's Update item runs. Two
// copies that both receive the click install once: the first claims INSTALL_CLICK_KEY until its install settles.
export function installClick() {
  if (!clickFree(shared(INSTALL_CLICK_KEY), Date.now())) return null;
  setShared(INSTALL_CLICK_KEY, Date.now());
  return checkUpdate({ install: true }).finally(() => setShared(INSTALL_CLICK_KEY, 0));
}

// `ah herdr install` into this Herdr's home: Update/Reinstall, and with ["--replace-layout"] the Layout row's button. Never throws.
const install = (flags = []) => ah().install(appHome(), { timeout: 60000 }, flags)
  .catch((e) => ({ code: 1, stdout: "", stderr: String(e?.message ?? e) }));

// The Layout row's button: replaces a layout without the Desk column, keeping a backup; a notice says how it went.
export async function replaceLayout() {
  const r = await install(["--replace-layout"]);
  notify(r.code === 0
    ? { title: "Wrangl layout replaced", body: "The old wrangl.json was kept as a backup beside it.", level: "success" }
    : { title: "Wrangl couldn't replace the layout", body: (r.stderr || r.stdout || "the install failed").trim().split("\n").pop(), level: "error" });
}

async function step(o) {
  const p = await probe();
  const s = step0(p);
  const stuck = s.next.failed?.stuck && s.next.failed.version === p.bundled;
  if (!(s.install || (o.install && !stuck && p.installed && p.bundled && (s.newer || o.reinstall)))) return s;
  setShared(UPDATE_KEY, { ...s.next, installing: Date.now(), failed: null });
  // Herdr may reload this mod mid-install; the next check then sees the new marker and says "updated" (updateStep).
  const r = await install();
  if (r.code === 0) {
    const kept = (r.stderr.match(/was kept/g) ?? []).length;
    if (kept) notify({ title: "Wrangl: edited files kept", body: `${kept} ${kept === 1 ? "file" : "files"} in mods/ah2 you edited stayed as they are.`, level: "warning" });
    return step0(await probe(), true);
  }
  const failed = { version: p.bundled, text: (r.stderr || r.stdout || "the install failed").trim().split("\n").pop() };
  setShared(UPDATE_KEY, { ...shared(UPDATE_KEY), installing: 0, failed });
  notify({ title: "Wrangl", body: `Could not update the Wrangl mod: ${(r.stderr || r.stdout || "the install failed").trim().split("\n")[0]}`, level: "error", action: GO });
  return s;
}

function step0(p, landed = false) {
  const s = updateStep(shared(UPDATE_KEY), p, Date.now(), settings().autoUpdate, landed, shared(REBUILD_KEY)?.state === "started");
  setShared(UPDATE_KEY, s.next);
  tell(s.notice);
  return s;
}

let running = null;
let runningAt = 0;
const CHECK_MS = 180000; // past the install's 60 s and the probes; a check older than this is taken as never settling
// A check, and an install when asked ({install}, {install, reinstall}) or when auto-update is on. One at a time per mod copy.
export function checkUpdate(o = {}) {
  if (running && Date.now() - runningAt < CHECK_MS) return o.install ? running.then(() => checkUpdate(o)) : running;
  runningAt = Date.now();
  const mine = step(o).catch(() => null).finally(() => { if (running === mine) running = null; });
  running = mine;
  return mine;
}

// ---- the dialog ----

// The "ah runs on" choices: auto, this computer, each named herdr machine, WSL's default distro on Windows, and a stored value not
// otherwise listed (a machine gone, a named distro) so it stays visible.
function ahOnChoices(current) {
  const opts = [["auto", "Auto"], ["local", "This computer"],
    ...machines().map((m) => m.name).filter(Boolean).map((n) => [`machine:${n}`, n])];
  if (window()?.platform === "windows") opts.push(["wsl:", "WSL"]);
  if (!opts.some(([v]) => v === current)) opts.push([current, current.startsWith("wsl:") ? `WSL ${current.slice(4)}` : current.replace(/^machine:/, "")]);
  return opts;
}

export function settingsView(self, t, close) {
  const s = settings();
  const u = updateState();
  const m = sidebarModel(self.st.slices, Date.now());

  const section = (label, ...rows) => v_flex().mt(16).flex_none()
    .child(h_flex().h(20).items_center().text_size(11).font_semibold().text_color(t.muted).child(label))
    .children(rows);
  const row = (label, control, hint) => v_flex().flex_none().border_b(1).border_color(t.border)
    .child(h_flex().min_h(36).gap(12).items_center()
      .child(div().flex_1().min_w(0).truncate().text_color(t.foreground).child(label))
      .child(control))
    .when(Boolean(hint), (el) => el.child(div().pb(8).text_size(11).text_color(t.muted).child(hint)));

  // An on/off switch: a 32x18 track, accent when on; 24 px tall with its focus ring. A Checkbox announcing itself as a switch,
  // since herdr's Switch drops focus styles.
  const toggle = (id, on, label, set) => Checkbox.new(id).role("switch").checked(on)
    .h(24).w(38).flex().flex_none().items_center().justify_center().rounded(12).cursor_pointer()
    .border(1).border_color(t.surface).focus((el) => el.border_color(t.accent))
    .accessibility_label(label)
    .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
    .on_change((v, cx) => set(v, cx))
    .child(h_flex().w(32).h(18).px(2).rounded(9).items_center().bg(on ? t.accent : t.input).when(on, (el) => el.justify_end())
      .child(div().size(14).rounded(7).bg(on ? t.accent_foreground : t.foreground)));

  // One of a few: joined radios, the chosen one raised.
  const seg = (id, label, opts, value, set) => RadioGroup.new(id).axis("horizontal").accessibility_label(label)
    .flex().flex_none().p(2).gap(2).rounded(8).border(1).border_color(t.border).bg(t.chrome)
    .children(opts.map(([v, text], i) => {
      const on = v === value;
      return Radio.new(`${id}-${v}`).checked(on).set_position(i + 1, opts.length)
        .h(24).px(10).flex().items_center().rounded(6).cursor_pointer().text_size(12)
        .border(1).border_color(on ? t.border : t.chrome).bg(on ? t.raised : t.chrome).text_color(on ? t.foreground : t.muted)
        .hover((el) => el.text_color(t.foreground))
        .focus((el) => el.border_color(t.accent))
        .accessibility_label(text)
        .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
        .on_change((_c, cx) => set(v, cx))
        .child(text);
    }));

  const button = (id, label, onClick, primary = false) => self.ring(t, Button.new(id), t.surface)
    .h(28).px(12).flex().flex_none().items_center().rounded(7).cursor_pointer().text_size(12).font_medium()
    .when(primary, (el) => el.bg(t.accent).text_color(t.accent_foreground).hover((x) => x.opacity(0.88)))
    .when(!primary, (el) => el.bg(t.raised).text_color(t.foreground).hover((x) => x.bg(t.hover)))
    .accessibility_label(label)
    .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
    .on_click(onClick)
    .child(label);

  // ---- Teams: every running team, lanes under their parent, then hidden teams that are no longer running ----
  const hidden = new Set(s.hidden);
  const teamRows = [];
  const teamRow = (tm, lane) => {
    const shown = !hidden.has(tm.id);
    teamRows.push(row(
      h_flex().gap(6).min_w(0).when(lane, (el) => el.pl(14).child(div().flex_none().text_color(t.dim).child("↳"))).child(div().min_w(0).truncate().child(tm.name)),
      toggle(`set-team-${tm.id}`, shown, `Show ${tm.name} in the sidebar`, (v, cx) => setSetting({ hidden: v ? s.hidden.filter((x) => x !== tm.id) : [...s.hidden, tm.id] }, cx)),
    ));
    tm.lanes.forEach((l) => teamRow(l, true));
  };
  m.teams.forEach((tm) => teamRow(tm, false));
  const running = new Set();
  const seen = (list) => list.forEach((tm) => { running.add(tm.id); seen(tm.lanes); });
  seen(m.teams);
  for (const id of s.hidden.filter((x) => !running.has(x))) teamRow({ id, name: `${id.split("/").pop()} (not running)`, lanes: [] }, false);
  if (teamRows.length === 0) teamRows.push(div().py(8).text_size(11).text_color(t.muted).child(self.status === "loading" ? "Loading teams…" : "No teams running."));

  // ---- Updates ----
  const busy = self.updating ?? (u.installing ? "install" : null);
  const stuck = u.stuck;
  const status = busy === "install" ? `Installing v${u.bundled}…`
    : stuck && u.newer ? `v${u.bundled} didn't take: ${u.failed.text}. Rebuild ah from the plugin.`
    : u.failed && u.newer ? `Couldn't update to v${u.failed.version}: ${u.failed.text}`
    : !u.checked ? "Not checked yet"
    : !u.installed ? "Not installed by ah herdr install, so it can't update itself"
    : !u.bundled ? "The plugin's version is unknown (is ah on Herdr's PATH?)"
    : u.newer ? `v${u.bundled} is ready to install`
    : "Up to date";
  const act = (o, what) => (_e, cx) => {
    if (self.updating) return;
    self.updating = what;
    cx.notify();
    cx.spawn(async (cx) => {
      await Promise.race([checkUpdate(o), cx.sleep(CHECK_MS)]); // the button comes back even if the check never settles
      self.updating = null;
      cx.notify();
    });
  };
  const canInstall = Boolean(u.installed && u.bundled) && !busy && !stuck;
  const notes = u.notes ?? "";

  const updates = section("Updates",
    row("Installed", div().flex_none().font_family("Geist Mono").text_size(12).text_color(t.muted).child(u.installed ? `v${u.installed}` : "—")),
    row("In the Wrangl plugin", div().flex_none().font_family("Geist Mono").text_size(12).text_color(t.muted).child(u.bundled ? `v${u.bundled}` : "—")),
    h_flex().id("set-update-status").min_h(36).gap(8).items_center().border_b(1).border_color(t.border).tooltip(status)
      .when(u.newer && !u.failed, (el) => el.child(div().size(8).flex_none().rounded(4).bg(t.accent)))
      .when(Boolean(u.failed && u.newer), (el) => el.child(div().flex_none().text_color(t.blocked).child("⚠")))
      .child(div().flex_1().min_w(0).truncate().text_color(t.foreground).child(status)),
    row("Auto-update", toggle("set-autoupdate", s.autoUpdate, "Auto-update", (v, cx) => setSetting({ autoUpdate: v }, cx)),
      "Installs a newer plugin's mod as soon as a check finds it. A check runs when the Wrangl layout opens, then hourly."),
    h_flex().mt(12).gap(8).items_center().flex_none()
      .when(canInstall && u.newer, (el) => el.child(button("set-update", `Update to v${u.bundled}`, act({ install: true }, "install"), true)))
      .when(canInstall && !u.newer, (el) => el.child(button("set-reinstall", busy === "install" ? "Reinstalling…" : "Reinstall", act({ install: true, reinstall: true }, "install"))))
      .child(button("set-check", busy === "check" ? "Checking…" : "Check now", act({}, "check")))
      .child(div().flex_1())
      .when(Boolean(notes), (el) => el.child(self.ring(t, Button.new("set-notes"), t.surface)
        .h(28).px(8).flex().flex_none().items_center().rounded(7).cursor_pointer().text_size(12).text_color(t.muted)
        .hover((x) => x.bg(t.hover).text_color(t.foreground))
        .accessibility_label(self.notesOpen ? "Hide what's new" : `What's new in v${u.bundled}`)
        .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
        .on_click((_e, cx) => { self.notesOpen = !self.notesOpen; cx.notify(); })
        .child(self.notesOpen ? "Hide what's new" : "What's new"))),
    ...(self.notesOpen && notes ? [v_flex().id("set-notes-text").mt(8).max_h(220).px(12).py(8).rounded(8).bg(t.raised).overflow_y_scrollbar()
      .child(TextView.markdown("set-notes-md", notes).selectable(true))] : []),
    div().mt(8).text_size(11).text_color(t.muted)
      .child("Updates come from the Wrangl plugin on this machine. Update and Reinstall run ah herdr install, which keeps any mod file you edited."));

  // ---- About: the mascot, what this is and the versions the update check already read (aboutLines); no call of its own ----
  const about = section("About",
    h_flex().mt(8).gap(14).items_center().flex_none()
      .child(image("mods/ah2/wrangl-color.svg").size(64).flex_none())
      .child(v_flex().flex_1().min_w(0).gap(3)
        .child(div().text_size(14).font_semibold().text_color(t.foreground).child(ABOUT.title))
        .child(div().text_color(t.muted).child(ABOUT.what))
        .child(div().font_family("Geist Mono").text_size(12).text_color(t.muted).child(aboutVersions(u)))
        .child(TextView.markdown("set-about-url", ABOUT.url).selectable(true))));

  // ---- Layout: a wrangl.json without the Desk column (kept by ah herdr install as user-edited) can be replaced; a backup stays ----
  const replace = (_e, cx) => {
    if (self.replacing) return;
    self.replacing = true;
    cx.notify();
    cx.spawn(async (cx) => {
      await replaceLayout();
      self.replacing = false;
      cx.notify();
    });
  };

  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(self.keys)
    .on_key_down((e, _cx) => {
      if (e.keystroke === "escape") close();
    })
    .child(h_flex().h(44).px(16).gap(10).flex_none().items_center().border_b(1).border_color(t.border)
      .child(div().flex_1().min_w(0).truncate().text_size(13).font_semibold().text_color(t.foreground).child("Wrangl settings"))
      .child(self.closeButton(t, "ah2-settings-close", "Close Wrangl settings", close)))
    .child(v_flex().id("set-body").flex_1().min_h(0).px(16).pb(16).overflow_y_scrollbar().on_mouse_down("left", (_e, cx) => cx.stop_propagation())
      .when(!hasDesk(), (el) => el.child(section("Layout",
        row("This layout has no Desk column",
          button("set-replace-layout", self.replacing ? "Replacing…" : "Replace layout (keeps a backup)", replace)))))
      .child(section("Sidebar",
        row("Opens on", seg("set-tab", "Tab shown when the Wrangl layout opens", [["agents-list", "Agents"], ["ah2", "Wrangl"]], s.tab, (v, cx) => setSetting({ tab: v }, cx)),
          "The tab shown each time you switch to the Wrangl layout.")))
      .child(section("Tab strip",
        ...STRIP.map(([k, label]) => row(label, toggle(`set-strip-${k}`, s.strip[k], `Show ${label} in the tab strip`, (v, cx) => setSetting({ strip: { ...s.strip, [k]: v } }, cx))))))
      .child(section("Teams and lanes",
        row("Lanes", seg("set-lanes", "Lanes", [["nested", "Nested"], ["collapsed", "Collapsed"]], s.lanes, (v, cx) => setSetting({ lanes: v }, cx)),
          "Collapsed keeps a lane's header row and hides its members."),
        div().mt(12).h(20).flex().items_center().text_size(11).text_color(t.muted).child("Shown in the sidebar"),
        ...teamRows,
        div().mt(4).text_size(11).text_color(t.muted).child("A hidden team's questions still count in Needs you and Q&A.")))
      .child(section("Refresh and alerts",
        row("Refresh", seg("set-interval", "Refresh", INTERVALS, s.interval, (v, cx) => setSetting({ interval: v }, cx)),
          "Live updates as soon as anything changes; the others refresh at most that often."),
        row("Alarms as herdr notices", toggle("set-notify", s.notify, "Alarms as herdr notices", (v, cx) => setSetting({ notify: v }, cx)),
          "When a team raises an alarm, also post a herdr notice."),
        row("Auto-update Q&A", toggle("set-auto-qa", s.autoQa, "Auto-update Q&A", (v, cx) => setSetting({ autoQa: v }, cx)),
          "Off: Q&A keeps what you are reading and offers Updates available. On: new questions replace the list as they arrive.")))
      .child(section("Where ah runs",
        row("ah runs on", seg("set-ah-on", "Where ah runs", ahOnChoices(s.ahOn), s.ahOn, (v, cx) => setSetting({ ahOn: v }, cx)),
          `Now: ${targetLabel(target())}. Auto is this computer, except on Windows: the one herdr machine, else WSL. Windows with ah in WSL2 is untested.`),
        row("Link", div().flex_none().text_size(12).text_color(t.muted).child(self.link?.execReason ? `Polling through ah (bridge: ${self.link.execReason})`
          : self.link?.port ? `ahd's bridge, 127.0.0.1:${self.link.port}` : "Polling through ah"))))
      .child(updates)
      .child(about));
}
