using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Build.Logging.StructuredLogger.Paging
{
    /// <summary>
    /// An <see cref="IPositionedSource"/> over an HTTP(S) resource that supports Range requests. Uses only
    /// HttpClient so that it also works in the browser (where requests must be awaited: use
    /// <see cref="ReadAsync"/>, the synchronous <see cref="Read"/> blocks on the request and can only be
    /// used on a thread that is allowed to block).
    /// </summary>
    public sealed class HttpRangeSource : IPositionedSource
    {
        private readonly HttpClient client;
        private readonly Uri uri;
        private readonly string validator;

        private HttpRangeSource(HttpClient client, Uri uri, long length, string validator, string identity)
        {
            this.client = client;
            this.uri = uri;
            this.Length = length;
            this.validator = validator;
            this.Identity = identity;
        }

        public long Length { get; }

        public string Identity { get; }

        /// <summary>Number of Range requests sent (for tests and statistics).</summary>
        public int RequestCount => requestCount;

        /// <summary>Number of bytes received in response bodies.</summary>
        public long BytesReceived => bytesReceived;

        private int requestCount;
        private long bytesReceived;

        /// <summary>
        /// Sends a "Range: bytes=0-0" request to learn the length and the ETag (or Last-Modified), and to verify
        /// that the server supports ranges.
        /// </summary>
        public static async Task<HttpRangeSource> OpenAsync(HttpClient client, Uri uri, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    throw new NotSupportedException("The server ignored the Range request header: " + uri);
                }

                response.EnsureSuccessStatusCode();
                throw new NotSupportedException("Unexpected status " + (int)response.StatusCode + " for " + uri);
            }

            long? length = response.Content.Headers.ContentRange?.Length;
            if (length == null)
            {
                throw new NotSupportedException("The server did not report the total length in Content-Range: " + uri);
            }

            string etag = response.Headers.ETag?.ToString();
            string version = etag ?? response.Content.Headers.LastModified?.UtcTicks.ToString();
            string identity = "http:" + uri + "|" + (version ?? "?") + "|" + length.Value;
            return new HttpRangeSource(client, uri, length.Value, etag, identity);
        }

        public int Read(long position, byte[] buffer, int offset, int count)
            => ReadAsync(position, buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<int> ReadAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            if (position >= Length || count <= 0)
            {
                return 0;
            }

            long last = Math.Min(Length - 1, position + count - 1);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(position, last);
            if (validator != null)
            {
                // fail (412) instead of silently mixing two versions of the file
                request.Headers.TryAddWithoutValidation("If-Match", validator);
            }

            Interlocked.Increment(ref requestCount);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new IOException("The resource changed on the server: " + uri);
            }

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new IOException("Expected 206 Partial Content for " + uri + " but got " + (int)response.StatusCode);
            }

            int wanted = (int)(last - position + 1);
            int total = 0;
            using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                while (total < wanted)
                {
                    int n = await body.ReadAsync(buffer, offset + total, wanted - total, cancellationToken).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    total += n;
                }
            }

            Interlocked.Add(ref bytesReceived, total);
            return total;
        }

        public void Dispose()
        {
            // the HttpClient belongs to the caller
        }
    }
}
