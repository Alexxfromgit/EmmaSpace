namespace EmmaSpace.Core.Tests;

/// <summary>A disposable folder tree on disk for scanner and duplicate tests.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "emmaspace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relativePath, int size, byte fill = (byte)'x')
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public string File(string relativePath, byte[] content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public string Folder(string relativePath) => Directory.CreateDirectory(Path.Combine(Root, relativePath)).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
