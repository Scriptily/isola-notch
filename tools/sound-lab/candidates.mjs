// Isola's sound candidates, to listen through and pick from:
//   node tools/sound-lab/candidates.mjs
// writes .work/sound-candidates/<kind>/*.wav and an index.html to play them.
// "remake" marks the four kept from before (bubble, classic, sweep, click),
// rebuilt in high quality.
import { writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { N, hz, shot, loop, mallet, bell, pluck, fm, glide, tone, click, air, wav, RATE } from './synth.mjs';

const OUT = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '.work', 'sound-candidates');

const sounds = {
  notification: [
    ['Bubble', 'remake', 'The quick upward glide, now rounder, with a water-drop resonance and a soft room', () => shot(0.6, (t, a) => {
      glide(t, a, hz(N.E5), hz(N.E6), { dur: 0.32, amp: 0.85, decay: 8, curve: 1.8, harmonic: 0.08 });
      glide(t, a + 0.012, hz(N.B5), hz(N.B6), { dur: 0.18, amp: 0.18, decay: 14, pan: 0.2 });
    }, { wet: 0.14 })],
    ['Bubble pair', 'new', 'Two droplets, the second a fifth higher', () => shot(0.7, (t, a) => {
      glide(t, a, hz(N.Cs5), hz(N.Cs6), { dur: 0.22, amp: 0.8, decay: 11, curve: 2 });
      glide(t, a + 0.13, hz(N.Gs5), hz(N.Gs6), { dur: 0.24, amp: 0.75, decay: 10, curve: 2, pan: 0.15 });
    }, { wet: 0.16 })],
    ['Pop (fixed)', 'fixed', 'The old pop with its full ring-out instead of the sharp stop', () => shot(0.8, (t, a) => {
      mallet(t, a, hz(N.A6), { dur: 0.7, amp: 0.9, bright: 0.7 });
      click(t, a, { amp: 0.15, pitch: 5000, body: 1800, len: 0.006 });
    }, { wet: 0.12 })],
    ['Glass ping', 'new', 'One clear, high glass note', () => shot(1.4, (t, a) => {
      fm(t, a, hz(N.B6), { dur: 1.3, amp: 0.6, ratio: 3.01, index: 1.4, indexDecay: 6, decay: 3.2 });
    }, { wet: 0.2 })],
    ['Duo chime', 'new', 'A rising two-note mallet chime, warm', () => shot(1.0, (t, a) => {
      mallet(t, a, hz(N.B5), { dur: 0.9, amp: 0.7, pan: -0.15 });
      mallet(t, a + 0.1, hz(N.E6), { dur: 1.0, amp: 0.75, pan: 0.15 });
    }, { wet: 0.18 })],
    ['Sparkle', 'new', 'Three quick, high bell notes, like a tiny wind chime', () => shot(1.4, (t, a) => {
      [N.E7, N.B6, N.Gs7].forEach((n, i) => bell(t, a + i * 0.06, hz(n) / 1.79, { dur: 1.1, amp: 0.45, pan: [-0.3, 0.1, 0.35][i] }));
    }, { wet: 0.22 })],
    ['Pluck', 'new', 'A soft plucked string', () => shot(1.2, (t, a) => {
      pluck(t, a, hz(N.E5), { dur: 1.1, amp: 0.8, bright: 0.45, damp: 0.995 });
      pluck(t, a + 0.008, hz(N.B5), { dur: 0.9, amp: 0.35, bright: 0.4, pan: 0.2, damp: 0.994 });
    }, { wet: 0.15 })],
    ['Tap', 'new', 'A light wooden knock, the quietest choice', () => shot(0.4, (t, a) => {
      click(t, a, { amp: 0.7, pitch: 3500, body: 1100, len: 0.014 });
      mallet(t, a, hz(N.Fs6), { dur: 0.22, amp: 0.25, bright: 0.4 });
    }, { wet: 0.08 })],
    ['Crystal', 'new', 'An FM bell, bright at first and mellowing', () => shot(1.6, (t, a) => {
      fm(t, a, hz(N.Gs5), { dur: 1.5, amp: 0.55, ratio: 2.76, index: 3, indexDecay: 3.5, decay: 2.4 });
      fm(t, a + 0.09, hz(N.Cs6), { dur: 1.4, amp: 0.45, ratio: 2.76, index: 2.4, indexDecay: 4, decay: 2.6, pan: 0.2 });
    }, { wet: 0.2 })],
    ['Bloom', 'new', 'A short, soft chord that swells in', () => shot(1.2, (t, a) => {
      // held longer than the first try, which ended too soon (as Isola plays it)
      [N.E5, N.Gs5, N.B5, N.Ds6].forEach((n, i) => fm(t, a + i * 0.025, hz(n), { dur: 2.0, amp: 0.3, ratio: 1, index: 0.6, indexDecay: 2, decay: 1.8, attack: 0.05, pan: -0.3 + i * 0.2 }));
      air(t, a, { dur: 0.5, amp: 0.05, cutoff: 5000 });
    }, { wet: 0.25 })],
  ],

  call: [
    ['Classic', 'remake', "An old phone's double trill, smoother and fuller", () => loop(3.0, (t, a) => {
      for (let b = 0; b < 2; b++) for (let i = 0; i < 12; i++)
        tone(t, a + b * 0.55 + i * 0.038, i % 2 ? hz(N.Cs6) : hz(N.E6), { dur: 0.034, amp: 0.4, attack: 0.003, release: 0.008, odd: 3 });
    }, { wet: 0.12 })],
    ['Classic warm', 'new', 'The same trill, lower and rounder', () => loop(3.0, (t, a) => {
      for (let b = 0; b < 2; b++) for (let i = 0; i < 12; i++)
        tone(t, a + b * 0.55 + i * 0.04, i % 2 ? hz(N.Gs5) : hz(N.B5), { dur: 0.036, amp: 0.45, attack: 0.004, release: 0.01, odd: 2 });
    }, { wet: 0.14 })],
    ['Marimba dance', 'new', 'A bouncing marimba phrase and its answer', () => loop(2.8, (t, a) => {
      const notes = [N.E5, N.B5, N.Gs5, N.E6, N.B5, N.Gs5, N.Fs5, N.Cs6, N.A5, N.Fs6];
      const at = [0, 0.13, 0.26, 0.39, 0.52, 0.65, 1.1, 1.23, 1.36, 1.49];
      notes.forEach((n, i) => mallet(t, a + at[i], hz(n), { dur: 0.7, amp: 0.7, pan: (i % 3 - 1) * 0.25 }));
    }, { wet: 0.15 })],
    ['Kalimba', 'new', 'A thumb-piano melody, gentle', () => loop(3.2, (t, a) => {
      [[0, N.C6], [0.16, N.E6], [0.32, N.G6], [0.48, N.E6], [0.8, N.D6], [0.96, N.F6], [1.12, N.A6], [1.28, N.F6]]
        .forEach(([at, n], i) => pluck(t, a + at, hz(n), { dur: 1.2, amp: 0.7, bright: 0.3, damp: 0.997, pan: (i % 2 ? 0.2 : -0.2), seed: i + 2 }));
    }, { wet: 0.2 })],
    ['Harp rise', 'new', 'A harp sweeping up, then a breath', () => loop(3.0, (t, a) => {
      [N.C5, N.E5, N.G5, N.B5, N.D6, N.E6, N.G6, N.B6].forEach((n, i) => pluck(t, a + i * 0.075, hz(n), { dur: 1.6, amp: 0.55, bright: 0.55, damp: 0.998, pan: -0.4 + i * 0.11, seed: i + 5 }));
    }, { wet: 0.25 })],
    ['Bells', 'new', 'A bell arpeggio, clearer than before', () => loop(3.0, (t, a) => {
      [N.E6, N.B5, N.Gs5, N.B5, N.E6, N.Gs6].forEach((n, i) => bell(t, a + i * 0.18, hz(n) / 1.79, { dur: 1.4, amp: 0.55, pan: (i % 2 ? 0.25 : -0.25) }));
    }, { wet: 0.2 })],
    ['Digital', 'new', 'A modern electronic ring: quick rising triplets', () => loop(2.6, (t, a) => {
      for (let r = 0; r < 2; r++) [N.B5, N.Ds6, N.Fs6].forEach((n, i) => fm(t, a + r * 0.5 + i * 0.075, hz(n), { dur: 0.25, amp: 0.55, ratio: 2, index: 1.2, indexDecay: 8, decay: 12 }));
    }, { wet: 0.12 })],
    ['Morning', 'new', 'A soft, unhurried bell melody', () => loop(3.6, (t, a) => {
      [[0, N.G5], [0.3, N.B5], [0.6, N.D6], [0.9, N.G6], [1.5, N.Fs6], [1.8, N.D6]]
        .forEach(([at, n], i) => fm(t, a + at, hz(n), { dur: 1.4, amp: 0.5, ratio: 3.5, index: 1.6, indexDecay: 3, decay: 2.2, pan: (i % 2 ? 0.2 : -0.2) }));
    }, { wet: 0.22 })],
    ['Pulse', 'new', 'Soft pulses climbing in threes', () => loop(2.4, (t, a) => {
      for (let r = 0; r < 2; r++) for (let i = 0; i < 3; i++) tone(t, a + r * 0.9 + i * 0.22, hz(N.B5 + i * 2), { dur: 0.16, amp: 0.45, attack: 0.02, release: 0.06, odd: 2 });
    }, { wet: 0.16 })],
  ],

  timer: [
    ['Sweep', 'remake', 'Two rising sweeps, smoother, with a room around them', () => loop(1.6, (t, a) => {
      for (let i = 0; i < 2; i++) glide(t, a + i * 0.55, hz(N.E5), hz(N.B6), { dur: 0.5, amp: 0.8, decay: 3, curve: 1.2, harmonic: 0.1 });
    }, { wet: 0.18 })],
    ['Sweep triple', 'new', 'Three quicker sweeps, a little more urgent', () => loop(1.7, (t, a) => {
      for (let i = 0; i < 3; i++) glide(t, a + i * 0.32, hz(N.Gs5), hz(N.E7), { dur: 0.3, amp: 0.75, decay: 5, curve: 1.3 });
    }, { wet: 0.15 })],
    ['Alarm bells', 'new', 'Three bright bells, then quiet', () => loop(1.9, (t, a) => {
      [[0, N.E6], [0.22, N.E6], [0.44, N.B6]].forEach(([at, n]) => bell(t, a + at, hz(n) / 1.79, { dur: 1.2, amp: 0.6 }));
    }, { wet: 0.18 })],
    ['Chime steps', 'new', 'A rising four-note mallet figure', () => loop(1.8, (t, a) => {
      [N.E6, N.Gs6, N.B6, N.E7].forEach((n, i) => mallet(t, a + i * 0.12, hz(n), { dur: 0.8, amp: 0.7, pan: -0.3 + i * 0.2 }));
    }, { wet: 0.18 })],
    ['Beacon', 'new', 'A calm pulsing tone, like a lighthouse', () => loop(2.0, (t, a) => {
      for (let i = 0; i < 2; i++) fm(t, a + i * 0.45, hz(N.A6), { dur: 0.42, amp: 0.55, ratio: 1, index: 0.4, decay: 6, attack: 0.03 });
    }, { wet: 0.2 })],
    ['Clock', 'new', "An alarm clock's four beeps, softened", () => loop(1.5, (t, a) => {
      for (let i = 0; i < 4; i++) tone(t, a + i * 0.15, hz(N.A6), { dur: 0.085, amp: 0.45, attack: 0.004, release: 0.02, odd: 3 });
    }, { wet: 0.1 })],
    ['Marimba wake', 'new', 'A bouncy marimba figure', () => loop(1.7, (t, a) => {
      [N.E6, N.B5, N.E6, N.B5, N.Gs5, N.B5].forEach((n, i) => mallet(t, a + i * 0.12, hz(n), { dur: 0.6, amp: 0.7, pan: (i % 2 ? 0.2 : -0.2) }));
    }, { wet: 0.15 })],
    ['Glass cascade', 'new', 'Glass notes tumbling down', () => loop(2.0, (t, a) => {
      [N.B6, N.Gs6, N.E6, N.B5, N.Gs5].forEach((n, i) => fm(t, a + i * 0.09, hz(n), { dur: 1.0, amp: 0.5, ratio: 3.01, index: 1.6, indexDecay: 5, decay: 3, pan: 0.3 - i * 0.15 }));
    }, { wet: 0.22 })],
  ],

  connect: [
    ['Click', 'remake', 'Two crisp clicks rising, now with a little body', () => shot(0.35, (t, a) => {
      click(t, a, { amp: 0.6, pitch: 3000, body: hz(N.E6), len: 0.01 });
      click(t, a + 0.085, { amp: 0.7, pitch: 3800, body: hz(N.B6), len: 0.01, seed: 9 });
    }, { wet: 0.08 })],
    ['Click soft', 'new', 'The clicks, quieter and rounder', () => shot(0.35, (t, a) => {
      click(t, a, { amp: 0.5, pitch: 1800, body: hz(N.E5), len: 0.014 });
      click(t, a + 0.09, { amp: 0.55, pitch: 2200, body: hz(N.B5), len: 0.014, seed: 9 });
    }, { wet: 0.08 })],
    ['Rise', 'new', 'Two mallet notes stepping up', () => shot(0.8, (t, a) => {
      mallet(t, a, hz(N.E5), { dur: 0.6, amp: 0.75 }); mallet(t, a + 0.09, hz(N.B5), { dur: 0.7, amp: 0.8, pan: 0.15 });
    }, { wet: 0.15 })],
    ['Glass rise', 'new', 'Two glass notes stepping up', () => shot(1.0, (t, a) => {
      fm(t, a, hz(N.E6), { dur: 0.8, amp: 0.5, ratio: 3.01, index: 1.2, indexDecay: 6, decay: 4 });
      fm(t, a + 0.08, hz(N.B6), { dur: 0.9, amp: 0.55, ratio: 3.01, index: 1.2, indexDecay: 6, decay: 4, pan: 0.2 });
    }, { wet: 0.2 })],
    ['Bubble up', 'new', 'A droplet that rises', () => shot(0.5, (t, a) => {
      glide(t, a, hz(N.B4), hz(N.B5), { dur: 0.25, amp: 0.85, decay: 10, curve: 2 });
    }, { wet: 0.12 })],
    ['Pluck up', 'new', 'Two plucks rising', () => shot(0.9, (t, a) => {
      pluck(t, a, hz(N.E5), { dur: 0.7, amp: 0.7, bright: 0.45 }); pluck(t, a + 0.08, hz(N.B5), { dur: 0.8, amp: 0.7, bright: 0.45, pan: 0.2, seed: 6 });
    }, { wet: 0.15 })],
  ],

  disconnect: [
    ['Click', 'remake', 'Two crisp clicks falling', () => shot(0.35, (t, a) => {
      click(t, a, { amp: 0.7, pitch: 3800, body: hz(N.B6), len: 0.01, seed: 9 });
      click(t, a + 0.085, { amp: 0.6, pitch: 3000, body: hz(N.E6), len: 0.01 });
    }, { wet: 0.08 })],
    ['Click soft', 'new', 'The clicks falling, quieter and rounder', () => shot(0.35, (t, a) => {
      click(t, a, { amp: 0.55, pitch: 2200, body: hz(N.B5), len: 0.014, seed: 9 });
      click(t, a + 0.09, { amp: 0.5, pitch: 1800, body: hz(N.E5), len: 0.014 });
    }, { wet: 0.08 })],
    ['Fall', 'new', 'Two mallet notes stepping down', () => shot(0.8, (t, a) => {
      mallet(t, a, hz(N.B5), { dur: 0.6, amp: 0.8, pan: 0.15 }); mallet(t, a + 0.09, hz(N.E5), { dur: 0.7, amp: 0.75 });
    }, { wet: 0.15 })],
    ['Glass fall', 'new', 'Two glass notes stepping down', () => shot(1.0, (t, a) => {
      fm(t, a, hz(N.B6), { dur: 0.8, amp: 0.55, ratio: 3.01, index: 1.2, indexDecay: 6, decay: 4, pan: 0.2 });
      fm(t, a + 0.08, hz(N.E6), { dur: 0.9, amp: 0.5, ratio: 3.01, index: 1.2, indexDecay: 6, decay: 4 });
    }, { wet: 0.2 })],
    ['Bubble down', 'new', 'A droplet that sinks', () => shot(0.5, (t, a) => {
      glide(t, a, hz(N.B5), hz(N.B4), { dur: 0.28, amp: 0.85, decay: 10, curve: 2 });
    }, { wet: 0.12 })],
    ['Pluck down', 'new', 'Two plucks falling', () => shot(0.9, (t, a) => {
      pluck(t, a, hz(N.B5), { dur: 0.7, amp: 0.7, bright: 0.45, pan: 0.2, seed: 6 }); pluck(t, a + 0.08, hz(N.E5), { dur: 0.8, amp: 0.7, bright: 0.45 });
    }, { wet: 0.15 })],
  ],

  charge: [
    ['Click', 'remake', 'Three quick clicks rolling up', () => shot(0.4, (t, a) => {
      [N.E6, N.B6, N.E7].forEach((n, i) => click(t, a + i * 0.035, { amp: 0.55 - i * 0.08, pitch: 3200 + i * 600, body: hz(n), len: 0.009, seed: i + 3 }));
    }, { wet: 0.1 })],
    ['Spark', 'new', 'A quick bright chirp upward', () => shot(0.5, (t, a) => {
      glide(t, a, hz(N.E5), hz(N.E7), { dur: 0.16, amp: 0.7, decay: 9, curve: 1.4 });
      click(t, a, { amp: 0.2, pitch: 6000, body: 2500, len: 0.005 });
    }, { wet: 0.14 })],
    ['Chord roll', 'new', 'Three notes rolled up, quick', () => shot(0.9, (t, a) => {
      [N.E5, N.B5, N.E6].forEach((n, i) => mallet(t, a + i * 0.018, hz(n), { dur: 0.7, amp: 0.6 - i * 0.1, pan: -0.2 + i * 0.2 }));
    }, { wet: 0.16 })],
    ['Glass roll', 'new', 'Three glass notes rolled up', () => shot(1.1, (t, a) => {
      [N.E6, N.B6, N.E7].forEach((n, i) => fm(t, a + i * 0.03, hz(n), { dur: 0.9, amp: 0.45, ratio: 3.01, index: 1, indexDecay: 6, decay: 4, pan: -0.2 + i * 0.2 }));
    }, { wet: 0.2 })],
    ['Energy', 'new', 'A soft swell rising into a note', () => shot(0.8, (t, a) => {
      air(t, a, { dur: 0.25, amp: 0.12, cutoff: 6000 });
      glide(t, a + 0.08, hz(N.B4), hz(N.B5), { dur: 0.4, amp: 0.6, decay: 4, attack: 0.03, curve: 1.2 });
    }, { wet: 0.18 })],
  ],

  low: [
    ['Click', 'remake', 'Two clicks falling, slower: a gentle nudge', () => shot(0.5, (t, a) => {
      click(t, a, { amp: 0.6, pitch: 2800, body: hz(N.E6), len: 0.012 });
      click(t, a + 0.18, { amp: 0.6, pitch: 2000, body: hz(N.B5), len: 0.012, seed: 4 });
    }, { wet: 0.1 })],
    ['Fall', 'new', 'Two mallet notes falling, unhurried', () => shot(1.0, (t, a) => {
      mallet(t, a, hz(N.E5), { dur: 0.7, amp: 0.8 }); mallet(t, a + 0.18, hz(N.B4), { dur: 0.8, amp: 0.8, pan: -0.15 });
    }, { wet: 0.16 })],
    ['Bell warning', 'new', 'One low, soft bell', () => shot(1.6, (t, a) => {
      bell(t, a, hz(N.Gs4) / 1.79 * 2, { dur: 1.5, amp: 0.6 });
    }, { wet: 0.2 })],
    ['Sink', 'new', 'A tone that slowly drops', () => shot(0.8, (t, a) => {
      glide(t, a, hz(N.E5), hz(N.E4), { dur: 0.5, amp: 0.75, decay: 4, curve: 1, attack: 0.01 });
    }, { wet: 0.15 })],
    ['Double tap', 'new', 'Two low taps', () => shot(0.6, (t, a) => {
      for (let i = 0; i < 2; i++) { click(t, a + i * 0.16, { amp: 0.6, pitch: 1500, body: hz(N.E4), len: 0.02, seed: i + 8 }); mallet(t, a + i * 0.16, hz(N.E5), { dur: 0.3, amp: 0.3 }); }
    }, { wet: 0.1 })],
  ],
};

