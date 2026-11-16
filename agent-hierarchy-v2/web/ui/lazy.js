import { html, Component } from "./h.js";

const sheets = new Map();

// Adds a view's stylesheet the first time it is wanted and resolves once it has loaded, so the view never draws unstyled.
// It goes in before the static sheet `before`, which keeps the cascade order the page had when every sheet was linked up front.
// A load that fails rejects, and takes its link and its cached promise away, so the next ask tries again.
export function sheet(href, before) {
  if (!sheets.has(href)) {
    sheets.set(href, new Promise((done, fail) => {
      const link = Object.assign(document.createElement("link"), { rel: "stylesheet", href });
      link.addEventListener("load", () => done(), { once: true });
      link.addEventListener("error", () => {
        link.remove();
        sheets.delete(href);
        fail(new Error(`the stylesheet ${href} did not load`));
      }, { once: true });
      document.head.insertBefore(link, document.querySelector(`link[href="${before}"]`));
      if (link.sheet) {
        done();
      }
    }));
  }
  return sheets.get(href);
}

let d3 = null;

// d3, from the vendored UMD file, the first time a graph renderer asks for it: a classic script, never preloaded, so views that do not
// draw a graph never pay for it. A failed load is forgotten, so the next ask tries again.
export function loadD3() {
  if (globalThis.d3) {
    return Promise.resolve(globalThis.d3);
  }
  d3 ??= new Promise((done, fail) => {
    const script = Object.assign(document.createElement("script"), { src: "/ui/vendor/d3.min.js" });
    script.addEventListener("load", () => done(globalThis.d3), { once: true });
    script.addEventListener("error", () => {
      script.remove();
      d3 = null;
      fail(new Error("d3 did not load"));
    }, { once: true });
    document.head.append(script);
  });
  return d3;
}

// A view whose module (and stylesheet, given as [href, before]) is fetched the first time it is shown, so the landing page does not pay
// for views it never opens. Both are cached after the first load, so later mounts draw at once.
export function lazy(load, pick, css = null) {
  let View = null;
  return class Lazy extends Component {
    componentDidMount() {
      if (!View) {
        Promise.all([load(), css && sheet(...css)]).then(([m]) => { View = pick(m); }, () => { this.failed = true; }).then(() => !this.gone && this.forceUpdate());
      }
    }

    componentWillUnmount() {
      this.gone = true;
    }

    render(props) {
      if (View) {
        return html`<${View} ...${props} />`;
      }
      return html`<p class="dim skeleton">${this.failed ? "Could not load this view." : "Loading…"}</p>`;
    }
  };
}
