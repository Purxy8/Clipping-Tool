# Changelog

All notable user-facing changes to ClipForge are recorded here.

## [1.1.0-beta.1] - 2026-07-12

### Release status

- First public beta used to establish the release history required for a SignPath Foundation application.
- This beta is explicitly unsigned and is not an official trusted release. Windows can show an unverified-publisher or SmartScreen warning.

### Added

- A redesigned settings sidebar, large latest-clip player, and thumbnail gallery for the four most recent clips.
- Configurable global shortcuts for Save Clip and Toggle Overlay, with conflict-safe re-registration.
- A compact always-on-top replay overlay and notification-area controls for showing ClipForge, saving, and exiting.
- Per-user/session single-instance activation so a second launch reopens the existing app instead of causing shortcut conflicts.
- Runtime-tested NVIDIA NVENC, Intel Quick Sync, and AMD AMF encoding, with automatic software H.264 fallback.
- Runtime selection between direct Windows Graphics Capture, a multi-GPU compatibility transfer, and GDI capture, with the active strategy visible in the interface.
- Authenticode/Azure Artifact Signing release support; public GitHub releases fail closed when signing is not configured.
- GitHub CI, CodeQL scanning, and Dependabot configuration for continuous build, test, static-analysis, and dependency monitoring.

### Changed

- FFmpeg capture runs at below-normal priority, the software encoder has a bounded thread count, and WASAPI transfer uses bounded pooled buffers to reduce contention and unbounded memory growth.
- The modernized interface adds subtle entry and gallery animations while pausing clip playback when the main window is hidden.
- Closing the main window now hides ClipForge to the notification area so replay and global shortcuts can remain active.

### Security

- Restricted native DLL search policy is applied at process startup.
- Settings input, child-process output, media probing, thumbnail generation, local Open actions, and update-source validation are bounded or validated.
- Audio pipes are current-user-only, and recursive replay cleanup rejects junctions, symlinks, traversal, and unrelated directories.
- FFmpeg installation remains HTTPS-only with a pinned archive digest, and child processes are launched without a command shell.

### Verification note

- On the development PC, real AMD AMF/GDI smoke runs produced valid six-second video and mixed-audio clips at `BelowNormal` priority. Sampled normalized FFmpeg CPU was 2.3% for video, 1.8% for desktop-plus-microphone audio, and 2.0% during rollover/pruning, with roughly 122-125 MB working set.
- The direct multi-GPU Windows Graphics Capture compatibility-transfer probe also succeeded. A complete six-second replay run on that newly selected path is still required before calling the candidate final.
- These measurements describe only the test PC and configuration; they are not a guarantee of zero game input latency on every GPU, driver, resolution, or frame rate. Target-game testing remains required before a public release.

## [1.0.0]

- Initial local-first Windows instant-replay recorder with rolling disk buffer, display/resolution/duration selection, desktop and microphone audio, configurable save path, and Velopack update packaging.
