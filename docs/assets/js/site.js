// PDF Viewer documentation site: menu, theme, heading anchors, table of contents, copy buttons
// and the live viewer of the home page. No external requests; nothing is recorded.
(function () {
  'use strict';
  var root = document.documentElement;

  // Theme: the button switches between light and dark and remembers the choice.
  function currentTheme() {
    var set = root.getAttribute('data-theme');
    if (set) return set;
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  function labelThemeButtons() {
    var next = currentTheme() === 'dark' ? 'light' : 'dark';
    document.querySelectorAll('.theme-toggle').forEach(function (b) {
      b.setAttribute('aria-label', 'Switch to ' + next + ' theme');
    });
  }
  document.querySelectorAll('.theme-toggle').forEach(function (button) {
    button.addEventListener('click', function () {
      var next = currentTheme() === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem('site-theme', next); } catch (e) { /* storage blocked: the choice lasts for this page */ }
      labelThemeButtons();
    });
  });
  labelThemeButtons();

  // Menu on small screens.
  var header = document.querySelector('.site-header');
  var toggle = document.querySelector('.nav-toggle');
  if (header && toggle) {
    toggle.addEventListener('click', function () {
      var open = header.classList.toggle('nav-open');
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && header.classList.contains('nav-open')) {
        header.classList.remove('nav-open');
        toggle.setAttribute('aria-expanded', 'false');
        toggle.focus();
      }
    });
  }

  // The documentation menu is folded on small screens.
  var details = document.querySelector('.sidebar-details');
  if (details && window.matchMedia('(max-width: 860px)').matches) details.removeAttribute('open');

  var article = document.querySelector('[data-toc-source]');

  // Wide tables scroll in their own box.
  document.querySelectorAll('.prose table').forEach(function (table) {
    if (table.parentElement.classList.contains('table-wrap')) return;
    var wrap = document.createElement('div');
    wrap.className = 'table-wrap';
    table.parentNode.insertBefore(wrap, table);
    wrap.appendChild(table);
  });

  // Anchors on headings, and the table of contents.
  if (article) {
    var headings = Array.prototype.slice.call(article.querySelectorAll('h2[id], h3[id]'));
    article.querySelectorAll('h2[id], h3[id], h4[id]').forEach(function (h) {
      var a = document.createElement('a');
      a.className = 'anchor';
      a.href = '#' + h.id;
      a.setAttribute('aria-label', 'Link to this section');
      a.textContent = '#';
      h.appendChild(a);
    });

    var toc = document.querySelector('.docs-toc');
    var list = toc && toc.querySelector('.toc-list');
    if (list && headings.length >= 2) {
      var links = headings.map(function (h) {
        var a = document.createElement('a');
        a.href = '#' + h.id;
        a.className = 'toc-' + h.tagName.toLowerCase();
        var clone = h.cloneNode(true);
        clone.querySelectorAll('.anchor').forEach(function (n) { n.remove(); });
        a.textContent = clone.textContent.trim();
        list.appendChild(a);
        return a;
      });
      toc.hidden = false;

      if ('IntersectionObserver' in window) {
        var visible = new Map();
        var observer = new IntersectionObserver(function (entries) {
          entries.forEach(function (entry) { visible.set(entry.target, entry.isIntersecting); });
          var active = null;
          for (var i = 0; i < headings.length; i++) {
            if (visible.get(headings[i])) { active = i; break; }
          }
          if (active === null) return;
          links.forEach(function (l, i) { l.classList.toggle('is-active', i === active); });
        }, { rootMargin: '-64px 0px -60% 0px' });
        headings.forEach(function (h) { observer.observe(h); });
      }
    }
  }

  // A copy button on each code block.
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
    return new Promise(function (resolve, reject) {
      var area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.select();
      try { document.execCommand('copy') ? resolve() : reject(); } catch (e) { reject(e); }
      area.remove();
    });
  }
  var copyIcon = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3"/></svg>';
  document.querySelectorAll('div.highlighter-rouge, figure.highlight, .snippet').forEach(function (block) {
    if (block.querySelector('.copy-button') || block.parentElement.closest('.highlighter-rouge, .snippet')) return;
    var pre = block.querySelector('pre');
    if (!pre) return;
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'copy-button';
    button.innerHTML = copyIcon + '<span>Copy</span>';
    button.setAttribute('aria-label', 'Copy code');
    button.addEventListener('click', function () {
      var code = pre.querySelector('code') || pre;
      copyText(code.innerText.replace(/\n$/, '')).then(function () {
        button.classList.add('is-done');
        button.lastChild.textContent = 'Copied';
        setTimeout(function () { button.classList.remove('is-done'); button.lastChild.textContent = 'Copy'; }, 1600);
      }, function () {
        button.lastChild.textContent = 'Press Ctrl+C';
      });
    });
    block.appendChild(button);
  });

  // Home page: the live viewer loads only when the reader asks for it.
  var demo = document.querySelector('[data-live-demo]');
  if (demo) {
    var start = demo.querySelector('[data-live-demo-start]');
    var status = demo.querySelector('[data-live-demo-status]');
    function load(src) {
      return new Promise(function (resolve, reject) {
        // Only scripts from this site's own origin are loaded.
        var url;
        try { url = new URL(src, window.location.href); } catch (e) { url = null; }
        if (!url || url.origin !== window.location.origin) {
          reject(new Error('Refused to load a script from another site'));
          return;
        }
        var s = document.createElement('script');
        s.src = url.href;
        s.onload = resolve;
        s.onerror = function () { reject(new Error('Could not load ' + src)); };
        document.body.appendChild(s);
      });
    }
    start.addEventListener('click', function () {
      start.disabled = true;
      status.textContent = 'Loading the viewer…';
      load(demo.getAttribute('data-sdk'))
        .then(function () { return load(demo.getAttribute('data-sample')); })
        .then(function () {
          var view = new window.PdfViewer.View({ divId: 'home-viewer' });
          demo.classList.add('is-loaded');
          return view.previewFile({
            content: { arrayBuffer: window.samplePdf() },
            metaData: { fileName: 'Quarterly report.pdf' },
          }, { embedMode: 'SIZED_CONTAINER', showFullScreen: true, defaultViewMode: 'FIT_WIDTH', theme: currentTheme() });
        })
        .catch(function (error) {
          demo.classList.remove('is-loaded');
          start.disabled = false;
          status.textContent = (error && error.code ? error.code + ': ' : '') + (error && error.message ? error.message : 'The viewer did not load.');
        });
    });
  }
})();
