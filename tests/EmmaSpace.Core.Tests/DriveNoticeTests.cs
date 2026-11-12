using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Notices;
using EmmaSpace.Core.Platform;

namespace EmmaSpace.Core.Tests;

public sealed class DriveNoticeTests
{
    private const string Header =
        """<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0">""";

    // Trimmed from `diskutil apfs list -plist` on a Mac with a locked-then-unlocked Time Machine disk attached.
    private const string ApfsList = Header + """
        <dict><key>Containers</key><array>
          <dict>
            <key>ContainerReference</key><string>disk3</string>
            <key>PhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk0s2</string></dict></array>
            <key>Volumes</key><array>
              <dict><key>DeviceIdentifier</key><string>disk3s1</string><key>Name</key><string>Macintosh HD</string>
                <key>APFSVolumeUUID</key><string>AAAAAAAA-0000-4000-8000-000000000001</string><key>Roles</key><array><string>System</string></array>
                <key>Locked</key><false/><key>Encryption</key><false/><key>FileVault</key><true/><key>CapacityInUse</key><integer>11257839616</integer></dict>
              <dict><key>DeviceIdentifier</key><string>disk3s5</string><key>Name</key><string>Data</string>
                <key>APFSVolumeUUID</key><string>AAAAAAAA-0000-4000-8000-000000000004</string><key>Roles</key><array><string>Data</string></array>
                <key>Locked</key><false/><key>FileVault</key><true/><key>CapacityInUse</key><integer>911341383680</integer></dict>
            </array>
          </dict>
          <dict>
            <key>ContainerReference</key><string>disk5</string>
            <key>PhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk4s1</string></dict></array>
            <key>Volumes</key><array>
              <dict><key>DeviceIdentifier</key><string>disk5s1</string><key>Name</key><string>iOS 26.1 Simulator</string>
                <key>APFSVolumeUUID</key><string>AAAAAAAA-0000-4000-8000-000000000002</string><key>Roles</key><array/>
                <key>Locked</key><false/><key>CapacityInUse</key><integer>17066635264</integer></dict>
            </array>
          </dict>
          <dict>
            <key>ContainerReference</key><string>disk15</string>
            <key>PhysicalStores</key><array><dict><key>DeviceIdentifier</key><string>disk14s2</string></dict></array>
            <key>Volumes</key><array>
              <dict><key>DeviceIdentifier</key><string>disk15s2</string><key>Name</key><string>My Passport</string>
                <key>APFSVolumeUUID</key><string>AAAAAAAA-0000-4000-8000-000000000003</string><key>Roles</key><array><string>Backup</string></array>
                <key>Locked</key><LOCKED/><key>Encryption</key><true/><key>FileVault</key><true/><key>CapacityInUse</key><integer>1299257090048</integer></dict>
            </array>
          </dict>
        </array></dict></plist>
        """;

    // Trimmed from `diskutil list -plist`: where the APFS volumes are mounted.
    private const string DiskListUnlocked = Header + """
        <dict><key>AllDisksAndPartitions</key><array>
          <dict><key>DeviceIdentifier</key><string>disk3</string><key>APFSVolumes</key><array>
            <dict><key>DeviceIdentifier</key><string>disk3s1</string><key>VolumeName</key><string>Macintosh HD</string></dict>
            <dict><key>DeviceIdentifier</key><string>disk3s1s1</string><key>MountPoint</key><string>/</string></dict>
            <dict><key>DeviceIdentifier</key><string>disk3s5</string><key>MountPoint</key><string>/System/Volumes/Data</string></dict>
          </array></dict>
          <dict><key>DeviceIdentifier</key><string>disk5</string><key>APFSVolumes</key><array>
            <dict><key>DeviceIdentifier</key><string>disk5s1</string><key>MountPoint</key><string>/Library/Developer/CoreSimulator/Volumes/iOS_23B86</string></dict>
          </array></dict>
          <dict><key>DeviceIdentifier</key><string>disk15</string><key>APFSVolumes</key><array>
            <dict><key>DeviceIdentifier</key><string>disk15s2</string><key>MountPoint</key><string>/Volumes/My Passport</string></dict>
          </array></dict>
        </array></dict></plist>
        """;

    // `diskutil list -plist external physical`: disk images such as the simulator's (disk4) aren't physical.
    private const string ExternalPhysical = Header + """
        <dict><key>AllDisksAndPartitions</key><array/><key>WholeDisks</key><array><string>disk14</string></array></dict></plist>
        """;

