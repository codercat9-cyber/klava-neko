// Клава: живой VRM-аватар в прозрачном окне.
// Хост (KlavaDesk.exe, WebView2) шлёт сюда: say / stop / state / emotion / gesture / paw / cursor / poke / zoom.
// Сюда хосту уходит: ready / mask (силуэт для сквозных кликов) / said / idle / poked / error.
// Без хоста (обычный браузер) управление мышью: курсор — взгляд, клик — тычок, колесо — зум.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from '@pixiv/three-vrm';
import { addKimono } from './kimono.js';
import { createGestures } from './gestures.js';
import { addTail } from './tail.js';
import { addClaws } from './claws.js';
import { addBeauty } from './beauty.js';
import { createAhegao } from './ahegao.js';
import { createTears } from './tears.js';
import { createJaw } from './jaw.js';
import { addFacePlus } from './faceplus.js';
import { createSnacks } from './snacks.js';
import { createFigure } from './figure.js';
import { createBodyPaint } from './bodypaint.js';
import { outfitConfig, addGarments, henshin, OUTFITS, hasOutfit } from './wardrobe.js?v=20261001';
import { createSongPlayer, applyGroove } from './music.js';
import { createProps } from './props.js';
import { createCraft } from './craft.js';

const host = window.chrome?.webview ?? null;
const send = (msg) => { if (host) host.postMessage(msg); };
window.addEventListener('error', (e) => send({ type: 'error', message: String(e.message) }));
window.addEventListener('unhandledrejection', (e) => send({ type: 'error', message: String(e.reason) }));

// config.json — облик из редактора; наряд (гардероб, wardrobe.js) ложится поверх на время загрузки: ?outfit=имя
const cfgText = await fetch('./config.json', { cache: 'no-store' }).then((r) => r.text());
const pageParams = new URLSearchParams(location.search);
const rawCfg = JSON.parse(cfgText);
// наряд — встроенный (OUTFITS) или свой (config.json → wardrobe.custom: его собирает она сама инструментом wardrobe)
const outfitName = hasOutfit(rawCfg, pageParams.get('outfit')) ? pageParams.get('outfit') : (hasOutfit(rawCfg, rawCfg.outfitName) ? rawCfg.outfitName : 'kimono');
const cfg = outfitConfig(rawCfg, outfitName);
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
const damp = (cur, target, rate, dt) => cur + (target - cur) * (1 - Math.exp(-rate * dt));

// ---------- сцена ----------
const renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true, premultipliedAlpha: true });
// Рисуем с запасом (supersample ×2) и сжимаем: аппаратное сглаживание берёт только края геометрии, а края, вырезанные
// шейдером (разрез и подол кимоно, ворот), и неоновый кант тоньше пикселя шли лесенкой и рвались пунктиром (друг 02.10:
// «в разрезе кимоно вижу, как идут полосы»). Окно маленькое — видеокарте это недорого.
renderer.setPixelRatio(Math.min(3, window.devicePixelRatio * (cfg.supersample ?? 2)));
// в журнал хозяина — чтобы видеть, что запас разрешения действительно включён (а не на глаз)
setTimeout(() => send({ type: 'error',
  message: `рендер: экран ×${window.devicePixelRatio}, рисую ×${renderer.getPixelRatio()} (${renderer.domElement.width}×${renderer.domElement.height})` }), 1500);
renderer.setSize(innerWidth, innerHeight);
renderer.setClearColor(0x000000, 0);
renderer.outputColorSpace = THREE.SRGBColorSpace;
document.body.prepend(renderer.domElement);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(cfg.cameraFov, innerWidth / innerHeight, 0.05, 30);
const light = new THREE.DirectionalLight(0xffffff, Math.PI);
light.position.set(0.5, 1.0, 1.4).normalize();
scene.add(light);
scene.add(new THREE.AmbientLight(0xffffff, 0.25 * Math.PI));

// ---------- модель ----------
const loader = new GLTFLoader();
loader.register((parser) => new VRMLoaderPlugin(parser));
const gltf = await loader.loadAsync(cfg.model);
const vrm = gltf.userData.vrm;
VRMUtils.removeUnnecessaryVertices(gltf.scene);
VRMUtils.combineSkeletons(gltf.scene);
VRMUtils.rotateVRM0(vrm);
// Нормали модели — родные из VRoid: они сглажены и совпадают по обе стороны швов развёртки. Пересчёт
// computeVertexNormals (был добавлен 02.10) давал на разрезанных по швам вершинах разные нормали — мультяшный свет
// рисовал по швам резкие полосы, и силуэт выглядел гранёным («полосы», «нехватка полигонов»).
// Вырезы по прозрачности (кончики прядей, ресницы, мех) — через alpha-to-coverage: край смягчается сглаживанием
// (MSAA), а не рубится порогом в лесенку.
vrm.scene.traverse((o) => {
  o.frustumCulled = false;
  if (o.isMesh) {
    const isFace = Boolean(
      o.morphTargetInfluences?.length > 0 ||
      o.geometry?.morphAttributes?.position?.length > 0 ||
      /face|fcl|eye|mouth|lip|teeth|tongue|brow/i.test(o.name || '') ||
      [].concat(o.material).some((m) => /face|fcl|eye|mouth|lip|teeth|tongue|brow/i.test(m?.name || ''))
    );
    for (const m of [].concat(o.material)) {
      if (!m) continue;
      m.flatShading = false;
      if ('shadingToonyFactor' in m && m.shadingToonyFactor > 0.8) {
        m.shadingToonyFactor = isFace ? 0.82 : 0.72;
      }
      if (m.alphaTest > 0 && !m.transparent) m.alphaToCoverage = true;
      m.needsUpdate = true;
    }
  }
});
scene.add(vrm.scene);

// Тело из редактора (рост, пропорции — figure.js): первым, всё остальное шьётся уже по нему. Живая Клава этого раньше
// не делала — правки «Тела» в редакторе видно было только в нём.
vrm.humanoid.resetNormalizedPose();
vrm.update(0);
vrm.scene.updateMatrixWorld(true);
createFigure(vrm).apply(cfg);

// Волосы Vita серебристые с чисто синими прядями. Серебро подкрашиваем в голубой (hairTint),
// а слишком густой синий смягчаем до hairAccent — ближе к неко с видео.
const tint = new THREE.Color(...cfg.hairTint);
const accent = new THREE.Color(...cfg.hairAccent);
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

// Родные кибер-ушки Vita — два куска сетки волос; прячем их, вместо них будут пушистые.
const hide = new Set(cfg.hideMeshes || []);
vrm.scene.traverse((o) => { if (hide.has(o.name)) o.visible = false; });

// Грудь: у VRoid-моделей свои кости J_Sec_*_Bust1 (на них же физика колыхания). Кость стоит в 5 см от
// центра, а сама грудь смещена ещё наружу — поэтому равномерное раздувание уводило её к подмышкам.
// Масштаб по осям кости: X — вширь (сужаем), Y — вверх, Z — вперёд (объём); сдвиг — чуть к центру и вверх.
// Оси кости у Vita совпадают с мировыми с точностью до знака (X = −X мира, Z = −Z мира).
const bust = cfg.bust ?? { scale: [1, 1, 1], shift: [0, 0, 0] };
for (const [n, side] of [['J_Sec_L_Bust1', 1], ['J_Sec_R_Bust1', -1]]) {
  const b = vrm.scene.getObjectByName(n);
  if (!b) continue;
  b.scale.set(...bust.scale);
  b.position.x += (bust.shift?.[0] ?? 0) * side;
  b.position.y += bust.shift?.[1] ?? 0;
  b.position.z += bust.shift?.[2] ?? 0;
}

// Синее кимоно поверх тела; родной кибер-костюм (верх с воротником) прячем.
let kimono = null;
if (cfg.kimono && cfg.kimono.enabled !== false) {
  cfg.kimono.tailOffset = cfg.tail?.offset;
  for (const n of cfg.kimono.hideMeshes ?? []) { const o = vrm.scene.getObjectByName(n); if (o) o.visible = false; }
  const body = vrm.scene.getObjectByName(cfg.kimono.bodyMesh ?? 'Bodybaked');
  if (body?.isSkinnedMesh) kimono = addKimono(vrm, body, cfg.kimono);
  else send({ type: 'error', message: 'кимоно: нет сетки тела ' + (cfg.kimono.bodyMesh ?? 'Bodybaked') });
}

const boneNames = ['hips', 'spine', 'chest', 'upperChest', 'neck', 'head',
  'leftShoulder', 'rightShoulder', 'leftUpperArm', 'rightUpperArm', 'leftLowerArm', 'rightLowerArm', 'leftHand', 'rightHand'];
const B = Object.fromEntries(boneNames.map((n) => [n, vrm.humanoid.getNormalizedBoneNode(n)]));
const em = vrm.expressionManager;
// У Vita удивление — пользовательское выражение «Surprised» с большой буквы: ищем оба написания.
const exprName = (n) => (em?.getExpression(n) ? n : em?.getExpression(n[0].toUpperCase() + n.slice(1)) ? n[0].toUpperCase() + n.slice(1) : null);
const hasExpr = (n) => !!exprName(n);

vrm.update(0);
const headRest = B.head.getWorldPosition(new THREE.Vector3());
// Рамка кадра. По умолчанию она стоит в полный рост (длинное кимоно не режется краем окна),
// колесом приближается до бюста. Кость head — у основания черепа, макушка с ушками на ~30 см выше.
const topY = headRest.y + 0.26;
const footY = (vrm.humanoid.getRawBoneNode('leftFoot')?.getWorldPosition(new THREE.Vector3()).y ?? 0.08) - 0.08;
function fullDistance() {
  // с этого расстояния она целиком влезает и по высоте, и по ширине (руки с рукавами ≈ ±0,55 м)
  const t = Math.tan(THREE.MathUtils.degToRad(camera.fov / 2));
  return Math.max(((topY - footY) / 2) * 1.01 / t, 0.55 / (t * camera.aspect));
}
let camFull = fullDistance();
let camDist = cfg.cameraStart === 'bust' ? cfg.cameraDistance : camFull;
function placeCamera() {
  // вблизи (cameraDistanceMin) — лицо чуть выше центра, в полный рост — середина тела
  const k = clamp((camDist - cfg.cameraDistanceMin) / Math.max(0.01, camFull - cfg.cameraDistanceMin), 0, 1);
  const targetY = headRest.y + 0.06 + ((topY + footY) / 2 - headRest.y - 0.06) * k;
  camera.position.set(0, targetY + 0.02, camDist);
  camera.lookAt(0, targetY, 0);
  camera.updateMatrixWorld();
}
placeCamera();

