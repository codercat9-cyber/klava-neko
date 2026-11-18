// Песня на сцене: Клава поёт свой кавер (или включает трек, который разобрала). Друг 30.09: «пусть почувствует ритм
// и споёт». Микс идёт в колонки, её голос отдельной дорожкой — только в анализатор губ (рот двигается по пению, а
// не по барабанам); доли и такты из разбора — в грув: кивок на долю и покачивание корпусом; строки текста — в облачко.
//
// Сообщение хозяина: { type: 'song', mix, vocal?, beats, downbeats, lines: [{t0, t1, text}], mood, offset?, until? }
// Страница отвечает { type: 'song_start' } и { type: 'song_end', natural } (natural — доиграла до конца).

const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const damp = (cur, target, rate, dt) => cur + (target - cur) * (1 - Math.exp(-rate * dt));

// кивок раз в ~0,65 с: на быстрых долях (beat_this иногда ловит дробление медленной баллады) — через одну-две
function nodEvery(beatLen) {
  let best = 1, bd = 1e9;
  for (const k of [1, 2, 3, 4]) {
    const d = Math.abs(k * beatLen - 0.65);
    if (d < bd) { bd = d; best = k; }
  }
  return best;
}

function indexAt(arr, t) {
  let lo = 0, hi = arr.length - 1;
  if (!arr.length || t < arr[0]) return -1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (arr[mid] <= t) lo = mid; else hi = mid - 1;
  }
  return lo;
}

