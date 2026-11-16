// A just-enough DOM for Preact to mount into and patch, plus a render counter. Not a browser: no layout, no events.
import { options } from "../../web/ui/vendor/preact.js";

class Node_ {
  constructor(type) {
    this.nodeType = type;
    this.childNodes = [];
    this.parentNode = null;
  }
  get firstChild() {
    return this.childNodes[0] ?? null;
  }
  get lastChild() {
    return this.childNodes.at(-1) ?? null;
  }
  get nextSibling() {
    const p = this.parentNode;
    return p ? (p.childNodes[p.childNodes.indexOf(this) + 1] ?? null) : null;
  }
  get previousSibling() {
    const p = this.parentNode;
    return p ? (p.childNodes[p.childNodes.indexOf(this) - 1] ?? null) : null;
  }
  appendChild(c) {
    c.parentNode?.removeChild(c);
    c.parentNode = this;
    this.childNodes.push(c);
    return c;
  }
  append(...nodes) {
    nodes.forEach((n) => this.appendChild(n));
  }
  insertBefore(c, ref) {
    if (ref == null) {
      return this.appendChild(c);
    }
    c.parentNode?.removeChild(c);
    this.childNodes.splice(this.childNodes.indexOf(ref), 0, c);
    c.parentNode = this;
    return c;
  }
  removeChild(c) {
    const i = this.childNodes.indexOf(c);
    if (i >= 0) {
      this.childNodes.splice(i, 1);
      c.parentNode = null;
    }
    return c;
  }
  remove() {
    this.parentNode?.removeChild(this);
  }
  get children() {
    return this.childNodes.filter((c) => c.nodeType === 1);
  }
  get textContent() {
    return this.nodeType === 3 ? this.data : this.childNodes.map((c) => c.textContent).join("");
  }
  set textContent(v) {
    if (this.nodeType === 3) {
      this.data = v;
      return;
    }
    this.childNodes.length = 0;
    if (v !== "") {
      this.appendChild(new Text_(v));
    }
  }
}

class Text_ extends Node_ {
  constructor(data) {
    super(3);
    this.data = String(data);
  }
  get nodeValue() {
    return this.data;
  }
  set nodeValue(v) {
    this.data = String(v);
  }
}

class Style_ {
  constructor() {
    this.props = new Map();
  }
  setProperty(k, v) {
    this.props.set(k, String(v));
  }
  removeProperty(k) {
    this.props.delete(k);
  }
  getPropertyValue(k) {
    return this.props.get(k) ?? "";
  }
}

