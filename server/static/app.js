// hostwatch dashboard: polls /api/agents and renders one card per PC,
// PCs with problems first.
'use strict';

const $ = (s) => document.querySelector(s);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
let filter = 'all';
let last = null;
let pollTimer = null;
let tab = 'computers';
let idleDay = null;      // null = today
let idleTimer = null;

function fmtBytes(b) {
  if (b == null) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (b >= 1024 && i < units.length - 1) { b /= 1024; i++; }
  return `${b.toFixed(b < 10 && i ? 1 : 0)} ${units[i]}`;
}
function fmtDur(sec) {
  if (sec == null) return '—';
  sec = Math.max(0, Math.round(sec));
  const d = Math.floor(sec / 86400), h = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : m ? `${m}m` : `${sec}s`;
}
function fmtSpeed(bps) {
  if (!bps) return null;
  return bps >= 1e9 ? `${+(bps / 1e9).toFixed(1)} Gbps` : `${Math.round(bps / 1e6)} Mbps`;
}
function fmtBits(bytesPerSec) {
  if (bytesPerSec == null) return '—';
  let b = bytesPerSec * 8;
  const units = ['bit/s', 'Kbit/s', 'Mbit/s', 'Gbit/s'];
  let i = 0;
  while (b >= 1000 && i < units.length - 1) { b /= 1000; i++; }
  return `${b.toFixed(b < 10 && i ? 1 : 0)} ${units[i]}`;
}
const level = (pct) => (pct >= 90 ? 'crit' : pct >= 75 ? 'hot' : '');

function spark(values, max = 100, cls = '') {
  if (!values || values.length < 2) return '';
  const top = Math.max(max, 1e-9);
  const pts = values.map((v, i) => `${((i / (values.length - 1)) * 100).toFixed(1)},${(21 - (Math.min(v, top) / top) * 19).toFixed(1)}`);
  return `<svg class="spark ${cls}" viewBox="0 0 100 22" preserveAspectRatio="none"><polygon points="0,22 ${pts.join(' ')} 100,22"/><polyline points="${pts.join(' ')}"/></svg>`;
}

function card(a, now) {
  const name = a.label || a.hostname || a.id;
  const bad = a.problems.length && a.status !== 'offline';
  const stateText = a.status === 'offline' ? `OFFLINE ${fmtDur(now - a.seen)}`
    : a.status === 'idle' ? `IDLE ${fmtDur(a.idle_seconds)}` : 'ACTIVE';
  const cpu = a.cpu || {};
  const mem = a.memory || {};
  const memPct = mem.total ? (100 * mem.used) / mem.total : 0;
  const primary = (a.networks || []).find((n) => n.primary) || (a.networks || [])[0] || {};
  const disks = (a.disks || []).map((d) => {
    const used = d.total - d.free;
    const pct = d.total ? (100 * used) / d.total : 0;
    return `<div class="metric"><div class="row"><span>DISK ${esc(d.name)} ${esc(d.label || '')}</span>
      <b>${fmtBytes(used)} / ${fmtBytes(d.total)} · ${fmtBytes(d.free)} free</b></div>
      <div class="bar"><i class="${level(pct)}" style="width:${pct.toFixed(1)}%"></i></div></div>`;
  }).join('');
  return `<article class="pc ${a.status} ${bad ? 'bad' : ''}">
    <div class="pc-head">
      <i class="dot"></i>
      <div class="pc-name"><h3>${esc(name)}</h3><p>${esc(a.label ? `${a.hostname} · ` : '')}${esc(a.user || '')}</p></div>
      <span class="state ${a.status}">${stateText}</span>
      <button class="icon-btn" data-act="label" data-id="${esc(a.id)}" title="Rename">✎</button>
      ${a.status === 'offline' ? `<button class="icon-btn" data-act="forget" data-id="${esc(a.id)}" title="Remove from list">✕</button>` : ''}
    </div>
    ${a.problems.length ? `<div class="problems">${a.problems.map((p) => `<span>▲ ${esc(p)}</span>`).join('')}</div>` : ''}
    <div class="net"><b>${esc(a.ip || a.ip_seen || '—')}</b> · ${esc(a.mac || a.id)}${primary.name ? ` · ${esc(primary.name)}` : ''}${fmtSpeed(primary.speed) ? ` · ${fmtSpeed(primary.speed)}` : ''}</div>
    <div class="metric"><div class="row"><span>CPU</span><b>${cpu.percent != null ? `${Math.round(cpu.percent)}%` : '—'} of ${cpu.cores || '?'} cores / ${cpu.threads || '?'} threads</b></div>
      <div class="bar"><i class="${level(cpu.percent)}" style="width:${Math.min(100, cpu.percent || 0)}%"></i></div>
      ${spark(a.cpu_hist)}<div class="sub">${esc(cpu.name || '')}</div></div>
    <div class="metric"><div class="row"><span>MEMORY</span><b>${fmtBytes(mem.used)} / ${fmtBytes(mem.total)} · ${Math.round(memPct)}%</b></div>
      <div class="bar"><i class="${level(memPct)}" style="width:${memPct.toFixed(1)}%"></i></div></div>
    ${netBlock(a)}
    ${disks}
    <div class="foot"><span>${esc(a.os || '')}</span><span>up ${fmtDur(a.uptime_seconds)} · idle ${fmtDur(a.idle_seconds)}</span></div>
  </article>`;
}

