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
let lastWork = null;
let holdRenderUntil = 0;   // pause grid re-render briefly so a click isn't lost to a refresh

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
    : a.status === 'off' ? 'OFF-HOURS'
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
      <div class="pc-name"><h3>${esc(name)}</h3>${a.label && a.hostname ? `<p>${esc(a.hostname)}</p>` : ''}${a.user ? `<p>${esc(a.user)}</p>` : ''}${a.session_id != null && a.session_id >= 0 ? `<p>session ${a.session_id}</p>` : ''}</div>
      <span class="state ${a.status}">${stateText}</span>
      <button class="icon-btn" data-act="label" data-id="${esc(a.id)}" title="Rename">✎</button>
      ${a.status === 'offline' ? `<button class="icon-btn" data-act="forget" data-id="${esc(a.id)}" title="Remove from list">✕</button>` : ''}
    </div>
    ${a.problems.length ? `<div class="problems">${a.problems.map((p) => `<span>▲ ${esc(p)}</span>`).join('')}</div>` : ''}
    <div class="net"><b>${esc(a.ip || a.ip_seen || '—')}</b>${(a.ip || a.ip_seen) ? ` <button class="mini" data-act="rdp" data-ip="${esc(a.ip || a.ip_seen)}" data-host="${esc(name)}" title="Open Remote Desktop (RDP) to this PC - starts a new session">RDP</button>` : ''}${(a.ip || a.ip_seen) && a.session_id != null && a.session_id >= 0 ? ` <button class="mini" data-act="shadow" data-ip="${esc(a.ip || a.ip_seen)}" data-sid="${a.session_id}" data-host="${esc(name)}" title="Join the user's current session (RDP shadow - view & control, no logout)">SHADOW</button>` : ''} · ${esc(a.mac || a.id)}${primary.name ? ` · ${esc(primary.name)}` : ''}${fmtSpeed(primary.speed) ? ` · ${fmtSpeed(primary.speed)}` : ''}</div>
    <div class="metric"><div class="row"><span>CPU</span><b>${cpu.percent != null ? `${Math.round(cpu.percent)}%` : '—'} of ${cpu.cores || '?'} cores / ${cpu.threads || '?'} threads</b></div>
      <div class="bar"><i class="${level(cpu.percent)}" style="width:${Math.min(100, cpu.percent || 0)}%"></i></div>
      ${spark(a.cpu_hist)}<div class="sub">${esc(cpu.name || '')}</div></div>
    <div class="metric"><div class="row"><span>MEMORY${a.status !== 'offline' ? ` <button class="mini" data-act="clearcache" data-id="${esc(a.id)}" title="Free cached memory (RAM) on this PC">free</button>` : ''}</span><b>${fmtBytes(mem.used)} / ${fmtBytes(mem.total)} · ${Math.round(memPct)}%</b></div>
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
  return { active: 1, idle: 2, off: 3, offline: 4 }[a.status] ?? 5;
}

function render() {
  if (!last || Date.now() < holdRenderUntil) return;
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
    + `<b style="color:var(--amber)">${count('idle')} idle</b> · <b style="color:var(--slate)">${count('off')} off-hours</b> · `
    + `<b style="color:var(--dim)">${count('offline')} offline</b> · `
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

$('#grid').addEventListener('pointerdown', (e) => {
  if (e.target.closest('[data-act]')) holdRenderUntil = Date.now() + 1500;
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
  } else if (b.dataset.act === 'rdp') {
    const rdp = [`full address:s:${b.dataset.ip}:3389`, 'prompt for credentials:i:1',
      'screen mode id:i:2', 'authentication level:i:2', ''].join('\r\n');
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([rdp], { type: 'application/x-rdp' }));
    link.download = `${(b.dataset.host || b.dataset.ip).replace(/[^\w.-]+/g, '_')}.rdp`;
    document.body.appendChild(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(link.href), 10000);
    return;
  } else if (b.dataset.act === 'shadow') {
    const cmd = `@echo off\r\nrem Join ${b.dataset.host}'s current session (view & control, no logout).\r\nrem Run as an admin of the target PC. Drop /control for view-only.\r\nmstsc /shadow:${b.dataset.sid} /v:${b.dataset.ip} /control\r\n`;
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([cmd], { type: 'application/octet-stream' }));
    link.download = `${(b.dataset.host || b.dataset.ip).replace(/[^\w.-]+/g, '_')}-shadow.cmd`;
    document.body.appendChild(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(link.href), 10000);
    return;
  } else if (b.dataset.act === 'clearcache') {
    b.textContent = 'sent';
    await fetch(`api/agents/${encodeURIComponent(b.dataset.id)}/command`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ command: 'clear_cache' }),
    });
    return;   // let the memory bar drop on the next polls
  } else if (b.dataset.act === 'forget' && confirm(`Remove ${agent?.label || agent?.hostname} from the list? It comes back if the agent reports again.`)) {
    await fetch(`api/agents/${encodeURIComponent(b.dataset.id)}`, { method: 'DELETE' });
  }
  clearTimeout(pollTimer);
  poll();
});

