using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
            else if (SnapshotFileType.FindInArguments(desktop.Args) is { } snapshot)
                window.Opened += (_, _) => _ = viewModel.OpenSnapshotFileAsync(snapshot); // double-clicked on Windows or Linux
            OpenSnapshotsFromFinder(viewModel);
            window.Opened += (_, _) => _ = viewModel.Updates.CheckOnStartupAsync();
            window.Opened += (_, _) => DesktopIntegration.StartOnLinux();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // macOS hands a double-clicked document to the running app as an "open files" event, both at
    // launch and while it runs, instead of on the command line.
    private void OpenSnapshotsFromFinder(MainWindowViewModel viewModel)
    {
        if (this.TryGetFeature<IActivatableLifetime>() is not { } activatable) return;
        activatable.Activated += (_, e) =>
        {
            if (e is not FileActivatedEventArgs files) return;
            var snapshot = files.Files.Select(f => f.TryGetLocalPath()).FirstOrDefault(SnapshotFileType.IsSnapshot);
            if (snapshot is not null) Dispatcher.UIThread.Post(() => _ = viewModel.OpenSnapshotFileAsync(snapshot));
        };
    }

    private static ServiceProvider ConfigureServices(MainWindow window)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScanner, DefaultScanner>();
        services.AddSingleton<IFileActions, FileActions>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ElevationService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<IDialogService>(_ => new DialogService(window));
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
