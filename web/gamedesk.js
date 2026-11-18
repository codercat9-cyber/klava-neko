// Игровое место Клавы: кибер-кресло, стол с неоновой кромкой, коврик и мышка. Когда она играет (Зума, тетрис,
// шутеры), мебель «вырастает» из пола, она садится, ноут встаёт на стол — не стоять же часами столбом в аватаре.
//
// Координаты — как у жестов (мир в покое): +X — её ЛЕВАЯ сторона, +Y — вверх от пола, +Z — вперёд, к зрителю.
// Размеры сняты с Vita 28.09: таз 0,98 м, бедро 0,37, голень 0,43, рука 0,43 (плечо+предплечье) — отсюда высота
// сиденья, стола и то, что клавиатура не дальше ~35 см от плеча, иначе лапы не дотянутся.
import * as THREE from 'three';

/** Размеры места (метры) — их же берёт поза сидя в gestures.js. */
export const DESK = {
  seatY: 0.49,            // верх сиденья
  sitDrop: 0.383,         // на сколько опускается таз (0,983 → 0,60: тазобедренные суставы на 0,56, бедро на подушке)
  deskY: 0.74,            // верх столешницы
  deskFront: 0.20,        // ближний к ней край стола
  laptop: [0.07, 0.32],   // центр клавиатуры ноута (x, z)
  mouse: [-0.29, 0.34],   // мышка на коврике (справа от неё — это −X)
  foot: [0.11, 0.42],     // стопы на полу: в стороны и вперёд
};

const smooth = (x) => (x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x));