// ---------- пушистые кошачьи ушки ----------
// Ухо — острый треугольник выше, чем шире (круглые широкие уши читаются как мышиные),
// слегка выгнутый «совочком», открытый вперёд. Мех — shell fur: поверх кожи лежат слои-оболочки,
// каждый чуть дальше по нормали и с порогом прозрачности по шумовой текстуре. Каждая точка шума —
// один волосок: яркие точки проходят сквозь все слои и становятся длинными, тусклые — короткими.
// Снаружи мех цвета волос, внутри светлая кожа и белый пух, который торчит из глубины уха.
const toonGradient = (() => {
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
function strandTexture(seed, count, repeatU, repeatV, rMin = 2.4, rMax = 5.6) {
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
// Сечение — дуга, раскрытая вперёд (+Z); ширина сходится к острому кончику, спинка чуть отклонена назад.
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
      const x = w * Math.sin(th);
      const z = -d * Math.cos(th) - 0.012 * k * v * v;
      pos.push(x, H * v, z);
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
  // наружу (к спинке уха, −Z) должна смотреть лицевая сторона; если обход вышел обратным — разворачиваем
  const mid = Math.round(NV / 2) * (NU + 1) + NU / 2;
  if (geo.attributes.normal.getZ(mid) > 0) {
    const ix = geo.index.array;
    for (let t = 0; t < ix.length; t += 3) { const s = ix[t + 1]; ix[t + 1] = ix[t + 2]; ix[t + 2] = s; }
    geo.computeVertexNormals();
  }
  return geo;
}

// Слои меха: копии сетки, сдвинутые по нормали (dir = 1 наружу, −1 внутрь уха) на длину len(u, v)·h,
// плюс «расчёска» comb·h² — волоски клонятся к кончику, а не торчат ёжиком.
function furShells(base, { layers, len, dir, comb, color, tex, cut0, cut1, darkBase }) {
  const group = new THREE.Group();
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
    const shade = darkBase + (1 - darkBase) * Math.pow(h, 0.7);   // у корней мех темнее — даёт объём
    const mat = new THREE.MeshToonMaterial({
      color: new THREE.Color(...color).multiplyScalar(shade), gradientMap: toonGradient,
      alphaMap: tex, alphaTest: cut0 + (cut1 - cut0) * h, side: THREE.DoubleSide, alphaToCoverage: true,   // мех: кончики ворсинок мягкие, не лесенкой
    });
    const m = new THREE.Mesh(geo, mat);
    m.frustumCulled = false;
    m.renderOrder = 2;
    group.add(m);
  }
  return group;
}

function makeEar(side) {
  const e = cfg.ears ?? {};
  const k = e.size ?? 1;
  const outerRgb = e.outer ?? [0.45, 0.66, 1.0];
  const innerRgb = e.inner ?? [1.0, 0.86, 0.93];
  const tuftRgb = e.tuft ?? [1.0, 0.98, 1.0];
  const group = new THREE.Group();          // шарнир в основании уха

  const outerGeo = earGeometry(k);
  const outer = new THREE.Mesh(outerGeo, new THREE.MeshToonMaterial({
    color: 0xffffff, map: skinTexture(outerRgb, side > 0 ? 7 : 11), gradientMap: toonGradient, side: THREE.DoubleSide }));
  // внутренняя кожа заметно уже наружной: спереди её обрамляет синий мохнатый край, как у кошки
  const innerGeo = earGeometry(k * 0.84, 0.64, 0.75);
  const inner = new THREE.Mesh(innerGeo, new THREE.MeshToonMaterial({
    color: 0xffffff, map: skinTexture(innerRgb, side > 0 ? 3 : 5), gradientMap: toonGradient, side: THREE.BackSide }));
  inner.position.set(0, 0.004 * k, 0.003 * k);
  group.add(outer, inner);

  // наружный мех: по всей спинке, к кончику короче, по краям пышнее — силуэт получается пушистым
  const outerFur = furShells(outerGeo, {
    layers: 18, dir: 1, comb: [0, 0.9, 0.15],
    len: (u, v) => 0.008 * k * (1 - 0.5 * v) * (1 + 1.2 * Math.pow(Math.abs(u - 0.5) * 2, 3)),
    color: outerRgb, tex: strandTexture(side > 0 ? 101 : 202, 900, 3, 4, 5, 11), cut0: 0.03, cut1: 0.85, darkBase: 0.68,
  });
  // пух изнутри: густой белый у основания и в середине, к кончику и краям сходит на нет
  const innerFur = furShells(innerGeo, {
    layers: 22, dir: -1, comb: [0, 1.3, 0.6],
    len: (u, v) => 0.032 * k * Math.pow(Math.max(0, 1 - v * 1.2), 1.2) * (0.25 + 0.75 * Math.cos((u - 0.5) * Math.PI)),
    color: tuftRgb, tex: strandTexture(side > 0 ? 303 : 404, 240, 2, 2, 9, 20), cut0: 0.04, cut1: 0.8, darkBase: 0.72,
  });
  innerFur.position.copy(inner.position);
  // синий мех по переднему краю: растёт внутрь «совочка» только у кромки, обрамляет светлое нутро
  const rimFur = furShells(outerGeo, {
    layers: 16, dir: -1, comb: [0, 0.7, 0.4],
    len: (u, v) => 0.009 * k * Math.max(0, (Math.abs(u - 0.5) * 2 - 0.5) / 0.5) * (1 - 0.6 * v),
    color: outerRgb, tex: strandTexture(side > 0 ? 505 : 606, 900, 3, 4, 5, 11), cut0: 0.03, cut1: 0.85, darkBase: 0.8,
  });
  group.add(outerFur, innerFur, rimFur);
  for (const m of [outer, inner]) { m.frustumCulled = false; m.renderOrder = 2; }
  return group;
}

const headRaw = vrm.humanoid.getRawBoneNode('head');
const ears = [1, -1].map((side) => {
  const pivot = makeEar(side);
  // Ставим в мировых координатах (модель смотрит в +Z, её левая сторона — +X), потом переводим в кость головы.
  const [ox, oy, oz] = cfg.ears?.offset ?? [0.074, 0.175, -0.018];
  const [tx, ty, tz] = cfg.ears?.tilt ?? [-0.18, 0.3, -0.3];
  // смещение — в масштабе головы (рост из «Тела» в редакторе): ушки растут вместе с ней и не тонут (как в look.js)
  const hs = headRaw.getWorldScale(new THREE.Vector3()).x;
  const world = headRaw.getWorldPosition(new THREE.Vector3()).add(new THREE.Vector3(ox * side, oy, oz).multiplyScalar(hs));
  const holder = new THREE.Group();
  headRaw.add(holder);
  holder.position.copy(headRaw.worldToLocal(world.clone()));
  const headQ = headRaw.getWorldQuaternion(new THREE.Quaternion());
  const restQ = new THREE.Quaternion().setFromEuler(new THREE.Euler(tx, ty * side, tz * side, 'YXZ'));
  holder.quaternion.copy(headQ.invert().multiply(restQ));
  holder.add(pivot);
  return { side, pivot, twitch: 0, pitch: 0, roll: 0 };
});
let nextTwitch = 3;

// ---------- хвост ----------
const tail = cfg.tail?.enabled === false ? null : addTail(vrm, {
  gradientMap: toonGradient, strandTex: strandTexture(707, 1100, 6, 12, 5, 11),
  color: cfg.ears?.outer, tip: cfg.ears?.tuft, ...(cfg.tail ?? {}),
});

// ---------- коготки ----------
// Кошачьи коготки на всех десяти пальцах; по настроению втянуты/выпущены, «ня» — выпустить (claws.js)
const claws = cfg.claws?.enabled === false ? null : addClaws(vrm, { gradientMap: toonGradient, ...(cfg.claws ?? {}) });

// ---------- красота ----------
// Блик на волосах, глубокие глаза с искоркой, тени у глаз, губы, живой румянец, канзаши-сакура и чокер с колокольчиком
// (beauty.js; ушки и хвост — свои модули, их не трогает)
// Своя раскраска из редактора: картинки (textureOverrides) и перекраска нарисованного на коже купальника/перчаток/чулок
// (bodypaint.js). Лицо не трогаем — его рисует beauty/faceplus ниже.
applyTextureOverrides(cfg.textureOverrides ?? {});
{
  const faceSkins = new Set();
  vrm.scene.traverse((o) => { if (o.isMesh) for (const m of [].concat(o.material)) if (/Face/i.test(m?.name ?? '')) faceSkins.add(m.name); });
  for (const n of Object.keys(cfg.textureOverrides ?? {})) faceSkins.add(n);
  if (Array.isArray(cfg.body?.skin) || cfg.bodyPaint) createBodyPaint(vrm).apply(cfg.body?.skin ?? null, cfg.bodyPaint ?? null, faceSkins);
}
function applyTextureOverrides(over) {
  if (!Object.keys(over).length) return;
  const loader = new THREE.TextureLoader();
  vrm.scene.traverse((o) => {
    if (!o.isMesh) return;
    for (const m of [].concat(o.material)) {
      const path = m && (over[m.name] ?? over[m.name.replace(/\s*\(Outline\)\s*$/, '')]);
      if (!path) continue;
      const old = m.map;
      loader.load(`${path}?v=${Date.now()}`, (tex) => {
        tex.flipY = old?.flipY ?? false;
        tex.colorSpace = THREE.SRGBColorSpace;
        m.map = tex;
        if ('shadeMultiplyTexture' in m && m.shadeMultiplyTexture) m.shadeMultiplyTexture = tex;
        m.needsUpdate = true;
      });
    }
  });
}

// лицо: киберглаз, дорожки импланта, родинка, блик на губе, клычок, мягкий подбородок (faceplus.js) — до beauty,
// потому что рисунки кожи ложатся в её основу
const facePlus = cfg.face?.enabled === false ? null : addFacePlus(vrm, cfg.face ?? {}, { gradientMap: toonGradient });
const beauty = cfg.beauty?.enabled === false ? null : addBeauty(vrm, cfg.beauty ?? {}, { gradientMap: toonGradient, faceDecor: facePlus?.decor });
// ахегао (эмоция по просьбе друга): глаза под лоб, язык, румянец — ahegao.js
const ahegao = createAhegao(vrm, { gradientMap: toonGradient });
// слёзы ([плач]): капли катятся по щекам по настоящей поверхности лица, мокрый след сохнет — tears.js
const tears = createTears(vrm);
// челюсть: подбородок и нижняя губа опускаются дугой вместе с ртом, а не губа проваливается в неподвижный подбородок (jaw.js)
const jaw = createJaw(vrm, cfg.jaw ?? {});

// Позы ушей по команде хоста (меню «👂 Ушки»): поверх настроения на время hold, rest — вернуть как было.
// pitch + — назад, roll + — в стороны/вниз, yaw + — наружу (у каждого уха со своим знаком).
const EAR_POSES = {
  perked: { pitch: -0.38, roll: -0.12, yaw: 0 },
  flat: { pitch: 0.95, roll: 0.45, yaw: 0.1 },
  converge: { pitch: -0.12, roll: -0.18, yaw: -0.42 },
  spread: { pitch: 0.05, roll: 0.42, yaw: 0.38 },
  droop: { pitch: 0.25, roll: 0.85, yaw: 0.05 },
  alert: { pitch: -0.3, roll: -0.08, yaw: -0.16 },
};
const earPose = { name: null, until: 0, w: 0 };

function earsUpdate(dt) {
  const e = S.emotion;
  // перк вверх-вперёд: слушает, рада, удивлена; прижаты назад: злится; висят: грустит
  const p = S.pleased;
  const up = ((S.state === 'listening' ? 0.2 : 0) + e.happy * 0.18 + e.surprised * 0.3) * (1 - p);
  const back = e.angry * 0.75 + p * 0.2;
  // гладят — ушки расслабленно расходятся в стороны, как у довольной кошки
  const droop = e.sad * 0.55 + p * 0.45;
  if (earPose.name && S.t > earPose.until) earPose.name = null;
  earPose.w = damp(earPose.w, earPose.name ? 1 : 0, 7, dt);
  const pose = EAR_POSES[earPose.name] ?? earPose.last ?? { pitch: 0, roll: 0, yaw: 0 };
  if (earPose.name) earPose.last = pose;
  if (S.t > nextTwitch) {
    ears[Math.random() < 0.5 ? 0 : 1].twitch = 1;
    // настороже — дёргает чаще
    nextTwitch = S.t + (earPose.name === 'alert' ? 0.8 + Math.random() * 1.5 : 3 + Math.random() * 7);
  }
  for (const ear of ears) {
    ear.twitch = Math.max(0, ear.twitch - dt * 6);
    const flick = Math.sin((1 - ear.twitch) * Math.PI * 3) * 0.28 * ear.twitch;
    const w = earPose.w;
    ear.pitch = damp(ear.pitch, (-up + back) * (1 - w) + pose.pitch * w + Math.sin(S.t * 1.7) * 0.02, 8, dt);
    ear.roll = damp(ear.roll, (droop * 0.9 - up * 0.3) * (1 - w) + pose.roll * w, 8, dt);
    ear.yaw = damp(ear.yaw ?? 0, pose.yaw * w, 8, dt);
    ear.pivot.rotation.set(ear.pitch, (flick + ear.yaw) * ear.side, ear.roll * ear.side);
  }
}

function earsCommand(m) {
  if (m.twitch) {
    for (const ear of ears) if (m.twitch === 'both' || (m.twitch === 'L') === (ear.side > 0)) ear.twitch = 1;
  }
  if (m.pose) {
    earPose.name = m.pose === 'rest' || !(m.pose in EAR_POSES) ? null : m.pose;
    earPose.until = S.t + (m.hold ?? 8);
  }
}

// ---------- взгляд ----------
const lookTarget = new THREE.Object3D();
scene.add(lookTarget);
vrm.lookAt.target = lookTarget;
let cursorNdc = null;          // курсор в координатах окна (может быть за его краем)
let lastCursorAt = -1e9;
const raycaster = new THREE.Raycaster();

// ---------- состояние ----------
const S = {
  t: 0, state: 'idle', speaking: false,
  headYaw: 0, headPitch: 0, headRoll: 0, nod: 0,
  emotion: { happy: 0, angry: 0, sad: 0, relaxed: 0, surprised: 0 },
  emoTarget: { happy: 0, angry: 0, sad: 0, relaxed: 0.15, surprised: 0 },
  emoUntil: 0,
  blink: 0, blinkT: -1, nextBlink: 1.2,
  mouth: { aa: 0, oh: 0, ih: 0, jaw: 0 },
  idleAction: null, nextIdle: 6,
  // отдёргивание от тычка — затухающая пружина (раньше тут был синус 38 Гц — выглядел как тремор)
  recoil: 0, recoilV: 0,
  // «довольная кошка»: 0..1, пока её гладят — жмурится, мягко улыбается, ластится, мурчит
  pleased: 0, patUntil: 0, petSide: 0,
  beerUntil: 0,   // до этого времени у неё «налито» — в простое она прихлёбывает
  paw: false,     // друг разрешил ей водить мышью — рука-лапка на курсоре
};

function setEmotion(name, strength = 0.8, holdSec = 4) {
  for (const k of Object.keys(S.emoTarget)) S.emoTarget[k] = 0;
  if (name === 'ahegao') {                          // своего выражения нет: ahegao.js + румянец + лёгкая улыбка
    S.ahegaoUntil = S.t + holdSec;
    S.shyUntil = S.t + holdSec;
    S.emoTarget.relaxed = 0.35;
    S.emoUntil = S.t + holdSec;
    return;
  }
  if (name === 'cry') {                              // плач: грустное лицо + слёзы по щекам (tears.js)
    S.cryUntil = S.t + holdSec;
    S.emoTarget.sad = clamp(Math.max(strength, 0.7), 0, 1);
    S.emoUntil = S.t + holdSec;
    return;
  }
  if (name === 'extra') S.shyUntil = S.t + holdSec;   // смущение: своего выражения у Vita нет — горит румянец (beauty.js)
  if (name && name in S.emoTarget) S.emoTarget[name] = clamp(strength, 0, 1);
  else S.emoTarget.relaxed = 0.15;
  // сильная радость или злость — иногда тряхнёт головой (и звякнет колокольчиком)
  if ((name === 'happy' || name === 'angry') && strength >= 0.6 && Math.random() < 0.3) headShake(name === 'angry' ? 0.45 : 0.6);
  S.emoUntil = S.t + holdSec;
}

// ---------- жесты ----------
// Точка курсора в сцене — туда тянется лапка, когда она водит мышью, и туда она показывает пальцем.
let cursorWorld = null;
// вещи наряда (юбка в складку, топ, косуха, легинсы, сетка) — юбку тоже отдаём жестам: присела/села — она раздаётся
const garments = addGarments(vrm, outfitName, { gradientMap: toonGradient, bodyMesh: cfg.kimono?.bodyMesh ?? 'Bodybaked', cfg: rawCfg });
// мастерская: 3D-вещи, которые она сделала себе сама (craft.js) — рамки креплений снимаются здесь, в позе покоя
const craft = createCraft({ vrm, scene, renderer, gradientMap: toonGradient, send });
craft.applyAll(rawCfg.craft);
if (!host) window.klavaCraft = craft;   // страница открыта в обычном браузере (проверка) — мастерскую видно снаружи
const gestures = createGestures({ vrm, S, setEmotion, cursorPoint: () => cursorWorld, gradientMap: toonGradient, ears,
  kimono: kimono ?? (garments.skirt ? { skirt: garments.skirt } : null) });
// что хозяину стоит знать, что сработало (еда, газы) — в его журнал
const REPORT = new Set(['chips', 'fish', 'garlic', 'burp', 'fart']);
// мебель и дела (props.js) — одно место за раз; «встать» — слезть со всего и перестать танцевать
const PLACES = ['chair', 'armchair', 'bed', 'sleep', 'bath', 'dance', 'danceBrief', 'caramell', 'caramellSong'];
function playGesture(name, opts) {
  if (name === 'stand') { for (const p of PLACES) gestures.stop(p); return; }
  if (PLACES.includes(name)) for (const p of PLACES) if (p !== name) gestures.stop(p);
  if (!gestures.play(name, opts)) return;
  if (REPORT.has(name)) send({ type: 'played', name });
  if (name === 'beer' || name === 'cheers') S.beerUntil = S.t + 300;   // пьёт — в простое будет прихлёбывать
}

// ---------- звук и губы ----------
const audio = new AudioContext();
const analyser = audio.createAnalyser();
analyser.fftSize = 1024;
analyser.smoothingTimeConstant = 0.45;
analyser.connect(audio.destination);
const timeBuf = new Float32Array(analyser.fftSize);

// ---------- колокольчик на чокере ----------
// Друг 28.09: «чтобы колокольчик позвякивал, когда головой трясёт». Звук синтезируется тут же (без файлов): удар —
// металлические неровные обертоны бубенчика (1 : 2,76 : 5,4 : 8,93) с быстрым затуханием плюс щелчок шарика внутри;
// в шумную тряску удары сыплются чаще — звон. Мимо анализатора: он двигает губы, от колокольчика рот открываться не должен.
let bellVolume = cfg.bellVolume ?? 0.6;
// еда, рыги, пуки и зелёный газ (snacks.js) — свои жесты поверх gestures; звуки молчат вместе с голосом (bell = 0 — без звука)
const snacks = createSnacks({ vrm, scene, gradientMap: toonGradient, audio, gestures, S, setEmotion, mouth: facePlus?.mouth,
  volume: () => (bellVolume > 0 ? cfg.sfxVolume ?? 0.85 : 0) });
// песни (music.js): её кавер или разобранный трек — губы по её голосу, кивок на долю, строки в облачко
// обстановка и дела (props.js): коврик, кресло, кровать, купель, микрофон, наушники, синтезатор, скрипка, бар, танец, сопение во сне
const props = createProps({ vrm, scene, gradientMap: toonGradient, gestures, S, setEmotion, cfg, audio });
const song = createSongPlayer({ audio, S, setEmotion, gestures, showBubble: (t) => showBubble(t), hideBubbleLater: () => hideBubbleLater(), send,
  subtitles: () => subtitlesOn });
let lastBell = -1;
const bellClick = (() => {
  const b = audio.createBuffer(1, Math.floor(audio.sampleRate * 0.006), audio.sampleRate);
  const d = b.getChannelData(0);
  for (let i = 0; i < d.length; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / d.length);
  return b;
})();
function jingle(strength) {
  if (bellVolume <= 0 || audio.state !== 'running') return;
  const now = audio.currentTime;
  const hits = 1 + Math.floor(strength * 2 + Math.random() * strength * 2);
  for (let i = 0; i < hits; i++) {
    const t0 = now + i * (0.03 + Math.random() * 0.035);
    const f = 2300 + Math.random() * 420;
    const amp = bellVolume * (0.035 + 0.09 * strength) * (i === 0 ? 1 : 0.5);
    const len = 0.3 + strength * 0.25;
    const g = audio.createGain();
    g.gain.setValueAtTime(0, t0);
    g.gain.linearRampToValueAtTime(amp, t0 + 0.002);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + len);
    g.connect(audio.destination);
    for (const [k, a, d] of [[1, 1, 1], [2.76, 0.5, 0.6], [5.4, 0.25, 0.4], [8.93, 0.12, 0.25]]) {
      const o = audio.createOscillator();
      o.frequency.value = f * k * (1 + (Math.random() - 0.5) * 0.004);
      const og = audio.createGain();
      og.gain.setValueAtTime(a, t0);
      og.gain.exponentialRampToValueAtTime(0.0001, t0 + len * d);
      o.connect(og).connect(g);
      o.start(t0);
      o.stop(t0 + len + 0.05);
    }
    const n = audio.createBufferSource();
    n.buffer = bellClick;
    const bp = audio.createBiquadFilter();
    bp.type = 'bandpass'; bp.frequency.value = 5200; bp.Q.value = 3;
    const ng = audio.createGain();
    ng.gain.value = amp * 0.7;
    n.connect(bp).connect(ng).connect(audio.destination);
    n.start(t0);
  }
}
const freqBuf = new Float32Array(analyser.frequencyBinCount);
const binHz = audio.sampleRate / analyser.fftSize;
function bandEnergy(lo, hi) {
  let s = 0, n = 0;
  for (let i = Math.floor(lo / binHz); i <= Math.min(freqBuf.length - 1, Math.ceil(hi / binHz)); i++) { s += 10 ** (freqBuf[i] / 10); n++; }
  return n ? s / n : 0;
}
function lipSync(dt) {
  let level = 0, low = 0.33, mid = 0.34, high = 0.33;
  // поёт — рот по её отдельной дорожке голоса (микс с барабанами в анализатор не идёт); говорит поверх песни — речь
  const src = S.speaking ? analyser : song.singing ? song.analyser : null;
  if (src) {
    src.getFloatTimeDomainData(timeBuf);
    let sum = 0;
    for (const v of timeBuf) sum += v * v;
    level = clamp((Math.sqrt(sum / timeBuf.length) - 0.012) * 9, 0, 1);
    src.getFloatFrequencyData(freqBuf);
    const l = bandEnergy(150, 700), m = bandEnergy(700, 1600), h = bandEnergy(1600, 4000);
    const tot = l + m + h || 1;
    low = l / tot; mid = m / tot; high = h / tot;
  }
  // Друг 29.09: «рот при разговоре проваливается в челюсть». Раньше aa+oh+ih складывались до 1,6 — пещера на пол-лица при
  // неподвижном подбородке. Теперь раскрытие делят челюсть (jaw.js) и губы, а форма губ в сумме не больше lipMax.
  let aa = level * (0.55 + 0.45 * mid), oh = level * low * 0.8, ih = level * high * 0.9;
  const lipMax = cfg.lipMax ?? 0.5, sum = aa + oh + ih;
  if (sum > lipMax) { const k = lipMax / sum; aa *= k; oh *= k; ih *= k; }
  const target = { aa, oh, ih, jaw: level * (cfg.jawTalk ?? 0.7) * (1 - 0.5 * high) };
  for (const k of Object.keys(S.mouth)) {
    const rate = target[k] > S.mouth[k] ? 28 : 14;   // рот открывается быстрее, чем закрывается
    S.mouth[k] = damp(S.mouth[k], target[k], rate, dt);
  }
}

