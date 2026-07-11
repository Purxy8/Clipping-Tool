using ClipForge.Models;
using ClipForge.Services;
using ClipForge.Capture;

namespace ClipForge.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Replay length preset catalog", TestReplayLengthPresetsAsync),
            ("Resolution preset catalog", TestResolutionPresetsAsync),
            ("Storage estimate helpers", TestStorageEstimatorAsync),
            ("FFmpeg capture arguments", TestCaptureArgumentsAsync),
            ("FFmpeg concat arguments", TestConcatArgumentsAsync),
            ("Configured FFmpeg discovery", TestConfiguredFfmpegDiscoveryAsync),
            ("Release metadata", TestReleaseMetadataAsync),
            ("Unconfigured updater is non-fatal", TestUnconfiguredUpdaterAsync),
            ("Default save directory", TestDefaultSaveDirectoryAsync),
            ("Settings JSON roundtrip", TestSettingsRoundtripAsync),
            ("Malformed settings fallback", TestMalformedSettingsFallbackAsync)
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

    private static string CreateTestDirectory() =>
        Path.Combine(Path.GetTempPath(), "ClipForge.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
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
