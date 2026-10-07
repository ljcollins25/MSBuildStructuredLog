# Hosting StructuredLogViewer.Browser in Ref12/webbox at `/binlog/`

The MSBuild Structured Log Viewer (Avalonia) running in the browser on .NET WebAssembly, built workload-free and staged as a static site. **Preview:** it opens a .binlog by file picker, drag and drop or `?url=`, shows the tree, search, node details, and the embedded source files with find in files. Timeline, Tracing and the graph views are not there yet.

**Big logs do not fit yet.** The page says so. The wasm32 heap runs out above roughly 50 MB on disk (about 200 MB decompressed); a 79 MB dotnet/runtime log fails at about 60% (see docs/wasm/measurements.md).

- **Fork / ref to pin:** `ljcollins25/MSBuildStructuredLog`, branch `wasm`, at the commit you choose (put the sha here when you pin it; bump it deliberately). Pin a commit at or after the shared-UI restructure: the page now runs the real `StructuredLogViewer.Avalonia.Core` viewer (same BuildControl as the desktop app), not a separate hand-written UI. Screenshots: `docs/wasm/shared-ui-*.png`.
- **Build command** (ubuntu-latest, .NET 10 SDK, node 22; no workload):
  `src/StructuredLogViewer.Browser/build-static.sh <outdir> --target=pages` (or `--target=cloudflare`)
- **Output folder:** `<outdir>`: the site root (`index.html`, `staging.json`, hashed `main.*.js`, `_framework/`, `vendor/`). Copy its *contents* to `_site/binlog/`. About 20 MiB on disk for Pages.
- **Subpath:** every URL is relative (`<base href="./">`, relative imports), so it works at `/binlog/` and `/webbox/binlog/`. No service worker.
- **Pages:** no Content-Encoding is available, so `--target=pages` keeps `.br` siblings of the `.wasm/.dll/.dat` files and `main.js` decodes them in the page (brotli-dec-wasm in `vendor/`, MIT OR Apache-2.0); decoded files are cached in the Cache API. This is the same approach as ILSpy.Browser.
- **Cloudflare:** `--target=cloudflare` ships no `.br`/`.gz`, fails if a file is over 25 MiB (such a file would ship as `.br` only, decoded in the page), and `--headers=<_headers file>` appends this app's rules. No COOP/COEP needed (no threads).
- **Opening a log from another site with `?url=`:** the server must allow CORS (GitHub Actions and Azure DevOps artifact URLs generally need a proxy or a signed URL that allows it; same-origin files always work).

## Smoke test

`cd src/StructuredLogViewer.Browser/tools && npm ci && npx playwright install chromium && node e2e.mjs <outdir> <small.binlog> --prefix=/webbox/binlog --target=CoreCompile --source=.cs`

It serves the site under `/webbox/binlog/` like Pages (gzip text, no encoding for binaries), drops the binlog, checks the tree and the size warning, searches, selects a hit, opens an embedded source file, repeats with `?url=`, and fails on any console error or 4xx.

## Ready-to-paste: `pages.yml`

```yaml
      # --- binlog (MSBuild Structured Log Viewer in the browser, .NET WebAssembly): pinned commit of the fork, no workload ---
      - name: Check out MSBuildStructuredLog
        uses: actions/checkout@v4
        with:
          repository: ljcollins25/MSBuildStructuredLog
          ref: <pinned sha>
          path: binlog-src
      - name: Build binlog
        run: |
          binlog-src/src/StructuredLogViewer.Browser/build-static.sh "$PWD/_binlog_pages" --target=pages
          binlog-src/src/StructuredLogViewer.Browser/build-static.sh "$PWD/_binlog_cf" --target=cloudflare --base=/binlog --headers="$PWD/_binlog_headers"
      # --- end binlog ---
```

Stage with `mkdir -p _site/binlog && cp -r _binlog_pages/. _site/binlog/` (Pages) and `mkdir -p _cf_site/binlog && cp -r _binlog_cf/. _cf_site/binlog/; cat _binlog_headers >> _cf_site/_headers` (Cloudflare, after the other stages wrote `_headers`). Exclude `/binlog-src`, `/_binlog_pages` and `/_binlog_cf` from the repo rsync copies.
