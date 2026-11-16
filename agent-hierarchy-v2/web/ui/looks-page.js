import { html, Component } from "./h.js";
import { sheet } from "./lazy.js";
import { kbCard, kbHead } from "./board-kanban.js";
import { Footer, Header } from "./shell.js";
import {
  ACCENT_IDS, BD_RANGES, BUILTINS, CARD_RANGES, HOUSE, MARK_TOKENS, PATTERNS, PATTERN_NAMES, RANGES, ROLES, ROLE_FONTS, ROLE_SIZE, TEXT_TOKENS,
  apply, boxOf, contrastOf, copyOf, exportLook, importLooks, lookFor, nameError, onLooks, profileOf, save, stateOf, themeOf, withoutLook,
} from "./looks.js";

// The Looks page (ah-ui-looks §6): pick a look, copy a built-in, tweak its controls on a live preview of real components, save it
// under a name. Every look formula lives in looks.js; this page only edits Controls and calls its one apply() on the preview
// container, never on the app root.

const ACCENT_NAMES = { obsidian: "Obsidian", cyan: "Cyan", amber: "Amber", emerald: "Emerald" };
const THEME_NAMES = { light: "Light", dark: "Dark" };

// [key, label, unit] for the lab's seven controls; the ranges are looks.js's.
const HALO = [["sliderHaloBlur", "Halo blur", "px"], ["sliderHaloIntensity", "Halo intensity", "%"]];
const GLASS = [["sliderGlassOpacity", "Glass opacity", "%"], ["glassTexture", "Frost texture", "%"], ["sliderGlassBlur", "Glass blur", "px"]];
// [token, label] for the look's text colours; contrastOf gives each its value and its worst ratio.
const INKS = [["--ink", "Body"], ["--ink-2", "Secondary"], ["--muted", "Muted"], ["--dim", "Dim"], ["--ink-strong", "Headings and strong text"], ["--accent-ink", "Accent"]];
const toHex = (rgb) => `#${rgb.map((c) => Math.round(c).toString(16).padStart(2, "0")).join("")}`;
const BD_UNIT = { baseAngle: "°", patternSize: "px" };
// What each profile chip means: a custom look draws with the profile it saves; a built-in always draws Dual, and Copy carries the chip.
const PROFILE_WHAT = { shadow: "Headings get one soft glow.", dual: "Headings get a blurred copy of their letters behind them." };
const PROFILE_SAVE = "Save with this selected and the app draws this look that way.";
const PROFILE_BUILTIN = { shadow: "Built-in looks draw with Dual, so copy this one to use Shadow.", dual: "Built-in looks always draw this way." };
const GRAPH_NOTE = "The Graph page draws with this look's Shadow profile; everything else stays Dual.";

// A computed colour as [r, g, b]: Chrome gives rgb()/rgba(), or color(srgb …) for a color-mix().
const rgbOfComputed = (s) => {
  const n = (s.match(/-?[\d.]+/g) ?? []).map(Number);
  return s.startsWith("color(") ? n.slice(0, 3).map((v) => v * 255) : n.slice(0, 3);
};

// What the preview's real Board draws: sample data through the Board's own card and head, between the app's own Header and Footer. The look
// reaches it through apply() and the same rules as the app; nothing here or in looks-page.css styles it apart from its size.
const SAMPLE_COLS = [
  { key: "progress", label: "In progress", cards: [
    { id: "p1", title: "Look editor page", where: { kind: "team", name: "ship", tip: "Team ship" }, owner: "implementor-1", size: "M", msgs: 3, ref: "b125" },
    { id: "p2", title: "Contrast warnings stay honest on every surface", where: { kind: "team", name: "ship", tip: "Team ship" }, owner: "ux-engineer", flag: { cls: "attn", text: "⚠ waiting on you", tip: "Waiting on you" } },
  ] },
  { key: "review", label: "In review", cards: [
    { id: "r1", title: "Board slab settings", where: { kind: "stream", name: "ui-looks", tip: "Stream ui-looks" }, owner: "reviewer", size: "S", ref: "b103" },
  ] },
];
const noop = () => {};

