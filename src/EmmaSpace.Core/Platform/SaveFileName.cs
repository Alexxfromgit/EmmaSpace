namespace EmmaSpace.Core.Platform;

/// <summary>Tidies the path a system save dialog returns, whose extension handling differs per platform.</summary>
public static class SaveFileName
{
    /// <summary>
    /// The path ending in <paramref name="extension"/> exactly once: repeats of it at the end are collapsed
    /// (macOS's save panel adds the extension again to a suggested name that already has it, giving
    /// "scan.emmasnap.emmasnap"), and it is appended if missing (e.g. "disk.old" typed on Linux, where the
    /// dialog keeps any extension it is given). Case is ignored, but the case the user typed is kept.
    /// </summary>
    public static string WithSingleExtension(string path, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        if (!extension.StartsWith('.')) extension = "." + extension;

        var nameStart = path.Length - Path.GetFileName(path.AsSpan()).Length;
        var end = path.Length;
        // Keep at least one character of name before the extension, so ".emmasnap.emmasnap" stays a name.
        while (end - extension.Length > nameStart + extension.Length &&
               path.AsSpan(0, end).EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
               path.AsSpan(0, end - extension.Length).EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            end -= extension.Length;

        return path.AsSpan(0, end).EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? path[..end]
            : path + extension;
    }

    /// <summary>The suggested file name to hand the save dialog: without the extension on macOS, whose panel adds it.</summary>
    public static string Suggestion(string fileName, string extension) =>
        OperatingSystem.IsMacOS() && fileName.Length > extension.Length &&
        fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
}
