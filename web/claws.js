// Кошачьи коготки на кончиках пальцев (оба кулачка, 10 штук). Кончик пальца ищется по самой коже: вершины,
// которыми правит кость последней фаланги, в позе покоя — самая дальняя по направлению пальца и есть кончик,
// толщина пальца — по тем же вершинам у кончика. Коготь — изогнутый заострённый конус: растёт с верха фаланги
// (где ноготь), выходит за подушечку и загибается к ладони. Висит на кости фаланги — гнётся вместе с пальцем.
// Живёт по настроению, как у кошки: спокойна — коготки чуть втянуты, злится или удивлена — выпущены целиком;
// «ня / лапки-царапки» — выпустить на пару секунд (flash).
import * as THREE from 'three';
import { restTransform } from './kimono.js';

const FINGERS = ['Thumb', 'Index', 'Middle', 'Ring', 'Little'];
const SEG = 12, SIDES = 12;

/**
 * Коготь вдоль +Y: основание в нуле, кончик на length; загиб к −Z (к ладони), сечение — овал (шире, чем толще).
 * У основания толстый, к кончику острый. Цвет — от base у основания к tip на кончике (вершинные цвета).
 */
function clawGeometry(length, radius, curl, base, tip) {
  const pos = [], nrm = [], colr = [], idx = [];
  const X = new THREE.Vector3(1, 0, 0);
  for (let s = 0; s <= SEG; s++) {
    const t = s / SEG;
    const c = new THREE.Vector3(0, length * t, -curl * length * t * t);
    const T = new THREE.Vector3(0, length, -2 * curl * length * t).normalize();
    const N = new THREE.Vector3().crossVectors(X, T).normalize();          // ⟂ оси в плоскости изгиба, «спинка» когтя
    const r = radius * Math.pow(1 - t, 0.75) + 1e-5;
    const k = Math.pow(t, 1.6);
    const cr = base[0] + (tip[0] - base[0]) * k, cg = base[1] + (tip[1] - base[1]) * k, cb = base[2] + (tip[2] - base[2]) * k;
    for (let j = 0; j <= SIDES; j++) {
      const a = (j / SIDES) * Math.PI * 2;
      const ca = Math.cos(a), sa = Math.sin(a);
      const p = c.clone().addScaledVector(X, ca * r * 1.2).addScaledVector(N, sa * r * 0.85);
      pos.push(p.x, p.y, p.z);
      const n = new THREE.Vector3().addScaledVector(X, ca / 1.2).addScaledVector(N, sa / 0.85).normalize();
      nrm.push(n.x, n.y, n.z);
      colr.push(cr, cg, cb);
    }
  }
  for (let s = 0; s < SEG; s++) for (let j = 0; j < SIDES; j++) {
    const a = s * (SIDES + 1) + j, b = a + SIDES + 1;
    // обход против часовой снаружи (иначе лицевые грани смотрят внутрь: коготь выглядел тонкой иглой — была видна
    // только дальняя стенка, а контур закрывал его целиком)
    idx.push(a, b, a + 1, b, b + 1, a + 1);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nrm, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(colr, 3));
  g.setIndex(idx);
  return g;
}

/**
 * Кончики пальцев в позе покоя (мир): для каждой последней фаланги — кость, направление пальца, точка кончика
 * на коже и толщина пальца у кончика. Своё (кимоно, уши, хвост) и спрятанное не смотрим.
 */
