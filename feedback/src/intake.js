// Reports from the app. Public: anyone can call this, including modified
// copies of the app, so every limit the app shows is enforced again here.
import { fingerprint, message, json, readJson, sha256hex } from './util.js';

// the apps this hub takes reports for (var APPS, comma separated)
export const appsOf = env => (env.APPS || 'isola').split(',').map(s => s.trim()).filter(Boolean);
const APP = /^[a-z][a-z0-9-]{1,31}$/;

export const LIMITS = { perUserMs: 86_400_000, perIpPerDay: 3, allPerDay: 100, subjectMax: 120, textMin: 10, textMax: 4000, logsMax: 60_000 };

const USER = /^User[1-9]\d{5,19}$/;
const VERIFIER = /^[0-9a-f]{32}$/;

export function validate(b, apps = ['isola']) {
  if (!b || typeof b !== 'object') return 'Expected a JSON object.';
  if (typeof b.app !== 'string' || !APP.test(b.app) || !apps.includes(b.app)) return 'Unknown app.';
  if (typeof b.user !== 'string' || !USER.test(b.user)) return 'Bad user.';
  if (typeof b.verifier !== 'string' || !VERIFIER.test(b.verifier)) return 'Bad verifier.';
  if (b.kind !== 'bug' && b.kind !== 'feature') return 'Bad kind.';
  if (typeof b.subject !== 'string' || !b.subject.trim() || b.subject.length > LIMITS.subjectMax) return 'Bad subject.';
  if (typeof b.text !== 'string' || b.text.trim().length < LIMITS.textMin || b.text.length > LIMITS.textMax) return 'Bad description.';
  for (const k of ['version', 'windows', 'source']) if (b[k] != null && (typeof b[k] !== 'string' || b[k].length > 40)) return `Bad ${k}.`;
  if (b.logs != null && (typeof b.logs !== 'string' || b.logs.length > LIMITS.logsMax)) return 'Logs too long.';
  return null;
}

/**
 * The ID this PC is known by. A PC seen before keeps the ID it was given. A new
 * PC gets the ID it derived, unless another PC already holds it (two machines
 * hashing to the same number), in which case it gets the next free "-2", "-3".
 */
export async function resolveUser(db, app, base, verifier) {
  const known = await db.prepare('SELECT id FROM users WHERE app = ? AND verifier = ?').bind(app, verifier).first();
  if (known) return { id: known.id, isNew: false };
  for (let n = 1; n < 1000; n++) {
    const id = n === 1 ? base : `${base}-${n}`;
    if (!(await db.prepare('SELECT 1 AS x FROM users WHERE app = ? AND id = ?').bind(app, id).first())) return { id, isNew: true };
  }
  throw new Error('no free user id');
}

// the address only as a salted hash: enough to count, nothing to keep
const ipHash = (ip, env) => sha256hex(`${env.IP_SALT || 'isola'}:${ip}`);

export async function submit(req, env, now) {
  const r = await readJson(req);
  if (r.error) return message(400, r.error);
  const b = r.value;
  if (b && typeof b === 'object' && b.app == null) b.app = 'isola';   // Isola 1.4.0 sends it; nothing older talks to the hub
  const bad = validate(b, appsOf(env));
  if (bad) return message(400, bad);
  const db = env.DB;

  const app = b.app;
  const user = await resolveUser(db, app, b.user, b.verifier);
  const ip = await ipHash(req.headers.get('cf-connecting-ip') || 'unknown', env);
  const dayAgo = now - 86_400_000;
  const [last, fromIp, all] = await Promise.all([
    db.prepare('SELECT MAX(created_at) AS t FROM reports WHERE app = ? AND user_id = ?').bind(app, user.id).first(),
    db.prepare('SELECT COUNT(*) AS n FROM reports WHERE app = ? AND ip_hash = ? AND created_at > ?').bind(app, ip, dayAgo).first(),
    db.prepare('SELECT COUNT(*) AS n FROM reports WHERE app = ? AND created_at > ?').bind(app, dayAgo).first(),
  ]);
  if (last?.t && now - last.t < LIMITS.perUserMs) return message(429, 'Thanks, you sent feedback in the last day. Try again tomorrow.', { user: user.id });
  // made-up user numbers get past the per-person limit, so the address and the
  // whole service have ceilings too
  if (fromIp.n >= LIMITS.perIpPerDay || all.n >= LIMITS.allPerDay) return message(429, 'Feedback is busy right now. Try again tomorrow.', { user: user.id });
  const fp = await fingerprint(b.subject, b.text);
  if (await db.prepare('SELECT 1 AS x FROM reports WHERE app = ? AND fingerprint = ?').bind(app, fp).first()) return message(409, "You've already sent this.", { user: user.id });

  const steps = [];
  if (user.isNew) steps.push(db.prepare('INSERT INTO users (app, id, verifier, first_seen) VALUES (?, ?, ?, ?)').bind(app, user.id, b.verifier, now));
  steps.push(db.prepare(`INSERT INTO reports (app, user_id, kind, subject, body, version, windows, source, logs, fingerprint, ip_hash, created_at, updated_at)
                         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
    .bind(app, user.id, b.kind, b.subject.trim(), b.text.trim(), b.version ?? null, b.windows ?? null, b.source ?? null, b.logs || null, fp, ip, now, now));
  // in the same transaction, so a report never exists without its history
  steps.push(db.prepare("INSERT INTO events (report_id, actor, what, at) VALUES (last_insert_rowid(), ?, 'opened', ?)").bind(user.id, now));
  try {
    const res = await db.batch(steps);
    const id = res[res.length - 2].meta.last_row_id;
    return json(201, { message: 'Thanks for your feedback.', report: id, user: user.id });
  } catch (e) {
    // two sends racing for the same new ID or the same text: the database's
    // UNIQUE constraints refuse the second, and nothing half-written remains
    return message(409, "You've already sent this, or it's being sent. Try again in a minute.");
  }
}

/** A PC's own reports, with their status, for the app's "Your reports" list. */
export async function mine(req, env) {
  const r = await readJson(req, 2000);
  if (r.error) return message(400, r.error);
  const v = r.value?.verifier, app = r.value?.app ?? 'isola';
  if (typeof v !== 'string' || !VERIFIER.test(v)) return message(400, 'Bad verifier.');
  if (!appsOf(env).includes(app)) return message(400, 'Unknown app.');
  const user = await env.DB.prepare('SELECT id FROM users WHERE app = ? AND verifier = ?').bind(app, v).first();
  if (!user) return json(200, { user: null, reports: [] });
  const { results } = await env.DB.prepare(
    'SELECT id, kind, subject, status, created_at, closed_at FROM reports WHERE app = ? AND user_id = ? ORDER BY id DESC LIMIT 20').bind(app, user.id).all();
  return json(200, { user: user.id, reports: results });
}
