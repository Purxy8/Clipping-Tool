using System.Diagnostics;
using System.IO.Pipes;
using ClipForge.Models;
using ClipForge.Services;
using ClipForge.Capture;
using System.Windows.Input;

namespace ClipForge.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Replay length preset catalog", TestReplayLengthPresetsAsync),
            ("Resolution preset catalog", TestResolutionPresetsAsync),
            ("Hotkey gesture validation", TestHotkeyGesturesAsync),
            ("Storage estimate helpers", TestStorageEstimatorAsync),
            ("FFmpeg capture arguments", TestCaptureArgumentsAsync),
            ("FFmpeg encoder strategies", TestEncoderStrategiesAsync),
            ("FFmpeg capability priority", TestEncoderCapabilityPriorityAsync),
            ("FFmpeg diagnostic prioritization", TestCaptureDiagnosticPriorityAsync),
            ("FFmpeg concat arguments", TestConcatArgumentsAsync),
            ("Configured FFmpeg discovery", TestConfiguredFfmpegDiscoveryAsync),
            ("Release metadata", TestReleaseMetadataAsync),
            ("Unconfigured updater is non-fatal", TestUnconfiguredUpdaterAsync),
            ("Default save directory", TestDefaultSaveDirectoryAsync),
            ("Settings JSON roundtrip", TestSettingsRoundtripAsync),
            ("Malformed settings fallback", TestMalformedSettingsFallbackAsync),
            ("Oversized settings fallback", TestOversizedSettingsFallbackAsync),
            ("Secure clip discovery and thumbnail cache", TestClipLibraryAsync),
            ("Clip path and media process hardening", TestClipLibrarySecurityPolicyAsync),
            ("Runtime local-data boundaries", TestRuntimeLocalDataBoundariesAsync),
            ("Clip library cancellation", TestClipLibraryCancellationAsync)
        ];

        var failures = 0;

        foreach (var test in tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {test.Name}");
                Console.Error.WriteLine($"      {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestReplayLengthPresetsAsync()
    {
        int[] expectedSeconds = [30, 60, 120, 180, 300, 600, 1200, 1800, 2400, 3600];
        var actualSeconds = ReplayLengthOption.All.Select(option => checked((int)option.Duration.TotalSeconds));

        Assert.SequenceEqual(expectedSeconds, actualSeconds, "Replay presets differ from the product requirements.");
        Assert.Equal(
            ReplayLengthOption.All.Count,
            ReplayLengthOption.All.Select(option => option.Label).Distinct(StringComparer.Ordinal).Count(),
            "Replay preset labels must be unique.");

        return Task.CompletedTask;
    }

    private static Task TestResolutionPresetsAsync()
    {
        string[] expectedIds = ["source", "720p", "1080p", "1440p", "2160p"];
        Assert.SequenceEqual(
            expectedIds,
            ResolutionOption.All.Select(option => option.Id),
            "Resolution presets are incomplete or out of order.");

        var fullHd = ResolutionOption.All.Single(option => option.Id == "1080p");
        Assert.Equal(1920, fullHd.Width, "1080p width is incorrect.");
        Assert.Equal(1080, fullHd.Height, "1080p height is incorrect.");

        return Task.CompletedTask;
    }

    private static Task TestDefaultSaveDirectoryAsync()
    {
        var path = AppSettings.GetDefaultSaveDirectory();
        Assert.True(Path.IsPathRooted(path), "The default save directory must be absolute.");
        Assert.Equal("ClipForge", Path.GetFileName(path), "The default save directory should have a ClipForge folder.");

        var expectedSettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipForge");
        Assert.Equal(
            expectedSettingsDirectory,
            SettingsService.GetDefaultSettingsDirectory(),
            "Settings must be stored under the user's local application data folder.");
        Assert.True(new AppSettings().CheckForUpdatesAutomatically, "Automatic update checks should default on.");
        var defaults = new AppSettings();
        Assert.Equal(HotkeyGesture.DefaultSaveClip, defaults.SaveClipHotkey, "The Save Clip hotkey default is incorrect.");
        Assert.Equal(
            HotkeyGesture.DefaultToggleOverlay,
            defaults.ToggleOverlayHotkey,
            "The Toggle Overlay hotkey default is incorrect.");
        return Task.CompletedTask;
    }

    private static Task TestHotkeyGesturesAsync()
    {
        var saveClip = HotkeyGesture.DefaultSaveClip;
        var toggleOverlay = HotkeyGesture.DefaultToggleOverlay;

        Assert.True(saveClip.IsValid, "The default Save Clip hotkey must be valid.");
        Assert.True(toggleOverlay.IsValid, "The default Toggle Overlay hotkey must be valid.");
        Assert.True(saveClip != toggleOverlay, "The two global actions must not share a default hotkey.");
        Assert.Equal("Ctrl + Shift + F10", saveClip.DisplayText, "The Save Clip display text is not user friendly.");
        Assert.Equal(
            "Ctrl + Shift + F9",
            toggleOverlay.DisplayText,
            "The Toggle Overlay display text is not user friendly.");

        var bareKey = new HotkeyGesture(HotkeyModifiers.None, Key.F10);
        Assert.True(!bareKey.TryValidate(out _), "A bare global key should be rejected.");
        var modifierOnly = new HotkeyGesture(HotkeyModifiers.Control, Key.LeftShift);
        Assert.True(!modifierOnly.TryValidate(out _), "A modifier-only gesture should be rejected.");
        var reservedDebuggerKey = new HotkeyGesture(HotkeyModifiers.Control, Key.F12);
        Assert.True(!reservedDebuggerKey.TryValidate(out _), "The debugger-reserved F12 key should be rejected.");

        return Task.CompletedTask;
    }

    private static Task TestReleaseMetadataAsync()
    {
        var numericVersion = ReleaseInfo.Version.Split('-', 2)[0];
        var parts = numericVersion.Split('.');
        Assert.True(parts.Length >= 3, "The product version must contain major, minor, and patch numbers.");
        Assert.True(parts.Take(3).All(part => int.TryParse(part, out _)), "The product version must be semantic.");
        return Task.CompletedTask;
    }

    private static async Task TestUnconfiguredUpdaterAsync()
    {
        using var service = new AppUpdateService();
        Assert.Equal(AppUpdateState.Disabled, service.Snapshot.State, "A raw developer build has no update feed.");
        Assert.True(!service.CanCheck, "A raw developer build must not make update network requests.");
        await service.CheckAsync().ConfigureAwait(false);
        Assert.Equal(AppUpdateState.Disabled, service.Snapshot.State, "A disabled update check should remain non-fatal.");
    }

    private static Task TestCaptureArgumentsAsync()
    {
        var configuration = new CaptureConfiguration(
            new DisplayOption(@"\\.\DISPLAY2", "Display 2", -1920, 40, 1920, 1080, false),
            ResolutionOption.All.Single(option => option.Id == "720p"),
            60,
            TimeSpan.FromMinutes(2),
            true,
            new AudioDeviceOption("output", "Speakers"),
            true,
            new AudioDeviceOption("microphone", "Microphone"),
            @"C:\Clips");
        AudioInputSpecification[] audioInputs =
        [
            new(@"\\.\pipe\desktop", "f32le", 48000, 2),
            new(@"\\.\pipe\microphone", "s16le", 44100, 1)
        ];

        var arguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            audioInputs,
            @"C:\Buffer");

        Assert.ContainsSequence(arguments, "-offset_x", "-1920", "-offset_y", "40");
        Assert.ContainsSequence(arguments, "-video_size", "1920x1080", "-i", "desktop");
        Assert.ContainsSequence(arguments, "-thread_queue_size", "8", "-f", "gdigrab");
        Assert.ContainsSequence(arguments, "-c:v", "libx264", "-preset", "ultrafast");
        Assert.ContainsSequence(arguments, "-g", "120", "-keyint_min", "120");
        Assert.ContainsSequence(arguments, "-f", "segment", "-segment_time", "2");
        Assert.True(
            arguments.Any(argument => argument.Contains("amix=inputs=2", StringComparison.Ordinal)),
            "Two selected audio endpoints must be mixed.");
        Assert.True(
            arguments[^1].EndsWith("segment-%09d.mkv", StringComparison.Ordinal),
            "Capture output must be a numbered Matroska segment pattern.");

        return Task.CompletedTask;
    }

    private static Task TestEncoderStrategiesAsync()
    {
        var configuration = CreateCaptureConfiguration(monitorIndex: 3);

        var nvenc = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.Gdi),
            @"C:\Buffer");
        Assert.ContainsSequence(nvenc, "-c:v", "h264_nvenc", "-preset", "p4");
        Assert.ContainsSequence(nvenc, "-rc-lookahead", "0", "-surfaces", "4", "-bf", "0");
        Assert.ContainsSequence(nvenc, "-forced-idr", "1");
        Assert.True(
            nvenc.Any(argument => argument.EndsWith("format=nv12", StringComparison.Ordinal)),
            "GDI hardware capture should convert frames to bounded NV12 input.");

        var quickSync = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            new VideoEncodingStrategy(VideoEncoderKind.IntelQuickSync, DesktopCaptureBackend.Gdi),
            @"C:\Buffer");
        Assert.ContainsSequence(quickSync, "-c:v", "h264_qsv", "-preset", "veryfast");
        Assert.ContainsSequence(quickSync, "-look_ahead", "0", "-async_depth", "2");
        Assert.ContainsSequence(quickSync, "-forced_idr", "1");

        var amf = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            new VideoEncodingStrategy(VideoEncoderKind.AmdAmf, DesktopCaptureBackend.Gdi),
            @"C:\Buffer");
        Assert.ContainsSequence(amf, "-c:v", "h264_amf", "-usage", "lowlatency");
        Assert.ContainsSequence(amf, "-async_depth", "2", "-preanalysis", "false", "-bf", "0");
        Assert.ContainsSequence(amf, "-forced_idr", "true");

        var software = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            VideoEncodingStrategy.SoftwareGdi,
            @"C:\Buffer");
        Assert.ContainsSequence(software, "-preset", "ultrafast", "-tune", "zerolatency");
        Assert.ContainsSequence(
            software,
            "-threads",
            FfmpegArgumentBuilder.GetSoftwareEncoderThreadCount(Environment.ProcessorCount).ToString());
        Assert.Equal(1, FfmpegArgumentBuilder.GetSoftwareEncoderThreadCount(1), "One-core fallback is invalid.");
        Assert.Equal(4, FfmpegArgumentBuilder.GetSoftwareEncoderThreadCount(64), "Software threads must be capped.");

        var graphicsNvenc = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            new VideoEncodingStrategy(
                VideoEncoderKind.NvidiaNvenc,
                DesktopCaptureBackend.WindowsGraphicsCapture),
            @"C:\Buffer");
        Assert.True(
            graphicsNvenc.Any(argument => argument.Contains(
                "gfxcapture=monitor_idx=3:capture_cursor=1:max_framerate=60",
                StringComparison.Ordinal)),
            "Windows Graphics Capture must retain the selected zero-based monitor index.");
        Assert.True(
            !graphicsNvenc.Contains("gdigrab", StringComparer.Ordinal),
            "The low-overhead graphics backend must not also start GDI capture.");
        Assert.True(
            !graphicsNvenc.Any(argument => argument.Contains("hwdownload", StringComparison.Ordinal)),
            "Direct graphics capture should keep frames on the GPU.");

        var amfTransferStrategy = new VideoEncodingStrategy(
            VideoEncoderKind.AmdAmf,
            DesktopCaptureBackend.WindowsGraphicsCapture,
            RequiresSystemMemoryTransfer: true);
        var graphicsAmfTransfer = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            amfTransferStrategy,
            @"C:\Buffer");
        Assert.True(
            graphicsAmfTransfer.Any(argument => argument.Contains(
                "hwdownload,format=bgra",
                StringComparison.Ordinal)),
            "AMF must support WGC compatibility transfer on multi-GPU systems.");
        Assert.True(
            amfTransferStrategy.Description.Contains("compatibility transfer", StringComparison.Ordinal),
            "Diagnostics should distinguish the multi-GPU compatibility path.");

        var graphicsQsvProbe = FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
            configuration,
            new VideoEncodingStrategy(
                VideoEncoderKind.IntelQuickSync,
                DesktopCaptureBackend.WindowsGraphicsCapture,
                RequiresSystemMemoryTransfer: true));
        Assert.True(
            graphicsQsvProbe.Any(argument => argument.Contains(
                "hwdownload,format=bgra,format=nv12",
                StringComparison.Ordinal)),
            "Quick Sync graphics capture must use its supported NV12 system-memory format.");
        Assert.Equal(ProcessPriorityClass.BelowNormal, ProcessTuning.CapturePriority,
            "Capture processes should yield CPU time to the foreground game.");

        return Task.CompletedTask;
    }

    private static async Task TestEncoderCapabilityPriorityAsync()
    {
        Assert.Equal(
            VideoEncoderKind.NvidiaNvenc,
            FfmpegCapabilityProbe.SelectBestEncoder(true, true, true),
            "NVENC should be preferred when it passes its runtime probe.");
        Assert.Equal(
            VideoEncoderKind.IntelQuickSync,
            FfmpegCapabilityProbe.SelectBestEncoder(false, true, true),
            "Quick Sync should be preferred after NVENC.");
        Assert.Equal(
            VideoEncoderKind.AmdAmf,
            FfmpegCapabilityProbe.SelectBestEncoder(false, false, true),
            "AMF should be preferred after NVENC and Quick Sync.");
        Assert.Equal(
            VideoEncoderKind.SoftwareX264,
            FfmpegCapabilityProbe.SelectBestEncoder(false, false, false),
            "Software H.264 must remain the universal fallback.");

        var configuration = CreateCaptureConfiguration(monitorIndex: 1);
        await AssertProbeSelectionAsync(
            configuration,
            VideoEncoderKind.NvidiaNvenc,
            directGraphicsCaptureAvailable: true,
            transferGraphicsCaptureAvailable: false,
            expectedEncoder: VideoEncoderKind.NvidiaNvenc,
            expectedBackend: DesktopCaptureBackend.WindowsGraphicsCapture,
            expectedTransfer: false);
        await AssertProbeSelectionAsync(
            configuration,
            VideoEncoderKind.IntelQuickSync,
            directGraphicsCaptureAvailable: false,
            transferGraphicsCaptureAvailable: true,
            expectedEncoder: VideoEncoderKind.IntelQuickSync,
            expectedBackend: DesktopCaptureBackend.WindowsGraphicsCapture,
            expectedTransfer: true);
        await AssertProbeSelectionAsync(
            configuration,
            VideoEncoderKind.AmdAmf,
            directGraphicsCaptureAvailable: false,
            transferGraphicsCaptureAvailable: true,
            expectedEncoder: VideoEncoderKind.AmdAmf,
            expectedBackend: DesktopCaptureBackend.WindowsGraphicsCapture,
            expectedTransfer: true);
        await AssertProbeSelectionAsync(
            configuration,
            VideoEncoderKind.IntelQuickSync,
            directGraphicsCaptureAvailable: false,
            transferGraphicsCaptureAvailable: false,
            expectedEncoder: VideoEncoderKind.IntelQuickSync,
            expectedBackend: DesktopCaptureBackend.Gdi,
            expectedTransfer: false);
        await AssertProbeSelectionAsync(
            configuration,
            VideoEncoderKind.SoftwareX264,
            directGraphicsCaptureAvailable: false,
            transferGraphicsCaptureAvailable: false,
            expectedEncoder: VideoEncoderKind.SoftwareX264,
            expectedBackend: DesktopCaptureBackend.Gdi,
            expectedTransfer: false);
    }

    private static Task TestCaptureDiagnosticPriorityAsync()
    {
        string[] diagnostics =
        [
            "[gdigrab] Failed to capture image (error 5)",
            "Output file does not contain any stream",
            "Nothing was written into output file"
        ];
        Assert.Equal(
            diagnostics[0],
            ReplayBufferService.SelectMostUsefulDiagnostic(diagnostics),
            "Capture failures should show the actionable device error, not FFmpeg's generic final line.");
        return Task.CompletedTask;
    }

    private static async Task AssertProbeSelectionAsync(
        CaptureConfiguration configuration,
        VideoEncoderKind availableEncoder,
        bool directGraphicsCaptureAvailable,
        bool transferGraphicsCaptureAvailable,
        VideoEncoderKind expectedEncoder,
        DesktopCaptureBackend expectedBackend,
        bool expectedTransfer)
    {
        var runner = new ScriptedProbeRunner((arguments) =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var requestedEncoder = encoderName switch
            {
                "h264_nvenc" => VideoEncoderKind.NvidiaNvenc,
                "h264_qsv" => VideoEncoderKind.IntelQuickSync,
                "h264_amf" => VideoEncoderKind.AmdAmf,
                _ => VideoEncoderKind.SoftwareX264
            };
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            var isTransfer = arguments.Any(argument =>
                argument.Contains("hwdownload", StringComparison.Ordinal));
            return requestedEncoder == availableEncoder &&
                   (!isGraphicsCapture ||
                    (isTransfer
                        ? transferGraphicsCaptureAvailable
                        : directGraphicsCaptureAvailable));
        });
        var probe = new FfmpegCapabilityProbe(runner);
        var result = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(expectedEncoder, result.Strategy.Encoder, "Runtime probe chose the wrong encoder.");
        Assert.Equal(expectedBackend, result.Strategy.CaptureBackend, "Runtime probe chose the wrong backend.");
        Assert.Equal(expectedTransfer, result.Strategy.RequiresSystemMemoryTransfer,
            "Runtime probe chose the wrong graphics transfer mode.");
        Assert.True(runner.CallCount is >= 1 and <= 5, "Runtime probing performed an unexpected number of checks.");

        var completedProbeCalls = runner.CallCount;
        var cachedResult = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(result.Strategy, cachedResult.Strategy, "A cached probe changed strategy.");
        Assert.Equal(completedProbeCalls, runner.CallCount, "A completed capability probe should be cached.");
    }

    private static string? GetArgumentAfter(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static CaptureConfiguration CreateCaptureConfiguration(int monitorIndex) => new(
        new DisplayOption(@"\\.\DISPLAY4", "Display 4", 0, 0, 2560, 1440, true, monitorIndex),
        ResolutionOption.All.Single(option => option.Id == "1080p"),
        60,
        TimeSpan.FromMinutes(2),
        false,
        null,
        false,
        null,
        @"C:\Clips");

    private static Task TestConcatArgumentsAsync()
    {
        var arguments = FfmpegArgumentBuilder.BuildConcatArguments(
            @"C:\Buffer\manifest.txt",
            @"C:\Clips\clip.mp4",
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(30));

        Assert.ContainsSequence(arguments, "-f", "concat", "-safe", "0");
        Assert.ContainsSequence(arguments, "-ss", "1.5", "-t", "30");
        Assert.ContainsSequence(arguments, "-map", "0:v:0", "-map", "0:a?");
        Assert.ContainsSequence(arguments, "-c:v", "libx264", "-preset", "veryfast");
        Assert.ContainsSequence(arguments, "-c:a", "aac", "-b:a", "192k");
        Assert.Equal(@"C:\Clips\clip.mp4", arguments[^1], "The output path must remain one argument.");

        var remuxArguments = FfmpegArgumentBuilder.BuildConcatArguments(
            @"C:\Buffer\manifest.txt",
            @"C:\Clips\whole-segments.mp4",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30));
        Assert.ContainsSequence(remuxArguments, "-c", "copy", "-avoid_negative_ts", "make_zero");

        return Task.CompletedTask;
    }

    private static Task TestConfiguredFfmpegDiscoveryAsync()
    {
        var testDirectory = CreateTestDirectory();
        var previous = Environment.GetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH");

        try
        {
            Directory.CreateDirectory(testDirectory);
            var executable = Path.Combine(testDirectory, "ffmpeg.exe");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            Environment.SetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH", executable);

            var service = new FfmpegSetupService(Path.Combine(testDirectory, "private"));
            Assert.Equal(
                Path.GetFullPath(executable),
                service.FindExecutable(),
                "The explicitly configured FFmpeg path should take priority.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH", previous);
            DeleteTestDirectory(testDirectory);
        }

        return Task.CompletedTask;
    }

    private static Task TestStorageEstimatorAsync()
    {
        var display = new DisplayOption("DISPLAY1", "Primary display", 0, 0, 1920, 1080, true);
        var resolution = ResolutionOption.All.Single(option => option.Id == "1080p");
        var withoutAudio = StorageEstimator.EstimateBufferBytes(
            display,
            resolution,
            framesPerSecond: 30,
            duration: TimeSpan.FromMinutes(1),
            hasAudio: false);
        var withAudio = StorageEstimator.EstimateBufferBytes(
            display,
            resolution,
            framesPerSecond: 30,
            duration: TimeSpan.FromMinutes(1),
            hasAudio: true);

        Assert.True(withoutAudio > 0, "A non-empty replay should have a positive storage estimate.");
        Assert.Equal(1_440_000L, withAudio - withoutAudio, "The audio allowance is incorrect.");
        Assert.Equal("0 B", StorageEstimator.FormatBytes(-1), "Negative byte counts should be clamped to zero.");
        Assert.Equal("1 KB", StorageEstimator.FormatBytes(1024), "One kibibyte should format as 1 KB.");
        Assert.Equal("1.5 KB", StorageEstimator.FormatBytes(1536), "Fractional units should use one decimal place.");

        return Task.CompletedTask;
    }

    private static async Task TestSettingsRoundtripAsync()
    {
        var testDirectory = CreateTestDirectory();

        try
        {
            using var service = new SettingsService(testDirectory);
            var expected = new AppSettings
            {
                ReplaySeconds = 600,
                ResolutionId = "1440p",
                FramesPerSecond = 60,
                DisplayDeviceName = @"\\.\DISPLAY2",
                CaptureSystemAudio = false,
                OutputAudioDeviceId = "output-device",
                CaptureMicrophone = true,
                MicrophoneDeviceId = "microphone-device",
                CheckForUpdatesAutomatically = false,
                SaveClipHotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.F8),
                ToggleOverlayHotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.O),
                SaveDirectory = Path.Combine(testDirectory, "Clips")
            };

            await service.SaveAsync(expected).ConfigureAwait(false);
            var actual = await service.LoadAsync().ConfigureAwait(false);

            Assert.True(File.Exists(service.SettingsPath), "The settings file was not created.");
            Assert.Equal(expected.ReplaySeconds, actual.ReplaySeconds, "Replay duration did not roundtrip.");
            Assert.Equal(expected.ResolutionId, actual.ResolutionId, "Resolution did not roundtrip.");
            Assert.Equal(expected.FramesPerSecond, actual.FramesPerSecond, "Frame rate did not roundtrip.");
            Assert.Equal(expected.DisplayDeviceName, actual.DisplayDeviceName, "Display did not roundtrip.");
            Assert.Equal(expected.CaptureSystemAudio, actual.CaptureSystemAudio, "System audio setting did not roundtrip.");
            Assert.Equal(expected.OutputAudioDeviceId, actual.OutputAudioDeviceId, "Output device did not roundtrip.");
            Assert.Equal(expected.CaptureMicrophone, actual.CaptureMicrophone, "Microphone setting did not roundtrip.");
            Assert.Equal(expected.MicrophoneDeviceId, actual.MicrophoneDeviceId, "Microphone device did not roundtrip.");
            Assert.Equal(
                expected.CheckForUpdatesAutomatically,
                actual.CheckForUpdatesAutomatically,
                "Automatic update preference did not roundtrip.");
            Assert.Equal(expected.SaveClipHotkey, actual.SaveClipHotkey, "Save Clip hotkey did not roundtrip.");
            Assert.Equal(
                expected.ToggleOverlayHotkey,
                actual.ToggleOverlayHotkey,
                "Toggle Overlay hotkey did not roundtrip.");
            var settingsJson = await File.ReadAllTextAsync(service.SettingsPath).ConfigureAwait(false);
            Assert.True(
                settingsJson.Contains("\"F8\"", StringComparison.Ordinal),
                "Hotkey keys should be stored as readable enum names.");
            Assert.Equal(expected.SaveDirectory, actual.SaveDirectory, "Save directory did not roundtrip.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestMalformedSettingsFallbackAsync()
    {
        var testDirectory = CreateTestDirectory();

        try
        {
            Directory.CreateDirectory(testDirectory);
            using var service = new SettingsService(testDirectory);
            await File.WriteAllTextAsync(service.SettingsPath, "{ this is not valid json").ConfigureAwait(false);

            var settings = await service.LoadAsync().ConfigureAwait(false);
            Assert.Equal(120, settings.ReplaySeconds, "Malformed JSON should fall back to defaults.");
            Assert.Equal("1080p", settings.ResolutionId, "Malformed JSON should fall back to defaults.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestOversizedSettingsFallbackAsync()
    {
        var testDirectory = CreateTestDirectory();

        try
        {
            Directory.CreateDirectory(testDirectory);
            using var service = new SettingsService(testDirectory);
            var oversizedButValidJson =
                $"{{\"replaySeconds\":30,\"padding\":\"{new string('a', 1024 * 1024)}\"}}";
            await File.WriteAllTextAsync(service.SettingsPath, oversizedButValidJson).ConfigureAwait(false);

            var settings = await service.LoadAsync().ConfigureAwait(false);
            Assert.Equal(120, settings.ReplaySeconds, "Settings larger than 1 MiB must be ignored before parsing.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestClipLibraryAsync()
    {
        var testDirectory = CreateTestDirectory();
        var clipsDirectory = Path.Combine(testDirectory, "Clips");
        var cacheDirectory = Path.Combine(testDirectory, "Cache");
        var toolsDirectory = Path.Combine(testDirectory, "Tools");

        try
        {
            Directory.CreateDirectory(clipsDirectory);
            Directory.CreateDirectory(toolsDirectory);
            var ffmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            var ffprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            await File.WriteAllBytesAsync(ffmpegPath, [0x4D, 0x5A]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ffprobePath, [0x4D, 0x5A]).ConfigureAwait(false);

            var oldest = Path.Combine(clipsDirectory, "older.MP4");
            var newest = Path.Combine(clipsDirectory, "Clip & pretend-command.mp4");
            var corrupt = Path.Combine(clipsDirectory, "corrupt.mp4");
            var empty = Path.Combine(clipsDirectory, "empty.mp4");
            await File.WriteAllBytesAsync(oldest, [1, 2, 3]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(newest, [4, 5, 6]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(corrupt, [7, 8, 9]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(empty, []).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(clipsDirectory, "not-a-clip.txt"), "ignored")
                .ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(clipsDirectory, "Nested"));
            await File.WriteAllBytesAsync(
                    Path.Combine(clipsDirectory, "Nested", "nested.mp4"),
                    [10, 11, 12])
                .ConfigureAwait(false);

            File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newest, new DateTime(2026, 1, 2, 1, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(corrupt, new DateTime(2026, 1, 3, 1, 0, 0, DateTimeKind.Utc));

            var runner = new FakeClipMediaProcessRunner();
            var service = new ClipLibraryService(
                () => ffmpegPath,
                () => ffprobePath,
                runner,
                cacheDirectory,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3));

            var clips = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 5,
                    includeThumbnails: true)
                .ConfigureAwait(false);

            Assert.Equal(2, clips.Count, "Only non-empty, top-level, playable MP4 files should be returned.");
            Assert.Equal(Path.GetFileName(newest), clips[0].FileName, "Clips should be ordered newest first.");
            Assert.Equal(Path.GetFullPath(newest), clips[0].FullPath, "Clip paths should be normalized.");
            Assert.Equal(TimeSpan.FromSeconds(42.5), clips[0].Duration, "ffprobe duration should be exposed.");
            Assert.True(
                clips.All(item => item.ThumbnailPath is not null && File.Exists(item.ThumbnailPath)),
                "Playable clips should receive cached thumbnails.");

            var snapshot = new ClipLibrarySnapshot(clips);
            Assert.Equal(clips[0], snapshot.LatestClip, "The latest clip helper is incorrect.");
            Assert.Equal(2, snapshot.GalleryClips.Count, "The gallery should expose available recent clips.");

            var unsafeLookingProbe = runner.Invocations.Single(invocation =>
                Path.GetFileName(invocation.ExecutablePath).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase) &&
                invocation.Arguments[^1].Equals(newest, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(newest, unsafeLookingProbe.Arguments[^1], "The clip path must remain one process argument.");
            Assert.Equal(TimeSpan.FromSeconds(2), unsafeLookingProbe.Timeout, "Probe timeout was not enforced.");

            var thumbnailRuns = runner.ThumbnailRunCount;
            var secondLoad = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 5,
                    includeThumbnails: true)
                .ConfigureAwait(false);
            Assert.Equal(
                thumbnailRuns,
                runner.ThumbnailRunCount,
                "A deterministic valid thumbnail should be reused from cache.");
            Assert.Equal(
                clips[0].ThumbnailPath,
                secondLoad[0].ThumbnailPath,
                "An unchanged clip must have a deterministic thumbnail cache key.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static Task TestClipLibrarySecurityPolicyAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClipForge-Security-Root"));
        var valid = Path.Combine(root, "Clip.mp4");
        var nested = Path.Combine(root, "Nested", "Clip.mp4");
        var traversal = Path.Combine(root, "..", "outside.mp4");

        Assert.True(
            ClipLibraryService.IsSafeTopLevelClipPath(root, valid, FileAttributes.Normal),
            "A regular top-level MP4 should pass the path policy.");
        Assert.True(
            !ClipLibraryService.IsSafeTopLevelClipPath(root, nested, FileAttributes.Normal),
            "Nested files must not escape the top-level clip policy.");
        Assert.True(
            !ClipLibraryService.IsSafeTopLevelClipPath(root, traversal, FileAttributes.Normal),
            "Traversal paths must be rejected.");
        Assert.True(
            !ClipLibraryService.IsSafeTopLevelClipPath(root, valid, FileAttributes.ReparsePoint),
            "Reparse-point clip files must be rejected.");
        Assert.True(
            !ClipLibraryService.IsSafeTopLevelClipPath(root, $"{valid}.exe", FileAttributes.Normal),
            "A disguised executable must not be treated as MP4 media.");

        string[] arguments = ["-i", valid, "argument & not-a-command"];
        var startInfo = ClipMediaProcessRunner.CreateStartInfo(@"C:\Tools\ffmpeg.exe", arguments);
        Assert.True(!startInfo.UseShellExecute, "Media tools must never use shell execution.");
        Assert.True(startInfo.CreateNoWindow, "Media tools should not create a console window.");
        Assert.SequenceEqual(arguments, startInfo.ArgumentList, "Media arguments must use ArgumentList unchanged.");

        var thumbnailArguments = ClipLibraryService.BuildThumbnailArguments(
            valid,
            Path.Combine(root, "thumbnail.jpg"),
            TimeSpan.FromSeconds(20));
        Assert.ContainsSequence(thumbnailArguments, "-nostdin", "-y", "-threads", "1");
        Assert.ContainsSequence(thumbnailArguments, "-i", valid, "-map", "0:v:0");
        return Task.CompletedTask;
    }

    private static Task TestRuntimeLocalDataBoundariesAsync()
    {
        Assert.True(
            WasapiAudioPipe.ServerPipeOptions.HasFlag(PipeOptions.CurrentUserOnly),
            "The private audio pipe must reject clients running as another Windows user.");

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClipForge-Buffer-Root"));
        var session = Path.Combine(root, "session-20260712-test");
        Assert.True(
            ReplayBufferService.IsSafeBufferDirectoryPath(root, session, FileAttributes.Directory),
            "A regular top-level replay session should pass the cleanup policy.");
        Assert.True(
            !ReplayBufferService.IsSafeBufferDirectoryPath(
                root,
                session,
                FileAttributes.Directory | FileAttributes.ReparsePoint),
            "Replay cleanup must reject junctions and symbolic links.");
        Assert.True(
            !ReplayBufferService.IsSafeBufferDirectoryPath(
                root,
                Path.Combine(root, "Nested", "session-escape"),
                FileAttributes.Directory),
            "Replay cleanup must reject nested directories.");
        Assert.True(
            !ReplayBufferService.IsSafeBufferDirectoryPath(
                root,
                Path.Combine(root, "..", "session-escape"),
                FileAttributes.Directory),
            "Replay cleanup must reject traversal outside the buffer root.");
        Assert.True(
            !ReplayBufferService.IsSafeBufferDirectoryPath(
                root,
                Path.Combine(root, "unrelated-directory"),
                FileAttributes.Directory),
            "Replay cleanup must only delete ClipForge session directories.");

        return Task.CompletedTask;
    }

    private static async Task TestClipLibraryCancellationAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            Directory.CreateDirectory(testDirectory);
            await File.WriteAllBytesAsync(Path.Combine(testDirectory, "clip.mp4"), [1]).ConfigureAwait(false);
            var runner = new FakeClipMediaProcessRunner();
            var service = new ClipLibraryService(
                () => null,
                () => null,
                runner,
                Path.Combine(testDirectory, "Cache"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var cancelled = false;
            try
            {
                await service.GetRecentClipsAsync(
                        testDirectory,
                        cancellationToken: cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.True(cancelled, "A cancelled library load must stop promptly.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static string CreateTestDirectory() =>
        Path.Combine(Path.GetTempPath(), "ClipForge.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class ScriptedProbeRunner(
        Func<IReadOnlyList<string>, bool> succeeds) : IFfmpegProbeRunner
    {
        public int CallCount { get; private set; }

        public Task<FfmpegProbeExecution> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(succeeds(arguments)
                ? new FfmpegProbeExecution(true)
                : new FfmpegProbeExecution(false, "scripted unavailable capability"));
        }
    }

    private sealed class FakeClipMediaProcessRunner : IClipMediaProcessRunner
    {
        public List<Invocation> Invocations { get; } = [];

        public int ThumbnailRunCount { get; private set; }

        public async Task<ClipMediaProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(new Invocation(executablePath, arguments.ToArray(), timeout));

            if (Path.GetFileName(executablePath).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(arguments[^1]).Equals("corrupt.mp4", StringComparison.OrdinalIgnoreCase)
                    ? new ClipMediaProcessResult(1, string.Empty, "invalid media", false)
                    : new ClipMediaProcessResult(
                        0,
                        "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"42.5\"}}",
                        string.Empty,
                        false);
            }

            ThumbnailRunCount++;
            var outputPath = arguments[^1];
            await File.WriteAllBytesAsync(
                    outputPath,
                    [0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xD9],
                    cancellationToken)
                .ConfigureAwait(false);
            return new ClipMediaProcessResult(0, string.Empty, string.Empty, false);
        }

        public sealed record Invocation(
            string ExecutablePath,
            IReadOnlyList<string> Arguments,
            TimeSpan Timeout);
    }

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
            }
        }

        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
        {
            if (!expected.SequenceEqual(actual))
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void ContainsSequence<T>(IReadOnlyList<T> actual, params T[] expected)
        {
            for (var start = 0; start <= actual.Count - expected.Length; start++)
            {
                var found = true;
                for (var offset = 0; offset < expected.Length; offset++)
                {
                    if (!EqualityComparer<T>.Default.Equals(actual[start + offset], expected[offset]))
                    {
                        found = false;
                        break;
                    }
                }

                if (found)
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"Expected contiguous sequence was not found: {string.Join(", ", expected)}.");
        }
    }
}
