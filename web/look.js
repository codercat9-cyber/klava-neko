// Облик Клавы поверх голой VRM: окраска волос, спрятанные куски, грудь, пушистые ушки, кимоно, хвост,
// свои текстуры. Всё — по разделам config.json, тем же, что читает klava.js; ушки перенесены из klava.js
// один в один, кимоно и хвост — те же модули kimono.js и tail.js. Нужен редактору (editor.js): там облик
// пересобирается на каждое изменение, поэтому apply() идемпотентен — всё исходное запоминается при первом
// вызове и восстанавливается перед новой сборкой.
import * as THREE from 'three';
import { addKimono } from './kimono.js';
import { addTail } from './tail.js';
import { createFigure } from './figure.js';
import { createBodyPaint } from './bodypaint.js';
import { addClaws } from './claws.js';

const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

export const toonGradient = (() => {
  const t = new THREE.DataTexture(new Uint8Array([110, 185, 255]), 3, 1, THREE.RedFormat);
  t.minFilter = t.magFilter = THREE.NearestFilter;
  t.needsUpdate = true;
  return t;
})();

function makeRng(seed) {
  let s = seed;
  return () => ((s = (s * 16807) % 2147483647) / 2147483647);
}

// Шум волосков: чёрный фон, белые мягкие точки разной яркости (альфа-карта берёт зелёный канал).
export function strandTexture(seed, count, repeatU, repeatV, rMin = 2.4, rMax = 5.6) {
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const g = c.getContext('2d');
  g.fillStyle = '#000';
  g.fillRect(0, 0, 256, 256);
  const rnd = makeRng(seed);
  for (let i = 0; i < count; i++) {
    const x = rnd() * 256, y = rnd() * 256, r = rMin + rnd() * (rMax - rMin), v = Math.round(70 + rnd() * 185);
    for (const [dx, dy] of [[0, 0], [256, 0], [-256, 0], [0, 256], [0, -256]]) {   // бесшовный повтор
      const gr = g.createRadialGradient(x + dx, y + dy, 0, x + dx, y + dy, r);
      gr.addColorStop(0, `rgb(${v},${v},${v})`);
      gr.addColorStop(1, 'rgb(0,0,0)');
      g.fillStyle = gr;
      g.fillRect(x + dx - r, y + dy - r, r * 2, r * 2);
    }
  }
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.NoColorSpace;
  tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
  tex.repeat.set(repeatU, repeatV);
  return tex;
}

// Кожа уха: мелкие штрихи-волоски к кончику, чтобы между слоями меха не было гладкого пластика.
function skinTexture(rgb, seed) {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  const [r, gg, b] = rgb.map((v) => Math.round(v * 255));
  g.fillStyle = `rgb(${r},${gg},${b})`;
  g.fillRect(0, 0, 128, 128);
  const rnd = makeRng(seed);
  for (let i = 0; i < 900; i++) {
    const x = rnd() * 128, y = rnd() * 128, len = 3 + rnd() * 7, sh = (rnd() - 0.5) * 50;
    g.strokeStyle = `rgba(${clamp(r + sh, 0, 255)},${clamp(gg + sh, 0, 255)},${clamp(b + sh, 0, 255)},0.5)`;
    g.lineWidth = 0.7 + rnd();
    g.beginPath();
    g.moveTo(x, y);
    g.lineTo(x + (rnd() - 0.5) * 2, y - len);
    g.stroke();
  }
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
  return tex;
}

