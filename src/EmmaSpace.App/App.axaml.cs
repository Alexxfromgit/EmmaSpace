using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EmmaSpace.App.Controls;
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
            // The saved theme applies before the first window shows, so it never flashes the system one.
            var settings = new SettingsService();
            SettingsViewModel.Apply(AppThemes.Parse(settings.Current.Theme));
            ColumnPicker.Settings = settings;
            var window = new MainWindow();
            _services = ConfigureServices(window, settings);
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            window.DataContext = viewModel;
            if (OperatingSystem.IsMacOS()) AddMacAppMenu(viewModel);
            desktop.MainWindow = window;
            // Snapshots double-clicked while this window is open arrive from a short-lived new process.
            var handoff = OperatingSystem.IsMacOS() ? null
                : SnapshotHandoff.Listen(received => Dispatcher.UIThread.Post(() => Offer(window, viewModel, received)));
            desktop.ShutdownRequested += (_, _) =>
            {
                handoff?.Dispose();
                viewModel.Dispose();
                _services.Dispose();
            };
            if (ElevationService.StartupScanPath(desktop.Args) is { } path)
                window.Opened += (_, _) => viewModel.StartScan(path);
            else if (SnapshotFileType.FindInArguments(desktop.Args) is { } snapshot)
                window.Opened += (_, _) => _ = viewModel.OfferSnapshotAsync(snapshot, ask: false); // double-clicked on Windows or Linux
            OpenSnapshotsFromFinder(window, viewModel);
            window.Opened += (_, _) => _ = viewModel.Updates.CheckOnStartupAsync();
            window.Opened += (_, _) => DesktopIntegration.StartOnLinux();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Brings the window forward and asks whether to open a snapshot double-clicked meanwhile. The view
    /// model checks the file off the UI thread and queues the question behind any earlier one.
    /// </summary>
    private static void Offer(Window window, MainWindowViewModel viewModel, string snapshot)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        _ = viewModel.OfferSnapshotAsync(snapshot);
    }

    // macOS hands a double-clicked document to the app as an "open files" event instead of on the
    // command line: once for the file(s) that launched it and again for every file double-clicked
    // while it runs. Only the first launch file opens directly; every other one is asked about first,
    // like on Windows and Linux, in the order Finder sent them (the view model skips non-snapshots).
    private void OpenSnapshotsFromFinder(Window window, MainWindowViewModel viewModel)
    {
        if (this.TryGetFeature<IActivatableLifetime>() is not { } activatable) return;
        // The launch file arrives right as the app starts; anything after this settling time was
        // double-clicked while it was already open.
        DateTime? openedAt = null;
        window.Opened += (_, _) => openedAt = DateTime.UtcNow;
        activatable.Activated += (_, e) =>
        {
            if (e is not FileActivatedEventArgs files) return;
            var paths = files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count == 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                var running = openedAt is { } opened && DateTime.UtcNow - opened > TimeSpan.FromSeconds(2);
                foreach (var path in paths)
                {
                    if (running || viewModel.HasTree) Offer(window, viewModel, path);
                    else _ = viewModel.OfferSnapshotAsync(path, ask: false); // asks anyway if it isn't the first
                }
            });
        };
    }

    // macOS keeps Settings in the app menu: Emma Space ▸ Settings… (⌘,). Avalonia adds the standard
    // Services, Hide and Quit items after the ones set here.
    private void AddMacAppMenu(MainWindowViewModel viewModel)
    {
        var settings = new NativeMenuItem("Settings…")
        {
            Command = viewModel.OpenSettingsCommand,
            Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
        };
        NativeMenu.SetMenu(this, new NativeMenu { Items = { settings } });
    }

    private static ServiceProvider ConfigureServices(MainWindow window, SettingsService settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScanner, DefaultScanner>();
        services.AddSingleton<IFileActions, FileActions>();
        services.AddSingleton(settings);
        services.AddSingleton<ElevationService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UpdateViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<IDialogService>(sp => new DialogService(window, sp.GetRequiredService<SettingsViewModel>));
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
