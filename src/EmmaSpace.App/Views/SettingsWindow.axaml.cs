using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EmmaSpace.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    /// <summary>
    /// Narrows the window to the working area of <paramref name="owner"/>'s screen when it is wider than that (it can't
    /// be resized). Call before showing it, so it is centred at the width it will have.
    /// </summary>
    public SettingsWindow FitToScreenOf(Window owner)
    {
        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
        {
            var available = screen.WorkingArea.Width / screen.Scaling - 32;
            if (available > 0 && Width > available) Width = available;
        }
        return this;
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
