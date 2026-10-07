// Headless-Chromium smoke test of a staged StructuredLogViewer.Browser site, served under a subpath like GitHub Pages.
//   node tools/e2e.mjs <siteDir> <binlog> [--prefix=/webbox/binlog] [--metrics=<file.json>] [--source=<file name fragment>] [--target=<search text>]
// Opens the page, drops a small binlog onto it (a synthetic HTML5 drop, like main.js expects from a real one), waits
// for the tree, searches for a target, selects the first hit (node details), and opens an embedded source file.
// Also loads the same log through ?url=. Records download size, load time, console errors; exits 1 on any failure.
// Needs: npm ci && npx playwright install chromium.
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
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
// a second origin for cross-origin ?url= tests: /cors.binlog (CORS + Range), /nocors.binlog (no CORS headers), /page.html, /norange.binlog (CORS, ignores Range)
const data = fs.readFileSync(binlog);
const otherLog = [];
const other = http.createServer((req, res) => {
    const p = req.url.split('?')[0];
    otherLog.push({ p, range: req.headers.range ?? '' });
    const cors = { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Headers': 'Range', 'Access-Control-Expose-Headers': 'Content-Range, Content-Length', 'Access-Control-Allow-Methods': 'GET' };
    if (req.method === 'OPTIONS') { res.writeHead(204, cors); return res.end(); }
    if (p === '/page.html') { res.writeHead(200, { 'Content-Type': 'text/html', ...cors }); return res.end('<!doctype html><html><body>sign in</body></html>'); }
    if (p === '/nocors.binlog') { res.writeHead(200, { 'Content-Type': 'application/octet-stream' }); return res.end(data); }
    if (p === '/norange.binlog') { res.writeHead(200, { 'Content-Type': 'application/octet-stream', ...cors }); return res.end(data); }
    if (p === '/cors.binlog') {
        const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range ?? '');
        if (!m) { res.writeHead(200, { 'Content-Type': 'application/octet-stream', ...cors }); return res.end(data); }
        const a = Number(m[1]), b = Math.min(m[2] ? Number(m[2]) : data.length - 1, data.length - 1);
        res.writeHead(206, { 'Content-Type': 'application/octet-stream', 'Content-Range': `bytes ${a}-${b}/${data.length}`, ...cors });
        return res.end(data.subarray(a, b + 1));
    }
    res.writeHead(404, cors); res.end('nope');
});
await new Promise(r => other.listen(0, '127.0.0.1', r));
const otherOrigin = 'http://127.0.0.1:' + other.address().port;
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

    // ---- start page and Open from URL (failures first, on the start page) ----
    const u = await visit(context, url);
    await u.until(s => s.status !== undefined, 'the app to start', 120000);
    await u.page.waitForTimeout(1500);
    const controls = (await u.page.evaluate(() => globalThis.binlogBrowser.WelcomeControls())).split(',');
    check(controls.includes('openFromUrl') && controls.includes('urlText') && !controls.includes('openProject'),
        'start page: Open from URL shown, Open Project/Solution hidden (' + controls.join(',') + ')');
    await u.page.screenshot({ path: path.join(path.dirname(path.resolve(opt.metrics || 'metrics.json')), 'e2e-0-start.png') });
    const tryUrl = async address => u.page.evaluate(a => globalThis.binlogBrowser.OpenUrl(a), address);
    let msg = await tryUrl(otherOrigin + '/nocors.binlog');
    check(/cross-origin|CORS/i.test(msg), 'no CORS headers gives a CORS message: ' + msg.slice(0, 60));
    await u.page.waitForTimeout(500);
    await u.page.screenshot({ path: path.join(path.dirname(path.resolve(opt.metrics || 'metrics.json')), 'e2e-0b-start-error.png') });
    msg = await tryUrl(otherOrigin + '/page.html');
    check(/web page/i.test(msg), 'an HTML response gives a not-a-binlog message: ' + msg.slice(0, 60));
    msg = await tryUrl(otherOrigin + '/missing.binlog');
    check(/HTTP 404/.test(msg), 'a 404 is reported: ' + msg.slice(0, 60));
    msg = await tryUrl('ftp://x/y.binlog');
    check(/http and https/.test(msg), 'non-http URL refused');
    msg = await tryUrl(otherOrigin + '/norange.binlog');
    check(msg === '' && (await u.state()).loaded, 'server without Range support: opens with one download');
    await u.page.close();
    const v2 = await visit(context, url);
    await v2.until(s => !!s.status, 'the app to start', 120000);
    otherLog.length = 0;
    msg = await v2.page.evaluate(a => globalThis.binlogBrowser.OpenUrl(a), otherOrigin + '/cors.binlog');
    check(msg === '' && (await v2.state()).loaded, 'cross-origin URL with CORS and Range opens: ' + msg);
    check(otherLog.some(r => /^bytes=0-/.test(r.range)) && otherLog.filter(r => r.p === '/cors.binlog' && r.range).length >= 1, 'it was fetched with Range requests (' + otherLog.filter(r => r.range).length + ')');
    await v2.page.close();

    // ---- drag and drop ----
    const v = await visit(context, url);
    await v.until(s => !!s.status, 'the app to start', 120000);
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

    // ---- shortcuts ----
    await v.page.mouse.click(700, 450);
    await v.page.keyboard.press('Control+Shift+F');
    await v.page.waitForTimeout(500);
    s = await v.state();
    check(s.leftTab === 'findInFilesTab', 'Ctrl+Shift+F opens Find in Files: ' + s.leftTab + ' (tab available: ' + s.findInFiles + ')');
    await v.page.keyboard.press('Control+F');
    await v.page.waitForTimeout(300);
    await shot('e2e-1b-shortcut.png');

    // ---- search (typed into the shared search box) ----
    const results = await v.page.evaluate(q => globalThis.binlogBrowser.Search(q), targetText);
    check(results > 0, `search for '${targetText}' returns results (${results})`);
    s = await v.state();
    const picked = await v.page.evaluate(() => globalThis.binlogBrowser.SelectFirstTask('Csc'));
    s = await v.state();
    check(!!picked && s.selected === picked, 'selecting a task shows it in the main tree: ' + picked);
    await v.page.waitForTimeout(500);
    await shot('e2e-2-search-and-details.png');

    // ---- source file ----
    const opened = await v.page.evaluate(f => globalThis.binlogBrowser.OpenFirstSourceFile(f), sourceFragment);
    check(!!opened, 'a source file from the archive opens in the text viewer: ' + opened);
    await v.page.waitForTimeout(1500);
    await shot('e2e-3-source.png');

    // ---- tracing and graph views (shared controls) ----
    const tg = await v.page.evaluate(() => globalThis.binlogBrowser.GoToTracingAndGraphs('Csc'));
    console.log('graph hook: ' + tg);
    const [tracingBlocks, refVerts, targetVerts, propVerts] = tg.split('|').map(Number);
    check(tracingBlocks > 0, 'tracing renders blocks: ' + tg);
    check(refVerts > 0, 'project reference graph renders');
    check(targetVerts > 0, 'target graph renders');
    check(propVerts > 0, 'property graph renders');
    await shot('e2e-6-graph.png');

    // ---- timeline (shared TimelineControl) ----
    const blocks = Number(await v.page.evaluate(() => globalThis.binlogBrowser.GoToTimeline('Csc')));
    await v.page.waitForTimeout(800);
    await shot('e2e-4-timeline.png');
    check(blocks > 0, 'Timeline tab renders blocks: ' + blocks);

    // ---- shared-UI commands that need browser support ----
    const buttons = await v.page.evaluate(() => globalThis.binlogBrowser.VisibleViewerButtons());
    check(buttons.includes('save') && !buttons.includes('openInExternalEditor'), 'source toolbar: Save shown, Open in external editor hidden');
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin });
    await v.page.evaluate(() => globalThis.binlogBrowser.ClickViewerButton('copyFullPath'));
    await v.page.waitForTimeout(500);
    const clip = await v.page.evaluate(() => navigator.clipboard.readText()).catch(e => 'ERR ' + e.message);
    check(clip === opened, 'Copy Path puts the file path on the clipboard: ' + String(clip).slice(0, 80));
    const download = v.page.waitForEvent('download', { timeout: 10000 }).catch(() => null);
    await v.page.evaluate(() => globalThis.binlogBrowser.ClickViewerButton('save'));
    const dl = await download;
    const dlText = dl ? fs.readFileSync(await dl.path(), 'utf8') : '';
    check(!!dl && dlText.length > 0 && dl.suggestedFilename() === path.basename(opened), 'Save downloads the file: ' + (dl?.suggestedFilename() ?? 'no download') + ' (' + dlText.length + ' chars)');

    // ---- settings persist in localStorage ----
    check(await v.page.evaluate(() => globalThis.binlogBrowser.SetDarkTheme(true)), 'dark theme switched on');
    const stored = await v.page.evaluate(() => localStorage.getItem('binlog:Settings.txt'));
    check(!!stored && /UseDarkTheme/i.test(stored) && /true/i.test(stored), 'settings written to localStorage');
    await v.page.reload();
    await v.until(s => s.status !== undefined, 'the app to restart', 120000);
    check(await v.page.evaluate(() => globalThis.binlogBrowser.GetDarkTheme()), 'dark theme restored after reload');
    await v.page.evaluate(() => globalThis.binlogBrowser.SetDarkTheme(false));
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
    other.close();
}
metrics.passed = failures.length === 0;
console.log(JSON.stringify(metrics, null, 2));
if (opt.metrics) fs.writeFileSync(opt.metrics, JSON.stringify(metrics, null, 2) + '\n');
process.exit(failures.length ? 1 : 0);
