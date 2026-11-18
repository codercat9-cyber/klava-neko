// Фигура поверх любой VRM: пропорции тела, цвет кожи, перекраска и посадка вещей. Общая для живой Клавы
// (klava.js) и редактора (look.js → editor.js). Разделы config.json:
//   body:   множители, 1 — как в модели: height, head, neckLen, neck, shoulders, torsoLen, chest, waist, hips, butt,
//           armLen, arms, hands, legLen, thighs, calves, feet; skin: [r, g, b] — цвет кожи (красит bodypaint.js — только пиксели кожи)
//   outfit: tint {материал: [r, g, b]}, gain {материал: яркость}, fit {сетка: метры — свободнее (+) / в облипку (−)}
//
// Как устроено, чтобы подходило к любой VRM и не ломало анимацию:
//  - длины (шея, торс, плечи, руки, ноги) — суставы-дети отъезжают вдоль кости, а сетка между суставами
//    растягивается по доле длины (вершина посреди бедра уезжает на половину прибавки). Масштаба по осям
//    у костей нет, поэтому при сгибе колена голень не перекашивает.
//  - голова, кисти, стопы — равномерный масштаб кости (с головой растут волосы и ушки).
//  - толщины — правка сетки в позе покоя: вершина отходит от оси своей кости пропорционально весу этой кости.
//    Одежда висит на тех же костях — раздаётся вместе с телом.
//  - нормализованный скелет three-vrm (по нему считают жесты, IK лапок и камера) подгоняется под новые длины,
//    пружины (волосы, грудь) запоминают новое состояние покоя.
//  - рост — масштаб всей модели; ноги длиннее или стопы больше — модель приподнимается, стопы стоят на полу.
// apply() идемпотентен: перед сборкой всё возвращается к исходному (редактор пересобирает на каждый ползунок).
import * as THREE from 'three';
import { VRMHumanBoneList } from '@pixiv/three-vrm';
import { restTransform } from './kimono.js';

export const BODY_DEFAULTS = {
  height: 1, head: 1, neckLen: 1, neck: 1, shoulders: 1, torsoLen: 1, chest: 1, waist: 1, hips: 1, butt: 1,
  armLen: 1, arms: 1, hands: 1, legLen: 1, thighs: 1, calves: 1, feet: 1,
};

// Толщина: кость → [параметр, куда смотрит её ось (первая существующая кость из списка)].
// Таз особый: «hips» — только вширь, «butt» — только назад (попа), живот не трогаем.
const THICK = {
  hips: ['hips', ['spine']], spine: ['waist', ['chest', 'upperChest', 'neck']],
  chest: ['chest', ['upperChest', 'neck']], upperChest: ['chest', ['neck', 'head']], neck: ['neck', ['head']],
  leftUpperArm: ['arms', ['leftLowerArm']], rightUpperArm: ['arms', ['rightLowerArm']],
  leftLowerArm: ['arms', ['leftHand']], rightLowerArm: ['arms', ['rightHand']],
  leftUpperLeg: ['thighs', ['leftLowerLeg']], rightUpperLeg: ['thighs', ['rightLowerLeg']],
  leftLowerLeg: ['calves', ['leftFoot']], rightLowerLeg: ['calves', ['rightFoot']],
};

// Длины: [параметр, какие суставы отъезжают от родителя, 'x' — только поперёк (плечи)].
const STRETCH = [
  ['torsoLen', ['spine', 'chest', 'upperChest', 'neck']],
  ['neckLen', ['head']],
  ['shoulders', ['leftShoulder', 'rightShoulder', 'leftUpperArm', 'rightUpperArm'], 'x'],
  ['armLen', ['leftLowerArm', 'rightLowerArm', 'leftHand', 'rightHand']],
  ['legLen', ['leftLowerLeg', 'rightLowerLeg', 'leftFoot', 'rightFoot']],
];

// Равномерный масштаб кости целиком.
const GROW = [['head', ['head']], ['hands', ['leftHand', 'rightHand']], ['feet', ['leftFoot', 'rightFoot']]];

const baseName = (name) => (name ?? '').replace(/\s*\(Outline\)\s*$/, '');
const isOne = (v) => Math.abs(v - 1) < 1e-4;

