// The trailer's soundtrack: 30 s at 120 BPM (a bar is 2 s, a section is 2 bars,
// so every scene change lands on a downbeat). Original, made with the sound
// lab's voices plus drums, bass and pads made here. Writes music.wav.
import { writeFileSync } from 'node:fs';
import { RATE, hz, N, track, mallet, bell, pluck, fm, glide, tone, click, air, room, polish, wav } from '../../tools/sound-lab/synth.mjs';

const SEC = 30.5, BEAT = 0.5, BAR = 2;
const TAU = Math.PI * 2;
const drums = track(SEC), bass = track(SEC), keys = track(SEC), pads = track(SEC), fx = track(SEC);

function rng(seed = 1) { let s = seed >>> 0 || 1; return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 2 ** 32) * 2 - 1; }
const put = (t, i, v, pan = 0) => { if (i < 0 || i >= t.L.length) return; const a = (pan + 1) * Math.PI / 4; t.L[i] += v * Math.cos(a); t.R[i] += v * Math.sin(a); };

// ---- drums -------------------------------------------------------------------------------
function kick(at, amp = 1) {
  const s0 = Math.round(at * RATE), n = Math.round(0.42 * RATE); let ph = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE, f = 48 + 110 * Math.exp(-t * 32);
    ph += TAU * f / RATE;
    const env = Math.exp(-t * 7.5) * Math.min(1, t / 0.0015);
    put(drums, s0 + i, amp * 0.95 * env * Math.sin(ph) + amp * 0.25 * Math.exp(-t * 300) * Math.sin(ph * 3));
  }
}
function clap(at, amp = 0.5, seed = 5) {
  const r = rng(seed), s0 = Math.round(at * RATE), n = Math.round(0.3 * RATE); let bp = 0, lp = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE;
    // three quick bursts then a tail, band-passed around 1.5 kHz
    const bursts = [0, 0.011, 0.022].reduce((s, o) => s + (t >= o ? Math.exp(-(t - o) * 180) : 0), 0) * 0.6 + Math.exp(-t * 18) * 0.5;
    const x = r(); lp += (x - lp) * 0.35; bp = x - lp;
    put(drums, s0 + i, amp * bursts * bp, 0.1);
  }
}
function hat(at, amp = 0.16, open = false, seed = 9) {
  const r = rng(seed), s0 = Math.round(at * RATE), n = Math.round((open ? 0.22 : 0.05) * RATE); let prev = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE, x = r(), hp = x - prev; prev = x;
    put(drums, s0 + i, amp * hp * Math.exp(-t * (open ? 14 : 70)), 0.35);
  }
}
function crash(at, amp = 0.32) {
  const r = rng(77), s0 = Math.round(at * RATE), n = Math.round(2.6 * RATE); let prev = 0;
  for (let i = 0; i < n; i++) { const t = i / RATE, x = r(), hp = x - prev; prev = x; put(fx, s0 + i, amp * hp * Math.exp(-t * 1.6) * Math.min(1, t / 0.002), Math.sin(i / 900) * 0.4); }
}

// ---- bass: a warm saw-ish pluck an octave under, pumping with the kick ------------------------
function bassNote(at, semis, dur = 0.42, amp = 0.5) {
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE), f = hz(semis); let ph = 0, lp = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE; ph += f / RATE;
    const saw = 2 * (ph - Math.floor(ph + 0.5)), sub = Math.sin(TAU * ph);
    const cutoff = 1 - Math.exp(-TAU * (180 + 1600 * Math.exp(-t * 14)) / RATE);
    lp += (saw - lp) * cutoff;
    const env = Math.min(1, t / 0.004) * Math.exp(-t * 2.5) * Math.min(1, (dur - t) / 0.03);
    put(bass, s0 + i, amp * env * (0.55 * lp + 0.6 * sub));
  }
}

// ---- pads: detuned soft saws through a slow filter, wide ------------------------------------
function pad(at, notes, dur, amp = 0.09, open = 1) {
  const s0 = Math.round(at * RATE), n = Math.round(dur * RATE);
  notes.forEach((semis, k) => {
    for (const det of [-0.11, 0.0, 0.12]) {
      const f = hz(semis + det); let ph = Math.random(), lp = 0;
      const pan = (k % 2 ? 0.5 : -0.5) * (det === 0 ? 0.3 : 1);
      for (let i = 0; i < n; i++) {
        const t = i / RATE; ph += f / RATE;
        const saw = 2 * (ph - Math.floor(ph + 0.5));
        const c = 1 - Math.exp(-TAU * (500 + 2600 * open * (0.5 - 0.5 * Math.cos(Math.min(1, t / dur) * Math.PI))) / RATE);
        lp += (saw - lp) * c;
        const env = Math.min(1, t / 0.35) * Math.min(1, (dur - t) / 0.4);
        put(pads, s0 + i, amp * env * lp, pan);
      }
    }
  });
}

