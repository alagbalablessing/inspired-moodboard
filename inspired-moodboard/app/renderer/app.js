// Inspired Moodboard board page.
"use strict";
const $ = (id) => document.getElementById(id);
const api = window.board;
const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;

const COLW = 240, GAP = 14, DEFAULT_ASPECT = 1.4;
const NOTE_LABELS = { prompts: "Image prompt", copy: "Copy (text in the design)", breakdown: "Shot breakdown" };

let root = null, boardName = "", items = [], byId = new Map(), mode = "masonry";
let stars = new Set(), positions = {}, view = { x: 0, y: 0, s: 1 }, sel = null, autoFit = true;
let settings = {}, collectionsCache = [];

const stage = $("stage"), world = $("world");
const thumbUrl = (p) => `board://thumb/?p=${encodeURIComponent(p)}`;
const fileUrl = (p) => `board://file/?p=${encodeURIComponent(p)}`;
const plural = (n, w) => `${n} ${w}${n === 1 ? "" : "s"}`;

// ---------- toast ----------
let toastT = 0;
function toast(text) {
  const t = $("toast");
  t.textContent = text;
  t.classList.add("show");
  clearTimeout(toastT);
  toastT = setTimeout(() => t.classList.remove("show"), 2200);
}

// ---------- colour ----------
function rgbToHsl(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min, s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [h * 60, s, l];
}
const hex = (c) => "#" + c.map((v) => Math.round(v).toString(16).padStart(2, "0")).join("").toUpperCase();

// Small k-means on a 48px copy of the thumbnail: palette plus a dominant colour for sorting.
function analyse(img) {
  try {
    const W = 48, H = Math.max(1, Math.round(48 * img.naturalHeight / img.naturalWidth));
    const cv = document.createElement("canvas"); cv.width = W; cv.height = H;
    const ctx = cv.getContext("2d", { willReadFrequently: true });
    ctx.drawImage(img, 0, 0, W, H);
    const d = ctx.getImageData(0, 0, W, H).data, px = [];
    for (let i = 0; i < d.length; i += 4) if (d[i + 3] > 128) px.push([d[i], d[i + 1], d[i + 2]]);
    const K = Math.min(5, px.length);
    let cents = Array.from({ length: K }, (_, k) => px[Math.floor((k + 0.5) * px.length / K)].slice());
    let counts = new Array(K).fill(0);
    for (let it = 0; it < 7; it++) {
      const sums = cents.map(() => [0, 0, 0]); counts = new Array(K).fill(0);
      for (const p of px) {
        let best = 0, bd = Infinity;
        for (let k = 0; k < K; k++) { const c = cents[k], dd = (p[0] - c[0]) ** 2 + (p[1] - c[1]) ** 2 + (p[2] - c[2]) ** 2; if (dd < bd) { bd = dd; best = k; } }
        sums[best][0] += p[0]; sums[best][1] += p[1]; sums[best][2] += p[2]; counts[best]++;
      }
      cents = cents.map((c, k) => counts[k] ? sums[k].map((v) => v / counts[k]) : c);
    }
    const clusters = cents.map((c, k) => ({ c, share: counts[k] / px.length, hsl: rgbToHsl(...c) })).filter((x) => x.share > 0.02).sort((a, b) => b.share - a.share);
    const dom = clusters.slice().sort((a, b) => b.share * (0.35 + b.hsl[1]) - a.share * (0.35 + a.hsl[1]))[0];
    return { palette: clusters.map((x) => ({ hex: hex(x.c), share: x.share, c: x.c })), dom: dom.hsl };
  } catch (_) { return null; }
}

function colourKey(it) {
  if (!it.colour) return 9e9;
  const [h, s, l] = it.colour.dom;
  if (s < 0.14 || l < 0.08 || l > 0.94) return 1000 + (1 - l) * 100; // neutrals last, light to dark
  return ((h + 15) % 360) * 2 + l; // reds first, round the wheel
}

// ---------- layout ----------
// Columns chosen so the whole board has roughly the window's shape, so Fit shows images big.
function columnsFor(n) {
  const W = stage.clientWidth || 1000, H = Math.max(200, (stage.clientHeight || 700) - 80);
  const avg = items.length ? items.reduce((s, it) => s + it.aspect, 0) / items.length : DEFAULT_ASPECT;
  return Math.max(2, Math.min(24, Math.round(Math.sqrt(n * avg * (W / H)))));
}

