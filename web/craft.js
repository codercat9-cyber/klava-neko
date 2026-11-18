// Мастерская Клавы: 3D-вещи, которые она делает себе сама (друг 01.10: «инструменты по моделькам гардероба дал, а 3D
// модельки она себе сама делать не может»). Гардероб (wardrobe.js) — это готовые наряды и их перекраска; здесь —
// новые предметы с нуля: наушники, очки, бантик, рожки, крылья, меч в лапу, ваза на пол. Вещь описывается данными
// (инструмент craft хозяина): к чему крепится, где стоит и из каких частей собрана — простые формы (шар, коробка,
// цилиндр, конус, тор, капсула, кольцо, тело вращения по профилю, трубка по точкам, выдавленный контур) с цветом,
// материалом и зеркальной парой. Либо готовая модель .glb (её Blender или чужая).
//
// Система координат вещи — ЕЁ, а не мира: x — к её левой руке, y — вверх, z — вперёд (куда смотрит), метры. Начало —
// точка крепления (голова — центр головы, кисть — ладонь…). Вещь сидит на кости и ходит вместе с ней.
// Рамки креплений снимаются один раз в позе покоя (при загрузке), поэтому вещь можно надеть в любой момент — посреди
// жеста тоже. Снимок вещи с трёх сторон и её самой в полный рост уходит ей в глаза: сделала — посмотрела — поправила.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';

/** Крепления: имя → кость VRM (и чем это место является). */
export const ANCHORS = {
  head: ['head', 'центр головы'],
  neck: ['neck', 'шея'],
  chest: ['upperChest', 'грудь'],
  waist: ['spine', 'талия'],
  hips: ['hips', 'таз'],
  shoulderL: ['leftUpperArm', 'левое плечо'], shoulderR: ['rightUpperArm', 'правое плечо'],
  elbowL: ['leftLowerArm', 'левый локоть'], elbowR: ['rightLowerArm', 'правый локоть'],
  handL: ['leftHand', 'левая ладонь'], handR: ['rightHand', 'правая ладонь'],
  thighL: ['leftUpperLeg', 'левое бедро'], thighR: ['rightUpperLeg', 'правое бедро'],
  kneeL: ['leftLowerLeg', 'левое колено'], kneeR: ['rightLowerLeg', 'правое колено'],
  footL: ['leftFoot', 'левая щиколотка'], footR: ['rightFoot', 'правая щиколотка'],
  room: [null, 'пол комнаты у её ног'],
};

const MAX_PARTS = 80, MAX_POINTS = 96;
const num = (v, d) => (Number.isFinite(Number(v)) ? Number(v) : d);
const lim = (v, a, b, d) => Math.min(b, Math.max(a, num(v, d)));
const vec = (a, d = [0, 0, 0]) => (Array.isArray(a) ? [num(a[0], d[0]), num(a[1], d[1]), num(a[2], d[2])] : d);
const rad = (deg) => THREE.MathUtils.degToRad(deg);

