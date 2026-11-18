// Гардероб Клавы (друг 29.09: «чтобы она могла себе костюмчик подобрать и надеть что-то другое»). Что она сама хотела
// (журнал 28.09, разговор с Аксютой): готика — короткая чёрная юбка в складку, топ с открытыми плечами, чокер с
// колечком, чулки в сеточку; киберпанк — неоновый кроп-топ, кожаная косуха, легинсы с ремешками. Плюс классическое
// платье («немного классик вуман») и родной киберкостюм Виты.
//
// Наряд — это правка config.json на время загрузки (outfitConfig) и вещи, сшитые кодом (addGarments). Вещи устроены
// как верх кимоно: копия сетки тела на тех же костях, отступ по нормали; рисунок и вырезы считает шейдер по координатам
// тела в покое, поэтому ткань не плывёт при движении. Юбка — жёсткий конус на тазе, как юбка кимоно.
// Переодевание — страница перезагружается с ?outfit=имя (хозяин помнит наряд в klava.json), вспышка «хэнсин» прикрывает.
//
// 30.09 (друг: «ботинки новые нескольких типов, берет художника под кимоно, пусть делает себе вещи сама»): обувь —
// берцы, сапоги до колена, ботильоны (голенище — оболочка по ноге, стопа — оболочка по родной обуви, та прячется);
// шляпы — берет, ведьминская; перекраска частей наряда; свои наряды Клавы — config.json → wardrobe.custom (её
// инструмент wardrobe пишет их сам, страница перечитывает конфиг и переодевается).
import * as THREE from 'three';
import { restTransform } from './kimono.js';
import { atRest, holderAt } from './beauty.js';

// кожа Клавы (линейные) — ею перекрашиваются нарисованные на теле чулки/купальник там, где тело должно быть голым
const SKIN = [0.92, 0.72, 0.62];
const BLACK = [0.012, 0.011, 0.014];

/** Наряды: title — как называть вслух, words — как их могут назвать, patch — поверх config.json, garments — вещи. */
export const OUTFITS = {
  kimono: { title: 'кимоно', words: ['кимоно', 'халат', 'юката', 'обычн'], patch: {}, garments: [], hat: { kind: 'beret' } },
  gothic: {
    title: 'готика', words: ['гот', 'готик', 'готич', 'юбк', 'сеточк', 'чулк'],
    patch: { kimono: { enabled: false }, bodyPaint: { suit: SKIN, gloves: BLACK, stockings: SKIN }, beauty: { chokerStyle: 'ring' } },
    garments: ['offShoulder', 'pleatSkirt', 'fishnet'],
    shoes: { style: 'combat' },
  },
  // её хотелка 30.09: «шибари как доп. костюм — готика с верёвочками»: декоративная обвязка из красной верёвки поверх топа
  rope: {
    title: 'готика с верёвочками', words: ['верёвоч', 'веревоч', 'шибари', 'обвязк', 'портупе'],
    patch: { kimono: { enabled: false }, bodyPaint: { suit: SKIN, gloves: BLACK, stockings: SKIN }, beauty: { chokerStyle: 'ring' } },
    garments: ['offShoulder', 'pleatSkirt', 'fishnet', 'harness'],
    shoes: { style: 'combat' },
  },
  cyber: {
    title: 'кибер', words: ['кибер', 'киберпанк', 'косух', 'неон', 'легинс', 'кроп'],
    patch: { kimono: { enabled: false }, bodyPaint: { suit: SKIN, gloves: BLACK, stockings: BLACK }, beauty: { chokerStyle: 'ring' } },
    garments: ['cropTop', 'jacket', 'leggings'],
    shoes: { style: 'knee' },
  },
  classic: {
    title: 'классика', words: ['классик', 'платье', 'вечерн', 'элегант', 'нарядн'],
    patch: {
      kimono: {
        enabled: true, closure: 'wrap', open: 0, gap: 0.02, innerShirt: true, obiOn: true,
        top: [0.16, 0.008, 0.028], hem: [0.07, 0.003, 0.012], wave: [0.3, 0.02, 0.05], collar: [0.12, 0.006, 0.02],
        gold: [0.85, 0.62, 0.25], eri: [0.9, 0.86, 0.78], juban: [0.16, 0.008, 0.028], obi: [0.015, 0.012, 0.014],
        cord: [0.85, 0.62, 0.25], petal: [0.9, 0.86, 0.78], petalDeep: [0.5, 0.05, 0.1],
        waves: 0, sakura: 0, sleeveBell: 0.008, flare: 0.7, thickness: 0.01, obiH: 0.02, neckline: 0.1, innerShift: 0.02, hemAboveFoot: 0.09, slitW: 0.06,
      },
      beauty: { chokerStyle: 'pearls' },
    },
    garments: [],
    shoes: { style: 'ankle' },
  },
  native: {
    title: 'родное', words: ['родн', 'киберкостюм', 'как была', 'исходн'],
    patch: { kimono: { enabled: false }, bodyPaint: null, showMeshes: ['Bodybaked_4'], outfit: { tint: {}, gain: {} } },
    garments: [],
  },
  pajama: {
    title: 'пижамка', words: ['пижам', 'пижамк', 'дома', 'домашн', 'домаш', 'носочк', 'уютн', 'спать', 'тапочк'],
    patch: { kimono: { enabled: false }, bodyPaint: { suit: SKIN, gloves: SKIN, stockings: SKIN } },
    garments: ['pajamaTop', 'pajamaPants'],
    shoes: { style: 'slippers', color: '#e0d4f8', accent: '#c4aee8' },
  },
};

/** Обувь: стиль → название и цвета по умолчанию (основной, отделка — шнурки, кант, пряжка). native — родные ботинки. */
export const SHOES = {
  combat: { title: 'берцы на шнуровке', style: 0, color: '#0e0d10', accent: '#b01830' },
  knee: { title: 'лаковые сапоги до колена с неоновым кантом', style: 1, color: '#0a0a0e', accent: '#38e8ff' },
  ankle: { title: 'ботильоны на каблуке с пряжкой', style: 2, color: '#5c0f1d', accent: '#e6b24a' },
  slippers: { title: 'пушистые тапочки-лапки', style: 3, color: '#e0d4f8', accent: '#c4aee8' },
  native: { title: 'родные ботинки' },
};

/** Шляпы: вид → название и цвета по умолчанию. none — без шляпы. */
export const HATS = {
  beret: { title: 'берет художника', color: '#7a1c2c', accent: '#e6b24a' },
  witch: { title: 'ведьминская шляпа', color: '#1b1026', accent: '#a64dff' },
  none: { title: 'без шляпы' },
};

/** Вещи-слои, из которых она собирает свой наряд сама (custom.garments): по одной на место, из любых нарядов. */
export const GARMENTS = ['offShoulder', 'cropTop', 'pajamaTop', 'jacket', 'pleatSkirt', 'pajamaPants', 'fishnet', 'leggings', 'harness'];

/**
 * Что надето по имени: встроенный наряд или свой (cfg.wardrobe.custom[имя] = { base, title, garments?, shoes, hat, colors }).
 * Свой наряд из вещей (garments) — «топ от готики, косуха и легинсы от кибера»: основа даёт только кожу/перчатки/чокер.
 * Поверх встроенного — сменённые обувь и шляпа (cfg.wardrobe.shoes[имя], cfg.wardrobe.hat[имя]).
 */
export function lookOf(cfg, name) {
  const w = cfg?.wardrobe ?? {};
  const custom = w.custom?.[name];
  const baseName = custom ? (OUTFITS[custom.base] ? custom.base : 'kimono') : (OUTFITS[name] ? name : 'kimono');
  const base = OUTFITS[baseName];
  // свои вещи: готовые слои (GARMENTS) и сшитые ею самой «sewn:имя» (wardrobe.sewn)
  const own = Array.isArray(custom?.garments)
    ? custom.garments.filter((g) => GARMENTS.includes(g) || (typeof g === 'string' && g.startsWith('sewn:') && w.sewn?.[g.slice(5)])) : null;
  return {
    name, baseName, base, custom: custom ?? null,
    garments: own && own.length ? own : base.garments,
    shoes: custom?.shoes ?? w.shoes?.[name] ?? base.shoes ?? null,
    hat: custom?.hat ?? w.hat?.[name] ?? base.hat ?? null,
    colors: custom?.colors ?? {},
  };
}

/** Есть ли такой наряд (встроенный или свой). */
export function hasOutfit(cfg, name) { return !!(OUTFITS[name] || cfg?.wardrobe?.custom?.[name]); }

/** Наряд по словам («в готику», «косуху», «платье») → имя или null. */
export function outfitByWords(text) {
  const t = (text ?? '').toLowerCase();
  if (OUTFITS[t]) return t;
  for (const [name, o] of Object.entries(OUTFITS)) if (o.words.some((w) => t.includes(w))) return name;
  return null;
}

function merge(a, b) {
  if (b === null) return null;
  if (typeof b !== 'object' || Array.isArray(b)) return b;
  const out = { ...(typeof a === 'object' && a && !Array.isArray(a) ? a : {}) };
  for (const [k, v] of Object.entries(b)) out[k] = merge(out[k], v);
  return out;
}

