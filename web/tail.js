// Пушистый кошачий хвост. Цепочка костей растёт из копчика назад, на ней — трубка со скиннингом:
// толще к середине, закруглённый кончик. Мех — те же оболочки, что у ушей: копии трубки, раздутые по
// нормали, с порогом прозрачности по шуму волосков; все оболочки на том же скелете, гнутся вместе.
// Живёт сам: бегущая волна от основания к кончику, кончик загибается «вопросиком». Настроение меняет
// всё: радость — выше и бодрее, злость — хлещет, грусть — опускается, когда гладят — медленные волны.
import * as THREE from 'three';
import { restTransform } from './kimono.js';

const RINGS = 44, SIDES = 18;

/**
 * Где у неё копчик: точка на коже по средней линии спины у верхнего конца межъягодичной складки.
 * Ищется по самой сетке тела в покое — правки фигуры («Попа», «Таз вширь»…) в ней уже есть, поэтому хвост
 * встаёт на место, как бы ягодицы ни раздули. Раньше корень считался от костей (позвоночник − 5,7 см,
 * таз − 8,8 см назад): при пышной попе он оказывался посреди складки, а хвост уходил вниз вдоль неё.
 * Возвращает точку в мире: x — по центру таза, z — на коже.
 */
export function tailRoot(vrm) {
  vrm.scene.updateMatrixWorld(true);
  const wp = (n) => vrm.humanoid.getRawBoneNode(n)?.getWorldPosition(new THREE.Vector3()) ?? null;
  const hips = wp('hips') ?? wp('spine');
  const spine = wp('spine') ?? hips.clone().add(new THREE.Vector3(0, 0.1, 0));
  const leg = wp('leftUpperLeg') ?? hips.clone().add(new THREE.Vector3(0, -0.05, 0));
  const cx = hips.x;
  // карта глубины спины: клетки 4×4 мм по x (±8 см от середины) и y, в клетке — самая задняя точка кожи.
  // Треугольники растеризуются целиком: у VRoid-тела пара тысяч вершин, по одним вершинам срезы выходят
  // дырявыми, и складка «находилась» на пару сантиметров ниже — корень садился внутрь неё
  const CELL = 0.004, W = 41, C = 20;
  const X0 = cx - C * CELL, y0 = Math.min(leg.y, hips.y) - 0.06, y1 = spine.y + 0.04;
  const H = Math.ceil((y1 - y0) / CELL) + 1;
  const depth = new Float32Array(W * H).fill(Infinity);
  const skip = (o) => {
    // только сама кожа: свои вещи Клавы (кимоно, уши, хвост — имя у их держателей), волосы и спрятанное не в счёт
    for (let a = o; a; a = a.parent) if (!a.visible || /Klava|Хвост|Ухо|hair/i.test(a.name)) return true;
    return false;
  };
  const v = new THREE.Vector3();
  vrm.scene.traverse((o) => {
    if (!o.isSkinnedMesh || skip(o)) return;
    const geo = o.geometry, P = geo.attributes.position;
    if (!P || !geo.attributes.skinIndex) return;
    const toWorld = restTransform(o);
    const w = new Float32Array(P.count * 3);
    for (let i = 0; i < P.count; i++) {
      v.fromBufferAttribute(P, i).applyMatrix4(toWorld(i));
      w[i * 3] = v.x; w[i * 3 + 1] = v.y; w[i * 3 + 2] = v.z;
    }
    const ix = geo.index?.array, tris = ix ? ix.length / 3 : P.count / 3;
    for (let t = 0; t < tris; t++) {
      const a = (ix ? ix[t * 3] : t * 3) * 3, b = (ix ? ix[t * 3 + 1] : t * 3 + 1) * 3, c = (ix ? ix[t * 3 + 2] : t * 3 + 2) * 3;
      const ax = w[a], ay = w[a + 1], az = w[a + 2], bx = w[b], by = w[b + 1], bz = w[b + 2], qx = w[c], qy = w[c + 1], qz = w[c + 2];
      if (Math.min(az, bz, qz) > hips.z) continue;   // −Z — спина; перёд не нужен
      const c0 = Math.max(0, Math.ceil((Math.min(ax, bx, qx) - X0) / CELL)), c1 = Math.min(W - 1, Math.floor((Math.max(ax, bx, qx) - X0) / CELL));
      const r0 = Math.max(0, Math.ceil((Math.min(ay, by, qy) - y0) / CELL)), r1 = Math.min(H - 1, Math.floor((Math.max(ay, by, qy) - y0) / CELL));
      if (c0 > c1 || r0 > r1) continue;
      const d = (by - qy) * (ax - qx) + (qx - bx) * (ay - qy);
      if (Math.abs(d) < 1e-12) continue;
      for (let r = r0; r <= r1; r++) {
        const py = y0 + r * CELL;
        for (let col = c0; col <= c1; col++) {
          const px = X0 + col * CELL;
          const l1 = ((by - qy) * (px - qx) + (qx - bx) * (py - qy)) / d;
          const l2 = ((qy - ay) * (px - qx) + (ax - qx) * (py - qy)) / d;
          const l3 = 1 - l1 - l2;
          if (l1 < -1e-6 || l2 < -1e-6 || l3 < -1e-6) continue;
          const z = l1 * az + l2 * bz + l3 * qz;
          if (z < depth[r * W + col]) depth[r * W + col] = z;
        }
      }
    }
  });
  // профили по высоте: середина спины (±4 мм) и ягодицы (1,2–4 см от середины)
  const mid = new Float32Array(H).fill(Infinity), cheek = new Float32Array(H).fill(Infinity);
  for (let r = 0; r < H; r++) for (let col = 0; col < W; col++) {
    const dx = Math.abs(col - C) * CELL, z = depth[r * W + col];
    if (dx <= 0.004) mid[r] = Math.min(mid[r], z);
    else if (dx >= 0.012 && dx <= 0.04) cheek[r] = Math.min(cheek[r], z);
  }
  // Глубина складки не годится: у VRoid-тела ложбинка вдоль позвоночника (~2 см) тянется через всю поясницу.
  // Ориентир — сами ягодицы: вершина (самая задняя точка между тазобедренным суставом и поясницей) и подъём
  // от неё к пояснице. Копчик — верхний конец складки — там, где выпуклость спала на 60 %: по замерам это
  // 4–6 см над вершиной, сразу под оби.
  const row = (y) => THREE.MathUtils.clamp(Math.round((y - y0) / CELL), 0, H - 1);
  let apex = -1;
  for (let r = row(leg.y - 0.03); r <= row(spine.y); r++) if (isFinite(cheek[r]) && (apex < 0 || cheek[r] < cheek[apex])) apex = r;
  let low = row(spine.y);
  while (low > apex && !isFinite(cheek[low])) low--;
  let k;
  if (apex >= 0 && low > apex && cheek[low] - cheek[apex] > 0.01) {
    const ref = cheek[apex] + 0.6 * (cheek[low] - cheek[apex]);
    for (k = apex; k < low && !(isFinite(cheek[k]) && cheek[k] >= ref); k++);
  } else k = row(hips.y + 0.5 * (spine.y - hips.y));   // ягодиц не видно (штаны, плоская сетка)
  for (let d = 0; d < 8 && !isFinite(mid[k]); d++) k = Math.min(H - 1, k + 1);
  return new THREE.Vector3(cx, y0 + k * CELL, isFinite(mid[k]) ? mid[k] : hips.z - 0.09);
}

