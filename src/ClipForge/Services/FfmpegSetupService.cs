using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ClipForge.Services;

/// <summary>
/// Locates or installs the private FFmpeg binaries used by ClipForge.
/// </summary>
public sealed class FfmpegSetupService
{
    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip";
    private const string ExpectedArchiveSha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";
    private const string ExecutableName = "ffmpeg.exe";
    private const string ProbeExecutableName = "ffprobe.exe";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly string _installDirectory;

    public FfmpegSetupService(string? installDirectory = null)
    {
        _installDirectory = installDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipForge",
            "Tools",
            "FFmpeg");
    }

    public string? FindExecutable()
    {
        var configuredPath = Environment.GetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH");
        foreach (var candidate in GetExecutableCandidates(configuredPath, ExecutableName))
        {
            if (IsUsableFile(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public async Task<string> DownloadAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryDirectory = null;

        try
        {
            var existing = FindExecutable();
            if (existing is not null)
            {
                progress?.Report(1);
                return existing;
            }

            var parentDirectory = Path.GetDirectoryName(_installDirectory)
                ?? throw new InvalidOperationException("The FFmpeg installation directory is invalid.");
            Directory.CreateDirectory(parentDirectory);

            temporaryDirectory = Path.Combine(parentDirectory, $".ffmpeg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var archivePath = Path.Combine(temporaryDirectory, "ffmpeg.zip");

            progress?.Report(0);
            using (var response = await HttpClient.GetAsync(
                       DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var destination = new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await CopyWithProgressAsync(
                        source,
                        destination,
                        totalBytes,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await VerifyArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
            var extractedFfmpeg = Path.Combine(temporaryDirectory, ExecutableName);
            var extractedFfprobe = Path.Combine(temporaryDirectory, ProbeExecutableName);
            await ExtractExecutableAsync(archivePath, ExecutableName, extractedFfmpeg, cancellationToken)
                .ConfigureAwait(false);
            await ExtractExecutableAsync(archivePath, ProbeExecutableName, extractedFfprobe, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(0.98);

            Directory.CreateDirectory(_installDirectory);
            var installedFfmpeg = Path.Combine(_installDirectory, ExecutableName);
            var installedFfprobe = Path.Combine(_installDirectory, ProbeExecutableName);
            File.Move(extractedFfmpeg, installedFfmpeg, overwrite: true);
            File.Move(extractedFfprobe, installedFfprobe, overwrite: true);

            if (!IsUsableFile(installedFfmpeg))
            {
                throw new InvalidDataException("The downloaded FFmpeg executable is empty or missing.");
            }

            progress?.Report(1);
            return installedFfmpeg;
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                TryDeleteDirectory(temporaryDirectory);
            }

            _downloadGate.Release();
        }
    }

    internal string? FindProbeExecutable()
    {
        var ffmpeg = FindExecutable();
        if (ffmpeg is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, ProbeExecutableName);
            if (IsUsableFile(sibling))
            {
                return sibling;
            }
        }

        return GetExecutableCandidates(null, ProbeExecutableName).FirstOrDefault(IsUsableFile);
    }

    private IEnumerable<string> GetExecutableCandidates(string? configuredPath, string executableName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return Directory.Exists(configuredPath)
                ? Path.Combine(configuredPath, executableName)
                : configuredPath;
        }

        yield return Path.Combine(_installDirectory, executableName);
        yield return Path.Combine(AppContext.BaseDirectory, executableName);
        yield return Path.Combine(AppContext.BaseDirectory, "Tools", "FFmpeg", executableName);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedDirectory = directory.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(trimmedDirectory))
            {
                yield return Path.Combine(trimmedDirectory, executableName);
            }
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copiedBytes = 0;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
            copiedBytes += bytesRead;

            if (totalBytes is > 0)
            {
                progress?.Report(Math.Min(0.9, copiedBytes / (double)totalBytes.Value * 0.9));
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractExecutableAsync(
        string archivePath,
        string executableName,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var suffix = $"/bin/{executableName}";
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidDataException($"The FFmpeg archive did not contain {executableName}.");
        }

        await using var source = entry.Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!actualHash.Equals(ExpectedArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The FFmpeg download did not match ClipForge's verified checksum. Installation was cancelled.");
        }
    }

    private static bool IsUsableFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later installation can safely ignore this uniquely named staging directory.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClipForge", ReleaseInfo.Version));
        return client;
    }
}
