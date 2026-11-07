using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Tests;

internal static class TreeFixtures
{
    public static readonly long Old = DateTimeOffset.UtcNow.AddYears(-3).ToUnixTimeMilliseconds();
    public static readonly long Recent = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

    /// <summary>
    /// root
    ///   videos/      movie.mp4 (1000, old), clip.MKV (300)
    ///   docs/        a.pdf (50), notes.txt (10, old), sub/ (empty) / deeper/ (empty)
    ///   empty/
    ///   big.zip (2000)
    ///   README (5)
    /// </summary>
    public static FileTree Sample(string rootPath = @"C:\Root")
    {
        var b = new TreeBuilder();
        b.AddRoot(rootPath);
        var videos = b.AddChild(0, "videos", NodeFlags.Directory);
        b.AddChild(videos, "movie.mp4", NodeFlags.None, 1000, 4096, Old, Old);
        b.AddChild(videos, "clip.MKV", NodeFlags.None, 300, 4096, Recent, Recent);
        var docs = b.AddChild(0, "docs", NodeFlags.Directory);
        b.AddChild(docs, "a.pdf", NodeFlags.None, 50, 4096, Recent, Recent);
        b.AddChild(docs, "notes.txt", NodeFlags.None, 10, 4096, Old, Old);
        var sub = b.AddChild(docs, "sub", NodeFlags.Directory);
        b.AddChild(sub, "deeper", NodeFlags.Directory);
        b.AddChild(0, "empty", NodeFlags.Directory);
        b.AddChild(0, "big.zip", NodeFlags.None, 2000, 4096, Recent, Recent);
        b.AddChild(0, "README", NodeFlags.None, 5, 4096, Recent, Recent);
        return b.Build(new ScanInfo(rootPath, DateTimeOffset.Now, TimeSpan.Zero, ScanMode.Walker, ScanSource.Managed, 0));
    }

    public static int Find(FileTree tree, string relative) =>
        tree.FindByPath(Path.Join(tree.RootPath, relative));
}
