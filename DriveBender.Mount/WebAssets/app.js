"use strict";
// Dependency-free live dashboard: SSE feed → animated meters, sparklines, tier topology.
const token = new URLSearchParams(location.search).get("token") || "";
const auth = { headers: { Authorization: "Bearer " + token } };
const history = new Map();   // poolId -> [hitRate samples]
const HIST = 40;

function fmtBytes(n) {
  if (!n || n <= 0) return "0 B";
  const u = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
  let i = 0; while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(n < 10 && i > 0 ? 1 : 0) + " " + u[i];
}
function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }

function donut(free, total) {
  const used = total > 0 ? (total - free) / total : 0;
  const r = 34, c = 2 * Math.PI * r, off = c * (1 - used);
  const pct = Math.round(used * 100);
  return `<svg class="donut" width="84" height="84" viewBox="0 0 84 84">
    <circle class="track" cx="42" cy="42" r="${r}" fill="none" stroke-width="9"/>
    <circle class="fill" cx="42" cy="42" r="${r}" fill="none" stroke-width="9"
      stroke-dasharray="${c.toFixed(1)}" stroke-dashoffset="${off.toFixed(1)}"
      transform="rotate(-90 42 42)" style="stroke:${pct>90?'var(--bad)':pct>75?'var(--warn)':'var(--accent)'}"/>
    <text x="42" y="47" text-anchor="middle" font-size="16">${pct}%</text>
  </svg>`;
}

function sparkline(samples) {
  if (samples.length < 2) return `<svg class="spark" viewBox="0 0 100 44" preserveAspectRatio="none"></svg>`;
  const max = 1, min = 0, w = 100, h = 40;
  const pts = samples.map((v, i) => [i / (samples.length - 1) * w, h - (v - min) / (max - min || 1) * h + 2]);
  const line = pts.map((p, i) => (i ? "L" : "M") + p[0].toFixed(1) + " " + p[1].toFixed(1)).join(" ");
  const area = line + ` L ${w} ${h+2} L 0 ${h+2} Z`;
  return `<svg class="spark" viewBox="0 0 100 44" preserveAspectRatio="none">
    <path class="area" d="${area}"/><path class="line" d="${line}"/></svg>`;
}

function topology(pool) {
  // three tiers: RAM (T0) → fast (landing) → capacity; animated flow when draining/dirty
  const fast = pool.members.filter(m => m.role === "landing");
  const cap = pool.members.filter(m => m.role !== "landing");
  const m = pool.metrics || {};
  const draining = (m.drainedFiles || 0) > 0;
  const dirty = (m.dirtyFiles || 0) > 0;
  const box = (x, label, sub) => `<g class="tier"><rect x="${x}" y="10" width="90" height="46" rx="8"/>
    <text x="${x+45}" y="32" text-anchor="middle">${label}</text>
    <text class="cap" x="${x+45}" y="47" text-anchor="middle">${sub}</text></g>`;
  const flow = (x1, x2, cls) => `<path class="flow ${cls}" d="M ${x1} 33 C ${(x1+x2)/2} 33, ${(x1+x2)/2} 33, ${x2} 33"/>`;
  return `<svg class="topo" width="100%" height="66" viewBox="0 0 320 66" preserveAspectRatio="xMidYMid meet">
    ${box(0, "RAM", dirty ? (m.dirtyFiles+" dirty") : "cache")}
    ${dirty ? flow(90, 115, "") : ""}
    ${box(115, "Fast", fast.length + " member" + (fast.length===1?"":"s"))}
    ${draining ? flow(205, 230, "drain") : (pool.degraded ? "" : flow(205,230,"dup"))}
    ${box(230, "Capacity", cap.length + " member" + (cap.length===1?"":"s"))}
  </svg>`;
}