    private static IReadOnlyList<MacVolume> Volumes(bool locked) =>
        MacDisks.Parse(ApfsList.Replace("<LOCKED/>", locked ? "<true/>" : "<false/>"),
            locked ? DiskListUnlocked.Replace("<key>MountPoint</key><string>/Volumes/My Passport</string>", "") : DiskListUnlocked,
            ExternalPhysical);

    [Fact]
    public void A_locked_backup_disk_is_external_locked_and_not_mounted()
    {
        var passport = Assert.Single(Volumes(locked: true), v => v.Name == "My Passport");
        Assert.True(passport.IsLocked);
        Assert.True(passport.IsEncrypted);
        Assert.True(passport.IsBackup);
        Assert.True(passport.IsExternal);
        Assert.Null(passport.MountPoint);
        Assert.Equal("disk15s2", passport.DeviceId);
        Assert.Equal(1_299_257_090_048UL, passport.CapacityInUse);
    }

    [Fact]
    public void Unlocked_it_is_mounted_and_internal_volumes_and_disk_images_are_not_external()
    {
        var volumes = Volumes(locked: false);
        var passport = Assert.Single(volumes, v => v.Name == "My Passport");
        Assert.False(passport.IsLocked);
        Assert.Equal("/Volumes/My Passport", passport.MountPoint);
        Assert.All(volumes.Where(v => v.Name != "My Passport"), v => Assert.False(v.IsExternal));
        Assert.All(volumes.Where(v => v.Name != "My Passport"), v => Assert.False(v.IsBackup));
        Assert.Equal("/System/Volumes/Data", Assert.Single(volumes, v => v.Name == "Data").MountPoint);
    }

    [Fact]
    public void Unmounted_or_locked_external_volumes_are_unavailable_but_system_ones_are_not_listed_as_such()
    {
        Assert.True(Volumes(locked: true).Single(v => v.Name == "My Passport").IsUnavailable);
        Assert.False(Volumes(locked: false).Single(v => v.Name == "My Passport").IsUnavailable);
        // Unmounted but unlocked (ejected while the disk stays attached): still unavailable, and said so.
        var ejected = MacDisks.Parse(ApfsList.Replace("<LOCKED/>", "<false/>"),
            DiskListUnlocked.Replace("<key>MountPoint</key><string>/Volumes/My Passport</string>", ""), ExternalPhysical)
            .Single(v => v.Name == "My Passport");
        Assert.True(ejected.IsUnavailable);
        Assert.False(ejected.IsLocked);
        var notice = AppNotices.UnmountedDrive(ejected);
        Assert.Equal("My Passport is connected but not mounted", notice.Title);
        Assert.Contains(notice.Actions, a => a is { Id: NoticeActions.MountVolume, Argument: "disk15s2" });
        // macOS's own volumes (System here) are never offered, even unmounted.
        Assert.False(Volumes(locked: false).Single(v => v.Name == "Macintosh HD").HoldsUserFiles);
        Assert.True(Volumes(locked: false).Single(v => v.Name == "Data").HoldsUserFiles);
    }

    [Fact]
    public void Without_an_external_disk_or_backups_the_volumes_are_still_listed()
    {
        // diskutil exits 1 for "external physical" when nothing is attached, and tmutil when no backup disk is set up:
        // both mean "none", not "unknown".
        var volumes = MacDisks.Parse(ApfsList.Replace("<LOCKED/>", "<false/>"), DiskListUnlocked, null, null);
        Assert.Contains(volumes, v => v.MountPoint == "/System/Volumes/Data");
        Assert.All(volumes, v => Assert.False(v.IsExternal));
        Assert.True(volumes.Single(v => v.Name == "My Passport").IsBackup); // by its APFS role
    }

    // `tmutil destinationinfo -X`, and an external disk with an ExFAT stick's partition that isn't mounted and an HFS+ one
    // Time Machine backs up to.
    private const string Destinations = Header + """
        <dict><key>Destinations</key><array><dict><key>Kind</key><string>Local</string><key>Name</key><string>Old Backups</string>
        <key>MountPoint</key><string>/Volumes/Old Backups</string></dict></array></dict></plist>
        """;

