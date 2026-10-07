using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StructuredLogViewer.Avalonia.Controls;

namespace StructuredLogViewer.Avalonia
{
    /// <summary>
    /// Launches the desktop head (the real MainWindow over the shared UI library) without a display and saves screenshots.
    ///   dotnet run -c Release --project src/StructuredLogViewer.Avalonia.Headless -- &lt;log.binlog&gt; &lt;outDir&gt; [--dark]
    /// Exit code 0 when the welcome screen and the loaded log both rendered.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: <log.binlog> <outDir> [--dark]");
                return 2;
            }

            string log = Path.GetFullPath(args[0]);
            string outDir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(outDir);

            // settings and recent items must not touch the real profile
            string data = Path.Combine(outDir, "data");
            Environment.SetEnvironmentVariable("MSBUILDSTRUCTUREDLOG_DATA_DIR", data);
            if (args.Contains("--dark"))
            {
                SettingsService.UseDarkTheme = true;
            }

            App.CreateMainWindow = () => new MainWindow();
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            var window = new MainWindow { Width = 1400, Height = 900 };
            window.Show();
            Pump(500);
            Save(window, Path.Combine(outDir, "desktop-1-welcome.png"));

            var stopwatch = Stopwatch.StartNew();
            if (!window.OpenFile(log))
            {
                Console.Error.WriteLine("MainWindow.OpenFile refused " + log);
                return 1;
            }

            BuildControl control = null;
            while (stopwatch.Elapsed.TotalSeconds < 90 && (control = window.GetVisualDescendants().OfType<BuildControl>().FirstOrDefault()) == null)
            {
                Pump(100);
            }

            if (control == null)
            {
                Console.Error.WriteLine("the log did not load in 90 s");
                Save(window, Path.Combine(outDir, "desktop-failed.png"));
                return 1;
            }

            Console.WriteLine($"loaded {Path.GetFileName(log)} in {stopwatch.Elapsed.TotalSeconds:N1} s");
            Pump(1500);
            Save(window, Path.Combine(outDir, "desktop-2-tree.png"));

            control.SelectSearchTab("CoreCompile");
            Pump(2500);
            Save(window, Path.Combine(outDir, "desktop-3-search.png"));
            Console.WriteLine("search results: " + control.searchLogControl.ResultsList.ItemCount);

            control.SelectTree();
            control.GoToTimeLine();
            var task = control.Build.FindFirstDescendant<Microsoft.Build.Logging.StructuredLogger.Task>(t => t.Name == "Csc");
            if (task != null)
            {
                control.SelectItem(task);
                control.GoToTimeLine();
            }

            Pump(1500);
            Save(window, Path.Combine(outDir, "desktop-4-timeline.png"));
            Console.WriteLine("timeline blocks: " + control.TimelineControl.TextBlocks.Count);

            control.SelectTree();
            if (task != null)
            {
                control.SelectItem(task);
                control.GoToTracing();
            }

            Pump(2000);
            Save(window, Path.Combine(outDir, "desktop-5-tracing.png"));
            Console.WriteLine("tracing blocks: " + control.TracingControl.BlockCount);
            return control.searchLogControl.ResultsList.ItemCount > 0 && control.TracingControl.BlockCount > 0 ? 0 : 1;
        }

        private static void Pump(int milliseconds)
        {
            var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(20);
            }
        }

        private static void Save(global::Avalonia.Controls.Window window, string path)
        {
            var frame = window.CaptureRenderedFrame();
            frame?.Save(path);
            Console.WriteLine("saved " + path + (frame == null ? " (no frame)" : ""));
        }
    }
}
