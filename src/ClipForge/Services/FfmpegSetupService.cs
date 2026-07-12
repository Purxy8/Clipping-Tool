using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;

namespace ClipForge.Services;

/// <summary>
/// Locates or installs the private FFmpeg binaries used by ClipForge.
/// </summary>
public sealed class FfmpegSetupService
{
    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip";
    private const string ExpectedArchiveSha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";
    private const string ExpectedFfmpegSha256 = "1326dde4c84ff1f96fe6b8916c5bed29e163e9b5dccf995f6f3db069d143ec5e";
    private const string ExpectedFfprobeSha256 = "b49ccc7c6547b141ad5a2f6ec69cc04323d7133d7704d70b331b904c63eecb07";
    private const string ExecutableName = "ffmpeg.exe";
    private const string ProbeExecutableName = "ffprobe.exe";
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExecutableBytes = 192L * 1024 * 1024;
    private const long MinimumInstallFreeBytes =
        MaximumArchiveBytes + (2 * MaximumExecutableBytes) + (64L * 1024 * 1024);
    private const int MaximumCompressionRatio = 20;

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly object _verificationGate = new();
    private readonly Dictionary<string, VerifiedToolStamp> _verifiedTools =
        new(StringComparer.OrdinalIgnoreCase);
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
        if (IsExternalToolDeveloperModeEnabled())
        {
            var configuredPath = Environment.GetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH");
            foreach (var candidate in GetExternalToolCandidates(configuredPath, ExecutableName))
            {
                if (IsUsableRegularFile(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        foreach (var candidate in GetPrivateToolCandidates(ExecutableName))
        {
            if (IsVerifiedTool(candidate, ExpectedFfmpegSha256))
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
            EnsureSafeDirectoryChain(parentDirectory);
            Directory.CreateDirectory(parentDirectory);
            EnsureSafeDirectoryChain(parentDirectory);
            EnsureSufficientFreeSpace(parentDirectory);

            temporaryDirectory = Path.Combine(parentDirectory, $".ffmpeg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            EnsureSafeDirectoryChain(temporaryDirectory);
            var archivePath = Path.Combine(temporaryDirectory, "ffmpeg.zip");

            progress?.Report(0);
            using (var response = await HttpClient.GetAsync(
                       DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is null ||
                    !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(finalUri.UserInfo))
                {
                    throw new InvalidDataException("The FFmpeg download redirected to an unsafe address.");
                }

                var totalBytes = response.Content.Headers.ContentLength;
                if (totalBytes is null or <= 0 || totalBytes > MaximumArchiveBytes)
                {
                    throw new InvalidDataException(
                        $"The FFmpeg server returned an invalid download size. The limit is {MaximumArchiveBytes / (1024 * 1024)} MB.");
                }

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
                        MaximumArchiveBytes,
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
            if (!IsVerifiedTool(extractedFfmpeg, ExpectedFfmpegSha256) ||
                !IsVerifiedTool(extractedFfprobe, ExpectedFfprobeSha256))
            {
                throw new InvalidDataException(
                    "The extracted FFmpeg tools did not match ClipForge's verified executable checksums.");
            }

            progress?.Report(0.98);

            EnsureSafeDirectoryChain(_installDirectory);
            Directory.CreateDirectory(_installDirectory);
            EnsureSafeDirectoryChain(_installDirectory);
            var installedFfmpeg = Path.Combine(_installDirectory, ExecutableName);
            var installedFfprobe = Path.Combine(_installDirectory, ProbeExecutableName);
            File.Move(extractedFfmpeg, installedFfmpeg, overwrite: true);
            File.Move(extractedFfprobe, installedFfprobe, overwrite: true);

            lock (_verificationGate)
            {
                _verifiedTools.Remove(installedFfmpeg);
                _verifiedTools.Remove(installedFfprobe);
            }

            if (!IsVerifiedTool(installedFfmpeg, ExpectedFfmpegSha256) ||
                !IsVerifiedTool(installedFfprobe, ExpectedFfprobeSha256))
            {
                throw new InvalidDataException("The installed FFmpeg tools failed their checksum verification.");
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
        if (IsExternalToolDeveloperModeEnabled())
        {
            var configuredPath = Environment.GetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH");
            foreach (var candidate in GetExternalToolCandidates(configuredPath, ProbeExecutableName))
            {
                if (IsUsableRegularFile(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        var ffmpeg = FindExecutable();
        if (ffmpeg is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, ProbeExecutableName);
            if (IsExternalToolDeveloperModeEnabled() && IsUsableRegularFile(sibling))
            {
                return sibling;
            }

            if (IsVerifiedTool(sibling, ExpectedFfprobeSha256))
            {
                return sibling;
            }
        }

        return GetPrivateToolCandidates(ProbeExecutableName)
            .FirstOrDefault(candidate => IsVerifiedTool(candidate, ExpectedFfprobeSha256));
    }

    private IEnumerable<string> GetPrivateToolCandidates(string executableName)
    {
        yield return Path.Combine(_installDirectory, executableName);
        yield return Path.Combine(AppContext.BaseDirectory, executableName);
        yield return Path.Combine(AppContext.BaseDirectory, "Tools", "FFmpeg", executableName);
    }

    private static IEnumerable<string> GetExternalToolCandidates(
        string? configuredPath,
        string executableName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (Directory.Exists(configuredPath))
            {
                yield return Path.Combine(configuredPath, executableName);
            }
            else if (Path.GetFileName(configuredPath).Equals(
                         executableName,
                         StringComparison.OrdinalIgnoreCase))
            {
                yield return configuredPath;
            }
            else
            {
                var configuredDirectory = Path.GetDirectoryName(configuredPath);
                if (!string.IsNullOrWhiteSpace(configuredDirectory))
                {
                    yield return Path.Combine(configuredDirectory, executableName);
                }
            }
        }

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

    internal static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        long maximumBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copiedBytes = 0;

        if (maximumBytes <= 0 || totalBytes is null or <= 0 || totalBytes > maximumBytes)
        {
            throw new InvalidDataException("The FFmpeg download size is missing or exceeds the configured limit.");
        }

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            if (copiedBytes > maximumBytes - bytesRead)
            {
                throw new InvalidDataException("The FFmpeg download exceeded the configured byte limit.");
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


        if (entry.Length is <= 0 or > MaximumExecutableBytes ||
            entry.CompressedLength <= 0 ||
            entry.Length / (double)entry.CompressedLength > MaximumCompressionRatio)
        {
            throw new InvalidDataException(
                $"The {executableName} archive entry exceeded ClipForge's extraction limits.");
        }

        await using var source = entry.Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyWithHardLimitAsync(
                source,
                destination,
                MaximumExecutableBytes,
                cancellationToken)
            .ConfigureAwait(false);
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

    private bool IsVerifiedTool(string path, string expectedSha256)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists ||
                info.Length is <= 0 or > MaximumExecutableBytes ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            EnsureSafeDirectoryChain(info.DirectoryName!);
            var stamp = new VerifiedToolStamp(info.Length, info.LastWriteTimeUtc, expectedSha256);
            lock (_verificationGate)
            {
                if (_verifiedTools.TryGetValue(fullPath, out var verified) && verified == stamp)
                {
                    return true;
                }
            }

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!actualHash.Equals(expectedSha256, StringComparison.Ordinal))
            {
                return false;
            }

            lock (_verificationGate)
            {
                _verifiedTools[fullPath] = stamp;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsUsableRegularFile(string path)
    {
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            return info.Exists &&
                   info.Length > 0 &&
                   (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsExternalToolDeveloperModeEnabled() =>
        Environment.GetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE")
            ?.Equals("1", StringComparison.Ordinal) == true;

    private static void EnsureSafeDirectoryChain(string directoryPath)
    {
        if (!ReplayBufferService.IsSafeBufferRootPath(directoryPath))
        {
            throw new InvalidOperationException(
                "The FFmpeg tool path or one of its parent folders is a junction or symbolic link.");
        }
    }

    private static void EnsureSufficientFreeSpace(string directoryPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directoryPath));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The FFmpeg installation drive could not be determined.");
        }

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < MinimumInstallFreeBytes)
        {
            throw new IOException(
                $"ClipForge needs at least {MinimumInstallFreeBytes / (1024 * 1024)} MB free to install the capture engine safely.");
        }
    }

    private static async Task CopyWithHardLimitAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copiedBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }

            if (copiedBytes > maximumBytes - bytesRead)
            {
                throw new InvalidDataException("An FFmpeg archive entry exceeded the extraction byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
            copiedBytes += bytesRead;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && ReplayBufferService.IsSafeBufferRootPath(path))
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

    private readonly record struct VerifiedToolStamp(
        long Length,
        DateTime LastWriteTimeUtc,
        string ExpectedSha256);
}