export function createCraft({ vrm, scene, renderer, gradientMap, send }) {
  scene.updateMatrixWorld(true);
  const raw = (n) => vrm.humanoid.getRawBoneNode(n);
  const wp = (o) => o.getWorldPosition(new THREE.Vector3());
  // её оси в покое: лево — от правого плеча к левому, верх — мировой, вперёд — лево × верх
  const up = new THREE.Vector3(0, 1, 0);
  const left = wp(raw('leftUpperArm')).sub(wp(raw('rightUpperArm'))).setY(0).normalize();
  const fwd = new THREE.Vector3().crossVectors(left, up).normalize();
  const basis = new THREE.Matrix4().makeBasis(left, up, fwd);
  const feet = wp(raw('leftFoot')).add(wp(raw('rightFoot'))).multiplyScalar(0.5);
  const floorY = Math.min(wp(raw('leftToes') ?? raw('leftFoot')).y, feet.y) - 0.02;
  const ground = new THREE.Vector3(feet.x, Math.max(0, floorY - 0.06), feet.z);

  // голова: кость стоит у основания черепа, а вещи удобнее считать от центра головы — он по сетке лица
  const headBone = raw('head');
  let headBox = null, hairBox = null;
  vrm.scene.traverse((o) => {
    if (!o.isMesh || !o.visible) return;
    const face = /face/i.test(o.name), hair = /hair/i.test(o.name);
    if (!face && !hair) return;
    const b = new THREE.Box3().setFromObject(o, true);
    if (!Number.isFinite(b.min.y)) return;
    if (face) headBox = headBox ? headBox.union(b) : b;
    else hairBox = hairBox ? hairBox.union(b) : b;
  });
  const headBase = wp(headBone);
  const s = vrm.scene.scale.x || 1;
  const headCenter = headBox ? new THREE.Vector3(headBase.x, (headBox.min.y + headBox.max.y) / 2, headBase.z) : headBase.clone().addScaledVector(up, 0.09 * s);
  const headHalf = headBox ? (headBox.max.y - headBox.min.y) / 2 : 0.1 * s;
  const headSize = headBox ? headBox.getSize(new THREE.Vector3()) : null;
  const headHalfWidth = headSize ? (Math.abs(left.x) * headSize.x + Math.abs(left.z) * headSize.z) / 2 : 0.075 * s;

  /** Рамки креплений: имя → { bone, inv (обратная мировая матрица кости в покое), origin (мир) }. */
  const frames = {};
  for (const [name, [boneName]] of Object.entries(ANCHORS)) {
    if (!boneName) { frames[name] = { bone: null, origin: ground.clone() }; continue; }
    const bone = raw(boneName) ?? (boneName === 'upperChest' ? raw('chest') : null);
    if (!bone) continue;
    let origin = wp(bone);
    if (name === 'head') origin = headCenter.clone();
    // ладонь — между запястьем и основанием среднего пальца
    if (name === 'handL' || name === 'handR') {
      const mid = raw(name === 'handL' ? 'leftMiddleProximal' : 'rightMiddleProximal');
      if (mid) origin = origin.clone().lerp(wp(mid), 0.75);
    }
    frames[name] = { bone, inv: bone.matrixWorld.clone().invert(), origin };
  }

  // ---------- тело в покое: чтобы пришивать вещи к коже (skin) и отдавать фигуру в Blender ----------
  //
  // Жёсткая вещь сидит на одной кости. Одежда из деталей или из Blender (штаны, худи, тапки) должна гнуться вместе с
  // телом: каждая её вершина берёт веса костей у ближайших точек кожи. Снимок тела — сейчас, в позе покоя (страница
  // зовёт createCraft до первого жеста): положения вершин и матрицы костей.
  const skinParts = [];
  vrm.scene.traverse((o) => { if (o.isSkinnedMesh && /body|face|hair/i.test(o.name) && !/^Klava/.test(o.name)) skinParts.push(o); });
  const bodyMesh = skinParts.find((o) => o.name === 'Bodybaked') ?? skinParts.find((o) => /body/i.test(o.name)) ?? null;
  const snapNow = (mesh) => {
    const sk = mesh.skeleton;
    return {
      boneM: sk.bones.map((b, i) => new THREE.Matrix4().multiplyMatrices(b.matrixWorld, sk.boneInverses[i])),
      pre: mesh.bindMatrix.clone(), post: new THREE.Matrix4().multiplyMatrices(mesh.matrixWorld, mesh.bindMatrixInverse),
    };
  };
  // матрицы снимаются сейчас (покой), а не в момент выгрузки — тогда она может быть посреди жеста
  const snaps = new Map(skinParts.map((m) => [m, snapNow(m)]));
  const snap = (mesh) => snaps.get(mesh) ?? snapNow(mesh);
  /** Матрица «из сетки в мир в покое» для набора весов: post · Σ wᵢ·(кость·обратная) · pre. */
  const restMatrix = (sn, bones, weights) => {
    const acc = new THREE.Matrix4().set(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    for (let c = 0; c < bones.length; c++) {
      if (!weights[c]) continue;
      const e = sn.boneM[bones[c]].elements;
      for (let k = 0; k < 16; k++) acc.elements[k] += e[k] * weights[c];
    }
    return new THREE.Matrix4().multiplyMatrices(sn.post, acc).multiply(sn.pre);
  };
  const restOf = (mesh, sn) => {
    const geo = mesh.geometry, P = geo.attributes.position, si = geo.attributes.skinIndex, sw = geo.attributes.skinWeight;
    const out = new Float32Array(P.count * 3), v = new THREE.Vector3();
    for (let i = 0; i < P.count; i++) {
      const m = restMatrix(sn, [si.getX(i), si.getY(i), si.getZ(i), si.getW(i)], [sw.getX(i), sw.getY(i), sw.getZ(i), sw.getW(i)]);
      v.fromBufferAttribute(P, i).applyMatrix4(m);
      out[i * 3] = v.x; out[i * 3 + 1] = v.y; out[i * 3 + 2] = v.z;
    }
    return out;
  };
  let skinBody = null;   // { sn, rest, grid, cell }
  if (bodyMesh) {
    try {
      const sn = snap(bodyMesh);
      const rest = restOf(bodyMesh, sn);
      const cell = 0.03, grid = new Map();
      const keyOf = (x, y, z) => `${Math.floor(x / cell)},${Math.floor(y / cell)},${Math.floor(z / cell)}`;
      for (let i = 0; i < rest.length / 3; i++) {
        const k = keyOf(rest[i * 3], rest[i * 3 + 1], rest[i * 3 + 2]);
        (grid.get(k) ?? grid.set(k, []).get(k)).push(i);
      }
      skinBody = { sn, rest, grid, cell };
    } catch (e) { send({ type: 'craft_error', name: '', message: `снимок тела для пришивания не вышел: ${e?.message ?? e}` }); }
  }

  /** Веса костей для точки мира (покой): 6 ближайших вершин кожи, ближние весомее; 4 сильнейшие кости. */
  function weightsAt(p) {
    const { rest, grid, cell } = skinBody;
    const cx = Math.floor(p.x / cell), cy = Math.floor(p.y / cell), cz = Math.floor(p.z / cell);
    const best = [];
    for (let r = 0; r < 12 && (best.length < 6 || best[best.length - 1].d > (r - 1) * cell); r++) {
      for (let x = cx - r; x <= cx + r; x++) for (let y = cy - r; y <= cy + r; y++) for (let z = cz - r; z <= cz + r; z++) {
        if (Math.max(Math.abs(x - cx), Math.abs(y - cy), Math.abs(z - cz)) !== r) continue;
        for (const i of grid.get(`${x},${y},${z}`) ?? []) {
          const d = Math.hypot(rest[i * 3] - p.x, rest[i * 3 + 1] - p.y, rest[i * 3 + 2] - p.z);
          if (best.length < 6 || d < best[best.length - 1].d) { best.push({ i, d }); best.sort((a, b) => a.d - b.d); if (best.length > 6) best.pop(); }
        }
      }
    }
    if (!best.length) {
      // далеко от кожи (больше 36 см) — ближайшая вершина перебором
      let bi = 0, bd = Infinity;
      for (let i = 0; i < rest.length / 3; i++) {
        const d = Math.hypot(rest[i * 3] - p.x, rest[i * 3 + 1] - p.y, rest[i * 3 + 2] - p.z);
        if (d < bd) { bd = d; bi = i; }
      }
      best.push({ i: bi, d: bd });
    }
    const si = bodyMesh.geometry.attributes.skinIndex, sw = bodyMesh.geometry.attributes.skinWeight;
    const acc = new Map();
    for (const { i, d } of best) {
      const k = 1 / (d + 0.01);
      for (let c = 0; c < 4; c++) {
        const w = sw.getComponent(i, c);
        if (w > 0) { const b = si.getComponent(i, c); acc.set(b, (acc.get(b) ?? 0) + w * k); }
      }
    }
    const top = [...acc.entries()].sort((a, b) => b[1] - a[1]).slice(0, 4);
    const sum = top.reduce((s, [, w]) => s + w, 0) || 1;
    const bones = [0, 0, 0, 0], weights = [0, 0, 0, 0];
    top.forEach(([b, w], c) => { bones[c] = b; weights[c] = w / sum; });
    return { bones, weights, near: best[0]?.d ?? 1 };
  }

  /**
   * Пришить вещь к коже: её сетки (уже на месте, в рамке крепления) → SkinnedMesh на скелете тела. world — рамка вещи
   * в мире в покое (как при сборке). Возвращает новые сетки (лежат рядом с телом) и самое дальнее расстояние до кожи.
   */
  function skinify(it, world) {
    if (!skinBody) throw new Error('тело для пришивания не нашлось — вещь осталась на кости');
    it.holder.updateMatrixWorld(true);
    const holderInv = it.holder.matrixWorld.clone().invert();
    const meshes = [];
    it.group.traverse((o) => { if (o.isMesh && !o.isSkinnedMesh) meshes.push(o); });
    const made = [];
    let far = 0;
    const v = new THREE.Vector3(), nrm = new THREE.Vector3(), nm = new THREE.Matrix3(), nm2 = new THREE.Matrix3();
    for (const m of meshes) {
      const R = new THREE.Matrix4().multiplyMatrices(world, new THREE.Matrix4().multiplyMatrices(holderInv, m.matrixWorld));
      nm.getNormalMatrix(R);
      const g = m.geometry.clone();
      const P = g.attributes.position, N = g.attributes.normal;
      const idx = new Uint16Array(P.count * 4), wts = new Float32Array(P.count * 4);
      for (let i = 0; i < P.count; i++) {
        v.fromBufferAttribute(P, i).applyMatrix4(R);
        const { bones, weights, near } = weightsAt(v);
        far = Math.max(far, near);
        const inv = restMatrix(skinBody.sn, bones, weights).invert();
        const local = v.clone().applyMatrix4(inv);
        P.setXYZ(i, local.x, local.y, local.z);
        if (N) { nrm.fromBufferAttribute(N, i).applyMatrix3(nm).applyMatrix3(nm2.getNormalMatrix(inv)).normalize(); N.setXYZ(i, nrm.x, nrm.y, nrm.z); }
        idx.set(bones, i * 4); wts.set(weights, i * 4);
      }
      g.setAttribute('skinIndex', new THREE.Uint16BufferAttribute(idx, 4));
      g.setAttribute('skinWeight', new THREE.Float32BufferAttribute(wts, 4));
      g.computeBoundingSphere();
      const sm = new THREE.SkinnedMesh(g, m.material);
      sm.name = `craft-skin:${it.name}`;
      sm.position.copy(bodyMesh.position); sm.quaternion.copy(bodyMesh.quaternion); sm.scale.copy(bodyMesh.scale);
      bodyMesh.parent.add(sm);
      sm.bind(bodyMesh.skeleton, bodyMesh.bindMatrix);
      sm.frustumCulled = false;
      sm.visible = it.holder.visible;
      made.push(sm);
      m.visible = false;
    }
    it.skinned.push(...made);
    return { count: made.length, far };
  }

  /** Её тело в покое .glb — для Blender: кроить одежду прямо по фигуре (её оси: x — влево, y — вверх, z — вперёд, от пола). */
  function bodyGlb() {
    const root = new THREE.Scene();
    for (const mesh of skinParts) {
      const sn = mesh === bodyMesh && skinBody ? skinBody.sn : snap(mesh);
      const rest = mesh === bodyMesh && skinBody ? skinBody.rest : restOf(mesh, sn);
      const pos = new Float32Array(rest.length);
      for (let i = 0; i < rest.length / 3; i++) {
        const d = new THREE.Vector3(rest[i * 3], rest[i * 3 + 1], rest[i * 3 + 2]).sub(ground);
        pos[i * 3] = d.dot(left); pos[i * 3 + 1] = d.dot(up); pos[i * 3 + 2] = d.dot(fwd);
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
      if (mesh.geometry.index) g.setIndex(mesh.geometry.index.clone());
      g.computeVertexNormals();
      const out = new THREE.Mesh(g, new THREE.MeshStandardMaterial({ color: /hair/i.test(mesh.name) ? 0x3a7bd5 : 0xf0c8b8 }));
      out.name = mesh.name.replace(/[^\w-]/g, '_');
      root.add(out);
    }
    return new Promise((resolve) => {
      new GLTFExporter().parse(root, (buf) => {
        const bytes = new Uint8Array(buf);
        let bin = '';
        for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
        resolve(btoa(bin));
      }, () => resolve(null), { binary: true });
    });
  }

  /** Точка мира в её координатах от пола: [влево, вверх, вперёд], см. */
  const her = (p) => {
    const d = p.clone().sub(ground);
    return [Math.round(d.dot(left) * 100), Math.round(d.dot(up) * 100), Math.round(d.dot(fwd) * 100)];
  };

  /** Мерки тела — чтобы она знала, куда ставить вещи (сантиметры, её координаты от пола между ступнями). */
  function body() {
    const out = { units: 'см; x — к её левой руке, y — вверх от пола, z — вперёд', height: her(headCenter.clone().addScaledVector(up, headHalf))[1], anchors: {} };
    for (const [name, f] of Object.entries(frames)) out.anchors[name] = { at: her(f.origin), what: ANCHORS[name][1] };
    out.head = {
      halfHeight: Math.round(headHalf * 100), halfWidth: Math.round(headHalfWidth * 100),
      note: 'от центра головы: макушка ≈ +halfHeight по y (ушки и причёска выше ещё на 8–12), уши по бокам ≈ ±halfWidth по x, лицо — вперёд по z, затылок — назад',
    };
    if (hairBox) {
      // причёска пышнее головы: вещь на голову (наушники, ободок, корона) должна её огибать, иначе утонет в волосах
      const corners = [hairBox.min, hairBox.max].map((p) => p.clone().sub(headCenter));
      const along = (axis) => corners.map((c) => c.dot(axis));
      const xs = along(left), zs = along(fwd);
      out.hair = {
        halfWidth: Math.round(Math.max(Math.abs(xs[0]), Math.abs(xs[1])) * 100), top: Math.round((hairBox.max.y - headCenter.y) * 100),
        bottom: Math.round((hairBox.min.y - headCenter.y) * 100), front: Math.round(Math.max(zs[0], zs[1]) * 100), back: Math.round(Math.min(zs[0], zs[1]) * 100),
        note: 'причёска от центра головы, см: в стороны ±halfWidth, вверх top, вперёд front, назад back — вещь на голову делай чуть шире и выше причёски',
      };
    }
    out.rest = 'в покое руки разведены в стороны (T-поза): левая ладонь далеко по +x, правая — по −x, ладони вниз';
    return out;
  }

  // ---------- материалы и формы ----------

  function material(p) {
    const color = new THREE.Color(p.color ?? '#cccccc');
    const opacity = lim(p.opacity, 0.05, 1, 1);
    const common = { color, transparent: opacity < 1, opacity, side: THREE.DoubleSide };
    switch (String(p.material ?? 'toon')) {
      case 'metal': return new THREE.MeshPhongMaterial({ ...common, shininess: 140, specular: 0xffffff });
      case 'glass': return new THREE.MeshPhongMaterial({ ...common, transparent: true, opacity: Math.min(opacity, 0.4), shininess: 200, specular: 0xffffff, depthWrite: false });
      case 'glow': return new THREE.MeshBasicMaterial(common);
      default: {
        const m = new THREE.MeshToonMaterial({ ...common, gradientMap });
        if (p.glow) { m.emissive = new THREE.Color(p.glow); m.emissiveIntensity = 0.9; }
        return m;
      }
    }
  }

  function points2(a, fallback) {
    const pts = (Array.isArray(a) ? a : fallback).slice(0, MAX_POINTS).filter(Array.isArray).map((q) => new THREE.Vector2(num(q[0], 0), num(q[1], 0)));
    return pts.length >= 2 ? pts : fallback.map((q) => new THREE.Vector2(q[0], q[1]));
  }

  // пришитая к коже вещь гнётся по вершинам: коробке и цилиндру нужна мелкая сетка (шаг ~2 см), иначе гнуться нечему
  const segs = (len, fine) => (fine ? Math.min(60, Math.max(1, Math.ceil(len / 0.02))) : 1);
  function geometry(p, fine = false) {
    const r = lim(p.r, 0.001, 3, 0.03), h = lim(p.h, 0.001, 4, 0.06);
    switch (String(p.shape ?? 'sphere')) {
      case 'box': { const sz = (Array.isArray(p.size) ? vec(p.size, [0.05, 0.05, 0.05]) : [num(p.size, 0.05), num(p.size, 0.05), num(p.size, 0.05)]).map((v) => lim(v, 0.001, 4, 0.05)); return new THREE.BoxGeometry(...sz, ...sz.map((v) => segs(v, fine))); }
      case 'cylinder': return new THREE.CylinderGeometry(lim(p.rTop, 0, 3, r), lim(p.rBottom, 0, 3, r), h, 32, segs(h, fine), !!p.open);
      case 'cone': return new THREE.ConeGeometry(r, h, 32, segs(h, fine), !!p.open);
      case 'torus': return new THREE.TorusGeometry(r, lim(p.tube, 0.0005, 1, r * 0.15), 14, 48, rad(lim(p.arc, 5, 360, 360)));
      case 'capsule': return new THREE.CapsuleGeometry(r, h, 8, 20);
      case 'ring': return lim(p.rIn, 0, 3, 0) <= 0 ? new THREE.CircleGeometry(r, 40) : new THREE.RingGeometry(lim(p.rIn, 0, 3, 0), r, 48);
      case 'lathe': return new THREE.LatheGeometry(points2(p.profile, [[0.001, 0], [0.03, 0.02], [0.02, 0.06], [0.001, 0.08]]), 36);
      case 'tube': {
        const path = (Array.isArray(p.path) ? p.path : [[0, 0, 0], [0, 0.1, 0]]).slice(0, MAX_POINTS).map((q) => new THREE.Vector3(...vec(q)));
        if (path.length < 2) path.push(new THREE.Vector3(0, 0.1, 0));
        return new THREE.TubeGeometry(new THREE.CatmullRomCurve3(path, !!p.closed), Math.max(12, path.length * 8), lim(p.r, 0.0005, 1, 0.005), 10, !!p.closed);
      }
      case 'extrude': {
        const shape = new THREE.Shape(points2(p.outline, [[0, 0], [0.05, 0], [0.025, 0.05]]));
        const depth = lim(p.depth, 0.0005, 2, 0.01), bevel = lim(p.bevel, 0, 0.2, 0);
        const g = new THREE.ExtrudeGeometry(shape, { depth, bevelEnabled: bevel > 0, bevelSize: bevel, bevelThickness: bevel, bevelSegments: 3, curveSegments: 16 });
        g.translate(0, 0, -depth / 2);
        return g;
      }
      default: return new THREE.SphereGeometry(r, 28, 18);
    }
  }

  function place(o, p) {
    o.position.set(...vec(p.pos));
    const r = vec(p.rot);
    o.rotation.set(rad(r[0]), rad(r[1]), rad(r[2]));
    const sc = Array.isArray(p.scale) ? vec(p.scale, [1, 1, 1]) : [num(p.scale, 1), num(p.scale, 1), num(p.scale, 1)];
    o.scale.set(...sc.map((v) => lim(v, 0.01, 50, 1)));
  }

  function part(p, fine = false) {
    const m = new THREE.Mesh(geometry(p, fine), material(p));
    place(m, p);
    const list = [m];
    // зеркальная пара через её плоскость симметрии (x = 0 вещи): второе ухо наушников, второе крыло, второй рог
    if (p.mirror === 'x' || p.mirror === true) {
      const twin = new THREE.Mesh(m.geometry, m.material);
      twin.position.set(-m.position.x, m.position.y, m.position.z);
      twin.rotation.set(m.rotation.x, -m.rotation.y, -m.rotation.z);
      twin.scale.copy(m.scale);
      list.push(twin);
    }
    return list;
  }

  const loader = new GLTFLoader();
  const items = new Map();   // имя → { spec, holder, group }

  /** Собрать вещь по описанию. Модель .glb догружается сама и встаёт на место, когда прочитается. */
  function build(name, spec) {
    const anchor = frames[spec.attach] ? spec.attach : 'head';
    const f = frames[anchor];
    const holder = new THREE.Group();
    holder.name = `craft:${name}`;
    // рамка вещи в мире в покое: её оси, начало — точка крепления; дальше — сдвиг, поворот и размер самой вещи
    const world = new THREE.Matrix4().copy(basis).setPosition(f.origin);
    if (f.bone) { holder.applyMatrix4(new THREE.Matrix4().multiplyMatrices(f.inv, world)); f.bone.add(holder); }
    else { holder.applyMatrix4(world); scene.add(holder); }
    const group = new THREE.Group();
    place(group, { pos: spec.at, rot: spec.rot, scale: spec.scale });
    holder.add(group);
    const it = { name, spec, holder, group, skinned: [], skinInfo: null };
    for (const p of (Array.isArray(spec.parts) ? spec.parts : []).slice(0, MAX_PARTS))
      if (p && typeof p === 'object') group.add(...part(p, !!spec.skin));
    holder.visible = spec.worn !== false;
    // пришитая к коже вещь (skin) гнётся вместе с телом: штаны, худи, тапки из деталей или из Blender
    const sew = () => {
      try { it.skinInfo = skinify(it, world); }
      catch (e) { it.skinInfo = { error: String(e?.message ?? e) }; send({ type: 'craft_error', name, message: it.skinInfo.error }); }
    };
    if (spec.glb) {
      it.loading = new Promise((done) => loader.load(`./${spec.glb}?v=${Date.now()}`, (gltf) => {
        const model = gltf.scene;
        const wrap = new THREE.Group();
        // одежда из Blender по её фигуре (skin) стоит как скроена — без центровки и подгонки размера
        if (!spec.skin || spec.size) {
          const box = new THREE.Box3().setFromObject(model);
          const size = box.getSize(new THREE.Vector3()), center = box.getCenter(new THREE.Vector3());
          const big = Math.max(size.x, size.y, size.z) || 1;
          const k = spec.size ? lim(spec.size, 0.005, 5, 0.2) / big : 1;
          if (!spec.skin) model.position.sub(center);
          wrap.scale.setScalar(k);
        }
        wrap.add(model);
        group.add(wrap);
        group.traverse((m) => { m.frustumCulled = false; });
        if (spec.skin) sew();
        send({ type: 'craft_loaded', name });
        done();
      }, undefined, (e) => { send({ type: 'craft_error', name, message: `модель ${spec.glb} не прочиталась: ${e?.message ?? e}` }); done(); }));
    } else if (spec.skin) sew();
    holder.traverse((m) => { m.frustumCulled = false; });
    return it;
  }

  function drop(name) {
    const it = items.get(name);
    if (!it) return;
    it.holder.parent?.remove(it.holder);
    it.holder.traverse((m) => { if (m.isMesh) { m.geometry?.dispose(); m.material?.dispose?.(); } });
    for (const sm of it.skinned) { sm.parent?.remove(sm); sm.geometry?.dispose(); }
    items.delete(name);
  }

  function set(name, spec) {
    drop(name);
    items.set(name, build(name, spec ?? {}));
  }

  function wear(name, on) {
    const it = items.get(name);
    if (it) { it.holder.visible = !!on; it.spec.worn = !!on; for (const sm of it.skinned) sm.visible = !!on; }
    return !!it;
  }

  /** Всё из config.json → craft.items (при загрузке страницы). */
  function applyAll(craft) {
    for (const [name, spec] of Object.entries(craft?.items ?? {})) {
      try { set(name, spec); } catch (e) { send({ type: 'craft_error', name, message: String(e?.message ?? e) }); }
    }
  }

  // ---------- снимок: она в полный рост и вещь крупно с трёх сторон ----------

  function shot(name) {
    const it = items.get(name);
    if (!it) return null;
    scene.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(it.holder);
    for (const sm of it.skinned) box.expandByObject(sm, true);   // пришитая вещь — по её коже в нынешней позе
    if (!Number.isFinite(box.min.x) || box.isEmpty()) return null;
    const c = box.getCenter(new THREE.Vector3());
    const radius = Math.max(0.06, box.getSize(new THREE.Vector3()).length() / 2);
    const W = 384, H = 384;
    const rt = new THREE.WebGLRenderTarget(W, H, { samples: 4 });
    rt.texture.colorSpace = THREE.SRGBColorSpace;
    const cam = new THREE.PerspectiveCamera(26, 1, 0.02, 40);
    const f = fwd.clone(), l = left.clone();
    const dist = radius * 1.75 / Math.tan(rad(13));
    const hipsNow = wp(raw('hips'));
    const tall = (headCenter.y + headHalf + 0.14) - ground.y;
    const views = [
      () => { const mid = new THREE.Vector3(hipsNow.x, ground.y + tall / 2, hipsNow.z); cam.position.copy(mid).addScaledVector(f, (tall / 2) * 1.1 / Math.tan(rad(13))); cam.lookAt(mid); },
      () => { cam.position.copy(c).addScaledVector(f, dist); cam.lookAt(c); },
      () => { cam.position.copy(c).addScaledVector(l, dist); cam.lookAt(c); },
      () => { cam.position.copy(c).addScaledVector(f, -dist * 0.8).addScaledVector(l, -dist * 0.5).addScaledVector(up, dist * 0.35); cam.lookAt(c); },
    ];
    const out = document.createElement('canvas');
    out.width = W * views.length; out.height = H;
    const g = out.getContext('2d');
    const px = new Uint8Array(W * H * 4);
    const prevAlpha = renderer.getClearAlpha();
    views.forEach((view, i) => {
      view();
      cam.updateMatrixWorld();
      renderer.setRenderTarget(rt);
      renderer.setClearColor(0xdde2e8, 1);
      renderer.clear();
      renderer.render(scene, cam);
      renderer.readRenderTargetPixels(rt, 0, 0, W, H, px);
      const img = g.createImageData(W, H);
      for (let y = 0; y < H; y++) img.data.set(px.subarray((H - 1 - y) * W * 4, (H - y) * W * 4), y * W * 4);
      g.putImageData(img, i * W, 0);
    });
    renderer.setRenderTarget(null);
    renderer.setClearColor(0x000000, prevAlpha);
    rt.dispose();
    g.fillStyle = 'rgba(20,20,30,0.75)';
    g.font = '13px Segoe UI, sans-serif';
    ['вся ты, спереди', 'вещь спереди', 'вещь слева', 'вещь сзади-сверху'].forEach((t, i) => g.fillText(t, i * W + 8, 18));
    return out.toDataURL('image/jpeg', 0.86);
  }

  // ---------- выгрузка в .glb: вещь как обычная 3D-модель (Blender, VRoid, печать) ----------

  function exportGlb(name) {
    const it = items.get(name);
    if (!it) return Promise.resolve(null);
    const copy = it.group.clone(true);
    copy.traverse((m) => {
      if (!m.isMesh) return;
      const src = m.material;
      m.material = new THREE.MeshStandardMaterial({
        color: src.color?.clone() ?? new THREE.Color(0xcccccc), transparent: !!src.transparent, opacity: src.opacity ?? 1,
        metalness: src.isMeshPhongMaterial ? 0.85 : 0, roughness: src.isMeshPhongMaterial ? 0.3 : 0.75,
        emissive: src.isMeshBasicMaterial ? src.color.clone() : (src.emissive?.clone() ?? new THREE.Color(0)), side: THREE.DoubleSide,
      });
    });
    const root = new THREE.Scene();
    root.add(copy);
    return new Promise((resolve) => {
      new GLTFExporter().parse(root, (buf) => {
        const bytes = new Uint8Array(buf);
        let bin = '';
        for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
        resolve(btoa(bin));
      }, () => resolve(null), { binary: true });
    });
  }

  /** Сообщение хозяина {type:'craft', op, id, name, spec}. */
  async function handle(m) {
    try {
      switch (m.op) {
        case 'set': set(String(m.name), m.spec); break;
        case 'remove': drop(String(m.name)); break;
        case 'wear': wear(String(m.name), m.on !== false); break;
        case 'body': send({ type: 'craft_reply', id: m.id, body: body() }); return;
        case 'export': send({ type: 'craft_reply', id: m.id, data: await exportGlb(String(m.name)) }); return;
        case 'bodymesh': send({ type: 'craft_reply', id: m.id, data: await bodyGlb() }); return;
        case 'shot': break;
        default: send({ type: 'craft_reply', id: m.id, error: `мастерская не знает действия ${m.op}` }); return;
      }
      if (m.id) {
        // вещь из .glb читается не сразу — снимок после загрузки (до 6 с)
        const it = items.get(String(m.name));
        if (m.op === 'set' && it?.loading) await Promise.race([it.loading, new Promise((r) => setTimeout(r, 6000))]);
        await new Promise((r) => requestAnimationFrame(() => r()));
        const skin = it?.skinInfo ? { count: it.skinInfo.count ?? 0, far: Math.round((it.skinInfo.far ?? 0) * 100), error: it.skinInfo.error ?? null } : null;
        send({ type: 'craft_reply', id: m.id, data: m.op === 'remove' ? null : shot(String(m.name)), skin });
      }
    } catch (e) {
      send({ type: 'craft_reply', id: m.id, error: String(e?.message ?? e) });
    }
  }

  return { applyAll, set, drop, wear, shot, exportGlb, bodyGlb, body, handle, names: () => [...items.keys()], item: (n) => items.get(n) };
}
