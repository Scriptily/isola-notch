// The trailer, rendered frame by frame: window.render(t) draws the moment t
// seconds in, exactly, whatever the machine's speed (capture.mjs steps it).
import * as THREE from 'three';
import { RoundedBoxGeometry } from 'three/addons/geometries/RoundedBoxGeometry.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';

const W = 1920, H = 1080;

// ---- easing and keyframes --------------------------------------------------------------------
const clamp = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
const lerp = (a, b, k) => a + (b - a) * k;
const ease = { inOut: k => k < 0.5 ? 4 * k * k * k : 1 - (-2 * k + 2) ** 3 / 2, out: k => 1 - (1 - k) ** 3, in: k => k * k * k,
  back: k => { const c = 1.6; return 1 + (c + 1) * (k - 1) ** 3 + c * (k - 1) ** 2; }, sine: k => 0.5 - 0.5 * Math.cos(Math.PI * k) };
const span = (t, a, b) => clamp((t - a) / (b - a));
/** [time, [values], easing?] keys; easing applies into that key. */
function keys(list, t) {
  if (t <= list[0][0]) return list[0][1];
  for (let i = 1; i < list.length; i++) {
    const [t1, v1, e = 'inOut'] = list[i], [t0, v0] = list[i - 1];
    if (t <= t1) { const k = ease[e]((t - t0) / (t1 - t0)); return v0.map((v, j) => lerp(v, v1[j], k)); }
  }
  return list[list.length - 1][1];
}
function rand(seed) { let s = seed >>> 0 || 1; return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 2 ** 32); }

// ---- renderer --------------------------------------------------------------------------------
const renderer = new THREE.WebGLRenderer({ canvas: document.getElementById('gl'), antialias: true, preserveDrawingBuffer: true });
renderer.setSize(W, H, false);
renderer.setPixelRatio(1);
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.05;
renderer.outputColorSpace = THREE.SRGBColorSpace;
const scene = new THREE.Scene();
const pmrem = new THREE.PMREMGenerator(renderer);
scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
scene.environmentIntensity = 0.55;
const camera = new THREE.PerspectiveCamera(32, W / H, 0.5, 2000);

// the backdrop: near black with a slow color glow, like a stage
function backdropTexture() {
  const c = document.createElement('canvas'); c.width = 1920; c.height = 1080;
  const g = c.getContext('2d');
  g.fillStyle = '#04050a'; g.fillRect(0, 0, 1920, 1080);
  for (const [x, y, r, col] of [[300, 900, 900, 'rgba(18,110,120,0.30)'], [1650, 820, 950, 'rgba(96,58,190,0.30)'], [960, 1250, 900, 'rgba(40,70,190,0.22)']]) {
    const rg = g.createRadialGradient(x, y, 0, x, y, r); rg.addColorStop(0, col); rg.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = rg; g.fillRect(0, 0, 1920, 1080);
  }
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; return t;
}
scene.background = backdropTexture();

// ---- the laptop ----------------------------------------------------------------------------
const alu = new THREE.MeshPhysicalMaterial({ color: 0x2a2c33, metalness: 0.85, roughness: 0.32, clearcoat: 0.3, clearcoatRoughness: 0.4 });
const black = new THREE.MeshStandardMaterial({ color: 0x050506, roughness: 0.5, metalness: 0.2 });
const laptop = new THREE.Group();
scene.add(laptop);
const BW = 32, BD = 22, BH = 1.3, LH = 21, LT = 0.5;
const base = new THREE.Mesh(new RoundedBoxGeometry(BW, BH, BD, 6, 0.55), alu);
base.position.y = BH / 2;
laptop.add(base);

