using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EmmaSpace.App.Services;
using EmmaSpace.App.ViewModels;
using EmmaSpace.App.Views;
using EmmaSpace.Core.Platform;
using EmmaSpace.Core.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace EmmaSpace.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            _services = ConfigureServices(window);
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                viewModel.Dispose();
                _services.Dispose();
            };
            if (ElevationService.StartupScanPath(desktop.Args) is { } path)
                window.Opened += (_, _) => viewModel.StartScan(path);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices(MainWindow window)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScanner, DefaultScanner>();
        services.AddSingleton<IFileActions, FileActions>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ElevationService>();
        services.AddSingleton<IDialogService>(_ => new DialogService(window));
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
