using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Logging.StructuredLogger.Paging;

namespace StructuredLogViewer.Browser
{
    /// <summary>Ranged reads of a dropped File (blob: URL) or a same-origin/CORS URL through synchronous XHR; nothing is copied into wasm memory except the bytes being read.</summary>
    public sealed class JsPositionedSource : IPositionedSource
    {
        private const int ReadAhead = 4 * 1024 * 1024;
        private readonly int id;
        private byte[] window;
        private long windowStart, windowLength;

        private JsPositionedSource(int id, string identity)
        {
            this.id = id;
            Length = (long)JsInterop.SourceLength(id);
            Identity = identity + ":" + Length;
        }

        public static JsPositionedSource FromUrl(string url) => new JsPositionedSource(JsInterop.OpenSourceUrl(url), url);

        public static JsPositionedSource FromPendingFile(string name) => new JsPositionedSource(JsInterop.OpenPendingSource(), name);

        public long Length { get; }

        public string Identity { get; }

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, Length - position);
            if (count <= 0) return 0;
            int total = 0;
            while (total < count)
            {
                if (position < windowStart || position >= windowStart + windowLength)
                {
                    // one big ranged request instead of many small ones; sequential reads then hit the window
                    int want = (int)Math.Min(ReadAhead, Length - position);
                    window = JsInterop.ReadSource(id, position, want);
                    windowStart = position; windowLength = window.Length;
                    if (windowLength == 0) break;
                }
                int n = Math.Min(count - total, (int)(windowStart + windowLength - position));
                Buffer.BlockCopy(window, (int)(position - windowStart), buffer, offset + total, n);
                total += n; position += n;
            }
            return total;
        }

        public Task<int> ReadAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
            => Task.FromResult(Read(position, buffer, offset, count));

        public void Dispose() { }
    }
}
