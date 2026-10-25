// A small PDF written in the browser for the examples, so the repository ships no PDF file:
// five Letter pages of Helvetica text, an outline (bookmarks) and document information.
// samplePdf() returns an ArrayBuffer; samplePdfUrl() a blob: URL of the same bytes.
(function (global) {
    'use strict';

    const pages = [
        ['Quarterly report', 'This PDF was written by the example page itself, in your browser.',
            'The viewer reads it with its own engine, compiled to WebAssembly.', 'Nothing is uploaded: there is no server to send it to.'],
        ['Sales', 'Sales grew in every region this quarter.', 'The invoice total for the north region is 1,240,000.',
            'Search for the word invoice to find it on two pages.'],
        ['Costs', 'Costs stayed flat. Travel fell and software rose.', 'Select this text with the mouse and copy it with Ctrl+C.'],
        ['Outlook', 'Next quarter we plan to open two offices.', 'Each open office will send one invoice a month.'],
        ['Appendix', 'This page is the last one.', 'Use the outline in the side panel to jump between pages.'],
    ];

    function escape(text) {
        return text.replace(/\\/g, '\\\\').replace(/\(/g, '\\(').replace(/\)/g, '\\)');
    }

    function build() {
        const objects = [];                 // object n is objects[n - 1]
        const add = body => { objects.push(body); return objects.length; };
        const catalog = add(null), pageTree = add(null), font = add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>');
        const bold = add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>');
        const pageIds = [];
        pages.forEach(([title, ...lines], i) => {
            let content = `BT /F2 28 Tf 72 700 Td (${escape(title)}) Tj ET\n`;
            lines.forEach((line, j) => { content += `BT /F1 14 Tf 72 ${650 - j * 24} Td (${escape(line)}) Tj ET\n`; });
            content += `BT /F1 10 Tf 72 60 Td (Page ${i + 1} of ${pages.length}) Tj ET\n`;
            content += '0.05 0.43 0.48 RG 2 w 72 680 m 540 680 l S\n';
            const stream = add(`<< /Length ${content.length} >>\nstream\n${content}endstream`);
            pageIds.push(add(`<< /Type /Page /Parent ${pageTree} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 ${font} 0 R /F2 ${bold} 0 R >> >> /Contents ${stream} 0 R >>`));
        });
        const outline = add(null);
        const items = pages.map(() => add(null));
        items.forEach((id, i) => {
            objects[id - 1] = `<< /Title (${escape(pages[i][0])}) /Parent ${outline} 0 R /Dest [${pageIds[i]} 0 R /XYZ 0 792 null]` +
                (i > 0 ? ` /Prev ${items[i - 1]} 0 R` : '') + (i < items.length - 1 ? ` /Next ${items[i + 1]} 0 R` : '') + ' >>';
        });
        objects[outline - 1] = `<< /Type /Outlines /First ${items[0]} 0 R /Last ${items[items.length - 1]} 0 R /Count ${items.length} >>`;
        objects[pageTree - 1] = `<< /Type /Pages /Kids [${pageIds.map(p => `${p} 0 R`).join(' ')}] /Count ${pageIds.length} >>`;
        objects[catalog - 1] = `<< /Type /Catalog /Pages ${pageTree} 0 R /Outlines ${outline} 0 R /PageMode /UseOutlines >>`;
        const info = add('<< /Title (Quarterly report: SDK example) /Author (PDF Viewer examples) /Producer (sample-pdf.js) >>');

        let pdf = '%PDF-1.7\n';
        const offsets = objects.map((body, i) => {
            const at = pdf.length;
            pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
            return at;
        });
        const xref = pdf.length;
        pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
        for (const at of offsets) pdf += `${String(at).padStart(10, '0')} 00000 n \n`;
        pdf += `trailer\n<< /Size ${objects.length + 1} /Root ${catalog} 0 R /Info ${info} 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
        // Every character above is ASCII, so one character is one byte.
        const bytes = new Uint8Array(pdf.length);
        for (let i = 0; i < pdf.length; i++) bytes[i] = pdf.charCodeAt(i);
        return bytes.buffer;
    }

    global.samplePdf = () => build();
    global.samplePdfUrl = () => URL.createObjectURL(new Blob([build()], { type: 'application/pdf' }));
})(globalThis);
