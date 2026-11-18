// Лицо Клавы, 29.09 (друг: «моргание, лицо поправить на более анатомичное, добавить красоты и шарма, синий глаз сделать
// киберимплантом»). Всё поверх модели, Vita.vrm не меняется:
//  - киберглаз: голубая радужка (её левый глаз) перерисована в механическую — металлическое кольцо, диафрагма из
//    лепестков, кольцо сенсоров; светящийся слой (emissiveMap) живёт: крутятся дуги, бежит огонёк по делениям, зрачок-щель
//    пульсирует с голосом, изредка проходит луч сканера; когда она думает — кольцо крутится быстрее;
//  - кожа у импланта: тонкие неоновые дорожки вокруг глазницы в стиле её кибер-узоров на щеках, «порты»-точки;
//  - шарм: родинка под зелёным глазом, блик на нижней губе, клычок (кошачий «яэба») — виден, когда рот открыт;
//  - форма: V-подбородок скруглён в мягкую U, кончик чуть приподнят и выдвинут вперёд (в профиль был срезан).
// Рисунки на коже ложатся по настоящей поверхности лица: точки задаются в 3D и лучом переводятся в координаты текстуры.
import * as THREE from 'three';

const TAU = Math.PI * 2;
const smooth = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

function materials(root, rx) {
  const out = new Set();
  root.traverse((o) => { if (o.isMesh) for (const m of [].concat(o.material)) if (m && rx.test(m.name ?? '')) out.add(m); });
  return [...out];
}
function meshesWith(root, rx) {
  const out = [];
  root.traverse((o) => { if (o.isMesh && [].concat(o.material).some((m) => rx.test(m?.name ?? ''))) out.push(o); });
  return out;
}
function canvasOf(tex) {
  const img = tex?.image;
  if (!img?.width) return null;
  const c = document.createElement('canvas');
  c.width = img.width; c.height = img.height;
  c.getContext('2d').drawImage(img, 0, 0);
  return c;
}
function swapMap(root, old, canvas) {
  const tex = new THREE.CanvasTexture(canvas);
  for (const k of ['flipY', 'wrapS', 'wrapT', 'minFilter', 'magFilter', 'anisotropy', 'colorSpace', 'generateMipmaps']) tex[k] = old[k];
  tex.needsUpdate = true;
  root.traverse((o) => { if (o.isMesh) for (const m of [].concat(o.material)) if (m?.map === old) { m.map = tex; m.needsUpdate = true; } });
  return tex;
}

/** Центр глаза (белок) и его края в мире: side +1 — её левый (+X), −1 — правый. */
function eyeInfo(vrm, side) {
  const [white] = meshesWith(vrm.scene, /EyeWhite/);
  if (!white) return null;
  const p = white.geometry.attributes.position, v = new THREE.Vector3();
  const c = new THREE.Vector3();
  let n = 0, minX = 9, maxX = -9, minY = 9, maxY = -9, maxZ = -9;
  for (let i = 0; i < p.count; i++) {
    white.localToWorld(v.fromBufferAttribute(p, i));
    if (Math.sign(v.x) !== side) continue;
    c.add(v); n++;
    minX = Math.min(minX, v.x); maxX = Math.max(maxX, v.x); minY = Math.min(minY, v.y); maxY = Math.max(maxY, v.y); maxZ = Math.max(maxZ, v.z);
  }
  if (!n) return null;
  c.divideScalar(n);
  return { c, w: maxX - minX, h: maxY - minY, front: maxZ, inner: side > 0 ? minX : maxX, outer: side > 0 ? maxX : minX };
}

/**
 * Точка на коже лица → координаты текстуры кожи: луч спереди назад по треугольникам в позе покоя (как в файле модели),
 * UV — по барицентрам. Свой расчёт: у SkinnedMesh.raycast до первого кадра нет матриц костей, и попадание уезжало.
 */
