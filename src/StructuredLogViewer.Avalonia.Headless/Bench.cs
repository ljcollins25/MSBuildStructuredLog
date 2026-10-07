using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Build.Logging.StructuredLogger;
using StructuredLogViewer.Avalonia.Controls;

namespace StructuredLogViewer.Avalonia
{
    /// <summary>
    /// Main tree benchmark on the desktop head (headless Skia):
    ///   dotnet run -c Release --project src/StructuredLogViewer.Avalonia.Headless -- --bench &lt;log.binlog&gt; &lt;outDir&gt; [--flat|--treeview] [--dark]
    /// Prints one key=value line per measurement: time to first tree, time to expand the node with the
    /// most children, time to scroll through it (frame times), and managed memory after a full GC.
    /// </summary>
    internal static class Bench
    {
        public static int Run(string[] args)
        {
            string log = Path.GetFullPath(args[1]);
            string outDir = Path.GetFullPath(args[2]);
            bool flat = args.Contains("--flat");
            Directory.CreateDirectory(outDir);
            Environment.SetEnvironmentVariable("MSBUILDSTRUCTUREDLOG_DATA_DIR", Path.Combine(outDir, "data"));
            SettingsService.VirtualizedTree = flat;
            SettingsService.UseDarkTheme = args.Contains("--dark");
            Console.WriteLine("tree=" + (flat ? "flat" : "treeview"));

            App.CreateMainWindow = () => new MainWindow();
            AppBuilder.Configure<App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var window = new MainWindow { Width = 1400, Height = 900 };
            window.Show();
            Pump(300);

            var total = Stopwatch.StartNew();
            var sw = Stopwatch.StartNew();
            if (!window.OpenFile(log))
            {
                return 1;
            }

            BuildControl control = null;
            while (total.Elapsed.TotalSeconds < 600 && (control = window.GetVisualDescendants().OfType<BuildControl>().FirstOrDefault()) == null)
            {
                Pump(20);
            }

            double loadMs = sw.Elapsed.TotalMilliseconds;
            // first tree: until the main tree has realized rows
            while (total.Elapsed.TotalSeconds < 600 && control.MainTreeControl.GetVisualDescendants().OfType<ListBoxItem>().Count() + control.MainTreeControl.GetVisualDescendants().OfType<TreeViewItem>().Count() == 0)
            {
                Pump(20);
            }

            Console.WriteLine($"time_to_first_tree_ms={sw.Elapsed.TotalMilliseconds:N0} (open+parse+first rows; parse part {loadMs:N0})");
            Pump(500);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Console.WriteLine($"managed_mb_after_load={GC.GetTotalMemory(true) / 1048576}");
            Save(window, Path.Combine(outDir, "bench-1-tree.png"));

            // the node with the most children
            TreeNode big = null;
            int max = 0;
            control.Build.VisitAllChildren<TreeNode>(n => { if (n.Children.Count > max) { max = n.Children.Count; big = n; } });
            Console.WriteLine($"big_node={big?.GetType().Name} children={max}");

            sw.Restart();
            control.SelectItem(big);
            big.IsExpanded = true;
            bool done = false;
            // done when layout settled: two consecutive empty pumps after the node's rows exist
            for (int i = 0; i < 2000 && !done; i++)
            {
                Pump(10);
                done = i > 3 && control.IsTreeLayoutSettled();
            }

            Console.WriteLine($"expand_ms={sw.Elapsed.TotalMilliseconds:N0} settled={done}");
            Pump(300);
            Save(window, Path.Combine(outDir, "bench-2-expanded.png"));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Console.WriteLine($"managed_mb_expanded={GC.GetTotalMemory(true) / 1048576}");
            Console.WriteLine($"realized_rows={control.MainTreeControl.GetVisualDescendants().OfType<ListBoxItem>().Count() + control.MainTreeControl.GetVisualDescendants().OfType<TreeViewItem>().Count()}");

            // scroll through the big node: a viewport per frame, frame time = pump of one render tick
            var viewer = control.MainTreeControl.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (viewer != null)
            {
                var times = new System.Collections.Generic.List<double>();
                var all = Stopwatch.StartNew();
                double step = viewer.Viewport.Height * 4;
                viewer.Offset = new Vector(0, 0);
                Pump(50);
                int guard = 0;
                while (viewer.Offset.Y + viewer.Viewport.Height < viewer.Extent.Height - 1 && guard++ < 5000)
                {
                    var f = Stopwatch.StartNew();
                    viewer.Offset = new Vector(0, Math.Min(viewer.Offset.Y + step, viewer.Extent.Height - viewer.Viewport.Height));
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    times.Add(f.Elapsed.TotalMilliseconds);
                    if (all.Elapsed.TotalSeconds > 300) break;
                }

                times.Sort();
                if (times.Count > 0)
                {
                    Console.WriteLine($"scroll_to_end_ms={all.Elapsed.TotalMilliseconds:N0} frames={times.Count} extent_px={viewer.Extent.Height:N0} frame_median_ms={times[times.Count / 2]:N1} frame_p95_ms={times[(int)(times.Count * 0.95)]:N1} frame_max_ms={times[^1]:N1}");
                }

                // wheel-like scrolling: 3 rows (~54 px) per frame for 300 frames from the top of the big node
                {
                    var small = new System.Collections.Generic.List<double>();
                    viewer.Offset = new Vector(0, 0);
                    Pump(50);
                    for (int i = 0; i < 300; i++)
                    {
                        var f2 = Stopwatch.StartNew();
                        viewer.Offset = new Vector(0, Math.Min(viewer.Offset.Y + 54, viewer.Extent.Height - viewer.Viewport.Height));
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        small.Add(f2.Elapsed.TotalMilliseconds);
                    }

                    small.Sort();
                    Console.WriteLine($"wheel_scroll frames={small.Count} frame_median_ms={small[small.Count / 2]:N1} frame_p95_ms={small[(int)(small.Count * 0.95)]:N1} frame_max_ms={small[^1]:N1}");
                }

                Save(window, Path.Combine(outDir, "bench-3-scrolled-end.png"));
            }

            // collapse the big node again
            sw.Restart();
            big.IsExpanded = false;
            for (int i = 0; i < 2000; i++)
            {
                Pump(10);
                if (i > 3 && control.IsTreeLayoutSettled()) break;
            }

            Console.WriteLine($"collapse_ms={sw.Elapsed.TotalMilliseconds:N0}");
            sw.Restart();
            big.IsExpanded = true;
            for (int i = 0; i < 2000; i++)
            {
                Pump(10);
                if (i > 3 && control.IsTreeLayoutSettled()) break;
            }

            Console.WriteLine($"re_expand_ms={sw.Elapsed.TotalMilliseconds:N0}");
            Console.WriteLine($"managed_mb_peak_end={GC.GetTotalMemory(false) / 1048576} gc_gen2={GC.CollectionCount(2)}");
            return 0;
        }

        private static void Pump(int milliseconds)
        {
            var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            do
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(1);
            }
            while (DateTime.UtcNow < end);
        }

        private static void Save(Window window, string path)
        {
            window.CaptureRenderedFrame()?.Save(path);
            Console.WriteLine("saved " + path);
        }
    }
}