const KINDS = [
  ["file", "Local folder / drive"], ["unc", "UNC share"], ["ftp", "FTP"], ["ftps", "FTPS"],
  ["sftp", "SFTP"], ["webdav", "WebDAV"], ["webdavs", "WebDAV (HTTPS)"], ["s3", "Amazon S3"],
  ["azblob", "Azure Blob"], ["azfile", "Azure File"], ["dropbox", "Dropbox"], ["onedrive", "OneDrive"],
  ["gdrive", "Google Drive"], ["gcs", "Google Cloud Storage"]
];
const ROLES = ["capacity", "landing", "readonly"];

// local kinds are picked with the folder browser and need no credentials; every other kind is
// a remote service with a scheme-specific credential form (mapped to the daemon's user/secret).
const LOCAL_KINDS = new Set(["file", "unc"]);
const needsCred = k => !LOCAL_KINDS.has(k);
const CRED_SPECS = {
  ftp:      [["user", "Username", "text"], ["pass", "Password", "password"]],
  ftps:     [["user", "Username", "text"], ["pass", "Password", "password"]],
  webdav:   [["user", "Username", "text"], ["pass", "Password", "password"]],
  webdavs:  [["user", "Username", "text"], ["pass", "Password", "password"]],
  sftp:     "sftp", // special: password or private-key authentication
  s3:       [["accessKey", "Access key ID", "text"], ["secretKey", "Secret access key", "password"],
             ["region", "Region (optional)", "text"], ["serviceUrl", "Endpoint URL (optional, S3-compatible)", "text"]],
  azblob:   [["accountKey", "Storage account key", "password"]],
  azfile:   [["accountKey", "Storage account key", "password"]],
  dropbox:  [["token", "Access token", "password"]],
  onedrive: [["token", "Access token", "password"]],
  gdrive:   [["json", "Service account JSON", "textarea"]],
  gcs:      [["json", "Service account JSON", "textarea"]],
};
const kindLabel = k => (KINDS.find(x => x[0] === k) || [, k])[1];

// collapse the scheme-specific fields into the daemon's { user, secret } shape (multi-field
// secrets are stored as a JSON object the matching backend knows how to read — see CredentialPayload)
function credPayload(kind, v) {
  switch (kind) {
    case "ftp": case "ftps": case "webdav": case "webdavs":
      return { user: v.user || "", secret: v.pass || "" };
    case "sftp":
      return v.mode === "key"
        ? { user: v.user || "", secret: JSON.stringify({ privateKey: v.privateKey || "", passphrase: v.passphrase || "" }) }
        : { user: v.user || "", secret: v.pass || "" };
    case "s3": {
      const o = { accessKey: v.accessKey || "", secretKey: v.secretKey || "" };
      if (v.serviceUrl) o.serviceUrl = v.serviceUrl; else if (v.region) o.region = v.region;
      return { user: "", secret: JSON.stringify(o) };
    }
    case "azblob": case "azfile": return { user: "", secret: v.accountKey || "" };
    case "dropbox": case "onedrive": return { user: "", secret: v.token || "" };
    case "gdrive": case "gcs": return { user: "", secret: v.json || "" };
    default: return { user: "", secret: "" };
  }
}

function memberRow(pool, m) {
  const row = el("div", "member");
  row.innerHTML = `<span class="status ${m.online ? "" : "off"}"></span>
    <span>${m.label || m.path}</span>${m.network ? '<span class="badge info">remote</span>' : ""}
    <span class="role">${m.role}</span>`;
  const scatter = el("button", "small danger", "remove");
  scatter.title = "Scatter this member's data to the others, then remove it";
  scatter.onclick = () => confirm(`Remove member "${m.label || m.path}"? Its data is scattered to the other members first.`)
    && op(`/api/pool/remove-member?pool=${pool.id}&member=${encodeURIComponent(m.id)}`);
  row.appendChild(scatter);
  return row;
}

function card(pool) {
  const c = el("div", "card");
  c.dataset.id = pool.id;
  patchCard(c, pool);
  return c;
}

