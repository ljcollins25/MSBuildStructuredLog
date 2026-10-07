using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// Random access to the bytes of a compressed log: "read up to N bytes at offset K".
    /// Implemented for local files (<see cref="FilePositionedSource"/>) and for HTTP servers that support
    /// Range requests (<see cref="HttpRangeSource"/>).
    /// </summary>
    public interface IPositionedSource : IDisposable
    {
        /// <summary>Total length of the source in bytes.</summary>
        long Length { get; }

        /// <summary>
        /// Identifies this version of the content, for example length + timestamp for a file or URL + ETag for a
        /// resource on a server. Cached indexes are keyed by it.
        /// </summary>
        string Identity { get; }

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes starting at <paramref name="position"/>. Returns fewer
        /// than requested only at the end of the source.
        /// </summary>
        int Read(long position, byte[] buffer, int offset, int count);

        /// <summary>Asynchronous version of <see cref="Read"/>; the only option where blocking is not allowed (browser).</summary>
        Task<int> ReadAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default);
    }

    public static class PositionedSourceExtensions
    {
        /// <summary>Reads exactly <paramref name="count"/> bytes or throws <see cref="EndOfStreamException"/>.</summary>
        public static byte[] ReadBytes(this IPositionedSource source, long position, int count)
        {
            var result = new byte[count];
            int total = 0;
            while (total < count)
            {
                int n = source.Read(position + total, result, total, count - total);
                if (n <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of source at {position + total}.");
                }

                total += n;
            }

            return result;
        }

        public static async Task<byte[]> ReadBytesAsync(this IPositionedSource source, long position, int count, CancellationToken cancellationToken = default)
        {
            var result = new byte[count];
            int total = 0;
            while (total < count)
            {
                int n = await source.ReadAsync(position + total, result, total, count - total, cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of source at {position + total}.");
                }

                total += n;
            }

            return result;
        }
    }

    /// <summary>A forward-only <see cref="Stream"/> over an <see cref="IPositionedSource"/>, starting at an offset.</summary>
    public sealed class PositionedSourceStream : Stream
    {
        private readonly IPositionedSource source;
        private readonly long end;
        private long position;

        public PositionedSourceStream(IPositionedSource source, long start = 0, long? end = null)
        {
            this.source = source;
            this.position = start;
            this.end = end ?? source.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => end;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, end - position);
            if (count <= 0)
            {
                return 0;
            }

            int n = source.Read(position, buffer, offset, count);
            position += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            count = (int)Math.Min(count, end - position);
            if (count <= 0)
            {
                return 0;
            }

            int n = await source.ReadAsync(position, buffer, offset, count, cancellationToken).ConfigureAwait(false);
            position += n;
            return n;
        }
    }
}
