"use strict";
// Dependency-free live dashboard: SSE feed → animated meters, sparklines, tier topology.
const token = new URLSearchParams(location.search).get("token") || "";
const auth = { headers: { Authorization: "Bearer " + token } };
const history = new Map();   // poolId -> [hitRate samples]
const HIST = 120;            // 2 minutes of 1 Hz hit-rate history

function fmtBytes(n) {
  if (!n || n <= 0) return "0 B";
  const u = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
  let i = 0; while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(n < 10 && i > 0 ? 1 : 0) + " " + u[i];
}
function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }

// coarse relative age (5 s buckets under a minute) — coarse on purpose, so the rendered HTML
// doesn't change every second and re-trigger the anti-flicker diff
function ago(stamp) {
  if (!stamp) return "";
  const s = Math.max(0, (Date.now() - Date.parse(stamp)) / 1000);
  if (s < 5) return "· just now";
  if (s < 60) return "· " + Math.floor(s / 5) * 5 + "s ago";
  if (s < 3600) return "· " + Math.floor(s / 60) + "m ago";
  return "· " + Math.floor(s / 3600) + "h ago";
}

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

// ---- live flow map: pool entry → RAM cache → fast tier → capacity storage, with data-block ----
// particles flying on curves. The SVG PERSISTS across the 1 Hz refresh (rebuilding it would kill
// running animations); only labels update in place, and particles spawn from real activity rows
// and counter deltas, so what flies is what actually moves inside the pool.
const flowState = new Map(); // poolId -> { sig, svg, pos, latEls, ... }

function leafName(p) { return (p || "").replace(/[\\/]+$/, "").split(/[\\/]/).pop() || p; }

function pathBetween(a, b) {
  const ax = a.x + a.w, ay = a.y + a.h / 2, bx = b.x, by = b.y + b.h / 2;
  return `M ${ax} ${ay} C ${ax + 36} ${ay}, ${bx - 36} ${by}, ${bx} ${by}`;
}

function spawnParticle(svg, d, cls, dur, label) {
  const ns = "http://www.w3.org/2000/svg";
  const g = document.createElementNS(ns, "g");
  g.setAttribute("class", "pkt " + cls);
  const c = document.createElementNS(ns, "circle");
  c.setAttribute("r", "3.2");
  g.appendChild(c);
  if (label) {
    // the file's name travels with its data block, so distinct files are followable
    const t = document.createElementNS(ns, "text");
    t.setAttribute("y", "-5");
    t.setAttribute("text-anchor", "middle");
    t.textContent = label.length > 14 ? label.slice(0, 13) + "…" : label;
    g.appendChild(t);
  }
  const am = document.createElementNS(ns, "animateMotion");
  am.setAttribute("dur", (dur || 900) + "ms");
  am.setAttribute("path", d);
  am.setAttribute("fill", "freeze");
  am.setAttribute("calcMode", "spline");
  am.setAttribute("keyTimes", "0;1");
  am.setAttribute("keySplines", "0.4 0 0.2 1");
  g.appendChild(am);
  svg.appendChild(g);
  setTimeout(() => g.remove(), (dur || 900) + 200);
}

function burst(st, fromId, toId, cls, n, label) {
  const a = st.pos[fromId], b = st.pos[toId];
  if (!a || !b) return;
  const d = pathBetween(a, b);
  for (let i = 0; i < (n || 1); i++)
    setTimeout(() => st.svg.isConnected && spawnParticle(st.svg, d, cls, 850, i === 0 ? label : null), i * 140);
}

