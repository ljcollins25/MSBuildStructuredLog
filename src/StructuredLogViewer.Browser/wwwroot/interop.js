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

// Synchronous ranged reads for the paged reader: ReadBuild is synchronous, so the bytes must be available without await.
// Main-thread synchronous XHR cannot use responseType 'arraybuffer', so the body is read as a binary string.
const sources = new Map();
let nextSource = 1;

export function openSource(urlOrFile) {
    const url = typeof urlOrFile === 'string' ? urlOrFile : URL.createObjectURL(urlOrFile);
    return openSourceUrl(url);
}

export function openSourceUrl(url) {
    const xhr = new XMLHttpRequest();
    xhr.open('GET', url, false);
    xhr.setRequestHeader('Range', 'bytes=0-0');
    xhr.overrideMimeType('text/plain; charset=x-user-defined');
    xhr.send();
    let length = -1;
    const cr = xhr.getResponseHeader('Content-Range');
    if (xhr.status === 206 && cr) length = parseInt(cr.split('/')[1], 10);
    else if (xhr.status === 200) length = xhr.responseText.length; // blob: URLs answer the whole body
    const id = nextSource++;
    sources.set(id, { url, length });
    return id;
}

export function sourceLength(id) { return sources.get(id).length; }

export function readSource(id, position, count) {
    const s = sources.get(id);
    const xhr = new XMLHttpRequest();
    xhr.open('GET', s.url, false);
    xhr.setRequestHeader('Range', 'bytes=' + position + '-' + (position + count - 1));
    xhr.overrideMimeType('text/plain; charset=x-user-defined');
    xhr.send();
    const t = xhr.responseText;
    const bytes = new Uint8Array(t.length);
    for (let i = 0; i < t.length; i++) bytes[i] = t.charCodeAt(i) & 0xff;
    return bytes;
}

export function pendingFile() { const f = globalThis.binlogPendingFile; return f ? f.name : ''; }
export function openPendingSource() { return openSource(globalThis.binlogPendingFile); }