const LABEL = { notification: 'Notifications', call: 'Ringtones (they loop)', timer: 'Timer alarms (they loop)', connect: 'A device connects', disconnect: 'A device disconnects', charge: 'Charger plugged in', low: 'Low battery' };
const BADGE = { remake: 'Remade', fixed: 'Fixed', new: 'New' };

rmSync(OUT, { recursive: true, force: true });
const rows = [];
for (const [kind, list] of Object.entries(sounds)) {
  mkdirSync(join(OUT, kind), { recursive: true });
  list.forEach(([name, badge, note, make], i) => {
    const t0 = performance.now();
    const t = make();
    const file = `${String(i + 1).padStart(2, '0')} ${name}.wav`;
    writeFileSync(join(OUT, kind, file), wav(t));
    rows.push({ kind, file, name, badge, note, sec: t.L.length / RATE });
    process.stdout.write(`${kind}/${file} (${(t.L.length / RATE).toFixed(2)} s, ${Math.round(performance.now() - t0)} ms)\n`);
  });
}

// a page to listen with: every sound, its kind, what it is, and loops looping
const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Isola sound candidates</title>
<style>
:root{color-scheme:dark;--bg:oklch(14% .005 275);--card:oklch(19% .006 275);--line:rgb(255 255 255/.08);--text:oklch(96% .003 275);--dim:rgb(255 255 255/.55);--blue:oklch(66% .17 255);--green:oklch(72% .17 152);--amber:oklch(76% .15 75)}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
main{max-width:860px;margin:0 auto;padding:32px 20px 80px}h1{font:600 26px/1.2 "Segoe UI Variable Display","Segoe UI",sans-serif;margin:0 0 6px}
p.lede{color:var(--dim);margin:0 0 28px}h2{font:700 12px/1 "Segoe UI",sans-serif;letter-spacing:.06em;text-transform:uppercase;color:var(--dim);margin:30px 0 10px}
.row{display:grid;grid-template-columns:44px minmax(0,1fr) auto;gap:14px;align-items:center;padding:12px 14px;border-radius:14px;background:var(--card);border:1px solid var(--line);margin-bottom:8px}
button{width:44px;height:44px;border-radius:12px;border:1px solid var(--line);background:rgb(255 255 255/.06);color:var(--text);cursor:pointer;display:grid;place-items:center}
button:hover{background:rgb(255 255 255/.12)}button.on{background:var(--blue);border-color:transparent}
svg{width:18px;height:18px;fill:currentColor}.name{font-weight:600}.note{color:var(--dim);font-size:13px}
.badge{font-size:11.5px;font-weight:600;padding:3px 8px;border-radius:7px;border:1px solid var(--line);color:var(--dim)}
.badge.remake{color:var(--green);border-color:color-mix(in oklch,var(--green) 40%,transparent)}.badge.fixed{color:var(--amber);border-color:color-mix(in oklch,var(--amber) 40%,transparent)}
.meta{display:flex;align-items:center;gap:10px}.sec{color:var(--dim);font-size:12px;font-variant-numeric:tabular-nums}
</style></head><body><main>
<h1>Isola sound candidates</h1>
<p class="lede">Press a sound to hear it; ringtones and timer alarms loop until you press again. Tell Claude which ones to use by their number and name, for example "notification 01 Bubble". The files are in this folder too.</p>
${Object.keys(sounds).map(kind => `<h2>${LABEL[kind]}</h2>` + rows.filter(r => r.kind === kind).map(r => `
<div class="row"><button data-src="${esc(kind + '/' + r.file)}" data-loop="${kind === 'call' || kind === 'timer'}" aria-label="Play ${esc(r.name)}"><svg viewBox="0 0 24 24"><path d="M8 5.5v13l11-6.5z"/></svg></button>
<div><div class="name">${esc(r.file.replace('.wav', ''))}</div><div class="note">${esc(r.note)}</div></div>
<div class="meta"><span class="sec">${r.sec.toFixed(1)} s</span><span class="badge ${r.badge}">${BADGE[r.badge]}</span></div></div>`).join('')).join('')}
</main><script>
let cur = null, btn = null;
const play = '<svg viewBox="0 0 24 24"><path d="M8 5.5v13l11-6.5z"/></svg>', stop = '<svg viewBox="0 0 24 24"><rect x="6.5" y="6.5" width="11" height="11" rx="2"/></svg>';
document.addEventListener('click', e => {
  const b = e.target.closest('button[data-src]'); if (!b) return;
  if (cur) { cur.pause(); btn.classList.remove('on'); btn.innerHTML = play; }
  if (btn === b) { cur = btn = null; return; }
  cur = new Audio(encodeURI(b.dataset.src)); cur.loop = b.dataset.loop === 'true'; cur.play();
  btn = b; b.classList.add('on'); b.innerHTML = stop;
  cur.onended = () => { b.classList.remove('on'); b.innerHTML = play; cur = btn = null; };
});
</script></body></html>`;
writeFileSync(join(OUT, 'index.html'), html);
console.log(`\n${rows.length} sounds in ${OUT}`);
