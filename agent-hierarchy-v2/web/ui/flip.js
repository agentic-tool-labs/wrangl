// FLIP for lists that reorder: each frame's measuring is one batch, reads first and then writes, and the only
// properties animated are transform and opacity. Nothing here is called while a component renders.

const DURATION = 180;

const before = new WeakMap();
const queue = new Map();
let queued = false;

const calm = () => typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;

function run() {
  queued = false;
  const jobs = [...queue];
  queue.clear();
  const reads = jobs.map(([box, keys]) => {
    const spot = new Map();
    keys.forEach((k, i) => {
      const el = box.children[i];
      if (el) {
        spot.set(k, { el, x: el.offsetLeft, y: el.offsetTop });
      }
    });
    return [box, spot];
  });
  const still = calm();
  for (const [box, spot] of reads) {
    const was = before.get(box);
    before.set(box, spot);
    if (!was || still) {
      continue;
    }
    for (const [k, now] of spot) {
      const old = was.get(k);
      if (!now.el.animate) {
        continue;
      }
      if (old === undefined) {
        now.el.animate([{ opacity: 0 }, { opacity: 1 }], { duration: DURATION });
      } else if (old.x !== now.x || old.y !== now.y) {
        now.el.animate([{ transform: `translate(${old.x - now.x}px, ${old.y - now.y}px)` }, { transform: "none" }], { duration: DURATION, easing: "ease-out" });
      }
    }
  }
}

// Asks for `box`'s children, in `keys` order, to be measured in the next frame's batch and the moved ones animated.
export function flip(box, keys, raf = globalThis.requestAnimationFrame) {
  queue.set(box, keys);
  if (!queued) {
    queued = true;
    raf(run);
  }
}

// Forgets where `box`'s children were, so the next flip only records them. A view whose children also move for reasons it does not
// animate (a zoom) calls this, then flips once synchronously, just before the change it does animate.
export const forget = (box) => before.delete(box);
