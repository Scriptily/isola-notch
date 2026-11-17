// node --test feedback/test/
import test from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync, createSign } from 'node:crypto';
import { handle } from '../src/worker.js';
import { fingerprint } from '../src/util.js';
import { LIMITS } from '../src/intake.js';
import { resetAccessCache } from '../src/access.js';
import { d1 } from './d1.mjs';

const T0 = Date.UTC(2026, 10, 17, 12);
const HOUR = 3_600_000;
const V1 = 'a'.repeat(32), V2 = 'b'.repeat(32), V3 = 'c'.repeat(32);

function env(extra = {}) {
  return { DB: d1(), ASSETS: { fetch: async () => new Response('<!doctype html>dashboard', { headers: { 'content-type': 'text/html' } }) }, ...extra };
}
const send = (e, body, { ip = '1.2.3.4', now = T0 } = {}) =>
  handle(new Request('https://isola-feedback.example.workers.dev/feedback', { method: 'POST', body: JSON.stringify(body), headers: { 'cf-connecting-ip': ip } }), e, { now });
const report = (o = {}) => ({ user: 'User4815162342', verifier: V1, kind: 'bug', subject: 'Glow flickers', text: 'The glow flickers on my second screen.', version: '1.4.0', windows: '10.0.26300', source: 'github', ...o });
const body = async r => r.json();

// ---- intake ---------------------------------------------------------------------

test('a report gets the next number, the PC keeps its ID, and the reply says both', async () => {
  const e = env();
  const r = await send(e, report());
  assert.equal(r.status, 201);
  const b = await body(r);
  assert.equal(b.report, 1);
  assert.equal(b.user, 'User4815162342');
  const row = e.DB.raw.prepare('SELECT status, kind, subject FROM reports WHERE id = 1').get();
  assert.deepEqual({ ...row }, { status: 'open', kind: 'bug', subject: 'Glow flickers' });
  assert.equal(e.DB.raw.prepare("SELECT what FROM events WHERE report_id = 1").get().what, 'opened');
});

test('report numbers are never reused, even after the newest report is deleted', async () => {
  const e = env();
  await send(e, report());
  await send(e, report({ user: 'User2718281828', verifier: V2, subject: 'Second', text: 'A second report entirely.' }), { ip: '5.5.5.5' });
  e.DB.raw.exec('DELETE FROM events WHERE report_id = 2; DELETE FROM reports WHERE id = 2');
  const b = await body(await send(e, report({ user: 'User1414213562', verifier: V3, subject: 'Third', text: 'A third report entirely.' }), { ip: '6.6.6.6' }));
  assert.equal(b.report, 3, 'not 2 again');
});

test('two PCs that derive the same ID get different IDs, and each keeps its own for good', async () => {
  const e = env();
  const a = await body(await send(e, report()));
  const b = await body(await send(e, report({ verifier: V2, subject: 'Other PC', text: 'From a different machine.' }), { ip: '7.7.7.7' }));
  assert.equal(a.user, 'User4815162342');
  assert.equal(b.user, 'User4815162342-2', 'the second machine is never given the first one\'s ID');
  const again = await body(await send(e, report({ verifier: V2, subject: 'Later', text: 'The same second machine, a day later.' }), { ip: '7.7.7.7', now: T0 + 25 * HOUR }));
  assert.equal(again.user, 'User4815162342-2', 'and keeps the one it was given');
});

test('one report a day per PC; the same words twice never, from anyone', async () => {
  const e = env();
  await send(e, report());
  const soon = await send(e, report({ subject: 'Other', text: 'Something else to report here.' }), { now: T0 + 23 * HOUR });
  assert.equal(soon.status, 429);
  const dup = await send(e, report({ user: 'User9999999999', verifier: V2, subject: 'glow FLICKERS!', text: 'the  glow flickers on my second screen' }), { ip: '8.8.8.8' });
  assert.equal(dup.status, 409);
  const later = await send(e, report({ subject: 'Other', text: 'Something else to report here.' }), { now: T0 + 25 * HOUR });
  assert.equal(later.status, 201, 'a day later it goes through');
});

test('made-up IDs still hit the per-address and whole-service ceilings', async () => {
  const e = env();
  for (let i = 0; i < LIMITS.perIpPerDay; i++)
    assert.equal((await send(e, report({ user: `User10000000${i}`, verifier: String(i).repeat(32).slice(0, 32).replace(/[^0-9a-f]/g, 'f'), subject: `S${i}`, text: `Distinct text number ${i} here.` }))).status, 201);
  assert.equal((await send(e, report({ user: 'User1999999', verifier: 'd'.repeat(32), subject: 'More', text: 'One more from the same address.' }))).status, 429);
});

