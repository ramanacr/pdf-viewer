// PdfEngine API reference: a link back to the documentation site, and the repository link.
// The reference is served from /api/ below the site root, so the link is relative and works in
// any copy of the site. docfx:rel is the path from this page to the root of the reference.
export default {
  iconLinks: [
    { icon: 'github', href: 'https://github.com/ramanacr/pdf-viewer', title: 'Source code on GitHub' },
  ],
  start: () => {
    const meta = document.querySelector('meta[name="docfx:rel"]');
    const rel = meta ? meta.getAttribute('content') || '' : '';
    const brand = document.querySelector('header .navbar-brand');
    if (!brand || document.querySelector('.pv-site-link')) return;
    const link = document.createElement('a');
    link.className = 'pv-site-link';
    link.href = rel + '../';
    link.textContent = '← PDF Viewer site';
    brand.insertAdjacentElement('afterend', link);
  },
};
