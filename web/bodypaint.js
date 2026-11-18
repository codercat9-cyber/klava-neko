// Раскраска того, что нарисовано прямо на коже. У VRoid-моделей (Вита) купальник-боди, длинные перчатки и чулки —
// не отдельные вещи, а рисунок на той же текстуре, что и кожа (материал *_Body_00_SKIN). Поэтому «цвет кожи»
// умножением материала красил и купальник, а перекрасить купальник отдельно было нечем.
//
// Как устроено: каждый пиксель текстуры привязывается к части тела — треугольники сетки тела растеризуются в их UV,
// часть берётся по кости, у которой больше всего веса в вершинах (таз/позвоночник/грудь → купальник, руки → перчатки,
// ноги → чулки). Кожа от рисунка одежды отличается оттенком: кожа тёплая (красный ≥ зелёного ≥ синего), одежда — нет.
// Кожа перекрашивается умножением (румянец, тени и родинки остаются), одежда — заливкой нового цвета с сохранением
// светотени (яркость каждого пикселя относительно средней по части). Исходная текстура хранится, apply() идемпотентен.
//
// Раздел config.json: bodyPaint { suit, gloves, stockings } — [r, g, b] (линейные, как остальные цвета) или null —
// как в модели; цвет кожи — body.skin. Общий для редактора (look.js) и живой Клавы (klava.js).
import * as THREE from 'three';

const PARTS = { suit: 1, gloves: 2, stockings: 3 };
const BONE_PART = {
  hips: 1, spine: 1, chest: 1, upperChest: 1, neck: 1,
  leftShoulder: 2, rightShoulder: 2, leftUpperArm: 2, rightUpperArm: 2, leftLowerArm: 2, rightLowerArm: 2, leftHand: 2, rightHand: 2,
  leftUpperLeg: 3, rightUpperLeg: 3, leftLowerLeg: 3, rightLowerLeg: 3, leftFoot: 3, rightFoot: 3, leftToes: 3, rightToes: 3,
};

// sRGB ↔ линейный — таблицами (4 млн пикселей на 2048² текстуре)
const toLin = new Float32Array(256);
for (let i = 0; i < 256; i++) { const c = i / 255; toLin[i] = c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }
function toSrgbByte(l) {
  const v = l <= 0 ? 0 : l >= 1 ? 1 : l <= 0.0031308 ? l * 12.92 : 1.055 * Math.pow(l, 1 / 2.4) - 0.055;
  return Math.round(v * 255);
}

/** Кожа: тёплый оттенок (не серый, не синий/фиолетовый/зелёный). */
function isSkin(r, g, b) {
  return r > 60 && r >= g && g >= b - 8 && r - b > 18;
}

