using Velopack;
using Velopack.Sources;

namespace AutoShazam.Services.Update;

public sealed class AppUpdateService
{
    private readonly UpdateManager _manager;

    public AppUpdateService()
    {
        _manager = new UpdateManager(new GithubSource(UpdateConfig.GithubRepoUrl, null, false));
    }

    /// <summary>True only for a genuine Velopack install (i.e. this is the shipped release build, not a local/dev run).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (!IsInstalled)
        {
            return null;
        }

        try
        {
            return await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch
        {
            // No network, no releases published yet, placeholder repo URL not updated, etc.
            // A failed startup check should never block the app from opening.
            return null;
        }
    }

    public async Task DownloadAndApplyAsync(UpdateInfo updateInfo, Action<int>? progress = null)
    {
        await _manager.DownloadUpdatesAsync(updateInfo, progress).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(updateInfo);
    }
}
