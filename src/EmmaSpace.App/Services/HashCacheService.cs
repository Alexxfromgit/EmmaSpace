using EmmaSpace.Core.Analysis;

namespace EmmaSpace.App.Services;

/// <summary>
/// The duplicate search's memory of file hashes, shared by every search in this session. It lives in the
/// local (not roaming) application data folder, since it can grow to a few tens of megabytes.
/// </summary>
public sealed class HashCacheService(SettingsService settings)
{
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmmaSpace", "hash-cache.bin");

    private readonly Lock _lock = new();
    private Task<FileHashCache>? _load;

    /// <summary>Whether searches use and fill the cache ("Remember file fingerprints" in Settings).</summary>
    public bool Enabled
    {
        get => settings.Current.UseHashCache;
        set
        {
            settings.Current.UseHashCache = value;
            settings.Save();
        }
    }

    /// <summary>The cache, read from disk the first time (off the UI thread); null while it is turned off.</summary>
    public async Task<FileHashCache?> GetAsync()
    {
        if (!Enabled) return null;
        Task<FileHashCache> load;
        lock (_lock) load = _load ??= Task.Run(() => FileHashCache.Load(s_path));
        return await load;
    }

    /// <summary>Forgets every remembered hash and deletes the file, whether or not it was loaded.</summary>
    public async Task ClearAsync()
    {
        // Not loaded yet: an empty cache takes the loaded one's place, so a search starting meanwhile uses that one
        // instead of reading the file this is about to delete.
        Task<FileHashCache> load;
        lock (_lock) load = _load ??= Task.FromResult(new FileHashCache(s_path));
        var cache = await load;
        await Task.Run(cache.Clear);
    }
}
