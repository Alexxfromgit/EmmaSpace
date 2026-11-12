using System.Globalization;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Tests;

public sealed class VolumeSpaceTests
{
    [Theory]
    [InlineData(999UL, true, "999 B")]
    [InlineData(1_000UL, true, "1.00 KB")]
    [InlineData(1_000UL, false, "1000 B")]
    [InlineData(1_024UL, false, "1.00 KB")]
    // The Mac in the bug report: Finder said 994.7 GB and 61.3 GB, the app 926 GB and 57.1 GB in 1024s.
    [InlineData(994_662_584_320UL, true, "994.7 GB")]
    [InlineData(994_662_584_320UL, false, "926.4 GB")]
    [InlineData(61_289_947_136UL, true, "61.3 GB")]
    [InlineData(61_289_947_136UL, false, "57.1 GB")]
    [InlineData(2_500_000_000_000UL, true, "2.50 TB")]
    [InlineData(121_780_685_924UL, true, "121.8 GB")]
    [InlineData(999_949_999UL, true, "999.9 MB")]
    // Rounding up to the next unit shows that unit, not "1000.0 MB" or "1024.0 KB".
    [InlineData(999_950_000UL, true, "1.00 GB")]
    [InlineData(999_996UL, true, "1.00 MB")]
    [InlineData(1_048_525UL, false, "1.00 MB")]
    [InlineData(1_048_524UL, false, "1023.9 KB")]
    [InlineData(9_996UL, true, "10.0 KB")]
    [InlineData(ulong.MaxValue, true, "18446.7 PB")]
    public void Sizes_use_decimal_or_binary_multiples(ulong bytes, bool decimalUnits, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, ByteSize.Format(bytes, decimalUnits));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Units_follow_the_system_file_manager()
    {
        // Finder and System Settings count in 1000s; Explorer (and the Linux default here) in 1024s.
        Assert.Equal(OperatingSystem.IsMacOS(), ByteSize.UsesDecimalUnits);
        Assert.Equal(ByteSize.Format(1_000_000UL, ByteSize.UsesDecimalUnits), ByteSize.Format(1_000_000UL));
    }

    [Fact]
    public void Purgeable_space_is_what_is_available_beyond_the_free_space()
    {
        var space = new VolumeSpace(Total: 1000, Free: 60, Available: 120);
        Assert.Equal(60UL, space.Purgeable);
        Assert.Equal(880UL, space.Used);
        Assert.Equal(940UL, space.UsedWithPurgeable);
        Assert.Equal(0UL, new VolumeSpace(1000, 60, 60).Purgeable);
        // Figures that don't add up never wrap around.
        var odd = new VolumeSpace(Total: 100, Free: 150, Available: 120);
        Assert.Equal(0UL, odd.Purgeable);
        Assert.Equal(0UL, odd.Used);
        Assert.Equal(0UL, odd.UsedWithPurgeable);
    }

    private static string Decimal(ulong bytes) => ByteSize.Format(bytes, decimalUnits: true);

    [Fact]
    public void Drive_cards_say_available_only_for_the_systems_own_figure()
    {
        var mac = new VolumeSpace(994_662_584_320, 61_289_947_136, 121_780_685_924);
        Assert.Equal($"{Decimal(121_780_685_924)} available of {Decimal(994_662_584_320)}", mac.Describe(true, Decimal));
        Assert.Equal($"incl. {Decimal(60_490_738_788)} purgeable", mac.DescribePurgeable(Decimal));

        // Little purgeable data: still "available" (the figure Finder shows), just without the extra line.
        var little = new VolumeSpace(1_000_000_000_000, 50_000_000_000, 50_050_000_000);
        Assert.StartsWith(Decimal(50_050_000_000) + " available of", little.Describe(true, Decimal));
        Assert.Equal($"less than {Decimal(100_000_000)} purgeable", little.DescribePurgeable(Decimal));

        // DriveInfo's figure (Windows, Linux, or macOS when the system's can't be had) is plain free space.
        var plain = new VolumeSpace(1_000_000_000_000, 50_000_000_000, 50_000_000_000);
        Assert.StartsWith(Decimal(50_000_000_000) + " free of", plain.Describe(false, Decimal));
        Assert.Equal("no purgeable data", plain.DescribePurgeable(Decimal));
        Assert.Equal("", default(VolumeSpace).Describe(true, Decimal));
    }

    [Fact]
    public void Volume_space_is_reported_on_macOS_only()
    {
        Assert.SkipUnless(NativeEngine.IsAvailable, "Volume space needs the native engine.");
        using var temp = new TempTree();
        var space = StorageInfo.GetSpace(temp.Root);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Null(space);
            return;
        }
        Assert.NotNull(space);
        Assert.True(space.Value.Total > 0);
        Assert.InRange(space.Value.Free, 0UL, space.Value.Total);
        Assert.InRange(space.Value.Available, space.Value.Free, space.Value.Total);
        Assert.Null(StorageInfo.GetSpace(""));
        Assert.Null(StorageInfo.GetSpace(Path.Combine(temp.Root, "does-not-exist", "x")));
    }

    [Fact]
    public void The_purgeable_explanation_names_both_figures()
    {
        var text = MacStorage.Explanation("the startup disk", 61_289_947_136, 60_490_738_788, Decimal);
        Assert.Contains(Decimal(61_289_947_136) + " of the startup disk is empty", text);
        Assert.Contains("Another " + Decimal(60_490_738_788) + " is purgeable", text);
        Assert.Contains("Time Machine snapshots are not files", text);
        Assert.StartsWith("Available space on Backup includes " + Decimal(60_490_738_788),
            MacStorage.Note("Backup", 60_490_738_788, Decimal));
    }
}