function buildFlowmap(wrap, pool, fast, cap) {
  const rows = Math.max(fast.length, cap.length, 1);
  const H = Math.max(64, 12 + rows * 38);
  const midY = Math.max(4, H / 2 - 22);
  const pos = { entry: { x: 1, y: midY, w: 58, h: 44 }, cache: { x: 89, y: midY, w: 58, h: 44 } };
  const esc = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;");
  const memberBox = (mm, x, y) => {
    pos[mm.id] = { x, y, w: 86, h: 30 };
    const name = leafName(mm.label || mm.path);
    return `<g class="fnode ${mm.online ? "" : "off"}"><rect x="${x}" y="${y}" width="86" height="30" rx="7"/>
      <text x="${x + 43}" y="${y + 12}" text-anchor="middle">${mm.online ? "" : "⚠"}${esc(name.length > 12 ? name.slice(0, 11) + "…" : name)}</text>
      <text class="lat" data-lat="${mm.id}" x="${x + 43}" y="${y + 21}" text-anchor="middle"></text>
      <rect class="filltrack" x="${x + 4}" y="${y + 24}" width="78" height="3" rx="1.5"/>
      <rect class="nodefill" data-fill="${mm.id}" x="${x + 4}" y="${y + 24}" width="0" height="3" rx="1.5"/></g>`;
  };
  const fastBoxes = fast.map((mm, i) => memberBox(mm, 177, 8 + i * 38)).join("");
  const capBoxes = cap.map((mm, i) => memberBox(mm, 295, 8 + i * 38)).join("");
  const guides = [pathBetween(pos.entry, pos.cache)];
  fast.forEach(mm => guides.push(pathBetween(pos.cache, pos[mm.id])));
  cap.forEach(mm => guides.push(pathBetween(fast.length ? pos[fast[0].id] : pos.cache, pos[mm.id])));
  wrap.innerHTML = `<svg class="flowmap" width="100%" viewBox="0 0 382 ${H}" preserveAspectRatio="xMidYMid meet">
    ${guides.map(d => `<path class="guide" d="${d}"/>`).join("")}
    <g class="fnode hub"><rect x="${pos.entry.x}" y="${pos.entry.y}" width="58" height="44" rx="8"/>
      <text x="${pos.entry.x + 29}" y="${pos.entry.y + 19}" text-anchor="middle">Pool I/O</text>
      <text class="lat" x="${pos.entry.x + 29}" y="${pos.entry.y + 33}" text-anchor="middle">${esc(pool.mounted || "not mounted")}</text></g>
    <g class="fnode hub"><rect x="${pos.cache.x}" y="${pos.cache.y}" width="58" height="44" rx="8"/>
      <text x="${pos.cache.x + 29}" y="${pos.cache.y + 19}" text-anchor="middle">RAM</text>
      <text class="lat" data-cachesub="1" x="${pos.cache.x + 29}" y="${pos.cache.y + 33}" text-anchor="middle">cache</text></g>
    ${fast.length ? `<text class="collabel" x="220" y="${H - 1}">fast (landing)</text>` : ""}
    <text class="collabel" x="338" y="${H - 1}">capacity</text>
    ${fastBoxes}${capBoxes}
  </svg>`;
  const svgEl = wrap.querySelector("svg");
  const latEls = {};
  svgEl.querySelectorAll("[data-lat]").forEach(t => latEls[t.dataset.lat] = t);
  const fillEls = {};
  svgEl.querySelectorAll("[data-fill]").forEach(r => fillEls[r.dataset.fill] = r);
  return {
    sig: null, svg: svgEl, pos, latEls, fillEls,
    cacheSub: svgEl.querySelector("[data-cachesub]"),
    firstFast: fast.length ? fast[0].id : null,
    firstCap: cap.length ? cap[0].id : null,
    lastStamp: null, lastRead: null, lastWrite: null,
  };
}

function memberNodeId(st, pool, displayName) {
  if (!displayName) return null;
  const hit = pool.members.find(mm => (mm.label || mm.path) === displayName || leafName(mm.label || mm.path) === leafName(displayName));
  return hit && st.pos[hit.id] ? hit.id : null;
}

