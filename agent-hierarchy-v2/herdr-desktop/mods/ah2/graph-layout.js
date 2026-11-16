// The web Map's layout without d3: Herdr freezes the JS intrinsics and d3.min.js throws on load (d3-color writes
// prototype.constructor). A straight port of the three d3 pieces graph-pack.js uses, so the herdr Graph lays out as the web's
// does; tests/web/herdr-graph-model.test.mjs holds it to web/ui/vendor/d3.min.js (same circles, same path commands).
//   d3.hierarchy (eachBefore, eachAfter, descendants, leaves, ancestors, path), d3.pack().radius().padding() with the pack's
//   lcg, siblings and enclose (d3-hierarchy); d3.line().curve(d3.curveBundle.beta(b)) into a command list (d3-shape). Both as
//   bundled in d3 7.9.0 (web/ui/vendor/d3.min.js, licence web/ui/vendor/LICENSE-d3.txt), whose notice follows in full:
//
// Copyright 2010-2023 Mike Bostock
//
// Permission to use, copy, modify, and/or distribute this software for any purpose
// with or without fee is hereby granted, provided that the above copyright notice
// and this permission notice appear in all copies.
//
// THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
// REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
// FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
// INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
// OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
// TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
// THIS SOFTWARE.

class Node {
  constructor(data, parent) {
    this.data = data;
    this.parent = parent;
    this.depth = parent ? parent.depth + 1 : 0;
    const kids = data.children ?? [];
    if (kids.length) this.children = kids.map((d) => new Node(d, this));
  }

  eachBefore(f) {
    const nodes = [this];
    for (let n; (n = nodes.pop()); ) {
      f(n);
      if (n.children) for (let i = n.children.length - 1; i >= 0; --i) nodes.push(n.children[i]);
    }
    return this;
  }

  eachAfter(f) {
    const nodes = [this];
    const next = [];
    for (let n; (n = nodes.pop()); ) {
      next.push(n);
      if (n.children) for (const c of n.children) nodes.push(c);
    }
    for (let n; (n = next.pop()); ) f(n);
    return this;
  }

  // Breadth first, as d3's iterator.
  descendants() {
    const out = [this];
    for (let i = 0; i < out.length; i++) out.push(...(out[i].children ?? []));
    return out;
  }

  // Depth first, as d3's.
  leaves() {
    const out = [];
    this.eachBefore((n) => n.children || out.push(n));
    return out;
  }

  ancestors() {
    const out = [];
    for (let n = this; n; n = n.parent) out.push(n);
    return out;
  }

  // The nodes from this one up to the least common ancestor, then down to `end`.
  path(end) {
    const up = this.ancestors();
    const down = end.ancestors();
    let lca = null;
    for (let a = up.pop(), b = down.pop(); a === b && a; a = up.pop(), b = down.pop()) lca = a;
    const nodes = [];
    for (let n = this; n !== lca; n = n.parent) nodes.push(n);
    nodes.push(lca);
    const k = nodes.length;
    for (let n = end; n !== lca; n = n.parent) nodes.splice(k, 0, n);
    return nodes;
  }
}

export const hierarchy = (data) => new Node(data, null);

// d3.pack().radius(() => radius).padding(padding)(root), on the unit square as d3's default size.
export function pack(root, radius, padding) {
  let s = 1;
  const random = () => (s = (1664525 * s + 1013904223) % 4294967296) / 4294967296;
  root.x = 0.5;
  root.y = 0.5;
  root.eachBefore((n) => {
    if (!n.children) n.r = Math.max(0, +radius(n) || 0);
  });
  root.eachAfter((n) => {
    const c = n.children;
    if (!c) return;
    const r = padding(n) * 0.5 || 0; // with a fixed radius d3 packs at half the padding (packChildrenRandom(padding, 0.5))
    if (r) for (const x of c) x.r += r;
    const e = packSiblings(c, random);
    if (r) for (const x of c) x.r -= r;
    n.r = e + r;
  });
  root.eachBefore((n) => {
    if (n.parent) {
      n.x = n.parent.x + n.x;
      n.y = n.parent.y + n.y;
    }
  });
  return root;
}

