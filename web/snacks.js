// Еда, рыги и пуки (друг 29.09: «добавь пуки и рыги, как в Рике и Морти; еду — чипсы, рыбу, чеснок; после чеснока
// зелёный пар изо рта, и при пуках и рыгах тоже»).
//  - жесты (через gestures.define): chips — пачка в левой лапке, правой таскает чипсины, хрустит; fish — рыбка за хвост,
//    два укуса, жуёт; garlic — зубчик чеснока, жуёт с кислой миной, выдыхает зелёным облаком, и потом минуты две при
//    разговоре изо рта идёт зелёный пар; burp — рыг с зелёным облачком изо рта; fart — пук с зелёным облаком сзади;
//  - звуки — настоящие записи CC0 (web/voices/sfx, отобраны судьёй AudioSet, tools/sfx_collect.py), нет записи — синтез
//    (sfx.js); идут мимо анализатора губ, рот двигает жест;
//  - зелёный газ — мягкие спрайты: всплывают, раздуваются, тают.
import * as THREE from 'three';
import { createSfx } from './sfx.js';

const smooth = (x) => (x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x));
const ramp = (u, a, b) => smooth((u - a) / Math.max(1e-4, b - a));
const bell = (u, a, b, c, d) => ramp(u, a, b) * (1 - ramp(u, c, d));
const TAU = Math.PI * 2;

// ---------- звуки: записи + синтез ----------

function createBank(audio, volume) {
  const synth = createSfx(audio, volume);
  const bank = {};
  const last = {};
  const level = { burp: 0.95, fart: 0.95, crunch: 0.7, chew: 0.5 };
  fetch('./voices/sfx/sfx.json').then((r) => (r.ok ? r.json() : {})).then(async (list) => {
    for (const [kind, names] of Object.entries(list)) {
      bank[kind] = [];
      for (const n of names) {
        try {
          const buf = await audio.decodeAudioData(await (await fetch(`./voices/sfx/${n}.wav`)).arrayBuffer());
          bank[kind].push(buf);
        } catch { /* битый файл — пропускаем */ }
      }
    }
  }).catch(() => {});
  return {
    /** Сыграть звук вида kind; вернуть длительность, с. */
    play(kind, gain = 1) {
      const v = volume();
      if (v <= 0) return 0;
      const list = bank[kind];
      if (!list?.length) return synth.play(kind, { gain });
      if (audio.state === 'suspended') audio.resume().catch(() => {});
      let i = Math.floor(Math.random() * list.length);
      if (list.length > 1 && i === last[kind]) i = (i + 1) % list.length;
      last[kind] = i;
      const src = audio.createBufferSource();
      src.buffer = list[i];
      src.playbackRate.value = 0.94 + Math.random() * 0.12;
      const g = audio.createGain();
      g.gain.value = v * gain * (level[kind] ?? 0.8);
      src.connect(g).connect(audio.destination);
      src.start();
      return list[i].duration / src.playbackRate.value;
    },
    ready: () => Object.keys(bank),
  };
}

// ---------- зелёный газ ----------

function puffTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d');
  // клубок: несколько мягких пятен, а не ровный круг
  for (const [x, y, r, a] of [[32, 32, 26, 0.55], [24, 28, 16, 0.45], [40, 30, 15, 0.45], [30, 40, 14, 0.4], [38, 40, 12, 0.35]]) {
    const gr = g.createRadialGradient(x, y, 0, x, y, r);
    gr.addColorStop(0, `rgba(255,255,255,${a})`);
    gr.addColorStop(1, 'rgba(255,255,255,0)');
    g.fillStyle = gr;
    g.fillRect(0, 0, 64, 64);
  }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

