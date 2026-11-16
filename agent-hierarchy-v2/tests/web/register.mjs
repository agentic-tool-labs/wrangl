// Makes the bare `preact` import of the vendored render-to-string resolve to the one Preact the page ships,
// so the cards and the renderer share a single copy.
import { register } from "node:module";

register("./resolve-preact.mjs", import.meta.url);
