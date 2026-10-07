using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;
using Task = System.Threading.Tasks.Task;

namespace StructuredLogViewer.Browser
{
    /// <summary>Entry points called from main.js (file picker, drag and drop) and from the smoke test.</summary>
    public static partial class BrowserInterop
    {
        [JSExport]
        public static Task OpenBytes(string name, byte[] bytes) => BrowserShell.Instance.OpenBytesAsync(name, bytes);

        /// <summary>Summary of the loaded log, for the smoke test.</summary>
        [JSExport]
        public static string GetState()
        {
            var doc = BrowserShell.Instance?.Document;
            var bc = BrowserShell.Instance?.BuildControl;
            if (doc == null || bc == null)
            {
                return "{\"status\":\"" + (BrowserShell.Instance?.StatusText ?? "").Replace("\"", "'") + "\"}";
            }

            string Esc(string v) => (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
            return "{\"loaded\":true,\"status\":\"" + Esc(BrowserShell.Instance.StatusText) + "\",\"succeeded\":" + (doc.Build.Succeeded ? "true" : "false") +
                ",\"files\":" + doc.Files.Count +
                ",\"searchText\":\"" + Esc(bc.SearchText) + "\"" +
                ",\"selected\":\"" + Esc((bc.searchLogControl.ResultsList.SelectedItem as SearchResult)?.Node?.ToString()) + "\"" +
                ",\"searchResults\":" + bc.searchLogControl.ResultsList.ItemCount + "}";
        }

        /// <summary>Types the query into the real search box and waits for the results.</summary>
        [JSExport]
        public static async Task<int> Search(string query)
        {
            var bc = BrowserShell.Instance.BuildControl;
            await Dispatcher.UIThread.InvokeAsync(() => bc.SelectSearchTab(query));
            await Task.Delay(2000);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var list = bc.searchLogControl.ResultsList;
                if (list.ItemCount > 0)
                {
                    list.SelectedItem = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Cast<object>(list.Items));
                }

                return list.ItemCount;
            });
        }

        /// <summary>Opens the first embedded source file whose path contains the filter in the shared text viewer.</summary>
        [JSExport]
        public static async Task<string> OpenFirstSourceFile(string filter)
        {
            var shell = BrowserShell.Instance;
            foreach (var file in shell.Document.FindFiles(filter))
            {
                bool ok = await Dispatcher.UIThread.InvokeAsync(() => shell.BuildControl.DisplayFile(file));
                if (ok)
                {
                    return file;
                }
            }

            return "";
        }
    }
}