function keyboardTexture() {
  const c = document.createElement('canvas'); c.width = 2048; c.height = 1408;
  const g = c.getContext('2d');
  g.fillStyle = '#1f2127'; g.fillRect(0, 0, 2048, 1408);
  const rows = [[14, 1], [14, 1], [14, 1], [13, 1], [12, 1]];
  const kx = 170, ky = 120, kw = 1708, unit = kw / 14.6;
  for (let r = 0; r < 6; r++) {
    const y = ky + r * (unit * 0.82), h = r === 0 ? unit * 0.5 : unit * 0.72;
    let x = kx;
    const n = r === 0 ? 14 : r === 5 ? 9 : 14 - (r > 2 ? r - 2 : 0);
    for (let i = 0; i < n; i++) {
      let w = unit * 0.9;
      if (r === 5 && i === 4) w = unit * 4.8;                 // the space bar
      if (r > 0 && r < 5 && (i === 0 || i === n - 1)) w = unit * (1.2 + (r - 1) * 0.25);
      g.fillStyle = '#0d0e11';
      g.beginPath(); g.roundRect(x, y, w, h, 12); g.fill();
      x += w + unit * 0.1;
      if (x > kx + kw) break;
    }
  }
  g.fillStyle = '#26282f';
  g.beginPath(); g.roundRect(724, 900, 600, 400, 26); g.fill();   // the touchpad
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; t.anisotropy = 8; return t;
}
const deck = new THREE.Mesh(new THREE.PlaneGeometry(BW - 1, BD - 1), new THREE.MeshStandardMaterial({ map: keyboardTexture(), roughness: 0.55, metalness: 0.3 }));
deck.rotation.x = -Math.PI / 2; deck.position.y = BH + 0.01;
laptop.add(deck);

const lid = new THREE.Group();   // pivots at the hinge
lid.position.set(0, BH, -BD / 2 + 0.3);
laptop.add(lid);
const lidBody = new THREE.Mesh(new RoundedBoxGeometry(BW, LH, LT, 6, 0.24), alu);
lidBody.position.set(0, LH / 2, -LT / 2);
lid.add(lidBody);
const bezel = new THREE.Mesh(new THREE.PlaneGeometry(BW - 0.5, LH - 0.5), black);
bezel.position.set(0, LH / 2, 0.005);
lid.add(bezel);

// the screen: a canvas drawn every frame (wallpaper, taskbar, the island, the cursor)
const SW = 2560, SH = 1600;
const screenCanvas = document.createElement('canvas'); screenCanvas.width = SW; screenCanvas.height = SH;
const sg = screenCanvas.getContext('2d');
const screenTex = new THREE.CanvasTexture(screenCanvas); screenTex.colorSpace = THREE.SRGBColorSpace; screenTex.anisotropy = 8;
const SPW = 30, SPH = SPW * SH / SW;
const screen = new THREE.Mesh(new THREE.PlaneGeometry(SPW, SPH), new THREE.MeshBasicMaterial({ map: screenTex, toneMapped: false, color: 0xe2e2e2 }));
screen.position.set(0, 0.85 + SPH / 2, 0.012);
lid.add(screen);
lid.rotation.x = -0.24;   // open, tipped back a little
// the screen's light on the deck and the space in front of it
const screenLight = new THREE.PointLight(0x7f9cff, 30, 40, 1.6);
screenLight.position.set(0, 12, 6);
laptop.add(screenLight);
laptop.updateMatrixWorld(true);
const onScreen = (u, v) => screen.localToWorld(new THREE.Vector3((u - 0.5) * SPW, (0.5 - v) * SPH, 0));

// a soft pool of light under it
function poolTexture() {
  const c = document.createElement('canvas'); c.width = c.height = 512;
  const g = c.getContext('2d'), rg = g.createRadialGradient(256, 256, 0, 256, 256, 256);
  rg.addColorStop(0, 'rgba(80,110,220,0.55)'); rg.addColorStop(0.45, 'rgba(60,40,140,0.2)'); rg.addColorStop(1, 'rgba(0,0,0,0)');
  g.fillStyle = rg; g.fillRect(0, 0, 512, 512);
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; return t;
}
const pool = new THREE.Mesh(new THREE.PlaneGeometry(110, 110), new THREE.MeshBasicMaterial({ map: poolTexture(), transparent: true, depthWrite: false, blending: THREE.AdditiveBlending }));
pool.rotation.x = -Math.PI / 2; pool.position.y = -0.05;
scene.add(pool);

// lights: a soft key, two colored rims (the island's teal and violet)
scene.add(new THREE.HemisphereLight(0x6070a0, 0x05060a, 0.35));
const key = new THREE.DirectionalLight(0xffffff, 1.3); key.position.set(-30, 50, 40); scene.add(key);
const rimA = new THREE.DirectionalLight(0x37d6c4, 2.2); rimA.position.set(-50, 20, -40); scene.add(rimA);
const rimB = new THREE.DirectionalLight(0x9b6bff, 2.6); rimB.position.set(50, 25, -40); scene.add(rimB);

