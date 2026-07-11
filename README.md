# ClipForge

ClipForge is a Windows instant-replay recorder: leave a private rolling buffer running, then save the moments that already happened as an MP4. The MVP is designed around a single, friendly window and local-only capture.

## MVP features

- Replay lengths: 30 seconds; 1, 2, 3, 5, 10, 20, 30, or 40 minutes; and 1 hour.
- One selected display, captured at source resolution, 720p, 1080p, 1440p, or 2160p.
- Selectable frame rate.
- Optional desktop audio from a selected Windows output device.
- Optional microphone audio from any active Windows capture device.
- A configurable clips folder, defaulting to `%USERPROFILE%\Videos\ClipForge`.
- Save from the app or anywhere in Windows with **Ctrl+Shift+F10**.
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

NuGet restore may use the network the first time the project is built.

## First-run FFmpeg setup

ClipForge uses FFmpeg as its local capture and MP4 engine. If it cannot find `ffmpeg.exe`, the app shows an **Install engine** panel. Selecting **Install engine** downloads the FFmpeg essentials archive over HTTPS from Gyan.dev, extracts `ffmpeg.exe` and `ffprobe.exe`, and places them in:

```text
%LOCALAPPDATA%\ClipForge\Tools\FFmpeg
```

The FFmpeg download is one of two optional network features. An installed release can also check its configured release host for update metadata and download an update after the user requests it. ClipForge does not download or redistribute FFmpeg while building the application.

For a managed or offline installation, set `CLIPFORGE_FFMPEG_PATH` to either an `ffmpeg.exe` file or a directory containing it before launching ClipForge. The app also checks its own directory, `Tools\FFmpeg` beside the app, and the Windows `PATH`.

## Using ClipForge

1. Install or provide FFmpeg when prompted.
2. Choose a display, replay length, frame rate, and output resolution.
3. Enable desktop audio and/or microphone capture, then select the desired devices.
4. Choose the save folder.
5. Select **Start replay**. ClipForge begins building the rolling buffer on local disk.
6. Select **Save last clip** or press **Ctrl+Shift+F10** after the buffer has content.

The saved MP4 goes to the selected folder. Saving does not stop the rolling buffer, so another clip can be saved later. Stopping replay clears the temporary buffer. Changing the display, resolution, frame rate, or audio configuration while replay is active automatically restarts capture and clears the old buffer; changing only replay length adjusts retention in place.

If another application already owns Ctrl+Shift+F10, ClipForge continues to work from its window but reports that the global shortcut could not be registered.

## Privacy and local data

Screen frames and selected audio are passed only between ClipForge, local named pipes, and a local FFmpeg process. ClipForge has no telemetry, cloud storage, authentication, or clip-upload path.

Network access is limited to user-visible setup and maintenance:

| Operation | When it happens | Data sent |
| --- | --- | --- |
| Install FFmpeg | The user selects **Install engine** | A normal HTTPS request for the pinned FFmpeg archive; no screen or audio content |
| Check for updates | Automatically when enabled, or when the user selects **Check for updates** | A normal HTTPS request to the release host for update metadata; no clips or capture content |
| Download an update | The user accepts an available update | A normal HTTPS request for the ClipForge release package |

Local data is kept in these locations:

| Data | Location |
| --- | --- |
| Saved clips | The folder selected in the app; `%USERPROFILE%\Videos\ClipForge` by default |
| Settings | `%LOCALAPPDATA%\ClipForge\settings.json` |
| Private FFmpeg install | `%LOCALAPPDATA%\ClipForge\Tools\FFmpeg` |
| Rolling replay segments | `%LOCALAPPDATA%\ClipForge\Buffer` in a per-session folder removed when replay stops normally |

Choosing a cloud-synced save folder, such as a OneDrive folder, can cause the operating system or another application to upload saved clips; ClipForge itself does not do so. A forced shutdown can leave temporary files behind; ClipForge removes stale session folders on a later launch. Avoid selecting a replay length larger than the available disk space.

## Storage estimates

ClipForge estimates video at `width × height × FPS × 0.14` bits per second, clamped between 3 and 55 Mbps, plus 192 Kbps when audio is enabled. FFmpeg uses quality-based H.264 encoding, so actual use depends on motion and scene complexity.

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
  -Version 1.0.0 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY
```

The release output contains `ClipForge-Setup.exe`, the full update package, Velopack feed metadata, and `SHA256SUMS.txt`. Use a permanent update URL: it is embedded in the installed application. Every published build must use a new semantic version; do not replace release files for a version users may already have installed.

The manual GitHub Actions release workflow downloads the previous feed, builds and tests ClipForge, optionally signs the installer, and uploads a draft or public GitHub Release. It also attaches a stable friendly download name:

```text
https://github.com/OWNER/REPOSITORY/releases/latest/download/ClipForge-Setup.exe
```

Unsigned installers are appropriate for internal testing, but should not be described as an official trusted release. The workflow refuses to publish an unsigned run. A public build needs a user-provided Authenticode certificate or Azure Artifact Signing configuration; repository credentials and signing identities are intentionally not included in source control.

See [docs/RELEASING.md](docs/RELEASING.md) for signing secrets, the GitHub workflow, release verification, and recovery guidance.

## MVP limitations

- Capture uses Windows desktop capture, not a game-specific hook. Windowed and borderless games are the intended target; exclusive-fullscreen games may not be captured reliably.
- HDR/10-bit capture and tone mapping are not implemented. Output is SDR H.264 (`yuv420p`) with AAC audio.
- Encoding uses CPU-based `libx264`; NVIDIA NVENC, AMD AMF, and Intel Quick Sync are future improvements.
- One display is captured at a time. There is no window/region picker, overlay, tray mode, clip editor, clip library, or automatic upload yet.
- Desktop and microphone audio are mixed into one stereo track. Per-application audio and separate editable tracks are not available.
- Protected/DRM video and some overlays may appear blank.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for implementation details and extension points, and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency notices.
