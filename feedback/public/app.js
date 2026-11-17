// The feedback hub's dashboard: every app's reports in one place. No framework
// and no build step: the page is small, and anything here can be changed with a
// text editor.
//
// Pages: /  every app at a glance · /<app>/feedback  its reports ·
// /<app>/feedback/<number>  one report · /agents  the guide for AI agents.
// On an app's own hostname the app is implied: /feedback/<number>.

const $ = (s, el = document) => el.querySelector(s);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const icon = (id, cls = '') => `<svg class="ico ${cls}"><use href="#i-${id}"/></svg>`;
const SUGGESTED = ['duplicate', 'planned', 'fixed', 'needs info', "won't fix", 'spam'];

const state = {
  view: 'home', app: '', hostApp: '', apps: [],
  status: 'open', kind: '', tag: '', user: '', logs: false, q: '', sort: 'new',
  items: [], total: 0, stats: null, number: null, report: null, cursor: 0,
  checked: new Set(), loading: true,
};

// ---- talking to the Worker ------------------------------------------------------------
async function api(path, opts = {}) {
  const r = await fetch(path, { ...opts, headers: { 'content-type': 'application/json', ...(opts.headers || {}) }, credentials: 'same-origin' });
  if (r.status === 401) { toast('Signed out. Reload to sign in again.'); throw new Error('401'); }
  const body = r.headers.get('content-type')?.includes('json') ? await r.json() : null;
  if (!r.ok) { toast(body?.message || `Something went wrong (${r.status})`); throw new Error(String(r.status)); }
  return body;
}

// ---- addresses ------------------------------------------------------------------------
function pathFor(view, app = state.app, number = null) {
  if (view === 'agents') return '/agents';
  if (view === 'home') return '/';
  const base = state.hostApp && app === state.hostApp ? '' : `/${app}`;
  return `${base}/feedback${number ? '/' + number : ''}`;
}
function filterQuery() {
  const p = new URLSearchParams();
  if (state.status !== 'open') p.set('status', state.status);
  if (state.sort !== 'new') p.set('sort', state.sort);
  for (const k of ['kind', 'tag', 'user', 'q']) if (state[k]) p.set(k, state[k]);
  if (state.logs) p.set('logs', '1');
  return p.toString() ? '?' + p : '';
}
function readLocation() {
  const seg = location.pathname.split('/').filter(Boolean);
  const p = new URLSearchParams(location.search);
  state.status = p.get('status') || 'open'; state.sort = p.get('sort') || 'new';
  state.kind = p.get('kind') || ''; state.tag = p.get('tag') || ''; state.user = p.get('user') || '';
  state.q = p.get('q') || ''; state.logs = p.get('logs') === '1';
  let app = state.hostApp, rest = seg;
  if (!state.hostApp && seg.length && seg[0] !== 'agents') { app = seg[0]; rest = seg.slice(1); }
  if (seg[0] === 'agents') { state.view = 'agents'; return; }
  if (!app) { state.view = 'home'; return; }
  state.view = 'feedback'; state.app = app;
  state.number = rest[0] === 'feedback' && /^\d+$/.test(rest[1] || '') ? +rest[1] : null;
}
function go(path, { replace = false } = {}) {
  if (path !== location.pathname + location.search) history[replace ? 'replaceState' : 'pushState'](null, '', path);
  route();
}
const syncUrl = () => { const u = pathFor('feedback', state.app, state.number) + filterQuery(); if (u !== location.pathname + location.search) history.replaceState(null, '', u); };

// ---- time: stored in UTC, shown in this browser's time zone ----------------------------
// date and time parts spelled out: dateStyle and timeStyle can't be combined with timeZoneName
const fmtLocal = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short' });
const when = t => fmtLocal.format(new Date(t));
const offsetMin = tz => { const m = /^([+-])(\d{2}):(\d{2})$/.exec(tz || ''); return m ? (m[1] === '-' ? -1 : 1) * (+m[2] * 60 + +m[3]) : null; };
/** The sender's own clock when they sent it, if it differs from yours. */
function theirTime(t, tz) {
  const off = offsetMin(tz);
  if (off == null || off === -new Date(t).getTimezoneOffset()) return '';
  const s = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit', timeZone: 'UTC' }).format(new Date(t + off * 60_000));
  return `${s} their time (UTC${tz})`;
}
const ago = t => {
  const s = (Date.now() - t) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 86400 * 30) return `${Math.floor(s / 86400)} d ago`;
  return new Date(t).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
};
const agoEl = t => `<time datetime="${new Date(t).toISOString()}" title="${esc(when(t))}">${ago(t)}</time>`;

