import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';

const TARGET_DIR = 'C:/Users/KOT/Pictures/Для Насти';
if (!fs.existsSync(TARGET_DIR)) {
  fs.mkdirSync(TARGET_DIR, { recursive: true });
}

let chromeProc = null;
const server = http.createServer((req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }

  if (req.method === 'POST' && req.url === '/save_shot') {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      try {
        const json = JSON.parse(body);
        const base64Data = json.image.replace(/^data:image\/\w+;base64,/, '');
        const buf = Buffer.from(base64Data, 'base64');
        const outPath = path.join(TARGET_DIR, json.filename);
        fs.writeFileSync(outPath, buf);
        console.log(`SAVED: ${outPath} (${buf.length} bytes)`);

        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ ok: true, path: outPath }));

        if (json.done) {
          console.log('ALL_DONE');
          res.end();
          setTimeout(() => {
            if (chromeProc) chromeProc.kill();
            server.close(() => process.exit(0));
          }, 1000);
        }
      } catch (err) {
        console.error('Error saving image:', err);
        res.writeHead(500);
        res.end(String(err));
      }
    });
    return;
  }

  res.writeHead(404);
  res.end();
});

server.listen(9876, '127.0.0.1', () => {
  console.log('Capture receiver listening on http://127.0.0.1:9876');

  // Запуск Chrome headless
  const chromePath = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
  const url = 'http://127.0.0.1:5173/render_nude.html';
  console.log(`Launching Chrome to ${url}...`);

  chromeProc = spawn(chromePath, [
    '--headless=new',
    '--enable-webgl',
    '--ignore-gpu-blocklist',
    '--use-gl=angle',
    '--disable-gpu-sandbox',
    '--window-size=1600,2400',
    url
  ], {
    stdio: 'inherit'
  });

  chromeProc.on('exit', (code) => {
    console.log(`Chrome exited with code ${code}`);
  });
});