function createGas(scene) {
  const tex = puffTexture();
  const group = new THREE.Group();
  scene.add(group);
  const pool = [];
  for (let i = 0; i < 90; i++) {
    const mat = new THREE.SpriteMaterial({ map: tex, color: 0x9be04a, transparent: true, opacity: 0, depthWrite: false });
    const s = new THREE.Sprite(mat);
    s.visible = false;
    s.renderOrder = 5;
    group.add(s);
    pool.push({ s, mat, on: false, life: 0, max: 1, vel: new THREE.Vector3(), size: 0.02, grow: 3, alpha: 0.5, seed: Math.random() * 10 });
  }
  const colors = [0x9be04a, 0x86d23c, 0xb4ea5c, 0x7cc83a];
  return {
    group,
    /** Облачко: pos — откуда, dir — куда (м/с), n — сколько клубков, size — начальный размер, life — сек. */
    emit(pos, dir, { n = 6, size = 0.02, life = 2.2, spread = 0.05, grow = 4, alpha = 0.5 } = {}) {
      for (let k = 0; k < n; k++) {
        const p = pool.find((x) => !x.on);
        if (!p) return;
        p.on = true; p.life = 0; p.max = life * (0.75 + Math.random() * 0.5);
        p.s.position.copy(pos).add(new THREE.Vector3((Math.random() - 0.5) * 0.01, (Math.random() - 0.5) * 0.01, (Math.random() - 0.5) * 0.01));
        p.vel.copy(dir).add(new THREE.Vector3((Math.random() - 0.5) * spread, (Math.random() - 0.3) * spread, (Math.random() - 0.5) * spread));
        p.size = size * (0.7 + Math.random() * 0.6); p.grow = grow; p.alpha = alpha;
        p.mat.color.setHex(colors[Math.floor(Math.random() * colors.length)]);
        p.mat.rotation = Math.random() * TAU;
        p.s.visible = true;
      }
    },
    update(dt, t) {
      for (const p of pool) {
        if (!p.on) continue;
        p.life += dt;
        const u = p.life / p.max;
        if (u >= 1) { p.on = false; p.s.visible = false; continue; }
        // вихрится и всплывает: газ тёплый
        p.vel.x += Math.sin(t * 2.3 + p.seed) * 0.02 * dt;
        p.vel.z += Math.cos(t * 1.9 + p.seed) * 0.02 * dt;
        p.vel.y += 0.025 * dt;
        p.vel.multiplyScalar(Math.max(0, 1 - dt * 1.6));
        p.s.position.addScaledVector(p.vel, dt);
        const sz = p.size * (1 + p.grow * Math.sqrt(u));
        p.s.scale.set(sz, sz, sz);
        p.mat.rotation += dt * 0.4;
        p.mat.opacity = p.alpha * (u < 0.12 ? u / 0.12 : Math.pow(1 - (u - 0.12) / 0.88, 1.4));
      }
    },
  };
}

// ---------- еда ----------

const toon = (color, gradientMap, extra = {}) => new THREE.MeshToonMaterial({ color, gradientMap, ...extra });

/** Пачка чипсов «ХРУСТ»: подушка из фольги, яркая этикетка, сверху обжатый шов. */
function makeBag(gradientMap) {
  const W = 0.078, H = 0.11, D = 0.032;
  const geo = new THREE.BoxGeometry(W, H, D, 10, 12, 4);
  const p = geo.attributes.position;
  for (let i = 0; i < p.count; i++) {
    const x = p.getX(i) / (W / 2), y = p.getY(i) / (H / 2);
    // подушка: к краям тоньше, у шва сверху — совсем плоско
    const k = Math.max(0.05, (1 - x * x * 0.85) * (y > 0.82 ? Math.max(0.05, (1 - y) / 0.18) : 1 - Math.max(0, -y - 0.85) * 3));
    p.setZ(i, p.getZ(i) * k);
  }
  geo.computeVertexNormals();
  const c = document.createElement('canvas');
  c.width = 256; c.height = 360;
  const g = c.getContext('2d');
  const bg = g.createLinearGradient(0, 0, 256, 360);
  bg.addColorStop(0, '#ff4d6d'); bg.addColorStop(1, '#ff9f1c');
  g.fillStyle = bg; g.fillRect(0, 0, 256, 360);
  g.fillStyle = '#c9184a'; g.fillRect(0, 0, 256, 40);                    // шов
  for (let x = 0; x < 256; x += 8) { g.fillStyle = '#ffd6e0'; g.fillRect(x, 32, 4, 6); }
  g.fillStyle = '#fff'; g.font = 'bold 58px sans-serif'; g.textAlign = 'center';
  g.fillText('ХРУСТ', 128, 120);
  g.font = 'bold 26px sans-serif'; g.fillText('со сметаной', 128, 158);
  // чипсина на этикетке и кошачья лапка-логотип
  g.fillStyle = '#ffd166';
  g.beginPath(); g.ellipse(128, 250, 70, 48, -0.3, 0, TAU); g.fill();
  g.fillStyle = '#e9a23b';
  for (let i = 0; i < 9; i++) { g.beginPath(); g.arc(90 + (i * 37) % 80, 225 + (i * 23) % 50, 5, 0, TAU); g.fill(); }
  g.fillStyle = '#5ce1e6';
  g.beginPath(); g.arc(210, 320, 16, 0, TAU); g.fill();
  for (const [dx, dy] of [[-16, -20], [-5, -27], [7, -27], [18, -20]]) { g.beginPath(); g.arc(210 + dx, 320 + dy, 6, 0, TAU); g.fill(); }
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  const label = toon(0xffffff, gradientMap, { map: tex });
  const foil = toon(0xff7a45, gradientMap);
  const bag = new THREE.Mesh(geo, [foil, foil, toon(0xc9184a, gradientMap), foil, label, label]);
  const g2 = new THREE.Group();
  g2.add(bag);
  return g2;
}

