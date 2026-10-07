using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StructuredLogViewer.Avalonia.Controls;
using Microsoft.Build.Logging.StructuredLogger;
using Task = System.Threading.Tasks.Task;

namespace StructuredLogViewer.Browser
{
    /// <summary>Entry points called from main.js (file picker, drag and drop) and from the smoke test.</summary>
    public static partial class BrowserInterop
    {
        [JSExport]
        public static Task OpenBytes(string name, byte[] bytes) => BrowserShell.Instance.OpenBytesAsync(name, bytes);

        /// <summary>Types the URL into the start page's Open from URL box and runs it; returns the error shown ("" when it opened).</summary>
        [JSExport]
        public static async Task<string> OpenUrl(string url) => await BrowserShell.Instance.OpenUrlAsync(url);

        /// <summary>Which start page controls are visible: "project,url,log" subset (smoke test).</summary>
        [JSExport]
        public static string WelcomeControls() => BrowserShell.Instance.WelcomeControlsVisible();

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
                ",\"selected\":\"" + Esc((bc.SelectedMainNode ?? bc.SelectedTreeViewItem?.DataContext)?.ToString()) + "\"" +
                ",\"searchResults\":" + bc.searchLogControl.ResultsList.ItemCount +
                ",\"leftTab\":\"" + Esc(bc.SelectedLeftTabName) + "\",\"findInFiles\":" + (bc.IsFindInFilesAvailable ? "true" : "false") + "}";
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

