// Applies the theme the user picked before the page draws, so a dark theme does not flash light.
// Embedded in a host page (index.html?embed=1, see pdfviewer-embed.js), the host's config sets
// the theme later and nothing is read from storage; the classes set here shape the boot screen.
(function () {
    const query = new URLSearchParams(location.search);
    if (query.has('embed')) {
        document.documentElement.classList.add('embed');
        if (query.get('mode') === 'IN_LINE') document.documentElement.classList.add('embed-inline');
        return;
    }
    try {
        const theme = localStorage.getItem('pdfviewer.theme');
        if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
    } catch { /* storage unavailable: follow the system */ }
})();
