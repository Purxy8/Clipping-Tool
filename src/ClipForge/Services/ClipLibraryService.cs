using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipForge.Models;
using Microsoft.Win32.SafeHandles;

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
    private const int MaximumProbeCacheEntries = 512;
    private const long MaximumThumbnailBytes = 16 * 1024 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;
    private const int MaximumFinalPathCharacters = 32_768;
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
    private readonly SemaphoreSlim _probeExecutionGate = new(1, 1);
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly object _probeCacheGate = new();
    private readonly Dictionary<ProbeCacheKey, CachedProbe> _probeCache = [];
    private readonly string _thumbnailCacheDirectory;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _thumbnailTimeout;
    private long _probeCacheAccessOrder;

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
        var probeAttempts = 0;

        foreach (var candidate in discovery.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Capture a stable Windows identity before consulting the cache.
            // Path, size and timestamp alone are not sufficient because a file
            // can be replaced between gallery refreshes.
            if (!TryGetCurrentFileIdentity(candidate, out var identity))
            {
                continue;
            }

            var cacheKey = new ProbeCacheKey(
                identity,
                candidate.Length,
                candidate.LastWriteTimeUtc.Ticks);
            var cacheHit = TryGetCachedValidProbe(cacheKey, out var probe);
            if (!cacheHit && probeAttempts >= maximumProbes)
            {
                break;
            }

            if (!cacheHit)
            {
                probeAttempts++;
                try
                {
                    probe = await GetValidatedProbeAsync(
                            ffprobePath,
                            candidate,
                            identity,
                            cacheKey,
                            probeBudget.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    probeBudget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    break;
                }
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
                probe.Duration)
            {
                FileIdentity = identity
            });

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

    private async Task<ProbeOutcome> GetValidatedProbeAsync(
        string ffprobePath,
        ClipCandidate candidate,
        ClipFileIdentity expectedIdentity,
        ProbeCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        // Only one low-priority ffprobe process may run per library service.
        // A second foreground/library request waits, then consumes the first
        // request's cached result instead of competing with the game and capture.
        await _probeExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedValidProbe(cacheKey, out var cached))
            {
                return cached;
            }

            var probe = await ProbeAsync(
                    ffprobePath,
                    candidate.FullPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (probe.State != ProbeState.Valid)
            {
                return probe;
            }

            // Re-open the candidate after ffprobe exits. Cache the result only
            // when the exact file identity and metadata are still unchanged.
            if (!TryGetCurrentFileIdentity(candidate, out var currentIdentity) ||
                currentIdentity != expectedIdentity)
            {
                return ProbeOutcome.Invalid;
            }

            CacheValidProbe(cacheKey, probe);
            return probe;
        }
        finally
        {
            _probeExecutionGate.Release();
        }
    }

    private bool TryGetCachedValidProbe(ProbeCacheKey key, out ProbeOutcome probe)
    {
        lock (_probeCacheGate)
        {
            if (!_probeCache.TryGetValue(key, out var cached))
            {
                probe = ProbeOutcome.Invalid;
                return false;
            }

            probe = cached.Outcome;
            _probeCache[key] = cached with { LastAccessOrder = NextProbeCacheAccessOrderLocked() };
            return true;
        }
    }

    private void CacheValidProbe(ProbeCacheKey key, ProbeOutcome probe)
    {
        if (probe.State != ProbeState.Valid)
        {
            return;
        }

        lock (_probeCacheGate)
        {
            if (!_probeCache.ContainsKey(key) && _probeCache.Count >= MaximumProbeCacheEntries)
            {
                var oldestKey = _probeCache.MinBy(entry => entry.Value.LastAccessOrder).Key;
                _probeCache.Remove(oldestKey);
            }

            _probeCache[key] = new CachedProbe(probe, NextProbeCacheAccessOrderLocked());
        }
    }

    private long NextProbeCacheAccessOrderLocked()
    {
        if (_probeCacheAccessOrder == long.MaxValue)
        {
            _probeCacheAccessOrder = 0;
            foreach (var key in _probeCache.Keys.ToArray())
            {
                _probeCache[key] = _probeCache[key] with { LastAccessOrder = 0 };
            }
        }

        return ++_probeCacheAccessOrder;
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
        return TryGetCurrentClipPath(saveDirectory, clip, out _);
    }

    /// <summary>
    /// Revalidates a gallery item and returns the normalized current path. Callers must perform this
    /// immediately before a non-destructive action such as revealing the item in Explorer.
    /// </summary>
    internal static bool TryGetCurrentClipPath(
        string saveDirectory,
        ClipLibraryItem clip,
        out string fullPath)
    {
        ArgumentNullException.ThrowIfNull(clip);
        fullPath = string.Empty;
        var rootDirectory = TryGetSafeRootDirectory(saveDirectory);
        if (rootDirectory is null ||
            !TryGetMatchingCurrentCandidate(rootDirectory, clip, out var candidate))
        {
            return false;
        }

        fullPath = candidate.FullPath;
        return true;
    }

    /// <summary>
    /// Permanently deletes the exact, revalidated file represented by a gallery item. Windows handle-
    /// based deletion prevents a path or reparse-point swap between validation and deletion.
    /// </summary>
    internal static ClipDeletionResult DeleteCurrentClip(
        string saveDirectory,
        ClipLibraryItem clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!OperatingSystem.IsWindows() ||
            !TryGetCurrentClipPath(saveDirectory, clip, out var validatedPath))
        {
            return ClipDeletionResult.ChangedOrUnsafe;
        }

        using var handle = CreateFileW(
            validatedPath,
            DeleteAccess | FileReadAttributes,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return ClipDeletionResult.Unavailable;
        }

        if (!TryValidateOpenClipHandle(handle, clip, requireSingleLink: true))
        {
            return ClipDeletionResult.ChangedOrUnsafe;
        }

        var disposition = new FileDispositionInformation { DeleteFile = true };
        return SetFileInformationByHandle(
            handle,
            FileInfoByHandleClass.FileDispositionInfo,
            ref disposition,
            Marshal.SizeOf<FileDispositionInformation>())
            ? ClipDeletionResult.Deleted
            : ClipDeletionResult.Unavailable;
    }

    /// <summary>
    /// Removes both the identity-bound thumbnail and its legacy v1.2 key after
    /// a clip is deleted. Paths are derived from trusted discovery metadata
    /// instead of accepting presentation paths as deletion targets.
    /// </summary>
    internal void RemoveCachedThumbnail(ClipLibraryItem clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!OperatingSystem.IsWindows() || clip.FileIdentity is not { } identity || !HasUsableFileId(identity))
        {
            return;
        }

        var currentPath = GetDeterministicThumbnailPath(clip);
        var legacyPath = GetLegacyThumbnailPath(clip);
        using var cacheDirectoryHandle = CreateFileW(
            _thumbnailCacheDirectory,
            GenericRead,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!TryValidateDirectoryHandle(cacheDirectoryHandle))
        {
            return;
        }

        TryDeleteCachedThumbnail(cacheDirectoryHandle, currentPath);
        if (!currentPath.Equals(legacyPath, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteCachedThumbnail(cacheDirectoryHandle, legacyPath);
        }
    }

    private static void TryDeleteCachedThumbnail(
        SafeFileHandle cacheDirectoryHandle,
        string thumbnailPath)
    {
        using var thumbnailHandle = CreateFileW(
            thumbnailPath,
            DeleteAccess | FileReadAttributes,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!TryValidateCachedThumbnailHandle(cacheDirectoryHandle, thumbnailHandle))
        {
            return;
        }

        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                thumbnailHandle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                Marshal.SizeOf<FileDispositionInformation>()))
        {
            // Cache cleanup is best effort; failure leaves only a bounded deterministic entry.
        }
    }

    private void PruneLegacyThumbnail(
        ClipLibraryItem clip,
        PinnedThumbnailContext pinnedContext)
    {
        var currentName = Path.GetFileName(GetDeterministicThumbnailPath(clip));
        var legacyName = Path.GetFileName(GetLegacyThumbnailPath(clip));
        if (currentName.Equals(legacyName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Exactly one deterministic legacy key is considered per clip load.
        TryDeleteCachedThumbnail(
            pinnedContext.CacheDirectoryHandle,
            pinnedContext.GetCachePath(legacyName));
    }

    /// <summary>
    /// Returns a cached JPEG path or null. Validated source, source-root, and cache-root handles stay
    /// open through cache validation, helper execution, and cache commit to prevent pathname ABA.
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
            clip.FileIdentity is not { } identity ||
            !HasUsableFileId(identity) ||
            !TryPrepareCacheDirectory())
        {
            return null;
        }

        var thumbnailName = Path.GetFileName(GetDeterministicThumbnailPath(clip));
        using (var pinnedContext = TryOpenPinnedThumbnailContext(rootDirectory, clip))
        {
            if (pinnedContext is null)
            {
                return null;
            }

            var thumbnailPath = pinnedContext.GetCachePath(thumbnailName);
            if (IsUsableThumbnail(thumbnailPath))
            {
                PruneLegacyThumbnail(clip, pinnedContext);
                return thumbnailPath;
            }
        }

        await _thumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingPath = null;

        try
        {
            using var pinnedContext = TryOpenPinnedThumbnailContext(rootDirectory, clip);
            if (pinnedContext is null)
            {
                return null;
            }

            var thumbnailPath = pinnedContext.GetCachePath(thumbnailName);
            if (IsUsableThumbnail(thumbnailPath))
            {
                PruneLegacyThumbnail(clip, pinnedContext);
                return thumbnailPath;
            }

            if (IsReparsePoint(thumbnailPath))
            {
                return null;
            }

            stagingPath = Path.Combine(
                pinnedContext.CacheDirectoryPath,
                $".{Path.GetFileNameWithoutExtension(thumbnailPath)}.{Guid.NewGuid():N}.tmp.jpg");
            var ffmpegPath = GetSafeToolPath(_findFfmpeg());
            if (ffmpegPath is null)
            {
                return null;
            }

            var arguments = BuildThumbnailArguments(
                pinnedContext.MediaPath,
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

            // The validated source/root handles are still open here, so the clip
            // cannot be written, renamed, deleted, or rebound before cache commit.
            if (IsReparsePoint(thumbnailPath))
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

            if (!IsUsableThumbnail(thumbnailPath))
            {
                return null;
            }

            PruneLegacyThumbnail(clip, pinnedContext);
            return thumbnailPath;
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

    private static bool CandidateMatchesClip(ClipCandidate candidate, ClipLibraryItem clip) =>
        candidate.FileName.Equals(clip.FileName, StringComparison.Ordinal) &&
        candidate.Length == clip.FileSizeBytes &&
        candidate.LastWriteTimeUtc == clip.RecordedAtUtc.UtcDateTime;

    private static bool TryGetMatchingCurrentCandidate(
        string rootDirectory,
        ClipLibraryItem clip,
        out ClipCandidate candidate)
    {
        candidate = default;
        return clip.FileIdentity is { } expectedIdentity &&
               HasUsableFileId(expectedIdentity) &&
               TryGetCurrentCandidate(rootDirectory, clip.FullPath, out candidate) &&
               CandidateMatchesClip(candidate, clip) &&
               TryGetCurrentFileIdentity(candidate, out var currentIdentity) &&
               currentIdentity == expectedIdentity;
    }

    private PinnedThumbnailContext? TryOpenPinnedThumbnailContext(
        string rootDirectory,
        ClipLibraryItem clip)
    {
        if (!OperatingSystem.IsWindows() ||
            clip.FileIdentity is not { } identity ||
            !HasUsableFileId(identity))
        {
            return null;
        }

        SafeFileHandle? rootHandle = null;
        SafeFileHandle? clipHandle = null;
        SafeFileHandle? cacheDirectoryHandle = null;
        try
        {
            var fullPath = Path.GetFullPath(clip.FullPath);
            if (!Path.GetFileName(fullPath).Equals(clip.FileName, StringComparison.Ordinal) ||
                !IsSafeTopLevelClipPath(rootDirectory, fullPath, FileAttributes.Normal))
            {
                return null;
            }

            rootHandle = CreateFileW(
                rootDirectory,
                GenericRead,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);
            if (!TryValidateDirectoryHandle(rootHandle))
            {
                return null;
            }

            clipHandle = CreateFileW(
                fullPath,
                GenericRead,
                FileShare.Read,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!TryValidateOpenClipHandle(clipHandle, clip, requireSingleLink: false) ||
                !TryValidateDirectChildHandle(rootHandle, clipHandle, out var mediaPath))
            {
                return null;
            }

            cacheDirectoryHandle = CreateFileW(
                _thumbnailCacheDirectory,
                GenericRead,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);
            if (!TryValidateDirectoryHandle(cacheDirectoryHandle) ||
                TryGetFinalDosPath(cacheDirectoryHandle) is not { } cacheDirectoryPath)
            {
                return null;
            }

            var pinnedContext = new PinnedThumbnailContext(
                rootHandle,
                clipHandle,
                cacheDirectoryHandle,
                mediaPath,
                cacheDirectoryPath);
            rootHandle = null;
            clipHandle = null;
            cacheDirectoryHandle = null;
            return pinnedContext;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }
        finally
        {
            cacheDirectoryHandle?.Dispose();
            clipHandle?.Dispose();
            rootHandle?.Dispose();
        }
    }

    internal static bool HasUsableFileId(ClipFileIdentity identity) =>
        HasUsableFileId(identity.FileIdLow, identity.FileIdHigh);

    private static bool HasUsableFileId(ulong fileIdLow, ulong fileIdHigh) =>
        fileIdLow != 0 || fileIdHigh != 0;

    internal static bool TryGetCurrentFileIdentity(
        string path,
        out ClipFileIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists ||
                info.Length <= 0 ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            return TryGetCurrentFileIdentity(
                new ClipCandidate(
                    info.Name,
                    info.FullName,
                    info.Length,
                    DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
                out identity);
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return false;
        }
    }

    private static bool TryGetCurrentFileIdentity(
        ClipCandidate candidate,
        out ClipFileIdentity identity)
    {
        identity = default;
        using var handle = CreateFileW(
            candidate.FullPath,
            FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInformation attributes,
                Marshal.SizeOf<FileAttributeTagInformation>()) ||
            (attributes.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileStandardInfo,
                out FileStandardInformation standard,
                Marshal.SizeOf<FileStandardInformation>()) ||
            standard.Directory ||
            standard.DeletePending ||
            standard.EndOfFile != candidate.Length ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                out FileBasicInformation basic,
                Marshal.SizeOf<FileBasicInformation>()) ||
            basic.LastWriteTime != candidate.LastWriteTimeUtc.ToFileTimeUtc() ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out FileIdInformation fileId,
                Marshal.SizeOf<FileIdInformation>()) ||
            !HasUsableFileId(fileId.FileIdLow, fileId.FileIdHigh))
        {
            return false;
        }

        identity = new ClipFileIdentity(
            fileId.VolumeSerialNumber,
            fileId.FileIdLow,
            fileId.FileIdHigh,
            standard.NumberOfLinks);
        return standard.NumberOfLinks > 0;
    }

    private static bool TryValidateOpenClipHandle(
        SafeFileHandle handle,
        ClipLibraryItem clip,
        bool requireSingleLink)
    {
        if (clip.FileIdentity is not { } expectedIdentity ||
            !HasUsableFileId(expectedIdentity) ||
            expectedIdentity.NumberOfLinks == 0 ||
            (requireSingleLink && expectedIdentity.NumberOfLinks != 1) ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInformation attributes,
                Marshal.SizeOf<FileAttributeTagInformation>()) ||
            (attributes.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileStandardInfo,
                out FileStandardInformation standard,
                Marshal.SizeOf<FileStandardInformation>()) ||
            standard.Directory ||
            standard.DeletePending ||
            standard.NumberOfLinks == 0 ||
            (requireSingleLink && standard.NumberOfLinks != 1) ||
            standard.EndOfFile != clip.FileSizeBytes ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                out FileBasicInformation basic,
                Marshal.SizeOf<FileBasicInformation>()) ||
            basic.LastWriteTime != clip.RecordedAtUtc.UtcDateTime.ToFileTimeUtc() ||
            !GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out FileIdInformation fileId,
                Marshal.SizeOf<FileIdInformation>()) ||
            !HasUsableFileId(fileId.FileIdLow, fileId.FileIdHigh))
        {
            return false;
        }

        return StableFileIdentityMatches(
            expectedIdentity,
            fileId.VolumeSerialNumber,
            fileId.FileIdLow,
            fileId.FileIdHigh);
    }

    private static bool StableFileIdentityMatches(
        ClipFileIdentity expectedIdentity,
        ulong volumeSerialNumber,
        ulong fileIdLow,
        ulong fileIdHigh) =>
        expectedIdentity.VolumeSerialNumber == volumeSerialNumber &&
        expectedIdentity.FileIdLow == fileIdLow &&
        expectedIdentity.FileIdHigh == fileIdHigh;

    private static bool TryValidateDirectoryHandle(SafeFileHandle handle)
    {
        return !handle.IsInvalid &&
               GetFileInformationByHandleEx(
                   handle,
                   FileInfoByHandleClass.FileAttributeTagInfo,
                   out FileAttributeTagInformation attributes,
                   Marshal.SizeOf<FileAttributeTagInformation>()) &&
               (attributes.FileAttributes & FileAttributes.Directory) != 0 &&
               (attributes.FileAttributes & FileAttributes.ReparsePoint) == 0 &&
               GetFileInformationByHandleEx(
                   handle,
                   FileInfoByHandleClass.FileStandardInfo,
                   out FileStandardInformation standard,
                   Marshal.SizeOf<FileStandardInformation>()) &&
               standard.Directory &&
               !standard.DeletePending;
    }

    private static bool TryValidateCachedThumbnailHandle(
        SafeFileHandle cacheDirectoryHandle,
        SafeFileHandle thumbnailHandle)
    {
        if (thumbnailHandle.IsInvalid ||
            !GetFileInformationByHandleEx(
                thumbnailHandle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInformation attributes,
                Marshal.SizeOf<FileAttributeTagInformation>()) ||
            (attributes.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return false;
        }

        return TryValidateDirectChildHandle(cacheDirectoryHandle, thumbnailHandle, out _);
    }

    private static bool TryValidateDirectChildHandle(
        SafeFileHandle rootDirectoryHandle,
        SafeFileHandle childHandle,
        out string childPath)
    {
        childPath = string.Empty;
        var rootDirectoryPath = TryGetFinalDosPath(rootDirectoryHandle);
        var resolvedChildPath = TryGetFinalDosPath(childHandle);
        if (rootDirectoryPath is null || resolvedChildPath is null)
        {
            return false;
        }

        var actualParent = Path.GetDirectoryName(resolvedChildPath);
        if (actualParent is null ||
            !Path.TrimEndingDirectorySeparator(rootDirectoryPath)
                   .Equals(
                       Path.TrimEndingDirectorySeparator(actualParent),
                       StringComparison.Ordinal))
        {
            return false;
        }

        childPath = resolvedChildPath;
        return true;
    }

    private static string? TryGetFinalDosPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= MaximumFinalPathCharacters)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                builder,
                checked((uint)builder.Capacity),
                FileNameNormalized);
            if (length == 0)
            {
                return null;
            }

            if (length < builder.Capacity)
            {
                var path = builder.ToString();
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    path = @"\\" + path[8..];
                }
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    path = path[4..];
                }

                try
                {
                    return Path.GetFullPath(path);
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    return null;
                }
            }

            if (length >= MaximumFinalPathCharacters)
            {
                return null;
            }

            capacity = checked((int)length + 1);
        }

        return null;
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

    private string GetDeterministicThumbnailPath(
        ClipCandidate candidate,
        ClipFileIdentity fileIdentity)
    {
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{candidate.FullPath.ToUpperInvariant()}\n{candidate.Length}\n{candidate.LastWriteTimeUtc.Ticks}\n{fileIdentity.VolumeSerialNumber:X16}\n{fileIdentity.FileIdHigh:X16}{fileIdentity.FileIdLow:X16}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_thumbnailCacheDirectory, $"{hash}.jpg");
    }

    private string GetLegacyThumbnailPath(ClipCandidate candidate)
    {
        var legacyIdentity = string.Create(
            CultureInfo.InvariantCulture,
            $"{candidate.FullPath.ToUpperInvariant()}\n{candidate.Length}\n{candidate.LastWriteTimeUtc.Ticks}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyIdentity)));
        return Path.Combine(_thumbnailCacheDirectory, $"{hash}.jpg");
    }

    internal string GetDeterministicThumbnailPath(ClipLibraryItem clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.FileIdentity is not { } identity || !HasUsableFileId(identity))
        {
            throw new InvalidOperationException("A stable file identity is required for a thumbnail cache key.");
        }

        return GetDeterministicThumbnailPath(new ClipCandidate(
            clip.FileName,
            Path.GetFullPath(clip.FullPath),
            clip.FileSizeBytes,
            clip.RecordedAtUtc.UtcDateTime), identity);
    }

    internal string GetLegacyThumbnailPath(ClipLibraryItem clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return GetLegacyThumbnailPath(new ClipCandidate(
            clip.FileName,
            Path.GetFullPath(clip.FullPath),
            clip.FileSizeBytes,
            clip.RecordedAtUtc.UtcDateTime));
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

    private readonly record struct ProbeCacheKey(
        ClipFileIdentity FileIdentity,
        long Length,
        long LastWriteTimeUtcTicks);

    private readonly record struct CachedProbe(
        ProbeOutcome Outcome,
        long LastAccessOrder);

    private sealed class PinnedThumbnailContext(
        SafeFileHandle rootDirectoryHandle,
        SafeFileHandle clipHandle,
        SafeFileHandle cacheDirectoryHandle,
        string mediaPath,
        string cacheDirectoryPath) : IDisposable
    {
        private readonly SafeFileHandle _rootDirectoryHandle = rootDirectoryHandle;
        private readonly SafeFileHandle _clipHandle = clipHandle;
        private readonly SafeFileHandle _cacheDirectoryHandle = cacheDirectoryHandle;

        public string MediaPath { get; } = mediaPath;

        public string CacheDirectoryPath { get; } = cacheDirectoryPath;

        public SafeFileHandle CacheDirectoryHandle => _cacheDirectoryHandle;

        public string GetCachePath(string fileName) =>
            Path.Combine(CacheDirectoryPath, Path.GetFileName(fileName));

        public void Dispose()
        {
            _clipHandle.Dispose();
            _rootDirectoryHandle.Dispose();
            _cacheDirectoryHandle.Dispose();
        }
    }

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

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileStandardInfo = 1,
        FileDispositionInfo = 4,
        FileAttributeTagInfo = 9,
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public FileAttributes FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;

        [MarshalAs(UnmanagedType.U1)]
        public bool DeletePending;

        [MarshalAs(UnmanagedType.U1)]
        public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileStandardInformation fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileBasicInformation fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInformation fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathCharacters,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInformation fileInformation,
        int bufferSize);
}

internal enum ClipDeletionResult
{
    Deleted,
    ChangedOrUnsafe,
    Unavailable
}
