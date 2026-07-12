# ClipForge privacy

ClipForge is a local-first desktop application. Screen frames, selected audio, rolling replay segments, thumbnails, and saved clips stay on the Windows PC in local memory, local named pipes, ClipForge's temporary buffer, thumbnail cache, and the save folder chosen by the user. ClipForge has no account system, telemetry, analytics, advertising, social feed, cloud storage, or clip-upload feature.

ClipForge makes network requests only for these visible maintenance operations:

- **Update checks:** when automatic update checks are enabled or the user selects **Check for updates**, an installed release contacts its configured GitHub release feed over HTTPS. If the user accepts an update, ClipForge downloads the release package from that feed. No capture media is included in these requests.
- **FFmpeg installation:** when the user selects **Install engine**, ClipForge downloads the pinned FFmpeg archive from Gyan.dev over HTTPS and verifies its SHA-256 digest before extraction. No capture media is included in this request.

A development build without a configured update feed does not make update requests. Choosing a folder managed by OneDrive, Dropbox, or another synchronization product can cause that separate product to upload saved clips; ClipForge does not control or initiate that synchronization.

Local data locations and deletion guidance are documented in [README.md](README.md#privacy-and-local-data). Security boundaries and vulnerability reporting are documented in [SECURITY.md](SECURITY.md).
