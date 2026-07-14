# Releasing ClipForge

ClipForge uses [Velopack](https://docs.velopack.io/) for its Windows installer and in-app updates. The application package and the local `vpk` tool are both pinned to version 1.2.0.

## Permanent release identity

These values are part of the installed application's identity and should not be changed after the first public release:

| Setting | Value |
| --- | --- |
| Package ID | `ClipForge.Desktop` |
| Executable | `ClipForge.exe` |
| Runtime | `win-x64` |
| Update channel | `stable` |
| Default installer name | `ClipForge-Setup.exe` |

The package ID intentionally differs from the `%LOCALAPPDATA%\ClipForge` data folder. Velopack owns an installation directory based on the package ID, while ClipForge continues to own its settings, FFmpeg tools, and replay buffer under the existing data folder.

Every release must have a new [SemVer](https://semver.org/) value such as `1.0.0` or `1.0.1`. Never rebuild or replace a version that users may already have installed. Velopack and update caches use the version as an immutable identity.

## Local release build

Prerequisites:

- 64-bit Windows 10/11.
- The .NET 10 SDK. The release workflow currently installs SDK 10.0.301 explicitly.
- PowerShell.
- Network access for the first NuGet and local-tool restore.

Restore the pinned tool, then build an installer from the repository root:

```powershell
dotnet tool restore
.\scripts\release.ps1 `
  -Version 1.2.0-beta.1 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY
```

The update URL is compiled into the application. Use the final, permanent repository URL for a distributable build. A build without `-UpdateUrl` can be used for local testing, but it cannot discover later releases.

By default, local release assets are written to `artifacts\Releases`; the GitHub workflow explicitly uses the isolated `artifacts\Distribution` directory. They include the user-facing installer, portable ZIP, Velopack package and update-feed files, and `SHA256SUMS.txt`. The self-contained application includes the .NET runtime but does not bundle FFmpeg.

The script runs the Release build and tests before packaging. It refuses to overwrite an existing full package with the same version.

## Authentic code signing

An unsigned installer is suitable for internal testing, but it is not an official trusted Windows release. Windows SmartScreen and antivirus products commonly warn about unsigned applications. Velopack must perform signing while it creates the package so that the application, updater, and installer are signed at the correct stages. See [Velopack's Windows signing guide](https://docs.velopack.io/packaging/signing).

ClipForge has selected the open-source SignPath Foundation route and is preparing an application. **The project has not been accepted, and current preview packages are unsigned.** Do not claim that a preview is signed, trusted, or approved by SignPath. The mandatory team roles, MFA rules, privacy disclosure, and manual-approval requirements are recorded in the repository's [Code signing policy](../CODE_SIGNING_POLICY.md).

If ClipForge is accepted, the SignPath integration must build from the public repository revision, preserve verifiable build provenance, enforce ClipForge product/version metadata, and require a listed Approver to approve every signing request manually. The workflow must not treat SignPath acceptance or signing as automatic. Until that integration has been reviewed and exercised successfully, the existing workflow must continue to refuse immediate unsigned publication. A Foundation-eligibility preview may be published manually only as a conspicuously labeled **unsigned pre-release**, never as a trusted or latest stable release.

Two signing routes are supported by `scripts/release.ps1`:

For a Bulgarian publisher, choose the identity route before purchasing anything:

- A registered company or other legal organization in the EU can currently apply for Microsoft Azure Artifact Signing Public Trust, Microsoft's recommended direct-download option.
- An individual publisher in Bulgaria is not currently eligible for Azure Artifact Signing Public Trust. Use a publicly trusted OV code-signing provider that explicitly supports individuals and unattended/cloud CI, or consider SignPath Foundation only if the project deliberately adopts an OSI-approved open-source license and meets its program conditions.
- A self-signed certificate is useful only for private testing or centrally managed company PCs; it is not suitable for a public GitHub download.

See Microsoft's current [Windows code-signing options](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options) and [Artifact Signing eligibility FAQ](https://learn.microsoft.com/azure/artifact-signing/faq) before ordering. Availability, validation rules, and pricing can change.

### Authenticode certificate

First make the certificate available to `signtool.exe`, then pass the signing arguments without the `sign` verb:

```powershell
$signing = '/sha1 CERTIFICATE_THUMBPRINT /fd SHA256 /td SHA256 /tr https://timestamp.digicert.com'
.\scripts\release.ps1 `
  -Version 1.2.0-beta.1 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY `
  -SignParams $signing
```

Use a certificate issued to the publisher identity that should appear in Windows. Since the 2023 CA/Browser Forum key-protection change, newly issued publicly trusted code-signing private keys normally live in approved hardware or a compliant cloud HSM. An ordinary exportable PFX placed in GitHub Secrets is therefore not the default modern public-trust route. Use the PFX workflow only when the issuer explicitly provides a compliant, unattended-CI PFX; otherwise use Azure Artifact Signing or add the chosen CA's cloud-signing integration.

### Azure Artifact Signing

Authenticate to Azure, then create a UTF-8 JSON file without a byte-order mark:

```json
{
  "Endpoint": "https://REGION.codesigning.azure.net/",
  "CodeSigningAccountName": "ACCOUNT_NAME",
  "CertificateProfileName": "PROFILE_NAME"
}
```

Pass it to the release script:

```powershell
.\scripts\release.ps1 `
  -Version 1.2.0-beta.1 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY `
  -AzureTrustedSignFile C:\secure\clipforge-signing.json
```

Artifact Signing also requires a valid Microsoft identity validation, certificate profile, Azure credentials, and the appropriate signing role. The metadata file is not itself a credential, but the account details and Azure access should still be managed as deployment configuration.

## GitHub Actions release

`.github/workflows/release.yml` is a manual `workflow_dispatch` pipeline. It:

1. Restores .NET dependencies and the repository-local Velopack 1.2.0 tool.
2. Refuses a duplicate or non-increasing version and downloads the previous Velopack GitHub release when a feed already exists, allowing Velopack to produce delta updates.
3. Builds, tests, and publishes the self-contained payload before any signing identity is activated.
4. Activates exactly one signing route only for Velopack packaging, then immediately removes temporary signing material.
5. Verifies the expected publisher, trusted timestamp, and Authenticode chain on both installers plus the application and updater inside the portable/update packages.
6. Uploads the update assets through `vpk upload github` using GitHub's short-lived `GITHUB_TOKEN`, then attaches the friendly `ClipForge-Setup.exe` alias, portable ZIP, and checksum file to the same release.
7. Retains the primary release files as a workflow artifact for 14 days.

To run it, open **Actions > Release ClipForge > Run workflow**, enter a new version and optional release notes, and decide whether to publish immediately. The `publish` option defaults to off, which creates a draft GitHub release. Review the assets and signature before publishing the draft. The workflow refuses immediate publishing when no signing configuration is present.

Before adding any signing configuration, create a GitHub Environment named `release` under **Settings > Environments**. Restrict its deployment branch to `main` and add a required reviewer when the repository plan supports it. Store signing values as environment secrets, not in the repository or source tree. The workflow itself is main-only and waits for this environment before it can access signing credentials.

No signing secret is required for an unsigned test run. Configure exactly one of the following sets for a trusted release, plus `WINDOWS_SIGNING_SUBJECT`. Set that common secret to a stable part of the exact certificate subject, normally `CN=LEGAL PUBLISHER NAME`; every signed release component must match it.

### PFX secrets

Use this mode only when the issuer provides an exportable, CI-suitable PFX. Many modern public code-signing products keep the private key in a hardware token or cloud HSM and require their own signing integration instead.

| GitHub Actions secret | Content |
| --- | --- |
| `WINDOWS_SIGNING_PFX_BASE64` | Base64 encoding of the PFX file |
| `WINDOWS_SIGNING_PFX_PASSWORD` | PFX password |
| `WINDOWS_SIGNING_SUBJECT` | Expected certificate subject text, for example `CN=LEGAL PUBLISHER NAME` |

The workflow rejects ambiguous PFX bundles unless exactly one imported certificate has both a private key and the Code Signing EKU. It passes only that certificate's thumbprint to Velopack and removes every imported certificate plus the temporary PFX immediately after packaging.

### Azure Artifact Signing secrets

| GitHub Actions secret | Content |
| --- | --- |
| `AZURE_SIGNING_METADATA_JSON` | The three-field signing metadata JSON shown above |
| `AZURE_CLIENT_ID` | Entra application or managed identity client ID |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `WINDOWS_SIGNING_SUBJECT` | Expected public certificate subject text, for example `CN=LEGAL PUBLISHER NAME` |

The workflow uses GitHub OIDC through `azure/login`, so configure a federated credential for this repository and grant the identity the Artifact Signing certificate-profile signer role. No long-lived Azure client secret is stored in GitHub.

Repository Actions must be allowed to create releases. The workflow declares `contents: write`; organization or repository policy can still restrict that permission.

## Release checks

Before making a GitHub release public:

```powershell
$installer = '.\artifacts\Releases\ClipForge-Setup.exe'
Get-AuthenticodeSignature $installer | Format-List Status, StatusMessage, SignerCertificate
Get-FileHash $installer -Algorithm SHA256
Get-Content .\artifacts\Releases\SHA256SUMS.txt
```

On the Windows capture test PC, validate the fixed-resolution and Source paths with the same engine and audio configuration intended for release:

```powershell
$smokeProject = '.\tests\ClipForge.CaptureSmoke\ClipForge.CaptureSmoke.csproj'
$smokeRoot = '.\artifacts\capture-release-smoke'
dotnet build $smokeProject -c Release --no-restore
dotnet run --project $smokeProject -c Release --no-build -- --concat-smoke --artifacts $smokeRoot
dotnet run --project $smokeProject -c Release --no-build -- --resolution 720p --fps 60 --audio --microphone --artifacts $smokeRoot
dotnet run --project $smokeProject -c Release --no-build -- --resolution 1080p --fps 60 --audio --microphone --artifacts $smokeRoot
dotnet run --project $smokeProject -c Release --no-build -- --resolution source --fps 60 --audio --microphone --artifacts $smokeRoot
dotnet run --project $smokeProject -c Release --no-build -- --resolution 720p --fps 60 --audio --microphone --force-gdi --artifacts $smokeRoot
```

The smoke harness fails on an unexpected duration, resolution, average frame rate, frame-count floor, audio-stream count, capture priority, excessive normalized CPU, or excessive working set. It also inspects packet timestamps: video frame spacing must stay within the configured FPS budget, audio DTS must remain monotonic, and audio/video stream durations must stay aligned across two-second replay-buffer joins. The desktop-independent `--concat-smoke` case builds three real 1920x1080 60 FPS AAC-backed segments and must complete with no join gap larger than 25 ms. Record the selected capture strategy and measured results. `--force-gdi` is an internal smoke-only override that keeps the runtime-verified encoder but exercises the GDI fallback command end to end; it is not available in the production application. A successful fallback run on one PC still does not replace validation on the affected or equivalent fallback/hybrid hardware before claiming that hardware-specific lag is resolved.

### Library trim smoke checklist

Complete this checklist with a real ClipForge-generated MP4 before publishing a build that changes trimming, Library discovery, FFmpeg media helpers, or guarded deletion:

```powershell
$smokeProject = '.\tests\ClipForge.CaptureSmoke\ClipForge.CaptureSmoke.csproj'
$trimSmokeRoot = '.\artifacts\trim-release-smoke'
dotnet build $smokeProject -c Release --no-restore
dotnet run --project $smokeProject -c Release --no-build -- --trim-smoke --artifacts $trimSmokeRoot
dotnet run --project $smokeProject -c Release --no-build -- --trim-smoke --audio --artifacts $trimSmokeRoot
# Optional regression against a copied real ClipForge recording:
dotnet run --project $smokeProject -c Release --no-build -- --trim-smoke --audio --trim-source C:\path\to\Clip_yyyy-MM-dd_HH-mm-ss.mp4 --artifacts $trimSmokeRoot
```

The two automated trim runs create a synthetic ClipForge source with non-keyframe-aligned boundaries and validate duration, frame count, resolution, frame rate, audio-stream count, strict trimmed naming, original-file retention, partial cleanup, and helper-process termination. They complement rather than replace the following interactive player, filter, prompt, cancellation, and target-hardware checks.

1. Save a normal clip containing motion and audio, open it in Library, and confirm the **All**, **Normal**, and **Trimmed** filters classify it correctly.
2. While replay is running, verify the trim export action is disabled. Stop replay before continuing; capture FFmpeg and trim FFmpeg must not intentionally run together.
3. Move both trim handles to a non-keyframe-aligned interval, including a short selection of approximately five seconds. Confirm the preview labels and selected range match the requested start/end frames.
4. Export the range and use FFprobe to verify the trimmed MP4 is playable, has the expected video dimensions and audio-stream count, starts/ends within the accepted frame-duration tolerance, and appears under **Trimmed** without hiding the normal clip.
5. At the post-export prompt, choose the default keep-both path and confirm both files remain. Repeat with another source, explicitly choose deletion, and confirm only the identity-revalidated original is removed after the trimmed output is safely present.
6. Repeat a trim that would produce the same friendly base name and confirm a unique suffix is used instead of overwriting either earlier file.
7. Start a longer trim and cancel it, then test an induced helper failure if practical. Confirm the original remains, no completed trimmed entry appears, no owned trim partial remains in the clips folder, and no FFmpeg/FFprobe child process is orphaned.
8. Confirm the trim helper runs at below-normal priority and that the app remains responsive. Record duration and resource observations for 720p, 1080p, and Source/high-resolution inputs; these measurements describe only the test hardware and are not proof of zero latency everywhere.
9. Start replay again after trimming and rerun the applicable capture smoke. Confirm save, playback, gallery refresh, and trim-filter state do not leave a decoder or export helper running in the background.

Do not publish a frame-accuracy claim from UI observation alone. Record the input, selected timestamps, output duration/stream details, and FFprobe result in the release verification notes. Do not claim zero lag: trimming performs a separate re-encode and can use noticeable CPU, GPU, disk bandwidth, and temporary space even though capture must be stopped and helper priority is reduced.

For a signed release, `Get-AuthenticodeSignature` must report `Valid`, and the publisher should match the intended ClipForge publisher identity. Also verify on a clean Windows user account or VM:

1. The installer starts without an unexpected publisher warning.
2. ClipForge launches from the Start menu and Desktop shortcut.
3. FFmpeg setup, replay capture, clip saving, Library filtering, local playback, and frame-accurate trimming work.
4. Trim export remains disabled while replay is active, keeps both files by default, and deletes only an explicitly confirmed, identity-revalidated original.
5. The displayed application version matches the release.
6. After publishing a newer version, **Check for updates**, download, and **Restart to update** complete successfully. Verify that a staged package is not applied merely by launching the app without that explicit action.

Raw `dotnet run`, IDE, and portable publish builds are intentionally excluded from updater installation tests. Velopack updates only apply to a Velopack-installed copy.

Every download or release page must include a heading or link using the exact term **Code signing policy** and point to [`CODE_SIGNING_POLICY.md`](../CODE_SIGNING_POLICY.md). While the application remains pending, release notes must also label the package **unsigned preview**. After acceptance and successful signing, include this exact attribution in release notes:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation

## Publishing and recovery

The public download lives on the repository's GitHub Releases page. Because the workflow attaches the friendly installer alias, the latest stable download can use:

```text
https://github.com/OWNER/REPOSITORY/releases/latest/download/ClipForge-Setup.exe
```

Keep the full package, update-feed files, and installer attached to every release; installed clients need the feed and packages to update.

The existing local 1.0.0 build was created without an embedded GitHub update URL, so it cannot discover hosted releases. The public 1.1.0-beta.1 client was configured to ignore later pre-releases, so moving from that beta to 1.2.0-beta.1 also requires one manual installer run after exiting every old ClipForge tray instance. From 1.2.0-beta.1 onward, beta versions include later pre-releases in update checks; stable versions continue to exclude them.

Startup auto-apply is disabled in 1.2. A downloaded package is scheduled only when the user selects **Restart to update**, allowing the application shutdown path to stop capture first. This does not close the current authenticity gap: the unsigned beta uses HTTPS and Velopack checksums but does not pin a project signing key or expected Authenticode publisher in the client. Before publishing a trusted stable updater, complete a valid signing integration and an explicit client-side trust-enforcement/rotation design. Do not claim that checksum validation alone authenticates the ClipForge publisher.

If a release has a serious defect, do not replace its files in place and do not publish an older version under the same channel. Fix the issue, increment the version, and publish a new release. A GitHub release can be changed from public to draft to stop new manual downloads, but already installed clients may have cached metadata, so a forward-fix release is the dependable recovery path.

If the repository is transferred or renamed, preserve GitHub redirects or rebuild from a deliberate update-source migration plan. The GitHub repository URL is embedded into each shipped application.
