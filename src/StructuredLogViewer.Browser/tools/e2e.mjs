// Headless-Chromium smoke test of a staged StructuredLogViewer.Browser site, served under a subpath like GitHub Pages.
//   node tools/e2e.mjs <siteDir> <binlog> [--prefix=/webbox/binlog] [--metrics=<file.json>] [--source=<file name fragment>] [--target=<search text>]
// Opens the page, drops a small binlog onto it (a synthetic HTML5 drop, like main.js expects from a real one), waits
// for the tree, searches for a target, selects the first hit (node details), and opens an embedded source file.
// Also loads the same log through ?url=. Records download size, load time, console errors; exits 1 on any failure.
// Needs: npm ci && npx playwright install chromium.
import fs from 'node:fs';
import path from 'node:path';
import { chromium } from 'playwright';
import { serve } from './serve.mjs';

const args = process.argv.slice(2);
const pos = args.filter(a => !a.startsWith('--'));
const opt = Object.fromEntries(args.filter(a => a.startsWith('--')).map(a => { const [k, ...v] = a.slice(2).split('='); return [k, v.join('=')]; }));
const site = path.resolve(pos[0] ?? '');
const binlog = path.resolve(pos[1] ?? '');
const prefix = opt.prefix || '/webbox/binlog';
const targetText = opt.target || 'CoreCompile';
const sourceFragment = opt.source || '.csproj';
if (!fs.existsSync(path.join(site, 'index.html'))) throw new Error('not a site folder: ' + site);
if (!fs.existsSync(binlog)) throw new Error('binlog not found: ' + binlog);

const failures = [];
const check = (ok, what) => { console.log((ok ? 'ok   ' : 'FAIL ') + what); if (!ok) failures.push(what); };
const server = await serve(site, 0, prefix);
// the binlog is served next to the site so ?url= can fetch it (same origin, no CORS needed)
const origHandler = server.server.listeners('request')[0];
server.server.removeAllListeners('request');
server.server.on('request', (req, res) => {
    if (req.url.startsWith('/fixture.binlog')) { res.writeHead(200, { 'Content-Type': 'application/octet-stream' }); return res.end(fs.readFileSync(binlog)); }
    return origHandler(req, res);
});
const origin = 'http://127.0.0.1:' + server.port;
const url = origin + server.prefix + '/';
const browser = await chromium.launch();
const metrics = { site, prefix, binlogBytes: fs.statSync(binlog).size };

async function visit(context, address) {
    const page = await context.newPage();
    await page.setViewportSize({ width: 1400, height: 900 });
    const errors = [], failed = [];
    page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
    page.on('pageerror', e => errors.push('pageerror: ' + e.message));
    page.on('requestfailed', r => failed.push(r.url() + ' ' + (r.failure()?.errorText ?? '')));
    const state = () => page.evaluate(() => JSON.parse(globalThis.binlogBrowser?.GetState() ?? '{}')).catch(() => ({}));
    const until = async (pred, what, ms = 180000) => {
        const t = Date.now();
        while (Date.now() - t < ms) { const s = await state(); if (pred(s)) return s; await page.waitForTimeout(100); }
        throw new Error('timeout waiting for ' + what + ' (last state: ' + JSON.stringify(await state()).slice(0, 300) + ')');
    };
    const t0 = Date.now();
    await page.goto(address);
    return { page, errors, failed, state, until, t0 };
}

try {
    const context = await browser.newContext();

    // ---- drag and drop ----
    const v = await visit(context, url);
    await v.until(s => s.status !== undefined, 'the app to start', 120000);
    metrics.startMs = Date.now() - v.t0;
    const b64 = fs.readFileSync(binlog).toString('base64');
    const t1 = Date.now();
    await v.page.evaluate(async b64 => {
        const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
        const dt = new DataTransfer();
        dt.items.add(new File([bytes], 'fixture.binlog', { type: 'application/octet-stream' }));
        document.dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));
    }, b64);
    let s = await v.until(s => s.loaded, 'the binlog to load');
    metrics.dropToTreeMs = Date.now() - t1;
    check(s.loaded && /parsed in/.test(s.status), 'drop opens the binlog: ' + s.status);
    check(s.files > 0, 'embedded source files found: ' + s.files);

    const shot = async name => v.page.screenshot({ path: path.join(path.dirname(path.resolve(opt.metrics || 'metrics.json')), name) });
    await v.page.waitForTimeout(1500);
    await shot('e2e-1-tree.png');

    // ---- search (typed into the shared search box) ----
    const results = await v.page.evaluate(q => globalThis.binlogBrowser.Search(q), targetText);
    check(results > 0, `search for '${targetText}' returns results (${results})`);
    s = await v.state();
    check(s.searchText === targetText && s.selected, 'the first hit is selected: ' + (s.selected ?? '').slice(0, 60));
    await v.page.waitForTimeout(500);
    await shot('e2e-2-search-and-details.png');

    // ---- source file ----
    const opened = await v.page.evaluate(f => globalThis.binlogBrowser.OpenFirstSourceFile(f), sourceFragment);
    check(!!opened, 'a source file from the archive opens in the text viewer: ' + opened);
    await v.page.waitForTimeout(1500);
    await shot('e2e-3-source.png');
    check(v.errors.length === 0, 'no console errors (drop)' + (v.errors.length ? ': ' + v.errors.slice(0, 3).join(' | ') : ''));
    check(v.failed.length === 0, 'no failed requests (drop)' + (v.failed.length ? ': ' + v.failed.slice(0, 3).join(' | ') : ''));
    await v.page.close();

    // ---- ?url= (warm: same context) ----
    const w = await visit(context, url + '?url=' + encodeURIComponent(origin + '/fixture.binlog'));
    const s2 = await w.until(s => s.loaded, 'the ?url= binlog to load');
    metrics.urlWarmToTreeMs = Date.now() - w.t0;
    check(s2.loaded, '?url= opens the binlog: ' + s2.status);
    check(w.errors.length === 0, 'no console errors (?url=)' + (w.errors.length ? ': ' + w.errors.slice(0, 3).join(' | ') : ''));
    await w.page.close();

    const bad = server.log.filter(r => r.status >= 400);
    check(bad.length === 0, 'no 4xx responses' + (bad.length ? ': ' + bad.slice(0, 5).map(b => b.status + ' ' + b.path).join(', ') : ''));
    check(server.log.filter(r => !r.path.startsWith('/fixture.binlog')).every(r => r.path.startsWith(prefix + '/')), 'every site request stays under ' + prefix + '/');
} catch (e) {
    check(false, String(e.stack ?? e));
} finally {
    await browser.close();
    server.server.close();
}
metrics.passed = failures.length === 0;
console.log(JSON.stringify(metrics, null, 2));
if (opt.metrics) fs.writeFileSync(opt.metrics, JSON.stringify(metrics, null, 2) + '\n');
process.exit(failures.length ? 1 : 0);