function updateFlowmap(wrap, pool) {
  const fast = pool.members.filter(mm => mm.role === "landing");
  const cap = pool.members.filter(mm => mm.role !== "landing");
  const sig = pool.members.map(mm => mm.id + mm.role + (mm.online ? 1 : 0)).join("|") + "|" + (pool.mounted || "");
  let st = flowState.get(pool.id);
  if (!st || st.sig !== sig) {
    st = buildFlowmap(wrap, pool, fast, cap);
    st.sig = sig;
    flowState.set(pool.id, st);
  }
  const m = pool.metrics || {};

  // live labels: measured per-member latency (the auto-tier signal) + cache state
  const lat = {};
  (m.memberLatencies || []).forEach(l => lat[l.memberId] = l.avgMs);
  pool.members.forEach(mm => {
    const t = st.latEls[mm.id];
    if (t) t.textContent = !mm.online ? "offline" : lat[mm.id] != null ? lat[mm.id].toFixed(1) + " ms" : "";
    // live fill level inside the node: used vs free of this storage's volume
    const f = st.fillEls[mm.id];
    if (f && mm.bytesTotal > 0) {
      const frac = (mm.bytesTotal - mm.bytesFree) / mm.bytesTotal;
      f.setAttribute("width", Math.max(0, Math.min(78, Math.round(frac * 78))));
    }
  });
  if (st.cacheSub) st.cacheSub.textContent = (m.dirtyFiles || 0) > 0 ? m.dirtyFiles + " dirty" : "cache";
  if (!pool.mounted) return;

  // real activity rows first: distinct FILES flying between the exact nodes involved —
  // reads leave the member that served them, writes land on the member that took them.
  // Only rows NEWER than the last seen stamp animate: the ring buffer is history, and finished
  // work must not replay — neither on page load (baseline first) nor when rows carry no stamp.
  const newest = (m.activity || []).length ? m.activity[0].stamp : null;
  const fresh = [];
  if (st.lastStamp !== null && newest) {
    for (const a of m.activity || []) {
      if (!a.stamp || a.stamp <= st.lastStamp) break;
      fresh.push(a);
    }
  }
  if (newest !== null || st.lastStamp === null) st.lastStamp = newest || ""; // "" = baselined, nothing to replay

  let labeled = 0;
  for (const a of fresh.reverse()) { // oldest first so the motion reads causally
    if (labeled >= 6) break; // keep the map readable under load
    const name = leafName(a.path || "");
    const from = memberNodeId(st, pool, a.from), to = memberNodeId(st, pool, a.to);
    if (a.kind === "Read") {
      const src = from || st.firstFast || st.firstCap;
      if (src) { burst(st, src, "cache", "read", 1, name); setTimeout(() => burst(st, "cache", "entry", "read", 1, name), 420); ++labeled; }
    } else if (a.kind === "Write") {
      const dst = to || st.firstFast || st.firstCap;
      burst(st, "entry", "cache", "write", 1, name);
      if (dst) setTimeout(() => burst(st, "cache", dst, "write", 1, name), 420);
      ++labeled;
    } else if ((a.kind === "Drain" || a.kind === "Rebalance" || a.kind === "RemoteTransfer") && from && to) {
      burst(st, from, to, "drain", 1, name); ++labeled;
    } else if ((a.kind === "Duplicate" || a.kind === "ShadowCreate") && to) {
      burst(st, from || st.firstFast || "cache", to, "dup", 1, name); ++labeled;
    }
  }

  // ambient fallback: the activity feed is sampled/rate-limited, so when counters moved but no
  // row arrived this tick, unlabeled blocks still show the traffic
  const nOf = b => Math.max(1, Math.min(4, Math.round(Math.log2(1 + b / 4096))));
  const rd = st.lastRead == null ? 0 : (m.readBytes || 0) - st.lastRead;
  const wr = st.lastWrite == null ? 0 : (m.writtenBytes || 0) - st.lastWrite;
  st.lastRead = m.readBytes || 0;
  st.lastWrite = m.writtenBytes || 0;
  if (!labeled) {
    if (wr > 0) {
      burst(st, "entry", "cache", "write", nOf(wr));
      const tgt = st.firstFast || st.firstCap;
      if (tgt) setTimeout(() => burst(st, "cache", tgt, "write", 1), 400);
    }
    if (rd > 0) {
      const src = st.firstFast || st.firstCap;
      if (src) burst(st, src, "cache", "read", nOf(rd));
      setTimeout(() => burst(st, "cache", "entry", "read", 1), 400);
    }
  }
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
    <span>${m.label || m.path}</span>${m.network ? '<span class="badge info">remote</span>' : ""}`;
  if (pool.source === "manifest") {
    // live tier reconfiguration: switching the role reflows new writes immediately on a mounted pool
    const sel = el("select", "role-select");
    sel.innerHTML = ROLES.map(r => `<option value="${r}" ${r === m.role ? "selected" : ""}>${r}</option>`).join("");
    sel.title = "Change this storage's tier: landing = fast intake that drains to capacity; readonly = no new data";
    sel.onchange = async () => {
      if (!await op(`/api/pool/member-role?pool=${pool.id}&member=${encodeURIComponent(m.id)}&role=${sel.value}`))
        sel.value = m.role; // rejected — snap back
    };
    row.appendChild(sel);
  } else
    row.appendChild(el("span", "role", m.role));
  const scatter = el("button", "small danger", "remove");
  scatter.title = "Scatter this member's data to the others, then remove it";
  scatter.onclick = () => confirm(`Remove member "${m.label || m.path}"? Its data is scattered to the other members first.`)
    && op(`/api/pool/remove-member?pool=${pool.id}&member=${encodeURIComponent(m.id)}`);
  row.appendChild(scatter);

  // fill level: how full this storage's volume is (used vs free)
  if (m.bytesTotal > 0) {
    const used = m.bytesTotal - m.bytesFree;
    const frac = used / m.bytesTotal;
    row.appendChild(el("div", "fillbar", // used = dodgerblue, free = white — a glanceable gauge
      `<span style="width:${Math.min(100, Math.round(frac * 100))}%"></span>
       <i>${fmtBytes(used)} used · ${fmtBytes(m.bytesFree)} free</i>`));
  }
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
  // three zones: the flow map in the middle persists across refreshes (rebuilding it every second
  // would kill its running particle animations); top and bottom are re-rendered
  let top = c.querySelector(":scope > .card-top"), fm = c.querySelector(":scope > .flowmap-wrap"), bottom = c.querySelector(":scope > .card-bottom");
  if (!top) {
    top = el("div", "card-top");
    fm = el("div", "flowmap-wrap");
    bottom = el("div", "card-bottom");
    c.append(top, fm, bottom);
  }

  const health = pool.degraded ? '<span class="badge warn">degraded</span>' : '<span class="badge ok">healthy</span>';
  const mountBadge = pool.mounted ? `<span class="badge info">mounted ${pool.mounted}</span>` : "";
  // a mounted pool always shows its live-stats section (zeros until the first snapshot lands);
  // an unmounted pool shows a hint where the stats will appear
  const m = pool.metrics || (pool.mounted ? {} : null);
  const hist = history.get(pool.id) || [];

  const topHtml = `
    <h2>${pool.name} ${health} ${mountBadge}</h2>
    <div class="sub">${pool.source} · ${pool.failureDomains} failure domain(s) · <span title="primary placement strategy — change it via the Duplication dialog">⚖ ${pool.placementStrategy || "most-free-space"}</span>${pool.autoLandingZone ? ' · <span title="placement.autoLandingZone: the landing zone follows the measured-fastest drive automatically">🚀 auto-LZ</span>' : ""}</div>
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
      <div class="meter"><div class="label"><span>Read cache</span><span>${fmtBytes(m.cacheReadUsedBytes)} / ${fmtBytes(m.cacheReadMaxBytes)}</span></div>
        <div class="bar hit"><span style="width:${m.cacheReadMaxBytes ? Math.min(100, Math.round(m.cacheReadUsedBytes / m.cacheReadMaxBytes * 100)) : 0}%"></span></div></div>
      <div class="meter"><div class="label"><span>Write buffer</span><span>${fmtBytes(m.cacheWriteUsedBytes)} / ${fmtBytes(m.cacheWriteMaxBytes)}</span></div>
        <div class="bar write"><span style="width:${m.cacheWriteMaxBytes ? Math.min(100, Math.round(m.cacheWriteUsedBytes / m.cacheWriteMaxBytes * 100)) : 0}%"></span></div></div>
    </div>
    <div class="meter"><div class="label"><span>Hit-rate history (2 min)</span><span>read ${fmtBytes(m.readBytes)} · wrote ${fmtBytes(m.writtenBytes)} · drained ${m.drainedFiles||0}</span></div>
      ${sparkline(hist)}</div>` : '<div class="nostats">📊 Mount the pool to see live I/O statistics.</div>'}`;

  // anti-flicker: only touch the DOM when the rendered HTML actually changed — rebuilding
  // identical nodes every second makes badges/emoji shimmer and closes an open role dropdown
  if (c._topHtml !== topHtml) {
    top.innerHTML = topHtml;
    c._topHtml = topHtml;
  }

  updateFlowmap(fm, pool);

  const bottomHtml = `
    <div class="members"></div>
    ${pool.warnings && pool.warnings.length ? `<div class="warnings">${pool.warnings.map(w => "<div>⚠ " + w + "</div>").join("")}</div>` : ""}
    ${m ? `<div class="activity">${(m.activity && m.activity.length ? m.activity.slice(0,10).map(a => {
      const icon = ({Read:"📖",Write:"✍️",Drain:"⬇️",Duplicate:"🔁",Rebalance:"⚖️",RemoteTransfer:"☁️",CacheAdmit:"📥",CacheEvict:"📤",Recovery:"🩹",Scrub:"🔬",TrashMove:"🗑️"})[a.kind] || "•";
      const move = a.from || a.to ? ` <span class="mv">${a.from || "?"} → ${a.to || "?"}</span>` : "";
      return `<div>${icon} ${a.kind} <b>${a.path||""}</b> ${a.bytes?fmtBytes(a.bytes):""}${move} ${a.reason?`<span class="rsn">${a.reason}</span>`:""} <span class="age">${ago(a.stamp)}</span></div>`;
    }).join("") : '<div class="rsn">no activity yet — reads, writes, drains and duplications appear here live</div>')}</div>` : ""}
    <div class="actions"></div>`;

  if (c._bottomHtml === bottomHtml)
    return; // nothing below the flow map changed — keep the existing nodes (and any open dropdown)

  bottom.innerHTML = bottomHtml;
  c._bottomHtml = bottomHtml;

  const memberBox = bottom.querySelector(".members");
  pool.members.forEach(mem => memberBox.appendChild(memberRow(pool, mem)));

  const actions = bottom.querySelector(".actions");
  const add = (label, cls, fn) => { const b = el("button", cls, label); b.onclick = fn; actions.appendChild(b); };
  if (pool.mounted)
    add("Unmount", "", () => op("/api/pool/unmount?pool=" + pool.id));
  else
    add("Mount", "primary", () => mountPool(pool));
  add("Browse", "", () => browseDialog(pool, ""));
  add("Health", "", () => healthDialog(pool, false));
  add("Fix", "", () => healthDialog(pool, true));
  add("Restore", "", async () => { if (await op("/api/restore?pool=" + pool.id)) alert("Restore finished — missing copies were recreated."); });
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

// modal with just a Close button (reports, browsers)
function infoModal(title, bodyNode, wide) {
  const overlay = el("div", "overlay");
  const modal = el("div", "modal" + (wide ? " wide" : ""));
  const actions = el("div", "modal-actions");
  const close = el("button", "primary", "Close"); close.onclick = closeModal;
  actions.appendChild(close);
  modal.append(el("h2", null, title), bodyNode, actions);
  overlay.appendChild(modal);
  overlay.onclick = e => { if (e.target === overlay) closeModal(); };
  document.getElementById("modal-root").appendChild(overlay);
  return modal;
}

// Problem scan: runs the health check and shows the full report — under-duplicated files,
// integrity issues (stale copies, conflicts, external edits), device SMART. The default scan
// checks metadata only (fast, never changes anything); the DEEP scan re-checksums every file
// to also surface silent bit-rot (reads all pool data — can take a long time); FIX repairs.
async function healthDialog(pool, fix, deep) {
  const body = el("div");
  body.innerHTML = `<p class="hint">${fix ? "Scanning and repairing" : deep ? "Deep-scanning (re-checksumming every file — this can take a long time)" : "Scanning"} <b>${pool.name}</b> —
    checking duplication levels, integrity and device health…</p>`;
  infoModal(fix ? "Fix problems" : deep ? "Deep scan (bit-rot)" : "Pool problem scan", body);

  const j = await post("/api/health?pool=" + pool.id + (fix ? "&fix=true" : "") + (deep && !fix ? "&deep=true" : ""));
  if (!j.ok) { body.innerHTML = `<p class="hint">Scan failed: ${j.error || "unknown error"}</p>`; return; }
  const r = j.result;
  const smartBadge = h => h === "Healthy" ? '<span class="badge ok">healthy</span>'
    : h === "Failing" ? '<span class="badge bad">FAILING</span>'
    : h === "Warning" ? '<span class="badge warn">warning</span>' : '<span class="badge info">unknown</span>';
  body.innerHTML = `
    <p>${r.healthy ? '<span class="badge ok">no problems found</span>' : '<span class="badge warn">attention needed</span>'}
       ${r.deep ? '<span class="badge info">deep scan — every byte verified</span>' : '<span class="badge info">metadata scan</span>'}</p>
    <div class="report-row"><span>Files below their duplication level</span><b>${r.underDuplicatedFiles}</b></div>
    ${r.corrected ? `<div class="report-row"><span>Copies repaired / created</span><b>${r.copiesRepaired}</b></div>` : ""}
    ${r.issues && r.issues.length ? `<label>Integrity issues</label><div class="issues">${r.issues.map(i =>
      `<div>⚠ <b>${i.kind}</b> ${i.path}<br><span class="rsn">${i.message}</span></div>`).join("")}</div>` : ""}
    <label>Device health (SMART)</label>
    <div class="issues">${(r.members || []).map(mm =>
      `<div>${smartBadge(mm.health)} <b>${mm.name}</b>${mm.model ? " · " + mm.model : ""}${mm.temperatureC != null ? " · " + mm.temperatureC + "°C" : ""}${mm.reallocatedSectors ? " · " + mm.reallocatedSectors + " reallocated sectors" : ""}${mm.detail ? `<br><span class="rsn">${mm.detail}</span>` : ""}</div>`).join("")}</div>
    ${!fix && !r.deep ? `<p class="hint">This scan checks metadata only and never changes anything — missing duplicates, inconsistent file sizes and external edits show up here. Silent bit-rot needs the <b>deep scan</b>, which re-reads and re-checksums every file (it can take a long time on a large pool).</p>` : ""}
    ${!fix && !r.healthy ? `<p class="hint"><b>Fix</b> repairs what was found: bit-rot from a good copy, stale copies re-synced, conflicts preserved, missing copies recreated.</p>` : ""}`;
  if (!fix) {
    const row = el("div", "modal-actions");
    if (!r.deep) {
      const deepBtn = el("button", "", "Deep scan (bit-rot)");
      deepBtn.onclick = () => { closeModal(); healthDialog(pool, false, true); };
      row.appendChild(deepBtn);
    }
    if (!r.healthy) {
      const fixBtn = el("button", "primary", "Fix problems now");
      fixBtn.onclick = () => { closeModal(); healthDialog(pool, true); };
      row.appendChild(fixBtn);
    }
    if (row.childNodes.length)
      body.appendChild(row);
  }
}

// Pool browser (FR-UI-MAP): a tree-style listing with one column per member showing exactly
// where every file/folder physically lives — ✅ primary copy, 🔁 shadow copy, ❌ not present.
async function browseDialog(pool, path) {
  const body = el("div");
  body.innerHTML = `<p class="hint">Loading…</p>`;
  infoModal(`Browse — ${pool.name}`, body, true);
  await browseInto(pool, path, body);
}

async function browseInto(pool, path, body) {
  const j = await fetch(`/api/pool/browse?pool=${pool.id}&path=${encodeURIComponent(path)}&token=${encodeURIComponent(token)}`)
    .then(r => r.json()).catch(e => ({ ok: false, error: String(e) }));
  if (!j.ok) { body.innerHTML = `<p class="hint">Browse failed: ${j.error || ""}</p>`; return; }
  const r = j.result;
  const leaf = p => (p || "").replace(/[\\/]+$/, "").split(/[\\/]/).pop() || p;
  const crumbs = r.path ? r.path.split("/") : [];
  body.innerHTML = `
    <div class="fp-path">🗂 /${r.path || ""}</div>
    <table class="browse"><thead><tr>
      <th style="text-align:left">Name</th><th>Size</th>
      ${r.members.map(mm => `<th title="${mm.label}">${leaf(mm.label)}</th>`).join("")}
    </tr></thead><tbody>
      ${r.path ? `<tr class="dir up"><td>⬆ ..</td><td></td>${r.members.map(() => "<td></td>").join("")}</tr>` : ""}
      ${r.entries.map(e => `<tr class="${e.isDirectory ? "dir" : ""}" data-name="${e.name.replace(/"/g, "&quot;")}">
        <td>${e.isDirectory ? "📁" : "📄"} ${e.name}</td>
        <td class="sz">${e.isDirectory ? "" : fmtBytes(e.length)}</td>
        ${e.presence.map(p => `<td class="pm">${p.primary ? "✅" : p.shadow ? "" : "❌"}${p.shadow ? "🔁" : ""}</td>`).join("")}
      </tr>`).join("")}
      ${!r.entries.length ? `<tr><td colspan="${2 + r.members.length}" class="rsn">(empty folder)</td></tr>` : ""}
    </tbody></table>
    <p class="hint">✅ primary copy · 🔁 shadow (duplicate) copy · ❌ not on this storage. Folders count as present when the member holds the folder itself.</p>`;
  body.querySelectorAll("tr.dir").forEach(row => {
    row.onclick = () => {
      if (row.classList.contains("up")) {
        const upPath = crumbs.slice(0, -1).join("/");
        browseInto(pool, upPath, body);
      } else
        browseInto(pool, (r.path ? r.path + "/" : "") + row.dataset.name, body);
    };
  });
}

