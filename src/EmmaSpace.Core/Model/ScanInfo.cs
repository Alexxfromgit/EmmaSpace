using EmmaSpace.Core.Interop;

namespace EmmaSpace.Core.Model;

public enum ScanSource
{
    Native,
    Managed,
    Snapshot,
}

public sealed record ScanInfo(
    string RootPath,
    DateTimeOffset ScannedAt,
    TimeSpan Elapsed,
    ScanMode Mode,
    ScanSource Source,
    int Errors);
