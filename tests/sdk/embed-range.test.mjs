// Tests of the host side of range loading in the embed SDK (src/PdfViewer.Web/wwwroot/pdfviewer-embed.js):
// the checks on a viewer's range request, the URL range source (with a fake fetch) and the
// Adobe-style linearizationInfo adapter. No packages: run with `node --test tests/sdk`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInThisContext } from 'node:vm';
import { fileURLToPath } from 'node:url';

const sdkPath = fileURLToPath(new URL('../../src/PdfViewer.Web/wwwroot/pdfviewer-embed.js', import.meta.url));
globalThis.document = { baseURI: 'https://host.test/page.html', currentScript: null };
runInThisContext(readFileSync(sdkPath, 'utf8'), { filename: sdkPath });
const { UrlRangeSource, LinearizedRangeSource, checkRange, parseContentRange, MAX_RANGE_BYTES, MAX_RANGE_REQUESTS } = globalThis.PdfViewer._internals;

const MB = 1024 * 1024;
const file = new Uint8Array(3 * MB).map((_, i) => (i * 31) & 255);

/** A server for the fake fetch: answers single ranges with 206, or whole with 200; `options` change its behaviour. */
function server(options = {}) {
    const calls = [];
    globalThis.fetch = async (href, init) => {
        const range = init.headers.get('Range');
        calls.push({ href, range, credentials: init.credentials, auth: init.headers.get('Authorization') });
        const headers = new Map();
        const answer = (status, body) => ({
            status, ok: status >= 200 && status < 300, body: null,
            headers: { get: name => headers.get(name.toLowerCase()) ?? null },
            arrayBuffer: async () => body.slice().buffer,
        });
        const m = range && !options.noRanges ? /^bytes=(\d+)-(\d+)$/.exec(range) : null;
        if (!m) {
            headers.set('content-length', String(file.length));
            return answer(200, file);
        }
        const start = Number(m[1]), end = Math.min(Number(m[2]), file.length - 1);
        if (!options.hideContentRange) headers.set('content-range', options.wrongRange ? `bytes 0-${end - start}/${file.length}` : `bytes ${start}-${end}/${file.length}`);
        return answer(206, file.slice(start, options.short ? end : end + 1));
    };
    return calls;
}

const init = { href: 'https://files.test/big.pdf', headers: new Headers({ Authorization: 'Bearer x' }), credentials: 'include' };

test('a range request is accepted only inside the file, not empty, and at most 4 MB', () => {
    assert.equal(MAX_RANGE_BYTES, 4 * MB);
    assert.equal(MAX_RANGE_REQUESTS, 6);
    assert.ok(checkRange(0, 1, 10));
    assert.ok(checkRange(0, 4 * MB, 10 * MB));
    for (const [s, e, n] of [[5, 5, 10], [6, 5, 10], [-1, 5, 10], [0, 11, 10], [0, 4 * MB + 1, 10 * MB], [0.5, 2, 10], ['0', 2, 10], [0, NaN, 10], [0, 2 ** 60, 2 ** 61]]) {
        assert.equal(checkRange(s, e, n), false, `${s}-${e} of ${n}`);
    }
});

test('Content-Range is read strictly', () => {
    assert.deepEqual(parseContentRange('bytes 0-0/123'), { start: 0, end: 0, total: 123 });
    assert.deepEqual(parseContentRange('BYTES 10-19/20'), { start: 10, end: 19, total: 20 });
    for (const v of [null, '', 'bytes */20', 'bytes 0-20/20', 'bytes 5-4/20', 'items 0-1/2', 'bytes 0-1/*']) assert.equal(parseContentRange(v), null, String(v));
});

test('a server with ranges: one probe byte, then the end of the file, from the given URL only', async () => {
    const calls = server();
    const { source, buffer } = await UrlRangeSource.open(init);
    assert.equal(buffer, undefined);
    assert.equal(source.length, file.length);
    assert.equal(source.initialOffset, file.length - MB);
    assert.deepEqual(new Uint8Array(source.initial), file.slice(file.length - MB));
    assert.deepEqual(calls.map(c => c.range), ['bytes=0-0', `bytes=${file.length - MB}-${file.length - 1}`]);
    assert.ok(calls.every(c => c.href === init.href && c.credentials === 'include' && c.auth === 'Bearer x'));

    const part = await source.read(1000, 2000);
    assert.deepEqual(new Uint8Array(part), file.slice(1000, 2000));
    assert.equal(calls.at(-1).range, 'bytes=1000-1999');
    await assert.rejects(source.read(0, file.length + 1), /not inside the file/);
    await assert.rejects(source.read(0, 4 * MB + 1), /not inside the file/);
});

