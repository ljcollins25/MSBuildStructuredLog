// Static server that behaves like GitHub Pages under a subpath: the site lives at <prefix>/, text
// files are gzipped on the fly, binaries and .br files are sent as is (no Content-Encoding), and
// everything gets max-age=600. Used by e2e.mjs; also handy by hand:
//   node tools/serve.mjs <siteDir> [port] [prefix]      (default 8080, /webbox/binlog)
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css', '.json': 'application/json', '.wasm': 'application/wasm', '.map': 'application/json', '.txt': 'text/plain' };
const gzipped = new Set(['.html', '.js', '.mjs', '.css', '.json', '.txt', '.map']);

export function serve(root, port = 0, prefix = '/webbox/binlog') {
    root = path.resolve(root);
    prefix = '/' + prefix.replace(/^\/+|\/+$/g, '');
    const log = [];
    const server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://x');
        let p = decodeURIComponent(url.pathname);
        const original = p;
        const record = status => log.push({ path: original, status });
        if (p === prefix) { res.writeHead(301, { Location: prefix + '/' }); return res.end(); }
        if (!p.startsWith(prefix + '/')) { record(404); res.writeHead(404); return res.end('outside ' + prefix); }
        p = p.slice(prefix.length);
        let file = path.join(root, p.endsWith('/') ? p + 'index.html' : p);
        if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { record(404); res.writeHead(404); return res.end('not found'); }
        const ext = path.extname(file);
        const headers = { 'Content-Type': types[ext] ?? 'application/octet-stream', 'Cache-Control': 'max-age=600', 'Access-Control-Allow-Origin': '*' };
        let body = fs.readFileSync(file);
        if (gzipped.has(ext) && /\bgzip\b/.test(req.headers['accept-encoding'] ?? '')) { body = zlib.gzipSync(body); headers['Content-Encoding'] = 'gzip'; }
        headers['Content-Length'] = body.length;
        record(200);
        res.writeHead(200, headers);
        res.end(body);
    });
    return new Promise(resolve => server.listen(port, '127.0.0.1', () => resolve({ server, port: server.address().port, prefix, log })));
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    const [dir, port, prefix] = process.argv.slice(2);
    const s = await serve(dir ?? '.', Number(port ?? 8080), prefix);
    console.log('serving ' + dir + ' at http://127.0.0.1:' + s.port + s.prefix + '/');
}
