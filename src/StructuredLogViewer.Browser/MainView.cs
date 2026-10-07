using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Browser
{
    public class MainView : UserControl
    {
        public const string SizeWarning =
            "Big logs do not fit yet: the browser build runs out of memory above roughly 50 MB (about 200 MB decompressed). Larger logs will need paged loading.";

        public static MainView Instance { get; private set; }

        private readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox searchBox = new TextBox { Width = 320, Watermark = "Search (e.g. Csc, $task Copy, under(...))" };
        private readonly TreeView tree = new TreeView();
        private readonly ListBox results = new ListBox();
        private readonly TextBox details = Mono();
        private readonly TextBox fileText = Mono();
        private readonly TextBox fileFilter = new TextBox { Watermark = "Find in files (text)" };
        private readonly ListBox fileList = new ListBox();
        private readonly TabControl leftTabs = new TabControl();
        private readonly TabControl rightTabs = new TabControl();
        private readonly TextBlock fileTitle = new TextBlock { Margin = new Thickness(4) };

        private BinlogDocument document;
        private IReadOnlyList<SearchResult> lastResults = Array.Empty<SearchResult>();
        private BaseNode selected;
        private string openFile;

        public MainView()
        {
            Instance = this;
            Content = BuildLayout();
            ShowStart();
            Dispatcher.UIThread.Post(async () => await StartupAsync());
        }

        private static TextBox Mono() => new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            TextWrapping = TextWrapping.NoWrap,
        };

        private Control BuildLayout()
        {
            var open = new Button { Content = "Open binlog..." };
            open.Click += (_, _) => JsInterop.PickFile();
            searchBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    await RunSearchAsync(searchBox.Text);
                }
            };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Spacing = 4 };
            top.Children.Add(open);
            top.Children.Add(searchBox);
            top.Children.Add(status);

            tree.ItemTemplate = new FuncTreeDataTemplate<NodeViewModel>(
                (n, _) => new TextBlock { Text = n.Text },
                n => n.Children);
            tree.SelectionChanged += (_, e) =>
            {
                if (tree.SelectedItem is NodeViewModel vm)
                {
                    Select(vm.Node);
                }
            };

            results.SelectionChanged += (_, _) =>
            {
                if (results.SelectedIndex >= 0 && results.SelectedIndex < lastResults.Count)
                {
                    Select(lastResults[results.SelectedIndex].Node);
                }
            };

            fileFilter.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    RefreshFileList(fileFilter.Text);
                }
            };
            fileList.SelectionChanged += (_, _) =>
            {
                if (fileList.SelectedItem is FileEntry entry)
                {
                    OpenFile(entry.Path, entry.Line);
                }
            };

            leftTabs.Items.Add(new TabItem { Header = "Log", Content = tree });
            leftTabs.Items.Add(new TabItem { Header = "Search results", Content = results });
            var filesPanel = new DockPanel();
            DockPanel.SetDock(fileFilter, Dock.Top);
            filesPanel.Children.Add(fileFilter);
            filesPanel.Children.Add(fileList);
            leftTabs.Items.Add(new TabItem { Header = "Files", Content = filesPanel });

            rightTabs.Items.Add(new TabItem { Header = "Details", Content = details });
            var filePanel = new DockPanel();
            DockPanel.SetDock(fileTitle, Dock.Top);
            filePanel.Children.Add(fileTitle);
            filePanel.Children.Add(fileText);
            rightTabs.Items.Add(new TabItem { Header = "File", Content = filePanel });

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,*") };
            Grid.SetColumn(leftTabs, 0);
            var splitter = new GridSplitter { Width = 5 };
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(rightTabs, 2);
            split.Children.Add(leftTabs);
            split.Children.Add(splitter);
            split.Children.Add(rightTabs);

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            root.Children.Add(split);
            return root;
        }

        private void ShowStart()
        {
            status.Text = "Drop a .binlog here, use Open, or pass ?url=. " + SizeWarning;
            details.Text =
                "MSBuild Structured Log Viewer (browser preview)\n\n" +
                "Open a .binlog by drag and drop, the Open button, or add ?url=<binlog url> to the address.\n" +
                "The log is parsed in your browser; nothing is uploaded.\n\n" + SizeWarning;
        }

        private async Task StartupAsync()
        {
            string url = HttpUtility.ParseQueryString(JsInterop.GetQuery())["url"];
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    status.Text = "Downloading " + url + " ...";
                    byte[] bytes = await JsInterop.FetchBytesAsync(url);
                    await OpenBytesAsync(Path.GetFileName(new Uri(new Uri(JsInterop.GetHref()), url).AbsolutePath), bytes);
                }
                catch (Exception ex)
                {
                    status.Text = "Could not load the URL: " + ex.Message;
                    JsInterop.Report("ERROR " + ex);
                }
            }
        }

        public async Task OpenBytesAsync(string name, byte[] bytes)
        {
            try
            {
                status.Text = $"Parsing {name} ({bytes.Length / 1048576.0:N1} MB)... the page can be unresponsive until it is done.";
                await Task.Delay(50);
                double t0 = JsInterop.Now();
                var progress = new Action<double>(ratio =>
                {
                    JsInterop.Report($"progress {ratio:P0} heap {GC.GetTotalMemory(false) / 1048576} MB");
                });

                document = BinlogDocument.Load(new MemoryStream(bytes), progress);
                double t1 = JsInterop.Now();
                var build = document.Build;
                var root = new NodeViewModel(build);
                tree.ItemsSource = new[] { root };
                status.Text = $"{name}: parsed in {(t1 - t0) / 1000:N1} s, {document.Files.Count:N0} embedded files, " +
                    $"{(build.Succeeded ? "succeeded" : "failed")}";
                details.Text = build.GetFullText();
                RefreshFileList(null);
                JsInterop.Report($"Parsed {name} in {t1 - t0:N0} ms; nodes {build.FindChildrenRecursive<BaseNode>().Count:N0}; files {document.Files.Count}; heap {GC.GetTotalMemory(false) / 1048576} MB");
            }
            catch (Exception ex)
            {
                status.Text = "Could not open " + name + ": " + ex.Message;
                JsInterop.Report("ERROR " + ex);
            }
        }

        public async Task RunSearchAsync(string query)
        {
            if (document == null)
            {
                return;
            }

            status.Text = "Searching...";
            await Task.Delay(20);
            double t0 = JsInterop.Now();
            lastResults = document.Search(query);
            results.ItemsSource = lastResults.Select(r => (r.Node.TypeName + ": " + new NodeViewModel(r.Node).Text)).ToList();
            leftTabs.SelectedIndex = 1;
            status.Text = $"{lastResults.Count} results for '{query}' in {JsInterop.Now() - t0:N0} ms" + (lastResults.Count >= BinlogDocument.MaxSearchResults ? " (limited)" : "");
        }

        public void Select(BaseNode node)
        {
            selected = node;
            details.Text = document?.GetDetails(node) ?? "";
            rightTabs.SelectedIndex = 0;
            var path = document?.FindSourceFile(node);
            if (path != null)
            {
                OpenFile(path, 1, activate: false);
            }
        }

        public void OpenFile(string path, int line, bool activate = true)
        {
            string text = document?.GetFileText(path);
            if (text == null)
            {
                return;
            }

            openFile = path;
            fileTitle.Text = path + (line > 1 ? ":" + line : "");
            fileText.Text = text;
            if (activate)
            {
                rightTabs.SelectedIndex = 1;
            }

            if (line > 1)
            {
                int position = 0;
                for (int i = 1; i < line && position >= 0; i++)
                {
                    position = text.IndexOf('\n', position) + 1;
                    if (position == 0)
                    {
                        break;
                    }
                }

                fileText.CaretIndex = Math.Max(0, position);
            }
        }

        private record FileEntry(string Path, int Line, string Display)
        {
            public override string ToString() => Display;
        }

        public void RefreshFileList(string filter)
        {
            if (document == null)
            {
                return;
            }

            var entries = new List<FileEntry>();
            foreach (var path in document.FindFiles(filter))
            {
                entries.Add(new FileEntry(path, 1, path));
            }

            if (!string.IsNullOrEmpty(filter))
            {
                foreach (var hit in document.FindInFiles(filter))
                {
                    entries.Add(new FileEntry(hit.File, hit.Line, hit.File + ":" + hit.Line + "  " + hit.Text));
                }
            }

            fileList.ItemsSource = entries;
        }

        /// <summary>State for the smoke test (the UI is a canvas, so it cannot be read from the DOM).</summary>
        public string GetStateJson()
        {
            string Esc(string s) => System.Text.Json.JsonSerializer.Serialize(s ?? "");
            var sb = new StringBuilder("{");
            sb.Append("\"loaded\":").Append(document != null ? "true" : "false");
            sb.Append(",\"status\":").Append(Esc(status.Text));
            sb.Append(",\"results\":").Append(lastResults.Count);
            sb.Append(",\"firstResult\":").Append(Esc(lastResults.FirstOrDefault()?.Node.Title));
            sb.Append(",\"selected\":").Append(Esc(selected?.Title));
            sb.Append(",\"details\":").Append(Esc(details.Text));
            sb.Append(",\"openFile\":").Append(Esc(openFile));
            sb.Append(",\"fileText\":").Append(Esc(fileText.Text?.Length > 2000 ? fileText.Text.Substring(0, 2000) : fileText.Text));
            sb.Append(",\"files\":").Append(document?.Files.Count ?? 0);
            sb.Append("}");
            return sb.ToString();
        }

        public async Task<string> SearchAndSelectFirstAsync(string query)
        {
            searchBox.Text = query;
            await RunSearchAsync(query);
            if (lastResults.Count > 0)
            {
                results.SelectedIndex = 0;
            }

            return GetStateJson();
        }

        public string OpenFirstSourceFile(string filter)
        {
            var path = document?.FindFiles(filter).FirstOrDefault();
            if (path != null)
            {
                OpenFile(path, 1);
            }

            return GetStateJson();
        }
    }
}