// Сетка уха. u — поперёк (0 — один край, 0.5 — спинка уха, 1 — другой край), v — от основания к кончику.
function earGeometry(k, widthK = 1, depthK = 1) {
  const NU = 26, NV = 22;
  const H = 0.105 * k, W = 0.042 * k * widthK, D = 0.02 * k * depthK;
  const pos = [], uv = [], idx = [];
  for (let j = 0; j <= NV; j++) {
    const v = j / NV;
    const w = W * Math.pow(1 - v, 1.05) * (1 + 0.28 * Math.sin(Math.PI * v * 0.9)) + 0.0012 * k;
    const d = D * Math.pow(1 - v, 1.1) + 0.0008 * k;
    for (let i = 0; i <= NU; i++) {
      const u = i / NU;
      const th = (u - 0.5) * Math.PI * 0.92;
      pos.push(w * Math.sin(th), H * v, -d * Math.cos(th) - 0.012 * k * v * v);
      uv.push(u, v);
    }
  }
  for (let j = 0; j < NV; j++) for (let i = 0; i < NU; i++) {
    const a = j * (NU + 1) + i, b = a + 1, c = a + NU + 1, d = c + 1;
    idx.push(a, c, b, b, c, d);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  geo.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  geo.setIndex(idx);
  geo.computeVertexNormals();
  const mid = Math.round(NV / 2) * (NU + 1) + NU / 2;
  if (geo.attributes.normal.getZ(mid) > 0) {
    const ix = geo.index.array;
    for (let t = 0; t < ix.length; t += 3) { const s = ix[t + 1]; ix[t + 1] = ix[t + 2]; ix[t + 2] = s; }
    geo.computeVertexNormals();
  }
  return geo;
}

// Слои меха: копии сетки, сдвинутые по нормали на len(u, v)·h, плюс «расчёска» comb·h².
function furShells(base, { layers, len, dir, comb, color, tex, cut0, cut1, darkBase, name }) {
  const group = new THREE.Group();
  group.name = name;
  group.userData.fur = true;
  const p = base.attributes.position, n = base.attributes.normal, t = base.attributes.uv;
  for (let s = 1; s <= layers; s++) {
    const h = s / layers;
    const arr = new Float32Array(p.count * 3);
    for (let i = 0; i < p.count; i++) {
      const L = len(t.getX(i), t.getY(i)) * h;
      arr[i * 3] = p.getX(i) + n.getX(i) * L * dir + comb[0] * L * h;
      arr[i * 3 + 1] = p.getY(i) + n.getY(i) * L * dir + comb[1] * L * h;
      arr[i * 3 + 2] = p.getZ(i) + n.getZ(i) * L * dir + comb[2] * L * h;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(arr, 3));
    geo.setAttribute('uv', t);
    geo.setAttribute('normal', n);
    geo.setIndex(base.index);
    const shade = darkBase + (1 - darkBase) * Math.pow(h, 0.7);
    const mat = new THREE.MeshToonMaterial({
      color: new THREE.Color(...color).multiplyScalar(shade), gradientMap: toonGradient,
      alphaMap: tex, alphaTest: cut0 + (cut1 - cut0) * h, side: THREE.DoubleSide, alphaToCoverage: true,   // мех: кончики ворсинок мягкие, не лесенкой
    });
    mat.name = `${name}_слой${s}`;
    const m = new THREE.Mesh(geo, mat);
    m.name = `${name}_слой${s}`;
    m.userData.furLayer = s;
    m.userData.furLayers = layers;
    m.frustumCulled = false;
    m.renderOrder = 2;
    group.add(m);
  }
  return group;
}

function makeEar(e, side) {
  const k = e.size ?? 1;
  const outerRgb = e.outer ?? [0.45, 0.66, 1.0];
  const innerRgb = e.inner ?? [1.0, 0.86, 0.93];
  const tuftRgb = e.tuft ?? [1.0, 0.98, 1.0];
  const tag = side > 0 ? 'L' : 'R';
  const group = new THREE.Group();
  group.name = `Ухо_${tag}`;

  const outerGeo = earGeometry(k);
  const outerMat = new THREE.MeshToonMaterial({ color: 0xffffff, map: skinTexture(outerRgb, side > 0 ? 7 : 11), gradientMap: toonGradient, side: THREE.DoubleSide });
  outerMat.name = `Ухо_${tag}_снаружи`;
  const outer = new THREE.Mesh(outerGeo, outerMat);
  outer.name = `Ухо_${tag}_снаружи`;
  const innerGeo = earGeometry(k * 0.84, 0.64, 0.75);
  const innerMat = new THREE.MeshToonMaterial({ color: 0xffffff, map: skinTexture(innerRgb, side > 0 ? 3 : 5), gradientMap: toonGradient, side: THREE.BackSide });
  innerMat.name = `Ухо_${tag}_внутри`;
  const inner = new THREE.Mesh(innerGeo, innerMat);
  inner.name = `Ухо_${tag}_внутри`;
  inner.position.set(0, 0.004 * k, 0.003 * k);
  group.add(outer, inner);

  const outerFur = furShells(outerGeo, {
    name: `Ухо_${tag}_мех`, layers: 18, dir: 1, comb: [0, 0.9, 0.15],
    len: (u, v) => 0.008 * k * (1 - 0.5 * v) * (1 + 1.2 * Math.pow(Math.abs(u - 0.5) * 2, 3)),
    color: outerRgb, tex: strandTexture(side > 0 ? 101 : 202, 900, 3, 4, 5, 11), cut0: 0.03, cut1: 0.85, darkBase: 0.68,
  });
  const innerFur = furShells(innerGeo, {
    name: `Ухо_${tag}_пух`, layers: 22, dir: -1, comb: [0, 1.3, 0.6],
    len: (u, v) => 0.032 * k * Math.pow(Math.max(0, 1 - v * 1.2), 1.2) * (0.25 + 0.75 * Math.cos((u - 0.5) * Math.PI)),
    color: tuftRgb, tex: strandTexture(side > 0 ? 303 : 404, 240, 2, 2, 9, 20), cut0: 0.04, cut1: 0.8, darkBase: 0.72,
  });
  innerFur.position.copy(inner.position);
  const rimFur = furShells(outerGeo, {
    name: `Ухо_${tag}_кромка`, layers: 16, dir: -1, comb: [0, 0.7, 0.4],
    len: (u, v) => 0.009 * k * Math.max(0, (Math.abs(u - 0.5) * 2 - 0.5) / 0.5) * (1 - 0.6 * v),
    color: outerRgb, tex: strandTexture(side > 0 ? 505 : 606, 900, 3, 4, 5, 11), cut0: 0.03, cut1: 0.85, darkBase: 0.8,
  });
  group.add(outerFur, innerFur, rimFur);
  for (const m of [outer, inner]) { m.frustumCulled = false; m.renderOrder = 2; }
  return group;
}

/** Понятное имя куска VRM по материалу — для списка «Части» в редакторе. */
export function partLabel(mesh) {
  const m = [].concat(mesh.material)[0]?.name ?? '';
  const table = [
    [/FaceMouth/i, 'Рот'], [/EyeWhite/i, 'Белки глаз'], [/EyeIris/i, 'Радужки'], [/EyeHighlight/i, 'Блики глаз'],
    [/EyeExtra/i, 'Глаза (доп.)'], [/Eyeline/i, 'Подводка'], [/Eyelash/i, 'Ресницы'], [/Brow/i, 'Брови'],
    [/Face_\d+_SKIN/i, 'Лицо'], [/Body_\d+_SKIN/i, 'Тело (кожа)'], [/Tops/i, 'Родной костюм'], [/Shoes/i, 'Обувь'],
    [/HairBack/i, 'Волосы (основа)'], [/HAIR/i, 'Пряди волос'],
  ];
  for (const [rx, label] of table) if (rx.test(m)) return label;
  return m || mesh.name;
}

/**
 * Облик на модели. apply(cfg) снимает прошлую сборку и собирает заново; колонки кимоно можно менять
 * на лету через kimonoUniforms без пересборки.
 */
export function createLook(vrm) {
  const orig = { colors: new Map(), visible: new Map(), bust: new Map(), maps: new Map() };
  vrm.scene.traverse((o) => {
    if (o.isMesh) orig.visible.set(o, o.visible);
    if (!o.isMesh) return;
    for (const m of [].concat(o.material)) {
      if (!m || orig.colors.has(m)) continue;
      orig.colors.set(m, { color: m.color?.clone(), shade: m.shadeColorFactor?.clone() });
      orig.maps.set(m, { map: m.map ?? null, shade: m.shadeMultiplyTexture ?? null });
    }
  });
  for (const n of ['J_Sec_L_Bust1', 'J_Sec_R_Bust1']) {
    const b = vrm.scene.getObjectByName(n);
    if (b) orig.bust.set(n, { pos: b.position.clone(), scale: b.scale.clone() });
  }

  const built = { ears: [], earHolders: [], kimono: null, tail: null, figure: null, claws: null };
  const loader = new THREE.TextureLoader();
  const figure = createFigure(vrm);   // тело и одёжка (figure.js) — сама помнит исходное и откатывает
  const bodyPaint = createBodyPaint(vrm);   // кожа и нарисованные на ней купальник, перчатки, чулки (bodypaint.js)

  function teardown() {
    for (const h of built.earHolders) h.removeFromParent();
    built.earHolders = [];
    built.ears = [];
    if (built.kimono) { built.kimono.top.removeFromParent(); built.kimono.skirt.removeFromParent(); built.kimono = null; }
    if (built.tail) { built.tail.holder.removeFromParent(); built.tail = null; }
    if (built.claws) { built.claws.dispose(); built.claws = null; }
    for (const [o, v] of orig.visible) o.visible = v;
    for (const [m, c] of orig.colors) { if (c.color) m.color.copy(c.color); if (c.shade) m.shadeColorFactor.copy(c.shade); }
    for (const [n, b] of orig.bust) { const bone = vrm.scene.getObjectByName(n); bone.position.copy(b.pos); bone.scale.copy(b.scale); }
  }

  function apply(cfg) {
    teardown();
    vrm.humanoid.resetNormalizedPose();
    vrm.update(0);
    vrm.scene.updateMatrixWorld(true);
    built.figure = figure.apply(cfg);   // первым: кимоно шьётся по уже изменённому телу

    // Волосы: серебро подкрашиваем в hairTint, чисто синие пряди — в hairAccent (как в klava.js).
    const tint = new THREE.Color(...(cfg.hairTint ?? [1, 1, 1]));
    const accent = new THREE.Color(...(cfg.hairAccent ?? [0.36, 0.62, 1]));
    vrm.scene.traverse((o) => {
      if (!o.isMesh) return;
      for (const m of [].concat(o.material)) {
        if (!/hair/i.test(m?.name || '') || !m.color) continue;
        const c = m.color;
        const saturatedBlue = c.b > 0.6 && c.r < 0.25 && c.g < 0.35;
        if (saturatedBlue) { c.copy(accent); m.shadeColorFactor?.copy(accent).multiplyScalar(0.7); }
        else { c.multiply(tint); m.shadeColorFactor?.multiply(tint); }
      }
    });

    const hide = new Set(cfg.hideMeshes ?? []);
    vrm.scene.traverse((o) => { if (o.isMesh && hide.has(o.name)) o.visible = false; });

    const bust = cfg.bust ?? { scale: [1, 1, 1], shift: [0, 0, 0] };
    for (const [n, side] of [['J_Sec_L_Bust1', 1], ['J_Sec_R_Bust1', -1]]) {
      const b = vrm.scene.getObjectByName(n);
      if (!b) continue;
      b.scale.set(...(bust.scale ?? [1, 1, 1]));
      b.position.x += (bust.shift?.[0] ?? 0) * side;
      b.position.y += bust.shift?.[1] ?? 0;
      b.position.z += bust.shift?.[2] ?? 0;
    }
    vrm.scene.updateMatrixWorld(true);
    vrm.springBoneManager?.setInitState();   // пружины груди — от новой посадки, а не от родной

    if (cfg.kimono && cfg.kimono.enabled !== false) {
      for (const n of cfg.kimono.hideMeshes ?? []) { const o = vrm.scene.getObjectByName(n); if (o) o.visible = false; }
      const body = vrm.scene.getObjectByName(cfg.kimono.bodyMesh ?? 'Bodybaked');
      if (body?.isSkinnedMesh) {
        // прорезь под хвост — там, где хвост: с его поправкой из редактора; хвоста нет — и дырки нет
        built.kimono = addKimono(vrm, body, { ...cfg.kimono, tailOffset: cfg.tail?.offset, tailHole: cfg.kimono.tailHole !== false && cfg.tail?.enabled !== false });
        built.kimono.top.userData.bodyUv = body.geometry.attributes.uv ?? null;   // для запекания узора в текстуру
      }
    }

    if (cfg.ears?.enabled !== false) {
      const headRaw = vrm.humanoid.getRawBoneNode('head');
      for (const side of [1, -1]) {
        const pivot = makeEar(cfg.ears ?? {}, side);
        const [ox, oy, oz] = cfg.ears?.offset ?? [0.074, 0.175, -0.018];
        const [tx, ty, tz] = cfg.ears?.tilt ?? [-0.18, 0.3, -0.3];
        // смещение — в масштабе головы (рост и размер головы из «Тела»): ушки растут вместе с ней и не тонут
        const hs = headRaw.getWorldScale(new THREE.Vector3()).x;
        const world = headRaw.getWorldPosition(new THREE.Vector3()).add(new THREE.Vector3(ox * side, oy, oz).multiplyScalar(hs));
        const holder = new THREE.Group();
        holder.name = `Ухо_${side > 0 ? 'L' : 'R'}_крепление`;
        headRaw.add(holder);
        holder.position.copy(headRaw.worldToLocal(world.clone()));
        const headQ = headRaw.getWorldQuaternion(new THREE.Quaternion());
        const restQ = new THREE.Quaternion().setFromEuler(new THREE.Euler(tx, ty * side, tz * side, 'YXZ'));
        holder.quaternion.copy(headQ.invert().multiply(restQ));
        holder.add(pivot);
        built.earHolders.push(holder);
        built.ears.push({ side, pivot });
      }
    }

    if (cfg.tail?.enabled !== false) {
      built.tail = addTail(vrm, {
        gradientMap: toonGradient, strandTex: strandTexture(707, 1100, 6, 12, 5, 11),
        color: cfg.ears?.outer, tip: cfg.ears?.tuft, ...(cfg.tail ?? {}),
      });
      built.tail.holder.name = 'Хвост';
    }

    // коготки — после фигуры (длина пальцев из «Тела» уже в костях), в позе покоя
    if (cfg.claws?.enabled !== false) built.claws = addClaws(vrm, { gradientMap: toonGradient, ...(cfg.claws ?? {}) });

    applyTextures(cfg.textureOverrides ?? {});
    // после своих текстур: у материала со своей текстурой (textureOverrides) или с несохранёнными мазками кисти
    // раскраску не трогаем — иначе она затёрла бы нарисованное
    const own = new Set(Object.keys(cfg.textureOverrides ?? {}));
    for (const m of orig.maps.keys()) if (m.userData.painted) own.add(m.name);
    bodyPaint.apply(cfg.body?.skin ?? null, cfg.bodyPaint ?? null, own);
    return built;
  }

  // Свои текстуры: {имя материала: путь к PNG}. Цвет и тень MToon берут одну и ту же картинку.
  function applyTextures(overrides) {
    for (const [m, o] of orig.maps) {
      // контур «имя (Outline)» берёт картинку своего материала
      const path = overrides[m.name] ?? overrides[m.name.replace(/\s*\(Outline\)\s*$/, '')];
      if (!path) {
        if (m.userData.painted) continue;   // только что нарисованное в редакторе не откатываем
        m.map = o.map;
        if ('shadeMultiplyTexture' in m) m.shadeMultiplyTexture = o.shade;
        m.needsUpdate = true;
        continue;
      }
      loader.load(`${path}?v=${Date.now()}`, (tex) => {
        tex.flipY = o.map?.flipY ?? false;
        tex.colorSpace = THREE.SRGBColorSpace;
        m.map = tex;
        if ('shadeMultiplyTexture' in m && o.shade) m.shadeMultiplyTexture = tex;
        m.needsUpdate = true;
      });
    }
  }

  return { apply, built, orig, materials: () => [...orig.maps.keys()], bodyPaintAvailable: bodyPaint.available };
}