export function createSongPlayer({ audio, S, setEmotion, gestures, showBubble, hideBubbleLater, send, subtitles }) {
  const analyser = audio.createAnalyser();
  analyser.fftSize = 1024;
  analyser.smoothingTimeConstant = 0.45;
  const mute = audio.createGain();
  mute.gain.value = 0;
  analyser.connect(mute).connect(audio.destination);   // анализатор должен стоять в графе, но голос второй раз не звучит
  const out = audio.createGain();
  out.connect(audio.destination);
  S.groove = { amt: 0, nod: 0, sway: 0 };
  let cur = null;

  async function load(url) {
    const r = await fetch(url, { cache: 'no-store' });
    if (!r.ok) throw new Error(`${url}: ${r.status}`);
    return audio.decodeAudioData(await r.arrayBuffer());
  }

  async function play(m) {
    stop(false);
    const token = {};
    cur = { token, m, loading: true };
    try {
      const [mix, voc] = await Promise.all([load(m.mix), m.vocal ? load(m.vocal) : Promise.resolve(null)]);
      if (cur?.token !== token) return;
      if (audio.state === 'suspended') await audio.resume();
      const off = clamp(m.offset ?? 0, 0, Math.max(0, mix.duration - 1));
      const end = m.until ? Math.min(m.until, mix.duration) : mix.duration;
      const t0 = audio.currentTime + 0.15;
      out.gain.value = m.volume ?? 1;
      const src = audio.createBufferSource();
      src.buffer = mix;
      src.connect(out);
      let vsrc = null;
      if (voc) {
        vsrc = audio.createBufferSource();
        vsrc.buffer = voc;
        vsrc.connect(analyser);
        vsrc.start(t0, off);
        vsrc.stop(t0 + (end - off));
      }
      src.start(t0, off);
      src.stop(t0 + (end - off));
      src.onended = () => { if (cur?.token === token) finish(true); };
      Object.assign(cur, { src, vsrc, t0, off, end, loading: false, line: -1 });
      // поза на песню (props.js): свой голос — к микрофону; ноты для клавиш — за синтезатор; скрипка; без голоса — танцует
      cur.pose = m.pose ?? (voc ? 'mic' : 'dance');
      // песенный танец сменяет короткий (иначе оба качали бы её разом)
      if (cur.pose === 'caramellSong' || cur.pose === 'dance') for (const g of ['caramell', 'danceBrief', 'dance', 'caramellSong']) if (g !== cur.pose) gestures?.stop(g);
      if (gestures?.has(cur.pose)) gestures.play(cur.pose);
      if (cur.pose === 'synth') gestures?.play('headphones');   // за синтезатором — в наушниках
      S.songNotes = m.keys ?? null;
      S.songSections = Array.isArray(m.sections) ? m.sections : null;   // части песни (куплет, припев…) — для танца по фазам
      S.songBeats = Array.isArray(m.beats) ? m.beats : null;
      const beats = m.beats ?? [];
      cur.every = beats.length > 2 ? nodEvery((beats[beats.length - 1] - beats[0]) / (beats.length - 1)) : 1;
      // настроение песни — на лицо на всю песню (грустная — грусть, нежная — мягкая улыбка)
      const mood = (m.mood ?? [])[0] ?? '';
      const emo = { 'Sad music': ['sad', 0.45], 'Tender music': ['relaxed', 0.6], 'Happy music': ['happy', 0.5],
        'Exciting music': ['happy', 0.4], 'Angry music': ['angry', 0.4], 'Scary music': ['surprised', 0.3], 'Funny music': ['happy', 0.6] }[mood];
      if (emo) setEmotion(emo[0], emo[1], end - off + 1);
      send({ type: 'song_start', title: m.title ?? '' });
    } catch (e) {
      if (cur?.token === token) cur = null;
      send({ type: 'error', message: `песня: ${e}` });
      send({ type: 'song_end', natural: false, error: String(e) });
    }
  }

  function releasePose(was) {
    S.groove.on = false;   // доли трека кончились — танцы снова считают сами
    if (was?.pose && gestures?.has(was.pose)) gestures.stop(was.pose);
    if (was?.pose === 'synth') gestures?.stop('headphones');
    S.songNotes = null;
    S.songPos = null;
    S.songSections = null;
    S.songBeats = null;
  }

  function finish(natural) {
    releasePose(cur);
    cur = null;
    S.singing = false;
    hideBubbleLater();
    send({ type: 'song_end', natural });
  }

  function stop(report = true) {
    if (!cur) return;
    const was = cur;
    cur = null;
    S.singing = false;
    releasePose(was);
    try { was.src?.stop(); } catch { /* ещё не стартовал */ }
    try { was.vsrc?.stop(); } catch { /* ещё не стартовал */ }
    if (report && !was.loading) send({ type: 'song_end', natural: false });
  }

  // живой бит от хозяина (она слушает звук компьютера): период доли и когда следующая
  function live(period, next) {
    if (!(period > 0.2 && period < 2)) return;
    S.grooveLive = { t0: S.t + (next ?? 0), period, until: S.t + 2.5 };
  }

  function update(dt) {
    const g = S.groove;
    if (!cur || cur.loading) {
      const L = S.grooveLive;
      if (L && S.t < L.until) {
        const k = nodEvery(L.period);
        const ph = (S.t - L.t0) / L.period;
        const i = ((Math.floor(ph) % (2 * k)) + 2 * k) % (2 * k);
        const frac = ph - Math.floor(ph);
        g.nod = ((i % k) + frac) / k;
        g.sway = (i + frac) / (2 * k);
        g.beat = ph / k;
        g.amt = damp(g.amt, 0.8, 2, dt);
        return;
      }
      g.amt = damp(g.amt, 0, 3, dt);
      return;
    }
    const pos = audio.currentTime - cur.t0 + cur.off;
    if (pos < cur.off) return;
    S.singing = !!cur.vsrc;
    S.songPos = pos;
    // заговорила поверх песни (ответ другу) — музыка приглушается, как у диджея, и возвращается после
    const vol = cur.m.volume ?? 1;
    const want = vol * (S.speaking ? 0.3 : 1);
    if (Math.abs((cur.gainWant ?? vol) - want) > 1e-3) {
      out.gain.setTargetAtTime(want, audio.currentTime, 0.12);
      cur.gainWant = want;
    }
    const b = cur.m.beats ?? [];
    const i = indexAt(b, pos);
    if (i >= 0 && i + 1 < b.length) {
      const ph = (pos - b[i]) / Math.max(0.05, b[i + 1] - b[i]);
      const k = cur.every;
      g.nod = ((i % k) + ph) / k;
      g.sway = ((i % (2 * k)) + ph) / (2 * k);
      g.beat = (i + ph) / k;   // счёт для танца (props.js): доля танца = кивок
      g.rawBeat = i + ph;      // каждая доля трека (Карамельдансен — толчок бедром на каждую)
      g.on = true;
      // поёт в микрофон — корпус стоит в стойке, без кивков и маятника (друг 30.09: «выглядит как марионетка»);
      // на скрипке и за синтезатором — тоже: инструмент и лапки стоят по своим точкам, а кивки и маятник качали тело под
      // ними — скрипка проваливалась в грудь, её «колбасило» (друг 02.10)
      g.amt = damp(g.amt, cur.pose === 'mic' || cur.pose === 'violin' || cur.pose === 'synth' || cur.pose === 'caramellSong' ? 0 : cur.m.dance ?? 1, 2, dt);
    } else {
      g.amt = damp(g.amt, 0, 2, dt);
    }
    const lines = cur.m.lines ?? [];
    if (subtitles()) {
      const li = lines.findIndex((l) => pos >= l.t0 - 0.25 && pos <= l.t1 + 0.4);
      if (li !== cur.line) {
        cur.line = li;
        if (li >= 0) showBubble(lines[li].text); else hideBubbleLater();
      }
    }
  }

  return {
    play, stop, update, live, analyser,
    get singing() { return !!cur && !cur.loading && !!cur.vsrc; },
    get playing() { return !!cur; },
  };
}

// Грув на кости: кивок на долю (резко вниз, мягко назад), корпус и бёдра покачиваются на два кивка.
export function applyGroove(B, g) {
  if (!g || g.amt < 0.01) return;
  const hit = Math.exp(-g.nod * 5.5);
  const sway = Math.sin(g.sway * Math.PI * 2);
  B.head.rotation.x += g.amt * 0.075 * hit;
  B.neck.rotation.x += g.amt * 0.03 * hit;
  B.spine.rotation.x += g.amt * 0.02 * hit;
  B.hips.rotation.z += g.amt * 0.035 * sway;
  B.spine.rotation.z -= g.amt * 0.02 * sway;
  B.neck.rotation.z += g.amt * 0.025 * sway;
}
