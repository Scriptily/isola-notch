// A small synthesizer for Isola's sounds: mallets, bells, plucked strings,
// FM tones, glides and soft clicks, through a stereo reverb, mastered so every
// sound ends in silence rather than being cut off. Pure JavaScript, no
// packages. 48 kHz, written as 24-bit stereo WAV.

export const RATE = 48000;
const TAU = Math.PI * 2;

export const hz = n => 440 * 2 ** (n / 12);   // semitones from A4
// note names to semitones from A4, e.g. N.E5
export const N = {};
for (let o = 2; o <= 8; o++) {
  for (const [name, off] of [['C', -9], ['Cs', -8], ['D', -7], ['Ds', -6], ['E', -5], ['F', -4], ['Fs', -3], ['G', -2], ['Gs', -1], ['A', 0], ['As', 1], ['B', 2]])
    N[name + o] = off + (o - 4) * 12;
}

/** A stereo buffer of `sec` seconds. */
export function track(sec) {
  const n = Math.ceil(sec * RATE);
  return { L: new Float64Array(n), R: new Float64Array(n) };
}

// how a voice ends: the last 20 ms fade out, so no note stops dead at the end of its length
const release = (i, n) => { const r = Math.round(0.02 * RATE); return i > n - r ? Math.max(0, (n - i) / r) ** 2 : 1; };

// a sample added at a pan position (-1 left .. 1 right), equal-power
function put(t, i, v, pan) {
  if (i < 0 || i >= t.L.length) return;
  const a = (pan + 1) * Math.PI / 4;
  t.L[i] += v * Math.cos(a); t.R[i] += v * Math.sin(a);
}

// deterministic noise, so a sound renders the same every time
function rng(seed = 1) { let s = seed >>> 0 || 1; return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 2 ** 32) * 2 - 1; }

/** A struck bar: a soft thump and decaying partials, higher ones dying first. */
export function mallet(t, at, f, { dur = 1.2, amp = 0.8, pan = 0, bright = 1, partials = [[1, 1, 1], [3.93, 0.28, 2.4], [9.7, 0.07, 5]] } = {}) {
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE), noise = rng(Math.round(f * 7));
  const phases = partials.map(() => Math.random() * 0);
  for (let i = 0; i < n; i++) {
    const tt = i / RATE;
    let v = 0;
    partials.forEach(([ratio, a, decayMul], k) => {
      if (f * ratio > RATE / 2.2) return;
      const env = Math.exp(-tt * (2.2 + 3.2 * decayMul * bright) / dur * 1.2);
      v += a * env * Math.sin(TAU * f * ratio * tt + phases[k]);
    });
    const attack = Math.min(1, tt / 0.0015);
    const thump = Math.exp(-tt / 0.004) * noise() * 0.05 * bright;
    put(t, s0 + i, amp * (attack * v + thump) * release(i, n), pan);
  }
}

/** A bell: inharmonic partials with their own decays (after Risset). */
export function bell(t, at, f, { dur = 2.2, amp = 0.6, pan = 0, shimmer = 1 } = {}) {
  const parts = [[0.56, 1, 0.9], [0.92, 0.67, 0.8], [1.19, 1, 0.65], [1.71, 1.8, 0.55], [2, 2.67, 0.33], [2.74, 1.67, 0.32], [3, 1.46, 0.25], [3.76, 1.33, 0.2], [4.07, 1.33, 0.15]];
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE);
  for (let i = 0; i < n; i++) {
    const tt = i / RATE;
    let v = 0;
    for (const [ratio, a, d] of parts) {
      const fr = f * ratio * 1.79;
      if (fr > RATE / 2.2) continue;
      const detune = ratio === 0.56 || ratio === 0.92 ? 1 + 0.0015 * shimmer : 1;
      v += a * Math.exp(-tt / (dur * d)) * Math.sin(TAU * fr * detune * tt);
    }
    put(t, s0 + i, amp * 0.12 * Math.min(1, tt / 0.002) * v * release(i, n), pan);
  }
}

/** A plucked string (Karplus-Strong): warm, with a natural decay. */
export function pluck(t, at, f, { dur = 1.6, amp = 0.7, pan = 0, damp = 0.996, bright = 0.5, seed = 3 } = {}) {
  const period = Math.max(2, Math.round(RATE / f)), line = new Float64Array(period), noise = rng(seed + Math.round(f));
  // a softened burst: brightness sets how much high end the pluck starts with
  let last = 0;
  for (let i = 0; i < period; i++) { const x = noise(); last = last + (x - last) * (0.15 + 0.8 * bright); line[i] = last; }
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE);
  let idx = 0, prev = 0;
  for (let i = 0; i < n; i++) {
    const cur = line[idx];
    const next = damp * 0.5 * (cur + prev);
    prev = cur; line[idx] = next; idx = (idx + 1) % period;
    const fadeIn = Math.min(1, i / (RATE * 0.0008));
    put(t, s0 + i, amp * cur * fadeIn * release(i, n), pan);
  }
}

