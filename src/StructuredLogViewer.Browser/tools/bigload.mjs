// Loads a big .binlog through the paged source: node bigload.mjs <site> <binlog> [--drop] [--cap-mb=2048]
// --cap-mb clamps every WebAssembly.Memory to that many MB (default 2048; 0 = no cap), so growth past it fails like on a real 2 GB heap.
// Reports parse time, time to first tree, peak wasm memory and peak renderer RSS (sampled), then exercises search, big-node expansion and an embedded file.
// Serves the site and the log with Range support and reports load time, peak memory and the viewer state.
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path';
import { chromium } from 'playwright';
const argv = process.argv.slice(2); const [site, log] = argv; const mode = argv.includes('--drop') ? '--drop' : '';
const capArg = argv.find(a => a.startsWith('--cap-mb=')); const capMb = capArg ? +capArg.slice(9) : 2048;
import { execSync } from 'node:child_process';
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
await page.addInitScript(cap => {
  const Orig = WebAssembly.Memory; globalThis.__wasmPeak = 0; globalThis.__mems = [];
  const track = m => { globalThis.__mems.push(new WeakRef(m)); return m; };
  const pages = cap ? Math.floor(cap * 16) : 0;
  WebAssembly.Memory = function (d) { if (pages && d) { d = { ...d }; if (!d.maximum || d.maximum > pages) d.maximum = pages; } return track(new Orig(d)); };
  WebAssembly.Memory.prototype = Orig.prototype;
  const grow = Orig.prototype.grow; Orig.prototype.grow = function (n) { const r = grow.call(this, n); globalThis.__wasmPeak = Math.max(globalThis.__wasmPeak, this.buffer.byteLength); return r; };
}, capMb);
let peakRss = 0; const rssTimer = setInterval(() => { try {
  const out = execSync("ps -eo rss,args | grep -i 'chrom' | grep -- '--type=renderer' | grep -v grep | awk '{s+=$1} END {print s}'").toString().trim(); peakRss = Math.max(peakRss, +out || 0); } catch { } }, 500);
page.on('console', m => { const t = m.text(); if (/progress|Parsed|ERROR/.test(t)) console.log(new Date().toISOString().slice(11, 19), t.slice(0, 300)); });
page.on('pageerror', e => console.log('PAGEERROR', String(e).slice(0, 300)));
const t0 = Date.now();
await page.goto(`http://localhost:${port}/` + (mode === '--drop' ? '' : '?url=log.binlog'));
if (mode === '--drop') {
  await page.waitForFunction(() => globalThis.binlogOpenFile, null, { timeout: 120000 });
  await page.evaluate(async u => { const b = await (await fetch(u)).blob(); await globalThis.binlogOpenFile(new File([b], 'Build.binlog')); }, '/log.binlog');
}
let state = {}; let tree = null;
for (let i = 0; i < 1500; i++) {
  await new Promise(r => setTimeout(r, 500));
  try { state = JSON.parse(await page.evaluate(() => globalThis.binlogBrowser?.GetState() ?? '{}')); } catch { }
  if (state.loaded && !tree) tree = (Date.now() - t0) / 1000;
  if (state.loaded || /Could not/.test(state.status || '')) break;
  if (page.isClosed()) break;
}
console.log('state', JSON.stringify(state), 'seconds', (Date.now() - t0) / 1000);
clearInterval(rssTimer);
const wasmPeak = await page.evaluate(() => { let m = 0; for (const w of globalThis.__mems) { const x = w.deref(); if (x) m = Math.max(m, x.buffer.byteLength); } return Math.max(m, globalThis.__wasmPeak) / 1048576; }).catch(() => -1);
console.log('RESULT', JSON.stringify({ capMb, timeToFirstTreeSec: tree, wasmMemoryMB: Math.round(wasmPeak), peakRendererRssMB: Math.round(peakRss / 1024) }));
if (state.loaded) {
  const t1 = Date.now(); console.log('biggest node', await page.evaluate(() => globalThis.binlogBrowser.SelectBiggestNode()), (Date.now() - t1) + 'ms');
  const t2 = Date.now(); console.log('open embedded file', JSON.stringify(await page.evaluate(() => globalThis.binlogBrowser.OpenFirstSourceFile('.cs'))).slice(0, 200), (Date.now() - t2) + 'ms');
}
if (state.loaded) { const n = await page.evaluate(() => globalThis.binlogBrowser.Search('CoreCompile')); console.log('search results', n); }
await browser.close(); srv.close();
