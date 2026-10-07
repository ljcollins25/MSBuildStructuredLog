using System;
using System.Collections.Generic;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// A place in a gzip/deflate stream where decoding can start without having decoded what comes before:
    /// a deflate block boundary, together with the 32 KB of output that blocks after it may refer back to
    /// (see zran.c in zlib's examples).
    /// </summary>
    public sealed class SeekPoint
    {
        public SeekPoint(long inputBitOffset, long outputOffset, byte[] window)
        {
            InputBitOffset = inputBitOffset;
            OutputOffset = outputOffset;
            Window = window ?? Array.Empty<byte>();
        }

        /// <summary>Offset in the compressed file, in bits, of the first bit of the block header.</summary>
        public long InputBitOffset { get; }

        /// <summary>Offset in the decompressed stream of the first byte this block produces.</summary>
        public long OutputOffset { get; }

        /// <summary>The up to 32 KB of decompressed data that precede <see cref="OutputOffset"/>.</summary>
        public byte[] Window { get; }

        /// <summary>Offset in the compressed file of the byte holding the first bit of the block header.</summary>
        public long InputByteOffset => InputBitOffset >> 3;

        public override string ToString() => $"compressed bit {InputBitOffset} -> decompressed {OutputOffset} (window {Window.Length})";
    }

    /// <summary>
    /// The seek points of one gzip file, plus its sizes. Built by <see cref="GzipIndexingStream"/> during a
    /// sequential pass, used by <see cref="GzipRandomAccessReader"/> for random reads.
    /// </summary>
    public sealed class GzipIndex
    {
        public GzipIndex(IReadOnlyList<SeekPoint> points, long compressedLength, long uncompressedLength, long spacingBytes)
        {
            if (points == null || points.Count == 0)
            {
                throw new ArgumentException("An index needs at least one seek point.", nameof(points));
            }

            Points = points;
            CompressedLength = compressedLength;
            UncompressedLength = uncompressedLength;
            SpacingBytes = spacingBytes;
        }

        public IReadOnlyList<SeekPoint> Points { get; }

        /// <summary>Length of the gzip file in bytes.</summary>
        public long CompressedLength { get; }

        /// <summary>Length of the decompressed stream in bytes.</summary>
        public long UncompressedLength { get; }

        /// <summary>The minimum distance in compressed bytes between seek points that was requested when indexing.</summary>
        public long SpacingBytes { get; }

        /// <summary>Index of the last seek point whose output offset is at or before <paramref name="outputOffset"/>.</summary>
        public int FindPoint(long outputOffset)
        {
            int lo = 0;
            int hi = Points.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (Points[mid].OutputOffset <= outputOffset)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }

        /// <summary>Decompressed offset where the segment that starts at seek point <paramref name="pointIndex"/> ends.</summary>
        public long SegmentEnd(int pointIndex) => pointIndex + 1 < Points.Count ? Points[pointIndex + 1].OutputOffset : UncompressedLength;

        /// <summary>Compressed byte offset (exclusive) up to which the segment starting at the point needs input.</summary>
        public long SegmentInputEnd(int pointIndex)
            => pointIndex + 1 < Points.Count ? Math.Min(CompressedLength, (Points[pointIndex + 1].InputBitOffset + 7) >> 3) : CompressedLength;
    }
}
