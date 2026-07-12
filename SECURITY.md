# ClipForge security

## Supported releases

Security fixes are provided for the latest stable ClipForge release. Users should install updates from the in-app update panel or the official GitHub release page.

## Reporting a vulnerability

Do not publish an exploitable vulnerability, private recording, credential, certificate, or signing secret in a public issue. Use GitHub's private vulnerability reporting for the repository when it is enabled. Include the affected ClipForge version, Windows version, reproduction steps, and the security impact. Remove personal media and secrets from logs or screenshots.

## Security boundary

- ClipForge runs as the signed-in user and never requests administrator privileges.
- Screen and audio media remain in local memory, named pipes, the replay buffer, and the user-selected clips folder. There is no clip upload feature or telemetry.
- Audio named pipes are limited to the current Windows user, and replay cleanup refuses reparse points and paths outside ClipForge's buffer root.
- External processes are launched with explicit executable paths and argument lists, without a command shell.
- The bundled update integration accepts HTTPS feeds; public releases are required to pass Authenticode verification in the release workflow.
- The optional FFmpeg installer downloads a pinned archive and verifies its SHA-256 digest before extraction.
- Process startup enables the restricted Windows DLL search policy to reduce DLL preloading risk.
- GitHub CI, CodeQL, Dependabot, and NuGet vulnerability auditing provide continuous checks; they supplement rather than replace code review and runtime testing.

No desktop application can guarantee protection from an already-compromised Windows account, administrator, kernel driver, screen-injection tool, or malicious software with equal or higher privileges. Code signing proves publisher identity and detects modification; it does not make unsafe code invulnerable.