// ---- apps -----------------------------------------------------------------------------
// apps get a steady color from their name, all at one OKLCH lightness
const appColor = a => `oklch(72% 0.14 ${Math.round(([...a].reduce((h, c) => (h * 31 + c.charCodeAt(0)) >>> 0, 7) * 137.508) % 360)})`;
const appName = a => a.split('-').map(w => w[0].toUpperCase() + w.slice(1)).join(' ');
const dot = a => `<span class="dot" style="background:${appColor(a)}"></span>`;
const kindChip = k => k === 'bug' ? `<span class="chip bug">${icon('bug')}Bug</span>` : `<span class="chip feature">${icon('spark')}Feature</span>`;
const stateIcon = s => `<svg class="ico state ${s}"><use href="#i-${s === 'open' ? 'dot-circle' : 'check-circle'}"/></svg>`;
const person = id => `<a class="mono who-link" href="${pathFor('feedback')}?status=all&user=${id}" data-nav title="Every report from this person">@${id}</a>`;

/** #482913 links to that report in this app; @ and 20 digits to that person's reports. */
function linkify(text, app) {
  return esc(text)
    .replace(/(^|[^\w&])#(\d{6,15})\b/g, (m, pre, n) => `${pre}<a href="${pathFor('feedback', app, n)}" data-nav>#${n}</a>`)
    .replace(/(^|[^\w])@(\d{20})\b/g, (m, pre, id) => `${pre}<a class="mono" href="${pathFor('feedback', app)}?status=all&user=${id}" data-nav>@${id}</a>`);
}

// ---- routing ----------------------------------------------------------------------------
let shownView = '';
async function route() {
  const prevApp = state.app, prevQuery = filterQuery();
  readLocation();
  for (const v of ['home', 'feedback', 'agents']) $(`#${v}-view`).hidden = state.view !== v;
  document.body.dataset.view = state.view;
  if (state.view !== 'feedback') { $('#detail').hidden = true; state.report = null; }
  renderSide();
  updateExport();
  if (state.view === 'home') { document.title = 'Feedback Hub'; renderHome(); }
  else if (state.view === 'agents') { document.title = 'Guide for AI agents · Feedback Hub'; renderAgents(); }
  else {
    document.title = `${appName(state.app)} feedback · Feedback Hub`;
    $('#q').value = state.q;
    if (shownView !== 'feedback' || prevApp !== state.app || prevQuery !== filterQuery()) { state.items = []; state.checked.clear(); await load(); }
    else renderList();
    if (state.number) openReport(state.number); else closeDetail(false);
  }
  shownView = state.view;
}

// ---- home ---------------------------------------------------------------------------------
async function renderHome() {
  const el = $('#home-view');
  if (!el.dataset.ready) el.innerHTML = `<div class="view-head"><h1>Your apps</h1><p class="lede">Every app the hub serves, at a glance.</p></div><div class="apps">${Array.from({ length: 2 }, () => '<div class="app-card"><div class="sk" style="height:18px;width:40%"></div><div class="sk" style="height:60px;margin-top:16px"></div><div class="sk" style="height:90px;margin-top:16px"></div></div>').join('')}</div>`;
  let s;
  try { s = await api('/api/summary'); } catch { return; }
  const total = s.apps.reduce((a, x) => a + x.open, 0);
  el.dataset.ready = '1';
  el.innerHTML = `
    <div class="view-head">
      <h1>Your apps</h1>
      <p class="lede">${total} open ${total === 1 ? 'report' : 'reports'} across ${s.apps.length} ${s.apps.length === 1 ? 'app' : 'apps'}, from ${s.people} ${s.people === 1 ? 'person' : 'people'}.</p>
    </div>
    <div class="apps">
      ${s.apps.map(a => `
        <article class="app-card">
          <a class="app-top" href="${pathFor('feedback', a.app)}" data-nav>
            <span class="app-badge" style="--c:${appColor(a.app)}">${icon('app')}</span>
            <span class="app-title"><b>${esc(appName(a.app))}</b><span>${a.last ? 'Last report ' + ago(a.last) : 'No reports yet'}</span></span>
            ${icon('arrow', 'go')}
          </a>
          <div class="app-stats">
            <a href="${pathFor('feedback', a.app)}" data-nav><b>${a.open}</b><span>Open</span></a>
            <a href="${pathFor('feedback', a.app)}?kind=bug" data-nav><b>${a.bugs}</b><span>Bugs</span></a>
            <a href="${pathFor('feedback', a.app)}?kind=feature" data-nav><b>${a.features}</b><span>Features</span></a>
            <a href="${pathFor('feedback', a.app)}?status=all" data-nav><b>${a.week}</b><span>This week</span></a>
          </div>
          <div class="recent">
            ${a.recent.length ? a.recent.map(r => `
              <a class="recent-row" href="${pathFor('feedback', a.app, r.number)}" data-nav>
                ${stateIcon(r.status)}<span class="num">#${r.number}</span><span class="subject">${esc(r.subject)}</span>${agoEl(r.created_at)}
              </a>`).join('') : '<div class="quiet">Reports from Settings -> Feedback show up here.</div>'}
          </div>
        </article>`).join('')}
      <a class="app-card guide" href="/agents" data-nav>
        <span class="app-badge">${icon('book')}</span>
        <span class="app-title"><b>Guide for AI agents</b><span>How the apps are laid out, built and shipped. Point a new session here.</span></span>
        ${icon('arrow', 'go')}
      </a>
    </div>`;
}

// ---- agents guide: a small markdown renderer for one known document ------------------
async function renderAgents() {
  const el = $('#agents-view');
  if (el.dataset.ready) return;
  el.innerHTML = '<div class="sk" style="height:28px;width:40%"></div><div class="sk" style="height:200px;margin-top:20px"></div>';
  let md;
  try { const r = await fetch('/agents.md', { credentials: 'same-origin' }); md = await r.text(); } catch { return; }
  el.dataset.ready = '1';
  el.innerHTML = `<article class="md">${markdown(md)}</article><p class="quiet raw">Raw for agents: <a href="/agents.md">/agents.md</a></p>`;
}
function inline(s) {
  return esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (m, t, u) => /^(https?:\/\/|\/)/.test(u) ? `<a href="${u}">${t}</a>` : t);
}
function markdown(md) {
  const out = [], lines = md.replace(/\r/g, '').split('\n');
  for (let i = 0; i < lines.length;) {
    const l = lines[i];
    if (/^```/.test(l)) { const code = []; i++; while (i < lines.length && !/^```/.test(lines[i])) code.push(lines[i++]); i++; out.push(`<pre><code>${esc(code.join('\n'))}</code></pre>`); continue; }
    const h = /^(#{1,3}) (.*)$/.exec(l);
    if (h) { out.push(`<h${h[1].length}>${inline(h[2])}</h${h[1].length}>`); i++; continue; }
    if (/^\|/.test(l)) {
      const rows = []; while (i < lines.length && /^\|/.test(lines[i])) rows.push(lines[i++]);
      const cells = r => r.replace(/^\||\|$/g, '').split('|').map(c => c.trim());
      const body = rows.filter(r => !/^\|[\s|:-]+\|$/.test(r));
      out.push(`<table>${body.map(r => `<tr>${cells(r).map(c => `<td>${inline(c)}</td>`).join('')}</tr>`).join('')}</table>`);
      continue;
    }
    if (/^(\d+\.|-) /.test(l)) {
      const ordered = /^\d+\./.test(l), items = [];
      while (i < lines.length && (/^(\d+\.|-) /.test(lines[i]) || /^ {2,}\S/.test(lines[i]))) {
        if (/^(\d+\.|-) /.test(lines[i])) items.push(lines[i].replace(/^(\d+\.|-) /, '')); else items[items.length - 1] += ' ' + lines[i].trim();
        i++;
      }
      out.push(`<${ordered ? 'ol' : 'ul'}>${items.map(t => `<li>${inline(t)}</li>`).join('')}</${ordered ? 'ol' : 'ul'}>`);
      continue;
    }
    if (!l.trim()) { i++; continue; }
    const para = []; while (i < lines.length && lines[i].trim() && !/^(#|```|\||(\d+\.|-) )/.test(lines[i])) para.push(lines[i++].trim());
    out.push(`<p>${inline(para.join(' '))}</p>`);
  }
  return out.join('\n');
}

// ---- an app's feedback ----------------------------------------------------------------
function query() {
  const p = new URLSearchParams({ app: state.app, status: state.status, sort: state.sort });
  for (const k of ['kind', 'tag', 'user', 'q']) if (state[k]) p.set(k, state[k]);
  if (state.logs) p.set('logs', '1');
  return p;
}

async function load({ keepCursor = false } = {}) {
  if (state.view !== 'feedback') return;
  const myTurn = ++load.seq;
  state.loading = !state.items.length;
  if (state.loading) { renderStats(); renderList(); }
  try {
    const [list, stats] = await Promise.all([api('/api/reports?' + query()), api('/api/stats?app=' + encodeURIComponent(state.app))]);
    if (myTurn !== load.seq) return;   // a newer search already answered
    state.items = list.items; state.total = list.total; state.stats = stats; state.loading = false;
    if (!keepCursor) state.cursor = 0;
    state.cursor = Math.min(state.cursor, Math.max(0, state.items.length - 1));
    for (const n of [...state.checked]) if (!state.items.some(i => i.number === n)) state.checked.delete(n);
    renderSide(); renderStats(); renderList();
  } catch { state.loading = false; renderList(); }
}
load.seq = 0;

async function openReport(number) {
  state.number = number;
  syncUrl();
  renderList();
  const panel = $('#detail');
  panel.hidden = false;
  if (state.report?.number !== number) panel.innerHTML = skeletonDetail();
  try {
    state.report = await api(`/api/reports/${state.app}/${number}`);
    renderDetail();
    document.title = `#${number} ${state.report.subject} · ${appName(state.app)}`;
  } catch { panel.hidden = true; state.number = null; syncUrl(); }
}

function closeDetail(focus = true) {
  state.number = null; state.report = null;
  $('#detail').hidden = true;
  syncUrl(); renderList();
  if (focus) $('#list').focus();
}

async function patch(number, data, done) {
  const r = await api(`/api/reports/${state.app}/${number}`, { method: 'PATCH', body: JSON.stringify(data) });
  if (state.number === number) { state.report = r; renderDetail(); }
  if (done) toast(done);
  load({ keepCursor: true });
}
const setStatus = (n, status) => patch(n, { status }, status === 'closed' ? `Closed #${n}` : `Reopened #${n}`);

// ---- drawing ------------------------------------------------------------------------------
function renderSide() {
  const s = state.view === 'feedback' ? state.stats || {} : {};
  const nav = (label, ico, n, active, href) => `<a class="nav ${active ? 'on' : ''}" href="${href}" data-nav>${ico}<span>${label}</span><span class="n">${n ?? ''}</span></a>`;
  const fb = (set = {}) => { const keep = { ...state }; Object.assign(state, { status: 'open', kind: '', tag: '', user: '', logs: false, q: '' }, set); const u = pathFor('feedback') + filterQuery(); Object.assign(state, keep); return u; };
  const apps = state.hostApp ? [state.hostApp] : state.apps;
  let html = '';
  if (!state.hostApp) html += `<h3>Hub</h3>${nav('Home', icon('home'), '', state.view === 'home', '/')}`;
  html += `<h3>Apps</h3>${apps.map(a => nav(esc(appName(a)), dot(a), state.app === a && state.view === 'feedback' ? s.open : '', state.view === 'feedback' && state.app === a, pathFor('feedback', a))).join('')}`;
  if (state.view === 'feedback') {
    const plain = !state.kind && !state.tag && !state.user && !state.logs;
    html += `<h3>Reports</h3>
      ${nav('Inbox', icon('inbox'), s.open, plain && state.status === 'open', fb())}
      ${nav('Closed', icon('check-circle'), s.closed, plain && state.status === 'closed', fb({ status: 'closed' }))}
      ${nav('Everything', icon('layers'), s.total, plain && state.status === 'all', fb({ status: 'all' }))}
      <h3>Type</h3>
      ${nav('Bugs', icon('bug'), s.bugs, state.kind === 'bug', fb({ status: state.status, kind: state.kind === 'bug' ? '' : 'bug' }))}
      ${nav('Feature requests', icon('spark'), s.features, state.kind === 'feature', fb({ status: state.status, kind: state.kind === 'feature' ? '' : 'feature' }))}
      ${nav('With logs', icon('file'), '', state.logs, fb({ status: state.status, logs: !state.logs }))}
      ${(s.tags || []).length ? '<h3>Tags</h3>' + s.tags.map(t => nav(esc(t.tag), icon('tag'), t.n, state.tag === t.tag, fb({ status: 'all', tag: state.tag === t.tag ? '' : t.tag }))).join('') : ''}`;
  }
  html += `<h3>Help</h3>${nav('Guide for AI agents', icon('book'), '', state.view === 'agents', '/agents')}`;
  $('#side').innerHTML = html;
}

function updateExport() {
  const a = state.view === 'feedback' ? '&app=' + encodeURIComponent(state.app) : '';
  $('#export-csv').href = '/api/export?format=csv' + a;
  $('#export-json').href = '/api/export?format=json' + a;
}

function renderStats() {
  const s = state.stats;
  $('#view-head').innerHTML = `<h1>${dot(state.app)}${esc(appName(state.app))}</h1>`;
  const card = (label, ico, value, sub, href, lead = false) =>
    `<a class="stat ${lead ? 'lead' : ''}" href="${href}" data-nav><div class="label">${ico}${label}</div><div class="value">${value}</div><div class="sub">${sub}</div></a>`;
  if (!s || state.loading) { $('#stats').innerHTML = Array.from({ length: 5 }, () => `<div class="stat"><div class="sk" style="height:12px;width:50%"></div><div class="sk" style="height:26px;width:40%;margin-top:10px"></div></div>`).join(''); return; }
  const f = pathFor('feedback');
  $('#stats').innerHTML =
    card('Open', icon('dot-circle'), s.open, `${s.closed} closed`, f, true) +
    card('Bugs', icon('bug'), s.bugs, 'open', f + '?kind=bug') +
    card('Features', icon('spark'), s.features, 'open', f + '?kind=feature') +
    card('This week', icon('clock'), s.week, 'new reports', f + '?status=all') +
    card('People', icon('user'), s.users, `${s.total} reports in all`, f + '?status=all&sort=user');
}

function renderToolbar() {
  const seg = $('#status-seg');
  seg.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.status === state.status));
  const on = seg.querySelector('button.on'), thumb = seg.querySelector('.seg-thumb');
  if (on) { thumb.style.left = on.offsetLeft + 'px'; thumb.style.width = on.offsetWidth + 'px'; }
  $('#sort').value = state.sort;
  const f = [];
  if (state.kind) f.push([state.kind === 'bug' ? 'Bugs' : 'Feature requests', { kind: '' }]);
  if (state.tag) f.push([`Tag: ${state.tag}`, { tag: '' }]);
  if (state.user) f.push([`@${state.user}`, { user: '' }]);
  if (state.logs) f.push(['With logs', { logs: false }]);
  if (state.q) f.push([`“${state.q}”`, { q: '' }]);
  $('#active-filters').innerHTML = f.map(([l, set]) => `<span class="chip removable" data-set='${esc(JSON.stringify(set))}'>${esc(l)}${icon('close')}</span>`).join('');
  const n = state.checked.size;
  $('#bulk').hidden = n === 0;
  $('#bulk-count').textContent = `${n} selected`;
  const all = $('#select-all');
  all.checked = n > 0 && n === state.items.length; all.indeterminate = n > 0 && n < state.items.length;
  $('#count').textContent = state.loading ? '' : `${state.total} ${state.total === 1 ? 'report' : 'reports'}`;
}