function netBlock(a) {
  const io = a.net_io;
  if (!io) return '';
  const peak = Math.max(1, ...(a.rx_hist || []), ...(a.tx_hist || []));
  return `<div class="metric"><div class="row"><span>NETWORK</span><b>↓ ${fmtBits(io.rx_bps)} · ↑ ${fmtBits(io.tx_bps)}</b></div>
    <div class="spark2">${spark(a.rx_hist, peak)}${spark(a.tx_hist, peak, 'up')}</div></div>`;
}

function rank(a) {
  if (a.problems.length && a.status !== 'offline') return 0;
  return { active: 1, idle: 2, offline: 3 }[a.status] ?? 4;
}

function render() {
  if (!last) return;
  const now = last.ts;
  const all = last.agents.slice().sort((a, b) => rank(a) - rank(b)
    || String(a.label || a.hostname).localeCompare(String(b.label || b.hostname)));
  const shown = all.filter((a) => filter === 'all'
    || (filter === 'problems' ? a.problems.length && a.status !== 'offline' : a.status === filter));
  $('#grid').innerHTML = shown.map((a) => card(a, now)).join('');
  $('#empty').hidden = all.length > 0;
  const count = (s) => all.filter((a) => a.status === s).length;
  const problems = all.filter((a) => a.problems.length && a.status !== 'offline').length;
  $('#counts').innerHTML = `${all.length} PCs · <b style="color:var(--green)">${count('active')} active</b> · `
    + `<b style="color:var(--amber)">${count('idle')} idle</b> · <b style="color:var(--dim)">${count('offline')} offline</b> · `
    + `<b style="color:${problems ? 'var(--red)' : 'var(--dim)'}">${problems} with problems</b>`;
  document.title = problems ? `(${problems}) HOSTWATCH` : 'HOSTWATCH';
}

async function poll() {
  try {
    const response = await fetch('api/agents', { cache: 'no-store' });
    if (!response.ok) throw new Error(response.status);
    last = await response.json();
    render();
    $('#live').classList.remove('lost');
    $('#live span').textContent = 'LIVE';
  } catch {
    $('#live').classList.add('lost');
    $('#live span').textContent = 'LINK LOST';
  }
  pollTimer = setTimeout(poll, 1000);
}

$('#filters').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  filter = b.dataset.f;
  for (const x of document.querySelectorAll('#filters button')) x.classList.toggle('on', x === b);
  render();
});

$('#grid').addEventListener('click', async (e) => {
  const b = e.target.closest('[data-act]');
  if (!b) return;
  const agent = last.agents.find((a) => a.id === b.dataset.id);
  if (b.dataset.act === 'label') {
    const label = prompt('Label for this PC (empty to clear)', agent?.label || '');
    if (label === null) return;
    await fetch(`api/agents/${encodeURIComponent(b.dataset.id)}/label`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ label }),
    });
  } else if (b.dataset.act === 'forget' && confirm(`Remove ${agent?.label || agent?.hostname} from the list? It comes back if the agent reports again.`)) {
    await fetch(`api/agents/${encodeURIComponent(b.dataset.id)}`, { method: 'DELETE' });
  }
  clearTimeout(pollTimer);
  poll();
});