// Update a card's contents in place. The card NODE is reused across the 1 Hz refresh on purpose:
// replacing it re-triggers the :hover lift transform (and reflows the grid), which is the "jumps a
// few pixels up and down" the user saw. Only the inner content is refreshed.
function patchCard(c, pool) {
  const health = pool.degraded ? '<span class="badge warn">degraded</span>' : '<span class="badge ok">healthy</span>';
  const mountBadge = pool.mounted ? `<span class="badge info">mounted ${pool.mounted}</span>` : "";
  const m = pool.metrics;
  const hist = history.get(pool.id) || [];

  c.innerHTML = `
    <h2>${pool.name} ${health} ${mountBadge}</h2>
    <div class="sub">${pool.source} · ${pool.failureDomains} failure domain(s)</div>
    <div class="capacity-row">
      ${donut(pool.bytesFree, pool.bytesTotal)}
      <div class="cap-legend">
        <div><b>${fmtBytes(pool.bytesFree)}</b> free</div>
        <div>of <b>${fmtBytes(pool.bytesTotal)}</b></div>
      </div>
    </div>
    ${m ? `<div class="meters">
      <div class="meter"><div class="label"><span>Cache hit</span><span>${Math.round((m.cacheHitRate||0)*100)}%</span></div>
        <div class="bar hit"><span style="width:${Math.round((m.cacheHitRate||0)*100)}%"></span></div></div>
      <div class="meter"><div class="label"><span>Dirty files</span><span>${m.dirtyFiles||0}</span></div>
        <div class="bar write"><span style="width:${Math.min(100,(m.dirtyFiles||0)*10)}%"></span></div></div>
    </div>
    <div class="meter"><div class="label"><span>Cache hit rate</span><span>read ${fmtBytes(m.readBytes)} · wrote ${fmtBytes(m.writtenBytes)}</span></div>
      ${sparkline(hist)}</div>` : ""}
    ${topology(pool)}
    <div class="members"></div>
    ${pool.warnings && pool.warnings.length ? `<div class="warnings">${pool.warnings.map(w => "<div>⚠ " + w + "</div>").join("")}</div>` : ""}
    ${m && m.activity && m.activity.length ? `<div class="activity">${m.activity.slice(0,8).map(a =>
      `<div>${a.kind} ${a.path||""} ${a.bytes?fmtBytes(a.bytes):""} ${a.reason||""}</div>`).join("")}</div>` : ""}
    <div class="actions"></div>`;

  const memberBox = c.querySelector(".members");
  pool.members.forEach(mem => memberBox.appendChild(memberRow(pool, mem)));

  const actions = c.querySelector(".actions");
  const add = (label, cls, fn) => { const b = el("button", cls, label); b.onclick = fn; actions.appendChild(b); };
  if (pool.mounted)
    add("Unmount", "", () => op("/api/pool/unmount?pool=" + pool.id));
  else
    add("Mount", "primary", () => mountPool(pool));
  add("Health", "", () => op("/api/health?pool=" + pool.id));
  add("Fix", "", () => op("/api/health?fix=true&pool=" + pool.id));
  add("Restore", "", () => op("/api/restore?pool=" + pool.id));
  add("Add member", "", () => addMemberDialog(pool));
  if (pool.source === "manifest")
    add("Duplication", "", () => duplicationDialog(pool));
  if (pool.source === "manifest")
    add("Settings", "", () => settingsDialog(pool));
  if (pool.source === "manifest")
    add("Forget", "", () => confirm(`Remove "${pool.name}" from this machine's list? Data and on-disk markers are kept — the pool can be restored or re-imported later.`) && op("/api/pool/forget?pool=" + pool.id));
  add("Delete", "danger", () => confirm(`Delete pool "${pool.name}"? The manifest is removed; your files are kept on disk.`) && op("/api/pool/delete?pool=" + pool.id));
  add("Purge", "danger", () => prompt(`PURGE wipes all data in "${pool.name}". Type the pool name to confirm:`) === pool.name && op("/api/pool/purge?pool=" + pool.id));
}

