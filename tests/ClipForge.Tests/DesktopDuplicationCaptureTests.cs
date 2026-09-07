using ClipForge.Capture;
using ClipForge.Models;
using ClipForge.Services;
using System.Diagnostics;

namespace ClipForge.Tests;

internal static class DesktopDuplicationCaptureTests
{
    internal static Task DisplayIdentityAsync()
    {
        var display = CreateConfiguration().Display;
        var matching = new DxgiOutputSnapshot(
            2, 1, 17, display.DeviceName.ToLowerInvariant(),
            display.Left, display.Top, display.Left + display.Width, display.Top + display.Height,
            true, 1);
        var resolved = DxgiDisplayDiscovery.ResolveTarget(display, [matching]);
        Require(resolved is { AdapterIndex: 2, OutputIndex: 1, AdapterLuid: 17, Rotation: 1 },
            "DXGI identity did not resolve case-insensitive device name plus exact desktop bounds.");
        Require(resolved!.OutputIndex != display.MonitorIndex,
            "The test must distinguish DXGI output identity from WGC monitor ordering.");
        Require(DxgiDisplayDiscovery.ResolveTarget(display, []) is null,
            "An undiscovered DXGI target must not guess a default adapter/output.");
        foreach (var rejected in new[]
                 {
                     matching with { DeviceName = @"\\.\DISPLAY99" },
                     matching with { Left = display.Left + 1 },
                     matching with { Right = display.Left },
                     matching with { AttachedToDesktop = false },
                     matching with { Rotation = 0 },
                     matching with { Rotation = 2 },
                     matching with { AdapterIndex = -1 },
                     matching with { OutputIndex = -1 }
                 })
        {
            Require(DxgiDisplayDiscovery.ResolveTarget(display, [rejected]) is null,
                "Unsafe, detached, rotated, or mismatched DXGI identity was accepted.");
        }
        Require(DxgiDisplayDiscovery.ResolveTarget(display,
                [matching, matching with { AdapterIndex = 3, AdapterLuid = 18 }]) is null,
            "An ambiguous mirrored desktop must not choose an arbitrary adapter.");
        var plan = new CaptureSessionPlan(display, CreateConfiguration().Resolution,
            new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.DesktopDuplication));
        Require(!CaptureGeometry.RequiresRestartForDisplayChange(plan, display),
            "An unchanged DXGI target unnecessarily restarted capture.");
        foreach (var changedDisplay in new[]
                 {
                     display with { DesktopDuplicationTarget = null },
                     display with { DesktopDuplicationTarget = display.DesktopDuplicationTarget! with { AdapterLuid = 18 } },
                     display with { DesktopDuplicationTarget = display.DesktopDuplicationTarget! with { OutputIndex = 0 } },
                     display with { Left = display.Left - 1 },
                     display with { Width = 1290, Height = 980 }
                 })
            Require(CaptureGeometry.RequiresRestartForDisplayChange(plan, changedDisplay),
                "A changed or lost DXGI target retained a stale fixed desktop capture graph.");
        return Task.CompletedTask;
    }

    internal static Task CaptureArgumentsAsync()
    {
        var configuration = CreateConfiguration();
        var strategy = new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.DesktopDuplication);
        var sourceArguments = FfmpegArgumentBuilder.BuildCaptureArguments(configuration, [], strategy, @"C:\Buffer");
        var sourceGraph = Option(sourceArguments, "-filter_complex") ?? string.Empty;
        Require(Option(sourceArguments, "-init_hw_device") == "d3d11va=clipforge_dda:2" &&
                Option(sourceArguments, "-filter_hw_device") == "clipforge_dda",
            "Desktop Duplication must bind the mapped adapter rather than implicit GPU zero.");
        Require(Option(sourceArguments, "-map") == "[captured_video]" && !sourceArguments.Contains("-i") &&
                !sourceArguments.Contains("-thread_queue_size") && !sourceArguments.Contains("lavfi"),
            "DDA must run as an output filter source so FFmpeg receives the explicit D3D11 device.");
        Require(sourceGraph.Contains("ddagrab=output_idx=1:", StringComparison.Ordinal) &&
                !sourceGraph.Contains("monitor_idx=", StringComparison.Ordinal),
            "Desktop Duplication reused WGC MonitorIndex instead of the mapped per-adapter output.");
        Require(sourceGraph.Contains(":draw_mouse=0", StringComparison.Ordinal) &&
                sourceGraph.Contains(":framerate=60", StringComparison.Ordinal) &&
                sourceGraph.Contains(":dup_frames=1", StringComparison.Ordinal) &&
                sourceGraph.Contains(":output_fmt=bgra", StringComparison.Ordinal) &&
                sourceGraph.Contains(":video_size=2560x1440", StringComparison.Ordinal),
            "The static-desktop DDA graph lost its fixed-rate, cursor, format, or native geometry contract.");
        Require(!sourceGraph.Contains("scale=", StringComparison.Ordinal) &&
                !sourceGraph.Contains("scale_cuda=", StringComparison.Ordinal),
            "Source/native DDA capture introduced unnecessary resizing.");
        Require(Option(sourceArguments, "-r") == "60" && Option(sourceArguments, "-fps_mode") == "cfr" &&
                Option(sourceArguments, "-bsf:v") == "setts=ts=N/(60*TB):duration=1/(60*TB)",
            "DDA output clocks differ from the verified Replay packet timeline.");
        Require(Option(sourceArguments, "-segment_time") == "2" &&
                !sourceArguments.Contains("-t") && configuration.Retention == TimeSpan.FromHours(1) &&
                ReplayLengthOption.All.Max(option => option.Duration) == TimeSpan.FromHours(1),
            "Introducing a capture backend must preserve one-hour Replay retention and two-second segments.");

        var scaled = configuration with { Resolution = ResolutionOption.All.Single(option => option.Id == "1080p"), CaptureCursor = true };
        var scaledArguments = FfmpegArgumentBuilder.BuildCaptureArguments(scaled, [], strategy, @"C:\Buffer");
        var scaledGraph = Option(scaledArguments, "-filter_complex") ?? string.Empty;
        Require(scaledGraph.Contains(":draw_mouse=1", StringComparison.Ordinal) &&
                scaledGraph.Contains(":video_size=2560x1440", StringComparison.Ordinal) &&
                scaledGraph.Contains("scale_cuda=w=1920:h=1080", StringComparison.Ordinal),
            "1080p NVENC must capture native DDA frames then use the independently verified CUDA scaler.");
        foreach (var encoder in new[] { VideoEncoderKind.IntelQuickSync, VideoEncoderKind.AmdAmf, VideoEncoderKind.SoftwareX264 })
        {
            var compatibility = new VideoEncodingStrategy(encoder, DesktopCaptureBackend.DesktopDuplication, true);
            var graph = Option(FfmpegArgumentBuilder.BuildCaptureArguments(scaled, [], compatibility, @"C:\Buffer"), "-filter_complex") ?? string.Empty;
            Require(graph.Contains("hwdownload", StringComparison.Ordinal) &&
                    graph.Contains("scale=1920:1080", StringComparison.Ordinal),
                "A non-CUDA DDA encoder lost its compatible download and software scale path.");
        }
        var custom = scaled with { Display = scaled.Display with { Width = 1290, Height = 980 } };
        var customGraph = Option(FfmpegArgumentBuilder.BuildCaptureArguments(custom, [], strategy, @"C:\Buffer"), "-filter_complex") ?? string.Empty;
        Require(customGraph.Contains(":video_size=1290x980", StringComparison.Ordinal) &&
                !customGraph.Contains("scale_cuda=", StringComparison.Ordinal),
            "1080p unnecessarily upscaled a smaller custom desktop mode.");
        ExpectRejected(() => FfmpegArgumentBuilder.BuildCaptureArguments(
            configuration with { Display = configuration.Display with { DesktopDuplicationTarget = null } },
            [], strategy, @"C:\Buffer"), "DDA capture guessed a monitor when DXGI mapping was unavailable.");

        AudioInputSpecification[] audioInputs =
        [
            new(@"\\.\pipe\desktop", "f32le", 48000, 2),
            new(@"\\.\pipe\microphone", "s16le", 44100, 1)
        ];
        for (var count = 1; count <= audioInputs.Length; count++)
        {
            var audioArguments = FfmpegArgumentBuilder.BuildCaptureArguments(configuration, audioInputs[..count], strategy, @"C:\Buffer");
            var audioGraph = Option(audioArguments, "-filter_complex") ?? string.Empty;
            Require(audioArguments.Count(value => value == "-filter_complex") == 1 &&
                    audioArguments.Count(value => value == "-i") == count &&
                    audioGraph.Contains("[0:a]aresample=", StringComparison.Ordinal) &&
                    !audioGraph.Contains($"[{count}:a]", StringComparison.Ordinal) &&
                    audioArguments.Contains("[mixed_audio]") && audioArguments.Contains("[captured_video]"),
                "DDA audio inputs must begin at index zero and share one video/audio graph.");
            if (count == 2)
                Require(audioGraph.Contains("[1:a]aresample=", StringComparison.Ordinal) &&
                        audioGraph.Contains("amix=inputs=2", StringComparison.Ordinal),
                    "DDA desktop plus microphone capture lost the second input or mixer.");
        }
        return Task.CompletedTask;
    }

    internal static Task ProbeCadenceAsync()
    {
        var configuration = CreateConfiguration();
        var strategy = new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.DesktopDuplication);
        foreach (var preset in new[] { "source", "1080p" })
        {
            var configured = configuration with { Resolution = ResolutionOption.All.Single(option => option.Id == preset) };
            var probe = FfmpegArgumentBuilder.BuildDesktopDuplicationProbeArguments(configured, strategy);
            var live = FfmpegArgumentBuilder.BuildCaptureArguments(configured, [], strategy, @"C:\Buffer");
            Require(Option(probe, "-filter_complex") == Option(live, "-filter_complex") && Option(probe, "-init_hw_device") == Option(live, "-init_hw_device"),
                "DDA capability probing does not exercise the actual live source graph and adapter.");
            Require(Option(probe, "-r") == "60" && Option(probe, "-frames:v") == "180",
                "DDA capability probe lost its three-second output frame contract.");
            Require(FfmpegProbeRunner.TryResolveCaptureProbePolicy(probe, out var resolved, out var scaling, out _) &&
                    resolved.CaptureBackend == DesktopCaptureBackend.DesktopDuplication &&
                    resolved.Encoder == VideoEncoderKind.NvidiaNvenc && scaling == (preset == "1080p"),
                "DDA probe process policy was misclassified as WGC, GDI, or a non-capture encoder test.");
            Require(!FfmpegProbeRunner.IsProbeCadenceAcceptable(probe, null, out _),
                "DDA capability selection accepted a source without progress samples.");
            Require(!FfmpegProbeRunner.IsProbeCadenceAcceptable(probe,
                    new FfmpegProbeCadenceObservation(1, TimeSpan.Zero, 180, TimeSpan.FromSeconds(6)), out _),
                "DDA accepted a 30 FPS graph for requested 60 FPS capture.");
            Require(FfmpegProbeRunner.IsProbeCadenceAcceptable(probe,
                    new FfmpegProbeCadenceObservation(1, TimeSpan.Zero, 180, TimeSpan.FromSeconds(3), 0, 90), out var diagnostic),
                "Healthy DDA output on a static desktop was mistaken for failed GDI unique-frame acquisition.");
            Require(!diagnostic.Contains("unique", StringComparison.OrdinalIgnoreCase),
                "DDA output-progress cadence must not claim to measure unique source content.");
        }
        ExpectRejected(() => FfmpegArgumentBuilder.BuildDesktopDuplicationProbeArguments(
            configuration, strategy with { CaptureBackend = DesktopCaptureBackend.Gdi }),
            "DDA probe accepted the wrong backend.");
        return Task.CompletedTask;
    }

    internal static Task RecoveryPolicyAsync()
    {
        const DesktopCaptureBackend dda = DesktopCaptureBackend.DesktopDuplication;
        Require(MainWindow.SupportsAutomaticCaptureRecovery(dda) && ReplayBufferService.CanRefreshCaptureBackend(dda) &&
                ReplayBufferService.SupportsGraphicsCaptureRecovery(dda),
            "DDA lost the existing objective-fault recovery path.");
        Require(MainWindow.ShouldUseSourceSafetyRecovery(dda, true, 0, true) &&
                !MainWindow.ShouldUseSourceSafetyRecovery(dda, true, 0, false) &&
                MainWindow.ShouldUseSourceSafetyRecovery(dda, true, 1, false) &&
                !MainWindow.ShouldUseSourceSafetyRecovery(dda, false, 1, true),
            "DDA Source safety recovery must respect the recovery budget and measured prior attempts.");
        Require(!ReplayBufferService.SupportsSourceCadenceDiagnostics(dda) &&
                ReplayBufferService.SupportsSourceCadenceDiagnostics(DesktopCaptureBackend.WindowsGraphicsCapture) &&
                ReplayBufferService.SupportsSourceCadenceDiagnostics(DesktopCaptureBackend.Gdi),
            "DDA internal repeated frames cannot be classified as observable unique source cadence.");
        Require(!ReplayBufferService.ShouldScheduleCaptureRefresh(dda, TimeSpan.FromHours(24)),
            "Healthy DDA capture must not enter the WGC-specific timed renewal path.");
        var strategy = new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, dda);
        Require(ProcessTuning.GetCaptureCpuPriority(strategy, false, CapturePerformanceProfile.LowImpact) == ProcessPriorityClass.BelowNormal &&
                ProcessTuning.GetCaptureCpuPriority(strategy, true, CapturePerformanceProfile.LowImpact) == ProcessPriorityClass.Normal &&
                ProcessTuning.GetCaptureCpuPriority(strategy, false, CapturePerformanceProfile.Resilient) == ProcessPriorityClass.Normal,
            "DDA must retain bounded native scheduling and normal priority when scaled or recovering.");
        var context = new CaptureForegroundContext(true, true);
        var quiet = new CaptureStarvationWatchdog(60);
        var slow = new CaptureStarvationWatchdog(60);
        CaptureStarvationAssessment? slowAssessment = null;
        for (var second = 0; second <= 16; second++)
        {
            Require(quiet.Observe(Sample(second, 60L * second, 59L * second, 1_000_000L * second), context,
                    TimeSpan.FromSeconds(second), allowSchedulingPressure: false, allowOutputThroughput: true,
                    allowChronicLowCadence: false, allowSourceCadence: false) is null,
                "Static DDA output triggered an unsupported unique-content starvation claim.");
            slowAssessment ??= slow.Observe(Sample(second, 30L * second, 0, 500_000L * second), context,
                TimeSpan.FromSeconds(second), allowSchedulingPressure: false, allowOutputThroughput: true,
                allowChronicLowCadence: false, allowSourceCadence: false);
        }
        Require(slowAssessment is { Kind: CaptureStarvationKind.OutputThroughput },
            "Disabling unsupported DDA source analysis also disabled objective output-throughput protection.");
        var gap = new CaptureStarvationWatchdog(60);
        for (var second = 0; second <= 12; second++)
            Require(gap.Observe(Sample(second, 60L * second, 0, 1_000_000L * second), context,
                    TimeSpan.FromSeconds(second), allowSchedulingPressure: false, allowOutputThroughput: true,
                    allowChronicLowCadence: false, allowSourceCadence: false) is null,
                "Healthy DDA throughput unexpectedly triggered recovery.");
        Require(gap.Observe(Sample(16, 750, 0, 12_500_000), context, TimeSpan.FromSeconds(16),
                allowSchedulingPressure: false, allowOutputThroughput: true,
                allowChronicLowCadence: false, allowSourceCadence: false) is null,
            "DDA progress-gap monitoring must retain queued-progress catch-up grace.");
        Require(gap.Observe(Sample(20, 780, 0, 13_000_000), context, TimeSpan.FromSeconds(20),
                allowSchedulingPressure: false, allowOutputThroughput: true,
                allowChronicLowCadence: false, allowSourceCadence: false) is { Kind: CaptureStarvationKind.ProgressGap },
            "DDA source-cadence suppression also disabled objective progress-freeze detection.");
        return Task.CompletedTask;
    }

    internal static async Task CapabilitySelectionAsync()
    {
        var configuration = CreateConfiguration();
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var runner = new ScriptedRunner(arguments => Option(arguments, "-c:v") == "h264_nvenc");
        var probe = new FfmpegCapabilityProbe(runner, getUtcNow: () => now);
        var selected = await probe.SelectAsync(@"C:\Test\ffmpeg.exe", configuration, CancellationToken.None);
        Require(selected.Strategy.CaptureBackend == DesktopCaptureBackend.DesktopDuplication &&
                runner.Arguments.Any(IsDda) && !runner.Arguments.Any(IsWgc),
            "Verified DDA did not take precedence over the change-driven WGC path.");
        var originalCalls = runner.Arguments.Count;
        now += TimeSpan.FromMinutes(1);
        await probe.SelectAsync(@"C:\Test\ffmpeg.exe", configuration, CancellationToken.None);
        Require(runner.Arguments.Count == originalCalls,
            "Healthy DDA was incorrectly cached as a short-lived degraded GDI fallback.");
        foreach (var changed in new[]
                 {
                     configuration.Display.DesktopDuplicationTarget! with { AdapterIndex = 3 },
                     configuration.Display.DesktopDuplicationTarget! with { OutputIndex = 2 },
                     configuration.Display.DesktopDuplicationTarget! with { AdapterLuid = 18 }
                 })
        {
            var before = runner.Arguments.Count;
            await probe.SelectAsync(@"C:\Test\ffmpeg.exe",
                configuration with { Display = configuration.Display with { DesktopDuplicationTarget = changed } }, CancellationToken.None);
            Require(runner.Arguments.Count > before,
                "Changed DXGI adapter/output identity reused a capability result for a different target.");
        }

        var fallbackRunner = new ScriptedRunner(arguments => !IsDda(arguments) && Option(arguments, "-c:v") == "h264_nvenc");
        var fallback = await new FfmpegCapabilityProbe(fallbackRunner).SelectAsync(
            @"C:\Test\ffmpeg.exe", configuration, CancellationToken.None);
        Require(fallback.Strategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
                fallbackRunner.Arguments.FindIndex(IsDda) >= 0 &&
                fallbackRunner.Arguments.FindIndex(IsDda) < fallbackRunner.Arguments.FindIndex(IsWgc),
            "Failed DDA must fall through to the already verified WGC fallback.");
        var noTargetRunner = new ScriptedRunner(arguments => Option(arguments, "-c:v") == "h264_nvenc");
        var noTarget = await new FfmpegCapabilityProbe(noTargetRunner).SelectAsync(
            @"C:\Test\ffmpeg.exe",
            configuration with { Display = configuration.Display with { DesktopDuplicationTarget = null } }, CancellationToken.None);
        Require(noTarget.Strategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture &&
                !noTargetRunner.Arguments.Any(IsDda),
            "Missing DXGI mapping must preserve the existing WGC capability sequence.");
    }

    internal static async Task FocusedRecoveryAsync()
    {
        var configuration = CreateConfiguration();
        var current = new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.Gdi);
        foreach (var successfulAttempt in new[] { 1, 2, 3, 0 })
        {
            var attempts = 0;
            var runner = new ScriptedRunner(_ => ++attempts == successfulAttempt);
            var selected = await new FfmpegCapabilityProbe(runner).ReprobeWindowsGraphicsCaptureAsync(
                @"C:\Test\ffmpeg.exe", configuration, current, CancellationToken.None,
                CapturePerformanceProfile.Resilient);
            Require(runner.Arguments.Count == (successfulAttempt == 0 ? 4 : successfulAttempt) &&
                    runner.Arguments.All(arguments => Option(arguments, "-c:v") == "h264_nvenc" &&
                        (IsDda(arguments) || IsWgc(arguments))),
                "Focused degraded-GDI recovery escaped its same-encoder bounded graphics probe budget.");
            Require(IsDda(runner.Arguments[0]) &&
                    !(Option(runner.Arguments[0], "-filter_complex") ?? string.Empty).Contains("hwdownload", StringComparison.Ordinal),
                "A mapped focused recheck must first try direct DDA for the active encoder.");
            if (successfulAttempt != 1)
                Require(IsDda(runner.Arguments[1]) &&
                        (Option(runner.Arguments[1], "-filter_complex") ?? string.Empty).Contains("hwdownload", StringComparison.Ordinal),
                    "Focused recovery skipped the compatible DDA transfer before trying WGC.");
            if (successfulAttempt is 0 or 3)
                Require(IsWgc(runner.Arguments[2]), "Both failed DDA candidates must fall back to WGC.");
            Require(successfulAttempt switch
            {
                1 => selected.Strategy.CaptureBackend == DesktopCaptureBackend.DesktopDuplication && !selected.Strategy.RequiresSystemMemoryTransfer,
                2 => selected.Strategy.CaptureBackend == DesktopCaptureBackend.DesktopDuplication && selected.Strategy.RequiresSystemMemoryTransfer,
                3 => selected.Strategy.CaptureBackend == DesktopCaptureBackend.WindowsGraphicsCapture,
                _ => selected.Strategy == current
            }, "Focused recovery returned an unverified backend or discarded the existing usable GDI strategy.");
        }
    }

    internal static Task PromotionPolicyAsync()
    {
        var gdi = new VideoEncodingStrategy(VideoEncoderKind.NvidiaNvenc, DesktopCaptureBackend.Gdi);
        var dda = gdi with { CaptureBackend = DesktopCaptureBackend.DesktopDuplication };
        var wgc = gdi with { CaptureBackend = DesktopCaptureBackend.WindowsGraphicsCapture };
        Require(ReplayBufferService.CanPromoteDegradedCapture(gdi, dda, true) &&
                ReplayBufferService.CanPromoteDegradedCapture(gdi, wgc, true),
            "A capability-verified DDA replacement was rejected by degraded-GDI promotion.");
        Require(!ReplayBufferService.CanPromoteDegradedCapture(gdi, dda, false) &&
                !ReplayBufferService.CanPromoteDegradedCapture(dda, dda, true) &&
                !ReplayBufferService.CanPromoteDegradedCapture(wgc, dda, true) &&
                !ReplayBufferService.CanPromoteDegradedCapture(gdi, gdi, true),
            "DDA promotion bypassed the verified-strategy or active-degraded-GDI requirements.");
        return Task.CompletedTask;
    }

    private static CaptureConfiguration CreateConfiguration() => new(
        new DisplayOption(@"\\.\DISPLAY2", "Secondary display", -2560, 40, 2560, 1440, false, 7, 165,
            new DxgiCaptureTarget(2, 1, 17, @"\\.\DISPLAY2", 1)),
        ResolutionOption.All.Single(option => option.Id == "source"),
        60, TimeSpan.FromHours(1), false, false, null, false, null, @"C:\Clips");

    private static bool IsDda(IReadOnlyList<string> arguments) => arguments.Any(value => value.Contains("ddagrab=", StringComparison.Ordinal));
    private static bool IsWgc(IReadOnlyList<string> arguments) => arguments.Any(value => value.Contains("gfxcapture=", StringComparison.Ordinal));
    private static string? Option(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
            if (arguments[index] == name) return arguments[index + 1];
        return null;
    }

    private static CaptureProgressSample Sample(int second, long frame, long duplicated, long outputMicroseconds) =>
        new(frame, duplicated, 0, outputMicroseconds, checked(second * (long)Stopwatch.Frequency));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectRejected(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class ScriptedRunner(Func<IReadOnlyList<string>, bool> succeeds) : IFfmpegProbeRunner
    {
        public List<IReadOnlyList<string>> Arguments { get; } = [];
        public Task<FfmpegProbeExecution> RunAsync(string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken, CapturePerformanceProfile? capturePerformanceProfile = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Arguments.Add(arguments.ToArray());
            return Task.FromResult(new FfmpegProbeExecution(succeeds(arguments), "scripted capture result"));
        }
    }
}