function makeSkinUv(vrm) {
  const tris = [];
  for (const mesh of meshesWith(vrm.scene, /Face_00_SKIN/)) {
    const g = mesh.geometry, p = g.attributes.position, uv = g.attributes.uv, ix = g.index?.array;
    if (!uv || !ix) continue;
    const w = [];
    const v = new THREE.Vector3();
    for (let i = 0; i < p.count; i++) { mesh.localToWorld(v.fromBufferAttribute(p, i)); w.push(v.x, v.y, v.z); }
    for (let t = 0; t < ix.length; t += 3) tris.push({ w, uv, a: ix[t], b: ix[t + 1], c: ix[t + 2] });
  }
  return (x, y) => {
    let best = null;
    for (const T of tris) {
      const { w, a, b, c } = T;
      const ax = w[a * 3], ay = w[a * 3 + 1], bx = w[b * 3], by = w[b * 3 + 1], cx = w[c * 3], cy = w[c * 3 + 1];
      // проекция на плоскость XY: луч идёт вдоль −Z
      const d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
      if (Math.abs(d) < 1e-12) continue;
      const l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
      const l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
      const l3 = 1 - l1 - l2;
      if (l1 < 0 || l2 < 0 || l3 < 0) continue;
      // только лицевая сторона к зрителю: в дырке глаза луч иначе попадал в затылок изнутри (UV с макушки)
      if ((bx - ax) * (cy - ay) - (by - ay) * (cx - ax) <= 0) continue;
      const z = l1 * w[a * 3 + 2] + l2 * w[b * 3 + 2] + l3 * w[c * 3 + 2];
      if (best && z <= best.z) continue;                       // ближняя к зрителю поверхность
      const uv = T.uv;
      best = { z, u: l1 * uv.getX(a) + l2 * uv.getX(b) + l3 * uv.getX(c), v: l1 * uv.getY(a) + l2 * uv.getY(b) + l3 * uv.getY(c) };
    }
    return best;
  };
}

// ---------- киберглаз ----------

/** Рамка радужки на текстуре, в которую попадает точка (u, v) — по непрозрачным пикселям. */
function irisBoxAt(ctx, w, h, u, v) {
  const px = ctx.getImageData(0, 0, w, h).data;
  const half = u < 0.5 ? [0, w / 2] : [w / 2, w];
  let x0 = w, y0 = h, x1 = 0, y1 = 0;
  for (let y = 0; y < h * 0.85; y += 2)
    for (let x = half[0]; x < half[1]; x += 2)
      if (px[(y * w + x) * 4 + 3] > 200) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
  if (x1 <= x0) return null;
  return { cx: (x0 + x1) / 2, cy: (y0 + y1) / 2, rx: (x1 - x0) / 2, ry: (y1 - y0) / 2 };
}

/** Средний цвет радужки в рамке. */
function boxColor(ctx, b) {
  const d = ctx.getImageData(Math.round(b.cx - b.rx * 0.6), Math.round(b.cy - b.ry * 0.6), Math.round(b.rx * 1.2), Math.round(b.ry * 1.2)).data;
  let r = 0, g = 0, bl = 0, n = 0;
  for (let i = 0; i < d.length; i += 16) if (d[i + 3] > 200) { r += d[i]; g += d[i + 1]; bl += d[i + 2]; n++; }
  return n ? [r / n, g / n, bl / n] : [0, 0, 0];
}