// Full settings editor: edit the pool's whole config block as JSON, validated on save. Every knob
// lives here (write policy, cache, tiers, background, safety, resilience, integrity, trash,
// placement, duplication, observability); the dedicated dialogs are shortcuts to common ones.
async function settingsDialog(pool) {
  const j = await fetch("/api/pool/config?pool=" + pool.id, auth).then(r => r.json()).catch(() => ({}));
  const data = (j && j.result) || {};
  const pretty = (s) => { try { return JSON.stringify(JSON.parse(s), null, 2); } catch (_) { return s || ""; } };
  const current = data.current ? pretty(data.current) : "";
  const template = pretty(data.template || "{}");

  const form = el("div");
  form.innerHTML = `
    <p class="hint">These are this pool's settings, merged over the built-in defaults on mount. Edit any
    keys you want to override and leave the rest out — omitted keys keep inheriting. Saved changes take
    effect on the next mount. Invalid values are rejected with a reason.</p>
    <label>Pool settings (JSON)</label>
    <textarea class="cfg" rows="16" spellcheck="false" placeholder="{ }">${current}</textarea>
    <div class="row" style="margin-top:6px">
      <button type="button" class="cfg-template" style="flex:0 0 auto">Load all defaults as a template</button>
    </div>
    <details style="margin-top:8px"><summary class="hint" style="cursor:pointer">Show built-in defaults (reference)</summary>
      <pre class="cfg-ref">${template.replace(/</g, "&lt;")}</pre></details>`;
  const area = form.querySelector(".cfg");
  form.querySelector(".cfg-template").onclick = () => { area.value = template; };
  showModal(`Settings — ${pool.name}`, form, async () => {
    const text = area.value.trim();
    if (!text) return "Enter a JSON object (or click ‘Load all defaults as a template’).";
    try { JSON.parse(text); } catch (e) { return "Not valid JSON: " + e.message; }
    return await op("/api/pool/config?pool=" + pool.id, { json: text }) ? null : "Settings were rejected.";
  }, "Save settings");
}

// Configure how many copies of each file the pool keeps — pool-wide and optionally per folder/file.
function duplicationDialog(pool) {
  const form = el("div");
  form.innerHTML = `
    <label>Copies of every file, pool-wide (1 = no duplication)</label>
    <input class="dup" type="number" min="1" max="10" value="${pool.duplication || 1}">
    <label style="display:flex;align-items:center;gap:8px;margin-top:10px">
      <input class="dupsame" type="checkbox" style="width:auto" ${pool.allowSamePhysical ? "checked" : ""}>
      Keep extra copies even on the same physical disk
    </label>
    <p class="hint">By default copies only land on <b>independent physical disks</b> — members on the
    same disk are one failure domain (SAFE-PHYS), and this pool has <b>${pool.failureDomains}</b> of them.
    Tick the box to store the extra copies anyway when no independent disk is free: that guards against
    <b>bit-rot / silent corruption</b> (a scrub can heal from the good copy) but <b>not</b> against the
    disk failing. Changes take effect on the next mount.</p>
    <label>Optional — override a folder or file (glob, e.g. <code>Photos/**</code> or <code>Docs/tax.pdf</code>)</label>
    <input class="dupfolder" placeholder="leave empty to set only the pool-wide default">
    <label>Copies for that pattern</label>
    <input class="dupfolderlevel" type="number" min="1" max="10" value="2">`;
  showModal(`Duplication — ${pool.name}`, form, async () => {
    const folder = form.querySelector(".dupfolder").value.trim();
    if (folder) {
      const flvl = parseInt(form.querySelector(".dupfolderlevel").value, 10);
      if (!(flvl >= 1)) return "Enter a valid copy count for the pattern.";
      if (!await op("/api/pool/duplication?pool=" + pool.id, { level: flvl, folder })) return "Could not set the folder override.";
    }
    const level = parseInt(form.querySelector(".dup").value, 10);
    if (!(level >= 1)) return "Enter a valid pool-wide copy count.";
    const allowSamePhysical = form.querySelector(".dupsame").checked;
    return await op("/api/pool/duplication?pool=" + pool.id, { level, allowSamePhysical }) ? null : "Could not set duplication.";
  }, "Save");
}