test('a server without ranges, or one that hides Content-Range, gives the whole file', async () => {
    server({ noRanges: true });
    const plain = await UrlRangeSource.open(init);
    assert.equal(plain.source, undefined);
    assert.equal(plain.buffer.byteLength, file.length);

    const calls = server({ hideContentRange: true });
    const hidden = await UrlRangeSource.open(init);
    assert.equal(hidden.buffer.byteLength, file.length);
    assert.deepEqual(calls.map(c => c.range), ['bytes=0-0', null]);
});

test('a range answer must be exactly the bytes asked for', async () => {
    server();
    const { source } = await UrlRangeSource.open(init);
    server({ wrongRange: true });
    await assert.rejects(source.read(5000, 6000), /another range/);
    server({ short: true });
    await assert.rejects(source.read(5000, 6000), /fewer bytes/);
});

test('linearizationInfo: getInfo, getInitialBuffer and getFileBufferRanges (end inclusive)', async () => {
    const asked = [];
    const info = {
        getInfo: async () => ({ fileSize: file.length }),
        getInitialBuffer: async () => file.slice(0, 1000),
        getFileBufferRanges: async ranges => {
            asked.push(...ranges);
            return { bufferList: ranges.map(r => file.slice(r.start, r.end + 1).buffer) };
        },
    };
    const source = await LinearizedRangeSource.open(info, Promise.resolve(file.buffer));
    assert.equal(source.length, file.length);
    assert.equal(source.initialOffset, 0);
    assert.equal(source.initial.byteLength, 1000);
    const part = await source.read(10, 20);
    assert.deepEqual(new Uint8Array(part), file.slice(10, 20));
    assert.deepEqual(asked, [{ start: 10, end: 19 }]);
    assert.equal((await source.readWhole()).byteLength, file.length);

    // Plain arrays of buffers and { buffer, start } initial buffers are accepted too.
    const other = await LinearizedRangeSource.open({
        getInfo: async () => ({ fileSize: file.length }),
        getInitialBuffer: async () => ({ buffer: file.slice(2 * MB, 2 * MB + 100).buffer, start: 2 * MB }),
        getFileBufferRanges: async ranges => ranges.map(r => file.slice(r.start, r.end + 1)),
    });
    assert.equal(other.initialOffset, 2 * MB);
    assert.deepEqual(new Uint8Array(await other.read(0, 5)), file.slice(0, 5));
});

test('linearizationInfo that is incomplete or answers wrongly is refused', async () => {
    await assert.rejects(LinearizedRangeSource.open({ getInfo: async () => ({}) }), /getInfo\(\) and getFileBufferRanges/);
    await assert.rejects(LinearizedRangeSource.open({ getInfo: async () => ({ fileSize: -1 }), getFileBufferRanges: async () => [] }), /fileSize/);
    await assert.rejects(LinearizedRangeSource.open({ getInfo: async () => ({ fileSize: 600 * MB }), getFileBufferRanges: async () => [] }), e => e.code === 'TOO_LARGE');
    const wrong = await LinearizedRangeSource.open({ getInfo: async () => ({ fileSize: 100 }), getFileBufferRanges: async () => ({ bufferList: [new ArrayBuffer(3)] }) });
    await assert.rejects(wrong.read(0, 10), /must resolve with 10 bytes/);
    await assert.rejects(wrong.read(50, 101), /not inside the file/);
    // An initial buffer that does not fit the file is ignored, not trusted.
    const ignored = await LinearizedRangeSource.open({
        getInfo: async () => ({ fileSize: 100 }), getInitialBuffer: async () => new ArrayBuffer(500), getFileBufferRanges: async () => [],
    });
    assert.equal(ignored.initial, null);
});
