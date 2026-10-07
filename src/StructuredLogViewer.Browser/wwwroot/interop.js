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

export async function fetchBytes(url) {
    const response = await fetch(url);
    if (!response.ok) {
        throw new Error('HTTP ' + response.status + ' for ' + url);
    }

    return { bytes: new Uint8Array(await response.arrayBuffer()) };
}
