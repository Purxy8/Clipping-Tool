using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using ClipForge.Controls;
using ClipForge.Models;
using ClipForge.Services;
using ClipForge.Capture;
using ClipForge.Testing;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipForge.Tests;

internal static class Program
{
    private const string CaptureJobCrashHelperArgument = "--capture-job-crash-helper";
    private static readonly byte[] ValidTestJpegBytes = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDvfh5458SaT4A8M2Nj4g1WzsrbTLWGC2t72SOOKNYlCoqhsKoAAAHAAooor8Sn8b9T8EzT/f8AEf45f+lM/9k=");
    private const string ProbeJobCrashHelperArgument = "--probe-job-crash-helper";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 &&
            string.Equals(args[0], CaptureJobCrashHelperArgument, StringComparison.Ordinal))
        {
            return await RunCaptureJobCrashHelperAsync(args[1]).ConfigureAwait(false);
        }
        if (args.Length == 2 &&
            string.Equals(args[0], ProbeJobCrashHelperArgument, StringComparison.Ordinal))
        {
            return await RunProbeJobCrashHelperAsync(args[1]).ConfigureAwait(false);
        }

        (string Name, Func<Task> Run)[] tests =
        [
            ("Replay length preset catalog", TestReplayLengthPresetsAsync),
            ("Resolution preset catalog", TestResolutionPresetsAsync),
            ("Windows autostart launch options", TestLaunchOptionsAsync),
            ("Windows autostart registration policy", TestStartupRegistrationAsync),
            ("Windows autostart replay decision", TestAutoStartReplayPolicyAsync),
            ("Recorder startup mode policy", TestRecorderStartupModePolicyAsync),
            ("Settings startup lifecycle policy", TestSettingsStartupLifecyclePolicyAsync),
            ("Best-effort shutdown cleanup", TestBestEffortShutdownCleanupAsync),
            ("Replay presentation state policy", TestReplayPresentationStatePolicyAsync),
            ("Capture settings change coalescing", TestCaptureSettingsChangeCoalescingAsync),
            ("Capture engine verification scheduling", TestCaptureEngineVerificationSchedulingAsync),
            ("Hotkey gesture validation", TestHotkeyGesturesAsync),
            ("Storage estimate helpers", TestStorageEstimatorAsync),
            ("Long recording storage and segment policy", TestLongRecordingPolicyAsync),
            ("Recorder recovery state discovery", TestRecorderRecoveryStateAsync),
            ("Central Recorder recovery journal discovery", TestCentralRecorderRecoveryJournalAsync),
            ("Unavailable Recorder journal capture gate", TestUnavailableRecorderRecoveryJournalAsync),
            ("Zero-segment Recorder recovery requires explicit discard", TestZeroSegmentRecorderRecoveryAsync),
            ("Discarded Recorder journal crash ordering", TestDiscardedRecorderRecoveryJournalAsync),
            ("Recorder journal newest candidate ordering", TestRecorderRecoveryJournalCandidateOrderingAsync),
            ("Recorder journal close fault containment", TestRecorderRecoveryJournalCloseFaultAsync),
            ("Recorder journal commit durability", TestRecorderRecoveryJournalCommitDurabilityAsync),
            ("Exact detached recovery beats stale journal", TestExactDetachedRecoveryPreferredAsync),
            ("Recorder journal partial segment recovery", TestRecorderJournalPartialSegmentRecoveryAsync),
            ("FFmpeg capture arguments", TestCaptureArgumentsAsync),
            ("WGC low-overhead capture path", TestWgcLowOverheadCapturePathAsync),
            ("WGC refresh-rate sampling policy", TestWgcRefreshRateSamplingPolicyAsync),
            ("Recorder locked output geometry", TestRecorderLockedOutputGeometryAsync),
            ("FFmpeg progress parser", TestCaptureProgressParserAsync),
            ("Capture starvation watchdog", TestCaptureStarvationWatchdogAsync),
            ("Capture recovery request gate", TestCaptureRecoveryRequestGateAsync),
            ("Scheduled capture refresh coordinator", TestScheduledCaptureRefreshCoordinatorAsync),
            ("Discontinuous capture refresh coalescing", TestDiscontinuousCaptureRefreshCoalescingAsync),
            ("Replay capture fallback recovery policy", TestReplayCaptureFallbackRecoveryPolicyAsync),
            ("Replay post-save state and scheduler", TestReplayPostSaveStateAndSchedulerAsync),
            ("Renewal segment quarantine provenance", TestRenewalSegmentQuarantineProvenanceAsync),
            ("Capture smoke argument propagation", TestCaptureSmokeArgumentPropagationAsync),
            ("Replay service concurrent disposal", TestReplayServiceConcurrentDisposalAsync),
            ("Replay maintenance and bounded pruning", TestReplayMaintenanceAndBoundedPruningAsync),
            ("Capture runtime journal", TestCaptureRuntimeJournalAsync),
            ("Capture runtime journal resource bounds", TestCaptureRuntimeJournalResourceBoundsAsync),
            ("Capture geometry matrix", TestCaptureGeometryMatrixAsync),
            ("Capture process job lifetime", TestCaptureProcessJobLifetimeAsync),
            ("FFmpeg probe crash containment", TestFfmpegProbeCrashContainmentAsync),
            ("FFmpeg encoder strategies", TestEncoderStrategiesAsync),
            ("FFmpeg capability priority", TestEncoderCapabilityPriorityAsync),
            ("FFmpeg degraded fallback cache", TestDegradedCapabilityCacheAsync),
            ("FFmpeg diagnostic prioritization", TestCaptureDiagnosticPriorityAsync),
            ("FFmpeg concat arguments", TestConcatArgumentsAsync),
            ("Replay export timeline validation", TestReplayExportTimelineValidationAsync),
            ("FFmpeg trim arguments", TestTrimArgumentsAsync),
            ("Trim range and output naming", TestTrimRangeAndNamingAsync),
            ("Configured FFmpeg discovery", TestConfiguredFfmpegDiscoveryAsync),
            ("Pinned FFmpeg trust policy", TestPinnedFfmpegTrustPolicyAsync),
            ("Transactional FFmpeg tool-pair publication", TestTransactionalFfmpegToolPairAsync),
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
            ("Settings I/O fallback", TestSettingsIoFallbackAsync),
            ("Settings concurrent disposal", TestSettingsConcurrentDisposalAsync),
            ("Secure clip discovery and thumbnail cache", TestClipLibraryAsync),
            ("Replay-safe thumbnail hydration", TestReplayThumbnailHydrationAsync),
            ("Clip classification and filtered discovery", TestClipClassificationAndFilteringAsync),
            ("Transactional clip trimming", TestClipTrimServiceAsync),
            ("Trim free-space policy", TrimFreeSpacePolicyTests.RunAsync),
            ("Clip path and media process hardening", TestClipLibrarySecurityPolicyAsync),
            ("Long-path pinned media validation", TestLongPathPinnedMediaValidationAsync),
            ("Race-resistant clip deletion", TestClipDeletionAsync),
            ("Thumbnail decoding releases cache files", TestThumbnailDecoderReleasesFileAsync),
            ("Corrupt thumbnail cache regeneration", TestCorruptThumbnailRegenerationAsync),
            ("Clip library fail-closed probing", TestClipLibraryFailClosedAsync),
            ("Clip library probe budget", TestClipLibraryProbeBudgetAsync),
            ("Clip library probe result cache", TestClipLibraryProbeCacheAsync),
            ("Clip library negative probe cache", TestClipLibraryNegativeProbeCacheAsync),
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

    private static Task TestRenewalSegmentQuarantineProvenanceAsync()
    {
        var knownGenerationHeads = new HashSet<int> { 0, 3, 6 };
        RenewalSegmentSequence.AssertMonotonicWithKnownQuarantinedHeads(
            [1, 2, 4, 5, 7, 8],
            knownGenerationHeads,
            "in the deterministic renewal regression");

        Assert.Throws<InvalidDataException>(
            () => RenewalSegmentSequence.AssertMonotonicWithKnownQuarantinedHeads(
                [1, 2, 5],
                new HashSet<int> { 3 },
                "with an unknown missing id"),
            "A gap containing a non-quarantined segment id must fail renewal provenance.");
        Assert.Throws<InvalidDataException>(
            () => RenewalSegmentSequence.AssertMonotonicWithKnownQuarantinedHeads(
                [1, 2, 2],
                knownGenerationHeads,
                "with a reused id"),
            "A duplicate or reused segment id must fail renewal provenance.");

        static IReadOnlyList<int> Select(
            IReadOnlyList<(int Number, bool Trusted)> segments,
            int maximumCount = 10) =>
            ReplayBufferService.SelectNewestContiguousTrustedSuffix(
                    segments,
                    segments.Count,
                    maximumCount,
                    static segment => segment.Number,
                    static segment => segment.Trusted)
                .Select(segment => segment.Number)
                .ToArray();

        Assert.SequenceEqual(
            [4, 5],
            Select([(1, true), (2, true), (3, false), (4, true), (5, true)]),
            "Export crossed an untrusted capture-generation head.");
        Assert.SequenceEqual(
            [4, 5],
            Select([(1, true), (2, true), (4, true), (5, true)]),
            "Export crossed an unexplained segment-number gap.");
        Assert.SequenceEqual(
            [2, 3],
            Select([(1, true), (2, true), (3, true)], maximumCount: 2),
            "The newest bounded contiguous suffix was not selected.");
        Assert.SequenceEqual(
            Array.Empty<int>(),
            Select([(1, true), (2, false)]),
            "An untrusted newest completed segment fell back to an older generation.");
        Assert.True(
            ReplayBufferService.IsCaptureGenerationExportable(
                selectedGeneration: 5,
                blockedGeneration: 4) &&
            !ReplayBufferService.IsCaptureGenerationExportable(
                selectedGeneration: 5,
                blockedGeneration: 5) &&
            !ReplayBufferService.IsCaptureGenerationExportable(
                selectedGeneration: -1,
                blockedGeneration: -1),
            "A save could commit an invalid or cadence-blocked capture generation.");

        return Task.CompletedTask;
    }

    private static Task TestCaptureSmokeArgumentPropagationAsync()
    {
        var forwarded =
            CaptureSmokeArgumentPolicy.GetInteractiveMatrixForwardedSwitches(
                [
                    "--wgc-matrix",
                    "--FORCE-WGC",
                    "--audio",
                    "--print-probe-diagnostics",
                    "--unknown-switch"
                ]);

        Assert.SequenceEqual(
            ["--audio", "--force-wgc", "--print-probe-diagnostics"],
            forwarded,
            "The live matrix did not forward its WGC backend or diagnostic switches exactly.");
        Assert.True(
            !forwarded.Contains("--force-gdi", StringComparer.OrdinalIgnoreCase),
            "The live matrix invented a fallback backend switch that was not requested.");
        return Task.CompletedTask;
    }

    private static async Task TestCaptureSettingsChangeCoalescingAsync()
    {
        var applyCount = 0;
        var latestSelection = -1;
        var appliedSelection = -1;
        var appliedRestartRequired = false;
        var coordinator = new CaptureConfigurationChangeCoordinator(
            TimeSpan.FromMilliseconds(40),
            restartRequired =>
            {
                Interlocked.Increment(ref applyCount);
                appliedSelection = latestSelection;
                appliedRestartRequired = restartRequired;
                return Task.CompletedTask;
            });

        var requests = new List<Task>();
        for (var selection = 0; selection < 50; selection++)
        {
            latestSelection = selection;
            requests.Add(coordinator.RequestAsync(
                restartRequired: selection == 17));
            await Task.Delay(TimeSpan.FromMilliseconds(2)).ConfigureAwait(false);
        }

        await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        Assert.Equal(
            1,
            applyCount,
            "A rapid settings burst must execute only one final configuration apply.");
        Assert.Equal(
            49,
            appliedSelection,
            "The coalesced apply did not persist the final visible selection.");
        Assert.True(
            appliedRestartRequired,
            "A restart-required change was lost while coalescing later selections.");
    }

    private static async Task TestCaptureEngineVerificationSchedulingAsync()
    {
        using var verificationStarted = new ManualResetEventSlim();
        using var releaseVerification = new ManualResetEventSlim();
        var callerThreadId = Environment.CurrentManagedThreadId;
        var verificationThreadId = -1;
        var verificationCount = 0;
        var coordinator = new CaptureEngineVerificationCoordinator(() =>
        {
            verificationThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref verificationCount);
            verificationStarted.Set();
            releaseVerification.Wait(TimeSpan.FromSeconds(5));
            return true;
        });

        var first = coordinator.VerifyAsync(forceVerification: false);
        Assert.True(
            verificationStarted.Wait(TimeSpan.FromSeconds(2)),
            "The background capture-engine verification did not start.");
        Assert.True(
            verificationThreadId != callerThreadId,
            "Capture-engine trust verification ran inline on the caller/UI thread.");
        Assert.True(
            !first.IsCompleted,
            "Engine readiness was published before trust verification completed.");

        var concurrentRefresh = coordinator.VerifyAsync(forceVerification: true);
        Assert.Equal(
            1,
            Volatile.Read(ref verificationCount),
            "A concurrent refresh duplicated the in-flight FFmpeg hash pass.");
        releaseVerification.Set();
        Assert.True(
            await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) &&
            await concurrentRefresh.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
            "A successful trust verification did not publish engine readiness.");
        Assert.Equal(
            1,
            verificationCount,
            "Coalesced verification callers did not share one completed result.");
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
        Assert.True(Path.IsPathFullyQualified(path), "The default save directory must be absolute.");
        Assert.Equal("ClipForge", Path.GetFileName(path), "The default save directory should have a ClipForge folder.");

        var localFallbackRoot = Path.Combine(
            Path.GetTempPath(),
            "ClipForge-default-save-directory-test");
        var localFallback = AppSettings.ResolveDefaultSaveDirectory(
            videosDirectory: string.Empty,
            documentsDirectory: string.Empty,
            localFallbackRoot);
        Assert.Equal(
            Path.Combine(localFallbackRoot, "ClipForge", "Clips"),
            localFallback,
            "Missing media folders should fall back to a local application-data Clips directory.");
        Assert.True(
            Path.IsPathFullyQualified(
                AppSettings.ResolveDefaultSaveDirectory(
                    videosDirectory: null,
                    documentsDirectory: null,
                    localApplicationDataDirectory: null)),
            "The last-resort default clips directory must remain absolute.");

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

    private static async Task TestAutoStartReplayPolicyAsync()
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

        var startupSteps = new List<string>();
        await MainWindow.RunAutoStartReplaySequenceAsync(
            shouldAutoStartReplay: true,
            preloadClipLibrary: () =>
            {
                startupSteps.Add("library");
                return Task.CompletedTask;
            },
            startReplay: () =>
            {
                startupSteps.Add("replay");
                return Task.CompletedTask;
            });
        Assert.SequenceEqual(
            new[] { "library", "replay" },
            startupSteps,
            "Windows autostart must populate Recent clips before replay suppresses library work.");

        startupSteps.Clear();
        await MainWindow.RunAutoStartReplaySequenceAsync(
            shouldAutoStartReplay: false,
            preloadClipLibrary: () =>
            {
                startupSteps.Add("library");
                return Task.CompletedTask;
            },
            startReplay: () =>
            {
                startupSteps.Add("replay");
                return Task.CompletedTask;
            });
        Assert.Equal(0, startupSteps.Count,
            "A normal launch must leave gallery refresh and manual replay startup on their existing paths.");

        var preloadActive = 0;
        var replayStarted = false;
        var boundedStart = Stopwatch.StartNew();
        await MainWindow.RunAutoStartReplaySequenceAsync(
            shouldAutoStartReplay: true,
            preloadClipLibrary: async () =>
            {
                var completed = await MainWindow.RunBoundedAutoStartPreloadAsync(
                    async cancellationToken =>
                    {
                        Interlocked.Exchange(ref preloadActive, 1);
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref preloadActive, 0);
                        }
                    },
                    TimeSpan.FromMilliseconds(80),
                    CancellationToken.None);
                Assert.True(
                    !completed,
                    "A hung autostart library preload ignored its bounded timeout.");
            },
            startReplay: () =>
            {
                Assert.Equal(
                    0,
                    Volatile.Read(ref preloadActive),
                    "Replay started while the cancelled library helper was still unwinding.");
                replayStarted = true;
                return Task.CompletedTask;
            });
        boundedStart.Stop();
        Assert.True(
            replayStarted && boundedStart.Elapsed < TimeSpan.FromSeconds(2),
            "A hung cached-library preload held Windows autostart replay too long.");

        var releaseNonCooperativePreload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hardBoundedStart = Stopwatch.StartNew();
        var hardBoundedResult = await MainWindow.RunBoundedAutoStartPreloadAsync(
            _ => releaseNonCooperativePreload.Task,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);
        hardBoundedStart.Stop();
        releaseNonCooperativePreload.TrySetResult();
        Assert.True(
            !hardBoundedResult &&
            hardBoundedStart.Elapsed < TimeSpan.FromSeconds(1),
            "A non-cooperative filesystem preload defeated the hard autostart deadline.");

        var readySnapshot = CreateReplayStateSnapshot(ReplayState.Ready);
        Assert.True(
            MainWindow.ShouldRecoverAutoStartLibraryDuringReady(
                refreshPending: true,
                currentClipCount: 0,
                requestedClipCount: 4,
                isClosing: false,
                isVisible: true,
                isActive: true,
                captureCritical: false,
                replayServiceRunning: true,
                snapshot: readySnapshot),
            "A timed-out autostart preload could not recover its empty gallery after replay became Ready.");
        Assert.True(
            !MainWindow.ShouldRecoverAutoStartLibraryDuringReady(
                refreshPending: true,
                currentClipCount: 0,
                requestedClipCount: 4,
                isClosing: false,
                isVisible: true,
                isActive: true,
                captureCritical: false,
                replayServiceRunning: true,
                snapshot: CreateReplayStateSnapshot(ReplayState.Buffering)),
            "Gallery metadata recovery started while the replay engine was still buffering.");
        Assert.True(
            !MainWindow.ShouldRecoverAutoStartLibraryDuringReady(
                refreshPending: true,
                currentClipCount: 4,
                requestedClipCount: 4,
                isClosing: false,
                isVisible: true,
                isActive: true,
                captureCritical: false,
                replayServiceRunning: true,
                snapshot: readySnapshot),
            "A complete cached gallery scheduled unnecessary capture-time discovery.");
    }

    private static Task TestRecorderStartupModePolicyAsync()
    {
        Assert.Equal(
            CaptureSessionMode.Recording,
            MainWindow.ResolveAutomaticCaptureMode(
                isAutoStartLaunch: true,
                replayPreferenceEnabled: false,
                recordingPreferenceEnabled: true,
                initializationCompleted: true,
                engineReady: true,
                captureRunning: false,
                isClosing: false),
            "An opted-in ready Windows launch did not choose Recorder.");
        Assert.Equal(
            CaptureSessionMode.InstantReplay,
            MainWindow.ResolveAutomaticCaptureMode(
                true,
                replayPreferenceEnabled: true,
                recordingPreferenceEnabled: true,
                initializationCompleted: true,
                engineReady: true,
                captureRunning: false,
                isClosing: false),
            "Malformed dual startup preferences must fail safely to bounded Instant Replay.");
        Assert.Equal<CaptureSessionMode?>(
            null,
            MainWindow.ResolveAutomaticCaptureMode(
                true,
                replayPreferenceEnabled: false,
                recordingPreferenceEnabled: true,
                initializationCompleted: true,
                engineReady: true,
                captureRunning: true,
                isClosing: false),
            "A pending or active capture session did not suppress automatic Recorder startup.");
        Assert.Equal<CaptureSessionMode?>(
            null,
            MainWindow.ResolveAutomaticCaptureMode(
                isAutoStartLaunch: false,
                replayPreferenceEnabled: false,
                recordingPreferenceEnabled: true,
                initializationCompleted: true,
                engineReady: true,
                captureRunning: false,
                isClosing: false),
            "Interactive launch unexpectedly auto-started Recorder.");

        var malformed = new AppSettings
        {
            StartReplayWithWindows = true,
            StartRecordingWithWindows = true
        };
        MainWindow.NormalizeAutomaticCapturePreferences(malformed);
        Assert.True(
            malformed.StartReplayWithWindows &&
            !malformed.StartRecordingWithWindows,
            "Startup preference normalization did not preserve the bounded fail-safe mode.");

        return Task.CompletedTask;
    }

    private static Task TestSettingsStartupLifecyclePolicyAsync()
    {
        Assert.True(
            MainWindow.ShouldRemoveStaleAutoStartRegistration(
                isAutoStartLaunch: true,
                initializationCompleted: true,
                settingsLoadOutcome: SettingsLoadOutcome.Loaded,
                preferenceEnabled: false),
            "Only a successfully loaded explicit opt-out should remove the Startup shortcut.");

        foreach (var outcome in new SettingsLoadOutcome?[]
                 {
                     null,
                     SettingsLoadOutcome.Missing,
                     SettingsLoadOutcome.Invalid,
                     SettingsLoadOutcome.TransientFailure
                 })
        {
            Assert.True(
                !MainWindow.ShouldRemoveStaleAutoStartRegistration(
                    isAutoStartLaunch: true,
                    initializationCompleted: true,
                    settingsLoadOutcome: outcome,
                    preferenceEnabled: false),
                "A missing, invalid, unavailable, or incomplete settings load is not an explicit opt-out.");
        }

        Assert.True(
            !MainWindow.ShouldRemoveStaleAutoStartRegistration(
                isAutoStartLaunch: true,
                initializationCompleted: false,
                settingsLoadOutcome: SettingsLoadOutcome.Loaded,
                preferenceEnabled: false),
            "An incomplete startup must preserve the Startup shortcut.");
        Assert.True(
            !MainWindow.ShouldRemoveStaleAutoStartRegistration(
                isAutoStartLaunch: true,
                initializationCompleted: true,
                settingsLoadOutcome: SettingsLoadOutcome.Loaded,
                preferenceEnabled: true),
            "An explicit opt-in must preserve the Startup shortcut.");

        Assert.True(
            MainWindow.ShouldPersistSettingsOnShutdown(
                SettingsLoadOutcome.Loaded,
                settingsControlsPopulated: true),
            "Fully loaded settings should be persisted during orderly shutdown.");
        Assert.True(
            MainWindow.ShouldPersistSettingsOnShutdown(
                SettingsLoadOutcome.Missing,
                settingsControlsPopulated: true),
            "Confirmed first-run defaults may be persisted after controls are populated.");

        foreach (var outcome in new SettingsLoadOutcome?[]
                 {
                     null,
                     SettingsLoadOutcome.Invalid,
                     SettingsLoadOutcome.TransientFailure
                 })
        {
            Assert.True(
                !MainWindow.ShouldPersistSettingsOnShutdown(
                    outcome,
                    settingsControlsPopulated: true),
                "Untrusted fallback settings must not overwrite the user's file during shutdown.");
        }

        Assert.True(
            !MainWindow.ShouldPersistSettingsOnShutdown(
                SettingsLoadOutcome.Loaded,
                settingsControlsPopulated: false),
            "Exit before control hydration must not persist partial settings.");
        return Task.CompletedTask;
    }

    private static async Task TestBestEffortShutdownCleanupAsync()
    {
        var steps = new List<string>();
        await MainWindow.RunBestEffortShutdownStepsAsync(
            () =>
            {
                steps.Add("save");
                return Task.FromException(new IOException("Injected save failure."));
            },
            () =>
            {
                steps.Add("stop");
                return Task.FromException(new InvalidOperationException("Injected stop failure."));
            },
            () =>
            {
                steps.Add("dispose");
                return Task.FromException(new ObjectDisposedException("Injected dispose failure."));
            });
        Assert.SequenceEqual(
            new[] { "save", "stop", "dispose" },
            steps,
            "Every shutdown phase must run even when every preceding phase fails.");

        steps.Clear();
        await MainWindow.RunBestEffortShutdownStepsAsync(
            persistSettingsAsync: null,
            () =>
            {
                steps.Add("stop");
                return Task.CompletedTask;
            },
            () =>
            {
                steps.Add("dispose");
                return Task.CompletedTask;
            });
        Assert.SequenceEqual(
            new[] { "stop", "dispose" },
            steps,
            "Skipping an unsafe settings save must not skip capture disposal.");
    }

    private static Task TestReplayPresentationStatePolicyAsync()
    {
        (ReplayState State, bool IsSession, bool SuspendsPresentation)[] cases =
        [
            (ReplayState.Stopped, false, false),
            (ReplayState.Starting, true, true),
            (ReplayState.Buffering, true, false),
            (ReplayState.Ready, true, false),
            (ReplayState.Saving, true, true),
            (ReplayState.Faulted, false, false),
            (ReplayState.Stopping, true, true)
        ];

        Assert.SequenceEqual(
            Enum.GetValues<ReplayState>(),
            cases.Select(item => item.State),
            "The replay presentation matrix must explicitly cover every ReplayState.");
        foreach (var item in cases)
        {
            var snapshot = CreateReplayStateSnapshot(item.State);
            Assert.Equal(
                item.IsSession,
                MainWindow.IsReplaySessionState(snapshot),
                $"{item.State} has the wrong replay-session classification.");
            Assert.Equal(
                item.SuspendsPresentation,
                MainWindow.IsCapturePresentationSuspendedState(snapshot),
                $"{item.State} has the wrong presentation-suspension classification.");
            Assert.Equal(
                item.IsSession,
                MainWindow.ShouldSuppressAutomaticLibraryWork(
                    captureCritical: false,
                    replayServiceRunning: false,
                    snapshot),
                $"{item.State} has the wrong automatic-library-work policy.");
            Assert.True(!item.SuspendsPresentation || item.IsSession,
                $"{item.State} cannot suspend presentation outside an active replay session.");
        }

        var stopped = CreateReplayStateSnapshot(ReplayState.Stopped);
        Assert.True(
            MainWindow.ShouldSuppressAutomaticLibraryWork(
                captureCritical: true,
                replayServiceRunning: false,
                stopped),
            "Starting capture must suppress gallery helpers before replay publishes its first state.");
        Assert.True(
            MainWindow.ShouldSuppressAutomaticLibraryWork(
                captureCritical: false,
                replayServiceRunning: true,
                stopped),
            "A running capture process must suppress gallery helpers even during a delayed state update.");
        Assert.True(
            !MainWindow.ShouldSuppressAutomaticLibraryWork(
                captureCritical: false,
                replayServiceRunning: false,
                stopped),
            "Automatic gallery work may resume only after capture and replay are fully stopped.");

        var ready = CreateReplayStateSnapshot(ReplayState.Ready);
        Assert.True(
            MainWindow.ShouldHydrateRecentClipThumbnails(
                isClosing: false,
                isVisible: true,
                isActive: true,
                captureCritical: false,
                replayServiceRunning: true,
                ready,
                clipCount: 4,
                missingThumbnailCount: 2),
            "A foreground steady replay may hydrate already validated recent cards.");
        Assert.True(
            MainWindow.ShouldHydrateRecentClipThumbnails(
                isClosing: false,
                isVisible: true,
                isActive: true,
                captureCritical: false,
                replayServiceRunning: false,
                stopped,
                clipCount: 4,
                missingThumbnailCount: 2),
            "A foreground stopped session must retry transiently missing thumbnails.");

        (bool Closing, bool Visible, bool Active, bool Critical, bool Running, ReplayState State, int Clips, int Missing)[]
            blockedHydrationCases =
            [
                (true, true, true, false, true, ReplayState.Ready, 4, 2),
                (false, false, true, false, true, ReplayState.Ready, 4, 2),
                (false, true, false, false, true, ReplayState.Ready, 4, 2),
                (false, true, true, true, true, ReplayState.Ready, 4, 2),
                (false, true, true, false, false, ReplayState.Ready, 4, 2),
                (false, true, true, false, true, ReplayState.Buffering, 4, 2),
                (false, true, true, false, true, ReplayState.Saving, 4, 2),
                (false, true, true, false, true, ReplayState.Ready, 0, 0),
                (false, true, true, false, true, ReplayState.Ready, 4, 0),
                (false, true, true, false, true, ReplayState.Ready, 1, 2)
            ];
        foreach (var item in blockedHydrationCases)
        {
            Assert.True(
                !MainWindow.ShouldHydrateRecentClipThumbnails(
                    item.Closing,
                    item.Visible,
                    item.Active,
                    item.Critical,
                    item.Running,
                    CreateReplayStateSnapshot(item.State),
                    item.Clips,
                    item.Missing),
                $"Unsafe thumbnail hydration policy was accepted for {item.State}.");
        }

        Assert.True(
            MainWindow.ShouldContinueRecentClipThumbnailHydration(
                missingThumbnailCountBefore: 4,
                missingThumbnailCountAfter: 2),
            "A bounded hydration pass that made partial progress must continue.");
        foreach (var counts in new[] { (Before: 4, After: 4), (Before: 4, After: 0), (Before: 0, After: 0) })
        {
            Assert.True(
                !MainWindow.ShouldContinueRecentClipThumbnailHydration(
                    counts.Before,
                    counts.After),
                $"Thumbnail hydration must stop after no progress or completion ({counts}).");
        }

        Assert.True(
            LibraryWindow.ShouldSuppressAutomaticRefresh(
                replayRunning: true,
                presentationSuspended: false,
                trimInProgress: false),
            "Library discovery and thumbnail work must remain deferred for the whole replay session.");
        Assert.True(
            !LibraryWindow.ShouldSuppressAutomaticRefresh(
                replayRunning: false,
                presentationSuspended: false,
                trimInProgress: false),
            "The Library may refresh after replay and other foreground work have stopped.");
        Assert.True(
            LibraryWindow.ShouldSuppressAutomaticRefresh(
                replayRunning: false,
                presentationSuspended: false,
                trimInProgress: true),
            "Library helpers must stay deferred while the trim editor or export owns the clip.");
        Assert.True(
            LibraryWindow.ShouldSuppressAutomaticRefresh(
                replayRunning: false,
                presentationSuspended: false,
                trimInProgress: false,
                explicitPlaybackOrMediaOpen: true),
            "Library helpers must stay deferred from explicit validation through media open and playback.");

        return Task.CompletedTask;
    }

    private static ReplayStateSnapshot CreateReplayStateSnapshot(ReplayState state) =>
        new(
            state,
            AvailableDuration: TimeSpan.Zero,
            Retention: TimeSpan.FromMinutes(2),
            BufferBytes: 0);

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

        var replaySafeSelection = LibraryMediaOpenPlan.Create(
            autoplay: true,
            playbackVolume: 0);
        Assert.Equal(0d, replaySafeSelection.PrimeVolume,
            "Replay-safe media priming must remain silent.");
        Assert.Equal(0d, replaySafeSelection.PlaybackVolume,
            "Replay-safe playback must stay muted until the user explicitly opts in to audio.");
        Assert.True(replaySafeSelection.MustPrimeWithPlay && replaySafeSelection.ContinueAfterOpened,
            "A replay-safe foreground selection must still build and continue its media graph.");
        Assert.True(
            MediaPlaybackStartupPolicy.ShouldKeepPrimedPlaybackRunning(
                autoplay: true,
                hasRestorePosition: false),
            "Autoplay without a restore seek must keep the already-running priming graph alive.");
        Assert.True(
            !MediaPlaybackStartupPolicy.ShouldKeepPrimedPlaybackRunning(
                autoplay: true,
                hasRestorePosition: true),
            "A restore seek must pause primed playback before changing the media position.");
        Assert.True(
            !MediaPlaybackStartupPolicy.ShouldKeepPrimedPlaybackRunning(
                autoplay: false,
                hasRestorePosition: false) &&
            !MediaPlaybackStartupPolicy.ShouldKeepPrimedPlaybackRunning(
                autoplay: false,
                hasRestorePosition: true),
            "A non-autoplay open must never leak the muted priming playback state.");
        Assert.True(
            LibraryWindow.ShouldResumeAutomaticMediaWork(
                isSuspended: true,
                requestedOwner: 12,
                currentOwner: 12),
            "The active playback owner must be able to release helper suspension.");
        Assert.True(
            !LibraryWindow.ShouldResumeAutomaticMediaWork(
                isSuspended: true,
                requestedOwner: 11,
                currentOwner: 12),
            "A stale playback completion must not release a newer media-open suspension.");
        Assert.True(
            LibraryWindow.ShouldResumeAutomaticMediaWork(
                isSuspended: true,
                requestedOwner: 0,
                currentOwner: 12),
            "A background or shutdown transition must invalidate every pending playback owner.");
        Assert.True(
            !LibraryWindow.ShouldResumeAutomaticMediaWork(
                isSuspended: false,
                requestedOwner: 12,
                currentOwner: 12),
            "An already released suspension must remain idempotent.");
        Assert.True(
            LibraryWindow.ShouldDeferAutomaticMediaOpen(
                replayRunning: true,
                beginTrimWhenReady: false),
            "Opening Library during replay must not allocate a decoder without user intent.");
        Assert.True(
            !LibraryWindow.ShouldDeferAutomaticMediaOpen(
                replayRunning: true,
                beginTrimWhenReady: true),
            "An explicit direct-trim request must be allowed to attach its source during replay.");
        Assert.True(
            !LibraryWindow.ShouldDeferAutomaticMediaOpen(
                replayRunning: false,
                beginTrimWhenReady: false),
            "Normal Library browsing may preload the selected paused clip.");
        Assert.True(
            LibraryWindow.ShouldOpenRequestedTrimDirectly(
                isLoaded: true,
                isVisible: true,
                isActive: true,
                presentationSuspended: false),
            "An existing foreground Library must open an identity-bound trim request without a discovery pass.");
        foreach (var state in new[]
                 {
                     (Loaded: false, Visible: true, Active: true, Suspended: false),
                     (Loaded: true, Visible: false, Active: true, Suspended: false),
                     (Loaded: true, Visible: true, Active: false, Suspended: false),
                     (Loaded: true, Visible: true, Active: true, Suspended: true)
                 })
        {
            Assert.True(
                !LibraryWindow.ShouldOpenRequestedTrimDirectly(
                    state.Loaded,
                    state.Visible,
                    state.Active,
                    state.Suspended),
                $"Unsafe direct trim open was accepted for {state}.");
        }

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
        foreach (var state in new[] { ReplayState.Buffering, ReplayState.Ready })
        {
            var snapshot = CreateReplayStateSnapshot(state);
            Assert.True(MainWindow.IsReplaySessionState(snapshot),
                $"{state} must remain part of the active replay session.");
            Assert.True(!MainWindow.IsCapturePresentationSuspendedState(snapshot),
                $"{state} must not suspend foreground playback presentation.");
            Assert.True(
                MainWindow.ShouldHandlePlayerMediaEvent(
                    captureCritical: false,
                    isClosing: false,
                    isVisible: true,
                    isActive: true,
                    hasCurrentClip: true,
                    hasSource: true),
                $"A foreground {state} replay player event should be handled.");
        }
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
            TimeSpan.FromSeconds(10),
            OverlayPresentationPolicy.AutoHideDelay,
            "The topmost overlay should have a short, bounded presentation lifetime.");
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            OverlayPresentationPolicy.HardMaximumVisibleDuration,
            "The topmost overlay must have an absolute lifetime even if mouse capture gets stuck.");
        Assert.True(
            OverlayPresentationPolicy.ShouldAutoHide(
                isVisible: true,
                hasMouseCapture: false,
                visibleDuration: OverlayPresentationPolicy.AutoHideDelay),
            "An idle visible overlay must dismiss itself so it cannot keep fullscreen composition active.");
        Assert.True(
            !OverlayPresentationPolicy.ShouldAutoHide(
                isVisible: false,
                hasMouseCapture: false,
                visibleDuration: OverlayPresentationPolicy.HardMaximumVisibleDuration),
            "A hidden overlay must not run recurring dismissal work.");
        Assert.True(
            !OverlayPresentationPolicy.ShouldAutoHide(
                isVisible: true,
                hasMouseCapture: true,
                visibleDuration: TimeSpan.FromSeconds(12)),
            "The overlay must not disappear in the middle of an active drag.");
        Assert.True(
            OverlayPresentationPolicy.ShouldAutoHide(
                isVisible: true,
                hasMouseCapture: true,
                visibleDuration: OverlayPresentationPolicy.HardMaximumVisibleDuration),
            "A stuck mouse capture must not leave the overlay topmost indefinitely.");
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            OverlayPresentationPolicy.GetNextAutoHideDelay(TimeSpan.FromSeconds(12)),
            "The retry timer must wake at the absolute overlay deadline.");

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
        Assert.True(
            MainWindow.CanQueryStorageFreeSpace(@"C:\Videos\ClipForge"),
            "A regular local save path should allow asynchronous capacity lookup.");
        Assert.True(
            !MainWindow.CanQueryStorageFreeSpace(@"\\offline-server\clips"),
            "UNC capacity lookup must be skipped instead of blocking the WPF dispatcher.");
        Assert.True(
            MainWindow.IsDefinitelyFixedLocalPath(
                Path.Combine(
                    Path.GetPathRoot(Environment.SystemDirectory)!,
                    "ClipForge")),
            "The fixed system volume was excluded from safe autostart preload.");
        Assert.True(
            !MainWindow.IsDefinitelyFixedLocalPath(@"\\offline-server\clips"),
            "A UNC folder was allowed into capture-critical autostart preload.");
        Assert.True(
            ClipLibraryService.ShouldContinueDiscovery(
                inspectedEntries: 16_384,
                elapsed: TimeSpan.FromSeconds(2)),
            "Library discovery stopped before its documented bounded edge.");
        Assert.True(
            !ClipLibraryService.ShouldContinueDiscovery(
                inspectedEntries: 16_385,
                elapsed: TimeSpan.FromMilliseconds(10)) &&
            !ClipLibraryService.ShouldContinueDiscovery(
                inspectedEntries: 1,
                elapsed: TimeSpan.FromSeconds(2.01)),
            "Library discovery must be bounded by both inspected entries and elapsed work.");
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
        Assert.ContainsSequence(arguments, "-draw_mouse", "1");
        var cursorlessGdiArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration with { CaptureCursor = false },
            audioInputs,
            @"C:\Buffer");
        Assert.ContainsSequence(cursorlessGdiArguments, "-draw_mouse", "0");
        Assert.ContainsSequence(arguments, "-c:v", "libx264", "-preset", "ultrafast");
        Assert.ContainsSequence(arguments, "-g", "120", "-keyint_min", "120");
        Assert.ContainsSequence(arguments, "-f", "segment", "-segment_time", "2");
        Assert.True(
            arguments.Any(argument => argument.Contains("amix=inputs=2", StringComparison.Ordinal)),
            "Two selected audio endpoints must be mixed.");
        Assert.Equal(
            "scale=1280:720:flags=fast_bilinear,format=yuv420p,setpts=PTS-STARTPTS",
            GetArgumentAfter(arguments, "-vf"),
            "Fixed-resolution GDI capture must downscale directly without a padded canvas.");
        Assert.True(
            arguments[^1].EndsWith("segment-%09d.mkv", StringComparison.Ordinal),
            "Capture output must be a numbered Matroska segment pattern.");

        var resumedArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            audioInputs,
            VideoEncodingStrategy.SoftwareGdi,
            @"C:\Buffer",
            segmentStartNumber: 1_234);
        Assert.ContainsSequence(
            resumedArguments,
            "-segment_start_number",
            "1234");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegArgumentBuilder.BuildCaptureArguments(
                configuration,
                audioInputs,
                VideoEncodingStrategy.SoftwareGdi,
                @"C:\Buffer",
                segmentStartNumber: -1),
            "A capture renewal must never reuse a negative segment number.");
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            ReplayBufferService.CaptureProcessMaximumAge,
            "Long-running WGC processes must have a bounded lifetime.");
        Assert.True(
            !ReplayBufferService.ShouldScheduleCaptureRefresh(
                DesktopCaptureBackend.WindowsGraphicsCapture,
                ReplayBufferService.CaptureProcessMaximumAge - TimeSpan.FromMilliseconds(1)),
            "WGC renewal must not run before the bounded process age.");
        Assert.True(
            ReplayBufferService.ShouldScheduleCaptureRefresh(
                DesktopCaptureBackend.WindowsGraphicsCapture,
                ReplayBufferService.CaptureProcessMaximumAge),
            "WGC renewal must run at the bounded process age.");
        Assert.True(
            !ReplayBufferService.ShouldScheduleCaptureRefresh(
                DesktopCaptureBackend.Gdi,
                TimeSpan.FromHours(24)),
            "The compatibility GDI backend must not enter the WGC renewal path.");

        var sourceArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration with
            {
                Resolution = ResolutionOption.All.Single(option => option.Id == "source")
            },
            [],
            @"C:\Buffer");
        Assert.Equal(
            "null,format=yuv420p,setpts=PTS-STARTPTS",
            GetArgumentAfter(sourceArguments, "-vf"),
            "Source/native GDI capture must remain a no-resize path.");

        return Task.CompletedTask;
    }

    private static Task TestWgcLowOverheadCapturePathAsync()
    {
        var configuration = new CaptureConfiguration(
            new DisplayOption(@"\\.\DISPLAY1", "Primary display", 0, 0, 2560, 1440, true, 0),
            ResolutionOption.All.Single(option => option.Id == "1080p"),
            60,
            TimeSpan.FromMinutes(2),
            false,
            false,
            null,
            false,
            null,
            @"C:\Clips");
        var strategy = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.WindowsGraphicsCapture);

        var arguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            strategy,
            @"C:\Buffer");
        var captureFilter = GetArgumentAfter(arguments, "-i") ?? string.Empty;

        Assert.True(
            captureFilter.Contains(
                ":width=1920:height=1080:resize_mode=scale:scale_mode=point",
                StringComparison.Ordinal),
            "Fixed-resolution WGC capture must use the low-overhead point scaler.");
        Assert.True(
            !captureFilter.Contains("scale_mode=bilinear", StringComparison.Ordinal),
            "The live WGC path must not reintroduce the expensive bilinear sampler.");
        Assert.True(
            captureFilter.Split(',').All(part =>
                !part.StartsWith("fps=", StringComparison.OrdinalIgnoreCase)),
            "WGC must not synthesize duplicate frames through a lavfi fps filter.");
        Assert.True(
            captureFilter.Contains(":max_framerate=60", StringComparison.Ordinal),
            "WGC should still request the configured maximum capture rate at the source.");
        Assert.True(
            captureFilter.Contains(":capture_cursor=0", StringComparison.Ordinal),
            "Cursor-free WGC capture must disable WinRT cursor composition exactly.");
        Assert.ContainsSequence(
            arguments,
            "-filter_threads", "1",
            "-filter_complex_threads", "1",
            "-thread_queue_size", "4",
            "-f", "lavfi");
        Assert.Equal(
            1,
            arguments.Count(argument => argument == "-filter_threads"),
            "Direct WGC hardware capture must create one bounded simple-filter pool.");
        Assert.Equal(
            1,
            arguments.Count(argument => argument == "-filter_complex_threads"),
            "Direct WGC hardware capture must create one bounded complex-filter pool.");
        Assert.ContainsSequence(arguments, "-fps_mode", "cfr", "-r", "60");
        Assert.ContainsSequence(arguments, "-stats_period", "0.25");

        var sourceConfiguration = configuration with
        {
            Resolution = ResolutionOption.All.Single(option => option.Id == "source")
        };
        var lowImpactSourceArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            sourceConfiguration,
            [],
            strategy,
            @"C:\Buffer",
            performanceProfile: CapturePerformanceProfile.LowImpact);
        Assert.ContainsSequence(
            lowImpactSourceArguments,
            "-thread_queue_size", "2",
            "-f", "lavfi");
        var resilientSourceArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            sourceConfiguration,
            [],
            strategy,
            @"C:\Buffer",
            performanceProfile: CapturePerformanceProfile.Resilient);
        Assert.ContainsSequence(
            resilientSourceArguments,
            "-thread_queue_size", "4",
            "-f", "lavfi");
        Assert.Equal(
            ProcessPriorityClass.Normal,
            ProcessTuning.GetCaptureCpuPriority(
                strategy,
                captureOutputRequiresScaling: false,
                CapturePerformanceProfile.Resilient),
            "A Source session promoted after measured pressure must use resilient CPU scheduling.");
        Assert.Equal(
            GraphicsSchedulingPriorityClass.Normal,
            ProcessTuning.GetCaptureGraphicsPriority(
                strategy,
                captureOutputRequiresScaling: false,
                CapturePerformanceProfile.Resilient),
            "A Source session promoted after measured pressure must use resilient GPU scheduling.");

        var sourceProbeArguments =
            FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
                sourceConfiguration,
                strategy);
        Assert.ContainsSequence(
            sourceProbeArguments,
            "-fps_mode", "cfr",
            "-r", "60",
            "-frames:v", "180");
        Assert.True(
            !FfmpegProbeRunner.IsProbeCadenceAcceptable(
                sourceProbeArguments,
                new FfmpegProbeCadenceObservation(
                    FirstFrame: 1,
                    FirstFrameElapsed: TimeSpan.FromSeconds(0.1),
                    LastFrame: 180,
                    LastFrameElapsed: TimeSpan.FromSeconds(6.1)),
                out var sourceProbeDiagnostic) &&
            sourceProbeDiagnostic.Contains("required minimum", StringComparison.Ordinal),
            "A Source WGC path sustaining only about 30 FPS must not pass capability selection.");
        Assert.True(
            FfmpegProbeRunner.IsProbeCadenceAcceptable(
                sourceProbeArguments,
                new FfmpegProbeCadenceObservation(
                    FirstFrame: 1,
                    FirstFrameElapsed: TimeSpan.Zero,
                    LastFrame: 180,
                    LastFrameElapsed: TimeSpan.FromSeconds(3)),
                out _),
            "A Source WGC path sustaining about 60 FPS was rejected.");
        var resilientSourceProbeArguments =
            FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
                sourceConfiguration,
                strategy,
                CapturePerformanceProfile.Resilient);
        Assert.ContainsSequence(
            resilientSourceProbeArguments,
            "-thread_queue_size", "4",
            "-f", "lavfi");

        var cursorArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration with { CaptureCursor = true },
            [],
            strategy,
            @"C:\Buffer");
        Assert.True(
            (GetArgumentAfter(cursorArguments, "-i") ?? string.Empty)
                .Contains(":capture_cursor=1", StringComparison.Ordinal),
            "Cursor-enabled WGC capture must opt in to WinRT cursor composition exactly.");

        var transferArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            strategy with { RequiresSystemMemoryTransfer = true },
            @"C:\Buffer");
        Assert.ContainsSequence(
            transferArguments,
            "-thread_queue_size", "4",
            "-f", "lavfi");
        Assert.True(
            !transferArguments.Contains("-filter_threads", StringComparer.Ordinal) &&
            !transferArguments.Contains("-filter_complex_threads", StringComparer.Ordinal),
            "Compatibility-transfer WGC must not inherit the direct hardware filter-pool policy.");
        var hardwareGdiArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            new VideoEncodingStrategy(
                VideoEncoderKind.NvidiaNvenc,
                DesktopCaptureBackend.Gdi),
            @"C:\Buffer");
        Assert.True(
            !hardwareGdiArguments.Contains("-filter_threads", StringComparer.Ordinal) &&
            !hardwareGdiArguments.Contains("-filter_complex_threads", StringComparer.Ordinal),
            "Hardware-encoded GDI capture must not inherit the direct WGC filter-pool policy.");

        return Task.CompletedTask;
    }

    private static Task TestWgcRefreshRateSamplingPolicyAsync()
    {
        (int OutputFps, int RefreshRateHz, int ExpectedInputFps)[] cases =
        [
            (30, 0, 30),
            (30, 23, 30),
            (30, 30, 31),
            (30, 59, 60),
            (30, 60, 31),
            (30, 75, 39),
            (30, 120, 31),
            (30, 144, 37),
            (30, 165, 34),
            (30, 240, 31),
            (30, 1000, 32),
            (30, 1001, 30),
            (60, 0, 60),
            (60, 23, 60),
            (60, 24, 60),
            (60, 59, 60),
            (60, 60, 61),
            (60, 75, 76),
            (60, 100, 101),
            (60, 119, 120),
            (60, 120, 61),
            (60, 121, 62),
            (60, 144, 73),
            (60, 165, 84),
            (60, 180, 61),
            (60, 200, 68),
            (60, 240, 61),
            (60, 360, 61),
            (60, 1000, 64),
            (60, 1001, 60),
            (120, 60, 120),
            (120, 120, 121),
            (120, 144, 145),
            (120, 165, 166),
            (120, 240, 121),
            (120, 360, 121),
            (120, 1000, 126),
            (240, 120, 240),
            (240, 240, 241),
            (240, 360, 361),
            (240, 1000, 251)
        ];

        foreach (var (outputFps, refreshRateHz, expectedInputFps) in cases)
        {
            Assert.Equal(
                expectedInputFps,
                FfmpegArgumentBuilder.ResolveGraphicsCaptureInputFrameRate(
                    outputFps,
                    refreshRateHz),
                $"WGC sampling resolved the wrong input rate for {outputFps} FPS at {refreshRateHz} Hz.");
        }

        foreach (var outputFps in new[] { 30, 60, 120, 240 })
        {
            for (var refreshRateHz = 24; refreshRateHz <= 1000; refreshRateHz++)
            {
                var inputFps = FfmpegArgumentBuilder.ResolveGraphicsCaptureInputFrameRate(
                    outputFps,
                    refreshRateHz);
                Assert.True(
                    inputFps >= outputFps &&
                    inputFps <= Math.Min(1000, outputFps * 2),
                    $"WGC sampling escaped its bounded work budget for {outputFps} FPS at {refreshRateHz} Hz.");

                if (refreshRateHz >= outputFps)
                {
                    var admittedCadence = refreshRateHz /
                        Math.Ceiling(refreshRateHz / (double)inputFps);
                    Assert.True(
                        admittedCadence >= outputFps,
                        $"WGC sampling would undersupply {outputFps} FPS at {refreshRateHz} Hz (resolved {inputFps}).");
                }
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegArgumentBuilder.ResolveGraphicsCaptureInputFrameRate(0, 60),
            "WGC sampling accepted a zero output frame rate.");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegArgumentBuilder.ResolveGraphicsCaptureInputFrameRate(241, 360),
            "WGC sampling accepted an output frame rate above the supported maximum.");

        var knownRefreshDisplay = new DisplayOption(
            @"\\.\DISPLAY1",
            "Primary display",
            0,
            0,
            2560,
            1440,
            true,
            0,
            165);
        var unknownRefreshDisplay = knownRefreshDisplay with { RefreshRateHz = 0 };
        Assert.Equal(
            "Primary display \u00B7 2560\u00D71440 @ 165 Hz",
            knownRefreshDisplay.ToString(),
            "Display labels must expose the refresh rate used by WGC sampling.");
        Assert.Equal(
            "Primary display \u00B7 2560\u00D71440",
            unknownRefreshDisplay.ToString(),
            "Display labels must omit unknown refresh-rate metadata.");

        var configuration = new CaptureConfiguration(
            knownRefreshDisplay,
            ResolutionOption.All.Single(option => option.Id == "1080p"),
            60,
            TimeSpan.FromMinutes(2),
            false,
            false,
            null,
            false,
            null,
            @"C:\Clips");
        var strategy = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.WindowsGraphicsCapture);
        var liveArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration,
            [],
            strategy,
            @"C:\Buffer");
        var probeArguments = FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
            configuration,
            strategy);
        Assert.True(
            (GetArgumentAfter(liveArguments, "-i") ?? string.Empty)
                .Contains(":max_framerate=84", StringComparison.Ordinal) &&
            (GetArgumentAfter(probeArguments, "-i") ?? string.Empty)
                .Contains(":max_framerate=84", StringComparison.Ordinal),
            "Live capture and its capability probe must share the divisor-aware 165 Hz sampling rate.");
        Assert.ContainsSequence(liveArguments, "-fps_mode", "cfr", "-r", "60");
        Assert.ContainsSequence(probeArguments, "-fps_mode", "cfr", "-r", "60");

        var unknownRefreshArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration with { Display = unknownRefreshDisplay },
            [],
            strategy,
            @"C:\Buffer");
        Assert.True(
            (GetArgumentAfter(unknownRefreshArguments, "-i") ?? string.Empty)
                .Contains(":max_framerate=60", StringComparison.Ordinal),
            "An unknown display refresh rate must preserve the proven low-overhead target-rate fallback.");

        return Task.CompletedTask;
    }

    private static Task TestRecorderLockedOutputGeometryAsync()
    {
        var sourceResolution = ResolutionOption.All.Single(option => option.Id == "source");
        var strategy = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.WindowsGraphicsCapture);
        var initialDisplay = new DisplayOption(
            @"\\.\DISPLAY1",
            "Recorder display",
            0,
            0,
            1920,
            1080,
            true,
            0,
            165);
        var recorderConfiguration = new CaptureConfiguration(
            initialDisplay,
            sourceResolution,
            60,
            TimeSpan.FromMinutes(2),
            false,
            false,
            null,
            false,
            null,
            @"C:\Clips")
        {
            SessionMode = CaptureSessionMode.Recording,
            LockOutputGeometry = true,
            LockedOutputWidth = 1920,
            LockedOutputHeight = 1080
        };

        var initialArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            recorderConfiguration,
            [],
            strategy,
            @"C:\Buffer");
        var initialProbeArguments =
            FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
                recorderConfiguration,
                strategy);
        var initialFilter = GetArgumentAfter(initialArguments, "-i") ?? string.Empty;
        var initialProbeFilter =
            GetArgumentAfter(initialProbeArguments, "-i") ?? string.Empty;
        const string nativeGeometry =
            ":width=-2:height=-2:resize_mode=crop:scale_mode=point";
        Assert.True(
            initialFilter.Contains(nativeGeometry, StringComparison.Ordinal) &&
            initialProbeFilter.Contains(nativeGeometry, StringComparison.Ordinal),
            "A Recorder whose locked canvas matches Source/native must keep WGC on its no-resize path.");
        Assert.True(
            !initialFilter.Contains("resize_mode=scale", StringComparison.Ordinal),
            "An initially matching Recorder canvas must not pay for a per-frame WGC scaler.");
        Assert.Equal(
            FfmpegArgumentBuilder.VideoInputQueuePackets.ToString(),
            GetArgumentAfter(initialArguments, "-thread_queue_size"),
            "An initially matching Recorder canvas must retain the two-packet low-impact queue.");
        Assert.Equal(
            FfmpegArgumentBuilder.VideoInputQueuePackets.ToString(),
            GetArgumentAfter(initialProbeArguments, "-thread_queue_size"),
            "The initial Recorder probe must exercise the same low-impact queue as live capture.");

        var renewedConfiguration = recorderConfiguration with
        {
            Display = initialDisplay with
            {
                Width = 1280,
                Height = 960,
                RefreshRateHz = 144
            }
        };
        var renewedArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
            renewedConfiguration,
            [],
            strategy,
            @"C:\Buffer");
        var renewedProbeArguments =
            FfmpegArgumentBuilder.BuildGraphicsCaptureProbeArguments(
                renewedConfiguration,
                strategy);
        var renewedFilter = GetArgumentAfter(renewedArguments, "-i") ?? string.Empty;
        var renewedProbeFilter =
            GetArgumentAfter(renewedProbeArguments, "-i") ?? string.Empty;
        const string lockedGeometry =
            ":width=1920:height=1080:resize_mode=scale:scale_mode=point";
        Assert.True(
            renewedFilter.Contains(lockedGeometry, StringComparison.Ordinal) &&
            renewedProbeFilter.Contains(lockedGeometry, StringComparison.Ordinal),
            "A renewed 1280x960 custom-mode capture must retain the Recorder's original 1920x1080 canvas.");
        Assert.True(
            !renewedFilter.Contains("resize_mode=crop", StringComparison.Ordinal) &&
            !renewedFilter.Contains("scale_mode=bilinear", StringComparison.Ordinal) &&
            !renewedFilter.Contains("resize_mode=scale_aspect", StringComparison.Ordinal),
            "Recorder renewal must use the bounded point scaler without crop, bilinear, or padding paths.");
        Assert.Equal(
            FfmpegArgumentBuilder.ScaledVideoInputQueuePackets.ToString(),
            GetArgumentAfter(renewedArguments, "-thread_queue_size"),
            "A renewed custom-mode Recorder capture must reserve the scaled-path WGC input queue.");
        Assert.Equal(
            FfmpegArgumentBuilder.ScaledVideoInputQueuePackets.ToString(),
            GetArgumentAfter(renewedProbeArguments, "-thread_queue_size"),
            "The renewed Recorder probe must exercise the same scaled-path WGC queue as live capture.");
        Assert.ContainsSequence(initialArguments, "-fps_mode", "cfr", "-r", "60");
        Assert.ContainsSequence(initialProbeArguments, "-fps_mode", "cfr", "-r", "60");
        Assert.ContainsSequence(renewedArguments, "-fps_mode", "cfr", "-r", "60");
        Assert.ContainsSequence(renewedProbeArguments, "-fps_mode", "cfr", "-r", "60");

        return Task.CompletedTask;
    }

    private static async Task TestCaptureProcessJobLifetimeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (var currentProcess = Process.GetCurrentProcess())
        {
            Assert.Throws<InvalidOperationException>(
                () => CaptureProcessJob.Attach(currentProcess),
                "The job helper must refuse to attach the ClipForge process itself.");
        }

        Assert.True(
            CaptureProcessJob.IsBenignExitedProcessAttachFailure(
                new InvalidOperationException("process exited during attachment"),
                processHasExited: true) &&
            CaptureProcessJob.IsBenignExitedProcessAttachFailure(
                new System.ComponentModel.Win32Exception(6),
                processHasExited: true),
            "Expected process-exit races must be benign for both supported ownership failures.");
        Assert.True(
            !CaptureProcessJob.IsBenignExitedProcessAttachFailure(
                new InvalidOperationException("ownership failed while process remained alive"),
                processHasExited: false) &&
            !CaptureProcessJob.IsBenignExitedProcessAttachFailure(
                new IOException("unexpected ownership failure"),
                processHasExited: true),
            "Live-process or unexpected ownership failures must remain fatal.");

        var pingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "ping.exe");
        Assert.True(File.Exists(pingPath), "The Windows ping helper was not found for the job-object test.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pingPath,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add("127.0.0.1");

        Assert.True(process.Start(), "The isolated job-object test process did not start.");
        try
        {
            using var job = CaptureProcessJob.Attach(process);
            await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            Assert.True(
                !process.HasExited,
                "The isolated job-object test process exited before ownership was exercised.");

            job.Dispose();
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.True(
                process.HasExited,
                "Closing the capture job did not terminate its owned process.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
        }

        var pipeName = $"ClipForge-JobCrash-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var helper = CreateCaptureJobCrashHelperProcess(pipeName);
        Assert.True(helper.Start(), "The isolated crash-owner helper did not start.");

        int ownedPingProcessId = 0;
        Process? ownedPing = null;
        try
        {
            using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(server, leaveOpen: true);
            var processIdLine = await reader.ReadLineAsync(connectionTimeout.Token).ConfigureAwait(false);
            Assert.True(
                int.TryParse(processIdLine, CultureInfo.InvariantCulture, out ownedPingProcessId),
                "The crash-owner helper did not report its owned process ID.");

            ownedPing = Process.GetProcessById(ownedPingProcessId);
            Assert.True(
                string.Equals(ownedPing.ProcessName, "PING", StringComparison.OrdinalIgnoreCase) &&
                !ownedPing.HasExited,
                "The crash-owner helper did not create the expected isolated ping process.");

            // Kill only the helper, not its process tree. The ping process must
            // exit because Windows closes the helper's last Job Object handle,
            // which models abrupt ClipForge termination rather than Dispose().
            helper.Kill(entireProcessTree: false);
            await helper.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await ownedPing.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.True(
                ownedPing.HasExited,
                "Abrupt owner termination left its Job-owned process running.");
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: false);
                await helper.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }

            if (ownedPing is not null)
            {
                if (!ownedPing.HasExited &&
                    string.Equals(ownedPing.ProcessName, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    ownedPing.Kill(entireProcessTree: true);
                    await ownedPing.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }

                ownedPing.Dispose();
            }
        }
    }

    private static Process CreateCaptureJobCrashHelperProcess(string pipeName)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test executable path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(executable),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(
                System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The test assembly path is unavailable."));
        }

        startInfo.ArgumentList.Add(CaptureJobCrashHelperArgument);
        startInfo.ArgumentList.Add(pipeName);
        return new Process { StartInfo = startInfo };
    }

    private static async Task<int> RunCaptureJobCrashHelperAsync(string pipeName)
    {
        var pingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "ping.exe");
        using var ping = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pingPath,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        ping.StartInfo.ArgumentList.Add("-t");
        ping.StartInfo.ArgumentList.Add("127.0.0.1");
        if (!ping.Start())
        {
            return 2;
        }

        using var job = CaptureProcessJob.Attach(ping);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
        await using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync(
                ping.Id.ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task TestFfmpegProbeCrashContainmentAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = $"ClipForge-ProbeJobCrash-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var helper = CreateProbeJobCrashHelperProcess(pipeName);
        Assert.True(helper.Start(), "The isolated FFmpeg probe owner did not start.");

        Process? ownedProbe = null;
        try
        {
            using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(server, leaveOpen: true);
            var processIdLine = await reader.ReadLineAsync(connectionTimeout.Token).ConfigureAwait(false);
            Assert.True(
                int.TryParse(processIdLine, CultureInfo.InvariantCulture, out var processId),
                "The FFmpeg probe owner did not report its child process ID.");

            ownedProbe = Process.GetProcessById(processId);
            Assert.True(
                string.Equals(ownedProbe.ProcessName, "PING", StringComparison.OrdinalIgnoreCase) &&
                !ownedProbe.HasExited,
                "The FFmpeg probe owner did not create the expected isolated process.");

            // Kill only the probe owner. The child must exit because the
            // FfmpegProbeRunner's Job Object handle closes with its process.
            helper.Kill(entireProcessTree: false);
            await helper.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await ownedProbe.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.True(
                ownedProbe.HasExited,
                "Abrupt ClipForge termination left an FFmpeg capability probe running.");
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: false);
                await helper.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }

            if (ownedProbe is not null)
            {
                if (!ownedProbe.HasExited &&
                    string.Equals(ownedProbe.ProcessName, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    ownedProbe.Kill(entireProcessTree: true);
                    await ownedProbe.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }

                ownedProbe.Dispose();
            }
        }
    }

    private static Process CreateProbeJobCrashHelperProcess(string pipeName)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test executable path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(executable),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(
                System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The test assembly path is unavailable."));
        }

        startInfo.ArgumentList.Add(ProbeJobCrashHelperArgument);
        startInfo.ArgumentList.Add(pipeName);
        return new Process { StartInfo = startInfo };
    }

    private static async Task<int> RunProbeJobCrashHelperAsync(string pipeName)
    {
        var pingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "ping.exe");
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
        await using var writer = new StreamWriter(client) { AutoFlush = true };
        var runner = new FfmpegProbeRunner(processId =>
            writer.WriteLine(processId.ToString(CultureInfo.InvariantCulture)));
        var execution = await runner.RunAsync(
                pingPath,
                ["-t", "127.0.0.1"],
                CancellationToken.None)
            .ConfigureAwait(false);
        return execution.Succeeded ? 0 : 2;
    }

    private static Task TestCaptureProgressParserAsync()
    {
        var parser = new CaptureProgressParser();
        Assert.True(
            !parser.TryParse(null, 1, out _),
            "A null FFmpeg progress line must be ignored.");
        Assert.True(
            !parser.TryParse("malformed", 2, out _),
            "A malformed FFmpeg progress line must be ignored.");
        Assert.True(
            !parser.TryParse("frame=120", 3, out _),
            "A partial progress record must not be emitted.");
        Assert.True(
            !parser.TryParse("unknown_counter=999", 4, out _),
            "Unknown numeric progress keys must be ignored.");
        Assert.True(
            !parser.TryParse("speed=1.0x", 5, out _),
            "Unknown non-numeric progress keys must be ignored.");
        Assert.True(
            !parser.TryParse("frame=-1", 6, out _),
            "Negative counters must not replace a valid partial value.");
        _ = parser.TryParse("dup_frames=116", 7, out _);
        _ = parser.TryParse("drop_frames=2", 8, out _);
        _ = parser.TryParse("out_time_us=2000000", 9, out _);

        Assert.True(
            parser.TryParse("progress=continue", 10, out var sample),
            "A complete FFmpeg progress record was not emitted at its boundary.");
        Assert.True(sample is not null, "The progress parser returned a null completed record.");
        Assert.Equal(120L, sample!.Frame, "The parsed frame counter is incorrect.");
        Assert.Equal(116L, sample.DuplicatedFrames, "The parsed duplicate counter is incorrect.");
        Assert.Equal(2L, sample.DroppedFrames, "The parsed dropped-frame counter is incorrect.");
        Assert.Equal(2_000_000L, sample.OutputTimeMicroseconds, "The parsed output time is incorrect.");
        Assert.Equal(10L, sample.Timestamp, "The completed record must use its boundary timestamp.");

        _ = parser.TryParse("frame=240", 11, out _);
        _ = parser.TryParse("dup_frames=230", 12, out _);
        Assert.True(
            !parser.TryParse("progress=continue", 13, out _),
            "A record missing out_time_us must not be emitted.");
        _ = parser.TryParse("dup_frames=231", 14, out _);
        _ = parser.TryParse("out_time_us=4000000", 15, out _);
        Assert.True(
            !parser.TryParse("progress=end", 16, out _),
            "A partial record boundary must clear prior fields rather than leak them forward.");

        return Task.CompletedTask;
    }

    private static Task TestCaptureStarvationWatchdogAsync()
    {
        var fullscreenRecent = new CaptureForegroundContext(
            IsFullscreenOnCapturedDisplay: true,
            HasRecentInput: true);
        var fullscreenIdle = fullscreenRecent with { HasRecentInput = false };
        var windowedRecent = fullscreenRecent with { IsFullscreenOnCapturedDisplay = false };
        Assert.True(
            CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.60,
                monitorMatches: true,
                isBorderless: true,
                isAnchoredToMonitor: true,
                foregroundAspectRatio: 4d / 3,
                monitorAspectRatio: 16d / 9),
            "A stretched borderless game anchored to the captured monitor was not recognized.");
        Assert.True(
            !CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.60,
                monitorMatches: true,
                isBorderless: false,
                isAnchoredToMonitor: true,
                foregroundAspectRatio: 4d / 3,
                monitorAspectRatio: 16d / 9) &&
            !CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.60,
                monitorMatches: false,
                isBorderless: true,
                isAnchoredToMonitor: true,
                foregroundAspectRatio: 4d / 3,
                monitorAspectRatio: 16d / 9) &&
            !CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.44,
                monitorMatches: true,
                isBorderless: true,
                isAnchoredToMonitor: true,
                foregroundAspectRatio: 16d / 9,
                monitorAspectRatio: 16d / 9) &&
            !CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.44,
                monitorMatches: true,
                isBorderless: true,
                isAnchoredToMonitor: true,
                foregroundAspectRatio: 4d / 3,
                monitorAspectRatio: 16d / 9),
            "An ordinary native-aspect or 4:3 partial window was mistaken for a custom fullscreen game.");
        Assert.True(
            CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.50,
                monitorMatches: true,
                isBorderless: true,
                isAnchoredToMonitor: true,
                foregroundAspectRatio: 4d / 3,
                monitorAspectRatio: 16d / 9) &&
            !CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.49,
                monitorMatches: true,
                isBorderless: true,
                isAnchoredToMonitor: true,
                foregroundAspectRatio: 4d / 3,
                monitorAspectRatio: 16d / 9) &&
            !CaptureForegroundContextProbe.IsFullscreenCandidate(
                capturedDisplayCoverage: 0.72,
                monitorMatches: true,
                isBorderless: true,
                isAnchoredToMonitor: false,
                foregroundAspectRatio: 4d / 3,
                monitorAspectRatio: 16d / 9),
            "The custom-fullscreen boundary admitted an undersized or unanchored window.");

        var healthy = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
        {
            var assessment = healthy.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: second),
                fullscreenRecent);
            Assert.True(
                assessment is null,
                "Healthy fullscreen capture must not trigger starvation recovery.");
        }

        var windowed = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
        {
            var assessment = windowed.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                windowedRecent);
            Assert.True(
                assessment is null,
                "Severe duplicates outside fullscreen must not trigger starvation recovery.");
        }

        var transient = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
        {
            var duplicatedFrames = second <= 4
                ? 58L * second
                : 58L * 4;
            var assessment = transient.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames),
                fullscreenRecent);
            Assert.True(
                assessment is null,
                "A short duplicate burst followed by healthy frames must not trigger recovery.");
        }

        var severeRecent = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? recentAssessment = null;
        for (var second = 0; second <= 8; second++)
        {
            recentAssessment = severeRecent.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                fullscreenRecent);
        }

        Assert.True(
            recentAssessment is not null,
            "A sustained 97% duplicate fullscreen stream with recent input must trigger recovery.");
        Assert.True(
            recentAssessment!.DuplicateRatio >= 0.96,
            "The starvation assessment reported an unexpectedly low duplicate ratio.");
        Assert.True(
            recentAssessment.UniqueFramesPerSecond <= 2.1,
            "The starvation assessment reported too many unique frames.");
        Assert.True(
            severeRecent.Observe(
                CreateProgressSample(9, frame: 540, duplicatedFrames: 522),
                fullscreenRecent) is null,
            "A watchdog must emit at most one recovery request per capture session.");

        var severeIdle = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 30; second++)
        {
            Assert.True(
                severeIdle.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                    fullscreenIdle) is null,
                "A fully idle/static fullscreen stream must not trigger destructive recovery.");
        }

        var singleInputPulse = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 16; second++)
        {
            var context = second <= 5 ? fullscreenRecent : fullscreenIdle;
            Assert.True(
                singleInputPulse.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                    context) is null,
                "One input pulse whose recent-input flag decays after five seconds must not trigger recovery.");
        }

        var transientFullscreenMiss = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? transientFullscreenAssessment = null;
        for (var second = 0; second <= 8; second++)
        {
            var context = second == 3 ? windowedRecent : fullscreenRecent;
            transientFullscreenAssessment = transientFullscreenMiss.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                context);
        }

        Assert.True(
            transientFullscreenAssessment is not null,
            "One transient fullscreen probe miss must not hide sustained active starvation.");

        var insufficientFullscreenCoverage = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 8; second++)
        {
            var context = second is 2 or 4 or 6 ? windowedRecent : fullscreenRecent;
            Assert.True(
                insufficientFullscreenCoverage.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                    context) is null,
                "A candidate with less than 75% fullscreen coverage must not trigger recovery.");
        }

        var fullscreenReset = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 7; second++)
        {
            _ = fullscreenReset.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                fullscreenRecent);
        }

        for (var second = 8; second <= 12; second++)
        {
            Assert.True(
                fullscreenReset.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                    windowedRecent) is null,
                "A sustained fullscreen eligibility loss must not trigger recovery.");
        }

        for (var second = 13; second <= 20; second++)
        {
            Assert.True(
                fullscreenReset.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 58L * second),
                    fullscreenRecent) is null,
                "Returning after a sustained eligibility loss must start a fresh confirmation window.");
        }

        Assert.True(
            fullscreenReset.Observe(
                CreateProgressSample(21, frame: 1_260, duplicatedFrames: 1_218),
                fullscreenRecent) is not null,
            "A fresh sustained fullscreen starvation window should still trigger recovery.");

        var youngModerate = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 24; second++)
        {
            Assert.True(
                youngModerate.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 44L * second),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(second)) is null,
                "Moderate CFR duplication must not trigger before the long-session guard becomes eligible.");
        }

        var alwaysSixteenFramesPerSecond = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 40; second++)
        {
            Assert.True(
                alwaysSixteenFramesPerSecond.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 44L * second),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second)) is null,
                "A capture that has produced 16 meaningful FPS since process start must not establish " +
                "a healthy baseline or trigger moderate recovery.");
        }

        var agedModerate = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? moderateAssessment = null;
        for (var second = 0; second <= 12; second++)
        {
            Assert.True(
                agedModerate.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 0),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(second)) is null,
                "Healthy active capture must only arm, never trigger, the moderate-degradation path.");
        }

        for (var second = 13; second <= 33; second++)
        {
            var duplicatedFrames = 44L * (second - 12);
            moderateAssessment ??= agedModerate.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames),
                fullscreenRecent,
                captureUptime: TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(second));
        }

        Assert.True(
            moderateAssessment is not null,
            "An aged 60 FPS session degraded to about 16 meaningful FPS must trigger recovery.");
        Assert.True(
            moderateAssessment!.DuplicateRatio >= 0.73,
            "The moderate starvation assessment reported an unexpectedly low duplicate ratio.");
        Assert.True(
            moderateAssessment.UniqueFramesPerSecond <= 16.1,
            "The moderate starvation assessment reported too many meaningful frames.");

        var counterRollback = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
        {
            _ = counterRollback.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 0),
                fullscreenRecent,
                captureUptime: TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(second));
        }

        // A restarted/replaced FFmpeg process resets its cumulative counters.
        // That rollback must also clear the healthy latch from the old process.
        Assert.True(
            counterRollback.Observe(
                CreateProgressSample(13, frame: 0, duplicatedFrames: 0),
                fullscreenRecent,
                captureUptime: TimeSpan.FromMinutes(6)) is null,
            "A progress-counter rollback must not trigger recovery.");
        for (var second = 14; second <= 40; second++)
        {
            var elapsedSinceRollback = second - 13;
            Assert.True(
                counterRollback.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * elapsedSinceRollback,
                        duplicatedFrames: 44L * elapsedSinceRollback),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(elapsedSinceRollback)) is null,
                "A counter rollback must clear the prior process's healthy-cadence latch.");
        }

        var healthyThenAltTabThenLowCadence = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
        {
            _ = healthyThenAltTabThenLowCadence.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 0),
                fullscreenRecent,
                captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second),
                allowChronicLowCadence: true);
        }

        for (var second = 13; second <= 17; second++)
        {
            Assert.True(
                healthyThenAltTabThenLowCadence.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 0),
                    windowedRecent,
                    captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second),
                    allowChronicLowCadence: true) is null,
                "Leaving fullscreen must not trigger capture recovery.");
        }

        for (var second = 18; second <= 45; second++)
        {
            var lowCadenceSeconds = second - 17;
            Assert.True(
                healthyThenAltTabThenLowCadence.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 44L * lowCadenceSeconds),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second),
                    allowChronicLowCadence: true) is null,
                "A game that was previously healthy must not be reclassified as startup-low cadence after an alt-tab.");
        }

        var healthyThenSeventeenFramesPerSecond = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
        {
            _ = healthyThenSeventeenFramesPerSecond.Observe(
                CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 0),
                fullscreenRecent,
                captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second));
        }

        for (var second = 13; second <= 38; second++)
        {
            Assert.True(
                healthyThenSeventeenFramesPerSecond.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 43L * (second - 12)),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second)) is null,
                "Seventeen meaningful FPS must remain just above the 60 FPS moderate-recovery boundary.");
        }

        var targetThirtyHealthyThenEight = new CaptureStarvationWatchdog(30);
        CaptureStarvationAssessment? targetThirtyAssessment = null;
        for (var second = 0; second <= 12; second++)
        {
            _ = targetThirtyHealthyThenEight.Observe(
                CreateProgressSample(second, frame: 30L * second, duplicatedFrames: 0),
                fullscreenRecent,
                captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second));
        }

        for (var second = 13; second <= 33; second++)
        {
            targetThirtyAssessment ??= targetThirtyHealthyThenEight.Observe(
                CreateProgressSample(
                    second,
                    frame: 30L * second,
                    duplicatedFrames: 22L * (second - 12)),
                fullscreenRecent,
                captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second));
        }

        Assert.True(
            targetThirtyAssessment is not null &&
            targetThirtyAssessment.UniqueFramesPerSecond <= 8.1,
            "A healthy 30 FPS process that degrades to eight meaningful FPS must trigger moderate recovery.");

        foreach (var uniqueFramesPerSecond in new[] { 24, 30 })
        {
            var legitimateLowerCadence = new CaptureStarvationWatchdog(60);
            for (var second = 0; second <= 24; second++)
            {
                var duplicatedFrames = (60L - uniqueFramesPerSecond) * second;
                Assert.True(
                    legitimateLowerCadence.Observe(
                        CreateProgressSample(second, frame: 60L * second, duplicatedFrames),
                        fullscreenRecent,
                        captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second)) is null,
                    $"Legitimate {uniqueFramesPerSecond} FPS content must not be treated as an aged WGC failure.");
            }
        }

        foreach (var uniqueFramesPerSecond in new[] { 16, 24, 30 })
        {
            var legitimateLowImpactCadence = new CaptureStarvationWatchdog(60);
            for (var second = 0; second <= 24; second++)
            {
                var duplicatedFrames =
                    (60L - uniqueFramesPerSecond) * second;
                Assert.True(
                    legitimateLowImpactCadence.Observe(
                        CreateProgressSample(
                            second,
                            frame: 60L * second,
                            duplicatedFrames),
                        fullscreenRecent,
                        captureUptime:
                            TimeSpan.FromHours(2) +
                            TimeSpan.FromSeconds(second),
                        allowSchedulingPressure: true) is null,
                    $"A stable {uniqueFramesPerSecond} FPS game was mistaken for capture scheduling pressure.");
            }
        }

        var chronicLowImpactCadence = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? chronicAssessment = null;
        for (var second = 0; second <= 12; second++)
        {
            chronicAssessment ??= chronicLowImpactCadence.Observe(
                CreateProgressSample(
                    second,
                    frame: 60L * second,
                    duplicatedFrames: 30L * second),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(second),
                allowChronicLowCadence: true);
        }

        Assert.True(
            chronicAssessment is
            {
                Kind: CaptureStarvationKind.ChronicLowCadence
            },
            "Native LowImpact capture stuck near 30 FPS from startup did not request its one-way resilient promotion.");

        var healthyThenThirtyFramesPerSecond =
            new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
        {
            Assert.True(
                healthyThenThirtyFramesPerSecond.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 0),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowChronicLowCadence: true) is null,
                "Healthy initial cadence must arm, not trigger, recovery.");
        }

        for (var second = 13; second <= 32; second++)
        {
            Assert.True(
                healthyThenThirtyFramesPerSecond.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 30L * (second - 12)),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowChronicLowCadence: true) is null,
                "A later legitimate 30 FPS cap was mistaken for initial recorder pressure.");
        }

        var progressGap = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 8; second++)
        {
            Assert.True(
                progressGap.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 0),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowOutputThroughput: true) is null,
                "Healthy progress unexpectedly triggered gap recovery.");
        }

        var healthyTelemetryGap = progressGap.Observe(
            CreateProgressSample(
                seconds: 12,
                frame: 720,
                duplicatedFrames: 0),
            fullscreenRecent,
            captureUptime: TimeSpan.FromSeconds(12),
            allowOutputThroughput: true);
        Assert.True(
            healthyTelemetryGap is null,
            "A missing progress report invalidated capture even though frame and media counters advanced in real time.");

        var lowFpsTelemetryGap = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 8; second++)
        {
            Assert.True(
                lowFpsTelemetryGap.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 52L * second),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowOutputThroughput: true) is null,
                "Stable low-FPS content unexpectedly triggered objective throughput recovery.");
        }

        Assert.True(
            lowFpsTelemetryGap.Observe(
                CreateProgressSample(
                    seconds: 12,
                    frame: 720,
                    duplicatedFrames: 624),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(12),
                allowOutputThroughput: true) is null,
            "A telemetry gap over stable low-FPS content was mistaken for a stalled output graph.");

        var firstGapCandidate = progressGap.Observe(
            new CaptureProgressSample(
                Frame: 750,
                DuplicatedFrames: 0,
                DroppedFrames: 0,
                OutputTimeMicroseconds: 12_500_000,
                Timestamp: checked(16L * Stopwatch.Frequency)),
            fullscreenRecent,
            captureUptime: TimeSpan.FromSeconds(16),
            allowOutputThroughput: true);
        Assert.True(
            firstGapCandidate is null,
            "One delayed progress receipt was treated as an objective capture freeze before pipe catch-up could be observed.");
        var gapAssessment = progressGap.Observe(
            new CaptureProgressSample(
                Frame: 780,
                DuplicatedFrames: 0,
                DroppedFrames: 0,
                OutputTimeMicroseconds: 13_000_000,
                Timestamp: checked(20L * Stopwatch.Frequency)),
            fullscreenRecent,
            captureUptime: TimeSpan.FromSeconds(20),
            allowOutputThroughput: true);
        Assert.True(
            gapAssessment is
            {
                Kind: CaptureStarvationKind.ProgressGap,
                Window.TotalSeconds: >= 3.9
            },
            "A visible multi-second capture progress freeze escaped cadence recovery.");

        var parentPipeBacklog = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 8; second++)
        {
            Assert.True(
                parentPipeBacklog.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 0),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowOutputThroughput: true) is null,
                "Healthy pre-backlog progress unexpectedly triggered recovery.");
        }

        Assert.True(
            parentPipeBacklog.Observe(
                new CaptureProgressSample(
                    Frame: 495,
                    DuplicatedFrames: 0,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 8_250_000,
                    Timestamp: checked(12L * Stopwatch.Frequency)),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(12),
                allowOutputThroughput: true) is null,
            "The first stale progress block after a parent-process pause was treated as a capture fault.");
        Assert.True(
            parentPipeBacklog.Observe(
                new CaptureProgressSample(
                    Frame: 510,
                    DuplicatedFrames: 0,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 8_500_000,
                    Timestamp: checked(
                        12L * Stopwatch.Frequency +
                        Stopwatch.Frequency / 200)),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(12.005),
                allowOutputThroughput: true) is null,
            "A queued progress burst triggered recovery before its cumulative counters could catch up.");
        Assert.True(
            parentPipeBacklog.Observe(
                new CaptureProgressSample(
                    Frame: 720,
                    DuplicatedFrames: 0,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 12_000_000,
                    Timestamp: checked(
                        12L * Stopwatch.Frequency +
                        Stopwatch.Frequency / 100)),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(12.01),
                allowOutputThroughput: true) is null,
            "Queued FFmpeg progress that caught up immediately after the parent resumed still triggered recovery.");

        var longParentPipeBacklog =
            new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 8; second++)
        {
            Assert.True(
                longParentPipeBacklog.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 0),
                    fullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowOutputThroughput: true) is null,
                "Healthy progress before the long pipe backlog unexpectedly triggered recovery.");
        }

        Assert.True(
            longParentPipeBacklog.Observe(
                new CaptureProgressSample(
                    Frame: 495,
                    DuplicatedFrames: 0,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 8_250_000,
                    Timestamp: checked(32L * Stopwatch.Frequency)),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(32),
                allowOutputThroughput: true) is null,
            "The first stale record from a long bounded progress backlog triggered recovery.");
        for (var queuedSample = 1; queuedSample <= 80; queuedSample++)
        {
            Assert.True(
                longParentPipeBacklog.Observe(
                    new CaptureProgressSample(
                        Frame: 495L + 15L * queuedSample,
                        DuplicatedFrames: 0,
                        DroppedFrames: 0,
                        OutputTimeMicroseconds:
                            8_250_000L + 250_000L * queuedSample,
                        Timestamp: checked(
                            32L * Stopwatch.Frequency +
                            queuedSample *
                            (long)Stopwatch.Frequency / 1000)),
                    fullscreenRecent,
                    captureUptime:
                        TimeSpan.FromSeconds(
                            32 + queuedSample / 1000d),
                    allowOutputThroughput: true) is null,
                $"Queued progress sample {queuedSample} was classified before the bounded pipe backlog drained.");
        }

        Assert.True(
            longParentPipeBacklog.Observe(
                new CaptureProgressSample(
                    Frame: 1_926,
                    DuplicatedFrames: 0,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 32_100_000,
                    Timestamp: checked(
                        32L * Stopwatch.Frequency +
                        Stopwatch.Frequency / 10)),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(32.1),
                allowOutputThroughput: true) is null,
            "A fully caught-up long bounded progress backlog still triggered destructive recovery.");

        var agedModerateIdle = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 24; second++)
        {
            Assert.True(
                agedModerateIdle.Observe(
                    CreateProgressSample(second, frame: 60L * second, duplicatedFrames: 44L * second),
                    fullscreenIdle,
                    captureUptime: TimeSpan.FromHours(2) + TimeSpan.FromSeconds(second)) is null,
                "An aged but idle fullscreen scene must not trigger moderate recovery.");
        }

        var burstPressure = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? burstPressureAssessment = null;
        long burstDuplicates = 0;
        for (var quarter = 0; quarter <= 32; quarter++)
        {
            if (quarter is 8 or 9 or 16 or 17)
            {
                burstDuplicates += 15;
            }

            var timestamp = checked(
                quarter * (long)Stopwatch.Frequency / 4);
            burstPressureAssessment ??= burstPressure.Observe(
                new CaptureProgressSample(
                    Frame: 15L * quarter,
                    DuplicatedFrames: burstDuplicates,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 250_000L * quarter,
                    Timestamp: timestamp),
                fullscreenRecent,
                allowSchedulingPressure: true);
        }

        Assert.True(
            burstPressureAssessment is
            {
                Kind: CaptureStarvationKind.SchedulingPressure
            },
            "Short repeated Source stalls were hidden by one-second progress sampling.");

        var slowOutput = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? slowOutputAssessment = null;
        for (var second = 0; second <= 8; second++)
        {
            slowOutputAssessment ??= slowOutput.Observe(
                new CaptureProgressSample(
                    Frame: 30L * second,
                    DuplicatedFrames: 0,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 500_000L * second,
                    Timestamp: checked(second * (long)Stopwatch.Frequency)),
                fullscreenRecent,
                allowSchedulingPressure: true);
        }

        Assert.True(
            slowOutputAssessment is
            {
                Kind: CaptureStarvationKind.OutputThroughput,
                OutputSpeedRatio: < 0.85
            },
            "A Source graph running below real time did not request the resilient profile.");

        var duplicateAndSlowOutput = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? duplicateAndSlowAssessment = null;
        for (var second = 0; second <= 8; second++)
        {
            duplicateAndSlowAssessment ??= duplicateAndSlowOutput.Observe(
                new CaptureProgressSample(
                    Frame: 60L * second,
                    DuplicatedFrames: 58L * second,
                    DroppedFrames: 0,
                    OutputTimeMicroseconds: 500_000L * second,
                    Timestamp: checked(second * (long)Stopwatch.Frequency)),
                fullscreenRecent,
                captureUptime: TimeSpan.FromSeconds(second),
                allowSchedulingPressure: true,
                allowOutputThroughput: true);
        }

        Assert.True(
            duplicateAndSlowAssessment is
            {
                Kind: CaptureStarvationKind.OutputThroughput,
                OutputSpeedRatio: < 0.85
            },
            "Content duplication hid objective below-real-time output throughput.");

        var customFullscreenRecent =
            new CaptureForegroundContext(
                IsFullscreenOnCapturedDisplay: true,
                HasRecentInput: true,
                CapturedDisplayCoverage: 0.72,
                UsedCustomFullscreenFallback: true);
        var exactFullscreenRecent =
            new CaptureForegroundContext(
                IsFullscreenOnCapturedDisplay: true,
                HasRecentInput: true,
                CapturedDisplayCoverage: 1,
                UsedCustomFullscreenFallback: false);
        var customHistoryWithExactProbeFlicker =
            new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? flickerAssessment = null;
        for (var second = 0; second <= 8; second++)
        {
            flickerAssessment ??=
                customHistoryWithExactProbeFlicker.Observe(
                    CreateProgressSample(
                        second,
                        frame: 60L * second,
                        duplicatedFrames: 58L * second),
                    second == 8
                        ? exactFullscreenRecent
                        : customFullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowOutputThroughput: true,
                    allowSourceCadence: second == 8);
        }

        Assert.True(
            flickerAssessment is
            {
                Kind: CaptureStarvationKind.Severe,
                UsedCustomFullscreenFallback: true
            } &&
            ReplayBufferService.SelectSafeCadenceRecoveryReason(
                outputRequiresScaling: true,
                CapturePerformanceProfile.Resilient,
                flickerAssessment.Kind,
                flickerAssessment.UsedCustomFullscreenFallback) is null,
            "One exact-fullscreen probe flicker discarded the custom/stretched history and enabled destructive duplicate-only recovery.");

        var customObjectiveThroughput =
            new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? customObjectiveAssessment = null;
        for (var second = 0; second <= 8; second++)
        {
            customObjectiveAssessment ??=
                customObjectiveThroughput.Observe(
                    new CaptureProgressSample(
                        Frame: 30L * second,
                        DuplicatedFrames: 0,
                        DroppedFrames: 0,
                        OutputTimeMicroseconds:
                            500_000L * second,
                        Timestamp: checked(
                            second *
                            (long)Stopwatch.Frequency)),
                    customFullscreenRecent,
                    captureUptime: TimeSpan.FromSeconds(second),
                    allowOutputThroughput: true,
                    allowSourceCadence: false);
        }

        Assert.True(
            customObjectiveAssessment is
            {
                Kind: CaptureStarvationKind.OutputThroughput,
                UsedCustomFullscreenFallback: true
            } &&
            ReplayBufferService.SelectSafeCadenceRecoveryReason(
                outputRequiresScaling: true,
                CapturePerformanceProfile.Resilient,
                customObjectiveAssessment.Kind,
                customObjectiveAssessment
                    .UsedCustomFullscreenFallback) ==
                CaptureRecoveryReason.SourceStarvation,
            "Window-level custom ambiguity incorrectly suppressed objective below-real-time output recovery.");

        Assert.Equal(
            CaptureRecoveryReason.SourcePressure,
            ReplayBufferService.SelectCadenceRecoveryReason(
                outputRequiresScaling: false,
                CapturePerformanceProfile.LowImpact),
            "The first native LowImpact cadence fault did not promote its capture profile.");
        Assert.Equal(
            CaptureRecoveryReason.SourceProfilePromotion,
            ReplayBufferService.SelectCadenceRecoveryReason(
                outputRequiresScaling: false,
                CapturePerformanceProfile.LowImpact,
                CaptureStarvationKind.ChronicLowCadence),
            "Ambiguous initial low cadence was treated as a destructive capture fault.");
        Assert.Equal(
            CaptureRecoveryReason.SourceStarvation,
            ReplayBufferService.SelectCadenceRecoveryReason(
                outputRequiresScaling: false,
                CapturePerformanceProfile.Resilient),
            "Persistent cadence failure after promotion did not enter bounded recovery.");
        Assert.Equal(
            CaptureRecoveryReason.SourceStarvation,
            ReplayBufferService.SelectCadenceRecoveryReason(
                outputRequiresScaling: true,
                CapturePerformanceProfile.LowImpact),
            "A scaled WGC graph running below real time was mistaken for native profile pressure.");
        Assert.Equal(
            CaptureRecoveryReason.SourceProfilePromotion,
            ReplayBufferService.SelectSafeCadenceRecoveryReason(
                outputRequiresScaling: false,
                CapturePerformanceProfile.LowImpact,
                CaptureStarvationKind.Severe,
                usedCustomFullscreenFallback: true),
            "Ambiguous custom fullscreen cadence did not use the non-destructive native profile promotion.");
        Assert.True(
            ReplayBufferService.SelectSafeCadenceRecoveryReason(
                outputRequiresScaling: false,
                CapturePerformanceProfile.Resilient,
                CaptureStarvationKind.Severe,
                usedCustomFullscreenFallback: true) is null &&
            ReplayBufferService.SelectSafeCadenceRecoveryReason(
                outputRequiresScaling: true,
                CapturePerformanceProfile.LowImpact,
                CaptureStarvationKind.SchedulingPressure,
                usedCustomFullscreenFallback: true) is null,
            "Ambiguous custom fullscreen content cadence remained destructive after promotion or scaling.");
        Assert.Equal(
            CaptureRecoveryReason.SourceStarvation,
            ReplayBufferService.SelectSafeCadenceRecoveryReason(
                outputRequiresScaling: true,
                CapturePerformanceProfile.Resilient,
                CaptureStarvationKind.OutputThroughput,
                usedCustomFullscreenFallback: true),
            "Objective custom-fullscreen throughput failure was incorrectly suppressed.");
        Assert.True(
            ReplayBufferService.ShouldSuppressNonObjectiveCaptureCadence(
                usedCustomFullscreenFallback: true,
                outputRequiresScaling: false,
                CapturePerformanceProfile.Resilient,
                deferredRecoveryIsPending: false) &&
            ReplayBufferService.ShouldSuppressNonObjectiveCaptureCadence(
                usedCustomFullscreenFallback: true,
                outputRequiresScaling: false,
                CapturePerformanceProfile.LowImpact,
                deferredRecoveryIsPending: true) &&
            ReplayBufferService.ShouldSuppressNonObjectiveCaptureCadence(
                usedCustomFullscreenFallback: false,
                outputRequiresScaling: true,
                CapturePerformanceProfile.Resilient,
                deferredRecoveryIsPending: true) &&
            !ReplayBufferService.ShouldSuppressNonObjectiveCaptureCadence(
                usedCustomFullscreenFallback: false,
                outputRequiresScaling: true,
                CapturePerformanceProfile.Resilient,
                deferredRecoveryIsPending: false),
            "A pending promotion can still latch non-objective cadence, or normal non-custom monitoring stayed suppressed afterward.");
        Assert.True(
            ReplayBufferService.CanRecoverySupersedeDeferredMaintenance(
                CaptureRecoveryReason.SourcePressure) &&
            ReplayBufferService.CanRecoverySupersedeDeferredMaintenance(
                CaptureRecoveryReason.SourceStarvation) &&
            ReplayBufferService.CanRecoverySupersedeDeferredMaintenance(
                CaptureRecoveryReason.CaptureHang) &&
            !ReplayBufferService.CanRecoverySupersedeDeferredMaintenance(
                CaptureRecoveryReason.SourceProfilePromotion),
            "A deferred profile retry can hide an objective capture fault or be superseded by another ambiguous promotion.");
        Assert.True(
            ReplayBufferService.HasCaptureRecoveryRetryCapacity(
                attempt: 1) &&
            ReplayBufferService.HasCaptureRecoveryRetryCapacity(
                attempt: 2) &&
            !ReplayBufferService.HasCaptureRecoveryRetryCapacity(
                attempt: 3),
            "Deferred capture verification can retry forever or lost its bounded recovery window.");

        Assert.True(
            !MainWindow.UsesAutomaticCaptureRecoveryBudget(
                CaptureRecoveryReason.ScheduledRefresh) &&
            !MainWindow.UsesAutomaticCaptureRecoveryBudget(
                CaptureRecoveryReason.SourcePressure) &&
            !MainWindow.UsesAutomaticCaptureRecoveryBudget(
                CaptureRecoveryReason.SourceProfilePromotion),
            "Routine renewal and one-way Source profile promotion must not consume fault retries.");
        Assert.True(
            MainWindow.UsesAutomaticCaptureRecoveryBudget(
                CaptureRecoveryReason.SourceStarvation) &&
            MainWindow.UsesAutomaticCaptureRecoveryBudget(
                CaptureRecoveryReason.CaptureHang),
            "Fault recovery must remain bounded independently from scheduled WGC renewal.");
        Assert.True(
            MainWindow.SupportsAutomaticCaptureRecovery(
                DesktopCaptureBackend.WindowsGraphicsCapture) &&
            MainWindow.SupportsAutomaticCaptureRecovery(
                DesktopCaptureBackend.Gdi),
            "A verified desktop capture backend was excluded from bounded recovery.");
        Assert.True(
            ReplayBufferService.CanRefreshCaptureBackend(
                DesktopCaptureBackend.WindowsGraphicsCapture) &&
            ReplayBufferService.CanRefreshCaptureBackend(
                DesktopCaptureBackend.Gdi),
            "A monitored desktop backend cannot execute its requested capture refresh.");
        Assert.True(
            !ReplayBufferService.ShouldInvalidateCaptureGeneration(
                CaptureRecoveryReason.SourceProfilePromotion) &&
            ReplayBufferService.ShouldInvalidateCaptureGeneration(
                CaptureRecoveryReason.SourcePressure) &&
            ReplayBufferService.ShouldInvalidateCaptureGeneration(
                CaptureRecoveryReason.SourceStarvation) &&
            ReplayBufferService.ShouldInvalidateCaptureGeneration(
                CaptureRecoveryReason.CaptureHang),
            "Ambiguous profile promotion invalidated media, or a proven capture fault left media exportable.");
        Assert.True(
            MainWindow.ShouldUseSourceSafetyRecovery(
                DesktopCaptureBackend.WindowsGraphicsCapture,
                usesRecoveryBudget: true,
                completedRecoveryCount: 0,
                outputRequiresScaling: true) &&
            MainWindow.ShouldUseSourceSafetyRecovery(
                DesktopCaptureBackend.WindowsGraphicsCapture,
                usesRecoveryBudget: true,
                completedRecoveryCount: 1,
                outputRequiresScaling: false) &&
            !MainWindow.ShouldUseSourceSafetyRecovery(
                DesktopCaptureBackend.Gdi,
                usesRecoveryBudget: true,
                completedRecoveryCount: 1,
                outputRequiresScaling: true),
            "GDI recovery could enter the WGC-only Source safety fallback.");
        Assert.True(
            MainWindow.ShouldRetainResilientCaptureProfile(
                profileWasPromoted: true,
                CapturePerformanceProfile.LowImpact,
                outputRequiresScaling: false) &&
            MainWindow.ShouldRetainResilientCaptureProfile(
                profileWasPromoted: false,
                CapturePerformanceProfile.Resilient,
                outputRequiresScaling: false) &&
            !MainWindow.ShouldRetainResilientCaptureProfile(
                profileWasPromoted: false,
                CapturePerformanceProfile.LowImpact,
                outputRequiresScaling: false) &&
            !MainWindow.ShouldRetainResilientCaptureProfile(
                profileWasPromoted: true,
                CapturePerformanceProfile.Resilient,
                outputRequiresScaling: true),
            "Automatic restarts did not retain a promoted no-scale profile or incorrectly retained it for an already-normal scaled graph.");

        return Task.CompletedTask;
    }

    private static CaptureProgressSample CreateProgressSample(
        int seconds,
        long frame,
        long duplicatedFrames) =>
        new(
            frame,
            duplicatedFrames,
            DroppedFrames: 0,
            OutputTimeMicroseconds: checked(seconds * 1_000_000L),
            Timestamp: checked(seconds * (long)Stopwatch.Frequency));

    private static Task TestCaptureRecoveryRequestGateAsync()
    {
        var gate = new CaptureRecoveryRequestGate();
        Assert.True(
            gate.TryBegin(
                CaptureRecoveryReason.SourceStarvation,
                out var healthRequestId,
                out _),
            "The first health recovery should acquire the request gate.");
        Assert.True(gate.IsPending, "The acquired recovery request was not marked pending.");
        Assert.True(
            !gate.TryBegin(
                CaptureRecoveryReason.ScheduledRefresh,
                out _,
                out var pendingReason),
            "A second request must not overlap the pending health recovery.");
        Assert.Equal(
            CaptureRecoveryReason.SourceStarvation,
            pendingReason,
            "A rejected scheduled request must know that it should be coalesced behind a health request.");

        Assert.True(
            gate.SuppressFaults(healthRequestId),
            "The active health request should accept fault suppression.");
        Assert.True(
            gate.IsPending,
            "Fault suppression must keep ownership until the UI queue clears in its finally block.");
        Assert.True(
            gate.Complete(healthRequestId, out var scheduledRefreshQueued),
            "The suppressed health request should complete after the UI queue clears.");
        Assert.True(
            scheduledRefreshQueued,
            "A display/scheduled refresh queued behind the health request was lost.");
        Assert.True(
            !gate.IsPending,
            "Rejecting an over-budget health recovery must release its pending request.");
        Assert.True(
            !gate.TryBegin(CaptureRecoveryReason.CaptureHang, out _, out _),
            "Further health faults must stay suppressed for the bounded session.");
        Assert.True(
            gate.TryBegin(
                CaptureRecoveryReason.ScheduledRefresh,
                out var scheduledRequestId,
                out _),
            "Fault suppression must never disable routine long-session WGC renewal.");

        Assert.True(
            !gate.Complete(healthRequestId, out _),
            "A stale request unexpectedly completed the scheduled renewal.");
        Assert.True(
            gate.IsPending,
            "A stale completion from an older request released the scheduled renewal.");
        Assert.True(
            gate.Complete(scheduledRequestId, out var duplicateScheduledRefresh) &&
            !duplicateScheduledRefresh,
            "Completing the coalesced scheduled request queued an unnecessary duplicate.");
        gate.ResetForSession();
        Assert.True(
            gate.TryBegin(CaptureRecoveryReason.CaptureHang, out var resetRequestId, out _),
            "A manual replay session must reset the bounded fault suppression.");
        Assert.True(
            gate.Complete(resetRequestId, out _),
            "The reset health request did not complete.");

        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            gate.ResetForSession();
            Assert.True(
                gate.TryBegin(
                    CaptureRecoveryReason.SourceStarvation,
                    out var racedRequestId,
                    out _),
                "The race test could not acquire its initial health request.");
            var overlappingFaultAccepted = false;
            Parallel.Invoke(
                () => gate.SuppressFaults(racedRequestId),
                () => overlappingFaultAccepted = gate.TryBegin(
                    CaptureRecoveryReason.CaptureHang,
                    out _,
                    out _));
            Assert.True(
                !overlappingFaultAccepted,
                "Fault suppression raced with and admitted a new health request.");
            Assert.True(
                gate.Complete(racedRequestId, out _),
                "The race test failed to release its suppressed request.");
        }

        return Task.CompletedTask;
    }

    private static async Task TestScheduledCaptureRefreshCoordinatorAsync()
    {
        var entered = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processIds = new List<int>();
        var concurrentWorkers = 0;
        var maximumConcurrentWorkers = 0;

        await using (var coordinator = new ScheduledCaptureRefreshCoordinator(
            async (expectedProcessId, _, cancellationToken) =>
            {
                lock (processIds)
                {
                    processIds.Add(expectedProcessId);
                }

                var concurrent = Interlocked.Increment(ref concurrentWorkers);
                maximumConcurrentWorkers = Math.Max(maximumConcurrentWorkers, concurrent);
                entered.TrySetResult(expectedProcessId);
                try
                {
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref concurrentWorkers);
                }
            }))
        {
            Assert.True(
                coordinator.TrySchedule(101, "aged WGC generation"),
                "The first scheduled refresh did not acquire the background coordinator.");
            Assert.Equal(
                101,
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false),
                "The background coordinator lost the expected capture PID.");
            Assert.True(
                coordinator.IsActive,
                "The coordinator did not report its blocked background worker.");
            Assert.True(
                !coordinator.TrySchedule(102, "overlapping request"),
                "The coordinator admitted an overlapping capture refresh.");

            release.TrySetResult();
            await coordinator.WaitForIdleAsync()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            Assert.Equal(
                1,
                maximumConcurrentWorkers,
                "Scheduled capture refreshes were not serialized.");

            Assert.True(
                coordinator.TrySchedule(202, "next WGC generation"),
                "A completed background refresh did not release the coordinator gate.");
            await coordinator.WaitForIdleAsync()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            lock (processIds)
            {
                Assert.SequenceEqual(
                    new[] { 101, 202 },
                    processIds,
                    "The coordinator did not preserve generation-specific PID ordering.");
            }
        }

        var failureCount = 0;
        await using (var faultingCoordinator = new ScheduledCaptureRefreshCoordinator(
            (_, _, _) => Task.FromException(new InvalidOperationException("synthetic refresh failure")),
            (_, _, _) => _ = Interlocked.Increment(ref failureCount)))
        {
            Assert.True(
                faultingCoordinator.TrySchedule(303, "fault containment"),
                "The fault-containment coordinator rejected its first request.");
            await faultingCoordinator.WaitForIdleAsync()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            Assert.Equal(1, failureCount, "A background refresh failure escaped its handler.");
            Assert.True(
                faultingCoordinator.TrySchedule(304, "post-fault retry"),
                "A contained refresh failure permanently latched the coordinator gate.");
            await faultingCoordinator.WaitForIdleAsync()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }

        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposableCoordinator = new ScheduledCaptureRefreshCoordinator(
            async (_, _, cancellationToken) =>
            {
                disposalEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    cancellationObserved.TrySetResult();
                }
            });
        Assert.True(
            disposableCoordinator.TrySchedule(404, "disposal cancellation"),
            "The disposal test could not queue its refresh.");
        await disposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await disposableCoordinator.DisposeAsync().ConfigureAwait(false);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Assert.True(
            !disposableCoordinator.TrySchedule(405, "after disposal"),
            "A disposed coordinator accepted another refresh.");
    }

    private static Task TestDiscontinuousCaptureRefreshCoalescingAsync()
    {
        Assert.True(
            ReplayBufferService.ShouldScheduleDiscontinuousRefreshContinuation(
                refreshWasQueued: false),
            "A request racing the end of an active refresh did not retain a continuation.");
        Assert.True(
            !ReplayBufferService.ShouldScheduleDiscontinuousRefreshContinuation(
                refreshWasQueued: true),
            "A successfully queued discontinuous refresh scheduled a redundant continuation.");

        Assert.True(
            ReplayBufferService.IsDiscontinuousRefreshQuiescent(
                hasActionablePendingRequest: false,
                coordinatorIsActive: false,
                continuationIsScheduled: false),
            "A fully drained discontinuous refresh pipeline did not report quiescence.");
        Assert.True(
            !ReplayBufferService.IsDiscontinuousRefreshQuiescent(
                hasActionablePendingRequest: true,
                coordinatorIsActive: false,
                continuationIsScheduled: false),
            "Wait-for-idle ignored an actionable pending display transition.");
        Assert.True(
            !ReplayBufferService.IsDiscontinuousRefreshQuiescent(
                hasActionablePendingRequest: false,
                coordinatorIsActive: true,
                continuationIsScheduled: false),
            "Wait-for-idle ignored an active display-transition worker.");
        Assert.True(
            !ReplayBufferService.IsDiscontinuousRefreshQuiescent(
                hasActionablePendingRequest: false,
                coordinatorIsActive: false,
                continuationIsScheduled: true),
            "Wait-for-idle ignored a coalescing continuation.");

        Assert.Equal(
            ReplayBufferService.DiscontinuousRefreshReadiness.Wait,
            ReplayBufferService.GetDiscontinuousRefreshReadiness(
                hasCurrentPendingRequest: true,
                isDisposed: false,
                isRunning: true,
                isStopping: true,
                hasCaptureProcess: false,
                canRefreshCaptureBackend: true),
            "A same-session display epoch lost its continuation while another refresh temporarily owned the process.");
        Assert.Equal(
            ReplayBufferService.DiscontinuousRefreshReadiness.Wait,
            ReplayBufferService.GetDiscontinuousRefreshReadiness(
                hasCurrentPendingRequest: true,
                isDisposed: false,
                isRunning: true,
                isStopping: false,
                hasCaptureProcess: false,
                canRefreshCaptureBackend: true),
            "A process-null replacement race was treated as a terminal display epoch.");
        Assert.Equal(
            ReplayBufferService.DiscontinuousRefreshReadiness.Actionable,
            ReplayBufferService.GetDiscontinuousRefreshReadiness(
                hasCurrentPendingRequest: true,
                isDisposed: false,
                isRunning: true,
                isStopping: false,
                hasCaptureProcess: true,
                canRefreshCaptureBackend: true),
            "A live pending display epoch did not become actionable after replacement.");
        Assert.Equal(
            ReplayBufferService.DiscontinuousRefreshReadiness.Terminal,
            ReplayBufferService.GetDiscontinuousRefreshReadiness(
                hasCurrentPendingRequest: true,
                isDisposed: false,
                isRunning: false,
                isStopping: false,
                hasCaptureProcess: false,
                canRefreshCaptureBackend: true),
            "A settled stopped session retained a display continuation forever.");
        Assert.Equal(
            ReplayBufferService.DiscontinuousRefreshReadiness.Terminal,
            ReplayBufferService.GetDiscontinuousRefreshReadiness(
                hasCurrentPendingRequest: true,
                isDisposed: true,
                isRunning: true,
                isStopping: true,
                hasCaptureProcess: false,
                canRefreshCaptureBackend: true),
            "A disposed service retained a display continuation.");
        Assert.True(
            ReplayBufferService.IsCurrentCaptureRecoveryRetry(
                currentGeneration: 12,
                taskGeneration: 12),
            "A retry registered for the current generation was treated as stale.");
        Assert.True(
            !ReplayBufferService.IsCurrentCaptureRecoveryRetry(
                currentGeneration: 13,
                taskGeneration: 12),
            "A superseded retry task was allowed to delay a newer display epoch.");

        var firstAssessment = new CaptureStarvationAssessment(
            0.85,
            8,
            TimeSpan.FromSeconds(8),
            CaptureStarvationKind.SchedulingPressure);
        var laterAssessment = new CaptureStarvationAssessment(
            0.95,
            3,
            TimeSpan.FromSeconds(8),
            CaptureStarvationKind.Severe);
        Assert.True(
            ReferenceEquals(
                firstAssessment,
                ReplayBufferService.RetainFirstCaptureAssessment(
                    firstAssessment,
                    laterAssessment)),
            "The progress drain replaced the first actionable assessment in a batch.");
        Assert.True(
            ReferenceEquals(
                laterAssessment,
                ReplayBufferService.RetainFirstCaptureAssessment(
                    retained: null,
                    laterAssessment)),
            "The progress drain failed to retain its first actionable assessment.");

        return Task.CompletedTask;
    }

    private static Task TestReplayCaptureFallbackRecoveryPolicyAsync()
    {
        var deadline = new DateTimeOffset(
            2026,
            7,
            25,
            12,
            0,
            0,
            TimeSpan.Zero).UtcDateTime.Ticks;
        Assert.True(
            !ReplayBufferService.ShouldScheduleDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true,
                deadline,
                deadline - 1),
            "A degraded capture reprobe ran before the real cache deadline.");
        Assert.True(
            ReplayBufferService.ShouldScheduleDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true,
                deadline,
                deadline),
            "A service-selected GDI strategy did not become eligible at cache expiry.");
        Assert.True(
            !ReplayBufferService.ShouldScheduleDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: false,
                deadline,
                deadline),
            "An explicit GDI strategy override was incorrectly scheduled for promotion.");
        Assert.True(
            !ReplayBufferService.ShouldScheduleDegradedCaptureReprobe(
                DesktopCaptureBackend.WindowsGraphicsCapture,
                strategyUsesCapabilityProbe: true,
                deadline,
                deadline),
            "An active WGC strategy entered the degraded-capture reprobe path.");
        Assert.True(
            !ReplayBufferService.ShouldScheduleDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true,
                deadlineUtcTicks: 0,
                deadline),
            "A degraded capture without an actual cache expiry entered a reprobe loop.");
        Assert.True(
            ReplayBufferService.ShouldMaintainDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true) &&
            !ReplayBufferService.ShouldMaintainDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: false) &&
            !ReplayBufferService.ShouldMaintainDegradedCaptureReprobe(
                DesktopCaptureBackend.WindowsGraphicsCapture,
                strategyUsesCapabilityProbe: true),
            "A GDI refresh lost or incorrectly enabled its background WGC promotion opportunity.");
        Assert.True(
            !ReplayBufferService.ShouldRunDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true,
                deadline,
                deadline,
                new CaptureForegroundContext(
                    IsFullscreenOnCapturedDisplay: true,
                    HasRecentInput: true)) &&
            ReplayBufferService.ShouldRunDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true,
                deadline,
                deadline,
                new CaptureForegroundContext(
                    IsFullscreenOnCapturedDisplay: true,
                    HasRecentInput: false)) &&
            !ReplayBufferService.ShouldRunDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true,
                deadline,
                deadline,
                new CaptureForegroundContext(
                    IsFullscreenOnCapturedDisplay: false,
                    HasRecentInput: true)) &&
            ReplayBufferService.ShouldRunDegradedCaptureReprobe(
                DesktopCaptureBackend.Gdi,
                strategyUsesCapabilityProbe: true,
                deadline,
                deadline,
                new CaptureForegroundContext(
                    IsFullscreenOnCapturedDisplay: false,
                    HasRecentInput: false)),
            "The real capture/encode reprobe can compete with recent foreground input or never resumes while idle.");
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            ReplayBufferService.GetDegradedCaptureReprobeDelay(attempt: 0),
            "The first active GDI recheck can still compete with gameplay too frequently.");
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            ReplayBufferService.GetDegradedCaptureReprobeDelay(attempt: 1),
            "The active GDI recheck did not back off after a repeated miss.");
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            ReplayBufferService.GetDegradedCaptureReprobeDelay(attempt: 20),
            "The active GDI recheck did not respect its bounded long-session cap.");

        var currentGdi = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.Gdi);
        var verifiedWgc = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.WindowsGraphicsCapture);
        Assert.True(
            ReplayBufferService.CanPromoteDegradedCapture(
                currentGdi,
                verifiedWgc,
                strategyUsesCapabilityProbe: true),
            "A capability-probed GDI session rejected a confirmed WGC replacement.");
        Assert.True(
            !ReplayBufferService.CanPromoteDegradedCapture(
                currentGdi,
                VideoEncodingStrategy.SoftwareGdi,
                strategyUsesCapabilityProbe: true),
            "A second GDI result was treated as a WGC promotion.");
        Assert.True(
            !ReplayBufferService.CanPromoteDegradedCapture(
                currentGdi,
                verifiedWgc,
                strategyUsesCapabilityProbe: false),
            "An explicit strategy override was eligible for automatic promotion.");
        Assert.True(
            ReplayBufferService.ShouldDeferDegradedCapturePromotion(
                promotesDegradedCapture: true,
                reachedSegmentBoundary: false),
            "An optional WGC promotion could still stop healthy GDI before a completed segment boundary.");
        Assert.True(
            !ReplayBufferService.ShouldDeferDegradedCapturePromotion(
                promotesDegradedCapture: true,
                reachedSegmentBoundary: true),
            "A boundary-aligned optional WGC promotion was deferred.");
        Assert.True(
            !ReplayBufferService.ShouldDeferDegradedCapturePromotion(
                promotesDegradedCapture: false,
                reachedSegmentBoundary: false),
            "A mandatory service-owned WGC renewal was mistaken for an optional promotion.");

        Assert.True(
            ReplayBufferService.ShouldRetryCaptureLaunch(
                allowFreshCapabilityRetry: true,
                strategyUsesCapabilityProbe: true,
                realCaptureLaunchFailed: true),
            "A real launch failure did not permit the single fresh capability retry.");
        Assert.True(
            !ReplayBufferService.ShouldRetryCaptureLaunch(
                allowFreshCapabilityRetry: false,
                strategyUsesCapabilityProbe: true,
                realCaptureLaunchFailed: true),
            "Capture launch recovery was not bounded to one retry.");
        Assert.True(
            !ReplayBufferService.ShouldRetryCaptureLaunch(
                allowFreshCapabilityRetry: true,
                strategyUsesCapabilityProbe: false,
                realCaptureLaunchFailed: true),
            "An explicit strategy override entered capability-cache recovery.");
        Assert.True(
            !ReplayBufferService.ShouldRetryCaptureLaunch(
                allowFreshCapabilityRetry: true,
                strategyUsesCapabilityProbe: true,
                realCaptureLaunchFailed: false),
            "A preflight failure was misclassified as a real capture launch failure.");

        return Task.CompletedTask;
    }

    private static async Task TestReplayPostSaveStateAndSchedulerAsync()
    {
        var faulted = new ReplayStateSnapshot(
            ReplayState.Faulted,
            TimeSpan.FromSeconds(18),
            TimeSpan.FromSeconds(30),
            BufferBytes: 12_345,
            Message: "capture fault");
        var faultedAfterSave = ReplayBufferService.BuildPostSaveSnapshot(
            faulted,
            @"C:\Clips\saved.mp4");
        Assert.Equal(
            ReplayState.Faulted,
            faultedAfterSave.State,
            "Save completion overwrote a terminal capture fault.");
        Assert.Equal(
            faulted.AvailableDuration,
            faultedAfterSave.AvailableDuration,
            "Save completion rewrote the faulted buffer duration.");
        Assert.Equal(
            faulted.BufferBytes,
            faultedAfterSave.BufferBytes,
            "Save completion rewrote the faulted buffer size.");
        Assert.Equal(
            faulted.Message,
            faultedAfterSave.Message,
            "Save completion replaced the terminal fault diagnostic.");
        Assert.Equal(
            @"C:\Clips\saved.mp4",
            faultedAfterSave.LastSavedPath,
            "Save completion did not preserve the successful export path.");

        var stopped = faulted with
        {
            State = ReplayState.Stopped,
            Message = "stopped",
            LastSavedPath = @"C:\Clips\previous.mp4"
        };
        var stoppedAfterFailedSave = ReplayBufferService.BuildPostSaveSnapshot(
            stopped,
            lastSavedPath: null);
        Assert.Equal(
            stopped,
            stoppedAfterFailedSave,
            "A failed save changed an already stopped replay snapshot.");

        var awaiter = ReplayBufferService.SwitchToThreadPool().GetAwaiter();
        Assert.True(
            !awaiter.IsCompleted,
            "The replay snapshot scheduler can complete inline on the caller thread.");
        var continuation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        awaiter.UnsafeOnCompleted(
            () => continuation.TrySetResult(Thread.CurrentThread.IsThreadPoolThread));
        Assert.True(
            await continuation.Task
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false),
            "The replay segment snapshot continuation did not move to the thread pool.");
    }

    private static async Task TestCaptureRuntimeJournalAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var journal = new CaptureRuntimeJournal(testDirectory);
            journal.Record(
                "capture_started",
                backend: DesktopCaptureBackend.WindowsGraphicsCapture.ToString(),
                width: 1920,
                height: 1080,
                framesPerSecond: 60,
                captureCursor: false,
                detail: "Deterministic lifecycle test.");
            var journalPath = journal.JournalPath;
            await journal.DisposeAsync().ConfigureAwait(false);

            Assert.True(File.Exists(journalPath), "The local capture journal was not flushed on disposal.");
            var content = await File.ReadAllTextAsync(journalPath).ConfigureAwait(false);
            Assert.True(
                content.Contains("\"event\":\"capture_started\"", StringComparison.Ordinal) &&
                content.Contains("\"captureCursor\":false", StringComparison.Ordinal) &&
                content.Contains("\"framesPerSecond\":60", StringComparison.Ordinal),
                "The capture journal omitted required lifecycle metadata.");
            Assert.True(
                !content.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase),
                "The capture journal must not contain the Windows account name.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestCaptureRuntimeJournalResourceBoundsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = CreateTestDirectory();
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            currentProcess.Refresh();
            var initialHandleCount = currentProcess.HandleCount;
            var journal = new CaptureRuntimeJournal(testDirectory);
            for (var sample = 0; sample < 256; sample++)
            {
                journal.Record(
                    "resource_stress_sample",
                    backend: DesktopCaptureBackend.WindowsGraphicsCapture.ToString(),
                    width: 1920,
                    height: 1080,
                    framesPerSecond: 60,
                    captureCursor: false,
                    detail: $"sample={sample}");
            }

            await journal.DisposeAsync().ConfigureAwait(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            currentProcess.Refresh();
            var retainedHandles = currentProcess.HandleCount - initialHandleCount;
            Assert.True(
                retainedHandles <= 24,
                $"Runtime journal sampling retained {retainedHandles} process handles after disposal.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestReplayServiceConcurrentDisposalAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var service = new ReplayBufferService(
                new FfmpegSetupService(Path.Combine(testDirectory, "ffmpeg")),
                Path.Combine(testDirectory, "buffer"));
            var disposals = Enumerable.Range(0, 8)
                .Select(_ => service.DisposeAsync().AsTask())
                .ToArray();
            await Task.WhenAll(disposals).ConfigureAwait(false);

            var rejectedAfterDispose = false;
            try
            {
                await service.StopAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                rejectedAfterDispose = true;
            }

            Assert.True(
                rejectedAfterDispose,
                "A fully disposed replay service accepted new lifecycle work.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestReplayMaintenanceAndBoundedPruningAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var maintenanceEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseMaintenance = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ReplayBufferService? service = null;
            var construction = Task.Run(() =>
            {
                service = new ReplayBufferService(
                    new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                    Path.Combine(testDirectory, "Async-Buffer"),
                    async () =>
                    {
                        maintenanceEntered.TrySetResult();
                        await releaseMaintenance.Task.ConfigureAwait(false);
                    });
            });

            await maintenanceEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            var constructorReturnedBeforeCleanup = false;
            try
            {
                await construction
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
                constructorReturnedBeforeCleanup = true;
            }
            catch (TimeoutException)
            {
                // Release below so a regressed synchronous constructor cannot
                // leave the test worker blocked after the assertion.
            }
            finally
            {
                releaseMaintenance.TrySetResult();
            }

            await construction.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.True(
                constructorReturnedBeforeCleanup,
                "ReplayBufferService construction blocked on slow stale-buffer maintenance.");
            Assert.True(service is not null, "The replay maintenance test did not construct its service.");
            await service!.WaitForInitialBufferMaintenanceAsync()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await service.DisposeAsync().ConfigureAwait(false);

            Assert.True(
                ReplayBufferService.ShouldContinueCurrentSessionCleanup(
                    directoriesInspected: 0,
                    filesDeleted: 0,
                    elapsed: TimeSpan.Zero),
                "Current-session crash cleanup rejected an empty maintenance pass.");
            Assert.True(
                !ReplayBufferService.ShouldContinueCurrentSessionCleanup(
                    ReplayBufferService.MaximumCurrentSessionCleanupDirectoryCandidatesPerRun,
                    filesDeleted: 0,
                    elapsed: TimeSpan.Zero),
                "Current-session crash cleanup exceeded its directory bound.");
            Assert.True(
                !ReplayBufferService.ShouldContinueCurrentSessionCleanup(
                    directoriesInspected: 0,
                    ReplayBufferService.MaximumCurrentSessionCleanupFilesPerRun,
                    elapsed: TimeSpan.Zero),
                "Current-session crash cleanup exceeded its file bound.");
            Assert.True(
                !ReplayBufferService.ShouldContinueCurrentSessionCleanup(
                    directoriesInspected: 0,
                    filesDeleted: 0,
                    ReplayBufferService.CurrentSessionCleanupTimeBudget),
                "Current-session crash cleanup exceeded its elapsed-time bound.");

            var oneHourReady = new ReplayStateSnapshot(
                ReplayState.Ready,
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(1),
                BufferBytes: 12_345,
                Message: "ready");
            var reduced = ReplayBufferService.BuildRetentionUpdateSnapshot(
                oneHourReady,
                TimeSpan.FromSeconds(30),
                isRunning: true,
                encoderDescription: "test encoder");
            Assert.Equal(
                TimeSpan.FromSeconds(30),
                reduced.AvailableDuration,
                "A 1h -> 30s retention update did not clamp available duration immediately.");
            Assert.Equal(
                TimeSpan.FromSeconds(30),
                reduced.Retention,
                "The O(1) retention snapshot retained the old replay length.");
            Assert.Equal(
                ReplayState.Ready,
                reduced.State,
                "A fully buffered reduced ring should remain ready while pruning catches up.");

            var expanded = ReplayBufferService.BuildRetentionUpdateSnapshot(
                reduced,
                TimeSpan.FromHours(1),
                isRunning: true,
                encoderDescription: "test encoder");
            Assert.Equal(
                ReplayState.Buffering,
                expanded.State,
                "Increasing retention did not return the partially filled ring to Buffering.");

            const int oneHourSegments = 1_800;
            const int thirtySecondSegments = 15;
            var remainingExcess = oneHourSegments - thirtySecondSegments;
            var pruningTicks = 0;
            while (remainingExcess > 0)
            {
                var attemptBudget = ReplayBufferService.CalculateSegmentDeleteAttemptBudget(
                    thirtySecondSegments + remainingExcess,
                    thirtySecondSegments,
                    removableUntrustedCount: 0);
                Assert.True(
                    attemptBudget is > 0 and <=
                    ReplayBufferService.MaximumSegmentDeleteAttemptsPerRefresh,
                    "A large retention reduction escaped the bounded delete-attempt policy.");
                remainingExcess -= Math.Min(remainingExcess, attemptBudget);
                pruningTicks++;
            }

            Assert.Equal(
                28,
                pruningTicks,
                "A 1h -> 30s ring should be drained in bounded 64-file background batches.");
            Assert.Equal(
                ReplayBufferService.MaximumSegmentDeleteAttemptsPerRefresh,
                ReplayBufferService.CalculateSegmentDeleteAttemptBudget(
                    trustedCompletedCount: thirtySecondSegments,
                    maximumCompletedSegments: thirtySecondSegments,
                    removableUntrustedCount: 10_000),
                "A failed generation could schedule thousands of untrusted deletes in one refresh.");
            var mixedDeleteBudgets =
                ReplayBufferService.CalculateSegmentDeleteAttemptBudgets(
                    trustedCompletedCount: thirtySecondSegments + 10_000,
                    maximumCompletedSegments: thirtySecondSegments,
                    removableUntrustedCount: 10_000);
            Assert.Equal(
                ReplayBufferService.MaximumSegmentDeleteAttemptsPerRefresh / 2,
                mixedDeleteBudgets.UntrustedAttempts,
                "Mixed pruning did not reserve a bounded share for untrusted cleanup.");
            Assert.Equal(
                ReplayBufferService.MaximumSegmentDeleteAttemptsPerRefresh / 2,
                mixedDeleteBudgets.TrustedAttempts,
                "Undeletable untrusted entries could still starve trusted retention pruning.");
            Assert.Equal(
                ReplayBufferService.MaximumSegmentDeleteAttemptsPerRefresh,
                mixedDeleteBudgets.UntrustedAttempts +
                mixedDeleteBudgets.TrustedAttempts,
                "Fair pruning exceeded or wasted its per-refresh delete-attempt bound.");

            var skewedDeleteBudgets =
                ReplayBufferService.CalculateSegmentDeleteAttemptBudgets(
                    trustedCompletedCount: thirtySecondSegments + 1,
                    maximumCompletedSegments: thirtySecondSegments,
                    removableUntrustedCount: 10_000);
            Assert.Equal(
                1,
                skewedDeleteBudgets.TrustedAttempts,
                "The sole excess trusted segment did not receive a pruning attempt.");
            Assert.Equal(
                ReplayBufferService.MaximumSegmentDeleteAttemptsPerRefresh - 1,
                skewedDeleteBudgets.UntrustedAttempts,
                "Unused trusted capacity was not lent back to untrusted cleanup.");

            var firstUntrustedPage = ReplayBufferService.BuildCyclicCandidatePage(
                candidateCount: 80,
                maximumAttempts: mixedDeleteBudgets.UntrustedAttempts,
                startOffset: 0);
            Assert.SequenceEqual(
                Enumerable.Range(0, 32),
                firstUntrustedPage.CandidateIndices,
                "The first bounded untrusted cleanup page was not deterministic.");
            Assert.Equal(
                32,
                firstUntrustedPage.NextOffset,
                "The untrusted cleanup cursor did not advance past locked newest entries.");
            var secondUntrustedPage = ReplayBufferService.BuildCyclicCandidatePage(
                candidateCount: 80,
                maximumAttempts: mixedDeleteBudgets.UntrustedAttempts,
                startOffset: firstUntrustedPage.NextOffset);
            Assert.SequenceEqual(
                Enumerable.Range(32, 32),
                secondUntrustedPage.CandidateIndices,
                "A second tick retried the same locked untrusted page instead of progressing.");

            var trustedCandidateIds = Enumerable.Range(100, 80).ToArray();
            var firstTrustedStart =
                ReplayBufferService.FindCyclicCandidateStartIndex(
                    trustedCandidateIds,
                    nextCandidateId: -1);
            var firstTrustedPage = ReplayBufferService.BuildCyclicCandidatePage(
                trustedCandidateIds.Length,
                mixedDeleteBudgets.TrustedAttempts,
                firstTrustedStart);
            var nextTrustedSegmentId =
                trustedCandidateIds[firstTrustedPage.NextOffset];
            var secondTrustedStart =
                ReplayBufferService.FindCyclicCandidateStartIndex(
                    trustedCandidateIds,
                    nextTrustedSegmentId);
            Assert.Equal(
                32,
                secondTrustedStart,
                "Thirty-two locked oldest trusted entries hid every newer excess segment.");
            var afterSuccessfulFirstPage = trustedCandidateIds.Skip(32).ToArray();
            Assert.Equal(
                0,
                ReplayBufferService.FindCyclicCandidateStartIndex(
                    afterSuccessfulFirstPage,
                    nextTrustedSegmentId),
                "The trusted segment-ID cursor was not mutation-safe after earlier deletions.");
            var retainedTailRegressionIds = Enumerable.Range(1, 47).ToArray();
            var trustedExcessIds =
                ReplayBufferService.SelectTrustedExcessSegmentNumbers(
                    retainedTailRegressionIds,
                    maximumCompletedSegments: 15);
            Assert.SequenceEqual(
                Enumerable.Range(1, 32),
                trustedExcessIds,
                "Trusted pruning did not restrict candidates to the oldest excess prefix.");
            Assert.True(
                !trustedExcessIds.Intersect(Enumerable.Range(33, 15)).Any(),
                "A cyclic trusted cursor selected IDs from the newest retained tail.");

            var firstRootInspectionPage =
                ReplayBufferService.BuildCyclicCandidatePage(
                    candidateCount: 130,
                    maximumAttempts: 64,
                    startOffset: 0);
            Assert.Equal(
                64,
                firstRootInspectionPage.CandidateIndices.Length,
                "Stale-root full inspections exceeded their per-run bound.");
            var secondRootInspectionPage =
                ReplayBufferService.BuildCyclicCandidatePage(
                    candidateCount: 130,
                    maximumAttempts: 64,
                    startOffset: firstRootInspectionPage.NextOffset);
            Assert.Equal(
                64,
                secondRootInspectionPage.CandidateIndices[0],
                "The stale-root inspection cursor did not advance to the next bounded page.");

            var bufferParent = Path.Combine(testDirectory, "Session-Buffer");
            var currentRoot = Path.Combine(bufferParent, "WindowsSession-6");
            Directory.CreateDirectory(currentRoot);
            var utcNow = DateTime.UtcNow;

            static string CreateWindowsSessionResidue(
                string parent,
                int sessionId,
                DateTime lastWriteUtc,
                string fileName = "segment-000000000.mkv")
            {
                var root = Path.Combine(parent, $"WindowsSession-{sessionId}");
                var session = Path.Combine(root, $"session-20260725-{sessionId}");
                Directory.CreateDirectory(session);
                var file = Path.Combine(session, fileName);
                File.WriteAllBytes(file, [1, 2, 3]);
                File.SetLastWriteTimeUtc(file, lastWriteUtc);
                Directory.SetLastWriteTimeUtc(session, lastWriteUtc);
                Directory.SetLastWriteTimeUtc(root, lastWriteUtc);
                return root;
            }

            var staleRoot = CreateWindowsSessionResidue(
                bufferParent,
                1,
                utcNow - TimeSpan.FromDays(2));
            var recentRoot = CreateWindowsSessionResidue(
                bufferParent,
                2,
                utcNow - TimeSpan.FromHours(2));
            var activeRoot = CreateWindowsSessionResidue(
                bufferParent,
                3,
                utcNow - TimeSpan.FromDays(2));
            var unexpectedRoot = CreateWindowsSessionResidue(
                bufferParent,
                4,
                utcNow - TimeSpan.FromDays(2),
                fileName: "unexpected.bin");

            Assert.True(
                !ReplayBufferService.IsSafeWindowsSessionBufferRootPath(
                    bufferParent,
                    Path.Combine(bufferParent, "WindowsSession-5"),
                    FileAttributes.Directory | FileAttributes.ReparsePoint),
                "Inactive-session cleanup accepted a reparse-point root.");
            Assert.Equal(
                0,
                ReplayBufferService.CleanupInactiveWindowsSessionBufferRoots(
                    bufferParent,
                    currentRoot,
                    utcNow,
                    new HashSet<int>(),
                    ownershipEstablished: false),
                "Session cleanup did not fail closed when process ownership was unknown.");
            Assert.True(
                Directory.Exists(staleRoot),
                "Fail-closed ownership cleanup removed inactive capture data.");

            using (var cancelledCleanup = new CancellationTokenSource())
            {
                cancelledCleanup.Cancel();
                Assert.Equal(
                    0,
                    ReplayBufferService.CleanupInactiveWindowsSessionBufferRoots(
                        bufferParent,
                        currentRoot,
                        utcNow,
                        new HashSet<int> { 3 },
                        ownershipEstablished: true,
                        cancellationToken: cancelledCleanup.Token),
                    "Cancelled startup maintenance continued deleting inactive-session data.");
            }
            Assert.True(
                Directory.Exists(staleRoot),
                "Cancelled startup maintenance deleted data before live capture.");

            Assert.Equal(
                1,
                ReplayBufferService.CleanupInactiveWindowsSessionBufferRoots(
                    bufferParent,
                    currentRoot,
                    utcNow,
                    new HashSet<int> { 3 },
                    ownershipEstablished: true),
                "Exactly one old, inactive, strictly validated Windows session should be removed.");
            Assert.True(
                !Directory.Exists(staleRoot) &&
                Directory.Exists(recentRoot) &&
                Directory.Exists(activeRoot) &&
                Directory.Exists(unexpectedRoot),
                "Inactive-session cleanup crossed its age, active-owner, or content boundary.");

            var boundedRootA = CreateWindowsSessionResidue(
                bufferParent,
                10,
                utcNow - TimeSpan.FromDays(5));
            var boundedRootB = CreateWindowsSessionResidue(
                bufferParent,
                11,
                utcNow - TimeSpan.FromDays(4));
            var boundedRootC = CreateWindowsSessionResidue(
                bufferParent,
                12,
                utcNow - TimeSpan.FromDays(3));
            Assert.Equal(
                2,
                ReplayBufferService.CleanupInactiveWindowsSessionBufferRoots(
                    bufferParent,
                    currentRoot,
                    utcNow,
                    new HashSet<int> { 3 },
                    ownershipEstablished: true),
                "One maintenance pass exceeded its Windows-session root deletion bound.");
            Assert.True(
                !Directory.Exists(boundedRootA) &&
                !Directory.Exists(boundedRootB) &&
                Directory.Exists(boundedRootC),
                "Bounded cleanup did not remove the oldest eligible roots first.");

            var selectionParent = Path.Combine(
                testDirectory,
                "Session-Selection-Buffer");
            var selectionCurrent = Path.Combine(
                selectionParent,
                "WindowsSession-9999");
            Directory.CreateDirectory(selectionCurrent);
            var blockerPaths = new List<string>();
            var activeBlockerIds = new HashSet<int>();
            for (var index = 0; index < 6; index++)
            {
                var activeSessionId = 10_000 + index;
                activeBlockerIds.Add(activeSessionId);
                blockerPaths.Add(CreateWindowsSessionResidue(
                    selectionParent,
                    activeSessionId,
                    utcNow - TimeSpan.FromDays(10 + index)));
                blockerPaths.Add(CreateWindowsSessionResidue(
                    selectionParent,
                    11_000 + index,
                    utcNow - TimeSpan.FromHours(1)));
                blockerPaths.Add(CreateWindowsSessionResidue(
                    selectionParent,
                    12_000 + index,
                    utcNow - TimeSpan.FromDays(10 + index),
                    fileName: "unexpected.bin"));
            }

            var hiddenEligibleOldest = CreateWindowsSessionResidue(
                selectionParent,
                13_000,
                utcNow - TimeSpan.FromDays(6));
            var hiddenEligibleSecond = CreateWindowsSessionResidue(
                selectionParent,
                13_001,
                utcNow - TimeSpan.FromDays(5));
            var deliberatelyHostileOrder = blockerPaths
                .Concat([hiddenEligibleSecond, hiddenEligibleOldest])
                .ToArray();
            var selectionScan =
                ReplayBufferService.SelectInactiveWindowsSessionBufferRootCandidates(
                    Path.GetFullPath(selectionParent),
                    Path.GetFullPath(selectionCurrent),
                    utcNow,
                    activeBlockerIds,
                    scanSuffix: string.Empty,
                    candidatePaths: deliberatelyHostileOrder);
            Assert.SequenceEqual(
                [hiddenEligibleOldest, hiddenEligibleSecond],
                selectionScan.Candidates.Select(candidate => candidate.Path),
                "Active, recent, or unsafe first entries hid later stale safe roots, " +
                "or eligible roots were not sorted before the candidate limit.");

            var durableParent = Path.Combine(
                testDirectory,
                "Session-Durable-Buffer");
            var durableCurrent = Path.Combine(
                durableParent,
                "WindowsSession-39998");
            Directory.CreateDirectory(durableCurrent);
            var unsafeDurableRoots = new List<string>();
            for (var index = 0; index < 70; index++)
            {
                unsafeDurableRoots.Add(CreateWindowsSessionResidue(
                    durableParent,
                    20_000 + index,
                    utcNow - TimeSpan.FromDays(10),
                    fileName: "unexpected.bin"));
            }

            var durableEligible = CreateWindowsSessionResidue(
                durableParent,
                29_999,
                utcNow - TimeSpan.FromDays(10));
            var enumeratedForBoundedScan = 0;
            IEnumerable<string> CountedDurableCandidates()
            {
                foreach (var path in unsafeDurableRoots.Append(durableEligible))
                {
                    enumeratedForBoundedScan++;
                    yield return path;
                }
            }

            var boundedScan =
                ReplayBufferService.SelectInactiveWindowsSessionBufferRootCandidates(
                    Path.GetFullPath(durableParent),
                    Path.GetFullPath(durableCurrent),
                    utcNow,
                    new HashSet<int>(),
                    scanSuffix: string.Empty,
                    candidatePaths: CountedDurableCandidates());
            Assert.Equal(
                65,
                enumeratedForBoundedScan,
                "A stale-root startup scan enumerated beyond its bounded overflow sentinel.");
            Assert.Equal(
                65,
                boundedScan.EnumeratedCount,
                "The stale-root scan reported an incorrect bounded enumeration count.");
            Assert.True(
                boundedScan.InspectedCount <= 64,
                "A stale-root startup scan exceeded 64 full tree inspections.");
            Assert.Equal(
                "0",
                boundedScan.NextSuffix,
                "An overflowing root bucket did not durably descend to its first suffix page.");

            var durableRemoved = false;
            for (var simulatedStart = 0;
                 simulatedStart < 12 && !durableRemoved;
                 simulatedStart++)
            {
                _ = ReplayBufferService.CleanupInactiveWindowsSessionBufferRoots(
                    durableParent,
                    durableCurrent,
                    utcNow,
                    new HashSet<int>(),
                    ownershipEstablished: true);
                durableRemoved = !Directory.Exists(durableEligible);
            }

            Assert.True(
                durableRemoved,
                "More than 64 unsafe siblings hid a later safe stale root across simulated starts.");
            Assert.Equal(
                "10",
                ReplayBufferService.AdvanceWindowsSessionCleanupSuffix("00"),
                "The durable suffix cursor did not advance between child buckets.");
            Assert.Equal(
                string.Empty,
                ReplayBufferService.AdvanceWindowsSessionCleanupSuffix("99"),
                "The durable suffix cursor did not ascend and wrap after the final child bucket.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static Task TestCaptureGeometryMatrixAsync()
    {
        (int Width, int Height)[] displaySizes =
        [
            (1280, 720),
            (1366, 768),
            (1600, 900),
            (1920, 1080),
            (1920, 1200),
            (2560, 1080),
            (2560, 1440),
            (3440, 1440),
            (3840, 2160),
            (5120, 1440),
            (1024, 768),
            (1290, 980),
            (1080, 1080),
            (1080, 1920),
            (1919, 1079),
            (1365, 767)
        ];

        var caseNumber = 0;
        foreach (var (sourceWidth, sourceHeight) in displaySizes)
        {
            foreach (var resolution in ResolutionOption.All)
            {
                caseNumber++;
                var display = new DisplayOption(
                    $@"\\.\DISPLAY{caseNumber}",
                    $"Matrix display {caseNumber}",
                    0,
                    0,
                    sourceWidth,
                    sourceHeight,
                    true,
                    caseNumber);
                var configuration = new CaptureConfiguration(
                    display,
                    resolution,
                    60,
                    TimeSpan.FromMinutes(2),
                    false,
                    false,
                    null,
                    false,
                    null,
                    @"C:\Clips");
                var expected = ResolveExpectedCaptureSize(
                    sourceWidth,
                    sourceHeight,
                    resolution);
                var actual = CaptureGeometry.ResolveOutputSize(display, resolution);
                var context = $"{sourceWidth}x{sourceHeight} at {resolution.Id}";

                Assert.Equal(expected.Width, actual.Width, $"{context} resolved the wrong output width.");
                Assert.Equal(expected.Height, actual.Height, $"{context} resolved the wrong output height.");
                Assert.Equal(
                    expected.RequiresScaling,
                    actual.RequiresScaling,
                    $"{context} reported the wrong scaling state.");
                Assert.True(actual.Width >= 2 && actual.Height >= 2,
                    $"{context} produced an invalid output size.");
                Assert.True(actual.Width % 2 == 0 && actual.Height % 2 == 0,
                    $"{context} did not produce encoder-safe even dimensions.");
                Assert.True(actual.Width <= sourceWidth - sourceWidth % 2,
                    $"{context} upscaled the source width.");
                Assert.True(actual.Height <= sourceHeight - sourceHeight % 2,
                    $"{context} upscaled the source height.");
                if (resolution.Width is { } maximumWidth && resolution.Height is { } maximumHeight)
                {
                    Assert.True(actual.Width <= maximumWidth && actual.Height <= maximumHeight,
                        $"{context} exceeded its preset bounds.");
                }

                var aspectCrossProductError = Math.Abs(
                    actual.Width * (long)sourceHeight - actual.Height * (long)sourceWidth);
                var maximumEvenRoundingError = 2L * sourceWidth + 2L * sourceHeight;
                Assert.True(aspectCrossProductError <= maximumEvenRoundingError,
                    $"{context} did not preserve the source aspect ratio within even-pixel rounding.");

                var gdiArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
                    configuration,
                    [],
                    VideoEncodingStrategy.SoftwareGdi,
                    @"C:\Buffer");
                var gdiFilter = GetArgumentAfter(gdiArguments, "-vf") ?? string.Empty;
                var expectedGdiFilter = expected.RequiresScaling
                    ? $"scale={expected.Width}:{expected.Height}:flags=fast_bilinear,format=yuv420p,setpts=PTS-STARTPTS"
                    : sourceWidth % 2 == 0 && sourceHeight % 2 == 0
                        ? "null,format=yuv420p,setpts=PTS-STARTPTS"
                        : "scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=fast_bilinear,format=yuv420p,setpts=PTS-STARTPTS";
                Assert.Equal(expectedGdiFilter, gdiFilter,
                    $"{context} built the wrong GDI geometry filter.");
                Assert.True(!gdiFilter.Contains("pad=", StringComparison.Ordinal),
                    $"{context} reintroduced a padded GDI canvas.");

                var wgcArguments = FfmpegArgumentBuilder.BuildCaptureArguments(
                    configuration,
                    [],
                    new VideoEncodingStrategy(
                        VideoEncoderKind.NvidiaNvenc,
                        DesktopCaptureBackend.WindowsGraphicsCapture),
                    @"C:\Buffer");
                var wgcFilter = GetArgumentAfter(wgcArguments, "-i") ?? string.Empty;
                var expectedWgcGeometry = expected.RequiresScaling
                    ? $":width={expected.Width}:height={expected.Height}:resize_mode=scale:scale_mode=point"
                    : ":width=-2:height=-2:resize_mode=crop:scale_mode=point";
                Assert.True(wgcFilter.Contains(expectedWgcGeometry, StringComparison.Ordinal),
                    $"{context} built the wrong WGC geometry path: {wgcFilter}");
                Assert.True(!wgcFilter.Contains("resize_mode=scale_aspect", StringComparison.Ordinal),
                    $"{context} reintroduced the padded WGC aspect scaler.");
                Assert.Equal(
                    expected.RequiresScaling
                        ? FfmpegArgumentBuilder.ScaledVideoInputQueuePackets.ToString()
                        : FfmpegArgumentBuilder.VideoInputQueuePackets.ToString(),
                    GetArgumentAfter(wgcArguments, "-thread_queue_size"),
                    $"{context} built the wrong WGC input queue budget.");
            }
        }

        Assert.Equal(80, caseNumber, "The capture geometry Cartesian matrix is incomplete.");
        Assert.Equal(
            (1920, 804, true),
            ResolveExpectedCaptureSize(
                3440,
                1440,
                ResolutionOption.All.Single(option => option.Id == "1080p")),
            "Ultrawide 1080p bounds should choose the closest even secondary axis.");
        Assert.Equal(
            (608, 1080, true),
            ResolveExpectedCaptureSize(
                1080,
                1920,
                ResolutionOption.All.Single(option => option.Id == "1080p")),
            "Portrait 1080p bounds should choose the closest even secondary axis.");

        var originalDisplay = new DisplayOption(
            @"\.\DISPLAY1",
            "Original",
            0,
            0,
            2560,
            1440,
            true,
            0,
            165);
        var movedDisplay = originalDisplay with { Left = 2560 };
        var fixedOutputDisplay = originalDisplay with { Width = 1920, Height = 1080 };
        var squareDisplay = originalDisplay with { Width = 1080, Height = 1080 };
        var changedRefreshDisplay = originalDisplay with { RefreshRateHz = 144 };
        var unknownRefreshDisplay = originalDisplay with { RefreshRateHz = 0 };
        var sourceResolution = ResolutionOption.All.Single(option => option.Id == "source");
        var fullHdResolution = ResolutionOption.All.Single(option => option.Id == "1080p");
        var wgcStrategy = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.WindowsGraphicsCapture);
        var wgcSourcePlan = new CaptureSessionPlan(originalDisplay, sourceResolution, wgcStrategy);
        var wgcFixedPlan = new CaptureSessionPlan(originalDisplay, fullHdResolution, wgcStrategy);
        var gdiPlan = new CaptureSessionPlan(
            originalDisplay,
            fullHdResolution,
            VideoEncodingStrategy.SoftwareGdi);

        Assert.True(
            !CaptureGeometry.RequiresRestartForDisplayChange(wgcFixedPlan, movedDisplay),
            "A coordinate-only WGC change must not erase the rolling buffer.");
        Assert.True(
            !CaptureGeometry.RequiresRestartForDisplayChange(
                wgcFixedPlan,
                originalDisplay with { Label = "Renamed display" }),
            "Unchanged known refresh metadata must not restart WGC capture.");
        Assert.True(
            CaptureGeometry.RequiresRestartForDisplayChange(
                wgcFixedPlan,
                changedRefreshDisplay),
            "A known WGC refresh-rate transition must rebuild its divisor-aware input cadence.");
        Assert.True(
            !CaptureGeometry.RequiresRestartForDisplayChange(
                wgcFixedPlan,
                unknownRefreshDisplay),
            "A transient unknown refresh reading must not interrupt an active known-refresh session.");
        var unknownRefreshPlan = new CaptureSessionPlan(
            unknownRefreshDisplay,
            fullHdResolution,
            wgcStrategy);
        Assert.True(
            !CaptureGeometry.RequiresRestartForDisplayChange(
                unknownRefreshPlan,
                originalDisplay),
            "Learning a previously unknown refresh rate must wait for the next natural capture start.");
        Assert.True(
            CaptureGeometry.RequiresRestartForDisplayChange(gdiPlan, movedDisplay),
            "GDI must restart when its baked desktop coordinates change.");
        Assert.True(
            CaptureGeometry.RequiresRestartForDisplayChange(wgcFixedPlan, fixedOutputDisplay),
            "WGC must reacquire the monitor after a source mode change even when fixed output remains 1920x1080.");
        Assert.True(
            CaptureGeometry.RequiresRestartForDisplayChange(wgcSourcePlan, fixedOutputDisplay),
            "Source/native must restart when its encoded dimensions change.");
        Assert.True(
            CaptureGeometry.RequiresRestartForDisplayChange(wgcFixedPlan, squareDisplay),
            "A square custom mode that changes encoded dimensions must restart safely.");
        Assert.True(
            MainWindow.ShouldRestartReplayAfterDisplayChange(
                replayStartRequested: true,
                replayRunning: false,
                captureRestartInProgress: false,
                capturePlan: wgcFixedPlan,
                currentDisplay: fixedOutputDisplay),
            "A WGC fault before the debounce completes must recover the requested replay session.");
        Assert.True(
            !MainWindow.ShouldRestartReplayAfterDisplayChange(
                replayStartRequested: false,
                replayRunning: true,
                captureRestartInProgress: false,
                capturePlan: wgcSourcePlan,
                currentDisplay: fixedOutputDisplay),
            "An explicit user stop must win over a late display-change event.");
        Assert.True(
            MainWindow.ShouldRetryPendingDisplayRefresh(
                captureRestartInProgress: true,
                replayRunning: false,
                ReplayState.Faulted) &&
            MainWindow.ShouldRetryPendingDisplayRefresh(
                captureRestartInProgress: false,
                replayRunning: false,
                ReplayState.Starting) &&
            MainWindow.ShouldRetryPendingDisplayRefresh(
                captureRestartInProgress: false,
                replayRunning: false,
                ReplayState.Stopping),
            "A pending same-geometry refresh was dropped during an active capture transition.");
        Assert.True(
            !MainWindow.ShouldRetryPendingDisplayRefresh(
                captureRestartInProgress: false,
                replayRunning: false,
                ReplayState.Faulted) &&
            !MainWindow.ShouldRetryPendingDisplayRefresh(
                captureRestartInProgress: false,
                replayRunning: false,
                ReplayState.Stopped) &&
            !MainWindow.ShouldRetryPendingDisplayRefresh(
                captureRestartInProgress: false,
                replayRunning: true,
                ReplayState.Ready),
            "A settled or already-running replay kept an unnecessary 250 ms display-refresh retry alive.");
        return Task.CompletedTask;
    }

    private static (int Width, int Height, bool RequiresScaling) ResolveExpectedCaptureSize(
        int sourceWidth,
        int sourceHeight,
        ResolutionOption resolution)
    {
        var evenSourceWidth = sourceWidth - sourceWidth % 2;
        var evenSourceHeight = sourceHeight - sourceHeight % 2;
        if (resolution.Width is not { } maximumWidth || resolution.Height is not { } maximumHeight ||
            (sourceWidth <= maximumWidth && sourceHeight <= maximumHeight))
        {
            return (evenSourceWidth, evenSourceHeight, false);
        }

        int width;
        int height;
        if ((long)maximumWidth * sourceHeight <= (long)maximumHeight * sourceWidth)
        {
            width = maximumWidth;
            height = RoundExpectedToEven(sourceHeight * (double)maximumWidth / sourceWidth);
        }
        else
        {
            height = maximumHeight;
            width = RoundExpectedToEven(sourceWidth * (double)maximumHeight / sourceHeight);
        }

        return (
            Math.Min(evenSourceWidth, Math.Min(maximumWidth - maximumWidth % 2, width)),
            Math.Min(evenSourceHeight, Math.Min(maximumHeight - maximumHeight % 2, height)),
            width != evenSourceWidth || height != evenSourceHeight);
    }

    private static int RoundExpectedToEven(double value) =>
        Math.Max(2, (int)Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2);

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
            nvenc.Any(argument => argument.EndsWith(
                "format=nv12,setpts=PTS-STARTPTS",
                StringComparison.Ordinal)),
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
                "gfxcapture=monitor_idx=3:capture_cursor=0:max_framerate=60",
                StringComparison.Ordinal)),
            "Windows Graphics Capture must retain the selected zero-based monitor index.");
        Assert.True(
            !graphicsNvenc.Contains("gdigrab", StringComparer.Ordinal),
            "The low-overhead graphics backend must not also start GDI capture.");
        Assert.True(
            !graphicsNvenc.Any(argument => argument.Contains("hwdownload", StringComparison.Ordinal)),
            "Direct graphics capture should keep frames on the GPU.");
        Assert.ContainsSequence(graphicsNvenc, "-vf", "setpts=PTS-STARTPTS");

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
                ":width=-2:height=-2:resize_mode=crop:scale_mode=point",
                StringComparison.Ordinal)),
            "A fixed preset that matches the native display must bypass WGC resizing.");
        Assert.True(
            graphicsNvenc.Any(argument => argument.Contains(
                ":width=1920:height=1080:resize_mode=scale:scale_mode=point",
                StringComparison.Ordinal)),
            "A smaller fixed preset must use the low-overhead WGC point scaler.");
        Assert.ContainsSequence(
            nativeFullHdGraphics,
            "-thread_queue_size", "2",
            "-f", "lavfi");
        Assert.ContainsSequence(
            graphicsNvenc,
            "-thread_queue_size", "4",
            "-f", "lavfi");

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
        Assert.ContainsSequence(
            graphicsQsvProbe,
            "-nostats",
            "-stats_period", "0.25",
            "-progress", "pipe:1");
        Assert.ContainsSequence(
            graphicsQsvProbe,
            "-vf",
            "setpts=PTS-STARTPTS");
        Assert.ContainsSequence(
            graphicsQsvProbe,
            "-fps_mode", "cfr",
            "-r", configuration.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-frames:v",
            (configuration.FramesPerSecond * FfmpegArgumentBuilder.CaptureProbeSeconds)
                .ToString(CultureInfo.InvariantCulture),
            "-an");
        Assert.True(
            FfmpegProbeRunner.IsProbeCadenceAcceptable(
                graphicsQsvProbe,
                new FfmpegProbeCadenceObservation(
                    FirstFrame: 1,
                    FirstFrameElapsed: TimeSpan.FromSeconds(2.5),
                    LastFrame: 180,
                    LastFrameElapsed: TimeSpan.FromSeconds(5.5)),
                out _),
            "A scaled graphics probe sustaining about 60 FPS after startup was rejected.");
        Assert.True(
            !FfmpegProbeRunner.IsProbeCadenceAcceptable(
                graphicsQsvProbe,
                new FfmpegProbeCadenceObservation(
                    FirstFrame: 1,
                    FirstFrameElapsed: TimeSpan.FromSeconds(0.1),
                    LastFrame: 180,
                    LastFrameElapsed: TimeSpan.FromSeconds(6.1)),
                out var slowProbeDiagnostic) &&
            slowProbeDiagnostic.Contains("required minimum", StringComparison.Ordinal),
            "A scaled graphics probe sustaining about 30 FPS was accepted.");
        Assert.True(
            FfmpegProbeRunner.IsProbeCadenceAcceptable(
                graphicsQsvProbe,
                new FfmpegProbeCadenceObservation(
                    FirstFrame: 1,
                    FirstFrameElapsed: TimeSpan.FromSeconds(5),
                    LastFrame: 180,
                    LastFrameElapsed: TimeSpan.FromSeconds(8)),
                out _),
            "Legitimate D3D startup time incorrectly reduced the sustained cadence result.");
        Assert.True(
            !FfmpegProbeRunner.IsProbeCadenceAcceptable(
                graphicsQsvProbe,
                observation: null,
                out var missingProgressDiagnostic) &&
            missingProgressDiagnostic.Contains("frame-progress", StringComparison.Ordinal),
            "A scaled graphics probe without progress evidence was accepted.");

        var gdiNvencStrategy = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.Gdi);
        var gdiProbe = FfmpegArgumentBuilder.BuildGdiCaptureProbeArguments(
            configuration,
            gdiNvencStrategy);
        Assert.ContainsSequence(
            gdiProbe,
            "-nostats",
            "-stats_period", "0.25",
            "-progress", "pipe:1");
        Assert.ContainsSequence(
            gdiProbe,
            "-thread_queue_size",
            FfmpegArgumentBuilder.CompatibilityVideoInputQueuePackets.ToString(
                CultureInfo.InvariantCulture),
            "-f", "gdigrab",
            "-draw_mouse", "0",
            "-framerate", "60",
            "-offset_x", "0",
            "-offset_y", "0",
            "-video_size", "2560x1440",
            "-i", "desktop");
        Assert.True(
            gdiProbe.Any(argument => argument.Equals(
                "scale=1920:1080:flags=fast_bilinear,format=nv12,setpts=PTS-STARTPTS",
                StringComparison.Ordinal)),
            "The GDI fallback probe did not exercise the production scaling and hardware pixel-format graph.");
        Assert.ContainsSequence(
            gdiProbe,
            "-fps_mode", "cfr",
            "-r", "60",
            "-g", "120",
            "-keyint_min", "120",
            "-force_key_frames", "expr:gte(t,n_forced*2)",
            "-frames:v", "180",
            "-an",
            "-f", "null", "NUL");
        Assert.True(
            !gdiProbe.Any(argument => argument.Contains(
                "gfxcapture=",
                StringComparison.Ordinal)),
            "The production GDI probe unexpectedly started a WGC source.");
        Assert.True(
            FfmpegProbeRunner.IsProbeCadenceAcceptable(
                gdiProbe,
                new FfmpegProbeCadenceObservation(
                    FirstFrame: 1,
                    FirstFrameElapsed: TimeSpan.FromSeconds(0.25),
                    LastFrame: 180,
                    LastFrameElapsed: TimeSpan.FromSeconds(3.25)),
                out _),
            "A production GDI graph sustaining about 60 FPS was rejected.");
        Assert.True(
            !FfmpegProbeRunner.IsProbeCadenceAcceptable(
                gdiProbe,
                new FfmpegProbeCadenceObservation(
                    FirstFrame: 1,
                    FirstFrameElapsed: TimeSpan.FromSeconds(0.25),
                    LastFrame: 180,
                    LastFrameElapsed: TimeSpan.FromSeconds(6.25)),
                out var slowGdiDiagnostic) &&
            slowGdiDiagnostic.Contains(
                "required minimum",
                StringComparison.Ordinal),
            "A production GDI graph sustaining only about 30 FPS was accepted.");
        Assert.True(
            !FfmpegProbeRunner.IsProbeCadenceAcceptable(
                gdiProbe,
                observation: null,
                out var missingGdiProgressDiagnostic) &&
            missingGdiProgressDiagnostic.Contains(
                "frame-progress",
                StringComparison.Ordinal),
            "A GDI fallback probe without throughput evidence was accepted.");
        Assert.True(
            FfmpegProbeRunner.TryResolveCaptureProbePolicy(
                gdiProbe,
                out var resolvedGdiProbeStrategy,
                out var resolvedGdiScaling,
                out var resolvedGdiProfile) &&
            resolvedGdiProbeStrategy == gdiNvencStrategy &&
            resolvedGdiScaling &&
            resolvedGdiProfile == CapturePerformanceProfile.LowImpact,
            "The GDI fallback probe did not resolve to the live capture priority policy.");
        Assert.Equal(
            ProcessTuning.GetCaptureCpuPriority(
                gdiNvencStrategy,
                captureOutputRequiresScaling: true),
            ProcessTuning.GetCaptureCpuPriority(
                resolvedGdiProbeStrategy,
                resolvedGdiScaling,
                resolvedGdiProfile),
            "The GDI fallback probe CPU priority differs from live capture.");
        Assert.Equal(
            ProcessTuning.GetCaptureGraphicsPriority(
                gdiNvencStrategy,
                captureOutputRequiresScaling: true),
            ProcessTuning.GetCaptureGraphicsPriority(
                resolvedGdiProbeStrategy,
                resolvedGdiScaling,
                resolvedGdiProfile),
            "The GDI fallback probe GPU priority differs from live capture.");

        var customSourceConfiguration = configuration with
        {
            Display = configuration.Display with
            {
                Left = -1290,
                Top = 100,
                Width = 1290,
                Height = 980
            },
            Resolution = ResolutionOption.All.Single(option =>
                option.Id == "source")
        };
        var customSourceGdiProbe =
            FfmpegArgumentBuilder.BuildGdiCaptureProbeArguments(
                customSourceConfiguration,
                VideoEncodingStrategy.SoftwareGdi);
        Assert.ContainsSequence(
            customSourceGdiProbe,
            "-offset_x", "-1290",
            "-offset_y", "100",
            "-video_size", "1290x980");
        Assert.True(
            customSourceGdiProbe.Any(argument => argument.Equals(
                "null,format=yuv420p,setpts=PTS-STARTPTS",
                StringComparison.Ordinal)),
            "A custom Source GDI probe introduced scaling that live capture would not use.");
        Assert.Equal(ProcessPriorityClass.BelowNormal, ProcessTuning.CapturePriority,
            "Capture processes should yield CPU time to the foreground game.");
        Assert.Equal(ProcessPriorityClass.BelowNormal, ProcessTuning.HardwareCapturePriority,
            "Direct WGC/NVENC capture must also yield CPU time to the foreground game.");
        Assert.Equal(ProcessPriorityClass.Normal, ProcessTuning.ScaledCapturePriority,
            "Scaled WGC must keep enough CPU scheduling priority to sustain its D3D resize cadence.");
        Assert.Equal(
            GraphicsSchedulingPriorityClass.BelowNormal,
            ProcessTuning.CaptureGraphicsPriority,
            "WGC, scaling and hardware encoding must yield GPU scheduling to DWM and the game.");
        var wgcStrategy = new VideoEncodingStrategy(
            VideoEncoderKind.NvidiaNvenc,
            DesktopCaptureBackend.WindowsGraphicsCapture);
        Assert.Equal(
            ProcessPriorityClass.Normal,
            ProcessTuning.GetCaptureCpuPriority(
                wgcStrategy,
                captureOutputRequiresScaling: true),
            "Scaled WGC must not be starved by a BelowNormal coordination thread.");
        Assert.Equal(
            ProcessPriorityClass.BelowNormal,
            ProcessTuning.GetCaptureCpuPriority(
                wgcStrategy,
                captureOutputRequiresScaling: false),
            "Native Source WGC should retain the low-impact CPU policy.");
        Assert.Equal(
            ProcessPriorityClass.Normal,
            ProcessTuning.GetCaptureCpuPriority(
                VideoEncodingStrategy.SoftwareGdi,
                captureOutputRequiresScaling: true),
            "Scaled GDI capture must keep enough CPU scheduling priority to sustain real-time cadence.");
        Assert.Equal(
            ProcessPriorityClass.BelowNormal,
            ProcessTuning.GetCaptureCpuPriority(
                VideoEncodingStrategy.SoftwareGdi,
                captureOutputRequiresScaling: false,
                CapturePerformanceProfile.LowImpact),
            "Native LowImpact GDI capture should continue yielding CPU time to the foreground game.");
        Assert.Equal(
            ProcessPriorityClass.Normal,
            ProcessTuning.GetCaptureCpuPriority(
                VideoEncodingStrategy.SoftwareGdi,
                captureOutputRequiresScaling: false,
                CapturePerformanceProfile.Resilient),
            "A native GDI session promoted after measured pressure must use resilient CPU scheduling.");
        Assert.Equal(
            GraphicsSchedulingPriorityClass.Normal,
            ProcessTuning.GetCaptureGraphicsPriority(
                wgcStrategy,
                captureOutputRequiresScaling: true),
            "Scaled WGC must keep enough GPU scheduling priority to avoid game-time scaler starvation.");
        Assert.Equal(
            GraphicsSchedulingPriorityClass.BelowNormal,
            ProcessTuning.GetCaptureGraphicsPriority(
                wgcStrategy,
                captureOutputRequiresScaling: false),
            "Native Source WGC should retain the low-impact GPU policy.");
        Assert.Equal(
            GraphicsSchedulingPriorityClass.Normal,
            ProcessTuning.GetCaptureGraphicsPriority(
                VideoEncodingStrategy.SoftwareGdi,
                captureOutputRequiresScaling: true),
            "Scaled GDI capture must keep enough GPU scheduling priority to sustain real-time cadence.");
        Assert.Equal(
            GraphicsSchedulingPriorityClass.BelowNormal,
            ProcessTuning.GetCaptureGraphicsPriority(
                VideoEncodingStrategy.SoftwareGdi,
                captureOutputRequiresScaling: false,
                CapturePerformanceProfile.LowImpact),
            "Native LowImpact GDI capture should continue yielding GPU time to the foreground game.");
        Assert.Equal(
            GraphicsSchedulingPriorityClass.Normal,
            ProcessTuning.GetCaptureGraphicsPriority(
                VideoEncodingStrategy.SoftwareGdi,
                captureOutputRequiresScaling: false,
                CapturePerformanceProfile.Resilient),
            "A native GDI session promoted after measured pressure must use resilient GPU scheduling.");

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
        var resilientSourceQueueObserved = false;
        var resilientSourceRunner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            if (encoderName != "h264_nvenc")
            {
                return false;
            }

            if (!isGraphicsCapture)
            {
                return true;
            }

            resilientSourceQueueObserved =
                GetArgumentAfter(arguments, "-thread_queue_size") == "4";
            return resilientSourceQueueObserved;
        });
        var resilientSourceSelection = await new FfmpegCapabilityProbe(
                resilientSourceRunner)
            .SelectAsync(
                @"C:\Test\ffmpeg.exe",
                configuration with
                {
                    Resolution = ResolutionOption.All.Single(option =>
                        option.Id == "source")
                },
                CancellationToken.None,
                CapturePerformanceProfile.Resilient);
        Assert.True(
            resilientSourceQueueObserved &&
            resilientSourceSelection.Strategy.CaptureBackend ==
                DesktopCaptureBackend.WindowsGraphicsCapture,
            "Source safety probing did not exercise the production Resilient queue.");

        var resilientGdiRunner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            var isGdiCapture = arguments.Any(argument =>
                argument.Equals("gdigrab", StringComparison.Ordinal));
            return encoderName == "h264_nvenc" &&
                   (!isGraphicsCapture || isGdiCapture);
        });
        var resilientGdiSelection = await new FfmpegCapabilityProbe(
                resilientGdiRunner)
            .SelectAsync(
                @"C:\Test\ffmpeg.exe",
                configuration with
                {
                    Resolution = ResolutionOption.All.Single(option =>
                        option.Id == "source")
                },
                CancellationToken.None,
                CapturePerformanceProfile.Resilient);
        var resilientGdiInvocation =
            resilientGdiRunner.Invocations.Single(invocation =>
                invocation.Arguments.Any(argument =>
                    argument.Equals("gdigrab", StringComparison.Ordinal)));
        Assert.True(
            resilientGdiSelection.Strategy.CaptureBackend ==
                DesktopCaptureBackend.Gdi &&
            resilientGdiInvocation.CapturePerformanceProfile ==
                CapturePerformanceProfile.Resilient,
            "The real GDI fallback probe did not inherit the live Resilient priority policy.");

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

        var probedGdiEncoders = new List<string>();
        var validatedGdiRunner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v") ?? string.Empty;
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            var isGdiCapture = arguments.Any(argument =>
                argument.Equals("gdigrab", StringComparison.Ordinal));
            if (isGdiCapture)
            {
                probedGdiEncoders.Add(encoderName);
                return encoderName == "h264_qsv";
            }

            if (isGraphicsCapture)
            {
                return false;
            }

            return encoderName is "h264_nvenc" or "h264_qsv";
        });
        var validatedGdiSelection = await new FfmpegCapabilityProbe(
                validatedGdiRunner)
            .SelectAsync(
                @"C:\Test\ffmpeg.exe",
                configuration,
                CancellationToken.None);
        Assert.SequenceEqual(
            new[] { "h264_nvenc", "h264_qsv" },
            probedGdiEncoders,
            "GDI fallback candidates were not production-probed in hardware preference order.");
        Assert.Equal(
            VideoEncoderKind.IntelQuickSync,
            validatedGdiSelection.Strategy.Encoder,
            "A hardware encoder whose real GDI graph failed was still selected.");
        Assert.Equal(
            DesktopCaptureBackend.Gdi,
            validatedGdiSelection.Strategy.CaptureBackend,
            "The first production-verified GDI fallback was not selected.");

        var noRealTimeCaptureRunner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isCaptureProbe = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal) ||
                argument.Equals("gdigrab", StringComparison.Ordinal));
            return encoderName == "h264_nvenc" && !isCaptureProbe;
        });
        var noRealTimeCaptureRejected = false;
        try
        {
            _ = await new FfmpegCapabilityProbe(noRealTimeCaptureRunner)
                .SelectAsync(
                    @"C:\Test\ffmpeg.exe",
                    configuration,
                    CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            noRealTimeCaptureRejected = exception.Message.Contains(
                "real-time capture path",
                StringComparison.Ordinal);
        }

        Assert.True(
            noRealTimeCaptureRejected,
            "Capability selection accepted an unverified software GDI fallback after every real capture graph failed.");

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

        var recoveredGraphicsCapture = false;
        var transientRunner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            var isTransfer = arguments.Any(argument =>
                argument.Contains("hwdownload", StringComparison.Ordinal));
            return encoderName == "h264_nvenc" &&
                   (!isGraphicsCapture ||
                    recoveredGraphicsCapture && !isTransfer);
        });
        var transientUtcNow = new DateTimeOffset(
            2026,
            7,
            25,
            10,
            0,
            0,
            TimeSpan.Zero);
        var transientProbe = new FfmpegCapabilityProbe(
            transientRunner,
            degradedCacheInitialDuration: TimeSpan.FromSeconds(10),
            degradedCacheMaximumDuration: TimeSpan.FromSeconds(40),
            getUtcNow: () => transientUtcNow);
        var degradedResult = await transientProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            DesktopCaptureBackend.Gdi,
            degradedResult.Strategy.CaptureBackend,
            "A transient WGC failure should initially retain the verified hardware GDI fallback.");
        var degradedProbeCalls = transientRunner.CallCount;

        recoveredGraphicsCapture = true;
        var cachedDegradedResult = await transientProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            DesktopCaptureBackend.Gdi,
            cachedDegradedResult.Strategy.CaptureBackend,
            "A rapid replay restart should reuse the short-lived degraded result.");
        Assert.Equal(
            degradedProbeCalls,
            transientRunner.CallCount,
            "An unexpired degraded result repeated the expensive capability probes.");

        transientUtcNow += TimeSpan.FromSeconds(11);
        var recoveredResult = await transientProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            DesktopCaptureBackend.WindowsGraphicsCapture,
            recoveredResult.Strategy.CaptureBackend,
            "An expired degraded result must be re-probed so transient WGC failures recover.");
        Assert.True(
            transientRunner.CallCount > degradedProbeCalls,
            "An expired degraded GDI result was incorrectly retained.");

        var recoveredProbeCalls = transientRunner.CallCount;
        var cachedRecoveredResult = await transientProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            recoveredResult.Strategy,
            cachedRecoveredResult.Strategy,
            "A recovered WGC strategy changed when read from the positive cache.");
        Assert.Equal(
            recoveredProbeCalls,
            transientRunner.CallCount,
            "A verified WGC recovery should be cached after the transient failure clears.");
    }

    private static async Task TestDegradedCapabilityCacheAsync()
    {
        var configuration = CreateCaptureConfiguration(monitorIndex: 1);
        var utcNow = new DateTimeOffset(
            2026,
            7,
            25,
            11,
            0,
            0,
            TimeSpan.Zero);
        var graphicsCaptureRecovered = false;
        var runner = new ScriptedProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            var isTransfer = arguments.Any(argument =>
                argument.Contains("hwdownload", StringComparison.Ordinal));
            return encoderName == "h264_nvenc" &&
                   (!isGraphicsCapture ||
                    graphicsCaptureRecovered && !isTransfer);
        });
        var probe = new FfmpegCapabilityProbe(
            runner,
            degradedCacheInitialDuration: TimeSpan.FromSeconds(10),
            degradedCacheMaximumDuration: TimeSpan.FromSeconds(20),
            positiveCacheDuration: TimeSpan.FromSeconds(30),
            getUtcNow: () => utcNow);

        var first = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            DesktopCaptureBackend.Gdi,
            first.Strategy.CaptureBackend,
            "The permanent-fallback regression must begin on GDI.");
        Assert.Equal(
            utcNow + TimeSpan.FromSeconds(10),
            first.CacheExpiresAtUtc,
            "The degraded selection did not expose its actual cache expiry to the replay service.");
        var firstProbeCalls = runner.CallCount;

        var rapidRestart = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            first.Strategy,
            rapidRestart.Strategy,
            "A rapid replay restart changed the cached degraded strategy.");
        Assert.Equal(
            firstProbeCalls,
            runner.CallCount,
            "A rapid replay restart repeated the full permanent-fallback probe latency.");

        var beforeCursorKeyProbe = runner.CallCount;
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration with
            {
                CaptureCursor = !configuration.CaptureCursor
            },
            CancellationToken.None);
        Assert.True(
            runner.CallCount > beforeCursorKeyProbe,
            "A capture-cursor graph change reused a capability result for different WGC arguments.");

        var beforeDisplayCoordinateKeyProbe = runner.CallCount;
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration with
            {
                Display = configuration.Display with
                {
                    Left = -2560,
                    Top = 120
                }
            },
            CancellationToken.None);
        Assert.True(
            runner.CallCount > beforeDisplayCoordinateKeyProbe,
            "A GDI desktop-coordinate change reused a capability result for different gdigrab arguments.");

        var beforeProfileKeyProbe = runner.CallCount;
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None,
            CapturePerformanceProfile.Resilient);
        Assert.True(
            runner.CallCount > beforeProfileKeyProbe,
            "A Resilient Source probe reused the cached LowImpact queue/priority graph.");

        var profileInvalidationRunner =
            new ScriptedProbeRunner(arguments =>
            {
                var encoderName = GetArgumentAfter(arguments, "-c:v");
                var isGraphicsCapture = arguments.Any(argument =>
                    argument.Contains(
                        "gfxcapture=",
                        StringComparison.Ordinal));
                return encoderName == "h264_nvenc" &&
                       !isGraphicsCapture;
            });
        var profileInvalidationProbe = new FfmpegCapabilityProbe(
            profileInvalidationRunner,
            degradedCacheInitialDuration: TimeSpan.FromSeconds(10),
            degradedCacheMaximumDuration: TimeSpan.FromSeconds(20),
            positiveCacheDuration: TimeSpan.FromSeconds(30),
            getUtcNow: () => utcNow);
        var lowImpactProfileSelection =
            await profileInvalidationProbe.SelectAsync(
                @"C:\Test\ffmpeg.exe",
                configuration,
                CancellationToken.None,
                CapturePerformanceProfile.LowImpact);
        var resilientProfileSelection =
            await profileInvalidationProbe.SelectAsync(
                @"C:\Test\ffmpeg.exe",
                configuration,
                CancellationToken.None,
                CapturePerformanceProfile.Resilient);
        var afterResilientProfileProbe =
            profileInvalidationRunner.CallCount;
        Assert.True(
            profileInvalidationProbe.Invalidate(
                @"C:\Test\ffmpeg.exe",
                configuration,
                lowImpactProfileSelection.Strategy,
                CapturePerformanceProfile.LowImpact),
            "The exact LowImpact capability entry was not invalidated.");
        _ = await profileInvalidationProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None,
            CapturePerformanceProfile.Resilient);
        Assert.Equal(
            afterResilientProfileProbe,
            profileInvalidationRunner.CallCount,
            "LowImpact launch-failure invalidation removed the independent Resilient capability entry.");
        Assert.True(
            profileInvalidationProbe.Invalidate(
                @"C:\Test\ffmpeg.exe",
                configuration,
                resilientProfileSelection.Strategy,
                CapturePerformanceProfile.Resilient),
            "The exact Resilient capability entry was not invalidated.");
        _ = await profileInvalidationProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None,
            CapturePerformanceProfile.Resilient);
        Assert.True(
            profileInvalidationRunner.CallCount >
                afterResilientProfileProbe,
            "Resilient launch-failure invalidation did not force a fresh Resilient capability probe.");

        var staleFlightRunner =
            new BlockingProbeRunner(arguments =>
            {
                var encoderName = GetArgumentAfter(arguments, "-c:v");
                var isGraphicsCapture = arguments.Any(argument =>
                    argument.Contains(
                        "gfxcapture=",
                        StringComparison.Ordinal));
                return encoderName == "h264_nvenc" &&
                       !isGraphicsCapture;
            });
        var staleFlightProbe = new FfmpegCapabilityProbe(
            staleFlightRunner,
            degradedCacheInitialDuration:
                TimeSpan.FromSeconds(10),
            degradedCacheMaximumDuration:
                TimeSpan.FromSeconds(20),
            getUtcNow: () => utcNow);
        var staleFlight = staleFlightProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None,
            CapturePerformanceProfile.LowImpact);
        await staleFlightRunner.FirstInvocationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        staleFlightProbe.InvalidateConfiguration(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CapturePerformanceProfile.LowImpact);
        staleFlightRunner.Release();
        _ = await staleFlight.ConfigureAwait(false);
        var staleFlightCalls = staleFlightRunner.CallCount;
        _ = await staleFlightProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None,
            CapturePerformanceProfile.LowImpact);
        Assert.True(
            staleFlightRunner.CallCount > staleFlightCalls,
            "A capability flight invalidated by session teardown repopulated the cache after the new session began.");

        var differentConfiguration = CreateCaptureConfiguration(monitorIndex: 2);
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            differentConfiguration,
            CancellationToken.None);
        Assert.True(
            runner.CallCount > firstProbeCalls,
            "A degraded result leaked across distinct capture-configuration cache keys.");

        utcNow += TimeSpan.FromSeconds(10);
        var beforeSecondProbe = runner.CallCount;
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.True(
            runner.CallCount > beforeSecondProbe,
            "The initial degraded-cache TTL did not expire.");
        var secondProbeCalls = runner.CallCount;

        utcNow += TimeSpan.FromSeconds(19);
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            secondProbeCalls,
            runner.CallCount,
            "The second degraded selection did not apply its bounded backoff.");

        utcNow += TimeSpan.FromSeconds(1);
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.True(
            runner.CallCount > secondProbeCalls,
            "The backed-off degraded cache did not expire.");
        var maximumBackoffProbeCalls = runner.CallCount;

        graphicsCaptureRecovered = true;
        utcNow += TimeSpan.FromSeconds(19);
        var beforeMaximumExpiry = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            DesktopCaptureBackend.Gdi,
            beforeMaximumExpiry.Strategy.CaptureBackend,
            "A recovery should wait only until the active bounded fallback TTL expires.");
        Assert.Equal(
            maximumBackoffProbeCalls,
            runner.CallCount,
            "The maximum degraded backoff expired too early.");

        utcNow += TimeSpan.FromSeconds(1);
        var recovered = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            DesktopCaptureBackend.WindowsGraphicsCapture,
            recovered.Strategy.CaptureBackend,
            "A transiently recovered WGC path was not selected after the maximum fallback TTL.");
        Assert.True(
            runner.CallCount > maximumBackoffProbeCalls,
            "Recovery after degraded-cache expiry did not run a fresh capability probe.");
        Assert.Equal(
            utcNow + TimeSpan.FromSeconds(30),
            recovered.CacheExpiresAtUtc,
            "A verified WGC result did not receive a bounded positive-cache lifetime.");

        var recoveredProbeCalls = runner.CallCount;
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            recoveredProbeCalls,
            runner.CallCount,
            "A verified WGC recovery was not promoted to the stable positive cache.");

        utcNow += TimeSpan.FromSeconds(29);
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(
            recoveredProbeCalls,
            runner.CallCount,
            "The bounded positive cache expired before its configured lifetime.");
        utcNow += TimeSpan.FromSeconds(1);
        var refreshedPositive = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.True(
            runner.CallCount > recoveredProbeCalls,
            "A verified WGC capability remained cached forever.");
        Assert.Equal(
            DesktopCaptureBackend.WindowsGraphicsCapture,
            refreshedPositive.Strategy.CaptureBackend,
            "The fresh positive-cache probe lost a still-working WGC path.");

        var refreshedPositiveProbeCalls = runner.CallCount;
        Assert.True(
            !probe.Invalidate(
                @"C:\Test\ffmpeg.exe",
                differentConfiguration,
                refreshedPositive.Strategy),
            "Capability invalidation removed a different configuration key.");
        Assert.True(
            !probe.Invalidate(
                @"C:\Test\ffmpeg.exe",
                configuration,
                VideoEncodingStrategy.SoftwareGdi),
            "Capability invalidation removed a cache entry for a different strategy.");
        Assert.True(
            probe.Invalidate(
                @"C:\Test\ffmpeg.exe",
                configuration,
                refreshedPositive.Strategy),
            "The exact failed capability cache entry was not invalidated.");
        _ = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.True(
            runner.CallCount > refreshedPositiveProbeCalls,
            "A launch-failure invalidation did not force one fresh capability probe.");

        var blockingRunner = new BlockingProbeRunner(arguments =>
        {
            var encoderName = GetArgumentAfter(arguments, "-c:v");
            var isGraphicsCapture = arguments.Any(argument =>
                argument.Contains("gfxcapture=", StringComparison.Ordinal));
            return encoderName == "h264_nvenc" && !isGraphicsCapture;
        });
        var singleFlightProbe = new FfmpegCapabilityProbe(
            blockingRunner,
            degradedCacheInitialDuration: TimeSpan.FromSeconds(10),
            degradedCacheMaximumDuration: TimeSpan.FromSeconds(20),
            getUtcNow: () => utcNow);
        var firstCaller = singleFlightProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        await blockingRunner.FirstInvocationStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var concurrentCaller = singleFlightProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        using var cancelledWaiterSource = new CancellationTokenSource();
        var cancelledWaiter = singleFlightProbe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            cancelledWaiterSource.Token);
        cancelledWaiterSource.Cancel();
        var cancellationObserved = false;
        try
        {
            _ = await cancelledWaiter.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        Assert.True(
            cancellationObserved,
            "Cancelling one capability-cache waiter did not release it from the single-flight gate.");
        blockingRunner.Release();
        var concurrentResults = await Task.WhenAll(firstCaller, concurrentCaller)
            .WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        Assert.True(
            concurrentResults.All(result =>
                result.Strategy.CaptureBackend == DesktopCaptureBackend.Gdi),
            "Concurrent capability callers observed different degraded strategies.");
        Assert.Equal(
            6,
            blockingRunner.CallCount,
            "Concurrent callers launched more than one permanent-fallback probe flight.");
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
        var productionGdiProbeObserved = false;
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
            var isGdiCapture = arguments.Any(argument =>
                argument.Equals("gdigrab", StringComparison.Ordinal));
            var isTransfer = arguments.Any(argument =>
                argument.Contains("hwdownload", StringComparison.Ordinal));
            productionGdiProbeObserved |= isGdiCapture;
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
        Assert.True(runner.CallCount is >= 1 and <= 6, "Runtime probing performed an unexpected number of checks.");
        Assert.Equal(
            expectedBackend == DesktopCaptureBackend.Gdi,
            productionGdiProbeObserved,
            "Capability selection did not limit the production GDI probe to an actual fallback decision.");

        var completedProbeCalls = runner.CallCount;
        var cachedResult = await probe.SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration,
            CancellationToken.None);
        Assert.Equal(result.Strategy, cachedResult.Strategy, "A cached probe changed strategy.");
        if (expectedBackend == DesktopCaptureBackend.WindowsGraphicsCapture)
        {
            Assert.Equal(
                completedProbeCalls,
                runner.CallCount,
                "A verified WGC capability probe should be cached.");
        }
        else
        {
            Assert.Equal(
                completedProbeCalls,
                runner.CallCount,
                "A rapid replay restart should reuse the short-lived degraded capability result.");
        }
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
        Assert.True(
            !remuxArguments.Contains("-movflags", StringComparer.Ordinal) &&
            !remuxArguments.Contains("+faststart", StringComparer.Ordinal),
            "A local replay remux must not rewrite the whole MP4 for faststart.");

        return Task.CompletedTask;
    }

    private static Task TestReplayExportTimelineValidationAsync()
    {
        var validationArguments = ReplayBufferService.BuildExportValidationArguments(
            @"C:\Clips\clip.partial.mp4");
        Assert.ContainsSequence(
            validationArguments,
            "-show_entries",
            "stream=codec_type,start_time,duration,avg_frame_rate,r_frame_rate,nb_frames:format=duration");
        Assert.Equal(
            @"C:\Clips\clip.partial.mp4",
            validationArguments[^1],
            "The validation input path must remain one exact argument.");

        const string healthy = """
            {
              "streams": [
                {
                  "codec_type": "video",
                  "r_frame_rate": "60/1",
                  "avg_frame_rate": "10800000/179999",
                  "start_time": "0.000000",
                  "duration": "179.999000",
                  "nb_frames": "10800"
                },
                {
                  "codec_type": "audio",
                  "start_time": "0.005000",
                  "duration": "180.010500"
                }
              ],
              "format": { "duration": "180.015500" }
            }
            """;
        Assert.True(
            ReplayBufferService.TryValidateExportProbe(
                healthy,
                TimeSpan.FromSeconds(180),
                60,
                expectedAudio: true,
                out var healthyFailure),
            $"A healthy export was rejected: {healthyFailure}");

        const string actualCorruptRenewalClip = """
            {
              "streams": [
                {
                  "codec_type": "video",
                  "r_frame_rate": "60/1",
                  "avg_frame_rate": "2159800/29061",
                  "start_time": "34.699000",
                  "duration": "145.305000",
                  "nb_frames": "10799"
                },
                {
                  "codec_type": "audio",
                  "start_time": "0.000000",
                  "duration": "179.999500"
                }
              ],
              "format": { "duration": "180.004000" }
            }
            """;
        Assert.True(
            !ReplayBufferService.TryValidateExportProbe(
                actualCorruptRenewalClip,
                TimeSpan.FromSeconds(180),
                60,
                expectedAudio: true,
                out var corruptFailure) &&
            corruptFailure.Contains("begins at", StringComparison.Ordinal),
            "The real late-video renewal corruption must be rejected before publication.");

        const string videoOnly = """
            {
              "streams": [
                {
                  "codec_type": "video",
                  "r_frame_rate": "60/1",
                  "avg_frame_rate": "60/1",
                  "start_time": "0",
                  "duration": "30",
                  "nb_frames": "1800"
                }
              ],
              "format": { "duration": "30" }
            }
            """;
        Assert.True(
            !ReplayBufferService.TryValidateExportProbe(
                videoOnly,
                TimeSpan.FromSeconds(30),
                60,
                expectedAudio: true,
                out var missingAudioFailure) &&
            missingAudioFailure.Contains("audio", StringComparison.OrdinalIgnoreCase),
            "An expected audio stream must not disappear silently.");
        Assert.True(
            ReplayBufferService.TryValidateExportProbe(
                videoOnly,
                TimeSpan.FromSeconds(30),
                60,
                expectedAudio: false,
                out _),
            "A deliberately silent capture should validate without audio.");
        Assert.True(
            !ReplayBufferService.TryValidateExportProbe(
                "[]",
                TimeSpan.FromSeconds(30),
                60,
                expectedAudio: false,
                out _),
            "A non-object ffprobe root must fail closed without throwing.");
        Assert.True(
            !ReplayBufferService.TryValidateExportProbe(
                "{not-json}",
                TimeSpan.FromSeconds(30),
                60,
                expectedAudio: false,
                out _),
            "Malformed ffprobe JSON must fail closed.");

        Assert.True(
            ReplayBufferService.ShouldRetainCompletedSegments(
                preserveCompletedSegments: true,
                reachedSegmentBoundary: true),
            "A healthy boundary-aligned maintenance renewal should retain the ring.");
        Assert.True(
            !ReplayBufferService.ShouldRetainCompletedSegments(
                preserveCompletedSegments: true,
                reachedSegmentBoundary: false) &&
            !ReplayBufferService.ShouldRetainCompletedSegments(
                preserveCompletedSegments: false,
                reachedSegmentBoundary: true),
            "Unaligned and health-triggered renewals must invalidate the old generation.");
        Assert.True(
            ReplayBufferService.ShouldDeferNonDestructiveCaptureRefresh(
                requireCompletedSegmentBoundary: true,
                reachedSegmentBoundary: false) &&
            !ReplayBufferService.ShouldDeferNonDestructiveCaptureRefresh(
                requireCompletedSegmentBoundary: true,
                reachedSegmentBoundary: true) &&
            !ReplayBufferService.ShouldDeferNonDestructiveCaptureRefresh(
                requireCompletedSegmentBoundary: false,
                reachedSegmentBoundary: false),
            "A non-destructive profile promotion can still stop capture without a completed segment boundary.");
        Assert.True(
            !ReplayBufferService.IsCaptureSegmentTrusted(42, 42) &&
            ReplayBufferService.IsCaptureSegmentTrusted(43, 42),
            "Only the startup head of each capture generation should be quarantined.");

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

            var fastCopyArguments = FfmpegArgumentBuilder.BuildTrimArguments(
                inputPath,
                outputPath,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                includeAudio: true,
                framesPerSecond: 60,
                VideoEncodingStrategy.SoftwareGdi,
                replayCoexisting: true,
                fastStreamCopyVerified: true);
            Assert.ContainsSequence(fastCopyArguments, "-ss", "2", "-i", inputPath, "-t", "4");
            Assert.ContainsSequence(fastCopyArguments, "-map", "0:v:0", "-map", "0:a:0?");
            Assert.ContainsSequence(fastCopyArguments, "-c", "copy");
            Assert.True(
                !fastCopyArguments.Contains("-c:v", StringComparer.Ordinal),
                "A GOP-aligned trim unexpectedly selected a video encoder.");
            Assert.True(
                !fastCopyArguments.Contains("-movflags", StringComparer.Ordinal),
                "A fast stream-copy trim must not pay for MP4 faststart relocation.");
            Assert.True(
                !fastCopyArguments.Contains("-readrate", StringComparer.Ordinal) &&
                !fastCopyArguments.Contains("-threads", StringComparer.Ordinal),
                "A packet-only trim must not inherit software replay throttling.");
            Assert.True(
                ClipTrimService.TryValidateKeyframeProbe(
                    """
                    {"packets":[{"pts_time":"2.000000","flags":"K__"}]}
                    """,
                    TimeSpan.FromSeconds(2),
                    framesPerSecond: 60),
                "An exact source keyframe was not accepted for fast trim.");
            Assert.True(
                !ClipTrimService.TryValidateKeyframeProbe(
                    """
                    {"packets":[{"pts_time":"1.966667","flags":"K__"},{"pts_time":"2.000000","flags":"___"}]}
                    """,
                    TimeSpan.FromSeconds(2),
                    framesPerSecond: 60),
                "Fast trim accepted a non-key packet at the requested start.");
            var keyframeProbeArguments = ClipTrimService.BuildKeyframeProbeArguments(
                inputPath,
                TimeSpan.FromSeconds(2),
                framesPerSecond: 60);
            Assert.ContainsSequence(
                keyframeProbeArguments,
                "-select_streams", "v:0",
                "-read_intervals", "2%+0.1",
                "-show_entries", "packet=pts_time,flags");

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

            var replayCoexistingArguments = FfmpegArgumentBuilder.BuildTrimArguments(
                inputPath,
                outputPath,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3),
                includeAudio: true,
                framesPerSecond: 60,
                VideoEncodingStrategy.SoftwareGdi,
                replayCoexisting: true);
            Assert.ContainsSequence(
                replayCoexistingArguments,
                "-filter_threads", "1",
                "-threads", "1",
                "-readrate", "1");
            Assert.ContainsSequence(replayCoexistingArguments, "-c:v", "libx264");
            Assert.Equal(
                2,
                replayCoexistingArguments.Count(argument => argument == "-threads"),
                "Replay-coexisting trim must constrain both decoding and software encoding.");
            Assert.True(
                replayCoexistingArguments
                    .Select((argument, index) => (argument, index))
                    .Where(item => item.argument == "-threads")
                    .All(item => item.index + 1 < replayCoexistingArguments.Count &&
                                 replayCoexistingArguments[item.index + 1] == "1"),
                "Every replay-coexisting trim thread limit must be one.");
            Assert.True(
                Array.IndexOf(replayCoexistingArguments.ToArray(), "-readrate") <
                Array.IndexOf(replayCoexistingArguments.ToArray(), "-i"),
                "Replay pacing must be applied before opening the trim input.");
            Assert.True(
                !replayCoexistingArguments.Any(argument =>
                    argument is "h264_nvenc" or "h264_qsv" or "h264_amf"),
                "The software fallback unexpectedly selected a hardware encoder.");

            var hardwareReplayArguments = FfmpegArgumentBuilder.BuildTrimArguments(
                inputPath,
                outputPath,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3),
                includeAudio: true,
                framesPerSecond: 60,
                new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.Gdi),
                replayCoexisting: true);
            Assert.ContainsSequence(hardwareReplayArguments, "-c:v", "h264_nvenc");
            Assert.True(
                !hardwareReplayArguments.Contains("-readrate", StringComparer.Ordinal) &&
                !hardwareReplayArguments.Contains("-filter_threads", StringComparer.Ordinal) &&
                !hardwareReplayArguments.Contains("-threads", StringComparer.Ordinal),
                "A validated replay-time hardware encoder inherited software throttling.");
            Assert.True(
                !hardwareReplayArguments.Contains("copy", StringComparer.Ordinal),
                "A trim with only one GOP-aligned bound must remain frame-accurate.");

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

            var trustedBytes = new byte[] { 0x4D, 0x5A, 10, 20, 30, 40, 50, 60 };
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(trustedBytes));
            File.WriteAllBytes(fakePrivateTool, trustedBytes);
            var originalTimestamp = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(fakePrivateTool, originalTimestamp);
            originalTimestamp = File.GetLastWriteTimeUtc(fakePrivateTool);

            var cachedService = new FfmpegSetupService(testDirectory, expectedHash);
            Assert.Equal(
                Path.GetFullPath(fakePrivateTool),
                cachedService.FindExecutable(),
                "A private FFmpeg binary with the configured pinned hash should be discovered.");

            using (var verifiedLease = cachedService.OpenVerifiedExecutableLease(fakePrivateTool))
            {
                Assert.True(
                    verifiedLease is not null,
                    "The exact pinned FFmpeg file should produce a launch lease.");
                var replacementBlocked = false;
                try
                {
                    File.WriteAllBytes(fakePrivateTool, trustedBytes);
                }
                catch (IOException)
                {
                    replacementBlocked = true;
                }

                Assert.True(
                    replacementBlocked,
                    "The verified launch lease must deny executable replacement until Process.Start.");
            }

            var tamperedBytes = trustedBytes.ToArray();
            tamperedBytes[^1] ^= 0xFF;
            File.WriteAllBytes(fakePrivateTool, tamperedBytes);
            File.SetLastWriteTimeUtc(fakePrivateTool, originalTimestamp);
            Assert.Equal(
                Path.GetFullPath(fakePrivateTool),
                cachedService.FindExecutable(),
                "The test setup must preserve the cached length and timestamp stamp.");
            Assert.Equal<string?>(
                null,
                cachedService.FindExecutable(forceVerification: true),
                "Forced FFmpeg discovery must recompute SHA-256 and reject same-stamp tampering.");
            Assert.Equal<string?>(
                null,
                cachedService.FindExecutable(),
                "A failed forced verification must evict the stale trusted cache entry.");
            Assert.True(
                cachedService.OpenVerifiedExecutableLease(fakePrivateTool) is null,
                "A launch lease must reject same-stamp executable tampering.");

            Environment.SetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE", "1");
            using var developerLease = cachedService.OpenVerifiedExecutableLease(fakePrivateTool);
            Assert.True(
                developerLease is not null,
                "Explicit developer mode must still pin the selected external file handle without requiring the production hash.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH", previousPath);
            Environment.SetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE", previousDeveloperMode);
            DeleteTestDirectory(testDirectory);
        }

        return Task.CompletedTask;
    }

    private static Task TestTransactionalFfmpegToolPairAsync()
    {
        var testDirectory = CreateTestDirectory();
        var previousConfiguredPath =
            Environment.GetEnvironmentVariable("CLIPFORGE_FFMPEG_PATH");
        var previousDeveloperMode =
            Environment.GetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE");
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Directory.CreateDirectory(testDirectory);

            var discoveryDirectory = Path.Combine(testDirectory, "discovery");
            Directory.CreateDirectory(discoveryDirectory);
            var discoveredFfmpeg = Path.Combine(discoveryDirectory, "ffmpeg.exe");
            var discoveredFfprobe = Path.Combine(discoveryDirectory, "ffprobe.exe");
            File.WriteAllBytes(discoveredFfmpeg, [0x4D, 0x5A, 1]);
            Environment.SetEnvironmentVariable(
                "CLIPFORGE_FFMPEG_PATH",
                discoveryDirectory);
            Environment.SetEnvironmentVariable("CLIPFORGE_DEVELOPER_MODE", "1");
            Environment.SetEnvironmentVariable("PATH", string.Empty);

            var discoveryService = new FfmpegSetupService(
                Path.Combine(testDirectory, "unused-private-install"));
            Assert.True(
                !discoveryService.TryFindUsableToolPair(
                    out var incompleteFfmpeg,
                    out var incompleteFfprobe) &&
                incompleteFfmpeg == Path.GetFullPath(discoveredFfmpeg) &&
                incompleteFfprobe is null,
                "A usable FFmpeg without its sibling FFprobe must not complete installation.");

            File.WriteAllBytes(discoveredFfprobe, [0x4D, 0x5A, 2]);
            Assert.True(
                discoveryService.TryFindUsableToolPair(
                    out var completeFfmpeg,
                    out var completeFfprobe),
                "A complete usable FFmpeg/FFprobe pair was not discovered.");
            Assert.Equal(
                Path.GetFullPath(discoveredFfmpeg),
                completeFfmpeg,
                "Complete pair discovery returned the wrong FFmpeg path.");
            Assert.Equal(
                Path.GetFullPath(discoveredFfprobe),
                completeFfprobe,
                "Complete pair discovery returned the wrong FFprobe path.");

            var successfulRoot = Path.Combine(testDirectory, "successful-publish");
            Directory.CreateDirectory(successfulRoot);
            var successfulInstall = Path.Combine(successfulRoot, "install");
            var successfulPayload = Path.Combine(successfulRoot, "payload");
            WriteToolPair(successfulInstall, ffmpegMarker: 10, ffprobeMarker: 11);
            WriteToolPair(successfulPayload, ffmpegMarker: 20, ffprobeMarker: 21);
            var verificationCalls = 0;
            FfmpegSetupService.PublishPreparedInstallation(
                successfulPayload,
                successfulInstall,
                publishedDirectory =>
                {
                    verificationCalls++;
                    AssertToolPair(
                        publishedDirectory,
                        ffmpegMarker: 20,
                        ffprobeMarker: 21,
                        "Post-publish verification did not observe the complete new pair.");
                });
            Assert.Equal(
                1,
                verificationCalls,
                "The published FFmpeg pair must be verified exactly once.");
            AssertToolPair(
                successfulInstall,
                ffmpegMarker: 20,
                ffprobeMarker: 21,
                "A successful directory transaction did not publish both new tools.");
            AssertNoTransactionDirectories(
                successfulRoot,
                "A successful FFmpeg publication left transaction directories behind.");

            var rollbackRoot = Path.Combine(testDirectory, "verified-rollback");
            Directory.CreateDirectory(rollbackRoot);
            var rollbackInstall = Path.Combine(rollbackRoot, "install");
            var rollbackPayload = Path.Combine(rollbackRoot, "payload");
            WriteToolPair(rollbackInstall, ffmpegMarker: 30, ffprobeMarker: 31);
            WriteToolPair(rollbackPayload, ffmpegMarker: 40, ffprobeMarker: 41);
            Assert.Throws<InvalidDataException>(
                () => FfmpegSetupService.PublishPreparedInstallation(
                    rollbackPayload,
                    rollbackInstall,
                    publishedDirectory =>
                    {
                        AssertToolPair(
                            publishedDirectory,
                            ffmpegMarker: 40,
                            ffprobeMarker: 41,
                            "Rollback verification did not observe the complete staged pair.");
                        throw new InvalidDataException("Synthetic checksum failure.");
                    }),
                "A post-publish verification failure was not propagated.");
            AssertToolPair(
                rollbackInstall,
                ffmpegMarker: 30,
                ffprobeMarker: 31,
                "A verification failure did not restore both old tools.");
            AssertNoTransactionDirectories(
                rollbackRoot,
                "A completed FFmpeg rollback left transaction directories behind.");

            var firstInstallRoot = Path.Combine(testDirectory, "failed-first-install");
            Directory.CreateDirectory(firstInstallRoot);
            var firstInstallPath = Path.Combine(firstInstallRoot, "install");
            var firstInstallPayload = Path.Combine(firstInstallRoot, "payload");
            WriteToolPair(
                firstInstallPayload,
                ffmpegMarker: 45,
                ffprobeMarker: 46);
            Assert.Throws<InvalidDataException>(
                () => FfmpegSetupService.PublishPreparedInstallation(
                    firstInstallPayload,
                    firstInstallPath,
                    _ => throw new InvalidDataException(
                        "Synthetic checksum failure on first installation.")),
                "A first-install verification failure was not propagated.");
            Assert.True(
                !Directory.Exists(firstInstallPath),
                "A failed first installation left a partially published tool directory.");
            AssertNoTransactionDirectories(
                firstInstallRoot,
                "A failed first installation left transaction directories behind.");

            var failedRollbackRoot = Path.Combine(testDirectory, "failed-rollback");
            Directory.CreateDirectory(failedRollbackRoot);
            var failedRollbackInstall = Path.Combine(failedRollbackRoot, "install");
            var failedRollbackPayload = Path.Combine(failedRollbackRoot, "payload");
            WriteToolPair(
                failedRollbackInstall,
                ffmpegMarker: 50,
                ffprobeMarker: 51);
            WriteToolPair(
                failedRollbackPayload,
                ffmpegMarker: 60,
                ffprobeMarker: 61);
            var moveCount = 0;
            AggregateException? rollbackFailure = null;
            try
            {
                FfmpegSetupService.PublishPreparedInstallation(
                    failedRollbackPayload,
                    failedRollbackInstall,
                    _ => throw new InvalidDataException(
                        "Synthetic verification failure before rollback."),
                    (source, destination) =>
                    {
                        moveCount++;
                        if (moveCount == 4)
                        {
                            throw new IOException(
                                "Synthetic failure while restoring the backup.");
                        }

                        Directory.Move(source, destination);
                    });
            }
            catch (AggregateException exception)
            {
                rollbackFailure = exception;
            }

            Assert.True(
                rollbackFailure is not null &&
                rollbackFailure.InnerExceptions.Count == 2,
                "A rollback failure must preserve both the publish and rollback diagnostics.");
            var retainedBackups = Directory.GetDirectories(
                failedRollbackRoot,
                ".ffmpeg-backup-*",
                SearchOption.TopDirectoryOnly);
            Assert.Equal(
                1,
                retainedBackups.Length,
                "A failed rollback must retain exactly one old-install backup.");
            AssertToolPair(
                retainedBackups[0],
                ffmpegMarker: 50,
                ffprobeMarker: 51,
                "Rollback failure deleted or modified the retained old tool pair.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "CLIPFORGE_FFMPEG_PATH",
                previousConfiguredPath);
            Environment.SetEnvironmentVariable(
                "CLIPFORGE_DEVELOPER_MODE",
                previousDeveloperMode);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            DeleteTestDirectory(testDirectory);
        }

        return Task.CompletedTask;

        static void WriteToolPair(
            string directory,
            byte ffmpegMarker,
            byte ffprobeMarker)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, "ffmpeg.exe"),
                [0x4D, 0x5A, ffmpegMarker]);
            File.WriteAllBytes(
                Path.Combine(directory, "ffprobe.exe"),
                [0x4D, 0x5A, ffprobeMarker]);
        }

        static void AssertToolPair(
            string directory,
            byte ffmpegMarker,
            byte ffprobeMarker,
            string message)
        {
            Assert.SequenceEqual(
                new byte[] { 0x4D, 0x5A, ffmpegMarker },
                File.ReadAllBytes(Path.Combine(directory, "ffmpeg.exe")),
                message);
            Assert.SequenceEqual(
                new byte[] { 0x4D, 0x5A, ffprobeMarker },
                File.ReadAllBytes(Path.Combine(directory, "ffprobe.exe")),
                message);
        }

        static void AssertNoTransactionDirectories(
            string parentDirectory,
            string message)
        {
            Assert.True(
                !Directory.EnumerateDirectories(
                        parentDirectory,
                        ".ffmpeg-*",
                        SearchOption.TopDirectoryOnly)
                    .Any(),
                message);
        }
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

    private static Task TestLongRecordingPolicyAsync()
    {
        const long gibibyte = 1024L * 1024 * 1024;
        Assert.True(
            !RecordingStoragePolicy.HasStartCapacity(
                RecordingStoragePolicy.MinimumStartFreeBytes - 1),
            "Recorder accepted a drive below its minimum start reserve.");
        Assert.True(
            RecordingStoragePolicy.HasStartCapacity(
                RecordingStoragePolicy.MinimumStartFreeBytes),
            "Recorder rejected the exact minimum start reserve.");

        const long sessionBytes = 100L * 1024 * 1024 * 1024;
        Assert.Equal(
            sessionBytes + RecordingStoragePolicy.FinalizationSafetyReserveBytes,
            RecordingStoragePolicy.GetRequiredFinalizationFreeBytes(sessionBytes),
            "Finalization reserve did not include one complete output copy.");
        Assert.True(
            !RecordingStoragePolicy.ShouldFinalize(
                TimeSpan.FromHours(12),
                sessionBytes,
                sessionBytes + 5 * gibibyte),
            "Recorder stopped before reaching its projected-write headroom.");
        Assert.True(
            RecordingStoragePolicy.ShouldFinalize(
                TimeSpan.FromHours(12),
                sessionBytes,
                sessionBytes + 4 * gibibyte),
            "Recorder did not stop with enough headroom left for finalization.");
        Assert.True(
            RecordingStoragePolicy.ShouldFinalize(
                RecordingStoragePolicy.MaximumDuration,
                0,
                long.MaxValue),
            "The 24-hour safety limit did not request finalization.");
        Assert.Equal(
            long.MaxValue,
            RecordingStoragePolicy.GetRequiredFinalizationFreeBytes(long.MaxValue),
            "Finalization free-space math overflowed instead of saturating.");

        const int twelveHourSegmentCount = 21_600;
        var manifest = ReplayBufferService.BuildConcatManifestLines(
            Enumerable.Range(0, twelveHourSegmentCount)
                .Select(index => $@"C:\Recorder\segment-{index:D9}.mkv"));
        Assert.Equal(
            twelveHourSegmentCount * 2,
            manifest.Count,
            "A logical 12-hour session produced the wrong concat manifest size.");
        Assert.Equal(
            "file 'C:/Recorder/segment-000000000.mkv'",
            manifest[0],
            "The 12-hour manifest did not preserve its first segment.");
        Assert.Equal(
            "duration 2.000000",
            manifest[^1],
            "The 12-hour manifest lost its fixed final segment cadence.");
        Assert.Equal(
            RecordingStoragePolicy.EngineRetention,
            RecordingStoragePolicy.MaximumDuration + TimeSpan.FromMinutes(5),
            "Recorder engine retention no longer covers the complete safety window.");

        return Task.CompletedTask;
    }

    private static async Task TestRecorderRecoveryStateAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var saveDirectory = Path.Combine(testDirectory, "Clips");
            var recordingRoot = RecordingStoragePolicy.GetWorkingRoot(saveDirectory);
            var sessionDirectory = Path.Combine(
                recordingRoot,
                "session-20260804-120000-00000000000000000000000000000000");
            Directory.CreateDirectory(sessionDirectory);
            var segmentPaths = Enumerable.Range(0, 4)
                .Select(index => Path.Combine(
                    sessionDirectory,
                    $"segment-{index:D9}.mkv"))
                .ToArray();
            foreach (var path in segmentPaths)
            {
                await File.WriteAllBytesAsync(path, [1, 2, 3, 4])
                    .ConfigureAwait(false);
            }

            var statePath = Path.Combine(
                sessionDirectory,
                ".clipforge-recording-recovery.json");
            await File.WriteAllTextAsync(
                    statePath,
                    JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        SessionDirectory = sessionDirectory,
                        SegmentPaths = segmentPaths,
                        SegmentBytes = 16,
                        FramesPerSecond = 60,
                        HasAudio = true
                    }))
                .ConfigureAwait(false);

            ReplayStateSnapshot? recoveredState = null;
            await using (var service = new ReplayBufferService(
                             new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                             Path.Combine(testDirectory, "ReplayBuffer"),
                             () => Task.CompletedTask))
            {
                service.StateChanged += (_, snapshot) => recoveredState = snapshot;
                Assert.True(
                    await service.TryLoadPendingRecordingAsync(
                            saveDirectory,
                            CancellationToken.None)
                        .ConfigureAwait(false),
                    "A valid stopped Recorder session was not discovered after restart.");
                Assert.True(
                    service.HasPendingRecording && service.HasUnfinishedRecording,
                    "Recovered Recorder state was not retained for Retry save.");
                Assert.Equal(
                    CaptureSessionMode.Recording,
                    service.ActiveSessionMode,
                    "Recovered session did not restore Recorder identity.");
                Assert.Equal(
                    sessionDirectory,
                    service.PendingRecordingDirectory,
                    "Recovered session directory changed identity.");
                Assert.Equal(
                    ReplayState.Faulted,
                    recoveredState?.State,
                    "Recovered Recorder did not surface its waiting-to-save state.");
                Assert.Equal(
                    16L,
                    recoveredState?.BufferBytes,
                    "Recovery recomputation did not retain segment bytes.");
            }

            var invalidSaveDirectory = Path.Combine(testDirectory, "InvalidClips");
            var invalidRoot = RecordingStoragePolicy.GetWorkingRoot(invalidSaveDirectory);
            var invalidSession = Path.Combine(
                invalidRoot,
                "session-20260804-130000-11111111111111111111111111111111");
            Directory.CreateDirectory(invalidSession);
            var outsidePath = Path.Combine(testDirectory, "outside.mkv");
            await File.WriteAllBytesAsync(outsidePath, [5, 6, 7, 8]).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(invalidSession, ".clipforge-recording-recovery.json"),
                    JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        SessionDirectory = invalidSession,
                        SegmentPaths = new[] { outsidePath },
                        SegmentBytes = 4,
                        FramesPerSecond = 60,
                        HasAudio = false
                    }))
                .ConfigureAwait(false);
            await using var invalidService = new ReplayBufferService(
                new FfmpegSetupService(Path.Combine(testDirectory, "InvalidTools")),
                Path.Combine(testDirectory, "InvalidReplayBuffer"),
                () => Task.CompletedTask);
            Assert.True(
                !await invalidService.TryLoadPendingRecordingAsync(
                        invalidSaveDirectory,
                        CancellationToken.None)
                    .ConfigureAwait(false),
                "Recovery accepted a segment path outside its identity-bound session directory.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestCentralRecorderRecoveryJournalAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var serviceBufferRoot = Path.Combine(testDirectory, "ReplayBuffer");
            var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(serviceBufferRoot);
            var originalSaveDirectory = Path.Combine(testDirectory, "OriginalClips");
            var sourceBufferRoot =
                RecordingStoragePolicy.GetWorkingRoot(originalSaveDirectory);
            var sessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-140000-22222222222222222222222222222222");
            Directory.CreateDirectory(sessionDirectory);
            var segmentPaths = Enumerable.Range(0, 3)
                .Select(index => Path.Combine(
                    sessionDirectory,
                    $"segment-{index:D9}.mkv"))
                .ToArray();
            for (var index = 0; index < segmentPaths.Length; index++)
            {
                await File.WriteAllBytesAsync(
                        segmentPaths[index],
                        Enumerable.Repeat((byte)(index + 1), index + 4).ToArray())
                    .ConfigureAwait(false);
            }

            string journalPath;
            string sessionId;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             sessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 60,
                             hasAudio: true,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                journalPath = journal.JournalPath;
                sessionId = journal.SessionId;
                for (var index = 0; index < segmentPaths.Length; index++)
                {
                    Assert.True(
                        journal.RecordCompleted(
                            segmentPaths[index],
                            new FileInfo(segmentPaths[index]).Length,
                            index),
                        $"The recovery journal rejected trusted segment {index}.");
                }

                await journal.CloseAsync(detached: true).ConfigureAwait(false);
            }

            var markerPath = Path.Combine(
                sessionDirectory,
                RecordingRecoveryJournal.SessionMarkerFileName);
            Assert.Equal(
                sessionId,
                (await File.ReadAllTextAsync(markerPath).ConfigureAwait(false)).Trim(),
                "Recorder recovery did not bind the source directory to its central journal identity.");
            Assert.Equal(
                Path.GetFullPath(recoveryRoot),
                Path.GetDirectoryName(Path.GetFullPath(journalPath)),
                "Recorder recovery placed its journal outside the central buffer-root index.");

            var snapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                snapshot is
                {
                    SourceAvailable: true,
                    Detached: true,
                    FramesPerSecond: 60,
                    HasAudio: true
                },
                "The detached central journal did not restore its trusted session metadata.");
            Assert.Equal(
                sessionId,
                snapshot?.SessionId,
                "The recovered journal changed its marker-bound session identity.");
            Assert.Equal(
                sessionDirectory,
                snapshot?.SessionDirectory,
                "The recovered journal changed its identity-bound source directory.");
            Assert.SequenceEqual(
                segmentPaths,
                snapshot?.SegmentPaths ?? [],
                "The recovered journal changed the trusted segment ordering or paths.");
            Assert.True(
                await RecordingRecoveryJournal.TryReadAsync(
                        Path.Combine(testDirectory, "ForeignRecoveryRoot"),
                        journalPath,
                        CancellationToken.None)
                    .ConfigureAwait(false) is null,
                "Journal recovery accepted a journal path outside its declared central root.");

            var unsafeSessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-150000-33333333333333333333333333333333");
            Directory.CreateDirectory(unsafeSessionDirectory);
            var outsideSegmentPath = Path.Combine(testDirectory, "segment-000000000.mkv");
            await File.WriteAllBytesAsync(outsideSegmentPath, [9, 8, 7, 6])
                .ConfigureAwait(false);
            string unsafeJournalPath;
            await using (var unsafeJournal =
                         await RecordingRecoveryJournal.CreateAsync(
                                 recoveryRoot,
                                 unsafeSessionDirectory,
                                 sourceBufferRoot,
                                 framesPerSecond: 60,
                                 hasAudio: false,
                                 CancellationToken.None)
                             .ConfigureAwait(false))
            {
                unsafeJournalPath = unsafeJournal.JournalPath;
                Assert.True(
                    unsafeJournal.RecordCompleted(
                        outsideSegmentPath,
                        new FileInfo(outsideSegmentPath).Length,
                        segmentNumber: 0),
                    "The path-security fixture could not append its deliberately unsafe record.");
                await unsafeJournal.CloseAsync(detached: true).ConfigureAwait(false);
            }

            Assert.True(
                await RecordingRecoveryJournal.TryReadAsync(
                        recoveryRoot,
                        unsafeJournalPath,
                        CancellationToken.None)
                    .ConfigureAwait(false) is null,
                "Journal recovery accepted a completed segment outside its marker-bound session directory.");

            var differentCurrentSaveDirectory =
                Path.Combine(testDirectory, "CurrentClips");
            ReplayStateSnapshot? recoveredState = null;
            await using var service = new ReplayBufferService(
                new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                serviceBufferRoot,
                () => Task.CompletedTask);
            service.StateChanged += (_, state) => recoveredState = state;
            Assert.True(
                await service.TryLoadPendingRecordingAsync(
                        differentCurrentSaveDirectory,
                        CancellationToken.None)
                    .ConfigureAwait(false),
                "The central journal did not recover a detached session after SaveDirectory changed.");
            Assert.True(
                service.HasPendingRecording && service.HasUnfinishedRecording,
                "Central-journal recovery did not retain the stopped recording for Retry save.");
            Assert.Equal(
                CaptureSessionMode.Recording,
                service.ActiveSessionMode,
                "Central-journal recovery did not restore Recorder mode.");
            Assert.Equal(
                sessionDirectory,
                service.PendingRecordingDirectory,
                "SaveDirectory-independent recovery changed the source-session identity.");
            Assert.Equal(
                segmentPaths.Sum(path => new FileInfo(path).Length),
                recoveredState?.BufferBytes,
                "Central-journal recovery did not recompute the trusted segment bytes.");
            Assert.Equal(
                ReplayState.Faulted,
                recoveredState?.State,
                "The recovered detached recording did not surface the Retry-save state.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestUnavailableRecorderRecoveryJournalAsync()
    {
        var testDirectory = CreateTestDirectory();
        ReplayBufferService? service = null;
        string? unavailableSessionDirectory = null;
        try
        {
            var serviceBufferRoot = Path.Combine(testDirectory, "ReplayBuffer");
            var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(serviceBufferRoot);
            var originalSaveDirectory = Path.Combine(testDirectory, "DetachedDrive", "Clips");
            var sourceBufferRoot =
                RecordingStoragePolicy.GetWorkingRoot(originalSaveDirectory);
            unavailableSessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-160000-44444444444444444444444444444444");
            Directory.CreateDirectory(unavailableSessionDirectory);
            var segmentPath = Path.Combine(
                unavailableSessionDirectory,
                "segment-000000000.mkv");
            await File.WriteAllBytesAsync(segmentPath, [1, 3, 3, 7])
                .ConfigureAwait(false);

            string journalPath;
            string sessionId;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             unavailableSessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 60,
                             hasAudio: false,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                journalPath = journal.JournalPath;
                sessionId = journal.SessionId;
                Assert.True(
                    journal.RecordCompleted(
                        segmentPath,
                        new FileInfo(segmentPath).Length,
                        segmentNumber: 0),
                    "The unavailable-drive fixture could not append its trusted segment.");
                await journal.CloseAsync(detached: true).ConfigureAwait(false);
            }

            Assert.True(
                File.Exists(Path.Combine(
                    unavailableSessionDirectory,
                    RecordingRecoveryJournal.SessionMarkerFileName)),
                "The unavailable-drive fixture never established its session identity marker.");
            Directory.Delete(unavailableSessionDirectory, recursive: true);

            var unavailableSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                unavailableSnapshot is
                {
                    SourceAvailable: false,
                    Detached: true,
                    SegmentPaths.Count: 0,
                    SegmentBytes: 0
                },
                "The central journal discarded an unavailable source instead of preserving fail-closed recovery state.");
            Assert.Equal(
                sessionId,
                unavailableSnapshot?.SessionId,
                "Unavailable-source recovery changed the journal identity.");
            Assert.Equal(
                unavailableSessionDirectory,
                unavailableSnapshot?.SessionDirectory,
                "Unavailable-source recovery changed the marker-bound session path.");

            ReplayStateSnapshot? recoveredState = null;
            service = new ReplayBufferService(
                new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                serviceBufferRoot,
                () => Task.CompletedTask);
            service.StateChanged += (_, state) => recoveredState = state;
            Assert.True(
                await service.TryLoadPendingRecordingAsync(
                        Path.Combine(testDirectory, "DifferentCurrentClips"),
                        CancellationToken.None)
                    .ConfigureAwait(false),
                "The central journal hid a preserved recording while its source drive was unavailable.");
            Assert.True(
                service.HasPendingRecording && service.HasUnfinishedRecording,
                "An unavailable Recorder source did not keep the capture identity gate closed.");
            Assert.Equal(
                CaptureSessionMode.Recording,
                service.ActiveSessionMode,
                "Unavailable-source recovery did not retain Recorder identity.");
            Assert.Equal(
                unavailableSessionDirectory,
                service.PendingRecordingDirectory,
                "Unavailable-source recovery changed the pending session identity.");
            Assert.True(
                recoveredState is
                {
                    State: ReplayState.Faulted,
                    BufferBytes: 0
                } &&
                recoveredState?.Message?.Contains(
                    "source drive is unavailable",
                    StringComparison.OrdinalIgnoreCase) == true,
                "Unavailable-source recovery did not surface its reconnect-and-retry state.");

            InvalidOperationException? blockedStart = null;
            try
            {
                await service.StartAsync(
                        CreateCaptureConfiguration(monitorIndex: 0) with
                        {
                            SaveDirectory = Path.Combine(
                                testDirectory,
                                "DifferentCurrentClips")
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                blockedStart = exception;
            }

            Assert.True(
                blockedStart?.Message.Contains(
                    "waiting to be saved",
                    StringComparison.OrdinalIgnoreCase) == true,
                "An unavailable pending recording did not block creation of a new capture identity.");
            Assert.True(
                !service.IsRunning &&
                service.HasPendingRecording &&
                service.PendingRecordingDirectory == unavailableSessionDirectory,
                "The rejected capture start replaced or cleared the unavailable pending recording identity.");
        }
        finally
        {
            if (service is not null)
            {
                if (!string.IsNullOrWhiteSpace(unavailableSessionDirectory))
                {
                    Directory.CreateDirectory(unavailableSessionDirectory);
                }

                await service.DisposeAsync().ConfigureAwait(false);
            }

            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestZeroSegmentRecorderRecoveryAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var serviceBufferRoot = Path.Combine(testDirectory, "ReplayBuffer");
            var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(serviceBufferRoot);
            var sourceBufferRoot = RecordingStoragePolicy.GetWorkingRoot(
                Path.Combine(testDirectory, "OriginalClips"));
            var sessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-163000-45454545454545454545454545454545");
            Directory.CreateDirectory(sessionDirectory);
            var unclassifiedSegmentPath = Path.Combine(
                sessionDirectory,
                "segment-000000000.mkv");
            await File.WriteAllBytesAsync(unclassifiedSegmentPath, [4, 5, 4, 5])
                .ConfigureAwait(false);

            string journalPath;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             sessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 60,
                             hasAudio: false,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                journalPath = journal.JournalPath;
                Assert.True(
                    await journal.CloseAsync(detached: true).ConfigureAwait(false),
                    "The zero-segment fixture could not close its locator journal.");
            }

            await using var service = new ReplayBufferService(
                new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                serviceBufferRoot,
                () => Task.CompletedTask);
            Assert.True(
                await service.TryLoadPendingRecordingAsync(
                        Path.Combine(testDirectory, "DifferentCurrentClips"),
                        CancellationToken.None)
                    .ConfigureAwait(false),
                "A detached zero-segment locator was silently discarded instead of requiring an explicit decision.");
            Assert.True(
                service.HasPendingRecording &&
                service.PendingRecordingDirectory == sessionDirectory &&
                Directory.Exists(sessionDirectory) &&
                File.Exists(unclassifiedSegmentPath) &&
                File.Exists(journalPath),
                "Zero-segment recovery deleted or hid source data before explicit discard.");

            InvalidOperationException? retryResult = null;
            try
            {
                _ = await service.StopAndSaveRecordingAsync(
                        Path.Combine(testDirectory, "DifferentCurrentClips"),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                retryResult = exception;
            }

            Assert.True(
                retryResult?.Message.Contains(
                    "enough video to save",
                    StringComparison.OrdinalIgnoreCase) == true,
                "Zero-segment retry did not return the expected no-safe-video result.");
            Assert.True(
                service.HasPendingRecording &&
                service.CanDiscardIncompleteRecording &&
                !service.PendingRecordingHasSafeSegments &&
                Directory.Exists(sessionDirectory) &&
                File.Exists(unclassifiedSegmentPath) &&
                File.Exists(journalPath),
                "Retry save deleted or cleared the zero-safe-segment source before explicit discard.");

            IOException? blockedDiscardResult = null;
            await using (var segmentLease = new FileStream(
                             unclassifiedSegmentPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read))
            {
                try
                {
                    await service.DiscardIncompleteRecordingAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    blockedDiscardResult = exception;
                }

                Assert.True(
                    blockedDiscardResult is not null &&
                    service.HasPendingRecording &&
                    service.CanDiscardIncompleteRecording &&
                    Directory.Exists(sessionDirectory) &&
                    File.Exists(unclassifiedSegmentPath) &&
                    File.Exists(journalPath),
                    "A failed source-directory deletion cleared pending recovery or its journal.");
            }

            await service.DiscardIncompleteRecordingAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                !service.HasPendingRecording &&
                !Directory.Exists(sessionDirectory) &&
                !File.Exists(journalPath),
                "Confirmed zero-segment discard left a permanent pending-recovery lock.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestDiscardedRecorderRecoveryJournalAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var serviceBufferRoot = Path.Combine(testDirectory, "ReplayBuffer");
            var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(serviceBufferRoot);
            var sourceBufferRoot = RecordingStoragePolicy.GetWorkingRoot(
                Path.Combine(testDirectory, "OriginalClips"));

            var onlineSessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-164000-47474747474747474747474747474747");
            Directory.CreateDirectory(onlineSessionDirectory);
            await File.WriteAllBytesAsync(
                    Path.Combine(onlineSessionDirectory, "segment-000000000.mkv"),
                    [4, 7, 4, 7])
                .ConfigureAwait(false);
            string onlineJournalPath;
            string onlineSessionId;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             onlineSessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 60,
                             hasAudio: false,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                onlineJournalPath = journal.JournalPath;
                onlineSessionId = journal.SessionId;
                Assert.True(
                    await journal.CloseAsync(detached: true).ConfigureAwait(false),
                    "The online discarded-session fixture could not close its journal.");
            }

            await File.AppendAllTextAsync(
                    onlineJournalPath,
                    "{\"version\":1,\"kind\":\"add\"")
                .ConfigureAwait(false);
            var tornSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    onlineJournalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                tornSnapshot is { Discarded: false, Detached: true },
                "A torn final journal append hid the preceding durable recovery records.");

            await RecordingRecoveryJournal.MarkDiscardedAsync(
                    recoveryRoot,
                    onlineJournalPath,
                    onlineSessionId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var discardedSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    onlineJournalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                discardedSnapshot is
                {
                    Discarded: true,
                    SourceAvailable: true,
                    SegmentPaths.Count: 0,
                    SegmentBytes: 0
                },
                "The durable discard terminal did not supersede recoverable segment discovery.");
            var normalizedJournal = await File.ReadAllLinesAsync(onlineJournalPath)
                .ConfigureAwait(false);
            Assert.True(
                normalizedJournal.Length >= 3 &&
                !normalizedJournal.Any(line =>
                    line.Contains("\"kind\":\"add\"", StringComparison.Ordinal)) &&
                normalizedJournal[^1].Contains(
                    "\"kind\":\"discarded\"",
                    StringComparison.Ordinal) &&
                normalizedJournal[^1].Contains(
                    "\"sessionId\":\"" + onlineSessionId + "\"",
                    StringComparison.Ordinal),
                "Terminal append did not remove only the torn tail and persist the discard marker.");

            await using (var cleanupService = new ReplayBufferService(
                             new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                             serviceBufferRoot,
                             () => Task.CompletedTask))
            {
                Assert.True(
                    !await cleanupService.TryLoadPendingRecordingAsync(
                            Path.Combine(testDirectory, "DifferentCurrentClips"),
                            CancellationToken.None)
                        .ConfigureAwait(false) &&
                    !cleanupService.HasPendingRecording,
                    "A durable discarded session incorrectly restored the capture startup gate.");
            }

            Assert.True(
                !Directory.Exists(onlineSessionDirectory) &&
                !File.Exists(onlineJournalPath),
                "Startup did not finish cleanup for an available discarded source.");

            var unavailableSessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-164100-48484848484848484848484848484848");
            Directory.CreateDirectory(unavailableSessionDirectory);
            await File.WriteAllBytesAsync(
                    Path.Combine(unavailableSessionDirectory, "segment-000000000.mkv"),
                    [4, 8, 4, 8])
                .ConfigureAwait(false);
            string unavailableJournalPath;
            string unavailableSessionId;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             unavailableSessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 60,
                             hasAudio: false,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                unavailableJournalPath = journal.JournalPath;
                unavailableSessionId = journal.SessionId;
                Assert.True(
                    await journal.CloseAsync(detached: true).ConfigureAwait(false),
                    "The unavailable discarded-session fixture could not close its journal.");
            }

            await RecordingRecoveryJournal.MarkDiscardedAsync(
                    recoveryRoot,
                    unavailableJournalPath,
                    unavailableSessionId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Directory.Delete(unavailableSessionDirectory, recursive: true);

            await using (var crashGapService = new ReplayBufferService(
                             new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                             serviceBufferRoot,
                             () => Task.CompletedTask))
            {
                Assert.True(
                    !await crashGapService.TryLoadPendingRecordingAsync(
                            Path.Combine(testDirectory, "DifferentCurrentClips"),
                            CancellationToken.None)
                        .ConfigureAwait(false) &&
                    !crashGapService.HasPendingRecording &&
                    File.Exists(unavailableJournalPath),
                    "A crash between source and journal deletion resurrected an offline pending-recovery ghost.");
            }

            Directory.CreateDirectory(unavailableSessionDirectory);
            await File.WriteAllTextAsync(
                    Path.Combine(
                        unavailableSessionDirectory,
                        RecordingRecoveryJournal.SessionMarkerFileName),
                    unavailableSessionId)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    Path.Combine(unavailableSessionDirectory, "segment-000000000.mkv"),
                    [8, 4, 8, 4])
                .ConfigureAwait(false);

            await using (var retryService = new ReplayBufferService(
                             new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                             serviceBufferRoot,
                             () => Task.CompletedTask))
            {
                Assert.True(
                    !await retryService.TryLoadPendingRecordingAsync(
                            Path.Combine(testDirectory, "DifferentCurrentClips"),
                            CancellationToken.None)
                        .ConfigureAwait(false) &&
                    !retryService.HasPendingRecording,
                    "A reconnected discarded source incorrectly restored pending recovery.");
            }

            Assert.True(
                !Directory.Exists(unavailableSessionDirectory) &&
                !File.Exists(unavailableJournalPath),
                "Discard cleanup was not retried after the owned source became available again.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static Task TestRecorderRecoveryJournalCandidateOrderingAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var recoveryRoot = Path.Combine(testDirectory, "RecorderRecovery");
            Directory.CreateDirectory(recoveryRoot);
            var oldestTimestamp = DateTime.UtcNow.AddDays(-10);
            for (var index = 0; index < 256; index++)
            {
                var path = Path.Combine(
                    recoveryRoot,
                    $"session-{index:D4}.jsonl");
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, oldestTimestamp.AddMinutes(index));
            }

            var newestPath = Path.Combine(recoveryRoot, "session-zzzz.jsonl");
            File.WriteAllText(newestPath, "{}");
            File.SetLastWriteTimeUtc(newestPath, DateTime.UtcNow);

            var candidates = RecordingRecoveryJournal.EnumerateCandidatePaths(recoveryRoot);
            Assert.Equal(
                256,
                candidates.Count,
                "Recorder recovery did not retain its bounded candidate limit.");
            Assert.True(
                candidates.Any(path => string.Equals(
                    path,
                    newestPath,
                    StringComparison.OrdinalIgnoreCase)),
                "Recorder recovery applied its candidate cap before selecting the newest journal.");
            Assert.Equal(
                Path.GetFullPath(newestPath),
                candidates[0],
                "Recorder recovery did not inspect the newest central journal first.");

            return Task.CompletedTask;
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestRecorderRecoveryJournalCloseFaultAsync()
    {
        var testDirectory = CreateTestDirectory();
        RecordingRecoveryJournal? journal = null;
        try
        {
            var recoveryRoot = Path.Combine(testDirectory, "RecorderRecovery");
            var sourceBufferRoot = RecordingStoragePolicy.GetWorkingRoot(
                Path.Combine(testDirectory, "Clips"));
            var sessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-164500-46464646464646464646464646464646");
            Directory.CreateDirectory(sessionDirectory);
            var segmentPath = Path.Combine(
                sessionDirectory,
                "segment-000000000.mkv");
            await File.WriteAllBytesAsync(segmentPath, [4, 6, 4, 6])
                .ConfigureAwait(false);

            journal = await RecordingRecoveryJournal.CreateAsync(
                    recoveryRoot,
                    sessionDirectory,
                    sourceBufferRoot,
                    framesPerSecond: 60,
                    hasAudio: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var streamField = typeof(RecordingRecoveryJournal).GetField(
                "_stream",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.True(
                streamField?.GetValue(journal) is FileStream,
                "The close-fault fixture could not access the journal stream.");
            await ((FileStream)streamField!.GetValue(journal)!).DisposeAsync()
                .ConfigureAwait(false);
            Assert.True(
                journal.RecordCompleted(
                    segmentPath,
                    new FileInfo(segmentPath).Length,
                    segmentNumber: 0),
                "The close-fault fixture could not queue its record before the writer observed the injected failure.");

            Assert.True(
                !await journal.CloseAsync(detached: true).ConfigureAwait(false),
                "CloseAsync reported success after an expected local journal I/O failure.");
            Assert.True(
                journal.WriterFailure is ObjectDisposedException,
                "CloseAsync did not retain the expected writer failure for diagnostics.");
            Assert.True(
                !await journal.CloseAsync(detached: true).ConfigureAwait(false),
                "A repeated CloseAsync rethrew or forgot the expected writer failure.");
            await journal.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (journal is not null)
            {
                await journal.DisposeAsync().ConfigureAwait(false);
            }

            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestRecorderRecoveryJournalCommitDurabilityAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var serviceBufferRoot = Path.Combine(testDirectory, "ReplayBuffer");
            var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(serviceBufferRoot);
            var sourceBufferRoot = RecordingStoragePolicy.GetWorkingRoot(
                Path.Combine(testDirectory, "Clips"));
            var sessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-170000-55555555555555555555555555555555");
            Directory.CreateDirectory(sessionDirectory);
            var segmentPath = Path.Combine(
                sessionDirectory,
                "segment-000000000.mkv");
            await File.WriteAllBytesAsync(segmentPath, [1, 2, 3, 4])
                .ConfigureAwait(false);

            string journalPath;
            string sessionId;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             sessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 60,
                             hasAudio: false,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                journalPath = journal.JournalPath;
                sessionId = journal.SessionId;
                Assert.True(
                    journal.RecordCompleted(
                        segmentPath,
                        new FileInfo(segmentPath).Length,
                        segmentNumber: 0),
                    "The commit-durability fixture could not record its trusted source segment.");
                await journal.CloseAsync(detached: true).ConfigureAwait(false);
            }

            var outputDirectory = Path.Combine(testDirectory, "Exports");
            Directory.CreateDirectory(outputDirectory);
            var plannedOutputPath = Path.Combine(
                outputDirectory,
                "Recording_2026-08-04_17-00-00.mp4");
            await File.WriteAllBytesAsync(plannedOutputPath, [9, 9, 9, 9, 9])
                .ConfigureAwait(false);
            await RecordingRecoveryJournal.MarkCommittingAsync(
                    recoveryRoot,
                    journalPath,
                    sessionId,
                    plannedOutputPath,
                    CancellationToken.None)
                .ConfigureAwait(false);

            var plannedSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                plannedSnapshot is
                {
                    SourceAvailable: true,
                    Detached: true,
                    CommittedOutputPath: null,
                    SegmentPaths.Count: 1
                },
                "A planned commit incorrectly treated a pre-existing output file as durably committed.");
            Assert.Equal(
                segmentPath,
                plannedSnapshot?.SegmentPaths.Single(),
                "A planned commit discarded its still-recoverable source segment.");

            var committedLength = new FileInfo(plannedOutputPath).Length;
            await RecordingRecoveryJournal.MarkCommittedAsync(
                    recoveryRoot,
                    journalPath,
                    sessionId,
                    plannedOutputPath,
                    committedLength,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var committedSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.Equal(
                Path.GetFullPath(plannedOutputPath),
                committedSnapshot?.CommittedOutputPath,
                "A durable committed marker with the exact output length was not recognized.");
            Assert.True(
                committedSnapshot is { SegmentPaths.Count: 0, SegmentBytes: 0 },
                "A validated committed output did not supersede its recoverable source segments.");

            await File.AppendAllTextAsync(plannedOutputPath, "changed")
                .ConfigureAwait(false);
            var lengthMismatchSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                lengthMismatchSnapshot is
                {
                    SourceAvailable: true,
                    CommittedOutputPath: null,
                    SegmentPaths.Count: 1
                },
                "A committed marker accepted an output whose durable length no longer matched.");
            Assert.Equal(
                segmentPath,
                lengthMismatchSnapshot?.SegmentPaths.Single(),
                "An output-length mismatch discarded the recoverable source identity.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestExactDetachedRecoveryPreferredAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var serviceBufferRoot = Path.Combine(testDirectory, "ReplayBuffer");
            var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(serviceBufferRoot);
            var sourceBufferRoot = RecordingStoragePolicy.GetWorkingRoot(
                Path.Combine(testDirectory, "OriginalClips"));
            var sessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-180000-66666666666666666666666666666666");
            Directory.CreateDirectory(sessionDirectory);
            var segmentPaths = Enumerable.Range(0, 3)
                .Select(index => Path.Combine(
                    sessionDirectory,
                    $"segment-{index:D9}.mkv"))
                .ToArray();
            for (var index = 0; index < segmentPaths.Length; index++)
            {
                await File.WriteAllBytesAsync(
                        segmentPaths[index],
                        Enumerable.Repeat((byte)(index + 1), index + 4).ToArray())
                    .ConfigureAwait(false);
            }

            string journalPath;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             sessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 30,
                             hasAudio: false,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                journalPath = journal.JournalPath;
                Assert.True(
                    journal.RecordCompleted(
                        segmentPaths[0],
                        new FileInfo(segmentPaths[0]).Length,
                        segmentNumber: 0),
                    "The stale-journal fixture could not record its first segment.");
                await journal.CloseAsync(detached: true).ConfigureAwait(false);
            }

            await File.AppendAllTextAsync(
                    journalPath,
                    "{\"version\":1,\"kind\":\"add\"")
                .ConfigureAwait(false);
            var staleSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                staleSnapshot is
                {
                    SourceAvailable: true,
                    Detached: true,
                    SegmentPaths.Count: 1
                },
                "The torn-tail central journal fixture did not retain its earlier durable checkpoint.");

            var exactSegmentBytes = segmentPaths.Sum(path => new FileInfo(path).Length);
            await File.WriteAllTextAsync(
                    Path.Combine(
                        sessionDirectory,
                        ".clipforge-recording-recovery.json"),
                    JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        SessionDirectory = sessionDirectory,
                        SegmentPaths = segmentPaths,
                        SegmentBytes = long.MaxValue,
                        FramesPerSecond = 60,
                        HasAudio = true
                    }))
                .ConfigureAwait(false);

            ReplayStateSnapshot? recoveredState = null;
            await using var service = new ReplayBufferService(
                new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                serviceBufferRoot,
                () => Task.CompletedTask);
            service.StateChanged += (_, state) => recoveredState = state;
            Assert.True(
                await service.TryLoadPendingRecordingAsync(
                        Path.Combine(testDirectory, "DifferentCurrentClips"),
                        CancellationToken.None)
                    .ConfigureAwait(false),
                "Recovery did not discover the exact detached state behind its stale central locator.");
            Assert.True(
                service.HasPendingRecording &&
                service.PendingRecordingDirectory == sessionDirectory,
                "Exact detached recovery changed or dropped its session identity.");
            Assert.Equal(
                exactSegmentBytes,
                recoveredState?.BufferBytes,
                "Recovery trusted the stale journal subset instead of the exact detached segment list.");
            Assert.True(
                (recoveredState?.BufferBytes ?? 0) >
                (staleSnapshot?.SegmentBytes ?? long.MaxValue),
                "The exact detached state did not supersede the truncated journal checkpoint.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestRecorderJournalPartialSegmentRecoveryAsync()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var serviceBufferRoot = Path.Combine(testDirectory, "ReplayBuffer");
            var recoveryRoot = RecordingRecoveryJournal.GetRecoveryRoot(serviceBufferRoot);
            var sourceBufferRoot = RecordingStoragePolicy.GetWorkingRoot(
                Path.Combine(testDirectory, "Clips"));
            var sessionDirectory = Path.Combine(
                sourceBufferRoot,
                "session-20260804-190000-77777777777777777777777777777777");
            Directory.CreateDirectory(sessionDirectory);
            var segmentPaths = Enumerable.Range(0, 3)
                .Select(index => Path.Combine(
                    sessionDirectory,
                    $"segment-{index:D9}.mkv"))
                .ToArray();
            for (var index = 0; index < segmentPaths.Length; index++)
            {
                await File.WriteAllBytesAsync(
                        segmentPaths[index],
                        Enumerable.Repeat((byte)(index + 1), index + 4).ToArray())
                    .ConfigureAwait(false);
            }

            string journalPath;
            await using (var journal = await RecordingRecoveryJournal.CreateAsync(
                             recoveryRoot,
                             sessionDirectory,
                             sourceBufferRoot,
                             framesPerSecond: 60,
                             hasAudio: true,
                             CancellationToken.None)
                         .ConfigureAwait(false))
            {
                journalPath = journal.JournalPath;
                for (var index = 0; index < segmentPaths.Length; index++)
                {
                    Assert.True(
                        journal.RecordCompleted(
                            segmentPaths[index],
                            new FileInfo(segmentPaths[index]).Length,
                            index),
                        $"The partial-recovery fixture could not record segment {index}.");
                }

                await journal.CloseAsync(detached: true).ConfigureAwait(false);
            }

            File.Delete(segmentPaths[1]);
            var expectedRemainingPaths = new[] { segmentPaths[0], segmentPaths[2] };
            var expectedRemainingBytes = expectedRemainingPaths
                .Sum(path => new FileInfo(path).Length);
            var partialSnapshot = await RecordingRecoveryJournal.TryReadAsync(
                    recoveryRoot,
                    journalPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(
                partialSnapshot is
                {
                    SourceAvailable: true,
                    Detached: true,
                    MissingSegmentCount: 1
                },
                "One missing segment incorrectly made the online marker-bound session look offline.");
            Assert.SequenceEqual(
                expectedRemainingPaths,
                partialSnapshot?.SegmentPaths ?? [],
                "Partial journal recovery did not preserve the remaining trusted segment order.");
            Assert.Equal(
                expectedRemainingBytes,
                partialSnapshot?.SegmentBytes,
                "Partial journal recovery computed the wrong remaining source size.");

            ReplayStateSnapshot? recoveredState = null;
            await using var service = new ReplayBufferService(
                new FfmpegSetupService(Path.Combine(testDirectory, "Tools")),
                serviceBufferRoot,
                () => Task.CompletedTask);
            service.StateChanged += (_, state) => recoveredState = state;
            Assert.True(
                await service.TryLoadPendingRecordingAsync(
                        Path.Combine(testDirectory, "CurrentClips"),
                        CancellationToken.None)
                    .ConfigureAwait(false),
                "Replay recovery discarded an online session because one journaled segment was missing.");
            Assert.True(
                service.HasPendingRecording &&
                service.PendingRecordingDirectory == sessionDirectory,
                "Partial segment recovery did not retain the Recorder session for Retry save.");
            Assert.Equal(
                expectedRemainingBytes,
                recoveredState?.BufferBytes,
                "Replay recovery did not publish the remaining trusted segment bytes.");
            Assert.True(
                recoveredState?.Message?.Contains(
                    "without 1 missing segment",
                    StringComparison.OrdinalIgnoreCase) == true &&
                recoveredState?.Message?.Contains(
                    "source drive is unavailable",
                    StringComparison.OrdinalIgnoreCase) == false,
                "Partial segment recovery surfaced an offline-drive error instead of the bounded missing-segment warning.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestSettingsRoundtripAsync()
    {
        var testDirectory = CreateTestDirectory();

        try
        {
            using var service = new SettingsService(testDirectory);
            var missing = await service.LoadAsync().ConfigureAwait(false);
            Assert.Equal(
                SettingsLoadOutcome.Missing,
                missing.Outcome,
                "An absent settings file must be reported separately from a read failure.");
            var expected = new AppSettings
            {
                ReplaySeconds = 600,
                ResolutionId = "1440p",
                FramesPerSecond = 60,
                DisplayDeviceName = @"\\.\DISPLAY2",
                CaptureCursor = true,
                CaptureSystemAudio = false,
                OutputAudioDeviceId = "output-device",
                CaptureMicrophone = true,
                MicrophoneDeviceId = "microphone-device",
                StartReplayWithWindows = false,
                StartRecordingWithWindows = true,
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
            var loaded = await service.LoadAsync().ConfigureAwait(false);
            Assert.Equal(
                SettingsLoadOutcome.Loaded,
                loaded.Outcome,
                "A valid settings file must report a successful load.");
            var actual = loaded.Settings;

            Assert.True(File.Exists(service.SettingsPath), "The settings file was not created.");
            Assert.Equal(expected.ReplaySeconds, actual.ReplaySeconds, "Replay duration did not roundtrip.");
            Assert.Equal(expected.ResolutionId, actual.ResolutionId, "Resolution did not roundtrip.");
            Assert.Equal(expected.FramesPerSecond, actual.FramesPerSecond, "Frame rate did not roundtrip.");
            Assert.Equal(expected.DisplayDeviceName, actual.DisplayDeviceName, "Display did not roundtrip.");
            Assert.Equal(expected.CaptureCursor, actual.CaptureCursor, "Cursor capture did not roundtrip.");
            Assert.Equal(expected.CaptureSystemAudio, actual.CaptureSystemAudio, "System audio setting did not roundtrip.");
            Assert.Equal(expected.OutputAudioDeviceId, actual.OutputAudioDeviceId, "Output device did not roundtrip.");
            Assert.Equal(expected.CaptureMicrophone, actual.CaptureMicrophone, "Microphone setting did not roundtrip.");
            Assert.Equal(expected.MicrophoneDeviceId, actual.MicrophoneDeviceId, "Microphone device did not roundtrip.");
            Assert.Equal(
                expected.StartReplayWithWindows,
                actual.StartReplayWithWindows,
                "Windows autostart replay preference did not roundtrip.");
            Assert.Equal(
                expected.StartRecordingWithWindows,
                actual.StartRecordingWithWindows,
                "Windows autostart Recorder preference did not roundtrip.");
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

            var load = await service.LoadAsync().ConfigureAwait(false);
            Assert.Equal(
                SettingsLoadOutcome.Invalid,
                load.Outcome,
                "Malformed JSON must be classified as invalid, not as a transient read failure.");
            var settings = load.Settings;
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

            var load = await service.LoadAsync().ConfigureAwait(false);
            Assert.Equal(
                SettingsLoadOutcome.Invalid,
                load.Outcome,
                "Oversized settings must be classified as invalid.");
            var settings = load.Settings;
            Assert.Equal(120, settings.ReplaySeconds, "Settings larger than 1 MiB must be ignored before parsing.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestSettingsIoFallbackAsync()
    {
        var testDirectory = CreateTestDirectory();

        try
        {
            Directory.CreateDirectory(testDirectory);
            using var service = new SettingsService(testDirectory);
            await File.WriteAllTextAsync(
                    service.SettingsPath,
                    "{\"replaySeconds\":600}")
                .ConfigureAwait(false);
            await using var exclusiveLock = new FileStream(
                service.SettingsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var load = await service.LoadAsync().ConfigureAwait(false);
            Assert.Equal(
                SettingsLoadOutcome.TransientFailure,
                load.Outcome,
                "A temporary file lock must remain distinguishable from an explicit preference.");
            var settings = load.Settings;
            Assert.Equal(
                120,
                settings.ReplaySeconds,
                "A temporarily locked settings file should fall back to defaults.");

            var collisionDirectory = Path.Combine(testDirectory, "PathCollision");
            Directory.CreateDirectory(collisionDirectory);
            using var collisionService = new SettingsService(collisionDirectory);
            Directory.CreateDirectory(collisionService.SettingsPath);
            var collisionLoad = await collisionService.LoadAsync().ConfigureAwait(false);
            Assert.Equal(
                SettingsLoadOutcome.TransientFailure,
                collisionLoad.Outcome,
                "An inaccessible settings path must not be mistaken for a missing file.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
    }

    private static async Task TestSettingsConcurrentDisposalAsync()
    {
        var testDirectory = CreateTestDirectory();
        SettingsService? service = null;
        SemaphoreSlim? serializationGate = null;
        var ownsSerializationGate = false;

        try
        {
            service = new SettingsService(testDirectory);
            var gateField = typeof(SettingsService).GetField(
                "_gate",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.True(gateField is not null, "The settings serialization gate was not found.");
            serializationGate = gateField!.GetValue(service) as SemaphoreSlim;
            Assert.True(serializationGate is not null, "The settings serialization gate has an unexpected type.");

            await serializationGate!.WaitAsync().ConfigureAwait(false);
            ownsSerializationGate = true;
            var pendingSave = service.SaveAsync(new AppSettings
            {
                ReplaySeconds = 600,
                SaveDirectory = Path.Combine(testDirectory, "Clips")
            });
            Assert.True(
                !pendingSave.IsCompleted,
                "The disposal regression test must hold an in-flight settings operation.");

            service.Dispose();
            serializationGate.Release();
            ownsSerializationGate = false;
            await pendingSave.ConfigureAwait(false);
            Assert.True(
                File.Exists(service.SettingsPath),
                "An operation started before disposal should finish safely.");

            var rejectedAfterDisposal = false;
            try
            {
                _ = await service.LoadAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                rejectedAfterDisposal = true;
            }

            Assert.True(
                rejectedAfterDisposal,
                "A settings operation started after disposal must be rejected.");
        }
        finally
        {
            if (ownsSerializationGate)
            {
                serializationGate!.Release();
            }

            service?.Dispose();
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

            var missingCachedThumbnail = clips[0].ThumbnailPath
                ?? throw new InvalidOperationException("The generated thumbnail path was missing.");
            File.Delete(missingCachedThumbnail);
            var thumbnailRunsBeforeCachedOnlyLoad = runner.ThumbnailRunCount;
            var cachedOnlyLoad = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 5,
                    includeThumbnails: true,
                    filter: ClipLibraryFilter.All,
                    thumbnailPolicy: ClipThumbnailPolicy.CachedOnly)
                .ConfigureAwait(false);
            Assert.Equal(2, cachedOnlyLoad.Count,
                "Cached-only discovery must still return safely probed clips.");
            Assert.Equal(
                thumbnailRunsBeforeCachedOnlyLoad,
                runner.ThumbnailRunCount,
                "Cached-only discovery must never start FFmpeg for a missing thumbnail.");
            Assert.True(
                cachedOnlyLoad.Single(item => item.FileName == clips[0].FileName).ThumbnailPath is null,
                "A missing cached-only thumbnail must remain absent instead of being regenerated.");
            Assert.True(
                cachedOnlyLoad.Single(item => item.FileName == clips[1].FileName).ThumbnailPath is { } cachedPath &&
                File.Exists(cachedPath),
                "Cached-only discovery should continue exposing an existing valid thumbnail.");
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

    private static async Task TestReplayThumbnailHydrationAsync()
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

            var olderClipPath = Path.Combine(clipsDirectory, "Clip_2026-07-14_16-00-00.mp4");
            var newerClipPath = Path.Combine(clipsDirectory, "Clip_2026-07-14_16-01-00.mp4");
            await File.WriteAllBytesAsync(olderClipPath, [1, 2, 3, 4]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(newerClipPath, [5, 6, 7, 8]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(
                olderClipPath,
                new DateTime(2026, 7, 14, 16, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                newerClipPath,
                new DateTime(2026, 7, 14, 16, 1, 0, DateTimeKind.Utc));

            var runner = new FakeClipMediaProcessRunner
            {
                PauseThumbnailGeneration = true
            };
            var service = new ClipLibraryService(
                () => ffmpegPath,
                () => ffprobePath,
                runner,
                cacheDirectory,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3));

            var firstReplayRefresh = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 4,
                    includeThumbnails: true,
                    filter: ClipLibraryFilter.All,
                    thumbnailPolicy: ClipThumbnailPolicy.CachedOnly)
                .ConfigureAwait(false);
            Assert.Equal(2, firstReplayRefresh.Count,
                "Replay discovery must expose valid clips before their thumbnails are hydrated.");
            Assert.True(firstReplayRefresh.All(clip => clip.ThumbnailPath is null),
                "Uncached replay thumbnails should initially use the visual fallback.");
            Assert.Equal(0, runner.ThumbnailRunCount,
                "The initial cached-only replay pass must not start a thumbnail helper.");
            var probeRunsBeforeHydration = runner.Invocations.Count(invocation =>
                Path.GetFileName(invocation.ExecutablePath).Equals(
                    "ffprobe.exe",
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(
                LibraryWindow.ShouldDeferAutomaticMediaOpen(
                    replayRunning: true,
                    beginTrimWhenReady: false),
                "Thumbnail hydration must not regress the replay policy that defers the UI media decoder.");

            var zeroLimit = await service.HydrateThumbnailsAsync(
                    clipsDirectory,
                    firstReplayRefresh,
                    maximumMissingThumbnails: 0)
                .ConfigureAwait(false);
            Assert.True(zeroLimit.All(clip => clip.ThumbnailPath is null),
                "A zero hydration limit must preserve the cached-first placeholder snapshot.");
            Assert.Equal(0, runner.ThumbnailRunCount,
                "A zero hydration limit must not launch a media helper.");

            using (var alreadyCancelled = new CancellationTokenSource())
            {
                alreadyCancelled.Cancel();
                var cancellationObserved = false;
                try
                {
                    await service.HydrateThumbnailsAsync(
                            clipsDirectory,
                            firstReplayRefresh,
                            maximumMissingThumbnails: 1,
                            alreadyCancelled.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                }

                Assert.True(cancellationObserved,
                    "A cancelled foreground refresh must cancel replay thumbnail hydration.");
                Assert.Equal(0, runner.ThumbnailRunCount,
                    "Cancelled hydration must not launch a media helper.");
            }

            var invalidLimitRejected = false;
            try
            {
                await service.HydrateThumbnailsAsync(
                        clipsDirectory,
                        firstReplayRefresh,
                        maximumMissingThumbnails: 101)
                    .ConfigureAwait(false);
            }
            catch (ArgumentOutOfRangeException)
            {
                invalidLimitRejected = true;
            }

            Assert.True(invalidLimitRejected,
                "Thumbnail hydration must reject work beyond the bounded library limit.");

            var hydration = service.HydrateThumbnailsAsync(
                clipsDirectory,
                firstReplayRefresh,
                maximumMissingThumbnails: 1);

            try
            {
                await runner.ThumbnailStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
                Assert.True(!hydration.IsCompleted,
                    "Thumbnail generation should yield asynchronously while the low-priority helper is running.");
                Assert.Equal(ProcessPriorityClass.Idle, ProcessTuning.AuxiliaryMediaPriority,
                    "Replay thumbnail helpers must remain below the live capture process priority.");

                // A blocked thumbnail helper must not monopolize the caller or the
                // thread pool used by independent replay/capture coordination work.
                var captureCoordinationPulse = await Task.Run(static () => 42)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
                Assert.Equal(42, captureCoordinationPulse,
                    "Independent capture coordination stalled behind thumbnail hydration.");
            }
            finally
            {
                runner.ReleaseThumbnailGeneration();
                try
                {
                    await hydration.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original assertion failure; the outer cleanup
                    // remains best-effort if the scripted helper itself failed.
                }
            }

            var partiallyHydrated = await hydration
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            Assert.True(
                partiallyHydrated[0].ThumbnailPath is { } thumbnailPath && File.Exists(thumbnailPath),
                "A steady replay refresh should eventually publish its generated thumbnail.");
            Assert.True(partiallyHydrated[1].ThumbnailPath is null,
                "Thumbnail hydration must honor its missing-item limit.");
            Assert.True(firstReplayRefresh.All(clip => clip.ThumbnailPath is null),
                "Background hydration must not mutate the already-rendered cached-first snapshot.");
            Assert.Equal(1, runner.ThumbnailRunCount,
                "One missing replay thumbnail should launch exactly one serialized helper.");

            var fullyHydrated = await service.HydrateThumbnailsAsync(
                    clipsDirectory,
                    partiallyHydrated,
                    maximumMissingThumbnails: 4)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            Assert.True(fullyHydrated.All(clip =>
                    clip.ThumbnailPath is { } path && File.Exists(path)),
                "A later bounded hydration pass should fill the remaining replay thumbnail.");
            Assert.Equal(2, runner.ThumbnailRunCount,
                "Hydration must reuse the populated item and decode only the remaining thumbnail.");
            Assert.Equal(
                probeRunsBeforeHydration,
                runner.Invocations.Count(invocation =>
                    Path.GetFileName(invocation.ExecutablePath).Equals(
                        "ffprobe.exe",
                        StringComparison.OrdinalIgnoreCase)),
                "Hydrating an already validated snapshot must not repeat media probes.");

            var thumbnailRuns = runner.ThumbnailRunCount;
            var laterReplayRefresh = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 4,
                    includeThumbnails: true,
                    filter: ClipLibraryFilter.All,
                    thumbnailPolicy: ClipThumbnailPolicy.CachedOnly)
                .ConfigureAwait(false);
            Assert.Equal(
                fullyHydrated[0].ThumbnailPath,
                laterReplayRefresh[0].ThumbnailPath,
                "Later replay refreshes should surface the newest hydrated cache entry.");
            Assert.Equal(
                fullyHydrated[1].ThumbnailPath,
                laterReplayRefresh[1].ThumbnailPath,
                "Later replay refreshes should surface every hydrated deterministic cache entry.");
            Assert.Equal(thumbnailRuns, runner.ThumbnailRunCount,
                "A hydrated replay thumbnail must not launch another helper on cached-only refresh.");
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
            Assert.True(
                successRunner.Invocations.All(invocation =>
                    invocation.Priority == ClipMediaProcessPriority.Interactive),
                "User-requested trim probes/exports must not inherit background Idle priority.");
            Assert.True(!EnumerateTrimPartials(clipsDirectory).Any(),
                "A successful trim left a partial output behind.");

            var replayHardwareRunner = new FakeTrimMediaProcessRunner
            {
                AvailableHardwareEncoder = VideoEncoderKind.NvidiaNvenc
            };
            var replayHardwareService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                replayHardwareRunner);
            var replayHardware = await replayHardwareService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    ClipTrimExecutionMode.ReplayCoexisting)
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.Succeeded, replayHardware.Status,
                $"Hardware replay-coexisting trim failed: {replayHardware.Message}");
            Assert.Equal(1, replayHardwareRunner.TrimRunCount,
                "A validated hardware replay trim should launch one export.");
            var replayHardwareFfmpegInvocations = replayHardwareRunner.Invocations
                .Where(invocation => Path.GetFileName(invocation.ExecutablePath).Equals(
                    "ffmpeg.exe",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(2, replayHardwareFfmpegInvocations.Length,
                "A replay-time hardware trim should validate the selected encoder once before export.");
            Assert.True(
                replayHardwareFfmpegInvocations[0].Arguments.Contains("lavfi", StringComparer.Ordinal),
                "The replay-time hardware encoder was not capability-probed.");
            var replayHardwareTrimArguments = replayHardwareFfmpegInvocations[1].Arguments;
            Assert.ContainsSequence(replayHardwareTrimArguments, "-c:v", "h264_nvenc");
            Assert.True(
                !replayHardwareTrimArguments.Contains("-readrate", StringComparer.Ordinal) &&
                !replayHardwareTrimArguments.Contains("-filter_threads", StringComparer.Ordinal) &&
                !replayHardwareTrimArguments.Contains("-threads", StringComparer.Ordinal),
                "A validated replay-time hardware trim inherited software fallback throttling.");

            var fastCopyRunner = new FakeTrimMediaProcessRunner();
            var fastCopyService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                fastCopyRunner);
            var fastCopy = await fastCopyService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(6),
                    ClipTrimExecutionMode.ReplayCoexisting)
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.Succeeded, fastCopy.Status,
                $"GOP-aligned fast trim failed: {fastCopy.Message}");
            Assert.Equal(1, fastCopyRunner.TrimRunCount,
                "A GOP-aligned trim should launch one packet-copy export.");
            var fastCopyFfmpegInvocations = fastCopyRunner.Invocations
                .Where(invocation => Path.GetFileName(invocation.ExecutablePath).Equals(
                    "ffmpeg.exe",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(1, fastCopyFfmpegInvocations.Length,
                "A GOP-aligned trim unexpectedly ran hardware encoder probes.");
            var fastCopyTrimArguments = fastCopyFfmpegInvocations[0].Arguments;
            Assert.ContainsSequence(fastCopyTrimArguments, "-c", "copy");
            Assert.True(
                !fastCopyTrimArguments.Contains("-movflags", StringComparer.Ordinal) &&
                !fastCopyTrimArguments.Contains("-readrate", StringComparer.Ordinal) &&
                !fastCopyTrimArguments.Contains("lavfi", StringComparer.Ordinal),
                "A GOP-aligned packet copy inherited transcode-only work.");
            Assert.True(
                fastCopyRunner.Invocations.Any(invocation =>
                    Path.GetFileName(invocation.ExecutablePath).Equals(
                        "ffprobe.exe",
                        StringComparison.OrdinalIgnoreCase) &&
                    invocation.Arguments.Contains(
                        "packet=pts_time,flags",
                        StringComparer.Ordinal)),
                "A GOP-aligned trim copied packets without first verifying its source keyframe.");

            var fastCopyRetryRunner = new FakeTrimMediaProcessRunner
            {
                FailFirstTrimOnly = true
            };
            var fastCopyRetryService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                fastCopyRetryRunner);
            var fastCopyRetry = await fastCopyRetryService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(6),
                    ClipTrimExecutionMode.ReplayCoexisting)
                .ConfigureAwait(false);
            Assert.Equal(
                ClipTrimStatus.Succeeded,
                fastCopyRetry.Status,
                "A verified packet-copy failure should retry through exact encoding.");
            Assert.Equal(
                2,
                fastCopyRetryRunner.TrimRunCount,
                "A failed packet copy should launch exactly one encoded retry.");
            var fastCopyRetryArguments = fastCopyRetryRunner.Invocations
                .Where(invocation =>
                    Path.GetFileName(invocation.ExecutablePath).Equals(
                        "ffmpeg.exe",
                        StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(invocation.Arguments[^1]).StartsWith(
                        ".clipforge-trim-",
                        StringComparison.OrdinalIgnoreCase))
                .Select(invocation => invocation.Arguments)
                .ToArray();
            Assert.Equal(
                2,
                fastCopyRetryArguments.Length,
                "The packet-copy fallback launched an unexpected number of exports.");
            Assert.ContainsSequence(fastCopyRetryArguments[0], "-c", "copy");
            Assert.ContainsSequence(fastCopyRetryArguments[1], "-c:v", "libx264");
            Assert.True(
                !fastCopyRetryArguments[1].Contains("copy", StringComparer.Ordinal),
                "The exact fallback retained packet-copy arguments.");

            var invalidFastCopyRunner = new FakeTrimMediaProcessRunner
            {
                ReturnInvalidFirstTrimOutputMetadata = true
            };
            var invalidFastCopyService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                invalidFastCopyRunner);
            var invalidFastCopy = await invalidFastCopyService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(6),
                    ClipTrimExecutionMode.ReplayCoexisting)
                .ConfigureAwait(false);
            Assert.Equal(
                ClipTrimStatus.Succeeded,
                invalidFastCopy.Status,
                "A packet copy with invalid output metadata should retry through exact encoding.");
            Assert.Equal(
                2,
                invalidFastCopyRunner.TrimRunCount,
                "Invalid packet-copy output should launch exactly one encoded retry.");
            Assert.True(
                invalidFastCopy.OutputPath is not null &&
                File.Exists(invalidFastCopy.OutputPath),
                "The exact fallback did not commit its validated trim.");
            Assert.SequenceEqual(
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                File.ReadAllBytes(sourcePath),
                "Recovering from invalid packet-copy output changed the source bytes.");
            Assert.True(
                !EnumerateTrimPartials(clipsDirectory).Any(),
                "The invalid packet-copy fallback left a partial output behind.");

            var failedFastCopyFallbackRunner = new FakeTrimMediaProcessRunner
            {
                FailTrim = true
            };
            var failedFastCopyFallbackService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                failedFastCopyFallbackRunner);
            var failedFastCopyFallback = await failedFastCopyFallbackService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(6),
                    ClipTrimExecutionMode.ReplayCoexisting)
                .ConfigureAwait(false);
            Assert.Equal(
                ClipTrimStatus.EncodingFailed,
                failedFastCopyFallback.Status,
                "A failed packet copy and failed exact retry should return EncodingFailed.");
            Assert.Equal(
                2,
                failedFastCopyFallbackRunner.TrimRunCount,
                "A failed packet-copy fallback must stop after one exact retry.");
            Assert.True(
                failedFastCopyFallback.Diagnostic?.Contains(
                    "scripted encoding failure",
                    StringComparison.Ordinal) == true,
                "The final exact-encode failure diagnostic was not preserved.");
            Assert.SequenceEqual(
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                File.ReadAllBytes(sourcePath),
                "A double trim failure changed the source bytes.");
            Assert.True(
                !EnumerateTrimPartials(clipsDirectory).Any(),
                "A failed packet copy and exact retry left a partial output behind.");

            var missingKeyframeRunner = new FakeTrimMediaProcessRunner
            {
                HasRequestedKeyframe = false,
                AvailableHardwareEncoder = VideoEncoderKind.NvidiaNvenc
            };
            var missingKeyframeService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                missingKeyframeRunner);
            var missingKeyframe = await missingKeyframeService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(6),
                    ClipTrimExecutionMode.ReplayCoexisting)
                .ConfigureAwait(false);
            Assert.Equal(
                ClipTrimStatus.Succeeded,
                missingKeyframe.Status,
                "A missing fast-copy keyframe should safely fall back to encoding.");
            var missingKeyframeTrimArguments = missingKeyframeRunner.Invocations
                .Where(invocation => Path.GetFileName(invocation.ExecutablePath).Equals(
                    "ffmpeg.exe",
                    StringComparison.OrdinalIgnoreCase))
                .Last()
                .Arguments;
            Assert.ContainsSequence(missingKeyframeTrimArguments, "-c:v", "h264_nvenc");
            Assert.True(
                !missingKeyframeTrimArguments.Contains("copy", StringComparer.Ordinal),
                "A source without a keyframe at the requested start was packet-copied.");

            var replaySoftwareRunner = new FakeTrimMediaProcessRunner();
            var replaySoftwareService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                replaySoftwareRunner);
            var replaySoftware = await replaySoftwareService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    ClipTrimExecutionMode.ReplayCoexisting)
                .ConfigureAwait(false);
            Assert.Equal(ClipTrimStatus.Succeeded, replaySoftware.Status,
                $"Software replay-coexisting trim failed: {replaySoftware.Message}");
            Assert.Equal(1, replaySoftwareRunner.TrimRunCount,
                "Replay software fallback must launch one bounded export.");
            var replaySoftwareFfmpegInvocations = replaySoftwareRunner.Invocations
                .Where(invocation => Path.GetFileName(invocation.ExecutablePath).Equals(
                    "ffmpeg.exe",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(4, replaySoftwareFfmpegInvocations.Length,
                "Software replay fallback should follow the three failed hardware probes.");
            var replayTrimArguments = replaySoftwareFfmpegInvocations[^1].Arguments;
            Assert.ContainsSequence(
                replayTrimArguments,
                "-filter_threads", "1",
                "-threads", "1",
                "-readrate", "1");
            Assert.ContainsSequence(replayTrimArguments, "-c:v", "libx264", "-preset", "ultrafast");
            Assert.True(
                !replayTrimArguments.Any(argument =>
                    argument is "h264_nvenc" or "h264_qsv" or "h264_amf"),
                "The replay software fallback unexpectedly retained a hardware encoder.");
            Assert.True(
                !replayTrimArguments.Contains("lavfi", StringComparer.Ordinal),
                "The final replay software export was confused with a capability probe.");
            Assert.True(!EnumerateTrimPartials(clipsDirectory).Any(),
                "Replay-coexisting trim left a partial output behind.");

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
                Assert.True(
                    cancellationService.HasReplayBlockingTrimWork,
                    "A standard trim must keep replay blocked while cancellation is still unwinding.");
                cancellation.Cancel();
                var cancelled = await trimTask.ConfigureAwait(false);
                Assert.Equal(ClipTrimStatus.Cancelled, cancelled.Status,
                    "A cancelled export did not return Cancelled.");
                Assert.True(!cancelled.Succeeded && cancelled.OutputPath is null,
                    "A cancelled export returned an output path.");
                Assert.True(
                    !cancellationService.HasReplayBlockingTrimWork,
                    "The shared replay block did not clear after the standard trim finished cancelling.");
            }

            var coexistCancellationRunner = new FakeTrimMediaProcessRunner
            {
                WaitForTrimCancellation = true
            };
            var coexistCancellationService = new ClipTrimService(
                () => ffmpegPath,
                () => ffprobePath,
                coexistCancellationRunner);
            using (var cancellation = new CancellationTokenSource())
            {
                var trimTask = coexistCancellationService.TrimAsync(
                    clipsDirectory,
                    source,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(4),
                    ClipTrimExecutionMode.ReplayCoexisting,
                    cancellation.Token);
                await coexistCancellationRunner.TrimStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                Assert.True(
                    !coexistCancellationService.HasReplayBlockingTrimWork,
                    "A paced replay-coexisting trim must not block replay start/restart.");
                cancellation.Cancel();
                var cancelled = await trimTask.ConfigureAwait(false);
                Assert.Equal(ClipTrimStatus.Cancelled, cancelled.Status,
                    "A cancelled replay-coexisting export did not return Cancelled.");
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

    private static async Task TestLongPathPinnedMediaValidationAsync()
    {
        var testDirectory = CreateTestDirectory();
        var longRoot = testDirectory;
        try
        {
            const int targetRootLength = 215;
            while (longRoot.Length + 41 < targetRootLength)
            {
                longRoot = Path.Combine(
                    longRoot,
                    $"nested-{Guid.NewGuid():N}");
            }
            var finalComponentLength = targetRootLength - longRoot.Length - 1;
            Assert.True(
                finalComponentLength > 0,
                "The test temp root was unexpectedly too long for the MAX_PATH regression setup.");
            longRoot = Path.Combine(
                longRoot,
                new string('x', finalComponentLength));

            Directory.CreateDirectory(longRoot);
            var sourcePath = Path.Combine(
                longRoot,
                "Clip_2026-07-25_04-00-00.mp4");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4])
                .ConfigureAwait(false);
            Assert.True(
                sourcePath.Length < 260,
                "The long-path regression source should remain below the legacy MAX_PATH boundary.");
            Assert.True(
                ClipLibraryService.TryGetCurrentFileIdentity(sourcePath, out var identity),
                "The long-path regression source did not receive a stable file identity.");

            var sourceInfo = new FileInfo(sourcePath);
            var source = new ClipLibraryItem(
                sourceInfo.Name,
                sourceInfo.FullName,
                new DateTimeOffset(
                    DateTime.SpecifyKind(sourceInfo.LastWriteTimeUtc, DateTimeKind.Utc)),
                sourceInfo.Length,
                TimeSpan.FromSeconds(1))
            {
                FileIdentity = identity,
                Kind = ClipKind.Original
            };
            using var pinnedSource = ClipLibraryService.TryOpenPinnedClipReadContext(
                longRoot,
                source);
            Assert.True(
                pinnedSource is not null,
                "A safe source below MAX_PATH could not pin its long save root.");

            var stagingPath = Path.Combine(
                longRoot,
                $".clipforge-trim-{Guid.NewGuid():N}.partial.mp4");
            Assert.True(
                stagingPath.Length > 260,
                "The validation target must cross MAX_PATH to exercise extended Win32 paths.");
            await File.WriteAllBytesAsync(stagingPath, [5, 6, 7, 8])
                .ConfigureAwait(false);

            Assert.True(
                ClipLibraryService.TryValidatePinnedDirectChildFile(
                    pinnedSource!,
                    stagingPath,
                    out var resolvedPath,
                    out var length,
                    out var diagnostic),
                $"A safe non-empty trim staging file above MAX_PATH was rejected: {diagnostic}.");
            Assert.Equal(
                Path.GetFullPath(stagingPath),
                resolvedPath,
                "Long-path handle validation did not return the normalized DOS path.");
            Assert.Equal(
                4L,
                length,
                "Long-path handle validation returned the wrong file length.");
        }
        finally
        {
            DeleteTestDirectory(testDirectory);
        }
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
                ClipLibraryService.TryCreateKnownOutputItem(
                    clipsDirectory,
                    clipPath,
                    TimeSpan.FromSeconds(1),
                    out var knownOutput) &&
                knownOutput is not null &&
                knownOutput.FileSizeBytes == info.Length &&
                knownOutput.Duration == TimeSpan.FromSeconds(1),
                "A trusted save/trim output should become an identity-bound cached item without a media probe.");
            var unrelatedPath = Path.Combine(clipsDirectory, "unrelated.mp4");
            await File.WriteAllBytesAsync(unrelatedPath, [1, 2, 3]).ConfigureAwait(false);
            Assert.True(
                !ClipLibraryService.TryCreateKnownOutputItem(
                    clipsDirectory,
                    unrelatedPath,
                    knownDuration: null,
                    out _),
                "The probe-free replay cache path must reject unrelated file names.");
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
            await File.WriteAllBytesAsync(thumbnailPath, ValidTestJpegBytes).ConfigureAwait(false);
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

    private static async Task TestCorruptThumbnailRegenerationAsync()
    {
        var testDirectory = CreateTestDirectory();
        var clipsDirectory = Path.Combine(testDirectory, "Clips");
        var cacheDirectory = Path.Combine(testDirectory, "Cache");
        var toolsDirectory = Path.Combine(testDirectory, "Tools");
        try
        {
            Directory.CreateDirectory(clipsDirectory);
            Directory.CreateDirectory(cacheDirectory);
            Directory.CreateDirectory(toolsDirectory);
            var ffmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            await File.WriteAllBytesAsync(ffmpegPath, [0x4D, 0x5A]).ConfigureAwait(false);
            var clipPath = Path.Combine(clipsDirectory, "Clip_2026-07-13_14-00-00.mp4");
            await File.WriteAllBytesAsync(clipPath, [1, 2, 3, 4]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(
                clipPath,
                new DateTime(2026, 7, 13, 14, 0, 0, DateTimeKind.Utc));
            Assert.True(
                ClipLibraryService.TryCreateKnownOutputItem(
                    clipsDirectory,
                    clipPath,
                    TimeSpan.FromSeconds(4),
                    out var knownClip) &&
                knownClip is not null,
                "The thumbnail regeneration test clip should receive a stable identity.");

            var decodeValidationCount = 0;
            var runner = new FakeClipMediaProcessRunner();
            var service = new ClipLibraryService(
                () => ffmpegPath,
                () => null,
                runner,
                cacheDirectory,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                validateThumbnailDecode: path =>
                {
                    decodeValidationCount++;
                    return ClipLibraryService.TryDecodeThumbnailForValidation(path);
                });
            var thumbnailPath = service.GetDeterministicThumbnailPath(knownClip!);
            byte[] fakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xD9];
            await File.WriteAllBytesAsync(thumbnailPath, fakeJpeg).ConfigureAwait(false);
            Assert.True(
                !ClipLibraryService.TryDecodeThumbnailForValidation(thumbnailPath),
                "SOI/EOI marker bytes alone must not qualify as a decodable cached thumbnail.");

            var regenerated = await service.GetThumbnailAsync(
                    clipsDirectory,
                    knownClip!)
                .ConfigureAwait(false);
            Assert.Equal(
                thumbnailPath,
                regenerated,
                "A corrupt deterministic cache entry should be replaced by one valid FFmpeg output.");
            Assert.Equal(
                1,
                runner.ThumbnailRunCount,
                "A corrupt cached thumbnail should launch exactly one regeneration helper.");
            Assert.True(
                ClipLibraryService.TryDecodeThumbnailForValidation(thumbnailPath),
                "The regenerated thumbnail must pass a bounded real JPEG decode.");
            Assert.Equal(
                3,
                decodeValidationCount,
                "The corrupt target, staging output, and committed target should each be decoded once.");

            var reused = await service.GetThumbnailAsync(
                    clipsDirectory,
                    knownClip!)
                .ConfigureAwait(false);
            Assert.Equal(
                thumbnailPath,
                reused,
                "An unchanged regenerated thumbnail should be reused.");
            Assert.Equal(
                1,
                runner.ThumbnailRunCount,
                "A valid unchanged cache hit must not repeat FFmpeg generation.");
            Assert.Equal(
                3,
                decodeValidationCount,
                "A path/length/mtime-validated cache hit must not repeat JPEG decoding.");

            File.Delete(thumbnailPath);
            Assert.True(
                !File.Exists(thumbnailPath),
                "Thumbnail validation must release its file handle immediately.");
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

    private static async Task TestClipLibraryNegativeProbeCacheAsync()
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

            string[] invalidNames =
            [
                "Clip_2026-07-13_12-00-03.mp4",
                "Clip_2026-07-13_12-00-02.mp4",
                "Clip_2026-07-13_12-00-01.mp4"
            ];
            var validName = "Clip_2026-07-13_12-00-00.mp4";
            foreach (var name in invalidNames.Append(validName))
            {
                var path = Path.Combine(clipsDirectory, name);
                await File.WriteAllBytesAsync(path, [1, 2, 3]).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(
                    path,
                    DateTime.SpecifyKind(
                        DateTime.ParseExact(
                            name.AsSpan(5, 19),
                            "yyyy-MM-dd_HH-mm-ss",
                            CultureInfo.InvariantCulture),
                        DateTimeKind.Utc));
            }

            var utcNow = new DateTimeOffset(
                2026,
                7,
                13,
                12,
                1,
                0,
                TimeSpan.Zero);
            var runner = new FakeClipMediaProcessRunner
            {
                BlockFirstProbeFileName = invalidNames[2]
            };
            foreach (var name in invalidNames)
            {
                runner.RejectedProbeFileNames.Add(name);
            }

            var service = new ClipLibraryService(
                () => null,
                () => ffprobePath,
                runner,
                Path.Combine(testDirectory, "Cache"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                maximumTotalProbeDuration: TimeSpan.FromMilliseconds(100),
                invalidProbeCacheDuration: TimeSpan.FromSeconds(30),
                getUtcNow: () => utcNow);

            var first = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 1,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(
                0,
                first.Count,
                "The first bounded pass should stop while a bad newest candidate owns the probe budget.");

            var second = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 1,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(
                validName,
                second.Single().FileName,
                "Short-lived negative results should let an immediate refresh reach an older valid clip.");
            Assert.Equal(
                1,
                runner.GetProbeInvocationCount(invalidNames[0]),
                "An unchanged corrupt clip must not be re-probed by an immediate refresh.");
            Assert.Equal(
                1,
                runner.GetProbeInvocationCount(invalidNames[1]),
                "Each unchanged negative result should consume no more than one helper run within the TTL.");

            var helperRunsAfterRecovery = runner.Invocations.Count;
            for (var refresh = 0; refresh < 50; refresh++)
            {
                var repeated = await service.GetRecentClipsAsync(
                        clipsDirectory,
                        count: 1,
                        includeThumbnails: false)
                    .ConfigureAwait(false);
                Assert.Equal(
                    validName,
                    repeated.Single().FileName,
                    "A long sequence of unchanged refreshes should keep reaching the cached valid clip.");
            }

            Assert.Equal(
                helperRunsAfterRecovery,
                runner.Invocations.Count,
                "Repeated long-use refreshes must not respawn probes for cached valid or negative results.");

            runner.RejectedProbeFileNames.Remove(invalidNames[0]);
            var beforeExpiry = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 1,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(
                validName,
                beforeExpiry.Single().FileName,
                "A transiently recovered clip should remain fail-closed only for the bounded negative-cache TTL.");
            Assert.Equal(
                1,
                runner.GetProbeInvocationCount(invalidNames[0]),
                "The negative result must be reused before its TTL expires.");

            utcNow += TimeSpan.FromSeconds(31);
            var afterExpiry = await service.GetRecentClipsAsync(
                    clipsDirectory,
                    count: 1,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(
                invalidNames[0],
                afterExpiry.Single().FileName,
                "An expired negative result must be re-probed so transient failures recover.");
            Assert.Equal(
                2,
                runner.GetProbeInvocationCount(invalidNames[0]),
                "The negative cache must retry an unchanged clip after its TTL.");

            var changedDirectory = Path.Combine(testDirectory, "Changed");
            Directory.CreateDirectory(changedDirectory);
            var changedName = "Clip_2026-07-13_13-00-00.mp4";
            var changedPath = Path.Combine(changedDirectory, changedName);
            await File.WriteAllBytesAsync(changedPath, [4, 5, 6]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(
                changedPath,
                new DateTime(2026, 7, 13, 13, 0, 0, DateTimeKind.Utc));

            var changedRunner = new FakeClipMediaProcessRunner();
            changedRunner.RejectedProbeFileNames.Add(changedName);
            var changedService = new ClipLibraryService(
                () => null,
                () => ffprobePath,
                changedRunner,
                Path.Combine(testDirectory, "Changed-Cache"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                invalidProbeCacheDuration: TimeSpan.FromSeconds(30),
                getUtcNow: () => utcNow);
            var initiallyInvalid = await changedService.GetRecentClipsAsync(
                    changedDirectory,
                    count: 1,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(0, initiallyInvalid.Count, "The scripted changed clip should initially fail closed.");

            await File.WriteAllBytesAsync(changedPath, [4, 5, 6, 7]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(
                changedPath,
                new DateTime(2026, 7, 13, 13, 0, 1, DateTimeKind.Utc));
            changedRunner.RejectedProbeFileNames.Remove(changedName);
            var afterIdentityChange = await changedService.GetRecentClipsAsync(
                    changedDirectory,
                    count: 1,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(
                changedName,
                afterIdentityChange.Single().FileName,
                "A file identity or metadata change must bypass an unexpired negative result.");
            Assert.Equal(
                2,
                changedRunner.GetProbeInvocationCount(changedName),
                "Changed media must receive a fresh validation immediately.");
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

            var brokenTimelineRunner = new FakeClipMediaProcessRunner
            {
                ProbeOutput =
                    """
                    {
                      "streams": [
                        {
                          "codec_type": "video",
                          "start_time": "34.699000",
                          "duration": "145.305000",
                          "avg_frame_rate": "25917120/348839",
                          "r_frame_rate": "60/1"
                        }
                      ],
                      "format": { "duration": "180.004000" }
                    }
                    """
            };
            var brokenTimelineService = new ClipLibraryService(
                () => null,
                () => probePath,
                brokenTimelineRunner,
                Path.Combine(testDirectory, "Cache-Broken-Timeline"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
            var afterBrokenTimeline = await brokenTimelineService.GetRecentClipsAsync(
                    testDirectory,
                    includeThumbnails: false)
                .ConfigureAwait(false);
            Assert.Equal(
                0,
                afterBrokenTimeline.Count,
                "A clip whose video starts late and covers only part of the container must be hidden.");
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
                await replay.WaitForInitialBufferMaintenanceAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
                Assert.True(
                    !Directory.Exists(abandonedSession),
                    "Abandoned screen/audio replay data must be purged before capture starts.");
            }

            var legacyRoot = Path.Combine(testDirectory, "LegacyBuffer");
            var staleLegacy = Path.Combine(
                legacyRoot,
                "session-20260712-stale-legacy");
            var recentLegacy = Path.Combine(
                legacyRoot,
                "session-20260715-recent-legacy");
            var otherWindowsSession = Path.Combine(legacyRoot, "WindowsSession-99");
            Directory.CreateDirectory(staleLegacy);
            Directory.CreateDirectory(recentLegacy);
            Directory.CreateDirectory(otherWindowsSession);
            await File.WriteAllBytesAsync(
                    Path.Combine(staleLegacy, "segment-000000000.mkv"),
                    [1, 2, 3])
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    Path.Combine(recentLegacy, "segment-000000000.mkv"),
                    [1, 2, 3])
                .ConfigureAwait(false);
            var now = DateTime.UtcNow;
            Directory.SetLastWriteTimeUtc(staleLegacy, now - TimeSpan.FromDays(2));

            Assert.Equal(
                0,
                ReplayBufferService.CleanupLegacyStaleBufferRoot(
                    legacyRoot,
                    now,
                    potentialOwnerRunning: true),
                "Legacy cleanup must stop when another ClipForge/FFmpeg owner may be active.");
            Assert.True(
                Directory.Exists(staleLegacy),
                "Active-owner protection removed a legacy replay directory.");

            Assert.Equal(
                1,
                ReplayBufferService.CleanupLegacyStaleBufferRoot(
                    legacyRoot,
                    now,
                    potentialOwnerRunning: false),
                "One inactive, old-layout replay directory should be migrated away.");
            Assert.True(
                !Directory.Exists(staleLegacy),
                "Old pre-session-scoping replay data was not removed.");
            Assert.True(
                Directory.Exists(recentLegacy),
                "Recently active legacy replay data must remain untouched.");
            Assert.True(
                Directory.Exists(otherWindowsSession),
                "Legacy migration must never delete another WindowsSession root.");

            var unexpectedLegacy = Path.Combine(
                legacyRoot,
                "session-20260712-unexpected-content");
            Directory.CreateDirectory(unexpectedLegacy);
            await File.WriteAllTextAsync(
                    Path.Combine(unexpectedLegacy, "keep-me.txt"),
                    "not a replay segment")
                .ConfigureAwait(false);
            Directory.SetLastWriteTimeUtc(unexpectedLegacy, now - TimeSpan.FromDays(2));
            Assert.Equal(
                0,
                ReplayBufferService.CleanupLegacyStaleBufferRoot(
                    legacyRoot,
                    now,
                    potentialOwnerRunning: false),
                "Unexpected legacy-folder content must fail closed.");
            Assert.True(
                File.Exists(Path.Combine(unexpectedLegacy, "keep-me.txt")),
                "Fail-closed legacy cleanup removed an unexpected file.");
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
        public List<(
            IReadOnlyList<string> Arguments,
            CapturePerformanceProfile? CapturePerformanceProfile)> Invocations
        { get; } = [];

        public Task<FfmpegProbeExecution> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            CapturePerformanceProfile? capturePerformanceProfile = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Invocations.Add((
                arguments.ToArray(),
                capturePerformanceProfile));
            return Task.FromResult(succeeds(arguments)
                ? new FfmpegProbeExecution(true)
                : new FfmpegProbeExecution(false, "scripted unavailable capability"));
        }
    }

    private sealed class BlockingProbeRunner(
        Func<IReadOnlyList<string>, bool> succeeds) : IFfmpegProbeRunner
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public void Release() => _release.TrySetResult();

        public async Task<FfmpegProbeExecution> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            CapturePerformanceProfile? capturePerformanceProfile = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _callCount);
            FirstInvocationStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return succeeds(arguments)
                ? new FfmpegProbeExecution(true)
                : new FfmpegProbeExecution(false, "scripted unavailable capability");
        }
    }

    private sealed class FakeTrimMediaProcessRunner : IClipMediaProcessRunner
    {
        private double _lastTrimDurationSeconds = 1;

        public List<Invocation> Invocations { get; } = [];

        public TaskCompletionSource TrimStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailTrim { get; init; }

        public bool FailFirstTrimOnly { get; init; }

        public bool ReturnInvalidOutputMetadata { get; init; }

        public bool ReturnInvalidFirstTrimOutputMetadata { get; init; }

        public bool WaitForTrimCancellation { get; init; }

        public VideoEncoderKind? AvailableHardwareEncoder { get; init; }

        public string SourceAverageFrameRate { get; init; } = "60/1";

        public string OutputAverageFrameRate { get; init; } = "60/1";

        public string SourceNominalFrameRate { get; init; } = "60/1";

        public string OutputNominalFrameRate { get; init; } = "60/1";

        public bool HasRequestedKeyframe { get; init; } = true;

        public int TrimRunCount { get; private set; }

        public async Task<ClipMediaProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            ClipMediaProcessPriority priority = ClipMediaProcessPriority.Background)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(new Invocation(executablePath, arguments.ToArray(), timeout, priority));
            if (Path.GetFileName(executablePath).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Contains("packet=pts_time,flags", StringComparer.Ordinal))
                {
                    var intervalIndex = arguments.ToList().IndexOf("-read_intervals");
                    var interval = intervalIndex >= 0 && intervalIndex + 1 < arguments.Count
                        ? arguments[intervalIndex + 1]
                        : "0%+0.1";
                    var boundary = interval.Split('%', 2)[0];
                    var packetTime = HasRequestedKeyframe ? boundary : "0";
                    var flags = HasRequestedKeyframe ? "K__" : "___";
                    return new ClipMediaProcessResult(
                        0,
                        $"{{\"packets\":[{{\"pts_time\":\"{packetTime}\",\"flags\":\"{flags}\"}}]}}",
                        string.Empty,
                        false);
                }

                var mediaPath = arguments[^1];
                var isTrimPartial = Path.GetFileName(mediaPath).StartsWith(
                    ".clipforge-trim-",
                    StringComparison.OrdinalIgnoreCase);
                var duration = isTrimPartial
                    ? ReturnInvalidOutputMetadata ? 99 : _lastTrimDurationSeconds
                    : 10;
                var durationText = duration.ToString("0.######", CultureInfo.InvariantCulture);
                var startTime = isTrimPartial &&
                                ReturnInvalidFirstTrimOutputMetadata &&
                                TrimRunCount == 1
                    ? "34.699"
                    : "0";
                var averageFrameRate = isTrimPartial
                    ? OutputAverageFrameRate
                    : SourceAverageFrameRate;
                var nominalFrameRate = isTrimPartial
                    ? OutputNominalFrameRate
                    : SourceNominalFrameRate;
                var output =
                    $"{{\"streams\":[{{\"codec_type\":\"video\",\"width\":1280,\"height\":720," +
                    $"\"start_time\":\"{startTime}\"," +
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
                var expectedEncoder = AvailableHardwareEncoder switch
                {
                    VideoEncoderKind.NvidiaNvenc => "h264_nvenc",
                    VideoEncoderKind.IntelQuickSync => "h264_qsv",
                    VideoEncoderKind.AmdAmf => "h264_amf",
                    _ => null
                };
                return expectedEncoder is not null &&
                       arguments.Contains(expectedEncoder, StringComparer.Ordinal)
                    ? new ClipMediaProcessResult(0, string.Empty, string.Empty, false)
                    : new ClipMediaProcessResult(1, string.Empty, "probe unavailable", false);
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

            return FailTrim || (FailFirstTrimOnly && TrimRunCount == 1)
                ? new ClipMediaProcessResult(1, string.Empty, "scripted encoding failure", false)
                : new ClipMediaProcessResult(0, string.Empty, string.Empty, false);
        }

        public sealed record Invocation(
            string ExecutablePath,
            IReadOnlyList<string> Arguments,
            TimeSpan Timeout,
            ClipMediaProcessPriority Priority);
    }

    private sealed class FakeClipMediaProcessRunner : IClipMediaProcessRunner
    {
        private readonly TaskCompletionSource _releaseThumbnailGeneration =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Invocation> Invocations { get; } = [];

        public int ThumbnailRunCount { get; private set; }

        public TaskCompletionSource ThumbnailStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PauseThumbnailGeneration { get; init; }

        public bool RejectAllProbes { get; set; }

        public bool TimeOutAllProbes { get; init; }

        public TimeSpan ProbeDelay { get; init; }

        public string? ProbeOutput { get; init; }

        public string? BlockFirstProbeFileName { get; init; }

        public HashSet<string> RejectedProbeFileNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, int> ProbeInvocationCounts { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Action<IReadOnlyList<string>>? BeforeThumbnailWrite { get; init; }

        public void ReleaseThumbnailGeneration() =>
            _releaseThumbnailGeneration.TrySetResult();

        public int GetProbeInvocationCount(string fileName) =>
            ProbeInvocationCounts.GetValueOrDefault(fileName);

        public async Task<ClipMediaProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            ClipMediaProcessPriority priority = ClipMediaProcessPriority.Background)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(new Invocation(executablePath, arguments.ToArray(), timeout, priority));

            if (Path.GetFileName(executablePath).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                var clipName = Path.GetFileName(arguments[^1]);
                var invocationCount = ProbeInvocationCounts.GetValueOrDefault(clipName) + 1;
                ProbeInvocationCounts[clipName] = invocationCount;
                if (ProbeDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ProbeDelay, cancellationToken).ConfigureAwait(false);
                }

                if (invocationCount == 1 &&
                    clipName.Equals(BlockFirstProbeFileName, StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }

                if (TimeOutAllProbes)
                {
                    return new ClipMediaProcessResult(-1, string.Empty, string.Empty, true);
                }

                return RejectAllProbes ||
                       RejectedProbeFileNames.Contains(clipName) ||
                       clipName.Equals(
                        "Clip_2026-01-03_01-00-00.mp4",
                        StringComparison.OrdinalIgnoreCase)
                    ? new ClipMediaProcessResult(1, string.Empty, "invalid media", false)
                    : new ClipMediaProcessResult(
                        0,
                        ProbeOutput ??
                        "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"42.5\"}}",
                        string.Empty,
                        false);
            }

            ThumbnailRunCount++;
            ThumbnailStarted.TrySetResult();
            if (PauseThumbnailGeneration)
            {
                await _releaseThumbnailGeneration.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            BeforeThumbnailWrite?.Invoke(arguments);
            var outputPath = arguments[^1];
            await File.WriteAllBytesAsync(
                    outputPath,
                    ValidTestJpegBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ClipMediaProcessResult(0, string.Empty, string.Empty, false);
        }

        public sealed record Invocation(
            string ExecutablePath,
            IReadOnlyList<string> Arguments,
            TimeSpan Timeout,
            ClipMediaProcessPriority Priority);
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
