import * as THREE from 'three';

/**
 * «Ахегао» — эмоция по просьбе друга (28.09): глаза закатываются под лоб, рот открыт, язык наружу, лицо горит
 * румянцем (румянец — beauty.js по S.shyUntil), голова чуть запрокинута (klava.js headLife).
 * Своих выражений для этого у модели нет:
 *  - язык — своя деталь на кости головы; рот найден по вершинам, которые двигает морф «aa» (центр ~1,469 м, 7,6 см
 *    вперёд от оси головы); выезжает изо рта, когда эмоция включается, и прячется обратно;
 *  - глаза — кости; штатный взгляд вверх у модели всего 16°, поэтому после vrm.update они докручиваются вверх
 *    ещё на ~20° поворотом в мировых осях (модель смотрит в +Z: вверх — поворот вокруг X на минус угол).
 */
export function createAhegao(vrm, { gradientMap } = {}) {
  const head = vrm.humanoid.getRawBoneNode('head');
  const eyes = ['leftEye', 'rightEye'].map((n) => vrm.humanoid.getRawBoneNode(n)).filter(Boolean);
  const mouth = findMouth(vrm) ?? new THREE.Vector3(0, 1.469, 0.076);

  // язык: сплюснутая капля, кончик вперёд и чуть вниз; основание — в глубине рта
  const geo = new THREE.SphereGeometry(1, 20, 12);
  geo.translate(0, 0, 1);   // основание в начале координат — растёт вперёд
  const mat = new THREE.MeshToonMaterial({ color: 0xe8768c, gradientMap: gradientMap ?? null });
  const tongue = new THREE.Mesh(geo, mat);
  tongue.scale.set(0.0105, 0.0036, 0.0175);   // длинный и плоский: свисает на нижнюю губу
  tongue.frustumCulled = false;
  const pivot = new THREE.Object3D();
  pivot.add(tongue);
  head?.add(pivot);
  if (head) {
    head.updateWorldMatrix(true, false);
    // основание у нижней губы внутри рта, наклон вперёд-вниз ~35°: прямо в камеру язык читался круглым блином,
    // круче — уходил под кожу и торчал из подбородка
    const base = mouth.clone().add(new THREE.Vector3(0, -0.003, -0.002));
    pivot.position.copy(head.worldToLocal(base));
    const want = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), 0.62);
    pivot.quaternion.copy(head.getWorldQuaternion(new THREE.Quaternion()).invert().multiply(want));
  }
  pivot.visible = false;

  const qa = new THREE.Quaternion(), qb = new THREE.Quaternion(), roll = new THREE.Quaternion();
  const axisX = new THREE.Vector3(1, 0, 0);

  return {
    /** Выражения лица поверх остальных (после губ и эмоций): рот открыт, веки чуть прикрыты. */
    expressions(em, a) {
      if (a < 0.01) return;
      em.setValue('aa', Math.max(em.getValue('aa') ?? 0, 0.72 * a));
      em.setValue('blink', Math.max(em.getValue('blink') ?? 0, 0.22 * a));
    },
    /** После vrm.update: глаза под лоб, язык выехал на долю a (0..1). */
    afterUpdate(a) {
      pivot.visible = a > 0.02;
      pivot.scale.set(1, 1, Math.max(0.001, a));
      if (a < 0.01) return;
      roll.setFromAxisAngle(axisX, -0.34 * a);   // радужки у верхнего века, не совсем белые глаза
      for (const eye of eyes) {
        eye.getWorldQuaternion(qa);
        eye.parent.getWorldQuaternion(qb);
        eye.quaternion.copy(qb.invert().multiply(roll.clone().multiply(qa)));
      }
    },
    tongue: pivot,
  };
}

/** Центр рта: вершины лица, которые заметно двигает морф «aa». null — не нашлось. */
function findMouth(vrm) {
  const ex = vrm.expressionManager?.getExpression('aa');
  const binds = ex?._binds ?? [];
  let found = null;
  vrm.scene.traverse((o) => {
    if (found || !o.isMesh || !o.morphTargetDictionary) return;
    const bind = binds.find((b) => b.primitives?.includes(o));
    const morph = bind ? o.geometry.morphAttributes.position?.[bind.index] : null;
    if (!morph) return;
    const pos = o.geometry.attributes.position;
    const sum = new THREE.Vector3(), v = new THREE.Vector3();
    let n = 0;
    for (let i = 0; i < pos.count; i++) {
      if (Math.hypot(morph.getX(i), morph.getY(i), morph.getZ(i)) <= 0.002) continue;
      o.localToWorld(v.fromBufferAttribute(pos, i));
      sum.add(v);
      n++;
    }
    if (n > 20) found = sum.divideScalar(n);
  });
  return found;
}
