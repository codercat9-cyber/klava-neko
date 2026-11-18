// Жесты Клавы: руки и ноги двигаются двухзвенной IK — жест задаёт, куда поставить кисть (к уху, ко рту,
// на бедро), а плечо и локоть считаются сами. Так жест не разваливается при любом повороте, наклоне и
// приседе: цели привязаны к костям тела. Ещё здесь кружка киберпива и подмигивание.
//
// Система координат для смещений (как мир в покое): +X — её ЛЕВАЯ сторона, +Y — вверх, +Z — вперёд, к зрителю.
// Кости — нормализованные VRM (в покое у всех единичный поворот, оси модели).
import * as THREE from 'three';
import { makeGamingDesk, DESK } from './gamedesk.js';

const smooth =(x) => (x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x));
/** Плавный подъём 0→1 на отрезке [a, b]. */
const ramp = (u, a, b) => smooth((u - a) / Math.max(1e-4, b - a));
/** Подъём на [a, b], спад на [c, d]. */
const bell = (u, a, b, c, d) => ramp(u, a, b) * (1 - ramp(u, c, d));
const TAU = Math.PI * 2;

const FINGERS = ['Index', 'Middle', 'Ring', 'Little'];
const PHALANX = ['Proximal', 'Intermediate', 'Distal'];