export function createBodyPaint(vrm) {
  const humanOf = new Map();   // кость скелета → номер части (через ближайшую гуманоидную кость вверх)
  const byRaw = new Map();
  for (const name of Object.keys(BONE_PART)) {
    const n = vrm.humanoid.getRawBoneNode(name);
    if (n) byRaw.set(n, BONE_PART[name]);
  }
  const partOfBone = (bone) => {
    if (humanOf.has(bone)) return humanOf.get(bone);
    let p = bone, part = 0;
    while (p) { if (byRaw.has(p)) { part = byRaw.get(p); break; } p = p.parent; }
    humanOf.set(bone, part);
    return part;
  };

  // материалы с кожей: у каждого — список его сеток (скиненных), исходная текстура и её копия-холст
  const skins = new Map();
  vrm.scene.traverse((o) => {
    if (!o.isSkinnedMesh) return;
    for (const m of [].concat(o.material)) {
      if (!m?.map?.image || !/SKIN/i.test(m.name) || /\(Outline\)\s*$/.test(m.name)) continue;
      if (!skins.has(m)) skins.set(m, { meshes: [], origMap: m.map, origShade: m.shadeMultiplyTexture ?? null, data: null, labels: null });
      skins.get(m).meshes.push(o);
    }
  });
  const twins = (m) => {
    const out = [];
    vrm.scene.traverse((o) => { if (o.isMesh) for (const x of [].concat(o.material)) if (x !== m && x?.name?.startsWith(m.name) && /\(Outline\)\s*$/.test(x.name)) out.push(x); });
    return out;
  };

  /** Пиксели исходной текстуры и карта частей (один раз на материал). */
  function prepare(entry) {
    if (entry.data) return;
    const img = entry.origMap.image;
    const w = img.width, h = img.height;
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const ctx = c.getContext('2d', { willReadFrequently: true });
    ctx.drawImage(img, 0, 0);
    entry.data = ctx.getImageData(0, 0, w, h);
    entry.w = w; entry.h = h;
    const labels = new Uint8Array(w * h);
    const flip = entry.origMap.flipY;
    for (const mesh of entry.meshes) {
      const g = mesh.geometry;
      const uv = g.attributes.uv, si = g.attributes.skinIndex, sw = g.attributes.skinWeight;
      if (!uv || !si || !sw) continue;
      const bones = mesh.skeleton.bones;
      const vpart = new Uint8Array(uv.count);
      for (let v = 0; v < uv.count; v++) {
        const acc = [0, 0, 0, 0];
        for (let k = 0; k < 4; k++) {
          const wgt = sw.getComponent(v, k);
          if (wgt > 0) acc[partOfBone(bones[si.getComponent(v, k)])] += wgt;
        }
        let best = 0;
        for (let p = 1; p < 4; p++) if (acc[p] > acc[best]) best = p;
        vpart[v] = best;
      }
      const idx = g.index ? g.index.array : null;
      const triCount = idx ? idx.length / 3 : uv.count / 3;
      for (let t = 0; t < triCount; t++) {
        const a = idx ? idx[t * 3] : t * 3, b = idx ? idx[t * 3 + 1] : t * 3 + 1, cc = idx ? idx[t * 3 + 2] : t * 3 + 2;
        // часть треугольника — большинство его вершин (две из трёх), иначе первой
        const pa = vpart[a], pb = vpart[b], pc = vpart[cc];
        const part = pa === pb || pa === pc ? pa : pb === pc ? pb : pa;
        if (!part) continue;
        const P = [a, b, cc].map((i) => {
          const u = uv.getX(i), vv = uv.getY(i);
          return [(((u % 1) + 1) % 1) * w, (((flip ? 1 - vv : vv) % 1 + 1) % 1) * h];
        });
        rasterize(labels, w, h, P, part);
      }
    }
    // края островов развёртки: 2 пикселя наружу, чтобы при фильтрации на шве не проступал старый цвет
    for (let pass = 0; pass < 2; pass++) {
      const add = [];
      for (let y = 1; y < h - 1; y++) for (let x = 1; x < w - 1; x++) {
        const i = y * w + x;
        if (labels[i]) continue;
        const n = labels[i - 1] || labels[i + 1] || labels[i - w] || labels[i + w];
        if (n) add.push(i, n);
      }
      for (let k = 0; k < add.length; k += 2) labels[add[k]] = add[k + 1];
    }
    entry.labels = labels;
  }

  function rasterize(labels, w, h, P, part) {
    const minX = Math.max(0, Math.floor(Math.min(P[0][0], P[1][0], P[2][0])));
    const maxX = Math.min(w - 1, Math.ceil(Math.max(P[0][0], P[1][0], P[2][0])));
    const minY = Math.max(0, Math.floor(Math.min(P[0][1], P[1][1], P[2][1])));
    const maxY = Math.min(h - 1, Math.ceil(Math.max(P[0][1], P[1][1], P[2][1])));
    if (maxX - minX > w / 2 || maxY - minY > h / 2) return;   // треугольник через шов повтора UV — не рисуем
    const [x0, y0] = P[0], [x1, y1] = P[1], [x2, y2] = P[2];
    const d = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
    if (Math.abs(d) < 1e-9) return;
    for (let y = minY; y <= maxY; y++) {
      const py = y + 0.5;
      for (let x = minX; x <= maxX; x++) {
        const px = x + 0.5;
        const l0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / d;
        const l1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / d;
        if (l0 >= -0.01 && l1 >= -0.01 && 1 - l0 - l1 >= -0.01) labels[y * w + x] = part;
      }
    }
  }

  function setMap(m, entry, tex) {
    for (const t of [m, ...twins(m)]) {
      t.map = tex;
      if ('shadeMultiplyTexture' in t && entry.origShade) t.shadeMultiplyTexture = tex;
      t.needsUpdate = true;
    }
  }

  /** Вернуть родную текстуру (и освободить перекрашенную). */
  function unpaint(m, e) {
    if (!e.painted) return;
    setMap(m, e, e.origMap);
    e.tex?.dispose();
    e.tex = null;
    e.painted = false;
  }

  function restore() {
    for (const [m, e] of skins) unpaint(m, e);
  }

  /**
   * skin — [r, g, b] линейный множитель кожи (или null); paint — { suit, gloves, stockings }; skipMaterials — имена
   * материалов, которые не трогать (у них своя нарисованная в редакторе текстура).
   */
  function apply(skin, paint, skipMaterials = new Set()) {
    const want = !!paint && Object.keys(PARTS).some((k) => Array.isArray(paint[k]));
    // те же цвета, что в прошлый раз, — готовую текстуру просто вернуть на место (редактор пересобирает облик на
    // каждый ползунок, а пересчёт 2048² стоит ~0,8 с)
    const key = JSON.stringify([skin, paint?.suit, paint?.gloves, paint?.stockings]);
    for (const [m, e] of skins) {
      if (skipMaterials.has(m.name) || (!Array.isArray(skin) && !want)) {
        unpaint(m, e);
        e.key = null;
        continue;
      }
      if (e.tex && e.key === key) {
        setMap(m, e, e.tex);
        e.painted = true;
        continue;
      }
      e.key = key;
      prepare(e);
      const src = e.data.data, n = e.w * e.h;
      const out = new ImageData(e.w, e.h);
      const dst = out.data;
      // средняя яркость «одежды» по каждой части — для светотени новой заливки
      const sum = [0, 0, 0, 0], cnt = [0, 0, 0, 0];
      for (let i = 0; i < n; i++) {
        const p = e.labels[i];
        if (!p) continue;
        const j = i * 4;
        if (isSkin(src[j], src[j + 1], src[j + 2])) continue;
        sum[p] += 0.2126 * toLin[src[j]] + 0.7152 * toLin[src[j + 1]] + 0.0722 * toLin[src[j + 2]];
        cnt[p]++;
      }
      // одежда нарисована только на текстуре тела; на лице (*_Face_*_SKIN) не-кожа — это метки и тени у глаз, их не трогаем
      const clothes = /Body/i.test(m.name);
      const target = [null, paint?.suit, paint?.gloves, paint?.stockings].map((c) => (clothes && Array.isArray(c) ? c : null));
      for (let i = 0; i < n; i++) {
        const j = i * 4;
        const r = src[j], g = src[j + 1], b = src[j + 2];
        dst[j + 3] = src[j + 3];
        const skinPx = isSkin(r, g, b);
        const p = e.labels[i];
        if (skinPx && Array.isArray(skin)) {
          dst[j] = toSrgbByte(toLin[r] * skin[0]);
          dst[j + 1] = toSrgbByte(toLin[g] * skin[1]);
          dst[j + 2] = toSrgbByte(toLin[b] * skin[2]);
        } else if (!skinPx && p && target[p] && cnt[p] > 0) {
          const lum = 0.2126 * toLin[r] + 0.7152 * toLin[g] + 0.0722 * toLin[b];
          const k = Math.min(2.2, lum / Math.max(1e-4, sum[p] / cnt[p]));
          const t = target[p];
          dst[j] = toSrgbByte(t[0] * k);
          dst[j + 1] = toSrgbByte(t[1] * k);
          dst[j + 2] = toSrgbByte(t[2] * k);
        } else {
          dst[j] = r; dst[j + 1] = g; dst[j + 2] = b;
        }
      }
      const c = document.createElement('canvas');
      c.width = e.w; c.height = e.h;
      c.getContext('2d').putImageData(out, 0, 0);
      const tex = new THREE.CanvasTexture(c);
      for (const k of ['flipY', 'wrapS', 'wrapT', 'colorSpace', 'minFilter', 'magFilter', 'anisotropy']) tex[k] = e.origMap[k];
      tex.offset.copy(e.origMap.offset);
      tex.repeat.copy(e.origMap.repeat);
      tex.needsUpdate = true;
      setMap(m, e, tex);
      e.tex?.dispose();
      e.tex = tex;
      e.painted = true;
    }
  }

  /** Есть ли что красить (у модели кожа с нарисованной одеждой) — для панели редактора. */
  const available = () => skins.size > 0;

  return { apply, restore, available, materials: () => [...skins.keys()].map((m) => m.name) };
}
