// SentinelDesk console: the few things a Blazor Server page needs the browser for
// (theme, file downloads, clipboard, keyboard shortcuts, element positions and Leaflet maps).
window.sd = (function () {
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  // Keys a component wants to handle itself: <button data-prevent-keys="ArrowDown ArrowUp Enter Space"> stops the browser default.
  document.addEventListener('keydown', (e) => {
    const el = e.target && e.target.closest ? e.target.closest('[data-prevent-keys]') : null;
    if (!el) return;
    const keys = el.getAttribute('data-prevent-keys').split(' ');
    const k = e.key === ' ' ? 'Space' : e.key;
    if (keys.includes(k)) e.preventDefault();
  }, true);

  // Focus elements marked data-autofocus as soon as they are rendered (modals, palette, forms).
  const focusNew = (root) => {
    const el = root.querySelector ? (root.matches && root.matches('[data-autofocus]') ? root : root.querySelector('[data-autofocus]')) : null;
    if (el && !el.dataset.focused) {
      el.dataset.focused = '1';
      setTimeout(() => { el.focus(); if (el.dataset.autofocus === 'select' && el.select) el.select(); }, 0);
    }
  };
  new MutationObserver((list) => { for (const m of list) for (const n of m.addedNodes) if (n.nodeType === 1) focusNew(n); })
    .observe(document.documentElement, { childList: true, subtree: true });

  const maps = new Map();
  let mapSeq = 0;

  function tiles(cfg) {
    return cfg.tileUrl || `https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_${cfg.dark ? 'Dark' : 'Light'}_Gray_Base/MapServer/tile/{z}/{y}/{x}`;
  }

  function tileLayer(cfg, maxZoom) {
    // The console sends "Referrer-Policy: no-referrer"; tile servers require a Referer, so send the origin only.
    return L.tileLayer(tiles(cfg), { maxNativeZoom: 16, maxZoom: maxZoom || 18, attribution: cfg.attribution, referrerPolicy: 'strict-origin-when-cross-origin' });
  }

  // ── Branch map (overview): marker size = devices, ring = share online; small dots = located devices.
  function drawBranches(m) {
    const cfg = m.cfg;
    m.layer.clearLayers();
    const max = Math.max(1, ...cfg.branches.map((b) => b.total));
    for (const b of cfg.branches) {
      const ratio = b.total ? b.online / b.total : 0;
      const color = ratio >= 0.8 ? 'var(--good)' : ratio >= 0.5 ? 'var(--warning)' : 'var(--critical)';
      const mk = L.circleMarker([b.lat, b.lng], { radius: 8 + (b.total / max) * 14, color, weight: 3, fillColor: 'var(--series-1)', fillOpacity: 0.55 });
      const body = m.offline ? `<div class="tabular">${b.online}/${b.total}</div>` : `<div>${b.online}/${b.total} online · ${esc(b.todayText)} today</div>`;
      mk.bindTooltip(`<div class="text-xs"><div class="font-semibold">${esc(b.name)}</div>${body}</div>`,
        { direction: m.offline ? 'right' : 'top', offset: m.offline ? [10, 0] : [0, -8], permanent: m.offline, className: m.offline ? 'sd-map-label' : '' });
      mk.on('click', () => m.ref.invokeMethodAsync('SelectBranch', b.id));
      mk.addTo(m.layer);
    }
    for (const d of cfg.devices || []) {
      let ring = null;
      const mk = L.circleMarker([d.lat, d.lng], { radius: 5, color: cfg.dark ? '#0a0a0a' : '#ffffff', weight: 1.5, fillColor: d.online ? 'var(--good)' : 'var(--muted)', fillOpacity: 1 });
      mk.bindTooltip(`<div class="text-xs"><div class="font-semibold">${esc(d.hostname)}</div><div>${esc(d.line2)}</div><div class="text-muted">${esc(d.line3)}</div></div>`, { direction: 'top', offset: [0, -6] });
      mk.on('click', () => m.ref.invokeMethodAsync('SelectDevice', d.id));
      mk.on('mouseover', () => { if (d.accuracyM > 0) ring = L.circle([d.lat, d.lng], { radius: d.accuracyM, color: 'var(--series-2)', weight: 1, dashArray: '4 4', fillOpacity: 0.08, interactive: false }).addTo(m.layer); });
      mk.on('mouseout', () => { if (ring) { m.layer.removeLayer(ring); ring = null; } });
      mk.addTo(m.layer);
    }
  }

  function goOffline(m) {
    if (m.offline) return;
    m.offline = true;
    if (m.tiles) { m.map.removeLayer(m.tiles); m.tiles = null; }
    if (m.map.attributionControl) m.map.attributionControl.remove();
    m.note.style.display = '';
    drawBranches(m);
  }

  function setBranchTiles(m) {
    if (m.offline) return;
    if (m.tiles) m.map.removeLayer(m.tiles);
    const stats = { ok: 0, err: 0 };
    m.tiles = tileLayer(m.cfg);
    m.tiles.on('tileload', () => { stats.ok++; });
    m.tiles.on('tileerror', () => { stats.err++; if (stats.err >= 4 && stats.ok === 0) goOffline(m); });
    m.tiles.addTo(m.map);
  }

  function branchMap(el, cfg, ref) {
    const id = ++mapSeq;
    const offline = cfg.tileUrl === 'none';
    const map = L.map(el, { center: [7.75, 80.7], zoom: 7, minZoom: 6, maxBounds: [[4.5, 78], [11, 83.5]], scrollWheelZoom: false, attributionControl: !offline });
    const note = document.createElement('div');
    note.className = 'pointer-events-none absolute bottom-2 right-3 z-500 rounded-md bg-panel/90 px-2 py-1 text-[10px] text-muted';
    note.textContent = 'Offline map · basemap unavailable';
    note.style.display = offline ? '' : 'none';
    el.parentElement.appendChild(note);
    const m = { map, cfg, ref, offline, note, layer: L.layerGroup().addTo(map), tiles: null, dark: cfg.dark };
    maps.set(id, m);
    setBranchTiles(m);
    drawBranches(m);
    return id;
  }

  function updateBranchMap(id, cfg) {
    const m = maps.get(id);
    if (!m) return;
    const themeChanged = m.dark !== cfg.dark;
    m.cfg = cfg; m.dark = cfg.dark;
    if (themeChanged) setBranchTiles(m);
    drawBranches(m);
  }

  // ── Device location: current fix with accuracy radius, movement trail and recent points.
  function drawLocation(m) {
    const cfg = m.cfg;
    m.layer.clearLayers();
    const pts = cfg.trail.map((p) => [p.lat, p.lng]);
    if (pts.length > 1) L.polyline(pts, { color: 'var(--series-1)', weight: 3, opacity: 0.8 }).addTo(m.layer);
    cfg.trail.slice(0, -1).forEach((p) => L.circleMarker([p.lat, p.lng], { radius: 3, color: 'var(--series-1)', weight: 1, fillOpacity: 0.7 }).bindTooltip(esc(p.tip)).addTo(m.layer));
    if (cfg.current) {
      const c = cfg.current;
      if (c.accuracyM > 0) L.circle([c.lat, c.lng], { radius: c.accuracyM, interactive: false, color: 'var(--series-2)', weight: 1, dashArray: '4 4', fillOpacity: 0.1 }).addTo(m.layer);
      L.circleMarker([c.lat, c.lng], { radius: 8, color: cfg.dark ? '#0a0a0a' : '#ffffff', weight: 2, fillColor: 'var(--brand)', fillOpacity: 1 })
        .bindTooltip(esc(c.tip), { permanent: true, direction: 'top', offset: [0, -8] }).addTo(m.layer);
    }
    const fit = pts.length > 1 ? pts : cfg.current ? [[cfg.current.lat, cfg.current.lng]] : [];
    const key = fit.map((p) => p.join(',')).join(';');
    if (key !== m.fitKey) {
      m.fitKey = key;
      if (fit.length === 1) m.map.setView(fit[0], 15);
      else if (fit.length > 1) m.map.fitBounds(fit, { padding: [30, 30], maxZoom: 16 });
    }
  }

  function locationMap(el, cfg) {
    const id = ++mapSeq;
    const first = cfg.current ? [cfg.current.lat, cfg.current.lng] : cfg.trail.length ? [cfg.trail[cfg.trail.length - 1].lat, cfg.trail[cfg.trail.length - 1].lng] : [7.75, 80.7];
    const map = L.map(el, { center: first, zoom: 15, scrollWheelZoom: false });
    const m = { map, cfg, layer: L.layerGroup().addTo(map), tiles: tileLayer(cfg, 19).addTo(map), dark: cfg.dark, fitKey: null };
    maps.set(id, m);
    drawLocation(m);
    return id;
  }

  function updateLocationMap(id, cfg) {
    const m = maps.get(id);
    if (!m) return;
    if (m.dark !== cfg.dark) { m.map.removeLayer(m.tiles); m.tiles = tileLayer(cfg, 19).addTo(m.map); m.dark = cfg.dark; }
    m.cfg = cfg;
    drawLocation(m);
  }

  function disposeMap(id) {
    const m = maps.get(id);
    if (!m) return;
    m.map.remove();
    if (m.note) m.note.remove();
    maps.delete(id);
  }

  let shortcutRef = null;
  document.addEventListener('keydown', (e) => {
    if (!shortcutRef || !(e.ctrlKey || e.metaKey)) return;
    const k = e.key.toLowerCase();
    if (k === 'k' || k === 'b') { e.preventDefault(); shortcutRef.invokeMethodAsync('Shortcut', k); }
  });

  return {
    env: () => ({
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      language: navigator.language,
      dark: document.documentElement.classList.contains('dark'),
    }),
    setTheme: (d) => {
      document.documentElement.classList.toggle('dark', d);
      document.documentElement.dataset.theme = d ? 'dark' : 'light';
      try { localStorage.setItem('sd-theme', d ? 'dark' : 'light'); } catch { /* private mode */ }
    },
    getItem: (k) => { try { return localStorage.getItem(k); } catch { return null; } },
    setItem: (k, v) => { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
    isDesktop: () => window.matchMedia('(min-width: 1024px)').matches,
    shortcuts: (ref) => { shortcutRef = ref; },
    copy: async (text) => { try { await navigator.clipboard.writeText(text); return true; } catch { return false; } },
    download: async (name, type, streamRef) => {
      const buf = await streamRef.arrayBuffer();
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob([buf], { type }));
      a.download = name;
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    },
    // Where a drop-down list should open (below, or above when there is no room).
    // need = expected height of the pop-up; popWidth keeps wide pop-ups (calendar) inside the window.
    place: (el, need, popWidth) => {
      const r = el.getBoundingClientRect();
      const h = window.innerHeight;
      const room = need || 240;
      const up = h - r.bottom < room && r.top > h - r.bottom;
      const left = popWidth ? Math.max(8, Math.min(r.left, window.innerWidth - popWidth - 8)) : r.left;
      return { left, top: up ? null : r.bottom + 4, bottom: up ? h - r.top + 4 : null, width: r.width };
    },
    scrollActive: (el) => {
      if (!el) return;
      for (const a of el.querySelectorAll('[data-active="true"]')) {
        const box = a.parentElement;
        if (box && box.scrollHeight > box.clientHeight) box.scrollTop = a.offsetTop - box.clientHeight / 2 + a.offsetHeight / 2;
        else a.scrollIntoView({ block: 'nearest' });
      }
    },
    focus: (el) => { if (el) el.focus(); },
    branchMap, updateBranchMap, locationMap, updateLocationMap, disposeMap,
  };
})();