export function addTail(vrm, opts) {
  const {
    gradientMap, strandTex, color = [0.42, 0.64, 1], tip = [1, 0.97, 1],
    length = 0.8, radius = 0.05, segments = 12,
    // offset — поправка к копчику из редактора: [в сторону, выше(+)/ниже(−), глубже в тело(+)/дальше от спины(−)]
    offset = [0, 0, 0], furLayers = 16, furLen = 0.042,
  } = opts;
  const hipsRaw = vrm.humanoid.getRawBoneNode('hips') ?? vrm.humanoid.getRawBoneNode('spine');
  const at = tailRoot(vrm).add(new THREE.Vector3(offset[0] ?? 0, offset[1] ?? 0, offset[2] ?? 0));

  // Держатель в кости таза (копчик ходит вместе с тазом), но с осями мира в покое:
  // +Z — к зрителю, −Z — назад, +X — её левая сторона.
  const holder = new THREE.Group();
  hipsRaw.add(holder);
  holder.position.copy(hipsRaw.worldToLocal(at));
  holder.quaternion.copy(hipsRaw.getWorldQuaternion(new THREE.Quaternion()).invert());
  holder.updateMatrixWorld(true);

  // Кости: корень на копчике, дальше — назад по −Z.
  const seg = length / segments;
  const bones = [];
  for (let j = 0; j <= segments; j++) {
    const b = new THREE.Bone();
    b.position.set(0, 0, j === 0 ? 0 : -seg);
    (j === 0 ? holder : bones[j - 1]).add(b);
    bones.push(b);
  }
  holder.updateMatrixWorld(true);
  const skeleton = new THREE.Skeleton(bones);

  // Трубка вдоль −Z. s — доля длины 0..1; радиус: тонкий у основания, пухлый к середине, круглый кончик.
  const rad = (s) => radius * (0.5 + 0.5 * Math.sin(Math.min(1, s * 1.7) * Math.PI / 2)) * (s > 0.86 ? Math.sqrt(Math.max(0, Math.cos((s - 0.86) / 0.14 * Math.PI / 2))) : 1);
  const pos = [], nrm = [], uv = [], col = [], si = [], sw = [], idx = [];
  const cBase = new THREE.Color(...color), cTip = new THREE.Color(...tip);
  for (let i = 0; i <= RINGS; i++) {
    const s = i / RINGS, z = -s * length, r = Math.max(0.0015, rad(s));
    // вес по костям: вершина между суставами j и j+1 смешивается с соседом у концов отрезка
    const f = Math.min(segments - 1e-6, s * segments), j = Math.floor(f), fr = f - j;
    let b0 = j, b1 = j, w0 = 1, w1 = 0;
    if (fr > 0.5 && j + 1 <= segments) { b1 = j + 1; w1 = fr - 0.5; w0 = 1 - w1; }
    else if (fr < 0.5 && j > 0) { b1 = j - 1; w1 = 0.5 - fr; w0 = 1 - w1; }
    const c = cBase.clone().lerp(cTip, THREE.MathUtils.smoothstep(s, 0.78, 0.97));
    for (let k = 0; k <= SIDES; k++) {
      const a = (k / SIDES) * Math.PI * 2;
      const nx = Math.cos(a), ny = Math.sin(a);
      pos.push(nx * r, ny * r, z);
      nrm.push(nx, ny, 0);
      uv.push(k / SIDES, s);
      col.push(c.r, c.g, c.b);
      si.push(b0, b1, 0, 0);
      sw.push(w0, w1, 0, 0);
    }
  }
  for (let i = 0; i < RINGS; i++) for (let k = 0; k < SIDES; k++) {
    const a = i * (SIDES + 1) + k, b = a + 1, c = a + SIDES + 1, d = c + 1;
    idx.push(a, b, c, b, d, c);
  }
  const base = new THREE.BufferGeometry();
  base.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  base.setAttribute('normal', new THREE.Float32BufferAttribute(nrm, 3));
  base.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  base.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  base.setAttribute('skinIndex', new THREE.Uint16BufferAttribute(si, 4));
  base.setAttribute('skinWeight', new THREE.Float32BufferAttribute(sw, 4));
  base.setIndex(idx);

  const meshes = [];
  const add = (geo, mat) => {
    const m = new THREE.SkinnedMesh(geo, mat);
    m.frustumCulled = false;
    m.renderOrder = 2;
    holder.add(m);
    m.bind(skeleton, holder.matrixWorld);
    meshes.push(m);
  };
  add(base, new THREE.MeshToonMaterial({ color: 0xffffff, vertexColors: true, gradientMap }));

  // Мех: чем дальше слой, тем выше порог прозрачности — от шума остаются только кончики волосков.
  // Волоски расчёсаны к кончику хвоста (−Z) и у основания короче (прячутся под кимоно).
  const p = base.attributes.position, n = base.attributes.normal, t = base.attributes.uv;
  for (let L = 1; L <= furLayers; L++) {
    const h = L / furLayers;
    const arr = new Float32Array(p.count * 3);
    for (let i = 0; i < p.count; i++) {
      const s = t.getY(i);
      const len = furLen * (0.35 + 0.65 * Math.min(1, s * 3)) * h;
      arr[i * 3] = p.getX(i) + n.getX(i) * len;
      arr[i * 3 + 1] = p.getY(i) + n.getY(i) * len;
      arr[i * 3 + 2] = p.getZ(i) + n.getZ(i) * len - 0.6 * len * h;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(arr, 3));
    for (const a of ['normal', 'uv', 'color', 'skinIndex', 'skinWeight']) geo.setAttribute(a, base.getAttribute(a));
    geo.setIndex(base.index);
    const shade = 0.7 + 0.3 * Math.pow(h, 0.7);
    add(geo, new THREE.MeshToonMaterial({
      color: new THREE.Color(shade, shade, shade), vertexColors: true, gradientMap,
      alphaMap: strandTex, alphaTest: 0.03 + 0.82 * h, side: THREE.DoubleSide, alphaToCoverage: true,   // мех: кончики ворсинок мягкие, не лесенкой
    }));
  }

  // ---------- жизнь хвоста ----------
  let t0 = Math.random() * 10, flick = 0, nextFlick = 4;
  const pitch = new Float32Array(segments + 1), yaw = new Float32Array(segments + 1);
  function update(dt, S) {
    t0 += dt;
    const e = S.emotion, pl = S.pleased;
    const happy = e.happy + 0.2 * e.relaxed, angry = e.angry, sad = e.sad, surp = e.surprised;
    const amp = 0.16 + 0.22 * happy + 0.28 * angry - 0.08 * sad + 0.14 * pl + (S.speaking ? 0.05 : 0);
    const speed = 0.9 + 1.2 * happy + 3.2 * angry + 0.8 * surp - 0.35 * sad - 0.3 * pl;
    const lift = 0.3 * happy + 0.45 * surp + 0.15 * pl - 0.45 * sad + 0.1 * angry;
    const curlTip = 0.55 + 0.35 * happy + 0.3 * surp - 0.4 * sad;
    if (t0 > nextFlick) { flick = 1; nextFlick = t0 + 3 + Math.random() * 6; }
    flick = Math.max(0, flick - dt * 2.5);
    const fl = Math.sin((1 - flick) * Math.PI * 2) * flick;
    // Красивая кошачья поза с выразительным S-образным боковым изгибом:
    // Хвост выходит от копчика назад, плавно огибает бедро вбок (+X / вправо) и грациозно поднимается
    // пушистым кончиком вверх («вопросиком»), поэтому он ВЕЛИКОЛЕПНО ВИДЕН спереди рядом с талией Клавы!
    // Настроение поднимает хвост выше (радость/удивление — хвост трубой), в танце он сочно летает в такт бёдрам.
    const isDancing = S.groove?.on || S.dance || (S.speaking && /caramell|dance/i.test(S.gesture ?? ''));
    const danceBoost = isDancing ? 2.2 : 1.0;
    const effLift = Math.max(lift, isDancing ? 0.45 : -0.2);
    const down = THREE.MathUtils.clamp(0.65 - effLift * 0.7, 0.15, 1.2);
    const hook = 0.65 + 0.6 * curlTip + (isDancing ? 0.3 : 0);
    const stand = (s) => -(0.08 + down * THREE.MathUtils.smoothstep(s, 0, 0.55)) + hook * THREE.MathUtils.smoothstep(s, 0.55, 1)
      + 0.45 * effLift * (1 - 0.4 * s);
    // сидит в игровом кресле: от копчика круто вниз в зазор между сиденьем и спинкой, у пола загибается назад
    // и ложится сбоку от кресла
    const sit = THREE.MathUtils.clamp(S.sit ?? 0, 0, 1);
    const seated = (s) => -(0.9 + 0.45 * THREE.MathUtils.smoothstep(s, 0, 0.4)) + 1.3 * THREE.MathUtils.smoothstep(s, 0.45, 0.85)
      + 0.25 * curlTip * THREE.MathUtils.smoothstep(s, 0.85, 1);
    const dir = (s) => stand(s) * (1 - sit) + seated(s) * sit;

    // Боковой вынос (yaw): хвост плавно выглядывает вправо (+X), огибая бедро, а кончик игриво завивается
    const sideRest = (s) => 0.38 * THREE.MathUtils.smoothstep(s, 0.15, 0.85) * (1 - 0.5 * sit);
    // Раскачка таза в танце передаётся на хвост с упругой задержкой волны
    const hipsInfluence = (S.hipsX ?? 0) * 3.8;

    for (let j = 0; j <= segments; j++) {
      const s = j / segments;
      const shape = j === 0 ? dir(0) : dir(s) - dir((j - 1) / segments);
      const sideShape = j === 0 ? sideRest(0) : sideRest(s) - sideRest((j - 1) / segments);
      const waveFreq = isDancing ? 2.75 : 2.2;
      const wave = Math.sin(t0 * speed * waveFreq - j * 0.45) * amp * (0.35 + 0.85 * s) * danceBoost * (1 - 0.6 * sit);
      pitch[j] += (shape + Math.sin(t0 * speed * 1.3 - j * 0.4) * 0.04 - pitch[j]) * Math.min(1, dt * 7);
      yaw[j] += (sideShape + wave + hipsInfluence * s * 0.25 + fl * 0.35 * s * s - yaw[j]) * Math.min(1, dt * 10);
      bones[j].rotation.set(pitch[j], yaw[j], j === 0 ? 0 : Math.sin(t0 * speed - j) * 0.04);
    }
  }

  return { holder, bones, meshes, update };
}
