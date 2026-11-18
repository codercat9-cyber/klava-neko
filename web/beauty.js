// Красота Клавы (друг 28.09: «больше детализации в лицо, аксессуары, красивее волосы; ушки и хвост не трогать»).
// Всё поверх модели, файл Vita.vrm не меняется:
//  - волосы: аниме-блик «ангельское кольцо» (своя matcap-текстура), голубой ободок света, тень прядей глубже;
//  - глаза: тёмный ободок радужки, тень от века сверху, светлые прожилки к зрачку, третья искорка-звёздочка;
//  - лицо: тени у глаз, чуть цвета на губах и живой румянец (смущение, радость, когда гладят) — кибер-узоры на щеках
//    не трогаются: румянец ложится только на кожу (малая насыщенность), яркие пиксели узора пропускаются;
//  - канзаши: три сакуры на шпильке слева и подвески из бусин, качаются от движения головы;
//  - чокер: бархатная лента с розовой кромкой и золотой колокольчик спереди.
//
// Координаты аксессуаров — мир в покое (+X — её левая сторона, +Y — вверх, +Z — вперёд, к зрителю); держатель в
// кости повёрнут так, чтобы его оси совпадали с миром в покое (как хвост в tail.js). Размеры сняты лучами по сетке
// 28.09: шея на высоте 1,41–1,43 м — радиус ~3,4 см, волосы у левого виска на y 1,52 — x 0,138.
import * as THREE from 'three';

const TAU = Math.PI * 2;

/** Картинка текстуры (ImageBitmap/Image) → холст того же размера. */
function canvasOf(tex) {
  const img = tex?.image;
  if (!img?.width) return null;
  const c = document.createElement('canvas');
  c.width = img.width;
  c.height = img.height;
  c.getContext('2d').drawImage(img, 0, 0);
  return c;
}

/** Холст → текстура с теми же настройками, что у старой; заменяется во всех материалах модели. */
function retexture(root, old, canvas) {
  const tex = new THREE.CanvasTexture(canvas);
  for (const k of ['flipY', 'wrapS', 'wrapT', 'minFilter', 'magFilter', 'anisotropy', 'colorSpace', 'generateMipmaps'])
    tex[k] = old[k];
  tex.needsUpdate = true;
  root.traverse((o) => {
    if (!o.isMesh) return;
    for (const m of [].concat(o.material)) if (m?.map === old) { m.map = tex; m.needsUpdate = true; }
  });
  return tex;
}

function materials(root, rx) {
  const out = new Set();
  root.traverse((o) => { if (o.isMesh) for (const m of [].concat(o.material)) if (m && rx.test(m.name ?? '')) out.add(m); });
  return [...out];
}

// ---------- волосы ----------

/**
 * Matcap «ангельского кольца»: чёрный фон (MToon складывает matcap поверх цвета), мягкая светлая дуга там, где нормали
 * смотрят вверх-на камеру (макушка, выпуклость прядей), и тонкий холодный ободок. У MToon ось v у matcap перевёрнута
 * относительно холста (замер 28.09: дуга в верхней половине холста легла пятном посреди чёлки) — рисуем вверх ногами.
 */