const queue = [];
let current = null;
const b64ToBuf = (b64) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0)).buffer;

// сколько ждать после рыга/пука до слов (звук + облачко)
const FX_LEAD = { burp: 1150, fart: 1400 };
let stopped = false;
async function playNext() {
  stopped = false;
  const m = queue.shift();
  if (!m) {
    current = null; S.speaking = false;
    hideBubbleLater();
    send({ type: 'idle' });
    return;
  }
  try {
    if (audio.state === 'suspended') await audio.resume();
    if (m.stream) { await startStream(m); return; }
    const buf = await audio.decodeAudioData(b64ToBuf(m.wav));
    const src = audio.createBufferSource();
    src.buffer = buf;
    src.connect(analyser);
    current = src;
    S.speaking = true;
    if (m.emotion) setEmotion(m.emotion, m.strength ?? 0.8, buf.duration + 2);
    if (m.gesture) playGesture(m.gesture);
    // рыг/пук перед фразой: сначала звук с облачком, потом слова (в духе Рика — «*буэрп* слушай…»)
    if (m.fx && m.fxAt !== 'after') {
      playGesture(m.fx);
      if (FX_LEAD[m.fx]) {                              // еда — жуёт под слова, рыг/пук — сначала звук
        await new Promise((r) => setTimeout(r, FX_LEAD[m.fx]));
        if (stopped) return;
      }
    }
    if (subtitlesOn) showBubble(m.text);
    S.nod = 1;
    src.onended = () => {
      if (current !== src) return;
      // рыг/пук после слов (или посреди фразы — мозг режет её на метке): звук, пауза, дальше следующая
      if (m.fx && m.fxAt === 'after') {
        playGesture(m.fx);
        send({ type: 'said', id: m.id });
        setTimeout(() => { if (current === src) playNext(); }, FX_LEAD[m.fx] ?? 0);
        return;
      }
      if (Math.random() < 0.45) S.blinkReq = true;   // конец фразы — моргнуть, как люди на паузе
      send({ type: 'said', id: m.id });
      playNext();
    };
    src.start();
  } catch (e) {
    send({ type: 'error', message: `звук ${m.id}: ${e}` });
    playNext();
  }
}
// Голос потоком (друг 29.09: «убрать задержку перед ответом»): хозяин шлёт say_start, потом say_chunk с сырым PCM
// 16 бит 24 кГц по мере синтеза и say_end. Куски ставятся в очередь звуковой карты встык (без щелей), первый звучит
// сразу — не ждём, пока голос досинтезирует всю фразу.
function pcmToBuffer(b64) {
  const bytes = b64ToBuf(b64);
  const n = bytes.byteLength >> 1;
  const view = new DataView(bytes);
  const buf = audio.createBuffer(1, Math.max(1, n), 24000);
  const ch = buf.getChannelData(0);
  for (let i = 0; i < n; i++) ch[i] = view.getInt16(i * 2, true) / 32768;
  return buf;
}
function streamItem(id) {
  if (current?.stream && current.id === id) return current;
  return queue.find((q) => q.stream && q.id === id) ?? null;
}
async function startStream(m) {
  current = m;
  m.live = new Set();
  m.nextT = 0;
  if (m.gesture) playGesture(m.gesture);
  if (m.fx && m.fxAt !== 'after') {
    playGesture(m.fx);
    if (FX_LEAD[m.fx]) {                              // рыг/пук — сначала звук с облачком, куски ждут
      await new Promise((r) => setTimeout(r, FX_LEAD[m.fx]));
      if (stopped || current !== m) return;
    }
  }
  S.speaking = true;
  if (m.emotion) setEmotion(m.emotion, m.strength ?? 0.8, 4);
  if (subtitlesOn) showBubble(m.text);
  S.nod = 1;
  // Заикание (друг 01.10): когда голос синтезируется медленнее, чем звучит (мозг и голос делят видеокарту), каждый
  // опоздавший кусок раньше давал щель посреди слова. Теперь фраза стартует с запасом (lead от хозяина — по темпу
  // прошлых фраз), а если очередь всё же доиграла раньше следующего куска — не лепим по одному, а копим запас
  // (растущий), пока синтез не догонит или фраза не кончится: одна пауза вместо дрожи.
  m.pump = () => {
    if (current !== m) return;
    const now = audio.currentTime;
    const starved = m.nextT > 0 && m.nextT < now + 0.02;
    if (starved && m.chunks.length && !m.done) {
      m.rebuf ??= 0.3;
      const have = m.chunks.reduce((sum, c) => sum + (c.length * 0.75) / 48000, 0);
      if (have < m.rebuf) return;                       // ждём следующий кусок (или конец фразы)
      m.rebuf = Math.min(1.8, m.rebuf * 1.7);
    }
    while (m.chunks.length && current === m) {
      const src = audio.createBufferSource();
      src.buffer = pcmToBuffer(m.chunks.shift());
      src.connect(analyser);
      // первый кусок — с запасом (0,12 с или больше, если синтез не поспевает), дальше — встык
      const t0 = Math.max(audio.currentTime + (m.nextT ? 0.03 : Math.max(0.12, Number(m.lead) || 0)), m.nextT);
      src.start(t0);
      m.nextT = t0 + src.buffer.duration;
      m.live.add(src);
      src.onended = () => { m.live.delete(src); maybeFinish(m); };
      // эмоция держится, пока звучит фраза
      S.emoUntil = Math.max(S.emoUntil, S.t + (m.nextT - audio.currentTime) + 1.5);
    }
  };
  m.pump();
  maybeFinish(m);
}
function maybeFinish(m) {
  if (current !== m || !m.done || m.chunks.length || m.live.size || m.finished) return;
  m.finished = true;
  if (m.fx && m.fxAt === 'after') {
    playGesture(m.fx);
    send({ type: 'said', id: m.id });
    setTimeout(() => { if (current === m) playNext(); }, FX_LEAD[m.fx] ?? 0);
    return;
  }
  if (Math.random() < 0.45) S.blinkReq = true;
  send({ type: 'said', id: m.id });
  playNext();
}
function streamChunk(m) {
  const it = streamItem(m.id);
  if (!it) return;
  it.chunks.push(m.pcm);
  if (it === current && it.pump) it.pump();
}
function streamEnd(m) {
  const it = streamItem(m.id);
  if (!it) return;
  it.done = true;
  if (it === current) { it.pump?.(); maybeFinish(it); }   // хвост, который копился в запас, доигрывается
}

