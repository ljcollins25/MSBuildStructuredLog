using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.Build.Logging.StructuredLogger
{
    /// <summary>
    /// A placeholder for the children of a node that have not been created yet. A node whose children
    /// are derived from compact data (the metadata of an item) keeps only this small object until somebody
    /// asks for <see cref="TreeNode.Children"/>, then the real nodes are created once and replace it.
    /// <see cref="TreeNode.HasChildren"/> does not trigger the creation. It implements IList only so that it
    /// fits the field of the node; TreeNode never exposes it, every member other than Count throws.
    /// </summary>
    internal abstract class LazyChildren : IList<BaseNode>
    {
        /// <summary>Number of children that <see cref="Create"/> will produce.</summary>
        public abstract int Count { get; }

        /// <summary>Creates the child nodes; they get their Parent set by the caller.</summary>
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
    /// The metadata of an item, as the two arrays of the name/value record they were read from. The record is
    /// shared by all items that have the same metadata, so this placeholder can be shared too.
    /// </summary>
    internal sealed class LazyMetadata : LazyChildren
    {
        private readonly string[] names;
        private readonly string[] values;
        private readonly int count;

        public LazyMetadata(string[] names, string[] values, int count)
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
                list.Add(new Metadata { Name = names[i], Value = values[i] });
            }

            return list;
        }
    }

    /// <summary>The properties of a project or evaluation, as the arrays of the dictionary they were read from.</summary>
    internal sealed class LazyProperties : LazyChildren
    {
        private readonly string[] names;
        private readonly string[] values;
        private readonly int count;

        public LazyProperties(string[] names, string[] values, int count)
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
                list.Add(new Property { Name = names[i], Value = values[i] });
            }

            return list;
        }
    }
}
