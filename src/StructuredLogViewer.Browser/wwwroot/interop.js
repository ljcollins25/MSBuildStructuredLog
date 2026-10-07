export function getQuery() {
    return globalThis.location.search;
}

export function getHref() {
    return globalThis.location.href;
}

export function now() {
    return globalThis.performance.now();
}

export function report(message) {
    console.log(message);
    document.title = message;
}

export function pickFile() {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.binlog,.zip,.buildlog,.xml';
    input.onchange = () => {
        if (input.files.length > 0) {
            globalThis.binlogOpenFile(input.files[0]);
        }
    };
    input.click();
}

// start < 0: plain GET. end is inclusive. fetch() itself failing (CORS, network, mixed content) is reported as 'NETWORK'.
export async function fetchRange(url, start, end) {
    let response;
    try {
        response = await fetch(url, start >= 0 ? { headers: { Range: 'bytes=' + start + '-' + end } } : {});
    } catch {
        throw new Error('NETWORK');
    }

    const bytes = new Uint8Array(await response.arrayBuffer());
    const match = /\/(\d+)$/.exec(response.headers.get('Content-Range') || '');
    return { status: response.status, bytes, total: match ? Number(match[1]) : -1, contentType: response.headers.get('Content-Type') || '' };
}

// localStorage, used by BrowserSettingsStore (null when the key is missing or storage is blocked)
export function storageGet(key) {
    try { return globalThis.localStorage.getItem(key); } catch { return null; }
}

export function storageSet(key, value) {
    try { globalThis.localStorage.setItem(key, value); } catch { /* storage blocked or full: settings just do not persist */ }
}

// Save: a plain browser download of a text file
export function downloadText(name, text) {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    globalThis.binlogLastDownload = { name, length: text.length };
}

export function prefersDark() {
    return !!globalThis.matchMedia?.('(prefers-color-scheme: dark)').matches;
}

// Save Log As: a plain browser download of the bytes
export function downloadBytes(name, bytes) {
    const url = URL.createObjectURL(new Blob([bytes], { type: 'application/octet-stream' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    globalThis.binlogLastDownload = { name, length: bytes.length };
}

export function openUrl(url) {
    globalThis.open(url, '_blank', 'noopener');
}