function renderList() {
  if (state.view !== 'feedback') return;
  renderToolbar();
  const list = $('#list');
  if (state.loading) {
    list.innerHTML = Array.from({ length: 7 }, (_, i) => `<div class="row"><div class="sk" style="width:18px;height:18px"></div><div class="sk" style="width:20px;height:20px;border-radius:50%"></div><div><div class="sk" style="height:13px;width:${45 + (i * 13) % 35}%"></div><div class="sk" style="height:11px;width:70%;margin-top:8px"></div></div><div class="sk" style="width:90px;height:12px"></div></div>`).join('');
    return;
  }
  if (!state.items.length) {
    const filtered = state.kind || state.tag || state.user || state.logs || state.q;
    list.innerHTML = `<div class="empty">${icon(filtered ? 'search' : 'inbox')}<h4>${filtered ? 'Nothing matches' : state.status === 'open' ? 'Inbox zero' : 'No reports yet'}</h4><div>${filtered ? 'Try fewer filters.' : state.status === 'open' ? 'Every report is closed.' : 'Reports from Settings -> Feedback show up here.'}</div></div>`;
    edges(); return;
  }
  list.innerHTML = state.items.map((r, i) => `
    <div class="row ${state.number === r.number ? 'sel' : ''} ${i === state.cursor ? 'cursor' : ''}" role="option" aria-selected="${state.number === r.number}" data-n="${r.number}" data-i="${i}">
      <label class="check" data-stop><input type="checkbox" ${state.checked.has(r.number) ? 'checked' : ''} data-check="${r.number}" aria-label="Select #${r.number}"><span></span></label>
      ${stateIcon(r.status)}
      <div class="body">
        <div class="title"><span class="num">#${r.number}</span><span class="subject">${esc(r.subject)}</span></div>
        <div class="preview">${esc(r.preview)}</div>
        <div class="chips">${kindChip(r.kind)}${r.tags.map(t => `<span class="chip tag">${esc(t)}</span>`).join('')}</div>
      </div>
      <div class="meta">
        <div class="line">${r.has_logs ? `<span title="Logs attached">${icon('file')}</span>` : ''}${r.notes ? `<span title="${r.notes} note${r.notes > 1 ? 's' : ''}">${icon('note')}</span>` : ''}${agoEl(r.created_at)}</div>
        <div class="line"><span class="mono">@${esc(r.user_id)}</span></div>
        <div class="line">${r.version ? 'v' + esc(r.version) : ''}${r.source ? ' · ' + (r.source === 'store' ? 'Store' : 'GitHub') : ''}</div>
      </div>
    </div>`).join('');
  edges();
}