export function createGestures({ vrm, S, setEmotion, cursorPoint, gradientMap, ears, kimono }) {
  const H = vrm.humanoid;
  const N = (n) => H.getNormalizedBoneNode(n);
  const rig = H.normalizedHumanBonesRoot;
  vrm.scene.updateMatrixWorld(true);
  const sceneRestInv = vrm.scene.getWorldQuaternion(new THREE.Quaternion()).invert();
  const hipsRest = N('hips').position.clone();
  // в какую сторону поворот стопы вокруг её оси X поднимает пятку (носок остаётся на полу) — подобрано по снимку
  const HEEL_SIGN = -1;

  const limbs = {};
  for (const [key, up, lo, end] of [
    ['L', 'leftUpperArm', 'leftLowerArm', 'leftHand'], ['R', 'rightUpperArm', 'rightLowerArm', 'rightHand'],
    ['LL', 'leftUpperLeg', 'leftLowerLeg', 'leftFoot'], ['RL', 'rightUpperLeg', 'rightLowerLeg', 'rightFoot'],
  ]) {
    const u = N(up), l = N(lo), e = N(end);
    limbs[key] = {
      up: u, lo: l, end: e,
      restUp: l.position.clone().normalize(), restLo: e.position.clone().normalize(),
      a: l.position.length(), b: e.position.length(),
    };
  }
  // где стоят стопы в покое (в координатах модели): при приседе они остаются на полу
  const footRest = {
    LL: vrm.scene.worldToLocal(N('leftFoot').getWorldPosition(new THREE.Vector3())),
    RL: vrm.scene.worldToLocal(N('rightFoot').getWorldPosition(new THREE.Vector3())),
  };
  const fingerBones = { L: [], R: [] };
  for (const [side, pre] of [['L', 'left'], ['R', 'right']])
    for (const f of FINGERS) for (const p of PHALANX) { const b = N(pre + f + p); if (b) fingerBones[side].push({ b, f }); }
  const thumbs = { L: ['leftThumbProximal', 'leftThumbDistal'].map(N).filter(Boolean), R: ['rightThumbProximal', 'rightThumbDistal'].map(N).filter(Boolean) };

  // Юбка кимоно — жёсткий конус на тазе. При приседе колени уходят вперёд и протыкали её насквозь, а подол
  // уезжал под пол. Поэтому юбка укорачивается от пояса (подол остаётся у пола) и расширяется — будто ткань
  // легла на колени. Геометрия юбки в координатах мира в покое: ось Y от пола, пояс — на высоте таза.
  const skirt = kimono?.skirt ?? null;
  const skirtRest = skirt ? { pos: skirt.position.clone(), q: skirt.quaternion.clone(), scale: skirt.scale.clone() } : null;
  const waistY = N('hips').getWorldPosition(new THREE.Vector3()).y;
  const skirtLen = waistY - (footRest.LL.y - 0.05);
  function shapeSkirt(h, sit = 0) {
    if (!skirt) return;
    if (sit > 0.001) {
      // сидя: юбка лежит на бёдрах, подол — на подушке сиденья (под столешницей, за передней панелью стола).
      // Прежний «колокол до пола» растопыривался из-под стола на полкомнаты, а длинный — торчал под сиденьем
      const len = Math.max(0.1, waistY - DESK.sitDrop * sit - (DESK.seatY - 0.03));
      const s = THREE.MathUtils.lerp(1 - h / skirtLen, len / skirtLen, sit);
      // короткий конус — это почти одно кольцо подола: не раздувать вглубь (торчал назад сквозь кресло), а
      // сдвинуть вперёд на бёдра
      skirt.scale.set(skirtRest.scale.x * (1 + 0.15 * sit), skirtRest.scale.y * s, skirtRest.scale.z * (1 - 0.1 * sit));
      skirt.position.copy(skirtRest.pos).add(new THREE.Vector3(0, waistY * (1 - s), 0.2 * sit).applyQuaternion(skirtRest.q));
      return;
    }
    const s = 1 - h / skirtLen;
    // колоколом вширь и вперёд (+Z мира в покое — к зрителю): подол накрывает выдвинутые колени
    skirt.scale.set(skirtRest.scale.x * (1 + 2.4 * h), skirtRest.scale.y * s, skirtRest.scale.z * (1 + 3.2 * h));
    skirt.position.copy(skirtRest.pos).add(new THREE.Vector3(0, waistY * (1 - s), 0.55 * h).applyQuaternion(skirtRest.q));
  }

  // ---------- помощники ----------
  const qTmp = new THREE.Quaternion();
  /** Точка у кости: смещение (x — её левая сторона, z — вперёд) поворачивается вместе с костью. */
  function P(bone, x, y, z) {
    const b = typeof bone === 'string' ? N(bone) : bone;
    const q = b.getWorldQuaternion(new THREE.Quaternion()).multiply(sceneRestInv);
    return b.getWorldPosition(new THREE.Vector3()).add(new THREE.Vector3(x, y, z).applyQuaternion(q));
  }
  const E = (x, y, z) => new THREE.Quaternion().setFromEuler(new THREE.Euler(x, y, z));

  /**
   * Двухзвенная IK: кость «плечо» → «локоть» → конец. Сначала считается полное решение, потом локальные
   * повороты смешиваются с базовой позой по весу w — при нарастании жеста рука идёт плавно, без выкрутасов.
   */
  function solve(limb, target, pole, w) {
    const { up, lo, a, b } = limb;
    const baseUp = up.quaternion.clone(), baseLo = lo.quaternion.clone();
    const s0 = up.getWorldPosition(new THREE.Vector3());
    const d = target.clone().sub(s0);
    let dist = d.length();
    if (dist < 1e-4) return;
    const dir = d.divideScalar(dist);
    dist = THREE.MathUtils.clamp(dist, Math.abs(a - b) + 1e-3, (a + b) * 0.9995);
    const cosA = THREE.MathUtils.clamp((a * a + dist * dist - b * b) / (2 * a * dist), -1, 1);
    const sinA = Math.sqrt(1 - cosA * cosA);
    const pv = pole.clone().sub(s0);
    pv.addScaledVector(dir, -pv.dot(dir));
    if (pv.lengthSq() < 1e-8) pv.set(0, -1, 0).addScaledVector(dir, dir.y);
    pv.normalize();
    const elbow = s0.clone().addScaledVector(dir, a * cosA).addScaledVector(pv, a * sinA);

    const uDir = elbow.clone().sub(s0).normalize().applyQuaternion(up.parent.getWorldQuaternion(qTmp).invert());
    const qUp = new THREE.Quaternion().setFromUnitVectors(limb.restUp, uDir);
    up.quaternion.copy(qUp);
    up.updateMatrixWorld(true);
    const e0 = lo.getWorldPosition(new THREE.Vector3());
    const fDir = s0.clone().addScaledVector(dir, dist).sub(e0).normalize().applyQuaternion(up.getWorldQuaternion(qTmp).invert());
    const qLo = new THREE.Quaternion().setFromUnitVectors(limb.restLo, fDir);
    up.quaternion.copy(baseUp).slerp(qUp, w);
    lo.quaternion.copy(baseLo).slerp(qLo, w);
    up.updateMatrixWorld(true);
  }

  /** Кисть смотрит так, как задано в мире (ладонь-кружка, лапка), а не как вышло после IK. */
  function handWorld(limb, qWorldChar, w) {
    const want = sceneQ().multiply(qWorldChar);
    const local = limb.lo.getWorldQuaternion(new THREE.Quaternion()).invert().multiply(want);
    limb.end.quaternion.slerp(local, w);
  }
  /** Поворот модели сейчас (с поворотом из «Настроек вида») относительно покоя. */
  function sceneQ() { return vrm.scene.getWorldQuaternion(new THREE.Quaternion()).multiply(sceneRestInv); }

  /** Сжать пальцы: 0 — ладонь раскрыта, 1 — кулак. index и middle — отдельно (сигарета, жесты). */
  function curl(side, amount, w, index = amount, middle = amount) {
    const sign = side === 'L' ? 1 : -1;
    for (const { b, f } of fingerBones[side]) {
      const c = (f === 'Index' ? index : f === 'Middle' ? middle : amount) * 1.25;
      b.rotation.z += (sign * c - b.rotation.z) * w;
    }
    // большой палец: −X — вниз к ладони, Y к остальным пальцам (у левой +, у правой −); иначе выходил «лайк»
    for (const t of thumbs[side]) {
      t.rotation.x += (-0.65 * amount - t.rotation.x) * w;
      t.rotation.y += (sign * 0.55 * amount - t.rotation.y) * w;
    }
  }

  // ---------- кружка киберпива ----------
  const mug = makeMug(gradientMap);
  vrm.scene.parent?.add(mug);
  mug.visible = false;
  let mugShow = 0;

  // ---------- стильная сигарета и дым ----------
  const cigObj = makeCigarette(gradientMap);
  vrm.scene.parent?.add(cigObj.group);
  cigObj.group.visible = false;
  let cigShow = 0;

  const smoke = makeSmokeSystem(vrm.scene.parent);

  // ---------- ноутбук: парит перед ней, пока она печатает лапками или кодит ----------
  const laptop = makeLaptop(gradientMap);
  vrm.scene.parent?.add(laptop.group);
  laptop.group.visible = false;
  let laptopShow = 0;

  /**
   * Стук по клавишам ноутбука: кисти над клавиатурой (ноут стоит на P('chest', 0, -0.25, 0.33)), пальцы
   * вразнобой постукивают, голова наклонена к экрану. Клавиши «нажимаются» чаще, чем кисть ходит вперёд-назад.
   */
  function typingSpec(u) {
    const clkL = Math.max(0, Math.sin(u * TAU * 4.5));
    const clkR = Math.max(0, Math.cos(u * TAU * 5.2 + 0.7));
    const tL = P('chest', 0.10 + Math.sin(u * TAU * 0.7) * 0.012, -0.215 - clkL * 0.012, 0.30 + Math.sin(u * TAU * 1.1) * 0.012);
    const tR = P('chest', -0.10 + Math.cos(u * TAU * 0.6) * 0.012, -0.215 - clkR * 0.012, 0.30 + Math.cos(u * TAU * 1.3) * 0.012);
    return {
      // минус — вперёд/вниз (см. gamingSpec): раньше стояли плюсы, и она печатала, задрав подбородок
      body: {
        chest: [-0.05, 0, 0],
        head: [-0.14 + Math.sin(u * TAU * 1.5) * 0.012, Math.sin(u * TAU * 0.25) * 0.05, 0],
        spine: [-0.03, 0, 0],
      },
      L: { t: tL, pole: P('chest', 0.65, -0.55, 0.1), curl: 0.35 + clkL * 0.25, hand: [0.25, 0.1, -0.2] },
      R: { t: tR, pole: P('chest', -0.65, -0.55, 0.1), curl: 0.35 + clkR * 0.25, hand: [0.25, -0.1, 0.2] },
      laptop: 1,
    };
  }

  // ---------- игровое место: кресло, стол, ноут на столе, лапы на WASD и мышке ----------
  const desk = makeGamingDesk(gradientMap);
  vrm.scene.parent?.add(desk.group);
  let deskShow = 0;
  const vTmp = new THREE.Vector3();
  /** Точка игрового места (координаты мира в покое, как у жестов) — поворачивается вместе с моделью. */
  function R(x, y, z) { return new THREE.Vector3(x, y, z).applyQuaternion(sceneQ()).add(vrm.scene.getWorldPosition(vTmp)); }

  /**
   * Сидит в кресле и играет: левая лапка на WASD (пальцы перебирают — бег, редкие прыжки), правая на мышке
   * (мелкий «прицел», редкие резкие доводки и щелчки), корпус чуть вперёд, голова к экрану ноута.
   */
  function gamingSpec(u) {
    const [lx, lz] = DESK.laptop;
    const tapA = Math.max(0, Math.sin(u * TAU * 1.7)) ** 3;
    const tapB = Math.max(0, Math.sin(u * TAU * 2.3 + 1.1)) ** 3;
    const tL = R(lx + 0.07 + Math.sin(u * TAU * 0.4) * 0.006, DESK.deskY + 0.045 - tapA * 0.008, lz - 0.03);
    const flick = Math.sin(u * TAU * 0.23) > 0.96 ? Math.sin(u * TAU * 3) * 0.02 : 0;
    const mx = DESK.mouse[0] + Math.sin(u * TAU * 0.31) * 0.022 + Math.sin(u * TAU * 1.3) * 0.005 + flick;
    const mz = DESK.mouse[1] + Math.cos(u * TAU * 0.27) * 0.016;
    const click = Math.max(0, Math.sin(u * TAU * 0.9)) ** 8;
    const tR = R(mx, DESK.deskY + 0.05, mz - 0.06);
    return {
      sit: 1, desk: 1, laptop: 1, mouseAt: [mx, mz],
      // у этого рига +X у позвоночника и головы — назад (замер 28.09: spine.x += 0.3 увёл голову на 13 см от камеры);
      // к ноуту — минус: корпус чуть вперёд, голова вниз к экрану
      body: {
        spine: [-0.06, 0, 0], chest: [-0.08, 0, 0], neck: [-0.04, 0, 0],
        head: [-0.14 + Math.sin(u * TAU * 0.5) * 0.015, Math.sin(u * TAU * 0.13) * 0.05, 0],
      },
      L: { t: tL, pole: P('chest', 0.7, -0.6, 0.0), curl: 0.3 + tapA * 0.3, index: 0.3 + tapB * 0.3, hand: [0.3, 0.15, -0.25] },
      R: { t: tR, pole: P('chest', -0.7, -0.6, 0.0), curl: 0.55, index: 0.45 + click * 0.25, hand: [0.35, -0.1, 0.25] },
    };
  }

  // ---------- жесты ----------
  // Каждый: dur (с), fadeIn/fadeOut, face (эмоция при старте), spec(u, g) → что делать в момент u.
  // base — базовый слой (игровое место): другие жесты его не гасят, а играют поверх (руки перехватывают они).
  // Ответ spec: body — добавки к позе (радианы), L/R — цели кистей {t, pole, hand, curl}, squat — глубина
  // приседа (м), wink — 'L'|'R' и сила, mug — {on, tilt}, yawn — зевок 0..1, lookUp — взгляд вверх.
  const G = {
    arms: { dur: 6, fadeIn: 0.55, fadeOut: 0.6, face: ['relaxed', 0.5], spec: () => ({
      body: { head: [-0.05, 0, 0.05], chest: [-0.03, 0, 0] },
      L: { t: P('chest', -0.075, -0.04, 0.14), pole: P('chest', 0.6, -0.35, 0.25), curl: 0.7 },
      R: { t: P('chest', 0.08, -0.015, 0.165), pole: P('chest', -0.6, -0.35, 0.25), curl: 0.7 },
    }) },
    ear: { dur: 3.4, fadeIn: 0.5, fadeOut: 0.55, face: ['relaxed', 0.7], spec: (u) => {
      const k = bell(u, 0.5, 0.8, 2.6, 2.9);
      const sc = Math.sin(u * TAU * 5.5) * k;
      return {
        body: { head: [0.05, 0.05, -0.2 * ramp(u, 0.3, 0.9)], neck: [0, 0, -0.05] },
        R: { t: P('head', -0.075, 0.115 + sc * 0.012, -0.035 + Math.cos(u * TAU * 5.5) * k * 0.008), pole: P('chest', -0.7, 0.1, -0.1), curl: 0.35 + 0.25 * (0.5 + 0.5 * sc), hand: [0, 0, -0.6] },
        blink: 0.45 * k, earTwitch: 'R',
      };
    } },
    beer: { dur: 5.2, fadeIn: 0.5, fadeOut: 0.6, face: ['relaxed', 0.6], spec: (u) => {
      const up = ramp(u, 0.4, 1.1) * (1 - ramp(u, 3.4, 4.2));
      const sip = bell(u, 1.1, 1.5, 3.1, 3.5);
      const gulp = Math.sin((u - 1.1) * TAU * 1.6) * sip;

      const handRest = P('chest', -0.16, -0.12, 0.20);
      // Кружка ко рту: раньше y=-0.082 (это ниже подбородка), кружка не поднималась к губам.
      // Правильные губы Vita — y=+0.002 в системе кости head. Но mug держит вершина ручки в ладони,
      // а полусферические сосок кружки выше — компенсируем -0.03. Итог y=-0.028 (кружка касается губ).
      const handMouth = P('head', -0.068, -0.028 + 0.005 * gulp, 0.115);
      const t = handRest.lerp(handMouth, up);

      const pole = P('chest', -0.85, -0.65, -0.05);
      const tiltX = -0.42 * sip - 0.06 * gulp;
      const tiltY = 0.16 * sip;
      const headBack = -0.20 * sip + 0.03 * gulp;
      const mouthOpen = 0.40 * sip + 0.08 * gulp;

      return {
        body: {
          head: [headBack, 0.04 * up, -0.03 * up],
          neck: [-0.05 * sip, 0, 0],
          chest: [0.03 * up, 0, 0],
        },
        R: {
          t,
          pole,
          curl: 0.8,
          handMug: true,
          handTilt: tiltX,
        },
        mug: { on: 1, tiltX, tiltY },
        mouth: { aa: mouthOpen, oh: 0.25 * sip },
        blink: 0.65 * sip,
        earTwitch: u > 4.2 ? 'R' : null,
      };
    } },
    cheers: { dur: 3.2, fadeIn: 0.45, fadeOut: 0.55, face: ['happy', 0.85], spec: (u) => {
      const up = ramp(u, 0.2, 0.8) * (1 - ramp(u, 2.4, 3.0));
      const clink = Math.exp(-((u - 1.35) ** 2) / 0.008);
      const t = P('chest', -0.08, 0.08, 0.35 + 0.06 * clink);
      const pole = P('chest', -0.75, -0.60, 0.0);
      return {
        body: { chest: [0.04, -0.06 * up, 0], head: [0, -0.04 * up, 0.04 * up] },
        R: { t, pole, curl: 0.8, handMug: true },
        mug: { on: 1, tiltX: 0.10 * clink, tiltY: -0.08 * up },
        wink: ['R', bell(u, 1.1, 1.35, 1.8, 2.1)],
      };
    } },
    smoke: { dur: 6.4, fadeIn: 0.5, fadeOut: 0.6, face: ['relaxed', 0.75], spec: (u) => {
      // 0.3..1.0: подъем сигареты к губам; 2.6..3.4: отвод лапки в сторону
      const up = ramp(u, 0.3, 1.0) * (1 - ramp(u, 2.5, 3.3));
      // 1.0..2.5: смачный напас (затяжка)
      const puff = bell(u, 1.0, 1.3, 2.2, 2.5);
      // 3.4..5.2: выдох густого дыма
      const exhale = bell(u, 3.4, 3.8, 4.8, 5.3);

      const handRest = P('chest', -0.22, -0.16, 0.18);
      // Рука у рта: правее центра лица, на высоте губ, выдвинута вперед на 3 см перед лицом (не втыкаясь в подбородок!)
      const handMouth = P('head', -0.068, -0.065, 0.140);
      const handSide = P('chest', -0.28, -0.12, 0.24);

      let t;
      if (u < 2.7) {
        t = handRest.lerp(handMouth, up);
      } else {
        const toSide = ramp(u, 2.7, 3.5);
        t = handMouth.lerp(handSide, toSide);
      }

      const pole = P('chest', -0.85, -0.5, 0.0);
      // При затяжке сигарета смотрит вперед-вправо (к руке), чуть вниз: tiltX = -0.10, tiltY = -0.32
      const tiltX = -0.10 * puff + 0.18 * exhale;
      const tiltY = -0.32 * puff - 0.15 * exhale;
      const headBack = -0.14 * puff - 0.18 * exhale;
      const headTilt = 0.04 * puff + 0.08 * exhale;

      // Уголёк: в покое 0.25, на затяжке ярко вспыхивает до 1.0
      const emberIntensity = 0.25 + 0.75 * puff;

      return {
        body: {
          head: [headBack, headTilt, -0.02 * up],
          neck: [-0.04 * puff - 0.06 * exhale, 0, 0],
          chest: [0.03 * up + 0.04 * exhale, 0, 0],
        },
        R: {
          t,
          pole,
          curl: 0.85,
          handCig: true,
          handTilt: tiltX,
        },
        cig: { on: 1, tiltX, tiltY, ember: emberIntensity, puff },
        exhale,
        mouth: {
          oh: 0.22 * puff + 0.44 * exhale,
          aa: 0.02 * puff + 0.12 * exhale,
        },
        blink: 0.60 * puff + 0.25 * exhale,
        earTwitch: u > 4.5 ? 'R' : null,
      };
    } },
    wave: { dur: 2.8, fadeIn: 0.45, fadeOut: 0.55, face: ['relaxed', 0.7], spec: (u) => {
      const sw = Math.sin(u * TAU * 2.2) * bell(u, 0.4, 0.7, 2.1, 2.4);
      return {
        body: { head: [0, 0, 0.08] },
        R: { t: P('chest', -0.3 + sw * 0.05, 0.43, 0.1), pole: P('chest', -0.8, -0.3, -0.1), curl: 0.08, hand: [0, 0, sw * 0.3] },
      };
    } },
    hips: { dur: 5, fadeIn: 0.5, fadeOut: 0.6, face: ['angry', 0.25], spec: () => ({
      body: { chest: [-0.05, 0, 0], head: [-0.06, 0, 0.07] },
      L: { t: P('hips', 0.16, 0.08, 0.01), pole: P('chest', 0.8, -0.1, -0.35), curl: 0.75 },
      R: { t: P('hips', -0.16, 0.08, 0.01), pole: P('chest', -0.8, -0.1, -0.35), curl: 0.75 },
    }) },
    think: { dur: 3.6, fadeIn: 0.5, fadeOut: 0.55, face: ['relaxed', 0.3], spec: () => ({
      body: { head: [-0.05, 0.1, 0.1] },
      R: { t: P('head', -0.02, -0.065, 0.1), pole: P('chest', -0.4, -0.5, 0.1), curl: 0.6 },
      L: { t: P('chest', -0.04, -0.1, 0.17), pole: P('chest', 0.5, -0.4, 0.2), curl: 0.4 },
      lookUp: 1,
    }) },
    stretch: { dur: 4.2, fadeIn: 0.8, fadeOut: 0.8, face: ['relaxed', 0.5], spec: (u) => {
      const k = bell(u, 0.6, 1.4, 2.8, 3.5);
      return {
        body: { spine: [-0.08 * k, 0, 0], chest: [-0.1 * k, 0, 0], head: [-0.12 * k, 0, 0] },
        L: { t: P('head', 0.1, 0.52, 0.02), pole: P('chest', 0.9, 0.2, -0.1), curl: 0.2 },
        R: { t: P('head', -0.1, 0.52, 0.02), pole: P('chest', -0.9, 0.2, -0.1), curl: 0.2 },
        yawn: bell(u, 1.0, 1.6, 2.6, 3.2), blink: 0.9 * bell(u, 1.0, 1.4, 2.8, 3.2),
      };
    } },
    shrug: { dur: 2.0, fadeIn: 0.35, fadeOut: 0.5, face: ['relaxed', 0.3], spec: (u) => {
      const k = bell(u, 0.2, 0.5, 1.3, 1.7);
      return {
        body: { head: [0, 0, 0.12 * k], shoulders: 0.14 * k },
        L: { t: P('hips', 0.3, 0.12, 0.2), pole: P('chest', 0.8, -0.6, -0.2), curl: 0.1, hand: [0, 0, 0.9] },
        R: { t: P('hips', -0.3, 0.12, 0.2), pole: P('chest', -0.8, -0.6, -0.2), curl: 0.1, hand: [0, 0, -0.9] },
      };
    } },
    point: { dur: 2.6, fadeIn: 0.4, fadeOut: 0.6, spec: () => {
      const sh = N('rightUpperArm').getWorldPosition(new THREE.Vector3());
      const c = cursorPoint();
      const dir = c ? c.clone().sub(sh).normalize() : P('chest', -0.3, 0.1, 1).sub(sh).normalize();
      return { R: { t: sh.clone().addScaledVector(dir, 0.43), pole: P('chest', -0.6, -0.6, -0.2), curl: 0.85, index: 0 } };
    } },
    // лапка на мышке: пока друг разрешил ей водить курсором — рука тянется к курсору, кисть — кошачья лапка
    paw: { dur: Infinity, fadeIn: 0.4, fadeOut: 0.5, spec: (u) => {
      const sh = N('rightUpperArm').getWorldPosition(new THREE.Vector3());
      const c = cursorPoint();
      const dir = c ? c.clone().sub(sh).normalize() : P('chest', -0.3, 0, 1).sub(sh).normalize();
      return {
        body: { chest: [0.04, -0.1, 0] },
        R: { t: sh.clone().addScaledVector(dir, 0.41 + Math.sin(u * 3) * 0.006), pole: P('chest', -0.6, -0.7, -0.1), curl: 0.8, hand: [0.5, 0, 0] },
      };
    } },
    nod: { dur: 1.3, fadeIn: 0.15, fadeOut: 0.25, spec: (u) => ({ body: { head: [0.16 * Math.sin(u * TAU * 1.8) * bell(u, 0, 0.15, 0.9, 1.2), 0, 0] } }) },
    no: { dur: 1.4, fadeIn: 0.15, fadeOut: 0.25, face: ['relaxed', 0.2], spec: (u) => ({ body: { head: [0, 0.22 * Math.sin(u * TAU * 2) * bell(u, 0, 0.15, 1.0, 1.3), 0] } }) },
    wink: { dur: 1.3, fadeIn: 0.15, fadeOut: 0.3, face: ['relaxed', 0.8], spec: (u) => ({
      body: { head: [0, 0.04, -0.12 * bell(u, 0, 0.2, 0.8, 1.2)] }, wink: ['R', bell(u, 0.1, 0.25, 0.7, 0.95)],
    }) },
    lean: { dur: 3.8, fadeIn: 0.7, fadeOut: 0.7, face: ['relaxed', 0.5], spec: () => ({
      body: { spine: [0.14, 0, 0], chest: [0.1, 0, 0], head: [-0.2, 0, 0.1] },
      L: { t: P('hips', 0.07, 0.0, -0.17), pole: P('hips', 0.7, 0.1, -0.2), curl: 0.4 },
      R: { t: P('hips', -0.07, 0.0, -0.17), pole: P('hips', -0.7, 0.1, -0.2), curl: 0.4 },
    }) },
    squat: { dur: 4.2, fadeIn: 0.8, fadeOut: 0.8, face: ['relaxed', 0.5], spec: () => ({
      squat: 0.2,
      body: { spine: [0.18, 0, 0], chest: [0.05, 0, 0], head: [-0.2, 0, 0] },
      L: { t: P('hips', 0.13, -0.2, 0.3), pole: P('chest', 0.8, -0.2, -0.1), curl: 0.5 },
      R: { t: P('hips', -0.13, -0.2, 0.3), pole: P('chest', -0.8, -0.2, -0.1), curl: 0.5 },
    }) },
    turn: { dur: 3.2, fadeIn: 0.6, fadeOut: 0.7, spec: () => ({ body: { spine: [0, 0.3, 0], chest: [0, 0.2, 0], head: [0, 0.35, 0] } }) },
    clap: { dur: 2.4, fadeIn: 0.4, fadeOut: 0.5, face: ['relaxed', 0.8], spec: (u) => {
      const open = 0.5 + 0.5 * Math.cos(u * TAU * 3);
      return {
        L: { t: P('chest', 0.025 + 0.07 * open, 0.07, 0.3), pole: P('chest', 0.7, -0.5, 0), curl: 0.1, hand: [0, 0, -1.3] },
        R: { t: P('chest', -0.025 - 0.07 * open, 0.07, 0.3), pole: P('chest', -0.7, -0.5, 0), curl: 0.1, hand: [0, 0, 1.3] },
      };
    } },
    // печатает на ноутбуке: пока идёт работа с клавиатурой (typing — до команды «хватит»), по разговору о коде — typingBrief
    typing: { dur: Infinity, fadeIn: 0.6, fadeOut: 0.7, face: ['relaxed', 0.4], spec: (u) => typingSpec(u) },
    typingBrief: { dur: 7, fadeIn: 0.6, fadeOut: 0.8, face: ['relaxed', 0.4], spec: (u) => typingSpec(u) },
    // играет: сидит в кресле за столом, пока хост не скажет «хватит» (gesture_stop gaming)
    gaming: { dur: Infinity, fadeIn: 1.4, fadeOut: 1.2, base: true, spec: (u) => gamingSpec(u) },
    nya: { dur: 3.6, fadeIn: 0.4, fadeOut: 0.55, face: ['happy', 0.95], spec: (u) => {
      const bob = Math.sin(u * TAU * 1.2) * bell(u, 0.4, 0.8, 2.8, 3.2);
      const tilt = Math.sin(u * TAU * 1.2) * 0.14 * bell(u, 0.3, 0.7, 2.9, 3.3);
      const pawWiggle = Math.sin(u * TAU * 3.5) * 0.015;
      const tL = P('head', 0.14 + pawWiggle, -0.065, 0.115);
      const tR = P('head', -0.14 - pawWiggle, -0.065, 0.115);
      const poleL = P('chest', 0.7, -0.2, 0.1);
      const poleR = P('chest', -0.7, -0.2, 0.1);
      return {
        body: {
          head: [0.04, bob * 0.05, tilt],
          chest: [-0.02, 0, 0],
        },
        L: { t: tL, pole: poleL, curl: 0.88, hand: [0.6, 0.2, -0.7] },
        R: { t: tR, pole: poleR, curl: 0.88, hand: [0.6, -0.2, 0.7] },
        blink: 0.4 * bell(u, 0.8, 1.2, 2.2, 2.6),
        earTwitch: u > 2.0 ? 'L' : 'R',
      };
    } },
    facepalm: { dur: 3.8, fadeIn: 0.35, fadeOut: 0.5, face: ['angry', 0.3], spec: (u) => {
      const hit = ramp(u, 0.3, 0.8) * (1 - ramp(u, 2.8, 3.5));
      const sigh = bell(u, 0.8, 1.3, 2.5, 3.0);
      const t = P('head', -0.02, 0.035, 0.125);
      const pole = P('chest', -0.65, -0.4, 0.2);
      return {
        body: {
          head: [0.22 * hit + sigh * 0.04, 0.08 * hit, -0.08 * hit],
          neck: [0.06 * hit, 0, 0],
          chest: [0.05 * hit, 0, 0],
        },
        R: { t, pole, curl: 0.65, hand: [-0.1, 0.2, -0.2] },
        L: { t: P('hips', 0.22, 0.04, 0.1), pole: P('chest', 0.6, -0.5, -0.1), curl: 0.4 },
        blink: 0.9 * hit,
      };
    } },
  };

  const active = [];
  // spin — поворот всей её вокруг себя (рад, танцевальный оборот), walk — шаги по рабочему столу (м, к её левой руке): их
  // применяет страница — поворачивает сцену и двигает окно хозяина
  const out = { wink: { L: 0, R: 0 }, yawn: 0, blink: 0, lookUp: 0, busyArms: false, sit: 0, mouth: { aa: 0, oh: 0, ih: 0 }, spin: 0, walk: 0 };

  /** Сыграть жест. freeze — отладка: застыть на этой секунде жеста в полную силу (подгонка поз). */
  function play(name, { freeze } = {}) {
    const def = G[name];
    if (!def) return false;
    if (freeze === undefined) {
      // базовый слой уже идёт — второй не нужен; новый обычный жест гасит обычные, базовый не трогает
      if (def.base && active.some((a) => a.name === name && !a.stopping)) return true;
      for (const a of active) if (!a.stopping && (def.base ? a.name === name : !a.def.base)) { a.stopping = true; a.stopAt = a.t; }
    } else {
      active.length = 0;
    }
    const entry = { name, def, t: freeze ?? 0, stopping: false, stopAt: 0, freeze: freeze !== undefined };
    // базовый слой — первым: жесты после него перехватывают руки, а посадка и мебель остаются
    if (def.base) active.unshift(entry); else active.push(entry);
    if (def.face && setEmotion) setEmotion(def.face[0], def.face[1], Math.min(def.dur, 6));
    return true;
  }
  function stop(name) {
    for (const a of active) if ((!name || a.name === name) && !a.stopping) { a.stopping = true; a.stopAt = a.t; }
  }
  function weight(a) {
    if (a.freeze) return 1;
    const fin = ramp(a.t, 0, a.def.fadeIn);
    const fout = a.stopping ? 1 - ramp(a.t - a.stopAt, 0, a.def.fadeOut)
      : Number.isFinite(a.def.dur) ? 1 - ramp(a.t, a.def.dur - a.def.fadeOut, a.def.dur) : 1;
    return fin * fout;
  }

  /** Сброс того, что базовая поза не трогает (ноги, кисти, пальцы, таз), — иначе кончившийся жест залипнет. */
  function resetExtras() {
    N('hips').position.copy(hipsRest);
    for (const n of ['leftUpperLeg', 'rightUpperLeg', 'leftLowerLeg', 'rightLowerLeg', 'leftFoot', 'rightFoot', 'leftHand', 'rightHand', 'leftShoulder', 'rightShoulder'])
      N(n)?.quaternion.identity();
    // пальцы в покое чуть согнуты — прямые «доской» выглядят кукольно
    for (const side of ['L', 'R']) {
      const sign = side === 'L' ? 1 : -1;
      for (const { b } of fingerBones[side]) b.rotation.set(0, 0, sign * 0.22);
      for (const t of thumbs[side]) t.rotation.set(-0.12, sign * 0.12, 0);
    }
  }

  function update(dt) {
    resetExtras();
    out.wink.L = out.wink.R = 0;
    out.yawn = out.blink = out.lookUp = 0;
    out.mouth.aa = out.mouth.oh = out.mouth.ih = 0;
    out.busyArms = false;
    out.sit = 0;
    out.spin = 0;
    out.walk = 0;
    let mugTarget = 0, mugTiltX = 0, mugTiltY = 0, mugHand = null;
    let cigTarget = 0, cigTiltX = 0, cigTiltY = 0, cigHand = null, cigEmber = 0.25, cigExhale = 0, cigPuff = 0;
    let laptopTarget = 0, deskTarget = 0, mouseAt = null;
    const hooks = [];
    for (let i = active.length - 1; i >= 0; i--) {
      const a = active[i];
      if (a.freeze) continue;
      a.t += dt;
      const done = a.stopping ? a.t - a.stopAt >= a.def.fadeOut : a.t >= a.def.dur;
      if (done) active.splice(i, 1);
    }
    if (active.length === 0) {
      mugShow = Math.max(0, mugShow - dt * 4); placeMug(null, 0, 0);
      cigShow = Math.max(0, cigShow - dt * 4); placeCig(null, 0, 0, 0.2, 0);
      laptopShow = Math.max(0, laptopShow - dt * 2); placeLaptop(dt, S.t);
      deskShow = Math.max(0, deskShow - dt * 2.5); placeDesk(null);
      smoke.update(dt, S.t, cigShow, null, 0, null);
      shapeSkirt(0);
      return;
    }

    vrm.scene.updateWorldMatrix(true, false);
    let squatNow = 0, sitNow = 0;
    for (const a of active) {
      const w = weight(a);
      if (w <= 0) continue;
      const sp = a.def.spec(a.t, a);
      const body = sp.body ?? {};
      for (const [bone, key] of [['spine', 'spine'], ['chest', 'chest'], ['neck', 'neck'], ['head', 'head']]) {
        const v = body[key];
        if (v) { const r = N(bone).rotation; r.x += v[0] * w; r.y += v[1] * w; r.z += v[2] * w; }
      }
      if (body.shoulders) {
        N('leftShoulder').rotation.z += body.shoulders * w;
        N('rightShoulder').rotation.z -= body.shoulders * w;
      }
      if (sp.squat) {
        // как в настоящем приседе: таз опускается и уходит назад (+Z у нормализованного таза — от зрителя),
        // тогда колени выходят вперёд меньше и не протыкают юбку
        N('hips').position.y -= sp.squat * w;
        N('hips').position.z += sp.squat * 0.5 * w;
        squatNow += sp.squat * w;
      }
      // покачивание тазом вбок (Карамельдансен): таз уходит в сторону, стопы стоят где стояли (нужен stance — ноги по IK)
      if (sp.hipsX) N('hips').position.x += sp.hipsX * w;
      // таз «восьмёркой»: бедро на стороне толчка поднимается (крен), таз чуть доворачивается; позвоночник гасит крен сам
      if (sp.hipsRoll) N('hips').rotation.z += sp.hipsRoll * w;
      if (sp.hipsYaw) N('hips').rotation.y += sp.hipsYaw * w;
      if (sp.spin) out.spin += sp.spin * w;
      if (sp.walk) out.walk += sp.walk * w;
      if (sp.sit) {
        // на сиденье: таз вниз ровно до подушки (тазобедренные суставы над ней на толщину бедра); у другой мебели
        // (кровать, массажное кресло, купель — props.js) своя высота: sitDrop, и таз может съехать назад/вперёд: hipsZ
        N('hips').position.y -= (sp.sitDrop ?? DESK.sitDrop) * sp.sit * w;
        if (sp.hipsZ) N('hips').position.z += sp.hipsZ * sp.sit * w;
        sitNow = Math.max(sitNow, sp.sit * w);
      }
      if (sp.desk) {
        // мебель вырастает сразу, до посадки; убирается, когда она почти встала
        deskTarget = Math.max(deskTarget, a.stopping ? (w > 0.25 ? 1 : 0) : 1);
        if (sp.mouseAt) mouseAt = sp.mouseAt;
      }
      rig.updateMatrixWorld(true);
      if (sp.squat || sp.sit || sp.stance) {
        for (const key of ['LL', 'RL']) {
          const side = key === 'LL' ? 1 : -1;
          // сидя — стопы вперёд и чуть врозь, колени смотрят вперёд-вверх (бедро лежит на сиденье);
          // в приседе — стопы на месте, колени врозь и вперёд (меньше выпирают из-под юбки)
          // стопы: у стола — на полу; у другой мебели — где скажет жест (feet: [x от середины, y, z] — на матрасе, на
          // подставке кресла); колени смотрят туда, куда knees (по умолчанию — вперёд-вверх)
          const f = sp.feet;
          // стоя: stance — сдвиг стоп от покоя [наружу, вперёд] в осях самой модели (широкая стойка певца); «наружу» —
          // по знаку стопы в покое (у этой модели левая стопа на −X сцены: side-знак скрещивал ноги)
          const st = sp.stance?.[key];
          let foot = sp.sit ? (f ? R(side * f[0], f[1], f[2]) : R(side * DESK.foot[0], footRest[key].y, DESK.foot[1]))
            : vrm.scene.localToWorld(footRest[key].clone());
          if (st && !sp.sit) {
            // «наружу» — по тому, с какой стороны от другой стопы стоит эта (в покое), а не от таза: в танце таз уезжает
            // за стопу, и знак переворачивался — ногу тянуло внутрь, ноги заплетались
            const o = P('hips', 0, 0, 0);
            const other = vrm.scene.localToWorld(footRest[key === 'LL' ? 'RL' : 'LL'].clone());
            const out = Math.sign(P('hips', 1, 0, 0).sub(o).dot(foot.clone().sub(other))) || side;
            foot = foot.add(P('hips', out * st[0], 0, st[1]).sub(o).setY(0));
            // шаг: стопа над полом (st[2], м) и пятка вверх на носок (st[3], рад) — щиколотка поднимается на длину стопы
            const lift = st[2] ?? 0, heel = Math.max(0, st[3] ?? 0);
            if (lift || heel) foot.y += (lift + 0.11 * Math.sin(heel)) * vrm.scene.scale.y;
          }
          const kn = sp.knees ?? [0.22, 0.95, 1.6];
          solve(limbs[key], foot, sp.sit ? R(side * kn[0], kn[1], kn[2]) : P('hips', side * 0.35, -0.3, 0.8), w);
          // стопа стоит ровно на полу, как бы ни согнулась нога
          const lo = limbs[key].lo;
          lo.updateMatrixWorld(true);
          const want = vrm.scene.getWorldQuaternion(new THREE.Quaternion());
          limbs[key].end.quaternion.slerp(lo.getWorldQuaternion(new THREE.Quaternion()).invert().multiply(want), w);
          // пятка вверх: стопа на носке (поворот вокруг её поперечной оси)
          const heelUp = !sp.sit && sp.stance?.[key]?.[3] ? Math.max(0, sp.stance[key][3]) : 0;
          if (heelUp) limbs[key].end.rotateX(heelUp * w * HEEL_SIGN);
        }
        rig.updateMatrixWorld(true);
      }
      for (const side of ['L', 'R']) {
        const s = sp[side];
        if (!s) continue;
        out.busyArms = true;
        const limb = limbs[side];
        solve(limb, s.t, s.pole, w);
        limb.lo.updateMatrixWorld(true);
        if (s.handMug) {
          const tilt = s.handTilt ?? 0;
          handWorld(limb, E(tilt, 0, 0), w);
          mugHand = limb.end;
        }
        else if (s.handCig) {
          const tilt = s.handTilt ?? 0;
          handWorld(limb, E(tilt, 0.15, -0.25), w);
          cigHand = limb.end;
        }
        else if (s.hand) limb.end.quaternion.slerp(E(...s.hand), w);
        if (s.handCig) {
          curl(side, 0.85, w, 0.18, 0.22);
          cigHand = limb.end;
        } else {
          curl(side, s.curl ?? 0.3, w, s.index ?? s.curl ?? 0.3);
        }
        if (s.handMug) mugHand = limb.end;
      }
      if (sp.wink) out.wink[sp.wink[0]] = Math.max(out.wink[sp.wink[0]], sp.wink[1] * w);
      if (sp.yawn) out.yawn = Math.max(out.yawn, sp.yawn * w);
      if (sp.blink) out.blink = Math.max(out.blink, sp.blink * w);
      if (sp.lookUp) out.lookUp = Math.max(out.lookUp, sp.lookUp * w);
      if (sp.earTwitch && ears && Math.random() < dt * 3) ears[sp.earTwitch === 'R' ? 1 : 0].twitch = 1;
      if (sp.mouth) {
        if (sp.mouth.aa) out.mouth.aa = Math.max(out.mouth.aa, sp.mouth.aa * w);
        if (sp.mouth.oh) out.mouth.oh = Math.max(out.mouth.oh, sp.mouth.oh * w);
        if (sp.mouth.ih) out.mouth.ih = Math.max(out.mouth.ih, sp.mouth.ih * w);
      }
      if (sp.mug) {
        mugTarget = Math.max(mugTarget, sp.mug.on * w);
        mugTiltX = sp.mug.tiltX ?? (typeof sp.mug.tilt === 'number' ? sp.mug.tilt : 0);
        mugTiltY = sp.mug.tiltY ?? 0;
      }
      if (sp.cig) {
        cigTarget = Math.max(cigTarget, sp.cig.on * w);
        cigTiltX = sp.cig.tiltX ?? 0;
        cigTiltY = sp.cig.tiltY ?? 0;
        cigEmber = sp.cig.ember ?? 0.25;
        cigPuff = Math.max(cigPuff, (sp.cig.puff ?? 0) * w);
      }
      if (sp.exhale) {
        cigExhale = Math.max(cigExhale, sp.exhale * w);
      }
      if (sp.laptop) laptopTarget = Math.max(laptopTarget, sp.laptop * w);
      if (sp.after) hooks.push([sp.after, w]);
    }
    rig.updateMatrixWorld(true);
    // жесты со своими предметами (еда — snacks.js): ставят их после того, как руки встали по IK
    for (const [f, w] of hooks) f(w, { L: limbs.L.end, R: limbs.R.end });
    out.sit = sitNow;
    shapeSkirt(squatNow, sitNow);
    deskShow += (deskTarget - deskShow) * Math.min(1, dt * 3);
    placeDesk(mouseAt);
    mugShow += (mugTarget - mugShow) * Math.min(1, dt * 10);
    placeMug(mugHand, mugTiltX, mugTiltY);

    cigShow += (cigTarget - cigShow) * Math.min(1, dt * 10);
    const cigTipPos = placeCig(cigHand, cigTiltX, cigTiltY, cigEmber, cigPuff);
    // Рот кошки: центр губ (x = 0, y = -0.058, z = 0.115)
    const mouthPos = P('head', 0.0, -0.058, 0.115);
    smoke.update(dt, S.t, cigShow, cigTipPos, cigExhale, mouthPos);

    // ноут выезжает чуть медленнее рук и чуть позже закрывается — не прыгает
    laptopShow += (laptopTarget - laptopShow) * Math.min(1, dt * 5);
    placeLaptop(dt, S.t);
  }

  // Ноутбук перед грудью, клавиатура под кистями. Ориентация — ровно по горизонту (sceneQ), а не по наклону
  // груди: он висит на неоновой «подушке», его никто не держит. Сначала появляется закрытым, потом раскрывается.
  function placeLaptop(dt, t) {
    laptop.group.visible = laptopShow > 0.02;
    if (!laptop.group.visible) return;
    if (laptop.group.parent !== vrm.scene.parent) vrm.scene.parent?.add(laptop.group);
    const onDesk = deskShow > 0.5;
    laptop.pad.visible = !onDesk;
    laptop.setMode(onDesk ? 'game' : 'code');
    if (onDesk) {
      // на столе: корпус лежит на столешнице (0 группы — верх корпуса, толщина корпуса 12 мм)
      laptop.group.position.copy(R(DESK.laptop[0], DESK.deskY + 0.012, DESK.laptop[1]));
    } else {
      laptop.group.position.copy(P('chest', 0, -0.25, 0.33));
      laptop.group.position.y += Math.sin(t * 1.7) * 0.003;   // чуть покачивается на подушке
    }
    laptop.group.quaternion.copy(sceneQ());
    laptop.group.scale.setScalar(Math.max(0.001, smooth(Math.min(1, laptopShow * 2.2))));
    laptop.setOpen(smooth((laptopShow - 0.35) / 0.65));
    laptop.update(dt, t);
  }

  /** Игровое место — у ног модели, повёрнуто вместе с ней; мышка ходит за правой лапкой. */
  function placeDesk(mouseAt) {
    desk.setShow(deskShow);
    if (!desk.group.visible) return;
    if (desk.group.parent !== vrm.scene.parent) vrm.scene.parent?.add(desk.group);
    desk.group.position.copy(vrm.scene.getWorldPosition(vTmp));
    desk.group.quaternion.copy(sceneQ());
    if (mouseAt) desk.mouse.position.set(mouseAt[0], DESK.deskY + 0.004, mouseAt[1]);
  }

  // Кружка у ладони правой руки. Позиция — в мире (localToWorld от ладони); ориентация — вертикально
  // (sceneQ) + локальный tilt при чоканье/питье. Держим НЕ через parenting: parent=scene, чтобы жёстко
  // контролировать вертикальность независимо от того как рука повёрнута в IK-жесте.
  function placeMug(hand, tiltX, tiltY) {
    mug.visible = mugShow > 0.02 && !!hand;
    if (!mug.visible) return;
    if (mug.parent !== vrm.scene.parent) vrm.scene.parent?.add(mug);
    const palmLocal = new THREE.Vector3(0.042, 0.005, -0.022);
    mug.position.copy(hand.localToWorld(palmLocal.clone()));
    mug.quaternion.copy(sceneQ().multiply(E(tiltX, tiltY, 0)));
    mug.scale.setScalar(Math.max(0.001, smooth(mugShow)));
  }

  // Сигарета зажата между указательным и средним пальчиками правой руки. Тот же подход:
  // ориентация от sceneQ (вертикально в мире), при затяжке — лерпаем к губам головы.
  function placeCig(hand, tiltX, tiltY, ember, puffWeight = 0) {
    cigObj.group.visible = cigShow > 0.02 && !!hand;
    if (!cigObj.group.visible) return null;
    if (cigObj.group.parent !== vrm.scene.parent) vrm.scene.parent?.add(cigObj.group);

    const pIdx = N('rightIndexIntermediate')?.getWorldPosition(new THREE.Vector3()) || N('rightIndexProximal')?.getWorldPosition(new THREE.Vector3());
    const pMid = N('rightMiddleIntermediate')?.getWorldPosition(new THREE.Vector3()) || N('rightMiddleProximal')?.getWorldPosition(new THREE.Vector3());
    let pos = pIdx && pMid ? pIdx.clone().add(pMid).multiplyScalar(0.5) : hand.localToWorld(new THREE.Vector3(0.042, 0.015, -0.015));
    const qCig = sceneQ().multiply(E(tiltX, tiltY, 0));
    cigObj.group.quaternion.copy(qCig);
    if (puffWeight > 0.001) {
      // Губы Vita: x=0.008, y=0.002, z=0.113 от кости head (между верхней и нижней губами, чуть правее — курит правой).
      const lipPos = P('head', 0.008, 0.002, 0.113);
      const vButt = new THREE.Vector3(0, 0, -cigObj.buttOffset).applyQuaternion(qCig);
      const posAtLips = lipPos.clone().sub(vButt);
      pos.lerp(posAtLips, puffWeight);
    }
    cigObj.group.position.copy(pos);
    cigObj.group.scale.setScalar(Math.max(0.001, smooth(cigShow)));

    const emb = THREE.MathUtils.clamp(ember ?? 0.25, 0.1, 1.0);
    if (emb > 0.6) {
      cigObj.emberMat.color.setHex(0xff7722);
      cigObj.light.intensity = (emb - 0.4) * 1.5;
    } else {
      cigObj.emberMat.color.setHex(0xbb2200);
      cigObj.light.intensity = 0.08;
    }

    return cigObj.group.localToWorld(new THREE.Vector3(0, 0, cigObj.tipLen));
  }

  /** Добавить жест снаружи (snacks.js: еда, рыг, пук) — те же правила, что у жестов выше. */
  function define(name, def) { G[name] = def; }
  return { play, stop, update, out, names: Object.keys(G), active, mug, cig: cigObj.group, smoke: smoke.group, laptop: laptop.group, desk: desk.group, P, N, E, R, sceneQ, define, has: (n) => n in G,
    isOn: (n) => active.some((a) => a.name === n && !a.stopping) };
}

