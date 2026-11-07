using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Scanning;

public sealed record ScanOptions
{
    public ScanMode Mode { get; init; } = ScanMode.Auto;
    public bool FollowReparsePoints { get; init; }
    /// <summary>0 = automatic.</summary>
    public int ThreadCount { get; init; }
}

public readonly record struct ScanProgress(
    ulong Files,
    ulong Directories,
    ulong Bytes,
    uint Errors,
    ScanPhase Phase,
    string CurrentPath);

public interface IScanner
{
    string Name { get; }

    Task<FileTree> ScanAsync(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class ScanException(EsStatus status, string message) : Exception(message)
{
    public EsStatus Status { get; } = status;
}
