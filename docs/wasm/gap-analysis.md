# Binlog viewer in the browser: gap analysis (phase 1)

Method: static comparison of `src/StructuredLogViewer` (WPF) and `src/StructuredLogViewer.Avalonia` at upstream main (dd885a9e), plus greps of the shared projects for browser-hostile APIs. Nothing was built or run for this document yet; the effort figures are estimates (S = under 1 day, M = 1 to 3 days, L = a week or more).

## 1. WPF vs Avalonia feature table

The Avalonia viewer is a close port of the WPF one (BuildControl.xaml.cs is 3248 lines vs 3474). The gaps are the specialised views, not the core tree.

| Feature | Avalonia | Evidence | Effort to close |
|---|---|---|---|
| Log tree, node templates, icons, expand/collapse, lazy children | present | Controls/BuildControl, NodeTemplateConverters | none |
| Search Log tab (search syntax, results tree, typing-debounce, `$task` and `under()` etc.) | present | SearchAndResultsControl, TypingConcurrentOperation | none; engine needs threading work (section 2) |
| Node detail views (properties, items, metadata, message/error text, build summary) | present | BuildControl, Properties and items tab | S: check visual parity |
| Properties and items tab (per-project, evaluation) | present | tab exists in both | S |
| Preprocessed view and source files from the embedded archive | present | ArchiveFileResolver, PreprocessedFileManager, TextViewerControl (AvaloniaEdit) | S in the UI. The resolver is on the shared core. Needs the Files tab to work from memory, not disk |
| Files tab and Find in Files | present | filesTab, findInFilesTab (hidden until archive loaded) | S |
| Copy commands, context menus | partial | about 56 menu references vs 62 in WPF. Clipboard goes through Avalonia, not Win32 | S to M: audit item by item; "Open in explorer", "Open with default app", "Copy file" need browser equivalents |
| Favorites tab | present | favoritesTab | none |
| **Timeline** | **missing** | TabItem is commented out in BuildControl.xaml (line 86). WPF TimelineControl is 425 lines | M: the model is in StructuredLogViewer.Core/Timeline, so only the view is needed (custom-drawn canvas; Avalonia Canvas/DrawingContext port) |
| **Tracing (chrome-trace style view)** | **missing** | no TracingControl. WPF 1490 lines | L: a large hand-drawn control with zoom, scroll, and selection |
| **Project References, Targets, NuGet, Properties graphs (MSAGL)** | **missing** | Avalonia BuildControl has only 3 mentions of "graph". WPF GraphControl is 868 and GraphHostControl 414 lines | L: MSAGL layout is managed so should run. The WPF rendering has to be redone. Reasonable to defer |
| Settings (theme, font size, recent files, MSBuild path) | partial | SettingsService is on the core and writes to disk | S: back it by localStorage (JS interop) |
| Open binlog / zip / xml / buildlog | partial | MainWindow.xaml.cs handles extensions via FileTypes.cs. Opens from a path on disk | M: needs the stream-based entry point (section 2) and the browser file picker |
| Open from URL | missing | not in either viewer for http URLs | S in the web head (fetch). Memory is the issue |
| Drag and drop | partial | wired (Drop mentions in MainWindow) | S: works through Avalonia browser's storage items; verify |
| Build solution/project, rebuild | present but not applicable | HostedBuild runs msbuild as a Process | drop in the web head (not possible in a browser) |
| Redact secrets | present | RedactInputControl, BinlogRedactor in StructuredLogger.Utils | M: depends on the SensitiveDataDetector package and file I/O; probably omit at first |
| Save log as / Reload / Recent logs | partial | file-path based | M: Save becomes a browser download; Reload and recent need a stored handle or the URL |
| Statistics, About, Search syntax help, Start page | present | MainWindow | S: Start page needs the web welcome text |
| Single instance / file associations / macOS bundle helpers | present, desktop only | SingleGlobalInstance, MacOsEnvironmentExporter | drop in the web head |
| Keyboard navigation, Ctrl+F in tree, hyperlinks between nodes, go-to-definition in source | present | NavigationHelper, NodeHyperlinkControl, ImportLinkHighlighter | S: verify |

## 2. What blocks a browser build

Both viewers use `net10.0`. The core libraries multi-target `netstandard2.0;net10.0`, which is good: browser-wasm uses the net10 build.

### File system and dialogs
- 32 files across StructuredLogger, StructuredLogViewer.Core, Utils and BinlogMcp use `File.`, `Directory.` or `FileStream`. In browser-wasm the file system is an in-memory Emscripten MEMFS: it works, but it is volatile and counts against the same heap. Most uses are fine (temp files, reading by path).
- The central need is a **Stream-based load**. `BinLogReader`/`BinaryLog` and the `Serialization.Read` entry points need to be driven from a `Stream`, not a path. This looks mostly there; the viewer's load path in MainWindow/BuildControl goes through a file path and needs a new overload (M).
- Dialogs: DialogService in Core, and the Avalonia MainWindow, use the desktop file pickers. Avalonia.Browser provides `StorageProvider` (File System Access API or an input element); `OpenFilePickerAsync` works but returns a stream, not a path. Source-file lookup on the local disk (LocalSourceFileResolver) has no meaning in the browser: only the archive resolver applies.
- `SettingsService` writes to `%AppData%`, and uses a Mutex/NamedPipe in SingleGlobalInstance: replace with localStorage or in-memory (S).