const fileName = (name) => `${name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "look"}.json`;

export class LooksPage extends Component {
  constructor(props) {
    super(props);
    const optics = stateOf();
    this.state = {
      optics, selId: lookFor(optics, themeOf()).id, draft: null, tab: null, tokens: null, role: ROLES[0].id,
      nameDraft: null, nameErr: null, confirmDelete: false, pending: null, ioMsg: null, ioErr: null,
    };
  }

  componentDidMount() {
    this.offLooks = onLooks(() => this.setState({ optics: stateOf() }));
    this.props.store.setLeaveGuard((patch) => {
      if (!this.state.draft) return false;
      this.setState({ pending: { patch } });
      return true;
    });
    // The theme tokens, read once from two hidden theme scopes (one style flush, then reads only): the contrast check's text colours.
    const read = (el) => Object.fromEntries([...el.children].map((p) => [p.dataset.token, rgbOfComputed(getComputedStyle(p).color)]));
    this.setState({ tokens: { light: read(this.probeLight), dark: read(this.probeDark) } });
    // The preview's components keep their own sheets (the member rail's and the Board's); the frame draws once they are in, inside a
    // fixed-height container, so nothing shifts.
    Promise.all([sheet("/ui/r6-conversation.css", "/ui/r6-plans.css"), sheet("/ui/r6-plans-view.css")])
      .then(() => !this.gone && this.setState({ sheets: true }), () => !this.gone && this.setState({ sheets: true }));
    this.paint();
  }

  componentDidUpdate() {
    this.paint();
  }

  componentWillUnmount() {
    this.gone = true;
    this.offLooks();
    this.props.store.setLeaveGuard(null);
  }

  look() {
    const { draft, selId, optics } = this.state;
    return draft ?? [...BUILTINS, ...optics.custom].find((l) => l.id === selId) ?? HOUSE;
  }

  // The chip that is on, which the preview draws: what the app draws for this look, unless a built-in's chip is browsing the other profile.
  tab(look = this.look()) {
    return (look.builtin && this.state.tab) || profileOf(look);
  }

  pickTab = (t) => {
    this.setState({ tab: t });
    if (profileOf(this.look()) !== t) this.edit((d) => ({ ...d, profile: t }));
  };

  paint() {
    if (this.pv) apply(this.look(), this.tab(), this.pv);
  }

  // Leave the page or switch look: ask first while a draft is unsaved.
  guarded(action) {
    if (this.state.draft) this.setState({ pending: { action } });
    else action();
  }

  discard = () => {
    const { pending } = this.state;
    this.setState({ draft: null, pending: null, nameDraft: null, nameErr: null, confirmDelete: false });
    if (pending?.patch) {
      this.props.store.setLeaveGuard(null);
      this.props.store.setUi(pending.patch);
    } else pending?.action?.();
  };

  stay = () => {
    this.setState({ pending: null });
    if (globalThis.location && globalThis.location.hash !== "#/looks") globalThis.history.replaceState(globalThis.history.state, "", "#/looks");
  };

  select = (id) => this.guarded(() => this.setState({ selId: id, tab: null, draft: null, nameDraft: null, nameErr: null, confirmDelete: false, ioMsg: null, ioErr: null }));

  copy = () => {
    const make = () => {
      const draft = { ...copyOf(this.look(), this.state.optics), profile: this.tab() };
      this.setState({ draft, selId: draft.id, nameDraft: null, nameErr: null, confirmDelete: false, ioMsg: null, ioErr: null });
    };
    this.guarded(make);
  };

  // An edit to the selected custom look starts (or continues) its draft.
  edit(change) {
    const look = this.look();
    if (look.builtin) return;
    this.setState({ draft: change(this.state.draft ?? structuredClone(look)) });
  }

  setControl = (key, value) => this.edit((d) => ({ ...d, profiles: { ...d.profiles, [this.tab()]: { ...d.profiles[this.tab()], [key]: value } } }));

