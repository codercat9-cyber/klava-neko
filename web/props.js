// Обстановка и дела Клавы — её собственные хотелки из ночного разговора 30.09 (Сова: «чё себе ещё хочешь?»):
//  - мягкий коврик под лапки (всегда, выключается в klava.json: rug=false);
//  - массажное кресло (chair): сидит, откинувшись, ноги на подставке, ролики ходят по спинке;
//  - кровать с балдахином (bed — сидит в подушках под пледом, sleep — спит: глаза закрыты, «Z-z-z»);
//  - купель с пеной (bath) — деревянная японская, пена по грудь, уточка, пар;
//  - золотой микрофон на «полустойке» в руке (mic, стойка Фредди Меркьюри) — когда поёт; большие наушники
//    (headphones) — когда слушает музыку;
//  - синтезатор (synth) — лапы ходят по клавишам за мелодией; скрипка (violin) — «Паганини», смычок по нотам;
//  - бар-тележка с киберпивом и бронзовая пепельница — сами появляются, пока пьёт и курит; танец (dance) под бит.
// Мебель — как игровое место (gamedesk.js): группа у ног модели, оси как у жестов (+X — её ЛЕВАЯ сторона, +Y вверх,
// +Z к зрителю), появляется «вырастая» из пола. Поза — через gestures.define (сидение с высотой таза sitDrop и
// стопами feet — gestures.js), предмет ставится в хуке after, когда руки уже встали по IK.
import * as THREE from 'three';

const smooth = (x) => (x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x));
const ramp = (u, a, b) => smooth((u - a) / Math.max(1e-4, b - a));
const TAU = Math.PI * 2;
const V = (x, y, z) => new THREE.Vector3(x, y, z);

// ---------- материалы и формы ----------

function kit(gradientMap) {
  const toon = (color, extra = {}) => new THREE.MeshToonMaterial({ color, gradientMap, ...extra });
  const glow = (color, extra = {}) => new THREE.MeshBasicMaterial({ color, ...extra });
  const box = (w, h, d, mat, x = 0, y = 0, z = 0) => {
    const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat);
    m.position.set(x, y, z);
    return m;
  };
  const cyl = (rt, rb, h, mat, x = 0, y = 0, z = 0, seg = 24) => {
    const m = new THREE.Mesh(new THREE.CylinderGeometry(rt, rb, h, seg), mat);
    m.position.set(x, y, z);
    return m;
  };
  /** Пухлая подушка: скруглённый прямоугольник w×h, толщина d (вдоль Z), с фаской. */
  const pillow = (w, h, d, r, mat) => {
    const s = new THREE.Shape();
    const x0 = -w / 2, y0 = -h / 2;
    s.moveTo(x0 + r, y0);
    s.lineTo(x0 + w - r, y0); s.quadraticCurveTo(x0 + w, y0, x0 + w, y0 + r);
    s.lineTo(x0 + w, y0 + h - r); s.quadraticCurveTo(x0 + w, y0 + h, x0 + w - r, y0 + h);
    s.lineTo(x0 + r, y0 + h); s.quadraticCurveTo(x0, y0 + h, x0, y0 + h - r);
    s.lineTo(x0, y0 + r); s.quadraticCurveTo(x0, y0, x0 + r, y0);
    const bev = Math.min(r, d / 3);
    const geo = new THREE.ExtrudeGeometry(s, { depth: Math.max(0.001, d - 2 * bev), bevelEnabled: true, bevelThickness: bev, bevelSize: bev * 0.8, bevelSegments: 3, curveSegments: 6 });
    geo.translate(0, 0, -(d - 2 * bev) / 2);
    return new THREE.Mesh(geo, mat);
  };
  const tex = (w, h, draw) => {
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    draw(c.getContext('2d'), w, h);
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    t.anisotropy = 4;
    return t;
  };
  return { toon, glow, box, cyl, pillow, tex };
}

/** Мягкий пушистый круглый коврик с лапкой посередине. */
function makeRug(K) {
  const g = new THREE.Group();
  const map = K.tex(512, 512, (x, w, h) => {
    const cx = w / 2;
    const gr = x.createRadialGradient(cx, cx, 20, cx, cx, cx);
    gr.addColorStop(0, '#f7c6e4'); gr.addColorStop(0.7, '#e7a6d6'); gr.addColorStop(1, '#c98bd9');
    x.fillStyle = gr; x.fillRect(0, 0, w, h);
    // ворс: тысячи коротких штрихов
    for (let i = 0; i < 9000; i++) {
      const a = Math.random() * TAU, r = Math.sqrt(Math.random()) * cx;
      const px = cx + Math.cos(a) * r, py = cx + Math.sin(a) * r;
      x.strokeStyle = `rgba(255,255,255,${0.05 + Math.random() * 0.12})`;
      x.beginPath(); x.moveTo(px, py); x.lineTo(px + (Math.random() - 0.5) * 6, py + (Math.random() - 0.5) * 6); x.stroke();
    }
    x.fillStyle = 'rgba(255,255,255,0.55)';
    x.beginPath(); x.ellipse(cx, cx + 40, 62, 52, 0, 0, TAU); x.fill();
    for (const [dx, dy] of [[-70, -40], [-26, -78], [26, -78], [70, -40]]) { x.beginPath(); x.ellipse(cx + dx, cx + dy, 22, 28, 0, 0, TAU); x.fill(); }
  });
  const disc = new THREE.Mesh(new THREE.CylinderGeometry(0.46, 0.47, 0.014, 64), [K.toon(0xd9a0d6), K.toon(0xffffff, { map }), K.toon(0xd9a0d6)]);
  disc.position.y = 0.007;
  // пушистый край: валик из шерстяных комков
  const edge = new THREE.Group();
  const fluff = K.toon(0xf0b8e2);
  for (let i = 0; i < 64; i++) {
    const a = (i / 64) * TAU;
    const b = new THREE.Mesh(new THREE.SphereGeometry(0.024 + (i % 3) * 0.004, 8, 6), fluff);
    b.position.set(Math.cos(a) * 0.465, 0.014, Math.sin(a) * 0.465);
    b.scale.y = 0.55;
    edge.add(b);
  }
  g.add(disc, edge);
  return g;
}

/** Массажное кресло: откинутая спинка с бегающими роликами, подставка под ноги, неон, пульт. */
/** Дедовское кресло (друг 30.09: «обычное дедовское, не качалку — от массажного колокольчик звенит»): советский
 *  гарнитур — оливковый велюр в ромбик, деревянные подлокотники и растопыренные ножки, кружевная салфетка на спинке. */
function makeArmchair(K) {
  const g = new THREE.Group();
  const velour = K.tex(256, 256, (x, w, h) => {
    x.fillStyle = '#6b6a3a'; x.fillRect(0, 0, w, h);
    x.strokeStyle = 'rgba(40,38,18,0.35)'; x.lineWidth = 3;
    for (let i = -h; i < w + h; i += 32) {
      x.beginPath(); x.moveTo(i, 0); x.lineTo(i + h, h); x.stroke();
      x.beginPath(); x.moveTo(i + h, 0); x.lineTo(i, h); x.stroke();
    }
    for (let i = 0; i < 2500; i++) {       // ворс велюра
      x.fillStyle = `rgba(${Math.random() < 0.5 ? '255,250,200' : '20,20,0'},${0.03 + Math.random() * 0.05})`;
      x.fillRect(Math.random() * w, Math.random() * h, 2, 2);
    }
  });
  velour.wrapS = velour.wrapT = THREE.RepeatWrapping;
  velour.repeat.set(2, 2);
  const cloth = K.toon(0xffffff, { map: velour }), wood = K.toon(0x6b3a1e), woodDark = K.toon(0x4a2812);
  // сиденье на обитом основании
  g.add(K.box(0.6, 0.2, 0.56, cloth, 0, 0.31, 0));
  const seat = K.pillow(0.56, 0.52, 0.14, 0.06, cloth);
  seat.rotation.x = -Math.PI / 2;
  seat.position.set(0, 0.45, 0.01);
  g.add(seat);
  // спинка: почти прямая, чуть откинута
  const back = new THREE.Group();
  const backPad = K.pillow(0.58, 0.74, 0.14, 0.07, cloth);
  backPad.position.set(0, 0.37, 0);
  back.add(backPad);
  // кружевная салфетка на верху спинки — «у бабушки так»
  const lace = K.tex(256, 256, (x, w, h) => {
    x.clearRect(0, 0, w, h);
    x.fillStyle = 'rgba(250,248,240,0.95)';
    x.beginPath(); x.arc(w / 2, h / 2, w / 2 - 4, 0, TAU); x.fill();
    x.globalCompositeOperation = 'destination-out';
    for (let ring = 1; ring <= 5; ring++) {
      const r = ring * 22, n = ring * 8;
      for (let k = 0; k < n; k++) {
        const a = (k / n) * TAU + ring * 0.3;
        x.beginPath(); x.arc(w / 2 + Math.cos(a) * r, h / 2 + Math.sin(a) * r, 5 + ring * 0.6, 0, TAU); x.fill();
      }
    }
    x.globalCompositeOperation = 'source-over';
  });
  const doily = new THREE.Mesh(new THREE.CircleGeometry(0.16, 32), K.toon(0xffffff, { map: lace, transparent: true, alphaTest: 0.3, side: THREE.DoubleSide }));
  doily.position.set(0, 0.72, 0.075);
  back.add(doily);
  back.position.set(0, 0.46, -0.25);
  back.rotation.x = -0.18;
  g.add(back);
  // деревянные подлокотники на стойках и обитые боковины
  for (const sx of [1, -1]) {
    g.add(K.box(0.07, 0.03, 0.6, wood, sx * 0.335, 0.645, 0.01));
    g.add(K.box(0.035, 0.24, 0.035, woodDark, sx * 0.335, 0.52, 0.27), K.box(0.035, 0.24, 0.035, woodDark, sx * 0.335, 0.52, -0.2));
    g.add(K.box(0.05, 0.22, 0.46, cloth, sx * 0.3, 0.5, 0.03));
  }
  // растопыренные конические ножки
  for (const [lx, lz] of [[0.26, 0.24], [-0.26, 0.24], [0.26, -0.24], [-0.26, -0.24]]) {
    const leg = K.cyl(0.022, 0.012, 0.22, woodDark, lx * 1.08, 0.1, lz * 1.08, 12);
    leg.rotation.set(-Math.sign(lz) * 0.18, 0, Math.sign(lx) * 0.18);
    g.add(leg);
  }
  return g;
}