// drifting motes for depth
const motes = (() => {
  const r = rand(7), n = 500, pos = new Float32Array(n * 3), speed = new Float32Array(n);
  for (let i = 0; i < n; i++) { pos[i * 3] = (r() - 0.5) * 200; pos[i * 3 + 1] = r() * 80 - 10; pos[i * 3 + 2] = (r() - 0.5) * 200 - 20; speed[i] = 0.3 + r() * 1.2; }
  const geo = new THREE.BufferGeometry(); geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  const c = document.createElement('canvas'); c.width = c.height = 64;
  const g = c.getContext('2d'), rg = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  rg.addColorStop(0, 'rgba(255,255,255,1)'); rg.addColorStop(0.3, 'rgba(180,200,255,0.5)'); rg.addColorStop(1, 'rgba(0,0,0,0)');
  g.fillStyle = rg; g.fillRect(0, 0, 64, 64);
  const m = new THREE.PointsMaterial({ size: 0.7, map: new THREE.CanvasTexture(c), transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, opacity: 0.55, color: 0xa8b8ff });
  const p = new THREE.Points(geo, m); scene.add(p);
  return { p, base: pos.slice(), speed };
})();

// ---- Alt + Space keycaps ---------------------------------------------------------------------
function capTexture(label) {
  const c = document.createElement('canvas'); c.width = 512; c.height = 256;
  const g = c.getContext('2d'); g.fillStyle = '#5d5f68'; g.fillRect(0, 0, 512, 256);
  g.fillStyle = '#d4d5dc'; g.font = '600 96px "Segoe UI Variable Display", "Segoe UI"'; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(label, 256, 132);
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; return t;
}
const capMat = new THREE.MeshStandardMaterial({ color: 0x3a3c44, roughness: 0.5, metalness: 0.1 });
function keycap(label, w) {
  const grp = new THREE.Group();
  grp.add(new THREE.Mesh(new RoundedBoxGeometry(w, 1.4, 3.6, 6, 0.45), capMat));
  const top = new THREE.Mesh(new THREE.PlaneGeometry(w - 0.6, 3.0), new THREE.MeshStandardMaterial({ map: capTexture(label), roughness: 0.45 }));
  top.rotation.x = -Math.PI / 2; top.position.y = 0.71; grp.add(top);
  // a glow ring under it when pressed
  const glow = new THREE.Mesh(new THREE.PlaneGeometry(w + 3, 6.5), new THREE.MeshBasicMaterial({ map: poolTexture(), transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, opacity: 0 }));
  glow.rotation.x = -Math.PI / 2; glow.position.y = -0.75; grp.add(glow);
  grp.userData.glow = glow;
  scene.add(grp); grp.visible = false;
  return grp;
}
const alt = keycap('Alt', 4.2), space = keycap('Space', 12);

// ---- the dashboard cards (20-24 s) ---------------------------------------------------------------
const cards = [];

// ---- the clips, as images ----------------------------------------------------------------------------
const idx = await (await fetch('frames/index.json')).json();
const load = src => new Promise((res, rej) => { const im = new Image(); im.onload = () => res(im); im.onerror = rej; im.src = src; });
const clips = {};
for (const name of ['hello', 'music', 'popups', 'downloads', 'search', 'open']) {
  const n = idx[name].frames;
  clips[name] = await Promise.all(Array.from({ length: n }, (_, i) => load(`frames/${name}/${String(i).padStart(4, '0')}.png`)));
}
const stills = {};
for (const name of ['home', 'weather', 'notes', 'timer', 'shelf', 'media', 'commands']) stills[name] = await load(`frames/${name}.png`);
const wallpaper = await (async () => {
  const c = document.createElement('canvas'); c.width = SW; c.height = SH;
  const g = c.getContext('2d');
  const lg = g.createLinearGradient(0, 0, SW, SH); lg.addColorStop(0, '#0a1023'); lg.addColorStop(1, '#14082a');
  g.fillStyle = lg; g.fillRect(0, 0, SW, SH);
  // soft ribbons of color, the way Windows' own wallpapers bloom
  for (const [x, y, r, col] of [[600, 1300, 1200, 'rgba(32,170,170,0.55)'], [2100, 1100, 1300, 'rgba(120,70,230,0.55)'], [1300, 650, 900, 'rgba(40,90,230,0.42)'], [1900, 300, 700, 'rgba(210,90,200,0.25)']]) {
    const rg = g.createRadialGradient(x, y, 0, x, y, r); rg.addColorStop(0, col); rg.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = rg; g.fillRect(0, 0, SW, SH);
  }
  // the taskbar
  g.fillStyle = 'rgba(16,18,28,0.82)'; g.fillRect(0, SH - 96, SW, 96);
  g.fillStyle = 'rgba(255,255,255,0.10)'; g.fillRect(0, SH - 96, SW, 2);
  const icons = ['#3b82f6', '#22c55e', '#f59e0b', '#ef4444', '#a855f7', '#06b6d4', '#e5e7eb'];
  icons.forEach((col, i) => { g.fillStyle = col; g.globalAlpha = 0.9; g.beginPath(); g.roundRect(SW / 2 - 3.5 * 76 + i * 76 + 14, SH - 74, 50, 50, 14); g.fill(); });
  g.globalAlpha = 1;
  g.fillStyle = 'rgba(255,255,255,0.85)'; g.font = '500 30px "Segoe UI Variable Text", "Segoe UI"'; g.textAlign = 'right';
  g.fillText('10:24', SW - 40, SH - 40);
  return c;
})();

