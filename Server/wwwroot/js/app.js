/* ═════════════════════════════════════════════════════════════════════════
   AndroidStorageSpy — app.js (Updated)
   Features: Progressive Chunked Grid Rendering (0 lag for 10,000+ files),
   SessionState Persistence, Frame/Quality Card Overrides, Async Size Display,
   Search Engine with Cancel, Disk Cache Integration, Fixed Download Bar.
   ═════════════════════════════════════════════════════════════════════════ */

'use strict';

let currentDevice = null;
const devicePlatforms = {};

// One-for-all resilience: every fetch on this page automatically retries once
// on network-level failures (tunnel blips, brief reconnects) before erroring.
const _origFetch = window.fetch.bind(window);
window.fetch = async function (input, init) {
    try { return await _origFetch(input, init); }
    catch (e) { await new Promise(r => setTimeout(r, 450)); return _origFetch(input, init); }
};
let currentPath   = '';
let selectedPaths = new Set();
let isSearchActive = false;
let searchAbortController = null;

// ── 🐛 Device Debug Console ─────────────────────────────────────────────────
const debugLogs = [];
const MAX_DEBUG_LOGS = 500;

// Debug lines are stamped by the source as [ERROR] / [SUCCESS] / [MESSAGE].
// Map those stamps to CSS color classes (red / green / light-grey).
function debugLineClass(text) {
    if (text.startsWith('[ERROR]'))   return 'debug-error';
    if (text.startsWith('[SUCCESS]')) return 'debug-success';
    return 'debug-message';
}

let dbgUserAtBottom = true;
function renderDebugLogs() {
    const list = document.getElementById('debug-log-list');
    if (!list) return;
    // Use sticky flag updated on scroll; fallback to geometry check for first render
    const atBottom = dbgUserAtBottom && (list.scrollHeight - list.scrollTop - list.clientHeight < 12);
    const prevTop = list.scrollTop;
    const prevHeight = list.scrollHeight;
    list.innerHTML = debugLogs.map(l =>
        `<div class="debug-log-line ${debugLineClass(l.text)}"><span class="debug-log-time">${l.time}</span> ${escHtml(l.text)}</div>`
    ).join('');
    if (atBottom) list.scrollTop = list.scrollHeight;
    else {
        // Keep visual position stable when new logs arrive while scrolled up
        const delta = list.scrollHeight - prevHeight;
        if (delta) list.scrollTop = prevTop + delta;
    }
}

function toggleDebugPanel() {
    document.getElementById('debug-panel')?.classList.toggle('hidden');
}

async function clearDebugLogs() {
    // Separated → clear only current service; All → clear all services for device; Server → clear server logs
    // Always clear local view; server clear is best-effort (separated with no service selected: only local)
    try {
        if (dbgSelectedDevice === '__server__') {
            await fetch('/api/debug/clear?device=__server__', { method: 'POST' });
        } else if (dbgMode === 'separated' && dbgSelectedService) {
            await fetch('/api/debug/clear?device=' + encodeURIComponent(dbgSelectedDevice) + '&service=' + encodeURIComponent(dbgSelectedService), { method: 'POST' });
        } else if (dbgMode === 'all' && dbgSelectedDevice) {
            await fetch('/api/debug/clear?device=' + encodeURIComponent(dbgSelectedDevice), { method: 'POST' });
        }
    } catch {}
    debugLogs.length = 0;
    renderDebugLogs();
}

function showDebugPanel() {
    document.getElementById('debug-panel')?.classList.remove('hidden');
}

// ── Debug state ──────────────────────────────────────────────────────────────
let dbgTargets = { server: { name: 'Server' }, devices: [] };
let dbgSelectedDevice = null;      // device name, or '__server__'
let dbgSelectedService = null;     // service name (Separated mode)
let dbgMode = 'separated';         // 'separated' (default) | 'all'

async function dbgFetchTargets() {
    try {
        const r = await fetch('/api/debug/targets');
        const d = await r.json();
        if (d.success) dbgTargets = { server: d.server, devices: d.devices || [] };
    } catch (e) {}
    // Auto-select: first connected device; on its disconnect, another available one.
    const names = dbgTargets.devices.map(x => x.name);
    if (!dbgSelectedDevice || (!names.includes(dbgSelectedDevice) && dbgSelectedDevice !== '__server__')) {
        dbgSelectedDevice = names.length ? names[0] : '__server__';
        dbgSelectedService = null;
    }
    dbgUpdateHeaderControls();
}

function dbgCurrentDeviceName() {
    return dbgSelectedDevice === '__server__' ? dbgTargets.server.name : (dbgSelectedDevice || '');
}

function dbgServicesForSelected() {
    if (dbgSelectedDevice === '__server__') return ['console'];
    const dev = dbgTargets.devices.find(x => x.name === dbgSelectedDevice);
    return dev ? (dev.services || []) : [];
}

function dbgUpdateHeaderControls() {
    const devBtn = document.getElementById('dbg-device-btn');
    const svcBtn = document.getElementById('dbg-service-btn');
    if (devBtn) {
        const isSrv = dbgSelectedDevice === '__server__';
        devBtn.textContent = (isSrv ? '🖥 ' : '📱 ') + dbgCurrentDeviceName();
    }
    if (svcBtn) {
        const showSvc = dbgMode === 'separated';
        svcBtn.style.display = showSvc ? '' : 'none';
        svcBtn.textContent = dbgSelectedService ? '🧩 ' + dbgSelectedService : '🧩 Services ▾';
    }
}

// ── Device picker popup (1.1 + 1.4): Server Device section, then Connected Devices
function dbgToggleDevicePopup() {
    const pop = document.getElementById('dbg-device-popup');
    if (!pop) return;
    if (pop.style.display === 'flex') { pop.style.display = 'none'; return; }
    pop.style.display = 'flex';
    dbgRenderDevicePopup();
    // live refresh of the list while open
    clearInterval(window._dbgPopTimer);
    window._dbgPopTimer = setInterval(() => {
        if (pop.style.display !== 'flex') { clearInterval(window._dbgPopTimer); return; }
        dbgFetchTargets().then(dbgRenderDevicePopup);
    }, 3000);
}

function dbgRenderDevicePopup() {
    const pop = document.getElementById('dbg-device-popup');
    if (!pop) return;
    let html = '<div class="dbg-pop-label">SERVER DEVICE</div>';
    html += `<div class="dbg-pop-item ${dbgSelectedDevice === '__server__' ? 'active' : ''}" onclick="dbgPickDevice('__server__')">🖥 ${escHtml(dbgTargets.server?.name || 'Server')}</div>`;
    html += '<div class="dbg-pop-divider"></div>';
    html += '<div class="dbg-pop-label">CONNECTED DEVICES</div>';
    if (!dbgTargets.devices.length) html += '<div class="dbg-pop-empty">(no connected devices)</div>';
    for (const dev of dbgTargets.devices) {
        html += `<div class="dbg-pop-item ${dbgSelectedDevice === dev.name ? 'active' : ''}" onclick="dbgPickDevice('${escHtml(dev.name).replace(/'/g, "\\'")}')">📱 ${escHtml(dev.name)} <span style="opacity:.5;font-size:.75rem;">${escHtml(dev.platform || '')}</span></div>`;
    }
    pop.innerHTML = html;
}

function dbgPickDevice(name) {
    dbgSelectedDevice = name;
    dbgSelectedService = null;
    document.getElementById('dbg-device-popup').style.display = 'none';
    dbgUpdateHeaderControls();
    debugLogs.length = 0;
    renderDebugLogs();
}

// ── Service picker popup (1.2): classes/.exe that announced for this device
function dbgToggleServicePopup() {
    const pop = document.getElementById('dbg-service-popup');
    if (!pop) return;
    if (pop.style.display === 'flex') { pop.style.display = 'none'; return; }
    pop.style.display = 'flex';
    dbgRenderServicePopup();
    clearInterval(window._dbgSvcTimer);
    window._dbgSvcTimer = setInterval(async () => {
        if (pop.style.display !== 'flex') { clearInterval(window._dbgSvcTimer); return; }
        await dbgFetchTargets();          // picks up newly-announced services
        dbgRenderServicePopup();
    }, 2000);
}

function dbgRenderServicePopup() {
    const pop = document.getElementById('dbg-service-popup');
    if (!pop) return;
    const svcs = dbgServicesForSelected();
    let html = `<div class="dbg-pop-label">SERVICES — ${escHtml(dbgCurrentDeviceName())}</div>`;
    if (!svcs.length) html += '<div class="dbg-pop-empty">(no services announced yet…)</div>';
    for (const s of svcs) {
        html += `<div class="dbg-pop-item ${dbgSelectedService === s ? 'active' : ''}" onclick="dbgPickService('${escHtml(s).replace(/'/g, "\\'")}')">🧩 ${escHtml(s)}</div>`;
    }
    pop.innerHTML = html;
}

function dbgPickService(s) {
    dbgSelectedService = s;
    document.getElementById('dbg-service-popup').style.display = 'none';
    dbgUpdateHeaderControls();
    debugLogs.length = 0;
    renderDebugLogs();
}

function dbgSetMode(mode) {
    dbgMode = mode;
    document.getElementById('dbg-filter-sel').value = mode;
    dbgUpdateHeaderControls();
    debugLogs.length = 0;
    renderDebugLogs();
}

// ── Polling loop (replaces the old global-only feed) ────────────────────────
async function dbgPollOnce() {
    try {
        let url;
        if (dbgMode === 'all') {
            if (dbgSelectedDevice === '__server__') url = '/api/debug/server-logs?limit=400';
            else url = '/api/debug/all?device=' + encodeURIComponent(dbgSelectedDevice) + '&limit=500';
        } else {
            if (dbgSelectedDevice === '__server__') url = '/api/debug/server-logs?limit=400';
            else if (dbgSelectedService) url = '/api/debug/service-logs?device=' + encodeURIComponent(dbgSelectedDevice) + '&service=' + encodeURIComponent(dbgSelectedService) + '&limit=300';
            else url = null;
        }
        if (!url) return;
        const r = await fetch(url);
        const d = await r.json();
        // endpoints return bare arrays; tolerate both {success,logs} and bare []
        const arr = Array.isArray(d) ? d : (Array.isArray(d.logs) ? d.logs : null);
        if (!arr) return;

        // Replace view with latest snapshot (queues cap server-side).
        debugLogs.length = 0;
        for (const l of arr.slice(-MAX_DEBUG_LOGS)) {
            debugLogs.push({ time: new Date(l.time).toLocaleTimeString(), text: l.text });
        }
        renderDebugLogs();
    } catch (e) {}
}

// Create debug panel DOM + start polling for logs
document.addEventListener('DOMContentLoaded', () => {
    // Auto-refresh the online device sidebar whenever the PC registers a
    // device connect/disconnect (Server-Sent Events from /api/device-events).
    const deviceList = document.getElementById('device-list');
    if (deviceList && deviceList.dataset.mode === 'online') {
        const deviceEvents = new EventSource('/api/device-events');
        deviceEvents.addEventListener('devices', () => refreshDeviceList());
    }

    const panel = document.createElement('div');
    panel.id = 'debug-panel';
    panel.className = 'debug-panel hidden';
    panel.innerHTML =
        '<div class="debug-header">' +
            '<span>🐛 Device Debug</span>' +
            '<button id="dbg-device-btn" class="btn btn-ghost btn-xs" onclick="dbgToggleDevicePopup()">📱 …</button>' +
            '<button id="dbg-service-btn" class="btn btn-ghost btn-xs" onclick="dbgToggleServicePopup()">🧩 Services ▾</button>' +
            '<div style="display:flex;align-items:center;gap:6px;margin-left:auto;">' +
                '<button class="btn btn-ghost btn-xs" onclick="clearDebugLogs()">Clear</button>' +
                '<select id="dbg-filter-sel" class="btn btn-ghost btn-xs" style="margin-left:10px;" onchange="dbgSetMode(this.value)">' +
                    '<option value="separated">Separate each Service Logs</option>' +
                    '<option value="all">Show all Services debugs</option>' +
                '</select>' +
                '<button class="btn btn-ghost btn-xs" onclick="toggleDebugPanel()">✕</button>' +
            '</div>' +
        '</div>' +
        '<div id="dbg-device-popup" class="dbg-popup"></div>' +
        '<div id="dbg-service-popup" class="dbg-popup"></div>' +
        '<div id="debug-log-list" class="debug-log-list"></div>';
    document.body.appendChild(panel);

    dbgFetchTargets();
    setInterval(dbgPollOnce, 2000);
    // track whether user is at bottom to make auto-scroll sticky
    setTimeout(() => {
        const list = document.getElementById('debug-log-list');
        if (!list) return;
        list.addEventListener('scroll', () => {
            dbgUserAtBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 12;
        });
        // initial state: at bottom
        dbgUserAtBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 12;
    }, 500);
});

// ── File Filters ────────────────────────────────────────────────────────────
let filterState = { image: true, video: true, date: 'all' };
let _lastBrowseData = null;  // cached for re-filtering without re-fetching

function toggleFilter(type) {
    if (type === 'all') {
        // All clicked: disable both image and video
        filterState.image = false;
        filterState.video = false;
        document.getElementById('filter-img-btn').classList.remove('active');
        document.getElementById('filter-vid-btn').classList.remove('active');
        document.getElementById('filter-all-btn').classList.add('active');
    } else {
        // Image or Video clicked: toggle it, disable All
        filterState[type] = !filterState[type];
        document.getElementById(type === 'image' ? 'filter-img-btn' : 'filter-vid-btn').classList.toggle('active', filterState[type]);
        document.getElementById('filter-all-btn').classList.remove('active');
        // If both are now off, auto-enable All
        if (!filterState.image && !filterState.video) {
            document.getElementById('filter-all-btn').classList.add('active');
        }
    }
    applyFilters();
}

function applyFilters() {
    filterState.date = document.getElementById('filter-date')?.value || 'all';
    // Re-fetch from phone with new filters
    if (currentDevice) browsePath(currentPath || '');
    else if (offlineDeviceId) navigateOfflinePath(offlineCurrentPath || '');
}

function showFilterBar() {
    document.getElementById('filter-bar')?.classList.remove('hidden');
}
function hideFilterBar() {
    document.getElementById('filter-bar')?.classList.add('hidden');
}

function matchesFilter(file) {
    const name = (file.name || '').toLowerCase();
    const isImg = /\.(jpe?g|png|webp|bmp|gif|heic)$/i.test(name);
    const isVid = /\.(mp4|mkv|mov|avi|webm|3gp|m4v)$/i.test(name);

    // Type filter: when the user has narrowed to images and/or videos, only those
    // types pass (All = both off = everything passes).
    if (filterState.image || filterState.video) {
        const typeOk = (filterState.image && isImg) || (filterState.video && isVid);
        if (!typeOk) return false;
    }

    // Date filter — applied regardless of the type filter.
    if (filterState.date !== 'all' && file.lastWriteTimeUtc) {
        const fileDate = new Date(file.lastWriteTimeUtc);
        const now = new Date();
        const days = { today: 1, '7days': 7, '30days': 30 }[filterState.date] || 0;
        if (days > 0 && (now - fileDate) > days * 86400000) return false;
    }

    return true;
}

function applyFileFilters(data) {
    if (!data || !data.files) return data;
    const filtered = data.files.filter(matchesFilter);
    return { ...data, files: filtered, totalCount: filtered.length };
}

// ── Search Functions ────────────────────────────────────────────────────────
function showSearchBar() {
    document.querySelector('.search-container')?.classList.add('visible');
}
function hideSearchBar() {
    document.querySelector('.search-container')?.classList.remove('visible');
}

async function executeSearch() {
    const query = document.getElementById('searchInput')?.value.trim();
    if (!query) { showToast('Enter a search query', 'warn'); return; }
    if (!currentDevice && !offlineDeviceId) { showToast('Select a device first', 'warn'); return; }

    if (isSearchActive) { stopActiveSearch(); await new Promise(r => setTimeout(r, 200)); }

        const scope = document.getElementById('searchScope')?.value || 'folder';
        const startPath = scope === 'folder' ? (currentPath || offlineCurrentPath || '') : '';
        const recursive = scope !== 'folder';
        const isOfflineSearch = !!offlineDeviceId;

    isSearchActive = true;
    searchAbortController = new AbortController();
    const stopBtn = document.getElementById('stopSearchBtn');
    const searchBtn = document.querySelector('.search-btn');
    if (stopBtn) stopBtn.classList.remove('hidden');
    if (searchBtn) searchBtn.disabled = true;

    // Show search results panel
    let panel = document.getElementById('search-results-panel');
    if (!panel) {
        panel = document.createElement('div');
        panel.id = 'search-results-panel';
        panel.className = 'search-results-panel';
        panel.innerHTML = `
            <div class="search-results-header">
                <span class="search-results-title">🔍 Results: "<span id="search-query-text"></span>"</span>
                <button class="btn btn-ghost btn-xs" onclick="closeSearchResults()">✕</button>
            </div>
            <div id="search-results-list" class="search-results-list"></div>`;
        document.querySelector('.app-shell')?.appendChild(panel);
    }
    panel.classList.add('open');
    document.getElementById('search-query-text').textContent = query;
    document.getElementById('search-results-list').innerHTML = '<div class="search-loading">⏳ Searching...</div>';

    try {
        let url;
        if (isOfflineSearch) {
            const device = offlineDeviceId;
            url = `/api/cache/search/${encodeURIComponent(device)}?query=${encodeURIComponent(query)}&startPath=${encodeURIComponent(startPath)}&recursive=${recursive}&maxResults=500`;
        } else {
            const device = currentDevice;
            url = `/api/search/${encodeURIComponent(device)}?query=${encodeURIComponent(query)}&startPath=${encodeURIComponent(startPath)}&recursive=${recursive}&maxResults=500`;
        }
        const r = await fetch(url, { signal: searchAbortController.signal });
        if (!r.ok) throw new Error(await r.text());

        const data = await r.json();
        renderSearchResults(data, query);
    } catch (e) {
        if (e.name === 'AbortError') {
            document.getElementById('search-results-list').innerHTML = '<div class="search-empty">Search cancelled</div>';
        } else {
            document.getElementById('search-results-list').innerHTML = `<div class="search-empty">Error: ${e.message}</div>`;
        }
    } finally {
        isSearchActive = false;
        searchAbortController = null;
        if (stopBtn) stopBtn.classList.add('hidden');
        if (searchBtn) searchBtn.disabled = false;
    }
}

