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

            int graphFailures = 0;
            var project = control.Build.FindFirstDescendant<Microsoft.Build.Logging.StructuredLogger.Project>(_ => true);

            var prg = control.ProjectReferenceGraphHost;
            Pump(1500);
            Save(window, Path.Combine(outDir, "desktop-6-project-references.png"));
            Console.WriteLine("project reference graph vertices: " + (prg?.GraphControl.DisplayedCount ?? -1));
            if (prg == null || prg.GraphControl.DisplayedCount == 0) graphFailures++;

            if (prg != null)
            {
                var first = prg.Graph.Vertices.OrderByDescending(v => v.InDegree).First();
                prg.Locate(first.Title);
                Pump(800);
                Save(window, Path.Combine(outDir, "desktop-6b-project-references-selected.png"));
                Console.WriteLine("selected: " + prg.GraphControl.SelectedVertex?.Title);
                if (prg.GraphControl.SelectedVertex == null) graphFailures++;
            }

            var tg = control.ShowTargetGraph(project);
            Pump(1500);
            Save(window, Path.Combine(outDir, "desktop-7-targets.png"));
            Console.WriteLine("target graph vertices: " + (tg?.GraphControl.DisplayedCount ?? -1));
            if (tg == null || tg.GraphControl.DisplayedCount == 0) graphFailures++;

            var ng = control.ShowNuGetGraph(project);
            Pump(1500);
            Save(window, Path.Combine(outDir, "desktop-8-nuget.png"));
            Console.WriteLine("nuget graph vertices: " + (ng?.GraphControl.DisplayedCount ?? -1));

            var pg = control.ShowPropertyGraph(project);
            Pump(1500);
            Save(window, Path.Combine(outDir, "desktop-9-properties.png"));
            Console.WriteLine("property graph vertices: " + (pg?.GraphControl.DisplayedCount ?? -1));
            if (pg == null || pg.GraphControl.DisplayedCount == 0) graphFailures++;

            var graphFile = Environment.GetEnvironmentVariable("HEXAD_SCRATCH") is { } scratch ? Path.Combine(scratch, "sample.graph") : null;
            if (graphFile != null && File.Exists(graphFile))
            {
                window.OpenGraphFile(graphFile);
                Pump(1000);
                Save(window, Path.Combine(outDir, "desktop-10-open-graph.png"));
                var openedHost = window.GetVisualDescendants().OfType<GraphHostControl>().FirstOrDefault();
                Console.WriteLine("open graph vertices: " + (openedHost?.GraphControl.DisplayedCount ?? -1));
                if (openedHost == null || openedHost.GraphControl.DisplayedCount != 5) graphFailures++;
            }

            return control.searchLogControl.ResultsList.ItemCount > 0 && control.TracingControl.BlockCount > 0 && graphFailures == 0 ? 0 : 1;
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
