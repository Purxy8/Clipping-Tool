# Code signing policy

## Status

ClipForge is preparing an application to the SignPath Foundation open-source program. The project has **not** yet been accepted by SignPath Foundation, and no certificate has been issued or assigned to ClipForge. Current preview and development packages are unsigned and may trigger Windows SmartScreen or antivirus reputation warnings.

If the application is accepted, the intended service attribution is:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation

A signature issued under that program would identify **SignPath Foundation** as the Windows publisher. It would verify the origin and integrity of an approved build; it would not imply that SignPath Foundation created or endorses ClipForge.

## Project and source

- Project: **ClipForge**
- Source repository: <https://github.com/Purxy8/Clipping-Tool>
- License: [GNU General Public License v3.0 or later](LICENSE)
- Copyright holder for project-owned source and assets: **Purxy8**

Only artifacts built from this repository's project-owned source code and build scripts may be submitted for signing. Project-owned source code, build scripts, documentation, UI artwork, icons, and other project assets are licensed under GPL-3.0-or-later unless a file clearly states otherwise. Project artwork is recorded in [ASSET_PROVENANCE.md](ASSET_PROVENANCE.md). Third-party components remain under their respective open-source licenses and are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). ClipForge does not submit upstream third-party binaries for signing as if they were project-owned.

## Team roles

ClipForge currently has one maintainer, so the same named maintainer holds the required roles. Role membership must be updated here before access changes.

| Role | Member | Responsibility |
| --- | --- | --- |
| Authors | [Purxy8](https://github.com/Purxy8) | May modify project-owned source code and build configuration in the repository. |
| Reviewers | [Purxy8](https://github.com/Purxy8) | Reviews contributions from people who are not trusted authors before merge, with particular attention to build and release changes. |
| Approvers | [Purxy8](https://github.com/Purxy8) | Manually reviews and approves or rejects every individual signing request. |

Contributions from people outside the Authors role require review by a listed Reviewer before they are merged. Access is granted only to the minimum role needed and is removed when no longer required.

## Account security and approval

- Multi-factor authentication is mandatory for every team member on both GitHub and SignPath. Enrollment is not considered ready until MFA is enabled and verified on both services.
- GitHub and SignPath credentials, recovery codes, tokens, and signing material must never be committed to the repository or included in build artifacts.
- Every signing request requires a separate, manual decision by an Approver. Signing approval must never be automatic.
- Before approval, the Approver verifies the source commit or tag, successful CI and security checks, version and product metadata, generated checksums, and that the requested files came from the documented build pipeline.
- The Approver rejects a request if its source, build provenance, contents, version, or checks are unclear.

## Privacy policy

ClipForge's complete privacy policy is published in [PRIVACY.md](PRIVACY.md). Screen frames, selected audio, replay segments, thumbnails, and clips remain on the user's computer. ClipForge has no telemetry, analytics, advertising, account system, cloud storage, or clip-upload feature.

ClipForge contacts another networked system only for a user-visible maintenance feature:

- an update check when automatic checks are enabled or the user selects **Check for updates**;
- an update download after the user accepts it; or
- an FFmpeg download after the user selects **Install engine**.

These requests do not contain captured screen, audio, or clip media. Normal HTTPS connection metadata is visible to the service operator. Updates are hosted by GitHub and are subject to the [GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). Optional FFmpeg downloads come from [Gyan.dev](https://www.gyan.dev/ffmpeg/builds/); its published site notice states that the site author does not solicit or store personal information. Users may instead provide FFmpeg locally and disable automatic update checks.

## Release and signing controls

- Builds proposed for signing are produced by the repository's automated release workflow from a specific public source revision.
- Product-name metadata must be **ClipForge**, and all product-version attributes within a release must use the same release version.
- A release version is immutable. Published artifacts are never replaced with different files under the same version.
- Signed artifacts are published with SHA-256 checksums and a link to the corresponding public source revision.
- Every public release page must contain a **Code signing policy** heading or a link bearing that exact text to this document.
- Preview builds remain explicitly labeled **unsigned** until SignPath Foundation accepts the project and the SignPath build/signing integration is operational.

The project will follow all technical restrictions imposed by SignPath.io and SignPath Foundation and will not attempt to bypass provenance verification, manual approval, file restrictions, or other program controls.
