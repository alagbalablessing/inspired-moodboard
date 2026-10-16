// Inspired Moodboard: a desktop board for the folders Inspired Pin Saver makes.
// Open any folder of images, arrange it into a moodboard, float it on top of
// Photoshop or Figma, and drag images straight out into your design.

const { app, BrowserWindow, ipcMain, dialog, protocol, net, shell, clipboard, nativeImage, screen } = require("electron");
const path = require("path");
const fs = require("fs");
const fsp = fs.promises;
const crypto = require("crypto");
const { pathToFileURL } = require("url");

const IMAGE_EXT = new Set([".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif"]);
const THUMBABLE = new Set([".jpg", ".jpeg", ".png", ".bmp"]); // formats Electron can resize itself
const SIDE_DIRS = ["prompts", "copy", "breakdown"];
const SKIP_DIRS = new Set([...SIDE_DIRS, "node_modules", ".git"]);
const BOARD_FILE = ".inspired-board.json";

protocol.registerSchemesAsPrivileged([
  { scheme: "board", privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true, stream: true } }
]);

let win = null;
let settings = { recent: [], onTop: false, opacity: 1, bounds: null, float: false, normalBounds: null };
const allowedRoots = new Set();
let watcher = null;
let thumbDir = "";

const settingsFile = () => path.join(app.getPath("userData"), "settings.json");
async function loadSettings() {
  try { settings = { ...settings, ...JSON.parse(await fsp.readFile(settingsFile(), "utf8")) }; } catch (_) {}
}
let saveTimer = null;
function saveSettings() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => fsp.writeFile(settingsFile(), JSON.stringify(settings, null, 2)).catch(() => {}), 300);
}

const pinterestRoot = () => path.join(app.getPath("downloads"), "Pinterest");
const inside = (p, root) => { const r = path.relative(root, p); return r === "" || (!r.startsWith("..") && !path.isAbsolute(r)); };
const allowed = (p) => [...allowedRoots, pinterestRoot()].some((r) => inside(path.resolve(p), r));

// ---------- reading folders ----------

async function scanImages(root, dir = root, depth = 0, out = []) {
  let entries = [];
  try { entries = await fsp.readdir(dir, { withFileTypes: true }); } catch (_) { return out; }
  for (const e of entries) {
    if (out.length >= 3000) break;
    if (e.name.startsWith(".")) continue;
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (depth < 3 && !SKIP_DIRS.has(e.name.toLowerCase())) await scanImages(root, full, depth + 1, out);
    } else if (IMAGE_EXT.has(path.extname(e.name).toLowerCase())) {
      try {
        const st = await fsp.stat(full);
        out.push({ path: full, name: path.basename(e.name, path.extname(e.name)), file: e.name, rel: path.relative(root, full), size: st.size, mtime: st.mtimeMs });
      } catch (_) {}
    }
  }
  return out;
}

