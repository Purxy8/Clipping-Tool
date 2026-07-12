# Changelog

All notable user-facing changes to ClipForge are recorded here.

## [1.2.0-beta.1] - 2026-07-12

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Added

- Five dark background presets, a Windows color picker for a custom background, and a reset action. Custom values are validated and proportionally darkened when needed so the fixed light interface remains readable.
- Complete controls for the large latest-clip player: play/pause, clickable and draggable seeking, elapsed/total time, restart, 10-second backward/forward skips, mute, and volume.
- Short, GPU-friendly hover and entrance animations that stop doing timer work when the main window is inactive.

### Changed

- Replay retention keeps an incremental index of sequential FFmpeg segments instead of repeatedly enumerating, sorting, and measuring the entire rolling buffer.
- Audio capture now limits its pooled backlog to 16 blocks, keeps the newest samples under writer pressure, and promptly returns dropped or shutdown-remnant buffers; media probes and thumbnail helpers now run at low-impact process priority.
- The embedded player's position timer runs only while the visible, active window is playing media.
- Beta installations can discover later beta releases; stable installations continue to ignore pre-releases.
- Applying an update is now explicitly initiated from the update panel. Velopack's implicit apply-on-startup path is disabled so capture can be stopped through ClipForge's orderly shutdown path first.

### Security

- The clip gallery auto-discovers only top-level files using ClipForge's generated `Clip_YYYY-MM-DD_HH-mm-ss[_N].mp4` naming format.
- FFprobe and thumbnail FFmpeg invocations force the MOV/MP4 demuxer and allow only the local `file` protocol, reducing the chance that a planted playlist or polyglot file triggers a network protocol.
- Replay cleanup now rejects a buffer root when it or an existing ancestor is a junction/symbolic link, separates roots by Windows logon-session ID, and purges abandoned capture buffers in the current logon scope on the next single-instance startup instead of retaining them for a grace period.
- FFmpeg setup now enforces declared and streamed download limits, ZIP extraction limits, free-space checks, pinned per-executable hashes, and regular non-reparse tool paths. Arbitrary environment/`PATH` engines require explicit developer mode.
- Existing path, reparse-point, timeout, bounded-output, current-user pipe, no-shell process, pinned FFmpeg archive, CI, CodeQL, and dependency-audit controls remain in place.
- The updater's remaining trust limitation is documented: this unsigned beta does not pin a project signing key or Authenticode publisher in the client. HTTPS and package checksums do not by themselves prove publisher identity if the release account/feed is compromised.

### Verification note

- On the development PC, real NVIDIA NVENC plus Windows Graphics Capture smoke runs produced valid 6.000-second video and 6.032-second desktop-audio-plus-microphone clips at `BelowNormal` priority. The five-second samples reported 0.1% normalized FFmpeg CPU and 87.3-90.5 MB working set; the six-second retention test kept four segment files including the active file.
- These changes reduce repeated disk work, allocations, and background process contention; they are not a guarantee of zero game lag or input delay on every PC.
- A release candidate still requires the normal locked build/tests, vulnerability audit, real capture/save smoke test, package inspection, and target-game testing before publication.

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
