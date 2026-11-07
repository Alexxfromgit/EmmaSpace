using System.Globalization;
using System.Net;
using System.Text;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Export;

/// <summary>Writes a self-contained HTML report (no external resources) summarising a scan.</summary>
public static class HtmlReportExporter
{
    /// <summary>
    /// Builds the report in the background. Call it on the thread that changes the tree (the UI
    /// thread): the tree is copied before the first await, so every section describes the same
    /// state even if a removal happens meanwhile.
    /// </summary>
    public static async Task ExportAsync(FileTree tree, string path, CancellationToken ct = default)
    {
        string html;
        using (var copy = tree.Copy())
            html = await Task.Run(() => Build(copy, ct), ct);
        await File.WriteAllTextAsync(path, html, Encoding.UTF8, ct);
    }

    /// <summary>Builds synchronously, on the calling thread (which must be the one changing the tree).</summary>
    public static string Build(FileTree tree, CancellationToken ct = default)
    {
        ref readonly var root = ref tree.Nodes[FileTree.Root];
        var types = FileTypeBreakdown.Compute(tree, ct: ct);
        var largest = FileQueries.LargestFiles(tree, 50, ct: ct);
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>Emma Space report</title><style>").Append(Css).Append("</style></head><body><main>");

        sb.Append("<h1>Emma Space report</h1><p class=\"muted\">")
          .Append(E(tree.RootPath)).Append(" · scanned ")
          .Append(E(tree.Info.ScannedAt.ToString("f", CultureInfo.CurrentCulture))).Append("</p>");

        sb.Append("<section class=\"cards\">");
        Card(sb, "Total size", ByteSize.Format(root.Size));
        Card(sb, "Size on disk", ByteSize.Format(root.Allocated));
        Card(sb, "Files", root.FileCount.ToString("N0", CultureInfo.CurrentCulture));
        Card(sb, "Folders", root.DirCount.ToString("N0", CultureInfo.CurrentCulture));
        sb.Append("</section>");

        sb.Append("<h2>Largest folders</h2><table><thead><tr><th>Folder</th><th class=\"num\">Size</th><th class=\"bar\">Share</th><th class=\"num\">Files</th></tr></thead><tbody>");
        foreach (var (node, depth) in TopFolders(tree, maxDepth: 2, perLevel: 12))
        {
            ref readonly var n = ref tree.Nodes[node];
            var share = root.Size == 0 ? 0 : n.Size * 100.0 / root.Size;
            sb.Append("<tr><td style=\"padding-left:").Append((depth * 18 + 8).ToString(inv)).Append("px\">")
              .Append(E(depth == 0 ? tree.RootPath : tree.GetName(node))).Append("</td><td class=\"num\">")
              .Append(ByteSize.Format(n.Size)).Append("</td><td class=\"bar\">");
            Bar(sb, share);
            sb.Append("</td><td class=\"num\">").Append(n.FileCount.ToString("N0", CultureInfo.CurrentCulture)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<h2>By category</h2><table><thead><tr><th>Category</th><th class=\"num\">Size</th><th class=\"bar\">Share</th><th class=\"num\">Files</th></tr></thead><tbody>");
        foreach (var c in types.Categories)
        {
            var share = root.Size == 0 ? 0 : c.Bytes * 100.0 / root.Size;
            sb.Append("<tr><td>").Append(E(c.Category.ToString())).Append("</td><td class=\"num\">").Append(ByteSize.Format(c.Bytes))
              .Append("</td><td class=\"bar\">");
            Bar(sb, share);
            sb.Append("</td><td class=\"num\">").Append(c.Count.ToString("N0", CultureInfo.CurrentCulture)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<h2>Top extensions</h2><table><thead><tr><th>Extension</th><th>Category</th><th class=\"num\">Size</th><th class=\"num\">Files</th></tr></thead><tbody>");
        foreach (var e in types.Extensions.Take(25))
            sb.Append("<tr><td>").Append(E(e.Extension)).Append("</td><td>").Append(E(e.Category.ToString()))
              .Append("</td><td class=\"num\">").Append(ByteSize.Format(e.Bytes)).Append("</td><td class=\"num\">")
              .Append(e.Count.ToString("N0", CultureInfo.CurrentCulture)).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Largest files</h2><table><thead><tr><th>File</th><th class=\"num\">Size</th><th>Modified</th></tr></thead><tbody>");
        foreach (var node in largest)
        {
            ref readonly var n = ref tree.Nodes[node];
            var modified = UnixTime.ToDateTimeOffset(n.MTime)?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "";
            sb.Append("<tr><td class=\"path\">").Append(E(tree.GetPath(node))).Append("</td><td class=\"num\">")
              .Append(ByteSize.Format(n.Size)).Append("</td><td>").Append(E(modified)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<p class=\"muted\">Generated by Emma Space.</p></main></body></html>");
        return sb.ToString();
    }

    private static IEnumerable<(int Node, int Depth)> TopFolders(FileTree tree, int maxDepth, int perLevel)
    {
        var stack = new Stack<(int, int)>();
        stack.Push((FileTree.Root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            yield return (node, depth);
            if (depth >= maxDepth) continue;
            var dirs = new List<int>();
            foreach (var c in tree.Children(node))
            {
                if (!tree.Nodes[c].IsDirectory) continue;
                dirs.Add(c);
                if (dirs.Count == (depth == 0 ? perLevel : perLevel / 3)) break;
            }
            for (var i = dirs.Count - 1; i >= 0; i--) stack.Push((dirs[i], depth + 1));
        }
    }

    private static void Card(StringBuilder sb, string label, string value) =>
        sb.Append("<div class=\"card\"><div class=\"muted\">").Append(E(label)).Append("</div><div class=\"value\">")
          .Append(E(value)).Append("</div></div>");

    private static void Bar(StringBuilder sb, double percent) =>
        sb.Append("<div class=\"track\"><div class=\"fill\" style=\"width:")
          .Append(Math.Clamp(percent, 0, 100).ToString("0.0", CultureInfo.InvariantCulture)).Append("%\"></div></div><span>")
          .Append(percent.ToString("0.0", CultureInfo.CurrentCulture)).Append("%</span>");

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private const string Css = """
        :root{--bg:#f7f7fb;--fg:#1d1d27;--muted:#6b6b80;--card:#fff;--line:#e4e4ee;--accent:#7c5cff}
        @media (prefers-color-scheme:dark){:root{--bg:#14141b;--fg:#ececf3;--muted:#9a9ab0;--card:#1d1d27;--line:#2c2c3a;--accent:#9d86ff}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:1100px;margin:0 auto;padding:32px 16px}h1{margin:0 0 4px;font-size:28px}h2{margin:32px 0 12px;font-size:18px}
        .muted{color:var(--muted)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin-top:20px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}.value{font-size:22px;font-weight:600}
        table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:hidden}
        th,td{padding:7px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:middle}th{color:var(--muted);font-weight:500}
        tr:last-child td{border-bottom:0}.num{text-align:right;white-space:nowrap}.path{word-break:break-all}
        .bar{width:28%}.bar span{font-size:12px;color:var(--muted);margin-left:8px}
        .track{display:inline-block;width:calc(100% - 60px);height:8px;border-radius:4px;background:var(--line);vertical-align:middle}
        .fill{height:100%;border-radius:4px;background:var(--accent)}
        """;
}
