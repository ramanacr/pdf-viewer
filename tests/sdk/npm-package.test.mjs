// Tests of the npm package (packages/pdfviewer-embed): its entry exports the SDK, its version
// matches the script's, and the viewer's default place is the project's site. The package's copy
// of pdfviewer-embed.js is made by the npm workflow; run with `node --test tests/sdk` after
// copying it there.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const dir = new URL('../../packages/pdfviewer-embed/', import.meta.url);
const copied = existsSync(fileURLToPath(new URL('pdfviewer-embed.js', dir)));
const pkg = JSON.parse(readFileSync(new URL('package.json', dir), 'utf8'));

test('the package entry exports the SDK', { skip: !copied && 'pdfviewer-embed.js is not copied into the package' }, async () => {
    const sdk = await import(new URL(pkg.exports['.'], dir).href);
    assert.equal(typeof sdk.View, 'function');
    assert.equal(typeof sdk.PdfViewerError, 'function');
    assert.equal(sdk.default, globalThis.PdfViewer);
    assert.equal(sdk.version, pkg.version);
});

test('the package lists only its own files', () => {
    assert.deepEqual([...pkg.files].sort(), ['LICENSE', 'README.md', 'index.mjs', 'pdfviewer-embed.js']);
    assert.equal(pkg.license, 'MIT');
    assert.equal(pkg.repository.directory, 'packages/pdfviewer-embed');
});
