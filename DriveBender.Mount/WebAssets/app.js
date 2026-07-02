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

function memberRow(m) {
  return `<div class="member"><span class="status ${m.online ? "" : "off"}"></span>
    <span>${m.label || m.path}</span>${m.network ? '<span class="badge info">remote</span>' : ""}
    <span class="role">${m.role}</span></div>`;
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
    <div class="members">${pool.members.map(memberRow).join("")}</div>
    ${pool.warnings && pool.warnings.length ? `<div class="warnings">${pool.warnings.map(w => "<div>⚠ " + w + "</div>").join("")}</div>` : ""}
    ${m && m.activity && m.activity.length ? `<div class="activity">${m.activity.slice(0,8).map(a =>
      `<div>${a.kind} ${a.path||""} ${a.bytes?fmtBytes(a.bytes):""} ${a.reason||""}</div>`).join("")}</div>` : ""}
    <div class="actions">
      <button data-act="health">Health check</button>
      <button data-act="fix" class="primary">Check &amp; fix</button>
      <button data-act="restore">Restore</button>
    </div>`;

  c.querySelector('[data-act="health"]').onclick = () => op("/api/health?pool=" + pool.id);
  c.querySelector('[data-act="fix"]').onclick = () => op("/api/health?fix=true&pool=" + pool.id);
  c.querySelector('[data-act="restore"]').onclick = () => op("/api/restore?pool=" + pool.id);
  return c;
}

async function op(url) {
  try {
    const r = await fetch(url, { method: "POST", ...auth });
    const j = await r.json();
    alert(j.ok ? "Done: " + JSON.stringify(j.result) : "Failed: " + j.error);
  } catch (e) { alert("Request failed: " + e); }
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