/** Постоянная часть импланта — прямо в текстуру радужки. */
function paintImplant(g, b) {
  const { cx, cy, rx, ry } = b;
  g.save();
  g.beginPath();
  g.ellipse(cx, cy, rx * 0.985, ry * 0.985, 0, 0, TAU);
  g.clip();
  g.translate(cx, cy);
  g.scale(rx, ry);                      // дальше — в единичном круге
  const r0 = 1;
  // тёмное стекло объектива
  const bg = g.createRadialGradient(0, 0.1, 0.05, 0, 0, r0);
  bg.addColorStop(0, '#0d2233'); bg.addColorStop(0.55, '#08131d'); bg.addColorStop(1, '#16283a');
  g.fillStyle = bg;
  g.fillRect(-1, -1, 2, 2);
  // шлифованное металлическое кольцо
  const metal = g.createConicGradient(-0.6, 0, 0);
  for (const [t, c] of [[0, '#b9c9d8'], [0.12, '#4a5d72'], [0.3, '#d6e2ee'], [0.45, '#3d5064'], [0.62, '#9fb2c6'], [0.8, '#34465a'], [1, '#b9c9d8']]) metal.addColorStop(t, c);
  g.fillStyle = metal;
  g.beginPath(); g.arc(0, 0, 1, 0, TAU); g.arc(0, 0, 0.86, 0, TAU, true); g.fill();
  g.strokeStyle = 'rgba(10,18,28,0.9)'; g.lineWidth = 0.025;
  g.beginPath(); g.arc(0, 0, 0.855, 0, TAU); g.stroke();
  // винтики на кольце
  g.fillStyle = '#1b2735';
  for (let i = 0; i < 6; i++) { const a = (i / 6) * TAU + 0.26; g.beginPath(); g.arc(Math.cos(a) * 0.93, Math.sin(a) * 0.93, 0.028, 0, TAU); g.fill(); }
  // кольцо сенсоров: 24 деления
  g.fillStyle = '#0a1622';
  g.beginPath(); g.arc(0, 0, 0.82, 0, TAU); g.arc(0, 0, 0.6, 0, TAU, true); g.fill();
  for (let i = 0; i < 24; i++) {
    const a = (i / 24) * TAU;
    g.save(); g.rotate(a);
    g.fillStyle = i % 6 === 0 ? '#7b90a6' : '#3f5468';
    g.fillRect(0.63, -0.022, i % 6 === 0 ? 0.16 : 0.1, 0.044);
    g.restore();
  }
  // диафрагма: 8 лепестков вокруг зрачка
  const blades = 8;
  for (let i = 0; i < blades; i++) {
    const a = (i / blades) * TAU;
    g.save(); g.rotate(a);
    g.beginPath();
    g.moveTo(0.22, -0.02);
    g.quadraticCurveTo(0.42, -0.24, 0.6, -0.1);
    g.lineTo(0.6, 0.16);
    g.quadraticCurveTo(0.38, 0.1, 0.2, 0.12);
    g.closePath();
    g.fillStyle = i % 2 ? '#2b3f55' : '#35506a';
    g.fill();
    g.strokeStyle = 'rgba(160,190,215,0.55)'; g.lineWidth = 0.015; g.stroke();
    g.restore();
  }
  // зрачок: чёрный, с тонким ободком
  g.fillStyle = '#03060a';
  g.beginPath(); g.arc(0, 0, 0.23, 0, TAU); g.fill();
  g.strokeStyle = 'rgba(120,230,255,0.5)'; g.lineWidth = 0.02;
  g.beginPath(); g.arc(0, 0, 0.23, 0, TAU); g.stroke();
  // тень от верхнего века (как у второго глаза) и отблеск стекла снизу
  const top = g.createLinearGradient(0, -1, 0, 0);
  top.addColorStop(0, 'rgba(0,4,12,0.6)'); top.addColorStop(1, 'rgba(0,4,12,0)');
  g.fillStyle = top; g.fillRect(-1, -1, 2, 1);
  const low = g.createRadialGradient(0, 0.62, 0, 0, 0.62, 0.55);
  low.addColorStop(0, 'rgba(140,220,255,0.28)'); low.addColorStop(1, 'rgba(140,220,255,0)');
  g.fillStyle = low; g.fillRect(-1, 0, 2, 1);
  g.restore();
}

/**
 * Живой свет импланта (emissiveMap): рисуется в свой небольшой холст в тех же долях UV. t — время, s — состояние:
 * speed (скорость кольца), voice (0..1 громкость речи), scan (0..1 проход луча), glitch (0..1), look (0..1 «фокус»).
 */
