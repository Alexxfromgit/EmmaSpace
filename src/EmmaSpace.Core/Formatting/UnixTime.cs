namespace EmmaSpace.Core.Formatting;

/// <summary>Safe conversion of the scan's Unix-millisecond timestamps (0 = unknown).</summary>
public static class UnixTime
{
    /// <summary>The range <see cref="DateTimeOffset"/> can represent: years 0001 to 9999.</summary>
    public const long MinMs = -62_135_596_800_000;
    public const long MaxMs = 253_402_300_799_999;

    public static bool IsValid(long ms) => ms is >= MinMs and <= MaxMs;

    /// <summary>The time, or null when it is unknown (0) or outside what can be displayed.</summary>
    public static DateTimeOffset? ToDateTimeOffset(long ms) =>
        ms == 0 || !IsValid(ms) ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
