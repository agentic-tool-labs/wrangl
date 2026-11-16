// The one normalizer of the contract tooling. Rules are data (tests/contracts/normalize.json); this module only applies them.
// Used by the contract runner, the recorder, the tree diff and the parity replay. It imports nothing from hooks/.
//
//   import { loadRules, createNormalizer, selfTest } from "./normalize.mjs";
//   const n = createNormalizer(loadRules(path), { T, roots: {home, ah, proj}, plugin });
//   n.text("stdout", s)   // plain text: stdout | stderr | file | path
//   n.json(value)         // a parsed JSON value (a wire body): the same rules as a file
//
// Placeholders written `<NAME:n>` in a rule's `replace` are numbered by first appearance within one normalizer (one case),
// across every stream and file that normalizer sees, so two different ids stay two different placeholders.
//
// JSON and JSONL texts are parsed so keyed rules act on values and not on look-alike text; the parsed value is written back
// in the indent it came with, and every text rule then runs over the written text (strings and keys alike).
//
// A rule with `action: "drop"` removes a key from a parsed JSON file instead of rewriting its value. It is scoped: it needs `paths`
// (globs over the file's case-relative path, `<root>/<path>`), `keys`, and a `value_pattern` the value must match in full, so a
// look-alike key elsewhere, or a value of another shape, is kept. `n.text("file", s, { path })` carries the path.

import { readFileSync, realpathSync } from "node:fs";

const KINDS = new Set(["stdout", "stderr", "file", "path"]);

/** Reads and validates a rule file. A rule without both an example and a counterexample is rejected. */
export function loadRules(file) {
  const doc = JSON.parse(readFileSync(file, "utf8"));
  const rules = doc.rules;
  if (!Array.isArray(rules)) throw new Error(`${file}: "rules" must be an array`);
  const seen = new Set();
  for (const r of rules) {
    const where = `${file}: rule ${JSON.stringify(r && r.name)}`;
    if (!r || typeof r.name !== "string" || !r.name) throw new Error(`${where}: missing name`);
    if (seen.has(r.name)) throw new Error(`${where}: duplicate name`);
    seen.add(r.name);
    if (!Array.isArray(r.applies_to) || r.applies_to.length === 0 || r.applies_to.some((k) => !KINDS.has(k))) throw new Error(`${where}: applies_to must list some of ${[...KINDS].join(", ")}`);
    if (r.action !== undefined && r.action !== "drop") throw new Error(`${where}: unknown action ${JSON.stringify(r.action)}`);
    const drop = r.action === "drop";
    if (drop) {
      if (!Array.isArray(r.keys) || r.keys.length === 0) throw new Error(`${where}: a drop rule needs keys`);
      if (typeof r.value_pattern !== "string" || !r.value_pattern) throw new Error(`${where}: a drop rule needs a value_pattern (the shape of the value it may remove)`);
      if (!Array.isArray(r.paths) || r.paths.length === 0 || r.paths.some((g) => typeof g !== "string" || !g)) throw new Error(`${where}: a drop rule needs paths (the files it may act on)`);
      if (r.applies_to.length !== 1 || r.applies_to[0] !== "file") throw new Error(`${where}: a drop rule applies to file only`);
    } else if (typeof r.replace !== "string") throw new Error(`${where}: missing replace`);
    if (!r.example || typeof r.example.in !== "string" || typeof r.example.out !== "string") throw new Error(`${where}: missing example {in, out}`);
    if (!r.counterexample || typeof r.counterexample.in !== "string") throw new Error(`${where}: missing counterexample {in}`);
    const keyed = Array.isArray(r.keys);
    if (!keyed && typeof r.context !== "string" && typeof r.pattern !== "string") throw new Error(`${where}: missing pattern`);
    if (keyed && r.keys.length === 0) throw new Error(`${where}: keys is empty`);
    if (keyed && !drop && !(r.replace.startsWith('"') && r.replace.endsWith('"'))) throw new Error(`${where}: a keyed rule's replace must be a quoted JSON string`);
    if (r.any_value !== undefined && (r.any_value !== true || !keyed || drop || typeof r.within !== "string" || !r.within)) throw new Error(`${where}: any_value needs keys and a within (the name of the object whose keys it masks)`);
  }
  return { rules, ignore_paths: Array.isArray(doc.ignore_paths) ? doc.ignore_paths : [] };
}

/** Whether a number is inside a keyed rule's open range: above `min` and below `max`, each only when the rule sets it. */
const inRange = (r, n) => (r.min === undefined || n > r.min) && (r.max === undefined || n < r.max);

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const UUID_SHAPE = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

