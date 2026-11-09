using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Scanning;

/// <summary>How typed text relates to a folder, as far as the text alone can tell.</summary>
public enum TypedPathMatch
{
    /// <summary>The text names another folder (or is not a valid path).</summary>
    Different,

    /// <summary>The text names the folder: same full path, ignoring a trailing separator and case where the system does.</summary>
    Same,

    /// <summary>
    /// The text names the folder once its surrounding spaces are dropped. Whether a scan drops them depends
    /// on the disk: if a folder whose name keeps the spaces exists, the text names that one instead.
    /// </summary>
    SameIfTrimmed,
}

public static class ScanPaths
{
    /// <summary>
    /// The folder a scan of typed text reads: the text as typed if that folder exists (on macOS and
    /// Linux a name may start or end with spaces), otherwise the text without surrounding spaces.
    /// Asks the disk, so keep it off the UI thread: a network share can be slow to answer.
    /// </summary>
    public static string FromTyped(string text) => Directory.Exists(text) ? text : text.Trim();

    /// <summary>
    /// How the typed text relates to <paramref name="root"/>, from the text alone: both are normalized
    /// the way the scanners do it (full path, "C:" means "C:\", no trailing separator) and compared with
    /// the app's path case rules. Never touches the disk, so it is safe in UI bindings.
    /// </summary>
    public static TypedPathMatch Match(string? typed, string root)
    {
        if (string.IsNullOrWhiteSpace(typed) || string.IsNullOrEmpty(root)) return TypedPathMatch.Different;
        string normalizedRoot;
        try
        {
            normalizedRoot = ManagedScanner.NormalizeRoot(root);
        }
        catch (ScanException)
        {
            return TypedPathMatch.Different;
        }
        if (NormalizesTo(typed, normalizedRoot)) return TypedPathMatch.Same;
        var trimmed = typed.Trim();
        return trimmed.Length != typed.Length && NormalizesTo(trimmed, normalizedRoot)
            ? TypedPathMatch.SameIfTrimmed
            : TypedPathMatch.Different;
    }

    /// <summary>
    /// Whether scanning the typed text would scan <paramref name="root"/> again: <see cref="Match"/>,
    /// with the disk deciding whether stray spaces are a typo. Asks the disk, so keep it off the UI thread.
    /// </summary>
    public static bool AreSame(string? typed, string root) => Match(typed, root) switch
    {
        TypedPathMatch.Same => true,
        TypedPathMatch.SameIfTrimmed => !Directory.Exists(typed),
        _ => false,
    };

    private static bool NormalizesTo(string text, string normalizedRoot)
    {
        try
        {
            return string.Equals(ManagedScanner.NormalizeRoot(text), normalizedRoot, FileTree.PathComparison);
        }
        catch (ScanException)
        {
            return false; // not a valid path, so not the one on screen
        }
    }
}
