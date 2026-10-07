using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>An <see cref="IPositionedSource"/> over a local file.</summary>
    public sealed class FilePositionedSource : IPositionedSource
    {
        private readonly FileStream stream;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public FilePositionedSource(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);
            var info = new FileInfo(path);
            Length = stream.Length;
            Identity = "file:" + info.Name + "|" + Length + "|" + info.LastWriteTimeUtc.Ticks;
        }

        public long Length { get; }

        public string Identity { get; }

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            gate.Wait();
            try
            {
                stream.Position = position;
                return stream.Read(buffer, offset, count);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<int> ReadAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                stream.Position = position;
                return await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            stream.Dispose();
            gate.Dispose();
        }
    }
}
