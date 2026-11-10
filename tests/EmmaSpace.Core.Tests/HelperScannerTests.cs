using System.Diagnostics;
using System.Runtime.InteropServices;
using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;
using EmmaSpace.Core.Platform;
using EmmaSpace.Core.Scanning;
using EmmaSpace.Core.Snapshots;

namespace EmmaSpace.Core.Tests;

public class HelperScannerTests
{
    // ---- Protocol, with streams written here

    private static void Header(BinaryWriter w, uint version = HelperProtocol.Version)
    {
        w.Write("EMSH"u8);
        w.Write(version);
        w.Write((uint)Marshal.SizeOf<EsNode>());
    }

    private static void Progress(BinaryWriter w, ulong files, string path)
    {
        w.Write((byte)1);
        w.Write(files);
        w.Write(2UL);
        w.Write(300UL);
        w.Write(0u);
        w.Write((uint)ScanPhase.Enumerating);
        w.Write((uint)ScanMode.Ext4);
        w.Write((uint)path.Length);
        w.Write(MemoryMarshal.AsBytes(path.AsSpan()));
    }

    private static void Result(BinaryWriter w, EsStatus status, EsNode[] nodes, string names)
    {
        w.Write((byte)2);
        w.Write((uint)status);
        w.Write((uint)ScanMode.Ext4);
        w.Write(3u);   // errors
        w.Write(42UL); // elapsed ms
        w.Write((uint)nodes.Length);
        w.Write((ulong)names.Length);
        w.Write(MemoryMarshal.AsBytes(nodes.AsSpan()));
        w.Write(MemoryMarshal.AsBytes(names.AsSpan()));
    }

    // "/" with one file "a" (5 bytes).
    private static EsNode[] TwoNodes() =>
    [
        new() { Parent = EsNode.None, FirstChild = 1, NextSibling = EsNode.None, NameOffset = 0, NameLength = 1, Size = 5, FileCount = 1, Flags = NodeFlags.Directory },
        new() { Parent = 0, FirstChild = EsNode.None, NextSibling = EsNode.None, NameOffset = 1, NameLength = 1, Size = 5, FileCount = 1 },
    ];

    private static MemoryStream Stream(Action<BinaryWriter> write)
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Protocol_reports_progress_then_returns_the_tree()
    {
        using var stream = Stream(w =>
        {
            Header(w);
            Progress(w, 10, "Reading the file system's inode tables…");
            Progress(w, 20, "");
            Result(w, EsStatus.Ok, TwoNodes(), "/a");
        });
        var reports = new List<ScanProgress>();
        var result = await HelperProtocol.ReadAsync(stream, new ScannerTests.SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.Equal([10UL, 20UL], reports.Select(r => r.Files));
        Assert.Equal("Reading the file system's inode tables…", reports[0].CurrentPath);
        Assert.Equal(EsStatus.Ok, result.Status);
        Assert.Equal(ScanMode.Ext4, result.Mode);
        Assert.Equal(3u, result.Errors);
        Assert.Equal(42UL, result.ElapsedMs);
        Assert.Equal(2, result.Nodes.Length);
        Assert.Equal(5UL, result.Nodes[1].Size);
        Assert.Equal("/a", new string(result.Names));
    }

    [Fact]
    public async Task Protocol_passes_a_failed_status_through()
    {
        using var stream = Stream(w =>
        {
            Header(w);
            Result(w, EsStatus.Cancelled, [], "");
        });
        var result = await HelperProtocol.ReadAsync(stream, null, TestContext.Current.CancellationToken);
        Assert.Equal(EsStatus.Cancelled, result.Status);
        Assert.Empty(result.Nodes);
    }

    [Fact]
    public async Task Protocol_rejects_other_versions_truncation_and_unknown_frames()
    {
        var ct = TestContext.Current.CancellationToken;
        using var version = Stream(w => Header(w, version: 99));
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperProtocol.ReadAsync(version, null, ct));

        using var full = Stream(w =>
        {
            Header(w);
            Result(w, EsStatus.Ok, TwoNodes(), "/a");
        });
        using var truncated = new MemoryStream(full.ToArray()[..^3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperProtocol.ReadAsync(truncated, null, ct));

        using var empty = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperProtocol.ReadAsync(empty, null, ct));

        using var unknown = Stream(w =>
        {
            Header(w);
            w.Write((byte)7);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperProtocol.ReadAsync(unknown, null, ct));
    }

    [Fact]
    public void Damaged_trees_are_rejected()
    {
        var cycle = TwoNodes();
        cycle[1].NextSibling = 1; // a sibling list that loops
        Assert.Throws<InvalidDataException>(() => HelperProtocol.Validate(cycle, "/a"));

        var outside = TwoNodes();
        outside[1].NameOffset = 5; // name past the pool
        Assert.Throws<InvalidDataException>(() => HelperProtocol.Validate(outside, "/a"));

        var orphan = TwoNodes();
        orphan[0].FirstChild = EsNode.None; // node 1 is unreachable
        Assert.Throws<InvalidDataException>(() => HelperProtocol.Validate(orphan, "/a"));

        HelperProtocol.Validate(TwoNodes(), "/a");

        // Names below the root must be single path components (a crafted entry must not lead outside the folder).
        foreach (var bad in new[] { "..", ".", "a/b", "", "x\0y" })
        {
            var named = TwoNodes();
            named[1].NameLength = (ushort)bad.Length;
            Assert.Throws<InvalidDataException>(() => HelperProtocol.Validate(named, "/" + bad));
        }
    }

    // ---- The real helper, started directly instead of through pkexec (as the user, so it walks)

    private static HelperScanner DirectScanner(bool kill = true) => new((helper, root) =>
    {
        var start = new ProcessStartInfo(helper);
        start.ArgumentList.Add("scan");
        start.ArgumentList.Add(root);
        return start;
    }) { KillOnCancel = kill };

    [Fact]
    public async Task Helper_scans_like_the_native_engine()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && HelperScanner.BundledHelperPath is not null && NativeEngine.IsAvailable,
            "The helper is built on Linux only.");
        var ct = TestContext.Current.CancellationToken;
        var folder = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var reports = new List<ScanProgress>();
        using var viaHelper = await DirectScanner().ScanAsync(folder, new ScanOptions(), new ScannerTests.SyncProgress(reports), ct);
        using var native = await new NativeScanner().ScanAsync(folder, new ScanOptions(), null, ct);

