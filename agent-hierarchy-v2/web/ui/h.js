import { h, render, Component, options } from "./vendor/preact.js";
import htm from "./vendor/htm.js";

export const html = htm.bind(h);
export { h, render, options, Component };

const differ = (a, b) => a.length !== b.length || a.some((v, i) => v !== b[i]);

// A component that draws again only when a prop changed by reference. Pair it with merge(), which keeps the
// reference of every subtree a new document did not change. `children` are compared like any other prop, so a
// component given children always draws again. `pick(props)` names the values to compare when only some of
// the props matter (a card is handed the whole document's groups but reads one of them).
export function memo(Inner, { pick, name = Inner.name } = {}) {
  const values = pick ?? ((props) => Object.keys(props).flatMap((k) => [k, props[k]]));
  class Memo extends Component {
    shouldComponentUpdate(next) {
      return differ(values(next), values(this.props));
    }

    render(props) {
      return h(Inner, props);
    }
  }
  Memo.displayName = `memo(${name})`;
  return Memo;
}
