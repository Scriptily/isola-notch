// node --test feedback/test/service.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync, createSign } from 'node:crypto';
import { handle } from '../src/worker.js';
import { fingerprint } from '../src/util.js';
import { LIMITS, alternateId } from '../src/intake.js';
import { resetAccessCache } from '../src/access.js';
import { d1 } from './d1.mjs';

const T0 = Date.UTC(2026, 10, 17, 12);
const HOUR = 3_600_000;
const V1 = 'a'.repeat(32), V2 = 'b'.repeat(32), V3 = 'c'.repeat(32);
const ID1 = '48151623424815162342', ID2 = '27182818284590452353', ID3 = '14142135623730950488';

function env(extra = {}) {
  return { DB: d1(), APPS: 'isola,notes-app', ASSETS: { fetch: async r => new URL(r.url).pathname === '/index.html' || new URL(r.url).pathname === '/' ? new Response('<!doctype html>dashboard', { headers: { 'content-type': 'text/html' } }) : new Response('nope', { status: 404 }) }, ...extra };
}
// a random source that gives these values first, then real randomness
const seq = (...v) => () => (v.length ? v.shift() : Math.random());
const send = (e, body, { ip = '1.2.3.4', now = T0, random } = {}) =>
  handle(new Request('https://feedback-hub.example.workers.dev/feedback', { method: 'POST', body: JSON.stringify(body), headers: { 'cf-connecting-ip': ip } }), e, { now, random });
const report = (o = {}) => ({ app: 'isola', user: ID1, verifier: V1, kind: 'bug', subject: 'Glow flickers', text: 'The glow flickers on my second screen.', version: '1.4.0', windows: '10.0.26300', source: 'github', tz: '+03:00', ...o });
const body = async r => r.json();
const rows = (e, sql) => e.DB.raw.prepare(sql).all();

// ---- report numbers ------------------------------------------------------------------

test('a report gets a random number of at least 6 digits, not a count; the reply names it and the machine', async () => {
  const e = env();
  const b = await body(await send(e, report(), { random: seq(0.5) }));
  assert.equal(b.report, 550000, '100000 + 0.5 x 900000');
  assert.equal(b.user, ID1);
  const b2 = await body(await send(e, report({ user: ID2, verifier: V2, subject: 'Second', text: 'A second report entirely.' }), { ip: '5.5.5.5', random: seq(0.01) }));
  assert.equal(b2.report, 109000, 'the next one is not 550001');
  assert.equal(rows(e, "SELECT what FROM events")[0].what, 'opened');
});

test('a number is never handed out twice in an app, even after its report is deleted', async () => {
  const e = env();
  await send(e, report(), { random: seq(0.5) });
  e.DB.raw.exec("DELETE FROM events; DELETE FROM reports WHERE number = 550000");
  // the random source offers 550000 again: it's skipped
  const b = await body(await send(e, report({ user: ID2, verifier: V2, subject: 'Another', text: 'Another report entirely.' }), { ip: '5.5.5.5', random: seq(0.5, 0.25) }));
  assert.equal(b.report, 325000);
  // another app may use the same number: numbers are per app
  const c = await body(await send(e, report({ app: 'notes-app' }), { ip: '6.6.6.6', random: seq(0.5) }));
  assert.equal(c.report, 550000);
});

test('numbers grow a digit as an app fills up, keeping 100 times more free than used', async () => {
  const e = env();
  const ins = e.DB.raw.prepare("INSERT INTO issued (app, number) VALUES ('isola', ?)");
  for (let i = 0; i < 10_000; i++) ins.run(1_000_000 + i);   // 10,000 used: 7 digits
  const b = await body(await send(e, report(), { random: seq(0.5) }));
  assert.equal(String(b.report).length, 7);
});

// ---- user IDs --------------------------------------------------------------------------

test('a machine has the same 20-digit ID in every app', async () => {
  const e = env();
  const a = await body(await send(e, report({ app: 'isola' })));
  const b = await body(await send(e, report({ app: 'notes-app' }), { ip: '9.9.9.9' }));
  assert.equal(a.user, ID1);
  assert.equal(b.user, ID1);
  assert.equal(rows(e, 'SELECT COUNT(*) AS n FROM users')[0].n, 1);
});