/** config.json с нарядом поверх (сам config.json не меняется). */
export function outfitConfig(cfg, name) {
  const baseName = lookOf(cfg, name).baseName;
  const o = OUTFITS[baseName];
  if (!o || baseName === 'kimono') return { ...cfg, outfitName: name };
  const out = merge(cfg, o.patch);
  if (o.patch.kimono && o.patch.kimono.enabled !== false) out.kimono = { ...cfg.kimono, ...o.patch.kimono };
  const show = new Set(o.patch.showMeshes ?? []);
  out.hideMeshes = (cfg.hideMeshes ?? []).filter((m) => !show.has(m));
  if (out.kimono) out.kimono.hideMeshes = (cfg.kimono?.hideMeshes ?? []).filter((m) => !show.has(m));
  out.outfitName = name;
  return out;
}

// ---------- ткань по сетке тела ----------

const GLSL_COMMON = `
varying vec3 vRest;
varying vec3 vRN;
uniform float uNeckY, uChestY, uSpineY, uHipsY, uShX, uElbowX, uHandX, uKneeY, uAnkleY, uTime;
uniform vec3 uRecolor;
uniform float uRecolorOn;
float gHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
// Сглаживание рисунка по размеру пикселя (друг 02.10: «вечно эти полосы глитчевые»): кант ремешка в 2 мм, молния в 5 мм,
// сетка в 1 см — меньше пикселя на её экране, и в движении они рассыпались мерцающим пунктиром.
// gPx — сколько метров тела в одном пикселе экрана.
float gPx(vec3 p) { return max(length(fwidth(p)), 1e-5); }
// полоса |d| < w с мягким краем; тоньше пикселя — шире, но бледнее, без пунктира
float gBand(float d, float w, float px) { float ww = max(w, px * 0.75); return (1.0 - smoothstep(ww - px * 0.5, ww + px * 0.5, d)) * (w / ww); }
// мелкий узор (период меньше ~3 пикселей) уходит в среднее: 0 — только среднее, 1 — узор как есть
float gFade(float period, float px) { return clamp((period / px - 2.0) / 3.0, 0.0, 1.0); }
// ровный по экрану порог для «сита» (прозрачная ткань, сетка): не мерцает, как шум по телу
float gDither(vec2 fc) { return fract(52.9829189 * fract(0.06711056 * fc.x + 0.00583715 * fc.y)); }
`;

function garmentMaterial(key, glsl, uniforms, gradientMap, recolor = null, own = {}) {
  const mat = new THREE.MeshToonMaterial({ color: 0xffffff, gradientMap, side: THREE.DoubleSide });
  const rc = { uRecolor: { value: recolor ? rgb(recolor) : new THREE.Vector3(1, 1, 1) }, uRecolorOn: { value: recolor ? 1 : 0 } };
  mat.onBeforeCompile = (sh) => {
    Object.assign(sh.uniforms, uniforms, rc, own);
    sh.vertexShader = sh.vertexShader
      .replace('#include <common>', '#include <common>\nattribute vec3 restPos;\nattribute vec3 restNrm;\nvarying vec3 vRest;\nvarying vec3 vRN;')
      .replace('#include <begin_vertex>', '#include <begin_vertex>\nvRest = restPos;\nvRN = restNrm;');
    sh.fragmentShader = sh.fragmentShader
      .replace('#include <common>', `#include <common>\n${GLSL_COMMON}\n${glsl}`)
      .replace('vec3 totalEmissiveRadiance = emissive;', 'vec3 totalEmissiveRadiance = emissive;\nvec3 gEmit = vec3(0.0);')
      .replace('#include <color_fragment>', `#include <color_fragment>
{
  vec4 g = garment(vRest, normalize(vRN), gEmit);
  if (g.a < 0.5) discard;
  // своя расцветка: узор и блики те же (по яркости), цвет — новый; неон (emit) не трогается
  if (uRecolorOn > 0.5) {
    float lum = dot(g.rgb, vec3(0.299, 0.587, 0.114));
    g.rgb = uRecolor * clamp(lum * 1.05, 0.25, 1.05);
  }
  diffuseColor.rgb *= g.rgb;
}`)
      .replace('#include <emissivemap_fragment>', '#include <emissivemap_fragment>\ntotalEmissiveRadiance += gEmit;');
  };
  mat.customProgramCacheKey = () => `klava-garment-${key}-3`;
  return mat;
}

/** Мерки тела в покое (мир). */
function measure(vrm) {
  vrm.scene.updateMatrixWorld(true);
  const b = (n) => vrm.humanoid.getRawBoneNode(n);
  const wp = (n) => b(n)?.getWorldPosition(new THREE.Vector3());
  return {
    neckY: wp('neck').y, chestY: (wp('upperChest') ?? wp('chest')).y, spineY: wp('spine').y, hipsY: wp('hips').y,
    shX: Math.abs(wp('leftUpperArm').x), elbowX: Math.abs(wp('leftLowerArm').x), handX: Math.abs(wp('leftHand').x),
    kneeY: wp('leftLowerLeg').y, ankleY: wp('leftFoot').y, legX: Math.abs(wp('leftUpperLeg').x),
    kneeX: Math.abs(wp('leftLowerLeg').x), ankleX: Math.abs(wp('leftFoot').x),
  };
}

/**
 * Одёжка-оболочка: треугольники тела, у которых все вершины прошли keep(p, n), отодвинутые по нормали на off(p, n).
 * p, n — положение и нормаль вершины в покое (мир). Сгибается с костями, как верх кимоно.
 */
function shell(vrm, body, keep, off, mat, name) {
  const geo = body.geometry;
  const P = geo.attributes.position, N = geo.attributes.normal;
  const toWorld = restTransform(body);
  const rest = new Float32Array(P.count * 3), restN = new Float32Array(P.count * 3);
  const v = new THREE.Vector3(), nm = new THREE.Matrix3(), p = new THREE.Vector3(), n = new THREE.Vector3();
  const ok = new Uint8Array(P.count);
  const pos = new Float32Array(P.count * 3);
  for (let i = 0; i < P.count; i++) {
    const m = toWorld(i);
    p.fromBufferAttribute(P, i).applyMatrix4(m);
    n.fromBufferAttribute(N, i).applyMatrix3(nm.setFromMatrix4(m)).normalize();
    rest.set([p.x, p.y, p.z], i * 3);
    restN.set([n.x, n.y, n.z], i * 3);
    ok[i] = keep(p, n) ? 1 : 0;
    const d = ok[i] ? off(p, n) : 0;
    v.fromBufferAttribute(N, i);
    pos[i * 3] = P.getX(i) + v.x * d;
    pos[i * 3 + 1] = P.getY(i) + v.y * d;
    pos[i * 3 + 2] = P.getZ(i) + v.z * d;
  }
  const src = geo.index.array, idx = [];
  const g0 = geo.groups[0] ?? { start: 0, count: src.length };
  for (let t = g0.start; t < g0.start + g0.count; t += 3) {
    const a = src[t], b = src[t + 1], c = src[t + 2];
    if (ok[a] && ok[b] && ok[c]) idx.push(a, b, c);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  g.setAttribute('normal', N);
  g.setAttribute('skinIndex', geo.attributes.skinIndex);
  g.setAttribute('skinWeight', geo.attributes.skinWeight);
  g.setAttribute('restPos', new THREE.BufferAttribute(rest, 3));
  g.setAttribute('restNrm', new THREE.BufferAttribute(restN, 3));
  g.setIndex(idx);
  const mesh = new THREE.SkinnedMesh(g, mat);
  mesh.name = name;
  mesh.position.copy(body.position);
  mesh.quaternion.copy(body.quaternion);
  mesh.scale.copy(body.scale);
  body.parent.add(mesh);
  mesh.bind(body.skeleton, body.bindMatrix);
  mesh.frustumCulled = false;
  return mesh;
}

// ---------- вещи ----------

// Топ с открытыми плечами: чёрный трикотаж в рубчик, по верхнему краю — белое кружево фестонами, низ — фестоны.
// Полоса на плечах рук (в T-позе руки лежат вдоль X) — «спущенные рукава», сами плечи голые.
const OFF_SHOULDER = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float ax = abs(p.x);
  bool arm = ax > uShX + 0.005;
  float rib = mix(0.5, step(0.5, fract(ax * 180.0 + p.z * 60.0)), gFade(1.0 / 180.0, gPx(p)));
  vec3 c = vec3(0.03, 0.028, 0.034) * (0.85 + 0.15 * rib);   // рубчик (мелкий — средним тоном, без ряби)
  if (!arm) {
    float top = uChestY + 0.035;
    float scal = 0.004 * (1.0 - abs(sin((p.x + p.z) * 140.0)));
    if (p.y > top + scal) return vec4(0.0);
    if (p.y > top - 0.013) c = vec3(0.94, 0.92, 0.95) * (0.8 + 0.2 * step(0.5, gHash(floor(p.xz * 900.0))));
    float bot = uSpineY + 0.02 - 0.004 * abs(sin((p.x + p.z) * 110.0));
    if (p.y < bot) return vec4(0.0);
  } else {
    float a0 = uShX + 0.035, a1 = uShX + 0.11;
    if (ax < a0 || ax > a1) return vec4(0.0);
    if (ax < a0 + 0.012 || ax > a1 - 0.01) c = vec3(0.94, 0.92, 0.95);
  }
  return vec4(c, 1.0);
}`;

// Сетка-чулки: ромбы из тонких чёрных нитей, дырки прозрачные; сверху кружевная резинка.
const FISHNET = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float band = uHipsY - 0.12;
  if (p.y > band + 0.02) return vec4(0.0);
  if (p.y > band) return vec4(vec3(0.02) + 0.03 * step(0.5, fract((p.x + p.z) * 160.0)), 1.0);
  float s = 0.011;
  float a = abs(fract((p.y + p.x + p.z) / s) - 0.5), b = abs(fract((p.y - p.x - p.z) / s) - 0.5);
  float line = min(a, b);
  // нить в 2 мм тоньше пикселя: вблизи — ромбы как есть, издалека — ровный прозрачный капрон (сито по экрану), без ряби
  float px = gPx(p), f = gFade(s, px);
  float cover = mix(0.36, 1.0 - smoothstep(0.09 - px / s, 0.09 + px / s, line), f);
  if (gDither(gl_FragCoord.xy) > cover) return vec4(0.0);
  return vec4(vec3(0.015), 1.0);
}`;

