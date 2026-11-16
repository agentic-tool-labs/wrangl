// The page's boot watchdog, a classic script so it runs even when a module fails to load (the page's CSP forbids an inline one).
// ui.js calls window.ahBoot.started() as its first act; a script error, an unhandled rejection or ten silent seconds before that
// replace the status line with the first error.
(() => {
  let started = false;
  let reported = false;
  const report = (why) => {
    // This script runs in <head>, before the body, so the status line is looked up when an error arrives, not at load.
    const status = document.getElementById("status");
    if (started || reported || !status) {
      return;
    }
    reported = true;
    status.textContent = `The page's scripts failed to load: ${why}`;
  };
  window.ahBoot = {
    started() {
      started = true;
    },
  };
  // A module that cannot be fetched or parsed fires `error` on its element, which does not bubble: only the capture phase sees it.
  window.addEventListener(
    "error",
    (e) => {
      const el = e.target;
      if (el && el.tagName) {
        if (el.tagName === "SCRIPT" || (el.tagName === "LINK" && el.rel === "modulepreload")) {
          report(`could not load ${el.src || el.href}`);
        }
        return;
      }
      report(e.message || String(e.error));
    },
    true,
  );
  window.addEventListener("unhandledrejection", (e) => {
    const r = e.reason;
    report(r instanceof Error ? r.message : String(r));
  });
  setTimeout(() => report("the page did not start within 10 seconds"), 10000);
})();