function drawGlow(g, b, t, s) {
  const { cx, cy, rx, ry } = b;
  g.clearRect(cx - rx - 2, cy - ry - 2, rx * 2 + 4, ry * 2 + 4);
  g.save();
  g.beginPath(); g.ellipse(cx, cy, rx * 0.985, ry * 0.985, 0, 0, TAU); g.clip();
  g.translate(cx, cy); g.scale(rx, ry);
  const flick = 1 - s.glitch * (0.4 + 0.6 * Math.random());
  const pulse = (0.78 + 0.22 * Math.sin(t * 2.1)) * flick;
  g.lineCap = 'round';
  // тонкое светящееся кольцо у кромки металла
  g.strokeStyle = `rgba(84,240,255,${0.75 * pulse})`;
  g.lineWidth = 0.035;
  g.beginPath(); g.arc(0, 0, 0.84, 0, TAU); g.stroke();
  // три дуги крутятся по кольцу сенсоров
  const rot = s.angle;
  g.strokeStyle = `rgba(141,255,224,${0.9 * flick})`;
  g.lineWidth = 0.07;
  for (let k = 0; k < 3; k++) {
    const a0 = rot + (k / 3) * TAU;
    g.beginPath(); g.arc(0, 0, 0.71, a0, a0 + 0.55); g.stroke();
  }
  // огонёк бежит по делениям в обратную сторону
  const chase = (-rot * 1.7) % TAU;
  for (let i = 0; i < 24; i++) {
    const a = (i / 24) * TAU;
    let d = Math.abs(((a - chase) % TAU + TAU) % TAU - Math.PI) ;
    d = Math.PI - d;                                   // угловое расстояние до огонька
    const k = Math.max(0, 1 - d / 0.9);
    if (k <= 0.02) continue;
    g.save(); g.rotate(a);
    g.fillStyle = `rgba(120,245,255,${k * flick})`;
    g.fillRect(0.63, -0.022, i % 6 === 0 ? 0.16 : 0.1, 0.044);
    g.restore();
  }
  // зрачок-щель: кошачий, светится мятным, дышит с голосом
  const w = 0.055 + 0.03 * s.look, h = 0.17 + 0.05 * s.voice;
  const core = g.createRadialGradient(0, 0, 0, 0, 0, 0.25);
  core.addColorStop(0, `rgba(170,255,235,${(0.55 + 0.45 * s.voice) * flick})`);
  core.addColorStop(1, 'rgba(60,200,255,0)');
  g.fillStyle = core;
  g.beginPath(); g.arc(0, 0, 0.25, 0, TAU); g.fill();
  g.fillStyle = `rgba(210,255,245,${0.95 * flick})`;
  g.beginPath(); g.ellipse(0, 0, w, h, 0, 0, TAU); g.fill();
  // луч сканера сверху вниз
  if (s.scan > 0 && s.scan < 1) {
    const y = -1 + 2 * s.scan;
    const gr = g.createLinearGradient(0, y - 0.12, 0, y + 0.12);
    gr.addColorStop(0, 'rgba(84,240,255,0)'); gr.addColorStop(0.5, 'rgba(160,255,250,0.85)'); gr.addColorStop(1, 'rgba(84,240,255,0)');
    g.fillStyle = gr;
    g.fillRect(-1, y - 0.12, 2, 0.24);
  }
  g.restore();
}