function masonry(list, spanOf) {
  const cols = columnsFor(list.length);
  const hts = new Array(cols).fill(0), out = {};
  for (const it of list) {
    const span = cols >= 3 ? Math.min(2, spanOf(it)) : 1;
    let c = 0;
    if (span === 1) { for (let i = 1; i < cols; i++) if (hts[i] < hts[c] - 0.5) c = i; }
    else { let best = Infinity; for (let i = 0; i <= cols - 2; i++) { const m = Math.max(hts[i], hts[i + 1]); if (m < best - 0.5) { best = m; c = i; } } }
    const w = span * COLW + (span - 1) * GAP, h = Math.round(w * it.aspect);
    const y = span === 1 ? hts[c] : Math.max(hts[c], hts[c + 1]);
    out[it.id] = { x: c * (COLW + GAP), y, w, h };
    for (let i = c; i < c + span; i++) hts[i] = y + h + GAP;
  }
  return out;
}

// Spectrum: sorted by colour, poured column by column so it reads left to right.
function byColour(list) {
  const sorted = list.slice().sort((a, b) => colourKey(a) - colourKey(b));
  const cols = columnsFor(list.length);
  const total = sorted.reduce((s, it) => s + COLW * it.aspect + GAP, 0), target = total / cols;
  const out = {}; let c = 0, y = 0;
  for (const it of sorted) {
    const h = Math.round(COLW * it.aspect);
    if (y > 0 && y + h / 2 > target && c < cols - 1) { c++; y = 0; }
    out[it.id] = { x: c * (COLW + GAP), y, w: COLW, h };
    y += h + GAP;
  }
  return out;
}

function computeLayout(m) {
  if (m === "colour") return byColour(items);
  if (m === "mosaic") {
    const any = items.some((it) => stars.has(it.id));
    return masonry(items, (it) => (any ? stars.has(it.id) : items.indexOf(it) % 7 === 0) ? 2 : 1);
  }
  if (m === "free") {
    const out = {};
    let maxX = -Infinity, minY = Infinity;
    for (const it of items) { const p = positions[it.id]; if (p) { out[it.id] = { x: p.x, y: p.y, w: p.w, h: Math.round(p.w * it.aspect) }; maxX = Math.max(maxX, p.x + p.w); minY = Math.min(minY, p.y); } }
    const loose = items.filter((it) => !out[it.id]);
    if (loose.length) {
      const base = masonry(loose, () => 1), x0 = maxX === -Infinity ? 0 : maxX + GAP * 4, y0 = minY === Infinity ? 0 : minY;
      for (const it of loose) { const b = base[it.id]; out[it.id] = { ...b, x: b.x + x0, y: b.y + y0 }; }
    }
    return out;
  }
  return masonry(items, () => 1);
}

function place(it, box) {
  it.box = box;
  it.el.style.width = box.w + "px";
  it.el.style.height = box.h + "px";
  it.el.style.transform = `translate(${box.x}px, ${box.y}px)`;
}

function relayout() {
  const lay = computeLayout(mode);
  for (const it of items) if (lay[it.id]) place(it, lay[it.id]);
  if (mode === "free") for (const it of items) positions[it.id] = { x: it.box.x, y: it.box.y, w: it.box.w };
  if (autoFit) fit(true);
  saveSoon();
}

// ---------- view ----------
let movingT = 0;
function moving() {
  document.body.classList.add("moving");
  clearTimeout(movingT);
  movingT = setTimeout(() => document.body.classList.remove("moving"), 160);
}
let viewRaf = 0;
function viewSoon() { if (!viewRaf) viewRaf = requestAnimationFrame(() => { viewRaf = 0; applyView(false); }); }

