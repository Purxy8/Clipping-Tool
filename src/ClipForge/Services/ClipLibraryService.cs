using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipForge.Models;

namespace ClipForge.Services;

/// <summary>
/// Discovers clips and creates presentation metadata without trusting file names or invoking a shell.
/// </summary>
public sealed class ClipLibraryService
{
    public const int DefaultClipCount = 5;

    private const int MaximumClipCount = 100;
    private const int MaximumDiscoveryCandidates = 4096;
    private const int MinimumProbeCandidates = 20;
    private const int ProbeCandidatesPerRequestedClip = 4;
    private const long MaximumThumbnailBytes = 16 * 1024 * 1024;
    private static readonly Regex ClipForgeFileNamePattern = new(
        @"\AClip_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(?:_\d+)?\.mp4\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DefaultThumbnailTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumTotalProbeDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumTotalThumbnailDuration = TimeSpan.FromSeconds(20);

    private readonly Func<string?> _findFfmpeg;
    private readonly Func<string?> _findFfprobe;
    private readonly IClipMediaProcessRunner _processRunner;
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly string _thumbnailCacheDirectory;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _thumbnailTimeout;

    public ClipLibraryService(
        FfmpegSetupService ffmpegSetupService,
        string? thumbnailCacheDirectory = null)
        : this(
            ffmpegSetupService,
            new ClipMediaProcessRunner(),
            thumbnailCacheDirectory ?? GetDefaultThumbnailCacheDirectory(),
            DefaultProbeTimeout,
            DefaultThumbnailTimeout)
    {
    }

    internal ClipLibraryService(
        FfmpegSetupService ffmpegSetupService,
        IClipMediaProcessRunner processRunner,
        string thumbnailCacheDirectory,
        TimeSpan probeTimeout,
        TimeSpan thumbnailTimeout)
        : this(
            ffmpegSetupService.FindExecutable,
            ffmpegSetupService.FindProbeExecutable,
            processRunner,
            thumbnailCacheDirectory,
            probeTimeout,
            thumbnailTimeout)
    {
    }

    internal ClipLibraryService(
        Func<string?> findFfmpeg,
        Func<string?> findFfprobe,
        IClipMediaProcessRunner processRunner,
        string thumbnailCacheDirectory,
        TimeSpan probeTimeout,
        TimeSpan thumbnailTimeout)
    {
        ArgumentNullException.ThrowIfNull(findFfmpeg);
        ArgumentNullException.ThrowIfNull(findFfprobe);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbnailCacheDirectory);

        if (!Path.IsPathFullyQualified(thumbnailCacheDirectory))
        {
            throw new ArgumentException("The thumbnail cache directory must be absolute.", nameof(thumbnailCacheDirectory));
        }

        if (probeTimeout <= TimeSpan.Zero || probeTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        if (thumbnailTimeout <= TimeSpan.Zero || thumbnailTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(thumbnailTimeout));
        }

        _findFfmpeg = findFfmpeg;
        _findFfprobe = findFfprobe;
        _processRunner = processRunner;
        _thumbnailCacheDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(thumbnailCacheDirectory));
        _probeTimeout = probeTimeout;
        _thumbnailTimeout = thumbnailTimeout;
    }

    public static string GetDefaultThumbnailCacheDirectory() =>
        Path.Combine(SettingsService.GetDefaultSettingsDirectory(), "Cache", "Thumbnails");

    /// <summary>
    /// Returns safely discovered clips in newest-first order. The gallery fails closed when the
    /// pinned private probe is unavailable or cannot validate a candidate.
    /// </summary>
    public async Task<IReadOnlyList<ClipLibraryItem>> GetRecentClipsAsync(
        string saveDirectory,
        int count = DefaultClipCount,
        bool includeThumbnails = true,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > MaximumClipCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"Clip count must be between 1 and {MaximumClipCount}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var discovery = await Task.Run(
                () => DiscoverSafeCandidates(saveDirectory, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        if (discovery is null || discovery.Candidates.Count == 0)
        {
            return Array.Empty<ClipLibraryItem>();
        }

        var ffprobePath = GetSafeToolPath(_findFfprobe());
        if (ffprobePath is null)
        {
            return Array.Empty<ClipLibraryItem>();
        }

        var clips = new List<ClipLibraryItem>(count);
        using var probeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeBudget.CancelAfter(MaximumTotalProbeDuration);
        var maximumProbes = Math.Min(
            discovery.Candidates.Count,
            Math.Max(MinimumProbeCandidates, checked(count * ProbeCandidatesPerRequestedClip)));
        var probesStarted = 0;

        foreach (var candidate in discovery.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (probesStarted >= maximumProbes)
            {
                break;
            }

            probesStarted++;
            ProbeOutcome probe;
            try
            {
                probe = await ProbeAsync(
                        ffprobePath,
                        candidate.FullPath,
                        probeBudget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                probeBudget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (probe.State != ProbeState.Valid)
            {
                continue;
            }

            clips.Add(new ClipLibraryItem(
                candidate.FileName,
                candidate.FullPath,
                new DateTimeOffset(candidate.LastWriteTimeUtc),
                candidate.Length,
                probe.Duration));

            if (clips.Count == count)
            {
                break;
            }
        }

        if (includeThumbnails && clips.Count > 0)
        {
            using var thumbnailBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            thumbnailBudget.CancelAfter(MaximumTotalThumbnailDuration);
            for (var index = 0; index < clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? thumbnail;
                try
                {
                    thumbnail = await GetThumbnailAsync(
                            discovery.RootDirectory,
                            clips[index],
                            thumbnailBudget.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    thumbnailBudget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                clips[index] = clips[index] with { ThumbnailPath = thumbnail };
            }
        }

        return new ReadOnlyCollection<ClipLibraryItem>(clips);
    }

    public async Task<ClipLibrarySnapshot> LoadAsync(
        string saveDirectory,
        int count = DefaultClipCount,
        bool includeThumbnails = true,
        CancellationToken cancellationToken = default)
    {
        var clips = await GetRecentClipsAsync(
                saveDirectory,
                count,
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        return clips.Count == 0 ? ClipLibrarySnapshot.Empty : new ClipLibrarySnapshot(clips);
    }

    /// <summary>
    /// Revalidates a discovered clip immediately before it is handed to an in-process media decoder.
    /// </summary>
    internal static bool IsCurrentClipSafe(string saveDirectory, ClipLibraryItem clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var rootDirectory = TryGetSafeRootDirectory(saveDirectory);
        return rootDirectory is not null &&
               TryGetCurrentCandidate(rootDirectory, clip.FullPath, out var candidate) &&
               candidate.FileName.Equals(clip.FileName, StringComparison.Ordinal) &&
               candidate.Length == clip.FileSizeBytes &&
               candidate.LastWriteTimeUtc == clip.RecordedAtUtc.UtcDateTime;
    }

    /// <summary>
    /// Returns a cached JPEG path or null. The clip is revalidated against its configured root and
    /// recorded file identity before any helper process is launched.
    /// </summary>
    public async Task<string?> GetThumbnailAsync(
        string saveDirectory,
        ClipLibraryItem clip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();

        var rootDirectory = TryGetSafeRootDirectory(saveDirectory);
        if (rootDirectory is null ||
            !TryGetCurrentCandidate(rootDirectory, clip.FullPath, out var candidate) ||
            !candidate.FileName.Equals(clip.FileName, StringComparison.Ordinal) ||
            candidate.Length != clip.FileSizeBytes ||
            candidate.LastWriteTimeUtc != clip.RecordedAtUtc.UtcDateTime)
        {
            return null;
        }

        var ffmpegPath = GetSafeToolPath(_findFfmpeg());
        if (ffmpegPath is null || !TryPrepareCacheDirectory())
        {
            return null;
        }

        var thumbnailPath = GetDeterministicThumbnailPath(candidate);
        if (IsUsableThumbnail(thumbnailPath))
        {
            return thumbnailPath;
        }

        await _thumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingPath = null;

        try
        {
            if (IsUsableThumbnail(thumbnailPath))
            {
                return thumbnailPath;
            }

            if (!TryPrepareCacheDirectory() ||
                !TryGetCurrentCandidate(rootDirectory, clip.FullPath, out var currentCandidate) ||
                currentCandidate.Length != candidate.Length ||
                currentCandidate.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
            {
                return null;
            }

            if (IsReparsePoint(thumbnailPath))
            {
                return null;
            }

            stagingPath = Path.Combine(
                _thumbnailCacheDirectory,
                $".{Path.GetFileNameWithoutExtension(thumbnailPath)}.{Guid.NewGuid():N}.tmp.jpg");
            var arguments = BuildThumbnailArguments(
                currentCandidate.FullPath,
                stagingPath,
                clip.Duration);

            ClipMediaProcessResult result;
            try
            {
                result = await _processRunner.RunAsync(
                        ffmpegPath,
                        arguments,
                        _thumbnailTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedMediaException(exception))
            {
                return null;
            }

            if (!result.Succeeded || !IsUsableThumbnail(stagingPath))
            {
                return null;
            }

            if (!TryPrepareCacheDirectory() || IsReparsePoint(thumbnailPath))
            {
                return null;
            }

            TryRemoveInvalidRegularThumbnail(thumbnailPath);
            try
            {
                File.Move(stagingPath, thumbnailPath, overwrite: false);
                stagingPath = null;
            }
            catch (IOException) when (IsUsableThumbnail(thumbnailPath))
            {
                // Another ClipForge process atomically populated the same deterministic cache key.
            }

            return IsUsableThumbnail(thumbnailPath) ? thumbnailPath : null;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }
        finally
        {
            if (stagingPath is not null)
            {
                TryDeleteFile(stagingPath);
            }

            _thumbnailGate.Release();
        }
    }

    internal static bool IsSafeTopLevelClipPath(
        string rootDirectory,
        string candidatePath,
        FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !Path.IsPathFullyQualified(rootDirectory) ||
            !Path.IsPathFullyQualified(candidatePath) ||
            !ClipForgeFileNamePattern.IsMatch(Path.GetFileName(candidatePath)))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            var normalizedCandidate = Path.GetFullPath(candidatePath);
            var parent = Path.GetDirectoryName(normalizedCandidate);
            if (!normalizedRoot.Equals(parent, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
            return !relativePath.Equals(".", StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relativePath) &&
                   !relativePath.Contains(Path.DirectorySeparatorChar) &&
                   !relativePath.Contains(Path.AltDirectorySeparatorChar) &&
                   !relativePath.Equals("..", StringComparison.Ordinal) &&
                   !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> BuildProbeArguments(string clipPath) =>
    [
        "-v", "error",
        "-protocol_whitelist", "file",
        "-f", "mov",
        "-select_streams", "v:0",
        "-show_entries", "stream=codec_type:format=duration",
        "-of", "json",
        clipPath
    ];

    internal static IReadOnlyList<string> BuildThumbnailArguments(
        string clipPath,
        string outputPath,
        TimeSpan? duration)
    {
        var seekSeconds = duration is { } knownDuration && knownDuration > TimeSpan.Zero
            ? Math.Min(1, knownDuration.TotalSeconds * 0.1)
            : 0.5;

        return
        [
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-y",
            "-threads", "1",
            "-protocol_whitelist", "file",
            "-f", "mov",
            "-ss", seekSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", clipPath,
            "-map", "0:v:0",
            "-frames:v", "1",
            "-vf", "scale=640:-2:force_original_aspect_ratio=decrease",
            "-an",
            "-sn",
            "-dn",
            "-c:v", "mjpeg",
            "-q:v", "4",
            "-f", "image2",
            "-update", "1",
            outputPath
        ];
    }

    private async Task<ProbeOutcome> ProbeAsync(
        string ffprobePath,
        string clipPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                    ffprobePath,
                    BuildProbeArguments(clipPath),
                    _probeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.TimedOut)
            {
                return ProbeOutcome.Invalid;
            }

            if (!result.Succeeded)
            {
                return ProbeOutcome.Invalid;
            }

            return ParseProbeOutput(result.StandardOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedMediaException(exception))
        {
            return ProbeOutcome.Invalid;
        }
    }

    private static ProbeOutcome ParseProbeOutput(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array ||
                streams.GetArrayLength() == 0)
            {
                return ProbeOutcome.Invalid;
            }

            TimeSpan? duration = null;
            if (document.RootElement.TryGetProperty("format", out var format) &&
                format.ValueKind == JsonValueKind.Object &&
                format.TryGetProperty("duration", out var durationElement) &&
                TryReadPositiveSeconds(durationElement, out var seconds))
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            return new ProbeOutcome(ProbeState.Valid, duration);
        }
        catch (JsonException)
        {
            return ProbeOutcome.Invalid;
        }
        catch (OverflowException)
        {
            return ProbeOutcome.Invalid;
        }
    }

    private static bool TryReadPositiveSeconds(JsonElement element, out double seconds)
    {
        var parsed = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out var number) ? number : double.NaN,
            JsonValueKind.String => double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var textNumber)
                ? textNumber
                : double.NaN,
            _ => double.NaN
        };

        if (double.IsFinite(parsed) && parsed > 0 && parsed <= TimeSpan.MaxValue.TotalSeconds)
        {
            seconds = parsed;
            return true;
        }

        seconds = 0;
        return false;
    }

    private static DiscoveryResult? DiscoverSafeCandidates(
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        var rootDirectory = TryGetSafeRootDirectory(saveDirectory);
        if (rootDirectory is null)
        {
            return null;
        }

        var newestCandidates = new PriorityQueue<ClipCandidate, long>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetCurrentCandidate(rootDirectory, path, out var candidate))
                {
                    continue;
                }

                newestCandidates.Enqueue(candidate, candidate.LastWriteTimeUtc.Ticks);
                if (newestCandidates.Count > MaximumDiscoveryCandidates)
                {
                    newestCandidates.Dequeue();
                }
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }

        var ordered = newestCandidates.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(item => item.LastWriteTimeUtc)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DiscoveryResult(rootDirectory, ordered);
    }

    private static bool TryGetCurrentCandidate(
        string rootDirectory,
        string path,
        out ClipCandidate candidate)
    {
        candidate = default;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists ||
                info.Length <= 0 ||
                !IsSafeTopLevelClipPath(rootDirectory, fullPath, info.Attributes))
            {
                return false;
            }

            candidate = new ClipCandidate(
                info.Name,
                fullPath,
                info.Length,
                DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc));
            return true;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return false;
        }
    }

    private static string? TryGetSafeRootDirectory(string saveDirectory)
    {
        if (string.IsNullOrWhiteSpace(saveDirectory) || !Path.IsPathFullyQualified(saveDirectory))
        {
            return null;
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveDirectory));
            var info = new DirectoryInfo(fullPath);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0
                ? fullPath
                : null;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }
    }

    private static string? GetSafeToolPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            return info.Exists &&
                   info.Length > 0 &&
                   (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                ? info.FullName
                : null;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }
    }

    private bool TryPrepareCacheDirectory()
    {
        try
        {
            Directory.CreateDirectory(_thumbnailCacheDirectory);
            var info = new DirectoryInfo(_thumbnailCacheDirectory);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return false;
        }
    }

    private string GetDeterministicThumbnailPath(ClipCandidate candidate)
    {
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{candidate.FullPath.ToUpperInvariant()}\n{candidate.Length}\n{candidate.LastWriteTimeUtc.Ticks}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_thumbnailCacheDirectory, $"{hash}.jpg");
    }

    private static bool IsUsableThumbnail(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists ||
                info.Length < 5 ||
                info.Length > MaximumThumbnailBytes ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4,
                FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[3];
            if (stream.Read(header) != header.Length ||
                header[0] != 0xFF ||
                header[1] != 0xD8 ||
                header[2] != 0xFF)
            {
                return false;
            }

            stream.Seek(-2, SeekOrigin.End);
            return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD9;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.Exists(path) &&
                   (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return true;
        }
    }

    private static void TryRemoveInvalidRegularThumbnail(string path)
    {
        try
        {
            if (File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                !IsUsableThumbnail(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            // A stale cache entry only prevents regeneration of this optional thumbnail.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!IsReparsePoint(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            // Unique staging files are best-effort cleanup after a failed helper process.
        }
    }

    private static bool IsExpectedMediaException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            Win32Exception or JsonException or NotSupportedException or SecurityException;

    private static bool IsExpectedFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SecurityException;

    private readonly record struct ClipCandidate(
        string FileName,
        string FullPath,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record DiscoveryResult(
        string RootDirectory,
        IReadOnlyList<ClipCandidate> Candidates);

    private enum ProbeState
    {
        Valid,
        Invalid
    }

    private readonly record struct ProbeOutcome(ProbeState State, TimeSpan? Duration)
    {
        public static ProbeOutcome Invalid { get; } = new(ProbeState.Invalid, null);
    }
}