function place(b, a, c) {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const d2 = dx * dx + dy * dy;
  if (d2) {
    const a2 = (a.r + c.r) ** 2;
    const b2 = (b.r + c.r) ** 2;
    if (a2 > b2) {
      const x = (d2 + b2 - a2) / (2 * d2);
      const y = Math.sqrt(Math.max(0, b2 / d2 - x * x));
      c.x = b.x - x * dx - y * dy;
      c.y = b.y - x * dy + y * dx;
    } else {
      const x = (d2 + a2 - b2) / (2 * d2);
      const y = Math.sqrt(Math.max(0, a2 / d2 - x * x));
      c.x = a.x + x * dx - y * dy;
      c.y = a.y + x * dy + y * dx;
    }
  } else {
    c.x = a.x + c.r;
    c.y = a.y;
  }
}

function intersects(a, b) {
  const dr = a.r + b.r - 1e-6;
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  return dr > 0 && dr * dr > dx * dx + dy * dy;
}

function score(node) {
  const a = node._;
  const b = node.next._;
  const ab = a.r + b.r;
  const dx = (a.x * b.r + b.x * a.r) / ab;
  const dy = (a.y * b.r + b.y * a.r) / ab;
  return dx * dx + dy * dy;
}

const link = (circle) => ({ _: circle, next: null, previous: null });

function packSiblings(circles, random) {
  const n = circles.length;
  if (!n) return 0;
  let a = circles[0];
  a.x = 0;
  a.y = 0;
  if (n === 1) return a.r;
  let b = circles[1];
  a.x = -b.r;
  b.x = a.r;
  b.y = 0;
  if (n === 2) return a.r + b.r;
  let c = circles[2];
  place(b, a, c);
  a = link(a);
  b = link(b);
  c = link(c);
  a.next = c.previous = b;
  b.next = a.previous = c;
  c.next = b.previous = a;
  pack: for (let i = 3; i < n; ++i) {
    place(a._, b._, circles[i]);
    c = link(circles[i]);
    let j = b.next;
    let k = a.previous;
    let sj = b._.r;
    let sk = a._.r;
    do {
      if (sj <= sk) {
        if (intersects(j._, c._)) {
          b = j;
          a.next = b;
          b.previous = a;
          --i;
          continue pack;
        }
        sj += j._.r;
        j = j.next;
      } else {
        if (intersects(k._, c._)) {
          a = k;
          a.next = b;
          b.previous = a;
          --i;
          continue pack;
        }
        sk += k._.r;
        k = k.previous;
      }
    } while (j !== k.next);
    c.previous = a;
    c.next = b;
    a.next = b.previous = b = c;
    let aa = score(a);
    while ((c = c.next) !== b) {
      const ca = score(c);
      if (ca < aa) {
        a = c;
        aa = ca;
      }
    }
    b = a.next;
  }
  const chain = [b._];
  for (c = b; (c = c.next) !== b; ) chain.push(c._);
  const e = enclose(chain, random);
  for (const x of circles) {
    x.x -= e.x;
    x.y -= e.y;
  }
  return e.r;
}

// Not in d3: a pass limit and a non-finite check, so degenerate geometry throws instead of looping. Never reached on finite input.
const ENCLOSE_PASSES = (n) => Math.max(64, 16 * n * n);

function enclose(circles, random) {
  const list = Array.from(circles);
  for (let m = list.length; m; ) {
    const i = (random() * m--) | 0;
    [list[m], list[i]] = [list[i], list[m]];
  }
  let basis = [];
  let e;
  const limit = ENCLOSE_PASSES(list.length);
  for (let i = 0, passes = 0; i < list.length; ) {
    const p = list[i];
    if (e && enclosesWeak(e, p)) ++i;
    else {
      basis = extendBasis(basis, p);
      e = encloseBasis(basis);
      i = 0;
      if (++passes > limit || !(Number.isFinite(e.x) && Number.isFinite(e.y) && Number.isFinite(e.r))) {
        throw new Error("pack: no enclosing basis");
      }
    }
  }
  return e;
}

function extendBasis(B, p) {
  if (enclosesWeakAll(p, B)) return [p];
  for (const q of B) if (enclosesNot(p, q) && enclosesWeakAll(encloseBasis2(q, p), B)) return [q, p];
  for (let i = 0; i < B.length - 1; ++i) {
    for (let j = i + 1; j < B.length; ++j) {
      if (enclosesNot(encloseBasis2(B[i], B[j]), p) && enclosesNot(encloseBasis2(B[i], p), B[j])
        && enclosesNot(encloseBasis2(B[j], p), B[i]) && enclosesWeakAll(encloseBasis3(B[i], B[j], p), B)) {
        return [B[i], B[j], p];
      }
    }
  }
  throw new Error("pack: no enclosing basis");
}

