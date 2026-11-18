// Редактор Клавы: та же модель и тот же config.json, что у живой Клавы, но с ручками.
//  - Части: снять/надеть куски VRM, кимоно, ушки, хвост.
//  - Цвета и форма: палитра и ползунки по разделам конфига; кимоно перекрашивается на лету.
//  - Лицо и поза: мимика VRM, стойка, поворот головы.
//  - Кисть: рисуешь прямо по модели — луч попадает в треугольник, по его UV мажем текстуру материала.
//  - Экспорт: GLB для Blender (каждая часть отдельно, скелет, формы лица, кимоно запечено в картинку),
//    пакет текстур для VRoid (раскладка Виты — это VRoid-модель), скриншот.
// Хост — KlavaEditor.exe (WebView2): пишет файлы. Без хоста (обычный браузер) файлы скачиваются.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { VRMLoaderPlugin, VRMUtils } from '@pixiv/three-vrm';
import { createLook, partLabel } from './look.js';

const $ = (id) => document.getElementById(id);
const host = window.chrome?.webview ?? null;
const params = new URLSearchParams(location.search);
const round3 = (v) => Math.round(v * 1000) / 1000;

// ---------- связь с хостом ----------
let reqId = 0;
const pending = new Map();
host?.addEventListener('message', (e) => {
  const m = e.data;
  if (m?.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
});
function call(type, payload = {}) {
  if (!host) return Promise.resolve(browserFallback(type, payload));
  const id = ++reqId;
  return new Promise((resolve) => { pending.set(id, resolve); host.postMessage({ id, type, ...payload }); });
}
function download(name, blob) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 5000);
}
const b64ToBytes = (b64) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
function browserFallback(type, p) {
  // отладка без хоста: window.__editorDry = [] — файлы не скачиваются, а складываются сюда (имя и размер)
  if (Array.isArray(window.__editorDry)) {
    window.__editorDry.push({ type, name: p.name ?? type, folder: p.folder, bytes: p.base64 ? Math.round(p.base64.length * 0.75) : (p.text ?? p.json ?? '').length });
    return { ok: true, path: `(сухой прогон) ${p.folder ?? ''}/${p.name ?? ''}` };
  }
  if (type === 'saveConfig') download('config.json', new Blob([p.json], { type: 'application/json' }));
  else if (type === 'saveTexture' || type === 'saveFile') download(p.name, new Blob([p.text ?? b64ToBytes(p.base64)]));
  else if (type === 'pickModel') return { ok: false, error: 'Сменить модель можно только в KlavaEditor.exe' };
  return { ok: true, path: '(скачано браузером)' };
}
function bytesToB64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}
const canvasB64 = (c) => c.toDataURL('image/png').split(',')[1];

function status(text, kind = '') {
  const el = $('status');
  el.textContent = text;
  el.className = kind;
}
async function busy(text, fn) {
  $('busy').textContent = text;
  $('busy').classList.add('show');
  await new Promise((r) => setTimeout(r, 30));   // дать панели нарисоваться до тяжёлой работы
  try { return await fn(); } finally { $('busy').classList.remove('show'); }
}

// ---------- конфиг ----------
let cfg = await fetch('./config.json', { cache: 'no-store' }).then((r) => r.json());
let draft = structuredClone(cfg);
const getPath = (o, path) => path.split('.').reduce((a, k) => (a == null ? a : a[k]), o);
function setPath(o, path, v) {
  const ks = path.split('.');
  let a = o;
  for (let i = 0; i < ks.length - 1; i++) {
    const k = ks[i], nextIsIndex = /^\d+$/.test(ks[i + 1]);
    if (a[k] == null) a[k] = nextIsIndex ? [] : {};
    a = a[k];
  }
  a[ks[ks.length - 1]] = v;
}

