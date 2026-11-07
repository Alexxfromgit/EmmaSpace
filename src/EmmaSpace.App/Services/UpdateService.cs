using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace EmmaSpace.App.Services;

/// <summary>
/// Checks GitHub Releases for a newer Emma Space and stages it with Velopack. Each installed package
/// knows its own channel (win-x64, osx-arm64, …), so it only ever sees builds for its platform.
/// Only Velopack-installed copies update; a <c>dotnet run</c> build reports <see cref="IsInstalled"/> false.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/Alexxfromgit/EmmaSpace";

    private readonly UpdateManager _manager = new(CreateSource());

    /// <summary>Version for display, e.g. "0.2.0" (the build metadata after '+' is dropped).</summary>
    public static string CurrentVersion { get; } = ReadVersion();

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>An update that was downloaded earlier and is applied on restart (or the next start).</summary>
    public string? PendingVersion => _manager.UpdatePendingRestart?.Version?.ToString();

    /// <summary>
    /// Looks for a newer release and downloads it. Returns the staged version, or null when up to date.
    /// Throws on network or feed errors, so callers decide whether to stay quiet about them.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync(CancellationToken cancel = default)
    {
        if (!IsInstalled) return null;
        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null) return null;
        await _manager.DownloadUpdatesAsync(update, progress: null, cancelToken: cancel).ConfigureAwait(false);
        return update.TargetFullRelease.Version.ToString();
    }

    /// <summary>Exits, installs the staged update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_manager.UpdatePendingRestart is { } pending) _manager.ApplyUpdatesAndRestart(pending);
    }

    public static string ReleaseNotesUrl(string version) => $"{RepositoryUrl}/releases/tag/v{version}";

    /// <summary>
    /// GitHub Releases of this repository. For testing, EMMASPACE_UPDATE_FEED can name a local folder of
    /// vpk output (or an HTTP feed), and EMMASPACE_GITHUB_TOKEN lets a tester read a private repository.
    /// </summary>
    private static IUpdateSource CreateSource()
    {
        var feed = Environment.GetEnvironmentVariable("EMMASPACE_UPDATE_FEED");
        if (!string.IsNullOrWhiteSpace(feed))
        {
            Log($"Using update feed {feed}");
            return Directory.Exists(feed) ? new SimpleFileSource(new DirectoryInfo(feed)) : new SimpleWebSource(feed);
        }

        var token = Environment.GetEnvironmentVariable("EMMASPACE_GITHUB_TOKEN");
        return new GithubSource(RepositoryUrl, string.IsNullOrWhiteSpace(token) ? null : token, prerelease: false);
    }

    private static string ReadVersion()
    {
        var info = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus];
    }

    internal static void Log(string message) => Trace.WriteLine($"[updates] {message}");
}
