using System.Globalization;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Export;

public sealed record CsvExportOptions
{
    public bool IncludeFiles { get; init; }
    /// <summary>Maximum depth below the root (root = 0). Null = unlimited.</summary>
    public int? MaxDepth { get; init; } = 4;
}

public static class CsvExporter
{
    /// <summary>
    /// Exports in the background. Call it on the thread that changes the tree (the UI thread): the
    /// tree is copied before the first await, so a removal meanwhile cannot skip folders or mix
    /// totals from before and after it.
    /// </summary>
    public static async Task ExportAsync(FileTree tree, string path, CsvExportOptions options, CancellationToken ct = default)
    {
        using var copy = tree.Copy();
        await using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await Task.Run(() => Write(copy, writer, options, ct), ct);
    }

    /// <summary>Writes synchronously, on the calling thread (which must be the one changing the tree).</summary>
    public static void Write(FileTree tree, TextWriter writer, CsvExportOptions options, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        writer.WriteLine("Path,Type,Size,SizeOnDisk,Files,Folders,PercentOfParent,Modified");
        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((FileTree.Root, 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (node, depth) = stack.Pop();
            ref readonly var n = ref tree.Nodes[node];
            var parent = tree.GetParent(node);
            var parentSize = parent == FileTree.None ? n.Size : tree.Nodes[parent].Size;
            var percent = parentSize == 0 ? 0 : n.Size * 100.0 / parentSize;
            var modified = UnixTime.ToDateTimeOffset(n.MTime)?.ToString("u", inv) ?? "";

            writer.Write(Escape(tree.GetPath(node)));
            writer.Write(n.IsDirectory ? ",Folder," : ",File,");
            writer.Write(n.Size.ToString(inv));
            writer.Write(',');
            writer.Write(n.Allocated.ToString(inv));
            writer.Write(',');
            writer.Write(n.FileCount.ToString(inv));
            writer.Write(',');
            writer.Write(n.DirCount.ToString(inv));
            writer.Write(',');
            writer.Write(percent.ToString("0.00", inv));
            writer.Write(',');
            writer.WriteLine(modified);

            if (!n.IsDirectory || (options.MaxDepth is { } max && depth >= max)) continue;
            // Push in reverse so output keeps the largest-first order.
            var children = new List<int>();
            foreach (var c in tree.Children(node))
                if (options.IncludeFiles || tree.Nodes[c].IsDirectory) children.Add(c);
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
    }

    internal static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