// which island frame is on screen at time t: [clip, frame index]
const seg = (t, a, b, clip, f0, f1) => (t >= a && t < b) ? [clip, Math.round(lerp(f0, f1, span(t, a, b)))] : null;
function islandAt(t) {
  return seg(t, 0, 0.5, 'hello', 0, 0) || seg(t, 0.5, 3.7, 'hello', 0, 107) || seg(t, 3.7, 4.0, 'music', 20, 30)
    || seg(t, 4.0, 5.0, 'music', 30, 112) || seg(t, 5.0, 5.45, 'music', 112, 134) || seg(t, 5.45, 8.0, 'music', 134, 205)
    || seg(t, 8.0, 11.95, 'popups', 12, 216) || seg(t, 11.95, 12.0, 'downloads', 0, 0)
    || seg(t, 12.0, 12.3, 'downloads', 24, 36) || seg(t, 12.3, 12.8, 'downloads', 36, 62) || seg(t, 12.8, 13.05, 'downloads', 62, 74)
    || seg(t, 13.05, 14.0, 'downloads', 132, 152) || seg(t, 14.0, 15.1, 'downloads', 168, 214) || seg(t, 15.1, 16.0, 'downloads', 236, 262)
    || seg(t, 16.0, 16.3, 'search', 0, 12) || seg(t, 16.3, 17.9, 'search', 12, 84) || seg(t, 17.9, 20.0, 'search', 84, 95)
    || seg(t, 20.0, 20.4, 'open', 8, 18) || seg(t, 20.4, 21.3, 'open', 18, 42) || seg(t, 21.3, 21.7, 'open', 42, 54) || seg(t, 21.7, 22.5, 'open', 54, 62)
    || seg(t, 22.5, 22.9, 'open', 62, 72) || seg(t, 22.9, 24.0, 'open', 72, 108) || seg(t, 24.0, 31, 'music', 20, 100);
}

