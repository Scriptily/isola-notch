// The feedback hub's dashboard: every app's reports in one place. No framework and no build step: the page is small,
// and anything here can be changed with a text editor.

const $ = (s, el = document) => el.querySelector(s);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const icon = (id, cls = '') => `<svg class="ico ${cls}"><use href="#i-${id}"/></svg>`;
const SUGGESTED = ['duplicate', 'planned', 'fixed', 'needs info', "won't fix", 'spam'];

const state = {
  app: '', status: 'open', kind: '', tag: '', user: '', logs: false, q: '', sort: 'new',
  items: [], total: 0, stats: null, selected: null, report: null, cursor: 0,
  checked: new Set(), loading: true,
};

// ---- talking to the Worker ------------------------------------------------------
async function api(path, opts = {}) {
  const r = await fetch(path, { ...opts, headers: { 'content-type': 'application/json', ...(opts.headers || {}) }, credentials: 'same-origin' });
  if (r.status === 401) { toast('Signed out. Reload to sign in again.'); throw new Error('401'); }
  const body = r.headers.get('content-type')?.includes('json') ? await r.json() : null;
  if (!r.ok) { toast(body?.message || `Something went wrong (${r.status})`); throw new Error(String(r.status)); }
  return body;
}

function query() {
  const p = new URLSearchParams({ status: state.status, sort: state.sort });
  if (state.app) p.set('app', state.app);
  if (state.kind) p.set('kind', state.kind);
  if (state.tag) p.set('tag', state.tag);
  if (state.user) p.set('user', state.user);
  if (state.logs) p.set('logs', '1');
  if (state.q) p.set('q', state.q);
  return p;
}

async function load({ keepCursor = false } = {}) {
  const myTurn = ++load.seq;
  state.loading = !state.items.length;
  if (state.loading) renderList();
  try {
    const [list, stats] = await Promise.all([api('/api/reports?' + query()), api('/api/stats' + (state.app ? '?app=' + encodeURIComponent(state.app) : ''))]);
    if (myTurn !== load.seq) return;   // a newer search already answered
    state.items = list.items; state.total = list.total; state.stats = stats; state.loading = false;
    if (!keepCursor) state.cursor = 0;
    state.cursor = Math.min(state.cursor, Math.max(0, state.items.length - 1));
    for (const id of [...state.checked]) if (!state.items.some(i => i.id === id)) state.checked.delete(id);
    render();
  } catch { state.loading = false; renderList(); }
}
load.seq = 0;

async function open(id, { focus = false } = {}) {
  state.selected = id;
  syncHash();
  renderList();
  const panel = $('#detail');
  panel.hidden = false;
  if (state.report?.id !== id) panel.innerHTML = skeletonDetail();
  try {
    state.report = await api(`/api/reports/${id}`);
    renderDetail();
    if (focus) $('#detail .d-head')?.focus();
  } catch { panel.hidden = true; state.selected = null; syncHash(); }
}

function closeDetail() {
  state.selected = null; state.report = null;
  $('#detail').hidden = true;
  syncHash(); renderList(); $('#list').focus();
}

async function patch(id, data, done) {
  const r = await api(`/api/reports/${id}`, { method: 'PATCH', body: JSON.stringify(data) });
  if (state.selected === id) { state.report = r; renderDetail(); }
  if (done) toast(done);
  load({ keepCursor: true });
}

async function setStatus(id, status) {
  await patch(id, { status }, status === 'closed' ? `Closed #${id}` : `Reopened #${id}`);
}

// ---- the URL holds the view, so a report or filter can be bookmarked ------------
function syncHash() {
  const p = new URLSearchParams();
  for (const k of ['app', 'status', 'kind', 'tag', 'user', 'q', 'sort']) if (state[k] && !(k === 'status' && state[k] === 'open') && !(k === 'sort' && state[k] === 'new')) p.set(k, state[k]);
  if (state.logs) p.set('logs', '1');
  const h = (state.selected ? `#/report/${state.selected}` : '#/') + (p.toString() ? '?' + p : '');
  if (location.hash !== h) history.replaceState(null, '', h);
}
function readHash() {
  const [path, qs] = location.hash.slice(1).split('?');
  const p = new URLSearchParams(qs || '');
  state.app = p.get('app') || '';
  state.status = p.get('status') || 'open';
  state.kind = p.get('kind') || ''; state.tag = p.get('tag') || ''; state.user = p.get('user') || '';
  state.q = p.get('q') || ''; state.sort = p.get('sort') || 'new'; state.logs = p.get('logs') === '1';
  const m = /^\/report\/(\d+)$/.exec(path || '');
  return m ? +m[1] : null;
}

