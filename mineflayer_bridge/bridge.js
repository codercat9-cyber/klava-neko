// Мост Клавы в Minecraft: бот mineflayer (отдельный игрок «Klava») и HTTP API для KlavaDesk на 127.0.0.1:8099.
//
//   GET  /status              — состояние бота: здоровье, голод, координаты, время суток, инвентарь, кто рядом
//   GET  /lan                 — миры, открытые по локальной сети («Открыть для сети» в игре): порт и название
//   GET  /events?since=N      — что случилось в игре с события N: чат игроков, смерть, кик
//   POST /connect {host?, port?, username?, version?} — без порта: мир из /lan (ждём объявления до 4 с);
//                               ответ — когда бот заспавнился или сервер отказал (до 25 с)
//   POST /disconnect
//   POST /chat {message}
//   POST /action {action, target?, x?, y?, z?, count?} — goto, come, follow, stop, collect (mine), fight (attack),
//                               look, jump, eat, give (toss), equip; ответ — когда действие закончено (до 90 с)
//
// Сервер должен пускать игроков без лицензии (online-mode=false): так работают миры по LAN в TLauncher и
// большинство «пиратских» серверов. Модовые сборки (Forge с модами) бот не пройдёт — у него нет модов.
const http = require('http');
const dgram = require('dgram');
const os = require('os');
const mineflayer = require('mineflayer');
const { pathfinder, Movements, goals } = require('mineflayer-pathfinder');

const PORT = 8099;
let bot = null;
let lastError = '';
const lanWorlds = new Map();   // "host:port" -> { motd, host, port, seen }
const events = [];             // { id, time, kind, from, text }
let eventId = 0;

function pushEvent(kind, from, text) {
  events.push({ id: ++eventId, time: new Date().toISOString(), kind, from, text });
  if (events.length > 200) events.shift();
}

// ---------- миры по локальной сети: игра раз в 1,5 с шлёт «[MOTD]…[/MOTD][AD]порт[/AD]» на 224.0.2.60:4445 ----------
function listenLan() {
  const sock = dgram.createSocket({ type: 'udp4', reuseAddr: true });
  sock.on('message', (msg, rinfo) => {
    const s = msg.toString('utf8');
    const motd = /\[MOTD\](.*?)\[\/MOTD\]/.exec(s);
    const ad = /\[AD\](\d+)\[\/AD\]/.exec(s);
    if (!ad) return;
    // мир открыт на этом же компьютере — ходим через 127.0.0.1: адрес, с которого ушло объявление, бывает адресом
    // VPN-адаптера, и подключение по нему уходит не туда
    const own = Object.values(os.networkInterfaces()).flat().some(i => i && i.address === rinfo.address);
    const host = own ? '127.0.0.1' : rinfo.address;
    const port = parseInt(ad[1], 10);
    lanWorlds.set(`${host}:${port}`, { motd: motd ? motd[1] : '', host, port, seen: Date.now() });
  });
  sock.on('error', err => console.log('[LAN] ' + err.message));
  sock.bind(4445, () => {
    try { sock.addMembership('224.0.2.60'); } catch (e) { console.log('[LAN] multicast: ' + e.message); }
  });
}

function freshLan() {
  const now = Date.now();
  return [...lanWorlds.values()].filter(w => now - w.seen < 10000).sort((a, b) => b.seen - a.seen);
}

// ---------- состояние ----------
function describeItem(i) { return i ? `${i.name}${i.count > 1 ? ' ×' + i.count : ''}` : null; }

function status() {
  if (!bot || !bot.entity) return { connected: false, error: lastError || undefined, lan: freshLan() };
  const pos = bot.entity.position;
  const nearby = [];
  for (const id in bot.entities) {
    const e = bot.entities[id];
    if (!e || e === bot.entity || !e.position) continue;
    const d = pos.distanceTo(e.position);
    if (d > 24) continue;
    if (e.type === 'player' || e.type === 'hostile' || e.type === 'mob' || e.type === 'animal' || e.type === 'passive') {
      nearby.push({ name: e.username || e.displayName || e.name, type: e.type, distance: Math.round(d * 10) / 10 });
    }
  }
  nearby.sort((a, b) => a.distance - b.distance);
  return {
    connected: true,
    username: bot.username,
    server: `${bot._client.socket?.remoteAddress || ''}:${bot._client.socket?.remotePort || ''}`,
    version: bot.version,
    health: bot.health, food: bot.food,
    position: { x: Math.round(pos.x), y: Math.round(pos.y), z: Math.round(pos.z) },
    dimension: bot.game?.dimension,
    time: bot.time ? (bot.time.isDay ? 'день' : 'ночь') : undefined,
    raining: bot.isRaining,
    holding: describeItem(bot.heldItem),
    inventory: bot.inventory.items().map(describeItem),
    players: Object.keys(bot.players).filter(n => n !== bot.username),
    nearby: nearby.slice(0, 12),
    busy: bot.pathfinder?.isMoving() || false,
  };
}