// ---- effects: a rising noise into each section, an impact on it -------------------------------
function riser(at, dur, amp = 0.22) {
  const r = rng(31), s0 = Math.round(at * RATE), n = Math.round(dur * RATE); let lp = 0;
  for (let i = 0; i < n; i++) {
    const t = i / RATE, k = t / dur, cut = 1 - Math.exp(-TAU * (300 + 9000 * k * k) / RATE);
    lp += (r() - lp) * cut;
    put(fx, s0 + i, amp * k * k * lp, Math.sin(k * 12) * 0.6);
  }
}
function impact(at, amp = 0.8) {
  const s0 = Math.round(at * RATE), n = Math.round(1.6 * RATE); let ph = 0;
  for (let i = 0; i < n; i++) { const t = i / RATE, f = 38 + 60 * Math.exp(-t * 10); ph += TAU * f / RATE; put(fx, s0 + i, amp * Math.exp(-t * 2.8) * Math.sin(ph) * Math.min(1, t / 0.002)); }
  crash(at, 0.22);
}
const whoosh = (at, dur = 0.5) => air(fx, at, { dur, amp: 0.12, cutoff: 5000, seed: Math.round(at * 10) });

// ---- the music ---------------------------------------------------------------------------------
// vi IV I V in E: C#m7, Amaj7, Emaj7, B(add4) - two seconds each
const CHORDS = [
  { root: N.Cs2, pad: [N.Cs4, N.E4, N.Gs4, N.B4], arp: [N.Cs5, N.E5, N.Gs5, N.B5] },
  { root: N.A2 - 12, pad: [N.A3, N.Cs4, N.E4, N.Gs4], arp: [N.A4, N.Cs5, N.E5, N.Gs5] },
  { root: N.E2, pad: [N.E4, N.Gs4, N.B4, N.Ds5], arp: [N.E5, N.Gs5, N.B5, N.Ds6] },
  { root: N.B2 - 12, pad: [N.B3, N.Ds4, N.Fs4, N.A4], arp: [N.B4, N.Ds5, N.Fs5, N.A5] },
];
const chordAt = t => CHORDS[Math.floor(t / BAR) % 4];

// 0-4: the island wakes. Pads swell, a chime as it says hello, a rise into the drop
pad(0, CHORDS[0].pad, 2.1, 0.07, 0.4);
pad(2, CHORDS[1].pad, 2.2, 0.08, 0.7);
[N.E6, N.B5, N.Gs5, N.E6].forEach((n, i) => bell(keys, 0.55 + i * 0.25, hz(n), { dur: 2.2, amp: 0.22, pan: -0.3 + i * 0.2 }));
riser(2.4, 1.6);

// 4-24: the groove
for (let bar = 2; bar < 12; bar++) {
  const t0 = bar * BAR, ch = chordAt(t0), section = Math.floor(t0 / 4);
  pad(t0, ch.pad, BAR + 0.15, 0.06, 1);
  for (let b = 0; b < 4; b++) {
    const t = t0 + b * BEAT;
    kick(t, b === 0 ? 1 : 0.92);
    if (b % 2 === 1) clap(t, 0.42);
    hat(t + BEAT / 2, 0.15, b === 3 && bar % 2 === 1);
    if (section >= 3) { hat(t + BEAT / 4, 0.06, false, 11); hat(t + 3 * BEAT / 4, 0.06, false, 13); }
    // bass on the offbeat eighths, ducking under the kick (the pump)
    bassNote(t + BEAT / 2, ch.root, 0.36, 0.42);
    if (b === 0) bassNote(t, ch.root, 0.2, 0.25);
  }
  // keys: an arpeggio climbing through the chord, brighter as the sections build
  const arpSteps = section >= 4 ? 16 : 8, step = BAR / arpSteps;
  for (let i = 0; i < arpSteps; i++) {
    const n = ch.arp[[0, 1, 2, 3, 2, 1, 3, 2][i % 8]] + (i >= arpSteps / 2 && section >= 5 ? 12 : 0);
    mallet(keys, t0 + i * step, hz(n), { dur: 0.5, amp: section >= 4 ? 0.13 : 0.17, pan: Math.sin(i) * 0.5, bright: 0.8 });
    // a plucked string doubling it an octave up: the bright edge that carries on small speakers
    if (i % 2 === 0) pluck(keys, t0 + i * step, hz(n + 12), { dur: 0.6, amp: 0.07, bright: 0.85, pan: -Math.sin(i) * 0.6, seed: i + 3 });
  }
}
// scene moments
whoosh(3.6); impact(4, 0.75);
// music (4-8): the click that opens Now Playing
click(fx, 5.0, { amp: 0.35, pitch: 3000, body: hz(N.E6), len: 0.01 });
// notifications (8-12): bubbles as they arrive
whoosh(7.6); impact(8, 0.45);
for (const [t, n] of [[8.6, N.E5], [9.6, N.Gs5], [10.6, N.B5], [11.3, N.E6]]) glide(keys, t, hz(n), hz(n + 12), { dur: 0.3, amp: 0.3, decay: 9, curve: 1.8 });
// downloads (12-16): a rising tone with the progress, a chime when it's done
whoosh(11.6); impact(12, 0.45);
glide(keys, 13.0, hz(N.E4), hz(N.E6), { dur: 1.6, amp: 0.12, decay: 0.6, curve: 0.8, attack: 0.2 });
[N.B5, N.E6].forEach((n, i) => mallet(keys, 14.75 + i * 0.12, hz(n), { dur: 1.2, amp: 0.3 }));
// search (16-20): typing, then the answer
whoosh(15.6); impact(16, 0.45);
for (let i = 0; i < 9; i++) click(fx, 16.55 + i * 0.11, { amp: 0.16, pitch: 4200, body: 1400, len: 0.006, seed: 20 + i });
fm(keys, 17.75, hz(N.Gs5), { dur: 0.8, amp: 0.25, ratio: 2, index: 1.2, indexDecay: 6, decay: 5 });
// cards (20-24): one lands on every beat
whoosh(19.6); impact(20, 0.5);
for (let i = 0; i < 4; i++) { air(fx, 20.2 + i, { dur: 0.35, amp: 0.08, cutoff: 7000, seed: 40 + i }); click(fx, 20.5 + i, { amp: 0.25, pitch: 2600, body: hz(N.B5 + i * 2), len: 0.012, seed: 50 + i }); }
// a fill into the finale
for (let i = 0; i < 8; i++) clap(23 + i * 0.125, 0.12 + i * 0.04, 60 + i);
riser(22.4, 1.6, 0.26);