// Кибер-ноутбук: графитовый корпус с неоновой кромкой, клавиатура с подсветкой, на крышке — светящаяся
// кошачья мордочка, на экране бежит код и подсвечивает ей лицо. Точка (0,0,0) — центр клавиатуры (верх корпуса);
// +Z — к зрителю (там шарнир), −Z — к ней. Крышка раскрывается поворотом вокруг шарнира.
function makeLaptop(gradientMap) {
  const W = 0.27, D = 0.185, T = 0.012, LT = 0.006;
  const g = new THREE.Group();
  const toon = (color, extra = {}) => new THREE.MeshToonMaterial({ color, gradientMap, ...extra });
  const neon = new THREE.MeshBasicMaterial({ color: 0x38e8ff });
  /** Неоновая рамка по краю прямоугольника w×d (четыре тонкие полоски), центр — (0, y, z). */
  const rim = (w, d, y, z = 0) => {
    const r = new THREE.Group();
    const s = 0.0028, h = 0.0022;
    for (const [bw, bd, px, pz] of [[w + s, s, 0, d / 2], [w + s, s, 0, -d / 2], [s, d, w / 2, 0], [s, d, -w / 2, 0]]) {
      const bar = new THREE.Mesh(new THREE.BoxGeometry(bw, h, bd), neon);
      bar.position.set(px, 0, pz);
      r.add(bar);
    }
    r.position.set(0, y, z);
    return r;
  };

  const base = new THREE.Mesh(new THREE.BoxGeometry(W, T, D), toon(0x2a2f3a));
  base.position.y = -T / 2;
  const edge = rim(W, D, -T * 0.5);

  // клавиатура: рисунок клавиш с голубой подсветкой (одна плоскость вместо сотни кубиков)
  const kc = document.createElement('canvas');
  kc.width = 512; kc.height = 256;
  const kx = kc.getContext('2d');
  kx.fillStyle = '#12151c'; kx.fillRect(0, 0, 512, 256);
  const rows = [14, 14, 13, 12, 10];
  for (let r = 0; r < rows.length; r++) {
    const n = rows[r], kw = 480 / n;
    for (let i = 0; i < n; i++) {
      const wide = r === 4 && i === 4 ? 4 : 1;   // пробел
      if (r === 4 && i > 4 && i < 8) continue;
      const x = 16 + i * kw + (r % 2) * 6, y = 14 + r * 30;
      kx.fillStyle = '#1f2531'; kx.fillRect(x, y, kw * wide - 5, 24);
      kx.strokeStyle = 'rgba(56,232,255,0.55)'; kx.lineWidth = 1.2; kx.strokeRect(x + 0.5, y + 0.5, kw * wide - 6, 23);
    }
  }
  // тачпад ближе к ней
  kx.strokeStyle = 'rgba(56,232,255,0.8)'; kx.lineWidth = 2; kx.strokeRect(186, 176, 140, 64);
  const keysTex = new THREE.CanvasTexture(kc);
  keysTex.colorSpace = THREE.SRGBColorSpace;
  const keys = new THREE.Mesh(new THREE.PlaneGeometry(W * 0.94, D * 0.9), new THREE.MeshBasicMaterial({ map: keysTex }));
  keys.rotation.x = -Math.PI / 2;
  // верх рисунка (ряд цифр) — к шарниру (+Z), тачпад — к ней (−Z)
  keys.rotation.z = Math.PI;
  keys.position.y = 0.0003;

  // крышка на шарнире у дальнего края
  const hinge = new THREE.Group();
  hinge.position.set(0, 0, D / 2);
  const lid = new THREE.Group();
  const shell = new THREE.Mesh(new THREE.BoxGeometry(W, LT, D), toon(0x2a2f3a));
  shell.position.set(0, LT / 2 + 0.0005, -D / 2);
  const lidEdge = rim(W, D, LT * 0.6, -D / 2);

  // экран (смотрит вниз, когда закрыт; раскрытый — на неё): бегущий код
  const sc = document.createElement('canvas');
  sc.width = 256; sc.height = 176;
  const sx = sc.getContext('2d');
  const screenTex = new THREE.CanvasTexture(sc);
  screenTex.colorSpace = THREE.SRGBColorSpace;
  const screen = new THREE.Mesh(new THREE.PlaneGeometry(W * 0.92, D * 0.86), new THREE.MeshBasicMaterial({ map: screenTex }));
  screen.rotation.x = Math.PI / 2;
  screen.position.set(0, 0.00045, -D / 2);

  // спина крышки (к зрителю, когда раскрыта): неоновая кошачья мордочка
  const lc = document.createElement('canvas');
  lc.width = 256; lc.height = 256;
  const lx = lc.getContext('2d');
  lx.shadowColor = '#38e8ff'; lx.shadowBlur = 14;
  lx.strokeStyle = '#7ff3ff'; lx.lineWidth = 7; lx.lineJoin = 'round';
  lx.beginPath();
  lx.moveTo(70, 118); lx.lineTo(78, 52); lx.lineTo(112, 88);
  lx.quadraticCurveTo(128, 82, 144, 88);
  lx.lineTo(178, 52); lx.lineTo(186, 118);
  lx.quadraticCurveTo(192, 196, 128, 200);
  lx.quadraticCurveTo(64, 196, 70, 118);
  lx.stroke();
  lx.fillStyle = '#ff4fa8'; lx.shadowColor = '#ff4fa8';
  lx.beginPath(); lx.ellipse(104, 136, 9, 13, 0, 0, TAU); lx.ellipse(152, 136, 9, 13, 0, 0, TAU); lx.fill();
  lx.fillStyle = '#7ff3ff'; lx.shadowColor = '#38e8ff';
  lx.font = 'bold 30px Segoe UI, sans-serif'; lx.textAlign = 'center';
  lx.fillText('KLAVA', 128, 244);
  const logoTex = new THREE.CanvasTexture(lc);
  logoTex.colorSpace = THREE.SRGBColorSpace;
  const logo = new THREE.Mesh(new THREE.PlaneGeometry(0.1, 0.1),
    new THREE.MeshBasicMaterial({ map: logoTex, transparent: true, depthWrite: false }));
  logo.rotation.x = -Math.PI / 2;
  logo.position.set(0, LT + 0.0012, -D / 2);

  // свет экрана на её лицо
  const glow = new THREE.PointLight(0x66d9ff, 0, 0.7);
  glow.position.set(0, -0.06, -D / 2);

  lid.add(shell, lidEdge, screen, logo, glow);
  hinge.add(lid);

  // неоновая «подушка» снизу — на чём ноут парит
  const pad = new THREE.Mesh(new THREE.RingGeometry(0.06, 0.13, 40),
    new THREE.MeshBasicMaterial({ color: 0x38e8ff, transparent: true, opacity: 0.22, side: THREE.DoubleSide, depthWrite: false }));
  pad.rotation.x = -Math.PI / 2;
  pad.position.y = -T - 0.012;

  g.add(base, edge, keys, hinge, pad);
  g.traverse((o) => { o.frustumCulled = false; o.renderOrder = 3; });

  const words = ['const', 'let', 'if', 'await', 'return', 'purr()', 'nya', 'meow', 'git', 'push', 'fix', 'bug', '=>', '{', '}', '42',
    'klava.say()', 'paw.click()', 'while', 'true', 'mur', 'sudo', 'catch', 'async', 'fish++'];
  const colors = ['#7ff3ff', '#ff4fa8', '#b8ff6a', '#ffd166', '#c9d1ff'];
  const lines = [];
  let acc = 0;
  function addLine() {
    const indent = Math.floor(Math.random() * 4) * 14;
    const n = 2 + Math.floor(Math.random() * 4);
    const parts = [];
    for (let i = 0; i < n; i++) parts.push({ w: words[Math.floor(Math.random() * words.length)], c: colors[Math.floor(Math.random() * colors.length)] });
    lines.push({ indent, parts });
    if (lines.length > 11) lines.shift();
  }
  for (let i = 0; i < 8; i++) addLine();
  function drawScreen(t) {
    sx.fillStyle = '#0a0f1c'; sx.fillRect(0, 0, 256, 176);
    sx.font = '12px Consolas, monospace';
    lines.forEach((ln, i) => {
      let x = 10 + ln.indent;
      for (const p of ln.parts) { sx.fillStyle = p.c; sx.fillText(p.w, x, 18 + i * 14); x += sx.measureText(p.w).width + 6; }
    });
    if (Math.sin(t * 6) > 0) { sx.fillStyle = '#7ff3ff'; sx.fillRect(10 + (lines.at(-1)?.indent ?? 0), 18 + lines.length * 14 - 10, 7, 12); }
    screenTex.needsUpdate = true;
  }
  drawScreen(0);

  let openNow = 0;
  // игра на экране (когда ноут на игровом столе): коридор шутера, враги-кубики, прицел, HUD
  const foes = [{ x: 60, s: 1 }, { x: 180, s: 0.7 }];
  function drawGame(t) {
    sx.fillStyle = '#0b1026'; sx.fillRect(0, 0, 256, 88);
    const floor = sx.createLinearGradient(0, 88, 0, 176);
    floor.addColorStop(0, '#1b2a3a'); floor.addColorStop(1, '#0e1620');
    sx.fillStyle = floor; sx.fillRect(0, 88, 256, 88);
    sx.strokeStyle = 'rgba(56,232,255,0.35)'; sx.lineWidth = 1;
    for (let i = -6; i <= 6; i++) { sx.beginPath(); sx.moveTo(128 + i * 8, 88); sx.lineTo(128 + i * 60, 176); sx.stroke(); }
    const sway = Math.sin(t * 0.7) * 18;
    for (const f of foes) {
      f.x += Math.sin(t * 1.3 + f.s * 5) * 1.6;
      if (f.x < 20 || f.x > 236) f.x = 128;
      const h = 30 * f.s, x = f.x + sway;
      sx.fillStyle = '#ff4fa8'; sx.fillRect(x - h / 4, 96 - h / 2, h / 2, h);
      sx.fillStyle = '#ffd166'; sx.fillRect(x - h / 8, 96 - h / 2 + 3, h / 4, 3);
    }
    if (Math.sin(t * 9) > 0.7) { sx.fillStyle = 'rgba(255,209,102,0.8)'; sx.beginPath(); sx.arc(150, 150, 10, 0, TAU); sx.fill(); }
    sx.strokeStyle = '#b8ff6a'; sx.lineWidth = 2;
    sx.beginPath(); sx.moveTo(118, 96); sx.lineTo(124, 96); sx.moveTo(132, 96); sx.lineTo(138, 96);
    sx.moveTo(128, 86); sx.lineTo(128, 92); sx.moveTo(128, 100); sx.lineTo(128, 106); sx.stroke();
    sx.font = 'bold 12px Consolas, monospace'; sx.fillStyle = '#7ff3ff';
    sx.fillText('HP 100', 8, 168); sx.fillStyle = '#ffd166'; sx.fillText(`AMMO ${30 - Math.floor(t * 3) % 30}`, 190, 168);
    screenTex.needsUpdate = true;
  }
  let mode = 'code';

  return {
    group: g,
    /** Неоновая «подушка» снизу: на столе не нужна. */
    pad,
    /** 0 — закрыт, 1 — раскрыт на 110°. */
    setOpen(k) {
      openNow = THREE.MathUtils.clamp(k, 0, 1);
      hinge.rotation.x = 1.92 * openNow;
      glow.intensity = 0.55 * openNow;
    },
    /** 'code' — бежит код (печатает), 'game' — на экране игра (сидит за игровым столом). */
    setMode(m) { mode = m === 'game' ? 'game' : 'code'; },
    /** Экран живёт, пока крышка открыта. */
    update(dt, t) {
      if (openNow < 0.3) return;
      acc += dt;
      if (mode === 'game') {
        if (acc < 0.06) return;
        acc = 0;
        drawGame(t);
        return;
      }
      if (acc < 0.18) return;
      acc = 0;
      if (Math.random() < 0.6) addLine();
      drawScreen(t);
    },
  };
}

