# ClipForge

ClipForge is a Windows instant-replay recorder: leave a private rolling buffer running, then save the moments that already happened as an MP4. ClipForge keeps capture local while providing complete in-app playback controls, a customizable dark interface, conservative capture optimizations, and tightly scoped media-library and update handling.

Copyright (C) 2026 Purxy8. ClipForge is free and open-source software licensed under the [GNU General Public License v3.0 or later](LICENSE). Project-owned source code, build scripts, documentation, UI artwork, icons, and other assets use that license unless a file clearly says otherwise. Third-party software retains its own open-source license; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Features

- Replay lengths: 30 seconds; 1, 2, 3, 5, 10, 20, 30, or 40 minutes; and 1 hour.
- One selected display, captured at source resolution or an aspect-preserving **up to 720p, 1080p, 1440p, or 2160p** bound. Fixed presets never upscale or add a padded canvas.
- Selectable frame rate.
- Optional desktop audio from a selected Windows output device.
- Optional microphone audio from any active Windows capture device.
- A configurable clips folder, defaulting to `%USERPROFILE%\Videos\ClipForge`.
- Save from the app or anywhere in Windows with a configurable global shortcut (**Ctrl+Shift+F10** by default).
- Toggle a compact, always-on-top replay overlay with a second configurable shortcut (**Ctrl+Shift+F9** by default).
- Close the main window to the notification area while replay continues; use the tray menu to reopen ClipForge, save a clip, or exit.
- Optionally start ClipForge and its rolling replay automatically after Windows sign-in. This installed-build setting is off by default.
- A second launch reopens the existing ClipForge instance instead of competing for capture devices or global shortcuts.
- Play, pause, restart, seek, skip backward/forward 10 seconds, mute, and adjust the volume of the latest saved clip without opening another application.
- Browse 4, 8, 10, or 15 recent ClipForge-generated clips in an adaptive gallery that fills the available width, shows each file size, and scrolls larger sets horizontally.
- Open the full in-app **Library** for up to the newest 100 validated recordings, switch between **All**, **Normal**, and **Trimmed** views, scroll a virtualized list, select any clip, and use the same complete embedded playback controls without opening another player.
- Trim a selected latest or Library clip to an exact range with two timeline handles, including while steady replay is running. ClipForge writes a separate local MP4, keeps the normal clip by default, and asks before deleting the identity-revalidated original.
- Right-click a recent clip to reveal it in File Explorer or permanently delete it after confirmation.
- Customize the app background, accent/buttons, or panels/controls from one compact Appearance selector. Unsafe custom colors are adjusted to preserve dark surfaces and readable button contrast.
- Use a native black Windows title bar and restrained startup/save transitions that honor High Contrast, reduced-motion, and rendering-capability preferences.
- Receive a clear, short confirmation pop after a clip is safely saved, with an option to disable it from the Feedback panel.
- Runtime-tested NVIDIA NVENC, Intel Quick Sync, and AMD AMF H.264 encoding, with software H.264 as a compatibility fallback.
- A live estimate of the disk space needed by the selected rolling buffer.
- Settings remembered between launches.

ClipForge does not require an account, upload clips, or include a social feed. Recording and clip creation happen on the PC.

## Requirements

To run a packaged build:

- 64-bit Windows 10 version 2004 (build 19041) or newer, or Windows 11.
- Enough free local disk space for the selected buffer and a saved copy.
- FFmpeg, installed from ClipForge on first run or provided manually as described below.

