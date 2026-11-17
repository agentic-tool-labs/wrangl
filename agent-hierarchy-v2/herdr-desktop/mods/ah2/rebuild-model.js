// The background rebuild of ahd (ah2-111), read from <AH_HOME>/install.json alone; imports no Herdr code. The same rule as
// mod/view.ts (rebuildOf, rebuildStep, rebuildGaveUp): the Claude mod's guard (tests/test-mod-readonly.sh) lets it import values
// from ./view only, so the two cannot share one file; tests/web/rebuild-notice.test.mjs drives both copies through the same cases.

// Herdr's shared store key for the rebuild this window last kept.
export const REBUILD_KEY = "ah2.rebuild";
// How often a running build is re-read, and for how long before the watch gives up and says nothing more.
export const REBUILD_EVERY_MS = 3000;
export const REBUILD_WATCH_MS = 10 * 60_000;
// `ah where`'s output -> ah's home as ah resolves it, its "home" field; null when there is no output, it is not JSON, or it names
// no home (a binary older than that field).
export function homeOf(stdout) {
  let j;
  try { j = JSON.parse(String(stdout)); } catch { return null; }
  const h = j !== null && typeof j === "object" ? j.home : null;
  return typeof h === "string" && h !== "" ? h : null;
}

// ah's home for the target `key`, held in `cache` ({ key, home }): looked up with `lookup` (resolves to `ah where`'s output, or
// null) when the target changed or no home is held, so a failed or home-less reply is tried again on the next check.
export async function homeFor(cache, key, lookup) {
  if (cache.key === key && cache.home !== null) return cache.home;
  cache.key = key;
  cache.home = homeOf(await lookup());
  return cache.home;
}

// The stamp's text -> its last_rebuild, with the stamp's own version; null when the text is not a stamp with one.
export function rebuildOf(text) {
  let j;
  try { j = JSON.parse(String(text)); } catch { return null; }
  const r = j !== null && typeof j === "object" ? j.last_rebuild : null;
  if (r === null || typeof r !== "object" || typeof r.version !== "string" || typeof r.at !== "string" || typeof r.state !== "string") return null;
  return { version: r.version, at: r.at, state: r.state, stamp: typeof j.version === "string" ? j.version : null };
}

// Given the rebuild this surface last kept (`before`, null: none) and the one the stamp records now (null: unreadable): what to
// say, what to keep (`before` itself when nothing changed, so a caller writes only when keep !== before), and whether to keep
// watching. A rebuild is keyed on version + at, so one already kept says nothing; "ready" and "failed" need its "started" kept first.
export function rebuildStep(before, now) {
  const b = before !== null && typeof before === "object" ? before : null;
  const watching = (r) => r !== null && r.state === "started";
  if (now === null || (b !== null && b.version === now.version && b.at === now.at)) return { notice: null, keep: before, watch: watching(b) };
  const ended = b !== null && b.state === "started" && b.version === now.version;
  const notice = now.state === "started" ? { kind: "building", version: now.version }
    : ended && now.state === "ok" && now.stamp === now.version ? { kind: "ready", version: now.version }
    : ended && now.state === "failed" ? { kind: "failed", version: now.version }
    : null;
  return { notice, keep: now, watch: watching(now) };
}

// The kept rebuild once the watch gave up: same key, so it says nothing more, and not "started", so it is not watched again.
export function rebuildGaveUp(kept) {
  return kept !== null && typeof kept === "object" && kept.state === "started" ? { ...kept, state: "gave-up" } : kept;
}

// The herdr notice for a step's notice: the Claude toast's words under the title Wrangl.
export function rebuildNotify(n, home) {
  if (n.kind === "building") return { title: "Wrangl", body: `Building ahd ${n.version}…`, level: "info" };
  if (n.kind === "ready") return { title: "Wrangl", body: `ahd ${n.version} is ready`, level: "info" };
  return { title: "Wrangl", body: `The ahd ${n.version} build failed; see ${home}/logs/rebuild.log`, level: "warning" };
}
