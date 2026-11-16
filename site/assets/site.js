// Wrangl docs: theme toggle, docs menu, copy buttons and search. No dependencies, no network beyond search.json.
(() => {
  const root = document.documentElement;
  const $ = (s, el = document) => el.querySelector(s);

  // Theme: follows the system until the reader picks one; the pick is kept per browser.
  const themeBtn = $('.theme-btn');
  const dark = () => (root.dataset.theme || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')) === 'dark';
  const syncTheme = () => themeBtn && themeBtn.setAttribute('aria-pressed', String(dark()));
  syncTheme();
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', syncTheme);
  themeBtn?.addEventListener('click', () => {
    root.dataset.theme = dark() ? 'light' : 'dark';
    try { localStorage.setItem('wrangl-theme', root.dataset.theme); } catch { /* private mode: not kept */ }
    syncTheme();
  });

  // Docs menu on narrow screens.
  const menuBtn = $('.menu-btn');
  const nav = $('#docs-nav');
  menuBtn?.addEventListener('click', () => {
    const open = menuBtn.getAttribute('aria-expanded') !== 'true';
    menuBtn.setAttribute('aria-expanded', String(open));
    nav.classList.toggle('open', open);
    if (open) nav.querySelector('a')?.focus();
  });

  // Copy buttons on code blocks.
  const status = $('#q-status');
  const say = msg => { if (status) { status.textContent = ''; requestAnimationFrame(() => { status.textContent = msg; }); } };
  if (navigator.clipboard) for (const btn of document.querySelectorAll('.copy')) {
    btn.hidden = false;
    btn.setAttribute('aria-label', 'Copy code');
    btn.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(btn.nextElementSibling.innerText.replace(/\n$/, ''));
        btn.textContent = 'Copied'; say('Copied to the clipboard');
      } catch { btn.textContent = 'Failed'; say('Could not copy'); }
      setTimeout(() => { btn.textContent = 'Copy'; }, 1600);
    });
  }

  // Search over search.json, loaded on first use.
  const input = $('#q');
  const box = $('#q-results');
  if (!input) return;
  let index = null;
  let loading = null;
  const load = () => loading ??= fetch('search.json').then(r => r.json()).then(rows => {
    index = rows.map(r => ({ ...r, hay: `${r.p} ${r.h} ${r.x}`.toLowerCase(), hl: `${r.p} ${r.h}`.toLowerCase() }));
  }).catch(() => { index = []; });
  const escHtml = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  const reEsc = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const highlight = (text, terms) => escHtml(text).replace(new RegExp(`(${terms.map(t => reEsc(escHtml(t))).join('|')})`, 'gi'), '<mark>$1</mark>');
  const snippet = (text, terms) => {
    const low = text.toLowerCase();
    const at = Math.min(...terms.map(t => { const i = low.indexOf(t); return i < 0 ? Infinity : i; }));
    if (!isFinite(at)) return text.slice(0, 140);
    const start = Math.max(0, at - 50);
    return (start ? '…' : '') + text.slice(start, start + 150) + (start + 150 < text.length ? '…' : '');
  };
  const close = () => { box.hidden = true; };
  const run = async () => {
    const q = input.value.trim().toLowerCase();
    if (!q) { close(); say(''); return; }
    await load();
    const terms = q.split(/\s+/).filter(Boolean);
    const hits = index.filter(r => terms.every(t => r.hay.includes(t)))
      .map(r => ({ r, score: (r.hay.includes(q) ? 20 : 0) + (r.hl.includes(q) ? 20 : 0)
        + terms.reduce((n, t) => n + (r.hl.includes(t) ? 10 : 0) + (r.h.toLowerCase().startsWith(t) ? 5 : 0), 0) }))
      .sort((a, b) => b.score - a.score).slice(0, 12);
    box.innerHTML = hits.length
      ? `<ul>${hits.map(({ r }) => `<li><a href="${r.s}.html${r.a ? `#${r.a}` : ''}"><span class="r-title">${highlight(r.h || r.p, terms)}${r.h ? ` <span class="r-page">in ${escHtml(r.p)}</span>` : ''}</span><span class="r-snip">${highlight(snippet(r.x, terms), terms)}</span></a></li>`).join('')}</ul>`
      : `<p class="r-empty">Nothing matches “${escHtml(input.value.trim())}”. Try fewer words.</p>`;
    box.hidden = false;
    say(hits.length ? `${hits.length} result${hits.length === 1 ? '' : 's'}. Press Down to reach them.` : 'No results');
  };
  let timer;
  input.addEventListener('focus', load, { once: true });
  input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(run, 120); });
  input.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { const a = box.querySelector('a'); if (a && !box.hidden) { e.preventDefault(); a.focus(); } }
    else if (e.key === 'Escape') { if (!box.hidden) close(); else input.value = ''; }
    else if (e.key === 'Enter') { const a = box.querySelector('a'); if (a && !box.hidden) { e.preventDefault(); a.click(); } }
  });
  box.addEventListener('keydown', e => {
    const links = [...box.querySelectorAll('a')];
    const i = links.indexOf(document.activeElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); links[Math.min(i + 1, links.length - 1)]?.focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); (i <= 0 ? input : links[i - 1]).focus(); }
    else if (e.key === 'Escape') { close(); input.focus(); }
  });
  document.addEventListener('click', e => { if (!e.target.closest('.search')) close(); });
  document.addEventListener('keydown', e => {
    if (e.key !== '/' || e.metaKey || e.ctrlKey || e.altKey) return;
    const t = e.target;
    if (t.closest?.('input, textarea, select, [contenteditable]')) return;
    e.preventDefault();
    input.focus();
    input.select();
  });
})();