// ---------- idle tab ----------
const hhmm = (ts) => new Date(ts * 1000).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

const toMin = (hhmm) => { const [h, m] = hhmm.split(':'); return (+h) * 60 + (+m); };

// The timeline spans only the work schedule: from the first interval's start to the last one's end.
function workMinutes(work) {
  const intervals = (work && work.intervals && work.intervals.length) ? work.intervals : [['00:00', '24:00']];
  return [Math.min(...intervals.map((iv) => toMin(iv[0]))), Math.max(...intervals.map((iv) => toMin(iv[1])))];
}

function workBands(work, lo, hi) {
  const span = hi - lo;
  if (!work || !work.intervals || span <= 0) return '';
  return work.intervals.map(([a, b]) => `<i class="work" style="left:${((toMin(a) - lo) / span * 100).toFixed(3)}%;width:${((toMin(b) - toMin(a)) / span * 100).toFixed(3)}%"></i>`).join('');
}

function timeline(r, start, now, work) {
  const [lo, hi] = workMinutes(work);
  const winStart = start + lo * 60, winEnd = start + hi * 60, span = winEnd - winStart || 1;
  const segs = r.segments.map(([a, b, state]) => {
    const from = Math.max(a, winStart), to = Math.min(b, winEnd);
    if (to <= from) return '';
    return `<i class="seg ${state}" style="left:${((from - winStart) / span * 100).toFixed(3)}%;width:${Math.max(0.15, (to - from) / span * 100).toFixed(3)}%" title="${state} ${hhmm(a)}–${hhmm(b)}"></i>`;
  }).join('');
  const nowLine = winStart <= now && now < winEnd ? `<i class="nowline" style="left:${((now - winStart) / span * 100).toFixed(2)}%"></i>` : '';
  return `<div class="tl">${workBands(work, lo, hi)}${segs}${nowLine}</div>`;
}

function workAxis(work) {
  const [lo, hi] = workMinutes(work);
  const span = hi - lo || 1;
  const spanH = span / 60;
  const step = spanH <= 10 ? 1 : spanH <= 20 ? 2 : 3;
  const ticks = [];
  for (let h = Math.ceil(lo / 60); h <= Math.floor(hi / 60); h += step) ticks.push(h);
  return ticks.map((h) => {
    const left = (h * 60 - lo) / span * 100;
    const shift = left <= 1 ? '0' : left >= 99 ? '-100%' : '-50%';
    return `<span style="left:${left.toFixed(2)}%;transform:translateX(${shift})">${String(h).padStart(2, '0')}</span>`;
  }).join('');
}