// Кроп-топ: чёрный, неоновая голубая окантовка сверху и снизу, светящиеся кошачьи ушки на груди, лямки.
const CROP_TOP = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float ax = abs(p.x);
  float bot = uChestY - 0.07, top = uChestY + 0.045;
  bool strap = p.y > top && ax > 0.03 && ax < 0.058;
  if (p.y < bot || (p.y > top && !strap)) return vec4(0.0);
  vec3 c = vec3(0.025, 0.025, 0.03);
  float pulse = 0.75 + 0.25 * sin(uTime * 2.2);
  if (p.y < bot + 0.006 || (p.y > top - 0.006 && !strap) || (strap && (ax < 0.034 || ax > 0.054))) {
    c = vec3(0.3, 0.95, 1.0); emit += vec3(0.1, 0.75, 1.0) * pulse;
  }
  // значок: два треугольничка-ушка на груди спереди
  if (n.z > 0.3) {
    vec2 q = vec2(ax - 0.022, p.y - (uChestY - 0.035));
    if (q.y > 0.0 && q.y < 0.014 && abs(q.x) < 0.008 * (1.0 - q.y / 0.014)) { c = vec3(1.0, 0.4, 0.8); emit += vec3(1.0, 0.25, 0.7) * pulse; }
  }
  return vec4(c, 1.0);
}`;

// Косуха: чёрная кожа с отблеском, распахнута спереди, серебряные молнии по краям полочек и на рукавах, заклёпки на
// плечах, по манжетам и низу — неоновая полоса.
const JACKET = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float ax = abs(p.x);
  bool arm = ax > uShX + 0.01;
  bool front = n.z > 0.2;
  float bot = uSpineY - 0.005;
  if (!arm && p.y < bot) return vec4(0.0);
  if (!arm && ax < 0.075 && p.y > uNeckY - 0.02) return vec4(0.0);          // ворот
  float gap = mix(0.035, 0.07, clamp((p.y - bot) / max(uNeckY - bot, 0.01), 0.0, 1.0));
  if (!arm && front && ax < gap) return vec4(0.0);                              // распахнута
  float sheen = pow(max(dot(n, normalize(vec3(0.35, 0.8, 0.5))), 0.0), 12.0);
  vec3 c = vec3(0.03, 0.03, 0.036) + vec3(0.55, 0.6, 0.7) * sheen * 0.5;
  float pulse = 0.75 + 0.25 * sin(uTime * 2.2 + 1.0);
  // молнии по краю полочек
  if (!arm && front && ax < gap + 0.006) c = vec3(0.75, 0.78, 0.82) * (0.7 + 0.3 * mix(0.5, step(0.5, fract(p.y / 0.005)), gFade(0.005, gPx(p))));
  // низ и манжеты — неон
  if (!arm && p.y < bot + 0.007) { c = vec3(0.3, 0.95, 1.0); emit += vec3(0.1, 0.75, 1.0) * pulse; }
  if (arm && ax > uHandX - 0.05) {
    if (ax > uHandX - 0.035) return vec4(0.0);
    c = vec3(0.3, 0.95, 1.0); emit += vec3(0.1, 0.75, 1.0) * pulse;
  }
  // молния на рукаве сверху
  if (arm && n.y > 0.6 && abs(p.z) < 0.004 && ax > uElbowX) c = vec3(0.75, 0.78, 0.82);
  // заклёпки — ряд по верху плеча
  if (n.y > 0.5 && ax > uShX - 0.035 && ax < uShX + 0.045 && abs(p.z - 0.0) < 0.025) {
    vec2 g = fract(vec2(p.x / 0.011, p.z / 0.011)) - 0.5;
    if (length(g) < 0.2) c = vec3(0.85, 0.87, 0.9);
  }
  return vec4(c, 1.0);
}`;

// Легинсы: чёрные глянцевые, на каждом бедре по два ремешка с серебряной пряжкой снаружи и неоновым кантом.
const LEGGINGS = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  if (p.y > uSpineY - 0.02 || p.y < uAnkleY - 0.015) return vec4(0.0);
  float sheen = pow(max(dot(n, normalize(vec3(0.3, 0.6, 0.75))), 0.0), 16.0);
  vec3 c = vec3(0.018, 0.018, 0.022) + vec3(0.5, 0.55, 0.65) * sheen * 0.45;
  float pulse = 0.75 + 0.25 * sin(uTime * 2.2 + 2.0);
  float px = gPx(p);
  for (int k = 0; k < 2; k++) {
    float y0 = uHipsY - 0.11 - float(k) * 0.085;
    float d = abs(p.y - y0);
    float band = gBand(d, 0.0075, px);                              // ремешок
    float edge = band * (1.0 - gBand(d, 0.0058, px));               // неоновый кант по его краям
    float buckle = gBand(d, 0.0068, px) * step(0.02, abs(p.x)) * smoothstep(0.7, 0.8, n.x * sign(p.x));
    c = mix(c, vec3(0.09, 0.09, 0.1), band);
    c = mix(c, vec3(0.3, 0.95, 1.0), edge);
    emit += vec3(0.1, 0.7, 1.0) * pulse * 0.8 * edge;
    c = mix(c, vec3(0.8, 0.82, 0.86), buckle);                      // пряжка сбоку
  }
  return vec4(c, 1.0);
}`;

// Пижамный кардиган с рюшечками и пуговичками: нежный сиренево-лавандовый трикотаж,
// планка с перламутровыми пуговичками по центру, рюшечки по горловине, подолу и манжетам.
const PAJAMA_TOP = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float ax = abs(p.x);
  float bot = uHipsY - 0.052;
  // тело: от низа бедер до шеи; рукав: свободный крой ниже локтя
  bool body = ax < uShX + 0.012 && p.y > bot && p.y < uNeckY;
  bool sleeve = ax >= uShX - 0.015 && ax < uElbowX + 0.026 && p.y > uChestY - 0.095 && p.y < uChestY + 0.08;
  if (!body && !sleeve) return vec4(0.0);

  // V-образный вырез горловины кардигана
  float vCut = clamp((p.y - (uNeckY - 0.075)) / 0.075, 0.0, 1.0);
  float cutWidth = 0.048 * vCut;
  if (body && n.z > 0.15 && p.y > uNeckY - 0.075 && ax < cutWidth) return vec4(0.0);

  // мягкая трикотажная текстура кардигана: нежный лавандовый мохер (вертикальный рубчик + микропушок)
  float rib = cos(p.x * 320.0) * 0.06;
  float fuzz = gHash(floor(p.xz * 165.0));
  vec3 base = vec3(0.76, 0.66, 0.93) * (0.87 + rib + 0.13 * fuzz);
  vec3 c = base;

  // рюшечки: нежные волнистые воланы со складочками (светло-кремовые)
  vec3 ruffleCol = vec3(0.97, 0.95, 0.98);

  // 1) Рюшечки по горловине / вырезу
  if (body && p.y > uNeckY - 0.082 && n.z > 0.1) {
    float dNeck = abs(ax - cutWidth);
    if (dNeck < 0.011) {
      float rflWave = sin(p.y * 360.0);
      c = ruffleCol * (0.86 + 0.14 * rflWave);
    }
  }

  // 2) Рюшечки по подолу кардигана (нижний край)
  if (body && p.y < bot + 0.014) {
    float rflHem = sin(p.x * 260.0);
    c = ruffleCol * (0.88 + 0.12 * rflHem);
  }

  // 3) Рюшечки по манжетам рукавов
  if (sleeve && ax > uElbowX + 0.012) {
    float rflCuff = sin(p.y * 240.0 + p.z * 180.0);
    c = ruffleCol * (0.88 + 0.12 * rflCuff);
  }

  // Центральная планка с пуговичками (спереди)
  if (body && n.z > 0.28 && ax < 0.013 && p.y <= uNeckY - 0.075 && p.y >= bot + 0.014) {
    // планка кардигана
    c = mix(base, vec3(0.88, 0.82, 0.96), 0.65) * (0.92 + 0.08 * step(0.5, fract(p.y * 180.0)));
    // тонкий центральный шов соединения
    if (ax < 0.0016) c *= 0.72;

    // Пуговички: 4 аккуратные перламутровые пуговицы
    float yStart = uChestY - 0.012;
    float yStep = 0.040;
    for (int k = 0; k < 4; k++) {
      float by = yStart - float(k) * yStep;
      if (by > bot + 0.020) {
        vec2 bq = vec2(p.x, p.y - by);
        float bd = length(bq);
        if (bd < 0.0058) {
          // перламутровая пуговичка с мягким бликом
          c = vec3(0.96, 0.95, 0.93) * (0.90 + 0.10 * step(0.0, bq.y));
          if (bd > 0.0044) c = vec3(0.78, 0.72, 0.85); // кант
          // 4 дырочки для ниток
          vec2 holes = abs(bq);
          if (holes.x < 0.0022 && holes.y < 0.0022 && length(holes - vec2(0.0011, 0.0011)) < 0.00075)
            c = vec3(0.42, 0.35, 0.50);
        }
      }
    }
  }

  // Накладной кармашек с выглядывающими ушками котика (слева на груди)
  if (body && n.z > 0.32 && !sleeve) {
    if (p.x < -0.016 && p.x > -0.046 && p.y > uChestY - 0.065 && p.y < uChestY - 0.022) {
      // полотно кармашка
      c = vec3(0.82, 0.72, 0.96) * (0.90 + 0.10 * gHash(floor(p.xy * 190.0)));
      // кант и верхний край кармана
      if (p.y > uChestY - 0.026 || p.x < -0.043 || p.x > -0.019 || p.y < uChestY - 0.062)
        c = vec3(0.95, 0.92, 0.98); // белая окантовочка кармашка
      // мордочка белого котика, выглядывающая из кармашка
      vec2 catPos = vec2(p.x - (-0.031), p.y - (uChestY - 0.020));
      if (length(catPos) < 0.011 && catPos.y >= -0.004) {
        c = vec3(0.98, 0.97, 0.96); // белый котик
        if (abs(catPos.x) < 0.0018 && abs(catPos.y - 0.001) < 0.0014) c = vec3(0.94, 0.55, 0.65); // розовый носик
      }
      // ушки котика над кармашком
      if (catPos.y > 0.005 && catPos.y < 0.014 && abs(catPos.x) > 0.003 && abs(catPos.x) < 0.009)
        c = vec3(0.98, 0.97, 0.96);
    }
  }

  // нежные пастельные звездочки по кардигану
  float sh = gHash(floor(vec2(p.x + p.z, p.y) * 82.0));
  if (sh > 0.925 && (body || sleeve)) {
    vec2 gv = fract(vec2((p.x + p.z) * 82.0, p.y * 82.0)) - 0.5;
    float star = 1.0 - smoothstep(0.06, 0.18, length(gv));
    if (star > 0.1) c = mix(c, vec3(0.98, 0.96, 0.80), star * 0.85);
  }

  return vec4(c, 1.0);
}`;

