// Isola's feedback relay: a Cloudflare Worker between the app and a private
// GitHub repository. The app is open source, so it can't hold a credential:
// anything it holds, anyone could use to read the feedback or post around the
// limits. The GitHub token lives only here, as a Worker secret, and every
// limit is enforced here, whatever a modified app claims.
//
// POST /feedback  {user, kind, subject, text, version, windows, source, logs?}
//   201 filed  400 malformed  409 already sent  429 limit reached  502 GitHub failed
//
// Bindings (wrangler.toml): KV namespace FEEDBACK; vars FEEDBACK_REPO
// ("owner/repo", private); secret GITHUB_TOKEN (fine-grained, Issues
// read/write on that repository only).

export const LIMITS = { perUserSeconds: 86400, perIpPerDay: 3, allPerDay: 100, subjectMax: 120, textMin: 10, textMax: 4000, logsMax: 60000 };

export function validate(b) {
  if (!b || typeof b !== 'object') return 'Expected a JSON object.';
  if (typeof b.user !== 'string' || !/^User[1-9]\d{8}$/.test(b.user)) return 'Bad user.';
  if (b.kind !== 'bug' && b.kind !== 'feature') return 'Bad kind.';
  if (typeof b.subject !== 'string' || !b.subject.trim() || b.subject.length > LIMITS.subjectMax) return 'Bad subject.';
  if (typeof b.text !== 'string' || b.text.trim().length < LIMITS.textMin || b.text.length > LIMITS.textMax) return 'Bad description.';
  for (const k of ['version', 'windows', 'source']) if (b[k] != null && (typeof b[k] !== 'string' || b[k].length > 40)) return 'Bad ' + k + '.';
  if (b.logs != null && (typeof b.logs !== 'string' || b.logs.length > LIMITS.logsMax)) return 'Logs too long.';
  return null;
}

// the same normalization as the app (Feedback.Fingerprint): case, spacing and punctuation don't make it new
export async function fingerprint(subject, text) {
  const norm = `${subject} ${text}`.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(norm));
  return [...new Uint8Array(hash)].map(x => x.toString(16).padStart(2, '0')).join('');
}

// nobody gets pinged from inside feedback, and nothing breaks out of the log block
const quiet = s => String(s ?? '').replace(/@/g, '@​');

export function issue(b) {
  const kind = b.kind === 'bug' ? 'Bug' : 'Feature';
  return {
    title: `[${kind}] ${quiet(b.subject.trim())}`,
    labels: [b.kind === 'bug' ? 'bug' : 'enhancement', 'feedback'],
    body: [
      quiet(b.text.trim()),
      '',
      '---',
      `**From** ${b.user} · **Isola** ${quiet(b.version) || '?'} (${quiet(b.source) || '?'}) · **Windows** ${quiet(b.windows) || '?'}`,
      b.logs ? '_Logs attached below._' : '_No logs attached._',
    ].join('\n'),
  };
}

const reply = (status, message) => new Response(JSON.stringify({ message }), { status, headers: { 'content-type': 'application/json' } });

async function github(env, path, payload, fetchImpl) {
  return fetchImpl(`https://api.github.com/repos/${env.FEEDBACK_REPO}${path}`, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${env.GITHUB_TOKEN}`,
      accept: 'application/vnd.github+json',
      'user-agent': 'isola-feedback-relay',
      'content-type': 'application/json',
    },
    body: JSON.stringify(payload),
  });
}

export async function handle(req, env, fetchImpl = fetch, now = Date.now()) {
  const url = new URL(req.url);
  if (req.method !== 'POST' || url.pathname !== '/feedback') return reply(404, 'Not found.');
  let b;
  try { b = JSON.parse(await req.text()); } catch { return reply(400, 'Expected JSON.'); }
  const bad = validate(b);
  if (bad) return reply(400, bad);

  const day = new Date(now).toISOString().slice(0, 10);
  const ip = req.headers.get('cf-connecting-ip') || 'unknown';
  const kv = env.FEEDBACK;
  // a made-up user number gets past the per-user limit, so the address and the
  // whole service have ceilings too
  const ipKey = `ip:${day}:${ip}`, allKey = `all:${day}`;
  const [userSeen, ipCount, allCount] = await Promise.all([kv.get(`u:${b.user}`), kv.get(ipKey), kv.get(allKey)]);
  if (userSeen) return reply(429, 'Thanks, you sent feedback in the last day. Try again tomorrow.');
  if (+ipCount >= LIMITS.perIpPerDay || +allCount >= LIMITS.allPerDay) return reply(429, 'Feedback is busy right now. Try again tomorrow.');
  const fp = await fingerprint(b.subject, b.text);
  if (await kv.get(`h:${fp}`)) return reply(409, "You've already sent this.");

  const made = await github(env, '/issues', issue(b), fetchImpl);
  if (!made.ok) return reply(502, "Couldn't file it just now. Try again later.");
  const { number } = await made.json();
  if (b.logs) {
    const fence = '~~~~~';
    await github(env, `/issues/${number}/comments`, { body: `Logs (scrubbed on the PC before sending):\n\n${fence}text\n${b.logs.replaceAll(fence, '~ ~ ~')}\n${fence}` }, fetchImpl);
  }
  await Promise.all([
    kv.put(`u:${b.user}`, String(number), { expirationTtl: LIMITS.perUserSeconds }),
    kv.put(`h:${fp}`, String(number)),
    kv.put(ipKey, String(+ipCount + 1), { expirationTtl: 2 * 86400 }),
    kv.put(allKey, String(+allCount + 1), { expirationTtl: 2 * 86400 }),
  ]);
  return reply(201, 'Thanks for your feedback.');
}

export default { fetch: (req, env) => handle(req, env) };