// ---- drawing ------------------------------------------------------------------------
function render() { renderSide(); renderStats(); renderToolbar(); renderList(); }

function renderSide() {
  const s = state.stats || {};
  const nav = (label, ico, n, active, set) => `<button class="nav ${active ? 'on' : ''}" data-set='${esc(JSON.stringify(set))}'>${ico}<span>${label}</span><span class="n">${n ?? ''}</span></button>`;
  const views = !state.kind && !state.tag && !state.user && !state.logs;
  $('#side').innerHTML = `
    <h3>Reports</h3>
    ${nav('Inbox', icon('inbox'), s.open, views && state.status === 'open', { status: 'open', kind: '', tag: '', user: '', logs: false })}
    ${nav('Closed', icon('check-circle'), s.closed, views && state.status === 'closed', { status: 'closed', kind: '', tag: '', user: '', logs: false })}
    ${nav('Everything', icon('layers'), s.total, views && state.status === 'all', { status: 'all', kind: '', tag: '', user: '', logs: false })}
    <h3>Type</h3>
    ${nav('Bugs', icon('bug'), s.bugs, state.kind === 'bug', { kind: state.kind === 'bug' ? '' : 'bug' })}
    ${nav('Feature requests', icon('spark'), s.features, state.kind === 'feature', { kind: state.kind === 'feature' ? '' : 'feature' })}
    ${nav('With logs', icon('file'), '', state.logs, { logs: !state.logs })}
    ${multiApp() ? '<h3>Apps</h3>' + nav('All apps', icon('layers'), '', !state.app, { app: '' }) + s.apps.map(a => nav(appName(a.app), '<span class="dot" style="background:' + appColor(a.app) + '"></span>', a.open, state.app === a.app, { app: state.app === a.app ? '' : a.app })).join('') : ''}
    ${(s.tags || []).length ? '<h3>Tags</h3>' + s.tags.map(t => nav(esc(t.tag), icon('tag'), t.n, state.tag === t.tag, { tag: state.tag === t.tag ? '' : t.tag })).join('') : ''}`;
}