        Assert.Equal(ScanSource.Native, viaHelper.Info.Source);
        Assert.Equal(native.Count, viaHelper.Count);
        Assert.Equal(native.Nodes[0].Size, viaHelper.Nodes[0].Size);
        Assert.Equal(native.Nodes[0].FileCount, viaHelper.Nodes[0].FileCount);
        Assert.Equal(native.RootPath, viaHelper.RootPath);
        Assert.NotEqual(FileTree.None, viaHelper.FindByPath(typeof(object).Assembly.Location));
    }

    [Fact]
    public async Task Helper_reports_missing_folders_and_honours_cancellation()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && HelperScanner.BundledHelperPath is not null,
            "The helper is built on Linux only.");
        var missing = Path.Combine(Path.GetTempPath(), "emmaspace-missing-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<ScanException>(() =>
            DirectScanner().ScanAsync(missing, new ScanOptions(), null, TestContext.Current.CancellationToken));
        Assert.Equal(EsStatus.NotFound, ex.Status);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DirectScanner().ScanAsync("/usr", new ScanOptions(), null, cts.Token));

        // Cancel while it walks all of / (seconds as a user), with Kill() off as for a root helper: stdin only.
        using var running = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();
        var scan = DirectScanner(kill: false).ScanAsync("/", new ScanOptions(), null, running.Token);
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Same(scan, finished); // it did not hang
        if (scan.IsCompletedSuccessfully)
        {
            scan.Result.Dispose(); // a very fast machine finished before the cancel
            return;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed} to stop");
    }

    [Fact]
    public async Task Cancel_kills_a_launcher_that_is_still_waiting()
    {
        // Stands in for pkexec at its password prompt: no output, ignores stdin, still killable by the user.
        // Cancel must end it (that closes the dialog), not only write to its stdin.
        Assert.SkipUnless(OperatingSystem.IsLinux() && HelperScanner.BundledHelperPath is not null,
            "The helper is built on Linux only.");
        var dir = Directory.CreateTempSubdirectory("emmaspace-kill-");
        try
        {
            var pidFile = Path.Combine(dir.FullName, "pid");
            var scanner = new HelperScanner((_, _) =>
            {
                var start = new ProcessStartInfo("/bin/sh");
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("echo $$ > \"$0\"; exec sleep 60");
                start.ArgumentList.Add(pidFile);
                return start;
            });
            using var cts = new CancellationTokenSource();
            var scan = scanner.ScanAsync("/", new ScanOptions(), null, cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "the stand-in never started");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            var pid = int.Parse(File.ReadAllText(pidFile).Trim());
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);

            deadline = DateTime.UtcNow.AddSeconds(5);
            while (IsAlive(pid))
            {
                Assert.True(DateTime.UtcNow < deadline, $"process {pid} still runs after Cancel");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }

        // Gone, or a zombie waiting to be reaped.
        static bool IsAlive(int pid)
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task Helper_stops_and_reports_cancelled_when_written_to_on_stdin()
    {
        // The only way to stop a root helper is its stdin, so test exactly that: no Kill(), no HelperScanner.
        Assert.SkipUnless(OperatingSystem.IsLinux() && HelperScanner.BundledHelperPath is not null,
            "The helper is built on Linux only.");
        var ct = TestContext.Current.CancellationToken;
        var start = new ProcessStartInfo(HelperScanner.BundledHelperPath!)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add("scan");
        start.ArgumentList.Add("/"); // a walk as the user: seconds, so it is still running at the first progress frame
        using var process = Process.Start(start)!;
        _ = process.StandardError.ReadToEndAsync(ct);
        var sent = 0;
        var progress = new InlineProgress(_ =>
        {
            if (Interlocked.Exchange(ref sent, 1) != 0) return;
            process.StandardInput.Write('c');
            process.StandardInput.Flush();
        });
        var result = await HelperProtocol.ReadAsync(process.StandardOutput.BaseStream, progress, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(1, sent);
        Assert.Equal(EsStatus.Cancelled, result.Status);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    [Fact]
    public void Mounts_are_found_by_the_longest_mount_point()
    {
        string[] mountinfo =
        [
            "82 67 8:48 / / rw,relatime - ext4 /dev/sdd rw",
            "133 82 0:71 / /mnt/c rw,noatime - 9p C:\\134 rw,aname=drvfs",
            "140 82 8:50 / /home rw - btrfs /dev/sde rw",
            "141 140 8:51 / /home/with\\040space shared:5 - ext4 /dev/sdf rw",
            "142 82 0:40 / /mnt/cache rw - tmpfs none rw",
            "143 82 8:52 / /mnt/cache rw - ext2 /dev/sdg rw", // mounted over the tmpfs: the last one is visible
        ];
        static (string, string, bool) Of(MountedFileSystem? m) => (m!.MountPoint, m.Type, m.IsMountPoint);
        Assert.Equal(("/", "ext4", false), Of(LinuxMounts.Find("/usr/lib", mountinfo)));
        Assert.Equal(("/", "ext4", false), Of(LinuxMounts.Find("/mnt/caches", mountinfo)));
        Assert.Equal(("/mnt/c", "9p", false), Of(LinuxMounts.Find("/mnt/c/Users/", mountinfo)));
        Assert.Equal(("/home", "btrfs", false), Of(LinuxMounts.Find("/home/me", mountinfo)));
        Assert.Equal(("/home/with space", "ext4", false), Of(LinuxMounts.Find("/home/with space/x", mountinfo)));
        Assert.Equal(("/mnt/cache", "ext2", true), Of(LinuxMounts.Find("/mnt/cache/", mountinfo)));
        Assert.True(LinuxMounts.Find("/mnt/cache", mountinfo)!.IsExt);
        Assert.False(LinuxMounts.Find("/home", mountinfo)!.IsExt);

        // With the kernel's identity of the path, a mount hidden under a later one is not taken for it: ext4 on
        // /mnt/a, then a tmpfs mounted on /mnt, which hides it.
        string[] hidden =
        [
            "82 67 8:48 / / rw - ext4 /dev/sdd rw",
            "150 82 8:60 / /mnt/a rw - ext4 /dev/sdh rw",
            "151 82 0:90 / /mnt rw - tmpfs none rw",
        ];
        Assert.Equal(("/mnt/a", "ext4", false), Of(LinuxMounts.Find("/mnt/a/x", hidden))); // without it: a guess
        Assert.Equal(("/mnt", "tmpfs", false), Of(LinuxMounts.Find("/mnt/a/x", hidden, new MountIdentity(151, 0, 90))));
        Assert.Equal(("/mnt", "tmpfs", false), Of(LinuxMounts.Find("/mnt/a/x", hidden, new MountIdentity(null, 0, 90))));
    }

    [Fact]
    public void Mount_lookup_on_this_system_resolves_links_and_finds_the_mount()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var root = LinuxMounts.Find("/");
        Assert.NotNull(root); // also proves realpath and statx were found in libc
        Assert.Equal("/", root.MountPoint);
        Assert.True(root.IsMountPoint);

        var proc = LinuxMounts.Find("/proc/self");
        Assert.Equal(("/proc", "proc"), (proc!.MountPoint, proc.Type));

        var dir = Directory.CreateTempSubdirectory("emmaspace-mounts-");
        try
        {
            var link = Path.Combine(dir.FullName, "to-proc");
            File.CreateSymbolicLink(link, "/proc");
            var viaLink = LinuxMounts.Find(link);
            Assert.Equal(("/proc", "proc", "/proc", true), (viaLink!.MountPoint, viaLink.Type, viaLink.Path, viaLink.IsMountPoint));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Snapshots_keep_the_ext4_scan_mode()
    {
        var b = new TreeBuilder();
        b.AddRoot("/");
        b.AddChild(0, "a", NodeFlags.None, 5, 4096);
        using var tree = b.Build(new ScanInfo("/", DateTimeOffset.Now, TimeSpan.FromSeconds(1), ScanMode.Ext4, ScanSource.Native, 0));
        using var stream = new MemoryStream();
        SnapshotFile.Save(tree, stream);
        stream.Position = 0;
        using var loaded = SnapshotFile.Load(stream);
        Assert.Equal(ScanMode.Ext4, loaded.Info.Mode);
    }
}
