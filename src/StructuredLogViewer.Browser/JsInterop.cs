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

        [JSImport("openSourceUrl", Module)]
        public static partial int OpenSourceUrl(string url);

        [JSImport("openPendingSource", Module)]
        public static partial int OpenPendingSource();

        [JSImport("sourceLength", Module)]
        public static partial double SourceLength(int id);

        [JSImport("readSource", Module)]
        public static partial byte[] ReadSource(int id, double position, double count);

        [JSImport("fetchBytes", Module)]
        private static partial Task<JSObject> FetchBytes(string url);

        public static async Task<byte[]> FetchBytesAsync(string url)
        {
            using var result = await FetchBytes(url);
            return result.GetPropertyAsByteArray("bytes");
        }
    }
}