// Пижамные пушистые прямые штаны: не в обтяжку по ногам, свободный прямой крой до тапочек,
// бархатистый пушистый флис (fuzz + peach rim), мягкий эластичный пояс, нежные пастельные облачка.
const PAJAMA_PANTS = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  if (p.y > uHipsY + 0.008 || p.y < uAnkleY + 0.012) return vec4(0.0);

  // пушистая фактура ткани: многослойный микроворс + бархатистый пушок по контуру (velvet peach-fuzz)
  float fz1 = gHash(floor(p.xz * 210.0));
  float fz2 = gHash(floor(p.xy * 350.0));
  float fuzz = 0.6 * fz1 + 0.4 * fz2;

  float rim = clamp(1.0 - abs(dot(n, vec3(0.0, 0.0, 1.0))), 0.0, 1.0);
  float fluff = rim * rim * (0.8 + 0.2 * fuzz);

  vec3 base = vec3(0.82, 0.73, 0.96) * (0.86 + 0.14 * fuzz) + vec3(0.11, 0.07, 0.15) * fluff;
  vec3 c = base;

  // эластичный пояс на талии (мягкий рубчик)
  float wb = abs(p.y - (uHipsY - 0.018));
  if (wb < 0.018) {
    c = vec3(0.68, 0.52, 0.88) * (0.82 + 0.18 * step(0.5, fract(p.y * 280.0)));
  } else if (p.y < uAnkleY + 0.028) {
    // прямой свободный низ штанин (без утягивающих манжет! прямая пушистая подгибка)
    c = mix(c, vec3(0.92, 0.88, 0.98), 0.35 * (0.8 + 0.2 * fuzz));
  } else {
    // мягкие пушистые белые облачка по ткани
    vec2 gp = vec2(p.x * 46.0 + p.z * 24.0, p.y * 32.0);
    float cloud = 0.0;
    for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++) {
      vec2 cell = floor(gp) + vec2(float(i), float(j));
      if (gHash(cell) > 0.80) {
        vec2 cv = fract(gp) - (vec2(float(i), float(j)) + 0.5);
        cloud = max(cloud, smoothstep(0.42, 0.12, length(cv)));
      }
    }
    c = mix(c, vec3(0.98, 0.97, 1.00), cloud * 0.75);

    // маленькие редкие мерцающие звездочки между облачками
    float st = gHash(floor(vec2(p.x * 70.0 + p.z * 35.0, p.y * 70.0)));
    if (st > 0.96) {
      c = mix(c, vec3(1.0, 0.98, 0.82), 0.8);
    }
  }

  return vec4(c, 1.0);
}`;

// Обувь (стиль 0 — берцы: матовая кожа, шнуровка крест-накрест с люверсами, мягкий ворот; 1 — лаковые сапоги до
// колена: блик, неоновый кант снаружи и по верху, платформа; 2 — ботильоны: гладкая кожа, ремешок с пряжкой, каблук
// темнее). Середина ноги на высоте — от лодыжки к колену; цвета — uBootColor (основной) и uBootAccent (отделка).
const BOOTS = `
uniform float uBootStyle, uBootTop, uKneeX, uAnkleX;
uniform vec3 uBootColor, uBootAccent;
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  if (p.y > uBootTop) return vec4(0.0);
  float side = p.x >= 0.0 ? 1.0 : -1.0;
  float t = clamp((p.y - uAnkleY) / max(uKneeY - uAnkleY, 0.01), 0.0, 1.0);
  float dx = p.x - side * mix(uAnkleX, uKneeX, t);
  bool front = n.z > 0.35;
  int st = int(uBootStyle + 0.5);
  // стиль 3 — домашние тапочки-лапки: пушистые, мягкие, с лапкой-подушечкой на носке
  if (st == 3) {
    float fuzz = gHash(floor(p.xz * 94.0));
    vec3 c = uBootColor * (0.78 + 0.32 * fuzz);
    // резинка-кант сверху тапочки
    if (p.y > uBootTop - 0.016)
      c = uBootColor * (0.55 + 0.45 * step(0.5, fract(p.y * 240.0)));
    // подушечка-лапка на носке спереди: большой круг + четыре пальчика
    if (front && p.y < 0.045) {
      float ax2 = abs(p.x);
      float side2 = p.x >= 0.0 ? 1.0 : -1.0;
      vec2 q = vec2(p.x - side2 * 0.018, p.y - 0.020);  // центр большой подушечки
      float d = length(q);
      if (d < 0.018) c = uBootAccent * (0.9 + 0.1 * fuzz);
      // четыре маленьких пальчика выше
      for (int k = 0; k < 4; k++) {
        vec2 fp = vec2(p.x - side2 * (0.004 + float(k) * 0.008 - 0.012), p.y - 0.038);
        if (length(fp) < 0.0075) c = uBootAccent * (0.9 + 0.1 * fuzz);
      }
    }
    return vec4(c, 1.0);
  }
  float sheen = pow(max(dot(n, normalize(vec3(0.3, 0.7, 0.6))), 0.0), 14.0);
  vec3 c = uBootColor;
  if (st == 1) c += vec3(0.55, 0.6, 0.7) * sheen * 0.55;
  else if (st == 2) c += vec3(0.45) * sheen * 0.3;
  else c *= 0.88 + 0.12 * step(0.5, gHash(floor(p.xy * 650.0)));
  float pulse = 0.8 + 0.2 * sin(uTime * 2.0);
  if (p.y < 0.022) {
    c = st == 1 ? uBootColor * 1.6 : vec3(0.09, 0.09, 0.1) * (0.8 + 0.2 * step(0.5, fract(p.z * 90.0)));
    if (st == 1 && p.y > 0.018) { c = uBootAccent; emit += uBootAccent * 0.7 * pulse; }
  }
  if (p.y > uBootTop - 0.012) {
    c = st == 0 ? uBootColor * 0.55 + 0.02 : uBootAccent;
    if (st == 1) emit += uBootAccent * 0.8 * pulse;
  }
  if (st == 0 && front && p.y > 0.045 && p.y < uBootTop - 0.016 && abs(dx) < 0.018) {
    float yy = fract(p.y / 0.016), lx = abs(dx) / 0.014;
    if (abs(lx - abs(yy * 2.0 - 1.0)) < 0.2) c = uBootAccent;
    if (abs(lx - 1.05) < 0.22 && abs(fract(p.y / 0.016 + 0.5) - 0.5) < 0.13) c = vec3(0.78, 0.8, 0.84);
  }
  if (st == 1 && n.x * side > 0.62 && p.y > 0.022 && p.y < uBootTop - 0.012) { c = uBootAccent; emit += uBootAccent * 0.55 * pulse; }
  if (st == 2) {
    float strap = abs(p.y - (uAnkleY + 0.032));
    if (strap < 0.0065) c = uBootColor * 0.5;
    if (strap < 0.0065 && n.x * side > 0.7) c = uBootAccent;
    if (n.z < -0.45 && p.y < 0.065) c *= 0.65;
  }
  return vec4(c, 1.0);
}`;

// Обвязка «готика с верёвочками» (её хотелка): красная кручёная верёвка поверх топа — под грудью и по талии, спереди
// ромб и середина, лямки через плечи сходятся к шее; на пересечениях — узелки.
const HARNESS = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float ax = abs(p.x);
  float y0 = uSpineY + 0.03, y1 = uChestY - 0.05, ym = (y0 + y1) * 0.5, h = max((y1 - y0) * 0.5, 0.01);
  float r = 0.0045, d = 1.0;
  d = min(d, abs(p.y - y1));
  d = min(d, abs(p.y - y0));
  if (n.z > 0.0 && p.y > y0 - 0.06 && p.y < y1) {
    d = min(d, ax);
    if (p.y > y0) d = min(d, abs(ax - 0.062 * (1.0 - abs(p.y - ym) / h)));
  }
  if (p.y > y1 && p.y < uNeckY - 0.01) {
    float k = clamp((p.y - y1) / max(uNeckY - y1, 0.01), 0.0, 1.0);
    d = min(d, abs(ax - mix(0.078, 0.045, k)));
  }
  float knot = min(length(vec2(ax, p.y - y1)), length(vec2(ax, p.y - y0)));
  if (d > r && !(n.z > 0.0 && knot < 0.009)) return vec4(0.0);
  float tw = step(0.5, fract((p.x * 0.7 + p.y * 1.7 + p.z) * 240.0));
  vec3 c = vec3(0.55, 0.04, 0.07) * (0.72 + 0.38 * tw) * (1.0 - 0.35 * clamp(d / r, 0.0, 1.0));
  if (n.z > 0.0 && knot < 0.009) c = vec3(0.5, 0.03, 0.06) * (0.8 + 0.3 * tw);
  return vec4(c, 1.0);
}`;

