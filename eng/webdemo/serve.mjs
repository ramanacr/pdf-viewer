// A minimal static server for the published web demo (no dependencies, Node 18+).
//
//   node eng/webdemo/serve.mjs <publish>/wwwroot [--port 8080] [--base /pdf-viewer/] [--gzip]
//
// --base serves the site under a path, as GitHub Pages serves a project site; the published
// index.html must carry the same <base href> (the workflow rewrites it). --gzip compresses
// responses on the fly, roughly as GitHub Pages does, to measure realistic transfer sizes.
// It listens on 127.0.0.1 only and logs nothing but the URL.

import { createServer } from 'node:http';
import { createReadStream, statSync } from 'node:fs';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { createGzip } from 'node:zlib';

const args = process.argv.slice(2);
// The root is the first argument that is neither a flag nor the value of a flag that takes one.
const valued = new Set(['--port', '--base']);
const root = resolve(args.find((a, i) => !a.startsWith('--') && !valued.has(args[i - 1])) ?? '.');
const option = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(option('--port', '8080'));
let base = option('--base', '/');
if (!base.endsWith('/')) base += '/';
const gzip = args.includes('--gzip');

const types = {
    '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.wasm': 'application/wasm', '.svg': 'image/svg+xml',
    '.png': 'image/png', '.ico': 'image/x-icon', '.dat': 'application/octet-stream', '.pdf': 'application/pdf',
    '.txt': 'text/plain; charset=utf-8', '.webmanifest': 'application/manifest+json',
};
const compressible = new Set(['.html', '.js', '.mjs', '.css', '.json', '.wasm', '.svg', '.dat', '.txt']);

createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (!url.pathname.startsWith(base)) {
        res.writeHead(302, { Location: base });
        return res.end();
    }
    let relative = decodeURIComponent(url.pathname.slice(base.length)) || 'index.html';
    let file = normalize(join(root, relative));
    if (file !== root && !file.startsWith(root + sep)) {
        res.writeHead(403);
        return res.end();
    }
    let stat;
    try {
        stat = statSync(file);
        if (stat.isDirectory()) { file = join(file, 'index.html'); stat = statSync(file); }
    } catch {
        res.writeHead(404, { 'Content-Type': 'text/plain' });
        return res.end('Not found');
    }
    const ext = extname(file).toLowerCase();
    const headers = { 'Content-Type': types[ext] ?? 'application/octet-stream', 'Cache-Control': 'no-cache' };
    if (gzip && compressible.has(ext) && /\bgzip\b/.test(req.headers['accept-encoding'] ?? '')) {
        res.writeHead(200, { ...headers, 'Content-Encoding': 'gzip', Vary: 'Accept-Encoding' });
        createReadStream(file).pipe(createGzip({ level: 6 })).pipe(res);
    } else {
        res.writeHead(200, { ...headers, 'Content-Length': stat.size });
        createReadStream(file).pipe(res);
    }
}).listen(port, '127.0.0.1', () => console.log(`Serving ${root} at http://127.0.0.1:${port}${base}`));