/** A glob over a `<root>/<path>`: `*` is any run inside one component, `**` any run. */
const globRe = (g) => new RegExp(`^${g.split("**").map((part) => part.split("*").map(escapeRe).join("[^/]*")).join(".*")}$`);

/** Every spelling of one absolute path: itself, its realpath when it exists, and the /tmp <-> /private/tmp twin. */
export function spellings(p) {
  const out = new Set();
  const add = (s) => {
    if (!s) return;
    out.add(s);
    if (s.startsWith("/private/tmp/")) out.add(s.slice("/private".length));
    else if (s.startsWith("/tmp/")) out.add("/private" + s);
    if (s.startsWith("/private/var/")) out.add(s.slice("/private".length));
    else if (s.startsWith("/var/")) out.add("/private" + s);
  };
  add(p);
  try { add(realpathSync(p)); } catch { /* the path may not exist yet */ }
  return [...out];
}

function contextTable(rule, ctx) {
  // [[absolute path, placeholder]], longest path first so a root under T wins over T itself
  const rows = [];
  // `ctx.aliases` lists further {roots, T, plugin, repo} that map to the same placeholders: the places a recorded run lived in
  const sets = [ctx, ...(ctx.aliases || [])];
  if (rule.context === "roots") {
    for (const c of sets) {
      for (const [name, p] of Object.entries(c.roots || {})) if (p) for (const s of spellings(p)) rows.push([s, `<ROOT:${name}>`]);
      if (c.T) for (const s of spellings(c.T)) rows.push([s, "<T>"]);
    }
  } else if (rule.context === "plugin") {
    for (const c of sets) if (c.plugin) for (const s of spellings(c.plugin)) rows.push([s, rule.replace]);
  } else if (rule.context === "pid") {
    if (ctx.pid) rows.push([String(ctx.pid), rule.replace]);
  } else if (rule.context === "pidstart") {
    if (ctx.pidStart) rows.push([String(ctx.pidStart), rule.replace]);
  } else if (rule.context === "version") {
    if (ctx.version) rows.push([String(ctx.version), rule.replace]);
  } else if (rule.context === "repo") {
    for (const c of sets) if (c.repo) for (const s of spellings(c.repo)) rows.push([s, rule.replace]);
  } else {
    throw new Error(`rule ${rule.name}: unknown context ${rule.context}`);
  }
  rows.sort((a, b) => b[0].length - a[0].length);
  return rows;
}

