// Синее кимоно-халатик для VRM-модели, собирается кодом при загрузке.
//
// Верх — копия сетки тела (та же кожа, те же кости и веса), раздутая по нормалям: гнётся вместе
// со скелетом, как настоящая одежда. Рукава к запястью расширяются колоколом. Низ — юбка-«конус»
// по обхвату бёдер, прикреплена к кости таза. Рисунок (сакура, воротник, запах, оби) считает шейдер
// по координатам тела в покое, поэтому узор не плывёт, когда она двигается.
//
// Запах как положено у живых: левая пола поверх правой — со стороны зрителя буква «y».
import * as THREE from 'three';
import { tailRoot } from './tail.js';

const toonGradient = (() => {
  const t = new THREE.DataTexture(new Uint8Array([110, 185, 255]), 3, 1, THREE.RedFormat);
  t.minFilter = t.magFilter = THREE.NearestFilter;
  t.needsUpdate = true;
  return t;
})();

// Цвета — в линейном пространстве, на экране (sRGB) выходят заметно светлее: 0.07, 0.16, 0.62 ≈ #4a70cf.
const col = (a, fallback) => new THREE.Vector3(...(a ?? fallback));

// Координаты вершины кожаной сетки в позе покоя (мир) — та же формула, что у SkinnedMesh в шейдере.
// Её же берёт figure.js, чтобы править толщины тела прямо в сетке.
export function restTransform(mesh) {
  const geo = mesh.geometry, sk = mesh.skeleton;
  const si = geo.attributes.skinIndex, sw = geo.attributes.skinWeight;
  const boneM = sk.bones.map((b, i) => new THREE.Matrix4().multiplyMatrices(b.matrixWorld, sk.boneInverses[i]));
  const pre = mesh.bindMatrix, post = new THREE.Matrix4().multiplyMatrices(mesh.matrixWorld, mesh.bindMatrixInverse);
  const m = new THREE.Matrix4(), acc = new THREE.Matrix4();
  return (i) => {
    acc.set(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    for (let c = 0; c < 4; c++) {
      const w = sw.getComponent(i, c);
      if (w === 0) continue;
      const e = boneM[si.getComponent(i, c)].elements;
      for (let k = 0; k < 16; k++) acc.elements[k] += e[k] * w;
    }
    return m.multiplyMatrices(post, acc).multiply(pre);
  };
}

function kimonoMaterial(u) {
  const mat = new THREE.MeshToonMaterial({ color: 0xffffff, gradientMap: toonGradient, side: THREE.DoubleSide, transparent: true, alphaToCoverage: true, flatShading: false });
  mat.onBeforeCompile = (sh) => {
    Object.assign(sh.uniforms, u);
    sh.vertexShader = sh.vertexShader
      .replace('#include <common>', '#include <common>\nattribute vec3 restPos;\nattribute vec3 restNrm;\nvarying vec3 vRest;\nvarying vec3 vRN;')
      .replace('#include <begin_vertex>', '#include <begin_vertex>\nvRest = restPos;\nvRN = restNrm;');
    sh.fragmentShader = sh.fragmentShader
      .replace('#include <common>', `#include <common>
varying vec3 vRest;
varying vec3 vRN;
uniform float uNeckY, uCrossX, uCrossY, uSlope, uInY, uInSlope, uObiY, uObiH, uHemY, uCuffX, uBand, uSlitY, uSlitW, uTailHoleX, uTailHoleY;
uniform float uWaveK, uSakuraK, uTailHole;
uniform float uStyle, uOpen, uGap, uJubanOn, uObiOn;
uniform vec3 uTop, uHem, uWave, uCollar, uGold, uEri, uJuban, uObi, uCord, uPetal, uPetalDeep, uZip;
float kHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

// Сэйгайха («волны синего моря»): ряды вееров, каждый нижний ряд перекрывает верхний.
// Возвращает расстояние до центра видимого веера (0..1) — по нему рисуются дуги.
float kSeigaiha(vec2 uv) {
  float jy = floor(uv.y / 0.5);
  for (int dj = -2; dj <= 2; dj++) {
    float j = jy + float(dj);
    float off = mod(j, 2.0);
    float cx = floor((uv.x - off) / 2.0 + 0.5) * 2.0 + off;
    for (int di = -1; di <= 1; di++) {
      float d = length(uv - vec2(cx + float(di) * 2.0, j * 0.5));
      if (d < 1.0) return d;
    }
  }
  return 1.0;
}
float kWaves(vec2 p) {
  float d = kSeigaiha(p / 0.034);
  float r = fract(d * 3.5);
  return max(1.0 - smoothstep(0.06, 0.18, min(r, 1.0 - r)), smoothstep(0.9, 0.97, d));
}

// Сакура: пять лепестков с вырезом-сердечком на кончике. x — лепесток, y — 0 у центра .. 1 у края, z — тычинки.
vec3 kSakura(vec2 uv, float cell, float density, float seed) {
  vec2 g = floor(uv / cell), f = fract(uv / cell) - 0.5;
  if (kHash(g + seed) > density) return vec3(0.0);
  vec2 q = f - (vec2(kHash(g + 3.1 + seed), kHash(g + 7.7 + seed)) - 0.5) * 0.25;
  float size = 0.24 + 0.12 * kHash(g + 1.7 + seed);
  float a = atan(q.y, q.x) + kHash(g + 5.3 + seed) * 6.2831;
  float r = length(q) / size;
  float sec = 6.2831853 / 5.0;
  float phi = mod(a + sec * 0.5, sec) - sec * 0.5;
  float R = pow(max(cos(phi * 2.5), 0.0), 0.4) - 0.24 * exp(-pow(phi / 0.09, 2.0));
  float petal = 1.0 - smoothstep(R - 0.06, R + 0.01, r);
  return vec3(petal, clamp(r / max(R, 0.05), 0.0, 1.0), 1.0 - smoothstep(0.1, 0.16, r));
}
vec3 kSakuraAll(vec2 uv, float dens) {
  vec3 big = kSakura(uv, 0.11, dens, 0.0);
  vec3 small = kSakura(uv + 0.031, 0.062, dens * 0.8, 17.0);
  return big.x > 0.02 ? big : small;
}`)
      .replace('#include <color_fragment>', `#include <color_fragment>
{
  vec3 p = vRest;
  vec3 n = normalize(vRN);
  bool front = n.z > 0.15;
  bool nearNeck = abs(p.x) < 0.14;
  float obiTop = uObiY + uObiH, obiBot = uObiY - uObiH;

  // --- ткань верхнего кимоно: переход от синего к индиго, волны, сакура ---
  vec3 w = pow(abs(n), vec3(4.0));
  w /= (w.x + w.y + w.z);
  float toHem = 1.0 - smoothstep(uHemY, uCrossY + 0.2, p.y);   // у smoothstep край0 < край1, иначе поведение не определено
  vec3 c = mix(uTop, uHem, toHem);
  float wave = kWaves(p.xy) * w.z + kWaves(p.zy + 5.0) * w.x + kWaves(p.xz + 9.0) * w.y;
  c = mix(c, min(c * 1.8 + uWave * 0.08, vec3(1.0)), wave * mix(0.35, 0.7, toHem) * uWaveK);   // волны тон в тон, светлее основы
  float dens = (mix(0.22, 0.7, toHem) + 0.3 * smoothstep(uNeckY - 0.15, uNeckY, p.y)) * uSakuraK;   // гуще у подола и на плечах
  vec3 s = kSakuraAll(p.xy, dens) * w.z + kSakuraAll(p.zy + 3.0, dens) * w.x + kSakuraAll(p.xz + 6.0, dens) * w.y;
  vec3 petal = mix(uPetalDeep, uPetal, smoothstep(0.1, 0.75, s.y));
  petal = mix(petal, uPetalDeep * 0.85, smoothstep(0.86, 0.98, s.y));   // тонкий контур лепестка
  c = mix(c, petal, s.x);
  c = mix(c, uGold, s.z * s.x);

  // --- застёжка: запах (uStyle 0) или молния по центру (1); расстёгнуто сверху вниз на uOpen ---
  bool zip = uStyle > 0.5;
  float cx = zip ? 0.0 : uCrossX;
  float dxc = abs(p.x - cx);
  // до какой высоты расстёгнуто; с поясом оби — не ниже пояса (пояс держит полы)
  float yOpen = uNeckY - uOpen * (uNeckY - uHemY);
  if (uObiOn > 0.5) yOpen = max(yOpen, obiTop);
  float gap = -1.0;   // полуширина распаха на этой высоте (<0 — застёгнуто)
  if (uOpen > 0.001 && p.y > yOpen) gap = uGap * pow(clamp((p.y - yOpen) / max(uNeckY - yOpen, 0.01), 0.0, 1.0), 0.7);

  // Субпиксельное сглаживание срезов (anti-aliasing via screen-space derivatives)
  float cutA = 1.0;
  if (front && gap > 0.0) {
    float dCut = dxc - gap;
    float fw = max(fwidth(dCut), 0.0006);
    cutA = min(cutA, smoothstep(-fw * 0.75, fw * 0.75, dCut));
  }

  float band = -1.0;
  float sO = uSlope, kO = inversesqrt(1.0 + sO * sO);
  if (zip) {
    // круглый ворот вместо запаха
    float q = clamp(dxc / 0.085, 0.0, 1.0);
    float neckCut = uNeckY - 0.015 - 0.035 * (1.0 - q * q);
    if (front && dxc < 0.085) {
      float dNeck = neckCut - p.y;
      float fw = max(fwidth(dNeck), 0.0006);
      cutA = min(cutA, smoothstep(-fw * 0.75, fw * 0.75, dNeck));
    }
    if (front && dxc < 0.085 && p.y > neckCut - uBand) band = neckCut - p.y;
    if (nearNeck && !front) {
      float dBack = (uNeckY - 0.035) - p.y;
      float fw = max(fwidth(dBack), 0.0006);
      cutA = min(cutA, smoothstep(-fw * 0.75, fw * 0.75, dBack));
      if (p.y > uNeckY - 0.035 - uBand) band = uNeckY - 0.035 - p.y;
    }
  } else {
    // --- ворот: глубокий запах до оби, под ним нижняя рубаха с воротничком ---
    float dA = (sO * (p.x - uCrossX) - (p.y - uCrossY)) * kO;    // край левой (верхней) полы, >0 — на ней
    float dB = (-sO * (p.x - uCrossX) - (p.y - uCrossY)) * kO;   // край правой полы
    if (front && p.y > uCrossY && dA < 0.0 && dB < 0.0) {
      if (uJubanOn < 0.5) {
        float dGate = max(dA, dB);
        float fw = max(fwidth(dGate), 0.0006);
        cutA = min(cutA, smoothstep(-fw * 0.75, fw * 0.75, dGate));
      }
      float sI = uInSlope, kI = inversesqrt(1.0 + sI * sI);
      float dIn = (uInY + sI * abs(p.x - uCrossX) - p.y) * kI;    // >0 — ткань рубахи, <0 — кожа
      float fwIn = max(fwidth(dIn), 0.0006);
      cutA = min(cutA, smoothstep(-fwIn * 0.75, fwIn * 0.75, dIn));
      c = dIn < 0.016 ? uEri : uJuban * (0.9 + 0.1 * smoothstep(0.0, 0.08, dIn));
    } else {
      if (front && dA >= 0.0 && dA < uBand && p.y > obiBot) band = dA;
      if (front && dB >= 0.0 && dB < uBand && dA < 0.0 && p.y > uCrossY) band = dB;
      if (nearNeck && !front) {
        float dBack = (uNeckY - 0.035) - p.y;
        float fw = max(fwidth(dBack), 0.0006);
        cutA = min(cutA, smoothstep(-fw * 0.75, fw * 0.75, dBack));
        if (p.y > uNeckY - 0.035 - uBand) band = uNeckY - 0.035 - p.y;
      }
    }
  }
  // край распаха — кант (у молнии поверх — зубцы)
  if (front && gap > 0.0 && dxc - gap < uBand * 0.6) band = dxc - gap;
  // полы ниже оби: край полы отвесный, от колен расходятся «платьем» — внутри видна рубаха (или тело, если её нет)
  float x0 = zip ? 0.0 : uCrossX + (obiBot - uCrossY) / sO;
  if (front && p.y < obiBot) {
    float slitHalf = uSlitW * clamp((uSlitY - p.y) / (uSlitY - uHemY), 0.0, 1.0);
    float e = abs(p.x - x0) - slitHalf;
    if (e < 0.0 && slitHalf > 0.0) {
      if (uJubanOn < 0.5) {
        float fwE = max(fwidth(e), 0.0006);
        cutA = min(cutA, smoothstep(-fwE * 0.75, fwE * 0.75, e));
      }
      c = uJuban * (0.88 + 0.12 * smoothstep(0.0, 0.05, -e)); band = -1.0;
    }
    else if (e < uBand * 0.8) band = e;
  }
  // кант ворота и распаха со сглаживанием
  if (band >= 0.0) {
    float fwB = max(fwidth(band), 0.0006);
    float goldMix = 1.0 - smoothstep(0.0045 - fwB, 0.0045 + fwB, band);
    c = mix(uCollar, uGold, goldMix);
  }

  // молния: застёгнутая часть — дорожка зубцов по центру, бегунок там, где кончается распах, по краям распаха — зубцы
  if (zip && front) {
    float teeth = 0.72 + 0.28 * step(0.5, fract(p.y / 0.0065 + step(0.0, p.x - cx) * 0.5));
    if (p.y <= yOpen && p.y > uHemY + 0.03 && dxc < 0.0055) c = uZip * teeth;
    if (gap > 0.0 && dxc >= gap && dxc < gap + 0.0055) c = uZip * teeth;
    if (uOpen > 0.001 && yOpen > uHemY + 0.03 && abs(p.y - (yOpen - 0.012)) < 0.014 && dxc < 0.012) c = min(uZip * 1.3, vec3(1.0));
  }

  // оби: светлый, с золотыми кромками и розовым шнурком посередине (можно снять)
  float dy = abs(p.y - uObiY);
  if (uObiOn > 0.5 && dy < uObiH) {
    c = uObi * (0.92 + 0.08 * kWaves(p.xy * 0.6 + 2.0));
    if (dy > uObiH - 0.006) c = uGold;
    if (dy < 0.007) c = uCord;
  }
  // кант по подолу и по краю рукавов с субпиксельным сглаживанием
  if (p.y < uHemY + 0.028) {
    float fwY = max(fwidth(p.y), 0.0006);
    float goldY = smoothstep(uHemY + 0.022 - fwY, uHemY + 0.022 + fwY, p.y);
    c = mix(uCollar, uGold, goldY);
  }
  if (abs(p.x) > uCuffX) {
    float fwX = max(fwidth(abs(p.x)), 0.0006);
    float goldX = 1.0 - smoothstep(uCuffX + 0.006 - fwX, uCuffX + 0.006 + fwX, abs(p.x));
    c = mix(uCollar, uGold, goldX);
  }

  // прорезь под хвост
  if (uTailHole > 0.5 && !front && p.z < -0.01) {
    float dTail = 1.0 - length(vec2((p.x - uTailHoleX) / 0.042, (p.y - uTailHoleY - 0.008) / 0.048));
    float fwT = max(fwidth(dTail), 0.0006);
    cutA = min(cutA, smoothstep(-fwT * 0.75, fwT * 0.75, -dTail));
  }

  if (cutA <= 0.001) discard;
  diffuseColor.a *= cutA;
  diffuseColor.rgb *= c;
}`);
  };
  mat.customProgramCacheKey = () => 'klava-kimono-8';
  return mat;
}

/**
 * Надеть кимоно. body — кожаная сетка тела (у VRoid «Bodybaked»); opts — раздел kimono из config.json.
 * Возвращает созданные меши и uniforms шейдера (для подстройки из консоли).
 */
export function addKimono(vrm, body, opts = {}) {
  vrm.scene.updateMatrixWorld(true);
  const bone = (n) => vrm.humanoid.getRawBoneNode(n);
  const wp = (n) => bone(n).getWorldPosition(new THREE.Vector3());
  const neckY = wp('neck').y;
  const hipsY = wp('hips').y;
  const spineY = wp('spine').y;
  const chestY = (bone('upperChest') ?? bone('chest')).getWorldPosition(new THREE.Vector3()).y;
  const handX = Math.abs(wp('leftHand').x);
  const footY = wp('leftFoot').y;
  const elbowX = Math.abs(wp('leftLowerArm').x);

  const geo = body.geometry;
  const P = geo.attributes.position, N = geo.attributes.normal;
  const toWorld = restTransform(body);
  const rest = new Float32Array(P.count * 3), restN = new Float32Array(P.count * 3);
  const v = new THREE.Vector3(), nm = new THREE.Matrix3();
  for (let i = 0; i < P.count; i++) {
    const m = toWorld(i);
    v.fromBufferAttribute(P, i).applyMatrix4(m);
    rest.set([v.x, v.y, v.z], i * 3);
    v.fromBufferAttribute(N, i).applyMatrix3(nm.setFromMatrix4(m)).normalize();
    restN.set([v.x, v.y, v.z], i * 3);
  }
  const rx = (i) => rest[i * 3], ry = (i) => rest[i * 3 + 1];

  // Крой из редактора (раздел kimono): всё необязательное, по умолчанию — прежнее кимоно один в один.
  //   obiShift — оби выше/ниже, obiH — полуширина оби, neckline — запах выше (+) / глубже (−),
  //   innerShift — вырез нижней рубахи, sleeveEnd — насколько рукав не доходит до запястья,
  //   sleeveBell — раструб рукава, thickness — отступ ткани от тела, flare — колокол юбки (множитель),
  //   waves / sakura — сила волн и густота сакуры (0 — нет), tailHole — прорезь под хвост; застёжка — см. uniforms ниже.
  const obiY = opts.obiY ?? (spineY + 0.035 + (opts.obiShift ?? 0));
  const obiH = opts.obiH ?? 0.06;
  const hemY = footY + (opts.hemAboveFoot ?? 0.03);   // длинное, до щиколоток
  const sleeveEnd = opts.sleeveEnd ?? 0.05;
  const cuffX = handX - sleeveEnd - 0.025;
  const keepV = (i) => {
    const x = Math.abs(rx(i)), y = ry(i);
    if (y < hipsY - 0.04 || y > neckY + 0.06) return false;
    if (x < 0.1 && y > neckY - 0.005) return false;   // шея и голова — не ткань
    return x < handX - sleeveEnd;                      // рукав кончается у запястья
  };

  // --- верх: раздутая копия тела ---
  const src = geo.index.array;
  const idx = [];
  const group0 = geo.groups[0] ?? { start: 0, count: src.length };
  for (let t = group0.start; t < group0.start + group0.count; t += 3) {
    const a = src[t], b = src[t + 1], c = src[t + 2];
    if (keepV(a) && keepV(b) && keepV(c)) idx.push(a, b, c);
  }
  const pos = new Float32Array(P.count * 3);
  for (let i = 0; i < P.count; i++) {
    const x = Math.abs(rx(i)), y = ry(i);
    let off = opts.thickness ?? 0.011;
    if (opts.obiOn !== false && Math.abs(y - obiY) < obiH + 0.005) off += 0.012;   // оби толще ткани (сняли пояс — и утолщения нет)
    const tSleeve = THREE.MathUtils.clamp((x - elbowX) / (handX - elbowX), 0, 1);
    off += (opts.sleeveBell ?? 0.08) * Math.pow(tSleeve, 1.3);                 // рукав-колокол
    pos[i * 3] = P.getX(i) + N.getX(i) * off;
    pos[i * 3 + 1] = P.getY(i) + N.getY(i) * off;
    pos[i * 3 + 2] = P.getZ(i) + N.getZ(i) * off;
  }
  const topGeo = new THREE.BufferGeometry();
  topGeo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  topGeo.setAttribute('normal', N.clone());
  topGeo.setAttribute('skinIndex', geo.attributes.skinIndex);
  topGeo.setAttribute('skinWeight', geo.attributes.skinWeight);
  topGeo.setAttribute('restPos', new THREE.BufferAttribute(rest, 3));
  topGeo.setAttribute('restNrm', new THREE.BufferAttribute(restN, 3));
  topGeo.setIndex(idx);
  topGeo.computeVertexNormals();
  if (topGeo.attributes.normal) topGeo.attributes.normal.needsUpdate = true;

  // полураспахнутый ворот: полы сходятся у самого оби, у шеи расходятся на ~8 см от центра;
  // внутри — нижняя рубаха с вырезом чуть выше груди
  const crossY = opts.crossY ?? Math.min(obiY + obiH + 0.01 + (opts.neckline ?? 0), neckY - 0.04);
  const slope = (neckY - crossY) / 0.08;
  const inY = opts.innerY ?? Math.min(chestY + 0.03 + (opts.innerShift ?? 0), neckY - 0.01);
  const inSlope = (neckY - inY) / 0.055;
  // прорезь под хвост — над копчиком, где его и крепит tail.js (с той же поправкой tailOffset, если хвост сдвинут)
  const hole = opts.tailHole === false ? new THREE.Vector3(0, -10, 0)
    : tailRoot(vrm).add(new THREE.Vector3(opts.tailOffset?.[0] ?? 0, opts.tailOffset?.[1] ?? 0, 0));
  const uniforms = {
    uNeckY: { value: neckY }, uCrossX: { value: opts.crossX ?? 0.004 }, uCrossY: { value: crossY },
    uSlope: { value: slope }, uInY: { value: inY }, uInSlope: { value: inSlope },
    uObiY: { value: obiY }, uObiH: { value: obiH }, uHemY: { value: hemY },
    uCuffX: { value: cuffX }, uBand: { value: 0.03 },
    uTailHoleX: { value: hole.x }, uTailHoleY: { value: hole.y },
    // разрез спереди: начинается чуть выше колен, у подола расходится на uSlitW в каждую сторону
    uSlitY: { value: opts.slitY ?? (hipsY - 0.32) }, uSlitW: { value: opts.slitW ?? 0.11 },
    uWaveK: { value: opts.waves ?? 1 }, uSakuraK: { value: opts.sakura ?? 1 }, uTailHole: { value: opts.tailHole === false ? 0 : 1 },
    // застёжка: closure "wrap" (запах) | "zip" (молния по центру), open 0..1 — насколько расстёгнуто сверху вниз, gap — ширина
    // распаха у ворота, innerShirt — нижняя рубаха (в вороте и разрезе; без неё там видно тело), obiOn — пояс оби, zip — цвет молнии
    uStyle: { value: opts.closure === "zip" ? 1 : 0 }, uOpen: { value: opts.open ?? 0 }, uGap: { value: opts.gap ?? 0.09 },
    uJubanOn: { value: opts.innerShirt === false ? 0 : 1 }, uObiOn: { value: opts.obiOn === false ? 0 : 1 },
    uZip: { value: col(opts.zip, [0.82, 0.84, 0.9]) },
    uTop: { value: col(opts.top, [0.07, 0.16, 0.62]) },
    uHem: { value: col(opts.hem, [0.008, 0.012, 0.08]) },
    uWave: { value: col(opts.wave, [0.55, 0.72, 1.0]) },
    uCollar: { value: col(opts.collar, [0.01, 0.015, 0.09]) },
    uGold: { value: col(opts.gold, [1.0, 0.8, 0.35]) },
    uEri: { value: col(opts.eri, [0.98, 0.98, 1.0]) },
    uJuban: { value: col(opts.juban, [1.0, 0.86, 0.9]) },
    uObi: { value: col(opts.obi, [0.94, 0.94, 1.0]) },
    uCord: { value: col(opts.cord, [1.0, 0.45, 0.65]) },
    uPetal: { value: col(opts.petal, [1.0, 0.93, 0.96]) },
    uPetalDeep: { value: col(opts.petalDeep, [1.0, 0.38, 0.55]) },
  };
  const mat = kimonoMaterial(uniforms);
  const top = new THREE.SkinnedMesh(topGeo, mat);
  top.name = 'KlavaKimonoTop';
  top.position.copy(body.position);
  top.quaternion.copy(body.quaternion);
  top.scale.copy(body.scale);
  body.parent.add(top);
  top.bind(body.skeleton, body.bindMatrix);
  top.frustumCulled = false;

  // --- низ: юбка по обхвату бёдер, сверху заходит под оби ---
  const yTop = obiY + obiH * 0.4;
  const NJ = 36, NI = 64, PW = 2.4;   // Мягкий анатомичный овал без угловых граней и ступеней полигонов
  const slab = (y0, y1) => {
    let mx = 0, zmin = 1e9, zmax = -1e9;
    for (let i = 0; i < P.count; i++) {
      const y = ry(i), x = rx(i);
      if (y < y0 || y > y1 || Math.abs(x) > 0.3) continue;
      mx = Math.max(mx, Math.abs(x));
      zmin = Math.min(zmin, rest[i * 3 + 2]);
      zmax = Math.max(zmax, rest[i * 3 + 2]);
    }
    return mx > 0 ? { rx: mx, cz: (zmin + zmax) / 2, rz: (zmax - zmin) / 2 } : null;
  };
  const rings = [];
  let rX = 0, rZ = 0, cZ = 0;
  for (let j = 0; j <= NJ; j++) {
    const t = j / NJ, y = yTop + (hemY - yTop) * t;
    const e = slab(y - 0.02, y + 0.02);
    if (e) { rX = Math.max(rX, e.rx); rZ = Math.max(rZ, e.rz); cZ = j === 0 ? e.cz : cZ * 0.7 + e.cz * 0.3; }
    const flare = opts.flare ?? 1;
    rings.push({ y, rx: rX + 0.02 + 0.07 * flare * t * t, rz: rZ + 0.025 + 0.05 * flare * t * t, cz: cZ });
  }
  const sp = [], sn = [], sIdx = [];
  for (let j = 0; j <= NJ; j++) for (let i = 0; i <= NI; i++) {
    const a = (i / NI) * Math.PI * 2, ca = Math.cos(a), sa = Math.sin(a);
    const r = rings[j];
    const x = r.rx * Math.sign(ca) * Math.pow(Math.abs(ca), 2 / PW);
    const z = r.cz + r.rz * Math.sign(sa) * Math.pow(Math.abs(sa), 2 / PW);
    sp.push(x, r.y, z);
    sn.push(ca, 0, sa);
  }
  for (let j = 0; j < NJ; j++) for (let i = 0; i < NI; i++) {
    const a = j * (NI + 1) + i, b = a + 1, c = a + NI + 1, d = c + 1;
    sIdx.push(a, b, c, b, d, c);
  }
  const skirtGeo = new THREE.BufferGeometry();
  skirtGeo.setAttribute('position', new THREE.Float32BufferAttribute(sp, 3));
  skirtGeo.setIndex(sIdx);
  skirtGeo.computeVertexNormals();
  skirtGeo.setAttribute('restPos', new THREE.Float32BufferAttribute(sp.slice(), 3));
  skirtGeo.setAttribute('restNrm', new THREE.Float32BufferAttribute(sn, 3));   // наружу, по радиусу
  const skirt = new THREE.Mesh(skirtGeo, mat);
  skirt.name = 'KlavaKimonoSkirt';
  const hips = bone('hips');
  hips.add(skirt);
  hips.updateMatrixWorld(true);
  new THREE.Matrix4().copy(hips.matrixWorld).invert().decompose(skirt.position, skirt.quaternion, skirt.scale);
  skirt.frustumCulled = false;

  return { top, skirt, uniforms };
}