function makeChair(K) {
  const g = new THREE.Group();
  const shell = K.toon(0x2a2438), cushion = K.toon(0x5b3a6e), soft = K.toon(0x6e4a85), cyan = K.glow(0x38e8ff), pink = K.glow(0xff4fa8);
  g.add(K.box(0.62, 0.3, 0.62, shell, 0, 0.15, 0));                       // основание
  const seat = K.pillow(0.54, 0.52, 0.13, 0.06, cushion);
  seat.rotation.x = -Math.PI / 2;
  seat.position.set(0, 0.46, 0.0);
  g.add(seat, K.box(0.5, 0.006, 0.006, cyan, 0, 0.3, 0.312));
  // спинка на шарнире у заднего края сиденья, откинута назад
  const back = new THREE.Group();
  const backPad = K.pillow(0.56, 0.86, 0.16, 0.08, cushion);
  backPad.position.set(0, 0.43, 0);
  const headPad = K.pillow(0.34, 0.16, 0.1, 0.06, soft);
  headPad.position.set(0, 0.8, 0.06);
  const shellBack = K.box(0.6, 0.9, 0.05, shell, 0, 0.44, -0.1);
  const rollers = [];
  for (const sx of [0.07, -0.07]) {
    const r = new THREE.Mesh(new THREE.SphereGeometry(0.035, 14, 10), K.glow(0x9fe8ff, { transparent: true, opacity: 0.85 }));
    r.position.set(sx, 0.4, 0.075);
    rollers.push(r);
    back.add(r);
  }
  back.add(backPad, headPad, shellBack, K.box(0.006, 0.8, 0.006, pink, 0.27, 0.43, 0.08), K.box(0.006, 0.8, 0.006, pink, -0.27, 0.43, 0.08));
  back.position.set(0, 0.5, -0.27);
  back.rotation.x = -0.42;
  g.add(back);
  // подлокотники
  for (const sx of [1, -1]) {
    const arm = K.pillow(0.12, 0.56, 0.12, 0.05, soft);
    arm.rotation.x = -Math.PI / 2;
    arm.position.set(sx * 0.34, 0.6, -0.02);
    g.add(arm, K.box(0.13, 0.28, 0.5, shell, sx * 0.34, 0.4, -0.02), K.box(0.004, 0.004, 0.46, cyan, sx * 0.34 + sx * 0.062, 0.6, -0.02));
  }
  // пульт на правом подлокотнике (её правая — −X)
  g.add(K.box(0.05, 0.02, 0.1, K.toon(0x1b1b24), -0.34, 0.67, 0.08), K.box(0.03, 0.004, 0.02, pink, -0.34, 0.682, 0.1));
  // подставка под ноги на шарнире у переднего края
  const leg = new THREE.Group();
  const legPad = K.pillow(0.46, 0.5, 0.12, 0.05, cushion);
  legPad.position.set(0, -0.25, 0.02);
  leg.add(legPad, K.box(0.44, 0.006, 0.006, cyan, 0, -0.49, 0.08));
  leg.position.set(0, 0.46, 0.27);
  leg.rotation.x = -0.95;
  g.add(leg);
  g.userData.tick = (t) => {
    const k = 0.5 + 0.5 * Math.sin(t * 1.3);
    rollers.forEach((r, i) => { r.position.y = 0.15 + 0.55 * k + (i ? 0.03 : 0) * Math.sin(t * 9); });
  };
  return g;
}

/** Кровать с балдахином: изголовье с кошачьими ушками, подушки, пухлый плед в лапках, столбики, полупрозрачные шторы. */
function makeBed(K) {
  const g = new THREE.Group();
  const wood = K.toon(0x3a2a4a), sheet = K.toon(0xf3e6f7), pinkCloth = K.toon(0xd08ad8), cyan = K.glow(0x38e8ff);
  const z0 = -0.48, z1 = 0.72, zc = (z0 + z1) / 2, L = z1 - z0;
  g.add(K.box(1.0, 0.26, L, wood, 0, 0.2, zc));
  const matt = K.pillow(0.96, L - 0.04, 0.16, 0.06, sheet);
  matt.rotation.x = -Math.PI / 2;
  matt.position.set(0, 0.42, zc);
  g.add(matt);
  // изголовье: мягкая панель с кошачьими ушками и неоновой кромкой
  const head = new THREE.Group();
  const panel = K.pillow(0.98, 0.72, 0.1, 0.12, K.toon(0x6c4a8a));
  panel.position.y = 0.85;
  head.add(panel);
  for (const sx of [1, -1]) {
    const ear = new THREE.Mesh(new THREE.ConeGeometry(0.13, 0.24, 3), K.toon(0x6c4a8a));
    ear.position.set(sx * 0.32, 1.28, 0);
    ear.rotation.z = -sx * 0.25;
    head.add(ear);
  }
  head.add(K.box(0.96, 0.006, 0.006, cyan, 0, 1.21, 0.055));
  head.position.z = z0;
  g.add(head);
  // подушки у изголовья (в них она откидывается)
  for (const sx of [0.26, -0.26]) {
    const p = K.pillow(0.42, 0.34, 0.16, 0.08, K.toon(0xfbe7f6));
    p.position.set(sx, 0.7, z0 + 0.13);
    p.rotation.x = -0.35;
    g.add(p);
  }
  // плед: пухлый, в клетку с лапками
  const plaid = K.tex(512, 512, (x, w, h) => {
    x.fillStyle = '#9b6bc9'; x.fillRect(0, 0, w, h);
    x.fillStyle = 'rgba(255,170,220,0.45)';
    for (let i = 0; i < w; i += 64) { x.fillRect(i, 0, 22, h); x.fillRect(0, i, w, 22); }
    x.fillStyle = 'rgba(255,255,255,0.8)';
    for (let yy = 32; yy < h; yy += 128) for (let xx = 32; xx < w; xx += 128) {
      x.beginPath(); x.ellipse(xx, yy + 8, 12, 10, 0, 0, TAU); x.fill();
      for (const [dx, dy] of [[-12, -8], [-4, -16], [4, -16], [12, -8]]) { x.beginPath(); x.ellipse(xx + dx, yy + dy, 4, 5, 0, 0, TAU); x.fill(); }
    }
  });
  plaid.wrapS = plaid.wrapT = THREE.RepeatWrapping;
  const blanket = K.pillow(1.0, 0.96, 0.34, 0.12, K.toon(0xffffff, { map: plaid }));
  blanket.rotation.x = -Math.PI / 2;
  blanket.position.set(0, 0.64, 0.34);
  g.add(blanket);
  // балдахин: столбики, рама, полупрозрачные шторы сзади и подхваченные у столбиков, фестоны сверху
  const H = 1.72;
  for (const [x, z] of [[0.49, z0], [-0.49, z0], [0.49, z1], [-0.49, z1]]) {
    g.add(K.cyl(0.024, 0.03, H, wood, x, H / 2, z));
    g.add(new THREE.Mesh(new THREE.SphereGeometry(0.045, 12, 8), cyan).translateX(x).translateY(H + 0.02).translateZ(z));
  }
  for (const [w, d, x, z] of [[1.0, 0.03, 0, z0], [1.0, 0.03, 0, z1], [0.03, L, 0.49, zc], [0.03, L, -0.49, zc]]) g.add(K.box(w, 0.03, d, wood, x, H, z));
  const veil = K.toon(0xf2b8ee, { transparent: true, opacity: 0.38, side: THREE.DoubleSide, depthWrite: false });
  const backDrape = new THREE.Mesh(new THREE.PlaneGeometry(0.98, 0.5, 16, 1), veil);
  backDrape.position.set(0, 1.46, z0 - 0.01);
  for (let i = 0; i < backDrape.geometry.attributes.position.count; i++) {
    const px = backDrape.geometry.attributes.position.getX(i);
    backDrape.geometry.attributes.position.setZ(i, Math.sin(px * 28) * 0.02);
  }
  g.add(backDrape);
  // подхваченные шторы: у каждого столбика — «песочные часы» из ткани с бантиком
  for (const [x, z] of [[0.49, z0], [-0.49, z0], [0.49, z1], [-0.49, z1]]) {
    const pts = [];
    for (let i = 0; i <= 12; i++) {
      const y = i / 12;
      pts.push(new THREE.Vector2(0.03 + 0.09 * (Math.abs(y - 0.45) * 1.4) ** 1.2 + 0.02, y * (H - 0.15)));
    }
    const d = new THREE.Mesh(new THREE.LatheGeometry(pts, 16), veil);
    d.position.set(x, 0.12, z);
    g.add(d);
    const bow = new THREE.Mesh(new THREE.TorusGeometry(0.05, 0.012, 6, 16), K.toon(0xff4fa8));
    bow.position.set(x, 0.12 + 0.45 * (H - 0.15), z);
    g.add(bow);
  }
  // фестоны сверху
  for (const [len, x, z, ry] of [[0.98, 0, z1, 0], [0.98, 0, z0, 0], [L, 0.49, zc, Math.PI / 2], [L, -0.49, zc, Math.PI / 2]]) {
    const n = Math.round(len / 0.14);
    for (let i = 0; i < n; i++) {
      const sc = new THREE.Mesh(new THREE.CylinderGeometry(0.07, 0.07, 0.01, 16, 1, true, 0, Math.PI), pinkCloth);
      sc.rotation.set(Math.PI / 2, ry, 0);
      const off = -len / 2 + (i + 0.5) * (len / n);
      sc.position.set(x + (ry ? 0 : off), H - 0.04, z + (ry ? off : 0));
      g.add(sc);
    }
  }
  return g;
}