// Mount at the pool's configured target, or ask for a drive letter / folder when it has none.
function mountPool(pool) {
  let target = pool.configuredTarget;
  if (!target) {
    target = prompt("Mount at which location?\n\nWindows: a free drive letter like X: (or an empty folder)\nLinux: an empty directory like /mnt/pool", "");
    if (!target) return;
  }
  op("/api/pool/mount?pool=" + pool.id + "&target=" + encodeURIComponent(target.trim()));
}

async function post(url, body) {
  try {
    const opts = { method: "POST", headers: { ...auth.headers } };
    if (body !== undefined) { opts.headers["Content-Type"] = "application/json"; opts.body = JSON.stringify(body); }
    const r = await fetch(url, opts);
    return await r.json().catch(() => ({ ok: r.ok }));
  } catch (e) { return { ok: false, error: String(e) }; }
}

async function op(url, body) {
  const j = await post(url, body);
  if (!j.ok) {
    if (j.needsPrereq && j.installable && confirm(`${j.error}\n\nInstall ${j.driver} now?`))
      await installDriver();
    else if (j.conflict)
      conflictDialog(j.conflict, url, body);
    else
      alert("Failed: " + (j.error || ""));
  }
  return j.ok;
}

// A member folder is claimed by another (often orphaned/invisible) pool — offer to restore that
// pool from the copy it left behind, or take the folder over for the pool we were creating.
function conflictDialog(conflict, retryUrl, retryBody) {
  const body = el("div");
  body.innerHTML = `<p>The folder <b>${conflict.path}</b> already belongs to another pool
    (<code>${conflict.poolId}</code>).${conflict.registered ? " It's already in your pool list." : ""}</p>
    <p>${conflict.restorable
      ? "You can restore that pool from the manifest copy still in the folder, or take the folder over for this pool."
      : "Its manifest copy is gone, so it can't be restored — you can take the folder over for this pool."}</p>`;
  const err = el("div", "err");
  const actions = el("div", "modal-actions");
  const cancel = el("button", null, "Cancel"); cancel.onclick = closeModal;
  actions.appendChild(cancel);
  if (conflict.restorable && !conflict.registered) {
    const rec = el("button", "primary", "Restore old pool");
    rec.onclick = async () => { if (await op("/api/pool/recover", { path: conflict.path })) closeModal(); else err.textContent = "Restore failed."; };
    actions.appendChild(rec);
  }
  const take = el("button", conflict.restorable ? "danger" : "primary", "Use this folder anyway");
  take.onclick = async () => {
    const ok = retryUrl.includes("/pool/create")
      ? await op(retryUrl, { ...retryBody, takeOver: true })
      : await op(retryUrl + (retryUrl.includes("?") ? "&" : "?") + "takeover=true", retryBody);
    if (ok) closeModal(); else err.textContent = "Could not take over the folder.";
  };
  actions.appendChild(take);
  const root = document.getElementById("modal-root");
  const overlay = el("div", "overlay");
  const modal = el("div", "modal");
  modal.append(el("h2", null, "Folder already in use"), body, err, actions);
  overlay.appendChild(modal);
  overlay.onclick = e => { if (e.target === overlay) closeModal(); };
  root.innerHTML = ""; root.appendChild(overlay);
}

