// The dashboard's API. Only reached after access.js has identified the caller.
// Reports are addressed the way people see them: by app and number (isola,
// #482913). The internal row id never leaves the database.
import { json, message, readJson } from './util.js';

const TAG = /^[a-z0-9][a-z0-9 -]{0,23}$/;
const SORTS = {
  new: 'r.created_at DESC, r.id DESC',
  old: 'r.created_at ASC, r.id ASC',
  updated: 'r.updated_at DESC, r.id DESC',
  user: 'r.user_id ASC, r.created_at DESC',
};
const PUBLIC = 'r.app, r.number, r.user_id, r.kind, r.subject, r.status, r.version, r.source, r.tz, r.created_at, r.updated_at';

async function tagsFor(db, rowIds) {
  if (!rowIds.length) return new Map();
  const { results } = await db.prepare(`SELECT report_id, tag FROM tags WHERE report_id IN (${rowIds.map(() => '?').join(',')}) ORDER BY tag`).bind(...rowIds).all();
  const m = new Map();
  for (const t of results) (m.get(t.report_id) ?? m.set(t.report_id, []).get(t.report_id)).push(t.tag);
  return m;
}

export async function list(db, q) {
  const where = [], args = [];
  const app = q.get('app');
  if (app) { where.push('r.app = ?'); args.push(app); }
  const status = q.get('status') || 'open';
  if (status !== 'all') { where.push('r.status = ?'); args.push(status); }
  const kind = q.get('kind');
  if (kind === 'bug' || kind === 'feature') { where.push('r.kind = ?'); args.push(kind); }
  if (q.get('logs') === '1') where.push('r.logs IS NOT NULL');
  const user = q.get('user');
  if (user) { where.push('r.user_id = ?'); args.push(user); }
  const tag = q.get('tag');
  if (tag) { where.push('EXISTS (SELECT 1 FROM tags t WHERE t.report_id = r.id AND t.tag = ?)'); args.push(tag); }
  const text = (q.get('q') || '').trim();
  if (text) {
    // "#482913" is a report and nothing else; "@" and 20 digits is a person;
    // other digits find either; anything else searches the words people wrote
    const num = /^#(\d+)$/.exec(text), who = /^@?(\d{20})$/.exec(text), bare = /^(\d+)$/.exec(text);
    if (num) { where.push('r.number = ?'); args.push(+num[1]); }
    else if (who) { where.push('r.user_id = ?'); args.push(who[1]); }
    else if (bare) { where.push('(r.number = ? OR r.user_id LIKE ?)'); args.push(+bare[1], `%${bare[1]}%`); }
    else { where.push('(r.subject LIKE ? OR r.body LIKE ?)'); args.push(`%${text}%`, `%${text}%`); }
  }
  const w = where.length ? 'WHERE ' + where.join(' AND ') : '';
  const order = SORTS[q.get('sort')] || SORTS.new;
  const limit = Math.min(200, Math.max(1, +q.get('limit') || 100)), offset = Math.max(0, +q.get('offset') || 0);
  const [{ results }, count] = await Promise.all([
    db.prepare(`SELECT r.id, ${PUBLIC}, r.logs IS NOT NULL AS has_logs, substr(r.body, 1, 160) AS preview,
                       (SELECT COUNT(*) FROM notes n WHERE n.report_id = r.id) AS notes
                FROM reports r ${w} ORDER BY ${order} LIMIT ? OFFSET ?`).bind(...args, limit, offset).all(),
    db.prepare(`SELECT COUNT(*) AS n FROM reports r ${w}`).bind(...args).first(),
  ]);
  const tags = await tagsFor(db, results.map(r => r.id));
  return { total: count.n, items: results.map(({ id, ...r }) => ({ ...r, has_logs: !!r.has_logs, tags: tags.get(id) || [] })) };
}

const row = (db, app, number) => db.prepare('SELECT * FROM reports WHERE app = ? AND number = ?').bind(app, number).first();

