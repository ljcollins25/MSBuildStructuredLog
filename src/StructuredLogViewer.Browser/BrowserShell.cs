using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Task = System.Threading.Tasks.Task;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;
using StructuredLogViewer.Avalonia.Controls;

namespace StructuredLogViewer.Browser
{
    /// <summary>
    /// The browser head: a single view hosting the shared BuildControl (the same content the desktop
    /// MainWindow shows), with the shared start page for opening a log. Loading is browser-specific
    /// (file picker, drag and drop and ?url= in main.js / JsInterop; parsing in BinlogDocument).
    /// </summary>
    public class BrowserShell : UserControl
    {
        public static BrowserShell Instance { get; private set; }

        // not shown anywhere: read back by the e2e state, and mirrored onto the start page while no log is open
        private string statusText = "Open a .binlog (file picker, drag and drop, URL, or ?url=...).";
        private readonly ContentPresenter content = new ContentPresenter();
        private BuildControl buildControl;

        public string StatusText => statusText;

        public BinlogDocument Document { get; private set; }
        public BuildControl BuildControl => buildControl;

        public BrowserShell()
        {
            Instance = this;
            // the desktop MainWindow's menu (the parts that make sense in a browser), nothing else
            var startPage = new MenuItem { Header = "Start Page" };
            startPage.Click += (s, e) => ShowWelcome();
            var openLog = new MenuItem { Header = "_Open Log..." };
            openLog.Click += (s, e) => JsInterop.PickFile();
            var file = new MenuItem { Header = "_File" };
            file.Items.Add(startPage);
            file.Items.Add(openLog);
            var menu = new Menu();
            menu.Items.Add(file);
            var dock = new DockPanel();
            DockPanel.SetDock(menu, Dock.Top);
            dock.Children.Add(menu);
            dock.Children.Add(content);
            Content = dock;
            // the desktop MainWindow's Ctrl+F / Ctrl+Shift+F; Ctrl+0 and Ctrl+wheel stay the browser's own page zoom
            ShowWelcome();

            Dispatcher.UIThread.Post(async () => await StartupAsync(), DispatcherPriority.Background);
        }

        private WelcomeScreen welcome;

        private void SetStatus(string text)
        {
            statusText = text;
            if (buildControl == null && welcome != null)
            {
                welcome.Message = text;
            }
        }

        /// <summary>The shared start page, with what a browser cannot do hidden and Open from URL added.</summary>
        private void ShowWelcome(string message = "", string url = null)
        {
            welcome = new WelcomeScreen { ShowOpenProject = false, ShowOpenFromUrl = true, Message = message, Url = url };
            welcome.OpenLogFileRequested += () => JsInterop.PickFile();
            welcome.OpenUrlRequested += async u => await OpenUrlAsync(u);
            // like the desktop MainWindow: the theme follows the checkbox immediately
            welcome.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(WelcomeScreen.UseDarkTheme))
                {
                    StructuredLogViewer.Avalonia.App.UpdateTheme();
                }
            };

            content.Content = welcome;
        }

        private async Task StartupAsync()
        {
            string url = HttpUtility.ParseQueryString(JsInterop.GetQuery())["url"];
            if (!string.IsNullOrEmpty(url))
            {
                await OpenUrlAsync(url);
            }
        }

        /// <summary>Opens a log from a URL through the ILogSource seam; failures show on the start page. Returns the error or "".</summary>
        public async Task<string> OpenUrlAsync(string url)
        {
            try
            {
                var source = await LogSources.Provider.OpenAsync(url);
                SetStatus($"Downloading {source.Name}" + (source.Length is long n ? $" ({n / 1048576.0:N1} MB)" : "") +
                    (source.SupportsRange ? " with Range requests" : "") + " ...");
                byte[] bytes = await source.ReadAllAsync(new Progress<double>(p => SetStatus($"Downloading {source.Name}: {p:P0}")));
                await OpenBytesAsync(source.Name, bytes);
                return "";
            }
            catch (Exception ex)
            {
                string message = ex is LogSourceException ? ex.Message : "Could not open the URL: " + ex.Message;
                SetStatus(message);
                JsInterop.Report("ERROR " + ex);
                if (buildControl == null)
                {
                    ShowWelcome(message, url);
                }

                return message;
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // on the TopLevel: with nothing focused, key events never pass through this control
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        }

        private void OnShortcut(object sender, KeyEventArgs e)
        {
            if (buildControl == null || e.Key != Key.F)
            {
                return;
            }

            if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                buildControl.SelectFindInFilesTab();
                e.Handled = true;
            }
            else if (e.KeyModifiers == KeyModifiers.Control)
            {
                buildControl.FocusSearch();
                e.Handled = true;
            }
        }

        public bool TickDarkTheme(bool dark)
        {
            var box = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<CheckBox>()
                .FirstOrDefault(c => c.IsEffectivelyVisible && (c.Content as string) == "Dark Theme");
            if (box == null)
            {
                return false;
            }

            box.IsChecked = dark;
            return true;
        }

        /// <summary>Center of the Dark Theme checkbox in page pixels ("x,y"), so a test can click it for real; "" when not shown.</summary>
        public string DarkThemeCheckBoxCenter()
        {
            var box = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<CheckBox>()
                .FirstOrDefault(c => c.IsEffectivelyVisible && (c.Content as string) == "Dark Theme");
            var p = box?.TranslatePoint(new Point(10, box.Bounds.Height / 2), this);
            return p == null ? "" : $"{p.Value.X:0},{p.Value.Y:0}";
        }

        public string ThemeInfo()
        {
            var variant = Application.Current.ActualThemeVariant;
            string background = "";
            if (Application.Current.TryGetResource("Theme_Background", variant, out var res) && res is ISolidColorBrush brush)
            {
                background = brush.Color.ToString();
            }

            var box = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<CheckBox>()
                .FirstOrDefault(c => (c.Content as string) == "Dark Theme");
            return variant + "|" + background + "|" + (box == null ? "nobox" : box.IsChecked == true ? "checked" : "unchecked");
        }

        public string WelcomeControlsVisible()
        {
            return Dispatcher.UIThread.Invoke(() => string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(
                global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<Control>(),
                c => c.IsEffectivelyVisible && (c.Name == "openFromUrl" || c.Name == "openUrlButton" || c.Name == "urlText" || (c is Button b && b.Command == welcome?.OpenProjectCommand))), c => c.Name ?? "openProject")));
        }

        public async Task OpenBytesAsync(string name, byte[] bytes)
        {
            try
            {
                SetStatus($"Parsing {name} ({bytes.Length / 1048576.0:N1} MB)... the page can be unresponsive until it is done.");
                await Task.Delay(50);
                double t0 = JsInterop.Now();
                Document = BinlogDocument.Load(new MemoryStream(bytes),
                    ratio => JsInterop.Report($"progress {ratio:P0} heap {GC.GetTotalMemory(false) / 1048576} MB"));
                double t1 = JsInterop.Now();

                buildControl?.Dispose();
                buildControl = new BuildControl(Document.Build, name);
                content.Content = buildControl;
                SetStatus($"{name}: parsed in {(t1 - t0) / 1000:N1} s, {Document.Files.Count:N0} embedded files, " +
                    (Document.Build.Succeeded ? "succeeded" : "failed"));
                JsInterop.Report($"Parsed {name} in {t1 - t0:N0} ms; files {Document.Files.Count}; heap {GC.GetTotalMemory(false) / 1048576} MB");
            }
            catch (Exception ex)
            {
                SetStatus("Could not open " + name + ": " + ex.Message);
                JsInterop.Report("ERROR " + ex);
            }
        }
    }
}