The self-contained package includes the .NET runtime. Building from source additionally requires the 64-bit [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and PowerShell 7 or Windows PowerShell.

## Build and run from source

From the repository root:

```powershell
.\scripts\build.ps1
.\scripts\run.ps1
```

If dependencies are already restored and the machine is temporarily offline, use `./scripts/build.ps1 -NoRestore`.

Run the lightweight test executable with:

```powershell
dotnet run --project .\tests\ClipForge.Tests\ClipForge.Tests.csproj --configuration Release
```

The capture-smoke project also provides a desktop-independent geometry/encode matrix, an interactive live Windows Graphics Capture matrix, and a replay-plus-trim coexistence check:

```powershell
dotnet run --project .\tests\ClipForge.CaptureSmoke\ClipForge.CaptureSmoke.csproj -c Release -- --resolution-matrix --matrix-fps both
dotnet run --project .\tests\ClipForge.CaptureSmoke\ClipForge.CaptureSmoke.csproj -c Release -- --trim-smoke --replay-coexisting --audio
dotnet run --project .\tests\ClipForge.CaptureSmoke\ClipForge.CaptureSmoke.csproj -c Release -- --wgc-matrix --matrix-resolutions source,720p,1080p,1440p,2160p --fps 60 --motion-validation
dotnet run --project .\tests\ClipForge.CaptureSmoke\ClipForge.CaptureSmoke.csproj -c Release -- --replay-trim-smoke --resolution 1080p --fps 60
```

Run `--wgc-matrix` from an interactive Windows desktop with the intended game and fullscreen/custom mode already selected. Synthetic and local desktop tests cannot reproduce every affected GPU, driver, exclusive-fullscreen path, or game's internal resolution; target-PC testing remains required.

NuGet restore may use the network the first time the project is built.

## First-run FFmpeg setup

ClipForge uses FFmpeg as its local capture and MP4 engine. If it cannot find `ffmpeg.exe`, the app shows an **Install engine** panel. Selecting **Install engine** downloads the FFmpeg essentials archive over HTTPS from Gyan.dev, extracts `ffmpeg.exe` and `ffprobe.exe`, and places them in:

```text
%LOCALAPPDATA%\ClipForge\Tools\FFmpeg
```

The FFmpeg download is one of two optional network features. An installed release can also check its configured release host for update metadata and download an update after the user requests it. ClipForge does not download or redistribute FFmpeg while building the application.

For a managed or offline installation, place the exact pinned `ffmpeg.exe` and `ffprobe.exe` pair in `%LOCALAPPDATA%\ClipForge\Tools\FFmpeg`, beside the application, or in `Tools\FFmpeg` beside the application. ClipForge verifies both executable SHA-256 values before use. Arbitrary `PATH` or `CLIPFORGE_FFMPEG_PATH` tools are ignored by default; developers can opt into them explicitly by setting `CLIPFORGE_DEVELOPER_MODE=1`, which deliberately bypasses the pinned-tool trust policy and must not be used for a production release.

## Using ClipForge

1. Install or provide FFmpeg when prompted.
2. Choose a display, replay length, frame rate, and output resolution.
3. Enable desktop audio and/or microphone capture, then select the desired devices.
4. Choose the save folder.
5. Select **Start replay**. ClipForge begins building the rolling buffer on local disk.
6. Select **Save last clip** or use the configured Save Clip shortcut after the buffer has content.

The fixed resolution choices are maximum bounds, not forced canvases. ClipForge keeps the selected display's real aspect ratio, never enlarges a smaller source, and does not surround square, portrait, 4:3, 16:10, or ultrawide content with black padding. For example, a 1080x1080 source remains square and a 3440x1440 source selected at **Up to 1080p** is reduced to an even, ultrawide output that fits within 1920x1080. **Source** keeps the native dimensions, rounded down only when an encoder requires even values.

To make replay available automatically after restarting or signing back into Windows, enable **Start ClipForge and replay with Windows** in the settings sidebar. This explicit opt-in is available in the installed app and is off by default. It creates a per-user Windows Startup shortcut; at the next sign-in ClipForge launches in the background, finishes loading the saved capture configuration and local FFmpeg engine, and then starts replay. If initialization or the engine is not ready, ClipForge does not force a capture start. A normal manual launch still opens the main window, and disabling the setting removes the Startup shortcut.

The saved MP4 goes to the selected folder and becomes available in the player and recent-clips gallery. After the save succeeds, ClipForge can play a short confirmation sound and show a compact in-app confirmation; disable the sound at any time from **Feedback**. The player includes a timeline, elapsed/total time, play/pause, restart, 10-second skip controls, mute, and volume. Saving does not stop the rolling buffer, so another clip can be saved later. Stopping replay clears the temporary buffer. Changing the display, resolution, frame rate, or audio configuration while replay is active automatically restarts capture and clears the old buffer; changing only replay length adjusts retention in place. If Windows reports that the selected display changed, ClipForge refreshes it by device name. Source/output-size changes and GDI's baked coordinates or input dimensions restart safely, while direct Windows Graphics Capture keeps the existing buffer for coordinate-only changes and fixed-preset mode changes that resolve to the same encoded size. If a mode transition faults capture before the refresh completes, ClipForge remembers that replay was requested and recovers it with the newest geometry.

The gallery automatically loads only top-level files using ClipForge's generated normal `Clip_YYYY-MM-DD_HH-mm-ss[_N].mp4` and trimmed `Clip_YYYY-MM-DD_HH-mm-ss[_N]_trimmed[_N].mp4` forms. Other MP4 files in the save folder are left untouched and are not automatically decoded by the embedded player. Use the selector above the gallery to show 4, 8, 10, or 15 recent clips; the available clips fill the row, every card shows its size, and larger sets scroll horizontally. Select **Library** to browse up to the newest 100 validated recordings in a recycling list and filter the view to All, Normal, or Trimmed clips. Clicking any Library entry loads it into a large local player with play/pause, restart, timeline seeking, 10-second skips, mute, and volume. While replay is steadily buffering or ready, Library browsing, playback, seeking, and trimming remain available; ClipForge reuses cached thumbnails instead of launching missing thumbnail decodes, keeps only the explicitly selected foreground decoder active, and starts replay-time playback muted. Unmuting is allowed, but its sound is normal desktop audio and can therefore enter the rolling buffer when desktop audio capture is enabled. The short start, save, and stop transitions temporarily suspend presentation work and release the decoder, then restore the selected clip when the transition finishes. Right-clicking a Recent or Library item can reveal the revalidated file in Explorer or permanently delete that exact ClipForge recording after confirmation.

To trim, open the latest clip or a Library clip, enable the trim editor, and move its two handles to the first and last frame you want to keep. When replay is stopped, ClipForge performs the normal local frame-accurate export. During steady replay it switches to a coexistence mode: software `libx264`, one decoder/encoder thread, input paced at real time, and an Idle-priority helper, with no hardware-encoder probe or GPU encoder. ClipForge validates the completed MP4 and then adds it to the Library as a separate trimmed clip. The normal clip is kept by default. Only after a successful export does ClipForge ask whether to delete it; accepting that prompt still revalidates the exact original file identity before deletion.

Select either shortcut in the left settings panel and press a new combination to change it. Each shortcut must contain at least one modifier and a non-modifier key, and Save Clip and Toggle Overlay must be different. If another application owns a chosen combination, ClipForge keeps the previous working registration and reports the conflict.

Closing the main window hides it to the notification area rather than stopping capture. The ClipForge process and FFmpeg capture process must remain running for instant replay to work; choose **Exit ClipForge** from the tray menu when you want them to stop. The compact overlay is a desktop control surface, not an injected in-game overlay. Pointer clicks use Windows mouse-activation suppression so they do not intentionally steal focus or relative-mouse ownership from a game, while the controls remain available to accessibility navigation. Exclusive-fullscreen applications may still display above it.

## Capture performance

At replay startup, ClipForge runs short, real encoding probes instead of assuming that an encoder compiled into FFmpeg is usable with the installed driver. It verifies NVIDIA NVENC, Intel Quick Sync, and AMD AMF, then tries direct and multi-GPU-compatible Windows Graphics Capture across the verified hardware choices before accepting a hardware-plus-GDI fallback. Software H.264 remains the final compatibility fallback. The active strategy is shown in the interface.

Direct hardware Windows Graphics Capture runs at Normal process priority to reduce the likelihood of capture-feed starvation behind a fullscreen game. Compatibility and GDI/software capture fallbacks run below normal, and auxiliary probe, thumbnail, and replay-coexisting trim helpers run at Idle priority. Desktop/microphone PCM transfer is bounded and reuses pooled buffers so capture cannot grow an unlimited in-memory queue. Replay retention follows FFmpeg's sequential segment names incrementally instead of repeatedly enumerating and sorting the entire buffer. Valid unchanged clip metadata is reused from a bounded file-identity cache, simultaneous Recent/Library requests coalesce to one probe per clip, and frozen thumbnail decodes use a bounded weak cache. Fixed presets downscale directly to the smallest even aspect-preserving size inside their bound, bypass scaling when the source already fits, and never pay for a padded fixed canvas. Hidden, inactive, and tray states cancel unnecessary helpers, release embedded-player sources, keep at most one decoder active, and avoid rebuilding the full UI on background state ticks. During steady replay, Library uses cached-only thumbnails and opens media only after an explicit selection or play action. Player position timers run only while their visible, active window is playing a clip. NVIDIA capture favors game headroom with the fast P2 preset, single-pass encoding, no lookahead, and no B-frames.

These are conservative reductions in disk scans, allocation pressure, and background CPU contention; they do not change the captured format or promise zero lag. Hardware encoding generally reduces CPU pressure, but performance still depends on the GPU driver, resolution, frame rate, game, and other software. Windows Graphics Capture is most reliable with Borderless Fullscreen (Fullscreen Windowed); true Exclusive Fullscreen can bypass desktop composition and is not guaranteed to capture consistently. The release smoke test samples normalized FFmpeg CPU use, working set, and process priority while producing and validating a real six-second clip; that diagnostic is evidence for the test machine, not a guarantee of zero input latency on every PC. Test the intended games, especially at 1440p/2160p or 60 FPS, before relying on a configuration.

Frame-accurate trimming is an explicit export and performs a second video encode. With replay stopped it uses the normal local export path; with steady replay running it deliberately uses real-time-paced, one-thread software H.264 at Idle priority and avoids the GPU encoder. This reduces competition with capture but makes long replay-time trims take at least approximately their selected duration. Trimming can still use noticeable CPU, disk bandwidth, and temporary disk space, especially for high-resolution sources, and it does not provide a zero-resource or instant export guarantee.

## Privacy and local data

Screen frames and selected audio are passed only between ClipForge, local named pipes, and a local FFmpeg process. ClipForge has no telemetry, cloud storage, authentication, or clip-upload path.

Network access is limited to user-visible setup and maintenance:

| Operation | When it happens | Data sent |
| --- | --- | --- |
| Install FFmpeg | The user selects **Install engine** | A normal HTTPS request for the pinned FFmpeg archive; no screen or audio content |
| Check for updates | After the user enables automatic checks, or when the user selects **Check for updates** | A normal HTTPS request to the release host for update metadata; no clips or capture content |
| Download an update | The user accepts an available update | A normal HTTPS request for the ClipForge release package |

Local data is kept in these locations:

| Data | Location |
| --- | --- |
| Saved normal and trimmed clips | The folder selected in the app; `%USERPROFILE%\Videos\ClipForge` by default |
| Settings | `%LOCALAPPDATA%\ClipForge\settings.json` |
| Optional Windows autostart entry | The signed-in user's Windows Startup folder; present only while **Start ClipForge and replay with Windows** is enabled |
| Private FFmpeg install | `%LOCALAPPDATA%\ClipForge\Tools\FFmpeg` |
| Rolling replay segments | `%LOCALAPPDATA%\ClipForge\Buffer\WindowsSession-<id>` in a per-capture folder removed when replay stops normally |

Choosing a cloud-synced save folder, such as a OneDrive folder, can cause the operating system or another application to upload saved clips; ClipForge itself does not do so. A forced shutdown can leave temporary files behind; ClipForge removes stale session folders on a later launch. Avoid selecting a replay length larger than the available disk space.

## Storage estimates

ClipForge estimates video at `width × height × FPS × 0.14` bits per second, clamped between 3 and 55 Mbps, plus 192 Kbps when audio is enabled. FFmpeg uses quality-based H.264 encoding, so actual use depends on motion, scene complexity, and the selected encoder.

Approximate storage for **1080p, 30 FPS, with audio**:

| Replay length | Estimated buffer |
| ---: | ---: |
| 30 seconds | 31.8 MB |
| 1 minute | 63.7 MB |
| 2 minutes | 127.3 MB |
| 5 minutes | 318.3 MB |
| 10 minutes | 636.7 MB |
| 20 minutes | 1.24 GB |
| 40 minutes | 2.49 GB |
| 1 hour | 3.73 GB |

Leave additional room for segment overhead and the MP4 being saved. Higher frame rates and resolutions can increase both disk use and CPU load substantially; 2160p at 60 FPS can approach the estimator's 55 Mbps ceiling.

## Uninstalling and removing local data

1. Choose **Exit ClipForge** from the notification-area menu so ClipForge and its FFmpeg process have stopped. You can first disable **Start ClipForge and replay with Windows** to remove its per-user Startup shortcut immediately; the installed app also requests this cleanup during uninstall.
2. Open **Windows Settings > Apps > Installed apps**, find **ClipForge**, open its menu, and select **Uninstall**. A portable development build has no registered uninstaller; delete the folder into which it was extracted instead.
3. Uninstalling the application does not intentionally delete personal normal or trimmed clips or the separate ClipForge data folder. To remove settings, the optional FFmpeg install, thumbnails, and any leftover replay buffer, delete `%LOCALAPPDATA%\ClipForge` after ClipForge has exited.
4. To remove saved recordings, trimmed exports, or a partial left by an abnormal machine shutdown, inspect and then delete the clips folder selected in ClipForge. Its default is `%USERPROFILE%\Videos\ClipForge`. Check that folder before deleting it because it contains the user's recordings, not disposable application files.

Removing `%LOCALAPPDATA%\ClipForge` resets ClipForge, including the selected background color, if it is installed again. A cloud-sync provider may retain its own copies or deleted-file history when the selected clips folder is synchronized; consult that provider for complete removal.

## Package a portable development build

Create a self-contained, single-file `win-x64` publish:

```powershell
.\scripts\package.ps1
```

The default output is `artifacts\ClipForge-win-x64`. Override it with, for example:

```powershell
.\scripts\package.ps1 -OutputPath C:\Builds\ClipForge
```

The package includes the .NET runtime but not FFmpeg. It is also not code-signed, so Windows SmartScreen may warn when testing a locally distributed build. A portable publish is not registered with Velopack and therefore cannot install in-app updates.

## Create an installer and updates

Restore the pinned Velopack 1.2.0 tool and create a versioned Windows installer:

```powershell
dotnet tool restore
.\scripts\release.ps1 `
  -Version 1.2.0-beta.1 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY
```

The release output contains `ClipForge-Setup.exe`, a portable ZIP, the full update package, Velopack feed metadata, and `SHA256SUMS.txt`. Use a permanent update URL: it is embedded in the installed application. Every published build must use a new semantic version; do not replace release files for a version users may already have installed.

The manual GitHub Actions release workflow downloads the previous feed, builds and tests ClipForge, optionally signs the installer, and uploads a draft or public GitHub Release. Separate CI and CodeQL workflows verify pushes and pull requests, while Dependabot monitors NuGet, .NET SDK, and GitHub Actions dependencies. A stable release also attaches a friendly download name:

```text
https://github.com/OWNER/REPOSITORY/releases/latest/download/ClipForge-Setup.exe
```

Unsigned installers must not be described as official trusted releases. The workflow refuses immediate public publication when signing is unavailable. While the SignPath Foundation application is pending, a deliberately unsigned beta may be published manually as a GitHub **pre-release** only when its title and notes prominently warn that Windows will show an unverified publisher; it is never promoted as the latest stable download. A trusted public build requires a valid Authenticode signing route such as an accepted SignPath Foundation integration.

ClipForge disables Velopack's implicit apply-on-startup behavior. A downloaded update is applied only after the user selects **Restart to update**, allowing ClipForge to stop capture and shut down cleanly first. This improves control over when an update runs, but it does not solve publisher authentication: the current unsigned beta uses HTTPS, GitHub access controls, and feed/package checksums and does not pin a project signing key or Authenticode publisher in the client. Treat beta updates as previews and verify them on the GitHub release page until a signing route and client-side trust policy have been completed.

See [docs/RELEASING.md](docs/RELEASING.md) for signing secrets, the GitHub workflow, release verification, and recovery guidance.

## Code signing policy

[Read the ClipForge Code signing policy](CODE_SIGNING_POLICY.md).

ClipForge is preparing an application to the SignPath Foundation open-source program, but it has not been accepted. Current preview packages are unsigned. If accepted, releases will use the attribution "Free code signing provided by SignPath.io, certificate by SignPath Foundation," and every signing request will require manual approval. Do not interpret this statement as confirmation that SignPath has accepted or signed ClipForge.

## Current limitations

- Capture uses direct or compatibility-transfer Windows Graphics Capture when runtime verification succeeds, otherwise GDI desktop capture. None is a game-specific hook; windowed and borderless games are the intended target, and exclusive-fullscreen games may not be captured reliably.
- HDR/10-bit capture and tone mapping are not implemented. Output is SDR H.264 (`yuv420p`) with AAC audio.
- Hardware H.264 is used only after a successful runtime probe. Unsupported encoders or drivers fall back automatically; software encoding can be expensive at high resolution or frame rate.
- One display is captured at a time. There is no window/region picker, multi-track editor, transitions/effects editor, or automatic upload; the Library editor is intentionally limited to frame-accurate start/end trimming.
- Trimming creates a separately encoded MP4 and can take noticeable time and resources for long or high-resolution selections. Replay-time trim uses a deliberately slower real-time-paced, one-thread software path to protect the active capture; it cannot guarantee zero contention.
- Desktop and microphone audio are mixed into one stereo track. Per-application audio and separate editable tracks are not available.
- Protected/DRM video and some overlays may appear blank.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for implementation details and extension points, and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency notices.

See [PRIVACY.md](PRIVACY.md) for the concise network and local-media policy, [SECURITY.md](SECURITY.md) for security boundaries and reporting, [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) for release-signing governance, and [CHANGELOG.md](CHANGELOG.md) for release history.