// Кружка киберпива: стекло, янтарное пиво, шапка пены и неоновые кольца — пиво-то кибер.
// Точка (0,0,0) всей группы g — вершина ручки, точно в ладонь.
function makeMug(gradientMap) {
  const g = new THREE.Group();
  const R = 0.034, Hh = 0.105;
  const toon = (color, extra = {}) => new THREE.MeshToonMaterial({ color, gradientMap, ...extra });
  const glass = new THREE.Mesh(new THREE.CylinderGeometry(R, R * 0.93, Hh, 28, 1, true),
    toon(0xd8ecff, { transparent: true, opacity: 0.32, side: THREE.DoubleSide, depthWrite: false }));
  const beer = new THREE.Mesh(new THREE.CylinderGeometry(R * 0.94, R * 0.87, Hh * 0.74, 28), toon(0xf0a11a));
  beer.position.y = -Hh * 0.12;
  const foam = new THREE.Mesh(new THREE.CylinderGeometry(R * 0.99, R * 0.95, Hh * 0.2, 28), toon(0xfffaf0));
  foam.position.y = Hh * 0.33;
  const foamBlobs = new THREE.Group();
  for (let i = 0; i < 7; i++) {
    const a = (i / 7) * Math.PI * 2;
    const blob = new THREE.Mesh(new THREE.SphereGeometry(R * (0.32 + (i % 3) * 0.06), 12, 8), toon(0xffffff));
    blob.position.set(Math.cos(a) * R * 0.55, Hh * 0.45, Math.sin(a) * R * 0.55);
    foamBlobs.add(blob);
  }
  const neon = new THREE.MeshBasicMaterial({ color: 0x38e8ff });
  const rings = new THREE.Group();
  for (const y of [-Hh * 0.3, Hh * 0.12]) {
    const ring = new THREE.Mesh(new THREE.TorusGeometry(R * 1.02, 0.0028, 6, 40), neon);
    ring.rotation.x = Math.PI / 2;
    ring.position.y = y;
    rings.add(ring);
  }
  const handle = new THREE.Mesh(new THREE.TorusGeometry(Hh * 0.28, 0.0065, 8, 20, Math.PI), toon(0xd8ecff, { transparent: true, opacity: 0.7 }));
  handle.rotation.z = Math.PI / 2;
  handle.position.set(-R, 0, 0);

  // Сдвиг стакана на +gripShift, чтобы точка хвата ручки была в (0,0,0)
  const gripShift = R + Hh * 0.28;
  const body = new THREE.Group();
  body.add(glass, beer, foam, foamBlobs, rings, handle);
  body.position.set(gripShift, 0, 0);
  g.add(body);
  g.traverse((o) => { o.frustumCulled = false; o.renderOrder = 3; });
  return g;
}

