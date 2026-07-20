# Changelog

All notable user-facing changes to ClipForge are recorded here.

## [Unreleased]

### Fixed

- Windows-sign-in autostart now validates and binds the configured 4/8/10/15 recent clips before replay suppresses automatic library discovery. Opening the foreground window can then fill missing posters from that identity-bound snapshot without repeating folder enumeration or FFprobe work, fixing the single poster-less card left by a hidden autostart session.
- Fixed-resolution WGC capture now gives its already downscaled input a four-frame queue, while Source/native retains the two-frame low-latency queue. This restores bounded pacing headroom for 1080p capture during stretched/custom-resolution fullscreen transitions without reverting the larger native queue that caused foreground desktop/game latency.
- Settings loading now falls back safely when the local JSON file is temporarily locked or inaccessible, concurrent disposal no longer destroys the serialization gate underneath an operation that already started, and the default clips folder remains fully qualified even when Windows media-folder discovery returns no path.
- FFmpeg setup now treats FFmpeg and FFprobe as one verified installation. It publishes the prepared pair with a same-volume directory swap, retains the old pair until both new tools pass post-publish verification, and restores the complete backup after a failure instead of leaving capture installed while Library previews are broken.

### Verification note

- Release builds complete with zero warnings/errors, the deterministic suite passes 54/54 tests, and the curated capture matrix passes 75 geometry combinations plus 36 real FFmpeg encodes at 30/60 FPS with no duplicated frames.
- The scaled-queue policy includes custom 1290x980 coverage. A synthetic matrix cannot reproduce a game monopolizing the GPU or every exclusive-fullscreen driver path, so a final target-PC CS2/custom-resolution smoke remains required.

## [1.9.0-beta.7] - 2026-07-16

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and remains a GitHub pre-release.

### Fixed

- The capture FFmpeg child now runs at Below Normal CPU priority on every capture path and at Below Normal Windows GPU scheduling priority. This makes WGC capture, D3D11 preset scaling, and hardware encoding yield scheduling time to the foreground game and Desktop Window Manager instead of competing at the same process class.
- WGC creates its graphics graph asynchronously and was observed returning to Normal after an early GPU-priority assignment succeeded. ClipForge now reapplies the policy after the live D3D/WGC graph is initialized and after every capture-process renewal, then reads both CPU/GPU classes every 30 seconds and writes them again only when an observed value has drifted.
- Start Replay now cancels and waits for both Main and Library refresh pipelines before launching capture. Automatic clip discovery, FFprobe, and thumbnail generation remain fully deferred throughout Buffering and Ready rather than resuming approximately half a second after startup. Existing cached cards remain visible, explicit local playback and trimming remain available, and trusted save/trim outputs or deletions update the identity-bound in-memory views without launching media helpers. One full refresh resumes after replay stops.
- Main and Library now preserve a strict one-decoder invariant when the current clip is deleted. Main keeps the replacement poster-only while Library owns playback, then restores replay-safe Play/Trim controls after Library closes without silently creating a second media graph.

### Diagnostics and privacy

- The bounded local capture-lifecycle journal now records the observed CPU and GPU scheduling classes with its existing numerical process counters. It remains local and still records no pixels, audio, file names, device names, or user input.

### Verification note