### Threading (single-threaded wasm, no `wasm-threads`)
Hot spots found:
- `StructuredLogger/BinaryLogger/AsyncBufferedReadStream.cs:170`: a `Task.Run` prefetch with a blocking hand-off. **It will deadlock or throw on a single thread.** Must be bypassed in the browser (use a plain buffered stream).
- `Search/SearchIndex.cs` (lines 95, 131, 301, 327): producer Task.Run + Parallel.For/ForEach. `Search/NodeQueryMatcher.cs` (432 to 456): Task.Run and Parallel.ForEach. `Search/Search.cs:132`: Task.Run. `ObjectModel/TreeNode.cs:650`: Parallel.ForEach. `ObjectModel/Build.cs:272` and `Utilities.cs:73`: TPLTask.Run for background work.
- Parallel.For/ForEach run sequentially on one thread and are OK; the dangers are `.Wait()`, `.Result`, and `Task.WaitAll` on a Task.Run task (a blocking wait on the only thread = deadlock or PlatformNotSupportedException). To audit. No `new Thread` or `ThreadPool` use was found.
- The viewer's UI layer (`BuildControl`, `MainWindow`, `TypingConcurrentOperation`, `SettingsService`, `HostedBuild`) also uses Task.Run for loading and searching; that is fine if awaited and not blocked on.
- Consequence: a long parse or search blocks the UI. Options: (a) yield periodically (`await Task.Yield()`/setTimeout) with progress, (b) the wasm-threads setup with SharedArrayBuffer (needs COOP/COEP headers; poorly supported in Avalonia.Browser), (c) a separate worker running a headless parse (.NET WebWorker through JS interop), which is the better fit for big logs. Phase 2 starts with (a) and then tries (c).

### Process, registry, Windows-only
- `System.Diagnostics.Process`: DotnetUtilities, FileExplorerHelper, HostedBuild (Core), plus MainWindow, BuildControl, TextViewerControl, MacOs* (Avalonia). All are about launching msbuild, explorer, or the default editor. Not available in the browser: stub or drop (S to M, mostly `#if`/DI).
- Registry: only a string match for `Registry:` property functions in ParsedExpression.cs, not an API use. No blocker.
- `Marshal.SizeOf(typeof(Guid))` in BuildEventArgsReader.cs and `Marshal` in BinlogMcp/BinlogCache.cs: works on wasm; no P/Invoke (`DllImport`) found in these projects.
- Windows-only code lives in the WPF head only. AutomaticGraphLayout.Drawing (MSAGL, referenced by Core) is managed. Needs a check that it links; it is only needed for the graph views.
- Microsoft.Build.Framework and Microsoft.Build.Utilities.Core are managed assemblies: OK, but they add size to the download.

### Memory
- wasm32 gives a 2 GB hard address-space cap (often about 1 to 1.5 GB usable in practice, and a browser tab can fail earlier), with the GC heap, MEMFS and the input all in the same space. Binlogs are gzip-compressed; WPF users load multi-GB logs with server GC. **Large logs (hundreds of MB decompressed) are the real limit** of this project, not the UI.
- The object model (strings, TreeNode per message) is heavy: typically several times the decompressed size. A binlog of 50 MB on disk is realistic, and 200 MB probably fails.
- Mitigations, in order: stream and never hold the compressed file twice (do not copy the fetch response into a byte[] and then a MemoryStream); load without the search index until asked; skip embedded files until requested; the string deduplication already in the reader helps; filter at load (there is a filtered-binlog-replay path upstream, see commit 208d4610). Possible later: do the parse and queries on a server (the MCP server side), and keep the tab as a viewer. This will be measured in phase 2 with a small and a big log.

### GZip/deflate
- `System.IO.Compression` (GZipStream, DeflateStream, ZipArchive) is part of the browser-wasm runtime: the native zlib is linked in `dotnet.native.wasm`, and ZipArchive is managed. It works in the interpreter and AOT builds. Binlog files are gzip and the embedded archive is a zip: both should work. Expect it to be slow in the interpreter (decompress 100s of MB). To confirm by test in phase 2; the browser's native `DecompressionStream` via JS interop is the fallback.

### BinlogMcp reuse (phase 3 preview)
- `BinlogMcp` targets net10.0 and depends on `Microsoft.Extensions.Hosting` and `ModelContextProtocol` (stdio server). The tool classes (BinlogTools.*.cs) use file paths and a `BinlogCache` keyed by path. Plan: split a transport-free library (tools + cache over streams/in-memory) and keep the existing console host over it. Hosting and the MCP SDK's transports may not work in the browser, so the WASM side would call the tools directly and implement the JSON-RPC framing itself. Detail in docs/wasm/mcp.md in phase 3.

## 3. Proposed plan

1. **Spike (1 to 2 days)**: workload-free Avalonia.Browser head that loads a tiny binlog from a `Stream` and shows the tree; confirm the core assemblies load (Microsoft.Build.Framework, GZip, ZipArchive). This answers most unknowns.
2. **Core changes** (M): stream-based load, a switch to turn off AsyncBufferedReadStream and blocking waits, a settings/dialog/process abstraction (stubs in the web head).
3. **Viewer parity for reading** (M): search, tree, node details, preprocessed/source from the archive, Files/Find in Files. **Timeline** (M) next. Tracing and graphs last (L), only if wanted.
4. **Measure** small vs big binlog (time to tree, peak memory) before designing around a worker.
5. Phase 3 per docs/wasm/mcp.md.

## 4. Questions for you
- Is a ~1 to 1.5 GB practical ceiling acceptable? If big logs (500 MB and up) matter, the answer is a worker or server-side parse, not UI work.
- Tracing and the graph views are the biggest gaps (L each). OK to defer both and do Timeline only?
- Is the headless-parse-in-a-worker direction wanted, or is main-thread with progress enough for the first version?
