using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Logging.StructuredLogger.Paging;

namespace StructuredLogViewer.Browser
{
    /// <summary>Ranged reads of a dropped File (blob: URL) or a same-origin/CORS URL through synchronous XHR; nothing is copied into wasm memory except the bytes being read.</summary>
    public sealed class JsPositionedSource : IPositionedSource
    {
        private readonly int id;

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
            var bytes = JsInterop.ReadSource(id, position, count);
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
            return bytes.Length;
        }

        public Task<int> ReadAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
            => Task.FromResult(Read(position, buffer, offset, count));

        public void Dispose() { }
    }
}