// the cursor: where it is, and when it clicks
const cursorKeys = [
  [3.9, [2300, 1500, 0]], [4.2, [2150, 1250, 1]], [4.95, [1300, 60, 1], 'inOut'], [5.6, [1500, 420, 1]], [6.2, [1700, 760, 0]],
  [11.9, [2000, 900, 0]], [12.05, [1900, 760, 1]], [12.25, [1060, 130, 1]], [12.55, [1060, 130, 1]], [12.7, [1690, 130, 1]], [13.2, [1900, 500, 0]],
  // Home: the Kitchen light, then the coffee maker
  [20.3, [1750, 900, 0]], [20.6, [1650, 650, 1]], [21.25, [1395, 196, 1]], [21.75, [1395, 196, 1]], [22.45, [924, 470, 1]], [22.9, [924, 470, 1]], [23.5, [1150, 820, 0]],
];
const clicks = [5.0, 12.28, 12.72, 21.3, 22.5];
function drawCursor(t) {
  const [x, y, a] = keys(cursorKeys, t);
  if (a <= 0.01) return;
  // a press squeezes it a little and sends out a ring
  let press = 0;
  for (const c of clicks) if (t >= c - 0.06 && t < c + 0.5) {
    press = Math.max(press, t < c ? 1 - (c - t) / 0.06 : Math.max(0, 1 - (t - c) / 0.12));
    const r = span(t, c, c + 0.45);
    if (r > 0 && r < 1) { sg.strokeStyle = `rgba(255,255,255,${0.55 * (1 - r)})`; sg.lineWidth = 4; sg.beginPath(); sg.arc(x, y, 12 + 60 * ease.out(r), 0, Math.PI * 2); sg.stroke(); }
  }
  const s = 1.9 * (1 - 0.14 * press);
  sg.save(); sg.translate(x, y); sg.scale(s, s); sg.globalAlpha = a;
  sg.beginPath(); sg.moveTo(0, 0); sg.lineTo(0, 22); sg.lineTo(5.5, 17); sg.lineTo(9.5, 25.5); sg.lineTo(12.5, 24); sg.lineTo(8.7, 16); sg.lineTo(15.5, 16); sg.closePath();
  sg.shadowColor = 'rgba(0,0,0,0.5)'; sg.shadowBlur = 6; sg.shadowOffsetY = 2;
  sg.fillStyle = '#fff'; sg.fill(); sg.shadowColor = 'transparent';
  sg.lineWidth = 1.3; sg.strokeStyle = '#000'; sg.stroke();
  sg.restore();
}

const ISL = 1.62;   // the island's recordings at a 1600-pixel-wide screen's size
let screenOverride = null;   // { im, cursor: [x, y] } for a still
function drawScreen(t) {
  sg.drawImage(wallpaper, 0, 0);
  const at = screenOverride ? null : islandAt(t);
  if (screenOverride) {
    const sc = screenOverride.scale ?? ISL, im = screenOverride.im, w = im.width * sc, h = im.height * sc;
    sg.drawImage(im, (SW - w) / 2, 0, w, h);
    screenTex.needsUpdate = true;
    return;
  }
  if (at) {
    const im = clips[at[0]][Math.min(at[1], clips[at[0]].length - 1)];
    const w = im.width * ISL, h = im.height * ISL;
    sg.drawImage(im, (SW - w) / 2, 0, w, h);
  }
  drawCursor(t);
  screenTex.needsUpdate = true;
}

// ---- captions ----------------------------------------------------------------------------------------
const captionsEl = document.getElementById('captions');
const shade = document.createElement('div');
shade.style.cssText = 'position:absolute;inset:0;pointer-events:none;background:linear-gradient(90deg in oklch, rgba(3,4,8,0.78) 0%, rgba(3,4,8,0.5) 32%, rgba(3,4,8,0) 58%);opacity:0';
captionsEl.before(shade);
const CAPS = [
  { a: 0.7, b: 3.75, x: 150, y: 700, lines: ['Meet Isola.'], sub: 'A dynamic island for your Windows laptop.' },
  { a: 4.3, b: 7.8, x: 150, y: 640, lines: ['Your music,', 'alive at the top.'], sub: 'The cover, a waveform, and a glow in its colors.' },
  { a: 8.3, b: 11.8, x: 150, y: 640, lines: ['Notifications', 'that stay out of the way.'], sub: 'Messages, calls, songs and devices, in one pill.' },
  { a: 12.3, b: 15.8, x: 150, y: 640, lines: ['Paste a link.', 'Get the video.'], sub: 'From YouTube and most sites. Or convert to MP3, MP4 or GIF.' },
  { a: 17.05, b: 19.8, x: 150, y: 640, lines: ['Everything,', 'one keystroke away.'], sub: 'Math, apps, files and commands on Alt+Space.' },
  { a: 20.3, b: 23.8, x: 150, y: 640, lines: ['Your home,', 'one click away.'], sub: 'Lights, scenes, climate and locks from Home Assistant.' },
];
for (const c of CAPS) {
  const el = document.createElement('div'); el.className = 'caption'; el.style.left = c.x + 'px'; el.style.top = c.y + 'px';
  c.words = [];
  c.lines.forEach((line, li) => {
    // a two-line caption: the first line leads in gray, the second lands in white
    const l = document.createElement('span'); l.className = 'line' + (c.lines.length > 1 && li === 0 ? ' lead' : '');
    line.split(' ').forEach((w, wi, arr) => {
      const s = document.createElement('span'); s.className = 'w'; s.textContent = w + (wi < arr.length - 1 ? ' ' : '');
      l.appendChild(s); c.words.push(s);
    });
    el.appendChild(l);
  });
  if (c.sub) { const s = document.createElement('span'); s.className = 'sub w'; s.textContent = c.sub; el.appendChild(s); c.words.push(s); }
  captionsEl.appendChild(el); c.el = el;
}
function drawCaptions(t) {
  shade.style.opacity = CAPS.reduce((o, c) => Math.max(o, ease.out(span(t, c.a - 0.3, c.a + 0.3)) * (1 - ease.in(span(t, c.b - 0.3, c.b + 0.2)))), 0);
  for (const c of CAPS) {
    const on = t >= c.a - 0.05 && t <= c.b + 0.05;
    c.el.style.display = on ? 'block' : 'none';
    if (!on) continue;
    c.words.forEach((w, i) => {
      const k = ease.out(span(t, c.a + i * 0.07, c.a + i * 0.07 + 0.55)), out = ease.in(span(t, c.b - 0.4 + i * 0.02, c.b));
      const y = 46 * (1 - k) - 26 * out, blur = 14 * (1 - k) + 10 * out, op = k * (1 - out);
      w.style.transform = `translateY(${y}px)`; w.style.filter = `blur(${blur}px)`; w.style.opacity = op;
    });
  }
}

