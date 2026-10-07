using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Task = System.Threading.Tasks.Task;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;
using StructuredLogViewer.Avalonia.Controls;

namespace StructuredLogViewer.Browser
{
    /// <summary>
    /// The browser head: a single view hosting the shared BuildControl (the same content the desktop
    /// MainWindow shows), plus a minimal bar for opening a log. Loading is browser-specific
    /// (file picker, drag and drop and ?url= in main.js / JsInterop; parsing in BinlogDocument).
    /// </summary>
    public class BrowserShell : UserControl
    {
        public static BrowserShell Instance { get; private set; }

        private readonly TextBlock status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly ContentPresenter content = new ContentPresenter();
        private BuildControl buildControl;

        public string StatusText => status.Text;

        public BinlogDocument Document { get; private set; }
        public BuildControl BuildControl => buildControl;

        public BrowserShell()
        {
            Instance = this;
            var open = new Button { Content = "Open log..." };
            open.Click += (s, e) => JsInterop.PickFile();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            bar.Children.Add(open);
            bar.Children.Add(status);

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(content);
            Content = dock;
            status.Text = "Open a .binlog (button, drag and drop, URL, or ?url=...).";
            ShowWelcome();

            Dispatcher.UIThread.Post(async () => await StartupAsync(), DispatcherPriority.Background);
        }

        private WelcomeScreen welcome;

        /// <summary>The shared start page, with what a browser cannot do hidden and Open from URL added.</summary>
        private void ShowWelcome(string message = "", string url = null)
        {
            welcome = new WelcomeScreen { ShowOpenProject = false, ShowOpenFromUrl = true, Message = message, Url = url };
            welcome.OpenLogFileRequested += () => JsInterop.PickFile();
            welcome.OpenUrlRequested += async u => await OpenUrlAsync(u);
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
                status.Text = $"Downloading {source.Name}" + (source.Length is long n ? $" ({n / 1048576.0:N1} MB)" : "") +
                    (source.SupportsRange ? " with Range requests" : "") + " ...";
                byte[] bytes = await source.ReadAllAsync(new Progress<double>(p => status.Text = $"Downloading {source.Name}: {p:P0}"));
                await OpenBytesAsync(source.Name, bytes);
                return "";
            }
            catch (Exception ex)
            {
                string message = ex is LogSourceException ? ex.Message : "Could not open the URL: " + ex.Message;
                status.Text = message;
                JsInterop.Report("ERROR " + ex);
                if (buildControl == null)
                {
                    ShowWelcome(message, url);
                }

                return message;
            }
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
                status.Text = $"Parsing {name} ({bytes.Length / 1048576.0:N1} MB)... the page can be unresponsive until it is done.";
                await Task.Delay(50);
                double t0 = JsInterop.Now();
                Document = BinlogDocument.Load(new MemoryStream(bytes),
                    ratio => JsInterop.Report($"progress {ratio:P0} heap {GC.GetTotalMemory(false) / 1048576} MB"));
                double t1 = JsInterop.Now();

                buildControl?.Dispose();
                buildControl = new BuildControl(Document.Build, name);
                content.Content = buildControl;
                status.Text = $"{name}: parsed in {(t1 - t0) / 1000:N1} s, {Document.Files.Count:N0} embedded files, " +
                    (Document.Build.Succeeded ? "succeeded" : "failed");
                JsInterop.Report($"Parsed {name} in {t1 - t0:N0} ms; files {Document.Files.Count}; heap {GC.GetTotalMemory(false) / 1048576} MB");
            }
            catch (Exception ex)
            {
                status.Text = "Could not open " + name + ": " + ex.Message;
                JsInterop.Report("ERROR " + ex);
            }
        }
    }
}
