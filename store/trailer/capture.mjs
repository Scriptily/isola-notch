// Renders the trailer in a headless Edge, frame by frame, to out/NNNNN.png.
//   node capture.mjs [fps] [from] [to]      (a still: node capture.mjs still 5.2 6.1 ...)
import http from 'node:http';
import { spawn } from 'node:child_process';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { join, extname, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = dirname(fileURLToPath(import.meta.url));
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml' };
const server = http.createServer(async (req, res) => {
  try { const p = join(ROOT, decodeURIComponent(new URL(req.url, 'http://x').pathname)); const body = await readFile(p); res.writeHead(200, { 'content-type': types[extname(p)] || 'application/octet-stream' }); res.end(body); }
  catch { res.writeHead(404); res.end(); }
}).listen(8931);

const args = process.argv.slice(2);
const stills = args[0] === 'still' ? args.slice(1).map(Number) : null;
const fps = stills ? 0 : Number(args[0] || 60), from = Number(args[1] || 0), to = Number(args[2] || 30);
const edge = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', ['--headless=new', '--remote-debugging-port=9341', `--user-data-dir=${join(ROOT, 'edge-profile')}`,
  '--hide-scrollbars', '--force-device-scale-factor=1', '--ignore-gpu-blocklist', '--enable-gpu-rasterization', '--window-size=1920,1080', 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let ws;
for (let i = 0; i < 60 && !ws; i++) { await sleep(250); try { const t = await (await fetch('http://127.0.0.1:9341/json')).json(); const pg = t.find(x => x.type === 'page'); if (pg) ws = new WebSocket(pg.webSocketDebuggerUrl); } catch {} }
await new Promise(r => ws.addEventListener('open', r));
let id = 0; const pending = new Map();
ws.addEventListener('message', m => { const d = JSON.parse(m.data); if (d.id && pending.has(d.id)) { pending.get(d.id)(d.result ?? d); pending.delete(d.id); } if (d.method === 'Runtime.exceptionThrown') console.log('EXCEPTION', JSON.stringify(d.params.exceptionDetails).slice(0, 600)); if (d.method === 'Runtime.consoleAPICalled') console.log('console', JSON.stringify(d.params.args.map(a => a.value ?? a.description)).slice(0, 300)); });
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url: 'http://127.0.0.1:8931/index.html' });
for (let i = 0; i < 400; i++) { await sleep(250); const r = await send('Runtime.evaluate', { expression: 'window.ready === true', returnByValue: true }); if (r.result?.value) break; if (i === 399) { console.log('never ready'); process.exit(1); } }
const gl = await send('Runtime.evaluate', { expression: `(() => { const g = document.getElementById('gl').getContext('webgl2'); const d = g && g.getExtension('WEBGL_debug_renderer_info'); return d ? g.getParameter(d.UNMASKED_RENDERER_WEBGL) : 'unknown'; })()`, returnByValue: true });
console.log('renderer:', gl.result?.value);
const out = join(ROOT, stills ? 'stills' : 'out');
await mkdir(out, { recursive: true });
const times = stills ?? Array.from({ length: Math.round((to - from) * fps) }, (_, i) => from + i / fps);
const t0 = Date.now();
for (let i = 0; i < times.length; i++) {
  const t = times[i];
  await send('Runtime.evaluate', { expression: `window.render(${t})`, returnByValue: true });
  const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  const name = stills ? `t${t.toFixed(2)}.png` : `${String(Math.round((t - 0) * fps)).padStart(5, '0')}.png`;
  await writeFile(join(out, name), Buffer.from(shot.data, 'base64'));
  if (i % 60 === 0) console.log(`${i}/${times.length}  ${((Date.now() - t0) / (i + 1)).toFixed(0)} ms a frame`);
}
console.log('done', times.length, 'frames in', ((Date.now() - t0) / 1000).toFixed(0), 's');
ws.close(); edge.kill(); server.close();
process.exit(0);