  setBackdrop = (key, value) => this.edit((d) => ({ ...d, backdrop: { ...d.backdrop, [key]: value } }));

  setInk = (token, value) => this.edit((d) => ({ ...d, ink: { ...d.ink, [token]: value } }));

  // One knob of one role; null takes it back to the role's own value.
  setRole = (role, key, value) => this.edit((d) => {
    const { [key]: _old, ...rest } = d.type?.[role] ?? {};
    const knobs = value === null ? rest : { ...rest, [key]: value };
    const { [role]: _was, ...others } = d.type ?? {};
    const type = Object.keys(knobs).length > 0 ? { ...others, [role]: knobs } : others;
    const { type: _t, ...look } = d;
    return Object.keys(type).length > 0 ? { ...look, type } : look;
  });

  commit(optics, extra = {}) {
    this.setState({ optics: save(optics), ...extra });
  }

  save = () => {
    const { draft, optics } = this.state;
    if (!draft) return;
    const has = optics.custom.some((l) => l.id === draft.id);
    this.commit({ ...optics, custom: has ? optics.custom.map((l) => (l.id === draft.id ? draft : l)) : [...optics.custom, draft] }, { draft: null, ioMsg: null });
  };

  use = () => {
    const look = this.look();
    this.commit({ ...this.state.optics, active: { ...this.state.optics.active, [look.theme]: look.id } });
  };

  rename = (e) => {
    e.preventDefault();
    const look = this.look();
    const name = this.state.nameDraft ?? look.name;
    const err = nameError(name, look.id, this.state.optics);
    if (err) {
      this.setState({ nameErr: err });
      return;
    }
    const t = name.trim();
    const { optics, draft } = this.state;
    const saved = optics.custom.some((l) => l.id === look.id);
    this.setState({ nameDraft: null, nameErr: null, ...(draft ? { draft: { ...draft, name: t } } : null) });
    if (saved) this.commit({ ...optics, custom: optics.custom.map((l) => (l.id === look.id ? { ...l, name: t } : l)) });
  };

  remove = () => {
    const look = this.look();
    const optics = withoutLook(this.state.optics, look.id);
    this.commit(optics, { draft: null, confirmDelete: false, selId: lookFor(optics, look.theme).id });
  };

  exportFile = () => {
    const look = this.look();
    const url = URL.createObjectURL(new Blob([JSON.stringify(exportLook(look, this.tab()), null, 2)], { type: "application/json" }));
    const a = Object.assign(document.createElement("a"), { href: url, download: fileName(look.name) });
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  };

  importFile = async (e) => {
    const file = e.currentTarget.files?.[0];
    e.currentTarget.value = "";
    if (!file) return;
    const out = importLooks(await file.text(), this.state.optics);
    if (out.error) {
      this.setState({ ioErr: out.error, ioMsg: null });
      return;
    }
    this.guarded(() => this.commit(out.state, { draft: null, selId: out.looks[0].id, ioErr: null, ioMsg: `Imported ${out.looks.length} look${out.looks.length === 1 ? "" : "s"}.` }));
  };

  // Every text and mark pair of a look's profile on its panes; [] until the tokens are read.
  contrasts(look, technique) {
    const t = this.state.tokens?.[look.theme];
    if (!t || !look.profiles) return [];
    const pick = (names) => Object.fromEntries(names.filter((n) => t[n]).map((n) => [n, t[n]]));
    return contrastOf(look, technique, pick(TEXT_TOKENS), pick(MARK_TOKENS));
  }

  failing(look, technique) {
    return this.contrasts(look, technique).filter((r) => !r.ok);
  }

  warns(look) {
    return !look.builtin && (this.failing(look, "shadow").length > 0 || this.failing(look, "dual").length > 0);
  }

