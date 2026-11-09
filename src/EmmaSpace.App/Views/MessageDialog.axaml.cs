using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EmmaSpace.App.Views;

/// <summary>Which portrait of Emma a dialog shows on its left.</summary>
public enum DialogArt
{
    None,
    Question,
    Error,
}

public partial class MessageDialog : Window
{
    private static readonly Dictionary<DialogArt, Bitmap> s_portraits = [];

    public MessageDialog() : this("", "", "OK", null, false)
    {
    }

    /// <param name="art">Emma's portrait on the left; the dialog grows wider by its width.</param>
    public MessageDialog(string title, string message, string primaryText, string? secondaryText, bool destructive,
        DialogArt art = DialogArt.None)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        if (destructive && this.TryFindResource("EmmaDangerBrush", ActualThemeVariant, out var brush) && brush is IBrush b)
            PrimaryButton.Background = b;
        SecondaryButton.IsVisible = secondaryText is not null;
        SecondaryButton.Content = secondaryText;
        SecondaryButton.IsCancel = secondaryText is not null;
        if (art != DialogArt.None)
        {
            ArtPanel.Background = new ImageBrush(Portrait(art)) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
            ArtPanel.IsVisible = true;
            Width += ArtPanel.Width - 12;   // the text column keeps (almost) its width
            Body.Margin = new Thickness(4, 24, 28, 22);
        }
    }

    // Decoded once per run; dialogs are opened on the UI thread.
    private static Bitmap Portrait(DialogArt art)
    {
        if (!s_portraits.TryGetValue(art, out var bitmap))
        {
            var file = art == DialogArt.Error ? "dialog-emma-error.jpg" : "dialog-emma.jpg";
            using var stream = AssetLoader.Open(new Uri($"avares://EmmaSpace/Assets/{file}"));
            s_portraits[art] = bitmap = new Bitmap(stream);
        }
        return bitmap;
    }

    private void OnPrimary(object? sender, RoutedEventArgs e) => Close(true);

    private void OnSecondary(object? sender, RoutedEventArgs e) => Close(false);
}
