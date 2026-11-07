using Avalonia.Media;
using Avalonia.Media.Immutable;
using EmmaSpace.Core.Analysis;

namespace EmmaSpace.App.Services;

/// <summary>One colour per file category, shared by the treemap, charts and lists.</summary>
public static class CategoryPalette
{
    private static readonly Color[] s_colors = BuildColors();
    private static readonly IImmutableSolidColorBrush[] s_brushes = s_colors.Select(c => (IImmutableSolidColorBrush)new ImmutableSolidColorBrush(c)).ToArray();

    public static readonly Color Folder = Color.Parse("#5B5873");

    public static Color ColorOf(FileCategory category) => s_colors[(int)category];

    public static IImmutableSolidColorBrush BrushOf(FileCategory category) => s_brushes[(int)category];

    public static IImmutableSolidColorBrush FolderBrush { get; } = new ImmutableSolidColorBrush(Folder);

    private static Color[] BuildColors()
    {
        var colors = new Color[Enum.GetValues<FileCategory>().Length];
        colors[(int)FileCategory.Other] = Color.Parse("#8A8FA3");
        colors[(int)FileCategory.Video] = Color.Parse("#8B5CF6");
        colors[(int)FileCategory.Audio] = Color.Parse("#EC4899");
        colors[(int)FileCategory.Images] = Color.Parse("#F59E0B");
        colors[(int)FileCategory.Documents] = Color.Parse("#3B82F6");
        colors[(int)FileCategory.Archives] = Color.Parse("#F97316");
        colors[(int)FileCategory.Code] = Color.Parse("#14B8A6");
        colors[(int)FileCategory.Applications] = Color.Parse("#22C55E");
        colors[(int)FileCategory.System] = Color.Parse("#64748B");
        colors[(int)FileCategory.Data] = Color.Parse("#06B6D4");
        return colors;
    }
}