function haloMatcap() {
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const g = c.getContext('2d');
  g.fillStyle = '#000';
  g.fillRect(0, 0, 256, 256);
  g.filter = 'blur(7px)';
  g.strokeStyle = 'rgba(255,255,255,0.95)';
  g.lineWidth = 20;
  g.beginPath();
  g.ellipse(128, 146, 92, 88, 0, Math.PI * 1.2, Math.PI * 1.8);
  g.stroke();
  g.filter = 'blur(3px)';
  g.strokeStyle = 'rgba(255,255,255,0.65)';
  g.lineWidth = 6;
  g.beginPath();
  g.ellipse(128, 146, 92, 88, 0, Math.PI * 1.3, Math.PI * 1.7);
  g.stroke();
  g.filter = 'blur(4px)';
  g.strokeStyle = 'rgba(150,215,255,0.5)';
  g.lineWidth = 7;
  g.beginPath();
  g.arc(128, 128, 121, 0, TAU);
  g.stroke();
  // вверх ногами (см. выше): верх холста — нормали вниз
  const f = document.createElement('canvas');
  f.width = f.height = 256;
  const fg = f.getContext('2d');
  fg.translate(0, 256);
  fg.scale(1, -1);
  fg.drawImage(c, 0, 0);
  const tex = new THREE.CanvasTexture(f);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

function prettyHair(root, o) {
  const hair = materials(root, /hair/i);
  if (!hair.length) return 0;
  const halo = haloMatcap();
  for (const m of hair) {
    if (o.hairShine > 0 && 'matcapTexture' in m) {
      m.matcapTexture = halo;
      m.matcapFactor?.setScalar(0.42 * o.hairShine);
    }
    if (o.hairDepth > 0) {
      // тень прядей — глубже и синее: объём вместо плоской заливки
      m.shadeColorFactor?.multiply(new THREE.Color(0.72, 0.78, 0.95));
      if ('shadingToonyFactor' in m) m.shadingToonyFactor = Math.min(m.shadingToonyFactor ?? 0.75, 0.75);
      m.parametricRimColorFactor?.setRGB(0.22, 0.42, 0.7).multiplyScalar(o.hairDepth);
      if ('parametricRimFresnelPowerFactor' in m) m.parametricRimFresnelPowerFactor = 3.2;
      if ('parametricRimLiftFactor' in m) m.parametricRimLiftFactor = 0.0;
      if ('rimLightingMixFactor' in m) m.rimLightingMixFactor = 1.0;
    }
    m.needsUpdate = true;
  }
  return hair.length;
}

// ---------- глаза ----------

/** Радужки на текстуре: по половинам — рамка непрозрачных пикселей (две радужки, как у VRoid). */
function irisBoxes(ctx, w, h) {
  const px = ctx.getImageData(0, 0, w, h).data;
  const boxes = [];
  for (const [xa, xb] of [[0, w / 2], [w / 2, w]]) {
    let x0 = w, y0 = h, x1 = 0, y1 = 0;
    for (let y = 0; y < h; y += 2)
      for (let x = xa; x < xb; x += 2)
        if (px[(y * w + x) * 4 + 3] > 200) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
    // иконки-плашки внизу текстуры (цвет глаз для редактора VRoid) — не радужка: берём только крупное
    if (x1 - x0 > w * 0.08) boxes.push({ cx: (x0 + x1) / 2, cy: (y0 + y1) / 2, rx: (x1 - x0) / 2, ry: (y1 - y0) / 2 });
  }
  return boxes;
}

function prettyEyes(root) {
  const [iris] = materials(root, /EyeIris/);
  const [hl] = materials(root, /EyeHighlight/);
  const ic = iris?.map ? canvasOf(iris.map) : null;
  if (!ic) return 0;
  const g = ic.getContext('2d');
  const boxes = irisBoxes(g, ic.width, ic.height).filter((b) => b.ry > ic.height * 0.15);
  g.save();
  g.globalCompositeOperation = 'source-atop';   // только по самой радужке
  for (const b of boxes) {
    // тень от верхнего века — аниме-глаз темнее сверху
    const top = g.createLinearGradient(0, b.cy - b.ry, 0, b.cy);
    top.addColorStop(0, 'rgba(10,15,40,0.55)');
    top.addColorStop(1, 'rgba(10,15,40,0)');
    g.fillStyle = top;
    g.fillRect(b.cx - b.rx, b.cy - b.ry, b.rx * 2, b.ry);
    // светлые прожилки от зрачка
    g.strokeStyle = 'rgba(255,255,255,0.16)';
    g.lineWidth = Math.max(1, b.rx * 0.03);
    for (let i = 0; i < 22; i++) {
      const a = (i / 22) * TAU;
      g.beginPath();
      g.moveTo(b.cx + Math.cos(a) * b.rx * 0.3, b.cy + Math.sin(a) * b.ry * 0.3);
      g.lineTo(b.cx + Math.cos(a) * b.rx * 0.85, b.cy + Math.sin(a) * b.ry * 0.85);
      g.stroke();
    }
    // свечение снизу
    const low = g.createRadialGradient(b.cx, b.cy + b.ry * 0.55, 0, b.cx, b.cy + b.ry * 0.55, b.rx * 0.8);
    low.addColorStop(0, 'rgba(255,255,255,0.22)');
    low.addColorStop(1, 'rgba(255,255,255,0)');
    g.fillStyle = low;
    g.fillRect(b.cx - b.rx, b.cy, b.rx * 2, b.ry);
    // тёмный ободок радужки
    g.strokeStyle = 'rgba(8,12,35,0.6)';
    g.lineWidth = b.rx * 0.1;
    g.beginPath();
    g.ellipse(b.cx, b.cy, b.rx * 0.96, b.ry * 0.96, 0, 0, TAU);
    g.stroke();
  }
  g.restore();
  retexture(root, iris.map, ic);
  // искорка-звёздочка внизу справа и пара крошечных бликов — «живые» аниме-глаза
  const hc = hl?.map ? canvasOf(hl.map) : null;
  if (hc) {
    const h = hc.getContext('2d');
    h.fillStyle = '#fff';
    for (const b of boxes) {
      const sx = b.cx + b.rx * 0.34, sy = b.cy + b.ry * 0.36, s = b.rx * 0.2;
      h.beginPath();
      for (let i = 0; i < 8; i++) {
        const a = (i / 8) * TAU - Math.PI / 2, r = i % 2 ? s * 0.25 : s;
        h.lineTo(sx + Math.cos(a) * r, sy + Math.sin(a) * r);
      }
      h.closePath();
      h.fill();
      h.beginPath();
      h.arc(b.cx - b.rx * 0.38, b.cy + b.ry * 0.45, b.rx * 0.05, 0, TAU);
      h.fill();
    }
    retexture(root, hl.map, hc);
  }
  return boxes.length;
}

// ---------- лицо: тени у глаз, губы, румянец ----------

/**
 * Места на текстуре лица Vita (1024², сняты с картинки 28.09): глазницы ≈ (512 ∓ 157, 563), щёки (512 ∓ 150, 690),
 * губы (512, 770). Координаты — в долях размера текстуры, чтобы пережить другой размер.
 */
const FACE = { eyes: [[0.347, 0.55], [0.653, 0.55]], cheeks: [[0.365, 0.674], [0.635, 0.674]], lips: [0.5, 0.752] };

function faceLayer(root, o, decor) {
  const [face] = materials(root, /Face_00_SKIN/);
  const base = face?.map ? canvasOf(face.map) : null;
  if (!base) return null;
  const W = base.width, H = base.height;
  const bg = base.getContext('2d');
  // постоянное: тени у глаз и цвет губ — прямо в основу
  bg.save();
  bg.globalCompositeOperation = 'multiply';
  for (const [u, v] of FACE.eyes) {
    const x = u * W, y = v * H - 0.035 * H;
    const gr = bg.createRadialGradient(x, y, 0, x, y, 0.085 * W);
    gr.addColorStop(0, `rgba(222,150,190,${0.55 * o.eyeshadow})`);
    gr.addColorStop(1, 'rgba(222,150,190,0)');
    bg.fillStyle = gr;
    bg.save();
    bg.translate(x, y); bg.scale(1, 0.5); bg.translate(-x, -y);
    bg.fillRect(x - 0.1 * W, y - 0.1 * W, 0.2 * W, 0.2 * W);
    bg.restore();
  }
  const [lu, lv] = FACE.lips;
  const lg = bg.createRadialGradient(lu * W, lv * H, 0, lu * W, lv * H, 0.03 * W);
  lg.addColorStop(0, `rgba(235,120,150,${0.55 * o.lips})`);
  lg.addColorStop(1, 'rgba(235,120,150,0)');
  bg.fillStyle = lg;
  bg.save();
  bg.translate(lu * W, lv * H); bg.scale(1, 0.45); bg.translate(-lu * W, -lv * H);
  bg.fillRect(lu * W - 0.04 * W, lv * H - 0.04 * W, 0.08 * W, 0.08 * W);
  bg.restore();
  bg.restore();
  // кибер-дорожки у импланта, родинка, блик на губе (faceplus.js) — тоже в постоянную основу, до румянца
  decor?.(bg, W, H);

  // живое: румянец — отдельным слоем поверх основы, только по коже (кибер-узоры насыщенные — их пропускаем)
  const out = document.createElement('canvas');
  out.width = W; out.height = H;
  const og = out.getContext('2d');
  og.drawImage(base, 0, 0);
  const tex = retexture(root, face.map, out);
  const rx = 0.095 * W, ry = 0.055 * H;
  const regions = FACE.cheeks.map(([u, v]) => ({ x: Math.round(u * W - rx * 1.3), y: Math.round(v * H - ry * 1.6), w: Math.round(rx * 2.6), h: Math.round(ry * 3.2), cx: u * W, cy: v * H }));
  const basePx = regions.map((r) => bg.getImageData(r.x, r.y, r.w, r.h));
  let shown = -1;
  function setBlush(k) {
    k = Math.max(0, Math.min(1, k));
    if (Math.abs(k - shown) < 0.025) return;
    shown = k;
    regions.forEach((r, i) => {
      const src = basePx[i].data;
      const img = og.createImageData(r.w, r.h);
      const d = img.data;
      for (let y = 0; y < r.h; y++)
        for (let x = 0; x < r.w; x++) {
          const j = (y * r.w + x) * 4;
          let R = src[j], G = src[j + 1], B = src[j + 2];
          const mx = Math.max(R, G, B), mn = Math.min(R, G, B);
          const skin = mx > 120 && (mx - mn) / mx < 0.42 && R >= B;   // кожа: светлая, розоватая, ненасыщенная
          if (skin && k > 0) {
            const dx = (r.x + x - r.cx) / rx, dy = (r.y + y - r.cy) / ry;
            const fall = Math.max(0, 1 - (dx * dx + dy * dy));
            let a = 0.5 * k * fall * fall;
            // аниме-штрихи «///» при сильном смущении
            if (k > 0.55) {
              const s = ((r.x + x - r.cx) + (r.y + y - r.cy) * 0.9) / (0.025 * W);
              const stripe = Math.abs(s - Math.round(s)) < 0.1 && (dx * dx) / 0.45 + (dy * dy) / 0.28 < 1;   // овал, не коробка
              if (stripe) a = Math.min(0.9, a + (k - 0.55) * 1.6);
            }
            R = R + (240 - R) * a; G = G + (95 - G) * a; B = B + (135 - B) * a;
          }
          d[j] = R; d[j + 1] = G; d[j + 2] = B; d[j + 3] = src[j + 3];
        }
      og.putImageData(img, r.x, r.y);
    });
    tex.needsUpdate = true;
  }
  setBlush(0);
  return { setBlush };
}

// ---------- аксессуары ----------

/**
 * Точка, снятая на модели в родном росте (замеры 28.09), — туда же на модели после «Тела» из редактора: рост меняет
 * масштаб всей сцены VRM и приподнимает её (figure.js). Без этого чокер при росте 0,95 оказывался в подбородке.
 */
export function atRest(vrm, x, y, z) {
  const s = vrm.scene.scale.x;
  return new THREE.Vector3(x * s, y * s, z * s).add(vrm.scene.position);
}

/** Держатель в кости с осями мира в покое (как у хвоста). */
export function holderAt(bone, worldPoint) {
  const h = new THREE.Group();
  bone.add(h);
  h.position.copy(bone.worldToLocal(worldPoint.clone()));
  h.quaternion.copy(bone.getWorldQuaternion(new THREE.Quaternion()).invert());
  h.updateMatrixWorld(true);
  return h;
}

/** Цветок сакуры: пять лепестков с вырезом, от бело-розового центра к розовому краю (цвета по вершинам). */
function sakura(r, gradientMap) {
  const shape = new THREE.Shape();
  shape.moveTo(0, 0);
  shape.bezierCurveTo(-0.55 * r, 0.35 * r, -0.45 * r, 0.9 * r, -0.12 * r, r);
  shape.lineTo(0, 0.86 * r);                                   // вырез на кончике лепестка
  shape.lineTo(0.12 * r, r);
  shape.bezierCurveTo(0.45 * r, 0.9 * r, 0.55 * r, 0.35 * r, 0, 0);
  const petal = new THREE.ShapeGeometry(shape, 10);
  const pos = petal.attributes.position;
  const cols = [];
  const inner = new THREE.Color(1, 0.93, 0.96), outer = new THREE.Color(0.98, 0.52, 0.72), c = new THREE.Color();
  for (let i = 0; i < pos.count; i++) {
    const t = Math.min(1, Math.hypot(pos.getX(i), pos.getY(i)) / r);
    c.copy(inner).lerp(outer, t * t);
    cols.push(c.r, c.g, c.b);
  }
  petal.setAttribute('color', new THREE.Float32BufferAttribute(cols, 3));
  const mat = new THREE.MeshToonMaterial({ vertexColors: true, gradientMap, side: THREE.DoubleSide });
  const g = new THREE.Group();
  for (let i = 0; i < 5; i++) {
    const m = new THREE.Mesh(petal, mat);
    m.rotation.z = (i / 5) * TAU;
    m.rotation.x = 0.18;                                      // лепестки чуть чашечкой
    g.add(m);
  }
  const gold = new THREE.MeshToonMaterial({ color: 0xffd35a, gradientMap, emissive: 0x332200 });
  for (let i = 0; i < 6; i++) {
    const s = new THREE.Mesh(new THREE.SphereGeometry(0.075 * r, 6, 5), gold);
    const a = (i / 6) * TAU;
    s.position.set(Math.cos(a) * 0.22 * r, Math.sin(a) * 0.22 * r, 0.06 * r);
    g.add(s);
  }
  return g;
}

function kanzashi(vrm, gradientMap) {
  const head = vrm.humanoid.getRawBoneNode('head');
  if (!head) return null;
  // над левым виском, на поверхности волос (x 0,138 на y 1,52), чуть позади
  const hold = holderAt(head, atRest(vrm, 0.133, 1.522, -0.02));
  const g = new THREE.Group();
  // цветы смотрят наружу (её левая сторона, +X) и немного вперёд-вверх
  g.rotation.set(0.25, Math.PI / 2 - 0.35, 0);
  const big = sakura(0.024, gradientMap);
  const s1 = sakura(0.016, gradientMap); s1.position.set(0.024, 0.017, -0.004); s1.rotation.z = 0.5;
  const s2 = sakura(0.014, gradientMap); s2.position.set(-0.02, 0.022, -0.006); s2.rotation.z = -0.3;
  // шпилька уходит в волосы назад-вниз
  const pin = new THREE.Mesh(new THREE.CylinderGeometry(0.0022, 0.0015, 0.1, 8),
    new THREE.MeshToonMaterial({ color: 0xe8d9b0, gradientMap }));
  pin.rotation.x = Math.PI / 2;
  pin.position.set(0.004, -0.003, -0.05);
  g.add(big, s1, s2, pin);
  hold.add(g);
  // подвески: три нити бусин — отдельный держатель (качаются в мире, а не вместе с цветами)
  const bead = new THREE.MeshToonMaterial({ color: 0xff9ac4, gradientMap });
  const goldM = new THREE.MeshToonMaterial({ color: 0xffd35a, gradientMap });
  const dangles = [];
  for (let k = 0; k < 3; k++) {
    const pivot = new THREE.Group();
    pivot.position.set(0.004, -0.016 - k * 0.002, 0.006 - k * 0.011);
    const len = 0.05 + k * 0.012;
    for (let i = 0; i < 5; i++) {
      const b = new THREE.Mesh(new THREE.SphereGeometry(i === 4 ? 0.0042 : 0.0026, 8, 6), i % 2 ? goldM : bead);
      b.position.y = -((i + 1) / 5) * len;
      pivot.add(b);
    }
    const tip = sakura(0.006, gradientMap);
    tip.position.y = -len - 0.006;
    tip.rotation.y = Math.PI / 2;
    pivot.add(tip);
    hold.add(pivot);
    dangles.push({ pivot, a: 0, v: 0, b: 0, bv: 0, phase: k * 1.7 });
  }
  return { hold, dangles };
}

function choker(vrm, gradientMap, style = 'bell') {
  const neck = vrm.humanoid.getRawBoneNode('neck');
  if (!neck) return null;
  const hold = holderAt(neck, atRest(vrm, 0.007, 1.42, -0.008));
  if (style === 'pearls') return pearls(hold, gradientMap);
  const band = new THREE.Mesh(new THREE.CylinderGeometry(0.0375, 0.0385, 0.012, 48, 1, true),
    new THREE.MeshToonMaterial({ color: 0x1b1626, gradientMap, side: THREE.DoubleSide }));
  const edge = new THREE.MeshToonMaterial({ color: 0xff6fae, gradientMap });
  const top = new THREE.Mesh(new THREE.TorusGeometry(0.0378, 0.0011, 6, 48), edge);
  top.rotation.x = Math.PI / 2; top.position.y = 0.006;
  const bot = top.clone(); bot.position.y = -0.006;
  const g = new THREE.Group();
  g.scale.set(1, 1, 0.97);
  g.add(band, top, bot);
  hold.add(g);
  if (style === 'ring') {
    // кожаный чокер с серебряным колечком (гардероб: готика, кибер) — колечко качается на шарнире, как колокольчик
    band.material.color.setHex(0x0d0b10);
    edge.color.setHex(0x3a3440);
    const silver = new THREE.MeshToonMaterial({ color: 0xd8dde6, gradientMap, emissive: 0x111318 });
    const loop = new THREE.Mesh(new THREE.TorusGeometry(0.0034, 0.0009, 6, 14), silver);
    loop.position.set(0, -0.0035, 0.039);
    const pivot = new THREE.Group();
    pivot.position.set(0, -0.006, 0.0395);
    const ring = new THREE.Mesh(new THREE.TorusGeometry(0.0085, 0.0014, 8, 28), silver);
    ring.position.y = -0.0095;
    pivot.add(ring);
    hold.add(loop, pivot);
    return { hold, bell: { pivot, a: 0, v: 0, b: 0, bv: 0, phase: 0, gain: 0.3 } };
  }
  // колокольчик спереди: колечко и шарик с прорезью, висит на шарнире
  const gold = new THREE.MeshToonMaterial({ color: 0xffcf4a, gradientMap, emissive: 0x2a1a00 });
  const ring = new THREE.Mesh(new THREE.TorusGeometry(0.0038, 0.0011, 6, 16), gold);
  ring.position.set(0, -0.004, 0.039);
  const pivot = new THREE.Group();
  pivot.position.set(0, -0.007, 0.0395);
  const bell = new THREE.Mesh(new THREE.SphereGeometry(0.0085, 16, 12), gold);
  bell.position.y = -0.0095;
  bell.scale.set(1, 0.92, 1);
  const slit = new THREE.Mesh(new THREE.BoxGeometry(0.0115, 0.0012, 0.004), new THREE.MeshBasicMaterial({ color: 0x3a2400 }));
  slit.position.set(0, -0.0125, 0.0068);
  const hole = new THREE.Mesh(new THREE.CircleGeometry(0.0016, 10), new THREE.MeshBasicMaterial({ color: 0x3a2400 }));
  hole.position.set(0, -0.0145, 0.0084);
  pivot.add(bell, slit, hole);
  hold.add(ring, pivot);
  // gain — сила раскачки от ускорения шеи: бубенчик маленький и лёгкий, болтается живее подвесок шпильки
  return { hold, bell: { pivot, a: 0, v: 0, b: 0, bv: 0, phase: 0, gain: 0.4 } };
}

/** Нитка жемчуга вместо чокера (гардероб: классика) — два ряда, нижний провисает; звенеть нечему. */
function pearls(hold, gradientMap) {
  const mat = new THREE.MeshToonMaterial({ color: 0xf6f0e6, gradientMap, emissive: 0x1a1612 });
  const g = new THREE.Group();
  for (const [r, drop, n, size] of [[0.0395, 0.0, 44, 0.0023], [0.041, 0.016, 48, 0.0021]]) {
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2;
      const front = Math.max(0, Math.sin(a));                    // провис только спереди (+Z)
      const b = new THREE.Mesh(new THREE.SphereGeometry(size, 8, 6), mat);
      b.position.set(Math.cos(a) * r, -0.004 - drop * front * front, Math.sin(a) * r * 0.97);
      g.add(b);
    }
  }
  hold.add(g);
  return { hold, bell: null };
}

