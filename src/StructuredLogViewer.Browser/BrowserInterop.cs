using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace StructuredLogViewer.Browser
{
    /// <summary>Entry points called from main.js (file picker, drag and drop) and from the smoke test.</summary>
    public static partial class BrowserInterop
    {
        [JSExport]
        public static Task OpenBytes(string name, byte[] bytes) => MainView.Instance.OpenBytesAsync(name, bytes);

        [JSExport]
        public static string GetState() => MainView.Instance?.GetStateJson() ?? "{}";

        [JSExport]
        public static Task<string> SearchAndSelectFirst(string query) => MainView.Instance.SearchAndSelectFirstAsync(query);

        [JSExport]
        public static string OpenFirstSourceFile(string filter) => MainView.Instance.OpenFirstSourceFile(filter);
    }
}