export function createFigure(vrm) {
  const H = vrm.humanoid;
  const raw = (n) => H.getRawBoneNode(n);
  const norm = (n) => H.getNormalizedBoneNode(n);
  const bones = VRMHumanBoneList.filter((n) => raw(n) && norm(n));
  const nameOfRaw = new Map(bones.map((n) => [raw(n), n]));
  const nameOfNorm = new Map(bones.map((n) => [norm(n), n]));

  // всё, что правим, запоминаем в исходном виде — только из самой модели, до кимоно/ушек/хвоста
  const meshes = [];
  vrm.scene.traverse((o) => { if (o.isMesh) meshes.push(o); });
  const skinned = meshes.filter((o) => o.isSkinnedMesh);
  const orig = {
    raw: new Map(bones.map((n) => [n, { pos: raw(n).position.clone(), scale: raw(n).scale.clone() }])),
    norm: new Map(bones.map((n) => [n, norm(n).position.clone()])),
    rest: JSON.parse(JSON.stringify(H.normalizedRestPose)),
    scene: { pos: vrm.scene.position.clone(), scale: vrm.scene.scale.clone() },
    geo: new Map(),
    nrm: new Map(),
    colors: new Map(),
  };
  for (const o of meshes) {
    const a = o.geometry.attributes.position;
    if (a && !orig.geo.has(a)) orig.geo.set(a, a.array.slice());
    const n = o.geometry.attributes.normal;
    if (n && !orig.nrm.has(n)) orig.nrm.set(n, n.array.slice());
    for (const m of [].concat(o.material)) {
      if (m?.color && !orig.colors.has(m)) orig.colors.set(m, { color: m.color.clone(), shade: m.shadeColorFactor?.clone() ?? null });
    }
  }
  const touched = new Set();

  function restore() {
    vrm.springBoneManager?.reset();
    for (const a of touched) { a.array.set(orig.geo.get(a)); a.needsUpdate = true; }
    touched.clear();
    for (const [n, arr] of orig.nrm) { n.array.set(arr); n.needsUpdate = true; }
    for (const [n, s] of orig.raw) { raw(n).position.copy(s.pos); raw(n).scale.copy(s.scale); }
    for (const [n, p] of orig.norm) norm(n).position.copy(p);
    const rp = H.normalizedRestPose;
    for (const [n, r] of Object.entries(orig.rest)) if (r?.position && rp[n]) rp[n].position = r.position.slice();
    vrm.scene.position.copy(orig.scene.pos);
    vrm.scene.scale.copy(orig.scene.scale);
    for (const [m, c] of orig.colors) { m.color.copy(c.color); if (c.shade) m.shadeColorFactor.copy(c.shade); }
    vrm.scene.updateMatrixWorld(true);
  }

  // ближайшая гуманоидная кость вверх по скелету (для прядей, груди, юбки — кость, к которой они растут)
  const humanOf = (bone) => {
    for (let p = bone; p; p = p.parent) if (nameOfRaw.has(p)) return nameOfRaw.get(p);
    return null;
  };
  const humanParent = (n) => humanOf(raw(n).parent);

  function apply(cfg) {
    restore();
    const b = { ...BODY_DEFAULTS, ...(cfg.body ?? {}) };
    const out = cfg.outfit ?? {};
    H.resetNormalizedPose();
    H.update();
    vrm.scene.updateMatrixWorld(true);

    const toLocal = new THREE.Matrix4().copy(vrm.scene.matrixWorld).invert();
    const P = (n) => (raw(n) ? raw(n).getWorldPosition(new THREE.Vector3()).applyMatrix4(toLocal) : null);
    const rest = new Map(bones.map((n) => [n, P(n)]));
    const toe = rest.get('leftToes'), ankle = rest.get('leftFoot');
    const frontZ = toe && ankle && Math.abs(toe.z - ankle.z) > 1e-3 ? Math.sign(toe.z - ankle.z) : (vrm.meta?.metaVersion === '0' ? -1 : 1);

    // --- какие суставы куда отъедут (в координатах модели) ---
    const moves = [];
    for (const [key, list, comp] of STRETCH) {
      const k = b[key];
      if (isOne(k)) continue;
      for (const n of list) {
        if (!raw(n)) continue;
        const seg = humanParent(n);
        if (!seg) continue;
        const off = rest.get(n).clone().sub(rest.get(seg));
        if (off.lengthSq() < 1e-10) continue;
        const delta = comp === 'x' ? new THREE.Vector3(off.x * (k - 1), 0, 0) : off.clone().multiplyScalar(k - 1);
        moves.push({ seg, child: n, a: rest.get(seg), off, len2: off.lengthSq(), delta });
      }
    }
    const segMoves = new Map();
    for (const m of moves) { if (!segMoves.has(m.seg)) segMoves.set(m.seg, []); segMoves.get(m.seg).push(m); }

    const thick = new Map();
    for (const [n, [key, towards]] of Object.entries(THICK)) {
      if (!raw(n)) continue;
      const k = b[key], kBack = n === 'hips' ? b.butt : 1;
      if (isOne(k) && isOne(kBack)) continue;
      const to = towards.find((t) => raw(t));
      if (!to) continue;
      const a = rest.get(n);
      thick.set(n, { a, dir: rest.get(to).clone().sub(a).normalize(), k, kBack, pelvis: n === 'hips' });
    }

    // --- сетка: толщины и растяжка между суставами (в позе покоя, до сдвига костей) ---
    const done = new Set();
    if (thick.size || segMoves.size) {
      const S = new THREE.Matrix4(), L = new THREE.Matrix3();
      const v = new THREE.Vector3(), d = new THREE.Vector3(), r = new THREE.Vector3();
      for (const mesh of skinned) {
        const geo = mesh.geometry, pos = geo.attributes.position, si = geo.attributes.skinIndex, sw = geo.attributes.skinWeight;
        if (!pos || !si || !sw || done.has(pos)) continue;
        const hum = mesh.skeleton.bones.map(humanOf);
        const tE = hum.map((h) => (h ? thick.get(h) ?? null : null));
        const mE = hum.map((h) => (h ? segMoves.get(h) ?? null : null));
        if (!tE.some(Boolean) && !mE.some(Boolean)) continue;
        done.add(pos);
        const M = restTransform(mesh);
        let changed = false;
        for (let i = 0; i < pos.count; i++) {
          let hit = false;
          for (let c = 0; c < 4 && !hit; c++) {
            if (sw.getComponent(i, c) <= 0) continue;
            const j = si.getComponent(i, c);
            hit = !!(tE[j] || mE[j]);
          }
          if (!hit) continue;
          S.multiplyMatrices(toLocal, M(i));
          v.fromBufferAttribute(pos, i).applyMatrix4(S);
          d.set(0, 0, 0);
          for (let c = 0; c < 4; c++) {
            const w = sw.getComponent(i, c);
            if (w <= 0) continue;
            const j = si.getComponent(i, c);
            const e = tE[j];
            if (e) {
              r.copy(v).sub(e.a);
              r.addScaledVector(e.dir, -r.dot(e.dir));
              if (e.pelvis) {
                d.x += w * r.x * (e.k - 1);
                if (r.z * frontZ < 0) d.z += w * r.z * (e.kBack - 1);
              } else d.addScaledVector(r, w * (e.k - 1));
            }
            const ms = mE[j];
            if (ms) for (const m of ms) {
              const t = THREE.MathUtils.clamp(r.copy(v).sub(m.a).dot(m.off) / m.len2, 0, 1);
              d.addScaledVector(m.delta, w * t);
            }
          }
          if (d.lengthSq() === 0) continue;
          L.setFromMatrix4(S).invert();
          d.applyMatrix3(L);
          pos.setXYZ(i, pos.getX(i) + d.x, pos.getY(i) + d.y, pos.getZ(i) + d.z);
          changed = true;
        }
        if (changed) {
          pos.needsUpdate = true;
          touched.add(pos);
          const isFace = Boolean(
            mesh.morphTargetInfluences?.length > 0 ||
            mesh.geometry?.morphAttributes?.position?.length > 0 ||
            /face|fcl|eye|mouth|lip|teeth|tongue|brow/i.test(mesh.name || '')
          );
          if (!isFace) {
            mesh.geometry.computeVertexNormals();
            if (mesh.geometry.attributes.normal) mesh.geometry.attributes.normal.needsUpdate = true;
          }
        }
      }
    }

    // --- посадка вещей: сдвиг по нормали (свободнее / в облипку) ---
    const fitDone = new Set();
    for (const mesh of meshes) {
      const f = out.fit?.[mesh.name];
      const pos = mesh.geometry.attributes.position, nrm = mesh.geometry.attributes.normal;
      if (!f || !pos || !nrm || fitDone.has(pos)) continue;
      fitDone.add(pos);
      for (let i = 0; i < pos.count; i++) pos.setXYZ(i, pos.getX(i) + nrm.getX(i) * f, pos.getY(i) + nrm.getY(i) * f, pos.getZ(i) + nrm.getZ(i) * f);
      pos.needsUpdate = true;
      touched.add(pos);
      const isFace = Boolean(
        mesh.morphTargetInfluences?.length > 0 ||
        mesh.geometry?.morphAttributes?.position?.length > 0 ||
        /face|fcl|eye|mouth|lip|teeth|tongue|brow/i.test(mesh.name || '')
      );
      if (!isFace) {
        mesh.geometry.computeVertexNormals();
        if (mesh.geometry.attributes.normal) mesh.geometry.attributes.normal.needsUpdate = true;
      }
    }

    // --- кости: суставы отъезжают, голова/кисти/стопы растут ---
    const ankle0 = rest.get('leftFoot')?.y ?? 0;
    for (const m of moves) {
      const node = raw(m.child);
      const Lp = new THREE.Matrix3().setFromMatrix4(new THREE.Matrix4().multiplyMatrices(toLocal, node.parent.matrixWorld)).invert();
      node.position.add(m.delta.clone().applyMatrix3(Lp));
    }
    for (const [key, list] of GROW) {
      if (isOne(b[key])) continue;
      for (const n of list) raw(n)?.scale.multiplyScalar(b[key]);
    }
    vrm.scene.updateMatrixWorld(true);
    const ankle1 = P('leftFoot')?.y ?? ankle0;
    // подошва ≈ щиколотка минус её высота над полом, выросшая вместе со стопой
    const lift = b.feet * ankle0 - ankle1;
    vrm.scene.scale.copy(orig.scene.scale).multiplyScalar(b.height);
    vrm.scene.position.copy(orig.scene.pos);
    vrm.scene.position.y += lift * b.height * orig.scene.scale.y;
    vrm.scene.updateMatrixWorld(true);

    // --- нормализованный скелет three-vrm — под новые длины ---
    if (moves.length || GROW.some(([k]) => !isOne(b[k]))) {
      const toLocal2 = new THREE.Matrix4().copy(vrm.scene.matrixWorld).invert();
      const Q = (n) => raw(n).getWorldPosition(new THREE.Vector3()).applyMatrix4(toLocal2);
      const rp = H.normalizedRestPose;
      for (const n of bones) {
        const node = norm(n);
        const parent = nameOfNorm.get(node.parent);
        const p = Q(n);
        if (parent) p.sub(Q(parent));
        node.position.copy(p);
        if (rp[n]) rp[n].position = p.toArray();
      }
      vrm.scene.updateMatrixWorld(true);
    }
    vrm.springBoneManager?.setInitState();

    // --- цвета: перекраска вещей (множитель поверх текстуры, у тени тоже). Кожу (body.skin) красит bodypaint.js —
    // по пикселям кожи: множитель на весь материал красил заодно и нарисованные на коже купальник с перчатками ---
    for (const m of orig.colors.keys()) {
      const name = baseName(m.name);
      const tint = out.tint?.[name], gain = out.gain?.[name];
      if (!tint && !gain) continue;
      const t = new THREE.Color(1, 1, 1);
      if (tint) t.multiply(new THREE.Color(...tint));
      if (gain) t.multiplyScalar(gain);
      m.color.multiply(t);
      m.shadeColorFactor?.multiply(t);
    }
    return { height: b.height, head: b.head };
  }

  return { apply, restore };
}