    private const string ExternalWithOtherFileSystems = Header + """
        <dict><key>WholeDisks</key><array><string>disk14</string><string>disk20</string></array>
        <key>AllDisksAndPartitions</key><array>
          <dict><key>DeviceIdentifier</key><string>disk20</string><key>Partitions</key><array>
            <dict><key>DeviceIdentifier</key><string>disk20s1</string><key>Content</key><string>EFI</string><key>VolumeName</key><string>EFI</string></dict>
            <dict><key>DeviceIdentifier</key><string>disk20s2</string><key>Content</key><string>Microsoft Basic Data</string>
              <key>VolumeName</key><string>STICK</string><key>VolumeUUID</key><string>11111111-2222-3333-4444-555555555555</string></dict>
            <dict><key>DeviceIdentifier</key><string>disk20s3</string><key>Content</key><string>Apple_HFS</string>
              <key>VolumeName</key><string>Old Backups</string><key>MountPoint</key><string>/Volumes/Old Backups</string></dict>
          </array></dict>
        </array></dict></plist>
        """;

    [Fact]
    public void Other_file_systems_and_older_backup_disks_are_known_too()
    {
        var volumes = MacDisks.Parse(ApfsList.Replace("<LOCKED/>", "<false/>"), DiskListUnlocked, ExternalWithOtherFileSystems,
            Destinations);
        var stick = Assert.Single(volumes, v => v.Name == "STICK");
        Assert.False(stick.IsApfs);
        Assert.True(stick.IsExternal);
        Assert.True(stick.IsUnavailable); // not mounted
        Assert.Equal("11111111-2222-3333-4444-555555555555", stick.Key);
        var old = Assert.Single(volumes, v => v.Name == "Old Backups");
        Assert.True(old.IsBackup); // Time Machine backs up there
        Assert.False(old.IsUnavailable);
        Assert.DoesNotContain(volumes, v => v.Name == "EFI");
        Assert.Contains("Full Disk Access", AppNotices.BackupDrive(old, [], Decimal).Message);
        Assert.DoesNotContain("snapshots", AppNotices.BackupDrive(old, [], Decimal).Message);
    }

    [Theory]
    [InlineData("disk15s2\n")]
    [InlineData("disk15s")]
    [InlineData("disk15s2; rm -rf /")]
    [InlineData("/dev/disk15s2")]
    public void Mount_refuses_anything_but_a_volume_name(string device) =>
        Assert.Throws<ArgumentException>(() => MacDisks.Mount(device));

    [Theory]
    [InlineData("disk14s2", "disk14")]
    [InlineData("disk3s1s1", "disk3")]
    [InlineData("disk14", "disk14")]
    public void Partitions_belong_to_their_whole_disk(string device, string whole) =>
        Assert.Equal(whole, MacDisks.WholeDisk(device));

    [Fact]
    public void Time_Machine_mounts_are_hidden_and_tied_to_their_backup_volume()
    {
        const string mount = "/Volumes/.timemachine/AAAAAAAA-0000-4000-8000-000000000003/2025-10-19-215311.backup";
        Assert.True(MacDisks.IsHiddenMount(mount));
        Assert.Equal("AAAAAAAA-0000-4000-8000-000000000003", MacDisks.TimeMachineMountSource(mount));
        Assert.False(MacDisks.IsHiddenMount("/Volumes/My Passport"));
        Assert.Null(MacDisks.TimeMachineMountSource("/Volumes/My Passport"));
        Assert.Null(MacDisks.TimeMachineMountSource("/Volumes/.timemachine/not-a-uuid/x"));
    }

    [Fact]
    public void Property_lists_read_every_value_type_and_refuse_other_xml()
    {
        var root = PropertyList.Parse(Header + """
            <dict><key>s</key><string>a &amp; b</string><key>i</key><integer>-5</integer><key>big</key><integer>18446744073709551615</integer>
            <key>r</key><real>1.5</real><key>t</key><true/><key>a</key><array><string>x</string><dict/></array><key>e</key><string/></dict></plist>
            """);
        Assert.Equal("a & b", PropertyList.String(root, "s"));
        Assert.Equal(0UL, PropertyList.UInt(root, "i")); // negative: not a size
        Assert.Equal(ulong.MaxValue, PropertyList.UInt(root, "big"));
        Assert.True(PropertyList.Bool(root, "t"));
        Assert.Equal(["x"], PropertyList.Strings(root, "a"));
        Assert.Equal("", PropertyList.String(root, "e"));
        Assert.Throws<FormatException>(() => PropertyList.Parse("<html/>"));
        Assert.Throws<FormatException>(() => PropertyList.Parse("not xml"));
    }