test('malformed reports are refused and nothing is stored', async () => {
  const e = env();
  for (const b of [report({ user: 'User12' }), report({ user: 'User0123456' }), report({ verifier: 'xyz' }), report({ kind: 'spam' }), report({ subject: ' ' }), report({ text: 'short' }), report({ logs: 'x'.repeat(60_001) })])
    assert.equal((await send(e, b)).status, 400, JSON.stringify(b).slice(0, 50));
  assert.equal(e.DB.raw.prepare('SELECT COUNT(*) AS n FROM reports').get().n, 0);
  assert.equal(e.DB.raw.prepare('SELECT COUNT(*) AS n FROM users').get().n, 0, 'no ID is claimed by a refused report');
});

test('a PC can list its own reports with their status, and only its own', async () => {
  const e = env();
  await send(e, report());
  e.DB.raw.exec("UPDATE reports SET status = 'closed' WHERE id = 1");
  const mine = r => handle(new Request('https://x.workers.dev/feedback/mine', { method: 'POST', body: JSON.stringify({ verifier: r }) }), e, { now: T0 });
  const a = await body(await mine(V1));
  assert.equal(a.reports.length, 1);
  assert.equal(a.reports[0].status, 'closed');
  assert.ok(!('body' in a.reports[0]) && !('logs' in a.reports[0]), 'titles and status only');
  assert.deepEqual((await body(await mine(V2))).reports, [], 'another PC sees nothing');
});

test('fingerprints match the app exactly', async () => {
  assert.equal(await fingerprint('Glow flickers', 'The glow flickers, on my 2nd screen!'), '8ca0f056b8b880f2a301253119330f994ef493c90ae3f58622ef626d46bd5426');
});

// ---- the dashboard: only through Access -------------------------------------------

const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'k1', alg: 'RS256' };
const TEAM = 'team.cloudflareaccess.com', AUD = 'aud-tag';
function jwt(payload, key = privateKey) {
  const enc = o => Buffer.from(JSON.stringify(o)).toString('base64url');
  const head = enc({ alg: 'RS256', kid: 'k1' }), body = enc(payload);
  const s = createSign('RSA-SHA256'); s.update(`${head}.${body}`);
  return `${head}.${body}.${s.sign(key).toString('base64url')}`;
}
const certs = async url => (url === `https://${TEAM}/cdn-cgi/access/certs` ? new Response(JSON.stringify({ keys: [jwk] })) : new Response('', { status: 404 }));
const dashEnv = () => env({ ACCESS_TEAM_DOMAIN: TEAM, ACCESS_AUD: AUD, DASHBOARD_HOST: 'feedback.example.com' });
const good = () => jwt({ aud: [AUD], iss: `https://${TEAM}`, exp: T0 / 1000 + 3600, email: 'owner@example.com' });
const api = (e, path, { token = good(), method = 'GET', data, host = 'feedback.example.com' } = {}) =>
  handle(new Request(`https://${host}${path}`, { method, body: data ? JSON.stringify(data) : undefined, headers: token ? { 'cf-access-jwt-assertion': token } : {} }), e, { now: T0, fetchImpl: certs });

test('the dashboard and its API need a valid Access sign-in, on the dashboard host only', async () => {
  resetAccessCache();
  const e = dashEnv();
  await send(e, report());
  assert.equal((await api(e, '/api/reports')).status, 200);
  assert.equal((await api(e, '/')).status, 200);
  assert.equal((await api(e, '/api/reports', { token: null })).status, 401, 'no sign-in');
  const { privateKey: other } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  assert.equal((await api(e, '/api/reports', { token: jwt({ aud: [AUD], iss: `https://${TEAM}`, exp: T0 / 1000 + 3600, email: 'x' }, other) })).status, 401, 'forged signature');
  assert.equal((await api(e, '/api/reports', { token: jwt({ aud: ['other'], iss: `https://${TEAM}`, exp: T0 / 1000 + 3600 }) })).status, 401, 'another app\'s token');
  assert.equal((await api(e, '/api/reports', { token: jwt({ aud: [AUD], iss: `https://${TEAM}`, exp: T0 / 1000 - 1 }) })).status, 401, 'expired');
  assert.equal((await api(e, '/api/reports', { host: 'isola-feedback.example.workers.dev' })).status, 404, 'the app\'s public address serves no dashboard');
  assert.equal((await api(e, '/', { host: 'isola-feedback.example.workers.dev' })).status, 404);
});