test('two machines that derive the same ID get different 20-digit IDs, each kept for good', async () => {
  const e = env();
  const a = await body(await send(e, report()));
  const b = await body(await send(e, report({ verifier: V2, subject: 'Other PC', text: 'From a different machine.' }), { ip: '7.7.7.7' }));
  assert.equal(a.user, ID1);
  assert.notEqual(b.user, ID1, 'never given the first machine\'s ID');
  assert.match(b.user, /^[1-9]\d{19}$/, 'still exactly 20 digits');
  assert.equal(b.user, await alternateId(ID1, 1));
  const again = await body(await send(e, report({ app: 'notes-app', verifier: V2, subject: 'Later', text: 'The same second machine, in another app.' }), { ip: '7.7.7.7' }));
  assert.equal(again.user, b.user, 'and keeps it, in every app');
});

test('alternate IDs are always 20 digits', async () => {
  for (let k = 1; k < 300; k++) assert.match(await alternateId(ID1, k), /^[1-9]\d{19}$/);
});

// ---- limits and validation --------------------------------------------------------------

test('one report a day per machine per app; the same words twice never, in an app', async () => {
  const e = env();
  await send(e, report());
  assert.equal((await send(e, report({ subject: 'Other', text: 'Something else to report here.' }), { now: T0 + 23 * HOUR })).status, 429);
  assert.equal((await send(e, report({ user: ID2, verifier: V2, subject: 'glow FLICKERS!', text: 'the  glow flickers on my second screen' }), { ip: '8.8.8.8' })).status, 409);
  assert.equal((await send(e, report({ subject: 'Other', text: 'Something else to report here.' }), { now: T0 + 25 * HOUR })).status, 201);
});

test('made-up IDs still hit the per-address and per-app ceilings', async () => {
  const e = env();
  for (let i = 0; i < LIMITS.perIpPerDay; i++)
    assert.equal((await send(e, report({ user: `1000000000000000000${i}`, verifier: String(i).repeat(32).slice(0, 32), subject: `S${i}`, text: `Distinct text number ${i} here.` }))).status, 201);
  assert.equal((await send(e, report({ user: '19999999999999999999', verifier: 'd'.repeat(32), subject: 'More', text: 'One more from the same address.' }))).status, 429);
});

test('malformed reports are refused and nothing is stored, not even an ID', async () => {
  const e = env();
  for (const b of [report({ user: '4815162342481516234' }), report({ user: '04815162342481516234' }), report({ user: 'User48151623424815162342' }), report({ verifier: 'xyz' }),
                   report({ app: 'stranger' }), report({ kind: 'spam' }), report({ subject: ' ' }), report({ text: 'short' }), report({ tz: '3' }), report({ logs: 'x'.repeat(60_001) })])
    assert.equal((await send(e, b)).status, 400, JSON.stringify(b).slice(0, 60));
  assert.equal(rows(e, 'SELECT COUNT(*) AS n FROM reports')[0].n, 0);
  assert.equal(rows(e, 'SELECT COUNT(*) AS n FROM users')[0].n, 0);
});

test('a machine can list its own reports in an app, by number, titles and status only', async () => {
  const e = env();
  const { report: n } = await body(await send(e, report()));
  e.DB.raw.exec("UPDATE reports SET status = 'closed'");
  const mine = (v, app = 'isola') => handle(new Request('https://x.workers.dev/feedback/mine', { method: 'POST', body: JSON.stringify({ verifier: v, app }) }), e, { now: T0 });
  const a = await body(await mine(V1));
  assert.deepEqual(a.reports.map(r => [r.number, r.status]), [[n, 'closed']]);
  assert.ok(!('body' in a.reports[0]) && !('logs' in a.reports[0]));
  assert.deepEqual((await body(await mine(V2))).reports, []);
  assert.deepEqual((await body(await mine(V1, 'notes-app'))).reports, [], 'per app');
});

test('fingerprints match the app exactly', async () => {
  assert.equal(await fingerprint('Glow flickers', 'The glow flickers, on my 2nd screen!'), '8ca0f056b8b880f2a301253119330f994ef493c90ae3f58622ef626d46bd5426');
});

// ---- the dashboard: only through Access, only on its hostnames ---------------------------