  row(l, theme) {
    const { optics, draft, selId } = this.state;
    const shown = draft?.id === l.id ? draft : l;
    const inUse = (l.theme === null ? [theme] : [l.theme]).some((th) => lookFor(optics, th).id === l.id);
    const warn = this.warns(shown);
    return html`<li key=${`${theme}-${l.id}`}>
      <button class="looks-item" aria-current=${selId === l.id && (l.theme !== null || theme === themeOf()) ? "true" : undefined} onClick=${() => this.select(l.id)}>
        <span class="looks-item-name" title=${shown.name}>${shown.name}</span>
        ${draft?.id === l.id ? html`<span class="looks-tag">unsaved</span>` : null}
        ${inUse ? html`<span class="looks-tag">in use</span>` : null}
        ${warn ? html`<span class="looks-warn" title="Some text is below its contrast target"><span aria-hidden="true">⚠</span><span class="sr">, low contrast</span></span>` : null}
      </button>
    </li>`;
  }

  slider(key, label, unit, value, range, onInput) {
    const id = `lk-${key}`;
    return html`<div class="looks-field" key=${key}>
      <label for=${id}>${label}</label>
      <output for=${id} class="looks-val">${value}${unit}</output>
      <input id=${id} type="range" min=${range[0]} max=${range[1]} step=${range[2]} value=${value} onInput=${(e) => onInput(key, Number(e.currentTarget.value))} />
    </div>`;
  }

  colour(key, label, value, onInput) {
    const id = `lk-${key}`;
    return html`<div class="looks-field looks-colour" key=${key}>
      <label for=${id}>${label}</label>
      <input id=${id} type="color" value=${value} onInput=${(e) => onInput(key, e.currentTarget.value.toLowerCase())} />
      <output for=${id} class="looks-val mono">${value}</output>
    </div>`;
  }

  // One text colour, with its worst ratio on the panes: a warning under 4.5:1, never a block.
  ink(token, label, row) {
    const id = `lk-ink${token}`;
    const value = toHex(row.rgb);
    const ratio = (Math.floor(row.worst * 10) / 10).toFixed(1);
    return html`<div class="looks-field looks-colour looks-ink" key=${token}>
      <label for=${id}>${label}</label>
      <input id=${id} type="color" value=${value} onInput=${(e) => this.setInk(token, e.currentTarget.value.toLowerCase())} />
      <output for=${id} class="looks-val mono">${value}</output>
      <span class=${row.ok ? "looks-val looks-ratio" : "looks-val looks-ratio warn"}>${row.ok ? null : html`<span aria-hidden="true">⚠ </span>`}${ratio}:1${row.ok ? null : html`<span class="sr">, below ${row.need}:1</span>`}</span>
    </div>`;
  }

  // The picked role's four knobs. Each has its own reset; the preview shows them as they change.
  language(look) {
    const role = ROLES.find((r) => r.id === this.state.role);
    const set = look.type?.[role.id] ?? {};
    const reset = (key, label) => html`<button type="button" class="looks-reset" disabled=${set[key] === undefined} aria-label=${`Reset ${role.name} ${label}`} onClick=${() => this.setRole(role.id, key, null)}>Reset</button>`;
    const size = set.size ?? role.size;
    return html`<fieldset class="looks-language" disabled=${look.builtin}>
      <legend>UI language <span class="hint">both profiles</span></legend>
      <div class="looks-field">
        <label for="lk-role">Style</label>
        <select id="lk-role" value=${role.id} onInput=${(e) => this.setState({ role: e.currentTarget.value })}>
          ${ROLES.map((r) => html`<option key=${r.id} value=${r.id}>${look.type?.[r.id] ? `${r.name} (changed)` : r.name}</option>`)}
        </select>
      </div>
      <p class="looks-profile-note">${role.where}.</p>
      <div class="looks-field looks-knob">
        <label for="lk-role-font">Font</label>
        <select id="lk-role-font" value=${set.font ?? role.font} onInput=${(e) => this.setRole(role.id, "font", e.currentTarget.value)}>
          ${Object.entries(ROLE_FONTS).map(([id, [name]]) => html`<option key=${id} value=${id}>${name}</option>`)}
        </select>
        ${reset("font", "font")}
      </div>
      <div class="looks-field looks-check looks-knob">
        <input id="lk-role-bold" type="checkbox" checked=${set.bold ?? role.bold} onInput=${(e) => this.setRole(role.id, "bold", e.currentTarget.checked)} />
        <label for="lk-role-bold">Bold</label>
        ${reset("bold", "bold")}
      </div>
      <div class="looks-field looks-check looks-knob">
        <input id="lk-role-italic" type="checkbox" checked=${set.italic ?? role.italic} onInput=${(e) => this.setRole(role.id, "italic", e.currentTarget.checked)} />
        <label for="lk-role-italic">Italics</label>
        ${reset("italic", "italics")}
      </div>
      <div class="looks-field looks-knob looks-size">
        <label for="lk-role-size">Size</label>
        <output for="lk-role-size" class="looks-val">${size}px</output>
        ${reset("size", "size")}
        <input id="lk-role-size" type="range" min=${ROLE_SIZE[0]} max=${ROLE_SIZE[1]} step=${ROLE_SIZE[2]} value=${size} onInput=${(e) => this.setRole(role.id, "size", Number(e.currentTarget.value))} />
      </div>
    </fieldset>`;
  }