test('list, filter, search and sort; details hide internal bookkeeping', async () => {
  resetAccessCache();
  const e = dashEnv();
  await send(e, report());
  await send(e, report({ user: 'User2222222', verifier: V2, kind: 'feature', subject: 'Dark tint option', text: 'A darker tint for settings, please.', logs: 'INF line' }), { ip: '2.2.2.2', now: T0 + 1000 });
  await send(e, report({ user: 'User3333333', verifier: V3, subject: 'Crash on shelf', text: 'It crashed when I opened the shelf.' }), { ip: '3.3.3.3', now: T0 + 2000 });
  const get = async p => body(await api(e, p));
  assert.deepEqual((await get('/api/reports')).items.map(r => r.id), [3, 2, 1], 'newest first, open by default');
  assert.deepEqual((await get('/api/reports?sort=old')).items.map(r => r.id), [1, 2, 3]);
  assert.deepEqual((await get('/api/reports?kind=feature')).items.map(r => r.id), [2]);
  assert.deepEqual((await get('/api/reports?logs=1')).items.map(r => r.id), [2]);
  assert.deepEqual((await get('/api/reports?q=shelf')).items.map(r => r.id), [3], 'words in the description');
  assert.deepEqual((await get('/api/reports?q=%232')).items.map(r => r.id), [2], '#2 finds report 2');
  assert.deepEqual((await get('/api/reports?user=User2222222')).items.map(r => r.id), [2]);
  const d = await get('/api/reports/2');
  assert.equal(d.logs, 'INF line');
  assert.equal(d.user_reports, 1);
  assert.ok(!('fingerprint' in d) && !('ip_hash' in d), 'no fingerprint or address hash in what the dashboard gets');
  assert.equal((await api(e, '/api/reports/99')).status, 404);
});

test('closing, reopening, tags and notes; each lands in the history, and stats follow', async () => {
  resetAccessCache();
  const e = dashEnv();
  await send(e, report());
  let d = await body(await api(e, '/api/reports/1', { method: 'PATCH', data: { status: 'closed', addTags: ['duplicate'] } }));
  assert.equal(d.status, 'closed');
  assert.ok(d.closed_at);
  assert.deepEqual(d.tags, ['duplicate']);
  assert.deepEqual((await body(await api(e, '/api/reports'))).items, [], 'closed reports leave the open list');
  assert.equal((await body(await api(e, '/api/reports?status=closed'))).total, 1);
  d = await body(await api(e, '/api/reports/1/notes', { method: 'POST', data: { body: 'Same as #0, fixed in 1.4.0.' } }));
  assert.equal(d.notes[0].author, 'owner@example.com');
  d = await body(await api(e, '/api/reports/1', { method: 'PATCH', data: { status: 'open', removeTags: ['duplicate'] } }));
  assert.equal(d.status, 'open');
  assert.equal(d.closed_at, null);
  assert.deepEqual(d.events.map(x => x.what), ['opened', 'closed', 'tagged duplicate', 'reopened', 'untagged duplicate']);
  assert.equal((await api(e, '/api/reports/1', { method: 'PATCH', data: { addTags: ['<script>'] } })).status, 400, 'tags are plain words');
  const s = await body(await api(e, '/api/stats'));
  assert.equal(s.open, 1); assert.equal(s.bugs, 1); assert.equal(s.users, 1);
  const csv = await (await api(e, '/api/export?format=csv')).text();
  assert.match(csv.split('\r\n')[0], /^id,app,status,kind,user_id,subject/);
});

test('bulk close, and pages served with no framing or referrer', async () => {
  resetAccessCache();
  const e = dashEnv();
  await send(e, report());
  await send(e, report({ user: 'User2222222', verifier: V2, subject: 'Another', text: 'Another report to close.' }), { ip: '2.2.2.2' });
  assert.equal((await api(e, '/api/bulk', { method: 'POST', data: { ids: [1, 2], status: 'closed' } })).status, 200);
  assert.equal((await body(await api(e, '/api/stats'))).closed, 2);
  const page = await api(e, '/');
  assert.equal(page.headers.get('x-frame-options'), 'DENY');
  assert.match(page.headers.get('content-security-policy'), /frame-ancestors 'none'/);
});

// ---- one hub, several apps --------------------------------------------------------------

test('several apps share the hub without sharing limits, IDs or duplicates; unknown apps are refused', async () => {
  const e = env({ APPS: 'isola,notes-app' });
  assert.equal((await send(e, report({ app: 'isola' }))).status, 201);
  const other = await send(e, report({ app: 'notes-app' }), { ip: '9.9.9.9' });
  assert.equal(other.status, 201, 'the same PC, the same words, another app: a separate report');
  assert.equal((await body(other)).user, 'User4815162342', 'IDs are per app');
  assert.equal((await send(e, report({ app: 'stranger', subject: 'x y', text: 'From an app the hub does not know.' }), { ip: '4.4.4.4' })).status, 400);
  assert.equal((await send(env(), report({ app: undefined }))).status, 201, 'a report without an app counts as Isola');
  resetAccessCache();
  const d = { ...e, ACCESS_TEAM_DOMAIN: TEAM, ACCESS_AUD: AUD, DASHBOARD_HOST: 'feedback.example.com' };
  assert.deepEqual((await body(await api(d, '/api/reports?app=notes-app'))).items.map(r => r.app), ['notes-app']);
  const s = await body(await api(d, '/api/stats'));
  assert.deepEqual(s.apps.map(a => [a.app, a.n]), [['isola', 1], ['notes-app', 1]]);
  assert.equal((await body(await api(d, '/api/stats?app=isola'))).total, 1, 'stats for one app');
});