/** Чипсина-«седло»: изогнутая, золотистая, с крапинками. */
function makeChip(gradientMap) {
  const geo = new THREE.CircleGeometry(0.016, 20, 0, TAU);
  const p = geo.attributes.position;
  const col = [];
  for (let i = 0; i < p.count; i++) {
    const x = p.getX(i), y = p.getY(i);
    p.setZ(i, (x * x - y * y) * 22 * 0.9);
    const spot = Math.sin(x * 900) * Math.cos(y * 700) > 0.75 ? 0.82 : 1;
    col.push(1 * spot, 0.8 * spot, 0.38 * spot);
  }
  geo.scale(1, 0.78, 1);
  geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  geo.computeVertexNormals();
  return new THREE.Mesh(geo, toon(0xffffff, gradientMap, { vertexColors: true, side: THREE.DoubleSide }));
}

/** Рыбка: хвост в начале координат (там лапка), тело вверх по +Y, голова на конце. */
function makeFish(gradientMap) {
  const L = 0.13;
  const body = new THREE.SphereGeometry(1, 24, 14);
  const p = body.attributes.position;
  const col = [];
  for (let i = 0; i < p.count; i++) {
    const x = p.getX(i), y = p.getY(i), z = p.getZ(i);
    // сплюснутое веретено: вдоль Y длина, по Z спина/брюхо, по X толщина
    const along = y;                                       // −1 хвост … +1 голова
    const girth = Math.pow(Math.max(0, 1 - along * along), 0.6) * (along > 0 ? 1 : 0.8 + 0.2 * (1 + along));
    p.setXYZ(i, x * 0.012 * girth, (along + 1) / 2 * L + 0.012, z * 0.022 * girth);
    const back = z > 0 ? 1 : 0;                            // +Z — спина: сине-серая, брюхо серебристое
    const c = back ? [0.16, 0.3, 0.48] : [0.9, 0.93, 0.97];
    const stripe = Math.abs(z) < 0.15 ? 0.85 : 1;
    col.push(c[0] * stripe, c[1] * stripe, c[2] * stripe);
  }
  body.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  body.computeVertexNormals();
  const group = new THREE.Group();
  const bodyMesh = new THREE.Mesh(body, toon(0xffffff, gradientMap, { vertexColors: true }));
  group.add(bodyMesh);
  // хвост — вилка у лапки
  const tail = new THREE.Shape();
  tail.moveTo(0, 0.016); tail.lineTo(-0.018, -0.008); tail.lineTo(0, 0.002); tail.lineTo(0.018, -0.008); tail.closePath();
  const tailMesh = new THREE.Mesh(new THREE.ShapeGeometry(tail), toon(0x4f6d8f, gradientMap, { side: THREE.DoubleSide }));
  tailMesh.rotation.y = Math.PI / 2;
  group.add(tailMesh);
  // спинной плавник
  const fin = new THREE.Shape();
  fin.moveTo(0, 0.045); fin.lineTo(0.016, 0.058); fin.lineTo(0.0, 0.078); fin.closePath();
  const finMesh = new THREE.Mesh(new THREE.ShapeGeometry(fin), toon(0x4f6d8f, gradientMap, { side: THREE.DoubleSide }));
  finMesh.rotation.y = -Math.PI / 2;
  finMesh.position.z = 0.016;
  group.add(finMesh);
  // глаза
  const eyeM = new THREE.MeshBasicMaterial({ color: 0x111111 });
  const eyes = new THREE.Group();
  for (const s of [1, -1]) {
    const e = new THREE.Mesh(new THREE.SphereGeometry(0.0022, 8, 6), eyeM);
    e.position.set(s * 0.0058, 0.012 + L * 0.9, 0.004);
    const hl = new THREE.Mesh(new THREE.SphereGeometry(0.0008, 6, 4), new THREE.MeshBasicMaterial({ color: 0xffffff }));
    hl.position.set(s * 0.0072, 0.012 + L * 0.905, 0.0052);
    eyes.add(e, hl);
  }
  group.add(eyes);
  return { group, body: bodyMesh, eyes, fin: finMesh, L };
}