/** Японская деревянная купель с пеной, пузырями, паром и резиновой уточкой. */
function makeBath(K) {
  const g = new THREE.Group();
  const woodTex = K.tex(256, 128, (x, w, h) => {
    for (let i = 0; i < w; i += 16) {
      x.fillStyle = i % 32 ? '#c89b62' : '#bb8d56';
      x.fillRect(i, 0, 16, h);
      x.fillStyle = 'rgba(90,60,30,0.35)';
      x.fillRect(i, 0, 1.5, h);
    }
  });
  woodTex.wrapS = THREE.RepeatWrapping;
  woodTex.repeat.x = 3;
  const wood = K.toon(0xffffff, { map: woodTex, side: THREE.DoubleSide });
  const Rr = 0.48, H = 0.6, zc = 0.06;
  const tub = new THREE.Mesh(new THREE.CylinderGeometry(Rr, Rr * 0.94, H, 48, 1, true), wood);
  tub.position.set(0, H / 2, zc);
  const bottom = K.cyl(Rr * 0.94, Rr * 0.94, 0.03, K.toon(0xa9824f), 0, 0.015, zc, 40);
  const rim = new THREE.Mesh(new THREE.TorusGeometry(Rr, 0.025, 8, 48), K.toon(0xd9ae74));
  rim.rotation.x = Math.PI / 2;
  rim.position.set(0, H, zc);
  g.add(tub, bottom, rim);
  for (const y of [0.12, 0.46]) {
    const band = new THREE.Mesh(new THREE.TorusGeometry(Rr * (y < 0.3 ? 0.955 : 0.985) + 0.006, 0.01, 6, 48), K.toon(0xc79a3a));
    band.rotation.x = Math.PI / 2;
    band.position.set(0, y, zc);
    g.add(band);
  }
  const water = K.cyl(Rr - 0.01, Rr - 0.01, 0.01, K.toon(0x8fdcf0, { transparent: true, opacity: 0.8 }), 0, 0.52, zc, 40);
  g.add(water);
  // пена: шапки из шаров — плотнее у неё и по краю, выше к центру
  const foamMat = K.toon(0xffffff);
  const foam = new THREE.Group();
  const rnd = (s) => { const x = Math.sin(s * 12.9898) * 43758.5453; return x - Math.floor(x); };
  for (let i = 0; i < 90; i++) {
    const a = rnd(i + 1) * TAU, r = Math.sqrt(rnd(i + 7)) * (Rr - 0.06);
    const bx = Math.cos(a) * r, bz = zc + Math.sin(a) * r;
    const nearHer = Math.max(0, 1 - Math.hypot(bx, bz - 0.02) / 0.3);
    const s = 0.05 + rnd(i + 3) * 0.06 + nearHer * 0.03;
    const b = new THREE.Mesh(new THREE.SphereGeometry(s, 12, 8), foamMat);
    b.position.set(bx, 0.58 + s * 0.6 + nearHer * 0.14 + rnd(i + 5) * 0.05, bz);
    b.userData.base = b.position.y;
    b.userData.ph = rnd(i + 9) * TAU;
    foam.add(b);
  }
  g.add(foam);
  // уточка
  const duck = new THREE.Group();
  const yellow = K.toon(0xffd23a);
  const body = new THREE.Mesh(new THREE.SphereGeometry(0.05, 14, 10), yellow);
  body.scale.set(1, 0.75, 1.2);
  const dh = new THREE.Mesh(new THREE.SphereGeometry(0.032, 12, 10), yellow);
  dh.position.set(0, 0.05, 0.035);
  const beak = new THREE.Mesh(new THREE.ConeGeometry(0.012, 0.03, 8), K.toon(0xff8a1f));
  beak.rotation.x = Math.PI / 2;
  beak.position.set(0, 0.045, 0.07);
  duck.add(body, dh, beak);
  duck.position.set(0.27, 0.62, zc + 0.26);
  g.add(duck);
  // пузыри: переливчатые, всплывают и лопаются
  const bubbles = [];
  const bubMat = K.glow(0xd8f4ff, { transparent: true, opacity: 0.45 });
  for (let i = 0; i < 14; i++) {
    const b = new THREE.Mesh(new THREE.SphereGeometry(0.012 + rnd(i + 20) * 0.02, 10, 8), bubMat);
    b.userData = { t: rnd(i + 30) * 3, x: (rnd(i + 40) - 0.5) * 0.7, z: zc + (rnd(i + 50) - 0.3) * 0.6 };
    bubbles.push(b);
    g.add(b);
  }
  g.userData.tick = (t, dt) => {
    foam.children.forEach((b) => { b.position.y = b.userData.base + Math.sin(t * 1.3 + b.userData.ph) * 0.006; });
    duck.position.y = 0.62 + Math.sin(t * 1.7) * 0.008;
    duck.rotation.y = Math.sin(t * 0.4) * 0.5;
    for (const b of bubbles) {
      const u = b.userData;
      u.t += dt;
      const k = (u.t % 3.2) / 3.2;
      b.position.set(u.x + Math.sin(u.t * 2) * 0.03, 0.78 + k * 0.7, u.z);
      b.scale.setScalar(k > 0.95 ? 0.01 : 1);
    }
  };
  return g;
}

/** Золотой микрофон на «полустойке», как у Фредди Меркьюри (друг 30.09): начало координат — хват лапки, +Y — к губам
 *  (ручка и сетка), вниз по −Y — штанга без основания с набалдашником. */
function makeGoldMic(K) {
  const g = new THREE.Group();
  const gold = K.toon(0xe0ad2e), goldDark = K.toon(0xa8791a), shine = K.glow(0xffe89a), grilleMat = K.toon(0xf0c85a);
  const handle = K.cyl(0.018, 0.013, 0.13, gold, 0, 0.075, 0, 20);
  const grille = new THREE.Mesh(new THREE.SphereGeometry(0.03, 20, 14), grilleMat);
  grille.position.y = 0.165;
  // сетка: тёмные кольца поверх шара
  for (const [y, r] of [[0.15, 0.028], [0.165, 0.0305], [0.18, 0.026]]) {
    const band = new THREE.Mesh(new THREE.TorusGeometry(r, 0.0018, 6, 28), goldDark);
    band.rotation.x = Math.PI / 2;
    band.position.y = y;
    g.add(band);
  }
  const collar = K.cyl(0.02, 0.02, 0.012, goldDark, 0, 0.14, 0, 20);
  const rod = K.cyl(0.0085, 0.0085, 0.56, gold, 0, -0.26, 0, 12);
  const glint = K.box(0.002, 0.5, 0.002, shine, 0.006, -0.25, 0.006);
  const knob = new THREE.Mesh(new THREE.SphereGeometry(0.016, 14, 10), gold);
  knob.position.y = -0.545;
  const clip = K.box(0.03, 0.03, 0.02, goldDark, 0, 0.005, 0);
  g.add(handle, grille, collar, rod, glint, knob, clip);
  return g;
}

/** Большие наушники: чашки с неоновым кольцом и лапкой, дужка — позади кошачьих ушек. */
function makeHeadphones(K) {
  const g = new THREE.Group();
  const shell = K.toon(0x1d1a2a), pad = K.toon(0x3a2f4d), pink = K.glow(0xff4fa8), cyan = K.glow(0x38e8ff);
  for (const sx of [1, -1]) {
    const cup = new THREE.Group();
    const c = K.cyl(0.062, 0.062, 0.046, shell, 0, 0, 0, 28);
    c.rotation.z = Math.PI / 2;
    const cushion = new THREE.Mesh(new THREE.TorusGeometry(0.05, 0.016, 8, 24), pad);
    cushion.rotation.y = Math.PI / 2;
    cushion.position.x = -sx * 0.026;
    const glowRing = new THREE.Mesh(new THREE.TorusGeometry(0.057, 0.005, 6, 32), sx > 0 ? cyan : pink);
    glowRing.rotation.y = Math.PI / 2;
    glowRing.position.x = sx * 0.024;
    const pawDot = new THREE.Mesh(new THREE.CircleGeometry(0.018, 16), sx > 0 ? pink : cyan);
    pawDot.rotation.y = sx * Math.PI / 2;
    pawDot.position.x = sx * 0.0236;
    cup.add(c, cushion, glowRing, pawDot);
    cup.position.x = sx * 0.13;
    g.add(cup);
  }
  const band = new THREE.Mesh(new THREE.TorusGeometry(0.13, 0.013, 8, 32, Math.PI), shell);
  band.position.y = 0.012;
  band.rotation.x = -0.45;   // назад: дужка ложится за кошачьими ушками
  const bandGlow = new THREE.Mesh(new THREE.TorusGeometry(0.13, 0.0035, 6, 32, Math.PI), cyan);
  bandGlow.position.copy(band.position);
  bandGlow.rotation.copy(band.rotation);
  bandGlow.scale.setScalar(1.08);
  g.add(band, bandGlow);
  return g;
}