/** FM: a carrier whose brightness (modulation index) fades: electric piano, glassy bells. */
export function fm(t, at, f, { dur = 1.2, amp = 0.6, pan = 0, ratio = 3.5, index = 2.2, indexDecay = 4, decay = 3, attack = 0.002 } = {}) {
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE);
  for (let i = 0; i < n; i++) {
    const tt = i / RATE;
    const idx = index * Math.exp(-tt * indexDecay);
    const env = Math.min(1, tt / attack) * Math.exp(-tt * decay);
    put(t, s0 + i, amp * env * Math.sin(TAU * f * tt + idx * Math.sin(TAU * f * ratio * tt)) * release(i, n), pan);
  }
}

/** A sine gliding from one pitch to another, shaped like a droplet or a sweep. */
export function glide(t, at, f0, f1, { dur = 0.4, amp = 0.8, pan = 0, curve = 1.6, decay = 7, attack = 0.004, harmonic = 0.12 } = {}) {
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE);
  let ph = 0;
  for (let i = 0; i < n; i++) {
    const tt = i / RATE, k = Math.min(1, (tt / dur) * curve);
    const f = f0 * (f1 / f0) ** (1 - (1 - k) ** 2);
    ph += TAU * f / RATE;
    const env = Math.min(1, tt / attack) * Math.exp(-tt * decay) * Math.min(1, (dur - tt) / 0.02);
    put(t, s0 + i, amp * env * (Math.sin(ph) + harmonic * Math.sin(2 * ph)), pan);
  }
}

/** A soft tone with a few odd harmonics (a rounded square): rings, beeps. */
export function tone(t, at, f, { dur = 0.1, amp = 0.5, pan = 0, attack = 0.004, release = 0.012, odd = 4, vibrato = 0 } = {}) {
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE);
  let ph = 0;
  for (let i = 0; i < n; i++) {
    const tt = i / RATE;
    ph += TAU * f * (1 + vibrato * Math.sin(TAU * 5.5 * tt)) / RATE;
    let v = 0;
    for (let h = 1; h <= odd * 2 - 1; h += 2) if (f * h < RATE / 2.4) v += Math.sin(h * ph) / (h * h * 0.8 + 0.2 * h);
    const env = Math.min(1, tt / attack, (dur - tt) / release);
    put(t, s0 + i, amp * Math.max(0, env) * v, pan);
  }
}

/** A tiny click: a short filtered transient with a little body. */
export function click(t, at, { amp = 0.6, pan = 0, pitch = 2400, body = 900, len = 0.012, seed = 7 } = {}) {
  const s0 = Math.round(at * RATE), n = Math.round(len * 6 * RATE), noise = rng(seed);
  let lp = 0;
  const a = 1 - Math.exp(-TAU * pitch / RATE);
  for (let i = 0; i < n; i++) {
    const tt = i / RATE;
    lp += (noise() - lp) * a;
    const env = Math.exp(-tt / (len * 0.35));
    const knock = Math.sin(TAU * body * tt) * Math.exp(-tt / (len * 0.9));
    put(t, s0 + i, amp * (lp * env * 0.9 + knock * 0.55), pan);
  }
}

/** A short noise breath (airy layers). */
export function air(t, at, { dur = 0.5, amp = 0.15, pan = 0, cutoff = 3000, seed = 11 } = {}) {
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE), noise = rng(seed);
  let lp = 0; const a = 1 - Math.exp(-TAU * cutoff / RATE);
  for (let i = 0; i < n; i++) {
    const tt = i / RATE;
    lp += (noise() - lp) * a;
    const env = Math.sin(Math.PI * Math.min(1, tt / dur)) ** 2;
    put(t, s0 + i, amp * lp * env, pan);
  }
}

// ---- the room ------------------------------------------------------------------------------------
// Freeverb (Jezar's): 8 combs and 4 all-passes per side, the right side's
// delays a little longer for width.
function freeverb(input, { size = 0.78, damp = 0.35, spread = 23 } = {}) {
  const combs = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617], alls = [556, 441, 341, 225];
  const scale = RATE / 44100, out = new Float64Array(input.length);
  const make = (lens, extra) => lens.map(l => ({ buf: new Float64Array(Math.round((l + extra) * scale)), i: 0, store: 0 }));
  return side => {
    const cs = make(combs, side * spread), as = make(alls, side * spread);
    for (let n = 0; n < input.length; n++) {
      const x = input[n] * 0.015;
      let y = 0;
      for (const c of cs) {
        const o = c.buf[c.i];
        c.store = o * (1 - damp) + c.store * damp;
        c.buf[c.i] = x + c.store * size;
        c.i = (c.i + 1) % c.buf.length;
        y += o;
      }
      for (const a of as) {
        const b = a.buf[a.i];
        a.buf[a.i] = y + b * 0.5;
        a.i = (a.i + 1) % a.buf.length;
        y = b - y;
      }
      out[n] = y;
    }
    return out;
  };
}

