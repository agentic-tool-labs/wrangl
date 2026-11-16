// Where ah runs, and how to reach it there (herdr-http-link.md §3): the one module that decides it. Every call that runs ah, or reads a
// file in ah's home, goes through a runner made here. Imports no Herdr code: herdr's exec and readTextFile are passed in, so node tests
// (tests/web/herdr-link-model.test.mjs) drive it.

export const UNRESOLVED = "Set where ah runs in Wrangl settings";

// The Settings value "ah runs on" as stored: "auto", "local", "machine:<name>", or "wsl:" (the default distro) / "wsl:<distro>".
export const AH_ON_AUTO = "auto";

// The target, in the §3 order: the setting, else auto. Auto is this computer off Windows; on Windows the one herdr machine there is,
// the default WSL distro when there is none, and unresolved for two or more (never a guess) or when WSL is missing (`wsl` false).
// `machines` is herdr's machines(): null names this computer.
export function targetOf({ platform, machines, setting, wsl = true }) {
  const s = typeof setting === "string" ? setting : AH_ON_AUTO;
  if (s === "local") return { kind: "local" };
  if (s.startsWith("machine:") && s.length > 8) return { kind: "machine", name: s.slice(8) };
  if (s.startsWith("wsl:")) return { kind: "wsl", distro: s.slice(4) || null, auto: false };
  if (platform !== "windows") return { kind: "local" };
  const named = (machines ?? []).map((m) => m?.name).filter((n) => typeof n === "string" && n !== "");
  if (named.length === 1) return { kind: "machine", name: named[0] };
  if (named.length > 1 || !wsl) return { kind: "unresolved" };
  return { kind: "wsl", distro: null, auto: true };
}

// What Settings says the target is.
export function targetLabel(t) {
  if (t.kind === "local") return "This computer";
  if (t.kind === "machine") return `herdr machine ${t.name}`;
  if (t.kind === "wsl") return t.distro ? `WSL distro ${t.distro}` : "WSL (default distro)";
  return "Not set";
}

// A stable key for a target: a copy notices a changed setting by it.
export const targetKey = (t) => `${t.kind}:${t.name ?? t.distro ?? ""}`;

// The Error a runner rejects with when the target cannot run anything; `down` is the reason a view shows.
const down = (text, cause) => Object.assign(new Error(text), { down: text, cause });
const slow = (e) => /longer than/.test(e?.message ?? String(e));

// The runner for target `t`: `exec` takes herdr's exec arguments and runs the command where ah runs; `read(path)` a text file in ah's home (null where
// the mod cannot read one: a WSL distro); `install(appHome, opts)` the `ah herdr install` into this herdr's home. `onMissingWsl` is
// told when an auto WSL target finds no wsl.exe, so the next target is unresolved.
export function runnerOf(exec, readTextFile, t, { platform = null, onMissingWsl = null } = {}) {
  const where = t.kind === "machine" ? t.name : t.kind === "wsl" ? (t.distro ?? "default distro") : null;
  const failed = (e) => {
    if (slow(e) || !where) throw e;
    if (t.kind === "wsl" && t.auto) {
      onMissingWsl?.();
      throw down(UNRESOLVED, e);
    }
    throw down(`WSL is not running (${where})`, e);
  };
  const run = (cmd, args, opts = {}) => {
    if (t.kind === "unresolved") return Promise.reject(down(UNRESOLVED));
    if (t.kind === "machine") return exec(cmd, args, { ...opts, on: t.name }).catch(failed);
    if (t.kind === "wsl") return exec("wsl.exe", [...(t.distro ? ["-d", t.distro] : []), "--exec", cmd, ...args], opts).catch(failed);
    return exec(cmd, args, opts);
  };
  return {
    target: t,
    exec: run,
    read: (path) => (t.kind === "local" ? readTextFile(path) : t.kind === "machine" ? readTextFile(path, { on: t.name }) : Promise.resolve(null)),
    // `flags`: constant install flags (`--replace-layout`), appended to the command line.
    install: (appHome, opts, flags = []) => (platform === "windows" && t.kind !== "local"
      ? run("sh", ["-c", `HERDR_APP_HOME="$(wslpath -u "$1")" exec ah herdr install${flags.map((f) => ` ${f}`).join("")}`, "sh", appHome], opts)
      : exec("sh", ["-c", `HERDR_APP_HOME="$1" exec ah herdr install${flags.map((f) => ` ${f}`).join("")}`, "sh", appHome], opts)),
  };
}

// A terminals() address as ah records it: on a herdr machine target, `M/<x>` is `<x>` there (labels on that machine omit the prefix).
export const paneOf = (t, address) => (t?.kind === "machine" && address?.startsWith(`${t.name}/`) ? address.slice(t.name.length + 1) : address);
