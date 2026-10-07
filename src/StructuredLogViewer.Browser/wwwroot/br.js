// Brotli decoding in the page. GitHub Pages cannot send Content-Encoding for precompressed files, so
// tools/stage.mjs ships the big binaries as <file>.br (opaque bytes to the server) and main.js fetches
// and decodes them here with a ~200 KB WebAssembly decoder (brotli-dec-wasm, MIT OR Apache-2.0, copied
// into ./vendor by the staging step; browsers have no DecompressionStream('brotli')).
let decoder = null;
async function load() {
    const mod = await import('./vendor/brotli_dec_wasm.js');
    const response = await fetch(new URL('./vendor/brotli_dec_wasm_bg.wasm', import.meta.url));
    if (!response.ok) {
        throw new Error('brotli decoder: HTTP ' + response.status);
    }
    await mod.default({ module_or_path: new Uint8Array(await response.arrayBuffer()) });
    return mod;
}
export async function brotliDecode(bytes) {
    const mod = await (decoder ||= load().catch(error => { decoder = null; throw error; }));
    return mod.decompress(bytes);
}
