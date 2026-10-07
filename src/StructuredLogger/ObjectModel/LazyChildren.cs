using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.Build.Logging.StructuredLogger
{
    /// <summary>
    /// A placeholder for the children of a node that have not been created yet. A node whose children
    /// are derived from compact data (the metadata of an item, the properties of a project) keeps only
    /// this small object until somebody asks for <see cref="TreeNode.Children"/>; then the real nodes are
    /// created once and replace it. <see cref="TreeNode.HasChildren"/> does not trigger the creation.
    /// It implements IList only so that it fits the field of the node; TreeNode never exposes it and every
    /// member other than Count throws.
    /// </summary>
    internal abstract class LazyChildren : IList<BaseNode>
    {
        /// <summary>Number of children that <see cref="Create"/> will produce.</summary>
        public abstract int Count { get; }

        /// <summary>Creates the child nodes; the caller sets their Parent.</summary>
        public abstract IList<BaseNode> Create(TreeNode parent);

        public bool IsReadOnly => true;

        public BaseNode this[int index] { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public int IndexOf(BaseNode item) => throw new NotSupportedException();

        public void Insert(int index, BaseNode item) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void Add(BaseNode item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(BaseNode item) => throw new NotSupportedException();

        public void CopyTo(BaseNode[] array, int arrayIndex) => throw new NotSupportedException();

        public bool Remove(BaseNode item) => throw new NotSupportedException();

        public IEnumerator<BaseNode> GetEnumerator() => throw new NotSupportedException();

        IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
    }

    /// <summary>
    /// Name/value children (metadata of an item, properties of a project) kept as the two arrays of the
    /// name/value record they were read from. The record is shared by all items that have the same metadata,
    /// so the placeholder costs one small object per item instead of one node per entry.
    /// </summary>
    internal sealed class LazyNameValues<T> : LazyChildren where T : NameValueNode, new()
    {
        private readonly string[] names;
        private readonly string[] values;
        private readonly int count;

        public LazyNameValues(string[] names, string[] values, int count)
        {
            this.names = names;
            this.values = values;
            this.count = count;
        }

        public override int Count => count;

        public override IList<BaseNode> Create(TreeNode parent)
        {
            var list = new ChildrenList(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new T { Name = names[i], Value = values[i] });
            }

            return list;
        }
    }

    /// <summary>
    /// The items of one item type (the children of an AddItem node) kept as the item records that were read
    /// from the log: the item spec and a reference to the shared metadata dictionary. An Item node, and its
    /// metadata, are only created when the children of the AddItem node are requested.
    /// </summary>
    internal sealed class LazyItems : LazyChildren
    {
        private readonly List<Microsoft.Build.Framework.ITaskItem> items;
        private readonly Action<Microsoft.Build.Framework.ITaskItem, Item> addMetadata;

        public LazyItems(List<Microsoft.Build.Framework.ITaskItem> items, Action<Microsoft.Build.Framework.ITaskItem, Item> addMetadata)
        {
            this.items = items;
            this.addMetadata = addMetadata;
        }

        public override int Count => items.Count;

        public override IList<BaseNode> Create(TreeNode parent)
        {
            var list = new ChildrenList(items.Count);
            foreach (var taskItem in items)
            {
                var item = new Item { Text = taskItem.ItemSpec };
                addMetadata(taskItem, item);
                list.Add(item);
            }

            return list;
        }
    }

    /// <summary>The children a node already has, followed by lazily created ones.</summary>
    internal sealed class LazyAfterExisting : LazyChildren
    {
        private readonly IList<BaseNode> existing;
        private readonly LazyChildren lazy;

        public LazyAfterExisting(IList<BaseNode> existing, LazyChildren lazy)
        {
            this.existing = existing;
            this.lazy = lazy;
        }

        public override int Count => existing.Count + lazy.Count;

        public override IList<BaseNode> Create(TreeNode parent)
        {
            var created = lazy.Create(parent);
            var list = new ChildrenList(existing.Count + created.Count);
            foreach (var node in existing)
            {
                list.Add(node);
            }

            foreach (var node in created)
            {
                list.Add(node);
            }

            return list;
        }
    }
}