function cyberEye(vrm, o) {
  const [iris] = materials(vrm.scene, /EyeIris/);
  const ic = iris?.map ? canvasOf(iris.map) : null;
  if (!ic) return null;
  const g = ic.getContext('2d');
  // какой глаз: по умолчанию голубой (у Vita это её левый); side из настроек — «left»/«right»
  const [irisMesh] = meshesWith(vrm.scene, /EyeIris/);
  const uvOf = (side) => {
    const p = irisMesh.geometry.attributes.position, uv = irisMesh.geometry.attributes.uv, v = new THREE.Vector3();
    let u = 0, vv = 0, n = 0;
    for (let i = 0; i < p.count; i++) { irisMesh.localToWorld(v.fromBufferAttribute(p, i)); if (Math.sign(v.x) === side) { u += uv.getX(i); vv += uv.getY(i); n++; } }
    return n ? [u / n, vv / n] : null;
  };
  const cand = [1, -1].map((side) => {
    const uv = uvOf(side);
    const b = uv ? irisBoxAt(g, ic.width, ic.height, uv[0], uv[1]) : null;
    const col = b ? boxColor(g, b) : [0, 0, 0];
    return { side, b, blue: col[2] - col[0] };
  }).filter((c) => c.b);
  if (!cand.length) return null;
  const pick = o.side === 'left' ? cand.find((c) => c.side > 0) : o.side === 'right' ? cand.find((c) => c.side < 0) : cand.sort((a, b) => b.blue - a.blue)[0];
  if (!pick) return null;
  paintImplant(g, pick.b);
  swapMap(vrm.scene, iris.map, ic);
  // светящийся слой: холст вдвое меньше текстуры, та же раскладка UV
  const k = 0.5;
  const gc = document.createElement('canvas');
  gc.width = Math.round(ic.width * k); gc.height = Math.round(ic.height * k);
  const gg = gc.getContext('2d');
  const gb = { cx: pick.b.cx * k, cy: pick.b.cy * k, rx: pick.b.rx * k, ry: pick.b.ry * k };
  const glow = new THREE.CanvasTexture(gc);
  glow.flipY = iris.map.flipY;
  glow.colorSpace = THREE.SRGBColorSpace;
  iris.emissiveMap = glow;
  iris.emissive?.setRGB(1, 1, 1);
  iris.emissiveIntensity = o.glow ?? 1.0;
  iris.needsUpdate = true;
  const st = { angle: 0, voice: 0, scan: 0, glitch: 0, look: 0, nextScan: 4, nextGlitch: 25, acc: 0 };
  return {
    side: pick.side,
    update(dt, S) {
      const thinking = S?.state === 'thinking';
      st.angle += dt * (thinking ? 2.6 : 0.35);
      st.voice += ((S?.speaking ? Math.min(1, (S.mouth?.aa ?? 0) * 2) : 0) - st.voice) * Math.min(1, dt * 10);
      st.look += (((S?.t ?? 0) - (S?.lastCursorAt ?? -99) < 2 ? 1 : 0) - st.look) * Math.min(1, dt * 3);
      const t = S?.t ?? 0;
      if (st.scan > 0) { st.scan += dt / 0.55; if (st.scan >= 1) st.scan = 0; }
      else if (t > st.nextScan) { st.scan = 0.001; st.nextScan = t + (thinking ? 1.5 : 7 + Math.random() * 9); }
      if (st.glitch > 0) st.glitch = Math.max(0, st.glitch - dt * 6);
      else if (t > st.nextGlitch) { st.glitch = 1; st.nextGlitch = t + 20 + Math.random() * 30; }
      st.acc += dt;
      if (st.acc < 1 / 30) return;             // свет перерисовываем 30 раз в секунду — хватает и дёшево
      st.acc = 0;
      drawGlow(gg, gb, t, st);
      glow.needsUpdate = true;
    },
  };
}

// ---------- рисунки на коже ----------

/**
 * Возвращает функцию (ctx, W, H) — beauty.js зовёт её, рисуя постоянную основу кожи (до румянца).
 * Точки считаются в 3D от глаз и рта и лучом переводятся в UV, поэтому рисунок не плывёт от раскладки текстуры.
 */
