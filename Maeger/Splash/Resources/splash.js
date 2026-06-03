// Splash animation controller
(function() {
    const totalDuration = 1600; // 0.8s draw + 0.3s hold + 0.5s fade
    
    setTimeout(() => {
        document.body.classList.add('fade-out');
    }, totalDuration - 500);

    // Signal parent that splash is complete after animation
    setTimeout(() => {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'splashComplete' });
        }
    }, totalDuration);
})();
