import { Component } from "./h.js";

// A component that reads the store itself. `select(snapshot, props)` returns a flat object of what the component draws;
// it draws again only when one of those values changed (or a prop did), so a store change that touches none of them
// re-renders nothing, and no parent has to draw to pass data down.
const differ = (a, b) => {
  const ak = Object.keys(a);
  return ak.length !== Object.keys(b).length || ak.some((k) => a[k] !== b[k]);
};

let owners = 0;

export class Live extends Component {
  // A component that shows slices the page does not always hold defines `leaseIds(snapshot, props)`, the ids it shows. They are
  // leased under this instance's own owner key while it is mounted and released when it unmounts; they are re-read on every store
  // change and on every prop change, and the store restarts the poll only when the union of all leases changes.
  holdLease(snap) {
    if (!this.leaseIds) {
      return;
    }
    const ids = this.leaseIds(snap, this.props);
    const key = ids.join(",");
    if (key !== this.leaseKey) {
      this.leaseKey = key;
      this.props.store.lease(this.leaseOwner ??= `${this.constructor.name}#${++owners}`, ids);
    }
  }

  componentDidMount() {
    this.holdLease(this.props.store.state);
    this.off = this.props.store.subscribe((snap) => {
      this.holdLease(snap);
      const next = this.select(snap, this.props);
      if (differ(next, this.sel ?? {})) {
        this.sel = next;
        this.forceUpdate();
      }
    });
  }

  componentDidUpdate() {
    this.holdLease(this.props.store.state);
  }

  componentWillUnmount() {
    this.off?.();
    if (this.leaseKey) {
      this.leaseKey = "";
      this.props.store.lease(this.leaseOwner, []);
    }
  }

  shouldComponentUpdate(nextProps, nextState) {
    return differ(nextProps, this.props) || differ(nextState ?? {}, this.state ?? {});
  }

  render(props) {
    this.sel = this.select(props.store.state, props);
    return this.draw(props, this.sel);
  }
}

export { differ };