  controls(look) {
    const tab = this.tab(look);
    const c = look.profiles[tab];
    const b = look.backdrop;
    const rows = this.contrasts(look, tab);
    const fails = rows.filter((r) => !r.ok);
    return html`<form class="looks-controls" onSubmit=${(e) => e.preventDefault()}>
      <div class="looks-tabs" role="group" aria-label="Profile to edit" aria-describedby="lk-profile-note">
        ${["shadow", "dual"].map((t) => html`<button type="button" key=${t} class="chip" aria-pressed=${tab === t} onClick=${() => this.pickTab(t)}>${t === "shadow" ? "Shadow profile" : "Dual profile"}</button>`)}
      </div>
      <p id="lk-profile-note" class="looks-profile-note" aria-live="polite">${PROFILE_WHAT[tab]} ${look.builtin ? PROFILE_BUILTIN[tab] : PROFILE_SAVE}</p>
      ${tab === "dual" ? html`<div class="looks-field looks-check looks-graph">
        <input id="lk-graph" type="checkbox" disabled=${look.builtin} checked=${look.graphShadow === true} aria-describedby="lk-graph-note" onInput=${(e) => { const on = e.currentTarget.checked; this.edit((d) => ({ ...d, graphShadow: on })); }} />
        <label for="lk-graph">Draw the graph with Shadow</label>
        <p id="lk-graph-note" class="looks-profile-note">${GRAPH_NOTE}</p>
      </div>` : null}
      <p class=${fails.length ? "looks-contrast warn" : "looks-contrast"}>
        ${fails.length
          ? html`<span aria-hidden="true">⚠ </span>${fails.map((f, i) => html`${i ? "; " : ""}<code>${f.token}</code> <span class="num">${(Math.floor(f.worst * 10) / 10).toFixed(1)}:1</span> on ${f.where} (needs ${f.need})`)}`
          : this.state.tokens ? "All text and marks meet their contrast targets on cards and menus." : ""}
        ${this.state.tokens ? html` <span class="num">${rows.find((r) => r.token === "--ink")?.slab?.toFixed(1)}:1</span> for body text directly on the glass.` : ""}
      </p>
      ${this.language(look)}
      ${this.state.tokens ? html`<fieldset disabled=${look.builtin}>
        <legend>Text <span class="hint">both profiles</span></legend>
        ${INKS.map(([t, label]) => this.ink(t, label, rows.find((r) => r.token === t)))}
      </fieldset>` : null}
      <fieldset disabled=${look.builtin}>
        <legend>Halo</legend>
        ${HALO.map(([k, label, unit]) => this.slider(k, label, unit, c[k], RANGES[k], this.setControl))}
        <div class="looks-field">
          <label for="lk-accent">Tint</label>
          <select id="lk-accent" value=${c.accent} onInput=${(e) => this.setControl("accent", e.currentTarget.value)}>
            ${ACCENT_IDS.map((a) => html`<option key=${a} value=${a}>${ACCENT_NAMES[a]}</option>`)}
          </select>
        </div>
      </fieldset>
      <fieldset disabled=${look.builtin}>
        <legend>Glass</legend>
        ${this.colour("glassColor", "Glass colour", c.glassColor, this.setControl)}
        ${GLASS.map(([k, label, unit]) => this.slider(k, label, unit, c[k], RANGES[k], this.setControl))}
        ${this.colour("boxColor", "Card colour", toHex(boxOf(look, c)), this.setControl)}
        ${this.slider("sliderBoxOpacity", "Card fill", "%", Math.round(boxOf(look, c)[3] * 100), CARD_RANGES.sliderBoxOpacity, this.setControl)}
        ${this.slider("sliderBoxBorder", "Card border", "%", c.sliderBoxBorder ?? 50, CARD_RANGES.sliderBoxBorder, this.setControl)}
        ${this.slider("sliderLineStrength", "Lane line strength", "%", c.sliderLineStrength ?? 100, CARD_RANGES.sliderLineStrength, this.setControl)}
      </fieldset>
      ${b ? html`<fieldset disabled=${look.builtin}>
        <legend>Backdrop <span class="hint">both profiles</span></legend>
        <div class="looks-field">
          <label for="lk-pattern">Pattern</label>
          <select id="lk-pattern" value=${b.pattern} onInput=${(e) => this.setBackdrop("pattern", e.currentTarget.value)}>
            ${PATTERNS.map((p) => html`<option key=${p} value=${p}>${PATTERN_NAMES[p]}</option>`)}
          </select>
        </div>
        ${this.colour("patternColor", "Pattern colour", b.patternColor, this.setBackdrop)}
        ${this.slider("patternOpacity", "Pattern opacity", "%", b.patternOpacity, BD_RANGES.patternOpacity, this.setBackdrop)}
        ${b.pattern === "noise" ? null : this.slider("patternSize", "Pattern size", "px", b.patternSize, BD_RANGES.patternSize, this.setBackdrop)}
        ${this.colour("baseFrom", "Base from", b.baseFrom, this.setBackdrop)}
        ${this.colour("baseTo", "Base to", b.baseTo, this.setBackdrop)}
        ${this.slider("baseAngle", "Base angle", BD_UNIT.baseAngle, b.baseAngle, BD_RANGES.baseAngle, this.setBackdrop)}
        ${["washA", "washB", "washC"].map((k, i) => this.colour(k, `Wash ${i + 1}`, b[k], this.setBackdrop))}
        ${this.slider("washIntensity", "Wash intensity", "%", b.washIntensity, BD_RANGES.washIntensity, this.setBackdrop)}
        <div class="looks-field looks-check">
          <input id="lk-glow" type="checkbox" checked=${b.glow} onInput=${(e) => this.setBackdrop("glow", e.currentTarget.checked)} />
          <label for="lk-glow">Accent glow (the orbs)</label>
        </div>
        ${["glowA", "glowB", "glowC"].map((k, i) => this.colour(k, `Orb ${i + 1}`, b[k], this.setBackdrop))}
        ${this.slider("glowIntensity", "Glow intensity", "%", b.glowIntensity, BD_RANGES.glowIntensity, this.setBackdrop)}
      </fieldset>` : null}
    </form>`;
  }

