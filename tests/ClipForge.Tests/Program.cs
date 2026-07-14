using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using ClipForge.Controls;
using ClipForge.Models;
using ClipForge.Services;
using ClipForge.Capture;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipForge.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Replay length preset catalog", TestReplayLengthPresetsAsync),
            ("Resolution preset catalog", TestResolutionPresetsAsync),
            ("Windows autostart launch options", TestLaunchOptionsAsync),
            ("Windows autostart registration policy", TestStartupRegistrationAsync),
            ("Windows autostart replay decision", TestAutoStartReplayPolicyAsync),
            ("Hotkey gesture validation", TestHotkeyGesturesAsync),
            ("Storage estimate helpers", TestStorageEstimatorAsync),
            ("FFmpeg capture arguments", TestCaptureArgumentsAsync),
            ("FFmpeg encoder strategies", TestEncoderStrategiesAsync),
            ("FFmpeg capability priority", TestEncoderCapabilityPriorityAsync),
            ("FFmpeg diagnostic prioritization", TestCaptureDiagnosticPriorityAsync),
            ("FFmpeg concat arguments", TestConcatArgumentsAsync),
            ("FFmpeg trim arguments", TestTrimArgumentsAsync),
            ("Trim range and output naming", TestTrimRangeAndNamingAsync),
            ("Configured FFmpeg discovery", TestConfiguredFfmpegDiscoveryAsync),
            ("Pinned FFmpeg trust policy", TestPinnedFfmpegTrustPolicyAsync),
            ("FFmpeg download byte limits", TestFfmpegDownloadLimitsAsync),
            ("Release metadata", TestReleaseMetadataAsync),
            ("Unconfigured updater is non-fatal", TestUnconfiguredUpdaterAsync),
            ("Updater channel selection", TestUpdaterChannelSelectionAsync),
            ("Default save directory", TestDefaultSaveDirectoryAsync),
            ("Background color policy", TestBackgroundColorPolicyAsync),
            ("Appearance and gallery layout", TestAppearanceAndGalleryAsync),
            ("Trim range selector policy", TestTrimRangeSelectorAsync),
            ("Library player open policy", TestLibraryPlayerOpenPolicyAsync),
            ("UI feedback helpers", TestUiFeedbackHelpersAsync),
            ("Settings JSON roundtrip", TestSettingsRoundtripAsync),
            ("Malformed settings fallback", TestMalformedSettingsFallbackAsync),
            ("Oversized settings fallback", TestOversizedSettingsFallbackAsync),
            ("Secure clip discovery and thumbnail cache", TestClipLibraryAsync),
            ("Clip classification and filtered discovery", TestClipClassificationAndFilteringAsync),
            ("Transactional clip trimming", TestClipTrimServiceAsync),
            ("Clip path and media process hardening", TestClipLibrarySecurityPolicyAsync),
            ("Race-resistant clip deletion", TestClipDeletionAsync),
            ("Thumbnail decoding releases cache files", TestThumbnailDecoderReleasesFileAsync),
            ("Clip library fail-closed probing", TestClipLibraryFailClosedAsync),
            ("Clip library probe budget", TestClipLibraryProbeBudgetAsync),
            ("Clip library probe result cache", TestClipLibraryProbeCacheAsync),
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
        Assert.True(
            !new AppSettings().CheckForUpdatesAutomatically,
            "Automatic update checks should require explicit opt-in.");
        var defaults = new AppSettings();
        Assert.True(
            !defaults.StartReplayWithWindows,
            "Starting replay with Windows must require explicit opt-in.");
        Assert.Equal(
            AppSettings.DefaultBackgroundColor,
            defaults.BackgroundColor,
            "The default background color is incorrect.");
        Assert.Equal(AppSettings.DefaultAccentColor, defaults.AccentColor, "The default accent color is incorrect.");
        Assert.Equal(AppSettings.DefaultSurfaceColor, defaults.SurfaceColor, "The default surface color is incorrect.");
        Assert.Equal(4, defaults.RecentClipCount, "The recent clip gallery should default to four items.");
        Assert.True(defaults.PlayClipSavedSound, "Saved-clip sound feedback should be enabled by default.");
        int[] expectedRecentClipCounts = [4, 8, 10, 15];
        Assert.SequenceEqual(
            expectedRecentClipCounts,
            expectedRecentClipCounts.Select(AppSettings.NormalizeRecentClipCount),
            "Supported recent clip counts should remain unchanged.");
        Assert.Equal(
            4,
            AppSettings.NormalizeRecentClipCount(100),
            "An unsupported persisted recent clip count must fall back to four.");
        Assert.Equal(HotkeyGesture.DefaultSaveClip, defaults.SaveClipHotkey, "The Save Clip hotkey default is incorrect.");
        Assert.Equal(
            HotkeyGesture.DefaultToggleOverlay,
            defaults.ToggleOverlayHotkey,
            "The Toggle Overlay hotkey default is incorrect.");
        return Task.CompletedTask;
    }

    private static Task TestLaunchOptionsAsync()
    {
        var interactive = AppLaunchOptions.Parse([]);
        Assert.True(!interactive.IsAutoStart, "A normal launch must remain interactive.");
        Assert.True(!interactive.StartInBackground, "A normal launch must not be hidden automatically.");
        Assert.True(
            interactive.ShouldActivateExistingInstance,
            "A normal second launch should activate the existing ClipForge window.");

        var autoStart = AppLaunchOptions.Parse(["--AUTOSTART"]);
        Assert.True(autoStart.IsAutoStart, "The fixed Windows Startup argument was not recognized.");
        Assert.True(autoStart.StartInBackground, "A Windows autostart launch should stay in the background.");
        Assert.True(
            !autoStart.ShouldActivateExistingInstance,
            "A duplicate Windows autostart launch must not unexpectedly raise the existing window.");
        Assert.True(
            !AppLaunchOptions.Parse(["--unrelated", "autostart"]).IsAutoStart,
            "Only the complete private autostart argument should change launch behavior.");
        Assert.Equal(
            AppLaunchOptions.Interactive,
            AppLaunchOptions.Parse(Array.Empty<string>()),
            "The explicit interactive options and an empty command line should agree.");
        Assert.Throws<ArgumentNullException>(
            () => AppLaunchOptions.Parse(null!),
            "A null argument collection must be rejected.");

        return Task.CompletedTask;
    }

    private static Task TestStartupRegistrationAsync()
    {
        var backend = new FakeStartupShortcutBackend { IsSupported = true };
        var service = new StartupRegistrationService(backend);

        Assert.True(service.IsSupported, "An installed backend should expose Windows startup support.");
        Assert.True(!service.IsEnabled, "A missing Startup shortcut must report the preference as disabled.");
        Assert.Equal(
            StartupRegistrationService.ApplicationExecutableName,
            backend.LastInspectedExecutable,
            "Startup lookup must be scoped to the packaged ClipForge executable.");

        service.SetEnabled(true);
        Assert.True(service.IsEnabled, "Creating the Startup shortcut should enable the feature.");
        Assert.Equal(
            StartupRegistrationService.ApplicationExecutableName,
            backend.LastCreatedExecutable,
            "The Startup shortcut must target only the packaged ClipForge executable name.");
        Assert.Equal(
            AppLaunchOptions.AutoStartArgument,
            backend.LastCreatedArguments,
            "The Startup shortcut must receive only ClipForge's fixed private argument.");

        service.SetEnabled(false);
        Assert.True(!service.IsEnabled, "Deleting the Startup shortcut should disable the feature.");
        Assert.Equal(
            StartupRegistrationService.ApplicationExecutableName,
            backend.LastDeletedExecutable,
            "Startup cleanup must remain scoped to the packaged ClipForge executable.");

        var unsupportedBackend = new FakeStartupShortcutBackend { IsSupported = false };
        var unsupportedService = new StartupRegistrationService(unsupportedBackend);
        Assert.True(!unsupportedService.IsSupported, "A portable/development backend must remain unsupported.");
        Assert.True(!unsupportedService.IsEnabled, "Unsupported builds must never report startup as enabled.");
        unsupportedService.SetEnabled(false);
        Assert.Equal(0, unsupportedBackend.DeleteCount, "Disabling unsupported startup should be a no-op.");
        Assert.Throws<InvalidOperationException>(
            () => unsupportedService.SetEnabled(true),
            "Portable/development builds must fail closed instead of creating an ambiguous startup entry.");

        return Task.CompletedTask;
    }

    private static Task TestAutoStartReplayPolicyAsync()
    {
        Assert.True(
            MainWindow.ShouldAutoStartReplay(
                isAutoStartLaunch: true,
                preferenceEnabled: true,
                initializationCompleted: true,
                engineReady: true,
                replayRunning: false,
                isClosing: false),
            "A ready opted-in Windows launch should start replay exactly once.");

        (bool IsAutoStart, bool Preference, bool Initialized, bool EngineReady, bool Running, bool Closing)[]
            blockedCases =
            [
                (false, true, true, true, false, false),
                (true, false, true, true, false, false),
                (true, true, false, true, false, false),
                (true, true, true, false, false, false),
                (true, true, true, true, true, false),
                (true, true, true, true, false, true)
            ];

        foreach (var item in blockedCases)
        {
            Assert.True(
                !MainWindow.ShouldAutoStartReplay(
                    item.IsAutoStart,
                    item.Preference,
                    item.Initialized,
                    item.EngineReady,
                    item.Running,
                    item.Closing),
                "Autostart replay must wait for every safety precondition and must not restart an active session.");
        }

        return Task.CompletedTask;
    }

    private static Task TestBackgroundColorPolicyAsync()
    {
        Assert.Equal(
            AppSettings.DefaultBackgroundColor,
            AppSettings.NormalizeBackgroundColor(null),
            "A missing background color must use the safe default.");
        Assert.Equal(
            AppSettings.DefaultBackgroundColor,
            AppSettings.NormalizeBackgroundColor("red"),
            "Malformed persisted color text must use the safe default.");
        Assert.Equal(
            "#161321",
            AppSettings.NormalizeBackgroundColor("#161321"),
            "A valid dark preset should remain unchanged.");
        Assert.Equal(
            "#0D1A19",
            AppSettings.NormalizeBackgroundColor("#0d1a19"),
            "Valid hex should be stored canonically.");
        Assert.Equal(
            "#303030",
            AppSettings.NormalizeBackgroundColor("#FFFFFF"),
            "A bright neutral custom color should be darkened for readable fixed typography.");
        Assert.Equal(
            "#300000",
            AppSettings.NormalizeBackgroundColor("#FF0000"),
            "Tone limiting should preserve the requested hue.");
        Assert.Equal(
            "#303030",
            AppSettings.NormalizeSurfaceColor("#FFFFFF"),
            "Bright surfaces must remain inside the readable dark range.");
        Assert.Equal(
            AppSettings.DefaultAccentColor,
            AppSettings.NormalizeAccentColor("invalid"),
            "Malformed accent values must use the tested default.");
        Assert.True(
            !string.Equals("#000000", AppSettings.NormalizeAccentColor("#000000"), StringComparison.Ordinal),
            "An invisible custom accent must be lifted into the visible range.");
        return Task.CompletedTask;
    }

    private static Task TestAppearanceAndGalleryAsync()
    {
        var palette = AppearanceThemePalette.Create("#0D1422", "#3B82F6", "#17131F");
        Assert.Equal("#0D1422", palette.BackgroundColor, "Background palette normalization changed a valid preset.");
        Assert.Equal("#3B82F6", palette.AccentColor, "Accent palette normalization changed a visible preset.");
        Assert.Equal("#17131F", palette.SurfaceColor, "Surface palette normalization changed a valid preset.");
        Assert.True(
            palette.SurfaceTranslucentColor.StartsWith("#A1", StringComparison.Ordinal),
            "The translucent surface palette must preserve its intended alpha channel.");
        Assert.True(
            palette.AccentSoftColor.StartsWith("#24", StringComparison.Ordinal),
            "The soft accent palette must preserve its intended alpha channel.");

        var parsedArgb = MainWindow.ParseThemeColor("#A112151D");
        Assert.Equal((byte)0xA1, parsedArgb.A, "ARGB theme parsing lost the alpha component.");
        Assert.Equal((byte)0x12, parsedArgb.R, "ARGB theme parsing mapped the red component incorrectly.");
        Assert.Equal((byte)0x15, parsedArgb.G, "ARGB theme parsing mapped the green component incorrectly.");
        Assert.Equal((byte)0x1D, parsedArgb.B, "ARGB theme parsing mapped the blue component incorrectly.");
        Assert.True(
            palette.PrimaryButtonTextColor is "#000000" or "#FFFFFF",
            "Primary button text must choose a deterministic contrasting color.");
        Assert.True(
            !string.Equals(palette.SurfaceColor, palette.SurfaceRaisedColor, StringComparison.Ordinal),
            "Raised controls need a visible derived surface color.");

        var rootResources = new System.Windows.ResourceDictionary();
        var originalSurfaceBrush = new SolidColorBrush(Colors.Black);
        var themeResources = new System.Windows.ResourceDictionary
        {
            ["WindowColor"] = Colors.Black,
            ["SurfaceBrush"] = originalSurfaceBrush
        };
        rootResources.MergedDictionaries.Add(themeResources);
        var replacementWindowColor = Color.FromRgb(0x18, 0x30, 0x30);
        Assert.True(
            MainWindow.SetThemeResourceValue(rootResources, "WindowColor", replacementWindowColor),
            "The appearance updater must find colors declared in a merged theme dictionary.");
        Assert.Equal(
            replacementWindowColor,
            (Color)themeResources["WindowColor"],
            "The appearance updater changed the wrong resource dictionary.");
        Assert.True(
            !rootResources.Keys.Cast<object>().Any(key => Equals(key, "WindowColor")),
            "The appearance updater must not create a root shadow that leaves theme brushes unchanged.");
        Assert.True(
            !MainWindow.SetThemeResourceValue(rootResources, "MissingColor", Colors.Red),
            "The appearance updater should fail closed when a theme key is missing.");
        var replacementSurfaceColor = Color.FromRgb(0x18, 0x18, 0x30);
        Assert.True(
            MainWindow.SetThemeBrushColorResource(rootResources, "SurfaceBrush", replacementSurfaceColor),
            "The appearance updater must refresh existing shared brush instances.");
        Assert.True(
            ReferenceEquals(originalSurfaceBrush, themeResources["SurfaceBrush"]),
            "The appearance updater should preserve live brush references whenever possible.");
        Assert.Equal(
            replacementSurfaceColor,
            originalSurfaceBrush.Color,
            "The appearance updater did not repaint controls holding a shared brush reference.");

        Assert.Equal("0 MB", ClipLibraryItem.FormatFileSize(0), "Zero-byte size formatting is incorrect.");
        Assert.Equal("<1 MB", ClipLibraryItem.FormatFileSize(512 * 1024), "Sub-megabyte size formatting is incorrect.");
        Assert.Equal("1.0 MB", ClipLibraryItem.FormatFileSize(1024 * 1024), "One-megabyte size formatting is incorrect.");
        Assert.Equal("1.5 MB", ClipLibraryItem.FormatFileSize(1536 * 1024), "Fractional size formatting is incorrect.");

        Assert.Equal(
            594d,
            MainWindow.CalculateRecentGalleryCardWidth(1200, requestedCount: 4, actualItemCount: 2),
            "Two available clips should fill the selected four-clip viewport without an empty half.");
        Assert.Equal(
            294d,
            MainWindow.CalculateRecentGalleryCardWidth(1200, requestedCount: 4, actualItemCount: 4),
            "Four recent clips should divide the viewport edge to edge.");
        Assert.Equal(
            234d,
            MainWindow.CalculateRecentGalleryCardWidth(1200, requestedCount: 8, actualItemCount: 8),
            "Eight-clip mode should use five compact visible slots before scrolling.");
        Assert.Equal(
            168d,
            MainWindow.CalculateRecentGalleryCardWidth(1200, requestedCount: 15, actualItemCount: 15),
            "Fifteen-clip mode must retain a readable minimum card width and scroll.");

        return Task.CompletedTask;
    }

    private static Task TestTrimRangeSelectorAsync() => RunOnStaThreadAsync(() =>
    {
        var selector = new TrimRangeSelector
        {
            Minimum = 0,
            Maximum = 10,
            MinimumSpan = 0.5,
            LowerValue = 2,
            UpperValue = 8
        };
        Assert.Equal(2d, selector.LowerValue, "A valid trim start should remain unchanged.");
        Assert.Equal(8d, selector.UpperValue, "A valid trim end should remain unchanged.");

        selector.LowerValue = 9.9;
        Assert.Equal(9.5d, selector.LowerValue,
            "The start handle must remain inside the duration and preserve the minimum span.");
        Assert.Equal(10d, selector.UpperValue,
            "Crossing the end handle should normalize to the clip boundary.");

        selector.UpperValue = -10;
        Assert.Equal(10d, selector.UpperValue,
            "The end handle must not cross an already clamped start handle.");
        Assert.True(selector.UpperValue - selector.LowerValue >= selector.MinimumSpan,
            "Range normalization violated the minimum trim span.");

        selector.Minimum = double.NaN;
        selector.Maximum = double.PositiveInfinity;
        selector.MinimumSpan = double.NegativeInfinity;
        selector.LowerValue = double.NaN;
        selector.UpperValue = double.PositiveInfinity;
        Assert.True(
            double.IsFinite(selector.Minimum) &&
            double.IsFinite(selector.Maximum) &&
            double.IsFinite(selector.MinimumSpan) &&
            double.IsFinite(selector.LowerValue) &&
            double.IsFinite(selector.UpperValue),
            "Non-finite trim selector input must normalize to finite values.");
        Assert.True(selector.Maximum > selector.Minimum,
            "A normalized trim selector must retain a positive total range.");
        Assert.True(
            selector.LowerValue >= selector.Minimum &&
            selector.UpperValue <= selector.Maximum &&
            selector.UpperValue >= selector.LowerValue + selector.MinimumSpan,
            "Normalized trim handles escaped their legal range.");
    });

    private static Task TestLibraryPlayerOpenPolicyAsync()
    {
        var backgroundLoad = LibraryMediaOpenPlan.Create(
            autoplay: false,
            playbackVolume: 0.73);
        Assert.Equal(0d, backgroundLoad.PrimeVolume, "Media priming must remain silent.");
        Assert.Equal(0.73, backgroundLoad.PlaybackVolume, "Requested player volume was not preserved.");
        Assert.True(
            backgroundLoad.MustPrimeWithPlay,
            "A manually controlled MediaElement must explicitly Play to build its media graph.");
        Assert.True(
            !backgroundLoad.ContinueAfterOpened,
            "A restored or programmatically selected clip must pause after the media graph opens.");

        var userSelection = LibraryMediaOpenPlan.Create(
            autoplay: true,
            playbackVolume: 2);
        Assert.Equal(1d, userSelection.PlaybackVolume, "Playback volume must be clamped to MediaElement limits.");
        Assert.True(
            userSelection.ContinueAfterOpened,
            "A foreground user selection should continue after muted media priming completes.");

        var requestedPath = @"C:\Clips\Clip_2026-07-13_15-00-00.mp4";
        Assert.True(
            !LibraryWindow.ShouldBeginRequestedTrim(
                requestPending: true,
                mediaReady: false,
                currentClipPath: requestedPath,
                requestedClipPath: requestedPath),
            "Direct trim must remain pending until MediaOpened establishes the real duration.");
        Assert.True(
            !LibraryWindow.ShouldBeginRequestedTrim(
                requestPending: true,
                mediaReady: true,
                currentClipPath: @"C:\Clips\Clip_2026-07-13_14-00-00.mp4",
                requestedClipPath: requestedPath),
            "A late MediaOpened event for another clip must not start the requested trim.");
        Assert.True(
            LibraryWindow.ShouldBeginRequestedTrim(
                requestPending: true,
                mediaReady: true,
                currentClipPath: requestedPath,
                requestedClipPath: requestedPath),
            "The exact direct-trim request should start once its media graph is ready.");
        Assert.True(
            MainWindow.ShouldHandlePlayerMediaEvent(
                captureCritical: false,
                isClosing: false,
                isVisible: true,
                isActive: true,
                hasCurrentClip: true,
                hasSource: true),
            "A foreground player event with a live source should be handled.");
        Assert.True(
            !MainWindow.ShouldHandlePlayerMediaEvent(
                captureCritical: true,
                isClosing: false,
                isVisible: true,
                isActive: true,
                hasCurrentClip: true,
                hasSource: true),
            "A queued MediaOpened event must not re-enable playback during capture.");
        Assert.True(
            !MainWindow.ShouldHandlePlayerMediaEvent(
                captureCritical: false,
                isClosing: false,
                isVisible: true,
                isActive: true,
                hasCurrentClip: true,
                hasSource: false),
            "A late player event without a source must be suppressed.");
        return Task.CompletedTask;
    }

    private static Task TestUiFeedbackHelpersAsync()
    {
        Assert.Equal(
            0x00332211u,
            NativeWindowThemeService.ToColorRef(0x11, 0x22, 0x33),
            "Native title-bar colors must use Win32 COLORREF byte order.");

        var wave = ClipSavedSoundService.CreateChimeWave();
        Assert.Equal(20_204, wave.Length, "The in-memory confirmation chime has an unexpected size.");
        Assert.SequenceEqual(
            new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' },
            wave.Take(4),
            "The confirmation chime is not a RIFF file.");
        Assert.SequenceEqual(
            new byte[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' },
            wave.Skip(8).Take(4),
            "The confirmation chime is not a WAVE stream.");
        Assert.Equal(20_160, BitConverter.ToInt32(wave, 40), "The PCM data length is incorrect.");

        var peak = Enumerable.Range(0, (wave.Length - 44) / sizeof(short))
            .Select(index => Math.Abs((int)BitConverter.ToInt16(wave, 44 + (index * sizeof(short)))))
            .Max();
        Assert.True(
            peak >= short.MaxValue * 0.26 && peak <= short.MaxValue * 0.28,
            "The confirmation pop should be clearly audible without approaching clipping.");

        var constructionTimer = Stopwatch.StartNew();
        using (var soundService = new ClipSavedSoundService())
        {
            soundService.TryPlay(enabled: false);
        }

        constructionTimer.Stop();
        Assert.True(
            constructionTimer.Elapsed < TimeSpan.FromSeconds(2),
            "Preparing optional sound feedback must not block application startup.");
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

    private static Task TestUpdaterChannelSelectionAsync()
    {
        Assert.True(
            !AppUpdateService.ShouldIncludePrereleases("1.2.0"),
            "Stable builds must not discover pre-release updates.");
        Assert.True(
            AppUpdateService.ShouldIncludePrereleases("1.2.0-beta.1"),
            "Beta builds must be able to discover the next beta update.");
        Assert.True(
            AppUpdateService.ShouldIncludePrereleases("2.0.0-rc.2+build.5"),
            "Release-candidate builds must remain on the pre-release channel.");
        return Task.CompletedTask;
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
        Assert.Equal(
            "scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=fast_bilinear," +
            "pad=1280:720:(ow-iw)/2:(oh-ih)/2:color=black,format=yuv420p",
            GetArgumentAfter(arguments, "-vf"),
            "Fixed-resolution GDI capture must use the low-impact scaling path.");
        Assert.True(
            arguments[^1].EndsWith("segment-%09d.mkv", StringComparison.Ordinal),
            "Capture output must be a numbered Matroska segment pattern.");

        var sourceArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration with
            {
                Resolution = ResolutionOption.All.Single(option => option.Id == "source")
            },
            [],
            @"C:\Buffer");
        Assert.Equal(
            "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p",
            GetArgumentAfter(sourceArguments, "-vf"),
            "Source/native GDI capture must remain a no-resize path.");

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
        Assert.ContainsSequence(nvenc, "-c:v", "h264_nvenc", "-preset", "p2");
        Assert.ContainsSequence(nvenc, "-multipass", "disabled", "-rc-lookahead", "0");
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

        var nativeFullHdConfiguration = configuration with
        {
            Display = new DisplayOption(
                @"\\.\DISPLAY1",
                "Display 1",
                0,
                0,
                1920,
                1080,
                true,
                0),
            Resolution = ResolutionOption.All.Single(option => option.Id == "1080p")
        };
        var nativeFullHdGraphics = FfmpegArgumentBuilder.BuildCaptureArguments(
            nativeFullHdConfiguration,
            [],
            new VideoEncodingStrategy(
                VideoEncoderKind.NvidiaNvenc,
                DesktopCaptureBackend.WindowsGraphicsCapture),
            @"C:\Buffer");
        Assert.True(
            nativeFullHdGraphics.Any(argument => argument.Contains(
                ":width=-2:height=-2:resize_mode=scale_aspect",
                StringComparison.Ordinal)),
            "A fixed preset that matches the native display must bypass WGC resizing.");
        Assert.True(
            graphicsNvenc.Any(argument => argument.Contains(
                ":width=1920:height=1080:resize_mode=scale_aspect",
                StringComparison.Ordinal)),
            "A smaller fixed preset must retain WGC aspect-preserving scaling.");

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

        var hybridRunner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            var isTransfer = arguments.Any(argument =>
                argument.Contains("hwdownload", StringComparison.Ordinal));

            return encoderName switch
            {
                "h264_nvenc" when !isGraphicsCapture => true,
                "h264_nvenc" => false,
                "h264_qsv" when !isGraphicsCapture => true,
                "h264_qsv" => !isTransfer,
                _ => false
            };
        });
        var hybridProbe = new FfmpegCapabilityProbe(hybridRunner);
        var hybridResult = await hybridProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            VideoEncoderKind.IntelQuickSync,
            hybridResult.Strategy.Encoder,
            "A verified direct-WGC encoder must outrank an earlier hardware GDI fallback.");
        Assert.Equal(
            DesktopCaptureBackend.WindowsGraphicsCapture,
            hybridResult.Strategy.CaptureBackend,
            "Hybrid systems must keep capture on the graphics path when another encoder supports it.");
        Assert.True(
            !hybridResult.Strategy.RequiresSystemMemoryTransfer,
            "The verified direct-WGC path should not add a compatibility transfer.");
        Assert.Equal(
            5,
            hybridRunner.CallCount,
            "The probe should test NVENC graphics paths before selecting verified direct QSV capture.");

        var hybridTransferRunner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            var isTransfer = arguments.Any(argument =>
                argument.Contains("hwdownload", StringComparison.Ordinal));

            return encoderName switch
            {
                "h264_nvenc" when !isGraphicsCapture => true,
                "h264_nvenc" => false,
                "h264_qsv" when !isGraphicsCapture => true,
                "h264_qsv" => isTransfer,
                _ => false
            };
        });
        var hybridTransferProbe = new FfmpegCapabilityProbe(hybridTransferRunner);
        var hybridTransferResult = await hybridTransferProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            VideoEncoderKind.IntelQuickSync,
            hybridTransferResult.Strategy.Encoder,
            "A verified transfer-WGC encoder must outrank an earlier hardware GDI fallback.");
        Assert.Equal(
            DesktopCaptureBackend.WindowsGraphicsCapture,
            hybridTransferResult.Strategy.CaptureBackend,
            "Hybrid systems must keep the compatibility transfer on the graphics capture path.");
        Assert.True(
            hybridTransferResult.Strategy.RequiresSystemMemoryTransfer,
            "The selected hybrid compatibility path should retain its required transfer.");
        Assert.Equal(
            6,
            hybridTransferRunner.CallCount,
            "The probe should test the later encoder's transfer path before accepting the earlier GDI fallback.");
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
        var manifestLines = ReplayBufferService.BuildConcatManifestLines(
        [
            @"C:\Buffer\segment-000000001.mkv",
            @"C:\Buffer\segment-000000002.mkv"
        ]);
        Assert.SequenceEqual(
        [
            "file 'C:/Buffer/segment-000000001.mkv'",
            "duration 2.000000",
            "file 'C:/Buffer/segment-000000002.mkv'",
            "duration 2.000000"
        ],
            manifestLines,
            "Concat manifests must advance by the known video cadence instead of AAC-long container durations.");

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

    private static Task TestTrimArgumentsAsync()
    {
        var inputPath = @"C:\Clips\Clip with spaces & quote's.mp4";
        var outputPath = @"C:\Clips\Clip with spaces & quote's_trimmed.mp4";
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("bg-BG");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("bg-BG");
            var arguments = FfmpegArgumentBuilder.BuildTrimArguments(
                inputPath,
                outputPath,
                TimeSpan.FromMilliseconds(1250),
                TimeSpan.FromMilliseconds(5375),
                includeAudio: true,
                framesPerSecond: 60,
                VideoEncodingStrategy.SoftwareGdi);

            Assert.ContainsSequence(arguments, "-nostdin", "-protocol_whitelist", "file", "-f", "mov");
            Assert.ContainsSequence(arguments, "-ss", "1.25", "-i", inputPath, "-t", "5.375");
            Assert.ContainsSequence(arguments, "-map", "0:v:0", "-map", "0:a:0?");
            Assert.ContainsSequence(arguments, "-c:v", "libx264");
            Assert.ContainsSequence(arguments, "-threads", "2");
            Assert.ContainsSequence(arguments, "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2");
            Assert.ContainsSequence(arguments, "-f", "mp4", "-n", outputPath);
            Assert.Equal(1, arguments.Count(argument => argument == inputPath),
                "A trim input path must remain one exact ArgumentList entry.");
            Assert.Equal(1, arguments.Count(argument => argument == outputPath),
                "A trim output path must remain one exact ArgumentList entry.");
            Assert.True(
                !arguments.Contains("copy", StringComparer.Ordinal),
                "Frame-accurate trim must re-encode rather than copy keyframe-bounded packets.");

            var silentArguments = FfmpegArgumentBuilder.BuildTrimArguments(
                inputPath,
                outputPath,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                includeAudio: false,
                framesPerSecond: 30,
                new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.Gdi));
            Assert.True(silentArguments.Contains("-an", StringComparer.Ordinal),
                "A silent source must explicitly disable audio output.");
            Assert.True(!silentArguments.Contains("0:a:0?", StringComparer.Ordinal),
                "A silent trim must not add an optional audio mapping.");
            Assert.ContainsSequence(silentArguments, "-c:v", "h264_nvenc");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => FfmpegArgumentBuilder.BuildTrimArguments(
                    inputPath,
                    outputPath,
                    TimeSpan.FromMilliseconds(-1),
                    TimeSpan.FromSeconds(1),
                    true,
                    60,
                    VideoEncodingStrategy.SoftwareGdi),
                "A negative trim start must be rejected.");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FfmpegArgumentBuilder.BuildTrimArguments(
                    inputPath,
                    outputPath,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    true,
                    60,
                    VideoEncodingStrategy.SoftwareGdi),
                "A zero trim duration must be rejected.");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FfmpegArgumentBuilder.BuildTrimArguments(
                    inputPath,
                    outputPath,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    true,
                    0,
                    VideoEncodingStrategy.SoftwareGdi),
                "An invalid trim frame rate must be rejected.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        return Task.CompletedTask;
    }

    private static Task TestTrimRangeAndNamingAsync()
    {
        Assert.True(
            ClipTrimService.TryNormalizeRange(
                TimeSpan.FromMilliseconds(1011),
                TimeSpan.FromMilliseconds(6234),
                TimeSpan.FromSeconds(10),
                60,
                out var range,
                out var rangeError),
            $"A valid non-keyframe-aligned trim range was rejected: {rangeError}");
        Assert.Equal(61L, range.StartFrame, "The trim start was not snapped to the nearest source frame.");
        Assert.Equal(374L, range.EndFrame, "The trim end was not snapped to the nearest source frame.");
        Assert.Equal(313L, range.EndFrame - range.StartFrame, "The normalized frame interval is incorrect.");
        Assert.True(
            Math.Abs(range.Start.TotalSeconds - 61d / 60) < 0.000001 &&
            Math.Abs(range.End.TotalSeconds - 374d / 60) < 0.000001,
            "Normalized timestamps do not match their source-frame indices.");

        Assert.True(
            ClipTrimService.TryNormalizeRange(
                TimeSpan.FromSeconds(-5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(10),
                60,
                out var clamped,
                out _),
            "A range overlapping the complete source should clamp safely.");
        Assert.Equal(0L, clamped.StartFrame, "A negative requested start did not clamp to frame zero.");
        Assert.Equal(600L, clamped.EndFrame, "An oversized requested end did not clamp to the source end.");

        Assert.True(
            ClipTrimService.TryNormalizeRange(
                TimeSpan.FromSeconds(9.999),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                60,
                out var lastFrame,
                out _),
            "The final source frame should remain trimmable.");
        Assert.Equal(599L, lastFrame.StartFrame, "The last-frame selection began at the wrong frame.");
        Assert.Equal(600L, lastFrame.EndFrame, "The last-frame selection ended at the wrong frame.");

        Assert.True(
            !ClipTrimService.TryNormalizeRange(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                60,
                out _,
                out _),
            "An empty trim range must be rejected.");
        Assert.True(
            !ClipTrimService.TryNormalizeRange(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                60,
                out _,
                out _),
            "A source with no duration must be rejected.");
        foreach (var invalidFrameRate in new[] { double.NaN, double.PositiveInfinity, 0, -1, 241 })
        {
            Assert.True(
                !ClipTrimService.TryNormalizeRange(
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(10),
                    invalidFrameRate,
                    out _,
                    out _),
                $"An invalid source frame rate was accepted: {invalidFrameRate}");
        }

        var timestamp = new DateTime(2026, 7, 13, 14, 30, 45, DateTimeKind.Local);
        Assert.Equal(
            "Clip_2026-07-13_14-30-45_trimmed.mp4",
            ClipTrimService.BuildTrimmedFileName(timestamp, 1),
            "The first trimmed output name is not stable.");
        Assert.Equal(
            "Clip_2026-07-13_14-30-45_trimmed_2.mp4",
            ClipTrimService.BuildTrimmedFileName(timestamp, 2),
            "Trimmed collision suffixes are not stable.");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClipTrimService.BuildTrimmedFileName(timestamp, 0),
            "A non-positive trimmed suffix must be rejected.");

        var testDirectory = CreateTestDirectory();
        try
        {
            Directory.CreateDirectory(testDirectory);
            var firstName = ClipTrimService.BuildTrimmedFileName(timestamp, 1);
            var existingPath = Path.Combine(testDirectory, firstName);
            File.WriteAllBytes(existingPath, [9, 9, 9]);
            var stagingPath = Path.Combine(
                testDirectory,
                $".clipforge-trim-{Guid.NewGuid():N}.partial.mp4");
            File.WriteAllBytes(stagingPath, [1, 2, 3, 4]);

            var committedPath = ClipTrimService.CommitStagingFile(
                testDirectory,
                stagingPath,
                timestamp);
            Assert.Equal(
                Path.Combine(testDirectory, ClipTrimService.BuildTrimmedFileName(timestamp, 2)),
                committedPath,
                "A collision did not reserve the next non-overwriting trimmed name.");
            Assert.SequenceEqual(new byte[] { 9, 9, 9 }, File.ReadAllBytes(existingPath),
                "Committing a trim overwrote an existing output.");
            Assert.True(committedPath is not null && File.Exists(committedPath),
                "The unique trimmed output was not committed.");
            Assert.SequenceEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(committedPath!),
                "The committed output does not contain the staged media.");
            Assert.True(!File.Exists(stagingPath), "Atomic trim commit left the staging path behind.");
            Assert.True(
                ClipLibraryService.TryClassifyClipFileName(
                    Path.GetFileName(committedPath),
                    out var committedKind) && committedKind == ClipKind.Trimmed,
                "A committed trim name is not discoverable as Trimmed.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }

        return Task.CompletedTask;
    }

    private static Task TestConfiguredFfmpegDiscoveryAsync()
    {
        var testDirectory = CreateTestDirectory();
        var previous = Environment.GetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH");
        var previousDeveloperMode = Environment.GetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE");

        try
        {
            Directory.CreateDirectory(testDirectory);
            var executable = Path.Combine(testDirectory, "ffmpeg.exe");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            Environment.SetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH", executable);
            Environment.SetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE", "1");

            var service = new FfmpegSetupService(Path.Combine(testDirectory, "private"));
            Assert.Equal(
                Path.GetFullPath(executable),
                service.FindExecutable(),
                "The explicitly configured FFmpeg path should take priority.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH", previous);
            Environment.SetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE", previousDeveloperMode);
            DeleteTestDirectory(testDirectory);
        }

        return Task.CompletedTask;
    }

    private static Task TestPinnedFfmpegTrustPolicyAsync()
    {
        var testDirectory = CreateTestDirectory();
        var previousPath = Environment.GetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH");
        var previousDeveloperMode = Environment.GetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE");

        try
        {
            Directory.CreateDirectory(testDirectory);
            var fakePrivateTool = Path.Combine(testDirectory, "ffmpeg.exe");
            File.WriteAllBytes(fakePrivateTool, [0x4D, 0x5A, 1, 2, 3]);
            Environment.SetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH", fakePrivateTool);
            Environment.SetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE", null);

            var service = new FfmpegSetupService(testDirectory);
            Assert.Equal<string?>(
                null,
                service.FindExecutable(),
                "Production discovery must reject an unverified private or environment-provided FFmpeg binary.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH", previousPath);
            Environment.SetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE", previousDeveloperMode);
            DeleteTestDirectory(testDirectory);
        }

        return Task.CompletedTask;
    }

    private static async Task TestFfmpegDownloadLimitsAsync()
    {
        await using (var source = new MemoryStream([1, 2, 3, 4]))
        await using (var destination = new MemoryStream())
        {
            await FfmpegSetupService.CopyWithProgressAsync(
                    source,
                    destination,
                    totalBytes: 4,
                    maximumBytes: 4,
                    progress: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.Equal(4L, destination.Length, "A download at the exact byte limit should succeed.");
        }

        var oversizedRejected = false;
        try
        {
            await using var source = new MemoryStream([1, 2, 3, 4, 5]);
            await using var destination = new MemoryStream();
            await FfmpegSetupService.CopyWithProgressAsync(
                    source,
                    destination,
                    totalBytes: 4,
                    maximumBytes: 4,
                    progress: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            oversizedRejected = true;
        }

        Assert.True(oversizedRejected, "A chunked response that exceeds its hard byte cap must be rejected.");

        var missingLengthRejected = false;
        try
        {
            await using var source = new MemoryStream([1]);
            await using var destination = new MemoryStream();
            await FfmpegSetupService.CopyWithProgressAsync(
                    source,
                    destination,
                    totalBytes: null,
                    maximumBytes: 4,
                    progress: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            missingLengthRejected = true;
        }

        Assert.True(missingLengthRejected, "A download without a declared size must be rejected.");
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
                StartReplayWithWindows = true,
                CheckForUpdatesAutomatically = false,
                PlayClipSavedSound = false,
                BackgroundColor = "#161321",
                AccentColor = "#3B82F6",
                SurfaceColor = "#17131F",
                RecentClipCount = 10,
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
                expected.StartReplayWithWindows,
                actual.StartReplayWithWindows,
                "Windows autostart replay preference did not roundtrip.");
            Assert.Equal(
                expected.CheckForUpdatesAutomatically,
                actual.CheckForUpdatesAutomatically,
                "Automatic update preference did not roundtrip.");
            Assert.Equal(
                expected.PlayClipSavedSound,
                actual.PlayClipSavedSound,
                "Saved-clip sound preference did not roundtrip.");
            Assert.Equal(
                expected.BackgroundColor,
                actual.BackgroundColor,
                "Background color did not roundtrip.");
            Assert.Equal(expected.AccentColor, actual.AccentColor, "Accent color did not roundtrip.");
            Assert.Equal(expected.SurfaceColor, actual.SurfaceColor, "Surface color did not roundtrip.");
            Assert.Equal(
                expected.RecentClipCount,
                actual.RecentClipCount,
                "Recent clip count did not roundtrip.");
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

            var oldest = Path.Combine(clipsDirectory, "Clip_2026-01-01_01-00-00.MP4");
            var newest = Path.Combine(clipsDirectory, "Clip_2026-01-02_01-00-00_2.mp4");
            var corrupt = Path.Combine(clipsDirectory, "Clip_2026-01-03_01-00-00.mp4");
            var empty = Path.Combine(clipsDirectory, "Clip_2026-01-04_01-00-00.mp4");
            await File.WriteAllBytesAsync(oldest, [1, 2, 3]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(newest, [4, 5, 6]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(corrupt, [7, 8, 9]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(empty, []).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(clipsDirectory, "not-a-clip.txt"), "ignored")
                .ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(clipsDirectory, "Nested"));
            await File.WriteAllBytesAsync(
                    Path.Combine(clipsDirectory, "Nested", "Clip_2026-01-05_01-00-00.mp4"),
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
            Assert.True(
                ClipLibraryService.IsCurrentClipSafe(clipsDirectory, clips[0]),
                "A freshly discovered unchanged recording should pass the pre-playback identity check.");
            await File.AppendAllTextAsync(clips[0].FullPath, "changed after discovery").ConfigureAwait(false);
            Assert.True(
                !ClipLibraryService.IsCurrentClipSafe(clipsDirectory, clips[0]),
                "A recording changed after discovery must be rejected before in-process playback.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestClipClassificationAndFilteringAsync()
    {
        (string FileName, ClipKind Kind)[] validNames =
        [
            ("Clip_2026-07-13_12-34-56.mp4", ClipKind.Original),
            ("Clip_2026-07-13_12-34-56_2.mp4", ClipKind.Original),
            ("Clip_2026-07-13_12-34-56_trimmed.mp4", ClipKind.Trimmed),
            ("Clip_2026-07-13_12-34-56_trimmed_2.mp4", ClipKind.Trimmed),
            ("Clip_2026-07-13_12-34-56_2_trimmed.mp4", ClipKind.Trimmed),
            ("Clip_2026-07-13_12-34-56_2_trimmed_3.MP4", ClipKind.Trimmed)
        ];
        foreach (var (fileName, expectedKind) in validNames)
        {
            Assert.True(
                ClipLibraryService.TryClassifyClipFileName(fileName, out var actualKind),
                $"A generated clip name was rejected: {fileName}");
            Assert.Equal(expectedKind, actualKind, $"Clip kind was misclassified for {fileName}");
        }

        string?[] invalidNames =
        [
            null,
            string.Empty,
            " Clip_2026-07-13_12-34-56.mp4",
            "Nested\\Clip_2026-07-13_12-34-56.mp4",
            "Clip_2026-02-30_12-34-56.mp4",
            "Clip_0000-07-13_12-34-56.mp4",
            "Clip_٢٠٢٦-07-13_12-34-56.mp4",
            "Clip_2026-07-13_12-34-56_0.mp4",
            "Clip_2026-07-13_12-34-56_01.mp4",
            "Clip_2026-07-13_12-34-56_trimmed_.mp4",
            "Clip_2026-07-13_12-34-56_trimmed_0.mp4",
            "Clip_2026-07-13_12-34-56_trimmed_01.mp4",
            "Clip_2026-07-13_12-34-56_trimmed_2_3.mp4",
            "Clip_2026-07-13_12-34-56_trimmed.partial.mp4",
            "Clip_2026-07-13_12-34-56_trimmed.mp4.exe",
            "Clip_2026-07-13_12-34-56_edited.mp4",
            ".Clip_2026-07-13_12-34-56_trimmed.partial.mp4"
        ];
        foreach (var fileName in invalidNames)
        {
            Assert.True(
                !ClipLibraryService.TryClassifyClipFileName(fileName, out _),
                $"An unowned or partial file name was accepted: {fileName ?? "<null>"}");
        }

        var testDirectory = CreateTestDirectory();
        var clipsDirectory = Path.Combine(testDirectory, "Clips");
        var toolsDirectory = Path.Combine(testDirectory, "Tools");
        try
        {
            Directory.CreateDirectory(clipsDirectory);
            Directory.CreateDirectory(toolsDirectory);
            var ffmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            var ffprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            await File.WriteAllBytesAsync(ffmpegPath, [0x4D, 0x5A]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ffprobePath, [0x4D, 0x5A]).ConfigureAwait(false);

            var trimmedPath = Path.Combine(clipsDirectory, "Clip_2026-07-12_12-00-00_trimmed.mp4");
            await File.WriteAllBytesAsync(trimmedPath, [1, 2, 3]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(trimmedPath, new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc));
            for (var index = 0; index < 25; index++)
            {
                var originalPath = Path.Combine(
                    clipsDirectory,
                    $"Clip_2026-07-13_12-00-{index:00}.mp4");
                await File.WriteAllBytesAsync(originalPath, [(byte)(index + 1)]).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(
                    originalPath,
                    new DateTime(2026, 7, 13, 12, index, 0, DateTimeKind.Utc));
            }

            var runner = new FakeClipMediaProcessRunner();
            var service = new ClipLibraryService(
                () => ffmpegPath,
                () => ffprobePath,
                runner,
                Path.Combine(testDirectory, "Cache"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            var trimmed = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 1,
                    includeThumbnails: false,
                    filter: ClipLibraryFilter.Trimmed)
                .ConfigureAwait(false);
            Assert.Equal(1, trimmed.Count,
                "A trimmed clip older than more than the probe budget of originals was starved.");
            Assert.Equal(ClipKind.Trimmed, trimmed[0].Kind,
                "The Trimmed filter returned a normal clip.");
            Assert.Equal(Path.GetFileName(trimmedPath), trimmed[0].FileName,
                "The filtered Library returned the wrong trimmed clip.");
            Assert.Equal(1, runner.Invocations.Count,
                "Filtering must happen before probes, not after probing newer originals.");
            Assert.Equal(trimmedPath, runner.Invocations[0].Arguments[^1],
                "The Trimmed filter probed an item from another category.");

            var originals = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 2,
                    includeThumbnails: false,
                    filter: ClipLibraryFilter.Original)
                .ConfigureAwait(false);
            Assert.Equal(2, originals.Count, "The Original filter returned the wrong count.");
            Assert.True(originals.All(clip => clip.Kind == ClipKind.Original),
                "The Original filter included a trimmed clip.");
            Assert.True(originals[0].RecordedAtUtc >= originals[1].RecordedAtUtc,
                "Filtered results are not ordered newest first.");

            var all = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 30,
                    includeThumbnails: false,
                    filter: ClipLibraryFilter.All)
                .ConfigureAwait(false);
            Assert.Equal(26, all.Count, "The All filter did not return both clip kinds.");
            Assert.True(all.Any(clip => clip.Kind == ClipKind.Trimmed),
                "The All filter omitted trimmed clips.");
            Assert.True(all.Any(clip => clip.Kind == ClipKind.Original),
                "The All filter omitted normal clips.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestClipTrimServiceAsync()
    {
        var testDirectory = CreateTestDirectory();
        var clipsDirectory = Path.Combine(testDirectory, "Clips");
        var toolsDirectory = Path.Combine(testDirectory, "Tools");
        try
        {
            Directory.CreateDirectory(clipsDirectory);
            Directory.CreateDirectory(toolsDirectory);
            var ffmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            var ffprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            await File.WriteAllBytesAsync(ffmpegPath, [0x4D, 0x5A]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ffprobePath, [0x4D, 0x5A]).ConfigureAwait(false);

            var sourcePath = Path.Combine(clipsDirectory, "Clip_2026-07-13_15-00-00.mp4");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5, 6, 7, 8]).ConfigureAwait(false);
            var sourceInfo = new FileInfo(sourcePath);
            Assert.True(
                ClipLibraryService.TryGetCurrentFileIdentity(sourcePath, out var sourceIdentity),
                "The trim source did not receive a stable Windows identity.");
            var source = new ClipLibraryItem(
                sourceInfo.Name,
                sourceInfo.FullName,
                new DateTimeOffset(DateTime.SpecifyKind(sourceInfo.LastWriteTimeUtc, DateTimeKind.Utc)),
                sourceInfo.Length,
                TimeSpan.FromSeconds(10))
            {
                FileIdentity = sourceIdentity,
                Kind = ClipKind.Original
            };

            var successRunner = new FakeTrimMediaProcessRunner();
            var successService = new ClipTrimService(() => ffmpegPath, () => ffprobePath, successRunner);
            var success = await successService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromMilliseconds(1011),
                    TimeSpan.FromMilliseconds(6234))
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.Succeeded, success.Status,
                $"A valid transactional trim failed: {success.Message}");
            Assert.True(success.Succeeded && success.OutputPath is not null && File.Exists(success.OutputPath),
                "A successful trim did not return an existing output.");
            Assert.True(File.Exists(sourcePath), "A successful trim modified or deleted its source.");
            Assert.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, File.ReadAllBytes(sourcePath),
                "A successful trim changed the source bytes.");
            Assert.True(
                ClipLibraryService.TryClassifyClipFileName(
                    Path.GetFileName(success.OutputPath),
                    out var successfulKind) && successfulKind == ClipKind.Trimmed,
                "The successful trim did not use a strict Trimmed filename.");
            Assert.Equal(1, successRunner.TrimRunCount,
                "A software trim should launch exactly one export after hardware probes fail.");
            Assert.True(!EnumerateTrimPartials(clipsDirectory).Any(),
                "A successful trim left a partial output behind.");

            var variableAverageRunner = new FakeTrimMediaProcessRunner
            {
                SourceAverageFrameRate = "355/6",
                OutputAverageFrameRate = "294/5"
            };
            var variableAverageService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                variableAverageRunner);
            var variableAverage = await variableAverageService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.Succeeded, variableAverage.Status,
                "A valid nominal-60 FPS trim was rejected because its selection-local average FPS changed.");

            var wrongNominalRunner = new FakeTrimMediaProcessRunner
            {
                SourceAverageFrameRate = "355/6",
                OutputAverageFrameRate = "294/5",
                OutputNominalFrameRate = "30/1"
            };
            var wrongNominalService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                wrongNominalRunner);
            var wrongNominal = await wrongNominalService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.OutputValidationFailed, wrongNominal.Status,
                "A genuinely different nominal output frame rate passed trim validation.");

            var trimRunsBeforeInvalidRange = successRunner.TrimRunCount;
            var invalidRange = await successService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.InvalidRange, invalidRange.Status,
                "An empty selection did not return InvalidRange.");
            Assert.True(!invalidRange.Succeeded && invalidRange.OutputPath is null,
                "An invalid range returned a successful output.");
            Assert.Equal(trimRunsBeforeInvalidRange, successRunner.TrimRunCount,
                "An invalid range launched the trim encoder.");

            var failedRunner = new FakeTrimMediaProcessRunner { FailTrim = true };
            var failedService = new ClipTrimService(() => ffmpegPath, () => ffprobePath, failedRunner);
            var failed = await failedService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.EncodingFailed, failed.Status,
                "A non-zero FFmpeg result did not return EncodingFailed.");
            Assert.True(File.Exists(sourcePath), "An encoding failure removed the original.");
            Assert.True(!EnumerateTrimPartials(clipsDirectory).Any(),
                "An encoding failure left its owned partial behind.");

            var invalidOutputRunner = new FakeTrimMediaProcessRunner { ReturnInvalidOutputMetadata = true };
            var invalidOutputService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                invalidOutputRunner);
            var invalidOutput = await invalidOutputService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4))
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.OutputValidationFailed, invalidOutput.Status,
                "A wrong-duration output passed post-encode validation.");
            Assert.True(File.Exists(sourcePath), "Output validation failure removed the original.");
            Assert.True(!EnumerateTrimPartials(clipsDirectory).Any(),
                "Output validation failure left its owned partial behind.");

            var cancellationRunner = new FakeTrimMediaProcessRunner { WaitForTrimCancellation = true };
            var cancellationService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                cancellationRunner);
            using (var cancellation = new CancellationTokenSource())
            {
                var trimTask = cancellationService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(4),
                    cancellation.Token);
                await cancellationRunner.TrimStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                cancellation.Cancel();
                var cancelled = await trimTask.ConfigureAwait(false);
                Assert.Equal(ClipTrimStatus.Cancelled, cancelled.Status,
                    "A cancelled export did not return Cancelled.");
                Assert.True(!cancelled.Succeeded && cancelled.OutputPath is null,
                    "A cancelled export returned an output path.");
            }

            Assert.True(File.Exists(sourcePath), "Cancellation removed the original.");
            Assert.True(!EnumerateTrimPartials(clipsDirectory).Any(),
                "Cancellation left its owned partial behind.");

            var preservedTimestamp = sourceInfo.LastWriteTimeUtc;
            File.Delete(sourcePath);
            await File.WriteAllBytesAsync(sourcePath, [8, 7, 6, 5, 4, 3, 2, 1]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(sourcePath, preservedTimestamp);
            Assert.True(
                ClipLibraryService.TryGetCurrentFileIdentity(sourcePath, out var replacementIdentity) &&
                replacementIdentity != sourceIdentity,
                "The same-size replacement did not receive a new file identity.");
            var staleRunner = new FakeTrimMediaProcessRunner();
            var staleService = new ClipTrimService(() => ffmpegPath, () => ffprobePath, staleRunner);
            var stale = await staleService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.SourceChangedOrUnsafe, stale.Status,
                "A same-size, same-time source replacement was not rejected.");
            Assert.Equal(0, staleRunner.Invocations.Count,
                "A stale source launched a media helper before identity rejection.");
            Assert.True(File.Exists(sourcePath), "The replacement source was deleted after rejection.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static IEnumerable<string> EnumerateTrimPartials(string clipsDirectory) =>
        Directory.Exists(clipsDirectory)
            ? Directory.EnumerateFiles(
                clipsDirectory,
                ".clipforge-trim-*.partial.mp4",
                SearchOption.TopDirectoryOnly)
            : [];

    private static Task TestClipLibrarySecurityPolicyAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClipForge-Security-Root"));
        var valid = Path.Combine(root, "Clip_2026-07-12_18-30-00.mp4");
        var nested = Path.Combine(root, "Nested", "Clip_2026-07-12_18-30-00.mp4");
        var traversal = Path.Combine(root, "..", "Clip_2026-07-12_18-30-00.mp4");

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
        Assert.True(
            !ClipLibraryService.IsSafeTopLevelClipPath(
                root,
                Path.Combine(root, "downloaded-video.mp4"),
                FileAttributes.Normal),
            "The in-app gallery must not auto-decode unrelated MP4 files from the save folder.");

        var probeArguments = ClipLibraryService.BuildProbeArguments(valid);
        Assert.ContainsSequence(probeArguments, "-protocol_whitelist", "file", "-f", "mov");

        string[] arguments = ["-i", valid, "argument & not-a-command"];
        var startInfo = ClipMediaProcessRunner.CreateStartInfo(@"C:\Tools\ffmpeg.exe", arguments);
        Assert.True(!startInfo.UseShellExecute, "Media tools must never use shell execution.");
        Assert.True(startInfo.CreateNoWindow, "Media tools should not create a console window.");
        Assert.SequenceEqual(arguments, startInfo.ArgumentList, "Media arguments must use ArgumentList unchanged.");

        var thumbnailArguments = ClipLibraryService.BuildThumbnailArguments(
            valid,
            Path.Combine(root, "thumbnail.jpg"),
            TimeSpan.FromSeconds(20));
        Assert.ContainsSequence(
            thumbnailArguments,
            "-nostdin", "-y", "-threads", "1", "-protocol_whitelist", "file", "-f", "mov");
        Assert.ContainsSequence(thumbnailArguments, "-i", valid, "-map", "0:v:0");
        Assert.True(
            !ClipLibraryService.HasUsableFileId(new ClipFileIdentity(42, 0, 0, 1)),
            "An all-zero filesystem file ID must fail closed.");
        Assert.True(
            ClipLibraryService.HasUsableFileId(new ClipFileIdentity(42, 1, 0, 1)),
            "A non-zero filesystem file ID should be accepted.");
        return Task.CompletedTask;
    }

    private static async Task TestClipDeletionAsync()
    {
        var testDirectory = CreateTestDirectory();
        var clipsDirectory = Path.Combine(testDirectory, "Clips");
        var cacheDirectory = Path.Combine(testDirectory, "Cache");
        string? linkedCacheDirectory = null;

        try
        {
            Directory.CreateDirectory(clipsDirectory);
            Directory.CreateDirectory(cacheDirectory);
            var ffmpegPath = Path.Combine(testDirectory, "ffmpeg.exe");
            await File.WriteAllBytesAsync(ffmpegPath, [0x4D, 0x5A]).ConfigureAwait(false);
            var runner = new FakeClipMediaProcessRunner();
            var service = new ClipLibraryService(
                () => ffmpegPath,
                () => null,
                runner,
                cacheDirectory,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
            var clipPath = Path.Combine(clipsDirectory, "Clip_2026-07-12_20-00-00.mp4");
            await File.WriteAllBytesAsync(clipPath, [1, 2, 3, 4, 5]).ConfigureAwait(false);
            var info = new FileInfo(clipPath);
            Assert.True(
                ClipLibraryService.TryGetCurrentFileIdentity(clipPath, out var identity),
                "A discovered clip should receive a stable Windows file identity.");
            var clip = new ClipLibraryItem(
                info.Name,
                info.FullName,
                new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
                info.Length,
                TimeSpan.FromSeconds(1))
            {
                FileIdentity = identity
            };
            var thumbnailPath = service.GetDeterministicThumbnailPath(clip);
            var legacyThumbnailPath = service.GetLegacyThumbnailPath(clip);
            var legacyKeyMaterial = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{clip.FullPath.ToUpperInvariant()}\n{clip.FileSizeBytes}\n{clip.RecordedAtUtc.UtcDateTime.Ticks}");
            var expectedLegacyName = $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(legacyKeyMaterial)))}.jpg";
            Assert.Equal(
                expectedLegacyName,
                Path.GetFileName(legacyThumbnailPath),
                "The legacy key must remain byte-compatible with ClipForge v1.2 path/size/mtime hashing.");
            await File.WriteAllBytesAsync(thumbnailPath, [0xFF, 0xD8, 0xFF, 0xFF, 0xD9]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(legacyThumbnailPath, [0xFF, 0xD8, 0xFF, 0xFF, 0xD9]).ConfigureAwait(false);
            clip = clip with { ThumbnailPath = thumbnailPath };

            Assert.Equal(
                thumbnailPath,
                await service.GetThumbnailAsync(clipsDirectory, clip).ConfigureAwait(false),
                "The identity-bound thumbnail should be reused without invoking FFmpeg.");
            Assert.True(
                !File.Exists(legacyThumbnailPath),
                "Loading the current thumbnail should prune exactly its legacy v1.2 cache key.");
            Assert.Equal(0, runner.ThumbnailRunCount, "A current cache hit must not invoke FFmpeg.");
            await File.WriteAllBytesAsync(
                    legacyThumbnailPath,
                    [0xFF, 0xD8, 0xFF, 0xFF, 0xD9])
                .ConfigureAwait(false);

            Assert.True(
                ClipLibraryService.TryGetCurrentClipPath(clipsDirectory, clip, out var validatedPath),
                "An unchanged ClipForge-owned gallery item should revalidate before an action.");
            Assert.Equal(info.FullName, validatedPath, "The revalidated clip path should stay normalized.");
            Assert.Equal(
                ClipDeletionResult.Deleted,
                ClipLibraryService.DeleteCurrentClip(clipsDirectory, clip),
                "The exact revalidated clip should be deleted by handle.");
            Assert.True(!File.Exists(clipPath), "The deleted clip should no longer exist.");
            service.RemoveCachedThumbnail(clip);
            Assert.True(
                !File.Exists(thumbnailPath),
                "Permanently deleting a clip should remove its cached visual thumbnail.");
            Assert.True(
                !File.Exists(legacyThumbnailPath),
                "Permanently deleting a clip should also remove its legacy v1.2 thumbnail.");

            await File.WriteAllBytesAsync(clipPath, [6, 7, 8, 9, 10]).ConfigureAwait(false);
            info.Refresh();
            Assert.True(
                ClipLibraryService.TryGetCurrentFileIdentity(clipPath, out var staleIdentity),
                "The replacement test clip should receive a Windows file identity.");
            var staleClip = new ClipLibraryItem(
                info.Name,
                info.FullName,
                new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
                info.Length,
                TimeSpan.FromSeconds(1))
            {
                FileIdentity = staleIdentity
            };
            var staleThumbnailPath = service.GetDeterministicThumbnailPath(staleClip);
            await File.WriteAllBytesAsync(
                    staleThumbnailPath,
                    [0xFF, 0xD8, 0xFF, 0xFF, 0xD9])
                .ConfigureAwait(false);
            var preservedTimestamp = info.LastWriteTimeUtc;
            File.Delete(clipPath);
            await File.WriteAllBytesAsync(clipPath, [11, 12, 13, 14, 15]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(clipPath, preservedTimestamp);
            Assert.True(
                ClipLibraryService.TryGetCurrentFileIdentity(clipPath, out var replacementIdentity) &&
                replacementIdentity != staleIdentity,
                "A same-size, same-time replacement must still have a different file identity.");
            var replacementClip = staleClip with { FileIdentity = replacementIdentity };
            Assert.True(
                !service.GetDeterministicThumbnailPath(replacementClip)
                    .Equals(staleThumbnailPath, StringComparison.OrdinalIgnoreCase),
                "Thumbnail cache keys must include the stable file identity.");
            Assert.Equal(
                null,
                await service.GetThumbnailAsync(clipsDirectory, staleClip).ConfigureAwait(false),
                "A stale item must not reuse a cached thumbnail after same-metadata replacement.");
            Assert.Equal(
                0,
                runner.ThumbnailRunCount,
                "A stale file identity must be rejected before launching the thumbnail helper.");

            Assert.Equal(
                ClipDeletionResult.ChangedOrUnsafe,
                ClipLibraryService.DeleteCurrentClip(clipsDirectory, staleClip),
                "A same-metadata replacement must not be deleted.");
            Assert.True(File.Exists(clipPath), "The replacement clip must remain on disk after deletion is rejected.");
            service.RemoveCachedThumbnail(staleClip);
            Assert.True(
                !File.Exists(staleThumbnailPath),
                "Identity-bound stale thumbnails should still be removable by their trusted cache key.");

            var writeBlocked = false;
            var clipRenameBlocked = false;
            var rootRenameBlocked = false;
            var cacheRootRenameBlocked = false;
            var movedClipPath = Path.Combine(clipsDirectory, "Clip_2026-07-12_20-00-00_moved.mp4");
            var movedRootPath = Path.Combine(testDirectory, "Clips-Moved");
            var pinnedCacheDirectory = Path.Combine(testDirectory, "PinnedCache");
            var movedCachePath = Path.Combine(testDirectory, "PinnedCache-Moved");
            var pinnedRunner = new FakeClipMediaProcessRunner
            {
                BeforeThumbnailWrite = _ =>
                {
                    try
                    {
                        using var writer = new FileStream(
                            clipPath,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (IOException)
                    {
                        writeBlocked = true;
                    }

                    try
                    {
                        File.Move(clipPath, movedClipPath);
                        File.Move(movedClipPath, clipPath);
                    }
                    catch (IOException)
                    {
                        clipRenameBlocked = true;
                    }

                    try
                    {
                        Directory.Move(clipsDirectory, movedRootPath);
                        Directory.Move(movedRootPath, clipsDirectory);
                    }
                    catch (IOException)
                    {
                        rootRenameBlocked = true;
                    }

                    try
                    {
                        Directory.Move(pinnedCacheDirectory, movedCachePath);
                        Directory.Move(movedCachePath, pinnedCacheDirectory);
                    }
                    catch (IOException)
                    {
                        cacheRootRenameBlocked = true;
                    }
                }
            };
            var pinnedService = new ClipLibraryService(
                () => ffmpegPath,
                () => null,
                pinnedRunner,
                pinnedCacheDirectory,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
            var multiLinkReadClip = replacementClip with
            {
                FileIdentity = replacementIdentity with { NumberOfLinks = 2 }
            };
            Directory.CreateDirectory(pinnedCacheDirectory);
            var generatedLegacyPath = pinnedService.GetLegacyThumbnailPath(multiLinkReadClip);
            await File.WriteAllBytesAsync(
                    generatedLegacyPath,
                    [0xFF, 0xD8, 0xFF, 0xFF, 0xD9])
                .ConfigureAwait(false);
            var pinnedThumbnail = await pinnedService.GetThumbnailAsync(
                    clipsDirectory,
                    multiLinkReadClip)
                .ConfigureAwait(false);
            Assert.True(
                pinnedThumbnail is not null && File.Exists(pinnedThumbnail),
                "Thumbnail reads should permit a stable identity with multiple links.");
            Assert.True(
                !File.Exists(generatedLegacyPath),
                "Generating a current thumbnail should prune its single legacy v1.2 cache key.");
            Assert.True(writeBlocked, "The pinned clip handle must block writes while FFmpeg reads by pathname.");
            Assert.True(clipRenameBlocked, "The pinned clip handle must block rename/delete access during FFmpeg.");
            Assert.True(rootRenameBlocked, "The pinned root handle must block replacement during FFmpeg.");
            Assert.True(cacheRootRenameBlocked, "The pinned cache root must remain stable through helper and commit.");
            using (new FileStream(
                       clipPath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                // Opening succeeds only after the pinned read handle is disposed.
            }

            File.Move(clipPath, movedClipPath);
            File.Move(movedClipPath, clipPath);
            Directory.Move(clipsDirectory, movedRootPath);
            Directory.Move(movedRootPath, clipsDirectory);
            Directory.Move(pinnedCacheDirectory, movedCachePath);
            Directory.Move(movedCachePath, pinnedCacheDirectory);
            Assert.Equal(
                ClipDeletionResult.ChangedOrUnsafe,
                ClipLibraryService.DeleteCurrentClip(clipsDirectory, multiLinkReadClip),
                "Permanent deletion must retain the single-link requirement.");

            var linkedCacheTarget = Path.Combine(testDirectory, "LinkedCacheTarget");
            Directory.CreateDirectory(linkedCacheTarget);
            linkedCacheDirectory = Path.Combine(testDirectory, "LinkedCache");
            try
            {
                Directory.CreateSymbolicLink(linkedCacheDirectory, linkedCacheTarget);
                var linkedService = new ClipLibraryService(
                    () => ffmpegPath,
                    () => null,
                    runner,
                    linkedCacheDirectory,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1));
                var linkedThumbnailPath = linkedService.GetDeterministicThumbnailPath(replacementClip);
                var linkedTargetPath = Path.Combine(linkedCacheTarget, Path.GetFileName(linkedThumbnailPath));
                await File.WriteAllBytesAsync(
                        linkedTargetPath,
                        [0xFF, 0xD8, 0xFF, 0xFF, 0xD9])
                    .ConfigureAwait(false);

                linkedService.RemoveCachedThumbnail(replacementClip);
                Assert.True(
                    File.Exists(linkedTargetPath),
                    "Thumbnail cleanup must not traverse a reparse-point cache root.");
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Windows developer mode or symbolic-link privilege may be unavailable on CI.
            }
        }
        finally
        {
            if (linkedCacheDirectory is not null && Directory.Exists(linkedCacheDirectory))
            {
                Directory.Delete(linkedCacheDirectory);
            }

            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestThumbnailDecoderReleasesFileAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            await RunOnStaThreadAsync(() =>
            {
                Directory.CreateDirectory(testDirectory);
                var thumbnailPath = Path.Combine(testDirectory, "thumbnail.jpg");
                var source = BitmapSource.Create(
                    2,
                    2,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    new byte[16],
                    8);
                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = new FileStream(thumbnailPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(stream);
                }

                var converter = new ThumbnailPathConverter();
                var converted = converter.Convert(
                    thumbnailPath,
                    typeof(BitmapSource),
                    null,
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(
                    converted is BitmapSource { IsFrozen: true },
                    "Gallery thumbnails should be fully decoded and frozen in memory.");
                var convertedAgain = converter.Convert(
                    thumbnailPath,
                    typeof(BitmapSource),
                    null,
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(
                    ReferenceEquals(converted, convertedAgain),
                    "An unchanged visible thumbnail should reuse its frozen decode across gallery refreshes.");

                File.Delete(thumbnailPath);
                Assert.True(
                    !File.Exists(thumbnailPath),
                    "An in-memory gallery thumbnail must not keep its cache JPEG locked.");
                Assert.True(
                    converter.Convert(
                        thumbnailPath,
                        typeof(BitmapSource),
                        null,
                        System.Globalization.CultureInfo.InvariantCulture) is null,
                    "A deleted cache JPEG must not be resurrected by the in-memory decode cache.");
            }).ConfigureAwait(false);
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "ClipForge thumbnail test"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task TestClipLibraryProbeBudgetAsync()
    {
        var testDirectory = CreateTestDirectory();
        var clipsDirectory = Path.Combine(testDirectory, "Clips");
        var toolsDirectory = Path.Combine(testDirectory, "Tools");
        try
        {
            Directory.CreateDirectory(clipsDirectory);
            Directory.CreateDirectory(toolsDirectory);
            var ffprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            await File.WriteAllBytesAsync(ffprobePath, [0x4D, 0x5A]).ConfigureAwait(false);
            for (var index = 0; index < 40; index++)
            {
                await File.WriteAllBytesAsync(
                        Path.Combine(clipsDirectory, $"Clip_2026-07-12_18-30-{index:00}.mp4"),
                        [1, 2, 3])
                    .ConfigureAwait(false);
            }

            var runner = new FakeClipMediaProcessRunner { RejectAllProbes = true };
            var service = new ClipLibraryService(
                () => null,
                () => ffprobePath,
                runner,
                Path.Combine(testDirectory, "Cache"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            var clips = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 5,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(0, clips.Count, "Invalid media must not be returned by the gallery.");
            Assert.Equal(
                20,
                runner.Invocations.Count,
                "A folder full of invalid recordings must not launch an unbounded number of probe processes.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestClipLibraryProbeCacheAsync()
    {
        var testDirectory = CreateTestDirectory();
        var clipsDirectory = Path.Combine(testDirectory, "Clips");
        var toolsDirectory = Path.Combine(testDirectory, "Tools");
        try
        {
            Directory.CreateDirectory(clipsDirectory);
            Directory.CreateDirectory(toolsDirectory);
            var ffprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            await File.WriteAllBytesAsync(ffprobePath, [0x4D, 0x5A]).ConfigureAwait(false);
            var newestClip = Path.Combine(clipsDirectory, "Clip_2026-07-13_12-00-01.mp4");
            var olderClip = Path.Combine(clipsDirectory, "Clip_2026-07-13_12-00-00.mp4");
            await File.WriteAllBytesAsync(newestClip, [1, 2, 3]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(olderClip, [4, 5, 6]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(newestClip, new DateTime(2026, 7, 13, 12, 0, 1, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(olderClip, new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc));

            var runner = new FakeClipMediaProcessRunner
            {
                ProbeDelay = TimeSpan.FromMilliseconds(50)
            };
            var service = new ClipLibraryService(
                () => null,
                () => ffprobePath,
                runner,
                Path.Combine(testDirectory, "Cache"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            var simultaneousLoads = await Task.WhenAll(
                    service.GetRecentClipsAsync(
                        clipsDirectory,
                        count: 2,
                        includeThumbnails: false),
                    service.GetRecentClipsAsync(
                        clipsDirectory,
                        count: 2,
                        includeThumbnails: false))
                .ConfigureAwait(false);
            Assert.True(
                simultaneousLoads.All(clips => clips.Count == 2),
                "Simultaneous library loads should both retain the validated clips.");
            Assert.Equal(
                2,
                runner.Invocations.Count,
                "Simultaneous loads must coalesce validation to one probe per unchanged clip.");

            var second = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 2,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(2, second.Count, "The cached library load should retain both clips.");
            Assert.Equal(
                2,
                runner.Invocations.Count,
                "Unchanged clips must reuse identity-bound probe results instead of starting ffprobe again.");

            await File.WriteAllBytesAsync(newestClip, [1, 2, 3, 4]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(newestClip, new DateTime(2026, 7, 13, 12, 0, 2, DateTimeKind.Utc));
            var afterChange = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 2,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(2, afterChange.Count, "A changed clip should remain discoverable after revalidation.");
            Assert.Equal(
                3,
                runner.Invocations.Count,
                "Changing a clip's metadata must invalidate only that clip's cached probe result.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestClipLibraryFailClosedAsync()
    {
        var testDirectory = CreateTestDirectory();
        var clipPath = Path.Combine(testDirectory, "Clip_2026-07-12_18-30-00.mp4");
        var probePath = Path.Combine(testDirectory, "ffprobe.exe");
        try
        {
            Directory.CreateDirectory(testDirectory);
            await File.WriteAllBytesAsync(clipPath, [1, 2, 3]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(probePath, [0x4D, 0x5A]).ConfigureAwait(false);

            var missingProbeRunner = new FakeClipMediaProcessRunner();
            var missingProbeService = new ClipLibraryService(
                () => null,
                () => null,
                missingProbeRunner,
                Path.Combine(testDirectory, "Cache-Missing"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
            var withoutProbe = await missingProbeService.GetRecentClipsAsync(
                    testDirectory,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(0, withoutProbe.Count, "The gallery must fail closed when ffprobe is unavailable.");
            Assert.Equal(0, missingProbeRunner.Invocations.Count, "No helper should run without a trusted probe path.");

            var timeoutRunner = new FakeClipMediaProcessRunner { TimeOutAllProbes = true };
            var timeoutService = new ClipLibraryService(
                () => null,
                () => probePath,
                timeoutRunner,
                Path.Combine(testDirectory, "Cache-Timeout"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
            var afterTimeout = await timeoutService.GetRecentClipsAsync(
                    testDirectory,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(0, afterTimeout.Count, "A timed-out media probe must not reach embedded playback.");
            Assert.Equal(1, timeoutRunner.Invocations.Count, "The timed-out candidate should be probed once.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestRuntimeLocalDataBoundariesAsync()
    {
        Assert.True(
            WasapiAudioPipe.ServerPipeOptions.HasFlag(PipeOptions.CurrentUserOnly),
            "The private audio pipe must reject clients running as another Windows user.");

        using (var process = Process.GetCurrentProcess())
        {
            var defaultBufferRoot = ReplayBufferService.GetDefaultBufferRoot();
            Assert.Equal(
                $"WindowsSession-{process.SessionId}",
                Path.GetFileName(defaultBufferRoot),
                "Replay buffers must be separated between simultaneous Windows logon sessions.");
            Assert.Equal(
                "Buffer",
                Path.GetFileName(Path.GetDirectoryName(defaultBufferRoot)),
                "The session-scoped replay root should remain below ClipForge's Buffer directory.");
        }

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

        var testDirectory = CreateTestDirectory();
        string? linkPath = null;
        try
        {
            Directory.CreateDirectory(testDirectory);
            Assert.True(
                ReplayBufferService.IsSafeBufferRootPath(
                    Path.Combine(testDirectory, "ClipForge", "Buffer")),
                "A not-yet-created buffer below regular ancestors should pass the root policy.");
            Assert.True(
                !ReplayBufferService.IsSafeBufferRootPath(Path.Combine("relative", "Buffer")),
                "A relative buffer root must be rejected.");

            var regularFile = Path.Combine(testDirectory, "not-a-directory");
            File.WriteAllText(regularFile, "regular file");
            Assert.True(
                !ReplayBufferService.IsSafeBufferRootPath(regularFile),
                "A regular file must not be accepted as a replay buffer root or ancestor.");

            var linkTarget = Path.Combine(testDirectory, "LinkTarget");
            Directory.CreateDirectory(linkTarget);
            linkPath = Path.Combine(testDirectory, "LinkedRoot");
            try
            {
                Directory.CreateSymbolicLink(linkPath, linkTarget);
                Assert.True(
                    !ReplayBufferService.IsSafeBufferRootPath(linkPath),
                    "A symbolic-link buffer root must be rejected.");
                var linkedBuffer = Path.Combine(linkPath, "Buffer");
                Assert.True(
                    !ReplayBufferService.IsSafeBufferRootPath(linkedBuffer),
                    "A buffer root below a symbolic-link ancestor must be rejected.");
                Assert.True(
                    !ReplayBufferService.IsSafeBufferDirectoryPath(
                        linkedBuffer,
                        Path.Combine(linkedBuffer, "session-20260712-test"),
                        FileAttributes.Directory),
                    "Session cleanup must reject a buffer root chain containing a symbolic link.");
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Windows developer mode or symbolic-link privilege may be unavailable on CI.
            }

            var cleanupRoot = Path.Combine(testDirectory, "CleanupRoot");
            var abandonedSession = Path.Combine(cleanupRoot, "session-20260712-crash-residue");
            Directory.CreateDirectory(abandonedSession);
            await File.WriteAllBytesAsync(
                    Path.Combine(abandonedSession, "segment-000000000.mkv"),
                    [1, 2, 3])
                .ConfigureAwait(false);
            await using (var replay = new ReplayBufferService(
                             new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                             cleanupRoot))
            {
                Assert.True(
                    !Directory.Exists(abandonedSession),
                    "Abandoned screen/audio replay data must be purged when the service starts.");
            }
        }
        finally
        {
            if (linkPath is not null && Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            DeleteTestDirectory(testDirectory);
        }

    }

    private static async Task TestClipLibraryCancellationAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            Directory.CreateDirectory(testDirectory);
            await File.WriteAllBytesAsync(
                    Path.Combine(testDirectory, "Clip_2026-07-12_18-30-00.mp4"),
                    [1])
                .ConfigureAwait(false);
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

    private sealed class FakeStartupShortcutBackend : IStartupShortcutBackend
    {
        public bool IsSupported { get; init; }

        public bool Registered { get; private set; }

        public string? LastInspectedExecutable { get; private set; }

        public string? LastCreatedExecutable { get; private set; }

        public string? LastCreatedArguments { get; private set; }

        public string? LastDeletedExecutable { get; private set; }

        public int DeleteCount { get; private set; }

        public bool IsRegistered(string relativeExecutablePath)
        {
            LastInspectedExecutable = relativeExecutablePath;
            return Registered;
        }

        public void Create(string relativeExecutablePath, string arguments)
        {
            LastCreatedExecutable = relativeExecutablePath;
            LastCreatedArguments = arguments;
            Registered = true;
        }

        public void Delete(string relativeExecutablePath)
        {
            LastDeletedExecutable = relativeExecutablePath;
            DeleteCount++;
            Registered = false;
        }
    }

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

    private sealed class FakeTrimMediaProcessRunner : IClipMediaProcessRunner
    {
        private double _lastTrimDurationSeconds = 1;

        public List<Invocation> Invocations { get; } = [];

        public TaskCompletionSource TrimStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailTrim { get; init; }

        public bool ReturnInvalidOutputMetadata { get; init; }

        public bool WaitForTrimCancellation { get; init; }

        public string SourceAverageFrameRate { get; init; } = "60/1";

        public string OutputAverageFrameRate { get; init; } = "60/1";

        public string SourceNominalFrameRate { get; init; } = "60/1";

        public string OutputNominalFrameRate { get; init; } = "60/1";

        public int TrimRunCount { get; private set; }

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
                var mediaPath = arguments[^1];
                var isTrimPartial = Path.GetFileName(mediaPath).StartsWith(
                    ".clipforge-trim-",
                    StringComparison.OrdinalIgnoreCase);
                var duration = isTrimPartial
                    ? ReturnInvalidOutputMetadata ? 99 : _lastTrimDurationSeconds
                    : 10;
                var durationText = duration.ToString("0.######", CultureInfo.InvariantCulture);
                var averageFrameRate = isTrimPartial
                    ? OutputAverageFrameRate
                    : SourceAverageFrameRate;
                var nominalFrameRate = isTrimPartial
                    ? OutputNominalFrameRate
                    : SourceNominalFrameRate;
                var output =
                    $"{{\"streams\":[{{\"codec_type\":\"video\",\"width\":1280,\"height\":720," +
                    $"\"avg_frame_rate\":\"{averageFrameRate}\",\"r_frame_rate\":\"{nominalFrameRate}\"," +
                    $"\"duration\":\"{durationText}\"}}," +
                    $"{{\"codec_type\":\"audio\",\"duration\":\"{durationText}\"}}]," +
                    $"\"format\":{{\"duration\":\"{durationText}\"}}}}";
                return new ClipMediaProcessResult(0, output, string.Empty, false);
            }

            var outputPath = arguments[^1];
            if (!Path.GetFileName(outputPath).StartsWith(
                    ".clipforge-trim-",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Hardware encoder probes deliberately fail so the deterministic
                // service test exercises the bounded software fallback.
                return new ClipMediaProcessResult(1, string.Empty, "probe unavailable", false);
            }

            TrimRunCount++;
            var durationArgument = GetArgumentAfter(arguments, "-t")
                ?? throw new InvalidOperationException("The trim invocation omitted its duration.");
            _lastTrimDurationSeconds = double.Parse(durationArgument, CultureInfo.InvariantCulture);
            await File.WriteAllBytesAsync(
                    outputPath,
                    [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p'],
                    cancellationToken)
                .ConfigureAwait(false);
            TrimStarted.TrySetResult();

            if (WaitForTrimCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return FailTrim
                ? new ClipMediaProcessResult(1, string.Empty, "scripted encoding failure", false)
                : new ClipMediaProcessResult(0, string.Empty, string.Empty, false);
        }

        public sealed record Invocation(
            string ExecutablePath,
            IReadOnlyList<string> Arguments,
            TimeSpan Timeout);
    }

    private sealed class FakeClipMediaProcessRunner : IClipMediaProcessRunner
    {
        public List<Invocation> Invocations { get; } = [];

        public int ThumbnailRunCount { get; private set; }

        public bool RejectAllProbes { get; init; }

        public bool TimeOutAllProbes { get; init; }

        public TimeSpan ProbeDelay { get; init; }

        public Action<IReadOnlyList<string>>? BeforeThumbnailWrite { get; init; }

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
                if (ProbeDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ProbeDelay, cancellationToken).ConfigureAwait(false);
                }

                if (TimeOutAllProbes)
                {
                    return new ClipMediaProcessResult(-1, string.Empty, string.Empty, true);
                }

                return RejectAllProbes || Path.GetFileName(arguments[^1]).Equals(
                        "Clip_2026-01-03_01-00-00.mp4",
                        StringComparison.OrdinalIgnoreCase)
                    ? new ClipMediaProcessResult(1, string.Empty, "invalid media", false)
                    : new ClipMediaProcessResult(
                        0,
                        "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"42.5\"}}",
                        string.Empty,
                        false);
            }

            ThumbnailRunCount++;
            BeforeThumbnailWrite?.Invoke(arguments);
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

        public static void Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }
    }
}
