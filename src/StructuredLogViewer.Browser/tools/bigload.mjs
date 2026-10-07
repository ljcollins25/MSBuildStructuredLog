// Loads a big .binlog through the paged source: node bigload.mjs <site> <binlog> [--drop]
// Serves the site and the log with Range support and reports load time, peak memory and the viewer state.
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path';
import { chromium } from 'playwright';
const [site, log, mode] = process.argv.slice(2);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json', '.css': 'text/css' };
const srv = http.createServer((req, res) => {
  const u = decodeURIComponent(req.url.split('?')[0]);
  const f = u === '/log.binlog' ? log : path.join(site, u === '/' ? 'index.html' : u);
  if (!fs.existsSync(f) || fs.statSync(f).isDirectory()) { res.writeHead(404); return res.end(); }
  const size = fs.statSync(f).size; const type = types[path.extname(f)] || 'application/octet-stream';
  const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  res.setHeader('Access-Control-Allow-Origin', '*'); res.setHeader('Accept-Ranges', 'bytes');
  if (m) { const s = +m[1], e = Math.min(m[2] ? +m[2] : size - 1, size - 1);
    res.writeHead(206, { 'Content-Type': type, 'Content-Range': `bytes ${s}-${e}/${size}`, 'Content-Length': e - s + 1 }); fs.createReadStream(f, { start: s, end: e }).pipe(res); }
  else { res.writeHead(200, { 'Content-Type': type, 'Content-Length': size }); fs.createReadStream(f).pipe(res); }
}).listen(0);
const port = srv.address().port;
const browser = await chromium.launch({ args: ['--js-flags=--max-old-space-size=4096'] });
const page = await browser.newPage();
page.on('console', m => { const t = m.text(); if (/progress|Parsed|ERROR/.test(t)) console.log(new Date().toISOString().slice(11, 19), t.slice(0, 300)); });
page.on('pageerror', e => console.log('PAGEERROR', String(e).slice(0, 300)));
const t0 = Date.now();
await page.goto(`http://localhost:${port}/` + (mode === '--drop' ? '' : '?url=log.binlog'));
if (mode === '--drop') {
  await page.waitForFunction(() => globalThis.binlogOpenFile, null, { timeout: 120000 });
  await page.evaluate(async u => { const b = await (await fetch(u)).blob(); await globalThis.binlogOpenFile(new File([b], 'Build.binlog')); }, '/log.binlog');
}
let state = {};
for (let i = 0; i < 1500; i++) {
  await new Promise(r => setTimeout(r, 2000));
  try { state = JSON.parse(await page.evaluate(() => globalThis.binlogBrowser?.GetState() ?? '{}')); } catch { }
  if (state.loaded || /Could not/.test(state.status || '')) break;
  if (page.isClosed()) break;
}
console.log('state', JSON.stringify(state), 'seconds', (Date.now() - t0) / 1000);
const cdp = await page.context().newCDPSession(page);
console.log('metrics', JSON.stringify(await cdp.send('Performance.getMetrics').catch(() => null)).slice(0, 400));
if (state.loaded) { const n = await page.evaluate(() => globalThis.binlogBrowser.Search('CoreCompile')); console.log('search results', n); }
await browser.close(); srv.close();
