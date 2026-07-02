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
    add("Mount", "primary", () => op("/api/pool/mount?pool=" + pool.id));
  add("Health", "", () => op("/api/health?pool=" + pool.id));
  add("Fix", "", () => op("/api/health?fix=true&pool=" + pool.id));
  add("Restore", "", () => op("/api/restore?pool=" + pool.id));
  add("Add member", "", () => addMemberDialog(pool));
  add("Delete", "danger", () => confirm(`Delete pool "${pool.name}"? The manifest is removed; your files are kept on disk.`) && op("/api/pool/delete?pool=" + pool.id));
  add("Purge", "danger", () => prompt(`PURGE wipes all data in "${pool.name}". Type the pool name to confirm:`) === pool.name && op("/api/pool/purge?pool=" + pool.id));
  return c;
}

async function op(url, body) {
  try {
    const opts = { method: "POST", headers: { ...auth.headers } };
    if (body !== undefined) { opts.headers["Content-Type"] = "application/json"; opts.body = JSON.stringify(body); }
    const r = await fetch(url, opts);
    const j = await r.json().catch(() => ({ ok: r.ok }));
    if (!j.ok) alert("Failed: " + (j.error || r.status));
    return j.ok;
  } catch (e) { alert("Request failed: " + e); return false; }
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
    const fresh = card(pool);
    if (existing) container.replaceChild(fresh, existing); else container.appendChild(fresh);
  }
  container.querySelectorAll(".card").forEach(c => { if (!seen.has(c.dataset.id)) c.remove(); });
}

// ---- modals: create pool & add member -------------------------------------
function closeModal() { document.getElementById("modal-root").innerHTML = ""; }

function memberEditor() {
  // returns { node, getMembers() } — a kind/location/role/credential row + running list
  const members = [];
  const wrap = el("div");
  wrap.innerHTML = `
    <label>Add member</label>
    <div class="row">
      <select class="k">${KINDS.map(k => `<option value="${k[0]}">${k[1]}</option>`).join("")}</select>
      <select class="r">${ROLES.map(r => `<option value="${r}">${r}</option>`).join("")}</select>
    </div>
    <div class="row" style="margin-top:6px">
      <input class="loc" placeholder="path or URI (e.g. D:\\ , \\\\server\\share, sftp://user@host/path)">
      <button type="button" class="addm" style="flex:0 0 auto">Add</button>
    </div>
    <input class="cred" style="margin-top:6px" placeholder="credential reference (remote members, optional)">
    <div class="memberlist"></div>`;
  const list = wrap.querySelector(".memberlist");
  const redraw = () => {
    list.innerHTML = "";
    members.forEach((m, i) => {
      const row = el("div", "m", `<span>${m.location}</span><span class="role">${m.role}</span>`);
      const rm = el("button", "small danger", "×"); rm.onclick = () => { members.splice(i, 1); redraw(); };
      row.appendChild(rm); list.appendChild(row);
    });
  };
  wrap.querySelector(".addm").onclick = () => {
    const loc = wrap.querySelector(".loc").value.trim(); if (!loc) return;
    members.push({ location: loc, role: wrap.querySelector(".r").value, credential: wrap.querySelector(".cred").value.trim() || null });
    wrap.querySelector(".loc").value = ""; wrap.querySelector(".cred").value = ""; redraw();
  };
  return { node: wrap, getMembers: () => members };
}

function showModal(title, bodyNode, onOk, okLabel) {
  const root = document.getElementById("modal-root");
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
  root.innerHTML = ""; root.appendChild(overlay);
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
  form.innerHTML = `<div class="row"><select class="k">${KINDS.map(k=>`<option value="${k[0]}">${k[1]}</option>`).join("")}</select>
    <select class="r">${ROLES.map(r=>`<option value="${r}">${r}</option>`).join("")}</select></div>
    <label>Location</label><input class="loc" placeholder="path or URI">
    <label>Credential reference (remote, optional)</label><input class="cred">`;
  showModal(`Add member to ${pool.name}`, form, async () => {
    const loc = form.querySelector(".loc").value.trim();
    if (!loc) return "Enter a location.";
    const ok = await op("/api/pool/add-member?pool=" + pool.id, {
      location: loc, role: form.querySelector(".r").value, credential: form.querySelector(".cred").value.trim() || null });
    return ok ? null : "Add failed.";
  }, "Add");
}

document.getElementById("new-pool").onclick = createPoolDialog;

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