export function fingerTips(vrm) {
  vrm.scene.updateMatrixWorld(true);
  const tips = [];
  for (const side of ['left', 'right']) for (const f of FINGERS) {
    const distal = vrm.humanoid.getRawBoneNode(`${side}${f}Distal`);
    if (!distal?.parent) continue;
    const p0 = distal.parent.getWorldPosition(new THREE.Vector3());
    const p1 = distal.getWorldPosition(new THREE.Vector3());
    const len = p0.distanceTo(p1);
    if (len < 1e-4) continue;
    tips.push({ side, finger: f, bone: distal, p1, dir: p1.clone().sub(p0).normalize(), len, best: -Infinity, tip: null, pts: [] });
  }
  if (tips.length === 0) return tips;
  const skip = (o) => {
    for (let a = o; a; a = a.parent) if (!a.visible || /Klava|Хвост|Ухо|Коготь|hair/i.test(a.name)) return true;
    return false;
  };
  const v = new THREE.Vector3();
  vrm.scene.traverse((o) => {
    if (!o.isSkinnedMesh || skip(o)) return;
    const geo = o.geometry, P = geo.attributes.position, si = geo.attributes.skinIndex, sw = geo.attributes.skinWeight;
    if (!P || !si || !sw) return;
    const want = tips.map((t) => o.skeleton.bones.indexOf(t.bone));
    if (want.every((x) => x < 0)) return;
    const toWorld = restTransform(o);
    for (let i = 0; i < P.count; i++) {
      let bi = -1, bw = 0;
      for (let c = 0; c < 4; c++) { const w = sw.getComponent(i, c); if (w > bw) { bw = w; bi = si.getComponent(i, c); } }
      const k = bi < 0 || bw < 0.5 ? -1 : want.indexOf(bi);
      if (k < 0) continue;
      v.fromBufferAttribute(P, i).applyMatrix4(toWorld(i));
      const t = tips[k];
      const proj = v.clone().sub(t.p1).dot(t.dir);
      t.pts.push(v.clone());
      if (proj > t.best) { t.best = proj; t.tip = v.clone(); }
    }
  });
  for (const t of tips) {
    // кожи на кости нет (другая модель) — кончик по кости: фаланга продолжается на свою длину
    if (!t.tip) { t.tip = t.p1.clone().addScaledVector(t.dir, t.len * 0.9); t.radius = t.len * 0.35; continue; }
    // толщина — среднее расстояние от оси пальца у вершин последней трети фаланги
    const near = t.pts.filter((p) => p.clone().sub(t.p1).dot(t.dir) > t.best - Math.max(0.006, t.len * 0.45));
    let sum = 0;
    for (const p of near) {
      const d = p.clone().sub(t.p1);
      sum += d.sub(t.dir.clone().multiplyScalar(d.dot(t.dir))).length();
    }
    t.radius = near.length ? sum / near.length : t.len * 0.35;
    delete t.pts;
  }
  return tips;
}

/**
 * Коготки. opts: color (кончик) и base (основание) — [r,g,b] 0..1, length — длина в метрах (0.015), width —
 * толщина у основания относительно пальца (0.8), curl — загиб (0.7), outline — цвет контура ([r,g,b] или null),
 * glow — свечение, gradientMap — ступени тонового света.
 * Вернёт { claws, update(dt, S), flash(sec), setVisible(on), dispose() }.
 */
