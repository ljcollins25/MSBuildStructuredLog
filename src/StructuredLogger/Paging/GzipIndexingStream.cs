using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// Decompresses a gzip stream sequentially, like <see cref="System.IO.Compression.GZipStream"/>, and
    /// records a <see cref="SeekPoint"/> every <c>spacingBytes</c> of compressed input at the next deflate
    /// block boundary. When the end of the stream has been read, <see cref="Index"/> holds the result.
    /// </summary>
    public sealed class GzipIndexingStream : Stream
    {
        /// <summary>Default distance between seek points: 1 MB of compressed input (about 32 KB of index per MB).</summary>
        public const long DefaultSpacingBytes = 1 << 20;

        private readonly Stream compressed;
        private readonly bool leaveOpen;
        private readonly List<SeekPoint> points = new List<SeekPoint>();
        private readonly long spacingBytes;
        private readonly InflateEngine engine;
        private GzipIndex index;

        /// <param name="compressed">The gzip file, positioned at its start.</param>
        /// <param name="spacingBytes">Minimum compressed distance between seek points.</param>
        /// <param name="leaveOpen">Whether to leave <paramref name="compressed"/> open on dispose.</param>
        public GzipIndexingStream(Stream compressed, long spacingBytes = DefaultSpacingBytes, bool leaveOpen = false)
        {
            this.compressed = compressed;
            this.spacingBytes = Math.Max(1, spacingBytes);
            this.leaveOpen = leaveOpen;
            this.engine = InflateEngine.StartGzip(compressed, 0, points, this.spacingBytes);
        }

        /// <summary>
        /// The index, available after the whole stream was read to its end. Null before that
        /// (and for a truncated file, see <see cref="IsTruncated"/>).
        /// </summary>
        public GzipIndex Index => index;

        /// <summary>True when the compressed input ended before the end of the deflate data.</summary>
        public bool IsTruncated => engine.Truncated;

        /// <summary>The seek points recorded so far (an index of the part of the file that was read).</summary>
        public IReadOnlyList<SeekPoint> PointsSoFar => points;

        /// <summary>How many compressed bytes were consumed so far; use it for progress, like the position of a FileStream.</summary>
        public long CompressedPosition => engine.InputBytesLoaded;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        /// <summary>The number of decompressed bytes read so far.</summary>
        public override long Position { get => engine.OutputPosition; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = engine.Read(buffer, offset, count);
            if (n == 0 && index == null && engine.Finished)
            {
                if (points.Count == 0)
                {
                    // an empty file: a single point at the start keeps the index well formed
                    points.Add(new SeekPoint(0, 0, null));
                }

                long compressedLength = compressed.CanSeek ? compressed.Length : engine.InputBytesLoaded;
                index = new GzipIndex(points, compressedLength, engine.DecodedPosition, spacingBytes);
            }

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                compressed.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