export function makeGamingDesk(gradientMap) {
  const g = new THREE.Group();
  const toon = (color) => new THREE.MeshToonMaterial({ color, gradientMap });
  const shell = toon(0x1f1c2b);         // графит кресла и стола
  const cushion = toon(0x2b2640);       // обивка
  const metal = toon(0x3a3f4c);
  const pink = new THREE.MeshBasicMaterial({ color: 0xff4fa8 });
  const cyan = new THREE.MeshBasicMaterial({ color: 0x38e8ff });

  const box = (w, h, d, mat, x, y, z) => {
    const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat);
    m.position.set(x, y, z);
    return m;
  };
  /** Скруглённый «пухлый» блок обивки: коробка с фаской через ExtrudeGeometry. */
  const pillow = (w, h, d, r, mat) => {
    const s = new THREE.Shape();
    const x0 = -w / 2, y0 = -h / 2;
    s.moveTo(x0 + r, y0);
    s.lineTo(x0 + w - r, y0); s.quadraticCurveTo(x0 + w, y0, x0 + w, y0 + r);
    s.lineTo(x0 + w, y0 + h - r); s.quadraticCurveTo(x0 + w, y0 + h, x0 + w - r, y0 + h);
    s.lineTo(x0 + r, y0 + h); s.quadraticCurveTo(x0, y0 + h, x0, y0 + h - r);
    s.lineTo(x0, y0 + r); s.quadraticCurveTo(x0, y0, x0 + r, y0);
    const geo = new THREE.ExtrudeGeometry(s, { depth: d - 2 * Math.min(r, d / 3), bevelEnabled: true, bevelThickness: Math.min(r, d / 3), bevelSize: Math.min(r, d / 3) * 0.8, bevelSegments: 3, curveSegments: 6 });
    geo.translate(0, 0, -(d - 2 * Math.min(r, d / 3)) / 2);
    return new THREE.Mesh(geo, mat);
  };

  // ---------- кресло ----------
  const chair = new THREE.Group();
  // сиденье: подушка с боковыми валиками, задний край — с зазором до спинки (туда свешивается хвост)
  const seat = pillow(0.48, 0.44, 0.08, 0.05, cushion);
  seat.rotation.x = -Math.PI / 2;
  seat.position.set(0, DESK.seatY - 0.04, 0.08);
  const seatPipe = box(0.36, 0.006, 0.006, pink, 0, DESK.seatY + 0.001, 0.295);   // неон по переднему краю
  for (const sx of [1, -1]) chair.add(box(0.05, 0.05, 0.42, cushion, sx * 0.235, DESK.seatY + 0.012, 0.08));   // валики
  // спинка — «ковш» гоночного кресла, чуть откинута назад; зазор над сиденьем
  const back = new THREE.Group();
  const backPad = pillow(0.5, 0.66, 0.09, 0.07, cushion);
  backPad.position.set(0, 0.33, 0);
  const shoulderL = pillow(0.1, 0.34, 0.1, 0.04, cushion), shoulderR = shoulderL.clone();
  shoulderL.position.set(0.24, 0.42, 0.03); shoulderR.position.set(-0.24, 0.42, 0.03);
  const head = pillow(0.26, 0.12, 0.07, 0.05, toon(0x352f4d));
  head.position.set(0, 0.6, 0.06);
  const stripe = (x) => box(0.012, 0.52, 0.004, pink, x, 0.34, 0.047);
  back.add(backPad, shoulderL, shoulderR, head, stripe(0.08), stripe(-0.08));
  // на спинке сзади (к зрителю её не видно, но сбоку мелькает) — голубая кромка
  back.add(box(0.5, 0.006, 0.006, cyan, 0, 0.665, -0.046));
  back.position.set(0, DESK.seatY + 0.11, -0.28);
  back.rotation.x = -0.1;
  // подлокотники
  for (const sx of [1, -1]) {
    chair.add(box(0.035, 0.18, 0.035, metal, sx * 0.3, DESK.seatY + 0.05, -0.02));
    const armTop = pillow(0.07, 0.3, 0.035, 0.015, cushion);
    armTop.rotation.x = -Math.PI / 2;
    armTop.position.set(sx * 0.3, DESK.seatY + 0.15, 0.0);
    chair.add(armTop, box(0.004, 0.004, 0.26, cyan, sx * 0.3 + sx * 0.036, DESK.seatY + 0.15, 0.0));
  }
  // газлифт, механизм, крестовина на пяти колёсиках
  const mech = box(0.2, 0.05, 0.2, metal, 0, DESK.seatY - 0.105, 0.04);
  const lift = new THREE.Mesh(new THREE.CylinderGeometry(0.028, 0.034, DESK.seatY - 0.2, 16), metal);
  lift.position.set(0, (DESK.seatY - 0.2) / 2 + 0.07, 0.04);
  const liftRing = new THREE.Mesh(new THREE.TorusGeometry(0.036, 0.004, 6, 24), cyan);
  liftRing.rotation.x = Math.PI / 2;
  liftRing.position.set(0, 0.2, 0.04);
  chair.add(seat, seatPipe, back, mech, lift, liftRing);
  for (let i = 0; i < 5; i++) {
    const a = (i / 5) * Math.PI * 2 + Math.PI / 10;
    const arm = new THREE.Group();
    const bar = box(0.33, 0.035, 0.05, shell, 0.165, 0, 0);
    const wheel = new THREE.Mesh(new THREE.CylinderGeometry(0.032, 0.032, 0.026, 14), metal);
    wheel.rotation.x = Math.PI / 2;
    wheel.position.set(0.32, -0.045, 0);
    arm.add(bar, wheel);
    arm.rotation.y = a;
    arm.position.set(0, 0.075, 0.04);
    chair.add(arm);
  }
  g.add(chair);

  // ---------- стол ----------
  const desk = new THREE.Group();
  const zc = (DESK.deskFront + 0.78) / 2, depth = 0.78 - DESK.deskFront;
  const top = box(1.04, 0.035, depth, shell, 0, DESK.deskY - 0.0175, zc);
  // неон по кромке столешницы: ближняя (к ней) — розовая, дальняя (к зрителю) — голубая
  desk.add(top,
    box(1.04, 0.008, 0.008, pink, 0, DESK.deskY - 0.017, DESK.deskFront - 0.004),
    box(1.04, 0.008, 0.008, cyan, 0, DESK.deskY - 0.017, 0.784));
  // ноги-панели по бокам с неоновой вставкой и передняя панель (закрывает колени — стол «стримерский»)
  for (const sx of [1, -1]) {
    desk.add(box(0.035, DESK.deskY - 0.035, depth - 0.06, shell, sx * 0.49, (DESK.deskY - 0.035) / 2, zc));
    desk.add(box(0.004, DESK.deskY - 0.12, 0.012, cyan, sx * (0.49 + 0.019), (DESK.deskY - 0.035) / 2, 0.74));
  }
  desk.add(box(0.95, 0.3, 0.02, shell, 0, DESK.deskY - 0.035 - 0.15, 0.75));
  // лапка на передней панели
  const pc = document.createElement('canvas');
  pc.width = 128; pc.height = 128;
  const px = pc.getContext('2d');
  px.shadowColor = '#ff4fa8'; px.shadowBlur = 10; px.fillStyle = '#ff7fc0';
  px.beginPath(); px.ellipse(64, 80, 26, 22, 0, 0, Math.PI * 2); px.fill();
  for (const [x, y] of [[30, 44], [50, 30], [78, 30], [98, 44]]) { px.beginPath(); px.ellipse(x, y, 10, 13, 0, 0, Math.PI * 2); px.fill(); }
  const pawTex = new THREE.CanvasTexture(pc);
  pawTex.colorSpace = THREE.SRGBColorSpace;
  const paw = new THREE.Mesh(new THREE.PlaneGeometry(0.1, 0.1), new THREE.MeshBasicMaterial({ map: pawTex, transparent: true, depthWrite: false }));
  paw.position.set(0, DESK.deskY - 0.2, 0.7605);
  desk.add(paw);
  // коврик и мышка
  const pad = box(0.28, 0.004, 0.24, toon(0x14121c), DESK.mouse[0], DESK.deskY + 0.002, DESK.mouse[1] + 0.02);
  const padRim = new THREE.Mesh(new THREE.EdgesGeometry(new THREE.BoxGeometry(0.28, 0.004, 0.24)), new THREE.LineBasicMaterial({ color: 0xff4fa8 }));
  padRim.position.copy(pad.position);
  desk.add(pad, padRim);
  const mouse = new THREE.Group();
  const body = new THREE.Mesh(new THREE.SphereGeometry(0.5, 20, 14), toon(0x262a36));
  body.scale.set(0.062, 0.034, 0.105);
  body.position.y = 0.012;
  const wheelM = box(0.008, 0.01, 0.018, cyan, 0, 0.028, 0.03);
  const glowM = box(0.05, 0.003, 0.004, cyan, 0, 0.004, -0.05);
  mouse.add(body, wheelM, glowM);
  mouse.position.set(DESK.mouse[0], DESK.deskY + 0.004, DESK.mouse[1]);
  desk.add(mouse);
  g.add(desk);

  g.traverse((o) => { o.frustumCulled = false; });
  g.visible = false;

  let show = 0;
  return {
    group: g,
    mouse,
    /** 0 — нет, 1 — стоит: мебель вырастает из пола (масштаб по высоте), кресло чуть позже стола. */
    setShow(k) {
      show = THREE.MathUtils.clamp(k, 0, 1);
      g.visible = show > 0.01;
      chair.scale.set(1, Math.max(0.001, smooth(show * 1.4)), 1);
      desk.scale.set(1, Math.max(0.001, smooth(show * 1.4 - 0.25)), 1);
    },
    get show() { return show; },
  };
}
