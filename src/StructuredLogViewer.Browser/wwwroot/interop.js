export function getQuery() {
    return globalThis.location.search;
}

export function report(message) {
    console.log(message);
    document.title = message;
}

export function now() {
    return globalThis.performance.now();
}

export async function fetchBytes(url) {
    const response = await fetch(url);
    if (!response.ok) {
        throw new Error('HTTP ' + response.status + ' for ' + url);
    }

    return { bytes: new Uint8Array(await response.arrayBuffer()) };
}
