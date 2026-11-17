// Reports from the apps. Public: anyone can call this, including modified
// copies of an app, so every limit an app shows is enforced again here.
import { fingerprint, message, json, readJson, sha256hex } from './util.js';

// the apps this hub takes reports for (var APPS, comma separated)
export const appsOf = env => (env.APPS || 'isola').split(',').map(s => s.trim()).filter(Boolean);
const APP = /^[a-z][a-z0-9-]{1,31}$/;

export const LIMITS = { perUserMs: 86_400_000, perIpPerDay: 3, allPerDay: 100, subjectMax: 120, textMin: 10, textMax: 4000, logsMax: 60_000 };

export const USER = /^[1-9]\d{19}$/;           // always 20 digits
const VERIFIER = /^[0-9a-f]{32}$/;
const TZ = /^[+-](0\d|1[0-4]):[0-5]\d$/;

export function validate(b, apps = ['isola']) {
  if (!b || typeof b !== 'object') return 'Expected a JSON object.';
  if (typeof b.app !== 'string' || !APP.test(b.app) || !apps.includes(b.app)) return 'Unknown app.';
  if (typeof b.user !== 'string' || !USER.test(b.user)) return 'Bad user.';
  if (typeof b.verifier !== 'string' || !VERIFIER.test(b.verifier)) return 'Bad verifier.';
  if (b.kind !== 'bug' && b.kind !== 'feature') return 'Bad kind.';
  if (typeof b.subject !== 'string' || !b.subject.trim() || b.subject.length > LIMITS.subjectMax) return 'Bad subject.';
  if (typeof b.text !== 'string' || b.text.trim().length < LIMITS.textMin || b.text.length > LIMITS.textMax) return 'Bad description.';
  for (const k of ['version', 'windows', 'source']) if (b[k] != null && (typeof b[k] !== 'string' || b[k].length > 40)) return `Bad ${k}.`;
  if (b.tz != null && (typeof b.tz !== 'string' || !TZ.test(b.tz))) return 'Bad tz.';
  if (b.logs != null && (typeof b.logs !== 'string' || b.logs.length > LIMITS.logsMax)) return 'Logs too long.';
  return null;
}

const LOW = 10n ** 19n, SPAN = 9n * 10n ** 19n;   // 20-digit numbers: 10^19 .. 10^20 - 1

/** The k-th other 20-digit ID for a machine whose own is taken: still 20 digits, still derived from it. */
export async function alternateId(base, k) {
  const h = BigInt('0x' + (await sha256hex(`${base}:${k}`)).slice(0, 32));
  return (LOW + h % SPAN).toString();
}

/**
 * The ID this machine is known by, in every app. A machine seen before keeps
 * the ID it was given. A new machine gets the ID it derived, unless another
 * machine already holds it, in which case it gets another free 20-digit one.
 */
export async function resolveUser(db, base, verifier) {
  const known = await db.prepare('SELECT id FROM users WHERE verifier = ?').bind(verifier).first();
  if (known) return { id: known.id, isNew: false };
  for (let k = 0; k < 100; k++) {
    const id = k === 0 ? base : await alternateId(base, k);
    if (!(await db.prepare('SELECT 1 AS x FROM users WHERE id = ?').bind(id).first())) return { id, isNew: true };
  }
  throw new Error('no free user id');
}

/**
 * A report number this app has never handed out. Random rather than counting
 * up, so a number says nothing about how many reports there are. It has at
 * least 6 digits and grows so the app keeps 100 times more numbers free than
 * it has used: a random pick then almost never needs a second try.
 */
export async function newNumber(db, app, random) {
  const { n } = await db.prepare('SELECT COUNT(*) AS n FROM issued WHERE app = ?').bind(app).first();
  const digits = Math.min(15, Math.max(6, String(n * 100).length));
  for (let i = 0; i < 50; i++) {
    const lo = 10 ** (digits - 1);
    const num = lo + Math.floor(random() * 9 * lo);
    if (!(await db.prepare('SELECT 1 AS x FROM issued WHERE app = ? AND number = ?').bind(app, num).first())) return num;
  }
  throw new Error('no free report number');
}

export const cryptoRandom = () => crypto.getRandomValues(new Uint32Array(1))[0] / 2 ** 32;