// 24-30: the logo. A big hit on E, the drums drop away, the chord rings out
impact(24, 0.95);
kick(24, 1.1);
pad(24, [N.E3, N.B3, N.E4, N.Gs4, N.B4, N.Ds5], 6.2, 0.07, 1.2);
bassNote(24, N.E2 - 12, 3.5, 0.5);
[N.E5, N.Gs5, N.B5, N.Ds6, N.E6].forEach((n, i) => bell(keys, 24.25 + i * 0.16, hz(n), { dur: 4, amp: 0.2, pan: -0.4 + i * 0.2 }));
for (let b = 0; b < 4; b++) { kick(24 + b * BEAT, 0.7 - b * 0.12); hat(24 + b * BEAT + 0.25, 0.08); }
mallet(keys, 26.5, hz(N.E6), { dur: 2.5, amp: 0.18 });
mallet(keys, 26.75, hz(N.B6), { dur: 2.5, amp: 0.14 });
bell(keys, 28, hz(N.E5), { dur: 3, amp: 0.16 });

// ---- mixing --------------------------------------------------------------------------------------
// the pump: pads and bass dip under each kick, the dance-music breath
const duck = new Float64Array(drums.L.length).fill(1);
for (let t = 4; t < 26; t += BEAT) {
  const s0 = Math.round(t * RATE), n = Math.round(BEAT * RATE);
  for (let i = 0; i < n && s0 + i < duck.length; i++) duck[s0 + i] = 1 - 0.55 * Math.exp(-(i / RATE) * 9);
}
for (const t of [bass, pads]) for (let i = 0; i < duck.length; i++) { t.L[i] *= duck[i]; t.R[i] *= duck[i]; }
room(keys, { wet: 0.22, size: 0.82 });
room(pads, { wet: 0.18, size: 0.86 });
room(fx, { wet: 0.12 });
room(drums, { wet: 0.05 });
const mix = track(SEC);
const gains = [[drums, 0.95], [bass, 0.55], [keys, 1.5], [pads, 1.1], [fx, 0.8]];
for (const [t, g] of gains) for (let i = 0; i < mix.L.length; i++) { mix.L[i] += t.L[i] * g; mix.R[i] += t.R[i] * g; }
polish(mix, { hp: 30, lp: 17000 });
// glue: a gentle saturating limiter, then -1 dBFS, and a fade so it ends in silence at 30 s
let peak = 0;
for (let i = 0; i < mix.L.length; i++) peak = Math.max(peak, Math.abs(mix.L[i]), Math.abs(mix.R[i]));
const drive = 1.6 / peak;
for (let i = 0; i < mix.L.length; i++) {
  const t = i / RATE, fade = t > 28.6 ? Math.max(0, (30 - t) / 1.4) ** 2 : 1;
  mix.L[i] = Math.tanh(mix.L[i] * drive) * fade; mix.R[i] = Math.tanh(mix.R[i] * drive) * fade;
}
peak = 0;
for (let i = 0; i < mix.L.length; i++) peak = Math.max(peak, Math.abs(mix.L[i]), Math.abs(mix.R[i]));
for (let i = 0; i < mix.L.length; i++) { mix.L[i] *= 0.89 / peak; mix.R[i] *= 0.89 / peak; }
const out = { L: mix.L.slice(0, 30 * RATE), R: mix.R.slice(0, 30 * RATE) };
writeFileSync(new URL('./music.wav', import.meta.url), wav(out));
console.log('music.wav: 30 s');