/** Синтезатор на стойке: 4 октавы, неон, экранчик. keyX(midi) — где на клавиатуре эта нота (её ось X). */
function makeSynth(K) {
  const g = new THREE.Group();
  const W = 0.84, Y = 0.9, Z = 0.36;
  const keys = K.tex(1024, 128, (x, w, h) => {
    x.fillStyle = '#f4f4fa'; x.fillRect(0, 0, w, h);
    const n = 28;   // белых клавиш в 4 октавах
    for (let i = 0; i <= n; i++) { x.fillStyle = '#9aa0b4'; x.fillRect((i / n) * w - 1, 0, 2, h); }
    for (let i = 0; i < n; i++) {
      if ([2, 6].includes(i % 7)) continue;
      x.fillStyle = '#1a1a22';
      x.fillRect(((i + 1) / n) * w - w / n * 0.3, 0, (w / n) * 0.6, h * 0.62);
    }
  });
  const top = new THREE.Group();   // корпус с клавишами: наклон к зрителю вокруг ближней к ней кромки
  const body = K.box(W + 0.06, 0.06, 0.3, K.toon(0x1e1a2c), 0, -0.03, 0.15);
  const keyTop = new THREE.Mesh(new THREE.PlaneGeometry(W, 0.14), K.toon(0xffffff, { map: keys }));
  keyTop.rotation.x = -Math.PI / 2;
  keyTop.rotation.z = Math.PI;   // низкие ноты — у её ЛЕВОЙ руки (+X)
  keyTop.position.set(0, 0.002, 0.08);
  const screen = K.box(0.2, 0.004, 0.06, K.glow(0x38e8ff), 0, 0.002, 0.22);
  const edge = K.box(W + 0.06, 0.006, 0.006, K.glow(0xff4fa8), 0, -0.01, 0.302);
  top.add(body, keyTop, screen, edge);
  for (let i = 0; i < 6; i++) top.add(K.cyl(0.012, 0.012, 0.012, K.toon(0x9aa2b5), 0.18 + i * 0.035, 0.006, 0.23, 12));
  top.position.set(0, Y, Z - 0.15);
  top.rotation.x = 0.22;
  g.add(top);
  // стойка «иксом»: у каждого края — две перекрещенные трубы
  for (const sx of [1, -1]) for (const a of [0.5, -0.5]) {
    const leg = K.box(0.024, Y / Math.cos(a), 0.024, K.toon(0x2b2f3a), sx * 0.3, Y / 2 - 0.02, Z);
    leg.rotation.x = a;
    g.add(leg);
  }
  g.userData = { W, Y, Z };
  return g;
}

/** Скрипка и смычок. Скрипка: начало координат — середина корпуса, +Y — к завитку, +Z — из деки (струны). */
function makeViolin(K) {
  const vio = new THREE.Group();
  const s = new THREE.Shape();
  // контур корпуса «восьмёркой»: нижняя часть шире, талия с эсами
  const pts = [[0, -0.18], [0.1, -0.16], [0.105, -0.07], [0.07, -0.02], [0.068, 0.03], [0.09, 0.07], [0.085, 0.15], [0, 0.17]];
  s.moveTo(0, -0.18);
  for (let side = 1; side >= -1; side -= 2) {
    const list = side === 1 ? pts : [...pts].reverse();
    for (const [x, y] of list) s.lineTo(x * side, y);
  }
  const bodyGeo = new THREE.ExtrudeGeometry(s, { depth: 0.035, bevelEnabled: true, bevelThickness: 0.006, bevelSize: 0.006, bevelSegments: 3, curveSegments: 8 });
  bodyGeo.translate(0, 0, -0.0175);
  const varnish = K.toon(0xa4471d), dark = K.toon(0x1a1414);
  vio.add(new THREE.Mesh(bodyGeo, varnish));
  const fb = K.box(0.036, 0.28, 0.012, dark, 0, 0.19, 0.026);
  const neck = K.box(0.03, 0.14, 0.022, varnish, 0, 0.26, 0.012);
  const scroll = new THREE.Mesh(new THREE.TorusGeometry(0.018, 0.009, 8, 16), varnish);
  scroll.rotation.y = Math.PI / 2;
  scroll.position.set(0, 0.34, 0.012);
  const chin = K.box(0.06, 0.05, 0.012, dark, -0.04, -0.15, 0.028);
  const tail = K.box(0.03, 0.08, 0.006, dark, 0, -0.14, 0.024);
  const bridge = K.box(0.042, 0.004, 0.022, K.toon(0xe8d2a6), 0, -0.05, 0.03);
  vio.add(fb, neck, scroll, chin, tail, bridge);
  for (const sx of [-1, 1]) {
    const f = K.box(0.004, 0.07, 0.002, dark, sx * 0.035, -0.04, 0.0186);
    f.rotation.z = sx * 0.12;
    vio.add(f);
  }
  const strMat = new THREE.LineBasicMaterial({ color: 0xe8e8f0 });
  for (let i = 0; i < 4; i++) {
    const x = -0.012 + i * 0.008;
    vio.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints([V(x, -0.14, 0.03), V(x * 0.7, 0.32, 0.033)]), strMat));
  }
  const bow = new THREE.Group();   // 0 — колодка (в лапке), +X — к кончику
  const stick = K.cyl(0.004, 0.0025, 0.72, K.toon(0x5a2e16));
  stick.rotation.z = Math.PI / 2;
  stick.position.x = 0.36;
  const hair = K.box(0.7, 0.002, 0.008, K.toon(0xf4efe2), 0.36, -0.012, 0);
  const frog = K.box(0.04, 0.02, 0.014, dark, 0.01, -0.008, 0);
  bow.add(stick, hair, frog);
  return { vio, bow };
}

/** Бар-тележка с киберпивом (две полки, банки с неоном, вывеска). */
function makeBar(K) {
  const g = new THREE.Group();
  const frame = K.toon(0x2b2f3a), pink = K.glow(0xff4fa8), cyan = K.glow(0x38e8ff);
  for (const y of [0.35, 0.78]) g.add(K.box(0.42, 0.02, 0.3, K.toon(0x241f33), 0, y, 0), K.box(0.42, 0.006, 0.006, pink, 0, y + 0.012, 0.152));
  for (const [x, z] of [[0.19, 0.13], [-0.19, 0.13], [0.19, -0.13], [-0.19, -0.13]]) {
    g.add(K.box(0.018, 0.8, 0.018, frame, x, 0.42, z));
    g.add(new THREE.Mesh(new THREE.SphereGeometry(0.025, 10, 8), K.toon(0x15161c)).translateX(x).translateY(0.025).translateZ(z));
  }
  const labels = [0x38e8ff, 0xff4fa8, 0xb07cff, 0x38e8ff, 0xffd23a];
  labels.forEach((c, i) => {
    const can = K.cyl(0.033, 0.033, 0.12, K.toon(0x1b1b24), -0.14 + i * 0.07, 0.85, -0.03 + (i % 2) * 0.06, 20);
    const ring = new THREE.Mesh(new THREE.TorusGeometry(0.034, 0.004, 6, 20), K.glow(c));
    ring.rotation.x = Math.PI / 2;
    ring.position.set(can.position.x, 0.87, can.position.z);
    g.add(can, ring);
  });
  for (let i = 0; i < 3; i++) g.add(K.cyl(0.035, 0.035, 0.12, K.toon(0x2a2438), -0.1 + i * 0.1, 0.42, 0, 20));
  const sign = K.tex(512, 96, (x, w, h) => {
    x.fillStyle = '#12101a'; x.fillRect(0, 0, w, h);
    x.font = 'bold 58px Segoe UI, sans-serif';
    x.textAlign = 'center'; x.textBaseline = 'middle';
    x.shadowColor = '#ff4fa8'; x.shadowBlur = 16; x.fillStyle = '#ff9ad2';
    x.fillText('КИБЕРПИВО', w / 2, h / 2 + 3);
  });
  const plate = new THREE.Mesh(new THREE.PlaneGeometry(0.4, 0.075), K.glow(0xffffff, { map: sign }));
  plate.position.set(0, 0.6, 0.152);
  g.add(plate, K.box(0.004, 0.004, 0.3, cyan, 0.21, 0.78, 0));
  return g;
}

/** Бронзовая пепельница на тонкой ножке. */
function makeAshtray(K) {
  const g = new THREE.Group();
  const bronze = K.toon(0xb5813a), dark = K.toon(0x5b3d18);
  g.add(K.cyl(0.11, 0.12, 0.015, dark, 0, 0.008, 0, 28), K.cyl(0.008, 0.01, 0.88, bronze, 0, 0.45, 0, 12));
  const pts = [V(0, 0, 0), V(0.075, 0, 0), V(0.085, 0.02, 0), V(0.07, 0.035, 0), V(0.05, 0.015, 0)].map((p) => new THREE.Vector2(p.x, p.y));
  const bowl = new THREE.Mesh(new THREE.LatheGeometry(pts, 28), bronze);
  bowl.position.y = 0.89;
  const ash = K.cyl(0.045, 0.045, 0.004, K.toon(0x8d8d8d), 0, 0.905, 0, 20);
  g.add(bowl, ash);
  for (let i = 0; i < 3; i++) {
    const notch = K.box(0.02, 0.012, 0.018, dark, Math.cos(i * TAU / 3) * 0.078, 0.922, Math.sin(i * TAU / 3) * 0.078);
    notch.rotation.y = -i * TAU / 3;
    g.add(notch);
  }
  return g;
}