async function installDriver() {
  const banner = document.getElementById("prereq");
  if (banner) banner.textContent = "Installing driver… (accept any prompt that appears)";
  try {
    const r = await fetch("/api/prereqs/install", { method: "POST", headers: { ...auth.headers } });
    const j = await r.json().catch(() => ({ ok: r.ok }));
    alert(j.ok ? (j.result || "Installed.") : "Install failed: " + (j.error || r.status));
  } catch (e) { alert("Install failed: " + e); }
  checkPrereqs();
}

async function checkPrereqs() {
  const banner = document.getElementById("prereq");
  if (!banner) return;
  try {
    const s = await fetch("/api/prereqs", auth).then(r => r.json());
    if (s.ok && !s.needsElevation) { banner.hidden = true; return; }
    banner.hidden = false;
    banner.innerHTML = "";
    const msg = el("span", null, s.ok
      ? "⚠ Mounting needs administrator rights — accepting the prompt when you click Mount is enough."
      : "⚠ " + s.detail + " Mounting won't work until it's installed.");
    banner.appendChild(msg);
    if (!s.ok && s.installable) {
      const b = el("button", "primary small", "Install " + s.driver);
      b.style.marginLeft = "12px";
      b.onclick = installDriver;
      banner.appendChild(b);
    }
  } catch (_) { banner.hidden = true; }
}

function render(data) {
  const container = document.getElementById("pools");
  const pools = data.pools || [];
  document.getElementById("empty").hidden = pools.length > 0;
  const seen = new Set();
  for (const pool of pools) {
    seen.add(pool.id);
    const h = history.get(pool.id) || [];
    if (pool.metrics) { h.push(pool.metrics.cacheHitRate || 0); while (h.length > HIST) h.shift(); history.set(pool.id, h); }
    const existing = container.querySelector(`.card[data-id="${pool.id}"]`);
    if (existing) patchCard(existing, pool); else container.appendChild(card(pool));
  }
  container.querySelectorAll(".card").forEach(c => { if (!seen.has(c.dataset.id)) c.remove(); });
}

// ---- modals: a stack so a folder picker / credential dialog can open on top of the -----------
// create dialog without destroying it. closeModal pops just the topmost overlay.
function closeModal() {
  const root = document.getElementById("modal-root");
  if (root.lastElementChild) root.removeChild(root.lastElementChild);
}

function showModal(title, bodyNode, onOk, okLabel) {
  const err = el("div", "err");
  const overlay = el("div", "overlay");
  const modal = el("div", "modal");
  modal.appendChild(el("h2", null, title));
  modal.appendChild(bodyNode);
  modal.appendChild(err);
  const actions = el("div", "modal-actions");
  const cancel = el("button", null, "Cancel"); cancel.onclick = closeModal;
  const ok = el("button", "primary", okLabel || "OK");
  ok.onclick = async () => { const msg = await onOk(); if (msg) err.textContent = msg; else closeModal(); };
  actions.append(cancel, ok);
  modal.appendChild(actions);
  overlay.appendChild(modal);
  overlay.onclick = e => { if (e.target === overlay) closeModal(); };
  document.getElementById("modal-root").appendChild(overlay);
}

// a location input + a "Browse" button (local folders) + a scheme-aware "Set credentials" button.
// Returns { node, getKind, getLocation, getCred, reset }.
function locationControls(rowFor) {
  let cred = null;
  const wrap = el("div");
  wrap.innerHTML = `
    <div class="row">
      <select class="k">${KINDS.map(k => `<option value="${k[0]}">${k[1]}</option>`).join("")}</select>
      <select class="r">${ROLES.map(r => `<option value="${r}">${r}</option>`).join("")}</select>
    </div>
    <div class="row" style="margin-top:6px">
      <input class="loc" placeholder="path or URI (e.g. D:\\ , \\\\server\\share, sftp://user@host/path)">
      <button type="button" class="browse" style="flex:0 0 auto">📁 Browse</button>
    </div>
    <div class="row" style="margin-top:6px">
      <button type="button" class="setcred" style="flex:0 0 auto" hidden>🔑 Set credentials…</button>
      <span class="credmark"></span>
    </div>`;
  const kSel = wrap.querySelector(".k"), loc = wrap.querySelector(".loc");
  const browse = wrap.querySelector(".browse"), setcred = wrap.querySelector(".setcred"), credmark = wrap.querySelector(".credmark");
  const sync = () => { browse.hidden = kSel.value !== "file"; setcred.hidden = !needsCred(kSel.value); cred = null; credmark.textContent = ""; };
  kSel.onchange = sync; sync();
  browse.onclick = () => folderPicker(p => { loc.value = p; });
  setcred.onclick = () => credentialDialog(kSel.value, name => { cred = name; credmark.textContent = "🔑 " + name; });
  return {
    node: wrap,
    getKind: () => kSel.value,
    getLocation: () => loc.value.trim(),
    getRole: () => wrap.querySelector(".r").value,
    getCred: () => cred,
    reset: () => { loc.value = ""; cred = null; credmark.textContent = ""; },
  };
}