function renderStats() {
  const s = state.stats;
  const card = (label, ico, value, sub, set, lead = false) =>
    `<button class="stat ${lead ? 'lead' : ''}" data-set='${esc(JSON.stringify(set))}'><div class="label">${ico}${label}</div><div class="value">${value}</div><div class="sub">${sub}</div></button>`;
  if (!s) { $('#stats').innerHTML = Array.from({ length: 5 }, () => `<div class="stat"><div class="sk" style="height:12px;width:50%"></div><div class="sk" style="height:26px;width:40%;margin-top:10px"></div></div>`).join(''); return; }
  $('#stats').innerHTML =
    card('Open', icon('dot-circle'), s.open, `${s.closed} closed`, { status: 'open', kind: '' }, true) +
    card('Bugs', icon('bug'), s.bugs, 'open', { status: 'open', kind: 'bug' }) +
    card('Features', icon('spark'), s.features, 'open', { status: 'open', kind: 'feature' }) +
    card('This week', icon('clock'), s.week, 'new reports', { status: 'all', kind: '', sort: 'new' }) +
    card('People', icon('user'), s.users, `${s.total} reports in all`, { status: 'all' });
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
  if (state.user) f.push([state.user, { user: '' }]);
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

const ago = t => {
  const s = (Date.now() - t) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 86400 * 30) return `${Math.floor(s / 86400)} d ago`;
  return new Date(t).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
};
const when = t => new Date(t).toLocaleString(undefined, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
// apps get a steady color from their name, all at one OKLCH lightness
const appColor = a => `oklch(72% 0.14 ${Math.round(([...a].reduce((h, c) => (h * 31 + c.charCodeAt(0)) >>> 0, 7) * 137.508) % 360)})`;   // golden-angle steps keep hues apart
const appName = a => a.split('-').map(w => w[0].toUpperCase() + w.slice(1)).join(' ');
const multiApp = () => (state.stats?.apps || []).length > 1;
const appChip = a => multiApp() ? `<span class="chip"><span class="dot" style="background:${appColor(a)}"></span>${esc(appName(a))}</span>` : '';
const kindChip = k => k === 'bug' ? `<span class="chip bug">${icon('bug')}Bug</span>` : `<span class="chip feature">${icon('spark')}Feature</span>`;
const stateIcon = s => `<svg class="ico state ${s}"><use href="#i-${s === 'open' ? 'dot-circle' : 'check-circle'}"/></svg>`;

function renderList() {
  renderToolbar();
  const list = $('#list');
  if (state.loading) {
    list.innerHTML = Array.from({ length: 7 }, () => `<div class="row"><div class="sk" style="width:18px;height:18px"></div><div class="sk" style="width:20px;height:20px;border-radius:50%"></div><div><div class="sk" style="height:13px;width:${40 + Math.random() * 35}%"></div><div class="sk" style="height:11px;width:70%;margin-top:8px"></div></div><div class="sk" style="width:90px;height:12px"></div></div>`).join('');
    return;
  }
  if (!state.items.length) {
    const filtered = state.kind || state.tag || state.user || state.logs || state.q;
    list.innerHTML = `<div class="empty">${icon(filtered ? 'search' : 'inbox')}<h4>${filtered ? 'Nothing matches' : state.status === 'open' ? 'Inbox zero' : 'No reports yet'}</h4><div>${filtered ? 'Try fewer filters.' : state.status === 'open' ? 'Every report is closed.' : 'Reports from Settings -> Feedback show up here.'}</div></div>`;
    edges(); return;
  }
  list.innerHTML = state.items.map((r, i) => `
    <div class="row ${state.selected === r.id ? 'sel' : ''} ${i === state.cursor ? 'cursor' : ''}" role="option" aria-selected="${state.selected === r.id}" data-id="${r.id}" data-i="${i}">
      <label class="check" data-stop><input type="checkbox" ${state.checked.has(r.id) ? 'checked' : ''} data-check="${r.id}" aria-label="Select #${r.id}"><span></span></label>
      ${stateIcon(r.status)}
      <div class="body">
        <div class="title"><span class="num">#${r.id}</span><span class="subject">${esc(r.subject)}</span></div>
        <div class="preview">${esc(r.preview)}</div>
        <div class="chips">${appChip(r.app)}${kindChip(r.kind)}${r.tags.map(t => `<span class="chip tag">${esc(t)}</span>`).join('')}</div>
      </div>
      <div class="meta">
        <div class="line">${r.has_logs ? `<span title="Logs attached">${icon('file')}</span>` : ''}${r.notes ? `<span title="${r.notes} note${r.notes > 1 ? 's' : ''}">${icon('note')}</span>` : ''}<span>${ago(r.created_at)}</span></div>
        <div class="line"><span class="mono">${esc(r.user_id)}</span></div>
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
  panel.innerHTML = `
    <div class="d-head" tabindex="-1">
      <div class="d-top">
        <span class="badge ${r.status}">${icon(closed ? 'check-circle' : 'dot-circle')}${closed ? 'Closed' : 'Open'}</span>
        ${kindChip(r.kind)}
        <span class="num">#${r.id}</span>
        <button class="btn ghost copy" data-copy-link title="Copy link">${icon('link', 'a')}${icon('check', 'b')}</button>
        <button class="btn ghost x" data-close title="Close panel (Esc)">${icon('close')}</button>
      </div>
      <h2>${esc(r.subject)}</h2>
      <div class="d-actions">
        <button class="btn ${closed ? 'open-it' : 'close-it'}" data-toggle>${icon(closed ? 'dot-circle' : 'check-circle')}${closed ? 'Reopen' : 'Close report'}</button>
        <button class="btn" data-user>${icon('user')}${r.user_reports} from this person</button>
      </div>
    </div>
    <div class="d-body">
      <div class="card"><div class="text">${esc(r.body)}</div></div>
      <div class="card">
        <h4>${icon('window')}Details</h4>
        <dl class="grid">
          ${multiApp() ? `<dt>App</dt><dd>${esc(appName(r.app))}</dd>` : ''}
          <dt>From</dt><dd><a href="#" data-user class="mono">${esc(r.user_id)}</a></dd>
          <dt>Version</dt><dd>${r.version ? 'v' + esc(r.version) : 'Unknown'} · ${r.source === 'store' ? 'Microsoft Store' : r.source === 'github' ? 'GitHub' : 'Unknown'}</dd>
          <dt>Windows</dt><dd>${esc(r.windows || 'Unknown')}</dd>
          <dt>Sent</dt><dd>${when(r.created_at)}</dd>
          ${r.closed_at ? `<dt>Closed</dt><dd>${when(r.closed_at)}</dd>` : ''}
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
        <div class="notes">${r.notes.map(n => `<div class="note"><div class="by">${esc(n.author)} · ${when(n.created_at)}</div><div class="text">${esc(n.body)}</div></div>`).join('')}</div>
        <form class="note-form" id="note-form">
          <textarea id="note-text" placeholder="Only you can see notes" maxlength="10000"></textarea>
          <div class="row-end"><button class="btn primary" type="submit">Add note</button></div>
        </form>
      </div>
      <div class="card">
        <h4>${icon('clock')}History</h4>
        <ol class="timeline">${r.events.map(e => `<li class="${esc(e.what.split(' ')[0])}">${esc(e.what)} by <span class="mono">${esc(e.actor)}</span><time>${ago(e.at)}</time></li>`).join('')}</ol>
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

// ---- input ----------------------------------------------------------------------------
function apply(set) {
  Object.assign(state, set);
  state.checked.clear();
  if ('q' in set) $('#q').value = state.q;
  syncHash(); load();
}

document.addEventListener('click', e => {
  const t = e.target.closest('[data-set]');
  if (t) { apply(JSON.parse(t.dataset.set)); return; }
  const seg = e.target.closest('#status-seg button');
  if (seg) { apply({ status: seg.dataset.status }); return; }
  const check = e.target.closest('[data-check]');
  if (check) { const id = +check.dataset.check; check.checked ? state.checked.add(id) : state.checked.delete(id); renderToolbar(); return; }
  if (e.target.closest('[data-stop]')) return;
  const row = e.target.closest('.row[data-id]');
  if (row) { state.cursor = +row.dataset.i; open(+row.dataset.id); return; }
  const r = state.report;
  if (e.target.closest('[data-close]')) { closeDetail(); return; }
  if (r && e.target.closest('[data-toggle]')) { setStatus(r.id, r.status === 'open' ? 'closed' : 'open'); return; }
  if (r && e.target.closest('[data-user]')) { e.preventDefault(); apply({ user: r.user_id, status: 'all', kind: '', tag: '', logs: false, q: '' }); return; }
  const untag = e.target.closest('[data-untag]');
  if (r && untag) { patch(r.id, { removeTags: [untag.dataset.untag] }); return; }
  const add = e.target.closest('[data-addtag]');
  if (r && add) { patch(r.id, { addTags: [add.dataset.addtag] }, `Tagged ${add.dataset.addtag}`); return; }
  const cl = e.target.closest('[data-copy-link]');
  if (r && cl) { navigator.clipboard.writeText(location.href); copied(cl); return; }
  const cg = e.target.closest('[data-copy-logs]');
  if (r && cg) { navigator.clipboard.writeText(r.logs || ''); copied(cg); return; }
  const bulk = e.target.closest('[data-bulk]');
  if (bulk) {
    const ids = [...state.checked];
    api('/api/bulk', { method: 'POST', body: JSON.stringify({ ids, status: bulk.dataset.bulk }) }).then(() => {
      toast(`${bulk.dataset.bulk === 'closed' ? 'Closed' : 'Reopened'} ${ids.length} ${ids.length === 1 ? 'report' : 'reports'}`);
      state.checked.clear(); load({ keepCursor: true });
      if (state.selected && ids.includes(state.selected)) open(state.selected);
    });
    return;
  }
  const exp = e.target.closest('#export-btn');
  const menu = $('#export-menu');
  if (exp) { menu.hidden = !menu.hidden; exp.setAttribute('aria-expanded', String(!menu.hidden)); return; }
  if (!e.target.closest('#export-menu')) menu.hidden = true;
  if (e.target.closest('#refresh')) { load({ keepCursor: true }); if (state.selected) open(state.selected); }
});

$('#select-all').addEventListener('change', e => {
  state.checked = e.target.checked ? new Set(state.items.map(i => i.id)) : new Set();
  renderList();
});
$('#sort').addEventListener('change', e => apply({ sort: e.target.value }));
let qTimer;
$('#q').addEventListener('input', e => { clearTimeout(qTimer); qTimer = setTimeout(() => apply({ q: e.target.value.trim() }), 180); });
$('#list').addEventListener('scroll', edges, { passive: true });
window.addEventListener('resize', () => { renderToolbar(); edges(); });

document.addEventListener('submit', e => {
  if (e.target.id !== 'note-form') return;
  e.preventDefault();
  const text = $('#note-text').value.trim(), r = state.report;
  if (!text || !r) return;
  api(`/api/reports/${r.id}/notes`, { method: 'POST', body: JSON.stringify({ body: text }) }).then(d => { state.report = d; renderDetail(); toast('Note added'); load({ keepCursor: true }); });
});

document.addEventListener('keydown', e => {
  const r = state.report;
  if (e.target.id === 'tag-input' && e.key === 'Enter') {
    const tag = e.target.value.trim().toLowerCase();
    if (tag && r) patch(r.id, { addTags: [tag] }, `Tagged ${tag}`);
    return;
  }
  if (e.target.id === 'log-filter') return;
  if (e.target.id === 'note-text' && e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { $('#note-form').requestSubmit(); return; }
  if (e.target.matches('input, textarea, select')) { if (e.key === 'Escape') e.target.blur(); return; }
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  const move = d => {
    if (!state.items.length) return;
    state.cursor = Math.max(0, Math.min(state.items.length - 1, state.cursor + d));
    // with a report open, moving opens the next one: reading through the inbox in a row
    if (state.selected) open(state.items[state.cursor].id); else renderList();
    $(`.row[data-i="${state.cursor}"]`)?.scrollIntoView({ block: 'nearest' });
  };
  switch (e.key) {
    case 'j': case 'ArrowDown': e.preventDefault(); move(1); break;
    case 'k': case 'ArrowUp': e.preventDefault(); move(-1); break;
    case 'Enter': if (state.items[state.cursor]) open(state.items[state.cursor].id); break;
    case 'x': { const id = state.selected ?? state.items[state.cursor]?.id; const it = r?.id === id ? r : state.items.find(i => i.id === id); if (it) setStatus(id, it.status === 'open' ? 'closed' : 'open'); break; }
    case ' ': { e.preventDefault(); const id = state.items[state.cursor]?.id; if (id) { state.checked.has(id) ? state.checked.delete(id) : state.checked.add(id); renderList(); } break; }
    case '/': e.preventDefault(); $('#q').focus(); $('#q').select(); break;
    case 't': if (r) { e.preventDefault(); $('#tag-input')?.focus(); } break;
    case 'n': if (r) { e.preventDefault(); $('#note-text')?.focus(); } break;
    case '1': apply({ status: 'open' }); break;
    case '2': apply({ status: 'closed' }); break;
    case '3': apply({ status: 'all' }); break;
    case 'r': load({ keepCursor: true }); break;
    case '?': $('#help').showModal(); break;
    case 'Escape': if (state.selected) closeDetail(); break;
  }
});

document.addEventListener('input', e => {
  if (e.target.id !== 'log-filter') return;
  const f = e.target.value.toLowerCase();
  const lines = (state.report?.logs || '').split('\n');
  $('#log-view').innerHTML = lines.filter(l => !f || l.toLowerCase().includes(f)).map(colorLog).join('');
});

window.addEventListener('hashchange', () => { const id = readHash(); $('#q').value = state.q; load(); if (id && id !== state.selected) open(id); });

// ---- start ----------------------------------------------------------------------------
const first = readHash();
$('#q').value = state.q;
render();
load();
if (first) open(first);
api('/api/me').then(m => { $('#who').textContent = m.who; }).catch(() => {});
// new reports appear without a reload; paused while the tab is hidden
setInterval(() => { if (!document.hidden) load({ keepCursor: true }); }, 60_000);
