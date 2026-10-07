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
                ",\"selected\":\"" + Esc(bc.SelectedTreeViewItem?.DataContext?.ToString()) + "\"" +
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
                return list.ItemCount;
            });
        }

        /// <summary>Selects the first Task whose name contains the text, like clicking it in the tree (details, breadcrumb).</summary>
        [JSExport]
        public static async Task<string> SelectFirstTask(string name)
        {
            var shell = BrowserShell.Instance;
            var node = shell.Document.Build.FindFirstDescendant<Microsoft.Build.Logging.StructuredLogger.Task>(t => t.Name != null && t.Name.Contains(name));
            if (node == null)
            {
                return "";
            }

            await Dispatcher.UIThread.InvokeAsync(() => shell.BuildControl.SelectItem(node));
            await Task.Delay(500);
            return node.ToString();
        }

        /// <summary>Clicks a named button (save, copyFullPath) of the open source file tab, like a user would.</summary>
        [JSExport]
        public static async Task<bool> ClickViewerButton(string name)
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var button = System.Linq.Enumerable.FirstOrDefault(
                    global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(BrowserShell.Instance.BuildControl),
                    v => v is global::Avalonia.Controls.Button b && b.Name == name && b.IsEffectivelyVisible) as global::Avalonia.Controls.Button;
                if (button == null)
                {
                    return false;
                }

                button.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
                return true;
            });
        }

        /// <summary>Names of the visible buttons of the open source file tab (to check the desktop-only ones are hidden).</summary>
        [JSExport]
        public static string VisibleViewerButtons()
        {
            return Dispatcher.UIThread.Invoke(() => string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(
                System.Linq.Enumerable.OfType<global::Avalonia.Controls.Button>(global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(BrowserShell.Instance.BuildControl)),
                b => b.IsEffectivelyVisible && !string.IsNullOrEmpty(b.Name)), b => b.Name)));
        }

        /// <summary>Selects the first Task named like the text and runs Go to Timeline on it; returns "blocks|highlighted".</summary>
        [JSExport]
        public static async Task<string> GoToTimeline(string taskName)
        {
            var shell = BrowserShell.Instance;
            var node = shell.Document.Build.FindFirstDescendant<Microsoft.Build.Logging.StructuredLogger.Task>(t => t.Name != null && t.Name.Contains(taskName));
            if (node == null)
            {
                return "no task";
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                shell.BuildControl.SelectItem(node);
                shell.BuildControl.GoToTimeLine();
            });
            await Task.Delay(1000);
            return await Dispatcher.UIThread.InvokeAsync(() => shell.BuildControl.TimelineControl.TextBlocks.Count.ToString());
        }

        /// <summary>Switches the dark theme through SettingsService (persisted by the settings store).</summary>
        [JSExport]
        public static bool SetDarkTheme(bool dark)
        {
            return Dispatcher.UIThread.Invoke(() =>
            {
                StructuredLogViewer.SettingsService.UseDarkTheme = dark;
                StructuredLogViewer.Avalonia.App.UpdateTheme();
                return StructuredLogViewer.SettingsService.UseDarkTheme;
            });
        }

        [JSExport]
        public static bool GetDarkTheme() => StructuredLogViewer.SettingsService.UseDarkTheme;

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