// ---------- всё вместе ----------

export function addBeauty(vrm, opts = {}, { gradientMap, faceDecor } = {}) {
  const o = { hairShine: 1, hairDepth: 1, eyes: true, eyeshadow: 1, lips: 1, blush: 1, blushBase: 0.12, kanzashi: true, choker: true, ...opts };
  vrm.scene.updateMatrixWorld(true);
  const report = [];
  report.push(`волосы: ${prettyHair(vrm.scene, o)} материалов`);
  if (o.eyes) report.push(`глаза: ${prettyEyes(vrm.scene)} радужки`);
  const face = o.blush > 0 || o.eyeshadow > 0 || o.lips > 0 || faceDecor ? faceLayer(vrm.scene, o, faceDecor) : null;
  const kz = o.kanzashi ? kanzashi(vrm, gradientMap) : null;
  const ch = o.choker ? choker(vrm, gradientMap, o.chokerStyle ?? 'bell') : null;
  const swingers = [...(kz?.dangles ?? []), ...(ch?.bell ? [ch.bell] : [])];
  for (const h of [kz?.hold, ch?.hold]) h?.traverse((m) => { m.frustumCulled = false; });
  const prev = new Map();
  let blush = 0;

  /**
   * Каждый кадр: подвески и колокольчик — маятники в мире (раскачивает ускорение держателя, тянет вниз гравитация,
   * гасит трение); румянец — к цели: база + смущение (S.shyUntil) + радость + «гладят» (S.pleased).
   */
  function update(dt, S) {
    const step = Math.min(dt, 1 / 30);
    for (const s of swingers) {
      const holder = s.pivot.parent;
      const p = s.pivot.getWorldPosition(new THREE.Vector3());
      const last = prev.get(s) ?? { p: p.clone(), v: new THREE.Vector3() };
      const vel = p.clone().sub(last.p).divideScalar(Math.max(step, 1e-3));
      const acc = vel.clone().sub(last.v).divideScalar(Math.max(step, 1e-3));
      prev.set(s, { p, v: vel });
      // ускорение в осях держателя (мир в покое): X — вбок, Z — вперёд
      const q = holder.getWorldQuaternion(new THREE.Quaternion()).invert();
      // резкость движения в точке подвеса (м/с², сглажено): по ней звенит бубенчик — шарик внутри бьёт в стенку
      s.shake = (s.shake ?? 0) * 0.6 + Math.min(acc.length(), 60) * 0.4;
      const a = acc.clampLength(0, 30).applyQuaternion(q);
      const kg = s.gain ?? 0.02;
      s.v += (-a.x * kg - s.a * 90 - s.v * 6) * step;
      s.bv += (a.z * kg - s.b * 90 - s.bv * 6) * step;
      s.a += s.v * step;
      s.b += s.bv * step;
      s.pivot.rotation.set(THREE.MathUtils.clamp(s.b, -0.6, 0.6), 0, THREE.MathUtils.clamp(s.a, -0.6, 0.6));
    }
    if (face && S) {
      const shy = S.shyUntil && S.t < S.shyUntil ? 1 : 0;
      const want = o.blush * Math.min(1, o.blushBase + 0.85 * shy + 0.25 * (S.emotion?.happy ?? 0) + 0.6 * (S.pleased ?? 0));
      blush += (want - blush) * Math.min(1, dt * 3);
      face.setBlush(blush);
    }
  }

  return {
    update,
    report: report.join(', '),
    kanzashi: kz?.hold ?? null,
    choker: ch?.hold ?? null,
    setBlush: (k) => face?.setBlush(k),
    /** Резкость движения колокольчика чокера, м/с² — по ней он звенит (klava.js jingle). */
    // звенит только колокольчик: колечко и жемчуг — молчат
    bellShake: () => (ch?.bell && (o.chokerStyle ?? 'bell') === 'bell' ? ch.bell.shake ?? 0 : 0),
  };
}
