using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EmmaSpace.App.Services;

namespace EmmaSpace.App.Controls;

/// <summary>
/// Right-clicking a table's header row opens a menu that shows or hides its columns, and the choice is
/// remembered per table under <see cref="KeyProperty"/>. The first column names the row, so it always stays.
/// </summary>
public static class ColumnPicker
{
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<TableView, string?>("Key", typeof(ColumnPicker));

    /// <summary>Where the choices are kept; set once at startup, before the first table loads.</summary>
    public static SettingsService? Settings { get; set; }

    static ColumnPicker() => KeyProperty.Changed.AddClassHandler<TableView>(OnKeyChanged);

    public static string? GetKey(TableView table) => table.GetValue(KeyProperty);
    public static void SetKey(TableView table, string? value) => table.SetValue(KeyProperty, value);

    private static void OnKeyChanged(TableView table, AvaloniaPropertyChangedEventArgs e)
    {
        table.ContextRequested -= OnContextRequested;
        table.Loaded -= OnLoaded;
        if (e.NewValue is null) return;
        table.ContextRequested += OnContextRequested;
        // XAML sets the key before it fills Columns, so the saved choice applies once the table loads.
        table.Loaded += OnLoaded;
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TableView table || GetKey(table) is not { } key) return;
        var hidden = Settings?.Current.HiddenColumns?.GetValueOrDefault(key);
        for (var i = 1; i < table.Columns.Count; i++)
            table.Columns[i].IsVisible = hidden is null || !hidden.Contains(Name(table.Columns[i]));
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TableView table || e.Source is not Visual source) return;
        if (source.FindAncestorOfType<TableViewColumnHeadersPresenter>(includeSelf: true) is null) return; // a row: its own menu
        e.Handled = true;

        var menu = new ContextMenu();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var item = new MenuItem
            {
                Header = Name(column),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                IsEnabled = i > 0,
            };
            item.Click += (_, _) =>
            {
                column.IsVisible = !column.IsVisible;
                Save(table);
            };
            menu.Items.Add(item);
        }
        menu.Open(table);
    }

    private static void Save(TableView table)
    {
        if (Settings is null || GetKey(table) is not { } key) return;
        var hidden = table.Columns.Where(c => !c.IsVisible).Select(Name).ToList();
        var all = Settings.Current.HiddenColumns ??= [];
        if (hidden.Count == 0) all.Remove(key);
        else all[key] = hidden;
        Settings.Save();
    }

    private static string Name(TableViewColumn column) => column.Header?.ToString() ?? "";
}