  head(look) {
    const { draft, nameDraft, nameErr, confirmDelete, optics } = this.state;
    const custom = !look.builtin;
    const saved = optics.custom.some((l) => l.id === look.id);
    const inUse = look.theme !== null && lookFor(optics, look.theme).id === look.id;
    return html`<div class="looks-head">
      ${custom && nameDraft !== null
        ? html`<form class="looks-rename" onSubmit=${this.rename}>
            <label for="lk-name" class="sr">Look name</label>
            <input id="lk-name" value=${nameDraft} maxlength="60" aria-invalid=${nameErr ? "true" : undefined} aria-describedby=${nameErr ? "lk-name-err" : undefined}
              onInput=${(e) => this.setState({ nameDraft: e.currentTarget.value, nameErr: null })} ref=${(el) => el && !this.focusedName && (el.focus(), (this.focusedName = true))} />
            <button type="submit" class="action">Rename</button>
            <button type="button" class="action" onClick=${() => this.setState({ nameDraft: null, nameErr: null })}>Cancel</button>
            ${nameErr ? html`<p id="lk-name-err" class="looks-err" role="alert">${nameErr}</p>` : null}
          </form>`
        : html`<h3 class="looks-title" title=${look.name}>${look.name}</h3>`}
      <p class="looks-sub">${look.theme ? `${THEME_NAMES[look.theme]} theme` : "Both themes"} · ${look.builtin ? "built-in" : "custom"}${draft ? html` · <strong class="looks-unsaved">unsaved changes</strong>` : null}</p>
      <div class="looks-actions">
        ${look.profiles ? html`<button class="action" onClick=${this.copy}>Copy</button>` : null}
        ${custom ? html`<button class="action primary" disabled=${!draft} onClick=${this.save}>Save</button>` : null}
        ${look.theme ? html`<button class="action" disabled=${inUse || !!draft} onClick=${this.use}>${inUse ? `In use for ${look.theme}` : draft ? `Use for ${look.theme} (save first)` : `Use for ${look.theme}`}</button>` : null}
        ${custom && nameDraft === null ? html`<button class="action" onClick=${() => { this.focusedName = false; this.setState({ nameDraft: look.name }); }}>Rename</button>` : null}
        ${look.profiles ? html`<button class="action" onClick=${this.exportFile}>Export</button>` : null}
        ${custom ? html`<button class="action danger" onClick=${() => (saved ? this.setState({ confirmDelete: true }) : this.discardDraftCopy())}>Delete</button>` : null}
      </div>
      ${confirmDelete ? html`<div class="looks-confirm" role="alertdialog" aria-labelledby="lk-del-q">
          <p id="lk-del-q">Delete “${look.name}”? ${inUse ? `The ${look.theme} theme goes back to its default look. ` : ""}This cannot be undone.</p>
          <button class="action danger" onClick=${this.remove} ref=${(el) => el?.focus()}>Delete</button>
          <button class="action" onClick=${() => this.setState({ confirmDelete: false })}>Keep it</button>
        </div>` : null}
    </div>`;
  }

