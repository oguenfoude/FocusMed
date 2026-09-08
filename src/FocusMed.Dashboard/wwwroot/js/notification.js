// FocusMed arrival chime. Played by MainLayout when a new DicomImage row appears
// (every C-STORE and every print N-SET inserts one). Mute persists in localStorage.
const MUTE_KEY = 'focusmed-sound-muted';
const LAST_KEY = 'focusmed-last-chime';
let audio = null;

function getAudio() {
    if (!audio) {
        audio = new Audio('/sounds/notification.wav');
        audio.preload = 'auto';
    }
    return audio;
}

function storageGet(key) {
    try { return window.localStorage.getItem(key); }
    catch { return null; }
}

function storageSet(key, value) {
    try {
        if (value === null) window.localStorage.removeItem(key);
        else window.localStorage.setItem(key, value);
    } catch { /* private mode — ignore */ }
}

export function isSoundMuted() {
    return storageGet(MUTE_KEY) === '1';
}

export function setSoundMuted(muted) {
    storageSet(MUTE_KEY, muted ? '1' : null);
}

export function playNotificationSound() {
    if (isSoundMuted()) return false;
    // Cross-tab debounce: two open tabs would otherwise chime twice.
    const now = Date.now();
    const last = Number(storageGet(LAST_KEY) || 0);
    if (now - last < 3000) return false;
    storageSet(LAST_KEY, String(now));
    try {
        const a = getAudio();
        a.currentTime = 0;
        const p = a.play();
        if (p && typeof p.catch === 'function') p.catch(() => {});
        // Test hook: Playwright verifies arrival end-to-end via this timestamp.
        window.__focusmedLastChime = now;
        return true;
    } catch { return false; }
}
