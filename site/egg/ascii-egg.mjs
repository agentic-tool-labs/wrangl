// The mascot easter egg's lazy module: Canvas UI's ASCII Object plus its render settings panel. build.mjs bundles
// this file (with three) into assets/ascii-object.js, which site.js imports on the first click only.
export { createAsciiObject } from '../vendor/canvas-ui/AsciiObject/AsciiObjectVanilla.ts';

// The component's own defaults (not exported by it), for the keys the panel shows. site.js overrides some of them.
const DEFAULTS = {
  scale: 3, autoRotate: false, ascii: true, charset: 'full', cellSize: 10, colored: true, color: '#ffffff', invert: false,
  cellAspect: 0.6, edgeContrast: 3, contrast: 1.5, exposure: 1, highlight: '#066aff', pageBg: true, bgColour: '#ffffff',
  environmentIntensity: 1, roughness: -1, floatIntensity: 2, rotationIntensity: 1, floatSpeed: 2, zoom: false, fov: 65,
  cameraDistance: 4.2, xOffset: 0, yOffset: 0,
};
const CHARSETS = { full: undefined, simple: ' .:-=+*#%@', digits: ' .012345689' };

// The playground's controls (canvasui.dev, src/demos/ascii-object-demo.tsx) with its labels and ranges, regrouped
// for a short docked panel.
const range = (key, label, min, max, step, when) => ({ key, label, kind: 'range', min, max, step, when });
const check = (key, label, when) => ({ key, label, kind: 'check', when });
const colour = (key, label, when) => ({ key, label, kind: 'color', when });
const ascii = v => v.ascii;
const GROUPS = [
  ['Characters', [
    check('ascii', 'ASCII render'),
    { key: 'charset', label: 'Glyph set', kind: 'radio', options: [['full', 'Full'], ['simple', 'Simple'], ['digits', 'Digits']], when: ascii },
    range('cellSize', 'Cell size', 4, 28, 1, ascii),
    range('cellAspect', 'Cell aspect', 0.35, 1.25, 0.05),
    range('edgeContrast', 'Edge snap', 1, 6, 0.1),
    check('colored', 'Scene colours', ascii),
    colour('color', 'Character colour', v => v.ascii && !v.colored),
    check('invert', 'Invert', ascii),
  ]],
  ['Tone', [
    range('contrast', 'Contrast', 0.5, 4, 0.1),
    range('exposure', 'Exposure', 0, 3, 0.05),
    range('environmentIntensity', 'Environment', 0, 5, 0.1),
    range('roughness', 'Roughness', -1, 1, 0.01),
    colour('highlight', 'Highlight'),
    check('pageBg', 'Page background'),
    colour('bgColour', 'Background', v => !v.pageBg),
  ]],
  ['Motion', [
    check('autoRotate', 'Auto rotate'),
    range('floatIntensity', 'Float', 0, 6, 0.1),
    range('rotationIntensity', 'Rocking', 0, 4, 0.1),
    range('floatSpeed', 'Float speed', 0, 8, 0.1),
  ]],
  ['Camera', [
    range('scale', 'Size', 0.5, 6, 0.1),
    check('zoom', 'Scroll to zoom'),
    range('fov', 'Field of view', 20, 100, 1),
    range('cameraDistance', 'Distance', 2, 10, 0.1),
    range('xOffset', 'X offset', -3, 3, 0.1),
    range('yOffset', 'Y offset', -3, 3, 0.1),
  ]],
];
const CONTROLS = GROUPS.flatMap(([, controls]) => controls);

// What the reader changed. Module state, so it lasts while the page is open and is never stored.
const changed = {};
let base = {};
let fit = 1;
let live = null;
const values = () => ({ ...DEFAULTS, ...base, ...changed });
// Size and the offsets are in the component's units as if the canvas were the image's box; `fit` (from site.js)
// converts them to the whole canvas, so the object keeps the image's size and place whatever the overlay's shape.
// LIFT is the component's fixed MODEL_LIFT, which its y offset adds to.
const LIFT = 0.3;
const toOptions = v => {
  const { charset, pageBg, bgColour, ...rest } = v;
  return { ...rest, scale: v.scale * fit, xOffset: v.xOffset * fit, yOffset: (v.yOffset + LIFT) * fit - LIFT,
    charset: CHARSETS[charset], background: pageBg ? '' : bgColour };
};

// The component options the egg opens with: its own (`egg`, from site.js) plus whatever the reader changed.
export function settings(egg, f) {
  base = egg;
  fit = f;
  return { ...egg, ...toOptions(values()) };
}
// The canvas changed shape: keep the object at the image box's size.
export function refit(f) {
  if (Math.abs(f - fit) < 1e-4) return;
  fit = f;
  live?.setOptions(toOptions(values()));
}

const decimals = step => (String(step).split('.')[1] || '').length;
const show = (c, v) => (c.key === 'roughness' && v.roughness < 0 ? 'model' : Number(v[c.key]).toFixed(decimals(c.step)));

