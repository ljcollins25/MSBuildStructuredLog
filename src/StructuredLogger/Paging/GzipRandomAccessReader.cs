using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// Random access to the decompressed content of a gzip file, using the <see cref="GzipIndex"/> that was
    /// recorded when the file was read sequentially. The content between two seek points is a "segment": to
    /// read at an offset, the segment that contains it is fetched from the <see cref="IPositionedSource"/>
    /// (exactly its compressed bytes, which is one Range request for HTTP), inflated starting at the seek
    /// point with its saved window, and kept in a small least-recently-used cache.
    /// </summary>
    public sealed class GzipRandomAccessReader : IDisposable
    {
        public const long DefaultCacheBytes = 32L << 20;

        private readonly IPositionedSource source;
        private readonly GzipIndex index;
        private readonly long cacheBudget;
        private readonly object gate = new object();
        private readonly Dictionary<int, LinkedListNode<KeyValuePair<int, byte[]>>> cache = new Dictionary<int, LinkedListNode<KeyValuePair<int, byte[]>>>();
        private readonly LinkedList<KeyValuePair<int, byte[]>> lru = new LinkedList<KeyValuePair<int, byte[]>>();
        private long cachedBytes;
        private long segmentsDecoded;
        private long bytesInflated;
        private long cacheHits;

        public GzipRandomAccessReader(IPositionedSource source, GzipIndex index, long cacheBytes = DefaultCacheBytes)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.index = index ?? throw new ArgumentNullException(nameof(index));
            this.cacheBudget = cacheBytes;
        }

        public GzipIndex Index => index;

        /// <summary>Length of the decompressed content.</summary>
        public long Length => index.UncompressedLength;

        /// <summary>How many segments had to be inflated (cache misses).</summary>
        public long SegmentsDecoded => Interlocked.Read(ref segmentsDecoded);

        /// <summary>How many decompressed bytes were produced by cache misses.</summary>
        public long BytesInflated => Interlocked.Read(ref bytesInflated);

        public long CacheHits => Interlocked.Read(ref cacheHits);

        /// <summary>Reads up to <paramref name="count"/> bytes at a decompressed offset; fewer only at the end of the content.</summary>
        public int Read(long offset, byte[] buffer, int bufferOffset, int count)
        {
            int total = 0;
            while (count > 0 && offset < Length)
            {
                int k = index.FindPoint(offset);
                byte[] segment = GetSegment(k);
                total += CopyFromSegment(k, segment, ref offset, buffer, ref bufferOffset, ref count);
            }

            return total;
        }

        public async Task<int> ReadAsync(long offset, byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken = default)
        {
            int total = 0;
            while (count > 0 && offset < Length)
            {
                int k = index.FindPoint(offset);
                byte[] segment = await GetSegmentAsync(k, cancellationToken).ConfigureAwait(false);
                total += CopyFromSegment(k, segment, ref offset, buffer, ref bufferOffset, ref count);
            }

            return total;
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes at an offset or throws <see cref="EndOfStreamException"/>.</summary>
        public byte[] ReadBytes(long offset, int count)
        {
            var result = new byte[count];
            if (Read(offset, result, 0, count) != count)
            {
                throw new EndOfStreamException();
            }

            return result;
        }

        public async Task<byte[]> ReadBytesAsync(long offset, int count, CancellationToken cancellationToken = default)
        {
            var result = new byte[count];
            if (await ReadAsync(offset, result, 0, count, cancellationToken).ConfigureAwait(false) != count)
            {
                throw new EndOfStreamException();
            }

            return result;
        }

        private int CopyFromSegment(int k, byte[] segment, ref long offset, byte[] buffer, ref int bufferOffset, ref int count)
        {
            long segmentStart = index.Points[k].OutputOffset;
            int inSegment = (int)(offset - segmentStart);
            int n = Math.Min(count, segment.Length - inSegment);
            Buffer.BlockCopy(segment, inSegment, buffer, bufferOffset, n);
            offset += n;
            bufferOffset += n;
            count -= n;
            return n;
        }

        private byte[] GetSegment(int k)
        {
            if (TryGetCached(k, out var segment))
            {
                return segment;
            }

            long start = index.Points[k].InputByteOffset;
            int length = (int)(index.SegmentInputEnd(k) - start);
            byte[] compressed = source.ReadBytes(start, length);
            return Decode(k, compressed);
        }

        private async Task<byte[]> GetSegmentAsync(int k, CancellationToken cancellationToken)
        {
            if (TryGetCached(k, out var segment))
            {
                return segment;
            }

            long start = index.Points[k].InputByteOffset;
            int length = (int)(index.SegmentInputEnd(k) - start);
            byte[] compressed = await source.ReadBytesAsync(start, length, cancellationToken).ConfigureAwait(false);
            return Decode(k, compressed);
        }

        private byte[] Decode(int k, byte[] compressed)
        {
            var point = index.Points[k];
            int length = checked((int)(index.SegmentEnd(k) - point.OutputOffset));
            var result = new byte[length];
            var engine = InflateEngine.StartAt(new MemoryStream(compressed, writable: false), point.InputByteOffset, point);
            int total = 0;
            while (total < length)
            {
                int n = engine.Read(result, total, length - total);
                if (n == 0)
                {
                    throw new InvalidDataException("The compressed data of segment " + k + " ended after " + total + " of " + length + " bytes.");
                }

                total += n;
            }

            Interlocked.Increment(ref segmentsDecoded);
            Interlocked.Add(ref bytesInflated, length);
            AddToCache(k, result);
            return result;
        }

        private bool TryGetCached(int k, out byte[] segment)
        {
            lock (gate)
            {
                if (cache.TryGetValue(k, out var node))
                {
                    lru.Remove(node);
                    lru.AddFirst(node);
                    segment = node.Value.Value;
                    Interlocked.Increment(ref cacheHits);
                    return true;
                }
            }

            segment = null;
            return false;
        }

        private void AddToCache(int k, byte[] segment)
        {
            lock (gate)
            {
                if (cache.ContainsKey(k))
                {
                    return;
                }

                var node = new LinkedListNode<KeyValuePair<int, byte[]>>(new KeyValuePair<int, byte[]>(k, segment));
                lru.AddFirst(node);
                cache[k] = node;
                cachedBytes += segment.Length;

                // always keep the newest segment, even when it alone is over budget
                while (cachedBytes > cacheBudget && lru.Count > 1)
                {
                    var last = lru.Last;
                    lru.RemoveLast();
                    cache.Remove(last.Value.Key);
                    cachedBytes -= last.Value.Value.Length;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                cache.Clear();
                lru.Clear();
                cachedBytes = 0;
            }
        }
    }
}
