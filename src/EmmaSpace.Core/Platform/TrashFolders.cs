namespace EmmaSpace.Core.Platform;

/// <summary>
/// Recognizes paths inside a Recycle Bin or Trash folder, which scans include like any other folder:
/// <c>$Recycle.Bin</c> (and the older <c>RECYCLER</c>) on Windows drives, <c>.Trash</c> and <c>.Trashes</c> on
/// macOS, and the freedesktop.org trash on Linux (<c>~/.local/share/Trash</c> or <c>$XDG_DATA_HOME/Trash</c>,
/// <c>.Trash-1000</c> and <c>.Trash/1000</c> at the top of other drives). Checked on every system, since a drive
/// formatted on one can be read on another.
/// </summary>
public static class TrashFolders
{
    public static bool Contains(string path) => Contains(path, OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()
        ? Environment.GetEnvironmentVariable("XDG_DATA_HOME")
        : null);

    /// <param name="dataHome">
    /// <c>XDG_DATA_HOME</c>, which moves the home trash on Linux; used only when it is an absolute path, as the
    /// freedesktop.org specification asks.
    /// </param>
    public static bool Contains(string path, string? dataHome)
    {
        if (dataHome is { Length: > 1 } && dataHome[0] == '/' &&
            path.StartsWith(dataHome.TrimEnd('/') + "/Trash/", StringComparison.Ordinal))
            return true;
        foreach (var part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || part.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase) ||
                part is ".Trash" or ".Trashes" || IsUserTrash(part))
                return true;
        }
        return path.Replace('\\', '/').Contains("/.local/share/Trash/", StringComparison.Ordinal);
    }

    // .Trash-$uid: the freedesktop.org name is followed by a user's number, nothing else (.Trash-backup is a folder).
    private static bool IsUserTrash(string part) =>
        part.Length > ".Trash-".Length && part.StartsWith(".Trash-", StringComparison.Ordinal) &&
        part.AsSpan(".Trash-".Length).IndexOfAnyExceptInRange('0', '9') < 0;
}
