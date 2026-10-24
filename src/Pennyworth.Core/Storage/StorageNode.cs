namespace Pennyworth.Core.Storage;

/// <summary>A lightweight handle to one file or folder in a <see cref="StorageTree"/>.</summary>
public readonly record struct StorageNode(StorageTree Tree, int Index)
{
    public string Name => Tree.Name(Index);

    public bool IsDirectory => (Tree.Node(Index).Flags & NodeFlags.Directory) != 0;

    /// <summary>True when Windows refused to list this folder's contents.</summary>
    public bool IsLocked => (Tree.Node(Index).Flags & NodeFlags.Locked) != 0;

    public long SizeOnDisk => Tree.Node(Index).SizeOnDisk;

    public long LogicalSize => Tree.Node(Index).LogicalSize;

    public StorageCategory Category => Tree.Node(Index).Category;

    public StorageNode? Parent =>
        Index == StorageTreeBuilder.RootIndex ? null : new StorageNode(Tree, Tree.Node(Index).Parent);

    public bool HasChildren => Tree.Node(Index).FirstChild >= 0;

    public string FullPath
    {
        get
        {
            var segments = new Stack<string>();
            for (StorageNode? node = this; node is { } current; node = current.Parent)
            {
                segments.Push(current.Name);
            }

            return Path.Combine([.. segments]);
        }
    }

    /// <summary>Direct children, largest first.</summary>
    public IReadOnlyList<StorageNode> GetChildren()
    {
        var children = new List<StorageNode>();
        for (int child = Tree.Node(Index).FirstChild; child >= 0; child = Tree.Node(child).NextSibling)
        {
            children.Add(new StorageNode(Tree, child));
        }

        children.Sort((a, b) => b.SizeOnDisk.CompareTo(a.SizeOnDisk));
        return children;
    }
}