/** Кальмар в темпуре (обещание Кота 30.09): шпажка, на ней три золотых кольца в бугристом кляре — шпажка проходит
 *  кольцо по диаметру, как на настоящем шашлычке. Кончик шпажки в начале координат (там лапка), кольца вверх по +Y. */
function makeSquid(gradientMap) {
  const group = new THREE.Group();
  const stick = new THREE.Mesh(new THREE.CylinderGeometry(0.0015, 0.0015, 0.17, 6), toon(0xd8b27a, gradientMap));
  stick.position.y = 0.085;
  group.add(stick);
  const rings = [];
  for (let i = 0; i < 3; i++) {
    const g = new THREE.TorusGeometry(0.016, 0.0068, 10, 22);
    const p = g.attributes.position;
    const col = [];
    for (let j = 0; j < p.count; j++) {
      const x = p.getX(j), y = p.getY(j), z = p.getZ(j);
      // кляр бугрится: небольшие выпуклости по поверхности, на буграх — зажаристее
      const n = Math.sin(x * 830 + y * 610 + i) * Math.cos(z * 770 - x * 290) * Math.sin(y * 540 + z * 350);
      const k = 1 + 0.16 * n;
      p.setXYZ(j, x * (1 + 0.03 * n), y * (1 + 0.03 * n), z * k);
      const toast = 0.82 + 0.18 * (1 - Math.max(0, n));
      col.push(0.98 * toast, 0.74 * toast, 0.3 * toast);
    }
    g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    g.computeVertexNormals();
    const ring = new THREE.Mesh(g, toon(0xffffff, gradientMap, { vertexColors: true }));
    ring.position.y = 0.05 + i * 0.041;
    ring.rotation.y = i * 0.7;
    group.add(ring);
    rings.push(ring);
  }
  return { group, rings };
}

/** Зубчик чеснока: капля с носиком, кремово-белый с сиреневыми прожилками. */
function makeGarlic(gradientMap) {
  const pts = [];
  for (let i = 0; i <= 16; i++) {
    const t = i / 16;
    const r = 0.011 * Math.sin(Math.PI * Math.pow(t, 0.8)) * (1 - 0.55 * t * t);
    pts.push(new THREE.Vector2(Math.max(0.0004, r), t * 0.03));
  }
  const geo = new THREE.LatheGeometry(pts, 18);
  const p = geo.attributes.position;
  const col = [];
  for (let i = 0; i < p.count; i++) {
    const a = Math.atan2(p.getZ(i), p.getX(i));
    const vein = Math.abs(Math.sin(a * 3)) < 0.12 ? 1 : 0;
    const top = p.getY(i) / 0.03;
    col.push(0.98 - vein * 0.2, 0.95 - vein * 0.3 - top * 0.05, 0.9 + vein * 0.02);
  }
  geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  geo.scale(1, 1, 0.75);
  geo.computeVertexNormals();
  return new THREE.Mesh(geo, toon(0xffffff, gradientMap, { vertexColors: true }));
}

// ---------- всё вместе ----------