// Юбка в складку: чёрная, складки темнее в глубине, тонкая бордовая полоска у подола.
const PLEAT = `
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float a = atan(p.z, p.x);
  float k = fract(a * 26.0 / 6.2831853);
  vec3 c = vec3(0.035, 0.032, 0.04) * (0.75 + 0.35 * smoothstep(0.0, 0.5, k) * (1.0 - smoothstep(0.5, 1.0, k)));
  float hem = uHipsY - 0.2;
  if (p.y < hem + 0.012 && p.y > hem + 0.008) c = vec3(0.35, 0.02, 0.06);
  return vec4(c, 1.0);
}`;

/**
 * Юбка в складку: конус по обхвату таза, короткая; складки — зигзаг радиуса, к подолу раскрываются.
 * opts (своя юбка из швейной мастерской): hemY — подол, pleats — сколько складок (0 — гладкая), flare — расклёш к подолу, м.
 */
function pleatSkirt(vrm, body, M, mat, opts = {}) {
  const geo = body.geometry, P = geo.attributes.position;
  const toWorld = restTransform(body);
  const rest = new Float32Array(P.count * 3);
  const v = new THREE.Vector3();
  for (let i = 0; i < P.count; i++) { v.fromBufferAttribute(P, i).applyMatrix4(toWorld(i)); rest.set([v.x, v.y, v.z], i * 3); }
  const yTop = M.spineY + 0.005, hemY = opts.hemY ?? M.hipsY - 0.2;
  const PLEATS = opts.pleats ?? 26, FLARE = opts.flare ?? 0.05;
  const slab = (y0, y1) => {
    let mx = 0, zmin = 1e9, zmax = -1e9;
    for (let i = 0; i < P.count; i++) {
      const y = rest[i * 3 + 1], x = rest[i * 3];
      if (y < y0 || y > y1 || Math.abs(x) > 0.3) continue;
      mx = Math.max(mx, Math.abs(x)); zmin = Math.min(zmin, rest[i * 3 + 2]); zmax = Math.max(zmax, rest[i * 3 + 2]);
    }
    return mx > 0 ? { rx: mx, cz: (zmin + zmax) / 2, rz: (zmax - zmin) / 2 } : null;
  };
  const NJ = Math.max(12, Math.round((yTop - hemY) / 0.025)), NI = 208, PW = 3.0;
  const rings = [];
  let rX = 0, rZ = 0, cZ = 0;
  for (let j = 0; j <= NJ; j++) {
    const t = j / NJ, y = yTop + (hemY - yTop) * t;
    // длинная юбка: ниже колен обхват ног меньше бёдер — держим самый широкий обхват сверху (rX/rZ только растут)
    const e = slab(y - 0.02, y + 0.02);
    if (e) { rX = Math.max(rX, e.rx); rZ = Math.max(rZ, e.rz); cZ = j === 0 ? e.cz : cZ * 0.7 + e.cz * 0.3; }
    rings.push({ y, rx: rX + 0.012 + FLARE * t, rz: rZ + 0.016 + FLARE * 0.9 * t, cz: cZ, t });
  }
  const sp = [], sn = [], sIdx = [];
  for (let j = 0; j <= NJ; j++) for (let i = 0; i <= NI; i++) {
    const a = (i / NI) * Math.PI * 2, ca = Math.cos(a), sa = Math.sin(a);
    const r = rings[j];
    const tri = PLEATS > 0 ? Math.abs(((i / NI) * PLEATS) % 1 - 0.5) * 2 : 0.5;   // 0..1 зигзаг
    const k = 1 + (0.006 + 0.03 * r.t) * (tri - 0.5);
    const x = r.rx * k * Math.sign(ca) * Math.pow(Math.abs(ca), 2 / PW);
    const z = r.cz + r.rz * k * Math.sign(sa) * Math.pow(Math.abs(sa), 2 / PW);
    sp.push(x, r.y, z);
    sn.push(ca, 0, sa);
  }
  for (let j = 0; j < NJ; j++) for (let i = 0; i < NI; i++) {
    const a = j * (NI + 1) + i, b = a + 1, c = a + NI + 1, d = c + 1;
    sIdx.push(a, b, c, b, d, c);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(sp, 3));
  g.setIndex(sIdx);
  g.computeVertexNormals();
  g.setAttribute('restPos', new THREE.Float32BufferAttribute(sp.slice(), 3));
  g.setAttribute('restNrm', new THREE.Float32BufferAttribute(sn, 3));
  const skirt = new THREE.Mesh(g, mat);
  skirt.name = 'KlavaPleatSkirt';
  const hips = vrm.humanoid.getRawBoneNode('hips');
  hips.add(skirt);
  hips.updateMatrixWorld(true);
  new THREE.Matrix4().copy(hips.matrixWorld).invert().decompose(skirt.position, skirt.quaternion, skirt.scale);
  skirt.frustumCulled = false;
  return skirt;
}

// ---------- швейная мастерская: вещи её собственного кроя ----------
//
// Друг 02.10: «новую вещь она себе создать и сконструировать не может» — гардероб только перекрашивал готовое, а
// мастерская (craft.js) делает жёсткие предметы на кости. Здесь — одежда по её телу: что закрыть (торс от–до, рукава,
// штанины от–до или юбка до…), насколько свободно и расклёшено, цвет, второй цвет, узор, окантовка (можно неоном),
// блеск, прозрачность, вырез. Оболочка тела, как у готовых вещей, — гнётся с костями. Хранится в config.json
// wardrobe.sewn[имя]; в наряде — «sewn:имя».