// nested-path helpers for the visual settings form (paths like "write.policy")
function getPath(o, path) { return path.split(".").reduce((a, k) => (a == null ? undefined : a[k]), o); }
function setPath(o, path, v) {
  const ks = path.split("."); let a = o;
  for (let i = 0; i < ks.length - 1; i++) { if (typeof a[ks[i]] !== "object" || a[ks[i]] == null) a[ks[i]] = {}; a = a[ks[i]]; }
  a[ks[ks.length - 1]] = v;
}
function deletePath(o, path) {
  const ks = path.split("."); let a = o; const stack = [];
  for (let i = 0; i < ks.length - 1; i++) { if (a == null || typeof a[ks[i]] !== "object") return; stack.push([a, ks[i]]); a = a[ks[i]]; }
  delete a[ks[ks.length - 1]];
  for (let i = stack.length - 1; i >= 0; i--) { const [par, k] = stack[i]; if (par[k] && typeof par[k] === "object" && !Object.keys(par[k]).length) delete par[k]; }
}

// The knobs shown as form controls. Everything else stays reachable via the Advanced (JSON) box,
// and omitted controls keep inheriting the built-in default (§8). "inherit" = don't override.
const SETTINGS_SCHEMA = [
  ["Writing", [
    { path: "write.policy", label: "Write policy", type: "enum", options: [
      ["write-through", "Write-through — safest; acknowledge only once on stable storage"],
      ["write-back", "Write-back — cache writes and flush in the background (faster)"],
      ["deferred", "Deferred — batch writes over a short window"],
      ["performance", "Performance — lowest latency, weakest durability"]] },
    { path: "write.minCopiesBeforeAck", label: "Copies that must be written before a write is acknowledged", type: "int", min: 1, max: 10 },
  ]],
  ["Cache", [
    { path: "cache.size", label: "Cache size (e.g. 4GiB, 512MiB, or 10%)", type: "size" },
    { path: "readAhead.enabled", label: "Read-ahead — prefetch sequential reads", type: "bool" },
  ]],
  ["Background maintenance", [
    { path: "background.balancerEnabled", label: "Rebalance data across drives in the background", type: "bool" },
    { path: "background.duplicatorEnabled", label: "Create owed duplicate copies in the background", type: "bool" },
    { path: "background.maxThroughput", label: "Background throughput cap (e.g. 100MiB; empty = unlimited)", type: "size" },
  ]],
  ["Safety", [
    { path: "safety.journalEnabled", label: "Write-ahead journal (crash-consistent)", type: "bool" },
    { path: "safety.refuseMountOnUnrecoverable", label: "Refuse to mount on unrecoverable damage", type: "bool" },
    { path: "placement.autoLandingZone", label: "Auto landing zone — let the fastest drive lead", type: "bool" },
  ]],
  ["Resilience", [
    { path: "resilience.onMemberLoss", label: "When a member drive goes offline", type: "enum", options: [
      ["retain-metadata", "Keep serving — complete metadata from the in-memory shadow"],
      ["discard-inaccessible", "Hide files that live only on the lost member"]] },
    { path: "resilience.memberPollSeconds", label: "Re-check an offline member every (seconds)", type: "number", min: 1 },
  ]],
  ["Integrity", [
    { path: "integrity.checksumDb", label: "Maintain a checksum database (enables scrub / heal)", type: "bool" },
    { path: "integrity.onExternalEdit", label: "When a file is edited outside the pool", type: "enum", options: [
      ["accept-newest", "Accept the newest copy"],
      ["conflict-only", "Flag it as a conflict only"],
      ["read-only-until-reconciled", "Make it read-only until reconciled"]] },
  ]],
];

