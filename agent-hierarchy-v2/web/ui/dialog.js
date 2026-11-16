import { html, Component } from "./h.js";

// The one dialog frame: an aria-modal dialog with a focus trap, closed by Escape, the backdrop or ×, returning focus to what opened it.

const FOCUSABLE = 'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])';

// Where Tab should send focus: the first control from the last, the last from the first with Shift, the first from outside; otherwise nowhere (the browser moves it).
export function trapTarget(items, active, shift) {
  if (items.length === 0) {
    return null;
  }
  const first = items[0];
  const last = items.at(-1);
  if (!items.includes(active)) {
    return first;
  }
  if (shift && active === first) {
    return last;
  }
  return !shift && active === last ? first : null;
}

export class Dialog extends Component {
  componentDidMount() {
    this.opener = document.activeElement;
    (this.controls()[0] ?? this.el).focus();
  }

  // `returnTo`, a selector, takes focus instead when the opener is gone or was the page itself (a dialog opened by an address).
  componentWillUnmount() {
    const lost = !this.opener || this.opener.isConnected === false || this.opener === globalThis.document?.body;
    const back = lost && this.props.returnTo ? globalThis.document?.querySelector?.(this.props.returnTo) : this.opener;
    back?.focus?.();
  }

  controls() {
    return [...this.el.querySelectorAll(FOCUSABLE)].filter((n) => !n.disabled);
  }

  onKeyDown = (e) => {
    if (e.key === "Escape") {
      e.stopPropagation();
      this.props.onClose();
    } else if (e.key === "Tab") {
      const target = trapTarget(this.controls(), document.activeElement, e.shiftKey === true);
      if (target) {
        e.preventDefault();
        target.focus();
      }
    }
  };

  render({ title, titleId, onClose, cls = "", children }) {
    return html`<div class="dialog-backdrop" onClick=${(e) => e.target === e.currentTarget && onClose()}>
      <section class=${`dialog ${cls}`} role="dialog" aria-modal="true" aria-label=${title} tabindex="-1" ref=${(el) => (this.el = el)} onKeyDown=${this.onKeyDown}>
        <header class="dialog-head"><h2 id=${titleId}>${title}</h2><button class="close" aria-label="Close" onClick=${onClose}>×</button></header>
        ${children}
      </section>
    </div>`;
  }
}
