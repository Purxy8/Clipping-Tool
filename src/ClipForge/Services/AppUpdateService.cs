using ClipForge.Models;
using Velopack;
using Velopack.Sources;

namespace ClipForge.Services;

public enum AppUpdateState
{
    Disabled,
    NotInstalled,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed
}

public sealed record AppUpdateSnapshot(
    AppUpdateState State,
    string CurrentVersion,
    string? TargetVersion = null,
    int ProgressPercent = 0,
    string? Message = null);

/// <summary>
/// Coordinates non-fatal Velopack update checks and downloads. Applying is
/// deliberately split from downloading so MainWindow can stop FFmpeg cleanly.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly UpdateManager? _manager;
    private UpdateInfo? _availableUpdate;
    private VelopackAsset? _readyAsset;
    private AppUpdateSnapshot _snapshot;
    private bool _disposed;

    public AppUpdateService()
    {
        _snapshot = new AppUpdateSnapshot(AppUpdateState.Disabled, ReleaseInfo.Version);

        if (ReleaseInfo.UpdateUrl is null)
        {
            _snapshot = _snapshot with
            {
                Message = "Update hosting has not been configured for this build."
            };
            return;
        }

        try
        {
            _manager = CreateManager(ReleaseInfo.UpdateUrl);
            if (!_manager.IsInstalled)
            {
                _snapshot = new AppUpdateSnapshot(
                    AppUpdateState.NotInstalled,
                    ReleaseInfo.Version,
                    Message: "Install ClipForge Setup to receive automatic updates.");
                return;
            }

            _readyAsset = _manager.UpdatePendingRestart;
            _snapshot = _readyAsset is null
                ? new AppUpdateSnapshot(
                    AppUpdateState.Idle,
                    _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                    Message: "ClipForge can check for new versions.")
                : new AppUpdateSnapshot(
                    AppUpdateState.ReadyToRestart,
                    _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                    _readyAsset.Version.ToString(),
                    100,
                    "An update is downloaded. Select Restart to update to install it.");
        }
        catch (Exception exception)
        {
            _snapshot = new AppUpdateSnapshot(
                AppUpdateState.Failed,
                ReleaseInfo.Version,
                Message: $"Updates could not be initialized: {exception.Message}");
        }
    }

    public event EventHandler<AppUpdateSnapshot>? StateChanged;

    public AppUpdateSnapshot Snapshot => _snapshot;

    public bool CanCheck => _manager?.IsInstalled == true;

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_manager?.IsInstalled != true)
        {
            Publish(_snapshot);
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publish(_snapshot with
            {
                State = AppUpdateState.Checking,
                ProgressPercent = 0,
                Message = "Checking for updates…"
            });

            _readyAsset = _manager.UpdatePendingRestart;
            if (_readyAsset is not null)
            {
                Publish(new AppUpdateSnapshot(
                    AppUpdateState.ReadyToRestart,
                    _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                    _readyAsset.Version.ToString(),
                    100,
                    "An update is downloaded. Select Restart to update to install it."));
                return;
            }

            _availableUpdate = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_availableUpdate is null)
            {
                Publish(new AppUpdateSnapshot(
                    AppUpdateState.UpToDate,
                    _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                    Message: "You have the latest version."));
                return;
            }

            Publish(new AppUpdateSnapshot(
                AppUpdateState.Available,
                _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                _availableUpdate.TargetFullRelease.Version.ToString(),
                Message: $"ClipForge {_availableUpdate.TargetFullRelease.Version} is available."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(_snapshot with
            {
                State = AppUpdateState.Idle,
                Message = "Update check cancelled."
            });
            throw;
        }
        catch (Exception exception)
        {
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Failed,
                _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                Message: $"Could not check for updates: {exception.Message}"));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_manager is null || _availableUpdate is null)
        {
            throw new InvalidOperationException("Check for an update before downloading it.");
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targetVersion = _availableUpdate.TargetFullRelease.Version.ToString();
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Downloading,
                _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                targetVersion,
                Message: "Downloading update…"));

            await _manager.DownloadUpdatesAsync(
                    _availableUpdate,
                    progress => Publish(new AppUpdateSnapshot(
                        AppUpdateState.Downloading,
                        _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                        targetVersion,
                        Math.Clamp(progress, 0, 100),
                        $"Downloading update… {progress}%")),
                    cancellationToken)
                .ConfigureAwait(false);

            _readyAsset = _availableUpdate.TargetFullRelease;
            Publish(new AppUpdateSnapshot(
                AppUpdateState.ReadyToRestart,
                _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                targetVersion,
                100,
                "Update downloaded. Select Restart to update to install it."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(_snapshot with
            {
                State = AppUpdateState.Available,
                Message = "Update download cancelled."
            });
            throw;
        }
        catch (Exception exception)
        {
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Failed,
                _manager.CurrentVersion?.ToString() ?? ReleaseInfo.Version,
                Message: $"Could not download the update: {exception.Message}"));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void ScheduleApplyAndRestart()
    {
        ThrowIfDisposed();
        if (_manager is null || _readyAsset is null)
        {
            throw new InvalidOperationException("No downloaded update is ready to install.");
        }

        _manager.WaitExitThenApplyUpdates(
            _readyAsset,
            silent: false,
            restart: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private static UpdateManager CreateManager(string updateUrl)
    {
        if (Path.IsPathFullyQualified(updateUrl))
        {
            return new UpdateManager(updateUrl);
        }

        if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "ClipForge updates require an HTTPS release address or a fully qualified local test path.");
        }

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateManager(new GithubSource(
                updateUrl,
                accessToken: null,
                prerelease: ShouldIncludePrereleases(ReleaseInfo.Version)));
        }

        return new UpdateManager(updateUrl);
    }

    /// <summary>
    /// Stable installations stay on stable releases, while beta installations can
    /// discover the next beta and eventually move forward to the stable build.
    /// </summary>
    internal static bool ShouldIncludePrereleases(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return version.Contains('-', StringComparison.Ordinal);
    }

    private void Publish(AppUpdateSnapshot snapshot)
    {
        _snapshot = snapshot;
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<AppUpdateSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // Update observers must not break download or apply operations.
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
