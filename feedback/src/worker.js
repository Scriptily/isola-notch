// Isola feedback: one Cloudflare Worker for the app's reports (public, rate
// limited) and the private dashboard (behind Cloudflare Access). See README.md.
import { submit, mine } from './intake.js';
import { admin } from './admin.js';
import { identify } from './access.js';
import { message } from './util.js';

export async function handle(req, env, { now = Date.now(), fetchImpl = fetch } = {}) {
  const url = new URL(req.url);
  if (url.pathname === '/feedback' && req.method === 'POST') return submit(req, env, now);
  if (url.pathname === '/feedback/mine' && req.method === 'POST') return mine(req, env);

  // Everything else is the dashboard: only on its own hostname, and only for
  // someone Access let through. The app's workers.dev address serves nothing
  // but the two routes above.
  if (env.DASHBOARD_HOST && url.hostname !== env.DASHBOARD_HOST) return message(404, 'Not found.');
  const who = env.DEV_NO_AUTH === '1' && url.hostname === 'localhost' ? 'dev@localhost' : await identify(req, env, fetchImpl, now);
  if (!who) return message(401, 'Sign in through Cloudflare Access.');
  if (url.pathname.startsWith('/api/')) return admin(req, env, who, url, now);
  const res = await env.ASSETS.fetch(req);
  // the dashboard itself never caches or frames
  const h = new Headers(res.headers);
  h.set('x-frame-options', 'DENY');
  h.set('referrer-policy', 'no-referrer');
  h.set('content-security-policy', "default-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'");
  return new Response(res.body, { status: res.status, headers: h });
}

export default { fetch: (req, env) => handle(req, env) };
