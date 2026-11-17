// The feedback hub: one Cloudflare Worker for every app's reports (public,
// rate limited) and the private dashboard (behind Cloudflare Access).
// See README.md.
import { submit, mine, appsOf, cryptoRandom } from './intake.js';
import { admin } from './admin.js';
import { identify } from './access.js';
import { message } from './util.js';

/**
 * Which dashboard hostnames exist, and what each opens: secret HOSTS, JSON
 * like {"dashboard.example.com": "", "app.example.com": "app"}. "" is the home
 * page for all apps; an app id opens that app, so app.example.com/feedback/12
 * is the same page as dashboard.example.com/app/feedback/12. A secret, so this
 * public code never names the domain. Unset (local preview): any host is home.
 */
export function hosts(env) {
  if (env.HOSTS) { try { return JSON.parse(env.HOSTS); } catch { return {}; } }
  return env.DASHBOARD_HOST ? { [env.DASHBOARD_HOST]: '' } : null;
}

const SECURITY = {
  'x-frame-options': 'DENY',
  'referrer-policy': 'no-referrer',
  'x-content-type-options': 'nosniff',
  'content-security-policy': "default-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'",
};

export async function handle(req, env, { now = Date.now(), fetchImpl = fetch, random = cryptoRandom } = {}) {
  const url = new URL(req.url);
  if (url.pathname === '/feedback' && req.method === 'POST') return submit(req, env, now, random);
  if (url.pathname === '/feedback/mine' && req.method === 'POST') return mine(req, env);

  // Everything else is the dashboard: only on its own hostnames, and only for
  // someone Access let through. The apps' workers.dev address serves nothing
  // but the two routes above.
  const map = hosts(env);
  if (map && !(url.hostname in map)) return message(404, 'Not found.');
  const hostApp = map ? map[url.hostname] : '';
  const who = env.DEV_NO_AUTH === '1' && url.hostname === 'localhost' ? 'dev@localhost' : await identify(req, env, fetchImpl, now);
  if (!who) return message(401, 'Sign in through Cloudflare Access.');
  if (url.pathname.startsWith('/api/')) return admin(req, env, who, url, now, { hostApp, apps: appsOf(env) });

  // the page and its files; any other path is a page of the app (/isola/feedback/482913)
  let res = await env.ASSETS.fetch(req);
  if (res.status === 404 && req.method === 'GET' && !/\.[a-z0-9]+$/i.test(url.pathname))
    res = await env.ASSETS.fetch(new Request(new URL('/index.html', url), req));
  const h = new Headers(res.headers);
  for (const [k, v] of Object.entries(SECURITY)) h.set(k, v);
  h.set('cache-control', 'no-store');
  return new Response(res.body, { status: res.status, headers: h });
}

export default { fetch: (req, env) => handle(req, env) };
