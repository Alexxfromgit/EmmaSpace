using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Export;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Notices;
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
    private readonly SemaphoreSlim _trash = new(1, 1); // one move to the bin at a time, duplicate checks included
    private readonly HashSet<(FileTree, int)> _trashQueued = []; // moves waiting or under way (UI thread only)
    private Task _snapshotLoad = Task.CompletedTask; // the latest snapshot load, which the next offer waits for
    private bool _snapshotHandedOver; // a snapshot came from outside already, so later ones are asked about
    private bool _syncingSelection;
    private readonly HashSet<string> _dismissedNotices = []; // keys the user dismissed, for this session
    // macOS: what diskutil said last. Kept across refreshes of the start page, so cards and notices don't blink out and
    // back (and aren't posted again) while it is asked anew.
    private IReadOnlyList<MacVolume>? _macVolumes;
    private bool _diskInfoFailed; // diskutil didn't answer the last time it was asked
    private bool _inspecting, _inspectAgain; // one diskutil run at a time; a refresh during one asks for another after it
    private bool _inspectedOnce;
    private HashSet<string> _unavailableSeen = []; // volumes unmounted at the last look, by MacVolume.Key
    private HashSet<string> _unavailableShown = []; // of those, the ones shown: unmounted at two looks in a row
    private readonly Dictionary<string, VolumeSpace> _lastSpace = []; // macOS: Finder's figures last measured, by path
    private IReadOnlyList<string> _hiddenMounts = []; // mounts the start page leaves out (Finder hides them too)
    private readonly CancellationTokenSource _watchCts = new();

    public MainWindowViewModel(IScanner scanner, IFileActions fileActions, IDialogService dialogs, SettingsService settings,
        ElevationService elevation, UpdateViewModel updates, HashCacheService hashCache)
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
        Duplicates = new DuplicatesViewModel(this, settings, hashCache);
        Cleanup = new CleanupViewModel(this, settings);
        Compare = new CompareViewModel(this, dialogs);
        PathText = settings.Current.LastPath ?? "";
        RefreshDrives();
        _ = WatchDrivesAsync(_watchCts.Token);
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

    // Notices: what Emma Space noticed, what it can't do and why, and what the user can do about it

    /// <summary>Notices on the start page: about the drives (locked, backup disks, hidden volumes, purgeable space).</summary>
    public ObservableCollection<NoticeViewModel> HomeNotices { get; } = [];

    /// <summary>Notices above the results: about the scan on screen (unreadable folders, backup disks).</summary>
    public ObservableCollection<NoticeViewModel> ResultNotices { get; } = [];

    /// <summary>
    /// Raised on the UI thread for each notice when it appears, so other parts of the app (the Emma companion) can voice
    /// it too. Not raised again while it stays on screen, even when its wording is updated (same key).
    /// </summary>
    public event EventHandler<Notice>? NoticePosted;

    /// <summary>Raised on the UI thread when a notice leaves the screen: what it said no longer holds, or it was dismissed.</summary>
    public event EventHandler<Notice>? NoticeWithdrawn;

    private void ShowNotices(ObservableCollection<NoticeViewModel> shown, IEnumerable<Notice> notices)
    {
        var wanted = notices.Where(n => !_dismissedNotices.Contains(n.Key)).ToList();
        if (wanted.Count == shown.Count && wanted.Zip(shown).All(p => SameNotice(p.First, p.Second.Notice))) return;
        var before = shown.Select(n => n.Notice).ToList();
        shown.Clear();
        foreach (var n in wanted) shown.Add(new NoticeViewModel(n, RunNoticeActionAsync, n.CanDismiss ? DismissNotice : null));
        var wantedKeys = wanted.Select(n => n.Key).ToHashSet();
        var beforeKeys = before.Select(n => n.Key).ToHashSet();
        foreach (var n in before.Where(n => !wantedKeys.Contains(n.Key))) NoticeWithdrawn?.Invoke(this, n);
        foreach (var n in wanted.Where(n => !beforeKeys.Contains(n.Key))) NoticePosted?.Invoke(this, n);
    }

    // Records compare their action lists by reference, so compare what is shown instead.
    private static bool SameNotice(Notice a, Notice b) =>
        a.Key == b.Key && a.Level == b.Level && a.Title == b.Title && a.Message == b.Message && a.Details == b.Details &&
        a.CanDismiss == b.CanDismiss && a.Actions.SequenceEqual(b.Actions);

    private void DismissNotice(Notice notice)
    {
        _dismissedNotices.Add(notice.Key);
        foreach (var list in (ObservableCollection<NoticeViewModel>[])[HomeNotices, ResultNotices])
            if (list.FirstOrDefault(n => n.Key == notice.Key) is { } shown)
            {
                list.Remove(shown);
                NoticeWithdrawn?.Invoke(this, notice);
            }
    }

    private async Task RunNoticeActionAsync(Notice notice, NoticeAction action)
    {
        if (action.Id == NoticeActions.LearnMore)
        {
            // The explanation, with the notice's first other action as the dialog's button.
            var next = notice.Actions.FirstOrDefault(a => a.Id != NoticeActions.LearnMore);
            if (await _dialogs.ConfirmAsync(notice.Title, notice.Details ?? notice.Message, next?.Label ?? "OK",
                    cancelText: "Close") && next is not null)
                await RunNoticeActionAsync(notice, next);
            return;
        }
        Action? open = action.Id switch
        {
            NoticeActions.MountVolume when action.Argument is { } device => () => MacDisks.Mount(device),
            NoticeActions.OpenStorageSettings => MacStorage.OpenStorageSettings,
            NoticeActions.OpenTimeMachineSettings => MacStorage.OpenTimeMachineSettings,
            NoticeActions.OpenDiskUtility => MacStorage.OpenDiskUtility,
            NoticeActions.OpenPrivacySettings => MacPrivacy.OpenFullDiskAccessSettings,
            _ => null,
        };
        if (open is null) return;
        try
        {
            await Task.Run(open);
        }
        catch (IOException ex)
        {
            await _dialogs.ShowErrorAsync(action.Label, ex.Message);
            return;
        }
        // A mounted drive shows up at once, rather than at the next look for changes.
        if (action.Id == NoticeActions.MountVolume && IsHome) RefreshDrives();
    }

    private void UpdateHomeNotices()
    {
        var notices = new List<Notice>();
        foreach (var d in Drives.Where(d => d.IsUnavailable && d.Volume is not null))
            notices.Add(d.IsLocked ? AppNotices.LockedDrive(d.Volume!) : AppNotices.UnmountedDrive(d.Volume!));
        var attributed = new HashSet<string>();
        foreach (var d in Drives.Where(d => d.IsBackup && d.Volume is not null))
        {
            var mounts = _hiddenMounts.Where(m => string.Equals(MacDisks.TimeMachineMountSource(m), d.Volume!.Uuid,
                StringComparison.OrdinalIgnoreCase)).ToList();
            attributed.UnionWith(mounts);
            notices.Add(AppNotices.BackupDrive(d.Volume!, mounts, ByteSize.Format));
        }
        // Until diskutil has answered, a Time Machine mount can't be tied to its drive yet: say nothing about it for now.
        var unexplained = _hiddenMounts.Where(m => !attributed.Contains(m) &&
                                                   (_macVolumes is not null || MacDisks.TimeMachineMountSource(m) is null)).ToList();
        if (unexplained.Count > 0)
            notices.Add(AppNotices.HiddenMounts(unexplained, m => MacDisks.TimeMachineMountSource(m) is { } uuid
                ? _macVolumes?.FirstOrDefault(v => string.Equals(v.Uuid, uuid, StringComparison.OrdinalIgnoreCase))?.Name
                : null));
        if (_diskInfoFailed) notices.Add(AppNotices.DiskInfoUnavailable());
        if (Drives.FirstOrDefault(d => d.HasPurgeable) is { } p)
            notices.Add(AppNotices.Purgeable(p.Name, p.Space.Free, p.Space.Purgeable, ByteSize.Format));
        ShowNotices(HomeNotices, notices);
    }

    private void UpdateResultNotices()
    {
        var tree = Tree;
        var notices = new List<Notice>();
        if (tree is { Info.Source: not ScanSource.Snapshot })
        {
            ref readonly var root = ref tree.Nodes[FileTree.Root];
            var rootPath = Path.TrimEndingDirectorySeparator(tree.Info.RootPath);
            var rootUnreadable = (root.Flags & NodeFlags.Error) != 0 && root.FileCount == 0 && root.DirCount == 0;
            var backup = OperatingSystem.IsMacOS() ? _macVolumes?.FirstOrDefault(v => v.IsBackup && v.MountPoint == rootPath) : null;
            if (backup is not null) notices.Add(AppNotices.BackupScan(backup, root.Allocated, !rootUnreadable, ByteSize.Format));
            // Full Disk Access is the answer only where macOS guards the data: the user's private folders, backup disks and
            // other drives. A folder only POSIX permissions keep closed (/private/var/root) needs no such advice.
            var guarded = backup is not null || (rootUnreadable && rootPath.StartsWith("/Volumes/", StringComparison.Ordinal)) ||
                          MacPrivacy.MayCoverProtectedData(tree.Info.RootPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (OperatingSystem.IsMacOS() && tree.Info.Errors > 0 && guarded && MacPrivacy.HasFullDiskAccess() == false)
                notices.Add(AppNotices.FullDiskAccess(rootPath));
            // The backup notice already says the map is empty and why.
            if (rootUnreadable && backup is null) notices.Add(AppNotices.RootUnreadable(tree.Info.RootPath));
        }
        ShowNotices(ResultNotices, notices);
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        var found = DriveViewModel.Discover();
        Drives.Clear();
        foreach (var d in found.Drives) Drives.Add(d);
        _hiddenMounts = found.HiddenMounts;
        // What diskutil and the space query said last stands in until they answer again, so nothing blinks.
        ApplyVolumes();
        foreach (var d in Drives.Where(d => d.IsMeasuring))
            if (_lastSpace.TryGetValue(d.Path, out var space)) d.SetMeasured(space);
        UpdateHomeNotices();
        // macOS: Finder's available space comes from a system service that can be slow, so it is asked off the UI thread
        // and the card updated when its answer is in.
        foreach (var d in Drives.Where(d => d.IsMeasuring || _lastSpace.ContainsKey(d.Path))) _ = MeasureDriveAsync(d);
        if (OperatingSystem.IsMacOS()) _ = InspectDisksAsync();
    }

    // macOS: diskutil names the drives as Finder does and tells locked, unmounted and Time Machine backup disks apart. It
    // takes a fraction of a second, so it runs off the UI thread, one run at a time, and the cards follow its answer.
    private async Task InspectDisksAsync()
    {
        if (_inspecting)
        {
            _inspectAgain = true;
            return;
        }
        _inspecting = true;
        try
        {
            do
            {
                _inspectAgain = false;
                IReadOnlyList<MacVolume>? volumes;
                try
                {
                    volumes = await Task.Run(MacDisks.List);
                }
                catch (Exception)
                {
                    volumes = null;
                }
                _diskInfoFailed = volumes is null;
                if (volumes is not null)
                {
                    // A drive that was just plugged in is unmounted for a moment before macOS mounts it: show a volume as
                    // unmounted only once it was so at two looks in a row, and look again soon after a new one.
                    var now = volumes.Where(v => v.IsUnavailable).Select(v => v.Key).ToHashSet();
                    _unavailableShown = _inspectedOnce ? now.Where(_unavailableSeen.Contains).ToHashSet() : now;
                    if (_inspectedOnce && now.Any(k => !_unavailableSeen.Contains(k))) _ = InspectAgainSoonAsync();
                    _unavailableSeen = now;
                    _macVolumes = volumes;
                    _inspectedOnce = true;
                }
                ApplyVolumes();
                UpdateHomeNotices();
                if (Tree is not null) UpdateResultNotices();
            } while (_inspectAgain);
        }
        finally
        {
            _inspecting = false;
        }
    }

    private async Task InspectAgainSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (IsHome) await InspectDisksAsync();
    }

    // Names the cards as diskutil does, and shows cards for volumes that hold files but aren't mounted (locked, or ejected
    // while their disk stays attached): .NET doesn't see those, and the user should know why they can't be scanned.
    private void ApplyVolumes()
    {
        if (_macVolumes is not { } volumes) return;
        foreach (var d in Drives.Where(d => !d.IsUnavailable))
            if (volumes.FirstOrDefault(v => v.MountPoint is not null && v.MountPoint == Path.TrimEndingDirectorySeparator(d.Path)) is { } v)
                d.SetVolume(v);
        var wanted = volumes.Where(v => v.IsUnavailable && _unavailableShown.Contains(v.Key)).ToList();
        for (var i = Drives.Count - 1; i >= 0; i--)
            if (Drives[i] is { IsUnavailable: true } card && !wanted.Any(v => v == card.Volume))
                Drives.RemoveAt(i);
        var at = Drives.Count > 0 && Drives[^1].IsUserFolder ? Drives.Count - 1 : Drives.Count;
        foreach (var v in wanted.Where(v => !Drives.Any(d => d.IsUnavailable && d.Volume == v)))
            Drives.Insert(at++, DriveViewModel.Unavailable(v));
    }

    // Refreshes the start page when a drive is attached, unlocked, mounted or ejected, so a newly unlocked drive appears
    // without the user having to look for a refresh. Cheap: the mounts the start page lists (hidden ones included, which
    // its notices mention) and, on macOS, the whole-disk /dev entries.
    private async Task WatchDrivesAsync(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var last = await Task.Run(DriveSignature, cancel);
        try
        {
            while (await timer.WaitForNextTickAsync(cancel))
            {
                if (!IsHome) continue;
                var now = await Task.Run(DriveSignature, cancel);
                if (now == last) continue;
                last = now;
                if (IsHome) RefreshDrives();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string DriveSignature()
    {
        try
        {
            return string.Join('\n', DriveInfo.GetDrives().Select(d => d.Name).Where(DriveViewModel.IsListedMount)) + "\n" +
                   MacDisks.DeviceSignature();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>A drive that hasn't answered by then shows DriveInfo's figures rather than none.</summary>
    private static readonly TimeSpan DriveMeasureTimeout = TimeSpan.FromSeconds(5);

    // Runs on the UI thread (RefreshDrives does), so the await resumes there. A drive a newer refresh replaced is no longer
    // shown, so filling it in does no harm.
    private async Task MeasureDriveAsync(DriveViewModel drive)
    {
        VolumeSpace? space = null;
        try
        {
            space = await Task.Run(() => StorageInfo.GetSpace(drive.Path)).WaitAsync(DriveMeasureTimeout);
        }
        catch (Exception)
        {
            // Timed out or failed: keep the figures shown (the last ones, or DriveInfo's). A late answer is ignored.
        }
        if (space is { } s)
        {
            _lastSpace[drive.Path] = s;
            drive.SetMeasured(s);
        }
        else if (drive.IsMeasuring)
        {
            drive.SetMeasured(null);
        }
        UpdateHomeNotices();
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
        // A locked or unmounted drive has nothing to scan yet: explain why, and what to do.
        if (drive is { IsUnavailable: true, Volume: { } volume })
        {
            var notice = drive.IsLocked ? AppNotices.LockedDrive(volume) : AppNotices.UnmountedDrive(volume);
            return RunNoticeActionAsync(notice, NoticeActions.LearnMoreAction);
        }
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
        UpdateResultNotices();
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
        var errors = info.Errors > 0 ? $" · {AppNotices.Count(info.Errors, "folder", "folders")} could not be read" : "";
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

    public Task MoveToTrashAsync(FileTree tree, int node) => MoveOneAtATimeAsync(tree, node, copies: null);

    public Task MoveDuplicateToTrashAsync(FileTree tree, int node, IReadOnlyList<int> copies) =>
        MoveOneAtATimeAsync(tree, node, copies);

    /// <param name="tree">The results the click was made in: its node numbers mean something only there.</param>
    private async Task MoveOneAtATimeAsync(FileTree tree, int node, IReadOnlyList<int>? copies)
    {
        // Results replaced before the click (a menu left open over a rescan) or while it waited: drop it. So is a
        // second click on an item whose move is already waiting or being asked about.
        if (!ReferenceEquals(tree, Tree) || !_trashQueued.Add((tree, node))) return;
        try
        {
            // One at a time, whatever is moved: two copies of one group moved at once could each be checked against
            // the other, and any other move could take away the copy (or its folder) that a check is comparing with.
            await _trash.WaitAsync();
            try
            {
                if (!ReferenceEquals(tree, Tree)) return;
                await MoveToTrashAsync(tree, node, copies);
            }
            finally
            {
                _trash.Release();
            }
        }
        finally
        {
            _trashQueued.Remove((tree, node));
        }
    }

    private async Task MoveToTrashAsync(FileTree tree, int node, IReadOnlyList<int>? copies)
    {
        if (node <= FileTree.Root || node >= tree.Count || tree.Nodes[node].IsDeleted) return;
        var path = tree.GetPath(node);
        ref readonly var n = ref tree.Nodes[node];
        var name = tree.GetName(node);
        var size = n.Size;
        var what = n.IsDirectory ? $"the folder \"{name}\" ({n.FileCount:N0} files, {ByteSize.Format(n.Size)})"
                                 : $"\"{name}\" ({ByteSize.Format(n.Size)})";
        // The other copies still on screen and outside the bin. With none left (moved or removed meanwhile, e.g. by
        // a click that waited for this one) this is the last copy: the question says so, and it is moved like any file.
        var others = copies is null ? []
            : copies.Where(c => c != node && c > FileTree.Root && c < tree.Count && !tree.Nodes[c].IsDeleted)
                .Select(tree.GetPath).Where(p => !TrashFolders.Contains(p)).ToList();
        var bin = FileActions.TrashName;
        if (others.Count > 0 && !DuplicateFinder.CanCheckCopies)
        {
            await _dialogs.ShowErrorAsync($"Not moved to {bin}",
                $"Emma Space can't check that \"{name}\" is still identical to another copy: the check needs its native " +
                $"engine, which didn't load. Nothing was moved.\n\n{path}");
            return;
        }
        var check = copies is null ? ""
            : others.Count > 0 ? "\n\nEmma Space first checks that it is still identical to another copy."
            : "\n\nIt is the last copy left of these duplicates: the others were moved or removed.";
        if (!await _dialogs.ConfirmAsync($"Move to {bin}?", $"Move {what} to the {bin}?{check}\n\n{path}", $"Move to {bin}", destructive: true))
            return;

        if (others.Count > 0)
        {
            // The search may be old (or saved in a snapshot) and the files may have changed since: compare
            // them byte by byte now, without any remembered hash. Copies in the bin don't count.
            var ct = Duplicates.BeginCheck($"Checking that \"{name}\" is still identical to another copy…");
            IdenticalCopyCheck? found = null;
            var cancelled = false;
            try
            {
                var progress = new Progress<double>(Duplicates.ReportCheck);
                found = await Task.Run(() => DuplicateFinder.FindIdenticalCopyAsync(path, size, others, progress, ct), ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true; // Stop: nothing is moved
            }
            finally
            {
                Duplicates.EndCheck(cancelled);
            }
            if (cancelled) return;
            if (!ReferenceEquals(tree, Tree) || tree.Nodes[node].IsDeleted)
            {
                await _dialogs.ShowErrorAsync($"Not moved to {bin}",
                    $"The results changed while the copies were compared, so \"{name}\" was not moved. Try again.\n\n{path}");
                return;
            }
            if (found is { Match: null, InUse: true })
            {
                await _dialogs.ShowErrorAsync($"Not moved to {bin}",
                    $"\"{name}\" or one of its copies is open in another program, so they can't be compared now. Close it " +
                    $"and try again.\n\n{path}");
                return;
            }
            // Both files must still be the ones compared, unchanged: only moves made here wait for the check, not
            // other programs, which could have changed either or put another file in its place since.
            if (found?.Match is not { } identical || !DuplicateFinder.IsStillTheMatch(identical, found.MatchStamp) ||
                !DuplicateFinder.IsStillTheMatch(path, found.TargetStamp))
            {
                await _dialogs.ShowErrorAsync($"Not moved to {bin}",
                    $"\"{name}\" is no longer identical to any other copy outside the {bin}: a file changed since they were " +
                    $"compared, or can't be read. Search for duplicates again.\n\n{path}");
                return;
            }
        }

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
        // A completed duplicate search is saved with it, so opening the snapshot shows it without searching again.
        var duplicates = Duplicates.CurrentResult;
        await RunFileTaskAsync("Could not save snapshot", () => SnapshotFile.SaveAsync(tree, path, duplicates));
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
            var (tree, duplicates) = await SnapshotFile.LoadSnapshotAsync(path);
            if (load != _loadGeneration || IsScanning)
            {
                tree.Dispose();
                return;
            }
            SetTree(tree);
            if (duplicates is not null) Duplicates.ShowSaved(duplicates);
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
        _watchCts.Cancel();
        _scanCts?.Cancel();
        foreach (var vm in AnalysisViewModels) vm.Cancel();
        Tree?.Dispose();
    }
}
