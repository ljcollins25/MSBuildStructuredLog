using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Avalonia.Controls
{
    /// <summary>
    /// Port of the WPF TimelineControl: one column per build node (lane) with blocks for projects,
    /// targets and tasks laid out by time. Ctrl+MouseWheel or the slider zooms; clicking highlights a block
    /// and double-clicking selects its node in the main tree.
    /// </summary>
    public partial class TimelineControl : UserControl
    {
        private ScrollViewer scrollViewer;
        private LayoutTransformControl zoomHost;
        private Grid grid;
        private Button resetZoomButton;
        private Slider zoomSlider;

        private const double minimumZoom = 0.1;
        private const double maximumZoom = 4.0;
        private readonly ScaleTransform scaleTransform = new ScaleTransform();
        private double scaleFactor = 1;

        private TextBlock activeTextBlock;
        private readonly Border highlight = new Border
        {
            BorderBrush = Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };

        public BuildControl BuildControl { get; set; }
        public Dictionary<BaseNode, TextBlock> TextBlocks { get; set; } = new Dictionary<BaseNode, TextBlock>();
        public Timeline Timeline { get; set; }

        public TimelineControl()
        {
            InitializeComponent();
            zoomHost.LayoutTransform = scaleTransform;
            zoomSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    zoomSlider_ValueChanged();
                }
            };
            resetZoomButton.Click += (s, e) => zoomSlider.Value = 1;
            AddHandler(PointerWheelChangedEvent, TimelineControl_MouseWheel, RoutingStrategies.Tunnel);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            this.RegisterControl(out scrollViewer, nameof(scrollViewer));
            this.RegisterControl(out zoomHost, nameof(zoomHost));
            this.RegisterControl(out grid, nameof(grid));
            this.RegisterControl(out resetZoomButton, nameof(resetZoomButton));
            this.RegisterControl(out zoomSlider, nameof(zoomSlider));
        }

        public void Dispose()
        {
            RemoveHandler(PointerWheelChangedEvent, TimelineControl_MouseWheel);
            grid.Children.Clear();
            Timeline = null;
            BuildControl = null;
            TextBlocks.Clear();
            activeTextBlock = null;
        }

        private void zoomSlider_ValueChanged()
        {
            double ratio = zoomSlider.Value;
            if (Math.Abs(ratio - 1) <= 0.001)
            {
                ratio = 1;
            }

            scaleFactor = ratio;
            scaleTransform.ScaleX = ratio;
            scaleTransform.ScaleY = ratio;
            resetZoomButton.IsVisible = ratio != 1;
        }

        private void TimelineControl_MouseWheel(object sender, PointerWheelEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                return;
            }

            if (e.Delta.Y > 0)
            {
                if (scaleFactor < maximumZoom)
                {
                    zoomSlider.Value = scaleFactor + 0.1;
                }
            }
            else if (scaleFactor > minimumZoom + 0.1)
            {
                zoomSlider.Value = scaleFactor - 0.1;
            }

            e.Handled = true;
        }

        public void SetTimeline(Timeline timeline, long globalStart)
        {
            Timeline = timeline;

            var lanesPanel = new StackPanel { Orientation = Orientation.Horizontal };
            grid.Children.Add(lanesPanel);

            var keys = Timeline.Lanes.Keys.ToList();
            keys.Sort();

            foreach (var key in keys)
            {
                var panel = CreatePanelForLane(Timeline.Lanes[key], globalStart);
                if (panel != null && panel.Children.Count > 0)
                {
                    panel.HorizontalAlignment = HorizontalAlignment.Left;
                    lanesPanel.Children.Add(panel);
                }
            }
        }

        public void GoToTimedNode(TimedNode node)
        {
            TextBlock textblock = null;
            foreach (TimedNode timedNode in node.GetParentChainIncludingThis().OfType<TimedNode>().Reverse())
            {
                if (TextBlocks.TryGetValue(timedNode, out textblock))
                {
                    HighlightTextBlock(textblock, scrollToElement: true);
                    break;
                }
            }

            if (textblock == null && activeTextBlock != null)
            {
                (highlight.Parent as Panel)?.Children.Remove(highlight);
                activeTextBlock = null;
                scrollViewer.Offset = new Vector(0, 0);
            }
        }

        private Canvas CreatePanelForLane(Lane lane, double start)
        {
            var blocks = lane.Blocks.ToList();
            if (blocks.Count == 0)
            {
                return null;
            }

            var canvas = new Canvas { VerticalAlignment = VerticalAlignment.Top };

            var endpoints = new List<BlockEndpoint>();
            foreach (var block in blocks)
            {
                block.StartPoint = new BlockEndpoint { Block = block, Timestamp = block.StartTime.Ticks, IsStart = true };
                block.EndPoint = new BlockEndpoint { Block = block, Timestamp = block.EndTime.Ticks };
                endpoints.Add(block.StartPoint);
                endpoints.Add(block.EndPoint);
            }

            endpoints.Sort();

            int level = 0;
            foreach (var endpoint in endpoints)
            {
                if (endpoint.IsStart)
                {
                    level++;
                    endpoint.Block.Indent = level;
                }
                else
                {
                    level--;
                }
            }

            blocks.Sort((l, r) =>
            {
                var startDifference = l.StartTime.Ticks.CompareTo(r.StartTime.Ticks);
                return startDifference != 0 ? startDifference : l.Length.CompareTo(r.Length);
            });

            DateTime maxDateTime = blocks[blocks.Count - 1].StartTime;
            foreach (var block in blocks)
            {
                block.Start = block.StartTime.Ticks;
                block.End = block.EndTime.Ticks;
            }

            double totalDuration = maxDateTime.Ticks - start;
            if (totalDuration == 0)
            {
                totalDuration = 1;
            }

            double width = 0;

            var sample = new TextBlock { Text = "W" };
            sample.Measure(new Size(10000, 10000));
            var textHeight = sample.DesiredSize.Height;

            double preferredTotalHeight = textHeight * blocks.Count(b => b.Length > totalDuration / 2000);

            double currentHeight = 0;
            double totalHeight = 0;

            foreach (var block in blocks)
            {
                var textBlock = new TextBlock
                {
                    Text = $"{block.Text} ({TextUtilities.DisplayDuration(block.Duration)})",
                    Background = ChooseBackground(block)
                };

                double left = 24 * block.Indent;
                double top = (block.Start - start) / totalDuration * preferredTotalHeight;
                double height = (block.End - block.Start) / totalDuration * preferredTotalHeight;
                if (height < textHeight)
                {
                    continue;
                }

                textBlock.Measure(new Size(10000, 10000));
                double currentTotalWidth = left + textBlock.DesiredSize.Width;
                if (currentTotalWidth > width)
                {
                    width = currentTotalWidth;
                }

                double minimumTop = currentHeight;
                if (minimumTop > top)
                {
                    double adjustment = minimumTop - top;
                    if (height > adjustment + textHeight)
                    {
                        height -= adjustment;
                        top = minimumTop;
                    }
                    else
                    {
                        continue;
                    }
                }

                textBlock.Height = height;
                ToolTip.SetTip(textBlock, block.GetTooltip());
                textBlock.PointerReleased += TextBlock_PointerReleased;
                textBlock.DoubleTapped += TextBlock_DoubleTapped;
                textBlock.Tag = block;
                TextBlocks.Add(block.Node, textBlock);

                currentHeight = top + textHeight;
                if (totalHeight < top + height)
                {
                    totalHeight = top + height;
                }

                Canvas.SetLeft(textBlock, left);
                Canvas.SetTop(textBlock, top);
                canvas.Children.Add(textBlock);
            }

            canvas.Height = totalHeight;
            canvas.Width = width;
            return canvas;
        }

        private void TextBlock_DoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is TextBlock { Tag: Block block })
            {
                BuildControl?.SelectItem(block.Node);
                e.Handled = true;
            }
        }

        private void TextBlock_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (sender is TextBlock { Tag: Block } textBlock)
            {
                HighlightTextBlock(textBlock);
            }
        }

        private void HighlightTextBlock(TextBlock hit, bool scrollToElement = false)
        {
            if (activeTextBlock != hit)
            {
                (highlight.Parent as Panel)?.Children.Remove(highlight);
                activeTextBlock = hit;

                if (hit?.Parent is Canvas canvas)
                {
                    canvas.Children.Add(highlight);
                    Canvas.SetLeft(highlight, Canvas.GetLeft(hit));
                    Canvas.SetTop(highlight, Canvas.GetTop(hit));
                    highlight.Width = hit.Bounds.Width > 0 ? hit.Bounds.Width : hit.DesiredSize.Width;
                    highlight.Height = hit.Height;
                }
            }

            if (scrollToElement && hit?.Parent is Canvas parent)
            {
                // the tab may have just become visible and not be laid out yet
                Dispatcher.UIThread.Post(() =>
                {
                    var p = parent.TranslatePoint(new Point(Canvas.GetLeft(hit), Canvas.GetTop(hit)), grid);
                    if (p.HasValue)
                    {
                        double x = p.Value.X * scaleFactor;
                        double y = p.Value.Y * scaleFactor;
                        scrollViewer.Offset = new Vector(x > 20 ? x - 20 : x, y > 20 ? y - 20 : y);
                    }
                }, DispatcherPriority.Background);
            }
        }

        private static readonly IBrush projectBackground = new SolidColorBrush(Color.FromArgb(10, 180, 180, 180));
        private static readonly IBrush projectEvaluationBackground = new SolidColorBrush(Color.FromArgb(20, 100, 255, 150));
        private static readonly IBrush targetBackground = new SolidColorBrush(Color.FromArgb(20, 255, 100, 255));
        private static readonly IBrush taskBackground = new SolidColorBrush(Color.FromArgb(30, 100, 255, 255));

        private static IBrush ChooseBackground(Block block)
        {
            switch (block.Node)
            {
                case Project _: return projectBackground;
                case ProjectEvaluation _: return projectEvaluationBackground;
                case Target _: return targetBackground;
                case Microsoft.Build.Logging.StructuredLogger.Task _: return taskBackground;
            }

            return Brushes.Transparent;
        }
    }
}
