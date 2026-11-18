import * as THREE from 'three';

/**
 * Слёзы (эмоция [плач], друг 28.09: «грусть, слёзы»). Своих слёз у модели нет:
 *  - дорожка каждой слезы — по настоящей щеке: при старте лучи спереди ищут кожу лица (Face_00_SKIN) под глазом
 *    сверху вниз, путь хранится в осях кости головы — слёзы едут вместе с головой;
 *  - капля — вытянутая прозрачная голубая бусина с белым бликом; скатывается рывками (прилипает и срывается),
 *    к подбородку тает;
 *  - мокрый след — тонкая полоска по той же дорожке, проявляется, пока она плачет, и медленно сохнет.
 * cry(a): 0..1 — насколько сильно плачет сейчас (затухание — в klava.js).
 */
export function createTears(vrm) {
  const head = vrm.humanoid.getRawBoneNode('head');
  const face = [];
  let white = null;
  const matNames = (o) => (Array.isArray(o.material) ? o.material : [o.material]).map((m) => m?.name ?? '').join('|');
  vrm.scene.traverse((o) => {
    if (!o.isMesh) return;
    if (/Face_00_SKIN/.test(matNames(o))) face.push(o);   // кожа лица (не рот, не ресницы)
    if (/EyeWhite/.test(matNames(o))) white = o;
  });
  if (!head || !white || !face.length) return null;

  vrm.scene.updateMatrixWorld(true);
  const headInv = head.matrixWorld.clone().invert();
  // Глаза — по белкам (кости глаз у Vita сидят глубоко и ближе к носу, чем видимые глаза): для каждой стороны
  // середина по ширине и низ белка = линия нижнего века. Куда смотрит лицо — от кости головы к глазам
  // (VRM 0.x развёрнута на 180°, оси кости головы тут не помогут).
  const sides = [[], []];
  const v = new THREE.Vector3();
  for (let i = 0; i < white.geometry.attributes.position.count; i++) {
    white.getVertexPosition(i, v);
    white.localToWorld(v);
    sides[v.x > 0 ? 0 : 1].push(v.clone());
  }
  const headPos = head.getWorldPosition(new THREE.Vector3());
  const eyesAt = sides.filter((s) => s.length > 3).map((s) => {
    const c = s.reduce((a, p) => a.add(p), new THREE.Vector3()).divideScalar(s.length);
    c.y = Math.min(...s.map((p) => p.y));
    return c;
  });
  if (eyesAt.length < 2) return null;
  const mid = eyesAt[0].clone().add(eyesAt[1]).multiplyScalar(0.5);
  const fwd = mid.clone().sub(headPos).setY(0).normalize();
  const up = new THREE.Vector3(0, 1, 0);
  const ray = new THREE.Raycaster();
  const paths = [];
  for (const c of eyesAt) {
    const side = Math.sign(c.x - mid.x) || 1;   // наружу от середины лица
    const pts = [];
    // от нижнего века вниз по щеке ~6 см: низ белка прячется под кожей, поэтому начинаем чуть выше — первая кожа под
    // глазом и есть нижнее веко (лучи в глаз мимо кожи пропускаются)
    for (let i = 0; i <= 36; i++) {
      const drop = -0.008 + i * 0.0019;
      const drift = side * (0.002 + Math.max(0, i - 4) * 0.00012);   // щека уходит наружу — слеза немного тоже
      const p = c.clone().addScaledVector(up, -drop);
      p.x += drift;
      ray.set(p.clone().addScaledVector(fwd, 0.12), fwd.clone().negate());
      ray.far = 0.2;
      const hit = ray.intersectObjects(face, false)[0];
      if (!hit) { if (pts.length > 4) break; continue; }
      const n = hit.face ? hit.face.normal.clone().transformDirection(hit.object.matrixWorld) : fwd;
      pts.push(hit.point.clone().addScaledVector(n.dot(fwd) > 0 ? n : fwd, 0.0012).applyMatrix4(headInv));
    }
    if (pts.length >= 6) paths.push({ curve: new THREE.CatmullRomCurve3(pts), side });
  }
  if (!paths.length) return null;
  const fwdL = fwd.clone().transformDirection(headInv), upL = up.clone().transformDirection(headInv);
  const sideL = new THREE.Vector3(1, 0, 0).transformDirection(headInv);

  // По-анимешному, чтобы читалось и в полный рост: крупные капли с тёмно-голубой каймой (на светлой коже иначе не
  // видно), ручеёк — голубая полоса с белой серединкой, у нижнего века — блестящая «лужица».
  const root = new THREE.Group();
  head.add(root);
  const mat = (color, opacity) => new THREE.MeshBasicMaterial({ color, transparent: true, opacity, depthWrite: false });
  const rimMat = mat(0x4f9fe0, 0.9), dropMat = mat(0xb8e4ff, 0.92), glintMat = mat(0xffffff, 1);
  const trackRim = mat(0x6fb6ee, 0), trackMat = mat(0xa6dcff, 0), trackCore = mat(0xffffff, 0), poolMat = mat(0xc8ecff, 0);
  const dropGeo = new THREE.SphereGeometry(1, 14, 10);
  const tracks = [];
  for (const { curve, side } of paths) {
    for (const [r, m, order] of [[0.0021, trackRim, 2], [0.0015, trackMat, 3], [0.0005, trackCore, 4]]) {
      const t = new THREE.Mesh(new THREE.TubeGeometry(curve, 48, r, 6, false), m);
      t.renderOrder = order;
      root.add(t);
      tracks.push(t);
    }
    // лужица слёз вдоль нижнего века — к середине глаза от начала дорожки
    const pool = new THREE.Mesh(dropGeo, poolMat);
    pool.position.copy(curve.getPointAt(0)).addScaledVector(sideL, -side * 0.0035).addScaledVector(upL, 0.0006);
    pool.scale.set(0.0065, 0.0013, 0.0012);
    pool.renderOrder = 4;
    root.add(pool);
    tracks.push(pool);
  }

  const drops = [];
  function spawn(pi) {
    const g = new THREE.Group();
    const rim = new THREE.Mesh(dropGeo, rimMat);
    rim.scale.set(0.0043, 0.0059, 0.0022);
    rim.position.copy(fwdL).multiplyScalar(-0.0006);
    const body = new THREE.Mesh(dropGeo, dropMat);
    body.scale.set(0.0035, 0.0050, 0.0024);
    const glint = new THREE.Mesh(dropGeo, glintMat);
    glint.scale.setScalar(0.0011);
    glint.position.copy(upL).multiplyScalar(0.0017).addScaledVector(sideL, -0.0008).addScaledVector(fwdL, 0.0024);
    rim.renderOrder = 5; body.renderOrder = 6; glint.renderOrder = 7;
    g.add(rim, body, glint);
    root.add(g);
    drops.push({ g, pi, u: 0, v: 0.09 + Math.random() * 0.07, stick: 0.3 + Math.random() * 0.4, life: 0 });
  }

  let track = 0;
  const nextAt = paths.map(() => 0.2 + Math.random() * 0.6);
  let clock = 0;
  const tmp = new THREE.Vector3();
  return {
    update(dt, a) {
      clock += dt;
      track += ((a > 0.05 ? 1 : 0) - track) * Math.min(1, dt * (a > 0.05 ? 1.2 : 0.12));   // след сохнет медленно
      trackRim.opacity = 0.35 * track; trackMat.opacity = 0.55 * track; trackCore.opacity = 0.8 * track;
      poolMat.opacity = 0.85 * Math.min(track, a * 1.5);
      for (const m of tracks) m.visible = track > 0.01;
      if (a > 0.05) {
        for (let i = 0; i < paths.length; i++) {
          if (clock < nextAt[i]) continue;
          if (drops.filter((d) => d.pi === i).length < 2) spawn(i);
          nextAt[i] = clock + (1.4 + Math.random() * 1.8) / Math.max(0.4, a);
        }
      }
      for (let k = drops.length - 1; k >= 0; k--) {
        const d = drops[k];
        d.life += dt;
        // прилипла у века, потом едет рывками: скорость то падает, то растёт
        const go = d.life < d.stick ? 0.05 : 0.6 + 0.4 * Math.sin(d.life * 5 + k);
        d.u += d.v * go * dt;
        if (d.u >= 1) { root.remove(d.g); drops.splice(k, 1); continue; }
        paths[d.pi].curve.getPointAt(d.u, tmp);
        d.g.position.copy(tmp);
        const grow = Math.min(1, d.life / 0.35);        // набухает у века
        const fade = d.u > 0.8 ? (1 - d.u) / 0.2 : 1;   // к подбородку тает
        d.g.scale.setScalar(Math.max(0.05, grow * fade));
      }
    },
    root,
  };
}
