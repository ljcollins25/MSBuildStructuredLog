// Stages the published StructuredLogViewer.Browser site (the publish output's wwwroot folder) for a static host.
//   node tools/stage.mjs <publish/wwwroot> <outDir> [--target=pages|cloudflare] [--base=/binlog] [--headers=<file>]
//
// target=pages (GitHub Pages: no Content-Encoding for precompressed files): only the big binaries
//   (.wasm/.dll/.dat) keep their .br sibling; the page fetches and decodes it itself (wwwroot/br.js).
//   Every .gz and the .br of text files are dropped (Pages gzips text on its own).
// target=cloudflare (Worker static assets): no .br/.gz and no decoder, the edge compresses. A file over
//   the 25 MiB per-asset limit ships only as <file>.br (decoded in the page); any other oversized file
//   fails the staging. --headers=<file> appends this app's rules (caching, wasm Content-Type) to the
//   site's Workers _headers file.
// Both: app files get content-hashed names (the SDK fingerprints the framework files already), because
//   Pages sends max-age=600 for everything; staging.json (build id, delivery) is the only unhashed file
//   besides index.html. web.config (IIS only) is removed.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const pos = args.filter(a => !a.startsWith('--'));
const opt = Object.fromEntries(args.filter(a => a.startsWith('--')).map(a => { const [k, ...v] = a.slice(2).split('='); return [k, v.join('=')]; }));
if (pos.length < 2) throw new Error('usage: stage.mjs <publish/wwwroot> <outDir> [--target=pages|cloudflare]');
const [src, out] = [path.resolve(pos[0]), path.resolve(pos[1])];
const target = opt.target || 'pages';
if (!['pages', 'cloudflare'].includes(target)) throw new Error('--target must be pages or cloudflare');
const base = (opt.base ?? '/' + path.basename(out)).replace(/\/+$/, '');
const CF_LIMIT = 25 * 1024 * 1024;

fs.rmSync(out, { recursive: true, force: true });
fs.cpSync(src, out, { recursive: true });
fs.rmSync(path.join(out, 'web.config'), { force: true });

const walk = d => fs.readdirSync(d, { withFileTypes: true }).flatMap(e => (e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)]));
const rel = p => path.relative(out, p).split(path.sep).join('/');
const hash = buf => crypto.createHash('sha1').update(buf).digest('hex').slice(0, 10);
const keepBr = /\.(wasm|dll|dat)$/;

// ---- 1. compression ----
const brotliOnly = [];
if (target === 'cloudflare') {
    for (const f of walk(out)) {
        if (/\.(gz|br)$/.test(f) || fs.statSync(f).size <= CF_LIMIT) continue;
        const packed = fs.existsSync(f + '.br') && fs.statSync(f + '.br').size;
        if (!packed || packed > CF_LIMIT || !keepBr.test(f)) {
            throw new Error('Cloudflare asset too large: ' + rel(f) + ' is ' + fs.statSync(f).size + ' bytes (limit ' + CF_LIMIT + ')' + (packed ? ', .br ' + packed : ', no usable .br'));
        }
        fs.rmSync(f);
        brotliOnly.push(rel(f));
    }
}
let kept = 0, dropped = 0;
for (const f of walk(out)) {
    if (f.endsWith('.gz')) { fs.rmSync(f); dropped++; }
    else if (f.endsWith('.br')) {
        const plain = f.slice(0, -3);
        const keep = target === 'pages' ? keepBr.test(plain) && fs.existsSync(plain) : brotliOnly.includes(rel(plain));
        if (keep) kept++; else { fs.rmSync(f); dropped++; }
    }
}
const brotli = target === 'pages' || brotliOnly.length > 0;
if (brotli) {
    const vendor = path.join(path.dirname(fileURLToPath(import.meta.url)), 'node_modules/brotli-dec-wasm');
    if (!fs.existsSync(vendor)) throw new Error('run "npm ci" in StructuredLogViewer.Browser/tools first (brotli-dec-wasm is copied into the site)');
    fs.mkdirSync(path.join(out, 'vendor'), { recursive: true });
    for (const f of ['pkg/brotli_dec_wasm.js', 'pkg/brotli_dec_wasm_bg.wasm', 'LICENSE-MIT.txt', 'LICENSE-Apache.txt']) {
        fs.copyFileSync(path.join(vendor, f), path.join(out, 'vendor', path.basename(f)));
    }
} else {
    fs.rmSync(path.join(out, 'br.js'), { force: true });
}

