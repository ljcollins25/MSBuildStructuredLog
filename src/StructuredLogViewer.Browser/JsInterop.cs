using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace StructuredLogViewer.Browser
{
    internal static partial class JsInterop
    {
        private const string Module = "interop.js";

        [JSImport("getQuery", Module)]
        public static partial string GetQuery();

        [JSImport("report", Module)]
        public static partial void Report(string message);

        [JSImport("now", Module)]
        public static partial double Now();

        [JSImport("fetchBytes", Module)]
        private static partial Task<JSObject> FetchBytes(string url);

        public static async Task<byte[]> FetchBytesAsync(string url)
        {
            using var result = await FetchBytes(url);
            return result.GetPropertyAsByteArray("bytes");
        }

        public static Task InitializeAsync() => JSHost.ImportAsync(Module, "./interop.js");
    }
}