  // An unsaved copy has nothing stored: deleting it drops the draft.
  discardDraftCopy = () => this.setState({ draft: null, selId: lookFor(this.state.optics, this.look().theme).id });

  preview(look) {
    const { store } = this.props;
    const theme = look.theme ?? themeOf();
    return html`<div class="looks-pv" data-theme=${theme} ref=${(el) => (this.pv = el)}>
      ${this.state.sheets ? html`<div class="r6 frame looks-pv-frame" inert aria-label=${`Preview of ${look.name}`} role="img">
        <${Header} store=${store} onView=${noop} paused=${false} onPause=${noop} />
        <div class="frame-body">
          <div class="main-col"><main class="stage-wrap view-board"><div class="kb-wrap">
            <div class="kb-bar"><button type="button" class="tab on">Board</button><button type="button" class="tab">List</button><span class="chip">3 open</span></div>
            <div class="kb-body"><div class="kb-stage">
              <div class="kb" style="grid-template-columns:minmax(var(--kb-col-min), 1fr) minmax(var(--kb-col-min), 1fr) 40px">
                ${SAMPLE_COLS.map((c) => html`<section key=${c.key} class="kb-col glass">${kbHead({ ...c, count: c.cards.length }, false, null, noop)}<div class="kb-cards">${c.cards.map((x) => kbCard(x))}</div></section>`)}
                <div class="kb-strip"><span class="kb-stripin"><span class="kb-vl">Done</span><span class="num">0</span></span></div>
              </div>
            </div></div>
          </div></main></div>
          <aside class="rail">
            <section class="feed"><h2 data-text="Live feed">Live feed</h2></section>
            <nav class="member-rail"><ul>
              <li class="rail-row" data-role="orchestrator" data-state="working"><span class="rail-body"><span class="dot"></span><span class="n">orchestrator</span><span class="s">ui-looks</span><span class="ctx">41%</span></span></li>
              <li class="rail-row" data-role="reviewer" data-state="idle"><span class="rail-body"><span class="dot"></span><span class="n">reviewer</span><span class="s">idle</span><span class="ctx">12%</span></span></li>
            </ul></nav>
            <section class="panel">
              <div class="panel-head"><span data-text="Overview">Overview</span></div>
              <p>Body text with <a href="#/looks" tabindex="-1">accent text</a>.</p>
              <p><span class="looks-pv-ink2">Secondary</span> · <span class="looks-pv-muted">muted</span> · <span class="looks-pv-dim">dim</span></p>
              <table class="teams-table"><thead><tr><th>Team</th><th>Open</th></tr></thead><tbody><tr><td>ship</td><td class="mono">b125</td></tr></tbody></table>
            </section>
          </aside>
        </div>
        <${Footer} store=${store} />
      </div>` : null}
    </div>`;
  }