// Visual settings: a form of the common knobs + the pool's mount location, with an Advanced (JSON)
// box that still exposes every setting. The dedicated Duplication dialog owns copies/placement.
async function settingsDialog(pool) {
  const j = await fetch("/api/pool/config?pool=" + pool.id, auth).then(r => r.json()).catch(() => ({}));
  const data = (j && j.result) || {};
  let cfg = {}; try { cfg = data.current ? JSON.parse(data.current) : {}; } catch (_) { cfg = {}; }
  const template = (() => { try { return JSON.stringify(JSON.parse(data.template || "{}"), null, 2); } catch (_) { return "{}"; } })();

  const form = el("div");
  form.append(el("p", "hint", "Overrides on top of the built-in defaults. Leave a control on “inherit” to keep the default. Changes apply live to a mounted pool, otherwise on the next mount."));

  // --- mount location -------------------------------------------------------
  const mountWrap = el("div", "setgroup");
  mountWrap.innerHTML = `<h3>Mount location</h3>
    <div class="row"><input class="mtpath" placeholder="X:\\ or /mnt/pool (empty = ask each time)" value="${(pool.configuredTarget || "").replace(/"/g, "&quot;")}">
    <button type="button" class="mtbrowse" style="flex:0 0 auto">📁 Browse</button></div>
    <p class="hint">The default place this pool mounts. Pick a drive letter (Windows) or an empty folder you own (Linux/macOS).</p>`;
  mountWrap.querySelector(".mtbrowse").onclick = () => folderPicker(p => { mountWrap.querySelector(".mtpath").value = p; });
  form.appendChild(mountWrap);

  // --- schema-driven controls ----------------------------------------------
  const controls = [];
  for (const [section, fields] of SETTINGS_SCHEMA) {
    const g = el("div", "setgroup");
    g.appendChild(el("h3", null, section));
    for (const f of fields) {
      const cur = getPath(cfg, f.path);
      g.appendChild(el("label", null, f.label));
      let input;
      if (f.type === "enum" || f.type === "bool") {
        input = el("select");
        const opts = f.type === "bool" ? [["true", "On"], ["false", "Off"]] : f.options;
        input.innerHTML = `<option value="">(inherit default)</option>` +
          opts.map(([v, l]) => `<option value="${v}">${l}</option>`).join("");
        input.value = cur === undefined || cur === null ? "" : String(cur);
      } else {
        input = el("input");
        input.type = f.type === "int" || f.type === "number" ? "number" : "text";
        if (f.min !== undefined) input.min = f.min;
        if (f.max !== undefined) input.max = f.max;
        input.placeholder = "inherit default";
        input.value = cur === undefined || cur === null ? "" : cur;
      }
      g.appendChild(input);
      controls.push({ f, input });
    }
    form.appendChild(g);
  }

  // --- advanced (raw JSON) escape hatch ------------------------------------
  let rawEdited = false;
  const adv = el("details"); adv.style.marginTop = "10px";
  adv.innerHTML = `<summary class="hint" style="cursor:pointer">Advanced — edit every setting as JSON</summary>
    <p class="hint">Editing here overrides the form above on save. Omitted keys keep inheriting.</p>
    <textarea class="cfg" rows="12" spellcheck="false"></textarea>
    <div class="row" style="margin-top:6px"><button type="button" class="cfg-template" style="flex:0 0 auto">Load all defaults as a template</button></div>`;
  const area = adv.querySelector(".cfg");
  area.value = Object.keys(cfg).length ? JSON.stringify(cfg, null, 2) : "";
  area.oninput = () => { rawEdited = true; };
  adv.querySelector(".cfg-template").onclick = () => { area.value = template; rawEdited = true; };
  form.appendChild(adv);

  showModal(`Settings — ${pool.name}`, form, async () => {
    // 1) mount location (its own endpoint)
    const mt = mountWrap.querySelector(".mtpath").value.trim();
    if (mt !== (pool.configuredTarget || "")) {
      const r = await post("/api/pool/mount-target?pool=" + pool.id, { target: mt });
      if (!r.ok) return "Mount location was rejected: " + (r.error || "");
    }

    // 2) settings: raw JSON overrides the form when edited
    let out;
    if (rawEdited) {
      const text = area.value.trim();
      if (!text) out = {};
      else { try { out = JSON.parse(text); } catch (e) { return "Advanced JSON is not valid: " + e.message; } }
    } else {
      out = JSON.parse(JSON.stringify(cfg)); // preserve keys not covered by the form
      for (const { f, input } of controls) {
        const v = input.value.trim();
        if (v === "") { deletePath(out, f.path); continue; }
        if (f.type === "bool") setPath(out, f.path, v === "true");
        else if (f.type === "int") setPath(out, f.path, parseInt(v, 10));
        else if (f.type === "number") setPath(out, f.path, parseFloat(v));
        else setPath(out, f.path, v);
      }
    }
    const r = await post("/api/pool/config?pool=" + pool.id, { json: JSON.stringify(out) });
    return r.ok ? null : "Settings were rejected" + (r.error ? ": " + r.error : "") + ".";
  }, "Save settings");
}