// Тонкая стильная сигарета с фильтром, золотым ободком, пеплом и тлеющим угольком.
function makeCigarette(gradientMap) {
  const g = new THREE.Group();
  const R = 0.0036;
  const filterLen = 0.024;
  const bodyLen = 0.058;
  const ashLen = 0.004;

  const toon = (color, extra = {}) => new THREE.MeshToonMaterial({ color, gradientMap, ...extra });

  // Фильтр: оранжево-песочный цвет
  const filter = new THREE.Mesh(new THREE.CylinderGeometry(R, R, filterLen, 16), toon(0xd58f52));
  filter.position.z = filterLen * 0.5;
  filter.rotation.x = Math.PI / 2;

  // Золотое колечко
  const ring = new THREE.Mesh(new THREE.CylinderGeometry(R * 1.03, R * 1.03, 0.0018, 16), new THREE.MeshBasicMaterial({ color: 0xffd700 }));
  ring.position.z = filterLen - 0.001;
  ring.rotation.x = Math.PI / 2;

  // Белая гильза
  const paper = new THREE.Mesh(new THREE.CylinderGeometry(R, R, bodyLen, 16), toon(0xf8f8f8));
  paper.position.z = filterLen + bodyLen * 0.5;
  paper.rotation.x = Math.PI / 2;

  // Серый пепел
  const ash = new THREE.Mesh(new THREE.CylinderGeometry(R * 0.96, R, ashLen, 16), toon(0x555555));
  ash.position.z = filterLen + bodyLen + ashLen * 0.5;
  ash.rotation.x = Math.PI / 2;

  // Тлеющий уголёк на самом кончике
  const emberMat = new THREE.MeshBasicMaterial({ color: 0xff3b00 });
  const ember = new THREE.Mesh(new THREE.SphereGeometry(R * 0.9, 12, 8), emberMat);
  ember.position.z = filterLen + bodyLen + ashLen;

  // Неоновый свет уголька при затяжке (подсвечивает лицо кошки)
  const light = new THREE.PointLight(0xff5500, 0, 0.35);
  light.position.z = filterLen + bodyLen + ashLen + 0.005;

  const body = new THREE.Group();
  body.add(filter, ring, paper, ash, ember, light);

  // Смещение: точка хвата между пальцами находится на стыке фильтра и гильзы (у золотого кольца).
  // Торец фильтра (который вставляется в рот) выступает назад по -Z на 18 мм!
  const buttOffset = filterLen * 0.75;
  body.position.z = -buttOffset;

  g.add(body);
  g.traverse((o) => { o.frustumCulled = false; o.renderOrder = 4; });

  const tipLen = filterLen + bodyLen + ashLen - buttOffset;
  return { group: g, emberMat, light, tipLen, buttOffset };
}