export async function detail(db, app, number) {
  const r = await row(db, app, number);
  if (!r) return null;
  const [tags, notes, events, fromUser, everywhere] = await Promise.all([
    db.prepare('SELECT tag FROM tags WHERE report_id = ? ORDER BY tag').bind(r.id).all(),
    db.prepare('SELECT author, body, created_at FROM notes WHERE report_id = ? ORDER BY id').bind(r.id).all(),
    db.prepare('SELECT actor, what, at FROM events WHERE report_id = ? ORDER BY id').bind(r.id).all(),
    db.prepare('SELECT COUNT(*) AS n FROM reports WHERE app = ? AND user_id = ?').bind(app, r.user_id).first(),
    db.prepare('SELECT COUNT(*) AS n FROM reports WHERE user_id = ?').bind(r.user_id).first(),
  ]);
  const { id, fingerprint, ip_hash, ...rest } = r;   // internal bookkeeping stays internal
  return { ...rest, tags: tags.results.map(t => t.tag), notes: notes.results, events: events.results, user_reports: fromUser.n, user_reports_all: everywhere.n };
}

/** Status and tag changes, each recorded in the report's history. */
export async function change(db, app, number, body, who, now) {
  const r = await row(db, app, number);
  if (!r) return { error: 404 };
  const id = r.id, steps = [];
  const log = what => steps.push(db.prepare('INSERT INTO events (report_id, actor, what, at) VALUES (?, ?, ?, ?)').bind(id, who, what, now));
  if (body.status === 'open' || body.status === 'closed') {
    if (body.status !== r.status) {
      steps.push(db.prepare('UPDATE reports SET status = ?, closed_at = ? WHERE id = ?').bind(body.status, body.status === 'closed' ? now : null, id));
      log(body.status === 'closed' ? 'closed' : 'reopened');
    }
  } else if (body.status != null) return { error: 400, message: 'Bad status.' };
  for (const t of body.addTags || []) {
    if (typeof t !== 'string' || !TAG.test(t)) return { error: 400, message: `Bad tag "${t}".` };
    steps.push(db.prepare('INSERT OR IGNORE INTO tags (report_id, tag) VALUES (?, ?)').bind(id, t));
    log(`tagged ${t}`);
  }
  for (const t of body.removeTags || []) {
    if (typeof t !== 'string') continue;
    steps.push(db.prepare('DELETE FROM tags WHERE report_id = ? AND tag = ?').bind(id, t));
    log(`untagged ${t}`);
  }
  if (steps.length) { steps.push(db.prepare('UPDATE reports SET updated_at = ? WHERE id = ?').bind(now, id)); await db.batch(steps); }
  return { ok: true };
}

const counts = `SUM(status = 'open') AS open, SUM(status = 'closed') AS closed,
  SUM(status = 'open' AND kind = 'bug') AS bugs, SUM(status = 'open' AND kind = 'feature') AS features,
  SUM(created_at > ?) AS week, COUNT(DISTINCT user_id) AS users, COUNT(*) AS total`;
const z = c => ({ open: c?.open ?? 0, closed: c?.closed ?? 0, bugs: c?.bugs ?? 0, features: c?.features ?? 0, week: c?.week ?? 0, users: c?.users ?? 0, total: c?.total ?? 0 });

export async function stats(db, now, app) {
  const week = now - 7 * 86_400_000;
  const w = app ? 'WHERE app = ?' : '', a = app ? [app] : [];
  const [c, tags, apps] = await Promise.all([
    db.prepare(`SELECT ${counts} FROM reports ${w}`).bind(week, ...a).first(),
    db.prepare(`SELECT t.tag, COUNT(*) AS n FROM tags t JOIN reports r ON r.id = t.report_id ${app ? 'WHERE r.app = ?' : ''} GROUP BY t.tag ORDER BY n DESC, t.tag LIMIT 30`).bind(...a).all(),
    db.prepare("SELECT app, SUM(status = 'open') AS open, COUNT(*) AS n FROM reports GROUP BY app ORDER BY app").all(),
  ]);
  return { ...z(c), tags: tags.results, apps: apps.results };
}

/** The home page: every app the hub serves, its numbers and its newest reports. */
export async function summary(db, now, apps) {
  const week = now - 7 * 86_400_000;
  const out = [];
  for (const app of apps) {
    const [c, recent] = await Promise.all([
      db.prepare(`SELECT ${counts}, MAX(created_at) AS last FROM reports WHERE app = ?`).bind(week, app).first(),
      db.prepare('SELECT number, kind, subject, status, created_at FROM reports WHERE app = ? ORDER BY created_at DESC LIMIT 5').bind(app).all(),
    ]);
    out.push({ app, ...z(c), last: c?.last ?? null, recent: recent.results });
  }
  const people = await db.prepare('SELECT COUNT(*) AS n FROM users').first();
  return { apps: out, people: people.n };
}

