// Звуки без файлов (друг 29.09: «пуки и рыги, как в Рике и Морти; еда — чипсы, рыба, чеснок»). Всё синтезируется
// тут же, каждый раз чуть по-своему (случайные высота, длина, дрожь), поэтому не надоедает одним и тем же сэмплом.
//  - рыг: неровные толчки пищеводного сфинктера (низкий «голос» 70–110 Гц с сильной дрожью периода и громкости)
//    через резонансы тёмной гласной «ɜ/ɔ» (форманты ~550/1000/2400 Гц), трепет 6–9 Гц и влажный шум;
//  - пук: хлопки воздуха 55–160 Гц (каждый — короткая вспышка шума) через низкий резонанс, контур высоты «пр-р-рт»,
//    на хвосте — сипящий выдох;
//  - хруст (чипсы): щелчки звонкого шума 1,5–5 кГц пачкой за 80–150 мс плюс глухой удар;
//  - чавк (рыба, жевание): мягкие вспышки шума ниже 900 Гц.
// Звук идёт мимо анализатора губ — рот двигает жест, а не звук.

const rnd = (a, b) => a + Math.random() * (b - a);

/** Двухполюсный резонатор (полосовой, единичное усиление на центре). */
function resonator(x, sr, f, bw, gain = 1) {
  const r = Math.exp(-Math.PI * bw / sr);
  const c = 2 * r * Math.cos(2 * Math.PI * f / sr);
  const g = (1 - r * r) * gain;
  const y = new Float32Array(x.length);
  let y1 = 0, y2 = 0;
  for (let i = 0; i < x.length; i++) {
    const v = g * x[i] + c * y1 - r * r * y2;
    y[i] = v; y2 = y1; y1 = v;
  }
  return y;
}
function lowpass(x, sr, f) {
  const a = 1 - Math.exp(-2 * Math.PI * f / sr);
  const y = new Float32Array(x.length);
  let s = 0;
  for (let i = 0; i < x.length; i++) { s += a * (x[i] - s); y[i] = s; }
  return y;
}
function highpass(x, sr, f) {
  const lp = lowpass(x, sr, f);
  const y = new Float32Array(x.length);
  for (let i = 0; i < x.length; i++) y[i] = x[i] - lp[i];
  return y;
}
function normalize(x, peak = 0.9) {
  let m = 0;
  for (const v of x) m = Math.max(m, Math.abs(v));
  if (m > 0) for (let i = 0; i < x.length; i++) x[i] *= peak / m;
  return x;
}
function mix(...parts) {
  const n = Math.max(...parts.map(([a]) => a.length));
  const y = new Float32Array(n);
  for (const [a, k] of parts) for (let i = 0; i < a.length; i++) y[i] += a[i] * k;
  return y;
}

/** Рыг. long 0..1 — от короткого «ык» до протяжного «бу-у-ургх». */
export function burpSamples(sr, long = Math.random()) {
  const dur = 0.35 + long * 0.75;
  const n = Math.floor(sr * dur);
  const src = new Float32Array(n);
  const f0a = rnd(85, 118), f0b = f0a * rnd(0.62, 0.8);
  let t = 0;
  // толчки: как голос, только период гуляет на ±15 % и то и дело «проваливается» вдвое — хрип
  while (t < n) {
    const u = t / n;
    const f0 = f0a + (f0b - f0a) * u + Math.sin(u * 30) * 4;
    let period = sr / f0 * (1 + (Math.random() - 0.5) * 0.3);
    if (Math.random() < 0.12) period *= 2;
    const amp = 0.55 + Math.random() * 0.6;
    const open = Math.floor(period * 0.45);
    for (let k = 0; k < open && t + k < n; k++) {
      const ph = k / open;
      src[t + k] += amp * (ph < 0.8 ? Math.sin(Math.PI * ph / 0.8) ** 2 : -(1 - ph) * 2.5);   // импульс Розенберга
    }
    t += Math.max(4, Math.floor(period));
  }
  // шум «мокроты»
  const noise = new Float32Array(n);
  for (let i = 0; i < n; i++) noise[i] = (Math.random() * 2 - 1);
  const wet = resonator(noise, sr, rnd(400, 700), 300, 0.5);
  const vowel = mix(
    [resonator(src, sr, rnd(480, 620), 110), 1],
    [resonator(src, sr, rnd(900, 1150), 140), 0.55],
    [resonator(src, sr, rnd(2200, 2600), 220), 0.18],
    [lowpass(src, sr, 180), 0.8],
  );
  const y = mix([vowel, 1], [wet, 0.25 + long * 0.2]);
  // огибающая: резкая атака, трепет 6–9 Гц, затухание
  const flutter = rnd(6, 9), att = 0.025, rel = 0.12 + long * 0.1;
  for (let i = 0; i < n; i++) {
    const s = i / sr;
    const env = Math.min(1, s / att) * Math.min(1, (dur - s) / rel) * (0.78 + 0.22 * Math.sin(2 * Math.PI * flutter * s));
    y[i] *= Math.max(0, env);
  }
  return normalize(y, 0.85);
}

