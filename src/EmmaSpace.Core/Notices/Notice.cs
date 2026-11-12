namespace EmmaSpace.Core.Notices;

/// <summary>How a notice reads: news, a limit of what Emma Space can do (and why), or something to act on.</summary>
public enum NoticeLevel
{
    Info,
    Limitation,
    Warning,
}

/// <summary>
/// A button on a notice. <see cref="Id"/> is one of <see cref="NoticeActions"/>; the app decides what it does, with
/// <see cref="Argument"/> when the action needs one (the device to mount).
/// </summary>
public sealed record NoticeAction(string Id, string Label, string? Argument = null);

/// <summary>
/// Something Emma Space wants the user to know: what it noticed, what it can't do and why, and what the user can do
/// about it. The app shows notices on the start page and above scan results, and hands each new one to anyone listening
/// (the Emma companion can voice them), so nothing is skipped or hidden without a word.
/// </summary>
/// <param name="Key">
/// Stable identifier from a fixed vocabulary ("space.purgeable", "drive.locked:&lt;uuid&gt;"…), for telling notices apart
/// and remembering which ones were dismissed or already voiced.
/// </param>
/// <param name="Title">One line: what is going on.</param>
/// <param name="Message">A few sentences: why, and what it means for what Emma Space shows.</param>
public sealed record Notice(string Key, NoticeLevel Level, string Title, string Message)
{
    /// <summary>The full explanation behind "Learn more"; null when the message says it all.</summary>
    public string? Details { get; init; }

    public IReadOnlyList<NoticeAction> Actions { get; init; } = [];

    /// <summary>The user may hide it for the rest of the session.</summary>
    public bool CanDismiss { get; init; }
}

/// <summary>The actions a notice can offer.</summary>
public static class NoticeActions
{
    /// <summary>Shows <see cref="Notice.Details"/>.</summary>
    public const string LearnMore = "learn-more";
    public const string OpenStorageSettings = "open-storage-settings";
    public const string OpenTimeMachineSettings = "open-time-machine-settings";
    public const string OpenDiskUtility = "open-disk-utility";
    public const string OpenPrivacySettings = "open-privacy-settings";

    /// <summary>Mounts the volume named by the action's argument (a BSD device such as "disk15s2"), read-write as usual.</summary>
    public const string MountVolume = "mount-volume";

    public static NoticeAction LearnMoreAction { get; } = new(LearnMore, "Learn more");
}
