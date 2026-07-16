# ClipForge security

## Supported releases

Security fixes are provided for the latest supported ClipForge release in its published channel. Stable users should install the latest stable release; beta users should install the newest explicitly labeled pre-release. The public beta is unsigned and is not equivalent to a trusted stable release.

## Reporting a vulnerability

Do not publish an exploitable vulnerability, private recording, credential, certificate, or signing secret in a public issue. Use ClipForge's enabled [private vulnerability reporting form](https://github.com/Purxy8/Clipping-Tool/security/advisories/new). Include the affected ClipForge version, Windows version, reproduction steps, and the security impact. Remove personal media and secrets from logs or screenshots.

## Security boundary

- ClipForge runs as the signed-in user and never requests administrator privileges.
- Screen and audio media, trim selections, trim partials, and completed normal/trimmed clips remain in local memory, named pipes, the replay buffer, local FFmpeg processes, and the user-selected clips folder. There is no clip upload feature or telemetry.
- The rotating local capture-lifecycle journal contains only timestamps, geometry/backend and cursor state, and numerical process counters. It does not contain captured pixels or audio, paths, file/device names, or user input, and it is never uploaded by ClipForge.
- Audio named pipes are limited to the current Windows user. Replay roots are separated by Windows logon-session ID so simultaneous RDP/local sessions do not clean one another's buffers. Cleanup rejects a buffer root or existing ancestor that is a reparse point and deletes only regular direct `session-*` children; abandoned sessions in the current logon scope are purged after single-instance ownership is established on the next launch.
- External processes are launched with explicit executable paths and individual argument-list entries, without a command shell. Media helper processes run at below-normal priority, have bounded runtime/diagnostic output, honor cancellation, and are terminated on timeout.
- The clip gallery auto-loads only top-level files using ClipForge's generated normal `Clip_YYYY-MM-DD_HH-mm-ss[_N].mp4` or trimmed `Clip_YYYY-MM-DD_HH-mm-ss[_N]_trimmed[_N].mp4` forms. Its FFprobe, thumbnail, and trim processes force the MOV/MP4 demuxer and allow only the local `file` protocol.
- Frame-accurate trim export revalidates and pins the selected source and save root while local FFmpeg reads the file. It writes a unique partial outside Library discovery, validates the resulting MP4, and atomically commits a separate non-overwriting trimmed file; cancellation or failure removes the owned partial and leaves the source unchanged.
- The original clip is kept by default after trimming. If the user explicitly chooses deletion, ClipForge revalidates its stable Windows file identity and guarded single-link deletion policy after the trimmed file has committed. A changed, replaced, unsafe, or multi-link original is preserved.
- The optional **Start ClipForge and replay with Windows** feature is off by default and uses only the installed package's per-user Startup shortcut. Registration is restricted to the relative `ClipForge.exe` target and fixed `--autostart` argument; portable/development builds cannot enable it, and no service, Run-key entry, scheduled task, or administrator privilege is introduced.
- The bundled update integration accepts HTTPS feeds. Applying a staged update requires an explicit **Restart to update** action; automatic application at process startup is disabled so capture can shut down cleanly first.
- Trusted/stable releases are required to pass Authenticode verification in the release workflow; a pending-SignPath unsigned preview may be public only when it is conspicuously labeled as an unsigned pre-release.
- The optional FFmpeg installer requires a declared size, enforces download/extraction limits, downloads a pinned archive over HTTPS, verifies its SHA-256 digest, and verifies the pinned SHA-256 of both extracted executables. Normal production discovery ignores arbitrary `PATH` tools and rejects reparse-point tool paths.
- Process startup enables the restricted Windows DLL search policy to reduce DLL preloading risk.
- GitHub CI, CodeQL, Dependabot, and NuGet vulnerability auditing provide continuous checks; they supplement rather than replace code review and runtime testing.

## Known update-authenticity limitation

The current beta is unsigned. HTTPS protects transport and Velopack feed/package checksums detect accidental or inconsistent package corruption, but the client does not yet pin a ClipForge project signing key or an expected Authenticode publisher. A compromised release account or feed could therefore publish a higher-version package with matching feed metadata. Disabling startup auto-apply limits surprise installation timing; it does not authenticate the publisher.

Until a public signing route is accepted, successfully exercised, and enforced by a client-side trust policy, treat in-app beta updates as previews. Verify the release tag and published SHA-256 value on the official GitHub release page before installing. Do not describe an unsigned beta as trusted or officially signed.

No desktop application can guarantee protection from an already-compromised Windows account, administrator, kernel driver, screen-injection tool, or malicious software with equal or higher privileges. Code signing proves publisher identity and detects modification; it does not make unsafe code invulnerable.