        /// <summary>Opens Go to Tracing for a task and the graph tabs; returns "tracingBlocks|projectRefVertices|targetVertices|propertyVertices".</summary>
        [JSExport]
        public static async Task<string> GoToTracingAndGraphs(string taskName)
        {
            var shell = BrowserShell.Instance;
            var build = shell.Document.Build;
            var node = build.FindFirstDescendant<Microsoft.Build.Logging.StructuredLogger.Task>(t => t.Name != null && t.Name.Contains(taskName));
            var project = build.FindFirstDescendant<Microsoft.Build.Logging.StructuredLogger.Project>(_ => true);
            if (node == null || project == null)
            {
                return "no task or project";
            }

            string result = null;
            string Step(string name, System.Func<object> action)
            {
                try { return action()?.ToString(); }
                catch (System.Exception ex) { return name + " failed: " + ex.GetType().Name + " " + ex.Message + " @ " + (ex.StackTrace ?? "").Split('\n')[0].Trim(); }
            }

            var errors = "";
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var bc = shell.BuildControl;
                errors += Step("select", () => { bc.SelectItem(node); return ""; });
                errors += Step("goToTracing", () => { bc.GoToTracing(); return ""; });
            });
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var bc = shell.BuildControl;
                string tracing = Step("tracing", () => bc.TracingControl.BlockCount);
                string refs = Step("refs", () => bc.ProjectReferenceGraphHost?.GraphControl.DisplayedCount ?? -1);
                string targets = Step("targets", () => bc.ShowTargetGraph(project)?.GraphControl.DisplayedCount ?? -1);
                string props = Step("props", () => bc.ShowPropertyGraph(project)?.GraphControl.DisplayedCount ?? -1);
                result = $"{tracing}|{refs}|{targets}|{props}|{errors}";
            });
            await Task.Delay(500);
            return result;
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

        /// <summary>prefers-color-scheme changed (called from main.js).</summary>
        [JSExport]
        public static void OnSchemeChanged(bool dark) => Dispatcher.UIThread.Post(() => BrowserTheme.SchemeChanged(dark));

        /// <summary>Ticks the Dark Theme checkbox on the start page like a click would; returns false when it is not shown.</summary>
        [JSExport]
        public static bool TickDarkThemeCheckBox(bool dark) => Dispatcher.UIThread.Invoke(() => BrowserShell.Instance.TickDarkTheme(dark));

        /// <summary>The resolved theme variant and background color, to see a live switch without a screenshot.</summary>
        [JSExport]
        public static string GetDarkThemeCheckBoxCenter() => Dispatcher.UIThread.Invoke(() => BrowserShell.Instance.DarkThemeCheckBoxCenter());

        /// <summary>Visible File menu commands, e.g. "Reload,Save Log As,Statistics" (for the e2e).</summary>
        [JSExport]
        public static string GetFileMenu() => Dispatcher.UIThread.Invoke(() => BrowserShell.Instance.FileMenuState());

        [JSExport]
        public static void SaveLogAs() => Dispatcher.UIThread.Invoke(() => BrowserShell.Instance.SaveLogAs());

        [JSExport]
        public static string ShowStatistics() => Dispatcher.UIThread.Invoke(() => { BrowserShell.Instance.ShowStatistics(); return BrowserShell.Instance.BuildControl?.Build.FindChild<Folder>(static f => f.Name.StartsWith(Strings.Statistics))?.Name ?? ""; });

        [JSExport]
        public static string GetThemeInfo() => Dispatcher.UIThread.Invoke(() => BrowserShell.Instance.ThemeInfo());

        [JSExport]
        public static bool GetDarkTheme() => StructuredLogViewer.SettingsService.UseDarkTheme;

        // ---- flat virtualized tree test hooks ----

        [JSExport]
        public static bool SetVirtualizedTree(bool on) { StructuredLogViewer.SettingsService.VirtualizedTree = on; return StructuredLogViewer.SettingsService.VirtualizedTree; }

        private static FlatTreeView Flat => BrowserShell.Instance?.BuildControl?.MainTreeControl as FlatTreeView;

        private static ListBoxItem RealizedItem(FlatTreeView tree, BaseNode node) =>
            tree.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => (i.DataContext as FlatRow)?.Node == node);

        private static bool InViewport(FlatTreeView tree, ListBoxItem item)
        {
            var p = item?.TranslatePoint(new Point(0, 0), tree);
            return p != null && p.Value.Y >= 0 && p.Value.Y + item.Bounds.Height <= tree.Bounds.Height + 1;
        }

        /// <summary>"flat|rows|realized" of the main tree.</summary>
        [JSExport]
        public static string TreeInfo() => Dispatcher.UIThread.Invoke(() =>
        {
            var t = Flat;
            return t == null ? "treeview" : "flat|" + t.RowCount + "|" + t.GetVisualDescendants().OfType<ListBoxItem>().Count();
        });

        /// <summary>Selects the last node (deepest in document order) matching a task name, expanding its ancestors. Returns "selectedOk|realized|inViewport|rowIndex".</summary>
        [JSExport]
        public static async Task<string> GoToDeepTask(string name)
        {
            var shell = BrowserShell.Instance;
            Microsoft.Build.Logging.StructuredLogger.Task node = null;
            shell.Document.Build.VisitAllChildren<Microsoft.Build.Logging.StructuredLogger.Task>(t => { if (t.Name != null && t.Name.Contains(name)) node = t; });
            if (node == null) return "none";
            await Dispatcher.UIThread.InvokeAsync(() => shell.BuildControl.SelectItem(node));
            await Task.Delay(800);
            return Dispatcher.UIThread.Invoke(() =>
            {
                var t = Flat;
                var item = RealizedItem(t, node);
                return (t.SelectedNode == node) + "|" + (item != null) + "|" + InViewport(t, item) + "|" + t.IndexOfNode(node);
            });
        }

        /// <summary>Window coordinates "x,y" of the realized row of the selected node (or the first row when asked), for real pointer input.</summary>
        [JSExport]
        public static string SelectedRowCenter() => Dispatcher.UIThread.Invoke(() =>
        {
            var t = Flat;
            var item = RealizedItem(t, t.SelectedNode);
            var p = item?.TranslatePoint(new Point(120, item.Bounds.Height / 2), BrowserShell.Instance);
            return p == null ? "" : $"{p.Value.X:0},{p.Value.Y:0}";
        });

        /// <summary>Selected node text and whether the context menu is open.</summary>
        [JSExport]
        public static string SelectionAndMenu() => Dispatcher.UIThread.Invoke(() =>
        {
            var t = Flat;
            return (t.SelectedNode?.ToString() ?? "") + "|" + (t.ContextMenu?.IsOpen == true) + "|" + (t.ContextMenu?.Items.Count ?? 0);
        });

        /// <summary>Selects the first row, then moves Down n times with real key events; reports "indexBefore|indexAfter|inViewport|realized".</summary>
        [JSExport]
        public static async Task<string> KeyboardDown(int count)
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                var t = Flat;
                t.SelectedItem = t.Items.Cast<object>().First();
                t.Focus();
            });
            await Task.Delay(300);
            int before = Dispatcher.UIThread.Invoke(() => Flat.SelectedIndex);
            for (int i = 0; i < count; i++)
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    var t = Flat;
                    var focused = TopLevel.GetTopLevel(t)?.FocusManager?.GetFocusedElement() as Control ?? t;
                    focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = focused });
                });
                if (i % 10 == 0) await Task.Delay(30);
            }

            await Task.Delay(600);
            return Dispatcher.UIThread.Invoke(() =>
            {
                var t = Flat;
                var item = RealizedItem(t, t.SelectedNode);
                return before + "|" + t.SelectedIndex + "|" + InViewport(t, item) + "|" + t.GetVisualDescendants().OfType<ListBoxItem>().Count();
            });
        }

        /// <summary>Expands the node with the most children, collapses and re-expands: "children|rows|realized|expandMs|rowsAfterCollapse|reexpandMs".</summary>
        [JSExport]
        public static async Task<string> BigNodeToggle()
        {
            var shell = BrowserShell.Instance;
            TreeNode big = null;
            shell.Document.Build.VisitAllChildren<TreeNode>(n => { if (big == null || n.Children.Count > big.Children.Count) big = n; });
            var t = Flat;
            var sw = Stopwatch.StartNew();
            await Dispatcher.UIThread.InvokeAsync(() => shell.BuildControl.SelectItem(big));
            await Dispatcher.UIThread.InvokeAsync(() => big.IsExpanded = false);
            await Task.Delay(300);
            int collapsedRows = Dispatcher.UIThread.Invoke(() => t.RowCount);
            sw.Restart();
            await Dispatcher.UIThread.InvokeAsync(() => big.IsExpanded = true);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            long expandMs = sw.ElapsedMilliseconds;
            await Task.Delay(300);
            var (rows, realized) = Dispatcher.UIThread.Invoke(() => (t.RowCount, t.GetVisualDescendants().OfType<ListBoxItem>().Count()));
            await Dispatcher.UIThread.InvokeAsync(() => big.IsExpanded = false);
            await Task.Delay(300);
            int after = Dispatcher.UIThread.Invoke(() => t.RowCount);
            sw.Restart();
            await Dispatcher.UIThread.InvokeAsync(() => big.IsExpanded = true);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            return big.Children.Count + "|" + rows + "|" + realized + "|" + expandMs + "|" + (after == collapsedRows) + "|" + sw.ElapsedMilliseconds;
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