// Система частиц сизого сигаретного дыма: струйка от кончика и густой выдох изо рта
function makeSmokeSystem(parentGroup) {
  const MAX_SMOKE = 42;
  const smokeParticles = [];
  const group = new THREE.Group();
  parentGroup?.add(group);

  const geom = new THREE.SphereGeometry(0.012, 8, 6);
  for (let i = 0; i < MAX_SMOKE; i++) {
    const mat = new THREE.MeshBasicMaterial({
      color: 0xd8e4ed,
      transparent: true,
      opacity: 0,
      depthWrite: false,
    });
    const mesh = new THREE.Mesh(geom, mat);
    mesh.visible = false;
    group.add(mesh);
    smokeParticles.push({
      mesh,
      mat,
      active: false,
      life: 0,
      maxLife: 1.6,
      vel: new THREE.Vector3(),
      baseScale: 1,
    });
  }

  function spawn(pos, vel, maxLife, scale) {
    const p = smokeParticles.find((x) => !x.active);
    if (!p) return;
    p.active = true;
    p.life = 0;
    p.maxLife = maxLife;
    p.mesh.position.copy(pos);
    p.vel.copy(vel);
    p.baseScale = scale;
    p.mesh.scale.setScalar(scale);
    p.mat.opacity = 0;
    p.mesh.visible = true;
  }

  let lastTipTime = 0;
  let lastExhaleTime = 0;

  function update(dt, t, cigShow, tipPos, exhaleAmount, mouthPos) {
    // Тлеющая струйка дыма от кончика сигареты
    if (cigShow > 0.08 && tipPos && t - lastTipTime > 0.22) {
      lastTipTime = t;
      const v = new THREE.Vector3(
        (Math.random() - 0.5) * 0.02,
        0.05 + Math.random() * 0.03,
        (Math.random() - 0.5) * 0.02
      );
      spawn(tipPos, v, 1.3, 0.45 + Math.random() * 0.3);
    }
    // Густой выдох изо рта при затяжке (напасе)
    if (exhaleAmount > 0.12 && mouthPos && t - lastExhaleTime > 0.08) {
      lastExhaleTime = t;
      const forward = new THREE.Vector3(
        (Math.random() - 0.5) * 0.06,
        0.07 + Math.random() * 0.05,
        0.20 + Math.random() * 0.14
      );
      spawn(mouthPos, forward, 2.0 + Math.random() * 0.6, 0.85 + Math.random() * 0.55);
    }
    // Обновление частиц
    for (const p of smokeParticles) {
      if (!p.active) continue;
      p.life += dt;
      if (p.life >= p.maxLife) {
        p.active = false;
        p.mesh.visible = false;
        continue;
      }
      p.mesh.position.addScaledVector(p.vel, dt);
      p.vel.y += 0.04 * dt;
      p.vel.multiplyScalar(Math.max(0, 1 - dt * 0.85));
      const u = p.life / p.maxLife;
      p.mesh.scale.setScalar(p.baseScale * (1 + u * 4.8));
      const a = (u < 0.15 ? u / 0.15 : 1 - (u - 0.15) / 0.85) * 0.30;
      p.mat.opacity = Math.max(0, a);
    }
  }

  return { group, update };
}