/** Мерки для кроя: где по высоте (y, мир) и по руке (x) кончается вещь. */
function sewY(M, at) {
  switch (at) {
    case 'neck': return M.neckY + 0.02;
    case 'high': return M.neckY + 0.055;
    case 'chest': return M.chestY + 0.035;
    case 'underbust': return M.chestY - 0.07;
    case 'waist': return M.spineY;
    case 'hips': return M.hipsY;
    case 'crotch': return M.hipsY - 0.08;
    case 'thigh': return (M.hipsY + M.kneeY) / 2 - 0.03;
    case 'knee': return M.kneeY;
    case 'calf': return (M.kneeY + M.ankleY) / 2;
    case 'ankle': return M.ankleY + 0.02;
    case 'foot': return M.ankleY - 0.12;
    default: return null;
  }
}
function sewX(M, at) {
  switch (at) {
    case 'cap': return M.shX + 0.05;
    case 'elbow': return M.elbowX + 0.01;
    case 'forearm': return (M.elbowX + M.handX) / 2;
    case 'wrist': return M.handX - 0.015;
    default: return 0;
  }
}
export const SEW = {
  torsoFrom: ['neck', 'high', 'chest', 'underbust', 'waist'], torsoTo: ['underbust', 'waist', 'hips', 'crotch', 'thigh'],
  sleeves: ['none', 'cap', 'elbow', 'forearm', 'wrist'], legsFrom: ['waist', 'hips', 'crotch', 'thigh', 'knee', 'calf'],
  legsTo: ['crotch', 'thigh', 'knee', 'calf', 'ankle', 'foot'], skirtTo: ['crotch', 'thigh', 'knee', 'calf', 'ankle'],
  neckline: ['round', 'v', 'high', 'off', 'square'],
  pattern: ['solid', 'stripes', 'vstripes', 'plaid', 'dots', 'rib', 'fur', 'mesh', 'camo', 'hearts', 'checker'],
};

const SEWN = `
uniform vec3 uC1, uC2, uTrim;
uniform float uTop, uBot, uSleeve, uLegTop, uLegBot, uHasTorso, uHasLegs, uSkirt, uHem, uNeck, uPat, uScale, uTrimW, uTrimOn,
  uGlow, uShine, uOpacity, uArmY, uLegX, uTrimBot;
vec3 sewPattern(vec2 q, vec3 p) {
  float s = max(uScale, 0.003);
  int k = int(uPat + 0.5);
  float f = gFade(s, gPx(p));                       // мелкий узор издалека — средним тоном, без ряби
  vec3 mid = mix(uC1, uC2, 0.5);
  if (k == 1) return mix(mid, mix(uC1, uC2, step(0.5, fract(q.x / s))), f);
  if (k == 2) return mix(mid, mix(uC1, uC2, step(0.5, fract(q.y / s))), f);
  if (k == 3) {
    float a = step(0.5, fract(q.x / s)), b = step(0.5, fract(q.y / s));
    vec3 c = mix(uC1, uC2, clamp((a + b) * 0.5, 0.0, 1.0));
    float thin = step(0.93, fract(q.x / s * 2.0 + 0.25)) + step(0.93, fract(q.y / s * 2.0 + 0.25));
    return mix(mid, mix(c, uC2 * 0.55, clamp(thin, 0.0, 1.0) * 0.6), f);
  }
  if (k == 4) { vec2 g = fract(q / s) - 0.5; return length(g) < 0.22 ? uC2 : uC1; }
  if (k == 5) return uC1 * (0.8 + 0.2 * cos(q.y / s * 6.2831853));
  if (k == 6) { float h = gHash(floor(p.xy * 900.0) + floor(p.z * 900.0)); return mix(uC1, uC2, h * 0.4) * (0.78 + 0.3 * h); }
  if (k == 7) {
    float a = abs(fract((q.x + q.y) / s) - 0.5), b = abs(fract((q.x - q.y) / s) - 0.5);
    float cover = mix(0.35, 1.0 - step(0.1, min(a, b)), f);
    return gDither(gl_FragCoord.xy) > cover ? vec3(-1.0) : uC1;
  }
  if (k == 8) {
    float v = gHash(floor(q / (s * 1.7))) * 0.6 + gHash(floor(q / s + 0.5)) * 0.4;
    return v < 0.33 ? uC1 : v < 0.66 ? uC2 : mix(uC1, uC2, 0.5) * 0.65;
  }
  if (k == 9) {
    vec2 g = (fract(q / s) - 0.5) * 3.0; g.y = -g.y + 0.25;
    float x2 = g.x * g.x, y = g.y;
    float h = pow(x2 + y * y - 0.55, 3.0) - x2 * y * y * y;
    return h < 0.0 ? uC2 : uC1;
  }
  if (k == 10) return mix(mid, mod(floor(q.x / s) + floor(q.y / s), 2.0) < 0.5 ? uC1 : uC2, f);
  return uC1;
}
vec4 garment(vec3 p, vec3 n, inout vec3 emit) {
  float ax = abs(p.x);
  bool arm = uSkirt < 0.5 && ax > uShX + 0.01 && p.y > uChestY - 0.15;
  bool inTorso = uSkirt < 0.5 && uHasTorso > 0.5 && !arm && p.y <= uTop && p.y >= uBot;
  bool inSleeve = uSkirt < 0.5 && uHasTorso > 0.5 && arm && uSleeve > 0.0 && ax <= uSleeve;
  bool inLegs = uSkirt < 0.5 && uHasLegs > 0.5 && !arm && p.y <= uLegTop && p.y >= uLegBot;
  if (uSkirt < 0.5 && !(inTorso || inSleeve || inLegs)) return vec4(0.0);
  // вырез спереди: круглый, V, каре; «off» — плечи открыты (верх по груди уже срезан в uTop)
  bool front = n.z > 0.15;
  if (inTorso && front && p.y > uChestY - 0.06) {
    float d = p.y - (uChestY - 0.06);
    int nk = int(uNeck + 0.5);
    if (nk == 0 && ax < 0.055 && p.y > uTop - 0.03 * (1.0 - ax / 0.055)) return vec4(0.0);
    if (nk == 1 && ax < 0.075 * d / max(uTop - (uChestY - 0.06), 0.01)) return vec4(0.0);
    if (nk == 4 && ax < 0.06 && p.y > uChestY + 0.0) return vec4(0.0);
  }
  vec2 q;
  if (arm) q = vec2(ax, atan(p.y - uArmY, p.z) * 0.045);
  else if (uSkirt < 0.5 && p.y < uHipsY - 0.05) { float x0 = sign(p.x) * uLegX; q = vec2(p.y, atan(p.x - x0, p.z) * 0.075); }
  else q = vec2(p.y, atan(p.x, p.z) * 0.13);
  vec3 c = sewPattern(q, p);
  if (c.r < 0.0) return vec4(0.0);
  // окантовка по краям: горловина, низ торса, манжеты, низ штанин, пояс, подол юбки
  float e = 1e3;
  if (inTorso) { e = min(e, abs(p.y - uTop)); if (uTrimBot > 0.5) e = min(e, abs(p.y - uBot)); }
  if (inSleeve) e = min(e, abs(ax - uSleeve));
  if (inLegs) { e = min(e, abs(p.y - uLegBot)); if (uHasTorso < 0.5) e = min(e, abs(p.y - uLegTop)); }
  if (uSkirt > 0.5) e = min(e, abs(p.y - uHem));
  if (uTrimOn > 0.5 && e < uTrimW) {
    c = uTrim;
    if (uGlow > 0.5) emit += uTrim * (0.75 + 0.25 * sin(uTime * 2.2));
  }
  c += vec3(0.5, 0.55, 0.62) * pow(max(dot(n, normalize(vec3(0.3, 0.7, 0.6))), 0.0), 14.0) * uShine;
  // полупрозрачная ткань — сито точек: сквозь неё видно тело
  if (uOpacity < 0.99 && gDither(gl_FragCoord.xy) > uOpacity) return vec4(0.0);
  return vec4(c, 1.0);
}`;

