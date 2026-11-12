using System.Globalization;

namespace EmmaSpace.Core.Formatting;

public static class ByteSize
{
    private static readonly string[] s_units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Whether <see cref="Format(ulong)"/> uses decimal multiples (1 KB = 1000 bytes), as Finder and System Settings
    /// have since Mac OS X 10.6, instead of binary ones (1 KB = 1024 bytes), as Windows Explorer does. Decimal on
    /// macOS, so sizes match what the system shows next to them; binary elsewhere.
    /// </summary>
    public static bool UsesDecimalUnits { get; } = OperatingSystem.IsMacOS();

    /// <summary>Formats in this system's usual units (see <see cref="UsesDecimalUnits"/>).</summary>
    public static string Format(ulong bytes) => Format(bytes, UsesDecimalUnits);

    /// <summary>Formats with decimal (1000) or binary (1024) multiples, labelled KB, MB, GB… either way.</summary>
    public static string Format(ulong bytes, bool decimalUnits)
    {
        var step = decimalUnits ? 1000.0 : 1024.0;
        if (bytes < step) return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        double value = bytes;
        var unit = 0;
        while (value >= step && unit < s_units.Length - 1)
        {
            value /= step;
            unit++;
        }
        var rounded = Rounded(value);
        // Rounding can reach the next unit (999.96 MB would read "1000.0 MB"): show it in that unit instead.
        if (unit < s_units.Length - 1 && rounded >= step)
        {
            value /= step;
            unit++;
            rounded = Rounded(value);
        }
        return rounded.ToString(Pattern(rounded), CultureInfo.CurrentCulture) + " " + s_units[unit];

        // At least one decimal, as Finder shows "121.8 GB": without it sizes of 100 and up lose up to 1%.
        static string Pattern(double value) => value >= 10 ? "0.0" : "0.00";

        // The value as it will be shown, so 9.996 counts as 10.0 (one decimal) and 999.96 as 1000.
        static double Rounded(double value) =>
            double.Parse(value.ToString(Pattern(value), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public static string Format(long bytes) => bytes < 0 ? "-" + Format((ulong)(-bytes)) : Format((ulong)bytes);

    public static string FormatDelta(long bytes) => bytes > 0 ? "+" + Format(bytes) : Format(bytes);
}
