import { html, h, memo, Component } from "./h.js";
import { flip } from "./flip.js";

// A list over this many rows draws only the rows in view, each at a fixed height.
export const VIRTUAL_OVER = 200;
export const OVERSCAN = 5;
export const ROW_HEIGHT = 28;
export const VIEW_HEIGHT = 560;

const Row = memo(function Row({ r, render, ...ctx }) {
  return render(r, ctx);
});

// The rows [from, to) of a list of `count` that a window `top` px down, `view` px tall, needs.
export function windowOf(count, top, view = VIEW_HEIGHT, rowHeight = ROW_HEIGHT, overscan = OVERSCAN) {
  const to = Math.min(count, Math.ceil((top + view) / rowHeight) + overscan);
  const from = Math.min(to, Math.max(0, Math.floor(top / rowHeight) - overscan));
  return [from, to];
}

// The one list component. `keyOf` names a row by its stable id; `render(row, ctx)` returns the row's element.
// Each row draws only when the row or one of the `ctx` values changed by reference. Lists that fit are an
// ordinary <ul> whose reorders are animated; longer ones are windowed.
export class Rows extends Component {
  state = { top: 0 };

  onScroll = (e) => {
    const top = e.currentTarget.scrollTop;
    if (this.pending) {
      return;
    }
    this.pending = true;
    (this.raf ?? requestAnimationFrame)(() => {
      this.pending = false;
      this.setState({ top });
    });
  };

  componentDidMount() {
    this.measure();
  }

  componentDidUpdate() {
    this.measure();
  }

  measure() {
    const { rows, keyOf } = this.props;
    if (rows.length <= VIRTUAL_OVER && this.base) {
      flip(this.base, rows.map(keyOf));
    }
  }

  render({ cls, rows, keyOf, row, ctx = {}, as = "ul" }, { top }) {
    const tag = as;
    const draw = (from, to) => rows.slice(from, to).map((r) => h(Row, { key: keyOf(r), r, render: row, ...ctx }));
    if (rows.length <= VIRTUAL_OVER) {
      return h(tag, { class: cls }, draw(0, rows.length));
    }
    const [from, to] = windowOf(rows.length, top);
    return html`<div class="vlist" style=${{ height: `${VIEW_HEIGHT}px`, "--row-h": `${ROW_HEIGHT}px` }} onScroll=${this.onScroll}>
      ${h(tag, { class: cls, style: { paddingTop: `${from * ROW_HEIGHT}px`, paddingBottom: `${(rows.length - to) * ROW_HEIGHT}px` } }, draw(from, to))}
    </div>`;
  }
}