class Element_ extends Node_ {
  constructor(name, ns) {
    super(1);
    this.localName = name;
    this.nodeName = ns ? name : name.toUpperCase();
    this.namespaceURI = ns ?? null;
    this.attrs = new Map();
    this.style = new Style_();
    // Preact lower-cases an event name only when the element has the matching `on<event>` property.
    this.onclick = null;
    this.onkeydown = null;
    this.onscroll = null;
    this.oninput = null;
    this.onchange = null;
    this.onsubmit = null;
    this.onmouseenter = null;
    this.onmouseleave = null;
    this.onfocus = null;
    this.onblur = null;
    this.onfocusin = null;
    this.onfocusout = null;
    this.onpointerdown = null;
    this.onpointermove = null;
    this.onpointerup = null;
    this.onpointercancel = null;
    this.onwheel = null;
    this.scrollTop = 0;
    const el = this;
    this.classList = {
      toggle: (c, on) => {
        const set = new Set((el.attrs.get("class") ?? "").split(/\s+/).filter(Boolean));
        on === false || (on === undefined && set.has(c)) ? set.delete(c) : set.add(c);
        el.attrs.set("class", [...set].join(" "));
      },
    };
  }
  setAttribute(k, v) {
    this.attrs.set(k, String(v));
  }
  setAttributeNS(_, k, v) {
    this.attrs.set(k, String(v));
  }
  removeAttribute(k) {
    this.attrs.delete(k);
  }
  hasAttribute(k) {
    return this.attrs.has(k);
  }
  getAttribute(k) {
    return this.attrs.get(k) ?? null;
  }
  addEventListener(name, fn) {
    (this.listeners ??= {})[name] = [...(this.listeners[name] ?? []), fn];
  }
  removeEventListener(name, fn) {
    if (this.listeners?.[name]) {
      this.listeners[name] = this.listeners[name].filter((f) => f !== fn);
    }
  }
  // Runs the listeners of a DOM event, as a click would.
  // focusin and focusout bubble to the ancestors' listeners, as in a browser.
  fire(name, event = {}) {
    const bubbles = name === "focusin" || name === "focusout";
    for (let node = this; node; node = bubbles ? node.parentNode : null) {
      for (const fn of node.listeners?.[name] ?? []) {
        fn.call(node, { type: name, preventDefault() {}, stopPropagation() {}, target: this, currentTarget: node, ...event });
      }
    }
  }
  focus() {
    globalThis.document.activeElement = this;
  }
  getBoundingClientRect() {
    return this.rect ?? { left: 100, right: 140, top: 50, bottom: 90, width: 40, height: 40 };
  }
  contains(node) {
    for (let n = node; n; n = n.parentNode) {
      if (n === this) {
        return true;
      }
    }
    return false;
  }
  // The buttons, inputs and elements with a tabindex below this one, in document order (the dialog's focus trap reads these).
  querySelectorAll() {
    const out = [];
    const walk = (n) => {
      for (const c of n.childNodes) {
        if (c.nodeType === 1) {
          if (["button", "input", "select", "textarea", "a"].includes(c.localName) || (c.attrs.has("tabindex") && c.attrs.get("tabindex") !== "-1")) {
            out.push(c);
          }
          walk(c);
        }
      }
    };
    walk(this);
    return out;
  }
  get className() {
    return this.attrs.get("class") ?? "";
  }
  set className(v) {
    this.attrs.set("class", String(v));
  }
  // Elements below this one that match `.class`, `tag` or `tag.class`.
  all(sel) {
    const [tag, cls] = sel.startsWith(".") ? [null, sel.slice(1)] : sel.split(".");
    const out = [];
    const walk = (n) => {
      for (const c of n.childNodes) {
        if (c.nodeType === 1) {
          const classes = (c.attrs.get("class") ?? "").split(/\s+/);
          if ((!tag || c.localName === tag) && (!cls || classes.includes(cls))) {
            out.push(c);
          }
          walk(c);
        }
      }
    };
    walk(this);
    return out;
  }
}

export function installDom() {
  const body = new Element_("body");
  const documentElement = new Element_("html");
  documentElement.appendChild(body);
  // A stylesheet link put in the head counts as loaded at once: there is no network here.
  const head = new Element_("head");
  const insert = head.insertBefore.bind(head);
  head.insertBefore = (c, ref) => Object.assign(insert(c, ref), c.localName === "link" ? { sheet: {} } : {});
  globalThis.document = {
    createElement: (t) => new Element_(t),
    createElementNS: (ns, t) => new Element_(t, ns),
    createTextNode: (d) => new Text_(d),
    body,
    documentElement,
    head,
    activeElement: null,
    // Only the `link[href="…"]` lookup the lazy stylesheets make.
    querySelector(sel) {
      const href = /^link\[href="([^"]+)"\]$/.exec(sel)?.[1];
      return (href && this.head.childNodes.find((n) => n.href === href)) || null;
    },
  };
  const root = new Element_("div");
  body.appendChild(root);
  return root;
}

// Counts the components that actually render. Preact calls options.__r right before a component's render(); a memo that
// bails out never reaches it, whereas options.diffed also fires for a component that did not render.
export function countRenders() {
  const counts = new Map();
  const prior = options.__r;
  options.__r = (vnode) => {
    const name = vnode.type?.displayName ?? vnode.type?.name ?? "anon";
    counts.set(name, (counts.get(name) ?? 0) + 1);
    prior?.(vnode);
  };
  return {
    counts,
    total: () => [...counts.values()].reduce((a, b) => a + b, 0),
    reset: () => counts.clear(),
    stop: () => {
      options.__r = prior;
    },
  };
}

// Lets Preact's queued re-renders run.
export const settle = () => new Promise((r) => setTimeout(r, 0));
