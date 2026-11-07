using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Analysis;

public sealed record ExtensionStats(string Extension, FileCategory Category, long Count, ulong Bytes);

public sealed record CategoryStats(FileCategory Category, long Count, ulong Bytes);

public sealed record FileTypeReport(IReadOnlyList<ExtensionStats> Extensions, IReadOnlyList<CategoryStats> Categories);

public static class FileTypeBreakdown
{
    public const string NoExtension = "(none)";

    /// <summary>Aggregates file counts and bytes per extension and per category within <paramref name="scope"/>.</summary>
    public static FileTypeReport Compute(FileTree tree, int scope = FileTree.Root, CancellationToken ct = default)
    {
        var byExtension = new Dictionary<string, (long Count, ulong Bytes)>(StringComparer.OrdinalIgnoreCase);
        var lookup = byExtension.GetAlternateLookup<ReadOnlySpan<char>>();

        foreach (var node in TreeWalk.Files(tree, scope))
        {
            if ((node & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var ext = tree.GetExtension(node);
            if (ext.IsEmpty) ext = NoExtension;
            ref readonly var n = ref tree.Nodes[node];
            if (lookup.TryGetValue(ext, out var acc))
                lookup[ext] = (acc.Count + 1, acc.Bytes + n.Size);
            else
                lookup[ext] = (1, n.Size);
        }

        var extensions = byExtension
            .Select(kv => new ExtensionStats(kv.Key.ToLowerInvariant(), FileCategories.Categorize(kv.Key), kv.Value.Count, kv.Value.Bytes))
            .OrderByDescending(e => e.Bytes)
            .ToList();
        var categories = extensions
            .GroupBy(e => e.Category)
            .Select(g => new CategoryStats(g.Key, g.Sum(e => e.Count), (ulong)g.Sum(e => (decimal)e.Bytes)))
            .OrderByDescending(c => c.Bytes)
            .ToList();
        return new FileTypeReport(extensions, categories);
    }
}