// Reads width and height from the file header (no decoding), so the board can be laid out
// correctly before any thumbnail arrives. Handles JPEG, PNG, GIF, WebP and BMP.
function sizeFromHeader(b) {
  if (b.length < 26) return null;
  if (b[0] === 0x89 && b[1] === 0x50) return { w: b.readUInt32BE(16), h: b.readUInt32BE(20) };
  if (b.toString("ascii", 0, 3) === "GIF") return { w: b.readUInt16LE(6), h: b.readUInt16LE(8) };
  if (b[0] === 0x42 && b[1] === 0x4d) return { w: b.readInt32LE(18), h: Math.abs(b.readInt32LE(22)) };
  if (b.toString("ascii", 0, 4) === "RIFF" && b.toString("ascii", 8, 12) === "WEBP") {
    const t = b.toString("ascii", 12, 16);
    if (t === "VP8X") return { w: 1 + b.readUIntLE(24, 3), h: 1 + b.readUIntLE(27, 3) };
    if (t === "VP8L") { const v = b.readUInt32LE(21); return { w: 1 + (v & 0x3fff), h: 1 + ((v >> 14) & 0x3fff) }; }
    if (t === "VP8 ") return { w: b.readUInt16LE(26) & 0x3fff, h: b.readUInt16LE(28) & 0x3fff };
  }
  if (b[0] === 0xff && b[1] === 0xd8) {
    let i = 2, orient = 1;
    while (i + 9 < b.length) {
      if (b[i] !== 0xff) { i++; continue; }
      const m = b[i + 1], len = b.readUInt16BE(i + 2);
      if (m === 0xe1 && b.toString("ascii", i + 4, i + 8) === "Exif") { // orientation: rotated photos swap width and height
        const t = i + 10, le = b.toString("ascii", t, t + 2) === "II";
        const r16 = (o) => (le ? b.readUInt16LE(o) : b.readUInt16BE(o)), r32 = (o) => (le ? b.readUInt32LE(o) : b.readUInt32BE(o));
        try { const ifd = t + r32(t + 4), n = r16(ifd); for (let k = 0; k < n; k++) { const e = ifd + 2 + k * 12; if (r16(e) === 0x0112) { orient = r16(e + 8); break; } } } catch (_) {}
      }
      if (m >= 0xc0 && m <= 0xcf && m !== 0xc4 && m !== 0xc8 && m !== 0xcc) {
        const h = b.readUInt16BE(i + 5), w = b.readUInt16BE(i + 7);
        return orient >= 5 && orient <= 8 ? { w: h, h: w } : { w, h };
      }
      i += 2 + len;
    }
  }
  return null;
}
const dimCache = new Map();
async function dimsFor(img) {
  const key = `${img.path}|${img.mtime}`;
  if (dimCache.has(key)) return dimCache.get(key);
  let d = null;
  try {
    const fh = await fsp.open(img.path, "r");
    try {
      for (const n of [65536, 1048576]) {
        const buf = Buffer.alloc(Math.min(n, img.size || n));
        const { bytesRead } = await fh.read(buf, 0, buf.length, 0);
        d = sizeFromHeader(buf.subarray(0, bytesRead));
        if (d || bytesRead < buf.length) break;
      }
    } finally { await fh.close(); }
  } catch (_) {}
  if (d && d.w > 0 && d.h > 0) dimCache.set(key, d); else d = null;
  return d;
}
async function pool(list, n, fn) {
  let i = 0;
  await Promise.all(Array.from({ length: Math.min(n, list.length) }, async () => { while (i < list.length) { const k = i++; await fn(list[k], k); } }));
}

async function readText(p) { try { return (await fsp.readFile(p, "utf8")).trim(); } catch (_) { return ""; } }

