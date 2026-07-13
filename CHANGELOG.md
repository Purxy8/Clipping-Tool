# Changelog

All notable user-facing changes to ClipForge are recorded here.

## [Unreleased]

## [1.7.0-beta.1] - 2026-07-13

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Added

- The Library now includes a dual-handle, frame-accurate trim editor for a selected local clip. A successful export creates a separately named trimmed MP4, refreshes the Library, and keeps the normal clip by default; only then does ClipForge offer an optional original-file deletion with another identity check.
- Library browsing can be filtered between **All**, **Normal**, and **Trimmed** clips without treating unrelated MP4 files as ClipForge recordings.

### Performance

- Trim export runs as a single below-normal-priority local FFmpeg job and is disabled until Instant Replay is stopped, preventing the trim encoder from intentionally competing with live capture. Frame-accurate re-encoding can still use noticeable resources and is not presented as a zero-lag or instant operation.

### Security

- Trimming uses the pinned private FFmpeg through local-file-only MOV/MP4 arguments and no command shell. The selected source and save root stay pinned while a unique partial is written, validated, and atomically committed without overwriting another clip; cancellation and failure clean the owned partial and leave the original unchanged.
- Optional post-trim deletion defaults to keeping both files and revalidates the original's stable Windows identity and guarded single-link deletion policy after the trimmed output has committed.

### Verification note

- The Release solution builds with zero warnings and zero errors, formatting is clean, and the deterministic suite passes 37/37 tests. Real silent and audio trim smoke runs produced frame-accurate 640x360 30 FPS outputs, retained the source clips, and left no partial or orphaned files.
- The current NuGet vulnerability audit reports no known vulnerable direct or transitive packages. These checks validate the tested development PC and cannot guarantee identical performance on every system.

## [1.6.0-beta.1] - 2026-07-13

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Added

- A new **Library** window opens from Recent clips and shows up to the newest 100 validated local ClipForge recordings in a recycling, scrollable list. Selecting a clip keeps playback inside ClipForge with play/pause, restart, seeking, 10-second skips, mute, volume, file reveal, and guarded permanent deletion.

### Fixed

- Appearance colors now update the resource dictionary that actually owns the theme values instead of creating an ineffective root shadow. Existing shared brushes repaint in place, so **App background** changes the full canvas and **Panels & controls** changes the Capture settings sidebar, cards, controls, menus, Library, and overlay immediately without a restart.
- The Library explicitly primes WPF's manual media graph while muted, then pauses or continues according to the requested playback state. This prevents a selected clip from remaining on a blank disabled player while waiting indefinitely for `MediaOpened`.

### Performance

- Valid clip metadata is cached in a bounded 512-entry cache keyed by stable Windows file identity, size, and last-write time. Concurrent Recent clips and Library loads share one low-priority probe, unchanged refreshes launch no additional `ffprobe` process, and a changed file is revalidated before a new result is cached.
- Decoded, frozen thumbnail bitmaps use a bounded 128-key weak cache, avoiding repeated JPEG decoding and file locks without retaining image memory under pressure.
- Library and main-window players keep at most one decoder active. Hiding, deactivating, or closing either surface cancels helper work, stops its timer, closes the media graph, and restores the selected position paused only when the window becomes active again.
- Replay state ticks rebuild the full main UI only while the window is both visible and active; a background/full-screen game receives only deduplicated tray and visible-overlay state updates.

### Verification note

- The Release app and capture-smoke projects build with zero warnings and zero errors, formatting is clean, and the deterministic suite passes 32/32 tests. A real local MP4 was selected and played inside the Library with working pause, seeking, skip, mute, volume, and timeline controls.
- Hardware Windows Graphics Capture plus NVIDIA NVENC smoke runs at 720p and Source both produced 355 frames over 6.015 seconds at 59.226 average FPS with desktop audio and microphone. A forced 720p GDI fallback produced the same frame count and average FPS; measured normalized FFmpeg CPU was 0.1% on the hardware path and 1.7% on the fallback path on the development PC.
- With the app inactive after Library playback, the five-second sample measured 0.000% normalized ClipForge CPU and no lingering FFmpeg/FFprobe helper process. The current NuGet vulnerability audit reports no known vulnerable direct or transitive packages. These measurements validate the tested PC and cannot guarantee zero latency on every game, GPU, driver, or fullscreen mode.

## [1.5.0-beta.1] - 2026-07-13

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Added

- Appearance controls now customize the app background, accent/buttons, or panels/controls from one compact target selector, with safe dark presets, custom colors, readable derived states, and a per-target reset.
- Recent clip cards display their file size and use the available gallery width before switching larger 8/10/15-item sets to horizontal scrolling.

### Changed

- The saved-clip confirmation is now a clearer, short low-pitched pop instead of the previous quiet high-pitched chime.
- The startup reveal remains brief and respects Windows reduced-motion, High Contrast, and rendering-capability preferences.
- The replay overlay uses an opaque no-activate window without a layered transparent surface or WPF drop shadow, reducing compositor work over fullscreen and borderless games.

### Performance

- Capture selection now tests direct and compatibility-transfer Windows Graphics Capture across every verified hardware encoder before accepting a hardware-plus-GDI fallback. This avoids prematurely selecting a slower scaled GDI path on some hybrid-GPU systems.
- Fixed-resolution GDI fallback scaling, including 720p, uses FFmpeg's lower-cost `fast_bilinear` scaler; Source/native capture remains unscaled.
- Hidden and tray operation no longer performs full main-window control, executable, storage, player, or gallery work on every engine-state tick. Gallery helper work is cancelled while inactive and the embedded player releases its media source while hidden.

### Verification note

- Release capture smoke runs at 720p, 1080p, and Source on the development PC all used direct Windows Graphics Capture plus NVIDIA NVENC, produced 355 frames over 6.015 seconds with the same 59.226 average FPS, and reported 0.0-0.2% normalized FFmpeg CPU. Separate forced 720p NVIDIA NVENC plus GDI fallback runs also produced 355 frames at 59.226 FPS, one mixed-audio stream, `BelowNormal` priority, 1.3% normalized CPU, and 217-226 MB working sets. These measurements validate the tested PC and do not guarantee zero game latency on every GPU, driver, or fullscreen mode.
- The Release build completes with zero warnings and the deterministic suite passes 30/30 tests, including 720p filter selection, hybrid-GPU capture ordering, theme color safety, adaptive gallery sizing, file-size formatting, and confirmation-wave validation.

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