// Configure copies + where new primaries land — pool-wide and optionally per folder/file.
function duplicationDialog(pool) {
  const STRATEGIES = [
    ["most-free-space", "Most free space — fill the emptiest drive first (balances usage)"],
    ["round-robin", "Round-robin — spread consecutive files across drives (parallel throughput, lower latency)"],
    ["least-used", "Least used — favour the drive holding the least data"],
    ["lowest-latency", "Lowest latency — the drive that currently measures fastest (live EWMA)"],
  ];
  const form = el("div");
  form.innerHTML = `
    <label>Where do new files land (primary placement strategy)</label>
    <select class="strategy">${STRATEGIES.map(([v, l]) => `<option value="${v}" ${v === (pool.placementStrategy || "most-free-space") ? "selected" : ""}>${l}</option>`).join("")}</select>
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
    disk failing. Changes apply <b>live</b> to a mounted pool — owed copies are created right away —
    otherwise on the next mount.</p>
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
    const strategy = form.querySelector(".strategy").value;
    return await op("/api/pool/duplication?pool=" + pool.id, { level, allowSamePhysical, strategy }) ? null : "Could not set duplication.";
  }, "Save");
}

// Mount the pool. We ask the daemon WITHOUT a target so it uses the pool's CURRENT manifest target
// (a just-changed mount location applies immediately, not the value this client last cached); only
// when the daemon reports no target set do we ask the user where to mount.
async function mountPool(pool) {
  const j = await post("/api/pool/mount?pool=" + pool.id);
  if (j.ok) return;
  if (!j.needsTarget) {
    if (j.needsPrereq && j.installable && confirm(`${j.error}\n\nInstall ${j.driver} now?`)) { await installDriver(); return; }
    alert("Failed: " + (j.error || ""));
    return;
  }
  // No configured target — ask in an in-app modal. A hosting WebView's window.prompt() is
  // unreliable (WebKitGTK returns null), which made "Mount" silently do nothing.
  const form = el("div");
  form.innerHTML = `<label>Mount location</label>
    <input class="mt" placeholder="X:\\ or /mnt/mypool">
    <p class="hint">Windows: a free drive letter like <code>X:</code> or an empty folder.<br>
    Linux/macOS: an empty directory such as <code>/mnt/pool</code>.<br>
    Set a permanent one in <b>Settings</b>.</p>`;
  const browse = el("button", null, "📁 Browse"); browse.style.marginTop = "6px";
  browse.onclick = () => folderPicker(p => { form.querySelector(".mt").value = p; });
  form.appendChild(browse);
  showModal(`Mount ${pool.name}`, form, async () => {
    const target = form.querySelector(".mt").value.trim();
    if (!target) return "Enter a mount location.";
    const ok = await op("/api/pool/mount?pool=" + pool.id + "&target=" + encodeURIComponent(target));
    return ok ? null : "Mount failed.";
  }, "Mount");
}

async function post(url, body) {
  // one silent retry through a transient gap (e.g. the daemon restarting on its stable port):
  // a network-level "Failed to fetch" is retried after a short pause before it ever surfaces
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const opts = { method: "POST", headers: { ...auth.headers } };
      if (body !== undefined) { opts.headers["Content-Type"] = "application/json"; opts.body = JSON.stringify(body); }
      const r = await fetch(url, opts);
      return await r.json().catch(() => ({ ok: r.ok }));
    } catch (e) {
      if (attempt === 0) { await new Promise(res => setTimeout(res, 1200)); continue; }
      return { ok: false, error: "the manager service is momentarily unavailable — please retry" };
    }
  }
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
  container.querySelectorAll(".card").forEach(c => { if (!seen.has(c.dataset.id)) { flowState.delete(c.dataset.id); c.remove(); } });
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
  // returns { node, getMembers() } — a stack of open member-setting sections. Each "Add member"
  // click opens ANOTHER section; every open section that has a location is collected into the pool
  // on Create, with no separate per-member confirm step.
  const wrap = el("div");
  wrap.appendChild(el("label", null, "member"));
  const sections = el("div", "member-sections");
  wrap.appendChild(sections);
  const controls = [];
  const addSection = () => {
    const sec = el("div", "membersec");
    const ctl = locationControls();
    sec.appendChild(ctl.node);
    const rm = el("button", "small danger", "Remove member"); rm.style.marginTop = "8px";
    rm.onclick = () => {
      const i = controls.indexOf(ctl);
      if (i >= 0) controls.splice(i, 1);
      sec.remove();
      if (!controls.length) addSection(); // always keep at least one section open
    };
    sec.appendChild(rm);
    controls.push(ctl);
    sections.appendChild(sec);
    return ctl;
  };
  addSection(); // start with one open section
  const addBtn = el("button", null, "Add member"); addBtn.style.marginTop = "8px";
  addBtn.onclick = () => addSection();
  wrap.appendChild(addBtn);
  return {
    node: wrap,
    getMembers: () => controls
      .map(c => ({ location: c.getLocation(), role: c.getRole(), credential: c.getCred() }))
      .filter(m => m.location),
  };
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