    [Fact]
    public void Listing_disks_works_on_macOS_only()
    {
        var volumes = MacDisks.List();
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Null(volumes);
            return;
        }
        Assert.NotNull(volumes);
        Assert.Contains(volumes, v => v.MountPoint == "/System/Volumes/Data");
        Assert.NotEqual("", MacDisks.DeviceSignature());
    }

    private static string Decimal(ulong bytes) => ByteSize.Format(bytes, decimalUnits: true);

    [Fact]
    public void Notices_explain_locked_and_backup_disks_and_offer_what_helps()
    {
        var locked = Volumes(locked: true).Single(v => v.Name == "My Passport");
        var lockedNotice = AppNotices.LockedDrive(locked);
        Assert.Equal("drive.locked:AAAAAAAA-0000-4000-8000-000000000003", lockedNotice.Key);
        Assert.Equal("My Passport is locked", lockedNotice.Title);
        Assert.Contains("never asks for, sees or keeps drive passwords", lockedNotice.Details);
        Assert.Contains(lockedNotice.Actions, a => a.Id == NoticeActions.OpenDiskUtility);

        const string mount = "/Volumes/.timemachine/AAAAAAAA-0000-4000-8000-000000000003/2025-10-19-215311.backup";
        var backup = AppNotices.BackupDrive(locked, [mount], Decimal);
        Assert.Contains("snapshots", backup.Message);
        Assert.Contains("My Passport uses", backup.Message);
        Assert.Contains(Decimal(1_299_257_090_048), backup.Message);
        Assert.Contains("mounted one of its backups for browsing (" + mount + ")", backup.Details);
        Assert.Contains(backup.Actions, a => a.Id == NoticeActions.OpenTimeMachineSettings);
        Assert.DoesNotContain("for browsing", AppNotices.BackupDrive(locked, [], Decimal).Details);

        Assert.StartsWith("macOS didn't let Emma Space open it", AppNotices.BackupScan(locked, 0, opened: false, Decimal).Message);
        var scan = AppNotices.BackupScan(locked, 4_096, opened: true, Decimal);
        Assert.Contains($"The map shows the {Decimal(4_096)} stored as ordinary files and folders", scan.Message);
        Assert.Equal("scan.backup-disk:AAAAAAAA-0000-4000-8000-000000000003", scan.Key);
        Assert.True(scan.CanDismiss);
    }

    [Fact]
    public void Notices_about_scans_and_hidden_volumes_read_right_for_one_or_many()
    {
        Assert.Equal("A hidden volume isn't listed", AppNotices.HiddenMounts(["/Volumes/.x"], _ => null).Title);
        Assert.Equal("2 hidden volumes aren't listed", AppNotices.HiddenMounts(["/Volumes/.x", "/Volumes/.y"], _ => null).Title);
        var hidden = AppNotices.HiddenMounts(["/Volumes/.tm/b"], _ => "My Passport");
        Assert.Contains("a Time Machine backup of My Passport for browsing", hidden.Message);
        Assert.DoesNotContain("/Volumes/.tm/b", hidden.Message); // paths stay out of what may be read aloud
        Assert.Contains("• /Volumes/.tm/b (a Time Machine backup of My Passport, mounted for browsing)", hidden.Details);
        Assert.Equal("1 folder", AppNotices.Count(1, "folder", "folders"));
        Assert.Equal($"{1234:N0} folders", AppNotices.Count(1234, "folder", "folders"));
        Assert.Equal(NoticeLevel.Warning, AppNotices.FullDiskAccess("/").Level);
        Assert.Contains("Time Machine backups", AppNotices.FullDiskAccess("/").Message);
        // Dismissing a scan's notice hides it for that folder only.
        Assert.NotEqual(AppNotices.FullDiskAccess("/Volumes/A").Key, AppNotices.FullDiskAccess("/Volumes/B").Key);
        Assert.NotEqual(AppNotices.RootUnreadable("/a").Key, AppNotices.RootUnreadable("/b").Key);
        Assert.Equal("/Volumes/X could not be opened", AppNotices.RootUnreadable("/Volumes/X").Title);
        Assert.Equal("space.purgeable", AppNotices.Purgeable("the startup disk", 1, 2, Decimal).Key);
    }
}