  render({ store }, s) {
    const look = this.look();
    const custom = s.optics.custom.filter((l) => l.id !== s.draft?.id);
    const customRows = [...custom, ...(s.draft && !s.optics.custom.some((l) => l.id === s.draft.id) ? [s.draft] : [])];
    const probes = (theme) => html`<div class="looks-pv looks-probes" data-theme=${theme} hidden ref=${(el) => (this[theme === "light" ? "probeLight" : "probeDark"] = el)}>
      ${[...TEXT_TOKENS, ...MARK_TOKENS].map((t) => html`<span key=${t} data-token=${t} style=${`color: var(${t})`}></span>`)}
    </div>`;
    return html`<section class="looks-page" aria-labelledby="looks-h">
      <header class="looks-bar">
        <h2 id="looks-h">Looks</h2>
        <p class="hint">Copy a built-in look to make your own. Saved looks are kept in looks.json in your ah home. Export one to share it.</p>
        <label class="action looks-import">Import…<input type="file" accept="application/json,.json" class="sr" onChange=${this.importFile} /></label>
      </header>
      ${s.ioErr ? html`<p class="looks-err looks-io" role="alert">${s.ioErr}</p>` : null}
      ${s.ioMsg ? html`<p class="looks-io" role="status">${s.ioMsg}</p>` : null}
      ${s.pending ? html`<div class="looks-confirm looks-leave" role="alertdialog" aria-labelledby="lk-leave-q">
          <p id="lk-leave-q">“${look.name}” has unsaved changes. Discard them?</p>
          <button class="action danger" onClick=${this.discard} ref=${(el) => el?.focus()}>Discard changes</button>
          <button class="action" onClick=${this.stay}>Keep editing</button>
        </div>` : null}
      <div class="looks-body">
        <nav class="looks-list" aria-label="Looks">
          ${["light", "dark"].map((th) => html`<h3 key=${`${th}-h`}>${THEME_NAMES[th]}</h3>
            <ul key=${`${th}-l`}>${[HOUSE, ...BUILTINS.filter((l) => l.theme === th)].map((l) => this.row(l, th))}</ul>`)}
          <h3>Custom</h3>
          ${customRows.length
            ? html`<ul>${customRows.map((l) => this.row(l, l.theme))}</ul>`
            : html`<p class="dim looks-empty">No custom looks yet. Select a built-in and copy it.</p>`}
        </nav>
        <div class="looks-main">
          ${this.head(look)}
          ${this.preview(look)}
          ${look.profiles ? this.controls(look) : html`<p class="dim looks-empty">House is the app's own look. It has no glass, halo or backdrop to tune.</p>`}
        </div>
      </div>
      ${probes("light")}${probes("dark")}
    </section>`;
  }
}