- Release builds complete with zero warnings/errors, formatting and whitespace checks are clean, and the deterministic suite passes 51/51 tests. New policy assertions cover full-session automatic-library suppression and both CPU/GPU capture scheduling targets.
- A real 1920x1080/60 WGC/NVIDIA NVENC session with desktop audio and microphone ran at 0.3% normalized CPU and 110.7 MB working set while both observed scheduling classes were Below Normal. Its saved MP4 contained 359 frames over 6.016 seconds at 60.003 FPS with no frame-timestamp gap above 17 ms and one mixed audio stream.
- A controlled mixed-audio 1920x1080/60 replay test moved both scheduling classes back to Normal, crossed the 30-second monitor boundary, and verified that ClipForge repaired both to Below Normal. The saved MP4 contained 360 frames over 6.01 seconds at 60.01 FPS with no gap above 17 ms.
- A service-owned process-renewal regression retained completed segments, replaced the FFmpeg PID, and kept both the replacement CPU and GPU scheduling classes Below Normal. Its mixed-audio output contained 360 frames over 6.005 seconds at 60.01 FPS with no gap above 17 ms.
- Separate mixed-audio 60 FPS checks passed at fixed 1280x720 and Source/native 2560x1440. Both produced 359 frames over 6.016 seconds at 60.003 FPS with no gap above 17 ms; their normalized CPU samples were 0.2% and 0.1%, respectively.
- Replay also advanced continuously during a paced, one-thread software trim while the 1920x1080/60 WGC/NVENC capture stayed active. The trimmed 640x360 source retained 156 frames and audio over 5.221 seconds; the replay saved immediately afterward contained 4.005 seconds at 60.015 FPS with no frame gap above 17 ms.
- These checks verify scheduling, capture cadence, output, and the absence of replay-time background media helpers on the development PC. They cannot measure the user's subjective mouse latency inside every Call of Duty/fullscreen, driver, overlay, multi-recorder, or mixed-refresh configuration, so this beta does not claim universal zero-lag behavior.

## [1.9.0-beta.6] - 2026-07-16

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and remains a GitHub pre-release.

### Fixed

- The 30-minute Windows Graphics Capture renewal is now owned by the replay service instead of depending on the WPF dispatcher, player state, or an available UI recovery callback. It runs as one PID-scoped background operation, cannot overlap another renewal, remains serialized behind an active save, and is cancelled safely during shutdown.
- Unlock, console reconnect, remote-session reconnect, resume, and same-size graphics-device transitions renew the WGC generation so a stale D3D frame pool cannot remain attached after Windows replaces the desktop graphics device.
- Hidden or deactivated Main and Library players now close and remove their Media Foundation media graph instead of retaining an empty decoder/D3D graph for the lifetime of the app. Player elements are recreated only for an explicit foreground open or play action, and interrupted seek gestures release only mouse capture owned by that ClipForge window.

### Performance

- The direct WGC/NVENC path now limits FFmpeg's simple and complex filter pools to one worker and reduces its video input queue from eight full-size GPU frames to two. The compatibility GDI queue remains unchanged.
- Mouse-cursor composition is disabled by default to keep games on the lower-impact hardware-cursor path. A new **Include mouse cursor in clips** switch restores cursor recording when it is needed.
- Desktop Duplication was measured as a possible alternative but is not enabled: native capture was leaner in handles and threads, while fixed 1080p required a system-memory scaling path that used materially more CPU, memory, and GPU 3D work than the current WGC path.

### Diagnostics and privacy

- ClipForge now keeps a bounded, rotating local capture-lifecycle journal under its local app-data Diagnostics folder. It records only timestamps, capture geometry/backend, cursor state, and numerical process resource counters every five minutes and around start/renew/stop events; it never stores pixels, audio, file names, device names, or user input.

### Verification note

- Release builds complete with zero warnings/errors and the deterministic suite passes 51/51 tests, including service-owned renewal concurrency/disposal, concurrent replay-service disposal, exact WGC/GDI queue and cursor arguments, media-player release policy, and the bounded local journal.
- A 1920x1080/60 WGC/NVIDIA NVENC session with desktop audio and microphone passed 12 consecutive process renewals. Retired PIDs exited, completed replay segments survived, the test host settled to 494 handles and 67.1 MB private memory, and the saved MP4 contained 359 frames over 6.005 seconds at 60.003 FPS with no frame-timestamp gap above 17 ms.
- The actual UI-independent scheduled-renewal path separately passed three consecutive replacements with the same mixed-audio configuration. After finalization its test host settled to 498 handles and 30.4 MB private memory.
- A final mixed-audio scheduled-renewal regression on the release candidate produced 360 frames over 6.005 seconds at 60.01 FPS. DWM moved from 6,169 to 6,162 handles and the Windows audio engine from 23,275 to 23,272 across renewal and stop, with no retained-handle growth in that run.
- These tests verify the corrected lifecycle and resource bounds on the development PC. They cannot simulate every multi-hour game, driver, overlay, mixed-refresh display, or exclusive-fullscreen combination, so this beta does not claim universal zero-lag behavior.

