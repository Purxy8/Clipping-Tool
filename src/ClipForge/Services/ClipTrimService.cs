using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipForge.Capture;
using ClipForge.Models;

namespace ClipForge.Services;

/// <summary>
/// Creates a new, frame-aligned MP4 from a safely discovered ClipForge clip.
/// The original is never modified or deleted by this service.
/// </summary>
public sealed class ClipTrimService
{
    private const int MaximumEncoderCacheEntries = 16;
    private const int MaximumFramesPerSecond = 240;
    private const long MinimumWorkingFreeBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan EncoderProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MinimumTrimTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumTrimTimeout = TimeSpan.FromHours(2);
    private static readonly Regex PartialFileNamePattern = new(
        @"\A\.clipforge-trim-[0-9a-f]{32}\.partial\.mp4\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private readonly Func<string?> _findFfmpeg;
    private readonly Func<string?> _findFfprobe;
    private readonly IClipMediaProcessRunner _processRunner;
    private readonly SemaphoreSlim _trimGate = new(1, 1);
    private readonly Dictionary<EncoderCacheKey, VideoEncodingStrategy> _encoderCache = [];
    private int _replayBlockingTrimWork;

    public bool HasReplayBlockingTrimWork =>
        Volatile.Read(ref _replayBlockingTrimWork) > 0;

    public ClipTrimService(FfmpegSetupService ffmpegSetupService)
        : this(
            (ffmpegSetupService ?? throw new ArgumentNullException(nameof(ffmpegSetupService))).FindExecutable,
            ffmpegSetupService.FindProbeExecutable,
            new ClipMediaProcessRunner())
    {
    }

    internal ClipTrimService(
        FfmpegSetupService ffmpegSetupService,
        IClipMediaProcessRunner processRunner)
        : this(
            (ffmpegSetupService ?? throw new ArgumentNullException(nameof(ffmpegSetupService))).FindExecutable,
            ffmpegSetupService.FindProbeExecutable,
            processRunner)
    {
    }

    internal ClipTrimService(
        Func<string?> findFfmpeg,
        Func<string?> findFfprobe,
        IClipMediaProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(findFfmpeg);
        ArgumentNullException.ThrowIfNull(findFfprobe);
        ArgumentNullException.ThrowIfNull(processRunner);
        _findFfmpeg = findFfmpeg;
        _findFfprobe = findFfprobe;
        _processRunner = processRunner;
    }

    public async Task<ClipTrimResult> TrimAsync(
        string saveDirectory,
        ClipLibraryItem source,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken = default)
        => await TrimAsync(
                saveDirectory,
                source,
                start,
                end,
                ClipTrimExecutionMode.Standard,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<ClipTrimResult> TrimAsync(
        string saveDirectory,
        ClipLibraryItem source,
        TimeSpan start,
        TimeSpan end,
        ClipTrimExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(executionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(executionMode));
        }

        var enteredGate = false;
        var replayBlockingWork = executionMode == ClipTrimExecutionMode.Standard;
        if (replayBlockingWork)
        {
            Interlocked.Increment(ref _replayBlockingTrimWork);
        }

        try
        {
            await _trimGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            return await TrimCoreAsync(
                    saveDirectory,
                    source,
                    start,
                    end,
                    executionMode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ClipTrimResult(
                ClipTrimStatus.Cancelled,
                null,
                "Trimming was cancelled. The original clip was not changed.");
        }
        catch (Exception exception) when (IsExpectedTrimException(exception))
        {
            return new ClipTrimResult(
                ClipTrimStatus.EncodingFailed,
                null,
                $"The trimmed clip could not be created. {SanitizeDiagnostic(exception.Message)}");
        }
        finally
        {
            if (enteredGate)
            {
                _trimGate.Release();
            }

            if (replayBlockingWork)
            {
                Interlocked.Decrement(ref _replayBlockingTrimWork);
            }
        }
    }

    /// <summary>
    /// Clamps a requested range to the source, snaps both handles to the source's constant frame
    /// grid, and guarantees a non-empty inclusive/exclusive frame interval.
    /// </summary>
    public static bool TryNormalizeRange(
        TimeSpan start,
        TimeSpan end,
        TimeSpan sourceDuration,
        double framesPerSecond,
        out ClipTrimRange range,
        out string error)
    {
        range = default;
        error = string.Empty;
        if (sourceDuration <= TimeSpan.Zero)
        {
            error = "The source duration is unavailable.";
            return false;
        }

        if (!double.IsFinite(framesPerSecond) ||
            framesPerSecond <= 0 ||
            framesPerSecond > MaximumFramesPerSecond)
        {
            error = "The source frame rate is invalid.";
            return false;
        }

        var clampedStart = TimeSpan.FromTicks(Math.Clamp(start.Ticks, 0, sourceDuration.Ticks));
        var clampedEnd = TimeSpan.FromTicks(Math.Clamp(end.Ticks, 0, sourceDuration.Ticks));
        if (clampedEnd <= clampedStart)
        {
            error = "The trim end must be after the trim start.";
            return false;
        }

        // FFprobe emits decimal seconds, so allow a tiny sub-frame rounding margin before floor.
        var frameCountValue = Math.Floor(sourceDuration.TotalSeconds * framesPerSecond + 0.001);
        if (!double.IsFinite(frameCountValue) || frameCountValue < 1 || frameCountValue > long.MaxValue)
        {
            error = "The source frame timeline is invalid.";
            return false;
        }

        var sourceFrameCount = (long)frameCountValue;
        var startFrameValue = Math.Round(
            clampedStart.TotalSeconds * framesPerSecond,
            MidpointRounding.AwayFromZero);
        var endFrameValue = Math.Round(
            clampedEnd.TotalSeconds * framesPerSecond,
            MidpointRounding.AwayFromZero);
        if (!double.IsFinite(startFrameValue) || !double.IsFinite(endFrameValue))
        {
            error = "The requested trim range is invalid.";
            return false;
        }

        var startFrame = Math.Clamp((long)startFrameValue, 0, sourceFrameCount - 1);
        var endFrame = Math.Clamp((long)endFrameValue, 1, sourceFrameCount);
        if (endFrame <= startFrame)
        {
            endFrame = Math.Min(sourceFrameCount, startFrame + 1);
        }

        if (endFrame <= startFrame)
        {
            error = "Select at least one video frame.";
            return false;
        }

        var normalizedStart = TimeSpan.FromSeconds(startFrame / framesPerSecond);
        var normalizedEnd = TimeSpan.FromSeconds(endFrame / framesPerSecond);
        range = new ClipTrimRange(normalizedStart, normalizedEnd, startFrame, endFrame);
        return true;
    }

    private async Task<ClipTrimResult> TrimCoreAsync(
        string saveDirectory,
        ClipLibraryItem source,
        TimeSpan requestedStart,
        TimeSpan requestedEnd,
        ClipTrimExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = ClipLibraryService.GetSafeToolPath(_findFfmpeg());
        var ffprobePath = ClipLibraryService.GetSafeToolPath(_findFfprobe());
        if (ffmpegPath is null || ffprobePath is null)
        {
            return new ClipTrimResult(
                ClipTrimStatus.EngineUnavailable,
                null,
                "The verified ClipForge media engine is unavailable.");
        }

        using var pinnedSource = ClipLibraryService.TryOpenPinnedClipReadContext(
            saveDirectory,
            source);
        if (pinnedSource is null)
        {
            return new ClipTrimResult(
                ClipTrimStatus.SourceChangedOrUnsafe,
                null,
                "The original clip changed or is no longer a safe local ClipForge recording.");
        }

        var sourceMetadata = await ProbeMediaAsync(
                ffprobePath,
                pinnedSource.MediaPath,
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceMetadata is null)
        {
            return new ClipTrimResult(
                ClipTrimStatus.SourceChangedOrUnsafe,
                null,
                "The original clip could not be validated for trimming.");
        }

        if (!TryNormalizeRange(
                requestedStart,
                requestedEnd,
                sourceMetadata.Value.Duration,
                sourceMetadata.Value.NominalFramesPerSecond,
                out var range,
                out var rangeError))
        {
            return new ClipTrimResult(ClipTrimStatus.InvalidRange, null, rangeError);
        }

        if (!HasSufficientWorkingSpace(
                pinnedSource.RootDirectoryPath,
                source,
                sourceMetadata.Value,
                range.Duration))
        {
            return new ClipTrimResult(
                ClipTrimStatus.InsufficientSpace,
                null,
                "There is not enough free space to create the trimmed clip safely.");
        }

        var framesPerSecond = Math.Clamp(
            (int)Math.Round(
                sourceMetadata.Value.NominalFramesPerSecond,
                MidpointRounding.AwayFromZero),
            1,
            MaximumFramesPerSecond);
        var encoder = executionMode == ClipTrimExecutionMode.ReplayCoexisting
            ? VideoEncodingStrategy.SoftwareGdi
            : await SelectEncoderAsync(
                    ffmpegPath,
                    sourceMetadata.Value.Width,
                    sourceMetadata.Value.Height,
                    framesPerSecond,
                    cancellationToken)
                .ConfigureAwait(false);

        string? stagingPath = CreateUniqueStagingPath(pinnedSource.RootDirectoryPath);
        try
        {
            var timeout = CalculateTrimTimeout(range.Duration);
            var execution = await RunTrimAttemptAsync(
                    ffmpegPath,
                    pinnedSource.MediaPath,
                    stagingPath,
                    range,
                    sourceMetadata.Value.HasAudio,
                    framesPerSecond,
                    encoder,
                    executionMode,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!execution.Succeeded &&
                !execution.TimedOut &&
                executionMode == ClipTrimExecutionMode.Standard &&
                encoder.IsHardwareEncoder)
            {
                TryDeleteStagingFile(pinnedSource.RootDirectoryPath, stagingPath);
                var software = VideoEncodingStrategy.SoftwareGdi;
                CacheEncoder(
                    BuildEncoderCacheKey(
                        ffmpegPath,
                        sourceMetadata.Value.Width,
                        sourceMetadata.Value.Height,
                        framesPerSecond),
                    software);
                execution = await RunTrimAttemptAsync(
                        ffmpegPath,
                        pinnedSource.MediaPath,
                        stagingPath,
                        range,
                        sourceMetadata.Value.HasAudio,
                        framesPerSecond,
                        software,
                        executionMode,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!execution.Succeeded)
            {
                return new ClipTrimResult(
                    ClipTrimStatus.EncodingFailed,
                    null,
                    execution.TimedOut
                        ? "Trimming took too long and was stopped. The original clip was not changed."
                        : "The media engine could not encode the selected range.");
            }

            if (!ClipLibraryService.TryValidatePinnedDirectChildFile(
                    pinnedSource,
                    stagingPath,
                    out var validatedStagingPath,
                    out _))
            {
                return new ClipTrimResult(
                    ClipTrimStatus.OutputValidationFailed,
                    null,
                    "The media engine produced an unsafe or empty output file.");
            }

            var outputMetadata = await ProbeMediaAsync(
                    ffprobePath,
                    validatedStagingPath,
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outputMetadata is null)
            {
                return new ClipTrimResult(
                    ClipTrimStatus.OutputValidationFailed,
                    null,
                    "The generated MP4 could not be read back by ClipForge's media validator.");
            }

            if (!TryValidateExpectedOutput(
                    outputMetadata.Value,
                    range.Duration,
                    sourceMetadata.Value,
                    out var validationFailure))
            {
                return new ClipTrimResult(
                    ClipTrimStatus.OutputValidationFailed,
                    null,
                    $"The generated MP4 failed ClipForge's media validation. {validationFailure}");
            }

            if (!ClipLibraryService.TryValidatePinnedDirectChildFile(
                    pinnedSource,
                    validatedStagingPath,
                    out validatedStagingPath,
                    out _))
            {
                return new ClipTrimResult(
                    ClipTrimStatus.OutputValidationFailed,
                    null,
                    "The generated MP4 changed before it could be saved.");
            }

            var committedPath = CommitStagingFile(
                pinnedSource.RootDirectoryPath,
                validatedStagingPath);
            if (committedPath is null)
            {
                return new ClipTrimResult(
                    ClipTrimStatus.CommitFailed,
                    null,
                    "The trimmed clip was encoded, but a unique final name could not be reserved.");
            }

            stagingPath = null;
            return new ClipTrimResult(
                ClipTrimStatus.Succeeded,
                committedPath,
                "The trimmed clip was saved successfully.");
        }
        finally
        {
            if (stagingPath is not null)
            {
                TryDeleteStagingFile(pinnedSource.RootDirectoryPath, stagingPath);
            }
        }
    }

    private async Task<ClipMediaProcessResult> RunTrimAttemptAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        ClipTrimRange range,
        bool includeAudio,
        int framesPerSecond,
        VideoEncodingStrategy encoder,
        ClipTrimExecutionMode executionMode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var arguments = FfmpegArgumentBuilder.BuildTrimArguments(
            inputPath,
            outputPath,
            range.Start,
            range.Duration,
            includeAudio,
            framesPerSecond,
            encoder,
            executionMode == ClipTrimExecutionMode.ReplayCoexisting);
        return await _processRunner.RunAsync(
                ffmpegPath,
                arguments,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VideoEncodingStrategy> SelectEncoderAsync(
        string ffmpegPath,
        int width,
        int height,
        int framesPerSecond,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildEncoderCacheKey(ffmpegPath, width, height, framesPerSecond);
        if (_encoderCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        VideoEncoderKind[] hardwarePreference =
        [
            VideoEncoderKind.NvidiaNvenc,
            VideoEncoderKind.IntelQuickSync,
            VideoEncoderKind.AmdAmf
        ];
        foreach (var kind in hardwarePreference)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var strategy = new VideoEncodingStrategy(kind, DesktopCaptureBackend.Gdi);
            try
            {
                var result = await _processRunner.RunAsync(
                        ffmpegPath,
                        FfmpegArgumentBuilder.BuildEncoderProbeArguments(
                            strategy,
                            width,
                            height,
                            framesPerSecond),
                        EncoderProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.Succeeded)
                {
                    CacheEncoder(cacheKey, strategy);
                    return strategy;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedTrimException(exception))
            {
                // Continue to the next verified runtime encoder.
            }
        }

        CacheEncoder(cacheKey, VideoEncodingStrategy.SoftwareGdi);
        return VideoEncodingStrategy.SoftwareGdi;
    }

    private void CacheEncoder(EncoderCacheKey key, VideoEncodingStrategy strategy)
    {
        if (!_encoderCache.ContainsKey(key) && _encoderCache.Count >= MaximumEncoderCacheEntries)
        {
            _encoderCache.Remove(_encoderCache.Keys.First());
        }

        _encoderCache[key] = strategy;
    }

    private static EncoderCacheKey BuildEncoderCacheKey(
        string ffmpegPath,
        int width,
        int height,
        int framesPerSecond)
    {
        long writeTicks;
        try
        {
            writeTicks = File.GetLastWriteTimeUtc(ffmpegPath).Ticks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writeTicks = 0;
        }

        return new EncoderCacheKey(
            Path.GetFullPath(ffmpegPath),
            writeTicks,
            width,
            height,
            framesPerSecond);
    }

    private async Task<TrimMediaMetadata?> ProbeMediaAsync(
        string ffprobePath,
        string mediaPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
                ffprobePath,
                BuildMediaProbeArguments(mediaPath),
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded
            ? ParseMediaProbe(result.StandardOutput)
            : null;
    }

    internal static IReadOnlyList<string> BuildMediaProbeArguments(string mediaPath) =>
    [
        "-v", "error",
        "-protocol_whitelist", "file",
        "-f", "mov",
        "-show_entries", "stream=codec_type,width,height,avg_frame_rate,r_frame_rate,duration:format=duration",
        "-of", "json",
        mediaPath
    ];

    private static TrimMediaMetadata? ParseMediaProbe(string output)
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
                streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement? videoStream = null;
            var hasAudio = false;
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object ||
                    !stream.TryGetProperty("codec_type", out var codecType) ||
                    codecType.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = codecType.GetString();
                if (type == "video" && videoStream is null)
                {
                    videoStream = stream.Clone();
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                }
            }

            if (videoStream is not { } video ||
                !TryReadPositiveInt(video, "width", out var width) ||
                !TryReadPositiveInt(video, "height", out var height))
            {
                return null;
            }

            if (!TryReadFrameRate(video, "avg_frame_rate", out var framesPerSecond) &&
                !TryReadFrameRate(video, "r_frame_rate", out framesPerSecond))
            {
                return null;
            }

            var nominalFramesPerSecond = TryReadFrameRate(
                video,
                "r_frame_rate",
                out var parsedNominalFramesPerSecond)
                ? parsedNominalFramesPerSecond
                : framesPerSecond;

            double durationSeconds = double.NaN;
            if (video.TryGetProperty("duration", out var streamDuration))
            {
                _ = TryReadPositiveDouble(streamDuration, out durationSeconds);
            }

            if ((!double.IsFinite(durationSeconds) || durationSeconds <= 0) &&
                document.RootElement.TryGetProperty("format", out var format) &&
                format.ValueKind == JsonValueKind.Object &&
                format.TryGetProperty("duration", out var formatDuration))
            {
                _ = TryReadPositiveDouble(formatDuration, out durationSeconds);
            }

            if (!double.IsFinite(durationSeconds) ||
                durationSeconds <= 0 ||
                durationSeconds > TimeSpan.MaxValue.TotalSeconds)
            {
                return null;
            }

            return new TrimMediaMetadata(
                TimeSpan.FromSeconds(durationSeconds),
                width,
                height,
                framesPerSecond,
                nominalFramesPerSecond,
                hasAudio);
        }
        catch (Exception exception) when (exception is JsonException or OverflowException or FormatException)
        {
            return null;
        }
    }

    private static bool TryReadPositiveInt(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) &&
               value > 0 &&
               value <= 32_768;
    }

    private static bool TryReadFrameRate(
        JsonElement video,
        string propertyName,
        out double framesPerSecond)
    {
        framesPerSecond = 0;
        if (!video.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parts = property.GetString()?.Split('/', 2);
        if (parts is not { Length: 2 } ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            !double.IsFinite(numerator) ||
            !double.IsFinite(denominator) ||
            numerator <= 0 ||
            denominator <= 0)
        {
            return false;
        }

        var parsed = numerator / denominator;
        if (!double.IsFinite(parsed) || parsed <= 0 || parsed > MaximumFramesPerSecond)
        {
            return false;
        }

        framesPerSecond = parsed;
        return true;
    }

    private static bool TryReadPositiveDouble(JsonElement element, out double value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var textNumber) => textNumber,
            _ => double.NaN
        };
        return double.IsFinite(value) && value > 0;
    }

