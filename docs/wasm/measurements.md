# Browser load measurements (spike, interpreter build)

Environment: headless Chrome 64-bit on a GitHub runner (swiftshader), Release publish of src/StructuredLogViewer.Browser, parse on the main thread via `BinaryLog.ReadBuild(Stream, Progress)` from a MemoryStream, `EmccMaximumHeapSize=4294901760` for the second big run.

| binlog | on disk (gzip) | decompressed | fetch | parse to Build | GC heap after | result |
|---|---|---|---|---|---|---|
| StructuredLogger build | 539 KB | n/a | 62 ms | about 1.0 s | 19 MB | ok, 185,319 nodes, 6,239 strings |
| dotnet/runtime CI 1621702, linux x64 Release CoreCLR AllSubsets, Build.binlog | 79.0 MB | 305.3 MB | 160-200 ms (local) | fails at about 63% (default heap) / 87% (4 GB max heap) | 1.09 GB / 1.51 GB at failure | out of memory: "Garbage collector could not allocate 16384u bytes of memory for major heap section" |

Rate: about 0.7 to 1.5% of the file per second, so about 85 to 100 s for the whole file if it fit (interpreter). Managed heap grows about 17 to 20 MB per 1% of the log, so a full load would need about 1.7 to 2 GB of managed heap, plus the 79 MB input array and the decompression buffers, which is over the wasm32 2 GB address space. Chrome's total RSS peaked around 5 to 6.5 GB (all processes, includes swiftshader).

## Correction after the replay change (commit e782cfe98)

The first big-log numbers above are not a tree-load measurement. The single-threaded replay path collected every event in a list and only built the tree after reading the whole file, so the "63% / 87%" figures measured reading and queueing events, not the finished tree. With events dispatched as they are read, the tree is built during the parse and the log fails earlier:

| run (79 MB log, EmccMaximumHeapSize 4 GB) | progress at failure | GC heap at failure | heap per 1% | time to failure |
|---|---|---|---|---|
| before (read and queue, no tree yet) | 87% | 1,509 MB | about 17 MB | 72 s |
| after (tree built while reading) | about 62% | about 1.76 GB | about 29 MB | 74 s |

Heap growth is not linear: 339 MB at 9%, 819 MB at 31%, 1,516 MB at 50%, 1,757 MB at 60%. The wasm32 address space (about 2 GB including the 79 MB input array) is the ceiling, so a full load of this log needs a heap of about 2.8 GB and cannot fit. The baseline already includes upstream PR #962 (lazy message, dd885a9e is its merge), so its effect is part of these numbers.
