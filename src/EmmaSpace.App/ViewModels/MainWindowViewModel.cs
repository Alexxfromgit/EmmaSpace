using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Export;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Platform;
using EmmaSpace.Core.Scanning;
using EmmaSpace.Core.Snapshots;

namespace EmmaSpace.App.ViewModels;

public sealed record BreadcrumbItem(int Node, string Name);

public sealed partial class MainWindowViewModel : ObservableObject, INodeActions, IDisposable
{
    private readonly IScanner _scanner;
    private readonly IFileActions _fileActions;
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settings;
    private readonly ElevationService _elevation;
    private CancellationTokenSource? _scanCts;
    // The scanner that produced the results on screen, when it was not the default (the Linux fast scan), so that
    // rescanning them reads the same way instead of silently walking as the user.
    private IScanner? _resultsScanner;
    private int _loadGeneration; // bumped by every scan and snapshot load; the newest one wins
    private readonly SemaphoreSlim _snapshotOffers = new(1, 1);
    private Task _snapshotLoad = Task.CompletedTask; // the latest snapshot load, which the next offer waits for
    private bool _snapshotHandedOver; // a snapshot came from outside already, so later ones are asked about
    private bool _syncingSelection;
    private bool _privacyHintDismissed; // for this session

    public MainWindowViewModel(IScanner scanner, IFileActions fileActions, IDialogService dialogs, SettingsService settings,
        ElevationService elevation, UpdateViewModel updates)
    {
        _scanner = scanner;
        Updates = updates;
        _elevation = elevation;
        _fileActions = fileActions;
        _dialogs = dialogs;
        _settings = settings;
        FolderTree = new FolderTreeViewModel(this);
        FolderTree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderTreeViewModel.SelectedRow)) OnTreeRowSelected();
        };
        FileTypes = new FileTypesViewModel(this);
        Largest = new LargestFilesViewModel(this);
        Duplicates = new DuplicatesViewModel(this, settings);
        Cleanup = new CleanupViewModel(this, settings);
        Compare = new CompareViewModel(this, dialogs);
        PathText = settings.Current.LastPath ?? "";
        RefreshDrives();
    }

    public FolderTreeViewModel FolderTree { get; }
    public FileTypesViewModel FileTypes { get; }
    public LargestFilesViewModel Largest { get; }
    public DuplicatesViewModel Duplicates { get; }
    public CleanupViewModel Cleanup { get; }
    public CompareViewModel Compare { get; }
    public UpdateViewModel Updates { get; }

    public static string SettingsTip { get; } = OperatingSystem.IsMacOS() ? "Settings (⌘,)" : "Settings (Ctrl+,)";

    /// <summary>Settings button, Ctrl+, and on macOS Emma Space ▸ Settings… (⌘,).</summary>
    [RelayCommand]
    private Task OpenSettingsAsync() => _dialogs.ShowSettingsAsync();

    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public string EngineText => !NativeEngine.IsAvailable ? "Managed engine"
        : _elevation.IsElevated ? "Native engine · administrator" : "Native engine";

    public static string PathPlaceholder { get; } = OperatingSystem.IsWindows()
        ? @"Drive or folder to analyze, e.g. C:\"
        : "Drive or folder to analyze, e.g. " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Offer the fast scan: the NTFS master file table (restart as administrator) or ext4 metadata (root helper).</summary>
    public bool CanElevate => _elevation.CanElevate;

    public string FastScanTip => _elevation.ScansThroughHelper
        ? "Read the drive as administrator; on ext4 its inode tables are read directly (much faster)"
        : "Restart as administrator and read the NTFS master file table directly (much faster)";
    public bool CanShowProperties => _fileActions.CanShowProperties;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTree), nameof(IsHome), nameof(WindowTitle), nameof(IsRescan), nameof(ScanLabel), nameof(ScanTip))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    public partial FileTree? Tree { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RescanCommand))]
    public partial bool IsScanning { get; private set; }

    public bool HasTree => Tree is not null;
    public bool IsHome => Tree is null && !IsScanning;
    public string WindowTitle => Tree is null ? "Emma Space" : $"Emma Space — {Tree.RootPath}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRescan), nameof(ScanLabel), nameof(ScanTip))]
    public partial string PathText { get; set; }

    /// <summary>What a rescan reads: the scanned folder, or for a snapshot the folder it was taken of.</summary>
    private string? ResultsPath => Tree is null ? null : Tree.Info.Source == ScanSource.Snapshot ? Tree.Info.RootPath : Tree.RootPath;

    /// <summary>
    /// True when the path box names the results on screen. The one scan button then reads "Rescan";
    /// once another path is typed it reads "Analyze" again. Bindings read this, so it never asks the
    /// disk itself: stray spaces around the results' path count as a typo unless the background check
    /// (<see cref="CheckTypedFolder"/>) found a folder whose name keeps them.
    /// </summary>
    public bool IsRescan => ResultsPath is { } results && ScanPaths.Match(PathText, results) switch
    {
        TypedPathMatch.Same => true,
        TypedPathMatch.SameIfTrimmed => !(_checkedText == PathText && _checkedTextExists),
        _ => false,
    };

    // The last typed text checked on disk by CheckTypedFolder, and whether a folder of exactly that name exists.
    private string? _checkedText;
    private bool _checkedTextExists;

    partial void OnPathTextChanged(string value) => CheckTypedFolder();

    partial void OnTreeChanged(FileTree? value) => CheckTypedFolder();

    /// <summary>
    /// When the path box holds the results' path with stray spaces, checks in the background whether a
    /// folder keeps those spaces in its name (legal on macOS and Linux), then updates the scan button.
    /// </summary>
    private void CheckTypedFolder()
    {
        var text = PathText;
        if (ResultsPath is not { } results || ScanPaths.Match(text, results) != TypedPathMatch.SameIfTrimmed) return;
        _ = CheckTypedFolderAsync(text);
    }

    private async Task CheckTypedFolderAsync(string text)
    {
        var exists = await Task.Run(() => Directory.Exists(text));
        if (PathText != text) return; // typed on meanwhile; the new text has its own check
        _checkedText = text;
        _checkedTextExists = exists;
        OnPropertyChanged(nameof(IsRescan));
        OnPropertyChanged(nameof(ScanLabel));
        OnPropertyChanged(nameof(ScanTip));
    }

    public string ScanLabel => IsRescan ? "Rescan" : "Analyze";

    public static string RescanShortcut { get; } = OperatingSystem.IsMacOS() ? "⌘R" : "F5";

    public string ScanTip => IsRescan
        ? $"Scan {ResultsPath} again to see its current state ({RescanShortcut})"
        : "Map the drive or folder in the box (Enter)";

    /// <summary>Bumped whenever the tree changes in place, so the treemap re-lays itself out.</summary>
    [ObservableProperty]
    public partial int TreeRevision { get; private set; }

    [ObservableProperty]
    public partial int SelectedNode { get; set; } = FileTree.None;

    [ObservableProperty]
    public partial int FocusNode { get; set; } = FileTree.Root;

    [ObservableProperty]
    public partial int HoveredNode { get; set; } = FileTree.None;

    [ObservableProperty]
    public partial string HoverText { get; private set; } = "";

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    // Scan progress
    private const string MappingHeading = "Mapping your files…";

    /// <summary>"Mapping your files…", or the wait for the administrator password before a Linux fast scan.</summary>
    [ObservableProperty]
    public partial string ProgressHeading { get; private set; } = MappingHeading;

    [ObservableProperty]
    public partial string ProgressFiles { get; private set; } = "";

    [ObservableProperty]
    public partial string ProgressBytes { get; private set; } = "";

    [ObservableProperty]
    public partial string ProgressPath { get; private set; } = "";

    [ObservableProperty]
    public partial string ProgressElapsed { get; private set; } = "";

    // Status bar
    [ObservableProperty]
    public partial string TotalText { get; private set; } = "";

    [ObservableProperty]
    public partial string ScanInfoText { get; private set; } = "";

    [ObservableProperty]
    public partial string? SnapshotBanner { get; private set; }

    /// <summary>macOS: the scan missed private folders because Emma Space has no Full Disk Access.</summary>
    [ObservableProperty]
    public partial bool ShowPrivacyHint { get; private set; }

    public static string PrivacyHintText { get; } =
        "Some folders could not be read. macOS keeps Mail, Messages, Safari and other apps' data private unless Emma Space " +
        "has Full Disk Access. To include them, add Emma Space under Full Disk Access in System Settings (with + if it " +
        "isn't listed), turn it on, then quit and reopen Emma Space and scan again.";

    [RelayCommand]
    private async Task OpenPrivacySettingsAsync()
    {
        try
        {
            await Task.Run(MacPrivacy.OpenFullDiskAccessSettings);
        }
        catch (IOException ex)
        {
            await _dialogs.ShowErrorAsync("Full Disk Access", ex.Message);
        }
    }

    [RelayCommand]
    private void DismissPrivacyHint()
    {
        _privacyHintDismissed = true;
        ShowPrivacyHint = false;
    }

    private static bool NeedsPrivacyHint(FileTree? tree) =>
        OperatingSystem.IsMacOS() && tree is { Info.Source: not ScanSource.Snapshot, Info.Errors: > 0 } &&
        MacPrivacy.MayCoverProtectedData(tree.Info.RootPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) &&
        MacPrivacy.HasFullDiskAccess() == false;

    [RelayCommand]
    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveViewModel.Discover()) Drives.Add(d);
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var folder = await _dialogs.PickFolderAsync();
        if (folder is null) return;
        PathText = folder;
        await ScanPathAsync(folder);
    }

    [RelayCommand]
    private Task ScanDriveAsync(DriveViewModel drive)
    {
        PathText = drive.Path;
        return ScanPathAsync(drive.Path);
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var text = PathText;
        if (string.IsNullOrWhiteSpace(text))
        {
            await BrowseAsync();
            return;
        }
        var results = ResultsPath;
        var resultsScanner = _resultsScanner; // with `results`: another scan may finish during the await below
        var match = results is null ? TypedPathMatch.Different : ScanPaths.Match(text, results);
        // Rescanning reads the results' own path exactly as it was scanned, so a folder that is gone is
        // reported as gone instead of quietly scanning a look-alike (e.g. "reports" for "reports ").
        if (match == TypedPathMatch.Same)
        {
            await ScanPathAsync(results!, resultsScanner);
            return;
        }
        // Whether stray spaces are part of a folder's name is a question for the disk, which can be slow
        // to answer for a network share, so it is asked off the UI thread.
        var path = await Task.Run(() => TypedPath(text));
        // Back to the results' own folder (the typed spaces were stray): rescan it the way it was scanned.
        var rescan = match == TypedPathMatch.SameIfTrimmed && path != text;
        await ScanPathAsync(rescan ? results! : path, rescan ? resultsScanner : null);
    }

    // Stray spaces around typed text are dropped, unless the folder's name really has them
    // (legal on macOS and Linux). IsRescan applies the same rule to the button label.
    private static string TypedPath(string text) => ScanPaths.FromTyped(text);

    private bool CanRescan() => !IsScanning && Tree is not null;

    /// <summary>Scans the results on screen again (F5 / ⌘R), whatever the path box holds.</summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync()
    {
        if (ResultsPath is not { } path) return Task.CompletedTask;
        PathText = path;
        return ScanPathAsync(path, _resultsScanner);
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>Starts a scan right away, e.g. for a path given on the command line.</summary>
    public void StartScan(string path)
    {
        PathText = path;
        _ = ScanPathAsync(path);
    }

    [RelayCommand]
    private async Task FastScanAsync()
    {
        // Both the typed-path check and the mount lookup (realpath, statx) can block on a sleeping disk or a dead
        // network mount, so they run off the UI thread.
        // The window stays usable meanwhile, so if a scan starts or other results appear before they answer, this
        // click no longer applies and is dropped.
        var resultsRoot = Tree?.RootPath;
        var typed = PathText;
        bool Stale() => IsScanning || Tree?.RootPath != resultsRoot;
        var path = resultsRoot ?? (string.IsNullOrWhiteSpace(typed) ? null : await Task.Run(() => TypedPath(typed)));
        if (Stale()) return;
        if (_elevation.ScansThroughHelper)
        {
            // The fast scan reads a whole ext2/3/4 file system at once. Elsewhere running as root gains nothing, so
            // say so instead of asking for a password; for a subfolder, offer the file system that holds it.
            path ??= "/";
            const string password = "\n\nYour system will ask for your password. Emma Space itself keeps running as you.";
            var lookup = path;
            MountedFileSystem? mount;
            var finding = Task.Run(() => LinuxMounts.Find(lookup));
            _ = finding.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // if we stop waiting
            try
            {
                // A dead network mount can keep the lookup waiting for good; then go on without knowing the file system.
                mount = await finding.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                mount = null;
            }
            if (Stale()) return;
            if (mount is { IsExt: false })
            {
                await _dialogs.ShowErrorAsync("Fast scan",
                    $"Fast scan reads ext2, ext3 and ext4 file systems directly. {path} is on {mount.Type}, " +
                    "where it would be no faster than Analyze.");
                return;
            }
            if (mount is { IsMountPoint: false })
            {
                if (!await _dialogs.ConfirmAsync("Fast scan",
                        $"Fast scan reads a whole file system at once, straight from its inode tables. {path} is on the " +
                        $"{mount.Type} file system at {mount.MountPoint}, so Emma Space will scan all of {mount.MountPoint}, " +
                        "usually many times faster than reading folder by folder." + password,
                        $"Scan {mount.MountPoint}"))
                    return;
                path = mount.MountPoint;
            }
            else if (!await _dialogs.ConfirmAsync("Fast scan",
                         $"Emma Space will read {path} straight from the file system's inode tables, which is usually " +
                         "many times faster than reading folder by folder." + password,
                         "Continue"))
            {
                return;
            }
            PathText = path;
            await ScanPathAsync(path, _elevation.FastScanner);
            return;
        }

        if (!await _dialogs.ConfirmAsync("Fast scan",
                "Emma Space will restart as administrator so it can read the drive's master file table directly. " +
                "This is usually many times faster than reading folder by folder.\n\nWindows will ask for permission.",
                "Restart as administrator"))
            return;
        _elevation.TryRestartElevated(path);
    }

    [RelayCommand]
    private void GoHome()
    {
        if (IsScanning) return;
        SetTree(null);
        RefreshDrives();
    }

    private async Task ScanPathAsync(string path, IScanner? scanner = null)
    {
        if (IsScanning) return;
        IsScanning = true;
        _loadGeneration++; // a snapshot still loading must not replace this scan's result
        _scanCts = new CancellationTokenSource();
        ProgressFiles = ProgressBytes = ProgressPath = ProgressElapsed = "";
        ProgressHeading = MappingHeading;
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Phase == ScanPhase.WaitingForAuthorization)
            {
                ProgressHeading = "Waiting for administrator password…";
                ProgressFiles = "Enter it in the system's password dialog to start the fast scan.";
                return;
            }
            if (ProgressHeading != MappingHeading)
            {
                ProgressHeading = MappingHeading;
                stopwatch.Restart(); // the time shown is the scan's, not the time spent typing the password
            }
            ProgressFiles = $"{p.Files:N0} files · {p.Directories:N0} folders";
            ProgressBytes = ByteSize.Format(p.Bytes);
            ProgressPath = p.Phase == ScanPhase.Finalizing ? "Sorting and totalling…" : p.CurrentPath;
            ProgressElapsed = stopwatch.Elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        });

        try
        {
            var tree = await (scanner ?? _scanner).ScanAsync(path, new ScanOptions(), progress, _scanCts.Token);
            _settings.Current.LastPath = path;
            _settings.Save();
            SetTree(tree);
            _resultsScanner = scanner;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ScanException ex)
        {
            await _dialogs.ShowErrorAsync("Scan failed", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _dialogs.ShowErrorAsync("Scan failed", ex.Message);
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            IsScanning = false;
        }
    }

    private void SetTree(FileTree? tree)
    {
        var old = Tree;
        _resultsScanner = null;
        foreach (var vm in AnalysisViewModels) vm.Cancel();
        if (old is not null) old.NodeRemoved -= OnNodeRemoved;

        Tree = tree;
        SelectedNode = FileTree.None;
        FocusNode = FileTree.Root;
        HoveredNode = FileTree.None;
        if (tree is not null) tree.NodeRemoved += OnNodeRemoved;

        // The path box names what is on screen, so the scan button offers to rescan the snapshot's folder,
        // unless the snapshot was taken on another system (e.g. a Windows path opened on Linux).
        if (tree?.Info.Source == ScanSource.Snapshot && Path.IsPathFullyQualified(tree.Info.RootPath))
            PathText = tree.Info.RootPath;

        FolderTree.Load(tree);
        foreach (var vm in AnalysisViewModels) vm.Load(tree);
        UpdateBreadcrumbs();
        UpdateStatus();
        SnapshotBanner = tree?.Info.Source == ScanSource.Snapshot
            ? $"Viewing a snapshot taken {tree.Info.ScannedAt.ToString("g", CultureInfo.CurrentCulture)}. Rescan to see the current state."
            : null;
        ShowPrivacyHint = !_privacyHintDismissed && NeedsPrivacyHint(tree);
        TreeRevision++;

        // Background work holds leases, so the native memory is only freed once it finishes.
        old?.Dispose();
    }

    private IEnumerable<AnalysisViewModelBase> AnalysisViewModels => [FileTypes, Largest, Duplicates, Cleanup, Compare];

    private void UpdateStatus()
    {
        if (Tree is null)
        {
            TotalText = ScanInfoText = "";
            return;
        }
        ref readonly var root = ref Tree.Nodes[FileTree.Root];
        TotalText = $"{ByteSize.Format(root.Size)} in {root.FileCount:N0} files and {root.DirCount:N0} folders · {ByteSize.Format(root.Allocated)} on disk" +
                    (Tree.SizesApproximate ? " · approximate: hard-linked or cloned files were removed, rescan for exact totals" : "");
        var info = Tree.Info;
        var source = info.Source switch
        {
            ScanSource.Native => info.Mode switch
            {
                ScanMode.Mft => "native MFT scan",
                ScanMode.Ext4 => "native ext4 scan",
                _ => "native scan",
            },
            ScanSource.Managed => "managed scan",
            _ => "snapshot",
        };
        var errors = info.Errors > 0 ? $" · {info.Errors:N0} folders could not be read" : "";
        ScanInfoText = $"{source} in {info.Elapsed.TotalSeconds:0.0} s{errors}";
    }

    // Selection sync between folder tree and treemap

    private void OnTreeRowSelected()
    {
        if (_syncingSelection) return;
        var row = FolderTree.SelectedRow;
        if (row is null or MoreRowViewModel) return;
        _syncingSelection = true;
        SelectedNode = row.Node;
        _syncingSelection = false;
    }

    partial void OnSelectedNodeChanged(int value)
    {
        if (_syncingSelection || Tree is null || value == FileTree.None) return;
        _syncingSelection = true;
        FolderTree.Reveal(value);
        _syncingSelection = false;
    }

    partial void OnFocusNodeChanged(int value) => UpdateBreadcrumbs();

    partial void OnHoveredNodeChanged(int value)
    {
        if (Tree is null || value < 0 || value >= Tree.Count)
        {
            HoverText = "";
            return;
        }
        HoverText = $"{Tree.GetPath(value)}  ·  {ByteSize.Format(Tree.Nodes[value].Size)}";
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (Tree is null) return;
        var chain = new List<int>();
        for (var n = FocusNode; n != FileTree.None; n = Tree.GetParent(n)) chain.Add(n);
        for (var i = chain.Count - 1; i >= 0; i--)
            Breadcrumbs.Add(new BreadcrumbItem(chain[i], chain[i] == FileTree.Root ? Tree.RootPath : Tree.GetName(chain[i])));
    }

    [RelayCommand]
    private void ToggleRow(TreeRowViewModel row) => FolderTree.Toggle(row);

    [RelayCommand]
    private void Focus(int node)
    {
        if (Tree is not null && node >= 0 && Tree.IsDirectory(node)) FocusNode = node;
    }

    [RelayCommand]
    private void FocusUp()
    {
        if (Tree is null || FocusNode == FileTree.Root) return;
        FocusNode = Tree.GetParent(FocusNode);
    }

    /// <summary>Zooms the treemap one level towards <paramref name="node"/>.</summary>
    public void ZoomTowards(int node)
    {
        if (Tree is null || node < 0) return;
        var step = node;
        while (step != FileTree.None && Tree.GetParent(step) != FocusNode) step = Tree.GetParent(step);
        if (step != FileTree.None && Tree.IsDirectory(step)) FocusNode = step;
    }

    // INodeActions

    public void Open(int node) => Try(() => _fileActions.Open(Tree!.GetPath(node)));

    public async Task RevealAsync(int node)
    {
        if (Tree is null) return;
        try
        {
            await _fileActions.RevealAsync(Tree.GetPath(node));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await _dialogs.ShowErrorAsync("Could not show in folder", ex.Message);
        }
    }

    public void ShowProperties(int node) => Try(() => _fileActions.ShowProperties(Tree!.GetPath(node)));

    public async Task CopyPathAsync(int node)
    {
        if (Tree is not null) await _dialogs.SetClipboardTextAsync(Tree.GetPath(node));
    }

    public void ShowInTree(int node)
    {
        if (Tree is null) return;
        SelectedNode = node;
        FolderTree.Reveal(node);
    }

    public void ShowInTreemap(int node)
    {
        if (Tree is null) return;
        FocusNode = Tree.IsDirectory(node) ? node : Math.Max(FileTree.Root, Tree.GetParent(node));
        SelectedNode = node;
        SelectedTabIndex = 0;
    }

    public async Task MoveToTrashAsync(int node)
    {
        var tree = Tree;
        if (tree is null || node == FileTree.Root || tree.Nodes[node].IsDeleted) return;
        var path = tree.GetPath(node);
        ref readonly var n = ref tree.Nodes[node];
        var what = n.IsDirectory ? $"the folder \"{tree.GetName(node)}\" ({n.FileCount:N0} files, {ByteSize.Format(n.Size)})"
                                 : $"\"{tree.GetName(node)}\" ({ByteSize.Format(n.Size)})";
        var bin = FileActions.TrashName;
        if (!await _dialogs.ConfirmAsync($"Move to {bin}?", $"Move {what} to the {bin}?\n\n{path}", $"Move to {bin}", destructive: true))
            return;

        try
        {
            await _fileActions.MoveToTrashAsync(path);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            await _dialogs.ShowErrorAsync($"Could not move to {bin}", ex.Message);
            return;
        }

        if (ReferenceEquals(tree, Tree) && !File.Exists(path) && !Directory.Exists(path)) tree.Remove(node);
    }

    private void OnNodeRemoved(int node)
    {
        if (Tree is null) return;
        if (SelectedNode >= 0 && Tree.Nodes[SelectedNode].IsDeleted) SelectedNode = Tree.GetParent(node);
        if (Tree.Nodes[FocusNode].IsDeleted) FocusNode = Tree.GetParent(node);
        FolderTree.OnNodeRemoved();
        foreach (var vm in AnalysisViewModels) vm.OnNodeRemoved();
        UpdateStatus();
        TreeRevision++;
    }

    // Snapshots and export

    [RelayCommand]
    private async Task SaveSnapshotAsync()
    {
        if (Tree is not { } tree) return;
        var name = $"{SafeName(tree.RootPath)}-{DateTime.Now:yyyy-MM-dd-HHmm}{SnapshotFile.Extension}";
        var path = await _dialogs.SaveFileAsync("Save snapshot", name, "Emma Space snapshot", SnapshotFile.Extension);
        if (path is null || !ReferenceEquals(tree, Tree)) return; // the scan was replaced or closed meanwhile
        await RunFileTaskAsync("Could not save snapshot", () => SnapshotFile.SaveAsync(tree, path));
    }

    [RelayCommand]
    private async Task OpenSnapshotAsync()
    {
        if (IsScanning) return;
        var path = await _dialogs.OpenFileAsync("Open snapshot", "Emma Space snapshot", SnapshotFile.Extension);
        if (path is not null) await OpenSnapshotFileAsync(path);
    }

    /// <summary>
    /// A snapshot handed over from outside: double-clicked while Emma Space was already open, so it asks
    /// before opening it, since it replaces what is on screen. With <paramref name="ask"/> false (the file
    /// that launched the app) it opens directly, unless something is on screen by then or another file
    /// was handed over first. One at a time, in the order they arrive: each waits for the question and
    /// the load before it. Anything that isn't a snapshot file is ignored; that is checked off the UI
    /// thread, since the file may sit on a sleeping disk or a slow share.
    /// </summary>
    public async Task OfferSnapshotAsync(string path, bool ask = true)
    {
        await _snapshotOffers.WaitAsync();
        try
        {
            try
            {
                await _snapshotLoad; // let the snapshot before this one reach the screen first
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[snapshot] previous load failed: {ex.Message}"); // its own caller reports it
            }
            if (!await Task.Run(() => SnapshotFileType.IsSnapshot(path))) return;
            ask = ask || _snapshotHandedOver || Tree is not null;
            _snapshotHandedOver = true;
            if (!ask)
            {
                await OpenSnapshotFileAsync(path);
                return;
            }

            var name = Path.GetFileName(path);
            if (IsScanning)
            {
                await _dialogs.ShowErrorAsync("Snapshot not opened",
                    $"Emma Space is scanning. Open \"{name}\" again once the scan has finished or been cancelled.");
                return;
            }
            // Off the UI thread: the file may sit on a sleeping disk or a slow share.
            var about = await Task.Run(() => SnapshotFile.TryReadSummary(path)) is var (root, taken)
                ? $"\n\nIt is a snapshot of {root}, taken {taken.ToString("g", CultureInfo.CurrentCulture)}."
                : "";
            var replaces = Tree is null ? "" : $"\n\nIt replaces the results on screen ({Tree.RootPath}).";
            if (await _dialogs.ConfirmAsync("Open snapshot?", $"Open \"{name}\"?{about}{replaces}", "Open snapshot"))
                await OpenSnapshotFileAsync(path);
        }
        finally
        {
            _snapshotOffers.Release();
        }
    }

    /// <summary>
    /// Shows a saved snapshot, e.g. one the user double-clicked in Explorer, Finder or a Linux file
    /// manager. Not while a scan runs, and a load that a newer scan or snapshot overtook is dropped.
    /// </summary>
    public Task OpenSnapshotFileAsync(string path) => _snapshotLoad = LoadSnapshotFileAsync(path);

    private async Task LoadSnapshotFileAsync(string path)
    {
        if (IsScanning)
        {
            await _dialogs.ShowErrorAsync("Snapshot not opened",
                $"Emma Space is scanning. Open \"{Path.GetFileName(path)}\" again once the scan has finished or been cancelled.");
            return;
        }
        var load = ++_loadGeneration;
        try
        {
            var tree = await SnapshotFile.LoadAsync(path);
            if (load != _loadGeneration || IsScanning)
            {
                tree.Dispose();
                return;
            }
            SetTree(tree);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("Could not open snapshot", ex.Message);
        }
    }

    [RelayCommand]
    private Task ExportFoldersCsvAsync() => ExportCsvAsync(new CsvExportOptions { IncludeFiles = false, MaxDepth = 6 }, "folders");

    [RelayCommand]
    private Task ExportAllCsvAsync() => ExportCsvAsync(new CsvExportOptions { IncludeFiles = true, MaxDepth = null }, "all");

    private async Task ExportCsvAsync(CsvExportOptions options, string suffix)
    {
        if (Tree is not { } tree) return;
        var path = await _dialogs.SaveFileAsync("Export CSV", $"{SafeName(tree.RootPath)}-{suffix}.csv", "CSV file", ".csv");
        if (path is null || !ReferenceEquals(tree, Tree)) return;
        // The exporter copies the tree before going to the background, so removals cannot skew it.
        await RunFileTaskAsync("Export failed", () => CsvExporter.ExportAsync(tree, path, options));
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (Tree is not { } tree) return;
        var path = await _dialogs.SaveFileAsync("Export report", $"{SafeName(tree.RootPath)}-report.html", "HTML report", ".html");
        if (path is null || !ReferenceEquals(tree, Tree)) return;
        await RunFileTaskAsync("Export failed", () => HtmlReportExporter.ExportAsync(tree, path));
        Try(() => _fileActions.Open(path));
    }

    private async Task RunFileTaskAsync(string errorTitle, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(errorTitle, ex.Message);
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ = _dialogs.ShowErrorAsync("Something went wrong", ex.Message);
        }
    }

    private static string SafeName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(name)) name = path.TrimEnd('\\', '/', ':').Replace(":", "");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrEmpty(name) ? "scan" : name;
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        foreach (var vm in AnalysisViewModels) vm.Cancel();
        Tree?.Dispose();
    }
}