function stopSpeech() {
  stopped = true;
  queue.length = 0;
  if (current) {
    const c = current;
    current = null;
    if (c.stream) { for (const src of c.live ?? []) try { src.stop(); } catch { } }
    else try { c.stop(); } catch { }
  }
  S.speaking = false;
}

// ---------- пузырь и индикатор ----------
const bubble = document.getElementById('bubble');
const stateEl = document.getElementById('state');
let bubbleTimer = 0;
// Место облачка решается ОДИН раз на фразу (друг 28.09: «облачко лагает, мерцает то снизу, то сверху» — место
// пересчитывалось каждый кадр: внизу шрифт мельче, облачко ниже и «влезает» наверх, там крупнее — не влезает, и по кругу;
// а голова теперь сама двигается и границу качает). В пределах фразы облачко над головой только плавно едет за ней.
let bubbleLow = false, bubbleTop = -1;
// суфлёр (текст её реплик облачком) можно выключить — друг 28.09: «чтобы я не видел текста, как будто его нет»; флаг шлёт хозяин
let subtitlesOn = true;
function showBubble(text) {
  clearTimeout(bubbleTimer);
  bubble.classList.remove('low');
  bubble.textContent = text;
  bubble.classList.add('show');
  const h = headCircle();
  // запас 24 px: голова кивает и склоняется — облачко не должно упираться в край и прыгать
  bubbleLow = h.y - h.r * 1.1 - 8 - bubble.offsetHeight < 28;
  bubble.classList.toggle('low', bubbleLow);
  bubbleTop = -1;
}
function hideBubbleLater() {
  clearTimeout(bubbleTimer);
  bubbleTimer = setTimeout(() => bubble.classList.remove('show'), 3500);
}
// Облачко стояло у верхнего края окна — ровно на лице (друг 28.09: «текст её перекрывает, это комично»). Теперь по
// проекции головы: над макушкой, если влезает; не влезает — внизу окна поверх ног, компактнее. «Думаю…» — над головой.
function placeHeadUi() {
  const showB = bubble.classList.contains('show'), showS = stateEl.classList.contains('show');
  if (!showB && !showS) return;
  const h = headCircle();
  let room = h.y - h.r * 1.1 - 8;           // нижний край свободного места над головой
  if (showS) {
    const sh = stateEl.offsetHeight;
    const st = Math.max(4, room - sh);
    stateEl.style.top = `${st}px`;
    stateEl.style.left = `${clamp(h.x, 70, innerWidth - 70)}px`;
    if (st + sh <= room) room = st - 6;
  }
  if (showB) {
    const bh = bubble.offsetHeight;
    const want = bubbleLow ? innerHeight - bh - 10 : Math.max(4, room - bh);
    // над головой — мягко вслед за головой (без дрожи от кивков), внизу — на месте
    bubbleTop = bubbleTop < 0 || bubbleLow ? want : bubbleTop + (want - bubbleTop) * 0.12;
    bubble.style.top = `${Math.round(bubbleTop)}px`;
  }
}
const stateText = { listening: '🎙 слушаю…', thinking: '💭 думаю…', idle: '' };
function setState(s) {
  S.state = s;
  stateEl.textContent = stateText[s] ?? '';
  stateEl.classList.toggle('show', !!stateText[s]);
  if (s === 'listening') setEmotion('relaxed', 0.35, 30);
}

