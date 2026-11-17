// Who is asking, from Cloudflare Access. Access sits in front of the dashboard
// and signs every request it lets through (Cf-Access-Jwt-Assertion). The
// Worker checks that signature itself rather than trusting the route alone, so
// a request that reaches it some other way (the workers.dev address) gets
// nothing.
//
// Vars: ACCESS_TEAM_DOMAIN ("yourteam.cloudflareaccess.com"), ACCESS_AUD (the
// Access application's audience tag). Both are secrets here only to keep
// account details out of the public repository.

let cache = { url: '', keys: null, at: 0 };

const b64url = s => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - s.length % 4) % 4)), c => c.charCodeAt(0));
const text = u8 => new TextDecoder().decode(u8);

async function keys(team, fetchImpl, now) {
  const url = `https://${team}/cdn-cgi/access/certs`;
  if (cache.url === url && cache.keys && now - cache.at < 10 * 60_000) return cache.keys;
  const r = await fetchImpl(url);
  if (!r.ok) throw new Error('certs ' + r.status);
  const { keys: list } = await r.json();
  cache = { url, keys: list, at: now };
  return list;
}

/** The signed-in person's email (or a service token's name), or null. */
export async function identify(req, env, fetchImpl = fetch, now = Date.now()) {
  const token = req.headers.get('cf-access-jwt-assertion');
  if (!token || !env.ACCESS_TEAM_DOMAIN || !env.ACCESS_AUD) return null;
  try {
    const [h, p, s] = token.split('.');
    const header = JSON.parse(text(b64url(h)));
    const payload = JSON.parse(text(b64url(p)));
    if (header.alg !== 'RS256') return null;
    const jwk = (await keys(env.ACCESS_TEAM_DOMAIN, fetchImpl, now)).find(k => k.kid === header.kid);
    if (!jwk) return null;
    const key = await crypto.subtle.importKey('jwk', jwk, { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['verify']);
    const ok = await crypto.subtle.verify('RSASSA-PKCS1-v1_5', key, b64url(s), new TextEncoder().encode(`${h}.${p}`));
    if (!ok) return null;
    const aud = Array.isArray(payload.aud) ? payload.aud : [payload.aud];
    if (!aud.includes(env.ACCESS_AUD)) return null;
    if (payload.iss !== `https://${env.ACCESS_TEAM_DOMAIN}`) return null;
    if (!payload.exp || payload.exp * 1000 < now) return null;
    return payload.email || payload.common_name || 'service';
  } catch {
    return null;
  }
}

export function resetAccessCache() { cache = { url: '', keys: null, at: 0 }; }