// ---------- подключение ----------
function connect(opts) {
  return new Promise(async resolve => {
    if (bot) { try { bot.quit(); } catch (e) { } bot = null; }
    lastError = '';
    let host = opts.host || '127.0.0.1';
    let port = parseInt(opts.port, 10) || 0;
    if (!port) {
      for (let i = 0; i < 40 && freshLan().length === 0; i++) await new Promise(r => setTimeout(r, 100));
      const w = freshLan()[0];
      if (!w) return resolve({ ok: false, error: 'Мир по локальной сети не найден: в игре Esc → «Открыть для сети» (или скажи порт/адрес сервера).' });
      host = w.host === '0.0.0.0' ? '127.0.0.1' : w.host;
      port = w.port;
    }
    const username = (opts.username || 'Klava').replace(/[^A-Za-z0-9_]/g, '').slice(0, 16) || 'Klava';
    const b = mineflayer.createBot({ host, port, username, version: opts.version || false, auth: 'offline', hideErrors: true });
    bot = b;
    b.loadPlugin(pathfinder);
    let done = false;
    const finish = r => { if (!done) { done = true; clearTimeout(timer); resolve(r); } };
    const timer = setTimeout(() => finish({ ok: false, error: `Сервер ${host}:${port} не пустил за 25 с` }), 25000);
    b.once('spawn', () => {
      const moves = new Movements(b);
      moves.allowParkour = true;
      moves.canDig = true;
      b.pathfinder.setMovements(moves);
      pushEvent('spawn', b.username, `зашла на ${host}:${port}`);
      finish({ ok: true, message: `Зашла на ${host}:${port} ником ${b.username} (версия ${b.version}).`, status: status() });
    });
    b.on('chat', (from, message) => { if (from !== b.username) pushEvent('chat', from, message); });
    b.on('whisper', (from, message) => { if (from !== b.username) pushEvent('whisper', from, message); });
    b.on('death', () => pushEvent('death', b.username, 'Клаву убили — возрождается'));
    b.on('kicked', reason => {
      lastError = 'кикнули: ' + (typeof reason === 'string' ? reason : JSON.stringify(reason)).slice(0, 300);
      pushEvent('kicked', '', lastError);
      finish({ ok: false, error: lastError });
    });
    b.on('error', err => { lastError = err.message; finish({ ok: false, error: err.message }); });
    b.on('end', () => { pushEvent('end', '', 'отключилась от сервера'); if (bot === b) bot = null; finish({ ok: false, error: lastError || 'соединение закрыто' }); });
  });
}

// ---------- действия ----------
const sleep = ms => new Promise(r => setTimeout(r, ms));

function withTimeout(p, ms, what) {
  return Promise.race([p, sleep(ms).then(() => { throw new Error(`${what}: не успела за ${ms / 1000} с`); })]);
}

function findPlayer(name) {
  const players = Object.values(bot.players).filter(p => p.username !== bot.username && p.entity);
  if (!players.length) return null;
  if (name) {
    const n = name.toLowerCase();
    return players.find(p => p.username.toLowerCase() === n) || players.find(p => p.username.toLowerCase().includes(n)) || null;
  }
  return players.sort((a, b) => bot.entity.position.distanceTo(a.entity.position) - bot.entity.position.distanceTo(b.entity.position))[0];
}

async function equipBestFor(block) {
  // лучший инструмент из инвентаря для этого блока (быстрее всех копает)
  let best = null, bestTime = block.digTime(null, false, false, false, [], bot.entity.effects);
  for (const item of bot.inventory.items()) {
    const t = block.digTime(item.type, false, false, false, [], bot.entity.effects);
    if (t < bestTime) { best = item; bestTime = t; }
  }
  if (best) await bot.equip(best, 'hand');
}

