// node --test site/  — the generator's link rewriting and its privacy check.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { rewriteHref, privacyHits, slugify } from './build.mjs';

const files = new Set(['LICENSE', 'agent-hierarchy-v2/docs/architecture.md', 'agent-hierarchy-v2/skills']);
const ctx = {
  repo: 'https://github.com/agentic-tool-labs/wrangl', branch: 'main',
  slugOf: new Map([['agent-hierarchy-v2/README.md', 'overview'], ['agent-hierarchy-v2/docs/getting-started.md', 'getting-started']]),
  exists: t => files.has(t), isDir: t => t === 'agent-hierarchy-v2/skills',
};
const from = 'agent-hierarchy-v2/docs/how-it-works.md';
const href = h => rewriteHref(h, from, ctx);

test('a link to a page in the manifest becomes that page, keeping the anchor', () => {
  assert.equal(href('getting-started.md').href, 'getting-started.html');
  assert.equal(href('./getting-started.md#1-what-you-need').href, 'getting-started.html#1-what-you-need');
  assert.equal(href('../README.md#ah2-and-ah-v1').href, 'overview.html#ah2-and-ah-v1');
  assert.equal(href('..').href, 'overview.html', 'a folder links to its README page');
});

test('a link to another repo file goes to the repository, and a missing one is flagged', () => {
  assert.deepEqual([href('architecture.md').href, href('architecture.md').problem],
    ['https://github.com/agentic-tool-labs/wrangl/blob/main/agent-hierarchy-v2/docs/architecture.md', null]);
  assert.equal(href('../../LICENSE').href, 'https://github.com/agentic-tool-labs/wrangl/blob/main/LICENSE');
  assert.equal(href('../skills/').href, 'https://github.com/agentic-tool-labs/wrangl/tree/main/agent-hierarchy-v2/skills');
  assert.equal(href('gone.md').problem, 'no such file in the repository');
  assert.equal(href('../../../x.md').problem, 'points outside the repository');
});

test('anchors and absolute URLs are left alone', () => {
  for (const h of ['#the-team', 'https://example.org/a.md', 'mailto:x', '//cdn.example/x.md']) assert.equal(href(h).href, h);
});

test('heading slugs match GitHub', () => {
  assert.equal(slugify('1. What you need'), '1-what-you-need');
  assert.equal(slugify('ah2 and ah v1'), 'ah2-and-ah-v1');
  assert.equal(slugify('The browser page (ahd ui [--print] [--no-open])'), 'the-browser-page-ahd-ui---print---no-open');
});

test('the privacy check catches home paths and email addresses', () => {
  const names = t => privacyHits(t).map(h => h.name);
  assert.deepEqual(names('see /Users/someone/git/x'), ['home path']);
  assert.deepEqual(names('mail someone@example.com'), ['email address']);
  assert.deepEqual(names('/plugin install wrangl@wrangl and ah@agent-tools, ~/.claude/ah2'), []);
});
