import * as THREE from 'three';

/**
 * Челюсть. Друг 29.09: «рот при разговоре проваливается в челюсть — вблизи жутко». Причина — у VRoid-лица нет челюсти:
 * морфы «aa/oh» опускают нижнюю губу на ~1 см и уводят нутро рта на 1–2 см внутрь головы, а подбородок стоит на
 * месте, и губа уезжает в неподвижный подбородок. Губы ещё и складывали aa+oh+ih (до 1,6 в сумме).
 *
 * Здесь к сеткам рта (те, что двигает морф «aa»: кожа лица и нутро рта) добавляется свой морф «jaw» — поворот нижней
 * челюсти вокруг височного сустава: всё ниже линии рта (нижняя губа, подбородок, низ нутра рта) опускается дугой.
 * Шов лица с шеей не трогаем: вес у вершин края сетки гаснет до нуля — иначе на шее открылась бы щель.
 * Всё в координатах сетки (у VRM 0.x сцена повёрнута на 180°, поэтому «вперёд» считаем от центра лица ко рту).
 */
const smooth = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

export function createJaw(vrm, { angle = 0.2 } = {}) {
  const ex = vrm.expressionManager?.getExpression('aa');
  const binds = ex?._binds ?? [];
  const meshes = [];
  for (const b of binds) for (const m of b.primitives ?? []) {
    const d = m.geometry?.morphAttributes?.position?.[b.index];
    if (!d) continue;
    let n = 0;
    for (let i = 0; i < d.count && n < 20; i++) if (Math.hypot(d.getX(i), d.getY(i), d.getZ(i)) > 0.0005) n++;
    if (n >= 20) meshes.push({ mesh: m, aa: d, skin: [].concat(m.material).some((x) => /SKIN/i.test(x?.name ?? '')) });
  }
  const skin = meshes.find((x) => x.skin);
  if (!skin) return { set() {}, value: 0, ok: false };

  // рот: центр вершин кожи, которые двигает «aa»; вперёд — от центра всей кожи лица ко рту
  const P = skin.mesh.geometry.attributes.position;
  const mouth = new THREE.Vector3(), all = new THREE.Vector3(), v = new THREE.Vector3();
  let n = 0;
  for (let i = 0; i < P.count; i++) {
    all.add(v.fromBufferAttribute(P, i));
    if (Math.hypot(skin.aa.getX(i), skin.aa.getY(i), skin.aa.getZ(i)) > 0.002) { mouth.add(v); n++; }
  }
  mouth.divideScalar(Math.max(1, n));
  all.divideScalar(P.count);
  const fwd = Math.sign(mouth.z - all.z) || -1;
  // сустав: чуть выше линии рта и на 6,5 см вглубь головы
  const hinge = new THREE.Vector3(mouth.x, mouth.y + 0.01, mouth.z - fwd * 0.065);
  const a = angle * fwd;                 // знак поворота: то, что впереди сустава, идёт вниз
  const ca = Math.cos(a), sa = Math.sin(a);

  for (const { mesh, skin: isSkin } of meshes) {
    const g = mesh.geometry;
    const pos = g.attributes.position;
    // край сетки кожи — это и шов с шеей, и дырка рта; беречь надо только шов (он ниже рта или позади сустава)
    const edgeDist = isSkin ? distanceToEdge(g, (x, y, z) => y < mouth.y - 0.012 || (z - hinge.z) * fwd < 0.015) : null;
    const delta = new Float32Array(pos.count * 3);
    for (let i = 0; i < pos.count; i++) {
      const x = pos.getX(i), y = pos.getY(i), z = pos.getZ(i);
      const depth = (z - hinge.z) * fwd;               // насколько вершина впереди сустава
      let w = smooth(mouth.y + 0.002, mouth.y - 0.007, y) * smooth(0.0, 0.03, depth);
      if (edgeDist) w *= smooth(0.004, 0.016, edgeDist[i]);
      if (w <= 0) continue;
      const ry = y - hinge.y, rz = z - hinge.z;
      delta[i * 3 + 1] = w * (ry * ca - rz * sa - ry);
      delta[i * 3 + 2] = w * (ry * sa + rz * ca - rz);
    }
    const attr = new THREE.BufferAttribute(delta, 3);
    attr.name = 'jaw';
    g.morphAttributes.position.push(attr);
    if (g.morphAttributes.normal) g.morphAttributes.normal.push(new THREE.BufferAttribute(new Float32Array(pos.count * 3), 3));
    mesh.morphTargetInfluences.push(0);
    mesh.morphTargetDictionary.jaw = mesh.morphTargetInfluences.length - 1;
  }
  const idx = meshes.map((m) => [m.mesh, m.mesh.morphTargetDictionary.jaw]);

  const api = {
    ok: true,
    value: 0,
    /** Раскрыть челюсть на долю 0..1 (после vrm.update — three-vrm этот индекс не трогает). */
    set(x) {
      api.value = Math.min(1, Math.max(0, x));
      for (const [m, i] of idx) m.morphTargetInfluences[i] = api.value;
    },
    info: { meshes: meshes.map((m) => m.mesh.name), mouth: mouth.toArray().map((t) => +t.toFixed(4)), fwd },
  };
  return api;
}

/** Для каждой вершины — расстояние до ближайшей вершины края сетки (ребро одного треугольника), край — те, что прошли keep. */
function distanceToEdge(g, keep) {
  const pos = g.attributes.position;
  const ix = g.index?.array;
  const out = new Float32Array(pos.count).fill(1);
  if (!ix) return out;
  const edges = new Map();
  const key = (p, q) => (p < q ? p * 1048576 + q : q * 1048576 + p);
  for (let t = 0; t < ix.length; t += 3) {
    for (const [p, q] of [[ix[t], ix[t + 1]], [ix[t + 1], ix[t + 2]], [ix[t + 2], ix[t]]]) {
      const k = key(p, q);
      edges.set(k, (edges.get(k) ?? 0) + 1);
    }
  }
  const rim = [];
  for (const [k, c] of edges) if (c === 1) rim.push(Math.floor(k / 1048576), k % 1048576);
  // вершины с одинаковым положением (швы UV) считаются одной точкой: край — только настоящий край поверхности
  const rimPts = [...new Set(rim)].map((i) => [pos.getX(i), pos.getY(i), pos.getZ(i)]);
  const seams = new Set();
  const byPos = new Map();
  for (let i = 0; i < pos.count; i++) {
    const k = `${pos.getX(i).toFixed(5)},${pos.getY(i).toFixed(5)},${pos.getZ(i).toFixed(5)}`;
    if (byPos.has(k)) { seams.add(k); }
    byPos.set(k, i);
  }
  const real = rimPts.filter(([x, y, z]) => keep(x, y, z) && !seams.has(`${x.toFixed(5)},${y.toFixed(5)},${z.toFixed(5)}`));
  for (let i = 0; i < pos.count; i++) {
    const x = pos.getX(i), y = pos.getY(i), z = pos.getZ(i);
    let best = 1;
    for (const [rx, ry, rz] of real) {
      const d = (x - rx) ** 2 + (y - ry) ** 2 + (z - rz) ** 2;
      if (d < best) best = d;
    }
    out[i] = Math.sqrt(best);
  }
  return out;
}