async function collect(name, count) {
  const want = (name || '').toLowerCase().replace(/\s+/g, '_');
  const ids = Object.values(bot.registry.blocksByName).filter(b => b.name === want || b.name.includes(want)).map(b => b.id);
  if (!ids.length) throw new Error(`не знаю такого блока: ${name} (пиши по-игровому: oak_log, stone, dirt, iron_ore…)`);
  let got = 0;
  const started = Date.now();
  while (got < count && Date.now() - started < 85000) {
    const block = bot.findBlock({ matching: ids, maxDistance: 48 });
    if (!block) { if (!got) throw new Error(`рядом (48 блоков) нет ${name}`); break; }
    await withTimeout(bot.pathfinder.goto(new goals.GoalLookAtBlock(block.position, bot.world, { reach: 4 })), 30000, 'дойти до блока');
    await equipBestFor(block);
    await bot.dig(block, true);
    got++;
    // подобрать выпавшее: встать на место блока
    try { await withTimeout(bot.pathfinder.goto(new goals.GoalNear(block.position.x, block.position.y, block.position.z, 1)), 5000, 'подобрать'); } catch (e) { }
    await sleep(300);
  }
  const have = bot.inventory.items().filter(i => i.name.includes(want)).reduce((s, i) => s + i.count, 0);
  return `Добыла ${got} × ${name}${got < count ? ` (хотела ${count})` : ''}. В инвентаре такого: ${have}.`;
}

async function fight(target) {
  const pick = () => {
    const pos = bot.entity.position;
    let best = null, bestD = 24;
    for (const id in bot.entities) {
      const e = bot.entities[id];
      if (!e || e === bot.entity || !e.position) continue;
      const nm = (e.username || e.name || '').toLowerCase();
      const ok = target ? nm.includes(target.toLowerCase()) : e.type === 'hostile';
      if (!ok) continue;
      const d = pos.distanceTo(e.position);
      if (d < bestD) { best = e; bestD = d; }
    }
    return best;
  };
  const e = pick();
  if (!e) return target ? `Не вижу рядом ${target}.` : 'Враждебных мобов рядом нет.';
  const name = e.username || e.name;
  bot.pathfinder.setGoal(new goals.GoalFollow(e, 2), true);
  const started = Date.now();
  let hits = 0;
  while (bot.entities[e.id] && e.isValid !== false && Date.now() - started < 30000) {
    if (bot.entity.position.distanceTo(e.position) < 3.5) {
      await bot.lookAt(e.position.offset(0, e.height * 0.8, 0), true);
      bot.attack(e);
      hits++;
    }
    await sleep(650);
  }
  bot.pathfinder.setGoal(null);
  const dead = !bot.entities[e.id];
  return dead ? `Завалила ${name} (ударов: ${hits}).` : `Дралась с ${name} 30 с (ударов: ${hits}) — ещё жив, отошла.`;
}