// ---------- ввод ----------
function onCursor(x, y) {
  cursorNdc = new THREE.Vector2((x / innerWidth) * 2 - 1, -(y / innerHeight) * 2 + 1);
  lastCursorAt = S.lastCursorAt = S.t;
  petTrack(x, y);
}

// Голова на экране: круг вокруг черепа вместе с ушками — считается проекцией при любом приближении.
function headCircle() {
  const c = B.head.getWorldPosition(new THREE.Vector3()).add(new THREE.Vector3(0, 0.1, 0));
  const pc = c.clone().project(camera);
  const pt = c.clone().add(new THREE.Vector3(0, 0.17, 0)).project(camera);
  return { x: (pc.x + 1) / 2 * innerWidth, y: (1 - pc.y) / 2 * innerHeight, r: Math.abs(pt.y - pc.y) / 2 * innerHeight };
}
function onHead(x, y) {
  const h = headCircle();
  return Math.hypot(x - h.x, y - h.y) < h.r;
}

function onPoke(x, y) {
  const h = headCircle();
  const part = Math.hypot(x - h.x, y - h.y) < h.r ? 'head' : 'body';
  if (part === 'head') {                     // похлопали по голове — как погладили: жмурится
    S.patUntil = S.t + 2.5;
    S.petSide = Math.sign(x - h.x) || 1;
  } else {                                   // ткнули в бок: надула губки и чуть отпрянула
    setEmotion('angry', 0.35, 1.4);
    S.recoilV += 2.2;
  }
  send({ type: 'poked', part });
}

// ---------- поглаживание ----------
// Гладить — водить курсором туда-сюда по голове, кнопку нажимать не нужно. Считаем пройденный
// над головой путь и развороты движения: просто проехать мышью мимо — не поглаживание.
const pet = { score: 0, lastX: null, lastY: 0, dirX: 0, dirY: 0, flips: [], active: false, since: 0, idleSince: 0, reported: false };
function petTrack(x, y) {
  if (!onHead(x, y)) { pet.lastX = null; return; }
  if (pet.lastX !== null) {
    const dx = x - pet.lastX, dy = y - pet.lastY;
    const dist = Math.hypot(dx, dy);
    if (dist > 1.5) {
      const h = headCircle();
      pet.score = Math.min(3, pet.score + dist / Math.max(20, h.r * 1.5));
      const sx = Math.abs(dx) > 1.5 ? Math.sign(dx) : 0, sy = Math.abs(dy) > 1.5 ? Math.sign(dy) : 0;
      if ((sx && pet.dirX && sx !== pet.dirX) || (sy && pet.dirY && sy !== pet.dirY)) pet.flips.push(S.t);
      if (sx) pet.dirX = sx;
      if (sy) pet.dirY = sy;
      S.petSide = Math.sign(x - h.x) || S.petSide;
    }
  }
  pet.lastX = x;
  pet.lastY = y;
}
function petUpdate(dt) {
  pet.score = Math.max(0, pet.score - dt * 0.7);
  pet.flips = pet.flips.filter((t) => S.t - t < 1.6);
  const stroking = pet.score > 0.5 && pet.flips.length >= 2;
  if (stroking) {
    if (!pet.active) { pet.active = true; pet.since = S.t; pet.reported = false; }
    pet.idleSince = S.t;
  } else if (pet.active && S.t - pet.idleSince > 1.5) {
    pet.active = false;
  }
  // гладят дольше секунды — сообщаем хосту один раз за сеанс (он даст ей что-нибудь промурлыкать)
  if (pet.active && !pet.reported && S.t - pet.since > 1.2) { pet.reported = true; send({ type: 'petted' }); }
  const target = pet.active || S.t < S.patUntil ? 1 : 0;
  S.pleased = damp(S.pleased, target, target > S.pleased ? 4 : 1.5, dt);
  purrLevel(S.speaking ? 0 : S.pleased);
}

// Мурчание: коричневый шум через фильтр низких частот, промодулированный ~25 Гц — как у настоящей кошки.
// Идёт мимо анализатора губ, чтобы рот не шевелился.
let purr = null;
function purrLevel(level) {
  const vol = (cfg.purrVolume ?? 0.35) * level;
  if (!purr) {
    if (vol < 0.01) return;
    const len = audio.sampleRate * 2, buf = audio.createBuffer(1, len, audio.sampleRate), ch = buf.getChannelData(0);
    let last = 0;
    for (let i = 0; i < len; i++) { last = (last + 0.02 * (Math.random() * 2 - 1)) / 1.02; ch[i] = last * 3.5; }
    const src = audio.createBufferSource(); src.buffer = buf; src.loop = true;
    const lp = audio.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = 260;
    const am = audio.createGain(); am.gain.value = 0.55;
    const lfo = audio.createOscillator(); lfo.frequency.value = 25;
    const lfoDepth = audio.createGain(); lfoDepth.gain.value = 0.45;
    lfo.connect(lfoDepth).connect(am.gain);
    const master = audio.createGain(); master.gain.value = 0;
    src.connect(lp).connect(am).connect(master).connect(audio.destination);
    src.start(); lfo.start();
    purr = { master };
  }
  if (audio.state === 'suspended') audio.resume().catch(() => {});
  purr.master.gain.setTargetAtTime(vol, audio.currentTime, 0.25);
}
function onZoom(delta) {
  viewZoom = null;
  camDist = clamp(camDist * (delta > 0 ? 1.08 : 0.92), cfg.cameraDistanceMin, camFull * 1.15);
  placeCamera();
}

// Вид из «Настроек вида» хоста: zoom 0 — в полный рост, 1 — лицо крупно; yaw — поворот в градусах.
const baseYaw = vrm.scene.rotation.y;          // VRM 0.x уже развёрнута к камере (rotateVRM0)
let viewZoom = null;
let userYaw = 0;                                  // поворот из «Настроек вида» (рад); танцевальный оборот — поверх
const distFromZoom = (z) => camFull - (camFull - cfg.cameraDistanceMin) * clamp(z, 0, 1);
function setView(m) {
  if (typeof m.zoom === 'number') { viewZoom = m.zoom; camDist = distFromZoom(viewZoom); }
  if (typeof m.yaw === 'number') { userYaw = THREE.MathUtils.degToRad(m.yaw); vrm.scene.rotation.y = baseYaw + userYaw; }
  placeCamera();
}

// Что она расслышала — на пару секунд в плашке состояния: сразу видно, слух ошибся или мозг.
let heardTimer = 0;
function showHeard(text) {
  clearTimeout(heardTimer);
  stateEl.textContent = `👂 «${text.length > 70 ? text.slice(0, 70) + '…' : text}»`;
  stateEl.classList.add('show');
  heardTimer = setTimeout(() => setState(S.state), 4000);
}

