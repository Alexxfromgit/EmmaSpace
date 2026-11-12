using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EmmaSpace.Core.Interop;

internal static unsafe partial class NativeMethods
{
    private const string Library = "emmascan";
    public const uint AbiVersion = 3;
    public const uint FollowReparse = 0x1;

    [LibraryImport(Library, EntryPoint = "es_get_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GetVersion();

    [LibraryImport(Library, EntryPoint = "es_is_elevated")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int IsElevated();

    [LibraryImport(Library, EntryPoint = "es_scan_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus ScanStart(EsScanOptions* options, out ScanHandle scan);

    [LibraryImport(Library, EntryPoint = "es_scan_get_progress")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus ScanGetProgress(ScanHandle scan, EsProgress* progress, char* pathBuffer, uint pathBufferLength);

    [LibraryImport(Library, EntryPoint = "es_scan_wait")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus ScanWait(ScanHandle scan, uint timeoutMs);

    [LibraryImport(Library, EntryPoint = "es_scan_cancel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ScanCancel(ScanHandle scan);

    [LibraryImport(Library, EntryPoint = "es_scan_get_result")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus ScanGetResult(ScanHandle scan, EsResult* result);

    [LibraryImport(Library, EntryPoint = "es_scan_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ScanFree(nint scan);

    [LibraryImport(Library, EntryPoint = "es_move_to_trash", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus MoveToTrash(string path);

    [LibraryImport(Library, EntryPoint = "es_get_storage_kind", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus GetStorageKind(string path, out uint kind, out ulong device);

    [LibraryImport(Library, EntryPoint = "es_get_volume_space", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus GetVolumeSpace(string path, out EsVolumeSpace space);

    [LibraryImport(Library, EntryPoint = "es_get_file_stamp")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial EsStatus GetFileStamp(nint handle, out EsFileStamp stamp);
}

internal sealed class ScanHandle() : SafeHandle(nint.Zero, ownsHandle: true)
{
    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.ScanFree(handle);
        return true;
    }
}