// the list's top and bottom fade only where there's more to scroll
function edges() {
  const l = $('#list');
  l.classList.toggle('at-top', l.scrollTop < 2);
  l.classList.toggle('at-bottom', l.scrollTop + l.clientHeight >= l.scrollHeight - 2);
}

function colorLog(line) {
  const e = esc(line);
  return / (ERR|ERROR|CRASH|FTL)\b/.test(line) ? `<span class="e">${e}</span>` : / WRN\b/.test(line) ? `<span class="w">${e}</span>` : `<span>${e}</span>`;
}

function renderDetail() {
  const r = state.report, panel = $('#detail');
  if (!r) return;
  const closed = r.status === 'closed';
  const lines = (r.logs || '').split('\n');
  const theirs = theirTime(r.created_at, r.tz);
  panel.innerHTML = `
    <div class="d-head" tabindex="-1">
      <div class="d-top">
        <span class="badge ${r.status}">${icon(closed ? 'check-circle' : 'dot-circle')}${closed ? 'Closed' : 'Open'}</span>
        ${kindChip(r.kind)}
        <span class="num">#${r.number}</span>
        <button class="btn ghost copy" data-copy-link title="Copy link">${icon('link', 'a')}${icon('check', 'b')}</button>
        <button class="btn ghost x" data-close title="Close panel (Esc)">${icon('close')}</button>
      </div>
      <h2>${esc(r.subject)}</h2>
      <div class="d-actions">
        <button class="btn ${closed ? 'open-it' : 'close-it'}" data-toggle>${icon(closed ? 'dot-circle' : 'check-circle')}${closed ? 'Reopen' : 'Close report'}</button>
        <a class="btn" href="${pathFor('feedback')}?status=all&user=${r.user_id}" data-nav>${icon('user')}${r.user_reports} from this person</a>
      </div>
    </div>
    <div class="d-body">
      <div class="card"><div class="text">${linkify(r.body, r.app)}</div></div>
      <div class="card">
        <h4>${icon('window')}Details</h4>
        <dl class="grid">
          <dt>From</dt><dd>${person(r.user_id)}${r.user_reports_all > r.user_reports ? `<span class="quiet"> · ${r.user_reports_all} reports in all apps</span>` : ''}</dd>
          <dt>Version</dt><dd>${r.version ? 'v' + esc(r.version) : 'Unknown'} · ${r.source === 'store' ? 'Microsoft Store' : r.source === 'github' ? 'GitHub' : 'Unknown'}</dd>
          <dt>Windows</dt><dd>${esc(r.windows || 'Unknown')}</dd>
          <dt>Sent</dt><dd><time datetime="${new Date(r.created_at).toISOString()}">${esc(when(r.created_at))}</time>${theirs ? `<div class="quiet">${esc(theirs)}</div>` : ''}</dd>
          ${r.closed_at ? `<dt>Closed</dt><dd><time datetime="${new Date(r.closed_at).toISOString()}">${esc(when(r.closed_at))}</time></dd>` : ''}
        </dl>
      </div>
      <div class="card">
        <h4>${icon('tag')}Tags</h4>
        <div class="tags">
          ${r.tags.map(t => `<span class="chip tag">${esc(t)}<button data-untag="${esc(t)}" aria-label="Remove ${esc(t)}">${icon('close')}</button></span>`).join('')}
          <input class="tag-input" id="tag-input" placeholder="Add a tag" maxlength="24" autocomplete="off">
        </div>
        <div class="suggest">${SUGGESTED.filter(t => !r.tags.includes(t)).map(t => `<button data-addtag="${esc(t)}">${icon('plus')} ${esc(t)}</button>`).join('')}</div>
      </div>
      ${r.logs ? `
      <div class="card logs">
        <h4>${icon('file')}Logs <span class="right">${lines.length.toLocaleString()} lines</span></h4>
        <div class="logs-tools">
          <input id="log-filter" placeholder="Filter lines" autocomplete="off" spellcheck="false">
          <button class="btn copy" data-copy-logs title="Copy logs">${icon('copy', 'a')}${icon('check', 'b')}</button>
        </div>
        <pre id="log-view">${lines.map(colorLog).join('')}</pre>
      </div>` : ''}
      <div class="card">
        <h4>${icon('note')}Private notes</h4>
        <div class="notes">${r.notes.map(n => `<div class="note"><div class="by">${esc(n.author)} · <time datetime="${new Date(n.created_at).toISOString()}" title="${esc(when(n.created_at))}">${ago(n.created_at)}</time></div><div class="text">${linkify(n.body, r.app)}</div></div>`).join('')}</div>
        <form class="note-form" id="note-form">
          <textarea id="note-text" placeholder="Only you can see notes. #number and @person become links." maxlength="10000"></textarea>
          <div class="row-end"><button class="btn primary" type="submit">Add note</button></div>
        </form>
      </div>
      <div class="card">
        <h4>${icon('clock')}History</h4>
        <ol class="timeline">${r.events.map(e => `<li class="${esc(e.what.split(' ')[0])}">${esc(e.what)} by <span class="mono">${/^\d{20}$/.test(e.actor) ? '@' + esc(e.actor) : esc(e.actor)}</span><time datetime="${new Date(e.at).toISOString()}" title="${esc(when(e.at))}">${ago(e.at)}</time></li>`).join('')}</ol>
      </div>
    </div>`;
}

