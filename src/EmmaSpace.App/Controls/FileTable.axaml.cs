using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EmmaSpace.App.Controls;

public partial class FileTable : TableView
{
    public FileTable() => AvaloniaXamlLoader.Load(this);

    // Use TableView's control theme rather than looking for one keyed on this subclass.
    protected override Type StyleKeyOverride => typeof(TableView);
}