export function addClaws(vrm, opts = {}) {
  const tips = fingerTips(vrm);
  // по умолчанию — розовые у основания (как сакура на кимоно) и белые к острию: на бежевой коже иначе не видно
  const tipCol = opts.color ?? [1.0, 0.98, 0.96];
  const baseCol = opts.base ?? [0.98, 0.42, 0.66];
  const mat = new THREE.MeshToonMaterial({
    color: 0xffffff, vertexColors: true, gradientMap: opts.gradientMap ?? null,
    emissive: new THREE.Color(...(opts.glow ?? [0.05, 0.05, 0.07])),
  });
  // обводка как у самой модели (MToon рисует тёмный контур): копия чуть толще, изнанкой наружу
  const outlineCol = opts.outline ?? [0.22, 0.03, 0.08];
  const outlineMat = outlineCol ? new THREE.MeshBasicMaterial({ color: new THREE.Color(...outlineCol), side: THREE.BackSide }) : null;
  const claws = [];
  // тыльная сторона кисти — из самих костей, а не из догадки про позу покоя (у VRoid ладони в покое не вниз:
  // коготки по «вверх» вылезли из подушечек): поперёк ладони (от мизинца к указательному) × вдоль пальца
  const bonePos = (n) => vrm.humanoid.getRawBoneNode(n)?.getWorldPosition(new THREE.Vector3()) ?? null;
  const dorsalOf = {};
  for (const side of ['left', 'right']) {
    const hand = bonePos(`${side}Hand`), idx = bonePos(`${side}IndexProximal`), lit = bonePos(`${side}LittleProximal`), mid = bonePos(`${side}MiddleProximal`);
    if (!hand || !idx || !lit || !mid) continue;
    const across = idx.clone().sub(lit).normalize();
    const along = mid.clone().sub(hand).normalize();
    const n = side === 'right' ? along.clone().cross(across) : across.clone().cross(along);
    dorsalOf[side] = { n: n.normalize(), across };
  }
  for (const t of tips) {
    const hd = dorsalOf[t.side];
    let dorsal = hd ? hd.n.clone() : new THREE.Vector3(0, 1, 0);
    // у большого пальца ноготь смотрит вбок — наружу от ладони, к указательному
    if (t.finger === 'Thumb' && hd) dorsal.multiplyScalar(0.5).addScaledVector(hd.across, 0.85);
    dorsal.sub(t.dir.clone().multiplyScalar(dorsal.dot(t.dir))).normalize();
    // 1,5 см на полтора метра роста — по-анимешному крупно: на аватаре в окне 440×900 меньше просто не видно
    const length = (opts.length ?? 0.015) * (t.finger === 'Thumb' ? 1.1 : t.finger === 'Little' ? 0.8 : 1);
    const radius = Math.max(0.0015, t.radius * (opts.width ?? 0.8));
    const geo = clawGeometry(length, radius, opts.curl ?? 0.7, baseCol, tipCol);
    const mesh = new THREE.Mesh(geo, mat);
    mesh.name = `Коготь_${t.side}_${t.finger}`;
    mesh.frustumCulled = false;
    if (outlineMat) {
      const og = geo.clone();
      const P = og.attributes.position, N = og.attributes.normal;
      for (let i = 0; i < P.count; i++) P.setXYZ(i, P.getX(i) + N.getX(i) * 0.00045, P.getY(i) + N.getY(i) * 0.00045, P.getZ(i) + N.getZ(i) * 0.00045);
      const line = new THREE.Mesh(og, outlineMat);
      line.name = `${mesh.name}_контур`;
      line.frustumCulled = false;
      mesh.add(line);
    }
    // основание — на верху фаланги чуть позади кончика (под ноготь), ось когтя — вдоль пальца
    const baseW = t.tip.clone().addScaledVector(t.dir, -0.45 * length).addScaledVector(dorsal, t.radius * 0.55);
    const Y = t.dir.clone(), Z = dorsal.clone(), X = new THREE.Vector3().crossVectors(Y, Z).normalize();
    const world = new THREE.Matrix4().makeBasis(X, Y, Z).setPosition(baseW);
    const local = new THREE.Matrix4().copy(t.bone.matrixWorld).invert().multiply(world);
    const holder = new THREE.Group();
    holder.name = `Коготь_${t.side}_${t.finger}_держатель`;
    local.decompose(holder.position, holder.quaternion, holder.scale);
    t.bone.add(holder);
    holder.add(mesh);
    claws.push(mesh);
  }
  let ext = 0.85, flashT = 0, visible = opts.enabled !== false;
  return {
    claws,
    /** Снять коготки с костей (пересборка облика в редакторе). */
    dispose() {
      for (const m of claws) m.parent?.removeFromParent();
      for (const m of claws) { m.geometry.dispose(); for (const c of m.children) c.geometry?.dispose(); }
      mat.dispose();
      outlineMat?.dispose();
    },
    flash(sec = 2.5) { flashT = Math.max(flashT, sec); },
    setVisible(on) { visible = on; for (const m of claws) m.visible = on; },
    update(dt, S) {
      if (!visible || claws.length === 0) return;
      flashT = Math.max(0, flashT - dt);
      const e = S?.emotion ?? {};
      // спокойна — чуть втянуты (85 %), злится/удивлена/играет — наружу целиком
      const want = flashT > 0 ? 1.1 : Math.min(1, 0.85 + 0.15 * (e.angry ?? 0) + 0.1 * (e.surprised ?? 0) + 0.05 * (e.happy ?? 0));
      ext += (want - ext) * Math.min(1, dt * 8);
      for (const m of claws) m.scale.set(0.9 + 0.1 * ext, ext, 0.9 + 0.1 * ext);
    },
  };
}
