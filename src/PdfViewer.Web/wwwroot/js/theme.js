// Applies the theme the user picked before the page draws, so a dark theme does not flash light.
try {
    const theme = localStorage.getItem('pdfviewer.theme');
    if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
} catch { /* storage unavailable: follow the system */ }
