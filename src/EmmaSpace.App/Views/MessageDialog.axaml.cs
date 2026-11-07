using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace EmmaSpace.App.Views;

public partial class MessageDialog : Window
{
    public MessageDialog() : this("", "", "OK", null, false)
    {
    }

    public MessageDialog(string title, string message, string primaryText, string? secondaryText, bool destructive)
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
    }

    private void OnPrimary(object? sender, RoutedEventArgs e) => Close(true);

    private void OnSecondary(object? sender, RoutedEventArgs e) => Close(false);
}