// ---------- сцена ----------
const view = $('view');
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(Math.min(3, window.devicePixelRatio * 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
view.prepend(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color('#12131a');
const camera = new THREE.PerspectiveCamera(28, 1, 0.02, 50);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.dampingFactor = 0.1;
controls.target.set(0, 0.85, 0);
const sun = new THREE.DirectionalLight(0xffffff, Math.PI);
sun.position.set(0.6, 1.2, 1.5);
const rim = new THREE.DirectionalLight(0xbfd4ff, 1.2);
rim.position.set(-1, 1, -1.5);
const amb = new THREE.AmbientLight(0xffffff, 0.3 * Math.PI);
scene.add(sun, rim, amb);
const grid = new THREE.GridHelper(4, 40, 0x3a4060, 0x262a3a);
scene.add(grid);

function resize() {
  const w = view.clientWidth, h = view.clientHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / Math.max(1, h);
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(view);
resize();

function frameView(kind) {
  const t = { full: [0, 0.85, 0, 0, 0.95, 3.6], face: [0, 1.52, 0, 0, 1.55, 0.9], back: [0, 0.9, 0, 0, 1.0, -3.2] }[kind];
  controls.target.set(t[0], t[1], t[2]);
  camera.position.set(t[3], t[4], t[5]);
  controls.update();
}
frameView('full');

// ---------- модель ----------
const loader = new GLTFLoader();
loader.register((parser) => new VRMLoaderPlugin(parser));
let vrm = null, look = null;
const tailState = { emotion: { happy: 0.2, angry: 0, sad: 0, relaxed: 0.4, surprised: 0 }, pleased: 0, speaking: false };

async function loadModel(url) {
  status(`Загружаю ${url}…`);
  if (vrm) {
    scene.remove(vrm.scene);
    VRMUtils.deepDispose(vrm.scene);
  }
  const gltf = await loader.loadAsync(url);
  vrm = gltf.userData.vrm;
  VRMUtils.removeUnnecessaryVertices(gltf.scene);
  VRMUtils.combineSkeletons(gltf.scene);
  VRMUtils.rotateVRM0(vrm);
  vrm.scene.traverse((o) => {
    o.frustumCulled = false;
    if (o.isMesh) {
      const isFace = Boolean(
        o.morphTargetInfluences?.length > 0 ||
        o.geometry?.morphAttributes?.position?.length > 0 ||
        /face|fcl|eye|mouth|lip|teeth|tongue|brow/i.test(o.name || '') ||
        [].concat(o.material).some((m) => /face|fcl|eye|mouth|lip|teeth|tongue|brow/i.test(m?.name || ''))
      );
      if (!isFace && o.geometry) {
        o.geometry.computeVertexNormals();
        if (o.geometry.attributes.normal) o.geometry.attributes.normal.needsUpdate = true;
      }
      for (const m of [].concat(o.material)) {
        if (!m) continue;
        m.flatShading = false;
        if ('shadingToonyFactor' in m && m.shadingToonyFactor > 0.8) {
          m.shadingToonyFactor = isFace ? 0.82 : 0.72;
        }
        m.needsUpdate = true;
      }
    }
  });
  scene.add(vrm.scene);
  look = createLook(vrm);
  history.length = 0;
  // Свои уши/хвост/кимоно уже есть в модели (например, из Blender) — кодовые поверх дали бы дубли
  const own = ownParts();
  const off = [];
  for (const [k, name] of [['ears', 'ушки'], ['tail', 'хвост'], ['kimono', 'кимоно']]) {
    if (!own[k] || draft[k]?.enabled === false) continue;
    draft[k] = { ...(draft[k] ?? {}), enabled: false };
    off.push(name);
  }
  rebuild();
  rebuildPanels();
  buildFaces();
  buildMats();
  status(off.length ? `Модель: ${url} — в ней свои ${off.join(', ')}, кодовые выключила` : `Модель: ${url}`, 'ok');
}
function ownParts() {
  const own = { ears: false, tail: false, kimono: false };
  for (const o of look.orig.visible.keys()) {
    if (!o.isMesh) continue;
    const s = `${o.name} ${[].concat(o.material).map((m) => m?.name ?? '').join(' ')}`;
    if (/Ухо|Уши|(^|[^a-z])ears?([^a-z]|$)/i.test(s) && !/hair|волос/i.test(s)) own.ears = true;
    if ((/Хвост/i.test(s) || /(^|[^a-z])(cat_?tail|neko_?tail)([^a-z]|$)/i.test(s)) && !/hair|волос|pony|twin/i.test(s)) own.tail = true;
    if (/Кимоно|kimono/i.test(s)) own.kimono = true;
  }
  return own;
}

function rebuild() {
  if (!look) return;
  look.apply(draft);
  boundsDirty = true;   // тело могло вырасти или похудеть — габариты для луча кисти пересчитать
  applyPose();
}
function rebuildPanels() {
  buildParts();
  buildColors();
  buildShape();
  buildBody();
  buildOutfit();
}
let rebuildTimer = 0;
function rebuildSoon() { clearTimeout(rebuildTimer); rebuildTimer = setTimeout(rebuild, 180); }

// ---------- история: мазки кисти и правки конфига ----------
const history = [];
function pushDraft() {
  history.push({ kind: 'draft', json: JSON.stringify(draft) });
  if (history.length > 60) history.shift();
}
function undo() {
  const h = history.pop();
  if (!h) { status('Отменять нечего'); return; }
  if (h.kind === 'draft') {
    draft = JSON.parse(h.json);
    rebuild();
    rebuildPanels();
    status('Отменила правку');
  } else if (h.kind === 'paint') {
    h.ctx.putImageData(h.data, 0, 0);
    h.tex.needsUpdate = true;
    status('Отменила мазок');
  }
}

// ---------- части ----------
function check(parent, text, checked, onChange, note = '') {
  const row = document.createElement('label');
  row.className = 'check';
  row.innerHTML = `<input type="checkbox"><span></span><small></small>`;
  row.querySelector('span').textContent = text;
  row.querySelector('small').textContent = note;
  const box = row.querySelector('input');
  box.checked = checked;
  box.addEventListener('change', () => onChange(box.checked));
  parent.appendChild(row);
  return box;
}
const togglePart = (path, on) => { pushDraft(); setPath(draft, path, on); rebuild(); rebuildPanels(); };
/** Снять (hide) или надеть куски VRM по именам сеток — общая для «Частей» и «Одежды». */
function setHiddenMeshes(names, hide) {
  pushDraft();
  const set = new Set(draft.hideMeshes ?? []);
  for (const n of names) { if (hide) set.add(n); else set.delete(n); }
  draft.hideMeshes = [...set];
  rebuild();
  buildParts();
  buildGarments();
}
function buildParts() {
  const pk = $('partsKlava');
  pk.innerHTML = '';
  check(pk, 'Кимоно', draft.kimono?.enabled !== false, (on) => togglePart('kimono.enabled', on));
  check(pk, 'Кошачьи ушки', draft.ears?.enabled !== false, (on) => togglePart('ears.enabled', on));
  check(pk, 'Хвост', draft.tail?.enabled !== false, (on) => togglePart('tail.enabled', on));
  check(pk, 'Коготки', draft.claws?.enabled !== false, (on) => togglePart('claws.enabled', on));

  // Куски модели группами по смыслу («Пряди волос» — это полсотни кусков): галочка группы снимает/надевает
  // всю группу, внутри — каждый кусок отдельно (так, например, спрятаны родные кибер-ушки Виты).
  const pv = $('partsVrm');
  pv.innerHTML = '';
  if (!look) return;
  const byKimono = new Set(draft.kimono?.enabled !== false ? draft.kimono?.hideMeshes ?? [] : []);
  const groups = new Map();
  for (const m of look.orig.visible.keys()) {
    if (!m.isMesh) continue;
    const label = partLabel(m);
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label).push(m);
  }
  const setHidden = setHiddenMeshes;
  for (const [label, meshes] of [...groups].sort((a, b) => a[0].localeCompare(b[0], 'ru'))) {
    const hiddenSet = new Set(draft.hideMeshes ?? []);
    const shown = meshes.filter((m) => !hiddenSet.has(m.name)).length;
    if (meshes.length === 1) {
      const m = meshes[0];
      check(pv, label, shown === 1, (on) => setHidden([m.name], !on), byKimono.has(m.name) ? 'прячет кимоно' : '');
      continue;
    }
    const det = document.createElement('details');
    det.style.cssText = 'margin:2px 0;border:none;background:none';
    const sum = document.createElement('summary');
    sum.style.cssText = 'padding:0;font-weight:400';
    det.appendChild(sum);
    const box = check(sum, `${label}`, shown > 0, (on) => setHidden(meshes.map((m) => m.name), !on), `${shown}/${meshes.length}`);
    box.indeterminate = shown > 0 && shown < meshes.length;
    box.parentElement.style.display = 'inline-flex';
    box.parentElement.style.width = 'calc(100% - 20px)';
    box.addEventListener('click', (e) => e.stopPropagation());
    const inner = document.createElement('div');
    inner.style.paddingLeft = '22px';
    for (const m of meshes)
      check(inner, m.name, !hiddenSet.has(m.name), (on) => setHidden([m.name], !on), byKimono.has(m.name) ? 'прячет кимоно' : '');
    det.appendChild(inner);
    pv.appendChild(det);
  }
}

// ---------- цвета ----------
// Значения в конфиге — линейные (их умножают на цвет материала в шейдере); палитре показываем sRGB.
const KIMONO_DEFAULTS = {
  top: [0.07, 0.16, 0.62], hem: [0.008, 0.012, 0.08], wave: [0.55, 0.72, 1.0], collar: [0.01, 0.015, 0.09],
  gold: [1.0, 0.8, 0.35], eri: [0.98, 0.98, 1.0], juban: [1.0, 0.86, 0.9], obi: [0.94, 0.94, 1.0],
  cord: [1.0, 0.45, 0.65], petal: [1.0, 0.93, 0.96], petalDeep: [1.0, 0.38, 0.55], zip: [0.82, 0.84, 0.9],
};
const KIMONO_COLORS = [['top', 'Ткань сверху'], ['hem', 'Ткань к подолу'], ['wave', 'Волны'], ['petal', 'Сакура'], ['petalDeep', 'Сакура — серединка'],
  ['gold', 'Золото'], ['collar', 'Кант и ворот'], ['obi', 'Пояс оби'], ['cord', 'Шнурок'],
  ['juban', 'Нижняя рубаха (видна в вороте и разрезе)'], ['eri', 'Воротничок рубахи'], ['zip', 'Молния']]
  .map(([k, l]) => [`kimono.${k}`, l, KIMONO_DEFAULTS[k]]);
// нарисованное прямо на коже (у Виты — боди, перчатки, чулки): красит bodypaint.js; null — как в модели
const BODY_PAINT = [['bodyPaint.suit', 'Купальник / боди', [0.05, 0.06, 0.35]], ['bodyPaint.gloves', 'Перчатки (лапки)', [0.08, 0.2, 0.6]],
  ['bodyPaint.stockings', 'Чулки', [0.06, 0.25, 0.55]]];
const COLORS = [
  ['Волосы', [['hairTint', 'Оттенок волос', [1, 1, 1]], ['hairAccent', 'Синие пряди', [0.36, 0.62, 1]]]],
  ['Уши и хвост', [['ears.outer', 'Мех', [0.45, 0.66, 1]], ['ears.inner', 'Внутри уха', [1, 0.86, 0.93]], ['ears.tuft', 'Пух и кончик хвоста', [1, 0.98, 1]], ['tail.color', 'Хвост (если не как уши)', null]]],
  ['Коготки', [['claws.base', 'У основания', [0.98, 0.42, 0.66]], ['claws.color', 'Острие', [1, 0.98, 0.96]], ['claws.outline', 'Контур', [0.22, 0.03, 0.08]]]],
];
const ALL_COLORS = [...COLORS, ['Кимоно', KIMONO_COLORS], ['Тело', [['body.skin', 'Кожа', [1, 1, 1]], ...BODY_PAINT]]];   // для палитры VRoid
const toHex = (a) => '#' + new THREE.Color().setRGB(a[0], a[1], a[2], THREE.LinearSRGBColorSpace).getHexString(THREE.SRGBColorSpace);
const fromHex = (hex) => { const c = new THREE.Color().setStyle(hex, THREE.SRGBColorSpace); return [round3(c.r), round3(c.g), round3(c.b)]; };
const KIMONO_UNIFORM = { top: 'uTop', hem: 'uHem', wave: 'uWave', collar: 'uCollar', gold: 'uGold', eri: 'uEri', juban: 'uJuban', obi: 'uObi', cord: 'uCord', petal: 'uPetal', petalDeep: 'uPetalDeep', zip: 'uZip' };

function subTitle(root, text) {
  const sub = document.createElement('div');
  sub.className = 'sub';
  sub.textContent = text;
  root.appendChild(sub);
}
/** Строка «подпись + палитра» для пути в конфиге. Цвета кимоно меняются на лету, остальное — пересборкой. */
function colorRow(root, path, label, def) {
  const row = document.createElement('div');
  row.className = 'row';
  row.innerHTML = '<label></label><input type="color">';
  row.querySelector('label').textContent = label;
  const inp = row.querySelector('input');
  inp.value = toHex(getPath(draft, path) ?? def ?? getPath(draft, 'ears.outer') ?? [0.45, 0.66, 1]);
  let pushed = false;
  inp.addEventListener('input', () => {
    if (!pushed) { pushDraft(); pushed = true; }
    const v = fromHex(inp.value);
    setPath(draft, path, v);
    const k = path.startsWith('kimono.') ? path.slice(7) : null;
    const u = k && look?.built.kimono?.uniforms[KIMONO_UNIFORM[k]];
    if (u) u.value.set(...v);          // кимоно — на лету, без пересборки
    else rebuildSoon();
  });
  inp.addEventListener('change', () => { pushed = false; });
  root.appendChild(row);
  return inp;
}
function buildColors() {
  const root = $('colors');
  root.innerHTML = '';
  for (const [group, items] of COLORS) {
    subTitle(root, group);
    for (const [path, label, def] of items) colorRow(root, path, label, def);
  }
}

// ---------- ползунки: общая сборка ----------
// spec: [[группа, [[путь, подпись, min, max, step, по умолчанию], …]], …];
// live: путь → uniform кимоно, который меняется на лету без пересборки.
const fmt = (v) => (+v).toFixed(3).replace(/\.?0+$/, '');
function buildSliders(root, spec, live = {}) {
  root.innerHTML = '';
  for (const [group, items] of spec) {
    if (group) subTitle(root, group);
    for (const [path, label, min, max, step, def] of items) {
      const row = document.createElement('div');
      row.className = 'row';
      row.innerHTML = '<label></label><input type="range"><output></output>';
      row.querySelector('label').textContent = label;
      const inp = row.querySelector('input'), out = row.querySelector('output');
      Object.assign(inp, { min, max, step });
      inp.value = getPath(draft, path) ?? def;
      out.textContent = fmt(inp.value);
      let pushed = false;
      inp.addEventListener('input', () => {
        if (!pushed) { pushDraft(); pushed = true; }
        // массивы (offset, tilt, scale) при первой правке заполняем значениями по умолчанию
        const m = path.match(/^(.*)\.(\d)$/);
        if (m && !Array.isArray(getPath(draft, m[1]))) {
          const siblings = items.filter(([p]) => p.startsWith(m[1] + '.')).map((it) => it[5]);
          setPath(draft, m[1], [0, 1, 2].map((i) => siblings[i] ?? 0));
        }
        setPath(draft, path, +inp.value);
        out.textContent = fmt(inp.value);
        const u = live[path] && look?.built.kimono?.uniforms[live[path]];
        if (u) u.value = +inp.value;
        else rebuildSoon();
      });
      inp.addEventListener('change', () => { pushed = false; });
      root.appendChild(row);
    }
  }
}

// ---------- ушки и хвост ----------
const SHAPE = [
  ['Ушки', [['ears.size', 'Размер', 0.6, 2.2, 0.01, 1.3], ['ears.offset.0', 'Врозь', 0, 0.13, 0.001, 0.07], ['ears.offset.1', 'Высота', 0.06, 0.26, 0.001, 0.17],
    ['ears.offset.2', 'Вперёд/назад', -0.09, 0.07, 0.001, -0.02], ['ears.tilt.0', 'Наклон вперёд', -0.7, 0.5, 0.01, -0.12], ['ears.tilt.1', 'Поворот', -0.5, 0.9, 0.01, 0.25],
    ['ears.tilt.2', 'Развал в стороны', -0.9, 0.4, 0.01, -0.32]]],
  ['Хвост', [['tail.length', 'Длина', 0.3, 1.3, 0.01, 0.8], ['tail.radius', 'Толщина', 0.02, 0.09, 0.001, 0.05], ['tail.furLen', 'Пушистость', 0, 0.09, 0.001, 0.042],
    ['tail.offset.1', 'Выше/ниже', -0.15, 0.1, 0.001, 0], ['tail.offset.2', 'Ближе к спине', -0.1, 0.1, 0.001, 0]]],
  ['Коготки', [['claws.length', 'Длина', 0.004, 0.03, 0.0005, 0.015], ['claws.width', 'Толщина', 0.3, 1.2, 0.01, 0.8], ['claws.curl', 'Загиб', 0, 1.2, 0.01, 0.7]]],
];
function buildShape() { buildSliders($('shape'), SHAPE); }

// ---------- тело (figure.js) ----------
const BODY_SHAPE = [
  ['Общее', [['body.height', 'Рост', 0.8, 1.2, 0.005, 1], ['body.head', 'Голова', 0.8, 1.3, 0.005, 1]]],
  ['Шея и плечи', [['body.neckLen', 'Длина шеи', 0.7, 1.5, 0.01, 1], ['body.neck', 'Толщина шеи', 0.8, 1.35, 0.01, 1],
    ['body.shoulders', 'Ширина плеч', 0.8, 1.25, 0.005, 1]]],
  ['Торс', [['body.torsoLen', 'Длина торса', 0.85, 1.2, 0.005, 1], ['body.chest', 'Грудная клетка', 0.85, 1.25, 0.005, 1],
    ['body.waist', 'Талия', 0.75, 1.3, 0.005, 1], ['body.hips', 'Таз вширь', 0.8, 1.35, 0.005, 1], ['body.butt', 'Попа', 0.8, 1.45, 0.005, 1]]],
  ['Грудь', [['bust.scale.0', 'Ширина', 0.6, 1.6, 0.01, 1], ['bust.scale.1', 'Высота', 0.6, 1.6, 0.01, 1], ['bust.scale.2', 'Объём', 0.6, 1.8, 0.01, 1],
    ['bust.shift.0', 'К центру/в стороны', -0.03, 0.03, 0.001, 0], ['bust.shift.1', 'Выше/ниже', -0.03, 0.03, 0.001, 0], ['bust.shift.2', 'Вперёд', -0.03, 0.03, 0.001, 0]]],
  ['Руки', [['body.armLen', 'Длина', 0.85, 1.2, 0.005, 1], ['body.arms', 'Толщина', 0.75, 1.4, 0.01, 1], ['body.hands', 'Кисти', 0.8, 1.3, 0.005, 1]]],
  ['Ноги', [['body.legLen', 'Длина', 0.85, 1.2, 0.005, 1], ['body.thighs', 'Бёдра', 0.8, 1.4, 0.01, 1], ['body.calves', 'Икры', 0.8, 1.4, 0.01, 1],
    ['body.feet', 'Стопы', 0.8, 1.3, 0.005, 1]]],
];
const BODY_PRESETS = [
  ['Как в модели', {}],
  ['Миниатюрная', { height: 0.93, head: 1.05, neckLen: 0.95, shoulders: 0.94, chest: 0.95, waist: 0.92, hips: 0.97, armLen: 0.97, legLen: 0.96, hands: 0.93, feet: 0.92 }],
  ['Стройная', { neck: 0.93, waist: 0.88, arms: 0.9, thighs: 0.9, calves: 0.92, legLen: 1.04 }],
  ['Фигуристая', { chest: 1.03, waist: 0.88, hips: 1.14, butt: 1.2, thighs: 1.1, calves: 1.04 }],
  ['Спортивная', { neck: 1.08, shoulders: 1.07, chest: 1.06, waist: 0.95, arms: 1.12, thighs: 1.1, calves: 1.12 }],
  ['Чиби', { height: 0.9, head: 1.25, neckLen: 0.85, torsoLen: 0.92, armLen: 0.9, legLen: 0.86, hands: 1.08, feet: 1.1 }],
];
function buildBody() {
  const p = $('bodyPresets');
  p.innerHTML = '';
  for (const [name, preset] of BODY_PRESETS) {
    const b = document.createElement('button');
    b.textContent = name;
    b.title = name === 'Как в модели' ? 'Все пропорции — как в самой модели (кожа и грудь остаются)' : 'Заготовка — дальше крути ползунками';
    b.addEventListener('click', () => {
      pushDraft();
      const skin = draft.body?.skin;
      draft.body = { ...preset, ...(skin ? { skin } : {}) };
      rebuild();
      buildBody();
      status(`Тело: «${name}»`);
    });
    p.appendChild(b);
  }
  const skin = $('bodySkin');
  skin.innerHTML = '';
  colorRow(skin, 'body.skin', 'Цвет кожи', [1, 1, 1]);
  buildSliders($('bodyShape'), BODY_SHAPE);
}

// ---------- одежда: кимоно и вещи модели ----------
const KIMONO_SHAPE = [
  ['Застёжка', [
    ['kimono.open', 'Расстегнуть (0 — застёгнуто)', 0, 1, 0.01, 0], ['kimono.gap', 'Ширина распаха', 0.02, 0.2, 0.002, 0.09],
  ]],
  ['Крой', [
    ['kimono.hemAboveFoot', 'Подол над стопой', 0, 0.45, 0.005, 0.03], ['kimono.slitW', 'Разрез спереди (0 — зашит)', 0, 0.22, 0.002, 0.11],
    ['kimono.flare', 'Колокол подола', 0, 2.5, 0.05, 1], ['kimono.sleeveEnd', 'Рукав короче', -0.03, 0.3, 0.005, 0.05],
    ['kimono.sleeveBell', 'Раструб рукава', 0, 0.16, 0.002, 0.08], ['kimono.thickness', 'Отступ ткани от тела', 0.004, 0.03, 0.001, 0.011],
    ['kimono.obiShift', 'Оби выше / ниже', -0.08, 0.08, 0.002, 0], ['kimono.obiH', 'Ширина оби', 0.03, 0.1, 0.002, 0.06],
    ['kimono.neckline', 'Запах: глубже ↔ закрытее', -0.06, 0.12, 0.002, 0], ['kimono.innerShift', 'Вырез рубахи', -0.05, 0.08, 0.002, 0],
    ['kimono.waves', 'Волны', 0, 1.5, 0.05, 1], ['kimono.sakura', 'Сакура', 0, 2, 0.05, 1],
  ]],
];
const KIMONO_LIVE = { 'kimono.waves': 'uWaveK', 'kimono.sakura': 'uSakuraK', 'kimono.open': 'uOpen', 'kimono.gap': 'uGap' };
function buildOutfit() {
  const on = $('kimonoOn');
  on.innerHTML = '';
  check(on, 'Кимоно надето', draft.kimono?.enabled !== false, (v) => togglePart('kimono.enabled', v));
  // галочка кимоно: значение в конфиге и uniform шейдера (на лету); rebuild — если меняется ещё и сетка (утолщение оби)
  const flag = (label, path, uniform, value, toConfig, rebuildToo = false, note = '') => check(on, label, value, (v) => {
    pushDraft();
    setPath(draft, path, toConfig(v));
    const u = look?.built.kimono?.uniforms[uniform];
    if (u) u.value = v ? 1 : 0;
    if (rebuildToo) rebuildSoon();
  }, note);
  flag('Застёжка — молния (вместо запаха)', 'kimono.closure', 'uStyle', draft.kimono?.closure === 'zip', (v) => (v ? 'zip' : 'wrap'));
  flag('Пояс оби', 'kimono.obiOn', 'uObiOn', draft.kimono?.obiOn !== false, (v) => v, true, 'с поясом распах — до пояса');
  flag('Нижняя рубаха', 'kimono.innerShirt', 'uJubanOn', draft.kimono?.innerShirt !== false, (v) => v, false, 'без неё в вороте и разрезе — тело');
  flag('Прорезь под хвост', 'kimono.tailHole', 'uTailHole', draft.kimono?.tailHole !== false, (v) => v);
  buildSliders($('kimonoShape'), KIMONO_SHAPE, KIMONO_LIVE);
  const kc = $('kimonoColors');
  kc.innerHTML = '';
  for (const [path, label, def] of KIMONO_COLORS) colorRow(kc, path, label, def);
  buildBodyPaint();
  buildGarments();
}

/** Нарисованное на коже (купальник, перчатки, чулки): свой цвет или «как в модели» (null). */
function buildBodyPaint() {
  const root = $('bodyPaint');
  root.innerHTML = '';
  if (!look?.bodyPaintAvailable()) {
    root.innerHTML = '<div class="hint">У этой модели на коже ничего не нарисовано — перекрашивать нечего.</div>';
    return;
  }
  for (const [path, label, def] of BODY_PAINT) {
    const inp = colorRow(root, path, label, def);
    const row = inp.parentElement;
    const reset = document.createElement('button');
    reset.textContent = '↺';
    reset.title = 'Как в модели';
    reset.style.cssText = 'padding:2px 8px;font-size:12px';
    reset.addEventListener('click', () => {
      pushDraft();
      setPath(draft, path, null);
      rebuildSoon();
      buildBodyPaint();
    });
    row.appendChild(reset);
    if (getPath(draft, path) == null) inp.title = 'Сейчас — как в модели';
  }
}

// Вещь модели = материал (у VRoid одна вещь — один материал, иногда на нескольких сетках).
// Лицо, глаза, волосы и кожу сюда не берём — это «Тело», «Цвета» и «Части».
const NOT_CLOTH = /_(FACE|SKIN|EYE|HAIR)\b|Face|Eye|Hair|Brow|Lash|Mouth/i;
const outfitMap = (kind) => { draft.outfit ??= {}; draft.outfit[kind] ??= {}; return draft.outfit[kind]; };
function garmentSlider(parent, label, min, max, step, value, onValue) {
  let pushed = false;
  const inp = slider(parent, label, min, max, step, value, (v) => {
    if (!pushed) { pushDraft(); pushed = true; }
    onValue(v);
    rebuildSoon();
  });
  inp.addEventListener('change', () => { pushed = false; });
  return inp;
}
function buildGarments() {
  const root = $('garments');
  root.innerHTML = '';
  if (!look) return;
  const byMat = new Map();
  for (const m of look.orig.visible.keys()) {
    if (!m.isMesh) continue;
    const mat = [].concat(m.material).find((x) => x && !isOutline(x));
    if (!mat || NOT_CLOTH.test(mat.name)) continue;
    const key = mat.name.replace(/\s*\(Outline\)\s*$/, '');
    if (!byMat.has(key)) byMat.set(key, { label: partLabel(m), meshes: [] });
    byMat.get(key).meshes.push(m);
  }
  if (!byMat.size) {
    root.innerHTML = '<div class="hint">У этой модели нет отдельных вещей — одежда нарисована прямо на теле. Её можно перерисовать «Кистью».</div>';
    return;
  }
  const hidden = new Set(draft.hideMeshes ?? []);
  for (const [mat, { label, meshes }] of [...byMat].sort((a, b) => a[1].label.localeCompare(b[1].label, 'ru'))) {
    const box = document.createElement('div');
    box.className = 'garment';
    const head = document.createElement('div');
    head.className = 'head';
    const shown = meshes.filter((m) => !hidden.has(m.name)).length;
    const cb = check(head, label === mat ? mat : `${label} · ${mat}`, shown > 0, (v) => setHiddenMeshes(meshes.map((m) => m.name), !v),
      meshes.length > 1 ? `${shown}/${meshes.length}` : '');
    cb.indeterminate = shown > 0 && shown < meshes.length;
    cb.parentElement.title = `${mat}\nсетки: ${meshes.map((m) => m.name).join(', ')}`;
    const reset = document.createElement('button');
    reset.textContent = '↺';
    reset.title = 'Вернуть родной цвет и посадку';
    reset.addEventListener('click', () => {
      pushDraft();
      delete draft.outfit?.tint?.[mat];
      delete draft.outfit?.gain?.[mat];
      for (const m of meshes) delete draft.outfit?.fit?.[m.name];
      rebuild();
      buildGarments();
    });
    head.appendChild(reset);
    box.appendChild(head);

    const row = document.createElement('div');
    row.className = 'row';
    row.innerHTML = '<label>Цвет</label><input type="color">';
    const col = row.querySelector('input');
    col.value = toHex(draft.outfit?.tint?.[mat] ?? [1, 1, 1]);
    let pushed = false;
    col.addEventListener('input', () => {
      if (!pushed) { pushDraft(); pushed = true; }
      outfitMap('tint')[mat] = fromHex(col.value);
      rebuildSoon();
    });
    col.addEventListener('change', () => { pushed = false; });
    box.appendChild(row);
    garmentSlider(box, 'Яркость', 0.2, 3, 0.01, draft.outfit?.gain?.[mat] ?? 1, (v) => { outfitMap('gain')[mat] = v; });
    const fit0 = (draft.outfit?.fit?.[meshes[0].name] ?? 0) * 1000;
    garmentSlider(box, 'Посадка, мм', -8, 15, 0.5, fit0, (v) => {
      const fit = outfitMap('fit');
      for (const m of meshes) { if (v === 0) delete fit[m.name]; else fit[m.name] = v / 1000; }
    });
    for (const r of box.querySelectorAll('.row')) r.style.marginLeft = '24px';
    root.appendChild(box);
  }
}

// ---------- лицо и поза ----------
const pose = { preset: 'stand', headYaw: 0, headPitch: 0, bodyYaw: 0 };
const faceValues = {};
let lastPoseKey = '';
function applyPose() {
  if (!vrm) return;
  const key = `${pose.preset}|${pose.headYaw}|${pose.headPitch}|${pose.bodyYaw}`;
  if (key !== lastPoseKey) { lastPoseKey = key; boundsDirty = true; }
  const N = (n) => vrm.humanoid.getNormalizedBoneNode(n);
  vrm.humanoid.resetNormalizedPose();
  if (pose.preset === 'caramell') {
    const t = performance.now() * 0.001;
    const beat = t * 2.78; // ~167 BPM
    const theta = beat * Math.PI;
    const hx = -Math.cos(theta); // плавный маятник от -1 до +1
    const centerDip = 1.0 - hx * hx;
    const pulse = Math.pow(Math.abs(hx), 2.2);

    // Руки: «заячьи ушки» на макушке, локти в стороны, пульсация кистей на каждый «БАМ!»
    N('leftUpperArm').rotation.set(0.18, 0.4, 2.2);
    N('rightUpperArm').rotation.set(0.18, -0.4, -2.2);
    N('leftLowerArm').rotation.set(-1.5, 0.28, 0);
    N('rightLowerArm').rotation.set(-1.5, -0.28, 0);
    N('leftHand').rotation.set(0.30 + 0.50 * pulse, 0, 0.22);
    N('rightHand').rotation.set(0.30 + 0.50 * pulse, 0, -0.22);

    // Ноги: «ножки-мячики», пружинят в коленях (траектория буквы U)
    const legDip = 0.038 + 0.045 * centerDip;
    if (N('leftUpperLeg') && N('rightUpperLeg')) {
      N('leftUpperLeg').rotation.set(-0.10 - 0.16 * legDip, 0, 0.12);
      N('rightUpperLeg').rotation.set(-0.10 - 0.16 * legDip, 0, -0.12);
      N('leftLowerLeg').rotation.set(0.22 + 0.30 * legDip, 0, 0);
      N('rightLowerLeg').rotation.set(0.22 + 0.30 * legDip, 0, 0);
      N('leftFoot').rotation.set(-0.10 - 0.14 * legDip, 0, 0);
      N('rightFoot').rotation.set(-0.10 - 0.14 * legDip, 0, 0);
    }

    // Таз и бёдра: сочная буква «U» (в центре присели, по краям выныривает влево/вправо и вверх)
    N('hips').position.x = 0.088 * hx;
    N('hips').position.y = -legDip;
    N('hips').rotation.z = -hx * 0.08;
    N('spine').rotation.set(0, pose.bodyYaw + hx * 0.05, -hx * 0.13);
    N('neck').rotation.set(pose.headPitch * 0.35, pose.headYaw * 0.35, 0);
    N('head').rotation.set(pose.headPitch * 0.65 - 0.05 * pulse, pose.headYaw * 0.65 + hx * 0.07, hx * 0.17);
    return;
  }
  const arms = { stand: [1.2, 0.25], A: [0.62, 0.1], T: [0, 0] }[pose.preset] ?? [1.2, 0.25];
  N('leftUpperArm').rotation.set(0, 0, arms[0]);
  N('rightUpperArm').rotation.set(0, 0, -arms[0]);
  N('leftLowerArm').rotation.set(0, arms[1], 0);
  N('rightLowerArm').rotation.set(0, -arms[1], 0);
  N('spine').rotation.y = pose.bodyYaw;
  N('neck').rotation.set(pose.headPitch * 0.35, pose.headYaw * 0.35, 0);
  N('head').rotation.set(pose.headPitch * 0.65, pose.headYaw * 0.65, 0);
}
function slider(parent, label, min, max, step, value, onInput) {
  const row = document.createElement('div');
  row.className = 'row';
  row.innerHTML = '<label></label><input type="range"><output></output>';
  row.querySelector('label').textContent = label;
  const inp = row.querySelector('input'), out = row.querySelector('output');
  Object.assign(inp, { min, max, step });
  inp.value = value;
  out.textContent = (+value).toFixed(2);
  inp.addEventListener('input', () => { out.textContent = (+inp.value).toFixed(2); onInput(+inp.value); });
  parent.appendChild(row);
  return inp;
}
function buildPose() {
  const p = $('poses');
  p.innerHTML = '';
  for (const [key, text] of [['stand', 'Стоит'], ['A', 'А-поза'], ['T', 'T-поза'], ['caramell', '🍬 Карамельдансен']]) {
    const b = document.createElement('button');
    b.textContent = text;
    b.classList.toggle('on', pose.preset === key);
    b.addEventListener('click', () => { pose.preset = key; applyPose(); buildPose(); });
    p.appendChild(b);
  }
  const root = $('pose');
  root.innerHTML = '';
  slider(root, 'Голова — поворот', -0.8, 0.8, 0.01, pose.headYaw, (v) => { pose.headYaw = v; applyPose(); });
  slider(root, 'Голова — кивок', -0.5, 0.5, 0.01, pose.headPitch, (v) => { pose.headPitch = v; applyPose(); });
  slider(root, 'Корпус — поворот', -0.6, 0.6, 0.01, pose.bodyYaw, (v) => { pose.bodyYaw = v; applyPose(); });
}
const FACE_NAMES = { happy: 'Радость', angry: 'Злость', sad: 'Грусть', relaxed: 'Довольная', Surprised: 'Удивление', surprised: 'Удивление',
  Extra: 'Смущение ><', blink: 'Моргнуть', blinkLeft: 'Подмигнуть левым', blinkRight: 'Подмигнуть правым', aa: 'Рот «а»', ih: 'Рот «и»', ou: 'Рот «у»', ee: 'Рот «э»', oh: 'Рот «о»' };
function buildFaces() {
  const root = $('faces');
  root.innerHTML = '';
  const em = vrm?.expressionManager;
  if (!em) return;
  for (const e of em.expressions) {
    const name = e.expressionName;
    if (name === 'neutral' || /^look/.test(name)) continue;
    faceValues[name] ??= 0;
    slider(root, FACE_NAMES[name] ?? name, 0, 1, 0.01, faceValues[name], (v) => { faceValues[name] = v; });
  }
}

// ---------- кисть ----------
const brush = { on: false, pick: false, down: false, last: null };
const raycaster = new THREE.Raycaster();
const ndc = new THREE.Vector2();

// SkinnedMesh кэширует габариты от первой позы — после смены позы луч промахивался бы мимо рук
let boundsDirty = true;
function hitAt(ev) {
  const r = renderer.domElement.getBoundingClientRect();
  ndc.set(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
  raycaster.setFromCamera(ndc, camera);
  const targets = [];
  vrm?.scene.traverse((o) => {
    if (!o.isMesh || !o.visible || !look.orig.visible.has(o)) return;
    if (boundsDirty && o.isSkinnedMesh) { o.boundingSphere = null; o.boundingBox = null; }
    targets.push(o);
  });
  boundsDirty = false;
  for (const h of raycaster.intersectObjects(targets, false)) {
    const mats = [].concat(h.object.material);
    const mat = mats[h.face?.materialIndex ?? 0] ?? mats[0];
    // подводка, ресницы, блики — прозрачные накладки поверх лица: сквозь пустые пиксели бьём дальше
    if (h.uv && mat?.map && texel(mat, h.uv)[3] > 25) return { hit: h, mat };
  }
  return null;
}

const texelCanvas = document.createElement('canvas');
texelCanvas.width = texelCanvas.height = 1;
const texelCtx = texelCanvas.getContext('2d', { willReadFrequently: true });
/** Пиксель текстуры материала под UV: [r, g, b, a] 0..255. */
function texel(mat, uv) {
  const [x, y] = uvToPx(mat, uv);
  if (mat.userData.ctx) return mat.userData.ctx.getImageData(Math.floor(x), Math.floor(y), 1, 1).data;
  texelCtx.clearRect(0, 0, 1, 1);
  texelCtx.drawImage(mat.map.image, Math.floor(x), Math.floor(y), 1, 1, 0, 0, 1, 1);
  return texelCtx.getImageData(0, 0, 1, 1).data;
}

/** Текстура материала → холст, по которому можно рисовать (один раз на материал). */
function paintable(mat) {
  if (mat.userData.canvas) return mat.userData;
  const src = mat.map.image;
  const c = document.createElement('canvas');
  c.width = src.width;
  c.height = src.height;
  const ctx = c.getContext('2d', { willReadFrequently: true });
  ctx.drawImage(src, 0, 0);
  const tex = new THREE.CanvasTexture(c);
  for (const k of ['flipY', 'wrapS', 'wrapT', 'colorSpace', 'minFilter', 'magFilter', 'anisotropy']) tex[k] = mat.map[k];
  tex.offset.copy(mat.map.offset);
  tex.repeat.copy(mat.map.repeat);
  const oldMap = mat.map;
  // тот же холст — и контуру-близнецу «(Outline)», иначе контур остался бы со старой картинкой
  for (const m of [mat, ...look.materials().filter((o) => isOutline(o) && o.name.startsWith(mat.name))]) {
    if (m.map !== oldMap) continue;
    m.map = tex;
    if ('shadeMultiplyTexture' in m && m.shadeMultiplyTexture === oldMap) m.shadeMultiplyTexture = tex;
    m.needsUpdate = true;
  }
  Object.assign(mat.userData, { canvas: c, ctx, tex });
  return mat.userData;
}
function uvToPx(mat, uv) {
  const c = mat.userData.canvas ?? mat.map.image;
  const y = (mat.map.flipY ? 1 - uv.y : uv.y);
  return [((uv.x % 1) + 1) % 1 * c.width, ((y % 1) + 1) % 1 * c.height];
}
function dab(mat, x, y) {
  const { ctx, tex } = mat.userData;
  const size = +$('brushSize').value, alpha = +$('brushAlpha').value / 100, soft = +$('brushSoft').value / 100;
  const col = new THREE.Color($('brushColor').value);
  const rgb = `${Math.round(col.r * 255)},${Math.round(col.g * 255)},${Math.round(col.b * 255)}`;
  const g = ctx.createRadialGradient(x, y, 0, x, y, size / 2);
  g.addColorStop(0, `rgba(${rgb},${alpha})`);
  g.addColorStop(Math.max(0.001, 1 - soft), `rgba(${rgb},${alpha})`);
  g.addColorStop(1, `rgba(${rgb},0)`);
  ctx.fillStyle = g;
  ctx.beginPath();
  ctx.arc(x, y, size / 2, 0, Math.PI * 2);
  ctx.fill();
  tex.needsUpdate = true;
}
function pickColor(mat, uv) {
  const data = texel(mat, uv);
  $('brushColor').value = '#' + [data[0], data[1], data[2]].map((v) => v.toString(16).padStart(2, '0')).join('');
  status(`Взяла цвет ${$('brushColor').value}`);
}
function setBrushMode(on, pick = false) {
  brush.on = on;
  brush.pick = pick;
  $('btnPaint').classList.toggle('on', on && !pick);
  $('btnPick').classList.toggle('on', pick);
  controls.mouseButtons = on
    ? { LEFT: null, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.ROTATE }
    : { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
  $('hud').textContent = on ? (pick ? 'Кликни по модели — возьму цвет' : 'ЛКМ — рисовать · ПКМ — вертеть · колесо — ближе/дальше · Alt+клик — пипетка')
    : 'ЛКМ — вертеть · ПКМ — сдвигать · колесо — ближе/дальше';
  renderer.domElement.style.cursor = on ? 'crosshair' : '';
}
renderer.domElement.addEventListener('pointerdown', (ev) => {
  if (!brush.on || ev.button !== 0) return;
  const h = hitAt(ev);
  if (!h) return;
  if (brush.pick || ev.altKey) { pickColor(h.mat, h.hit.uv); if (brush.pick) setBrushMode(true, false); return; }
  const d = paintable(h.mat);
  history.push({ kind: 'paint', ctx: d.ctx, tex: d.tex, data: d.ctx.getImageData(0, 0, d.canvas.width, d.canvas.height) });
  if (history.length > 60) history.shift();
  h.mat.userData.painted = true;
  brush.down = true;
  brush.last = { mat: h.mat, xy: uvToPx(h.mat, h.hit.uv) };
  dab(h.mat, ...brush.last.xy);
  try { renderer.domElement.setPointerCapture(ev.pointerId); } catch { /* указатель уже отпущен — мазок останется точкой */ }
});
renderer.domElement.addEventListener('pointermove', (ev) => {
  const ring = $('brushRing');
  if (brush.on && !brush.pick) {
    const r = view.getBoundingClientRect();
    ring.style.display = 'block';
    ring.style.left = `${ev.clientX - r.left}px`;
    ring.style.top = `${ev.clientY - r.top}px`;
    const px = Math.max(6, +$('brushSize').value / 4);
    ring.style.width = ring.style.height = `${px}px`;
  } else ring.style.display = 'none';
  if (!brush.down) return;
  const h = hitAt(ev);
  if (!h || h.mat !== brush.last.mat) return;
  const [x, y] = uvToPx(h.mat, h.hit.uv);
  const [lx, ly] = brush.last.xy;
  const dist = Math.hypot(x - lx, y - ly);
  // между точками мазка — промежуточные пятна, иначе быстрый росчерк рассыпается в бусы
  if (dist < h.mat.userData.canvas.width * 0.25) {
    const step = Math.max(1, +$('brushSize').value / 5);
    for (let t = step; t < dist; t += step) dab(h.mat, lx + (x - lx) * t / dist, ly + (y - ly) * t / dist);
  }
  dab(h.mat, x, y);
  brush.last.xy = [x, y];
});
renderer.domElement.addEventListener('pointerup', () => { brush.down = false; });
renderer.domElement.addEventListener('pointerleave', () => { $('brushRing').style.display = 'none'; });

// ---------- текстуры: выгрузить / загрузить свою / сбросить ----------
const safeName = (s) => s.replace(/[^\p{L}\p{N}_-]+/gu, '_');
// three-vrm рисует контур MToon отдельным материалом «имя (Outline)» с той же картинкой и дублем геометрии
const isOutline = (m) => /\(Outline\)\s*$/.test(m?.name ?? '');
const surfaceMaterials = () => look.materials().filter((m) => !isOutline(m));
function imageToCanvas(img) {
  const c = document.createElement('canvas');
  c.width = img.width;
  c.height = img.height;
  c.getContext('2d').drawImage(img, 0, 0);
  return c;
}
let texTarget = null;
$('fileTex').addEventListener('change', async () => {
  const f = $('fileTex').files[0];
  $('fileTex').value = '';
  if (!f || !texTarget) return;
  const img = await createImageBitmap(f);
  const d = paintable(texTarget);
  history.push({ kind: 'paint', ctx: d.ctx, tex: d.tex, data: d.ctx.getImageData(0, 0, d.canvas.width, d.canvas.height) });
  d.ctx.clearRect(0, 0, d.canvas.width, d.canvas.height);
  d.ctx.drawImage(img, 0, 0, d.canvas.width, d.canvas.height);
  d.tex.needsUpdate = true;
  texTarget.userData.painted = true;
  status(`Своя текстура на «${texTarget.name}» — не забудь «Сохранить в Клаву»`, 'warn');
});
function buildMats() {
  const root = $('mats');
  root.innerHTML = '';
  if (!look) return;
  for (const m of surfaceMaterials()) {
    if (!m.map?.image) continue;
    const row = document.createElement('div');
    row.className = 'mat';
    row.innerHTML = '<span></span><button>PNG</button><button>Своя…</button><button>Сброс</button>';
    row.querySelector('span').textContent = m.name;
    row.querySelector('span').title = m.name;
    const [bPng, bOwn, bReset] = row.querySelectorAll('button');
    bPng.title = 'Выгрузить текстуру в папку «Текстуры» на рабочем столе — перерисуй в любой рисовалке';
    bPng.addEventListener('click', async () => {
      const c = m.userData.canvas ?? imageToCanvas(m.map.image);
      const r = await call('saveFile', { folder: 'textures', name: safeName(m.name) + '.png', base64: canvasB64(c) });
      status(r.ok ? `Текстура → ${r.path}` : `Не записала: ${r.error}`, r.ok ? 'ok' : 'bad');
    });
    bOwn.title = 'Подложить свою картинку (например, перерисованную PNG)';
    bOwn.addEventListener('click', () => { texTarget = m; $('fileTex').click(); });
    bReset.title = 'Вернуть родную текстуру';
    bReset.addEventListener('click', () => {
      for (const t of [m, ...look.materials().filter((x) => isOutline(x) && x.name.startsWith(m.name))]) {
        const o = look.orig.maps.get(t);
        t.map = o.map;
        if ('shadeMultiplyTexture' in t) t.shadeMultiplyTexture = o.shade;
        t.needsUpdate = true;
      }
      delete m.userData.canvas; delete m.userData.ctx; delete m.userData.tex;
      m.userData.painted = false;
      pushDraft();
      if (draft.textureOverrides) delete draft.textureOverrides[m.name];
      status(`«${m.name}» — родная текстура`);
    });
    root.appendChild(row);
  }
}

// ---------- сохранить в Клаву ----------
async function saveToKlava() {
  await busy('Сохраняю…', async () => {
    for (const m of surfaceMaterials()) {
      if (!m.userData.painted || !m.userData.canvas) continue;
      const name = safeName(m.name) + '.png';
      const r = await call('saveTexture', { name, base64: canvasB64(m.userData.canvas) });
      if (!r.ok) { status(`Текстура «${m.name}» не записалась: ${r.error}`, 'bad'); return; }
      draft.textureOverrides ??= {};
      draft.textureOverrides[m.name] = `textures/${name}`;
      m.userData.painted = false;
    }
    const r = await call('saveConfig', { json: JSON.stringify(draft, null, 2) });
    const live = r.live === true ? ' Клава перерисуется сама через пару секунд (посреди фразы — дождётся тишины).'
      : r.live === false ? ' Клава не запущена — увидишь при следующем запуске.' : '';
    if (r.ok) { cfg = structuredClone(draft); status(`Сохранено в ${r.path}.${live}`, 'ok'); }
    else status(`Не сохранилось: ${r.error}`, 'bad');
  });
}

// ---------- запекание кимоно в текстуру ----------
// Узор кимоно считает шейдер по координатам тела в покое. Для Blender/VRoid нужна обычная картинка:
// рисуем сетку не в экран, а в её UV-развёртку (gl_Position = uv), фрагмент — тот же узор без света.
// Два прохода: без дырок (цвет везде, чтобы не было тёмной каймы) и с дырками (прозрачность), потом
// раскрашиваем пустоту вокруг островов развёртки соседним цветом, чтобы на швах не проступал фон.
const SKIRT_NI = 56, SKIRT_NJ = 30;   // сетка юбки из kimono.js: 31 кольцо × 57 точек
function skirtUv(geo) {
  const n = geo.attributes.position.count;
  const uv = new Float32Array(n * 2);
  if (n === (SKIRT_NI + 1) * (SKIRT_NJ + 1)) {
    for (let k = 0; k < n; k++) { const j = Math.floor(k / (SKIRT_NI + 1)), i = k % (SKIRT_NI + 1); uv[k * 2] = i / SKIRT_NI; uv[k * 2 + 1] = j / SKIRT_NJ; }
  } else {
    const p = geo.attributes.position;
    let y0 = Infinity, y1 = -Infinity;
    for (let k = 0; k < n; k++) { y0 = Math.min(y0, p.getY(k)); y1 = Math.max(y1, p.getY(k)); }
    for (let k = 0; k < n; k++) { uv[k * 2] = Math.atan2(p.getX(k), p.getZ(k)) / (Math.PI * 2) + 0.5; uv[k * 2 + 1] = (y1 - p.getY(k)) / (y1 - y0); }
  }
  return new THREE.BufferAttribute(uv, 2);
}
function bakeMaterial(src, noHoles) {
  const m = new THREE.MeshToonMaterial({ color: 0xffffff, side: THREE.DoubleSide });
  const inner = src.onBeforeCompile;
  m.onBeforeCompile = (sh, r) => {
    inner.call(src, sh, r);
    sh.vertexShader = sh.vertexShader
      .replace('#include <common>', '#include <common>\nattribute vec2 bakeUv;')
      .replace('#include <project_vertex>', '#include <project_vertex>\ngl_Position = vec4(bakeUv * 2.0 - 1.0, 0.0, 1.0);');
    let fs = sh.fragmentShader.replace('diffuseColor.rgb *= c;\n}', `diffuseColor.rgb *= c;
}
{
  vec3 lc = clamp(diffuseColor.rgb, 0.0, 1.0);
  vec3 sc = mix(lc * 12.92, 1.055 * pow(lc, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), lc));
  gl_FragColor = vec4(sc, 1.0);
  return;
}`);
    if (noHoles) fs = fs.replaceAll('discard;', '{}');
    sh.fragmentShader = fs;
  };
  m.customProgramCacheKey = () => `kimono-bake-${noHoles ? 'solid' : 'holes'}`;
  return m;
}
function renderUv(mesh, mat, w, h) {
  const rt = new THREE.WebGLRenderTarget(w, h);
  const cam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
  cam.layers.set(7);
  const oldMat = mesh.material, oldLayers = mesh.layers.mask;
  mesh.material = mat;
  mesh.layers.set(7);
  const oldBg = scene.background;
  scene.background = null;
  renderer.setRenderTarget(rt);
  renderer.setClearColor(0x000000, 0);
  renderer.clear();
  renderer.render(scene, cam);
  const px = new Uint8Array(w * h * 4);
  renderer.readRenderTargetPixels(rt, 0, 0, w, h, px);
  renderer.setRenderTarget(null);
  scene.background = oldBg;
  mesh.material = oldMat;
  mesh.layers.mask = oldLayers;
  rt.dispose();
  mat.dispose();
  return px;
}
function bakeKimono(mesh, uv, w, h) {
  mesh.geometry.setAttribute('bakeUv', uv);
  const solid = renderUv(mesh, bakeMaterial(mesh.material, true), w, h);
  const holes = renderUv(mesh, bakeMaterial(mesh.material, false), w, h);
  mesh.geometry.deleteAttribute('bakeUv');
  const out = new Uint8ClampedArray(w * h * 4);
  const covered = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) {
    out[i * 4] = solid[i * 4]; out[i * 4 + 1] = solid[i * 4 + 1]; out[i * 4 + 2] = solid[i * 4 + 2];
    out[i * 4 + 3] = holes[i * 4 + 3];
    covered[i] = solid[i * 4 + 3] > 0 ? 1 : 0;
  }
  // раскраска пустоты вокруг островов: 8 шагов наружу, цвет и непрозрачность соседа
  for (let pass = 0; pass < 8; pass++) {
    const add = [];
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
      const i = y * w + x;
      if (covered[i]) continue;
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const xx = x + dx, yy = y + dy;
        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
        const j = yy * w + xx;
        if (covered[j]) { add.push(i, j); break; }
      }
    }
    for (let k = 0; k < add.length; k += 2) {
      const i = add[k], j = add[k + 1];
      for (let c = 0; c < 4; c++) out[i * 4 + c] = out[j * 4 + c];
      covered[i] = 1;
    }
  }
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  c.getContext('2d').putImageData(new ImageData(out, w, h), 0, 0);
  return c;
}
function bakedKimonoCanvases() {
  const k = look.built.kimono;
  if (!k) return null;
  const top = k.top.userData.bodyUv ? bakeKimono(k.top, k.top.userData.bodyUv, 2048, 2048) : null;
  const skirtUvAttr = skirtUv(k.skirt.geometry);
  const skirt = bakeKimono(k.skirt, skirtUvAttr, 2048, 1024);
  return { top, skirt, skirtUv: skirtUvAttr };
}

// ---------- экспорт для Blender ----------
function canvasTexture(c) {
  const t = new THREE.CanvasTexture(c);
  t.flipY = false;                 // UV у VRM по правилам glTF: v = 0 — верх картинки
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
const alphaCache = new Map();
/** Мех: glTF не знает отдельной альфа-карты — кладём шум волосков в альфу белой картинки. */
function alphaAsMap(tex) {
  if (alphaCache.has(tex)) return alphaCache.get(tex);
  const src = tex.image;
  const c = document.createElement('canvas');
  c.width = src.width; c.height = src.height;
  const ctx = c.getContext('2d');
  ctx.drawImage(src, 0, 0);
  const d = ctx.getImageData(0, 0, c.width, c.height);
  for (let i = 0; i < d.data.length; i += 4) { d.data[i + 3] = d.data[i + 1]; d.data[i] = d.data[i + 1] = d.data[i + 2] = 255; }
  ctx.putImageData(d, 0, 0);
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.repeat.copy(tex.repeat);
  t.colorSpace = THREE.SRGBColorSpace;
  alphaCache.set(tex, t);
  return t;
}
function exportMaterial(mat, mesh, baked) {
  const std = (o) => new THREE.MeshStandardMaterial({ roughness: 0.9, metalness: 0, name: mat.name || mesh.name, ...o });
  if (mesh.name === 'KlavaKimonoTop' && baked?.top) return std({ name: 'Кимоно_верх', map: canvasTexture(baked.top), alphaTest: 0.5, side: THREE.DoubleSide });
  if (mesh.name === 'KlavaKimonoSkirt' && baked?.skirt) return std({ name: 'Кимоно_низ', map: canvasTexture(baked.skirt), alphaTest: 0.5, side: THREE.DoubleSide });
  if (mat.alphaMap) return std({ color: mat.color.clone(), map: alphaAsMap(mat.alphaMap), alphaTest: mat.alphaTest, side: THREE.DoubleSide, vertexColors: mat.vertexColors, roughness: 1 });
  return std({
    color: mat.color?.clone() ?? new THREE.Color(1, 1, 1), map: mat.map ?? null, vertexColors: mat.vertexColors,
    transparent: mat.transparent, alphaTest: mat.alphaTest, side: mat.side, opacity: mat.opacity ?? 1,
  });
}
async function exportBlender() {
  return busy('Собираю GLB для Blender…', async () => {
    const saved = { pose: { ...pose }, faces: { ...faceValues } };
    pose.preset = 'T'; pose.headYaw = pose.headPitch = pose.bodyYaw = 0;
    for (const k of Object.keys(faceValues)) faceValues[k] = 0;
    applyPose();
    for (const k of Object.keys(faceValues)) vrm.expressionManager.setValue(k, 0);
    look.built.tail?.bones.forEach((b) => b.rotation.set(0, 0, 0));   // хвост — в позе привязки
    vrm.update(0);
    vrm.scene.updateMatrixWorld(true);

    const baked = bakedKimonoCanvases();
    const restore = [];
    const hiddenInKlava = [];
    const rig = vrm.humanoid.normalizedHumanBonesRoot;
    const rigParent = rig.parent;
    rig.removeFromParent();
    const tailMeshes = new Set(look.built.tail?.meshes ?? []);
    vrm.scene.traverse((o) => {
      if (!o.isMesh) return;
      // куски, которые у Клавы спрятаны, тоже едут в Blender — в список «спрятанное», чтобы их можно было надеть
      if (look.orig.visible.has(o) && !o.visible) { hiddenInKlava.push(o); restore.push(() => { o.visible = false; }); o.visible = true; }
      // мех — оставляем каждый четвёртый слой: в Blender с полусотней слоёв на ухо не поработаешь
      const layer = o.userData.furLayer ?? (tailMeshes.has(o) ? look.built.tail.meshes.indexOf(o) : 0);
      const layers = o.userData.furLayers ?? (tailMeshes.has(o) ? look.built.tail.meshes.length - 1 : 0);
      if (layer > 0 && !(layer % 4 === 0 || layer === layers) && o.visible) { o.visible = false; restore.push(() => { o.visible = true; }); }
      const oldMat = o.material, oldName = o.name;
      // контур MToon — дубль геометрии с отдельным материалом; в Blender он лёг бы второй поверхностью поверх первой
      if (Array.isArray(oldMat) && oldMat.some(isOutline)) {
        const oldGroups = o.geometry.groups;
        const remap = new Map();
        oldMat.forEach((m, i) => { if (!isOutline(m)) remap.set(i, remap.size); });
        o.geometry.groups = oldGroups.filter((g) => remap.has(g.materialIndex)).map((g) => ({ ...g, materialIndex: remap.get(g.materialIndex) }));
        restore.push(() => { o.geometry.groups = oldGroups; });
      }
      const mats = [].concat(oldMat).filter((m) => !isOutline(m)).map((m) => exportMaterial(m, o, baked));
      o.material = Array.isArray(oldMat) ? mats : mats[0];
      if (o.name === 'KlavaKimonoTop' && o.geometry.attributes.uv === undefined && o.userData.bodyUv) {
        o.geometry.setAttribute('uv', o.userData.bodyUv);
        restore.push(() => o.geometry.deleteAttribute('uv'));
      }
      if (o.name === 'KlavaKimonoSkirt' && baked) { o.geometry.setAttribute('uv', baked.skirtUv); restore.push(() => o.geometry.deleteAttribute('uv')); }
      if (look.orig.visible.has(o)) o.name = `${partLabel(o)} · ${oldName}`;
      else if (oldName === 'KlavaKimonoTop') o.name = 'Кимоно — верх';
      else if (oldName === 'KlavaKimonoSkirt') o.name = 'Кимоно — низ';
      else if (tailMeshes.has(o)) o.name = look.built.tail.meshes.indexOf(o) === 0 ? 'Хвост' : `Хвост_мех_слой${look.built.tail.meshes.indexOf(o)}`;
      restore.push(() => { o.material = oldMat; o.name = oldName; });
    });
    const hiddenNames = hiddenInKlava.map((o) => o.name);
    let glb;
    try {
      glb = await new GLTFExporter().parseAsync(vrm.scene, { binary: true, onlyVisible: true, maxTextureSize: 4096 });
    } finally {
      for (let i = restore.length - 1; i >= 0; i--) restore[i]();
      rigParent.add(rig);
      Object.assign(pose, saved.pose);
      Object.assign(faceValues, saved.faces);
      rebuild();
    }
    const parts = { hidden: hiddenNames, note: 'Список собран редактором Клавы: эти куски у Клавы спрятаны; скрипт Blender кладёт их в коллекцию «Спрятанное».' };
    const r1 = await call('saveFile', { folder: 'blender', name: 'Клава.glb', base64: bytesToB64(new Uint8Array(glb)) });
    const r2 = await call('saveFile', { folder: 'blender', name: 'Клава.parts.json', text: JSON.stringify(parts, null, 2) });
    const mb = (glb.byteLength / 1048576).toFixed(1);
    status(r1.ok && r2.ok ? `GLB для Blender (${mb} МБ) → ${r1.path}` : `Не записалось: ${r1.error ?? r2.error}`, r1.ok && r2.ok ? 'ok' : 'bad');
    return r1.ok && r2.ok;
  });
}

// ---------- пакет для VRoid ----------
// VRoid Studio открывает только свои .vroid, VRM он не импортирует. Зато Вита — VRoid-модель, и её текстуры
// лежат в раскладке VRoid: их (и кимоно, запечённое в раскладку тела) можно подложить в редакторе текстур VRoid.
async function exportVroid() {
  return busy('Собираю текстуры для VRoid…', async () => {
    let ok = true;
    const save = async (name, c) => { const r = await call('saveFile', { folder: 'vroid', name, base64: canvasB64(c) }); ok &&= r.ok; return r; };
    for (const m of surfaceMaterials()) {
      if (!m.map?.image) continue;
      await save(`Текстуры Виты/${safeName(m.name)}.png`, m.userData.canvas ?? imageToCanvas(m.map.image));
    }
    const baked = bakedKimonoCanvases();
    if (baked?.top) await save('Кимоно в раскладке тела (F00_002_Body).png', baked.top);
    if (baked?.skirt) await save('Кимоно — юбка (развёртка цилиндром).png', baked.skirt);
    const pal = [];
    for (const [group, items] of ALL_COLORS) {
      pal.push(`${group}:`);
      for (const [path, label, def] of items) {
        const v = getPath(draft, path) ?? def;
        if (v) pal.push(`  ${label.padEnd(28)} ${toHex(v).toUpperCase()}`);
      }
    }
    pal.push('', 'Глаза: правый зелёный, левый голубой (гетерохромия).');
    const r = await call('saveFile', { folder: 'vroid', name: 'Палитра Клавы.txt', text: pal.join('\r\n') });
    ok &&= r.ok;
    status(ok ? `Пакет для VRoid → ${r.path}` : 'Часть файлов не записалась — глянь права на папку', ok ? 'ok' : 'bad');
    return ok;
  });
}

async function screenshot() {
  const now = new Date();
  const stamp = `${now.getFullYear()}${String(now.getMonth() + 1).padStart(2, '0')}${String(now.getDate()).padStart(2, '0')}_${String(now.getHours()).padStart(2, '0')}${String(now.getMinutes()).padStart(2, '0')}${String(now.getSeconds()).padStart(2, '0')}`;
  const r = await call('saveFile', { folder: 'shots', name: `Клава_${stamp}.png`, base64: canvasB64(renderer.domElement) });
  status(r.ok ? `Скриншот → ${r.path}` : `Не записался: ${r.error}`, r.ok ? 'ok' : 'bad');
}

// ---------- кнопки ----------
$('btnSave').addEventListener('click', saveToKlava);
$('btnBlender').addEventListener('click', exportBlender);
$('btnVroid').addEventListener('click', exportVroid);
$('btnShot').addEventListener('click', screenshot);
$('btnUndo').addEventListener('click', undo);
$('btnFolder').addEventListener('click', () => call('openFolder', { folder: 'root' }));
$('btnModel').addEventListener('click', async () => {
  const r = await call('pickModel');
  if (!r.ok) { if (r.error) status(r.error, 'warn'); return; }
  pushDraft();
  draft.model = r.path;
  await busy('Гружу модель…', () => loadModel(r.path));
});
$('btnPaint').addEventListener('click', () => setBrushMode(!(brush.on && !brush.pick)));
$('btnPick').addEventListener('click', () => setBrushMode(true, true));
for (const id of ['brushSize', 'brushAlpha', 'brushSoft']) $(id).addEventListener('input', () => { $(id + 'O').textContent = $(id).value; });
$('vFull').addEventListener('click', () => frameView('full'));
$('vFace').addEventListener('click', () => frameView('face'));
$('vBack').addEventListener('click', () => frameView('back'));
$('vSpin').addEventListener('click', () => { controls.autoRotate = !controls.autoRotate; $('vSpin').classList.toggle('on', controls.autoRotate); });
$('bgColor').addEventListener('input', () => { scene.background.set($('bgColor').value); });
$('grid').addEventListener('change', () => { grid.visible = $('grid').checked; });
$('light').addEventListener('input', () => { const k = +$('light').value / 100; sun.intensity = Math.PI * k; amb.intensity = 0.3 * Math.PI * k; $('lightO').textContent = $('light').value; });
addEventListener('keydown', (e) => {
  if (e.ctrlKey && e.code === 'KeyZ') { e.preventDefault(); undo(); }
  if (e.ctrlKey && e.code === 'KeyS') { e.preventDefault(); saveToKlava(); }
  if (e.target.tagName === 'INPUT') return;
  if (e.code === 'KeyB') setBrushMode(!(brush.on && !brush.pick));
  if (e.code === 'KeyF') frameView('face');
  if (e.code === 'KeyA') frameView('full');
});

// ---------- цикл ----------
const timer = new THREE.Timer();
function frame(now) {
  requestAnimationFrame(frame);
  timer.update(now);
  const dt = Math.min(timer.getDelta(), 0.1);
  if (vrm) {
    applyPose();
    for (const [k, v] of Object.entries(faceValues)) vrm.expressionManager?.setValue(k, v);
    if ($('tailAnim').checked) look.built.tail?.update(dt, tailState);
    vrm.update(dt);
  }
  controls.update();
  renderer.render(scene, camera);
}

rebuildPanels();
buildPose();
// ?model=./assets/x.vrm — посмотреть другую модель, не трогая конфиг (проверка выгрузок из Blender)
await loadModel(params.get('model') ?? draft.model ?? './assets/Vita.vrm');
$('info').textContent = host ? 'KlavaEditor' : 'браузер (файлы будут скачиваться)';
requestAnimationFrame(frame);
window.__editor = { THREE, get vrm() { return vrm; }, get look() { return look; }, get draft() { return draft; }, exportBlender, exportVroid, bakedKimonoCanvases, renderer, camera, controls,
  // один кадр вручную — в свёрнутой вкладке requestAnimationFrame стоит
  renderOnce: () => { applyPose(); vrm?.update(1 / 60); controls.update(); renderer.render(scene, camera); }, frameView, setBrushMode };

// Безлюдный режим для KlavaEditor.exe --export: собрать всё и доложить хосту.
if (params.get('auto') === 'export') {
  const ok1 = await exportBlender();
  const ok2 = await exportVroid();
  await call('autoDone', { ok: ok1 && ok2 });
}