function skeletonDetail() {
  return `<div class="d-head"><div class="sk" style="height:24px;width:120px"></div><div class="sk" style="height:22px;width:80%;margin-top:14px"></div></div>
    <div class="d-body">${[90, 120, 70].map(h => `<div class="card"><div class="sk" style="height:${h}px"></div></div>`).join('')}</div>`;
}

let toastTimer;
function toast(text) {
  const t = $('#toast');
  t.textContent = text; t.classList.add('show');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 2200);
}
function copied(btn) { btn.classList.add('done'); setTimeout(() => btn.classList.remove('done'), 1400); }

// ---- input ----------------------------------------------------------------------------------
function apply(set) {
  Object.assign(state, set);
  state.checked.clear();
  if ('q' in set) $('#q').value = state.q;
  go(pathFor('feedback', state.app, state.number) + filterQuery(), { replace: 'q' in set });
}

document.addEventListener('click', e => {
  const a = e.target.closest('a[data-nav]');
  if (a && !e.ctrlKey && !e.metaKey && !e.shiftKey && e.button === 0) { e.preventDefault(); go(a.getAttribute('href')); return; }
  const t = e.target.closest('[data-set]');
  if (t) { apply(JSON.parse(t.dataset.set)); return; }
  const seg = e.target.closest('#status-seg button');
  if (seg) { apply({ status: seg.dataset.status }); return; }
  const check = e.target.closest('[data-check]');
  if (check) { const n = +check.dataset.check; check.checked ? state.checked.add(n) : state.checked.delete(n); renderToolbar(); return; }
  if (e.target.closest('[data-stop]')) return;
  const row = e.target.closest('.row[data-n]');
  if (row) { state.cursor = +row.dataset.i; go(pathFor('feedback', state.app, +row.dataset.n) + filterQuery()); return; }
  const r = state.report;
  if (e.target.closest('[data-close]')) { closeDetail(); return; }
  if (r && e.target.closest('[data-toggle]')) { setStatus(r.number, r.status === 'open' ? 'closed' : 'open'); return; }
  const untag = e.target.closest('[data-untag]');
  if (r && untag) { patch(r.number, { removeTags: [untag.dataset.untag] }); return; }
  const add = e.target.closest('[data-addtag]');
  if (r && add) { patch(r.number, { addTags: [add.dataset.addtag] }, `Tagged ${add.dataset.addtag}`); return; }
  const cl = e.target.closest('[data-copy-link]');
  if (r && cl) { navigator.clipboard.writeText(location.origin + pathFor('feedback', r.app, r.number)); copied(cl); return; }
  const cg = e.target.closest('[data-copy-logs]');
  if (r && cg) { navigator.clipboard.writeText(r.logs || ''); copied(cg); return; }
  const bulk = e.target.closest('[data-bulk]');
  if (bulk) {
    const items = [...state.checked].map(number => ({ app: state.app, number }));
    api('/api/bulk', { method: 'POST', body: JSON.stringify({ items, status: bulk.dataset.bulk }) }).then(() => {
      toast(`${bulk.dataset.bulk === 'closed' ? 'Closed' : 'Reopened'} ${items.length} ${items.length === 1 ? 'report' : 'reports'}`);
      state.checked.clear(); load({ keepCursor: true });
      if (state.number && items.some(i => i.number === state.number)) openReport(state.number);
    });
    return;
  }
  const exp = e.target.closest('#export-btn'), menu = $('#export-menu');
  if (exp) { menu.hidden = !menu.hidden; exp.setAttribute('aria-expanded', String(!menu.hidden)); return; }
  if (!e.target.closest('#export-menu')) menu.hidden = true;
  if (e.target.closest('#refresh')) { if (state.view === 'home') renderHome(); else { load({ keepCursor: true }); if (state.number) openReport(state.number); } }
});

