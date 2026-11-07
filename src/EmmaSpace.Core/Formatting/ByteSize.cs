using System.Globalization;

namespace EmmaSpace.Core.Formatting;

public static class ByteSize
{
    private static readonly string[] s_units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats with binary (1024) multiples, as Windows Explorer and Finder's "binary" mode do.</summary>
    public static string Format(ulong bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < s_units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + s_units[unit];
    }

    public static string Format(long bytes) => bytes < 0 ? "-" + Format((ulong)(-bytes)) : Format((ulong)bytes);

    public static string FormatDelta(long bytes) => bytes > 0 ? "+" + Format(bytes) : Format(bytes);
}
