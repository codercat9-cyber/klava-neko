// Мини-сервер статики для отладки страницы Клавы в обычном браузере (в самом KlavaDesk не нужен:
// там WebView2 отдаёт папку web через виртуальный хост).
//   node devserver.mjs [порт]
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'web');
// каверы и разобранные треки (в KlavaDesk — хост klava-music.local) — здесь /music-data/…
const musicRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'data', 'music');
const port = Number(process.argv[2] || 5173);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8', '.vrm': 'model/gltf-binary', '.wav': 'audio/wav', '.png': 'image/png', '.flac': 'audio/flac', '.mp3': 'audio/mpeg' };

http.createServer((req, res) => {
  const url = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  const music = url.startsWith('/music-data/');
  const base = music ? musicRoot : root;
  const file = path.normalize(path.join(base, music ? url.slice('/music-data/'.length) : url === '/' ? 'index.html' : url));
  if (!file.startsWith(base)) { res.writeHead(403).end(); return; }
  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) { res.writeHead(404).end('нет файла'); return; }
    res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream', 'content-length': st.size, 'cache-control': 'no-store' });
    fs.createReadStream(file).pipe(res);
  });
}).listen(port, '127.0.0.1', () => console.log(`Клава (отладка): http://127.0.0.1:${port}/`));