    private static bool TryValidateExpectedOutput(
        TrimMediaMetadata output,
        TimeSpan expectedDuration,
        TrimMediaMetadata source,
        out string failure)
    {
        var toleranceSeconds = Math.Max(
            0.15,
            (2 / source.NominalFramesPerSecond) + 0.05);
        if (output.Width != source.Width || output.Height != source.Height)
        {
            failure = $"Video dimensions were {output.Width}x{output.Height}; expected {source.Width}x{source.Height}.";
            return false;
        }

        if (output.HasAudio != source.HasAudio)
        {
            failure = source.HasAudio
                ? "The trimmed copy is missing the source audio stream."
                : "The trimmed copy contains an unexpected audio stream.";
            return false;
        }

        // avg_frame_rate is selection-local for timestamp-variable captures and
        // legitimately changes when a short range is re-encoded. r_frame_rate is
        // the stable nominal cadence that the capture and encoder promise.
        var frameRateTolerance = Math.Max(0.05, source.NominalFramesPerSecond * 0.002);
        if (Math.Abs(output.NominalFramesPerSecond - source.NominalFramesPerSecond) >
            frameRateTolerance)
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"Nominal frame rate was {output.NominalFramesPerSecond:0.###} FPS; expected {source.NominalFramesPerSecond:0.###} FPS.");
            return false;
        }

        var durationDifference = Math.Abs(
            output.Duration.TotalSeconds - expectedDuration.TotalSeconds);
        if (durationDifference > toleranceSeconds)
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"Duration was {output.Duration.TotalSeconds:0.###}s; expected {expectedDuration.TotalSeconds:0.###}s (tolerance {toleranceSeconds:0.###}s).");
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool HasSufficientWorkingSpace(
        string rootDirectory,
        ClipLibraryItem source,
        TrimMediaMetadata metadata,
        TimeSpan trimDuration)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(rootDirectory);
            if (string.IsNullOrWhiteSpace(driveRoot))
            {
                return false;
            }

            var sourceRatio = Math.Clamp(
                trimDuration.TotalSeconds / metadata.Duration.TotalSeconds,
                0,
                1);
            var proportionalBytes = source.FileSizeBytes * sourceRatio * 2;
            var estimatedVideoBytes = metadata.Width *
                                      (double)metadata.Height *
                                      metadata.FramesPerSecond *
                                      0.20 / 8 *
                                      trimDuration.TotalSeconds;
            var required = Math.Max(proportionalBytes, estimatedVideoBytes) + MinimumWorkingFreeBytes;
            return double.IsFinite(required) &&
                   required < long.MaxValue &&
                   new DriveInfo(driveRoot).AvailableFreeSpace >= (long)Math.Ceiling(required);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            return false;
        }
    }

    private static string CreateUniqueStagingPath(string rootDirectory)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(
                rootDirectory,
                $".clipforge-trim-{Guid.NewGuid():N}.partial.mp4");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("A unique temporary trim file could not be reserved.");
    }

    internal static string? CommitStagingFile(
        string rootDirectory,
        string stagingPath,
        DateTime? localTimestamp = null)
    {
        var timestamp = localTimestamp ?? DateTime.Now;
        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            var fileName = BuildTrimmedFileName(timestamp, suffix);
            var candidate = Path.Combine(rootDirectory, fileName);
            if (!ClipLibraryService.IsSafeTopLevelClipPath(
                    rootDirectory,
                    candidate,
                    FileAttributes.Normal))
            {
                return null;
            }

            try
            {
                File.Move(stagingPath, candidate, overwrite: false);
                return Path.GetFullPath(candidate);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another save won the same generated name; try the next suffix.
            }
        }

        return null;
    }

    internal static string BuildTrimmedFileName(DateTime localTimestamp, int suffix)
    {
        if (suffix < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(suffix));
        }

        var stem = FormattableString.Invariant(
            $"Clip_{localTimestamp:yyyy-MM-dd_HH-mm-ss}_trimmed");
        return suffix == 1
            ? $"{stem}.mp4"
            : FormattableString.Invariant($"{stem}_{suffix}.mp4");
    }

    private static void TryDeleteStagingFile(string rootDirectory, string stagingPath)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            var fullPath = Path.GetFullPath(stagingPath);
            if (!fullRoot.Equals(Path.GetDirectoryName(fullPath), StringComparison.OrdinalIgnoreCase) ||
                !PartialFileNamePattern.IsMatch(Path.GetFileName(fullPath)) ||
                !File.Exists(fullPath) ||
                (File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return;
            }

            File.Delete(fullPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SecurityException)
        {
            // A uniquely named failed partial is safe to ignore if it cannot be removed.
        }
    }

    private static TimeSpan CalculateTrimTimeout(TimeSpan trimDuration)
    {
        var requested = TimeSpan.FromSeconds(trimDuration.TotalSeconds * 4 + 60);
        return requested < MinimumTrimTimeout
            ? MinimumTrimTimeout
            : requested > MaximumTrimTimeout
                ? MaximumTrimTimeout
                : requested;
    }

    private static string SanitizeDiagnostic(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return "Try again after refreshing the library.";
        }

        var text = diagnostic.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 240 ? text : text[^240..];
    }

    private static bool IsExpectedTrimException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            Win32Exception or JsonException or NotSupportedException or SecurityException or
            ArgumentException or OverflowException;

    private readonly record struct TrimMediaMetadata(
        TimeSpan Duration,
        int Width,
        int Height,
        double FramesPerSecond,
        double NominalFramesPerSecond,
        bool HasAudio);

    private readonly record struct EncoderCacheKey(
        string FfmpegPath,
        long LastWriteTimeUtcTicks,
        int Width,
        int Height,
        int FramesPerSecond);
}

public readonly record struct ClipTrimRange(
    TimeSpan Start,
    TimeSpan End,
    long StartFrame,
    long EndFrame)
{
    public TimeSpan Duration => End - Start;
}

public enum ClipTrimStatus
{
    Succeeded,
    Cancelled,
    InvalidRange,
    SourceChangedOrUnsafe,
    EngineUnavailable,
    InsufficientSpace,
    EncodingFailed,
    OutputValidationFailed,
    CommitFailed
}

public enum ClipTrimExecutionMode
{
    Standard,
    ReplayCoexisting
}

public sealed record ClipTrimResult(
    ClipTrimStatus Status,
    string? OutputPath,
    string Message)
{
    public bool Succeeded => Status == ClipTrimStatus.Succeeded && OutputPath is not null;
}