export function createSnacks({ vrm, scene, gradientMap, audio, volume, gestures, S, setEmotion, mouth }) {
  const sfx = createBank(audio, volume);
  const gas = createGas(scene);
  const { P } = gestures;
  // рот и «выхлоп» сзади — точки в костях (едут с головой и тазом)
  const head = vrm.humanoid.getRawBoneNode('head');
  const hips = vrm.humanoid.getRawBoneNode('hips');
  vrm.scene.updateMatrixWorld(true);
  const mouthAnchor = new THREE.Object3D();
  head.add(mouthAnchor);
  mouthAnchor.position.copy(head.worldToLocal((mouth ?? new THREE.Vector3(0, 1.465, 0.098)).clone().add(new THREE.Vector3(0, 0, 0.004))));
  const fwdAnchor = new THREE.Object3D();
  head.add(fwdAnchor);
  fwdAnchor.position.copy(head.worldToLocal((mouth ?? new THREE.Vector3(0, 1.465, 0.098)).clone().add(new THREE.Vector3(0, 0.01, 0.1))));
  const hipsW = hips.getWorldPosition(new THREE.Vector3());
  const buttAnchor = new THREE.Object3D();
  hips.add(buttAnchor);
  buttAnchor.position.copy(hips.worldToLocal(hipsW.clone().add(new THREE.Vector3(0, -0.07, -0.16))));
  const backAnchor = new THREE.Object3D();
  hips.add(backAnchor);
  backAnchor.position.copy(hips.worldToLocal(hipsW.clone().add(new THREE.Vector3(0, -0.1, -0.4))));
  const wp = (o) => o.getWorldPosition(new THREE.Vector3());
  const mouthPos = () => wp(mouthAnchor);
  const mouthDir = () => wp(fwdAnchor).sub(mouthPos());       // ~0,1 м вперёд-вверх

  // предметы
  const bag = makeBag(gradientMap);
  const chip = makeChip(gradientMap);
  const fish = makeFish(gradientMap);
  const garlic = makeGarlic(gradientMap);
  const squid = makeSquid(gradientMap);
  for (const o of [bag, chip, fish.group, garlic, squid.group]) { o.visible = false; o.traverse((m) => { m.frustumCulled = false; }); scene.add(o); }

  const qTmp = new THREE.Quaternion();
  const up = new THREE.Vector3(0, 1, 0);
  /** Предмет у кончиков пальцев правой лапки, осью +Y к цели (ко рту). */
  function holdAt(obj, hand, to, show, finger = true) {
    obj.visible = show > 0.02;
    if (!obj.visible) return;
    const N = gestures.N;
    const a = (finger && N('rightIndexIntermediate')?.getWorldPosition(new THREE.Vector3())) || hand.localToWorld(new THREE.Vector3(0.05, 0, -0.01));
    const b = (finger && N('rightThumbDistal')?.getWorldPosition(new THREE.Vector3())) || a;
    const pos = a.clone().add(b).multiplyScalar(0.5);
    obj.position.copy(pos);
    const dir = to.clone().sub(pos).normalize();
    obj.quaternion.copy(qTmp.setFromUnitVectors(up, dir));
    obj.scale.setScalar(Math.max(0.001, smooth(show)));
  }
  /** Один раз за проигрыш жеста: событие key на отметке u. */
  const once = (a, key, cond, fn) => { a.fired ??= {}; if (cond && !a.fired[key]) { a.fired[key] = true; fn(); } };
  const chew = (u, a, b) => (u > a && u < b ? 0.12 + 0.12 * Math.max(0, Math.sin((u - a) * TAU * 2.6)) : 0);

  // руку ко рту: чуть правее и ниже губ, перед лицом (кончики пальцев с едой — у губ)
  const toMouth = () => P('head', -0.05, -0.08, 0.15);

  gestures.define('chips', { dur: 7.4, fadeIn: 0.5, fadeOut: 0.6, face: ['relaxed', 0.7], spec: (u, a) => {
    const bagAt = P('chest', 0.06, 0.07, 0.27);
    // два захода: лапка в пачку → ко рту → хрум-хрум → жуёт
    const k = u < 3.6 ? 0 : 1, s = u - 0.5 - k * 3.1;
    const dip = bell(s, -0.4, 0.0, 0.25, 0.55);
    const lift = ramp(s, 0.4, 1.0) * (1 - ramp(s, 2.2, 2.8));
    const handAt = bagAt.clone().add(new THREE.Vector3(0, 0.05, 0)).lerp(toMouth(), lift);
    handAt.y -= dip * 0.03;
    const bite1 = 1.15, bite2 = 1.5;
    once(a, `c${k}a`, s >= bite1, () => sfx.play('crunch'));
    once(a, `c${k}b`, s >= bite2, () => sfx.play('crunch', 0.85));
    a.chip = s < bite1 ? 1 : s < bite2 ? 0.55 : 0;
    const open = bell(s, 0.8, 1.05, 1.15, 1.25) * 0.45 + bell(s, 1.3, 1.4, 1.5, 1.6) * 0.3;
    const chewing = chew(s, 1.6, 2.7);
    once(a, `ch${k}1`, s >= 1.9, () => sfx.play('chew', 0.7));
    once(a, `ch${k}2`, s >= 2.3, () => sfx.play('chew', 0.6));
    return {
      body: { head: [-0.06 * lift + Math.sin(u * 16) * 0.012 * (chewing > 0 ? 1 : 0), 0.05 * lift, 0], chest: [-0.02, 0, 0] },
      L: { t: bagAt, pole: P('chest', 0.7, -0.5, 0.1), curl: 0.75, hand: [0.2, 0.3, -0.4] },
      R: { t: handAt, pole: P('chest', -0.8, -0.5, 0.0), curl: 0.55, index: 0.35 },
      mouth: { aa: open + chewing, oh: chewing * 0.3 },
      blink: 0.5 * bell(s, 1.1, 1.2, 1.5, 1.7),
      after: (w, hands) => {
        bag.visible = w > 0.05;
        if (bag.visible) {
          bag.position.copy(hands.L.localToWorld(new THREE.Vector3(-0.045, 0.01, 0.0)));
          bag.quaternion.copy(gestures.sceneQ()).multiply(gestures.E(-0.15, 0.25, 0));
          bag.scale.setScalar(Math.max(0.001, smooth(w)));
        }
        holdAt(chip, hands.R, mouthPos(), Math.min(w, a.chip ?? 0) * (s > -0.1 ? 1 : 0));
        if (chip.visible) chip.scale.multiplyScalar(0.6 + 0.4 * (a.chip ?? 0));
      },
    };
  } });

  gestures.define('fish', { dur: 6.8, fadeIn: 0.5, fadeOut: 0.6, face: ['relaxed', 0.85], spec: (u, a) => {
    const rest = P('chest', -0.1, -0.12, 0.25);
    const lift = ramp(u, 0.5, 1.2) * (1 - ramp(u, 5.2, 5.9));
    const bites = [1.6, 3.3];
    const near = Math.max(...bites.map((b) => bell(u, b - 0.45, b - 0.1, b + 0.1, b + 0.5)));
    const handAt = rest.lerp(P('head', -0.045, -0.17 + 0.03 * near, 0.14 + 0.02 * (1 - near)), lift);
    bites.forEach((b, i) => once(a, `b${i}`, u >= b, () => { sfx.play('chew', 1); sfx.play('crunch', 0.35); }));
    a.left = u < bites[0] ? 1 : u < bites[1] ? 0.62 : 0.3;
    const open = Math.max(...bites.map((b) => bell(u, b - 0.35, b - 0.12, b, b + 0.1))) * 0.5;
    const chewing = chew(u, 1.75, 3.0) + chew(u, 3.45, 4.9);
    once(a, 'm1', u >= 2.3, () => sfx.play('chew', 0.6));
    once(a, 'm2', u >= 4.1, () => sfx.play('chew', 0.6));
    return {
      body: { head: [-0.04 * lift + Math.sin(u * 16) * 0.01 * (chewing > 0 ? 1 : 0), 0.04 * lift, 0.04 * near] },
      R: { t: handAt, pole: P('chest', -0.8, -0.5, 0.0), curl: 0.8 },
      mouth: { aa: open + chewing, oh: chewing * 0.25 },
      blink: 0.6 * near,
      earTwitch: u > 4.5 ? 'L' : null,
      after: (w, hands) => {
        const show = w;
        fish.group.visible = show > 0.02;
        if (!fish.group.visible) return;
        const palm = hands.R.localToWorld(new THREE.Vector3(0.05, 0.0, -0.015));
        fish.group.position.copy(palm);
        const dir = mouthPos().sub(palm).normalize();
        fish.group.quaternion.copy(qTmp.setFromUnitVectors(up, dir));
        fish.group.scale.set(smooth(show), smooth(show) * (a.left ?? 1), smooth(show));
        fish.eyes.visible = (a.left ?? 1) > 0.9;
      },
    };
  } });

  // кальмар в темпуре: три кольца — три хрустящих укуса, верхнее (ближнее ко рту) уходит первым
  gestures.define('squid', { dur: 7.6, fadeIn: 0.5, fadeOut: 0.6, face: ['happy', 0.7], spec: (u, a) => {
    const rest = P('chest', -0.1, -0.12, 0.25);
    const lift = ramp(u, 0.5, 1.2) * (1 - ramp(u, 6.0, 6.7));
    const bites = [1.6, 3.1, 4.6];
    const near = Math.max(...bites.map((b) => bell(u, b - 0.45, b - 0.1, b + 0.1, b + 0.5)));
    const handAt = rest.lerp(P('head', -0.05, -0.2 + 0.03 * near, 0.13 + 0.02 * (1 - near)), lift);
    bites.forEach((b, i) => once(a, `b${i}`, u >= b, () => { sfx.play('crunch', 0.9); sfx.play('chew', 0.7); }));
    a.left = bites.filter((b) => u < b).length;
    const open = Math.max(...bites.map((b) => bell(u, b - 0.35, b - 0.12, b, b + 0.1))) * 0.55;
    const chewing = chew(u, 1.75, 2.9) + chew(u, 3.25, 4.4) + chew(u, 4.75, 5.9);
    return {
      body: { head: [-0.04 * lift + Math.sin(u * 16) * 0.01 * (chewing > 0 ? 1 : 0), 0.04 * lift, 0.04 * near] },
      R: { t: handAt, pole: P('chest', -0.8, -0.5, 0.0), curl: 0.85 },
      mouth: { aa: open + chewing, oh: chewing * 0.25 },
      blink: 0.6 * near,
      earTwitch: u > 5.5 ? 'R' : null,
      after: (w, hands) => {
        squid.group.visible = w > 0.02;
        if (!squid.group.visible) return;
        const palm = hands.R.localToWorld(new THREE.Vector3(0.05, 0.0, -0.015));
        squid.group.position.copy(palm);
        squid.group.quaternion.copy(qTmp.setFromUnitVectors(up, mouthPos().sub(palm).normalize()));
        squid.group.scale.setScalar(smooth(w));
        squid.rings.forEach((r, i) => { r.visible = i < (a.left ?? 3); });
      },
    };
  } });

  gestures.define('garlic', { dur: 7.2, fadeIn: 0.5, fadeOut: 0.6, face: ['relaxed', 0.4], spec: (u, a) => {
    const rest = P('chest', -0.1, -0.12, 0.25);
    const lift = ramp(u, 0.4, 1.1) * (1 - ramp(u, 1.7, 2.3));
    const handAt = rest.lerp(toMouth(), lift);
    once(a, 'bite', u >= 1.35, () => sfx.play('crunch', 0.5));
    a.clove = u < 1.35 ? 1 : 0;
    const chewing = chew(u, 1.5, 3.6);
    once(a, 'c1', u >= 2.1, () => sfx.play('chew', 0.6));
    once(a, 'c2', u >= 2.9, () => sfx.play('chew', 0.6));
    // кислая мина, потом «ха-а» — зелёный выдох
    once(a, 'yuck', u >= 2.4, () => setEmotion?.('angry', 0.35, 1.6));
    const exhale = bell(u, 3.9, 4.1, 4.9, 5.3);
    once(a, 'puff', u >= 4.0, () => {
      gas.emit(mouthPos(), mouthDir().multiplyScalar(2.2), { n: 14, size: 0.025, life: 2.6, spread: 0.08, grow: 5, alpha: 0.55 });
      S.garlicUntil = S.t + 150;
      setEmotion?.('surprised', 0.5, 1.5);
    });
    return {
      body: { head: [-0.04 * lift + Math.sin(u * 16) * 0.01 * (chewing > 0 ? 1 : 0) - 0.08 * exhale, 0.03 * lift, 0] },
      R: { t: handAt, pole: P('chest', -0.8, -0.5, 0.0), curl: 0.6, index: 0.4 },
      mouth: { aa: bell(u, 1.1, 1.3, 1.35, 1.45) * 0.4 + chewing + exhale * 0.45, oh: exhale * 0.35 },
      blink: 0.7 * bell(u, 2.3, 2.5, 3.4, 3.7),
      after: (w, hands) => holdAt(garlic, hands.R, mouthPos(), Math.min(w, a.clove ?? 1)),
    };
  } });

  gestures.define('burp', { dur: 2.2, fadeIn: 0.2, fadeOut: 0.4, face: ['relaxed', 0.3], spec: (u, a) => {
    once(a, 'go', u >= 0.25, () => {
      a.len = sfx.play('burp') || 0.7;
      a.at = u;
      gas.emit(mouthPos(), mouthDir().multiplyScalar(1.6), { n: Math.round(6 + a.len * 6), size: 0.02, life: 2.2, spread: 0.06, grow: 4.5, alpha: 0.45 });
    });
    const t0 = a.at ?? 0.25, len = a.len ?? 0.7;
    const on = bell(u, t0 - 0.05, t0 + 0.05, t0 + len - 0.1, t0 + len + 0.05);
    once(a, 'after', u >= t0 + len, () => setEmotion?.(Math.random() < 0.5 ? 'happy' : 'relaxed', 0.6, 1.5));
    return {
      body: { chest: [0.04 * on, 0, 0], head: [-0.07 * on + 0.03 * bell(u, 0, 0.15, 0.2, 0.3), 0, 0.03 * on], shoulders: 0.05 * on },
      mouth: { aa: on * (0.35 + 0.1 * Math.sin(u * 40)), oh: on * 0.35 },
      blink: 0.4 * on,
    };
  } });

  gestures.define('fart', { dur: 2.6, fadeIn: 0.25, fadeOut: 0.5, face: ['surprised', 0.4], spec: (u, a) => {
    once(a, 'go', u >= 0.35, () => {
      a.len = sfx.play('fart') || 0.8;
      a.at = u;
      const back = wp(backAnchor).sub(wp(buttAnchor)).normalize();
      gas.emit(wp(buttAnchor), back.multiplyScalar(0.35), { n: Math.round(8 + a.len * 8), size: 0.05, life: 3.2, spread: 0.12, grow: 4, alpha: 0.42 });
    });
    const t0 = a.at ?? 0.35, len = a.len ?? 0.8;
    const on = bell(u, t0 - 0.1, t0 + 0.05, t0 + len, t0 + len + 0.2);
    // после — либо довольная ухмылка, либо смущение
    once(a, 'after', u >= t0 + len * 0.6, () => (Math.random() < 0.55 ? setEmotion?.('happy', 0.7, 1.6) : setEmotion?.('extra', 0.8, 2)));
    return {
      body: { spine: [-0.05 * on, 0, 0], chest: [-0.03 * on, 0, 0], head: [0.04 * on, 0.06 * on, 0] },
      earTwitch: on > 0.5 ? 'L' : null,
      blink: 0.3 * on,
    };
  } });

  let lastBreath = 0;
  const props = { chips: [bag, chip], fish: [fish.group], garlic: [garlic], squid: [squid.group] };
  return {
    gas,
    sfx,
    update(dt, t) {
      gas.update(dt, t);
      // жест кончился — его еду убрать (ставит её только живой жест)
      const live = new Set(gestures.active.map((x) => x.name));
      for (const [name, objs] of Object.entries(props)) if (!live.has(name)) for (const o of objs) o.visible = false;
      // чесночный пар при разговоре: пока «пахнет», с каждым открытием рта — зелёная струйка
      if (S.garlicUntil && S.t < S.garlicUntil && S.speaking && (S.mouth?.aa ?? 0) > 0.18 && S.t - lastBreath > 0.35) {
        lastBreath = S.t;
        const left = (S.garlicUntil - S.t) / 150;
        gas.emit(mouthPos(), mouthDir().multiplyScalar(1.2), { n: 2, size: 0.012, life: 1.6, spread: 0.04, grow: 4, alpha: 0.25 + 0.2 * left });
      }
    },
    /** Съесть/сделать: chips | fish | garlic | squid | burp | fart. */
    play: (name) => gestures.play(name),
  };
}
