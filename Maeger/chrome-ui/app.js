const tabsBar = document.getElementById('tabs-bar');
const addressBar = document.querySelector('#addressBarWrapper input');
const btnBack = document.getElementById('btnBack');
const btnForward = document.getElementById('btnForward');
const btnReload = document.getElementById('btnReload');
const btnHistory = document.getElementById('btnHistory');
const btnNewTab = document.getElementById('btnNewTab');
const btnSettings = document.getElementById('btnSettings');
const btnBookmarks = document.getElementById('btnBookmarks');
const btnDownloads = document.getElementById('btnDownloads');
const btnAddShortcut = document.getElementById('btnAddShortcut');
const btnMenu = document.getElementById('btnMenu');
const btnMinimize = document.getElementById('btnMinimize');
const btnMaximize = document.getElementById('btnMaximize');
const btnClose = document.getElementById('btnClose');

let currentState = { tabs: [], url: '' };
let previousTabIds = [];

function init() {
    // Ensure we are inside WebView2; fail gracefully if opened in a normal browser
    if (!window.chrome?.webview) {
        console.error('Yalb Chrome UI must run inside WebView2');
        return;
    }

    // Listen for state updates pushed from the C# backend
    window.chrome.webview.addEventListener('message', onMessage);

    // Toolbar buttons
    btnBack.addEventListener('click', () => post('goBack'));
    btnForward.addEventListener('click', () => post('goForward'));
    btnReload.addEventListener('click', () => post('reload'));
    btnNewTab.addEventListener('click', () => post('newTab'));
    btnHistory?.addEventListener('click', () => post('showHistory'));
    btnSettings?.addEventListener('click', () => post('navigate', { url: 'yalb://settings' }));
    btnBookmarks?.addEventListener('click', () => post('navigate', { url: 'yalb://bookmarks' }));
    btnDownloads?.addEventListener('click', () => post('navigate', { url: 'yalb://downloads' }));
    btnAddShortcut?.addEventListener('click', () => post('newTab'));
    btnMenu?.addEventListener('click', () => post('showHistory'));
    btnMinimize?.addEventListener('click', () => post('minimize'));
    btnMaximize?.addEventListener('click', () => post('maximize'));
    btnClose?.addEventListener('click', () => post('closeWindow'));

    // Address bar: Enter to navigate
    addressBar.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            post('navigate', { url: addressBar.value });
        }
    });
    // Keyboard shortcuts when focus is inside the chrome UI (address bar, buttons, etc.)
    document.addEventListener('keydown', (e) => {
        if (e.ctrlKey) {
            if (e.key === 'Tab' && !e.shiftKey) {
                e.preventDefault();
                post('nextTab');
                return;
            }
            if (e.key === 'Tab' && e.shiftKey) {
                e.preventDefault();
                post('prevTab');
                return;
            }
        }
        if (!e.ctrlKey) return;
        switch (e.key.toLowerCase()) {
            case 't':
                e.preventDefault();
                if (!e.shiftKey) post('newTab');
                break;
            case 'f': case 'F':
                if (e.altKey) {
                    if (e.shiftKey) post('fullFrameless');
                    else post('toggleFrameless');
                }
                break;
                break;
            case 'b':
                if (e.shiftKey) {
                    e.preventDefault();
                    post('toggleChromeVisibility');
                }
                break;
            case 'w': e.preventDefault(); post('closeTab'); break;
            case 'l': e.preventDefault(); post('focusAddressBar'); break;
            case 'r': e.preventDefault(); post('reload'); break;
            case 'h': e.preventDefault(); post('home'); break;
            case 'arrowleft': e.preventDefault(); post('goBack'); break;
            case 'arrowright': e.preventDefault(); post('goForward'); break;
            case '[': e.preventDefault(); post('goBack'); break;
            case ']': e.preventDefault(); post('goForward'); break;
        }
    });

    document.addEventListener('mousedown', (e) => {
        if (e.button !== 0) return;
        if (e.target.closest('button, input, .tab, #addressBarWrapper, #sidebar, #windowControls')) return;
        post('beginWindowDrag');
    });
}

// Helper: send JSON message to C# backend
function post(type, data = {}) {
    window.chrome.webview.postMessage({ type, ...data });
}

// Handle state broadcast from C#
function onMessage(event) {
    const data = event.data;
    if (data.type === 'state') {
        currentState = data;
        renderTabs(data.tabs);
        renderToolbar(data);
    }
}

function renderTabs(tabs) {
    const currentTabIds = tabs.map(tab => tab.id);
    const removedTabIds = previousTabIds.filter(id => !currentTabIds.includes(id));
    
    // Animate removing tabs
    removedTabIds.forEach(id => {
        const tabEl = document.querySelector(`[data-tab-id="${id}"]`);
        if (tabEl) {
            tabEl.classList.add('tab-closing');
            setTimeout(() => tabEl.remove(), 120);
        }
    });

    tabs.forEach(tab => {
        let el = document.querySelector(`[data-tab-id="${tab.id}"]`);
        
        if (!el) {
            // New tab - create with animation
            el = document.createElement('div');
            el.className = 'tab tab-new' + (tab.active ? ' active' : '');
            el.draggable = true;
            el.dataset.tabId = tab.id;
            
            // Remove animation class after animation completes
            el.addEventListener('animationend', () => {
                el.classList.remove('tab-new');
            });
        } else {
            // Update existing tab
            el.className = 'tab' + (tab.active ? ' active' : '');
        }
        
        const faviconHtml = tab.faviconUrl
            ? `<img class="tab-favicon" src="${escapeHtml(tab.faviconUrl)}" alt="" onerror="this.replaceWith(Object.assign(document.createElement('span'), { className: 'tab-dot' }))" />`
            : '<span class="tab-dot"></span>';
        el.innerHTML = `
            ${faviconHtml}
            <span class="tab-title">${escapeHtml(tab.title)}</span>
            <button class="tab-close" title="Close tab">×</button>
        `;

        // Click tab body to switch; click × to close
        el.addEventListener('click', (e) => {
            if (e.target.classList.contains('tab-close')) {
                e.stopPropagation();
                post('closeTab', { tabId: tab.id });
            } else {
                post('switchTab', { tabId: tab.id });
            }
        });

        // Middle-click anywhere on a tab to close it
        el.addEventListener('mousedown', (e) => {
            if (e.button === 1) { // middle mouse button
                e.preventDefault();
                post('closeTab', { tabId: tab.id });
            }
        });

        el.addEventListener('dragstart', (e) => {
            e.dataTransfer.setData('text/plain', String(tab.id));
            e.dataTransfer.effectAllowed = 'move';
        });

        el.addEventListener('dragover', (e) => {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
        });

        el.addEventListener('drop', (e) => {
            e.preventDefault();
            const fromId = Number(e.dataTransfer.getData('text/plain'));
            if (!Number.isNaN(fromId) && fromId !== tab.id) {
                post('reorderTab', { fromId, toId: tab.id });
            }
        });

        if (!el.parentNode) {
            tabsBar.appendChild(el);
        }
    });
    
    previousTabIds = currentTabIds;
}

function renderToolbar(state) {
    addressBar.value = state.url || '';
    btnBack.disabled = !state.canGoBack;
    btnForward.disabled = !state.canGoForward;
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

init();
