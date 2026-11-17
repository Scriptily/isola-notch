export const json = (status, body, headers = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', ...headers } });

export const message = (status, text, extra = {}) => json(status, { message: text, ...extra });

export async function sha256hex(s) {
  const h = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
  return [...new Uint8Array(h)].map(x => x.toString(16).padStart(2, '0')).join('');
}

// the same normalization as the app (Feedback.Fingerprint): case, spacing and
// punctuation don't make a report new
export const fingerprint = (subject, text) =>
  sha256hex(`${subject} ${text}`.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim());

export async function readJson(req, max = 200_000) {
  const t = await req.text();
  if (t.length > max) return { error: 'Too large.' };
  try { return { value: JSON.parse(t) }; } catch { return { error: 'Expected JSON.' }; }
}
