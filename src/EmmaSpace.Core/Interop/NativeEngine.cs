namespace EmmaSpace.Core.Interop;

/// <summary>Reports whether the native engine (emmascan) can be loaded in this process.</summary>
public static class NativeEngine
{
    private static readonly Lazy<bool> s_available = new(() =>
    {
        try
        {
            return NativeMethods.GetVersion() == NativeMethods.AbiVersion;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    });

    public static bool IsAvailable => s_available.Value;

    public static bool IsElevated => IsAvailable ? NativeMethods.IsElevated() != 0 : Environment.IsPrivilegedProcess;
}
