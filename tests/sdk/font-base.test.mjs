// The font folder rule of the viewer (src/PdfViewer.Web/wwwroot/js/font-base.js, used by viewer.js
// and render-worker.js) on the shared case list (font-base-cases.json), which EmbedSdkTests also
// runs against EmbedFrame.ParseFontBase, so the JavaScript and C# rules stay the same. The SDK's
// fontBasePath must only ever produce a folder that this rule accepts. Run with `node --test tests/sdk`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInThisContext } from 'node:vm';
import { fileURLToPath } from 'node:url';

// Loaded as a module from its text: a .js file outside a "type": "module" package is not taken as
// ES module syntax by every Node version that CI may have.
const moduleText = readFileSync(new URL('../../src/PdfViewer.Web/wwwroot/js/font-base.js', import.meta.url), 'utf8');
const { fontBaseOf, fontFilePath } = await import('data:text/javascript,' + encodeURIComponent(moduleText));

const cases = JSON.parse(readFileSync(new URL('./font-base-cases.json', import.meta.url), 'utf8'));

const sdkPath = fileURLToPath(new URL('../../src/PdfViewer.Web/wwwroot/pdfviewer-embed.js', import.meta.url));
globalThis.document = { baseURI: 'https://host.test/page.html', currentScript: null };
runInThisContext(readFileSync(sdkPath, 'utf8'), { filename: sdkPath });
const { fontBasePath } = globalThis.PdfViewer._internals;

test('the viewer accepts exactly the valid font folders', () => {
    for (const value of cases.valid) assert.equal(fontBaseOf(value), value, value);
    for (const value of cases.invalid) assert.equal(fontBaseOf(value), null, value);
    for (const value of [null, undefined, 42, {}, ['/fonts/'], '/' + 'a'.repeat(600) + '/']) assert.equal(fontBaseOf(value), null);
});

test('a font file goes to the given folder, or the default one; never elsewhere', () => {
    assert.equal(fontFilePath('noto/index.json', '/shared/fonts/', 'fonts/'), '/shared/fonts/noto/index.json');
    assert.equal(fontFilePath('LiberationSans-Regular.ttf', null, 'fonts/'), 'fonts/LiberationSans-Regular.ttf');
    for (const bad of cases.invalid) assert.equal(fontFilePath('noto/index.json', bad, 'fonts/'), 'fonts/noto/index.json', bad);
    for (const file of ['', '../x.ttf', 'noto/../x', 'noto//x', './x', '/x']) assert.equal(fontFilePath(file, null, 'fonts/'), null, file);
    assert.equal(fontFilePath('a b.ttf', null, 'fonts/'), 'fonts/a%20b.ttf');
});

test('the SDK only hands the viewer folders that the viewer accepts', () => {
    const viewer = new URL('https://pdf.example.com/viewer/');
    for (const value of cases.valid) assert.equal(fontBasePath(value, viewer), value, value);
    for (const value of [...cases.valid, ...cases.invalid]) {
        let path;
        try {
            path = fontBasePath(value, viewer);
        } catch (e) {
            assert.equal(e.code, 'INVALID_ARGUMENT', value);
            continue;
        }
        // A relative or dotted input is resolved by URL first; whatever comes out must be valid.
        assert.equal(fontBaseOf(path), path, `${value} -> ${path}`);
    }
});