function skinDecor(vrm, o, implantSide, mouth) {
  const uvAt = makeSkinUv(vrm);
  const eyes = { 1: eyeInfo(vrm, 1), [-1]: eyeInfo(vrm, -1) };
  return (g, W, H) => {
    const toPx = (p) => (p ? [p.u * W, p.v * H] : null);
    // дорожки импланта вокруг глазницы: дуга снизу от внутреннего угла к внешнему и дальше к виску, короткая сверху
    const e = implantSide ? eyes[implantSide] : null;
    if (e && o.implantSkin !== false) {
      const s = implantSide;
      // точки дорожки; скачок в UV (край лица уходит на висок в другом месте текстуры) обрывает дорожку
      const path = (pts) => {
        const out = [];
        for (const [dx, dy] of pts) {
          const p = toPx(uvAt(e.c.x + dx * s, e.c.y + dy));
          if (!p) continue;
          const last = out[out.length - 1];
          if (last && Math.hypot(p[0] - last[0], p[1] - last[1]) > W * 0.06) break;
          out.push(p);
        }
        return out;
      };
      const hw = e.w / 2, hh = e.h / 2;
      // q — наружу (к виску), y — вверх; от центра белка: под нижним веком от внутренней трети к внешнему углу и к виску,
      // короткая дорожка над внешним углом
      const lower = [];
      for (let i = 0; i <= 14; i++) {
        const t = i / 14;
        const q = -0.35 * hw + t * 1.35 * hw;
        lower.push([q, -hh * (0.98 - 0.55 * t * t)]);
      }
      lower.push([hw * 1.12, -hh * 0.36], [hw * 1.24, -hh * 0.3]);
      const upper = [[hw * 0.82, hh * 1.25], [hw * 1.02, hh * 1.1], [hw * 1.18, hh * 1.02]];
      const stroke = (pts, wBase) => {
        if (pts.length < 2) return;
        g.lineJoin = g.lineCap = 'round';
        for (const [c, w] of [['rgba(38,76,190,0.95)', wBase * 2.6], ['rgba(95,255,215,0.95)', wBase * 1.1], ['rgba(230,255,248,0.9)', wBase * 0.45]]) {
          g.strokeStyle = c; g.lineWidth = w;
          g.beginPath(); g.moveTo(...pts[0]); for (const p of pts.slice(1)) g.lineTo(...p); g.stroke();
        }
      };
      const wBase = W / 1024 * 2.2;
      const lp = path(lower), up = path(upper);
      stroke(lp, wBase);
      stroke(up, wBase);
      // «порты» — квадратики-узлы на дорожке
      g.fillStyle = 'rgba(38,76,190,0.95)';
      for (const i of [3, 8, 13]) if (lp[i]) { g.fillRect(lp[i][0] - wBase * 2.2, lp[i][1] - wBase * 2.2, wBase * 4.4, wBase * 4.4); }
      g.fillStyle = 'rgba(140,255,230,0.95)';
      for (const i of [3, 8, 13]) if (lp[i]) { g.fillRect(lp[i][0] - wBase * 1.1, lp[i][1] - wBase * 1.1, wBase * 2.2, wBase * 2.2); }
    }
    // родинка под вторым глазом, чуть к внешнему углу
    const other = eyes[-(implantSide || 1)];
    if (other && o.beautyMark !== false) {
      const s = -(implantSide || 1);
      const p = toPx(uvAt(other.c.x + s * other.w * 0.14, other.c.y - other.h * 0.62));
      if (p) {
        const r = W / 1024 * 4.6;
        const gr = g.createRadialGradient(p[0], p[1], 0, p[0], p[1], r);
        gr.addColorStop(0, 'rgba(96,52,46,0.95)'); gr.addColorStop(0.7, 'rgba(110,62,54,0.8)'); gr.addColorStop(1, 'rgba(120,70,60,0)');
        g.fillStyle = gr;
        g.beginPath(); g.arc(p[0], p[1], r, 0, TAU); g.fill();
      }
    }
    // блик на нижней губе
    if (mouth && o.lipGloss !== false) {
      const p = toPx(uvAt(mouth.x + 0.003, mouth.y - 0.0045));
      if (p) {
        const r = W / 1024 * 7;
        g.save();
        g.translate(p[0], p[1]); g.scale(1.6, 0.55);
        const gr = g.createRadialGradient(0, 0, 0, 0, 0, r);
        gr.addColorStop(0, 'rgba(255,248,250,0.55)'); gr.addColorStop(1, 'rgba(255,248,250,0)');
        g.fillStyle = gr;
        g.beginPath(); g.arc(0, 0, r, 0, TAU); g.fill();
        g.restore();
      }
    }
  };
}

// ---------- клычок ----------

/**
 * Клычок: маленький конус остриём вниз у верхней губы справа (кошачий «яэба»). Когда рот закрыт — чуть выглядывает над
 * нижней губой, открыт — висит у верхних зубов. Едет вместе с верхней губой: каждый кадр берётся смещение ближайшей
 * вершины губы от морфов (aa/oh/ih/…/челюсть), иначе при открытом рте он «висел» посреди рта.
 */
