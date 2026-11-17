// node --test tools/feedback-relay/worker.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { handle, fingerprint, issue, LIMITS } from './worker.js';

function kv() {
  const m = new Map();
  return { m, get: async k => m.get(k) ?? null, put: async (k, v) => { m.set(k, v); } };
}
function env() { return { FEEDBACK: kv(), FEEDBACK_REPO: 'owner/private-feedback', GITHUB_TOKEN: 'secret' }; }
function gh() {
  const calls = [];
  let n = 0;
  const f = async (url, init) => { calls.push({ url, body: JSON.parse(init.body), auth: init.headers.authorization }); return new Response(JSON.stringify({ number: ++n }), { status: 201 }); };
  return { calls, f };
}
const post = (b, ip = '1.2.3.4') => new Request('https://relay.example/feedback', { method: 'POST', body: JSON.stringify(b), headers: { 'cf-connecting-ip': ip } });
const fb = (o = {}) => ({ user: 'User123456789', kind: 'bug', subject: 'Glow flickers', text: 'The glow flickers on my second screen.', version: '1.3.0', windows: '10.0.26300', source: 'github', ...o });

test('files an issue in the private repo with the token, then holds the user back for a day', async () => {
  const e = env(), g = gh();
  const r = await handle(post(fb()), e, g.f);
  assert.equal(r.status, 201);
  assert.equal(g.calls.length, 1);
  assert.equal(g.calls[0].url, 'https://api.github.com/repos/owner/private-feedback/issues');
  assert.equal(g.calls[0].auth, 'Bearer secret');
  assert.equal(g.calls[0].body.title, '[Bug] Glow flickers');
  assert.deepEqual(g.calls[0].body.labels, ['bug', 'feedback']);
  const again = await handle(post(fb({ subject: 'Other', text: 'A different problem entirely.' })), e, g.f);
  assert.equal(again.status, 429);
  assert.equal(g.calls.length, 1, 'nothing filed');
  assert.equal(e.FEEDBACK.m.get('u:User123456789'), '1');
});

test('the same feedback twice is refused, from any user, however it is written', async () => {
  const e = env(), g = gh();
  await handle(post(fb()), e, g.f);
  const dup = await handle(post(fb({ user: 'User987654321', subject: 'glow FLICKERS!!', text: 'the glow   flickers on my second screen' }), '5.6.7.8'), e, g.f);
  assert.equal(dup.status, 409);
  assert.equal(g.calls.length, 1);
});

test('made-up user numbers still hit the per-address and whole-service ceilings', async () => {
  const e = env(), g = gh();
  for (let i = 0; i < LIMITS.perIpPerDay; i++)
    assert.equal((await handle(post(fb({ user: `User10000000${i}`, subject: `S${i}`, text: `Distinct text number ${i} here.` })), e, g.f)).status, 201);
  const over = await handle(post(fb({ user: 'User199999999', subject: 'More', text: 'One more from the same address.' })), e, g.f);
  assert.equal(over.status, 429);
  e.FEEDBACK.m.set(`all:${new Date().toISOString().slice(0, 10)}`, String(LIMITS.allPerDay));
  assert.equal((await handle(post(fb({ user: 'User188888888', subject: 'X', text: 'From a fresh address.' }), '9.9.9.9'), e, g.f)).status, 429);
});

test('malformed or oversized feedback is rejected before GitHub is touched', async () => {
  const e = env(), g = gh();
  for (const b of [fb({ user: 'User0123' }), fb({ kind: 'spam' }), fb({ subject: '' }), fb({ text: 'short' }), fb({ text: 'x'.repeat(4001) }), fb({ logs: 'x'.repeat(60001) })])
    assert.equal((await handle(post(b), e, g.f)).status, 400, JSON.stringify(b).slice(0, 60));
  assert.equal((await handle(new Request('https://relay.example/feedback', { method: 'GET' }), e, g.f)).status, 404);
  assert.equal(g.calls.length, 0);
});

test('logs go in a comment, fenced so they cannot break out; mentions never ping anyone', async () => {
  const e = env(), g = gh();
  await handle(post(fb({ text: 'Ping @someone about this crash.', logs: 'INF line\n~~~~~\nescape attempt' })), e, g.f);
  assert.equal(g.calls.length, 2);
  assert.ok(!g.calls[0].body.body.includes('@someone'));
  assert.match(g.calls[1].url, /\/issues\/1\/comments$/);
  const fenced = g.calls[1].body.body;
  assert.equal(fenced.split('~~~~~').length - 1, 2, 'only the two fences');
});

test('a GitHub failure files nothing and uses up no limit', async () => {
  const e = env();
  const r = await handle(post(fb()), e, async () => new Response('{}', { status: 500 }));
  assert.equal(r.status, 502);
  assert.equal(e.FEEDBACK.m.size, 0);
});

test('fingerprints match the app: same words, any case, spacing or punctuation', async () => {
  assert.equal(await fingerprint('Glow flickers', 'The glow flickers.'), await fingerprint('glow, FLICKERS', ' the  glow flickers '));
  assert.notEqual(await fingerprint('Glow flickers', 'a'), await fingerprint('Glow flickers', 'b'));
  assert.equal(issue(fb({ kind: 'feature' })).labels[0], 'enhancement');
  // the app's Feedback.Fingerprint gives this exact value (tests/Scenarios9.cs)
  assert.equal(await fingerprint('Glow flickers', 'The glow flickers, on my 2nd screen!'), '8ca0f056b8b880f2a301253119330f994ef493c90ae3f58622ef626d46bd5426');
});
