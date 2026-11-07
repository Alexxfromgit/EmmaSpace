using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Snapshots;

public enum ChangeKind
{
    Grown,
    Shrunk,
    Added,
    Removed,
}

public sealed record DiffEntry(string Path, bool IsDirectory, ChangeKind Kind, ulong OldSize, ulong NewSize)
{
    public long Delta => (long)NewSize - (long)OldSize;
}

public sealed record DiffReport(ulong OldTotal, ulong NewTotal, IReadOnlyList<DiffEntry> Changes)
{
    public long Delta => (long)NewTotal - (long)OldTotal;
}

/// <summary>Compares two scans of the same location by matching paths.</summary>
public static class SnapshotDiff
{
    /// <summary>
    /// Whether two scans are of the same folder or drive. Compare only matches children by name,
    /// so comparing unrelated locations would report meaningless growth or no changes at all.
    /// </summary>
    public static bool SameLocation(FileTree a, FileTree b) =>
        string.Equals(TrimRoot(a.RootPath), TrimRoot(b.RootPath), FileTree.PathComparison);

    // "C:\" and "C:", "/data/" and "/data" are the same place.
    private static string TrimRoot(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static DiffReport Compare(FileTree older, FileTree newer, int maxResults = 5000, CancellationToken ct = default)
    {
        var changes = new List<DiffEntry>();
        var stack = new Stack<(int Old, int New, string Path)>();
        stack.Push((FileTree.Root, FileTree.Root, newer.RootPath));
        var oldChildren = new Dictionary<string, int>(FileTree.PathComparer);
        var lookup = oldChildren.GetAlternateLookup<ReadOnlySpan<char>>();

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (o, n, path) = stack.Pop();

            oldChildren.Clear();
            foreach (var c in older.Children(o)) oldChildren[older.GetName(c)] = c;

            foreach (var c in newer.Children(n))
            {
                var name = newer.GetNameSpan(c);
                ref readonly var nn = ref newer.Nodes[c];
                var childPath = Path.Join(path, name);
                if (!lookup.TryGetValue(name, out var oc))
                {
                    changes.Add(new DiffEntry(childPath, nn.IsDirectory, ChangeKind.Added, 0, nn.Size));
                    continue;
                }
                lookup.Remove(name);
                ref readonly var on = ref older.Nodes[oc];

                if (on.IsDirectory != nn.IsDirectory)
                {
                    changes.Add(new DiffEntry(childPath, on.IsDirectory, ChangeKind.Removed, on.Size, 0));
                    changes.Add(new DiffEntry(childPath, nn.IsDirectory, ChangeKind.Added, 0, nn.Size));
                    continue;
                }
                if (on.Size != nn.Size)
                    changes.Add(new DiffEntry(childPath, nn.IsDirectory, nn.Size > on.Size ? ChangeKind.Grown : ChangeKind.Shrunk, on.Size, nn.Size));
                // Always descend: equal totals do not mean equal contents (a rename, or one file
                // growing while another shrinks, keeps size and count the same).
                if (nn.IsDirectory) stack.Push((oc, c, childPath));
            }

            foreach (var (name, oc) in oldChildren)
            {
                ref readonly var on = ref older.Nodes[oc];
                changes.Add(new DiffEntry(Path.Join(path, name), on.IsDirectory, ChangeKind.Removed, on.Size, 0));
            }
        }

        var top = changes
            .OrderByDescending(c => Math.Abs(c.Delta))
            .Take(maxResults)
            .ToList();
        return new DiffReport(older[FileTree.Root].Size, newer[FileTree.Root].Size, top);
    }
}