/** Вещь своего кроя (spec из config.json wardrobe.sewn[имя]) → сетки по её телу. Непонятное — по умолчанию. */
function sewnGarment(vrm, body, M, spec, uniforms, gradientMap, name) {
  const pick = (v, list, d) => (list.includes(v) ? v : d);
  const cm = (v, a, b, d) => Math.min(b, Math.max(a, Number.isFinite(Number(v)) ? Number(v) : d)) / 100;
  const torso = spec.torso && typeof spec.torso === 'object' ? spec.torso : null;
  const legs = spec.legs && typeof spec.legs === 'object' ? spec.legs : null;
  const skirt = spec.skirt && typeof spec.skirt === 'object' ? spec.skirt : null;
  const neck = pick(spec.neckline, SEW.neckline, 'round');
  let top = torso ? sewY(M, pick(torso.from, SEW.torsoFrom, 'neck')) : 0;
  if (torso && neck === 'off') top = Math.min(top, M.chestY + 0.035);
  if (torso && neck === 'high') top = Math.max(top, M.neckY + 0.055);
  const bot = torso ? sewY(M, pick(torso.to, SEW.torsoTo, 'hips')) : 0;
  const sleeve = torso ? sewX(M, pick(spec.sleeves, SEW.sleeves, 'none')) : 0;
  const legTop = legs ? sewY(M, pick(legs.from, SEW.legsFrom, 'waist')) : 0;
  const legBot = legs ? sewY(M, pick(legs.to, SEW.legsTo, 'ankle')) : 0;
  const fit = cm(spec.fit, 0.15, 6, 0.6), flare = cm(spec.flare, 0, 12, 0);
  const own = {
    uC1: { value: rgb(spec.color ?? '#3a3a48') }, uC2: { value: rgb(spec.color2 ?? spec.color ?? '#e8e8ee') },
    uTrim: { value: rgb(spec.trim ?? '#ffffff') }, uTrimOn: { value: spec.trim ? 1 : 0 }, uTrimW: { value: cm(spec.trimWidth, 0.2, 5, 0.8) },
    uGlow: { value: spec.glow ? 1 : 0 }, uShine: { value: Math.min(1, Math.max(0, Number(spec.shine) || 0)) },
    uOpacity: { value: Math.min(1, Math.max(0.15, Number.isFinite(Number(spec.opacity)) ? Number(spec.opacity) : 1)) },
    uPat: { value: Math.max(0, SEW.pattern.indexOf(spec.pattern ?? 'solid')) }, uScale: { value: cm(spec.scale, 0.3, 30, 3) },
    uTop: { value: top }, uBot: { value: bot }, uSleeve: { value: sleeve }, uLegTop: { value: legTop }, uLegBot: { value: legBot },
    uHasTorso: { value: torso ? 1 : 0 }, uHasLegs: { value: legs ? 1 : 0 }, uSkirt: { value: 0 }, uHem: { value: 0 },
    uNeck: { value: SEW.neckline.indexOf(neck) }, uArmY: { value: vrm.humanoid.getRawBoneNode('leftUpperArm').getWorldPosition(new THREE.Vector3()).y },
    uLegX: { value: M.legX }, uTrimBot: { value: torso && legs && legTop >= bot - 0.01 ? 0 : 1 },
  };
  const key = `sewn-${name}`;
  const meshes = [];
  if (torso || legs) {
    const mat = garmentMaterial(key, SEWN, uniforms, gradientMap, null, own);
    const armOf = (p) => Math.abs(p.x) >= M.shX - 0.025 && p.y > M.chestY - 0.15;
    const keep = (p) => {
      const ax = Math.abs(p.x);
      if (torso && !armOf(p) && ax < M.shX + 0.02 && p.y <= top + 0.02 && p.y >= bot - 0.02 && p.y < M.neckY + 0.08) return true;
      if (torso && sleeve > 0 && armOf(p) && ax <= sleeve + 0.015) return true;
      if (legs && !(armOf(p) && ax > M.shX) && p.y <= legTop + 0.02 && p.y >= legBot - 0.02 && ax < 0.3 && p.y < M.chestY - 0.1) return true;
      return false;
    };
    // свободный крой — дальше от тела; расклёш растёт к краю (к подолу, манжете, низу штанины)
    const off = (p) => {
      const ax = Math.abs(p.x);
      let t = 0;
      if (torso && armOf(p) && sleeve > 0) t = Math.min(1, Math.max(0, (ax - M.shX) / Math.max(0.01, sleeve - M.shX)));
      else if (legs && p.y <= legTop) t = Math.min(1, Math.max(0, (legTop - p.y) / Math.max(0.01, legTop - legBot)));
      else if (torso) t = Math.min(1, Math.max(0, (top - p.y) / Math.max(0.01, top - bot)));
      return fit + flare * t * t;
    };
    meshes.push(shell(vrm, body, keep, off, mat, `KlavaSewn_${name}`));
  }
  if (skirt) {
    const hem = sewY(M, pick(skirt.to, SEW.skirtTo, 'thigh'));
    const sown = { ...own, uSkirt: { value: 1 }, uHem: { value: hem } };
    const mat = garmentMaterial(`${key}-skirt`, SEWN, uniforms, gradientMap, null, sown);
    const pleats = Math.round(Math.min(48, Math.max(0, Number.isFinite(Number(skirt.pleats)) ? Number(skirt.pleats) : 0)));
    const s = pleatSkirt(vrm, body, M, mat, { hemY: hem, pleats, flare: cm(skirt.flare, 0, 30, 5) });
    s.name = `KlavaSewnSkirt_${name}`;
    meshes.push(s);
  }
  return meshes;
}

/**
 * Сшить вещи наряда. Возвращает { skirt, meshes, update(dt) } — skirt отдаётся жестам (присесть/сесть), update крутит
 * время для неона.
 */
export function addGarments(vrm, name, { gradientMap, bodyMesh = 'Bodybaked', cfg = null } = {}) {
  const look = lookOf(cfg, name);
  const list = look.garments;
  const shoes = look.shoes && look.shoes.style !== 'native' && SHOES[look.shoes.style] ? look.shoes : null;
  const hat = look.hat && look.hat.kind !== 'none' && HATS[look.hat.kind] ? look.hat : null;
  const out = { skirt: null, meshes: [], hat: null, update() {} };
  if (!list.length && !shoes && !hat) return out;
  const body = vrm.scene.getObjectByName(bodyMesh);
  if (!body?.isSkinnedMesh) return out;
  const M = measure(vrm);
  const uniforms = {
    uNeckY: { value: M.neckY }, uChestY: { value: M.chestY }, uSpineY: { value: M.spineY }, uHipsY: { value: M.hipsY },
    uShX: { value: M.shX }, uElbowX: { value: M.elbowX }, uHandX: { value: M.handX }, uKneeY: { value: M.kneeY },
    uAnkleY: { value: M.ankleY }, uTime: { value: 0 },
  };
  // своя расцветка части наряда: верх, юбка, куртка, ноги, верёвка — из её наряда (colors) или никакой
  const part = { offsh: 'top', crop: 'top', jacket: 'jacket', pleat: 'skirt', leggings: 'legs', fishnet: 'legs', harness: 'harness', pajamaTop: 'top', pajamaPants: 'skirt' };
  const mk = (key, glsl) => garmentMaterial(key, glsl, uniforms, gradientMap, look.colors?.[part[key]] ?? null);
  const torso = (p) => Math.abs(p.x) < M.shX + 0.01 && p.y < M.neckY + 0.02 && !(Math.abs(p.x) < 0.075 && p.y > M.neckY - 0.03);
  const armZone = (p, x1) => Math.abs(p.x) >= M.shX - 0.02 && Math.abs(p.x) < x1 && p.y > M.chestY - 0.1;
  const legs = (p, top) => p.y < top && p.y > M.ankleY - 0.04 && Math.abs(p.x) < 0.3;
  for (const g of list) {
    if (typeof g === 'string' && g.startsWith('sewn:')) {
      const spec = cfg?.wardrobe?.sewn?.[g.slice(5)];
      if (!spec) continue;
      try {
        const made = sewnGarment(vrm, body, M, spec, uniforms, gradientMap, g.slice(5).replace(/[^\p{L}\p{N}_-]/gu, '_'));
        for (const m of made) { out.meshes.push(m); if (!out.skirt && m.name.startsWith('KlavaSewnSkirt_')) out.skirt = m; }
      } catch (e) { console.warn('сшитая вещь не собралась', g, e); }
      continue;
    }
    if (g === 'offShoulder') {
      out.meshes.push(shell(vrm, body,
        (p) => (torso(p) && p.y > M.spineY - 0.01 && p.y < M.chestY + 0.05) || armZone(p, M.shX + 0.12),
        () => 0.006, mk('offsh', OFF_SHOULDER), 'KlavaOffShoulderTop'));
    } else if (g === 'fishnet') {
      out.meshes.push(shell(vrm, body, (p) => legs(p, M.hipsY - 0.08), () => 0.0016, mk('fishnet', FISHNET), 'KlavaFishnet'));
    } else if (g === 'cropTop') {
      out.meshes.push(shell(vrm, body, (p) => torso(p) && p.y > M.chestY - 0.08 && p.y < M.neckY + 0.02, () => 0.005, mk('crop', CROP_TOP), 'KlavaCropTop'));
    } else if (g === 'jacket') {
      out.meshes.push(shell(vrm, body,
        (p) => (torso(p) && p.y > M.spineY - 0.015) || armZone(p, M.handX - 0.02),
        (p) => (Math.abs(p.x) > M.shX + 0.01 ? 0.012 : 0.017), mk('jacket', JACKET), 'KlavaJacket'));
    } else if (g === 'leggings') {
      out.meshes.push(shell(vrm, body, (p) => legs(p, M.spineY - 0.01), () => 0.0035, mk('leggings', LEGGINGS), 'KlavaLeggings'));
    } else if (g === 'pleatSkirt') {
      out.skirt = pleatSkirt(vrm, body, M, mk('pleat', PLEAT));
      out.meshes.push(out.skirt);
    } else if (g === 'harness') {
      out.meshes.push(shell(vrm, body, (p) => torso(p) && p.y > M.spineY - 0.01 && p.y < M.neckY, () => 0.011, mk('harness', HARNESS), 'KlavaHarness'));
    } else if (g === 'pajamaTop') {
      // кардиган: свободный крой (offset 0.024), тело от низа бёдер до шеи, рукава с рюшами
      out.meshes.push(shell(vrm, body,
        (p) => (Math.abs(p.x) < M.shX + 0.012 && p.y > M.hipsY - 0.055 && p.y < M.neckY) ||
               (Math.abs(p.x) >= M.shX - 0.015 && Math.abs(p.x) < M.elbowX + 0.026 && p.y > M.chestY - 0.095 && p.y < M.chestY + 0.08),
        () => 0.024, mk('pajamaTop', PAJAMA_TOP), 'KlavaPajamaTop'));
    } else if (g === 'pajamaPants') {
      // прямые пушистые штаны: от бёдер до щиколоток, свободный прямой крой
      // offset расширяется книзу (от 0.022 на бёдрах до 0.042 у щиколоток), компенсируя сужение ноги
      const straightOffset = (p) => {
        const t = Math.max(0, Math.min(1, (M.hipsY - p.y) / Math.max(0.01, M.hipsY - M.ankleY)));
        return 0.022 + t * 0.020;
      };
      out.meshes.push(shell(vrm, body, (p) => legs(p, M.hipsY + 0.01), straightOffset, mk('pajamaPants', PAJAMA_PANTS), 'KlavaPajamaPants'));
    }
  }
  if (shoes) {
    const sh = SHOES[shoes.style];
    const top = shoes.style === 'knee' ? M.kneeY + 0.03 : shoes.style === 'ankle' ? M.ankleY + 0.075 : shoes.style === 'slippers' ? M.ankleY + 0.055 : M.kneeY - 0.1;
    const bootMat = garmentMaterial(`boots-${shoes.style}`, BOOTS, uniforms, gradientMap, null, {
      uBootStyle: { value: sh.style }, uBootTop: { value: top }, uKneeX: { value: M.kneeX }, uAnkleX: { value: M.ankleX },
      uBootColor: { value: rgb(shoes.color ?? sh.color) }, uBootAccent: { value: rgb(shoes.accent ?? sh.accent) },
    });
    out.meshes.push(shell(vrm, body, (p) => p.y < top + 0.004 && p.y > M.ankleY - 0.08 && Math.abs(p.x) < 0.3,
      () => (shoes.style === 'combat' ? 0.007 : 0.0045), bootMat, 'KlavaBoots'));
    // стопа: оболочка по родной обуви тем же материалом, родная прячется
    let shoeMesh = null;
    vrm.scene.traverse((o) => { if (!shoeMesh && o.isSkinnedMesh && [].concat(o.material).some((m) => /Shoes/i.test(m?.name ?? ''))) shoeMesh = o; });
    if (shoeMesh) {
      out.meshes.push(shell(vrm, shoeMesh, () => true, () => (shoes.style === 'combat' ? 0.003 : 0.0015), bootMat, 'KlavaShoes'));
      shoeMesh.visible = false;
    }
  }
  if (hat) out.hat = makeHat(vrm, hat, gradientMap);
  let t = 0;
  out.update = (dt) => { t += dt; uniforms.uTime.value = t; };
  return out;
}

