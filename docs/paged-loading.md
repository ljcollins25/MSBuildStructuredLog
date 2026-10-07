# Paged loading of large binlogs

Status: design. The seekable inflater, index and positioned sources exist (`src/StructuredLogger/Paging`); the lazy tree and index cache do not yet.

## Problem (measured)

dotnet/runtime CI Build.binlog (79,911,102 bytes, 305,311,034 decompressed), `BinaryLog.ReadBuild` on net10.0 Linux:

| | value |
|---|---|
| time to tree | 19.7 s |
| managed heap after GC | 4,505 MB (peak sampled 4,715 MB) |
| peak RSS | about 5.3 GB |

Census of the built tree: 44,766,121 Metadata, 7,168,730 Item, 5,055,977 Property nodes, 427,505 Folder, 277,619 PropertyReassignmentMessage. The string table holds only 387,034 unique strings (104,694,689 chars, about 210 MB). Node count, not string size, dominates. Strings alone cannot fix this, so item metadata, properties and item lists must also be deferred.

## Where things are materialized today

- `BuildEventArgsReader`: string records go to a string storage; name/value list records become dictionaries; embedded archive callbacks produce byte arrays.
- `BinaryLog.ReadBuild` wires those to `Construction`, which creates a node per item, per metadata entry and per property (`Construction.AddItems/AddMetadata`, `AddPropertiesSorted`).
- Messages: text of `PropertyReassignmentMessage` (102M chars), `TimedMessage`, `Message`, `Import`.

## Design

1. First pass: stream the file once through `GzipIndexingStream` (managed inflater). It records a seek point at the first deflate block boundary after every 1 MiB of compressed input: compressed bit offset, decompressed offset, last 32 KB of output.
2. Later reads: `GzipRandomAccessReader` takes the seek point at or before offset X, fetches exactly that segment's compressed bytes from an `IPositionedSource` (file seek or one HTTP Range request), primes the inflater (bit offset + window) and inflates the segment into a small LRU cache (default 32 MB).
3. Inflater: DeflateStream cannot start at a bit offset or take a preset window, and cannot report block boundaries, so a managed inflater is used for both the first pass and seeks. It runs at about 400 MB/s versus about 680 MB/s for DeflateStream, so the first pass is about 0.3 s slower per 300 MB.
4. Sources: `FilePositionedSource`, `HttpRangeSource` (HttpClient only, async-capable, If-Match on the ETag so a changed file fails instead of mixing versions).
5. Lazy values (to do): strings above a threshold, and item/property/metadata groups of an item list, are kept as (offset, length) in the decompressed stream and re-parsed on demand with the cache. The embedded files archive is read as positions instead of a byte array.
6. Index cache (to do): seek points plus the string/record position index, serialized and keyed by source Identity (file: name, length, timestamp; HTTP: URL, ETag, length).

## Index format (cache file)

Header: magic, version, identity string, compressed length, decompressed length, spacing. Then N seek points: bitOffset (int64), outputOffset (int64), window length (int32), window bytes (raw; compressible with Deflate). Then record index sections. Measured: 75 points, 2,424,832 bytes of windows for this file at 1 MiB spacing.

## Memory expectation

Seek windows about 32 KB per MiB of compressed input (about 2.4 MB here), cache about 32 MB, tree skeleton without Item/Metadata/Property leaves is expected to be a small fraction of the 4.5 GB; to be measured.