/** Пук. kind: 'short' (пфт), 'long' (пр-р-р-рт), 'squeak' (писклявый). */
export function fartSamples(sr, kind = ['short', 'long', 'long', 'squeak'][Math.floor(Math.random() * 4)]) {
  const dur = kind === 'short' ? rnd(0.25, 0.45) : kind === 'squeak' ? rnd(0.5, 0.9) : rnd(0.8, 1.5);
  const n = Math.floor(sr * dur);
  const src = new Float32Array(n);
  const base = kind === 'squeak' ? rnd(150, 230) : rnd(55, 95);
  let t = 0;
  while (t < n) {
    const u = t / n;
    // контур: подъём и спад высоты, у длинного — пара «перекатов»
    const contour = kind === 'long' ? 1 + 0.35 * Math.sin(u * Math.PI * rnd(2.5, 3.5)) - 0.25 * u : 1 + 0.4 * u - 0.6 * u * u;
    const f = base * contour;
    const period = sr / f * (1 + (Math.random() - 0.5) * 0.35);
    const burst = Math.floor(sr * rnd(0.0015, 0.004));
    const amp = 0.5 + Math.random() * 0.7;
    for (let k = 0; k < burst && t + k < n; k++) src[t + k] += amp * (Math.random() * 2 - 1) * Math.exp(-k / (burst * 0.35));
    t += Math.max(3, Math.floor(period));
  }
  const body = mix(
    [resonator(src, sr, kind === 'squeak' ? rnd(500, 800) : rnd(160, 260), 120), 1],
    [resonator(src, sr, rnd(380, 520), 200), 0.45],
    [lowpass(src, sr, 140), 0.6],
  );
  // сипящий хвост «пфф»
  const tailN = Math.floor(sr * rnd(0.08, 0.2));
  const hiss = new Float32Array(n + tailN);
  for (let i = 0; i < hiss.length; i++) {
    const s = (i - (n - tailN * 0.5)) / tailN;
    hiss[i] = s > 0 && s < 1 ? (Math.random() * 2 - 1) * (1 - s) * s * 4 : 0;
  }
  const y = mix([body, 1], [highpass(lowpass(hiss, sr, 3000), sr, 700), 0.12]);
  const att = 0.012;
  for (let i = 0; i < n; i++) {
    const s = i / sr;
    const env = Math.min(1, s / att) * (0.75 + 0.25 * Math.sin(2 * Math.PI * rnd(9, 13) * s)) * Math.min(1, (dur - s) / 0.06 + 0.15);
    y[i] *= Math.max(0, env);
  }
  return normalize(y, 0.9);
}

/** Хруст чипсины. */
export function crunchSamples(sr) {
  const dur = rnd(0.09, 0.16);
  const n = Math.floor(sr * (dur + 0.05));
  const y = new Float32Array(n);
  const clicks = 4 + Math.floor(Math.random() * 6);
  for (let c = 0; c < clicks; c++) {
    const at = Math.floor(sr * rnd(0, dur));
    const len = Math.floor(sr * rnd(0.001, 0.006));
    const f = rnd(1500, 5200);
    const burst = new Float32Array(len);
    for (let k = 0; k < len; k++) burst[k] = (Math.random() * 2 - 1) * Math.exp(-k / (len * 0.3));
    const b = resonator(burst, sr, f, f * 0.6, 1.5);
    const amp = rnd(0.4, 1);
    for (let k = 0; k < len && at + k < n; k++) y[at + k] += b[k] * amp;
  }
  // глухой удар зубов
  const th = Math.floor(sr * 0.03);
  for (let k = 0; k < th; k++) y[k] += Math.sin(2 * Math.PI * 280 * k / sr) * Math.exp(-k / (sr * 0.008)) * 0.35;
  return normalize(y, 0.75);
}

/** Мягкий чавк. */
export function chewSamples(sr) {
  const n = Math.floor(sr * rnd(0.08, 0.14));
  const x = new Float32Array(n);
  for (let i = 0; i < n; i++) x[i] = (Math.random() * 2 - 1) * Math.sin(Math.PI * i / n) ** 2;
  return normalize(resonator(lowpass(x, sr, 900), sr, rnd(300, 500), 250, 1), 0.45);
}

/**
 * Проигрыватель: sfx.play('burp' | 'fart' | 'crunch' | 'chew', {gain}) — сразу в динамики (мимо анализатора губ).
 * volume() — общая громкость (0, когда хозяин выключил звук).
 */
export function createSfx(audio, volume = () => 0.8) {
  const make = { burp: burpSamples, fart: fartSamples, crunch: crunchSamples, chew: chewSamples };
  const level = { burp: 0.9, fart: 0.95, crunch: 0.55, chew: 0.35 };
  return {
    play(name, { gain = 1, arg } = {}) {
      const v = volume();
      if (v <= 0 || !make[name] || audio.state === 'closed') return 0;
      if (audio.state === 'suspended') audio.resume().catch(() => {});
      const data = make[name](audio.sampleRate, arg);
      const buf = audio.createBuffer(1, data.length, audio.sampleRate);
      buf.copyToChannel(data, 0);
      const src = audio.createBufferSource();
      src.buffer = buf;
      const g = audio.createGain();
      g.gain.value = v * gain * level[name];
      src.connect(g).connect(audio.destination);
      src.start();
      return buf.duration;
    },
  };
}

/** WAV (16 бит, моно) — выгрузить синтезированный звук послушать. */
export function toWav(data, sr) {
  const b = new ArrayBuffer(44 + data.length * 2);
  const v = new DataView(b);
  const w = (o, s) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
  w(0, 'RIFF'); v.setUint32(4, 36 + data.length * 2, true); w(8, 'WAVE'); w(12, 'fmt ');
  v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true); v.setUint32(24, sr, true);
  v.setUint32(28, sr * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true); w(36, 'data'); v.setUint32(40, data.length * 2, true);
  for (let i = 0; i < data.length; i++) v.setInt16(44 + i * 2, Math.max(-1, Math.min(1, data[i])) * 32767, true);
  return b;
}