const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'k1', alg: 'RS256' };
const TEAM = 'team.cloudflareaccess.com', AUD = 'aud-tag';
function jwt(payload, key = privateKey) {
  const enc = o => Buffer.from(JSON.stringify(o)).toString('base64url');
  const head = enc({ alg: 'RS256', kid: 'k1' }), b = enc(payload);
  const s = createSign('RSA-SHA256'); s.update(`${head}.${b}`);
  return `${head}.${b}.${s.sign(key).toString('base64url')}`;
}
const certs = async url => (url === `https://${TEAM}/cdn-cgi/access/certs` ? new Response(JSON.stringify({ keys: [jwk] })) : new Response('', { status: 404 }));
const HOSTS = JSON.stringify({ 'dash.example.com': '', 'isola.example.com': 'isola' });
const dashEnv = () => env({ ACCESS_TEAM_DOMAIN: TEAM, ACCESS_AUD: AUD, HOSTS });
const good = () => jwt({ aud: [AUD], iss: `https://${TEAM}`, exp: T0 / 1000 + 3600, email: 'owner@example.com' });
const api = (e, path, { token = good(), method = 'GET', data, host = 'dash.example.com' } = {}) =>
  handle(new Request(`https://${host}${path}`, { method, body: data ? JSON.stringify(data) : undefined, headers: token ? { 'cf-access-jwt-assertion': token } : {} }), e, { now: T0, fetchImpl: certs });

test('the dashboard needs a valid Access sign-in, on its own hostnames only', async () => {
  resetAccessCache();
  const e = dashEnv();
  await send(e, report());
  assert.equal((await api(e, '/api/reports')).status, 200);
  assert.equal((await api(e, '/api/reports', { token: null })).status, 401, 'no sign-in');
  const { privateKey: other } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  assert.equal((await api(e, '/api/reports', { token: jwt({ aud: [AUD], iss: `https://${TEAM}`, exp: T0 / 1000 + 3600 }, other) })).status, 401, 'forged signature');
  assert.equal((await api(e, '/api/reports', { token: jwt({ aud: ['other'], iss: `https://${TEAM}`, exp: T0 / 1000 + 3600 }) })).status, 401, 'another app\'s token');
  assert.equal((await api(e, '/api/reports', { token: jwt({ aud: [AUD], iss: `https://${TEAM}`, exp: T0 / 1000 - 1 }) })).status, 401, 'expired');
  assert.equal((await api(e, '/', { host: 'feedback-hub.example.workers.dev' })).status, 404, 'the apps\' public address serves no dashboard');
  assert.equal((await api(e, '/api/reports', { host: 'elsewhere.example.com' })).status, 404);
});

test('each hostname opens its view, and every path is a page of the dashboard', async () => {
  resetAccessCache();
  const e = dashEnv();
  assert.deepEqual(await body(await api(e, '/api/me')), { who: 'owner@example.com', hostApp: '', apps: ['isola', 'notes-app'] });
  assert.equal((await body(await api(e, '/api/me', { host: 'isola.example.com' }))).hostApp, 'isola', 'the app\'s own hostname opens that app');
  for (const [host, path] of [['dash.example.com', '/isola/feedback/482913'], ['isola.example.com', '/feedback/482913'], ['dash.example.com', '/agents']]) {
    const page = await api(e, path, { host });
    assert.equal(page.status, 200, path);
    assert.match(await page.text(), /dashboard/);
    assert.equal(page.headers.get('x-frame-options'), 'DENY');
  }
  assert.equal((await api(e, '/missing.css')).status, 404, 'a missing file is still missing');
});

