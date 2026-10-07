using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace StructuredLogViewer.Browser
{
    internal static partial class JsInterop
    {
        private const string Module = "interop.js";

        [JSImport("getQuery", Module)]
        public static partial string GetQuery();

        [JSImport("getHref", Module)]
        public static partial string GetHref();

        [JSImport("now", Module)]
        public static partial double Now();

        [JSImport("report", Module)]
        public static partial void Report(string message);

        [JSImport("storageGet", Module)]
        public static partial string StorageGet(string key);

        [JSImport("storageSet", Module)]
        public static partial void StorageSet(string key, string value);

        [JSImport("downloadText", Module)]
        public static partial void DownloadText(string name, string text);

        [JSImport("pickFile", Module)]
        public static partial void PickFile();

        [JSImport("fetchRange", Module)]
        private static partial Task<JSObject> FetchRange(string url, double start, double end);

        public sealed class FetchResult
        {
            public int Status;
            public byte[] Bytes;
            public long Total;
            public string ContentType;
        }

        /// <summary>start &lt; 0 sends no Range header. Throws JsNetworkException when fetch itself fails (CORS or network).</summary>
        public static async Task<FetchResult> FetchRangeAsync(string url, double start, double end)
        {
            try
            {
                using var result = await FetchRange(url, start, end);
                return new FetchResult
                {
                    Status = result.GetPropertyAsInt32("status"),
                    Bytes = result.GetPropertyAsByteArray("bytes"),
                    Total = (long)result.GetPropertyAsDouble("total"),
                    ContentType = result.GetPropertyAsString("contentType"),
                };
            }
            catch (JSException ex) when (ex.Message.Contains("NETWORK"))
            {
                throw new JsNetworkException();
            }
        }
    }

    internal sealed class JsNetworkException : System.Exception { }
}