async function doAction(a) {
  const act = (a.action || '').toLowerCase();
  const target = a.target || '';
  switch (act) {
    case 'stop':
      bot.pathfinder.setGoal(null); bot.clearControlStates();
      return 'Остановилась.';
    case 'goto': {
      const x = +a.x, y = +a.y, z = +a.z;
      if ([x, y, z].some(Number.isNaN)) throw new Error('goto: нужны x, y, z');
      await withTimeout(bot.pathfinder.goto(new goals.GoalNear(x, y, z, 1)), 80000, 'дойти');
      return `Пришла к ${x} ${y} ${z}.`;
    }
    case 'come': {
      const p = findPlayer(target);
      if (!p) throw new Error(target ? `не вижу игрока ${target}` : 'рядом нет игроков');
      const t = p.entity.position;
      await withTimeout(bot.pathfinder.goto(new goals.GoalNear(t.x, t.y, t.z, 2)), 80000, 'подойти');
      await bot.lookAt(p.entity.position.offset(0, 1.6, 0));
      return `Подошла к ${p.username}.`;
    }
    case 'follow': {
      const p = findPlayer(target);
      if (!p) throw new Error(target ? `не вижу игрока ${target}` : 'рядом нет игроков');
      bot.pathfinder.setGoal(new goals.GoalFollow(p.entity, 2), true);
      return `Иду за ${p.username} (стоп — action stop).`;
    }
    case 'collect': case 'mine': case 'dig':
      return await collect(target || 'oak_log', Math.max(1, Math.min(64, parseInt(a.count, 10) || 1)));
    case 'fight': case 'attack':
      return await fight(target);
    case 'look': {
      const p = findPlayer(target);
      if (!p) throw new Error('не на кого смотреть');
      await bot.lookAt(p.entity.position.offset(0, 1.6, 0));
      return `Смотрю на ${p.username}.`;
    }
    case 'jump':
      bot.setControlState('jump', true); await sleep(400); bot.setControlState('jump', false);
      return 'Прыгнула.';
    case 'eat': {
      const food = bot.inventory.items().find(i => bot.registry.foodsByName?.[i.name] || bot.registry.foods?.[i.type]);
      if (!food) throw new Error('еды в инвентаре нет');
      await bot.equip(food, 'hand');
      await bot.consume();
      return `Съела ${food.name}. Голод: ${bot.food}/20.`;
    }
    case 'equip': {
      const it = bot.inventory.items().find(i => i.name.includes(target.toLowerCase()));
      if (!it) throw new Error(`нет в инвентаре: ${target}`);
      await bot.equip(it, 'hand');
      return `Взяла в руку ${it.name}.`;
    }
    case 'give': case 'toss': {
      const it = bot.inventory.items().find(i => i.name.includes(target.toLowerCase()));
      if (!it) throw new Error(`нет в инвентаре: ${target}`);
      const p = findPlayer(a.player || '');
      if (p) await bot.lookAt(p.entity.position.offset(0, 1.2, 0));
      const n = Math.min(it.count, parseInt(a.count, 10) || it.count);
      await bot.toss(it.type, null, n);
      return `Кинула ${n} × ${it.name}${p ? ' в сторону ' + p.username : ''}.`;
    }
    default:
      throw new Error(`не знаю действия ${act} (есть: goto, come, follow, stop, collect, fight, look, jump, eat, equip, give)`);
  }
}

// ---------- HTTP ----------
function send(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(body) });
  res.end(body);
}

http.createServer((req, res) => {
  let body = '';
  req.on('data', c => { body += c; });
  req.on('end', async () => {
    let json = {};
    try { json = body ? JSON.parse(body) : {}; } catch (e) { }
    const url = new URL(req.url, 'http://x');
    try {
      if (req.method === 'GET' && url.pathname === '/status') return send(res, 200, status());
      if (req.method === 'GET' && url.pathname === '/lan') return send(res, 200, freshLan());
      if (req.method === 'GET' && url.pathname === '/events') {
        const since = parseInt(url.searchParams.get('since') || '0', 10);
        return send(res, 200, { last: eventId, events: events.filter(e => e.id > since) });
      }
      if (req.method === 'POST' && url.pathname === '/connect') return send(res, 200, await connect(json));
      if (req.method === 'POST' && url.pathname === '/disconnect') {
        if (bot) { bot.quit(); bot = null; }
        return send(res, 200, { ok: true, message: 'Вышла из игры.' });
      }
      if (!bot || !bot.entity) return send(res, 400, { ok: false, error: 'Клава не в игре — сначала подключись (mc_connect).' });
      if (req.method === 'POST' && url.pathname === '/chat') {
        const msg = String(json.message || '').slice(0, 250);
        if (!msg) return send(res, 400, { ok: false, error: 'пустое сообщение' });
        bot.chat(msg);
        return send(res, 200, { ok: true, message: `Написала в чат: ${msg}` });
      }
      if (req.method === 'POST' && url.pathname === '/action') return send(res, 200, { ok: true, message: await doAction(json), status: status() });
      return send(res, 404, { ok: false, error: 'нет такого адреса' });
    } catch (err) {
      return send(res, 200, { ok: false, error: err.message });
    }
  });
}).listen(PORT, '127.0.0.1', () => console.log(`[МОСТ] Клава в Minecraft слушает http://127.0.0.1:${PORT}`));

listenLan();
process.on('uncaughtException', err => { console.log('[МОСТ] ' + err.stack); lastError = err.message; });
process.on('unhandledRejection', err => { console.log('[МОСТ] ' + (err && err.stack || err)); });