/** Adds a room: wet is how much of it (0.1 a touch, 0.3 a lot). */
export function room(t, { wet = 0.16, size = 0.78, damp = 0.35 } = {}) {
  const mono = t.L.map((v, i) => (v + t.R[i]) / 2);
  const wl = freeverb(mono, { size, damp })(0), wr = freeverb(mono, { size, damp })(1);
  for (let i = 0; i < t.L.length; i++) { t.L[i] = t.L[i] * (1 - wet * 0.5) + wl[i] * wet * 3; t.R[i] = t.R[i] * (1 - wet * 0.5) + wr[i] * wet * 3; }
  return t;
}

// ---- finishing -------------------------------------------------------------------------------------

/** High-pass (removes rumble and DC) then a gentle low-pass to soften any edge. */
export function polish(t, { hp = 60, lp = 14000 } = {}) {
  for (const ch of [t.L, t.R]) {
    let x1 = 0, y1 = 0, l = 0;
    const r = Math.exp(-TAU * hp / RATE), a = 1 - Math.exp(-TAU * lp / RATE);
    for (let i = 0; i < ch.length; i++) {
      const y = r * (y1 + ch[i] - x1); x1 = ch[i]; y1 = y;
      l += (y - l) * a; ch[i] = l;
    }
  }
  return t;
}

/** Cuts trailing silence once the tail is truly gone (-72 dB), then fades the last 40 ms: nothing ends abruptly. */
export function trim(t, { floor = -72, fadeMs = 40 } = {}) {
  const thr = 10 ** (floor / 20);
  let end = t.L.length - 1;
  while (end > 0 && Math.abs(t.L[end]) < thr && Math.abs(t.R[end]) < thr) end--;
  const len = Math.min(t.L.length, end + Math.round(RATE * fadeMs / 1000));
  t.L = t.L.slice(0, len); t.R = t.R.slice(0, len);
  const f = Math.round(RATE * fadeMs / 1000);
  for (let i = 0; i < f && i < len; i++) { const g = (i / f) ** 2; t.L[len - 1 - i] *= g; t.R[len - 1 - i] *= g; }
  return t;
}

/** Peak to -1 dBFS. */
export function normalize(t, peakDb = -1) {
  let p = 0;
  for (let i = 0; i < t.L.length; i++) p = Math.max(p, Math.abs(t.L[i]), Math.abs(t.R[i]));
  const g = p > 0 ? 10 ** (peakDb / 20) / p : 0;
  for (let i = 0; i < t.L.length; i++) { t.L[i] *= g; t.R[i] *= g; }
  return t;
}

/**
 * A seamless loop: the pattern is rendered three times in a row and the
 * middle one kept, so its start already carries the reverb tail of the one
 * before. Played on repeat, there's no seam.
 */
export function loop(period, draw, roomOpts) {
  const t = track(period * 3 + 3);
  for (let k = 0; k < 3; k++) draw(t, k * period);
  room(t, roomOpts); polish(t);
  const s = Math.round(period * RATE);
  const out = { L: t.L.slice(s, 2 * s), R: t.R.slice(s, 2 * s) };
  return normalize(out);
}

/** A one-shot: drawn, put in the room, finished. */
export function shot(sec, draw, roomOpts) {
  const t = track(sec + 2.5);
  draw(t, 0.02);
  room(t, roomOpts); polish(t); trim(t);
  return normalize(t);
}

/** 24-bit stereo WAV bytes. */
export function wav(t) {
  const n = t.L.length, data = n * 2 * 3, b = Buffer.alloc(44 + data);
  b.write('RIFF', 0); b.writeUInt32LE(36 + data, 4); b.write('WAVE', 8); b.write('fmt ', 12);
  b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(2, 22); b.writeUInt32LE(RATE, 24);
  b.writeUInt32LE(RATE * 6, 28); b.writeUInt16LE(6, 32); b.writeUInt16LE(24, 34); b.write('data', 36); b.writeUInt32LE(data, 40);
  let o = 44;
  // a little triangular dither under the 24-bit step
  const d = rng(99);
  for (let i = 0; i < n; i++) for (const v of [t.L[i], t.R[i]]) {
    const x = Math.max(-1, Math.min(1, v)) * 8388607 + (d() + d()) * 0.5;
    b.writeIntLE(Math.round(Math.max(-8388608, Math.min(8388607, x))), o, 3); o += 3;
  }
  return b;
}
