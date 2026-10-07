using System.Collections.Generic;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Browser
{
    /// <summary>A lazily expanded tree item wrapping a log node.</summary>
    public class NodeViewModel
    {
        private List<NodeViewModel> children;

        public NodeViewModel(BaseNode node)
        {
            Node = node;
        }

        public BaseNode Node { get; }

        public string Text
        {
            get
            {
                var title = Node.Title ?? Node.TypeName;
                title = title.Replace("\r", " ").Replace("\n", " ");
                return title.Length > 300 ? title.Substring(0, 300) + "..." : title;
            }
        }

        public IEnumerable<NodeViewModel> Children
        {
            get
            {
                if (children == null)
                {
                    children = new List<NodeViewModel>();
                    if (Node is TreeNode tree && tree.HasChildren)
                    {
                        foreach (var child in tree.Children)
                        {
                            children.Add(new NodeViewModel(child));
                        }
                    }
                }

                return children;
            }
        }

        public override string ToString() => Text;
    }
}