$('#select-all').addEventListener('change', e => {
  state.checked = e.target.checked ? new Set(state.items.map(i => i.number)) : new Set();
  renderList();
});
$('#sort').addEventListener('change', e => apply({ sort: e.target.value }));
let qTimer;
$('#q').addEventListener('input', e => {
  clearTimeout(qTimer);
  qTimer = setTimeout(() => {
    const q = e.target.value.trim();
    // searching from the home page or the guide searches the app you came from, or the first app
    if (state.view !== 'feedback') { state.app = state.app || state.hostApp || state.apps[0]; state.view = 'feedback'; }
    apply({ q });
  }, 180);
});
$('#list').addEventListener('scroll', edges, { passive: true });
window.addEventListener('resize', () => { if (state.view === 'feedback') { renderToolbar(); edges(); } });
window.addEventListener('popstate', route);

document.addEventListener('submit', e => {
  if (e.target.id !== 'note-form') return;
  e.preventDefault();
  const text = $('#note-text').value.trim(), r = state.report;
  if (!text || !r) return;
  api(`/api/reports/${r.app}/${r.number}/notes`, { method: 'POST', body: JSON.stringify({ body: text }) }).then(d => { state.report = d; renderDetail(); toast('Note added'); load({ keepCursor: true }); });
});

