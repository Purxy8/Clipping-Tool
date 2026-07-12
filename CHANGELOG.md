# Changelog

All notable user-facing changes to ClipForge are recorded here.

## [Unreleased]

## [1.4.0-beta.1] - 2026-07-13

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Added

- ClipForge now plays a short, original confirmation chime after an MP4 has been saved successfully. The sound is enabled by default and can be disabled from the new **Feedback** setting.
- Successful saves show a compact animated confirmation panel while the main window is active. Its interactive **Open** action remains available until the panel is replaced or the window is hidden.

### Changed

- The main window keeps the native Windows caption and system buttons but now requests a black DWM title bar with light caption text. High Contrast mode retains the system-selected colors.
- The app header, settings sidebar, replay controls, player, and gallery receive a short staggered startup reveal; refreshed gallery content and save confirmation use restrained opacity/translation transitions.
- Optional movement is disabled when Windows client animations are disabled, High Contrast is active, or hardware rendering is unavailable. Static hover color states remain available without retaining animation clocks.
- Sound preparation is asynchronous, playback begins only after the save operation succeeds, and all UI feedback remains outside the replay capture and encoding path.

### Verification note

- The native black title bar, system caption buttons, responsive settings panel, player, and gallery were visually checked in the real Windows development build.
- The Release build completes with zero warnings and the deterministic suite passes 29/29 tests, including title-color encoding, confirmation-wave validation, settings persistence, and non-blocking sound-service construction.

## [1.3.0-beta.1] - 2026-07-13

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Added

- The recent-clips gallery can show 4, 8, 10, or 15 clips. The preference is remembered, card widths adapt to the window, and larger sets use a compact horizontal scrollbar.
- Recent clip cards now have a dark right-click menu for showing the exact clip in File Explorer or permanently deleting it after confirmation.

### Changed

- Scrollbars now use a compact dark track and thumb instead of the native light Windows appearance.
- The always-on-top overlay returns `MA_NOACTIVATE` for pointer activation, so mouse clicks remain usable without taking focus or relative-mouse ownership away from a fullscreen game. Unlike the permanent no-activate window style, accessibility tools can still navigate its controls. A failed drag also releases any WPF mouse capture.
- NVIDIA capture now uses the faster NVENC P2 preset with multipass disabled, retaining the existing zero-lookahead, no-B-frame, GPU-resident Windows Graphics Capture path.
- Gallery refresh is latest-request-wins: changing 4/8/10/15 quickly cancels superseded probe and thumbnail work instead of queueing duplicate FFmpeg passes.

### Security

- Explorer reveal revalidates the selected ClipForge-owned file and launches the explicit Windows `explorer.exe` path without a command shell.
- Permanent deletion revalidates the save root, path, stable Windows volume/file ID, timestamp, size, link count, and reparse state, then marks the already validated Windows file handle for deletion to resist same-metadata replacements and path-swap races.
- Gallery JPEGs are decoded fully into frozen in-memory images so WPF does not keep cache files locked; permanently deleting a clip also removes its deterministic cached thumbnail.
- Thumbnail cache keys now include the stable Windows volume/file identity, reject unusable zero IDs, and revalidate that identity around helper execution. Cache cleanup is handle-based and refuses reparse-root or parent mismatches.
- Thumbnail decoding pins the validated clip and save-root handles for the entire FFmpeg read and cache commit, blocking same-path write/delete swaps without copying large recordings. Legacy v1.2 thumbnail keys are removed during normal loading and permanent deletion.

### Verification note

- The Release build completes with zero warnings and the deterministic suite passes 28/28 tests, including same-metadata replacement rejection and thumbnail-unlock tests.
- A real Windows Graphics Capture plus NVIDIA NVENC run with desktop audio and microphone produced a valid 6.032-second MP4. The five-second sample reported 0.4% normalized FFmpeg CPU, 92.8 MB working set, and `BelowNormal` priority on the development PC.

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
