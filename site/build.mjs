#!/usr/bin/env node
// Builds the Wrangl documentation site. site/docs.json is the only list of pages: it names each Markdown source,
// its slug, title and summary. Output is a static site in site/dist/ with relative URLs only, so it works from any
// subpath (GitHub Pages project sites) and from disk. After writing, the build checks every internal link and anchor,
// and fails if the output carries anything private (see PRIVATE). Usage: node site/build.mjs [--links]
import { readFileSync, writeFileSync, mkdirSync, rmSync, existsSync, statSync, cpSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { Marked, Renderer, TextRenderer } from 'marked';

const SITE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.dirname(SITE);
const DIST = path.join(SITE, 'dist');

const ENTITIES = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'", '&nbsp;': ' ' };
export const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const decode = s => s.replace(/&(amp|lt|gt|quot|#39|nbsp);/g, m => ENTITIES[m]);
const plain = html => decode(html.replace(/<\/?(?:p|li|ul|ol|td|th|tr|h\d|pre|div|br|blockquote)\b[^>]*>/g, ' ').replace(/<[^>]+>/g, ''))
  .replace(/\s+/g, ' ').trim();

// GitHub's heading slug, so anchors written against GitHub's rendering keep working here.
export const slugify = text => text.toLowerCase().trim().replace(/[^\p{L}\p{M}\p{N}\p{Pc} -]/gu, '').replace(/ /g, '-');

// Rewrites a link found in the Markdown file `from` (a repo-relative path). A link to a page in the manifest becomes
// that page's .html; a link to any other repo file becomes its URL on the repository; anchors and absolute URLs are
// left alone. `problem` is set when the target cannot exist on the public site.
export function rewriteHref(href, from, ctx) {
  if (!href || href.startsWith('#') || href.startsWith('//') || /^[a-z][a-z0-9+.-]*:/i.test(href)) return { href };
  const cut = href.indexOf('#');
  const frag = cut < 0 ? '' : href.slice(cut);
  let rel = cut < 0 ? href : href.slice(0, cut);
  try { rel = decodeURI(rel); } catch { /* keep as written */ }
  const target = path.posix.normalize(path.posix.join(path.posix.dirname(from), rel)).replace(/\/$/, '');
  if (target === '..' || target.startsWith('../')) return { href, target, problem: 'points outside the repository' };
  const slug = ctx.slugOf.get(target) ?? ctx.slugOf.get(path.posix.join(target, 'README.md'));
  if (slug) return { href: `${slug}.html${frag}`, target };
  if (target === '.') return { href: ctx.repo, target };
  const kind = ctx.isDir(target) ? 'tree' : 'blob';
  const url = `${ctx.repo}/${kind}/${ctx.branch}/${target.split('/').map(encodeURIComponent).join('/')}${frag}`;
  return { href: url, target, external: true, problem: ctx.exists(target) ? null : 'no such file in the repository' };
}

// Strings that must never reach the public site. The check runs over the built output.
export const PRIVATE = [
  { name: 'home path', re: /\/Users\/[^\s"'<>)]*/g },
  { name: 'email address', re: /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g },
];
export function privacyHits(text) {
  const hits = [];
  for (const { name, re } of PRIVATE) for (const m of text.matchAll(re)) hits.push({ name, match: m[0] });
  return hits;
}

const callout = (body) => {
  const gh = body.match(/^<p>\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*/);
  if (gh) {
    const word = gh[1][0] + gh[1].slice(1).toLowerCase();
    body = `<p><strong>${word}.</strong> ` + body.slice(gh[0].length);
  }
  const m = body.match(/^<p><strong>(Note|Tip|Important|Warning|Caution|Beta)\b[^<]*<\/strong>/i);
  if (!m) return null;
  const kind = /^(warning|caution|beta)$/i.test(m[1]) ? 'warn' : 'note';
  return `<div class="callout callout-${kind}" role="note">\n${body}</div>\n`;
};

function renderDoc(page, ctx) {
  const md = readFileSync(path.join(ROOT, page.source), 'utf8');
  const toc = [];
  const seen = new Map();
  const unique = base => { const n = seen.get(base) ?? 0; seen.set(base, n + 1); return n ? `${base}-${n}` : base; };
  let h1 = null;
  const link = href => {
    const r = rewriteHref(href, page.source, ctx);
    if (r.external) ctx.outbound.push({ from: page.source, href, url: r.href, problem: r.problem });
    else if (r.problem) ctx.outbound.push({ from: page.source, href, url: href, problem: r.problem });
    return r.href;
  };
  const media = src => {
    if (/^[a-z][a-z0-9+.-]*:|^\/\//i.test(src)) return src;
    const target = path.posix.normalize(path.posix.join(path.posix.dirname(page.source), src));
    if (!ctx.exists(target)) { ctx.outbound.push({ from: page.source, href: src, url: src, problem: 'image not found' }); return src; }
    ctx.media.add(target);
    return `media/${target}`;
  };
  // marked.use() copies own enumerable methods only, so the overrides are a plain object; `base` is the default.
  const base = Renderer.prototype;
  const renderer = {
    heading({ tokens, depth }) {
      const html = this.parser.parseInline(tokens);
      const text = decode(this.parser.parseInline(tokens, new TextRenderer()));
      const id = unique(slugify(text));
      if (depth === 1 && h1 === null) { h1 = html; return ''; }
      if (depth <= 3) toc.push({ depth, id, html: html.replace(/<\/?a\b[^>]*>/g, '') });
      return `<h${depth} id="${id}">${html}<a class="anchor" href="#${id}" aria-label="Link to this section: ${esc(text)}">#</a></h${depth}>\n`;
    },
    link(token) { return base.link.call(this, { ...token, href: link(token.href) }); },
    image(token) { return base.image.call(this, { ...token, href: media(token.href) }); },
    html(token) { return token.text.replace(/(<img\b[^>]*?\bsrc=")([^"]+)"/g, (_, pre, src) => `${pre}${media(src)}"`); },
    table(token) { return `<div class="table-wrap" tabindex="0" role="group" aria-label="Table">${base.table.call(this, token)}</div>\n`; },
    code({ text, lang, escaped }) {
      const cls = lang ? ` class="language-${esc(lang.split(/\s/)[0])}"` : '';
      return `<div class="code"><button type="button" class="copy" hidden>Copy</button><pre tabindex="0"><code${cls}>${escaped ? text : esc(text)}</code></pre></div>\n`;
    },
    blockquote({ tokens }) {
      const body = this.parser.parse(tokens);
      return callout(body) ?? `<blockquote>\n${body}</blockquote>\n`;
    },
  };
  const body = new Marked({ gfm: true }).use({ renderer }).parse(md);
  const sections = body.split(/(?=<h[23] id=")/).map(part => {
    const m = part.match(/^<h[23] id="([^"]+)">([\s\S]*?)<a class="anchor"/);
    return { a: m ? m[1] : '', h: m ? plain(m[2]) : '', x: plain(m ? part.replace(/^<h[23][\s\S]*?<\/h[23]>/, '') : part) };
  });
  return { h1: h1 ?? esc(page.title), body, toc, sections };
}

const MOON = '<svg class="icon" viewBox="0 0 8 8" aria-hidden="true" shape-rendering="crispEdges"><path d="M3 0h3v1H3zM2 1h2v1H2zM1 2h2v4H1zM2 6h2v1H2zM3 7h3v1H3zM6 6h1v1H6z"/></svg>';

function layout({ site, repo, title, description, body, navHtml, isDoc }) {
  const pageTitle = title ? `${title} – ${site}` : site;
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(pageTitle)}</title>
<meta name="description" content="${esc(description)}">
<meta name="color-scheme" content="light dark">
<link rel="icon" href="assets/wrangl.svg" type="image/svg+xml">
<link rel="stylesheet" href="assets/site.css">
<script>try{var t=localStorage.getItem('wrangl-theme');if(t==='light'||t==='dark')document.documentElement.dataset.theme=t}catch(e){}document.documentElement.classList.add('js')</script>
<script src="assets/site.js" defer></script>
</head>
<body class="${isDoc ? 'is-doc' : 'is-home'}">
<a class="skip" href="#main">Skip to content</a>
<header class="masthead">
  <a class="brand" href="index.html"><img src="assets/wrangl.svg" alt="" width="32" height="32"><span>Wrangl</span></a>
  ${isDoc ? '<button class="menu-btn" type="button" aria-expanded="false" aria-controls="docs-nav">Menu</button>' : ''}
  <div class="search" role="search">
    <label class="visually-hidden" for="q">Search the docs</label>
    <input id="q" type="search" placeholder="Search the docs" autocomplete="off" spellcheck="false" aria-describedby="q-key" aria-controls="q-results">
    <kbd id="q-key" title="Press / to search"><span class="visually-hidden">Press </span>/</kbd>
    <div id="q-results" class="results" hidden></div>
    <p id="q-status" class="visually-hidden" role="status"></p>
  </div>
  <button class="theme-btn" type="button" aria-pressed="false">${MOON}<span>Dark theme</span></button>
</header>
${isDoc ? `<div class="shell">
<nav id="docs-nav" class="sidebar" aria-label="Docs">
${navHtml}
</nav>
<main id="main" tabindex="-1">
${body}
</main>
</div>` : `<main id="main" tabindex="-1">
${body}
</main>`}
<footer class="colophon">
  <p>Wrangl: Agent Hierarchy is licensed under Apache-2.0. Source and issues: <a href="${esc(repo)}">${esc(repo.replace(/^https:\/\//, ''))}</a>.</p>
</footer>
</body>
</html>
`;
}

function navFor(manifest, current) {
  return manifest.sections.map((s, i) => `<div class="nav-group">
<p class="nav-title" id="nav-s${i}">${esc(s.title)}</p>
<ul aria-labelledby="nav-s${i}">
${s.pages.map(p => `<li><a href="${p.slug}.html"${p.slug === current ? ' aria-current="page"' : ''}>${esc(p.title)}</a></li>`).join('\n')}
</ul>
</div>`).join('\n');
}

function docBody(page, doc, prev, next) {
  const toc = doc.toc.length >= 2 ? `<nav class="toc" aria-labelledby="toc-title">
<p class="toc-title" id="toc-title">On this page</p>
<ul>
${doc.toc.map(t => `<li class="toc-${t.depth}"><a href="#${t.id}">${t.html}</a></li>`).join('\n')}
</ul>
</nav>` : '';
  const pager = [prev && `<a class="pager-prev" rel="prev" href="${prev.slug}.html"><span class="pager-dir">Previous</span><span class="pager-name">${esc(prev.title)}</span></a>`,
    next && `<a class="pager-next" rel="next" href="${next.slug}.html"><span class="pager-dir">Next</span><span class="pager-name">${esc(next.title)}</span></a>`].filter(Boolean).join('\n');
  return `<article class="doc">
<header class="doc-head"><h1>${doc.h1}</h1></header>
${toc}
<div class="prose">
${doc.body}</div>
${pager ? `<nav class="pager" aria-label="Previous and next page">\n${pager}\n</nav>` : ''}
</article>`;
}

function landing(manifest, pages) {
  const bySlug = new Map(pages.map(p => [p.slug, p]));
  const sections = manifest.sections.map(s => `<div class="map-group">
<h3>${esc(s.title)}</h3>
<ul>
${s.pages.map(p => `<li><a href="${p.slug}.html">${esc(p.title)}</a><span>${esc(p.summary)}</span></li>`).join('\n')}
</ul>
</div>`).join('\n');
  return readFileSync(path.join(SITE, 'landing.html'), 'utf8').replace(/<!--[\s\S]*?-->\s*/, '')
    .replace(/\{\{href:([a-z0-9-]+)(#[^}]*)?\}\}/g, (_, slug, frag = '') => {
      if (!bySlug.has(slug)) throw new Error(`landing.html links to unknown slug "${slug}"`);
      return `${slug}.html${frag}`;
    })
    .replace(/\{\{repo:([^}]+)\}\}/g, (_, file) => {
      if (!existsSync(path.join(ROOT, file))) throw new Error(`landing.html links to missing repo file "${file}"`);
      return `${manifest.repo}/blob/${manifest.branch}/${file}`;
    })
    .replace('{{sections}}', sections);
}

function checkLinks(files) {
  const ids = new Map();
  for (const [file, html] of files) if (file.endsWith('.html')) ids.set(file, new Set([...html.matchAll(/\sid="([^"]+)"/g)].map(m => m[1])));
  const broken = [];
  for (const [file, html] of files) {
    if (!file.endsWith('.html')) continue;
    for (const [, url] of html.matchAll(/\s(?:href|src)="([^"]*)"/g)) {
      if (/^[a-z][a-z0-9+.-]*:|^\/\//i.test(url)) continue;
      const cut = url.indexOf('#');
      const rel = decodeURI(cut < 0 ? url : url.slice(0, cut));
      const frag = cut < 0 ? '' : decodeURIComponent(url.slice(cut + 1));
      const target = rel ? path.posix.normalize(path.posix.join(path.posix.dirname(file), rel)) : file;
      if (!files.has(target) && !existsSync(path.join(DIST, target))) { broken.push(`${file}: ${url} (no such file)`); continue; }
      if (frag && target.endsWith('.html') && !ids.get(target)?.has(frag)) broken.push(`${file}: ${url} (no such anchor)`);
    }
  }
  return broken;
}

function main() {
  const manifest = JSON.parse(readFileSync(path.join(SITE, 'docs.json'), 'utf8'));
  const pages = manifest.sections.flatMap(s => s.pages.map(p => ({ ...p, section: s.title })));
  const slugs = new Set();
  for (const p of pages) {
    if (!/^[a-z0-9-]+$/.test(p.slug) || p.slug === 'index' || slugs.has(p.slug)) throw new Error(`bad or duplicate slug "${p.slug}"`);
    slugs.add(p.slug);
    if (!existsSync(path.join(ROOT, p.source))) throw new Error(`docs.json names a missing file: ${p.source}`);
  }
  const ctx = {
    repo: manifest.repo, branch: manifest.branch,
    slugOf: new Map(pages.map(p => [p.source, p.slug])),
    exists: t => existsSync(path.join(ROOT, t)),
    isDir: t => { try { return statSync(path.join(ROOT, t)).isDirectory(); } catch { return false; } },
    outbound: [], media: new Set(),
  };

  rmSync(DIST, { recursive: true, force: true });
  mkdirSync(DIST, { recursive: true });
  const files = new Map();
  const index = [];
  pages.forEach((page, i) => {
    const doc = renderDoc(page, ctx);
    const html = layout({ site: manifest.site, repo: manifest.repo, title: page.title, description: page.summary, isDoc: true,
      navHtml: navFor(manifest, page.slug), body: docBody(page, doc, pages[i - 1], pages[i + 1]) });
    files.set(`${page.slug}.html`, html);
    for (const s of doc.sections) index.push({ p: page.title, s: page.slug, h: s.h, a: s.a, x: s.x });
  });
  files.set('index.html', layout({ site: manifest.site, repo: manifest.repo, title: '', isDoc: false, navHtml: '',
    description: 'Wrangl is a Claude Code plugin that turns one Claude Code session into a small software team, coordinated by a local daemon.',
    body: landing(manifest, pages) }));
  files.set('search.json', JSON.stringify(index));
  for (const [file, text] of files) writeFileSync(path.join(DIST, file), text);
  cpSync(path.join(SITE, 'assets'), path.join(DIST, 'assets'), { recursive: true });
  cpSync(path.join(ROOT, manifest.mascot), path.join(DIST, 'assets', 'wrangl.svg'));
  for (const m of ctx.media) { mkdirSync(path.dirname(path.join(DIST, 'media', m)), { recursive: true }); cpSync(path.join(ROOT, m), path.join(DIST, 'media', m)); }

  let failed = false;
  const broken = checkLinks(files);
  if (broken.length) { failed = true; console.error(`Broken internal links (${broken.length}):\n  ${broken.join('\n  ')}`); }

  const leaks = [];
  const walk = dir => readdirSync(dir, { withFileTypes: true }).forEach(e => {
    const f = path.join(dir, e.name);
    if (e.isDirectory()) return walk(f);
    for (const h of privacyHits(readFileSync(f, 'utf8'))) leaks.push({ file: path.relative(DIST, f), ...h });
  });
  walk(DIST);
  if (leaks.length) {
    failed = true;
    console.error('Private strings in the built site. Fix them in the source docs:');
    for (const l of leaks) {
      const where = pages.flatMap(p => readFileSync(path.join(ROOT, p.source), 'utf8').split('\n')
        .map((line, n) => line.includes(l.match) ? `${p.source}:${n + 1}` : null).filter(Boolean));
      console.error(`  ${l.name} "${l.match}" in dist/${l.file}; source: ${where.join(', ') || 'not in a source doc'}`);
    }
  }

  const problems = ctx.outbound.filter(o => o.problem);
  for (const o of problems) console.warn(`warning: ${o.from}: link "${o.href}" ${o.problem}`);
  if (process.argv.includes('--links')) for (const o of ctx.outbound) console.log(`outbound: ${o.from}: ${o.href} -> ${o.url}`);
  if (failed) process.exit(1);
  console.log(`Built ${pages.length} pages and the landing page into ${path.relative(ROOT, DIST)}/ (${index.length} search entries, ${ctx.outbound.length} links to repository files).`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