document.addEventListener('keydown', e => {
  const r = state.report;
  if (e.target.id === 'tag-input' && e.key === 'Enter') {
    const tag = e.target.value.trim().toLowerCase();
    if (tag && r) patch(r.number, { addTags: [tag] }, `Tagged ${tag}`);
    return;
  }
  if (e.target.id === 'log-filter') return;
  if (e.target.id === 'note-text' && e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { $('#note-form').requestSubmit(); return; }
  if (e.target.matches('input, textarea, select')) { if (e.key === 'Escape') e.target.blur(); return; }
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === '/') { e.preventDefault(); $('#q').focus(); $('#q').select(); return; }
  if (e.key === '?') { $('#help').showModal(); return; }
  if (state.view !== 'feedback') return;
  const move = d => {
    if (!state.items.length) return;
    state.cursor = Math.max(0, Math.min(state.items.length - 1, state.cursor + d));
    // with a report open, moving opens the next one: reading through the inbox in a row
    if (state.number) go(pathFor('feedback', state.app, state.items[state.cursor].number) + filterQuery(), { replace: true }); else renderList();
    $(`.row[data-i="${state.cursor}"]`)?.scrollIntoView({ block: 'nearest' });
  };
  switch (e.key) {
    case 'j': case 'ArrowDown': e.preventDefault(); move(1); break;
    case 'k': case 'ArrowUp': e.preventDefault(); move(-1); break;
    case 'Enter': if (state.items[state.cursor]) go(pathFor('feedback', state.app, state.items[state.cursor].number) + filterQuery()); break;
    case 'x': { const n = state.number ?? state.items[state.cursor]?.number; const it = r?.number === n ? r : state.items.find(i => i.number === n); if (it) setStatus(n, it.status === 'open' ? 'closed' : 'open'); break; }
    case ' ': { e.preventDefault(); const n = state.items[state.cursor]?.number; if (n) { state.checked.has(n) ? state.checked.delete(n) : state.checked.add(n); renderList(); } break; }
    case 't': if (r) { e.preventDefault(); $('#tag-input')?.focus(); } break;
    case 'n': if (r) { e.preventDefault(); $('#note-text')?.focus(); } break;
    case '1': apply({ status: 'open' }); break;
    case '2': apply({ status: 'closed' }); break;
    case '3': apply({ status: 'all' }); break;
    case 'r': load({ keepCursor: true }); break;
    case 'Escape': if (state.number) closeDetail(); break;
  }
});

document.addEventListener('input', e => {
  if (e.target.id !== 'log-filter') return;
  const f = e.target.value.toLowerCase();
  $('#log-view').innerHTML = (state.report?.logs || '').split('\n').filter(l => !f || l.toLowerCase().includes(f)).map(colorLog).join('');
});

// ---- start ----------------------------------------------------------------------------------
(async () => {
  try {
    const me = await api('/api/me');
    state.hostApp = me.hostApp || ''; state.apps = me.apps || [];
    $('#who').textContent = me.who;
  } catch { /* signed out: the toast says so */ }
  route();
  // new reports appear without a reload; paused while the tab is hidden
  setInterval(() => { if (document.hidden) return; if (state.view === 'feedback') load({ keepCursor: true }); else if (state.view === 'home') renderHome(); }, 60_000);
})();
