// A small PDF written in the browser for the examples, so the repository ships no PDF file:
// five Letter pages of Helvetica text, an outline (bookmarks) and document information.
// samplePdf() returns an ArrayBuffer; samplePdfUrl() a blob: URL of the same bytes.
// sampleFormPdf() returns a one-page order form (AcroForm): text fields, a check box, radio
// buttons, a combo box and a total that the viewer calculates (quantity × price) without
// running the document's JavaScript.
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

    /** Objects to a PDF file: objects[n - 1] is object n, the catalog is object `root`. */
    function write(objects, root, info) {
        let pdf = '%PDF-1.7\n';
        const offsets = objects.map((body, i) => {
            const at = pdf.length;
            pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
            return at;
        });
        const xref = pdf.length;
        pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
        for (const at of offsets) pdf += `${String(at).padStart(10, '0')} 00000 n \n`;
        pdf += `trailer\n<< /Size ${objects.length + 1} /Root ${root} 0 R${info ? ` /Info ${info} 0 R` : ''} >>\nstartxref\n${xref}\n%%EOF\n`;
        const bytes = new Uint8Array(pdf.length);
        for (let i = 0; i < pdf.length; i++) bytes[i] = pdf.charCodeAt(i);
        return bytes.buffer;
    }

    function buildForm() {
        const objects = [];
        const add = body => { objects.push(body); return objects.length; };
        const stream = (dict, content) => add(`<< ${dict} /Length ${content.length} >>\nstream\n${content}\nendstream`);
        const catalog = add(null), pageTree = add(null), page = add(null);
        const helv = add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>');
        const bold = add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>');
        const js = code => `<< /S /JavaScript /JS (${escape(code)}) >>`;
        // Appearances of a check box or radio button: a box, and the box with a mark.
        const box = (w, round) => round
            ? `0.4 G 1 w ${w / 2} ${w / 2} m ${w / 2} ${w / 2} ${w / 2 - 0.5} 0 360 re S`
            : `0.4 G 1 w 0.5 0.5 ${w - 1} ${w - 1} re S`;
        const off = stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', box(14, false));
        const on = stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', `${box(14, false)}\n0.05 0.43 0.48 RG 2 w 3 7 m 6 4 l 11 11 l S`);
        const dotOff = stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', '0.4 G 1 w 7 13.5 m 10.6 13.5 13.5 10.6 13.5 7 c 13.5 3.4 10.6 0.5 7 0.5 c 3.4 0.5 0.5 3.4 0.5 7 c 0.5 10.6 3.4 13.5 7 13.5 c S');
        const dotOn = stream('/Type /XObject /Subtype /Form /BBox [0 0 14 14]', '0.4 G 1 w 7 13.5 m 10.6 13.5 13.5 10.6 13.5 7 c 13.5 3.4 10.6 0.5 7 0.5 c 3.4 0.5 0.5 3.4 0.5 7 c 0.5 10.6 3.4 13.5 7 13.5 c S\n0.05 0.43 0.48 rg 7 10.5 m 8.9 10.5 10.5 8.9 10.5 7 c 10.5 5.1 8.9 3.5 7 3.5 c 5.1 3.5 3.5 5.1 3.5 7 c 3.5 8.9 5.1 10.5 7 10.5 c f');
        const mk = '/MK << /BC [0.6 0.6 0.6] /BG [1 1 1] >>';
        const text = (name, rect, extra = '') => add(`<< /Type /Annot /Subtype /Widget /FT /Tx /T (${name}) /Rect [${rect}] /P ${page} 0 R /F 4 /DA (/Helv 11 Tf 0 g) ${mk} ${extra} >>`);
        const fields = [];
        fields.push(text('name', '180 640 520 662', '/TU (Your name)'));
        fields.push(text('email', '180 606 520 628', '/TU (E-mail address)'));
        fields.push(add(`<< /Type /Annot /Subtype /Widget /FT /Btn /T (subscribe) /TU (Send me the newsletter) /Rect [180 576 194 590] /P ${page} 0 R /F 4 /V /Off /AS /Off /AP << /N << /Yes ${on} 0 R /Off ${off} 0 R >> >> >>`));
        const plan = add(null);
        const basic = add(`<< /Type /Annot /Subtype /Widget /Parent ${plan} 0 R /Rect [180 540 194 554] /P ${page} 0 R /F 4 /AS /Basic /AP << /N << /Basic ${dotOn} 0 R /Off ${dotOff} 0 R >> >> >>`);
        const pro = add(`<< /Type /Annot /Subtype /Widget /Parent ${plan} 0 R /Rect [280 540 294 554] /P ${page} 0 R /F 4 /AS /Off /AP << /N << /Pro ${dotOn} 0 R /Off ${dotOff} 0 R >> >> >>`);
        objects[plan - 1] = `<< /FT /Btn /Ff ${(1 << 15) | (1 << 14)} /T (plan) /TU (Plan) /V /Basic /Kids [${basic} 0 R ${pro} 0 R] >>`;
        fields.push(plan);
        fields.push(add(`<< /Type /Annot /Subtype /Widget /FT /Ch /Ff ${1 << 17} /T (country) /TU (Country) /Opt [[(FR) (France)] [(IN) (India)] [(US) (United States)]] /V (IN) /Rect [180 498 360 520] /P ${page} 0 R /F 4 /DA (/Helv 11 Tf 0 g) ${mk} >>`));
        const number = (decimals, currency) => js(`AFNumber_Format(${decimals}, 0, 0, 0, "${currency}", true);`);
        fields.push(text('qty', '180 440 260 462', `/TU (Quantity) /Q 2 /AA << /F ${number(0, '')} /K ${js('AFNumber_Keystroke(0, 0, 0, 0, "", true);')} >>`));
        fields.push(text('price', '300 440 400 462', `/TU (Unit price) /Q 2 /V (12.5) /AA << /F ${number(2, '$')} /K ${js('AFNumber_Keystroke(2, 0, 0, 0, "$", true);')} /V ${js('AFRange_Validate(true, 0, true, 100000);')} >>`));
        const total = text('total', '420 440 520 462', `/TU (Total) /Q 2 /AA << /C ${js('AFSimple_Calculate("PRD", new Array ("qty", "price"));')} /F ${number(2, '$')} >>`);
        fields.push(total);

        const labels = [['Name', 646], ['E-mail', 612], ['Newsletter', 579], ['Plan', 543], ['Country', 504]];
        let content = `BT /F2 24 Tf 72 712 Td (${escape('Order form')}) Tj ET\n0.05 0.43 0.48 RG 2 w 72 696 m 540 696 l S\n`;
        for (const [label, y] of labels) content += `BT /F1 12 Tf 72 ${y} Td (${escape(label)}) Tj ET\n`;
        content += `BT /F1 12 Tf 200 543 Td (Basic) Tj ET\nBT /F1 12 Tf 300 543 Td (Pro) Tj ET\n`;
        content += `BT /F1 10 Tf 180 468 Td (Quantity) Tj ET\nBT /F1 10 Tf 300 468 Td (Unit price) Tj ET\nBT /F1 10 Tf 420 468 Td (Total \\(calculated\\)) Tj ET\n`;
        content += `BT /F1 12 Tf 72 446 Td (Order) Tj ET\n`;
        content += `BT /F1 10 Tf 72 400 Td (${escape('The total is quantity times price. The viewer calculates it as Acrobat does, without running JavaScript.')}) Tj ET\n`;
        const contents = stream('', content);
        objects[page - 1] = `<< /Type /Page /Parent ${pageTree} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 ${helv} 0 R /F2 ${bold} 0 R >> >> /Contents ${contents} 0 R` +
            ` /Annots [${fields.filter(f => f !== plan).concat([basic, pro]).map(f => `${f} 0 R`).join(' ')}] >>`;
        objects[pageTree - 1] = `<< /Type /Pages /Kids [${page} 0 R] /Count 1 >>`;
        const acroForm = add(`<< /Fields [${fields.map(f => `${f} 0 R`).join(' ')}] /CO [${total} 0 R] /DA (/Helv 11 Tf 0 g) /DR << /Font << /Helv ${helv} 0 R >> >> >>`);
        objects[catalog - 1] = `<< /Type /Catalog /Pages ${pageTree} 0 R /AcroForm ${acroForm} 0 R >>`;
        const info = add('<< /Title (Order form: SDK example) /Author (PDF Viewer examples) /Producer (sample-pdf.js) >>');
        return write(objects, catalog, info);
    }

    global.samplePdf = () => build();
    global.sampleFormPdf = () => buildForm();
    global.samplePdfUrl = () => URL.createObjectURL(new Blob([build()], { type: 'application/pdf' }));
})(globalThis);
