using EmmaSpace.Core.Interop;
using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Scanning;

/// <summary>Uses the native engine when it is available and supports this platform, otherwise the managed scanner.</summary>
public sealed class DefaultScanner(NativeScanner native, ManagedScanner managed) : IScanner
{
    public DefaultScanner() : this(new NativeScanner(), new ManagedScanner())
    {
    }

    public string Name => NativeEngine.IsAvailable ? native.Name : managed.Name;

    public async Task<FileTree> ScanAsync(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!NativeEngine.IsAvailable) return await managed.ScanAsync(rootPath, options, progress, cancellationToken);
        try
        {
            return await native.ScanAsync(rootPath, options, progress, cancellationToken);
        }
        catch (ScanException ex) when (ex.Status == EsStatus.NotSupported && options.Mode != ScanMode.Mft)
        {
            return await managed.ScanAsync(rootPath, options, progress, cancellationToken);
        }
    }
}
