using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Avalonia.Controls
{
    public enum GraphFilterMode
    {
        None,
        DirectReferencesOnly,
        ReachableOnly
    }

    /// <summary>
    /// Port of the WPF GraphControl: the vertices of a Digraph laid out in layers by height (or depth), as text blocks;
    /// selecting a vertex draws its incoming and outgoing edges, two selected vertices show all paths between them.
    /// </summary>
    public class GraphControl
    {
        private readonly ScrollViewer scrollViewer;
        private readonly Grid grid;
        private readonly StackPanel layersControl;
        private readonly Canvas canvas;
        private readonly Canvas allEdgesCanvas;

        private readonly Dictionary<Vertex, Control> controlFromVertex = new Dictionary<Vertex, Control>();
        private readonly HashSet<Control> selectedControls = new HashSet<Control>();
        private readonly HashSet<Vertex> selectedVertices = new HashSet<Vertex>();
        private readonly HashSet<Vertex> specifiedVertices = new HashSet<Vertex>();
        private IEnumerable<Vertex> displayedVertices = Array.Empty<Vertex>();

        private Color outgoingColor, incomingColor, border, allEdgesColor;
        private IBrush outgoingSolidBrush;
        private IBrush outgoingBrush, incomingBrush;
        private IBrush allEdgesBrush;

        public Control CanvasElement => grid;
        public Control Content => scrollViewer;

        public event Action SelectionChanged;

        public GraphControl()
        {
            DarkTheme = SettingsService.UseDarkTheme;

            layersControl = new StackPanel { Orientation = Orientation.Vertical };
            canvas = new Canvas { IsHitTestVisible = false };
            allEdgesCanvas = new Canvas { IsHitTestVisible = false };

            grid = new Grid();
            grid.Children.Add(allEdgesCanvas);
            grid.Children.Add(canvas);
            grid.Children.Add(layersControl);

            scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = grid
            };
        }

        private bool darkTheme;
        public bool DarkTheme
        {
            get => darkTheme;
            set
            {
                darkTheme = value;

                if (darkTheme)
                {
                    outgoingColor = Colors.MediumOrchid;
                    border = Colors.DeepSkyBlue;
                    incomingColor = Colors.PaleGreen;
                    allEdgesColor = Color.FromArgb(40, 255, 255, 255);
                }
                else
                {
                    outgoingColor = Colors.MediumOrchid;
                    border = Colors.DarkCyan;
                    incomingColor = Colors.Green;
                    allEdgesColor = Color.FromArgb(40, 128, 128, 128);
                }

                outgoingBrush = Gradient(outgoingColor, border);
                outgoingSolidBrush = new SolidColorBrush(outgoingColor);
                incomingBrush = Gradient(border, incomingColor);
                allEdgesBrush = new SolidColorBrush(allEdgesColor);
            }
        }

        private static IBrush Gradient(Color from, Color to)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops { new GradientStop(from, 0), new GradientStop(to, 1) }
            };
        }

        private Digraph graph;
        public Digraph Digraph
        {
            get => graph;
            set
            {
                if (graph != null)
                {
                    Clear();
                }

                graph = value;

                if (graph != null)
                {
                    Populate();
                }
            }
        }

        public void Redraw()
        {
            Clear(clearSelection: false);
            Populate();
        }

        private bool hideTransitiveEdges;
        public bool HideTransitiveEdges
        {
            get => hideTransitiveEdges;
            set
            {
                if (hideTransitiveEdges == value)
                {
                    return;
                }

                hideTransitiveEdges = value;
                if (ShowAllEdges)
                {
                    Redraw();
                }
                else
                {
                    SelectVertices(selectedVertices.ToArray());
                }
            }
        }

        private bool showAllEdges;
        public bool ShowAllEdges
        {
            get => showAllEdges;
            set
            {
                if (showAllEdges == value)
                {
                    return;
                }

                showAllEdges = value;
                Redraw();
            }
        }

        private GraphFilterMode filterMode;
        public GraphFilterMode FilterMode
        {
            get => filterMode;
            set
            {
                if (filterMode == value)
                {
                    return;
                }

                filterMode = value;
                Redraw();
                BringIntoView(SelectedAndSpecifiedVertices.FirstOrDefault());
            }
        }

        private bool layerByDepth;
        public bool LayerByDepth
        {
            get => layerByDepth;
            set
            {
                if (layerByDepth == value)
                {
                    return;
                }

                layerByDepth = value;
                Redraw();
            }
        }

        private bool horizontal;
        public bool Horizontal
        {
            get => horizontal;
            set
            {
                if (horizontal == value)
                {
                    return;
                }

                horizontal = value;
                Redraw();
            }
        }

        private bool inverted;
        public bool Inverted
        {
            get => inverted;
            set
            {
                if (inverted == value)
                {
                    return;
                }

                inverted = value;
                Redraw();
            }
        }

        /// <summary>Number of vertex controls currently shown (used by tests).</summary>
        public int DisplayedCount => controlFromVertex.Count;

        private void Populate()
        {
            if (graph == null || graph.IsEmpty)
            {
                return;
            }

            var verticesToDisplay = GetVerticesToDisplay().ToArray();
            displayedVertices = verticesToDisplay;

            if (verticesToDisplay.Length == 0)
            {
                return;
            }

            var maxHeight = verticesToDisplay.Max(g => g.Height);
            var maxDepth = verticesToDisplay.Max(g => g.Depth);
            var primaryOrientation = Orientation.Vertical;
            var secondaryOrientation = Orientation.Horizontal;
            if (Horizontal)
            {
                primaryOrientation = Orientation.Horizontal;
                secondaryOrientation = Orientation.Vertical;
            }

            layersControl.Orientation = primaryOrientation;

            Func<Vertex, int> groupBy = v => v.Height;
            if (LayerByDepth)
            {
                groupBy = v => maxDepth - v.Depth;
            }

            var groups = verticesToDisplay.GroupBy(groupBy).OrderBy(g => g.Key).ToArray();
            if (Inverted)
            {
                Array.Reverse(groups);
            }

            foreach (var vertexGroup in groups)
            {
                var layerPanel = new StackPanel { Orientation = secondaryOrientation };

                foreach (var vertex in vertexGroup.OrderByDescending(s => s.InDegree).ThenBy(s => s.Title))
                {
                    var depthOrHeight = vertex.Depth;
                    if (LayerByDepth)
                    {
                        depthOrHeight = maxHeight - vertex.Height;
                    }

                    var background = ComputeBackground(depthOrHeight);

                    var paddingHeight = Math.Pow(vertex.InDegree, 0.6);
                    var opacity = vertex.InDegree > 1 ? 0.9 : 0.5;

                    // highlight selected vertices when filtering
                    if (filterMode != GraphFilterMode.None && selectedVertices.Contains(vertex))
                    {
                        opacity = 1.0;
                        background = DarkTheme ? Color.FromRgb(255, 140, 0) : Color.FromRgb(255, 255, 0);
                    }

                    var vertexControl = new TextBlock
                    {
                        Text = vertex.Title.TrimQuotes(),
                        Margin = new Thickness(4, 2, 4, 2),
                        Padding = new Thickness(2, paddingHeight, 2, paddingHeight),
                        Background = new SolidColorBrush(background),
                        VerticalAlignment = VerticalAlignment.Center,
                        Opacity = opacity,
                        Tag = vertex
                    };
                    controlFromVertex[vertex] = vertexControl;

                    vertexControl.PointerPressed += (s, args) =>
                    {
                        var v = GetVertex(vertexControl);
                        if (selectedVertices.Contains(v))
                        {
                            SelectVertices(selectedVertices.Where(c => c != v).ToArray());
                        }
                        else if ((args.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0)
                        {
                            SelectVertices(selectedVertices.Take(1).Append(v).ToArray());
                        }
                        else
                        {
                            SelectVertices(new[] { v });
                        }

                        if (filterMode != GraphFilterMode.None)
                        {
                            Redraw();
                        }
                    };
                    layerPanel.Children.Add(vertexControl);
                }

                layersControl.Children.Add(layerPanel);
            }

            if (ShowAllEdges)
            {
                layersControl.UpdateLayout();
                HashSet<Vertex> visibleVertices = filterMode != GraphFilterMode.None ? GetVerticesToDisplay().ToHashSet() : null;

                foreach (var vertex in verticesToDisplay)
                {
                    var vertexControl = GetControl(vertex);
                    vertexControl.UpdateLayout();
                    var sourceRect = GetRectOnCanvas(vertexControl, allEdgesCanvas);
                    AddOutgoingEdges(sourceRect, vertex, allEdges: true, visibleVertices: visibleVertices);
                }
            }

            SelectVertices(selectedVertices.ToArray());
        }

        public IEnumerable<Vertex> AllVertices => graph == null ? Array.Empty<Vertex>() : graph.Vertices;

        public IEnumerable<Vertex> GetVerticesToDisplay()
        {
            if (graph == null)
            {
                return Array.Empty<Vertex>();
            }

            if (filterMode == GraphFilterMode.None || !SelectedAndSpecifiedVertices.Any())
            {
                return AllVertices;
            }

            switch (filterMode)
            {
                case GraphFilterMode.DirectReferencesOnly: return GetDirectlyConnectedVertices();
                case GraphFilterMode.ReachableOnly: return GetReachableVertices();
                default: return AllVertices;
            }
        }

        private IEnumerable<Vertex> GetDirectlyConnectedVertices()
        {
            var directlyConnected = new HashSet<Vertex>();

            foreach (var selected in SelectedAndSpecifiedVertices)
            {
                directlyConnected.Add(selected);

                foreach (var outgoing in selected.Outgoing)
                {
                    directlyConnected.Add(outgoing);
                }

                foreach (var incoming in selected.Incoming)
                {
                    directlyConnected.Add(incoming);
                }
            }

            return directlyConnected;
        }

        private IEnumerable<Vertex> GetReachableVertices()
        {
            var reachableVertices = new HashSet<Vertex>();

            foreach (var selected in SelectedAndSpecifiedVertices)
            {
                VisitOutgoing(selected);

                // remove it, otherwise VisitIncoming bails out immediately
                reachableVertices.Remove(selected);
                VisitIncoming(selected);
            }

            void VisitOutgoing(Vertex vertex)
            {
                if (!reachableVertices.Add(vertex))
                {
                    return;
                }

                foreach (var outgoing in vertex.Outgoing)
                {
                    VisitOutgoing(outgoing);
                }
            }

            void VisitIncoming(Vertex vertex)
            {
                if (!reachableVertices.Add(vertex))
                {
                    return;
                }

                foreach (var incoming in vertex.Incoming)
                {
                    VisitIncoming(incoming);
                }
            }

            return reachableVertices;
        }

        private Color ComputeBackground(int depth)
        {
            byte ratio, halfratio;

            if (DarkTheme)
            {
                ratio = (byte)Math.Min(150, depth * 6);
                halfratio = (byte)Math.Min(100, depth * 4);
                return Color.FromRgb(40, halfratio, ratio);
            }

            ratio = (byte)Math.Max(200, 255 - depth * 2);
            halfratio = (byte)Math.Max(224, 255 - depth);
            return Color.FromRgb(ratio, halfratio, 255);
        }

        private static Rect GetRectOnCanvas(Control control, Canvas target)
        {
            var topLeft = control.TranslatePoint(new Point(0, 0), target) ?? new Point();
            var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), target) ?? topLeft;
            return new Rect(topLeft, bottomRight);
        }

        private static void AddLine(Point sourcePoint, Point destinationPoint, IBrush stroke, Canvas target)
        {
            target.Children.Add(new Line { StartPoint = sourcePoint, EndPoint = destinationPoint, Stroke = stroke, StrokeThickness = 1 });
        }

        private static void AddLine(Rect sourceRect, Rect destinationRect, IBrush stroke, Canvas target)
        {
            var sourceCenter = Center(sourceRect);
            var destinationCenter = Center(destinationRect);
            var sourcePoint = GetPointOnBoundary(sourceRect, destinationCenter);
            var destinationPoint = GetPointOnBoundary(destinationRect, sourceCenter);
            AddLine(sourcePoint, destinationPoint, stroke, target);
        }

        private static void AddLine(Control fromControl, Control toControl, IBrush stroke, Canvas target)
        {
            AddLine(GetRectOnCanvas(fromControl, target), GetRectOnCanvas(toControl, target), stroke, target);
        }

        private static Point Center(Rect rect) => new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        private static Point GetPointOnBoundary(Rect rect, Point outsidePoint)
        {
            var center = Center(rect);
            var horizontalRatio = Math.Abs((outsidePoint.X - center.X) / (rect.Width / 2));
            var verticalRatio = Math.Abs((outsidePoint.Y - center.Y) / (rect.Height / 2));
            var horizontalPos = center.X + (outsidePoint.X - center.X) / verticalRatio;
            if (Math.Abs(horizontalPos - center.X) <= rect.Width / 2)
            {
                return new Point(horizontalPos, outsidePoint.Y > center.Y ? rect.Bottom : rect.Top);
            }

            var vertical = center.Y + (outsidePoint.Y - center.Y) / horizontalRatio;
            return new Point(outsidePoint.X > center.X ? rect.Right : rect.Left, vertical);
        }

        private void AddRectangle(Control element, IBrush stroke, IBrush fill = null)
        {
            AddRectangle(GetRectOnCanvas(element, canvas), stroke, fill);
        }

        private void AddRectangle(Rect rect, IBrush stroke, IBrush fill = null)
        {
            var rectangleShape = new Rectangle
            {
                Width = rect.Width + 2,
                Height = rect.Height + 2,
                Stroke = stroke,
                Fill = fill
            };
            Canvas.SetLeft(rectangleShape, rect.X - 1);
            Canvas.SetTop(rectangleShape, rect.Y - 1);
            canvas.Children.Add(rectangleShape);
        }

        private void SelectVertices(IEnumerable<Vertex> vertices)
        {
            selectedVertices.Clear();

            if (vertices == null)
            {
                SelectControls(null);
                return;
            }

            var list = vertices.ToArray();
            selectedVertices.UnionWith(list);
            SelectControls(list.Select(GetControl).Where(c => c != null).ToArray());
        }

        private void SelectControls(Control[] controls)
        {
            canvas.Children.Clear();
            selectedControls.Clear();

            if (controls != null)
            {
                selectedControls.UnionWith(controls);
                foreach (var control in controls)
                {
                    control.UpdateLayout();
                }

                if (controls.Length == 1)
                {
                    SelectControl(controls[0]);
                }
                else if (controls.Length == 2)
                {
                    SelectControls(controls[0], controls[1]);
                }
                else if (controls.Length > 2)
                {
                    SelectManyControls(controls);
                }
            }

            SelectionChanged?.Invoke();
        }

        private Control GetControl(Vertex vertex)
        {
            controlFromVertex.TryGetValue(vertex, out var result);
            return result;
        }

        private static Vertex GetVertex(Control control) => (Vertex)control.Tag;

        private void SelectManyControls(Control[] controls)
        {
            var vertices = controls.Select(GetVertex).ToHashSet();

            foreach (var control in controls)
            {
                var vertex = GetVertex(control);
                var sourceRect = GetRectOnCanvas(control, canvas);
                AddOutgoingEdges(sourceRect, vertex, visibleVertices: vertices);
                AddIncomingEdges(sourceRect, vertex, visibleVertices: vertices);
            }

            foreach (var control in controls)
            {
                AddRectangle(GetRectOnCanvas(control, canvas), new SolidColorBrush(border), Brushes.PaleGreen);
            }
        }

        private void SelectControls(Control fromControl, Control toControl)
        {
            Vertex from = GetVertex(fromControl);
            Vertex to = GetVertex(toControl);

            if (from.Height < to.Height)
            {
                (from, to) = (to, from);
                (fromControl, toControl) = (toControl, fromControl);
            }

            var highlighted = new HashSet<Control>();
            var edges = new HashSet<(Control start, Control end)>();

            Digraph.FindAllPaths(from, to, path =>
            {
                for (int i = 0; i < path.Count; i++)
                {
                    var control = GetControl(path[i]);
                    if (control != null)
                    {
                        highlighted.Add(control);

                        var target = i < path.Count - 1 ? path[i + 1] : to;
                        if (GetControl(target) is { } targetControl)
                        {
                            edges.Add((control, targetControl));
                        }
                    }
                }
            });

            highlighted.Remove(fromControl);
            highlighted.Remove(toControl);

            foreach (var highlight in highlighted)
            {
                AddRectangle(highlight, new SolidColorBrush(Colors.Blue), Brushes.Azure);
            }

            AddRectangle(fromControl, Brushes.Red, Brushes.Pink);
            AddRectangle(toControl, Brushes.Red, Brushes.Pink);

            foreach (var edge in edges)
            {
                AddLine(edge.start, edge.end, outgoingBrush, canvas);
            }
        }

        private void SelectControl(Control vertexControl)
        {
            var sourceRect = GetRectOnCanvas(vertexControl, canvas);
            var vertex = GetVertex(vertexControl);
            AddOutgoingEdges(sourceRect, vertex);
            AddIncomingEdges(sourceRect, vertex);
            AddRectangle(sourceRect, new SolidColorBrush(border), Brushes.PaleGreen);
        }

        private void AddIncomingEdges(Rect destinationRect, Vertex destinationVertex, HashSet<Vertex> visibleVertices = null)
        {
            if (visibleVertices == null)
            {
                visibleVertices = filterMode != GraphFilterMode.None ? GetVerticesToDisplay().ToHashSet() : null;
            }

            foreach (var incoming in destinationVertex.Incoming)
            {
                if (HideTransitiveEdges && incoming.TransitiveOutgoing != null && incoming.TransitiveOutgoing.Contains(destinationVertex))
                {
                    continue;
                }

                if (visibleVertices != null && !visibleVertices.Contains(incoming))
                {
                    continue;
                }

                if (GetControl(incoming) is { } sourceControl)
                {
                    var sourceRect = GetRectOnCanvas(sourceControl, canvas);
                    AddLine(sourceRect, destinationRect, incomingBrush, canvas);
                    AddRectangle(sourceRect, new SolidColorBrush(incomingColor));
                }
            }
        }

        private void AddOutgoingEdges(Rect sourceRect, Vertex node, bool allEdges = false, HashSet<Vertex> visibleVertices = null)
        {
            IEnumerable<Vertex> list = HideTransitiveEdges ? node.NonRedundantOutgoing : node.Outgoing;

            if (!allEdges && visibleVertices == null)
            {
                visibleVertices = filterMode != GraphFilterMode.None ? GetVerticesToDisplay().ToHashSet() : null;
            }

            var edgeBrush = allEdges ? allEdgesBrush : outgoingBrush;
            var target = allEdges ? allEdgesCanvas : canvas;

            foreach (var outgoing in list)
            {
                if (visibleVertices != null && !visibleVertices.Contains(outgoing))
                {
                    continue;
                }

                if (GetControl(outgoing) is { } destinationControl)
                {
                    var destinationRect = GetRectOnCanvas(destinationControl, target);
                    AddLine(sourceRect, destinationRect, edgeBrush, target);
                    if (!allEdges)
                    {
                        AddRectangle(destinationRect, outgoingSolidBrush);
                    }
                }
            }
        }

        private void Clear(bool clearSelection = true)
        {
            if (clearSelection)
            {
                selectedVertices.Clear();
                specifiedVertices.Clear();
            }

            selectedControls.Clear();
            controlFromVertex.Clear();
            canvas.Children.Clear();
            allEdgesCanvas.Children.Clear();
            layersControl.Children.Clear();
        }

        public void Locate(string text)
        {
            specifiedVertices.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                if (filterMode != GraphFilterMode.None)
                {
                    Redraw();
                }

                return;
            }

            var parts = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var foundVertices = parts.Select(FindVertexByText).Where(v => v != null).ToArray();
            specifiedVertices.UnionWith(foundVertices);

            var oldSelection = selectedVertices.ToArray();

            if (filterMode != GraphFilterMode.None)
            {
                Redraw();
            }

            var newSelection = oldSelection.Intersect(displayedVertices).Union(specifiedVertices).ToArray();
            SelectVertices(newSelection);
            BringIntoView(SpecifiedAndSelectedVertices.FirstOrDefault());
        }

        public void Dispose()
        {
            Clear();
            graph = null;
        }

        private void BringIntoView(Vertex vertex)
        {
            if (vertex == null)
            {
                return;
            }

            GetControl(vertex)?.BringIntoView();
        }

        private Vertex FindVertexByText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Trim();

            return AllVertices
                .OrderBy(v => v.Title.Length)
                .ThenBy(v => v.Title)
                .FirstOrDefault(v => v.Title.ContainsIgnoreCase(text));
        }

        public Vertex SelectedVertex => SelectedVertices.FirstOrDefault();

        public IReadOnlyList<Vertex> SelectedVertices => selectedVertices.ToArray();
        public IReadOnlyList<Vertex> SelectedAndSpecifiedVertices => selectedVertices.Union(specifiedVertices).ToArray();
        public IReadOnlyList<Vertex> SpecifiedAndSelectedVertices => specifiedVertices.Union(selectedVertices).ToArray();

        /// <summary>Selects vertices by their text (used by tests).</summary>
        public void Select(params Vertex[] vertices) => SelectVertices(vertices);
    }
}