// ---- the camera ----------------------------------------------------------------------------------------
// positions and what it looks at; the laptop sits right of center so the words have the left
const island = onScreen(0.5, 0.06), mid = onScreen(0.5, 0.45);
const I = (x, y, z) => [island.x + x, island.y + y, island.z + z];
const M = (x, y, z) => [mid.x + x, mid.y + y, mid.z + z];
// F(d, down, side): the camera d from the island (a little to its left by side x d), aimed so the
// island's content (down below its top edge) sits right of center, where the words aren't
const F = (d, down, side = -0.12) => ({ pos: I(side * d, -0.6 * down, d), look: I(-0.17 * d, -down - 0.04 * d, 0) });
const shots = [
  [3.6, F(40, 4)], [4.0, F(24, 1.6)], [4.95, F(22.5, 1.6)], [5.45, F(29, 4.6), 'out'], [7.8, F(26.5, 4.6), 'sine'],
  [8.0, F(21, 1.6, -0.25)], [11.8, F(19, 1.6, -0.3), 'sine'],
  [12.0, F(31, 5.2)], [12.95, F(30, 5.2), 'sine'], [13.25, F(18, 1.4), 'out'], [15.0, F(16.5, 1.4), 'sine'], [15.35, F(31, 5.2), 'out'], [15.8, F(30, 5.2), 'sine'],
  [16.0, F(42, 7, -0.24)], [19.8, F(36, 6, -0.24), 'sine'],
  [20.0, F(31, 5.6)], [23.8, F(28.5, 5.6), 'sine'],
];
const camPos = [[0, I(-55, 4, 118)], ...shots.map(([t, f, e]) => [t, f.pos, e]),
  [24.0, M(-12, 22, 92)], [30, M(0, 40, 128), 'inOut']];
const camLook = [[0, M(0, 0, 0)], ...shots.map(([t, f, e]) => [t, f.look, e]),
  [24.0, M(0, 16, 0)], [30, M(0, 19, 0)]];

// ---- post: a bloom so the glow and the screen light the air a little ----------------------------------------
const composer = new EffectComposer(renderer);
composer.setPixelRatio(1); composer.setSize(W, H);
composer.addPass(new RenderPass(scene, camera));
// only what's truly bright glows: white tiles and covers stay readable
const bloom = new UnrealBloomPass(new THREE.Vector2(W, H), 0.5, 0.6, 0.93);
composer.addPass(bloom);
composer.addPass(new OutputPass());