function stopActiveSearch() {
    if (searchAbortController) {
        searchAbortController.abort();
        searchAbortController = null;
    }
    // Also tell the phone to stop (online only — offline search is local cache)
    if (isSearchActive && currentDevice) {
        fetch(`/api/search/stop/${encodeURIComponent(currentDevice)}`, { method: 'POST' }).catch(() => {});
    }
    isSearchActive = false;
    const stopBtn = document.getElementById('stopSearchBtn');
    if (stopBtn) stopBtn.classList.add('hidden');
    const searchBtn = document.querySelector('.search-btn');
    if (searchBtn) searchBtn.disabled = false;
}

function closeSearchResults() {
    document.getElementById('search-results-panel')?.classList.remove('open');
}

function renderSearchResults(data, query) {
    const list = document.getElementById('search-results-list');
    if (!list) return;

    let results = [];
    // Handle different response formats from phone
    if (Array.isArray(data)) {
        results = data;
    } else if (data.results && Array.isArray(data.results)) {
        results = data.results;
    } else if (data.files && Array.isArray(data.files)) {
        results = data.files;
    }

    if (results.length === 0) {
        list.innerHTML = `<div class="search-empty">No files match "${query}"</div>`;
        return;
    }

    const isOffline = !!offlineDeviceId;
    list.innerHTML = results.map((f, i) => {
        const name = f.name || f.fileName || f.Name || `result-${i}`;
        const path = f.path || f.relativePath || f.RelativePath || '';
        const size = f.length || f.size || f.Size || 0;
        const isDir = f.isDirectory || f.IsDirectory || false;
        const icon = isDir ? '📁' : '📄';
        const sizeStr = size ? formatBytes(size) : '';
        const escapedPath = escAttr(path);
        const escapedName = escHtml(name);
        const dirClick = isOffline ? `navigateOfflinePath('${escapedPath}')` : `browsePath('${escapedPath}')`;

        return `<div class="search-result-item" onclick="${isDir ? dirClick : `navigateToFile('${escapedPath}')`}" title="${escapedPath}">
            <span class="search-result-icon">${icon}</span>
            <span class="search-result-name">${escapedName}</span>
            <span class="search-result-size">${sizeStr}</span>
            <span class="search-result-path" title="${escapedPath}">${escHtml(path)}</span>
        </div>`;
    }).join('');

    // Show total count
    const title = document.getElementById('search-query-text');
    if (title) title.textContent = `${query} (${results.length} files)`;
}

function navigateToFile(filePath) {
    // Navigate to parent folder and scroll to file
    const parentPath = filePath.substring(0, filePath.lastIndexOf('/'));
    if (parentPath) {
        if (offlineDeviceId) {
            navigateOfflinePath(parentPath);
        } else {
            browsePath(parentPath).then(() => {
                setTimeout(() => scrollToFile(filePath), 500);
            });
        }
    }
}

const thumbControllers = new WeakMap();

// ── Thumbnail fetch concurrency limiter ───────────────────────────────────
// Scrolling through a folder with thousands of files fires hundreds of
// simultaneous /api/thumb requests. The browser only allows ~6 connections per
// host, so the rest pile up unheard while every visible card spins forever.
// Cap the number of in-flight thumbnail loads and serve them FIFO so the grid
// stabilizes quickly. A card that scrolls out of view before its queued slot
// runs is skipped (its AbortController was fired), not fetched.
const ThumbFetchPoolSize = 8;
let thumbFetchActive = 0;
const thumbFetchQueue = [];

function enqueueThumbFetch(controller, task) {
    return new Promise((resolve, reject) => {
        thumbFetchQueue.push({ controller, task, resolve, reject });
        pumpThumbFetches();
    });
}

function pumpThumbFetches() {
    while (thumbFetchActive < ThumbFetchPoolSize && thumbFetchQueue.length > 0) {
        const next = thumbFetchQueue.shift();
        if (next == null) break;
        // Card scrolls out of view while queued → its controller was aborted, drop it.
        if (next.controller?.signal?.aborted) {
            next.reject(new DOMException('Aborted', 'AbortError'));
            continue;
        }
        thumbFetchActive++;
        Promise.resolve()
            .then(() => next.task())
            .then(next.resolve, next.reject)
            .finally(() => {
                thumbFetchActive--;
                pumpThumbFetches();
            });
    }
}

// ── IntersectionObserver for lazy thumbnails ───────────────────────────────
// Cards are NOT handled synchronously inside the observer callback: a fast fling
// would otherwise swap placeholder→spinner DOM for dozens of cards in a single
// frame and drop frames. Instead, visible cards are queued and processed from a
// rAF loop in small batches. Cards that leave the viewport are cancelled
// immediately (cheap — just an abort + flag clear).
const thumbEnterQueue = new Set();
let thumbBatchFrame = 0;
const ThumbBatchPerFrame = 12;

const thumbObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
        const card = entry.target;
        if (entry.isIntersecting && !card.dataset.thumbState) {
            thumbEnterQueue.add(card);
        } else if (!entry.isIntersecting) {
            thumbEnterQueue.delete(card);
            cancelThumbnail(card);
            stopOfflineFrameCycle(card);
        }
    }
    if (thumbEnterQueue.size > 0 && !thumbBatchFrame) {
        thumbBatchFrame = requestAnimationFrame(processThumbBatch);
    }
}, {
    rootMargin: '100px',
    threshold: 0.01
});

function processThumbBatch() {
    thumbBatchFrame = 0;

    // Take a fixed batch from the queue (Set preserves insertion order, so cards
    // that entered earlier are handled first). No getBoundingClientRect here —
    // layout reads inside a scroll handler are a jank source of their own.
    const batch = [];
    for (const card of thumbEnterQueue) {
        if (batch.length >= ThumbBatchPerFrame) break;
        batch.push(card);
        thumbEnterQueue.delete(card);
    }

    for (const card of batch) {
        if (card.isConnected && card.dataset.thumbState !== 'loaded' && card.dataset.thumbState !== 'loading') {
            // Offline video cards resolve their cached frames before the first load.
            if (card.dataset.framesPending === '1') {
                loadOfflineVideoFrames(card);
            } else {
                loadThumbnail(card);
            }
        }
    }

    if (thumbEnterQueue.size > 0) {
        thumbBatchFrame = requestAnimationFrame(processThumbBatch);
    }
}

// ── Server Status Poll (Quiet, no console spam) ────────────────────────────
(async function pollStatus() {
    const dot  = document.getElementById('status-dot');
    const text = document.getElementById('status-text');
    try {
        const r = await fetch('/status', { signal: AbortSignal.timeout(4000) });
        if (r.ok) {
            if (dot) dot.className  = 'status-dot online';
            if (text) text.textContent = 'Server online';
        } else { throw new Error(); }
    } catch {
        if (dot) dot.className  = 'status-dot offline';
        if (text) text.textContent = 'Server offline';
    }
    setTimeout(pollStatus, 10_000);
})();

// ── State Persistence (sessionStorage) ─────────────────────────────────────
function saveState() {
    if (!currentDevice) return;
    const state = {
        device: currentDevice,
        path: currentPath,
        startIndex: document.getElementById('startIndex')?.value || '0',
        fileLimit: document.getElementById('fileLimit')?.value || '0',
        scrollTop: document.getElementById('file-grid')?.scrollTop || 0
    };
    sessionStorage.setItem('AndroidStorageSpy_State', JSON.stringify(state));
}

function restoreStateAndLoad() {
    const savedJson = sessionStorage.getItem('AndroidStorageSpy_State');
    if (savedJson) {
        try {
            const state = JSON.parse(savedJson);
            if (state.device) {
                if (document.getElementById('startIndex')) document.getElementById('startIndex').value = state.startIndex || '0';
                if (document.getElementById('fileLimit')) document.getElementById('fileLimit').value = state.fileLimit || '0';

                const item = document.querySelector(`[data-device="${encodeURIComponent(state.device)}"]`);
                if (item) item.classList.add('active');

                currentDevice = state.device;
                browsePath(state.path || '', state.scrollTop || 0);
                loadFrameOverrideDefault(state.device);
                return;
            }
        } catch { }
    }

    // Hash fallback
    const hash = location.hash.replace('#', '');
    if (hash) {
        const item = document.querySelector(`[data-device="${decodeURIComponent(hash)}"]`);
        if (item) item.click();
    }
}

// ── Device Selection ───────────────────────────────────────────────────────
function selectDevice(el, name) {
    document.querySelectorAll('.device-item').forEach(i => i.classList.remove('active'));
    el.classList.add('active');

    currentDevice = name;
    currentPath   = '';
    selectedPaths.clear();
    updateSelectionBar();

    location.hash = encodeURIComponent(name);
    saveState();
    showSearchBar();
    showFeatures();
    loadFrameOverrideDefault(name);
}

function showFeatures() {
    document.getElementById('browser-state')?.classList.add('hidden');
    document.getElementById('file-grid')?.replaceChildren();
    document.getElementById('features-panel')?.classList.remove('hidden');
    document.getElementById('filter-bar')?.classList.add('hidden');
    document.getElementById('shell-panel')?.classList.add('hidden');
    document.getElementById('database-panel')?.classList.add('hidden');
    // CMD/PowerShell + Database cards are Windows-exclusive.
    const isWin = ((devicePlatforms[currentDevice] || '') === 'windows');
    const shellCard = document.getElementById('feature-card-shell');
    if (shellCard) shellCard.style.display = isWin ? '' : 'none';
    const dbCard = document.getElementById('feature-card-database');
    if (dbCard) dbCard.style.display = isWin ? '' : 'none';
}

function openFeature(feature) {
    if (feature === 'shell') {
        if ((devicePlatforms[currentDevice] || '') !== 'windows') {
            showToast('CMD / PowerShell is only available for Windows devices.', 'warn');
            return;
        }
        document.getElementById('features-panel')?.classList.add('hidden');
        document.getElementById('file-grid')?.replaceChildren();
        document.getElementById('filter-bar')?.classList.add('hidden');
        document.getElementById('browser-state')?.classList.add('hidden');
        document.getElementById('shell-panel')?.classList.remove('hidden');
        document.getElementById('database-panel')?.classList.add('hidden');
        document.getElementById('idx-shell-dev').textContent = '→ ' + currentDevice;
        renderIndexShellHistory();
        document.getElementById('idx-shell-input')?.focus();
        return;
    }
    if (feature === 'database') {
        if ((devicePlatforms[currentDevice] || '') !== 'windows') {
            showToast('Database is only available for Windows devices.', 'warn');
            return;
        }
        document.getElementById('features-panel')?.classList.add('hidden');
        document.getElementById('file-grid')?.replaceChildren();
        document.getElementById('filter-bar')?.classList.add('hidden');
        document.getElementById('browser-state')?.classList.add('hidden');
        document.getElementById('shell-panel')?.classList.add('hidden');
        document.getElementById('database-panel')?.classList.remove('hidden');
        document.getElementById('idx-db-dev').textContent = '→ ' + currentDevice;
        document.getElementById('idx-db-browse')?.classList.add('hidden');
        refreshDatabase();
        return;
    }
    if (feature === 'browser') {
        document.getElementById('features-panel')?.classList.add('hidden');
        browsePath('');
    } else if (feature === 'tunnel') {
        // Route the tunnel through the currently selected phone, then open the Tunnel page.
        fetch('/api/tunnel/select', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ device: currentDevice })
        }).catch(() => {});
        window.open('/Tunnel', '_blank');
    }
}

function renderDeviceItems(devices) {
    const list = document.getElementById('device-list');
    if (!list) return;
    let html = '';
    for (const dev of devices) {
        const name = dev.deviceName;
        devicePlatforms[name] = (dev.platform || '').toLowerCase();
        const isWin = (dev.platform || '').toLowerCase() === 'windows';
        const iconUrl = isWin ? '/Images/windows-icon.png' : '/Images/R.png';
        html += `
            <li class="device-item${name === currentDevice ? ' active' : ''}" data-device="${escHtml(name)}">
                <span class="device-icon"><img src="${iconUrl}" class="device-icon-img" alt="${isWin ? 'Windows' : 'Android'}" style="width:20px;height:20px;vertical-align:middle;object-fit:contain;" /></span>
                <span class="device-name">${escHtml(name)}</span>
                ${renderLinkTag(dev)}
            </li>`;
    }
    list.innerHTML = html;
    list.querySelectorAll('.device-item').forEach(li => {
        li.addEventListener('click', () => selectDevice(li, li.dataset.device));
    });
}

async function refreshDeviceList() {
    try {
        const r = await fetch('/api/devices');
        const devices = await r.json();
        if (!devices.length) {
            const list = document.getElementById('device-list');
            if (list) list.innerHTML = '<li class="device-empty">No devices connected</li>';
            return;
        }
        renderDeviceItems(devices);
    } catch {
        showToast('Failed to refresh device list.', 'error');
    }
}

// Render the P2P/Domain availability tag. Both parts are shown in one pill:
// P2P (left) is green when available, Domain (right) is orange when available;
// a part is dark-grey when unavailable. The tag only ever reflects confirmed
// link state — never an optimistic "connecting".
function renderLinkTag(dev) {
    const p2p    = !!dev.p2p;
    const domain = !!dev.domain;
    return `<span class="link-tag" title="P2P: ${p2p ? 'direct connection active' : 'unavailable'} · Domain: ${domain ? 'relay connected' : 'unavailable'}">
        <span class="link-part p2p-part ${p2p ? 'up' : 'down'}">P2P</span><span class="link-sep">/</span>
        <span class="link-part domain-part ${domain ? 'up' : 'down'}">Domain</span>
    </span>`;
}

// ── Directory Browsing ─────────────────────────────────────────────────────
async function browsePath(path, restoreScrollTop = 0) {
    if (!currentDevice) return;

    currentPath = path;
    selectedPaths.clear();
    updateSelectionBar();
    saveState();

    setBrowserState('⏳ Loading…');

    const startIndex = parseInt(document.getElementById('startIndex')?.value || '0', 10) || 0;
    const fileLimit  = parseInt(document.getElementById('fileLimit')?.value || '0', 10) || 0;

    const url = `/api/browse/${encodeURIComponent(currentDevice)}` +
        `?path=${encodeURIComponent(path)}&startIndex=${startIndex}&fileLimit=${fileLimit}` +
        `&imageOnly=${filterState.image}&videoOnly=${filterState.video}&sinceDays=${filterState.date === 'all' ? 0 : filterState.date === 'today' ? 1 : filterState.date === '7days' ? 7 : 30}`;

    console.log('Browse url', url, 'filterState', JSON.stringify(filterState), 'startIndex', startIndex, 'fileLimit', fileLimit);
    try {
        const r = await fetch(url);
        if (!r.ok) {
            setBrowserState(`❌ Error ${r.status}: ${await r.text()}`);
            return;
        }
        const data = await r.json();
        console.log('Browse data for', path, 'dirs', data.directories?.length, 'files', data.files?.length, 'total', data.totalFileCount, data);
        hideBrowserState();
        renderBreadcrumb(path);
        showFilterBar();

        // Render Grid with Progressive Chunking
        renderGridProgressive(data, restoreScrollTop);

        // Save folder metadata to disk cache for offline browsing
        saveFolderToCache(path, data);

        if (data.exceededAt != null) {
            const requested = startIndex + fileLimit;
            showToast(
                `⚠️ Only ${data.exceededAt} files available (requested up to ${requested}). Showing ${startIndex}–${data.exceededAt - 1}.`,
                'warn', 5000);
        }
    } catch (e) {
        setBrowserState(`❌ ${e.message}`);
    }
}

/**
 * Save folder metadata to disk cache for offline browsing.
 * Called automatically after each successful browsePath.
 */
