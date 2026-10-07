// Headless-Chromium smoke test of a staged ILSpy.Browser site, served under a subpath like GitHub Pages.
//   node tools/e2e.mjs <siteDir> [--prefix=/webbox/ilspy] [--metrics=<file.json>] [--fixture=<assembly.dll>]
// Opens the page (it starts with a sample assembly), waits for the first decompiled text, drops a small
// assembly onto the page (a synthetic HTML5 drop, like main.js expects from a real one), selects its type
// with the keyboard and checks the decompiled C#. Records the download size, time to first decompile,
// a warm reload, and console errors; exits 1 if anything fails. Needs: npm ci && npx playwright install chromium.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { serve } from './serve.mjs';

const args = process.argv.slice(2);
const site = path.resolve(args.find(a => !a.startsWith('--')) ?? '');
const opt = Object.fromEntries(args.filter(a => a.startsWith('--')).map(a => { const [k, ...v] = a.slice(2).split('='); return [k, v.join('=')]; }));
const here = path.dirname(fileURLToPath(import.meta.url));
const prefix = opt.prefix || '/webbox/ilspy';
if (!fs.existsSync(path.join(site, 'index.html'))) throw new Error('not a site folder: ' + site);

let fixture = opt.fixture && path.resolve(opt.fixture);
if (!fixture) {
    execFileSync('dotnet', ['build', path.join(here, 'fixture'), '-c', 'Release', '-v:q', '-nologo'], { stdio: 'inherit' });
    fixture = path.join(here, 'fixture/bin/Release/netstandard2.0/Fixture.dll');
}

const failures = [];
const check = (ok, what) => { console.log((ok ? 'ok   ' : 'FAIL ') + what); if (!ok) failures.push(what); };
const server = await serve(site, 0, prefix);
const url = 'http://127.0.0.1:' + server.port + server.prefix + '/';
const browser = await chromium.launch();

async function visit(context, label) {
    const page = await context.newPage();
    await page.setViewportSize({ width: 1400, height: 900 });
    const errors = [], failed = [];
    page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
    page.on('pageerror', e => errors.push('pageerror: ' + e.message));
    page.on('requestfailed', r => failed.push(r.url() + ' ' + (r.failure()?.errorText ?? '')));
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    let bytes = 0, requests = 0;
    cdp.on('Network.loadingFinished', e => { bytes += e.encodedDataLength; requests++; });
    const text = () => page.evaluate(() => globalThis.ilspyBrowser?.GetDecompiledText() ?? '');
    const until = async (pred, what, ms = 120000) => {
        const t = Date.now();
        while (Date.now() - t < ms) { const v = await text().catch(() => ''); if (pred(v)) return v; await page.waitForTimeout(100); }
        throw new Error('timeout waiting for ' + what);
    };
    const t0 = Date.now();
    await page.goto(url);
    const first = await until(t => t.includes('ICSharpCode.Decompiler'), 'the first decompile (sample assembly)');
    const firstMs = Date.now() - t0, firstBytes = bytes;
    return { page, errors, failed, text, until, firstMs, firstBytes, first, totalBytes: () => bytes, requests: () => requests };
}

const metrics = { site, prefix };
try {
    // ---- cold ----
    const context = await browser.newContext();
    const v = await visit(context, 'cold');
    check(/\/\/ ICSharpCode\.Decompiler, Version=/.test(v.first), 'sample assembly decompiled on startup');
    metrics.coldFirstDecompileMs = v.firstMs;
    metrics.coldDownloadBytesToFirstDecompile = v.firstBytes;

    // ---- drop an assembly ----
    const b64 = fs.readFileSync(fixture).toString('base64');
    await v.page.evaluate(async b64 => {
        const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
        const dt = new DataTransfer();
        dt.items.add(new File([bytes], 'Fixture.dll', { type: 'application/octet-stream' }));
        document.dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));
    }, b64);
    // ---- select its type: the tree is a canvas, so drive it with the keyboard ----
    await v.page.waitForTimeout(3000);
    await v.page.mouse.click(100, 60);                    // first assembly row: focuses the tree
    await v.page.keyboard.press('End');                   // last row = Fixture
    await v.page.keyboard.press('ArrowRight');            // expand
    const t1 = Date.now();
    // Fixture's children: Metadata, References, the global namespace "-", then Demo; Demo's child is Greeter.
    for (let i = 0; i < 4; i++) await v.page.keyboard.press('ArrowDown');
    await v.page.keyboard.press('ArrowRight');            // expand Demo
    await v.page.keyboard.press('ArrowDown');             // select Greeter
    const code = await v.until(t => /class Greeter/.test(t), 'the decompiled type', 30000).catch(() => v.text());
    check(/class Greeter/.test(code) && /Hello\(string name\)/.test(code), 'selecting the type shows decompiled C# (class Greeter / Hello)');
    metrics.dropToDecompiledTypeMs = Date.now() - t1;
    metrics.decompiledSnippet = code.split('\n').filter(l => l.trim()).slice(0, 12).join('\n');
    await v.page.screenshot({ path: path.join(path.dirname(path.resolve(opt.metrics || 'metrics.json')), 'e2e-screenshot.png') });
    metrics.coldTotalDownloadBytes = v.totalBytes();
    metrics.coldRequests = v.requests();
    check(v.errors.length === 0, 'no console errors (cold)' + (v.errors.length ? ': ' + v.errors.slice(0, 3).join(' | ') : ''));
    check(v.failed.length === 0, 'no failed requests (cold)' + (v.failed.length ? ': ' + v.failed.slice(0, 3).join(' | ') : ''));
    await v.page.close();

    // ---- warm: same context (HTTP cache + Cache API of decoded binaries) ----
    const w = await visit(context, 'warm');
    metrics.warmFirstDecompileMs = w.firstMs;
    metrics.warmDownloadBytesToFirstDecompile = w.firstBytes;
    check(w.errors.length === 0, 'no console errors (warm)' + (w.errors.length ? ': ' + w.errors.slice(0, 3).join(' | ') : ''));
    await w.page.close();

    const bad = server.log.filter(r => r.status >= 400);
    metrics.httpErrors = bad.length;
    check(bad.length === 0, 'no 4xx responses' + (bad.length ? ': ' + bad.slice(0, 5).map(b => b.status + ' ' + b.path).join(', ') : ''));
    check(server.log.every(r => r.path.startsWith(prefix + '/')), 'every request stays under ' + prefix + '/');
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
