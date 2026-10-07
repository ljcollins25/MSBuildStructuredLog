using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Avalonia.Controls
{
    /// <summary>One visible row of the flat tree: the node, its depth and whether its children are listed after it.</summary>
    public sealed class FlatRow : INotifyPropertyChanged
    {
        public FlatRow(BaseNode node, int depth)
        {
            Node = node;
            Depth = depth;
        }

        public BaseNode Node { get; }

        public int Depth { get; }

        /// <summary>True while the rows of this node's children are in the list.</summary>
        internal bool Listed;

        internal INotifyCollectionChanged WatchedChildren;

        public bool HasChildren => Node is TreeNode t && t.HasChildren;

        /// <summary>Reads and writes the node's own flag (the owning tree reacts to the node's change notification).</summary>
        public bool IsExpanded
        {
            get => Node is TreeNode t && t.IsExpanded;
            set
            {
                if (Node is TreeNode t)
                {
                    t.IsExpanded = value;
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        internal void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A list of rows that raises one collection event for a whole range (50,000 rows at once).</summary>
    internal sealed class FlatRowList : List<FlatRow>, INotifyCollectionChanged
    {
        public event NotifyCollectionChangedEventHandler CollectionChanged;

        public void Replace(List<FlatRow> items)
        {
            Clear();
            AddRange(items);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void InsertRangeNotify(int index, List<FlatRow> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            InsertRange(index, items);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items, index));
        }

        public void RemoveRangeNotify(int index, int count)
        {
            if (count == 0)
            {
                return;
            }

            var removed = GetRange(index, count);
            RemoveRange(index, count);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
        }
    }

    /// <summary>
    /// The main tree as a virtualized flat list: one row per currently visible node (node, depth, expanded),
    /// so only the rows on screen exist as controls. Expanding inserts the child rows, collapsing removes them,
    /// and the existing per-type node templates render the node itself. Licence-free replacement for the
    /// TreeView, which keeps every expanded descendant as a live control.
    /// </summary>
    public class FlatTreeView : ListBox
    {
        private const double IndentWidth = 16;

        private readonly FlatRowList rows = new FlatRowList();
        private readonly Dictionary<BaseNode, FlatRow> map = new Dictionary<BaseNode, FlatRow>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, FlatRow> watched = new Dictionary<object, FlatRow>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<FlatRow> pendingRefresh = new HashSet<FlatRow>();
        private readonly PropertyChangedEventHandler nodeChanged;
        private readonly NotifyCollectionChangedEventHandler childrenChanged;
        private FlatRow rootRow;
        private bool updatingSelection;
        private bool refreshPosted;

        public FlatTreeView()
        {
            nodeChanged = OnNodePropertyChanged;
            childrenChanged = OnChildrenChanged;
            Classes.Add("flatTree");
            SelectionMode = SelectionMode.Single;
            ItemsSource = rows;
            ItemTemplate = new FuncDataTemplate<FlatRow>((row, _) => new FlatRowVisual(IndentWidth), supportsRecycling: true);
            SelectionChanged += OnSelectionChanged;
            // compact rows, like TreeViewItem
            var compactItem = new Style(x => x.OfType<ListBoxItem>());
            compactItem.Setters.Add(new global::Avalonia.Styling.Setter(PaddingProperty, new Thickness(0, 1)));
            compactItem.Setters.Add(new global::Avalonia.Styling.Setter(MinHeightProperty, 0.0));
            Styles.Add(compactItem);
        }

        protected override Type StyleKeyOverride => typeof(ListBox);

        /// <summary>Raised after the selected node changed (null when nothing is selected).</summary>
        public event Action<BaseNode> SelectedNodeChanged;

        /// <summary>The (hidden) root whose children are the top level rows, usually the Build.</summary>
        public TreeNode Root
        {
            get => rootRow?.Node as TreeNode;
            set
            {
                Reset();
                if (value == null)
                {
                    return;
                }

                rootRow = new FlatRow(value, -1) { Listed = true };
                Subscribe(rootRow);
                WatchChildren(rootRow);
                var list = new List<FlatRow>();
                AppendChildren(value, 0, list);
                rows.Replace(list);
            }
        }

        /// <summary>The top level nodes (what the TreeView's Items were).</summary>
        public IEnumerable<BaseNode> RootNodes => Root?.Children ?? (IEnumerable<BaseNode>)Array.Empty<BaseNode>();

        /// <summary>Number of rows currently in the list (visible nodes, scrolled off or not).</summary>
        public int RowCount => rows.Count;

        public BaseNode SelectedNode
        {
            get => (SelectedItem as FlatRow)?.Node;
            set
            {
                Flush();
                if (value != null && map.TryGetValue(value, out var row))
                {
                    SelectedItem = row;
                    ScrollIntoView(rows.IndexOf(row));
                }
                else
                {
                    SelectedItem = null;
                }
            }
        }

        public int IndexOfNode(BaseNode node) => node != null && map.TryGetValue(node, out var row) ? rows.IndexOf(row) : -1;

        /// <summary>Releases all node subscriptions (the Build outlives the control).</summary>
        public void Reset()
        {
            foreach (var row in map.Values.ToArray())
            {
                Unsubscribe(row);
            }

            if (rootRow != null)
            {
                Unsubscribe(rootRow);
                rootRow = null;
            }

            foreach (var key in watched.Keys.ToArray())
            {
                if (key is INotifyCollectionChanged incc)
                {
                    incc.CollectionChanged -= childrenChanged;
                }
            }

            watched.Clear();
            map.Clear();
            pendingRefresh.Clear();
            rows.Replace(new List<FlatRow>());
        }

        // ---- building rows ----

        private FlatRow CreateRow(BaseNode node, int depth)
        {
            var row = new FlatRow(node, depth);
            map[node] = row;
            Subscribe(row);
            return row;
        }

        private void Subscribe(FlatRow row)
        {
            if (row.Node is TreeNode)
            {
                row.Node.PropertyChanged += nodeChanged;
            }
        }

        private void Unsubscribe(FlatRow row)
        {
            if (row.Node is TreeNode)
            {
                row.Node.PropertyChanged -= nodeChanged;
            }

            UnwatchChildren(row);
            if (row != rootRow)
            {
                map.Remove(row.Node);
            }
        }

        private void WatchChildren(FlatRow row)
        {
            if (row.WatchedChildren != null || row.Node is not TreeNode tn)
            {
                return;
            }

            if (tn.Children is INotifyCollectionChanged incc)
            {
                row.WatchedChildren = incc;
                watched[incc] = row;
                incc.CollectionChanged += childrenChanged;
            }
        }

        private void UnwatchChildren(FlatRow row)
        {
            if (row.WatchedChildren != null)
            {
                row.WatchedChildren.CollectionChanged -= childrenChanged;
                watched.Remove(row.WatchedChildren);
                row.WatchedChildren = null;
            }
        }

        /// <summary>Appends rows for the visible children of parent (recursively for children that are expanded).</summary>
        private void AppendChildren(TreeNode parent, int depth, List<FlatRow> output)
        {
            var children = parent.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child is TreeNode hidden && !hidden.IsVisible)
                {
                    continue;
                }

                var row = CreateRow(child, depth);
                output.Add(row);
                if (child is TreeNode tn && tn.IsExpanded && tn.HasChildren)
                {
                    row.Listed = true;
                    WatchChildren(row);
                    AppendChildren(tn, depth + 1, output);
                }
            }
        }

        private int IndexOf(FlatRow row) => row == rootRow ? -1 : rows.IndexOf(row);

        private void ListChildren(FlatRow row)
        {
            int index = IndexOf(row);
            if (index < -1 || row.Node is not TreeNode tn || (row != rootRow && index < 0))
            {
                return;
            }

            row.Listed = true;
            WatchChildren(row);
            var list = new List<FlatRow>();
            AppendChildren(tn, row.Depth + 1, list);
            rows.InsertRangeNotify(index + 1, list);
        }

        private void UnlistChildren(FlatRow row)
        {
            int index = IndexOf(row);
            if (row != rootRow && index < 0)
            {
                return;
            }

            int start = index + 1;
            int end = start;
            while (end < rows.Count && rows[end].Depth > row.Depth)
            {
                end++;
            }

            // the selection must not silently vanish with its row: a collapsed ancestor takes it over
            var selected = SelectedItem as FlatRow;
            bool takeOver = selected != null && row != rootRow && rows.IndexOf(selected) is int si && si >= start && si < end;
            for (int i = start; i < end; i++)
            {
                Unsubscribe(rows[i]);
            }

            row.Listed = false;
            if (row != rootRow)
            {
                UnwatchChildren(row);
            }

            if (takeOver)
            {
                SelectedItem = row;
            }

            rows.RemoveRangeNotify(start, end - start);
        }

        private void RefreshChildren(FlatRow row)
        {
            if (row != rootRow && IndexOf(row) < 0)
            {
                return;
            }

            var selectedNode = SelectedNode;
            bool wantListed = row == rootRow || (row.Node is TreeNode t && t.IsExpanded && t.HasChildren);
            if (row.Listed)
            {
                UnlistChildren(row);
            }

            if (row != rootRow)
            {
                UnwatchChildren(row);
            }

            if (wantListed)
            {
                ListChildren(row);
            }

            row.Raise(nameof(FlatRow.HasChildren));
            row.Raise(nameof(FlatRow.IsExpanded));
            if (selectedNode != null && !ReferenceEquals(SelectedNode, selectedNode) && map.ContainsKey(selectedNode))
            {
                SelectedNode = selectedNode;
            }
        }

        // ---- reacting to the model ----

        private void OnNodePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is not BaseNode node || !map.TryGetValue(node, out var row))
            {
                if (sender == rootRow?.Node && e.PropertyName == nameof(TreeNode.Children))
                {
                    QueueRefresh(rootRow);
                }

                return;
            }

            switch (e.PropertyName)
            {
                case nameof(TreeNode.IsExpanded):
                    {
                        bool want = row.Node is TreeNode t && t.IsExpanded && t.HasChildren;
                        if (want && !row.Listed)
                        {
                            ListChildren(row);
                        }
                        else if (!want && row.Listed)
                        {
                            UnlistChildren(row);
                        }

                        row.Raise(nameof(FlatRow.IsExpanded));
                        break;
                    }

                case nameof(TreeNode.HasChildren):
                case nameof(TreeNode.Children):
                    QueueRefresh(row);
                    break;

                case nameof(TreeNode.IsVisible):
                    {
                        var parentRow = node.Parent != null && map.TryGetValue(node.Parent, out var p) ? p : (node.Parent == rootRow?.Node ? rootRow : null);
                        if (parentRow != null && parentRow.Listed)
                        {
                            QueueRefresh(parentRow);
                        }

                        break;
                    }

                case nameof(BaseNode.IsSelected):
                    if (node.IsSelected && !updatingSelection && !ReferenceEquals(SelectedNode, node))
                    {
                        SelectedNode = node;
                    }

                    break;
            }
        }

        private void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (watched.TryGetValue(sender, out var row))
            {
                QueueRefresh(row);
            }
        }

        // children collections change in bursts (a node being filled): rebuild once, after the burst
        private void QueueRefresh(FlatRow row)
        {
            pendingRefresh.Add(row);
            if (!refreshPosted)
            {
                refreshPosted = true;
                Dispatcher.UIThread.Post(Flush, DispatcherPriority.Send);
            }
        }

        /// <summary>Applies pending child-collection changes now.</summary>
        public void Flush()
        {
            refreshPosted = false;
            if (pendingRefresh.Count == 0)
            {
                return;
            }

            var batch = pendingRefresh.OrderBy(r => r.Depth).ToArray();
            pendingRefresh.Clear();
            foreach (var row in batch)
            {
                RefreshChildren(row);
            }
        }

        // ---- selection ----

        private FlatRow previousSelected;

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = SelectedItem as FlatRow;
            updatingSelection = true;
            try
            {
                if (previousSelected != null && !ReferenceEquals(previousSelected, row))
                {
                    previousSelected.Node.IsSelected = false;
                }

                if (row != null)
                {
                    row.Node.IsSelected = true;
                }
            }
            finally
            {
                updatingSelection = false;
            }

            previousSelected = row;
            SelectedNodeChanged?.Invoke(row?.Node);
        }

        // ---- keyboard: Left / Right expand and collapse or move to the parent / first child ----

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!e.Handled && e.KeyModifiers == KeyModifiers.None && SelectedItem is FlatRow row)
            {
                if (e.Key == Key.Right)
                {
                    if (row.HasChildren && !row.IsExpanded)
                    {
                        row.IsExpanded = true;
                    }
                    else if (row.Listed)
                    {
                        int next = rows.IndexOf(row) + 1;
                        if (next < rows.Count && rows[next].Depth > row.Depth)
                        {
                            SelectedItem = rows[next];
                            ScrollIntoView(next);
                        }
                    }

                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Left)
                {
                    if (row.Listed && row.IsExpanded)
                    {
                        row.IsExpanded = false;
                    }
                    else
                    {
                        for (int i = rows.IndexOf(row) - 1; i >= 0; i--)
                        {
                            if (rows[i].Depth < row.Depth)
                            {
                                SelectedItem = rows[i];
                                ScrollIntoView(i);
                                break;
                            }
                        }
                    }

                    e.Handled = true;
                    return;
                }
            }

            base.OnKeyDown(e);
        }

        /// <summary>The node of the row under a pointer event source, or null.</summary>
        public static BaseNode NodeFromSource(object source) =>
            ((source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FlatRow)?.Node;
    }

    /// <summary>The visual of one row: indentation, expander and the node's own template. Recycled: it follows its DataContext.</summary>
    internal sealed class FlatRowVisual : StackPanel
    {
        private static readonly Geometry ChevronGeometry = Geometry.Parse("M0,0 L5,4 L0,8 Z");
        private readonly double indentWidth;
        private readonly Border indent = new Border();
        private readonly PathIcon chevron;
        private readonly RotateTransform rotate = new RotateTransform();
        private readonly ContentControl content;
        private FlatRow row;
        private bool attached;
        private TextBlock cheap;

        public FlatRowVisual(double indentWidth)
        {
            this.indentWidth = indentWidth;
            Orientation = global::Avalonia.Layout.Orientation.Horizontal;
            Background = Brushes.Transparent;
            chevron = new PathIcon
            {
                Data = ChevronGeometry,
                Width = 8,
                Height = 8,
                RenderTransform = rotate,
                RenderTransformOrigin = RelativePoint.Center,
                IsHitTestVisible = false,
            };
            var expander = new Border
            {
                Width = indentWidth,
                Height = 16,
                Background = Brushes.Transparent,
                Child = chevron,
            };
            expander.PointerPressed += (s, e) =>
            {
                if (row != null && row.HasChildren && e.GetCurrentPoint(expander).Properties.IsLeftButtonPressed)
                {
                    row.IsExpanded = !row.IsExpanded;
                    e.Handled = true;
                }
            };
            if (Environment.GetEnvironmentVariable("FLAT_CHEAP") == "1") { cheap = new TextBlock { VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center }; }
            content = new ContentControl { VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center, Focusable = false };
            Children.Add(indent);
            Children.Add(expander);
            Children.Add(cheap ?? (Control)content);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Bind(DataContext as FlatRow);
        }

        private void Bind(FlatRow newRow)
        {
            if (attached && row != null)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            row = newRow;
            if (row == null)
            {
                content.Content = null;
                return;
            }

            if (attached)
            {
                row.PropertyChanged += OnRowChanged;
            }

            indent.Width = Math.Max(0, row.Depth) * indentWidth;
            if (cheap != null) cheap.Text = row.Node.ToString(); else content.Content = row.Node;
            Update();
        }

        private void Update()
        {
            if (row == null)
            {
                return;
            }

            chevron.IsVisible = row.HasChildren;
            rotate.Angle = row.IsExpanded ? 90 : 0;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            if (row != null)
            {
                row.PropertyChanged += OnRowChanged;
                Update();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            attached = false;
            if (row != null)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            base.OnDetachedFromVisualTree(e);
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e) => Update();
    }
}
