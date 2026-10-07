# Hand-off (wasm viewer)

Branch: wasm-viewer work is on the exchange branch. Milestones: gap analysis 9810954b, browser spike d235de27 / cb9cce8a, core replay change e782cfe98.

Done: docs/wasm/gap-analysis.md, src/StructuredLogViewer.Browser (spike: ?url= load, progress, stats), docs/wasm/measurements.md, BinLogReader single-thread replay dispatches as it reads.
Findings: upstream already has PlatformUtilities.HasThreads guards in BinLogReader, Build, Search, SearchIndex, NodeQueryMatcher; AsyncBufferedReadStream is not referenced anywhere (nothing to bypass). ProjectImportsCollector.Close() waits on a Task; check it only matters when writing.
Remaining plan: (1) headless entry point (stream in, Build out, progress, no UI types) and a Stream-based reader start position; (2) reading parity in the browser head: tree, search, node details, archive files, Files and Find in Files, then Timeline; (3) memory: lazy big strings, re-measure heap per percent; (4) static deploy under /binlog/ with build-static.sh and WEBBOX.md (see ljcollins25/ILSpy branch browser, ILSpy.Browser/build-static.sh); (5) phase 3 docs/wasm/mcp.md.
Tests: src/StructuredLogger.Tests targets net472 only, so it cannot run on this Linux runner (needs mono).
Restart after a runner change: python3 $HEXAD_SCRATCH/serve.py in pub/wwwroot (port 8080), cdp.mjs headless Chrome driver in $HEXAD_SCRATCH (not committed; scratch files are lost). Big log: scripts/fetch-binlogs.sh from branch hexad/s-20261006-065706-2d4b, build 1621702.
Cautions: BinLogReader.cs and other core files use CRLF and a BOM; edit them bytewise (python) to keep diffs minimal.

## Update (preview deployable, 9ce4bf5b8)
Done: BinlogDocument (headless Stream->Build, progress, search, details, files, find in files), MainView (picker, drop, ?url=, tree, search results, details, Files tab, size warning), build-static.sh + tools/stage.mjs + WEBBOX.md, tools/e2e.mjs (passes under /webbox/binlog on the small StructuredLogger binlog).
Gotchas: System.Text.Json reflection is disabled in the wasm build (hand-written JSON in MainView). Task name clashes with StructuredLogger.Task (alias in MainView). Skia needs WasmBuildNative. Static-site dir under src/StructuredLogViewer.Browser/bin is ignored.
Next: Timeline view (model in StructuredLogViewer.Core/Timeline), preprocessed view in the UI (BinlogDocument.GetPreprocessedText exists, not wired), copy/context menus, syntax highlighting (AvaloniaEdit), phase 3 docs/wasm/mcp.md. Restart: none needed (no servers).
