using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Avalonia.Controls
{
    /// <summary>
    /// Port of the WPF GraphHostControl: toolbar (text, vertices, transitive reduction, all edges, filter mode,
    /// layering, locate, go to search) around a GraphControl.
    /// </summary>
    public class GraphHostControl : DockPanel
    {
        private readonly string initialSelection;

        public event Action<string> DisplayText;
        public event Action<string> GoToSearch;

        private Digraph graph;
        private GraphControl graphControl;
        private Button searchButton;
        private Button showTextButton;
        private Button showVerticesButton;
        private TextBox searchTextBox;
        private TextBox projectNameTextBlock;

        public GraphHostControl() : this(null)
        {
        }

        public GraphHostControl(string initialSelection)
        {
            this.initialSelection = initialSelection;
            Initialize();
        }

        public GraphControl GraphControl => graphControl;

        /// <summary>Loads a .dgml/.graph/.txt edge list the way WPF OpenGraphFile does.</summary>
        public static GraphHostControl FromFile(string filePath, Action<string> displayText = null)
        {
            var graph = Digraph.Load(filePath);
            graph.RemoveCycles();
            graph.CalculateHeight();
            graph.CalculateDepth();
            graph.ComputeTransitiveReduction();

            var host = new GraphHostControl();
            if (displayText != null)
            {
                host.DisplayText += displayText;
            }

            host.Graph = graph;
            return host;
        }

        public Digraph Graph
        {
            get => graph;
            set
            {
                if (graph == value)
                {
                    return;
                }

                if (graph != null)
                {
                    graphControl.Digraph = null;
                }

                graph = value;
                if (graph != null)
                {
                    graphControl.Digraph = graph;

                    if (!string.IsNullOrEmpty(initialSelection))
                    {
                        // wait until the vertices have been laid out
                        Dispatcher.UIThread.Post(() => Locate(initialSelection), DispatcherPriority.Loaded);
                    }
                }

                UpdateVisibility();
            }
        }

        public string SearchText
        {
            get => searchTextBox.Text;
            set => searchTextBox.Text = value;
        }

        /// <summary>Locates and selects a vertex by text.</summary>
        public void Locate(string text) => graphControl?.Locate(text);

        public void Dispose()
        {
            graphControl?.Dispose();
            DisplayText = null;
            GoToSearch = null;
        }

        private void UpdateVisibility()
        {
            bool text = DisplayText != null;
            showTextButton.IsVisible = text;
            showVerticesButton.IsVisible = text;
            searchButton.IsVisible = GoToSearch != null && graphControl != null && graphControl.SelectedVertex != null;
        }

        private static Thickness Gap => new Thickness(0, 0, 8, 0);

        private void Initialize()
        {
            var topToolbar = new StackPanel { Orientation = Orientation.Horizontal, MinHeight = 26 };
            var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, MinHeight = 26 };
            var toolbars = new StackPanel();
            toolbars.Children.Add(topToolbar);
            toolbars.Children.Add(toolbar);
            Children.Add(toolbars);
            SetDock(toolbars, Dock.Top);

            projectNameTextBlock = new TextBox
            {
                Margin = Gap,
                VerticalAlignment = VerticalAlignment.Center,
                IsReadOnly = true,
                IsVisible = false
            };

            Button MakeButton(string text) => new Button
            {
                Content = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = Gap,
                BorderThickness = new Thickness(0)
            };

            CheckBox MakeCheck(string text) => new CheckBox { Content = text, VerticalAlignment = VerticalAlignment.Center, Margin = Gap };

            searchButton = MakeButton("Go to search");
            searchButton.IsVisible = false;
            showTextButton = MakeButton("Text");
            showVerticesButton = MakeButton("Vertices");
            var locateButton = MakeButton("Locate on canvas");
            var helpButton = MakeButton("Help");
            searchTextBox = new TextBox { VerticalAlignment = VerticalAlignment.Center, Margin = Gap, MinWidth = 200, MaxWidth = 400 };

            var transitiveReduceCheck = MakeCheck("Hide transitive references");
            var allEdgesCheckbox = MakeCheck("All edges");
            var depthCheckbox = MakeCheck("Layer by depth");
            var horizontalCheckbox = MakeCheck("Horizontal");
            var invertedCheckbox = MakeCheck("Invert");

            var filterModeComboBox = new ComboBox { VerticalAlignment = VerticalAlignment.Center, Margin = Gap, Width = 190 };
            filterModeComboBox.Items.Add("No filter");
            filterModeComboBox.Items.Add("Direct references only");
            filterModeComboBox.Items.Add("Reachable only");
            filterModeComboBox.SelectedIndex = 0;

            graphControl = new GraphControl();

            helpButton.Click += (s, e) =>
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Launcher != null)
                {
                    _ = top.Launcher.LaunchUriAsync(new Uri("https://github.com/KirillOsenkov/MSBuildStructuredLog/wiki/Graph"));
                }
            };

            showTextButton.Click += (s, e) =>
            {
                var text = graph.GetDotText(transitiveReduceCheck.IsChecked == true, graphControl.GetVerticesToDisplay());
                DisplayText?.Invoke(text);
            };

            showVerticesButton.Click += (s, e) =>
            {
                var text = string.Join(Environment.NewLine, graphControl.GetVerticesToDisplay().Select(v => v.Title).OrderBy(t => t));
                DisplayText?.Invoke(text);
            };

            depthCheckbox.IsCheckedChanged += (s, e) => graphControl.LayerByDepth = depthCheckbox.IsChecked == true;
            horizontalCheckbox.IsCheckedChanged += (s, e) => graphControl.Horizontal = horizontalCheckbox.IsChecked == true;
            invertedCheckbox.IsCheckedChanged += (s, e) => graphControl.Inverted = invertedCheckbox.IsChecked == true;
            transitiveReduceCheck.IsCheckedChanged += (s, e) => graphControl.HideTransitiveEdges = transitiveReduceCheck.IsChecked == true;
            allEdgesCheckbox.IsCheckedChanged += (s, e) => graphControl.ShowAllEdges = allEdgesCheckbox.IsChecked == true;

            filterModeComboBox.SelectionChanged += (s, e) =>
            {
                e.Handled = true;
                switch (filterModeComboBox.SelectedIndex)
                {
                    case 1: graphControl.FilterMode = GraphFilterMode.DirectReferencesOnly; break;
                    case 2: graphControl.FilterMode = GraphFilterMode.ReachableOnly; break;
                    default: graphControl.FilterMode = GraphFilterMode.None; break;
                }
            };

            graphControl.SelectionChanged += () =>
            {
                var selected = graphControl.SelectedVertex;
                if (selected != null)
                {
                    projectNameTextBlock.Text = selected.Value;
                    projectNameTextBlock.IsVisible = true;
                    searchButton.IsVisible = GoToSearch != null;
                }
                else
                {
                    projectNameTextBlock.Text = "";
                    projectNameTextBlock.IsVisible = false;
                    searchButton.IsVisible = false;
                }
            };

            locateButton.Click += (s, e) =>
            {
                e.Handled = true;
                graphControl.Locate(searchTextBox.Text);
            };

            searchButton.Click += (s, e) =>
            {
                if (graphControl.SelectedVertex is Vertex vertex)
                {
                    GoToSearch?.Invoke(vertex.Value);
                }
            };

            searchTextBox.KeyDown += (s, e) =>
            {
                if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Return)
                {
                    e.Handled = true;
                    graphControl.Locate(searchTextBox.Text);
                }
            };

            toolbar.Children.Add(showTextButton);
            toolbar.Children.Add(showVerticesButton);
            toolbar.Children.Add(transitiveReduceCheck);
            toolbar.Children.Add(allEdgesCheckbox);
            toolbar.Children.Add(filterModeComboBox);
            toolbar.Children.Add(depthCheckbox);
            toolbar.Children.Add(horizontalCheckbox);
            toolbar.Children.Add(invertedCheckbox);
            toolbar.Children.Add(searchTextBox);
            toolbar.Children.Add(locateButton);
            toolbar.Children.Add(helpButton);

            topToolbar.Children.Add(searchButton);
            topToolbar.Children.Add(projectNameTextBlock);

            Children.Add(graphControl.Content);
        }
    }
}