// ---------- idle tab ----------
const hhmm = (ts) => new Date(ts * 1000).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

function timeline(r, start, end, now) {
  const span = end - start;
  const segs = r.segments.map(([a, b, state]) => `<i class="seg ${state}" style="left:${((a - start) / span * 100).toFixed(3)}%;width:${Math.max(0.15, (b - a) / span * 100).toFixed(3)}%" title="${state} ${hhmm(a)}–${hhmm(b)}"></i>`).join('');
  const nowLine = now < end ? `<i class="nowline" style="left:${((now - start) / span * 100).toFixed(2)}%"></i>` : '';
  return `<div class="tl">${segs}${nowLine}</div>`;
}

function renderIdle(d) {
  $('#idle-after').textContent = Math.round(d.idle_after / 60);
  $('#days').innerHTML = d.days.map((day, i) => `<button data-day="${day}" class="${day === d.day ? 'on' : ''}">${i === 0 ? 'TODAY' : i === 1 ? 'YESTERDAY' : day.slice(5)}</button>`).join('');
  const rows = d.rows.slice().sort((a, b) => b.idle - a.idle || String(a.name).localeCompare(String(b.name)));
  const axis = [0, 3, 6, 9, 12, 15, 18, 21, 24].map((h) => {
    const shift = h === 0 ? '0' : h === 24 ? '-100%' : '-50%';   // keep the end labels inside the bar
    return `<span style="left:${(h / 24) * 100}%;transform:translateX(${shift})">${String(h).padStart(2, '0')}</span>`;
  }).join('');
  const head = `<div class="irow head"><span>COMPUTER</span><span class="num">ACTIVE</span><span class="num">IDLE</span>
    <span class="num">IDLE SHARE</span><span class="num">LONGEST IDLE</span><div class="tlcell"><div class="tl-axis">${axis}</div></div></div>`;
  $('#idle-rows').innerHTML = rows.length ? head + rows.map((r) => {
    const tracked = r.active + r.idle;
    const pct = tracked ? (100 * r.idle) / tracked : 0;
    const now = r.status === 'idle' && r.current_idle_since
      ? `<div class="now">idle now · ${fmtDur(d.ts - r.current_idle_since + d.idle_after)}</div>` : '';
    return `<div class="irow">
      <div class="who"><b>${esc(r.name)}</b><span>${esc(r.hostname || '')}${r.user ? ` · ${esc(r.user)}` : ''}</span>${now}</div>
      <span class="num">${fmtDur(r.active)}</span>
      <span class="num" style="color:var(--amber)">${fmtDur(r.idle)}</span>
      <div class="pct"><div class="bar"><i style="width:${pct.toFixed(1)}%"></i></div><span>${Math.round(pct)}%</span></div>
      <span class="num">${r.longest_idle ? fmtDur(r.longest_idle) : '—'}</span>
      <div class="tlcell">${timeline(r, d.start, d.end, d.ts)}</div>
    </div>`;
  }).join('') : '<div class="empty"><p>No data for this day yet.</p></div>';
}

async function pollIdle() {
  clearTimeout(idleTimer);
  if (tab !== 'idle') return;
  try {
    const response = await fetch(`api/idle${idleDay ? `?day=${idleDay}` : ''}`, { cache: 'no-store' });
    renderIdle(await response.json());
  } catch { /* shown by the LIVE indicator */ }
  idleTimer = setTimeout(pollIdle, 10000);
}

$('#tabs').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  tab = b.dataset.tab;
  for (const x of document.querySelectorAll('#tabs button')) x.classList.toggle('on', x === b);
  $('#view-computers').hidden = tab !== 'computers';
  $('#view-idle').hidden = tab !== 'idle';
  try { localStorage.setItem('hostwatch.tab', tab); } catch { /* storage unavailable */ }
  pollIdle();
});

$('#days').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  idleDay = b.dataset.day === $('#days button').dataset.day ? null : b.dataset.day;
  pollIdle();
});

try {
  if (localStorage.getItem('hostwatch.tab') === 'idle') document.querySelector('#tabs [data-tab="idle"]').click();
} catch { /* storage unavailable */ }

poll();
