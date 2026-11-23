#!/usr/bin/env node
// Rebuilds the mascot easter egg's committed bundle (egg/ascii-object.js) from the private Canvas UI source in
// vendor/canvas-ui/ and egg/ascii-egg.mjs, bundled with three. Run by hand when those change: npm run build:egg.
// The public repo gets the bundle, its licence and the inputs' hash (egg/ascii-object.sha256), never the source.
import { readFileSync, writeFileSync, readdirSync, cpSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const EGG = path.dirname(fileURLToPath(import.meta.url));
const SITE = path.dirname(EGG);
export const VENDOR = path.join(SITE, 'vendor', 'canvas-ui');
export const BUNDLE = path.join(EGG, 'ascii-object.js');
export const HASH = path.join(EGG, 'ascii-object.sha256');

const installed = dep => JSON.parse(readFileSync(path.join(SITE, 'node_modules', dep, 'package.json'), 'utf8')).version;

// One hash over every file under vendor/canvas-ui/, egg/ascii-egg.mjs and the built bundle itself, in a fixed order,
// then the installed three and esbuild versions: a changed input, a hand-edited bundle or a version bump all show.
export function inputsHash() {
  const files = readdirSync(VENDOR, { recursive: true, withFileTypes: true }).filter(e => e.isFile())
    .map(e => path.join(e.parentPath ?? e.path, e.name)).concat(path.join(EGG, 'ascii-egg.mjs'), BUNDLE)
    .map(f => path.relative(SITE, f).split(path.sep).join('/')).sort();
  const h = createHash('sha256');
  for (const f of files) h.update(`${f}\0`).update(readFileSync(path.join(SITE, f))).update('\0');
  for (const dep of ['three', 'esbuild']) h.update(`${dep}@${installed(dep)}\0`);
  return h.digest('hex');
}

async function main() {
  const { buildSync } = await import('esbuild');
  const three = installed('three');
  buildSync({ entryPoints: [path.join(EGG, 'ascii-egg.mjs')], outfile: BUNDLE, bundle: true, format: 'esm', minify: true,
    legalComments: 'none', logLevel: 'warning',
    banner: { js: `/*! Canvas UI ASCII Object (c) 2026 David Haz, MIT + Commons Clause: see canvas-ui-LICENSE.md.\n`
      + ` * three.js ${three} (c) 2010-2026 three.js authors, MIT: https://github.com/mrdoob/three.js/blob/dev/LICENSE */` } });
  cpSync(path.join(VENDOR, 'LICENSE.md'), path.join(EGG, 'canvas-ui-LICENSE.md'));
  writeFileSync(HASH, `${inputsHash()}\n`);
  console.log(`Wrote ${path.relative(SITE, BUNDLE)} and its hash.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) await main();