// the address only as a salted hash: enough to count, nothing to keep
const ipHash = (ip, env) => sha256hex(`${env.IP_SALT || 'hub'}:${ip}`);

export async function submit(req, env, now, random = cryptoRandom) {
  const r = await readJson(req);
  if (r.error) return message(400, r.error);
  const b = r.value;
  const bad = validate(b, appsOf(env));
  if (bad) return message(400, bad);
  const db = env.DB, app = b.app;

  const user = await resolveUser(db, b.user, b.verifier);
  const ip = await ipHash(req.headers.get('cf-connecting-ip') || 'unknown', env);
  const dayAgo = now - 86_400_000;
  const [last, fromIp, all] = await Promise.all([
    db.prepare('SELECT MAX(created_at) AS t FROM reports WHERE app = ? AND user_id = ?').bind(app, user.id).first(),
    db.prepare('SELECT COUNT(*) AS n FROM reports WHERE app = ? AND ip_hash = ? AND created_at > ?').bind(app, ip, dayAgo).first(),
    db.prepare('SELECT COUNT(*) AS n FROM reports WHERE app = ? AND created_at > ?').bind(app, dayAgo).first(),
  ]);
  if (last?.t && now - last.t < LIMITS.perUserMs) return message(429, 'Thanks, you sent feedback in the last day. Try again tomorrow.', { user: user.id });
  // made-up IDs get past the per-person limit, so the address and the whole
  // app have ceilings too
  if (fromIp.n >= LIMITS.perIpPerDay || all.n >= LIMITS.allPerDay) return message(429, 'Feedback is busy right now. Try again tomorrow.', { user: user.id });
  const fp = await fingerprint(b.subject, b.text);
  const dup = () => db.prepare('SELECT 1 AS x FROM reports WHERE app = ? AND fingerprint = ?').bind(app, fp).first();
  if (await dup()) return message(409, "You've already sent this.", { user: user.id });

  for (let attempt = 0; attempt < 3; attempt++) {
    const number = await newNumber(db, app, random);
    const steps = [];
    if (user.isNew) steps.push(db.prepare('INSERT OR IGNORE INTO users (id, verifier, first_seen) VALUES (?, ?, ?)').bind(user.id, b.verifier, now));
    steps.push(db.prepare('INSERT INTO issued (app, number) VALUES (?, ?)').bind(app, number));
    steps.push(db.prepare(`INSERT INTO reports (app, number, user_id, kind, subject, body, version, windows, source, tz, logs, fingerprint, ip_hash, created_at, updated_at)
                           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
      .bind(app, number, user.id, b.kind, b.subject.trim(), b.text.trim(), b.version ?? null, b.windows ?? null, b.source ?? null, b.tz ?? null, b.logs || null, fp, ip, now, now));
    // in the same transaction, so a report never exists without its history
    steps.push(db.prepare("INSERT INTO events (report_id, actor, what, at) VALUES (last_insert_rowid(), ?, 'opened', ?)").bind(user.id, now));
    try {
      await db.batch(steps);
      return json(201, { message: 'Thanks for your feedback.', report: number, user: user.id });
    } catch {
      // a racing send took the number or the text: the UNIQUE constraints
      // refused the whole batch, so nothing half-written remains
      if (await dup()) return message(409, "You've already sent this.", { user: user.id });
    }
  }
  return message(503, 'Busy. Try again in a minute.');
}

/** A machine's own reports in one app, with their status, for the app's own list. */
export async function mine(req, env) {
  const r = await readJson(req, 2000);
  if (r.error) return message(400, r.error);
  const v = r.value?.verifier, app = r.value?.app ?? 'isola';
  if (typeof v !== 'string' || !VERIFIER.test(v)) return message(400, 'Bad verifier.');
  if (!appsOf(env).includes(app)) return message(400, 'Unknown app.');
  const user = await env.DB.prepare('SELECT id FROM users WHERE verifier = ?').bind(v).first();
  if (!user) return json(200, { user: null, reports: [] });
  const { results } = await env.DB.prepare(
    'SELECT number, kind, subject, status, created_at, closed_at FROM reports WHERE app = ? AND user_id = ? ORDER BY created_at DESC LIMIT 20').bind(app, user.id).all();
  return json(200, { user: user.id, reports: results });
}
