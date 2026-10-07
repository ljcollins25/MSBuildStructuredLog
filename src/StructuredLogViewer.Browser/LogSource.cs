using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StructuredLogViewer.Browser
{
    /// <summary>A log that can be read from somewhere: the seam for a paged reader (read only the byte ranges it needs).</summary>
    public interface ILogSource
    {
        string Name { get; }

        /// <summary>Total size in bytes, or null when the server did not say.</summary>
        long? Length { get; }

        /// <summary>True when ReadAsync can fetch arbitrary ranges (the server answered 206 to a Range request).</summary>
        bool SupportsRange { get; }

        /// <summary>Reads up to count bytes at offset. Throws NotSupportedException when SupportsRange is false.</summary>
        Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default);

        /// <summary>Reads the whole log. progress gets 0..1 when the length is known.</summary>
        Task<byte[]> ReadAllAsync(IProgress<double> progress = null, CancellationToken cancellationToken = default);
    }

    /// <summary>Opens an ILogSource for a URL. Replace LogSources.Provider to plug in another reader.</summary>
    public interface ILogSourceProvider
    {
        /// <summary>Probes the URL. Throws LogSourceException with a message fit to show the user.</summary>
        Task<ILogSource> OpenAsync(string url, CancellationToken cancellationToken = default);
    }

    public sealed class LogSourceException : Exception
    {
        public LogSourceException(string message) : base(message) { }
    }

    public static class LogSources
    {
        public static ILogSourceProvider Provider { get; set; } = new HttpLogSourceProvider();

        /// <summary>Checks the first bytes look like a log (gzip binlog, zip, or XML) and not a web page or other file.</summary>
        public static void Validate(byte[] head, string contentType, string url)
        {
            if (head == null || head.Length == 0)
            {
                throw new LogSourceException("The URL returned an empty response.");
            }

            if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
            {
                return; // gzip: a .binlog
            }

            if (head.Length >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
            {
                return; // zip
            }

            string text = Encoding.UTF8.GetString(head, 0, Math.Min(head.Length, 256)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            bool html = (contentType ?? "").IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
            if (html)
            {
                string hint = url.Contains("github.com") && url.Contains("/blob/") ? " For GitHub use the raw file link, not the /blob/ page." : "";
                throw new LogSourceException("The URL returned a web page (HTML), not a binlog." + hint);
            }

            if (text.StartsWith("<"))
            {
                return; // XML build log
            }

            throw new LogSourceException("The URL does not look like an MSBuild binary log: it does not start with the gzip header a .binlog has.");
        }
    }

    /// <summary>fetch()-based source. Uses Range requests when the server supports them, a single download otherwise.</summary>
    internal sealed class HttpLogSourceProvider : ILogSourceProvider
    {
        public const string CorsMessage = "Could not download the URL, most likely because the server does not allow cross-origin requests (CORS). " +
            "It must send Access-Control-Allow-Origin; for Range requests also Access-Control-Allow-Headers: Range and Access-Control-Expose-Headers: Content-Range. " +
            "A network error or an http URL from an https page gives the same failure.";

        public async Task<ILogSource> OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new LogSourceException("Enter the URL of a .binlog.");
            }

            url = url.Trim();
            Uri uri;
            try
            {
                uri = new Uri(new Uri(JsInterop.GetHref()), url);
            }
            catch (UriFormatException)
            {
                throw new LogSourceException("Not a valid URL: " + url);
            }

            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                throw new LogSourceException("Only http and https URLs can be opened.");
            }

            string full = uri.AbsoluteUri;
            string name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrEmpty(name))
            {
                name = uri.Host;
            }

            JsInterop.FetchResult probe;
            try
            {
                probe = await JsInterop.FetchRangeAsync(full, 0, 255); // a Range header can force a CORS preflight
            }
            catch (JsNetworkException)
            {
                try
                {
                    probe = await JsInterop.FetchRangeAsync(full, -1, -1); // retry plain: CORS without Range support
                }
                catch (JsNetworkException)
                {
                    throw new LogSourceException(CorsMessage);
                }
            }

            if (probe.Status != 200 && probe.Status != 206)
            {
                throw new LogSourceException($"The server answered HTTP {probe.Status} for {full}.");
            }

            byte[] head = probe.Bytes;
            LogSources.Validate(head, probe.ContentType, full);
            if (probe.Status == 206 && probe.Total > 0)
            {
                return new HttpLogSource(name, full, probe.Total, null);
            }

            // the server ignored Range (200): the probe already holds the whole body
            return new HttpLogSource(name, full, head.Length, head);
        }
    }

    internal sealed class HttpLogSource : ILogSource
    {
        private const int ChunkSize = 8 * 1024 * 1024;
        private readonly string url;
        private readonly byte[] whole;

        public HttpLogSource(string name, string url, long length, byte[] whole)
        {
            Name = name;
            this.url = url;
            Length = length;
            this.whole = whole;
        }

        public string Name { get; }
        public long? Length { get; }
        public bool SupportsRange => whole == null;

        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default)
        {
            if (whole != null)
            {
                throw new NotSupportedException("The server does not support Range requests.");
            }

            var result = await JsInterop.FetchRangeAsync(url, offset, offset + count - 1);
            if (result.Status != 206 && result.Status != 200)
            {
                throw new LogSourceException($"The server answered HTTP {result.Status} for a range of {url}.");
            }

            return result.Bytes;
        }

        public async Task<byte[]> ReadAllAsync(IProgress<double> progress = null, CancellationToken cancellationToken = default)
        {
            if (whole != null)
            {
                return whole;
            }

            var buffer = new byte[Length.Value];
            long position = 0;
            while (position < buffer.Length)
            {
                int count = (int)Math.Min(ChunkSize, buffer.Length - position);
                byte[] part = await ReadAsync(position, count, cancellationToken);
                if (part.Length == 0)
                {
                    throw new LogSourceException("The server returned fewer bytes than its Content-Range announced.");
                }

                Buffer.BlockCopy(part, 0, buffer, (int)position, part.Length);
                position += part.Length;
                progress?.Report((double)position / buffer.Length);
            }

            return buffer;
        }
    }
}