function handleMessage(m) {
  switch (m?.type) {
    case 'say': queue.push(m); if (!current) playNext(); break;
    case 'say_start': queue.push({ ...m, stream: true, chunks: [], done: false }); if (!current) playNext(); break;
    case 'say_chunk': streamChunk(m); break;
    case 'say_end': streamEnd(m); break;
    case 'stop': stopSpeech(); break;
    case 'song': song.play(m); break;
    case 'song_stop': song.stop(); break;
    case 'groove': song.live(m.period, m.next); break;
    case 'rug': cfg.rug = !!m.on; break;
    case 'state': setState(m.value); break;
    case 'emotion': setEmotion(m.name, m.strength ?? 0.8, m.hold ?? 4); if (m.gesture) playGesture(m.gesture); break;
    case 'gesture_stop': gestures.stop(m.name); break;
    case 'gesture':
      playGesture(m.name);
      // «ня / лапки-царапки» — показать коготки
      if (m.name === 'nya' || m.name === 'paw') claws?.flash(2.5);
      break;
    case 'ears': earsCommand(m); break;
    case 'claws':
      if (m.flash) claws?.flash(Number(m.flash) || 2.5);
      if (typeof m.visible === 'boolean') claws?.setVisible(m.visible);
      break;
    case 'paw':
      S.paw = !!m.on;
      if (S.paw) playGesture('paw'); else gestures.stop('paw');
      break;
    case 'cursor': onCursor(m.x, m.y); break;
    case 'poke': onPoke(m.x, m.y); break;
    case 'zoom': onZoom(m.delta); break;
    case 'view': setView(m); break;
    case 'heard': showHeard(m.text); break;
    case 'bubble': showBubble(m.text); hideBubbleLater(); placeHeadUi(); break;
    case 'bubble_hide': clearTimeout(bubbleTimer); bubble.classList.remove('show'); break;
    case 'subtitles':
      subtitlesOn = !!m.on;
      if (!subtitlesOn) { clearTimeout(bubbleTimer); bubble.classList.remove('show'); }
      break;
    case 'bell': bellVolume = Math.max(0, Number(m.volume) || 0); break;
    case 'shake': headShake(Number(m.duration) || 0.7); break;
    case 'outfit': changeOutfit(String(m.name)); break;
    case 'selfie': send({ type: 'selfie', id: m.id, data: selfie() }); break;
    case 'craft': craft.handle(m); break;
    case 'garlic': S.garlicUntil = Number(m.on) === 0 ? 0 : S.t + (Number(m.sec) || 150); break;   // «пахнет чесноком» без жеста
  }
}
if (host) host.addEventListener('message', (e) => handleMessage(e.data));
window.addEventListener('message', (e) => handleMessage(e.data));

if (!host) {
  addEventListener('mousemove', (e) => onCursor(e.clientX, e.clientY));
  addEventListener('click', (e) => onPoke(e.clientX, e.clientY));
  addEventListener('wheel', (e) => onZoom(e.deltaY));
  window.addEventListener('keydown', (e) => {
    if (e.key === 'b' || e.key === 'B' || e.key === 'и' || e.key === 'И') playGesture('beer');
    else if (e.key === 'c' || e.key === 'C' || e.key === 'с' || e.key === 'С') playGesture('cheers');
  });
}

// ---------- маска силуэта ----------
// Окно хоста обрезается по этой маске: клики мимо Клавы уходят в окна под ней.
// Друг 02.10: «края рваные, пиксельные, в движухе её рвёт, моделька то обрезанная, вечно глитчевые полосы». Регион
// окна — жёсткий (без полупрозрачности), а маска была грубой: клетка 4 px, запас одна клетка, раз в 150 мс, отрисовка
// в четверть разрешения. Сглаженный край резался лесенкой; в танце руки и хвост за 150 мс вылетали за маску и
// обрезались; пальцы, пряди и кончик хвоста тоньше клетки в маску не попадали. Теперь: маска рисуется вдвое подробнее
// клетки (SUB), запас — MASK_PAD клетки, к ней добавляется прошлая (покрывает движение между обновлениями), раз в 80 мс.
const cell = cfg.maskCell;
const SUB = 2, MASK_PAD = 2;
const maskEvery = Math.min(cfg.maskIntervalMs ?? 80, 80);
let mw = 0, mh = 0, rt = null, px = null, lastMask = '', prevBits = null;
function allocMask() {
  const w = Math.max(100, innerWidth || 440), h = Math.max(100, innerHeight || 900);
  mw = Math.ceil(w / cell); mh = Math.ceil(h / cell);
  rt?.dispose();
  rt = new THREE.WebGLRenderTarget(mw * SUB, mh * SUB);
  px = new Uint8Array(mw * SUB * mh * SUB * 4);
  prevBits = null;
}
allocMask();
function markRect(bits, r) {
  const x0 = clamp(Math.floor(r.left / cell), 0, mw - 1), x1 = clamp(Math.ceil(r.right / cell), 0, mw);
  const y0 = clamp(Math.floor(r.top / cell), 0, mh - 1), y1 = clamp(Math.ceil(r.bottom / cell), 0, mh);
  for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) bits[y * mw + x] = 1;
}
function computeMask() {
  if (!rt || mw <= 0 || mh <= 0) return;
  const W = mw * SUB, H = mh * SUB;
  renderer.setRenderTarget(rt);
  renderer.clear();
  renderer.render(scene, camera);
  renderer.readRenderTargetPixels(rt, 0, 0, W, H, px);
  renderer.setRenderTarget(null);
  let opaqueCount = 0;
  const raw = new Uint8Array(mw * mh);
  for (let y = 0; y < H; y++) {
    const src = (H - 1 - y) * W;              // у render target начало снизу
    const row = ((y / SUB) | 0) * mw;
    for (let x = 0; x < W; x++) {
      if (px[(src + x) * 4 + 3] > 6) {
        const i = row + ((x / SUB) | 0);
        if (!raw[i]) { raw[i] = 1; opaqueCount++; }
      }
    }
  }
  if (opaqueCount < 50 && !bubble.classList.contains('show')) return;

  // запас MASK_PAD клеток во все стороны (сначала по строкам, потом по столбцам): сглаженный край и то, что сдвинется до
  // следующей маски, не режутся
  const wide = new Uint8Array(mw * mh);
  for (let y = 0; y < mh; y++) for (let x = 0; x < mw; x++) {
    if (!raw[y * mw + x]) continue;
    for (let dx = -MASK_PAD; dx <= MASK_PAD; dx++) { const xx = x + dx; if (xx >= 0 && xx < mw) wide[y * mw + xx] = 1; }
  }
  const cur = new Uint8Array(mw * mh);
  for (let y = 0; y < mh; y++) for (let x = 0; x < mw; x++) {
    if (!wide[y * mw + x]) continue;
    for (let dy = -MASK_PAD; dy <= MASK_PAD; dy++) { const yy = y + dy; if (yy >= 0 && yy < mh) cur[yy * mw + x] = 1; }
  }
  // плюс прошлая маска: пока окно ждёт новую, рука, ушедшая с прошлого места, не обрезается
  const bits = new Uint8Array(mw * mh);
  for (let i = 0; i < bits.length; i++) bits[i] = cur[i] | (prevBits?.[i] ?? 0);
  prevBits = cur;
  if (bubble.classList.contains('show')) markRect(bits, bubble.getBoundingClientRect());
  if (stateEl.classList.contains('show')) markRect(bits, stateEl.getBoundingClientRect());
  const packed = new Uint8Array(Math.ceil(bits.length / 8));
  for (let i = 0; i < bits.length; i++) if (bits[i]) packed[i >> 3] |= 1 << (i & 7);
  let s = '';
  for (let i = 0; i < packed.length; i += 0x8000) s += String.fromCharCode(...packed.subarray(i, i + 0x8000));
  const b64 = btoa(s);
  if (b64 !== lastMask) { lastMask = b64; send({ type: 'mask', w: mw, h: mh, cell, bits: b64 }); }
}

addEventListener('resize', () => {
  renderer.setSize(innerWidth, innerHeight);
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  const wasFull = camDist >= camFull * 0.99 || !Number.isFinite(camDist);
  camFull = fullDistance();
  if (viewZoom !== null) camDist = distFromZoom(viewZoom);
  else if (wasFull) camDist = camFull;
  placeCamera();
  allocMask();
  lastMask = '';
});

// ---------- поведение ----------
const tmpV = new THREE.Vector3();
function updateLookTarget() {
  const eye = B.head.getWorldPosition(tmpV).clone().add(new THREE.Vector3(0, 0.06, 0));
  let target;
  if (cursorNdc) {
    raycaster.setFromCamera(cursorNdc, camera);
    cursorWorld = raycaster.ray.at(Math.max(0.3, camDist - 0.25), new THREE.Vector3());
  }
  if (S.paw && cursorWorld) {
    target = cursorWorld.clone();                    // водит мышью — смотрит на свою лапку
  } else if (S.state === 'thinking' || gestures.out.lookUp > 0.3) {
    target = eye.clone().add(new THREE.Vector3(-0.35, 0.3, 0.8));
  } else if (S.idleAction?.kind === 'lookAway') {
    target = eye.clone().add(S.idleAction.dir);
  } else if (cursorWorld && S.t - lastCursorAt < 4) {
    target = cursorWorld.clone();
  } else {
    target = camera.position.clone();
  }
  // Окно нулевого размера при старте давало NaN в камере, и взгляд оставался NaN навсегда — не берём такое.
  if (!Number.isFinite(target.x + target.y + target.z)) target = camera.position.clone();
  if (!Number.isFinite(lookTarget.position.x + lookTarget.position.y + lookTarget.position.z)) lookTarget.position.copy(target);
  // сглаживание по времени, а не «12 % за кадр»: иначе скорость взгляда зависела от частоты кадров
  lookTarget.position.lerp(target, 1 - Math.exp(-7 * lastStep));
  return eye;
}

// Простой: раз в 7–19 с она что-нибудь делает сама — отводит взгляд, наклоняет голову, дёргает ушами,
// чешет ухо, потягивается, скрещивает руки, оглядывается; если «налито» — прихлёбывает киберпиво.
// Не мешает: не во время речи, не когда гладят и не когда водит мышью.
function idleBehaviour() {
  if (S.idleAction && S.t > S.idleAction.until) S.idleAction = null;
  if (!S.idleAction && !S.speaking && !S.paw && S.pleased < 0.1 && S.state === 'idle' && S.t > S.nextIdle && gestures.active.length === 0) {
    const r = Math.random();
    const beer = S.t < S.beerUntil;
    if (beer && r < 0.3) { S.idleAction = { kind: 'gesture', until: S.t + 4 }; playGesture('beer'); }
    else if (r < 0.22) S.idleAction = { kind: 'lookAway', until: S.t + 1.6, dir: new THREE.Vector3((Math.random() - 0.5) * 1.2, (Math.random() - 0.3) * 0.5, 0.9) };
    else if (r < 0.38) { S.idleAction = { kind: 'tilt', until: S.t + 2.2 }; S.headRoll = (Math.random() < 0.5 ? -1 : 1) * 0.13; }
    else if (r < 0.46) { S.idleAction = { kind: 'flick', until: S.t + 0.4 }; for (const ear of ears) ear.twitch = 1; }   // дёрнуть обоими ушками
    else if (r < 0.54) { S.idleAction = { kind: 'shake', until: S.t + 0.9 }; headShake(0.75); for (const ear of ears) ear.twitch = 1; }   // по-кошачьи встряхнулась
    else {
      const pool = ['ear', 'ear', 'stretch', 'arms', 'turn', 'think', 'lean', 'hips', 'nya', 'facepalm'];
      const g = pool[Math.floor(Math.random() * pool.length)];
      S.idleAction = { kind: 'gesture', until: S.t + 4 };
      playGesture(g);
    }
    S.nextIdle = S.t + 7 + Math.random() * 12;
  }
}