// ---- one frame ----------------------------------------------------------------------------------------
const logo = document.getElementById('logo'), fade = document.getElementById('fade');
const cardSpots = [[-24, 17, 30, 0.3], [-6, 23, 22, 0.1], [13, 23, 22, -0.1], [31, 17, 30, -0.3]];
window.render = t => {
  // the laptop turns slowly, settles, and leans away at the end
  laptop.rotation.y = keys([[0, [0.55]], [4, [0.06]], [8, [0.02]], [12, [-0.02]], [16, [0.12]], [20, [0.04]], [24, [0.05]], [30, [0]]], t)[0];
  laptop.position.y = Math.sin(t * 0.8) * 0.35;
  drawScreen(t);
  // motes drift up
  const p = motes.p.geometry.attributes.position;
  for (let i = 0; i < p.count; i++) p.array[i * 3 + 1] = ((motes.base[i * 3 + 1] + t * motes.speed[i] + 10) % 80) - 10;
  p.needsUpdate = true;
  // Alt + Space: they rise in front, press down together, and fall away
  for (const [cap, x0, delay] of [[alt, -6.5, 0], [space, 3.2, 0.06]]) {
    // they rise once the camera has turned to them (before, one poked into the corner of the last shot)
    const up = ease.back(span(t, 15.98 + delay, 16.3 + delay)), down = span(t, 16.34, 16.41), rel = span(t, 16.41, 16.52), gone = ease.in(span(t, 16.85, 17.2));
    cap.visible = t > 15.97 && t < 17.25;
    const press = (down - rel) > 0 ? down - rel : 0;
    cap.position.set(island.x - 11 + x0, island.y - 17 + 6 * up - 0.9 * press - 12 * gone, island.z + 7);
    cap.scale.set(1 + 0.06 * press, 1 - 0.2 * press, 1 + 0.06 * press);   // squash on the press
    cap.rotation.set(0.75 - 0.15 * up, 0.28, -0.04);
    cap.userData.glow.material.opacity = 0.35 * press;
  }
  // the cards fly in on arcs, one a beat, and leave at the finale
  if (!cards.length) for (const [i, name] of ['home', 'weather', 'notes', 'timer'].entries()) {
    const im = stills[name], tex = new THREE.Texture(im); tex.needsUpdate = true; tex.colorSpace = THREE.SRGBColorSpace; tex.anisotropy = 8;
    const w = 16, h = w * im.height / im.width;
    const m = new THREE.Mesh(new THREE.PlaneGeometry(w, h), new THREE.MeshBasicMaterial({ map: tex, transparent: true, color: 0xc4c4cc, toneMapped: false, side: THREE.DoubleSide }));
    m.visible = false; scene.add(m); cards.push(m);
  }
  cards.forEach((m, i) => {
    const t0 = 20.25 + i * 1.0, k = ease.out(span(t, t0, t0 + 0.75)), out = ease.in(span(t, 23.85, 24.35));
    m.visible = false;
    const [x, y, z, ry] = cardSpots[i];
    // an arc: from behind and below the camera, curving into place
    m.position.set(mid.x + lerp(x * 1.8, x, k), mid.y + lerp(y - 26, y, k) + Math.sin(k * Math.PI) * 9 + 30 * out, mid.z + lerp(z + 40, z, k));
    m.rotation.set(lerp(0.9, 0, k), ry + lerp(0.8 * Math.sign(-x), 0, k), lerp(0.3, 0, k));
    m.scale.setScalar(lerp(0.6, 1, k) * (1 - out));
    m.material.opacity = k * (1 - out);
  });
  // the camera, with a little handheld breath
  const cp = keys(camPos, t), cl = keys(camLook, t);
  camera.position.set(cp[0] + Math.sin(t * 0.7) * 0.4, cp[1] + Math.sin(t * 0.9) * 0.3, cp[2]);
  camera.lookAt(cl[0], cl[1], cl[2]);
  bloom.strength = 0.5 + 0.3 * Math.max(0, 1 - span(t, 24, 25.2)) * (t >= 24 ? 1 : 0);
  // words, the logo and the fades
  drawCaptions(t);
  const lg = ease.out(span(t, 24.4, 25.3));
  logo.style.opacity = lg * (1 - span(t, 29.2, 29.9));
  logo.style.transform = `translateY(${30 * (1 - lg)}px) scale(${0.96 + 0.04 * lg})`;
  logo.style.filter = `blur(${10 * (1 - lg)}px)`;
  renderer.toneMappingExposure = 1.05 * (1 - 0.55 * ease.inOut(span(t, 24.2, 25.4)));   // the laptop dims behind the logo
  fade.style.opacity = Math.max(1 - span(t, 0, 0.9), span(t, 29.4, 30));
  composer.render();
  return true;
};