async function saveFolderToCache(path, data) {
    if (!currentDevice) return;
    try {
        await fetch(`/api/cache/browse/${encodeURIComponent(currentDevice)}?path=${encodeURIComponent(path)}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                currentNode: data.currentNode,
                children: [...(data.directories || []), ...(data.files || [])]
            })
        });
    } catch {
        // Silently ignore cache save errors — browsing should not be interrupted
    }
}

function applyLimiter() {
    browsePath(currentPath);
}

function resetThumbObserver() {
    thumbObserver.disconnect();
    thumbEnterQueue.clear();
    if (thumbBatchFrame) {
        cancelAnimationFrame(thumbBatchFrame);
        thumbBatchFrame = 0;
    }
}

// ── Progressive Chunked Grid Rendering (0 Lag for 10,000+ Files) ───────────
function renderGridProgressive(data, restoreScrollTop = 0) {
    resetThumbObserver();

    const grid = document.getElementById('file-grid');
    grid.innerHTML = '';

    const allDirs = data.directories || [];
    const allFiles = data.files || [];

    if (!allDirs.length && !allFiles.length) {
        grid.innerHTML = '<div style="color:var(--text2);padding:20px;grid-column:1/-1">This folder is empty.</div>';
        return;
    }

    // Directories render immediately
    for (const dir of allDirs) {
        grid.appendChild(makeDirCard(dir));
    }

    // Files render in progressive chunks of 40 using requestAnimationFrame
    let index = 0;
    const chunkSize = 40;

    function renderChunk() {
        const end = Math.min(index + chunkSize, allFiles.length);
        const fragment = document.createDocumentFragment();

        for (let i = index; i < end; i++) {
            const file = allFiles[i];
            const card = makeFileCard(file);
            fragment.appendChild(card);

            if (isMedia(file.name)) {
                thumbObserver.observe(card);
            }
        }
        grid.appendChild(fragment);
        index = end;

        if (index < allFiles.length) {
            requestAnimationFrame(renderChunk);
        } else if (restoreScrollTop > 0) {
            grid.scrollTop = restoreScrollTop;
        }
    }

    renderChunk();
}

function makeDirCard(dir) {
    const card = document.createElement('div');
    card.className = 'file-card';

    const disabled = !!dir.disabled;
    if (disabled) card.classList.add('entry-disabled');

    // Icon + label per entry kind (Windows roots / shell entries).
    const kinds = {
        disk:         { icon: '💽', sub: 'Disk' },
        usb:          { icon: '🔌', sub: 'USB / Removable' },
        networkdrive: { icon: '🌐', sub: 'Network drive' },
        cdrom:        { icon: '💿', sub: 'Optical drive' },
        ram:          { icon: '⚡', sub: 'RAM disk' },
        mtp:          { icon: '📱', sub: 'Portable device (MTP)' },
        mtpfile:      { icon: '📱', sub: 'Device file' },
        network:      { icon: '🌐', sub: 'Network location' }
    };
    const kind = kinds[dir.entryKind] || null;

    let sizeText = 'Folder';
    if (disabled) {
        sizeText = dir.accessWarning || 'Not accessible';
    } else if (dir.entryKind && dir.totalBytes > 0) {
        const usedPct = Math.max(1, Math.min(99, Math.round(100 * (dir.totalBytes - (dir.freeBytes || 0)) / dir.totalBytes)));
        sizeText = `${kind ? kind.sub + ' · ' : ''}${formatBytes(dir.freeBytes)} free of ${formatBytes(dir.totalBytes)} (${usedPct}% used)`;
    } else if (kind) {
        sizeText = kind.sub;
    }

    const icon = disabled ? '🚫' : (kind ? kind.icon : '📁');

    card.innerHTML = `
        <div class="file-thumb-container">
            <div class="file-thumb-placeholder">${icon}</div>
        </div>
        <div class="file-info">
            <div class="file-name" title="${escHtml(disabled ? (dir.accessWarning || 'Not accessible') : dir.name)}">${escHtml(dir.name)}</div>
            <div class="file-size">${escHtml(sizeText)}</div>
        </div>`;
    if (disabled) {
        card.title = dir.accessWarning || 'This entry is not accessible.';
        card.addEventListener('click', () => showToast(`⚠️ ${dir.name}: ${dir.accessWarning || 'Not accessible on the device.'}`, 'warn', 6000));
    } else {
        card.addEventListener('click', () => browsePath(dir.relativePath));
    }
    return card;
}

function makeFileCard(file) {
    const card = document.createElement('div');
    card.className = 'file-card';
    card.dataset.path = file.relativePath;
    card.dataset.name = file.name;
    card.dataset.length = file.length;

    const isImg   = /\.(jpe?g|png|gif|webp|heic|bmp)$/i.test(file.name);
    const isVideo = /\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(file.name);
    const isMed   = isImg || isVideo;
    const icon    = isImg ? '🖼️' : isVideo ? '🎬' : getFileIcon(file.name);

    const frameDefault = (frameOverrideDefault != null && frameOverrideDevice === currentDevice) ? frameOverrideDefault : null;
    const frameValText = frameDefault != null ? `Value ${frameDefault.toFixed(2)}` : 'Value …';
    const frameSliderVal = frameDefault != null ? frameDefault : 0.5;

    card.innerHTML = `
        <div class="file-thumb-container">
            ${isMed
                ? `<div class="file-thumb-placeholder" data-media="1" data-full-path="${escAttr(file.relativePath)}" data-device-id="${escAttr(currentDevice)}">${icon}</div>`
                : `<div class="file-thumb-placeholder">${icon}</div>`}

            <div class="file-actions">
                <button class="file-action-btn" title="Download" onclick="downloadFile(event,'${escAttr(file.relativePath)}','${escAttr(file.name)}')">⬇️</button>
                <button class="file-action-btn" title="Select"   onclick="toggleSelect(event,this.closest('.file-card'))">☑️</button>
                ${isMed ? `<button class="file-action-btn ai-context-btn" title="Add to AI queue" onclick="addToAIQueue(event,'${escAttr(file.relativePath)}','${escAttr(file.name)}')">🤖</button>` : ''}
            </div>

            ${isVideo ? `
                <div class="card-override-overlay" onclick="event.stopPropagation()"
                     onmouseenter="syncFrameOverrideDisplay(this.closest('.file-card'))">
                    <div class="override-label">
                        <span>Frame: <b class="frame-val-label">${frameValText}</b></span>
                    </div>
                    <input type="range" min="0" max="1" step="0.01" class="override-slider" value="${frameSliderVal}"
                           oninput="updateFrameLabel(this)"
                           onchange="applyFrameOverride(this, '${escAttr(file.relativePath)}')" />
                </div>
            ` : ''}
        </div>
        <div class="file-info">
            <div class="file-name" title="${escHtml(file.name)}">${escHtml(file.name)}</div>
            <div class="file-size" id="size-label-${escAttr(file.relativePath)}">${formatBytes(file.length)}</div>
        </div>`;

    card.addEventListener('click', (e) => {
        if (e.target.closest('.file-actions') || e.target.closest('.card-override-overlay')) return;
        openLightbox(file);
    });

    return card;
}

// ── Frame Override Handling ────────────────────────────────────────────────
let frameOverrideDefault = null;
let frameOverrideDevice = null;

/** Fetch the device's default video thumbnail frame (VideoFrameNormalized) for the hover slider. */
async function loadFrameOverrideDefault(deviceName) {
    try {
        const r = await fetch(`/api/device-config/${encodeURIComponent(deviceName)}`);
        if (!r.ok) return;
        const cfg = await r.json();
        frameOverrideDefault = parseFloat(cfg.videoFrameNormalized ?? cfg.VideoFrameNormalized ?? 0.1);
        frameOverrideDevice = deviceName;
        // Refresh any already-rendered video cards that haven't been overridden
        document.querySelectorAll('.file-card .card-override-overlay').forEach(overlay => {
            const card = overlay.closest('.file-card');
            if (card && !card.dataset.frameOverride) syncFrameOverrideDisplay(card);
        });
    } catch { }
}

/** Position the hover slider at the device default and label it "Value x.xx". */
function syncFrameOverrideDisplay(card) {
    if (!card) return;
    const slider = card.querySelector('.override-slider');
    const label  = card.querySelector('.frame-val-label');
    if (!slider || !label) return;
    if (card.dataset.frameOverride) {
        // Card already has a user override — keep showing that value
        slider.value = card.dataset.frameOverride;
        label.textContent = `Value ${parseFloat(card.dataset.frameOverride).toFixed(2)}`;
        return;
    }
    if (frameOverrideDefault == null || frameOverrideDevice !== currentDevice) return;
    slider.value = frameOverrideDefault;
    label.textContent = `Value ${frameOverrideDefault.toFixed(2)}`;
}

function updateFrameLabel(sliderInput) {
    const val = parseFloat(sliderInput.value);
    const card = sliderInput.closest('.file-card');
    const label = card?.querySelector('.frame-val-label');
    if (label) label.textContent = `Value ${val.toFixed(2)}`;
}

function applyFrameOverride(sliderInput, path) {
    const val = parseFloat(sliderInput.value);
    const card = sliderInput.closest('.file-card');
    const label = card.querySelector('.frame-val-label');
    if (label) label.textContent = `Value ${val.toFixed(2)}`;

    card.dataset.frameOverride = val;
    card.dataset.thumbState = ''; // Force re-fetch

    // Remove existing thumbnail image so it can be replaced with the new one
    const existingImg = card.querySelector('.file-thumb');
    if (existingImg) {
        if (existingImg.src && existingImg.src.startsWith('blob:')) {
            URL.revokeObjectURL(existingImg.src);
        }
        const isVideo = /\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(card.dataset.name);
        existingImg.outerHTML = `<div class="file-thumb-placeholder" data-media="1">${isVideo ? '🎬' : '🖼️'}</div>`;
    }

    showToast(`Requesting video frame ${val.toFixed(2)} for ${card.dataset.name}…`, 'ok', 2500);
    loadThumbnail(card);
}

// ── Lazy Thumbnail Loading with Async Size Update ──────────────────────────
// Returns true when the served thumbnail was native (full-resolution) quality.
async function loadThumbnail(card) {
    if (card.dataset.thumbState === 'loaded' || card.dataset.thumbState === 'loading') return false;
    card.dataset.thumbState = 'loading';

    const path        = card.dataset.path;
    const placeholder = card.querySelector('[data-media]');
    const frameOv     = card.dataset.frameOverride ? parseFloat(card.dataset.frameOverride) : -1.0;
    const qualOv      = card.dataset.qualityOverride ? parseFloat(card.dataset.qualityOverride) : -1.0;

    if (placeholder) {
        placeholder.outerHTML = `<div class="file-thumb-spinner"><div class="spinner"></div></div>`;
    }

    const controller = new AbortController();
    thumbControllers.set(card, controller);

    try {
        // Use data-device attribute (device ID) if set (offline mode), otherwise use currentDevice (device name)
        const deviceId = card.dataset.device || currentDevice;
        // Offline images always serve the best cached thumbnail — never refetch due to the
        // global thumbnail-quality setting.
        const bestCache = !!card.dataset.device
            && !/\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(card.dataset.name || '');
        const url = `/api/thumb/${encodeURIComponent(deviceId)}` +
            `?path=${encodeURIComponent(path)}&frameOverride=${frameOv}&qualityOverride=${qualOv}` +
            (bestCache ? '&bestCache=1' : '');

        // no-store: always ask the server so freshly promoted native thumbnails are picked up.
        // Throttled through the shared pool so a giant folder never floods the server.
        let r;
        try {
            r = await enqueueThumbFetch(controller, () => fetch(url, { signal: controller.signal, cache: 'no-store' }));
        } catch (fetchErr) {
            throw fetchErr;
        }
        if (!r.ok) throw new Error(`HTTP ${r.status}`);

        // Async Size Display Update
        const origSize = r.headers.get('X-Original-Size');
        const procSize = r.headers.get('X-Processed-Size');
        const isNative = r.headers.get('X-Native') === '1';
        if (origSize) {
            const sizeLabel = document.getElementById(`size-label-${path}`);
            if (sizeLabel) {
                sizeLabel.textContent = isNative
                    ? `${formatBytes(parseInt(origSize, 10))}`
                    : `${formatBytes(parseInt(origSize, 10))} → ${formatBytes(parseInt(procSize, 10))}`;
            }
        }

        const blob    = await r.blob();
        const objUrl  = URL.createObjectURL(blob);

        // Replace spinner OR existing thumbnail image with the new one
        const spinner = card.querySelector('.file-thumb-spinner');
        const existingImg = card.querySelector('.file-thumb');

        if (spinner) {
            const img = document.createElement('img');
            img.className = 'file-thumb';
            img.src = objUrl;
            img.loading = 'lazy';
            // Decode off the critical path so a burst of incoming thumbs never
            // blocks scrolling/painting on the main thread.
            img.decoding = 'async';
            spinner.replaceWith(img);
        } else if (existingImg) {
            // Revoke old blob URL to free memory
            if (existingImg.src && existingImg.src.startsWith('blob:')) {
                URL.revokeObjectURL(existingImg.src);
            }
            existingImg.src = objUrl;
        }

        card.dataset.thumbState = 'loaded';
        return isNative;
    } catch (e) {
        if (e.name === 'AbortError') {
            // Only reset the card if this fetch is still the active one, so a newer
            // frame request isn't clobbered by a stale abort handler.
            const current = thumbControllers.get(card);
            if (current !== controller) return false;
            card.dataset.thumbState = '';
            const spinner = card.querySelector('.file-thumb-spinner');
            if (spinner) {
                const isVideo = /\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(card.dataset.name);
                spinner.outerHTML = `<div class="file-thumb-placeholder" data-media="1">${isVideo ? '🎬' : '🖼️'}</div>`;
            }
        } else {
            card.dataset.thumbState = 'error';
            const spinner = card.querySelector('.file-thumb-spinner');
            if (spinner) spinner.outerHTML = '<div class="file-thumb-placeholder">⚠️</div>';
        }
        return false;
    }
}

// ── Immediate thumbnail refresh (native promotion) ─────────────────────────
// After an image is opened or downloaded in FULL, the PC promotes its cache to
// native (quality 1.0). Poll the thumb endpoint until it returns the native
// version, so the cover swaps to the full-res image without re-opening the folder.
async function refreshCardThumbnail(path) {
    const card = document.querySelector(`.file-card[data-path="${CSS.escape(path)}"]`);
    if (!card) return;
    for (let i = 0; i < 6; i++) {
        if (card.dataset.thumbState === 'loading') {
            await new Promise(r => setTimeout(r, 400));
            continue;
        }
        card.dataset.thumbState = '';
        const native = await loadThumbnail(card);
        if (native) return;
        await new Promise(r => setTimeout(r, 500));
    }
}

function cancelThumbnail(card) {
    // 'loaded' cards keep their thumbnail — never refetch just because the card
    // scrolled out of view and back.
    if (card.dataset.thumbState === 'loaded') return;
    // Always clear the loading flag so the card can reload when it scrolls back
    // into view — otherwise the AbortError handler (which sees the controller was
    // already deleted) leaves thumbState="loading" and the card spins forever.
    card.dataset.thumbState = '';
    const ctrl = thumbControllers.get(card);
    if (ctrl) {
        ctrl.abort();
        thumbControllers.delete(card);
    }
}


// ── Lightbox, Download, Multi-Select & ZIP ─────────────────────────────────
async function openLightbox(file) {
    const box     = document.getElementById('lightbox');
    const content = document.getElementById('lightbox-content');
    const nameEl  = document.getElementById('lightbox-name');
    const dlBtn   = document.getElementById('lightbox-dl-btn');
    const dlTrBtn = document.getElementById('lightbox-dl-tr-btn');
    const nativeSizeEl = document.getElementById('lightbox-dl-native-size');
    const trSizeEl     = document.getElementById('lightbox-dl-tr-size');

    content.innerHTML = '<div class="lightbox-spinner"><div class="spinner"></div></div>';
    nameEl.textContent = file.name;
    box.classList.remove('hidden');

    // Reset transcoded state for this lightbox session
    transcodedPath = null;
    transcodedSize = 0;
    if (dlTrBtn) dlTrBtn.classList.add('hidden');
    if (trSizeEl) trSizeEl.textContent = '';
    if (nativeSizeEl) nativeSizeEl.textContent = file.length ? `(${formatBytes(file.length)})` : '';

    const fileUrl = `/api/file/${encodeURIComponent(currentDevice)}?path=${encodeURIComponent(file.relativePath)}&preview=true`;

    const isImg   = /\.(jpe?g|png|gif|webp|heic|bmp)$/i.test(file.name);
    const isVideo = /\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(file.name);

    // Native download is always available
    dlBtn.onclick = () => downloadFile(null, file.relativePath, file.name);

    if (isImg) {
        const img = new Image();
        img.src = fileUrl;
        img.onload  = () => { content.innerHTML = ''; content.appendChild(img); refreshCardThumbnail(file.relativePath); };
        img.onerror = () => { content.innerHTML = '<div class="lightbox-spinner">Failed to load image.</div>'; };
    } else if (isVideo) {
        // Show the "Download Transcoded" button for videos
        if (dlTrBtn) dlTrBtn.classList.remove('hidden');

        // Fetch device config to seed the per-video override controls
        let cfg = {};
        try {
            const cr = await fetch(`/api/device-config/${encodeURIComponent(currentDevice)}`);
            if (cr.ok) {
                cfg = await cr.json();
            } else {
                console.warn(`[Lightbox] Failed to fetch device config, HTTP ${cr.status}`);
            }
        } catch (e) {
            console.error('[Lightbox] Error fetching device config:', e);
        }

        ovFile = file;
        ovFileUrl = fileUrl;
        ovContent = content;

        seedTranscodeOverrides(
            cfg.videoBitrateMultiplier ?? cfg.VideoBitrateMultiplier ?? 1.0,
            cfg.videoResolutionMultiplier ?? cfg.VideoResolutionMultiplier ?? 1.0,
            cfg.bitrateCappingEnabled ?? cfg.BitrateCappingEnabled ?? false,
            cfg.bitrateCapMbps ?? cfg.BitrateCapMbps ?? 3.0,
            cfg.resolutionCeilingEnabled ?? cfg.ResolutionCeilingEnabled ?? false,
            cfg.resolutionCeilingPx ?? cfg.ResolutionCeilingPx ?? 720
        );

        // "Download Transcoded" downloads the transcoded file if already generated,
        // otherwise it transcodes on demand with the current override values.
        if (dlTrBtn) {
            dlTrBtn.onclick = async () => {
                if (transcodedPath) {
                    downloadFile(null, transcodedPath, transcodedFileName(file.name));
                    return;
                }
                content.innerHTML = '<div class="lightbox-spinner">Transcoding...</div>';
                const ok = await runTranscodeForLightbox(file, fileUrl, currentOverrideSettings(), content);
                if (ok && transcodedPath) {
                    downloadFile(null, transcodedPath, transcodedFileName(file.name));
                }
            };
        }

        applyTranscodeOverrides();
    } else {
        content.innerHTML = `<div class="lightbox-spinner" style="padding:40px">No preview available.<br>Use Download to save.</div>`;
    }
}

async function runTranscodeForLightbox(file, fileUrl, settings, content) {
    const token = ++ovTranscodeToken;
    const result = await startTranscode(file.relativePath, settings);
    if (token !== ovTranscodeToken) return false; // superseded by a newer transcode request
    if (result) {
        transcodedPath = result.path;
        transcodedSize = result.size;
        updateTranscodedSizeLabel();
        playVideo(`/api/file/${encodeURIComponent(currentDevice)}?path=${encodeURIComponent(result.path)}&preview=true`, content);
        return true;
    }
    console.warn('[Lightbox] Transcode failed, falling back to raw video.');
    playVideo(fileUrl, content);
    return false;
}

function transcodedFileName(originalName) {
    const idx = originalName.lastIndexOf('.');
    const base = idx > 0 ? originalName.slice(0, idx) : originalName;
    return base + '_transcoded.mp4';
}

// ── Per-video transcode overrides (temporary, never persisted) ────────────
let ovFile = null;
let ovFileUrl = null;
let ovContent = null;
let ovLast = null;
let ovTranscodeToken = 0;

function currentOverrideSettings() {
    const el = id => document.getElementById(id);
    return {
        brm: parseFloat(el('ov-brm').value),
        resm: parseFloat(el('ov-resm').value),
        bitrateCapEnabled: el('ov-brcap-en').checked,
        bitrateCapMbps: parseFloat(el('ov-brcap-mbps').value) || 3.0,
        resCeilEnabled: el('ov-rescap-en').checked,
        resCeilPx: parseInt(el('ov-rescap-px').value, 10) || 720
    };
}

function seedTranscodeOverrides(brm, resm, capEn, capMbps, resEn, resPx) {
    const el = id => document.getElementById(id);
    const panel = el('lightbox-overrides');
    if (!panel) return;

    const setSlider = (inpId, dispId, val) => {
        const inp = el(inpId);
        if (!inp) return;
        inp.value = val;
        updateSliderFill(inp, dispId);
    };
    setSlider('ov-brm', 'ov-brm-display', brm);
    setSlider('ov-resm', 'ov-resm-display', resm);
    const enBr = el('ov-brcap-en'); if (enBr) enBr.checked = capEn;
    const capMbpsInp = el('ov-brcap-mbps'); if (capMbpsInp) capMbpsInp.value = capMbps;
    const enRes = el('ov-rescap-en'); if (enRes) enRes.checked = resEn;
    const pxSel = el('ov-rescap-px'); if (pxSel) pxSel.value = resPx;

    // Wire change handlers (re-transcode on adjustment)
    ['ov-brm', 'ov-resm', 'ov-brcap-en', 'ov-brcap-mbps', 'ov-rescap-en', 'ov-rescap-px'].forEach(id => {
        const ctrl = el(id);
        if (ctrl && !ctrl.dataset.ovWired) {
            ctrl.dataset.ovWired = '1';
            ctrl.onchange = () => applyTranscodeOverrides();
        }
    });

    panel.classList.remove('hidden');
    ovLast = null;
}

function equalOverride(a, b) {
    return Math.abs(a.brm - b.brm) < 0.001 && Math.abs(a.resm - b.resm) < 0.001 &&
        a.bitrateCapEnabled === b.bitrateCapEnabled && Math.abs(a.bitrateCapMbps - b.bitrateCapMbps) < 0.001 &&
        a.resCeilEnabled === b.resCeilEnabled && a.resCeilPx === b.resCeilPx;
}

function applyTranscodeOverrides() {
    if (!ovFile || !ovContent) return;
    const s = currentOverrideSettings();
    if (ovLast && equalOverride(s, ovLast)) return;
    ovLast = s;

    const capsEnabled = s.bitrateCapEnabled || s.resCeilEnabled;
    const shouldTranscode = s.brm < 1.0 || s.resm < 1.0 || capsEnabled;
    console.log(`[Lightbox] Override transcode check: brm=${s.brm}, resm=${s.resm}, caps=${capsEnabled} -> Should Transcode? ${shouldTranscode}`);

    stopCurrentTranscode();
    if (shouldTranscode) {
        ovContent.innerHTML = '<div class="lightbox-spinner">Transcoding...</div>';
        runTranscodeForLightbox(ovFile, ovFileUrl, s, ovContent);
    } else {
        console.log('[Lightbox] Multipliers are 1.0 and no hard caps enabled, playing raw video directly.');
        transcodedPath = null;
        transcodedSize = 0;
        updateTranscodedSizeLabel();
        playVideo(ovFileUrl, ovContent);
    }
}

function stopCurrentTranscode() {
    if (activeTranscodeSession && activeTranscodeDevice) {
        fetch(`/api/video/transcode/stop/${encodeURIComponent(activeTranscodeDevice)}/${activeTranscodeSession}`, { method: 'POST' }).catch(() => {});
        activeTranscodeSession = null;
        activeTranscodeDevice = null;
    }
}

function updateTranscodedSizeLabel() {
    const trSizeEl = document.getElementById('lightbox-dl-tr-size');
    if (trSizeEl) trSizeEl.textContent = transcodedSize ? `(${formatBytes(transcodedSize)})` : '';
}

function updateSliderFill(input, displayId) {
    const val = parseFloat(input.value);
    const displayEl = document.getElementById(displayId);
    if (displayEl) displayEl.textContent = val.toFixed(2);
    const pct = ((val - parseFloat(input.min)) / (parseFloat(input.max) - parseFloat(input.min))) * 100;
    input.style.setProperty('--val', pct + '%');
}

let activeTranscodeSession = null;
let activeTranscodeDevice = null;
let transcodedPath = null;
let transcodedSize = 0;

function playVideo(url, container) {
    const vid = document.createElement('video');
    vid.src = url; vid.controls = true;
    container.innerHTML = ''; container.appendChild(vid);
}

async function startTranscode(filePath, settings) {
    try {
        const r = await fetch(`/api/video/transcode/${encodeURIComponent(currentDevice)}`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                path: filePath,
                bitrateMultiplier: settings.brm,
                resolutionMultiplier: settings.resm,
                bitrateCappingEnabled: settings.bitrateCapEnabled,
                bitrateCapMbps: settings.bitrateCapMbps,
                resolutionCeilingEnabled: settings.resCeilEnabled,
                resolutionCeilingPx: settings.resCeilPx
            })
        });
        const data = await r.json();
        activeTranscodeSession = data.sessionId;
        activeTranscodeDevice = currentDevice;

        const container = document.getElementById('lightbox-content');
        for (let i = 0; i < 300; i++) {
            await new Promise(r => setTimeout(r, 1000));
            try {
                const pr = await fetch(`/api/video/transcode/${encodeURIComponent(currentDevice)}/${data.sessionId}`);
                const pd = await pr.json();
                if (pd.progress >= 100) return { path: pd.outputPath || data.outputPath, size: pd.outputBytes || 0 };
                if (pd.progress < 0) {
                    console.warn(`[Lightbox] Transcode failed (progress ${pd.progress}), falling back to raw video.`);
                    if (container) container.innerHTML = '<div class="lightbox-spinner">Transcoding failed. Loading raw video...</div>';
                    return null;
                }
                if (container) container.innerHTML = `<div class="lightbox-spinner">Transcoding: ${pd.progress}%</div>`;
            } catch (e) {}
        }
        console.warn('[Lightbox] Transcode timed out, falling back to raw video.');
        return null;
    } catch (e) { return null; }
}

function closeLightbox(e) {
    if (e && e.target !== document.getElementById('lightbox')) return;
    const box = document.getElementById('lightbox');
    box.classList.add('hidden');
    const vid = box.querySelector('video');
    if (vid) { vid.pause(); vid.src = ''; }
    if (activeTranscodeSession && activeTranscodeDevice) {
        fetch(`/api/video/transcode/stop/${encodeURIComponent(activeTranscodeDevice)}/${activeTranscodeSession}`, { method: 'POST' }).catch(() => {});
        activeTranscodeSession = null; activeTranscodeDevice = null;
    }
    // Per-video overrides are temporary: reset on close, never persisted
    const ovPanel = document.getElementById('lightbox-overrides');
    if (ovPanel) ovPanel.classList.add('hidden');
    ovTranscodeToken++; // invalidate any in-flight transcode result
    ovFile = null; ovFileUrl = null; ovContent = null; ovLast = null;
}

document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeLightbox(); });

async function downloadFile(e, path, name) {
    if (e) e.stopPropagation();
    const isImg = /\.(jpe?g|png|gif|webp|heic|bmp)$/i.test(name || '');

    if (isImg) {
        // Fetch the full image so we only act once the download REALLY completed
        // (no partial transfer), then immediately promote the card to native.
        try {
            const r = await fetch(`/api/file/${encodeURIComponent(currentDevice)}?path=${encodeURIComponent(path)}&preview=false`);
            if (!r.ok) throw new Error(`HTTP ${r.status}`);
            const blob = await r.blob();
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = name || 'file';
            a.click();
            setTimeout(() => URL.revokeObjectURL(a.href), 2000);
            refreshCardThumbnail(path);
        } catch { }
        return;
    }

    const a = document.createElement('a');
    a.href = `/api/file/${encodeURIComponent(currentDevice)}?path=${encodeURIComponent(path)}&preview=false`;
    a.download = name || 'file';
    a.click();
}

function toggleSelect(e, card) {
    e.stopPropagation();
    const path = card.dataset.path;
    if (selectedPaths.has(path)) {
        selectedPaths.delete(path);
        card.classList.remove('selected');
    } else {
        selectedPaths.add(path);
        card.classList.add('selected');
    }
    updateSelectionBar();
}

function toggleOfflineSelect(card) {
    if (!card) return;
    const path = card.dataset.path;
    if (selectedPaths.has(path)) {
        selectedPaths.delete(path);
        card.classList.remove('selected');
    } else {
        selectedPaths.add(path);
        card.classList.add('selected');
    }
    updateSelectionBar();
}

function clearSelection() {
    selectedPaths.clear();
    document.querySelectorAll('.file-card.selected').forEach(c => c.classList.remove('selected'));
    updateSelectionBar();
}

function updateSelectionBar() {
    const bar   = document.getElementById('download-bar');
    const count = document.getElementById('selection-count');
    if (!bar) return;
    if (selectedPaths.size === 0) {
        bar.classList.add('hidden');
    } else {
        bar.classList.remove('hidden');
        if (count) count.textContent = `${selectedPaths.size} file${selectedPaths.size > 1 ? 's' : ''} selected`;
    }
}

async function downloadSelected() {
    if (!selectedPaths.size) return;
    if (offlineDeviceId) {
        await downloadSelectedOffline();
        return;
    }
    if (!currentDevice) return;

    const prog  = document.getElementById('download-progress');
    const fill  = document.getElementById('progress-fill');
    const label = document.getElementById('progress-label');
    if (prog) prog.classList.remove('hidden');
    if (fill) fill.style.width = '0%';
    if (label) label.textContent = 'Generating ZIP archive…';

    const params = new URLSearchParams();
    for (const p of selectedPaths) params.append('paths', p);

    const url = `/api/download-zip/${encodeURIComponent(currentDevice)}?${params}`;

    try {
        const r = await fetch(url);
        if (!r.ok) throw new Error(`Server error ${r.status}`);

        const contentLength = parseInt(r.headers.get('Content-Length') || '0', 10);
        const reader  = r.body.getReader();
        const chunks  = [];
        let received  = 0;

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            chunks.push(value);
            received += value.length;
            if (contentLength > 0 && fill && label) {
                const pct = Math.round((received / contentLength) * 100);
                fill.style.width  = pct + '%';
                label.textContent = `${pct}% — ${formatBytes(received)} / ${formatBytes(contentLength)}`;
            } else if (label) {
                label.textContent = `Downloading… ${formatBytes(received)}`;
            }
        }

        const blob = new Blob(chunks, { type: 'application/zip' });
        const objUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = objUrl;
        a.download = 'download.zip';
        a.click();
        URL.revokeObjectURL(objUrl);

        if (fill) fill.style.width  = '100%';
        if (label) label.textContent = 'ZIP download complete!';
        setTimeout(() => prog?.classList.add('hidden'), 2500);
    } catch (e) {
        if (label) label.textContent = `Failed: ${e.message}`;
        showToast(`ZIP download failed: ${e.message}`, 'error');
    }
}

// ── Breadcrumb & Utilities ─────────────────────────────────────────────────
function renderBreadcrumb(path) {
    const nav = document.getElementById('breadcrumb');
    if (!nav) return;

    // Detach AI toggle button before clearing
    const toggleBtn = document.getElementById('ai-panel-toggle-btn');
    if (toggleBtn) toggleBtn.remove();
    nav.innerHTML = '';

    const rootBtn = document.createElement('span');
    rootBtn.className = 'breadcrumb-item';
    rootBtn.textContent = currentDevice || 'Device';
    rootBtn.addEventListener('click', () => browsePath(''));
    nav.appendChild(rootBtn);

    if (!path) {
        if (toggleBtn) nav.appendChild(toggleBtn);
        return;
    }

    const parts = path.split(/[\\/]/).filter(Boolean);
    let cumulative = '';
    for (let i = 0; i < parts.length; i++) {
        cumulative += (cumulative ? '/' : '') + parts[i];
        const sep  = document.createElement('span');
        sep.className = 'breadcrumb-sep';
        sep.textContent = ' / ';
        nav.appendChild(sep);

        if (i === parts.length - 1) {
            const curr = document.createElement('span');
            curr.className   = 'breadcrumb-current';
            curr.textContent = parts[i];
            nav.appendChild(curr);
        } else {
            const btn  = document.createElement('span');
            btn.className   = 'breadcrumb-item';
            btn.textContent = parts[i];
            const snapPath  = cumulative;
            btn.addEventListener('click', () => browsePath(snapPath));
            nav.appendChild(btn);
        }
    }
    // Re-append AI toggle button
    if (toggleBtn) nav.appendChild(toggleBtn);
}

function setBrowserState(msg) {
    const el   = document.getElementById('browser-state');
    const grid = document.getElementById('file-grid');
    if (el) { el.textContent = msg; el.classList.remove('hidden'); }
    if (grid) grid.innerHTML = '';
}
function hideBrowserState() {
    document.getElementById('browser-state')?.classList.add('hidden');
}

function isMedia(name) {
    return /\.(jpe?g|png|gif|webp|heic|bmp|mp4|mkv|mov|avi|webm|3gp)$/i.test(name);
}

function getFileIcon(name) {
    if (/\.(pdf)$/i.test(name)) return '📄';
    if (/\.(zip|rar|7z|tar|gz)$/i.test(name)) return '🗜️';
    if (/\.(mp3|wav|flac|aac|ogg)$/i.test(name)) return '🎵';
    if (/\.(txt|md|log)$/i.test(name)) return '📝';
    if (/\.(apk)$/i.test(name)) return '📦';
    return '📎';
}

function formatBytes(bytes) {
    if (!bytes) return '0 B';
    const k = 1024;
    const sizes = ['B','KB','MB','GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${(bytes / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
}

function showToast(msg, type = 'warn', ms = 4000) {
    const container = document.getElementById('toast-container');
    if (!container) return;
    const el = document.createElement('div');
    el.className = `toast toast-${type}`;
    el.textContent = msg;
    container.appendChild(el);
    setTimeout(() => {
        el.style.animation = 'toastOut 0.3s ease forwards';
        setTimeout(() => el.remove(), 350);
    }, ms);
}

function escHtml(str) {
    return String(str).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
function escAttr(str) {
    return String(str).replace(/'/g, "\\'").replace(/"/g, '"');
}

/* ═════════════════════════════════════════════════════════════════════════
   Offline Browser (Cache-based browsing without phone connection)
   ═════════════════════════════════════════════════════════════════════════ */

let offlineDeviceId = null;
let offlineDeviceName = null;
let offlineCurrentPath = '';

// ── Folder file-count hover popup (offline explorer) ───────────────────────
// After hovering a folder card for 2 seconds, show a small popup listing how
// many images/videos/audio/other files are cached inside it. Only the media
// types enabled in Settings are shown. The popup hides when the pointer leaves
// the folder, when another folder is hovered, or when the grid scrolls.
let folderCountCard = null;      // the folder card currently popup-targeted
let folderCountTimer = null;    // pending 2s hover timer
let folderCountPopup = null;    // single shared popup element (position: fixed)

function getFolderCountFlags() {
    const f = window.fileCountShowFlags || {};
    return {
        image: f.image !== false,
        video: f.video !== false,
        audio: !!f.audio,
        other: !!f.other
    };
}

function getFolderCountPopup() {
    if (!folderCountPopup) {
        folderCountPopup = document.createElement('div');
        folderCountPopup.className = 'folder-count-popup hidden';
        document.body.appendChild(folderCountPopup);
    }
    return folderCountPopup;
}

function hideFolderCountPopup() {
    if (folderCountTimer) { clearTimeout(folderCountTimer); folderCountTimer = null; }
    folderCountCard = null;
    if (folderCountPopup) folderCountPopup.classList.add('hidden');
}

function scheduleFolderCountPopup(card, counts) {
    // Restart the 2s window; moving to another folder resets it (hover "gone").
    if (folderCountTimer) { clearTimeout(folderCountTimer); folderCountTimer = null; }
    folderCountCard = card;
    folderCountTimer = setTimeout(() => {
        folderCountTimer = null;
        if (!folderCountCard) return;
        showFolderCountPopup(card, counts);
    }, 2000);
}

function showFolderCountPopup(card, counts) {
    const flags = getFolderCountFlags();
    const rows = [];
    if (flags.image) rows.push({ label: '🖼️ Images',  value: counts?.images ?? 0 });
    if (flags.video) rows.push({ label: '🎬 Videos',  value: counts?.videos ?? 0 });
    if (flags.audio) rows.push({ label: '🎵 Audio',   value: counts?.audio  ?? 0 });
    if (flags.other) rows.push({ label: '📄 Other',   value: counts?.other  ?? 0 });
    if (rows.length === 0) return;

    const popup = getFolderCountPopup();
    popup.innerHTML = `<div class="folder-count-header">📁 ${escHtml(card.dataset.name || 'Folder')}</div>` +
        rows.map(r => `<div class="folder-count-row"><span>${r.label}</span><b>${r.value}</b></div>`).join('');

    popup.classList.remove('hidden');

    // Position the popup just below the card, staying inside the viewport.
    const rect = card.getBoundingClientRect();
    const vw = window.innerWidth, vh = window.innerHeight;
    let left = Math.max(8, Math.min(rect.left, vw - popup.offsetWidth - 8));
    let top  = rect.bottom + 6;
    if (top + popup.offsetHeight > vh) top = Math.max(8, rect.top - popup.offsetHeight - 6);
    popup.style.left = left + 'px';
    popup.style.top  = top + 'px';
}

// Hide the popup while the grid scrolls (the anchor card moves under the pointer,
// so the hover no longer applies).
function bindFolderCountScrollDismiss() {
    const scrollers = document.querySelectorAll('.main-area, #file-grid');
    for (const el of scrollers) {
        el.addEventListener('scroll', () => hideFolderCountPopup(), { passive: true });
    }
}

/**
 * Initialize the offline browser page.
 * Called from Offline.cshtml on DOMContentLoaded.
 */
function initOfflineBrowser() {
    bindFolderCountScrollDismiss();
    // Check if a deviceId was passed via URL query parameter
    const params = new URLSearchParams(window.location.search);
    const deviceId = params.get('deviceId');
    if (deviceId) {
        // Auto-select this device
        offlineDeviceId = deviceId;
        // Find the device item in the sidebar and activate it
        const items = document.querySelectorAll('#device-list .device-item');
        for (const item of items) {
            if (item.dataset.deviceId === deviceId) {
                offlineDeviceName = item.dataset.deviceName || deviceId;
                // Highlight it
                document.querySelectorAll('#device-list .device-item').forEach(i => i.classList.remove('active'));
                item.classList.add('active');
                showSearchBar();
                // Navigate to root
                navigateOfflinePath('');
                break;
            }
        }
    }
}

/**
 * Refresh the offline device list from the API.
 */
async function refreshOfflineDeviceList() {
    try {
        const r = await fetch('/api/cache/devices');
        if (!r.ok) return;
        const devices = await r.json();
        const list = document.getElementById('device-list');
        if (!list) return;

        if (!devices || devices.length === 0) {
            list.innerHTML = '<li class="device-empty">No cached devices found.<br/>Browse files online first to populate cache.</li>';
            return;
        }

        list.innerHTML = devices.map(d => {
            const isWin = (d.platform || '').toLowerCase() === 'windows';
            const iconUrl = isWin ? '/Images/windows-icon.png' : '/Images/R.png';
            return `
            <li class="device-item"
                data-device-id="${escHtml(d.deviceId)}"
                data-device-name="${escHtml(d.deviceName)}"
                onclick="selectOfflineDevice(this, '${escAttr(d.deviceId)}', '${escAttr(d.deviceName)}')">
                <span class="device-icon"><img src="${iconUrl}" class="device-icon-img" alt="${isWin ? 'Windows' : 'Android'}" style="width:20px;height:20px;vertical-align:middle;object-fit:contain;" /></span>
                <span class="device-name">${escHtml(d.deviceName)}</span>
                <span class="device-status-dot offline-dot" style="background:var(--amber);box-shadow:0 0 6px var(--amber);" title="Offline (cached)"></span>
            </li>`;
        }).join('');
    } catch (e) {
        console.error('refreshOfflineDeviceList failed:', e);
    }
}

/**
 * Select an offline device from the sidebar.
 */
function selectOfflineDevice(el, deviceId, deviceName) {
    document.querySelectorAll('#device-list .device-item').forEach(i => i.classList.remove('active'));
    el.classList.add('active');
    offlineDeviceId = deviceId;
    offlineDeviceName = deviceName;
    showSearchBar();

    navigateOfflinePath('');
}

/**
 * Navigate to a path in offline mode (uses cached folder metadata).
 */
async function navigateOfflinePath(path) {
    if (!offlineDeviceId) return;

    const grid = document.getElementById('file-grid');
    const state = document.getElementById('browser-state');
    const banner = document.getElementById('offline-banner');
    const cacheTime = document.getElementById('offline-cache-time');

    if (grid) grid.innerHTML = '';
    if (state) { state.textContent = 'Loading cached data…'; state.classList.remove('hidden'); }
    if (banner) banner.classList.remove('hidden');

    offlineCurrentPath = path;

    const startIndex = parseInt(document.getElementById('startIndex')?.value || '0');
    const fileLimit  = parseInt(document.getElementById('fileLimit')?.value || '0');
    const sort       = document.getElementById('filter-sort')?.value || 'default';

    try {
        const url = `/api/cache/browse/${encodeURIComponent(offlineDeviceId)}?path=${encodeURIComponent(path)}&startIndex=${startIndex}&fileLimit=${fileLimit}&sort=${encodeURIComponent(sort)}`;
        const r = await fetch(url);

        if (!r.ok) {
            const err = await r.json().catch(() => ({ message: 'Failed to load cached data' }));
            if (state) { state.textContent = err.message || 'Folder not cached. Connect the phone and browse this folder first.'; state.classList.remove('hidden'); }
            if (grid) grid.innerHTML = '';
            if (banner) banner.classList.add('hidden');
            return;
        }

        const data = await r.json();
        hideBrowserState();
        showFilterBar();

        // Show cache time
        if (cacheTime && data.cachedTimeUtc) {
            const dt = new Date(data.cachedTimeUtc + 'Z');
            cacheTime.textContent = `Cached: ${dt.toLocaleString()}`;
        }

        renderOfflineBreadcrumb(path);

        // Offline cache returns everything it stored for the folder; apply the
        // active image/video/date filters client-side before rendering.
        const filtered = applyFileFilters(data);
        window._offlineExceededAt = (typeof data.exceededAt === 'number') ? data.exceededAt : null;
        renderOfflineFileGrid(filtered.directories || [], filtered.files || []);

        // Update URL without reload
        const params = new URLSearchParams(window.location.search);
        params.set('deviceId', offlineDeviceId);
        if (path) params.set('path', path);
        window.history.replaceState({}, '', `${window.location.pathname}?${params.toString()}`);

    } catch (e) {
        console.error('navigateOfflinePath failed:', e);
        if (state) { state.textContent = 'Error loading cached data.'; state.classList.remove('hidden'); }
    }
}

/**
 * Render breadcrumb for offline browsing.
 */
function renderOfflineBreadcrumb(path) {
    const bc = document.getElementById('breadcrumb');
    if (!bc) return;

    // Detach AI toggle button before clearing
    const toggleBtn = document.getElementById('ai-panel-toggle-btn');
    if (toggleBtn) toggleBtn.remove();

    const parts = path ? path.split('/').filter(Boolean) : [];
    let html = `<span class="breadcrumb-item" onclick="navigateOfflinePath('')">📱 ${escHtml(offlineDeviceName || offlineDeviceId)}</span>`;

    let cumulative = '';
    for (let i = 0; i < parts.length; i++) {
        cumulative += '/' + parts[i];
        html += `<span class="breadcrumb-sep">/</span>`;
        if (i === parts.length - 1) {
            html += `<span class="breadcrumb-current">${escHtml(parts[i])}</span>`;
        } else {
            html += `<span class="breadcrumb-item" onclick="navigateOfflinePath('${escAttr(cumulative)}')">${escHtml(parts[i])}</span>`;
        }
    }

    bc.innerHTML = html;
    // Re-append AI toggle button
    if (toggleBtn) bc.appendChild(toggleBtn);
}

/**
 * Render the file grid for offline browsing.
 * Uses the same progressive chunked rendering as the online browser so that
 * folders with thousands of files never block the main thread — cards are
 * appended in small batches via requestAnimationFrame instead of one giant
 * innerHTML assignment.
 */
function renderOfflineFileGrid(dirs, files, exceededAt) {
    resetThumbObserver();

    const grid = document.getElementById('file-grid');
    if (!grid) return;

    grid.innerHTML = '';

    if (!dirs.length && !files.length) {
        if (exceededAt == null) {
            grid.innerHTML = '<div style="color:var(--text2);padding:20px;grid-column:1/-1">This folder is empty.</div>';
        }
        finishOfflineRender();
        return;
    }

    // Directories render immediately (few of them).
    const dirFragment = document.createDocumentFragment();
    for (const d of dirs) dirFragment.appendChild(makeOfflineDirCard(d));
    grid.appendChild(dirFragment);

    // Files render in progressive chunks using requestAnimationFrame.
    let index = 0;
    const chunkSize = 40;

    function renderChunk() {
        const end = Math.min(index + chunkSize, files.length);
        const fragment = document.createDocumentFragment();

        for (let i = index; i < end; i++) {
            const card = makeOfflineFileCard(files[i]);
            fragment.appendChild(card);

            if (isMedia(files[i].name)) {
                thumbObserver.observe(card);
            }
        }
        grid.appendChild(fragment);
        index = end;

        if (index < files.length) {
            requestAnimationFrame(renderChunk);
        } else {
            finishOfflineRender();
        }
    }

    renderChunk();
}

function finishOfflineRender() {
    const grid = document.getElementById('file-grid');
    if (!grid) return;

    const exceededAt = window._offlineExceededAt;
    if (exceededAt != null) {
        const div = document.createElement('div');
        div.className = 'file-card';
        div.style.cssText = 'display:flex;align-items:center;justify-content:center;min-height:100px;cursor:default;opacity:0.7;';
        div.innerHTML = `<span style="color:var(--amber);text-align:center;padding:12px;">⚠️ File limit reached<br><small>${exceededAt} files total. Adjust limiter to see more.</small></span>`;
        grid.appendChild(div);
    }

    // Restore the "Fetch the Files" selection highlight on re-rendered cards.
    for (const card of grid.querySelectorAll('.file-card[data-path]')) {
        if (fetchSelectedPaths.has(card.dataset.path)) card.classList.add('fetch-selected');
    }

    // Attach hover handlers for offline video frame switching (auto-cycle + slider).
    const offlineVideoCards = grid.querySelectorAll('.offline-video-card');
    for (const card of offlineVideoCards) {
        card.addEventListener('mouseenter', () => startOfflineFrameCycle(card));
        card.addEventListener('mouseleave', () => stopOfflineFrameCycle(card));
        const overlay = card.querySelector('.card-override-overlay');
        if (overlay) {
            overlay.addEventListener('mouseenter', () => stopOfflineFrameCycle(card));
            overlay.addEventListener('mouseleave', () => startOfflineFrameCycle(card));
        }
    }
}

function makeOfflineDirCard(d) {
    const fullPath = offlineCurrentPath ? `${offlineCurrentPath}/${d.name}` : d.name;
    const card = document.createElement('div');
    card.className = 'file-card';
    card.dataset.path = fullPath;
    card.dataset.name = d.name;
    card.innerHTML = `
        <div class="file-thumb-container">
            <div class="file-thumb-placeholder">📁</div>
        </div>
        <div class="file-info">
            <div class="file-name">${escHtml(d.name)}</div>
            <div class="file-size">Folder</div>
        </div>`;
    card.addEventListener('click', () => navigateOfflinePath(fullPath));

    // Folder hover popup: file counts per media type (only for enabled types).
    const flags = getFolderCountFlags();
    if (flags.image || flags.video || flags.audio || flags.other) {
        card.addEventListener('mouseenter', () => {
            hideFolderCountPopup();
            scheduleFolderCountPopup(card, d.counts);
        });
        card.addEventListener('mouseleave', hideFolderCountPopup);
    }

    return card;
}

function makeOfflineFileCard(f) {
    const fullPath = offlineCurrentPath ? `${offlineCurrentPath}/${f.name}` : f.name;
    const isMediaFile = isMedia(f.name);
    const icon = getFileIcon(f.name);
    const sizeStr = formatBytes(f.length);

    const card = document.createElement('div');
    card.className = 'file-card';
    card.dataset.path = fullPath;
    card.dataset.device = offlineDeviceId;
    card.dataset.name = f.name;
    card.dataset.size = f.length;

    if (isMediaFile) {
        const isVid  = /\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(f.name);
        if (isVid) card.classList.add('offline-video-card');
        if (isVid) card.dataset.framesPending = '1';
        const overlay = isVid ? `
                <div class="card-override-overlay" onclick="event.stopPropagation()">
                    <div class="override-label">
                        <span>Frame: <b class="frame-val-label">Value –</b></span>
                    </div>
                    <input type="range" min="1" max="1" step="1" class="override-slider" value="1"
                           oninput="updateOfflineFrameLabel(this)"
                           onchange="applyOfflineFrameOverride(this, '${escAttr(fullPath)}')" />
                </div>` : '';
        // Media file with thumbnail (from cache)
        card.innerHTML = `
                <div class="file-thumb-container">
                    <div class="file-thumb-spinner"><div class="spinner"></div></div>
                    ${overlay}
                </div>
                <div class="file-info">
                    <div class="file-name">${escHtml(f.name)}</div>
                    <div class="file-size">${sizeStr}</div>
                </div>
                <div class="file-actions">
                    <button class="file-action-btn" title="Select for ZIP download" onclick="event.stopPropagation(); toggleOfflineSelect(this.closest('.file-card'))">☑️</button>
                    <button class="file-action-btn" title="Download" onclick="event.stopPropagation(); downloadOfflineFile('${escAttr(fullPath)}')">⬇️</button>
                    <button class="file-action-btn" title="Add to Fetch the Files action" onclick="event.stopPropagation(); toggleFetchSelect('${escAttr(fullPath)}', this)">⏰</button>
                    <button class="file-action-btn" title="Add to AI queue" onclick="event.stopPropagation(); addToAIQueue(event,'${escAttr(fullPath)}','${escAttr(f.name)}')">🤖</button>
                </div>`;
        card.addEventListener('click', () => previewOfflineFile(fullPath));
    } else {
        // Non-media file
        card.innerHTML = `
                <div class="file-thumb-container">
                    <div class="file-thumb-placeholder">${icon}</div>
                </div>
                <div class="file-info">
                    <div class="file-name">${escHtml(f.name)}</div>
                    <div class="file-size">${sizeStr}</div>
                </div>
                <div class="file-actions">
                    <button class="file-action-btn" title="Select for ZIP download" onclick="event.stopPropagation(); toggleOfflineSelect(this.closest('.file-card'))">☑️</button>
                    <button class="file-action-btn" title="Download" onclick="event.stopPropagation(); downloadOfflineFile('${escAttr(fullPath)}')">⬇️</button>
                    <button class="file-action-btn" title="Add to Fetch the Files action" onclick="event.stopPropagation(); toggleFetchSelect('${escAttr(fullPath)}', this)">⏰</button>
                </div>`;
    }
    return card;
}

/**
 * Preview an offline file in the lightbox.
 * Uses the same /api/thumb endpoint which will serve from cache.
 */
async function previewOfflineFile(filePath) {
    const lightbox = document.getElementById('lightbox');
    const content = document.getElementById('lightbox-content');
    const nameEl = document.getElementById('lightbox-name');
    const dlBtn = document.getElementById('lightbox-dl-btn');

    if (!lightbox || !content) return;

    lightbox.classList.remove('hidden');
    content.innerHTML = '<div class="lightbox-spinner">Loading…</div>';
    if (nameEl) nameEl.textContent = filePath.split('/').pop() || filePath;

    const card = document.querySelector(`.file-card[data-path="${CSS.escape(filePath)}"]`);
    const frameOv = (card && card.dataset.frameOverride) ? card.dataset.frameOverride : -1.0;

    // Prefer the full cached file (seeded by fetch actions / playback) so offline
    // video/audio/image previews play from disk with no phone connection needed.
    const fullUrl = `/api/cache/file/${encodeURIComponent(offlineDeviceId)}?path=${encodeURIComponent(filePath)}&download=0`;
    try {
        const head = await fetch(fullUrl, { method: 'HEAD' });
        if (head.ok) {
            const r = await fetch(fullUrl);
            if (r.ok) {
                const blob = await r.blob();
                const url = URL.createObjectURL(blob);
                const ctype = r.headers.get('Content-Type') || '';
                if (ctype.startsWith('video/') || ctype.startsWith('audio/')) {
                    content.innerHTML = `<video src="${url}" controls autoplay style="max-width:100%;max-height:72vh;border-radius:var(--radius);"></video>`;
                } else if (ctype.startsWith('image/')) {
                    content.innerHTML = `<img src="${url}" alt="Preview" style="max-width:100%;max-height:72vh;border-radius:var(--radius);" />`;
                } else {
                    content.innerHTML = `<div style="color:var(--text2);padding:24px">Full file available. Use Download to save it.</div>`;
                }
                if (dlBtn) {
                    dlBtn.onclick = () => downloadOfflineFile(filePath);
                    dlBtn.style.display = '';
                }
                return;
            }
        }
    } catch (e) {
        console.error('full-file preview probe failed:', e);
    }

    // Fallback: cached thumbnail / phone-streamed video preview.
    const bestCache = !/\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(filePath);
    const thumbUrl = `/api/thumb/${encodeURIComponent(offlineDeviceId)}?path=${encodeURIComponent(filePath)}&frameOverride=${frameOv}&qualityOverride=-1${bestCache ? '&bestCache=1' : ''}`;

    try {
        const r = await fetch(thumbUrl);
        if (!r.ok) {
            content.innerHTML = '<div class="lightbox-spinner" style="color:var(--red)">Thumbnail not available offline</div>';
            return;
        }

        const blob = await r.blob();
        const url = URL.createObjectURL(blob);
        const contentType = r.headers.get('Content-Type') || '';

        if (contentType.startsWith('video/')) {
            content.innerHTML = `<video src="${url}" controls autoplay style="max-width:100%;max-height:72vh;border-radius:var(--radius);"></video>`;
        } else {
            content.innerHTML = `<img src="${url}" alt="Preview" style="max-width:100%;max-height:72vh;border-radius:var(--radius);" />`;
        }

        if (dlBtn) {
            dlBtn.onclick = () => downloadOfflineFile(filePath);
            dlBtn.style.display = '';
        }
    } catch (e) {
        console.error('previewOfflineFile failed:', e);
        content.innerHTML = '<div class="lightbox-spinner" style="color:var(--red)">Failed to load preview</div>';
    }
}

/**
 * Download a file from offline cache (full file, no phone needed).
 */
async function downloadOfflineFile(filePath) {
    const cachedUrl = `/api/cache/file/${encodeURIComponent(offlineDeviceId)}?path=${encodeURIComponent(filePath)}&download=1`;
    try {
        const head = await fetch(cachedUrl, { method: 'HEAD' });
        if (head.ok) {
            window.open(cachedUrl, '_blank');
            return;
        }
    } catch (e) {
        console.error('offline download probe failed:', e);
    }

    showToast('Full file not in offline cache. Requires phone connection.', 'warn', 5000);
    const url = `/api/file/${encodeURIComponent(offlineDeviceId)}?path=${encodeURIComponent(filePath)}&preview=false`;
    window.open(url, '_blank');
}

/**
 * Apply limiter settings for offline browsing.
 */
function applyOfflineLimiter() {
    if (offlineCurrentPath !== undefined) {
        navigateOfflinePath(offlineCurrentPath);
    }
}

/**
 * Download selected files as ZIP (offline mode).
 * Builds the archive server-side from the cached full files, keeping their
 * original file names — no phone connection needed.
 */
async function downloadSelectedOffline() {
    if (selectedPaths.size === 0) {
        showToast('No files selected.', 'warn');
        return;
    }

    const prog  = document.getElementById('download-progress');
    const fill  = document.getElementById('progress-fill');
    const label = document.getElementById('progress-label');
    if (prog) prog.classList.remove('hidden');
    if (fill) fill.style.width = '0%';
    if (label) label.textContent = 'Generating ZIP from cache…';

    const params = new URLSearchParams();
    for (const p of selectedPaths) params.append('paths', p);
    const url = `/api/cache/zip/${encodeURIComponent(offlineDeviceId || '')}?${params}`;

    try {
        const r = await fetch(url);
        if (!r.ok) {
            const txt = await r.text();
            throw new Error(txt || `Server error ${r.status}`);
        }

        const contentLength = parseInt(r.headers.get('Content-Length') || '0', 10);
        const reader  = r.body.getReader();
        const chunks  = [];
        let received  = 0;

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            chunks.push(value);
            received += value.length;
            if (contentLength > 0 && fill && label) {
                const pct = Math.round((received / contentLength) * 100);
                fill.style.width  = pct + '%';
                label.textContent = `${pct}% — ${formatBytes(received)} / ${formatBytes(contentLength)}`;
            } else if (label) {
                label.textContent = `Downloading… ${formatBytes(received)}`;
            }
        }

        const blob = new Blob(chunks, { type: 'application/zip' });
        const objUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = objUrl;
        a.download = 'offline-files.zip';
        a.click();
        URL.revokeObjectURL(objUrl);

        if (fill) fill.style.width  = '100%';
        if (label) label.textContent = 'ZIP download complete!';
        setTimeout(() => prog?.classList.add('hidden'), 2500);
    } catch (e) {
        if (label) label.textContent = `Failed: ${e.message}`;
        showToast(`Offline ZIP failed: ${e.message}`, 'error');
        setTimeout(() => prog?.classList.add('hidden'), 3000);
    }
}

/* ═══════════════════════════════════════════════════════════════
   "Fetch the Files" multi-select (Explore Offline)
   Clicking the ⏰ on a hovered file adds it to a pinned bottom bar,
   where you can create a new "Fetch the Files" action for this device
   or add the files to an existing one. The ⓘ tooltip shows the actual
   device config settings that the fetch will use.
   ═══════════════════════════════════════════════════════════════ */

let fetchSelectedPaths = new Set();

function toggleFetchSelect(path, btn) {
    const card = btn.closest('.file-card');
    if (fetchSelectedPaths.has(path)) {
        fetchSelectedPaths.delete(path);
        if (card) card.classList.remove('fetch-selected');
    } else {
        fetchSelectedPaths.add(path);
        if (card) card.classList.add('fetch-selected');
    }
    updateFetchBar();
}

function updateFetchBar() {
    const bar = document.getElementById('fetch-files-bar');
    if (!bar) return;
    const count = document.getElementById('fetch-selection-count');
    if (fetchSelectedPaths.size === 0) {
        bar.classList.add('hidden');
        return;
    }
    bar.classList.remove('hidden');
    if (count) count.textContent = `${fetchSelectedPaths.size} file${fetchSelectedPaths.size > 1 ? 's' : ''} selected`;
    const addLabel = document.getElementById('fetch-add-group');
    if (addLabel) {
        const name = offlineDeviceName || offlineDeviceId || 'this device';
        const pre = addLabel.querySelector('.fetch-add-prefix');
        if (pre) pre.textContent = `Add to "Fetch the Files" action for ${name}:`;
    }
    populateFetchExistingActions();
}

async function populateFetchExistingActions() {
    const sel = document.getElementById('fetch-existing-actions');
    const btn = document.getElementById('fetch-add-btn');
    if (!sel) return;
    sel.innerHTML = '<option value="">Loading…</option>';
    try {
        const r = await fetch('/api/actions');
        if (!r.ok) throw new Error('bad status');
        const all = await r.json();
        const eligible = (all || []).filter(a =>
            (a.type || '') === 'fetchFiles'
            && a.enabled !== false
            && a.deviceId && a.deviceId.toLowerCase() === (offlineDeviceId || '').toLowerCase());
        if (!eligible.length) {
            sel.innerHTML = '<option value="">No "Fetch the Files" action for this device yet</option>';
            if (btn) btn.disabled = true;
        } else {
            sel.innerHTML = eligible.map(a => `<option value="${escAttr(a.id)}">${escHtml(a.name || 'Fetch the Files')} (${(a.files||[]).length} files)</option>`).join('');
            if (btn) btn.disabled = false;
        }
    } catch (e) {
        sel.innerHTML = '<option value="">Failed to load actions</option>';
        if (btn) btn.disabled = true;
    }
}

async function addFilesToExisting() {
    const sel = document.getElementById('fetch-existing-actions');
    if (!sel || !sel.value) { showToast('No "Fetch the Files" action for this device yet.', 'warn'); return; }
    if (fetchSelectedPaths.size === 0) return;
    const files = Array.from(fetchSelectedPaths);
    try {
        const r = await fetch(`/api/actions/${encodeURIComponent(sel.value)}/add-files`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ files })
        });
        if (!r.ok) throw new Error((await r.json().catch(() => ({}))).message || 'request failed');
        const data = await r.json();
        showToast(`Added ${data.added} file(s)${data.skipped ? ` (${data.skipped} already listed)` : ''} to "${data.action.name || 'Fetch the Files'}"`, 'ok', 5000);
        clearFetchSelection();
    } catch (e) {
        showToast('Failed to add files: ' + e.message, 'err', 6000);
    }
}

function clearFetchSelection() {
    fetchSelectedPaths.clear();
    document.querySelectorAll('.file-card.fetch-selected').forEach(c => c.classList.remove('fetch-selected'));
    updateFetchBar();
}

function openFetchCreateModal() {
    const modal = document.getElementById('fetch-create-modal');
    if (!modal) return;
    document.getElementById('fetch-create-name').value = '';
    document.querySelector('input[name="fetch-create-vidtype"][value="thumbnail"]').checked = true;
    document.querySelector('input[name="fetch-create-trigger"][value="manual"]').checked = true;
    // Default to Custom Overrides so every setting is visible, pre-filled with the
    // device's actual config values (identical behaviour to "Use Global Config").
    document.querySelector('input[name="fetch-create-config"][value="custom"]').checked = true;
    fcOnConfigMode();
    fcSetConcurrencyDefaults();
    renderFetchCreateFiles();
    modal.classList.remove('hidden');

    if (offlineDeviceId) {
        fetch(`/api/device-config-saved/${encodeURIComponent(offlineDeviceId)}`)
            .then(r => r.ok ? r.json() : null)
            .then(c => { if (c) fcApplyDeviceConfig(c); })
            .catch(() => {});
    }
}

function closeFetchCreateModal() {
    document.getElementById('fetch-create-modal')?.classList.add('hidden');
}

/* ── Full settings for the Explore Offline "Fetch the Files" modal ───────── */
function fcOnConfigMode() {
    const mode = document.querySelector('input[name="fetch-create-config"]:checked')?.value || 'global';
    document.getElementById('fc-overrides-panel')?.classList.toggle('hidden', mode !== 'custom');
}

function fcOnConcurrency(kind) {
    const prefix = kind === 'img' ? 'fc-img' : 'fc-hvy';
    const sel = document.getElementById(prefix + '-mode');
    document.getElementById(prefix + '-count').classList.toggle('hidden', sel.value !== 'custom');
}

function fcSetConcurrencyDefaults() {
    document.getElementById('fc-img-mode').value = 'default';
    document.getElementById('fc-hvy-mode').value = 'default';
    document.getElementById('fc-img-count').value = 5;
    document.getElementById('fc-hvy-count').value = 1;
    fcOnConcurrency('img');
    fcOnConcurrency('hvy');
}

function fcApplyDeviceConfig(c) {
    const setSlider = (id, disp, val) => {
        const el = document.getElementById(id);
        if (el && val != null && isFinite(parseFloat(val))) {
            el.value = val;
            updateSliderFill(el, disp);
        }
    };
    setSlider('fc-ov-vf', 'fc-ov-vf-disp', c.videoFrameNormalized);
    setSlider('fc-ov-tq', 'fc-ov-tq-disp', c.thumbnailQuality);
    setSlider('fc-ov-brm', 'fc-ov-brm-disp', c.videoBitrateMultiplier);
    setSlider('fc-ov-resm', 'fc-ov-resm-disp', c.videoResolutionMultiplier);
    const br = document.getElementById('fc-ov-brcap-en');
    if (br) br.checked = c.bitrateCappingEnabled !== false;
    const mbps = document.getElementById('fc-ov-brcap-mbps');
    if (mbps && c.bitrateCapMbps != null) mbps.value = c.bitrateCapMbps;
    const rc = document.getElementById('fc-ov-rescap-en');
    if (rc) rc.checked = c.resolutionCeilingEnabled !== false;
    const px = document.getElementById('fc-ov-rescap-px');
    if (px && c.resolutionCeilingPx) px.value = String(c.resolutionCeilingPx);
}

function renderFetchCreateFiles() {
    const list = document.getElementById('fetch-create-files');
    if (!list) return;
    list.innerHTML = '';
    Array.from(fetchSelectedPaths).forEach(p => {
        const row = document.createElement('div');
        row.className = 'file-row';
        row.dataset.path = p;
        row.innerHTML = `${fileRowThumb(offlineDeviceId, p)}
            <span class="file-row-path" title="${escHtml(p)}">${escHtml(p)}</span>
            <button class="btn btn-ghost btn-sm" type="button" onclick="this.closest('.file-row').remove(); refreshFetchCreateStatus()">✕</button>`;
        list.appendChild(row);
    });
    refreshFetchCreateStatus();
}

// Little cached thumbnail (if available for this device) shown in the file row.
function fileRowThumb(deviceId, path) {
    if (!deviceId || !path) return '';
    return `<img class="file-row-thumb" src="/api/thumb/${encodeURIComponent(deviceId)}?path=${encodeURIComponent(path)}&bestCache=1&frameOverride=-1&qualityOverride=-1" loading="lazy" alt="" onerror="this.remove()" />`;
}

async function refreshFetchCreateStatus() {
    const list = document.getElementById('fetch-create-files');
    if (!list || !offlineDeviceId) return;
    const rows = Array.from(list.querySelectorAll('.file-row'));
    const files = rows.map(r => r.dataset.path);
    rows.forEach(r => { r.classList.remove('fetch-greyed'); r.title = ''; });
    if (!files.length) return;
    const videoType = document.querySelector('input[name="fetch-create-vidtype"]:checked')?.value || 'thumbnail';
    try {
        const r = await fetch('/api/actions/cache-status', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ deviceId: offlineDeviceId, files, videoFetchType: videoType, useGlobalConfig: true, overrides: null })
        });
        if (!r.ok) return;
        const status = await r.json();
        const byPath = new Map(status.map(s => [s.path.toLowerCase(), s]));
        rows.forEach(r => {
            const s = byPath.get(r.dataset.path.toLowerCase());
            if (!s || s.status === 'fetch') return;
            r.classList.add('fetch-greyed');
            r.title = s.note || '';
        });
    } catch { }
}

async function saveFetchCreate() {
    const files = Array.from(document.querySelectorAll('#fetch-create-files .file-row')).map(r => r.dataset.path);
    if (!files.length) { showToast('No files selected.', 'warn'); return; }
    if (!offlineDeviceId) { showToast('No offline device selected.', 'warn'); return; }

    const videoFetchType = document.querySelector('input[name="fetch-create-vidtype"]:checked')?.value || 'thumbnail';
    const trigger = document.querySelector('input[name="fetch-create-trigger"]:checked')?.value || 'manual';
    const useGlobal = (document.querySelector('input[name="fetch-create-config"]:checked')?.value || 'global') === 'global';
    const imgMode = document.getElementById('fc-img-mode').value;
    const hvyMode = document.getElementById('fc-hvy-mode').value;
    if (imgMode === 'custom' && (!(document.getElementById('fc-img-count').value > 0) || document.getElementById('fc-img-count').value > 100)) {
        showToast('Image concurrency must be between 1 and 100.', 'warn'); return;
    }
    if (hvyMode === 'custom' && (!(document.getElementById('fc-hvy-count').value > 0) || document.getElementById('fc-hvy-count').value > 50)) {
        showToast('Video/other concurrency must be between 1 and 50.', 'warn'); return;
    }

    const payload = {
        name: (document.getElementById('fetch-create-name').value || '').trim(),
        type: 'fetchFiles',
        files,
        videoFetchType,
        deviceId: offlineDeviceId,
        contentTypes: [],
        trigger,
        enabled: true,
        useGlobalConfig: useGlobal,
        imageConcurrencyMode: imgMode,
        imageConcurrency: parseInt(document.getElementById('fc-img-count').value, 10) || 5,
        heavyConcurrencyMode: hvyMode,
        heavyConcurrency: parseInt(document.getElementById('fc-hvy-count').value, 10) || 1,
        overrides: {
            videoFrameNormalized: parseFloat(document.getElementById('fc-ov-vf').value),
            thumbnailQuality: parseFloat(document.getElementById('fc-ov-tq').value),
            videoBitrateMultiplier: parseFloat(document.getElementById('fc-ov-brm').value),
            videoResolutionMultiplier: parseFloat(document.getElementById('fc-ov-resm').value),
            bitrateCappingEnabled: document.getElementById('fc-ov-brcap-en').checked,
            bitrateCapMbps: parseFloat(document.getElementById('fc-ov-brcap-mbps').value) || 3.0,
            resolutionCeilingEnabled: document.getElementById('fc-ov-rescap-en').checked,
            resolutionCeilingPx: parseInt(document.getElementById('fc-ov-rescap-px').value, 10) || 720
        }
    };

    try {
        const r = await fetch('/api/actions', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (!r.ok) throw new Error((await r.json().catch(() => ({}))).message || 'request failed');
        const saved = await r.json();
        showToast(`Action "${saved.name || 'Fetch the Files'}" saved (${saved.files.length} files)`, 'ok', 5000);
        closeFetchCreateModal();
        clearFetchSelection();
    } catch (e) {
        showToast('Failed to create action: ' + e.message, 'err', 6000);
    }
}

async function showFetchInfo() {
    const popup = document.getElementById('fetch-info-popup');
    if (!popup || !offlineDeviceId) return;
    popup.classList.remove('hidden');
    popup.innerHTML = '<div style="font-size:0.8rem;color:var(--text2)">Loading config…</div>';
    try {
        const r = await fetch(`/api/device-config-saved/${encodeURIComponent(offlineDeviceId)}`);
        if (!r.ok) { popup.innerHTML = '<div style="font-size:0.8rem;color:var(--text2)">No saved config for this device.</div>'; return; }
        const c = await r.json();
        popup.innerHTML = `
            <div class="fetch-info-title">⚙️ Device config settings</div>
            <div class="fetch-info-row"><span>Video Frame</span><b>${fmtNum(c.videoFrameNormalized)}</b></div>
            <div class="fetch-info-row"><span>Thumbnail Quality</span><b>${fmtNum(c.thumbnailQuality)}</b></div>
            <div class="fetch-info-row"><span>Video Bitrate Mult.</span><b>${fmtNum(c.videoBitrateMultiplier)}</b></div>
            <div class="fetch-info-row"><span>Video Resolution Mult.</span><b>${fmtNum(c.videoResolutionMultiplier)}</b></div>
            <div class="fetch-info-row"><span>Bitrate Cap</span><b>${c.bitrateCappingEnabled ? (c.bitrateCapMbps + ' Mbps') : 'Off'}</b></div>
            <div class="fetch-info-row"><span>Resolution Ceiling</span><b>${c.resolutionCeilingEnabled ? (c.resolutionCeilingPx + 'p') : 'Off'}</b></div>
            <div class="fetch-info-row"><span>Cache Size</span><b>${(c.cacheSizeMb ?? 1024)} MB</b></div>
            <div class="fetch-info-row"><span>File Route</span><b>${escHtml(c.fileRoute || 'Domain')}</b></div>
        `;
    } catch (e) {
        popup.innerHTML = '<div style="font-size:0.8rem;color:var(--text2)">Failed to load config.</div>';
    }
}

function hideFetchInfo() {
    document.getElementById('fetch-info-popup')?.classList.add('hidden');
}

function fmtNum(v) {
    const n = parseFloat(v);
    if (isNaN(n)) return '—';
    return n.toFixed(2);
}

/* ═══════════════════════════════════════════════════════════════
   Offline Video Frame Switching
   Hovering a cached video shows a frame slider and auto-cycles through
   the downloaded frames (lowest -> highest, 1s each). Moving the mouse
   onto the slider stops the cycle so it can be scrubbed manually.
   ═══════════════════════════════════════════════════════════════ */

// Cached frame lists per (deviceId|path); only non-empty results are kept.
const offlineFramesCache = new Map();
// Remembers the user's last chosen frame per path across re-renders.
const offlineFrameSelection = new Map();
// Per-card timers for the hover auto-cycle.
const offlineCycleTimers = new WeakMap();

function getCardFrames(card) {
    try {
        const arr = JSON.parse(card.dataset.frames || '[]');
        return Array.isArray(arr) ? arr : [];
    } catch { return []; }
}

// Position the slider at the given frame. The slider spans the cached frame count
// (1..N), each position mapping to one fetched frame.
function updateOfflineSlider(card, frame) {
    const slider = card.querySelector('.override-slider');
    const label  = card.querySelector('.frame-val-label');
    if (!slider) return;
    const frames = getCardFrames(card);
    if (!frames.length) {
        slider.min = 1; slider.max = 1; slider.step = 1; slider.value = 1;
        if (label) label.textContent = `Value ${Number(frame).toFixed(2)}`;
        return;
    }
    slider.min = 1; slider.max = frames.length; slider.step = 1;
    let idx = frames.findIndex(f => Math.abs(f - frame) < 0.005);
    if (idx < 0) idx = 0;
    slider.value = idx + 1;
    if (label) label.textContent = `Value ${frames[idx].toFixed(2)} · ${idx + 1}/${frames.length}`;
}

function updateOfflineFrameLabel(slider) {
    const card = slider.closest('.file-card');
    const label = card?.querySelector('.frame-val-label');
    if (!label) return;
    const frames = getCardFrames(card);
    if (!frames.length) {
        label.textContent = `Value ${parseFloat(slider.value).toFixed(2)}`;
        return;
    }
    const idx = Math.min(Math.max(parseInt(slider.value, 10) || 1, 1), frames.length) - 1;
    label.textContent = `Value ${frames[idx].toFixed(2)} · ${idx + 1}/${frames.length}`;
}

// Show a specific frame on the card and (re)load its cached thumbnail.
function setOfflineCardFrame(card, frame) {
    const ctrl = thumbControllers.get(card);
    if (ctrl) ctrl.abort();
    card.dataset.frameOverride = String(frame);
    offlineFrameSelection.set(card.dataset.path, frame);
    updateOfflineSlider(card, frame);
    card.dataset.thumbState = '';
    loadThumbnail(card);
}

// Manual slider change: snaps to the cached frame at that position (stops auto-cycle).
function applyOfflineFrameOverride(slider, path) {
    const card = slider.closest('.file-card');
    if (!card) return;
    stopOfflineFrameCycle(card);
    const frames = getCardFrames(card);
    const idx = frames.length
        ? Math.min(Math.max(parseInt(slider.value, 10) || 1, 1), frames.length) - 1
        : 0;
    const val = frames.length ? frames[idx] : parseFloat(slider.value);
    card.dataset.frameOverride = String(val);
    offlineFrameSelection.set(path, val);
    updateOfflineSlider(card, val);
    const img = card.querySelector('.file-thumb');
    if (img) {
        if (img.src && img.src.startsWith('blob:')) URL.revokeObjectURL(img.src);
        const isVideo = /\.(mp4|mkv|mov|avi|webm|3gp)$/i.test(card.dataset.name);
        img.outerHTML = `<div class="file-thumb-placeholder" data-media="1">${isVideo ? '🎬' : '🖼️'}</div>`;
    }
    card.dataset.thumbState = '';
    loadThumbnail(card);
}

// Auto-cycle through cached frames after a 1s hover delay (loops until stopped).
function startOfflineFrameCycle(card) {
    stopOfflineFrameCycle(card);
    const frames = getCardFrames(card);
    if (frames.length < 2) return;
    const state = { delay: null, frame: null, index: 0 };
    offlineCycleTimers.set(card, state);

    const step = () => {
        if (offlineCycleTimers.get(card) !== state) return;
        const frame = frames[state.index % frames.length];
        state.index = (state.index + 1) % frames.length;
        setOfflineCardFrame(card, frame);
        state.frame = setTimeout(step, 1000);
    };

    state.delay = setTimeout(step, 1000);
}

function stopOfflineFrameCycle(card) {
    const state = offlineCycleTimers.get(card);
    if (!state) return;
    if (state.delay) clearTimeout(state.delay);
    if (state.frame) clearTimeout(state.frame);
    offlineCycleTimers.delete(card);
}

// Fetch the cached frames for an offline video card, pick the default frame, and load it.
async function loadOfflineVideoFrames(card) {
    const path = card.dataset.path;
    const deviceId = card.dataset.device;
    card.dataset.framesPending = '0';
    const key = deviceId + '|' + path;

    let frameObjs;
    if (offlineFramesCache.has(key)) {
        frameObjs = offlineFramesCache.get(key);
    } else {
        const fc = new AbortController();
        try {
            const r = await enqueueThumbFetch(fc, () =>
                fetch(`/api/cache/frames/${encodeURIComponent(deviceId)}?path=${encodeURIComponent(path)}`, { signal: fc.signal }));
            if (!r.ok) throw new Error('HTTP ' + r.status);
            const data = await r.json();
            // Server serializes CachedVideoFrame with camelCase (frameNormalized); accept both.
            frameObjs = (data.frames || []).map(f => ({
                frame: f.frame != null ? f.frame : f.frameNormalized,
                quality: f.quality ?? f.Quality ?? -1,
                cachedTimeUtc: f.cachedTimeUtc || f.CachedTimeUtc || ''
            })).filter(f => f.frame != null).sort((a, b) => a.frame - b.frame);
            if (frameObjs.length > 0) offlineFramesCache.set(key, frameObjs);
        } catch {
            frameObjs = [];
        }
    }

    try {
        card.dataset.frames = JSON.stringify(frameObjs.map(f => f.frame));

        if (frameObjs.length === 0) {
            card.dataset.thumbState = '';
            loadThumbnail(card);
            return;
        }

        let chosen = frameObjs[0];
        const prev = offlineFrameSelection.get(path);
        if (prev != null) {
            const match = frameObjs.find(f => Math.abs(f.frame - prev) < 0.005);
            if (match) chosen = match;
        } else {
            // Default: highest-quality cached frame (ties -> most recently cached).
            for (const f of frameObjs) {
                const q = f.quality ?? -1, cq = chosen.quality ?? -1;
                if (q > cq + 0.0001
                    || (Math.abs(q - cq) <= 0.0001 && (f.cachedTimeUtc || '') > (chosen.cachedTimeUtc || ''))) {
                    chosen = f;
                }
            }
        }
        offlineFrameSelection.set(path, chosen.frame);
        setOfflineCardFrame(card, chosen.frame);
    } catch (err) {
        // Never leave the card stuck on a spinner.
        console.error('loadOfflineVideoFrames failed:', err);
        card.dataset.thumbState = '';
        loadThumbnail(card);
    }
}

/* ═══════════════════════════════════════════════════════════════
   AI Sidebar (Moondream 2) — Client Logic
   ═══════════════════════════════════════════════════════════════ */

let aiActive = false;
let aiProcessing = false;       // true while a scan is running
let aiSkipCurrent = false;      // signal to skip current image
let aiStopRequested = false;    // signal to stop entire scan
let aiResults = [];             // accumulated YES results
let aiQueue = [];               // pending image paths
let aiProcessed = 0;
let aiTotal = 0;
let aiYesCount = 0;
let aiConcurrency = 1;
let aiActiveRequests = 0;

/**
 * Toggle AI panel visibility (show/hide on right side).
 */
function toggleAIPanel() {
    const panel = document.getElementById('ai-panel');
    panel.classList.toggle('open');
    // Save preference
    localStorage.setItem('ai-panel-open', panel.classList.contains('open'));
}

// Restore AI panel state on load
(function() {
    if (localStorage.getItem('ai-panel-open') === 'true') {
        document.getElementById('ai-panel')?.classList.add('open');
    }
})();

/**
 * Switch between AI models (SmolVLM / NSFW Filter).
 */
let currentAIModel = localStorage.getItem('ai-model') || 'smolvlm';

function switchAIModel(model) {
    if (currentAIModel === model) return;

    // If AI is running, warn and offer to switch
    if (aiActive) {
        if (!confirm(`Stop ${currentAIModel === 'smolvlm' ? 'SmolVLM' : 'NSFW Filter'} and switch to ${model === 'smolvlm' ? 'SmolVLM' : 'NSFW Filter'}?`)) return;
        // Stop current, then start new
        stopAIAndSwitch(model);
        return;
    }

    applyAIModel(model);
}

async function stopAIAndSwitch(targetModel) {
    const btn = document.getElementById('ai-toggle-btn');
    btn.disabled = true;
    btn.textContent = 'Stopping…';
    try { await fetch('/api/ai/stop?ai=' + currentAIModel, { method: 'POST' }); } catch {}

    aiActive = false;
    document.getElementById('ai-status-badge').className = 'ai-status-badge off';
    document.getElementById('ai-status-badge').textContent = 'OFF';
    applyAIModel(targetModel);
    btn.disabled = false;
    btn.textContent = 'Start AI';
}

function applyAIModel(model) {
    currentAIModel = model;
    localStorage.setItem('ai-model', model);

    // Update tabs
    document.querySelectorAll('.ai-tab').forEach(t => t.classList.toggle('active', t.dataset.ai === model));

    // Update body class for CSS
    document.body.classList.toggle('ai-nsfw', model === 'nsfw');

    // Update button text
    const btn = document.getElementById('ai-toggle-btn');
    if (!aiActive) {
        btn.textContent = 'Start AI';
    }

    // Disable prompt in NSFW mode, enable in smolvlm mode
    onAIModeChange();
}

function getCurrentAI() { return currentAIModel; }

// Initialize on load
document.addEventListener('DOMContentLoaded', () => {
    applyAIModel(currentAIModel);
});

/**
 * Toggle AI service on/off.
 */
async function toggleAI() {
    const btn = document.getElementById('ai-toggle-btn');
    const badge = document.getElementById('ai-status-badge');
    const scanVisibleBtn = document.getElementById('ai-scan-visible-btn');
    const scanAllBtn = document.getElementById('ai-scan-all-btn');
    const vramInfo = document.getElementById('ai-vram-info');

    if (!aiActive) {
        btn.disabled = true;
        btn.textContent = 'Starting…';
        badge.className = 'ai-status-badge loading';
        badge.textContent = 'LOADING';

        try {
            const r = await fetch('/api/ai/start?ai=' + currentAIModel, { method: 'POST' });
            if (!r.ok) {
                const err = await r.text();
                throw new Error(err || 'Failed to start AI');
            }
            const data = await r.json();
            aiActive = true;
            badge.className = 'ai-status-badge on';
            badge.textContent = 'ON';
            btn.textContent = 'Stop AI';
            btn.classList.remove('btn-primary');
            btn.classList.add('btn-ghost');
            scanVisibleBtn.disabled = false;
            scanAllBtn.disabled = false;

            if (data.vram) {
                document.getElementById('ai-vram-used').textContent = data.vram.usedMb;
                document.getElementById('ai-vram-total').textContent = data.vram.totalMb;
                document.getElementById('ai-vram-pct').textContent = data.vram.percent;
                const pct = data.vram.totalMb > 0 ? (data.vram.usedMb / data.vram.totalMb * 100) : 0;
                document.getElementById('ai-vram-bar-fill').style.width = pct + '%';
                vramInfo.classList.remove('hidden');
            }

            showToast('AI vision model loaded successfully', 'ok');
        } catch (e) {
            badge.className = 'ai-status-badge off';
            badge.textContent = 'OFF';
            btn.textContent = 'Start AI';
            showToast('AI start failed: ' + e.message, 'error');
        } finally {
            btn.disabled = false;
        }
    } else {
        // Stop AI — also abort any running scan
        if (aiProcessing) {
            stopAIProcessing();
        }
        btn.disabled = true;
        btn.textContent = 'Stopping…';

        try {
            await fetch('/api/ai/stop?ai=' + currentAIModel, { method: 'POST' });
        } catch { /* ignore */ }

        aiActive = false;
        badge.className = 'ai-status-badge off';
        badge.textContent = 'OFF';
        btn.textContent = 'Start AI';
        btn.classList.add('btn-primary');
        btn.classList.remove('btn-ghost');
        scanVisibleBtn.disabled = true;
        scanAllBtn.disabled = true;
        vramInfo.classList.add('hidden');
        btn.disabled = false;

        showToast('AI vision model unloaded', 'ok');
    }
}

/**
 * Set prompt from a preset button.
 */
function setAIPrompt(text) {
    document.getElementById('ai-prompt').value = text;
}

/**
 * Start an AI scan over images.
 * @param {'visible'|'all'} mode - 'visible' = only images currently in viewport,
 *                                 'all' = all images on the current page.
 */
async function startAIScan(mode) {
    if (!aiActive) {
        showToast('Start the AI model first', 'warn');
        return;
    }

    const prompt = document.getElementById('ai-prompt').value.trim();
    if (!prompt && currentAIModel !== 'nsfw') {
        showToast('Enter a prompt first', 'warn');
        return;
    }

    if (aiProcessing) {
        showToast('A scan is already running', 'warn');
        return;
    }

    // Collect media file cards (images + videos) for AI scanning
    const allCards = document.querySelectorAll('.file-card');
    const mediaCards = [];
    allCards.forEach(card => {
        const path = card.dataset.path;
        if (path) {
            const name = path.toLowerCase();
            if (/\.(jpg|jpeg|png|webp|bmp|gif|mp4|mkv|mov|avi|webm|3gp)$/.test(name)) {
                mediaCards.push(card);
            }
        }
    });

    if (mediaCards.length === 0) {
        showToast('No media files found on this page', 'warn');
        return;
    }

    let targetCards;
    if (mode === 'visible') {
        targetCards = mediaCards.filter(card => {
            const rect = card.getBoundingClientRect();
            return rect.top < window.innerHeight && rect.bottom > 0;
        });
        if (targetCards.length === 0) {
            showToast('No media files visible in viewport', 'warn');
            return;
        }
    } else {
        targetCards = mediaCards;
    }

    // Build queue: { deviceId, path, name }
    aiQueue = targetCards.map(card => {
        const path = card.dataset.path;
        const deviceId = card.dataset.device || currentDevice || '';
        const name = card.dataset.name || path.split('/').pop();
        return { deviceId, path, name };
    });

    aiResults = [];
    aiProcessed = 0;
    aiTotal = aiQueue.length;
    aiYesCount = 0;
    aiStopRequested = false;
    aiSkipCurrent = false;
    aiConcurrency = parseInt(document.getElementById('ai-concurrency').value) || 1;
    aiActiveRequests = 0;
    aiProcessing = true;

    // Show processing panel
    document.getElementById('ai-processing').classList.remove('hidden');
    document.getElementById('ai-results').classList.add('hidden');
    updateAIProgress();

    // Disable scan buttons during scan
    document.getElementById('ai-scan-visible-btn').disabled = true;
    document.getElementById('ai-scan-all-btn').disabled = true;

    // Start concurrent workers
    for (let i = 0; i < aiConcurrency; i++) {
        aiWorker();
    }
}

/**
 * Worker loop — picks next item from queue and processes it.
 */
async function aiWorker() {
    while (aiQueue.length > 0 && !aiStopRequested) {
        const item = aiQueue.shift();
        aiActiveRequests++;

        // Update preview
        document.getElementById('ai-processing-thumb').src =
            `/api/thumb/${encodeURIComponent(item.deviceId)}?path=${encodeURIComponent(item.path)}&frameOverride=-1&qualityOverride=-1`;
        document.getElementById('ai-processing-name').textContent = item.name;

        try {
            const mode = getAIMode();
            const prompt = mode === 'describe' 
                ? 'Describe the image in detail.' 
                : document.getElementById('ai-prompt').value.trim();

            const r = await fetch('/api/ai/query', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    device: item.deviceId,
                    path: item.path,
                    prompt: prompt,
                    mode: mode,
                    ai: currentAIModel
                })
            });

            if (!r.ok) {
                const err = await r.text();
                console.warn('AI query failed for', item.name, err);
            } else {
                const data = await r.json();
                const answer = (data.answer || '');
                const answerLower = answer.toLowerCase();
                
                if (currentAIModel === 'nsfw') {
                    // NSFW: only show tagged (YES) images
                    if (answerLower.startsWith('yes')) {
                        aiYesCount++;
                        aiResults.push({
                            deviceId: item.deviceId, path: item.path, name: item.name,
                            answer: data.answer, raw: data.raw || ''
                        });
                        renderAIResults();
                    }
                } else if (mode === 'yesno') {
                    if (answerLower.startsWith('yes') || answerLower.includes(' yes') || answerLower === 'yes') {
                        aiYesCount++;
                    }
                    aiResults.push({
                        deviceId: item.deviceId, path: item.path, name: item.name,
                        answer: data.answer, raw: data.raw || ''
                    });
                    renderAIResults();
                } else {
                    aiYesCount++;
                    aiResults.push({
                        deviceId: item.deviceId, path: item.path, name: item.name,
                        answer: data.answer, raw: data.raw || ''
                    });
                    renderAIResults();
                }
            }
        } catch (e) {
            console.warn('AI query error for', item.name, e.message);
        }

        aiProcessed++;
        aiActiveRequests--;
        updateAIProgress();

        // Check skip signal
        if (aiSkipCurrent) {
            aiSkipCurrent = false;
            // Just continue to next
        }
    }

    // If all workers done and queue empty
    if (aiActiveRequests <= 0 && aiQueue.length === 0) {
        finishAIScan();
    }
}

/**
 * Update the progress text.
 */
function updateAIProgress() {
    const mode = getAIMode();
    let label;
    if (currentAIModel === 'nsfw') label = 'NSFW found';
    else if (mode === 'yesno') label = 'YES found';
    else label = 'processed';
    document.getElementById('ai-progress-text').textContent =
        `Processing ${aiProcessed}/${aiTotal} — ${aiYesCount} ${label}`;
}

function renderAIResults() {
    const resultsDiv = document.getElementById('ai-results');
    const list = document.getElementById('ai-results-list');
    const countEl = document.getElementById('ai-results-count');
    const titleEl = resultsDiv.querySelector('.ai-results-title');
    const mode = getAIMode();

    resultsDiv.classList.remove('hidden');
    countEl.textContent = aiResults.length;

    if (titleEl) {
        if (currentAIModel === 'nsfw') {
            titleEl.innerHTML = `📋 Results (<span id="ai-results-count">${aiResults.length}</span>)`;
        } else if (mode === 'yesno') {
            titleEl.innerHTML = `✅ YES Results (<span id="ai-results-count">${aiResults.length}</span>)`;
        } else if (mode === 'describe') {
            titleEl.innerHTML = `📝 Descriptions (<span id="ai-results-count">${aiResults.length}</span>)`;
        } else {
            titleEl.innerHTML = `💬 Answers (<span id="ai-results-count">${aiResults.length}</span>)`;
        }
    }

    list.innerHTML = aiResults.map(r => `
        <div class="ai-result-item" onclick="scrollToFile('${r.path.replace(/'/g, "\\'")}')" title="${r.path}">
            <img src="/api/thumb/${encodeURIComponent(r.deviceId)}?path=${encodeURIComponent(r.path)}&frameOverride=-1&qualityOverride=-1" alt="" />
            <div class="ai-result-item-info">
                <div class="ai-result-item-name">${r.name}</div>
                <div class="ai-result-item-path">${r.path}</div>
                <div class="ai-result-item-answer">${r.answer}</div>
            </div>
        </div>
    `).join('');
}

/**
 * Scroll to a file card by path.
 */
function scrollToFile(filePath) {
    const cards = document.querySelectorAll('.file-card');
    for (const card of cards) {
        if (card.dataset.path === filePath) {
            card.scrollIntoView({ behavior: 'smooth', block: 'center' });
            card.classList.add('selected');
            setTimeout(() => card.classList.remove('selected'), 2000);
            break;
        }
    }
}

/**
 * Skip the current image being processed.
 */
function skipCurrentImage() {
    aiSkipCurrent = true;
}

/**
 * Stop the entire AI scan.
 */
function stopAIProcessing() {
    aiStopRequested = true;
    aiQueue = [];
    finishAIScan();
}

/**
 * Clean up after scan finishes.
 */
function finishAIScan() {
    aiProcessing = false;
    aiActiveRequests = 0;

    document.getElementById('ai-processing').classList.add('hidden');
    document.getElementById('ai-scan-visible-btn').disabled = false;
    document.getElementById('ai-scan-all-btn').disabled = false;

    if (aiResults.length > 0) {
        document.getElementById('ai-results').classList.remove('hidden');
        showToast(`Scan complete: ${aiYesCount} YES out of ${aiProcessed} images`, 'ok');
    } else {
        showToast(`Scan complete: 0 YES out of ${aiProcessed} images`, 'warn');
    }
}

/**
 * Clear the YES results list.
 */
function clearAIResults() {
    aiResults = [];
    document.getElementById('ai-results').classList.add('hidden');
    document.getElementById('ai-results-list').innerHTML = '';
    document.getElementById('ai-results-count').textContent = '0';
}

// ── AI Queue System ─────────────────────────────────────────────────────────
let aiQueueItems = [];

function onAIModeChange() {
    const mode = document.getElementById('ai-mode')?.value || 'yesno';
    const promptInput = document.getElementById('ai-prompt');
    if (mode === 'describe') {
        promptInput.disabled = true;
        promptInput.placeholder = 'Auto: "Describe the image in detail."';
        promptInput.value = '';
    } else {
        promptInput.disabled = false;
        promptInput.placeholder = 'e.g. Does this image contain a cat?';
    }
}

function getAIMode() {
    return document.getElementById('ai-mode')?.value || 'yesno';
}

function addToAIQueue(event, filePath, fileName) {
    event.stopPropagation();
    const card = event.target.closest('.file-card');
    const deviceId = card?.dataset?.device || currentDevice || offlineDeviceId;

    if (aiQueueItems.some(i => i.deviceId === deviceId && i.path === filePath)) {
        showToast('Already in queue', 'warn', 2000);
        return;
    }

    aiQueueItems.push({ deviceId, path: filePath, name: fileName });
    event.target.textContent = '✅';
    setTimeout(() => { event.target.textContent = '🤖'; }, 800);
    updateAIQueueCount();
}

function removeFromAIQueue(index) {
    aiQueueItems.splice(index, 1);
    updateAIQueueCount();
}

function clearAIQueue() {
    aiQueueItems = [];
    updateAIQueueCount();
}

function updateAIQueueCount() {
    const countEl = document.getElementById('ai-queue-count');
    const listEl = document.getElementById('ai-queue-list');
    const panel = document.getElementById('ai-queue-panel');
    if (!countEl || !listEl || !panel) return;
    countEl.textContent = aiQueueItems.length;
    if (aiQueueItems.length === 0) {
        panel.classList.add('hidden');
    } else {
        panel.classList.remove('hidden');
        listEl.innerHTML = aiQueueItems.map((item, i) =>
            `<div class="ai-queue-item">
                <span class="ai-queue-item-name" title="${escHtml(item.path)}">${escHtml(item.name)}</span>
                <button class="btn btn-ghost btn-xs" onclick="removeFromAIQueue(${i})">✕</button>
            </div>`
        ).join('');
    }
}

function processAIQueue() {
    if (aiQueueItems.length === 0) return;
    aiQueue = [...aiQueueItems];
    aiProcessed = 0;
    aiYesCount = 0;
    aiStopRequested = false;
    aiActiveRequests = 0;
    aiResults = [];

    document.getElementById('ai-results').classList.add('hidden');
    document.getElementById('ai-results-list').innerHTML = '';
    document.getElementById('ai-results-count').textContent = '0';

    clearAIQueue();

    const concurrency = parseInt(document.getElementById('ai-concurrency')?.value || '1', 10);
    document.getElementById('ai-processing').classList.remove('hidden');

    for (let i = 0; i < concurrency; i++) aiWorker();
}


// ── Remote PowerShell terminal (Index page, Windows devices) ───────────────
let idxShellHistory = [];

function cleanPsOutput(o) {
    let t = (o || '').replace(/<Objs[\s\S]*?<\/Objs>/g, '');
    return t.replace(/^#< CLIXML\r?\n/, '');
}

function closeShellBackToFeatures() {
    document.getElementById('shell-panel')?.classList.add('hidden');
    showFeatures();
}

function appendIdxShell(text) {
    const out = document.getElementById('idx-shell-output');
    out.textContent += text;
    out.scrollTop = out.scrollHeight;
}

function renderIndexShellHistory() {
    const out = document.getElementById('idx-shell-output');
    out.textContent = '';
    for (const h of idxShellHistory) {
        out.textContent += `PS> ${h.cmd}\n`;
        if (h.output) out.textContent += cleanPsOutput(h.output) + '\n';
    }
    out.scrollTop = out.scrollHeight;
}

async function runIndexShell() {
    const input = document.getElementById('idx-shell-input');
    const btn = document.getElementById('idx-shell-run-btn');
    const cmd = (input?.value || '').trim();
    if (!cmd || !currentDevice) return;
    appendIdxShell(`\nPS> ${cmd}\n`);
    input.value = '';
    if (btn) { btn.disabled = true; btn.textContent = '⏳'; }
    try {
        const res = await fetch('/api/shell/run', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ device: currentDevice, command: cmd, timeout: 60 })
        });
        const data = await res.json().catch(() => ({ success: false, message: 'Unreadable response' }));
        if (data.success && typeof data.output === 'string') {
            const clean = cleanPsOutput(data.output);
            appendIdxShell(clean + '\n');
            idxShellHistory.push({ cmd, output: data.output });
        } else {
            appendIdxShell(`[${data.message || 'timed out'}]\n`);
            idxShellHistory.push({ cmd, output: '[' + (data.message || 'timeout') + ']' });
        }
    } catch (e) {
        appendIdxShell('[request failed: ' + e.message + ']\n');
    } finally {
        if (btn) { btn.disabled = false; btn.textContent = '▶ Run'; }
        input?.focus();
    }
}

document.addEventListener('DOMContentLoaded', () => {
    document.getElementById('idx-shell-input')?.addEventListener('keydown', e => {
        if (e.key === 'Enter') runIndexShell();
    });
});

// ── Database feature (Windows devices) ─────────────────────────────────
function closeDatabaseBackToFeatures() {
    document.getElementById('database-panel')?.classList.add('hidden');
    showFeatures();
}
function closeDbBrowse() {
    document.getElementById('idx-db-browse')?.classList.add('hidden');
    document.getElementById('idx-db-browse-grid')?.replaceChildren();
}
async function refreshDatabase() {
    const pc = currentDevice;
    const listEl = document.getElementById('idx-db-list');
    const storageEl = document.getElementById('idx-db-storage');
    if (!pc) return;
    if (listEl) listEl.innerHTML = '<div style="color:var(--text2)">Loading database…</div>';
    if (storageEl) storageEl.innerHTML = '';
    try {
        const r = await fetch(`/api/windows-database/${encodeURIComponent(pc)}/stats`);
        if (!r.ok) throw new Error(await r.text());
        const data = await r.json();
        // support both wrapped and direct
        const raw = data.value || data.result || data;
        const devices = raw.devices || raw.items || [];
        const total = raw.total ?? 0;
        const partitions = raw.partitions || [];
        if (storageEl) {
            const avail = partitions.reduce((a,p)=>a+(p.free||0),0);
            const used = partitions.reduce((a,p)=>a+(p.used||0),0);
            storageEl.innerHTML = `
                <div style="font-weight:600;margin-bottom:6px">💾 PC Storage — DB total ${(total/1024/1024).toFixed(1)} MB · Partitions ${partitions.length}</div>
                <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:8px">
                    ${partitions.map(p=>`<span style="font-size:0.8rem;background:var(--surface);border:1px solid var(--glass-border);padding:4px 8px;border-radius:6px">${p.name} total ${(p.total/1024/1024/1024).toFixed(1)} GB · free ${(p.free/1024/1024/1024).toFixed(1)} GB</span>`).join('') || '<span style="font-size:0.8rem;color:var(--text2)">No partitions</span>'}
                </div>
                <div style="height:14px;display:flex;border-radius:4px;overflow:hidden;border:1px solid var(--glass-border)">${partitions.map(p=>`<div title="${p.name} used ${(p.used/1024/1024/1024).toFixed(1)} GB" style="flex:${p.used};background:#0a5c36"></div><div style="flex:${p.free};background:#2a2a2a"></div>`).join('')}</div>
                <div style="font-size:0.75rem;color:var(--text2);margin-top:6px">Dark green = used (non-DB) · grey = free · per-device colors on top (hover in detailed view)</div>`;
        }
        if (!devices.length) {
            listEl.innerHTML = '<div style="color:var(--text2);padding:12px;border:1px dashed var(--glass-border);border-radius:8px">No per-device DB yet. Plug a phone to this PC and run an OnConnect Windows action.</div>';
            return;
        }
        listEl.innerHTML = devices.map(d=>{
            const name = d.name||d.id||'device';
            const id = d.id||d.name||'';
            const sz = (d.size||0);
            return `<div style="display:flex;align-items:center;gap:10px;padding:10px;border:1px solid var(--glass-border);border-radius:8px;background:var(--surface)">
                <span style="font-size:1.2rem">📱</span>
                <div style="flex:1;min-width:0">
                    <div style="font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${escHtml(name)}</div>
                    <div style="font-size:0.75rem;color:var(--text2)">${escHtml(id)} · ${(sz/1024/1024).toFixed(1)} MB</div>
                </div>
                <button class="btn btn-ghost btn-sm" onclick="exploreDb('${escHtml(id).replace(/'/g,"\\'")}')">Explore</button>
                <div style="display:flex;gap:4px">
                    <button class="btn btn-primary btn-sm" onclick="addToServerCache('${escHtml(id).replace(/'/g,"\\'")}','lowest')">Add to server cache</button>
                    <button class="btn btn-ghost btn-sm" title="Options" onclick="toggleDbOptions(this)">▾</button>
                </div>
            </div>`;
        }).join('');
    } catch (e) {
        if (listEl) listEl.innerHTML = `<div style="color:var(--red)">Failed: ${escHtml(e.message)}</div>`;
    }
}
function exploreDb(deviceId) {
    const pc = currentDevice;
    const browseEl = document.getElementById('idx-db-browse');
    const grid = document.getElementById('idx-db-browse-grid');
    const pathEl = document.getElementById('idx-db-browse-path');
    if (!browseEl||!grid) return;
    browseEl.classList.remove('hidden');
    pathEl.textContent = deviceId + ' /';
    grid.innerHTML = '<div style="color:var(--text2)">Loading…</div>';
    fetch(`/api/windows-database/${encodeURIComponent(pc)}/browse`, { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({deviceId, path: ''}) })
        .then(r=>r.json()).then(data=>{
            const raw = data.value||data.result||data;
            grid.innerHTML = `<pre style="font-size:0.75rem;white-space:pre-wrap">${escHtml(JSON.stringify(raw, null, 2))}</pre>`;
        }).catch(e=>{ grid.innerHTML = `<span style="color:var(--red)">${escHtml(e.message)}</span>`; });
}
function toggleDbOptions(btn) {
    const opts = ['Lowest quality (thumbnail only, frame 0.5)','Config in Windows Client','Custom'];
    const menu = document.createElement('div');
    menu.style.cssText='position:absolute;background:var(--surface);border:1px solid var(--glass-border);border-radius:6px;padding:6px;z-index:999;box-shadow:0 4px 12px rgba(0,0,0,0.3)';
    menu.innerHTML = opts.map(o=>`<div style="padding:6px 10px;cursor:pointer" onmouseover="this.style.background='var(--surface2)'" onmouseout="this.style.background=''">${o} ${o.includes('Config')?'<span title="Windows global config: thumbQuality etc." style="margin-left:6px;cursor:help">ℹ️</span>':''}</div>`).join('');
    document.body.appendChild(menu);
    const rect = btn.getBoundingClientRect();
    menu.style.left = (rect.left - 120) + 'px';
    menu.style.top = (rect.bottom + 4) + 'px';
    const close = (ev)=>{ if(!menu.contains(ev.target)&&ev.target!==btn){ menu.remove(); document.removeEventListener('click', close); } };
    setTimeout(()=>document.addEventListener('click', close), 0);
}
function addToServerCache(deviceId, mode) {
    const pc = currentDevice;
    showToast('Add to server cache started for ' + deviceId + ' ('+mode+')','ok');
    fetch(`/api/windows-database/${encodeURIComponent(pc)}/add`, { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({deviceId, mode}) }).catch(()=>{});
}