function memberEditor() {
  // returns { node, getMembers() } — a location/credential row + a running member list
  const members = [];
  const wrap = el("div");
  wrap.appendChild(el("label", null, "Add member"));
  const ctl = locationControls();
  wrap.appendChild(ctl.node);
  const addBtn = el("button", null, "Add member"); addBtn.style.marginTop = "8px";
  wrap.appendChild(addBtn);
  const list = el("div", "memberlist"); wrap.appendChild(list);
  const redraw = () => {
    list.innerHTML = "";
    members.forEach((m, i) => {
      const row = el("div", "m", `<span>${m.location}</span><span class="role">${m.role}${m.credential ? " · 🔑" : ""}</span>`);
      const rm = el("button", "small danger", "×"); rm.onclick = () => { members.splice(i, 1); redraw(); };
      row.appendChild(rm); list.appendChild(row);
    });
  };
  addBtn.onclick = () => {
    const loc = ctl.getLocation(); if (!loc) return;
    members.push({ location: loc, role: ctl.getRole(), credential: ctl.getCred() });
    ctl.reset(); redraw();
  };
  return { node: wrap, getMembers: () => members };
}

function createPoolDialog() {
  const form = el("div");
  form.innerHTML = `<label>Pool name</label><input class="name" placeholder="MyPool">
    <label>Mount target (optional)</label><input class="mt" placeholder="X:\\ or /mnt/mypool">`;
  const editor = memberEditor();
  form.appendChild(editor.node);
  showModal("Create pool", form, async () => {
    const name = form.querySelector(".name").value.trim();
    const members = editor.getMembers();
    if (!name) return "Enter a pool name.";
    if (!members.length) return "Add at least one member.";
    const ok = await op("/api/pool/create", { name, mountTarget: form.querySelector(".mt").value.trim(), members });
    return ok ? null : "Create failed.";
  }, "Create");
}

function addMemberDialog(pool) {
  const form = el("div");
  const ctl = locationControls();
  form.appendChild(ctl.node);
  showModal(`Add member to ${pool.name}`, form, async () => {
    const loc = ctl.getLocation();
    if (!loc) return "Enter a location.";
    const ok = await op("/api/pool/add-member?pool=" + pool.id, { location: loc, role: ctl.getRole(), credential: ctl.getCred() });
    return ok ? null : "Add failed.";
  }, "Add");
}

