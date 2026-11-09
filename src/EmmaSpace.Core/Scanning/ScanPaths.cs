using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Scanning;

public static class ScanPaths
{
    /// <summary>
    /// The folder a scan of typed text reads: the text as typed if that folder exists (on macOS and
    /// Linux a name may start or end with spaces), otherwise the text without surrounding spaces.
    /// </summary>
    public static string FromTyped(string text) => Directory.Exists(text) ? text : text.Trim();

    /// <summary>
    /// Whether scanning the typed text would scan <paramref name="root"/> again: the folder
    /// <see cref="FromTyped"/> picks, normalized the way the scanners do it (full path, "C:" means
    /// "C:\", no trailing separator), compared with the app's path case rules.
    /// </summary>
    public static bool AreSame(string? typed, string root)
    {
        if (string.IsNullOrWhiteSpace(typed) || string.IsNullOrEmpty(root)) return false;
        try
        {
            var normalizedRoot = ManagedScanner.NormalizeRoot(root);
            if (string.Equals(ManagedScanner.NormalizeRoot(typed), normalizedRoot, FileTree.PathComparison)) return true;
            // Only ask the disk when the text has stray spaces that a scan might keep.
            var trimmed = typed.Trim();
            return trimmed.Length != typed.Length
                && string.Equals(ManagedScanner.NormalizeRoot(trimmed), normalizedRoot, FileTree.PathComparison)
                && FromTyped(typed) == trimmed;
        }
        catch (ScanException)
        {
            return false; // not a valid path, so not the one on screen
        }
    }
}