function fang(vrm, mouth, o, gradientMap) {
  const head = vrm.humanoid.getRawBoneNode('head');
  if (!head || !mouth) return null;
  const s = o.fangSide === 'left' ? 1 : -1;                      // по умолчанию справа (её правая сторона)
  const H = o.fangH ?? 0.0027;
  const geo = new THREE.ConeGeometry(0.00115, H, 10);
  geo.rotateX(Math.PI);                                          // остриё вниз
  geo.translate(0, -H / 2, 0);
  const m = new THREE.Mesh(geo, new THREE.MeshToonMaterial({ color: 0xfbfbff, gradientMap }));
  m.scale.set(1, 1, 0.7);
  m.frustumCulled = false;
  const hold = new THREE.Group();
  head.add(hold);
  head.updateWorldMatrix(true, false);
  const world = new THREE.Vector3(mouth.x + s * (o.fangX ?? 0.0062), mouth.y + (o.fangY ?? 0.0012), mouth.z - (o.fangZ ?? 0.0022));
  hold.position.copy(head.worldToLocal(world.clone()));
  hold.quaternion.copy(head.getWorldQuaternion(new THREE.Quaternion()).invert());
  hold.add(m);
  // ближайшая вершина верхней губы на коже — за ней и поедет
  let best = null;
  const v = new THREE.Vector3();
  for (const mesh of meshesWith(vrm.scene, /Face_00_SKIN/)) {
    const p = mesh.geometry.attributes.position;
    for (let i = 0; i < p.count; i++) {
      mesh.localToWorld(v.fromBufferAttribute(p, i));
      if (v.y < mouth.y - 0.0005) continue;
      const d = v.distanceToSquared(world);
      if (!best || d < best.d) best = { d, mesh, i };
    }
  }
  const q = best ? best.mesh.getWorldQuaternion(new THREE.Quaternion()) : null;
  const delta = new THREE.Vector3(), tmp = new THREE.Vector3();
  return {
    hold,
    update() {
      if (!best) return;
      const { mesh, i } = best;
      const infl = mesh.morphTargetInfluences, morphs = mesh.geometry.morphAttributes.position;
      delta.set(0, 0, 0);
      for (let k = 0; k < infl.length; k++) if (infl[k] > 0.001) delta.add(tmp.fromBufferAttribute(morphs[k], i).multiplyScalar(infl[k]));
      m.position.copy(delta.applyQuaternion(q));
    },
  };
}

// ---------- форма лица ----------

/**
 * Мягче подбородок: V → U. Сдвиги по месту (база сетки кожи, до морфов): кончик вверх и вперёд, бока нижней челюсти
 * чуть наружу. Вершины у края сетки (шов с шеей) не трогаем. mouth — центр рта в координатах сетки, fwd — знак «вперёд».
 */
function reshapeChin(skinMeshes, mouth, fwd, o) {
  const lift = o.chinLift ?? 0.0028, round = o.chinRound ?? 0.003, push = o.chinForward ?? 0.0015;
  let moved = 0;
  for (const mesh of skinMeshes) {
    const g = mesh.geometry, pos = g.attributes.position;
    const rim = rimDistance(g);
    let minY = 9;
    for (let i = 0; i < pos.count; i++) minY = Math.min(minY, pos.getY(i));
    if (minY > mouth.y - 0.02) continue;                       // в этой сетке нет подбородка
    for (let i = 0; i < pos.count; i++) {
      const x = pos.getX(i) - mouth.x, y = pos.getY(i), z = pos.getZ(i);
      const front = (z - mouth.z) * fwd;                      // 0 у поверхности рта, минус — вглубь
      const below = smooth(mouth.y - 0.01, mouth.y - 0.03, y);   // ниже губ
      if (below <= 0) continue;
      const keep = smooth(0.006, 0.02, rim[i]) * smooth(-0.045, -0.02, front);
      if (keep <= 0) continue;
      const tip = Math.exp(-((x / 0.011) ** 2)) * below;        // середина подбородка
      const side = Math.exp(-(((Math.abs(x) - 0.02) / 0.009) ** 2)) * below;   // бока нижней челюсти
      pos.setY(i, y + lift * tip * keep);
      pos.setX(i, pos.getX(i) + Math.sign(x) * round * side * keep);
      pos.setZ(i, z + fwd * push * tip * keep);
      moved++;
    }
    pos.needsUpdate = true;
    g.computeVertexNormals();
    g.computeBoundingSphere();
  }
  return moved;
}