function renderIdle(d) {
  $('#idle-after').textContent = Math.round(d.idle_after / 60);
  lastWork = d.work;
  if ($('#work-editor').hidden) $('#work-info').textContent =
    `${d.work.tz} · ${d.work.intervals.map(([a, b]) => `${a}\u2013${b}`).join(', ')}`;
  if (!($('#day-pick') && $('#days').dataset.day === d.day)) {   // don't rebuild (and close the picker) on every refresh
    $('#days').dataset.day = d.day;
    const today = d.days[0], yesterday = d.days[1];
    $('#days').innerHTML = `
      <button data-day="${today}" class="${d.day === today ? 'on' : ''}">TODAY</button>
      <button data-day="${yesterday}" class="${d.day === yesterday ? 'on' : ''}">YESTERDAY</button>
      <input type="date" id="day-pick" min="${d.oldest}" max="${today}" value="${d.day}" title="Pick a day">`;
  }
  for (const input of [$('#ex-start'), $('#ex-end')]) {
    input.min = d.oldest;
    input.max = d.days[0];
    if (!input.value) input.value = d.day;
  }
  const rows = d.rows.slice().sort((a, b) => b.idle - a.idle || String(a.name).localeCompare(String(b.name)));
  const axis = workAxis(d.work);
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
      <div class="tlcell">${timeline(r, d.start, d.ts, d.work)}</div>
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

$('#days').addEventListener('change', (e) => {
  if (e.target.id !== 'day-pick' || !e.target.value) return;
  idleDay = e.target.value === $('#days [data-day]').dataset.day ? null : e.target.value;   // today -> live
  pollIdle();
});

$('#export').addEventListener('click', (e) => {
  const b = e.target.closest('button[data-kind]');
  if (!b) return;
  const start = $('#ex-start').value, end = $('#ex-end').value || start;
  if (!start) return;
  location.href = `api/idle/export?kind=${b.dataset.kind}&start=${start}&end=${end}`;
});

// ---------- work-hours editor ----------
const intervalRow = (a = '09:00', b = '18:00') =>
  `<div class="we-row"><input type="time" value="${a}"> <span>–</span> <input type="time" value="${b}"> <button class="we-del" title="Remove">×</button></div>`;

function openWorkEditor() {
  const work = lastWork || { tz: 'Asia/Baku', intervals: [['09:00', '12:00'], ['14:00', '18:00']] };
  $('#work-editor').innerHTML = `
    <label class="we-tz">Timezone <input id="we-tz" value="${esc(work.tz)}" spellcheck="false"></label>
    <div id="we-rows">${work.intervals.map(([a, b]) => intervalRow(a, b)).join('')}</div>
    <button id="we-add" class="chip">+ interval</button>
    <div class="we-err" id="we-err"></div>
    <div class="we-actions"><button id="we-save" class="chip on">Save</button><button id="we-cancel" class="chip">Cancel</button></div>`;
  $('#work-editor').hidden = false;
  $('#work-info').hidden = true;
  $('#work-edit').hidden = true;
}

function closeWorkEditor() {
  $('#work-editor').hidden = true;
  $('#work-editor').innerHTML = '';
  $('#work-info').hidden = false;
  $('#work-edit').hidden = false;
}

async function saveWork() {
  const tz = $('#we-tz').value.trim();
  const intervals = [...document.querySelectorAll('#we-rows .we-row')]
    .map((row) => [...row.querySelectorAll('input[type=time]')].map((i) => i.value))
    .filter(([a, b]) => a && b);
  const err = $('#we-err');
  if (!intervals.length) { err.textContent = 'Add at least one interval.'; return; }
  try {
    const response = await fetch('api/settings/work', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tz, intervals }),
    });
    const data = await response.json();
    if (!data.ok) { err.textContent = data.error || 'Could not save.'; return; }
    closeWorkEditor();
    pollIdle();
  } catch { err.textContent = 'Could not reach the server.'; }
}

$('#work-edit').addEventListener('click', openWorkEditor);
$('#work-editor').addEventListener('click', (e) => {
  if (e.target.id === 'we-add') $('#we-rows').insertAdjacentHTML('beforeend', intervalRow());
  else if (e.target.classList.contains('we-del')) e.target.closest('.we-row').remove();
  else if (e.target.id === 'we-cancel') closeWorkEditor();
  else if (e.target.id === 'we-save') saveWork();
});

try {
  if (localStorage.getItem('hostwatch.tab') === 'idle') document.querySelector('#tabs [data-tab="idle"]').click();
} catch { /* storage unavailable */ }

poll();
