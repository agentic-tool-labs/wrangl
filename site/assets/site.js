// Wrangl docs: theme toggle, docs menu, copy buttons and search. No dependencies, no network beyond search.json.
(() => {
  const root = document.documentElement;
  const $ = (s, el = document) => el.querySelector(s);

  // Theme: follows the system until the reader picks one; the pick is kept per browser.
  const themeBtn = $('.theme-btn');
  const dark = () => (root.dataset.theme || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')) === 'dark';
  const syncTheme = () => themeBtn && themeBtn.setAttribute('aria-checked', String(dark()));
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

  // Easter egg: the landing page's mascot opens full screen, then turns into Canvas UI's ASCII Object. The renderer
  // (assets/ascii-object.js, three included) is fetched on the first click only; without WebGL 2 or the module, the
  // full-screen image stays. Motion is WAAPI on transform and opacity; the renderer's loop runs only while open.
  const egg = $('.mascot-btn');
  const asciiUrl = new URL('ascii-object.js', document.currentScript?.src || location.href).href;
  if (egg) {
    const pageImg = $('img', egg);
    const still = matchMedia('(prefers-reduced-motion: reduce)');
    const MOVE = 500, FADE = 600, EASE = 'cubic-bezier(0.2, 0, 0, 1)';
    let dlg, stage, img, tools, sizer, setBtn, mod = null, panel = null, ascii = null, run = 0, closing = false, downOnBackdrop = false;
    const build = () => {
      dlg = document.createElement('dialog');
      dlg.className = 'egg';
      dlg.setAttribute('aria-label', 'Wrangl mascot');
      dlg.innerHTML = '<div class="egg-veil"></div><div class="egg-view"><img alt=""></div><div class="egg-tools"><button type="button" class="btn egg-settings" aria-label="Render settings" aria-expanded="false" hidden>'
        + '<svg viewBox="0 0 9 9" aria-hidden="true" shape-rendering="crispEdges"><path d="M0 1h9v1H0zM0 4h9v1H0zM0 7h9v1H0zM5 0h2v3H5zM1 3h2v3H1zM4 6h2v3H4z"/></svg></button>'
        + '<button type="button" class="btn egg-close">Close</button></div>';
      document.body.append(dlg);
      stage = $('.egg-view', dlg);
      sizer = new ResizeObserver(() => mod?.refit(fit()));
      img = $('img', stage);
      img.src = pageImg.currentSrc || pageImg.src;
      img.alt = pageImg.alt;
      tools = $('.egg-tools', dlg);
      setBtn = $('.egg-settings', dlg);
      $('.egg-close', dlg).addEventListener('click', shut);
      // The backdrop is the overlay around the image; once the ASCII canvas fills it, clicks there orbit instead.
      const backdrop = t => t === dlg || t === stage || t.classList.contains('egg-veil');
      dlg.addEventListener('pointerdown', e => { downOnBackdrop = backdrop(e.target); });
      dlg.addEventListener('click', e => { if (downOnBackdrop && backdrop(e.target)) shut(); });
      // Escape closes the settings panel first, then the overlay.
      const esc = e => { e.preventDefault(); if (panel?.isOpen()) panel.close(); else shut(); };
      dlg.addEventListener('keydown', e => { if (e.key === 'Escape') esc(e); });
      dlg.addEventListener('cancel', esc);
      dlg.addEventListener('close', cleanup);
    };
    // The page mascot's box as a transform of the overlay image, for a FLIP move between the two.
    const flip = () => {
      const a = pageImg.getBoundingClientRect(), b = img.getBoundingClientRect();
      return `translate(${a.left - b.left}px, ${a.top - b.top}px) scale(${a.width / b.width})`;
    };
    // The canvas fills the overlay; the component keeps a fixed vertical field of view (widened on portrait canvases),
    // so this factor sizes the object to the image's box. Calibrated at a square canvas, where it is 1.
    const fit = () => {
      const w = stage.clientWidth, h = stage.clientHeight, a = w / h;
      return img.offsetWidth / (a >= 1 ? h : w / (1 + 0.25 * (1 - a)));
    };
    const webgl2 = () => { try { return !!document.createElement('canvas').getContext('webgl2'); } catch { return false; } };
    const loadAscii = token => new Promise(resolve => {
      if (!webgl2()) return resolve(null);
      import(asciiUrl).then(m => {
        mod = m;
        if (token !== run) return resolve(null);
        const canvas = document.createElement('canvas');
        canvas.className = 'egg-ascii';
        canvas.setAttribute('aria-hidden', 'true');
        stage.append(canvas);
        const css = getComputedStyle(document.documentElement);
        const tok = name => css.getPropertyValue(name).trim();
        const instance = m.createAsciiObject({ canvas }, {
          ...m.settings({ src: img.src, colored: true, color: tok('--ink'), highlight: tok('--accent'), bgColour: tok('--bg'),
            dracoDecoderPath: '', scale: 4.6, yOffset: -0.2 }, fit()), // measured, on the sliders' steps: the relief covers the image's pixels
          onLoad: () => requestAnimationFrame(() => resolve(token === run ? instance : null)),
          // A load failure: dispose at once, so the still image stays with no loop or canvas behind it.
          onError: () => { ascii?.instance.destroy(); canvas.remove(); ascii = null; resolve(null); },
        });
        if (!instance) { canvas.remove(); return resolve(null); }
        ascii = { instance, canvas };
      }).catch(() => resolve(null));
    });
    const open = () => {
      if (dlg?.open) return;
      if (!dlg) build();
      const token = ++run;
      closing = false;
      dlg.showModal();
      const loaded = loadAscii(token);
      let moved;
      if (still.matches) moved = dlg.animate({ opacity: [0, 1] }, { duration: 200 }).finished;
      else {
        pageImg.style.visibility = 'hidden';
        $('.egg-veil', dlg).animate({ opacity: [0, 1] }, { duration: MOVE, easing: 'linear' });
        tools.animate({ opacity: [0, 1] }, { duration: MOVE, easing: 'linear' });
        moved = img.animate({ transform: [flip(), 'none'] }, { duration: MOVE, easing: EASE }).finished;
      }
      Promise.all([moved, loaded]).then(([, instance]) => {
        if (!instance || token !== run || closing) return;
        ascii.canvas.animate({ opacity: [0, 1] }, { duration: FADE, fill: 'forwards' });
        panel ??= mod.createPanel(dlg, setBtn);
        panel.attach(instance);
        sizer.observe(stage);
        setBtn.hidden = false;
        setBtn.animate({ opacity: [0, 1] }, { duration: FADE });
        img.animate({ opacity: [1, 0] }, { duration: FADE, fill: 'forwards' });
      }).catch(() => {});
    };
    // Dispose the renderer (its loop stops with it) and take its canvas out.
    const drop = () => { sizer.disconnect(); panel?.detach(); setBtn.hidden = true; ascii?.instance.destroy(); ascii = null; for (const c of stage.querySelectorAll('canvas')) c.remove(); };
    function cleanup() {
      run++;
      drop();
      for (const a of img.getAnimations()) a.cancel();
      pageImg.style.visibility = '';
      closing = false;
      if (dlg.open) dlg.close();
      egg.focus();
    }
    function shut() {
      if (!dlg?.open || closing) return;
      closing = true;
      drop();
      for (const a of img.getAnimations()) a.cancel();
      const done = still.matches ? dlg.animate({ opacity: [1, 0] }, { duration: 200 }).finished
        : Promise.all([
          img.animate({ transform: ['none', flip()] }, { duration: MOVE, easing: EASE, fill: 'forwards' }).finished,
          $('.egg-veil', dlg).animate({ opacity: [1, 0] }, { duration: MOVE, easing: 'linear', fill: 'forwards' }).finished,
          tools.animate({ opacity: [1, 0] }, { duration: MOVE / 2, fill: 'forwards' }).finished,
        ]);
      done.then(() => { for (const el of [dlg, tools, $('.egg-veil', dlg)]) for (const a of el.getAnimations()) a.cancel(); dlg.close(); }, () => dlg.close());
    }
    egg.addEventListener('click', open);
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