function rimDistance(g) {
  const pos = g.attributes.position, ix = g.index?.array;
  const out = new Float32Array(pos.count).fill(1);
  if (!ix) return out;
  const edges = new Map();
  const key = (p, q) => (p < q ? p * 1048576 + q : q * 1048576 + p);
  for (let t = 0; t < ix.length; t += 3)
    for (const [p, q] of [[ix[t], ix[t + 1]], [ix[t + 1], ix[t + 2]], [ix[t + 2], ix[t]]]) { const k = key(p, q); edges.set(k, (edges.get(k) ?? 0) + 1); }
  const byPos = new Map();
  const pk = (i) => `${pos.getX(i).toFixed(5)},${pos.getY(i).toFixed(5)},${pos.getZ(i).toFixed(5)}`;
  for (let i = 0; i < pos.count; i++) byPos.set(pk(i), (byPos.get(pk(i)) ?? 0) + 1);
  const rim = [];
  for (const [k, c] of edges) if (c === 1) for (const i of [Math.floor(k / 1048576), k % 1048576]) if (byPos.get(pk(i)) === 1) rim.push(i);
  const pts = [...new Set(rim)].map((i) => [pos.getX(i), pos.getY(i), pos.getZ(i)]);
  for (let i = 0; i < pos.count; i++) {
    let best = 1;
    const x = pos.getX(i), y = pos.getY(i), z = pos.getZ(i);
    for (const [a, b, c] of pts) { const d = (x - a) ** 2 + (y - b) ** 2 + (z - c) ** 2; if (d < best) best = d; }
    out[i] = Math.sqrt(best);
  }
  return out;
}

/** Центр рта в координатах сетки кожи и в мире (по вершинам, которые двигает морф «aa»). */
function mouthOf(vrm) {
  const ex = vrm.expressionManager?.getExpression('aa');
  for (const b of ex?._binds ?? []) for (const m of b.primitives ?? []) {
    if (![].concat(m.material).some((x) => /SKIN/.test(x?.name ?? ''))) continue;
    const d = m.geometry.morphAttributes.position?.[b.index];
    if (!d) continue;
    const pos = m.geometry.attributes.position;
    const local = new THREE.Vector3(), all = new THREE.Vector3(), v = new THREE.Vector3();
    let n = 0;
    for (let i = 0; i < pos.count; i++) {
      all.add(v.fromBufferAttribute(pos, i));
      if (Math.hypot(d.getX(i), d.getY(i), d.getZ(i)) > 0.002) { local.add(v); n++; }
    }
    if (n < 10) continue;
    local.divideScalar(n); all.divideScalar(pos.count);
    // «передняя» точка рта: самая выдвинутая вперёд среди двигающихся вершин у середины
    const fwd = Math.sign(local.z - all.z) || -1;
    let frontZ = local.z;
    for (let i = 0; i < pos.count; i++) {
      if (Math.hypot(d.getX(i), d.getY(i), d.getZ(i)) <= 0.002 || Math.abs(pos.getX(i) - local.x) > 0.006) continue;
      if ((pos.getZ(i) - frontZ) * fwd > 0) frontZ = pos.getZ(i);
    }
    const localFront = new THREE.Vector3(local.x, local.y, frontZ);
    const world = m.localToWorld(localFront.clone());
    return { local, fwd, world, mesh: m };
  }
  return null;
}

export function addFacePlus(vrm, opts = {}, { gradientMap } = {}) {
  const o = { cyberEye: true, implantSkin: true, beautyMark: true, lipGloss: true, fang: true, chin: true, ...opts };
  vrm.scene.updateMatrixWorld(true);
  const report = [];
  const mouth = mouthOf(vrm);
  if (o.chin && mouth) {
    const skins = meshesWith(vrm.scene, /Face_00_SKIN/);
    report.push(`подбородок: ${reshapeChin(skins, mouth.local, mouth.fwd, o)} вершин`);
  }
  const eye = o.cyberEye ? cyberEye(vrm, o) : null;
  if (eye) report.push(`киберглаз: ${eye.side > 0 ? 'левый' : 'правый'}`);
  const fangs = o.fang && mouth ? fang(vrm, mouth.world, o, gradientMap) : null;
  if (fangs) report.push('клычок');
  const decor = skinDecor(vrm, o, eye?.side ?? 0, mouth?.world ?? null);
  return {
    report: report.join(', '),
    /** Рисование на коже — отдаётся в beauty.js (основа кожи до румянца). */
    decor,
    update(dt, S) { eye?.update(dt, S); },
    /** После vrm.update (морфы уже выставлены): клычок за верхней губой. */
    afterUpdate() { fangs?.update(); },
    fang: fangs?.hold ?? null,
    /** Центр губ спереди, мир в покое (для пара изо рта и еды). */
    mouth: mouth?.world ?? null,
    eye,
  };
}

export { eyeInfo, makeSkinUv, mouthOf };