function enclosesNot(a, b) {
  const dr = a.r - b.r;
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  return dr < 0 || dr * dr < dx * dx + dy * dy;
}

function enclosesWeak(a, b) {
  const dr = a.r - b.r + Math.max(a.r, b.r, 1) * 1e-9;
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  return dr > 0 && dr * dr > dx * dx + dy * dy;
}

const enclosesWeakAll = (a, B) => B.every((b) => enclosesWeak(a, b));

function encloseBasis(B) {
  if (B.length === 1) return { x: B[0].x, y: B[0].y, r: B[0].r };
  return B.length === 2 ? encloseBasis2(B[0], B[1]) : encloseBasis3(B[0], B[1], B[2]);
}

function encloseBasis2(a, b) {
  const x21 = b.x - a.x;
  const y21 = b.y - a.y;
  const r21 = b.r - a.r;
  const l = Math.sqrt(x21 * x21 + y21 * y21);
  return { x: (a.x + b.x + (x21 / l) * r21) / 2, y: (a.y + b.y + (y21 / l) * r21) / 2, r: (l + a.r + b.r) / 2 };
}

function encloseBasis3(a, b, c) {
  const [x1, y1, r1, x2, y2, r2, x3, y3, r3] = [a.x, a.y, a.r, b.x, b.y, b.r, c.x, c.y, c.r];
  const a2 = x1 - x2;
  const a3 = x1 - x3;
  const b2 = y1 - y2;
  const b3 = y1 - y3;
  const c2 = r2 - r1;
  const c3 = r3 - r1;
  const d1 = x1 * x1 + y1 * y1 - r1 * r1;
  const d2 = d1 - x2 * x2 - y2 * y2 + r2 * r2;
  const d3 = d1 - x3 * x3 - y3 * y3 + r3 * r3;
  const ab = a3 * b2 - a2 * b3;
  const xa = (b2 * d3 - b3 * d2) / (ab * 2) - x1;
  const xb = (b3 * c2 - b2 * c3) / ab;
  const ya = (a3 * d2 - a2 * d3) / (ab * 2) - y1;
  const yb = (a2 * c3 - a3 * c2) / ab;
  const A = xb * xb + yb * yb - 1;
  const B = 2 * (r1 + xa * xb + ya * yb);
  const C = xa * xa + ya * ya - r1 * r1;
  const r = -(Math.abs(A) > 1e-6 ? (B + Math.sqrt(B * B - 4 * A * C)) / (2 * A) : C / B);
  return { x: x1 + xa + xb * r, y: y1 + ya + yb * r, r };
}

// d3.line().curve(d3.curveBundle.beta(beta)) over points {x, y}, as commands: ["M", x, y], ["L", x, y], ["C", x1, y1, x2, y2, x, y].
export function bundle(points, beta) {
  const out = [];
  const j = points.length - 1;
  if (j < 1) return out;
  const { x: x0, y: y0 } = points[0];
  const dx = points[j].x - x0;
  const dy = points[j].y - y0;
  let n = 0;
  let px0 = NaN;
  let px1 = NaN;
  let py0 = NaN;
  let py1 = NaN;
  const curve = (x, y) => out.push(["C", (2 * px0 + px1) / 3, (2 * py0 + py1) / 3, (px0 + 2 * px1) / 3, (py0 + 2 * py1) / 3, (px0 + 4 * px1 + x) / 6, (py0 + 4 * py1 + y) / 6]);
  points.forEach((p, i) => {
    const t = i / j;
    const x = beta * p.x + (1 - beta) * (x0 + t * dx);
    const y = beta * p.y + (1 - beta) * (y0 + t * dy);
    if (n === 0) out.push(["M", x, y]);
    else if (n === 2) {
      out.push(["L", (5 * px0 + px1) / 6, (5 * py0 + py1) / 6]);
      curve(x, y);
    } else if (n > 2) curve(x, y);
    n++;
    [px0, px1, py0, py1] = [px1, x, py1, y];
  });
  if (n >= 3) curve(px1, py1);
  if (n >= 2) out.push(["L", px1, py1]);
  return out;
}
