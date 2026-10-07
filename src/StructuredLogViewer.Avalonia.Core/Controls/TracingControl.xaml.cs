using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Avalonia.Controls
{
    /// <summary>
    /// Port of the WPF TracingControl: a time axis with a heat graph on top and one row group per build node,
    /// blocks for evaluations, projects, targets and tasks stacked by nesting. All blocks are painted by a single
    /// surface that only draws what is inside the viewport, so large logs stay responsive.
    /// Ctrl+MouseWheel or the slider zooms, dragging pans, a click highlights a block, a double click selects its node.
    /// </summary>
    public partial class TracingControl : UserControl
    {
        public class TextField
        {
            public string Text { get; set; }
            public string ToolTip { get; set; }
            public Rect Position { get; set; }
            public Block Block { get; set; }
        }

        private class Row
        {
            public double Y;
            public double Height;
            public bool IsDivider;
            public bool ShowTime;
            public List<TextField> Fields = new List<TextField>();
        }

        private class HeatMapNode : BaseNode
        {
            internal static readonly HeatMapNode Stub = new HeatMapNode();
        }

        private static readonly Block heatMapBlockNormal = new Block { HasError = false, Node = HeatMapNode.Stub };
        private static readonly Block heatMapBlockError = new Block { HasError = true, Node = HeatMapNode.Stub };

        // Ratio of time reported to the smallest pixel to render.
        private const double TimeToPixel = 72000;
        private const double minimumZoom = 0.1;
        private const double maximumZoom = 4.0;

        private ScrollViewer scrollViewer;
        private LayoutTransformControl zoomHost;
        private Button resetZoomButton;
        private Slider zoomSlider;
        private readonly ScaleTransform scaleTransform = new ScaleTransform();
        private TraceSurface surface;

        private double scaleFactor = 1;
        private double textHeight = 16;
        private Typeface typeface;
        private double textFontSize = 12;
        private double oneSecondPixelWidth;
        private long globalStartTime;
        private long globalEndTime;

        private List<List<Block>> blocksCollection = new List<List<Block>>();
        private readonly List<Row> rows = new List<Row>();
        private Dictionary<BaseNode, TextField> TextBlocks { get; set; } = new Dictionary<BaseNode, TextField>();
        private TextField activeTextBlock;
        private TextField lastHoverText;
        private double totalWidth;
        private double totalHeight;

        private bool showEvaluation = true;
        private bool showProject = true;
        private bool showTarget = true;
        private bool showTask = true;
        private bool showCpp = false;
        private bool showNodes = true;
        private bool groupByNodes = true;
        private bool showProjectReferenceSelection = true;

        private int numberOfEvaluations;
        private int numberOfProjects;
        private int numberOfTargets;
        private int numberOfTasks;
        private int numberOfNodes;
        private int numberOfCpp;

        private TimeSpan computeTime = TimeSpan.Zero;
        private TimeSpan drawTime = TimeSpan.Zero;

        public TimeSpan TimelineTime { get; set; }
        public BuildControl BuildControl { get; set; }
        public Timeline Timeline { get; set; }

        public string LoadStatistics => $"Timeline:{Math.Round(TimelineTime.TotalMilliseconds)}ms, Compute:{Math.Round(computeTime.TotalMilliseconds)}ms, Draw:{Math.Round(drawTime.TotalMilliseconds)}ms";

        // used by tests and tools
        public int BlockCount => TextBlocks.Count;
        public bool ShowEvaluation { get => showEvaluation; set { showEvaluation = value; ComputeAndDraw(); } }
        public bool ShowProject { get => showProject; set { showProject = value; ComputeAndDraw(); } }
        public bool ShowTarget { get => showTarget; set { showTarget = value; ComputeAndDraw(); } }
        public bool ShowTask { get => showTask; set { showTask = value; ComputeAndDraw(); } }
        public bool ShowCpp { get => showCpp; set { showCpp = value; if (numberOfCpp > 0) { ComputeAndDraw(); } } }
        public bool ShowNodes { get => showNodes; set { showNodes = value; Layout(); } }
        public bool GroupByNodes { get => groupByNodes; set { groupByNodes = value; if (numberOfNodes > 1) { ComputeAndDraw(); } } }
        public bool ShowProjectReferenceSelection { get => showProjectReferenceSelection; set { showProjectReferenceSelection = value; surface?.InvalidateVisual(); } }

        public TracingControl()
        {
            InitializeComponent();
            zoomHost.LayoutTransform = scaleTransform;
            surface = new TraceSurface(this);
            zoomHost.Child = surface;
            zoomSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    ZoomSliderChanged();
                }
            };
            resetZoomButton.Click += (s, e) => zoomSlider.Value = 1;
            scrollViewer.ScrollChanged += (s, e) => surface.InvalidateVisual();
            scrollViewer.PropertyChanged += (s, e) =>
            {
                if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty)
                {
                    surface.InvalidateVisual();
                }
            };
            AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            ContextMenu = CreateContextMenu();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            this.RegisterControl(out scrollViewer, nameof(scrollViewer));
            this.RegisterControl(out zoomHost, nameof(zoomHost));
            this.RegisterControl(out resetZoomButton, nameof(resetZoomButton));
            this.RegisterControl(out zoomSlider, nameof(zoomSlider));
        }

        public void Dispose()
        {
            RemoveHandler(PointerWheelChangedEvent, OnWheel);
            blocksCollection.Clear();
            rows.Clear();
            TextBlocks.Clear();
            activeTextBlock = null;
            lastHoverText = null;
            Timeline = null;
            BuildControl = null;
        }

        private ContextMenu CreateContextMenu()
        {
            var menu = new ContextMenu();
            var items = new List<(Func<string> text, Func<bool> get, Action<bool> set)>
            {
                (() => $"Show Evaluations ({numberOfEvaluations})", () => ShowEvaluation, v => ShowEvaluation = v),
                (() => $"Show Projects ({numberOfProjects})", () => ShowProject, v => ShowProject = v),
                (() => $"Show Targets ({numberOfTargets})", () => ShowTarget, v => ShowTarget = v),
                (() => $"Show Tasks ({numberOfTasks})", () => ShowTask, v => ShowTask = v),
            };
            var more = new List<(Func<string> text, Func<bool> get, Action<bool> set)>
            {
                (() => $"Show Cpp Details ({numberOfCpp})", () => ShowCpp, v => ShowCpp = v),
                (() => "Show P2P on Selection", () => ShowProjectReferenceSelection, v => ShowProjectReferenceSelection = v),
                (() => $"Show Nodes Divider ({numberOfNodes})", () => ShowNodes, v => ShowNodes = v),
                (() => "Group By Nodes", () => GroupByNodes, v => GroupByNodes = v),
            };

            MenuItem Make((Func<string> text, Func<bool> get, Action<bool> set) i)
            {
                var m = new MenuItem { Header = i.text(), ToggleType = MenuItemToggleType.CheckBox, IsChecked = i.get(), StaysOpenOnClick = true };
                m.Click += (s, e) =>
                {
                    i.set(m.IsChecked);
                };
                return m;
            }

            var created = new List<(MenuItem item, Func<string> text, Func<bool> get)>();
            foreach (var i in items)
            {
                var m = Make(i);
                created.Add((m, i.text, i.get));
                menu.Items.Add(m);
            }

            var moreMenu = new MenuItem { Header = "More" };
            foreach (var i in more)
            {
                var m = Make(i);
                created.Add((m, i.text, i.get));
                moreMenu.Items.Add(m);
            }

            var stats = new MenuItem { IsEnabled = false };
            moreMenu.Items.Add(stats);
            menu.Items.Add(moreMenu);

            menu.Opening += (s, e) =>
            {
                foreach (var c in created)
                {
                    c.item.Header = c.text();
                    c.item.IsChecked = c.get();
                }

                stats.Header = LoadStatistics;
            };
            return menu;
        }

        private void ZoomSliderChanged()
        {
            double ratio = zoomSlider.Value;
            if (Math.Abs(ratio - 1) <= 0.001)
            {
                ratio = 1;
            }

            double delta = ratio / scaleFactor;
            var offset = scrollViewer.Offset;
            scaleFactor = ratio;
            scaleTransform.ScaleX = ratio;
            scaleTransform.ScaleY = ratio;
            resetZoomButton.IsVisible = ratio != 1;
            Dispatcher.UIThread.Post(() => scrollViewer.Offset = new Vector(offset.X * delta, offset.Y * delta), DispatcherPriority.Loaded);
            surface?.InvalidateVisual();
        }

        private void OnWheel(object sender, PointerWheelEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                return;
            }

            double delta = 0;
            if (e.Delta.Y > 0)
            {
                if (scaleFactor < maximumZoom)
                {
                    delta = 1.1;
                    if (scaleFactor * delta > maximumZoom)
                    {
                        delta = maximumZoom / scaleFactor;
                    }
                }
            }
            else if (scaleFactor > minimumZoom + 0.1)
            {
                delta = 0.9;
            }

            if (delta != 0)
            {
                var mouse = e.GetPosition(scrollViewer);
                var offset = scrollViewer.Offset;
                double newScale = scaleFactor * delta;
                scaleFactor = newScale;
                scaleTransform.ScaleX = newScale;
                scaleTransform.ScaleY = newScale;
                zoomSlider.Value = newScale;
                var target = new Vector((offset.X + mouse.X) * delta - mouse.X, (offset.Y + mouse.Y) * delta - mouse.Y);
                Dispatcher.UIThread.Post(() => scrollViewer.Offset = target, DispatcherPriority.Loaded);
            }

            e.Handled = true;
        }

        private static readonly string[] ignoreCommonP2PTargets =
        {
            "Build", "ResolveProjectReferences", "GetTargetFrameworksWithPlatformFromInnerBuilds", "DispatchToInnerBuilds",
            "BuildGenerateSources", "BuildGenerateSourcesTraverse", "BuildCompile", "BuildCompileTraverse", "BuildLink", "BuildLinkTraverse"
        };

        // SetTimeline is called from BuildControl, which provides the global start and end.
        public void SetTimeline(Timeline timeline, long globalStart, long globalEnd)
        {
            Timeline = timeline;

            if (timeline.Lanes.Count == 0)
            {
                return;
            }

            var minStartTime = long.MaxValue;
            var maxEndTime = long.MinValue;

            // The recorded start and end of the binlog are sometimes not accurate; widen them to the actual blocks.
            foreach (var lane in timeline.Lanes)
            {
                foreach (var block in lane.Value.Blocks)
                {
                    minStartTime = Math.Min(minStartTime, block.StartTime.Ticks);
                    maxEndTime = Math.Max(maxEndTime, block.EndTime.Ticks);
                }
            }

            globalEndTime = globalEnd > 0 ? Math.Max(maxEndTime, globalEnd) : maxEndTime;
            globalStartTime = globalStart > 0 ? Math.Min(minStartTime, globalStart) : minStartTime;

            foreach (var lane in timeline.Lanes)
            {
                foreach (var block in lane.Value.Blocks)
                {
                    switch (block.Node)
                    {
                        case ProjectEvaluation _:
                            numberOfEvaluations++;
                            break;
                        case Project _:
                            numberOfProjects++;
                            break;
                        case Target t:
                            if (!ignoreCommonP2PTargets.Contains(t.Name))
                            {
                                numberOfTargets++;
                            }

                            break;
                        case Microsoft.Build.Logging.StructuredLogger.Task _:
                            numberOfTasks++;
                            break;
                        default:
                            numberOfCpp++;
                            break;
                    }
                }

                if (lane.Value.Blocks.Count > 0)
                {
                    numberOfNodes++;
                }
            }

            typeface = new Typeface(FontFamily, FontStyle, FontWeight);
            textFontSize = FontSize;
            var sample = new FormattedText("W", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, textFontSize, Brushes.Black);
            textHeight = Math.Ceiling(sample.Height) + 2;

            ComputeAndDraw();
        }

        private void ComputeAndDraw()
        {
            if (Timeline == null)
            {
                return;
            }

            var start = DateTime.UtcNow;
            ComputeTimeline();
            computeTime = DateTime.UtcNow - start;
            start = DateTime.UtcNow;
            CreateTextFields();
            Layout();
            drawTime = DateTime.UtcNow - start;
        }

        private void ComputeTimeline()
        {
            blocksCollection.Clear();

            if (!groupByNodes)
            {
                var allBlocks = Timeline.Lanes.SelectMany(p => p.Value.Blocks);
                var visible = ComputeVisibleBlocks(allBlocks);
                if (visible != null)
                {
                    blocksCollection.Add(visible);
                }
            }
            else
            {
                // Sort lanes by the start time of their first block.
                var sortedKeys = Timeline.Lanes.Where(p => p.Value.Blocks.Any())
                    .Select(p => (key: p.Key, start: p.Value.Blocks.Min(b => b.StartTime.Ticks)))
                    .OrderBy(p => p.start)
                    .ToList();

                foreach (var (key, _) in sortedKeys)
                {
                    var visible = ComputeVisibleBlocks(Timeline.Lanes[key].Blocks);
                    if (visible != null)
                    {
                        blocksCollection.Add(visible);
                    }
                }
            }
        }

        private List<Block> ComputeVisibleBlocks(IEnumerable<Block> enumBlocks)
        {
            double pixelDuration = ConvertPixelToTime(1);
            bool showCppBlocks = ShowCpp && numberOfCpp > 0;
            var blocks = enumBlocks.Where(b =>
            {
                if (b.Duration.Ticks < pixelDuration)
                {
                    return false;
                }

                switch (b.Node)
                {
                    case ProjectEvaluation _:
                        return ShowEvaluation;
                    case Project _:
                        return ShowProject;
                    case Target t:
                        return ShowTarget && !ignoreCommonP2PTargets.Contains(t.Name);
                    case Microsoft.Build.Logging.StructuredLogger.Task node:
                        // When ShowCpp is enabled, hide the task and show the messages so that only one of them appears.
                        if (showCppBlocks && node is CppAnalyzer.CppTask cppNode && cppNode.HasTimedBlocks)
                        {
                            return false;
                        }

                        return ShowTask;
                    case Message _:
                        return ShowCpp && ShowTask;
                    default:
                        return false;
                }
            }).ToList();

            if (blocks.Count == 0)
            {
                return null;
            }

            var endpoints = new List<BlockEndpoint>();
            foreach (var block in blocks)
            {
                block.StartPoint = new BlockEndpoint { Block = block, Timestamp = block.StartTime.Ticks, IsStart = true };
                block.EndPoint = new BlockEndpoint { Block = block, Timestamp = block.EndTime.Ticks };
                endpoints.Add(block.StartPoint);
                endpoints.Add(block.EndPoint);
            }

            endpoints.Sort();
            var indentList = new List<long>(5);

            foreach (var endpoint in endpoints)
            {
                if (!endpoint.IsStart)
                {
                    continue;
                }

                int i = 0;
                while (i < indentList.Count)
                {
                    if (indentList[i] <= endpoint.Timestamp)
                    {
                        endpoint.Block.Indent = i;
                        indentList[i] = endpoint.Block.EndTime.Ticks;
                        break;
                    }

                    i++;
                }

                if (i == indentList.Count)
                {
                    endpoint.Block.Indent = i;
                    indentList.Add(endpoint.Block.EndTime.Ticks);
                }
            }

            blocks.Sort((l, r) =>
            {
                var startDifference = l.StartTime.Ticks.CompareTo(r.StartTime.Ticks);
                return startDifference != 0 ? startDifference : l.Length.CompareTo(r.Length);
            });

            foreach (var block in blocks)
            {
                block.Start = block.StartTime.Ticks;
                block.End = block.EndTime.Ticks;
            }

            return blocks;
        }

        private static double ConvertTimeToPixel(double time) => time / TimeToPixel;

        private static double ConvertPixelToTime(double pixel) => pixel * TimeToPixel;

        // fields for the blocks, laid out relative to their own lane (Y is fixed up in Layout)
        private readonly List<List<TextField>> laneFields = new List<List<TextField>>();
        private readonly List<double> laneHeights = new List<double>();
        private readonly List<double> laneWidths = new List<double>();

        private void CreateTextFields()
        {
            TextBlocks.Clear();
            laneFields.Clear();
            laneHeights.Clear();
            laneWidths.Clear();
            activeTextBlock = null;
            lastHoverText = null;

            foreach (var blocks in blocksCollection)
            {
                var fields = new List<TextField>();
                double height = 0;
                double width = 0;

                foreach (var block in blocks)
                {
                    double left = ConvertTimeToPixel(block.Start - globalStartTime);
                    double duration = ConvertTimeToPixel(block.End - block.Start);
                    double indentOffset = textHeight * block.Indent;

                    if (duration < 1)
                    {
                        continue;
                    }

                    var field = new TextField
                    {
                        Text = $"{block.Text} ({TextUtilities.DisplayDuration(block.Duration)})",
                        Position = new Rect(left, indentOffset, duration, textHeight),
                        ToolTip = block.GetTooltip(),
                        Block = block
                    };
                    TextBlocks[block.Node] = field;
                    fields.Add(field);
                    height = Math.Max(height, indentOffset + textHeight);
                    width = Math.Max(width, left + duration);
                }

                laneFields.Add(fields);
                laneHeights.Add(height);
                laneWidths.Add(width);
            }
        }

        private struct HeatGraphNode
        {
            public double Height;
            public bool HasError;
        }

        private HeatGraphNode[] ComputeHeatGraphData(double unitDuration)
        {
            var graphLength = (int)Math.Floor(ConvertTimeToPixel(globalEndTime - globalStartTime) / unitDuration) + 1;
            var graphData = new HeatGraphNode[graphLength];

            foreach (var blocks in blocksCollection)
            {
                foreach (var block in blocks)
                {
                    if (block.Node is ProjectEvaluation || block.Node is Project || block.Node is Target)
                    {
                        continue;
                    }

                    int left = (int)Math.Floor(ConvertTimeToPixel(block.Start - globalStartTime) / unitDuration);
                    int right = (int)Math.Floor(ConvertTimeToPixel(block.End - globalStartTime) / unitDuration);

                    if (left < 0 || right < 0 || left >= graphLength)
                    {
                        continue;
                    }

                    graphData[left].HasError |= block.HasError;

                    // the start and end are in the same unit
                    if (left == right)
                    {
                        graphData[left].Height += ConvertTimeToPixel(block.End - block.Start) % unitDuration / unitDuration;
                        continue;
                    }

                    // left edge, rounded to a percentage
                    graphData[left].Height += (unitDuration - ConvertTimeToPixel(block.Start - globalStartTime) % unitDuration) / unitDuration;

                    // right edge
                    if (left < right && right < graphLength)
                    {
                        graphData[right].Height += ConvertTimeToPixel(block.End - globalStartTime) % unitDuration / unitDuration;
                        graphData[right].HasError |= block.HasError;
                    }

                    left++;
                    while (left < right && left < graphLength)
                    {
                        graphData[left].Height++;
                        graphData[left].HasError |= block.HasError;
                        left++;
                    }
                }
            }

            return graphData;
        }

        // Stack the rows: heat graph, top ruler, then each lane followed by a divider.
        private void Layout()
        {
            rows.Clear();
            if (Timeline == null)
            {
                return;
            }

            oneSecondPixelWidth = ConvertTimeToPixel(TimeSpan.FromSeconds(1).Ticks);
            double timelineWidth = ConvertTimeToPixel(Math.Max(globalEndTime, globalStartTime) - globalStartTime);
            double y = 0;

            // heat graph
            var graphHeight = textHeight * 4;
            var heat = new Row { Y = y, Height = graphHeight };
            var barWidth = ConvertTimeToPixel(TimeSpan.FromMilliseconds(100).Ticks);
            var graphData = ComputeHeatGraphData(barWidth);
            double maxData = graphData.Length == 0 ? 0 : graphData.Max(g => g.Height);
            maxData = groupByNodes ? Math.Min(blocksCollection.Count, maxData) : maxData;
            if (maxData > 0)
            {
                double ratio = graphHeight / maxData;
                for (int i = 0; i < graphData.Length; i++)
                {
                    if (graphData[i].Height > 0)
                    {
                        double h = Math.Min(graphData[i].Height, maxData) * ratio;
                        heat.Fields.Add(new TextField
                        {
                            Position = new Rect(i * barWidth, y + graphHeight - h, barWidth, h),
                            Block = graphData[i].HasError ? heatMapBlockError : heatMapBlockNormal
                        });
                    }
                }
            }

            rows.Add(heat);
            y += graphHeight;

            rows.Add(new Row { Y = y, Height = textHeight, IsDivider = true, ShowTime = true });
            y += textHeight;

            double width = timelineWidth;
            int dividerIndex = 1;
            for (int i = 0; i < laneFields.Count; i++)
            {
                var row = new Row { Y = y, Height = laneHeights[i] };
                foreach (var f in laneFields[i])
                {
                    f.Position = new Rect(f.Position.X, y + f.Position.Y, f.Position.Width, f.Position.Height);
                    row.Fields.Add(f);
                }

                rows.Add(row);
                y += laneHeights[i];
                width = Math.Max(width, laneWidths[i]);

                if (showNodes)
                {
                    rows.Add(new Row { Y = y, Height = textHeight, IsDivider = true, ShowTime = dividerIndex % 5 == 0 });
                    y += textHeight;
                    dividerIndex++;
                }
            }

            totalWidth = width;
            totalHeight = y;
            surface.InvalidateMeasure();
            surface.InvalidateVisual();
        }

        public void GoToTimedNode(TimedNode node)
        {
            TextField field = null;
            foreach (TimedNode timedNode in node.GetParentChainIncludingThis().OfType<TimedNode>().Reverse())
            {
                if (TextBlocks.TryGetValue(timedNode, out field))
                {
                    activeTextBlock = field;
                    surface.InvalidateVisual();
                    ScrollToElement(field);
                    break;
                }
            }

            // clear the highlight when nothing could be selected
            if (field == null && activeTextBlock != null)
            {
                activeTextBlock = null;
                scrollViewer.Offset = new Vector(0, 0);
                surface.InvalidateVisual();
            }
        }

        private void ScrollToElement(TextField hit)
        {
            var p = hit.Position.TopLeft;
            double x = Math.Max((p.X > 20 ? p.X - 20 : p.X) * scaleFactor, 0);
            double y = Math.Max((p.Y > 20 ? p.Y - 20 : p.Y) * scaleFactor, 0);

            // the tab may have just been shown and not laid out yet
            Dispatcher.UIThread.Post(() => scrollViewer.Offset = new Vector(x, y), DispatcherPriority.Background);
        }

        private TextField HitTest(Point p)
        {
            foreach (var row in rows)
            {
                if (row.IsDivider || p.Y < row.Y || p.Y >= row.Y + row.Height)
                {
                    continue;
                }

                for (int i = row.Fields.Count - 1; i >= 0; i--)
                {
                    if (row.Fields[i].Position.Contains(p) && !(row.Fields[i].Block.Node is HeatMapNode))
                    {
                        return row.Fields[i];
                    }
                }
            }

            return null;
        }

        private static readonly IBrush nodeBackground = new SolidColorBrush(Color.FromArgb(20, 180, 180, 180));
        private static readonly IBrush projectBackground = new SolidColorBrush(Color.FromArgb(50, 180, 180, 180));
        private static readonly IBrush projectEvaluationBackground = new SolidColorBrush(Color.FromArgb(20, 180, 180, 180));
        private static readonly IBrush targetBackground = new SolidColorBrush(Color.FromArgb(50, 255, 100, 255));
        private static readonly IBrush taskBackground = new SolidColorBrush(Color.FromArgb(60, 100, 255, 255));
        private static readonly IBrush errorBackground = new SolidColorBrush(Color.FromArgb(60, 255, 86, 86));
        private static readonly IPen highlightPen = new Pen(Brushes.DeepSkyBlue, 1);

        private static IBrush ChooseBackground(Block block)
        {
            if (block.HasError)
            {
                return errorBackground;
            }

            switch (block.Node)
            {
                case Microsoft.Build.Logging.StructuredLogger.Task _: return taskBackground;
                case Target _: return targetBackground;
                case Project _: return projectBackground;
                case ProjectEvaluation _: return projectEvaluationBackground;
                case Message _: return taskBackground;
                case HeatMapNode _: return taskBackground;
            }

            return Brushes.Transparent;
        }

        private class TraceSurface : Control
        {
            private readonly TracingControl owner;
            private bool pressed;
            private bool moved;
            private Point pressPoint;
            private Vector pressOffset;

            public TraceSurface(TracingControl owner)
            {
                this.owner = owner;
                Background = Brushes.Transparent;
                ClipToBounds = true;
            }

            public IBrush Background { get; set; }

            protected override Size MeasureOverride(Size availableSize)
            {
                return new Size(owner.totalWidth, owner.totalHeight);
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    pressed = true;
                    moved = false;
                    pressPoint = e.GetPosition(owner.scrollViewer);
                    pressOffset = owner.scrollViewer.Offset;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                }
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                if (pressed)
                {
                    var current = e.GetPosition(owner.scrollViewer);
                    var vector = new Vector(pressPoint.X - current.X, pressPoint.Y - current.Y);
                    if (moved || Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y) > 5)
                    {
                        moved = true;
                        owner.scrollViewer.Offset = new Vector(Math.Max(0, pressOffset.X + vector.X), Math.Max(0, pressOffset.Y + vector.Y));
                    }

                    e.Handled = true;
                    return;
                }

                var hit = owner.HitTest(e.GetPosition(this));
                if (hit != owner.lastHoverText)
                {
                    owner.lastHoverText = hit;
                    ToolTip.SetTip(this, hit?.ToolTip);
                }
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                if (!pressed)
                {
                    return;
                }

                pressed = false;
                e.Pointer.Capture(null);
                if (!moved)
                {
                    var hit = owner.HitTest(e.GetPosition(this));
                    if (hit != null)
                    {
                        owner.activeTextBlock = hit;
                        owner.BuildControl?.UpdateBreadcrumb(hit.Block.Node);
                        InvalidateVisual();
                    }
                }

                e.Handled = true;
            }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                base.OnPointerWheelChanged(e);
            }

            protected override void OnDoubleTapped(TappedEventArgs e)
            {
                var hit = owner.HitTest(e.GetPosition(this));
                if (hit != null)
                {
                    owner.BuildControl?.SelectItem(hit.Block.Node);
                    e.Handled = true;
                }
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);
                context.DrawRectangle(Background, null, new Rect(Bounds.Size));

                var sv = owner.scrollViewer;
                double scale = owner.scaleFactor;
                var view = new Rect(
                    sv.Offset.X / scale,
                    sv.Offset.Y / scale,
                    Math.Max(sv.Viewport.Width, 400) / scale,
                    Math.Max(sv.Viewport.Height, 400) / scale);
                // draw a bit more than visible so that small scrolls need no repaint
                view = view.Inflate(new Thickness(view.Width / 4, view.Height / 4));

                var textBrush = SettingsService.UseDarkTheme ? Brushes.White : Brushes.Black;
                const double minTextWidth = 8;

                foreach (var row in owner.rows)
                {
                    if (row.Y + row.Height < view.Top || row.Y > view.Bottom)
                    {
                        continue;
                    }

                    if (row.IsDivider)
                    {
                        DrawDivider(context, row, view, textBrush);
                        continue;
                    }

                    foreach (var field in row.Fields)
                    {
                        var rect = field.Position;
                        if (!rect.Intersects(view))
                        {
                            continue;
                        }

                        context.DrawRectangle(ChooseBackground(field.Block), null, rect);

                        if (rect.Width < minTextWidth || string.IsNullOrEmpty(field.Text))
                        {
                            continue;
                        }

                        var formatted = new FormattedText(
                            field.Text,
                            CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            owner.typeface,
                            owner.textFontSize,
                            textBrush);
                        using (context.PushClip(rect))
                        {
                            context.DrawText(formatted, rect.TopLeft);
                        }
                    }
                }

                DrawHighlight(context);
            }

            private void DrawDivider(DrawingContext context, Row row, Rect view, IBrush textBrush)
            {
                double width = owner.totalWidth;
                context.DrawRectangle(nodeBackground, null, new Rect(0, row.Y, width, row.Height));

                double secondWidth = owner.oneSecondPixelWidth;
                if (secondWidth <= 0)
                {
                    return;
                }

                bool fiveSeconds = secondWidth / owner.textHeight < 3;
                for (int i = 0; i * secondWidth < width; i++)
                {
                    if (fiveSeconds && i % 5 != 0)
                    {
                        continue;
                    }

                    double x = i * secondWidth;
                    if (x + 60 < view.Left || x > view.Right)
                    {
                        continue;
                    }

                    context.DrawLine(new Pen(Brushes.Gray, 1), new Point(x, row.Y), new Point(x, row.Y + row.Height));
                    if (row.ShowTime)
                    {
                        var formatted = new FormattedText(
                            $"{i}s",
                            CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            owner.typeface,
                            owner.textFontSize,
                            textBrush);
                        context.DrawText(formatted, new Point(x + owner.textHeight / 2, row.Y));
                    }
                }
            }

            private void DrawHighlight(DrawingContext context)
            {
                var active = owner.activeTextBlock;
                if (active == null)
                {
                    return;
                }

                context.DrawRectangle(null, highlightPen, active.Position);

                // for a project, connect it with its parent and child projects
                if (owner.showProjectReferenceSelection && active.Block?.Node is Project project)
                {
                    var parent = project.GetNearestParent<Project>();
                    if (parent != null && owner.TextBlocks.TryGetValue(parent, out var parentField))
                    {
                        DrawLine(context, parentField.Position.TopLeft, active.Position.TopLeft);
                    }

                    foreach (var child in project.FindImmediateChildrenOfType<Project>())
                    {
                        if (owner.TextBlocks.TryGetValue(child, out var childField))
                        {
                            DrawLine(context, active.Position.TopLeft, childField.Position.TopLeft);
                        }
                    }
                }
            }

            private void DrawLine(DrawingContext context, Point origin, Point destination)
            {
                double originY;
                double destinationY;
                if (origin.Y < destination.Y)
                {
                    // below
                    originY = origin.Y + owner.textHeight;
                    destinationY = destination.Y + owner.textHeight;
                }
                else
                {
                    originY = origin.Y;
                    destinationY = destination.Y;
                }

                context.DrawLine(highlightPen, new Point(destination.X, originY), new Point(destination.X, destinationY));
            }
        }
    }
}