function applyView(animate) {
  world.style.transition = animate && !reduced ? "transform .6s cubic-bezier(.2,.8,.2,1)" : "none";
  world.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.s})`;
  const g = $("gridBg"), size = 24 * view.s;
  g.style.backgroundSize = `${size}px ${size}px`;
  g.style.backgroundPosition = `${view.x % size}px ${view.y % size}px`;
  $("zoomOut").textContent = Math.round(view.s * 100) + "%";
}

function bounds(list = items) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const it of list) { if (!it.box) continue; x0 = Math.min(x0, it.box.x); y0 = Math.min(y0, it.box.y); x1 = Math.max(x1, it.box.x + it.box.w); y1 = Math.max(y1, it.box.y + it.box.h); }
  return x0 === Infinity ? null : { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

function fitTo(b, animate, maxScale = 1.6) {
  if (!b) return;
  const W = stage.clientWidth, H = stage.clientHeight;
  const infoW = $("info").classList.contains("open") && W > 760 ? 340 : 0;
  const pad = W < 520 ? 16 : 40, bottom = document.body.classList.contains("float") ? 16 : 70;
  const s = Math.min((W - infoW - pad * 2) / b.w, (H - pad - bottom) / b.h, maxScale);
  view.s = Math.max(0.03, s);
  view.x = (W - infoW - b.w * view.s) / 2 - b.x * view.s;
  view.y = pad + (H - pad - bottom - b.h * view.s) / 2 - b.y * view.s;
  applyView(animate);
}
function fit(animate) { fitTo(bounds(), animate); }

function zoomAt(factor, cx, cy) {
  const s = Math.min(6, Math.max(0.03, view.s * factor));
  const k = s / view.s;
  view.x = cx - (cx - view.x) * k; view.y = cy - (cy - view.y) * k; view.s = s;
  autoFit = false;
  moving();
  viewSoon();
  saveSoon();
}

// ---------- board ----------
// Background work runs between frames so zooming and dragging stay smooth.
const idleQueue = [];
let idleOn = false;
function idle(fn) {
  idleQueue.push(fn);
  if (idleOn) return;
  idleOn = true;
  const run = (dl) => {
    while (idleQueue.length && (!dl || dl.timeRemaining() > 4)) idleQueue.shift()();
    if (idleQueue.length) (window.requestIdleCallback || setTimeout)(run, { timeout: 500 }); else idleOn = false;
  };
  (window.requestIdleCallback || setTimeout)(run, { timeout: 500 });
}
let shapeT = 0;
function shapeChanged() { clearTimeout(shapeT); shapeT = setTimeout(() => { if (items.length && mode !== "free") relayout(); }, 400); }
let palT = 0;
function paletteSoon() { clearTimeout(palT); palT = setTimeout(renderPalette, 300); }

function makeItem(img) {
  const known = img.w && img.h ? Math.min(3, Math.max(0.3, img.h / img.w)) : 0;
  const it = { id: img.rel, path: img.path, name: img.name, notes: img.notes || {}, link: img.link || "", aspect: known || DEFAULT_ASPECT, known: !!known, colour: null, box: null };
  const el = document.createElement("div");
  el.className = "item";
  el.dataset.id = it.id;
  const im = new Image();
  im.crossOrigin = "anonymous";
  im.decoding = "async";
  im.alt = it.name;
  it.ready = new Promise((res) => {
    im.onload = () => {
      const a = Math.min(3, Math.max(0.3, im.naturalHeight / im.naturalWidth));
      if (isFinite(a) && (!it.known || Math.abs(a - it.aspect) / it.aspect > 0.08)) { it.aspect = a; it.known = true; shapeChanged(); }
      idle(() => { it.colour = analyse(im); paletteSoon(); if (sel === it) openInfo(it); });
      res();
    };
    im.onerror = () => res();
  });
  im.src = thumbUrl(it.path);
  el.append(im);
  el.addEventListener("dragstart", (e) => { e.preventDefault(); api.startDrag(it.path); });
  it.el = el;
  if (stars.has(it.id)) el.classList.add("star");
  return it;
}

async function loadBoard(data) {
  if (!data) return;
  root = data.root; boardName = data.name;
  const lay = data.layout || {};
  mode = ["masonry", "colour", "mosaic", "free"].includes(lay.mode) ? lay.mode : "masonry";
  stars = new Set(lay.stars || []); positions = lay.positions || {};
  closeInfo(); sel = null;
  world.textContent = ""; items = []; byId.clear();
  for (const img of data.images) { const it = makeItem(img); items.push(it); byId.set(it.id, it); }
  $("empty").hidden = true;
  $("toolbar").hidden = false;
  $("boardName").textContent = boardName;
  $("boardCount").textContent = plural(items.length, "image");
  setModeUI();
  renderSide();
  if (!items.length) { toast("No images in this folder yet"); return; }
  const layout = computeLayout(mode);
  // Deal the board out of a stack, like opening an album.
  const b = boundsOf(layout);
  autoFit = true;
  fitTo(b, false);
  const cx = b.x + b.w / 2, cy = b.y + b.h / 2;
  document.body.classList.add("no-anim");
  items.forEach((it, i) => {
    const box = layout[it.id];
    it.el.style.width = box.w + "px"; it.el.style.height = box.h + "px";
    const r = (i % 2 ? 1 : -1) * (4 + (i * 7) % 14);
    it.el.style.transform = `translate(${cx - box.w / 2}px, ${cy - box.h / 2}px) rotate(${r}deg) scale(.6)`;
    it.el.style.opacity = "0";
    it.el.style.transitionDelay = reduced ? "0ms" : `${Math.min(i, 40) * 18}ms`;
    world.append(it.el);
  });
  void world.offsetWidth;
  document.body.classList.remove("no-anim");
  requestAnimationFrame(() => {
    items.forEach((it) => { it.el.style.opacity = "1"; place(it, layout[it.id]); });
    setTimeout(() => items.forEach((it) => { it.el.style.transitionDelay = ""; }), 1500);
  });
  if (lay.view && lay.view.s) { view = lay.view; autoFit = false; applyView(true); }
  paletteSoon();
}

function boundsOf(layout) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const k in layout) { const b = layout[k]; x0 = Math.min(x0, b.x); y0 = Math.min(y0, b.y); x1 = Math.max(x1, b.x + b.w); y1 = Math.max(y1, b.y + b.h); }
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

// New saves from the extension appear while the board is open.
async function refreshBoard() {
  if (!root) return;
  const data = await api.reload(root);
  if (!data) return;
  const fresh = new Map(data.images.map((i) => [i.rel, i]));
  const gone = items.filter((it) => !fresh.has(it.id));
  for (const it of gone) { it.el.style.opacity = "0"; setTimeout(() => it.el.remove(), 500); byId.delete(it.id); if (sel === it) closeInfo(); }
  items = items.filter((it) => fresh.has(it.id));
  const added = [];
  for (const img of data.images) {
    const old = byId.get(img.rel);
    if (old) { old.notes = img.notes || {}; old.link = img.link || ""; continue; }
    const it = makeItem(img); items.push(it); byId.set(it.id, it); added.push(it);
  }
  items.sort((a, b) => a.id.localeCompare(b.id, undefined, { numeric: true }));
  $("boardCount").textContent = plural(items.length, "image");
  if (added.length) {
    const lay = computeLayout(mode);
    document.body.classList.add("no-anim");
    for (const it of added) { const b = lay[it.id]; place(it, b); it.el.style.transform += " scale(.4)"; it.el.style.opacity = "0"; world.append(it.el); }
    void world.offsetWidth;
    document.body.classList.remove("no-anim");
    requestAnimationFrame(() => { for (const it of items) { it.el.style.opacity = "1"; place(it, lay[it.id]); } if (autoFit) fit(true); });
    if (mode === "free") for (const it of added) positions[it.id] = { x: lay[it.id].x, y: lay[it.id].y, w: lay[it.id].w };
    toast(`${plural(added.length, "new image")} added`);
    renderPalette();
    saveSoon();
  } else if (gone.length) relayout();
  if (sel && byId.has(sel.id)) openInfo(sel);
  loadCollections();
}

let saveT = 0;
function saveSoon() {
  if (!root) return;
  clearTimeout(saveT);
  saveT = setTimeout(() => api.saveLayout(root, { version: 1, mode, stars: [...stars], positions: mode === "free" ? positions : {}, view: autoFit ? null : view }), 600);
}

// ---------- modes ----------
function setModeUI() {
  const btns = [...document.querySelectorAll("#modes button")];
  for (const b of btns) b.setAttribute("aria-selected", b.dataset.mode === mode);
  const on = btns.find((b) => b.dataset.mode === mode);
  const ind = document.querySelector(".seg-ind");
  if (on && on.offsetWidth) { ind.style.width = on.offsetWidth + "px"; ind.style.transform = `translateX(${on.offsetLeft}px)`; }
}
function setMode(m, opts = {}) {
  if (m === mode && !opts.force) return;
  if (m === "free" && opts.keep) for (const it of items) if (it.box) positions[it.id] = { x: it.box.x, y: it.box.y, w: it.box.w };
  mode = m;
  setModeUI();
  if (!opts.keep) { autoFit = true; relayout(); } else saveSoon();
}
document.querySelectorAll("#modes button").forEach((b) => b.addEventListener("click", () => setMode(b.dataset.mode)));

// ---------- selection + info ----------
function select(it) {
  if (sel && sel.el) sel.el.classList.remove("sel");
  sel = it;
  if (it) { it.el.classList.add("sel"); openInfo(it); } else closeInfo();
}

function openInfo(it) {
  const info = $("info");
  const img = $("infoImg");
  img.src = fileUrl(it.path);
  $("infoName").textContent = it.name;
  const sw = $("infoSwatches"); sw.textContent = "";
  for (const p of (it.colour ? it.colour.palette : [])) {
    const b = document.createElement("button");
    b.className = "swatch"; b.type = "button"; b.title = `Copy ${p.hex}`;
    b.innerHTML = `<i style="background:${p.hex}"></i>${p.hex}`;
    b.onclick = () => { api.copyText(p.hex); toast(`Copied ${p.hex}`); };
    sw.append(b);
  }
  $("starBtn").setAttribute("aria-pressed", stars.has(it.id));
  $("starBtn").textContent = stars.has(it.id) ? "Featured" : "Feature";
  $("linkBtn").hidden = !it.link;
  const notes = $("notes"); notes.textContent = "";
  const keys = Object.keys(NOTE_LABELS).filter((k) => it.notes[k]);
  if (!keys.length) {
    const p = document.createElement("p"); p.className = "none";
    p.textContent = "No AI notes for this image yet. Make an AI pack for this collection in Inspired Pin Saver and its prompt, copy and shot breakdown show up here.";
    notes.append(p);
  }
  keys.forEach((k, i) => {
    const d = document.createElement("details"); if (i === 0) d.open = true;
    const s = document.createElement("summary");
    const t = document.createElement("span"); t.textContent = NOTE_LABELS[k];
    const c = document.createElement("button"); c.type = "button"; c.textContent = "Copy";
    c.onclick = (e) => { e.preventDefault(); api.copyText(it.notes[k]); toast(`${NOTE_LABELS[k]} copied`); };
    const pre = document.createElement("pre"); pre.textContent = it.notes[k];
    s.append(t, c); d.append(s, pre); notes.append(d);
  });
  if (!info.classList.contains("open")) { info.classList.add("open"); info.setAttribute("aria-hidden", "false"); }
  else { img.style.animation = "none"; void img.offsetWidth; img.style.animation = ""; }
}
function closeInfo() {
  $("info").classList.remove("open"); $("info").setAttribute("aria-hidden", "true");
  if (sel && sel.el) sel.el.classList.remove("sel");
  sel = null;
}
$("infoClose").onclick = () => closeInfo();
$("infoImg").addEventListener("dragstart", (e) => { e.preventDefault(); if (sel) api.startDrag(sel.path); });
$("starBtn").onclick = () => {
  if (!sel) return;
  if (stars.has(sel.id)) stars.delete(sel.id); else stars.add(sel.id);
  sel.el.classList.toggle("star", stars.has(sel.id));
  openInfo(sel);
  if (mode === "mosaic") relayout(); else { saveSoon(); toast(stars.has(sel.id) ? "Featured: shows big in Mosaic" : "No longer featured"); }
};
$("copyImgBtn").onclick = () => copySelected();
$("showBtn").onclick = () => sel && api.showInFolder(sel.path);
$("linkBtn").onclick = () => sel && sel.link && api.openLink(sel.link);

async function copySelected() {
  if (!sel) return;
  const r = await api.copyImage(sel.path);
  if (r === "decode") {
    const img = $("infoImg");
    await img.decode().catch(() => {});
    const cv = document.createElement("canvas"); cv.width = img.naturalWidth; cv.height = img.naturalHeight;
    cv.getContext("2d").drawImage(img, 0, 0);
    await api.copyImageData(cv.toDataURL("image/png"));
  }
  toast("Image copied. Paste it into your design");
}

// ---------- pointer: select, move, pan, zoom ----------
let drag = null;
stage.addEventListener("pointerdown", (e) => {
  if (e.target.closest(".toolbar, .info, .palette, .empty, .drop")) return;
  if (e.button !== 0 && e.button !== 1) return;
  const el = e.target.closest(".item");
  if (el && e.ctrlKey) return; // native drag-out handles this
  const it = el ? byId.get(el.dataset.id) : null;
  drag = { it, sx: e.clientX, sy: e.clientY, moved: false, vx: view.x, vy: view.y, bx: it && it.box ? it.box.x : 0, by: it && it.box ? it.box.y : 0, pan: !it || e.button === 1 };
  stage.setPointerCapture(e.pointerId);
});
stage.addEventListener("pointermove", (e) => {
  if (!drag) return;
  const dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
  if (!drag.moved && Math.hypot(dx, dy) < 6) return;
  if (!drag.moved) {
    drag.moved = true;
    if (drag.pan) stage.classList.add("panning");
    else {
      if (mode !== "free") { setMode("free", { keep: true }); toast("Free layout: arrange it your way"); }
      drag.it.el.classList.add("dragging");
      world.append(drag.it.el); // bring to front
    }
  }
  if (drag.pan) { view.x = drag.vx + dx; view.y = drag.vy + dy; autoFit = false; moving(); viewSoon(); }
  else {
    const it = drag.it;
    it.box = { ...it.box, x: drag.bx + dx / view.s, y: drag.by + dy / view.s };
    if (!drag.raf) drag.raf = requestAnimationFrame(() => { if (drag) drag.raf = 0; it.el.style.transform = `translate(${it.box.x}px, ${it.box.y}px)`; });
  }
});
const endDrag = (e) => {
  if (!drag) return;
  const d = drag; drag = null;
  stage.classList.remove("panning");
  if (d.it && d.moved) { d.it.el.style.transform = `translate(${d.it.box.x}px, ${d.it.box.y}px)`; d.it.el.classList.remove("dragging"); positions[d.it.id] = { x: d.it.box.x, y: d.it.box.y, w: d.it.box.w }; saveSoon(); }
  else if (d.it && !d.moved) select(d.it);
  else if (!d.it && !d.moved && e.type === "pointerup") select(null);
  else if (d.pan && d.moved) saveSoon();
};
stage.addEventListener("pointerup", endDrag);
stage.addEventListener("pointercancel", endDrag);
stage.addEventListener("wheel", (e) => {
  if (e.target.closest(".info, .empty")) return;
  e.preventDefault();
  const r = stage.getBoundingClientRect();
  const dy = e.deltaMode === 1 ? e.deltaY * 33 : e.deltaY;
  wheelAcc.f *= Math.exp(-dy * (e.ctrlKey ? 0.01 : 0.0016));
  wheelAcc.x = e.clientX - r.left; wheelAcc.y = e.clientY - r.top;
  if (!wheelAcc.raf) wheelAcc.raf = requestAnimationFrame(() => { const w = wheelAcc; wheelAcc = { f: 1, raf: 0 }; zoomAt(w.f, w.x, w.y); });
}, { passive: false });
let wheelAcc = { f: 1, raf: 0 };
stage.addEventListener("dblclick", (e) => {
  if (e.target.closest(".toolbar, .info, .palette, .empty")) return;
  const el = e.target.closest(".item");
  if (el) { const it = byId.get(el.dataset.id); autoFit = false; fitTo(it.box, true, 3); }
  else { autoFit = true; fit(true); }
});

// Ctrl held: pictures become draggable into other apps.
const setCtrl = (on) => { document.body.classList.toggle("ctrl", on); for (const it of items) it.el.draggable = on; };
addEventListener("keydown", (e) => { if (e.key === "Control") setCtrl(true); });
addEventListener("keyup", (e) => { if (e.key === "Control") setCtrl(false); });
addEventListener("blur", () => setCtrl(false));

// ---------- keyboard ----------
addEventListener("keydown", (e) => {
  if (e.target.closest && e.target.closest("input, textarea")) return;
  const k = e.key.toLowerCase();
  if (e.ctrlKey && k === "o") { e.preventDefault(); openPicker(); return; }
  if (e.ctrlKey && e.shiftKey && k === "f") { e.preventDefault(); toggleFloat(); return; }
  if (e.ctrlKey && k === "c" && sel) { e.preventDefault(); copySelected(); return; }
  if (e.ctrlKey || e.altKey || e.metaKey) return;
  if (k === "f") { autoFit = true; fit(true); }
  else if (k === "t") toggleTop();
  else if (k === "escape") select(null);
  else if (k === "s" && sel) $("starBtn").click();
  else if (["1", "2", "3", "4"].includes(k) && items.length) setMode(["masonry", "colour", "mosaic", "free"][+k - 1]);
});

// ---------- toolbar ----------
$("fitBtn").onclick = () => { autoFit = true; fit(true); };
$("paletteBtn").onclick = () => {
  const on = $("paletteBtn").getAttribute("aria-pressed") !== "true";
  $("paletteBtn").setAttribute("aria-pressed", on);
  $("palette").hidden = !on;
  if (on) renderPalette();
};

function renderPalette() {
  const pal = $("palette");
  if (pal.hidden) return;
  const buckets = new Map();
  for (const it of items) for (const p of (it.colour ? it.colour.palette : [])) {
    const key = p.c.map((v) => v >> 5).join(",");
    const b = buckets.get(key) || { w: 0, c: [0, 0, 0] };
    b.w += p.share; for (let i = 0; i < 3; i++) b.c[i] += p.c[i] * p.share;
    buckets.set(key, b);
  }
  const top = [...buckets.values()].map((b) => ({ w: b.w, c: b.c.map((v) => v / b.w) })).sort((a, b) => b.w - a.w);
  const picked = [];
  for (const t of top) { if (picked.every((p) => Math.hypot(p.c[0] - t.c[0], p.c[1] - t.c[1], p.c[2] - t.c[2]) > 40)) picked.push(t); if (picked.length === 8) break; }
  pal.textContent = "";
  picked.forEach((p, i) => {
    const h = hex(p.c), b = document.createElement("button");
    b.type = "button"; b.style.background = h; b.title = `Copy ${h}`; b.style.animationDelay = `${i * 40}ms`;
    b.onclick = () => { api.copyText(h); toast(`Copied ${h}`); };
    pal.append(b);
  });
}

$("exportBtn").onclick = async () => {
  const b = bounds();
  if (!b) return;
  toast("Building your board image");
  const pad = 60, scale = Math.min(3, 6000 / Math.max(b.w, b.h));
  const cv = document.createElement("canvas");
  cv.width = Math.round(b.w * scale + pad * 2); cv.height = Math.round(b.h * scale + pad * 2);
  const ctx = cv.getContext("2d");
  ctx.fillStyle = "#121217"; ctx.fillRect(0, 0, cv.width, cv.height);
  const load = (src) => new Promise((res) => { const im = new Image(); im.crossOrigin = "anonymous"; im.onload = () => res(im); im.onerror = () => res(null); im.src = src; });
  const list = items.slice().sort((a, c) => (a.el.compareDocumentPosition(c.el) & Node.DOCUMENT_POSITION_FOLLOWING) ? -1 : 1);
  for (const it of list) {
    const im = await load(fileUrl(it.path)) || await load(thumbUrl(it.path));
    if (!im) continue;
    const x = pad + (it.box.x - b.x) * scale, y = pad + (it.box.y - b.y) * scale, w = it.box.w * scale, h = it.box.h * scale;
    ctx.save(); ctx.beginPath(); ctx.roundRect(x, y, w, h, 6 * scale); ctx.clip();
    const s = Math.max(w / im.naturalWidth, h / im.naturalHeight);
    ctx.drawImage(im, x + (w - im.naturalWidth * s) / 2, y + (h - im.naturalHeight * s) / 2, im.naturalWidth * s, im.naturalHeight * s);
    ctx.restore();
  }
  const saved = await api.exportBoard(cv.toDataURL("image/png"), boardName);
  if (saved) toast("Board image saved");
};

// ---------- window: on top, see-through, float ----------
async function toggleTop() {
  const on = $("topBtn").getAttribute("aria-pressed") !== "true";
  await api.setOnTop(on);
  $("topBtn").setAttribute("aria-pressed", on);
  toast(on ? "Stays on top of other apps" : "Back to a normal window");
}
$("topBtn").onclick = toggleTop;
$("opacity").addEventListener("input", (e) => { $("opacityOut").textContent = e.target.value + "%"; api.setOpacity(e.target.value / 100); });

async function toggleFloat() {
  const on = !document.body.classList.contains("float");
  await api.setFloat(on);
  document.body.classList.toggle("float", on);
  $("floatBtn").setAttribute("aria-pressed", on);
  if (on) { $("topBtn").setAttribute("aria-pressed", true); }
  else $("topBtn").setAttribute("aria-pressed", !!settings.onTop);
  setTimeout(() => { setModeUI(); if (items.length) { autoFit = true; fit(true); } }, 420);
}
$("floatBtn").onclick = toggleFloat;

// Small windows: the collections list tucks away so the board gets the room.
let wasNarrow = null;
function checkNarrow() {
  const narrow = innerWidth < 760;
  if (narrow !== wasNarrow) { document.body.classList.toggle("no-side", narrow); wasNarrow = narrow; }
}
addEventListener("resize", checkNarrow);
checkNarrow();

$("sideToggle").onclick = () => {
  document.body.classList.toggle("no-side");
  setTimeout(() => { if (autoFit && items.length) fit(true); }, 460);
};
let roRaf = 0;
new ResizeObserver(() => { if (roRaf) return; roRaf = requestAnimationFrame(() => { roRaf = 0; if (autoFit && items.length) fit(false); setModeUI(); }); }).observe(stage);

// ---------- side: collections + recent ----------
function colButton(c) {
  const b = document.createElement("button");
  b.type = "button"; b.className = "col" + (root === c.path ? " active" : "");
  b.title = c.path;
  const fan = document.createElement("span"); fan.className = "fan";
  for (const p of c.covers.slice(0, 3).reverse()) { const im = new Image(); im.src = thumbUrl(p); im.alt = ""; fan.append(im); }
  const txt = document.createElement("span");
  const n = document.createElement("b"); n.textContent = c.name;
  const sm = document.createElement("small"); sm.textContent = plural(c.count, "image");
  txt.append(n, sm);
  b.append(fan, txt);
  b.onclick = async () => loadBoard(await api.openFolder(c.path));
  return b;
}
function renderSide() {
  const list = $("colList"); list.textContent = "";
  if (!collectionsCache.length) { const p = document.createElement("p"); p.className = "none"; p.textContent = "Collections you save with Inspired Pin Saver appear here."; list.append(p); }
  for (const c of collectionsCache) list.append(colButton(c));
  const ec = $("emptyCols"); ec.textContent = "";
  for (const c of collectionsCache.slice(0, 6)) ec.append(colButton(c));
  const rl = $("recentList"); rl.textContent = "";
  const recent = (settings.recent || []).filter((r) => !collectionsCache.some((c) => c.path === r));
  if (!recent.length) { const p = document.createElement("p"); p.className = "none"; p.textContent = "Folders you open show up here."; rl.append(p); }
  for (const r of recent) {
    const b = document.createElement("button"); b.className = "recent"; b.type = "button"; b.title = r;
    b.textContent = r.split(/[\\/]/).filter(Boolean).pop();
    b.onclick = async () => { const d = await api.openFolder(r); if (d) loadBoard(d); else toast("That folder isn't there any more"); };
    rl.append(b);
  }
}
async function loadCollections() { const r = await api.collections(); collectionsCache = r.list || []; renderSide(); }
addEventListener("focus", () => loadCollections());

async function openPicker() { const d = await api.pickFolder(); if (d) { settings.recent = [d.root, ...(settings.recent || []).filter((x) => x !== d.root)]; loadBoard(d); } }
$("openBtn").onclick = openPicker;
$("emptyOpen").onclick = openPicker;

// ---------- drop a folder ----------
let dropDepth = 0;
addEventListener("dragenter", (e) => { if (e.dataTransfer && [...e.dataTransfer.types].includes("Files")) { dropDepth++; document.body.classList.add("dropping"); } });
addEventListener("dragleave", () => { if (--dropDepth <= 0) { dropDepth = 0; document.body.classList.remove("dropping"); } });
addEventListener("dragover", (e) => e.preventDefault());
addEventListener("drop", async (e) => {
  e.preventDefault(); dropDepth = 0; document.body.classList.remove("dropping");
  const f = e.dataTransfer.files[0];
  if (!f) return;
  const p = api.pathForFile(f);
  if (p) { const d = await api.openFolder(p); if (d) loadBoard(d); }
});

api.onFolderChanged((r) => { if (r === root) refreshBoard(); });
api.onOpenPath(async (p) => { const d = await api.openFolder(p); if (d) loadBoard(d); });

// ---------- start ----------
(async () => {
  const s = await api.init();
  settings = s.settings || {};
  document.body.classList.toggle("mac", s.platform === "darwin");
  collectionsCache = (s.collections && s.collections.list) || [];
  $("topBtn").setAttribute("aria-pressed", !!settings.onTop);
  $("opacity").value = Math.round((settings.opacity || 1) * 100);
  $("opacityOut").textContent = $("opacity").value + "%";
  if (settings.float) { document.body.classList.add("float"); $("floatBtn").setAttribute("aria-pressed", true); }
  renderSide();
  setModeUI();
  applyView(false);
  // Pick up where you left off.
  const last = (settings.recent || [])[0];
  if (last) { const d = await api.openFolder(last); if (d) loadBoard(d); }
})();
