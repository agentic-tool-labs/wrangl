import { connect, Ended, endedText } from "./bridge.js";
import { start, START_VIEWS } from "./app.js";
import { loadLooks } from "./looks.js";

// The page entry: connect, learn whether the source is the mock, and mount the R6 frame.

const status = document.getElementById("status");
const root = document.getElementById("view");
const failure = (e) => (e instanceof Ended ? endedText(e.message) : `Could not load the page: ${e instanceof Error ? e.message : String(e)}`);
const RETRY_MS = 5000;

async function main() {
  window.ahBoot?.started();
  status.textContent = "Connecting";
  const get = await connect();
  // The first read also tells whether this is the mock source (the X-Ah-Mock header), which the frame shows as a banner.
  status.textContent = `Connecting: reading ${START_VIEWS[0]}`;
  // A failed read says why and tries again: the daemon may be restarting.
  for (;;) {
    try {
      await get.manifest(START_VIEWS[0]);
      break;
    } catch (e) {
      status.textContent = failure(e);
      await new Promise((done) => setTimeout(done, RETRY_MS));
    }
  }
  // The saved look before the first paint, so the page does not flash a built-in; a slow answer does not hold the page up.
  await Promise.race([loadLooks(), new Promise((done) => setTimeout(done, 800))]);
  status.remove();
  document.body.classList.add("r6");
  start(get, root, get.mock);
}

main().catch((e) => {
  status.textContent = failure(e);
});
