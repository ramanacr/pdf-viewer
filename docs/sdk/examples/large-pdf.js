// A large PDF written in the browser (or in Node) for the range-loading example and its
// measurements, so the repository ships no large file: `pages` Letter pages, each with a title,
// a few lines of text, an image of `imageSize`² RGB pixels stored uncompressed (about 150 KB at
// 224), and optionally `shapes` filled rectangles (to make a page heavy to draw). Objects are
// written page by page (content, image, page), with the page tree and catalog at the end, as
// most writers do; the file is not linearized.
//
//   largePdf({ pages: 320 })  -> ArrayBuffer of about 48 MB
//
// In Node:  require('./large-pdf.js'); fs.writeFileSync('large.pdf', Buffer.from(globalThis.largePdf({ pages: 320 })));
(function (global) {
    'use strict';

    const encoder = new TextEncoder();

    function largePdf(options) {
        const pages = Math.max(1, Math.min(5000, (options && options.pages) || 320));
        const side = Math.max(8, Math.min(1024, (options && options.imageSize) || 224));
        const shapes = Math.max(0, Math.min(200000, (options && options.shapes) || 0));
        const parts = [];          // Uint8Arrays in file order
        let length = 0;
        const offsets = [];        // offsets[n] of object n
        const put = bytes => { parts.push(bytes); length += bytes.length; };
        const text = s => put(encoder.encode(s));
        const object = (n, head, data) => {
            offsets[n] = length;
            text(`${n} 0 obj\n${head}`);
            if (data) {
                text('\nstream\n');
                put(data);
                text('\nendstream');
            }
            text('\nendobj\n');
        };

        // Object numbers: 1 catalog, 2 page tree, 3 and 4 fonts, then 3 per page, then the info.
        const pageIds = [];
        text('%PDF-1.7\n%âã\n');
        object(3, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>');
        object(4, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>');
        let seed = 12345;
        const random = () => (seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) / 4294967296;
        for (let p = 1; p <= pages; p++) {
            const content = 5 + (p - 1) * 3, image = content + 1, page = content + 2;
            let ops = `BT /F2 26 Tf 72 720 Td (Page ${p} of ${pages}) Tj ET\n` +
                `BT /F1 12 Tf 72 690 Td (A generated page with an image of ${side} x ${side} pixels, stored uncompressed.) Tj ET\n` +
                `BT /F1 12 Tf 72 672 Td (The viewer fetches this page's bytes only when the page is shown.) Tj ET\n` +
                `q 360 0 0 360 126 250 cm /Im1 Do Q\n` +
                `BT /F1 10 Tf 72 60 Td (large-pdf.js) Tj ET\n`;
            if (shapes > 0) {
                const rects = [];
                for (let i = 0; i < shapes; i++) {
                    const x = (random() * 560).toFixed(1), y = (random() * 740).toFixed(1);
                    const w = (2 + random() * 14).toFixed(1), h = (2 + random() * 14).toFixed(1);
                    rects.push(`${random().toFixed(2)} ${random().toFixed(2)} ${random().toFixed(2)} rg ${x} ${y} ${w} ${h} re f`);
                }
                ops = rects.join('\n') + '\n' + ops;
            }
            const stream = encoder.encode(ops);
            object(content, `<< /Length ${stream.length} >>`, stream);
            const pixels = new Uint8Array(side * side * 3);
            const r0 = (p * 37) % 256, g0 = (p * 91) % 256, b0 = (p * 53) % 256;
            for (let y = 0, i = 0; y < side; y++) {
                for (let x = 0; x < side; x++, i += 3) {
                    // A gradient per page with noise, so that pages differ and do not compress.
                    pixels[i] = (r0 + x + (random() * 40 | 0)) & 255;
                    pixels[i + 1] = (g0 + y + (random() * 40 | 0)) & 255;
                    pixels[i + 2] = (b0 + ((x + y) >> 1)) & 255;
                }
            }
            object(image, `<< /Type /XObject /Subtype /Image /Width ${side} /Height ${side} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length ${pixels.length} >>`, pixels);
            object(page, `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> /XObject << /Im1 ${image} 0 R >> >> /Contents ${content} 0 R >>`);
            pageIds.push(page);
        }
        object(2, `<< /Type /Pages /Kids [${pageIds.map(id => `${id} 0 R`).join(' ')}] /Count ${pages} >>`);
        object(1, '<< /Type /Catalog /Pages 2 0 R >>');
        const info = 5 + pages * 3;
        object(info, `<< /Title (Large generated document, ${pages} pages) /Producer (large-pdf.js) >>`);

        const xref = length;
        let table = `xref\n0 ${info + 1}\n0000000000 65535 f \n`;
        for (let n = 1; n <= info; n++) table += `${String(offsets[n]).padStart(10, '0')} 00000 n \n`;
        text(table);
        text(`trailer\n<< /Size ${info + 1} /Root 1 0 R /Info ${info} 0 R >>\nstartxref\n${xref}\n%%EOF\n`);

        const out = new Uint8Array(length);
        let at = 0;
        for (const part of parts) {
            out.set(part, at);
            at += part.length;
        }
        return out.buffer;
    }

    global.largePdf = largePdf;
})(globalThis);
