using System;
using System.IO;
using System.Threading.Tasks;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
            status.Text = "Open a .binlog (button, drag and drop, or ?url=...).";

            Dispatcher.UIThread.Post(async () => await StartupAsync(), DispatcherPriority.Background);
        }

        private async Task StartupAsync()
        {
            string url = HttpUtility.ParseQueryString(JsInterop.GetQuery())["url"];
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                status.Text = "Downloading " + url + " ...";
                var abs = new Uri(new Uri(JsInterop.GetHref()), url).AbsoluteUri;
                await OpenSourceAsync(Path.GetFileName(new Uri(abs).AbsolutePath), JsPositionedSource.FromUrl(abs));
            }
            catch (Exception ex)
            {
                status.Text = "Could not load the URL: " + ex.Message;
                JsInterop.Report("ERROR " + ex);
            }
        }

        public Task OpenPendingFileAsync(string name) => OpenSourceAsync(name, JsPositionedSource.FromPendingFile(name));

        /// <summary>Opens a log from a positioned source: the compressed file is read in ranges, never held whole in memory.</summary>
        public async Task OpenSourceAsync(string name, JsPositionedSource source)
        {
            try
            {
                status.Text = $"Parsing {name} ({source.Length / 1048576.0:N1} MB)... the page can be unresponsive until it is done.";
                await Task.Delay(50);
                double t0 = JsInterop.Now();
                Document = BinlogDocument.Load(new Microsoft.Build.Logging.StructuredLogger.Paging.PositionedSourceStream(source),
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