## [1.9.0-beta.5] - 2026-07-15

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and remains a GitHub pre-release.

### Fixed

- Fixed a recovery-state race that could permanently suppress every later 30-minute WGC renewal after the bounded health-recovery budget was exhausted. Recovery requests now use an atomic, generation-tagged gate; stale dispatcher callbacks cannot release a newer request, shutdown, queued, rejected, failed, and successful paths all resolve their own request, and a one-shot display/device refresh arriving behind a health event is coalesced and dispatched after that event releases the gate.
- Scheduled renewal now gives FFmpeg a one-second bounded graceful shutdown window so its WGC frame handler, capture session, frame pool, D3D device, COM resources, and selected WASAPI inputs can close normally. Kill-on-job-close remains the final fallback, and a replacement is never allowed to overlap uncertain old resources.
- The desktop overlay is no longer a permanently topmost surface. It becomes topmost only while explicitly shown, hides after ten idle seconds, forcibly releases a stuck mouse capture at a 15-second hard limit, stops its timer while hidden, and reuses the same HWND when the shortcut opens it again.
- Old pre-session-scoping `Buffer/session-*` crash residue is now removed by a fail-closed, bounded background migration instead of synchronously walking thousands of files on the WPF startup thread. Ownership is snapshotted before automatic replay can start, and capture waits asynchronously for that one-time task so deletion/antivirus work never overlaps recording. Other Windows-session roots, recent data, reparse points, nested content, unexpected files, and possible active owners are left untouched.
- Library replay-state updates no longer rebuild unchanged trim/player state every second.

### Root-cause evidence

- The affected five-minute Call of Duty clip contained 18,000 nominal CFR60 frames but only 2.38 meaningful large-scene changes per second, 96.03% repeated macro frames, and a longest held scene of 8.43 seconds. Its audio decoded continuously. The immediate next clip after recorder restart reached 48.35 meaningful changes per second.
- A second affected CS2 clip fell to 1.09 meaningful changes per second with a 36.82-second held scene, while the immediate post-restart clip reached 59.76 changes per second. This isolates the failure to long-lived WGC video delivery rather than MP4 timing, NVENC capacity, or audio capture.
- The stale recovery gate could leave one FFmpeg/WGC generation alive indefinitely, matching that restart-sensitive failure. Fresh beta4 gameplay remained healthy after its first scheduled renewal, so this release keeps the proven low-overhead 1080 path instead of switching to a `scale_d3d11` path that failed its RTX 5080 runtime probe with a D3D texture-pool error.

### Verification note

- Release builds complete with zero warnings/errors and the deterministic suite passes 48/48 tests, including a 1,000-iteration suppression race, stale-request isolation, overlay hard-limit behavior, bounded legacy cleanup, and existing capture/security coverage.
- Two accelerated 12-renewal 1920x1080/60 WGC/NVIDIA NVENC tests passed, one video-only and one with desktop audio. All 24 replacement PIDs were distinct, retired FFmpeg processes were gone before their successor was accepted, completed ring segments survived, and no overlapping recorder remained.
- The audio run produced a validated 6.005-second MP4 with 359 frames at 60.003 FPS, one audio stream, and no video-frame timestamp gap above 17 ms. After forced finalization the test host settled to 481 handles and 50.7 MB private memory; FFmpeg remained steady at roughly 1,103 handles and 108–109 MB per generation.
- Accelerated renewal tests exercise lifecycle/resource accumulation equivalent to repeated process rotations, but they cannot reproduce every multi-hour game/driver/compositor combination. Affected Call of Duty validation after installation is still required before claiming universal zero-lag capture.

## [1.9.0-beta.4] - 2026-07-15

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Fixed