// server-backed folder browser (a page can't open a native picker; the localhost daemon lists folders)
function folderPicker(onPick) {
  const body = el("div");
  const crumb = el("div", "fp-path", "This PC");
  const listBox = el("div", "fp-list");
  const manual = el("div");
  manual.innerHTML = `<label>Or type a folder path</label><input class="fp-manual" placeholder="D:\\Data\\Pool">`;
  body.append(crumb, listBox, manual);
  let cur = "";
  async function load(path) {
    const q = path ? "?path=" + encodeURIComponent(path) : "";
    let d;
    try { d = await fetch("/api/fs/list" + q, auth).then(r => r.json()); } catch (_) { d = { dirs: [] }; }
    cur = d.path || "";
    crumb.textContent = cur || "This PC";
    body.querySelector(".fp-manual").value = cur;
    listBox.innerHTML = "";
    if (cur) { const up = el("div", "fp-item", "⬆ .."); up.onclick = () => load(d.parent || ""); listBox.appendChild(up); }
    (d.dirs || []).forEach(x => { const it = el("div", "fp-item", "📁 " + x.name); it.onclick = () => load(x.path); listBox.appendChild(it); });
    if (cur && !(d.dirs || []).length) listBox.appendChild(el("div", "fp-empty", "(no subfolders)"));
  }
  load("");
  showModal("Select folder", body, async () => {
    const chosen = body.querySelector(".fp-manual").value.trim() || cur;
    if (!chosen) return "Navigate into a folder or type a path.";
    onPick(chosen); return null;
  }, "Select this folder");
}

// scheme-specific credential form → stored securely under a reference name (SEC-CRED)
function credFields(kind) {
  const wrap = el("div");
  if (kind === "sftp") {
    wrap.innerHTML = `
      <label>Username</label><input data-f="user">
      <label>Authentication</label>
      <select data-f="mode"><option value="password">Password</option><option value="key">Private key</option></select>
      <div class="pw"><label>Password</label><input type="password" data-f="pass"></div>
      <div class="key" hidden><label>Private key (PEM)</label><textarea data-f="privateKey" rows="4"></textarea>
        <label>Passphrase (optional)</label><input type="password" data-f="passphrase"></div>`;
    const sel = wrap.querySelector('[data-f="mode"]'), pw = wrap.querySelector(".pw"), key = wrap.querySelector(".key");
    sel.onchange = () => { const isKey = sel.value === "key"; pw.hidden = isKey; key.hidden = !isKey; };
    return wrap;
  }
  wrap.innerHTML = (CRED_SPECS[kind] || []).map(([f, label, type]) =>
    type === "textarea"
      ? `<label>${label}</label><textarea data-f="${f}" rows="6" placeholder="paste the JSON key"></textarea>`
      : `<label>${label}</label><input type="${type}" data-f="${f}">`).join("");
  return wrap;
}

function credentialDialog(kind, onDone) {
  const body = el("div");
  const nameWrap = el("div");
  nameWrap.innerHTML = `<label>Reference name (the manifest stores only this handle, never the secret)</label>
    <input class="crn" placeholder="e.g. backup-nas">`;
  const fields = credFields(kind);
  body.append(nameWrap, fields);
  showModal("Credentials — " + kindLabel(kind), body, async () => {
    const name = body.querySelector(".crn").value.trim();
    if (!name) return "Enter a reference name.";
    const v = {};
    fields.querySelectorAll("[data-f]").forEach(e => v[e.dataset.f] = e.tagName === "TEXTAREA" ? e.value : e.value.trim());
    const { user, secret } = credPayload(kind, v);
    if (!secret) return "Fill in the credential fields.";
    const ok = await op("/api/credential/set", { name, user, secret });
    if (!ok) return "Could not store the credential.";
    onDone(name);
    return null;
  }, "Save credentials");
}

document.getElementById("new-pool").onclick = createPoolDialog;
checkPrereqs();
setInterval(checkPrereqs, 15000);

function connect() {
  const es = new EventSource("/api/stream?token=" + encodeURIComponent(token));
  const dot = document.getElementById("live-dot"), text = document.getElementById("live-text");
  es.onopen = () => { dot.className = "dot on"; text.textContent = "live"; };
  es.onmessage = e => { try { render(JSON.parse(e.data)); } catch (_) {} };
  es.onerror = () => { dot.className = "dot"; text.textContent = "reconnecting…"; };
}

// initial paint, then live stream
fetch("/api/pools", auth).then(r => r.json()).then(render).catch(() => {});
connect();
