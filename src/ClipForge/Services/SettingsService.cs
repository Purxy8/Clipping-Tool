using System.IO;
using System.Text.Json;
using ClipForge.Models;

namespace ClipForge.Services;

/// <summary>
/// Persists the user's ClipForge preferences in their local application data folder.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private const string SettingsFileName = "settings.json";
    private const long MaximumSettingsBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeGate = new();
    private int _activeOperations;
    private bool _disposed;

    public SettingsService(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? GetDefaultSettingsDirectory();

        if (string.IsNullOrWhiteSpace(SettingsDirectory))
        {
            throw new ArgumentException("A settings directory is required.", nameof(settingsDirectory));
        }

        SettingsPath = Path.Combine(SettingsDirectory, SettingsFileName);
    }

    public string SettingsDirectory { get; }

    public string SettingsPath { get; }

    public static string GetDefaultSettingsDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "ClipForge");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        BeginOperation();
        var gateAcquired = false;

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return new AppSettings();
                }

                if (new FileInfo(SettingsPath).Length > MaximumSettingsBytes)
                {
                    return new AppSettings();
                }

                await using var stream = new FileStream(
                    SettingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                return await JsonSerializer.DeserializeAsync<AppSettings>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? new AppSettings();
            }
            catch (JsonException)
            {
                // A partially written or manually edited file should not prevent startup.
                return new AppSettings();
            }
            catch (NotSupportedException)
            {
                return new AppSettings();
            }
            catch (IOException)
            {
                // A temporary lock, concurrent replacement, or unavailable local
                // profile should not prevent the application from starting.
                return new AppSettings();
            }
            catch (UnauthorizedAccessException)
            {
                return new AppSettings();
            }
            catch (System.Security.SecurityException)
            {
                return new AppSettings();
            }
        }
        finally
        {
            if (gateAcquired)
            {
                _gate.Release();
            }

            EndOperation();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        BeginOperation();
        var gateAcquired = false;
        string? temporaryPath = null;

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

            Directory.CreateDirectory(SettingsDirectory);
            temporaryPath = Path.Combine(SettingsDirectory, $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the uniquely named file can be replaced on a later save.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the original persistence error if cleanup is denied.
                }
            }

            if (gateAcquired)
            {
                _gate.Release();
            }

            EndOperation();
        }
    }

    public void Dispose()
    {
        var disposeSemaphore = false;
        lock (_disposeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            disposeSemaphore = _activeOperations == 0;
        }

        if (disposeSemaphore)
        {
            _gate.Dispose();
        }
    }

    private void BeginOperation()
    {
        lock (_disposeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        var disposeSemaphore = false;
        lock (_disposeGate)
        {
            _activeOperations--;
            disposeSemaphore = _disposed && _activeOperations == 0;
        }

        if (disposeSemaphore)
        {
            _gate.Dispose();
        }
    }
}