// ---- the Store screenshots: one composed frame each, in the trailer's look -----------------------------
const stillCap = document.createElement('div'); stillCap.className = 'caption'; stillCap.style.display = 'none';
captionsEl.appendChild(stillCap);
window.still = spec => {
  const im = spec.clip ? clips[spec.clip][spec.frame] : stills[spec.still];
  // a little larger than life on the screen, so it reads when the Store shows the shot small
  screenOverride = spec.logo ? { im: clips.music[60] } : { im, scale: spec.scale ?? 2.55 };
  for (const c of CAPS) c.el.style.display = 'none';
  laptop.rotation.y = spec.rot ?? 0.3;
  laptop.position.y = 0;
  drawScreen(0);
  for (const cap of [alt, space]) cap.visible = false;
  cards.forEach(m => { m.visible = false; });
  const p = motes.p.geometry.attributes.position;
  for (let i = 0; i < p.count; i++) p.array[i * 3 + 1] = ((motes.base[i * 3 + 1] + 7 * motes.speed[i] + 10) % 80) - 10;
  p.needsUpdate = true;
  if (spec.logo) {
    camera.position.set(mid.x, mid.y + 34, mid.z + 112); camera.lookAt(mid.x, mid.y + 17, mid.z);
    logo.style.opacity = 1; logo.style.transform = 'none'; logo.style.filter = 'none';
    renderer.toneMappingExposure = 1.05 * 0.45;
    stillCap.style.display = 'none'; shade.style.opacity = 0;
  } else {
    // close on the island, from this shot's own angle: az (around), el (above), d (away) from the
    // island's middle as it sits on the turned laptop; it lands on the side away from the words
    laptop.updateMatrixWorld(true);
    const sc = screenOverride.scale, target = onScreen(0.5, Math.min(0.45, im.height * sc / 2 / SH));
    const az = spec.az * Math.PI / 180, el = spec.el * Math.PI / 180;
    // far enough that the island fills its share of the frame (about half), whatever its size
    const islandW = im.width * sc / SW * SPW;
    spec = { ...spec, d: spec.d ?? islandW / (1.02 * (spec.fill ?? 0.46)) };
    camera.up.set(0, 1, 0);
    camera.position.set(target.x + spec.d * Math.sin(az) * Math.cos(el), target.y + spec.d * Math.sin(el), target.z + spec.d * Math.cos(az) * Math.cos(el));
    camera.lookAt(target);
    const right = new THREE.Vector3(1, 0, 0).applyQuaternion(camera.quaternion), up = new THREE.Vector3(0, 1, 0).applyQuaternion(camera.quaternion);
    const textRight = spec.textSide === 'right';
    const side = spec.textSide === 'center' ? 0 : (textRight ? 1 : -1) * 0.21 * 1.02;
    camera.lookAt(target.clone().addScaledVector(right, side * spec.d).addScaledVector(up, -(spec.lift ?? 0.04) * spec.d));
    camera.rotateZ((spec.roll ?? 0) * Math.PI / 180);
    logo.style.opacity = 0;
    renderer.toneMappingExposure = 1.05;
    const textRight2 = spec.textSide === 'right';
    stillCap.style.left = textRight2 ? 'auto' : '150px'; stillCap.style.right = textRight2 ? '150px' : 'auto'; stillCap.style.textAlign = textRight2 ? 'right' : 'left';
    stillCap.style.top = (spec.y ?? 470) + "px"; stillCap.style.display = 'block';
    shade.style.background = `linear-gradient(${textRight2 ? 270 : 90}deg in oklch, rgba(3,4,8,0.78) 0%, rgba(3,4,8,0.5) 32%, rgba(3,4,8,0) 58%)`;
    if (spec.art) { stillCap.style.display = 'none'; }
    stillCap.innerHTML = `<span class="line lead" style="font-size:70px">${spec.lead}</span><span class="line" style="font-size:70px">${spec.line}</span><span class="sub" style="font-size:28px;max-width:640px;white-space:normal;${textRight2 ? 'margin-left:auto' : ''}">${spec.sub}</span>`;
    shade.style.opacity = spec.art ? 0 : 1;
  }
  bloom.strength = 0.5;
  fade.style.opacity = 0;
  composer.render();
  return true;
};
window.ready = true;
