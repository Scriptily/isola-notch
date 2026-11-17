// The dashboard's API. Only reached after access.js has identified the caller.
import { json, message, readJson } from './util.js';

const TAG = /^[a-z0-9][a-z0-9 -]{0,23}$/;
const SORTS = {
  new: 'r.id DESC',
  old: 'r.id ASC',
  updated: 'r.updated_at DESC, r.id DESC',
  user: 'r.user_id ASC, r.id DESC',
};

async function tagsFor(db, ids) {
  if (!ids.length) return new Map();
  const { results } = await db.prepare(`SELECT report_id, tag FROM tags WHERE report_id IN (${ids.map(() => '?').join(',')}) ORDER BY tag`).bind(...ids).all();
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
    // "#12" is report 12 and nothing else; bare digits also find user IDs
    // containing them; anything else searches the words people wrote
    const tagged = /^#(\d+)$/.exec(text), bare = /^(\d+)$/.exec(text);
    if (tagged) { where.push('r.id = ?'); args.push(+tagged[1]); }
    else if (bare) { where.push('(r.id = ? OR r.user_id LIKE ?)'); args.push(+bare[1], `%${bare[1]}%`); }
    else { where.push('(r.subject LIKE ? OR r.body LIKE ? OR r.user_id LIKE ?)'); args.push(`%${text}%`, `%${text}%`, `%${text}%`); }
  }
  const w = where.length ? 'WHERE ' + where.join(' AND ') : '';
  const order = SORTS[q.get('sort')] || SORTS.new;
  const limit = Math.min(200, Math.max(1, +q.get('limit') || 100)), offset = Math.max(0, +q.get('offset') || 0);
  const [{ results }, count] = await Promise.all([
    db.prepare(`SELECT r.id, r.app, r.user_id, r.kind, r.subject, r.status, r.version, r.source, r.created_at, r.updated_at,
                       r.logs IS NOT NULL AS has_logs, substr(r.body, 1, 160) AS preview,
                       (SELECT COUNT(*) FROM notes n WHERE n.report_id = r.id) AS notes
                FROM reports r ${w} ORDER BY ${order} LIMIT ? OFFSET ?`).bind(...args, limit, offset).all(),
    db.prepare(`SELECT COUNT(*) AS n FROM reports r ${w}`).bind(...args).first(),
  ]);
  const tags = await tagsFor(db, results.map(r => r.id));
  return { total: count.n, items: results.map(r => ({ ...r, has_logs: !!r.has_logs, tags: tags.get(r.id) || [] })) };
}

export async function detail(db, id) {
  const r = await db.prepare('SELECT * FROM reports WHERE id = ?').bind(id).first();
  if (!r) return null;
  const [tags, notes, events, fromUser] = await Promise.all([
    db.prepare('SELECT tag FROM tags WHERE report_id = ? ORDER BY tag').bind(id).all(),
    db.prepare('SELECT id, author, body, created_at FROM notes WHERE report_id = ? ORDER BY id').bind(id).all(),
    db.prepare('SELECT actor, what, at FROM events WHERE report_id = ? ORDER BY id').bind(id).all(),
    db.prepare('SELECT COUNT(*) AS n FROM reports WHERE app = ? AND user_id = ?').bind(r.app, r.user_id).first(),
  ]);
  const { fingerprint, ip_hash, ...rest } = r;   // internal bookkeeping stays internal
  return { ...rest, tags: tags.results.map(t => t.tag), notes: notes.results, events: events.results, user_reports: fromUser.n };
}

/** Status and tag changes, each recorded in the report's history. */
export async function change(db, id, body, who, now) {
  const r = await db.prepare('SELECT status FROM reports WHERE id = ?').bind(id).first();
  if (!r) return { error: 404 };
  const steps = [];
  const touch = () => steps.push(db.prepare('UPDATE reports SET updated_at = ? WHERE id = ?').bind(now, id));
  if (body.status === 'open' || body.status === 'closed') {
    if (body.status !== r.status) {
      steps.push(db.prepare('UPDATE reports SET status = ?, closed_at = ? WHERE id = ?').bind(body.status, body.status === 'closed' ? now : null, id));
      steps.push(db.prepare('INSERT INTO events (report_id, actor, what, at) VALUES (?, ?, ?, ?)').bind(id, who, body.status === 'closed' ? 'closed' : 'reopened', now));
    }
  } else if (body.status != null) return { error: 400, message: 'Bad status.' };
  for (const t of body.addTags || []) {
    if (typeof t !== 'string' || !TAG.test(t)) return { error: 400, message: `Bad tag "${t}".` };
    steps.push(db.prepare('INSERT OR IGNORE INTO tags (report_id, tag) VALUES (?, ?)').bind(id, t));
    steps.push(db.prepare('INSERT INTO events (report_id, actor, what, at) VALUES (?, ?, ?, ?)').bind(id, who, `tagged ${t}`, now));
  }
  for (const t of body.removeTags || []) {
    if (typeof t !== 'string') continue;
    steps.push(db.prepare('DELETE FROM tags WHERE report_id = ? AND tag = ?').bind(id, t));
    steps.push(db.prepare('INSERT INTO events (report_id, actor, what, at) VALUES (?, ?, ?, ?)').bind(id, who, `untagged ${t}`, now));
  }
  if (steps.length) { touch(); await db.batch(steps); }
  return { ok: true };
}