// The extension saves prompts/, copy/ and breakdown/ next to the images, named the same.
async function sidecars(root, images) {
  const sources = {};
  for (const b of [root, path.join(root, "..")]) {
    const txt = await readText(path.join(b, "sources.txt"));
    for (const line of txt.split(/\r?\n/)) { const [n, link] = line.split("\t"); if (n && link && /^https:\/\//.test(link)) sources[n.trim()] = link.trim(); }
  }
  const listing = new Map(); // "<dir>|<kind>" -> Set of file names
  const namesIn = async (dir, kind) => {
    const k = `${dir}|${kind}`;
    if (!listing.has(k)) { try { listing.set(k, new Set(await fsp.readdir(path.join(dir, kind)))); } catch (_) { listing.set(k, new Set()); } }
    return listing.get(k);
  };
  await pool(images, 16, async (img) => {
    const notes = {};
    for (const d of SIDE_DIRS) {
      for (const b of [path.dirname(img.path), root]) {
        if ((await namesIn(b, d)).has(img.name + ".txt")) { const t = await readText(path.join(b, d, img.name + ".txt")); if (t) { notes[d] = t; break; } }
      }
    }
    img.notes = notes;
    img.link = sources[img.name] || "";
  });
}

async function readBoard(folder) {
  const root = path.resolve(folder);
  allowedRoots.add(root);
  const images = await scanImages(root);
  images.sort((a, b) => a.rel.localeCompare(b.rel, undefined, { numeric: true }));
  await Promise.all([sidecars(root, images), pool(images, 12, async (img) => { const d = await dimsFor(img); if (d) { img.w = d.w; img.h = d.h; } })]);
  let layout = null;
  try { layout = JSON.parse(await fsp.readFile(path.join(root, BOARD_FILE), "utf8")); } catch (_) {
    try { layout = JSON.parse(await fsp.readFile(layoutFallback(root), "utf8")); } catch (_) {}
  }
  settings.recent = [root, ...settings.recent.filter((r) => r !== root)].slice(0, 8);
  saveSettings();
  watch(root);
  return { root, name: path.basename(root), images, layout };
}

function layoutFallback(root) {
  return path.join(app.getPath("userData"), "boards", crypto.createHash("sha1").update(root).digest("hex") + ".json");
}

function watch(root) {
  if (watcher) { try { watcher.close(); } catch (_) {} watcher = null; }
  let t = null;
  try {
    watcher = fs.watch(root, { recursive: true }, (_ev, file) => {
      if (file && (file.endsWith(BOARD_FILE) || file.endsWith(".crdownload") || file.endsWith(".tmp"))) return;
      clearTimeout(t);
      t = setTimeout(() => win && win.webContents.send("folder-changed", root), 700);
    });
  } catch (_) {}
}

let colCache = null, colTime = 0;
async function collections(force) {
  if (!force && colCache && Date.now() - colTime < 15000) return colCache;
  const root = pinterestRoot();
  let entries = [];
  try { entries = await fsp.readdir(root, { withFileTypes: true }); } catch (_) { return (colCache = { root, list: [] }); }
  const list = [];
  for (const e of entries) {
    if (!e.isDirectory() || e.name.startsWith(".")) continue;
    const dir = path.join(root, e.name);
    let files = [];
    try { files = (await fsp.readdir(dir)).filter((f) => IMAGE_EXT.has(path.extname(f).toLowerCase())); } catch (_) { continue; }
    if (!files.length) continue;
    files.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
    let updated = 0;
    try { updated = (await fsp.stat(dir)).mtimeMs; } catch (_) {}
    list.push({ path: dir, name: e.name, count: files.length, updated, covers: files.slice(-3).map((f) => path.join(dir, f)) });
  }
  list.sort((a, b) => b.updated - a.updated);
  colTime = Date.now();
  return (colCache = { root, list });
}

// ---------- thumbnails ----------

const THUMB = 480;
const thumbJobs = new Map();
let thumbActive = 0;
const thumbQueue = [];
const slot = () => new Promise((res) => { if (thumbActive < 3) { thumbActive++; res(); } else thumbQueue.push(res); });
const release = () => { const next = thumbQueue.shift(); if (next) next(); else thumbActive--; };

async function makeThumb(p, out) {
  await slot();
  try {
    let img = null;
    if (process.platform === "win32" || process.platform === "darwin") {
      try { img = await nativeImage.createThumbnailFromPath(p, { width: THUMB, height: THUMB * 2 }); } catch (_) { img = null; }
    }
    if (!img || img.isEmpty()) {
      img = nativeImage.createFromPath(p);
      if (img.isEmpty()) return null;
      if (img.getSize().width > THUMB) img = img.resize({ width: THUMB, quality: "good" });
    }
    await fsp.writeFile(out, img.toJPEG(84));
    return out;
  } finally { release(); }
}

async function thumbFor(p) {
  if (!THUMBABLE.has(path.extname(p).toLowerCase()) && process.platform !== "win32") return null;
  const st = await fsp.stat(p);
  if (st.size < 120 * 1024) return null; // small files are already light enough
  const key = crypto.createHash("sha1").update(`${p}|${st.size}|${st.mtimeMs}|${THUMB}`).digest("hex");
  const out = path.join(thumbDir, key + ".jpg");
  try { await fsp.access(out); return out; } catch (_) {}
  if (!thumbJobs.has(out)) thumbJobs.set(out, makeThumb(p, out).catch(() => null).finally(() => thumbJobs.delete(out)));
  return thumbJobs.get(out);
}

const mime = { ".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".woff2": "font/woff2", ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".webp": "image/webp", ".gif": "image/gif", ".bmp": "image/bmp", ".avif": "image/avif", ".svg": "image/svg+xml" };

async function serve(req) {
  const u = new URL(req.url);
  const headers = { "Access-Control-Allow-Origin": "*" };
  if (u.host === "app") {
    const rel = decodeURIComponent(u.pathname).replace(/^\/+/, "") || "index.html";
    const file = path.join(__dirname, "renderer", rel);
    if (!inside(file, path.join(__dirname, "renderer"))) return new Response("no", { status: 403 });
    try { return new Response(await fsp.readFile(file), { headers: { ...headers, "Content-Type": mime[path.extname(file)] || "application/octet-stream" } }); }
    catch (_) { return new Response("missing", { status: 404 }); }
  }
  const p = u.searchParams.get("p");
  if (!p || !allowed(p)) return new Response("not allowed", { status: 403 });
  try {
    let file = p;
    if (u.host === "thumb") file = (await thumbFor(p)) || p;
    const res = await net.fetch(pathToFileURL(file).toString());
    const h = new Headers(res.headers);
    h.set("Access-Control-Allow-Origin", "*");
    h.set("Content-Type", mime[path.extname(file).toLowerCase()] || "application/octet-stream");
    h.set("Cache-Control", "max-age=3600");
    return new Response(res.body, { status: res.status, headers: h });
  } catch (_) { return new Response("missing", { status: 404 }); }
}

// ---------- window ----------

function createWindow() {
  const b = settings.bounds;
  const onScreen = b && screen.getAllDisplays().some((d) => b.x < d.bounds.x + d.bounds.width && b.x + b.width > d.bounds.x && b.y < d.bounds.y + d.bounds.height && b.y + b.height > d.bounds.y);
  win = new BrowserWindow({
    width: onScreen ? b.width : 1280, height: onScreen ? b.height : 820, x: onScreen ? b.x : undefined, y: onScreen ? b.y : undefined,
    minWidth: 320, minHeight: 260,
    backgroundColor: "#0E0E12",
    title: "Inspired Moodboard",
    icon: path.join(__dirname, "icon.png"),
    titleBarStyle: "hidden",
    titleBarOverlay: process.platform === "darwin" ? undefined : { color: "#0E0E12", symbolColor: "#F4F4F7", height: 40 },
    show: false,
    webPreferences: { preload: path.join(__dirname, "preload.js"), contextIsolation: true, sandbox: true, nodeIntegration: false, spellcheck: false }
  });
  win.setAlwaysOnTop(!!settings.onTop, "floating");
  if (settings.opacity < 1) win.setOpacity(Math.max(0.3, settings.opacity));
  win.once("ready-to-show", () => win.show());
  const keep = () => { if (!win.isMaximized() && !win.isMinimized() && !settings.float) { settings.bounds = win.getBounds(); saveSettings(); } };
  win.on("resize", keep); win.on("move", keep);
  win.webContents.setWindowOpenHandler(({ url }) => { if (/^https:\/\//.test(url)) shell.openExternal(url); return { action: "deny" }; });
  win.webContents.on("will-navigate", (e, url) => { if (!url.startsWith("board://app/")) e.preventDefault(); });
  win.loadURL("board://app/index.html");
}

// ---------- IPC ----------

const handlers = {
  init: async () => ({ platform: process.platform, settings, collections: await collections(true), version: app.getVersion() }),
  collections: (_e, force) => collections(force),
  pickFolder: async () => {
    const r = await dialog.showOpenDialog(win, { title: "Open a folder of images", properties: ["openDirectory"], defaultPath: fs.existsSync(pinterestRoot()) ? pinterestRoot() : app.getPath("downloads") });
    if (r.canceled || !r.filePaths[0]) return null;
    return readBoard(r.filePaths[0]);
  },
  openFolder: async (_e, p) => {
    try { const st = await fsp.stat(p); if (!st.isDirectory()) p = path.dirname(p); } catch (_) { return null; }
    return readBoard(p);
  },
  reload: async (_e, root) => (allowed(root) || allowedRoots.has(path.resolve(root)) ? readBoard(root) : null),
  saveLayout: async (_e, root, layout) => {
    if (!allowedRoots.has(path.resolve(root))) return false;
    const data = JSON.stringify(layout);
    try { await fsp.writeFile(path.join(root, BOARD_FILE), data); return true; }
    catch (_) { const f = layoutFallback(root); await fsp.mkdir(path.dirname(f), { recursive: true }); await fsp.writeFile(f, data); return true; }
  },
  setOnTop: (_e, on) => { settings.onTop = !!on; win.setAlwaysOnTop(!!on, "floating"); saveSettings(); return settings.onTop; },
  setOpacity: (_e, v) => { settings.opacity = Math.min(1, Math.max(0.3, Number(v) || 1)); win.setOpacity(settings.opacity); saveSettings(); return settings.opacity; },
  setFloat: (_e, on) => {
    on = !!on;
    if (on === settings.float) return on;
    settings.float = on;
    if (on) {
      settings.normalBounds = win.getBounds();
      if (win.isMaximized()) win.unmaximize();
      const wa = screen.getDisplayMatching(win.getBounds()).workArea;
      win.setBounds({ width: 420, height: 560, x: wa.x + wa.width - 440, y: wa.y + 20 }, true);
      win.setAlwaysOnTop(true, "floating");
    } else {
      if (settings.normalBounds) win.setBounds(settings.normalBounds, true);
      win.setAlwaysOnTop(!!settings.onTop, "floating");
    }
    saveSettings();
    return on;
  },
  startDrag: (e, p) => {
    if (!allowed(p)) return;
    let icon = nativeImage.createFromPath(p);
    icon = icon.isEmpty() ? nativeImage.createFromPath(path.join(__dirname, "icon.png")) : icon.resize({ width: 96 });
    e.sender.startDrag({ file: p, icon });
  },
  copyImage: async (_e, p) => {
    if (!allowed(p)) return false;
    let img = nativeImage.createFromPath(p);
    if (img.isEmpty()) { // webp and friends: let Chromium decode, pass back as PNG data URL
      return "decode";
    }
    clipboard.writeImage(img); return true;
  },
  copyImageData: (_e, dataUrl) => { const img = nativeImage.createFromDataURL(dataUrl); if (img.isEmpty()) return false; clipboard.writeImage(img); return true; },
  copyText: (_e, t) => { clipboard.writeText(String(t || "")); return true; },
  showInFolder: (_e, p) => { if (allowed(p)) shell.showItemInFolder(p); },
  openPath: (_e, p) => { if (allowed(p)) shell.openPath(p); },
  openLink: (_e, url) => { if (/^https:\/\//.test(url)) shell.openExternal(url); },
  exportBoard: async (_e, dataUrl, name) => {
    const r = await dialog.showSaveDialog(win, { title: "Save moodboard image", defaultPath: path.join(app.getPath("pictures"), `${name || "Moodboard"}.png`), filters: [{ name: "PNG image", extensions: ["png"] }] });
    if (r.canceled || !r.filePath) return null;
    await fsp.writeFile(r.filePath, Buffer.from(dataUrl.split(",")[1], "base64"));
    return r.filePath;
  }
};

app.whenReady().then(async () => {
  await loadSettings();
  thumbDir = path.join(app.getPath("userData"), "thumbs");
  await fsp.mkdir(thumbDir, { recursive: true });
  protocol.handle("board", serve);
  for (const [k, fn] of Object.entries(handlers)) {
    if (k === "startDrag") ipcMain.on(k, (e, ...a) => fn(e, ...a));
    else ipcMain.handle(k, (e, ...a) => fn(e, ...a));
  }
  createWindow();
  // Opening a folder by dropping it on the app icon / "Open with".
  const arg = process.argv.slice(1).find((a) => {
    if (a.startsWith("-")) return false;
    const p = path.resolve(a);
    if (inside(p, __dirname) || inside(p, app.getAppPath())) return false;
    try { const st = fs.statSync(p); return st.isDirectory() || IMAGE_EXT.has(path.extname(p).toLowerCase()); } catch (_) { return false; }
  });
  if (arg) win.webContents.once("did-finish-load", () => win.webContents.send("open-path", arg));
});

app.on("window-all-closed", () => app.quit());
