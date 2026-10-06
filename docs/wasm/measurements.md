# Browser load measurements (spike, interpreter build)

Environment: headless Chrome 64-bit on a GitHub runner (swiftshader), Release publish of src/StructuredLogViewer.Browser, parse on the main thread via `BinaryLog.ReadBuild(Stream, Progress)` from a MemoryStream, `EmccMaximumHeapSize=4294901760` for the second big run.

| binlog | on disk (gzip) | decompressed | fetch | parse to Build | GC heap after | result |
|---|---|---|---|---|---|---|
| StructuredLogger build | 539 KB | n/a | 62 ms | about 1.0 s | 19 MB | ok, 185,319 nodes, 6,239 strings |
| dotnet/runtime CI 1621702, linux x64 Release CoreCLR AllSubsets, Build.binlog | 79.0 MB | 305.3 MB | 160-200 ms (local) | fails at about 63% (default heap) / 87% (4 GB max heap) | 1.09 GB / 1.51 GB at failure | out of memory: "Garbage collector could not allocate 16384u bytes of memory for major heap section" |

Rate: about 0.7 to 1.5% of the file per second, so about 85 to 100 s for the whole file if it fit (interpreter). Managed heap grows about 17 to 20 MB per 1% of the log, so a full load would need about 1.7 to 2 GB of managed heap, plus the 79 MB input array and the decompression buffers, which is over the wasm32 2 GB address space. Chrome's total RSS peaked around 5 to 6.5 GB (all processes, includes swiftshader).