// Моргание. Друг 29.09: «добавить моргание глазками». Было — треугольник 0,16 с: глаз закрыт один кадр, моргание почти не
// видно. Теперь как у живых: быстро закрыть (~70 мс), задержать (~40 мс), открыть медленнее (~150 мс); иногда дважды подряд;
// моргает на резком повороте взгляда и в конце фразы; по-кошачьи медленно жмурится (знак доверия), когда спокойно слушает
// или смотрит на друга. S.blink — итог для выражения, S.blinkT/S.nextBlink — как раньше (тесты их трогают).
const BL = { close: 0.07, hold: 0.04, open: 0.15, depth: 1, double: false, lastYaw: 0, yawV: 0, nextSlow: 25 };
function startBlink(kind = 'fast') {
  if (S.blinkT >= 0) return;
  S.blinkT = 0;
  if (kind === 'slow') { BL.close = 0.32; BL.hold = 0.3; BL.open = 0.5; BL.depth = 0.96; BL.double = false; }
  else {
    BL.close = 0.055 + Math.random() * 0.03; BL.hold = 0.025 + Math.random() * 0.04; BL.open = 0.12 + Math.random() * 0.08;
    BL.depth = 1; BL.double = Math.random() < 0.13;
  }
}
function blink(dt) {
  // резкий поворот взгляда — живые при этом моргают
  const yawV = Math.abs(S.headYaw - BL.lastYaw) / Math.max(dt, 1e-3);
  BL.lastYaw = S.headYaw;
  if (S.blinkT < 0) {
    if (yawV > 1.1 && S.t - (BL.lastEnd ?? 0) > 0.8 && Math.random() < 0.08) startBlink();
    else if (S.blinkReq) startBlink();
    else if (S.t > S.nextBlink) startBlink();
    else if (S.t > BL.nextSlow && !S.speaking && (S.state === 'listening' || S.t - (S.lastCursorAt ?? -99) < 3)) {
      startBlink('slow');
      BL.nextSlow = S.t + 25 + Math.random() * 40;
    }
    S.blinkReq = false;
  }
  if (S.blinkT < 0) { S.blink = 0; return; }
  S.blinkT += dt;
  const t = S.blinkT, c = BL.close, h = BL.hold, o = BL.open;
  if (t < c) { const u = t / c; S.blink = BL.depth * u * u; }                               // закрывается с разгона
  else if (t < c + h) S.blink = BL.depth;
  else if (t < c + h + o) { const u = (t - c - h) / o; S.blink = BL.depth * (1 - u * u * (3 - 2 * u)); }       // открывается плавно
  else {
    S.blinkT = -1; S.blink = 0; BL.lastEnd = S.t;
    if (BL.double) { BL.double = false; S.nextBlink = S.t + 0.09 + Math.random() * 0.06; }
    else S.nextBlink = S.t + (Math.random() < 0.12 ? 0.35 + Math.random() * 0.4 : 1.6 + Math.random() * Math.random() * 6.5);
  }
}

// ---------- живая голова ----------
// Друг 28.09: «чтобы она сама была живее и головой сама двигала». Раньше голова только шла за курсором да раз в 7–19 с
// делала одно движение. Теперь: говорит — «жестикулирует» головой (поворот и наклон к слушателю раз в 1–2,5 с, кивок на
// ударный слог); молчит — разглядывает, склоняет голову; настроение — в осанке (грусть вниз, смущение вниз и набок,
// радость чуть вверх, злость исподлобья); иногда по-кошачьи встряхивает головой — и звенит колокольчик на чокере.
const HL = { yaw: 0, pitch: 0, roll: 0, ty: 0, tp: 0, tr: 0, next: 0, prevAa: 0, onsets: 0, nodEvery: 3, shakeT: 0, shakeDur: 0.7, shake: 0 };
function headShake(dur = 0.7) { HL.shakeT = HL.shakeDur = dur; }
function headLife(dt) {
  const t = S.t, e = S.emotion;
  if (t > HL.next) {
    if (S.speaking) {
      HL.ty = (Math.random() - 0.5) * 0.24; HL.tp = (Math.random() - 0.4) * 0.1; HL.tr = (Math.random() - 0.5) * 0.18;
      HL.next = t + 1 + Math.random() * 1.5;
    } else if (S.state === 'listening') {
      HL.ty = (Math.random() - 0.5) * 0.08; HL.tp = 0.02; HL.tr = (Math.random() < 0.5 ? -1 : 1) * (0.06 + Math.random() * 0.06);
      HL.next = t + 2.5 + Math.random() * 3;
    } else {
      const still = Math.random() < 0.4;
      HL.ty = still ? 0 : (Math.random() - 0.5) * 0.2; HL.tp = (Math.random() - 0.5) * 0.07; HL.tr = (Math.random() - 0.5) * 0.12;
      HL.next = t + 2 + Math.random() * 4;
    }
  }
  const aa = S.mouth.aa;
  if (S.speaking && aa > 0.45 && HL.prevAa < 0.25 && ++HL.onsets % HL.nodEvery === 0) {
    S.nod += 0.5 + Math.random() * 0.45;
    HL.nodEvery = 2 + Math.floor(Math.random() * 3);
  }
  HL.prevAa = aa;
  const shy = S.shyUntil && t < S.shyUntil ? 1 : 0;
  const moodP = e.sad * 0.14 + shy * 0.12 - e.happy * 0.04 + e.angry * 0.06 - (S.aheg ?? 0) * 0.14;   // ахегао — голову назад
  const moodR = shy * 0.14 * (HL.tr >= 0 ? 1 : -1);
  const k = Math.min(1, dt * 2.2);
  HL.yaw += (HL.ty - HL.yaw) * k;
  HL.pitch += (HL.tp + moodP - HL.pitch) * k;
  HL.roll += (HL.tr + moodR - HL.roll) * k;
  HL.shake = 0;
  if (HL.shakeT > 0) {
    HL.shakeT = Math.max(0, HL.shakeT - dt);
    const left = HL.shakeT / HL.shakeDur;
    HL.shake = Math.sin((HL.shakeDur - HL.shakeT) * 2 * Math.PI * 6.5) * 0.22 * left;
  }
}

function pose(dt, eye) {
  const t = S.t;
  headLife(dt);
  const breath = Math.sin(t * 1.7);
  // руки опущены из T-позы (у повёрнутой VRM 0.x знак обратный), дыхание чуть поднимает плечи
  B.leftUpperArm.rotation.set(0, 0, 1.2 - breath * 0.015);
  B.rightUpperArm.rotation.set(0, 0, -1.2 + breath * 0.015);
  B.leftLowerArm.rotation.set(0, 0.25, 0.1);
  B.rightLowerArm.rotation.set(0, -0.25, -0.1);
  B.spine.rotation.set(breath * 0.012, Math.sin(t * 0.45) * 0.03, Math.sin(t * 0.37) * 0.012);
  B.chest.rotation.set(breath * 0.018, 0, 0);
  B.hips.rotation.set(0, Math.sin(t * 0.45 + 1) * 0.02, Math.sin(t * 0.31) * 0.01);

  // голова тянется за взглядом, шея берёт треть поворота
  const d = lookTarget.position.clone().sub(eye);
  const yaw = clamp(Math.atan2(d.x, d.z), -0.6, 0.6);
  const pitch = clamp(-Math.atan2(d.y, Math.hypot(d.x, d.z)), -0.35, 0.35);
  // один NaN (окно нулевого размера при старте) навсегда ломал голову: NaN в голове → NaN во взгляде → снова в голове
  S.headYaw = Number.isFinite(yaw + S.headYaw) ? damp(S.headYaw, yaw * 0.55, 5, dt) : 0;
  S.headPitch = Number.isFinite(pitch + S.headPitch) ? damp(S.headPitch, pitch * 0.5, 5, dt) : 0;
  const rollTarget = S.idleAction?.kind === 'tilt' || S.state === 'listening' ? S.headRoll || 0.08 : 0;
  S.headRoll = S.idleAction?.kind === 'tilt' ? S.headRoll : damp(S.headRoll, rollTarget, 3, dt);
  S.nod = damp(S.nod, 0, 4, dt);
  // отпрянуть от тычка: пружина ~1,6 Гц с затуханием — одно мягкое движение назад и обратно
  S.recoilV += (-40 * S.recoil - 7 * S.recoilV) * dt;
  S.recoil += S.recoilV * dt;
  const talkBob = S.speaking ? Math.sin(t * 7.5) * 0.012 * (S.mouth.aa + 0.2) : 0;
  const noiseY = Math.sin(t * 0.7) * 0.02 + Math.sin(t * 1.9) * 0.008;
  const noiseX = Math.sin(t * 0.9 + 2) * 0.012;
  // гладят — ластится: голова клонится к руке и чуть приподнимается навстречу, покачивается в такт
  const p = S.pleased;
  const nuzzleRoll = p * (0.16 * S.petSide + Math.sin(t * 2.2) * 0.03);
  const nuzzlePitch = -p * 0.06;
  B.spine.rotation.x += S.recoil * -0.05;
  // живая голова (headLife) поверх взгляда; встряхивание берёт и шею — на ней чокер, колокольчик качается и звенит
  B.neck.rotation.set((S.headPitch + HL.pitch) * 0.35 * (1 - p) - S.recoil * 0.08, (S.headYaw + HL.yaw) * 0.35 * (1 - p) + HL.shake * 0.45,
    (S.headRoll + HL.roll * (1 - p)) * 0.4 + nuzzleRoll * 0.4);
  B.head.rotation.set(((S.headPitch + HL.pitch) * 0.65 + noiseX) * (1 - p) + S.nod * 0.09 + talkBob + nuzzlePitch - S.recoil * 0.12,
    ((S.headYaw + HL.yaw) * 0.65 + noiseY) * (1 - p) + HL.shake * 0.55, (S.headRoll + HL.roll * (1 - p)) * 0.6 + nuzzleRoll * 0.6);
  applyGroove(B, S.groove);   // играет песня — кивает на долю и покачивается
}

