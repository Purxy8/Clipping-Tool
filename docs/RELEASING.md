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
  -Version 1.1.0 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY
```

The update URL is compiled into the application. Use the final, permanent repository URL for a distributable build. A build without `-UpdateUrl` can be used for local testing, but it cannot discover later releases.

By default, local release assets are written to `artifacts\Releases`; the GitHub workflow explicitly uses the isolated `artifacts\Distribution` directory. They include the user-facing installer, portable ZIP, Velopack package and update-feed files, and `SHA256SUMS.txt`. The self-contained application includes the .NET runtime but does not bundle FFmpeg.

The script runs the Release build and tests before packaging. It refuses to overwrite an existing full package with the same version.

## Authentic code signing

An unsigned installer is suitable for internal testing, but it is not an official trusted Windows release. Windows SmartScreen and antivirus products commonly warn about unsigned applications. Velopack must perform signing while it creates the package so that the application, updater, and installer are signed at the correct stages. See [Velopack's Windows signing guide](https://docs.velopack.io/packaging/signing).

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
  -Version 1.1.0 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY `
  -SignParams $signing
```

Use a certificate issued to the publisher identity that should appear in Windows. Modern commercial certificates may be held in a hardware or cloud HSM instead of an exportable PFX, so confirm that the provider supports unattended CI signing before purchase.

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
  -Version 1.1.0 `
  -UpdateUrl https://github.com/OWNER/REPOSITORY `
  -AzureTrustedSignFile C:\secure\clipforge-signing.json
```

Artifact Signing also requires a valid Microsoft identity validation, certificate profile, Azure credentials, and the appropriate signing role. The metadata file is not itself a credential, but the account details and Azure access should still be managed as deployment configuration.

## GitHub Actions release

`.github/workflows/release.yml` is a manual `workflow_dispatch` pipeline. It:

1. Restores .NET dependencies and the repository-local Velopack 1.2.0 tool.
2. Downloads the previous Velopack GitHub release when a feed already exists, allowing Velopack to produce delta updates.
3. Builds, tests, optionally signs, and packages ClipForge with `scripts/release.ps1`.
4. Verifies that a requested signature is valid.
5. Uploads the update assets through `vpk upload github` using GitHub's short-lived `GITHUB_TOKEN`, then attaches the friendly `ClipForge-Setup.exe` alias, portable ZIP, and checksum file to the same release.
6. Retains the primary release files as a workflow artifact for 14 days.

To run it, open **Actions > Release ClipForge > Run workflow**, enter a new version and optional release notes, and decide whether to publish immediately. The `publish` option defaults to off, which creates a draft GitHub release. Review the assets and signature before publishing the draft. The workflow refuses immediate publishing when no signing configuration is present.

No repository signing secret is required for an unsigned test run. Configure exactly one of the following sets for a trusted release.

### PFX secrets

Use this mode only when the issuer provides an exportable, CI-suitable PFX. Many modern public code-signing products keep the private key in a hardware token or cloud HSM and require their own signing integration instead.

| GitHub Actions secret | Content |
| --- | --- |
| `WINDOWS_SIGNING_PFX_BASE64` | Base64 encoding of the PFX file |
| `WINDOWS_SIGNING_PFX_PASSWORD` | PFX password |

The workflow imports the certificate into the ephemeral runner's current-user certificate store, passes only its thumbprint to Velopack, and removes the certificate and temporary PFX afterward.

### Azure Artifact Signing secrets

| GitHub Actions secret | Content |
| --- | --- |
| `AZURE_SIGNING_METADATA_JSON` | The three-field signing metadata JSON shown above |
| `AZURE_CLIENT_ID` | Entra application or managed identity client ID |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |

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

For a signed release, `Get-AuthenticodeSignature` must report `Valid`, and the publisher should match the intended ClipForge publisher identity. Also verify on a clean Windows user account or VM:

1. The installer starts without an unexpected publisher warning.
2. ClipForge launches from the Start menu and Desktop shortcut.
3. FFmpeg setup, replay capture, and clip saving work.
4. The displayed application version matches the release.
5. After publishing a newer version, **Check for updates**, download, and restart complete successfully.

Raw `dotnet run`, IDE, and portable publish builds are intentionally excluded from updater installation tests. Velopack updates only apply to a Velopack-installed copy.

## Publishing and recovery

The public download lives on the repository's GitHub Releases page. Because the workflow attaches the friendly installer alias, the latest stable download can use:

```text
https://github.com/OWNER/REPOSITORY/releases/latest/download/ClipForge-Setup.exe
```

Keep the full package, update-feed files, and installer attached to every release; installed clients need the feed and packages to update.

If a release has a serious defect, do not replace its files in place and do not publish an older version under the same channel. Fix the issue, increment the version, and publish a new release. A GitHub release can be changed from public to draft to stop new manual downloads, but already installed clients may have cached metadata, so a forward-fix release is the dependable recovery path.

If the repository is transferred or renamed, preserve GitHub redirects or rebuild from a deliberate update-source migration plan. The GitHub repository URL is embedded into each shipped application.
