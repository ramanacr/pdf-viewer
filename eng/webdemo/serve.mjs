// A minimal static server for the published web demo (no dependencies, Node 18+).
//
//   node eng/webdemo/serve.mjs <publish>/wwwroot [--port 8080] [--base /pdf-viewer/] [--gzip]
//                              [--rate <KB/s>] [--latency <ms>] [--log-ranges]
//
// --base serves the site under a path, as GitHub Pages serves a project site; the published
// index.html must carry the same <base href> (the workflow rewrites it). --gzip compresses
// responses on the fly, roughly as GitHub Pages does, to measure realistic transfer sizes.
// Range requests (one range: "bytes=a-b", "bytes=a-" or "bytes=-n") are answered with 206 and
// Content-Range, as GitHub Pages and most servers do; every file has Accept-Ranges: bytes.
// --rate and --latency slow every response down (kilobytes per second, delay before the
// answer) to measure on a slow network; --log-ranges prints each PDF range or whole file served.
// It listens on 127.0.0.1 only and logs nothing but the URL (and the ranges, when asked).

import { createServer } from 'node:http';
import { createReadStream, statSync } from 'node:fs';
import { Transform } from 'node:stream';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { createGzip } from 'node:zlib';

const args = process.argv.slice(2);
// The root is the first argument that is neither a flag nor the value of a flag that takes one.
const valued = new Set(['--port', '--base', '--rate', '--latency']);
const root = resolve(args.find((a, i) => !a.startsWith('--') && !valued.has(args[i - 1])) ?? '.');
const option = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(option('--port', '8080'));
let base = option('--base', '/');
if (!base.endsWith('/')) base += '/';
const gzip = args.includes('--gzip');
const rate = Number(option('--rate', '0')) * 1024;        // bytes per second, 0 = unlimited
const latency = Number(option('--latency', '0'));         // ms before each answer
const logRanges = args.includes('--log-ranges');

/** Passes bytes on at most `rate` bytes per second. */
function throttle() {
    let sent = 0, started = 0;
    return new Transform({
        transform(chunk, _, done) {
            if (!started) started = Date.now();
            sent += chunk.length;
            const due = started + (sent / rate) * 1000;
            setTimeout(() => done(null, chunk), Math.max(0, due - Date.now()));
        },
    });
}

/** Pipes the file to the response (through `transform`, if any); when the client goes away first, the streams are destroyed and the file closed. */
function send(res, file, transform) {
    const streams = [file, transform, rate > 0 ? throttle() : null].filter(Boolean);
    res.on('close', () => {
        if (!res.writableFinished) for (const stream of streams) stream.destroy();
    });
    streams.reduce((from, to) => from.pipe(to)).pipe(res);
}

/** One range "bytes=a-b", "bytes=a-" or "bytes=-n" as { start, end } (inclusive); null when there is none (or several); false when it is outside the file. */
function parseRange(header, size) {
    if (typeof header !== 'string') return null;
    const m = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
    if (!m || (m[1] === '' && m[2] === '')) return null;
    let start, end;
    if (m[1] === '') {
        const n = Number(m[2]);
        if (n === 0) return false;
        start = Math.max(0, size - n);
        end = size - 1;
    } else {
        start = Number(m[1]);
        end = m[2] === '' ? size - 1 : Math.min(Number(m[2]), size - 1);
    }
    return start <= end && start < size ? { start, end } : false;
}

const types = {
    '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.wasm': 'application/wasm', '.svg': 'image/svg+xml',
    '.png': 'image/png', '.ico': 'image/x-icon', '.dat': 'application/octet-stream', '.pdf': 'application/pdf',
    '.txt': 'text/plain; charset=utf-8', '.webmanifest': 'application/manifest+json', '.ttf': 'font/ttf', '.otf': 'font/otf',
};
const compressible = new Set(['.html', '.js', '.mjs', '.css', '.json', '.wasm', '.svg', '.dat', '.txt']);

createServer((req, res) => {
    if (latency > 0) setTimeout(() => handle(req, res), latency);
    else handle(req, res);
}).listen(port, '127.0.0.1', () => console.log(`Serving ${root} at http://127.0.0.1:${port}${base}`));

function handle(req, res) {
    const url = new URL(req.url, 'http://localhost');
    if (!url.pathname.startsWith(base)) {
        res.writeHead(302, { Location: base });
        return res.end();
    }
    let relative;
    try {
        relative = decodeURIComponent(url.pathname.slice(base.length)) || 'index.html';
    } catch (e) {
        if (!(e instanceof URIError)) throw e;
        res.writeHead(400, { 'Content-Type': 'text/plain' });
        return res.end('Bad request: the path is not valid percent-encoding');
    }
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
    const headers = { 'Content-Type': types[ext] ?? 'application/octet-stream', 'Cache-Control': 'no-cache', 'Accept-Ranges': 'bytes' };
    const head = req.method === 'HEAD';
    const range = parseRange(req.headers.range, stat.size);
    if (range === false) {
        res.writeHead(416, { ...headers, 'Content-Range': `bytes */${stat.size}` });
        return res.end();
    }
    if (range) {
        if (logRanges && ext === '.pdf') console.log(`${url.pathname} ${range.start}-${range.end} (${range.end - range.start + 1} bytes)`);
        res.writeHead(206, { ...headers, 'Content-Range': `bytes ${range.start}-${range.end}/${stat.size}`, 'Content-Length': range.end - range.start + 1 });
        if (head) return res.end();
        return send(res, createReadStream(file, { start: range.start, end: range.end }));
    }
    if (logRanges && ext === '.pdf') console.log(`${url.pathname} whole (${stat.size} bytes)`);
    if (gzip && compressible.has(ext) && /\bgzip\b/.test(req.headers['accept-encoding'] ?? '')) {
        res.writeHead(200, { ...headers, 'Content-Encoding': 'gzip', Vary: 'Accept-Encoding' });
        if (head) return res.end();
        send(res, createReadStream(file), createGzip({ level: 6 }));
    } else {
        res.writeHead(200, { ...headers, 'Content-Length': stat.size });
        if (head) return res.end();
        send(res, createReadStream(file));
    }
}
