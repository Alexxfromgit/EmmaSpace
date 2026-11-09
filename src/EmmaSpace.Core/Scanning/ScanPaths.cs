namespace EmmaSpace.Core.Scanning;

public static class ScanPaths
{
    /// <summary>
    /// Whether scanning the typed text would scan <paramref name="root"/> again. Both are normalized the
    /// way the scanners do it (full path, "C:" means "C:\", no trailing separator), surrounding spaces
    /// are ignored as the UI trims them, and so is case except on Linux.
    /// </summary>
    public static bool AreSame(string? typed, string root)
    {
        if (string.IsNullOrWhiteSpace(typed) || string.IsNullOrEmpty(root)) return false;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        try
        {
            var normalizedRoot = ManagedScanner.NormalizeRoot(root);
            return string.Equals(ManagedScanner.NormalizeRoot(typed), normalizedRoot, comparison)
                || string.Equals(ManagedScanner.NormalizeRoot(typed.Trim()), normalizedRoot, comparison);
        }
        catch (ScanException)
        {
            return false; // not a valid path, so not the one on screen
        }
    }
}