/** Цвет '#rrggbb' или [r, g, b] (линейные) → вектор для шейдера. */
function rgb(c) {
  if (Array.isArray(c)) return new THREE.Vector3(c[0], c[1], c[2]);
  const k = new THREE.Color(c);
  return new THREE.Vector3(k.r, k.g, k.b);
}

/** Шерсть: мягкий шум для берета. */
function woolTexture() {
  const cv = document.createElement('canvas');
  cv.width = cv.height = 128;
  const x = cv.getContext('2d');
  x.fillStyle = '#ffffff'; x.fillRect(0, 0, 128, 128);
  for (let i = 0; i < 3000; i++) {
    x.fillStyle = `rgba(0,0,0,${0.03 + Math.random() * 0.06})`;
    x.fillRect(Math.random() * 128, Math.random() * 128, 1.5, 1.5);
  }
  const t = new THREE.CanvasTexture(cv);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.repeat.set(3, 3);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

/**
 * Шляпа на голове (держатель в кости головы, оси мира в покое: +X — её левая, +Z — вперёд): берет — пухлый блин с
 * хвостиком, сдвинут набок и назад, чтобы кошачьи ушки стояли перед ним; ведьминская — конус с изломом и поля.
 */
function makeHat(vrm, hat, gradientMap) {
  const head = vrm.humanoid.getRawBoneNode('head');
  if (!head) return null;
  const spec = HATS[hat.kind];
  const color = new THREE.Color(hat.color ?? spec.color), accent = new THREE.Color(hat.accent ?? spec.accent);
  const g = new THREE.Group();
  if (hat.kind === 'beret') {
    const prof = [];
    const R = 0.108;
    for (let i = 0; i <= 18; i++) {
      const a = (i / 18) * Math.PI;
      // верх почти плоский, по краю пухлый валик, снизу — к ободку
      const r = R * Math.sin(a) ** 0.55;
      const y = 0.042 * Math.cos(a) + (i > 14 ? -0.007 * (i - 14) : 0);
      prof.push(new THREE.Vector2(Math.max(0.0005, i > 14 ? Math.max(r, 0.074) : r), y));
    }
    const mat = new THREE.MeshToonMaterial({ color, gradientMap, map: woolTexture() });
    const beret = new THREE.Mesh(new THREE.LatheGeometry(prof, 40), mat);
    beret.scale.set(1, 0.95, 1);
    const band = new THREE.Mesh(new THREE.TorusGeometry(0.075, 0.006, 8, 40), new THREE.MeshToonMaterial({ color: color.clone().multiplyScalar(0.6), gradientMap }));
    band.rotation.x = Math.PI / 2;
    band.position.y = -0.028;
    const stalk = new THREE.Mesh(new THREE.CylinderGeometry(0.0035, 0.005, 0.014, 8), mat);
    stalk.position.y = 0.043;
    const pin = new THREE.Mesh(new THREE.SphereGeometry(0.009, 12, 8), new THREE.MeshToonMaterial({ color: accent, gradientMap }));
    pin.position.set(0.082, -0.004, 0.03);
    g.add(beret, band, stalk, pin);
    // на макушке (причёска до y 1,63 в мире), набок к её правой стороне и чуть назад — ушки стоят перед ним
    g.position.copy(atRest(vrm, -0.03, 1.668, -0.022));
    g.rotation.set(-0.1, 0, 0.3);
  } else if (hat.kind === 'witch') {
    const mat = new THREE.MeshToonMaterial({ color, gradientMap, side: THREE.DoubleSide });
    // мини-шляпка ведьмы (как в аниме): высокий конус не влезал в окно — голова почти упирается в его верх
    const brim = new THREE.Mesh(new THREE.RingGeometry(0.07, 0.155, 48), mat);
    brim.rotation.x = -Math.PI / 2;
    const cone = new THREE.Mesh(new THREE.ConeGeometry(0.078, 0.16, 32, 6, true), mat);
    cone.position.y = 0.08;
    // излом кончика назад
    const pos = cone.geometry.attributes.position;
    for (let i = 0; i < pos.count; i++) { const y = pos.getY(i) + 0.08; const k = Math.max(0, (y - 0.07) / 0.09); pos.setZ(i, pos.getZ(i) - k * k * 0.05); pos.setX(i, pos.getX(i) - k * k * 0.02); }
    cone.geometry.computeVertexNormals();
    const band = new THREE.Mesh(new THREE.CylinderGeometry(0.07, 0.074, 0.02, 32, 1, true), new THREE.MeshToonMaterial({ color: accent, gradientMap, side: THREE.DoubleSide }));
    band.position.y = 0.012;
    g.add(brim, cone, band);
    g.position.copy(atRest(vrm, -0.02, 1.672, -0.025));
    g.rotation.set(-0.12, 0, 0.26);
  }
  const hold = holderAt(head, g.position.clone());
  g.position.set(0, 0, 0);
  hold.add(g);
  hold.traverse((m) => { m.frustumCulled = false; });
  return hold;
}

// ---------- хэнсин: вспышка при переодевании ----------

/** Вспышка с искрами поверх окна: in — прикрыть перед перезагрузкой, out — раскрыться после. */
export function henshin(dir = 'in', ms = 650) {
  const el = document.createElement('div');
  el.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:50;' +
    'background:radial-gradient(circle at 50% 40%, rgba(255,255,255,0.98) 0%, rgba(255,190,235,0.9) 25%, rgba(130,210,255,0.6) 55%, rgba(0,0,0,0) 80%);' +
    `transition:opacity ${ms}ms ease, transform ${ms}ms ease;opacity:${dir === 'in' ? 0 : 1};transform:scale(${dir === 'in' ? 0.2 : 1.2})`;
  document.body.appendChild(el);
  requestAnimationFrame(() => requestAnimationFrame(() => {
    el.style.opacity = dir === 'in' ? '1' : '0';
    el.style.transform = `scale(${dir === 'in' ? 1.2 : 0.2})`;
  }));
  if (dir === 'out') setTimeout(() => el.remove(), ms + 50);
  return new Promise((r) => setTimeout(r, ms));
}
