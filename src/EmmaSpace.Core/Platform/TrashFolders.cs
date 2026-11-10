namespace EmmaSpace.Core.Platform;

/// <summary>
/// Recognizes paths inside a Recycle Bin or Trash folder, which scans include like any other folder:
/// <c>$Recycle.Bin</c> (and the older <c>RECYCLER</c>) on Windows drives, <c>.Trash</c> and <c>.Trashes</c> on
/// macOS, and the freedesktop.org trash on Linux (<c>~/.local/share/Trash</c>, <c>.Trash-1000</c> and
/// <c>.Trash/1000</c> at the top of other drives). Checked on every system, since a drive formatted on one
/// can be read on another.
/// </summary>
public static class TrashFolders
{
    public static bool Contains(string path)
    {
        foreach (var part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || part.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase) ||
                part is ".Trash" or ".Trashes" || part.StartsWith(".Trash-", StringComparison.Ordinal))
                return true;
        }
        return path.Replace('\\', '/').Contains("/.local/share/Trash/", StringComparison.Ordinal);
    }
}
