#!/usr/bin/env node
// agent-hierarchy — the two jobs tests/run-set.sh leaves to node: reading the trx files of a dotnet test run, and writing or
// reading a result.json.
//
//   node tests/run-set-tools.mjs trx DIR
//       prints "COUNTS <total> <passed> <failed>", "FILES <n>" (the trx files read), one "BADOUTCOME <file>=<outcome>" per trx whose
//       ResultSummary outcome is neither Completed nor Failed, and one "FAIL <name>\t<project>\t<first error line>" per failed test method
//       of the *.trx files in DIR (a name is class.method, so a theory's rows are one test)
//   node tests/run-set-tools.mjs firsterror LOG [DIFFS]
//       prints a failed suite's first error: the first real lines of the first .diff under DIFFS, else the log's first FAIL line, never a PASS line
//   node tests/run-set-tools.mjs result --sha S --set N --status T --started A --ended B --log L
//                                       --counts "total passed failed" --failures FILE --flaky FILE
//       prints the result.json; FILE holds tab-separated rows: name, project, first error, failed twice (true|false) for
//       --failures, and name, project for --flaky
//   node tests/run-set-tools.mjs field FILE KEY
//       prints one top-level field of a result.json (nothing when the file is unreadable or the field absent)
//   node tests/run-set-tools.mjs merge --sha S --set N --started A --ended B --log L FILE...
//       prints the result.json of a set made of the result.json files of its components, in the order given
//   node tests/run-set-tools.mjs env
//       prints the environment fingerprint of this process: hostname, ncpu, in_session (CLAUDECODE, HERDR_* or any CODEX_* except CODEX_HOME set; CODEX_HOME and CLAUDE_CONFIG_DIR configure a CLI and never count) and
//       sandbox (always null: nothing here can tell whether the agent sandbox is on)
//   node tests/run-set-tools.mjs envcmp FILE
//       prints nothing when the result.json FILE was made on a host and in a session kind comparable with this process, else the reason
//   node tests/run-set-tools.mjs keys FILE
//       prints the name of every failure of the result.json FILE, one per line (prefixed "<set>|" in a merged result)
//   node tests/run-set-tools.mjs compare --head FILE --base FILE
//       prints the verdict JSON (base, verdict, reason, new, fixed, still, flaky, added, removed) and exits 0 for clean, 4 for new-failures,
//       5 for incomparable
//   node tests/run-set-tools.mjs annotate SHA SET VERDICTFILE HEADRESULT BASERESULT
//       prints the verdict document for the sha's set: sha, set, base, verdict, verdict_reason when there is one, classes, and the
//       sha256 of the head and base result.json it judged (head_sha256, base_sha256)
//   node tests/run-set-tools.mjs verdictcheck VERDICT HEADRESULT BASERESULT
//       prints ok and exits 0 when the verdict file's two hashes still match the files, else prints why it is stale and exits 1

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const unescape = (s) => s.replace(/&#x([0-9a-fA-F]+);/g, (_, h) => String.fromCodePoint(parseInt(h, 16))).replace(/&#([0-9]+);/g, (_, d) => String.fromCodePoint(Number(d))).replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const attrs = (s) => Object.fromEntries([...s.matchAll(/([\w:.-]+)="([^"]*)"/g)].map((m) => [m[1], unescape(m[2])]));
const oneLine = (s) => s.replace(/[\t\r\n]+/g, ' ').trim();

function trx(dir) {
  let total = 0, passed = 0, failed = 0;
  const badOutcomes = [];
  const failures = new Map();
  const files = fs.existsSync(dir) ? fs.readdirSync(dir).filter((f) => f.endsWith('.trx')).sort() : [];
  for (const f of files) {
    const text = fs.readFileSync(path.join(dir, f), 'utf8');
    const summary = text.match(/<ResultSummary\b([^>]*)>/);
    const outcome = summary ? attrs(summary[1]).outcome : undefined;
    if (outcome !== undefined && outcome !== 'Completed' && outcome !== 'Failed') badOutcomes.push(`${f}=${outcome}`);
    const counters = text.match(/<Counters\b([^>]*)\/>/);
    if (counters) {
      const a = attrs(counters[1]);
      total += Number(a.total || 0);
      passed += Number(a.passed || 0);
      failed += Number(a.failed || 0);
    }
    const methods = new Map();
    for (const m of text.matchAll(/<UnitTest\b([^>]*)>([\s\S]*?)<\/UnitTest>/g)) {
      const id = attrs(m[1]).id;
      const tm = m[2].match(/<TestMethod\b([^>]*)\/>/);
      if (id && tm) {
        const t = attrs(tm[1]);
        methods.set(id, { name: `${t.className}.${t.name}`, project: path.basename((t.codeBase || '').replace(/\\/g, '/'), '.dll') });
      }
    }
    for (const m of text.matchAll(/<UnitTestResult\b([^>]*?)(\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
      const a = attrs(m[1]);
      if (a.outcome !== 'Failed') continue;
      const method = methods.get(a.testId);
      if (!method || failures.has(method.name)) continue;
      const message = (m[3] || '').match(/<Message>([\s\S]*?)<\/Message>/);
      failures.set(method.name, { ...method, error: message ? oneLine(unescape(message[1]).split('\n')[0]) : '' });
    }
  }
  console.log(`COUNTS ${total} ${passed} ${failed}`);
  console.log(`FILES ${files.length}`);
  for (const b of badOutcomes) console.log(`BADOUTCOME ${b}`);
  for (const [name, f] of failures) console.log(`FAIL ${name}\t${f.project}\t${f.error}`);
}

const rows = (file) => (file && fs.existsSync(file) ? fs.readFileSync(file, 'utf8').split('\n').filter((l) => l.length > 0).map((l) => l.split('\t')) : []);

function opts(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i += 2) o[argv[i].replace(/^--/, '')] = argv[i + 1];
  return o;
}

// the suite workers tests/run-all.sh starts by default: two thirds of the cpus up to 12, and under a held grant of test tokens no more
// than the grant
const runAllWorkers = () => {
  const ncpu = os.cpus().length;
  let j = Math.max(1, Math.min(Math.floor(2 * ncpu / 3), 12));
  if (process.env.AH_TEST_SLOT_HELD === '1') {
    const v = process.env.AH_TEST_TOKENS_GRANTED;
    if (/^[0-9]+$/.test(v || '') && Number(v) >= 1) j = Math.min(j, Number(v));
  }
  return j;
};
const fingerprint = () => ({
  hostname: os.hostname(),
  ncpu: os.cpus().length,
  in_session: Object.keys(process.env).some((k) => k === 'CLAUDECODE' || (k.startsWith('CODEX_') && k !== 'CODEX_HOME') || k.startsWith('HERDR_')),
  sandbox: null,
  workers: runAllWorkers(),
});
// why two fingerprints cannot be compared, or '' when they can
const envDiff = (b, h, bn, hn) => {
  if (b.hostname !== h.hostname) return `${bn} was run on host ${b.hostname}, ${hn} on ${h.hostname}`;
  if (b.in_session !== h.in_session) return `${bn} was run ${b.in_session ? 'inside' : 'outside'} an agent session, ${hn} ${h.in_session ? 'inside' : 'outside'} one`;
  if (b.sandbox !== h.sandbox) return `${bn} and ${hn} differ in the sandbox flag`;
  if (b.workers !== h.workers) return `${bn} was run with ${b.workers} workers, ${hn} with ${h.workers}`;
  return '';
};
const sha256 = (f) => { try { return crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex'); } catch { return ''; } };
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8'));
const keyOf = (r, f) => `${f.set ?? r.set}|${f.name}`;
// A failure of a stored base counts as a baseline when it failed twice, or is still-class: it failed at the base's own base and again
// there, which is stored with failed_twice false and its plain first error. A row with no first error, or one that was never rerun
// ("not-rerun: ..."), does not count.
const isBaseFailure = (f) => f.failed_twice === true || (typeof f.first_error === 'string' && f.first_error !== '' && !f.first_error.startsWith('not-rerun:'));
const suiteKeys = (r) => (r.suites ? r.suites.map((n) => (n.includes('|') ? n : `${r.set}|${n}`)) : null);

const [cmd, ...rest] = process.argv.slice(2);
if (cmd === 'trx') {
  trx(rest[0]);
} else if (cmd === 'firsterror') {
  // <log> [<kept diffs dir>]: a kept diff says more than the suite's FAIL line, which only names the diff. Prints nothing when neither tells.
  const text = (f) => { try { return fs.readFileSync(f, 'utf8'); } catch { return ''; } };
  const notPass = (l) => !/^(PASS|ok)\b/.test(l);
  let diffs = [];
  try { diffs = fs.readdirSync(rest[1], { recursive: true }).filter((f) => f.endsWith('.diff')).sort(); } catch { /* none kept */ }
  if (diffs.length) {
    const lines = text(`${rest[1]}/${diffs[0]}`).split('\n').filter((l) => l.trim() && notPass(l) && !/^(--- expected|\+\+\+ actual)$/.test(l));
    const real = lines.filter((l) => /^[-+]|^FAIL\b/.test(l));
    const more = diffs.length > 1 ? ` (+${diffs.length - 1} more)` : '';
    console.log(`${diffs[0].replace(/\.diff$/, '')}: ${(real.length ? real : lines).slice(0, 3).join(' | ')}`.replace(/\t/g, ' ').slice(0, 600) + more);
  } else {
    const lines = text(rest[0]).split('\n');
    const line = lines.find((l) => /^(FAIL|TIMEOUT)\b|^not ok\b/.test(l)) ?? lines.find((l) => /FAIL|error/i.test(l) && notPass(l)) ?? '';
    console.log(line.replace(/\t/g, ' ').slice(0, 300));
  }
} else if (cmd === 'result') {
  const o = opts(rest);
  const [total, passed, failed] = (o.counts || '0 0 0').split(' ').map(Number);
  console.log(JSON.stringify({
    sha: o.sha, set: o.set, status: o.status, started: o.started, ended: o.ended,
    failures: rows(o.failures).map(([name, project, error, twice]) => ({ name, project: project || '', first_error: error || '', failed_twice: twice === 'true' })),
    flaky: rows(o.flaky).map(([name, project]) => ({ name, project: project || '' })),
    log: o.log, counts: { total, passed, failed },
    ...(o.env ? { env: readJson(o.env) } : {}),
    ...(rows(o.suites).length ? { suites: rows(o.suites).map((r) => r[0]) } : {}),
  }, null, 2));
} else if (cmd === 'field') {
  try {
    const v = JSON.parse(fs.readFileSync(rest[0], 'utf8'))[rest[1]];
    if (v !== undefined) console.log(typeof v === 'string' ? v : JSON.stringify(v));
  } catch { /* an unreadable file prints nothing */ }
} else if (cmd === 'merge') {
  const files = [];
  const o = {};
  for (let i = 0; i < rest.length;) {
    if (rest[i].startsWith('--')) { o[rest[i].slice(2)] = rest[i + 1]; i += 2; } else { files.push(rest[i]); i += 1; }
  }
  const parts = files.map((f) => JSON.parse(fs.readFileSync(f, 'utf8')));
  const rank = { PASS: 0, 'FLAKY-PASS': 1, FAIL: 2, SYSTEMIC: 3, ERROR: 4 };
  const status = parts.reduce((s, p) => (rank[p.status] > rank[s] ? p.status : s), 'PASS');
  const sum = (k) => parts.reduce((n, p) => n + (p.counts?.[k] || 0), 0);
  console.log(JSON.stringify({
    sha: o.sha, set: o.set, status, started: o.started, ended: o.ended,
    failures: parts.flatMap((p) => (p.failures || []).map((f) => ({ ...f, set: p.set }))),
    flaky: parts.flatMap((p) => (p.flaky || []).map((f) => ({ ...f, set: p.set }))),
    log: o.log, counts: { total: sum('total'), passed: sum('passed'), failed: sum('failed') },
    components: Object.fromEntries(parts.map((p) => [p.set, p.status])),
    ...(o['affected-base'] ? { affected_base: o['affected-base'] } : {}),
    ...(o['not-run'] ? { not_run: o['not-run'].split(',') } : {}),
    // a set named not_run that still has a part ran only that part's suites
    ...(o['not-run'] && parts.some((p) => o['not-run'].split(',').includes(p.set))
      ? { ran_only: Object.fromEntries(parts.filter((p) => o['not-run'].split(',').includes(p.set)).map((p) => [p.set, p.suites || []])) } : {}),
    // the full result carries a fingerprint only when its parts were all made in the same one
    ...(parts.length && parts.every((p) => p.env && JSON.stringify(p.env) === JSON.stringify(parts[0].env)) ? { env: parts[0].env } : {}),
    ...(parts.every((p) => p.suites) ? { suites: parts.flatMap((p) => p.suites.map((n) => `${p.set}|${n}`)) } : {}),
  }, null, 2));
} else if (cmd === 'env') {
  console.log(JSON.stringify(fingerprint()));
} else if (cmd === 'envcmp') {
  try {
    const e = readJson(rest[0]).env;
    const here = fingerprint();
    if (!e) console.log('the stored result has no environment fingerprint');
    else { const d = envDiff(e, here, 'the stored result', 'this run'); if (d) console.log(d); }
  } catch { console.log('the stored result could not be read'); }
} else if (cmd === 'keys') {
  try {
    const r = readJson(rest[0]);
    for (const f of r.failures || []) if (isBaseFailure(f)) console.log(r.set === 'full' || f.set ? `${f.set}|${f.name}` : f.name);
  } catch { /* an unreadable file prints nothing */ }
} else if (cmd === 'compare') {
  const o = opts(rest);
  const out = (v) => { console.log(JSON.stringify({ new: [], fixed: [], still: [], flaky: [], added: [], removed: [], ...v }, null, 2)); process.exit(v.verdict === 'clean' ? 0 : v.verdict === 'new-failures' ? 4 : 5); };
  let head, base;
  try { head = readJson(o.head); } catch { out({ base: null, verdict: 'incomparable', reason: 'the head result could not be read' }); }
  try { base = readJson(o.base); } catch { out({ base: o.basesha ?? null, verdict: 'incomparable', reason: 'the base result could not be read' }); }
  const he = head.env, be = base.env;
  // --incomparable WHY: the caller found the base unfit to compare (one of its parts was made elsewhere or differently)
  let reason = o.incomparable || '';
  if (!reason) {
    if (!be) reason = 'the base result has no environment fingerprint';
    else if (!he) reason = 'the head result has no environment fingerprint';
    else reason = envDiff(be, he, 'the base', 'the head');
  }
  if (!reason && !['PASS', 'FLAKY-PASS', 'FAIL'].includes(base.status)) reason = `the base result is ${base.status}`;
  // a base failure is a baseline only when it failed again on its isolated rerun there
  const set = (r, only) => new Set((r.failures || []).filter((f) => !only || isBaseFailure(f)).map((f) => keyOf(r, f)));
  // what the head did not run says nothing: the base's failures and suites there are neither fixed nor removed
  const notRun = head.not_run || [];
  const ranOnly = head.ran_only || {};
  const ran = (k) => {
    const s = k.slice(0, k.indexOf('|'));
    return !notRun.includes(s) || (ranOnly[s] || []).includes(k.slice(k.indexOf('|') + 1));
  };
  const hf = set(head), bf = new Set([...set(base, true)].filter(ran));
  const list = (it) => [...it].map((k) => (head.set === 'full' ? k : k.slice(k.indexOf('|') + 1))).sort();
  const hs = suiteKeys(head), bs = suiteKeys(base)?.filter(ran) ?? null;
  const added = hs && bs ? list(hs.filter((k) => !bs.includes(k))) : [];
  const removed = hs && bs ? list(bs.filter((k) => !hs.includes(k))) : [];
  const verdict = {
    base: base.sha,
    verdict: reason ? 'incomparable' : [...hf].some((k) => !bf.has(k)) ? 'new-failures' : 'clean',
    ...(reason ? { reason } : {}),
    new: reason ? [] : list([...hf].filter((k) => !bf.has(k))),
    fixed: reason ? [] : list([...bf].filter((k) => !hf.has(k) && !(hs && !hs.includes(k)))),
    still: reason ? [] : list([...hf].filter((k) => bf.has(k))),
    flaky: (head.flaky || []).map((f) => f.name).sort(),
    added: reason ? [] : added,
    removed: reason ? [] : removed,
    added_removed_known: Boolean(hs && bs),
    ...(notRun.length ? { not_run: notRun } : {}),
    ...(Object.keys(ranOnly).length ? { ran_only: ranOnly } : {}),
  };
  out(verdict);
} else if (cmd === 'verdictline') {
  const v = readJson(rest[0]);
  console.log(`run-set: verdict=${v.verdict} base=${String(v.base).slice(0, 7)} new=${v.new.length} fixed=${v.fixed.length} still=${v.still.length} flaky=${v.flaky.length}`);
  if (v.reason) console.log(`run-set: incomparable: ${v.reason}`);
  if (v.not_run) console.log(`run-set: did not run: ${v.not_run.join(', ')} (the diff touches no shell or script file)`);
  for (const [s, names] of Object.entries(v.ran_only || {})) console.log(`run-set: of ${s}, ran only: ${names.join(', ')}`);
  for (const n of v.new.slice(0, 30)) console.log(`  NEW ${n}`);
  if (v.new.length > 30) console.log(`  ... and ${v.new.length - 30} more`);
} else if (cmd === 'annotate') {
  const v = readJson(rest[2]);
  const { base, verdict, reason, ...classes } = v;
  console.log(JSON.stringify({ sha: rest[0], set: rest[1], base, verdict, ...(reason ? { verdict_reason: reason } : {}), classes, head_sha256: sha256(rest[3]), base_sha256: sha256(rest[4]) }, null, 2));
} else if (cmd === 'verdictcheck') {
  let why = '';
  try {
    const v = readJson(rest[0]);
    if (!v.head_sha256 || !v.base_sha256) why = 'the verdict records no result hashes';
    else if (v.head_sha256 !== sha256(rest[1])) why = 'the head result.json changed after the verdict';
    else if (v.base_sha256 !== sha256(rest[2])) why = 'the base result.json changed after the verdict';
  } catch { why = 'the verdict or a result file could not be read'; }
  console.log(why ? `stale: ${why}` : 'ok');
  process.exit(why ? 1 : 0);
} else if (cmd === 'affectedcheck') {
  // <result.json> <sha> <base>: ok when this green affected result is of that sha, narrowed by the diff from that base
  let why = '';
  try {
    const r = readJson(rest[0]);
    if (r.set !== 'affected') why = `the result is of the ${r.set} set`;
    else if (r.sha !== rest[1]) why = `the result is of ${r.sha}`;
    else if (r.affected_base !== rest[2]) why = `the result was narrowed against ${r.affected_base}`;
    else if (!['PASS', 'FLAKY-PASS'].includes(r.status)) why = `the result is ${r.status}`;
  } catch { why = 'the result could not be read'; }
  console.log(why ? `not accepted: ${why}` : 'ok');
  process.exit(why ? 1 : 0);
} else {
  console.error('usage: run-set-tools.mjs trx DIR | result ... | field FILE KEY | merge ... FILE... | env | envcmp FILE | keys FILE | compare ... | annotate SHA SET VERDICT HEAD BASE | verdictcheck VERDICT HEAD BASE | affectedcheck RESULT SHA BASE | verdictline FILE');
  process.exit(2);
}
