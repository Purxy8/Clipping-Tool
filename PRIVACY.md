# ClipForge privacy

ClipForge is a local-first desktop application. Screen frames, selected audio, rolling replay segments, thumbnails, and saved clips stay on the Windows PC in local memory, local named pipes, ClipForge's temporary buffer, thumbnail cache, and the save folder chosen by the user. ClipForge has no account system, telemetry, analytics, advertising, social feed, cloud storage, or clip-upload feature.

ClipForge makes network requests only for these visible maintenance operations:

- **Update checks:** when automatic update checks are enabled or the user selects **Check for updates**, an installed release contacts its configured GitHub release feed over HTTPS. If the user accepts an update, ClipForge downloads the release package from that feed. No capture media is included in these requests.
- **FFmpeg installation:** when the user selects **Install engine**, ClipForge downloads the pinned FFmpeg archive from Gyan.dev over HTTPS and verifies its SHA-256 digest before extraction. No capture media is included in this request.

Normal HTTPS connection metadata, such as the user's IP address, request headers, and request time, can be visible to the service that receives a maintenance request:

- GitHub hosts the configured ClipForge update feed and publishes the [GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
- [Gyan.dev](https://www.gyan.dev/ffmpeg/builds/) hosts the optional FFmpeg archive. Its published site notice states that the site author does not solicit or store personal information. The application requests the archive directly and does not load the site's webpages, advertising, or web fonts.

Automatic update checks default to off for a new installation and can be enabled explicitly in ClipForge. Users can turn them off again at any time and can provide a local FFmpeg executable instead of using the downloader. Update and FFmpeg requests do not contain screen frames, audio, thumbnails, clips, filenames, save-folder paths, or ClipForge settings.

A development build without a configured update feed does not make update requests. Choosing a folder managed by OneDrive, Dropbox, or another synchronization product can cause that separate product to upload saved clips; ClipForge does not control or initiate that synchronization.

Local data locations are documented in [README.md](README.md#privacy-and-local-data), and complete removal steps are documented in [README.md](README.md#uninstalling-and-removing-local-data). Security boundaries and vulnerability reporting are documented in [SECURITY.md](SECURITY.md).