function markup() {
  const id = c => `egg-${c.key}`;
  const row = c => {
    if (c.kind === 'range') {
      return `<div class="egg-row" data-key="${c.key}"><label for="${id(c)}">${c.label}</label>`
        + `<input id="${id(c)}" type="range" min="${c.min}" max="${c.max}" step="${c.step}"><output for="${id(c)}"></output></div>`;
    }
    if (c.kind === 'check') return `<div class="egg-row egg-check" data-key="${c.key}"><input id="${id(c)}" type="checkbox"><label for="${id(c)}">${c.label}</label></div>`;
    if (c.kind === 'color') return `<div class="egg-row egg-colour" data-key="${c.key}"><label for="${id(c)}">${c.label}</label><input id="${id(c)}" type="color"></div>`;
    return `<div class="egg-row egg-radio" data-key="${c.key}" role="radiogroup" aria-labelledby="${id(c)}-label"><span id="${id(c)}-label">${c.label}</span>`
      + `<span class="egg-radios">${c.options.map(([value, text]) => `<label><input type="radio" name="${id(c)}" value="${value}">${text}</label>`).join('')}</span></div>`;
  };
  return `<div class="egg-panel-head"><h2 id="egg-panel-title">Render settings</h2><button type="button" class="btn egg-reset">Reset</button>`
    + `<button type="button" class="btn egg-panel-close" aria-label="Close render settings">Close</button></div>`
    + `<div class="egg-groups">${GROUPS.map(([name, controls]) => `<section class="egg-group" aria-labelledby="egg-g-${name}"><h3 id="egg-g-${name}">${name}</h3>${controls.map(row).join('')}</section>`).join('')}</div>`;
}

// The panel: a bar docked under the canvas inside the overlay `dlg`, toggled by `button`. attach() points it at a
// live instance.
export function createPanel(dlg, button) {
  const still = matchMedia('(prefers-reduced-motion: reduce)');
  const panel = document.createElement('section');
  panel.className = 'egg-panel';
  panel.id = 'egg-panel';
  panel.hidden = true;
  panel.setAttribute('aria-labelledby', 'egg-panel-title');
  panel.innerHTML = markup();
  dlg.append(panel);
  button.setAttribute('aria-controls', panel.id);

  const sync = () => {
    const v = values();
    for (const c of CONTROLS) {
      const el = panel.querySelector(`[data-key="${c.key}"]`);
      el.hidden = c.when ? !c.when(v) : false;
      if (c.kind === 'radio') for (const r of el.querySelectorAll('input')) r.checked = r.value === v[c.key];
      else if (c.kind === 'check') el.querySelector('input').checked = !!v[c.key];
      else el.querySelector('input').value = v[c.key];
      if (c.kind === 'range') el.querySelector('output').textContent = show(c, v);
    }
  };
  const apply = () => { sync(); live?.setOptions(toOptions(values())); };
  panel.addEventListener('input', e => {
    const el = e.target.closest('[data-key]');
    if (!el) return;
    const c = CONTROLS.find(x => x.key === el.dataset.key);
    const input = e.target;
    changed[c.key] = c.kind === 'check' ? input.checked : c.kind === 'range' ? Number(input.value) : input.value;
    apply();
  });
  panel.querySelector('.egg-reset').addEventListener('click', () => { for (const k of Object.keys(changed)) delete changed[k]; apply(); });

  const isOpen = () => !panel.hidden;
  const set = open => {
    button.setAttribute('aria-expanded', String(open));
    for (const a of panel.getAnimations()) a.cancel();
  };
  const slide = dir => ({ opacity: dir, ...(still.matches ? {} : { transform: dir[0] ? ['none', 'translateY(100%)'] : ['translateY(100%)', 'none'] }) });
  const open = () => {
    if (isOpen()) return;
    set(true);
    panel.hidden = false;
    panel.animate(slide([0, 1]), { duration: 250, easing: 'cubic-bezier(0.2, 0, 0, 1)' });
    panel.querySelector('.egg-groups input').focus();
  };
  const close = (focusButton = true, animate = true) => {
    if (!isOpen()) return;
    set(false);
    if (focusButton) button.focus();
    if (!animate) { panel.hidden = true; return; }
    panel.animate(slide([1, 0]), { duration: 200, easing: 'cubic-bezier(0.3, 0, 1, 1)', fill: 'forwards' })
      .finished.then(() => { if (button.getAttribute('aria-expanded') === 'false') { panel.hidden = true; set(false); } }, () => {});
  };
  button.addEventListener('click', () => (isOpen() ? close() : open()));
  panel.querySelector('.egg-panel-close').addEventListener('click', () => close());
  return {
    attach(instance) { live = instance; sync(); },
    detach() { live = null; close(false, false); },
    isOpen, close,
  };
}