export async function stats(db, now, app) {
  const week = now - 7 * 86_400_000;
  const w = app ? 'WHERE app = ?' : '', a = app ? [app] : [];
  const [c, tags, apps] = await Promise.all([
    db.prepare(`SELECT
        SUM(status = 'open') AS open, SUM(status = 'closed') AS closed,
        SUM(status = 'open' AND kind = 'bug') AS bugs, SUM(status = 'open' AND kind = 'feature') AS features,
        SUM(created_at > ?) AS week, COUNT(DISTINCT app || ':' || user_id) AS users, COUNT(*) AS total FROM reports ${w}`).bind(week, ...a).first(),
    db.prepare(`SELECT t.tag, COUNT(*) AS n FROM tags t JOIN reports r ON r.id = t.report_id ${app ? 'WHERE r.app = ?' : ''} GROUP BY t.tag ORDER BY n DESC, t.tag LIMIT 30`).bind(...a).all(),
    db.prepare("SELECT app, SUM(status = 'open') AS open, COUNT(*) AS n FROM reports GROUP BY app ORDER BY app").all(),
  ]);
  const z = v => v ?? 0;
  return { open: z(c.open), closed: z(c.closed), bugs: z(c.bugs), features: z(c.features), week: z(c.week), users: z(c.users), total: z(c.total), tags: tags.results, apps: apps.results };
}

const csvCell = v => { const s = v == null ? '' : String(v); return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };

export async function exportAll(db, format) {
  const { results } = await db.prepare(`SELECT r.id, r.app, r.status, r.kind, r.user_id, r.subject, r.body, r.version, r.windows, r.source,
      r.created_at, r.closed_at, (SELECT group_concat(tag, ' ') FROM tags t WHERE t.report_id = r.id) AS tags FROM reports r ORDER BY r.id`).all();
  if (format === 'csv') {
    const cols = Object.keys(results[0] || { id: 0 });
    const rows = [cols.join(','), ...results.map(r => cols.map(c => csvCell(r[c])).join(','))];
    return new Response(rows.join('\r\n'), { headers: { 'content-type': 'text/csv; charset=utf-8', 'content-disposition': 'attachment; filename="isola-feedback.csv"' } });
  }
  return new Response(JSON.stringify(results, null, 2), { headers: { 'content-type': 'application/json', 'content-disposition': 'attachment; filename="isola-feedback.json"' } });
}

export async function admin(req, env, who, url, now) {
  const db = env.DB;
  const path = url.pathname;
  const m = /^\/api\/reports\/(\d+)(\/notes)?$/.exec(path);
  if (path === '/api/me' && req.method === 'GET') return json(200, { who });
  if (path === '/api/stats' && req.method === 'GET') return json(200, await stats(db, now, url.searchParams.get('app')));
  if (path === '/api/reports' && req.method === 'GET') return json(200, await list(db, url.searchParams));
  if (path === '/api/export' && req.method === 'GET') return exportAll(db, url.searchParams.get('format'));
  if (path === '/api/bulk' && req.method === 'POST') {
    const r = await readJson(req);
    const ids = r.value?.ids;
    if (r.error || !Array.isArray(ids) || ids.length > 200 || !ids.every(Number.isInteger)) return message(400, 'Bad request.');
    for (const id of ids) { const res = await change(db, id, r.value, who, now); if (res.error && res.error !== 404) return message(res.error, res.message); }
    return json(200, { ok: true });
  }
  if (m) {
    const id = +m[1];
    if (m[2]) {
      if (req.method !== 'POST') return message(405, 'Method not allowed.');
      const r = await readJson(req, 20_000);
      const body = r.value?.body;
      if (r.error || typeof body !== 'string' || !body.trim() || body.length > 10_000) return message(400, 'Bad note.');
      if (!(await db.prepare('SELECT 1 AS x FROM reports WHERE id = ?').bind(id).first())) return message(404, 'No such report.');
      await db.batch([
        db.prepare('INSERT INTO notes (report_id, author, body, created_at) VALUES (?, ?, ?, ?)').bind(id, who, body.trim(), now),
        db.prepare('UPDATE reports SET updated_at = ? WHERE id = ?').bind(now, id),
      ]);
      return json(201, await detail(db, id));
    }
    if (req.method === 'GET') { const d = await detail(db, id); return d ? json(200, d) : message(404, 'No such report.'); }
    if (req.method === 'PATCH') {
      const r = await readJson(req);
      if (r.error) return message(400, r.error);
      const res = await change(db, id, r.value || {}, who, now);
      if (res.error) return message(res.error, res.message || 'No such report.');
      return json(200, await detail(db, id));
    }
  }
  return message(404, 'Not found.');
}