test('list, search by #number, @person and words; details by app and number, with the sender\'s time zone', async () => {
  resetAccessCache();
  const e = dashEnv();
  const one = (await body(await send(e, report(), { random: seq(0.1) }))).report;
  const two = (await body(await send(e, report({ user: ID2, verifier: V2, kind: 'feature', subject: 'Dark tint option', text: 'A darker tint for settings, please.', logs: 'INF line' }), { ip: '2.2.2.2', now: T0 + 1000, random: seq(0.2) }))).report;
  await send(e, report({ app: 'notes-app', user: ID3, verifier: V3, subject: 'Crash on save', text: 'It crashed when I saved a note.' }), { ip: '3.3.3.3', now: T0 + 2000, random: seq(0.3) });
  const get = async p => body(await api(e, p));
  assert.deepEqual((await get('/api/reports?app=isola')).items.map(r => r.number), [two, one]);
  assert.deepEqual((await get('/api/reports?status=all')).items.map(r => r.app), ['notes-app', 'isola', 'isola']);
  assert.deepEqual((await get(`/api/reports?app=isola&q=%23${one}`)).items.map(r => r.number), [one]);
  assert.deepEqual((await get(`/api/reports?q=%40${ID2}`)).items.map(r => r.number), [two], '@ and 20 digits finds a person');
  assert.deepEqual((await get('/api/reports?q=tint')).items.map(r => r.number), [two]);
  const d = await get(`/api/reports/isola/${two}`);
  assert.equal(d.number, two); assert.equal(d.app, 'isola'); assert.equal(d.logs, 'INF line'); assert.equal(d.tz, '+03:00');
  assert.ok(!('id' in d) && !('fingerprint' in d) && !('ip_hash' in d), 'no internal fields');
  assert.ok(!('id' in (await get('/api/reports')).items[0]), 'nor in the list');
  assert.equal((await api(e, `/api/reports/notes-app/${two}`)).status, 404, 'a number belongs to its app');
});

test('a person\'s reports are counted in the app and across all apps', async () => {
  resetAccessCache();
  const e = dashEnv();
  const n = (await body(await send(e, report()))).report;
  await send(e, report({ app: 'notes-app' }), { ip: '9.9.9.9' });
  const d = await body(await api(e, `/api/reports/isola/${n}`));
  assert.equal(d.user_reports, 1);
  assert.equal(d.user_reports_all, 2);
});

test('closing, reopening, tags and notes land in the history; stats, bulk and export follow', async () => {
  resetAccessCache();
  const e = dashEnv();
  const n = (await body(await send(e, report()))).report;
  const m = (await body(await send(e, report({ user: ID2, verifier: V2, subject: 'Another', text: 'Another report to close.' }), { ip: '2.2.2.2' }))).report;
  let d = await body(await api(e, `/api/reports/isola/${n}`, { method: 'PATCH', data: { status: 'closed', addTags: ['duplicate'] } }));
  assert.equal(d.status, 'closed'); assert.ok(d.closed_at); assert.deepEqual(d.tags, ['duplicate']);
  d = await body(await api(e, `/api/reports/isola/${n}/notes`, { method: 'POST', data: { body: `Same as #${m}.` } }));
  assert.equal(d.notes[0].author, 'owner@example.com');
  d = await body(await api(e, `/api/reports/isola/${n}`, { method: 'PATCH', data: { status: 'open', removeTags: ['duplicate'] } }));
  assert.deepEqual(d.events.map(x => x.what), ['opened', 'closed', 'tagged duplicate', 'reopened', 'untagged duplicate']);
  assert.equal((await api(e, `/api/reports/isola/${n}`, { method: 'PATCH', data: { addTags: ['<script>'] } })).status, 400);
  assert.equal((await api(e, '/api/bulk', { method: 'POST', data: { items: [{ app: 'isola', number: n }, { app: 'isola', number: m }], status: 'closed' } })).status, 200);
  assert.equal((await body(await api(e, '/api/stats?app=isola'))).closed, 2);
  assert.match((await (await api(e, '/api/export?format=csv')).text()).split('\r\n')[0], /^app,number,status,kind,user_id,subject/);
});

test('the home summary covers every app, with counts and the newest reports', async () => {
  resetAccessCache();
  const e = dashEnv();
  await send(e, report());
  await send(e, report({ user: ID2, verifier: V2, kind: 'feature', subject: 'Idea', text: 'A feature idea for later.' }), { ip: '2.2.2.2', now: T0 + 5000 });
  const s = await body(await api(e, '/api/summary'));
  assert.deepEqual(s.apps.map(a => [a.app, a.open, a.bugs, a.features, a.recent.length]), [['isola', 2, 1, 1, 2], ['notes-app', 0, 0, 0, 0]]);
  assert.equal(s.apps[0].recent[0].subject, 'Idea', 'newest first');
  assert.equal(s.people, 2);
});