function expressions(dt) {
  if (S.t > S.emoUntil) {
    for (const k of Object.keys(S.emoTarget)) S.emoTarget[k] = 0;
    S.emoTarget.relaxed = S.state === 'listening' ? 0.35 : 0.15;
  }
  // Довольная кошка = мягкая улыбка с закрытым ртом (relaxed) + прищур почти до закрытых глаз.
  // «happy» у этой модели — широко открытый рот, при поглаживании он не нужен; прочие эмоции гасим.
  const p = S.pleased;
  for (const k of Object.keys(S.emotion)) {
    S.emotion[k] = damp(S.emotion[k], S.emoTarget[k], 6, dt);
    const name = exprName(k);
    if (!name) continue;
    const v = k === 'relaxed' ? Math.max(S.emotion[k], p) : S.emotion[k] * (1 - p);
    em.setValue(name, v);
  }
  // при улыбке «глазки-дуги» уже закрывают глаза — моргание гасим, чтобы не перебивать
  const g = gestures.out;
  em.setValue('blink', Math.max(S.blink * (1 - S.emotion.happy), p * 0.82, g.blink));
  // подмигнуть: один глаз закрывается, второй чуть прищуривается
  if (hasExpr('blinkLeft')) em.setValue('blinkLeft', Math.max(g.wink.L, g.wink.R * 0.15));
  if (hasExpr('blinkRight')) em.setValue('blinkRight', Math.max(g.wink.R, g.wink.L * 0.15));
  em.setValue('aa', Math.max(S.mouth.aa, g.yawn * 0.85, g.mouth?.aa ?? 0));
  em.setValue('oh', Math.max(S.mouth.oh, g.mouth?.oh ?? 0));
  em.setValue('ih', Math.max(S.mouth.ih, g.mouth?.ih ?? 0));
  S.jawV = Math.max(S.mouth.jaw ?? 0, g.yawn * 0.8, (g.mouth?.aa ?? 0) * 0.6);
  S.aheg = damp(S.aheg ?? 0, S.t < (S.ahegaoUntil ?? 0) ? 1 : 0, 5, dt);
  ahegao.expressions(em, S.aheg);
}

// ---------- танец по рабочему столу ----------
// Оборот вокруг себя (бридж Карамельдансена) — поворот сцены поверх «Настроек вида». Шаги вбок — окно хозяина едет по
// рабочему столу ровно на столько пикселей, на сколько ушла бы она сама (друг 02.10: «шагов не делает, ходить по столу
// не может»): в окне она стоит на месте, а переставляет ноги — ход даёт само окно.
let lastWalkPx = 0;
function danceMove() {
  vrm.scene.rotation.y = baseYaw + userYaw + (gestures.out.spin ?? 0);
  const walk = gestures.out.walk ?? 0;
  let px = 0;
  if (Math.abs(walk) > 1e-4) {
    const a = vrm.humanoid.getRawBoneNode('leftUpperArm').getWorldPosition(new THREE.Vector3());
    const b = vrm.humanoid.getRawBoneNode('rightUpperArm').getWorldPosition(new THREE.Vector3());
    const left = a.sub(b).setY(0).normalize();             // её левая рука в мире
    const c = vrm.scene.getWorldPosition(new THREE.Vector3());
    const p0 = c.clone().project(camera), p1 = c.clone().addScaledVector(left, walk).project(camera);
    px = (p1.x - p0.x) * innerWidth / 2;                    // CSS-пиксели по горизонтали
  }
  if (Math.abs(px - lastWalkPx) >= 0.5 || (px === 0 && lastWalkPx !== 0)) {
    lastWalkPx = px;
    send({ type: 'walk', px: Math.round(px * 10) / 10 });
  }
}

// ---------- цикл ----------
const timer = new THREE.Timer();
let maskAcc = 0, frameAcc = 0, lastStep = 1 / 60;
// Рисуем каждый кадр экрана. Прежние «40 кадров на мониторе 60 Гц» давали неровный шаг
// (кадр, кадр, пропуск) — это и было «дёрганье». Сцена лёгкая, видеокарте LLM это не мешает.
// На мониторах 120+ Гц режем до maxFps, но только целыми кадрами экрана.
const minStep = 1 / (cfg.maxFps ?? 75);
function frame(now) {
  requestAnimationFrame(frame);
  timer.update(now);
  frameAcc += timer.getDelta();
  if (frameAcc < minStep * 0.9) return;
  const step = Math.min(frameAcc, 0.1);
  frameAcc = 0;
  advance(step);
}
function advance(step) {
  lastStep = step;
  S.t += step;
  petUpdate(step);
  idleBehaviour();
  const eye = updateLookTarget();
  song.update(step);
  pose(step, eye);
  gestures.update(step);
  props.update(step);
  danceMove();
  S.sit = gestures.out.sit;   // сидит в игровом кресле — хвост свешивается за сиденье
  blink(step);
  lipSync(step);
  expressions(step);
  earsUpdate(step);
  tail?.update(step, S);
  claws?.update(step, S);
  beauty?.update(step, S);
  facePlus?.update(step, S);
  garments.update(step);
  snacks.update(step, S.t);
  const bs = beauty?.bellShake?.() ?? 0;
  if (bs > cfg.bellThreshold && !S.bellHush && S.t - lastBell > 0.085) { lastBell = S.t; jingle(clamp((bs - cfg.bellThreshold) / 8, 0, 1)); }
  vrm.update(step);
  ahegao.afterUpdate(S.aheg ?? 0);   // глаза под лоб и язык — после взгляда и костей
  jaw.set(Math.max(S.jawV ?? 0, (S.aheg ?? 0) * 0.5));
  facePlus?.afterUpdate();
  S.cryA = damp(S.cryA ?? 0, S.t < (S.cryUntil ?? 0) ? 1 : 0, 3, step);
  tears?.update(step, S.cryA);
  renderer.render(scene, camera);
  placeHeadUi();
  maskAcc += step * 1000;
  if (maskAcc >= maskEvery) { maskAcc = 0; computeMask(); }
}
vrm.springBoneManager?.reset();
// ручка для отладки из консоли браузера: калибровка камеры, проверка выражений
window.__klava = { THREE, vrm, kimono, camera, setView, gestures, tail, claws, beauty, playGesture, headShake, jingle, HL, ahegao, tears, jaw, facePlus, startBlink, snacks, garments, outfitName, changeOutfit, selfie, song, props,
  // прокрутить кадры вручную — в скрытой вкладке requestAnimationFrame стоит
  tick: (n = 1, dt = 1 / 60) => { for (let i = 0; i < n; i++) advance(dt); }, showHeard, onCursor, headCircle, pet, B, S, cfg, headRest, placeCamera, setEmotion, setState, onPoke, onZoom,
  setDist: (d) => { camDist = d; placeCamera(); } };
window.playGesture = playGesture;
window.gestures = gestures;

const urlParams = pageParams;

// Зеркало (друг 29.09: «она себя не видит — пусть знает, как выглядит»): три кадра рядом на светлом фоне — в полный
// рост с того же ракурса, что видит друг (с учётом поворота из «Настроек вида»), в полный рост со спины (её просьба
// 30.09: «зеркало побольше — чтобы хвост и попку было видно») и лицо крупно. Рисуется в свой буфер, окно на экране не
// дёргается. На снимках экрана её нет (окно исключено из захвата) — поэтому свой отдельный кадр.
function selfie() {
  const W = 384, H = 512;
  const rt = new THREE.WebGLRenderTarget(W, H, { samples: 4 });
  rt.texture.colorSpace = THREE.SRGBColorSpace;
  const cam = new THREE.PerspectiveCamera(28, W / H, 0.05, 30);
  const yaw = vrm.scene.rotation.y - baseYaw;
  const fwd = new THREE.Vector3(Math.sin(yaw), 0, Math.cos(yaw));
  const head = B.head.getWorldPosition(new THREE.Vector3());
  const midY = (topY + footY) / 2, tall = topY - footY;
  const setups = [
    () => { const d = (tall / 2) * 1.08 / Math.tan(THREE.MathUtils.degToRad(14)); cam.position.set(0, midY, 0).addScaledVector(fwd, d); cam.lookAt(0, midY, 0); },
    () => { const d = (tall / 2) * 1.08 / Math.tan(THREE.MathUtils.degToRad(14)); cam.position.set(0, midY, 0).addScaledVector(fwd, -d); cam.lookAt(0, midY, 0); },
    () => { const c = head.clone().add(new THREE.Vector3(0, 0.07, 0)); cam.position.copy(c).addScaledVector(fwd, 0.62); cam.lookAt(c); },
  ];
  const out = document.createElement('canvas');
  out.width = W * setups.length; out.height = H;
  const g = out.getContext('2d');
  const px = new Uint8Array(W * H * 4);
  const prevClear = renderer.getClearAlpha();
  setups.forEach((setup, i) => {
    setup();
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
  renderer.setClearColor(0x000000, prevClear);
  rt.dispose();
  return out.toDataURL('image/jpeg', 0.86);
}

// Переодеться: вспышка «хэнсин» прикрывает перезагрузку страницы с новым нарядом (хозяин помнит его в klava.json).
async function changeOutfit(name) {
  if (name === outfitName) return;
  // свой наряд мог появиться только что (она сшила его инструментом wardrobe) — проверяем по свежему config.json
  let fresh = rawCfg;
  try { fresh = JSON.parse(await fetch('./config.json', { cache: 'no-store' }).then((r) => r.text())); } catch { /* пишется — берём старый */ }
  if (!hasOutfit(fresh, name)) return;
  send({ type: 'played', name: `наряд ${name}` });
  await henshin('in');
  const q = new URLSearchParams(location.search);
  q.set('outfit', name);
  q.set('henshin', '1');
  q.set('v', String(Date.now()));
  location.replace(`${location.pathname}?${q}`);
}
if (pageParams.get('henshin')) henshin('out', 700);
// Правки из редактора подхватываются сами: config.json изменился — пересобраться (опрос раз в 2,5 с)
const sansCraft = (text) => { try { const o = JSON.parse(text); delete o.craft; return JSON.stringify(o); } catch { return text; } };
setInterval(async () => {
  try {
    const t = await fetch('./config.json', { cache: 'no-store' }).then((r) => r.text());
    // самодельные вещи (раздел craft) приходят сообщениями и уже на ней — ради них страницу не перезагружаем
    if (t && t !== cfgText && sansCraft(t) !== sansCraft(cfgText)) { await henshin('in', 400); location.reload(); }
  } catch { /* файл пишется — в следующий раз */ }
}, 2500);
const initGesture = urlParams.get('gesture');
if (initGesture) {
  const initFreeze = urlParams.get('freeze') !== null ? parseFloat(urlParams.get('freeze')) : undefined;
  playGesture(initGesture, { freeze: initFreeze });
}

frame();
setState('idle');
send({ type: 'ready', expressions: Object.keys(S.emotion).filter(hasExpr), outfit: outfitName, outfits: Object.keys(OUTFITS).concat(Object.keys(rawCfg.wardrobe?.custom ?? {})) });