/** One normalizer = one case: it owns the placeholder numbering. */
export function createNormalizer({ rules }, ctx = {}) {
  const index = new Map(); // `${rule}\0${match}` -> n
  const counters = new Map(); // rule -> next n
  const number = (rule, match) => {
    const key = `${rule}\0${match}`;
    if (!index.has(key)) {
      const n = (counters.get(rule) || 0) + 1;
      counters.set(rule, n);
      index.set(key, n);
    }
    return index.get(key);
  };
  const compiled = rules.map((r) => {
    if (r.action === "drop") return { r, drop: true, keySet: new Set(r.keys), valueRe: new RegExp(`^(?:${r.value_pattern})$`), pathRes: r.paths.map(globRe) };
    if (Array.isArray(r.keys)) return { r, keyed: true, keySet: new Set(r.keys), valueRe: typeof r.value_pattern === "string" ? new RegExp(`^(?:${r.value_pattern})$`) : null, valueCtx: typeof r.value_context === "string" ? r.value_context : null };
    if (r.context) return { r, ctxRows: contextTable(r, ctx) };
    return { r, re: new RegExp(r.pattern, r.flags || "g") };
  });

  // A numbered placeholder takes its number from the first appearance of the matched value; a rule with `numbering: "occurrence"`
  // (a clock value, whose equality with another one is an accident of timing) numbers every occurrence on its own.
  const occurrence = new Map(rules.map((r) => [r.name, r.numbering === "occurrence"]));
  const ticks = new Map();
  const fill = (rule, template, match) => {
    if (!template.includes(":n>")) return template;
    if (occurrence.get(rule)) {
      const n = (ticks.get(rule) || 0) + 1;
      ticks.set(rule, n);
      return template.replace(/:n>/g, `:${n}>`);
    }
    return template.replace(/:n>/g, `:${number(rule, match)}>`);
  };

  function applyTextRule(c, s) {
    if (c.keyed || c.drop) return s;
    if (c.ctxRows) {
      if (c.r.context === "plugin" || c.r.context === "repo") {
        // the checkout under test, as a path prefix: the longer plugin path is replaced first because its rule comes first
        for (const [p, ph] of c.ctxRows) s = s.replace(new RegExp(`${escapeRe(p)}(?![A-Za-z0-9_.-])`, "g"), ph);
        return s;
      }
      if (c.r.context === "pid") {
        // the pid of the process that runs the case, as a whole number (a longer digit run is not it)
        for (const [p, ph] of c.ctxRows) {
          // in a value position only a pid-named key (pid, ppid, orchestrator_pid, ...) holds the runner's pid: it becomes a quoted
          // string, so a normalized JSON text stays JSON. Any other field that happens to equal it (a duration, a count) is data.
          s = s.replace(new RegExp(`((?:"[A-Za-z0-9_]*pid"|\\b[A-Za-z0-9_]*pid)\\s*:\\s*)${escapeRe(p)}(?![0-9.A-Za-z])`, "gi"), `$1"${ph}"`);
          // a UUID-shaped token is consumed whole, so a random id that happens to contain the pid's digits is not edited; a number
          // after a colon is a value of some other key and stays
          s = s.replace(new RegExp(`(${UUID_SHAPE})|(?<!:\\s*)(?<![0-9])${escapeRe(p)}(?![0-9])`, "g"), (m, uuid) => (uuid ? m : ph));
        }
        return s;
      }
      if (c.r.context === "version") {
        // the plugin's version, as a whole dotted number (0.133.10 and 10.133.1 are not 0.133.1)
        for (const [p, ph] of c.ctxRows) s = s.replace(new RegExp(`(?<![0-9.])${escapeRe(p)}(?![0-9]|\\.[0-9])`, "g"), ph);
        return s;
      }
      // a root is replaced only where it ends a path component, so /x/home is not eaten inside /x/homework
      for (const [p, ph] of c.ctxRows) s = s.replace(new RegExp(`${escapeRe(p)}(?![A-Za-z0-9_.-])`, "g"), ph);
      return s;
    }
    // a `groups` rule rewrites with the regex's own captures ($1, $2, ...): one rule maps a whole family of spellings
    if (c.r.groups) return s.replace(c.re, c.r.replace);
    return s.replace(c.re, (...m) => fill(c.r.name, c.r.replace, m[0]));
  }

  // keyed rules on text that is not JSON as a whole: `"key": <integer>`
  function keyedText(s, kind) {
    for (const c of compiled) {
      if (!c.keyed || c.r.any_value || !c.r.applies_to.includes(kind)) continue;
      for (const k of c.keySet) {
        if (c.valueCtx) {
          // a string value equal to the context's value of the same name (the runtime under test)
          const want = ctx[c.valueCtx];
          if (typeof want !== "string" || !want) continue;
          const cre = new RegExp(`("${escapeRe(k)}"\\s*:\\s*)"${escapeRe(want)}"`, "g");
          s = s.replace(cre, (all, pre) => pre + c.r.replace);
          continue;
        }
        if (c.valueRe) {
          const sre = new RegExp(`("${escapeRe(k)}"\\s*:\\s*)"((?:[^"\\\\]|\\\\.)*)"`, "g");
          s = s.replace(sre, (all, pre, val) => (c.valueRe.test(val) ? pre + c.r.replace : all));
          continue;
        }
        const re = new RegExp(`("${escapeRe(k)}"\\s*:\\s*)(-?\\d+)`, "g");
        s = s.replace(re, (all, pre, num) => (inRange(c.r, Number(num)) ? pre + c.r.replace : all));
      }
    }
    return s;
  }

  // keyed rules on a parsed value: a number under one of the keys becomes the rule's string
  function keyedWalk(v, key, kind, parent) {
    // an `any_value` rule masks whatever sits under its key (a number, null, text or a whole object), only inside the object
    // named by `within`, so a look-alike key elsewhere keeps its value
    if (key !== undefined) {
      for (const c of compiled) {
        if (c.keyed && c.r.any_value && c.keySet.has(key) && c.r.within === parent && c.r.applies_to.includes(kind)) return c.r.replace.slice(1, -1);
      }
    }
    if (Array.isArray(v)) return v.map((x) => keyedWalk(x, key, kind, parent));
    if (v && typeof v === "object") {
      const o = {};
      for (const [k, x] of Object.entries(v)) o[k] = keyedWalk(x, k, kind, key);
      return o;
    }
    if (typeof v === "string" && key !== undefined) {
      for (const c of compiled) {
        if (!c.keyed || !(c.valueRe || c.valueCtx) || !c.keySet.has(key) || !c.r.applies_to.includes(kind)) continue;
        if (c.valueRe && c.valueRe.test(v)) return c.r.replace.slice(1, -1);
        if (c.valueCtx && typeof ctx[c.valueCtx] === "string" && ctx[c.valueCtx] && v === ctx[c.valueCtx]) return c.r.replace.slice(1, -1);
      }
    }
    if (typeof v === "number" && key !== undefined) {
      for (const c of compiled) {
        if (!c.keyed || c.r.any_value || c.valueRe || c.valueCtx || !c.keySet.has(key) || !c.r.applies_to.includes(kind)) continue;
        if (!inRange(c.r, v)) continue;
        return c.r.replace.slice(1, -1);
      }
    }
    return v;
  }

  // drop rules on a parsed value: a key whose value has the rule's shape, in a file the rule is scoped to, is removed
  function dropKeys(v, path) {
    const live = typeof path === "string" ? compiled.filter((c) => c.drop && c.pathRes.some((re) => re.test(path))) : [];
    if (live.length === 0) return v;
    const walk = (x) => {
      if (Array.isArray(x)) return x.map(walk);
      if (x && typeof x === "object") {
        const o = {};
        for (const [k, y] of Object.entries(x)) {
          if ((typeof y === "string" || typeof y === "boolean") && live.some((c) => c.keySet.has(k) && c.valueRe.test(String(y)))) continue;
          o[k] = walk(y);
        }
        return o;
      }
      return x;
    };
    return walk(v);
  }

  function parseJson(s) {
    if (!s.trim()) return null;
    const trail = /\n$/.test(s);
    try {
      return { doc: JSON.parse(s), jsonl: false, trail, indent: (/\n( +)"/.exec(s) || [, ""])[1].length };
    } catch { /* maybe JSONL */ }
    const rows = [];
    for (const l of s.split("\n")) {
      if (l === "") continue;
      try { rows.push(JSON.parse(l)); } catch { return null; }
    }
    return rows.length ? { doc: rows, jsonl: true, trail } : null;
  }

  function write(p, v) {
    if (p.jsonl) return v.map((x) => JSON.stringify(x)).join("\n") + (p.trail ? "\n" : "");
    return JSON.stringify(v, null, p.indent || undefined) + (p.trail ? "\n" : "");
  }

  function text(kind, s, meta = {}) {
    if (typeof s !== "string") s = String(s);
    let out = s;
    const parsed = kind === "stdout" || kind === "file" ? parseJson(s) : null;
    out = parsed ? write(parsed, keyedWalk(kind === "file" ? dropKeys(parsed.doc, meta.path) : parsed.doc, undefined, kind)) : keyedText(s, kind);
    for (const c of compiled) if (c.r.applies_to.includes(kind)) out = applyTextRule(c, out);
    return out;
  }

  return {
    text,
    /** A parsed JSON value (a wire body, an expect.json): keyed rules by key, then every text rule over its written text. */
    json(v) {
      const written = JSON.stringify(keyedWalk(v, undefined, "file"));
      let out = written;
      for (const c of compiled) if (c.r.applies_to.includes("file")) out = applyTextRule(c, out);
      return JSON.parse(out);
    },
  };
}

/** Runs every rule's example and counterexample. Returns {ok, failures: [string]}. */
export function selfTest(loaded) {
  const failures = [];
  for (const r of loaded.rules) {
    const kind = r.applies_to[0];
    const one = { rules: [r] };
    const got = createNormalizer(one, (r.example && r.example.ctx) || {}).text(kind, r.example.in, { path: r.example.path });
    if (got !== r.example.out) failures.push(`${r.name}: example ${JSON.stringify(r.example.in)} gave ${JSON.stringify(got)}, wanted ${JSON.stringify(r.example.out)}`);
    const cx = createNormalizer(one, (r.counterexample && r.counterexample.ctx) || (r.example && r.example.ctx) || {}).text(kind, r.counterexample.in, { path: r.counterexample.path ?? r.example.path });
    if (cx !== r.counterexample.in) failures.push(`${r.name}: counterexample ${JSON.stringify(r.counterexample.in)} changed to ${JSON.stringify(cx)}`);
  }
  return { ok: failures.length === 0, failures };
}

if (import.meta.url === `file://${process.argv[1]}` && process.argv[2] === "--self-test") {
  const file = process.argv[3];
  const res = selfTest(loadRules(file));
  for (const f of res.failures) console.error("FAIL " + f);
  console.log(res.ok ? "normalize self-test: ok" : "normalize self-test: FAIL");
  process.exit(res.ok ? 0 : 1);
}