const csvCell = v => { const s = v == null ? '' : String(v); return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };

export async function exportAll(db, format, app) {
  const { results } = await db.prepare(`SELECT r.app, r.number, r.status, r.kind, r.user_id, r.subject, r.body, r.version, r.windows, r.source, r.tz,
      r.created_at, r.closed_at, (SELECT group_concat(tag, ' ') FROM tags t WHERE t.report_id = r.id) AS tags
      FROM reports r ${app ? 'WHERE r.app = ?' : ''} ORDER BY r.app, r.created_at`).bind(...(app ? [app] : [])).all();
  const name = `feedback${app ? '-' + app : ''}`;
  if (format === 'csv') {
    const cols = Object.keys(results[0] || { app: '', number: 0 });
    const rows = [cols.join(','), ...results.map(r => cols.map(c => csvCell(r[c])).join(','))];
    return new Response(rows.join('\r\n'), { headers: { 'content-type': 'text/csv; charset=utf-8', 'content-disposition': `attachment; filename="${name}.csv"` } });
  }
  return new Response(JSON.stringify(results, null, 2), { headers: { 'content-type': 'application/json', 'content-disposition': `attachment; filename="${name}.json"` } });
}

export async function admin(req, env, who, url, now, ctx = {}) {
  const db = env.DB, path = url.pathname, q = url.searchParams;
  const m = /^\/api\/reports\/([a-z][a-z0-9-]{1,31})\/(\d{1,15})(\/notes)?$/.exec(path);
  if (path === '/api/me' && req.method === 'GET') return json(200, { who, hostApp: ctx.hostApp ?? '', apps: ctx.apps ?? [] });
  if (path === '/api/summary' && req.method === 'GET') return json(200, await summary(db, now, ctx.apps ?? []));
  if (path === '/api/stats' && req.method === 'GET') return json(200, await stats(db, now, q.get('app')));
  if (path === '/api/reports' && req.method === 'GET') return json(200, await list(db, q));
  if (path === '/api/export' && req.method === 'GET') return exportAll(db, q.get('format'), q.get('app'));
  if (path === '/api/bulk' && req.method === 'POST') {
    const r = await readJson(req);
    const items = r.value?.items;
    if (r.error || !Array.isArray(items) || items.length > 200 || !items.every(i => typeof i?.app === 'string' && Number.isInteger(i?.number))) return message(400, 'Bad request.');
    for (const i of items) { const res = await change(db, i.app, i.number, r.value, who, now); if (res.error && res.error !== 404) return message(res.error, res.message); }
    return json(200, { ok: true });
  }
  if (m) {
    const [, app, n, notes] = m, number = +n;
    if (notes) {
      if (req.method !== 'POST') return message(405, 'Method not allowed.');
      const r = await readJson(req, 20_000);
      const body = r.value?.body;
      if (r.error || typeof body !== 'string' || !body.trim() || body.length > 10_000) return message(400, 'Bad note.');
      const rr = await row(db, app, number);
      if (!rr) return message(404, 'No such report.');
      await db.batch([
        db.prepare('INSERT INTO notes (report_id, author, body, created_at) VALUES (?, ?, ?, ?)').bind(rr.id, who, body.trim(), now),
        db.prepare('UPDATE reports SET updated_at = ? WHERE id = ?').bind(now, rr.id),
      ]);
      return json(201, await detail(db, app, number));
    }
    if (req.method === 'GET') { const d = await detail(db, app, number); return d ? json(200, d) : message(404, 'No such report.'); }
    if (req.method === 'PATCH') {
      const r = await readJson(req);
      if (r.error) return message(400, r.error);
      const res = await change(db, app, number, r.value || {}, who, now);
      if (res.error) return message(res.error, res.message || 'No such report.');
      return json(200, await detail(db, app, number));
    }
  }
  return message(404, 'Not found.');
}