// ---- 2. fingerprint the app files ----
// Order matters: the leaves first, since a file's hash covers the (already rewritten) names it mentions.
const dotnetHash = hash(fs.readFileSync(path.join(out, '_framework/dotnet.js')));
const rename = (file, edit) => {
    const cur = path.join(out, file);
    let buf = fs.readFileSync(cur);
    if (edit) buf = Buffer.from(edit(buf.toString('utf8')));
    const ext = path.extname(file), hashed = file.slice(0, -ext.length) + '.' + hash(buf) + ext;
    fs.writeFileSync(path.join(out, hashed), buf);
    fs.rmSync(cur);
    return hashed;
};
const need = (text, from, to) => { if (!text.includes(from)) throw new Error('stage.mjs: expected ' + from); return text.replace(from, to); };
const names = {};
if (brotli) names['br.js'] = rename('br.js');
names['app.css'] = rename('app.css');
names['main.js'] = rename('main.js', t => {
    t = need(t, "'./_framework/dotnet.js'", "'./_framework/dotnet.js?h=" + dotnetHash + "'");
    if (brotli) t = need(t, "'./br.js'", "'./" + names['br.js'] + "'");
    return t;
});
let index = fs.readFileSync(path.join(out, 'index.html'), 'utf8');
index = need(index, 'href="app.css"', 'href="' + names['app.css'] + '"');
index = need(index, 'src="./main.js"', 'src="./' + names['main.js'] + '"');
fs.writeFileSync(path.join(out, 'index.html'), index);
fs.writeFileSync(path.join(out, 'staging.json'), JSON.stringify({ build: dotnetHash, brotli, only: target === 'pages' || !brotliOnly.length ? null : brotliOnly }));

// ---- 3. limits and headers ----
if (target === 'cloudflare') {
    const big = walk(out).filter(f => fs.statSync(f).size > CF_LIMIT);
    if (big.length) throw new Error('Cloudflare asset too large: ' + big.map(f => rel(f)).join(', '));
    if (opt.headers) {
        const rule = (pat, ...h) => pat + '\n' + h.map(x => '  ' + x).join('\n') + '\n';
        const immutable = 'Cache-Control: public, max-age=31536000, immutable';
        const parts = [
            '# generated by StructuredLogViewer.Browser/tools/stage.mjs --target=cloudflare',
            rule(base + '/', 'Cache-Control: no-cache'),
            rule(base + '/index.html', 'Cache-Control: no-cache'),
            rule(base + '/staging.json', 'Cache-Control: no-cache'),
            ...Object.values(names).map(n => rule(base + '/' + n, immutable)),
            rule(base + '/_framework/*', immutable),
            // the edge compresses application/wasm, not application/octet-stream
            rule(base + '/_framework/*.wasm', 'Content-Type: application/wasm'),
            rule(base + '/samples/*.dll', 'Content-Type: application/wasm'),
        ];
        fs.appendFileSync(path.resolve(opt.headers), '\n' + parts.join('\n'));
    }
}

const total = walk(out).reduce((s, f) => s + fs.statSync(f).size, 0);
console.log('staged (' + target + ') ' + out + ': ' + walk(out).length + ' files, ' + (total / 1048576).toFixed(1) + ' MiB on disk; kept ' + kept + ' .br, dropped ' + dropped + ' compressed copies' + (brotliOnly.length ? '; brotli-only: ' + brotliOnly.join(', ') : ''));