- Long-running Windows Graphics Capture sessions now renew their FFmpeg/WGC frame pool every 30 minutes to mitigate the observed long-session degradation in which picture freshness collapsed while CFR timestamps falsely continued at 60 FPS.
- Renewal waits briefly for the next two-second segment boundary, retains every completed replay segment in the existing ring, drops at most the new partial tail, reconnects selected audio devices, and resumes at the next safe segment number. Same-size Windows display/device transitions and resume from sleep use the same buffer-preserving path when the selected display returns with compatible source/output dimensions. Changed dimensions (or GDI's baked geometry) follow the full restart path, direct-WGC coordinate-only moves remain in place, and a display that remains unavailable stops replay safely.
- The first starvation/hang recovery now preserves the replay ring instead of clearing it. A second real fault retains the existing bounded Source-safety fallback, while routine scheduled renewals do not consume that two-event fault budget.
- Routine renewal keeps replay logically active so a save hotkey queues behind the short maintenance window, does not tear down an in-use Main/Library player, re-hashes the pinned FFmpeg executable before creating the replacement, and bounds graceful-stop, forced-kill, and cleanup waits so a wedged child cannot hold ClipForge's lifecycle gates forever.

### Reliability

- A second conservative health tier can detect an aged active-fullscreen process that first sustained healthy cadence and later degraded near 16 meaningful FPS at a 60 FPS target. A session that has always run at 15/16 FPS does not arm this tier; legitimate 24/30 FPS content does not trigger it, and a replacement-process counter reset or sustained fullscreen exit clears the earlier healthy latch. The original severe eight-second detector remains unchanged.
- ClipForge now treats premature EOF or failure of either FFmpeg progress/diagnostic pump as a broken capture-control channel instead of allowing a live child process to backpressure silently.

### Root-cause evidence

- The reported `00:26` clip decoded cleanly as 1920x1080 CFR 60 with exactly 10,800 frames and continuous audio, but perceptual analysis found 120.92 seconds (67.17%) of frozen picture and only about 16 meaningful visual updates per second. A clip made immediately after the same recorder started was healthy, and its FFmpeg PID had remained unchanged for approximately 3 hours 42 minutes.
- Sparse HUD changes kept packet timestamps and some pixels advancing, which explains why the previous 90%-duplicate and seven-second hard-hang checks did not fire. The bounded process lifetime is therefore the primary protection; the moderate counter tier is additional coverage rather than a claim of pixel-perfect classification.

### Verification note

- Release builds complete with zero warnings/errors, formatting and whitespace checks are clean, and the deterministic suite passes 47/47 tests. New assertions cover resumed segment numbering, negative-number rejection, the exact 30-minute WGC boundary, GDI exclusion, prior-healthy-then-degraded detection, always-low/young/idle exclusions, counter-reset isolation, legitimate 17/24/30 FPS cases, and separate scheduled/fault recovery budgets.
- Interactive 1920x1080 60 FPS WGC/NVIDIA NVENC renewal passed two consecutive replacements in one desktop-audio session. The FFmpeg PID changed each time, 3 then 5 completed segments remained, replacement segment IDs continued at 4 then 6, and the pre-save six-second ring snapshot contained completed segments 4/5/6 from both replacement processes. The validated MP4 contained 359 decoded frames over 6.005 seconds at 60.003 FPS, no video-frame timestamp gap above 17 ms, 283 continuous audio packets with at most a 0.001 ms timeline gap, and a clean full decode. The two API windows were 2.53 and 3.19 seconds, including time deliberately spent waiting at a segment boundary while the old process was still capturing; a persisted JSON report records PID and segment provenance.
- These checks directly verify renewal and ring continuity on the development PC. They cannot compress a real multi-hour Call of Duty session into an automated test, so affected-game validation after installing this beta remains necessary before describing every driver/game combination as universally lag-free.

## [1.9.0-beta.3] - 2026-07-14

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Fixed

- Windows Graphics Capture now reacquires its frame pool whenever the selected monitor's source dimensions change, including fullscreen/custom-mode transitions whose fixed preset still resolves to the same encoded size. Coordinate-only monitor moves continue without discarding the buffer.
- Fixed WGC presets now use the capture source's low-overhead point sampler for required downscaling, while **Source** and already-fitting presets stay on the native surface path instead of activating the resizer.

### Reliability

- The capture process now exposes machine-readable FFmpeg progress to a conservative WGC health monitor. It can distinguish an advancing CFR stream made almost entirely of duplicate frames from a static desktop by requiring a fullscreen foreground window, sustained coverage, and recent-input-aware confirmation; it also detects an alive recorder whose progress and segments both stop advancing.
- Automatic recovery is bounded per manual replay session. The first detection reacquires the same verified WGC strategy; the second freshly probes temporary, non-persisted **Source** geometry and proceeds only with a verified WGC path. Later detections warn without starting a restart loop. A recovery waits for an active clip save to finish and rejects stale events from an earlier FFmpeg PID.
- Each continuous FFmpeg capture child is attached immediately to a private Windows Job Object with kill-on-close containment. Normal shutdown remains graceful, while abrupt ClipForge termination no longer intentionally leaves its owned recorder running.

### Performance

- Required fixed-preset scaling now favors reduced live shader work over bilinear filtering, and the redundant capture-side FPS filter was removed so the WGC source and final CFR output own cadence without an extra filter stage.
- Progress health monitoring parses one small text record per second and does not decode the rolling video or scan the complete buffer.

### Release validation

- Interactive WGC release checks now require decoded-motion validation for Source and fixed presets because nominal CFR timestamps alone can hide repeated stale frames.
- Capture-lifecycle validation now includes a disposable parent-termination check proving that the exact Job-owned FFmpeg child exits promptly, in addition to normal Start/Stop/Exit coverage. These checks remain machine-specific and do not establish universal lag-free fullscreen capture.

### Verification note

- The Release solution builds with zero warnings and errors, formatting and whitespace checks are clean, the deterministic suite passes 47/47 tests, and the current NuGet audit reports no known vulnerable direct or transitive packages from the configured source.
- Regression coverage verifies the point-scaled/bypass argument paths, removal of the redundant FPS filter, complete progress-record parsing, healthy/non-fullscreen/transient/active/idle starvation cases, continuing-input gating, 75% fullscreen sampling, sustained eligibility reset, source-size restart policy, and kill-on-close owned-child lifetime. Code review separately verifies the save-deferred, PID-scoped, freshly probed Source recovery branch.
- Live WGC smoke on the development PC produced a 1920x1080 mixed-audio clip with 359 frames over 6.016 seconds at 60.003 FPS and a Source/native 2560x1440 mixed-audio clip with the same duration, frame count, and cadence. The desktop-independent matrix passed 75 geometry combinations and 36 real 30/60 FPS motion encodes with 0.0% duplicates.
- A three-frame-per-three-second synthetic input followed by final CFR pacing reported 177 duplicate frames out of 180 through the new progress channel; the removed capture-side FPS filter masked the same condition by reporting zero, confirming why it could not support recovery diagnostics.
- The Job Object coverage verifies both explicit handle disposal and abrupt termination of a separate owner process without killing its child directly.
- The deterministic checks do not recreate Call of Duty, an affected exclusive-fullscreen composition path, or sustained GPU contention. The progress watchdog observes CFR-inserted duplicates rather than decoding pixels, so a source that supplies already-timestamped identical frames can evade it. Decoded-motion validation on the affected or equivalent target PC remains required before this change can be described as universally resolving fullscreen stutter.

## [1.9.0-beta.2] - 2026-07-14

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Fixed

- Recent and Library cards now keep their full thumbnail area when an image is still missing or cannot be decoded, with a stable preview placeholder instead of collapsing around the filename and play icon.
- Clips saved while replay remains active no longer stay thumbnail-less forever. ClipForge paints cached metadata immediately, then hydrates a bounded set of missing thumbnails while the app is visible and foreground.
- The large Main and Library players show the selected cached poster before an explicitly deferred replay-time media decoder is opened.

### Performance

- Replay-safe thumbnail hydration reuses the already validated clip snapshot, avoids repeat media probes, generates only one image at a time with one FFmpeg thread at Idle priority, and stops on focus loss, capture transitions, trimming, close, or a newer refresh.
- Recent hydration is capped by the selected 4/8/10/15 gallery size. Library hydration prioritizes the selected clip and is capped at 12 items rather than decoding the complete 100-item view.

### Verification note

- The Release solution builds with zero warnings and errors, formatting is clean, `git diff --check` is clean, and the deterministic suite passes 43/43 tests.
- Regression coverage verifies cached-first rendering, bounded and cancellable hydration, cache reuse, no repeated media probe, deferred replay-time decoders, and Idle-priority helper execution. The production thumbnail command also generated a valid 640-pixel JPEG from an affected local trimmed clip.
- Thumbnail work is deliberately bounded and low priority, but foreground-game validation remains hardware dependent; this release does not claim universally zero capture impact.

## [1.9.0-beta.1] - 2026-07-14

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Fixed

- Fixed-resolution capture no longer forces every display into a padded 16:9 canvas. The 720p/1080p/1440p/2160p choices are now aspect-preserving **up to** bounds that never upscale, keep custom square, portrait, 4:3, 16:10, and ultrawide geometry, and bypass resizing when the selected display already fits.
- Library and latest-player media are no longer disabled for the entire replay session. Browsing, play/pause, seeking, and frame-accurate trimming remain available while replay is steadily buffering or ready; only the short start, save, and stop transitions suspend presentation and release the decoder.
- Opening or refreshing Library during replay no longer allocates a decoder until the user explicitly selects Play or requests Trim. Main and Library replace the WPF media element for every new source so a late event from a closed graph cannot mutate or autoplay the next clip.
- A standard trim keeps replay startup blocked until cancellation has actually unwound, even if Library closes. A replay-coexisting trim remains restart-compatible.
- A debounced Windows display-mode refresh re-resolves the selected monitor by device name. Source/output-size changes and GDI's baked geometry restart safely, while direct WGC coordinate-only or same-output fixed-preset changes preserve the current rolling buffer. Explicit replay intent now survives a mode-transition fault and recovers using the newest geometry.

### Performance

- Direct hardware Windows Graphics Capture runs at Normal priority so a fullscreen foreground workload is less likely to starve the capture feed. Compatibility, GDI, and software capture fallbacks remain below normal, while media probes, thumbnails, and other auxiliary FFmpeg work run at Idle priority.
- During steady replay, Recent and Library refreshes use existing cached thumbnails instead of starting missing thumbnail decodes, and media is connected only for the explicitly selected foreground player. Replay-time playback starts muted because unmuted desktop playback can be captured by the rolling buffer.
- Replay-coexisting trim forces real-time-paced one-thread `libx264`, skips hardware-encoder probes, avoids the GPU encoder, and runs at Idle priority. This protects capture headroom at the cost of an export that can take at least approximately the selected duration.

### Verification coverage

- Added a desktop-independent `--resolution-matrix` smoke mode for exhaustive geometry checks plus curated real 30/60 FPS encodes across standard, 16:10, 4:3, ultrawide, super-ultrawide, square, portrait, and odd-sized inputs. `--matrix-exhaustive` encodes every source/preset pair.
- Added an interactive `--wgc-matrix` mode for sequential Source and fixed-preset checks against the selected live Windows display, including cadence/motion validation, and a `--replay-trim-smoke` mode that exercises a constrained trim while real replay capture remains active.
- These checks do not reproduce every game's exclusive-fullscreen composition path, custom internal resolution, affected GPU/driver, or input-latency conditions. Target-game validation on affected or equivalent hardware remains required before claiming a hardware-specific stutter is resolved.

### Verification note

- The Release solution builds with zero warnings and errors, formatting is clean, `git diff --check` is clean, and the deterministic suite passes 42/42 tests. The NuGet audit reports no known vulnerable direct or transitive package from the configured sources.
- The exhaustive desktop-independent run passed 75/75 source/preset geometry cases and all 150 real encodes (75 at 30 FPS and 75 at 60 FPS). Every case reported the expected size, frame rate, and frame count, met the 33.334/16.667 ms cadence limits, and decoded with a 0.0% duplicate ratio across standard, 16:10, 4:3, square, portrait, ultrawide, super-ultrawide, and odd-sized inputs.
- The replay-coexisting audio trim smoke produced a validated 5.221-second, 156-frame 640x360 30 FPS MP4 with one audio stream, retained the original, and left no partial or orphaned helper process.
- This automated build session cannot access the interactive Windows Graphics Capture desktop (`gdigrab` error 5 after fallback). Fullscreen/custom-mode motion cadence and game impact still require confirmation on the installed build and affected PC; these results are not a universal zero-stutter guarantee.

## [1.8.0-beta.1] - 2026-07-14

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Added

- An opt-in **Start ClipForge and replay with Windows** setting lets an installed ClipForge build launch quietly after Windows sign-in and start the rolling replay only after settings, devices, and the local FFmpeg engine have initialized. The setting is off by default, and a normal manual launch remains interactive.

### Reliability

- Login startup waits briefly for Windows display and audio devices, makes only one bounded retry after a temporary capture failure, and reports a persistent failure through the tray instead of opening a blocking window.
- A stale autostart launch honors the saved opt-out, removes its owned shortcut when possible, and exits without starting capture. Duplicate login launches also leave an existing ClipForge window in the background.

### Security

- Windows autostart uses a per-user Startup shortcut owned by the installed package, targets only `ClipForge.exe`, and passes only the fixed private `--autostart` argument. Portable/development builds fail closed, no service or scheduled task is installed, and disabling the setting or uninstalling removes the shortcut.

### Verification note

- The Release solution builds with zero warnings and errors, formatting is clean, and the deterministic suite passes 40/40 tests, including launch parsing, fixed shortcut registration, default-off persistence, duplicate-instance behavior, and every automatic replay safety precondition.
- The actual sign-in/restart path still requires confirmation with the installed package because the automated build session cannot reboot the interactive Windows desktop.

## [1.7.0-beta.2] - 2026-07-13

### Release status

- Unsigned public beta while the SignPath Foundation application remains pending. Windows can show an unverified-publisher or SmartScreen warning.
- This beta is not an official trusted release and must remain a GitHub pre-release rather than the latest stable download.

### Fixed

- Opening Trim from the main Latest clip player now waits for that exact clip to finish opening before it initializes or enables the editor. Direct and Library trimming therefore use the same decoded duration instead of racing the media player with a slightly longer container duration.
- Trim output validation now compares the nominal source/output frame rate while retaining strict video, dimensions, audio, duration, path, and file-identity checks. Valid short 60 FPS selections are no longer rejected merely because their selection-local average frame rate differs from the complete rolling clip.
- Clip export now pins each replay-buffer manifest entry to the configured two-second segment duration. This removes the periodic frame gaps previously introduced when AAC made a Matroska segment's container duration slightly longer than its video timeline.

### Performance

- Starting Instant Replay now cancels thumbnail/probe refreshes and fully releases the main and Library media decoders. Saved clips are queued for a paused refresh after capture stops instead of immediately reopening a WPF decoder while the game is being recorded.
- Windows Graphics Capture skips its resize path when a fixed preset already matches the selected display's native even dimensions, avoiding unnecessary GPU scaling for common native 1080p capture.
- Capture smoke validation now checks maximum video packet spacing, monotonic audio timestamps, and A/V duration alignment so two-second join stutter cannot pass on average-FPS measurements alone.

### Interface

- The important Library launcher is now a larger accent-colored **Open Library** button with clearer hover, keyboard-focus, tooltip, and accessibility treatment while retaining the selected appearance theme.

### Verification note

- The Release solution builds with zero warnings and errors, formatting is clean, and the deterministic suite passes 37/37 tests. A real three-segment 1920x1080 60 FPS AAC concat completed at 60.003 average FPS with a 17 ms maximum video-frame delta, monotonic audio DTS, and a 21.833 ms A/V duration delta.
- A real 1920x1080 ClipForge recording that reports nominal 60 FPS but 59.164 average FPS produced a valid five-second trimmed copy at nominal 60 FPS and 58.65 selection-local average FPS. The original remained unchanged and no partial or orphaned helper remained.
- The automated desktop-capture smoke could not access the interactive Windows desktop from this build session (`gdigrab` error 5), so game-specific frame-time impact still needs confirmation on the installed build and the affected friend's PC. These checks cannot guarantee zero performance impact on every game or hardware combination.

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
