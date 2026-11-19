// The palette presets (ah2-276): which preset each mode shows, stored per browser in localStorage `ah-preset` as {dark, light}. A
// preset's colours live in presets.css (scripts/gen-presets.mjs), under :root[data-preset]; this module only sets that attribute.
// "default" is ui.css itself plus the stored look (House or a glass look, which the menu lists by name); any other preset draws flat,
// as House does (looks.js applyStored skips the glass under it). The pair rule: a preset with a counterpart in the other mode
// (Catppuccin, Solarized) sets both modes, so the light/dark toggle moves to its pair; any other pick leaves the other mode alone.

export const KEY = "ah-preset";
export const DEFAULT = "default";

// `file` is the theme-studio preset it is generated from. Two are left out: emerald-frost and for-ghost-glass carry for-ghost's
// palette byte for byte and differ only in their glow and glass pictures, which the web UI does not draw.
export const PRESETS = Object.freeze([
  { id: DEFAULT, name: "Default", mode: "dark" },
  { id: DEFAULT, name: "Default", mode: "light" },
  { id: "abyssal-bloom", name: "Abyssal Bloom", mode: "dark", file: "abyssal-bloom" },
  { id: "catppuccin-mocha", name: "Catppuccin Mocha", mode: "dark", file: "catppuccin-mocha", pair: "catppuccin-latte" },
  { id: "catppuccin-latte", name: "Catppuccin Latte", mode: "light", file: "catppuccin-latte", pair: "catppuccin-mocha" },
  { id: "command-deck", name: "Command Deck", mode: "dark", file: "command-deck" },
  { id: "dracula", name: "Dracula", mode: "dark", file: "dracula" },
  { id: "everforest", name: "Everforest", mode: "dark", file: "everforest" },
  { id: "for-ghost", name: "For Ghost", mode: "dark", file: "for-ghost" },
  { id: "for-ghost-green", name: "For Ghost Green", mode: "dark", file: "for-ghost-green" },
  { id: "gruvbox-dark", name: "Gruvbox Dark", mode: "dark", file: "gruvbox-dark" },
  { id: "kanagawa", name: "Kanagawa", mode: "dark", file: "kanagawa" },
  { id: "nord", name: "Nord", mode: "dark", file: "nord" },
  { id: "one-light", name: "One Light", mode: "light", file: "one-light" },
  { id: "rose-pine", name: "Rosé Pine", mode: "dark", file: "rose-pine" },
  { id: "solarized-dark", name: "Solarized Dark", mode: "dark", file: "solarized-dark", pair: "solarized-light" },
  { id: "solarized-light", name: "Solarized Light", mode: "light", file: "solarized-light", pair: "solarized-dark" },
  { id: "tokyo-night", name: "Tokyo Night", mode: "dark", file: "tokyo-night" },
].map((p) => Object.freeze(p)));

export const presetsFor = (mode) => PRESETS.filter((p) => p.mode === mode);

// The mode the page shows: data-theme when set, else the OS scheme (looks.js re-exports it as themeOf).
export function modeOf(doc = globalThis.document, win = globalThis) {
  const set = doc?.documentElement?.getAttribute?.("data-theme");
  if (set === "light" || set === "dark") return set;
  return win.matchMedia?.("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

// A store that is blocked keeps the choice for this page only.
const held = new WeakMap();
export function storedPresets(win = globalThis) {
  if (held.has(win)) return held.get(win);
  try {
    const v = JSON.parse(win.localStorage?.getItem(KEY) ?? "null");
    const ok = (mode) => (presetsFor(mode).some((p) => p.id === v?.[mode]) ? v[mode] : DEFAULT);
    return { dark: ok("dark"), light: ok("light") };
  } catch {
    return { dark: DEFAULT, light: DEFAULT };
  }
}

export const presetOf = (mode, win = globalThis) => storedPresets(win)[mode];

// Point :root at the shown mode's preset (none for Default). Returns that preset's id.
export function applyPreset(doc = globalThis.document, win = globalThis) {
  const id = presetOf(modeOf(doc, win), win);
  const root = doc?.documentElement;
  if (id === DEFAULT) root?.removeAttribute?.("data-preset");
  else root?.setAttribute?.("data-preset", id);
  return id;
}

// The menu's pick: `id` for `mode`, and its pair for the other mode. Stores, holds and returns the new choice; the caller re-applies.
export function choosePreset(mode, id, win = globalThis) {
  const p = presetsFor(mode).find((x) => x.id === id);
  if (!p) return storedPresets(win);
  const other = mode === "dark" ? "light" : "dark";
  const next = { ...storedPresets(win), [mode]: id, ...(p.pair ? { [other]: p.pair } : {}) };
  held.set(win, next);
  try {
    win.localStorage?.setItem(KEY, JSON.stringify(next));
  } catch {
    // held for this page
  }
  return next;
}
