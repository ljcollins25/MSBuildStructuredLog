using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Web;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Browser
{
    /// <summary>Spike: load a binlog from ?url= and show the load statistics.</summary>
    public class MainView : UserControl
    {
        private readonly TextBlock text = new TextBlock { Text = "Loading..." };

        public MainView()
        {
            Content = new ScrollViewer { Content = text };
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await JsInterop.InitializeAsync();
                    await LoadAsync();
                }
                catch (Exception ex)
                {
                    text.Text = ex.ToString();
                }
            });
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            NameValueCollection query = HttpUtility.ParseQueryString(JsInterop.GetQuery());
            string url = query["url"];
            if (string.IsNullOrEmpty(url))
            {
                text.Text = "Pass ?url=<binlog url>";
                return;
            }

            double t0 = JsInterop.Now();
            byte[] bytes = await JsInterop.FetchBytesAsync(url);
            double t1 = JsInterop.Now();
            text.Text = $"Fetched {bytes.Length:N0} bytes in {t1 - t0:N0} ms, parsing...";
            await System.Threading.Tasks.Task.Delay(50);

            var build = BinaryLog.ReadBuild(new MemoryStream(bytes));
            double t2 = JsInterop.Now();
            text.Text =
                $"Fetched {bytes.Length:N0} bytes in {t1 - t0:N0} ms\n" +
                $"Parsed in {t2 - t1:N0} ms\n" +
                $"Succeeded: {build.Succeeded}, duration: {build.Duration}\n" +
                $"Nodes: {build.FindChildrenRecursive<BaseNode>().Count:N0}\n" +
                $"Strings: {build.StringTable.Instances.Count():N0}\n" +
                $"GC heap: {GC.GetTotalMemory(false) / 1048576} MB";
        }
    }
}