/** «Z-z-z»: всплывающие буквы над спящей. */
function makeZzz(K) {
  const g = new THREE.Group();
  const t = K.tex(128, 128, (x, w, h) => {
    x.font = 'bold 96px Segoe UI, sans-serif';
    x.textAlign = 'center'; x.textBaseline = 'middle';
    x.shadowColor = '#7fdcff'; x.shadowBlur = 12; x.fillStyle = '#e6f6ff';
    x.fillText('Z', w / 2, h / 2);
  });
  for (let i = 0; i < 3; i++) {
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: t, transparent: true, depthWrite: false }));
    s.userData.ph = i / 3;
    g.add(s);
  }
  g.userData.tick = (time) => {
    g.children.forEach((s) => {
      const k = (time * 0.28 + s.userData.ph) % 1;
      s.position.set(0.05 + k * 0.12, k * 0.3, 0);
      s.scale.setScalar(0.04 + k * 0.07);
      s.material.opacity = Math.sin(k * Math.PI);
    });
  };
  return g;
}

// ---------- модуль ----------

export function createProps({ vrm, scene, gradientMap, gestures, S, setEmotion, cfg, audio }) {
  const K = kit(gradientMap);
  const { P, R, sceneQ } = gestures;
  const items = {};

  // Тихое сопение во сне (Web Audio): мягкое дыхание через носик спящей кошечки
  let snoreGain = null;
  let snoreSource = null;
  function initSnore() {
    if (!audio || snoreSource) return;
    try {
      const bufferSize = audio.sampleRate * 2;
      const noiseBuffer = audio.createBuffer(1, bufferSize, audio.sampleRate);
      const output = noiseBuffer.getChannelData(0);
      let b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
      for (let i = 0; i < bufferSize; i++) {
        const white = Math.random() * 2 - 1;
        b0 = 0.99886 * b0 + white * 0.0555179;
        b1 = 0.99332 * b1 + white * 0.0750759;
        b2 = 0.96900 * b2 + white * 0.1538520;
        b3 = 0.86650 * b3 + white * 0.3104856;
        b4 = 0.55000 * b4 + white * 0.5329522;
        b5 = -0.7616 * b5 - white * 0.0168980;
        output[i] = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.11;
        b6 = white * 0.115926;
      }
      snoreSource = audio.createBufferSource();
      snoreSource.buffer = noiseBuffer;
      snoreSource.loop = true;

      const snoreFilter = audio.createBiquadFilter();
      snoreFilter.type = 'bandpass';
      snoreFilter.frequency.value = 260;
      snoreFilter.Q.value = 2.0;

      snoreGain = audio.createGain();
      snoreGain.gain.value = 0;

      snoreSource.connect(snoreFilter);
      snoreFilter.connect(snoreGain);
      snoreGain.connect(audio.destination);
      snoreSource.start();
    } catch { }
  }
  /** Предмет у ног модели (floor) или у кости (bone) — появляется «вырастая», прячется, когда не нужен. */
  function item(name, obj, { floor = true, grow = true } = {}) {
    obj.traverse((o) => { o.frustumCulled = false; });
    obj.visible = false;
    scene.add(obj);
    items[name] = { obj, floor, grow, show: 0, want: 0 };
    return obj;
  }
  item('rug', makeRug(K));
  item('chair', makeChair(K));
  item('armchair', makeArmchair(K));
  item('bed', makeBed(K));
  item('bath', makeBath(K));
  const mic = item('mic', makeGoldMic(K), { floor: false, grow: false });
  const phones = item('headphones', makeHeadphones(K), { floor: false, grow: false });
  const synth = item('synth', makeSynth(K));
  const violin = makeViolin(K);
  item('violin', violin.vio, { floor: false, grow: false });
  item('bow', violin.bow, { floor: false, grow: false });
  item('bar', makeBar(K));
  item('ashtray', makeAshtray(K));
  const zzz = item('zzz', makeZzz(K), { floor: false, grow: false });

  const want = (name, w) => { items[name].want = Math.max(items[name].want, w); };
  const headQ = new THREE.Quaternion();
  const m4 = new THREE.Matrix4();
  /** Ориентация кости в осях персонажа (как P): по трём точкам. */
  function boneQ(bone) {
    const o = P(bone, 0, 0, 0), x = P(bone, 1, 0, 0).sub(o).normalize(), y = P(bone, 0, 1, 0).sub(o).normalize();
    const z = new THREE.Vector3().crossVectors(x, y).normalize();
    m4.makeBasis(x, y, z);
    return headQ.setFromRotationMatrix(m4).clone();
  }
  const worldFromRest = (v) => v.clone().applyQuaternion(sceneQ()).add(vrm.scene.getWorldPosition(new THREE.Vector3()));

  // ---------- позы ----------
  const soft = (u) => ramp(u, 0, 1.2);
  gestures.define('chair', { dur: Infinity, fadeIn: 1.4, fadeOut: 1.1, base: true, face: ['relaxed', 0.8], spec: (u) => {
    const buzz = Math.sin(u * 38) * 0.004 * soft(u);
    return {
      sit: 1, sitDrop: 0.36, hipsZ: -0.06, feet: [0.1, 0.27, 0.6], knees: [0.18, 1.0, 1.7],
      body: { spine: [0.28 + buzz, 0, buzz], chest: [0.1, 0, 0], neck: [-0.08, 0, 0], head: [-0.16, Math.sin(u * 0.3) * 0.06, 0.05] },
      L: { t: R(0.34, 0.68, 0.06), pole: R(0.9, 0.6, -0.4), curl: 0.35, hand: [0.2, 0, 0.3] },
      R: { t: R(-0.34, 0.68, 0.06), pole: R(-0.9, 0.6, -0.4), curl: 0.35, hand: [0.2, 0, -0.3] },
      blink: 0.35 + 0.25 * Math.max(0, Math.sin(u * 0.4)),
      after: (w) => want('chair', w),
    };
  } });
  // дедовское кресло: сидит ровно, без вибрации — колокольчик на чокере молчит; лапки на деревянных подлокотниках
  gestures.define('armchair', { dur: Infinity, fadeIn: 1.3, fadeOut: 1.0, base: true, face: ['relaxed', 0.7], spec: (u) => ({
    sit: 1, sitDrop: 0.35, hipsZ: -0.04, feet: [0.1, 0.25, 0.55], knees: [0.18, 1.0, 1.6],
    body: { spine: [0.1, 0, 0], chest: [0.05, 0, 0], neck: [-0.04, 0, 0], head: [-0.08, Math.sin(u * 0.22) * 0.07, 0.03] },
    L: { t: R(0.33, 0.68, 0.1), pole: R(0.9, 0.6, -0.4), curl: 0.4, hand: [0.1, 0, 0.2] },
    R: { t: R(-0.33, 0.68, 0.1), pole: R(-0.9, 0.6, -0.4), curl: 0.4, hand: [0.1, 0, -0.2] },
    blink: 0.2 + 0.2 * Math.max(0, Math.sin(u * 0.35)),
    after: (w) => want('armchair', w),
  }) });
  const bedPose = (u, sleep) => ({
    sit: 1, sitDrop: 0.36, hipsZ: -0.12, feet: [0.12, 0.62, 0.58], knees: [0.15, 0.7, 1.5],
    body: sleep
      ? { spine: [0.5 + Math.sin(u * 1.1) * 0.01, 0, 0.04], chest: [0.24, 0, 0], neck: [0.02, 0, 0.1], head: [0.06, 0.12, 0.26] }
      : { spine: [0.32, 0, 0], chest: [0.14, 0, 0], neck: [-0.06, 0, 0], head: [-0.22, Math.sin(u * 0.25) * 0.08, 0.04] },
    // лапки на пледе поверх живота (точки от пола: таз при посадке опускается уже после расчёта позы)
    L: { t: R(0.12, sleep ? 0.74 : 0.76, 0.14), pole: R(0.8, 0.9, -0.3), curl: 0.4 },
    R: { t: R(-0.1, sleep ? 0.76 : 0.78, 0.12), pole: R(-0.8, 0.9, -0.3), curl: 0.4 },
    blink: sleep ? 1 : 0.15,
    after: (w) => { want('bed', w); if (sleep) want('zzz', w); },
  });
  gestures.define('bed', { dur: Infinity, fadeIn: 1.5, fadeOut: 1.2, base: true, face: ['relaxed', 0.7], spec: (u) => bedPose(u, false) });
  gestures.define('sleep', { dur: Infinity, fadeIn: 2.2, fadeOut: 1.4, base: true, face: ['relaxed', 0.9], spec: (u) => bedPose(u, true) });
  gestures.define('bath', { dur: Infinity, fadeIn: 1.5, fadeOut: 1.2, base: true, face: ['happy', 0.55], spec: (u) => ({
    sit: 1, sitDrop: 0.46, hipsZ: -0.02, feet: [0.12, 0.1, 0.3], knees: [0.2, 0.9, 1.3],
    body: { spine: [0.12, 0, Math.sin(u * 0.5) * 0.03], chest: [0.04, 0, 0], head: [-0.1, Math.sin(u * 0.35) * 0.1, 0.06] },
    L: { t: R(0.42, 0.64, 0.24), pole: R(1, 0.8, -0.2), curl: 0.3, hand: [0, 0, 0.5] },
    R: { t: R(-0.42, 0.64, 0.24), pole: R(-1, 0.8, -0.2), curl: 0.3, hand: [0, 0, -0.5] },
    blink: 0.3,
    after: (w) => want('bath', w),
  }) });

  // микрофон: стоит перед ней, «журавль» к губам; левая лапка держит стойку, правая свободна (жесты песни)
  // стойка Фредди Меркьюри (друг 30.09: «убери покачивание, она как марионетка — сделай стойку как у Фредди и
  // золотой микрофон»): ноги широко, корпус чуть назад, подбородок вверх; правая лапка держит микрофон на полустойке у
  // губ, левая — на бедре, а на громких протяжных нотах кулак взлетает вверх. Без маятника: только дыхание и нота
  let power = 0;
  gestures.define('mic', { dur: Infinity, fadeIn: 0.9, fadeOut: 0.8, base: true, face: ['happy', 0.35], spec: (u) => {
    const loud = S.singing ? S.mouth.aa : 0;
    power += ((loud > 0.3 ? 1 : 0) - power) * (loud > 0.3 ? 0.025 : 0.012);   // кулак — на выдержанной ноте, не на слоге
    const fist = ramp(power, 0.45, 0.85);
    const breath = Math.sin(u * 1.6) * 0.012;
    return {
      // ноги прямые и чуть врозь: присед гнул колени наружу, и они прошивали длинный подол кимоно
      stance: { LL: [0.04, 0.0], RL: [0.035, 0.02] },   // шире — ноги прошивают длинный подол кимоно
      body: { spine: [0.05 + breath, 0.12, 0.02], chest: [0.04 + 0.05 * fist, 0.05, 0], neck: [0.02, -0.06, 0], head: [0.1 + 0.08 * fist, -0.08, 0.03] },
      R: { t: P('head', -0.05, -0.21, 0.15), pole: P('chest', -0.8, -0.6, 0.1), curl: 0.95, hand: [0.2, 0, 0.2] },
      L: fist > 0.02
        ? { t: P('hips', 0.2, 0.08, 0.06).lerp(P('head', 0.2, 0.34, 0.06), fist), pole: P('chest', 0.9, -0.2 + 0.8 * fist, -0.2), curl: 0.3 + 0.7 * fist, hand: [0, 0, 0.4] }
        : { t: P('hips', 0.2, 0.08, 0.06), pole: P('chest', 0.9, -0.2, -0.2), curl: 0.3, hand: [0, 0, 0.4] },
      after: (w, hands) => {
        want('mic', w);
        const grip = hands.R.localToWorld(V(0.05, 0, -0.015));
        mic.position.copy(grip);
        // сетка — к губам; хват ниже подбородка, так штанга уходит вниз-вперёд-вбок, как у Фредди, а не торчит трубой
        mic.quaternion.setFromUnitVectors(V(0, 1, 0), P('head', 0, -0.05, 0.09).sub(grip).normalize());
      },
    };
  } });
  gestures.define('headphones', { dur: Infinity, fadeIn: 0.6, fadeOut: 0.6, base: true, spec: () => ({ after: (w) => want('headphones', w) }) });

  // синтезатор: правая лапка — мелодия (нота → место на клавиатуре), левая — аккорд в басах; на атаке нота нажимается
  const sy = synth.userData;
  const keyX = (midi) => THREE.MathUtils.clamp(0.36 - ((midi - 48) / 36) * 0.72, -0.38, 0.38);
  function currentNote() {
    const notes = S.songNotes, pos = S.songPos;
    if (!notes || pos == null) return null;
    let lo = 0, hi = notes.length - 1, hit = null;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (notes[mid][0] <= pos) { hit = mid; lo = mid + 1; } else hi = mid - 1;
    }
    return hit == null ? null : { n: notes[hit], age: pos - notes[hit][0], idx: hit };
  }
  gestures.define('synth', { dur: Infinity, fadeIn: 0.8, fadeOut: 0.8, base: true, face: ['happy', 0.5], spec: (u) => {
    const c = currentNote();
    const rx = c ? keyX(c.n[2]) : -0.1 + Math.sin(u * 3) * 0.08;
    const press = c ? Math.exp(-c.age * 14) : Math.max(0, Math.sin(u * 7)) ** 4;
    const lx = 0.22 + Math.sin(u * 0.5) * 0.04;
    const lpress = Math.max(0, Math.sin(u * 3.2)) ** 6;
    const ky = sy.Y + 0.03, kz = sy.Z - 0.1;
    return {
      body: { chest: [-0.05, 0, 0], head: [-0.16, (rx - 0.05) * 0.5, 0], spine: [-0.03, 0, 0] },
      L: { t: worldFromRest(V(lx, ky - lpress * 0.012, kz)), pole: P('chest', 0.7, -0.6, 0), curl: 0.45 + lpress * 0.2, hand: [0.35, 0.1, -0.2] },
      R: { t: worldFromRest(V(rx, ky - press * 0.015, kz)), pole: P('chest', -0.7, -0.6, 0), curl: 0.35 + press * 0.3, index: 0.2 + press * 0.4, hand: [0.35, -0.1, 0.2] },
      after: (w) => want('synth', w),
    };
  } });

  // скрипка на левом плече, щека на подбородник; левая лапка на грифе, правая ведёт смычок, на каждой ноте — смена
  // направления (вверх-вниз), скорость — по длине ноты
  const vioPos = new THREE.Vector3(), vioQ = new THREE.Quaternion(), bowPos = new THREE.Vector3(), bowQ = new THREE.Quaternion();
  // Друг 02.10: «скрипка и палка проваливаются в сиську, её колбасит и трясёт». Было: корпус скрипки на 12 см ниже шеи и
  // носом вниз — нижняя дека лежала на груди; смычок уходил вниз к правому бедру и резал грудь наискосок; левая лапка
  // прыгала по грифу на 3 см рывком на каждой ноте, смычок на паузах перескакивал. Теперь скрипка на ключице под
  // подбородком, гриф чуть вверх; смычок поперёк струн на высоте плеч; лапки ходят плавно (сглаживание ~60 мс).
  function violinFrame() {
    const d = V(0.6, 0.06, 0.8).normalize();                    // от корпуса к завитку: вперёд-влево, гриф чуть вверх
    let n = V(-0.5, 0.85, 0.1);                                 // дека смотрит вверх и вправо (к смычку)
    n = n.sub(d.clone().multiplyScalar(n.dot(d))).normalize();
    const x = new THREE.Vector3().crossVectors(d, n).normalize();
    m4.makeBasis(x, d, n);
    const qRest = new THREE.Quaternion().setFromRotationMatrix(m4);
    const q = sceneQ().multiply(qRest);
    const center = P('neck', 0.1, -0.045, 0.13);                // ключица слева, выше груди, подбородок на подбороднике
    return { q, center, d: d.applyQuaternion(sceneQ()), n: n.applyQuaternion(sceneQ()), x: x.applyQuaternion(sceneQ()) };
  }
  const vioSmooth = { u: null, s: 0.5, finger: 0 };
  gestures.define('violin', { dur: Infinity, fadeIn: 0.9, fadeOut: 0.9, base: true, face: ['relaxed', 0.6], spec: (u) => {
    const f = violinFrame();
    const c = currentNote();
    const dt = vioSmooth.u == null ? 0 : Math.min(0.1, Math.max(0, u - vioSmooth.u));
    vioSmooth.u = u;
    // смычок: доля длины 0..1, направление меняется на каждой ноте; без нот — просто водит
    let s;
    if (c) {
      const len = Math.max(0.12, c.n[1] - c.n[0]);
      const k = Math.min(1, c.age / len);
      s = c.idx % 2 ? 0.15 + 0.7 * k : 0.85 - 0.7 * k;
    } else s = 0.5 + 0.35 * Math.sin(u * 2.4);
    const follow = dt > 0 ? 1 - Math.exp(-dt / 0.06) : 1;
    vioSmooth.s += (s - vioSmooth.s) * follow;
    s = vioSmooth.s;
    const contact = f.center.clone().add(f.d.clone().multiplyScalar(-0.05)).add(f.n.clone().multiplyScalar(0.035));
    // поперёк струн, в плоскости деки, чуть вниз к правой руке
    const bowDir = f.x.clone().multiplyScalar(-1).add(f.d.clone().multiplyScalar(-0.12)).add(f.n.clone().multiplyScalar(0.05)).normalize();
    const frog = contact.clone().sub(bowDir.clone().multiplyScalar(0.08 + s * 0.5));
    const neckPt = f.center.clone().add(f.d.clone().multiplyScalar(0.27)).add(f.n.clone().multiplyScalar(-0.01));
    vioSmooth.finger += ((c ? Math.sin(c.n[2] * 1.7) * 0.03 : 0) - vioSmooth.finger) * follow;
    const finger = vioSmooth.finger;
    return {
      body: { head: [-0.14, 0.32, 0.22], neck: [-0.06, 0.12, 0.1], chest: [0, 0.08, 0] },
      L: { t: neckPt.add(f.d.clone().multiplyScalar(finger)), pole: P('chest', 0.5, -0.8, -0.2), curl: 0.6, index: 0.4, hand: [0.4, 0.6, 0.4] },
      R: { t: frog, pole: P('chest', -0.9, -0.2, -0.3), curl: 0.55, hand: [0.2, -0.6, 0.3] },
      after: (w) => {
        want('violin', w); want('bow', w);
        vioPos.copy(f.center); vioQ.copy(f.q);
        bowPos.copy(frog);
        const bq = new THREE.Quaternion().setFromUnitVectors(V(1, 0, 0), bowDir);
        bowQ.copy(bq);
      },
    };
  } });

  // танец под бит (грув песни или свой счёт ~118 ударов): три связки по 4 доли — плечики, руки вверх, хлопки
  const danceDef = { dur: Infinity, fadeIn: 0.7, fadeOut: 0.8, face: ['happy', 0.8], spec: (u) => {
    const beat = S.groove?.amt > 0.15 && S.groove.beat != null ? S.groove.beat : u * 1.95;
    const ph = beat % 1, bar = Math.floor(beat / 4) % 3;
    const hit = Math.exp(-ph * 5);
    const sway = Math.sin(beat * Math.PI);
    const sp = { squat: 0.035 * (0.4 + 0.6 * hit), body: { spine: [0, sway * 0.08, sway * 0.07], chest: [-0.03 * hit, sway * 0.05, 0], head: [-0.06 * hit, sway * 0.1, -sway * 0.08] } };
    if (bar === 0) {
      const a = Math.sin(beat * Math.PI);
      sp.L = { t: P('chest', 0.22, 0.05 + 0.12 * a, 0.22), pole: P('chest', 0.8, -0.6, 0), curl: 0.7 };
      sp.R = { t: P('chest', -0.22, 0.05 - 0.12 * a, 0.22), pole: P('chest', -0.8, -0.6, 0), curl: 0.7 };
    } else if (bar === 1) {
      const a = Math.sin(beat * Math.PI * 0.5);
      sp.L = { t: P('chest', 0.22 + a * 0.08, 0.55, 0.08), pole: P('chest', 0.9, 0.2, -0.3), curl: 0.2, hand: [0, 0, 0.3 * a] };
      sp.R = { t: P('chest', -0.22 + a * 0.08, 0.55, 0.08), pole: P('chest', -0.9, 0.2, -0.3), curl: 0.2, hand: [0, 0, 0.3 * a] };
    } else {
      const clap = Math.floor(beat) % 2 === 1 ? Math.exp(-ph * 9) : 0;
      const gap = 0.1 * (1 - clap);
      sp.L = { t: P('chest', gap, 0.08, 0.3), pole: P('chest', 0.8, -0.6, 0), curl: 0.2, hand: [0, 0.4, 0] };
      sp.R = { t: P('chest', -gap, 0.08, 0.3), pole: P('chest', -0.8, -0.6, 0), curl: 0.2, hand: [0, -0.4, 0] };
    }
    return sp;
  } };
  gestures.define('dance', danceDef);
  // короткий танец по метке [танец] или просьбе друга: 12 с и сам кончается (без песни — свой счёт)
  gestures.define('danceBrief', { ...danceDef, dur: 12, fadeOut: 1.0 });

  // Карамельдансен целиком (друг 02.10, разбор по фазам; прошлый «маятник» таскал таз вбок на 9 см при прибитых стопах —
  // ноги качались, как у марионетки, сам таз не работал, шагов не было). Счёт — доли трека или свои 167 в минуту.
  //  куплет/вступление — шаг-приставка вправо и влево (окно едет по рабочему столу за шагами), хлопок у подбородка на
  //    «2» и «4», плечики приподняты;
  //  разгон — «моталочка» кулачками перед грудью, корпус вполоборота то влево, то вправо;
  //  за две доли до припева — взмах руками вверх буквой V и полуприсед, следом — ушки;
  //  припев и проигрыш — ушки с пульсацией, таз «восьмёркой»: на каждую долю бедро выталкивается в сторону и ВВЕРХ (крен
  //    таза), опорная нога выпрямляется, вторая мягко сгибается и отрывает пятку, корпус держится над стопами, голова — за
  //    бедром;
  //  бридж (середина длинного припева или свой круг) — оборот на 360° на месте с ушками, потом одна лапка — ушко, другая
  //    показывает на друга, ноги — шаг-приставка.
  // Фазы — по разметке песни (sections из разбора: куплет, предприпев, проигрыш, припев), без песни — по кругу.
  const HIP_SIDE = -1;     // знак: так hipsX > 0 уводит таз к её левой руке (у VRM 0.x сцена развёрнута — ось X таза смотрит вправо)
  const HIP_ROLL = -1;     // знак крена, при котором бедро на стороне толчка поднимается
  const CAR_LOOP = [['verse', 16], ['pre', 14], ['drop', 2], ['chorus', 32], ['bridge', 16], ['chorus', 16]];
  const carLoopLen = CAR_LOOP.reduce((s, [, n]) => s + n, 0);
  function carPhaseOwn(beat) {
    let b = ((beat % carLoopLen) + carLoopLen) % carLoopLen;
    for (const [name, n] of CAR_LOOP) { if (b < n) return { name, at: b, len: n }; b -= n; }
    return { name: 'chorus', at: 0, len: 32 };
  }
  function carPhaseSong(beat) {
    const secs = S.songSections, beats = S.songBeats;
    if (!Array.isArray(secs) || !secs.length || !Array.isArray(beats) || beats.length < 2) return null;
    const beatAt = (t) => { let lo = 0, hi = beats.length - 1, r = beats.length; while (lo <= hi) { const m = (lo + hi) >> 1; if (beats[m] >= t) { r = m; hi = m - 1; } else lo = m + 1; } return r; };
    const pos = S.songPos ?? 0;
    let i = secs.findIndex((s) => pos >= s.t0 && pos < s.t1);
    if (i < 0) i = pos < secs[0].t0 ? 0 : secs.length - 1;
    const kind = (j) => {
      const nm = String(secs[j]?.name ?? '');
      if (j === 0) return 'verse';                                   // начало песни — раскачка шагами и хлопками
      if (/пред|pre/i.test(nm)) return 'pre';
      if (/припев|проигрыш|chorus|drop|hook/i.test(nm)) return 'chorus';
      return 'verse';
    };
    const s = secs[i], b0 = beatAt(s.t0), b1 = beatAt(s.t1);
    const at = beat - b0, len = Math.max(2, b1 - b0);
    let k = kind(i);
    if (k === 'chorus' && i + 1 < secs.length && kind(i + 1) !== 'chorus' && len - at <= 2) return { name: 'drop', at: 2 - (len - at), len: 2 };
    if (i + 1 < secs.length && kind(i + 1) === 'chorus' && k !== 'chorus' && len - at <= 2) return { name: 'drop', at: 2 - (len - at), len: 2 };
    if (k === 'verse' && i > 0 && at >= len / 2) k = 'pre';           // вторая половина куплета — разгон «моталочкой»
    if (k === 'chorus' && len >= 56 && at >= 32 && at < 48) return { name: 'bridge', at: at - 32, len: 16 };
    return { name: k, at, len };
  }
  const ease = (x) => { const k = Math.min(1, Math.max(0, x)); return k * k * (3 - 2 * k); };
  // плавный переход между фазами: каждое число позы догоняет цель за ~70 мс (на смене фазы руки и таз не прыгают)
  const carSm = { u: null, v: new Map() };
  const sm = (key, value, dt, tau = 0.07) => {
    const old = carSm.v.get(key);
    const next = old == null || dt <= 0 || dt > 0.5 ? value : old + (value - old) * (1 - Math.exp(-dt / tau));
    carSm.v.set(key, next);
    return next;
  };
  // шаг-приставка: 4 доли — правая вправо, левая приставляется (носком), левая влево, правая приставляется.
  // Возвращает смещения стоп от корпуса (м, к её левой руке +), подъём стоп, пятки и ход корпуса (окно по столу).
  function stepTouch(at, ph) {
    const L = 0.16, c = ((Math.floor(at) % 4) + 4) % 4, q = ease(ph / 0.6), e = ease(ph);
    const arc = Math.sin(Math.PI * Math.min(1, ph / 0.6));
    let root = 0, rg = 0, lg = 0, liftR = 0, liftL = 0, heelR = 0, heelL = 0;
    if (c === 0) { root = -L * e; rg = -L * q; liftR = 0.035 * arc; }
    else if (c === 1) { root = -L; rg = -L; lg = -L * q; liftL = 0.025 * arc; heelL = 0.35 * ease((ph - 0.5) / 0.3); }
    else if (c === 2) { root = -L * (1 - e); rg = -L; lg = -L + L * q; liftL = 0.035 * arc; }
    else { root = 0; rg = -L + L * q; liftR = 0.025 * arc; heelR = 0.35 * ease((ph - 0.5) / 0.3); }
    return { dR: rg - root, dL: lg - root, root, liftR, liftL, heelR, heelL };
  }
  const caramellDef = { dur: 52, fadeIn: 0.5, fadeOut: 1.0, face: ['happy', 1.0], spec: (u) => {
    const dt = carSm.u == null ? 0 : u - carSm.u;
    carSm.u = u;
    const hasSong = S.groove?.on && S.groove.rawBeat != null;
    const beat = hasSong ? S.groove.rawBeat : u * 2.78;
    const phase = (hasSong && carPhaseSong(beat)) || carPhaseOwn(beat);
    const n = Math.floor(beat), ph = beat - n;
    const bounce = 0.5 + 0.5 * Math.cos(ph * Math.PI * 2);           // колени пружинят на каждую долю
    const nod = Math.exp(-ph * 7);                                    // кивок лапок и головы на долю
    // поза — числами, потом сглаживается: лапки (смещение от груди, локоть, кулачок), таз, стопы, корпус, голова
    const A = { lx: 0.15, ly: 0.45, lz: 0.05, rx: -0.15, ry: 0.45, rz: 0.05, lpole: 0.32, curlL: 0.5, curlR: 0.5, idxL: 0.45, idxR: 0.45,
      handP: 0.35, handRL: 0.2, handRR: -0.2 };
    let hipsX = 0, roll = 0, yaw = 0, squat = 0.03 + 0.02 * bounce, spin = 0, walk = 0;
    let dL = 0, dR = 0, liftL = 0, liftR = 0, heelL = 0, heelR = 0;
    let spineY = 0, spineZ = 0, chestZ = 0, headX = -0.05 * nod, headY = 0, headZ = 0, shoulders = 0;
    const legsStep = (at) => {
      const st = stepTouch(at, ph);
      dL = st.dL; dR = st.dR; liftL = st.liftL; liftR = st.liftR; heelL = st.heelL; heelR = st.heelR; walk = st.root;
      hipsX = 0.35 * (st.dL + st.dR) / 2;                            // вес переходит на шагнувшую ногу
    };
    if (phase.name === 'verse') {
      legsStep(phase.at);
      // хлопок перед подбородком на «2» и «4»: лапки сходятся на долю, между хлопками — расходятся
      const cp = (((phase.at - 1) % 2) + 2) % 2;
      const gap = 0.13 * Math.pow(Math.sin(Math.PI * Math.min(1, cp / 2)), 0.7);
      Object.assign(A, { lx: 0.018 + gap / 2, ly: 0.18, lz: 0.32, rx: -0.018 - gap / 2, ry: 0.18, rz: 0.32, lpole: -0.3,
        curlL: 0.15, curlR: 0.15, idxL: 0.1, idxR: 0.1, handP: 0.1, handRL: 0.4, handRR: -0.4 });
      shoulders = 0.05;
      headZ = 0.06 * Math.sin(beat * Math.PI);
    } else if (phase.name === 'pre') {
      // «моталочка»: кулачки крутятся друг вокруг друга перед грудью, корпус вполоборота влево-вправо
      const a = beat * Math.PI;
      Object.assign(A, { lx: 0.05, ly: 0.08 + 0.05 * Math.cos(a), lz: 0.3 + 0.05 * Math.sin(a),
        rx: -0.05, ry: 0.08 + 0.05 * Math.cos(a + Math.PI), rz: 0.3 + 0.05 * Math.sin(a + Math.PI), lpole: -0.2,
        curlL: 0.9, curlR: 0.9, idxL: 0.9, idxR: 0.9, handP: 0.2, handRL: 0.1, handRR: -0.1 });
      const turn = Math.sin(beat * Math.PI / 4);
      spineY = 0.22 * turn; yaw = 0.12 * turn;
      headZ = 0.05 * Math.sin(beat * Math.PI);
    } else if (phase.name === 'drop') {
      // взмах вверх буквой V, ладони раскрыты, и полуприсед — сразу за ним ушки припева
      const t = ease(phase.at / 2);
      Object.assign(A, { lx: 0.06 + 0.26 * t, ly: 0.25 + 0.38 * t, lz: 0.2 - 0.14 * t, rx: -0.06 - 0.26 * t, ry: 0.25 + 0.38 * t, rz: 0.2 - 0.14 * t,
        lpole: 0.2, curlL: 0.8 * (1 - t), curlR: 0.8 * (1 - t), idxL: 0.8 * (1 - t), idxR: 0.8 * (1 - t), handP: 0.1, handRL: 0.5, handRR: -0.5 });
      squat = 0.03 + 0.04 * t;
      headX = -0.08 * t;
    } else {
      // ушки на макушке: полукулачки, локти в стороны; на каждую долю — кивок лапками
      Object.assign(A, { lx: 0.15, ly: 0.45, lz: 0.05, rx: -0.15, ry: 0.45, rz: 0.05, lpole: 0.32, curlL: 0.5 + 0.3 * nod, curlR: 0.5 + 0.3 * nod,
        idxL: 0.42 + 0.3 * nod, idxR: 0.42 + 0.3 * nod, handP: 0.3 + 0.5 * nod, handRL: 0.2, handRR: -0.2 });
      if (phase.name === 'bridge' && phase.at < 8) {
        // оборот на месте за 8 долей, ноги переступают на носочках
        spin = Math.PI * 2 * ease(phase.at / 8);
        heelL = n % 2 ? 0.3 * Math.sin(Math.PI * ph) : 0;
        heelR = n % 2 ? 0 : 0.3 * Math.sin(Math.PI * ph);
        headZ = 0.08 * Math.sin(beat * Math.PI);
      } else if (phase.name === 'bridge') {
        // одна лапка — ушко, другая показывает на друга в такт; ноги — шаг-приставка
        legsStep(phase.at - 8);
        Object.assign(A, { rx: -0.08, ry: 0.1 + 0.03 * nod, rz: 0.52, curlR: 0.92, idxR: 0, handRR: -0.1 });
        headZ = 0.06 * Math.sin(beat * Math.PI);
      } else {
        // таз «восьмёркой»: на каждую долю бедро резко выталкивается в сторону и вверх и держится до следующей
        const s = n % 2 === 0 ? 1 : -1;                              // + — к её левой руке
        const hx = -s + 2 * s * ease(ph / 0.3);
        hipsX = 0.045 * hx;
        roll = 0.15 * hx;
        yaw = 0.05 * hx;
        squat = 0.03 + 0.032 * (1 - Math.abs(hx)) + 0.01 * bounce;  // в середине — ниже, на «БАМ» — выше
        // опорная нога — на стороне бедра (прямая, на всей стопе), вторая сгибается и встаёт на носок
        if (hx > 0) heelR = 0.32 * hx; else heelL = 0.32 * -hx;
        spineZ = -0.09 * hx; chestZ = -0.03 * hx;                    // корпус над стопами, против крена таза
        headZ = 0.15 * hx; headY = 0.05 * hx;                        // голова — за бедром
        A.lx += 0.012 * hx; A.rx += 0.012 * hx;
      }
    }
    const k = (name, v, tau) => sm(name, v, dt, tau);
    const arm = (side) => {
      const L = side > 0;
      return {
        t: P('chest', k(L ? 'lx' : 'rx', L ? A.lx : A.rx), k(L ? 'ly' : 'ry', L ? A.ly : A.ry), k(L ? 'lz' : 'rz', L ? A.lz : A.rz)),
        pole: P('chest', side * 0.95, k('pole', A.lpole, 0.12), 0.02),
        curl: k(L ? 'cL' : 'cR', L ? A.curlL : A.curlR), index: k(L ? 'iL' : 'iR', L ? A.idxL : A.idxR),
        hand: [k('hp', A.handP), 0, k(L ? 'hrL' : 'hrR', L ? A.handRL : A.handRR)],
      };
    };
    const base = 0.06;
    return {
      squat: k('squat', squat, 0.05),
      hipsX: HIP_SIDE * k('hx', hipsX, 0.04),
      hipsRoll: HIP_ROLL * k('roll', roll, 0.04),
      hipsYaw: k('yaw', yaw, 0.06),
      spin,
      walk: k('walk', walk, 0.03),
      stance: {
        LL: [base + k('dL', dL, 0.03), 0, k('liftL', liftL, 0.03), k('heelL', heelL, 0.05)],
        RL: [base - k('dR', dR, 0.03), 0, k('liftR', liftR, 0.03), k('heelR', heelR, 0.05)],
      },
      body: {
        spine: [0, k('spY', spineY, 0.12), k('spZ', spineZ, 0.04)],
        chest: [-0.02 * bounce, 0, k('chZ', chestZ, 0.04)],
        head: [headX, k('hdY', headY, 0.06), k('hdZ', headZ, 0.06)],
        shoulders: k('sh', shoulders, 0.1),
      },
      L: arm(1), R: arm(-1),
    };
  } };
  gestures.define('caramell', caramellDef);
  // под песню Caramell из её фонотеки: вся песня до конца
  gestures.define('caramellSong', { ...caramellDef, dur: Infinity });

  // ---------- кадр ----------
  function update(dt) {
    // коврик — всегда (кроме мебели, где она не стоит на полу); бар и пепельница — пока пьёт и курит
    const furniture = gestures.isOn('bed') || gestures.isOn('sleep') || gestures.isOn('bath') || gestures.isOn('chair') || gestures.isOn('armchair') || gestures.isOn('gaming');
    // массажное кресло трясёт — колокольчик на чокере от этого звенел без конца; пока массаж, он молчит
    S.bellHush = gestures.isOn('chair');
    // сопение во сне: тихое посапывание через носик в такт дыханию груди
    const isSleeping = gestures.isOn('sleep');
    if (isSleeping && audio && audio.state === 'running') {
      if (!snoreSource) initSnore();
      if (snoreGain) {
        const breath = Math.sin(S.t * (Math.PI * 2 / 3.5));
        const breathAmp = Math.max(0, breath) * 0.85 + Math.max(0, -breath) * 0.35;
        const targetVol = (cfg.sfxVolume ?? 0.8) * 0.035 * Math.pow(breathAmp, 2.0);
        snoreGain.gain.setValueAtTime(targetVol, audio.currentTime);
      }
    } else if (snoreGain && snoreGain.gain.value > 0.0001) {
      snoreGain.gain.setValueAtTime(0, audio.currentTime);
    }
    if (cfg.rug !== false && !furniture) want('rug', 1);
    if (gestures.isOn('beer') || gestures.isOn('cheers')) want('bar', 1);
    if (gestures.isOn('smoke')) want('ashtray', 1);
    const base = vrm.scene.getWorldPosition(new THREE.Vector3());
    const q = sceneQ();
    for (const [name, it] of Object.entries(items)) {
      it.show += (Math.min(1, it.want) - it.show) * Math.min(1, dt * (it.grow ? 3 : 8));
      it.want = 0;
      const o = it.obj;
      o.visible = it.show > 0.02;
      if (!o.visible) continue;
      if (it.floor) {
        o.position.copy(base);
        o.quaternion.copy(q);
        o.scale.set(1, it.grow ? Math.max(0.001, smooth(it.show)) : 1, 1);
      } else o.scale.setScalar(Math.max(0.001, smooth(it.show)));
      o.userData.tick?.(S.t, dt);
      if (name === 'bar') o.position.add(V(-0.66, 0, 0.02).applyQuaternion(q));
      if (name === 'ashtray') o.position.add(V(-0.42, 0, 0.3).applyQuaternion(q));
    }
    // предметы у костей и у рук
    if (phones.visible) {
      phones.position.copy(P('head', 0, 0.07, -0.01));
      phones.quaternion.copy(boneQ('head'));
    }
    if (items.violin.obj.visible) {
      items.violin.obj.position.copy(vioPos);
      items.violin.obj.quaternion.copy(vioQ);
      items.bow.obj.position.copy(bowPos);
      items.bow.obj.quaternion.copy(bowQ);
    }
    if (zzz.visible) {
      zzz.position.copy(P('head', 0.06, 0.2, 0.05));
      zzz.quaternion.copy(q);
    }
  }

  return { update, items, keyX };
}
