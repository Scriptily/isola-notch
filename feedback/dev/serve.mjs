// A local preview of the dashboard with made-up reports: the real Worker code,
// Node's SQLite in place of D1, no sign-in (localhost only).
//   node feedback/dev/serve.mjs   then open http://localhost:8787
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { join, dirname, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { handle } from '../src/worker.js';
import { d1 } from '../test/d1.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml' };
const ASSETS = {
  async fetch(req) {
    const p = new URL(req.url).pathname;
    const file = p === '/' ? 'index.html' : p.slice(1);
    if (file.includes('..')) return new Response('no', { status: 400 });
    try { return new Response(await readFile(join(root, 'public', file)), { headers: { 'content-type': types[extname(file)] || 'application/octet-stream' } }); }
    catch { return new Response('Not found', { status: 404 }); }
  },
};
const env = { DB: d1(), ASSETS, DEV_NO_AUTH: '1', APPS: 'isola,notes-app' };

// sample reports, sent through the real intake so they get real numbers
const now = Date.now(), H = 3_600_000;
const samples = [
  ['bug', 'Glow flickers on my second screen', 'When I move the pointer between my laptop and my 4K monitor, the colorful rim flickers for about a second. It started after the 1.3 update.', '1.3.0', 'github', 50, true],
  ['feature', 'Show the battery of my Galaxy Buds', 'AirPods get a lovely battery popup. Could Galaxy Buds Pro 2 get the same when they connect?', '1.3.0', 'store', 30],
  ['bug', 'Spotify volume slider jumps back', 'Dragging the Spotify volume under Now Playing works, but it jumps back to the old value for a moment before settling.', '1.2.1', 'github', 26],
  ['feature', 'Calendar events in the dashboard', 'A small "next meeting in 10 min" in the island would be amazing. I use Outlook.', '1.3.0', 'store', 20],
  ['bug', 'Search misses apps installed for all users', 'Typing "obs" finds nothing, but OBS Studio is installed in Program Files.', '1.3.0', 'github', 12, true],
  ['feature', 'Let me pick the island width', 'On my ultrawide the island looks tiny. A width slider in Appearance would help.', '1.3.0', 'github', 7],
  ['bug', 'Notification text cut off in Arabic', 'Right-to-left messages from WhatsApp show the end of the sentence first and get cut off.', '1.3.0', 'store', 3],
  ['feature', 'Pomodoro mode for the timer', 'A 25/5 loop with a different bell for breaks.', '1.3.0', 'github', 1],
];
let n = 0;
for (const [kind, subject, text, version, source, hoursAgo, logs] of samples) {
  n++;
  await handle(new Request('http://localhost/feedback', {
    method: 'POST', headers: { 'cf-connecting-ip': `10.0.0.${n}` },
    body: JSON.stringify({ app: n % 3 === 0 ? 'notes-app' : 'isola', user: `User${String(73_000_000_000 + n * 7_919_123_457).slice(0, 11 + (n % 4))}`, verifier: n.toString(16).padStart(32, '0'), kind, subject, text, version, windows: '10.0.26100', source,
      logs: logs ? `2026-11-17 09:01:12.004 INF [app] Isola ${version} starting\n2026-11-17 09:01:12.110 INF [gfx] device ready\n2026-11-17 09:01:14.930 WRN [display] monitor changed while rendering\n2026-11-17 09:01:15.002 ERR [gfx] present failed 0x887A0005\n2026-11-17 09:01:15.050 INF [gfx] recovering GPU device` : null }),
  }), env, { now: now - hoursAgo * H });
}
const db = env.DB.raw;
db.exec(`UPDATE reports SET status = 'closed', closed_at = ${now - 2 * H} WHERE id IN (3)`);
db.exec(`INSERT INTO events (report_id, actor, what, at) VALUES (3, 'dev@localhost', 'closed', ${now - 2 * H})`);
db.exec(`INSERT INTO tags VALUES (3, 'fixed'), (1, 'planned'), (6, 'planned'), (2, 'needs info')`);
db.exec(`INSERT INTO notes (report_id, author, body, created_at) VALUES (1, 'dev@localhost', 'Reproduced on a 150% + 100% setup. DComp target resize race.', ${now - 20 * H})`);

http.createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const body = chunks.length ? Buffer.concat(chunks) : undefined;
  // a phone-sized frame of the dashboard, for checking the narrow layout (preview only)
  if (req.url === '/__phone') { res.writeHead(200, { 'content-type': 'text/html' }); res.end('<body style="margin:0;background:#222"><iframe src="/" style="width:390px;height:844px;border:0"></iframe>'); return; }
  const r = await handle(new Request(`http://localhost:8787${req.url}`, { method: req.method, headers: req.headers, body: ['GET', 'HEAD'].includes(req.method) ? undefined : body }), env);
  const h = Object.fromEntries(r.headers);
  delete h['x-frame-options']; delete h['content-security-policy'];   // so the phone frame can show it
  res.writeHead(r.status, h);
  res.end(Buffer.from(await r.arrayBuffer()));
}).listen(8787, () => console.log('http://localhost:8787'));
